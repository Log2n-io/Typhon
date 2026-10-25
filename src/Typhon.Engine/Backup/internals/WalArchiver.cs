using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Typhon.Profiler;

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

    private const int WorkerRunning = 0;
    private const int WorkerExited = 1;
    private const int WorkerDetached = 2;

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
    private int _workerHandoff;   // WorkerRunning, then WorkerExited (Dispose frees the buffers) or WorkerDetached (the worker frees them at its exit)
    private WalSegmentReader _reader;
    private ArchivedSegmentWriter _writer;
    private bool _started;
    private Guid _storeId;
    private readonly Dictionary<Guid, TimelineArchive> _timelines = new();
    private long _releasesAtLastArchive;
    private bool _archivedThisSession;
    private ArchiveTimeSample[] _segmentSamples = new ArchiveTimeSample[64];
    private int _consecutiveFailures;
    private long _passesStarted;
    private long _lastFailedPass;
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
    private Guid _timelineSeen;
    private int _pointRequested;
    private StateBox _state;

    private sealed class StateBox(WalArchiveState value)
    {
        public readonly WalArchiveState Value = value;
    }

    /// <summary>One timeline's archive as the worker keeps it: its handle (paths, the per-segment lock), its segments, the publishes since its index.</summary>
    private sealed class TimelineArchive(WalArchiveTimeline handle)
    {
        public readonly WalArchiveTimeline Handle = handle;
        public readonly List<ArchivedSegmentInfo> Archived = [];
        public int PublishesSinceIndex;

        public long End => Archived.Count == 0 ? 0 : Archived[^1].End;
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
        _timelineSeen = engine.TimelineId;
        _releasesSeen = engine.CheckpointManager?.ArchiveReleases ?? 0;
        _releasesAtLastArchive = _releasesSeen;
        _state = new StateBox(new WalArchiveState(WalArchiveCondition.Starting, _timelineSeen, 0, 0, 0, 0, 0, 0, 0, ArchiveGapReason.None, DateTime.MinValue, null));
    }

    /// <summary>The close's final-pass budget (<see cref="ArchiveOptions.CloseTimeout"/>).</summary>
    internal TimeSpan CloseTimeout => _options.CloseTimeout;

    /// <summary>The archive's end: every LSN up to it is archived; 0 = nothing yet.</summary>
    internal long ArchivedThroughLsn => Volatile.Read(ref _archivedThroughLsn);

    /// <summary>Whether a <see cref="Stop"/> gave up joining the worker (its buffers were left to it).</summary>
    internal bool Detached => _detached;

    /// <summary>Waits for a detached worker to end (tests: once its store call returned).</summary>
    internal bool JoinWorker(TimeSpan timeout) => _thread == null || _thread.Join(timeout);

    /// <summary>
    /// The most samples kept in memory (24 B each, 1.5 MiB at the default): past it, while the archive is stalled or released, every other one is dropped.
    /// Settable for tests.
    /// </summary>
    internal int SampleRingLimit { get; set; } = 1 << 16;

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

        // A fork (a schema change, a bulk load): the new timeline has no point to restore from yet.
        var timeline = _engine.TimelineId;
        if (timeline != _timelineSeen)
        {
            _timelineSeen = timeline;
            Volatile.Write(ref _pointRequested, 1);
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
        var next = new WalArchiveState(condition, timeline, archivedThrough, lagBytes, snap.HeldBytes, lagSeconds, Volatile.Read(ref _gapsTotal), gapFrom,
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
            if (_ringCount == _ring.Length && _ring.Length < SampleRingLimit)
            {
                // A segment held past twice the max age (a stalled store): grow, up to the limit.
                var grown = new (long, long, long)[Math.Min(_ring.Length * 2, SampleRingLimit)];
                for (var i = 0; i < _ringCount; i++)
                {
                    grown[i] = _ring[(_ringStart + i) % _ring.Length];
                }

                _ring = grown;
                _ringStart = 0;
            }
            else if (_ringCount == _ring.Length)
            {
                // Past the limit (stalled, or released: the samples of a reclaimed segment are never archived): every other sample goes. The index loses
                // resolution, never correctness — an instant still resolves to an LSN durable at or before it, only an earlier one.
                var kept = 0;
                for (var i = 0; i < _ringCount; i += 2)
                {
                    _ring[(_ringStart + kept++) % _ring.Length] = _ring[(_ringStart + i) % _ring.Length];
                }

                _ringCount = kept;
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

    /// <summary>
    /// Copies the ring's samples with an LSN in <c>[first − 1, last]</c> into <see cref="_segmentSamples"/>; returns how many. A sample at
    /// <c>first − 1</c> was taken after the previous segment was archived (a step right after a switch samples the durable LSN of the step before, the
    /// previous segment's end): still true, it is filed with this segment rather than lost.
    /// </summary>
    private int TakeSamples(long first, long last)
    {
        lock (_ringLock)
        {
            var n = 0;
            for (var i = 0; i < _ringCount; i++)
            {
                var s = _ring[(_ringStart + i) % _ring.Length];
                if (s.Lsn < first - 1 || s.Lsn > last)
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
        try
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
        finally
        {
            // A Stop that could not join left the buffers to this thread (an as-crash Stop never waits): it frees them now, as it no longer uses them.
            if (Interlocked.CompareExchange(ref _workerHandoff, WorkerExited, WorkerRunning) == WorkerDetached)
            {
                FreeWorkerResources();
            }
        }
    }

    /// <summary>
    /// The worker's body: archives every sealed segment whose records are durable, oldest first, until none is left or one fails. Callable on a test's
    /// thread with the worker not started. Never throws: a failure is the state's (<see cref="WalArchiveCondition.Stalled"/>), retried with backoff.
    /// </summary>
    internal void ArchivePending(CancellationToken ct)
    {
        var pass = Interlocked.Increment(ref _passesStarted);
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

                // One BackupArchiveSegment span (trace kind 74) per segment: an exception is a failed copy (the catch below records it).
                var span = TyphonEvent.BeginBackupArchiveSegment(next.Id);
                bool archived;
                try
                {
                    archived = ArchiveOne(next, ref span);
                }
                catch
                {
                    span.Outcome = (byte)BackupArchiveSegmentOutcome.Failed;
                    throw;
                }
                finally
                {
                    span.Dispose();
                }

                if (!archived)
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
            Volatile.Write(ref _lastFailedPass, pass);
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

        _storeId = identity.StoreId;
        _reader ??= new WalSegmentReader(_wal.FileIO);
        _writer ??= new ArchivedSegmentWriter(new BufferOwner(_engine.MemoryAllocator, this));

        // The archive's end: the current timeline's; for a fork, its parent's when that is further (segments of the parent may still wait here, held by
        // the floor); for a restored database, at least its origin — the segments below it are the parent's history, restored, never re-archived.
        var current = _engine.TimelineFor(long.MaxValue);
        var through = 0L;
        TimelineArchive archive;
        try
        {
            archive = OpenTimeline(current);
            if (archive == null)
            {
                return false;
            }

            through = archive.End;
            if (current.ParentId != Guid.Empty && current.Reason is TimelineReason.SchemaChange or TimelineReason.BulkLoad)
            {
                var parent = OpenTimeline(_engine.TimelineFor(current.OriginLsn));
                if (parent == null)
                {
                    return false;
                }

                through = Math.Max(through, parent.End);
            }
        }
        catch (NotSupportedException e)
        {
            Refuse(e.Message);
            return false;
        }

        through = Math.Max(through, _engine.RestoreOriginLsn);
        if (archive.Archived.Count > 0)
        {
            Volatile.Write(ref _lastArchivedSealTicks, archive.Archived[^1].Header.SealedUtcTicks);
        }

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

        if (!StoreHasPointOnTimeline(current.TimelineId))
        {
            Volatile.Write(ref _pointRequested, 1);   // a timeline with no point cannot be restored from at all
        }

        _started = true;
        SetCondition(WalArchiveCondition.Archiving, null);
        WalArchiveLog.ArchiverStarted(_log, current.TimelineId, through, _storePath);
        return true;
    }

    /// <summary>
    /// A timeline's archive, opened once per archiver (its lock taken, <c>timeline.json</c> written when absent, its segments loaded, the lock let go: it is
    /// taken again per segment). Null when the timeline was written by another database (Diverged). Throws an <see cref="IOException"/> when the lock is
    /// busy (a retry).
    /// </summary>
    private TimelineArchive OpenTimeline(in TimelineEntry entry)
    {
        if (_timelines.TryGetValue(entry.TimelineId, out var cached))
        {
            return cached;
        }

        var handle = WalArchiveTimeline.TryOpen(_fs, _storePath, entry.TimelineId, _log)
                     ?? throw new IOException($"the archive lock of timeline {entry.TimelineId} is held (a retention run); retrying");
        var archive = new TimelineArchive(handle);
        try
        {
            var file = handle.EnsureTimelineFile(_engine.DescribeTimeline(entry, _storeId));
            if (file.DatabaseId != _engine.DatabaseId)
            {
                handle.Dispose();
                Diverge($"timeline {entry.TimelineId} of the store was written by database {file.DatabaseId}");
                return null;
            }

            archive.Archived.AddRange(handle.LoadAndRefreshIndex().Segments);
        }
        finally
        {
            handle.ReleaseLock();
        }

        _timelines[entry.TimelineId] = archive;
        return archive;
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

        var restoreOrigin = _engine.RestoreOriginLsn;
        foreach (var s in local)
        {
            if (s.LastLsn > through || s.LastLsn < s.FirstLsn || s.LastLsn <= restoreOrigin)
            {
                continue;   // not archived yet, empty, or the restored history of the parent (never re-archived)
            }

            var archive = OpenTimeline(_engine.TimelineFor(s.LastLsn));
            if (archive == null)
            {
                return false;
            }

            ArchivedSegmentInfo archived = null;
            foreach (var a in archive.Archived)
            {
                if (a.SegmentId == s.Id)
                {
                    archived = a;
                    break;
                }
            }

            uint crc;
            WalSegmentScan scan;
            lock (_engineAccess)
            {
                if (_stopped || !_reader.OpenSegmentForArchive(s.Path, s.WrittenBytes))
                {
                    continue;   // reclaimed meanwhile: nothing to compare
                }

                // The bytes the archive keeps, as the publish took them: the scan zeroes past the last complete frame, so a crash-sealed or damaged
                // segment's sentinel and torn bytes, still on disk, are not compared against the zeros its archived copy holds there.
                scan = WalArchiveScanner.Scan(_reader);
                crc = 0;
                if (archived != null)
                {
                    var length = (int)archived.Footer.ArchiveLength - WalSegmentHeader.SizeInBytes;
                    if (archived.FirstLsn != s.FirstLsn || length > _reader.LoadedData.Length)
                    {
                        Diverge($"local segment {s.Id} is not the segment archived under its id");
                        return false;
                    }

                    crc = Crc32CUtil.Finish(Crc32CUtil.Append(Crc32CUtil.Append(Crc32CUtil.Start, _reader.HeaderBytes), _reader.LoadedData[..length]));
                }
            }

            if (archived != null)
            {
                if (crc != archived.Footer.RawCrc)
                {
                    Diverge($"local segment {s.Id} differs from its archived copy");
                    return false;
                }

                continue;
            }

            // Not archived under its id: inside a recorded gap of the archive — unless the archive holds its records under another id (two histories).
            // By records, not headers: the first segment after a crash open starts, in its header, below the previous one's end.
            foreach (var a in archive.Archived)
            {
                if (scan.FirstRecordLsn != 0 && a.Footer.FirstRecordLsn != 0 && scan.FirstRecordLsn <= a.Footer.LastRecordLsn
                    && a.Footer.FirstRecordLsn <= scan.LastRecordLsn)
                {
                    Diverge($"local segment {s.Id} holds LSNs {scan.FirstRecordLsn}..{scan.LastRecordLsn}, archived under segment {a.SegmentId}");
                    return false;
                }
            }
        }

        return true;
    }

    private bool StoreHasPointOnTimeline(Guid timeline)
    {
        try
        {
            foreach (var p in StoreView.Open(_storePath, _fs, _log).Catalog.Packs)
            {
                if (p.TimelineId == timeline)
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

    /// <summary>
    /// Archives one sealed segment (impl/1418 §3.3 ArchiveOne). False when archiving must stop or retry later. Sets the span's outcome
    /// (<see cref="BackupArchiveSegmentOutcome"/>): published or already archived ⇒ Archived, with the archived file's bytes; diverged or no timeline to
    /// write to ⇒ Failed; the archiver stopping, or the segment reclaimed by a released cycle before its copy ⇒ Abandoned.
    /// </summary>
    private bool ArchiveOne(in WalSealedSegment s, ref BackupArchiveSegmentEvent span)
    {
        var archivedThrough = ArchivedThroughLsn;

        // a  Read and scan the segment, under the engine-access lock (the WAL's own file I/O, delete sharing: WA-5).
        WalSegmentScan scan;
        long durable;
        lock (_engineAccess)
        {
            if (_stopped)
            {
                span.Outcome = (byte)BackupArchiveSegmentOutcome.Abandoned;
                return false;
            }

            durable = _wal.DurableLsn;
            if (!_reader.OpenSegmentForArchive(s.Path, s.WrittenBytes))
            {
                if (_wal.FileIO.Exists(s.Path))
                {
                    // Still there, still listed: its header no longer validates. Taken as reclaimed, the next pass would find it again at once (a hot
                    // loop for as long as the floor holds it); a failure backs off, and the backlog policy's release ends it (a recorded gap).
                    throw new InvalidDataException($"WAL segment {s.Id} ({s.Path}) has no valid header: it cannot be archived.");
                }

                // Reclaimed by a released cycle: gone from the list too; the next segment's continuity records the gap.
                span.Outcome = (byte)BackupArchiveSegmentOutcome.Abandoned;
                return true;
            }

            scan = WalArchiveScanner.Scan(_reader);
        }

        if (s.LastLsn > durable)
        {
            throw new InvalidOperationException($"WA-15: segment {s.Id} ends at LSN {s.LastLsn}, past the durable LSN {durable}.");
        }

        // c  Continuity, by LSN (WA-8): a hole is a gap, recorded with its cause; a record inside the archive is another history (WA-14). The first segment
        // after a crash open may start, in its header, below the archive's end: the open's first scan names the last chunk's first record as its frontier,
        // its records start past the recovered one; what counts is that none of them is already archived.
        if (archivedThrough > 0 && scan.FirstRecordLsn != 0 && scan.FirstRecordLsn <= archivedThrough)
        {
            Diverge($"segment {s.Id} holds LSN {scan.FirstRecordLsn}, inside the archive (archived through {archivedThrough})");
            span.Outcome = (byte)BackupArchiveSegmentOutcome.Failed;
            return false;
        }

        // b  Its timeline (WA-22): the newest whose origin is below its last LSN — a fork's parent keeps what was written before the fork.
        var entry = _engine.TimelineFor(s.LastLsn);
        var target = OpenTimeline(entry);
        if (target == null)
        {
            span.Outcome = (byte)BackupArchiveSegmentOutcome.Failed;
            return false;
        }

        var footer = new ArchivedSegmentFooter
        {
            LastLsn = s.LastLsn,
            LastRecordLsn = scan.LastRecordLsn,
            LastUnitLsn = scan.LastUnitLsn,
            FirstRecordLsn = scan.FirstRecordLsn,
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
            StoreId = _storeId,
            TimelineId = entry.TimelineId,
            SegmentId = s.Id,
            FirstLsn = scan.FirstLsn,
            DeclaredSize = scan.DeclaredSize,
            SealedUtcTicks = s.SealedUtcTicks,
            PrevSegmentLsn = scan.PrevSegmentLsn,
        };

        // d  Write and publish under the timeline's lock, for this one segment (WA-14, WA-24).
        var sampleCount = TakeSamples(s.FirstLsn, s.LastLsn);
        var timeline = target.Handle;
        if (!timeline.TryReacquire())
        {
            throw new IOException($"the archive lock of timeline {entry.TimelineId} is held (a retention run)");
        }

        (ArchivePublishOutcome Outcome, ArchivedSegmentInfo Segment) published;
        try
        {
            published = timeline.Publish(_writer, header, _reader.HeaderBytes,
                _reader.LoadedData[..(scan.ArchiveLength - WalSegmentHeader.SizeInBytes)], footer, _segmentSamples.AsSpan(0, sampleCount));
            if (published.Outcome == ArchivePublishOutcome.Diverged)
            {
                Diverge($"segment {s.Id} is archived with other bytes (another database copy archives to this timeline)");
                span.Outcome = (byte)BackupArchiveSegmentOutcome.Failed;
                return false;
            }

            span.Outcome = (byte)BackupArchiveSegmentOutcome.Archived;
            span.Bytes = published.Segment.FileLength;

            if (published.Outcome == ArchivePublishOutcome.Published)
            {
                target.Archived.Add(published.Segment);
                if (++target.PublishesSinceIndex >= IndexWriteEvery)
                {
                    timeline.WriteIndex(target.Archived);
                    target.PublishesSinceIndex = 0;
                }
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

        var end = Math.Max(s.LastLsn, scan.LastRecordLsn);
        Volatile.Write(ref _archivedThroughLsn, end);
        Volatile.Write(ref _lastArchivedSealTicks, s.SealedUtcTicks);
        _releasesAtLastArchive = releases;
        _archivedThisSession = true;
        DropSamples(end);
        lock (_engineAccess)
        {
            if (_stopped)
            {
                return false;
            }

            _engine.CheckpointManager?.RaiseArchiveFloor(end + 1);
        }

        WalArchiveLog.SegmentArchived(_log, s.Id, s.FirstLsn, s.LastLsn, scan.ArchiveLength, published.Segment.FileLength);
        return true;
    }

    private void RecordGap(long from, long to, ArchiveGapReason reason)
    {
        Volatile.Write(ref _lastGapFrom, from);
        Volatile.Write(ref _lastGapReason, (int)reason);
        Volatile.Write(ref _lastGapTo, to);
        Interlocked.Increment(ref _gapsTotal);
        Volatile.Write(ref _pointRequested, 1);
        WalArchiveLog.GapRecorded(_log, _engine.TimelineId, from, to, reason);
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
        WalArchiveLog.ArchiveDiverged(_log, _engine.TimelineId, why);
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

            // Stalled from an earlier failure says nothing of the store now: the woken worker retries at once, and only a pass started after this wake
            // that fails again ends the wait (the next open archives the rest).
            var passesBefore = Volatile.Read(ref _passesStarted);
            _wake.Set();
            while (clock.Elapsed < budget && HasPendingWork())
            {
                if (Volatile.Read(ref _lastFailedPass) > passesBefore)
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
        if (_thread != null && !_thread.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)
                            && Interlocked.CompareExchange(ref _workerHandoff, WorkerDetached, WorkerRunning) == WorkerRunning)
        {
            // Still running (an as-crash Stop does not wait; a store call that does not return): the worker keeps its buffers and frees them at its exit,
            // never under it.
            _detached = true;
        }
        else if (_started)
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
        foreach (var archive in _timelines.Values)
        {
            if (archive.PublishesSinceIndex == 0)
            {
                continue;
            }

            try
            {
                if (archive.Handle.TryReacquire())
                {
                    archive.Handle.WriteIndex(archive.Archived);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                WalArchiveLog.StoreUnavailable(_log, _storePath, e.Message);
            }
            finally
            {
                archive.Handle.ReleaseLock();
            }
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
                // The worker still runs and owns the reader, the writer's blocks (parented here) and the store handles; it frees them at its exit.
                Parent?.RemoveChild(this);
                return;
            }

            FreeWorkerResources();
            _wake.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The worker's reader, writer and store handles, freed once: by the dispose when the worker ended, else by the worker at its exit.</summary>
    private void FreeWorkerResources()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        foreach (var archive in _timelines.Values)
        {
            archive.Handle.Dispose();
        }

        Volatile.Write(ref _workerResourcesFreed, true);
    }

    private bool _workerResourcesFreed;

    /// <summary>Whether the worker's native buffers and store handles were freed (tests: a detached worker frees them at its exit).</summary>
    internal bool WorkerResourcesFreed => Volatile.Read(ref _workerResourcesFreed);
}
