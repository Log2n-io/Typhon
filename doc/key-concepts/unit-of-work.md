---
uid: concept-unit-of-work
title: 'Unit of Work'
description: 'The unit of durability in Typhon — it groups one or more transactions into a single flush cycle and decides, via its DurabilityMode, when their commits become crash-safe.'
---

# Unit of Work

> **In one line:** the unit of **durability** — it groups one or more [transactions](xref:concept-transaction) into a single flush cycle and decides, via its `DurabilityMode`, *when* their commits become crash-safe.

Typhon splits "what's atomic" from "what's durable". A [transaction](xref:concept-transaction) is atomic and isolated; a **`UnitOfWork`** is the durability boundary that wraps it. Durability is a batched, cross-cutting concern — one `fsync` can make many transactions durable at once — so it lives one level up from the transaction.

A committed transaction is **visible immediately**, but only becomes **durable** when its UoW flushes — on the schedule set by its [DurabilityMode](xref:concept-durability). Under the runtime, there is exactly one UoW per [tick](xref:concept-tick).

## How it relates

- **[Transaction](xref:concept-transaction)** — what a UoW contains; visibility is the transaction's job, durability is the UoW's.
- **[Durability — mode & discipline](xref:concept-durability)** — the UoW's `DurabilityMode` is the *when-does-the-WAL-flush* dial.
- **[The tick](xref:concept-tick)** — the runtime opens one UoW per tick and flushes it at tick end.
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — visibility advances independently of the UoW's flush.

## In the API

- [`UnitOfWork`](xref:Typhon.Engine.UnitOfWork) — the type itself; [`CreateTransaction(...)`](xref:Typhon.Engine.UnitOfWork.CreateTransaction*), [`Flush()`](xref:Typhon.Engine.UnitOfWork.Flush*) / [`FlushAsync()`](xref:Typhon.Engine.UnitOfWork.FlushAsync*).
- [`DatabaseEngine`](xref:Typhon.Engine.DatabaseEngine) — [`CreateUnitOfWork(DurabilityMode)`](xref:Typhon.Engine.DatabaseEngine.CreateUnitOfWork*).
- [`DurabilityMode`](xref:Typhon.Engine.DurabilityMode) — the flush-timing enum set at UoW creation.

## Learn & use

- **Narrative:** [Guide ch.3 §1 — two layers](xref:guide-transactions)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [Unit of Work](xref:feature-transactions-unit-of-work) · [durability modes](xref:feature-transactions-durability-modes-index)
