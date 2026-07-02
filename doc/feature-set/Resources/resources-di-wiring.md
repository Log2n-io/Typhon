# DI Registration & Wiring
> Register Typhon services into `IServiceCollection` and have each one self-attach to the resource graph.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Resources](./README.md)

## 🎯 What it solves

Every managed Typhon service (storage, durability, allocation, synchronization, timers) needs a place in the resource graph to report metrics, budgets, and exhaustion state. Wiring this by hand — constructing each service, finding its parent subsystem node, registering it — is repetitive and easy to get wrong (wrong parent, missed registration, wrong lifetime). The DI extensions fold registry attachment into the same call that registers the service with `Microsoft.Extensions.DependencyInjection`, so a host gets a fully wired resource tree from `IServiceCollection` calls alone.

## ⚙️ How it works (in brief)

`AddResourceRegistry` (plus `AddScopedResourceRegistry` / `AddTransientResourceRegistry`) registers `IResourceRegistry` → `ResourceRegistry`, binding an optional `ResourceRegistryOptions` configure callback through `IOptions<T>`. Sibling extensions — `AddMemoryAllocator`, `AddManagedPagedMMF`, `AddEpochManager`, `AddHighResolutionSharedTimer`, `AddDeadlineWatchdog`, `AddDatabaseEngine` — each resolve `IResourceRegistry` inside their factory and pass the matching subsystem node (`rr.Storage`, `rr.Synchronization`, `rr.Timer`, …) into the constructed service, so registration with the graph happens as a side effect of normal DI resolution. Most extensions have `Add…` (singleton), `AddScoped…`, and `AddTransient…` variants for tests and short-lived hosts. Call order among the `Add…` calls themselves doesn't matter — standard `IServiceCollection` resolution is lazy, keyed by type, at `BuildServiceProvider` / first resolve, not at `Add…` time — but every dependency a service needs (`AddDatabaseEngine` needs the allocator, watchdog, epoch manager, and `ManagedPagedMMF`) must have been added to the collection by then, or the first resolve throws.

## 💻 Usage

```csharp
var services = new ServiceCollection();

services
    .AddResourceRegistry(opt => opt.Name = "MyHost")        // IResourceRegistry singleton
    .AddMemoryAllocator()                                    // attaches under registry.Allocation
    .AddHighResolutionSharedTimer()                          // attaches under registry.Timer
    .AddEpochManager()                                       // attaches under registry.Synchronization
    .AddDeadlineWatchdog()                                   // uses the shared timer + registry
    .AddManagedPagedMMF(opt => opt.DatabaseName = "db")      // attaches under registry.Storage
    .AddDatabaseEngine();                                    // attaches under registry.DataEngine

var sp = services.BuildServiceProvider();
var registry = sp.GetRequiredService<IResourceRegistry>();
var engine = sp.GetRequiredService<DatabaseEngine>();
```

| Option | Default | Effect |
|---|---|---|
| `AddResourceRegistry` / `AddScopedResourceRegistry` / `AddTransientResourceRegistry` | n/a | Pick the `IResourceRegistry` lifetime; singleton is the production case |
| `ResourceRegistryOptions.Name` | `"DefaultResourceRegistry"` | Diagnostic label for the registry instance |
| `AddManagedPagedMMF` / `AddScopedManagedPagedMemoryMappedFile` / `AddTransientManagedPagedMemoryMappedFile` | n/a | Pick the `ManagedPagedMMF` lifetime |
| `AddEpochManager` / `AddScopedEpochManager` / `AddTransientEpochManager` | n/a | Pick the `EpochManager` lifetime |
| `AddDatabaseEngine` / `AddScopedDatabaseEngine` / `AddTransientDatabaseEngine` | n/a | Pick the `DatabaseEngine` lifetime |

## ⚠️ Guarantees & limits

- All extensions are additive `IServiceCollection` calls returning `this`, so they chain fluently and compose with any other DI setup in the host.
- Each extension resolves `IResourceRegistry` (and its other dependencies) lazily inside its factory delegate — nothing is constructed at `Add…` time, only at `BuildServiceProvider`/resolve time. Forgetting `AddResourceRegistry` (or a required dependency like the epoch manager or shared timer) surfaces as a standard DI resolution failure, not a silent gap in the resource tree.
- Resource-graph attachment is not optional or toggleable per service: any service built through these extensions always registers under its subsystem node. There is no "construct without registering" escape hatch via DI — use the service's constructor directly if a tree-less instance is needed (e.g. tests that pass an explicit `IResourceRegistry` mock).
- `AddOptions<T>().Validate(...)` hooks exist on these extensions but are currently placeholders (`// TODO Add validation logic`) — invalid option values are not yet rejected at registration time.
- Options validation/binding follows standard `Microsoft.Extensions.Options` semantics (`IOptions<T>` resolved once per factory invocation); nothing Typhon-specific changes that contract.

## 🔗 Related

- Sibling: [Resource Tree Registry](./resource-tree-registry.md) — the tree these DI extensions attach services into.
- Sibling: [Resource Budget Configuration](./resource-budgets-options.md) — options object threaded into constructed services via these extensions.
- Source: [src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs](../../../src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs)
- Source: [src/Typhon.Engine/Resources/public/ResourceRegistry.cs](../../../src/Typhon.Engine/Resources/public/ResourceRegistry.cs)

<!-- Design: claude/design/Resources/10-di-and-public-surface.md -->
