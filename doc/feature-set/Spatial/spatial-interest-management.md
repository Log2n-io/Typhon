---
uid: feature-spatial-spatial-interest-management
title: 'Interest Management (Delta Spatial Queries)'
description: 'Per-observer "what changed near me since tick T" queries in O(dirty × observers), not O(everything in view × observers).'
---

# Interest Management (Delta Spatial Queries)
> Per-observer "what changed near me since tick T" queries in O(dirty × observers), not O(everything in view × observers).

**Status:** 🚧 Partial · **Visibility:** Public · **Category:** [Spatial](./README.md)

## 🎯 What it solves

Multiplayer servers face the N-squared broadcast problem: naively re-sending every entity's state to every connected player each tick costs O(entities × players) bandwidth and CPU. What each observer actually needs is much smaller — only the entities near them that changed since the observer last looked. Interest Management answers exactly that question per observer, without the caller re-running a full spatial query and diffing it by hand every tick.

## ⚙️ How it works (in brief)

Instead of the traditional "per observer: spatial query, then filter by freshness" (O(entities in view × observers)), Typhon inverts the order: the engine archives each tick's dirty bitmap into a 64-tick ring buffer per cluster archetype, then for a delta request it ORs together the bitmaps for the ticks the observer missed and walks only the resulting (small) dirty set, reading each dirty entity's current bounds straight out of cluster storage and testing them against the observer's interest AABB and category mask. Cost scales with how much changed, not with how much exists. An observer whose `LastConsumedTick` has fallen more than 64 ticks behind the ring (or who consumes before any tick has been archived) cannot be served a delta and instead gets a full-sync result — every currently-matching entity, treated as "changed." The full sync is an ordinary AABB query through the per-cell cluster index, the same one every other spatial query uses.

## 💻 Usage

```csharp
// The facade is a thin handle over engine-owned state — cheap to obtain, nothing to dispose.
SpatialObserverSet interest = dbe.SpatialObservers<Position>();

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
| `categoryMask` | `0` | `0` = no filtering; non-zero = AND-conjunctive, the same semantics the cluster broadphase applies |
| `initialTick` | `0` | Starting point for delta accumulation on the first `GetSpatialChanges` call |

## ⚠️ Guarantees & limits

- **Reachable from application code** — `dbe.SpatialObservers<T>()` returns a public `SpatialObserverSet` facade over the engine-owned system, so an application reaches it without an internal factory. A `default`-constructed facade is rejected by every member; check `IsValid` if you did not obtain it from the extension method.
- **SV-only** — dirty-bitmap ring archival only exists for SingleVersion/Transient `ComponentTable`s and SV-backed cluster archetypes; Versioned tables don't participate and have no ring to query.
- **No missed changes** — any entity mutated at tick T, still matching an observer's region and category mask at query time, appears in that observer's `ChangedEntities` provided `LastConsumedTick < T ≤ currentTick` and T is still within the ring.
- **Ring depth is 64 ticks** (~2.1s at 30Hz) — an observer that doesn't call `GetSpatialChanges` for longer than that is flagged `IsFullSync` rather than served a (silently incomplete) delta.
- **Deltas cover movement, not arbitrary component edits** — an observer is told that an entity's bounds changed inside its region, and that is the only kind of change it is told about. An entity whose health or inventory changed while it stood still does not appear in the delta. If you need those, watch the component yourself; there is no observer tier that projects them.
- **Zero-allocation in steady state** — `ChangedEntities` is a span over a per-observer buffer reused across calls; valid only until the next `GetSpatialChanges` call for that same observer.
- **Handles are generation-checked** — calling any method with an unregistered or stale (reused-slot) handle throws `ArgumentException`.
- **Do not hold the facade across a schema migration** — the underlying state is created on first use and lives as long as the component's `ComponentTable`, which is the engine's lifetime in ordinary use but not across a migration that reconstructs the table. Obtain it again afterwards rather than caching it.
- **Reads the one spatial index** — the delta path reads each cluster archetype's dirty ring and the entity bounds in cluster storage; the full-sync path issues an AABB query through the per-cell cluster index.

## 🧪 Tests

- [SpatialInterestTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/SpatialIndex/SpatialInterestTests.cs) — observer lifecycle + generation-checked handle reuse, `UpdateObserverBounds`, dirty-entity delta reporting in/out of region

## 🔗 Related

- Source: [src/Typhon.Engine/Spatial/public/SpatialObservers.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialObservers.cs) (the public `SpatialObserverSet` facade and the `SpatialObservers<T>()` extension)
- Source: [src/Typhon.Engine/Spatial/internals/SpatialInterestSystem.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/internals/SpatialInterestSystem.cs) (observer registry, inverted dirty-set delta query, full-sync fallback)
- Source: [src/Typhon.Engine/Spatial/public/SpatialObserverHandle.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialObserverHandle.cs) (public handle type)
- Source: [src/Typhon.Engine/Spatial/public/SpatialChangeResult.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Spatial/public/SpatialChangeResult.cs) (public result type)
- Source: [src/Typhon.Engine/Ecs/internals/DirtyBitmapRing.cs](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DirtyBitmapRing.cs) (64-tick archival ring, multi-tick OR accumulation)
- Related catalog entry: [Category Filtering](./spatial-category-filtering.md) (the AND-conjunctive mask semantics this feature reuses)

<!-- Deep dive: claude/design/Spatial/SpatialIndex/08-game-features.md (Feature F4 — Interest Management: inverted dirty-set rationale, ring buffer design, Tier 1/Tier 2 split) -->
<!-- Rules: rules/spatial.md (Module: Interest Management — IM-01 no missed changes, IM-02 ring buffer safety, IM-03 SV-only scope, IM-04 both systems read the cluster index) -->
