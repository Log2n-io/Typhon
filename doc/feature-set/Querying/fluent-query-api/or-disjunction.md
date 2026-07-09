---
uid: feature-querying-fluent-query-api-or-disjunction
title: 'OR Disjunction (DNF Predicates)'
description: '|| in a WhereField predicate, normalized to Disjunctive Normal Form and evaluated as independent branches.'
---

# OR Disjunction (DNF Predicates)
> `||` in a `WhereField` predicate, normalized to Disjunctive Normal Form and evaluated as independent branches.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](../README.md)

**Assumes:** [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md)

## 🎯 What it solves

Real filters are rarely a single AND chain — "high level OR in this faction", "(active AND high-level) OR admin". Hand-rolling that as separate queries unioned in application code works for one-shot reads, but breaks down for a reactive `ToView()`: each branch's membership has to be tracked independently, because a field going IN on one branch can keep an entity in the view even while another branch's field goes OUT. OR disjunction support means you write the natural boolean C# expression and the engine handles branch bookkeeping for you, for both one-shot queries and persistent views.

## ⚙️ How it works (in brief)

`WhereField<T>(p => p.Level > 50 || p.Faction == 1)` is parsed into Disjunctive Normal Form (DNF) — an OR of AND-clauses — at query-build time. `!` is pushed to the leaves via De Morgan's law before normalization (`!(A && B)` → `!A || !B`), and `AND(OR(A,B), C)` distributes into `OR(AND(A,C), AND(B,C))`. The result is `FieldPredicate[][]`: each outer entry is one branch, each inner array is that branch's AND-clause. One-shot terminals (`Execute`/`Count`/`Any`) run each branch as an independent index-driven plan and union the results into one `HashSet<EntityId>` — deduplication is free. `ToView()` instead enters **OR mode**: `EcsView<TArchetype>` tracks, per entity, a 16-bit bitmap of which branches currently hold; the entity is in the view whenever any bit is set, so on a per-field change only the branches referencing that field are re-evaluated.

## 💻 Usage

```csharp
// One-shot: OR across two field comparisons — union of both branches, deduplicated
var result = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level > 50 || p.Faction == 1)
    .Execute();                                  // → HashSet<EntityId>

// Mixed AND/OR — DNF: [[Level>50, Faction==1], [Rarity>=4]]
using var view = tx.Query<PlayerArch>()
    .WhereField<Player>(p => (p.Level > 50 && p.Faction == 1) || p.Rarity >= 4)
    .ToView();                                   // → EcsView<PlayerArch> in OR mode (2 branches)

// NOT — De Morgan applied at parse time, zero runtime cost
var negated = tx.Query<PlayerArch>()
    .WhereField<Player>(p => !(p.Level > 50 && p.Faction == 1))   // → Level <= 50 || Faction != 1
    .Execute();

// Chained WhereField calls cross-product as AND-of-OR
var crossed = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.A > 1 || p.B > 2)   // 2 branches
    .WhereField<Player>(p => p.C > 3 || p.D > 4)   // 2 branches
    .Execute();                                    // → cross-product: 4 DNF branches
```

## ⚠️ Guarantees & limits

- **Maximum 16 DNF branches.** Each entity's branch membership is tracked in a `ushort` bitmap (one bit per branch). Exceeding 16 throws `InvalidOperationException` naming the actual branch count.
- **Exponential blowup risk:** ANDing multiple OR-pairs multiplies branch count — `(A||B) && (C||D) && (E||F)` is already 2×2×2 = 8 branches; chaining `WhereField` calls that each contain OR cross-products the same way. Restructure as separate queries (unioned in application code) or reduce ANDed OR-pairs if you hit the cap.
- `ExecuteOrdered()` is **not supported** with OR predicates — merging multiple independently-ordered index scans isn't implemented; sort the unordered `Execute()` result yourself if needed.
- In OR-mode views, a component read only happens when a changed field crosses a branch's predicate boundary (matching → not-matching or vice versa) — not on every change to a tracked field.
- Memory cost for an OR-mode view: ~2 bytes per tracked entity for the branch bitmap, on top of the normal entity-set cost.
- Cross-component OR (branches spanning two different component types within one archetype) is not supported — all branches must resolve against the same `WhereField<T>` component.

## 🧪 Tests

- [ExpressionParserTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/ExpressionParserTests.cs) — DNF normalization: De Morgan's law, double/triple negation, branch flattening, the 16-branch cap
- [EcsOrViewTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsOrViewTests.cs) — OR-mode `ToView()`: union-of-branches population, per-branch field-crossing in/out, last-branch-fails removal

## 🔗 Related

- Parent feature: [Fluent Query API & Predicate Parsing](./README.md)
- Sibling: [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md) — OR disjunction is the `||` extension of a `WhereField` predicate
- Sibling: [Foreign-Key Navigation Joins (L4)](./fk-navigation-joins.md) — OR predicates inside a navigation `.Where(...)` aren't supported

<!-- Deep dive: claude/design/Querying/ViewSystem/02-predicates.md §L3, DNF Normalization Algorithm -->
<!-- Deep dive: claude/overview/05-query.md §5.9 OR Disjunction -->
