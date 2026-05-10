using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Subsystem categories for grouping resources in the registry tree.
/// </summary>
[PublicAPI]
public enum ResourceSubsystem
{
    /// <summary>Storage layer: PageCache, ManagedPagedMMF, segments</summary>
    Storage,

    /// <summary>Data engine: DatabaseEngine, ComponentTables, Transactions</summary>
    DataEngine,

    /// <summary>Durability layer: WAL, Checkpoint (future)</summary>
    Durability,

    /// <summary>Memory allocation: MemoryAllocator, Bitmaps</summary>
    Allocation,

    /// <summary>Synchronization primitives: EpochManager, latch pools</summary>
    Synchronization,

    /// <summary>Timer services: high-resolution shared and dedicated timers</summary>
    Timer,

    /// <summary>Runtime subsystem: DAG scheduler, tick loop, worker pool</summary>
    Runtime,

    /// <summary>Profiler subsystem: Tracy-style capture pipeline (consumer thread, exporters)</summary>
    Profiler
}

/// <summary>
/// Central registry for all managed resources in Typhon.
/// Provides a hierarchical tree structure for lifecycle management and observability.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="IResourceRegistry"/> exists per process. Multiple <see cref="DatabaseEngine"/>
/// instances are siblings under the same registry.
/// </para>
/// <para>
/// <b>Design:</b> No orphan container exists. Resources must have an explicit parent;
/// passing null throws <see cref="ArgumentNullException"/>. This fails fast and surfaces bugs.
/// </para>
/// </remarks>
[PublicAPI]
public interface IResourceRegistry : IDisposable
{
    /// <summary>Root node of the resource tree.</summary>
    IResource Root { get; }

    /// <summary>Storage subsystem node (PageCache, ManagedPagedMMF).</summary>
    IResource Storage { get; }

    /// <summary>Data engine subsystem node (DatabaseEngine, ComponentTables).</summary>
    IResource DataEngine { get; }

    /// <summary>Durability subsystem node (WAL, Checkpoint).</summary>
    IResource Durability { get; }

    /// <summary>Allocation subsystem node (MemoryAllocator, Bitmaps).</summary>
    IResource Allocation { get; }

    /// <summary>Synchronization subsystem node (EpochManager, latch pools).</summary>
    IResource Synchronization { get; }

    /// <summary>Timer subsystem node (high-resolution shared and dedicated timers).</summary>
    IResource Timer { get; }

    /// <summary>Timer/Dedicated sub-node for isolated single-handler timers.</summary>
    IResource TimerDedicated { get; }

    /// <summary>Runtime subsystem node (DAG scheduler, tick loop, worker pool).</summary>
    IResource Runtime { get; }

    /// <summary>Profiler subsystem node (Tracy-style consumer thread + exporters).</summary>
    IResource Profiler { get; }

    /// <summary>
    /// Gets the subsystem node for the specified category.
    /// </summary>
    IResource GetSubsystem(ResourceSubsystem subsystem);

    /// <summary>
    /// Registers a resource under the specified subsystem.
    /// </summary>
    /// <returns>The subsystem node (for fluent chaining or parent assignment).</returns>
    IResource Register<T>(T resource, ResourceSubsystem subsystem) where T : IResource;

    /// <summary>
    /// Finds a resource by its full path from root.
    /// </summary>
    /// <param name="path">Full path (e.g., "Root/DataEngine/DatabaseEngine_abc123/ComponentTable_Player").</param>
    /// <param name="separator">Path separator (default: "/").</param>
    /// <returns>The resource at the path, or null if not found.</returns>
    IResource FindByPath(string path, string separator = "/");

    /// <summary>
    /// Raised whenever a resource is added to or removed from the graph. Subscribers must not throw (the registry isolates faulty handlers but fault-tolerance
    /// is best-effort) and must not mutate the graph from within the handler (would re-enter and cause recursive raise).
    /// </summary>
    event Action<ResourceMutationEventArgs> NodeMutated;
}