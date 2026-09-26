---
uid: feature-spatial-realms
title: 'Realms — Several Worlds in One Engine'
description: 'Isolated worlds (planets, interiors, space, instanced dungeons) in one engine: each realm owns its grid, cell size and dimensionality, is queried alone, and runs under its own simulation policy.'
---

# Realms — Several Worlds in One Engine
> Planets, building interiors, space, instanced dungeons: each realm is its own world with its own grid, and an entity is in exactly one of them.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Spatial](./README.md)

## 🎯 What it solves

One grid cannot serve a planet (16 km at 256 m cells), a cantina (one 64 m cell) and space (a 3D volume at 500 m cells): whichever cell size you pick is
wrong for the others, and entities of two worlds that share local coordinates would land in the same cells. Faking it with coordinate offsets
("interiors live at x = 1 000 000") keeps one grid sized for everything, leaks queries across worlds at the seams, and simulates an empty building
as hard as a crowded city.

A **realm** is an isolated world. Each owns its spatial grid (bounds, cell size, 2D or 3D), its per-cell structures and its simulation policy. A
query answers in one realm only; an empty interior can sleep; thousands of one-room realms cost about a kilobyte each.

## ⚙️ How it works (in brief)

- **Realm 0 is the default world.** `ConfigureSpatialGrid(config)` registers realm 0, and an application that never names a realm keeps working
  unchanged — it has one realm, and every query, system and tier it already uses is realm 0's.
- **More realms are registered** with `ConfigureRealms(maxRealms)` (the id range, never clamped) and `Realms.Register(id, RealmConfig)`: before
  `InitializeArchetypes`, or on a running engine for instanced content. A `RealmConfig` carries the realm's `Grid`, its policy when unobserved and,
  optionally, a `Parent` (for event routing) and a `Replication` config (for [client sessions](../Subscriptions/realms.md)).
- **An entity's realm is a key.** An archetype that can live outside realm 0 marks one `ushort` field `[RealmKey]` — beside the spatial field, or in
  a component of its own. **An archetype without a key lives in realm 0**, always: no key column, no cost, and it cannot be moved elsewhere.
- **Moving between realms** is `Transaction.Teleport(id, spatialComp, realm, position)`: applied at the next tick fence, the entity keeps its
  `EntityId` and lands in the destination realm's grid. A realm change is always a cell crossing, whatever the hysteresis band.
- **Queries answer in one realm**: `EcsQuery.InRealm(realm)` for the fluent spatial predicates, `dbe.ClusterSpatialQuery<T>(realm)` for the cluster
  broadphase. Without a realm they query realm 0. Entities at identical coordinates in another realm are never returned. `InRealm` scopes a
  *spatial* predicate: on a query without one it throws rather than answering every realm.
- **One realm's clusters, walked alone.** `accessor.GetClusterEnumerator(realm)` (on `ArchetypeAccessor<T>`, or `GetClusterEnumerator<T>(realm)` on a
  transaction or `ctx.Accessor`) walks only that realm's clusters of the archetype — O(clusters in that realm), not a filter over every realm's — and
  `ClusterCountIn(realm)` counts them. Each realm keeps its own cluster list, joined at claim and left at drain.
- **Systems see every runnable realm.** A QuerySystem walks the clusters of every realm its policy lets run this tick; `ClusterRef.Realm` says which
  realm a cluster is in, so a system scopes its own spatial queries by it. A system that works on one realm walks that realm's list instead.
- **Each realm has a policy when nobody watches it.** `WhenUnobserved = Simulate` keeps it running, at full rate or at `UnobservedTickDivisor`
  (each cluster once every N runs, integrated over N ticks of delta time); `Sleep` makes it dormant after `SleepAfterTicks` unobserved ticks — no
  systems, no spatial maintenance. A realm is observed while a client session is in it, or while the application pins it with `Realms.Observe`.
  An entity teleported in, or `Realms.Wake`, wakes it.
- **Per-realm memory is sized from the realm's own config.** A one-cell realm costs about 1.2 KB of spatial structures; its state for an archetype
  is created only when that archetype first has a cluster there.

## 💻 Usage

