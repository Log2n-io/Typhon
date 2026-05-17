using JetBrains.Annotations;
using System;
using System.Linq;

namespace Typhon.Engine;

/// <summary>
/// Configuration options for <see cref="ResourceRegistry"/>.
/// </summary>
[PublicAPI]
public sealed class ResourceRegistryOptions
{
    /// <summary>Name for this registry instance (for diagnostics).</summary>
    public string Name { get; set; }
}

/// <summary>
/// Default implementation of <see cref="IResourceRegistry"/>.
/// Creates a hierarchical tree with four subsystem nodes under Root.
/// </summary>
/// <remarks>
/// Tree structure:
/// <code>
/// Root
/// ├── Storage/          (PageCache, ManagedPagedMMF)
/// ├── DataEngine/       (DatabaseEngine, ComponentTables)
/// ├── Durability/       (WAL, Checkpoint)
/// ├── Allocation/       (MemoryAllocator, Bitmaps)
/// ├── Synchronization/  (EpochManager, latch pools)
/// └── Timer/            (HighResolutionSharedTimerService)
///     └── Dedicated/    (HighResolutionTimerService instances)
/// </code>
/// </remarks>
[PublicAPI]
public class ResourceRegistry : IResourceRegistry
{
    /// <summary>Name of this registry instance.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public IResource Root { get; }

    /// <inheritdoc />
    public IResource Storage { get; }

    /// <inheritdoc />
    public IResource DataEngine { get; }

    /// <inheritdoc />
    public IResource Durability { get; }

    /// <inheritdoc />
    public IResource Allocation { get; }

    /// <inheritdoc />
    public IResource Synchronization { get; }

    /// <inheritdoc />
    public IResource Timer { get; }

    /// <inheritdoc />
    public IResource TimerDedicated { get; }

    /// <inheritdoc />
    public IResource Runtime { get; }

    /// <inheritdoc />
    public IResource Profiler { get; }

    /// <summary>
    /// Creates a new resource registry with the standard subsystem tree.
    /// </summary>
    public ResourceRegistry(ResourceRegistryOptions options)
    {
        Name = options?.Name ?? "DefaultResourceRegistry";

        // Create root node
        Root = ResourceNode.CreateRoot(this);

        // Create subsystem nodes under root (self-register via ResourceNode constructor)
        Storage = new ResourceNode("Storage", ResourceType.Node, Root);
        DataEngine = new ResourceNode("DataEngine", ResourceType.Node, Root);
        Durability = new ResourceNode("Durability", ResourceType.Node, Root);
        Allocation = new ResourceNode("Allocation", ResourceType.Node, Root);
        Synchronization = new ResourceNode("Synchronization", ResourceType.Node, Root);

        // Timer subsystem
        Timer = new ResourceNode("Timer", ResourceType.Node, Root);
        TimerDedicated = new ResourceNode("Dedicated", ResourceType.Node, Timer);

        // Runtime subsystem
        Runtime = new ResourceNode("Runtime", ResourceType.Node, Root);

        // Profiler subsystem (Tracy-style capture pipeline — #243)
        Profiler = new ResourceNode("Profiler", ResourceType.Node, Root);
    }

    /// <inheritdoc />
    public IResource GetSubsystem(ResourceSubsystem subsystem) => subsystem switch
    {
        ResourceSubsystem.Storage => Storage,
        ResourceSubsystem.DataEngine => DataEngine,
        ResourceSubsystem.Durability => Durability,
        ResourceSubsystem.Allocation => Allocation,
        ResourceSubsystem.Synchronization => Synchronization,
        ResourceSubsystem.Timer => Timer,
        ResourceSubsystem.Runtime => Runtime,
        ResourceSubsystem.Profiler => Profiler,
        _ => throw new ArgumentOutOfRangeException(nameof(subsystem), subsystem, "Unknown subsystem")
    };

    /// <inheritdoc />
    public IResource Register<T>(T resource, ResourceSubsystem subsystem) where T : IResource
    {
        var parent = GetSubsystem(subsystem);
        parent.RegisterChild(resource);
        return parent;
    }

    /// <inheritdoc />
    public event Action<ResourceMutationEventArgs> NodeMutated;

    /// <summary>
    /// Fires <see cref="NodeMutated"/>, invoking each subscriber inside its own try/catch so one
    /// faulty handler doesn't break the rest. Intentionally internal — only <see cref="ResourceNode"/>
    /// should raise mutations.
    /// </summary>
    internal void RaiseMutation(ResourceMutationEventArgs args)
    {
        var handlers = NodeMutated;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ResourceMutationEventArgs>)handler).Invoke(args);
            }
            catch
            {
                // Subscriber contract: handlers must not throw. Swallow to keep other subscribers alive.
            }
        }
    }

    /// <inheritdoc />
    public IResource FindByPath(string path, string separator = "/")
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var parts = path.Split([separator], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        // Path must start with Root
        if (parts[0] != Root.Id)
        {
            return null;
        }

        // Navigate from root
        return Root.FindByPath(string.Join(separator, parts.Skip(1)), separator);
    }

    /// <summary>
    /// Disposes all resources in the tree, subsystem by subsystem, in reverse-dependency order.
    /// </summary>
    /// <remarks>
    /// Teardown order is load-bearing. <see cref="DataEngine"/>'s graceful shutdown runs a final checkpoint plus
    /// <c>PersistArchetypeState</c> / <c>PersistEngineState</c> (durability rule CX-06) that read the <see cref="Storage"/>
    /// (ManagedPagedMMF), <see cref="Durability"/> (WAL) and <see cref="Synchronization"/> (EpochManager) subsystems — so
    /// those must outlive it. A bare <c>Root.Dispose()</c> cascades children in <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
    /// enumeration order, which is unspecified — when Storage happened to enumerate before DataEngine the MMF was torn down
    /// under the still-running engine teardown, faulting with a null page table / uninitialized segment. So dispose
    /// subsystems explicitly here, dependents first; the trailing <c>Root.Dispose()</c> is a safety net for the root and
    /// any subsystem not listed (already-disposed nodes are idempotent no-ops).
    /// </remarks>
    public void Dispose()
    {
        Profiler.Dispose();          // observers of the engine — stop capturing first
        Runtime.Dispose();           // scheduler — must stop driving ticks before the engine tears down
        DataEngine.Dispose();        // engine graceful shutdown — reads Storage / Durability / Synchronization below
        Durability.Dispose();        // WAL / checkpoint
        Storage.Dispose();           // page cache / ManagedPagedMMF
        Allocation.Dispose();        // leaf primitives everything above depends on — last
        Synchronization.Dispose();
        Timer.Dispose();
        Root.Dispose();              // safety net: root + anything not enumerated above
        GC.SuppressFinalize(this);
    }
}