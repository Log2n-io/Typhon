using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Lock-free MPSC (Multiple-Producer, Single-Consumer) ring buffer for WAL record serialization. Multiple transaction commit threads claim space atomically
/// via <see cref="TryClaim"/>, write WAL records, then <see cref="Publish"/>. A single WAL Writer thread drains published frames via <see cref="TryDrain"/>
/// and <see cref="CompleteDrain"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses the Aeron-style linear buffer pattern (ADR-012):
/// <list type="bullet">
///   <item>Atomic tail increment via <c>Interlocked.Add</c> — zero-contention claiming</item>
///   <item>Two-phase claim-then-publish protocol with frame headers</item>
///   <item>Double-buffering (ping-pong) to avoid wrap-around complexity</item>
///   <item>Back-pressure when the buffer fills, with adaptive spin-wait</item>
/// </list>
/// </para>
/// <para>
/// Memory layout: Two buffers allocated via <see cref="IMemoryAllocator.AllocatePinned"/> for cache-line alignment. Total memory = 2 x <see cref="BufferCapacity"/>.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class WalCommitBuffer : IDisposable
{
    // ═══════════════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════════════

    private const int CacheLineSize = 64;
    private const int MinBufferCapacity = 64 * 1024; // 64 KB minimum

    // Swap states
    private const int SwapNormal = 0;
    private const int SwapRequested = 1;
    private const int SwapDraining = 2;

    // ═══════════════════════════════════════════════════════════════════════
    // Buffers (single pinned allocation, split into two halves)
    // ═══════════════════════════════════════════════════════════════════════

    private readonly PinnedMemoryBlock _memoryBlock;
    private readonly byte* _buffer0;
    private readonly byte* _buffer1;

    /// <summary>Size of each buffer in bytes.</summary>
    public int BufferCapacity { get; }

    // ═══════════════════════════════════════════════════════════════════════
    // Cache-line isolated state (prevents false sharing)
    // ═══════════════════════════════════════════════════════════════════════

    // --- Cache line 1: Producer-hot (Interlocked.Add target) ---
    private long _tailPosition;
    // Padding to fill cache line: 64 - 8 = 56 bytes
#pragma warning disable CS0169 // Field is never used — intentional cache-line padding
    private long _pad1A, _pad1B, _pad1C, _pad1d, _pad1E, _pad1F, _pad1G;
#pragma warning restore CS0169

    // --- Cache line 2: Consumer-hot ---
    private long _drainPosition;
    // Padding to fill cache line
#pragma warning disable CS0169
    private long _pad2A, _pad2B, _pad2C, _pad2d, _pad2E, _pad2F, _pad2G;
#pragma warning restore CS0169

    // --- Cache line 3: LSN + swap coordination ---
    private long _nextLsn;
    private int _activeBufferIndex;
    private int _swapState;
    private int _inflightCount;

    // ═══════════════════════════════════════════════════════════════════════
    // Signaling
    // ═══════════════════════════════════════════════════════════════════════

    private readonly ManualResetEventSlim _dataAvailableEvent = new(false);

    // ═══════════════════════════════════════════════════════════════════════
    // Drain watermark (consumer-thread only — no synchronization needed)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Highest LSN among the frames returned by the most recent <see cref="TryDrain"/>. Read by the WAL writer to advance its durable watermark.</summary>
    private long _drainHighLsn;

    // ═══════════════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════════════

    private int _disposed;

    // ═══════════════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new WAL commit buffer with the specified capacity per buffer.
    /// </summary>
    /// <param name="allocator">Memory allocator for tracked, pinned buffer allocation.</param>
    /// <param name="parent">Parent resource for resource graph integration.</param>
    /// <param name="bufferCapacity">
    /// Size of each buffer in bytes. Must be a multiple of 64 (cache-line aligned) and at least <see cref="MinBufferCapacity"/> (64 KB).
    /// Total memory = 2 x bufferCapacity.
    /// </param>
    /// <param name="initialLsn">Initial Log Sequence Number (default 1).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="bufferCapacity"/> is not a multiple of 64 or is too small.
    /// </exception>
    public WalCommitBuffer(IMemoryAllocator allocator, IResource parent, int bufferCapacity, long initialLsn = 1)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(parent);

        if (bufferCapacity < MinBufferCapacity)
        {
            throw new ArgumentException($"Buffer capacity must be at least {MinBufferCapacity} bytes, got {bufferCapacity}.", nameof(bufferCapacity));
        }

        if ((bufferCapacity & (CacheLineSize - 1)) != 0)
        {
            throw new ArgumentException($"Buffer capacity must be a multiple of {CacheLineSize}, got {bufferCapacity}.", nameof(bufferCapacity));
        }

        BufferCapacity = bufferCapacity;
        _nextLsn = initialLsn;

        // Single allocation for both buffers via IMemoryAllocator for resource tracking.
        // zeroed: true ensures all frame headers start as unpublished (FrameLength = 0).
        _memoryBlock = allocator.AllocatePinned("WalCommitBuffer", parent, bufferCapacity * 2, true, CacheLineSize);
        _buffer0 = _memoryBlock.DataAsPointer;
        _buffer1 = _memoryBlock.DataAsPointer + bufferCapacity;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Current utilization of the active buffer as a fraction [0.0, 1.0+]. Values above 1.0 indicate overflow (producers are waiting for a swap).
    /// </summary>
    public double Utilization
    {
        get
        {
            var tail = Interlocked.Read(ref _tailPosition);
            return (double)tail / BufferCapacity;
        }
    }

    /// <summary>Current Log Sequence Number (next to be assigned).</summary>
    public long NextLsn => Interlocked.Read(ref _nextLsn);

    /// <summary>
    /// Seeds the LSN allocator so the next record claimed gets LSN == <paramref name="lsn"/>. Called once from <see cref="WalManager.Initialize"/> BEFORE the
    /// writer thread starts (single-threaded — plain write, no barrier needed for a ≤64-bit field), to continue the global LSN sequence past the durability
    /// frontier on reopen. Without it the counter restarts at 1 every session; a reopened session's records then fall below a prior session's persisted
    /// CheckpointLSN, and recovery skips the entire post-reopen window as already-consolidated (silent data loss — LOG-08). Monotonic: never lowers the
    /// counter, so a fresh writer (frontier 1) is unaffected.
    /// </summary>
    internal void SeedNextLsn(long lsn)
    {
        if (lsn > _nextLsn)
        {
            _nextLsn = lsn;
        }
    }

    /// <summary>
    /// Highest LSN contained in the frames returned by the most recent <see cref="TryDrain"/> call (0 if that drain returned nothing). Consumer-thread only. The WAL
    /// writer advances its durable watermark to this value after the drained bytes are physically written and flushed, so <see cref="WalWriter.DurableLsn"/> never
    /// exceeds what reached stable media (LOG-05). Replaces the old <c>NextLsn - 1</c> peek, which counted claims that had been assigned an LSN but not yet drained (TXW-2).
    /// </summary>
    internal long LastDrainHighLsn => _drainHighLsn;

    /// <summary>Index of the currently active buffer (0 or 1).</summary>
    public int ActiveBufferIndex => _activeBufferIndex;

    /// <summary>Number of claims currently in-flight (claimed but not yet published/abandoned).</summary>
    internal int InflightCount => _inflightCount;

    /// <summary>Current drain position (consumer read head).</summary>
    internal long DrainPosition => _drainPosition;

    /// <summary>Current tail position (producer write head).</summary>
    internal long TailPosition => Interlocked.Read(ref _tailPosition);

    /// <summary>
    /// Bytes currently queued in the active buffer awaiting drain — the producer-visible fill level. Computed as <c>TailPosition - DrainPosition</c>, clamped
    /// at 0 to guard against any transient under-run from out-of-order reads of the two counters (both are read atomically, but not atomically as a pair).
    /// Exposed for the gauge subsystem; non-transactional sampling semantics — microsecond staleness is acceptable for visualization.
    /// </summary>
    public long UsedBytes
    {
        get
        {
            var used = TailPosition - _drainPosition;
            return used > 0 ? used : 0;
        }
    }

    /// <summary>Current swap state (0=Normal, 1=Requested, 2=Draining).</summary>
    internal int SwapState => _swapState;

    // ═══════════════════════════════════════════════════════════════════════
    // TryClaim — Atomic Space Allocation (Producer, lock-free)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Atomically claims a contiguous region in the active buffer for writing WAL records.
    /// </summary>
    /// <param name="payloadBytes">Total payload bytes needed (all WAL records in this frame).</param>
    /// <param name="recordCount">Number of WAL records that will be written.</param>
    /// <param name="ctx">
    /// Wait context for back-pressure timeout. Pass <see cref="WaitContext.Null"/> for infinite wait.
    /// </param>
    /// <returns>
    /// A <see cref="WalClaim"/> with <see cref="WalClaim.IsValid"/> = true on success.
    /// The producer must write data into <see cref="WalClaim.DataSpan"/>, then call <see cref="Publish"/>.
    /// </returns>
    /// <exception cref="WalClaimTooLargeException">
    /// Thrown immediately if the aligned frame size exceeds <see cref="BufferCapacity"/>.
    /// </exception>
    /// <exception cref="WalBackPressureTimeoutException">
    /// Thrown if the wait for buffer space exceeds the deadline in <paramref name="ctx"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown if the buffer has been disposed.</exception>
    public WalClaim TryClaim(int payloadBytes, int recordCount, ref WaitContext ctx)
    {
        ThrowIfDisposed();

        // Frame = WalFrameHeader (8 bytes) + payload, rounded up to 8-byte alignment
        var frameSize = Align8(WalFrameHeader.SizeInBytes + payloadBytes);

        if (frameSize > BufferCapacity)
        {
            ThrowHelper.ThrowWalClaimTooLarge(frameSize, BufferCapacity);
        }

        while (true)
        {
            var bufferIndex = _activeBufferIndex;
            var buffer = bufferIndex == 0 ? _buffer0 : _buffer1;

            // Atomic tail increment — each producer gets a unique, non-overlapping region.
            // Interlocked.Add maps to LOCK XADD on x64: always succeeds in exactly one instruction.
            var newTail = Interlocked.Add(ref _tailPosition, frameSize);
            var offset = newTail - frameSize;

            // CASE A: Fits within buffer
            if (newTail <= BufferCapacity)
            {
                Interlocked.Increment(ref _inflightCount);

                // Zero the frame header (FrameLength = 0 = unpublished)
                var frameHeader = (WalFrameHeader*)(buffer + offset);
                frameHeader->FrameLength = 0;
                frameHeader->RecordCount = 0;
                frameHeader->LastLsn = 0;

                // Assign LSNs atomically
                var firstLsn = Interlocked.Add(ref _nextLsn, recordCount) - recordCount;

                return new WalClaim
                {
                    DataSpan = new Span<byte>(buffer + offset + WalFrameHeader.SizeInBytes, frameSize - WalFrameHeader.SizeInBytes),
                    FrameOffset = (int)offset,
                    TotalFrameSize = frameSize,
                    RecordCount = recordCount,
                    FirstLSN = firstLsn,
                    BufferIndex = bufferIndex,
                    IsValid = true,
                };
            }

            // CASE B: This producer straddles the boundary (overlap initiator).
            // offset < BufferCapacity: the claim starts inside the buffer but extends past the end.
            //   Write a padding sentinel at the claim offset so TryDrain/CompleteDrain knows to stop.
            // offset == BufferCapacity: the previous claim filled the buffer exactly. No space for a
            //   sentinel, but we still need to request a swap so the writer advances to a new buffer.
            if (offset < BufferCapacity)
            {
                // Write padding sentinel at our offset
                var frameHeader = (WalFrameHeader*)(buffer + offset);
                frameHeader->RecordCount = 0;
                Interlocked.Exchange(ref frameHeader->FrameLength, WalFrameHeader.PaddingSentinel);
            }

            if (offset <= BufferCapacity)
            {
                // Request a buffer swap
                Interlocked.CompareExchange(ref _swapState, SwapRequested, SwapNormal);

                // Wake the consumer so it knows to drain and swap
                _dataAvailableEvent.Set();
            }

            // CASE B & C: Wait for the buffer swap to complete.// Poll _activeBufferIndex — when it changes, a swap happened and the fresh buffer is ready.
            // AdaptiveWaiter provides spin → yield → sleep(1) progression.
            // Phase 8: Backpressure span — captures how long this producer blocked waiting for the writer thread to swap buffers.
            // Emitted on the calling (producer) thread, not the writer. Parents under whatever span the producer is in (e.g. TransactionPersist).
            var bpStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var bpScope = TyphonEvent.BeginDurabilityWalBackpressure(0, Environment.CurrentManagedThreadId);
            try
            {
                var waiter = new AdaptiveWaiter();
                while (_activeBufferIndex == bufferIndex)
                {
                    if (!Unsafe.IsNullRef(ref ctx) && ctx.ShouldStop)
                    {
                        ThrowHelper.ThrowWalBackPressureTimeout(frameSize, ctx.Deadline.Remaining);
                    }

                    ThrowIfDisposed();
                    waiter.Wait();
                }
                bpScope.WaitUs = (uint)Math.Min((System.Diagnostics.Stopwatch.GetTimestamp() - bpStart) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency, uint.MaxValue);
            }
            finally
            {
                bpScope.Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Publish — Two-Phase Commit (Producer)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Publishes a previously claimed frame, making it visible to the consumer. The producer must have finished writing all record data
    /// into <see cref="WalClaim.DataSpan"/> before calling this method.
    /// </summary>
    /// <param name="claim">The claim returned by <see cref="TryClaim"/>. Must be valid.</param>
    /// <exception cref="InvalidOperationException">Thrown if the claim is not valid.</exception>
    public void Publish(ref WalClaim claim)
    {
        if (!claim.IsValid)
        {
            ThrowHelper.ThrowInvalidOp("Cannot publish an invalid or already-published claim.");
        }

        var buffer = claim.BufferIndex == 0 ? _buffer0 : _buffer1;
        var frameHeader = (WalFrameHeader*)(buffer + claim.FrameOffset);

        // Write record count + the frame's highest LSN first (plain stores — consumer reads these after seeing FrameLength). LastLsn is the honest per-frame
        // watermark source (LOG-05): the consumer takes the max over drained frames so DurableLsn cannot advance past an unwritten record's LSN.
        frameHeader->RecordCount = claim.RecordCount;
        frameHeader->LastLsn = claim.FirstLSN + claim.RecordCount - 1;

        // Release fence: Interlocked.Exchange ensures all prior writes (record data + RecordCount + LastLsn) are visible to the consumer before it sees the
        // non-zero FrameLength.
        Interlocked.Exchange(ref frameHeader->FrameLength, claim.TotalFrameSize);

        Interlocked.Decrement(ref _inflightCount);

        // Wake consumer
        _dataAvailableEvent.Set();

        claim.IsValid = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AbandonClaim — Error Recovery (Producer)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Abandons a previously claimed frame when serialization fails. The frame is marked as a skip region (published with zero records) so the consumer
    /// can advance past it. The inflight count is decremented to unblock buffer swaps.
    /// </summary>
    /// <param name="claim">The claim to abandon. Must be valid.</param>
    public void AbandonClaim(ref WalClaim claim)
    {
        if (!claim.IsValid)
        {
            return;
        }

        var buffer = claim.BufferIndex == 0 ? _buffer0 : _buffer1;
        var frameHeader = (WalFrameHeader*)(buffer + claim.FrameOffset);

        // Mark as empty frame (zero records = skip)
        frameHeader->RecordCount = 0;
        Interlocked.Exchange(ref frameHeader->FrameLength, claim.TotalFrameSize);

        Interlocked.Decrement(ref _inflightCount);

        claim.IsValid = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TryDrain — Consumer Scan (Single WAL Writer thread)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans forward from the current drain position, collecting contiguous published frames. Stops at the first unpublished frame (FrameLength == 0)
    /// or a padding sentinel.
    /// </summary>
    /// <remarks>
    /// When the drain position sits at a padding sentinel and a swap is pending, this method performs the swap internally and scans the fresh buffer.
    /// </remarks>
    /// <param name="data">The contiguous batch of published frame data.</param>
    /// <param name="frameCount">Number of frames in the batch.</param>
    /// <returns>True if at least one published frame was found.</returns>
    public bool TryDrain(out ReadOnlySpan<byte> data, out int frameCount)
    {
        frameCount = 0;
        data = default;

        if (_disposed != 0)
        {
            return false;
        }

        var bufferIndex = _activeBufferIndex;
        var buffer = bufferIndex == 0 ? _buffer0 : _buffer1;
        var scanPos = _drainPosition;

        // If the drain position is at a padding sentinel (or at the exact buffer boundary) with a pending swap,
        // perform the swap now so we can scan the fresh buffer.
        if (_swapState == SwapRequested)
        {
            var atBoundary = scanPos >= BufferCapacity;
            var atPadding = !atBoundary && Volatile.Read(ref ((WalFrameHeader*)(buffer + scanPos))->FrameLength) == WalFrameHeader.PaddingSentinel;  // acquire
            if (atBoundary || atPadding)
            {
                PerformSwap(buffer);

                // Scan the fresh buffer
                bufferIndex = _activeBufferIndex;
                buffer = bufferIndex == 0 ? _buffer0 : _buffer1;
                scanPos = _drainPosition; // Reset to 0 after swap
            }
        }

        var tail = Interlocked.Read(ref _tailPosition);
        long maxLastLsn = 0;

        while (scanPos < tail && scanPos < BufferCapacity)
        {
            var frameHeader = (WalFrameHeader*)(buffer + scanPos);
            // Acquire load: pairs with the producer's Interlocked.Exchange release on FrameLength, so the RecordCount/LastLsn/payload reads below observe the
            // fully-published frame. Free on x64 (TSO); emits ldar on arm64, where a plain load would be relaxed and could see a published flag before its data.
            var frameLength = Volatile.Read(ref frameHeader->FrameLength);

            if (frameLength == 0)
            {
                // Unpublished frame — stop scanning
                break;
            }

            if (frameLength == WalFrameHeader.PaddingSentinel)
            {
                // Padding sentinel — end of usable data in this buffer
                break;
            }

            // Track the highest LSN among published data frames in this batch (LOG-05 honest watermark). Padding and abandoned frames carry RecordCount 0 / LastLsn 0.
            if (frameHeader->RecordCount > 0 && frameHeader->LastLsn > maxLastLsn)
            {
                maxLastLsn = frameHeader->LastLsn;
            }

            scanPos += frameLength;
            frameCount++;
        }

        _drainHighLsn = maxLastLsn;

        if (frameCount > 0)
        {
            data = new ReadOnlySpan<byte>(buffer + _drainPosition, (int)(scanPos - _drainPosition));
        }

        return frameCount > 0;
    }

    /// <summary>
    /// Wakes the consumer thread if it is blocked in <see cref="WaitForData"/>.
    /// Used by <see cref="WalWriter.RequestFlush"/> to avoid waiting for the full GroupCommit interval.
    /// </summary>
    public void Signal() => _dataAvailableEvent.Set();

    /// <summary>
    /// Waits for data to become available. Call this when <see cref="TryDrain"/> returns false.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds, or -1 for infinite.</param>
    /// <returns>True if signaled (data may be available), false on timeout.</returns>
    public bool WaitForData(int timeoutMs)
    {
        if (_disposed != 0)
        {
            return false;
        }

        try
        {
            var signaled = _dataAvailableEvent.Wait(timeoutMs);
            _dataAvailableEvent.Reset();
            return signaled;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CompleteDrain + Double-Buffer Swap (Consumer)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Advances the drain position after the consumer has processed the drained data. If a buffer swap was requested and all data has been drained, performs
    /// the swap and wakes blocked producers.
    /// </summary>
    /// <param name="bytesProcessed">Number of bytes consumed from the last <see cref="TryDrain"/> batch.</param>
    public void CompleteDrain(int bytesProcessed)
    {
        _drainPosition += bytesProcessed;

        // Check if a swap is pending and we've drained enough
        if (_swapState != SwapRequested)
        {
            return;
        }

        // Check if we've reached the end of usable data
        var buffer = _activeBufferIndex == 0 ? _buffer0 : _buffer1;
        var atEnd = false;

        if (_drainPosition >= BufferCapacity)
        {
            atEnd = true;
        }
        else
        {
            var frameHeader = (WalFrameHeader*)(buffer + _drainPosition);
            if (Volatile.Read(ref frameHeader->FrameLength) == WalFrameHeader.PaddingSentinel)  // acquire: pairs with producer release on FrameLength
            {
                atEnd = true;
            }
        }

        if (!atEnd)
        {
            return;
        }

        PerformSwap(buffer);
    }

    /// <summary>
    /// Performs the double-buffer swap. Called by <see cref="CompleteDrain"/> when the consumer has drained to the end of the old buffer, and
    /// by <see cref="TryDrain"/> when it detects a padding sentinel at the drain position with a pending swap.
    /// </summary>
    /// <param name="oldBuffer">Pointer to the current (old) buffer being swapped out.</param>
    private void PerformSwap(byte* oldBuffer)
    {
        // Transition to draining state
        Interlocked.Exchange(ref _swapState, SwapDraining);

        // Wait for in-flight producers to finish publishing
        var spinWait = new SpinWait();
        while (_inflightCount > 0)
        {
            spinWait.SpinOnce();
        }

        // Drain any remaining published frames that arrived while we were waiting
        DrainRemaining(oldBuffer);

        // Perform the buffer swap
        var newIndex = 1 - _activeBufferIndex;
        var newBuffer = newIndex == 0 ? _buffer0 : _buffer1;

        // Clear the entire new buffer so stale frame headers from its previous use cannot be misread by TryDrain during the window between a producer's
        // Interlocked.Add (advancing _tailPosition) and its header zero-write.
        new Span<byte>(newBuffer, BufferCapacity).Clear();

        // Reset positions using Interlocked.Exchange to ensure full memory barrier ordering. Producers spin on _activeBufferIndex and immediately
        // Interlocked.Add on _tailPosition — the tail MUST be 0 before they see the new buffer index. The Exchange barrier guarantees this ordering.
        Interlocked.Exchange(ref _tailPosition, 0);
        _drainPosition = 0;

        // This Exchange acts as a release fence: producers spinning on _activeBufferIndex will see _tailPosition = 0 before they see
        // the new index, because both stores went through Interlocked.Exchange which provides sequential consistency.
        Interlocked.Exchange(ref _activeBufferIndex, newIndex);

        // SwapNormal must be last — some code paths check _swapState
        Interlocked.Exchange(ref _swapState, SwapNormal);
    }

    /// <summary>
    /// Drains any remaining published frames from the old buffer after inflight count reaches zero. These are frames that late publishers finished after
    /// the swap was requested.
    /// </summary>
    private void DrainRemaining(byte* buffer)
    {
        while (_drainPosition < BufferCapacity)
        {
            var frameHeader = (WalFrameHeader*)(buffer + _drainPosition);
            var frameLength = Volatile.Read(ref frameHeader->FrameLength);  // acquire: pairs with the producer's Interlocked.Exchange release on FrameLength

            if (frameLength == 0)
            {
                break;
            }

            if (frameLength == WalFrameHeader.PaddingSentinel)
            {
                break;
            }

            _drainPosition += frameLength;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Consumer Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Iterates over individual frames within a drained data span. Each frame has a <see cref="WalFrameHeader"/> followed by payload data.
    /// </summary>
    /// <param name="drainedData">The data returned by <see cref="TryDrain"/>.</param>
    /// <param name="callback">
    /// Called for each non-empty frame with the payload span and record count.
    /// </param>
    public static void WalkFrames(ReadOnlySpan<byte> drainedData, WalFrameCallback callback)
    {
        var offset = 0;
        while (offset < drainedData.Length)
        {
            ref readonly var header = ref Unsafe.As<byte, WalFrameHeader>(ref Unsafe.AsRef(in drainedData[offset]));

            if (header.FrameLength <= 0)
            {
                break;
            }

            if (header.RecordCount > 0)
            {
                var payloadStart = offset + WalFrameHeader.SizeInBytes;
                var payloadLength = header.FrameLength - WalFrameHeader.SizeInBytes;
                var payload = drainedData.Slice(payloadStart, payloadLength);
                callback(payload, header.RecordCount);
            }

            offset += header.FrameLength;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rounds up to the nearest multiple of 8 (ensures 8-byte alignment for all frames).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Align8(int value) => (value + 7) & ~7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
        {
            ThrowObjectDisposed();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(WalCommitBuffer));

    // ═══════════════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Releases the native buffer memory. Subsequent operations throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Producers spin-wait on _activeBufferIndex / _disposed — they'll see the _disposed flag and throw ObjectDisposedException.

        _memoryBlock.Dispose();

        _dataAvailableEvent.Dispose();
    }
}

/// <summary>
/// Callback delegate for <see cref="WalCommitBuffer.WalkFrames"/>.
/// </summary>
/// <param name="payload">The frame payload data (WAL records).</param>
/// <param name="recordCount">Number of WAL records in this frame.</param>
[PublicAPI]
internal delegate void WalFrameCallback(ReadOnlySpan<byte> payload, int recordCount);
