---
uid: feature-ecs-query-system
title: 'Query System (EcsQuery)'
description: 'Three-tier constraint evaluation with planner-chosen broad or targeted scan, indexed predicates, FK joins, and spatial filters.'
---

# Query System (EcsQuery)
> Three-tier constraint evaluation with planner-chosen broad or targeted scan, indexed predicates, FK joins, and spatial filters.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Ecs](./README.md)

## 🎯 What it solves

Application code needs to ask "which entities match these conditions" without hand-rolling a scan, choosing an index, or tracking which fields are searchable. `EcsQuery` gives a single fluent builder that covers structural filters (archetype/component presence), lightweight per-entity state (enabled/disabled), and value predicates (field comparisons, FK joins, spatial overlap) — and picks the cheapest execution strategy for you. Without it, every query site would need its own decision about scanning everything versus walking a secondary index.

## ⚙️ How it works (in brief)

Constraints are evaluated in three tiers, cheapest first: Tier 1 collapses all archetype/component constraints into one `ArchetypeMask` (one AND + branch per entity); Tier 2 resolves `Enabled`/`Disabled` constraints into a per-archetype bitmask checked against the entity's `EnabledBits`; Tier 3 applies WHERE predicates. `Where<T>(Func<T,bool>)` is an opaque lambda evaluated per-entity during a broad (archetype-first, linear) scan — no index, no incremental view. `WhereField<T>(Expression<Func<T,bool>>)` is parsed into indexed `FieldEvaluator`s, enabling a targeted (index-first) scan when selective, and is the only predicate form that can back a `ToView()` incremental view. The planner compares estimated index cardinality against archetype cardinality to choose broad vs. targeted; `OrderByField` forces index-first.

## 💻 Usage

```csharp
// Field predicate — planner picks targeted (index-first) scan when selective
HashSet<EntityId> rare = tx.Query<Factory>()
    .WhereField<FactoryData>(f => f.Throughput > 50)
    .Execute();

// Structural + enabled-bit constraints — broad scan, no index needed
int armed = tx.Query<Building>()
    .With<Placement>()
    .Without<FactoryData>()
    .Enabled<Placement>()
    .Count();

// OR predicate (DNF, max 16 branches) + ordering/pagination (requires WhereField)
List<EntityId> topFactories = tx.Query<Factory>()
    .WhereField<FactoryData>(f => f.Throughput > 50 || f.Type == FactoryType.Steel)
    .OrderByFieldDescending<FactoryData, float>(f => f.Throughput)
    .Skip(20).Take(10)
    .ExecuteOrdered();

// FK navigation join — "Houses whose Owner is over 30"
HashSet<EntityId> houses = tx.Query<House>()
    .NavigateField<HouseData, OwnerData>(h => h.OwnerId)
    .Where((h, o) => o.Age > 30)
    .Execute();

// Spatial predicate on a [SpatialIndex] field
HashSet<EntityId> nearby = tx.Query<Building>()
    .WhereNearby<Placement>(centerX: 0, centerY: 0, centerZ: 0, radius: 100)
    .Execute();

// Persistent reactive view — incremental refresh via ring buffer (WhereField only)
using EcsView<Factory> view = tx.Query<Factory>()
    .WhereField<FactoryData>(f => f.Throughput > 50)
    .ToView();
```

| Constraint | Tier | Example |
|---|---|---|
| `Query<T>()` / `QueryExact<T>()` | T1 | Polymorphic vs. exact-archetype root |
| `.With<T>()` / `.Without<T>()` / `.Exclude<T>()` | T1 | Archetype mask AND / AND NOT / subtree removal |
| `.Enabled<T>()` / `.Disabled<T>()` | T2 | Max 4 each per query |
| `.Where<T>(Func)` | T3 | Opaque, broad scan only |
| `.WhereField<T>(Expr)` | T3 | Indexed, enables targeted scan + views |

## ⚠️ Guarantees & limits

- Constraints are evaluated cheapest-tier-first; a contradictory Tier 1 mask (e.g. `.With<A>().Without<A>()`) short-circuits to an empty result with zero iteration.
- `.Enabled<T>()`/`.Disabled<T>()`: max 4 type IDs each per query — exceeding throws `InvalidOperationException`.
- `WhereField` requires the field to carry a B+Tree index; non-indexed fields are rejected at plan-build time. `Where<T>(Func)` has no such requirement but never uses an index and never drives an incremental view.
- OR predicates inside `WhereField` are normalized to DNF, capped at 16 branches (ANDing OR pairs multiplies branch count — five 2-way ORs already exceeds the cap).
- `OrderByField`/`OrderByFieldDescending`/`Skip`/`Take` require a prior `WhereField` call (it identifies the component table) and an indexed field; `ExecuteOrdered()` does not support OR predicates.
- `ToView()` rejects `OrderBy`/`Skip`/`Take` (a view is unordered) and rejects combining `WhereField` with a spatial predicate or a chained `.Where(lambda)`.
- At most one spatial predicate (`WhereNearby`/`WhereInAABB`/`WhereRay`) per query; the target component must declare `[SpatialIndex]`.
- `EcsQuery<TArchetype>` is a mutable struct that borrows the `Transaction` — it does not own it and must not outlive it.
- Polymorphic queries (`Query<T>()`) match the archetype's full descendant subtree; use `QueryExact<T>()` to match only the named archetype.

## 🧪 Tests

- [EcsQueryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsQueryTests.cs) — Tier1/Tier2/Tier3 broad scan (`With`/`Without`/`Exclude`/`Enabled`/`Disabled`/opaque `Where`), contradiction short-circuit, `Count`/`Any`
- [EcsQueryTargetedScanTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsQueryTargetedScanTests.cs) — `WhereField` targeted (index-first) scan vs. broad-scan equivalence, `OrderByField`/`Skip`/`Take`, guard-throws for missing `WhereField`/`OrderBy`
- [EcsNavigationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsNavigationTests.cs) — `NavigateField` FK join combining source and target predicates, `Count`, incremental view over a join

## 🔗 Related

- Full query/view feature catalog: [Querying category](../Querying/README.md) — ordering & pagination, spatial predicates,
  statistics infrastructure, persistent views, view pooling, and temporal queries all build on the fluent API
  introduced here.

<!-- Deep dive: claude/design/Ecs/05-query-system.md, claude/overview/05-query.md -->
