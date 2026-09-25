---
uid: feature-spatial-spatial-category-filtering
title: 'Category Filtering'
description: 'Bitmask pruning skips whole clusters before geometry tests — one archetype-level mask, any-bit-overlap, and the same answer whether a cell is a linear scan or a promoted tree.'
---

# Category Filtering
> Bitmask pruning skips whole clusters before geometry tests — one archetype-level mask, any-bit-overlap, and the same answer whether a cell is a linear scan or a promoted tree.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Most spatial queries only want a subset of what is geometrically nearby — an AI perception check wants enemies, not props; a capture-zone trigger wants players, not projectiles. Without a category filter, every query opens each geometrically matching cluster and discards the irrelevant entities afterward, paying the chunk read and the per-entity bounds test for data the caller never wanted. Category Filtering pushes a 32-bit bitmask test into the index itself, so a non-matching cluster is skipped before its chunk is ever touched.

## ⚙️ How it works (in brief)

**One index, one mask, cluster granularity.** There is exactly one spatial index and it is the per-cell cluster index — no `SpatialRTree` is held outside that layer, a `[SpatialIndex]` component allocates no spatial storage segment, and every query shape resolves through the cluster path. Category filtering therefore has one home too. Nothing in the engine stores a per-entity category mask.

**The mask is an archetype constant.** It is declared on the schema field as `[SpatialIndex(Category = ...)]` and reaches the engine as `SpatialFieldInfo.Category`. Spawn (`Transaction.ECS.cs`) and cluster migration (`DatabaseEngine.ClusterMigration.cs`) OR that one value into `ClusterSpatialAabb.CategoryMask`, so a cluster's mask is the OR of N identical values — the archetype's own. The tick-fence recompute pass reads the stored mask back rather than re-deriving it (`ArchetypeClusterState.ReadStoredCategoryMask`), so it survives every AABB refresh and never changes after the first entity lands.

**Where the value is kept.** Three copies, all cluster-level and all equal. `ArchetypeClusterState.ClusterAabbs[clusterChunkId].CategoryMask` is the authority. `CellSpatialIndex.CategoryMasks[slot]` mirrors it in the per-cell linear structure-of-arrays list of cluster bounds. When a cell half is promoted above `CellTreePromoteThreshold`, `CellClusterTree` writes the same value into each R-Tree leaf entry and refits the ancestors' `UnionCategoryMask`.

**Where it prunes, and with what test.** At the cluster, before the cluster chunk is opened: a cluster is admitted when `(clusterMask & queryMask) != 0` — **any-bit-overlap**. A query mask of `0` is a sentinel meaning "no filter, accept every cluster". Because the mask is archetype-constant, every entity in an admitted cluster shares it, which makes the cluster-level decision **exact** — there is no per-entity re-filter after it, and none is needed.

**Promotion does not change the answer.** Every cluster query hands the R-Tree a mask of `0` and applies the any-bit test itself on the results: AABB and radius in `AabbClusterEnumerator`, ray and frustum in `ArchetypeClusterState.Ray`/`.Frustum`, kNN over `CellClusterTree.EnumerateClusterIds`. That is deliberate and commented at the call sites. The R-Tree's own leaf test is AND-conjunctive, so handing the mask down would make a promoted cell answer a different question from an unpromoted one — a false negative visible only above the promotion threshold, which is the hardest place to notice one. A cell promotes only past a density most deployments never reach, so most never build a tree at all — but the ones that do must not get a different answer, which is why the mask is applied above the tree rather than inside it.

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
    [SpatialIndex(Category = (uint)(Faction.Enemy | Faction.Alive))]
    public AABB2F Bounds;
}

// dbe.ConfigureSpatialGrid(...) must run before InitializeArchetypes.

var box = new AABB2F { MinX = 0, MinY = 0, MaxX = 50, MaxY = 50 };

