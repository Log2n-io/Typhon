# GC Event Tracing
> See every .NET garbage collection and EE-suspension pause on the same timeline as your transactions.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Typhon is deliberately unmanaged-heavy, but the process still runs on the CLR, and any managed allocation
that leaks into a hot path shows up as a GC pause you can't otherwise correlate to what the engine was doing
at that instant. Without in-trace visibility, "was that latency spike a checkpoint stall or a GC?" requires
cross-referencing a separate GC log by wall-clock time — slow, and never quite lines up. GC event tracing
puts collection starts, ends, and execution-engine suspension windows directly on the profiler timeline, next
to the transaction/B+Tree/page-cache spans they may be blocking.

## ⚙️ How it works (in brief)

A dedicated `EventListener` subscribes to the CLR's `Microsoft-Windows-DotNETRuntime` provider (the same
EventPipe path `dotnet-counters` uses, so it works identically on Windows/Linux/macOS) and translates GC
lifecycle callbacks into records on a small lock-free queue — never touching your producer threads. A
separate ingestion thread drains that queue and emits the records through the normal typed-event pipeline on
its own thread slot, so GC capture cost never lands on engine hot paths. `GcEnd` additionally snapshots
`GC.GetGCMemoryInfo()` for per-generation heap sizes, which also feed the gauge region's Memory track.

## 💻 Usage

Nothing to call — flip the config flag and the engine wires up the listener and ingestion thread for you:

```csharp
// typhon.telemetry.json next to your executable:
// { "Typhon": { "Telemetry": { "Enabled": true }, "Profiler": { "Enabled": true, "GcTracing": { "Enabled": true } } } }

using Typhon.Engine;

var runtime = TyphonRuntime.Create(dbe, schedule =>   // ProfilerBootstrap starts GcTracingHost, if enabled
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});

// …run your workload…

TyphonProfiler.Stop();   // optional; ordinary shutdown detaches the listener and joins the ingestion thread too
```

| Option | Default | Effect |
|---|---|---|
| `Typhon:Profiler:GcTracing:Enabled` | `false` | Starts the `EventListener` + ingestion thread; emits `GcStart`/`GcEnd`/`GcSuspension` and populates the GC heap-size gauges |

## ⚠️ Guarantees & limits

- **Off the hot path** — the CLR's GC callback thread only enqueues a small struct; translation into wire
  records happens later, on a dedicated ingestion thread, never on an engine-owned thread slot.
- **Cross-platform** — same EventPipe provider on Windows, Linux, and macOS; no ETW-only dependency (that's
  a separate, Windows-only feature for OS thread scheduling).
- **Three record kinds**: `GcStart` (generation, reason, type, count), `GcEnd` (same plus pause duration,
  promoted bytes, per-generation size-after values for Gen0/1/2/LOH/POH, and total committed bytes),
  `GcSuspension` (a span covering `GCSuspendEEBegin` → `GCRestartEEEnd`, tagged with the suspend reason).
  Suspension spans are process-level — no parent span, no attribution to the transaction that happened to be
  running.
- **Only GC-caused suspensions are recorded** — the EE also suspends for reasons unrelated to garbage
  collection (e.g., the CPU sampler's stack walks), and those are filtered out rather than flooding the trace.
- **Negligible overhead** — GC events occur at human/collection timescales (not per-span), so the ingestion
  thread's steady-state cost is effectively zero; measured combined overhead with GC tracing + other opt-in
  extras enabled is under 1% on tested workloads.
- **Gated independently** — `Typhon:Profiler:GcTracing:Enabled` is its own flag; it costs nothing when off
  and doesn't require enabling memory-allocation tracing or gauges.
- **Resolved once at startup** — like every `TelemetryConfig` gate, this is read at class load; toggling the
  JSON/env var after the process starts has no effect until restart.
- **Viewer** shows a dedicated GC track: red bars for suspension windows, start/end markers for collections,
  and a stacked-area Gen0–POH view in the Memory gauge track — pre-computed at trace-open time so its scale
  stays stable while you pan across a large trace.

## 🧪 Tests

- [GcTracingHostTests](../../../test/Typhon.Engine.Tests/Profiler/GcTracingHostTests.cs) — ingestion-thread → `TyphonEvent` emit path, records injected directly into the queue to exercise processing independent of a real GC
- [GcEventListenerTests](../../../test/Typhon.Engine.Tests/Profiler/GcEventListenerTests.cs) — `IsGcSuspendReason` filter that keeps CPU-sampler-induced EE suspensions out of the GC track

## 🔗 Related

- Sibling features: [Configuration & Performance Tuning](./profiler-configuration-tuning.md), [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md), [Built-in Engine Instrumentation Catalog](./builtin-subsystem-instrumentation.md)
- Source: `src/Typhon.Engine/Profiler/internals/{GcEventListener,GcIngestionThread,GcEventQueue,GcEventRecord,GcTracingHost,GcSuspensionEvent}.cs`, `src/Typhon.Profiler/GcEnums.cs`

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §6.7, claude/design/Profiler/06-profiler-feature-roadmap.md §3.1 -->
<!-- User manual: claude/design/Profiler/profiler-user-manual.md §3.1, §5.3, §8.2 -->
