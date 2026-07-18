---
uid: feature-hosting-index
title: 'Hosting'
description: 'A thin Microsoft.Extensions.DependencyInjection integration seam: extension methods that register the engine''s top-level singletons (resource registry,…'
---

# Hosting
> A thin `Microsoft.Extensions.DependencyInjection` integration seam: extension methods that register the engine's top-level singletons (resource registry, allocator, epoch manager, timer, watchdog, paged MMF, database engine) and bind their option types via `IOptions<T>`, giving an application a single canonical chain to bootstrap a working `DatabaseEngine`. Singleton/Scoped/Transient twins let test and tooling hosts scope an engine instance per DI scope, an injectable `IWalFileIO` seam swaps the WAL's disk backend for an in-memory one, and small conveniences (`AddTyphonProfiler`, `EnsureFileDeleted`) round out startup/teardown for tests and tooling.

> 🔬 **Recommended:** Hosting doesn't have its own in-depth-overview chapter — read [in-depth-overview/01-foundation.md §9](../../in-depth-overview/01-foundation.md) (Chapter 01: Foundation, alongside the primitives it wires up) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [DI Engine Bootstrap Chain](di-bootstrap-chain/README.md) | `Add*()` extension methods on `IServiceCollection` that register and wire the engine's top-level singletons into a working `DatabaseEngine` | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Singleton/Scoped/Transient Lifetime Variants](di-bootstrap-chain/lifetime-variants.md) | Every `Add*` with a lifetime choice ships as `Add.../AddScoped.../AddTransient...` twins sharing one factory delegate | ✅ Implemented | 🔵 Core |
| [Engine Options Configuration Surface](engine-options-configuration/README.md) | `IOptions<T>`-based configuration for engine services, set via configure delegates on each `Add*()` DI call | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Options Validation Hooks](engine-options-configuration/options-validation-stubs.md) | `AddOptions<T>().Validate(...)` is wired on every options type but its predicate is a no-op stub today; `ResourceOptions.Validate()` / `PagedMMFOptions.IsValid` are the real, directly-callable fail-fast checks | 🚧 Partial | 🟣 Advanced |
| [Database Seeding](database-seeding.md) | `TyphonOptions.Seed(revision, tx => …)` registers revision-stepped, crash-safe seed steps applied automatically at engine open — a fresh database runs them all, an existing one catches up on new ones; `DatabaseEngine.IsNewlyCreated` is the low-level primitive | ✅ Implemented | 🟢 Start Here |
| [Clean-Slate Database File Deletion](ensure-file-deleted.md) | `EnsureFileDeleted<TO>(IServiceProvider)` resolves `IOptions<TO>` in a fresh scope and deletes the backing database file it points at — the supported way to start test/tooling runs from a clean slate | ✅ Implemented | 🔵 Core |
| [Profiler Launch Override Hook](profiler-launch-override-hook.md) | `AddTyphonProfiler(Func<ProfilerLaunchConfig,ProfilerLaunchConfig>)` lets a host adjust the profiler launch config (resolved from `typhon.telemetry.json` + env) in code, e.g. to layer CLI args on top; zero-code default self-wiring is unaffected if not called | ✅ Implemented | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Pluggable WAL I/O Backend (IWalFileIO seam)](wal-io-injection-seam.md) | `AddDatabaseEngine` resolves an optional internal `IWalFileIO` from the container so tests/benchmarks can register `InMemoryWalFileIO` and run the full WAL + checkpoint pipeline with zero disk I/O; production falls back to the disk-backed `WalFileIO` — the interface and both implementations are `internal`, not reachable from application code | ✅ Implemented |