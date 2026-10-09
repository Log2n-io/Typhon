// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

// unset

using JetBrains.Annotations;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ChunkBasedSegmentHeader
{
    unsafe public static readonly int Size = sizeof(ChunkBasedSegmentHeader);
    public static readonly int TotalSize =  LogicalSegmentHeader.TotalSize + Size;
    public static readonly int Offset = LogicalSegmentHeader.TotalSize;

    private int _fill0;
}

/// <summary>
/// Logical Segment that stores fixed sized chunk of data.
/// </summary>
/// <remarks>
/// Provides API to allocate chunks; the occupancy map is stored in the Metadata of each page. Which pages may have a free chunk is a bitmap, one bit per
/// page with a summary bit per 64 pages, searched next-fit from the last page an allocation succeeded on (see <see cref="RoomBlock"/>). The minimum chunk
/// size is 8 bytes.
/// </remarks>
public class ChunkBasedSegment<TStore> : LogicalSegment<TStore> where TStore : struct, IPageStore
{
    private readonly Lock _growLock = new();
    private readonly EpochManager _epochManager;

    // Cached values for fast GetChunkLocation (avoids indirection)
    private readonly int _rootChunkCount;
    private readonly int _otherChunkCount;

    // Exact division of a chunk index by the chunks-per-page count, as one 64×64 multiply (#1204). See ChunkPageDivider.
    private readonly ChunkPageDivider _pageDivider;

    // Alignment padding: ensures chunks start at stride-aligned absolute page offsets (for ACLP).
    private readonly int _rootAlignmentPadding;
    private readonly int _otherAlignmentPadding;

    // Bitmap configuration: number of metadata longs used for L0 bitmap per page type
    private readonly int _bitmapLongsRoot;
    private readonly int _bitmapLongsOther;

    // ═══════════════════════════════════════════════════════════════════════
    // Page-room bitmap allocator state
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Room bits for 4 096 pages: one bit per page, set when the page may have a free chunk, and a summary bit per 64-page word, set when the word may be
    /// non-zero. A segment holds them in blocks that are allocated once and never move: growth adds blocks to a new outer array holding the same ones, so
    /// a bit set concurrently with a grow is never set on a copy that is then dropped (#1205).
    /// </summary>
    /// <remarks>
    /// The bits are a superset of the truth: a page with a free chunk always has its bit set; a page with its bit set may be full. A free clears its chunk's
    /// bit and then sets its page's; an allocation that finds a page full clears the page's bit and then reads the page again, setting the bit back if a
    /// chunk was freed in between. Each step is an interlocked operation — a full fence on x64 and arm64 — so whichever of the two runs second sees the
    /// other, and no page with a free chunk is left unmarked. The summary keeps the same rule one level up: a word that becomes non-zero sets its summary
    /// bit, and a search that finds a summary bit over a zero word clears it, then reads the word again.
    /// </remarks>
    private sealed class RoomBlock
    {
        internal const int Shift = 12;
        internal const int PageCount = 1 << Shift;
        internal const int WordCount = PageCount >> 6;
        internal readonly long[] Words = new long[WordCount];
        internal long Summary;
    }

    private RoomBlock[] _roomBlocks = [];

    // Bumped each time a room or summary bit goes from clear to set — by a free, or by an allocation setting back a bit it cleared. An allocation that
    // found no room grows the segment only if it is unchanged since its search began; otherwise a bit was set behind the search, and it searches again.
    private int _roomGeneration;

    // Clears in flight: an allocation that clears a room or summary bit counts itself here until it has read the page or word again (and set the bit
    // back if it had to). A search that ran inside that window skipped a bit about to be set back, so the grow decision waits for zero (#1205).
    private int _transientClears;

    // The page the last allocation succeeded on, where the next search starts (next fit). A hint: read and written without ordering.
    private int _allocationCursor;

    // Total allocated chunks across all pages. Updated via Interlocked.Increment/Decrement.
    private int _allocatedCount;

    // Total chunk capacity: written under _growLock with release semantics, read lock-free with acquire semantics — TryReserveChunk's callers on other
    // threads gate on it.
    private int _capacity;

    /// <summary>
    /// Test hook: runs when an allocation has found page <c>pageIndex</c> full, before it clears the page's room bit — the window in which a free on that
    /// page may already have set the bit it is about to clear. Per segment, null in production.
    /// </summary>
    internal Action<int> PageFullProbe;

    /// <summary>This segment's engine-scoped <c>EW-01</c> tick-fence guard, cached by the structures built over it.</summary>
    internal ExclusiveWindow FenceWindow => Store.EpochManager?.FenceWindow;

    internal ChunkBasedSegment(EpochManager epochManager, TStore store, int stride) : base(store)
    {
        if (stride < sizeof(long))
        {
            // Chunk-based segments (component storage, VSBS, string chunks) require a stride >= 8 bytes: chunk 0 is a reserved sentinel and free chunks store
            // an 8-byte next-chunk link in-place. The usual consumer trigger is a Versioned component whose PUBLIC fields total < 8 bytes — the stride is
            // derived from public fields, so a `private` padding field does NOT count. Remedy: add public padding (e.g. a second `public int`) so the
            // component is at least 8 bytes. (SingleVersion/Transient components already clear 8 bytes via their internal per-entity key, so this typically
            // only bites a Versioned component with a single 4-byte field.)
            throw new Exception(
                $"Invalid component/chunk stride: {stride} bytes, but a chunk-based segment requires at least {sizeof(long)} bytes. "
                + "If this is a component, its size is derived from its PUBLIC fields (a private padding field is not counted) — "
                + "add public padding so the component totals at least 8 bytes, e.g. a second 'public int'.");
        }

        _epochManager = epochManager;

        Stride = stride;

        // Alignment padding: align chunk starts to a cache line, capped at ChunkStartAlignment (64).
        // The original rule aligned to the full stride, which is pathological for large strides: for any stride
        // larger than PageHeaderSize (192) the padding becomes ~a whole chunk (stride - 192), so e.g. a 3960-byte
        // cluster stride wasted 3768 B/page and dropped capacity from 2 chunks to 1. Capping at 64 keeps the small-
        // stride behaviour (stride=64: 192 % 64 == 0 → zero padding) while never over-padding large strides. Callers
        // that need every chunk (not just chunk 0) cache-aligned round their stride up to this same boundary so the
        // constant pitch keeps each chunk aligned (clusters do this — see ArchetypeClusterInfo).
        int align = Math.Min(stride, PagedMMF.ChunkStartAlignment);
        bool needsAlignment = (PagedMMF.PageHeaderSize % align) != 0;
        _otherAlignmentPadding = needsAlignment ? align - (PagedMMF.PageHeaderSize % align) : 0;
        _rootAlignmentPadding = needsAlignment ? (align - ((PagedMMF.PageHeaderSize + RootHeaderIndexSectionLength) % align)) % align : 0;

        ChunkCountRootPage = (PagedMMF.PageRawDataSize - RootHeaderIndexSectionLength - _rootAlignmentPadding) / stride;
        ChunkCountPerPage = (PagedMMF.PageRawDataSize - _otherAlignmentPadding) / stride;

        // Cache for fast access in GetChunkLocation
        _rootChunkCount = ChunkCountRootPage;
        _otherChunkCount = ChunkCountPerPage;

        _pageDivider = new ChunkPageDivider(_otherChunkCount);

        // Bitmap longs per page type: ceil(chunks / 64)
        _bitmapLongsRoot = (ChunkCountRootPage + 63) >> 6;
        _bitmapLongsOther = (ChunkCountPerPage + 63) >> 6;
    }

    /// <summary>
    /// Byte offset within this segment's root page where chunk 0 <i>would</i> begin — after the page header, the
    /// logical-segment directory section, and stride-alignment padding. With the directory-only root (v4) the directory
    /// fills the whole raw-data area, so the root holds no chunks (<see cref="ChunkCountRootPage"/> == 0) and this offset
    /// lands at the page end; it is never used to address a chunk. Surfaced read-only for the Database File Map decoders
    /// (Module 15, A2), which gate on <see cref="ChunkCountRootPage"/> before slicing, so they never read at this offset.
    /// </summary>
    public int RootDataOffset => PagedMMF.PageHeaderSize + RootHeaderIndexSectionLength + _rootAlignmentPadding;

    /// <summary>Byte offset within a non-root page of this segment where chunk 0 begins.</summary>
    public int OtherDataOffset => PagedMMF.PageHeaderSize + _otherAlignmentPadding;

