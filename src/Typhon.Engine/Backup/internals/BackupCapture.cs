using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Typhon.Engine.Internals;

/// <summary>The points of a capture a test can stop at (<see cref="BackupCapture"/>'s probe). Null probe in production.</summary>
internal enum CapturePhase : byte
{
    /// <summary>The point was begun in the store; nothing is frozen yet.</summary>
    BeforeFreeze,

    /// <summary>The image is frozen and the write gate released; the hook is installed.</summary>
    AfterFreeze,

    /// <summary>The WAL tail is copied, and its point LSN not computed yet: the floor still holds (test seam for the read of <c>P</c>).</summary>
    TailBeforePoint,

    /// <summary>The WAL tail is copied and the reclaim floor released.</summary>
    TailCopied,

    /// <summary>The scanner has claimed a run starting at the page given and not read it yet.</summary>
    ScannerClaimed,

    /// <summary>Every page of the image is in the sink; the point is about to be completed.</summary>
    BeforeComplete,

    /// <summary>A data-file writer found the page given claimed by someone else and starts waiting for its capture (writer thread).</summary>
    WriterWaiting,
}

/// <summary>
/// One online backup capture (design 02 §3-§6, plan §3.6-§3.9): freeze, copy-before-write, the WAL tail, the page scan, then publish or roll back. Runs
/// on the backup thread; data-file writers reach it only through <see cref="IDataFileWriteHook.BeforeWrite"/>, between its install and its uninstall
/// under the exclusive write gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>BKP-02, the image.</b> The freeze takes the write gate exclusive, so no data-file write is in flight: the file is a valid crash image at that instant.
/// Under it, the capture reads the physical length (<c>N</c>), copies the meta pair, detaches the change map (an incremental's page set), samples the
/// published LSN for the tail and sets the reclaim floor, then installs itself as the hook. Every page of the set either is read by the scanner or, if a
/// writer gets to it first, is copied by that writer before its write (BKP-03).
/// </para>
/// <para>
/// <b>BKP-03, copy-before-write.</b> A page has a claimed bit and a done bit. Whoever sets the claimed bit first (scanner or writer) reads the page and then
/// sets done; a writer that finds a page claimed but not done waits for done. Done means the bytes are in capture memory, not yet in the store, so a writer
/// never waits on store I/O. Every wait is bounded by <see cref="BackupCaptureOptions.CbwStallTimeout"/>: past it the capture aborts and the writer
/// proceeds (BKP-12). After an abort every writer skips the hook.
/// </para>
/// <para>
/// <b>Uninstall (C7).</b> Only once every page is in the sink (so no writer can wait on the backup thread any more), or after an abort (which releases the
/// waiters). Taking the gate exclusive while a writer inside it waits on this thread would deadlock both.
/// </para>
/// <para>
/// <b>BKP-07, failure.</b> A capture that does not complete ORs its page set back into the live map (a full capture, the map it replaced), so the next
/// backup covers every page this one would have.
/// </para>
/// </remarks>
internal sealed partial class BackupCapture : IDataFileWriteHook, IDisposable
{
    private readonly DatabaseEngine _engine;
    private readonly ManagedPagedMMF _mmf;
    private readonly BackupCaptureOptions _options;
    private readonly IResource _owner;
    private readonly ILogger _logger;
    private readonly Action<int, ReadOnlySpan<byte>> _ingest;
    private readonly long _stallTicks;

    // Fixed under the freeze, before the hook is installed (the install's release store publishes them to writers).
    private bool _full;
    private PageChangeMap _imageSet;
    private int _imagePages;
    private ulong[] _claimed;
    private ulong[] _done;
    private CbwSlotPool _slots;

    private int _abortReason;
    private readonly CancellationTokenSource _abortCts = new();

    // Read by a closing engine that gave up joining the backup thread (it removes the hook itself), so both are volatile.
    private volatile bool _hookInstalled;
    private volatile bool _detached;

    // Backup thread only.
    private IBackupPointSink _sink;
    private PageChangeMap _mapToMergeOnFailure;
    private bool _floorSet;
    private bool _completed;
    private long _added;
    private long _expected;
    private PinnedMemoryBlock _scanBuffer;
    private PinnedMemoryBlock _metaBuffer;

