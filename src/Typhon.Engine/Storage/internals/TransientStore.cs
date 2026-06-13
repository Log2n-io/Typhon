using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Heap-backed page store for transient (non-persistent) segments.
/// Pages are allocated from pinned memory blocks via <see cref="IMemoryAllocator"/>.
/// No file I/O, no dirty tracking, no page cache eviction.
/// <para>
/// All dirty-tracking and slot-ref-counting methods are empty bodies — the JIT eliminates
/// them entirely when used through a <c>where TStore : struct, IPageStore</c> constraint
/// with <c>typeof(TStore) == typeof(TransientStore)</c> branches.
/// </para>
/// <para>
/// Pages are allocated in blocks of <see cref="TransientOptions.PagesPerBlock"/> pages.
/// Each block is a single <see cref="IMemoryAllocator.AllocatePinned"/> call.
/// A hard memory cap (<see cref="TransientOptions.MaxMemoryBytes"/>) prevents runaway allocation.
/// </para>
/// </summary>
[PublicAPI]
internal unsafe struct TransientStore : IPageStore, IDisposable
{
    private readonly TransientOptions _options;
    private readonly IMemoryAllocator _allocator;
    private readonly EpochManager _epochManager;
    private readonly IResource _parent;

    private readonly int _pagesPerBlock;
    private readonly int _maxPages;

    // Page storage: blocks of pinned memory, each holding _pagesPerBlock pages
    private List<PinnedMemoryBlock> _blocks;
    private byte*[] _pageAddresses;

    // Per-page spinlock for exclusive latching (growth, VSBS structural mods)
    private int[] _pageLatchOwner;  // thread ID or 0 = unlocked
    private short[] _pageLatchDepth; // re-entrance depth

    private int _pageCount;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new transient page store.
    /// </summary>
    /// <param name="options">Configuration (memory cap, pages per block).</param>
    /// <param name="allocator">Memory allocator for pinned heap blocks.</param>
    /// <param name="epochManager">Engine-wide epoch manager (shared with persistent store).</param>
    /// <param name="parent">Parent resource for allocation tracking.</param>
    public TransientStore(TransientOptions options, IMemoryAllocator allocator, EpochManager epochManager, IResource parent)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(epochManager);
        ArgumentNullException.ThrowIfNull(parent);

        _options = options;
        _allocator = allocator;
        _epochManager = epochManager;
        _parent = parent;

        _pagesPerBlock = Math.Max(1, options.PagesPerBlock);
        _maxPages = (int)Math.Min(options.MaxMemoryBytes / PagedMMF.PageSize, int.MaxValue);

        _blocks = new List<PinnedMemoryBlock>();
        _pageAddresses = new byte*[_pagesPerBlock * 4]; // initial capacity
        _pageLatchOwner = new int[_pagesPerBlock * 4];
        _pageLatchDepth = new short[_pagesPerBlock * 4];

