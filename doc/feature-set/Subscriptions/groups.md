---
uid: feature-subscriptions-groups
title: 'Groups'
description: 'A session holds a whole set of entities that share a key — a town and its buildings — while its observer meets the town, and is told when it holds all of it.'
---

# Groups
> A session holds a whole set of entities that share a key — a town and its buildings — while its observer meets the town, and is told when it holds all of it.

**Status:** 🚧 Partial — region, explicit and shared sets on Sphere observers, positioned and positionless members · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

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
- **A client can ask.** An explicit set's keys are the ones a session's client requested (`SubscribeRequest`, built-in command 1) and the
  application allowed, plus the ones a system chose (`SetSources`). Each request is answered in `SOURCES`: `SOURCE_APPLIED` once its key is complete,
  or `SOURCE_ERROR` with a code — the engine's below 100, the application's from 100. A shared set has one key every session naming it holds.
- **Members need no position.** An archetype with none (a structure's shards, a joint record) is a member like any other, reached through its
  keys alone. It is in no realm and in no cell, and it has replication state only while some session holds a key it carries: a cluster of it gets
  a block when a held key needs one, and gives it back once unneeded for 64 ticks. A key completes only once every member has that state and has
  entered. A Versioned key on such a member may be written off the tick; the fill reads the commits' moves beside the index, so none is lost.
- **Group keys are engine-wide, not per realm.** A positionless member belongs to no realm, so a key is one group wherever it is held: two realms
  whose regions use key 7 — or whose sessions subscribe to it — see the same members. That is how data is shared across realms (a guild roster seen
  from every instance); an application that wants instances isolated gives each its own keys, and an explicit set's `admit` decides who may ask.

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

// A member with no position: no Position or Motion, a member of the set, named by no observer.
subs.Archetype<Shard>(a => a.Field(Shard.Data, d => d.Material, Codec.U8, name: "material"));
subs.Groups("structure", g => g
    .Region<StructureGroup>(r => r.Key(StructureGroup.Bounds, b => b.Id).Extent(StructureGroup.Bounds, b => b.Box))
    .Member<Structure>(m => m.Key(Structure.Group, s => s.Id))
    .Member<Shard>(m => m.Key(Shard.Group, s => s.Id)));
```

```csharp
// An explicit set: keys a client asks for, allowed or not by the application; a shared one: one key for every session.
subs.Groups("myLists", g => g
    .Explicit((in GroupRequest r) => Owns(r.Session, r.Key) ? GroupDecision.Allow : GroupDecision.Deny(403))
    .Member<Listing>(m => m.Key(Listing.Owner, o => o.Account)));
subs.Groups("bazaar", g => g.Shared(1).Member<Listing>(m => m.Key(Listing.Listed, l => l.Board)));
subs.Profile("trader", p => p.Sphere(64).Groups("myLists").Groups("bazaar"));

ctx.Subscriptions.Session(session).SetSources("myLists", accountId);   // the server's keys, beside the client's
```

On the client: `store.IsGroupComplete(set, key)`, `store.CompleteGroups` and the frame's `store.Groups` (.NET);
`applier.groups.isComplete(set, key)` (TypeScript); `typhon_group_is_complete` and `typhon_groups_get` (C). The set's index is its position
in the catalog's `groupSets`, in name order.

Asking for a key, in each SDK — the answer arrives once the key's members are in the store:

```csharp
var answer = await client.SubscribeAsync("myLists", accountId);          // .NET: Applied, Refused (answer.Code), Rejected or Dropped
await client.UnsubscribeAsync("myLists", accountId);
```

```typescript
const subs = new GroupSubscriptions(session.plan);                        // TypeScript: pump before each flush, observe after each frame
const answer = await subs.subscribe('myLists', accountId);               // { outcome: 'applied' | 'refused' | 'rejected' | 'dropped', code, … }
```

```c
uint16_t id;                                                             /* C: sent by the next typhon_commands_flush */
typhon_group_subscribe(client, set, account_id, &id);
/* after a poll: typhon_subscribe_results_get(client, &results, &count) — outcome TYPHON_SUBSCRIBE_APPLIED, _REFUSED, _REJECTED or _DROPPED */
```

Requests leave in call order, paced by the command's rate; one the server still refuses for its rate goes again by itself; a session that ends
drops what is still waiting — a new session holds none of it.

## ⚠️ Guarantees & limits

- Refused at `Start`: a set with no region, groups on a `World` or `ClientRegion` observer, a key that is not an integer, not sparse and
  multi-value, on a `Transient` component (#1162), or of another signedness than its region key, a member with no projection, an observer
  naming an archetype with no position, more than four sets per archetype or 256 per runtime, an extent of another type, and a declaration
  without `MaxGroupsPerSession` (at most 1 024).
- A region in a realm other than 0 needs a positioned region archetype: one with no spatial field has no realm, and its regions are realm 0's.
- A profile may name groups and no archetype: its Sphere then holds the towns' members and nothing by geometry.
- Past `MaxGroupsPerSession` towns met at once, the kept ones and then the nearest are held and the rest counted.
- Explicit and shared sets: a profile names them with `Groups(set)`, no reach; at most `MaxGroupsPerSession` keys per set, a client's and the server's
  together; `SubscribeRequest` is rate-limited to 5 a second past a burst of `MaxGroupsPerSession` (the catalog says so: a login may subscribe every
  key it may hold at once), and a request past it — or past the answers a session may be owed, twice `MaxGroupsPerSession` and at least 64 — is an
  `ACK`, not an answer. A handler that throws or returns `default` refuses the request `Denied`; a subscription still filling when the session's profile stops
  naming the set is answered `NotInProfile`. A realm-kind variant names the same explicit sets as its profile.
- Visibility tags apply to members as to everything else ([Profiles & observers](./profiles-observers.md)): a town is complete when every member
  the session sees has entered, and a hidden member is never sent.
- Measured (`GroupRegionBench`, 14 § 3.6): a 456 m town on a 192 m Sphere costs no measurable gather time (within 1 µs over 128 sessions) and no steady-state bytes; widening
  the Sphere to 456 m instead sends 2.8× the bytes.

## 🧪 Tests

- [PushGroupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushGroupTests.cs) — the oracle (towns, members and walking sessions under churn and skips), hysteresis and re-entry, budgeted fill, moves between towns, a destroyed town, the refusals, the tap's mutant
- [PushGroupShardTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushGroupShardTests.cs) — positionless members: state only while held, the budget, a reference resolved once its target has state, #1389's split and merge, an off-DAG key move between the projection and the gather
- [LiveGroupSubscriptionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/LiveGroupSubscriptionTests.cs) — `SubscribeAsync` over TCP to a live engine: applied after the completion, the application's code, a login burst, a release, dropped on close
- The `subscribe-requests` vector — one script of requests, pumps, frames and a close, replayed by the .NET, TypeScript and native helpers
- [PushGroupRequestTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushGroupRequestTests.cs) — explicit and shared sets: answers, codes, the server's keys, a RESET
- [RealmGroupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Realms/RealmGroupTests.cs) — a positionless member held by the sessions of the realm its region is in
- [FrameApplierTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/FrameApplierTests.cs) — the client's complete set, release and `RESET`, a duplicate completion counted
- The `stream-kitchen-sink`, `tick-blocks` and `catalog-refusals` vectors, replayed by the .NET, C and TypeScript SDKs

## 🔗 Related

- See also: [Profiles & observers](./profiles-observers.md), [Sparse Secondary Index](../Indexing/secondary-index-storage-modes/sparse-secondary-index.md)
- Rule: [SUB-33](https://github.com/Log2n-io/Typhon/blob/main/rules/subscriptions.md#sub-33-a-session-holds-the-groups-its-observer-meets-each-whole-and-is-told-once-per-hold-fatalsilent)
