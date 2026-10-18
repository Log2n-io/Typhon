---
uid: feature-subscriptions-groups
title: 'Groups'
description: 'A session holds a whole set of entities that share a key — a town and its buildings — while its observer meets the town, and is told when it holds all of it.'
---

# Groups
> A session holds a whole set of entities that share a key — a town and its buildings — while its observer meets the town, and is told when it holds all of it.

**Status:** 🚧 Partial — region sets on Sphere observers, positioned members · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A Sphere around the player answers "what is near me". Some sets are anchored on something else: "the buildings of this town" is defined by
the town, not by the observer. At a town's north edge a sphere misses its south edge and fills the rest with desert, and no radius expresses
the set. A group set names the set by a key the members carry — the town each building belongs to — and a session holds every member of a
town while it is in or near it, with a completion signal a client can act on: once a town is complete, every building of it is in the store.

## ⚙️ How it works (in brief)

- **Membership is the engine's own index.** The key field carries `[Index(AllowMultiple = true, Sparse = true)]`: 0 is "no group" and is not
  indexed, every commit and fence path maintains it, a crash rebuilds it. Replication reads that index to fill a town and keeps no copy.
- **A region set** names a region archetype — one entity per town, carrying the key and an extent (`AABB2F`, `AABB3F`, `BSphere2F`,
  `BSphere3F`). A session's Sphere meets a town when its anchor is within the declared reach of the extent (0 inside it), keeps it within
  reach plus the set's leave margin, and releases it beyond. A town met again is filled and completed again.
- **Fill under the enter budget.** A town's members enter in ascending netId order, sharing the frame's enter budget with the cells; the
  frame carrying the last one also carries `GROUP_COMPLETE{set, key}`. A release carries `GROUP_RELEASED{set, key}` with the leaves of the
  members nothing else holds. A `RESET` releases everything; the towns still met are filled and completed again.
- **Union with geometry.** A member the Sphere also holds is one entity in the store; a member moving between two held towns is an update.
- **Key changes need no push.** Writing a member's key component marks it like a position write, so the projection sees the new key the
  same tick.

## 💻 Usage

```csharp
[Component("Game.Town", 1, StorageMode = StorageMode.SingleVersion)]
struct TownOf { [Index(AllowMultiple = true, Sparse = true)] public int Town; }   // 0 = in no town

[Component("Game.Zone", 1, StorageMode = StorageMode.SingleVersion)]
struct Zone { public int Key; public AABB2F Extent; }

subs.Groups("town", g => g
    .Region<TownRegion>(r => r.Key(TownRegion.Zone, z => z.Key).Extent(TownRegion.Zone, z => z.Extent))
    .Member<Building>(m => m.Key(Building.Town, t => t.Town))
    .Leave(30));                                                     // hysteresis, metres

subs.Profile("player", p => p.Sphere(192).Of<Creature>().Groups("town", reachM: 50));

var options = new SubscriptionsOptions { MaxGroupsPerSession = 8 /* required with any set */ };
```

On the client: `store.IsGroupComplete(set, key)`, `store.CompleteGroups` and the frame's `store.Groups` (.NET);
`applier.groups.isComplete(set, key)` (TypeScript); `typhon_group_is_complete` and `typhon_groups_get` (C). The set's index is its position
in the catalog's `groupSets`, in name order.

## ⚠️ Guarantees & limits

- Refused at `Start`: a set with no region, groups on a `World` or `ClientRegion` observer, a key that is not an integer, not sparse and
  multi-value, on a `Transient` (#1162) or `Versioned` component, or of another signedness than its region key, a member with no position or no
  projection, more than four sets per archetype or 256 per runtime, an extent of another type, and a declaration without `MaxGroupsPerSession` (at
  most 1 024).
- A profile may name groups and no archetype: its Sphere then holds the towns' members and nothing by geometry.
- Past `MaxGroupsPerSession` towns met at once, the kept ones and then the nearest are held and the rest counted.
- Not built yet: explicit and shared sets (a session asking for a key), positionless members, visibility tags.
- Measured (`GroupRegionBench`, 14 § 3.6): a 456 m town on a 192 m Sphere costs no measurable gather time (within 1 µs over 128 sessions) and no steady-state bytes; widening
  the Sphere to 456 m instead sends 2.8× the bytes.

## 🧪 Tests

- [PushGroupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushGroupTests.cs) — the oracle (towns, members and walking sessions under churn and skips), hysteresis and re-entry, budgeted fill, moves between towns, a destroyed town, the refusals, the tap's mutant
- [FrameApplierTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/FrameApplierTests.cs) — the client's complete set, release and `RESET`, a duplicate completion counted
- The `stream-kitchen-sink`, `tick-blocks` and `catalog-refusals` vectors, replayed by the .NET, C and TypeScript SDKs

## 🔗 Related

- See also: [Profiles & observers](./profiles-observers.md), [Sparse Secondary Index](../Indexing/secondary-index-storage-modes/sparse-secondary-index.md)
- Rule: [SUB-33](https://github.com/Log2n-io/Typhon/blob/main/rules/subscriptions.md#sub-33-a-session-holds-the-groups-its-observer-meets-each-whole-and-is-told-once-per-hold-fatalsilent)