    /// <inheritdoc />
    /// <remarks>
    /// The chunk-occupancy bitmap grows up from the start of the metadata region; the per-sector verification footer grows
    /// down from its end. This is what keeps them apart, and it is consulted at page <i>initialisation</i> so a page never
    /// — not even transiently, in the window where it is unlatched and dirty and a checkpoint could persist it — declares
    /// a finer geometry than its bitmap leaves room for. A page that did would have its footer stamped over its own chunk
    /// bitmap, which silently corrupts chunk allocation rather than failing loudly.
    /// </remarks>
    protected override int MetadataReservedBytes(bool isRootPage) => (isRootPage ? _bitmapLongsRoot : _bitmapLongsOther) * sizeof(long);

    /// <inheritdoc />
    /// <remarks>
    /// This is the value that is otherwise nowhere in the file: it arrives as a constructor argument derived from a CLR
    /// component type, so a reader without the schema assembly cannot reconstruct it — and without it, cannot find
    /// chunk <i>n</i> on any page of this segment.
    /// </remarks>
    protected override int ChunkStrideForGeometry => Stride;


    // ═══════════════════════════════════════════════════════════════════════
    // Segment lifecycle: Create, Load, Grow
    // ═══════════════════════════════════════════════════════════════════════

    internal override bool Create(PageBlockType type, StorageSegmentKind kind, Span<int> filePageIndices, ChangeSet changeSet = null)
    {
        if (!base.Create(type, kind, filePageIndices, changeSet))
        {
            return false;
        }

        // Clear the metadata sections that store the chunk's occupancy bitmap
        var epoch = _store.EpochManager.GlobalEpoch;
        var length = filePageIndices.Length;
        for (int i = 0; i < length; i++)
        {
            var page = GetPageExclusive(i, epoch, out var memPageIdx);
            int longSize = i == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
            page.Metadata<long>(0, longSize).Clear();

            // Clear chunk 0's raw data on the root page so the BTree directory starts clean.
            // We do this inline because ReserveChunk(index, clearContent:true) needs a ChunkAccessor which requires an epoch scope — unavailable during segment
            // creation.
            //
            // Guard on ChunkCountRootPage > 0: with the directory-only root (v4) the root page's entire raw-data area is the segment's page directory, so it
            // holds ZERO chunks (ChunkCountRootPage == 0 for every stride) and chunk 0 lives on page 1, not the root. The guard is therefore never taken today —
            // it stays as a defensive invariant: clearing a full Stride at RootChunkDataOffset on a chunk-less root would overrun into the next page's header.
            // ChunkCountRootPage > 0 ⟺ RootChunkDataOffset + Stride ≤ PageRawDataSize, so the clear is overrun-free exactly when it would run.
            if (i == 0 && ChunkCountRootPage > 0)
            {
                page.RawData<byte>(RootChunkDataOffset, Stride).Clear();
            }

            _store.UnlatchPageExclusive(memPageIdx);
        }

        // Initialize allocator state for a fresh (empty) segment: every page that holds chunks has room.
        Volatile.Write(ref _capacity, ComputeCapacity(length));
        _allocatedCount = 0;
        _allocationCursor = 0;
        _roomBlocks = [];
        EnsureRoomBlocks(length);
        for (var i = 0; i < length; i++)
        {
            if (ChunksOnPage(i) > 0)
            {
                SetRoomUnsynchronized(i);
            }
        }

        ReserveChunk(0);                    // Mark chunk 0 as allocated ("null" sentinel) — data already cleared above
        return true;
    }

    internal override bool Load(int filePageIndex) => Load(filePageIndex, null);

    /// <summary>
    /// Loads the segment rooted at <paramref name="filePageIndex"/>: its directory, then its allocator — from <paramref name="summary"/> when one fits,
    /// reading no data page, else by scanning every page's chunk bitmap.
    /// </summary>
    /// <param name="filePageIndex">The segment's root page.</param>
    /// <param name="summary">
    /// What the last clean close recorded for this segment (<see cref="ManagedPagedMMF.TakeChunkSummary"/>), or <c>null</c>. Used only when its page count
    /// matches the directory just read and its count fits the capacity; anything else is a summary that does not describe this segment, and the scan runs.
    /// </param>
    /// <remarks>
    /// The scan is what a healthy open must not do (#1143): one page read per data page. Each page is read and released before the next (EP-02), so the
    /// scan holds one page of the cache however large the segment. It still runs after a crash, where the summary is never trusted, and there it carries
    /// the chain-versus-directory cross-check that used to run on every load (#382): see <see cref="ScanForAllocatorState"/>.
    /// </remarks>
    internal bool Load(int filePageIndex, ChunkSegmentSummary summary)
    {
        if (!base.Load(filePageIndex))
        {
            return false;
        }

        var length = Length;
        Volatile.Write(ref _capacity, ComputeCapacity(length));
        _allocationCursor = 0;
        _roomBlocks = [];
        EnsureRoomBlocks(length);

        if (summary != null && summary.PageCount == length && summary.AllocatedCount <= _capacity)
        {
            _allocatedCount = summary.AllocatedCount;
            for (var i = 0; i < length; i++)
            {
                if (summary.HasRoom(i))
                {
                    SetRoomUnsynchronized(i);
                }
            }

            LoadedFromSummary = true;
            return true;
        }

        ScanForAllocatorState(filePageIndex);
        return true;
    }

    /// <summary>Whether the last <see cref="Load(int, ChunkSegmentSummary)"/> rebuilt the allocator from a summary rather than by reading the pages.</summary>
    internal bool LoadedFromSummary { get; private set; }

    /// <summary>
    /// Rebuilds the allocator from the chunk bitmaps, the source of truth: reads every page. Checks each page's forward link against the directory as the
    /// page goes by, at no extra read.
    /// </summary>
    /// <remarks>
    /// The page directory and the forward chain (<see cref="LogicalSegmentHeader.LogicalSegmentNextRawDataPBID"/>) are written by separate code paths during
    /// a grow, so a disagreement means one write did not reach the disk — the shape an interrupted checkpoint leaves. The check is positional, as the grow's
    /// own post-condition is: page <c>i</c> must link to directory entry <c>i+1</c>, the last to 0. It throws <see cref="InvalidOperationException"/>, which
    /// on the crash path <see cref="ManagedPagedMMF.TryLoadChunkBasedSegment"/> turns into a fresh allocation that WAL replay rebuilds (RB-01, #395).
    /// </remarks>
    private void ScanForAllocatorState(int filePageIndex)
    {
        var length = Length;
        var pages = Pages;
        _allocatedCount = 0;

        // One page at a time (EP-02): an epoch-tagged read would hold every page of the segment for the open's scope, and a segment larger than the
        // cache would then wait for evictions its own tags forbid (#1144).
        for (int i = 0; i < length; i++)
        {
            var maxChunks = i == 0 ? _rootChunkCount : _otherChunkCount;
            var bitmapLongs = i == 0 ? _bitmapLongsRoot : _bitmapLongsOther;
            var page = AcquirePageForRead(i, out var memPageIndex);
            int next;
            int popcount;
            try
            {
                next = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextRawDataPBID;
                popcount = CountAllocatedBits(page.MetadataReadOnly<long>(), bitmapLongs, maxChunks);
            }
            finally
            {
                ReleasePageForRead(memPageIndex);
            }

            var expected = (i + 1) < length ? pages[i + 1] : 0;
            if (next != expected)
            {
                throw new InvalidOperationException(
                    $"ChunkBasedSegment integrity check failed at load: root={filePageIndex} kind={Kind} length={length} — page[{i}] (file page {pages[i]}) "
                    + $"links to {next}, its directory says {expected}. Signature of a lost write: the directory append or the forward-chain pointer of a "
                    + "grow did not persist before the previous close.");
            }

            _allocatedCount += popcount;

            if (popcount < maxChunks) // page has free space
            {
                SetRoomUnsynchronized(i);
            }
        }
    }

    /// <summary>
    /// The allocator state a clean close records for the next open (<see cref="ChunkSummaryFile"/>): the allocated count and which pages have their room
    /// bit set. Must run with nothing allocating or freeing — the close calls it after the final flush.
    /// </summary>
    /// <remarks>
    /// The room bits are a superset of the truth (<see cref="RoomBlock"/>): a page may be recorded with room and be full, which the first allocation to
    /// look at it corrects. The summary records exactly what the running engine had, so a reopened engine behaves as this one would have, and the count —
    /// exact by construction — is what callers read.
    /// </remarks>
    internal ChunkSegmentSummary CaptureSummary()
    {
        var length = Length;
        var blocks = Volatile.Read(ref _roomBlocks);
        var words = new ulong[ChunkSegmentSummary.WordCount(length)];
        for (var w = 0; w < words.Length && (w >> 6) < blocks.Length; w++)
        {
            words[w] = (ulong)Volatile.Read(ref blocks[w >> 6].Words[w & (RoomBlock.WordCount - 1)]);
        }

        if ((length & 63) != 0 && words.Length > 0)
        {
            words[^1] &= (1UL << (length & 63)) - 1;
        }

        return new ChunkSegmentSummary(RootPageIndex, length, _allocatedCount, words);
    }

