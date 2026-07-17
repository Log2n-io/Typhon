---
uid: concept-deadlines-timeouts
title: 'Deadlines & timeouts'
description: 'Every operation runs under an absolute, monotonic deadline threaded through the context. When it expires, a hang becomes a typed timeout exception instead of an unbounded wait.'
---

# Deadlines & timeouts

> **In one line:** every operation carries an **absolute deadline**; when it passes, a would-be hang becomes a *typed exception* — Typhon never blocks forever.

A deadline is a monotonic point in time, not a duration — set once, it propagates through the whole [transaction](xref:concept-transaction) / [unit-of-work](xref:concept-unit-of-work) context and every blocking primitive it touches (lock acquisition, WAL backpressure, page-cache backpressure). Cooperative cancellation checks it at each wait point, so a contended lock or a saturated WAL surfaces as `LockTimeoutException` / `WalBackPressureTimeoutException` / `PageCacheBackpressureTimeoutException` — all under the `TyphonTimeoutException` family — rather than an indefinite stall. Timeouts are the canonical **transient** failure: [`IsTransient`](xref:concept-errors) is true, so a retry loop can back off and try again.

You set the budget through options (`TimeoutOptions`, per-transaction deadlines); the engine does the propagation. The point is predictability: in a real-time server, a bounded failure you can catch and retry beats an unbounded wait that blows your [tick](xref:concept-tick) budget.

## How it relates

- **[Errors & failures](xref:concept-errors)** — timeout exceptions are a family under `TyphonException`, all `IsTransient`.
- **[Transaction](xref:concept-transaction)** / **[Unit of Work](xref:concept-unit-of-work)** — carry the deadline; every wait they make respects it.
- **[Tick](xref:concept-tick)** — why deadlines matter: a stall would overrun the tick budget.

## In the API

- [`TimeoutOptions`](xref:Typhon.Engine.TimeoutOptions) — configures the default deadlines / budgets.
- [`TransactionTimeoutException`](xref:Typhon.Engine.TransactionTimeoutException) / [`LockTimeoutException`](xref:Typhon.Engine.LockTimeoutException) / [`WalBackPressureTimeoutException`](xref:Typhon.Engine.WalBackPressureTimeoutException) / [`PageCacheBackpressureTimeoutException`](xref:Typhon.Engine.PageCacheBackpressureTimeoutException) — the typed timeouts, all under [`TyphonTimeoutException`](xref:Typhon.Engine.TyphonTimeoutException).

## Learn & use

- **Feature detail:** [Deadline & timeout propagation](xref:feature-foundation-deadline-timeout-propagation) · [deadline & cooperative cancellation](xref:feature-transactions-deadline-cancellation) · [timeout exceptions](xref:feature-errors-timeout-exceptions-deadlines)
- **Narrative:** [Guide ch.6 — operating](xref:guide-operating)
