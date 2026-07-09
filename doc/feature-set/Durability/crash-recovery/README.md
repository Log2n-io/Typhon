---
uid: feature-durability-crash-recovery-index
title: 'Crash Recovery (RecoveryDriver)'
description: 'Scans the WAL''s durably-committed prefix and replays it, in strict LSN order, through the engine''s own write primitives.'
---

# Crash Recovery (RecoveryDriver)
> Scans the WAL's durably-committed prefix and replays it, in strict LSN order, through the engine's own write primitives.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Durability](../README.md)

## 🎯 What it solves

A process crash, OS panic, or power loss can land mid-write — there is no guarantee about what made it to disk
at that instant. Without an automatic, deterministic recovery step, every application would need to reason
about partial writes itself, or risk silently opening a database that's missing committed data or carrying
phantom uncommitted data. Crash Recovery reconstructs the database to exactly the durably-committed prefix of
what the application wrote before the crash — no more, no less — on every open, with zero application code
involved.

## ⚙️ How it works (in brief)

On open, the engine scans its retained WAL segments and CRC-validates the chunk chain, stopping at the first
torn chunk. A transaction's records are applied only if its commit marker falls inside that valid prefix —
everything else (partial writes, never-committed transactions) is discarded. Applicable records replay in
strict ascending LSN order through the same write primitives live transactions use (spawn-if-absent,
destroy-if-present, value-overwrite), so re-running any prefix twice converges to the same state — a crash
*during* recovery is itself safe. The engine accepts no transactions until recovery, scrub, and rebuild all
complete.

## 💻 Usage

```csharp
using Typhon.Engine;

var services = new ServiceCollection();
services.AddTyphon(o => o.DatabaseFile(@"C:\data\skirmish\skirmish.typhon"));

var serviceProvider = services.BuildServiceProvider();

try
{
    // If the prior session crashed, recovery runs here — before this call returns.
    // There is no separate "recover" step, flag, or API to invoke.
    var engine = serviceProvider.GetRequiredService<DatabaseEngine>();
}
catch (CorruptionException ex)
{
    // An unhealable torn primary page (RB-04) — the open fails loudly rather than
    // silently serving partial data. Needs human intervention / restore from backup.
    _logger.LogCritical(ex, "Recovery could not open '{Component}' page {Page}", ex.ComponentName, ex.PageIndex);
}
```

## ⚠️ Guarantees & limits

- **Fully automatic** — no recovery API, flag, or manual step; it runs on every open and is effectively free on
  a clean reopen (the WAL window since the last checkpoint is empty).
- **Transaction is the atomicity unit** (LOG-04) — a `Deferred`/`GroupCommit` UoW recovers as exactly its
  durably-marked transactions, each whole, the set possibly a true prefix of what you committed. A crash never
  yields a half-applied transaction.
- **Idempotent by construction** (AP-12) — every record kind applies as spawn-if-absent / destroy-if-present /
  absolute-overwrite / value-overwrite, so re-running any prefix (including a crash during recovery itself)
  converges to the same result.
- **One write path** (AP-10) — recovery has no parallel apply routine that can drift from the live commit path;
  it reuses the engine's own primitives.
- **Loud failure over silent corruption** — an unrecoverable torn primary page throws `CorruptionException` and
  aborts the open; it never serves corrupted data. (What counts as "unrecoverable" is the Rebuild sub-feature's
  concern.)
- **Typical recovery time ~15–60 ms**, dominated by the apply phase (scan + fate < 5 ms; apply 10–50 ms for the
  WAL window since the last checkpoint, 30 s by default).
- **No inspection API today** — recovery's record/transaction counters exist for internal testing, not as a
  public surface; an application can't query "how much was replayed" after open.

## 🧪 Tests

- [TrueCrashE2ETests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/TrueCrashE2ETests.cs) — the "One True Crash Test": hard-crash via `SimulateHardCrash` with data only in the WAL, reopen replays it byte-for-byte
- [WalRecoveryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/WalRecoveryTests.cs) — the recovery orchestrator: commit-marker fate resolution, promoting committed UoWs and voiding pending ones
- [DifferentialRecoveryOracleTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/DifferentialRecoveryOracleTests.cs) — recovered state compared byte-for-byte against a pre-crash shadow model

## 🔗 Related

- Sub-features: [Rebuild of Derived Structures (scrub + rebuild, no FPI)](./rebuild-derived-structures.md)
- Sibling: [Crash-Recovery Chain Scrub & Orphan Sweep](../../Revision/crash-recovery-chain-scrub.md) — ECS-level revision-chain scrub that runs in the same recovery sequence, one layer up

<!-- Deep dive: claude/overview/06-durability.md §6.5 -->
<!-- Design: claude/design/Durability/MinimalWal/03-recovery.md -->
<!-- Testing strategy: claude/design/Durability/crash-recovery-testing.md (recovery *model* section is historical — see its banner; the deterministic fault-injection *approach* shipped) -->
<!-- Rules: claude/rules/durability.md — modules LOG, AP -->
