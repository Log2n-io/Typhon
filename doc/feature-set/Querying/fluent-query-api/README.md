# Fluent Query API & Predicate Parsing
> Archetype-rooted fluent query builder that parses C# lambdas into index-driven plans — structural constraints, indexed/opaque predicates, OR disjunction, and FK navigation joins, all off one entry point.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Querying](../README.md)

## 🎯 What it solves

Application code needs a single, type-safe entry point for finding entities — "has Inventory but not Frozen", "Level ≥ 50", "players in guilds with Level ≥ 10" — without ever hand-rolling a B+Tree scan, an expression-tree walker, or a ring-buffer registration. `tx.Query<TArchetype>()` is that entry point: archetype/component constraints, enabled-bit checks, field predicates (indexed or opaque), OR disjunction, FK joins, ordering, pagination, and reactive materialization all compose off the same fluent builder, rooted at the transaction that will see the results.

## ⚙️ How it works (in brief)

`tx.Query<TArchetype>()` returns a mutable `ref`-friendly struct that borrows the `Transaction` and resolves the archetype's polymorphic subtree (the archetype plus all descendants, by default — use `tx.QueryExact<TArchetype>()` to skip descendants) into a bitmask at construction. Fluent calls narrow that mask (`With<T>`/`Without<T>`/`Exclude<TArchetype>`) or add per-entity constraints (`Enabled<T>`/`Disabled<T>`, field predicates, spatial predicates). `WhereField<T>(Expression<Func<T,bool>>)` walks the lambda's *expression tree* and decomposes it into `FieldPredicate` structs — flattening `&&` chains and, when `||` is present, normalizing to Disjunctive Normal Form (DNF). A one-shot terminal (`Execute`/`ExecuteOrdered`/`Count`/`Any`) consumes the parsed predicate immediately; `ToView()` instead compiles it into a `FieldEvaluator[]` array and wires up a persistent, incrementally-refreshed `EcsView<TArchetype>`.

## Sub-features

| Sub-feature | Use it when... |
|---|---|
| [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md) | The field has `[Index]` and you want the fastest scan path, ordering/pagination, or a reactive `ToView()` |
| [Opaque Post-Filter Predicates (Where)](./where-opaque-postfilter.md) | The condition needs arbitrary C# logic, multiple fields at once, or a non-indexed field |
| [OR Disjunction (DNF Predicates)](./or-disjunction.md) | Your predicate needs `\|\|` — across an indexed field comparison or a mixed AND/OR group |
| [Foreign-Key Navigation Joins (L4)](./fk-navigation-joins.md) | You need to filter or join across an entity-reference field (e.g. `Player.GuildId → Guild`) |

## ⚠️ Guarantees & limits

- Queries are **polymorphic by default** — `tx.Query<TArchetype>()` matches the archetype and every descendant; `tx.QueryExact<TArchetype>()` is exact-archetype-only.
- `With<T>`/`Without<T>`/`Exclude<TArchetype>` filter at the **archetype mask level**, not per-entity component presence; `Enabled<T>`/`Disabled<T>` test per-entity enabled bitmaps (at most 4 of each, backed by a `ushort` bitmap — exceeding it throws `InvalidOperationException`).
- At most one spatial predicate (`WhereNearby`/`WhereInAABB`/`WhereRay`) per query; composes with `WhereField`/`Where` but not with a second spatial call.
- `Execute()` returns an unordered `HashSet<EntityId>`; `ExecuteOrdered()` requires `OrderByField`/`OrderByFieldDescending` on an indexed field (itself requiring `WhereField`) and returns a `List<EntityId>`. `Count()`/`Any()` ignore `Skip`/`Take` and throw if either is set.
- `WhereField` predicates must reference `[Index]`-carrying fields; unsupported syntax (method calls, arithmetic, field-to-field comparisons) throws `NotSupportedException` immediately inside `WhereField`, while a resolvable-but-unindexed field throws `InvalidOperationException` at the first terminal call.
- Supported operators: `==`, `!=`, `>`, `<`, `>=`, `<=`, combined with `&&`, `||`, `!` (De Morgan's law applied at parse time). String-typed fields (`String64`) aren't supported in `WhereField`/`OrderByField` — use `.Where(lambda)` instead.
- `ToView()` rejects `OrderBy`/`Skip`/`Take` (a view is unordered) and rejects mixing `WhereField` with a spatial predicate or a chained `.Where(...)`.
- `NavigateField<TSource, TTarget>(fkSelector)` starts a separate FK-join query path (`EcsNavigationQueryBuilder`) — its predicates compose with the source archetype mask but not with the OR/spatial forms above.
- Parsing is a one-time cold-path cost (~100µs) per query/view construction; the compiled `FieldEvaluator[]` is what runs on the hot path.

## 🧪 Tests

- [EcsQueryTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EcsQueryTests.cs) — archetype-mask With/Without/Exclude, Enabled/Disabled bitmaps, Count/Any, foreach, and unsupported-clause-combo guards

## 🔗 Related

- Sibling: [Query System (EcsQuery)](../../Ecs/query-system.md) — the Ecs-category entry point this whole category is the deep-dive extension of
- Source: [src/Typhon.Engine/Ecs/public/EcsQuery.cs](../../../../src/Typhon.Engine/Ecs/public/EcsQuery.cs)
- Sub-features: [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md), [Opaque Post-Filter Predicates (Where)](./where-opaque-postfilter.md), [OR Disjunction (DNF Predicates)](./or-disjunction.md), [Foreign-Key Navigation Joins (L4)](./fk-navigation-joins.md)

<!-- Deep dive: claude/overview/05-query.md §5.1, §5.2, §5.9, §5.10, §5.12 -->
<!-- Deep dive: claude/design/Querying/QueryEngine.md -->
<!-- Deep dive: claude/design/Querying/ViewSystem/02-predicates.md -->
<!-- Deep dive: claude/design/Querying/ViewSystem/10-predicate-taxonomy.md -->
