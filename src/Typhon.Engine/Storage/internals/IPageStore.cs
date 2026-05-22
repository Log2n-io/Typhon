using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Abstraction over page storage backends: persistent (MMF-backed) and transient (heap-backed).
/// Used as a struct generic constraint (<c>where TStore : struct, IPageStore</c>) on
/// <see cref="LogicalSegment{TStore}"/>, <see cref="ChunkBasedSegment{TStore}"/>,
/// <see cref="ChunkAccessor{TStore}"/>, <see cref="BTree{TKey,TStore}"/>, and
/// <see cref="PagedHashMap{TKey,TValue,TStore}"/>.
/// <para>
/// The JIT specializes per concrete struct — zero virtual dispatch at runtime.
/// <c>typeof(TStore)</c> branches in hot paths (e.g. ChunkAccessor) enable dead-code
/// elimination: dirty tracking and SIMD page cache are eliminated for TransientStore,
/// while PersistentStore generates identical assembly to the current non-generic code.
/// </para>
/// </summary>
[PublicAPI]
public unsafe interface IPageStore
{
    // ═══════════════════════════════════════════════════════════════════════
    // Page Access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Load a page into the cache (persistent) or resolve its address (transient), tagging it
    /// with the current epoch to prevent eviction.
    /// <para>Persistent: fetches from file, epoch-tags, waits for I/O, CRC-verifies.</para>
    /// <para>Transient: identity mapping (<c>memPageIndex = filePageIndex</c>), always succeeds.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool RequestPageEpoch(int filePageIndex, long epoch, out int memPageIndex);

    /// <summary>
    /// Like <see cref="RequestPageEpoch"/> but skips CRC verification. Used during segment growth
    /// where page content will be immediately overwritten.
    /// <para>Transient: identical to <see cref="RequestPageEpoch"/>.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool RequestPageEpochUnchecked(int filePageIndex, long epoch, out int memPageIndex);

    /// <summary>
    /// Get a typed <see cref="PageAccessor"/> for a resolved memory page.
    /// The PageAccessor provides type-safe access to header, metadata, and raw data regions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    PageAccessor GetPage(int memPageIndex);

    /// <summary>
    /// Get the raw base address of a resolved memory page (offset 0, includes header).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte* GetMemPageAddress(int memPageIndex);

    /// <summary>
    /// Get the raw data address of a resolved memory page (skips header, offset = PageHeaderSize).
    /// Used by ChunkAccessor for direct chunk pointer computation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte* GetMemPageRawDataAddress(int memPageIndex);

    /// <summary>
    /// Base address of the contiguous page cache. Used by ChunkAccessor to reverse-map
    /// a raw pointer back to a memPageIndex via pointer arithmetic.
    /// <para>Persistent: returns the MMF page cache base address.</para>
    /// <para>Transient: returns <c>null</c> (pages are non-contiguous). The <c>typeof(TStore)</c>
    /// branch in ChunkAccessor skips the reverse-map path for TransientStore.</para>
    /// </summary>
    byte* MemPagesBaseAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty Tracking
    // Persistent: coordinates with checkpoint (DirtyCounter, ActiveChunkWriters).
    // Transient: all no-ops — JIT eliminates empty bodies entirely.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Increment ActiveChunkWriters for a page. While ACW &gt; 0, checkpoint cannot
    /// snapshot this page. Called by ChunkAccessor.MarkSlotDirty on first dirty access.
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IncrementActiveChunkWriters(int memPageIndex);

    /// <summary>
    /// Decrement ActiveChunkWriters for a page. Called by ChunkAccessor.CommitChanges
    /// when dirty slots are flushed.
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DecrementActiveChunkWriters(int memPageIndex);

    /// <summary>
    /// Increment DirtyCounter for a page. Prevents eviction while DC &gt; 0.
    /// Called by ChangeSet on page registration (first dirty) and re-dirty (CP-04 safety).
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IncrementDirty(int memPageIndex);

    /// <summary>
    /// Atomically ensure DirtyCounter &gt;= <paramref name="minValue"/>. Used during segment
    /// growth to protect new pages against checkpoint race (value 2 for growth, 1 for maintenance).
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void EnsureDirtyAtLeast(int memPageIndex, int minValue);

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Ref Counting
    // Persistent: prevents page eviction while ChunkAccessor slots hold pointers.
    // Transient: no-ops — pages are always resident, never evicted.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Increment slot reference count. While SlotRefCount &gt; 0, the page cache will not
    /// evict this page. Called when ChunkAccessor loads a page into a slot.
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void IncrementSlotRefCount(int memPageIndex);

    /// <summary>
    /// Decrement slot reference count. Called when ChunkAccessor evicts or disposes a slot.
    /// <para>Transient: no-op.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void DecrementSlotRefCount(int memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Latching
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acquire exclusive latch on a page. Used during segment growth (page initialization)
    /// and by VSBS for structural modifications.
    /// <para>Persistent: full state machine transition (Idle → Exclusive), seqlock bump.</para>
    /// <para>Transient: simple spinlock (mutual exclusion only, no seqlock/FPI).</para>
    /// </summary>
    bool TryLatchPageExclusive(int memPageIndex);

    /// <summary>
    /// Release exclusive latch on a page.
    /// </summary>
    void UnlatchPageExclusive(int memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Growth
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Allocate new pages for segment growth. Fills <paramref name="pageIds"/> from
    /// <paramref name="startFrom"/> onward with newly allocated page indices.
    /// <para>Persistent: allocates from MMF file page pool.</para>
    /// <para>Transient: allocates from heap blocks. Throws <see cref="InsufficientMemoryException"/>
    /// if <see cref="TransientOptions.MaxMemoryBytes"/> is exceeded.</para>
    /// </summary>
    void AllocatePages(ref Span<int> pageIds, int startFrom, ChangeSet changeSet);

    /// <summary>
    /// Create a <see cref="ChangeSet"/> for tracking dirty pages during growth or maintenance.
    /// <para>Persistent: returns <c>new ChangeSet(mmf)</c>.</para>
    /// <para>Transient: returns <c>null</c> (no dirty tracking needed). CBS/LS handle null ChangeSets.</para>
    /// </summary>
    ChangeSet CreateChangeSet();

    /// <summary>
    /// Map a resolved memory page index back to its file page index.
    /// <para>Persistent: looks up the file page index from the page cache slot.</para>
    /// <para>Transient: identity mapping (<c>memPageIndex == filePageIndex</c>).</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int GetFilePageIndex(int memPageIndex);

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The engine-wide epoch manager. Used for concurrent access protection:
    /// <para>Persistent: page eviction protection + chunk free deferral.</para>
    /// <para>Transient: chunk free deferral only (pages never evict).</para>
    /// </summary>
    EpochManager EpochManager
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    /// <summary>Whether the underlying store has been disposed.</summary>
    bool IsDisposed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }
}
