using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Semantic classification of a single file page, used by the Workbench Database File Map (Module 15).
/// Unlike <see cref="PageBlockType"/> — which only distinguishes <c>None</c> / <c>OccupancyMap</c> — this enum captures the role a page plays in the engine's
/// storage graph.
/// </summary>
[PublicAPI]
public enum StoragePageType : byte
{
    /// <summary>Page is allocated but not classifiable by Track-A introspection (e.g. cluster pages).</summary>
    Unknown = 0,

    /// <summary>Page is not allocated — its occupancy bit is clear.</summary>
    Free,

    /// <summary>One of the reserved root / header pages (page index &lt; 4).</summary>
    Root,

    /// <summary>A page of the occupancy-bitmap segment.</summary>
    Occupancy,

    /// <summary>A component-data page of a <c>ComponentTable</c>.</summary>
    Component,

    /// <summary>A component-revision (MVCC history) page.</summary>
    Revision,

    /// <summary>An index page (default / String64 / tail index).</summary>
    Index,

    /// <summary>An archetype cluster page.</summary>
    Cluster,

    /// <summary>A variable-sized-buffer segment page.</summary>
    Vsbs,

    /// <summary>A string-table segment page.</summary>
    StringTable,
}

/// <summary>Runtime role of a logical segment, as reported by <see cref="DatabaseEngine.EnumerateStorageSegments"/>.</summary>
[PublicAPI]
public enum StorageSegmentKind : byte
{
    /// <summary>Segment role not otherwise classified.</summary>
    Other = 0,

    /// <summary>Holds component instances.</summary>
    Component,

    /// <summary>Holds component-revision (MVCC history) records.</summary>
    Revision,

    /// <summary>Holds index entries (default / String64 / tail index).</summary>
    Index,

    /// <summary>Holds archetype cluster rows.</summary>
    Cluster,

    /// <summary>A variable-sized-buffer segment.</summary>
    Vsbs,

    /// <summary>A string-table segment.</summary>
    StringTable,

    /// <summary>The occupancy-bitmap segment.</summary>
    Occupancy,
}

/// <summary>
/// Read-only description of one logical segment's on-disk footprint — its kind and the file pages it owns.
/// Produced by <see cref="DatabaseEngine.EnumerateStorageSegments"/> for the Database File Map (Module 15).
/// </summary>
[PublicAPI]
public readonly struct StorageSegmentDescriptor
{
    /// <summary>Creates a descriptor for a segment.</summary>
    public StorageSegmentDescriptor(int rootPageIndex, StorageSegmentKind kind, ReadOnlyMemory<int> pages)
    {
        RootPageIndex = rootPageIndex;
        Kind = kind;
        Pages = pages;
    }

    /// <summary>The segment's root file-page index — stable and unique per segment.</summary>
    public int RootPageIndex { get; }

    /// <summary>The segment's runtime role.</summary>
    public StorageSegmentKind Kind { get; }

    /// <summary>The file-page indices this segment owns, in directory order. Not necessarily contiguous on disk.</summary>
    public ReadOnlyMemory<int> Pages { get; }
}
