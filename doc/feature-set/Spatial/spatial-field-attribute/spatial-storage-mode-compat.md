---
uid: feature-spatial-spatial-field-attribute-spatial-storage-mode-compat
title: 'Storage-Mode Compatibility (SingleVersion / Versioned)'
description: 'The same [SpatialIndex] field works on SingleVersion and Versioned components — only when your write reaches the cluster slot differs.'
---

# Storage-Mode Compatibility (SingleVersion / Versioned)
> The same `[SpatialIndex]` field works on SingleVersion and Versioned components — only *when your write reaches the cluster slot* differs.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A spatial field can live on a fast, loss-tolerant `SingleVersion` component (a ship's position) or on a full-MVCC
`Versioned` component (a building's footprint) — but those two storage modes publish data on very different
schedules. Game code needs a clear answer to "when does my write to a spatially-indexed field become visible to
a spatial query", or it will assume the index is always current and get surprised by a query that doesn't yet see
a just-written move. This sub-feature defines that timing per storage mode, and confirms what's explicitly out
of scope: `Transient` components.

## ⚙️ How it works (in brief)

There are two hand-offs, not one, and only the first depends on storage mode.

**Hand-off one — your write reaches the cluster slot.** For `SingleVersion`, it is there as soon as you write it: `ClusterRef.WriteSpatial` puts the new value in the slot and flags the bookkeeping in the same call. For `Versioned`, the write is staged on the revision chain and copied into the cluster slot by the commit's publish step, after MVCC conflict detection and revision stamping — so an uncommitted move is invisible to everyone, including the fence.

**Hand-off two — the index catches up.** This one is the same for both. The cluster's bounding box and the cell index entry that the broadphase prunes on are refreshed by the tick fence, from the flags the write left behind: `ClusterProcessBitmap` and the shrink mask for a `SingleVersion` write through the barrier, the cluster dirty bit for a `Versioned` publish. Nothing updates the cell index at commit time for either mode. `Transaction.Commit()` therefore makes a `Versioned` move *readable*, not *findable*; a spatial query finds it after the next `DatabaseEngine.WriteTickFence(tickNumber)`.

`Transient` is not a third tier: `[SpatialIndex]` on a Transient component fails schema validation outright (see the parent feature).

## 💻 Usage

```csharp
[Component("Game.Ship", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct ShipComponent
{
    [Field] [SpatialIndex]
    public AABB3F Bounds;
}

[Component("Game.Building", revision: 1, StorageMode = StorageMode.Versioned)]
public struct BuildingComponent
{
    [Field] [SpatialIndex]
    public AABB3F Footprint;
}

// ShipArchetype.Hull / BuildingArchetype.Footprint: Comp<T> handles registered on their archetypes as usual.

// SingleVersion — the value is in the cluster slot at once; the index catches up at the fence.
using (var tx = dbe.CreateQuickTransaction())
{
    ref ShipComponent hull = ref tx.OpenMut(shipId).Write(ShipArchetype.Hull);
    hull.Bounds = newBounds;
    tx.Commit();
}

// Versioned — the commit copies the new value into the cluster slot and marks the cluster dirty.
using (var tx = dbe.CreateQuickTransaction())
{
    ref BuildingComponent b = ref tx.OpenMut(buildingId).Write(BuildingArchetype.Footprint);
    b.Footprint = newFootprint;
    tx.Commit();
}

dbe.WriteTickFence(tickNumber);   // ← the index update happens here, for BOTH components
```

| Storage Mode | Write reaches the cluster slot | Index catches up | Typical Use |
|---|---|---|---|
| `SingleVersion` | immediately, on the write itself | `WriteTickFence()` | High-frequency movement (ships, units, projectiles) |
| `Versioned` | at `Transaction.Commit()`, in the publish step | `WriteTickFence()` | Low-frequency ACID spatial data (buildings, zones, triggers) |
| `Transient` | N/A — rejected at registration | N/A | Not applicable; spatial data must be persisted |

## ⚠️ Guarantees & limits

- **The index update is fence-time in both modes.** A `Versioned` commit publishes the value and marks the cluster dirty; it does not touch the cluster bound or the cell index. Code that commits a move and immediately runs a spatial query in the same tick will not find it, whichever storage mode it used.
- **Multiple writes to the same entity within one tick produce one index update** — the fence recomputes a flagged cluster's bound from its occupied slots, so intermediate positions within the tick never appear in query results. This holds for `Versioned` as well as `SingleVersion`.
- **The index has no MVCC in either mode** — it reflects the state the last fence observed, not a transaction snapshot; `Versioned`'s revision chain does not extend to spatial queries, so there are no AS-OF spatial reads.
- **`Destroy` takes effect at commit, in both modes** — `ReleaseSlot` clears the entity's occupancy bit there, so it is gone from the narrowphase immediately. What waits for the fence is the *tightening* of the cluster's bound, which is flagged on all four axes at the same site.
- **`Mode = Static` overrides all of this** — a static archetype is skipped by the fence's bound-refresh, drift and repair passes whatever its storage mode, so a value change is written to storage and the index is not updated to match. That is the documented contract, not a timing window; see [Static / Dynamic Separation](../spatial-rtree-index/spatial-rtree-static-dynamic.md).
- **Contention profile differs by mode** — `SingleVersion` writes go through a barrier whose bookkeeping is all `Interlocked`, so workers may write different slots of one cluster concurrently; `Versioned` commits serialise on MVCC conflict detection, and spatially-indexed `Versioned` data is expected to be low-update-rate (buildings and zones, not every-tick movers).
- **`Transient` plus `[SpatialIndex]` throws at registration** — the failure is loud and immediate, before the schema is built, rather than a component that registers and then answers no query.
- **Enable/Disable is not applied by the spatial path.** *Verified against `EcsQuery.ExecuteSpatial` and `AabbClusterEnumerator`: neither consults a visibility gate, and the only filter applied to a spatial result is archetype routing. `Query<T>().WhereNearby()` does not filter disabled entities, so treat a disabled entity as still findable and filter it yourself.*

## 🧪 Tests

- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — fence-batched update timing via `WriteTickFence` (`SpatialQuery_CountAndAny_RespectSpatialPredicate`), and schema registration on both `SingleVersion` and `Versioned` spatial components
- [ClusterVersionedTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/ClusterVersionedTests.cs) — the `Versioned`-in-a-cluster publish path this page's first hand-off describes

## 🔗 Related

- Source: [src/Typhon.Engine/Transactions/public/Transaction.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/public/Transaction.cs) (`PrepareClusterVersionedSlot` / `PublishClusterVersionedSlot` — the commit-side hand-off)
- Source: [src/Typhon.Engine/Ecs/public/ClusterRef.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/ClusterRef.cs) (`WriteSpatial` — the SingleVersion write barrier)
- Parent feature: [Field Attribute & Schema Integration](./README.md)
- Sibling: [Storage Modes](../../Ecs/storage-modes/README.md) — the `SingleVersion`/`Versioned`/`Transient` disciplines this compatibility table is defined against

<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md §Storage Mode Compatibility -->
<!-- Rules: rules/spatial.md (CA-01/CA-02 for the bound and the index entry the fence refreshes) -->
