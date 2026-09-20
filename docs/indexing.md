# Indexing Pipeline

The index builder loads .NET solutions via Roslyn's `MSBuildWorkspace`, extracts semantic information, and writes it to the SQLite store. It is a library consumed by both the CLI (full index) and the daemon (incremental index).

## Workspace Loading

```
MSBuildLocator.RegisterDefaults();  // Must be called before any Roslyn type loads
var workspace = MSBuildWorkspace.Create();
var solution = await workspace.OpenSolutionAsync(solutionPath);
```

`MSBuildLocator.RegisterDefaults()` must be initialized exactly once, before any `Microsoft.CodeAnalysis` types are loaded. This is a hard Roslyn constraint and is the first call in `Program.cs`.

Full solution loading (not per-project) is required so that a symbol defined in Project A and referenced in Project B resolves to the same `ISymbol` instance.

## Extraction Phases

The indexing pipeline runs through these phases in order:

### 1. Symbol Extraction

For each project in the solution, the `SymbolExtractor` walks the Roslyn compilation:

1. Gets `Compilation` via `project.GetCompilationAsync()`.
2. For each syntax tree, gets the `SemanticModel`.
3. Walks declared symbols and extracts:
   - **FQN**: `ISymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)`
   - **Display name**: `ISymbol.Name`
   - **Kind**: mapped from `ISymbol.Kind` and type-specific properties
   - **Accessibility**: from `ISymbol.DeclaredAccessibility`
   - **Signature**: for methods, `IMethodSymbol.ToDisplayString()` including return type and parameters
   - **Signature hash**: SHA256 of the signature string (for change detection)
   - **Doc comment**: `ISymbol.GetDocumentationCommentXml()`, XML tags stripped
   - **Location**: file path and line span from `ISymbol.Locations[0]`
   - **Attributes**: `ISymbol.GetAttributes()` as a JSON array of FQNs

**Symbol kinds extracted:** `class`, `interface`, `struct`, `enum`, `delegate`, `record`, `method`, `constructor`, `property`, `field`, `event`, `indexer`, `type_parameter`.

**Exclusions:**
- Symbols where `IsImplicitlyDeclared` is true
- Symbols in generated files (`*.g.cs`, `*.designer.cs`, `obj/` directories)

### 2. Relationship Extraction

The `RelationshipExtractor` captures type-level semantic relationships:

- **inherits**: `INamedTypeSymbol.BaseType` (if not `object`)
- **implements**: `INamedTypeSymbol.Interfaces` (direct only, not inherited)
- **overrides**: for each member, checks `IMethodSymbol.OverriddenMethod`, `IPropertySymbol.OverriddenProperty`, etc.

### 3. Reference Extraction

The `ReferenceExtractor` uses `SymbolFinder.FindReferencesAsync(symbol, solution)` to find all usages across the solution.

For each reference location:
- Records file path and line number
- Extracts a ~120 character `context_snippet` from the syntax tree
- Classifies `reference_kind` by inspecting the enclosing syntax node:
  - `InvocationExpressionSyntax` -> `invocation`
  - `ObjectCreationExpressionSyntax` -> `object_creation`
  - `BaseListSyntax` with interface -> `inheritance`
  - `AttributeSyntax` -> `attribute`
  - `OverrideKeyword` present -> `override`
  - Default -> `type_ref`

### 4. Call Graph Construction

The `CallGraphBuilder` resolves direct method invocations:

1. Gets the method's syntax node via `IMethodSymbol.DeclaringSyntaxReferences`.
2. Walks descendant nodes for `InvocationExpressionSyntax`.
3. Resolves each invocation target via `semanticModel.GetSymbolInfo(invocation).Symbol`.
4. Records `(caller, callee, file, line)` edges.

Virtual and interface dispatch records the **declared** (static) callee. The `get_implementors` MCP tool expands virtual edges at query time.

### 5. Dependency Recording

When loading a solution and resolving `ProjectReference` items:
- Same git repo -> `reference_kind = "project_ref"`
- Submodule directory -> `reference_kind = "submodule_ref"` with `submodule_pinned_commit`
- NuGet packages -> `reference_kind = "nuget_ref"`

