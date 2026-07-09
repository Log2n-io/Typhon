---
uid: feature-runtime-tick-execution-engine-parallel-tick-fence
title: 'Parallel Tick Fence'
description: 'Spreads the post-tick WriteTickFence step — cluster migrations, AABB refresh, WAL publish — across the worker pool instead of running it on one thread.'
---

# Parallel Tick Fence
> Spreads the post-tick `WriteTickFence` step — cluster migrations, AABB refresh, WAL publish — across the worker pool instead of running it on one thread.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Tick-Based Execution Engine](./README.md)

## 🎯 What it solves

`WriteTickFence` applies [cluster migrations](../../Spatial/spatial-coherent-clustering.md) (moving an entity to
a cluster for its new grid cell when it crosses a cell boundary), recomputes spatial AABBs, and publishes WAL
records for the tick's dirty cluster content. Run serially on the single TickDriver thread, it can dominate tick
time on a large simulation with heavy migration/AABB churn — one thread working while the rest of the
worker pool the runtime just spun up for the tick DAG sits idle, capping how far a tick can scale
with core count. The Parallel Tick Fence spreads that work across all workers so it scales the same
way the rest of the tick does.

## ⚙️ How it works (in brief)

The fence is split into four chained phases — Prep, Migrate, AabbRefresh, Finalize — each dispatched
as a chunk-parallel system on the worker pool right after the user's tick DAG completes. A per-tick
work planner sizes chunks from measured per-unit cost (continuously recalibrated from a sliding
window of recent ticks) and bin-packs work evenly across workers rather than splitting by a fixed
count, so one slow chunk doesn't stall the tick while idle workers wait. This is entirely internal —
application code does not call into the fence; it is tuned, not invoked, through `RuntimeOptions`.

## 💻 Usage

```csharp
var options = new RuntimeOptions
{
    EnableParallelFence = true,       // default — parallelize WriteTickFence across workers
    FenceChunkOversubscription = 2,   // chunk-count cap = oversubscription x WorkerCount
    AdaptiveFenceCost = true,         // recalibrate migration/AABB cost from measured wall-time
};

using var runtime = TyphonRuntime.Create(engine, schedule => { /* ... */ }, options);
```

| Option | Default | Effect |
|---|---|---|
| `EnableParallelFence` | `true` | Parallel fence sub-DAG vs. the legacy single-threaded `WriteTickFence` |
| `FenceChunkOversubscription` | 2 | Chunk-count cap = oversubscription × `WorkerCount`; smooths per-worker preemption jitter |
| `FenceCostModel` | AntHill-calibrated defaults | Seeds per-unit cost (migration ≈ 33µs/entity, AABB ≈ 2.4µs/cluster) |
| `AdaptiveFenceCost` | `true` | Continuously recalibrates `FenceCostModel` from a 64-tick sliding window; disable to pin the static seed for repeatable benchmarks |

## ⚠️ Guarantees & limits

- Application code never interacts with the fence DAG directly — there is no API surface beyond the
  `RuntimeOptions` knobs above.
- `EnableParallelFence = false` falls back to the legacy serial `WriteTickFence` on the TickDriver
  thread — useful for diagnostics or as a regression safety valve; observable behavior is otherwise
  equivalent.
- Both the parallel and serial paths feed the engine's mandatory WAL + checkpoint pipeline to drain
  the dirty pages they touch — this is not an opt-in durability mode, it's how dirty cluster state
  always gets to disk.
- Chunk sizing targets ~200µs of CPU per chunk; below that floor, per-dispatch overhead (worker wake,
  page cache attach) would dominate the chunk's own cost.
- Per-chunk dirty-page tracking uses a local pooled `ChangeSet`, capped at chunk end — this does not
  change the engine's WAL/checkpoint contract for the rest of the tick.
- Runs after the user's tick DAG completes and before `UoW.Flush()` — its WAL publishes land in the
  same fsync as the tick's other commits, not a tick later.

## 🧪 Tests

- [ParallelFenceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/ParallelFenceTests.cs) — opt-in/opt-out fence correctness, migration storms, spawn+mutation ticks, no-corruption/no-duplicate-chunk-execution
- [AdaptiveFenceCostTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/AdaptiveFenceCostTests.cs) — `AdaptiveFenceCost` on/off, cost-model recalibration from measured wall-time
- [FenceWorkPlanPackTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/FenceWorkPlanPackTests.cs) — chunk bin-packing against oversubscription cap and per-unit cost

## 🔗 Related

- Parent feature: [Tick-Based Execution Engine](./README.md)
- Sibling: [Worker Pool & Threading Model](./worker-pool-threading.md), [Execution Modes](./execution-modes.md)
- Sibling: [Spatially-Coherent Entity Clustering](../../Spatial/spatial-coherent-clustering.md) — defines what a cluster migration is and why it's deferred to the tick fence instead of applied inline
- Also catalogued from the tick-durability angle: [Tick Lifecycle → Parallel Tick Fence](../tick-lifecycle/parallel-tick-fence.md)

<!-- Deep dive: claude/overview/13-runtime.md §Parallel Tick Fence -->
