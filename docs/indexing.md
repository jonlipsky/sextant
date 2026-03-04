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
