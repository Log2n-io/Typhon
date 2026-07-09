---
uid: feature-querying-fluent-query-api-fk-navigation-joins
title: 'Foreign-Key Navigation Joins (L4)'
description: 'Join across an entity-reference field — filter source entities by predicates on the target entity they point to.'
---

# Foreign-Key Navigation Joins (L4)
> Join across an entity-reference field — filter source entities by predicates on the target entity they point to.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](../README.md)

**Assumes:** [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md)

## 🎯 What it solves

ECS data is naturally hierarchical — a `Player` points at its `Guild` via a `GuildId` field, and "players in guilds with `Level >= 10`" is a routine query shape. Reading every player, dereferencing its guild, and checking the guild's field by hand works once; keeping that filtered set live as either side changes (a player switches guilds, a guild levels up) is the part application code shouldn't have to re-implement. Navigation joins give you that as one fluent call, with both one-shot and reactive forms, on top of the existing FK index instead of a generic join algorithm.

## ⚙️ How it works (in brief)

`tx.Query<TSourceArch>().NavigateField<TSource, TTarget>(fkSelector)` returns an `EcsNavigationQueryBuilder` bound to the FK field; `.Where((source, target) => ...)` takes a two-parameter lambda whose predicates are split by which parameter they reference — source-side and target-side filters can be mixed freely. The result set always contains **source** entity IDs (the side you queried from), never target IDs. `Execute()`/`Count()`/`Any()` choose source-first or target-first traversal based on selectivity; `ToView()` produces a `NavigationView` that registers with **both** the source and target component tables' change-capture registries, so it refreshes on either a source FK change (player switches guilds — 1:1 re-evaluation) or a target field change (guild levels up — fans out via the FK's reverse index to every referencing source).

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    [Index(AllowMultiple = true), ForeignKey(typeof(Guild))]
    public long GuildId;
    [Index(AllowMultiple = true)]
    public int Active;
}

[Component("Game.Guild", 1)]
public struct Guild
{
    [Index(AllowMultiple = true)]
    public int Level;
}

using var tx = dbe.CreateQuickTransaction();

// One-shot: source EntityIds whose guild satisfies the target predicate
var members = tx.Query<PlayerArch>()
    .NavigateField<Player, Guild>(p => p.GuildId)
    .Where((p, g) => p.Active == 1 && g.Level >= 10)
    .Execute();                                  // → HashSet<EntityId> (Player ids)

int count = tx.Query<PlayerArch>()
    .NavigateField<Player, Guild>(p => p.GuildId)
    .Where((p, g) => g.Level >= 10)
    .Count();

// Reactive: refreshes on either a GuildId change or a target Guild.Level change
using var view = tx.Query<PlayerArch>()
    .NavigateField<Player, Guild>(p => p.GuildId)
    .Where((p, g) => g.Level >= 10)
    .ToView();                                   // → ViewBase (NavigationView<Player, Guild>)

foreach (var playerId in view) { /* player is in a guild with Level >= 10 */ }
```

## ⚠️ Guarantees & limits

- The FK field must be `long`, decorated with `[ForeignKey(typeof(TTarget))]`, and indexed as `[Index(AllowMultiple = true)]` (the reverse lookup needs the multi-value index). Violating any of these throws `InvalidOperationException` naming the field and the missing requirement.
- `NavigateField<TSource, TTarget>` must be called before `.Where(...)` on the navigation builder — there is no separate ordering violation to guard against since `Where` only exists on the returned navigation builder.
- `ToView()` requires at least one **target**-side predicate — without it, target deletions wouldn't be detected and the view would go stale; source-only filters throw `InvalidOperationException`. `Execute()`/`Count()`/`Any()` have no such requirement.
- The view/result set holds **source** entity IDs only; to read the target, dereference the FK field yourself (`tx.Open(id).TryRead<Player>(out var p)` then `tx.Open(EntityId.FromRaw(p.GuildId))`).
- `ExecuteOrdered()` is not supported on navigation queries — join results have no natural ordering.
- Navigation combined with OR predicates (`||` inside the navigation `.Where(...)`) is not supported — L4 and L3 don't currently compose.
- Reverse-navigation fan-out (a target field change re-evaluating every referencing source) costs one FK-index lookup plus one read per referencing source entity — proportional to how many sources point at the changed target, not to the total table size.

## 🧪 Tests

- [EcsNavigationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsNavigationTests.cs) — source+target predicate combination, `Count()`, and `ToView()` incremental refresh on either side
- [StatisticsRebuildTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/StatisticsRebuildTests.cs) — `NavigationView_ToView_NoTargetPredicates_Throws` / `NavigationQuery_OneShot_NoTargetPredicates_Works`: the target-predicate requirement is `ToView()`-only

## 🔗 Related

- Parent feature: [Fluent Query API & Predicate Parsing](./README.md)
- Sibling: [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md) — the FK field's reverse-lookup index the join scans
- Sibling: [OR Disjunction (DNF Predicates)](./or-disjunction.md) — L4 navigation and L3 OR predicates don't currently compose

<!-- Deep dive: claude/design/Querying/ViewSystem/02-predicates.md §L4 Navigation Join -->
<!-- Deep dive: claude/design/Querying/ViewSystem/05-view-types.md §NavigationView -->
<!-- Deep dive: claude/overview/05-query.md §5.10 Navigation Views -->