    /// <summary>
    /// Grows the segment to accommodate more chunks.
    /// </summary>
    /// <param name="minNewPageCount">Minimum number of pages after growth. If 0, doubles the current size.</param>
    /// <param name="changeSet">Optional change set for tracking modifications.</param>
    /// <returns>True if growth occurred, false if already at maximum capacity.</returns>
    /// <remarks>
    /// This method is thread-safe. It uses a lock to ensure only one thread grows the segment at a time. The new pages' room bits are set before the pages
    /// are published; an allocation searches only the pages the segment holds, so it never meets a page without them.
    /// </remarks>
    private bool GrowChunkCapacity(int minNewPageCount = 0, ChangeSet changeSet = null)
    {
        lock (_growLock)
        {
            var currentLength = Length;

            // Calculate new size with capped-doubling growth. Up to GrowDoublingCap pages we double (cheap geometric growth when the segment is small); beyond
            // that we grow additively by the cap. Pure doubling at scale produces catastrophic single-allocation requests (e.g. a segment at 8192 pages
            // doubling to 16384 = 8192 NEW pages requested from the occupancy bitmap in one call), which stresses the bitmap's allocate-failure paths and
            // produces unfriendly amortised behaviour for callers that just want "a bit more room". The cap keeps single-grow request size bounded while still
            // amortising O(log n) grow operations until the cap is reached.
            const int GrowDoublingCap = 1024;
            var doubledOrAdditive = currentLength < GrowDoublingCap ? currentLength * 2L : currentLength + (long)GrowDoublingCap;
            var newLength = (int)Math.Min(MaxPageCount, minNewPageCount > 0 ? Math.Max(doubledOrAdditive, minNewPageCount) : doubledOrAdditive);

            // Check if we can grow: the cap is the chunk-id space (MaxChunkCount), and an allocation that finds it exhausted throws ResourceExhausted.
            if (newLength <= currentLength)
            {
                return false; // Already at maximum capacity
            }

            // Prefer the caller's ChangeSet, whose unit of work will release the marks taken below. Without one we make a
            // local set and release it ourselves before returning — it used to be created and simply abandoned, so every
            // grow without a caller ChangeSet stranded one mark per new page for the lifetime of the process.
            var localChangeSet = changeSet == null ? _store.CreateChangeSet() : null;
            var effectiveChangeSet = changeSet ?? localChangeSet;
            try
            {
                // Room bits first: they exist and are set before any allocation can see the pages. If the grow fails, they mark pages past the segment's
                // end, which no search reaches, and the next grow sets them again.
                EnsureRoomBlocks(newLength);
                MarkRoomRange(currentLength, newLength);

                // Grow the underlying logical segment (thread-safe, will allocate new pages). It clears every new page in full — the chunk bitmap with it —
                // and registers each with the change set (InitDataPages, PS-14), so nothing here writes a page after it is published.
                base.Grow(newLength, effectiveChangeSet);
                GrowPagesPublishedForTest?.Invoke(this);

                var oldCapacity = _capacity;
                Volatile.Write(ref _capacity, ComputeCapacity(newLength));

                // Phase 5: Storage:ChunkSegment:Grow event.
                TyphonEvent.EmitStorageChunkSegmentGrow(Stride, oldCapacity, _capacity);
                return true;
            }
            finally
            {
                // Release the marks the local set took, on a throw too (PS-05): a mark nobody releases pins its page for good, and a grow that fails
                // has already marked the pages it initialized. The new pages remain protected by their writeback debt until a checkpoint writes them,
                // so nothing here depends on holding a counted mark past this point.
                localChangeSet?.ReleaseDirtyMarks();
            }
        }
    }