        _pageCount = 0;
        _isDisposed = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Page Access — identity mapping, pages always resident
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RequestPageEpoch(int filePageIndex, long epoch, out int memPageIndex)
    {
        memPageIndex = filePageIndex;
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RequestPageEpochUnchecked(int filePageIndex, long epoch, out int memPageIndex)
    {
        memPageIndex = filePageIndex;
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PageAccessor GetPage(int memPageIndex) => new(_pageAddresses[memPageIndex]);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* GetMemPageAddress(int memPageIndex) => _pageAddresses[memPageIndex];

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* GetMemPageRawDataAddress(int memPageIndex) => _pageAddresses[memPageIndex] + PagedMMF.PageHeaderSize;

    /// <inheritdoc />
    /// <remarks>Returns <c>null</c>: transient pages are non-contiguous (allocated in separate heap blocks).
    /// ChunkAccessor's reverse pointer-to-memPageIndex lookup uses a <c>typeof(TStore)</c> branch
    /// to skip this path for TransientStore.</remarks>
    public byte* MemPagesBaseAddress
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty Tracking — all no-ops (no persistence, no checkpoint)
    // JIT eliminates these entirely for TransientStore specializations.
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementActiveChunkWriters(int memPageIndex) { }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementActiveChunkWriters(int memPageIndex) { }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementDirty(int memPageIndex) { }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureDirtyAtLeast(int memPageIndex, int minValue) { }

    // ═══════════════════════════════════════════════════════════════════════
    // Slot Ref Counting — no-ops (pages are always resident, never evicted)
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IncrementSlotRefCount(int memPageIndex) { }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DecrementSlotRefCount(int memPageIndex) { }

    // ═══════════════════════════════════════════════════════════════════════
    // Latching — real spinlock (needed for concurrent B+Tree growth)
    // Lighter than persistent: no seqlock, no FPI, no state machine.
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool TryLatchPageExclusive(int memPageIndex)
    {
        var currentThreadId = Environment.CurrentManagedThreadId;

        // Re-entrance check
        if (_pageLatchOwner[memPageIndex] == currentThreadId)
        {
            _pageLatchDepth[memPageIndex]++;
            return true;
        }

        // Spin until acquired
        var sw = new SpinWait();
        while (Interlocked.CompareExchange(ref _pageLatchOwner[memPageIndex], currentThreadId, 0) != 0)
        {
            sw.SpinOnce();
        }

        _pageLatchDepth[memPageIndex] = 0;
        return true;
    }

    /// <inheritdoc />
    public void UnlatchPageExclusive(int memPageIndex)
    {
        if (_pageLatchDepth[memPageIndex] > 0)
        {
            _pageLatchDepth[memPageIndex]--;
            return;
        }

        Volatile.Write(ref _pageLatchOwner[memPageIndex], 0);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Growth — allocate heap-backed pages in blocks
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void AllocatePages(ref Span<int> pageIds, int startFrom, ChangeSet changeSet)
    {
        var pagesNeeded = pageIds.Length - startFrom;
        if (pagesNeeded <= 0)
        {
            return;
        }

        if (_pageCount + pagesNeeded > _maxPages)
        {
            throw new InsufficientMemoryException(
                $"Transient memory budget exceeded. Requested {pagesNeeded} pages " +
                $"({pagesNeeded * PagedMMF.PageSize / 1024} KB), " +
                $"current {_pageCount}/{_maxPages} pages. " +
                $"Increase TransientOptions.MaxMemoryBytes (current: {_options.MaxMemoryBytes / (1024 * 1024)} MB).");
        }

        // Grow arrays if needed
        var requiredCapacity = _pageCount + pagesNeeded;
        if (requiredCapacity > _pageAddresses.Length)
        {
            var newCapacity = Math.Max(requiredCapacity, _pageAddresses.Length * 2);

            // byte*[] can't use Array.Resize (pointer types not allowed as type args)
            var newAddresses = new byte*[newCapacity];
            for (var j = 0; j < _pageCount; j++)
            {
                newAddresses[j] = _pageAddresses[j];
            }

            _pageAddresses = newAddresses;

            Array.Resize(ref _pageLatchOwner, newCapacity);
            Array.Resize(ref _pageLatchDepth, newCapacity);
        }

        // Allocate pages, potentially across multiple blocks
        for (var i = 0; i < pagesNeeded; i++)
        {
            var pageIndex = _pageCount;

            // Allocate new block if current block is full or no blocks exist
            var blockIndex = pageIndex / _pagesPerBlock;
            var offsetInBlock = pageIndex % _pagesPerBlock;

            if (offsetInBlock == 0)
            {
                // Need a new block
                var blockSize = _pagesPerBlock * PagedMMF.PageSize;
                var block = _allocator.AllocatePinned($"TransientBlock-{blockIndex}", _parent, blockSize, true, 64); // cache-line aligned
                _blocks.Add(block);
            }

            // Compute page address within its block
            var currentBlock = _blocks[blockIndex];
            _pageAddresses[pageIndex] = currentBlock.DataAsPointer + (offsetInBlock * PagedMMF.PageSize);

            pageIds[startFrom + i] = pageIndex;
            _pageCount++;
        }
    }

    /// <inheritdoc />
    /// <remarks>Transient segments are never persisted, so they are not protected — no twin. JIT eliminates the stamp at the call site.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetOrAllocateDirectoryTwin(int primaryPageIndex, ChangeSet changeSet) => 0;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChangeSet CreateChangeSet() => null;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFilePageIndex(int memPageIndex) => memPageIndex;

    // ═══════════════════════════════════════════════════════════════════════
    // Infrastructure
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public EpochManager EpochManager
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _epochManager;
    }

    /// <inheritdoc />
    public bool IsDisposed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isDisposed;
    }

    /// <summary>
    /// Current number of allocated pages.
    /// </summary>
    public int PageCount => _pageCount;

    /// <summary>
    /// Maximum number of pages allowed (<see cref="TransientOptions.MaxMemoryBytes"/> / PageSize).
    /// </summary>
    public int MaxPages => _maxPages;

    /// <summary>
    /// Releases all pinned memory blocks. After disposal, the store cannot be used.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_blocks != null)
        {
            for (var i = 0; i < _blocks.Count; i++)
            {
                _blocks[i].Dispose();
            }

            _blocks.Clear();
        }

        _pageCount = 0;
    }
}
