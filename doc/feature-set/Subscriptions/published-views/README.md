---
uid: feature-subscriptions-published-views-index
title: 'Published Views'
description: 'Register a View as a subscribable target — one shared instance or one per client — and get a handle clients can subscribe to.'
---

# Published Views
> Register a View as a subscribable target — one shared instance or one per client — and get a handle clients can subscribe to.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

Streaming live state to connected clients needs a server-side notion of "what is subscribable" that's
independent of any one client's connection — a name clients reference, a priority for overload behavior, and a
subscriber count for diagnostics. Without it, every feature wiring up client sync would invent its own
ad-hoc registry of "queries clients can ask for." `PublishedView` is that registry entry: it wraps a query
result (a `ViewBase`) with the metadata the subscription system needs to deliver deltas, and it is the only
currency `SetSubscriptions` accepts — callers hand it `PublishedView` handles, never raw Views.

## ⚙️ How it works (in brief)

`TyphonRuntime.PublishView(...)` registers a View under a unique name and returns a `PublishedView` handle.
Two registration modes exist, picked by which overload you call: pass a `ViewBase` instance directly for a
**shared** View (one instance, all subscribers see the same data), or pass a `Func<ClientContext, ViewBase>`
factory for a **per-client** View (a fresh instance created for each subscriber). Every `PublishedView` carries
a `Name`, a `Priority` (for overload throttling), an `IsShared` flag, and a live `SubscriberCount`. The
`PublishedViewRegistry`, reachable via `runtime.PublishedViews`, holds every published View for lookup and
diagnostics. Registration itself does not start pushing data — a client only receives deltas for a View once
it is included in a `SetSubscriptions` call.

## Sub-features

| Sub-feature | Use it for |
|-------------|-----------|
| [Shared (world-state) Views](./shared-views.md) | World objects, NPCs, terrain — one delta computed per tick, fanned out to every subscriber |
| [Per-client View factories](./per-client-views.md) | Inventory, quest state, anything parameterized by which client is asking |

## ⚠️ Guarantees & limits

- **Names are unique and permanent** — `PublishView` throws `ArgumentException` if the name is already
  registered; there is no unpublish/rename.
- **A View can only be published once** — `RegisterShared` throws `InvalidOperationException` on a View
  that's already published, and on one already wired as a system input (see Published/System-Input View
  Separation). Use a dedicated View instance per published name.
- **Publishing is setup-time, not per-tick** — registration takes a lock and rebuilds a snapshot array;
  it is meant to happen during startup/configuration, not on a hot path.
- `PublishedView.PublishedId` (`ushort`) is assigned in registration order and is stable for the process
  lifetime; it is not persisted across restarts.
- `SubscriberCount` reflects only clients currently in `Active`/`Syncing` subscription state — it changes as
  `SetSubscriptions` transitions are applied during the Output phase, not the instant the call is made.

## 🧪 Tests

- [ViewSeparationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewSeparationTests.cs) — name
  uniqueness (`ArgumentException`), single-publish rule (`InvalidOperationException`), the `IsPublished` flag, and
  that two separate View instances over the same query don't conflict

## 🔗 Related

- Sibling: [Published/System-Input View Separation](../published-view-isolation.md)
- Sibling: [Persistent Views](../../Querying/persistent-views.md) — the underlying `ViewBase` query mechanism this feature publishes over the wire
- Sub-features: [Shared (world-state) Views](./shared-views.md), [Per-client View factories](./per-client-views.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Public Surface, View Types for Subscriptions -->
<!-- Deep dive: claude/overview/13-runtime.md — Subscription Server -->
