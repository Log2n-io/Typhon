---
uid: feature-revision-revision-gc-compaction
title: 'Revision Garbage Collection & Compaction'
description: 'Automatic, bounded-memory reclamation of old MVCC revisions once no active transaction can see them.'
---

# Revision Garbage Collection & Compaction
> Automatic, bounded-memory reclamation of old MVCC revisions once no active transaction can see them.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Revision](./README.md)

## 🎯 What it solves

Every overwrite of a `Versioned` component appends a new revision rather than mutating in place, so snapshot
reads keep working while the write happens. Left unchecked, that history grows forever — a hot entity updated
thousands of times would carry thousands of dead revisions, inflating storage and slowing every read that has
to walk past them to find the visible one. Revision GC reclaims revisions as soon as they're provably
unreachable, so chain length and storage footprint track *live* MVCC demand instead of cumulative write
history.

## ⚙️ How it works (in brief)

GC is keyed off `MinTSN`, the oldest transaction snapshot still active — only revisions older than `MinTSN` are
candidates for reclamation, since some open transaction may still need to resolve a read against them. When the
transaction holding the oldest snapshot completes, the engine compacts every chain it touched right away. If a
different (non-tail) transaction commits while an older one is still running, its cleanup is queued instead of
done immediately, and runs automatically once that older transaction finally completes. Either way, one
revision is always kept alive at the boundary — so a transaction reading at exactly `MinTSN` still resolves
correctly — and once every revision in a chain collapses to a single "deleted" marker, the entity itself is
removed.

## 💻 Usage

GC requires no application code — it runs transparently as a side effect of transaction completion:

```csharp
using var tx1 = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
var unit = tx1.Spawn<Unit>(Unit.Health.Set(new Health { Current = 100, Max = 100 }));
tx1.Commit();

// A long-running reader holds back the cleanup cutoff while updates accumulate.
using var reader = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
reader.OpenRead(unit, Unit.Health);

for (var i = 0; i < 50; i++)
{
    using var writer = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
    writer.OpenMut(unit).Write(Unit.Health).Current -= 1;
    writer.Commit();                    // each commit queues cleanup, blocked by `reader`
}

reader.Dispose();                       // reader was the tail — its disposal compacts every queued chain
```

The only application-facing knobs are queue-health thresholds, set via `DatabaseEngineOptions.DeferredCleanup`
at construction; queue depth and throughput are readable back through the engine's diagnostics surface:

```csharp
var options = new DatabaseEngineOptions
{
    DeferredCleanup = new DeferredCleanupOptions
    {
        HighWaterMark     = 100_000,    // log a warning once this many entities are queued (default)
        CriticalThreshold = 1_000_000,  // log a critical warning past this point (default)
        MaxCleanupBatchSize = 1000,     // entities reclaimed per deferred-cleanup pass (default)
    },
};

var props = dbe.GetDebugProperties();
Console.WriteLine($"pending={props["DeferredCleanup.QueueSize"]} processed={props["DeferredCleanup.ProcessedTotal"]}");
```

## ⚠️ Guarantees & limits

- A revision is never reclaimed while a transaction could still need it — the cutoff is the oldest live
  snapshot (`MinTSN`), not a timer or a fixed chain-length limit.
- Reclamation is eventual, not instant, when a long-running transaction holds `MinTSN` back: writes against a
  hot entity keep accumulating queued cleanup work, bounded and deduplicated per entity, until that transaction
  completes.
- A deleted entity's chain isn't removed immediately — it keeps a single "deleted" marker alive until no
  transaction could possibly observe the entity again, so concurrent readers reliably see "deleted" rather than
  "never existed".
- Compaction never changes what a transaction can observe — it only discards copies that are already provably
  invisible to every active and future transaction.
- This applies to `Versioned`-mode components only; `SingleVersion`/`Transient` storage has no revision chain
  to reclaim (see [Storage Modes](../Ecs/storage-modes/README.md)).
- Cost is proportional to chain length at the moment of reclamation — short chains (the common case) compact in
  low single-digit microseconds; long chains (entities updated heavily while a reader stalls them) cost more,
  but only once, when the blocking transaction finally completes.

## 🧪 Tests

- [DeferredCleanupTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/DeferredCleanupTests.cs) — tail-blocked cleanup on commit/dispose/rollback, queue dedup and TSN-bucket migration, partial-queue processing with sentinel preservation, matches the doc's own Usage sample almost line for line
- [ChaosStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ChaosStressTests.cs) — `RevisionChainDepth_DeepChainWithCleanup` (500+ revisions reclaimed as staggered readers release oldest-first), `SentinelRevision_StaggeredReaderRelease` (reverse-order release stresses the sentinel boundary when `nextMinTSN` doesn't match the first kept entry's TSN)

## 🔗 Related

- Related feature: [Revision Chain Storage](./revision-chain-storage.md) (the layout this reclaims space within),
  [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md) (the reader the sentinel revision protects)
- Sibling: [Chain Walk Correctness Under Compaction](./mvcc-visibility-walk.md) — the visibility-side counterpart that tolerates this compactor's relocations
- Source: [`ComponentRevisionManager.CleanUpUnusedEntriesCore`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs),
  [`DeferredCleanupManager`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/internals/DeferredCleanupManager.cs),
  [`DeferredCleanupOptions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DeferredCleanupOptions.cs)

<!-- Deep dive: claude/design/Revision/03-revision-gc-compaction.md, claude/overview/04-data.md §4.9 GC & Space Reclamation -->
<!-- ADR: claude/adr/035-deferred-cleanup-hybrid-gc.md -->
