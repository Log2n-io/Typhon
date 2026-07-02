# File-Based Trace Export (.typhon-trace)
> Write the whole session to a versioned binary file for offline post-mortem analysis in the Workbench.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Profiler](../README.md)

## 🎯 What it solves

Some bugs only show up after hours of load, or only once, or only right before a crash — you can't always be
watching live when the interesting moment happens. File export gives you a complete, durable record of a
session that you can open later, pan and zoom across the whole timeline, hand to a teammate, or attach to a bug
report, without needing the original process to still be running.

## ⚙️ How it works (in brief)

`FileExporter` owns one `.typhon-trace` v3 binary file for the session: a header plus system/archetype/component
tables written at start, then one LZ4-compressed block per drained batch (capped at 256 KB uncompressed,
matching the batch size). At session stop it appends optional trailer sections — source-location manifest, CPU
samples, interned query strings — and rewrites the header with their offsets. The file is chosen by setting a
trace path in configuration; the Workbench trace viewer reads it directly and builds a sidecar cache alongside
it the first time it's opened.

## 💻 Usage

```csharp
// typhon.telemetry.json — zero code:
// { "Typhon": { "Telemetry": { "Enabled": true,
//     "Profiler": { "Enabled": true, "Trace": "session.typhon-trace" } } } }

using Typhon.Engine;

var runtime = TyphonRuntime.Create(dbe, schedule =>   // profiler self-wires from config
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
// ... run the workload — every built-in call site emits automatically ...
TyphonProfiler.Stop();   // optional; ordinary engine shutdown finalizes the file automatically
```

Computing the path in code (e.g. one file per run) instead of a fixed JSON value:

```csharp
services.AddTyphonProfiler(cfg => cfg with
{
    TraceFilePath = $"trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}.typhon-trace"
});
var provider = services.BuildServiceProvider();
// pass serviceProvider: provider to TyphonRuntime.Create so ProfilerBootstrap picks up the override above
```

| Config key | Default | Effect |
|---|---|---|
| `Typhon:Profiler:Trace` | unset (no file export) | Path the `.typhon-trace` file is written to; setting it attaches a `FileExporter` for the session |

## ⚠️ Guarantees & limits

- **Complete only after a clean stop** — the file becomes fully self-contained (final block + trailer sections)
  once `TyphonProfiler.Stop()` runs, which ordinary engine shutdown does automatically; a process kill before
  that can leave the trailer or the last block missing.
- **Immutable once written** — never edit a `.typhon-trace` in place: the Workbench keys its sidecar cache off a
  fingerprint over the file's mtime, length, and first/last 4 KB, so any in-place edit forces (and invalidates)
  a rebuild. Copying, renaming, or moving the file is always safe.
- **Bounded, non-blocking handoff** — the exporter's queue holds up to 64 batches (~16 MB); a stalled disk drops
  the newest batch rather than stalling the profiler consumer thread or any other attached exporter.
- **Single writer per file** — one `FileExporter` instance owns the stream; don't point two sessions at the same
  path concurrently.
- **Trailer sections are best-effort** — a CPU-sample or query-string encoding failure is caught and logged; it
  never costs the rest of the trace, which stays fully readable without that section.
- **Unbounded file size** — a long-running session keeps appending compressed blocks indefinitely; rotation and
  retention are the operator's responsibility, not the exporter's.

## 🧪 Tests

- [FileExporterIntegrationTests](../../../../test/Typhon.Engine.Tests/Profiler/FileExporterIntegrationTests.cs) — start/emit/stop round-trips every typed event through the written `.typhon-trace`, plus CPU-sample trailer embedding and header offset patching

## 🔗 Related

- Source: [`src/Typhon.Engine/Profiler/internals/FileExporter.cs`](../../../../src/Typhon.Engine/Profiler/internals/FileExporter.cs)
- Parent feature: [Trace Export](./README.md)
- Sibling: [Live TCP Streaming Export](./live-tcp-trace-export.md) — the other trace-export sink; both can attach to the same session at once

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §4.6, §7.1, claude/design/Profiler/profiler-user-manual.md §9.1 -->
