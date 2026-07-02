# CallbackSystem
> Proactive system that runs every tick for non-entity work — timers, input draining, global state.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Some tick logic has nothing to do with entity iteration and must still run every tick regardless of what
changed: draining a network input queue, advancing a global clock, flushing pending entity destroys. Routing
that through an entity-input system would mean creating a View it never uses just to get dispatched.
`CallbackSystem` is the proactive base type — no View, no `ChangeFilter`, no reactive skip — for logic that the
runtime should simply invoke once per tick.

## ⚙️ How it works (in brief)

Derive from `CallbackSystem`, implement `Configure(SystemBuilder b)` (name, dependencies, optional
`ShouldRun`) and `Execute(TickContext ctx)`. The dispatching worker runs `Execute` inline — no entity-prep
step, no input refresh. The runtime creates a per-system `Transaction` on the same worker before `Execute`
runs and auto-commits it after; `ctx.Entities` is empty since there is no View input. `ShouldRun` (optional) is
evaluated once before dispatch — returning false skips the system for this tick while still dispatching
successors.

## 💻 Usage

```csharp
public class InputDrain : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("InputDrain")
        .Priority(SystemPriority.High);

    protected override void Execute(TickContext ctx)
    {
        // Drain network command queues, write results into components via ctx.Transaction.
        var entity = ctx.Transaction.OpenMut(someEntityId);
        ref var cmd = ref entity.Write<PendingCommand>();
        // ...
    }
}

dag.Add(new InputDrain());

// Lambda shorthand — trivial logic, no class boilerplate
dag.CallbackSystem("Cleanup", ctx => ctx.FlushDestroys(), after: "InputDrain");

// Conditional proactive system — skips most ticks
dag.CallbackSystem("ZoneRotation", ctx => RotateZone(ctx),
    shouldRun: () => zoneState.RotationDue, after: "InputDrain");
```

## ⚠️ Guarantees & limits

- Proactive — runs every tick unless `b.ShouldRun(...)` (or the lambda's `shouldRun:`) returns false. Cost of a
  false `ShouldRun`: ~200-300ns, successors still dispatch.
- No entity input is possible: `Build()` rejects `b.Input(...)` and `b.ChangeFilter(...)` on a `CallbackSystem`.
- Gets its own `Transaction` per tick (`ctx.Transaction`), created and committed by the runtime — never call
  `Commit()`/`Dispose()` inside `Execute`.
- A thrown exception rolls back the system's Transaction and skips its successors
  (`SkipReason.DependencyFailed`); other DAG branches continue.
- `b.Parallel()` is not valid on a plain `CallbackSystem` — use `ChunkedCallbackSystem` and `b.ChunkedParallel(N)`
  for chunk-parallel non-entity work.
- Registered via `dag.Add(new MySystem())` (class-based) or `dag.CallbackSystem(name, ctx => ..., ...)` (lambda)
  — both coexist in the same DAG.

## 🧪 Tests

- [ClassBasedSystemTests](../../../../test/Typhon.Engine.Tests/Runtime/ClassBasedSystemTests.cs) — `Add_CallbackSystem_ExecutesEveryTick`, unnamed-system rejection
- [ScheduleValidationTests](../../../../test/Typhon.Engine.Tests/Runtime/ScheduleValidationTests.cs) — `Build_ChangeFilterOnCallbackSystem_Throws`, `Build_ParallelOnCallbackSystem_Throws` — no entity input/parallel on a plain `CallbackSystem`
- [ShouldRunTests](../../../../test/Typhon.Engine.Tests/Runtime/ShouldRunTests.cs) — proactive `ShouldRun` gate: skip cost, successors still dispatch, telemetry recording

## 🔗 Related

- Parent feature: [System Types](./README.md)
- Sibling: [Typed Event Queues](../typed-event-queues.md) — proactive `CallbackSystem` producers/consumers commonly drive event-queue cascades.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — Proactive vs Reactive -->
<!-- Deep dive: claude/overview/13-runtime.md — CallbackSystem -->
