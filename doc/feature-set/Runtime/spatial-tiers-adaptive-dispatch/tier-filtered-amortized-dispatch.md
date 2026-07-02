# Tier-Filtered & Amortized Dispatch
> Scope a system to clusters in matching tier cells, and optionally process just 1/N of them per tick.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Not every system should touch every entity every tick. A combat AI needs full fidelity near the camera
but is wasted work on the far side of the map; a slow-decay or idle-drift update is correct even if it
only runs once every few seconds for distant entities. Without engine support, a system author would
have to thread a distance/tier check through every entity loop by hand — exactly the per-entity overhead
tiering exists to avoid. Tier-filtered dispatch lets a system declare its tier scope once at
registration; cell amortization lets it additionally spread coarse-tier work across ticks instead of
doing all of it at once.

## ⚙️ How it works (in brief)

A system declares a `SimTier` filter (default `SimTier.All`, meaning unfiltered — the pre-tiering fast
path). At dispatch-prepare time the scheduler reads the system's archetype's per-tier cluster list
(rebuilt once per tick, before any system runs) and restricts the parallel chunk partition to just that
list — never touching clusters outside the filter. Adding `CellAmortize(N)` further strides that list
into `N` rotating buckets keyed by `tickNumber % N`, so the system sees `1/N` of its tier's clusters on
any given tick; `TickContext.AmortizedDeltaTime` is scaled to `DeltaTime × N` so integration math (decay,
drift, movement) stays correct across the longer effective step. A `View`'s own `WithTier(...)` filter
applies the same cluster scoping independently, useful for published/subscription views; a system's
filter and its input view's filter combine by bit-AND.

## 💻 Usage

```csharp
var game = schedule.PublicTrack.DeclareDag("Game");

// Full fidelity, every tick — Tier 0 cells only.
game.QuerySystem("CombatAi", ctx =>
{
    foreach (var id in ctx.Entities) { /* full-fidelity AI */ }
}, input: () => antsView, parallel: true, tier: SimTier.Tier0);

// Coarse tier: process 1/60th of Tier 2's clusters per tick, amortizing the integration step.
game.QuerySystem("IdleDrift", ctx =>
{
    foreach (var id in ctx.Entities)
    {
        ref var pos = ref ctx.Accessor.OpenMut(id).Write(Ant.Position);
        pos.X += DriftSpeed * ctx.AmortizedDeltaTime;   // ~1s of drift, once every 60th tick
    }
}, input: () => antsView, parallel: true, tier: SimTier.Tier2, cellAmortize: 60);

// Equivalent class-based form (SystemBuilder):
// protected override void Configure(SystemBuilder b) => b.Name("IdleDrift").Tier(SimTier.Tier2).CellAmortize(60).Parallel();
```

| Option | Default | Effect |
|---|---|---|
| `tier:` (`QuerySystem`) / `b.Tier(...)` (`SystemBuilder`) | `SimTier.All` | Restrict dispatch to clusters in matching cells; flags combine (`SimTier.Near` = `Tier0 \| Tier1`) |
| `cellAmortize:` / `b.CellAmortize(N)` | `0` (off) | Process `1/N` of the tier's clusters per tick, rotating buckets by tick number; requires a non-`All` tier |

## ⚠️ Guarantees & limits

- Filtering operates on cluster lists, never per-entity — a tier-filtered system's chunk partition is
  built only over its tier's clusters, with zero scanning of clusters outside the filter.
- `Build()`-time validation rejects `SimTier.None` (would dispatch zero clusters), `CellAmortize > 0`
  without a non-`All` tier filter, and tier filters on system types other than `QuerySystem`.
- `CellAmortize` skips whole untouched clusters between buckets — it is bucket rotation, not per-entity
  modulo, so the 59/60 you skip in a tick are never iterated, not iterated-and-ignored.
- Multi-tier flag combinations (e.g. `SimTier.Near`, `SimTier.Active`) are merged once per rebuild and
  cached by flag byte — no per-system merge allocation for repeated combinations.
- `TickContext.TierBudgetMetrics` (per-tier cost and entity count from the previous tick) is all-zero on
  the first tick — guard `BudgetMs == 0` before computing a utilization ratio.
- A system's `Tier(...)` and its input `View.WithTier(...)` combine by bit-AND; a non-overlapping
  combination throws at dispatch-prepare time rather than silently materializing zero entities.

## 🧪 Tests

- [TierDispatchTests](../../../../test/Typhon.Engine.Tests/Runtime/TierDispatchTests.cs) — `Build_TierNone_Throws`, `Build_CellAmortizeWithoutTier_Throws`, `TierDispatch_Tier0System_SeesOnlyTier0Entities`, `TierDispatch_CellAmortize_ProcessesEachCellExactlyOncePerCycle`, `TierDispatch_AmortizedDeltaTime_EqualsDeltaTimeTimesAmortize`

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/public/SimTier.cs](../../../../src/Typhon.Engine/Runtime/public/SimTier.cs), [SystemBuilder.cs](../../../../src/Typhon.Engine/Runtime/public/SystemBuilder.cs) (`Tier`, `CellAmortize`), [Dag.cs](../../../../src/Typhon.Engine/Runtime/public/Dag.cs) (`tier:`/`cellAmortize:` parameters)
- Source: [src/Typhon.Engine/Ecs/internals/TierClusterIndex.cs](../../../../src/Typhon.Engine/Ecs/internals/TierClusterIndex.cs) (per-archetype tier-grouped index, dual invalidation, merge cache)
- Source: [src/Typhon.Engine/Runtime/public/TyphonRuntime.cs](../../../../src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (`OnParallelQueryPrepare` tier-scoped partition, `BuildTierIndexesAtTickStart`)
- Parent feature: [Spatial Tiers & Adaptive Dispatch](./README.md)
- Same mechanism, cell/grid-configuration angle: [Spatial category — Tiered Simulation Dispatch](../../Spatial/tiered-simulation-dispatch.md)

<!-- Deep dive: claude/overview/13-runtime.md §Tier-Filtered System Dispatch -->
<!-- Rules: claude/rules/spatial.md (modules TierClusterIndex/TI-01..TI-03, SetCellTier Validation/SC-01) -->
