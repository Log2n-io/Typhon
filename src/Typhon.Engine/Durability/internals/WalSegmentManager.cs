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
    private readonly int _preAllocateCount;
    private readonly bool _useFUA;

    private long _nextSegmentId;
    private long _lastPreAllocatedSegmentId;
    private bool _disposed;

    /// <summary>
    /// Sealed segments awaiting reclamation. Each entry records the segment's file path and last LSN so that <see cref="MarkReclaimable"/> can determine
    /// if all records have been checkpointed.
    /// </summary>
    private readonly List<(string Path, long LastLSN)> _sealedSegments = new();

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
    /// <param name="segmentSize">Size of each segment file in bytes (default 64 MB).</param>
    /// <param name="preAllocateCount">Number of segments to pre-allocate ahead (default 4).</param>
    /// <param name="useFUA">Whether to open segments with FUA (for Immediate durability mode).</param>
    public WalSegmentManager(IWalFileIO fileIO, string walDirectory, uint segmentSize, int preAllocateCount, bool useFUA)
    {
        ArgumentNullException.ThrowIfNull(fileIO);
        ArgumentNullException.ThrowIfNull(walDirectory);

        _fileIO = fileIO;
        _walDirectory = walDirectory;
        _segmentSize = segmentSize;
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
    public void Initialize(long lastSegmentId, long firstLSN, long checkpointLsn = 0)
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

        // Create and open the first active segment
        ActiveSegment = CreateSegment(_nextSegmentId, firstLSN, 0);

        // Pre-allocation must continue strictly AFTER the active segment id — otherwise EnsurePreAllocated would walk up
        // from id 1 and recreate the very placeholders we just reclaimed (and any retained real segments' ids).
        _lastPreAllocatedSegmentId = _nextSegmentId;
        _nextSegmentId++;

        // Pre-allocate additional segments
        EnsurePreAllocated();

        // Reclaim adopted segments whose records are all below the checkpoint frontier (data already in the data file).
        // Segments with records ≥ checkpointLsn stay in _sealedSegments and are reclaimed by the next checkpoint cycle —
        // same gate as steady-state recycling. Reuses MarkReclaimable so the reopen path invents no new frontier.
        MarkReclaimable(checkpointLsn);
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

        var valid = new List<(long Id, long FirstLsn, string Path)>(files.Length);
        long maxId = 0;
        foreach (var path in files)
        {
            var id = ParseSegmentIdFromPath(path);
            if (id > maxId)
            {
                maxId = id;
            }

            if (TryReadSegmentFirstLsn(path, out var firstLsn))
            {
                valid.Add((id, firstLsn, path));
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
                _sealedSegments.Add((valid[i].Path, lastLsn));
            }
        }

        return maxId;
    }

    /// <summary>Parses the segment id from a <c>{id:D16}.wal</c> path; 0 if unparseable.</summary>
    private static long ParseSegmentIdFromPath(string path)
        => long.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : 0;

    /// <summary>Reads + validates a segment header, returning its <see cref="WalSegmentHeader.FirstLSN"/>. False if the
    /// file has no valid header (empty placeholder or torn).</summary>
    private bool TryReadSegmentFirstLsn(string path, out long firstLsn)
    {
        firstLsn = 0;
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
    /// <returns>The new active segment context.</returns>
    public WalSegmentContext RotateSegment(long firstLSN, long prevLastLSN)
    {
        var oldSegment = ActiveSegment;
        oldSegment.LastLSN = prevLastLSN;

        // Open next segment
        var nextId = _nextSegmentId;
        var path = GetSegmentPath(nextId);
        _nextSegmentId++;

        WalSegmentContext newSegment;

        // If pre-allocated, just open and write header
        if (_fileIO.Exists(path))
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
            newSegment = CreateSegment(nextId, firstLSN, prevLastLSN);
        }

        ActiveSegment = newSegment;

        // Track the sealed segment for checkpoint-based reclamation (CK-07: under the list lock), then close its handle
        lock (_sealedLock)
        {
            _sealedSegments.Add((oldSegment.Path, oldSegment.LastLSN));
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
                var (path, lastLSN) = _sealedSegments[i];
                if (lastLSN < checkpointLSN)
                {
                    _fileIO.Delete(path);
                    _sealedSegments.RemoveAt(i);
                    reclaimed++;
                }
            }
        }

        return reclaimed;
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
    /// Total byte size of the WAL across all segment files — sealed segments counted at full segment size plus
    /// the active segment's current write offset. Read-only; for storage introspection (Database File Map).
    /// </summary>
    public long TotalWalBytes => (SealedSegmentCount * _segmentSize) + (ActiveSegment?.WriteOffset ?? 0L);

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
    public double ActiveSegmentUtilization
    {
        get
        {
            if (ActiveSegment == null)
            {
                return 0;
            }

            return (double)ActiveSegment.WriteOffset / _segmentSize;
        }
    }

    /// <summary>
    /// Returns the file path for a given segment ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetSegmentPath(long segmentId) => Path.Combine(_walDirectory, $"{segmentId:D16}.wal");

    private WalSegmentContext CreateSegment(long segmentId, long firstLsn, long prevSegmentLsn)
    {
        var path = GetSegmentPath(segmentId);
        var handle = _fileIO.OpenSegment(path, _useFUA);

        // Pre-allocate the file
        _fileIO.PreAllocate(handle, _segmentSize);

        var context = new WalSegmentContext
        {
            Handle = handle,
            WriteOffset = WalSegmentHeader.SizeInBytes,
            SegmentId = segmentId,
            SegmentSize = _segmentSize,
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
        header.Initialize(context.SegmentId, firstLsn, prevSegmentLsn, _segmentSize);
        header.ComputeAndSetCrc();

        var headerBytes = new byte[WalSegmentHeader.SizeInBytes];
        fixed (byte* dst = headerBytes)
        {
            *(WalSegmentHeader*)dst = header;
        }

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
    /// Used by <see cref="WalManager.SearchFpiForPage"/> for on-the-fly FPI lookup.
    /// </summary>
    internal List<string> GetAllSegmentPaths()
    {
        List<string> paths;
        lock (_sealedLock)
        {
            paths = new List<string>(_sealedSegments.Count + 1);
            foreach (var (path, _) in _sealedSegments)
            {
                paths.Add(path);
            }
        }

        if (ActiveSegment != null)
        {
            paths.Add(ActiveSegment.Path);
        }

        return paths;
    }

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
