---
uid: concept-replication-profile
title: 'Replication profile'
description: 'A named kind of view — one observer (World, Sphere or ClientRegion) over a set of archetypes, plus an optional Aggregate of per-tile counts — that sessions are bound to.'
---

# Replication profile

> **In one line:** a named kind of view a [session](xref:concept-replication-session) is bound to — **one observer** over a set of
> archetypes, answered from geometry every tick.

A profile is declared once, before the runtime starts, and shared by every session bound to it. It holds exactly **one entity observer**:

| Observer | A session holds… | For |
|---|---|---|
| **`World`** | every entity of the observed archetypes, delivered progressively | tools, viewer bots, small worlds |
| **`Sphere(r)`** | the entities within `r` of its centre — a point the application places, an entity, or the entity it controls | players |
| **`ClientRegion(maxEdge)`** | the entities inside a convex footprint the client sends (a camera's view), up to a near budget | god and strategy cameras |

beside which it may declare **one `Aggregate`**: per-tile, per-archetype counts refreshed at a bounded rate — a heat map or a far tier,
never entity records.

**What a session holds is geometry, not a list.** Nothing is stored per (session, entity): a Sphere session holds an entity when the
entity's last sent position lies within the radius of the session's anchor *and* the replication cell it is in has been delivered to the
session. A new view is filled cell by cell, nearest first, under a per-frame enter budget; a moving session receives the entities that came
into range and is told about those that left.

The Sphere has refinements, all declared on the profile: a **leave radius** (`Sphere(192, leave: 208)`) so an entity jittering on the
boundary does not flap in and out; a **run-time range** (`max:`) for `SetRadius`; **distance bands** that send far entities' updates less
often; and a **cadence** (`Every(2)`) that serves the profile one tick in two or four. A session's outbound **byte budget** lowers its
detail level before it ever drops state.

## How it relates

- **[Replication session](xref:concept-replication-session)** — bound to a profile by `Session(id).Profile(name)`.
- **[Projection](xref:concept-projection)** — the profile names archetypes (`.Of<T>()`); what of each travels is the projection's.
- **[Spatial index](xref:concept-spatial-index)** — delivery and sweeps query the spatial grid; the replication cell side is declared
  separately (`SubscriptionsOptions.ReplicationCellM`).

## In the API

- [`ProfileBuilder`](xref:Typhon.Engine.ProfileBuilder) — `World()`, `Sphere(…)`, `ClientRegion(…)`, `Aggregate(…)`, `Every(…)`,
  `Detection(…)`.
- [`ObserverBuilder`](xref:Typhon.Engine.ObserverBuilder) — `Of<T>()`, `Bind`, `At`, `AroundControlled`, `Near`, `Bands`.

## Learn & use

- **Narrative:** [Guide ch.7 §2 — profiles](xref:guide-subscriptions)
- **Feature detail:** [Profiles & observers](xref:feature-subscriptions-profiles-observers)
- **Internals:** [Technical overview 15 § 3 — the cell algorithm, and why it scales](../in-depth-overview/15-subscriptions.md#3-the-cell-algorithm-who-receives-what)
