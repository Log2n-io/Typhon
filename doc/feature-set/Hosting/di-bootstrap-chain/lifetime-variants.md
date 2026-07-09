---
uid: feature-hosting-di-bootstrap-chain-lifetime-variants
title: 'Singleton/Scoped/Transient Lifetime Variants'
description: 'Every Add* that has a lifetime choice ships as Add..., AddScoped..., and AddTransient... twins.'
---

# Singleton/Scoped/Transient Lifetime Variants
> Every `Add*` that has a lifetime choice ships as `Add...`, `AddScoped...`, and `AddTransient...` twins.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [DI Engine Bootstrap Chain](./README.md)

## 🎯 What it solves

The canonical setup is all-singleton, but not every host wants that. Test fixtures want one
isolated `DatabaseEngine` (own page cache, own WAL backend) per test while still sharing the
process-wide epoch manager and resource registry. Tooling that probes options wants a disposable,
one-shot instance. Rather than force a single topology, each engine service that has a meaningful
lifetime choice is registered through three parallel extension methods that differ only in the
`ServiceDescriptor` lifetime they produce.

## ⚙️ How it works (in brief)

`Add<X>()`, `AddScoped<X>()`, and `AddTransient<X>()` share the same factory delegate — they
resolve the same dependencies from `IServiceProvider` and bind the same options type — only the
registered `ServiceLifetime` differs. This is offered for `IResourceRegistry`,
`IMemoryAllocator`, `EpochManager`, `PagedMMF`/`ManagedPagedMMF`, and `DatabaseEngine`.
`AddHighResolutionSharedTimer` and `AddDeadlineWatchdog` have no scoped/transient twin — they're
singleton-only, because each owns (or multiplexes callbacks on) a single background thread that's
meant to be shared process-wide, not duplicated per scope.

The standard DI lifetime rule applies: a service can only depend on services of an equal or longer
lifetime. A **singleton** `DatabaseEngine` requires every upstream dependency to also be singleton;
a **scoped** one can sit on singleton dependencies — the common test pattern (share
`IResourceRegistry`/`EpochManager`/timers process-wide, scope `ManagedPagedMMF`/`DatabaseEngine`).

## 💻 Usage

```csharp
// TestBase's pattern: shared process-wide primitives, one scoped engine per test provider.
services
    .AddResourceRegistry()
    .AddMemoryAllocator()
    .AddEpochManager()
    .AddHighResolutionSharedTimer()
    .AddDeadlineWatchdog()
    .AddScopedManagedPagedMemoryMappedFile(opts =>
    {
        opts.DatabaseName      = "Fixture1";
        opts.DatabaseDirectory = tempDir;
        opts.DatabaseCacheSize = 65536UL * 512;
    })
    .AddScopedDatabaseEngine(engineOpts => { /* ... */ });

services.AddScoped<IWalFileIO>(_ => new InMemoryWalFileIO());   // scoped: fresh backend per scope

var provider = services.BuildServiceProvider();

using (var scope = provider.CreateScope())
{
    var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
    // ... use engine ...
}   // scope disposal tears down the page cache + WAL backend

// Reopen the SAME database (crash/recovery tests): create the next scope only after this one is
// disposed — see the IOptions<T> caveat below.
using (var scope2 = provider.CreateScope())
{
    var engine2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
}
```

| Extension family | Lifetimes available |
|---|---|
| `AddResourceRegistry` / `AddScopedResourceRegistry` / `AddTransientResourceRegistry` | Singleton, Scoped, Transient |
| `AddMemoryAllocator` / `AddScopedMemoryAllocator` / `AddTransientMemoryAllocator` | Singleton, Scoped, Transient |
| `AddEpochManager` / `AddScopedEpochManager` / `AddTransientEpochManager` | Singleton, Scoped, Transient |
| `AddPagedMemoryMappedFile` / `AddScopedPagedMemoryMappedFile` / `AddTransientPagedMemoryMappedFile` | Singleton, Scoped, Transient |
| `AddManagedPagedMMF` / `AddScopedManagedPagedMemoryMappedFile` / `AddTransientManagedPagedMemoryMappedFile` | Singleton, Scoped, Transient |
| `AddDatabaseEngine` / `AddScopedDatabaseEngine` / `AddTransientDatabaseEngine` | Singleton, Scoped, Transient |
| `AddHighResolutionSharedTimer` | Singleton only |
| `AddDeadlineWatchdog` | Singleton only |

## ⚠️ Guarantees & limits

- **Lifetime direction is enforced by the container, not the engine.** Registering a singleton
  `DatabaseEngine` on top of a scoped `ManagedPagedMMF` throws at
  `BuildServiceProvider(validateScopes: true)` (or on first resolution without that flag) — a
  standard DI captive-dependency failure, not an engine-specific check.
- **Scoped is the supported test-isolation pattern — one live scope at a time per provider.** A
  scope's `DatabaseEngine` gets its own page cache and (if `IWalFileIO` is also scoped) its own
  in-memory WAL backend; disposing the scope tears it down without touching the shared singletons
  below it. Two *concurrently live* scopes from the same provider are not independent: `IOptions<T>`
  is resolved once and cached for the provider's lifetime, so both would target the same configured
  database path.
- **Transient constructs a brand-new native-backed instance on every resolution** — for
  `ManagedPagedMMF`/`DatabaseEngine` a new page cache each time something asks the container.
  Reserve it for one-shot experiments, not services resolved repeatedly.
- **No scoped/transient `HighResolutionSharedTimer` or `DeadlineWatchdog`.** Both are
  process-wide by design; every scope that needs a watchdog shares the one singleton instance.

## 🧪 Tests

- [DatabaseFileLockingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/DatabaseFileLockingTests.cs) — builds a manual Scoped chain (`AddScopedManagedPagedMemoryMappedFile` + `AddScopedDatabaseEngine`) outside `TestBase`, then verifies scope-disposal tears the engine (and its lock file) down

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs)
- Reference consumer: `test/Typhon.Engine.Tests/TestBase.cs`
- Related feature: [Pluggable WAL I/O Backend](../wal-io-injection-seam.md)
- Parent feature: [DI Engine Bootstrap Chain](./README.md)

<!-- Deep dive: claude/design/Hosting/di-extensions.md -->
