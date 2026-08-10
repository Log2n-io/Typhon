using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Reads a variable-sized buffer (VSBS) out of a closed file, with no engine and no locks.
/// </summary>
/// <remarks>
/// <para>
/// The VSBS is the keystone of offline decode, which is not obvious until the pointers are followed:
/// <c>ComponentR1.Fields</c> is a <c>ComponentCollection&lt;FieldR1&gt;</c> and
/// <c>ArchetypeR1.ComponentNames</c> is a <c>ComponentCollection&lt;String64&gt;</c>, so field offsets, field types,
/// per-field index roots and per-archetype component membership all live behind a buffer id. Four check families
/// hang off that — see <c>09 §5.5</c>.
/// </para>
/// <para>
/// Layout, taken from <see cref="VariableSizedBufferSegmentBase{TStore}"/> rather than restated: every chunk starts
/// with a <see cref="VariableSizedBufferChunkHeader"/> (<c>NextChunkId</c>, <c>ElementCount</c>) and the chain is
/// walked through <c>NextChunkId</c> until it reaches 0. The one asymmetry is where elements begin — the root chunk
/// carries the wider <see cref="VariableSizedBufferRootHeader"/>, continuation chunks only the 8-byte header:
/// </para>
/// <code>
/// root chunk (bufferId)          continuation chunk
///   [0,8)   chunk header           [0,8)  chunk header
///   [8,32)  lock, free list,       [8,..) elements
///           TotalCount, refcount
///   [32,..) elements
/// </code>
/// <para>
/// The struct sizes are taken from the engine's own types via <see cref="Unsafe.SizeOf{T}"/> rather than written as
/// literals, so a layout change moves this reader with it instead of silently shifting every element by a few bytes.
/// </para>
/// </remarks>
internal sealed class VsbsReader
{
    /// <summary>Element count per chunk the engine sizes a collection segment for (<c>ComponentCollectionItemCountPerChunk</c>).</summary>
    private const int ItemCountPerChunk = 8;

    /// <summary>Chain length past which a buffer is declared cyclic rather than walked further.</summary>
    private const int MaxChainLength = 4096;

    private readonly IPageSource _source;
    private readonly byte[] _page = new byte[IntegrityConstants.PageSize];
    private int _cachedPage = -1;

    /// <summary>Creates a reader over a page source.</summary>
    /// <param name="source">The closed database to read.</param>
    public VsbsReader(IPageSource source) => _source = source;

    /// <summary>Diagnostics describing anything that could not be read, for the report's caveat list.</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>
    /// The chunk stride the engine allocates a component-collection segment with, for a given element size.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>GetComponentCollectionSegment</c>: the raw size is <c>max(elementSize × 8, sizeof(rootHeader))</c>,
    /// rounded up to a standard stride. This is what makes a buffer id resolvable at all — collection segments are
    /// pooled <i>by stride</i>, so an id alone is ambiguous, and it is the element type that picks the segment.
    /// </remarks>
    /// <param name="elementSize">Size of one element, as <c>sizeof(T)</c> sees it.</param>
    public static int StrideForElementSize(int elementSize)
        => RoundToStandardStride(Math.Max(elementSize * ItemCountPerChunk, Unsafe.SizeOf<VariableSizedBufferRootHeader>()));