    // The capture's own synchronous read handle on the data file. The engine's is overlapped (its batches write asynchronously), and every synchronous
    // read through an overlapped handle allocates on Windows: ~176 B per copy-before-write and per scanned run.
    private SafeFileHandle _reader;

    /// <summary>Creates a capture for <paramref name="engine"/>; <see cref="Run"/> performs it.</summary>
    public BackupCapture(DatabaseEngine engine, BackupCaptureOptions options, IResource owner, ILogger logger, Action<CapturePhase, int> probe)
    {
        _engine = engine;
        _mmf = engine.MMF;
        _options = options;
        _owner = owner;
        _logger = logger ?? NullLogger.Instance;
        Probe = probe;
        _ingest = AddPageToSink;
        _stallTicks = (long)(options.CbwStallTimeout.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>Test hook, null in production: invoked at each <see cref="CapturePhase"/>.</summary>
    internal Action<CapturePhase, int> Probe { get; }

    /// <summary>
    /// Mutant switch for BKP-03's verifier (in-test mutant only): a writer that finds a page claimed by someone else writes without waiting for it to be
    /// captured. Read only on that conflicted branch, never on the steady-state path.
    /// </summary>
    internal bool SkipClaimWaitForMutant { get; set; }

    /// <summary>Why the capture aborted, <see cref="BackupAbortReason.None"/> while it has not.</summary>
    internal BackupAbortReason AbortReason => (BackupAbortReason)Volatile.Read(ref _abortReason);

    /// <summary><c>N</c>: the image's length in pages.</summary>
    internal int ImagePages => _imagePages;

    /// <summary><c>C</c>: the checkpoint LSN of the frozen meta pair.</summary>
    internal long CheckpointLsn { get; private set; }

    /// <summary><c>PubF</c>: the WAL's last published LSN at the freeze.</summary>
    internal long PublishedAtFreeze { get; private set; }

    /// <summary>The reclaim floor set under the freeze.</summary>
    internal long ReclaimFloor { get; private set; }

    /// <summary>The kind admission decided.</summary>
    internal BackupKind Kind { get; private set; }

    /// <summary>How long the freeze waited for the writes in flight, in <see cref="Stopwatch"/> ticks.</summary>
    internal long FreezeAcquireTicks { get; private set; }

    /// <summary>How long the freeze held the write gate exclusive, in <see cref="Stopwatch"/> ticks.</summary>
    internal long FreezeHoldTicks { get; private set; }

    /// <summary>Pages preserved by writers (copy-before-write) rather than read by the scanner.</summary>
    internal int CbwCopies;

    /// <summary>
    /// Runs the capture into <paramref name="store"/>, updating <paramref name="lineage"/> on success.
    /// </summary>
    /// <exception cref="BackupAbortedException">The capture did not publish; nothing changed but the store's discarded temporary point.</exception>
    public BackupPointInfo Run(IBackupPointStore store, BackupKind requested, BackupLineage lineage, CancellationToken ct)
    {
        using var session = store.OpenSession(_engine.DatabaseId, ct);
        Kind = BackupAdmission.Decide(requested, lineage, session, _engine.TimelineId, out var parent);
        var plan = new BackupPointPlan(_engine.DatabaseId, _engine.TimelineId, Kind, Kind == BackupKind.Incremental ? lineage.ChainId : Guid.Empty,
            Kind == BackupKind.Incremental ? parent.BackupId : 0, PagedMMF.DatabaseFormatRevision,
            typeof(DatabaseEngine).Assembly.GetName().Version?.ToString() ?? "", false);
        _sink = Sink(() => session.BeginPoint(plan));

        try
        {
            var info = Capture(ct);
            lineage.Published(session.StoreId, info, DataFileWriteTracking.MapEnabled);
            return info;
        }
        catch (Exception e)
        {
            var aborted = Fail(e, ct);
            LogCaptureAborted(aborted.Reason, aborted.Message);
            throw aborted;
        }
    }

    private BackupPointInfo Capture(CancellationToken ct)
    {
        Probe?.Invoke(CapturePhase.BeforeFreeze, 0);

        // 1. A segment switch, then 2. a checkpoint: both best effort, for a short tail and a drained dirty set. Correctness never depends on them.
        var wal = _engine.WalManager;
        var ticket = wal.RequestSegmentSwitch();
        var switchDeadline = Stopwatch.GetTimestamp() + (long)(_options.SegmentSwitchTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (wal.SwitchesServed < ticket && Stopwatch.GetTimestamp() < switchDeadline)
        {
            ct.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        if (_options.CheckpointBeforeFreeze)
        {
            _engine.CheckpointManager.ForceCheckpointAndWait(_options.CheckpointTimeout, 0, ct);
        }

        ct.ThrowIfCancellationRequested();
        ThrowIfEngineCannotCapture();
        _slots = new CbwSlotPool(_engine.MemoryAllocator, _owner, _options.CbwBufferPages, CbwClock);
        _scanBuffer = _engine.MemoryAllocator.AllocatePinned("Backup.ScanBuffer", _owner, _options.ScanRunPages * PagedMMF.PageSize, false, 4096);
        _metaBuffer = _engine.MemoryAllocator.AllocatePinned("Backup.MetaPair", _owner, 2 * PagedMMF.PageSize, false, 4096);
        _reader = _mmf.OpenDataFileReader();

        var freezeSpan = TyphonEvent.BeginBackupFreeze();
        try
        {
            Freeze(ct);
            freezeSpan.ImagePages = _imagePages;
        }
        finally
        {
            freezeSpan.Dispose();
        }

        Probe?.Invoke(CapturePhase.AfterFreeze, 0);

        var meta0 = MetaSlot(0);
        var meta1 = MetaSlot(1);
        CheckpointLsn = BootstrapReader.Read(new FrozenMetaPageSource(meta0, meta1)).ReadWatermarks().CheckpointLsn;
        Sink(() => _sink.SetImage(_imagePages, CheckpointLsn, FreezeUtcTicks));
        AddPageToSink(0, meta0);
        AddPageToSink(1, meta1);

        // 5. The tail, holding the floor; writers' pre-images are drained between reads (C7).
        WalTailResult tail;
        var tailSpan = TyphonEvent.BeginBackupTail();
        try
        {
            tail = WalTailInspector.CaptureTail(wal, () => _engine.CheckpointManager.ReclaimFloor, _engine.MemoryAllocator, _owner, CheckpointLsn,
                PublishedAtFreeze, new SinkGuard(this), () => ServiceCbw(), _options.WalFlushTimeout, ct, BeforePointLsn);
        }
        finally
        {
            ReleaseFloor();
            tailSpan.Dispose();
        }

        var tailEndUtc = UtcNow().Ticks;
        Probe?.Invoke(CapturePhase.TailCopied, 0);

        // 6. The scan.
        var scanSpan = TyphonEvent.BeginBackupScan();
        try
        {
            Scan(ct);
            scanSpan.Pages = (int)Volatile.Read(ref _added);
        }
        finally
        {
            scanSpan.Dispose();
        }

        Probe?.Invoke(CapturePhase.BeforeComplete, 0);
        ThrowIfAborted();
        var info = Sink(() => _sink.Complete(tail.PointLsn, tailEndUtc));
        _completed = true;

        // 7. Uninstall: every page is in the sink, so no writer waits on this thread (C7).
        Uninstall();
        LogCaptureCompleted(info.BackupId, Kind, info.PageCount, _imagePages, tail.PointLsn, CbwCopies);
        return info;
    }

    /// <summary>
    /// The freeze (plan §3.6 step 3). Nothing is allocated under the gate, so the bitmaps are sized beforehand from the tracked length plus a margin;
    /// a freeze that finds the file longer (an async batch's growth is tracked only after its fsync) retries sized from the length it saw. Every
    /// attempt draws on one budget, <see cref="BackupCaptureOptions.FreezeAcquireTimeout"/> in all, with the gate open between attempts (BKP-12).
    /// </summary>
    private void Freeze(CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + Ticks(_options.FreezeAcquireTimeout);
        var margin = Math.Max(0, _options.FreezeGrowthMarginPages);
        long estimate = _mmf.FileSize / PagedMMF.PageSize + margin;
        while (true)
        {
            var sized = (int)Math.Min(int.MaxValue, estimate);
            var claimed = new ulong[(sized + 63) >> 6];
            var done = new ulong[(sized + 63) >> 6];
            var fresh = new PageChangeMap();
            fresh.Reserve(sized);

            var acquireStart = Stopwatch.GetTimestamp();
            if (!TryEnterGateExclusiveWithin(deadline, ct))
            {
                throw new BackupAbortedException(BackupAbortReason.GateTimeout, "the data-file writes in flight did not drain in time.");
            }

            var held = Stopwatch.GetTimestamp();
            int pages;
            try
            {
                pages = (int)(_mmf.DataFileLengthUnderGate() / PagedMMF.PageSize);
                if (pages <= sized)
                {
                    if (pages < 2)
                    {
                        throw new BackupAbortedException(BackupAbortReason.EngineFault, "the data file holds no meta pair.");
                    }

                    ReadPage(0, MetaSlot(0));
                    ReadPage(1, MetaSlot(1));
                    var detached = _mmf.SwapChangeMap(fresh);
                    _full = Kind == BackupKind.Full;
                    _imageSet = _full ? null : detached;
                    _mapToMergeOnFailure = detached;
                    _imagePages = pages;
                    _claimed = claimed;
                    _done = done;
                    _claimed[0] |= 3;
                    _done[0] |= 3;
                    ReclaimFloor = _engine.CheckpointManager.CheckpointLsn;
                    _engine.CheckpointManager.SetReclaimFloor(ReclaimFloor);
                    _floorSet = true;
                    PublishedAtFreeze = _engine.WalManager.LastPublishedLsn;
                    FreezeUtcTicks = UtcNow().Ticks;
                    _mmf.InstallWriteHook(this);
                    _hookInstalled = true;
                }
            }
            finally
            {
                _mmf.ExitWriteGateExclusive();
                FreezeAcquireTicks = held - acquireStart;
                FreezeHoldTicks = Stopwatch.GetTimestamp() - held;
            }

            if (pages <= sized)
            {
                // Published for the scheduler's heartbeat (DL-31): another thread reads the progress.
                Volatile.Write(ref _expected, _full ? _imagePages : 2 + CountImagePagesFrom(2));
                return;
            }

            // Sized from what the file was seen to hold, never again from the tracked length alone (it may lag the same batch still), so the next
            // attempt fits unless the file grew again since; the budget bounds the retries.
            FreezeRetries++;
            estimate = Math.Max(_mmf.FileSize / PagedMMF.PageSize, pages) + margin;
        }
    }

    /// <summary>Freezes that found the file longer than their bitmaps and retried (test seam).</summary>
    internal int FreezeRetries { get; private set; }

    /// <summary>
    /// Takes the write gate exclusive within <paramref name="deadline"/> (a <see cref="Stopwatch"/> timestamp), in attempts of at most
    /// <see cref="GateAttemptSlice"/>, the gate open between them so writers get through: the writers' stall is bounded by the budget in all, not per
    /// attempt (BKP-12).
    /// </summary>
    private bool TryEnterGateExclusiveWithin(long deadline, CancellationToken ct)
    {
        while (true)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return false;
            }

            var slice = TimeSpan.FromSeconds(Math.Min(remaining, Ticks(GateAttemptSlice)) / (double)Stopwatch.Frequency);
            if (_mmf.TryEnterWriteGateExclusive(slice))
            {
                return true;
            }

            ct.ThrowIfCancellationRequested();

            // The back-off: the gate is open until the writers the attempt held back have entered, so none of them waits through the next one too.
            _mmf.WriteGate.AdmitWaiters(Math.Min(deadline, Stopwatch.GetTimestamp() + Ticks(GateAttemptSlice)));
        }
    }

    /// <summary>The longest one exclusive attempt holds new writers back.</summary>
    internal static readonly TimeSpan GateAttemptSlice = TimeSpan.FromMilliseconds(100);

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    /// <summary>The freeze instant, UTC ticks.</summary>
    internal long FreezeUtcTicks { get; private set; }

    /// <summary>The scanner (plan §3.8): claims runs of unclaimed image pages, reads each run once, marks it done, then hands it to the sink.</summary>
    private void Scan(CancellationToken ct)
    {
        var scan = ScanSpan();
        var runPages = scan.Length / PagedMMF.PageSize;
        for (var p = NextImagePage(2); p >= 0;)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfAborted();
            ServiceCbw();

            var run = 0;
            while (run < runPages && p + run < _imagePages && IsImagePage(p + run))
            {
                var q = p + run;
                var bit = 1UL << (q & 63);
                if ((Interlocked.Or(ref _claimed[q >> 6], bit) & bit) != 0)
                {
                    break;   // a writer claimed it first: its pre-image arrives through a slot
                }

                run++;
            }

            if (run == 0)
            {
                p = NextImagePage(p + 1);
                continue;
            }

            Probe?.Invoke(CapturePhase.ScannerClaimed, p);
            var bytes = run * PagedMMF.PageSize;
            var read = ReadDataFile(p * (long)PagedMMF.PageSize, scan[..bytes]);
            if (read < bytes)
            {
                scan[read..bytes].Clear();
            }

            // Done once the bytes are in capture memory, before they reach the store (C8): a waiting writer never waits on store I/O.
            for (var i = 0; i < run; i++)
            {
                var q = p + i;
                Interlocked.Or(ref _done[q >> 6], 1UL << (q & 63));
            }

            for (var i = 0; i < run; i++)
            {
                AddPageToSink(p + i, scan.Slice(i * PagedMMF.PageSize, PagedMMF.PageSize));
            }

            p = NextImagePage(p + run);
        }

        // Pages writers claimed before the scanner reached them: wait for their pre-images.
        while (_added < _expected)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfAborted();
            if (ServiceCbw() == 0)
            {
                _slots.Published.Wait(10, ct);
            }
        }
    }

