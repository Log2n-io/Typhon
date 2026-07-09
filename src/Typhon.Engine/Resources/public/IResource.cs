using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Classification of a resource-graph node, used for filtering (e.g. by <see cref="ResourceSnapshot.FindByType"/>) and for display.
/// Values are grouped by engine layer (structural, service, transaction, storage, persistence, metadata, synchronization, durability).
/// </summary>
[PublicAPI]
public enum ResourceType
{
    // ═══════════════════════════════════════════════════════════════
    // STRUCTURAL TYPES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>No specific type assigned</summary>
    None = 0,

    /// <summary>Generic grouping node for hierarchy organization</summary>
    Node = 1,

    // ═══════════════════════════════════════════════════════════════
    // SERVICE LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Top-level singleton services (MemoryAllocator, etc.)</summary>
    Service = 10,

    /// <summary>The main database engine instance</summary>
    Engine = 11,

    // ═══════════════════════════════════════════════════════════════
    // TRANSACTION LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Transaction pool/chain managing active transactions</summary>
    TransactionPool = 20,

    /// <summary>Individual active transaction</summary>
    Transaction = 21,

    /// <summary>Pending changes within a transaction</summary>
    ChangeSet = 22,

    // ═══════════════════════════════════════════════════════════════
    // STORAGE LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Per-component-type storage table</summary>
    ComponentTable = 30,

    /// <summary>Logical or chunk-based segment</summary>
    Segment = 31,

    /// <summary>B+Tree index structure</summary>
    Index = 32,

    /// <summary>Page cache subsystem</summary>
    Cache = 33,

    // ═══════════════════════════════════════════════════════════════
    // PERSISTENCE LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Memory-mapped file or file handle</summary>
    File = 40,

    /// <summary>Memory block (pinned or array-backed)</summary>
    Memory = 41,

    /// <summary>Hierarchical bitmap for allocation tracking</summary>
    Bitmap = 42,

    // ═══════════════════════════════════════════════════════════════
    // METADATA LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Schema definitions and metadata</summary>
    Schema = 50,

    // ═══════════════════════════════════════════════════════════════
    // UTILITY TYPES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Block allocator for fixed-size allocations</summary>
    Allocator = 60,

    // ═══════════════════════════════════════════════════════════════
    // SYNCHRONIZATION LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Synchronization primitives (epoch manager, latch pools)</summary>
    Synchronization = 65,

    // ═══════════════════════════════════════════════════════════════
    // DURABILITY LAYER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Write-ahead log resources (ring buffer, segments)</summary>
    WAL = 70,

    /// <summary>Checkpoint subsystem</summary>
    Checkpoint = 71,

    /// <summary>Backup/restore resources (shadow buffer, snapshot store)</summary>
    Backup = 72
}

/// <summary>
/// A node in Typhon's runtime resource graph: a named, typed element of the engine tree that owns child resources and participates in
/// lifecycle (disposal) and diagnostic snapshotting. Nodes with measurable state additionally implement <see cref="IMetricSource"/>.
/// </summary>
[PublicAPI]
public interface IResource : IDisposable
{
    /// <summary>Stable identifier, unique among siblings. Forms one segment of the node's tree path (e.g. "PageCache" in "Storage/PageCache").</summary>
    string Id { get; }

    /// <summary>
    /// Human-readable display label. May equal <see cref="Id"/> for structural nodes whose id is already self-describing (e.g. "Storage"), but for resources
    /// with synthetic ids (GUIDs, hex suffixes) this is the user-facing name the Workbench and diagnostics should surface.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Optional scalar count exposed to UIs (Workbench tree badges, diagnostics). For ComponentTable this is the entity count; for a segments folder it could
    /// be the segment count. Structural nodes with no meaningful count return null.
    /// </summary>
    int? Count { get; }

    /// <summary>Classification of this resource, used for filtering and display.</summary>
    ResourceType Type { get; }

    /// <summary>Parent node in the tree, or <c>null</c> for the root.</summary>
    IResource Parent { get; }

    /// <summary>Direct child resources, in no particular order.</summary>
    IEnumerable<IResource> Children { get; }

    /// <summary>UTC timestamp captured when this node was constructed.</summary>
    DateTime CreatedAt { get; }

    /// <summary>Registry that owns the tree this node belongs to.</summary>
    IResourceRegistry Owner { get; }

    /// <summary>
    /// Attaches <paramref name="child"/> under this node and raises <see cref="IResourceRegistry.NodeMutated"/> with
    /// <see cref="ResourceMutationKind.Added"/>.
    /// </summary>
    /// <param name="child">The resource to attach.</param>
    /// <returns><c>true</c> if attached; <c>false</c> if a child with the same <see cref="Id"/> is already registered.</returns>
    bool RegisterChild(IResource child);

    /// <summary>
    /// Detaches <paramref name="resource"/> from this node and raises <see cref="IResourceRegistry.NodeMutated"/> with
    /// <see cref="ResourceMutationKind.Removed"/>.
    /// </summary>
    /// <param name="resource">The child resource to detach.</param>
    /// <returns><c>true</c> if detached; <c>false</c> if it was not a child of this node.</returns>
    bool RemoveChild(IResource resource);
}