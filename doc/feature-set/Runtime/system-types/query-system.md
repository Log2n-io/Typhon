# QuerySystem
> Reactive per-entity system that auto-skips when nothing relevant changed, with optional automatic multi-core chunking.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](../README.md)
**Assumes:** [Query System (EcsQuery)](../../Ecs/query-system.md)

## 🎯 What it solves

Most game logic is per-entity (AI, movement, status effects, combat), and in a large persistent world most
entities are unchanged on any given tick. Re-scanning the full population every tick wastes CPU; hand-rolling
"did anything I care about change?" checks per system is repetitive and easy to get subtly wrong. `QuerySystem`
gives per-entity logic a reactive trigger — it runs only when its input has dirty entities or pending events —
and an opt-in `Parallel()` mode that chunks the entity set across worker threads without changing the
`Execute` loop body.

## ⚙️ How it works (in brief)

Derive from `QuerySystem`, declare an `Input` View and an optional `ChangeFilter` in `Configure`. At dispatch,
the View refreshes and the runtime checks: any `ChangeFilter` dirty/Added entity, or any pending event in a
consumed queue? If neither, the system skips (~200-300ns) and successors still dispatch. Otherwise
`ctx.Entities` yields `dirtySet ∪ Added` (with a filter) or the full View (without one), and a `Transaction`
(`ctx.Transaction`) is created for the system to use. Adding `b.Parallel()` splits the filtered set into chunks
run on different workers — `Execute` is called once per chunk, `ctx.Entities` is that chunk's slice, and entity
access goes through `ctx.Accessor` by default (a lightweight, thread-safe reader/writer) unless the system
writes a `Versioned` component, in which case `b.WritesVersioned()` switches each chunk to its own
`ctx.Transaction`.

## 💻 Usage

```csharp
// Sequential — runs only on ticks where a Health component changed
public class HealthRegen : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("HealthRegen")
        .Input(() => unitsView)
        .ChangeFilter(typeof(EcsHealth))
        .After("InputDrain");

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            ref var hp = ref ctx.Transaction.OpenMut(id).Write<EcsHealth>();
            hp.Current = Math.Min(hp.Current + 1, hp.Max);
        }
    }
}
dag.Add(new HealthRegen());

// Parallel — same loop shape, chunked across workers via ctx.Accessor
public class MovementSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Movement")
        .Input(() => activeUnitsView)
        .After("InputDrain")
        .Parallel();

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            var entity = ctx.Accessor.OpenMut(id);
            ref var pos = ref entity.Write<EcsPosition>();
            pos.X += entity.Read<EcsVelocity>().X * ctx.DeltaTime;
        }
    }
}
dag.Add(new MovementSystem());

// Lambda shorthand
dag.QuerySystem("GameRules", ctx => { foreach (var id in ctx.Entities) { /* ... */ } },
    input: () => activeEntitiesView, changeFilter: [typeof(EcsHealth)], after: "Combat");
```

| Option | Default | Effect |
|--------|---------|--------|
| `b.Input(() => view)` | required | View providing the entity set; mandatory for any `QuerySystem` |
| `b.ChangeFilter(...)` | none | Reactive trigger — entity set narrows to `dirtySet ∪ Added`; OR logic across types |
| `b.Parallel()` | off | Chunk the filtered entity set across workers |
| `b.WritesVersioned()` | off | Switch chunk access from `ctx.Accessor` to a per-chunk `ctx.Transaction` (required to write `Versioned` data in parallel) |
| `b.ChunksPerWorker(factor)` | `1.0` | Oversubscribe chunk count (`round(WorkerCount × factor)`), range `[1.0, 64.0]` |

## ⚠️ Guarantees & limits

- Reactive skip: no `ChangeFilter` dirty/Added entities AND no pending consumed-queue events → skip
  (~200-300ns); a `QuerySystem` with no `ChangeFilter` and no events runs every tick its predecessors allow.
- `Build()` rejects: no `Input` View, `Parallel()` without `Input`, `ChangeFilter` without `Input`.
- Sequential systems get one `Transaction` for the whole `Execute` call; `Parallel()` systems get either a
  per-chunk `ctx.Accessor` (default) or a per-chunk `ctx.Transaction` (`WritesVersioned()`) — chunk Transactions
  cost markedly more than `ctx.Accessor`, so only declare `WritesVersioned()` when actually writing `Versioned`
  components.
- `ctx.Accessor` can read all storage modes and write `SingleVersion`/`Transient` components but throws on a
  `Versioned` write, and cannot `Spawn`/`Destroy`/`Commit`/`Rollback`.
- The DAG, not the runtime, guarantees parallel chunks don't race — overlapping writes across parallel systems
  or chunks are a design error to fix with `.After()`/`.Before()`, not something the runtime detects.
- Scaling falls off past one CCD's worth of cores on multi-CCD hardware (cross-CCD EntityMap access) — see the
  parallel-efficiency design doc for measured numbers.

## 🧪 Tests

- [ChangeFilterTests](../../../../test/Typhon.Engine.Tests/Runtime/ChangeFilterTests.cs) — reactive skip when nothing dirty, OR logic across filtered types, `ctx.Entities == dirtySet ∪ Added`
- [ScheduleValidationTests](../../../../test/Typhon.Engine.Tests/Runtime/ScheduleValidationTests.cs) — `Build()` rejects missing `Input`, `Parallel()`/`ChangeFilter()` without `Input`
- [ParallelQueryTests](../../../../test/Typhon.Engine.Tests/Runtime/ParallelQueryTests.cs) — `b.Parallel()` chunk dispatch, `ctx.Accessor` vs. `WritesVersioned()` → `ctx.Transaction`

## 🔗 Related

- Parent feature: [System Types](./README.md)
- Sibling: [Parallel Entity Processing (QuerySystem.Parallel)](../parallel-entity-processing.md) — the four dispatch-path deep dive behind `b.Parallel()` on this system type.
- Sibling: [Reactive Dispatch: Change Filters & Run Conditions](../reactive-dispatch-change-filters.md) — the `changeFilter`/`shouldRun` mechanics this system type's reactive skip relies on.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — QuerySystem.Parallel -->
<!-- Deep dive: claude/design/Runtime/06-parallel-efficiency.md -->
<!-- Deep dive: claude/overview/13-runtime.md — QuerySystem, QuerySystem.Parallel -->
