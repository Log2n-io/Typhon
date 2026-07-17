---
uid: concept-resources
title: 'Resources & budgets'
description: 'A live utilization map of every significant engine resource, read on demand at zero hot-path cost. Its point: enough real-time data to throttle before you hit a budget, instead of meeting the ceiling as an exception.'
---

# Resources & budgets

> **In one line:** a live **utilization map** of the engine — enough real-time data to see pressure building and **throttle before you hit a limit**, instead of meeting the ceiling as an exception.

You set the budgets once, at startup, through `ResourceOptions` — page-cache size, max active transactions, WAL ring/segment sizing, checkpoint thresholds — and `Validate()` checks that the fixed allocations actually fit the total. From then on every significant resource (page cache, transactions, WAL, allocators, timers, …) sits in a fixed tree and reports its own utilization. Reporting is **pull-based**: metrics are read when you take a snapshot, never pushed on the hot path — so having the map costs nothing while you aren't looking at it.

**Why this earns a page:** a budget is a ceiling, and hitting one isn't negotiable — it surfaces as a [`ResourceExhaustedException`](xref:concept-errors). The graph is what lets you *not get there*: it says how close you are **right now**, so you can shed load, slow your input, or throttle while there's still room. And when something does saturate, `FindRootCause` matters more than the raw number — the node that looks busiest is usually a *symptom*, so it walks the dependency chain back to whatever is actually backed up, letting you throttle the **right** thing instead of guessing.

> ⚠️ **The engine does not auto-throttle off this graph.** [Overload management](xref:concept-overload-management) reacts to *tick overrun*, not resource metrics — separate signals. This graph is the **data**; acting on it (health checks, alerts, shedding) is the host's job. Related caveat: `ExhaustionPolicy` currently *documents* intent rather than switching behaviour at runtime.

## How it relates

- **[Errors & failures](xref:concept-errors)** — the wall: exceeding a budget throws `ResourceExhaustedException`.
- **[Overload management](xref:concept-overload-management)** — the engine's own automatic degradation, on a different signal (tick overrun).
- **[Observability & telemetry](xref:concept-observability)** — the bridge that projects this tree into OTel metrics, health checks, and alerts.
- **[Page cache & paged store](xref:concept-page-cache)** — usually the largest budget you will set.

## In the API

- [`ResourceOptions`](xref:Typhon.Engine.ResourceOptions) — the startup budgets; `Validate()` checks they fit the total.
- [`IResourceGraph.GetSnapshot()`](xref:Typhon.Engine.IResourceGraph.GetSnapshot*) → [`ResourceSnapshot`](xref:Typhon.Engine.ResourceSnapshot), with [`FindMostUtilized`](xref:Typhon.Engine.ResourceSnapshot.FindMostUtilized*) and [`FindRootCause`](xref:Typhon.Engine.ResourceSnapshot.FindRootCause*).
- [`ResourceExhaustedException`](xref:Typhon.Engine.ResourceExhaustedException) · [`ExhaustionPolicy`](xref:Typhon.Engine.ExhaustionPolicy).

## Learn & use

- **Feature detail:** [Resources](xref:feature-resources-index) — [budgets & options](xref:feature-resources-resource-budgets-options) · [exhaustion policy](xref:feature-resources-exhaustion-policy-handling) · [snapshot & query](xref:feature-resources-snapshot-query-api-index) · [root-cause analysis](xref:feature-resources-snapshot-query-api-root-cause-cascade-analysis) · [OTel / health bridge](xref:feature-resources-observability-bridge-resources)
- **Narrative:** [Guide ch.6 — operating](xref:guide-operating)
