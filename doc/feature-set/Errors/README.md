---
uid: feature-errors-index
title: 'Errors'
description: 'Typhon''s unified error model: a single-rooted exception hierarchy with numeric error codes and an IsTransient retry hint, finite per-subsystem timeouts that…'
---

# Errors
> Typhon's unified error model: a single-rooted exception hierarchy with numeric error codes and an `IsTransient` retry hint, finite per-subsystem timeouts that replace infinite hangs, `ExhaustionPolicy`-driven bounded-resource failures, and a DEBUG-only declared-access validator. For expected non-error outcomes on hot paths, a zero-allocation `Result<TValue,TStatus>` struct replaces exceptions entirely — retrying is always the caller's decision, the engine never retries on its own.

> 🔬 **Recommended:** read [in-depth-overview/14-errors.md](../../in-depth-overview/14-errors.md) (Chapter 14: Errors) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [TyphonException Hierarchy & Catalog](exception-hierarchy.md) | Base `TyphonException` (`ErrorCode` + virtual `IsTransient`) rooted hierarchy giving callers three catch granularities, with a living catalog of every concrete exception type and a caller-owns-retry philosophy | ✅ Implemented | 🔵 Core |
| [IsTransient Retry Hint](transience-hint.md) | A virtual `IsTransient` flag on every `TyphonException` hinting whether a retry might succeed, without the engine ever retrying on the caller's behalf | ✅ Implemented | 🔵 Core |
| [Timeout Exceptions & Deadline Propagation](timeout-exceptions-deadlines.md) | Configurable, finite deadlines (`TimeoutOptions`, `WaitContext`) replace infinite-wait lock primitives, converting hangs into typed `TyphonTimeoutException` subclasses plus a bulk-load checkpoint timeout | ✅ Implemented | 🔵 Core |
| [Error Code Classification](error-codes.md) | A numeric `TyphonErrorCode` per failure, grouped into subsystem ranges (1xxx-8xxx) for logging/metrics dashboards — the exception type, not the code, is what callers catch on | ✅ Implemented | 🟣 Advanced |
| [Resource Exhaustion Handling](resource-exhaustion-handling.md) | `ExhaustionPolicy` metadata (FailFast/Wait/Evict/Degrade) documents how the engine reacts when a bounded resource is full — FailFast throws a structured `ResourceExhaustedException` (`IsTransient=true`), Wait resources throw a bounded `TyphonTimeoutException` subclass, never an unbounded hang or `InvalidOperationException` | ✅ Implemented | 🟣 Advanced |
| [Storage & Corruption Exceptions](storage-corruption-exceptions.md) | Typed failures for storage I/O, CRC32C page corruption (unhealable), and another-process database-file-lock detection | ✅ Implemented | 🟣 Advanced |
| [Durability (WAL / BulkLoad / Commit) Exceptions](durability-exceptions.md) | Typed, fail-fast failures from the WAL writer, the commit pipeline's durability wait, and BulkLoad session lifecycle | ✅ Implemented | 🟣 Advanced |
| [Schema & Constraint Violation Exceptions](schema-constraint-exceptions.md) | Engine-refuses-to-proceed failures for the data model: breaking schema mismatch, migration failure, revision downgrade, duplicate unique key | ✅ Implemented | 🟣 Advanced |
| [Runtime/Scheduler Declared-Access Validation](runtime-access-validation.md) | DEBUG-only `InvalidAccessException` when a system writes a component it never declared via `Writes<T>()`/`SideWrites<T>()`, compiled out in RELEASE | ✅ Implemented | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Result\<TValue,TStatus\> Hot-Path Result Type](result-type.md) | Zero-allocation dual-generic result struct for hot-path lookups that expect a miss — no exceptions, no boxing, no branch on access; used internally by B+Tree/MVCC hot paths, not surfaced through `Transaction`/`Query` call sites | ✅ Implemented |