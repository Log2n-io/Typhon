---
uid: feature-errors-error-codes
title: 'Error Code Classification'
description: 'A numeric TyphonErrorCode per failure, grouped into subsystem ranges, for logging and metrics — not for catching.'
---

# Error Code Classification
> A numeric `TyphonErrorCode` per failure, grouped into subsystem ranges, for logging and metrics — not for catching.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves

Operators need a stable, compact identifier to log, alert on, and group failures by in dashboards — a string message or a C# type name doesn't serialize cleanly into metrics labels, and message text can change without notice. `TyphonErrorCode` gives every thrown `TyphonException` a numeric code whose range alone tells you which subsystem failed (transaction, storage, schema, index, resource, durability, runtime), even before you look at the exception type.

## ⚙️ How it works (in brief)

`TyphonErrorCode` is a flat `enum` with codes grouped into per-subsystem ranges (`1xxx` Transaction, `2xxx` Storage, `3xxx` Component/Schema, `4xxx` Index, `6xxx` Resource, `7xxx` Durability, `8xxx` Runtime/Scheduler). Every `TyphonException` carries one via its `ErrorCode` property, set once at construction and never changed. Codes are sequential within a range with intentional numeric gaps, so new codes can be inserted later without renumbering existing ones; gaps in the range table (e.g. `5xxx` Query) are reserved for subsystems that don't throw yet. There is no separate `ErrorCategory` enum — the **exception type hierarchy** is the classification axis you `catch` on; `ErrorCode` is for logging/metrics, not control flow.

## 💻 Usage

```csharp
try
{
    using var tx = dbe.CreateQuickTransaction();
    var e = tx.OpenMut(soldier);
    e.Write(Unit.Health).Current -= 25;
    tx.Commit();
}
catch (TyphonException ex)
{
    // ErrorCode groups by subsystem range for metrics/log aggregation;
    // the catch type (here, TyphonException) is what drives the actual handling decision.
    _logger.LogError(ex, "Operation failed: {ErrorCode} IsTransient={IsTransient}",
        ex.ErrorCode, ex.IsTransient);
    throw;
}
```

| Range | Subsystem | Example codes |
|---|---|---|
| 1xxx | Transaction | `TransactionTimeout` (1002) |
| 2xxx | Storage | `DataCorruption` (2003), `PageChecksumMismatch` (2005), `DatabaseLocked` (2007) |
| 3xxx | Component / Schema | `SchemaValidation` (3001), `SchemaMigration` (3002) |
| 4xxx | Index | `UniqueConstraintViolation` (4001) |
| 6xxx | Resource | `ResourceExhausted` (6001), `LockTimeout` (6003) |
| 7xxx | Durability | `WalBackPressureTimeout` (7001), `WalWriteFailure` (7003), `CommitDurabilityUncertain` (7008) |
| 8xxx | Runtime / Scheduler | `InvalidSystemAccess` (8001) |

## ⚠️ Guarantees & limits

- **Range tells you the subsystem at a glance** — no need to open the type hierarchy to know a `7xxx` code came from durability.
- **Codes are append-only** — gaps within and between ranges are intentional headroom; existing codes never get renumbered, so logged/stored codes stay valid across engine versions.
- **Not exhaustive of every subsystem yet** — `5xxx` (Query) has no codes defined; future subsystems will land in their reserved range as exceptions are added there.
- **Not the catch axis** — don't `switch` on `ErrorCode` to decide control flow; `catch` the exception type instead (see the Exception Hierarchy entry in this category). `ErrorCode` is for telemetry, dashboards, and log correlation.
- **One code per exception instance, set at construction** — it never changes after the exception is thrown.

## 🧪 Tests

- [TyphonExceptionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — `ErrorCodeUniqueness_NoDuplicateValues` guards the whole `TyphonErrorCode` enum against colliding numeric values; per-type `ErrorCode` assignment is asserted alongside each exception's other properties (e.g. `LockTimeoutException_Properties`, `CorruptionException_Properties`).

## 🔗 Related

- Parent feature: [Errors](./README.md)
- Sibling: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md) — `ErrorCode` is carried by every exception in this hierarchy; the type, not the code, is the catch axis
- Sibling: [Durability (WAL / BulkLoad / Commit) Exceptions](./durability-exceptions.md) — the `7xxx` range this table cites is defined there
- Source: `src/Typhon.Engine/Errors/public/TyphonErrorCode.cs`

<!-- Deep dive: claude/overview/10-errors.md §10.2 -->
<!-- Design: claude/design/Errors/01-exception-hierarchy.md -->
