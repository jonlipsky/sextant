# Phase 6 — compact storage

> **Proposal.** This describes a change, not current behaviour.

**Status:** Approved, 2026-11-22.

## Goal
Phases 3 and 4 stop snapshots from copying each other. This phase makes what is stored small:
- each project version's rows and indexes;
- the working storage beside the catalog: package folders, checkouts and git objects.

## Why
Measured on cloudserver1 on 2026-11-22:
- **Catalog:** 3.7 GB, rebuilt after the 2026-11-21 reset, holding 14 snapshots.
- **Working storage:** about 23 GB, six times the catalog.

| Object | Size | Rows | Notes |
|---|---|---|---|
| `symbols` | 1,007 MB | 2.2 M | about 480 bytes a row, mostly repeated text: `symbol_key` averages 102 bytes, plus the fully qualified name, display name, signature (81 B), declaration (54 B) and attributes |
| symbol indexes | about 520 MB | | `ix_symbols_key` alone is 272 MB, because it repeats `symbol_key` |
| `occurrences` | 610 MB | 21.3 M | |
| occurrence indexes | 933 MB | | four of them, together larger than the table: `ix_occ_target` 298, `ix_occ_file_version` 291, `ix_occ_project` 277, `ix_occ_source` 67 |
| `api_surface_snapshots` + indexes | about 440 MB | 884 K | 170 K distinct (key, hash) pairs; repeats the key and the fully qualified name; `signature_hash` is 47-character hex text |
| FTS | about 120 MB | | |
| doc comments | | 450 K | 88 K distinct |
| `cache/nuget-packages` | **17 GB** | | over its own 10 GiB bound (`SEXTANT_SERVICE_PACKAGE_CACHE_MAX_MB` default), in 6 per-repository folders |
| `checkouts` | 4.3 GB | | monorepo 1.7 GB, TextEditor 1.5 GB |
| `cache/git-objects` | 849 MB | | |

Phase 4 removes the copying of unchanged projects between snapshots, which today repeats every symbol about 5 times. Phase 3 drops `ix_occ_project` and decides on `ix_occ_file_version`. The rest is what this phase addresses.

## Scope

### A. Compact rows in project-version files (on the Phase 3 layout)
1. **Store each repeated string once.**
   - Each project-version file gets a string table. Symbol keys, fully qualified names, display names, signatures, declarations, attributes and doc comments become integer references into it.
   - `ix_symbols_key` then indexes an integer, not text.
2. **Store hashes as bytes.** `signature_hash` (and any other hex hash) is stored as a BLOB: 16 or 32 bytes instead of 47.
3. **Drop per-row bookkeeping that the project version already carries.** For example, `last_indexed_at` on symbols and occurrences.
4. **Lay out occurrences for the reference queries.**
   - Make the table `WITHOUT ROWID`, clustered on the main reference-query key (target symbol, then file version, line, column, kind).
   - Keep a secondary index only when an MCP tool's query plan needs it, and write down which tool. Drop the rest.
5. **Slim the API surface.**
   - Per snapshot, store only `(symbol reference, signature hash)`, with no copies of the key or fully qualified name.
   - Changes between commits are computed at query time from two snapshots. Nothing is stored per change.
6. **Point full-text search at the string table.** FTS indexes the string table as external content, so the text isn't stored twice.

### B. Working storage beside the catalog
1. **Enforce the package bound.**
   - Find out why `nuget-packages` is 17 GB while the bound is 10 GiB, and fix it. The suspects are usage recorded only after a job, folders never measured, and eviction skipped when the folder in use is the largest.
   - Test the bound with real restores of fixture repositories.
