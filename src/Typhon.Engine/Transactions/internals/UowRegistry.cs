using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Persistent registry entry for a single UoW slot. Slot index = UoW ID.
/// </summary>
/// <remarks>
/// 40 bytes divides evenly into a page's 8000-byte data area (8000/40=200) — zero waste. With the directory-only root (v4)
/// the registry stores entries only on data pages (segment page 1+); the root page holds the segment's page directory.
/// <c>Free = 0</c> ensures that zeroed pages are automatically all-free entries.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UowRegistryEntry
{
    public UnitOfWorkState State;   // 1 byte
    public byte Reserved;           // 1 byte
    public short Reserved2;         // 2 bytes
    public int TransactionCount;    // 4 bytes
    public long CreatedTicks;       // 8 bytes
    public long CommittedTicks;     // 8 bytes
    public long MaxTSN;             // 8 bytes
    public long Reserved3;          // 8 bytes
    // Total: 40 bytes
}

/// <summary>
/// Persistent UoW ID allocator with crash-recovery support. Manages a flat array of <see cref="UowRegistryEntry"/> in a growing <see cref="LogicalSegment{PersistentStore}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Provides O(1) bitmap-based allocation of UoW IDs (1..32767), crash recovery by voiding Pending entries, and a committed bitmap for post-crash visibility filtering.
/// </para>
/// <para>
/// During normal operation, <see cref="CommittedBeforeTSN"/> is <c>long.MaxValue</c> — the committed bitmap is never touched. After crash recovery with
/// voided entries, it drops to 0, activating the bitmap as a visibility fallback until voided entries are cleaned up.
/// </para>
/// </remarks>
internal unsafe class UowRegistry : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    private  const int EntrySize        = 40;
    // Directory-only root (v4): the segment's root page holds only the page directory, so the registry stores no entries on
    // it — every data page (segment page 1+) holds the full 8000/40 = 200 entries. There is no longer a distinct (smaller)
    // root capacity.
    internal const int PerPageCapacity  = 200;      // 8000 / 40
    private  const int MaxUowId         = 32767;    // 15-bit max
    private  const int BitmapWords      = 512;      // 32768 / 64

    // ═══════════════════════════════════════════════════════════════
    // Fields
    // ═══════════════════════════════════════════════════════════════

    private readonly LogicalSegment<PersistentStore> _segment;
    private readonly ManagedPagedMMF _mmf;
    private readonly EpochManager _epochManager;

    /// <summary>
    /// Single pinned allocation holding both bitmaps contiguously:
    /// [allocationBitmap (512 ulongs)] [committedBitmap (512 ulongs)]
    /// </summary>
    private readonly PinnedMemoryBlock _bitmapBlock;

    /// <summary>Bit=1 means Free (available for allocation). Rebuilt on load.</summary>
    private readonly ulong* _allocationBitmap;

    /// <summary>Bit=1 means Committed or WalDurable. Used only after crash recovery.</summary>
    private readonly ulong* _committedBitmap;

    /// <summary>
    /// Visibility horizon. <c>long.MaxValue</c> during normal operation (bitmap never touched). <c>0</c> after crash recovery when voided entries
    /// exist (forces bitmap fallback).
    /// </summary>
    private long _committedBeforeTSN = long.MaxValue;

    private int _voidEntryCount;
    private int _activeCount;
    private int _currentCapacity;

    /// <summary>
    /// Semaphore signaled by <see cref="Release"/> when a slot becomes free.
    /// Waiters in <see cref="AllocateUowId(ref WaitContext, ChangeSet)"/> block on this semaphore instead of spin-polling.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="SemaphoreSlim"/> instead of <see cref="ManualResetEventSlim"/> to eliminate
    /// a signal-loss race: with MRES, a <see cref="ManualResetEventSlim.Set"/> call between
    /// <see cref="ManualResetEventSlim.Wait()"/> returning and <see cref="ManualResetEventSlim.Reset"/>
    /// in <c>finally</c> would permanently lose the signal. SemaphoreSlim atomically decrements the
    /// count on Wait — each Release() wakes exactly one waiter with no window for signal loss.
    /// Initial count = 0: callers scan the bitmap first (fast path), only wait if no slot is found.
    /// </remarks>
    private readonly SemaphoreSlim _slotFreed = new(0);

    // ═══════════════════════════════════════════════════════════════
    // Public Properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Visibility horizon for the read path. See design doc §6 CommittedBeforeTSN.</summary>
    public long CommittedBeforeTSN => _committedBeforeTSN;

    /// <summary>Number of UoW slots currently in use (Pending, WalDurable, Committed, or Void).</summary>
    public int ActiveCount => _activeCount;

    /// <summary>Total number of entries that fit in the current segment pages.</summary>
    public int CurrentCapacity => _currentCapacity;

    /// <summary>Number of voided entries (crash recovery).</summary>
    internal int VoidEntryCount => _voidEntryCount;

    /// <summary>
    /// Cumulative count of UoW slots allocated since engine start. Monotonic. Backs the profiler's per-tick "UoW created" gauge —
    /// viewer derives the per-tick delta by subtracting consecutive snapshots.
    /// </summary>
    internal long CreatedTotal => _createdTotal;

    /// <summary>Cumulative count of UoW slots committed (via <see cref="RecordCommit"/>) since engine start.</summary>
    internal long CommittedTotal => _committedTotal;

    private long _createdTotal;
    private long _committedTotal;

    /// <summary>Maximum number of concurrent UoW IDs (hard limit from 15-bit ID space).</summary>
    public int MaxConcurrentUoWs => MaxUowId;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    internal UowRegistry(LogicalSegment<PersistentStore> segment, ManagedPagedMMF mmf, EpochManager epochManager,
        IMemoryAllocator allocator, IResource parent)
    {
        _segment = segment;
        _mmf = mmf;
        _epochManager = epochManager;
        _currentCapacity = ComputeCapacity(segment.Length);

        // Single pinned allocation for both bitmaps: 2 x 512 ulongs = 8192 bytes, cache-line aligned.
        _bitmapBlock = allocator.AllocatePinned("UowRegistry-Bitmaps", parent, BitmapWords * sizeof(ulong) * 2, true, 64);
        _allocationBitmap = (ulong*)_bitmapBlock.DataAsPointer;
        _committedBitmap = _allocationBitmap + BitmapWords;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Allocates a unique UoW ID from the registry. Returns an ID in the range [1..32767].
    /// If no slot is available, waits for a slot to be freed (via <see cref="Release"/>) until the deadline expires.
    /// </summary>
    /// <param name="wc">
    /// Wait context with deadline and cancellation token. If <c>Unsafe.IsNullRef</c>, uses unbounded wait.
    /// </param>
    /// <param name="externalCs">
    /// Optional <see cref="ChangeSet"/> threaded into the entry initialization (and any segment growth it triggers) so dirty marks on the touched pages
    /// are tracked by the caller's unit of work. When <see langword="null"/>, a transient change set is created and committed internally.
    /// </param>
    /// <exception cref="ResourceExhaustedException">All slots are in use and the deadline expired.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was triggered while waiting.</exception>
    public ushort AllocateUowId(ref WaitContext wc, ChangeSet externalCs = null)
    {
        while (true)
        {
            var slot = TryClaimFreeSlot(externalCs);
            if (slot >= 0)
            {
                return (ushort)slot;
            }

            // No free slot — wait for Release() to signal the event
            if (!WaitForSlotFreed(ref wc))
            {
                ThrowHelper.ThrowResourceExhausted("Execution/UowRegistry/AllocateUowId", ResourceType.Service, _activeCount, MaxUowId);
            }
        }
    }

    /// <summary>
    /// Allocates a unique UoW ID with no timeout (immediate failure if full).
    /// </summary>
    /// <exception cref="ResourceExhaustedException">All slots are in use.</exception>
    public ushort AllocateUowId(ChangeSet externalCs = null)
    {
        var slot = TryClaimFreeSlot(externalCs);
        if (slot >= 0)
        {
            return (ushort)slot;
        }

        ThrowHelper.ThrowResourceExhausted("Execution/UowRegistry/AllocateUowId", ResourceType.Service, _activeCount, MaxUowId);
        return 0; // Unreachable
    }

    /// <summary>
    /// Scans the allocation bitmap for a free slot, claims it via CAS, and initializes the entry.
    /// Returns the slot index (1..32767) or -1 if no free slot is available.
    /// </summary>
    private int TryClaimFreeSlot(ChangeSet externalCs = null)
    {
        // Scan allocation bitmap for first Free slot (bit=1), starting from word 0.
        // Slot 0 is reserved (never set in allocation bitmap), so we always skip it.
        for (int w = 0; w < BitmapWords; w++)
        {
            var word = _allocationBitmap[w];
            if (word == 0)
            {
                continue;
            }

            // Found a word with at least one free bit
            var bit = BitOperations.TrailingZeroCount(word);
            var mask = 1UL << bit;

            // CAS to claim the slot
            var prev = Interlocked.And(ref _allocationBitmap[w], ~mask);
            if ((prev & mask) == 0)
            {
                // Another thread claimed this bit between our read and CAS — retry
                w--; // Re-scan this word
                continue;
            }

            var slotIndex = (w * 64) + bit;

            // Ensure capacity (may need to grow segment for higher slot indices)
            if (slotIndex >= _currentCapacity)
            {
                EnsureCapacity(slotIndex + 1);
            }

            // Initialize the entry on the page
            InitializeEntry(slotIndex, externalCs);

            Interlocked.Increment(ref _activeCount);
            // Cumulative allocation counter — monotonic. Profiler per-tick gauges derive the "created this tick" delta from this value.
            Interlocked.Increment(ref _createdTotal);
            return slotIndex;
        }

        return -1;
    }

    /// <summary>
    /// Waits for a registry slot to become free, using the <see cref="_slotFreed"/> semaphore.
    /// Returns <c>true</c> if signaled (a slot was freed), <c>false</c> on timeout or cancellation.
    /// </summary>
    private bool WaitForSlotFreed(ref WaitContext wc)
    {
        try
        {
            if (Unsafe.IsNullRef(ref wc))
            {
                _slotFreed.Wait();
                return true;
            }

            var ms = wc.Deadline.RemainingMilliseconds;
            if (ms == 0)
            {
                return false;
            }

            return _slotFreed.Wait(ms, wc.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        // No finally { Reset() } — SemaphoreSlim auto-consumes the signal on Wait()
    }

    /// <summary>
    /// Releases a UoW ID back to the pool. The slot becomes Free and available for reuse.
    /// </summary>
    public void Release(ushort uowId, ChangeSet externalCs = null)
    {
        if (uowId == 0 || _disposed)
        {
            return;
        }

        bool wasVoid;

        // Write Free state to the page
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            var entry = ReadEntry(uowId, guard.Epoch);
            wasVoid = entry.State == UnitOfWorkState.Void;
            WriteEntryState(uowId, UnitOfWorkState.Free, guard.Epoch, externalCs);
        }

        // Clear committed bitmap bit
        var wordIndex = uowId >> 6;
        var bitMask = 1UL << (uowId & 63);
        Interlocked.And(ref _committedBitmap[wordIndex], ~bitMask);

        // Set allocation bitmap bit (slot is free again)
        Interlocked.Or(ref _allocationBitmap[wordIndex], bitMask);

        Interlocked.Decrement(ref _activeCount);

        if (wasVoid)
        {
            var remaining = Interlocked.Decrement(ref _voidEntryCount);
            if (remaining == 0)
            {
                _committedBeforeTSN = long.MaxValue;
            }
        }

        // Wake any threads waiting in AllocateUowId() for a free slot
        _slotFreed.Release();
    }

    /// <summary>
    /// Checks whether a UoW ID is in a committed state (Committed or WalDurable). Used by the visibility check as a crash-recovery fallback (Layer 4).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCommitted(ushort uowId) => (_committedBitmap[uowId >> 6] & (1UL << (uowId & 63))) != 0;

    /// <summary>
    /// Records that a UoW has committed. Transitions to Committed state and sets the committed bitmap bit.
    /// </summary>
    /// <remarks>
    /// Without WAL (#53), state goes directly to Committed (skipping WalDurable).
    /// </remarks>
    public void RecordCommit(ushort uowId, long maxTSN, ChangeSet externalCs = null)
    {
        if (uowId == 0)
        {
            return;
        }

        using (var guard = EpochGuard.Enter(_epochManager))
        {
            WriteEntryFields(uowId, guard.Epoch, entry =>
            {
                entry.State = UnitOfWorkState.Committed;
                entry.CommittedTicks = Stopwatch.GetTimestamp();
                entry.MaxTSN = maxTSN;
                return entry;
            }, externalCs);
        }

        // Set committed bitmap bit
        var wordIndex = uowId >> 6;
        var bitMask = 1UL << (uowId & 63);
        Interlocked.Or(ref _committedBitmap[wordIndex], bitMask);

        // Cumulative commit counter for the profiler's per-tick gauge derivation.
        Interlocked.Increment(ref _committedTotal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes the registry for a freshly created database. Sets all bitmap bits except slot 0.
    /// </summary>
    internal void Initialize()
    {
        _currentCapacity = ComputeCapacity(_segment.Length);

        // Mark all slots as Free in the allocation bitmap (bit=1 means Free) Slot 0 is reserved — keep bit 0 cleared
        _allocationBitmap[0] = ~1UL; // All bits set except bit 0
        for (int i = 1; i < BitmapWords; i++)
        {
            _allocationBitmap[i] = ulong.MaxValue;
        }

        _activeCount = 0;
        _voidEntryCount = 0;
        _committedBeforeTSN = long.MaxValue;
    }

    /// <summary>
    /// Loads the registry from an existing database file. Reads entries from pages, rebuilds bitmaps, and voids any Pending entries (crash recovery).
    /// This is the legacy non-WAL path — voids all Pending entries immediately.
    /// </summary>
    internal void LoadFromDisk()
    {
        LoadFromDiskRaw();
        VoidRemainingPending();
    }

    /// <summary>
    /// Loads the registry from disk but preserves Pending entries as-is (does NOT void them).
    /// Call <see cref="PromoteToWalDurable"/> for WAL-confirmed UoWs, then <see cref="VoidRemainingPending"/> to void the rest.
    /// </summary>
    internal void LoadFromDiskRaw()
    {
        _currentCapacity = ComputeCapacity(_segment.Length);

        // Clear both bitmaps (single contiguous block)
        _bitmapBlock.DataAsSpan.Clear();

        _activeCount = 0;
        _voidEntryCount = 0;

        // Slot 0 is always reserved (never free)
        // _allocationBitmap[0] bit 0 stays 0

        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;

        // Scan all entries up to current capacity
        for (int slotIndex = 1; slotIndex < _currentCapacity; slotIndex++)
        {
            var entry = ReadEntry(slotIndex, epoch);

            switch (entry.State)
            {
                case UnitOfWorkState.Free:
                    // Mark as free in allocation bitmap
                    SetAllocationBit(slotIndex);
                    break;

                case UnitOfWorkState.Pending:
                    // Keep Pending — WAL recovery will determine fate
                    _activeCount++;
                    // NOT in allocation bitmap (slot is occupied)
                    // NOT in committed bitmap (not yet confirmed)
                    break;

                case UnitOfWorkState.WalDurable:
                    // Survived crash — keep as-is, mark as committed in bitmap
                    SetCommittedBit(slotIndex);
                    _activeCount++;
                    break;

                case UnitOfWorkState.Committed:
                    // Fully committed — mark in committed bitmap
                    SetCommittedBit(slotIndex);
                    _activeCount++;
                    break;

                case UnitOfWorkState.Void:
                    // Already voided from a previous recovery — count it
                    _voidEntryCount++;
                    _activeCount++;
                    break;
            }
        }

        // Set remaining slots (beyond current capacity) as free
        for (int slotIndex = _currentCapacity; slotIndex <= MaxUowId; slotIndex++)
        {
            SetAllocationBit(slotIndex);
        }
    }

    /// <summary>
    /// Promotes a Pending UoW to WalDurable after WAL scan confirms it has a commit marker. Sets the committed bitmap bit so the read path treats
    /// its revisions as visible.
    /// </summary>
    /// <param name="uowId">The UoW ID to promote. Must currently be in Pending state.</param>
    internal void PromoteToWalDurable(ushort uowId)
    {
        if (uowId == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        var entry = ReadEntry(uowId, guard.Epoch);

        if (entry.State != UnitOfWorkState.Pending)
        {
            return; // Already promoted or in another state
        }

        WriteEntryState(uowId, UnitOfWorkState.WalDurable, guard.Epoch);
        SetCommittedBit(uowId);
    }

    /// <summary>
    /// Voids all remaining Pending entries after WAL recovery promotions are complete. Sets <see cref="CommittedBeforeTSN"/> based on whether any entries were voided.
    /// </summary>
    internal void VoidRemainingPending()
    {
        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;

        for (int slotIndex = 1; slotIndex < _currentCapacity; slotIndex++)
        {
            var entry = ReadEntry(slotIndex, epoch);
            if (entry.State == UnitOfWorkState.Pending)
            {
                WriteEntryState(slotIndex, UnitOfWorkState.Void, epoch);
                _voidEntryCount++;
            }
        }

        // Set CommittedBeforeTSN based on void entry count
        _committedBeforeTSN = (_voidEntryCount > 0) ? 0 : long.MaxValue;
    }

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint Support
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Transitions all <see cref="UnitOfWorkState.WalDurable"/> entries to <see cref="UnitOfWorkState.Committed"/>. Called by the Checkpoint Manager after
    /// data pages have been fsynced. The committed bitmap is already set for WalDurable entries (set during <see cref="PromoteToWalDurable"/>
    /// or <see cref="LoadFromDiskRaw"/>), so no bitmap update is needed.
    /// </summary>
    /// <returns>The number of entries transitioned.</returns>
    internal int TransitionWalDurableToCommitted()
    {
        int count = 0;
        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;

        for (int slotIndex = 1; slotIndex < _currentCapacity; slotIndex++)
        {
            var entry = ReadEntry(slotIndex, epoch);
            if (entry.State == UnitOfWorkState.WalDurable)
            {
                WriteEntryState(slotIndex, UnitOfWorkState.Committed, epoch);
                count++;
            }
        }

        return count;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal Helpers — Page Addressing
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a registry entry from the persistent page. Caller must be inside an EpochGuard.
    /// </summary>
    internal UowRegistryEntry ReadEntry(int slotIndex, long epoch)
    {
        var (segPageIndex, itemIndex) = LogicalSegment<PersistentStore>.GetItemLocation(slotIndex, EntrySize);
        var page = _segment.GetPage(segPageIndex, epoch, out _);
        var byteOffset = (page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0) + (itemIndex * EntrySize);
        return page.RawDataReadOnly<UowRegistryEntry>(byteOffset, 1)[0];
    }

    /// <summary>
    /// Acquires an exclusive latch on a segment page with spin-retry for concurrent access. Unlike <see cref="LogicalSegment{PersistentStore}.GetPageExclusive"/>
    /// which asserts on latch failure, this method handles contention from concurrent AllocateUowId/Release calls on the same page.
    /// </summary>
    private PageAccessor LatchPageExclusive(int segPageIndex, long epoch, out int memPageIdx)
    {
        _mmf.RequestPageEpoch(_segment.Pages[segPageIndex], epoch, out memPageIdx);
        while (!_mmf.TryLatchPageExclusive(memPageIdx))
        {
            Thread.SpinWait(1);
            _mmf.RequestPageEpoch(_segment.Pages[segPageIndex], epoch, out memPageIdx);
        }
        return _mmf.GetPage(memPageIdx);
    }

    /// <summary>
    /// Writes the State field of a registry entry to the persistent page. Caller must be inside an EpochGuard.
    /// </summary>
    private void WriteEntryState(int slotIndex, UnitOfWorkState newState, long epoch, ChangeSet externalCs = null)
    {
        var (segPageIndex, itemIndex) = LogicalSegment<PersistentStore>.GetItemLocation(slotIndex, EntrySize);
        var page = LatchPageExclusive(segPageIndex, epoch, out var memPageIdx);
        var byteOffset = (page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0) + (itemIndex * EntrySize);

        var cs = externalCs ?? _mmf.CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        var entries = page.RawData<UowRegistryEntry>(byteOffset, 1);
        entries[0].State = newState;

        _mmf.UnlatchPageExclusive(memPageIdx);

        // When using an external ChangeSet, the caller handles flushing (e.g., UoW.FlushAsync pipeline).
        if (externalCs == null)
        {
            cs.SaveChanges();
        }
    }

    /// <summary>
    /// Writes multiple fields of a registry entry using a transform delegate. Caller must be inside an EpochGuard.
    /// When <paramref name="externalCS"/> is supplied, the page mutation is registered against it and SaveChanges is left to the caller (matches the
    /// pattern in <see cref="WriteEntryState"/> + <see cref="InitializeEntry"/>). Used by RecordCommit on the per-tick UoW.Flush path so the registry
    /// page write piggybacks on the UoW's shared ChangeSet instead of triggering a synchronous disk write+fsync on the TickDriver thread.
    /// </summary>
    private void WriteEntryFields(int slotIndex, long epoch, Func<UowRegistryEntry, UowRegistryEntry> transform, ChangeSet externalCS = null)
    {
        var (segPageIndex, itemIndex) = LogicalSegment<PersistentStore>.GetItemLocation(slotIndex, EntrySize);
        var page = LatchPageExclusive(segPageIndex, epoch, out var memPageIdx);
        var byteOffset = (page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0) + (itemIndex * EntrySize);

        var cs = externalCS ?? _mmf.CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        var entries = page.RawData<UowRegistryEntry>(byteOffset, 1);
        entries[0] = transform(entries[0]);

        _mmf.UnlatchPageExclusive(memPageIdx);

        // When using an external ChangeSet, the caller (UoW.Flush via RecordCommit) handles flushing through the UoW's lifecycle.
        if (externalCS == null)
        {
            cs.SaveChanges();
        }
    }

    /// <summary>
    /// Initializes a freshly allocated entry on the persistent page.
    /// </summary>
    private void InitializeEntry(int slotIndex, ChangeSet externalCs = null)
    {
        using var guard = EpochGuard.Enter(_epochManager);
        var epoch = guard.Epoch;
        var (segPageIndex, itemIndex) = LogicalSegment<PersistentStore>.GetItemLocation(slotIndex, EntrySize);
        var page = LatchPageExclusive(segPageIndex, epoch, out var memPageIdx);
        var byteOffset = (page.IsRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0) + (itemIndex * EntrySize);

        var cs = externalCs ?? _mmf.CreateChangeSet();
        cs.AddByMemPageIndex(memPageIdx);

        var entries = page.RawData<UowRegistryEntry>(byteOffset, 1);
        entries[0] = new UowRegistryEntry
        {
            State = UnitOfWorkState.Pending,
            CreatedTicks = Stopwatch.GetTimestamp(),
            TransactionCount = 0,
            CommittedTicks = 0,
            MaxTSN = 0
        };

        _mmf.UnlatchPageExclusive(memPageIdx);

        // When using an external ChangeSet, the caller handles flushing.
        if (externalCs == null)
        {
            cs.SaveChanges();
        }
    }

    /// <summary>
    /// Ensures the segment has enough pages to store entries up to the given capacity.
    /// </summary>
    private void EnsureCapacity(int needed)
    {
        if (needed <= _currentCapacity)
        {
            return;
        }

        // Calculate pages needed: 1 directory root (holds no entries) + ceil(needed / PerPageCapacity) data pages.
        var pagesNeeded = 1 + ((needed + PerPageCapacity - 1) / PerPageCapacity);

        if (pagesNeeded > _segment.Length)
        {
            _segment.Grow(pagesNeeded, true);
        }

        _currentCapacity = ComputeCapacity(_segment.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // Bitmap Helpers
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAllocationBit(int slotIndex)
    {
        var wordIndex = slotIndex >> 6;
        var bitMask = 1UL << (slotIndex & 63);
        _allocationBitmap[wordIndex] |= bitMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetCommittedBit(int slotIndex)
    {
        var wordIndex = slotIndex >> 6;
        var bitMask = 1UL << (slotIndex & 63);
        _committedBitmap[wordIndex] |= bitMask;
    }

    /// <summary>
    /// Computes entry capacity for a given number of segment pages.
    /// </summary>
    private static int ComputeCapacity(int pageCount)
    {
        if (pageCount <= 1)
        {
            return 0; // only the directory root (or nothing) — holds no entries
        }

        // Directory-only root (v4): page 0 is the page directory; each data page holds PerPageCapacity entries.
        return (pageCount - 1) * PerPageCapacity;
    }

    // ═══════════════════════════════════════════════════════════════
    // Test Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Returns the allocation bitmap as a span for test manipulation.</summary>
    internal Span<ulong> AllocationBitmapSpan => new(_allocationBitmap, BitmapWords);

    // ═══════════════════════════════════════════════════════════════
    // Size Validation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compile-time size validation. Called from tests to verify struct layout.
    /// </summary>
    internal static int EntryStructSize => sizeof(UowRegistryEntry);

    // ═══════════════════════════════════════════════════════════════
    // IDisposable
    // ═══════════════════════════════════════════════════════════════

    // Registry does not own the segment — DatabaseEngine manages segment lifecycle
    private volatile bool _disposed;
    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        _disposed = true;
        _slotFreed.Dispose();
        _bitmapBlock.Dispose();
    }
}