foreach (var hit in dbe.ClusterSpatialQuery<UnitArch>().AABB(box, categoryMask: (uint)Faction.Enemy))
{
    // hit.EntityId — this archetype's mask shares at least one bit with Faction.Enemy
}
```

| Surface | Parameter | Default | Effect |
|---|---|---|---|
| `[SpatialIndex]` | `Category` | `uint.MaxValue` | The archetype's constant mask, stored on every cluster it owns |
| `ClusterSpatialQuery<T>.AABB` / `.Radius` | `categoryMask` | `uint.MaxValue` | Cluster admitted when `(Category & categoryMask) != 0`; `0` disables the filter |
| `SpatialTriggers<T>().CreateRegion` | `categoryMask` | `0` | Same cluster admit; mutable afterwards via `UpdateRegionCategoryMask` |

## ⚠️ Guarantees & limits

- **The filter is cluster-granular, and that is exact rather than approximate.** Category is archetype-level, so every entity in a cluster carries the same bits and admitting the cluster admits precisely the right entities. This is why no narrowphase category test exists.
- **The stored mask cannot be changed at runtime.** It comes from the schema attribute; there is no per-entity assignment and no mutator for a cluster's mask. Reclassifying an archetype is a schema change. The *query* side is mutable — `UpdateRegionCategoryMask` changes what a trigger region asks for, not what any cluster is.
- **Promoted and unpromoted cells answer identically**, because the mask cannot be handed to the tree: every `CellClusterTree` query wrapper — `Query`, `QueryWith`, `QueryF32`, `QueryF32With`, `QueryRay`, `QueryFrustum` — takes no `categoryMask` parameter, so the guarantee is structural rather than a convention held by comments. See `AabbClusterEnumerator.TryStartCellHalf` and the tree branch of its `MoveNext`, which carry the reasoning inline. The price is that the R-Tree's node-level union prune is unreachable for cluster queries: a promoted cell descends to every geometrically overlapping cluster and filters on the way out.
- **The AND-conjunctive test still exists inside the R-Tree, and no query reaches it.** `SpatialNodeHelper` still stores a per-entry `CategoryMask` and a per-node `UnionCategoryMask`, and every `SpatialRTree` enumerator still tests `(entry.CategoryMask & queryMask) == queryMask` — every requested bit present. But every production call site passes `0`, so those masks are written and refit and never read. No engine type hands a caller's mask down to a tree: the one spatial index lives at the cluster layer, and every query applies the any-bit test above it. The AND-conjunctive semantics hold at the R-Tree enumerators, and no query resolves through that layer.
- **The default query mask differs by surface, and it matters in exactly one case.** `ClusterSpatialQuery` defaults to `uint.MaxValue`; trigger regions default to `0`. Both accept everything, unless an archetype explicitly declares `Category = 0` — then `uint.MaxValue` rejects its clusters (`0 & 0xFFFFFFFF == 0`) while a query mask of `0` accepts them. Leave `Category` at its default, or give it at least one bit.
- **No category parameter on the `EcsQuery` spatial predicates.** `WhereInAABB`, `WhereNearby` and `WhereRay` take no `categoryMask`. Use `ClusterSpatialQuery`, or post-filter on a component.
- **Cluster ray, frustum and kNN accept a mask but are not publicly reachable.** They live on `ArchetypeClusterState`, which is `internal`; only AABB and radius are exposed through `ClusterSpatialQuery`.
- **No false negatives from mask staleness.** The mask is set once from the archetype constant and preserved across every fence recompute, so the ancestor-union staleness that afflicts a mutable per-entry mask cannot arise here.
- **Cost when unused is one branch.** A `categoryMask` of `0` short-circuits before the mask load; `uint.MaxValue` costs one AND and one compare per cluster considered, never per entity.

## 🧪 Tests

- [CellSpatialIndexTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/CellSpatialIndexTests.cs) — per-cell mask storage across add, `UpdateAt_OverwritesAabbAndMask`, and the swap-with-last removal path; plus `ClusterSpatialAabb_Union_MultipleEntities_EnclosesAllAndCombinesMasks` for the OR
- [ClusterSpatialAabbRecomputeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterSpatialAabbRecomputeTests.cs) — `TickFence_CategoryMaskPreservedAcrossRecompute` stamps a non-default mask and asserts the fence does not clobber it
- [SpatialRTreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialRTreeTests.cs) — the tree-internal AND-conjunctive machinery (`Query_WithCategoryMask_FiltersCorrectly`, `CategoryMask_WithBruteForce_RandomData`, `SetEntryCategoryMask_UpdatesLeafAndAncestors`). These drive the tree directly rather than through a query path, so they cover storage and refit rather than any behaviour a caller can observe
- [ClusterCategoryFilterTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialGrid/ClusterCategoryFilterTests.cs) — two archetypes with **different** categories on identical geometry, separated by the mask alone through the public `AABB` and `Radius` surfaces in both the linear and the promoted arm; `ThePromotedAndUnpromotedArmsAnswerIdentically_UnderEveryMask` compares entity sets either side of the promotion threshold, which is what would fail if a mask were ever handed to a tree; `EveryShapeAppliesTheSameAnyBitTest` covers ray, frustum and kNN; and `NoCellClusterTreeQueryTakesACategoryMask` asserts by reflection that the tree wrappers take no mask at all. Alpha's category deliberately carries **two** bits, because any-bit and all-bits agree on a one-bit category and a fixture built on those would pass against either semantics

## 🔗 Related

- Source: [src/Typhon.Schema.Definition/Attributes.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/Attributes.cs) (`SpatialIndexAttribute.Category`; the any-bit semantics are documented inline)
- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialAabb.cs) (the authoritative per-cluster mask and its union helpers)
- Source: [src/Typhon.Engine/Spatial/internals/CellSpatialIndex.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellSpatialIndex.cs) (the per-cell linear mirror)
- Source: [src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/AabbClusterEnumerator.cs) (the any-bit test, on both the linear and the promoted path)
- Source: [src/Typhon.Engine/Spatial/internals/CellClusterTree.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/CellClusterTree.cs) (writes the mask into leaf entries when a cell is promoted)
- Source: [src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/ClusterSpatialQuery.cs) (public query entry point)
- Source: [src/Typhon.Engine/Spatial/public/SpatialObservers.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialObservers.cs) (the trigger surface, which also takes a mask)
- Related catalog entry: [Spatial Query API](./spatial-query-api.md), [Spatial Query Predicates](../Querying/spatial-predicates.md)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F1 — Category Filtering: design rationale, bit-width choice, node-layout impact) -->
<!-- Rules: rules/spatial.md (SH-01 one index and it is the cluster index; CA-01 cluster AABB + mask maintenance; SQ-01 query completeness; SQ-02 any-bit cluster semantics, the all-bits tree machinery no query reaches, and the prohibition on handing a mask to a promoted cell's tree) -->
