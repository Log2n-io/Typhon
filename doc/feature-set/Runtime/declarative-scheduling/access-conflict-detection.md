---
uid: feature-runtime-declarative-scheduling-access-conflict-detection
title: 'Access Declarations & Build-Time Conflict Detection'
description: 'Reads/Writes/ReadsFresh/ReadsSnapshot declare what a system touches; Build() derives safe ordering and rejects unsafe overlaps.'
---

# Access Declarations & Build-Time Conflict Detection
> Reads/Writes/ReadsFresh/ReadsSnapshot declare what a system touches; Build() derives safe ordering and rejects unsafe overlaps.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Two systems in the same phase writing the same component, or a system reading data with no stated
opinion on whether it wants this-tick or previous-tick values, are both silent races today and
correctness bugs in production. Declared access makes "who touches what" an explicit, checkable fact
instead of something a developer has to remember to verify by hand every time a new system is added.

## ⚙️ How it works (in brief)

On `SystemBuilder`, a system declares `Reads<T>()` / `Writes<T>()` for components, `WritesEvents` /
`ReadsEvents` for event queues, and `WritesResource` / `ReadsResource` for named non-component
resources. Two read variants resolve same-phase write conflicts explicitly: `ReadsFresh<T>()` orders
the reader after any same-phase writer (sees this tick's value), `ReadsSnapshot<T>()` orders it before
(sees the previous tick's value — and is only legal on `Versioned` components, since there's no MVCC
history to freeze to on `SingleVersion`/`Transient`). Plain `Reads<T>()` is legal only when no
same-phase writer of `T` exists. At `Build()`, the deriver walks each DAG's systems phase by phase:
same-phase write/write and ambiguous read/write overlaps throw with a fix suggestion; every other
access pair derives a DAG edge. Across phases the deriver is conflict-driven, not all-to-all — a
phase-N+1 system only waits on a phase-N system it genuinely conflicts with, so unrelated systems still
overlap even though phase order remains the human-readable contract.

## 💻 Usage

```csharp
private struct Position { public float X, Y; }
private struct Velocity { public float X, Y; }

class MovementSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Movement").Phase(Phase.Simulation)
        .Input(() => _movingUnits).Parallel()
        .Reads<Velocity>().Writes<Position>();

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            var entity = ctx.Accessor.OpenMut(id);
            ref var pos = ref entity.Write(Unit.Position);
            pos.X += entity.Read(Unit.Velocity).X * ctx.DeltaTime;
        }
    }
}

class ClampSystem : QuerySystem
{
    // Same phase, same component as Movement — the W×W must be disambiguated or Build() throws (AC-01).
    protected override void Configure(SystemBuilder b) => b
        .Name("Clamp").Phase(Phase.Simulation).After("Movement")
        .Writes<Position>();
    // Execute(...) elided.
}

class RenderSystem : QuerySystem
{
    // Different (later) phase — observes this tick's clamped Position via the derived cross-phase edge.
    protected override void Configure(SystemBuilder b) => b
        .Name("Render").Phase(Phase.Output)
        .ReadsFresh<Position>();
    // Execute(...) elided.
}
```

## ⚠️ Guarantees & limits

- `Build()`-time errors, with copy-paste-ready suggestions, for: same-phase `W×W` with no declared
  order; same-phase plain `Reads<T>()` against a writer of `T`; resource `W×W` with no declared order;
  two systems in one phase both declaring `ExclusivePhase()`, or one declaring it while sharing the
  phase with anyone else.
- `ReadsSnapshot<T>()` requires `T` to be `Versioned` — `Build()` rejects it on `SingleVersion` or
  `Transient` components (no MVCC history to freeze to).
- Cross-phase `ReadsSnapshot<T>()` against an earlier-phase writer degrades to fresh semantics: phase
  order already forces "writer first," so there's nothing earlier left to freeze to. This is a
  documented, intentional deviation from the intra-phase meaning.
- `SideWrites<T>()` (writes via a `DurabilityMode.Immediate` side-transaction) is surfaced to tooling
  but intentionally does **not** participate in scheduler ordering.
- A DEBUG-only check (`SystemAccessValidator`) throws `InvalidAccessException` when `EntityRef.Write<T>()`
  runs from a system that didn't declare `Writes<T>`/`SideWrites<T>` — silently skipped for systems
  with zero declarations (migration window) and compiled out entirely in RELEASE.
- Conflict checks consult only direct `.After()`/`.Before()` adjacency, not transitive reachability —
  a chain `A.Before(B).Before(C)` does not implicitly resolve an `A`/`C` write conflict; each pair
  needs its own edge.
- Access is the unit of truth for derivation, not actual runtime behavior — Typhon never inspects what
  a system body really touches; an undeclared access is simply invisible to the scheduler.

## 🔗 Related

- Parent feature: [Declarative Scheduling — Auto-DAG (RFC 07)](./README.md)
- Sibling: [Track → DAG → Phase Partitioning](./track-dag-phase-partitioning.md) — the phase/DAG structure this access derivation runs within.
- Sibling: [Runtime/Scheduler Declared-Access Validation](../../Errors/runtime-access-validation.md) — the DEBUG-only runtime check that cross-verifies these declarations.

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md -->
<!-- Deep dive: claude/rules/runtime-scheduling.md (AC-01..AC-05, ED-01..ED-05f, DV-01..DV-03) -->
