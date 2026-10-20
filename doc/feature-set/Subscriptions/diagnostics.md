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
  `typhon.subscriptions.track.p99`, `typhon.durability.wait.p99`, `typhon.sessions.closed` by reason and `typhon.frames.skipped` by cause,
  and per session `typhon.session.outBytesPerSec`, `skippedFrames`, `droppedCommands`) and your own. `typhon.durability.wait.p99` is the per-**tick** wait — the Unit-of-Work flush, which in WAL mode
  is `RequestFlush` + `WaitForDurable` for every record the tick produced together — and not a per-commit figure; read against
  `typhon.tick.p99` it says how much of a tick goes to durability rather than to compute.
- **Application metrics**: `subs.Metric(name, unit, codec, source)` (server scope, optionally labelled) and `subs.SessionMetric(…)` (per
  session); the server segment is encoded once for every subscriber.
- **The same numbers without a client**: `TyphonRuntime.ReadStats()` returns tick p50/p99 against the target, the overrun count, the
  per-tick durability wait, per-system means, entities per archetype and the session figures — reading the telemetry ring and the
  engine directly, so it answers on an engine with **no replication and no declared metrics**. `MapTyphonStats()`
  (`Typhon.Subscriptions.AspNetCore`) serves it as JSON at `/typhon/stats.json`. It has no authentication and publishes system and
  archetype names: restrict it as you would an admin route.
- **Why sessions closed.** `ReadStats().SessionsClosed` (per reason: left, link lost, kicked, and the engine's own: silent 4001, lagging 1013,
  protocol, abusive, internal) and `ReadStats().FramesSkipped` (per cause: in flight full, lagging, pool refused, oversize, gated, owed
  reset), cumulative, the same numbers `STATS` carries. And one **warning** a second at most, only in a second the engine itself closed
  sessions: how many, every reason and skip cause of that second, and the longest tick interval. Silent closes with normal ticks are clients
  that stopped talking; a long tick interval is the server stalling; lag closes with in-flight-full skips are clients that cannot keep up.
- **`DEBUG` capability**, granted only to sessions admitted with `SessionLimits.AllowDebug` (`SessionLimits.God`): engine sub-blocks such as
  the replication grid and a session's push geometry, for inspectors and recorders.
- **Push validator**: `TYPHON_PUSH_VALIDATE=N` — see [Push replication](push-replication.md).
- **Trace records** for the profiler and the Workbench, each behind its own gate under `Typhon:Profiler:Subscriptions`: the server and
  per-session figures once a second (kinds 68, 69), and one session's story as it happens — every frame published (71, the volume one), every
  frame skipped with its cause (72), every open, close and degrade step (73). See the [telemetry flags](../Observability/telemetry-flags-reference.md).
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

- [SubscriptionsSessionRecordsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/SubscriptionsSessionRecordsTests.cs) — a session's records, opened to closed (gated: run with `TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED=true`)
- [StatsBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/StatsBlockTests.cs) (one close counted alike through `STATS`, `ReadStats` and the warning) · [DebugBlockTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/Subscriptions/DebugBlockTests.cs)

## 🔗 Related

- Related feature: [Telemetry & Runtime Inspection](../Runtime/telemetry-runtime-inspection.md)
- [Wire protocol & catalog](wire-protocol.md)
