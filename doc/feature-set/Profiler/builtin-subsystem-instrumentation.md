# Built-in Engine Instrumentation Catalog
> Every transaction, B+Tree op, page fetch, WAL flush, checkpoint phase, and cluster migration is already traced — no app code required.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Profiler](./README.md)

## 🎯 What it solves

Diagnosing "why was this tick slow" or "why did commit latency spike" normally means going back to add instrumentation to the suspect code path, reproducing the problem, then removing the instrumentation afterward. That loop is expensive, and for a microsecond-budget engine the code you'd instrument is exactly the code you can't afford to slow down while investigating it. Typhon avoids the loop entirely for its own internals: the engine's Scheduler, Transaction, ECS, B+Tree, Page Cache, WAL, Checkpoint, Statistics, and Cluster Migration subsystems are already instrumented at every call site that matters, permanently, so turning the profiler on retroactively explains a run you didn't know you'd need to explain.

## ⚙️ How it works (in brief)

Dozens of call sites baked into these nine subsystems each open a typed [event](./typed-event-capture-pipeline.md) — `TyphonEvent.BeginTransactionCommit`, `BeginBTreeInsert`, `BeginPageCacheFetch`, `BeginWalFlush`, `BeginCheckpointCycle`, and so on — the same producer surface every part of the engine uses, so adding this catalog didn't require a second capture mechanism. Every one of these baseline kinds shares the default gate (`TelemetryConfig.ProfilerActive`) rather than needing its own category flag, so flipping the single master switch lights up all nine subsystems at once. Because every kind shares the same record framing, one generator-emitted `TraceEventDecoder` dispatches all of them into the DTOs the trace file, live TCP stream, and Workbench viewer consume — extending the catalog doesn't require touching consumer code. Page-cache disk I/O gets a two-record pattern: the synchronous kickoff span closes when the call returns, and a completion record carrying the *same* `SpanId` is emitted later from whatever thread-pool thread actually finished the read/write/flush, so the viewer can show both "how long did the call take" and "how long did the OS actually take" for the same operation.

## 💻 Usage

Nothing to write against these subsystems — enable the profiler and the coverage is already there:

```csharp
// typhon.telemetry.json next to your executable is the only "code" this feature needs:
// { "Typhon": { "Telemetry": { "Enabled": true, "Profiler": { "Enabled": true } } } }

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    schedule.PublicTrack.DeclareDag("Sim").CallbackSystem("Tick", ctx => RunGameLogic(ctx));
});
runtime.Start();

// No TyphonEvent calls anywhere above. Every transaction this workload commits, every B+Tree insert/split/merge
// its indexes perform, every page-cache fetch/read/write/evict, every WAL flush, every checkpoint phase, every
// statistics rebuild, and every archetype-cluster migration already lands in the trace — open it in the
// Workbench (or a `.typhon-trace` file) to see it, per §5 of the user manual.
```

`TyphonEvent` itself is `internal` to `Typhon.Engine` — application code was never meant to call it for these subsystems; this is the engine documenting itself, not an extension point. If you need your *own* code in the same flame graph, that's a separate (currently unimplemented) mechanism — see [Custom Application-Defined Spans](./custom-named-spans.md).

## ⚠️ Guarantees & limits

