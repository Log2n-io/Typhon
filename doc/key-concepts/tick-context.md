---
uid: concept-tick-context
title: 'TickContext'
description: 'The per-tick execution context a system receives in Execute — its transaction, parallel-read accessor, entity set, delta time, spatial-grid handle, event queues, and the side-transaction escape hatch.'
---

# TickContext

> **In one line:** what a [system](xref:concept-system)'s `Execute` receives each [tick](xref:concept-tick) — its transaction, its read accessor, its entities, the frame's delta time, and the escape hatches.

A system never creates a [unit of work](xref:concept-unit-of-work) or calls `Commit` — it is handed a `TickContext` and works through it. The context exposes: `Transaction` (the system's own per-tick [transaction](xref:concept-transaction), committed for you by the scheduler); `Accessor` (a [`PointInTimeAccessor`](xref:concept-point-in-time-accessor) for lock-free parallel reads); `Entities` (the [view](xref:concept-view)'s matched set, for a `QuerySystem`); `DeltaTime` (seconds since the last tick); and `SpatialGrid` (assign a [`SimTier`](xref:concept-spatial-tiers) per cell, read tier budgets).

It also carries the escape hatches from the one-transaction-per-tick default: **typed event queues** for inter-system signalling, and **side transactions** — `ctx.CreateSideTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit)` opens an independent transaction mid-tick for an immediate, ACID write (a player purchase, a teleport) that must not ride the tick's shared commit.

## How it relates

- **[System](xref:concept-system)** — receives a `TickContext` in `Execute`; it *is* the system's window on the world.
- **[Tick](xref:concept-tick)** — one context per system per tick; side transactions and event queues are how you step outside the default.
- **[Transaction](xref:concept-transaction)** — `ctx.Transaction` is the system's; `ctx.CreateSideTransaction` opens another.
- **[PointInTimeAccessor](xref:concept-point-in-time-accessor)** — `ctx.Accessor`, for parallel reads across worker threads.

## In the API

- [`TickContext`](xref:Typhon.Engine.TickContext) — [`Transaction`](xref:Typhon.Engine.TickContext.Transaction) / [`Accessor`](xref:Typhon.Engine.TickContext.Accessor) / [`Entities`](xref:Typhon.Engine.TickContext.Entities) / [`DeltaTime`](xref:Typhon.Engine.TickContext.DeltaTime) / [`SpatialGrid`](xref:Typhon.Engine.TickContext.SpatialGrid).
- [`TickContext.CreateSideTransaction(DurabilityMode, DurabilityDiscipline)`](xref:Typhon.Engine.TickContext.CreateSideTransaction) — an independent mid-tick transaction.
- Typed [`EventQueue<T>`](xref:Typhon.Engine.EventQueue`1) — single-producer inter-system signalling, drained per tick.

## Learn & use

- **Feature detail:** [Tick-based execution engine](xref:feature-runtime-tick-execution-engine-index) · [side-transactions](xref:feature-runtime-side-transactions) · [typed event queues](xref:feature-runtime-typed-event-queues)
- **Narrative:** [Guide ch.5 — systems](xref:guide-systems)
