# Resource Tree Registry
> The hierarchical map of every significant engine resource — self-registering nodes, no orphans, cascade disposal.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Resources](./README.md)

## 🎯 What it solves

Typhon manages dozens of distinct resource kinds — page cache, WAL, transactions, component tables, allocators, bitmaps — each with its own lifecycle. Without a common structure there is no single place to ask "what does the engine currently own?", no consistent teardown ordering, and no stable address space for diagnostics or metrics to hang off of. The Resource Tree Registry gives every tracked resource exactly one parent, one place in a navigable tree, and a guaranteed disposal path.

## ⚙️ How it works (in brief)

`ResourceRegistry` creates a `Root` node plus eight fixed subsystem nodes (`Storage`, `DataEngine`, `Durability`, `Allocation`, `Synchronization`, `Timer` — with a `Timer/Dedicated` sub-node, `Runtime`, `Profiler`) at construction. Any `IResource` — typically a generic `ResourceNode`, sometimes a domain type implementing the interface directly — registers itself with an explicit parent in its constructor; passing `null` throws immediately (`NullReferenceException`, since the constructor dereferences the parent before any explicit check), so there is no orphan bucket to hide bugs in. Resources are addressed by name-only `Id` (type comes from `ResourceType`), giving clean paths like `Root/DataEngine/Player/PrimaryKey`. Disposing a node cascades depth-first to its children (a node can opt out via `DisposeWithParent = false` when something else owns its lifecycle), and `ResourceRegistry.Dispose()` walks subsystems in an explicit dependency order rather than relying on unspecified dictionary enumeration. A `NodeMutated` event fires on every add/remove for live observers such as the Workbench.

## 💻 Usage

```csharp
services.AddResourceRegistry(); // DI: IResourceRegistry singleton, one per process

// or construct directly:
var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "Typhon" });

// subsystem nodes already exist
var storage = registry.Storage;
var durability = registry.GetSubsystem(ResourceSubsystem.Durability);

// any IResource self-registers under an explicit parent
var pageCache = new ResourceNode("PageCache", ResourceType.Cache, registry.Storage);

// or register an externally-constructed IResource under a subsystem
registry.Register(myCustomResource, ResourceSubsystem.Allocation);

// path lookup, both directions
var node = registry.FindByPath("Root/Storage/PageCache");
var path = pageCache.GetPath(); // "Root/Storage/PageCache" (ResourceExtensions)

// react to topology changes
registry.NodeMutated += args =>
{
    if (args.Kind == ResourceMutationKind.Added)
        Console.WriteLine($"{args.ParentId} gained {args.NodeId} ({args.Type})");
};

registry.Dispose(); // cascades subsystem-by-subsystem in dependency order
```

| Option | Default | Effect |
|---|---|---|
| `ResourceRegistryOptions.Name` | `"DefaultResourceRegistry"` | Diagnostic label for the registry instance |

## ⚠️ Guarantees & limits

- One `IResourceRegistry` per process; multiple `DatabaseEngine` instances are siblings under the same registry, not separate trees.
- No orphan container: every `IResource` requires a non-null parent at construction — a null parent faults at the exact call site (`NullReferenceException` today), not on a later traversal.
- Cascade disposal is depth-first; `ResourceRegistry.Dispose()` tears down subsystems in a fixed order (`Profiler` → `Runtime` → `DataEngine` → `Durability` → `Storage` → `Allocation` → `Synchronization` → `Timer` → `Root` as a safety net) because `DataEngine`'s graceful shutdown reads `Storage`/`Durability`/`Synchronization` during its own teardown.
- `Children` enumeration is a point-in-time snapshot (`ConcurrentDictionary.Values`) — fine for diagnostics, not a substitute for linearizable state.
- `NodeMutated` subscribers must not throw (the registry isolates each handler) and must not mutate the graph from inside the handler — re-entrant raises are not supported.
- Per-node overhead (~150–200 bytes) and registration cost are one-time, by design — the tree targets components and per-type instances (e.g. one node per `ComponentTable<T>`), not per-entity or per-latch granularity; fine-grained primitives aggregate into their owning node instead of getting their own.

## 🧪 Tests

- [ResourceTreeTests](../../../test/Typhon.Engine.Tests/Resources/ResourceTreeTests.cs) — subsystem structure, registration, ancestors/descendants, path lookup (`GetPath`/`FindByPath`), thread-safe concurrent registration
- [ResourceRegistryDisposeOrderTests](../../../test/Typhon.Engine.Tests/Resources/ResourceRegistryDisposeOrderTests.cs) — cascade disposal ordering (`DataEngine` tears down before `Storage`/`Durability`/`Synchronization`)

## 🔗 Related

- Sibling: [Resource Tree Mutation Notifications](./resource-tree-mutation-notifications.md) — live add/remove events built on this registry.
- Sibling: [DI Registration & Wiring](./resources-di-wiring.md) — how services attach to this tree via DI.

<!-- Overview: claude/overview/08-resources.md §8.2 -->
<!-- Design: claude/design/Resources/01-registry.md -->
<!-- Design: claude/design/Resources/02-registry-examples.md -->
