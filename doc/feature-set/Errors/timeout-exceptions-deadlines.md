---
uid: feature-errors-timeout-exceptions-deadlines
title: 'Timeout Exceptions & Deadline Propagation'
description: 'Configurable, finite deadlines replace infinite waits, turning every contention hang into a typed, catchable timeout exception.'
---

# Timeout Exceptions & Deadline Propagation
> Configurable, finite deadlines replace infinite waits, turning every contention hang into a typed, catchable timeout exception.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Errors](./README.md)
**Assumes:** [Deadline & Timeout Propagation](../Foundation/deadline-timeout-propagation.md)

## 🎯 What it solves

Every lock acquisition and buffer claim in Typhon used to wait forever — `ref WaitContext.Null` meant any
contention, stuck writer, or resource exhaustion hung the caller indefinitely, indistinguishable from a deadlock.
There was no way to bound how long an operation could block, and no structured signal when it did. This feature
gives every wait a finite, per-subsystem-configurable deadline and converts an expired wait into a specific
exception type carrying what was being waited for and how long — never a silent hang, never a generic
`System.TimeoutException`.

## ⚙️ How it works (in brief)

`TimeoutOptions` (on `DatabaseEngineOptions.Timeouts`) holds one `TimeSpan` per subsystem — page cache, B+Tree,
transaction chain, revision chain, segment allocation — plus back-pressure and bulk-load checkpoint budgets.
Each call site builds a `WaitContext.FromTimeout(...)` fresh (an absolute monotonic deadline, not a wall-clock
one) and passes it `ref` into the lock primitive. Lock primitives never throw — they return `false` on expiry —
so the *subsystem* checks the result and throws the appropriate `TyphonTimeoutException` subclass. The throw
always happens at the acquisition point, before any mutation begins, so a timeout never leaves a structural
operation half-done.

## 💻 Usage

```csharp
var options = new DatabaseEngineOptions
{
    Timeouts = new TimeoutOptions
    {
        BTreeLockTimeout = TimeSpan.FromSeconds(2),
        TransactionChainLockTimeout = TimeSpan.FromSeconds(10),
    },
};

try
{
    using var tx = dbe.CreateQuickTransaction();
    var e = tx.OpenMut(playerId);
    e.Write(Player.Wallet).Gold -= price;
    tx.Commit();
}
catch (TyphonTimeoutException ex)
{
    // Catches LockTimeoutException, TransactionTimeoutException,
    // PageCacheBackpressureTimeoutException, and WalBackPressureTimeoutException alike.
    _logger.LogWarning("Operation timed out after {Wait}ms", ex.WaitDuration.TotalMilliseconds);
    // ex.IsTransient is always true here — caller decides whether/how to retry.
}
```

| `TimeoutOptions` property | Default | Governs |
|---|---|---|
| `PageCacheLockTimeout` | 5s | Page cache state-transition locks |
| `BTreeLockTimeout` | 5s | B+Tree insert/delete/lookup locks |
| `TransactionChainLockTimeout` | 10s | Transaction chain create/remove/walk |
| `RevisionChainLockTimeout` | 5s | MVCC revision read/add/cleanup |
| `SegmentAllocationLockTimeout` | 10s | Chained-block allocator / segment growth |
| `PageCacheBackpressureTimeout` | 5s | Waiting for dirty pages to flush so allocation can proceed |
| `WalBackPressureTimeout` | 5s | Waiting for the WAL ring buffer to drain |
| `DefaultCommitTimeout` / `DefaultUowTimeout` | 30s | Outer bound when `Transaction.Commit()` / a UoW is created without an explicit timeout |
| `BulkLoadOptions.CheckpointTimeout` | 5 min | `BulkLoadSession.CompleteBulkLoad`'s forced synchronous checkpoint |

## ⚠️ Guarantees & limits

- `TyphonTimeoutException` is the common catchable base; `IsTransient` is always `true` for the whole family —
  the resource is presumed available later, but the engine never retries automatically.
- Four concrete leaves: `LockTimeoutException` (carries `ResourceName`), `TransactionTimeoutException` (carries
  `TransactionId`), `PageCacheBackpressureTimeoutException` (carries `DirtyPageCount`/`EpochProtectedCount`),
  `WalBackPressureTimeoutException` (carries `RequestedBytes`) — each also carries `WaitDuration`.
- `BulkLoadCheckpointTimeoutException` is a deadline-driven timeout in spirit but inherits `DurabilityException`,
  not `TyphonTimeoutException` — it does not surface in a `catch (TyphonTimeoutException)` block, and
  `IsTransient` defaults to `false`. Unlike the other four, the underlying `BulkLoadSession` remains open after
  this exception; the caller may retry `CompleteBulkLoad` or dispose the session.
- Deadlines are monotonic (`Stopwatch`-based), not wall-clock — immune to NTP/DST jumps — and are computed fresh
  at each call site rather than inherited, so nested lock acquisitions each get their own full window (not yet
  a single shared transaction-wide budget — that is a future Execution Context tier).
- Checking `WaitContext.ShouldStop` costs ~10-25ns per spin iteration — paid only while contended; the
  uncontended fast path skips the check entirely.
- Test code should use `TestWaitContext.Default` (a fresh 10s deadline) instead of `WaitContext.Null` in
  multi-threaded contention tests, to avoid genuinely hanging the test runner on a regression.

## 🧪 Tests

- [DeadlinePropagationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/DeadlinePropagationTests.cs) — `TimeoutOptions` defaults/overrides, `AccessControl`/`ResourceAccessControl` lock contention throwing `LockTimeoutException` once the deadline expires, and `TestWaitContext` expiry semantics.
- [TyphonExceptionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — `LockTimeoutException`/`TransactionTimeoutException` typed properties (`ResourceName`/`TransactionId`/`WaitDuration`) and the `catch (TyphonTimeoutException)` mid-granularity roundtrip.

## 🔗 Related

- Related catalog entries: [Deadline & Timeout Propagation](../Foundation/deadline-timeout-propagation.md) (the underlying `Deadline`/`WaitContext` mechanism), [TyphonException Hierarchy & Catalog](./exception-hierarchy.md)

<!-- Deep dive: claude/design/Errors/02-deadline-propagation.md -->
<!-- Overview: claude/overview/10-errors.md, claude/overview/01-concurrency.md -->
<!-- ADR: claude/adr/031-unified-concurrency-patterns.md (031 — Unified Concurrency Patterns) -->