    /// <summary>Drains the writers' pre-images into the sink. Backup thread only. Throws once the capture aborted.</summary>
    private int ServiceCbw()
    {
        ThrowIfAborted();
        return _slots.Drain(_ingest);
    }

    // ═══════════════════════════════════════════════════════════════
    // Writer side (any data-file writer thread, inside the shared write gate)
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void BeforeWrite(int firstPage, int count, ref long deadline)
    {
        if (Volatile.Read(ref _abortReason) != 0)
        {
            return;
        }

        for (var p = firstPage; p < firstPage + count; p++)
        {
            if ((uint)p >= (uint)_imagePages || !IsImagePage(p))
            {
                continue;
            }

            var word = p >> 6;
            var bit = 1UL << (p & 63);
            if ((Volatile.Read(ref _done[word]) & bit) != 0)
            {
                continue;   // already captured
            }

            if (deadline == 0)
            {
                deadline = CbwNow() + _stallTicks;
            }

            if ((Interlocked.Or(ref _claimed[word], bit) & bit) == 0)
            {
                if (!CopyBeforeWrite(p, word, bit, deadline))
                {
                    NoteHookElapsed(deadline);
                    return;
                }
            }
            else if (!SkipClaimWaitForMutant && !WaitCaptured(word, bit, deadline))
            {
                NoteHookElapsed(deadline);
                return;
            }
        }

        if (deadline != 0)
        {
            NoteHookElapsed(deadline);
        }
    }

