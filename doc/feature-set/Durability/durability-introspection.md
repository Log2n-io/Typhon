---
uid: feature-durability-durability-introspection
title: 'Durability Health & Introspection'
description: 'See whether WAL/checkpoint durability is keeping up — health state, cycle stats, honest watermarks — without reaching into engine internals.'
---

# Durability Health & Introspection
> See whether WAL/checkpoint durability is keeping up — health state, cycle stats, honest watermarks — without reaching into engine internals.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)

## 🎯 What it solves

"Are commits actually becoming durable, or is checkpoint stuck?" needs an answer operators and tooling (Workbench) can get without parsing WAL segment files or holding a reference to the engine's internal types. A transient I/O stall, a writer pinning a dirty page past the coverage gate, and a fatal disk error each call for a different operator response, but look identical from the outside unless the engine classifies and reports them as they happen.

## ⚙️ How it works (in brief)

Every checkpoint cycle ends in one of three `DurabilityHealth` states: `Ok`; `Degraded` — a transient stall (back-pressure, lock, or I/O timeout) that retries next cycle, no data at risk; or `Fatal` — a non-transient error that halts periodic checkpointing (shutdown still attempts one last-chance flush). State transitions are reported through the engine's own `ILogger`, so they land wherever the host application already routes its logs — no separate sink to wire up. Underneath, the engine continuously holds three watermarks in strict order — `CheckpointLSN ≤ DurableLsn ≤ LastAppendedLsn` — the basis both the health classification and the checkpoint coverage gate reason from; `DurableLsn` is derived from per-slot LSNs recorded at WAL publish time, not a coarse "highest claimed" guess, so it never claims a record durable before it is actually fsynced. Checkpoint-cycle counters (cycles run, pages written, segments recycled, cycle duration) and WAL-writer counters (bytes written, flushes, flush latency) are reported as nodes in the engine's Resource Graph, queryable the same way as any other subsystem.

## 💻 Usage

```csharp
// 1. Health transitions ride the engine's own logger — wire it once at startup, nothing durability-specific to opt into.
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole();
builder.Services
    .AddScopedManagedPagedMemoryMappedFile(o => { o.DatabaseName = "skirmish"; o.DatabaseDirectory = "."; })
    .AddScopedDatabaseEngine();
// A transient stall logs: "Checkpoint cycle hit a transient failure (Health=Degraded); retrying on the next cycle"
// A non-transient failure logs Health=Fatal and halts periodic checkpointing.

// 2. Checkpoint/WAL cycle counters via the Resource Graph (same pattern as any IMetricSource node).
builder.Services.AddSingleton<IResourceGraph>(sp => new ResourceGraph(sp.GetRequiredService<IResourceRegistry>()));
// ...
ResourceSnapshot snapshot = resourceGraph.GetSnapshot();
var ckpt = snapshot.Nodes["Durability/CheckpointManager"];
long cycles = ckpt.Throughput.First(t => t.Name == "Checkpoints").Count;
long lastCycleUs = ckpt.Duration.First(d => d.Name == "CheckpointDuration").LastUs;

var walWriter = snapshot.Nodes["Durability/WalManager/WalWriter"];
long bytesFlushed = walWriter.Throughput.First(t => t.Name == "BytesWritten").Count;
```

## ⚠️ Guarantees & limits

- **Three health states, never silently stuck** — `Degraded` always retries next cycle; `Fatal` halts periodic checkpointing, but a last-chance flush still runs at shutdown (rule CK-06).
- **Honest watermark ordering always holds** — `CheckpointLSN ≤ DurableLsn ≤ LastAppendedLsn`, continuously, not just at the moments you happen to check.
- **Cycle/WAL-writer counters are public today** via the Resource Graph (`Durability/CheckpointManager`, `Durability/WalManager/WalWriter` nodes) — wire the [Resource Graph Metrics Bridge](../Observability/otel-metrics-export/resource-graph-metrics-bridge.md) to push them into Prometheus/OTLP instead of polling manually.
- **Exact LSN values and commit-buffer occupancy are not yet on that public surface** — today they drive the engine's own internal retry/halt/coverage-gate decisions and are visible at session open/close via a WAL-watermarks log line; a unified snapshot exposing `CheckpointLsn`/`DurableLsn`/`LastAppendedLsn`/buffer occupancy directly in one call is designed (MinimalWal `01-architecture.md` §7) but not yet implemented.
- **Health is per-checkpoint-cycle, not per-commit** — a single slow commit under `Immediate` durability surfaces its own `WalBackPressureTimeoutException` / `CommitDurabilityUncertainException`, not a `DurabilityHealth` transition.
- **`DurabilityHealth` is distinct from the generic Resource Health Checks** (Healthy/Degraded/Unhealthy by capacity-utilization threshold) — checkpoint cycles don't report a `Capacity` metric, so they don't participate in that composite verdict today.

## 🧪 Tests

- [CheckpointResilienceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CheckpointResilienceTests.cs) — `Degraded` transient-fault retry vs. `Fatal` halt-with-last-chance-flush health transitions
- [DurableLsnHonestyTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/DurableLsnHonestyTests.cs) — `DurableLsn` never exceeds the highest LSN physically written (the honest-watermark fix, LOG-05)

## 🔗 Related

- Related feature: [Checkpoint v2 (SnapshotStore pipeline)](./checkpoint-v2/README.md), [Metric Reporting (IMetricSource / IMetricWriter)](../Resources/metric-reporting.md), [OpenTelemetry Metrics Export](../Observability/otel-metrics-export/README.md)

<!-- Deep dive: claude/overview/06-durability.md — Honest Watermarks, claude/design/Durability/MinimalWal/01-architecture.md §7 -->
<!-- Rules: claude/rules/durability.md — CK-06, LOG-05 -->
