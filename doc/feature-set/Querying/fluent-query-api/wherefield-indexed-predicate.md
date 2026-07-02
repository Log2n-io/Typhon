# Indexed Field Predicates (WhereField)
> Expression-parsed predicate that drives a targeted B+Tree scan and powers incrementally-maintained reactive views.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Querying](../README.md)

## 🎯 What it solves

Filtering entities by reading every archetype member's component is wasteful when the field already has a secondary index — the matching set can usually be found directly from the index without touching most entities. `WhereField` lets you express a field comparison as ordinary C# (`p => p.Level >= 50`) while the engine compiles it into an index-driven scan plan. Just as important: because the predicate is understood structurally (not an opaque delegate), it can also drive a persistent `EcsView` that updates itself incrementally as data changes, instead of re-scanning everything on every refresh.

## ⚙️ How it works (in brief)

`WhereField<T>(Expression<Func<T, bool>> predicate)` parses the lambda's *expression tree* (not a compiled delegate) into one or more field predicates, normalized to Disjunctive Normal Form when it contains `||`. Unsupported syntax — method calls, arithmetic between fields, field-to-field comparisons — is rejected immediately as the lambda is parsed. Whether the referenced field actually carries `[Index]` is checked later, the moment a terminal operation (`Execute`/`Count`/`Any`/`ExecuteOrdered`/`ToView`) resolves the predicate against the component's schema — before any B+Tree access, never a silent broad scan. At execution, the planner consults index statistics (histogram / most-common-values / HyperLogLog) to pick the most selective predicate as the primary B+Tree scan stream and applies the rest as ordered filters. Multiple `WhereField` calls AND together (cross-producting any DNF branches). Because the predicate decomposes into key comparisons, an `EcsView` built from it can re-evaluate membership from the before/after key values delivered at commit — no component read needed on the common path.

## 💻 Usage

```csharp
[Component("Game.Player", 1)]
public struct Player
{
    [Index]
    public int Level;
    [Index(AllowMultiple = true)]
    public int Faction;
}

[Archetype(10)]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<Player> Data = Register<Player>();
}

using var tx = dbe.CreateQuickTransaction();

// One-shot: matching EntityIds, index-driven scan
var rarePlayers = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level >= 50)
    .Execute();                                  // → HashSet<EntityId>

// Zero-allocation aggregation — fused index streaming, no collection built
int count = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level >= 50)
    .Count();

// Ordering + pagination (OrderByField requires WhereField to identify the component table)
var topPlayers = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level >= 10)
    .OrderByFieldDescending<Player, int>(p => p.Level)
    .Skip(20).Take(10)
    .ExecuteOrdered();                            // → List<EntityId>

// Reactive: incremental view, refreshed via ring-buffer deltas, not a full re-scan
using var view = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Level > 50 || p.Faction == 1)
    .ToView();                                    // → EcsView<PlayerArch> (OR mode: 2 DNF branches)
```

## ⚠️ Guarantees & limits

- The predicate's field must carry `[Index]` (B+Tree secondary index) — a field that resolves but isn't indexed throws `InvalidOperationException` naming the field, raised at the first terminal call, not inside `WhereField` itself.
- Operators: `==`, `!=`, `>`, `<`, `>=`, `<=`, combined with `&&` / `||` / `!` (De Morgan applied at parse time); method calls, arithmetic on fields, and field-to-field comparisons throw `NotSupportedException` immediately inside `WhereField`.
- String-typed fields (`String64`) have no key-type mapping for predicates — referencing one throws `NotSupportedException` at the first terminal call; use `.Where(lambda)` for string-field filtering instead.
- OR expressions normalize to DNF capped at **16 branches**; exceeding it throws `InvalidOperationException` — restructure as multiple queries or fewer ANDed OR-pairs (each ANDed OR-pair multiplies the branch count).
- `ToView()` on a `WhereField` query is the only path that yields an incrementally-maintained `EcsView` (ring-buffer delta refresh, O(1) typical cost per change); `Where` (opaque) forces full-rescan pull mode instead.
- `ExecuteOrdered()` requires `OrderByField`/`OrderByFieldDescending` on an indexed field and does not support multi-branch (OR) predicates.
- Multiple `WhereField` calls on the same query cross-product as AND-of-OR — chaining ANDed OR-pairs grows DNF branches multiplicatively against the 16-branch cap.
- Entities spawned (but not yet committed) in the same transaction have no index entry yet; `Execute`/`Count`/`Any` still see them via a deferred-compiled fallback predicate so read-your-own-writes holds, even though the targeted scan itself can't find them.

## 🧪 Tests

- [EcsQueryTargetedScanTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EcsQueryTargetedScanTests.cs) — targeted B+Tree scan vs. broad-scan equivalence, `Count`/`Any`, `ExecuteOrdered` guard throws
- [TransientIndexTests](../../../../test/Typhon.Engine.Tests/Data/ECS/TransientIndexTests.cs) — `WhereField` index-driven scan on the Transient storage discipline, with a non-primary filter
- [EcsHardeningTests](../../../../test/Typhon.Engine.Tests/Data/ECS/EcsHardeningTests.cs) — `Query_WhereField_SeesPendingSpawns`: deferred-compiled fallback so uncommitted spawns are still visible

## 🔗 Related

- Parent feature: [Fluent Query API & Predicate Parsing](./README.md)
- Sibling: [Query System (EcsQuery)](../../Ecs/query-system.md) — the Ecs-category entry point this whole category is the deep-dive extension of
- Sibling: [Execution Planning & Pipeline Execution](../execution-planning-pipeline.md) — consumes `WhereField` predicates to pick the primary scan
- Sibling: [Result Ordering & Pagination](../ordering-pagination.md) — `OrderByField` requires a preceding `WhereField` to identify the table
- Sibling: [Lookup and Range-Scan Operations](../../Indexing/lookup-and-range-scan.md) — `WhereField` compiles down to an index range scan

<!-- Deep dive: claude/overview/05-query.md §5.1, §5.2, §5.9 -->
<!-- Deep dive: claude/design/Querying/QueryEngine.md -->
<!-- Deep dive: claude/design/Querying/ViewSystem/10-predicate-taxonomy.md (L1-L4) -->
