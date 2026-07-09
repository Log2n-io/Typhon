---
uid: feature-indexing-lookup-and-range-scan
title: 'Lookup and Range-Scan Operations'
description: 'Lock-free point lookups and ordered range scans over any secondary index, MVCC-correct at your transaction''s snapshot.'
---

# Lookup and Range-Scan Operations
> Lock-free point lookups and ordered range scans over any secondary index, MVCC-correct at your transaction's snapshot.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Application code routinely needs "find the entity with key X" or "give me all entities with key between A and B, in order" — without scanning the whole table and without index reads blocking concurrent writers. A naive index read taking a lock for the duration of a scan would serialize readers against writers and tank throughput under the high-core-count workloads Typhon targets. Lookup and range-scan operations give you both point and ranged access to a secondary index's sorted key space, reading concurrently with in-flight writes and returning only the component versions visible to your transaction's snapshot.

## ⚙️ How it works (in brief)

`Transaction.EnumerateIndex<T, TKey>` takes an `IndexRef` plus a `[minKey, maxKey]` bound and streams matching entities in ascending key order; pass `minKey == maxKey` for a point lookup. Internally it walks the index's leaf chain using per-leaf optimistic version validation — no lock is held while reading, and only a leaf that was concurrently modified during the read is re-read. For `AllowMultiple` (non-unique) indexes, each key's value set is expanded transparently; you iterate `(EntityPK, Key, Component)` triples regardless of uniqueness. Every candidate is checked against the entity's revision chain at the transaction's snapshot TSN, so deleted or not-yet-committed versions are filtered out before you ever see them. The same range-scan machinery also backs the fluent `Query<T>()` pipeline when a predicate or ordering can be served by a secondary index.

## 💻 Usage

```csharp
var nameIndex = engine.GetIndexRef<Player, String64>(p => p.Name);

using var tx = engine.CreateQuickTransaction();

// Range scan — all players with Name in ["Anna", "Marco"], ascending
using (var range = tx.EnumerateIndex<Player, String64>(nameIndex, "Anna", "Marco"))
{
    foreach (var hit in range)
    {
        // hit.EntityPK, hit.Key, hit.Component (or hit.CurrentComponent for zero-copy)
    }
}

// Point lookup — minKey == maxKey
using var point = tx.EnumerateIndex<Player, String64>(nameIndex, "Marco", "Marco");
if (point.MoveNext())
{
    var found = point.CurrentComponent;
}
```

## ⚠️ Guarantees & limits

- Reads are lock-free (optimistic lock coupling): a concurrent writer never blocks a scan, and a scan never blocks a writer. A version conflict triggers a re-read of only the affected leaf, not a tree-wide restart.
- Results are MVCC-correct: only revisions committed and visible at the transaction's snapshot TSN are yielded; uncommitted writes from other transactions and tombstoned entities are skipped transparently.
- `EnumerateIndex` only accepts secondary `IndexRef`s — passing a primary-key `IndexRef` throws `InvalidOperationException` (use ECS entity APIs for PK lookups).
- Iteration order from `EnumerateIndex` is ascending by key; the fluent `Query<T>()` pipeline can request descending order when an `OrderBy` plan selects the same index.
- The returned enumerator is a `ref struct` tied to the issuing `Transaction` and its epoch scope — it cannot outlive the transaction or escape across threads/async boundaries.
- `CurrentComponent` gives a zero-copy `ref readonly` into page memory valid only until the next `MoveNext()`; use `Current` (which copies) if you need the value to outlive that step.
- Long-running scans periodically refresh the engine epoch internally so a single large range read doesn't pin reclamation indefinitely.

## 🧪 Tests

- [BulkEnumerateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `SecondaryIndex_UniqueField`/`SecondaryIndex_AllowMultiple`/`StaleIndexRef_Throws`/`ZeroCopy_ReadReturnsRefIntoPageMemory`: `Transaction.EnumerateIndex` ascending order, zero-copy reads, MVCC visibility (`MVCC_Visibility`, `DeletedEntity_Skipped`)
- [BtreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BTreeTests.cs) — `RangeScan_TightBounds_OnlyQualifyingEntries`/`RangeScan_MultiLeaf_CorrectOrdering`/`RangeScanDescending_ReturnsReverseOrder`/`RangeScan_AfterDeletions_CorrectEntries`: the leaf-chain range-scan machinery underneath `EnumerateIndex`

## 🔗 Related

- Sibling: [Optimistic Lock Coupling (per-node concurrency)](./olc-concurrency.md) — the lock-free concurrency protocol this operation relies on
- Sibling: [Index Handle Resolution (IndexRef)](./index-ref-resolution.md) — the handle passed into `EnumerateIndex`
- Sibling: [Indexed Field Predicates (WhereField)](../Querying/fluent-query-api/wherefield-indexed-predicate.md) — compiles down to this same range-scan machinery

<!-- Deep dive: claude/design/Indexing/public-api.md — operation surface, OLC concurrency model, B-link descent -->
<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes — node layout, concurrency model -->
<!-- Deep dive: claude/overview/05-query.md §5.5 Pipeline Executor — how range scans feed the fluent Query<T>() pipeline -->
