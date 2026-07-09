---
uid: feature-runtime-index
title: 'Runtime'
description: 'The Runtime is Typhon''s game-server execution layer: a DAG-scheduled, multi-threaded tick loop that dispatches developer-defined systems against the ECS…'
---

# Runtime
> The Runtime is Typhon's game-server execution layer: a DAG-scheduled, multi-threaded tick loop that dispatches developer-defined systems against the ECS data layer and automates their UoW/Transaction lifecycle. Systems declare phase and read/write access so the scheduler derives safe parallel execution automatically, scope dispatch to spatial simulation tiers, and skip entirely when nothing relevant changed. An overload detector and always-on per-tick telemetry let the server degrade gracefully under a load spike — throttling systems, slowing the tick rate, or signalling game code to shed load — instead of falling over.

> 🔬 **Recommended:** read [in-depth-overview/10-runtime.md](../../in-depth-overview/10-runtime.md) (Chapter 10: Runtime) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Tick-Based Execution Engine](tick-execution-engine/README.md) | `TyphonRuntime` — the top-level host that owns the tick loop, creates one UoW per tick and one Transaction per system automatically, and drives startup/crash-recovery/shutdown | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Execution Modes](tick-execution-engine/execution-modes.md) | Today: a fixed-timestep tick loop plus a single-threaded debug variant; event-driven and hybrid request/response modes are designed but not yet built | 🚧 Partial | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Worker Pool & Threading Model](tick-execution-engine/worker-pool-threading.md) | Core allocation between a dedicated tick-metronome thread and a worker pool that executes the system DAG in parallel | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Parallel Tick Fence](tick-execution-engine/parallel-tick-fence.md) | Spreads the post-tick `WriteTickFence` step (cluster migrations, AABB refresh, WAL publish) across the worker pool instead of running it on one thread — tuned via `RuntimeOptions` | ✅ Implemented | 🟣 Advanced |
| [Declarative System Scheduling (Track → DAG → Phase, Auto-DAG)](declarative-system-scheduling.md) | Systems declare per-component read/write access and a DAG-local phase; the scheduler auto-derives execution edges and rejects unsafe write/write or stale-read conflicts at `Build()` | ✅ Implemented | 🔵 Core |
| [System Types](system-types/README.md) | Five system base classes a developer picks per piece of game logic — proactive callbacks, reactive entity queries, chunk-parallel non-entity work, multi-stage pipelines, and sub-system grouping | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [CallbackSystem](system-types/callback-system.md) | Proactive system that runs every tick for non-entity work — timers, input draining, global state | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [QuerySystem](system-types/query-system.md) | Reactive per-entity system that auto-skips when nothing relevant changed, with optional automatic multi-core chunking | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [ChunkedCallbackSystem](system-types/chunked-callback-system.md) | Fan a CallbackSystem's body out across N workers for SIMD sweeps, reductions, and other non-entity chunkable work | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [PipelineSystem](system-types/pipeline-system.md) | Reactive multi-stage gather/process/scatter system for bulk entity processing — full execution model pending Patate | ✅ Implemented (chunk-dispatch only) | 🟣 Advanced |
| &nbsp;&nbsp;↳ [CompoundSystem](system-types/compound-system.md) | Group related sub-systems' registration under one `Configure` call — one node from the outside, parallel inside | ✅ Implemented | 🟣 Advanced |
| [Parallel Entity Processing (QuerySystem.Parallel)](parallel-entity-processing.md) | Automatic multi-core chunking with a reusable `PointInTimeAccessor` (no per-chunk Transaction for non-Versioned writes) across four dispatch paths selected by Versioned-write × change-filter | ✅ Implemented | 🟣 Advanced |
| [Reactive Dispatch: Change Filters & Run Conditions](reactive-dispatch-change-filters.md) | `changeFilter` limits a system's entity set to dirty ∪ Added by piggybacking on the View's ring buffer; `shouldRun` gives a zero-cost proactive skip predicate evaluated before any input work | ✅ Implemented | 🟣 Advanced |
| [Typed Event Queues](typed-event-queues.md) | Single-producer ring-buffer queues for inter-system signalling within a tick, enabling reactive cascade chains that early-out cheaply when dormant | ✅ Implemented | 🟣 Advanced |
| [Side-Transactions for Immediate Durability](side-transactions.md) | Per-tick `CreateSideTransaction(Immediate)` lets a system commit economy-critical writes durably mid-tick, independent of and invisible to the main tick UoW's snapshot | ✅ Implemented | 🟣 Advanced |
| [Overload Management](overload-management.md) | Single-writer overload state machine that escalates/de-escalates through system throttling and tick-rate modulation (TiDi, up to 6x) and fires a critical-overload callback for game-decided player shedding | 🚧 Partial | 🟣 Advanced |
| [Telemetry & Runtime Inspection](telemetry-runtime-inspection.md) | Always-on, zero-allocation ring buffer of per-tick/per-system telemetry inspectable from game code; a pluggable `IRuntimeInspector` hook for remote tooling is designed but not implemented | 🚧 Partial | 🟣 Advanced |
| [Spatial Tiers & Adaptive Dispatch](spatial-tiers-adaptive-dispatch/README.md) | Per-cluster simulation tiers let systems process near entities every tick and far entities at reduced/amortized/dormant rates, scoping dispatch automatically to the matching clusters | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Tier-Filtered & Amortized Dispatch](spatial-tiers-adaptive-dispatch/tier-filtered-amortized-dispatch.md) | Scope a system to clusters in matching tier cells, and optionally process just 1/N of them per tick | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Cluster Dormancy (Sleep/Wake)](spatial-tiers-adaptive-dispatch/cluster-dormancy.md) | Clusters untouched for N ticks sleep and are skipped by every dispatch path, waking within one tick of being written to | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Checkerboard (Red/Black) Dispatch](spatial-tiers-adaptive-dispatch/checkerboard-dispatch.md) | Two-phase Red/Black parallel dispatch so neighbor-touching systems never race across a cell boundary | ✅ Implemented | 🟣 Advanced |
| [Data-Driven Timers / Scheduled Entities](data-driven-timers.md) | Documented pattern for modeling respawns/expiries as entities with a time-of-expiry component and a `CallbackSystem` poll; no built-in timer/scheduling infrastructure exists yet | 📋 Planned | 🟣 Advanced |

## Internal Features

*No internal-only engine machinery documented separately in this category — every feature file above is directly reached from application code (`TyphonRuntime`, `SystemBuilder`, `TickContext`, `RuntimeOptions`, `Transaction`). The DAG deriver, overload detector, dormancy reporter, and access validator that back these features are implementation detail behind them, not separately catalogued.*