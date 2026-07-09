---
uid: feature-runtime-declarative-scheduling-track-dag-phase-partitioning
title: 'Track → DAG → Phase Partitioning'
description: 'Tracks order coarse execution stages, DAGs group independent dependency graphs, phases order systems within one DAG.'
---

# Track → DAG → Phase Partitioning
> Tracks order coarse execution stages, DAGs group independent dependency graphs, phases order systems within one DAG.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

## 🎯 What it solves

A tick needs more structure than one flat system list. The engine's own bookkeeping (the post-tick
Fence) must run as a unit after the whole app; an app may want to split unrelated work (gameplay vs.
debug tooling) into independently-built graphs that don't accidentally couple; and within one
subsystem there's usually a coarse natural ordering (Input → Simulation → Output). Track → DAG →
Phase gives each of those three needs its own explicit, structural level instead of overloading a
single list with per-system flags.

## ⚙️ How it works (in brief)

A `RuntimeSchedule` owns an ordered list of `Track`s: built-in **Engine-Pre**, **Public** (the app's
default track), and **Engine-Post** (holds the engine's Fence DAG), with app tracks declared via
`DeclareTrack(...)` slotting in between Public and Engine-Post. Track order is execution order — every
DAG in track *N* completes before any DAG in track *N+1* begins. Each `Track` holds one or more
independent `Dag`s (`Track.DeclareDag(name)`); DAGs in the same track have no ordering relationship
with each other. Each `Dag` declares its own ordered `Phase[]` list via `.Phases(...)` (or gets a
single implicit phase if it declares none) — phases are DAG-local, so two DAGs may reuse the same
phase name without coupling. A system lands in a phase via `b.Phase(...)` in `Configure`, or in the
DAG's default phase if it never calls it.

## 💻 Usage

```csharp
var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 8 });

// One DAG on the built-in Public track, with its own phase order.
var game = schedule.PublicTrack.DeclareDag("Game")
    .Phases(Phase.Input, Phase.Simulation, Phase.Output)
    .DefaultPhase(Phase.Simulation);

class ReadInputSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b.Name("ReadInput").Phase(Phase.Input);
    protected override void Execute(TickContext ctx) { /* ... */ }
}

game.Add(new ReadInputSystem());
game.Add(new MovementSystem());   // Configure: b.Name("Movement") — lands in Phase.Simulation (the default)
game.Add(new RenderSystem());     // Configure: b.Name("Render").Phase(Phase.Output)

// A second, independent app track + DAG — runs only after every Public-track DAG has completed.
var debugTrack = schedule.DeclareTrack("Debug");
debugTrack.DeclareDag("DebugOverlay").Add(new DebugDrawSystem());

using var scheduler = schedule.Build(registry.Runtime);
scheduler.Start();
```

| Level | Declared with | Ordering rule |
|---|---|---|
| Track | `RuntimeSchedule.DeclareTrack(name, tags...)` | Declaration order = execution order; a coarse barrier between tracks |
| DAG | `Track.DeclareDag(name)` | DAGs in the same track are independent of each other |
| Phase | `Dag.Phases(...)` + `b.Phase(...)` | DAG-local total order; unset systems land in the DAG's default phase |

## ⚠️ Guarantees & limits

- Declaring a DAG is mandatory — there is no default-DAG convenience; every system belongs to exactly
  one named `Dag`.
- A DAG that declares no phases gets a single implicit phase — fine for a trivial DAG ordered entirely
  by `.After()`/`.Before()`.
- Phases are DAG-local: there is no engine-global phase namespace, and the same phase name reused in
  two different DAGs does not couple them.
- App track names may not start with the reserved `Engine-` prefix, may not duplicate an existing
  track name, and may not carry the reserved `engine` tag — those mark the built-in Engine-Pre /
  Engine-Post tracks only.
- `.After()` / `.Before()` and access-conflict derivation never cross a DAG boundary — `Build()`
  rejects a cross-DAG dependency edge outright.
- The Engine-Post track (the Fence) is dispatched from the post-tick hook rather than the in-tick
  track loop, because it depends on serial tick-fence prep finishing first. The "track order =
  execution order" contract still holds — only the trigger differs.
- `DagScheduler.UserSystems` / `SystemCount` exclude systems on `engine`-tagged tracks so tooling
  counts stay app-focused; `AllSystemCount` includes them.

## 🔗 Related

- Parent feature: [Declarative Scheduling — Auto-DAG (RFC 07)](./README.md)
- Sibling: [Access Declarations & Build-Time Conflict Detection](./access-conflict-detection.md) — the per-system read/write declarations that combine with phase order to derive DAG edges.

<!-- Deep dive: claude/adr/052-track-dag-partitioning-hierarchy.md -->
<!-- Deep dive: claude/overview/13-runtime.md §Track → DAG → Phase → System partitioning -->
<!-- Deep dive: claude/rules/runtime-scheduling.md (PS-01, PR-01..PR-03) -->
