# Declarative System Scheduling (Track → DAG → Phase, Auto-DAG)
> Systems declare read/write access and a phase; the scheduler derives the DAG and rejects unsafe conflicts at Build().

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](./README.md)

## 🎯 What it solves
Hand-wired `.After()` edges don't scale: adding a new writer of a component means auditing every
existing system that touches it, and a missed edge is a silent race — nondeterministic behavior
that only reproduces under load. Manual DAGs also under-parallelize, because a hand-written graph
is only as concurrent as the developer bothered to make it, leaving cores idle. Declarative access
turns "who reads/writes what" into an explicit, checkable fact instead of tribal knowledge.

## ⚙️ How it works (in brief)
Systems register under a `Track → Dag → Phase` hierarchy: tracks run in strict execution order
(all DAGs of track N finish before track N+1 starts), DAGs are independent dependency graphs, and
each DAG declares its own ordered phase sequence. Each system declares a phase (`b.Phase(...)`)
and an access set (`b.Reads<T>()`, `b.Writes<T>()`, `b.ReadsFresh<T>()`, `b.ReadsSnapshot<T>()`,
plus event-queue and named-resource variants) on `SystemBuilder`. At `Build()`, the scheduler
groups systems by phase, derives a DAG edge for every access relationship it can prove safe, and
hard-errors — with a fix suggestion — for the ones it can't (two same-phase writers of a
component, or a plain `Reads<T>()` against a same-phase writer). `.After()` / `.Before()` remain
as the disambiguation tool for that error and as an escape hatch for ordering that isn't
access-driven. Across phases, edges are conflict-driven rather than all-to-all, so independent
systems in adjacent phases can run concurrently instead of waiting on an unrelated straggler.

## 💻 Usage
```csharp
var schedule = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 60, WorkerCount = 8 });

var dag = schedule.PublicTrack.DeclareDag("Game")
    .Phases(Phase.Input, Phase.Simulation, Phase.Output, Phase.Cleanup)
    .DefaultPhase(Phase.Simulation);

dag.Add(new MovementSystem());    // Configure(): b.Name("Movement").Phase(Phase.Simulation).Writes<Position>();
dag.Add(new RenderSyncSystem());  // Configure(): b.Name("RenderSync").Phase(Phase.Output).ReadsFresh<Position>();

using var scheduler = dag.Build(parentResource);
```

A system declaring a same-phase write conflict must disambiguate explicitly:
```csharp
protected override void Configure(SystemBuilder b) => b
    .Name("Clamp")
    .Phase(Phase.Simulation)
    .Writes<Position>()
    .After("Movement");   // both write Position in Simulation — Build() requires this edge
```

| Declaration | Effect |
|---|---|
| `Reads<T>()` | Plain read; legal only if no same-phase writer of `T` exists |
| `ReadsFresh<T>()` | Ordered after same-phase writers of `T` — sees this-tick value |
| `ReadsSnapshot<T>()` | Ordered before same-phase writers of `T` — sees previous-tick value (requires Versioned `T`) |
| `Writes<T>()` | Mutates `T`; two same-phase writers require `.After()` / `.Before()` |
| `ExclusivePhase()` | System runs alone in its phase |

## ⚠️ Guarantees & limits
- `W×W` (two same-phase writers of a component) and ambiguous `R×W` (plain `Reads<T>()` against a
  same-phase writer) are `Build()`-time errors naming both systems — never silent races.
- Access tracking is component-level, not field-level — `Writes<T>()` covers any field of `T`.
- `ReadsSnapshot<T>()` requires `T` to use Versioned storage; SingleVersion/Transient components
  have no prior-tick history to freeze to and are rejected at `Build()`.
- Phases order systems within one DAG only. Cross-DAG ordering is structural (track sequence), not
  access-derived — `.After()` / `.Before()` spanning DAGs is rejected at `Build()`.
- Cross-phase edges are conflict-driven, not all-to-all: a straggler in phase N no longer gates
  every system in phase N+1, only the ones with a declared data dependency on it.
- `Build()` validation has no suppress switch — a false positive is fixed by correcting the
  declaration, not disabling the check.
- DEBUG builds assert every `EntityRef.Write<T>()` against the executing system's declared writes;
  RELEASE strips the check (`[Conditional("DEBUG")]`) for zero production overhead.

## 🧪 Tests

- [AccessDagDerivationTests](../../../test/Typhon.Engine.Tests/Runtime/AccessDagDerivationTests.cs) — W×W same-phase throws, `.After()`/`.Before()` disambiguation, `ReadsFresh`/`ReadsSnapshot` edge derivation, `ReadsSnapshot` on SingleVersion rejected
- [SystemBuilderFluentTests](../../../test/Typhon.Engine.Tests/Runtime/SystemBuilderFluentTests.cs) — `Reads`/`Writes`/`ReadsFresh`/`ReadsSnapshot` declaration API, dedup, `Before`/`After` cycle detection
- [SystemAccessValidatorTests](../../../test/Typhon.Engine.Tests/Runtime/SystemAccessValidatorTests.cs) — DEBUG-only assert that `EntityRef.Write<T>()` matches the system's declared writes

## 🔗 Related
- Parent feature: [Runtime](./README.md)
- Sibling: [Declarative Scheduling — Auto-DAG (RFC 07)](./declarative-scheduling/README.md) — near-duplicate catalog entry for the same Track→DAG→Phase access-declaration system, filed under its own sub-category.

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md -->
<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md -->
<!-- Deep dive: claude/rules/runtime-scheduling.md -->
<!-- Deep dive: claude/adr/052-track-dag-partitioning-hierarchy.md -->
