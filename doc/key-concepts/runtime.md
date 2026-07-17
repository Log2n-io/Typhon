---
uid: concept-runtime
title: 'Typhon runtime'
description: 'The host that drives your systems continuously — it owns the worker pool and the tick loop, turning a DatabaseEngine into a running, self-ticking world. Recommended for tick-driven parallel logic, but optional.'
---

# Typhon runtime

> **In one line:** the **host that drives your [systems](xref:concept-system)** — it owns the worker pool and the [tick](xref:concept-tick) loop, turning a [`DatabaseEngine`](xref:concept-database-engine) into a running, self-ticking world.

`TyphonRuntime.Create(dbe, schedule => …, options)` takes your [engine](xref:concept-database-engine), a **schedule** of systems ([tracks → DAGs → phases](xref:concept-scheduler)), and `RuntimeOptions`. `Start()` spins up the worker pool and the metronome; the world ticks itself until `Shutdown()`. You don't drive the loop — there is no "run one tick" call. You *start* it, then observe it (`CurrentTickNumber`, telemetry) and feed it (input queues) from the outside.

What happens *inside* each iteration — one Unit of Work, one transaction per system, the fence — belongs to the [tick](xref:concept-tick); the runtime just drives it.

The runtime is **recommended but optional**: request/response and batch apps drive the [engine](xref:concept-database-engine) directly through [transactions](xref:concept-transaction) and never declare a system. Reach for it when you have continuous, parallel, tick-driven logic. Two lifecycle hooks cover the special moments — **`OnFirstTick`** (rebuild `Transient` state after a restart) and **`OnShutdown`** (a final `Immediate`-durable save before the process exits).

## How it relates

- **[Tick](xref:concept-tick)** — the iteration the runtime drives; it owns the per-tick mechanics (UoW, per-system transaction, fence).
- **[System](xref:concept-system)** — what you register on the schedule for the runtime to run.
- **[Scheduler & phases](xref:concept-scheduler)** — the schedule structure the runtime walks each tick.
- **[DatabaseEngine](xref:concept-database-engine)** — the runtime wraps one engine to make it tick.
- **[Overload management](xref:concept-overload-management)** — the runtime's standing policy when a tick runs over budget.
- **[Spatial tiers & adaptive dispatch](xref:concept-spatial-tiers)** — how it scales dispatch across a large world without per-entity cost.

## In the API

- [`TyphonRuntime`](xref:Typhon.Engine.TyphonRuntime) — [`Create`](xref:Typhon.Engine.TyphonRuntime.Create*) / [`Start`](xref:Typhon.Engine.TyphonRuntime.Start*) / [`Shutdown`](xref:Typhon.Engine.TyphonRuntime.Shutdown*) / [`OnFirstTick`](xref:Typhon.Engine.TyphonRuntime.OnFirstTick) / [`OnShutdown`](xref:Typhon.Engine.TyphonRuntime.OnShutdown) / [`CurrentTickNumber`](xref:Typhon.Engine.TyphonRuntime.CurrentTickNumber).
- [`RuntimeOptions`](xref:Typhon.Engine.RuntimeOptions) — [`BaseTickRate`](xref:Typhon.Engine.RuntimeOptions.BaseTickRate) (the tick cadence) / [`WorkerCount`](xref:Typhon.Engine.RuntimeOptions.WorkerCount) / parallel-query knobs.

## Learn & use

- **Narrative:** [Guide ch.5 §6 — building & running the runtime](xref:guide-systems)
- **Feature detail:** [runtime](xref:feature-runtime-index) · [tick execution engine](xref:feature-runtime-tick-execution-engine-index)
