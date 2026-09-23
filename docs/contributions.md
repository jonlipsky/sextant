# Client- and CI-assisted index contributions (Phase 16)

Sextant normally indexes a repository from one machine. But the machine that can *build* a project — with
the exact SDKs, workloads, and OS-specific reference packs installed — is often the best place to *index*
it. Phase 16 lets any environment that already possesses the required toolchain (a developer's machine or,
especially, **CI after a successful restore/build**) produce a **validated, deterministic semantic
contribution** for committed source and upload it to the [standalone index service](service.md). The
service authenticates, authorizes, hash-verifies against Git content, capability-verifies, and only then
publishes it as an immutable Phase-9 snapshot (see [service.md](service.md) and
[schema.md](schema.md#later-additive-migrations-012021)).

This is how the platform-specific project versions from [Phase 15](service.md) get produced by the
environments that can actually build them (a Windows runner builds the `net8.0-windows` project version, a
macOS runner builds the `net8.0-macos` one) and then **assembled into ONE repository snapshot**.

> **A contribution is untrusted external input.** It is authenticated, authorized (repo/commit/project),
> hash-verified against provider Git content, and capability-verified **before** a single row is imported or
> published. A contribution that fails any check is rejected with a structured reason and is **never**
> published. See [The supply-chain gate](#the-supply-chain-gate).

> **The service stays optional.** With no service configured, nothing changes — a normal build behaves
> byte-identically to today. Uploading is opt-in, an unreachable service is non-blocking unless CI explicitly
> opts in, and a dirty working tree is never uploaded. See [Service-optional & dirty-tree safety](#service-optional--dirty-tree-safety).

## `sextant contribute`

```bash
# Capture committed source and upload to a service, finalizing the snapshot (the common case).
sextant contribute path/to/Solution.slnx --service https://sextant.internal --token "$SEXTANT_CONTRIB_TOKEN"

# Capture only, writing the artifact to a file (no service contacted).
sextant contribute path/to/Solution.slnx --out contribution.sxc

# CI: fail the job if the service is unreachable (the ONLY thing that makes upload blocking).
sextant contribute path/to/Solution.slnx --service "$SEXTANT_SERVICE" --token "$TOK" --require
```

| Option | Purpose |
| --- | --- |
| `<solution-path>` | The `.sln`/`.slnx` to index (positional, required). |
| `--service <url>` | Index service base URL to upload to. Omit to capture only. |
| `--out, -o <file>` | Write the artifact to a file instead of uploading. |
| `--token <token>` | Contributor bearer token (or `SEXTANT_CONTRIB_TOKEN`). |
| `--tenant <id>` | Tenant/owner the contribution is published under (default: repository URL). |
| `--branch <name>` | Branch to advance to the published snapshot (default: current branch). |
| `--default-branch` | Mark the branch as the repository default. |
| `--allow-dirty` | Capture a dirty working tree. **Local-only; never uploaded** (see below). |
| `--require` | Fail if the service is unavailable (CI opt-in; otherwise non-blocking). |
| `--no-finalize` | Upload without finalizing, for multi-environment assembly (see below). |

The command resolves the Git facts (root, `HEAD` commit, tree SHA, branch, origin remote) itself, indexes
the committed source with the **local** SDK/workloads, packs a content-addressed artifact, and then either
writes it or uploads it. Producer identity (machine name), CLI version, toolchain fingerprint, and — in CI —
the `GITHUB_RUN_ID` execution provenance are recorded on the contribution.

## The artifact

A contribution artifact is a single content-addressed container:

```
[ magic + length ][ manifest (canonical JSON) ][ compact semantic payload (SQLite) ]
```

- The **manifest** ([`ContributionManifest`](../src/Sextant.Core/Platform/ContributionManifest.cs)) is the
  per-logical-project/TFM contract (see [platform routing](service.md#platform-specific-routing-by-worker-capability-phase-15)):
  repository
  remote URL, commit/tree SHA, schema + analyzer + config + toolchain fingerprints, the producing
  **capability fingerprint**, the payload snapshot's [identity hash](service.md), and one entry per
  contributed project version carrying its canonical id, repo-relative path, TFM, per-project capability, and
  `path@git-blob-hash` **source/import/reference fingerprints**.
- The **payload** is a compact ([migration 011](schema.md)) SQLite database holding exactly one *complete*
  snapshot for the committed state.
- The **content address** is `SHA-256(canonical manifest ++ payload)`. It is the **idempotency key**:
  re-uploading the same bytes is a no-op.

The manifest is derived purely from the payload (its capability is read *from* what the payload recorded it
built under, so **declared == built** by construction), and the only time-varying field is the creation
timestamp — the derivation is otherwise deterministic for identical committed inputs.

## Uploading & idempotency

Upload is an HTTP `POST` of the raw artifact bytes to the service control endpoint:

```
POST /control/contribute?finalize=true&branch=<name>&default_branch=<bool>
Authorization: Bearer <contributor token>
<artifact bytes>
```

The service responds with an [`IngestContributionResult`](../src/Sextant.Service/ContributionContracts.cs):

| `status` | HTTP | Meaning |
| --- | --- | --- |
| `complete` | 200 | Accepted and published (or attached to an already-published assembly snapshot). |
| `assembling` | 200 | Accepted and imported; the assembly snapshot is still pending more contributions (`--no-finalize`). |
| `duplicate` | 200 | Content-addressed no-op — this exact artifact was already accepted. |
| `rejected` | 422 | Rejected by the supply-chain gate; **never published**, with structured `diagnostics`. |

Idempotency is content-addressed and durable: `snapshot_contributions.content_hash` is `UNIQUE`
([migration 018](#durable-state-migration-018)), so a re-upload short-circuits before any re-import and
attaches to the existing snapshot. This reuses the Phase-13 `identity_hash` idempotency and atomic publish —
a contribution attaches to exactly ONE snapshot generation.

## Multi-environment assembly (Windows + macOS → one snapshot)

Platform-specific project versions are built where they can be built, then assembled into a single
repository snapshot. All contributions for the same committed state target the **same capability-less
assembly snapshot identity** (the manifest's `ToSnapshotIdentity()` deliberately omits the capability), so:

```bash
# On the Windows runner — import the Windows project version but don't finalize yet.
sextant contribute Solution.slnx --service "$SVC" --token "$TOK" --no-finalize

# On the macOS runner — import the macOS project version and finalize the assembled snapshot.
sextant contribute Solution.slnx --service "$SVC" --token "$TOK"
```

Each contribution carries its own capability fingerprint and assembles only with compatible inputs. While
assembly is in progress the snapshot stays **pending** — it is never a mutated complete snapshot
(immutability). If a contribution arrives for a snapshot that is already complete, its provenance is recorded
without mutating the published snapshot.

## The supply-chain gate

Every contribution passes eight ordered checks in
[`ContributionValidator`](../src/Sextant.Service/Contributions/ContributionValidator.cs) **before** import
or publication. Each failure produces a structured
[`ContributionRejectionCode`](../src/Sextant.Service/Contributions/ContributionValidationResult.cs) recorded
in `snapshot_job_diagnostics`, and the contribution is never published:

1. **Dirty tree** (`dirty_tree`) — a contribution produced from a dirty working tree is rejected.
2. **Repository / commit identity** (`repository_mismatch` / `commit_mismatch`) — must be present.
3. **Authorization** (`unauthorized`) — the tenant must be authorized for the repo/commit/projects; a
   required-auth policy also demands a token.
4. **Schema / analyzer compatibility** (`schema_mismatch` / `analyzer_mismatch`) — must match the service's,
   so an incompatible payload is never assembled.
5. **Payload completeness** (`payload_missing`) — the payload must actually contain a *complete* snapshot at
   the declared identity.
6. **Capability declared == built** (`capability_mismatch`) — the payload must record the capability it was
   produced under and it must equal the declared capability (per snapshot and per project).
7. **Git content verification** (`content_mismatch`) — every declared `source`/`import`/`reference` blob is
   verified against provider Git content for the exact commit. A **mismatch** is always fatal; an
   **unavailable** answer is fatal only when the policy requires verification (fail-closed).
8. **Project-graph consistency** (`project_graph_mismatch`) — the payload's mapped project versions must
   match the count the manifest declares.

The validator never weakens an existing authz/immutability/completeness contract — it only *adds* gates.

### Policy

[`ContributionPolicy`](../src/Sextant.Service/Contributions/ContributionSeams.cs) defaults to **dev-open**
(single-node development): `RequireAuthorization = false`, `RequireGitContentVerification = false`,
`MaxArtifactBytes = 512 MiB`. A deployment wires an `IContributionAuthorizer` (repo/commit/project authz)
and an `IGitContentProvider` (blob verification against the provider's Git content) and turns the `Require*`
flags on to make the gate fail-closed.

## Service-optional & dirty-tree safety

Two safety rules hold regardless of environment, and are enforced by the **pure**
[`ContributionCliPlan.Decide`](../src/Sextant.Cli/Handlers/ContributeHandler.cs) before any capture:

- **Service-optional (criterion 5).** A build with no service configured behaves byte-identically to today.
  An unreachable or erroring service is **non-blocking** — the command reports it and exits 0 — *unless*
  `--require` is passed (the CI opt-in), which is the only thing that can make a contribution upload fail a
  build.
- **Dirty source is never uploaded (criterion 6).** A dirty working tree is rejected outright unless
  `--allow-dirty` is passed, and even then the artifact may only be written **locally** (`--out`), never sent
  to a service. The validator independently rejects any dirty artifact that reaches the service.

## Durable state (migration 018)

[Migration 018](../src/Sextant.Store/Migrations/018_client_contributions.sql) is additive/forward-only:

- **`snapshot_contributions`** — one row per accepted contribution: the `UNIQUE` content hash (idempotency),
  the producing capability fingerprint, producer/toolchain/tenant provenance, and the assembled snapshot it
  fed (`ON DELETE CASCADE`). An assembled snapshot's provenance therefore names every environment that
  contributed to it.
- **`projects.capability_fingerprint`** — the capability that produced each contributed project *version*
  (nullable; `NULL` for ordinary locally-indexed project versions). An assembled snapshot spans project
  versions built by different capabilities, so per-project capability is recorded, not just per-snapshot.

`LatestSchemaVersion` advances to 18; the Phase-9 identity gate folds it into every snapshot's
`identity_hash`, so an existing schema-17 base is rebuilt into a schema-18 base on first run — the safe,
expected upgrade path.

## Not the default transport

A compiler analyzer or source generator is deliberately **not** the default transport for contributions — an
explicit `sextant contribute` capture + upload is. A non-blocking incremental MSBuild hook that merely
*enqueues* a capture is a possible future addition, but it is never on the normal build's critical path.