    /// <summary>
    /// The longest any write has spent in this hook since its budget started, in <see cref="Stopwatch"/> ticks: BKP-12's measure, cumulative over a
    /// <see cref="PagedMMF.SavePages"/> batch, whose runs share one budget. Taken only by writes that touched a page still to capture (a copy costs an
    /// 8 KiB read; a timestamp beside it is noise).
    /// </summary>
    internal long MaxHookElapsedTicks;

    /// <summary>
    /// Test seam, null in production: the clock of the copy-before-write budget (its start, every check against it, <see cref="MaxHookElapsedTicks"/>
    /// and the slot pool's waits), in <see cref="Stopwatch"/> ticks. A test's logical clock makes BKP-12 checkable without wall time: on a loaded box
    /// a writer can be off the CPU for a scheduler quantum between two checks, which no budget can promise away.
    /// </summary>
    internal Func<long> CbwClock { get; init; }

    /// <summary>
    /// Test seam, null in production: the wall clock of the point's freeze and tail-end stamps, so a scheduler on a logical clock judges a point's age
    /// on the same clock (plan 1416 §8.4).
    /// </summary>
    internal Func<DateTime> WallClock { get; init; }

    /// <summary>The copy-before-write slots, once allocated (test seam).</summary>
    internal CbwSlotPool Slots => _slots;

