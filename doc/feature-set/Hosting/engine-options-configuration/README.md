---
uid: feature-hosting-engine-options-configuration-index
title: 'Engine Options Configuration Surface'
description: 'IOptions-based configuration for every engine service, set with a configure delegate on each Add*() call.'
---

# Engine Options Configuration Surface
> `IOptions<T>`-based configuration for every engine service, set with a `configure` delegate on each `Add*()` call.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Hosting](../README.md)

## 🎯 What it solves

Bootstrapping a `DatabaseEngine` means tuning several independent subsystems — page cache size,
WAL directory and durability knobs, lock timeouts, MVCC cleanup cadence, transient storage, and
background statistics. Hardcoding these in constructors would force every consumer to know the
engine's internal wiring and recompile for any tuning change. The Options Configuration Surface
gives every DI registration a single, idiomatic .NET pattern — a `configure` delegate bound
through `Microsoft.Extensions.Options` — to set these values at startup without touching how the
services are constructed or wired together.

## ⚙️ How it works (in brief)

Each `Add*` extension (`AddDatabaseEngine`, `AddManagedPagedMMF`, `AddPagedMemoryMappedFile`,
`AddMemoryAllocator`, `AddResourceRegistry`) calls `services.AddOptions<TOptions>()` and applies
your `configure` delegate via `.Configure(...)`. The resulting `IOptions<TOptions>` is resolved
inside that service's factory when the container first builds it, so values are effectively frozen
at `BuildServiceProvider()` for singleton registrations. `DatabaseEngineOptions` is a composite: it
groups per-subsystem option types (`Resources`, `Timeouts`, `DeferredCleanup`, `Wal`, `Transient`,
`Statistics`) instead of one flat bag of properties, so you only touch the knobs you need.

## 💻 Usage

Most apps set options through the one-line `AddTyphon` surface — its `Configure*` delegates target
the very option types this page documents:

```csharp
services.AddTyphon(o => o
    .DatabaseFile(@"C:\Data\MyGame\game.typhon")
    .ConfigureStorage(s => s.DatabaseCacheSize = 65536UL * 8192)   // 512 MiB page cache
    .ConfigureEngine(e =>
    {
        e.Resources.MaxActiveTransactions = 2000;
        e.Wal.UseFUA       = false;                                // GroupCommit workload, no per-write FUA
        e.Statistics       = null;                                 // disable background stats worker
    }));

var engine = services.BuildServiceProvider().GetRequiredService<DatabaseEngine>();
```

`AddTyphon` folds those `Configure*` delegates into the **per-`Add*` configuration surface** below —
reach for it directly when you need finer control over service lifetimes, or want to configure or
substitute a single subsystem. `configure` is optional on every `Add*` call; omit it to run on defaults.

```csharp
var services = new ServiceCollection();
services.AddLogging(b => b.AddFilter((_, level) => level >= LogLevel.Warning));

services
    .AddResourceRegistry()
    .AddMemoryAllocator()
    .AddEpochManager()
    .AddHighResolutionSharedTimer()
    .AddDeadlineWatchdog()
    .AddManagedPagedMMF(opts =>
    {
        opts.DatabaseName      = "MyGame";
        opts.DatabaseDirectory = @"C:\Data\MyGame";
        opts.DatabaseCacheSize = 65536UL * 8192;      // 512 MiB page cache
    })
    .AddDatabaseEngine(engineOpts =>
    {
        engineOpts.Resources.MaxActiveTransactions = 2000;
        engineOpts.Wal.UseFUA       = false;          // GroupCommit workload, no per-write FUA
        engineOpts.Statistics       = null;           // disable background stats worker
    });

var provider = services.BuildServiceProvider();
var engine   = provider.GetRequiredService<DatabaseEngine>();
```

| Options type | Registered by | Key knobs |
|---|---|---|
| `DatabaseEngineOptions` | `AddDatabaseEngine` | `Resources`, `Timeouts`, `DeferredCleanup`, `Wal`, `Transient`, `Statistics` |
| `PagedMMFOptions` / `ManagedPagedMMFOptions` | `AddPagedMemoryMappedFile` / `AddManagedPagedMMF` | `DatabaseName`, `DatabaseDirectory`, `DatabaseCacheSize` |
| `MemoryAllocatorOptions` | `AddMemoryAllocator` | `Name` (diagnostics label only) |
| `ResourceRegistryOptions` | `AddResourceRegistry` | `Name` (diagnostics label only) |

## ⚠️ Guarantees & limits

- `configure` is optional on every `Add*` call — omit it to run on defaults (2 MiB page cache,
  the engine's minimum; WAL in `./wal`; `UseFUA = true`; etc.).
- Options are read once, effectively at `BuildServiceProvider()` for singletons — mutating an
  already-resolved `IOptions<T>.Value` afterward has no effect on a running engine.
- Calling the same `Add*()` more than once accumulates `Configure` callbacks (every registered
  delegate runs, in order) rather than replacing the previous one — avoid double-registration.
- Scoped/Transient variants (`AddScopedDatabaseEngine`, `AddTransientManagedPagedMemoryMappedFile`,
  etc.) accept the same `configure` delegate, but mixing lifetimes across the dependency chain
  risks a captive-dependency failure under `BuildServiceProvider(validateScopes: true)` — the
  canonical setup is all-singleton.
- `MemoryAllocatorOptions` and `ResourceRegistryOptions` are configurable but, in practice, only
  expose a diagnostics `Name` today — there is nothing else to tune on them.
- See [Options Validation Hooks](./options-validation-stubs.md) for what configuration mistakes
  are — and are not — caught before they reach the engine.

## 🧪 Tests

- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — `DatabaseEngineOptions.Resources` composite property and its defaults (`DatabaseEngineOptions_HasResourceOptionsProperty` / `_ResourceOptions_HasDefaults`)
- [TransactionChainTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/TransactionChainTests.cs) — `TransactionChainMaxActiveTests` fixture configures `AddScopedDatabaseEngine(options => options.Resources.MaxActiveTransactions = ...)` and proves the `configure` delegate value actually reaches and is enforced by the running engine

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs), [`DatabaseEngine.cs` — `DatabaseEngineOptions`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Ecs/public/DatabaseEngine.cs), [`PagedMMFOptions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs), [`WalWriterOptions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Durability/public/WalWriterOptions.cs)
- Sub-features: [Options Validation Hooks](./options-validation-stubs.md)
- Sibling: [DI Engine Bootstrap Chain](../di-bootstrap-chain/README.md) — the `Add*()` calls this surface's `configure` delegates bind options onto.

<!-- Deep dive: claude/design/Hosting/di-extensions.md — Options Reference (Quick), claude/overview/README.md -->