```csharp
// Before InitializeArchetypes: realm 0 is the planet, realms 1..N the rest.
dbe.ConfigureRealms(64);
dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(-8192, -8192), new Vector2(8192, 8192), 256));   // realm 0

dbe.Realms.Register(new RealmId(1), new RealmConfig                // a cantina: one 64 m cell, asleep when empty
{
    Grid = SpatialGridConfig.Flat(Vector2.Zero, new Vector2(64, 64), 64),
    WhenUnobserved = RealmUnobserved.Sleep, SleepAfterTicks = 500, UnobservedTickDivisor = 1,
    Parent = RealmId.Default,                                         // news of the planet reaches it (routing only)
});
dbe.Realms.Register(new RealmId(2), new RealmConfig                // space: a 3D volume, simulated at 1/4 rate when unwatched
{
    Grid = new SpatialGridConfig(new Vector3D(-8000, -8000, -8000), new Vector3D(8000, 8000, 8000), 500),
    WhenUnobserved = RealmUnobserved.Simulate, UnobservedTickDivisor = 4,
});
dbe.InitializeArchetypes();

// The key: a ushort beside the spatial field, or (to keep a 2D AABB at 16 bytes for the SIMD narrowphase) in a component of its own.
[Component("Game.PlayerRealm", 1)]
public struct PlayerRealm { [RealmKey] public ushort Value; }

// Spawn inside a realm, walk through a door, query one realm.
var id = tx.Spawn<Player>(Player.Bounds.Set(at), Player.Realm.Set(new PlayerRealm { Value = 0 }));
tx.Teleport(id, Player.Bounds, new RealmId(1), in doorway);        // lands in the cantina at the next fence

var nearby = tx.Query<Player>().WhereNearby<PlayerBounds>(x, y, 0, 30).InRealm(new RealmId(1)).Execute();

// Every player in the cantina, cluster by cluster — O(clusters there), whatever the planet holds.
using var players = tx.For<Player>();
foreach (var cluster in players.GetClusterEnumerator(new RealmId(1)))
{
    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1) { /* cluster.GetEntityId(slot), cluster.Get<T>(slot) … */ }
}

// Instanced content on a running engine.
dbe.Realms.Register(new RealmId(40), dungeonConfig);
using var pin = dbe.Realms.Observe(new RealmId(40));               // keep it active while the party is inside
// … later
dbe.Realms.DestroyContents(new RealmId(40), tx); tx.Commit();
dbe.Realms.Unregister(new RealmId(40));                            // removed at the first fence that finds it empty

// In a system: a divided realm's clusters are integrated over their divisor's worth of time.
var dt = ctx.Realms.DeltaTime(cluster.Realm);
```

Per realm at run time: `Realms.StateOf(id)` (`Active`, `Simulated`, `Dormant`, `Closing`), `Realms.Counts` (a histogram over every realm),
`ctx.Realms.IsRunnable / DivisorOf / TicksPerVisit`, and `GetSpatialGridOccupancy(realm)`. A system that must see every cluster every tick whatever
its realm's divisor declares `.RealmRate(RealmRate.Full)`.

## ⚠️ Guarantees & limits

- **Isolation.** A spatial query answers in exactly one realm (SQ-08); the narrowphase returns only that realm's entities (RM-04). A cluster holds
  entities of one realm.
- **Realm changes.** A realm change is a mandatory crossing (RM-03). An invalid key — an unregistered or closing realm, or one the archetype is
  incompatible with (an f32 archetype in a realm that needs f64 coordinates) — throws at `Spawn` / `Teleport`, in application code; a raw write of a
  bad key is reverted at the fence, never thrown there (RM-05).
- **Durability.** Every realm's identity (bounds, cell size, hysteresis) is written to the database's realm catalog at its first registration; on
  every later open a registration that differs from it is refused, a catalogued realm the application does not register is rebuilt from it (a
  generic opener such as the Workbench sees every realm), and a cluster naming a realm nobody knows refuses the open rather than being filed
  elsewhere (RLM-01). `ConfigureRealms` is never clamped (RLM-02). The rebuild checks every slot's realm (RM-06).
- **Per-realm walks.** A realm's cluster list holds exactly its clusters after every fence; a walk or count naming an unregistered realm throws; an
  unkeyed archetype is wholly in realm 0 (RM-07). Like the archetype-wide walk, the list is live: walk it from a system or outside the fence.
- **Policy.** Decided once per tick, before any dispatch (RLM-03); a dormant realm's clusters reach no QuerySystem (RLM-04); a divided realm's
  clusters run exactly once every N runs of a system (RLM-05). With every realm runnable, dispatch is exactly the single-realm path.
- **Lifecycle.** A realm is removed only once empty, and its id may be registered again only after a later open has proven it empty (RLM-06). Realm
  0 cannot be unregistered.
- **Limits.** A QuerySystem cannot yet be narrowed to one realm by its declaration (it walks every runnable realm; filter by `ClusterRef.Realm`, or
  walk `GetClusterEnumerator(realm)` from a non-parallel system). Tier assignment through `TickContext.SpatialGrid` is realm 0's grid only; other realms' cells keep their tiers. Realm ids are
  `ushort`s below `MaxRealms` (0xFFFF is "no realm"). `[RealmKey]` is one `ushort` per archetype, on a SingleVersion component, without `[Index]`.
  The per-archetype spatial telemetry event is one per archetype, not per realm.

## 🧪 Tests

`RealmClusterListTests`, `RealmTableTests`, `RealmRegistrationTests`, `RealmKeyTests`, `RealmKeyComponentTests`, `RealmArchetypeSpatialTests`, `CrossRealmMigrationTests`,
`RealmFenceTests`, `RealmRepairTests`, `RealmPolicyTests`, `RealmDivisorTests`, `RealmLifecycleTests`, `RealmCatalogTests`, `RealmCatalogCrashTests`,
`RealmReopenTests`, `RecoveryRealmTests`, `RealmFootprintTests`, `RealmRuntimeTests` (engine); the SWG demo's `RealmChecks` (planets, interiors,
space, dungeons).

## 🔗 Related

- [Sessions in Realms](../Subscriptions/realms.md) — replicating realms to clients · [Spatial Grid Configuration](spatial-grid-config.md) ·
  [Spatial Query API](spatial-query-api.md) · [Tiered Simulation Dispatch](tiered-simulation-dispatch.md)
- In depth: [Chapter 07 — Spatial, § Realms](../../in-depth-overview/07-spatial.md#8-realms)
