# Deadline & Cooperative Cancellation
> A single absolute deadline rides every transaction commit, aborting cleanly before work starts and never leaving a partial commit behind.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Transactions](./README.md)

## 🎯 What it solves

A commit can block on contended locks, revision-chain growth, or a durability wait — with no bound, a stuck commit holds resources indefinitely and stalls every transaction behind it. Per-call timeouts don't compose: a commit that re-derives a fresh timeout at each internal step (lock acquire, index update, WAL wait) can run far longer than the caller intended. And a timeout that fires *during* a commit is worse than no timeout at all if it aborts mid-write — half the components persisted, half not. Typhon needs one deadline shared by the whole commit, cancellation that reaches code blocked on a lock or a wait (not just spinning code), and a hard guarantee that once a commit starts mutating data it cannot be cut off partway through.

## ⚙️ How it works (in brief)

`UnitOfWorkContext` is a 24-byte struct carrying an absolute `Deadline`, a `CancellationToken`, the UoW id, and a holdoff counter; it is passed `ref` into `Transaction.Commit`/`Rollback` and propagates unchanged into every lock acquisition along the commit path, so every internal wait shares the same endpoint. `Commit` checks `ctx.ThrowIfCancelled()` exactly once, at entry, before any data is touched — that is the only place a timeout or cancellation can abort the operation. Immediately after, it enters a holdoff region (`ctx.EnterHoldoff()`) that wraps the entire commit loop and nested critical sections (B+Tree splits, revision-chain appends): while in holdoff, cancellation checks are a no-op, so the deadline keeps running underneath but cannot interrupt the commit — it can only fail a lock acquisition, never abandon a half-written commit. A 200Hz `DeadlineWatchdog` fires the `CancellationToken` for any deadline within ~5ms of expiry, so threads parked on a wait (e.g. WAL durability) observe cancellation too, not just threads spinning on a lock.

## 💻 Usage

```csharp
using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
using var t = dbe.CreateQuickTransaction();
t.Spawn<CompAArch>(CompAArch.A.Set(in a));

// One relative timeout, converted to an absolute deadline once.
var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(5));

try
{
    t.Commit(ref ctx);   // every lock acquired during commit shares ctx's single deadline
}
catch (TyphonTimeoutException)
{
    // Deadline expired before commit began — transaction state is still InProgress, untouched.
    t.Rollback();
}
```

| Option | Default | Effect |
|---|---|---|
| `Commit(handler)` / `Rollback()` | `TimeoutOptions.Current.DefaultCommitTimeout` (30s) / infinite | Backward-compatible overloads — no `ref ctx` needed, existing call sites are unaffected |
| `UnitOfWorkContext.FromTimeout(TimeSpan)` | — | Converts a relative timeout to an absolute deadline, no cancellation token |
| `UnitOfWorkContext.None` | — | Infinite deadline, no cancellation — used internally for rollback/cleanup |

## ⚠️ Guarantees & limits

- **Commit atomicity** — the only yield point in `Commit()` is before any mutation begins; once the holdoff opens, the commit loop runs to completion regardless of deadline expiry. A timeout can never leave some components committed and others not.
- **Rollback always completes** — `Rollback()` runs entirely under holdoff with no yield point; cleanup is never abandoned, even with an expired deadline or cancelled token.
- **Deadline composition** — internal lock sites combine the UoW deadline with a subsystem timeout via `Deadline.Min`, so a long UoW deadline never overrides a tighter internal lock-timeout ceiling.
- **~5ms cancellation latency** — the `DeadlineWatchdog` checks registered deadlines at 200Hz; threads blocked on a wait (not spinning) observe cancellation within one tick, not instantly.
- **Zero allocation** — `UnitOfWorkContext` is a stack-passed struct; no heap object, no pooling, no GC pressure on the commit hot path.
- **A lock timeout during holdoff still throws** — holdoff suppresses the cooperative `ThrowIfCancelled()` check, not lock-acquisition failures; a `LockTimeoutException` can still surface mid-commit, leaving the transaction `InProgress` and requiring an explicit `Rollback()`.
- **Backward compatible** — `Commit()`/`Rollback()` without a context build one internally (30s default / infinite); all pre-existing call sites behave unchanged.

## 🧪 Tests

- [TransactionUnitOfWorkContextTests](../../../test/Typhon.Engine.Tests/Concurrency/TransactionUnitOfWorkContextTests.cs)
  — `Commit`/`Rollback(ref UnitOfWorkContext)`: expired-deadline and cancelled-token throw at entry,
  expired-during-holdoff still commits, holdoff nesting counter, `ComposeWaitContext_*` deadline-composition cases
- [UnitOfWorkContextTests](../../../test/Typhon.Engine.Tests/Concurrency/UnitOfWorkContextTests.cs) — the
  `UnitOfWorkContext` struct itself: 24-byte size, `FromTimeout`/`None`, holdoff enter/exit semantics
  (`ThrowIfCancelled` becomes a no-op inside holdoff, throws again after exit)
- [DeadlineWatchdogTests](../../../test/Typhon.Engine.Tests/Concurrency/DeadlineWatchdogTests.cs) — the 200Hz
  watchdog firing `CancellationToken`s for deadlines near expiry, so parked (not spinning) waiters observe
  cancellation

## 🔗 Related

- Related feature: [Deadline & Timeout Propagation](../Foundation/deadline-timeout-propagation.md) (the underlying `Deadline`/`WaitContext` primitives)

<!-- Deep dive: claude/design/Transactions/01-uow-context.md, claude/design/Transactions/02-deadline-watchdog.md, claude/design/Transactions/03-yield-points-holdoff.md, claude/design/Transactions/04-transaction-api.md -->
<!-- Overview: claude/overview/02-execution.md §2.4-2.6 -->
<!-- ADR: 034 — UnitOfWorkContext Struct Design — claude/adr/034-unitofworkcontext-struct-design.md -->
