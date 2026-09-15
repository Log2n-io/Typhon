---
uid: feature-spatial-spatial-grid-config
title: 'Spatial Grid Configuration & Tier Control'
description: 'One global grid, one cell size, and a per-cell simulation-tier control surface for multi-resolution worlds.'
---

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
// A flat (2D) world — one cell deep on Z:
dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
    worldMin: new Vector2(-1000f, -1000f),
    worldMax: new Vector2( 1000f,  1000f),
    cellSize: 32f));

// A volumetric world uses the constructor directly:
dbe.ConfigureSpatialGrid(new SpatialGridConfig(
    worldMin: new Vector3(-1000f, -1000f, -1000f),
    worldMax: new Vector3( 1000f,  1000f,  1000f),
    cellSize: 32f));

// Every tick, a high-priority callback system assigns tiers:
schedule.CallbackSystem("TierAssignment", ctx =>
{
    var grid = ctx.SpatialGrid;
    if (!grid.IsValid) return;

    grid.ResetAllTiers(SimTier.Tier3);               // start everyone at lowest priority

    foreach (var observer in connectedPlayers)
    {
        grid.SetTierInAABB(observer.Tier0MinX, observer.Tier0MinY, 0f,
                            observer.Tier0MaxX, observer.Tier0MaxY, 0f, SimTier.Tier0);
        grid.SetTierInAABB(observer.Tier1MinX, observer.Tier1MinY, 0f,
                            observer.Tier1MaxX, observer.Tier1MaxY, 0f, SimTier.Tier1);
    }
}, priority: SystemPriority.High);
```

`SpatialGridConfig` carries twenty settings. The first three define the grid itself; the rest govern how hard the engine works to keep clusters tight as entities move, and every one of them is a constructor argument with a default. This table is the inventory — for how to *derive* a value, what its safe range is, and which telemetry counter tells you it is wrong, see [Tuning the Spatial Grid](./spatial-tuning.md).

| Config field | Default | Meaning |
|---|---|---|
| `WorldMin` / `WorldMax` | required | World-space extent, `Vector3`. `WorldMax` is exclusive on all three axes |
| `CellSize` | required | Side length of one cell, world units. Must be `> 0`; one size for the whole grid, and the single most consequential setting here |
| `MigrationHysteresisRatio` | `0.05` | Dead zone past a cell face, as a fraction of cell size, that an entity's centre must clear before a cell crossing is queued. Live on the migration path |
| `ClusterTargetExtentRatio` | `0.25` | The extent a cluster is expected to stay within, as a fraction of cell size. A cluster inside it is tight enough and its entities are never examined |
| `ClusterDriftMarginRatio` | `0.05` | Intra-cell counterpart of the hysteresis ratio: the dead zone around that target region below which a drifting entity is left alone |
| `ClusterRepairExtentRatio` | `0.75` | Extent past which a cell is nominated for a full Morton re-sort. Must be below `1.2` or it can never fire |
| `ReclusterBudgetMs` | `1.0` | Per-tick, per-archetype wall-clock budget for relocation and repair. An admission threshold, not a stopping condition; `0` disables repair *and* throttle enforcement |
| `RepairNsPerEntity` | `1500` | Projected repair cost per entity, the exchange rate the budget is spent at. A seed for a runtime EWMA, not a constant |
| `RepairWorstClustersPerUnit` | `8` | How many of a cell's worst clusters one indivisible repair unit re-packs; `0` means the whole cell |
| `ClusterRepairCriticalExtentRatio` | `1.0` | Degradation at which a cell jumps the repair queue despite the budget. Must be `0` or strictly between the repair ratio and `1.2` |
| `RepairAgingRatePerTick` | `0.05` | Rank growth per tick a candidate spends waiting, so no cell starves behind better-ranked ones; `0` disables ageing |
| `RepairQueueMaxCells` | `4096` | Hard cap on queued repair candidates; beyond it the worst-ranked candidate is evicted to admit a better one |
| `ClusterTargetPackingSlack` | `1.5` | Multiplier on the per-cell packing bound that derives the target extent from live density. `0` makes `ClusterTargetExtentRatio` the constant it used to be |
| `LeastEnlargementPlacement` | `false` | Place an arrival in the cluster whose bound grows least, rather than the first with a free slot. Opt-in: it measured a wash where repair runs |
| `GrowthCapPlacement` | `false` | Open a fresh cluster for an arrival that would stretch the best candidate past the target × `GrowthCapSlack`. Implies least-enlargement ranking |
| `GrowthCapSlack` | `1.25` | How far past the target a candidate may stretch before a fresh cluster is opened |
| `MaxOpenClustersPerCell` | `4` | Open clusters the growth cap may hold per cell before falling back to least enlargement. Must be at least 1 |
| `BatchSpawnSortThreshold` | `128` | Spawn count at which a transaction places its entities in per-cell Morton order; `0` disables |
| `RepairCooldownTicks` | `50` | Ticks a cell whose repair moved entities stays out of the repair queue; nominations meanwhile are held until it returns. `0` disables |

The re-clustering and placement settings below `MigrationHysteresisRatio` are **not persisted** with the database — only world bounds, cell size and the hysteresis ratio reach the bootstrap record, because those three decide which cell a position maps to. A tool that opens the database without calling `ConfigureSpatialGrid` therefore runs the rest at their defaults, and an application that does call it always wins.

## ⚠️ Guarantees & limits

- **Set once, immutable after** — `ConfigureSpatialGrid` throws `InvalidOperationException` if called twice or after `InitializeArchetypes`.
- **One grid, one cell size, for every spatial archetype** — no per-archetype grid sizing; entity scale should be roughly uniform across archetypes sharing the grid.
- **The grid is sparse: a cell exists only once something occupies it.** A cell key is a pool slot, not a coordinate — so `SpatialGridAccessor.ComputeCellKey` / `WorldToCell` return **`-1` for an empty region**, `CellCount` counts *occupied* cells rather than the whole world, and a key must not be cached across a reopen or a rebuild (slots are handed out in creation order and renumber). Reading grid state never creates a cell; only spawning, migrating or rebuilding does. An empty region costs one absent hash entry instead of 64 bytes per cell per archetype.
- **The grid is three-dimensional; a 2D world is a grid one cell deep.** `SpatialGridConfig.Flat(Vector2, Vector2, float)` builds that shape in one call. There is deliberately no `Vector2` overload set on the query methods — a 2D/3D pair is a call site where a `Z` gets dropped silently, which surfaces as a missing query result rather than an error.
- **Cell count must fit a 32-bit key** — `GridWidth × GridHeight × GridDepth ≤ int.MaxValue`; oversized world/cell-size combinations throw `ArgumentOutOfRangeException` at config time. Cell keys are plain row-major, so the only ceiling is the product of the three axis counts.
- **`SetCellTier` requires a single-bit `SimTier` flag** — passing a combined flag (e.g. `Tier0 | Tier1`) is rejected; use the `Min` variants or per-call assignment for unions.
- **`SetCellTierMin` / `SetTierInAABB` are promote-only** — they never demote a cell already holding a higher-priority (lower-valued) tier, which is what makes the multi-observer "union of zones" pattern correct without per-observer bookkeeping.
- **All accessor methods throw `InvalidOperationException` when no grid is configured** — check `SpatialGridAccessor.IsValid` first, especially in non-spatial engines or during shutdown.
- **One tick of staleness** — tier assignment runs once per tick (before other systems); cells crossing a tier boundary mid-tick are dispatched at their prior tier until the next tick.
- **Tier filtering itself lives downstream** — this feature only assigns and exposes tiers; consuming them to filter system/query dispatch is a separate mechanism.

## 🧪 Tests

- [SpatialGridTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/SpatialGridTests.cs) — grid dimension derivation, `WorldToCellKey`/`CellKeyToCoords` round-trips, `SetCellTier` single-bit validation, cell-count overflow throws, flat-vs-cubic Z behaviour
- [VdbSpatialGridTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/VdbSpatialGridTests.cs) — differential against a dense reference oracle, neighbour lookups across absent blocks, concurrent cell creation, memory at 80 % empty, intra-block fill
- [CheckerboardTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/CheckerboardTests.cs) — `SetCellTierMin_OnlyPromotes`, `ResetAllTiers_BulkSetsAllCells`, `SetTierInAABB_MinSemantics`, `SpatialGridAccessor_AccessibleFromTickContext`/`_MultiObserver_Union` (promote-only tiering, multi-observer union)

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridConfig.cs)
- Source: [src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialGridAccessor.cs)
- Source: [src/Typhon.Engine/Ecs/public/DatabaseEngine.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DatabaseEngine.cs) (`ConfigureSpatialGrid`)
- Related catalog entry: [Tuning the Spatial Grid](./spatial-tuning.md) (how to derive every value in the table above, and the telemetry that names the wrong one)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md) (the per-component query layer this grid complements)
- Sibling: [Tiered Simulation Dispatch](./tiered-simulation-dispatch.md) — the primary consumer of the per-cell `SimTier` this feature exposes
- Overview: [Spatial Architecture Overview](./spatial-architecture-overview.md) — how this grid and the per-cell cluster broadphase are two levels of one index

<!-- Deep dive: claude/design/Spatial/spatial-grid-api.md (full public API inventory) -->
<!-- Deep dive: claude/design/Spatial/SpatialTiers/03-tier-dispatch.md (tier assignment, dispatch, amortization, dormancy) -->
<!-- ADR: claude/adr/046-spatial-tiers-architecture.md -->
<!-- Rules: rules/spatial.md (modules SC-01, TI-01..TI-03) -->
