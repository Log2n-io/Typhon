---
uid: feature-profiler-query-definition-export
title: 'Query Definition & Execution Export'
description: 'Every View/EcsQuery describes its shape once, then tags each run so you can trace it back to the code that issued it.'
---

# Query Definition & Execution Export
> Every View/EcsQuery describes its shape once, then tags each run so you can trace it back to the code that issued it.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Profiler](./README.md)

## 🎯 What it solves
The query span chain (parse, plan, index scan, filter, sort, pagination) already tells you *how* a query ran, but
not *which* logical query it was, *where in your code* it was declared, or *who else* is running the same one.
Without a stable identity you can't group executions by definition, spot two systems accidentally holding
structurally-identical-but-distinct `View`s, or jump from a slow execution back to the `.Query<T>()` / `.ToView()`
call site that created it.

## ⚙️ How it works (in brief)
Every `View` and `EcsQuery` gets a process-wide, monotonic instance id at construction (`ViewId` / `EcsQueryId`).
The first time a query with a given id executes in a session, its structural shape — filters, sort, target
archetype, primary index — is written once as a definition record; every later execution of the same instance
just carries the id, so the trace never repeats the shape. Construction and execution-terminal call sites
(`.Query<T>()`, `.ToView()`, `.Count()`, `.Any()`, …) capture the caller's file/line/method at compile time via
`[CallerFilePath]`/`[CallerLineNumber]`/`[CallerMemberName]` — no stack walk, no runtime cost. Scheduler-driven
`View.Refresh` calls (no user call site) fall back to the owning system as attribution. Capture is gated behind
the `Query` telemetry category, so it costs nothing when profiling is off.

## 💻 Usage
```csharp
using Typhon.Engine;

// Definition-site attribution is automatic — no extra arguments to pass.
var query = tx.Query<Ant>()
    .With<Position>()
    .Where<Position>(a => a.Energy > 50);

// Execution-site attribution is captured at the terminal call:
int hungryAnts = query.Count();     // caller file/line/method recorded with this execution
bool anyLeft   = query.Any();

// A View records its own construction site once, then each execution keeps its own site:
var view = tx.Query<Ant>().ToView();
view.Refresh(tx);                   // scheduler-driven refresh falls back to the owning system
```

```jsonc
// typhon.telemetry.json — enable the Query category to capture definitions + per-execution args
{
  "Typhon": {
    "Profiler": {
      "Enabled": true,
      "Query": { "Enabled": true }
    }
  }
}
```

| Config key | Default | Enables |
|---|---|---|
| `Typhon:Profiler:Query:Enabled` | `false` | Query parse/plan/execute spans, `QueryDefinitionDescribe`, and per-execution `QueryArgs` |

## ⚠️ Guarantees & limits
- **Definitions dedup per session, not per execution.** One `QueryDefinitionDescribe` record per distinct
  `View`/`EcsQuery` instance, no matter how many times it runs; the threshold *values* of each run are captured
  separately as per-execution args, so two runs with different thresholds still share one definition.
- **No result-set capture.** Only what was asked and how it executed is recorded — not the entity IDs returned.
- **Immediate call site only, not a call stack.** Both construction and execution attribution are the single
  line that invoked the API, matching the [Source Attribution](./source-attribution.md) model.
- **Zero cost when disabled** — gated behind `TelemetryConfig.QueryActive`; JIT-eliminated like every other
  profiler subsystem.
- **Backward-compatible wire format** — trace files from before this feature still decode; the new fields simply
  decode as absent.
- **Partial status** — the Workbench-side Query Catalog / Plan Tree / Execution Inspector panels this data feeds
  are built but system ownership isn't wired yet (definitions currently show no owning system), the "triggered
  at" execution-site link isn't surfaced in the Execution Inspector, and polymorphic queries don't yet break out
  per-archetype-subtree stats in that view.

## 🧪 Tests
- [EcsQueryIdTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Querying/EcsQueryIdTests.cs) — `EcsQuery<T>.EcsQueryId` is monotonic and unique under concurrent construction
- [QuerySourceLocationTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Querying/QuerySourceLocationTests.cs) — `[CallerFilePath]`/`[CallerLineNumber]`/`[CallerMemberName]` substitution at user call sites, stored on the constructed `View`/`EcsQuery`
- [QueryDefinitionDescribeOnceTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Profiler/QueryDefinitionDescribeOnceTests.cs) — "describe each query identity exactly once per session" dedup semantics on `QueryDefinitionDescribeTracker`

## 🔗 Related
- Sibling features: [Span Source Attribution](./source-attribution.md), [Typed-Event Capture Pipeline](./typed-event-capture-pipeline.md)
- Source: `src/Typhon.Engine/Profiler/internals/QueryDefinitionDescribeTracker.cs`, `src/Typhon.Engine/Profiler/internals/QuerySourceStringInterner.cs`, `src/Typhon.Engine/Profiler/internals/QueryEcsViewEvents.cs`, `src/Typhon.Engine/Profiler/internals/EcsQueryEvents.cs`, `src/Typhon.Engine/Ecs/public/EcsQuery.cs`, `src/Typhon.Engine/Querying/public/ViewBase.cs`

<!-- Deep dive: claude/design/Profiler/11-query-definition-export.md, claude/design/Profiler/07-tracing-instrumentation/07-query-ecs-view.md -->
<!-- Overview: claude/overview/05-query.md -->