    /// <summary>
    /// Ensures the segment can hold at least <paramref name="minChunkCount"/> chunks by growing if necessary.
    /// Used during schema migration to pre-size new segments before mirroring occupancy bitmaps.
    /// </summary>
    internal void EnsureCapacity(int minChunkCount, ChangeSet changeSet = null)
    {
        while (ChunkCapacity < minChunkCount)
        {
            var pagesNeeded = (int)Math.Min(MaxPageCount, 1 + ((minChunkCount - (long)ChunkCountRootPage + ChunkCountPerPage - 1) / ChunkCountPerPage));
            if (!GrowChunkCapacity(pagesNeeded, changeSet))
            {
                // At MaxPageCount the segment cannot reach the count asked for: say so rather than return short to a caller that would index past it.
                ThrowHelper.ThrowResourceExhausted("Storage/ChunkBasedSegment/EnsureCapacity", ResourceType.Memory, minChunkCount, ChunkCapacity);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Allocation and deallocation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records that a chunk's bytes were modified in place by a path that holds no ChangeSet — a span handed out over a cluster, written by the caller —
    /// so its page carries writeback debt: never evicted, always collected by the next checkpoint, until a durable write discharges it (PS-10).
    /// </summary>
    /// <param name="chunkId">The chunk written.</param>
    /// <remarks>Call inside an epoch scope, as every page access is (PS-02); the page is resident, since it was just written.</remarks>
    internal void MarkChunkModified(int chunkId)
    {
        var (pageIndex, _) = GetChunkLocation(chunkId);
        GetPage(pageIndex, _store.EpochManager.GlobalEpoch, out var memPageIdx);
        _store.MarkPageModified(memPageIdx);
    }

    public void ReserveChunk(int index)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(index);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = _store.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var metadata = page.Metadata<long>();

        var prev = Interlocked.Or(ref metadata[wordIndex], mask);
        if ((prev & mask) != 0)
        {
            return; // already reserved
        }

        _store.MarkPageModified(memPageIdx);
        Interlocked.Increment(ref _allocatedCount);
    }

    /// <summary>
    /// Reserves a specific chunk by index. If the chunk was not previously reserved and <paramref name="clearContent"/> is true, the chunk data is zeroed.
    /// </summary>
    public void ReserveChunk(int index, bool clearContent, ChangeSet changeSet = null)
    {
        ReserveChunk(index);
        if (clearContent)
        {
            using var accessor = CreateChunkAccessor(changeSet);
            accessor.ClearChunk(index);
        }
    }

    /// <summary>
    /// Allocates chunk <paramref name="chunkId"/> itself, if it is free: the allocation a structure makes when the chunk's position is its address — the
    /// next bucket of a linear hash map (#1205). Returns false, changing nothing, when the chunk is already allocated.
    /// </summary>
    /// <remarks>
    /// Durability as <see cref="AllocateChunk(ChangeSet, ref ChunkAccessor{TStore})"/> (CP-04, #301): the bitmap page is registered with
    /// <paramref name="changeSet"/>, and the chunk cleared through the caller's <paramref name="accessor"/>, which then holds its page until it is disposed.
    /// The chunk must lie within the segment's capacity.
    /// </remarks>
    internal bool TryReserveChunk(int chunkId, ChangeSet changeSet, ref ChunkAccessor<TStore> accessor)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(chunkId);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = _store.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var prevBits = Interlocked.Or(ref page.Metadata<long>()[wordIndex], mask);
        if ((prevBits & mask) != 0)
        {
            return false;
        }

        if (changeSet != null)
        {
            changeSet.AddByMemPageIndex(memPageIdx);
        }
        else
        {
            _store.MarkPageModified(memPageIdx);
        }

        Interlocked.Increment(ref _allocatedCount);
        accessor.ClearChunk(chunkId);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Allocation floor
    // ═══════════════════════════════════════════════════════════════════════

    // Chunk id below which the allocator hands nothing out; 0 for every segment but one whose low chunks are addressed by position and taken one by one
    // with TryReserveChunk — a linear hash map's buckets (#1205). Volatile: the allocator reads it lock-free, the map raises it as its buckets grow.
    private volatile int _allocationFloor;

    /// <summary>
    /// Chunk id below which <see cref="AllocateChunk(ChangeSet, ref ChunkAccessor{TStore})"/> allocates nothing. Chunks below it can still be taken one by
    /// one with <see cref="TryReserveChunk"/>, freed, and read: the floor only keeps the allocator out.
    /// </summary>
    internal int AllocationFloor => _allocationFloor;

    /// <summary>Raises <see cref="AllocationFloor"/> to <paramref name="floor"/>; a lower value changes nothing.</summary>
    internal void RaiseAllocationFloor(int floor)
    {
        var current = _allocationFloor;
        while (floor > current)
        {
            var seen = Interlocked.CompareExchange(ref _allocationFloor, floor, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>Sets <see cref="AllocationFloor"/> outright, lower included: for a structure reset to empty, when nothing allocates concurrently.</summary>
    /// <remarks>
    /// Lowering it hands the allocator pages it never searched: the floor's own page may have had its room bit cleared while its only free chunks lay below
    /// the floor. Those pages are marked; the first allocation to find one full clears it again.
    /// </remarks>
    internal void ResetAllocationFloor(int floor)
    {
        var previous = _allocationFloor;
        _allocationFloor = floor;
        var length = Length;
        if (floor < previous && length > 0)
        {
            MarkRoomRange(Math.Min(LocateUnchecked(floor).Page, length), Math.Min(LocateUnchecked(previous).Page + 1, length));
        }
    }

    /// <summary>
    /// The page and in-page offset of chunk <paramref name="index"/>, without <see cref="GetChunkLocation"/>'s check that the page exists: for the floor,
    /// which may sit past the segment's current end.
    /// </summary>
    private (int Page, int Offset) LocateUnchecked(int index)
    {
        if (index < _rootChunkCount)
        {
            return (0, index);
        }

        var adjusted = (uint)(index - _rootChunkCount);
        var page = _pageDivider.Divide(adjusted);
        return ((int)page + 1, (int)(adjusted - page * (uint)_otherChunkCount));
    }

    /// <summary>
    /// Whether page <paramref name="pageIndex"/> has a free chunk at or above <paramref name="fromOffset"/>. Reads the page and releases it (EP-02): a search
    /// that looks at many full pages pins none of them for the caller's epoch.
    /// </summary>
    private bool PageHasFreeChunkFrom(int pageIndex, int fromOffset)
    {
        var page = AcquirePageForRead(pageIndex, out var memPageIndex);
        try
        {
            return HasFreeChunkFrom(page.MetadataReadOnly<long>(), BitmapLongsOnPage(pageIndex), ChunksOnPage(pageIndex), fromOffset);
        }
        finally
        {
            ReleasePageForRead(memPageIndex);
        }
    }

    /// <summary>Whether a page's chunk bitmap has a clear bit at or above <paramref name="fromOffset"/>, below <paramref name="maxChunks"/>.</summary>
    private static bool HasFreeChunkFrom(ReadOnlySpan<long> metadata, int bitmapLongs, int maxChunks, int fromOffset)
    {
        for (var w = fromOffset >> 6; w < bitmapLongs; w++)
        {
            var first = w * 64;
            var free = ~metadata[w];
            if (fromOffset > first)
            {
                free &= -1L << (fromOffset - first);
            }

            if (maxChunks - first < 64)
            {
                free &= (1L << (maxChunks - first)) - 1;
            }

            if (free != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Allocates a single chunk from the segment using the CALLER's <see cref="ChunkAccessor{TStore}"/> for the optional clear-content step. This is the
    /// CP-04-safe variant: the new chunk's content page stays under ACW &gt; 0 protection continuously from <see cref="ChunkAccessor{TStore}.ClearChunk"/>
    /// through the caller's first write — eliminating the transient ACW=0 window that exists when the legacy <see cref="AllocateChunk(bool, ChangeSet)"/>
    /// overload uses a short-lived local accessor.
    /// </summary>
    /// <param name="changeSet">ChangeSet for dirty-tracking the bitmap page (required for CP-04 protection of the allocation itself).</param>
    /// <param name="accessor">The caller's accessor; used for <see cref="ChunkAccessor{TStore}.ClearChunk"/> so ACW stays held until caller dispose.</param>
    /// <returns>The allocated chunk ID, with content cleared via the caller's accessor.</returns>
    /// <remarks>
    /// Use this overload for any allocation immediately followed by writes through the same accessor — bucket chunks, overflow chunks, dir chunks,
    /// OverflowDirIndex chunks. The legacy overload's race window (local accessor disposes → ACW=0 on new chunk's page → checkpoint snapshots zeroed content
    /// → fsync drops DC→0 → eviction → reload-from-disk-zeros loses subsequent dir/bucket writes) does not exist here because the caller's accessor remains
    /// the sole dirty-tracker for this page.
    /// </remarks>
    public int AllocateChunk(ChangeSet changeSet, ref ChunkAccessor<TStore> accessor) => AllocateChunkInternal(true, changeSet, ref accessor);

    /// <summary>
    /// Allocates a single chunk from the segment.
    /// </summary>
    /// <param name="clearContent">Whether to clear the chunk content after allocation.</param>
    /// <param name="changeSet">Optional ChangeSet for tracking dirty pages during segment growth. When provided, growth pages are
    /// tracked by this ChangeSet (tied to a UoW lifecycle) instead of an orphaned local ChangeSet. This prevents a race where checkpoint
    /// writes newly-grown pages (zeros) and decrements their DirtyCounter to 0 before the caller can protect them.</param>
    /// <returns>The allocated chunk ID.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment is at maximum capacity and cannot grow.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// The allocation itself is lock-free; only growth and rebuild operations require synchronization.
    /// <para>
    /// CP-04 race window: when <paramref name="clearContent"/> is true, a short-lived local accessor performs the
    /// clear and disposes immediately on return — leaving the new chunk's content page with ACW=0 before the caller
    /// can take ownership. Prefer <see cref="AllocateChunk(ChangeSet, ref ChunkAccessor{TStore})"/> when a caller
    /// accessor is in scope to eliminate this window.
    /// </para>
    /// </remarks>
    public int AllocateChunk(bool clearContent, ChangeSet changeSet = null)
    {
        var localAccessor = clearContent ? CreateChunkAccessor(changeSet) : default;
        try
        {
            return AllocateChunkInternal(clearContent, changeSet, ref localAccessor);
        }
        finally
        {
            if (clearContent)
            {
                localAccessor.Dispose();
            }
        }
    }

    /// <summary>Called with the segment at the start of every allocation; a test throws from it to stand in for a failing grow. Test seam.</summary>
    internal static Action<object> AllocateFaultForTest;

    /// <summary>Called with the segment by a grow between publishing its pages and publishing their capacity. Test seam.</summary>
    internal static Action<object> GrowPagesPublishedForTest;

    private int AllocateChunkInternal(bool clearContent, ChangeSet changeSet, ref ChunkAccessor<TStore> accessor)
    {
        AllocateFaultForTest?.Invoke(this);
        while (true)
        {
            var capacityAtStart = Volatile.Read(ref _capacity);
            var generationAtStart = Volatile.Read(ref _roomGeneration);

            // The pages the published capacity covers, not Length: a grow publishes its pages before their capacity, and a chunk taken on a page in
            // between would be an id past the capacity every other reader bounds by. A search that finds those pages out of reach waits on the grow lock
            // below, and searches again once the grow has published.
            var length = PagesCovering(capacityAtStart);

            // The allocation floor (#1205): nothing below it is the allocator's. Pages wholly below are not searched, and the floor's own page is searched
            // from the floor's offset.
            var floor = _allocationFloor;
            var (floorPage, floorOffset) = floor > 0 ? LocateUnchecked(floor) : (0, 0);
            if (floorPage < length)
            {
                var start = Math.Clamp(_allocationCursor, floorPage, length - 1);
                var chunkId = AllocateFrom(start, length, floorPage, floorOffset, clearContent, changeSet, ref accessor);
                if (chunkId < 0 && start > floorPage)
                {
                    chunkId = AllocateFrom(floorPage, start, floorPage, floorOffset, clearContent, changeSet, ref accessor);
                }

                if (chunkId >= 0)
                {
                    return chunkId;
                }
            }

            lock (_growLock)
            {
                // No page above the floor had a free chunk — unless the segment grew meanwhile, a bit was set behind the search, or a clear is in
                // flight (its page may be set back): then search again. A free that set no bit (the page's was already set) lands on a page the
                // search either found or has yet to reach.
                if (Volatile.Read(ref _capacity) == capacityAtStart && Volatile.Read(ref _roomGeneration) == generationAtStart
                    && Volatile.Read(ref _transientClears) == 0 && !GrowChunkCapacity(changeSet: changeSet))
                {
                    ThrowHelper.ThrowResourceExhausted("Storage/ChunkBasedSegment/AllocateChunk", ResourceType.Memory, _allocatedCount, _capacity);
                }
            }
        }
    }

    /// <summary>
    /// Allocates a chunk on the first page in [<paramref name="from"/>, <paramref name="to"/>) that has one free, searching the room bits; -1 when none
    /// does.
    /// </summary>
    private int AllocateFrom(int from, int to, int floorPage, int floorOffset, bool clearContent, ChangeSet changeSet,
        ref ChunkAccessor<TStore> accessor)
    {
        var blocks = Volatile.Read(ref _roomBlocks);
        to = (int)Math.Min(to, (long)blocks.Length << RoomBlock.Shift);
        var first = true;
        var page = from;
        while ((page = NextPageWithRoom(blocks, page, to)) >= 0)
        {
            var chunkId = TryAllocateOnPage(page, page == floorPage ? floorOffset : 0, !first, clearContent, changeSet, ref accessor);
            if (chunkId >= 0)
            {
                // ReSharper disable once RedundantCheckBeforeAssignment
                if (_allocationCursor != page)
                {
                    _allocationCursor = page;   // written only when it moves: its cache line is the one every allocation reads first
                }

                return chunkId;
            }

            first = false;
            page++;
        }

        return -1;
    }

    /// <summary>
    /// Takes a free chunk at or above <paramref name="pageFloor"/> on page <paramref name="pageIndex"/>, or finds it full: clears its room bit, then reads it
    /// again and sets the bit back if a chunk was freed in between (see <see cref="RoomBlock"/>). -1 when the page is full.
    /// </summary>
    /// <remarks>
    /// With <c>lookFirst</c> the page is read and released before it is claimed (EP-02). The first page a search tries is usually the one the last
    /// allocation succeeded on and has room, so it is claimed at once; the pages after it are looked at first, so a search across stale bits on full pages
    /// pins none of them for the caller's epoch.
    /// </remarks>
    private int TryAllocateOnPage(int pageIndex, int pageFloor, bool lookFirst, bool clearContent, ChangeSet changeSet,
        ref ChunkAccessor<TStore> accessor)
    {
        while (true)
        {
            if (!lookFirst || PageHasFreeChunkFrom(pageIndex, pageFloor))
            {
                var chunkId = ClaimOnPage(pageIndex, pageFloor, clearContent, changeSet, ref accessor);
                if (chunkId >= 0)
                {
                    return chunkId;
                }
            }

            // Full: clear the bit, then look again — a free that set it before the clear would otherwise be lost with it.
            PageFullProbe?.Invoke(pageIndex);
            Interlocked.Increment(ref _transientClears);
            try
            {
                ClearRoom(pageIndex);
                if (!PageHasFreeChunkFrom(pageIndex, pageFloor))
                {
                    return -1;
                }

                MarkRoom(pageIndex);   // set back, as news: a search that skipped it while clear must search again
            }
            finally
            {
                Interlocked.Decrement(ref _transientClears);
            }

            lookFirst = true;
        }
    }

    /// <summary>Sets a free bit at or above <paramref name="pageFloor"/> in page <paramref name="pageIndex"/>'s chunk bitmap; -1 when there is none.</summary>
    private int ClaimOnPage(int pageIndex, int pageFloor, bool clearContent, ChangeSet changeSet, ref ChunkAccessor<TStore> accessor)
    {
        var maxChunks = ChunksOnPage(pageIndex);
        var bitmapLongs = BitmapLongsOnPage(pageIndex);
        var epoch = _store.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var metadata = page.Metadata<long>();

        for (int w = 0; w < bitmapLongs; w++)
        {
            var word = metadata[w];
            var belowFloor = pageFloor - w * 64;
            var floorMask = belowFloor <= 0 ? 0L : belowFloor >= 64 ? -1L : (1L << belowFloor) - 1;
            word |= floorMask;
            while (word != -1L)
            {
                var bit = BitOperations.TrailingZeroCount(~word);
                var chunkInPage = w * 64 + bit;
                if (chunkInPage >= maxChunks)
                {
                    return -1;
                }

                var mask = 1L << bit;
                var prevBits = Interlocked.Or(ref metadata[w], mask);
                if ((prevBits & mask) != 0)
                {
                    // Lost race — update word with current state and retry next bit
                    word = prevBits | mask | floorMask;
                    continue;
                }

                // SUCCESS — chunk claimed.
                //
                // CRITICAL (#301): the bitmap write must leave the page owed, every time. A running checkpoint may have ALREADY captured this page's
                // bitmap word BEFORE our Interlocked.Or above, with bit=0. If nothing recorded our write, that capture's fsync would settle the page,
                // eviction + reload would restore bit=0 from disk — silently REVERTING the allocation — and a later AllocateChunk would hand the SAME
                // chunkId out twice (the DOUBLE-ALLOC caught at scale with the ground-truth tracker). Recording the modification moves WritebackGen past
                // the capture, so the page stays owed to the next cycle (CP-04): AddByMemPageIndex records it on every call and takes a mark on the
                // first; without a ChangeSet, MarkPageModified records it alone.
                if (changeSet != null)
                {
                    changeSet.AddByMemPageIndex(memPageIdx);
                }
                else
                {
                    _store.MarkPageModified(memPageIdx);
                }
                Interlocked.Increment(ref _allocatedCount);

                var chunkId = PageOffsetToChunkIndex(pageIndex, chunkInPage);

                if (clearContent)
                {
                    accessor.ClearChunk(chunkId);
                }

                return chunkId;
            }
        }

        return -1;
    }

    /// <summary>
    /// Allocates multiple chunks from the segment.
    /// </summary>
    /// <param name="count">The number of chunks to allocate.</param>
    /// <param name="clearContent">Whether to clear the chunk content after allocation.</param>
    /// <param name="changeSet">Optional ChangeSet for tracking dirty pages during segment growth.</param>
    /// <returns>A memory owner containing the allocated chunk IDs.</returns>
    /// <exception cref="ResourceExhaustedException">Thrown when the segment cannot accommodate the requested chunks.</exception>
    /// <remarks>
    /// This method automatically grows the segment when capacity is exhausted.
    /// Growth is attempted iteratively until the request can be satisfied or maximum capacity is reached.
    /// </remarks>
    public IMemoryOwner<int> AllocateChunks(int count, bool clearContent, ChangeSet changeSet = null)
    {
        var res = MemoryPool<int>.Shared.Rent(count);
        var span = res.Memory.Span;
        var allocated = 0;

        try
        {
            for (int i = 0; i < count; i++)
            {
                span[i] = AllocateChunk(clearContent, changeSet);
                allocated++;
            }
        }
        catch
        {
            // Rollback: free any chunks we already allocated
            for (int i = 0; i < allocated; i++)
            {
                FreeChunk(span[i]);
            }
            res.Dispose();
            throw;
        }

        return res;
    }

    /// <summary>
    /// Frees of a chunk that was already free. Diagnostic: each is a double free that a concurrent allocation would have turned into two owners.
    /// </summary>
    internal long DoubleFreeCount;

    public void FreeChunk(int chunkId)
    {
        // Chunk 0 is reserved (e.g., meta for paged hash maps). Refuse to free it — freeing would give its page room again and AllocateChunk would hand
        // it out, causing every caller to clobber the meta chunk.
        if (chunkId == 0)
        {
            return;
        }

        var (pageIndex, chunkInPage) = GetChunkLocation(chunkId);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = _store.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out var memPageIdx);
        var metadata = page.Metadata<long>();

        var prev = Interlocked.And(ref metadata[wordIndex], ~mask);

        // Guard against double-free - only proceed if the bit was actually set. Counted: a second free is harmless only while nobody has taken the chunk
        // in between, so every one is a latent shared chunk (REAP-02) — the counter is what lets a single-threaded test see it.
        if ((prev & mask) == 0)
        {
            Interlocked.Increment(ref DoubleFreeCount);
            return;
        }

        // CRITICAL (#301): same race as AllocateChunk. If a concurrent checkpoint snapshotted the bitmap before our
        // Interlocked.And, that snapshot still has bit=1 (allocated) — so unless this page is marked modified AFTER the
        // clear, the write in flight discharges an obligation it does not cover, and a later eviction + reload silently
        // REVERTS the free. Recording the modification is exactly right and costs nothing: the generation moves past
        // whatever the checkpoint sampled, so the page stays owed and gets rewritten.
        //
        // This used to take a counted mark (IncrementDirty) instead. FreeChunk has no ChangeSet and therefore no lifecycle
        // in which to release it, so every free permanently inflated the counter on a metadata page — a leak the old
        // comment acknowledged and hand-waved as "bounded in practice by the next per-UoW release". It was not: that
        // release only drains marks the ChangeSet itself took.
        _store.MarkPageModified(memPageIdx);
        Interlocked.Decrement(ref _allocatedCount);

        // The chunk bit first, then the page's room bit: an allocation that clears the room bit reads the page again afterwards (RoomBlock).
        MarkRoom(pageIndex);
    }

    /// <summary>
    /// Frees every allocated chunk from <paramref name="firstChunkId"/> on, by the bitmaps, a page at a time: for a structure reset to empty
    /// (<see cref="PagedHashMapBase{TStore}.ClearForRebuild"/>), with nothing allocating or freeing concurrently. Each page is read and released (EP-02),
    /// so a reset of a segment larger than the cache pins none of it for the caller's epoch; one bitmap word at a time, not one chunk. Chunk 0 is never
    /// freed.
    /// </summary>
    internal void FreeAllChunksFrom(int firstChunkId)
    {
        firstChunkId = Math.Max(1, firstChunkId);
        if (firstChunkId >= ChunkCapacity)
        {
            return;
        }

        var length = Length;
        var (firstPage, firstOffset) = GetChunkLocation(firstChunkId);
        var freed = 0;
        for (var pageIndex = firstPage; pageIndex < length; pageIndex++)
        {
            if (ChunksOnPage(pageIndex) == 0)
            {
                continue;
            }

            var page = AcquirePageForRead(pageIndex, out var memPageIndex);
            try
            {
                var metadata = page.Metadata<long>();
                var from = pageIndex == firstPage ? firstOffset : 0;
                var changed = false;
                for (var w = from >> 6; w < BitmapLongsOnPage(pageIndex); w++)
                {
                    var keep = from > w * 64 ? (1L << (from - w * 64)) - 1 : 0L;   // the bits below the first chunk freed stay
                    var word = metadata[w];
                    var cleared = word & ~keep;
                    if (cleared != 0)
                    {
                        metadata[w] = word & keep;
                        freed += BitOperations.PopCount((ulong)cleared);
                        changed = true;
                    }
                }

                if (changed)
                {
                    _store.MarkPageModified(memPageIndex);
                }
            }
            finally
            {
                ReleasePageForRead(memPageIndex);
            }
        }

        Interlocked.Add(ref _allocatedCount, -freed);
        MarkRoomRange(firstPage, length);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Page-room bitmap
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Chunks page <paramref name="pageIndex"/> holds: the root's count or every other page's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ChunksOnPage(int pageIndex) => pageIndex == 0 ? _rootChunkCount : _otherChunkCount;

    /// <summary>The 64-bit words of page <paramref name="pageIndex"/>'s chunk bitmap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BitmapLongsOnPage(int pageIndex) => pageIndex == 0 ? _bitmapLongsRoot : _bitmapLongsOther;

    /// <summary>
    /// Makes room blocks exist for pages [0, <paramref name="pageCount"/>): adds blocks to a new outer array that holds the existing ones, never copies a
    /// block. Under the grow lock, or before the segment is shared.
    /// </summary>
    private void EnsureRoomBlocks(int pageCount)
    {
        var blocks = _roomBlocks;
        var needed = (int)(((long)pageCount + RoomBlock.PageCount - 1) >> RoomBlock.Shift);
        if (needed <= blocks.Length)
        {
            return;
        }

        var grown = new RoomBlock[needed];
        Array.Copy(blocks, grown, blocks.Length);
        for (var b = blocks.Length; b < needed; b++)
        {
            grown[b] = new RoomBlock();
        }

        Volatile.Write(ref _roomBlocks, grown);
    }

    /// <summary>Sets page <paramref name="pageIndex"/>'s room bit, for a segment no other thread sees yet (create, load).</summary>
    private void SetRoomUnsynchronized(int pageIndex)
    {
        var block = _roomBlocks[pageIndex >> RoomBlock.Shift];
        var w = (pageIndex & (RoomBlock.PageCount - 1)) >> 6;
        block.Words[w] |= 1L << (pageIndex & 63);
        block.Summary |= 1L << w;
    }

    /// <summary>Sets page <paramref name="pageIndex"/>'s room bit — the page has a free chunk — and its word's summary bit.</summary>
    private void MarkRoom(int pageIndex)
    {
        if (SetRoomBit(pageIndex))
        {
            Interlocked.Increment(ref _roomGeneration);
        }
    }

    /// <summary>Sets page <paramref name="pageIndex"/>'s room bit and, when its word was zero, the summary bit. True when the room bit was clear.</summary>
    private bool SetRoomBit(int pageIndex)
    {
        var block = Volatile.Read(ref _roomBlocks)[pageIndex >> RoomBlock.Shift];
        var w = (pageIndex & (RoomBlock.PageCount - 1)) >> 6;
        var bit = 1L << (pageIndex & 63);
        var previous = Interlocked.Or(ref block.Words[w], bit);
        if ((previous & bit) != 0)
        {
            return false;
        }

        if (previous == 0)
        {
            Interlocked.Or(ref block.Summary, 1L << w);
        }

        return true;
    }

    /// <summary>Sets the room bits of pages [<paramref name="from"/>, <paramref name="to"/>), one interlocked operation per word.</summary>
    private void MarkRoomRange(int from, int to)
    {
        if (from >= to)
        {
            return;
        }

        var blocks = Volatile.Read(ref _roomBlocks);
        for (var page = from; page < to;)
        {
            var block = blocks[page >> RoomBlock.Shift];
            var w = (page & (RoomBlock.PageCount - 1)) >> 6;
            var first = page & 63;
            var count = Math.Min(64 - first, to - page);
            var mask = count == 64 ? -1L : ((1L << count) - 1) << first;
            Interlocked.Or(ref block.Words[w], mask);
            Interlocked.Or(ref block.Summary, 1L << w);
            page += count;
        }

        Interlocked.Increment(ref _roomGeneration);
    }

    /// <summary>Clears page <paramref name="pageIndex"/>'s room bit; the summary bit is cleared lazily, by the next search over a zero word.</summary>
    private void ClearRoom(int pageIndex)
    {
        var block = Volatile.Read(ref _roomBlocks)[pageIndex >> RoomBlock.Shift];
        Interlocked.And(ref block.Words[(pageIndex & (RoomBlock.PageCount - 1)) >> 6], ~(1L << (pageIndex & 63)));
    }

    /// <summary>The first page in [<paramref name="from"/>, <paramref name="to"/>) whose room bit is set, or -1.</summary>
    /// <remarks>Page arithmetic in 64 bits: a block's end is past <see cref="int.MaxValue"/> for the last block of a segment at the page cap.</remarks>
    private int NextPageWithRoom(RoomBlock[] blocks, int from, int to)
    {
        long page = from;
        while (page < to)
        {
            var b = (int)(page >> RoomBlock.Shift);
            var block = blocks[b];
            var blockBase = (long)b << RoomBlock.Shift;
            var w = (int)(page - blockBase) >> 6;
            var summary = Volatile.Read(ref block.Summary) & (-1L << w);
            if (summary == 0)
            {
                page = blockBase + RoomBlock.PageCount;
                continue;
            }

            var summaryWord = BitOperations.TrailingZeroCount(summary);
            if (summaryWord > w)
            {
                w = summaryWord;
                page = blockBase + (w << 6);
            }

            var word = Volatile.Read(ref block.Words[w]);
            if (word == 0)
            {
                // A summary bit over a zero word: clear it, then read the word again — a page marked in between sets it back (RoomBlock), as news to a
                // search that skipped the word while it was clear.
                Interlocked.Increment(ref _transientClears);
                try
                {
                    Interlocked.And(ref block.Summary, ~(1L << w));
                    if (Volatile.Read(ref block.Words[w]) != 0)
                    {
                        Interlocked.Or(ref block.Summary, 1L << w);
                        Interlocked.Increment(ref _roomGeneration);
                        continue;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _transientClears);
                }

                page = blockBase + ((w + 1) << 6);
                continue;
            }

            word &= -1L << (int)(page & 63);
            if (word != 0)
            {
                var found = blockBase + (w << 6) + BitOperations.TrailingZeroCount(word);
                return found < to ? (int)found : -1;
            }

            page = blockBase + ((w + 1) << 6);
        }

        return -1;
    }

    /// <summary>Whether page <paramref name="pageIndex"/>'s room bit is set. Test seam.</summary>
    internal bool HasRoomBitForTest(int pageIndex)
    {
        var block = Volatile.Read(ref _roomBlocks)[pageIndex >> RoomBlock.Shift];
        return (Volatile.Read(ref block.Words[(pageIndex & (RoomBlock.PageCount - 1)) >> 6]) & (1L << (pageIndex & 63))) != 0;
    }

    /// <summary>Sets every page's room bit, as a summary recorded before pages filled would. Test seam.</summary>
    internal void MarkEveryPageForTest() => MarkRoomRange(0, Length);

    /// <summary>Starts the next search at the first page, so the next allocation takes the lowest free chunk. Test seam.</summary>
    internal void RewindAllocationCursorForTest() => _allocationCursor = 0;

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The largest chunk count a segment may reach. A chunk id is an <c>int</c> and <c>-1</c> is the "none" sentinel everywhere, so ids stay in
    /// <c>[0, int.MaxValue)</c>.
    /// </summary>
    internal const int MaxChunkCount = int.MaxValue - 1;

    /// <summary>
    /// Chunks <paramref name="pageCount"/> pages of a segment hold — <paramref name="rootChunks"/> on the root, <paramref name="chunksPerPage"/> on every
    /// other — in <c>long</c>: the product overflows an <c>int</c> before the chunk-id space does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long CapacityOf(int rootChunks, int chunksPerPage, long pageCount) => rootChunks + (pageCount - 1) * chunksPerPage;

    /// <summary>The largest page count whose chunks stay addressable by an <c>int</c> chunk id (<see cref="MaxChunkCount"/>).</summary>
    internal static int MaxPageCountOf(int rootChunks, int chunksPerPage)
        => (int)Math.Min(int.MaxValue, 1 + (MaxChunkCount - (long)rootChunks) / chunksPerPage);

    /// <summary><see cref="CapacityOf"/> as an <c>int</c>, or <see cref="ResourceExhaustedException"/> past <see cref="MaxChunkCount"/>.</summary>
    internal static int CheckedCapacityOf(int rootChunks, int chunksPerPage, int pageCount, int stride)
    {
        var capacity = CapacityOf(rootChunks, chunksPerPage, pageCount);
        if (capacity > MaxChunkCount)
        {
            ThrowCapacityOverflow(pageCount, capacity, stride);
        }

        return (int)capacity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeCapacityLong(long pageCount) => CapacityOf(_rootChunkCount, _otherChunkCount, pageCount);

    private int MaxPageCount => MaxPageCountOf(_rootChunkCount, _otherChunkCount);

    /// <summary>The pages that hold the first <paramref name="capacity"/> chunks: the inverse of <see cref="ComputeCapacity"/>.</summary>
    private int PagesCovering(int capacity)
        => capacity <= 0 ? 0 : capacity <= _rootChunkCount ? 1 : (int)_pageDivider.Divide((uint)(capacity - _rootChunkCount)) + 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeCapacity(int pageCount) => CheckedCapacityOf(_rootChunkCount, _otherChunkCount, pageCount, Stride);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCapacityOverflow(int pageCount, long capacity, int stride)
        => throw new ResourceExhaustedException(
            $"A chunk-based segment of {pageCount:N0} pages at stride {stride} would hold {capacity:N0} chunks, past the {MaxChunkCount:N0} an int chunk id "
            + "can address.",
            "Storage/ChunkBasedSegment/Capacity", ResourceType.Memory, capacity, MaxChunkCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PageOffsetToChunkIndex(int pageIndex, int chunkInPage)
    {
        if (pageIndex == 0)
        {
            return chunkInPage;
        }
        return _rootChunkCount + (pageIndex - 1) * _otherChunkCount + chunkInPage;
    }

    /// <summary>
    /// Counts allocated (set) bits in L0 bitmap words, masking invalid bits in the last word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountAllocatedBits(ReadOnlySpan<long> metadata, int bitmapLongs, int maxChunks)
    {
        var popcount = 0;
        for (int w = 0; w < bitmapLongs; w++)
        {
            var word = metadata[w];
            if (w == bitmapLongs - 1)
            {
                var validBits = maxChunks - w * 64;
                if (validBits < 64)
                {
                    word &= (1L << validBits) - 1;
                }
            }
            popcount += BitOperations.PopCount((ulong)word);
        }
        return popcount;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ChunkAccessor factory and warm caches
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create an ChunkAccessor using the stored PagedMMF and EpochManager references.
    /// </summary>
    [AllowCopy]
    [return: TransfersOwnership]
    internal ChunkAccessor<TStore> CreateChunkAccessor(ChangeSet changeSet = null) => new(this, _store, _epochManager, changeSet);

    /// <summary>
    /// A read-only accessor for a scan whose length grows with the data — a query over a whole archetype, an index range (EP-02). It holds at most its
    /// window's 32 pages, never tags them with the caller's epoch, and an address it returns is valid only until its next window miss: see
    /// <see cref="ChunkAccessor{TStore}"/>, scan mode. An epoch-tagged accessor would keep every page the scan touched until the caller's scope ends,
    /// which a transaction holds until it is disposed.
    /// </summary>
    internal ChunkAccessor<TStore> CreateScanAccessor() => new(this, _store, _epochManager, null, scan: true);

    /// <summary>
    /// Single-entry thread-local cache for warm <see cref="ChunkAccessor{TStore}"/> reuse.
    /// Keeps the 16-entry SIMD page cache warm across repeated BTree operations on the same segment.
    /// </summary>
    private sealed class WarmAccessorCache
    {
        internal ChunkAccessor<TStore> Accessor;       // warm accessor
        internal ChunkBasedSegment<TStore> Segment;    // which segment this accessor belongs to
        internal long Epoch;                   // GlobalEpoch at creation time
        internal bool IsRented;                // debug guard against double-rent
        internal bool SuppressCommitChanges;   // batch mode: skip CommitChanges on return

        [ThreadStatic]
        // ReSharper disable once InconsistentNaming
        private static WarmAccessorCache _instance;
        internal static WarmAccessorCache Instance => _instance ??= new();
    }

    /// <summary>
    /// Rents a warm <see cref="ChunkAccessor{TStore}"/> from the thread-local cache.
    /// On cache hit (same segment + same epoch): swaps ChangeSet only (~1ns).
    /// On cache miss: disposes old, creates new.
    /// Must be paired with <see cref="ReturnWarmAccessor"/> in a finally block.
    /// </summary>
    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ChunkAccessor<TStore> RentWarmAccessor(ChangeSet changeSet = null)
    {
        var cache = WarmAccessorCache.Instance;
        Debug.Assert(!cache.IsRented, "double-rent (missing ReturnWarmAccessor?)");

        var currentEpoch = _epochManager.GlobalEpoch;
        if (cache.Segment == this && cache.Epoch == currentEpoch)
        {
            // Hot path: swap ChangeSet only, page cache stays warm
            cache.Accessor.ChangeSet = changeSet;
            cache.IsRented = true;
            return ref cache.Accessor;
        }

        // Cold path: different segment or epoch changed
        if (cache.Segment != null)
        {
            cache.Accessor.Dispose();
        }
        cache.Accessor = new ChunkAccessor<TStore>(this, _store, _epochManager, changeSet);
        cache.Segment = this;
        cache.Epoch = currentEpoch;
        cache.IsRented = true;
        return ref cache.Accessor;
    }

    /// <summary>
    /// Returns a warm <see cref="ChunkAccessor{TStore}"/> to the thread-local cache.
    /// Flushes dirty pages via <see cref="ChunkAccessor{TStore}.CommitChanges"/> but does NOT dispose —
    /// keeps the 16-entry SIMD page cache warm for the next operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReturnWarmAccessor()
    {
        var cache = WarmAccessorCache.Instance;
        Debug.Assert(cache.IsRented, "return without rent");
        if (!cache.SuppressCommitChanges)
        {
            cache.Accessor.CommitChanges();  // flush dirty pages, preserve page cache
        }
        else
        {
            // Batch mode: drain eviction queue only (prevents overflow).
            // Live dirty flags stay set → ACW > 0 → blocks checkpoint (safe under holdoff).
            cache.Accessor.FlushDeferredEvictions();
        }
        cache.IsRented = false;
        // Do NOT Dispose — keep the page cache warm
    }

    /// <summary>
    /// Updates the warm accessor cache's epoch to match the new GlobalEpoch.
    /// Called after <see cref="EpochManager.RefreshScope"/> within a transaction — the accessor's
    /// slot cache remains valid (FilePageIndex validation catches stale slots), so we avoid
    /// the costly cold-path that would re-stamp all hot pages via RequestPageEpoch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void RefreshWarmCacheEpoch(long newEpoch)
    {
        var cache = WarmAccessorCache.Instance;
        cache.Epoch = newEpoch;
        var sibCache = WarmSiblingAccessorCache.Instance;
        sibCache.Epoch = newEpoch;
    }

    /// <summary>
    /// Enters batch mode: suppresses <see cref="ChunkAccessor{TStore}.CommitChanges"/> on warm accessor return.
    /// During batch mode, <c>ActiveChunkWriters</c> stays &gt; 0 on dirty pages, which is safe — the commit
    /// runs under holdoff and completes in bounded time. Call <see cref="ExitBatchMode"/> to flush.
    /// </summary>
    internal static void EnterBatchMode()
    {
        WarmAccessorCache.Instance.SuppressCommitChanges = true;
        WarmSiblingAccessorCache.Instance.SuppressCommitChanges = true;
    }

    /// <summary>
    /// Exits batch mode: re-enables <see cref="ChunkAccessor{TStore}.CommitChanges"/> on warm accessor return
    /// and performs a single flush of accumulated dirty pages on both warm accessor caches.
    /// </summary>
    internal static void ExitBatchMode()
    {
        var cache = WarmAccessorCache.Instance;
        cache.SuppressCommitChanges = false;
        if (cache.Segment != null)
        {
            cache.Accessor.CommitChanges();
        }

        var sibCache = WarmSiblingAccessorCache.Instance;
        sibCache.SuppressCommitChanges = false;
        if (sibCache.Segment != null)
        {
            sibCache.Accessor.CommitChanges();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Second warm accessor cache — for B+Tree sibling/horizontal navigation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Second thread-local warm accessor cache dedicated to B+Tree sibling (horizontal) navigation.
    /// Separating vertical (parent→child) and horizontal (sibling) page access prevents sibling traversal from evicting parent path pages from the 16-slot
    /// accessor cache. Parent pages stay pinned via SlotRefCount in the primary warm accessor while siblings are loaded into this accessor — doubling the
    /// effective working set from 16 to 32 pages.
    /// </summary>
    private sealed class WarmSiblingAccessorCache
    {
        internal ChunkAccessor<TStore> Accessor;
        internal ChunkBasedSegment<TStore> Segment;
        internal long Epoch;
        internal bool IsRented;
        internal bool SuppressCommitChanges;   // batch mode: skip CommitChanges on return

        [ThreadStatic]
        // ReSharper disable once InconsistentNaming
        private static WarmSiblingAccessorCache _instance;
        internal static WarmSiblingAccessorCache Instance => _instance ??= new();
    }

    [AllowCopy]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ChunkAccessor<TStore> RentWarmSiblingAccessor(ChangeSet changeSet = null)
    {
        var cache = WarmSiblingAccessorCache.Instance;
        Debug.Assert(!cache.IsRented, "double-rent sibling accessor (missing ReturnWarmSiblingAccessor?)");

        var currentEpoch = _epochManager.GlobalEpoch;
        if (cache.Segment == this && cache.Epoch == currentEpoch)
        {
            cache.Accessor.ChangeSet = changeSet;
            cache.IsRented = true;
            return ref cache.Accessor;
        }

        if (cache.Segment != null)
        {
            cache.Accessor.Dispose();
        }
        cache.Accessor = new ChunkAccessor<TStore>(this, _store, _epochManager, changeSet);
        cache.Segment = this;
        cache.Epoch = currentEpoch;
        cache.IsRented = true;
        return ref cache.Accessor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReturnWarmSiblingAccessor()
    {
        var cache = WarmSiblingAccessorCache.Instance;
        Debug.Assert(cache.IsRented, "return sibling without rent");
        if (!cache.SuppressCommitChanges)
        {
            cache.Accessor.CommitChanges();
        }
        else
        {
            cache.Accessor.FlushDeferredEvictions();
        }
        cache.IsRented = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Addressing
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public (int segmentIndex, int offset) GetChunkLocation(int index)
    {
        // Fast path: chunk is on root page (most common for small segments)
        if (index < _rootChunkCount)
        {
            return (0, index);
        }

        // Adjust index relative to non-root pages
        var adjusted = (uint)(index - _rootChunkCount);

        var pageIndex = (int)_pageDivider.Divide(adjusted);
        var offset = (int)(adjusted - (uint)pageIndex * (uint)_otherChunkCount);

        var resultPageIndex = pageIndex + 1;

        // Safety check: ensure the page index is within the segment's bounds
        // This catches cases where a chunk ID from a grown segment is accessed
        // through a stale reference or invalid chunk ID
        var segmentLength = Length;
        if (resultPageIndex >= segmentLength)
        {
            ThrowPageIndexOutOfSegment(index, resultPageIndex, segmentLength);
        }

        return (resultPageIndex, offset);
    }

    /// <summary>
    /// The out-of-range report of <see cref="GetChunkLocation"/>, kept out of line. That method is inlined under every <c>GetChunkAddress</c>, and
    /// inlined with it this message builder left a 40-byte interpolated-string handler — a managed reference — in each caller's frame, which a fully
    /// interruptible caller zeroes in its prologue on every call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowPageIndexOutOfSegment(int index, int resultPageIndex, int segmentLength)
    {
        var msg = $"ChunkBasedSegment.GetChunkLocation: Computed page index {resultPageIndex} >= segment length {segmentLength}. " +
            $"ChunkId={index}, rootChunkCount={_rootChunkCount}, otherChunkCount={_otherChunkCount}, " +
            $"Capacity={ChunkCapacity}. This may indicate accessing a chunk ID that was never allocated or segment corruption.";
        // Issue #297: let tests capture the descent trace that produced this bogus chunk-id BEFORE we throw.
        OlcDescentTrace.OnInvalidChunkId?.Invoke(index, msg);
        throw new InvalidOperationException(msg);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    public int Stride { get; }
    public int ChunkCountRootPage { get; }
    public int ChunkCountPerPage { get; }

    // Store property inherited from LogicalSegment<TStore>

    /// <summary>Byte offset from start of raw data to first chunk on the root page (includes index section + alignment padding).</summary>
    internal int RootChunkDataOffset => RootHeaderIndexSectionLength + _rootAlignmentPadding;

    /// <summary>Byte offset from start of raw data to first chunk on non-root pages (alignment padding only).</summary>
    internal int OtherChunkDataOffset => _otherAlignmentPadding;

    public int ChunkCapacity => Volatile.Read(ref _capacity);

    public int AllocatedChunkCount => _allocatedCount;
    public int FreeChunkCount => _capacity - _allocatedCount;

    /// <summary>
    /// Checks whether the given chunk index is marked as allocated in the occupancy bitmap.
    /// </summary>
    public bool IsChunkAllocated(int index)
    {
        var (pageIndex, chunkInPage) = GetChunkLocation(index);
        var wordIndex = chunkInPage >> 6;
        var mask = 1L << (chunkInPage & 0x3F);

        var epoch = _store.EpochManager.GlobalEpoch;
        var page = GetPage(pageIndex, epoch, out _);
        var data = page.MetadataReadOnly<long>();
        return (data[wordIndex] & mask) != 0L;
    }

}

/// <summary>
/// Divides a chunk index by a segment's chunks-per-page count exactly, for every 32-bit index, in one 64×64 multiply (#1204).
/// </summary>
/// <remarks>
/// <para>
/// The multiplier is <c>M = ⌈2⁶⁴ / d⌉</c> and the quotient the high 64 bits of <c>M · n</c> (Lemire, Kaser and Kurz, "Faster Remainder by Direct
/// Computation", 2019): exact for every <c>n &lt; 2³²</c> and <c>d &lt; 2³²</c>, because the fraction has 64 bits for a 32-bit numerator and a 32-bit divisor.
/// </para>
/// <para>
/// The 32-bit multiplier it replaces, <c>⌈2³² / d⌉</c> with <c>(n · m) &gt;&gt; 32</c>, is exact only while <c>n · (m · d − 2³²) &lt; 2³²</c>. Past that,
/// ids with remainder <c>d − 1</c> resolved to the next page at offset −1 — one stride before that page's chunk area, inside its header — from chunk
/// 6 100 999 at an 8-byte stride and 54 366 749 at the revision chains' 64.
/// </para>
/// </remarks>
internal readonly struct ChunkPageDivider
{
    private readonly ulong _multiplier;
    private readonly uint _divisor;

    internal ChunkPageDivider(int divisor)
    {
        if (divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "A chunks-per-page count is positive.");
        }

        _divisor = (uint)divisor;

        // ⌈2⁶⁴ / d⌉ = ⌊(2⁶⁴ − 1) / d⌋ + 1, which wraps to 0 for d = 1: that divisor is the identity and is handled in Divide.
        _multiplier = divisor == 1 ? 0 : ulong.MaxValue / (uint)divisor + 1;
    }

    /// <summary><paramref name="n"/> / the divisor, exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Divide(uint n) => _divisor == 1 ? n : (uint)Math.BigMul(_multiplier, n, out _);
}
