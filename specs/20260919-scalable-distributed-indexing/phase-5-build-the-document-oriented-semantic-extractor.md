# Phase 5 — Build the document-oriented semantic extractor

## Goal

Replace whole-solution reference searches per declaration with one deterministic semantic and `IOperation` analysis pass per document.

## Scope

- Build the stable declaration catalog before occurrence extraction.
- Walk each document's syntax, semantic model, and operation tree once.
- Emit references, calls, reads/writes, object creation, inheritance, implementation, overrides, attributes, and containing-source context.
- Preserve comments and optional argument/return flow through shared document context.
- Deduplicate definitions, edges, and locations before persistence.
- Run the old and new extractors side by side on fixtures for differential validation.

## Technical design / files

- Add a `DocumentSemanticExtractor` and typed contribution records in `Sextant.Indexer`.
- Resolve occurrence targets from Roslyn symbols through the shared semantic-key factory.
- Attribute each occurrence to the nearest supported enclosing source symbol.
- Use operation kinds for invocation, conversion, operator, property, event, field, parameter, local, and type access where available.
- Use narrowly scoped syntax handling only where `IOperation` does not provide required evidence.
- Remove `SymbolFinder.FindReferencesAsync` from full-corpus indexing after parity gates pass; retain it only for explicitly on-demand interactive behavior if needed.

## Acceptance criteria

1. Full indexing performs no whole-solution `FindReferencesAsync` call per declaration.
2. Each source document obtains one cached semantic model/root per extraction run.
3. Existing reference kind, access kind, call hierarchy, implementor, type hierarchy, and impact fixtures pass through the new contribution model.
4. Differential tests explain every intentional difference from the old extractor.
5. Duplicate occurrence keys are rejected or coalesced before database insertion.
6. The benchmark demonstrates near-linear growth relative to document/source size over generated scale tiers.

## Notes / risks / dependencies

- `IOperation` may omit malformed-code regions; syntax fallback and completeness diagnostics are required.
- Dynamic dispatch remains a declared static target and is expanded at query time where supported.
- This is the largest local architectural change and should be feature-flagged until parity is established.
