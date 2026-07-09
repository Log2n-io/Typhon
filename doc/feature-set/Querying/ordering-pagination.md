---
uid: feature-querying-ordering-pagination
title: 'Result Ordering & Pagination'
description: 'Sorted, paged query results driven directly off a B+Tree index scan — no full-scan-then-sort.'
---

# Result Ordering & Pagination
> Sorted, paged query results driven directly off a B+Tree index scan — no full-scan-then-sort.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Querying](./README.md)

## 🎯 What it solves

Leaderboards, inventory lists, and any paginated UI need results in a specific order, a small slice of which is shown at a time. Scanning every match, sorting it in memory, then slicing off a page wastes I/O and CPU on rows the caller throws away — and gets worse as the table grows. Result Ordering & Pagination instead walks the indexed field in sorted order and stops once the requested page is filled, so a "page 3 of 50,000 leaderboard entries" query touches roughly what it returns, not the whole table.

## ⚙️ How it works (in brief)

`OrderByField`/`OrderByFieldDescending` designate an indexed field on the component identified by a preceding `WhereField` call; when it's the same field the predicate narrowed, the planner reuses that already-narrowed index range as the iteration order directly — no separate sort step. `Skip`/`Take` bound how much of that ordered stream is materialized. `ExecuteOrdered()` is the only terminal that honors this ordering, returning a `List<EntityId>`; `Execute()`, `Count()`, and `Any()` ignore `OrderByField`/`Skip`/`Take` entirely. Queries spanning a polymorphic archetype subtree still come back as one globally ordered list — matching archetypes are merged transparently.

## 💻 Usage

```csharp
// Page 3 of a Level-descending leaderboard (entries 21-30)
var page = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level >= 50)
    .OrderByFieldDescending<Player, int>(p => p.Level)
    .Skip(20)
    .Take(10)
    .ExecuteOrdered();                       // → List<EntityId>, Level descending

foreach (var id in page)
{
    var entity = tx.Open(id);
    if (entity.TryRead<Player>(out var player))
    {
        Render(player);
    }
}

// Unconditional sort: WhereField still required to identify the component table —
// an always-true comparison on the order field works fine.
var allByLevel = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level >= int.MinValue)
    .OrderByField<Player, int>(p => p.Level)
    .ExecuteOrdered();
```

| Method | Requires | Effect |
|---|---|---|
| `OrderByField<T,TKey>` | preceding `WhereField<T>` | Ascending iteration order on an indexed field of `T` |
| `OrderByFieldDescending<T,TKey>` | preceding `WhereField<T>` | Descending iteration order |
| `Skip(n)` | preceding `OrderByField`/`OrderByFieldDescending` | Drops the first `n` ordered results |
| `Take(n)` | preceding `OrderByField`/`OrderByFieldDescending` | Caps the result count at `n` |
| `ExecuteOrdered()` | `OrderByField` + `WhereField` | Materializes the ordered, paged `List<EntityId>` |

## ⚠️ Guarantees & limits

- **`WhereField` is mandatory** — it identifies the component table `OrderByField` resolves against; calling `OrderByField`, `Skip`, or `Take` without it throws `InvalidOperationException`.
- **Order field must be indexed** — `OrderByField`/`OrderByFieldDescending` reject non-indexed fields at call time, not at execution.
- **`Skip`/`Take` require an order first** — paging an unordered result set would have no defined meaning, so both throw `InvalidOperationException` if `OrderByField` hasn't been set.
- **Not supported on `ToView()`** — Views are unordered live sets; chaining `OrderByField`/`Skip`/`Take` before `.ToView()` throws. Re-run `ExecuteOrdered()` per refresh cycle for "paged + live" use cases.
- **Not supported with OR predicates** — a `WhereField` with `||` produces multiple independently-planned branches; merging them into one global order isn't implemented. Restructure as a single AND predicate, or run one ordered query per branch.
- **Not supported with spatial predicates or `.Where(lambda)`** — `WhereNearby`/`WhereInAABB`/`WhereRay` and the opaque post-filter are both unordered scans; sort the result client-side instead.
- **`Count()`/`Any()` ignore `Skip`/`Take`** — they report the full match count / existence regardless of paging; use `ExecuteOrdered().Count` for a paged count.
- **No chained `.Where(lambda)`** — fold any extra condition into the `WhereField` expression; `ExecuteOrdered()` throws if an opaque filter is attached.

## 🧪 Tests

- [EcsQueryTargetedScanTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsQueryTargetedScanTests.cs) — ascending/descending `ExecuteOrdered`, `Skip`/`Take` windowing, and the `OrderBy`/`Skip`-without-prerequisite guard throws
- [SimdKwayMergeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/SimdKwayMergeTests.cs) — k-way merge across a polymorphic archetype subtree stays globally ordered under `Skip`/`Take`

## 🔗 Related

- Sibling: [Indexed Field Predicates (WhereField)](fluent-query-api/wherefield-indexed-predicate.md) — `OrderByField` requires a preceding `WhereField` to identify the component table
- Sibling: [Execution Planning & Pipeline Execution](execution-planning-pipeline.md) — `OrderByField` pins the primary scan, overriding pure selectivity-driven choice

<!-- Deep dive: claude/overview/05-query.md §5.1 EcsQuery API (OrderBy/Skip/Take in the fluent API) -->
<!-- Deep dive: claude/overview/05-query.md §5.4 Execution Planning (OrderBy constraint on index selection) -->
<!-- Deep dive: claude/overview/05-query.md §5.12 ECS Query API (full query operations table) -->
