---
uid: concept-conflict-resolution
title: 'Conflict resolution'
description: 'The write side of MVCC. Typhon takes no write locks, so write-write conflicts are detected at commit, not prevented. By default the last writer wins; a ConcurrencyConflictHandler lets you reconcile per entity instead of silently clobbering.'
---

# Conflict resolution

> **In one line:** the **write** side of MVCC — Typhon takes no write locks, so a write-write conflict is *detected at commit*, and you choose the outcome: silent last-writer-wins, or a handler that reconciles.

[Snapshot isolation](xref:concept-snapshot-isolation) explains why reads never block; this is the other half. Because a [transaction](xref:concept-transaction) locks nothing while it runs, two transactions can read the same entity and both intend to write it. At `Commit`, each written entity is checked against a monotonic per-entity commit counter: if someone committed a newer value since your snapshot, that entity **conflicts**. The plain `Commit()` doesn't look — the committing value simply overwrites (last-writer-wins), silently discarding the other transaction's work. Fine for a position; dangerous for a counter, a balance, or an accumulator.

Pass a `ConcurrencyConflictHandler` to `Commit(handler)` and you decide, per conflicting entity. The engine populates a `ConcurrencyConflictSolver` with three views of the entity — what you **read**, what was **committed** underneath you, and what you're **committing** — and invokes your handler once for that entity, *under the entity's revision-chain lock*, so detection and resolution are one atomic step no other writer can interleave. Your handler picks `TakeRead` (drop your change), `TakeCommitted` (accept theirs), `TakeCommitting` (keep yours — the default), or writes a custom value into `ToCommitData` to rebase / merge / clamp. There is **no abort path** — a handler chooses the value that commits, it cannot fail the commit.

> ⚠️ **Versioned only.** Detection needs a revision chain to compare against, so it applies to [`Versioned`](xref:concept-storage-mode) components; `SingleVersion` / `Committed` / `Transient` are last-writer-wins by construction — there is no prior revision to reconcile. Handlers run under a lock on the commit path — keep them fast and allocation-light (no I/O, no calls back into the engine).

## How it relates

- **[Snapshot isolation](xref:concept-snapshot-isolation)** — the read side; this is the write side of the same optimistic model.
- **[Transaction](xref:concept-transaction)** — conflicts are detected and resolved during its `Commit`.
- **[Storage mode](xref:concept-storage-mode)** — only `Versioned` participates; the fast modes clobber.
- **[Unit of Work](xref:concept-unit-of-work)** — the durability boundary a resolved commit lands in.

## In the API

- [`Transaction.Commit(ConcurrencyConflictHandler)`](xref:Typhon.Engine.Transaction) — the reconciling overload; plain `Commit()` is last-writer-wins.
- [`ConcurrencyConflictSolver`](xref:Typhon.Engine.ConcurrencyConflictSolver) — a reused, thread-local struct valid only inside the handler: [`ReadData<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.ReadData*) / [`CommittedData<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.CommittedData*) / [`CommittingData<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.CommittingData*), and the resolution helpers [`TakeRead<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.TakeRead*) / [`TakeCommitted<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.TakeCommitted*) / [`TakeCommitting<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.TakeCommitting*) / [`ToCommitData<T>()`](xref:Typhon.Engine.ConcurrencyConflictSolver.ToCommitData*). Don't store it past the call.

## Learn & use

- **Feature detail:** [Optimistic concurrency conflict resolution](xref:feature-transactions-optimistic-conflict-resolution) · [commit & rollback pipeline](xref:feature-transactions-commit-rollback-pipeline)
- **Narrative:** [Guide ch.3 — transactions](xref:guide-transactions)
- **Decision record:** ADR-003 (MVCC snapshot isolation + optimistic conflict detection).
