---
uid: feature-subscriptions-backpressure-resync
title: 'Backpressure & Resync Recovery'
description: 'A full client send buffer drops one tick''s delta and triggers an automatic full-state resync — never an unbounded queue.'
---

# Backpressure & Resync Recovery
> A full client send buffer drops one tick's delta and triggers an automatic full-state resync — never an unbounded queue.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A client's network link can momentarily drain slower than the server produces deltas (a lag spike, a busy client thread, a saturated link). Without a bound, the server would have to choose between growing per-client memory without limit or blocking the tick on a slow socket — either one threatens the engine's real-time budget. Typhon instead caps per-client outbound memory, sacrifices one tick's delta for a stuck client, and recovers the client's state automatically — the application never has to detect or handle desync itself.

## ⚙️ How it works (in brief)

Each client has a fixed-capacity send buffer. As it fills, the server logs a warning past a configurable threshold but keeps enqueuing normally. If a tick's serialized `TickDeltaMessage` doesn't fit at all, that tick's delta is dropped for the client and the client is flagged for resync — no partial or torn writes. On the next tick, instead of an incremental delta, the affected View(s) send a `Resync` event followed by a full entity snapshot (batched across ticks via the same incremental-sync machinery as a fresh subscription, if the View is large). The client is expected to discard its local cache for that View and rebuild it from the snapshot.

## 💻 Usage

There's no resync-specific API — it's automatic backend behavior driven by `SubscriptionServerOptions` and observed via telemetry and the `Resync` event your client already handles:

```csharp
var options = new SubscriptionServerOptions
{
    SendBufferCapacity = 262_144,          // per-client send buffer, bytes
    BackpressureWarningThreshold = 0.75f,  // fill ratio that triggers a log warning
};

// Client-side: Resync is just another EventType — handle it like a (re)subscribe.
conn.OnTickDelta += tickDelta =>
{
    foreach (var evt in tickDelta.Events)
    {
        if (evt.Type == EventType.Resync) DiscardLocalCache(evt.ViewId);
    }

    foreach (var viewDelta in tickDelta.Views)
    {
        foreach (var added in viewDelta.Added) SpawnClientEntity(added); // resync snapshot arrives here too
    }
};
```

| Option | Default | Effect |
|--------|---------|--------|
| `SendBufferCapacity` | 262144 (256 KB) | Per-client send buffer; overflow drops the tick and triggers resync |
| `BackpressureWarningThreshold` | 0.75 | Fill ratio (0.0–1.0) at which a warning is logged; enqueueing continues |
| `SyncBatchSize` | 200 | Entities per batch when a resync snapshot is large enough to need incremental delivery |

## ⚠️ Guarantees & limits

- **No unbounded growth** — a client that can't keep up never grows server memory past `SendBufferCapacity`; the cost of falling behind is a dropped tick, not a memory leak.
- **Self-healing** — resync is fully automatic: the affected View's data is sent again (as a snapshot, batched if large) with no application code needed to detect or request it.
- **Per-View granularity** — only the View(s) implicated by the overflow are resynced; other subscriptions on the same client continue with normal incremental deltas.
- **Warning threshold is observational only** — crossing `BackpressureWarningThreshold` logs but never drops data or alters behavior; only a full buffer triggers resync.
- **Telemetry-visible** — per-tick overflow counts are tracked so persistent backpressure on a client population is observable rather than silent.
- **One dropped tick per overflow, not a stall** — the server never blocks the tick loop waiting for a slow client to drain; the I/O thread sends asynchronously regardless.

## 🧪 Tests

- [SubscriptionStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs) —
  `Backpressure_SlowClient_TriggersResync`: a slow client with a small `SendBufferCapacity` overflows and gets
  flagged for resync
- [SendBufferTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SendBufferTests.cs) —
  `TryWrite_ExceedsCapacity_Fails`/`FillPercentage_CorrectAtVariousLevels`: the overflow/threshold mechanics
  underneath backpressure
- [DeltaProcessingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/DeltaProcessingTests.cs) —
  `Resync_ClearsCacheAndFiresCallback`: client-side handling of the `Resync` event

## 🔗 Related

- Related feature: [TCP Transport & Wire Format](./wire-transport.md), [Server-driven subscriptions](./subscription-management/subscription-server-driven.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Backpressure & Recovery -->
<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Incremental Sync -->
