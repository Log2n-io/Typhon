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
    e.Write(Unit.Health).Current -= 25;
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
| `StorageException` | `TyphonException` | `false` | — |
| `CorruptionException` / `PageCorruptionException` | `StorageException` | `false` | `ComponentName`, `PageIndex` / `ExpectedCrc`, `ComputedCrc` |
| `DatabaseLockedException` | `StorageException` | `false` | `OwnerPid`, `OwnerMachine`, `StartedAt` |
| `DurabilityException` | `TyphonException` | `false` | — |
| `WalWriteException`, `WalSegmentException`, `WalClaimTooLargeException` | `DurabilityException` | `false` | `SegmentPath` / `RequestedBytes`, `BufferCapacity` |
| `ResourceExhaustedException` | `TyphonException` | `true` | see [Resource Exhaustion Handling](./resource-exhaustion-handling.md) |
| `SchemaValidationException`, `SchemaMigrationException`, `SchemaDowngradeException` | `TyphonException` | `false` | `Diff`, `Failures`, `PersistedRevision`/`RuntimeRevision` |
| `UniqueConstraintViolationException` | `TyphonException` | `false` | — |
| `InvalidAccessException` | `TyphonException` | `false` | `SystemName`, `UndeclaredType` (DEBUG-only) |

## ⚠️ Guarantees & limits

- Three catch granularities by construction: `catch (TyphonException)` (any engine error), `catch (TyphonTimeoutException)` / `catch (StorageException)` / `catch (DurabilityException)` (subsystem-wide), or a specific leaf type.
- `IsTransient` defaults to `false`; only `TyphonTimeoutException` (and its subclasses) and `ResourceExhaustedException` override it to `true`. See [IsTransient Retry Hint](./transience-hint.md) for the full retry philosophy.
- No `Context` dictionary on the base class — every subclass exposes its diagnostic data as typed, strongly-named properties, not a string-keyed bag.
- `ErrorCode` groups every exception into a stable, gap-numbered subsystem range — see [Error Code Classification](./error-codes.md).
- Not `[Serializable]` — pre-1.0, no cross-process/cross-AppDomain exception marshaling is supported.
- No nullable reference type annotations anywhere in the hierarchy (project-wide convention) — `null` is a valid, unannotated argument where documented.
- The catalog above is living — new leaf types are added under the appropriate intermediate (or directly under `TyphonException` for one-member groups) as new subsystems gain structured errors; existing types and codes are never renumbered or removed.

## 🧪 Tests

- [TyphonExceptionTests](../../../test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — the hierarchy itself: `LockTimeoutException`/`TransactionTimeoutException` are both `TyphonTimeoutException`, every leaf is a `TyphonException`, three-granularity catch-block roundtrip (`CatchGranularity_*`), and `ErrorCode`/`IsTransient` per leaf type.

## 🔗 Related

- Related catalog entries: [Error Code Classification](./error-codes.md), [IsTransient Retry Hint](./transience-hint.md), [Timeout Exceptions & Deadlines](./timeout-exceptions-deadlines.md), [Resource Exhaustion Handling](./resource-exhaustion-handling.md)

<!-- Deep dive: claude/design/Errors/01-exception-hierarchy.md, claude/design/Errors/05-public-exception-catalog.md -->
<!-- Overview: claude/overview/10-errors.md §10.1 -->
