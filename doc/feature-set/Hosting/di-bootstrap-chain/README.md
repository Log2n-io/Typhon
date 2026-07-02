# DI Engine Bootstrap Chain
> One `using`, one fluent chain of `Add*` calls — wires the engine's singletons into your DI container.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟢 Start Here · **Category:** [Hosting](../README.md)

## 🎯 What it solves

A `DatabaseEngine` needs a page cache, a memory allocator, an epoch manager, a deadline watchdog
and a shared timer all constructed and cross-wired correctly before it can be used — get the
wiring or the disposal order wrong and you leak native memory-mapped-file handles or resource-tree
nodes. `ServiceCollectionExtensions` packages that wiring as ordinary
`Microsoft.Extensions.DependencyInjection` extension methods, so a host application stands up the
engine the same way it stands up any other ASP.NET Core or generic-host service: register it once,
resolve `DatabaseEngine` from the container.

## ⚙️ How it works (in brief)

Each `Add*` method is a thin factory registration: it resolves its own dependencies from
`IServiceProvider` (e.g. `AddManagedPagedMMF` needs `IMemoryAllocator`, `EpochManager` and
`IResourceRegistry` already registered) and binds an options type via `IOptions<T>` if one exists.
Call order doesn't matter — registration is lazy, dependencies are only resolved when something
asks the container for `DatabaseEngine`. Most extensions have three lifetime variants (see the
[Singleton/Scoped/Transient Lifetime Variants](./lifetime-variants.md) sub-feature); the canonical
setup below is all-singleton.

## 💻 Usage

```csharp
using Typhon.Engine;

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
        opts.DatabaseName      = "MyApp";
        opts.DatabaseDirectory = @"C:\data\myapp";
        opts.DatabaseCacheSize = 65536UL * 8192;   // 512 MiB page cache
    })
    .AddDatabaseEngine(engineOpts =>
    {
        engineOpts.Wal = new WalWriterOptions
        {
            WalDirectory = @"C:\data\myapp\wal",
            UseFUA       = false,
        };
    });

var serviceProvider = services.BuildServiceProvider();
var engine = serviceProvider.GetRequiredService<DatabaseEngine>();
```

| Extension | Registers | Options type |
|---|---|---|
| `AddResourceRegistry` | `IResourceRegistry` | `ResourceRegistryOptions` |
| `AddMemoryAllocator` | `IMemoryAllocator` | `MemoryAllocatorOptions` |
| `AddEpochManager` | `EpochManager` | — |
| `AddHighResolutionSharedTimer` | `HighResolutionSharedTimerService` | — |
| `AddDeadlineWatchdog` | `DeadlineWatchdog` | — |
| `AddManagedPagedMMF` | `ManagedPagedMMF` | `ManagedPagedMMFOptions` |
| `AddPagedMemoryMappedFile` | `PagedMMF` | `PagedMMFOptions` |
| `AddDatabaseEngine` | `DatabaseEngine` | `DatabaseEngineOptions` |

## ⚠️ Guarantees & limits

- Call order is irrelevant — the dependency graph is enforced at resolution time, not registration
  time. Omit a required `Add*` and `GetRequiredService<DatabaseEngine>()` throws at the first
  missing dependency, not at startup.
- Options validation is wired but unimplemented (`Validate(_ => true)` stubs) — validate your own
  option values before passing the `configure` delegate if correctness matters.
- `EnsureFileDeleted<TO>(provider)` is a test/tooling convenience that opens a scope, resolves
  `IOptions<TO>`, and deletes the backing database file — not part of normal application startup.
- One `IResourceRegistry` per process is the intended topology; multiple `DatabaseEngine`
  instances are siblings under the same registry, not separate trees.
- See [Lifetime Variants](./lifetime-variants.md) for what's safe to mix when you move off the
  all-singleton default.

## 🧪 Tests

- [DatabaseFileLockingTests](../../../../test/Typhon.Engine.Tests/Storage/DatabaseFileLockingTests.cs) — builds the full `Add*` chain by hand (outside `TestBase`) and exercises both the happy path and wiring-adjacent failure modes (stale/live/cross-machine lock, corrupt lock file)

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](../../../../src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs)
- Reference consumer: `tools/Typhon.Workbench/Sessions/EngineLifecycle.cs`
- Related feature: [Pluggable WAL I/O Backend](../wal-io-injection-seam.md)
- Sub-features: [Singleton/Scoped/Transient Lifetime Variants](./lifetime-variants.md)

<!-- Deep dive: claude/design/Hosting/di-extensions.md -->
