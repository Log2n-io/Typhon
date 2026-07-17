---
uid: concept-system
title: 'System'
description: 'A system is a unit of logic with declared data access. You say what it reads and writes; the engine works out what can run at the same time. Three shapes: CallbackSystem, QuerySystem, PipelineSystem.'
---

# System

> **In one line:** a unit of logic with **declared data access** — you say *what it reads and writes*, the engine works out *what can run at the same time*.

A system is a class you derive from one of three bases and give two methods: `Configure` (declare it — `Name`, `Phase`, `Input`, `Reads`/`Writes`) and `Execute` (run it against a [`TickContext`](xref:Typhon.Engine.TickContext)). The [runtime](xref:concept-runtime) gives each system its own [transaction](xref:concept-transaction) per [tick](xref:concept-tick), created on its worker thread and committed by the scheduler — **you never call `Commit`**.

Three shapes, picked by what the work looks like: **`CallbackSystem`** (non-entity work — input, timers, spawning), **`QuerySystem`** (do something to every entity in a [view](xref:concept-view) — the workhorse, parallelisable), **`PipelineSystem`** (bulk data-parallel sweeps). The declared access is what lets the [scheduler](xref:concept-scheduler) parallelise safely.

## How it relates

- **[Runtime](xref:concept-runtime)** — runs your systems every tick, in parallel.
- **[Scheduler & phases](xref:concept-scheduler)** — turns declared access into a safe execution graph.
- **[View](xref:concept-view)** — a `QuerySystem`'s input set.
- **[Transaction](xref:concept-transaction)** / **[PointInTimeAccessor](xref:concept-point-in-time-accessor)** — how a system touches data (transactional, or lock-free parallel reads).
- **[TickContext](xref:concept-tick-context)** — the per-tick object `Execute` receives (transaction, accessor, entities, side-transactions).

## In the API

- [`CallbackSystem`](xref:Typhon.Engine.CallbackSystem) · [`QuerySystem`](xref:Typhon.Engine.QuerySystem) · [`PipelineSystem`](xref:Typhon.Engine.PipelineSystem) — the three bases.
- [`SystemBuilder`](xref:Typhon.Engine.SystemBuilder) — the `Configure` builder ([`Reads`](xref:Typhon.Engine.SystemBuilder.Reads*)/[`Writes`](xref:Typhon.Engine.SystemBuilder.Writes*)/[`Input`](xref:Typhon.Engine.SystemBuilder.Input*)/[`Parallel`](xref:Typhon.Engine.SystemBuilder.Parallel*)/…).
- [`TickContext`](xref:Typhon.Engine.TickContext) — what `Execute` receives ([`Transaction`](xref:Typhon.Engine.TickContext.Transaction) / [`Accessor`](xref:Typhon.Engine.TickContext.Accessor) / [`Entities`](xref:Typhon.Engine.TickContext.Entities) / [`DeltaTime`](xref:Typhon.Engine.TickContext.DeltaTime)).

## Learn & use

- **Narrative:** [Guide ch.5 — systems & the tick loop](xref:guide-systems)
- **Feature detail:** [declarative system scheduling](xref:feature-runtime-declarative-system-scheduling) · [system types](xref:feature-runtime-system-types-callback-system)
