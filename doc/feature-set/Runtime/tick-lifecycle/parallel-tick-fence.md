---
uid: feature-runtime-tick-lifecycle-parallel-tick-fence
title: 'Parallel Tick Fence (WriteTickFence)'
description: 'Spreads the post-tick WriteTickFence step — cluster migrations, AABB refresh, WAL publish — across the worker pool instead of running it serially on one thread.'
---

# Parallel Tick Fence (WriteTickFence)
> Spreads the post-tick WriteTickFence step — cluster migrations, AABB refresh, WAL publish — across the worker pool instead of running it serially on one thread.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

`WriteTickFence` applies [cluster migrations](../../Spatial/spatial-coherent-clustering.md) (moving an entity to a
cluster for its new grid cell when it crosses a cell boundary), recomputes spatial AABBs, and publishes WAL records
for the tick's dirty cluster content. Run serially on the single TickDriver thread, it can dominate tick time on a
large simulation with heavy migration/AABB churn — one thread working while the rest of the worker pool sits
idle, capping how far a tick can scale with core count. The Parallel Tick Fence spreads that work across all
workers so it scales the same way the rest of the tick DAG does.

## ⚙️ How it works (in brief)

The fence is split into four chained phases — Prep, Migrate, AabbRefresh, Finalize — each dispatched as a
chunk-parallel system after the user's tick DAG completes. A per-tick work planner sizes chunks from measured
per-unit cost, continuously recalibrated from a sliding window of recent ticks, so work is bin-packed evenly
across workers rather than split by a fixed count. **The parallel dispatch** is entirely internal — it is
configured via `RuntimeOptions`, not invoked.

> **`WriteTickFence` itself is public, and calling it is not optional outside the runtime.** Under
> `TyphonRuntime` the fence is invoked automatically at tick end and application code never touches it. A host
> embedding the engine **without** the runtime must call `dbe.WriteTickFence(n)` itself, once per tick — see
> [Embedding without the runtime](../../../guide/embedding-without-the-runtime.md). What is internal is *how* the fence spreads its
> work across workers, not *whether* the fence runs.

## 💻 Usage

```csharp
var options = new RuntimeOptions
{
    EnableParallelFence = true,       // default — parallelize WriteTickFence across workers
    FenceChunkOversubscription = 2,   // chunk-count cap = oversubscription × WorkerCount
    AdaptiveFenceCost = true,         // recalibrate migration/AABB cost from measured wall-time
};

using var runtime = TyphonRuntime.Create(dbe, schedule => { /* ... */ }, options);
```

| Option | Default | Effect |
|---|---|---|
| `EnableParallelFence` | `true` | Parallel fence sub-DAG vs. the single-threaded `WriteTickFence` |
| `FenceChunkOversubscription` | 2 | Chunk-count cap = oversubscription × `WorkerCount`; smooths per-worker preemption jitter |
| `FenceCostModel` | AntHill-calibrated defaults | Seeds per-unit cost (migration ≈ 33µs/entity, AABB ≈ 2.4µs/cluster) |
| `AdaptiveFenceCost` | `true` | Continuously recalibrates `FenceCostModel` from a 64-tick sliding window; disable to pin the static seed (repeatable benchmarks) |

## ⚠️ Guarantees & limits

- Application code never interacts with the fence DAG directly — there is no API surface beyond the
  `RuntimeOptions` knobs above.
- `EnableParallelFence = false` falls back to the serial `WriteTickFence` — useful for diagnostics or
  as a regression safety valve; behavior is otherwise equivalent.
- Requires the engine's WAL durability mode (mandatory engine-wide) — both the parallel and serial fence paths
  rely on it to drain dirty pages.
- Chunk sizing targets ~200µs of CPU per chunk; below that floor, per-dispatch overhead (worker wake, page
  cache attach) dominates the chunk's own cost.
- Per-chunk dirty-page tracking uses a local pooled `ChangeSet`, capped at chunk end — does not change the
  engine's WAL/checkpoint contract for the rest of the tick.
- Runs after the user's tick DAG completes and before `UoW.Flush()` — its WAL publishes become durable in the
  same fsync as the tick's other commits, not a tick later.

## 🔗 Related

- Parent feature: [Tick Lifecycle & Transaction Management](./README.md)
- Sibling: [Spatially-Coherent Entity Clustering](../../Spatial/spatial-coherent-clustering.md) — defines what a cluster migration is and why it's deferred to the tick fence instead of applied inline
- Also catalogued from the execution-engine angle: [Tick-Based Execution Engine → Parallel Tick Fence](../tick-execution-engine/parallel-tick-fence.md)

<!-- Deep dive: claude/overview/13-runtime.md §Parallel Tick Fence -->
