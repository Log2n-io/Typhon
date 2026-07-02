# Reactive Dispatch: Change Filters & Run Conditions
> Skip systems that have nothing to do — proactively via a predicate, reactively via dirty-entity tracking.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](./README.md)

## 🎯 What it solves
In a large persistent world, most entities are unchanged on most ticks. Running every system's full
loop every tick — re-checking health regen on units that took no damage, re-pathing NPCs that haven't
moved — burns CPU on work with no observable effect. Game logic also has conditions that gate a whole
system regardless of entity state (zone mode, feature flags, debug toggles). Without a cheap way to
express both, developers either pay the full per-entity cost every tick or hand-rewrite "did anything
change?" checks per system — repetitive and easy to get subtly wrong.

## ⚙️ How it works (in brief)
Two independent gates run before a system does any real work. `shouldRun` is a zero-arg predicate
checked first, before the system's View is even refreshed — false means skip immediately, no input
cost paid at all. `changeFilter` is checked second (QuerySystem/PipelineSystem only): it narrows the
view's `Input` so the system gets only the View's `Added` entities plus entities whose listed
component types were written since the last tick (`dirtySet ∪ Added`), and the system is skipped
entirely if that set is empty and no consumed event queue has events. The dirty set is built as a
side effect of the View's normal refresh — draining its existing ring buffer of writes — so there is
no extra write-path cost; the only added cost is one membership check per ring-buffer entry already
being processed.

## 💻 Usage
```csharp
public class HealthRegen : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("HealthRegen")
        .Input(() => unitsView)
        .ChangeFilter(typeof(EcsHealth))   // OR logic — runs only if Health changed (or unit is new)
        .After("InputDrain");

    protected override void Execute(TickContext ctx)
    {
        // ctx.Entities == dirtySet ∪ Added — not the full View
        foreach (var id in ctx.Entities)
        {
            ref var hp = ref ctx.Transaction.OpenMut(id).Write<EcsHealth>();
            hp.Current = Math.Min(hp.Current + 1, hp.Max);
        }
    }
}

public class PvPCombatSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("PvPCombat")
        .Input(() => combatView)
        .ShouldRun(() => zoneState.Mode == ZoneMode.PvP)   // evaluated before any input work
        .After("Movement");

    protected override void Execute(TickContext ctx) { /* ... */ }
}

// Lambda shorthand — both gates available on the registration overloads too
dag.QuerySystem("GameRules", ctx => { foreach (var id in ctx.Entities) { /* ... */ } },
    input: () => activeEntitiesView,
    changeFilter: [typeof(EcsHealth), typeof(EcsStatus)],
    shouldRun: () => !gameState.IsPaused,
    after: "Combat");
```

| Option | Default | Effect |
|--------|---------|--------|
| `b.ShouldRun(() => bool)` | none | Proactive gate, evaluated before View refresh / input cost. All system types. |
| `b.ChangeFilter(params Type[])` | none | Reactive gate, narrows entity set to `dirtySet ∪ Added`. Requires `b.Input(...)`. |

## ⚠️ Guarantees & limits
- Evaluation order is fixed: `shouldRun` first → View refresh (if any) → event-queue/`changeFilter`
  empty check → dispatch. A false `shouldRun` never pays View refresh or filter cost.
- `changeFilter` is OR logic across listed types — an entity qualifies if *any* listed component was
  written, not all.
- `changeFilter` requires `Input`; `Build()` rejects it on a `CallbackSystem` or any system without a
  View — there is no entity set to narrow.
- Skip cost is ~200-300ns in both cases; `shouldRun` predicates must be cheap, thread-safe, and
  side-effect-free — they run on whichever worker thread dispatches the system, with no synchronization.
- If the View's ring buffer overflows, the dirty-tracking falls back to a full re-scan and treats every
  entity in the View as dirty for that tick — correct but conservative, not a silent miss.
- A system can combine `shouldRun`, `changeFilter`, and consumed event queues; it executes if
  `shouldRun` passes AND (the filtered entity set is non-empty OR a consumed queue has events).
- A `QuerySystem`/`PipelineSystem` with *neither* `changeFilter` nor consumed events runs every tick
  its predecessors allow — there's no reactive trigger to gate it.

## 🧪 Tests

- [ChangeFilterTests](../../../test/Typhon.Engine.Tests/Runtime/ChangeFilterTests.cs) — `dirtySet ∪ Added` narrowing, OR logic across multiple filtered types, no-filter runs every tick
- [ShouldRunTests](../../../test/Typhon.Engine.Tests/Runtime/ShouldRunTests.cs) — proactive gate evaluated before View refresh, successors still dispatch on skip, dynamic per-tick predicate

## 🔗 Related
- Sibling feature: [QuerySystem](./system-types/query-system.md)

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — Change-Filtered System Inputs -->
<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — System Run Conditions -->
