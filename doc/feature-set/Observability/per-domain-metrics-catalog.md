---
uid: feature-observability-per-domain-metrics-catalog
title: 'Per-Domain Named Metrics Catalog'
description: 'A documented target list of ~40 fixed-name OTel instruments (typhon.tx., typhon.wal., typhon.lock.*...) — not yet wired to any exporter.'
---

# Per-Domain Named Metrics Catalog
> A documented target list of ~40 fixed-name OTel instruments (typhon.tx.*, typhon.wal.*, typhon.lock.*...) — not yet wired to any exporter.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Observability](./README.md)

## 🎯 What it solves

Dashboards and alert rules are easiest to build against stable, well-known metric names — `typhon.tx.committed`,
`typhon.wal.ring_fill`, `typhon.lock.contentions` — rather than against dynamically-generated, path-tagged metrics
that change shape as resources are added or renamed. The engine's high-value domains (transactions, page cache,
disk I/O, locking, B+Tree indexes, WAL/checkpoint, allocation, backup) don't yet have such a fixed surface: today's
exporters report whatever the Resource Graph happens to track, tagged by resource path, which is great for generic
coverage but not for a dashboard author who wants one unchanging name per concept.

## ⚙️ How it works (in brief)

The architecture overview's Metrics Catalog enumerates ~40 instruments across eight domains — Transaction, Page
Cache, I/O, Concurrency/AccessControl, B+Tree Index, WAL/Durability, Allocation Infrastructure, and Backup — each
with a fixed name, an OTel instrument type (Counter, Histogram, or Gauge), a description, and (where relevant) a
cross-reference to a Resource Taxonomy node. This is presently a documentation-only design target: no `Meter` in
the codebase registers any of these specific names. The two exporters that do exist
(`ResourceMetricsExporter`, `EcsMetricsExporter`) use a different, generic naming scheme (`typhon.resource.*`,
`typhon.ecs.*`) built from resource paths and metric kinds, not the fixed per-domain names catalogued here.

## 💻 Usage

Not implemented — no Meter registers these names today; nothing below compiles against the current API.

```csharp
// Illustrative only — not a real/current Typhon API. No exporter registers these instruments today.
// var meter = new Meter("Typhon.Engine", "1.0.0");
//
// var txCommitted = meter.CreateCounter<long>("typhon.tx.committed",
//     unit: "{transactions}", description: "Successful commits");
//
// var walFlushDuration = meter.CreateHistogram<double>("typhon.wal.flush_duration_us",
//     unit: "us", description: "FUA write latency (us)");
//
// var ringFill = meter.CreateObservableGauge("typhon.wal.ring_fill",
//     () => walRingBuffer.FillRatio, unit: "1", description: "Ring buffer utilization (0.0-1.0)");
```

| Domain | Example names | Types |
|---|---|---|
| Transaction | `typhon.tx.count`, `typhon.tx.committed`, `typhon.tx.duration_us` | Counter, Histogram, Gauge |
| Page Cache | `typhon.cache.hits`, `typhon.cache.hit_ratio`, `typhon.cache.evictions` | Counter, Gauge |
| I/O | `typhon.io.page_reads`, `typhon.io.read_duration_us` | Counter, Histogram |
| Concurrency / AccessControl | `typhon.lock.acquisitions`, `typhon.lock.contention_duration_us` | Counter, Histogram |
| B+Tree Index | `typhon.index.lookups`, `typhon.index.node_splits` | Counter, Gauge |
| WAL / Durability | `typhon.wal.flushes`, `typhon.checkpoint.duration_ms` | Counter, Histogram, Gauge |
| Allocation Infrastructure | `typhon.alloc.segment_pages`, `typhon.alloc.bitmap_scans` | Gauge, Counter |
| Backup | `typhon.backup.snapshot_duration_ms`, `typhon.backup.pages_changed` | Histogram, Counter |

## ⚠️ Guarantees & limits

- **Not implemented.** None of the ~40 catalogued names are registered as OTel instruments anywhere in
  `src/Typhon.Engine/Observability` today — the only live meters are `Typhon.Resources` (resource-path-tagged) and
  `Typhon.ECS` (per-archetype/per-component gauges), both with naming schemes distinct from this catalog.
- Intended to coexist with, not replace, the generic [Resource Graph Metrics Bridge](./otel-metrics-export/resource-graph-metrics-bridge.md)
  bridge: the catalog targets fixed, dashboard-stable names for the highest-value domains; the resource bridge
  stays dynamic/generic for everything else.
- No decision yet on emission mechanism — wiring live `Counter`/`Histogram` calls into hot paths (tx commit, lock
  acquire, B+Tree split) has a real per-call cost on a microsecond-budget engine, versus exposing the same data as
  observable gauges/counters read from existing counters at scrape time (the pattern the two live exporters use).
  That tradeoff is unresolved and will shape the final design.
- Names, types, and descriptions in the linked catalog table are a design target, not a frozen API — treat them as
  subject to change before implementation begins.

## 🔗 Related

- Related feature: [Resource Graph Metrics Bridge](./otel-metrics-export/resource-graph-metrics-bridge.md) — the exporter that IS implemented today, with a different naming scheme
- Sibling: [ECS Metrics Exporter](./otel-metrics-export/ecs-metrics-exporter.md) — the other exporter that already ships, using per-archetype/component naming instead of this catalog's fixed per-domain names
- Related features: [Resource-Aware Health Checks](./health-checks.md), [Threshold-Based Resource Alerting](./threshold-alerting.md) — consume the same Resource Graph data this catalog would expose under fixed names

<!-- Deep dive: claude/overview/09-observability.md §9.2 Metrics, Metrics Catalog -->
