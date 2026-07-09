---
uid: feature-profiler-typed-event-capture-pipeline
title: 'Typed-Event Capture Pipeline'
description: 'Any-thread, ~25-50ns, zero-allocation event capture that costs nothing when the profiler is off.'
---

# Typed-Event Capture Pipeline
> Any-thread, ~25-50ns, zero-allocation event capture that costs nothing when the profiler is off.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Diagnosing latency spikes and throughput regressions in a microsecond-budget engine requires seeing what every thread did, at the granularity of individual transactions, B+Tree operations, and page-cache hits — without the instrumentation itself becoming the bottleneck. String-keyed loggers, boxed `object[]` params, and `Activity`-based tracing all allocate or synchronize on the hot path, which is unacceptable inside a commit or a query loop. Typhon needs first-class visibility into its own engine internals that a developer can leave compiled in permanently and switch on only when needed, at effectively zero standing cost.

## ⚙️ How it works (in brief)

Every instrumented call site opens a typed, ref-struct "event" (one per operation kind — transaction commit, B+Tree insert, page fetch, etc.) via a `TyphonEvent.BeginXxx` factory, optionally sets a few fields, and disposes it when the operation ends. Disposal encodes the record directly into the calling thread's own ring buffer — no locks, no CAS, because each thread is the sole producer for its slot. A dedicated consumer thread wakes on a fixed cadence, drains every active thread's ring, timestamp-sorts the merged records (spans close out of start-time order because they're written on `Dispose`), and slices the result into batches for whatever exporters are attached. A per-thread spillover pool absorbs bursts that would otherwise overflow a slot's primary ring. When the profiler is disabled, the gate check is the first statement of every factory against a `static readonly bool`, so the JIT deletes the entire call — this is the mechanism the rest of the Profiler area's instrumentation (GC tracing, gauges, CPU sampling, etc.) is built on top of.

## 💻 Usage

This pipeline is engine-embedded: ~230 call sites across transactions, the B+Tree, page cache, WAL, checkpoint, and ECS already emit through it, so most applications never write capture code — they only flip it on. A `typhon.telemetry.json` next to your executable is enough:

```csharp
// typhon.telemetry.json (loaded from the working directory or the assembly directory at startup)
// {
//   "Typhon": {
//     "Telemetry": { "Enabled": true, "Profiler": { "Enabled": true } }
//   }
// }

// No profiler code required — TyphonRuntime.Create self-wires capture + a default exporter
// from that config file the moment the runtime is built.
var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
```

This is what one of those built-in call sites looks like under the hood (the same `BeginXxx`/`Dispose` shape every instrumented operation uses):

```csharp
var scope = TyphonEvent.BeginTransactionCommit(tsn);
scope.ComponentCount = componentInfos.Count;   // optional field — sets a payload bit, not required
try
{
    // ... commit body ...
    scope.ConflictDetected = hasConflict;
}
finally
{
    scope.Dispose();   // encodes + publishes the record to this thread's ring
}
```

| Option (`Typhon:Profiler:*`) | Default | Effect |
|---|---|---|
| `Enabled` | `false` | Master producer gate for this pipeline; `false` makes every `BeginXxx` fold to `return default;` at JIT time |

## ⚠️ Guarantees & limits

- **Any-thread, ~25-50 ns per span/instant** on the hot path — no allocation, no lock, no `Interlocked` (single-producer-per-slot design).
- **Zero cost when disabled** — the `ProfilerActive` gate is a `static readonly bool` checked first in every factory; RyuJIT dead-code-eliminates the rest of the call.
- **Drop-newest, never blocks** — a full ring (after spillover is exhausted) or a full exporter queue drops the newest record and counts it; the producer thread is never stalled waiting for capture to catch up.
- **Capped concurrency** — up to 256 live thread slots tracked at once; beyond that, new threads' events are dropped rather than growing the registry unboundedly.
- **Unique, ordering-free span IDs** — each span ID encodes its slot index plus a per-slot counter that is never reset, so IDs stay unique for the process lifetime without cross-thread coordination.
- **Consumer reorders for you** — spans publish on `Dispose` (end time), not start time, so nesting can write records out of chronological order; the consumer thread re-sorts by start timestamp before handing batches to exporters.
- **Fixed drain cadence** — the consumer thread drains on a configurable interval (default 1 ms); this is capture-side latency before a record is even eligible for export, independent of exporter speed.
- **Not the export path** — this feature only gets events into sorted, batched form in memory; turning that into a `.typhon-trace` file or a live TCP stream is a separate exporter feature.

## 🧪 Tests

- [TraceRecordRingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/Events/TraceRecordRingTests.cs) — reserve/publish/drain, size rounding, overflow, wrap-sentinel handling, SPSC producer/consumer correctness under real concurrency
- [TraceRecordChainTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/Events/TraceRecordChainTests.cs) — the spillover chain (`TraceRecordRing.Next`, `ThreadSlot.ChainHead`/`ChainTail`) that absorbs bursts overflowing a slot's primary ring
- [BeginFactoryParameterTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/BeginFactoryParameterTests.cs) — per-kind `BeginXxx` factory field assignment, and the suppressed/disabled-gate short-circuit that returns `default`

## 🔗 Related

- Sibling: [Built-in Engine Instrumentation Catalog](./builtin-subsystem-instrumentation.md) — the ~230 call sites across the engine that emit through this pipeline
- Sibling: [Configuration & Performance Tuning](./profiler-configuration-tuning.md) — the `ProfilerActive` gate every `BeginXxx` factory checks first
- Sibling: [Trace Export](./trace-export/README.md) — where the sorted, batched records this pipeline produces get exported to a file or live feed

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §3-6, claude/overview/09-observability.md -->
<!-- Design: claude/design/Profiler/typhon-event-emission-refactor.md, claude/design/Profiler/typhon-event-hand-written-holdouts.md -->
<!-- ADRs: 047 — Typed-Event Profiler Architecture (claude/adr/047-typed-event-profiler-architecture.md), 049 — Wire Format in Profiler Library (claude/adr/049-wire-format-in-profiler-library.md), 050 — Typed-Event Source Generator (claude/adr/050-typed-event-source-generator.md) -->
