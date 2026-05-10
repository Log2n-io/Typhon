using JetBrains.Annotations;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

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
    /// Initializes the segment manager, creating the WAL directory and first segment.
    /// </summary>
    /// <param name="lastSegmentId">Last known segment ID (0 for fresh start).</param>
    /// <param name="firstLSN">First LSN for the initial segment.</param>
    public void Initialize(long lastSegmentId, long firstLSN)
    {
        if (!Directory.Exists(_walDirectory))
        {
            Directory.CreateDirectory(_walDirectory);
        }

        _nextSegmentId = lastSegmentId + 1;

        // Create and open the first active segment
        ActiveSegment = CreateSegment(_nextSegmentId, firstLSN, 0);
        _nextSegmentId++;

        // Pre-allocate additional segments
        EnsurePreAllocated();
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

        // Track the sealed segment for checkpoint-based reclamation, then close its handle
        _sealedSegments.Add((oldSegment.Path, oldSegment.LastLSN));
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

        return reclaimed;
    }

    /// <summary>
    /// Returns the number of sealed segments awaiting reclamation.
    /// </summary>
    public int SealedSegmentCount => _sealedSegments.Count;

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
        var paths = new List<string>(_sealedSegments.Count + 1);
        foreach (var (path, _) in _sealedSegments)
        {
            paths.Add(path);
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
