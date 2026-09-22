# Phase 14 — Integrate ProcessStack repository-event orchestration

## Goal

Use ProcessStack to turn GitHub repository changes into durable, idempotent Sextant snapshot workflows.

## Scope

- Normalize GitHub push, branch create/delete, and pull-request head/base events.
- Add `EnsureSextantSnapshot` orchestration and Sextant service client/process activities.
- Key orchestration by tenant, installation, repository, commit, and index configuration.
- Route work to capability-matched workers or an existing snapshot.
- Persist status, progress, failures, artifact references, and audit events.
- Expose bounded control/status operations through ProcessStack MCP.

## Technical design / files

ProcessStack changes are expected in:

- GitHub event normalization/handler;
- platform event contracts and routing;
- application/process definitions;
- Sextant service connection/client;
- orchestration progress and retry policies; and
- MCP exposure configuration.

At-least-once events are deduplicated by provider delivery/event ID and snapshot request key. A duplicate event attaches to the existing orchestration or published snapshot.

Semantic data remains in Sextant. ProcessStack stores workflow and artifact references only.

## Acceptance criteria

1. A GitHub push event creates or attaches to the exact requested commit snapshot.
2. Duplicate/out-of-order deliveries do not publish duplicate snapshots or regress a branch pointer.
3. Pull-request synchronize events request both relevant head/base snapshots according to policy.
4. ProcessStack status exposes queued, evaluating, extracting, ingesting, complete, partial, unsupported, failed, and cancelled states.
5. Snapshot work is dispatched using declared worker capabilities.
6. ProcessStack MCP can start/check indexing without proxying every low-latency semantic query through a synchronous process.

## Notes / risks / dependencies

- ProcessStack currently subscribes to push events but requires a push event mapper.
- This work belongs in coordinated changes across Sextant and ProcessStack repositories.
