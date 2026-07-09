---
uid: feature-querying-view-factory-pooling
title: 'ViewFactory — Parameterized Queries & View Pooling'
description: 'Reusable query templates with a Rent/Return view pool — planned, to remove per-session view setup cost.'
---

# ViewFactory — Parameterized Queries & View Pooling
> Reusable query templates with a Rent/Return view pool — planned, to remove per-session view setup cost.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](./README.md)

## 🎯 What it solves

Server workloads with high session churn — a player logging in and out of a game server, a client opening and closing a UI panel — repeatedly construct the *same* view (same predicate shape, different parameter values: this player's zone, this player's level threshold) and then tear it down minutes later. Building a [Persistent View](./persistent-views.md) today re-parses the predicate expression, re-extracts field dependencies, and allocates a fresh ring buffer every time, even though the query shape never changes between sessions. None of that is necessary work — only the parameter values and the resulting entity set are session-specific.

## ⚙️ How it works (in brief)

A `ViewFactory` is built once at startup from a parameterized predicate — a lambda over both the component and a user-defined parameter struct (e.g., `(p, prm) => p.Zone == prm.ZoneId && p.Level > prm.MinLevel`). Expression parsing, field-dependency extraction, and evaluator compilation happen exactly once, at `Build()`. Per session, `Rent(tx, params)` either pulls an idle view from the factory's pool or allocates a new one, binds the parameter struct's concrete values into the cached evaluator template, re-registers the view for commit-time change notifications, and runs the initial population — all without re-parsing anything. `Return(view)` deregisters it and pushes it back onto the pool (capped at `MaxPooled`) for the next session to reuse; `view.Dispose()` is a safety-net equivalent of `Return()`. `Rebind(tx, newParams)` lets a long-lived view switch parameters mid-session without leaving the pool.

## 💻 Usage

```csharp
// Illustrative only — design complete (claude/design/Querying/ViewSystem/phase-4.md),
// not yet implemented; none of this compiles against today's API.

struct SessionParams
{
    public int ZoneId;
    public int MinLevel;
}

// At startup (once)
var factory = dbe.QueryFactory<Player, SessionParams>()
    .Where((p, prm) => p.Zone == prm.ZoneId && p.Level > prm.MinLevel)
    .Build(maxPooled: 32);

// Per player login
ViewBase view = factory.Rent(tx, new SessionParams { ZoneId = 3, MinLevel = 1 });

// Per tick
view.Refresh(tx);
var delta = view.GetDelta();
// ...
view.ClearDelta();

// Per player logout
factory.Return(view);   // or view.Dispose() — equivalent safety net

// Server shutdown — all sessions must have returned their views first
factory.Dispose();      // throws if any view is still rented
```

| Option | Default | Effect |
|---|---|---|
| `Build(maxPooled:)` | `Environment.ProcessorCount` | Cap on idle pooled views; pool grows on demand up to this, trims excess on `Return` |

## ⚠️ Guarantees & limits

- **Not implemented.** Design is complete (Phase 4); no `ViewFactory`, `QueryFactoryBuilder`, or `db.QueryFactory<…>()` exists in the codebase today. Names and shapes above are a design sketch, subject to change.
- Planned cold-path savings: expression parsing, field-dependency extraction, and evaluator compilation move from per-session (~80–160 µs) to once at `Build()`; a pool-hit `Rent()` additionally skips ring-buffer allocation (~5–10 µs).
- Initial population cost on `Rent`/`Rebind` is **not** eliminated — re-querying against new parameter values is unavoidable and expected to dominate (~100 µs–10 ms depending on result set size).
- Planned arities: 1- and 2-component predicates (`QueryFactory<T, TParams>`, `QueryFactory<T1, T2, TParams>`); N≥3 needs prior `QueryBuilder` extensions and is out of scope for this phase.
- Pooled views sit deregistered and idle (zero commit-time cost) but keep their ring buffer allocated — bounded by `MaxPooled`, not by inter-session activity.
- Parameter rebinding is explicit only — the factory does not detect or react to parameter changes on its own; the caller must call `Rebind()`.
- `Rent()`/`Return()` are planned to be thread-safe (callable from login/logout handlers on any thread); `Refresh()`/`Rebind()` remain single-consumer, same as a plain view.
- `Dispose()` on a factory is planned to throw if any rented view has not been returned — sessions must log out before shutdown.

## 🔗 Related

- Related feature: [Persistent Views — Incremental Refresh & Delta Tracking](./persistent-views.md) — the underlying view mechanism `ViewFactory` pools and parameterizes
- Sibling: [Reactive Views (EcsView)](../Ecs/reactive-views.md) — the Ecs-category view type `ViewFactory` will pool and parameterize

<!-- Deep dive: claude/design/Querying/ViewSystem/phase-4.md — full design (parameterized predicates, pooling, state machine, concurrency, cost analysis) -->
<!-- Deep dive: claude/design/Querying/ViewSystem/README.md — view system design series this phase builds on -->
<!-- Overview: claude/overview/05-query.md — architectural reference -->
