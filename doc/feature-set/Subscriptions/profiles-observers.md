---
uid: feature-subscriptions-profiles-observers
title: 'Profiles & Observers'
description: 'Named kinds of view — World, Sphere (leave band, run-time radius, distance bands, bound or controlled centre), ClientRegion with a near budget, and an Aggregate tier of per-tile counts.'
---

# Profiles & Observers
> Who sees what: one entity observer per profile, answered from geometry every tick, plus an optional tier of counts.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Every client wants a different slice of the world — the player the area around it, a god camera what its lens frames, a tool everything —
and the slice moves every tick. Profiles declare the shapes once; the engine keeps each session's slice exact without a per-session list of
what it holds.

## ⚙️ How it works (in brief)

- **One entity observer per profile**: `World()`, `Sphere(r, leave:, max:)` or `ClientRegion(maxEdgeM)`, over the archetypes named with
  `.Of<T>()`. Beside it, at most one `Aggregate(tileM, rateHz, radiusM)`: per-tile, per-archetype counts in `AGG` blocks, refreshed at a
  bounded rate, never entity records.
- **Held = geometry.** A Sphere session holds an entity when the entity's visibility position is within the radius of the session's anchor
  and its replication cell has been delivered; nothing is stored per (session, entity). The anchor moves only when the viewpoint drifts past
  a small slack, so a still session costs nothing.
- **Delivery.** A new view is filled cell by cell, nearest first, under `EnterBudgetPerFrame`; `VIEW_COMPLETE` is flagged when it is full.
  A World session is filled behind one cursor in grid order. A jump of more than a cell is a teleport and resets the view.
- **Centre of a Sphere**: `Place(session, point)` from a system every tick; or `.At(point)`, `.Bind(entity)`, `.AroundControlled()` — the
  bound or controlled entity's post-fence position, with no per-tick call.
- **ClientRegion**: the client sends a convex footprint (3–16 vertices on a flat world, 4–16 in 3D) through the built-in `ClientRegion`
  command (≤ 5 Hz); the engine takes the hull, clamps it to `maxEdgeM`, and holds the entities inside it; `.Near(budget)` caps the entities
  held, whole cells nearest the centroid first, with a deadband.
- **Visibility tags**: an archetype's projection may declare a `uint` mask field (`.Visibility(Comp, c => c.Mask)`); 0 is public. A session
  sees a tagged entity when its own mask (`SessionRequest.SetVisibility`, 0 at admission) shares a bit with it — on every observer and every
  group, so a GM-only object or a stealthed player is never sent to a session that may not see it. A mask change is a leave or an enter in
  the next frame; a session's own mask change resets its view.
- **Refinements**: a **leave radius** (hysteresis — an entity pacing on the boundary never flaps); a **run-time range**
  (`max:` + `ctx.Subscriptions.SetRadius`, no reset); **distance bands** (`.Bands(b => b.Every(2, beyond: 0.5).Every(4, beyond: 0.75))`:
  far updates every N ticks); **cadence** (`.Every(2 | 4)`: serve the profile one tick in N, replaying missed ticks).

## 💻 Usage

```csharp
subs.Profile("player", p => p
    .Sphere(192, leave: 208, max: 1500)
    .AroundControlled()
    .Bands(b => b.Every(2, beyond: 0.5))
    .Of<Player>().Of<Creature>().Of<CityNpc>());

subs.Profile("god", p =>
{
    p.ClientRegion(maxEdgeM: 3000).Near(10_000).Of<Creature>().Of<Player>();
    p.Aggregate(tileM: 256, rateHz: 1).Of<Creature>().Of<Player>();
});

subs.Profile("tool", p => p.World().Of<Creature>().Of<Player>());

// GM-only objects: bit 1 hidden from everyone but GMs.
subs.Archetype<Marker>(a => a.Position(Marker.Bounds).Visibility(Marker.Tag, t => t.Mask));
ctx.Subscriptions.Session(session).SetVisibility(GmBit);   // at a GM's admission
```

| Option | Default | Effect |
|---|---|---|
| `SubscriptionsOptions.ReplicationCellM` | **required** | The replication cell side; a session's window is `2⌈R/c⌉ + 5` cells per axis. Start with ≈ R/3 (an 11 × 11 window) |
| `SubscriptionsOptions.EnterBudgetPerFrame` | 500 | Enter records per frame |
| `ProfileBuilder.Every(n)` | 1 | Serve one tick in 1, 2 or 4 |
| `ProfileBuilder.Detection(…)` | `Explicit` | See [Push replication](push-replication.md) |

## ⚠️ Guarantees & limits

- **A session holds exactly what its geometry names**; one never placed holds nothing — not the area around the origin (SUB-16).
- **A session holds, is sent and names only what its mask sees** (SUB-34): a hidden entity never enters, an event routed to the sessions that
  know it skips the others, and a command naming it is refused. Not filtered: `AGG` counts (a density counts hidden entities too — keep them out
  of the aggregated archetypes if the count itself is secret) and an event's entity fields (a hidden one travels as a netId the client cannot
  resolve, like any entity beyond its view). A session's own controlled entity is not exempt from its mask in `ENTITIES`; `SELF` still names it.
- **Worlds are flat or volumetric**: a spatial grid one cell deep is served in the plane; a deeper one in 3D (2D archetypes then live on
  z = 0).
- A Sphere window is bounded (at most 15 cells per axis flat, 13 in 3D; a `ClientRegion` window at most 2 809 cells); `Start` refuses a
  profile past it, naming the cell side that would fit.
- **Refused at `Start`**: two entity observers in one profile, an `Aggregate` alone, a near budget on anything but a `ClientRegion`, a
  Sphere centred two ways, `Far(…)` (use `Aggregate`).

## 🧪 Tests

- [SphereObserverTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SphereObserverTests.cs) — a session holds exactly its disc
- [PushVisibilityTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushVisibilityTests.cs) — a mask flip, a session mask change, a hidden orphan, events and commands; the tagged oracles are in [PushOracleTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushOracleTests.cs) and [PushGroupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushGroupTests.cs)
- [PushRegionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushRegionTests.cs) · [PushAggregateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushAggregateTests.cs) · [PushLodLevelTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushLodLevelTests.cs) · [PushOracle3DTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/Oracle/PushOracle3DTests.cs)

## 🔗 Related

- Concept: [Replication profile](xref:concept-replication-profile)
- Internals: [the cell algorithm, and why it scales](../../in-depth-overview/15-subscriptions.md#3-the-cell-algorithm-who-receives-what)
- [Sessions & admission](sessions-admission.md) — binding a session to a profile
