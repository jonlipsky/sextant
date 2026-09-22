# Phase 16 — Add client- and CI-assisted index contributions

## Goal

Allow environments that already possess the required SDKs/workloads to contribute validated semantic project versions for committed source.

## Scope

- Add CLI/daemon commands to produce and upload deterministic contribution artifacts.
- Add CI integration after successful restore/build.
- Optionally add a non-blocking incremental MSBuild hook that enqueues capture.
- Authenticate contributors and validate repository/commit/project authorization.
- Verify contribution inputs and Git content before publication.
- Record producer and toolchain provenance.
- Keep dirty/uncommitted contributions local by default.

## Technical design / files

- Implement the manifest contract in `platform-indexing.md`.
- Contributions are per logical project/TFM and can be assembled only with compatible snapshot inputs.
- Upload is content-addressed, resumable, size-limited, and idempotent.
- Validation compares source/import/reference hashes with provider content and requested configuration.
- Service unavailability never fails a normal build unless a repository explicitly enables a required CI policy.
- A compiler analyzer/source generator is not the default transport.

## Acceptance criteria

1. An authenticated client can publish a deterministic contribution for an exact clean commit.
2. Re-uploading the same contribution is a no-op.
3. Dirty-tree, mismatched commit, content, schema, analyzer, or project-graph contributions are rejected with reasons.
4. CI can publish Windows/macOS-specific project versions and assemble them into one repository snapshot.
5. Normal developer builds remain successful and bounded when the service is unavailable.
6. Uncommitted source is never uploaded without a separate explicit opt-in.

## Notes / risks / dependencies

- Client contributions are supply-chain inputs and require the same scrutiny as build artifacts.
- Optional attestation can be added after the initial authenticated/hash-verified protocol.
