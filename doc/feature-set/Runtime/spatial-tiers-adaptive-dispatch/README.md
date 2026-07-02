# Spatial Tiers & Adaptive Dispatch
> Per-cluster simulation tiers let systems run near entities every tick and far entities at reduced/amortized/dormant rates.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

A 10M-entity world can't run every system at full frequency for every entity — distance-based detail
reduction is mandatory, but a per-entity distance check every tick costs O(N) before any tiering even
pays off. Game code needs a cheap, engine-owned way to say "this spatial region gets full simulation,
that one gets a fraction of it, that other one barely runs at all" — and have every system, view, and
parallel dispatch path downstream honor that classification automatically, at cluster granularity, with
no per-system spatial bookkeeping.

## ⚙️ How it works (in brief)

Game code assigns one of four `SimTier` flags (`Tier0`..`Tier3`) to each spatial grid cell, once per
tick, via `TickContext.SpatialGrid`. At tick start the engine rebuilds — per cluster-eligible archetype,
skipped whenever nothing changed — a compact list of active clusters grouped by tier
(`TierClusterIndex`). Three runtime mechanisms read that index to scope dispatch: a system's `Tier(...)`
filter restricts it to matching clusters and can add `CellAmortize(N)` to process only `1/N` of them per
tick; `Checkerboard()` splits a tier-filtered cluster set into two conflict-free Red/Black dispatch
phases for systems that touch neighboring cells; and per-cluster dormancy puts clusters that haven't
been written to in a configurable number of ticks to sleep, removing them from every dispatch path at
zero cost until something wakes them. All three compose — a system can be tier-filtered, amortized, and
checkerboard-dispatched at once, and dormancy filters out sleeping clusters underneath all of them.

## Sub-features

| Sub-feature | Use it when... |
|---|---|
| [Tier-Filtered & Amortized Dispatch](./tier-filtered-amortized-dispatch.md) | A system should only run against near/far cells, or should spread coarse-tier work across several ticks instead of doing it all at once |
| [Cluster Dormancy (Sleep/Wake)](./cluster-dormancy.md) | A large share of clusters go idle for long stretches (empty regions, parked units) and shouldn't be dispatched at all while quiet |
| [Checkerboard (Red/Black) Dispatch](./checkerboard-dispatch.md) | A parallel system reads/writes neighboring cells (diffusion, propagation) and needs conflict-free dispatch without going single-threaded |

## ⚠️ Guarantees & limits

- All three mechanisms operate on cluster lists, not per-entity checks — the cost of tiering scales with
  active cluster count, never with entity count.
- One tick of staleness end to end — a cell's tier change, a migration, or a dormancy wake all take
  effect at the *next* tick's dispatch, never the current one (`BuildTierIndexesAtTickStart` runs before
  any system in the tick).
- `TierClusterIndex` rebuild is dual-invalidated (grid tier version + per-archetype cluster-set version)
  — a stationary camera with no migrations pays two integer compares per archetype, not a re-scan.
- Composing tier filtering, amortization, checkerboard, and dormancy is additive and engine-managed —
  game code never has to intersect cluster sets by hand.
- `TickContext.TierBudgetMetrics` exposes per-tier wall-clock cost and entity counts from the previous
  tick so a `TierAssignment`-style system can adapt tier boundaries to the actual frame budget instead of
  fixed radii.

## 🧪 Tests

- [TierDispatchTests](../../../../test/Typhon.Engine.Tests/Runtime/TierDispatchTests.cs) — `TierClusterIndex` rebuild/staleness, tier-filtered dispatch, `CellAmortize`, `AmortizedDeltaTime`
- [DormancyTests](../../../../test/Typhon.Engine.Tests/Runtime/DormancyTests.cs) — sleep-threshold counter, wake on write/heartbeat, dispatch skips sleeping clusters
- [CheckerboardTests](../../../../test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — Red/Black two-phase split, no-overlap, composition with tier filtering and dormancy

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/public/SimTier.cs](../../../../src/Typhon.Engine/Runtime/public/SimTier.cs), [TierBudgetMetrics.cs](../../../../src/Typhon.Engine/Runtime/public/TierBudgetMetrics.cs)
- Also cataloged from the spatial-grid side: [Spatial category](../../Spatial/README.md) — [Tiered Simulation Dispatch](../../Spatial/tiered-simulation-dispatch.md), [Cluster Dormancy](../../Spatial/cluster-dormancy.md), [Checkerboard Dispatch](../../Spatial/checkerboard-dispatch.md) cover the same mechanisms from the cell/grid-configuration angle
- Sub-features: [Tier-Filtered & Amortized Dispatch](./tier-filtered-amortized-dispatch.md), [Cluster Dormancy (Sleep/Wake)](./cluster-dormancy.md), [Checkerboard (Red/Black) Dispatch](./checkerboard-dispatch.md)

<!-- Deep dive: claude/overview/13-runtime.md §Spatial Tiers & Multi-Resolution Dispatch -->
<!-- Deep dive: claude/rules/spatial.md (modules TierClusterIndex, Dormancy, Checkerboard Partition) -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md -->
