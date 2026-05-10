using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

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

[PublicAPI]
public interface IResource : IDisposable
{
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

    ResourceType Type { get; }
    IResource Parent { get; }
    IEnumerable<IResource> Children { get; }
    DateTime CreatedAt { get; }
    IResourceRegistry Owner { get; }
    
    bool RegisterChild(IResource child);
    bool RemoveChild(IResource resource);
}