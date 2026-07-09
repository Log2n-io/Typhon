---
uid: feature-profiler-trace-export-live-tcp-trace-export
title: 'Live TCP Streaming Export'
description: 'Stream a running session over TCP so the Workbench can watch a process tick-by-tick, right now.'
---

# Live TCP Streaming Export
> Stream a running session over TCP so the Workbench can watch a process tick-by-tick, right now.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Profiler](../README.md)

## 🎯 What it solves

Waiting for a process to exit — or crash — before you can look at its trace doesn't work when you're chasing a
live stall, watching memory climb during a soak test, or want to see a load test's behavior as it happens. Live
export lets a Workbench viewer attach directly to a running process's socket and see events arrive as they're
produced, with no file round-trip and no restart required.

## ⚙️ How it works (in brief)

`TcpExporter` listens on one TCP port and accepts a single client at a time. On connect it sends an Init frame
(the same header + metadata tables as the file format) and then a stream of Block frames, one per drained batch,
LZ4-compressed the same way as file export. It also re-sends a small catch-up frame — currently-claimed thread
names — about once a second, so a viewer that attaches mid-session still gets correct lane labels. Sends are
non-blocking: a frame that can't be written immediately is dropped rather than stalling the exporter thread. On
profiler stop, a Shutdown frame closes the session cleanly.

## 💻 Usage

```csharp
// typhon.telemetry.json — zero code:
// { "Typhon": { "Telemetry": { "Enabled": true,
//     "Profiler": { "Enabled": true, "Live": 9100, "LiveWaitMs": 5000 } } } }

using Typhon.Engine;

var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
// LiveWaitMs > 0 blocks Create() until the first viewer connects (or the timeout elapses).
// ... run the workload; in the Workbench: File → Connect Live → "localhost:9100" ...
```

| Config key | Default | Effect |
|---|---|---|
| `Typhon:Profiler:Live` | unset (no live export) | TCP port to listen on; any non-numeric value falls back to port 9100 |
| `Typhon:Profiler:LiveWaitMs` | `0` (don't wait) | Milliseconds `TyphonRuntime.Create` blocks waiting for the first viewer to attach before returning |

## ⚠️ Guarantees & limits

- **Single client only** — a second connection attempt while a viewer is attached is rejected outright; run a
  second session on a different `Live` port if you need a second observer.
- **No persistence** — bytes never delivered over this socket cannot be replayed; a live-only session has no
  equivalent of the file's completeness. Set both `Trace` and `Live` together to keep a durable copy while also
  watching live.
- **Drop-on-backpressure, not stall** — a slow client causes individual frames to be dropped
  (`SocketError.WouldBlock`) rather than backing up the profiler consumer thread; expect gaps in a chronically
  slow viewer's feed rather than delay.
- **Silent disconnect handling** — if the viewer's connection drops, the engine keeps running and simply accepts
  the next client; there's no application-visible error.
- **`LiveWaitMs` only gates startup once** — it blocks `TyphonRuntime.Create` for the *first* connection; a later
  reconnect after the viewer disconnects does not re-block anything.
- **Plain TCP, no platform dependency** — works identically on Windows/Linux/macOS, unlike the ETW-based
  off-CPU scheduling extension.

## 🧪 Tests

- [TcpExporterIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/TcpExporterIntegrationTests.cs) — Init/Block frame round-trip over a loopback socket, plus `LiveWaitMs`/live-connect-timeout blocking-vs-non-blocking connect behavior

## 🔗 Related

- Source: [`src/Typhon.Engine/Profiler/internals/TcpExporter.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Profiler/internals/TcpExporter.cs)
- Parent feature: [Trace Export](./README.md)
- Sibling: [File-Based Trace Export (.typhon-trace)](./file-trace-export.md) — the other trace-export sink; both can attach to the same session at once

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §4.7, §7.2, claude/design/Profiler/08-profiler-live-replay-unification.md, claude/design/Profiler/profiler-user-manual.md §4.4 -->
