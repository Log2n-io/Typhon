---
uid: concept-subscription
title: 'Subscription'
description: 'Engine state replicated outward to remote clients, each seeing what lies around it, with typed commands coming back.'
---

# Subscription

> **In one line:** engine state replicated outward to remote clients, and their typed commands drained back into the tick.

When the consumer of engine state is remote — a connected game client, a browser, another process — a subscription is what carries it
there and keeps it current, so a client can mirror "the characters near my player" without re-querying and without the application
writing encoding or networking.

The application declares what each archetype exposes and which profile a session follows — the whole world, or a radius around a point
the application places — and it says, after each write it wants seen, that the entity changed (`Replicate`). The engine does the rest:
it compares and encodes each changed entity once, gives every session the changes around it, fills a new view cell by cell, catches a
lagging session up from a short log, and drains typed commands from clients into the next tick, where ordinary systems validate and apply
them. Clients decode against a catalog rather than against C# type layouts, so a browser is as much a client as a native one.

## How it relates

- **[System](xref:concept-system)** — systems say what they changed; replication runs on its own engine track: compute after the
  [tick](xref:concept-tick) fence, publish after the flush.
- **[Query](xref:concept-query)** — a profile is a standing spatial question, answered per session from geometry rather than re-queried.
- **[View](xref:concept-view)** — a later phase publishes shared views to subscribed clients; today replication reads declared projections.

## Learn & use

- **Feature detail:** [subscriptions](xref:feature-subscriptions-index)
