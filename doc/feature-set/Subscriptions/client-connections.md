---
uid: feature-subscriptions-client-connections
title: 'Client Connections & Lifecycle'
description: 'Every accepted socket becomes a stable ClientContext handle, with disconnect cleanup handled automatically.'
---

# Client Connections & Lifecycle
> Every accepted socket becomes a stable `ClientContext` handle, with disconnect cleanup handled automatically.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

Subscription delivery needs a server-side identity for "this specific client" that survives across ticks —
something to hand to per-client View factories, to pass to `SetSubscriptions`, and to use for
application-level player identity. Without it, every feature would have to invent its own socket-to-player
mapping. Connections also need teardown: when a client disconnects, its View subscriptions and any per-client
View instances must be released or they leak. Typhon owns both the identity and the teardown — game code only
ever touches a `ClientContext`, never a socket.

## ⚙️ How it works (in brief)

A dedicated listener thread runs a `Socket.Accept()` loop on a dual-stack TCP/IPv6 socket. Each accepted
connection gets `TCP_NODELAY` enabled and is assigned a process-lifetime-unique `ConnectionId` (monotonically
increasing). The connection's public face is a `ClientContext` carrying that `ConnectionId` plus a free-form
`UserData` slot the application can use for its own identity (player ID, auth token, anything). All
subscription APIs (`SetSubscriptions`, per-client View factories) take `ClientContext`, never the underlying
connection. When a client disconnects — detected either by a failed send on the I/O thread or as a dead-socket
check during the Output phase — the runtime removes it from every `PublishedView`'s subscriber list, disposes
its per-client Views, and evicts it from the connection registry, all without game code intervention.

## 💻 Usage

```csharp
// The runtime hands a ClientContext to per-client View factories — this is the main
// touchpoint where game code reads identity stashed on UserData.
runtime.PublishView("my_inventory", (ClientContext client) =>
{
    using var tx = runtime.CreateSideTransaction();
    int playerId = (int)client.UserData;   // populated by application/auth code
    return tx.Query<InventoryArch>()
        .WhereField<InventoryItem>(i => i.OwnerId == playerId)
        .ToView();
});

// The same ClientContext is the key for subscription management — resolved internally
// by ConnectionId, so passing a stale (disconnected) context is a safe no-op.
runtime.SetSubscriptions(client, worldStatePub, inventoryPub);
```

| Option (`SubscriptionServerOptions`) | Default | Effect |
|---|---|---|
| `Port` | 9000 | TCP listen port (dual-stack IPv6/IPv4) |
| `MaxClients` | 0 (unlimited) | Connections beyond this are refused at accept time, before a `ClientContext` is created |

## ⚠️ Guarantees & limits

- `ConnectionId` is a monotonically increasing `int`, unique for the server process's lifetime — not reused,
  not persisted across restarts.
- `UserData` is an untyped `object` slot Typhon never reads or interprets; populating, casting, and validating
  it is entirely the application's responsibility.
- An accepted connection is implicitly trusted — v1 has no built-in authentication or TLS (see
  [TCP Transport & Wire Format](./wire-transport.md)).
- Disconnect cleanup (subscriber-list removal, per-client View disposal, registry eviction) runs exactly once
  per connection — `ClientConnection.Dispose` is idempotent, so a race between the I/O thread and the Output
  phase detecting the same disconnect cannot double-clean.
- `SetSubscriptions(client, ...)` silently no-ops if the connection behind `client` was already torn down —
  callers don't need to guard every call with a liveness check.
- No public "client connected" notification exists yet in v1 — a `ClientContext` only reaches game code via a
  per-client View factory call or as the argument to a `SetSubscriptions` call already in flight. There is no
  hook to run application code exactly once at accept time.

## 🧪 Tests

- [SubscriptionIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionIntegrationTests.cs)
  — `Client_Connects_And_ReceivesTickDelta_WithSpawnedEntity`: accept → `ClientContext` → subscribe → receive,
  end to end over a real socket
- [SubscriptionStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionStressTests.cs) —
  `RapidConnectDisconnect_NoServerCrash`: concurrent connect/disconnect churn exercises the idempotent cleanup path

## 🔗 Related

- Related feature: [Published Views](./published-views/README.md), [Server-driven subscriptions](./subscription-management/subscription-server-driven.md), [TCP Transport & Wire Format](./wire-transport.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Connection Lifecycle -->
<!-- Deep dive: claude/overview/13-runtime.md — Subscription Server -->
