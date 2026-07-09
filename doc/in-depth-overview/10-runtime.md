---
uid: overview-runtime
title: '10 — Runtime'
description: 'Runtime is the engine''s heartbeat — a tick-driven scheduler that runs a static DAG of systems on a worker pool. A dedicated timer thread (the TickDriver)…'
---

# 10 — Runtime

**Code:** [`src/Typhon.Engine/Runtime/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Runtime)

Runtime is the engine's heartbeat — a tick-driven scheduler that runs a static **DAG** of systems on a worker pool. A dedicated timer thread (the `TickDriver`) beats at a fixed rate; workers wake on each beat, dispatch ready systems by their access compatibility, and go back to sleep. There is no central dispatcher thread: every dispatch decision happens on a worker, in parallel.

If you've used Unity DOTS, Bevy, or any modern ECS scheduler, the shape will feel familiar — but Typhon's runtime sits on top of a **single-transaction-per-system** discipline ([08-transactions](08-transactions.md)) and emits per-tick fence work that the WAL must observe ([11-durability](11-durability.md)). The model is opinionated: each tick is one frame of simulation, one UoW, deterministic ordering between phases.

> No runtime-specific D2 diagram exists yet. The four-level `Track → DAG → Phase → System` hierarchy is a natural candidate; a future revision of this doc will add one.

---

## 1. The model

| Concept | What it is |
|---|---|
| **Tick** | One simulation frame. Driven by `TickDriver`, target rate set by `RuntimeOptions.BaseTickRate` (default 60 Hz). |
| **Track** | An ordered, tagged container of DAGs. Tracks run sequentially: every DAG of track *N* completes before any DAG of track *N+1* starts. |
| **DAG** | A dependency graph of systems. DAGs within one track are independent. |
| **Phase** | DAG-local ordering bucket. Systems in phase *N* finish before any system in phase *N+1* of the same DAG. |
| **System** | The unit of work. One of `CallbackSystem`, `QuerySystem`, `PipelineSystem` ([§5](#5-systems)). |
| **Worker** | A `Typhon.Worker-{i}` thread that picks ready systems off the DAG and runs them. |

The execution sequence at runtime is: **TickDriver fires → workers wake → for each track in order, workers find ready systems (predecessors complete + access-compatible) and run them → tick-end hooks run → workers sleep until the next beat.**

---

## 2. TickDriver — the metronome

[`Runtime/public/DagScheduler.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/DagScheduler.cs) derives from [`HighResolutionTimerServiceBase`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/HighResolutionTimerServiceBase.cs) ([01-foundation §3](01-foundation.md)).

| Property | Value |
|---|---|
| Thread name | `Typhon.TickDriver` |
| Priority | `ThreadPriority.AboveNormal`, `IsBackground = true` |
| Wait strategy | **3-phase Sleep → Yield → Spin** (inherited from the timer base) |
| Spin threshold | **50 µs** (`Stopwatch.Frequency * 0.000_050`) |
| Sleep threshold | **1.5 × calibrated worst-case `Thread.Sleep(1)` duration**, measured once at construction |

The three phases are picked by remaining time to the next scheduled tick:

1. **Sleep** — `Thread.Sleep(1)` while remaining > sleep threshold (cheap; coarse).
2. **Yield** — `Thread.Yield()` while between sleep threshold and 50 µs.
3. **Spin** — `Thread.SpinWait` for the last 50 µs to nail wake latency.

### Metronome advance

The driver never accumulates drift. After each tick, `GetNextTick` advances the target absolutely:

```csharp
_nextTickTimestamp += _tickIntervalTicks * _tickMultiplier;
_tickIntervalTicks  = Stopwatch.Frequency / options.BaseTickRate;
```

