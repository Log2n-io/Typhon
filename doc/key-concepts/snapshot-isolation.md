---
uid: concept-snapshot-isolation
title: 'Snapshot isolation'
description: 'Every read in Typhon sees a consistent view of the database frozen at the reading transaction''s start — no locks, no waiting on writers. Keyed on the TSN (the visibility clock).'
---

# Snapshot isolation

> **In one line:** every read sees a consistent view of the database **frozen at the transaction's start** — no locks, no waiting on writers.

Visibility is keyed on the **TSN** (Transaction Sequence Number), Typhon's *visibility clock*. A reader sees every change committed with `TSN ≤ its own`, plus its own uncommitted writes; it **never** sees anyone else's uncommitted writes, nor any commit that landed *after* it began. Readers never take a lock and never block a writer — the cost is keeping older versions around while someone might still need them.

Because the snapshot is fixed, this **prevents dirty reads, non-repeatable reads, and phantoms** — re-running a query inside one transaction never surfaces rows another transaction committed after your snapshot. It is still **not serializable**: the anomaly it permits is **write skew** — two transactions each read an overlapping set, then write *disjoint* items, together breaking an invariant that spans them. Only [`Versioned`](xref:concept-storage-mode) components get this; `SingleVersion`/`Transient` reads are *live* (the latest in-place value), with no isolation.

> ⚠️ "Point in time" here means *your transaction's start* — a consistent **current** view. It is **not** reading historical/past versions (Typhon has no user-facing as-of-past read API). Don't confuse it with [`PointInTimeAccessor`](xref:concept-point-in-time-accessor), which is the same idea fanned across threads.

## How it relates

- **[Transaction](xref:concept-transaction)** — fixes the snapshot at creation; its TSN *is* the snapshot.
- **[Storage mode](xref:concept-storage-mode)** — only `Versioned` is snapshot-isolated; the fast modes opt out.
- **[Durability — mode & discipline](xref:concept-durability)** — the *other* clock; a change can be visible before it is durable.
- **[PointInTimeAccessor](xref:concept-point-in-time-accessor)** — a single snapshot shared by many worker threads.
- **[Conflict resolution](xref:concept-conflict-resolution)** — the write side: what happens when two snapshots write the same entity.

## In the API

- [`Transaction`](xref:Typhon.Engine.Transaction) — its TSN is the read snapshot.
- [`EntityRef`](xref:Typhon.Engine.EntityRef) — [`Read<T>`](xref:Typhon.Engine.EntityRef.Read*) resolves the version visible at that TSN.

## Learn & use

- **Narrative:** [Guide ch.3 §4 — reads: snapshot isolation](xref:guide-transactions)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [Versioned storage mode](xref:feature-ecs-storage-modes-storage-mode-versioned) · [optimistic conflict resolution](xref:feature-transactions-optimistic-conflict-resolution)
