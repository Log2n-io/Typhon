---
uid: feature-durability-commit-pipeline
title: 'Commit Pipeline (append-before-publish)'
description: 'Transaction.Commit''s VALIDATE→PREPARE→BUILD→APPEND→PUBLISH→WAIT ordering guarantees nothing is visible before its WAL record is appended, and publish never…'
---

# Commit Pipeline (append-before-publish)
> Transaction.Commit's VALIDATE→PREPARE→BUILD→APPEND→PUBLISH→WAIT ordering guarantees nothing is visible before its WAL record is appended, and publish never rolls back.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](./README.md)

## 🎯 What it solves

A commit has to update many things at once — entity visibility, indexes, the WAL — and a crash can land between any two of them. If a change became visible (readable by other transactions, or capturable by a checkpoint) before its WAL record existed, a crash would leave readers having seen data that recovery can never reproduce: phantom state. Conversely, if "did it commit?" depended on multiple independent writes succeeding, a partial failure would leave the transaction in an undefined state — neither cleanly committed nor cleanly rolled back. The commit pipeline removes both failure modes by fixing one strict order and one irreversible step.

## ⚙️ How it works (in brief)

`Transaction.Commit` runs six phases in order: **VALIDATE** (conflict checks), **PREPARE** (all fallible work — allocation, index-key extraction — without touching visibility), **BUILD** (assemble the WAL batch from the prepared values), **APPEND** (write the batch to the WAL — the point of no return), **PUBLISH** (flip visibility — clear isolation, copy values to their committed slot, update indexes and the entity map), **WAIT** (only for `DurabilityMode.Immediate`: block until the appended batch is fsync'd). APPEND is the point of no return: once it succeeds, the transaction *is* committed and PUBLISH never rolls back. PUBLISH itself does no fallible allocation, so it cannot fail partway — it either runs to completion or the process is already going down for unrelated reasons. WAIT runs last and only for `Immediate` commits, specifically so the durability fsync is never on the critical path of any lock PUBLISH might still be holding.

## 💻 Usage

This pipeline runs on every `Commit()` — there is no separate API to invoke it. The only place it surfaces to application code is the outcome of an `Immediate`-mode commit whose post-append durability wait didn't confirm in time:

```csharp
using var uow = db.CreateUnitOfWork(DurabilityMode.Immediate);
using var tx = uow.CreateTransaction();

UpdateAccountBalance(tx, accountId, newBalance);

try
{
    tx.Commit();   // VALIDATE→PREPARE→BUILD→APPEND→PUBLISH→WAIT; returns once durable
}
catch (CommitDurabilityUncertainException ex)
{
    // The transaction is committed and visible (APPEND succeeded, PUBLISH ran) — this is
    // NOT a rollback. The WAL fsync that confirms it survives a crash didn't finish in time.
    // Do not retry the transaction. Optionally poll ex.HighLsn against the durability watermark.
    LogDurabilityUncertain(ex.HighLsn);
}
```

## ⚠️ Guarantees & limits

- **Append before publish** — no other transaction, index, or checkpoint can ever observe a change before its WAL record exists; a checkpoint can never persist never-durable data.
- **Append is the point of no return** — all conflict validation happens before APPEND; after APPEND, the transaction reaches `Committed` and is never rolled back, even if the subsequent durability wait fails.
- **A failed durability wait is not a failed commit** — `CommitDurabilityUncertainException` means "committed, durability unconfirmed," not "rolled back." Never re-run the transaction in response to it.
- **Publish is (mostly) non-throwing** — the per-component publish step is provably allocation-free. Spawning new entities retains a residual allocation-throw risk under page-cache backpressure (tracked separately; not a correctness gap, just an unclosed edge case).
- **No cost beyond the mode you chose** — the ordering itself adds no latency: `Deferred`/`GroupCommit` commits stay ~1–2 µs; `Immediate` pays exactly one FUA round-trip (~15–85 µs), same as without this pipeline.
- Concurrent commits on entities with conflicting writes resolve against the *published* value of the entity, not an intermediate state — the per-entity lock spans PREPARE through PUBLISH for that reason.

## 🧪 Tests

- [AppendBeforePublishTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/AppendBeforePublishTests.cs) — proves visible-implies-appended (AP-01) and that a concurrent reader never observes a value ahead of its WAL append, on the real WAL pipeline

## 🔗 Related

- Sibling: [Write-Ahead Log (WAL v2 logical records)](./wal-v2.md) — APPEND writes the assembled commit batch straight into this log
- Sibling: [Commit / Rollback Pipeline (ACID Commit Path)](../Transactions/commit-rollback-pipeline.md) — the Transaction-level view of this same PREPARE→append→PUBLISH ordering

<!-- Deep dive: claude/overview/06-durability.md §6.2 -->
<!-- Design: claude/design/Durability/MinimalWal/01-architecture.md §5 -->
<!-- Rules: rules/durability.md — module AP -->
