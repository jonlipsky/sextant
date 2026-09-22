# Phase 15 — Route platform-specific indexing by worker capability

## Goal

Faithfully index projects requiring OS-specific SDKs, workloads, targeting packs, or external toolchains.

## Scope

- Discover required capabilities from repository configuration and workspace diagnostics.
- Define Sextant worker capability advertisements and matching rules.
- Provide versioned Linux default/workload images and Windows/macOS worker support.
- Route Apple projects to macOS/Xcode and Windows-only projects/tasks to Windows when Linux evaluation is insufficient.
- Record capability provenance and explicit completeness.
- Fail closed when no compatible environment exists.

## Technical design / files

- Implement the fingerprint and routing contract in [Platform-aware and client-assisted indexing](platform-indexing.md).
- Discovery begins with a restricted evaluation/probe and may refine requirements after structured failure.
- Worker matching uses exact required fields and policy labels, not host-name conventions.
- Workload images are immutable and keyed to SDK feature band/workload set.
- Native Windows/macOS workers use ProcessStack's explicit trusted placement controls and restricted paths.

## Acceptance criteria

1. Ordinary fixtures select Linux by default.
2. Windows-sensitive fixtures route to Windows when Linux cannot evaluate them.
3. Apple fixtures route to a macOS worker with a compatible Xcode/workload fingerprint.
4. Missing workloads yield actionable structured diagnostics and cannot publish a complete snapshot.
5. Capability fingerprints appear in snapshot provenance and cache compatibility.
6. The platform fixture matrix in `platform-indexing.md` is automated where runners exist and environment-gated otherwise.

## Notes / risks / dependencies

- A non-native host may sometimes compile a target using restored reference packs; routing should use demonstrated evaluation success plus policy rather than TFM name alone.
- macOS worker capacity and licensing/operational constraints require explicit rollout planning.
