---
uid: feature-spatial-cluster-dormancy
title: 'Cluster Dormancy (Sleep / Wake)'
description: 'Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched.'
---

# Cluster Dormancy (Sleep / Wake)
> Clusters with no component writes for N ticks sleep and skip dispatch entirely, waking within one tick of being touched.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Even inside a coarse simulation tier, a large share of clusters do nothing tick after tick — ants in empty
space, idle props, units with no orders. [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) and
its cell amortization already shrink the per-tick cluster set; Cluster Dormancy shrinks it further by
letting the engine notice clusters that have gone quiet and stop dispatching them entirely, until something
inside them actually changes again.

## ⚙️ How it works (in brief)

Each cluster carries a sleep counter that increments every tick its dirty-bitmap region is clean (no
component write landed in it) and resets to zero the moment a write does land. Once the counter reaches a
configurable per-archetype threshold, the cluster flips `Active` → `Sleeping` and is dropped from every
system's dispatch list for that archetype — tier-filtered or not — at zero per-entity cost. A write to a
sleeping cluster during parallel system execution doesn't touch shared sleep state inline (that would race);
it instead records a deferred wake request on a thread-local list, drained single-threaded at the tick fence
(`Sleeping` → `WakePending`) and promoted to `Active` at the very start of the next tick, before any system
dispatches — one tick of latency end to end. An optional heartbeat interval wakes sleeping clusters anyway
on a staggered schedule, for a periodic idle re-check independent of writes.

## 💻 Usage

```csharp
// Dormancy is per-archetype and opt-in. SleepThresholdTicks / HeartbeatIntervalTicks live on the
// archetype's cluster runtime state (ArchetypeClusterState, internal) — there is no public configuration
// wrapper yet, so setting them requires the same engine-internal access AntHill.Core already builds under.
var clusterState = dbe._archetypeStates[Archetype<Ant>.Metadata.ArchetypeId].ClusterState;
clusterState.SleepThresholdTicks = 120;     // 60 Hz tick rate: 2s with no writes -> Sleeping
clusterState.HeartbeatIntervalTicks = 300;  // wake briefly every 5s regardless, for an idle re-check

// From here on dormancy is fully automatic — no per-system opt-in needed. Normal component writes
// (OpenMut().Write(...)) reset the sleep counter and wake a sleeping cluster; every QuerySystem against
// this archetype, tier-filtered or not, silently skips clusters that are Sleeping.
schedule.QuerySystem("IdleDrift", ctx =>
{
    foreach (var id in ctx.Entities) { /* never dispatched against a sleeping cluster */ }
}, input: () => antsView, parallel: true, tier: SimTier.Tier2, cellAmortize: 60);
```

| Field (`ArchetypeClusterState`) | Default | Effect |
|---|---|---|
| `SleepThresholdTicks` | `0` (disabled) | Consecutive clean ticks before `Active` → `Sleeping`; clamped to `[0, 65535]` |
| `HeartbeatIntervalTicks` | `0` (off) | When `> 0`, sleeping clusters wake on a staggered schedule for a periodic re-check |

## ⚠️ Guarantees & limits

- **Wake is bounded to one tick of latency** — a write to a sleeping cluster at tick T is guaranteed
  `WakePending` by T's tick fence and `Active` before any system dispatches at tick T+1; never permanently
  missed.
- **Zero overhead when nothing sleeps** — the dormancy filter in dispatch prep is skipped entirely
  (`SleepingClusterCount == 0`), for every system, every tick, until at least one cluster actually sleeps.
- **Activity is the dirty bitmap, not all writes** — `ClusterRef.WriteSpatial` (the typical high-frequency
  path for a `[SpatialIndex]`-marked field, e.g. movement) deliberately does **not** mark the slot dirty, so
  it neither resets the sleep counter nor wakes a sleeping cluster. A cluster whose entities only move via
  `WriteSpatial` can go to sleep, and stay asleep, while still moving — resetting the counter requires a
  normal `Transaction.OpenMut().Write(...)` on some other field, or an explicit `SetDirty` call after
  `WriteSpatial`.
- **Only two wake triggers exist today**: a dirty write and the heartbeat timer. Proximity-based wake
  ("another entity approached") and tier-promotion wake ("camera moved closer") are not implemented — a
  sleeping cluster promoted to `Tier0` stays `Sleeping`, and excluded from dispatch, until a write or
  heartbeat wakes it.
- **Filters both tier-filtered and unfiltered systems** — a `SimTier.All` `QuerySystem` against the
  archetype still skips sleeping clusters; dormancy is not a tier-only mechanism.
- **Sleep counters are `ushort`, clamped thresholds** — `SleepThresholdTicks` is clamped to `[0, 65535]`
  (~18 minutes at 60 Hz) so the counter can never wrap and falsely re-arm a sleep transition.
- **Cluster removal keeps the count honest** — destroying the last entity in a `Sleeping` or `WakePending`
  cluster decrements `SleepingClusterCount` when the cluster is removed from the active list.
- **No public configuration API yet** — `ArchetypeClusterState` is engine-internal; only the
  `ClusterSleepState` enum is part of the public surface. Configuring dormancy currently requires the host
  project to have `InternalsVisibleTo` access to `Typhon.Engine` (the same boundary `AntHill.Core` and
  `tsh` build under).

## 🧪 Tests

- [DormancyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/DormancyTests.cs) — `SleepAfterThreshold`/`SleepingClusterSkippedInDispatch`, deferred `WakeRequest_Transition`/`DuplicateWakeDeduplication`, `HeartbeatWake`, `SetDirty_WakesSleepingCluster` vs `WriteSpatial` not waking, cluster-removal count bookkeeping

## 🔗 Related

- Source: [src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs) (`DormancySweep`, `ProcessWakeRequest`, `TransitionWakePendingToActive`, `SetDirty`)
- Source: [src/Typhon.Engine/Runtime/internals/DormancyReporter.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/internals/DormancyReporter.cs) (thread-local deferred wake requests)
- Source: [src/Typhon.Engine/Runtime/public/TyphonRuntime.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`OnParallelQueryPrepare` dormancy filter, `BuildTierIndexesAtTickStart` wake transition)
- Source: [src/Typhon.Engine/Ecs/public/ClusterSleepState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterSleepState.cs)
- Related catalog entry: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) (the tier filter dormancy composes with)
- Related catalog entry: [Spatially-Coherent Entity Clustering](./spatial-coherent-clustering.md) (`WriteSpatial`, cluster/dirty-bitmap fundamentals)
- Sibling: [Cluster Dormancy (Sleep/Wake)](../Runtime/spatial-tiers-adaptive-dispatch/cluster-dormancy.md) — same feature cataloged from the Runtime/dispatch angle rather than the spatial-grid angle

<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md § Cluster-Level Dormancy -->
<!-- Rules: claude/rules/spatial.md (module Dormancy, DM-01..DM-03) -->
