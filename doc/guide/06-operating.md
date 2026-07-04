# 6 — Operating & going deeper

You can now model, write, query, and run a world. This last chapter is about *living with* a running engine: seeing inside it, giving it sensible memory budgets, handling the errors it can throw — and knowing where to go when this guide runs out.

It's deliberately light. The theme throughout Typhon holds here too: the engine does the heavy lifting (it instruments and budgets itself); your job is to *turn on what you want to see* and *set a few caps*. None of this is required to build a working app — reach for it when you're moving from "it runs" to "it runs in production."

---

## 1. 🔭 Seeing inside a running engine

Three independent surfaces, each opt-in, each costing nothing until you switch it on.

### Metrics & health (OpenTelemetry)

Every long-lived part of the engine — page cache, WAL, transaction pool, scheduler — continuously reports its own memory, capacity, and throughput into an internal **resource graph**. You don't build any of it; you just expose it. One DI call wires the graph to OpenTelemetry meters plus health checks:

```csharp
services.AddTyphonObservabilityBridge(o =>
{
    o.SnapshotInterval = TimeSpan.FromSeconds(5);   // how often metrics refresh
});
```

After that, the `Typhon.Resources` meter feeds your existing OTel/Prometheus pipeline, and a health check reports the *worst* subsystem (a single saturated resource is never lost in a sea of green). You read dashboards; the engine fills them.

### The profiler

For deep, per-tick performance work there's an in-process **profiler** that captures a rich trace (spans for commits, queries, tick phases, lock contention, even off-CPU time) and a **Workbench** UI to explore it. The key fact for *using* it: it's **config-driven, zero host code**. Drop a `typhon.telemetry.json` next to your binary and the engine self-wires the profiler on the next run:

```json
{ "Typhon": { "Profiler": { "Enabled": true, "Storage": { "PageCache": { "Enabled": true } } } } }
```

Categories are a tree — enable a parent to enable its children. You pick *at deployment time* what to capture; the rest stays off.

> 💡 **Why on/off is a restart, not a runtime toggle.** Typhon's telemetry gates are `static readonly bool`s resolved once at startup, so the JIT **deletes** every disabled instrumentation branch at compile time — a switched-off span isn't a cheap `if`, it's *no code at all*. That's the trade: you lose runtime toggling, and in exchange the engine pays literally zero CPU for telemetry it isn't emitting. For a microsecond-latency engine that's the right side of the bet — you don't want a hot path checking a config flag a million times a second.

### The Workbench

The profiler *captures*; the **Workbench** is how you *look*. It's a local, browser-based UI — an ASP.NET Core server plus a single-page app — that opens what the engine produces and turns it into something you can read. Think "DataGrip for Typhon," not "a profiler with a few extra tabs": data browsing, schema inspection, query analysis, and timeline profiling live under one roof.

It opens three kinds of thing, and the views light up according to which one you picked:

- **A trace file** (`.typhon-trace`) — a recorded run, opened offline. The full performance story.
- **A live engine** — *attach* to a running process over TCP and watch ticks stream in as they happen.
- **A database** (a `.typhon` directory) — open the on-disk store directly to inspect its schema and physical layout, no trace needed.

**What you get today**, organised by what you're trying to understand:

*Understand a run's performance* (trace or live attach):
- **Profiler timeline** — the tick-by-tick view: per-tick duration, system spans, phases, overload level, metronome wait, with CPU samples and off-CPU (thread-scheduling) gaps overlaid on the right thread's lane. Pan and zoom over the whole run.
- **Top Spans** / **Call Tree** — the heaviest spans in the window, and the captured CPU-sample call tree for whatever you've selected — with click-through to the originating source line (**Source Preview**).
- **Critical Path** — the chain of systems that actually determined a tick's length, so you tune what matters instead of guessing.
- **System DAG** — the scheduler's dependency graph as a canvas, phase swim-lanes and derived edges, nodes coloured by their measured cost (mean / p50 / p95 / p99 / max over a chosen window).

*Understand the queries* (trace):
- **Query Catalog**, **Query Plan Tree**, and **Execution Inspector** — every query definition seen in the run, its plan (structural or with per-execution stats overlaid), and a drill-down into individual executions' phase breakdowns.

