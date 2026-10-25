using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Context for the currently active WAL segment (handle + write position).
/// </summary>
[PublicAPI]
internal sealed class WalSegmentContext : IDisposable
{
    /// <summary>File handle for the active segment.</summary>
    public SafeFileHandle Handle { get; internal set; }

    /// <summary>Current write offset within the segment (starts at <see cref="WalSegmentHeader.SizeInBytes"/>).</summary>
    public long WriteOffset { get; internal set; }

    private long _batchEndOffset = WalSegmentHeader.SizeInBytes;

    /// <summary>
    /// <see cref="WriteOffset"/> as of the last batch written whole, published by <see cref="CompleteBatch"/> before the durable LSN moves over it. A batch
    /// larger than the staging buffer is written in several slices, cut anywhere in a chunk, and <see cref="WriteOffset"/> advances per slice: a reader
    /// that must never end inside a batch (a backup's tail, BKP-04) reads this instead.
    /// </summary>
    public long BatchEndOffset => Volatile.Read(ref _batchEndOffset);

    /// <summary>Called by the WAL writer once a batch is wholly written, before it advances the durable LSN over it.</summary>
    internal void CompleteBatch() => Volatile.Write(ref _batchEndOffset, WriteOffset);

    /// <summary>Segment identifier.</summary>
    public long SegmentId { get; internal set; }

    /// <summary>Total segment file size.</summary>
    public uint SegmentSize { get; internal set; }

    /// <summary>Path to the segment file.</summary>
    public string Path { get; internal set; }

    /// <summary>First LSN assigned to records in this segment.</summary>
    public long FirstLSN { get; internal set; }

    /// <summary>Last LSN written to this segment (updated as records are written).</summary>
    public long LastLSN { get; internal set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Handle?.Dispose();
        Handle = null;
    }
}

/// <summary>
/// Manages WAL segment file lifecycle: creation, pre-allocation, rotation, and reclamation.
/// </summary>
/// <remarks>
/// <para>
/// Segment files follow the naming convention <c>{segmentId:D16}.wal</c> in the configured WAL directory. Default segment size is 64 MB. A pool of
/// pre-allocated segments (default 4) is maintained ahead of the active write position to avoid filesystem metadata writes during normal operation.
/// </para>
/// <para>
/// Segment rotation occurs when the WAL writer detects the active segment has reached 75% capacity. The current segment is sealed and the next
/// pre-allocated segment becomes active.
/// </para>
/// </remarks>
[PublicAPI]
internal sealed class WalSegmentManager : IDisposable
{
    private readonly IWalFileIO _fileIO;
    private readonly string _walDirectory;
    private readonly uint _segmentSize;
    private readonly uint _initialSegmentSize;
    private readonly int _preAllocateCount;
    private readonly bool _useFUA;

    private long _nextSegmentId;
    private long _lastPreAllocatedSegmentId;
    private bool _disposed;

    /// <summary>
    /// Sealed segments awaiting reclamation, ascending id (= ascending LSN). Each entry records the segment's file path and last LSN so that
    /// <see cref="MarkReclaimable"/> can determine if all records have been checkpointed, plus its declared byte size — segments are NOT all
    /// <see cref="_segmentSize"/> (the first segment of a lifecycle uses <see cref="_initialSegmentSize"/>, and an oversized batch gets a segment sized to
    /// hold it), so <see cref="TotalWalBytes"/> must sum real sizes rather than multiply a count. <c>WrittenBytes</c> is where the writer stopped writing
    /// (the rest of the file is zeros), or the declared size for a segment adopted at open, whose written length was not recorded.
    /// </summary>
    private readonly List<WalSealedSegment> _sealedSegments = new();

    /// <summary>Bumped by every rotation, under <see cref="_sealedLock"/>: the WAL archiver's cheap "a segment was sealed" check.</summary>
    private long _sealedGeneration;

