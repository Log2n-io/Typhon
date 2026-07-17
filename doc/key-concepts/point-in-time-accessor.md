---
uid: concept-point-in-time-accessor
title: 'PointInTimeAccessor'
description: 'A thread-safe, frozen current snapshot fanned across many worker threads — a lock-free read engine far lighter than a Transaction (it carries the mechanics to read, not to mutate). It is not time travel.'
---

# PointInTimeAccessor

> **In one line:** a **thread-safe**, frozen **current** snapshot fanned across worker threads — a lock-free read engine **far lighter than a [Transaction](xref:concept-transaction)**.

**Built for concurrent reads.** One call allocates a single TSN; each worker thread then gets its *own* [`EntityAccessor`](xref:Typhon.Engine.EntityAccessor) from a flat per-worker array (`GetWorkerAccessor(workerId)`) — no dictionary, no per-entity locking — and every worker reads the *same* consistent moment. N threads scan the snapshot in parallel with zero coordination between them; the shared snapshot is immutable, so there is nothing to lock.

**Lighter than a `Transaction` because it carries only the mechanics to *read*, not to *mutate*.** A [Transaction](xref:concept-transaction) must haul write-staging, a local write cache, commit-time conflict detection, an undo log for `Rollback`, and the WAL/commit path. A `PointInTimeAccessor` holds none of that — just a TSN plus one read cursor per worker, reused across ticks with zero allocation after warmup (warm page caches preserved). It reads every [storage mode](xref:concept-storage-mode) (walking the revision chain for `Versioned`) and can still store `SingleVersion`/`Transient` in place, but it **cannot** Spawn, Destroy, Commit, Rollback, or write `Versioned` (throws). For anything structural or transactional, use a [Transaction](xref:concept-transaction).

> ⚠️ **The name is about parallelism, not time travel.** "Point in time" = one fixed *current* snapshot shared by all workers — it does **not** read historical/past versions (Typhon has no user-facing as-of-past read API). It is [snapshot isolation](xref:concept-snapshot-isolation), fanned across threads.

## How it relates

- **[Snapshot isolation](xref:concept-snapshot-isolation)** — it *is* a snapshot; every worker shares one TSN.
- **[Transaction](xref:concept-transaction)** — the heavier alternative when you need structural changes or `Versioned` writes; it carries the full staging/commit/rollback machinery this accessor omits.
- **[The tick](xref:concept-tick)** — the runtime creates one per parallel system, reused each tick.
- **[Storage mode](xref:concept-storage-mode)** — reads all three; writes only the non-`Versioned` ones.

## In the API

- [`PointInTimeAccessor`](xref:Typhon.Engine.PointInTimeAccessor) — [`Create(dbe, workerCount)`](xref:Typhon.Engine.PointInTimeAccessor.Create*), [`GetWorkerAccessor(i)`](xref:Typhon.Engine.PointInTimeAccessor.GetWorkerAccessor*), [`TSN`](xref:Typhon.Engine.PointInTimeAccessor.TSN).
- [`EntityRef`](xref:Typhon.Engine.EntityRef) — [`Read<T>`](xref:Typhon.Engine.EntityRef.Read*) / [`Write<T>`](xref:Typhon.Engine.EntityRef.Write*) through a worker accessor.

## Learn & use

- **Narrative:** [Guide ch.5 — parallel reads](xref:guide-systems) · [ch.4 §5](xref:guide-querying)
- **Reference:** [Isolation & durability cheat sheet — naming traps](xref:guide-isolation-durability)
