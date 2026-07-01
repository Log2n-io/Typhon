using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Epoch-protected chunk accessor with pure SOA layout and SIMD-optimized search.
/// Replaces ref-counted page access with epoch-based protection for page lifetime.
/// Always pass by ref to avoid copies.
/// </summary>
/// <remarks>
/// <para><b>Three-tier hot path:</b></para>
/// <list type="number">
///   <item>MRU check — branch-prediction-friendly for repeated access to same page</item>
///   <item>SIMD Vector256 search — parallel scan of all <see cref="Capacity"/> cached page indices</item>
///   <item>Clock-hand eviction — O(1) amortized, cannot fail (no pinned slots)</item>
/// </list>
/// <para>Pages are protected from eviction by their epoch tag, not by ref-counting.
/// Dirty tracking uses a bitmask flushed to <see cref="ChangeSet"/> via
/// <see cref="ChangeSet.AddByMemPageIndex"/>.</para>
/// </remarks>
[NoCopy(Reason = "struct with mutable SIMD cache and epoch-pinned pages")]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor<TStore> : IDisposable where TStore : struct, IPageStore
{
    // === SOA layout for SIMD search (2 cache lines) ===
    private fixed int _pageIndices[32];        // 128 bytes — segment page indices, SIMD searchable

    // === Base addresses for direct pointer arithmetic (4 cache lines) ===
    private fixed long _baseAddresses[32];     // 256 bytes — raw data address per slot

    // === Compact state ===
    private uint _dirtyFlags;                  // 4 bytes — bitmask of dirty slots (one bit per slot, needs 32 bits)
    private byte _clockHand;                   // 1 byte — eviction cursor
    private byte _mruSlot;                     // 1 byte — most recently used slot
    private byte _usedSlots;                   // 1 byte — high water mark (0-32)

    // === Cached hot-path values ===
    private int _stride;                       // 4 bytes — chunk size in bytes
    private int _rootHeaderOffset;             // 4 bytes — root page: index section + alignment padding
    private int _otherHeaderOffset;            // 4 bytes — non-root pages: alignment padding

    // === References ===
    private ChunkBasedSegment<TStore> _segment;
    private ChangeSet _changeSet;
    private TStore _store;
    private EpochManager _epochManager;

    // === Base address for computing memPageIndex on-demand (saves 64 bytes vs storing _memPageIndices[16]) ===
    private byte* _memPagesBaseAddr;           // 8 bytes

    // === Constants ===
    // Per-accessor warm window. Sized so a worker's hot index/directory pages stay slot-resident across lookups whose
    // leaf/chain pages churn through the rest of the window — at 16 the hot pages were evicted between lookups, forcing
    // a per-access re-pin (SlotRefCount Interlocked inc/dec + AccessEpoch CAS) on shared PageInfo lines that bounced
    // across all cores and capped concurrent-read scaling. 32 doubles the clock-hand runway so they survive.
    private const int Capacity = 32;
    private const int CapacityMask = Capacity - 1;   // power-of-two window → branch-free clock-hand wrap
    private const int InvalidPageIndex = -1;


    public ChunkBasedSegment<TStore> Segment => _segment;

    /// <summary>
    /// The ChangeSet used for dirty page tracking. Internal setter allows BTree's warm accessor to switch ChangeSets between operations without full
    /// re-initialization.
    /// </summary>
    internal ChangeSet ChangeSet
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _changeSet;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _changeSet = value;
    }

    /// <summary>
    /// Compute memPageIndex from a slot's base address.
    /// This saves 64 bytes by not storing _memPageIndices[16].
    /// Cost: one subtraction + one shift (2-3 cycles) — only used in slow paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMemPageIndexFromSlot(int slot) =>
        // _baseAddresses[slot] points to raw data (after PageHeaderSize)
        // memPageIndex = (rawDataAddr - PageHeaderSize - _memPagesBaseAddr) / PageSize
        (int)(((byte*)_baseAddresses[slot] - PagedMMF.PageHeaderSize - _memPagesBaseAddr) >> PagedMMF.PageSizePow2);

    /// <summary>
    /// Create a new ChunkAccessor. All storage is stack-allocated — zero heap allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkAccessor(ChunkBasedSegment<TStore> segment, TStore store, EpochManager epochManager, ChangeSet changeSet = null)
    {
        Debug.Assert(epochManager.IsCurrentThreadInScope, "ChunkAccessor must be created inside an epoch scope");
        _segment = segment;
        _store = store;
        _epochManager = epochManager;
        _changeSet = changeSet;
        _mruSlot = 0;
        _usedSlots = 0;
        _clockHand = 0;
        _dirtyFlags = 0;
        _stride = segment.Stride;
        _rootHeaderOffset = segment.RootChunkDataOffset;
        _otherHeaderOffset = segment.OtherChunkDataOffset;
        _memPagesBaseAddr = store.MemPagesBaseAddress;

        // Initialize page indices to invalid (-1). Other arrays are zero-initialized by struct init.
        fixed (int* pageIndices = _pageIndices)
        {
            Unsafe.InitBlockUnaligned(pageIndices, 0xFF, Capacity * sizeof(int));
        }

    }

    // ═══════════════════════════════════════════════════════════════════════
    // Eager dirty tracking
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks a slot dirty and signals the page as actively being written.
    /// <para>
    /// Three protections are applied on the 0→1 dirty transition for each slot:
    /// <list type="number">
    ///   <item><b>ActiveChunkWriters</b>: Atomically incremented so that <see cref="PagedMMF.WritePagesForCheckpoint"/>
    ///   skips this page (CAS sentinel). This prevents checkpoint from capturing a snapshot with partially-written
    ///   B+Tree data (e.g., a node with odd OLC version).</item>
    ///   <item><b>ChangeSet</b>: Eagerly registered via <see cref="ChangeSet.AddByMemPageIndex"/> so that
    ///   dirty pages are tracked for writeback. On first registration, ChangeSet itself bumps DirtyCounter via
    ///   <see cref="PagedMMF.IncrementDirty"/>.</item>
    ///   <item><b>DirtyCounter guard (CP-04)</b>: On re-registration (same page re-dirtied in a subsequent accessor
    ///   rental within the same UoW), <see cref="PagedMMF.IncrementDirty"/> unconditionally bumps DC. This handles
    ///   the race where checkpoint snapshots the page (Step 3) between two accessor rentals: the snapshot is stale,
    ///   so IncrementDirty ensures DC &gt; 1, surviving the pending DecrementDirty (Step 5).
    ///   <see cref="ChangeSet.ReleaseExcessDirtyMarks"/> caps DC at 1 when the UoW disposes.</item>
    /// </list>
    /// </para>
    /// ActiveChunkWriters is decremented in <see cref="CommitChanges"/> for live slots, and deferred
    /// via <see cref="ChangeSet.DeferEviction"/> for evicted dirty slots.
    /// SlotRefCount is deferred to ChangeSet for evicted slots (Transaction path) or decremented
    /// immediately (standalone path where epoch protection suffices).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkSlotDirty(int slot)
    {
        var mask = 1u << slot;
        if ((_dirtyFlags & mask) == 0)
        {
            _dirtyFlags |= mask;
            var memPageIndex = GetMemPageIndexFromSlot(slot);
            _store.IncrementActiveChunkWriters(memPageIndex);
            if (_changeSet != null)
            {
                if (_changeSet.AddByMemPageIndex(memPageIndex))
                {
                    // First registration: AddByMemPageIndex called IncrementDirty → DC≥1.
                    // ACW > 0 (set above) blocks checkpoint from snapshotting this page
                    // during the current write window.
                }
                else
                {
                    // Page already tracked by ChangeSet (re-dirtied in a subsequent accessor rental).
                    // Must IncrementDirty — NOT EnsureDirtyAtLeast — to satisfy CP-04:
                    //   If checkpoint snapshots the page between our previous CommitChanges (ACW→0)
                    //   and this re-dirty, the snapshot is stale. IncrementDirty pushes DC to ≥2,
                    //   so the pending DecrementDirty (Step 5) leaves DC≥1, keeping the page dirty
                    //   for the next checkpoint cycle which will capture our new modifications.
                    // Routed through ChangeSet.RegisterReDirty (was: direct _store.IncrementDirty) so the
                    // per-page mark count stays accurate — ReleaseExcessDirtyMarks then decrements the
                    // exact excess via the same conservation-respecting primitive as the checkpoint's
                    // own DecrementDirty, eliminating the cap-vs-decrement race captured in #385.
                    _changeSet.RegisterReDirty(memPageIndex);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Public API — chunk access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get mutable reference to chunk. The returned ref is valid for the lifetime of the
    /// enclosing <see cref="EpochGuard"/> — epoch protection prevents page eviction regardless
    /// of slot eviction within this accessor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T GetChunk<T>(int chunkId, bool dirty = false) where T : unmanaged
        => ref Unsafe.AsRef<T>(GetChunkAddress(chunkId, dirty));

    /// <summary>
    /// Get read-only reference to chunk. Safe for the lifetime of the enclosing <see cref="EpochGuard"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref readonly T GetChunkReadOnly<T>(int chunkId) where T : unmanaged => ref GetChunk<T>(chunkId);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Span<byte> GetChunkAsSpan(int index, bool dirtyPage = false) => new(GetChunkAddress(index, dirtyPage), _stride);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> GetChunkAsReadOnlySpan(int index) => new(GetChunkAddress(index), _stride);

    /// <summary>
    /// Loads and marks a chunk's page as dirty without returning a pointer.
    /// Used by B+Tree to pre-dirty pages before OLC TryWriteLock, ensuring ACW blocks checkpoint.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PreDirtyChunk(int chunkId) => GetChunkAddress(chunkId, true);

    internal void ClearChunk(int index)
    {
        // CRITICAL (#301): pass dirty:true so MarkSlotDirty fires before we write zeros. This sets ACW > 0 for the page, which blocks
        // CheckpointManager.WritePagesForCheckpoint from snapshotting it. Without this, a concurrent checkpoint could snapshot the page in a TORN state —
        // bitmap bit ALREADY set (post-AllocateChunk's Interlocked.Or, which marked the page dirty via my CP-04 fix) but content NOT YET CLEARED
        // (the Span.Clear below hasn't run / hasn't completed). That snapshot fsyncs to disk: bitmap=1 with stale content from when the chunk was previously
        // owned by something else. Once the snapshot's DC-- takes DC to 0, eviction can occur; reload-from-disk gets the stale content. The next caller that
        // walks a chain via this chunk's id reads garbage as if it were a header — the corruption cascade I've been chasing for two days.
        //
        // Setting ACW > 0 here ensures the clear is atomic w.r.t. checkpoint snapshots: the snapshot either runs entirely BEFORE this method (sees the chunk's
        // PREVIOUS content, but that's OK because the bitmap bit is also still =0 if FreeChunk's snapshot hasn't yet fsync'd) or entirely AFTER (sees cleared
        // content).
        var addr = GetChunkAddress(index, dirty: true);
        new Span<long>(addr, _stride / 8).Clear();
    }

    /// <summary>
    /// Mark a loaded chunk's slot as dirty without accessing the chunk data.
    /// </summary>
    internal void DirtyChunk(int index)
    {
        (int si, _) = _segment.GetChunkLocation(index);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(si);

            // Const-trip loop over the window in Vector256<int> (8-slot) strides — JIT-unrolled under AggressiveOptimization.
            for (int baseSlot = 0; baseSlot < Capacity; baseSlot += Vector256<int>.Count)
            {
                var v = Vector256.Load(indices + baseSlot);
                var mask = Vector256.Equals(v, target).ExtractMostSignificantBits();
                if (mask != 0)
                {
                    MarkSlotDirty(baseSlot + BitOperations.TrailingZeroCount(mask));
                    return;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty flush
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush dirty bitmask to ChangeSet and release ActiveChunkWriters signals.
    /// For each dirty slot: registers the page with <see cref="ChangeSet"/> (idempotent if already registered
    /// by <see cref="MarkSlotDirty"/>), then decrements <see cref="PagedMMF.DecrementActiveChunkWriters"/>
    /// so checkpoint can safely snapshot the page.
    /// Also flushes deferred eviction decrements from the ChangeSet (if present).
    /// </summary>
    public void CommitChanges()
    {
        // Decrement ACW for currently dirty slots
        if (_dirtyFlags != 0)
        {
            var flags = _dirtyFlags;
            while (flags != 0)
            {
                var bit = BitOperations.TrailingZeroCount(flags);
                var memPageIndex = GetMemPageIndexFromSlot(bit);
                _changeSet?.AddByMemPageIndex(memPageIndex);
                _store.DecrementActiveChunkWriters(memPageIndex);
                flags &= ~(1u << bit);
            }
            _dirtyFlags = 0;
        }

        // Flush deferred eviction decrements from ChangeSet (Transaction path only).
        // Standalone path (no ChangeSet) decrements immediately in EvictSlot.
        _changeSet?.FlushDeferredEvictions();
    }

    /// <summary>
    /// Drains the deferred eviction queue without processing live dirty flags.
    /// Used during batch mode: keeps ACW &gt; 0 on live dirty slots (blocks checkpoint)
    /// while preventing deferred eviction queue overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void FlushDeferredEvictions() => _changeSet?.FlushDeferredEvictions();

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive latch (for B+Tree node splits, etc.)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acquire exclusive latch on an epoch-protected page (Idle → Exclusive).
    /// The chunk must already be loaded into a slot.
    /// </summary>
    public bool TryLatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            for (int baseSlot = 0; baseSlot < Capacity; baseSlot += Vector256<int>.Count)
            {
                var v = Vector256.Load(indices + baseSlot);
                var mask = Vector256.Equals(v, target).ExtractMostSignificantBits();
                if (mask != 0)
                {
                    return _store.TryLatchPageExclusive(GetMemPageIndexFromSlot(baseSlot + BitOperations.TrailingZeroCount(mask)));
                }
            }
        }

        return false; // Page not cached
    }

    /// <summary>
    /// Release exclusive latch on an epoch-protected page (Exclusive → Idle).
    /// </summary>
    public void UnlatchExclusive(int chunkId)
    {
        (int pageIndex, _) = _segment.GetChunkLocation(chunkId);

        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            for (int baseSlot = 0; baseSlot < Capacity; baseSlot += Vector256<int>.Count)
            {
                var v = Vector256.Load(indices + baseSlot);
                var mask = Vector256.Equals(v, target).ExtractMostSignificantBits();
                if (mask != 0)
                {
                    _store.UnlatchPageExclusive(GetMemPageIndexFromSlot(baseSlot + BitOperations.TrailingZeroCount(mask)));
                    return;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Segment header access
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Access segment header (for internal ChunkBasedSegment operations).
    /// </summary>
    internal ref T GetChunkBasedSegmentHeader<T>(int offset, bool dirty) where T : unmanaged
    {
        // Page 0 is always the root page containing the header — ensure it's loaded
        GetChunkAddress(0, dirty);

        // _baseAddresses[_mruSlot] points to raw data area (after PageHeaderSize).
        // Walk back to page start to apply absolute offset.
        var rawDataAddr = (byte*)_baseAddresses[_mruSlot];
        var pageStart = rawDataAddr - PagedMMF.PageHeaderSize;
        return ref Unsafe.AsRef<T>(pageStart + offset);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HOT PATH: chunk address resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CRITICAL HOT PATH: Get chunk address with maximum performance.
    /// Three-tier optimization:
    /// 1. MRU check (branch prediction friendly for repeated access)
    /// 2. SIMD search (parallel scan of all slots)
    /// 3. Clock-hand eviction (O(1) amortized, cannot fail)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal byte* GetChunkAddress(int chunkId, bool dirty = false)
    {
        (int pageIndex, int offset) = _segment.GetChunkLocation(chunkId);

        // === ULTRA FAST PATH: MRU check ===
        var mru = _mruSlot;
        if (_pageIndices[mru] == pageIndex)
        {
            if (dirty)
            {
                MarkSlotDirty(mru);
            }

            var headerOffset = pageIndex == 0 ? _rootHeaderOffset : _otherHeaderOffset;
            return (byte*)_baseAddresses[mru] + headerOffset + offset * _stride;
        }

        // === FAST PATH: SIMD search through cache ===
        fixed (int* indices = _pageIndices)
        {
            var target = Vector256.Create(pageIndex);

            // Const-trip loop over the window in Vector256<int> (8-slot) strides — JIT-unrolled under AggressiveOptimization.
            for (int baseSlot = 0; baseSlot < Capacity; baseSlot += Vector256<int>.Count)
            {
                var v = Vector256.Load(indices + baseSlot);
                var mask = Vector256.Equals(v, target).ExtractMostSignificantBits();
                if (mask != 0)
                {
                    var slot = baseSlot + BitOperations.TrailingZeroCount(mask);
                    return GetFromSlot(slot, pageIndex, offset, dirty);
                }
            }
        }

        // === SLOW PATH: Cache miss — clock-hand eviction ===
        return LoadAndGet(pageIndex, offset, dirty);
    }

    /// <summary>
    /// Helper for SIMD search hit: update MRU, dirty flag, compute address.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* GetFromSlot(int slotIndex, int pageIndex, int offset, bool dirty)
    {
        _mruSlot = (byte)slotIndex;

        if (dirty)
        {
            MarkSlotDirty(slotIndex);
        }

        var headerOffset = pageIndex == 0 ? _rootHeaderOffset : _otherHeaderOffset;
        return (byte*)_baseAddresses[slotIndex] + headerOffset + offset * _stride;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SLOW PATH: eviction and page loading
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cache miss slow path: clock-hand eviction, load new page.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // Keep hot paths small
    private byte* LoadAndGet(int pageIndex, int offset, bool dirty)
    {
        var slot = FindEvictionSlot();
        EvictSlot(slot);
        LoadIntoSlot(slot, pageIndex);
        return GetFromSlot(slot, pageIndex, offset, dirty);
    }

    /// <summary>
    /// Clock-hand eviction: O(1) amortized, always succeeds (no pinning mechanism).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindEvictionSlot()
    {
        // Fast path: use unused slots first
        if (_usedSlots < Capacity)
        {
            return _usedSlots++;
        }

        // Clock-hand: advance, skip MRU
        var hand = (byte)((_clockHand + 1) & CapacityMask);
        if (hand == _mruSlot)
        {
            hand = (byte)((hand + 1) & CapacityMask);
        }
        _clockHand = hand;
        return hand;
    }

    /// <summary>
    /// Evict a slot: flush dirty state and handle page protection counters.
    /// <para>
    /// <b>Transaction path</b> (ChangeSet present): SlotRefCount and ACW decrements are deferred to
    /// <see cref="ChangeSet.DeferEviction"/> because <see cref="Transaction.FlushAndRefreshEpoch"/> can
    /// advance the epoch while callers still hold raw <c>byte*</c> or <c>ref T</c> pointers to evicted
    /// pages. SlotRefCount &gt; 0 prevents <see cref="PagedMMF.TryAcquire"/> from reusing the memory page.
    /// </para>
    /// <para>
    /// <b>Standalone path</b> (no ChangeSet): SlotRefCount is decremented immediately. Without a
    /// Transaction, no epoch refresh can occur during the accessor's lifetime, so epoch protection
    /// alone prevents page eviction. ACW is also decremented immediately for dirty slots.
    /// </para>
    /// </summary>
    private void EvictSlot(int slot)
    {
        if (_pageIndices[slot] == InvalidPageIndex)
        {
            return;
        }

        var memPageIndex = GetMemPageIndexFromSlot(slot);
        var mask = 1u << slot;

        if (_changeSet != null)
        {
            // Transaction path: defer all decrements to ChangeSet.
            if ((_dirtyFlags & mask) != 0)
            {
                _changeSet.AddByMemPageIndex(memPageIndex);
                _changeSet.DeferEviction(memPageIndex | unchecked((int)0x80000000));
                _dirtyFlags &= ~mask;
            }
            else
            {
                _changeSet.DeferEviction(memPageIndex);
            }
        }
        else
        {
            // Standalone path: decrement immediately (epoch protection is sufficient).
            if ((_dirtyFlags & mask) != 0)
            {
                _store.DecrementActiveChunkWriters(memPageIndex);
                _dirtyFlags &= ~mask;
            }
            _store.DecrementSlotRefCount(memPageIndex);
        }

        _pageIndices[slot] = InvalidPageIndex;
    }

    /// <summary>
    /// Load a page into a slot via epoch-protected access.
    /// Increments <see cref="PageInfo.SlotRefCount"/> to prevent PagedMMF from evicting the page
    /// while it is cached in this accessor (callers may hold raw pointers to the page memory).
    /// </summary>
    private void LoadIntoSlot(int slot, int pageIndex)
    {
        var pages = _segment.Pages;
        Debug.Assert((uint)pageIndex < (uint)pages.Length);

        var filePageIndex = pages[pageIndex];
        Debug.Assert(filePageIndex >= 0);

        // Resolve page memory through the LIVE segment store, not the accessor's by-value `_store` snapshot. `_store` is a copy taken at construction
        // (see ctor). For PersistentStore that copy is harmless — its pages live in the global page cache addressed via a stable base pointer, so a copy
        // aliases the same memory. TransientStore is different: its `_pageAddresses` array lives INSIDE the struct and AllocatePages REASSIGNS it to a larger
        // array when the store grows past its current capacity (first at PagesPerBlock×4 pages). A ChunkAccessor created before such a grow
        // — e.g. AllocateChunk(true)'s local accessor, which is snapshotted then immediately used to ClearChunk a freshly-grown page — would index the OLD,
        // now-undersized array and throw IndexOutOfRange. Reading the page address from `_segment.Store` (the canonical instance the segment grows in place)
        // always sees the current array. This is the cache-miss slow path, so the extra indirection is irrelevant; the cached raw pointers in `_baseAddresses`
        // (pinned transient blocks never move on grow) keep the hot path snapshot-free.
        ref var liveStore = ref _segment.Store;

        var result = liveStore.RequestPageEpoch(filePageIndex, _epochManager.GlobalEpoch, out var memPageIndex);
        Debug.Assert(result);

        _pageIndices[slot] = pageIndex;
        _baseAddresses[slot] = (long)liveStore.GetMemPageRawDataAddress(memPageIndex);

        // SlotRefCount prevents PagedMMF from evicting this page while the accessor holds a slot reference.
        // Deferred-decremented in EvictSlot, immediate-decremented in Dispose.
        liveStore.IncrementSlotRefCount(memPageIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispose accessor: flush dirty pages and release SlotRefCount for all live slots.
    /// </summary>
    public void Dispose()
    {
        if (_segment == null)
        {
            return;
        }

        // Guard against stale ThreadStatic warm cache: if the PagedMMF has been disposed
        // (e.g., previous test run on the same thread), the page cache no longer exists.
        // Skip all cleanup — SlotRefCount, ACW, dirty flags are meaningless for a disposed page cache.
        if (_store.IsDisposed)
        {
            _usedSlots = 0;
            _segment = null!;
            return;
        }

        CommitChanges();

        // Release SlotRefCount for live (non-evicted) slots.
        // Evicted slots were already handled by ChangeSet.FlushDeferredEvictions (Transaction path)
        // or decremented immediately in EvictSlot (standalone path).
        for (int i = 0; i < _usedSlots; i++)
        {
            if (_pageIndices[i] != InvalidPageIndex)
            {
                var memPageIndex = GetMemPageIndexFromSlot(i);
                _store.DecrementSlotRefCount(memPageIndex);
            }
        }

        _usedSlots = 0;
        _segment = null!;
    }
}