*Understand the data model & storage* (a `.typhon` database):
- **Component / Archetype browsers** and the **Schema Inspector** — survey every registered component (size, field count, entity count, indexes), inspect struct layout, indexes, and the systems that read/write or react to a component.
- **Database File Map** — a zoomable Hilbert-curve map of the actual data file: per-page fill, write-age, residency, segment/chunk structure, with search and pathology highlighting (e.g. under-filled pages). The physical truth of where your bytes live.
- **Resource Tree** — the engine's live resource graph (cache, WAL, pools…) as a navigable tree.

The shell ties it together: a dockable, persisted multi-panel workspace, a `Ctrl+K`-style command palette (commands and a `#`-prefixed jump-to-resource search), a Detail pane, and a Logs panel. A live attach session can also be saved to a self-contained `.typhon-replay` for later offline study.

**How to open it.** Start it with `pwsh -File wb-dev.ps1 start` from the repo root (it launches the backend on `:5200` and the UI on `:5173`), browse to `http://localhost:5173`, and from the welcome screen pick **Open .typhon File**, **Open .typhon-trace**, or **Attach to Engine**. For an attach session, run your engine with the profiler's TCP exporter enabled (`typhon.telemetry.json`, above) and point the Workbench at the port; for a trace, just open the `.typhon-trace` the run left behind.

> ⚠️ The performance views need profiler data — a `.typhon-trace` from a **profiler-enabled run**, or a live attach to an engine started with the TCP exporter on. Open a `.typhon` database (directory) with the profiler off and you still get the schema and File-Map views, but the timeline has nothing to show.

---

## 2. 🎚️ Resource budgets

The engine manages its own memory: a paged, memory-mapped store with a cache, the WAL stage, transaction pools. You never allocate a page. What you *do* set is a few **caps**, at engine-build time, and the engine lives within them.

```csharp
.AddScopedManagedPagedMemoryMappedFile(o =>
{
    o.DatabaseName    = "skirmish";
    o.DatabaseDirectory = ".";
    o.DatabaseCacheSize = 512UL * 1024 * 1024;   // page-cache size in bytes
})
.AddScopedDatabaseEngine(o =>
{
    o.Resources.TotalMemoryBudgetBytes = 4L << 30;   // overall budget — call o.Resources.Validate() yourself to enforce it
    o.Wal = new WalWriterOptions();                  // enable the WAL (durability)
});
```

The defaults are intentionally *small* — a 2 MB cache out of the box — so development exercises the cache machinery instead of hiding behind RAM. Size it up for real workloads. Call `options.Resources.Validate()` yourself after configuring — it throws if the fixed allocations (cache + WAL + buffers) don't fit inside `TotalMemoryBudgetBytes`. **The engine does not call this automatically at boot** — an oversized configuration is silently accepted unless you validate it yourself.

> 💡 **Cache size is not a database-size cap.** `DatabaseCacheSize` bounds the *resident working set*, not how much you can store — the on-disk database can be many times the cache; cold pages live on disk and page in on demand (persistent data, indexes, and the entity map all page out — only *Transient* components stay RAM-resident). Size the cache for throughput/latency, not capacity. This is the SQL/SQLite model, and it's what sets Typhon apart from in-memory ECS frameworks.

When a bounded resource fills, each one has a baked-in policy: a cache **evicts**, the WAL **waits** (applying backpressure to commits), and hard client-facing limits **fail fast** with a `ResourceExhaustedException` (which is transient — see §3). You don't choose these per resource; they're semantic properties of each subsystem.

> 💡 **Why you don't get a hundred tuning knobs.** The budget surface is deliberately tiny because the engine knows its own access patterns better than a config file does. You declare the *envelope* (how much memory it may use, where the data lives, whether durability is on); it self-manages the rest. If you ever need to see *where* the budget is going, that's exactly what the resource graph in §1 shows you — observe first, then resize the one cap that matters.

---

## 3. 🧯 Error-handling ground rules

Typhon's error model is small and has three rules worth internalising before you write a `catch`.

**1. The engine throws; it never retries.** When an operation can't proceed — a lock timed out, the cache is under backpressure, a WAL write failed — it raises an exception and stops. Retry *policy* is yours: a game server skips the tick, a batch job waits and tries again, a test asserts it never happens. None of that belongs inside the engine.

**2. Catch `TyphonException`, route on `IsTransient`.** Every engine exception derives from `TyphonException` and carries an `ErrorCode` plus a transience hint:

```csharp
try
{
    using var tx = dbe.CreateQuickTransaction();
    tx.Spawn<Unit>(/* … */);
    tx.Commit();
}
catch (TyphonException ex) when (ex.IsTransient)
{
    // the resource may free up — backoff / retry / drop (your call)
}
catch (TyphonException ex)
{
    // terminal — log with ex.ErrorCode, alert, give up
    throw;
}
```

