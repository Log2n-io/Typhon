---
uid: feature-spatial-spatial-rtree-index-spatial-rtree-static-dynamic
title: 'Static / Dynamic Separation'
description: 'An archetype''s clusters land in one of two independent halves of every cell — tick-fence-exempt static, or fence-maintained dynamic — chosen once at schema time.'
---

# Static / Dynamic Separation
> An archetype's clusters land in one of two independent halves of every cell — tick-fence-exempt static, or fence-maintained dynamic — chosen once at schema time.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A typical game or simulation world is mostly static geometry — terrain, buildings, walls, fixed trigger volumes — with a small fraction of entities actually moving every tick. Putting both in the same structure means every per-tick maintenance pass walks clusters that will never move, and the bound growth from moving entities keeps widening boxes that should stay tight forever. The static/dynamic split gives each cell two independent halves so movers pay tick-fence maintenance and never-movers pay none.

## ⚙️ How it works (in brief)

`SpatialMode` on `[SpatialIndex]` selects which half of every cell an archetype's clusters occupy — `Dynamic` (default) or `Static`. The choice is per spatial field, not per entity: every entity carrying that component lands in the same half, decided at schema registration, never at spawn time. A cell can hold clusters of several archetypes with different modes, so both halves are routinely populated at once, and each is independently either a linear array of cluster boxes or a promoted per-cell R-Tree (see [Per-Cell Cluster Index Internals](./README.md)).

Entities reach either half the same way — an ordinary spawn places the entity in a cluster, and the cluster is added to its cell's half. What differs is afterwards. The tick fence's AABB-refresh pass returns immediately for an archetype whose mode is not `Dynamic`, and so do drift detection and cell repair, so a static archetype's clusters are never rescanned for movement. Destruction removes the cluster from whichever half holds it by the same handle path either way.

Queries do not choose. Every AABB, radius, ray, frustum and kNN walk visits both halves of each cell it opens and unions the results, so the mode is invisible on the read side.

The engine also has a Sort-Tile-Recursive bulk-build primitive that packs a whole batch of entries into a near-optimal tree in one pass. It is genuinely internal — no production path calls it today; it is exercised by tests only — and spawning entities one at a time still produces a fully correct, query-ready static half.

## 💻 Usage

```csharp
[Component("Game.Terrain", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct TerrainPiece
{
    [Field] [SpatialIndex(Mode = SpatialMode.Static)]
    public AABB3F Footprint;
}

[Archetype]
partial class TerrainArchetype : Archetype<TerrainArchetype>
{
    public static readonly Comp<TerrainPiece> Footprint = Register<TerrainPiece>();
}

// Spawning is identical to a dynamic component — Mode only changes what happens to the entity afterward.
using (var tx = dbe.CreateQuickTransaction())
{
    tx.Spawn<TerrainArchetype>(TerrainArchetype.Footprint.Set(new TerrainPiece { Footprint = footprint }));
    tx.Commit();
}

// Querying is identical to a dynamic component too — the walk visits both halves of every cell it opens.
using var qtx = dbe.CreateQuickTransaction();
var hits = qtx.Query<TerrainArchetype>().WhereInAABB<TerrainPiece>(-5, -5, -5, 55, 15, 10).Execute();

dbe.WriteTickFence(tickNumber);   // never rescans TerrainPiece's clusters — only Dynamic-mode archetypes reach the AABB-refresh pass
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `Mode` | `SpatialMode.Dynamic` | `Static` — placed once, never revisited by tick-fence maintenance. `Dynamic` — full bound-refresh, migration, drift and repair cycle every tick. |

## ⚠️ Guarantees & limits

- **Exclusive membership** — a given archetype's clusters live in exactly one half of a cell, static *or* dynamic; the engine never splits one archetype's clusters across both.
- **Mode is schema-fixed** — set once via `[SpatialIndex(Mode = ...)]` at component registration; there is no runtime API to move an entity or a cluster between halves. Reclassifying means changing the schema and re-registering, not a per-entity operation.
- **Static skip is unconditional** — the tick fence's AABB-refresh pass, drift detection and cell repair all return immediately for a non-`Dynamic` archetype. If a static-mode component's field value does change, the change is written to component storage but the cluster bound the broadphase prunes on is not updated to match. `Mode = Static` is a correctness contract with the caller, not just a performance hint.
- **Placement and removal still work normally on the static half** — spawning adds a cluster, destroying removes it, both through the same back-pointer path as the dynamic half. Only the per-tick *movement* maintenance is skipped.
- **Queries union the halves, and there is nothing to reconcile** — `WhereInAABB`/`WhereNearby`/`WhereRay`/`WhereFrustum` visit both halves of every cell they open. A cluster appears in exactly one of them, so no deduplication is needed.
- **Each half is promoted independently** — a cell can serve its dynamic half from a per-cell R-Tree and its static half from a linear array, or the reverse. Each half's own cluster count is compared against the threshold, so a cell dense in static geometry and sparse in moving units promotes only the half that needs it.
- **Bulk construction is an internal primitive with no production caller** — the Sort-Tile-Recursive bulk-loader builds a near-optimal tree from a full dataset in one pass, but nothing in the engine calls it today, and it is not exposed through `DatabaseEngine`/ECS spawn. Populating a static half means spawning entities individually like any other component.

## 🧪 Tests

- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — `Schema_StaticMode_SetsFieldInfoMode`/`Schema_DefaultMode_IsDynamic` (mode selection at registration), `StaticComponent_InsertAndQuery`/`StaticComponent_Remove_Works`, `StaticComponent_TickFenceSkipped` (unconditional maintenance skip)
- [CellTreePromotionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/CellTreePromotionTests.cs) — `StaticTree_IsNotTouchedByTheFence` on a promoted static half, alongside promotion, demotion and rebuild coverage for the halves themselves
- [SpatialBulkLoadTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialBulkLoadTests.cs) — the internal STR `SpatialRTree.BulkLoad` primitive: valid-tree construction, query correctness vs brute force, category-mask filtering

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/PerCellSpatialSlot.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/PerCellSpatialSlot.cs) (the two halves, and the publication ordering that keeps a promotion invisible to readers)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs) (the field metadata `Mode` is read from)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialRTree.BulkLoad.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialRTree.BulkLoad.cs) (internal STR construction primitive)
- Parent feature: [Per-Cell Cluster Index Internals](./README.md)
- Sibling: [Spatial Query API](../spatial-query-api.md) — querying is identical regardless of which half a mode lands an archetype in

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md §Feature F2 — Static/Dynamic Separation -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/05-ecs-integration.md §SpatialIndexState -->
<!-- Rules: rules/spatial.md (Module: Fat AABB Updates — the invariants that apply here are ST-07 and CA-01) -->
