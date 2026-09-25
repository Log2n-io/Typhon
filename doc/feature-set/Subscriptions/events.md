---
uid: feature-subscriptions-events
title: 'Events'
description: 'One-off facts routed to the sessions they concern — near a point, to the holders of an entity, to an owner, to one session, or to all — encoded once, best effort with a loss count.'
---

# Events
> What happened, told to the sessions it concerns, with the same bytes for all of them.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Some things are facts, not state: an attack landed, a door exploded, a trade completed. Clients need them to play an effect or show a
message, and only the clients near the fact — or holding the entity it names — should pay for them.

## ⚙️ How it works (in brief)

- **Declaration**: `subs.Event<T>(e => e.Route…)` over an `unmanaged` struct. Every public field travels; an `EntityId` field travels as the
  netId the receiving client knows (0 when it has none). `Field`/`Entity`/`Ignore` override; attributes can declare codecs.
- **Emission**: `ctx.Subscriptions.Emit(in evt)` from any system; `EmitTo(session, in evt)` for `RouteToSession`. Each worker records into its
  own buffer; a tick's events travel in worker order, then call order.
- **Encode once**, after the tick's projection; each receiving session's `EVENTS` block copies the same bytes.
- **Routing**, per event, never per (event, session): `RouteNear(point, radius)` files events by cell and each session searches only its own
  cells; `RouteToKnown(entities)` tests the sessions' geometry for the named entities; `RouteToOwner` and `RouteToSession` look up sorted
  lists; `Broadcast` reaches everyone.
- **Skips**: an event log eight ticks deep; a session further behind receives the built-in `EventsLost` event with the count of what it
  missed.

## 💻 Usage

```csharp
subs.Event<Hit>(e => e
    .RouteToKnown(h => h.Target, h => h.Attacker)
    .Field(h => h.Damage, Codec.U16));

ctx.Subscriptions.Emit(new Hit { Attacker = shooter, Target = victim, Damage = 12 });
```

## ⚠️ Guarantees & limits

- **Best effort by design**: past the log, events are lost and counted, never retransmitted. Anything that must not be lost is state.
- A session bound to no profile hears only broadcasts and its own `EmitTo`.
- An event whose value its codec cannot carry is dropped and counted — the tick path never throws.
- **Reliable events are not built.**

## 🧪 Tests

- [EventDeliveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/EventDeliveryTests.cs) — each event reaches exactly the sessions its route names, across skips

## 🔗 Related

- Concept: [Replication event](xref:concept-replication-event)
- Related feature: [Typed Event Queues](../Runtime/typed-event-queues.md) — in-process signals between systems, a different mechanism
