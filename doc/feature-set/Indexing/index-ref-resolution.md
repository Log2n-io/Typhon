---
uid: feature-indexing-index-ref-resolution
title: 'Index Handle Resolution (IndexRef)'
description: 'Resolve an index once on the cold path, then reuse the handle for free on every hot-path call.'
---

# Index Handle Resolution (IndexRef)
> Resolve an index once on the cold path, then reuse the handle for free on every hot-path call.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Hot-path code (a per-tick query, a lookup inside a system loop) needs to repeatedly target the same primary-key or
secondary index without paying repeated resolution cost — naming a `ComponentTable`, finding which field, confirming
it's actually indexed. Doing that lookup by name on every call costs dictionary/string work for no reason, since the
target never changes between calls. `IndexRef` separates the two: pay the resolution cost once, then carry a cheap,
fixed-size handle into the hot path indefinitely.

## ⚙️ How it works (in brief)

`DatabaseEngine.GetPKIndexRef<T>()` and `DatabaseEngine.GetIndexRef<T, TKey>(selector)` are the only ways to obtain an
`IndexRef`. Both validate eagerly — component registered, field exists, field carries `[Index]` — and throw immediately
on failure rather than deferring the error to first use. On success they return a small readonly struct identifying the
target table and field, plus a snapshot of the table's current index-layout version. Every API that accepts an
`IndexRef` checks that snapshot against the table's live version before use — an O(1) integer compare — so a handle
captured before a schema migration that reshuffles indexed fields is detected and rejected rather than silently
misreading another field.

## 💻 Usage

```csharp
// Cold path — resolve once, e.g. at startup or before entering a hot loop.
var nameIndex = dbe.GetIndexRef<Player, String64>(p => p.Name);
var idIndex   = dbe.GetPKIndexRef<Player>();

// Hot path — pass the handles in repeatedly; no re-resolution, no allocation.
using (var tx = dbe.CreateQuickTransaction())
{
    using var hits = tx.EnumerateIndex<Player, String64>(nameIndex, minKey: default, maxKey: String64.MaxValue);
    foreach (var hit in hits)
    {
        // hit.EntityPK, hit.Key, hit.Component
    }
}
```

## ⚠️ Guarantees & limits

- `GetPKIndexRef<T>()` / `GetIndexRef<T, TKey>(selector)` throw `InvalidOperationException` immediately if the
  component isn't registered, the field doesn't exist, or the field isn't `[Index]`-annotated — failures surface at
  resolution time, not on first hot-path use.
- Resolution validates against the table's current schema; any later staleness check is a single integer compare
  (`CapturedLayoutVersion` vs. the table's live layout version) — no dictionary lookup, no page-cache access.
- A handle becomes stale the moment its owning table's index layout changes (e.g. an index added/removed/reordered by
  schema evolution). The next use throws rather than reading the wrong field; the only recovery is to call
  `GetIndexRef`/`GetPKIndexRef` again.
- `IndexRef` is a 16-byte `readonly struct` — copy it into fields, locals, or closures freely; there is nothing to
  dispose.
- `GetPKIndexRef<T>()` resolves and reports `IsPrimaryKey == true`, but primary-key lookups go through ECS entity
  APIs / `EntityMap`, not `Transaction.EnumerateIndex` — passing a PK `IndexRef` there throws. `IndexRef` is for
  secondary indexes in range/lookup APIs.

## 🧪 Tests

- [IndexRefTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/IndexRefTests.cs) — `GetPKIndexRef`/`GetIndexRef` success and failure paths (unregistered type, non-indexed field), plus the `Validate()` staleness check
- [BulkEnumerateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `StaleIndexRef_Throws` exercises the same staleness check on the hot-path `Transaction.EnumerateIndex` call

## 🔗 Related

- Sibling: [Lookup and Range-Scan Operations](./lookup-and-range-scan.md) — the hot-path API `IndexRef` handles are resolved for
- Sibling: [Indexed Field Predicates (WhereField)](../Querying/fluent-query-api/wherefield-indexed-predicate.md) — `WhereField` compiles down to an index lookup via `IndexRef`

<!-- Deep dive: claude/design/Indexing/public-api.md — public surface, IndexRef layout, staleness contract -->
<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes -->
