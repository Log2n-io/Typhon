# System Types
> Five system base classes for every shape of per-tick work — proactive, reactive, chunk-parallel, multi-stage, and grouped.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Tick logic comes in fundamentally different shapes: proactive non-entity work (timers, draining input queues,
global state), reactive per-entity work that should skip entirely when nothing relevant changed, chunkable
non-entity work that wants worker-pool parallelism without paying for entity machinery, bulk multi-stage entity
processing, and grouping of related systems into one schedulable unit. Forcing all of this through a single
system shape means either paying View/Transaction overhead for logic that touches no entities, or hand-rolling
skip checks and chunk distribution everywhere. The five system types let each piece of logic declare the
execution shape it actually needs.

## ⚙️ How it works (in brief)

Every system is a class deriving from one of five base types, implementing `Configure(SystemBuilder b)`
(name, dependencies, input, access declarations) and — except `CompoundSystem` — `Execute(TickContext ctx)`.
`CallbackSystem` is proactive (runs every tick); `QuerySystem` and `PipelineSystem` are reactive (skip when
their input has no work); `ChunkedCallbackSystem` is a `CallbackSystem` variant that fans `Execute` out across
N workers; `CompoundSystem` groups sibling systems' registration under one `Configure` call. Lambda shorthand
(`dag.CallbackSystem(name, ctx => ..., ...)`, `dag.QuerySystem(...)`, `dag.PipelineSystem(...)`) registers the
same kinds directly on a `Dag` without a class, for logic too small to justify one. Both styles coexist in the
same DAG and the same tick; class-based registration is required to use RFC-07 access declarations (`Reads<T>`,
`Writes<T>`, ...) that drive automatic DAG-edge derivation.

## Sub-features

| Sub-feature | Use it for |
|-------------|-----------|
| [CallbackSystem](./callback-system.md) | Proactive non-entity work that must run every tick — timers, input draining, global state |
| [QuerySystem](./query-system.md) | Reactive per-entity work, single-worker or auto-chunked across cores via `Parallel()` |
| [ChunkedCallbackSystem](./chunked-callback-system.md) | Chunkable non-entity work (SIMD sweeps, parallel reductions) that doesn't need entity iteration |
| [PipelineSystem](./pipeline-system.md) | Bulk multi-stage entity processing (gather → process → scatter) — execution model pending Patate |
| [CompoundSystem](./compound-system.md) | Bundling related sub-systems' registration into a single `Configure` call |

## ⚠️ Guarantees & limits

- `CallbackSystem` (and `ChunkedCallbackSystem`) is the only proactive kind — runs every tick unless
  `b.ShouldRun(...)` returns false. `QuerySystem` and `PipelineSystem` are reactive.
- Each dispatched `CallbackSystem`/`QuerySystem` (or chunk, for `Parallel()`) gets its own `Transaction`,
  created on the executing worker and auto-committed after `Execute` returns — systems never call
  `Commit()`/`Dispose()` themselves.
- A thrown exception rolls back that system's own Transaction and skips its successors
  (`SkipReason.DependencyFailed`); independent DAG branches still execute — one failure doesn't abort the tick.
- `CompoundSystem.Add(...)` expands each sub-system into its own DAG registration at `dag.Add(compound)` time —
  there is no aggregate group name; from outside the compound is atomic (all children complete before the
  compound's successors start), but other systems depend on individual sub-system names, not the group.
- `Build()` rejects at startup: a lambda/class system with no name, `Parallel()`/`ChangeFilter` without an
  `Input` View, `ChangeFilter`/`Input` on a `CallbackSystem`, `ChunkedParallel` combined with any entity-context
  concept, duplicate names, and dependency cycles.
- `PipelineSystem`'s full gather/process/scatter execution model is not implemented yet — see its sub-feature
  doc for exactly what works today.

## 🧪 Tests

- [ClassBasedSystemTests](../../../../test/Typhon.Engine.Tests/Runtime/ClassBasedSystemTests.cs) — all five base types registered side-by-side, mixed lambda/class dispatch, name/null validation
- [ScheduleValidationTests](../../../../test/Typhon.Engine.Tests/Runtime/ScheduleValidationTests.cs) — `Build()`-time rejections shared across system types (duplicate names, invalid `ChunksPerWorker`, `Parallel`/`ChangeFilter` without `Input`)

## 🔗 Related

- Sub-features: [CallbackSystem](./callback-system.md), [QuerySystem](./query-system.md),
  [ChunkedCallbackSystem](./chunked-callback-system.md), [PipelineSystem](./pipeline-system.md),
  [CompoundSystem](./compound-system.md)

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md -->
<!-- Deep dive: claude/overview/13-runtime.md -->
<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md -->
