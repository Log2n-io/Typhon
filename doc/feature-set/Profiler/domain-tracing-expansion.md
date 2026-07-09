---
uid: feature-profiler-domain-tracing-expansion
title: 'Domain-Specific Tracing Instrumentation Expansion'
description: 'Nine new domains of forensic-grade tracing — locks, spatial, scheduler, storage, MVCC, query, durability — zero cost when off.'
---

# Domain-Specific Tracing Instrumentation Expansion
> Nine new domains of forensic-grade tracing — locks, spatial, scheduler, storage, MVCC, query, durability — zero cost when off.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves

The original built-in instrumentation covers nine coarse-grained subsystems (transaction commit, B+Tree ops,
page cache, WAL, checkpoint, ...) but stops short of the questions you ask once a workload is deployed and you
can't attach a debugger: which thread waited on which lock, why a spatial query traversed so many R-Tree nodes,
how long a scheduler worker sat idle, or where in the MVCC chain a read stalled. Getting that detail used to
mean adding ad hoc instrumentation, reproducing the problem, then ripping the instrumentation back out. This
feature extends the same always-on tracing pipeline into nine additional engine domains so that level of
forensic detail is a config flag away, not a code change.

## ⚙️ How it works (in brief)

Every new event kind is gated by a two-tier compile-time scheme layered on top of the master `Profiler:Enabled`
switch: Tier 2 adds one `static readonly bool` flag per category, resolved once from a nested JSON tree
(`Profiler:<Domain>:<Category>:<Leaf>:Enabled`) at class load — a child inherits its parent's effective state
unless it overrides it. Because the flags are `static readonly`, the JIT proves a disabled branch is dead code
and deletes the guarded call outright; a release-build IL dead-code-elimination test enforces this for every
category before it ships — the same proof discipline the base [toggle mechanism](./profiler-configuration-tuning.md)
already applies. Call sites whose method body is sub-100ns or fires over 100k times/sec require the Tier-2 gate;
low-frequency sites can rely on the parent domain gate alone. The nine domains — Concurrency, Storage, Memory,
Data (Transaction/MVCC/BTree), Query, ECS, Spatial, Scheduler/Runtime, Durability, and Subscription dispatch —
each landed as an independent phase, so coverage depth varies: some categories have every emission site wired,
others ship the wire format and config flag with the actual producer call deferred to a follow-up.

## 💻 Usage

```csharp
// typhon.telemetry.json — turn on forensic detail only for the domains you're investigating;
// everything else stays off (and costs 0 ns) by default:
// {
//   "Typhon": {
//     "Profiler": {
//       "Enabled": true,
//       "Data":        { "Enabled": true },                  // Transaction/MVCC/BTree slow paths
//       "Durability":  { "Enabled": true },                  // WAL split spans, Checkpoint, Recovery
//       "Spatial":     { "Enabled": true, "Query": { "Enabled": true } },
//       "Concurrency": { "Enabled": false }                  // leave lock tracing off — extreme frequency
//     }
//   }
// }

using Typhon.Engine;

TelemetryConfig.EnsureInitialized();
Console.WriteLine(TelemetryConfig.GetConfigurationSummary());

// Gates are public static fields, one per category/leaf — same shape as the original nine subsystems:
if (TelemetryConfig.DataMvccChainWalkActive)
{
    Console.WriteLine("MVCC slow-path chain walks are landing in the trace.");
}
```

| Domain root | Default | Coverage in this drop |
|---|---|---|
| `Typhon:Profiler:Concurrency:Enabled` | `false` | AccessControl / AccessControlSmall / ResourceAccessControl / Epoch / AdaptiveWaiter / OlcLatch — fully wired |
| `Typhon:Profiler:Spatial:Enabled` | `false` | Query / RTree / Grid / Cell / ClusterMigration / TierIndex / Maintain / Trigger — fully wired |
| `Typhon:Profiler:Storage:Enabled` / `:Memory:Enabled` | `false` | Segment / FileHandle / OccupancyMap / DirtyWalk / AlignmentWaste — fully wired |
| `Typhon:Profiler:Data:Enabled` | `false` | Transaction lifecycle + MVCC cleanup wired; BTree Search/RangeScan/NodeCow ship gate + codec, producer call deferred |
| `Typhon:Profiler:Query:Enabled` / `:ECS:Enabled` | `false` | Parse/Plan and low-frequency ECS paths wired; hot-loop Iterate/Filter/ProcessEntry deferred |
| `Typhon:Profiler:Scheduler:Enabled` / `:Runtime:Enabled` | `true`\* / `false` | System / Worker / Overload / Graph + UoW phase markers wired |
| `Typhon:Profiler:Durability:Enabled` | `false` | WAL split spans + Recovery phase set wired; extreme-frequency `Frame` / `Record` / `UoW:State` ship gate + codec only |

