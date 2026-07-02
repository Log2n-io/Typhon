# Declarative Scheduling — Auto-DAG (RFC 07)
> Systems declare phase and read/write access; the scheduler derives the DAG and rejects unsafe overlaps at Build().

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Runtime](../README.md)

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
- A DEBUG-only assertion checks every `EntityRef.Write<T>()` against the executing system's declared
  `Writes`/`SideWrites` set; `[Conditional("DEBUG")]` strips the call entirely in RELEASE — zero
  production overhead.
- This is declaration, not inference — Typhon never inspects a system body to detect its actual
  reads/writes. An undeclared access is simply invisible to the scheduler (and, in DEBUG, only the
  write side is cross-checked at runtime).
- `Build()` validation has no suppress switch — it's a one-shot startup cost; a false positive should
  be fixed by correcting the declaration, not disabled.

## 🔗 Related

- Sub-features: [Track → DAG → Phase Partitioning](./track-dag-phase-partitioning.md), [Access Declarations & Build-Time Conflict Detection](./access-conflict-detection.md)

<!-- Deep dive: claude/design/Runtime/07-system-access-declarations.md -->
<!-- Deep dive: claude/rules/runtime-scheduling.md -->
<!-- Deep dive: claude/adr/052-track-dag-partitioning-hierarchy.md -->
