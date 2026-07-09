---
uid: feature-runtime-tick-execution-engine-index
title: 'Tick-Based Execution Engine'
description: 'TyphonRuntime — the single host object that turns a registered system schedule into a running, self-recovering, gracefully-shutting-down server.'
---

# Tick-Based Execution Engine
> TyphonRuntime — the single host object that turns a registered system schedule into a running, self-recovering, gracefully-shutting-down server.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](../README.md)

## 🎯 What it solves

A real-time simulation server needs more than "call my systems every frame" — it needs a process
lifecycle: spin up worker threads and a drift-free tick clock, recover correctly after a crash
(replay the WAL, rebuild transient state), and shut down without losing in-flight work or hanging
on a stuck system. Hand-rolling this lifecycle is exactly the kind of plumbing that's easy to get
subtly wrong — a worker thread that's never joined, transient state nobody rebuilds after recovery,
writes lost because shutdown didn't wait for them. `TyphonRuntime` is the one object that owns this
lifecycle end-to-end, so game code only has to define systems and a couple of lifecycle callbacks.

## ⚙️ How it works (in brief)

`TyphonRuntime.Create(engine, configure, options)` builds a `DagScheduler` from a `RuntimeSchedule`
the caller populates with systems, then `Start()` launches the worker pool and a dedicated
`Typhon.TickDriver` thread that fires ticks at a fixed metronome rate (see
[Worker Pool & Threading Model](./worker-pool-threading.md)). Every tick runs the registered system
DAG through a runtime-owned UoW/Transaction lifecycle — see
[Tick Lifecycle & Transaction Management](../tick-lifecycle/README.md) for what happens inside a
single tick. `OnFirstTick` fires exactly once, before the first tick's systems run, so game code can
rebuild transient state after crash recovery (the database engine itself replays the WAL during
`DatabaseEngine` open, before the runtime is even created). `OnShutdown` fires once during
`Shutdown()`, given a dedicated Immediate-durability transaction so cleanup writes (e.g. persisting
player state) survive the process exiting. `Dispose()` then tears down worker threads and the
underlying resources.

## Sub-features

| Sub-feature | What it covers |
|---|---|
| [Execution Modes](./execution-modes.md) | The tick-driven dispatch model TyphonRuntime runs today, including single-threaded debug mode |
| [Worker Pool & Threading Model](./worker-pool-threading.md) | Core allocation between the tick metronome thread and the worker pool, and how each waits |
| [Parallel Tick Fence](./parallel-tick-fence.md) | How the post-tick `WriteTickFence` step is spread across the worker pool instead of running serially |

## ⚠️ Guarantees & limits

- `Create` does not start anything — systems can be registered and validated before any thread
  exists. `Start()` is the only call that launches worker threads and the TickDriver.
- `OnFirstTick` and `OnShutdown` each receive their own `TickContext` with a dedicated `Transaction`
  — `OnShutdown`'s is Immediate durability, so its writes are crash-safe before `Shutdown()` returns.
- `Shutdown()` is synchronous: it stops the subscription server, runs `OnShutdown`, then blocks until
  every worker thread has joined. There is no async/timeout-bounded shutdown overload — a system
  that deadlocks blocks `Shutdown()` indefinitely.
- `Dispose()` must run after `Shutdown()` to release worker-pool resources; disposing without
  shutting down first does not run `OnShutdown`.

## 🧪 Tests

- [TyphonRuntimeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/TyphonRuntimeTests.cs) — Create/Start/Shutdown lifecycle, `OnFirstTick`/`OnShutdown` durability, transaction handoff to systems

## 🔗 Related

- Related feature: [Tick Lifecycle & Transaction Management](../tick-lifecycle/README.md) — what happens inside one tick (UoW, per-system Transactions, side-transactions)
- Sub-features: [Execution Modes](./execution-modes.md), [Worker Pool & Threading Model](./worker-pool-threading.md), [Parallel Tick Fence](./parallel-tick-fence.md)

<!-- Deep dive: claude/design/Runtime/01-tick-lifecycle.md, claude/design/Runtime/04-threading-and-modes.md, claude/overview/13-runtime.md -->
