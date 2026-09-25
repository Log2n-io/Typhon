---
uid: concept-replication-event
title: 'Replication event'
description: 'A one-off fact the server tells clients — an attack, an explosion, a chat line — routed to the sessions it concerns, encoded once, best effort.'
---

# Replication event

> **In one line:** a one-off fact the server tells clients — an attack, an explosion, a notice — routed to the
> [sessions](xref:concept-replication-session) it concerns and delivered with their next frame.

State says what *is*; an event says what *happened*. A system emits one with `ctx.Subscriptions.Emit(in evt)`, and its declaration
decides who hears it:

| Routing | Reaches |
|---|---|
| `RouteNear(point, radius)` | sessions whose view contains the point the event carries |
| `RouteToKnown(entity…)` | sessions that already hold any of the entities it names |
| `RouteToOwner(entity)` | the session controlling that entity |
| `RouteToSession()` | the one session the emitter names (`EmitTo`) |
| `Broadcast()` | every session |

An event is encoded once, after the tick's projection, and the same bytes reach every session it matches; an `EntityId` field travels as
the netId the client knows (0 when it has none). A session that misses frames receives the events of the ticks it missed with its next
one, from an event log eight ticks deep.

**Events are best effort, by design.** A session more than eight ticks behind loses the events of the ticks past the log, and its next
frame starts with the built-in `EventsLost` event counting them. Anything that must never be lost is **state**: a death is a mode change the
client learns from its next record, not only a message.

## How it relates

- **[Client command](xref:concept-client-command)** — the other direction: intents from clients.
- **[Projection](xref:concept-projection)** — the state that events complement, never replace.
- **[System](xref:concept-system)** — systems emit events in the tick; the runtime's in-process event queues are a different thing
  (signals between systems).

## In the API

- [`SubscriptionsRegistry.Event<T>(…)`](xref:Typhon.Engine.SubscriptionsRegistry) and [`EventBuilder<T>`](xref:Typhon.Engine.EventBuilder`1).
- [`SubscriptionsCommands.Emit<T>`](xref:Typhon.Engine.SubscriptionsCommands) / `EmitTo<T>`.

## Learn & use

- **Narrative:** [Guide ch.7 §6 — events](xref:guide-subscriptions)
- **Feature detail:** [Events](xref:feature-subscriptions-events)
