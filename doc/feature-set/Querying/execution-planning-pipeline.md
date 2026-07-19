---
uid: feature-querying-execution-planning-pipeline
title: 'Execution Planning & Pipeline Execution'
description: 'Picks the most selective index as the scan driver and streams results into your collection — no full-table scans, minimal allocation.'
---

# Execution Planning & Pipeline Execution
> Picks the most selective index as the scan driver and streams results into your collection — no full-table scans, minimal allocation.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](./README.md)

## 🎯 What it solves

A query with two or more indexed predicates can be answered by scanning any one of them and filtering the rest — but scanning the wrong one (or the first one written) means touching far more entities than the answer actually contains. Execution planning chooses which predicate to physically scan based on how selective it is, so a query like "Level >= 50 AND Gold > 10000" scans the rarer condition first and never reads the component data for entities the cheaper filter would have rejected anyway.

## ⚙️ How it works (in brief)

Every `Execute`, `ExecuteOrdered`, `Count`, `Any`, and `ToView()` call with an indexed `WhereField` predicate builds an execution plan: each predicate's cardinality is estimated from index statistics (most-common-values / histogram / approximate distinct-count, falling back to exact B+Tree counts or uniform distribution when no statistics have been built yet), predicates are ordered by ascending estimated cardinality, and the narrowest one that can bound a key range becomes the primary scan — its bounds become the index iteration range. The remaining predicates become an ordered filter chain, evaluated per candidate with short-circuit on first failure. Matching entity IDs stream straight into the caller-supplied `HashSet<EntityId>` / `List<EntityId>`, and `Skip`/`Take` stop the scan as soon as enough results are collected. `ToView()` builds the plan once and reuses it for every later incremental refresh and overflow-recovery rescan.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    [Index] public int Level;
    [Index] public int Gold;
}

[Archetype]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<Player> Data = Register<Player>();
}

using var tx = dbe.CreateQuickTransaction();

// Two indexed predicates — the planner picks whichever is more selective as the scan driver
var richVeterans = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level > 50 && p.Gold > 10000)
    .Execute();                                   // → HashSet<EntityId>

// Inspect the chosen plan on a view (diagnostic / tuning use)
using var view = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level > 50 && p.Gold > 10000)
    .ToView();

Console.WriteLine(view.ExecutionPlan);
// "Index scan Field[1] [10001..2147483647] → Filters: Field[0] GreaterThan 50 (est: 1200)"
```

## ⚠️ Guarantees & limits

- Planning runs fresh on every call for one-shot terminals (`Execute`/`ExecuteOrdered`/`Count`/`Any`); `ToView()` builds it once via `view.ExecutionPlan` and reuses the cached plan for every subsequent refresh and overflow rescan.
- Selectivity is an estimate (statistics-backed when available, exact-count/uniform fallback otherwise) — a bad estimate degrades plan quality, never correctness.
- Multiple predicates on the *same* indexed field (e.g., `Gold >= 100 && Gold < 5000`) intersect into one tighter scan range rather than being treated as separate filters.
- `!=` predicates can never become the primary scan — they cannot bound a range. Every `WhereField` predicate needs at least one `==`/`<`/`<=`/`>`/`>=` comparison the planner can narrow on; an all-`!=` predicate currently yields no results (zero for `Count()`) instead of falling back to a broad scan, so pair it with a narrowing condition.
- `OrderByField`/`OrderByFieldDescending` pins the primary scan to that field — overriding pure selectivity-driven choice — so ordered results come out pre-sorted with no separate sort pass; predicates on the ordered field still narrow the scan range.
- `Count()` uses a dedicated fused path when an indexed predicate is present: results are counted in place during the index scan, no `HashSet`/`List` is materialized.
- OR predicates (multi-branch `WhereField`) get one independently-built plan per DNF branch, unioned at execution; `view.ExecutionPlan` exposes only the first branch.
- Diagnostics are read-only — `ExecutionPlan`/`ToString()` describe the chosen plan; there is no public API to force a different index or override the planner's choice.

## 🧪 Tests

- [PlanBuilderAndExecutorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/PlanBuilderAndExecutorTests.cs) — selectivity-ascending predicate ordering, tie-breaking, `OrderBy`-pins-primary-scan, `ExecutionPlan.ToString()` diagnostics
- [PipelineExecutorCombinedPathTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/PipelineExecutorCombinedPathTests.cs) — fused `Count()` path, dual-bound range scans, and MVCC-snapshot-isolated plan execution on Versioned/SV storage
- [FieldEvaluatorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/FieldEvaluatorTests.cs) — the compiled `FieldEvaluator` struct's per-`CompareOp`/`KeyType` correctness that the hot-path filter chain runs

## 🔗 Related

- Related feature: [Indexed Field Predicates (WhereField)](./fluent-query-api/wherefield-indexed-predicate.md)
- Related feature: [Result Ordering & Pagination](./ordering-pagination.md)
- Related feature: [Persistent Views](./persistent-views.md)

<!-- Deep dive: claude/overview/05-query.md §5.3 Selectivity Estimator, §5.4 Execution Planning, §5.5 Pipeline Executor -->
<!-- Deep dive: claude/design/Querying/ViewSystem/04-query-planning.md — selectivity estimator chain, statistics maintenance, plan-building algorithm -->
<!-- ADR: claude/adr/040-btree-leaf-enumerator.md — range-enumerator API backing primary-stream iteration -->
