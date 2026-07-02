# Per-Tick Gauge/Metric Snapshots
> One packed record per tick exposes memory, page-cache, WAL, and transaction counters to the trace viewer.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Span-based tracing tells you what happened and how long it took, but it doesn't show continuous state —
how full the page cache is, how much is queued in the WAL commit buffer, how many transactions are live —
between the discrete events that touch them. Without a periodic sample, answering "was the engine under
memory or I/O pressure during that slow stretch of ticks" means bolting ad hoc logging onto internal
counters and correlating it by hand afterward. Per-tick gauge snapshots give the trace viewer numeric
time-series tracks for engine resource state, aligned to the same timeline as the flame graph, with zero
instrumentation required in application code.

## ⚙️ How it works (in brief)

At the end of every tick (when enabled, wired through the scheduler), the engine reads a fixed set of
`Interlocked`-updated counters from memory, page cache, transient store, WAL, and transaction/UoW subsystems
in one pass and packs them into a single `PerTickSnapshot` record — so every value in that record shares
exactly one timestamp. Each value is tagged with a stable `GaugeId` (grouped into category ranges: memory,
GC heap, page cache, transient store, WAL, transactions/UoW) and a `GaugeValueKind` declaring its wire shape.
Fixed-at-startup capacities (e.g. total page-cache pages) are emitted only in the very first snapshot; the
viewer caches them as reference lines. Cumulative counters (e.g. total commits) are monotonic — the viewer
derives per-tick throughput by subtracting consecutive snapshots.

## 💻 Usage

`GaugeSnapshotEmitter` is engine-internal — it runs automatically once wired to the scheduler. There is
nothing to call from application code; enabling the feature is a config toggle:

```csharp
// typhon.telemetry.json next to your executable:
// {
//   "Typhon": {
//     "Telemetry": { "Enabled": true },
//     "Profiler": { "Enabled": true, "Gauges": { "Enabled": true } }
//   }
// }

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
runtime.Start();

// No TyphonEvent calls anywhere above — one PerTickSnapshot lands per tick automatically. Open the trace
// in the Workbench: the gauge region above the flame graph populates with Memory / Page Cache / WAL /
// Transactions+UoW tracks, sharing the same time axis as everything else.
```

If you're writing your own consumer against `.typhon-trace` files (rather than using the Workbench viewer),
`GaugeId` and `GaugeValueKind` are public types in `Typhon.Profiler` — use them to decode a `PerTickSnapshot`
payload's `{gaugeId, valueKind, value}` triples.

| Config key | Default | Effect |
|---|---|---|
| `Typhon:Profiler:Gauges:Enabled` | `false` | Enables the end-of-tick snapshot pass and `PerTickSnapshot` emission |

## ⚠️ Guarantees & limits

- **Zero cost when disabled** — gated by `TelemetryConfig.ProfilerGaugesActive`, a `static readonly bool`
  the JIT dead-code-eliminates when off.
- **Sub-microsecond when enabled** — one pass over roughly twenty `Interlocked` source fields plus one
  record emit, once per tick, on the scheduler thread.
- **One snapshot, one timestamp** — all values in a given `PerTickSnapshot` are tagged as of the same
  instant; they are not independently time-stamped samples.
- **Cumulative counters are monotonic U64** — never reset mid-session. Consumers must diff consecutive
  snapshots to get a rate; U64 (not U32) avoids rollover at sustained high throughput.
- **Fixed-at-init capacities emitted once** — total page-cache pages, transient-store max bytes, WAL
  commit-buffer capacity, and WAL staging-pool capacity appear only in the first snapshot of a session.
- **GC heap-size gauges are always sampled when Gauges is on** — Gen0…POH sizes and committed bytes come
  from `GC.GetGCMemoryInfo()` every tick regardless of whether GC event tracing is separately enabled; they
  are not the same feature as per-collection `GcStart`/`GcEnd`/`GcSuspension` spans.
- **Wire-stable, append-only `GaugeId` numbering** — existing IDs are never renumbered; new gauges append
  within a category range or open a new one, so old trace files keep decoding correctly.
- **Coverage today**: unmanaged memory, GC heap, page-cache buckets + pending I/O + file size, transient
  store bytes, WAL commit buffer + staging pool, transaction-chain and UoW live/cumulative counts. Arena/pool
  allocation gauges and lock-wait gauges are reserved ranges only — not yet wired.

## 🧪 Tests

- [PerTickSnapshotEventCodecTests](../../../test/Typhon.Engine.Tests/Profiler/PerTickSnapshotEventCodecTests.cs) — wire round-trip for every `GaugeValueKind` variant plus empty-payload and boundary-size edge cases

## 🔗 Related

- Sibling features: [Configuration & Performance Tuning](./profiler-configuration-tuning.md), [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md)
- Source: `src/Typhon.Engine/Profiler/internals/GaugeSnapshotEmitter.cs`, `src/Typhon.Profiler/GaugeId.cs`

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §6.6, §12, claude/design/Profiler/profiler-user-manual.md §5.3 -->
<!-- Roadmap: claude/design/Profiler/06-profiler-feature-roadmap.md §2.2 -->
