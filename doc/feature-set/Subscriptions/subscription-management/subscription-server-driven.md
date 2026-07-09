---
uid: feature-subscriptions-subscription-management-subscription-server-driven
title: 'Server-Driven Subscriptions (v1)'
description: 'Game code calls SetSubscriptions whenever game state changes; the runtime applies the transition next tick.'
---

# Server-Driven Subscriptions (v1)
> Game code calls `SetSubscriptions` whenever game state changes; the runtime applies the transition next tick.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

For v1, every subscription change originates on the server — there is no client request to validate or
trust. Game systems already know when a client's interest set should change (a zone transition, a party
join, a UI panel opening) so they are the natural place to declare it. This sub-feature is the direct, no
round-trip path: call `SetSubscriptions` with the new target list from any worker thread, and the runtime
takes care of diffing, sequencing, and delivering the transition.

## ⚙️ How it works (in brief)

`SetSubscriptions(client, params PublishedView[] views)` resolves the live connection from the `ClientContext`
and atomically swaps in the new desired list as the connection's pending subscription set. The Output phase
picks up the pending set on the next tick, computes the diff against the client's active subscriptions, and
applies it — no further action from the caller. Because the set is replaced via an atomic exchange, this can
be called safely from any system/worker thread, including the same client multiple times within one tick.

## 💻 Usage

```csharp
var pubA = runtime.PublishView("nearby_players", nearbyPlayersView);
var pubB = runtime.PublishView("world_objects", worldObjectsView);
var pubC = runtime.PublishView("my_inventory", myInventoryView);

// Initial subscription set, e.g. right after the client connects
runtime.SetSubscriptions(client, pubA, pubB, pubC);

// Later — zone transition: drop nearby_players/world_objects, add dungeon Views, keep inventory
var pubD = runtime.PublishView("dungeon_npcs", dungeonNpcsView);
var pubE = runtime.PublishView("loot", lootView);
runtime.SetSubscriptions(client, pubC, pubD, pubE);
// -> nearby_players, world_objects unsubscribed (events sent)
// -> dungeon_npcs, loot subscribed (incremental sync begins)
// -> my_inventory kept (no interruption, no resync)
```

## ⚠️ Guarantees & limits

- **Thread-safe, no locking required** — backed by `Interlocked.Exchange` on the connection's pending-set slot; callable from any game system or worker thread.
- **Last-call-wins per tick** — if `SetSubscriptions` is called more than once for the same client before the next Output phase, only the most recent call is applied; earlier calls in that window are discarded entirely (not merged).
- **No server-side validation hook** — v1 trusts the caller; any `PublishedView` handle the game code holds can be assigned to any client. There is no concept of a client being disallowed from a View.
- **One tick of latency** — a call made during tick N's simulation is applied in tick N's (or N+1's, depending on call timing relative to the Output phase) `TickDeltaMessage`, never synchronously.

## 🧪 Tests

- [SubscriptionTransitionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionTransitionTests.cs)
  — the diff-based transition `SetSubscriptions` relies on (subscribed/unsubscribed/kept sets)
- [SubscriptionStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs) —
  `ConcurrentSetSubscriptions_LastWriterWins_NoCorruption`: last-call-wins under concurrent calls from multiple
  worker threads

## 🔗 Related

- Parent feature: [Subscription Management (SetSubscriptions)](./README.md)
- Sibling: [Client-Initiated Subscriptions (v2)](./subscription-client-initiated.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Server-Driven (v1) -->
