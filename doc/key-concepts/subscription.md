---
uid: concept-subscription
title: 'Subscription'
description: 'Engine state replicated outward to remote clients — each session sees what lies around it — and their typed commands drained back into the tick. Built into the engine, pushed and explicit.'
---

# Subscription

> **In one line:** engine state replicated outward to remote clients, each [session](xref:concept-replication-session) seeing what lies
> around it, and their typed [commands](xref:concept-client-command) drained back into the [tick](xref:concept-tick).

When the consumer of engine state is remote — a game client, a browser, a bot, another process — **Subscriptions** carry it there and keep
it current. A client mirrors "the characters near my player" without querying, and the application writes no change detection, encoding
or networking.

It is one engine feature with four moving parts:

- **What is visible** — a [projection](xref:concept-projection) per archetype: which fields travel, quantized how, grouped how, and which
  are private to the entity's owner. Declared once, by attributes on the data or by a builder call.
- **Who sees what** — a [profile](xref:concept-replication-profile) per kind of viewer: the whole world, a sphere around a point or an
  entity, a footprint the client sends, plus optional per-tile counts. A [session](xref:concept-replication-session) is bound to one.
- **What changed** — **push replication**: after a system writes a replicated field it calls `Replicate(cluster, slot)`. The engine
  pushes spawns, destroys, moves and migrations on its own, compares each pushed entity's quantized values with what it last sent, and
  encodes a change **once**, whatever the number of sessions.
- **What comes back** — typed [commands](xref:concept-client-command) from clients, rate-limited and role-checked, delivered to systems
  in the next tick; and [events](xref:concept-replication-event) the server emits to the sessions they concern.

Replication runs as an engine track after the [tick fence](xref:concept-tick-fence) and publishes after the tick's durability flush, so
a client never sees a state the database could lose. Clients decode against a [catalog](xref:concept-replication-catalog) sent at
connection, never against C# layouts, so a browser is as much a client as a .NET one.

> ⚠️ **Not database replication.** Subscriptions send a *view of engine state* to clients. They are not a way to replicate a database to
> another server: Typhon has no multi-node replication or clustering.

## How it relates

- **[System](xref:concept-system)** — systems say what they changed (`Replicate`), read commands and emit events through
  [`TickContext.Subscriptions`](xref:concept-tick-context).
- **[Spatial index](xref:concept-spatial-index)** — a replicated archetype has a position, and a session's view is found through the
  spatial grid.
- **[Tick fence](xref:concept-tick-fence)** — the fence records the tick's pushes; replication computes after it and publishes after the
  flush.
- **[View](xref:concept-view)** — a view is an in-process delta stream; a subscription is its remote counterpart, built from projections
  rather than from a view.

## In the API

- [`TyphonRuntime.Subscriptions`](xref:Typhon.Engine.TyphonRuntime.Subscriptions) — the [`SubscriptionsRegistry`](xref:Typhon.Engine.SubscriptionsRegistry)
  you declare everything on, before `Start()`.
- [`SubscriptionsCommands`](xref:Typhon.Engine.SubscriptionsCommands) — what a system reaches as `ctx.Subscriptions`: `Replicate`,
  `Commands<T>()`, `Emit`, `SessionEvents`, `Session(…)`, `TryResolve`.
- [`SubscriptionsOptions`](xref:Typhon.Engine.SubscriptionsOptions) — capacity, budgets and the required replication cell.

## Learn & use

- **Narrative:** [Guide ch.7 — serving remote clients](xref:guide-subscriptions)
- **Feature detail:** [Subscriptions](xref:feature-subscriptions-index)
- **Internals:** [Technical overview 15 — Subscriptions](xref:overview-subscriptions)
- **The algorithm:** [who receives what, and why it scales](xref:overview-subscriptions#3-the-cell-algorithm-who-receives-what)
