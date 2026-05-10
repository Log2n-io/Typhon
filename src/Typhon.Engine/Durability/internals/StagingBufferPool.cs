using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Pre-allocated pool of page-sized (8KB) staging buffers for checkpoint and backup operations.
/// Buffers are rented via <see cref="Rent"/> and returned via <see cref="StagingBuffer.Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a bitmap free-list for O(1) slot acquisition and a <see cref="SemaphoreSlim"/> for
/// back-pressure when all buffers are rented. The entire buffer region is allocated as a single
/// contiguous pinned block with 4096-byte alignment (matching OS page size).
/// </para>
/// <para>
/// Thread-safe: multiple threads may rent and return buffers concurrently.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class StagingBufferPool : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Size of each staging buffer in bytes (one database page).</summary>
    public const int BufferSize = PagedMMF.PageSize; // 8192

    /// <summary>Minimum pool capacity (number of buffers).</summary>
    public const int MinCapacity = 16;

    /// <summary>Maximum pool capacity (number of buffers).</summary>
    public const int MaxCapacity = 4096;

    /// <summary>Default pool capacity when not specified.</summary>
    public const int DefaultCapacity = 512;

    private const int BufferAlignment = 4096;

    // ═══════════════════════════════════════════════════════════════════════
    // Memory
    // ═══════════════════════════════════════════════════════════════════════

    private readonly PinnedMemoryBlock _memoryBlock;
    private readonly byte* _basePointer;
    private readonly int _poolCapacity;

    // ═══════════════════════════════════════════════════════════════════════
    // Free-list bitmap (1 = free, 0 = rented)
    // ═══════════════════════════════════════════════════════════════════════

    private readonly ulong[] _freeMap;
    private readonly int _wordCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Back-pressure
    // ═══════════════════════════════════════════════════════════════════════

    private readonly SemaphoreSlim _available;

    // ═══════════════════════════════════════════════════════════════════════
    // Metrics
    // ═══════════════════════════════════════════════════════════════════════

    private long _totalRents;
    private int _currentRents;

    // ═══════════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════════

    private int _disposed;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new staging buffer pool with the specified capacity.
    /// </summary>
    /// <param name="allocator">Memory allocator for tracked, pinned buffer allocation.</param>
    /// <param name="parent">Parent resource for resource graph integration.</param>
    /// <param name="poolCapacity">
    /// Number of buffers in the pool. Clamped to [<see cref="MinCapacity"/>, <see cref="MaxCapacity"/>].
    /// </param>
    public StagingBufferPool(IMemoryAllocator allocator, IResource parent, int poolCapacity = DefaultCapacity)
        : base("StagingBufferPool", ResourceType.WAL, parent, ExhaustionPolicy.Wait)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(parent);

        // Clamp capacity to valid range
        _poolCapacity = Math.Clamp(poolCapacity, MinCapacity, MaxCapacity);

        // Allocate contiguous buffer region: poolCapacity × 8KB, 4096-byte aligned
        var totalSize = _poolCapacity * BufferSize;
        _memoryBlock = allocator.AllocatePinned("StagingBufferPool", this, totalSize, true, BufferAlignment);
        _basePointer = _memoryBlock.DataAsPointer;

        // Initialize free-list bitmap: all bits set = all free
        _wordCount = (_poolCapacity + 63) >> 6;
        _freeMap = new ulong[_wordCount];
        for (var i = 0; i < _wordCount; i++)
        {
            _freeMap[i] = ulong.MaxValue;
        }

        // Clear trailing bits beyond poolCapacity (they must never appear "free")
        var trailingBits = _poolCapacity & 0x3F;
        if (trailingBits != 0)
        {
            _freeMap[_wordCount - 1] = (1UL << trailingBits) - 1;
        }

        // Semaphore for back-pressure: initial count = pool capacity
        _available = new SemaphoreSlim(_poolCapacity, _poolCapacity);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Number of buffers in this pool.</summary>
    public int PoolCapacity => _poolCapacity;

    /// <summary>Number of buffers currently rented.</summary>
    public int CurrentRents => _currentRents;

    /// <summary>Peak concurrent rents observed since process start (reset via <see cref="ResetPeaks"/>). Exposed for the gauge subsystem.</summary>
    public long PeakRents { get; private set; }

    /// <summary>Cumulative total rent count since process start. Monotonic. Viewer derives per-tick rent rate as Δ/Δt.</summary>
    public long TotalRents => _totalRents;

    // ═══════════════════════════════════════════════════════════════════════
    // Rent / Return
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rents a staging buffer from the pool. Blocks if all buffers are currently rented.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait if the pool is exhausted.</param>
    /// <returns>A <see cref="StagingBuffer"/> that must be disposed to return it to the pool.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the pool has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the cancellation token fires while waiting.</exception>
    public StagingBuffer Rent(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Phase 8: Durability:Checkpoint:Backpressure span — covers the wait when the pool is exhausted.
        // The span's TraceContext (parent span) attributes the backpressure to the calling pipeline
        // (Checkpoint or Backup). Exhausted=1 only when the wait actually blocked (semaphore had no
        // free slot at entry).
        var poolWasExhausted = _available.CurrentCount == 0;
        var bpStart = poolWasExhausted ? Stopwatch.GetTimestamp() : 0L;
        var bpScope = poolWasExhausted ? TyphonEvent.BeginDurabilityCheckpointBackpressure(0, 1) : default;
        try
        {
            // Block until a buffer is available (back-pressure)
            _available.Wait(cancellationToken);
        }
        finally
        {
            if (poolWasExhausted)
            {
                bpScope.WaitMs = (uint)Math.Min((Stopwatch.GetTimestamp() - bpStart) * 1000L / Stopwatch.Frequency, uint.MaxValue);
                bpScope.Dispose();
            }
        }

        // Find and claim a free slot via bitmap scan
        var slotIndex = AcquireFreeSlot();

        // Update metrics
        Interlocked.Increment(ref _totalRents);
        var current = Interlocked.Increment(ref _currentRents);
        UpdatePeakConcurrent(current);

        var pointer = _basePointer + ((long)slotIndex * BufferSize);
        return new StagingBuffer(this, slotIndex, pointer, BufferSize);
    }

    /// <summary>
    /// Returns a buffer to the pool. Called by <see cref="StagingBuffer.Dispose"/>.
    /// </summary>
    /// <param name="slotIndex">Slot index to return.</param>
    internal void Return(int slotIndex)
    {
        // Set the free bit back
        var wordIndex = slotIndex >> 6;
        var mask = 1UL << (slotIndex & 0x3F);
        Interlocked.Or(ref _freeMap[wordIndex], mask);

        Interlocked.Decrement(ref _currentRents);

        // Release semaphore (unblocks a waiting Rent)
        _available.Release();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Free-list Bitmap Scan
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans the bitmap for the first free slot and atomically claims it.
    /// </summary>
    /// <returns>The index of the acquired slot.</returns>
    /// <remarks>
    /// The semaphore guarantees at least one bit is set, so this scan always succeeds.
    /// Uses <see cref="BitOperations.TrailingZeroCount(ulong)"/> (TZCNT/BSF on x86) for O(1) per word.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AcquireFreeSlot()
    {
        while (true)
        {
            for (var wordIndex = 0; wordIndex < _wordCount; wordIndex++)
            {
                var word = _freeMap[wordIndex];
                while (word != 0)
                {
                    var bitIndex = BitOperations.TrailingZeroCount(word);
                    var mask = 1UL << bitIndex;

                    // Atomically clear the free bit (claim the slot)
                    var previous = Interlocked.And(ref _freeMap[wordIndex], ~mask);
                    if ((previous & mask) != 0)
                    {
                        // We successfully claimed this slot
                        return (wordIndex << 6) + bitIndex;
                    }

                    // Another thread claimed it — re-read and try next bit
                    word = _freeMap[wordIndex];
                }
            }

            // Extremely rare: all visible bits were claimed between semaphore release and our scan.
            // The semaphore guarantees a slot exists, so spin briefly and retry.
            Thread.SpinWait(1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdatePeakConcurrent(int current)
    {
        // Simple racy max — acceptable for diagnostics
        if (current > PeakRents)
        {
            PeakRents = current;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteCapacity(_currentRents, _poolCapacity);
        writer.WriteThroughput("Rents", _totalRents);
    }

    /// <inheritdoc />
    public void ResetPeaks() => PeakRents = _currentRents;

    // ═══════════════════════════════════════════════════════════════════════
    // IDebugPropertiesProvider
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["Pool.Capacity"] = _poolCapacity,
            ["Pool.BufferSize"] = BufferSize,
            ["Pool.TotalBytes"] = (long)_poolCapacity * BufferSize,
            ["Rents.Current"] = _currentRents,
            ["Rents.Peak"] = PeakRents,
            ["Rents.Total"] = _totalRents,
            ["IsDisposed"] = _disposed != 0,
        };

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            ThrowObjectDisposed();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(StagingBufferPool));

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing)
        {
            // _memoryBlock is a child in the resource tree — base.Dispose(disposing) handles it.
            // Only dispose the SemaphoreSlim which is not part of the resource graph.
            // Guard against null: constructor may have thrown before _available was initialized.
            _available?.Dispose();
        }

        base.Dispose(disposing);
    }
}
