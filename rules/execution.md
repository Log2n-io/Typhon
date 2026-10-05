# Execution Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-10-05 |
| Domain | Transaction lifecycle: Commit, Rollback, Dispose, the write entry points' state guard |

> Invariants of the unit-of-work / transaction layer. The commit path's ordering against the WAL lives in
> `durability.md` (module AP); this file holds what the transaction's own state machine promises its caller.

---

## Module: TX — Transaction lifecycle

A read-write transaction goes `Created → InProgress → Committed | Rollbacked`; the first write moves it to `InProgress`.
One that never wrote is empty, and goes `Created → Committed | Rollbacked` directly. Reads stay available in every state
(ADR-038). A read-only transaction refuses every write through `IsReadOnly`, whatever its state.

### TX-01: Commit and Rollback end the transaction, an empty one included `[silent]`
  invariant ∀ read-write transaction t: t.Commit() or t.Rollback() returns true → afterwards t.State ∈ {Committed,
            Rollbacked}, whether t was `InProgress` or empty (`Created`) — the empty fast path skips the work, never the
            transition
  invariant ∀ transaction t, read-only included: t.State ∈ {Committed, Rollbacked} → t.Commit() and t.Rollback() return
            false, and every write entry point throws `InvalidOperationException` (`EnsureMutable`)
  invariant the empty fast path does no commit or rollback work: no chain lock, no commit or rollback body, no
            `CommitTotal` / `RollbackTotal` increment
  never a Commit() that returns true on a transaction that still accepts writes: a write made after it lands only with
        a second Commit(), or is dropped without a word by Dispose's auto-rollback
  note: a read-only transaction's Commit() is a no-op that leaves it in `Created`; its Rollback() ends it like any other.
  scope: Transaction.Commit, Transaction.Rollback, Transaction.EnsureMutable, Transaction.EnsureCompleted,
         Transaction.Dispose, Transaction.PrepareOpenMut, Transaction.Spawn, Transaction.SpawnBatch,
         Transaction.SpawnBatchAllocate, Transaction.Destroy, Transaction.DestroyBatch
  on_violation: before #1056 both empty fast paths returned true and left the transaction in `Created`, so `OpenMut`,
                `TryOpenMut`, `Spawn` and `Destroy` still succeeded after a successful Commit(). Those writes needed a
                second Commit() to land, or were rolled back silently by Dispose.
  verified: TransactionTests.Commit_EmptyTransaction_IsTerminal [VerifiesRule] (state, refused writes, second Commit /
            Rollback false, counters unchanged), TransactionTests.Rollback_EmptyTransaction_IsTerminal [VerifiesRule]
            (the same for Rollback), TransactionTests.ReadOnly_RolledBack_CannotCommit [VerifiesRule] (the finished-state
            check runs before the read-only no-op). Each fails with the code it guards reverted, checked.
