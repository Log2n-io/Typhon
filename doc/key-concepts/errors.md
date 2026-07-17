---
uid: concept-errors
title: 'Errors & failures'
description: 'One exception hierarchy with three catch granularities, numeric error codes grouped by subsystem, an IsTransient retry hint, and a zero-allocation Result<TValue,TStatus> for hot paths.'
---

# Errors & failures

> **In one line:** one exception hierarchy you can catch at three granularities, each error carrying a code and an `IsTransient` hint — plus a zero-allocation `Result<T>` for the hot path.

Every engine failure derives from `TyphonException`, so you can catch broadly (`TyphonException`), by family (`TyphonTimeoutException`, `DurabilityException`, `StorageException`, `SchemaValidationException`, …), or by exact type (`LockTimeoutException`, `UniqueConstraintViolationException`, `PageCorruptionException`, …). Each carries a numeric `TyphonErrorCode` grouped into per-subsystem ranges (1xxx transactions, 6xxx resources, …) and a virtual **`IsTransient`** flag hinting whether a retry could succeed — a lock timeout is transient, a schema mismatch is not. That single flag is what a generic retry loop keys on.

On hot paths where throwing would be too costly, the engine returns a `Result<TValue, TStatus>` instead — a zero-allocation struct pairing a value with a per-subsystem status enum, so an *expected* "not found" or "would block" never pays exception cost. Exceptions are for the exceptional; `Result<T>` is for the routine miss.

## How it relates

- **[Deadlines & timeouts](xref:concept-deadlines-timeouts)** — the most common transient failures; they surface as `TyphonTimeoutException` subclasses.
- **[Transaction](xref:concept-transaction)** — a failed commit surfaces here; `IsTransient` tells you whether to retry.
- **[Conflict resolution](xref:concept-conflict-resolution)** — the alternative to failing on a write-write race: reconcile instead of throw.
- **[Observability & telemetry](xref:concept-observability)** — failures worth acting on also show up as health and metric signals.
- **[Resources & budgets](xref:concept-resources)** — `ResourceExhaustedException` is the wall; the resource graph is how you avoid reaching it.

## In the API

- [`TyphonException`](xref:Typhon.Engine.TyphonException) — the base; [`TyphonTimeoutException`](xref:Typhon.Engine.TyphonTimeoutException) / [`DurabilityException`](xref:Typhon.Engine.DurabilityException) / [`StorageException`](xref:Typhon.Engine.StorageException) / [`SchemaValidationException`](xref:Typhon.Engine.SchemaValidationException) — the family bases.
- [`TyphonErrorCode`](xref:Typhon.Engine.TyphonErrorCode) — the numeric code enum, ranged by subsystem.
- [`Result<TValue, TStatus>`](xref:Typhon.Engine.Result`2) — the hot-path error-or-value struct.

## Learn & use

- **Feature detail:** [Error handling](xref:feature-errors-index) — [exception hierarchy](xref:feature-errors-exception-hierarchy) · [error codes](xref:feature-errors-error-codes) · [IsTransient hint](xref:feature-errors-transience-hint) · [Result type](xref:feature-errors-result-type) · [resource exhaustion](xref:feature-errors-resource-exhaustion-handling)
- **Narrative:** [Guide ch.6 — operating](xref:guide-operating)
