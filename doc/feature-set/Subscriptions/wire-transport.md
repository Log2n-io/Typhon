---
uid: feature-subscriptions-wire-transport
title: 'TCP Transport & Wire Format'
description: 'One length-prefixed, MemoryPack-serialized message per client per tick over TCP_NODELAY.'
---

# TCP Transport & Wire Format
> One length-prefixed, MemoryPack-serialized message per client per tick over TCP_NODELAY.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Once the server knows what changed for a client, it still needs to get those bytes across the wire — reliably, in order, framed so the client can tell where one tick's message ends and the next begins, and cheaply enough that talking to hundreds of clients doesn't blow the tick budget. Hand-rolling this (socket setup, message framing, serialization, per-client buffering, backpressure) is exactly the kind of plumbing that has nothing to do with game logic. Typhon owns all of it: one call to publish a View and set a client's subscriptions is enough — the bytes arrive on the other end as ready-to-apply delta objects.

## ⚙️ How it works (in brief)

Each connected client gets a dedicated TCP socket with `TCP_NODELAY` enabled — deltas are already batched per tick, so Nagle's algorithm would only add latency for no batching benefit. Every tick, the server builds at most one `TickDeltaMessage` per client, serializes it with MemoryPack (a zero-copy, source-generated serializer — effectively a `memcpy` for the unmanaged component data Typhon stores), prefixes it with a 4-byte little-endian length, and queues it on that client's send buffer. A dedicated I/O thread drains send buffers asynchronously so a slow client never blocks the tick. When many clients share the same View data, the server serializes the common payload once and copies the resulting frame to every matching client's buffer instead of re-serializing per client.

## 💻 Usage

The wire transport has no separate API — it's the delivery mechanism behind `PublishView` / `SetSubscriptions` (see [shared-views](./published-views/shared-views.md) and [subscription-server-driven](./subscription-management/subscription-server-driven.md)). Configure it via `SubscriptionServerOptions` when setting up the runtime:

```csharp
var options = new SubscriptionServerOptions
{
    Port = 9000,
    SendBufferCapacity = 262_144,       // per-client send buffer, bytes
    BackpressureWarningThreshold = 0.75f,
};

// runtime wiring (DI/Hosting extension) takes SubscriptionServerOptions and starts
// the listener + I/O flush threads; clients connect with the reference C# SDK:
var conn = TyphonClient.Connect("tcp://server:9000");
conn.OnTickDelta += tickDelta =>
{
    foreach (var viewDelta in tickDelta.Views)
    {
        foreach (var added in viewDelta.Added) SpawnClientEntity(added);
        foreach (var modified in viewDelta.Modified) PatchClientEntity(modified);
        foreach (var removed in viewDelta.Removed) DespawnClientEntity(removed);
    }
};
```

| Option | Default | Effect |
|--------|---------|--------|
| `Port` | 9000 | TCP listen port (dual-stack IPv6/IPv4) |
| `SendBufferCapacity` | 262144 (256 KB) | Per-client send buffer; full buffer triggers a dropped tick + resync |
| `BackpressureWarningThreshold` | 0.75 | Fill ratio at which a warning is logged |
| `MaxClients` | 0 (unlimited) | Connections beyond this are refused at accept time |

## ⚠️ Guarantees & limits

- **One TCP send per client per tick** — all View deltas and subscription events for a tick are bundled into a single `TickDeltaMessage`, so the client never observes a half-applied tick.
- **Reliable, ordered delivery** — TCP guarantees entity lifecycle events (add/remove) arrive in order and are never dropped silently; this is why the transport is TCP, not UDP.
- **Length-prefixed framing** — `[4-byte LE length][MemoryPack payload]`; the client reads the prefix to know exactly how many bytes to buffer before decoding.
- **Serialize-once for shared state** — steady-state clients with no pending events, no per-client Views, and an Active subscription set matching exactly the shared Views computed that tick reuse one pre-built frame (one `MemoryPackSerializer.Serialize` call, N buffer writes). Clients with events, in-progress sync, or per-client Views fall back to individual serialization.
- **I/O is off the tick's critical path** — the timer thread only enqueues bytes into per-client buffers; an `ManualResetEventSlim` signal wakes a separate I/O thread that performs the blocking `Socket.Send` calls.
- **Backpressure, not unbounded growth** — if a client's send buffer is too full to hold the next frame, that tick's delta is dropped and the client is flagged for resync (full snapshot) instead of growing memory or stalling the server (see [Backpressure & Recovery](../../../claude/design/Subscriptions/05-subscriptions.md#backpressure--recovery)).
- **No built-in transport security or auth** — v1 treats an established connection as trusted; TLS, auth, and permissions are out of scope for this version.
- **C#-only wire format today** — MemoryPack is a .NET serializer; cross-language client SDKs would need either a MemoryPack-compatible reader or a future pluggable serializer.

## 🧪 Tests

- [ProtocolTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/ProtocolTests.cs) —
  `TickDeltaMessage`/`ViewDeltaMessage` MemoryPack round-trips, including the full multi-View Added/Modified/Removed
  message shape
- [FrameReaderTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Client.Tests/FrameReaderTests.cs) — length-prefixed frame reading:
  fragmentation, buffer growth, zero/negative/excessive length rejection
- [SendBufferTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SendBufferTests.cs) — the per-client
  ring buffer the I/O thread drains: wraparound, capacity, fill percentage

## 🔗 Related

- Related feature: [Shared Views](./published-views/shared-views.md), [Server-driven subscriptions](./subscription-management/subscription-server-driven.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Transport, Message Format, Output Phase Threading, Backpressure & Recovery sections -->
