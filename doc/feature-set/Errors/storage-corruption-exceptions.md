---
uid: feature-errors-storage-corruption-exceptions
title: 'Storage & Corruption Exceptions'
description: 'Typed failures for storage I/O, CRC32C page corruption, and another-process database locks.'
---

# Storage & Corruption Exceptions
> Typed failures for storage I/O, CRC32C page corruption, and another-process database locks.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves
Storage-layer failures need very different responses: a generic I/O error might be transient, a CRC32C mismatch on a primary page never is and demands an operator alert, and a database already opened by another process needs to name the culprit so an operator can decide whether to wait or intervene. Without typed exceptions, all three look like the same opaque `IOException`/`InvalidOperationException`, forcing the caller to parse message text to tell them apart.

## ⚙️ How it works (in brief)
`StorageException` is the subsystem-grouping base (`TyphonException`, `IsTransient = false`) for I/O and capacity failures — `catch (StorageException)` covers the whole storage layer in one clause. `CorruptionException` adds `ComponentName` and `PageIndex` for any data-integrity violation; its subclass `PageCorruptionException` is the concrete case thrown when a page's CRC32C checksum doesn't match its contents, and carries the `ExpectedCrc`/`ComputedCrc` pair. There is no on-load repair path — Typhon retired Full-Page-Image repair, so a CRC mismatch on a primary page outside crash recovery is unhealable: the exception is the only outcome, never a silently-patched page. `DatabaseLockedException` is thrown at database open when another process (or another `DatabaseEngine` instance) already holds the file, and carries `OwnerPid`, `OwnerMachine`, and `StartedAt` so the caller can report or act on who's holding it.

## 💻 Usage
```csharp
try
{
    var dbe = sp.GetRequiredService<DatabaseEngine>();
    // ... normal ECS/transaction work — CRC verification runs transparently on page load
}
catch (DatabaseLockedException ex)
{
    log.LogError("database held by PID {Pid} on '{Machine}' since {StartedAt:u}",
        ex.OwnerPid, ex.OwnerMachine, ex.StartedAt);
}
catch (PageCorruptionException ex)
{
    // never transient — a primary page failed CRC outside recovery, no on-load repair exists
    log.LogCritical(ex, "page {Page} CRC mismatch: stored=0x{Expected:X8} computed=0x{Computed:X8}",
        ex.PageIndex, ex.ExpectedCrc, ex.ComputedCrc);
}
catch (StorageException ex)
{
    // any other storage-layer failure (I/O, capacity)
    log.LogError(ex, "storage failure: {ErrorCode}", ex.ErrorCode);
}
```

## ⚠️ Guarantees & limits
- `StorageException` and its subclasses are always `IsTransient = false` — none of these failures resolve themselves on retry; corruption and lock contention both need an external action (restore from backup, close the other process).
- `PageCorruptionException` fires only for an *unhealable* mismatch on a primary page outside crash recovery — derived structures (secondary indexes, occupancy) are silently rebuilt instead of throwing, and a torn primary page caught *during* crash recovery either heals (if no live chunk references it) or fails the open loudly with a diagnostic bundle, rather than raising this exception mid-session.
- `DatabaseLockedException` only fires for a same-machine lock held by a live PID, or any lock file from a different machine name (which can't be verified remotely and is always treated as live); a stale lock from a dead local process is cleared automatically and never reaches the caller as an exception.
- `CorruptionException`'s `PageIndex` is `-1` when the corruption isn't tied to a specific page; `PageCorruptionException` always has a real page index.
- `StorageException` carries a `StorageCapacityExceeded` (2004) error code reserved for capacity-exhaustion failures, but no current throw site uses it — today only the corruption and lock-contention subclasses are live.
- Catching `Exception` instead of `StorageException` still works for compatibility, but loses the typed `ErrorCode`/`ComponentName`/`OwnerPid`-style diagnostics — prefer the specific or `StorageException` catch.

## 🧪 Tests

- [PageCrcVerificationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/PageCrcVerificationTests.cs) — a CRC32C mismatch on `OnLoad` verification throws `PageCorruptionException`; contrasting zero-CRC, `RecoveryOnly`, and root-page paths that correctly skip verification without throwing.
- [DatabaseFileLockingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/DatabaseFileLockingTests.cs) — a live same-machine lock and an unverifiable cross-machine lock both throw `DatabaseLockedException` (`OwnerPid`/`OwnerMachine`); a stale local-PID lock is cleared instead of throwing.
- [TyphonExceptionTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — `CorruptionException` property and `IsTransient == false` assertions.

## 🔗 Related
- Source: `src/Typhon.Engine/Errors/public/StorageException.cs`, `CorruptionException.cs`, `DatabaseLockedException.cs`
- Related catalog entries: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md)
- Related feature: [Database File Locking & Lifecycle](../Storage/file-locking-lifecycle.md), [Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing](../Storage/page-integrity.md)

<!-- Deep dive: claude/design/Errors/01-exception-hierarchy.md, claude/design/Errors/05-public-exception-catalog.md -->
<!-- Overview: claude/overview/10-errors.md §10.1, claude/overview/03-storage.md §3.9 -->
<!-- Rules: claude/rules/durability.md — RB-04 (suspect primary pages heal or fail loudly) -->
