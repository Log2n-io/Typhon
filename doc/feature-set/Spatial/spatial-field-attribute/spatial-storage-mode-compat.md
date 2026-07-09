---
uid: feature-spatial-spatial-field-attribute-spatial-storage-mode-compat
title: 'Storage-Mode Compatibility (SingleVersion / Versioned)'
description: 'The same [SpatialIndex] field works on SingleVersion and Versioned components — only when the tree catches up differs.'
---

# Storage-Mode Compatibility (SingleVersion / Versioned)
> The same `[SpatialIndex]` field works on SingleVersion and Versioned components — only *when* the tree catches up differs.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Spatial](../README.md)

## 🎯 What it solves

A spatial field can live on a fast, loss-tolerant `SingleVersion` component (a ship's position) or on a full-MVCC
`Versioned` component (a building's footprint) — but those two storage modes commit data on very different
schedules. Game code needs a clear answer to "when does my write to a spatially-indexed field become visible to
a spatial query", or it will assume the tree is always current and get surprised by a query that doesn't yet see
a just-written move. This sub-feature defines that timing per storage mode, and confirms what's explicitly out
of scope: `Transient` components.

## ⚙️ How it works (in brief)

The R-Tree maintenance logic (insert/update/remove) is identical regardless of storage mode — only the trigger
point differs. For `SingleVersion`, the engine batches per-table dirty entities and applies them once per
`DatabaseEngine.WriteTickFence(tickNumber)` call, so several writes to the same entity within a tick collapse
into a single tree update of its final position. For `Versioned`, the tree update happens inline during
`Transaction.Commit()`, right after MVCC conflict detection and revision stamping. In both cases the R-Tree
itself carries no MVCC — it is always a "current state" structure reflecting the latest write/commit, never an
AS-OF snapshot. `Transient` is not a third compatibility tier: `[SpatialIndex]` on a Transient component fails
schema validation outright (see the parent feature).

## 💻 Usage

```csharp
[Component("Game.Ship", revision: 1, StorageMode = StorageMode.SingleVersion)]
public struct ShipComponent
{
    [Field] [SpatialIndex(margin: 5.0f)]
    public AABB3F Bounds;
}

[Component("Game.Building", revision: 1, StorageMode = StorageMode.Versioned)]
public struct BuildingComponent
{
    [Field] [SpatialIndex(margin: 0.0f)]
    public AABB3F Footprint;
}

// ShipArchetype.Hull / BuildingArchetype.Footprint: Comp<T> handles registered on their archetypes as usual.

// SingleVersion — committed immediately, but the R-Tree only catches up at the next tick fence.
using (var tx = dbe.CreateQuickTransaction())
{
    ref ShipComponent hull = ref tx.OpenMut(shipId).Write(ShipArchetype.Hull);
    hull.Bounds = newBounds;
    tx.Commit();
}
dbe.WriteTickFence(tickNumber);   // ← R-Tree update for SingleVersion happens here

// Versioned — the R-Tree update happens inline, as part of the commit itself.
using (var tx = dbe.CreateQuickTransaction())
{
    ref BuildingComponent b = ref tx.OpenMut(buildingId).Write(BuildingArchetype.Footprint);
    b.Footprint = newFootprint;
    tx.Commit();                  // ← R-Tree update for Versioned happens here
}
```

| Storage Mode | Update Trigger | Typical Use |
|---|---|---|
| `SingleVersion` | `WriteTickFence()` — once per tick, batched | High-frequency movement (ships, units, projectiles) |
| `Versioned` | `Transaction.Commit()` — inline, per entity | Low-frequency ACID spatial data (buildings, zones, triggers) |
| `Transient` | N/A — rejected at registration | Not applicable; spatial data must be persisted |

## ⚠️ Guarantees & limits

- Multiple writes to the same `SingleVersion` entity within one tick produce exactly one tree update (the final
  position) — intermediate positions within the tick never appear in query results.
- The R-Tree has no MVCC in either mode — it always reflects current/latest-committed state; `Versioned`'s
  revision chain does not extend to spatial queries (no AS-OF spatial reads).
- `Destroy` removes the entity from the tree at the same trigger point as updates: tick fence for
  `SingleVersion`, commit for `Versioned`.
- Enable/Disable never mutates the tree in either mode — disabled entities stay indexed. A standalone spatial
  query returns them (the caller's `Open()`/visibility check filters); `Query<T>().WhereNearby()` filters them
  out automatically.
- Contention profile differs by mode: `SingleVersion` tick-fence processing is single-threaded per
  `ComponentTable` (no contention to manage); `Versioned` commits can run concurrently, but spatially-indexed
  `Versioned` data is expected to be low-update-rate (buildings/zones, not every-tick movers).
- `Transient` rejection is a schema-registration error, not a degraded mode — there is no heap-only spatial
  index.

## 🧪 Tests

- [SpatialEcsIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialEcsIntegrationTests.cs) — `SingleVersion` tick-fence-batched update timing (`SpatialQuery_CountAndAny_RespectSpatialPredicate` via `WriteTickFence`), schema registration on both `SingleVersion` and `Versioned` spatial components

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialMaintainer.cs)
- Parent feature: [Field Attribute & Schema Integration](./README.md)
- Sibling: [Storage Modes](../../Ecs/storage-modes/README.md) — the `SingleVersion`/`Versioned`/`Transient` disciplines this compatibility table is defined against

<!-- Deep dive: claude/design/Spatial/SpatialIndex/01-architecture.md §Storage Mode Compatibility -->
<!-- Deep dive: claude/design/Spatial/SpatialIndex/05-ecs-integration.md §SpatialMaintainer -->
