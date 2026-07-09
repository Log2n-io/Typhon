---
uid: feature-errors-durability-exceptions
title: 'Durability (WAL / BulkLoad / Commit) Exceptions'
description: 'Typed, fail-fast failures from the WAL writer, the commit pipeline''s durability wait, and BulkLoad session lifecycle.'
---

# Durability (WAL / BulkLoad / Commit) Exceptions
> Typed, fail-fast failures from the WAL writer, the commit pipeline's durability wait, and BulkLoad session lifecycle.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves

WAL and commit failures are not interchangeable: a disk write that fails outright must stop the engine from accepting further durable commits, a WAL claim that's larger than the whole ring buffer can never succeed no matter how many times it's retried, and an `Immediate`-mode commit whose post-publish fsync wait times out has *already committed* — it is not a failure to retry. Treating all of these as one generic I/O exception would force callers to parse messages to tell "restart the engine" apart from "this commit is fine, just unconfirmed." BulkLoad sessions add their own lifecycle failures (concurrent session, closed session, stuck checkpoint) that don't map to any standard transaction exception either. This feature gives each distinct outcome its own typed, catchable exception.

## ⚙️ How it works (in brief)

All seven types inherit `DurabilityException : TyphonException`, so `catch (DurabilityException)` is the subsystem-wide umbrella. `WalWriteException` wraps a fatal WAL writer I/O failure (ADR-020's dedicated writer thread) — not transient; the engine stops accepting durable commits and needs a restart. `WalClaimTooLargeException` and `WalSegmentException` are buffer-sizing and segment-file errors, also not transient — they're configuration problems, not contention. `CommitDurabilityUncertainException` is the **AP-02** point-of-no-return signal: by the time it's thrown, Append and Publish already succeeded and the transaction is committed and visible — only the durability *confirmation* (the post-publish FUA wait) didn't land in time. The three `BulkLoadSession` exceptions cover its own exclusivity gate, post-close calls, and the `CompleteBulkLoad` checkpoint barrier ([BulkLoad Write Path](../Durability/bulk-load.md) covers the session mechanics; this entry covers only its exceptions).

## 💻 Usage

```csharp
using var uow = db.CreateUnitOfWork(DurabilityMode.Immediate);
using var tx = uow.CreateTransaction();
UpdateAccountBalance(tx, accountId, newBalance);

try
{
    tx.Commit();
}
catch (CommitDurabilityUncertainException ex)
{
    // Committed and visible — APPEND + PUBLISH already ran. Do NOT retry the transaction.
    // Poll ex.HighLsn against DurabilityLog.DurableLsn to learn when it becomes durable.
    LogDurabilityUncertain(ex.HighLsn);
}
catch (WalWriteException)
{
    // Fatal WAL I/O — the engine will reject further durable commits. Escalate to restart.
    InitiateEngineRestart();
}

try
{
    using var bulk = db.BeginBulkLoad();
    // ... bulk.Spawn / bulk.Update ...
    bulk.CompleteBulkLoad();
}
catch (BulkLoadCheckpointTimeoutException ex)
{
    // Session is still open — caller may retry CompleteBulkLoad() or Dispose() to discard.
}
```

| Type | Error code | `IsTransient` | Notable properties |
|---|---|---|---|
| `WalWriteException` | `WalWriteFailure` (7003) | `false` | inner exception only |
| `WalClaimTooLargeException` | `WalClaimTooLarge` (7002) | `false` | `RequestedBytes`, `BufferCapacity` |
| `WalSegmentException` | `WalSegmentError` (7004) | `false` | `SegmentPath` |
| `CommitDurabilityUncertainException` | `CommitDurabilityUncertain` (7008) | `false` | `HighLsn` |
| `BulkSessionAlreadyActiveException` | `BulkSessionAlreadyActive` (7005) | `false` | `ActiveBulkSessionId` |
| `BulkSessionClosedException` | `BulkSessionClosed` (7006) | `false` | `BulkSessionId` |
| `BulkLoadCheckpointTimeoutException` | `BulkLoadCheckpointTimeout` (7007) | `false` | `BulkSessionId`, `Timeout` |

## ⚠️ Guarantees & limits

- `WalWriteException` is unconditionally fatal — there is no retry path; it signals the dedicated WAL writer thread itself failed (ADR-020), and the engine stops accepting durable commits until restarted.
- `CommitDurabilityUncertainException` means **"committed, durability unconfirmed,"** never a rollback — `IsTransient` is `false` specifically to stop callers from auto-retrying a transaction that already happened.
- `WalClaimTooLargeException` / `WalSegmentException` are not transient: a larger buffer or a fixed segment path is needed before the same operation can succeed.
- The three `BulkLoadSession` exceptions are scoped to that session's own state machine — `BulkSessionAlreadyActiveException` from a second `BeginBulkLoad`, `BulkSessionClosedException` from any call after `CompleteBulkLoad`/`Dispose`, `BulkLoadCheckpointTimeoutException` only from `CompleteBulkLoad` (the session stays open afterward — retry or dispose).
- All seven inherit `DurabilityException`, so `catch (DurabilityException)` catches the whole family uniformly for logging/alerting, while specific catches still get typed properties (`HighLsn`, `SegmentPath`, etc.).
- None of these are part of the `TyphonTimeoutException` family, even though `BulkLoadCheckpointTimeoutException` is deadline-driven — it stays under `DurabilityException` because the BulkLoad session, unlike a lock wait, remains alive and resumable after the timeout.

## 🧪 Tests

- [WalCommitBufferTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalCommitBufferTests.cs) — an over-capacity claim throws `WalClaimTooLargeException`; a ring-buffer claim that outlives its deadline throws `WalBackPressureTimeoutException`.
- [BulkLoadApiSurfaceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/BulkLoadApiSurfaceTests.cs) — a second concurrent `BeginBulkLoad` throws `BulkSessionAlreadyActiveException` (`ActiveBulkSessionId`); any call after `Dispose`/`CompleteBulkLoad` throws `BulkSessionClosedException` (`CompleteBulkLoad`, `Destroy`).

## 🔗 Related

- Related catalog entries: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md), [Commit Pipeline](../Durability/commit-pipeline.md), [BulkLoad Write Path](../Durability/bulk-load.md), [Bulk Load Session](../Transactions/bulk-load-session.md)

<!-- Deep dive: claude/overview/06-durability.md §6.2 (AP-02), §6.8 (BulkLoad) -->
<!-- Design: claude/design/Errors/05-public-exception-catalog.md, claude/design/Durability/BulkLoad/01-api.md -->
<!-- ADR: claude/adr/020-dedicated-wal-writer-thread.md, claude/adr/053-bulk-load-write-path.md -->
<!-- Rules: claude/rules/durability.md — modules LOG, AP, BL -->