2. **Share identical package files without sharing trust.** See the decision below.
   - Package folders are per repository by design: NuGet trusts a package by id and version, and a shared folder could be poisoned (`docs/service.md`, issue #272).
   - What can be shared is identical bytes, verified by content hash.
3. **One object store per repository.**
   - Each repository gets a single blobless partial clone (`--filter=blob:none`) as its object store.
   - Each branch being indexed gets a lightweight worktree from it, so blobs are fetched only for the checked-out tree.
   - Worktrees of branches outside the active set expire with Phase 2.
4. **Report storage by class.**
   - `/control/status` and the storage budget (decision 6) report bytes separately for: catalog, project-version files, package folders, checkouts and git objects.
   - Phase 5's alerts use these numbers.

## Technical design / files
- **Phase 3 project-version file schema:** the string table, symbols, occurrences, API surface and FTS, under `src/Sextant.Store`.
- **Readers in `src/Sextant.Store` and the query layer:** join through the string table, with a per-file string cache held for the life of a query.
- **Restore and checkout code:** `src/Sextant.Service/Restore/PackageCache.cs`, plus the checkout and git-object code in `src/Sextant.Service`.
- **Migration:** a fresh-catalog rebuild is acceptable, since Phase 0 proves recovery. Project-version files carry a schema version, and an old version is rebuilt.

## Testing seams
- **Storage harness** (indexing-performance phase 1). Real boundary: real indexing of TextEditor-sized fixtures. Proves bytes per symbol and per occurrence, including indexes and FTS (criteria 1 and 2).
- **Differential equivalence.** Real boundary: every MCP tool's answers from a compacted project version, compared with the current layout for the same commit (criterion 3).
- **MCP latency.** p95 of each tool on the same fixtures (criterion 4).
- **Package cache.** Real boundary: real `dotnet restore` of fixture repositories against local feeds. Covers the bound under concurrent jobs and the oversized-repository case, and checks that no repository ever resolves another feed's package. This includes the existing poisoning test (criteria 5 and 6).
- **Checkouts.** Real boundary: a real git fixture remote. Indexing two branches fetches each blob once, and an expired branch's worktree is removed (criterion 7).
- **Deliberately not tested:** filesystem-specific copy-on-write behaviour beyond the decision's chosen mechanism.

## Acceptance criteria
1. Bytes per stored symbol, including its indexes, FTS and strings, are **≤ 50%** of the 2026-11-22 measurement on the same fixture.
2. Bytes per stored occurrence, including indexes, are **≤ 50%** of the 2026-11-22 measurement.
3. Every MCP tool answers identically before and after.
4. No MCP tool's p95 regresses by more than 10%.
5. Package folders never exceed `SEXTANT_SERVICE_PACKAGE_CACHE_MAX_MB` after a job completes.
6. Identical package files are stored once across repositories, without any repository resolving a package from another's feed.
7. Checkouts plus git objects for today's watched set take **≤ 50%** of today's 5.1 GB.
8. `/control/status` reports bytes per storage class.

## Demo
- Run the storage harness on the TextEditor fixture before and after. It prints bytes per symbol and per occurrence, and the differential-equivalence result.
- Then `GET /control/status` on cloudserver1 shows bytes per storage class after a deploy, with package folders under their bound.

## Notes / risks / dependencies
- **Order:** runs after Phase 3, because it builds on the per-project-version file layout. It is measured after Phase 4, so the targets aren't inflated by duplication that Phase 4 removes. It is independent of Phase 5, which consumes Part B's per-class numbers.
- **Risk:** string-table joins add read cost. The per-query string cache and the latency criterion bound this. If a tool regresses, its hottest strings, such as display names, can stay inline.
- **Risk:** blobless clones need network access for blobs at checkout time. The indexer already fetches; offline indexing is not a requirement.

## Decision
**Sharing identical package files across repositories.**

**Approved (owner, 2026-11-22):** share at the file level by content hash, using copy-on-write reflinks where the filesystem supports them (XFS, Btrfs). Elsewhere (ext4, the cloudserver1 default), keep separate copies and rely on the fixed bound.
- If yes: no trust is shared. A repository can't change another's bytes, because a write breaks the reflink.
- If plain hard links: they save space on any filesystem. But repository code running in the service account could change a shared file and affect other repositories (the defense-in-depth concern in `docs/service.md`). That would need the shared files made immutable to the service account.
