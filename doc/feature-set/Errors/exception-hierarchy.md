---
uid: feature-errors-exception-hierarchy
title: 'TyphonException Hierarchy & Catalog'
description: 'A single-rooted, typed exception tree with numeric codes, an IsTransient hint, and a catalog of every concrete failure type.'
---

# TyphonException Hierarchy & Catalog
> A single-rooted, typed exception tree with numeric codes, an `IsTransient` hint, and a catalog of every concrete failure type.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Errors](./README.md)

## 🎯 What it solves

Database failures span wildly different recovery strategies — a lock timeout wants a retry, a CRC mismatch wants an operator alert, a duplicate key wants a validation message back to the user. A flat `Exception` (or a grab-bag of unrelated custom types) forces every caller to either catch everything or maintain a brittle list of exact types. Typhon gives every failure a place in one typed tree, rooted at `TyphonException`, so application code can catch as broadly or narrowly as the situation calls for, and read a numeric `ErrorCode` plus an `IsTransient` hint without parsing message text.

## ⚙️ How it works (in brief)

`TyphonException : Exception` carries a `TyphonErrorCode` (numeric, grouped into per-subsystem ranges) and a virtual `IsTransient` (default `false` — subclasses opt in). `TyphonTimeoutException` is an intermediate base so `catch (TyphonTimeoutException)` handles every kind of timeout uniformly; `StorageException` and `DurabilityException` are similar subsystem-grouping intermediates. Leaf types with no sibling in their category (`UniqueConstraintViolationException`, the schema exceptions, `InvalidAccessException`) sit directly under `TyphonException` — no intermediate exists for a one-member group. The engine never retries on the caller's behalf: `IsTransient` is informational, and only the caller decides whether and how to retry.

## 💻 Usage

```csharp
try
{
    using var tx = dbe.CreateQuickTransaction();
    var e = tx.OpenMut(soldier);
    var health = e.Read(Unit.Health);
    health.Current -= 25;
    e.Set(Unit.Health, health);
    tx.Commit();
}
catch (LockTimeoutException ex)
{
    // Narrowest: this specific failure — ex.ResourceName, ex.WaitDuration
}
catch (TyphonTimeoutException ex)
{
    // Mid: any timeout (lock, transaction, page-cache/WAL back-pressure)
}
catch (TyphonException ex)
{
    // Broadest: any engine error
    _logger.LogError(ex, "Operation failed: {ErrorCode} IsTransient={IsTransient}", ex.ErrorCode, ex.IsTransient);
    throw;
}
```

## Catalog of concrete types

