---
uid: feature-spatial-spatial-interest-management
title: 'Interest Management (Delta Spatial Queries)'
description: 'Per-observer "what changed near me since tick T" queries in O(dirty × observers), not O(everything in view × observers).'
---

# Interest Management (Delta Spatial Queries)
> Per-observer "what changed near me since tick T" queries in O(dirty × observers), not O(everything in view × observers).

**Status:** 🚧 Partial · **Visibility:** Internal · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Multiplayer servers face the N-squared broadcast problem: naively re-sending every entity's state to every connected player each tick costs O(entities × players) bandwidth and CPU. What each observer actually needs is much smaller — only the entities near them that changed since the observer last looked. Interest Management answers exactly that question per observer, without the caller re-running a full spatial query and diffing it by hand every tick.

## ⚙️ How it works (in brief)

Instead of the traditional "per observer: spatial query, then filter by freshness" (O(entities in view × observers)), Typhon inverts the order: the engine archives each tick's `DirtyBitmap` into a 64-tick ring buffer, then for a delta request it ORs together the bitmaps for the ticks the observer missed and walks only the resulting (small) dirty set, testing each dirty entity's current position against the observer's interest AABB and category mask. Cost scales with how much changed, not with how much exists. An observer whose `LastConsumedTick` has fallen more than 64 ticks behind the ring (or who consumes before any tick has been archived) cannot be served a delta and instead gets a full-sync result — every currently-matching entity, treated as "changed."

## 💻 Usage

```csharp
// Engine-internal entry point today — see Guarantees & limits.
var table = dbe.GetComponentTable<Position>();
var interest = table.SpatialIndex.GetOrCreateInterestSystem(table);

// Register once per connected client, e.g. centered on their camera/view frustum AABB.
double[] bounds = { 0, 0, 0, 200, 200, 200 };   // [minX,minY,minZ, maxX,maxY,maxZ]
SpatialObserverHandle observer = interest.RegisterObserver(bounds, categoryMask: (uint)Faction.Enemy, initialTick: dbe.CurrentTick);

// Each tick, after dbe.WriteTickFence(tick) has archived that tick's dirty set:
SpatialChangeResult delta = interest.GetSpatialChanges(observer, currentTick: tick);
if (delta.IsFullSync)
{
    // Observer fell off the 64-tick ring (or first call) — resync its whole interest region.
}
foreach (long entityId in delta.ChangedEntities)
{
    // Serialize this entity's current state into the observer's outgoing packet.
}

// On camera/view move:
interest.UpdateObserverBounds(observer, newBounds);

// On disconnect:
interest.UnregisterObserver(observer);
```

| `RegisterObserver` arg | Default | Effect |
|---|---|---|
| `bounds` | required | Interest AABB, `[minX,minY,(minZ,) maxX,maxY,(maxZ)]` |
| `categoryMask` | `0` | `0` = no filtering; non-zero = AND-conjunctive, same semantics as R-Tree category filtering |
| `initialTick` | `0` | Starting point for delta accumulation on the first `GetSpatialChanges` call |

## ⚠️ Guarantees & limits

- **Engine-internal only today** — `SpatialInterestSystem`, `ComponentTable.SpatialIndex`, and `SpatialIndexState.GetOrCreateInterestSystem` are all `internal`. There is no public `DatabaseEngine` method to reach an observer system; only `SpatialObserverHandle` and `SpatialChangeResult` are public types. Application code cannot call this without engine-internal access today (this is the "Partial" status).
- **SV-only (rule IM-03)** — dirty-bitmap ring archival only exists for SingleVersion/Transient `ComponentTable`s and SV-backed cluster archetypes; Versioned tables don't participate and have no ring to query.
- **No missed changes (rule IM-01)** — any entity mutated at tick T, still matching an observer's region and category mask at query time, appears in that observer's `ChangedEntities` provided `LastConsumedTick < T ≤ currentTick` and T is still within the ring.
- **Ring depth is 64 ticks** (~2.1s at 30Hz) — an observer that doesn't call `GetSpatialChanges` for longer than that is flagged `IsFullSync` rather than served a (silently incomplete) delta (rule IM-02).
- **Only Tier 1 (`GetSpatialChanges`) is implemented** — spatial/bounds changes only. The design's Tier 2 (`GetEntityChanges`, any component on the entity changed, via cross-table dirty projection) is documented but not built.
- **Zero-allocation in steady state** — `ChangedEntities` is a span over a per-observer buffer reused across calls; valid only until the next `GetSpatialChanges` call for that same observer.
- **Handles are generation-checked** — calling any method with an unregistered or stale (reused-slot) handle throws `ArgumentException`.
- **Covers both spatial-index layers** — a single delta call fans out across the legacy per-component R-Tree path and the per-cluster archetype path, so observers don't need to know which storage backs a given archetype.

## 🧪 Tests

- [SpatialInterestTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialInterestTests.cs) — observer lifecycle + generation-checked handle reuse, `UpdateObserverBounds`, dirty-entity delta reporting in/out of region

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/internals/SpatialInterestSystem.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialInterestSystem.cs) (observer registry, inverted dirty-set delta query, full-sync fallback)
- Source: [src/Typhon.Engine/Spatial/public/SpatialObserverHandle.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialObserverHandle.cs) (public handle type)
- Source: [src/Typhon.Engine/Spatial/public/SpatialChangeResult.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialChangeResult.cs) (public result type)
- Source: [src/Typhon.Engine/Ecs/internals/DirtyBitmapRing.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DirtyBitmapRing.cs) (64-tick archival ring, multi-tick OR accumulation)
- Related catalog entry: [Category Filtering](./spatial-category-filtering.md) (the AND-conjunctive mask semantics this feature reuses)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F4 — Interest Management: inverted dirty-set rationale, ring buffer design, Tier 1/Tier 2 split) -->
<!-- Rules: claude/rules/spatial.md (Module: Interest Management — IM-01 no missed changes, IM-02 ring buffer safety, IM-03 SV-only scope) -->
