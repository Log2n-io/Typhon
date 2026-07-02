# Rebuild of Derived Structures (scrub + rebuild, no FPI)
> Derived structures — indexes, EntityMap, occupancy — are never repaired after a crash; they're discarded and rebuilt from primary data.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Durability](../README.md)

## 🎯 What it solves

A torn index page or a torn EntityMap page can't be patched byte-for-byte the way a per-page repair scheme
would patch a torn data page — an index is wholly redundant with the data it indexes, and a half-repaired
EntityMap (a hash directory of raw chunk-id pointers) is worse than no map at all: dereferencing a torn pointer
crashes the process before any safety check can catch it. Typhon doesn't attempt to repair these structures.
Every derived structure is rebuilt wholesale from the data it derives from, so a torn on-disk copy is simply
replaced rather than trusted, patched, or guessed at.

## ⚙️ How it works (in brief)

After the WAL window is applied and every Versioned revision chain is collapsed to its single committed value,
the engine clears each derived structure — secondary B+Tree indexes (and their multi-value buffers), the
EntityMap, the page-occupancy bitmap — and repopulates it by scanning the now-final primary data: indexes from
each table's committed values, the EntityMap from cluster occupancy slots (or revision-chain heads for flat
archetypes), occupancy from actual page ownership. A torn page that happened to belong to one of these
structures needs no special handling — it's simply part of what gets discarded. Primary data (component
content, cluster slots, and the rare EntityMap that has no cluster/chain source to rebuild from) follows a
different rule: heal silently if no longer referenced by anything live, or fail the open loudly if it still
backs live data. There is no Full-Page-Image repair anywhere in the engine — that mechanism was retired in
favor of this rebuild-or-loud-fail net.

## 💻 Usage

```csharp
// Same DI-resolved open as Crash Recovery — rebuild is an internal recovery phase,
// there's nothing to call. What distinguishes it from the application's point of view:
// a torn INDEX, EntityMap, or occupancy page never surfaces an exception — it's silently
// rebuilt. Only a torn PRIMARY page that still backs live data does.
try
{
    var engine = serviceProvider.GetRequiredService<DatabaseEngine>();
    // No exception ⇒ indexes/EntityMap/occupancy are guaranteed consistent with the
    // recovered primary data, even if their on-disk copies were torn before the crash.
}
catch (CorruptionException ex)
{
    // RB-04: a PRIMARY page the rebuild net could not heal — never a derived
    // (index/EntityMap/occupancy) page; those are always rebuilt, never reported.
    _logger.LogCritical(ex, "Unrecoverable primary page in '{Component}' at page {Page}", ex.ComponentName, ex.PageIndex);
}
```

## ⚠️ Guarantees & limits

- **Always rebuilt, never repaired** — secondary B+Tree indexes, the page-occupancy bitmap, and the EntityMap of
  any "rebuildable" archetype (cluster-eligible, or every non-`Transient` slot `Versioned`) are unconditionally
  cleared and rebuilt after every crash recovery; a CRC-clean derived page on disk is not even consulted.
- **Ordering is load-bearing** — rebuild runs after the WAL window is applied and Versioned chains are scrubbed
  to their single committed HEAD, so indexes are built from final values, never from pre-crash MVCC history.
  The occupancy bitmap rebuild specifically runs after recovery's own seal checkpoint, since that checkpoint can
  still grow segments — page ownership is only final afterward.
- **Heal-or-loud-fail is reserved for primary data** — component/revision content, collections, cluster slots,
  and the rare non-rebuildable EntityMap (a non-cluster archetype still holding a `SingleVersion` slot with no
  persisted source to rebuild from). A torn primary page heals only if it no longer backs any live chunk (the
  entity was re-created within the recovery window); otherwise the open fails with `CorruptionException` rather
  than silently opening over corrupt data.
- **No Full-Page-Image anywhere** — the mechanism that historically repaired pages byte-for-byte (a before-image
  written per dirty page per checkpoint cycle) has been removed entirely; this rebuild net is the sole torn-page
  protection for derived structures, at zero steady-state cost — a clean reopen never enters the rebuild phase.
- **Cost scales with live data, not WAL size** — rebuild is a full scan of the post-recovery primary data (a few
  ms for typical entity/index counts), not an incremental repair of only the torn pages.

## 🧪 Tests

- [DifferentialRecoveryOracleTests](../../../../test/Typhon.Engine.Tests/Durability/CrashRecovery/DifferentialRecoveryOracleTests.cs) — index axis (RB-01: secondary B+Tree rebuilt at recovery) and cluster axis (EntityMap/SV rebuild) proofs
- [WalIntegrationTests](../../../../test/Typhon.Engine.Tests/Durability/WalIntegrationTests.cs) — `WAL_Destroy_TombstonedEntitiesExcludedFromEntityMapRebuild`: EntityMap rebuild on reopen correctly excludes tombstoned entities
- [RawValueHashMapTests](../../../../test/Typhon.Engine.Tests/Data/ECS/RawValueHashMapTests.cs) — `ClearForRebuild`/`InsertDuringRebuild` on the hash map backing the EntityMap, the primitive the rebuild phase drives

## 🔗 Related

- Parent feature: [Crash Recovery (RecoveryDriver)](./README.md)
- Sibling: [Crash-Recovery Chain Scrub & Orphan Sweep](../../Revision/crash-recovery-chain-scrub.md) — this rebuild runs after chain scrub collapses every Versioned chain to its committed HEAD

<!-- Deep dive: claude/overview/06-durability.md §6.6 — Torn-Page Safety (no FPI) -->
<!-- Design: claude/design/Durability/MinimalWal/03-recovery.md §6–7 -->
<!-- Historical (superseded): claude/design/Durability/fpi-durability.md -->
<!-- Rules: claude/rules/durability.md — module RB (RB-01..05), CK-09 -->
