# Batched Index Maintenance for Bulk Commits
> Commit-path rework that batches secondary-index updates per commit; the accessor-reuse half has shipped, the sorted-apply half has not.

**Status:** 🚧 Partial · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Bulk-mutation transactions — world-load, content generation, large batch imports touching thousands to hundreds
of thousands of entities in one commit — pay two costs naive per-entity index maintenance doesn't have to: page
accessor churn (a fresh accessor per index per entity, repeatedly rented/returned) and random key order (B+Tree
and value-buffer operations land in whatever order the entities happen to iterate in, thrashing the index page
cache instead of streaming through it sequentially). A 200K-entity bulk create with four secondary indexes was
measured at ~196 ms in index maintenance alone under the original fully-naive scheme.

## ⚙️ How it works (in brief)

The fix is a two-phase commit-path rework, entirely internal — no new public API either way. **Phase A (shipped):**
`Transaction.Commit()` now hoists one long-lived `ChunkAccessor` per index per component type out of the per-entity
loop — instead of thousands of short-lived ones — and runs the whole component type's mutation loop with warm
accessor `CommitChanges` suppressed (`ChunkBasedSegment.EnterBatchMode`/`ExitBatchMode`), flushing dirty pages
only every 1,024 entities rather than after every single one. This is unconditional for every commit touching a
`Versioned` component with at least one indexed field — there is no size threshold or opt-in. **Phase B (not yet
implemented):** deferring secondary-index writes until after the per-entity commit loop, sorting them by key per
index, and applying them in sorted order — turning random index-page and value-buffer access into sequential,
cache-friendly access. Phase B is where most of the modeled savings live; Phase A alone does not reorder anything,
so index operations during a bulk commit still land in entity-iteration order today.

## 💻 Usage

```csharp
[Component]
public partial struct ItemData
{
    [Index(AllowMultiple = true)] public int ItemTypeId;
    [Index(AllowMultiple = true)] public int Rarity;
    [Index(AllowMultiple = true)] public long OwnerId;
}

using var tx = dbe.CreateQuickTransaction();
for (int i = 0; i < 200_000; i++)
{
    tx.Spawn<Item>(Item.Data.Set(GenerateItem(i)));
}
tx.Commit();   // accessor-reuse batching (Phase A) already applies transparently; sorted application
               // (Phase B) does not exist yet — index writes still happen in entity-iteration order
```

## ⚠️ Guarantees & limits

- **Phase A is shipped and unconditional:** per-component-type accessor hoisting plus suppressed/periodic
  `CommitChanges` (every 1,024 entities) is live for every commit of a `Versioned` component with indexed fields —
  see `Transaction._batchIndexActive`/`_batchIndexAccessors` and the batched overloads of `IndexMaintainer.UpdateIndices`
  / `RemoveSecondaryIndices` that accept pre-created accessors. No size threshold gates it; even a single-entity
  commit goes through the hoisted-accessor path.
- **Phase B (sort-by-key, deferred apply) is not implemented.** No `IndexOpBuffer`, `DeferredIndexOp`, or sorted
  apply pass exist in the codebase — index operations within a commit are still applied in entity-iteration order,
  not key order. This is the larger share of the design's modeled savings (sorted VSBS/page-cache locality was
  attributed ~72% of the total gain in the design analysis).
- Fully transparent either way: no change to `Transaction.Commit()`'s signature or to any indexing API — existing
  call sites benefit automatically, with no opt-in, now or once Phase B lands.
- Modeled savings for Phase A alone on the 200K-entity, 4-secondary-index case study: ~1.4× faster index
  maintenance (~196 ms → ~136 ms). Modeled savings for Phase A+B together: ~2.9× faster commit overall, ~4× faster
  on index maintenance specifically — that combined figure is design-time only until Phase B ships.
- Scoped to commit-path restructuring only — no change to B+Tree node layout, the OLC protocol, or split/merge
  behavior; index operations are not parallelized across threads.
- The primary-key index is and remains unaffected by either phase — entities are already iterated in PK order
  during commit, so it keeps its existing inline, sequential update path.
- Phase B's design analysis covers MVCC visibility (the brief window between revision commit and deferred index
  apply is not observable under snapshot isolation), unique-constraint semantics under reordering, WAL impact
  (none — WAL serializes component data, not index state), and view-notification ordering (safe — same-TSN delta
  entries are order-independent); this analysis is design-time, pending Phase B's implementation and test
  verification.

## 🔗 Related

- Source (Phase A, shipped): `src/Typhon.Engine/Transactions/public/Transaction.cs` (`_batchIndexActive`), `src/Typhon.Engine/Transactions/internals/IndexMaintainer.cs`, `src/Typhon.Engine/Storage/internals/ChunkBasedSegment.cs` (`EnterBatchMode`/`ExitBatchMode`)
- Related feature: [Secondary Index Storage Modes](./secondary-index-storage-modes/README.md) — the indexes this optimization maintains

<!-- Deep dive: claude/design/Indexing/batched-index-maintenance.md — full design, ARPG ItemData case study, Phase A/B breakdown -->
