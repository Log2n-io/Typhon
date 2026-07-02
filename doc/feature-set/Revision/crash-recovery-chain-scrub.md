# Crash-Recovery Chain Scrub & Orphan Sweep
> After a crash, every Versioned component's revision history collapses back to its single committed value.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Revision](./README.md)

## 🎯 What it solves

A live `Versioned` component can carry several historical revisions on disk at once, kept around so
snapshot-isolated readers that started before the latest write still see a consistent value. That reasoning
stops applying the instant the process crashes: every in-flight reader is gone, so there is no snapshot left to
protect. Left untouched, the surviving chains would carry pre-crash MVCC history into the recovered database —
and on top of that, an operation interrupted mid-allocation (a revision or content chunk claimed but never
linked into a chain) leaves chunks marked allocated that nothing references at all. Both classes of leftover
need to be gone before the recovered database is trusted as a base, or storage grows unbounded and stale
history becomes indistinguishable from live data.

## ⚙️ How it works (in brief)

After the WAL window is applied, recovery walks every Versioned component table's revision chains and
collapses each one to its single highest-TSN **committed** element, freeing every other revision's content
chunk and every overflow chunk the chain had grown. A chain with no committed survivor (every element was
either voided or belonged to a transaction that never durably committed) collapses to one invisible element
instead, so the entity still reads as deleted rather than left in an ambiguous state. Once every chain is down
to its single root chunk, a second pass scans the table's revision and content chunk pools and frees anything
still marked allocated that no surviving chain root or its head value reaches — the chunks an interrupted
pre-crash operation leaked. Chain root locations never move, which is exactly what lets the EntityMap rebuild —
a separate, earlier step keyed off the same chain-root scan — run safely before this scrub, and what gives the
secondary-index rebuild that follows the scrub a stable, already-correct address for every entity.

## 💻 Usage

This is an internal recovery step, not something application code calls — it runs automatically as part of
opening a database that crashed (WAL files present), right after the WAL window is applied and before secondary
B+Tree indexes are rebuilt. (The EntityMap is rebuilt separately, *before* WAL apply, off the same chain-root
scan — see Guarantees.)

```csharp
using Typhon.Engine;

// Recovery — including the scrub — completes before this call returns. There is
// no separate API, flag, or step to trigger it.
var engine = serviceProvider.GetRequiredService<DatabaseEngine>();

// Observable effect: every Versioned revision chain that survived recovery holds
// exactly one element, however many updates landed against it before the crash.
foreach (var seg in engine.EnumerateStorageSegments())
{
    if (seg.Kind == StorageSegmentKind.Revision)
    {
        Console.WriteLine($"revision table allocChunks={seg.AllocatedChunkCount} " +
                           $"freeChunks={seg.FreeChunkCount}"); // no pre-crash overflow chunks remain
    }
}
```

## ⚠️ Guarantees & limits

- **Crash path only.** Runs solely when recovery detects WAL files at open; a clean reopen leaves chains
  untouched for the regular revision garbage collector to trim lazily.
- **Exactly one committed survivor per chain** — the highest-TSN element with no in-flight isolation marker.
  Elements from transactions that never durably committed are discarded along with their content, never kept
  "just in case."
- **No-committed-head chains read as deleted, not corrupt** — the chain collapses to a single invisible
  element rather than vanishing outright; the entity's EntityMap entry and root chunk stay in place (a known,
  harmless residual, not a leak the orphan sweep will touch).
- **Cluster HEAD values are unaffected** — the scrub only discards non-head revisions; a cluster archetype's
  already-correct head slot is never rewritten by this step.
- **Orphan sweep is per-table and scrub-ordered** — it only reclaims a table's own leaked revision/content
  chunks, and only after that table's chains are fully scrubbed; running it earlier would treat still-live
  non-head revisions as orphans and free data that should have survived.
- **Must run before secondary-index rebuild** — B+Tree indexes are built from the scrubbed chain heads, never
  from pre-scrub history, so a torn ordering would bake stale or duplicate entries into freshly rebuilt indexes.
  The EntityMap is the one derived structure rebuilt *before* this scrub (and before WAL apply): it only needs
  each chain's stable root location plus the entity's primary key, not the chain's post-scrub content, so
  running it earlier is safe and lets every later recovery phase resolve entities immediately.
- **No application-facing trigger or inspection API** — entirely internal to crash recovery; the only visible
  effect is the post-recovery chunk footprint (via `EnumerateStorageSegments`) and the absence of stale
  revisions on subsequent reads.

## 🧪 Tests

- [DeferredCleanupTests](../../../test/Typhon.Engine.Tests/Data/DeferredCleanupTests.cs) — `Scrub_CollapsesMultiRevisionChain_ToHeadValue_Idempotent` (built directly against `ScrubChainToHead`, verifies the single-committed-HEAD collapse and idempotency) and `SweepOrphans_FreesUnreachableChunks_KeepsReachableData` (frees an interrupted-alloc orphan chunk while keeping the reachable chain root and content intact)

## 🔗 Related

- Related feature: [Crash Recovery (RecoveryDriver)](../Durability/crash-recovery/README.md), [Rebuild of Derived Structures](../Durability/crash-recovery/rebuild-derived-structures.md), [Revision Chain Storage](./revision-chain-storage.md)
- Source: [`ComponentRevisionManager.ScrubChainToHead` / `SweepTableOrphans` / `EnumerateVersionedChainHeads`](../../../src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs), [`DatabaseEngine.ScrubVersionedChains`](../../../src/Typhon.Engine/Ecs/public/DatabaseEngine.cs)

<!-- Deep dive: claude/overview/06-durability.md §6.5 — Crash Recovery (RecoveryDriver) -->
<!-- Design: claude/design/Durability/MinimalWal/03-recovery.md §6 — Phase 4 SCRUB + orphan sweep -->
<!-- Rules: claude/rules/durability.md — RB-02 (rebuild ordering), RB-03 (chain scrub postcondition), RB-05 (TSN resumption) -->