    private void BeforePointLsn() => Probe?.Invoke(CapturePhase.TailBeforePoint, 0);

    private long CbwNow() => CbwClock != null ? CbwClock() : Stopwatch.GetTimestamp();

    private DateTime UtcNow() => WallClock != null ? WallClock() : DateTime.UtcNow;

    private void NoteHookElapsed(long deadline)
    {
        var elapsed = CbwNow() - (deadline - _stallTicks);
        long seen;
        while (elapsed > (seen = Volatile.Read(ref MaxHookElapsedTicks)) && Interlocked.CompareExchange(ref MaxHookElapsedTicks, elapsed, seen) != seen)
        {
        }
    }

    /// <summary>This writer claimed <paramref name="page"/> first: copy its pre-image into a slot before the write proceeds.</summary>
    private bool CopyBeforeWrite(int page, int word, ulong bit, long deadline)
    {
        var slot = _slots.TryRent(deadline, _abortCts.Token);
        if (slot < 0)
        {
            Abort(BackupAbortReason.CbwStall);
            return false;
        }

        try
        {
            // No write to this page can be in flight: every earlier one finished before the freeze or waited for this claim.
            ReadPage(page, _slots.Span(slot));
        }
        catch (Exception)
        {
            _slots.Release(slot);
            Abort(BackupAbortReason.EngineFault);
            return false;
        }

        Interlocked.Or(ref _done[word], bit);
        Interlocked.Increment(ref CbwCopies);
        _slots.Publish(slot, page);
        return true;
    }