- **Nine subsystems, always on once the profiler is on** — Scheduler (`TickStart`/`TickEnd`/`Phase*`/`SystemReady`/`SystemSkipped`/`SchedulerChunk`), Transaction (`Commit`/`Rollback`/`CommitComponent`/`Persist`), ECS (`Spawn`/`Destroy`/`QueryExecute`/`QueryCount`/`QueryAny`/`ViewRefresh`), B+Tree (`Insert`/`Delete`/`NodeSplit`/`NodeMerge`), Page Cache (`Fetch`/`DiskRead`/`DiskWrite`/`AllocatePage`/`Flush`/`Evicted`/two async-completion kinds/`Backpressure`), WAL (`Flush`/`SegmentRotate`/`Wait`), Checkpoint (`Cycle`/`Collect`/`Write`/`Fsync`/`Transition`/`Recycle`), Statistics (`Rebuild`), and Cluster Migration (one span per archetype batch) — none of these need a per-subsystem JSON flag; only `Profiler.Enabled` gates them.
- **One exception**: `PageCacheFetch` is suppressed by default (`TyphonEvent.SuppressedKinds`) because it fires on every cache lookup — potentially millions of times per second — and would saturate a ring buffer if left on unconditionally. The other page-cache kinds fire at disk-operation/eviction frequency and are on by default.
- **Async completions are duration-gated** — `PageCacheDiskReadCompleted`/`WriteCompleted`/`FlushCompleted` only emit when the async tail exceeds a threshold (1 ms by default), so a fast, uninteresting completion doesn't double the record count for every I/O.
- **Wire-stable, append-only IDs** — each kind's numeric ID is part of the on-disk format; the engine never renumbers or reuses one, so old `.typhon-trace` files keep decoding correctly as the catalog grows.
- **Decoded by one shared path** — `TraceEventDecoder.DecodeBlock` walks any mix of these kinds (plus every other registered kind) through a single dispatch, so nothing in this catalog needs bespoke client-side parsing.
- **This catalog, not the opt-in extras** — GC tracing, unmanaged-memory tracking, per-tick gauges, CPU sampling, off-CPU scheduling, source attribution, and query-definition export are separate, individually-gated observability extensions layered on the same pipeline; they're not part of this always-on baseline.
- **Coverage is fixed, not extensible from application code** — `TyphonEvent`'s producer surface lives in `Typhon.Engine.Internals`; adding a new built-in kind is an engine change, not something a host application can register.

## 🧪 Tests

- [TyphonEventKindSuppressionTests](../../../test/Typhon.Engine.Tests/Profiler/TyphonEventKindSuppressionTests.cs) — the default suppression deny-list: only `PageCacheFetch` is suppressed by default, the other 9 page-cache kinds and every other subsystem's kinds are open
- [TraceEventEncodeEquivalenceTests](../../../test/Typhon.Engine.Tests/Profiler/TraceEventEncodeEquivalenceTests.cs) — per-kind wire-encode equivalence across the built-in event set, catching struct-layer vs. codec argument-order/field-thread-through bugs

## 🔗 Related

- Sibling features: [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md) (the producer/consumer mechanism these call sites use), [Custom Application-Defined Spans](./custom-named-spans.md) (app-side equivalent, not yet implemented)
- Sibling: [Telemetry Configuration & Gating](../Observability/telemetry-config-gating.md) — the gating surface that turns this instrumentation on/off
- Sibling: [Distributed Tracing (Activity API)](../Observability/distributed-tracing.md) — the actual source of span correlation today; Transaction/B+Tree/page-cache visibility comes from this catalog, not `Activity`
- Source: `src/Typhon.Engine/Profiler/internals/{SchedulerSpanEvents,TransactionEvents,EcsLifecycleEvents,BTreeEvents,PageCacheEvents,WalEvents,CheckpointEvents,StatisticsEvents,ClusterMigrationEvent}.cs`, `src/Typhon.Engine/Profiler/public/TraceEventDecoder.BlockWalker.cs`, `src/Typhon.Engine/Profiler/internals/HandGlue/TraceEventDecoder.HandGlue.cs`

<!-- Deep dive: claude/design/Profiler/typhon-profiler.md §4.4, claude/overview/09-observability.md §9.8 -->
<!-- User manual: claude/design/Profiler/profiler-user-manual.md §10 — quick-reference event kind table -->
<!-- ADRs: 047 — Typed-Event Profiler Architecture (claude/adr/047-typed-event-profiler-architecture.md), 050 — Typed-Event Source Generator (claude/adr/050-typed-event-source-generator.md) -->