    private static int RoundToStandardStride(int size)
        => size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 64 => 64,
            _ => (int)BitOperations.RoundUpToPowerOf2((uint)size)
        };

    /// <summary>
    /// Reads every element of one buffer, following the chunk chain.
    /// </summary>
    /// <typeparam name="T">Element type. Read with managed layout, matching the engine's <c>sizeof(T)</c> sizing.</typeparam>
    /// <param name="segment">The component-collection segment holding the buffer.</param>
    /// <param name="geometry">That segment's chunk geometry.</param>
    /// <param name="bufferId">The buffer's root chunk id. <c>0</c> means "never allocated" and yields an empty list.</param>
    /// <param name="elements">Receives the elements, in chain order.</param>
    /// <returns><c>true</c> when the whole buffer was read; <c>false</c> when it was truncated or unreadable.</returns>
    public bool TryReadBuffer<T>(SegmentView segment, ChunkGeometry geometry, int bufferId, List<T> elements)
        where T : unmanaged
    {
        elements.Clear();
        if (bufferId == 0)
        {
            return true;   // an unallocated collection is empty, not damaged
        }

        var elementSize = Unsafe.SizeOf<T>();
        var seen = new HashSet<int>();
        var chunkId = bufferId;

        for (var hop = 0; chunkId != 0; hop++)
        {
            if (hop >= MaxChainLength || !seen.Add(chunkId))
            {
                Diagnostics.Add($"VSBS buffer {bufferId} in segment {segment.RootPageIndex} does not terminate (revisits chunk {chunkId})");
                return false;
            }

            if (!TryGetChunk(segment, geometry, chunkId, out var chunk))
            {
                Diagnostics.Add($"VSBS buffer {bufferId} in segment {segment.RootPageIndex} names chunk {chunkId}, which could not be read");
                return false;
            }

            var header = MemoryMarshal.Read<VariableSizedBufferChunkHeader>(chunk);
            var isRoot = chunkId == bufferId;
            var elementsAt = isRoot
                ? Unsafe.SizeOf<VariableSizedBufferRootHeader>()
                : Unsafe.SizeOf<VariableSizedBufferChunkHeader>();

            var capacity = (geometry.Stride - elementsAt) / elementSize;
            if (header.ElementCount < 0 || header.ElementCount > capacity)
            {
                Diagnostics.Add($"VSBS chunk {chunkId} of buffer {bufferId} claims {header.ElementCount} elements, "
                    + $"but {capacity} is all it can hold");
                return false;
            }

            for (var i = 0; i < header.ElementCount; i++)
            {
                var at = elementsAt + (i * elementSize);
                elements.Add(MemoryMarshal.Read<T>(chunk.Slice(at, elementSize)));
            }

            chunkId = header.NextChunkId;
        }

        return true;
    }

    /// <summary>
    /// Walks a buffer's chunk chain without decoding elements, collecting the chunk ids it occupies.
    /// </summary>
    /// <remarks>
    /// What <c>ALO-04</c> needs and <see cref="TryReadBuffer{T}"/> cannot give it: accounting for handles requires the
    /// chunks a buffer <i>occupies</i>, and that is answerable without knowing the element type at all — which matters
    /// because the reverse direction has to account for buffers whose element type the scan never learns.
    /// </remarks>
    /// <param name="segment">The component-collection segment holding the buffer.</param>
    /// <param name="geometry">That segment's chunk geometry.</param>
    /// <param name="bufferId">The buffer's root chunk id.</param>
    /// <param name="chunkIds">Receives every chunk id in the chain, starting with the root.</param>
    /// <returns><c>true</c> when the chain terminated normally; <c>false</c> when it dangled or cycled.</returns>
    public bool TryWalkChunkIds(SegmentView segment, ChunkGeometry geometry, int bufferId, List<int> chunkIds)
    {
        chunkIds.Clear();
        if (bufferId == 0)
        {
            return true;
        }

        var seen = new HashSet<int>();
        var chunkId = bufferId;

        for (var hop = 0; chunkId != 0; hop++)
        {
            if (hop >= MaxChainLength || !seen.Add(chunkId))
            {
                Diagnostics.Add($"VSBS buffer {bufferId} in segment {segment.RootPageIndex} does not terminate (revisits chunk {chunkId})");
                return false;
            }

            if (!TryGetChunk(segment, geometry, chunkId, out var chunk) || !IsAllocated(segment, geometry, chunkId))
            {
                Diagnostics.Add($"VSBS buffer {bufferId} in segment {segment.RootPageIndex} names chunk {chunkId}, which is "
                    + "unreadable or not allocated");
                return false;
            }

            chunkIds.Add(chunkId);
            chunkId = MemoryMarshal.Read<VariableSizedBufferChunkHeader>(chunk).NextChunkId;
        }

        return true;
    }

    /// <summary>Whether the segment's own bitmap marks a chunk allocated.</summary>
    private bool IsAllocated(SegmentView segment, ChunkGeometry geometry, int chunkId)
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

    /// <summary>The <c>TotalCount</c> a buffer's root header claims, for cross-checking against what the walk found.</summary>
    /// <param name="segment">The component-collection segment holding the buffer.</param>
    /// <param name="geometry">That segment's chunk geometry.</param>
    /// <param name="bufferId">The buffer's root chunk id.</param>
    /// <param name="totalCount">Receives the claimed element count.</param>
    public bool TryReadTotalCount(SegmentView segment, ChunkGeometry geometry, int bufferId, out int totalCount)
    {
        totalCount = 0;
        if (bufferId == 0)
        {
            return true;
        }

        if (!TryGetChunk(segment, geometry, bufferId, out var chunk))
        {
            return false;
        }

        totalCount = MemoryMarshal.Read<VariableSizedBufferRootHeader>(chunk).TotalCount;
        return true;
    }

    /// <summary>Resolves a chunk id to its bytes, honouring the segment's page list and the page cache of one.</summary>
    private bool TryGetChunk(SegmentView segment, ChunkGeometry geometry, int chunkId, out ReadOnlySpan<byte> chunk)
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
}
