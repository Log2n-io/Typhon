---
uid: feature-durability-durability-modes-index
title: 'Durability Modes'
description: 'Per-Unit-of-Work control over when WAL records become crash-safe — pick latency vs. data-at-risk per workload.'
---

# Durability Modes
> Per-Unit-of-Work control over when WAL records become crash-safe — pick latency vs. data-at-risk per workload.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Durability](../README.md)

## 🎯 What it solves

Different workloads in the same process need different durability guarantees: a game tick can tolerate losing
the last ~16ms of work on crash, a player trade cannot lose anything, and general server requests sit somewhere
in between. A single global durability setting forces every workload onto the slowest, most conservative choice.
Durability Modes let each Unit of Work (UoW) pick its own commit-latency vs. data-at-risk trade-off — without
touching the rest of the engine's guarantees: atomicity and isolation stay per-transaction in every mode.

## ⚙️ How it works (in brief)

`DurabilityMode` is fixed when a UoW is created and controls only *when* that UoW's WAL records are flushed
(fsync'd) to stable media — never whether a transaction commits or becomes visible. All commits stay in-memory
and MVCC-visible at the same ~1-2µs regardless of mode; the mode only changes how long the WAL record sits in
the commit buffer before it's durable. The **transaction** is always the unit of crash atomicity: a crash never
recovers a partial transaction, only a possibly-shorter prefix of the transactions you committed.

## 💻 Usage

```csharp
// General server workload — bounded data-at-risk, no per-tx wait
using var uow = db.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx = uow.CreateTransaction();
UpdatePlayerState(tx, playerId);
tx.Commit();                  // ~1-2µs — durable within WalWriterOptions.GroupCommitIntervalMs (default 5ms)

// Batch import — many transactions, one flush at the end
using var batch = db.CreateUnitOfWork(DurabilityMode.Deferred);
foreach (var row in rows)
{
    using var tx = batch.CreateTransaction();
    ImportRow(tx, row);
    tx.Commit();               // ~1-2µs — volatile until Flush()
}
await batch.FlushAsync();      // one FUA for the whole batch

// Financial trade — zero data-at-risk
using var trade = db.CreateUnitOfWork(DurabilityMode.Immediate);
using var tx2 = trade.CreateTransaction();
ExecuteTrade(tx2, alice, bob, item, gold);
tx2.Commit();                  // blocks ~15-85µs — durable on the data file's WAL before returning
```

| Mode | Commit latency | Data-at-risk window | Notes |
|------|-----------------|---------------------|-------|
| `Deferred` (default) | ~1-2µs | until `uow.Flush()` / `FlushAsync()` | best for ticks, bulk imports |
| `GroupCommit` | ~1-2µs | ≤ `WalWriterOptions.GroupCommitIntervalMs` (default 5ms, engine-wide via `services.AddDatabaseEngine(o => o.Wal.GroupCommitIntervalMs = …)`) | best for general request handlers |
| `Immediate` | ~15-85µs (one WAL FUA) | zero | best for trades, irreversible state |

## ⚠️ Guarantees & limits

- Mode is fixed for the UoW's lifetime — there is no API to change it mid-UoW; create a separate UoW for a
  different durability need.
- Atomicity and isolation are unaffected by mode. Only the post-crash data-loss window changes; a crash never
  yields a half-applied transaction.
- `GroupCommitIntervalMs` is an engine-wide WAL writer setting (`DatabaseEngineOptions.Wal`), not a per-UoW
  property — every `GroupCommit` UoW in the engine shares the same interval.
- Disposing a `Deferred` UoW does **not** flush — unflushed transactions stay volatile until something else
  flushes the WAL (explicit `Flush()`/`FlushAsync()`, the GroupCommit timer, or 80%-buffer back-pressure).
  Disposing a `GroupCommit` or `Immediate` UoW does flush.
- `Immediate` raises `CommitDurabilityUncertainException` rather than rolling back if the post-append fsync
  wait doesn't confirm in time — the transaction is already committed and visible; this is "durability
  unconfirmed," never a rollback signal. See the [Commit Pipeline](../commit-pipeline.md) feature.
- **Known gap:** the `DurabilityOverride` enum (`Default`/`Immediate`, escalating a single transaction inside an
  otherwise `Deferred`/`GroupCommit` UoW) is declared on the public API surface (`DurabilityMode.cs`) per
  ADR-005, but is not yet wired into
  `Transaction.Commit()` — there is currently no single-call escalation path for a commit within an existing
  UoW. Today's workaround for a critical operation inside an otherwise low-durability workload: commit it
  through its own `DurabilityMode.Immediate` UoW (`dbe.CreateQuickTransaction(DurabilityMode.Immediate)`), or,
  from a scheduled system, a side-transaction (`ctx.CreateSideTransaction(DurabilityMode.Immediate, …)`).
- Max durable tx/s: ~12K-65K for `Immediate` (FUA round-trip bound) vs. millions for `GroupCommit`/`Deferred`
  (CPU-serialization bound, amortized FUA).

## 🧪 Tests

- [UnitOfWorkTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Execution/UnitOfWorkTests.cs) — mode fixed per UoW, `Flush()`/`FlushAsync()` semantics, `Deferred` not flushing on dispose
- [WalIntegrationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalIntegrationTests.cs) — `Deferred`/`GroupCommit`/`Immediate` exercised across dirty-page and reopen scenarios

## 🔗 Related

- Sub-features: [Committed Durability Discipline](./committed-discipline.md)
- Sibling: [Unit of Work (durability boundary)](../../Transactions/unit-of-work.md) — `DurabilityMode` is fixed on the UoW at creation; the UoW is the object that owns this choice

<!-- Deep dive: claude/overview/06-durability.md §6.3, claude/overview/02-execution.md §2.3, claude/adr/005-durability-mode-per-uow.md -->
<!-- Rules: claude/rules/durability.md — module CX -->
