---
uid: feature-spatial-tiered-simulation-dispatch
title: 'Tiered Simulation Dispatch'
description: 'One simulation tier per spatial cell, four dispatch frequencies, zero per-entity distance checks.'
---

# Tiered Simulation Dispatch
> One simulation tier per spatial cell, four dispatch frequencies, zero per-entity distance checks.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Large worlds can't run every entity at full simulation frequency — entities near the player need 60 Hz
physics, the next ring only needs movement updates, and the bulk of a 10M-entity world should barely be
touched. Deciding "should I simulate this" with a per-entity distance check costs O(N) every tick and
becomes the bottleneck before any tiering even pays off. Tiered Simulation Dispatch lets game code
classify the world coarsely — per spatial cell, once per tick — and have every system and view
downstream automatically restrict its work to the matching cells, at cluster granularity, for near-zero
extra cost.

## ⚙️ How it works (in brief)

Game code assigns a `SimTier` flag (`Tier0`..`Tier3`) to each cell via `TickContext.SpatialGrid` (see
[Spatial Grid Configuration](./spatial-grid-config.md)). At tick start the engine rebuilds, per
archetype, a compact list of active clusters grouped by tier; the rebuild is skipped whenever neither
the grid's tier assignment nor the archetype's cluster set changed since the last tick. A `QuerySystem`
declares a `Tier(...)` filter to dispatch only against clusters in matching cells, and can add
`CellAmortize(N)` to additionally process just `1/N` of those clusters per tick — rotating cluster
buckets, not skipping entities one by one — for the coarsest tiers. `View.WithTier` applies the same
scoping to a View's materialized entity set, useful for published/subscription views or change-filter
scans. `TickContext.TierBudgetMetrics` reports per-tier wall-clock cost from the previous tick so a
`TierAssignment` system can adapt tier boundaries to a frame budget instead of fixed radii.

## 💻 Usage

```csharp
// Game-owned policy: assign cell tiers once per tick, before any tier-filtered system runs.
var game = schedule.PublicTrack.DeclareDag("Game");
game.CallbackSystem("TierAssignment", ctx =>
{
    var grid = ctx.SpatialGrid;
    if (!grid.IsValid) return;

    grid.ResetAllTiers(SimTier.Tier3);
    grid.SetTierInAABB(camera.Tier0MinX, camera.Tier0MinY, camera.Tier0MaxX, camera.Tier0MaxY, SimTier.Tier0);
    grid.SetTierInAABB(camera.Tier1MinX, camera.Tier1MinY, camera.Tier1MaxX, camera.Tier1MaxY, SimTier.Tier1);

    // Adapt next tick's radius to this tick's measured cost.
    if (ctx.TierBudgetMetrics.UtilizationRatio > 0.85f)
    {
        camera.Tier0Radius *= 0.95f;
    }
}, priority: SystemPriority.High);

// Full-fidelity AI: every tick, Tier 0 cells only.
game.QuerySystem("CombatAi", ctx =>
{
    foreach (var id in ctx.Entities) { /* full-fidelity AI */ }
}, input: () => antsView, parallel: true, tier: SimTier.Tier0);

// Coarse tier: process 1/60th of Tier 2's clusters per tick, integrate over the elapsed bucket time.
game.QuerySystem("IdleDrift", ctx =>
{
    foreach (var id in ctx.Entities)
    {
        ref var pos = ref ctx.Accessor.OpenMut(id).Write(Ant.Position);
        pos.X += DriftSpeed * ctx.AmortizedDeltaTime;   // ~1s of drift, once every 60th tick
    }
}, input: () => antsView, parallel: true, tier: SimTier.Tier2, cellAmortize: 60);

// A published view scoped to Tier 0 — the output phase only computes deltas for nearby entities.
var viewportView = tx.Query<Ant>().ToView().WithTier(SimTier.Tier0);
```

| Option | Default | Effect |
|---|---|---|
| `tier:` (`QuerySystem`) / `b.Tier(...)` (class-based) | `SimTier.All` | Restrict dispatch to clusters in matching cells; flags combine (e.g. `SimTier.Near` = `Tier0 \| Tier1`) |
| `cellAmortize:` / `b.CellAmortize(N)` | `0` (off) | Process `1/N` of the tier's clusters per tick, rotating buckets by tick number; requires a non-`All` tier |
| `View.WithTier(SimTier)` | `SimTier.All` | Scope a View's materialized entity set / change-filter scan to matching cells |

## ⚠️ Guarantees & limits

- Filtering operates on cluster lists, not per-entity checks — a tier-filtered system touches only the
  clusters in its tier, never scans the rest of the archetype.
- Tier-list rebuild is dual-invalidated (grid tier version + per-archetype cluster-set version) —
  camera-stationary, no-migration ticks pay two integer compares, not a re-scan.
- `CellAmortize` skips whole untouched clusters; it is not entity-id modulo — the 59/60 you skip in a
  tick are never iterated.
- A system's `Tier(...)` and its input View's `WithTier` combine by bit-AND; if the two have no overlap
  (e.g. system `Tier0` against a view `WithTier(Tier1)`), the engine throws at dispatch-prepare time
  instead of silently materializing zero entities.
- One tick of staleness — a cell's tier change takes effect at the next tick's dispatch, same as the
  underlying grid (see [Spatial Grid Configuration](./spatial-grid-config.md)).
- `TickContext.TierBudgetMetrics` is all-zero on the first tick — guard `BudgetMs == 0` before computing
  utilization ratios.
- Stored cell tiers must be single-bit (`SetCellTier` rejects combined flags like `Tier0 | Tier1`);
  multi-tier flags (`Near`, `Active`, `All`) are valid only as system/view filters, never as a cell's
  stored value.

## 🧪 Tests

- [TierDispatchTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/TierDispatchTests.cs) — tier-list rebuild/invalidation, `Tier(...)`/`CellAmortize` dispatch scoping, `View.WithTier`, tier×view AND-combination throw, `TierBudgetMetrics`

## 🔗 Related

- Source: [src/Typhon.Engine/Runtime/public/SimTier.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/SimTier.cs), [Dag.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/Dag.cs), [SystemBuilder.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/SystemBuilder.cs)
- Source: [src/Typhon.Engine/Ecs/internals/TierClusterIndex.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/TierClusterIndex.cs), [TyphonRuntime.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TyphonRuntime.cs) (tier-scoped dispatch, budget metrics)
- Related catalog entry: [Spatial Grid Configuration & Tier Control](./spatial-grid-config.md) (per-cell tier assignment, `SpatialGridAccessor`)
- Sibling: [Tier-Filtered & Amortized Dispatch](../Runtime/spatial-tiers-adaptive-dispatch/tier-filtered-amortized-dispatch.md) — same feature cataloged from the Runtime/dispatch angle rather than the spatial-grid angle

<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md, 04-tick-integration.md -->
<!-- Rules: claude/rules/spatial.md (modules TierClusterIndex/TI-01..TI-03, SetCellTier Validation/SC-01) -->
