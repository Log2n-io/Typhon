---
uid: feature-errors-runtime-access-validation
title: 'Runtime/Scheduler Declared-Access Validation'
description: 'InvalidAccessException when a system reads, writes, spawns, destroys or exchanges events through something it never declared. On by default in Debug builds.'
---

# Runtime/Scheduler Declared-Access Validation
> `InvalidAccessException` when a system touches something it never declared. On by default in a Debug build of your application.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves

Typhon's scheduler derives its execution DAG from what each system *declares* (`Reads<T>()`, `Writes<T>()`, `WritesEvents(q)`…), not
from what the system body actually does. A read or write the system never declared gets no ordering edge, so it races with the systems
that do declare it: a torn or stale read, a lost update, an event consumed before it was produced. Nothing about that is loud. This
validator turns the drift into an immediate, specific exception naming the system and the type.

## ⚙️ How it works (in brief)

Inside a system's body, every access is checked against the executing system's declarations:

| Access | Entry points | Allowed when |
|---|---|---|
| Component read | `EntityRef.Read` / `TryRead`, `ClusterRef.GetReadOnly` / `GetReadOnlySpan` | `Reads`, `ReadsFresh`, `ReadsSnapshot`, `AdditionalReads` or `Writes` of the type |
| Component write | `EntityRefMut.Set`, `ClusterRef.Set` / `Get` / `GetSpan` / `WriteSpatial` | `Writes` of the type |
| Spawn | `Spawn`, `SpawnBatch` | `Writes` of every component of the archetype |
| Destroy | `Destroy`, `DestroyBatch` | `Writes` of every component of the archetype and of everything its cascade-delete reaches |
| Event write | `ctx.Writer(queue)` | `WritesEvents(queue)` |
| Event drain | `queue.Drain` | `ReadsEvents(queue)` |

`SideWrites<T>()` grants reads and writes of `T` through a side transaction (`ctx.CreateSideTransaction`) only: through the system's own
transaction the write would escape the scheduler's write-write check. A class-form system with no declaration is checked like any other.

A mismatch throws `InvalidAccessException` with the system name, the undeclared type (or queue) and everything the system did declare.
The setting is `Typhon:Checks:DeclaredAccess` (env `TYPHON__CHECKS__DECLAREDACCESS`), independent of `Typhon:Checks:Enabled`. Unset, it
is **on when your application's entry assembly is a Debug build** and off otherwise. Off, the gate is a `static readonly` bool the JIT
folds away; on, a check is a bit test over the descriptor's bitsets.

## 💻 Usage

```csharp
class ClampSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Clamp").Phase(Phase.Simulation).After("Movement")
        .Writes<Position>();       // a writer may also read what it writes

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            var entity = ctx.Accessor.OpenMut(id);
            var pos = entity.Read(Unit.Position);   // OK: Position is declared
            pos.X = Math.Clamp(pos.X, 0, WorldWidth);
            entity.Set(Unit.Position, pos);

            var vel = entity.Read(Unit.Velocity);   // throws InvalidAccessException: Velocity was never declared
        }
    }
}

// The throw happens inside Execute, on a runtime worker: the scheduler handles it per its SystemExceptionPolicy.
// ex.SystemName == "Clamp", ex.UndeclaredType == typeof(Velocity).
// Fix: add `.Reads<Velocity>()` (or ReadsFresh / ReadsSnapshot) to Configure — never catch-and-continue.
```

## ⚠️ Guarantees & limits

- **Debug default.** On in a Debug build of the application, off in Release, explicit configuration wins either way. Under `dotnet test`
  the entry assembly is the (optimized) test host, so a test project gets it only by setting the key, e.g. in its `typhon.telemetry.json`
  (the engine's own suite does).
- **Lambda-form systems are not checked.** `dag.CallbackSystem("name", ctx => …)` and its `QuerySystem` / `PipelineSystem` siblings have no
  declaration surface: they are ordered by their explicit `after:` edges alone and the validator does not see them. Use the class form for
  anything that shares data with other systems.
- **Not checked:** untyped reads by slot (`ReadRaw`, `ReadText`), query predicates, `ClusterRef.MarkDirty`, code outside a system (host
  threads, pre-start writes), deferred `ctx.Commands` spawns and destroys (applied by the runtime at a serial point), and the engine's own
  systems (an engine track's systems reach storage through internals).
- **A system that declares a component in both `Writes` and a read kind** is not a conflict with itself: `Writes<T>` grants the read.
- **A destroy depends on the entity's actual archetype**: destroying through a base-archetype handle needs `Writes` for the components of
  whatever sub-archetype the entity is, and of what its cascade-delete reaches.
- `IsTransient` is `false`: this is a declaration bug, not a condition that clears on retry. Fix `Configure()`.
- Thread-local by construction: each worker tracks its own executing system, so concurrent dispatch cannot misattribute a violation.
- Not a substitute for `Build()`-time conflict detection (W×W, ambiguous R×W): this check catches divergence between a system's
  declarations and its body, not conflicts between two systems' declarations.

## 🧪 Tests

- [DeclaredAccessTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/DeclaredAccessTests.cs) — one
  undeclared probe per access kind (each read and write entry point, spawn, destroy, event write, drain) and the declared complements;
  `SideWrites` refused through the system transaction and accepted through a side transaction; a system with no declaration checked; a
  lambda-form system not.
- [SystemAccessValidatorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/SystemAccessValidatorTests.cs) —
  the descriptor stack (push/pop, per-thread isolation, cleared after dispatch).

## 🔗 Related

- Related catalog entries: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md), [IsTransient Retry Hint](./transience-hint.md)
- Sibling feature: [Declarative Scheduling — Auto-DAG](../Runtime/declarative-scheduling/README.md) (the `Writes<T>()`/`SideWrites<T>()` declarations this validator enforces)
- Sibling: [Access Conflict Detection](../Runtime/declarative-scheduling/access-conflict-detection.md) — `Build()`-time declared-conflict detection; this validator is the runtime, per-call complement

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md (Q6, Unit 4) -->
<!-- Deep dive: rules/runtime-scheduling.md (DV-01..DV-03) -->
<!-- Overview: claude/overview/10-errors.md, claude/overview/13-runtime.md -->