    /// <summary>
    /// Serializes all <see cref="_sealedSegments"/> access (CK-07 / TXW-7): the WAL writer thread adds on rotation,
    /// the checkpoint thread removes on reclaim, and introspection readers enumerate — without this the plain
    /// <see cref="List{T}"/> can corrupt under concurrent structural mutation. Off all hot paths.
    /// </summary>
    private readonly Lock _sealedLock = new();

    /// <summary>The currently active segment for writing.</summary>
    public WalSegmentContext ActiveSegment { get; private set; }

    /// <summary>
    /// Creates a new segment manager.
    /// </summary>
    /// <param name="fileIO">Platform I/O abstraction.</param>
    /// <param name="walDirectory">Directory for WAL segment files.</param>
    /// <param name="segmentSize">Size of each rotated segment file in bytes (default 64 MB).</param>
    /// <param name="preAllocateCount">Number of segments to pre-allocate ahead (default 4).</param>
    /// <param name="useFUA">Whether to open segments with FUA (for Immediate durability mode).</param>
    /// <param name="initialSegmentSize">
    /// Size of the segment <see cref="Initialize"/> creates, in bytes. 0 means "same as <paramref name="segmentSize"/>". A database that never rotates
    /// keeps exactly this one file, so sizing it for steady-state throughput costs every small database the full <paramref name="segmentSize"/> on disk
    /// forever (#784). Rotated segments are unaffected — from the first rotation on, everything is <paramref name="segmentSize"/> as before.
    /// </param>
    public WalSegmentManager(IWalFileIO fileIO, string walDirectory, uint segmentSize, int preAllocateCount, bool useFUA, uint initialSegmentSize = 0)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(walDirectory);