\* `Scheduler` keeps its pre-expansion default; the new leaves underneath it default off.

## ⚠️ Guarantees & limits

- **Zero cost when off, proven not assumed** — every Tier-2 leaf added by this expansion is covered by the same
  release-build IL-elimination test that gates the profiler's toggle mechanism; a category that fails the test
  doesn't ship.
- **"Partial" means uneven producer wiring, not uneven design** — every domain's wire IDs, codecs, and config
  flags are landed; several extreme-frequency call sites (BTree hot paths, ECS `ProcessEntry`, per-subscriber
  dispatch, WAL `Frame`, `Recovery:Record`, `UoW:State`/`:Deadline`) ship the format and gate but defer the
  actual emission call to a follow-up — flipping their flag on today produces no events yet.
- **Wire IDs never renumber** — new domains occupy fresh, append-only `TraceEventKind` ranges; existing kinds
  that gained payload fields (e.g. `TransactionRollback`'s reason byte, `PageEvicted`'s dirty bit) did so
  wire-additively, so older `.typhon-trace` files still decode.
- **Same consumer pipeline** — new events flow through the existing decoder/exporter/Workbench trace-viewer
  path; no new tooling is required to view them.
- **Config resolves once at class load** — editing `typhon.telemetry.json` or the `TYPHON__PROFILER__*` env
  vars after the process starts has no effect until restart.
- **Extreme-frequency leaves stay deny-listed even when their parent category is on** — e.g. per-step
  `AdaptiveWaiter` events and `Durability:Recovery:Record` stay off under a broad `Enabled: true` to avoid
  saturating the trace ring; they require explicitly enabling the leaf.

## 🧪 Tests

- [TelemetryConfigResolverTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Observability/TelemetryConfigResolverTests.cs) — the Tier-2 parent-implies-children resolution formula: parent off disables children even if explicitly enabled, explicit leaf override wins when the parent is on
- [ConcurrencyTracingStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/ConcurrencyTracingStressTests.cs) — end-to-end stress test for the Concurrency domain under real `AccessControl` contention; `[Explicit]` since it needs the domain's flag enabled via env var/JSON, with the fixture's own doc comment showing how

## 🔗 Related

- Sibling features: [Built-in Engine Instrumentation Catalog](./builtin-subsystem-instrumentation.md) (the original nine-subsystem baseline this expansion builds on), [Configuration & Performance Tuning](./profiler-configuration-tuning.md) (the gating mechanism these new flags plug into)
- Source: `src/Typhon.Engine/Observability/public/TelemetryConfig.cs`, `src/Typhon.Engine/Profiler/internals/{SpatialQueryEvents,SpatialMaintainEvents,SpatialRTreeEvents,SpatialMiscEvents,DataPlaneEvents,DurabilityEvents,StorageMemoryEvents,SubscriptionDispatchEvents}.cs`

<!-- Deep dive: claude/design/Profiler/07-tracing-instrumentation/README.md — umbrella (architecture, category tree, phasing, decisions) -->
<!-- Per-domain design: claude/design/Profiler/07-tracing-instrumentation/01-tier2-gating-infrastructure.md, 02-concurrency.md, 03-spatial.md, 04-scheduler-runtime.md, 05-storage-memory.md, 06-data-plane.md, 07-query-ecs-view.md, 08-durability.md, 09-subscription-dispatch.md -->
<!-- ADR: 019 — runtime telemetry toggle (JIT dead-code-elimination) (claude/adr/019-runtime-telemetry-toggle.md) -->
