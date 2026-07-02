# CompoundSystem
> Group related sub-systems' registration under one `Configure` call — one node from the outside, parallel inside.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Related systems (a pathing system, a behavior-tree system, a steering system that together form "AI") are
logically one unit, but registering them individually scatters their wiring across the schedule and forces
every dependent to know about each member by name. `CompoundSystem` lets a group declare its members in one
place and be depended on as a single name from outside, while still allowing the members to run in parallel
with each other when their declared access doesn't conflict.

## ⚙️ How it works (in brief)

Derive from `CompoundSystem`, implement `Configure()`, and call `Add(...)` for each `CallbackSystem` or
`QuerySystem` member (construct them as fields/constructor params so callers can reach into the group for
testing or telemetry). When the compound is registered with `dag.Add(compound)`, the runtime calls `Configure()`
and expands each member into its own registration on the same `Dag` — there is no compound-level DAG node;
members keep whatever name and `.After(...)` dependency they declared themselves. From outside, other systems
depending on `.After("AI")`-style names should target a specific member's name (e.g. the last stage), since the
compound itself has no name of its own.

## 💻 Usage

```csharp
public class AiGroup : CompoundSystem
{
    private readonly PathingSystem _pathing = new();
    private readonly SteeringSystem _steering = new();

    protected override void Configure()
    {
        Add(_pathing);
        Add(_steering);   // declares its own .After("Pathing") if it needs to run after _pathing
    }
}

var aiGroup = new AiGroup();
dag.Add(aiGroup);

dag.CallbackSystem("Cleanup", ctx => ctx.FlushDestroys(), after: "Steering");
```

## ⚠️ Guarantees & limits

- `Add(...)` accepts `CallbackSystem`, `QuerySystem`, or `PipelineSystem` — but a `PipelineSystem` member hits
  the same `NotSupportedException` as a top-level `dag.Add(PipelineSystem)` registration (pending Patate).
- Expansion happens once, at `dag.Add(compound)` time — each member becomes an ordinary registration on the
  owning `Dag`; there is no runtime concept of "the compound" after `Build()`.
- No aggregate name: the compound itself is not a DAG node. Internal dependency edges between members
  (`.After("Pathing")` inside `Configure`) are honored; a dependent outside the group targets a member's own
  name, not the group's class name.
- Members schedule by their own declarations — non-conflicting members run in parallel inside the group, exactly
  as they would if registered individually.
- All members must have unique names across the *whole* schedule (not just within the compound) — `Build()`
  rejects duplicates the same way it does for any two systems.
- A member that throws rolls back only its own Transaction and skips only its own successors; it does not abort
  sibling members in the same compound unless they have a declared dependency on it.

## 🧪 Tests

- [ClassBasedSystemTests](../../../../test/Typhon.Engine.Tests/Runtime/ClassBasedSystemTests.cs) — `Add_CompoundSystem_ExpandsSubSystems`, `Add_NullCompoundSystem_ThrowsArgumentNull`

## 🔗 Related

- Parent feature: [System Types](./README.md)
- Sibling: [QuerySystem](./query-system.md) — the most common member type grouped inside a CompoundSystem.

<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md — CompoundSystem -->
<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md — Q11 Compound systems -->
