---
uid: feature-transactions-commit-rollback-pipeline
title: 'Commit / Rollback Pipeline (ACID Commit Path)'
description: 'The two methods that end a transaction — Commit makes writes durable and visible as one atomic unit, Rollback always unwinds them completely.'
---

# Commit / Rollback Pipeline (ACID Commit Path)
> The two methods that end a transaction — `Commit` makes writes durable and visible as one atomic unit, `Rollback` always unwinds them completely.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](./README.md)

## 🎯 What it solves
Ending a transaction has to be all-or-nothing from the caller's point of view: either every component you touched becomes visible and crash-durable, or none of it does. A commit that can be interrupted partway — by a timeout, a lock failure, or a crash — and leave some components written and others not is worse than no transaction support at all. Symmetrically, an aborted transaction must always finish unwinding its work; a rollback that itself times out and leaves orphaned revisions behind turns every failure into a slow leak. `Commit`/`Rollback` are the two calls that give those guarantees without the caller having to reason about internal ordering.

## ⚙️ How it works (in brief)
`Commit` processes each touched component in two halves: PREPARE (conflict detection/resolution, index and spatial-index maintenance — fallible work, nothing made visible yet) and PUBLISH (clear isolation flags, bump revision sequence numbers, copy into cluster slots — plain field writes). Between the two phases sits one WAL append, the commit's point of no return: once it succeeds the transaction *is* `Committed`, even if a later `Immediate`-mode durability wait can't confirm the fsync in time (see [Commit Pipeline](../Durability/commit-pipeline.md) for the full append-before-publish mechanics). `Rollback` has no such split — it runs entirely inside a holdoff (see [Deadline & Cooperative Cancellation](./deadline-cancellation.md)) so cleanup always reaches completion regardless of the caller's deadline.

## 💻 Usage
```csharp
using var uow = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
using var tx = uow.CreateTransaction();

tx.OpenMut(accountId).Write(Account.Balance).Amount -= 10m;

var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(2));
try
{
    bool committed = tx.Commit(ref ctx);   // null handler => last-writer-wins on conflicts
}
catch (TyphonTimeoutException)
{
    // Deadline expired before any work was touched — state is still InProgress.
    var rbCtx = UnitOfWorkContext.None;    // rollback should never itself time out
    tx.Rollback(ref rbCtx);
}
catch (CommitDurabilityUncertainException)
{
    // Already committed and visible — do not roll back, do not retry.
}

// Backward-compatible wrappers — no explicit context needed:
tx.Commit();      // ConcurrencyConflictHandler optional; default 30s timeout
tx.Rollback();    // infinite deadline (UnitOfWorkContext.None)
```

| Overload | Default | Effect |
|---|---|---|
| `Commit(ref UnitOfWorkContext, handler = null)` | — | Full control over the deadline; pass a `ConcurrencyConflictHandler` to resolve write-write conflicts instead of last-writer-wins |
| `Commit(handler = null)` | `TimeoutOptions.Current.DefaultCommitTimeout` (30s) | Wrapper for call sites that don't need custom deadlines |
| `Rollback(ref UnitOfWorkContext)` / `Rollback()` | infinite deadline | Rollback ignores the deadline for its own cleanup work regardless of which overload is used |

## ⚠️ Guarantees & limits
- **Atomic commit** — the only cancellation check in `Commit` is at entry, before any component is touched; once the holdoff opens, every component's PREPARE/PUBLISH runs to completion, so a deadline can never produce a partially-committed transaction.
- **Append before publish** — no change (isolation flag, index, entity map, cluster slot) becomes visible before its WAL record is appended; see [Commit Pipeline](../Durability/commit-pipeline.md) for the full ordering and the residual spawn-publish throw case (#396).
- **Rollback always completes** — runs entirely inside holdoff with no yield point; an expired deadline or cancelled token cannot abort cleanup partway through, by design (`Rollback()`'s wrapper uses `UnitOfWorkContext.None`).
- **`bool` return, not an exception, for already-finished transactions** — both methods return `false` on an already-`Committed`/`Rollbacked` transaction and `true` on an empty (`Created`-state) one; only CRUD calls on a finished transaction throw `InvalidOperationException` (ADR-038).
- **Conflict resolution is opt-in** — pass a `ConcurrencyConflictHandler` to `Commit` for anything other than last-writer-wins; see [Optimistic Conflict Resolution](./optimistic-conflict-resolution.md) for the handler API.
- **A failed durability wait is not a failed commit** — `Immediate`-mode commits that append successfully but can't confirm the fsync raise `CommitDurabilityUncertainException`, never roll back; treat it as "committed, durability unconfirmed."
- **A lock timeout mid-commit is still possible** — holdoff suppresses the cooperative cancellation check, not lock-acquisition failure; a `LockTimeoutException` can surface before the WAL append, leaving the transaction `InProgress` and requiring an explicit `Rollback()`.

## 🧪 Tests
- [TransactionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/TransactionTests.cs) — `DoubleCommit_ReturnsFalse`,
  `DoubleRollback_ReturnsFalse`, `CrudAfterCommitOrRollback_ThrowsInvalidOperation`, `Rollback_*` series (created/
  updated/deleted/multi-component), `Commit_AfterRollback_ReturnsFalse`, `Dispose_UncommittedTransaction_AutoRollbacks`
- [TransactionUnitOfWorkContextTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/TransactionUnitOfWorkContextTests.cs)
  — `Commit`/`Rollback` overloads taking `ref UnitOfWorkContext`: expired-deadline-at-entry throw,
  already-committed/rolled-back returning `false`, holdoff correctness mid-commit

## 🔗 Related
- Related features: [Commit Pipeline](../Durability/commit-pipeline.md) (the append-before-publish mechanics in depth), [Deadline & Cooperative Cancellation](./deadline-cancellation.md), [Optimistic Conflict Resolution](./optimistic-conflict-resolution.md), [Unit of Work](./unit-of-work.md)

<!-- Deep dive: claude/design/Transactions/transaction-overview.md §4-5, claude/design/Transactions/04-transaction-api.md -->
<!-- Rules: claude/rules/durability.md — Module AP -->
<!-- ADR: 038 — Transaction CRUD Throws on Invalid State — claude/adr/038-transaction-throw-on-invalid-state.md -->
