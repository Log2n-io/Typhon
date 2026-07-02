# Trace Export
> Attach durable file capture and/or a live TCP feed to the same profiler session — pick one, both, or neither.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Profiler](../README.md)

## 🎯 What it solves

Capturing a profiler session is only useful if the bytes actually get somewhere. Some investigations need a
complete, durable record to pore over after the fact — a crash, a rare stall, a regression that only shows up
after hours of load. Others need to watch a process *right now* — a live load test, a running server you can't
restart to reproduce. Trace export gives both without forcing a choice: the same event stream can be written to
disk, streamed to a live viewer, or both, and a slow or misbehaving sink can never stall the engine or take
another sink down with it.

## ⚙️ How it works (in brief)

Every attached sink implements `IProfilerExporter`. When the profiler session starts, each attached exporter
gets its own dedicated OS thread and its own bounded queue; the consumer thread drains the per-thread ring
buffers, sorts records into batches, and hands each batch — refcounted, one physical buffer shared across every
sink — to every attached exporter's queue. A sink that falls behind only drops its own newest batch; it never
blocks the consumer or any other sink. Typhon ships two built-in sinks: a file writer for offline analysis and a
TCP streamer for live attach. Both are selected declaratively through `typhon.telemetry.json` — no exporter
construction or profiler start/stop calls are required in application code (see
[Profiler Session Lifecycle & Zero-Code Bootstrap](../profiler-lifecycle-bootstrap.md)).

## Sub-features

| Sub-feature | Use it for |
|---|---|
| [File-based trace export (.typhon-trace)](./file-trace-export.md) | Post-mortem: a complete, replayable binary file for offline analysis in the Workbench trace viewer |
| [Live TCP streaming export](./live-tcp-trace-export.md) | Real-time: attach the Workbench directly to a running process and watch ticks as they happen |

## ⚠️ Guarantees & limits

- **Per-exporter failure isolation** — each exporter owns a dedicated OS thread and its own bounded queue (64
  batches, ~16 MB of slack for the two built-ins); a slow disk or a stalled TCP client cannot stall the profiler
  consumer thread or any other attached exporter.
- **Drop-newest on backpressure** — when an exporter's queue is full, the batch that just failed to enqueue is
  discarded (not one already queued); the batch's refcount is released immediately so the shared pool never
  leaks regardless of how many sinks drop it.
- **Both sinks can run at once** — setting a trace path and a live port in the same config attaches both
  exporters to the same session; every record reaches both independently.
- **Attach/detach only while stopped** — exporters are fixed for the lifetime of a running session; there is no
  API to add or remove a sink while the profiler is capturing.
- **Two production sinks ship today** — `IProfilerExporter` is a public extension point, but file and live-TCP
  are the only sinks the engine builds and wires automatically.

## 🧪 Tests

- [FileExporterIntegrationTests](../../../../test/Typhon.Engine.Tests/Profiler/FileExporterIntegrationTests.cs) — file sink attached to a real session, start/emit/stop lifecycle
- [TcpExporterIntegrationTests](../../../../test/Typhon.Engine.Tests/Profiler/TcpExporterIntegrationTests.cs) — TCP sink attached to a real session, Init/Block frame round-trip

## 🔗 Related

- Source: [`src/Typhon.Engine/Profiler/public/IProfilerExporter.cs`](../../../../src/Typhon.Engine/Profiler/public/IProfilerExporter.cs), [`ExporterQueue.cs`](../../../../src/Typhon.Engine/Profiler/public/ExporterQueue.cs)
- Sub-features: [File-based trace export](./file-trace-export.md), [Live TCP streaming export](./live-tcp-trace-export.md)

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §6.3-6.5, §7, claude/design/Profiler/profiler-user-manual.md §2, §4 -->
<!-- Overview: claude/overview/09-observability.md -->
