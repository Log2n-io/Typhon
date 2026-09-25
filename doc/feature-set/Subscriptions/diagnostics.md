---
uid: feature-subscriptions-diagnostics
title: 'Replication Diagnostics'
description: 'STATS metrics a client can subscribe to — built-in server and session metrics plus your own — the DEBUG capability for tools, the push validator, and runtime counters.'
---

# Replication Diagnostics
> See what replication costs and what each client receives, from the server and from the client.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](./README.md)

## 🎯 What it solves

A replication problem shows up on a client — a stale entity, a stuttering mover, a slow frame — far from where it is caused. Clients and
tools need the server's own numbers alongside the stream, and developers need a way to catch a forgotten push.

## ⚙️ How it works (in brief)

- **`STATS` capability.** A client that requests it receives, about once a second, a dense snapshot of the catalog's metrics: built-ins
  (`typhon.tick.p50/p99`, `typhon.system.mean` per system, `typhon.archetype.entities`, `typhon.sessions`, `typhon.net.outBytesPerSec`,
  `typhon.subscriptions.track.p99`, `typhon.durability.wait.p99`, and per session `typhon.session.outBytesPerSec`, `skippedFrames`,
  `droppedCommands`) and your own.
- **Application metrics**: `subs.Metric(name, unit, codec, source)` (server scope, optionally labelled) and `subs.SessionMetric(…)` (per
  session); the server segment is encoded once for every subscriber.
- **`DEBUG` capability**, granted only to sessions admitted with `SessionLimits.AllowDebug` (`SessionLimits.God`): engine sub-blocks such as
  the replication grid and a session's push geometry, for inspectors and recorders.
- **Push validator**: `TYPHON_PUSH_VALIDATE=N` — see [Push replication](push-replication.md).
- **Runtime counters** on `ctx.Subscriptions` (projection blocks, enter/leave flow, identity flow, send path) for server-side logging.

## 💻 Usage

```csharp
subs.Metric("game.creatures.alive", "count", Codec.VarUInt, () => aliveCount);
subs.SessionMetric("game.session.ping", "ms", Codec.F16, session => PingOf(session));
```

## ⚠️ Guarantees & limits

- A skipped session loses nothing: gauges are windowed and counters cumulative.
- Metric names under `typhon.` are reserved; a metric is refused at `Start` if its name collides with a built-in.
- `DEBUG` exposes server structure (grid, clusters, migrations); never grant it to untrusted clients.

## 🧪 Tests

- [StatsBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/StatsBlockTests.cs) · [DebugBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/DebugBlockTests.cs)

## 🔗 Related

- Related feature: [Telemetry & Runtime Inspection](../Runtime/telemetry-runtime-inspection.md)
- [Wire protocol & catalog](wire-protocol.md)
