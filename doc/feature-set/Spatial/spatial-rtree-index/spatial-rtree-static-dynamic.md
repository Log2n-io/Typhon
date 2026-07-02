# Static / Dynamic Tree Separation
> A component's spatial field lands in one of two independent R-Trees — tick-fence-exempt static, or fat-AABB-maintained dynamic — chosen once at schema time.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A typical game or simulation world is mostly static geometry — terrain, buildings, walls, fixed trigger volumes — with a small fraction of entities actually moving every tick. Indexing both in the same R-Tree means every per-tick maintenance pass walks static entities that will never move, and the insert/split/remove churn from dynamic entities keeps widening MBRs around static ones that should stay tight forever. Static/Dynamic separation gives each spatial component its own tree so movers pay tick-fence maintenance cost and never-movers pay none.

## ⚙️ How it works (in brief)

`SpatialMode` on `[SpatialIndex]` selects which of the two trees a component's spatial field uses — `Dynamic` (default) or `Static`. The choice is per spatial field, not per entity: every entity carrying that component lands in the same tree, decided at schema registration, never at spawn time. Entities reach either tree the same way — a normal insert when the entity is spawned — but only the dynamic tree is visited by the per-tick (SingleVersion) or per-commit (Versioned) fat-AABB maintenance pass; the static tree is never scanned for movement once an entity is in it. Destruction still removes an entity from whichever tree it's in via the same O(1) back-pointer lookup. Internally the engine also has a Sort-Tile-Recursive bulk-build primitive that packs a whole batch of entries into a near-optimal tree in one pass — useful for constructing a large static tree from a pre-known dataset rather than one incremental insert at a time — but it is an engine-internal construction path today, not something application code calls directly; spawning entities one at a time still produces a fully correct, query-ready static tree.

## 💻 Usage

```csharp
[Component("Game.Terrain", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct TerrainPiece
{
    [Field] [SpatialIndex(margin: 0.0f, Mode = SpatialMode.Static)]
    public AABB3F Footprint;
}

[Archetype(50)]
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

// Querying is identical to a dynamic component too.
using var qtx = dbe.CreateQuickTransaction();
var hits = qtx.Query<TerrainArchetype>().WhereInAABB<TerrainPiece>(-5, -5, -5, 55, 15, 10).Execute();

dbe.WriteTickFence(tickNumber);   // never visits TerrainPiece's static tree — only Dynamic-mode components are scanned for movement
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `Mode` | `SpatialMode.Dynamic` | `Static` — inserted once, never visited by tick-fence/commit maintenance. `Dynamic` — full fat-AABB update cycle every tick/commit. |

## ⚠️ Guarantees & limits

- **Exclusive membership** — a given component's spatial field lives in exactly one tree (static *or* dynamic); the engine never holds the same `ComponentTable`'s entities split across both.
- **Mode is schema-fixed** — set once via `[SpatialIndex(Mode = ...)]` at component registration; there is no runtime API to move an entity between trees. Reclassifying means changing the schema and re-registering, not a per-entity operation.
- **Static skip is unconditional** — the tick-fence (SingleVersion) and commit (Versioned) maintenance passes never visit static-tree entities; if a static-mode component's field value does change, that change is written to component storage but the tree is not updated to match — `Mode = Static` is a correctness contract with the caller, not just a performance hint.
- **Insert and remove still work normally on the static tree** — spawning adds an entity, destroying removes it, both via the same back-pointer path as the dynamic tree. Only the per-tick *movement* maintenance is skipped.
- **No merge/dedup needed at query time** — `WhereInAABB`/`WhereNearby`/`WhereRay` against a component resolve to that component's single active tree; there's no second tree to fan out to or reconcile results against.
- **Bulk construction is an internal primitive, not a public API** — the engine has a Sort-Tile-Recursive bulk-loader that builds a near-optimal tree from a full dataset in one pass, but it isn't currently exposed through `DatabaseEngine`/ECS spawn; populating a static tree today means spawning entities individually like any other component.

## 🧪 Tests

- [SpatialEcsIntegrationTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — `Schema_StaticMode_SetsFieldInfoMode`/`Schema_DefaultMode_IsDynamic` (mode selection at registration), `StaticComponent_InsertAndQuery`/`StaticComponent_Remove_Works`, `StaticComponent_TickFenceSkipped` (unconditional maintenance skip)
- [SpatialBulkLoadTests](../../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialBulkLoadTests.cs) — the internal STR `SpatialRTree.BulkLoad` primitive: valid-tree construction, query correctness vs brute force, category-mask filtering

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs](../../../../src/Typhon.Engine/Spatial/internals/SpatialIndexState.cs)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialRTree.BulkLoad.cs](../../../../src/Typhon.Engine/Spatial/internals/SpatialRTree.BulkLoad.cs) (internal STR construction primitive)
- Parent feature: [Spatial R-Tree Index](./README.md)
- Sibling: [Fat-AABB Incremental Update](../fat-aabb-update.md) — only `Dynamic`-mode fields (this feature's default) receive fat-AABB maintenance; `Static` fields never do
- Sibling: [Spatial Query API](../spatial-query-api.md) — querying is identical regardless of which tree a field's mode lands it in

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md §Feature F2 — Static/Dynamic Separation -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/05-ecs-integration.md §SpatialIndexState -->
<!-- Rules: claude/rules/spatial.md §Module: Fat AABB Updates (SF-02) -->
