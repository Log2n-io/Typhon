using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Continuous WAL archiving of one engine (#1418, impl/1418 §3.3): every sealed segment copied to the store's <c>wal/&lt;timeline&gt;/</c> before the
/// checkpoint may reclaim it (BKP-10), timed segment switches so durable WAL waits at most <see cref="ArchiveOptions.MaxSegmentAge"/>, and a time index of
/// samples for restore-to-time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two parts (WA-1).</b> <see cref="Step"/> runs on the backup scheduler's tick and does bounded work only: a time sample, the switch decision, the
/// release bookkeeping, the state publish, the worker's wake. The worker (<c>Typhon-WalArchive</c>, below normal) does every segment read and store
/// write. A hung store stalls the worker only; the engine never waits on either: the floor and its backlog release are the checkpoint's.
/// </para>
/// <para>
/// <b>Ownership.</b> <see cref="Step"/> owns the switch ticket, the clock state and the published state; the worker owns the reader, the writer, the
/// store handles and the archive's end (<see cref="ArchivedThroughLsn"/>, published with release stores). The sample ring is the one thing both touch,
/// under a lock. The worker touches the engine (the segment manager, the WAL's file I/O, the floor) only under <c>_engineAccess</c>, which
/// <see cref="Stop"/> takes to end that for good: after it returns nothing of the engine is touched, whatever the worker is doing in the store.
/// </para>
/// </remarks>
internal sealed class WalArchiver : ResourceNode, IWalArchiver
{
    /// <summary>The index cache is rewritten every this many publishes (and at the close): it is a cache, rebuilt from the files when stale.</summary>
    private const int IndexWriteEvery = 16;

    private readonly DatabaseEngine _engine;
    private readonly WalManager _wal;
    private readonly ArchiveOptions _options;
    private readonly string _storePath;
    private readonly IStoreFileSystem _fs;
    private readonly ILogger _log;
    private readonly long _maxAgeTicks;
    private readonly long _sampleIntervalTicks;

    // ── Worker side ──
    private readonly Lock _engineAccess = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private Thread _thread;
    private volatile bool _exit;
    private bool _stopped;
    private volatile bool _detached;
    private WalSegmentReader _reader;
    private ArchivedSegmentWriter _writer;
    private bool _started;
    private Guid _timeline;
    private WalArchiveTimeline _timelineHandle;
    private readonly List<ArchivedSegmentInfo> _archived = [];
    private int _publishesSinceIndex;
    private long _releasesAtLastArchive;
    private bool _archivedThisSession;
    private ArchiveTimeSample[] _segmentSamples = new ArchiveTimeSample[64];
    private int _consecutiveFailures;
    private long _archivedThroughLsn;
    private int _condition = (int)WalArchiveCondition.Starting;
    private string _lastError;
    private long _gapsTotal;
    private long _lastGapFrom;
    private long _lastGapTo;
    private int _lastGapReason;
    private long _lastArchivedSealTicks;

    // ── Step side ──
    private readonly Lock _ringLock = new();
    private (long Lsn, long UtcTicks, long Mono)[] _ring;
    private int _ringStart;
    private int _ringCount;
    private long _durableAtLastStep = -1;
    private long _lastSampleLsn = -1;
    private long _lastSampleUtc;
    private long _lastSampleMono;
    private bool _clockSteppedBackLogged;
    private long _switchTicket;
    private long _activeSegmentSeen;
    private long _activeFirstDurableMono;
    private long _releasesSeen;
    private int _pointRequested;
    private StateBox _state;

    private sealed class StateBox(WalArchiveState value)
    {
        public readonly WalArchiveState Value = value;
    }

    /// <summary>Creates an archiver under <paramref name="parent"/>; its worker starts with <see cref="Start"/> (tests drive <see cref="ArchivePending"/>).</summary>
    internal WalArchiver(DatabaseEngine engine, ArchiveOptions options, string storePath, IResource parent, ILogger log, IStoreFileSystem fs = null)
        : base("WalArchiver", ResourceType.Backup, parent)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        _engine = engine;
        _wal = engine.WalManager ?? throw new InvalidOperationException("The WAL archiver needs the engine's WAL.");
        _options = options;
        _storePath = storePath;
        _fs = fs ?? StoreFileSystem.Instance;
        _log = log ?? NullLogger.Instance;
        _maxAgeTicks = (long)(options.MaxSegmentAge.TotalSeconds * Stopwatch.Frequency);
        _sampleIntervalTicks = (long)(options.TimeSampleInterval.TotalSeconds * Stopwatch.Frequency);
        var capacity = (int)Math.Clamp(2 * options.MaxSegmentAge.Ticks / Math.Max(1, options.TimeSampleInterval.Ticks), 16, 1 << 16);
        _ring = new (long, long, long)[capacity];
        _timeline = engine.TimelineId;
        _releasesSeen = engine.CheckpointManager?.ArchiveReleases ?? 0;
        _releasesAtLastArchive = _releasesSeen;
        _state = new StateBox(new WalArchiveState(WalArchiveCondition.Starting, _timeline, 0, 0, 0, 0, 0, 0, 0, ArchiveGapReason.None, DateTime.MinValue, null));
    }

    /// <summary>The close's final-pass budget (<see cref="ArchiveOptions.CloseTimeout"/>).</summary>
    internal TimeSpan CloseTimeout => _options.CloseTimeout;

    /// <summary>The archive's end: every LSN up to it is archived; 0 = nothing yet.</summary>
    internal long ArchivedThroughLsn => Volatile.Read(ref _archivedThroughLsn);

    /// <summary>Whether a <see cref="Stop"/> gave up joining the worker (its buffers were left to it).</summary>
    internal bool Detached => _detached;

    /// <summary>Waits for a detached worker to end (tests: once its store call returned).</summary>
    internal bool JoinWorker(TimeSpan timeout) => _thread == null || _thread.Join(timeout);

    /// <summary>Test seam: the clock used for archive times (footers). Null in production: <see cref="DateTime.UtcNow"/>.</summary>
    internal Func<DateTime> UtcClock { get; set; }

    /// <summary>Test seam: invoked on the worker after a segment's archived copy is durable and before the floor is raised. Null in production.</summary>
    internal Action<ArchivedSegmentInfo> AfterPublishProbe { get; set; }

    /// <inheritdoc />
    public WalArchiveState State => Volatile.Read(ref _state).Value;

    /// <inheritdoc />
    public bool ConsumeImmediatePointRequest() => Interlocked.Exchange(ref _pointRequested, 0) == 1;

    /// <summary>Starts the worker thread.</summary>
    internal void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(WorkerLoop) { Name = "Typhon-WalArchive", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        _wake.Set();
    }

    // ═══════════════════════════════════════════════════════════════
    // Step: the scheduler's tick
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void Step(DateTime utcNow, long monoNow, CancellationToken ct)
    {
        if (Volatile.Read(ref _stopped) || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            StepCore(utcNow.Ticks, monoNow);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never into the scheduler: the worst a failed step costs is one tick's sample and switch decision.
            WalArchiveLog.StepFailed(_log, e.Message);
        }
    }

    private void StepCore(long utcTicks, long monoNow)
    {
        var segments = _wal.SegmentManager;
        var checkpoint = _engine.CheckpointManager;
        if (segments == null || checkpoint == null)
        {
            return;
        }

        // 1 SAMPLE. The durable LSN read at the END of the previous step is paired with this step's clock, read by the caller after it: a sample never
        // claims an LSN durable before it was (restore-to-time never includes a commit durable after its instant).
        var durable = _durableAtLastStep;
        if (durable >= 0)
        {
            Sample(durable, utcTicks, monoNow);
        }

        // 2 SWITCH. Age = since the active segment's first durable record was seen (not its creation, WA-18); one ticket outstanding at a time.
        var archivedThrough = ArchivedThroughLsn;
        var snap = segments.ArchiveSnapshot(archivedThrough, checkpoint.CheckpointLsn);
        var nowDurable = _wal.DurableLsn;
        var activeHolds = snap.ActiveWrittenBytes > WalSegmentHeader.SizeInBytes && nowDurable >= snap.ActiveFirstLsn;
        if (snap.ActiveSegmentId != _activeSegmentSeen)
        {
            _activeSegmentSeen = snap.ActiveSegmentId;
            _activeFirstDurableMono = 0;
        }

        if (activeHolds && _activeFirstDurableMono == 0)
        {
            _activeFirstDurableMono = monoNow;
        }

        if (activeHolds && monoNow - _activeFirstDurableMono >= _maxAgeTicks && _switchTicket <= _wal.SwitchesServed)
        {
            _switchTicket = _wal.RequestSegmentSwitch();
        }

        // 3 WAKE the worker for a sealed segment whose records are durable (WA-15).
        if (snap.PendingCount > 0 && snap.Next.LastLsn <= nowDurable)
        {
            _wake.Set();
        }

        // 4 RELEASE: the checkpoint released the floor since the last step.
        var releases = checkpoint.ArchiveReleases;
        var released = releases != _releasesSeen;
        if (released)
        {
            _releasesSeen = releases;
            Volatile.Write(ref _pointRequested, 1);
        }

        // 5 STATE
        var condition = (WalArchiveCondition)Volatile.Read(ref _condition);
        if (checkpoint.ArchiveFloor == CheckpointManager.ArchiveFloorReleased && condition is WalArchiveCondition.Archiving or WalArchiveCondition.Stalled)
        {
            condition = WalArchiveCondition.Released;
        }

        var lagBytes = snap.PendingWrittenBytes + (activeHolds ? snap.ActiveWrittenBytes - WalSegmentHeader.SizeInBytes : 0);
        var lagSeconds = archivedThrough >= nowDurable ? 0 : LagSeconds(archivedThrough, monoNow);
        var gapTo = Volatile.Read(ref _lastGapTo);
        var gapFrom = Volatile.Read(ref _lastGapFrom);
        var sealTicks = Volatile.Read(ref _lastArchivedSealTicks);
        var next = new WalArchiveState(condition, _timeline, archivedThrough, lagBytes, snap.HeldBytes, lagSeconds, Volatile.Read(ref _gapsTotal), gapFrom,
            gapTo, (ArchiveGapReason)Volatile.Read(ref _lastGapReason), sealTicks == 0 ? DateTime.MinValue : new DateTime(sealTicks, DateTimeKind.Utc),
            Volatile.Read(ref _lastError));
        if (next != _state.Value)
        {
            Volatile.Write(ref _state, new StateBox(next));
        }

        _durableAtLastStep = _wal.DurableLsn;
    }

    private void Sample(long durable, long utcTicks, long monoNow)
    {
        if (_lastSampleLsn >= 0 && (durable == _lastSampleLsn || monoNow - _lastSampleMono < _sampleIntervalTicks))
        {
            return;   // kept only when the durable LSN moved (plus the first), at most once per interval (WA-19)
        }

        if (utcTicks < _lastSampleUtc)
        {
            // The wall clock stepped back: the time index stays monotonic (a backwards step is clamped, and logged once per step back).
            if (!_clockSteppedBackLogged)
            {
                WalArchiveLog.ClockSteppedBack(_log, new DateTime(_lastSampleUtc, DateTimeKind.Utc), new DateTime(utcTicks, DateTimeKind.Utc));
                _clockSteppedBackLogged = true;
            }

            utcTicks = _lastSampleUtc;
        }
        else
        {
            _clockSteppedBackLogged = false;
        }

        _lastSampleLsn = durable;
        _lastSampleUtc = utcTicks;
        _lastSampleMono = monoNow;
        if (durable == 0)
        {
            return;
        }

        lock (_ringLock)
        {
            if (_ringCount == _ring.Length)
            {
                // A segment held past twice the max age (a stalled store): grow, never drop a sample the archive will need.
                var grown = new (long, long, long)[_ring.Length * 2];
                for (var i = 0; i < _ringCount; i++)
                {
                    grown[i] = _ring[(_ringStart + i) % _ring.Length];
                }

                _ring = grown;
                _ringStart = 0;
            }

            _ring[(_ringStart + _ringCount) % _ring.Length] = (durable, utcTicks, monoNow);
            _ringCount++;
        }
    }

    /// <summary>Seconds since the first sample past the archive's end (WA-17): the RPO.</summary>
    private double LagSeconds(long archivedThrough, long monoNow)
    {
        lock (_ringLock)
        {
            for (var i = 0; i < _ringCount; i++)
            {
                var s = _ring[(_ringStart + i) % _ring.Length];
                if (s.Lsn > archivedThrough)
                {
                    return Math.Max(0, (monoNow - s.Mono) / (double)Stopwatch.Frequency);
                }
            }
        }

        return 0;
    }

    /// <summary>Copies the ring's samples with an LSN in <c>[first, last]</c> into <see cref="_segmentSamples"/>; returns how many.</summary>
    private int TakeSamples(long first, long last)
    {
        lock (_ringLock)
        {
            var n = 0;
            for (var i = 0; i < _ringCount; i++)
            {
                var s = _ring[(_ringStart + i) % _ring.Length];
                if (s.Lsn < first || s.Lsn > last)
                {
                    continue;
                }

                if (n == _segmentSamples.Length)
                {
                    Array.Resize(ref _segmentSamples, n * 2);
                }

                _segmentSamples[n++] = new ArchiveTimeSample(s.Lsn, s.UtcTicks);
            }

            return n;
        }
    }

    /// <summary>The samples not archived yet, oldest first (for tests and diagnostics; allocates).</summary>
    internal ArchiveTimeSample[] PendingSamples()
    {
        lock (_ringLock)
        {
            var result = new ArchiveTimeSample[_ringCount];
            for (var i = 0; i < _ringCount; i++)
            {
                var s = _ring[(_ringStart + i) % _ring.Length];
                result[i] = new ArchiveTimeSample(s.Lsn, s.UtcTicks);
            }

            return result;
        }
    }

    /// <summary>Drops the ring's samples at or below <paramref name="lsn"/>: archived, or of segments the archive will never hold.</summary>
    private void DropSamples(long lsn)
    {
        lock (_ringLock)
        {
            while (_ringCount > 0 && _ring[_ringStart].Lsn <= lsn)
            {
                _ringStart = (_ringStart + 1) % _ring.Length;
                _ringCount--;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Worker
    // ═══════════════════════════════════════════════════════════════

    private void WorkerLoop()
    {
        while (!_exit)
        {
            var backoff = _consecutiveFailures == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(_consecutiveFailures - 1, 16)), _options.MaxSegmentAge.TotalSeconds));
            _wake.Wait(backoff);
            _wake.Reset();
            if (_exit)
            {
                break;
            }

            ArchivePending(_stopping.Token);
        }
    }

    /// <summary>
    /// The worker's body: archives every sealed segment whose records are durable, oldest first, until none is left or one fails. Callable on a test's
    /// thread with the worker not started. Never throws: a failure is the state's (<see cref="WalArchiveCondition.Stalled"/>), retried with backoff.
    /// </summary>
    internal void ArchivePending(CancellationToken ct)
    {
        try
        {
            var condition = (WalArchiveCondition)Volatile.Read(ref _condition);
            if (condition is WalArchiveCondition.Diverged or WalArchiveCondition.Failed || (!_started && !TryStart()))
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                WalSealedSegment next;
                lock (_engineAccess)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    var snap = _wal.SegmentManager.ArchiveSnapshot(ArchivedThroughLsn, 0);
                    if (snap.PendingCount == 0 || snap.Next.LastLsn > _wal.DurableLsn)
                    {
                        break;
                    }

                    next = snap.Next;
                }

                if (!ArchiveOne(next))
                {
                    return;
                }
            }

            if (_consecutiveFailures > 0 || (WalArchiveCondition)Volatile.Read(ref _condition) == WalArchiveCondition.Stalled)
            {
                _consecutiveFailures = 0;
                SetCondition(WalArchiveCondition.Archiving, null);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    /// <summary>
    /// Opens the store side: the store's identity (created when absent), the timeline's lock and file, the archive's end; then raises the floor to it.
    /// A store of another database or a lock that is not enforced is a refusal (<see cref="WalArchiveCondition.Failed"/>); a busy lock is a retry.
    /// </summary>
    private bool TryStart()
    {
        var paths = new StorePaths(_storePath);
        var identity = StoreView.ReadIdentity(_fs, paths);
        if (identity == null)
        {
            // The store does not exist yet: create it (store.lock, briefly; a capture holding it makes this a retry).
            try
            {
                BackupStore.OpenForWrite(_storePath, _engine.DatabaseId, createIfMissing: true, _fs, _log).Dispose();
            }
            catch (NotSupportedException e)
            {
                Refuse(e.Message);
                return false;
            }

            identity = StoreView.ReadIdentity(_fs, paths);
        }

        if (identity.DatabaseId != _engine.DatabaseId)
        {
            Refuse($"the store '{_storePath}' belongs to database {identity.DatabaseId}, not {_engine.DatabaseId}");
            return false;
        }

        WalArchiveTimeline timeline;
        try
        {
            timeline = WalArchiveTimeline.TryOpen(_fs, _storePath, _timeline, _log);
        }
        catch (NotSupportedException e)
        {
            Refuse(e.Message);
            return false;
        }

        if (timeline == null)
        {
            Fail(new IOException($"the archive lock of timeline {_timeline} is held (a retention run); retrying"));
            return false;
        }

        long through;
        try
        {
            var file = timeline.EnsureTimelineFile(_engine.DescribeTimeline(identity.StoreId));
            if (file.DatabaseId != _engine.DatabaseId)
            {
                timeline.Dispose();
                Diverge($"timeline {_timeline} of the store was written by database {file.DatabaseId}");
                return false;
            }

            var loaded = timeline.LoadAndRefreshIndex();
            _archived.Clear();
            _archived.AddRange(loaded.Segments);
            through = loaded.ArchivedThroughLsn;
            if (_archived.Count > 0)
            {
                Volatile.Write(ref _lastArchivedSealTicks, _archived[^1].Header.SealedUtcTicks);
            }
        }
        finally
        {
            // Kept for the archiver's life, its paths computed once; the lock is taken again per segment (WA-24).
            timeline.ReleaseLock();
        }

        _timelineHandle?.Dispose();
        _timelineHandle = timeline;

        _reader ??= new WalSegmentReader(_wal.FileIO);
        _writer ??= new ArchivedSegmentWriter(new BufferOwner(_engine.MemoryAllocator, this));

        // Local segments the archive already covers (a crash before the floor rose): theirs must be the archived bytes, or two histories share this
        // timeline (WA-14).
        if (!CheckCovered(through))
        {
            return false;
        }

        Volatile.Write(ref _archivedThroughLsn, through);
        DropSamples(through);
        lock (_engineAccess)
        {
            if (_stopped)
            {
                return false;
            }

            _engine.CheckpointManager?.RaiseArchiveFloor(through + 1);
        }

        if (!StoreHasPointOnTimeline())
        {
            Volatile.Write(ref _pointRequested, 1);   // a timeline with no point cannot be restored from at all
        }

        _started = true;
        SetCondition(WalArchiveCondition.Archiving, null);
        WalArchiveLog.ArchiverStarted(_log, _timeline, through, _storePath);
        return true;
    }

    /// <summary>Whether every local sealed segment at or below <paramref name="through"/> is the one archived under its id (WA-14).</summary>
    private bool CheckCovered(long through)
    {
        if (through == 0)
        {
            return true;
        }

        List<WalSealedSegment> local;
        lock (_engineAccess)
        {
            if (_stopped)
            {
                return false;
            }

            local = _wal.SegmentManager.SealedSegments();
        }

        foreach (var s in local)
        {
            if (s.LastLsn > through || s.LastLsn < s.FirstLsn)
            {
                continue;
            }

            ArchivedSegmentInfo archived = null;
            foreach (var a in _archived)
            {
                if (a.SegmentId == s.Id)
                {
                    archived = a;
                }
                else if (a.FirstLsn <= s.LastLsn && s.FirstLsn <= a.LastLsn)
                {
                    Diverge($"local segment {s.Id} holds LSNs {s.FirstLsn}..{s.LastLsn}, archived under segment {a.SegmentId}");
                    return false;
                }
            }

            if (archived == null)
            {
                continue;   // inside a recorded gap of the archive
            }

            uint crc;
            lock (_engineAccess)
            {
                if (_stopped || !_reader.OpenSegmentForArchive(s.Path, s.WrittenBytes))
                {
                    continue;   // reclaimed meanwhile: nothing to compare
                }

                var length = (int)archived.Footer.ArchiveLength - WalSegmentHeader.SizeInBytes;
                if (archived.FirstLsn != s.FirstLsn || length > _reader.LoadedData.Length)
                {
                    Diverge($"local segment {s.Id} is not the segment archived under its id");
                    return false;
                }

                crc = Crc32CUtil.Finish(Crc32CUtil.Append(Crc32CUtil.Append(Crc32CUtil.Start, _reader.HeaderBytes), _reader.LoadedData[..length]));
            }

            if (crc != archived.Footer.RawCrc)
            {
                Diverge($"local segment {s.Id} differs from its archived copy");
                return false;
            }
        }

        return true;
    }

    private bool StoreHasPointOnTimeline()
    {
        try
        {
            foreach (var p in StoreView.Open(_storePath, _fs, _log).Catalog.Packs)
            {
                if (p.TimelineId == _timeline)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or BackupException or UnauthorizedAccessException)
        {
            // Unknown: ask for a point rather than assume one.
        }

        return false;
    }

    /// <summary>Archives one sealed segment (impl/1418 §3.3 ArchiveOne). False when archiving must stop or retry later.</summary>
    private bool ArchiveOne(in WalSealedSegment s)
    {
        var archivedThrough = ArchivedThroughLsn;

        // a  Read and scan the segment, under the engine-access lock (the WAL's own file I/O, delete sharing: WA-5).
        WalSegmentScan scan;
        long durable;
        lock (_engineAccess)
        {
            if (_stopped)
            {
                return false;
            }

            durable = _wal.DurableLsn;
            if (!_reader.OpenSegmentForArchive(s.Path, s.WrittenBytes))
            {
                // Reclaimed by a released cycle (or no valid header): gone from the list too; the next segment's continuity records the gap.
                return true;
            }

            scan = WalArchiveScanner.Scan(_reader);
        }

        if (s.LastLsn > durable)
        {
            throw new InvalidOperationException($"WA-15: segment {s.Id} ends at LSN {s.LastLsn}, past the durable LSN {durable}.");
        }

        // c  Continuity, by LSN (WA-8): a hole is a gap, recorded with its cause; an overlap is another history (WA-14).
        if (s.FirstLsn <= archivedThrough && archivedThrough > 0)
        {
            Diverge($"segment {s.Id} starts at LSN {s.FirstLsn}, inside the archive (archived through {archivedThrough})");
            return false;
        }

        var footer = new ArchivedSegmentFooter
        {
            LastLsn = s.LastLsn,
            LastRecordLsn = scan.LastRecordLsn,
            LastUnitLsn = scan.LastUnitLsn,
            ArchivedUtcTicks = (UtcClock?.Invoke() ?? DateTime.UtcNow).Ticks,
        };

        var releases = _engine.CheckpointManager?.ArchiveReleases ?? _releasesAtLastArchive;
        if (archivedThrough > 0 && s.FirstLsn > archivedThrough + 1)
        {
            var reason = releases != _releasesAtLastArchive ? ArchiveGapReason.BacklogReleased
                : !_archivedThisSession ? ArchiveGapReason.ArchivingOff
                : ArchiveGapReason.Unknown;
            footer.Flags |= ArchiveFormat.FooterFlagGapBefore;
            footer.GapFromLsn = archivedThrough + 1;
            footer.GapToLsn = s.FirstLsn - 1;
            footer.GapReason = reason;
        }

        if (scan.StoppedAtCorruption)
        {
            // A segment this session sealed scans clean by construction; a break is WAL damage. A crash open seals a torn last segment before anything
            // else is written (BKP-10's torn-tail clause), so an adopted one breaking here is damage too. Kept up to its last complete frame; what follows,
            // to the segment's end, is a range no restore may cross.
            footer.Flags |= ArchiveFormat.FooterFlagCorruptMidSegment;
            footer.DroppedFromLsn = footer.LastRecordLsn + 1;
            footer.DroppedToLsn = s.LastLsn;
            WalArchiveLog.SegmentDamaged(_log, s.Id, scan.FirstInvalidFrameOffset, footer.DroppedFromLsn, footer.DroppedToLsn);
        }
        else if (scan.SealedAtCrash)
        {
            footer.Flags |= ArchiveFormat.FooterFlagTornTrimmed;
            if (scan.DroppedToLsn > 0)
            {
                footer.Flags |= ArchiveFormat.FooterFlagDroppedApplicable;
                footer.DroppedFromLsn = footer.LastRecordLsn + 1;
                footer.DroppedToLsn = Math.Min(scan.DroppedToLsn, s.LastLsn);
            }
        }

        var header = new ArchivedSegmentHeader
        {
            FormatVersion = ArchiveFormat.FormatVersion,
            Compression = ArchiveFormat.CompressionLz4,
            Flags = s.Adopted ? ArchiveFormat.HeaderFlagAdopted : (byte)0,
            BlockSize = PackFormat.TailBlockSize,
            DatabaseId = _engine.DatabaseId,
            StoreId = StoreIdOrEmpty(),
            TimelineId = _timeline,
            SegmentId = s.Id,
            FirstLsn = scan.FirstLsn,
            DeclaredSize = scan.DeclaredSize,
            SealedUtcTicks = s.SealedUtcTicks,
            PrevSegmentLsn = scan.PrevSegmentLsn,
        };

        // d  Write and publish under the timeline's lock, for this one segment (WA-14, WA-24).
        var sampleCount = TakeSamples(s.FirstLsn, s.LastLsn);
        var timeline = _timelineHandle;
        if (!timeline.TryReacquire())
        {
            throw new IOException($"the archive lock of timeline {_timeline} is held (a retention run)");
        }

        (ArchivePublishOutcome Outcome, ArchivedSegmentInfo Segment) published;
        try
        {
            published = timeline.Publish(_writer, header, _reader.HeaderBytes,
                _reader.LoadedData[..(scan.ArchiveLength - WalSegmentHeader.SizeInBytes)], footer, _segmentSamples.AsSpan(0, sampleCount));
            if (published.Outcome == ArchivePublishOutcome.Diverged)
            {
                Diverge($"segment {s.Id} is archived with other bytes (another database copy archives to this timeline)");
                return false;
            }

            _archived.Add(published.Segment);
            if (++_publishesSinceIndex >= IndexWriteEvery)
            {
                timeline.WriteIndex(_archived);
                _publishesSinceIndex = 0;
            }
        }
        finally
        {
            timeline.ReleaseLock();
        }

        AfterPublishProbe?.Invoke(published.Segment);

        // e  Only now is the copy durable: the archive's end moves, and with it the floor.
        if ((published.Segment.Footer.Flags & ArchiveFormat.FooterFlagGapBefore) != 0)
        {
            RecordGap(published.Segment.Footer.GapFromLsn, published.Segment.Footer.GapToLsn, published.Segment.Footer.GapReason);
        }

        if (footer.DroppedToLsn > 0)
        {
            RecordGap(footer.DroppedFromLsn, footer.DroppedToLsn, scan.StoppedAtCorruption ? ArchiveGapReason.Unknown : ArchiveGapReason.TornAtCrash);
        }

        Volatile.Write(ref _archivedThroughLsn, s.LastLsn);
        Volatile.Write(ref _lastArchivedSealTicks, s.SealedUtcTicks);
        _releasesAtLastArchive = releases;
        _archivedThisSession = true;
        DropSamples(s.LastLsn);
        lock (_engineAccess)
        {
            if (_stopped)
            {
                return false;
            }

            _engine.CheckpointManager?.RaiseArchiveFloor(s.LastLsn + 1);
        }

        WalArchiveLog.SegmentArchived(_log, s.Id, s.FirstLsn, s.LastLsn, scan.ArchiveLength, published.Segment.FileLength);
        return true;
    }

    private Guid _storeId;

    private Guid StoreIdOrEmpty()
    {
        if (_storeId == Guid.Empty)
        {
            _storeId = StoreView.ReadIdentity(_fs, new StorePaths(_storePath))?.StoreId ?? Guid.Empty;
        }

        return _storeId;
    }

    private void RecordGap(long from, long to, ArchiveGapReason reason)
    {
        Volatile.Write(ref _lastGapFrom, from);
        Volatile.Write(ref _lastGapReason, (int)reason);
        Volatile.Write(ref _lastGapTo, to);
        Interlocked.Increment(ref _gapsTotal);
        Volatile.Write(ref _pointRequested, 1);
        WalArchiveLog.GapRecorded(_log, _timeline, from, to, reason);
    }

    private void SetCondition(WalArchiveCondition condition, string error)
    {
        Volatile.Write(ref _lastError, error);
        Volatile.Write(ref _condition, (int)condition);
    }

    private void Fail(Exception e)
    {
        _consecutiveFailures++;
        var was = (WalArchiveCondition)Volatile.Read(ref _condition);
        SetCondition(WalArchiveCondition.Stalled, e.Message);
        if (was != WalArchiveCondition.Stalled)
        {
            WalArchiveLog.StoreUnavailable(_log, _storePath, e.Message);
        }
    }

    private void Refuse(string why)
    {
        SetCondition(WalArchiveCondition.Failed, why);
        WalArchiveLog.ArchiverRefused(_log, _storePath, why);
    }

    private void Diverge(string why)
    {
        SetCondition(WalArchiveCondition.Diverged, why);
        WalArchiveLog.ArchiveDiverged(_log, _timeline, why);
    }

    // ═══════════════════════════════════════════════════════════════
    // Stop and dispose
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public void Stop(TimeSpan budget)
    {
        if (Volatile.Read(ref _stopped))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        if (budget > TimeSpan.Zero && _thread != null)
        {
            // The final pass (WA-23): seal what the session wrote, then let the worker archive everything sealed, within the budget.
            var ticket = _wal.RequestSegmentSwitch();
            while (_wal.SwitchesServed < ticket && clock.Elapsed < budget)
            {
                Thread.Yield();
            }

            _wake.Set();
            while (clock.Elapsed < budget && HasPendingWork())
            {
                if ((WalArchiveCondition)Volatile.Read(ref _condition) == WalArchiveCondition.Stalled)
                {
                    break;   // the store is failing: the next open archives the rest
                }

                Thread.Sleep(1);
            }

            if (HasPendingWork())
            {
                WalArchiveLog.ClosePassIncomplete(_log, ArchivedThroughLsn, _wal.DurableLsn);
            }
        }

        // From here the worker never touches the engine again: it checks the flag under the lock it touches the engine under.
        lock (_engineAccess)
        {
            Volatile.Write(ref _stopped, true);
        }

        _exit = true;
        _stopping.Cancel();
        _wake.Set();
        var remaining = budget - clock.Elapsed;
        if (_thread != null && !_thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
        {
            // A store call that does not return: the worker keeps its buffers (leaked, never freed under it).
            _detached = true;
        }
        else if (_started && _publishesSinceIndex > 0)
        {
            WriteIndexAtClose();
        }
    }

    private bool HasPendingWork()
    {
        var condition = (WalArchiveCondition)Volatile.Read(ref _condition);
        if (condition is WalArchiveCondition.Diverged or WalArchiveCondition.Failed)
        {
            return false;
        }

        var snap = _wal.SegmentManager.ArchiveSnapshot(ArchivedThroughLsn, 0);
        return snap.PendingCount > 0 && snap.Next.LastLsn <= _wal.DurableLsn;
    }

    private void WriteIndexAtClose()
    {
        var timeline = _timelineHandle;
        try
        {
            if (timeline != null && timeline.TryReacquire())
            {
                timeline.WriteIndex(_archived);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WalArchiveLog.StoreUnavailable(_log, _storePath, e.Message);
        }
        finally
        {
            timeline?.ReleaseLock();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop(TimeSpan.Zero);
            if (_detached)
            {
                // The worker still runs and owns the reader and the writer's blocks, parented here: disposing this node would free them under it.
                Parent?.RemoveChild(this);
                return;
            }

            _reader?.Dispose();
            _writer?.Dispose();
            _timelineHandle?.Dispose();
            _wake.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}