    /// <summary>Someone else claimed the page: wait until its pre-image is captured, the capture aborted, or the budget ran out (BKP-12).</summary>
    private bool WaitCaptured(int word, ulong bit, long deadline)
    {
        Probe?.Invoke(CapturePhase.WriterWaiting, (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bit));
        var spin = new SpinWait();
        while ((Volatile.Read(ref _done[word]) & bit) == 0)
        {
            if (Volatile.Read(ref _abortReason) != 0)
            {
                return false;
            }

            if (CbwNow() >= deadline)
            {
                Abort(BackupAbortReason.CbwStall);
                return false;
            }

            spin.SpinOnce(-1);
        }

        return true;
    }

    /// <summary>
    /// Aborts the capture: the first reason wins. Wakes every writer waiting on a slot and the backup thread; from now on writers skip the hook.
    /// </summary>
    internal void Abort(BackupAbortReason reason)
    {
        if (Interlocked.CompareExchange(ref _abortReason, (int)reason, 0) != 0)
        {
            return;
        }

        try
        {
            _abortCts.Cancel();
            _slots?.Published.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposed after the capture ended: nothing waits on it any more.
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Failure path (plan §3.9)
    // ═══════════════════════════════════════════════════════════════

    private BackupAbortedException Fail(Exception error, CancellationToken ct)
    {
        var reason = AbortReason;
        if (reason == BackupAbortReason.None)
        {
            reason = error switch
            {
                BackupAbortedException b => b.Reason,
                OperationCanceledException => ct.IsCancellationRequested ? BackupAbortReason.Cancelled : BackupAbortReason.EngineStopping,
                _ => BackupAbortReason.EngineFault,
            };
        }

        Abort(reason);
        try
        {
            if (!_completed)
            {
                Uninstall();

                // BKP-07: nothing this capture would have covered is forgotten. Lock-free beside the writers setting bits in the live map.
                if (_mapToMergeOnFailure != null)
                {
                    _mmf.ChangeMap.OrWith(_mapToMergeOnFailure);
                    _mapToMergeOnFailure = null;
                }

                _sink?.Abort();
            }
        }
        finally
        {
            ReleaseFloor();
        }

        return error as BackupAbortedException is { } same && same.Reason == reason
            ? same
            : new BackupAbortedException(reason, error.Message, error);
    }

    /// <summary>
    /// Removes the hook under the exclusive write gate, within <see cref="BackupCaptureOptions.UninstallTimeout"/> in all, in attempts with the gate
    /// open between them (BKP-12). Native memory can only be freed once no writer can be inside the hook; if the budget runs out the hook stays
    /// (harmless: every page is done or the capture aborted) and its memory is leaked rather than freed under a writer.
    /// </summary>
    internal bool Uninstall()
    {
        if (!_hookInstalled)
        {
            return true;
        }

        if (TryEnterGateExclusiveWithin(Stopwatch.GetTimestamp() + Ticks(_options.UninstallTimeout), CancellationToken.None))
        {
            try
            {
                _mmf.InstallWriteHook(null);
            }
            finally
            {
                _mmf.ExitWriteGateExclusive();
            }

            _hookInstalled = false;
            return true;
        }

        LogUninstallFailed();
        return false;
    }

    /// <summary>
    /// Called by a closing engine that gave up joining the backup thread: the capture still runs, so nothing it allocated may be freed under it. Its
    /// native memory is leaked instead (BKP-12's last resort; a store stuck past the close is already a failure being logged).
    /// </summary>
    internal void Detach() => _detached = true;

    /// <summary>Whether the capture's scan buffer is still allocated (test seam).</summary>
    internal bool ScanBufferAlive => _scanBuffer is { IsDisposed: false };

    private void ReleaseFloor()
    {
        if (_floorSet)
        {
            _engine.CheckpointManager?.ClearReclaimFloor();
            _floorSet = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private bool IsImagePage(int page) => _full || page < 2 || _imageSet.IsSet(page);

    /// <summary>Whether <paramref name="page"/> belongs to the frozen image's page set (test seam).</summary>
    internal bool IsInImage(int page) => page >= 0 && page < _imagePages && IsImagePage(page);

    private int NextImagePage(int from)
    {
        if (from >= _imagePages)
        {
            return -1;
        }

        return _full ? from : _imageSet.NextSetPage(from, _imagePages);
    }

    private long CountImagePagesFrom(int from)
    {
        long count = 0;
        for (var p = NextImagePage(from); p >= 0; p = NextImagePage(p + 1))
        {
            count++;
        }

        return count;
    }

    /// <summary>One page into the sink, suspect when it fails its checksum and is not all-zero (C9). Backup thread only.</summary>
    private void AddPageToSink(int page, ReadOnlySpan<byte> image)
    {
        var suspect = image.IndexOfAnyExcept((byte)0) >= 0 && !PagedMMF.VerifyPageImage(image, out _);
        try
        {
            _sink.AddPage(page, image, suspect);
        }
        catch (Exception e) when (e is not BackupAbortedException)
        {
            throw new BackupAbortedException(BackupAbortReason.SinkFailed, $"the store failed at page {page}.", e);
        }

        Volatile.Write(ref _added, _added + 1);
    }

    /// <summary>Pages handed to the store so far, and the pages the point will hold (0 until the freeze): read by the scheduler's heartbeat.</summary>
    internal (long Added, long Expected) Progress => (Volatile.Read(ref _added), Volatile.Read(ref _expected));

    private void ThrowIfAborted()
    {
        var reason = AbortReason;
        if (reason != BackupAbortReason.None)
        {
            throw new BackupAbortedException(reason, reason == BackupAbortReason.CbwStall
                ? "a data-file writer could not preserve a page in time; the backup cannot keep up with the engine's writes."
                : "the capture was aborted.");
        }
    }

    private void ThrowIfEngineCannotCapture()
    {
        var refusal = _engine.BackupRefusal();
        if (refusal != null)
        {
            throw new BackupAbortedException(BackupAbortReason.EngineFault, refusal);
        }
    }

    private static T Sink<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception e) when (e is not BackupAbortedException and not OperationCanceledException)
        {
            throw new BackupAbortedException(BackupAbortReason.SinkFailed, "the store failed.", e);
        }
    }

    private static void Sink(Action call)
    {
        try
        {
            call();
        }
        catch (Exception e) when (e is not BackupAbortedException and not OperationCanceledException)
        {
            throw new BackupAbortedException(BackupAbortReason.SinkFailed, "the store failed.", e);
        }
    }

    private unsafe Span<byte> MetaSlot(int slot) => new(_metaBuffer.DataAsPointer + slot * PagedMMF.PageSize, PagedMMF.PageSize);

    /// <summary>Reads data-file bytes at <paramref name="offset"/> through the capture's handle; returns how many (fewer past the end of the file).</summary>
    private int ReadDataFile(long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = RandomAccess.Read(_reader, destination[total..], offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>Reads file page <paramref name="page"/>; zeros past the end of the file.</summary>
    private void ReadPage(int page, Span<byte> destination)
    {
        var read = ReadDataFile(page * (long)PagedMMF.PageSize, destination);
        destination[read..].Clear();
    }

    private unsafe Span<byte> ScanSpan() => new(_scanBuffer.DataAsPointer, _options.ScanRunPages * PagedMMF.PageSize);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_detached)
        {
            return;
        }

        // What a writer could still reach stays alive while the hook is installed (see Uninstall).
        if (!_hookInstalled)
        {
            _slots?.Dispose();
            _slots = null;
            _reader?.Dispose();
            _reader = null;
            _abortCts.Dispose();
        }

        _scanBuffer?.Dispose();
        _scanBuffer = null;
        _metaBuffer?.Dispose();
        _metaBuffer = null;
    }

    /// <summary>The sink as the tail copy sees it: a store failure there is a <see cref="BackupAbortReason.SinkFailed"/> like anywhere else.</summary>
    private sealed class SinkGuard(BackupCapture capture) : IBackupPointSink
    {
        public uint BackupId => capture._sink.BackupId;

        public Guid ChainId => capture._sink.ChainId;

        public void SetImage(int imagePages, long checkpointLsn, long freezeUtcTicks) => Sink(() => capture._sink.SetImage(imagePages, checkpointLsn,
            freezeUtcTicks));

        public void AddPage(int pageId, ReadOnlySpan<byte> image, bool suspect) => capture.AddPageToSink(pageId, image);

        public void BeginTailSegment(long segmentId, long firstLsn, uint declaredSize) => Sink(() => capture._sink.BeginTailSegment(segmentId, firstLsn,
            declaredSize));

        public void AppendTail(ReadOnlySpan<byte> bytes)
        {
            try
            {
                capture._sink.AppendTail(bytes);
            }
            catch (Exception e) when (e is not BackupAbortedException)
            {
                throw new BackupAbortedException(BackupAbortReason.SinkFailed, "the store failed during the tail.", e);
            }
        }

        public void EndTailSegment() => Sink(() => capture._sink.EndTailSegment());

        public BackupPointInfo Complete(long pointLsn, long tailEndUtcTicks) => throw new InvalidOperationException("The tail copy never completes a point.");

        public void Abort() => capture._sink.Abort();
    }

    [LoggerMessage(LogLevel.Information,
        "Backup point {BackupId} ({Kind}) captured: {PageCount} of {ImagePages} pages, P = {PointLsn}, {CbwCopies} copy-before-write")]
    private partial void LogCaptureCompleted(uint backupId, BackupKind kind, int pageCount, int imagePages, long pointLsn, int cbwCopies);

    [LoggerMessage(LogLevel.Warning, "Backup capture aborted ({Reason}): {Message}")]
    private partial void LogCaptureAborted(BackupAbortReason reason, string message);

    [LoggerMessage(LogLevel.Error, "Backup capture could not remove its copy-before-write hook; its buffers are kept until the engine closes")]
    private partial void LogUninstallFailed();
}
