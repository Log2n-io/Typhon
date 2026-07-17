---
uid: concept-transaction
title: 'Transaction'
description: 'The unit of isolation in Typhon — one writer, one consistent read snapshot, one atomic set of changes that all commit or all roll back.'
---

# Transaction

> **In one line:** the unit of **isolation** — one writer, one consistent read snapshot, one atomic set of changes that either all commit or all roll back.

A `Transaction` is owned by the thread that created it (**single-thread-affine**) and carries **no locks on itself** — that is what makes opening one essentially free. Its reads run against a [snapshot](xref:concept-snapshot-isolation) fixed at creation; its writes become visible to *later* readers only at `Commit`, and are discarded on `Rollback`.

A transaction is a true ACID envelope **only for the `Versioned` data it touches**. For [`SingleVersion`/`Transient`](xref:concept-storage-mode) components it still gives you thread affinity, atomic entity spawn/destroy, and a consistent snapshot of any *Versioned* components in the same archetype — but not isolation, rollback, or commit-timed durability on those components' values.

> ⚠️ `Commit()` returning makes a change **visible**, not necessarily **durable** — that is the [Unit of Work](xref:concept-unit-of-work)'s job. See [visible ≠ durable](xref:guide-isolation-durability).

## How it relates

- **[Unit of Work](xref:concept-unit-of-work)** — wraps transactions; decides *when* their commits become crash-safe.
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — what a transaction's reads see; its snapshot is fixed at creation.
- **[Storage mode](xref:concept-storage-mode)** — determines what "transactional" actually guarantees per component.
- **[Durability — mode & discipline](xref:concept-durability)** — decide when and how a committed transaction reaches disk.
- **[Conflict resolution](xref:concept-conflict-resolution)** — what happens at `Commit` when another writer changed the same entity.
- **[Deadlines & timeouts](xref:concept-deadlines-timeouts)** — every transaction runs under a deadline; waits fail as typed timeouts.

## In the API

- [`Transaction`](xref:Typhon.Engine.Transaction) — the type itself.
- [`DatabaseEngine`](xref:Typhon.Engine.DatabaseEngine) — `CreateQuickTransaction` / `CreateReadOnlyTransaction`, or [`CreateUnitOfWork(...)`](xref:Typhon.Engine.DatabaseEngine.CreateUnitOfWork*)`.CreateTransaction(...)`.
- [`EntityRef`](xref:Typhon.Engine.EntityRef) — [`Read<T>`](xref:Typhon.Engine.EntityRef.Read*) / [`Write<T>`](xref:Typhon.Engine.EntityRef.Write*) on an opened entity.

## Learn & use

- **Narrative:** [Guide ch.3 — Transactions & durability](xref:guide-transactions)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [Transactions feature catalog](xref:feature-transactions-index) · [creation patterns](xref:feature-transactions-transaction-creation-patterns-index)
