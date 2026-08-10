using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>One B+Tree registered in an index segment's chunk-0 directory.</summary>
internal readonly record struct IndexTreeEntry(short StableId, short Slot, int RootChunkId, int Count)
{
    /// <summary>The stable key that must be unique within a segment: <c>-1</c> is the primary key.</summary>
    public string Identity => StableId == -1 ? $"primary key (slot {Slot})" : $"field #{StableId} (slot {Slot})";
}

/// <summary>
/// Reads an index segment's tree directory, and walks node chains without decoding keys.
/// </summary>
/// <remarks>
/// <para>
/// Chunk 0 of an index segment is a directory: a <see cref="BTreeDirectoryHeader"/> then one
/// <see cref="BTreeDirectoryEntry"/> per registered tree. That is what makes an index segment enumerable offline at
/// all — without it there is no way to tell a root node from any other allocated chunk.
/// </para>
/// <para>
/// <b>Only the variant-independent part of a node is decoded here.</b> All four node layouts
/// (<see cref="Index16Chunk"/>, <see cref="Index32Chunk"/>, <see cref="Index64Chunk"/>,
/// <see cref="IndexString64Chunk"/>) share a 20-byte prefix — <c>Control</c>, <c>OlcVersion</c>, <c>PrevChunk</c>,
/// <c>NextChunk</c>, <c>LeftValue</c> — and diverge immediately after it, because the high key is 2, 4, 8 or 64 bytes
/// wide and it shifts the value and key arrays with it. Which variant a tree uses follows from the indexed field's
/// type, and reading a node through the wrong one yields keys that decode perfectly and mean nothing.
/// </para>
/// <para>
/// So this reader answers the questions the shared prefix can answer — do the links resolve, does the chain terminate,
/// is the entry count within what the node could hold — and stops there. The checks that need keys say so rather than
/// guessing at a width.
/// </para>
/// </remarks>
internal sealed class IndexDirectoryReader
{
    /// <summary>Byte offset of <c>Control</c>, common to every node layout.</summary>
    private const int ControlOffset = 0;

    /// <summary>Byte offset of <c>PrevChunk</c>, common to every node layout.</summary>
    private const int PrevChunkOffset = 8;

    /// <summary>Byte offset of <c>NextChunk</c>, common to every node layout.</summary>
    private const int NextChunkOffset = 12;

    /// <summary>Byte offset of <c>LeftValue</c> — an internal node's leftmost child — common to every node layout.</summary>
    private const int LeftValueOffset = 16;

    /// <summary>Bytes every node spends before its high key: control, latch, two links and the left value.</summary>
    private const int SharedPrefixSize = 20;

    /// <summary>Narrowest key any variant stores, used for the loosest safe capacity bound.</summary>
    private const int NarrowestKeySize = 2;

    /// <summary>Hop limit past which a chain is declared non-terminating rather than followed further.</summary>
    private const int MaxChainLength = 1 << 20;

    private readonly IPageSource _source;
    private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
    private int _cachedPage = -1;

    /// <summary>Creates a reader over a page source.</summary>
    /// <param name="source">The closed database to read.</param>
    public IndexDirectoryReader(IPageSource source) => _source = source;

    /// <summary>Node state flags as recorded in the low half of <c>Control</c>.</summary>
    /// <param name="node">The node's bytes.</param>
    public static NodeStates StatesOf(ReadOnlySpan<byte> node) => (NodeStates)MemoryMarshal.Read<short>(node[ControlOffset..]);

    /// <summary>Stored item count, the high byte of <c>Control</c>.</summary>
    /// <param name="node">The node's bytes.</param>
    public static int CountOf(ReadOnlySpan<byte> node) => node[ControlOffset + 3];

    /// <summary>Next sibling chunk id, or <c>0</c> at the end of a level.</summary>
    /// <param name="node">The node's bytes.</param>
    public static int NextOf(ReadOnlySpan<byte> node) => MemoryMarshal.Read<int>(node[NextChunkOffset..]);

    /// <summary>Previous sibling chunk id, or <c>0</c> at the start of a level.</summary>
    /// <param name="node">The node's bytes.</param>
    public static int PrevOf(ReadOnlySpan<byte> node) => MemoryMarshal.Read<int>(node[PrevChunkOffset..]);

    /// <summary>
    /// An internal node's <b>leftmost</b> child, which is not in the value array.
    /// </summary>
    /// <remarks>
    /// A B+Tree node holds N keys and N+1 children: <c>Values[i]</c> is the subtree to the RIGHT of key <c>i</c>, and
    /// the leftmost subtree lives in its own field. A descent that reads only the value array therefore misses the
    /// leftmost subtree at every level — which is not a partial walk, it is a walk that omits an exponentially growing
    /// share of the tree. It showed up as <c>IDX-04</c> reporting live entities as missing from a healthy index.
    /// </remarks>
    /// <param name="node">The node's bytes.</param>
    public static int LeftChildOf(ReadOnlySpan<byte> node) => MemoryMarshal.Read<int>(node[LeftValueOffset..]);

    /// <summary>
    /// The largest entry count any node of this stride could hold, whichever variant it is.
    /// </summary>
    /// <remarks>
    /// Deliberately the loosest bound that is still sound. The exact capacity depends on the variant, and using a
    /// too-tight one would report healthy nodes of a wider-keyed tree as over-full — a false positive on a correct
    /// database, which is worse than a bound that lets a subtly-wrong count through.
    /// </remarks>
    /// <param name="stride">The segment's chunk stride.</param>
    public static int LooseCapacity(int stride) => Math.Max(0, (stride - SharedPrefixSize) / (sizeof(int) + NarrowestKeySize));

