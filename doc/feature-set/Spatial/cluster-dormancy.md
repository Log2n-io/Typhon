---
uid: feature-spatial-cluster-dormancy
title: 'Cluster Dormancy (Sleep / Wake)'
description: 'Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched.'
---

# Cluster Dormancy (Sleep / Wake)
> Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Even inside a coarse simulation tier, a large share of clusters do nothing tick after tick — ants in empty
space, idle props, units with no orders. [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) and
its cell amortization already shrink the per-tick cluster set; Cluster Dormancy shrinks it further by
letting the engine notice clusters that have gone quiet and stop dispatching them entirely, until something
inside them actually changes again.

## ⚙️ How it works (in brief)

Each cluster carries a sleep counter, advanced once per tick at the cluster tick fence: a tick with no write
landing in the cluster increments it, a tick with one resets it to zero. Once the counter reaches a
configurable per-archetype threshold, the cluster flips `Active` → `Sleeping` and is dropped from every
system's dispatch list for that archetype — tier-filtered or not — at zero per-entity cost. A write to a
sleeping cluster during parallel system execution doesn't touch shared sleep state inline (that would race);
it instead enqueues a deferred wake request on the archetype's concurrent queue, drained single-threaded at the tick fence
(`Sleeping` → `WakePending`) and promoted to `Active` at the very start of the next tick, before `TierClusterIndex` rebuilds or any
system dispatches — one tick of latency end to end. An optional heartbeat interval wakes sleeping clusters anyway
on a staggered schedule, for a periodic idle re-check independent of writes.

## 💻 Usage

```csharp
// Dormancy is per-archetype and opt-in; call after InitializeArchetypes.
dbe.SetClusterDormancy<Ant>(
    thresholdTicks: 120,   // 60 Hz tick rate: 2s with no writes -> Sleeping
    heartbeatTicks: 300);  // wake briefly every 5s regardless, for an idle re-check

// From here on dormancy is fully automatic — no per-system opt-in needed. Normal component writes
// (OpenMut().Set(...)) reset the sleep counter and wake a sleeping cluster; every QuerySystem against
// this archetype, tier-filtered or not, silently skips clusters that are Sleeping.
var game = schedule.PublicTrack.DeclareDag("Game");
game.QuerySystem("IdleDrift", ctx =>
{
    foreach (var id in ctx.Entities) { /* never dispatched against a sleeping cluster */ }
}, input: () => antsView, parallel: true, tier: SimTier.Tier2, cellAmortize: 60);
```

| Parameter (`SetClusterDormancy<TArch>`) | Default | Effect |
|---|---|---|
| `thresholdTicks` | `0` (disabled) | Consecutive clean ticks before `Active` → `Sleeping`; clamped to `[0, 65535]` |
| `heartbeatTicks` | `0` (off) | When `> 0`, sleeping clusters wake on a staggered schedule for a periodic re-check |

## ⚠️ Guarantees & limits

- **Wake is bounded to one tick of latency** — a write to a sleeping cluster at tick T is guaranteed
  `WakePending` by T's tick fence and `Active` before any system dispatches at tick T+1; never permanently
  missed.
- **Zero overhead when nothing sleeps** — the dormancy filter in dispatch prep is skipped entirely
  (`SleepingClusterCount == 0`), for every system, every tick, until at least one cluster actually sleeps.
- **Activity is every write, `WriteSpatial` included** — `ClusterRef.WriteSpatial` raises no dirty bit, but
  calls `NoteSpatialWrite`, which records the cluster so the fence resets its sleep counter and enqueues a wake
  request when it is `Sleeping`; a cluster stays awake while its entities move.
- **Only two wake triggers exist today**: a write and the heartbeat timer. Proximity-based wake
  ("another entity approached") and tier-promotion wake ("camera moved closer") are not implemented — a
  sleeping cluster promoted to `Tier0` stays `Sleeping`, and excluded from dispatch, until a write or
  heartbeat wakes it.
- **Filters both tier-filtered and unfiltered systems** — a `SimTier.All` `QuerySystem` against the
  archetype still skips sleeping clusters; dormancy is not a tier-only mechanism.
- **Sleep counters are `ushort`, clamped thresholds** — `thresholdTicks` is clamped to `[0, 65535]`
  (~18 minutes at 60 Hz) so the counter can never wrap and falsely re-arm a sleep transition.
- **Cluster removal keeps the count honest** — destroying the last entity in a `Sleeping` or `WakePending`
  cluster decrements `SleepingClusterCount` when the cluster is removed from the active list.
- **Spatial cluster archetypes only** — `SetClusterDormancy<TArch>` throws `InvalidOperationException` for an
  archetype that is not registered, not cluster-backed, or has no spatial-indexed component (it has no
  dormancy state to configure).
- **Replication honours it too** — a sleeping cluster is skipped by the subscriptions projection pass as well
  as by system dispatch.

## 🧪 Tests

- [DormancyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/DormancyTests.cs) — `SleepAfterThreshold`/`SleepingClusterSkippedInDispatch`, deferred `WakeRequest_Transition`/`DuplicateWakeDeduplication`, `HeartbeatWake`, `SetDirty_WakesSleepingCluster`, `WriteSpatial_WakesASleepingCluster`, cluster-removal count bookkeeping

## 🔗 Related

- Source: [src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs) (`DormancySweep`, `ProcessWakeRequest`, `TransitionWakePendingToActive`, `SetDirty`)
- Source: [src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs) (`PendingWakeRequests`: per-archetype deferred wake requests, applied at the fence by `DatabaseEngine.DrainDormancyWakeRequests`)
- Source: [src/Typhon.Engine/Runtime/public/TyphonRuntime.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`OnParallelQueryPrepare` dormancy filter, `BuildTierIndexesAtTickStart` wake transition)
- Source: [src/Typhon.Engine/Ecs/public/ClusterSleepState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterSleepState.cs)
- Related catalog entry: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) (the tier filter dormancy composes with)
- Runtime side: [Spatial Tiers & Adaptive Dispatch](../Runtime/spatial-tiers-adaptive-dispatch/README.md) (how tiers, amortization, checkerboard and dormancy compose in system dispatch)
- Related catalog entry: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) (`WriteSpatial`, cluster/dirty-bitmap fundamentals)

<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md § Cluster-Level Dormancy -->
<!-- Rules: rules/spatial.md (module Dormancy, DM-01..DM-03) -->
