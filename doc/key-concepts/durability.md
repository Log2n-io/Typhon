---
uid: concept-durability
title: 'Durability — mode & discipline'
description: 'Two orthogonal dials decide when and how a committed change reaches disk: DurabilityMode (per UnitOfWork — when the WAL flushes) and DurabilityDiscipline (per transaction, SingleVersion only — how an in-place write is made durable).'
---

# Durability — mode & discipline

> **In one line:** two **orthogonal** dials decide *when* and *how* a committed change reaches disk. Keep them separate — conflating them is the classic Typhon confusion.

Durability is a different clock from [visibility](xref:concept-snapshot-isolation): a change becomes visible at `Commit`, and becomes *durable* when the WAL is flushed — which may be later. Two dials control the flush.

## `DurabilityMode` — *when* the WAL flushes

Set per [Unit of Work](xref:concept-unit-of-work). Applies to every commit in that UoW.

| Mode | Flush | Commit latency | At risk on crash |
|---|---|---|---|
| `Deferred` | on explicit `Flush()` / dispose | ~1–2 µs | everything since last flush |
| `GroupCommit` | automatically, ~every 5 ms | ~1–2 µs | ≤ one flush interval |
| `Immediate` | `fsync` on every `Commit` | ~15–85 µs | nothing |

`Deferred` and `GroupCommit` commit at the *same* speed and are **visible before durable**; only `Immediate` blocks `Commit()` until the change is on disk. A single critical transaction can *escalate* to `Immediate` — never downgrade.

## `DurabilityDiscipline` — *how* a SingleVersion write is made durable

Set per [transaction](xref:concept-transaction); applies **only** to the [`SingleVersion`](xref:concept-storage-mode) layout.

- **`TickFence`** (default) — in-place, last-writer-wins, durable at the next [tick fence](xref:concept-tick-fence) (≤ 1 tick loss). Maximum throughput for hot, loss-tolerant data.
- **`Commit`** — the write is staged, made **atomic + zero-loss durable at `Commit`** via a logical-redo WAL record, then published in place — read-committed isolation, O(1) rollback, no revision chain. For writes that must not be lost and must be all-or-nothing (teleport, item pickup) without paying for MVCC.

> 📌 The discipline (*how*) is orthogonal to the mode (*when*) and to the [storage mode](xref:concept-storage-mode) (*layout*). `Commit` discipline is what the feature catalog calls "Committed" — it is **not** a `StorageMode` value.

## How it relates

- **[Unit of Work](xref:concept-unit-of-work)** — owns the `DurabilityMode`.
- **[Storage mode](xref:concept-storage-mode)** — the `Commit` discipline exists only on `SingleVersion`.
- **[Tick fence](xref:concept-tick-fence)** — where the default `TickFence` durability lands.
- **[Snapshot isolation](xref:concept-snapshot-isolation)** — the independent visibility clock.

## In the API

- [`DurabilityMode`](xref:Typhon.Engine.DurabilityMode) — [`Deferred`](xref:Typhon.Engine.DurabilityMode.Deferred) / [`GroupCommit`](xref:Typhon.Engine.DurabilityMode.GroupCommit) / [`Immediate`](xref:Typhon.Engine.DurabilityMode.Immediate) (per UoW).
- [`DurabilityDiscipline`](xref:Typhon.Schema.Definition.DurabilityDiscipline) — [`TickFence`](xref:Typhon.Schema.Definition.DurabilityDiscipline.TickFence) / [`Commit`](xref:Typhon.Schema.Definition.DurabilityDiscipline.Commit) (per transaction, SingleVersion).
- [`BulkLoadSession`](xref:Typhon.Engine.BulkLoadSession) — the third axis: [bulk load](xref:concept-bulk-load) bypasses per-row WAL entirely rather than tuning it.

## Learn & use

- **Narrative:** [Guide ch.3 §3 — durability modes](xref:guide-transactions)
- **Reference:** [Isolation & durability cheat sheet](xref:guide-isolation-durability)
- **Feature detail:** [Durability modes](xref:feature-transactions-durability-modes-index) · [durability disciplines](xref:feature-transactions-durability-discipline-index) · [override escalation](xref:feature-transactions-durability-modes-durability-override-escalation)