### 6. API Surface Capture

For projects with inbound dependencies, captures public/protected symbol signature hashes into `api_surface_snapshots` with the current git HEAD commit.

## Document-oriented extractor (Phase 5, feature-flagged)

The relationship / reference / call-graph phases above are the **legacy declaration-driven** path: they resolve occurrences by running `SymbolFinder.FindReferencesAsync` once per declared symbol, re-scanning the whole solution for each declaration (cost grows with `declarations × solution size`).

When the `document_extractor` flag is enabled, those three phases are replaced by a single **`extracting_occurrences`** phase driven by `DocumentSemanticExtractor`. It walks each processed project's documents **once**, over a single cached semantic model + syntax root per document, and emits references, calls, and relationships attributed to the nearest enclosing source symbol in that document (**usage-site extraction**). Cost scales with document/source size, not with `declarations × solution size` (near-linear — see [benchmarks.md](benchmarks.md)).

- **Evidence source.** `IOperation` drives the evidence where it exists — invocation targets (`IInvocationOperation.TargetMethod`), object creation (`IObjectCreationOperation`), and field/property access classification (`IMemberReferenceOperation`). Type/member *mentions* (declarations, base lists, attributes, signatures, generic arguments, `typeof`/cast) have no operation and are resolved from the bound symbol of each name-syntax node (narrow syntax handling where `IOperation` lacks evidence). Names that fail to bind in a malformed region increment a completeness diagnostic.
- **Target identity.** Every occurrence target is resolved through the shared `SemanticSymbolKeyFactory` declaration key and the Phase-2 `SymbolCatalog` (built **before** occurrence extraction), so definitions, occurrences, and edges agree on one identity. Occurrence targets are reduced to their original definition (`List<int>` → `List<T>`), reduced extension methods to their static original, and constructor bindings (e.g. `[Attr]`) to the constructed type.
- **Ownership flip and Phase-4 reconciliation.** A reference is now produced by the **using** document, not by the target declaration. References still persist `in_project_id` = the using project and `symbol_id` = the **target** declaration, and every cross-project call/inheritance also emits a reference edge. So the cross-project connectivity the Phase-4 incremental closure relies on (`ReferenceStore.GetCrossProjectPairs` + the undirected `ProjectClosure`) is preserved: any occurrence edge touching the closure has both endpoints in it, so target-cascade deletion (`DeleteByProject`) and usage-site re-production cover the same domain. No change to the closure or deletion logic is required.
- **Deduplication.** One `DocumentContributionSet` per (per-TFM) project coalesces contributions across its documents before persistence: references key on `(target, file, line, kind, access)` (snippet excluded — first wins), calls on `(caller, callee, file, line, call-site column)`, relationships on `(from, to, kind)`. Partial-type declarations across files collapse to one relationship set. Reference coalescing is information-preserving: same-line rows sharing a target/kind/access are byte-identical (the schema has no column), so collapsing them loses nothing. Calls keep the call-site column in the key because two distinct same-line calls to one callee carry **different argument dataflow**, so they must remain separate edges.
- **Parity.** The new path is proven at parity against the legacy path on the shared fixture corpus (`ExtractorParityTests`): relationships identical, call edges a superset (it recovers reduced-extension/constructed calls the legacy `GetSymbolInfo`-only path could drop), cross-project (consumer → dependency) reference pairs identical, and no spurious type targets. Intentional differences: reference kinds are classified by the name's precise syntactic role rather than the nearest enclosing expression, the legacy `override` reference kind is folded into the real occurrence kind, and object-creation/attribute references target the constructed type rather than the specific constructor overload.
- **Known limitation (gates flag graduation).** Target identity is resolved through the Phase-2 `SymbolCatalog`, which is exact whenever a declaration key resolves to a single project. When one declaration key exists in **two or more** projects that the using project could bind to — a multi-targeted dependency several of whose TFMs are visible, or an `extern alias`-duplicated assembly — the catalog deterministically picks the lowest project id (and increments `AmbiguousEdgeBindings`) rather than the exact compilation-scoped project/TFM. The legacy reference path anchored on the exact bound declaration symbol, so this is the one intentional narrowing in the new reference path. It is deterministic (so full == full-then-incremental still holds) and does not manifest on the single-TFM correctness corpus (the cross-project-pair parity assertion passes). Exact compilation-scoped resolution (via the bound target's `ContainingAssembly` → `Solution.GetProject(IAssemblySymbol)`) is the Phase-2-deferred item and must land before the flag is graduated to default-on.

The flag defaults **off**; the legacy path remains the default until parity is established across all corpora. See [configuration.md](configuration.md) for the flag and its environment override.

## Progress, Cancellation, and Metrics

The pipeline is instrumented so callers can observe, cancel, and measure a run without changing extraction behaviour.

- **Structured progress.** `IndexOrchestrator.IndexSolutionAsync` accepts an `IProgress<IndexingProgress>`. Each phase reports its name (`registering_projects`, `extracting_symbols`, `extracting_relationships`, `extracting_references`, `extracting_comments`, `extracting_call_graph`, `recording_dependencies`, `capturing_api_surface`), the current project, and project counts.
- **Cancellation.** Both the orchestrator and `IncrementalIndexer` take a `CancellationToken` and call `ThrowIfCancellationRequested()` at every phase boundary and per project/file. `SolutionLoader.LoadSolutionAsync` also honours the token. Cancellation surfaces as `OperationCanceledException`; the existing parameterless/overload signatures are preserved for backward compatibility.
- **Metrics collection.** Passing an optional `IndexingMetrics` collector records per-phase durations, project counts, row counts, and the terminal status. The collector is mutated **in place** and each `PhaseMetric` is appended when its phase starts, so partial results survive a cancellation or failure. Run status begins as `Running` and only becomes `Completed` on success; a caught cancellation/failure leaves it non-`Completed` (the harness marks it `Cancelled`/`Failed`).

Row and duplication counts come from `IndexMetricsStore`, which counts total reference rows versus semantically-distinct occurrences keyed by `(symbol_id, file_path, line, reference_kind)`. Storage sizing distinguishes the **final** database size (measured after a WAL checkpoint) from the **peak** database-plus-WAL size sampled during the run — see [benchmarks.md](benchmarks.md).

## Incremental Indexing

When invoked with a set of changed files (by the daemon or CLI):

1. Computes SHA256 content hash of each changed file.
2. Compares against the `file_index` table. Skips files whose hash hasn't changed.
3. For changed files:
   - Deletes all symbols, references, and call graph edges for the file.
   - Re-extracts everything for the changed file.
   - Updates `file_index` with the new content hash and timestamp.
4. If any symbol signatures changed (detected by `signature_hash` comparison), queues dependent files for re-indexing.

## Test Project Detection

A project is flagged as `is_test_project = true` if its project or package references include any of:
- `Microsoft.NET.Test.Sdk`
- `xunit` / `xunit.core`
- `NUnit` / `nunit`
- `MSTest.TestFramework`

## Project Identity

Projects are identified by `(git_remote_url, repo_relative_path)` rather than disk paths. The canonical ID is the first 16 hex characters of `SHA256(normalized_url + "|" + relative_path)`.

**Git remote normalization:**
- SSH URLs are converted to HTTPS
- `.git` suffix is stripped
- Credentials are removed
- Hostname is lowercased

This ensures the same project has the same identity regardless of which machine indexes it or which clone protocol was used.

## Cross-Repository Support

When indexing a repository with git submodules:

1. Runs `git submodule status --recursive` to discover submodules.
2. For each submodule, extracts the path, pinned commit SHA, and remote URL.
3. Indexes submodule projects using the submodule's own git remote URL as identity.
4. A shared project appearing as a submodule in multiple repos has a single canonical entry (deduplicated by `canonical_id`).

### Breaking Change Detection

When comparing API surface snapshots between two commits:

| Condition | Classification |
|---|---|
| Symbol in old snapshot but not in new | **Breaking** — symbol removed |
| Symbol exists in both but signature hash differs | **Breaking** — signature changed |
| Accessibility is more restrictive in new | **Breaking** — reduced visibility |
| Symbol in new but not in old | **Additive** — safe |
| Signature hash identical, accessibility same or broader | **Non-breaking** |