        _fileIO = fileIO;
        _walDirectory = walDirectory;
        _segmentSize = segmentSize;
        _initialSegmentSize = initialSegmentSize == 0 ? segmentSize : Math.Min(initialSegmentSize, segmentSize);
        _preAllocateCount = preAllocateCount;
        _useFUA = useFUA;
    }

    /// <summary>
    /// Initializes the segment manager, creating the WAL directory and first segment. On reopen, first reconciles WAL
    /// segment files left on disk by the previous engine lifecycle (see <see cref="ReconcileExistingSegments"/>): empty
    /// placeholders are deleted, valid segments are adopted for normal checkpoint-gated reclamation, and segment
    /// numbering continues past the highest existing id. Without this, ids restart at 1 every open and prior-lifecycle
    /// files orphan forever (their data is in the data file, but nothing ever deletes them). See WR-01 in durability.md.
    /// </summary>
    /// <param name="lastSegmentId">Last known segment ID (0 for fresh start). A floor only — the on-disk scan wins if higher.</param>
    /// <param name="firstLSN">First LSN for the initial (fresh) active segment — the recovery frontier + 1.</param>
    /// <param name="checkpointLsn">Checkpoint frontier. Adopted segments entirely below this are reclaimed immediately
    /// (their records are already in the data file); segments with records ≥ this are retained for recovery and reclaimed
    /// by the next checkpoint. Records ≥ checkpointLsn are NEVER deleted here.</param>
    /// <param name="reclaimFloorAtOpen">
    /// The WAL archive's floor at open (BKP-10, WA-3): no adopted segment whose last LSN is at or above it is reclaimed here. 0 when archiving is
    /// configured — the archive may not hold them yet, and nothing has spoken for it since the last session; <see cref="long.MaxValue"/> when off.
    /// </param>
    public void Initialize(long lastSegmentId, long firstLSN, long checkpointLsn = 0, long reclaimFloorAtOpen = long.MaxValue)
    {
        if (!Directory.Exists(_walDirectory))
        {
            Directory.CreateDirectory(_walDirectory);
        }

        // Reconcile leftover on-disk segments: delete empty placeholders, adopt valid segments into _sealedSegments, and
        // return the highest existing id so numbering continues (no restart-at-1, no filename collision with the new active).
        var recoveryLastValidLsn = firstLSN > 0 ? firstLSN - 1 : 0;
        var maxExistingId = ReconcileExistingSegments(recoveryLastValidLsn);

        _nextSegmentId = Math.Max(lastSegmentId, maxExistingId) + 1;

        // Create and open the first active segment, at the (possibly smaller) initial size — see _initialSegmentSize.
        ActiveSegment = CreateSegment(_nextSegmentId, firstLSN, 0, _initialSegmentSize);

        // Pre-allocation must continue strictly AFTER the active segment id — otherwise EnsurePreAllocated would walk up
        // from id 1 and recreate the very placeholders we just reclaimed (and any retained real segments' ids).
        _lastPreAllocatedSegmentId = _nextSegmentId;
        _nextSegmentId++;

        // NO pre-allocation here (#784). The pool exists so a rotation never blocks on file creation, but at Initialize it is
        // not yet known whether this database will EVER rotate — and most never do. Building it eagerly cost every database
        // _preAllocateCount * _segmentSize of empty files (320 MiB at the defaults) that MarkReclaimable cannot see, because
        // placeholders are never in _sealedSegments. The first RotateSegment finds no spare, falls through to its existing
        // CreateSegment branch, and its trailing EnsurePreAllocated() builds the full pool — so a database that does rotate
        // is back to the previous steady state from rotation #1, with no new state to track.

        // Reclaim adopted segments whose records are all below the checkpoint frontier (data already in the data file).
        // Segments with records ≥ checkpointLsn stay in _sealedSegments and are reclaimed by the next checkpoint cycle —
        // same gate as steady-state recycling. Reuses MarkReclaimable so the reopen path invents no new frontier. With archiving configured the
        // archive floor joins the gate, as at step 8: a close with the store unreachable must not lose its un-archived segments to the reopen (WA-3).
        MarkReclaimable(Math.Min(checkpointLsn, reclaimFloorAtOpen));
    }

    /// <summary>
    /// Reconciles WAL segment files left on disk by a previous engine lifecycle. Deletes files with no valid header
    /// (empty/zero pre-allocated placeholders — <see cref="WalSegmentReader.OpenSegment"/> rejects them and recovery skips
    /// them, so they hold zero records and deleting them cannot change recovery). Adopts each valid segment into
    /// <see cref="_sealedSegments"/> with its computed LastLSN (chain rule: a segment's LSNs are below the next segment's
    /// <see cref="WalSegmentHeader.FirstLSN"/>; the last runs to the recovery frontier) so the normal checkpoint gate can
    /// reclaim it. Returns the highest existing segment id (0 if none). Does NOT itself delete any valid segment — the
    /// caller's <see cref="MarkReclaimable"/> applies the checkpoint frontier. See WR-01.
    /// </summary>
    private long ReconcileExistingSegments(long recoveryLastValidLsn)
    {
        if (!Directory.Exists(_walDirectory))
        {
            return 0;
        }

        var files = Directory.GetFiles(_walDirectory, "*.wal");
        if (files.Length == 0)
        {
            return 0;
        }

        var valid = new List<(long Id, long FirstLsn, string Path, uint SizeBytes)>(files.Length);
        long maxId = 0;
        foreach (var path in files)
        {
            var id = ParseSegmentIdFromPath(path);
            if (id > maxId)
            {
                maxId = id;
            }

            if (TryReadSegmentFirstLsn(path, out var firstLsn, out var sizeBytes))
            {
                valid.Add((id, firstLsn, path, sizeBytes));
            }
            else
            {
                // No valid header → empty/zero placeholder. Recovery skips these; deleting them is always safe.
                _fileIO.Delete(path);
            }
        }

        valid.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        for (var i = 0; i < valid.Count; i++)
        {
            var lastLsn = (i + 1 < valid.Count) ? valid[i + 1].FirstLsn - 1 : recoveryLastValidLsn;
            if (lastLsn < valid[i].FirstLsn)
            {
                // The segment covers no live LSN (header-only / never written, e.g. the active segment a read-only
                // session creates but never writes to). Recovery extracted nothing from it, so it is reclaimable now —
                // delete it rather than adopt. Adopting (clamping LastLSN up to FirstLSN) would make it look non-empty
                // and ≥ the frontier, so MarkReclaimable would retain it → one leaked segment per reopen.
                _fileIO.Delete(valid[i].Path);
                continue;
            }

            // Single-threaded open path, but locked for consistency with the concurrent writers (CK-07).
            lock (_sealedLock)
            {
                _sealedSegments.Add(new WalSealedSegment(valid[i].Path, valid[i].Id, valid[i].FirstLsn, lastLsn, valid[i].SizeBytes, valid[i].SizeBytes, 0,
                    true));
            }
        }

        return maxId;
    }

    /// <summary>Parses the segment id from a <c>{id:D16}.wal</c> path; 0 if unparseable.</summary>
    private static long ParseSegmentIdFromPath(string path)
        => long.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : 0;

    /// <summary>Reads + validates a segment header, returning its <see cref="WalSegmentHeader.FirstLSN"/> and its declared
    /// <see cref="WalSegmentHeader.SegmentSize"/>. False if the file has no valid header (empty placeholder or torn).</summary>
    private bool TryReadSegmentFirstLsn(string path, out long firstLsn, out uint sizeBytes)
    {
        firstLsn = 0;
        sizeBytes = 0;
        try
        {
            using var handle = _fileIO.OpenSegmentForRead(path);
            var headerBuffer = new byte[WalSegmentHeader.SizeInBytes];
            _fileIO.ReadAligned(handle, 0, headerBuffer);
            var header = MemoryMarshal.Read<WalSegmentHeader>(headerBuffer);
            if (!header.Validate())
            {
                return false;
            }
            firstLsn = header.FirstLSN;
            sizeBytes = header.SegmentSize;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Seals the current active segment and rotates to the next pre-allocated segment.
    /// </summary>
    /// <param name="firstLSN">First LSN for the new segment.</param>
    /// <param name="prevLastLSN">Last LSN of the segment being sealed.</param>
    /// <param name="minDataBytes">
    /// Bytes the caller is about to write into the new segment. The segment is sized to hold them whatever
    /// <see cref="_segmentSize"/> says, so a batch larger than a whole configured segment still lands inside a declared
    /// region instead of past the end of the file (WR-03 / #785). 0 for a rotation with nothing pending.
    /// </param>
    /// <returns>The new active segment context.</returns>
    public WalSegmentContext RotateSegment(long firstLSN, long prevLastLSN, long minDataBytes = 0)
    {
        var oldSegment = ActiveSegment;
        oldSegment.LastLSN = prevLastLSN;

        // Open next segment
        var nextId = _nextSegmentId;
        var path = GetSegmentPath(nextId);
        _nextSegmentId++;

        // A pre-allocated spare is _segmentSize; if this batch needs more, the spare cannot be used as-is and
        // CreateSegment re-sizes the file to fit (SetLength grows it — the file is a never-written placeholder).
        var requiredSize = RequiredSegmentSize(minDataBytes);

        WalSegmentContext newSegment;

        // If pre-allocated AND large enough, just open and write header
        if (_fileIO.Exists(path) && requiredSize <= _segmentSize)
        {
            var handle = _fileIO.OpenSegment(path, _useFUA);
            newSegment = new WalSegmentContext
            {
                Handle = handle,
                WriteOffset = WalSegmentHeader.SizeInBytes,
                SegmentId = nextId,
                SegmentSize = _segmentSize,
                Path = path,
                FirstLSN = firstLSN,
                LastLSN = firstLSN,
            };

            // Write header
            WriteSegmentHeader(newSegment, firstLSN, prevLastLSN);
        }
        else
        {
            newSegment = CreateSegment(nextId, firstLSN, prevLastLSN, requiredSize);
        }

        RotateProbe?.Invoke();

        // The new segment becomes active and the old one sealed in ONE step under the list lock (CK-07): a reader that snapshots both under the lock
        // (SnapshotForTail, ArchiveSnapshot) then sees the old segment either as the active one or as sealed, never as neither (BKP-04). Then close
        // its handle. The seal time is the archive's: one clock read per rotation.
        var sealedUtcTicks = DateTime.UtcNow.Ticks;
        lock (_sealedLock)
        {
            ActiveSegment = newSegment;
            _sealedSegments.Add(new WalSealedSegment(oldSegment.Path, oldSegment.SegmentId, oldSegment.FirstLSN, oldSegment.LastLSN, oldSegment.SegmentSize,
                oldSegment.WriteOffset, sealedUtcTicks, false));
            Volatile.Write(ref _sealedGeneration, _sealedGeneration + 1);
        }
        oldSegment.Dispose();

        // Replenish pre-allocated pool
        EnsurePreAllocated();

        return newSegment;
    }

    /// <summary>
    /// Deletes sealed WAL segment files whose records have all been checkpointed (LastLSN &lt; checkpointLSN). Returns the number of segments reclaimed.
    /// </summary>
    /// <param name="checkpointLSN">The checkpoint LSN — segments with LastLSN below this are safe to delete.</param>
    /// <returns>The number of segment files deleted.</returns>
    public int MarkReclaimable(long checkpointLSN)
    {
        int reclaimed = 0;

        // CK-07: the whole iterate+RemoveAt+Delete runs under the list lock. Reclaim is at checkpoint cadence (low
        // frequency), so holding the lock across the deletes is correctness-over-micro-contention — it also keeps a
        // concurrent GetAllSegmentPaths / SealedSegmentCount from observing a torn view.
        lock (_sealedLock)
        {
            for (int i = _sealedSegments.Count - 1; i >= 0; i--)
            {
                var segment = _sealedSegments[i];
                if (segment.LastLsn < checkpointLSN)
                {
                    _fileIO.Delete(segment.Path);
                    _sealedSegments.RemoveAt(i);
                    reclaimed++;
                }
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// The bytes on disk only the WAL archive's floor keeps (BKP-10's backlog): the declared sizes of the sealed segments with
    /// <paramref name="archiveFloor"/> ≤ last LSN &lt; <paramref name="trimLsn"/> — archived below the floor, reclaimable by the checkpoint below the
    /// trim. One pass under the list lock, no allocation; the checkpoint calls it at step 8 only when archiving holds something back.
    /// </summary>
    internal long BytesHeldBetween(long archiveFloor, long trimLsn)
    {
        long held = 0;
        lock (_sealedLock)
        {
            foreach (var segment in _sealedSegments)
            {
                if (segment.LastLsn >= archiveFloor && segment.LastLsn < trimLsn)
                {
                    held += segment.SizeBytes;
                }
            }
        }

        return held;
    }

    /// <summary>A copy of the sealed segments, ascending (the archiver's start-up check; allocates).</summary>
    internal List<WalSealedSegment> SealedSegments()
    {
        lock (_sealedLock)
        {
            return new List<WalSealedSegment>(_sealedSegments);
        }
    }

    /// <summary>Bumped by every rotation: a change tells the WAL archiver a segment was sealed.</summary>
    internal long SealedGeneration => Volatile.Read(ref _sealedGeneration);

    /// <summary>
    /// What the WAL archiver needs to know, read in one step under the list lock (CK-07) without allocating: the oldest sealed segment whose last LSN
    /// is past <paramref name="archivedThroughLsn"/> (the next to archive), how many such segments there are and how many bytes they hold, the bytes
    /// the archive floor alone keeps, and the active segment.
    /// </summary>
    internal WalArchiveSnapshot ArchiveSnapshot(long archivedThroughLsn, long trimLsn)
    {
        lock (_sealedLock)
        {
            WalSealedSegment next = default;
            var pending = 0;
            long pendingBytes = 0;
            long heldBytes = 0;
            foreach (var segment in _sealedSegments)
            {
                if (segment.LastLsn <= archivedThroughLsn)
                {
                    continue;
                }

                if (pending++ == 0)
                {
                    next = segment;
                }

                pendingBytes += segment.WrittenBytes;
                if (segment.LastLsn < trimLsn)
                {
                    heldBytes += segment.SizeBytes;
                }
            }

            var active = ActiveSegment;
            return new WalArchiveSnapshot(next, pending, pendingBytes, heldBytes, active?.SegmentId ?? 0, active?.FirstLSN ?? 0,
                active == null ? 0 : active.BatchEndOffset, _sealedGeneration);
        }
    }

    /// <summary>
    /// Returns the number of sealed segments awaiting reclamation.
    /// </summary>
    public int SealedSegmentCount
    {
        get
        {
            lock (_sealedLock)
            {
                return _sealedSegments.Count;
            }
        }
    }

    /// <summary>
    /// Total byte size of the WAL across all segment files — each sealed segment at its own declared size plus
    /// the active segment's current write offset. Read-only; for storage introspection (Database File Map).
    /// </summary>
    /// <remarks>
    /// Sums per-segment sizes rather than multiplying a count: segments are no longer uniformly <see cref="_segmentSize"/>
    /// (the first segment of a lifecycle is <see cref="_initialSegmentSize"/>, and an oversized batch gets a segment sized
    /// to hold it), so a count * size product would over-report.
    /// </remarks>
    public long TotalWalBytes
    {
        get
        {
            long sealedBytes = 0;
            lock (_sealedLock)
            {
                for (var i = 0; i < _sealedSegments.Count; i++)
                {
                    sealedBytes += _sealedSegments[i].SizeBytes;
                }
            }

            return sealedBytes + (ActiveSegment?.WriteOffset ?? 0L);
        }
    }

    /// <summary>
    /// The size a segment must have to hold <paramref name="dataBytes"/> of records: at least the configured
    /// <see cref="_segmentSize"/>, and always enough for the header plus the data, rounded up to the 4 KiB alignment
    /// direct I/O requires. See WR-03.
    /// </summary>
    private uint RequiredSegmentSize(long dataBytes)
    {
        var needed = WalSegmentHeader.SizeInBytes + Math.Max(0, dataBytes);
        if (needed <= _segmentSize)
        {
            return _segmentSize;
        }

        // Round up to the direct-I/O page size; the cast is safe because a batch is bounded by the commit buffer half.
        return (uint)((needed + (WalPageSize - 1)) & ~((long)WalPageSize - 1));
    }

    /// <summary>Direct-I/O alignment for segment sizing (matches the WAL writer's page alignment).</summary>
    private const int WalPageSize = 4096;

    /// <summary>
    /// Whether <paramref name="dataBytes"/> more bytes fit inside the active segment's declared size. The WAL writer
    /// calls this BEFORE writing and rotates when it returns false — without that, the write runs past the declared end
    /// of the file and <see cref="WalSegmentReader"/>, which bounds its scan by the header's declared size, cannot see
    /// those records at recovery (WR-03 / #785).
    /// </summary>
    public bool ActiveSegmentCanHold(long dataBytes)
    {
        var segment = ActiveSegment;
        return segment != null && segment.WriteOffset + dataBytes <= segment.SegmentSize;
    }

    /// <summary>
    /// Ensures the pre-allocation pool is full (creates new empty segment files as needed).
    /// </summary>
    public void EnsurePreAllocated()
    {
        var targetId = _nextSegmentId + _preAllocateCount - 1;

        while (_lastPreAllocatedSegmentId < targetId)
        {
            _lastPreAllocatedSegmentId++;
            var path = GetSegmentPath(_lastPreAllocatedSegmentId);

            if (!_fileIO.Exists(path))
            {
                PreAllocateSegmentFile(path);
            }
        }
    }

    /// <summary>
    /// Returns the ratio of used space in the active segment (0.0 to 1.0).
    /// </summary>
    /// <remarks>
    /// Measured against the ACTIVE segment's own declared size, not the configured <see cref="_segmentSize"/>. Segments
    /// are not uniformly sized (see <see cref="_initialSegmentSize"/>), and dividing by the configured size would let a
    /// smaller segment report a fraction of its true fill — a 16 MiB segment measured against 64 MiB reaches the
    /// rotation threshold only at 48 MiB of writes, which it cannot hold.
    /// </remarks>
    public double ActiveSegmentUtilization
    {
        get
        {
            if (ActiveSegment == null)
            {
                return 0;
            }

            return (double)ActiveSegment.WriteOffset / ActiveSegment.SegmentSize;
        }
    }

    /// <summary>
    /// Returns the file path for a given segment ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetSegmentPath(long segmentId) => Path.Combine(_walDirectory, $"{segmentId:D16}.wal");

    private WalSegmentContext CreateSegment(long segmentId, long firstLsn, long prevSegmentLsn, uint segmentSize)
    {
        var path = GetSegmentPath(segmentId);
        var handle = _fileIO.OpenSegment(path, _useFUA);

        // Pre-allocate the file
        _fileIO.PreAllocate(handle, segmentSize);

        var context = new WalSegmentContext
        {
            Handle = handle,
            WriteOffset = WalSegmentHeader.SizeInBytes,
            SegmentId = segmentId,
            SegmentSize = segmentSize,
            Path = path,
            FirstLSN = firstLsn,
            LastLSN = firstLsn,
        };

        WriteSegmentHeader(context, firstLsn, prevSegmentLsn);

        return context;
    }

    private unsafe void WriteSegmentHeader(WalSegmentContext context, long firstLsn, long prevSegmentLsn)
    {
        var header = new WalSegmentHeader();
        // The context's OWN size, not the configured one — the header is what recovery bounds its scan by, so a segment
        // that was created larger (or smaller) than _segmentSize must declare what it actually is.
        header.Initialize(context.SegmentId, firstLsn, prevSegmentLsn, context.SegmentSize);
        header.ComputeAndSetCrc();

        var headerBytes = new byte[WalSegmentHeader.SizeInBytes];
        MemoryMarshal.Write(headerBytes, in header);   // into the managed array through a span, not a pinned pointer

        _fileIO.WriteAligned(context.Handle, 0, headerBytes);
    }

    private void PreAllocateSegmentFile(string path)
    {
        // Create empty file and set its size, but don't write a header yet
        // (header is written during rotation when we know the firstLSN)
        using var handle = _fileIO.OpenSegment(path, false);
        _fileIO.PreAllocate(handle, _segmentSize);
    }

    /// <summary>
    /// Returns paths of all known WAL segment files: sealed segments (awaiting reclamation) plus the active segment.
    /// Used by the recovery scan to read every retained segment.
    /// </summary>
    internal List<string> GetAllSegmentPaths()
    {
        List<string> paths;
        lock (_sealedLock)
        {
            paths = new List<string>(_sealedSegments.Count + 1);
            foreach (var segment in _sealedSegments)
            {
                paths.Add(segment.Path);
            }
        }

        if (ActiveSegment != null)
        {
            paths.Add(ActiveSegment.Path);
        }

        return paths;
    }

    /// <summary>
    /// The segments an online backup's tail may need, read in one step under the list lock (BKP-04, CK-07): every sealed segment, then the active one,
    /// in ascending id. The active segment's written length is its <see cref="WalSegmentContext.BatchEndOffset"/>: the caller reads it only after
    /// observing the durable LSN it needs, so it covers every record up to that LSN (the writer publishes it before it advances the durable LSN), and it
    /// never ends inside a batch written in slices. A sealed segment's is its final write offset, where a batch ended (rotation is between batches).
    /// </summary>
    internal List<WalTailSegment> SnapshotForTail()
    {
        lock (_sealedLock)
        {
            var result = new List<WalTailSegment>(_sealedSegments.Count + 1);
            foreach (var segment in _sealedSegments)
            {
                result.Add(new WalTailSegment(segment.Id, segment.Path, segment.LastLsn, (uint)segment.SizeBytes, segment.WrittenBytes, false));
            }

            var active = ActiveSegment;
            if (active != null)
            {
                result.Add(new WalTailSegment(active.SegmentId, active.Path, long.MaxValue, active.SegmentSize, active.BatchEndOffset, true));
            }

            result.Sort(static (a, b) => a.SegmentId.CompareTo(b.SegmentId));
            return result;
        }
    }

    /// <summary>Test hook: invoked by <see cref="RotateSegment"/> once the new segment exists, before it is made active. Null in production.</summary>
    internal Action RotateProbe { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ActiveSegment?.Dispose();
        ActiveSegment = null;
    }
}

/// <summary>One WAL segment as an online backup's tail sees it (<see cref="WalSegmentManager.SnapshotForTail"/>).</summary>
/// <param name="SegmentId">The segment's id, also its file name.</param>
/// <param name="Path">The segment file.</param>
/// <param name="LastLsn">The last LSN the segment holds; <see cref="long.MaxValue"/> for the active segment, whose last LSN is not known yet.</param>
/// <param name="DeclaredSize">The size its header declares.</param>
/// <param name="WrittenBytes">Where the writer stopped writing it; every byte past it is zero.</param>
/// <param name="IsActive">Whether it is the segment being written.</param>
internal readonly record struct WalTailSegment(long SegmentId, string Path, long LastLsn, uint DeclaredSize, long WrittenBytes, bool IsActive);

/// <summary>A sealed WAL segment, as <see cref="WalSegmentManager"/> keeps it until the checkpoint reclaims it.</summary>
/// <param name="Path">The segment file.</param>
/// <param name="Id">The segment's id, also its file name.</param>
/// <param name="FirstLsn">Its header's first LSN.</param>
/// <param name="LastLsn">The last LSN it holds; <c>FirstLsn - 1</c> for a segment holding none.</param>
/// <param name="SizeBytes">The size its header declares.</param>
/// <param name="WrittenBytes">Where the writer stopped writing it (a batch end); the declared size for a segment adopted at open.</param>
/// <param name="SealedUtcTicks">When this session sealed it; 0 for a segment adopted at open (unknown).</param>
/// <param name="Adopted">Whether the open adopted it from a previous session rather than this session sealing it.</param>
internal readonly record struct WalSealedSegment(string Path, long Id, long FirstLsn, long LastLsn, long SizeBytes, long WrittenBytes, long SealedUtcTicks,
    bool Adopted);

/// <summary>What the WAL archiver reads of the segment manager in one step (<see cref="WalSegmentManager.ArchiveSnapshot"/>).</summary>
/// <param name="Next">The oldest sealed segment not archived yet; meaningful only when <paramref name="PendingCount"/> &gt; 0.</param>
/// <param name="PendingCount">Sealed segments whose last LSN is past the archive's end.</param>
/// <param name="PendingWrittenBytes">Their written bytes.</param>
/// <param name="HeldBytes">The declared bytes of those the checkpoint could reclaim but for the archive.</param>
/// <param name="ActiveSegmentId">The segment being written.</param>
/// <param name="ActiveFirstLsn">Its header's first LSN.</param>
/// <param name="ActiveWrittenBytes">Its written length at a batch end.</param>
/// <param name="SealedGeneration">The rotation count at the snapshot.</param>
internal readonly record struct WalArchiveSnapshot(WalSealedSegment Next, int PendingCount, long PendingWrittenBytes, long HeldBytes, long ActiveSegmentId,
    long ActiveFirstLsn, long ActiveWrittenBytes, long SealedGeneration);