`_tickMultiplier` is `1` under normal load; the overload detector can bump it ([§8](#8-overload-management)). On every tick advance the driver emits a `Scheduler.Overload.TickMultiplier` instant ([12-observability](12-observability.md)).

On wait completion the base class invokes the `OnWaitComplete` hook, which emits a `SchedulerMetronomeWait` span tagged with `intentClass = CatchUp | Throttled | Headroom` so a trace can answer *why* the metronome was waiting.

---

## 3. Tracks

[`Runtime/public/Track.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/Track.cs), [`Runtime/public/RuntimeSchedule.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/RuntimeSchedule.cs)

Every schedule has three built-in tracks in execution order:

| Track | Tags | What lives there |
|---|---|---|
| **`Engine-Pre`** | `engine` | Engine work before the app (currently empty — declared for symmetry). |
| **`Public`** | — | The app's track. This is where game code declares its DAGs. |
| **`Engine-Post`** | `engine` | Engine work after the app — currently the parallel `WriteTickFence` DAG ([§7](#7-parallel-fence)). |

The string `EngineTag = "engine"` (constant on `Track`) marks engine-internal tracks; tooling hides their systems from default views, and `DagScheduler.SystemCount` reports user-registered systems only (vs. `AllSystemCount` for everything).

Apps may declare additional tracks via `RuntimeSchedule.DeclareTrack(name, tags…)`; they slot into the app region between `Public` and `Engine-Post` in declaration order. Names starting with `Engine-` are reserved.

### Declaring a DAG

```csharp
var schedule = RuntimeSchedule.Create(options);
schedule.PublicTrack
    .DeclareDag("Game")
    .Phases(new Phase("Input"), new Phase("Simulate"), new Phase("Output"))
    .Add(new MoveAntsSystem())
    .Add(new EatFoodSystem())
    .Add(new SpawnAntsSystem());
var scheduler = schedule.Build(parent: registry.Runtime, logger);
```

`DeclareDag` is mandatory — there is no default-DAG convenience. Within a DAG, phase order is a hard barrier; within a phase, edges come from explicit `.After()` / `.Before()` declarations and from access-derived dependencies ([§4](#4-the-dag)).

---

## 4. The DAG

[`Runtime/public/Dag.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/Dag.cs), [`Runtime/public/SystemDefinition.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/SystemDefinition.cs)

A DAG is built once at `RuntimeSchedule.Build()` time and never mutates. Each system gets a `SystemDefinition` — immutable except for a few per-tick fields:

| Field | Purpose |
|---|---|
| `Name`, `Type`, `Index` | Identity. `Index` is the canonical slot in `DagScheduler.Systems`. |
| `DagId`, `Phase`, `PhaseIndex` | Owning DAG, resolved phase, DAG-local phase index. |
| `Successors`, `PredecessorCount` | Graph topology computed by `DagBuilder.Build`. |
| `TotalChunks` | Static chunk count for pipeline/parallel systems. |
| **`RuntimeChunkCount`** | Per-tick override set by `OnPrepare` for chunked-callback systems (fence work-planner uses this to size `FenceExec`). |
| **`ExplicitChunkCount`** | Static chunk count from `SystemBuilder.ChunkedParallel(N)`. Zero = "derive from entity count". |
| `Access` | The `SystemAccessDescriptor` populated from `b.Reads<T>()` / `b.Writes<T>()` declarations. |

### `AccessDagDeriver` — derive edges from declared access

[`Runtime/internals/AccessDagDeriver.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/internals/AccessDagDeriver.cs)

Once phases and explicit edges are known, the deriver walks each DAG's systems and:

1. **Validates conflicts** as hard errors (W×W with no ordering, R×W plain without `ReadsFresh` / `ReadsSnapshot`, resource W×W, `ExclusivePhase` violations). `ReadsSnapshot` on a non-`Versioned` component (`SingleVersion` or `Transient`) is *also* a hard `Build()`-time error — SV/Transient have no per-tick consistent snapshot to give, regardless of `DurabilityDiscipline` (rule AC-05 / CM-04).
2. **Emits intra-phase edges**: `ReadsFresh` ⇒ writer-before-reader, `ReadsSnapshot` ⇒ reader-before-writer (snapshot is the previous-tick value, so the writer can run concurrently *after* the reader started), event producer-before-consumer, resource R/W ordering.
3. **Emits cross-phase edges only on conflict** (post-2026-05-07 change): a phase-(N+1) system with no access conflict against a phase-N system can run concurrently with it. Phase order is still a coarse contract, but no longer an all-to-all barrier.

The result is a single static graph the scheduler walks every tick.

---

## 5. Systems

[`Runtime/public/SystemType.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/SystemType.cs)

Three execution shapes:

| Type | Shape | Receives `TickContext` | Transaction |
|---|---|---|---|
| **`CallbackSystem`** | Single inline invocation on the dispatching worker. Use for input, cleanup, timers, non-entity work. | Yes | One per system (scheduler-managed) |
| **`QuerySystem`** | Single worker iterates an input View. Add `.Parallel()` to dispatch chunks across workers. | Yes | One per system (or per chunk if parallel) |
| **`PipelineSystem`** | Multi-worker chunked dispatch via atomic counter — `Action<int chunkIndex, int totalChunks>`. Gather/Scatter pipelines handle entity access separately. | **No** | **None** — pipeline has no Transaction at all |

### Single Transaction per system, single-thread-affine

This is the core discipline. Each `CallbackSystem` / `QuerySystem` invocation:

1. Lands on one worker thread (chosen by the dispatcher).
2. Receives a freshly-created `Transaction` on that worker (`SystemStartCallback`).
3. Runs the user body.
4. Has its `Transaction` committed and disposed by the scheduler (`SystemEndCallback`).

The user code **must not** commit or dispose its `Transaction` — the scheduler owns the lifecycle. The same applies to the per-tick `UnitOfWork`, created in `TickStartCallback` and flushed/disposed in `TickEndCallback`.

`PipelineSystem` is the deliberate exception: it has no `TickContext`, no `Transaction`. Entity access flows through the Gather/Scatter pipelines (separate mechanism). Use `PipelineSystem` for bulk data-parallel work that doesn't fit the per-tick transactional model.

### `TickContext`

[`Runtime/public/TickContext.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TickContext.cs)

The struct handed to every `CallbackSystem` / `QuerySystem` body:

```csharp
public struct TickContext {
    public long          TickNumber;
    public float         DeltaTime;
    public Transaction   Transaction;
    public EntityAccessor Accessor;        // per-worker, non-Versioned parallel path
    public IReadOnlyCollection<EntityId> Entities;
    public EventQueueBase[] ConsumedQueues;
    public Func<DurabilityMode, Transaction> CreateSideTransaction;
    public int WorkerId, ChunkIndex, ChunkCount;
    public int StartClusterIndex, EndClusterIndex;
    public int[] ClusterIds;
    public float AmortizedDeltaTime;
    public TierBudgetMetrics TierBudgetMetrics;
    public SpatialGridAccessor SpatialGrid;
    // …
}
```

`CreateSideTransaction(durabilityMode)` is the escape hatch for economy-critical operations (trades, purchases) that must commit independently of the tick's main UoW. The caller owns and disposes the side transaction.

---

## 6. Workers

[`Runtime/public/DagScheduler.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/DagScheduler.cs) → `WorkerLoop`

| Property | Value |
|---|---|
| Thread name | `Typhon.Worker-{i}` (zero-indexed) |
| Priority | `ThreadPriority.AboveNormal`, `IsBackground = true` |
| Default count | `Math.Max(1, Environment.ProcessorCount - 4)` — note the `Max(1, …)` floor |
| Override | `RuntimeOptions.WorkerCount` (`-1` = auto, `1` = single-threaded debug mode) |

The `-4` leaves headroom for the TickDriver, the WAL writer ([11-durability](11-durability.md)), the checkpointer, and the OS. When `WorkerCount == 1` the scheduler runs systems serially on the TickDriver thread itself — no worker threads, no dispatch — useful for debugging.

### Between-tick wait — kernel wait only, *not* 3-phase

This is a deliberate difference from the TickDriver. The scheduler holds a single signal:

```csharp
// _tickStartSignal = ManualResetEventSlim(initialState: false, spinCount: 0)
// "SpinCount=0: go straight to kernel wait" — DagScheduler.cs:135
private readonly ManualResetEventSlim _tickStartSignal = new(false, 0);
```

In `WorkerLoop`, between ticks:

```csharp
while (_tickGeneration == lastGen) {
    if (_workerShutdown != 0) return;
    _tickStartSignal.Wait(TimeSpan.FromMilliseconds(50));
}
```

That's it — a pure 50 ms kernel wait, no user-mode spinning, no yield phase. The TickDriver sets the signal when it bumps `_tickGeneration` to start a new tick. Wake latency is the kernel-transition cost (~1–5 µs), which is negligible against a 16 ms tick at 60 Hz. **Do not confuse this with the TickDriver's 3-phase Sleep/Yield/Spin** — that strategy is for the metronome only, because the *driver* needs sub-microsecond accuracy at the wake point. Workers need only "wake somewhere in the next millisecond"; spinning here would waste a core for no benefit.

### Within-tick dispatch

Once awake, each worker loops `FindReadySystem` → `ProcessSystem` until `_systemsRemaining == 0`:

- `FindReadySystem` does a linear scan of `_isReady[]` returning a system whose predecessors all completed.
- `ProcessSystem` claims the system (CAS on `_isReady` for single-shot systems, `Interlocked.Increment(_nextChunk)` for multi-chunk).
- Idle workers (no ready work) spin briefly with PAUSE for the first ~100 iterations, then `Thread.Yield` until work appears or the tick ends.

Failure isolation: if a system throws, the worker marks `_systemFailed[sysIdx] = true`, propagates failure to all successors, and emits a `SkipReason.DependencyFailed` for them. An outer safety-net `try/catch` in `WorkerLoop` ensures even a bug inside a catch handler can't kill the worker — the simulation would otherwise freeze with `_systemsRemaining > 0` forever.

---

## 7. Parallel fence

[`Runtime/public/TyphonRuntime.cs:RunParallelFence`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TyphonRuntime.cs), [`Runtime/internals/Fence/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Runtime/internals/Fence)

At tick end, after all user systems committed, the engine runs the **tick fence** — migration applies, AABB refresh, shadow drain, spatial maintenance. By default this runs in parallel on the Engine-Post track:

```
RunParallelFence
  ↓ (serial prep on TickDriver: context reset, dormancy drain, table fences)
  ↓
scheduler.DispatchDeferredTracks()
  ↓ (Fence DAG on workers: FencePrep → FenceMigrate → FenceAabbRefresh → FenceFinalize)
```

The fence emits its own WAL records (`ClusterTickFence` chunks) and the subsequent `UoW.Flush` waits for the LSN covering them — see [11-durability §recovery](11-durability.md).

### WAL-only fallback

```csharp
if (Engine.WalManager == null) {
    InspectorPhase(TickPhase.WriteTickFence, () => Engine.WriteTickFence(...));
    return;
}
```

Parallel fence is **WAL-mode only** in v1. The per-worker `ChangeSet` cleanup (`ReleaseExcessDirtyMarks`) is correct only in WAL mode — WAL-less mode would risk torn writes across workers touching the same page. When no `WalManager` is configured, the runtime falls back to the serial `WriteTickFence` on the TickDriver thread, which uses the UoW's single-thread `ChangeSet` correctly.

The split is also why `EnableParallelFence` exists as an off switch in `RuntimeOptions` — a diagnostic safety valve.

---

## 8. Overload management

[`Runtime/internals/OverloadDetector.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/internals/OverloadDetector.cs), [`Runtime/public/OverloadOptions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/OverloadOptions.cs), [`Runtime/public/OverloadLevel.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/OverloadLevel.cs)

The overload detector runs once per tick on the TickDriver. Single writer — no synchronization.

### Defaults (`OverloadOptions`)

| Option | Default | Meaning |
|---|---|---|
| `OverrunThreshold` | **1.2** | Tick takes >120 % of budget ⇒ overrunning. |
| `DeescalationRatio` | **0.6** | Tick takes <60 % of budget ⇒ recovering (40 % headroom). |
| `EscalationTicks` | **5** | Consecutive overrun ticks to escalate one level. |
| `DeescalationTicks` | **20** | Consecutive under-run ticks to step back. Deliberately asymmetric for anti-oscillation. |
| `MinTickRateHz` | **10** | Hard floor for tick rate under modulation. |
| `QueueGrowthTicks` | **5** | Sustained event-queue growth ticks before it counts as an escalation signal. |

### Level chain

```
Normal → SystemThrottling → ScopeReduction → TickRateModulation → PlayerShedding
```

| Level | What kicks in |
|---|---|
| **`Normal`** (0) | No overload — all systems run at normal rate. |
| **`SystemThrottling`** (1) | Low-priority systems shed; Normal-priority throttled via `ThrottledTickDivisor`. |
| **`ScopeReduction`** (2) | Per-system entity budgets enforced; deferred entities tracked. |
| **`TickRateModulation`** (3) | Tick rate slows by integer multiplier (TiDi). The detector walks `[1, 2, 3, 4, 6]` while in this level — capped by `baseTickRate / MinTickRateHz`. Entering the level seeds the multiplier at index 1 (2×). |
| **`PlayerShedding`** (4) | Last resort. `TyphonRuntime.OnCriticalOverload` fires for game-specific handling (drop connections, etc.). |

The chain is sticky in both directions — `EscalationTicks` consecutive overruns push up by one, `DeescalationTicks` consecutive under-runs ease back down. Inside `TickRateModulation`, escalation first bumps the multiplier (`2 → 3 → 4 → 6`) before promoting to `PlayerShedding`; deescalation reduces the multiplier (`6 → 4 → 3 → 2 → 1`) before stepping back to `ScopeReduction`.

`MinTickRateHz` caps which multipliers are usable. At 60 Hz base / 10 Hz floor, all of `[1, 2, 3, 4, 6]` are available (6× = 10 Hz). At 60 Hz / 15 Hz floor, only `[1, 2, 3, 4]` survive (4× = 15 Hz).

---

## 9. Lifecycle

[`Runtime/public/TyphonRuntime.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Runtime/public/TyphonRuntime.cs)

`TyphonRuntime` wraps `DatabaseEngine` + `DagScheduler` and runs the per-tick UoW / Transaction discipline so game code never sees commit plumbing.

### Tick-level hooks (events)

```csharp
runtime.OnFirstTick += ctx => { /* rebuild transient state after crash recovery */ };
runtime.OnShutdown  += ctx => { /* save player state, cleanup */ };
```

- **`OnFirstTick`** — fires once on the first tick after engine open. The callback receives a valid `TickContext` with a fresh `Transaction` so it can spawn or repair entities. Used to restore transient state that doesn't survive a crash.
- **`OnShutdown`** — fires during `Shutdown()`. The callback receives a dedicated `Transaction` with `Immediate` durability — its writes are durable on commit, independent of the regular per-tick UoW.

### Per-tick observability

Every tick, on advance, the driver emits a **`Scheduler.Overload.TickMultiplier`** instant ([12-observability](12-observability.md)) carrying the current multiplier byte. The `OnWaitComplete` hook emits a **`SchedulerMetronomeWait`** span describing the inter-tick wait (intent class, multiplier, phase flags). Without these, a throttled engine would look indistinguishable from a stalled one on a trace.

### `RuntimeOptions` knobs worth knowing

| Option | Default | What it controls |
|---|---|---|
| `BaseTickRate` | 60 | Target tick rate in Hz. |
| `WorkerCount` | -1 (auto) | `Math.Max(1, ProcessorCount - 4)`; `1` for serial debug. |
| `TelemetryRingCapacity` | 1024 | Per-scheduler tick telemetry buffer (must be power of 2). |
| **`ParallelQueryMinChunkSize`** | **64** | Floor on entities per chunk for parallel `QuerySystem` dispatch. Smaller entity sets still use the parallel path with `totalChunks = 1`. |
| `EnableParallelFence` | `true` | Off switch for [§7](#7-parallel-fence) — falls back to serial `WriteTickFence`. |
| `FenceChunkOversubscription` | 2 | Fence chunk cap = `factor × WorkerCount`. Smooths preemption jitter. |
| `Overload` | new() | The `OverloadOptions` from [§8](#8-overload-management). |

---

## See also

- [01-foundation](01-foundation.md) — `HighResolutionTimerServiceBase` is the TickDriver's parent class; the metronome math uses `Stopwatch.Frequency`.
- [08-transactions](08-transactions.md) — single `Transaction` per system, scheduler-managed lifecycle, single-thread affinity.
- [11-durability](11-durability.md) — parallel-fence WAL fallback; the tick fence emits `ClusterTickFence` chunks the WAL must persist before `UoW.Flush` returns.
- [12-observability](12-observability.md) — the `Scheduler.Overload.TickMultiplier` instant, `SchedulerMetronomeWait` and `SchedulerWorkerBetweenTick` spans.
- [13-resources](13-resources.md) — Runtime registers under `IResourceRegistry.Runtime` as a tracked subsystem (timer metrics, worker counts, overload level).
