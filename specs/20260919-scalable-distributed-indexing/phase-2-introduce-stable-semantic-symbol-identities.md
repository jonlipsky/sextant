# Phase 2 — Introduce stable semantic symbol identities

## Goal

Eliminate symbol collisions and make definitions and occurrences addressable across projects, overloads, target frameworks, and future immutable versions.

## Scope

- Introduce a semantic symbol-key abstraction distinct from display FQN.
- Use Roslyn documentation IDs for supported types and members when available.
- Include logical project and target framework in symbol identity.
- Define versioned source-declaration fallbacks for symbols without documentation IDs.
- Exclude anonymous implementation artifacts and unsupported compiler-generated declarations.
- Replace global FQN-only dictionaries with project/version-aware symbol maps.
- Update references, calls, relationships, dataflow, APIs, and MCP lookup compatibility.

## Technical design / files

- Add `SemanticSymbolKey` and a single canonical key factory in `Sextant.Core` or `Sextant.Indexer`.
- Store both `symbol_key` and formatted `fully_qualified_name`.
- Preserve existing FQN query inputs through a resolver that can return ambiguity metadata instead of silently selecting one row.
- Add project/TFM dimensions to uniqueness constraints and in-memory catalogs.
- Add a migration/new-schema path; do not reinterpret colliding rows in an existing index as valid data.

Tests must cover:

- same method name on many containing types;
- overloads and constructors;
- generic types and methods;
- explicit interface implementations;
- partial declarations;
- records and primary constructors;
- same FQN in separate projects/assemblies; and
- anonymous-object property names such as `type` and `description`.

## Acceptance criteria

1. Every supported declaration in the collision fixture has a distinct stable key.
2. Repeated extraction of identical inputs emits identical keys.
3. FQN lookups report ambiguity when multiple project/version matches exist.
4. Anonymous implementation artifacts no longer create top-level query symbols or duplicate references.
5. Call, relationship, and reference edges resolve by semantic key rather than a global FQN dictionary.
6. Existing MCP callers using unambiguous FQNs remain compatible.

## Notes / risks / dependencies

- Roslyn `SymbolKey` serialization is version-sensitive and is not the durable cross-version primary key without an explicit compatibility decision.
- Documentation IDs do not cover every source construct; fallback keys remain version-scoped.
- This phase intentionally precedes reference deduplication so uniqueness keys are defined correctly.
