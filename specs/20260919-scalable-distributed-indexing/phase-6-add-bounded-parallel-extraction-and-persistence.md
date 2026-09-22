# Phase 6 — Add bounded parallel extraction and persistence

## Goal

Use available CPU without unbounded Roslyn memory growth or concurrent SQLite write contention.

## Scope

- Add configurable analysis parallelism capped by machine and workload policy.
- Partition work by project/document batches using estimated source size.
- Preserve deterministic output ordering independent of task completion order.
- Feed one batched SQLite writer through a bounded channel.
- Apply backpressure when persistence is slower than extraction.
- Observe queue depth, batch duration, memory, cancellation, and worker utilization.

## Technical design / files

- Add extraction scheduling and batch models in `Sextant.Indexer`.
- Default maximum parallelism to the smaller of processor count and an experimentally selected cap.
- Keep project compilations and semantic models scoped so completed batches can be released.
- Sequence contributions by stable project/document ordinal before final validation.
- The writer owns the connection, transaction, prepared commands, and batch commit.
- Cancellation closes producers, drains or rolls back the active batch, and never deadlocks the writer.

## Acceptance criteria

1. Parallel and single-threaded runs produce byte-equivalent canonical semantic output.
2. Configured queue capacity bounds outstanding contribution memory.
3. No SQLite connection or command is used concurrently by analysis workers.
4. Cancellation and one-worker failure terminate all pipeline stages cleanly.
5. The benchmark records throughput gains and peak-memory impact for parallelism levels 1, 2, 4, 8, and default.
6. Default parallelism improves runtime without exceeding the agreed memory budget.

## Notes / risks / dependencies

- Roslyn compilations share immutable state but can retain substantial memory; document concurrency is capped and measured.
- More workers are not always faster when MSBuild or package restore is the bottleneck.
