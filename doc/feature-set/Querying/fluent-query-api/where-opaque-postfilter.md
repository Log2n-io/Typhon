---
uid: feature-querying-fluent-query-api-where-opaque-postfilter
title: 'Opaque Post-Filter Predicates (Where)'
description: 'Arbitrary per-entity C# predicate evaluated after a broad archetype scan — for logic the index system can''t express.'
---

# Opaque Post-Filter Predicates (Where)
> Arbitrary per-entity C# predicate evaluated after a broad archetype scan — for logic the index system can't express.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Querying](../README.md)

## 🎯 What it solves

Some filtering logic isn't a single indexed-field comparison: it combines several fields with custom logic, calls into application code, or targets a field that has no index at all. `Where<T>(Func<T, bool> predicate)` accepts any compiled C# delegate and applies it as a per-entity post-filter, trading the index-driven speed of `WhereField` for unrestricted expressiveness — useful for one-shot queries where simplicity matters more than scan cost, or whenever the condition genuinely can't be reduced to an indexed comparison.

## ⚙️ How it works (in brief)

`Where<T>` takes an ordinary `Func<T, bool>` — a compiled delegate, not a parsed expression tree — and chains it into a per-entity filter; multiple `Where` calls AND together. Execution always broad-scans the archetype mask's candidate entities, opens each one (`Transaction.Open` + `TryRead<T>`), and evaluates the delegate — there is no index lookup and no key-only short-circuit. Because the predicate is opaque, the engine cannot determine which fields it depends on, so it cannot wire commit-time change capture for it: an `EcsView` built from `Where` alone falls back to a full re-query (pull mode) on every `Refresh()` instead of an incremental ring-buffer drain.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    public int Level;
    public int Faction;
}

[Archetype(10)]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<Player> Data = Register<Player>();
}

using var tx = dbe.CreateQuickTransaction();

// Arbitrary per-entity logic — no index involved, evaluated via Transaction.Open + TryRead
var matched = tx.Query<PlayerArch>()
    .Where<Player>(p => SomeOpaqueRule(p.Level, p.Faction))
    .Execute();                                   // → HashSet<EntityId>

// Pull-mode view: re-evaluates the full archetype scan on every Refresh()
using var view = tx.Query<PlayerArch>()
    .Where<Player>(p => p.Level > 50)
    .ToView();                                    // → EcsView<PlayerArch> (pull mode)
```

## ⚠️ Guarantees & limits

- No index requirement — works on any field, indexed or not, and accepts any computation expressible as `Func<T, bool>`.
- Cannot be combined with `WhereField` for `ToView()`: an incremental view's predicate must come entirely from `WhereField`. Mixing throws `InvalidOperationException` ("fold the condition into WhereField, or drop WhereField to build a pull view from `.Where(...)`").
- Cost is O(archetype candidate count) per `Execute()` and per `Refresh()` — every mask-matching entity is opened and read, regardless of how selective the predicate turns out to be.
- An `EcsView` built from `Where` alone is always **pull mode**: full re-query on each `Refresh()`, no ring-buffer delta tracking, no boundary-crossing short-circuit.
- Multiple `.Where<T>()` calls chain as AND only — there is no OR composition across separate `.Where` calls (express OR inside the single lambda instead).
- `ExecuteOrdered()` requires `WhereField` and ignores `.Where(...)`; combining the two throws `InvalidOperationException`.

## 🧪 Tests

- [EcsQueryTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ECS/EcsQueryTests.cs) — `Where_*`: field-delegate filtering, empty/all-match edges, two-component combination, foreach, and pull-mode view refresh

## 🔗 Related

- Parent feature: [Fluent Query API & Predicate Parsing](./README.md)
- Sibling: [Indexed Field Predicates (WhereField)](./wherefield-indexed-predicate.md) — the index-driven counterpart; use it when the field is indexed and only fall back to opaque `Where` otherwise

<!-- Deep dive: claude/overview/05-query.md §5.1 ("Where vs WhereField") -->
