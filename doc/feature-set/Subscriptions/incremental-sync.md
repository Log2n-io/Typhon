---
uid: feature-subscriptions-incremental-sync
title: 'Incremental Sync'
description: 'New subscriptions to large Views sync in tick-sized batches instead of one giant first delta.'
---

# Incremental Sync
> New subscriptions to large Views sync in tick-sized batches instead of one giant first delta.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

When a client subscribes to a View that already holds thousands of entities, sending the whole entity set
in a single `TickDelta` would blow the per-tick output budget and stall every other client behind one large
serialization. Incremental sync spreads that initial snapshot across as many ticks as it takes, so a new
subscription never causes a latency spike — for everyone else, or for the subscribing client's own
steady-state deltas once sync finishes.

## ⚙️ How it works (in brief)

On subscribe, the runtime snapshots the View's current entity set and walks it forward a fixed number of
entities per tick, sending each chunk as a normal `Added` batch under a `Syncing` state. No `Modified` or
`Removed` deltas are sent for that View while sync is in progress — entities that change mid-sync simply pick
up their current values whenever their turn in the snapshot comes. Once the last batch is sent, the runtime
flips the View to `Active` for that client and emits a `SyncComplete` event; normal `Added`/`Modified`/`Removed`
deltas resume from the next tick. Small Views that fit in one batch sync and complete in the same tick. The
same mechanism is reused for `Resync` recovery after a backpressure overflow (see
[Backpressure & Resync Recovery](./backpressure-resync.md)).

## 💻 Usage

Incremental sync requires no special call — it activates automatically whenever `SetSubscriptions` adds a
client to a View:

```csharp
var dungeonNpcs = runtime.PublishView("dungeon_npcs", dungeonNpcsView);
runtime.SetSubscriptions(client, dungeonNpcs);   // sync begins next Output phase

// Client-side: only act on the View once it reports SyncComplete
conn.OnTickDelta += tickDelta =>
{
    foreach (var evt in tickDelta.Events)
    {
        if (evt.Type == EventType.SyncComplete)
        {
            MarkViewReady(evt.ViewId);   // safe to assume the local cache mirrors the server now
        }
    }

    foreach (var viewDelta in tickDelta.Views)
    {
        foreach (var added in viewDelta.Added)
        {
            SpawnOrUpdateClientEntity(added);   // applies to both sync batches and steady-state Added
        }
    }
};
```

| Option (`SubscriptionServerOptions`) | Default | Effect |
|---|---|---|
| `SyncBatchSize` | 200 | Max entities sent per `Added` batch per client per View per tick while syncing |

## ⚠️ Guarantees & limits

- **No interleaving** — a client never receives `Modified`/`Removed` deltas for a View while it is `Syncing`;
  only `Added` batches and the terminal `SyncComplete` event.
- **Consistent terminal state, not consistent snapshot** — the snapshot is captured once at subscribe time, but
  individual entities are read (and their current data sent) lazily as their batch comes up, so an entity
  changed mid-sync arrives with whatever state it has when its turn comes, not the state at subscribe time.
- **Fixed batch size, not adaptive** — `SyncBatchSize` is a flat per-tick cap; it does not currently scale down
  automatically under tick overload (see [Subscription Cost & Overload Integration](../../../claude/design/Subscriptions/05-subscriptions.md#subscription-cost--overload-integration)).
  Tune it down if large syncs are pushing `OutputPhaseMs` over budget.
- **Per-client, per-View state** — sync progress is tracked independently for each (client, View) pair; one
  client mid-sync on a View does not block or slow other clients' deltas on the same View.
- **Entity lifetime during sync is not guarded** — the sync snapshot is a fixed list of entity IDs captured at
  subscribe time; an entity destroyed before its batch comes up is not specially handled, unlike the
  steady-state delta path which only ever reports entities still present in the View.
- **Memory cost is bounded by subscriber count, not View size growth** — the sync snapshot is a flat array of
  entity IDs (not full component data) held only for the duration of that client's sync, and freed as soon as
  the last batch ships.

## 🧪 Tests

- [ViewDeltaTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewDeltaTests.cs) —
  `SubscriptionFlow_EntitiesExistBeforeSubscribe_SyncCapturesThem`/`SubscriptionFlow_RefreshPopulatesView_ThenBeginSync_ThenBuildDelta_NoDoubleAdd`:
  `BeginSync` snapshot capture and Output-phase ordering (no double-Added)

## 🔗 Related

- Related feature: [Subscription Management](./subscription-management/README.md), [Backpressure & Resync Recovery](./backpressure-resync.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Incremental Sync -->