`IsTransient` is `true` for the things that might pass on a second look — timeouts (`TyphonTimeoutException` and friends) and `ResourceExhaustedException` — and `false` for the terminal ones (corruption, a fatal WAL write, a schema mismatch).

**3. The ones you'll actually meet.** Most engine exceptions are operational edge cases. The handful that come from *your own modeling* are worth recognising:

| Exception | You'll hit it when… |
|---|---|
| `UniqueConstraintViolationException` | inserting a duplicate key into a `[Index]` (unique) field ([ch.2](02-modeling.md)) |
| `SchemaValidationException` / `SchemaDowngradeException` | reopening a database with a changed struct and no revision bump, or with an *older* app than wrote it ([ch.2](02-modeling.md)) |
| `TyphonTimeoutException` | a lock or transaction blew its deadline under contention — transient |

> ⚠️ Typhon's `TyphonTimeoutException` does **not** derive from `System.TimeoutException` — catch the Typhon type. And don't write speculative `catch` blocks for exceptions the docs list as *reserved but not yet thrown* (`TransactionConflictException`, etc.) — they'd be dead code until those types ship.

You won't see the engine's hot-path `Result<,>` return type (used internally for "not found" / "not visible" so those routine outcomes don't pay for a thrown exception) — it surfaces as ordinary return values like a query that simply yields nothing.

---

## 4. 🗺️ Going deeper — the map

This guide is the *task-oriented* layer. When you need struct layouts, algorithms, invariants, or the *why* behind a design, the [**in-depth overview**](../in-depth-overview/README.md) is the contributor/power-user reference. The mapping from a guide chapter to where it goes deep:

| When you want… | Go to |
|---|---|
| The MVCC revision chain, visibility predicate | [05-revision](../in-depth-overview/05-revision.md) |
| ECS storage internals, generated accessors, archetype layout | [06-ecs](../in-depth-overview/06-ecs.md) |
| Schema diffing & the migration engine | [04-schema](../in-depth-overview/04-schema.md) |
| Index structures (B+Tree) and the query planner | [03-indexing](../in-depth-overview/03-indexing.md) · [09-querying](../in-depth-overview/09-querying.md) |
| The spatial grid + R-tree, broad-phase, margins | [07-spatial](../in-depth-overview/07-spatial.md) |
| Transaction/UoW mechanics, conflict detection | [08-transactions](../in-depth-overview/08-transactions.md) |
| The scheduler in full — DAG derivation, overload, fence | [10-runtime](../in-depth-overview/10-runtime.md) |
| WAL, checkpoint, crash recovery (FPI) | [11-durability](../in-depth-overview/11-durability.md) |
| The telemetry pipeline & profiler internals | [12-observability](../in-depth-overview/12-observability.md) |
| The resource graph & budgets in detail | [13-resources](../in-depth-overview/13-resources.md) |
| The full exception hierarchy & error codes | [14-errors](../in-depth-overview/14-errors.md) |
| Page cache, memory-mapped store, foundation primitives | [02-storage](../in-depth-overview/02-storage.md) · [01-foundation](../in-depth-overview/01-foundation.md) |

---

## 🎓 You've finished the guide

You can now declare a data model, choose the right storage mode per component, write and read it transactionally, query it (one-shot and reactive), run systems over it every tick in parallel, and operate the result. That's the whole arc — from your first spawned entity to a real-time engine you can observe and tune.

Where to go from here:
- **Build something** — the loop from [ch.1](01-first-app.md) is a real, runnable starting point; grow it.
- **Go deep** when you hit a wall — the **map** in §4 points at the exact reference chapter.

## 🧩 The types you'll touch

`AddTyphonObservabilityBridge` / `ObservabilityBridgeOptions` · `typhon.telemetry.json` (profiler config) · `.typhon-trace` (recorded run) / `.typhon-replay` (saved attach session) · the Workbench (`tools/Typhon.Workbench`, `http://localhost:5173`) · `DatabaseCacheSize` · `DatabaseEngineOptions.Resources` (`TotalMemoryBudgetBytes`) · `WalWriterOptions` · `TyphonException` (`ErrorCode` / `IsTransient`) · `TyphonTimeoutException` · `ResourceExhaustedException` · `UniqueConstraintViolationException` · `SchemaValidationException`.
