---
uid: feature-runtime-declarative-scheduling-index
title: 'Declarative Scheduling — Auto-DAG (RFC 07)'
description: 'Systems declare phase and read/write access; the scheduler derives the DAG and rejects unsafe overlaps at Build().'
---

# Declarative Scheduling — Auto-DAG (RFC 07)
> Systems declare phase and read/write access; the scheduler derives the DAG and rejects unsafe overlaps at Build().

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Runtime](../README.md)

## 🎯 What it solves

Manual dependency wiring (`.After()` edges on every system) doesn't scale: adding a new writer of a
component means auditing every existing system that touches it, and a missed edge is a silent race —
nondeterministic behavior that only reproduces under load. It also leaves cores idle, because a
hand-written DAG is only as parallel as the developer bothered to make it. Declarative access turns
"who reads/writes what" into an explicit fact the scheduler can act on, so overlapping writes and
ambiguous reads become a `Build()`-time error instead of a production bug.

## ⚙️ How it works (in brief)

Every system declares a phase (its position in a DAG-local ordering) and a read/write set on
`SystemBuilder` — `Reads<T>()`, `Writes<T>()`, `ReadsFresh<T>()`, `ReadsSnapshot<T>()`, plus
event-queue and named-resource variants. At `Build()`, the scheduler groups systems by phase, derives
a DAG edge for every access relationship it can prove is safe (e.g. a fresh reader runs after its
writer), and throws — with a copy-paste-ready fix — for relationships it can't (two writers of the
same component, same phase, no declared order). The derived graph is an ordinary dependency DAG; the
any-worker, claim-on-CAS dispatch loop that executes it at runtime is unchanged by this RFC — only how
the DAG gets *built* changed.

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

## Sub-features

| Sub-feature | Use it when... |
|---|---|
| [Track → DAG → Phase Partitioning](./track-dag-phase-partitioning.md) | Structuring the schedule itself — grouping systems into independent DAGs, ordering coarse execution stages, declaring a DAG's phase sequence |
| [Access Declarations & Build-Time Conflict Detection](./access-conflict-detection.md) | Declaring what one system reads/writes so the scheduler can order it safely and parallelize it against everything else |

## ⚠️ Guarantees & limits

- `W×W` (two writers of the same component, same phase) and ambiguous `R×W` (plain `Reads<T>()`
  against a same-phase writer) are `Build()`-time errors with copy-paste-ready suggestions — never
  silent races.
- Access tracking is component-level, not field-level — `Writes<T>()` means "touches any field of T."
- `.After()` / `.Before()` remain as the escape hatch for non-access ordering constraints, and as the
  required disambiguation tool for an intentional same-phase `W×W`.
- An opt-in runtime check (`Typhon:Checks:DeclaredAccess`, off by default in every build) asserts every
  `EntityRefMut.Set<T>()` against the executing system's declared `Writes`/`SideWrites` set; off, the JIT
  folds the gate away — zero overhead.
- This is declaration, not inference — Typhon never inspects a system body to detect its actual
  reads/writes. An undeclared access is simply invisible to the scheduler (and, with the check on, only the
  write side is cross-checked at runtime).
- `ReadsSnapshot<T>()` requires `T` to use Versioned storage; SingleVersion/Transient components have no
  prior-tick history to freeze to and are rejected at `Build()`.
- Phases order systems within one DAG only. Cross-DAG ordering is structural (track sequence), not
  access-derived — `.After()` / `.Before()` spanning DAGs is rejected at `Build()`.
- Cross-phase edges are conflict-driven, not all-to-all: a straggler in phase N gates only the systems in
  phase N+1 with a declared data dependency on it, not every system in the phase.
- `Build()` validation has no suppress switch — it's a one-shot startup cost; a false positive should
  be fixed by correcting the declaration, not disabled.

## 🧪 Tests

- [AccessDagDerivationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/AccessDagDerivationTests.cs) — W×W same-phase throws, `.After()`/`.Before()` disambiguation, `ReadsFresh`/`ReadsSnapshot` edge derivation, `ReadsSnapshot` on SingleVersion rejected
- [SystemBuilderFluentTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/SystemBuilderFluentTests.cs) — `Reads`/`Writes`/`ReadsFresh`/`ReadsSnapshot` declaration API, dedup, `Before`/`After` cycle detection
- [SystemAccessValidatorTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Runtime/SystemAccessValidatorTests.cs) — opt-in assert that `EntityRefMut.Set<T>()` matches the system's declared writes

## 🔗 Related

- Sub-features: [Track → DAG → Phase Partitioning](./track-dag-phase-partitioning.md), [Access Declarations & Build-Time Conflict Detection](./access-conflict-detection.md)

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md -->
<!-- Deep dive: claude/design/Runtime/02-system-scheduling.md -->
<!-- Deep dive: rules/runtime-scheduling.md -->
<!-- Deep dive: claude/adr/052-track-dag-partitioning-hierarchy.md -->
