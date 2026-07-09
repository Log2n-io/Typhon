---
uid: feature-runtime-tick-execution-engine-execution-modes
title: 'Execution Modes'
description: 'Today: a fixed-timestep tick loop (plus a single-threaded debug variant); event-driven and hybrid request/response modes are designed but not yet built.'
---

# Execution Modes
> Today: a fixed-timestep tick loop (plus a single-threaded debug variant); event-driven and hybrid request/response modes are designed but not yet built.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Tick-Based Execution Engine](./README.md)

## 🎯 What it solves

Game servers split into two broad dispatch shapes: continuous simulation (FPS/MMO/survival — work
happens every tick whether or not a player acts) and discrete request/response (turn-based/card/idle
games — work happens only when a player acts). Forcing one shape onto the other either pays a full
tick's overhead for an idle turn-based game, or bolts a fake tick loop onto something that's
naturally event-driven. `TyphonRuntime` is designed to host either shape; today only the tick-driven
shape is implemented.

## ⚙️ How it works (in brief)

`TyphonRuntime.Create` + `Start()` runs a fixed-timestep loop: a dedicated `Typhon.TickDriver` thread
fires ticks at `RuntimeOptions.BaseTickRate` Hz, and every tick executes the full registered system
DAG through the runtime-owned UoW/Transaction lifecycle (see
[Tick Lifecycle & Transaction Management](../tick-lifecycle/README.md)). Setting
`RuntimeOptions.WorkerCount = 1` collapses the worker pool onto the TickDriver thread itself, running
every system synchronously in topological order — the same `ShouldRun`/`Prepare`/skip/failure
machinery as multi-threaded execution, just on one thread. This is a genuine single-threaded
execution path (not a simulated one), useful for deterministic step-by-step debugging without
touching any other code.

## 💻 Usage

```csharp
using var runtime = TyphonRuntime.Create(engine, schedule =>
{
    schedule.PublicTrack.DeclareDag("Game")
        .CallbackSystem("Movement", ctx => { /* ... */ })
        .CallbackSystem("AI", ctx => { /* ... */ }, after: "Movement");
}, new RuntimeOptions
{
    BaseTickRate = 60,   // Hz — the only execution mode implemented today
});

runtime.Start();   // spins up the worker pool + the Typhon.TickDriver metronome thread
// ... ticks fire on the metronome until Shutdown() ...
runtime.Shutdown();
runtime.Dispose();
```

```csharp
// Single-threaded debug mode — identical schedule, deterministic in-order execution on one thread
using var debugRuntime = TyphonRuntime.Create(engine, configureSchedule,
    new RuntimeOptions { BaseTickRate = 60, WorkerCount = 1 });
```

| Option | Default | Effect |
|---|---|---|
| `RuntimeOptions.BaseTickRate` | 60 | Metronome rate (Hz) for the tick-driven loop |
| `RuntimeOptions.WorkerCount` | -1 (`Max(1, ProcessorCount - 4)`) | Set to `1` for single-threaded, in-order debug execution |

## ⚠️ Guarantees & limits

- **Only tick-based mode is implemented.** `claude/design/Runtime/04-threading-and-modes.md` also
  designs an event-driven mode (`ProcessAction` per player action, no tick loop, per-player
  serialized/cross-player concurrent dispatch) and a hybrid mode (tick loop + in-tick action
  dispatch), but there is no `ExecutionMode` enum, `ProcessAction`, or `RegisterActionHandler` in the
  current codebase — every `TyphonRuntime` runs the fixed-timestep tick loop.
- Request/response workloads (turn-based, card, idle games) must currently be modeled as work
  processed inside a tick (e.g. drain a queued-actions buffer from a `CallbackSystem`) rather than
  through a dedicated event-driven dispatch path.
- The tick rate is fixed at `BaseTickRate` for the runtime's lifetime; only the *internal* multiplier
  the overload detector applies under load (1×-6×) changes effective cadence — see
  [Worker Pool & Threading Model](./worker-pool-threading.md).
- `WorkerCount = 1` still runs the full DAG (all phases, all tracks) — it changes *where* systems
  run (one thread, in topological order), not *what* runs.

## 🧪 Tests

- [TyphonRuntimeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/TyphonRuntimeTests.cs) — `SingleThreadedMode_TransactionWorks` proves `WorkerCount = 1` still gives systems a valid Transaction
- [DagSchedulerTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/DagSchedulerTests.cs) — `SingleThreadedMode_Works`, `SingleThreadedMode_PipelineSystem_AllChunksProcessed` — deterministic in-order execution of the same DAG

## 🔗 Related

- Parent feature: [Tick-Based Execution Engine](./README.md)
- Sibling: [Worker Pool & Threading Model](./worker-pool-threading.md)

<!-- Deep dive: claude/design/Runtime/04-threading-and-modes.md (§Execution Modes — covers the designed-but-unbuilt Event-Driven/Hybrid modes), claude/overview/13-runtime.md -->
