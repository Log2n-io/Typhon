# Cluster Dormancy (Sleep/Wake)
> Clusters untouched for N ticks sleep and are skipped by every dispatch path, waking within one tick of being written to.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Tier filtering already shrinks the dispatch set for distant regions, but even a coarse tier still
contains clusters doing nothing tick after tick — idle units, empty cells, parked props. Dormancy goes
one step further: it lets the runtime notice a cluster has gone quiet and stop dispatching it to *any*
system against that archetype, tier-filtered or not, until something inside it changes again — without
game code having to track idle state itself or remember to skip it.

## ⚙️ How it works (in brief)

Each cluster carries a sleep counter, advanced once per tick at the cluster tick fence: a tick with no
dirty write in the cluster increments it, a tick with one resets it to zero. Once the counter reaches a
configurable per-archetype threshold, the cluster flips `Active → Sleeping` and every parallel dispatch
path drops it from its cluster partition at zero per-entity cost. Because a write to a sleeping cluster
can happen from any worker thread mid-tick, the wake isn't applied inline (that would race on shared
sleep state) — it's recorded on a `[ThreadStatic]` list and drained single-threaded at the tick fence
(`Sleeping → WakePending`), then promoted to `Active` at the very start of the next tick, before
`TierClusterIndex` rebuilds or any system dispatches. End to end that's at most one tick of wake latency.
An optional heartbeat interval wakes sleeping clusters anyway on a staggered schedule, independent of
writes, for a periodic idle re-check.

## 💻 Usage

Dormancy is fully automatic from a system author's point of view — once thresholds are set for an
archetype, no per-system opt-in is needed. Ordinary writes wake a sleeping cluster; every `QuerySystem`
against the archetype, tier-filtered or not, silently skips clusters that are `Sleeping`:

```csharp
game.QuerySystem("IdleDrift", ctx =>
{
    foreach (var id in ctx.Entities) { /* never dispatched against a sleeping cluster */ }
}, input: () => antsView, parallel: true, tier: SimTier.Tier2, cellAmortize: 60);
```

The sleep threshold and heartbeat interval themselves are per-archetype runtime settings
(`SleepThresholdTicks`, `HeartbeatIntervalTicks`) — there is no public fluent configuration wrapper for
them yet; today they're set on the archetype's internal cluster runtime state from the host project
(the same `InternalsVisibleTo` boundary `AntHill.Core` builds under for engine integration), not from
arbitrary game code.

| Setting | Default | Effect |
|---|---|---|
| `SleepThresholdTicks` | `0` (disabled) | Consecutive clean ticks before `Active → Sleeping`; clamped to `[0, 65535]` |
| `HeartbeatIntervalTicks` | `0` (off) | When `> 0`, sleeping clusters wake on a staggered schedule for a periodic re-check |

## ⚠️ Guarantees & limits

- **Wake is bounded to one tick of latency** — a write at tick T is `WakePending` by T's tick fence and
  `Active` before any system dispatches at tick T+1; never permanently missed.
- **Zero overhead when nothing sleeps** — the dormancy filter in dispatch-prepare is skipped entirely
  while the sleeping count is zero, for every system, every tick.
- **Filters both tier-filtered and unfiltered systems** — a `SimTier.All` system still skips sleeping
  clusters; dormancy is not a tier-only mechanism.
- **Activity means a dirty write, not a spatial-only write** — a field written exclusively through the
  zero-copy spatial-update path does not mark the cluster dirty, so it neither resets the sleep counter
  nor wakes a sleeping cluster on its own.
- **Two wake triggers only**: a dirty write and the heartbeat timer. Proximity-based wake and
  tier-promotion wake are not implemented — a sleeping cluster promoted to `Tier0` stays asleep, excluded
  from dispatch, until a write or heartbeat wakes it.
- **No public per-archetype configuration API yet** — see Usage above; `ClusterSleepState` is the only
  public surface today.

## 🧪 Tests

- [DormancyTests](../../../../test/Typhon.Engine.Tests/Runtime/DormancyTests.cs) — `SleepAfterThreshold`, `SleepingClusterSkippedInDispatch`, `WakeRequest_Transition`, `HeartbeatWake`, `NoOverhead_WhenNoSleeping`, `SleepCounterReset_OnWrite`

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/internals/DormancyReporter.cs](../../../../src/Typhon.Engine/Runtime/internals/DormancyReporter.cs) (thread-local deferred wake requests, single-threaded drain)
- Source: [src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs](../../../../src/Typhon.Engine/Ecs/internals/ArchetypeClusterState.cs) (`DormancySweep`, `ProcessWakeRequest`, `TransitionWakePendingToActive`)
- Source: [src/Typhon.Engine/Ecs/public/ClusterSleepState.cs](../../../../src/Typhon.Engine/Ecs/public/ClusterSleepState.cs)
- Parent feature: [Spatial Tiers & Adaptive Dispatch](./README.md)
- Same mechanism, full configuration walkthrough: [Spatial category — Cluster Dormancy (Sleep / Wake)](../../Spatial/cluster-dormancy.md)

<!-- Deep dive: claude/overview/13-runtime.md §Cluster Dormancy -->
<!-- Rules: claude/rules/spatial.md (module Dormancy, DM-01..DM-03) -->
