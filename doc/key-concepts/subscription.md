---
uid: concept-subscription
title: 'Subscription'
description: 'Engine state replicated outward to remote clients, with per-client interest and typed commands coming back. Under construction.'
---

# Subscription

> **In one line:** engine state replicated outward to remote clients, and their typed commands drained back into the tick.

When the consumer of engine state is remote — a connected game client, a browser, another process — a subscription is what carries it there and keeps it current, so a client can mirror "the characters near my camera" without re-querying and without the application writing change detection, encoding or networking.

The application declares what each archetype exposes and who sees what. The engine does the rest: per-client interest, change detection, quantized encoding, sessions, backpressure, and an inbound path for typed commands that enter the tick and are validated by ordinary systems. Clients decode against a catalog rather than against C# type layouts, so renaming a type is not a wire break, and a browser is as much a client as a native one.

> 🚧 **Under construction.** The foundation is in the engine — replication state sized by the watched set, a track between the tick fence and the flush, network identities, per-session ingress rings — but there is no public API yet. Track [#205](https://github.com/Log2n-io/Typhon/issues/205).

## How it relates

- **[View](xref:concept-view)** — replication reads the same delta machinery, but from declared projections rather than from a published view.
- **[Query](xref:concept-query)** — the underlying question a projection answers.
- **[System](xref:concept-system)** — replication runs on its own engine track: compute after the [tick](xref:concept-tick) fence, publish after the flush.

## Learn & use

- **Feature detail:** [subscriptions](xref:feature-subscriptions-index)
