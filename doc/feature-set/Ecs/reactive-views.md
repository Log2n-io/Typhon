# Reactive Views (EcsView)
> Persistent, incrementally-maintained query results — documented in full under Querying, since `ToView()` is a terminal method on `EcsQuery`.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Ecs](./README.md)

**Assumes:** [Query System (EcsQuery)](./query-system.md)

## 🎯 What it solves

Game loops and reactive systems need to repeatedly know "which entities currently match this filter" without re-running a full scan every tick. This is listed here too because `EcsQuery<TArchetype>.ToView()` is the entry point application code actually calls, and it reads naturally as an ECS capability — but the full write-up (mechanics, modes, guarantees, tests) lives in one place to avoid two docs drifting apart on the same feature.

→ **See [Persistent Views — Incremental Refresh & Delta Tracking](../Querying/persistent-views.md)** in the Querying category for the complete documentation: how `Incremental`/`OR`/`Pull` modes are chosen, the ring-buffer change-capture mechanism, `Refresh`/delta semantics, config knobs, guarantees and limits (including the SingleVersion/Transient validation gap behind the Partial status), and tests.

## 🔗 Related

- Canonical doc: [Querying/persistent-views.md](../Querying/persistent-views.md)

<!-- Deep dive: claude/design/Ecs/05-query-system.md §EcsView — Persistent Query Results -->
<!-- ADR: 042-view-system-architecture (claude/adr/042-view-system-architecture.md) -->
