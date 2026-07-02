# Spatial Grid Configuration & Tier Control
> One global grid, one cell size, and a per-cell simulation-tier control surface for multi-resolution worlds.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Large worlds can't afford to simulate every entity at full frequency every tick — a 10M-entity world needs the few thousand entities near a player to run physics at 60 Hz while everything else runs coarser or not at all. Doing this with per-entity distance checks costs O(N) every frame and collapses well before six figures. Spatial Grid Configuration sets up the engine-wide coordinate grid every spatial archetype shares, and the tier control surface lets game code assign a simulation tier (full / reduced / coarse / dormant) per cell — cheaply, once per tick — instead of per entity.

## ⚙️ How it works (in brief)

`SpatialGridConfig` is computed once: world bounds and a single cell size derive the grid dimensions, and the config is handed to `DatabaseEngine.ConfigureSpatialGrid` before `InitializeArchetypes` — it cannot change afterward. All spatial archetypes share this one grid; there's no per-archetype sizing. At runtime, a `TierAssignment`-style callback system (run with `SystemPriority.High` so it executes before other systems) reads `TickContext.SpatialGrid` — an `SpatialGridAccessor` — and assigns each cell a `SimTier` flag (`Tier0`..`Tier3`). The engine consumes these per-cell tiers downstream to filter which clusters a system or query touches (see tier-filtered system dispatch in the Runtime category) — assignment itself is entirely game-owned policy; Typhon only provides storage and the helper methods below.

## 💻 Usage

```csharp
// Once, before InitializeArchetypes:
dbe.ConfigureSpatialGrid(new SpatialGridConfig(
    worldMin: new Vector2(-1000f, -1000f),
    worldMax: new Vector2( 1000f,  1000f),
    cellSize: 32f));

// Every tick, a high-priority callback system assigns tiers:
schedule.CallbackSystem("TierAssignment", ctx =>
{
    var grid = ctx.SpatialGrid;
    if (!grid.IsValid) return;

    grid.ResetAllTiers(SimTier.Tier3);               // start everyone at lowest priority

    foreach (var observer in connectedPlayers)
    {
        grid.SetTierInAABB(observer.Tier0MinX, observer.Tier0MinY,
                            observer.Tier0MaxX, observer.Tier0MaxY, SimTier.Tier0);
        grid.SetTierInAABB(observer.Tier1MinX, observer.Tier1MinY,
                            observer.Tier1MaxX, observer.Tier1MaxY, SimTier.Tier1);
    }
}, priority: SystemPriority.High);
```

| Config field | Meaning | Notes |
|---|---|---|
| `WorldMin` / `WorldMax` | World-space extent | `WorldMax` is exclusive on both axes |
| `CellSize` | Side length of one cell, world units | Must be `> 0`; one size for the whole grid |
| `MigrationHysteresisRatio` | Dead-zone fraction for migration | Default `0.05`; reserved, currently passive |

## ⚠️ Guarantees & limits

- **Set once, immutable after** — `ConfigureSpatialGrid` throws `InvalidOperationException` if called twice or after `InitializeArchetypes`.
- **One grid, one cell size, for every spatial archetype** — no per-archetype grid sizing; entity scale should be roughly uniform across archetypes sharing the grid.
- **`KeySpaceDim` capped at 32,768 per axis** — a consequence of 32-bit Morton cell-key encoding; oversized world/cell-size combinations throw `ArgumentOutOfRangeException` at config time.
- **`SetCellTier` requires a single-bit `SimTier` flag** — passing a combined flag (e.g. `Tier0 | Tier1`) is rejected; use the `Min` variants or per-call assignment for unions.
- **`SetCellTierMin` / `SetTierInAABB` are promote-only** — they never demote a cell already holding a higher-priority (lower-valued) tier, which is what makes the multi-observer "union of zones" pattern correct without per-observer bookkeeping.
- **All accessor methods throw `InvalidOperationException` when no grid is configured** — check `SpatialGridAccessor.IsValid` first, especially in non-spatial engines or during shutdown.
- **One tick of staleness** — tier assignment runs once per tick (before other systems); cells crossing a tier boundary mid-tick are dispatched at their prior tier until the next tick.
- **Tier filtering itself lives downstream** — this feature only assigns and exposes tiers; consuming them to filter system/query dispatch is a separate mechanism (rules `TI-01`..`TI-03`).

## 🧪 Tests

- [SpatialGridTests](../../../test/Typhon.Engine.Tests/Data/SpatialGrid/SpatialGridTests.cs) — grid dimension derivation, `WorldToCellKey`/`CellKeyToCoords` round-trips, `SetCellTier` single-bit validation, `KeySpaceDim` overflow throws
- [CheckerboardTests](../../../test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — `SetCellTierMin_OnlyPromotes`, `ResetAllTiers_BulkSetsAllCells`, `SetTierInAABB_MinSemantics`, `SpatialGridAccessor_AccessibleFromTickContext`/`_MultiObserver_Union` (promote-only tiering, multi-observer union)

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs](../../../src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs)
- Source: [src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs](../../../src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs)
- Source: [src/Typhon.Engine/Ecs/public/DatabaseEngine.cs](../../../src/Typhon.Engine/Ecs/public/DatabaseEngine.cs) (`ConfigureSpatialGrid`)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md) (the per-component query layer this grid complements)
- Sibling: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) — the primary consumer of the per-cell `SimTier` this feature exposes
- Overview: [Spatial Architecture Overview](./spatial-architecture-overview.md) — this grid vs. the R-Tree's own, unrelated Layer-1 occupancy filter

<!-- Deep dive: claude/design/Spatial/spatial-grid-api.md (full public API inventory) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md (tier assignment, dispatch, amortization, dormancy) -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md -->
<!-- Rules: claude/rules/spatial.md (modules SC-01, TI-01..TI-03) -->
