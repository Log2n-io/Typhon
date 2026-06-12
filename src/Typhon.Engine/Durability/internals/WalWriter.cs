using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Dedicated background thread that drains the <see cref="WalCommitBuffer"/>, writes WAL records to segment files with durable I/O (FUA), and signals
/// waiting producers based on durability mode.
/// </summary>
/// <remarks>
/// <para>
/// The writer is the single consumer of the MPSC commit buffer. It runs on a dedicated OS thread (<c>IsBackground=true</c>,
/// <see cref="ThreadPriority.AboveNormal"/>, named "Typhon-WAL-Writer").
/// </para>
/// <para>
/// <b>Drain loop:</b>
/// <list type="number">
///   <item><see cref="WalCommitBuffer.TryDrain"/> to collect published frames</item>
///   <item>If no data: <see cref="WalCommitBuffer.WaitForData"/> with GroupCommit timeout, then retry</item>
///   <item>Walk frames to track LSNs and accumulate data</item>
///   <item>Copy to 4096-aligned staging buffer, zero-pad tail</item>
///   <item>Write aligned data to active segment via <see cref="IWalFileIO.WriteAligned"/></item>
///   <item><see cref="WalCommitBuffer.CompleteDrain"/> to advance buffer position</item>
///   <item>Advance <see cref="DurableLsn"/> and signal <see cref="_durabilityEvent"/></item>
///   <item>Check segment rotation threshold (75%)</item>
/// </list>
/// </para>
/// <para>
/// <b>Error handling:</b> WAL write failure sets <see cref="_fatalError"/>. Subsequent <see cref="WaitForDurable"/> calls
/// throw <see cref="WalWriteException"/> (fail-fast per ADR-020).
/// </para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class WalWriter : ResourceNode, IMetricSource
{
    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    private const int PageSize = 4096;
    private const double RotationThreshold = 0.75;

    // ═══════════════════════════════════════════════════════════════
    // Dependencies
    // ═══════════════════════════════════════════════════════════════

    private readonly WalCommitBuffer _commitBuffer;
    private readonly WalSegmentManager _segmentManager;
    private readonly IWalFileIO _fileIO;
    private readonly WalWriterOptions _options;
    private readonly IMemoryAllocator _allocator;

    /// <summary>Optional logger, set post-construction by engine initialization.</summary>
    internal ILogger Logger { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Staging buffer (4096-aligned for O_DIRECT)
    // ═══════════════════════════════════════════════════════════════

    private readonly PinnedMemoryBlock _stagingBlock;
    private readonly byte* _stagingBuffer;
    private readonly int _stagingBufferSize;

    // ═══════════════════════════════════════════════════════════════
    // Thread lifecycle
    // ═══════════════════════════════════════════════════════════════

    private Thread _thread;
    private volatile bool _shutdown;
    private readonly Lock _lifecycleLock = new();

    // ═══════════════════════════════════════════════════════════════
    // Durability signaling
    // ═══════════════════════════════════════════════════════════════

    private long _durableLsn;
    private readonly ManualResetEventSlim _durabilityEvent = new(false);

    // ═══════════════════════════════════════════════════════════════
    // CRC chain state (single-threaded — writer thread only)
    // ═══════════════════════════════════════════════════════════════

    private uint _lastFooterCrc;

    // ═══════════════════════════════════════════════════════════════
    // Error state
    // ═══════════════════════════════════════════════════════════════

    private volatile Exception _fatalError;

    // ═══════════════════════════════════════════════════════════════
    // Flush request (for Deferred mode explicit Flush)
    // ═══════════════════════════════════════════════════════════════

    private volatile bool _flushRequested;

    // ═══════════════════════════════════════════════════════════════
    // Metrics
    // ═══════════════════════════════════════════════════════════════

    private long _totalBytesWritten;
    private long _totalFlushes;
    private long _lastFlushUs;
    private double _meanFlushUs;
    private long _maxFlushUs;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new WAL writer. Call <see cref="Start"/> to begin the writer thread.
    /// </summary>
    /// <param name="commitBuffer">The MPSC commit buffer to drain.</param>
    /// <param name="segmentManager">Manages WAL segment files.</param>
    /// <param name="fileIO">Platform I/O abstraction.</param>
    /// <param name="options">Writer configuration.</param>
    /// <param name="allocator">Memory allocator for the staging buffer.</param>
    /// <param name="parent">Parent resource node (typically <c>registry.Durability</c>).</param>
    internal WalWriter(WalCommitBuffer commitBuffer, WalSegmentManager segmentManager, IWalFileIO fileIO, WalWriterOptions options, IMemoryAllocator allocator, 
        IResource parent) : base("WalWriter", ResourceType.WAL, parent)
    {
        ArgumentNullException.ThrowIfNull(commitBuffer);
        ArgumentNullException.ThrowIfNull(segmentManager);
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allocator);

        _commitBuffer = commitBuffer;
        _segmentManager = segmentManager;
        _fileIO = fileIO;
        _options = options;
        _allocator = allocator;

        _stagingBufferSize = options.StagingBufferSize;
        _stagingBlock = allocator.AllocatePinned("WalWriter.Staging", this, _stagingBufferSize, true, PageSize);
        _stagingBuffer = _stagingBlock.DataAsPointer;
    }

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The highest LSN that has been durably written to stable media.</summary>
    public long DurableLsn => Interlocked.Read(ref _durableLsn);

    /// <summary>
    /// Seeds the durable watermark to a value already durable on disk from a prior lifecycle — used by crash recovery to mark the
    /// replayed WAL frontier as durable so the post-recovery seal checkpoint targets it (the cycle's targetLsn is DurableLsn, which
    /// is otherwise 0 on a fresh-opened writer). Monotonic; safe to call before the writer has produced any records this session.
    /// </summary>
    internal void SeedDurableLsn(long lsn) => AdvanceDurable(lsn);

    /// <summary>Whether the writer thread is currently running.</summary>
    public bool IsRunning => _thread != null && _thread.IsAlive;

    /// <summary>Whether a fatal I/O error has occurred.</summary>
    public bool HasFatalError => _fatalError != null;

    /// <summary>Total bytes written to WAL segments since startup.</summary>
    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

    /// <summary>Total number of flush operations performed.</summary>
    public long TotalFlushes => Interlocked.Read(ref _totalFlushes);

    // ═══════════════════════════════════════════════════════════════
    // Producer API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Requests a flush of the current buffer contents. Used by <see cref="DurabilityMode.Deferred"/>
    /// for explicit <c>Flush()</c> calls.
    /// </summary>
    public void RequestFlush()
    {
        _flushRequested = true;
        _commitBuffer.Signal(); // Wake the writer thread so it doesn't wait for the full GroupCommit interval
    }

    /// <summary>
    /// Blocks the caller until the specified LSN has been durably written to stable media.
    /// Used by <see cref="DurabilityMode.Immediate"/> producers and explicit <c>Flush()</c> callers.
    /// </summary>
    /// <param name="lsn">The LSN that must be durable before returning.</param>
    /// <param name="ctx">Wait context with timeout/cancellation.</param>
    /// <exception cref="WalWriteException">A fatal I/O error occurred — no further durable commits possible.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WaitForDurable(long lsn, ref WaitContext ctx)
    {
        // Fast path: already durable, returns inline. The WalWait span and wait loop live in WaitForDurableSlow so this shim stays EH-free and inlinable into
        // the per-commit caller (Transaction.PersistAndFinalize).
        if (Interlocked.Read(ref _durableLsn) >= lsn)
        {
            return;
        }
        WaitForDurableSlow(lsn, ref ctx);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WaitForDurableSlow(long lsn, ref WaitContext ctx)
    {
        // Check for fatal error
        if (_fatalError != null)
        {
            ThrowHelper.ThrowWalWriteFailure(_fatalError);
        }

        // Slow path — actual wait. The WalWait span captures how long this thread blocked waiting for the WAL writer to catch up.
        // Emitted on the CALLING thread (inside TransactionCommit), not the WAL writer thread. Parents under TransactionCommit
        // via the TLS open-span chain, so the viewer shows "Commit contained a WAL wait of N µs".
        using var waitScope = TyphonEvent.BeginWalWait(lsn);

        while (Interlocked.Read(ref _durableLsn) < lsn)
        {
            if (!Unsafe.IsNullRef(ref ctx) && ctx.ShouldStop)
            {
                ThrowHelper.ThrowWalBackPressureTimeout(0, ctx.Deadline.Remaining);
            }

            if (_fatalError != null)
            {
                ThrowHelper.ThrowWalWriteFailure(_fatalError);
            }

            // Wait for the durability event with a short timeout to re-check conditions
            _durabilityEvent.Wait(1);
            _durabilityEvent.Reset();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the WAL writer thread. Idempotent — does nothing if already running.
    /// </summary>
    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread != null && _thread.IsAlive)
            {
                return;
            }

            _shutdown = false;
            _thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "Typhon-WAL-Writer"
            };
            _thread.Start();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // IMetricSource
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        writer.WriteThroughput("BytesWritten", _totalBytesWritten);
        writer.WriteThroughput("Flushes", _totalFlushes);
        writer.WriteDuration("FlushLatency", _lastFlushUs, (long)_meanFlushUs, _maxFlushUs);

        var segment = _segmentManager.ActiveSegment;
        if (segment != null)
        {
            writer.WriteThroughput("ActiveSegmentId", segment.SegmentId);
        }
    }

    /// <inheritdoc />
    public void ResetPeaks() => _maxFlushUs = _lastFlushUs;

    // ═══════════════════════════════════════════════════════════════
    // Writer loop (core — runs on dedicated thread)
    // ═══════════════════════════════════════════════════════════════

    private void WriterLoop()
    {
        Logger?.LogDebug("WAL writer thread started");
        try
        {
            while (!_shutdown)
            {
                Logger?.LogDebug("WAL writer: loop iter, nextLsn={NextLsn} drain={DrainPos} tail={TailPos} swap={Swap} inflight={Inflight} active={Active}",
                    _commitBuffer.NextLsn, _commitBuffer.DrainPosition, _commitBuffer.TailPosition,
                    _commitBuffer.SwapState, _commitBuffer.InflightCount, _commitBuffer.ActiveBufferIndex);

                // 1. Try to drain published frames from the commit buffer
                if (!_commitBuffer.TryDrain(out var data, out var frameCount))
                {
                    // 2. No data — wait with GroupCommit timeout
                    if (_shutdown)
                    {
                        break;
                    }

                    _commitBuffer.WaitForData(_options.GroupCommitIntervalMs);

                    // Retry drain after waking
                    if (!_commitBuffer.TryDrain(out data, out frameCount))
                    {
                        // Still no data — handle GroupCommit timer flush if needed
                        if (_flushRequested)
                        {
                            // Phase 8: GroupCommit instant — captures the trigger interval + producer thread for latency analysis.
                            TyphonEvent.EmitDurabilityWalGroupCommit((ushort)Math.Min(_options.GroupCommitIntervalMs, ushort.MaxValue), Environment.CurrentManagedThreadId);
                            _flushRequested = false;
                            PerformFlush();
                        }

                        continue;
                    }
                }

                // 3. Highest LSN actually contained in this drained batch (LOG-05 honest watermark). TryDrain accumulated it from the drained frames' headers.
                //    This is NOT NextLsn - 1: that counts claims which have been assigned an LSN but whose frames are not yet drained/written, so advancing the
                //    durable watermark to it falsely acknowledges records still sitting in the buffer (TXW-2). LastDrainHighLsn never exceeds bytes about to be written.
                long batchHighLsn = _commitBuffer.LastDrainHighLsn;

                // WalFlush span: covers the write + signal cycle. The WAL writer thread claims its own ThreadSlotRegistry slot
                // on first emit, so it appears as a dedicated lane in the viewer.
                // Phase 8: kind 80 stays as the wrapper; QueueDrain/OsWrite/Signal sub-spans nest inside via the TLS open-span
                // chain so the viewer renders them as children of the existing WalFlush span.
                var flushScope = TyphonEvent.BeginWalFlush(data.Length, frameCount, batchHighLsn);
                try
                {

                    // 4. Copy to staging buffer with 4096-byte alignment
                    var bytesToWrite = AlignUp(data.Length, PageSize);

                    // Phase 8: Buffer span — covers the staging-buffer copy + zero-pad + CRC patch.
                    var bufferScope = TyphonEvent.BeginDurabilityWalBuffer(bytesToWrite, bytesToWrite - data.Length);
                    try
                    {
                        if (bytesToWrite > _stagingBufferSize)
                        {
                            // Data exceeds staging buffer — write in chunks
                            WriteInChunks(data);
                        }
                        else
                        {
                            // Copy data to staging buffer and zero-pad
                            data.CopyTo(new Span<byte>(_stagingBuffer, _stagingBufferSize));

                            // Zero-pad the remainder to the 4096 boundary
                            var padStart = data.Length;
                            var padLength = bytesToWrite - padStart;
                            if (padLength > 0)
                            {
                                new Span<byte>(_stagingBuffer + padStart, padLength).Clear();
                            }

                            // Patch chunk CRCs before writing to disk
                            PatchChunkCrcs(new Span<byte>(_stagingBuffer, _stagingBufferSize), data.Length);
                        }
                    }
                    finally
                    {
                        bufferScope.Dispose();
                    }

                    // Phase 8: OsWrite span — covers the actual disk write (WriteAligned + fsync via direct I/O).
                    if (bytesToWrite <= _stagingBufferSize)
                    {
                        var osWriteScope = TyphonEvent.BeginDurabilityWalOsWrite(bytesToWrite, frameCount, batchHighLsn);
                        try
                        {
                            // 5. Write aligned to active segment
                            var segment = _segmentManager.ActiveSegment;
                            var writeSpan = new ReadOnlySpan<byte>(_stagingBuffer, bytesToWrite);

                            var flushStart = Stopwatch.GetTimestamp();
                            _fileIO.WriteAligned(segment.Handle, segment.WriteOffset, writeSpan);
                            RecordFlushLatency(flushStart);

                            segment.WriteOffset += bytesToWrite;
                            Interlocked.Add(ref _totalBytesWritten, bytesToWrite);
                        }
                        finally
                        {
                            osWriteScope.Dispose();
                        }
                    }

                    // 6. Complete drain to advance buffer position. Phase 8: QueueDrain span — covers the drain advance.
                    var queueDrainScope = TyphonEvent.BeginDurabilityWalQueueDrain(data.Length, frameCount);
                    try
                    {
                        _commitBuffer.CompleteDrain(data.Length);
                    }
                    finally
                    {
                        queueDrainScope.Dispose();
                    }

                    // 7. Advance durable LSN and signal waiters. Phase 8: Signal span — LSN advance + waiter wake-up.
                    //    The outer check only gates span emission (avoid creating the Signal span when no advance is likely); AdvanceDurable performs the
                    //    monotonic advance itself.
                    if (batchHighLsn > Interlocked.Read(ref _durableLsn))
                    {
                        var signalScope = TyphonEvent.BeginDurabilityWalSignal(batchHighLsn);
                        try
                        {
                            Logger?.LogDebug("WAL writer: advancing durable LSN to {DurableLsn}, wrote {BytesWritten} bytes ({FrameCount} frames)",
                                batchHighLsn, bytesToWrite, frameCount);
                            AdvanceDurable(batchHighLsn);
                        }
                        finally
                        {
                            signalScope.Dispose();
                        }
                    }

                    Interlocked.Increment(ref _totalFlushes);

                    // 8. Check segment rotation threshold
                    if (_segmentManager.ActiveSegmentUtilization >= RotationThreshold)
                    {
                        Logger?.LogInformation("WAL segment rotation at {Utilization:P0}, rotating after LSN {LastLsn}",
                            _segmentManager.ActiveSegmentUtilization, batchHighLsn);
                        using var rotateScope = TyphonEvent.BeginWalSegmentRotate((int)(_segmentManager.ActiveSegment?.SegmentId ?? -1));
                        try
                        {
                            _segmentManager.RotateSegment(batchHighLsn + 1, batchHighLsn);
                            _lastFooterCrc = 0; // Reset CRC chain for new segment
                            Logger?.LogInformation("WAL segment rotation complete, new segment {SegmentId}",
                                _segmentManager.ActiveSegment?.SegmentId ?? -1);
                        }
                        catch (Exception rotEx)
                        {
                            Logger?.LogError(rotEx, "WAL segment rotation FAILED");
                            throw; // Let outer catch handle it
                        }
                    }

                    // Handle explicit flush request
                    if (_flushRequested)
                    {
                        _flushRequested = false;
                        PerformFlush();
                    }

                } // end WalFlush try
                finally
                {
                    flushScope.Dispose();
                }
            }

            // Shutdown: drain any remaining data
            Logger?.LogDebug("WAL writer: shutdown requested, draining remaining");
            DrainRemaining();
        }
        catch (Exception ex)
        {
            // Fatal I/O error — set error flag and wake all waiters
            Logger?.LogCritical(ex, "WAL writer FATAL error — thread terminating");
            _fatalError = ex;
            _durabilityEvent.Set();
        }
    }

    /// <summary>
    /// Writes a drained batch larger than the staging buffer. The CRC chain must be patched over the WHOLE batch in one pass: a record-batch chunk can be up to
    /// <see cref="RecordCodec.DefaultMaxChunkSize"/> (~64 KB) and a single committed frame up to the commit-buffer capacity (megabytes), so a chunk routinely straddles
    /// a staging-sized write boundary. Patching per-write-slice (the old behaviour) left any straddling chunk's footer CRC at its zero placeholder, which recovery then
    /// reads as a CRC break and treats as a torn tail — silently truncating the WAL at that point and losing every record after it. So patch first, then stream.
    /// </summary>
    /// <remarks>
    /// Patching happens in place on the drained region of the commit buffer. The WAL writer is the buffer's single consumer and producers never touch a frame once it is
    /// published (they only claim space ahead of the tail), so mutating the published bytes here races with nothing; the region is recycled (cleared) only on the next
    /// buffer swap, well after this write completes. Intermediate slices are exactly <see cref="_stagingBufferSize"/> (a 4096 multiple), so the batch lands contiguously
    /// on disk — identical bytes to what the single-write path would produce — with zero padding only after the final slice.
    /// </remarks>
    private void WriteInChunks(ReadOnlySpan<byte> data)
    {
        var segment = _segmentManager.ActiveSegment;

        // Patch the entire batch's CRC chain in one pass before any byte reaches disk (see remarks). `data` aliases the pinned commit buffer, so a writable view over the
        // same memory is sound — the bytes are mutable; the ReadOnlySpan is only an access restriction on this seam.
        fixed (byte* dataPtr = data)
        {
            PatchChunkCrcs(new Span<byte>(dataPtr, data.Length), data.Length);
        }

        int offset = 0;
        while (offset < data.Length)
        {
            var sliceLen = Math.Min(data.Length - offset, _stagingBufferSize);
            var writeLen = AlignUp(sliceLen, PageSize);

            // Copy the already-patched slice to the aligned staging buffer (the O_DIRECT write source must be page-aligned; the commit buffer is only 64-byte aligned).
            data.Slice(offset, sliceLen).CopyTo(new Span<byte>(_stagingBuffer, _stagingBufferSize));

            // Zero-pad the tail — only ever non-empty on the final slice, since intermediate slices are a whole _stagingBufferSize (page multiple) and stay contiguous.
            var padLen = writeLen - sliceLen;
            if (padLen > 0)
            {
                new Span<byte>(_stagingBuffer + sliceLen, padLen).Clear();
            }

            var writeSpan = new ReadOnlySpan<byte>(_stagingBuffer, writeLen);

            var flushStart = Stopwatch.GetTimestamp();
            _fileIO.WriteAligned(segment.Handle, segment.WriteOffset, writeSpan);
            RecordFlushLatency(flushStart);

            segment.WriteOffset += writeLen;
            Interlocked.Add(ref _totalBytesWritten, writeLen);
            offset += sliceLen;
        }
    }

    /// <summary>
    /// Performs an explicit flush (FlushFileBuffers) for GroupCommit timer or Deferred Flush().
    /// </summary>
    private void PerformFlush()
    {
        var segment = _segmentManager.ActiveSegment;
        if (segment?.Handle != null)
        {
            _fileIO.FlushBuffers(segment.Handle);
        }
    }

    /// <summary>
    /// Drains any remaining committed frames during shutdown.
    /// </summary>
    private void DrainRemaining()
    {
        // One final drain attempt
        if (_commitBuffer.TryDrain(out var data, out var frameCount) && frameCount > 0)
        {
            var bytesToWrite = AlignUp(data.Length, PageSize);

            if (bytesToWrite <= _stagingBufferSize)
            {
                data.CopyTo(new Span<byte>(_stagingBuffer, _stagingBufferSize));

                var padLen = bytesToWrite - data.Length;
                if (padLen > 0)
                {
                    new Span<byte>(_stagingBuffer + data.Length, padLen).Clear();
                }

                // Patch chunk CRCs before writing to disk
                PatchChunkCrcs(new Span<byte>(_stagingBuffer, _stagingBufferSize), data.Length);

                var segment = _segmentManager.ActiveSegment;
                if (segment?.Handle != null)
                {
                    var writeSpan = new ReadOnlySpan<byte>(_stagingBuffer, bytesToWrite);
                    _fileIO.WriteAligned(segment.Handle, segment.WriteOffset, writeSpan);
                    segment.WriteOffset += bytesToWrite;
                    Interlocked.Add(ref _totalBytesWritten, bytesToWrite);
                }
            }

            _commitBuffer.CompleteDrain(data.Length);

            // Final durable LSN advance — honest watermark over the drained frames (LOG-05), monotonic; never NextLsn - 1 (TXW-2).
            AdvanceDurable(_commitBuffer.LastDrainHighLsn);
        }

        // Final flush to ensure everything is on stable media
        PerformFlush();
    }

    // ═══════════════════════════════════════════════════════════════
    // Synchronous drain (crash-simulation tests — OQ-3)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Synchronously drains and writes every currently-published WAL frame on the CALLING thread, then flushes — the deterministic equivalent of running the
    /// background drain loop to quiescence. Runs the same staging-copy → CRC-patch → <see cref="IWalFileIO.WriteAligned"/> → durable-watermark-advance path as
    /// <see cref="WriterLoop"/> (minus telemetry spans), so a crash-simulation test can drive the real write path and fail the underlying <see cref="IWalFileIO"/>
    /// at a chosen write/flush boundary, then inspect the resulting on-disk state.
    /// </summary>
    /// <remarks>
    /// The background writer thread MUST NOT be running: the CRC chain state (<see cref="_lastFooterCrc"/>) assumes single-threaded access. Crash tests either never
    /// call <see cref="Start"/> or stop the thread first.
    /// </remarks>
    internal void DrainAndWriteSync()
    {
        if (IsRunning)
        {
            ThrowHelper.ThrowInvalidOp("DrainAndWriteSync must not be called while the WAL writer thread is running (CRC chain state is single-threaded).");
        }

        while (_commitBuffer.TryDrain(out var data, out var frameCount))
        {
            if (frameCount == 0)
            {
                break;
            }

            // Honest watermark over exactly these frames (LOG-05), captured before the write so a crash mid-write leaves the watermark unadvanced.
            var batchHighLsn = _commitBuffer.LastDrainHighLsn;
            var bytesToWrite = AlignUp(data.Length, PageSize);

            if (bytesToWrite > _stagingBufferSize)
            {
                WriteInChunks(data);
            }
            else
            {
                data.CopyTo(new Span<byte>(_stagingBuffer, _stagingBufferSize));

                var padLength = bytesToWrite - data.Length;
                if (padLength > 0)
                {
                    new Span<byte>(_stagingBuffer + data.Length, padLength).Clear();
                }

                PatchChunkCrcs(new Span<byte>(_stagingBuffer, _stagingBufferSize), data.Length);

                var segment = _segmentManager.ActiveSegment;
                var writeSpan = new ReadOnlySpan<byte>(_stagingBuffer, bytesToWrite);
                _fileIO.WriteAligned(segment.Handle, segment.WriteOffset, writeSpan);
                segment.WriteOffset += bytesToWrite;
                Interlocked.Add(ref _totalBytesWritten, bytesToWrite);
            }

            _commitBuffer.CompleteDrain(data.Length);

            AdvanceDurable(batchHighLsn);

            Interlocked.Increment(ref _totalFlushes);

            // Segment rotation parity with WriterLoop so multi-segment sync workloads behave like production.
            if (_segmentManager.ActiveSegmentUtilization >= RotationThreshold)
            {
                _segmentManager.RotateSegment(batchHighLsn + 1, batchHighLsn);
                _lastFooterCrc = 0;
            }
        }

        PerformFlush();
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC patching (single-threaded — writer thread only)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Patches PrevCRC and footer CRC for all chunks within the staging data.
    /// Called after data is copied to the staging buffer but before <see cref="IWalFileIO.WriteAligned"/>.
    /// Walks frame-by-frame, chunk-by-chunk, maintaining the CRC chain in <see cref="_lastFooterCrc"/>.
    /// </summary>
    private void PatchChunkCrcs(Span<byte> stagingData, int dataLength)
    {
        int frameOffset = 0;
        while (frameOffset + WalFrameHeader.SizeInBytes <= dataLength)
        {
            ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref stagingData[frameOffset]);
            if (frameHeader.FrameLength <= 0)
            {
                break;
            }

            var frameEnd = frameOffset + frameHeader.FrameLength;
            if (frameEnd > dataLength)
            {
                break;
            }

            var chunkOffset = frameOffset + WalFrameHeader.SizeInBytes;
            for (int i = 0; i < frameHeader.RecordCount; i++)
            {
                if (chunkOffset + WalChunkHeader.SizeInBytes > frameEnd)
                {
                    break;
                }

                ref var chunkHeader = ref Unsafe.As<byte, WalChunkHeader>(ref stagingData[chunkOffset]);

                // Validate chunk fits within frame bounds
                if (chunkHeader.ChunkSize < WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes ||
                    chunkOffset + chunkHeader.ChunkSize > frameEnd)
                {
                    break;
                }

                // 1. Patch PrevCRC from writer's chain state
                chunkHeader.PrevCRC = _lastFooterCrc;

                // 2. Compute CRC over [0, ChunkSize - FooterSize) — header + body
                var crcSpan = stagingData.Slice(chunkOffset, chunkHeader.ChunkSize - WalChunkFooter.SizeInBytes);
                var crc = WalCrc.Compute(crcSpan);

                // 3. Write footer CRC
                Unsafe.As<byte, uint>(ref stagingData[chunkOffset + chunkHeader.ChunkSize - WalChunkFooter.SizeInBytes]) = crc;

                // 4. Carry forward
                _lastFooterCrc = crc;
                chunkOffset += chunkHeader.ChunkSize;
            }

            frameOffset += frameHeader.FrameLength;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Monotonically advances the durable watermark to <paramref name="candidate"/> and wakes waiters. The single source of the advance invariant, shared by the
    /// background drain loop, shutdown drain, and the synchronous crash-test drain. Monotonic: a batch's high LSN can fall below the current watermark when claim
    /// order and buffer/drain order diverge (tail and LSN are claimed via two independent <c>Interlocked.Add</c>s); lowering the watermark would un-acknowledge an
    /// already-durable record. Caller is the single consumer (writer thread or a stopped-thread sync drain), so no CAS loop is needed.
    /// </summary>
    private void AdvanceDurable(long candidate)
    {
        if (candidate > Interlocked.Read(ref _durableLsn))
        {
            Interlocked.Exchange(ref _durableLsn, candidate);
            _durabilityEvent.Set();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    private void RecordFlushLatency(long startTimestamp)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        var us = (long)((double)elapsed / Stopwatch.Frequency * 1_000_000.0);

        _lastFlushUs = us;

        // EMA with alpha = 0.05 (~20-sample window)
        _meanFlushUs = _meanFlushUs * 0.95 + us * 0.05;

        if (us > _maxFlushUs)
        {
            _maxFlushUs = us;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    private bool _disposed;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _shutdown = true;
            _commitBuffer.Signal(); // Wake the writer thread so it sees _shutdown immediately
            _thread?.Join(TimeSpan.FromSeconds(5));

            _durabilityEvent.Dispose();
            _stagingBlock.Dispose();
        }

        base.Dispose(disposing);
        _disposed = true;
    }
}
