---
uid: feature-errors-runtime-access-validation
title: 'Runtime/Scheduler Declared-Access Validation'
description: 'Opt-in InvalidAccessException when a system writes a component it never declared.'
---

# Runtime/Scheduler Declared-Access Validation
> Opt-in `InvalidAccessException` when a system writes a component it never declared.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Errors](./README.md)

## 🎯 What it solves

Typhon's scheduler derives its execution DAG from what each system *declares* it writes
(`SystemBuilder.Writes<T>()` / `SideWrites<T>()`), not from what the system body actually does. If a
system's code drifts from its declaration — someone adds a `entity.Write(Comp)` call and forgets the
matching builder declaration — the scheduler keeps building a DAG from stale information. Nothing about
that failure is loud: the write still succeeds, but the parallelism/ordering guarantees the scheduler
computed around the declared set are now silently wrong. This validator turns that drift into an
immediate, specific exception when the check is switched on, before it ships as a hard-to-reproduce race.

## ⚙️ How it works (in brief)

With the check enabled, every `EntityRefMut.Write<T>()` call is checked against the declared `Writes`/`SideWrites`
set of the currently-executing system. A mismatch throws `InvalidAccessException` naming the system, the
undeclared component type, and everything the system *did* declare. Systems that haven't declared any
access yet (migration window) are exempt — the check only activates once a system declares at least one
`Writes`/`SideWrites`. It is a runtime strict-mode check, not a build flavour: enabled by `Typhon:Checks:Enabled`
**and** `Typhon:Checks:DeclaredAccess` (both default `false`, in Debug and Release alike). Off, the gate is a
`static readonly` bool the JIT folds away — zero cost on the `Write` path.

## 💻 Usage

```csharp
class ClampSystem : QuerySystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Clamp").Phase(Phase.Simulation).After("Movement")
        .Writes<Position>();       // declares the only component this system may mutate

    protected override void Execute(TickContext ctx)
    {
        foreach (var id in ctx.Entities)
        {
            var entity = ctx.Accessor.OpenMut(id);
            ref var pos = ref entity.Write(Unit.Position);   // OK: Position is declared
            pos.X = Math.Clamp(pos.X, 0, WorldWidth);

            ref var vel = ref entity.Write(Unit.Velocity);   // check on: throws InvalidAccessException
        }                                                    // (Velocity was never declared)
    }
}

try
{
    runtime.Tick();
}
catch (InvalidAccessException ex)
{
    // ex.SystemName == "Clamp", ex.UndeclaredType == typeof(Velocity)
    // Fix: add `.Writes<Velocity>()` (or `.SideWrites<Velocity>()`) to Configure — never catch-and-continue.
}
```

## ⚠️ Guarantees & limits

- Opt-in (`Typhon:Checks:Enabled` + `Typhon:Checks:DeclaredAccess`, both off by default in every build): zero
  runtime cost when off, but also zero protection. It costs two `HashSet` lookups per write when on, so enable it
  in test runs (the engine test suite does) rather than in production.
- Fires only on writes. Reads are not cross-checked at runtime — `Reads<T>()`/`ReadsFresh<T>()`/
  `ReadsSnapshot<T>()` correctness is enforced at `Build()` time (see [Declarative Scheduling](../Runtime/declarative-scheduling/README.md)), not per-call.
- Exempts systems with zero declarations entirely (not just the specific undeclared type) — a
  not-yet-migrated system that declares nothing passes silently. Declaring even one `Writes<T>()`
  switches the system into fully-enforced mode.
- `IsTransient` is `false` (inherited default) — this is a code-declaration bug, not a condition that
  clears on retry. Fix the `Configure()` declaration; don't catch-and-retry.
- Thread-local by construction: each worker thread tracks its own executing-system descriptor, so
  concurrent dispatch across workers cannot cross-contaminate which system a violation is attributed to.
- Not a substitute for `Build()`-time conflict detection (W×W, ambiguous R×W) — this check only catches
  divergence between a system's declared writes and its actual write calls; it says nothing about
  whether two systems' declarations conflict with each other.

## 🧪 Tests

- [SystemAccessValidatorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/SystemAccessValidatorTests.cs) — an undeclared write throws `InvalidAccessException` (`SystemName`/`UndeclaredType`); a no-declarations system passes silently (migration-window exemption); a declared write passes.

## 🔗 Related

- Related catalog entries: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md), [IsTransient Retry Hint](./transience-hint.md)
- Sibling feature: [Declarative Scheduling — Auto-DAG](../Runtime/declarative-scheduling/README.md) (the `Writes<T>()`/`SideWrites<T>()` declarations this validator enforces)
- Sibling: [Access Conflict Detection](../Runtime/declarative-scheduling/access-conflict-detection.md) — `Build()`-time declared-conflict detection; this validator is the runtime, per-call complement

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md (Q6, Unit 4) -->
<!-- Deep dive: rules/runtime-scheduling.md (DV-01..DV-03) -->
<!-- Overview: claude/overview/10-errors.md, claude/overview/13-runtime.md -->
