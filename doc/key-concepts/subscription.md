---
uid: concept-subscription
title: 'Subscription'
description: 'A subscription publishes a view to remote consumers and streams its Added/Removed/Modified deltas over a transport, with per-subscription priority — the same view+delta machinery wired to the wire.'
---

# Subscription

> **In one line:** a [view](xref:concept-view) published to *remote* consumers, streaming its deltas over a transport.

When the consumer of a view is remote — a connected client, another process — a subscription publishes the view and pushes its `Added`/`Removed`/`Modified` deltas out as it refreshes, with per-subscription priority and overload throttling. It's the same view + delta machinery from [views](xref:concept-view), wired to a transport, so a client can mirror "the units near my camera" without re-querying.

You register a `PublishedView` (managed by a `PublishedViewRegistry`). Reach for it when you're building a server, not a single-process simulation.

## How it relates

- **[View](xref:concept-view)** — a subscription is a view published outward.
- **[Query](xref:concept-query)** — the underlying question the view answers.
- **[System](xref:concept-system)** — subscription output is driven at tick end, alongside the [tick](xref:concept-tick) lifecycle.

## In the API

- [`PublishedView`](xref:Typhon.Engine.PublishedView) — a view published to subscribers.
- [`PublishedViewRegistry`](xref:Typhon.Engine.PublishedViewRegistry) — the registry that manages published views.

## Learn & use

- **Narrative:** [Guide ch.4 §4 — subscriptions](xref:guide-querying)
- **Feature detail:** [subscriptions](xref:feature-subscriptions-index) · [published views](xref:feature-subscriptions-published-views-index) · [incremental sync](xref:feature-subscriptions-incremental-sync)