| Type | Parent | `IsTransient` | Notable typed properties |
|---|---|---|---|
| `TyphonTimeoutException` | `TyphonException` | `true` | `WaitDuration` |
| `LockTimeoutException`, `TransactionTimeoutException`, `PageCacheBackpressureTimeoutException`, `WalBackPressureTimeoutException` | `TyphonTimeoutException` | `true` | see [Timeout Exceptions & Deadlines](./timeout-exceptions-deadlines.md) |
| `SnapshotExpiredException` | `TyphonException` | `false` | `SnapshotTsn`, `RetainedMinTsn` |
| `StorageException` | `TyphonException` | `false` | — |
| `CorruptionException` / `PageCorruptionException` | `StorageException` | `false` | `ComponentName`, `PageIndex` / `ExpectedCrc`, `ComputedCrc` |
| `DatabaseLockedException` | `StorageException` | `false` | `OwnerPid`, `OwnerMachine`, `StartedAt` |
| `TransactionExceedsPageCacheException` | `StorageException` | `false` | `HeldPages`, `CachePages`, `MarkedPages`, `PinnedPages` |
| `DurabilityException` | `TyphonException` | `false` | — |
| `WalWriteException`, `WalSegmentException`, `WalClaimTooLargeException` | `DurabilityException` | `false` | — / `SegmentPath` / `RequestedBytes`, `BufferCapacity` |
| `CommitDurabilityUncertainException` | `DurabilityException` | `false` | `HighLsn` |
| `CommitPublishIncompleteException` | `DurabilityException` | `false` | `HighLsn`, `Durable` (committed in the WAL but not fully published; the engine stops committing — reopen, do not re-run) |
| `BulkSessionAlreadyActiveException`, `BulkSessionClosedException`, `BulkLoadCheckpointTimeoutException` | `DurabilityException` | `false` | `ActiveBulkSessionId` / `BulkSessionId` / `BulkSessionId`, `Timeout` |
| `IndexRebuildTempCorruptException` | `DurabilityException` | `false` | `Path`, `Offset`, `Check` (an open's index rebuild read back a temp block that failed a check; the open fails and the next one rebuilds) |
| `BackupException` | `DurabilityException` | `false` | — (base of every backup, restore and verify failure) |
| `BackupStoreLockedException`, `BackupStoreMismatchException`, `BackupPackCorruptException`, `BackupChainBrokenException`, `BackupFormatUnsupportedException`, `RestoreRefusedException`, `BackupAbortedException`, `BackupPlanChangedException`, `RestoreTargetUnreachableException`, `BackupArchiveDivergedException` | `BackupException` | `false`, except `BackupStoreLockedException` (`true`) and `BackupAbortedException` (`true` for a CBW stall, gate timeout or WAL flush timeout) | e.g. `HolderPid`/`HolderMachine` (store locked), `PointId`/`MissingId`/`RestorableAlternatives` (chain broken), `Reason` (restore refused / aborted / target unreachable) |
| `ResourceExhaustedException` | `TyphonException` | `true` | `ResourcePath`, `ResourceType`, `CurrentUsage`, `Limit` — see [Resource Exhaustion Handling](./resource-exhaustion-handling.md) |
| `TransactionSequenceExhaustedException` | `ResourceExhaustedException` | `false` | TSN space (48 bits) exhausted — migrate to a fresh database |
| `SchemaValidationException`, `SchemaMigrationException`, `SchemaDowngradeException` | `TyphonException` | `false` | `Diff` / `ComponentName`, `Failures` / `ComponentName`, `PersistedRevision`, `RuntimeRevision` |
| `SchemaMismatchException` | `TyphonException` | `false` | `Differences` (an exact-schema open — backup drill / restore settle — found a different build) |
| `ArchetypeSchemaException` | `InvalidOperationException` (**not** a `TyphonException`) | — | `ArchetypeName`, `Kind` (`ArchetypeSchemaErrorKind`) |
| `DatabaseIntegrityException` | `Exception` (**not** a `TyphonException`) | — | `Report` (`IntegrityReport`; verify-on-open refused the file) |
| `UniqueConstraintViolationException` | `TyphonException` | `false` | — |
| `ForeignKeyViolationException` | `TyphonException` | `ParentDying` | `Child`, `Parent`, `ParentDying` |
| `InvalidAccessException` | `TyphonException` | `false` | `SystemName`, `UndeclaredType` (opt-in; `Typhon:Checks:DeclaredAccess`) |

## ⚠️ Guarantees & limits

- Three catch granularities by construction: `catch (TyphonException)` (any engine error), `catch (TyphonTimeoutException)` / `catch (StorageException)` / `catch (DurabilityException)` (subsystem-wide), or a specific leaf type.
- `IsTransient` defaults to `false`; `TyphonTimeoutException` (and its subclasses), `ResourceExhaustedException` (except `TransactionSequenceExhaustedException`) and `BackupStoreLockedException` override it to `true`, while `ForeignKeyViolationException` and `BackupAbortedException` compute it from their state. See [IsTransient Retry Hint](./transience-hint.md) for the full retry philosophy.
- No `Context` dictionary on the base class — every subclass exposes its diagnostic data as typed, strongly-named properties, not a string-keyed bag.
- `ErrorCode` groups every exception into a stable, gap-numbered subsystem range — see [Error Code Classification](./error-codes.md).
- Not `[Serializable]` — pre-1.0, no cross-process/cross-AppDomain exception marshaling is supported.
- No nullable reference type annotations anywhere in the hierarchy (project-wide convention) — `null` is a valid, unannotated argument where documented.
- The catalog above is living — new leaf types are added under the appropriate intermediate (or directly under `TyphonException` for one-member groups) as new subsystems gain structured errors; existing types and codes are never renumbered or removed.

## 🧪 Tests

- [TyphonExceptionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — the hierarchy itself: `LockTimeoutException`/`TransactionTimeoutException` are both `TyphonTimeoutException`, every leaf is a `TyphonException`, three-granularity catch-block roundtrip (`CatchGranularity_*`), and `ErrorCode`/`IsTransient` per leaf type.

## 🔗 Related

- Related catalog entries: [Error Code Classification](./error-codes.md), [IsTransient Retry Hint](./transience-hint.md), [Timeout Exceptions & Deadlines](./timeout-exceptions-deadlines.md), [Resource Exhaustion Handling](./resource-exhaustion-handling.md)

<!-- Deep dive: claude/design/Errors/01-exception-hierarchy.md, claude/design/Errors/05-public-exception-catalog.md -->
<!-- Overview: claude/overview/10-errors.md §10.1 -->