    /// <summary>Reads the tree directory from chunk 0 of an index segment.</summary>
    /// <param name="segment">The index segment.</param>
    /// <param name="geometry">Its chunk geometry.</param>
    /// <param name="entries">Receives the registered trees.</param>
    /// <returns><c>false</c> when the directory chunk itself could not be read.</returns>
    public bool TryReadDirectory(SegmentView segment, ChunkGeometry geometry, List<IndexTreeEntry> entries)
    {
        entries.Clear();

        if (!TryGetChunk(segment, geometry, 0, out var chunk))
        {
            return false;
        }

        var headerSize = Unsafe.SizeOf<BTreeDirectoryHeader>();
        var entrySize = Unsafe.SizeOf<BTreeDirectoryEntry>();
        var declared = MemoryMarshal.Read<BTreeDirectoryHeader>(chunk).EntryCount;

        // The directory is bounded by the chunk that holds it. A torn header can claim any count, and trusting one
        // reads whatever follows the chunk as further trees.
        var capacity = (geometry.Stride - headerSize) / entrySize;
        for (var i = 0; i < declared && i < capacity; i++)
        {
            var entry = MemoryMarshal.Read<BTreeDirectoryEntry>(chunk.Slice(headerSize + (i * entrySize), entrySize));
            entries.Add(new IndexTreeEntry(entry.StableId, entry.Slot, entry.RootChunkId, entry.Count));
        }

        return true;
    }

    /// <summary>Whether the directory claims more trees than its chunk can hold.</summary>
    /// <param name="segment">The index segment.</param>
    /// <param name="geometry">Its chunk geometry.</param>
    /// <param name="declared">Receives the claimed count.</param>
    /// <param name="capacity">Receives what the chunk can actually hold.</param>
    public bool DirectoryOverflows(SegmentView segment, ChunkGeometry geometry, out int declared, out int capacity)
    {
        declared = 0;
        capacity = 0;

        if (!TryGetChunk(segment, geometry, 0, out var chunk))
        {
            return false;
        }

        declared = MemoryMarshal.Read<BTreeDirectoryHeader>(chunk).EntryCount;
        capacity = (geometry.Stride - Unsafe.SizeOf<BTreeDirectoryHeader>()) / Unsafe.SizeOf<BTreeDirectoryEntry>();
        return declared > capacity;
    }

    /// <summary>How a sibling-chain walk ended.</summary>
    internal enum ChainOutcome
    {
        /// <summary>The chain terminated normally.</summary>
        Terminated,

        /// <summary>A link named a chunk that is out of range, unreadable, or free.</summary>
        Dangling,

        /// <summary>The chain revisited a node, or ran past its hop limit.</summary>
        Cyclic,

        /// <summary>A node claimed more entries than any layout of this stride could hold.</summary>
        Overfull
    }

    /// <summary>
    /// Follows a node's <c>NextChunk</c> chain, checking that every hop resolves and that it terminates.
    /// </summary>
    /// <param name="segment">The index segment.</param>
    /// <param name="geometry">Its chunk geometry.</param>
    /// <param name="startChunkId">Node to start from.</param>
    /// <param name="visited">Accumulates every node id reached, across calls, so shared nodes are seen once.</param>
    /// <param name="failedAt">Receives the chunk id the walk failed on, when it did.</param>
    public ChainOutcome WalkSiblingChain(SegmentView segment, ChunkGeometry geometry, int startChunkId,
        HashSet<int> visited, out int failedAt)
    {
        failedAt = 0;
        var chainSeen = new HashSet<int>();
        var capacity = LooseCapacity(geometry.Stride);
        var current = startChunkId;

        for (var hop = 0; current != 0; hop++)
        {
            if (hop >= MaxChainLength || !chainSeen.Add(current))
            {
                failedAt = current;
                return ChainOutcome.Cyclic;
            }

            if (!TryGetChunk(segment, geometry, current, out var node) || !IsAllocated(segment, geometry, current))
            {
                failedAt = current;
                return ChainOutcome.Dangling;
            }

            if (CountOf(node) > capacity)
            {
                failedAt = current;
                return ChainOutcome.Overfull;
            }

            visited.Add(current);
            current = NextOf(node);
        }

        return ChainOutcome.Terminated;
    }

    /// <summary>Reads one chunk's bytes, or <c>false</c> when the id does not address a readable chunk.</summary>
    public bool TryGetChunk(SegmentView segment, ChunkGeometry geometry, int chunkId, out ReadOnlySpan<byte> chunk)
    {
        chunk = default;

        if (chunkId < 0 || !geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
        {
            return false;
        }

        var filePage = segment.Pages[ordinal];
        if (_cachedPage != filePage)
        {
            if (!_source.TryReadPage(filePage, _page))
            {
                _cachedPage = -1;
                return false;
            }

            _cachedPage = filePage;
        }

        var at = geometry.OffsetInPage(ordinal, chunkInPage);
        if (at < 0 || at + geometry.Stride > IntegrityConstants.PageSize)
        {
            return false;
        }

        chunk = new ReadOnlySpan<byte>(_page, at, geometry.Stride);
        return true;
    }

    /// <summary>Whether the segment's own bitmap marks a chunk allocated.</summary>
    public bool IsAllocated(SegmentView segment, ChunkGeometry geometry, int chunkId)
    {
        if (!geometry.TryLocate(chunkId, out var ordinal, out var chunkInPage) || ordinal >= segment.Pages.Count)
        {
            return false;
        }

        var filePage = segment.Pages[ordinal];
        if (_cachedPage != filePage)
        {
            if (!_source.TryReadPage(filePage, _page))
            {
                _cachedPage = -1;
                return false;
            }

            _cachedPage = filePage;
        }

        return geometry.IsChunkAllocated(_page, ordinal == 0, chunkInPage);
    }
}
