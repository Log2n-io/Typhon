# Category Filtering
> Bitmask pruning skips whole subtrees and clusters before geometry tests — AND-conjunctive at the R-Tree, any-bit-overlap at the cluster broadphase.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Most spatial queries only want a subset of what's geometrically nearby — an AI perception check wants enemies, not props; a capture-zone trigger wants players, not projectiles. Without a category filter, every query visits all geometrically matching entities and discards the irrelevant ones afterward, wasting the bulk of the traversal on data the caller never wanted. Category Filtering pushes a 32-bit bitmask test into the index itself so non-matching subtrees and clusters are skipped before any per-entity work happens.

## ⚙️ How it works (in brief)

Two independent mechanisms exist, with different semantics — know which one you're using. **R-Tree (per-component index):** each leaf entry carries a 32-bit `CategoryMask`, and each internal node carries a `UnionCategoryMask` (the OR of its subtree's entry masks). Traversal prunes a subtree when `node.UnionCategoryMask & queryMask == 0`; surviving leaf entries are tested exactly with `(entry.CategoryMask & queryMask) == queryMask` — **AND-conjunctive**, every requested bit must be present. **Per-cluster broadphase:** category is an *archetype-level* constant set on the `[SpatialIndex]` field, not per-entity — every entity in the archetype contributes the same value, so a cluster's mask is just that constant once occupied. A cluster is admitted when `(clusterMask & queryMask) != 0` — **any-bit-overlap**, not AND. Both layers never produce false negatives; a removed entity's bit can linger in an ancestor's union mask until the next refit, causing extra (never missing) traversal.

## 💻 Usage

```csharp
[Flags]
public enum Faction : uint
{
    Player = 1 << 0,
    Enemy  = 1 << 1,
    Alive  = 1 << 2,
}

public struct Position
{
    [SpatialIndex(margin: 0f, Category = (uint)(Faction.Enemy | Faction.Alive))]
    public AABB2F Bounds;
}

// dbe.ConfigureSpatialGrid(...) must run before InitializeArchetypes — see the cluster broadphase setup.

var box = new AABB2F { MinX = 0, MinY = 0, MaxX = 50, MaxY = 50 };

foreach (var hit in dbe.ClusterSpatialQuery<UnitArch>().AABB(box, categoryMask: (uint)Faction.Enemy))
{
    // hit.EntityId — cluster's mask shared at least one bit with Faction.Enemy
}
```

| `[SpatialIndex]` arg | Default | Effect |
|---|---|---|
| `Category` | `uint.MaxValue` | Archetype-constant bitmask; cluster admitted when `(Category & queryCategoryMask) != 0` |
| `categoryMask` (query param) | `uint.MaxValue` | `0` disables the filter entirely (accept all clusters) |

## ⚠️ Guarantees & limits

- **R-Tree per-entry filtering is engine-internal today** — `SpatialNodeHelper`'s `CategoryMask`/`UnionCategoryMask` storage, pruning, and the AND-conjunctive leaf test are fully implemented and exercised by every R-Tree query enumerator (AABB/Radius/Ray/Frustum/kNN/Count), but reachable only via the internal `SpatialQuery<T>` handle — the public `EcsQuery.WhereInAABB`/`WhereNearby`/`WhereRay` predicates do not yet expose a `categoryMask` parameter, and there is no public API to assign a per-entity category. Use the two-pass pattern (spatial query → component post-filter) until that lands.
- **Cluster broadphase is the publicly usable path today** — `[SpatialIndex(Category = ...)]` plus `ClusterSpatialQuery<TArch>.AABB`/`.Radius`, both fully public.
- **Cluster category is archetype-level, not per-entity** — it cannot vary entity-to-entity within an archetype, and there is no runtime mutator; reclassifying requires a schema-level `Category` change.
- **Semantics differ by layer** — R-Tree is AND-conjunctive (`entry.CategoryMask & queryMask == queryMask`); cluster broadphase is OR/any-bit-overlap (`clusterMask & queryMask != 0`). Mixing up the two produces wrong filtering, not a crash.
- **No false negatives** — union-mask staleness after a remove only causes extra (unnecessary) traversal, never a missed match, at either layer.
- **Zero-cost when unused** — default `Category`/`categoryMask` of `uint.MaxValue` and the `0` "no filter" sentinel mean queries that never opt in pay no extra branch cost beyond the mask compare.

## 🧪 Tests

- [SpatialRTreeTests](../../../test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeTests.cs) — R-Tree AND-conjunctive leaf test and union-mask pruning (`Query_WithCategoryMask_FiltersCorrectly`, `CategoryMask_WithBruteForce_RandomData`, `SetEntryCategoryMask_UpdatesLeafAndAncestors`)
- [CellSpatialIndexTests](../../../test/Typhon.Engine.Tests/Data/SpatialGrid/CellSpatialIndexTests.cs) — per-cluster `CategoryMask` storage and any-bit-overlap union computation for the cluster broadphase

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialNodeHelper.cs](../../../src/Typhon.Engine/Spatial/internals/SpatialNodeHelper.cs) (leaf/union mask storage and refit)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialRTree.Query.cs](../../../src/Typhon.Engine/Spatial/internals/SpatialRTree.Query.cs) (per-enumerator pruning and leaf test)
- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs](../../../src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs) (per-cluster category union)
- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs](../../../src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs) (public query entry point)
- Source: [src/Typhon.Schema.Definition/Attributes.cs](../../../src/Typhon.Schema.Definition/Attributes.cs) (`SpatialIndexAttribute.Category`, OR-disjunctive semantics documented inline)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md), [Spatial Query Predicates](../Querying/spatial-predicates.md)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F1 — Category Filtering: design rationale, bit-width choice, node-layout impact) -->
<!-- Rules: claude/rules/spatial.md (ST-02 union mask correctness, SQ-01/SQ-02 query completeness and AND-conjunctive semantics) -->
