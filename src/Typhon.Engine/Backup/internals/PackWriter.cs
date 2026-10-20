using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary>Moves a sealed temporary pack to its final name and records it. Implemented by the store.</summary>
internal interface IPackPublisher
{
    /// <summary>
    /// Publishes <paramref name="writer"/>'s sealed, flushed and closed temp file (BKP-08) and returns the pack's final path. Either throws with
    /// nothing visible, or returns with the pack durable.
    /// </summary>
    string Publish(PackWriter writer);
}

/// <summary>Tuning a test can lower, and where the write buffers come from.</summary>
internal sealed class PackWriterOptions
{
    public static readonly PackWriterOptions Default = new();

    /// <summary>The engine whose resource tree the write buffers belong to (online capture); <c>null</c> for an engine-free tool.</summary>
    public BufferOwner Owner { get; init; }

    /// <summary>Size of each of the two write buffers.</summary>
    public int BufferSize { get; init; } = 4 << 20;

    /// <summary>Index entries sorted in memory before runs spill to sidecars (<see cref="PackIndexRuns"/>).</summary>
    public int IndexHeapEntries { get; init; } = PackIndexRuns.DefaultHeapEntries;

    /// <summary>
    /// Test only: bytes left unwritten (a sparse hole, zeros to the FileCRC) between the header and the first frame, so a test reaches 64-bit
    /// offsets without writing gigabytes. The format allows frames anywhere in <c>[0x100, DataRegionEnd)</c>.
    /// </summary>
    public long DataGapBytes { get; init; }
}

/// <summary>
/// Writes one backup point as a <c>.tpack</c> temp, strictly sequentially, and publishes it (plan 1413 §2.3). The sink capture (#1412) drives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-threaded by contract.</b> One thread calls it; writer threads never do. It takes no engine lock and never calls the engine.
/// </para>
/// <para>
/// <b>I/O.</b> Two native buffers: one fills while the other's asynchronous write is in flight, so <see cref="AddPage"/> waits on the disk only
/// when both are full. <c>FileCRC</c> is accumulated over each buffer once, in file order, just before its write is issued. Frames never straddle
/// a buffer: a buffer without room for the next frame is issued first.
/// </para>
/// <para>
/// <b>Validation.</b> A page out of <c>[0, N)</c>, a page added twice, a tail call out of order, or an image whose meta pair disagrees with the
/// header is a capture bug: <see cref="InvalidOperationException"/>, and capture aborts (BKP-02, BKP-03).
/// </para>
/// </remarks>
internal sealed class PackWriter : IBackupPointSink, IDisposable
{
    private enum State : byte
    {
        Created,
        Imaging,
        Failed,
        Completed,
        Aborted,
    }

    private readonly IStoreFileSystem _fs;
    private readonly IPackPublisher _publisher;
    private readonly PackWriterOptions _options;
    private readonly ILogger _log;
    private readonly PackIdentity _identity;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IStoreFile _file;
    private State _state;
    private PackHeader _header;
    private PackFooter _footer;

    // Write buffers.
    private NativeBuffer[] _buffers;
    private int _active;
    private int _fill;
    private long _bufferFileOffset;
    private Task _inFlight;
    private uint _fileCrc = Crc32CUtil.Start;

    // Pages.
    private ulong[] _seen;
    private int _pagesAdded;
    private readonly byte[] _metaPages = new byte[2 * PackFormat.PageSize];
    private readonly List<uint> _suspects = [];
    private PackIndexRuns _index;

    // Tail.
    private NativeBuffer _tailBlock;
    private int _tailFill;
    private bool _segmentOpen;
    private TailSegmentEntry _segment;
    private uint _declaredSize;
    private readonly byte[] _segmentHeader = new byte[PackFormat.WalSegmentHeaderSize];
    private readonly List<TailSegmentEntry> _segments = [];
    private readonly List<TailBlockEntry> _blocks = [];

    public PackWriter(IStoreFileSystem fs, IStoreFile tempFile, in PackIdentity identity, IPackPublisher publisher, PackWriterOptions options = null,
        ILogger log = null)
    {
        _fs = fs;
        _file = tempFile;
        _identity = identity;
        _publisher = publisher;
        _options = options ?? PackWriterOptions.Default;
        _log = log ?? NullLogger.Instance;
        TempPath = tempFile.Path;
    }

    /// <inheritdoc />
    public uint BackupId => _identity.BackupId;

    /// <inheritdoc />
    public Guid ChainId => _identity.ChainId;

    /// <summary>The temp file's path.</summary>
    public string TempPath { get; }

    /// <summary>The header, valid once <see cref="SetImage"/> ran.</summary>
    public PackHeader Header => _header;

    /// <summary>The footer, valid once the pack is sealed.</summary>
    public PackFooter Footer => _footer;

    /// <summary>The sealed file's length.</summary>
    public long FileLength { get; private set; }

    /// <summary>Neither completed nor aborted yet.</summary>
    public bool IsOpen => _state is State.Created or State.Imaging or State.Failed;

    /// <summary>Where the pack was published, once <see cref="Complete"/> returned.</summary>
    public string PublishedPath { get; private set; }

    /// <inheritdoc />
    public void SetImage(int imagePages, long checkpointLsn, long freezeUtcTicks)
    {
        Require(State.Created, nameof(SetImage));
        if (imagePages < 2)
        {
            Fail($"an image of {imagePages} pages has no meta pair");
        }

        _header = new PackHeader
        {
            FormatVersion = PackFormat.FormatVersion,
            Kind = _identity.Kind,
            Flags = _identity.Offline ? PackFormat.HeaderFlagOffline : (byte)0,
            PageSize = PackFormat.PageSize,
            DatabaseId = _identity.DatabaseId,
            StoreId = _identity.StoreId,
            ChainId = _identity.ChainId,
            BackupId = _identity.BackupId,
            ParentId = _identity.ParentId,
            BaseId = _identity.BaseId,
            ImagePages = imagePages,
            CheckpointLsn = checkpointLsn,
            FreezeUtcTicks = freezeUtcTicks,
            TimelineId = _identity.TimelineId,
            DatabaseFormatRevision = _identity.DatabaseFormatRevision,
            Compression = PackFormat.CompressionLz4,
            EngineVersion = _identity.EngineVersion,
        };

        // A frame never straddles two buffers, so one buffer must hold the largest: a tail block LZ4 could not shrink.
        if (_options.BufferSize < PackFormat.FramePrefixSize + LZ4Codec.MaximumOutputSize(PackFormat.TailBlockSize))
        {
            throw new InvalidOperationException($"PackWriterOptions.BufferSize {_options.BufferSize} is smaller than one tail block frame.");
        }

        // Two buffers, or none: a second allocation that fails must not leave the first one with no owner to free it.
        var first = NativeBuffer.Allocate(_options.BufferSize, _options.Owner, $"Backup.Pack{_identity.BackupId}.Buffer0");
        try
        {
            _buffers = [first, NativeBuffer.Allocate(_options.BufferSize, _options.Owner, $"Backup.Pack{_identity.BackupId}.Buffer1")];
        }
        catch
        {
            ((IDisposable)first).Dispose();
            throw;
        }
        _seen = new ulong[(imagePages + 63) >> 6];
        _index = new PackIndexRuns(_fs, TempPath + PackFormat.SidecarInfix, _options.IndexHeapEntries, _options.Owner,
            $"Backup.Pack{_identity.BackupId}.Index");
        _header.WriteTo(Reserve(PackFormat.HeaderSize));
        _fill += PackFormat.HeaderSize;
        if (_options.DataGapBytes > 0)
        {
            SkipGap(_options.DataGapBytes);
        }

        _state = State.Imaging;
    }

    /// <inheritdoc />
    public void AddPage(int pageId, ReadOnlySpan<byte> image, bool suspect)
    {
        Require(State.Imaging, nameof(AddPage));
        if (image.Length != PackFormat.PageSize)
        {
            Fail($"page {pageId} image is {image.Length} bytes, not {PackFormat.PageSize}");
        }

        if ((uint)pageId >= (uint)_header.ImagePages)
        {
            Fail($"page {pageId} is outside the image's {_header.ImagePages} pages");
        }

        ref var word = ref _seen[pageId >> 6];
        var bit = 1UL << (pageId & 63);
        if ((word & bit) != 0)
        {
            Fail($"page {pageId} was added twice");
        }

        word |= bit;
        _pagesAdded++;
        if (pageId < 2)
        {
            image.CopyTo(_metaPages.AsSpan(pageId * PackFormat.PageSize));
        }

        if (suspect)
        {
            _suspects.Add((uint)pageId);
        }

        var crc = Crc32CUtil.Compute(image);
        if (image.IndexOfAnyExcept((byte)0) < 0)
        {
            _index.Add(new PackPageEntry((uint)pageId, 0, 0, crc));
            return;
        }

        var frame = Reserve(PackFormat.FramePrefixSize + PackFormat.MaxCompressedPage);
        var payload = frame[PackFormat.FramePrefixSize..];
        var stored = LZ4Codec.Encode(image, payload);
        if (stored <= 0 || stored >= PackFormat.PageSize)
        {
            image.CopyTo(payload);
            stored = PackFormat.PageSize;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)stored);
        var payloadOffset = Position + PackFormat.FramePrefixSize;
        _fill += PackFormat.FramePrefixSize + stored;
        _index.Add(new PackPageEntry((uint)pageId, (uint)stored, payloadOffset, crc));
    }

    /// <inheritdoc />
    public void BeginTailSegment(long segmentId, long firstLsn, uint declaredSize)
    {
        Require(State.Imaging, nameof(BeginTailSegment));
        if (_segmentOpen)
        {
            Fail($"tail segment {segmentId} begins inside segment {_segment.SegmentId}");
        }

        if (_segments.Count > 0 && segmentId <= _segments[^1].SegmentId)
        {
            Fail($"tail segment {segmentId} does not follow segment {_segments[^1].SegmentId}");
        }

        // TailSegmentEntry.RawLength is an int: a declared size past int.MaxValue could never be copied whole, so it is refused here, explicitly.
        if (declaredSize > int.MaxValue)
        {
            Fail($"tail segment {segmentId} declares {declaredSize} bytes, more than a segment can hold ({int.MaxValue})");
        }

        if (declaredSize < PackFormat.WalSegmentHeaderSize || declaredSize % PackFormat.WalSegmentHeaderSize != 0)
        {
            Fail($"tail segment {segmentId} declares {declaredSize} bytes, not a positive multiple of {PackFormat.WalSegmentHeaderSize}");
        }

        _tailBlock ??= NativeBuffer.Allocate(PackFormat.TailBlockSize, _options.Owner, $"Backup.Pack{_identity.BackupId}.TailBlock");
        _segment = new TailSegmentEntry { SegmentId = segmentId, FirstLsn = firstLsn, RawCrc = Crc32CUtil.Start, FirstBlock = _blocks.Count };
        _declaredSize = declaredSize;
        _tailFill = 0;
        _segmentOpen = true;
    }

    /// <inheritdoc />
    public void AppendTail(ReadOnlySpan<byte> bytes)
    {
        Require(State.Imaging, nameof(AppendTail));
        if (!_segmentOpen)
        {
            Fail("tail bytes outside a segment");
        }

        if ((long)_segment.RawLength + bytes.Length > _declaredSize)
        {
            Fail($"tail segment {_segment.SegmentId} copies more than its declared {_declaredSize} bytes");
        }

        if (_segment.RawLength < PackFormat.WalSegmentHeaderSize)
        {
            var n = Math.Min(bytes.Length, PackFormat.WalSegmentHeaderSize - _segment.RawLength);
            bytes[..n].CopyTo(_segmentHeader.AsSpan(_segment.RawLength));
        }

        _segment.RawCrc = Crc32CUtil.Append(_segment.RawCrc, bytes);
        _segment.RawLength += bytes.Length;
        while (bytes.Length > 0)
        {
            var n = Math.Min(bytes.Length, PackFormat.TailBlockSize - _tailFill);
            bytes[..n].CopyTo(_tailBlock.Span[_tailFill..]);
            _tailFill += n;
            bytes = bytes[n..];
            if (_tailFill == PackFormat.TailBlockSize)
            {
                EmitTailBlock();
            }
        }
    }

    /// <inheritdoc />
    public void EndTailSegment()
    {
        Require(State.Imaging, nameof(EndTailSegment));
        if (!_segmentOpen)
        {
            Fail("a tail segment ends that never began");
        }

        if (_tailFill > 0)
        {
            EmitTailBlock();
        }

        var raw = _segment.RawLength;
        if (raw < PackFormat.WalSegmentHeaderSize || raw % PackFormat.WalSegmentHeaderSize != 0)
        {
            Fail($"tail segment {_segment.SegmentId} copied {raw} bytes, not a positive multiple of {PackFormat.WalSegmentHeaderSize}");
        }

        var header = MemoryMarshal.Read<WalSegmentHeader>(_segmentHeader);
        if (!header.Validate() || header.SegmentId != _segment.SegmentId || header.FirstLSN != _segment.FirstLsn || header.SegmentSize != _declaredSize)
        {
            Fail($"tail segment {_segment.SegmentId}: its header (valid={header.Validate()}, id {header.SegmentId}, first LSN {header.FirstLSN}, "
                 + $"size {header.SegmentSize}) does not match what capture declared (first LSN {_segment.FirstLsn}, size {_declaredSize})");
        }

        _segment.RawCrc = Crc32CUtil.Finish(_segment.RawCrc);
        _segment.BlockCount = _blocks.Count - _segment.FirstBlock;
        _segments.Add(_segment);
        _segmentOpen = false;
    }

    /// <inheritdoc />
    public BackupPointInfo Complete(long pointLsn, long tailEndUtcTicks)
    {
        Require(State.Imaging, nameof(Complete));
        if (_segmentOpen)
        {
            Fail($"tail segment {_segment.SegmentId} was never ended");
        }

        CheckImage(pointLsn);

        _footer.DataRegionEnd = Position;
        _footer.TailDirOffset = Position;
        WriteTailDirectory();
        _footer.SuspectOffset = Position;
        WriteSuspects();
        _footer.IndexOffset = Position;
        WriteIndex();
        _footer.PageCount = (int)_index.Count;
        _footer.TailSegmentCount = _segments.Count;
        _footer.PointLsn = pointLsn;
        _footer.TailEndUtcTicks = tailEndUtcTicks;
        _footer.SuspectCount = _suspects.Count;
        _footer.Flags = _suspects.Count > 0 ? PackFormat.FooterFlagSuspect : (byte)0;
        WriteFooterAndSeal();

        PublishedPath = _publisher.Publish(this);
        _state = State.Completed;
        ReleaseResources();
        BackupLog.PackPublished(_log, BackupId, _identity.Kind, _footer.PageCount, FileLength, _clock.ElapsedMilliseconds);
        return new BackupPointInfo(BackupId, ChainId, _identity.Kind, pointLsn, _header.CheckpointLsn, _header.ImagePages, _footer.PageCount,
            _footer.SuspectCount, FileLength, PublishedPath);
    }

    /// <inheritdoc />
    public void Abort()
    {
        if (_state is State.Completed or State.Aborted)
        {
            return;
        }

        _state = State.Aborted;
        try
        {
            _inFlight?.Wait();
        }
        catch (Exception)
        {
            // The pack is being abandoned; the write's failure is moot.
        }

        _inFlight = null;
        try
        {
            _file?.Dispose();
        }
        catch (Exception)
        {
        }

        _file = null;
        TryDelete(TempPath);
        ReleaseResources();
        BackupLog.PackAborted(_log, BackupId, "aborted by capture");
    }

    /// <summary>Aborts unless completed.</summary>
    public void Dispose() => Abort();

    private void CheckImage(long pointLsn)
    {
        if (_identity.Kind is BackupKind.Full or BackupKind.Compacted && _pagesAdded != _header.ImagePages)
        {
            Fail($"a {_identity.Kind} point holds {_pagesAdded} of its {_header.ImagePages} pages");
        }

        if ((_seen[0] & 3UL) != 3UL)
        {
            Fail("the meta pair (pages 0 and 1) is missing");
        }

        var meta = BackupImageMeta.Read(_metaPages.AsSpan(0, PackFormat.PageSize), _metaPages.AsSpan(PackFormat.PageSize));
        if (!meta.Usable)
        {
            Fail($"the image's meta pair is unreadable: {meta.Problem}");
        }

        if (meta.CheckpointLsn != _header.CheckpointLsn)
        {
            Fail($"the image's meta pair records CheckpointLSN {meta.CheckpointLsn}, the header {_header.CheckpointLsn}");
        }

        if (meta.DatabaseId != _header.DatabaseId)
        {
            Fail($"the image belongs to database {meta.DatabaseId}, the point to {_header.DatabaseId}");
        }

        if (meta.FormatRevision != _header.DatabaseFormatRevision)
        {
            Fail($"the image is at format revision {meta.FormatRevision}, the header says {_header.DatabaseFormatRevision}");
        }

        if (!meta.CleanShutdown && _segments.Count == 0)
        {
            // DC-3: an unclean image opens through recovery only when a *.wal exists; with none, the open skips replay, the rebuild and the net.
            Fail("an unclean image needs at least one WAL segment in its tail");
        }

        if (pointLsn < _header.CheckpointLsn)
        {
            Fail($"PointLSN {pointLsn} is below CheckpointLSN {_header.CheckpointLsn}");
        }
    }

    private void WriteTailDirectory()
    {
        Span<byte> entry = stackalloc byte[PackFormat.TailSegmentEntrySize];
        var crc = Crc32CUtil.Start;
        BinaryPrimitives.WriteInt32LittleEndian(entry, _segments.Count);
        BinaryPrimitives.WriteInt32LittleEndian(entry[4..], _blocks.Count);
        crc = Append(entry[..PackFormat.SectionPrefixSize], crc);
        foreach (var s in _segments)
        {
            s.WriteTo(entry);
            crc = Append(entry, crc);
        }

        foreach (var b in _blocks)
        {
            b.WriteTo(entry);
            crc = Append(entry[..PackFormat.TailBlockEntrySize], crc);
        }

        _footer.TailDirCrc = Crc32CUtil.Finish(crc);
    }

    private void WriteSuspects()
    {
        _suspects.Sort();
        Span<byte> word = stackalloc byte[PackFormat.SectionPrefixSize];
        BinaryPrimitives.WriteInt32LittleEndian(word, _suspects.Count);
        BinaryPrimitives.WriteInt32LittleEndian(word[4..], 0);
        var crc = Append(word, Crc32CUtil.Start);
        foreach (var id in _suspects)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, id);
            crc = Append(word[..4], crc);
        }

        _footer.SuspectCrc = Crc32CUtil.Finish(crc);
    }

    private void WriteIndex()
    {
        var crc = Crc32CUtil.Start;
        _index.Merge(batch =>
        {
            var bytes = MemoryMarshal.AsBytes(batch);
            while (bytes.Length > 0)
            {
                // Whole entries per chunk, so a buffer boundary never splits one.
                var room = (_buffers[_active].Length - _fill) / PackFormat.PageEntrySize * PackFormat.PageEntrySize;
                if (room == 0)
                {
                    IssueActive();
                    continue;
                }

                var n = Math.Min(room, bytes.Length);
                crc = Append(bytes[..n], crc);
                bytes = bytes[n..];
            }
        });
        _footer.IndexCrc = Crc32CUtil.Finish(crc);
    }

    private void WriteFooterAndSeal()
    {
        var footer = Reserve(PackFormat.FooterSize);
        _footer.WriteTo(footer);
        // FileCRC covers [0, EOF-4): every buffer already issued, then this one up to the footer's last four bytes.
        _fill += PackFormat.FooterSize - 4;
        var fileCrc = Crc32CUtil.Finish(Crc32CUtil.Append(_fileCrc, _buffers[_active].Span[.._fill]));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffers[_active].Span[_fill..], fileCrc);
        _fill += 4;
        IssueActive(appendToFileCrc: false);
        _inFlight?.Wait();
        _inFlight = null;
        FileLength = _bufferFileOffset;
        _file.Flush();
        _file.Dispose();
        _file = null;
        _index.Dispose();
    }

    /// <summary>Copies <paramref name="bytes"/> into the write stream and returns the section CRC advanced over them.</summary>
    private uint Append(ReadOnlySpan<byte> bytes, uint crc)
    {
        var dst = Reserve(bytes.Length);
        bytes.CopyTo(dst);
        _fill += bytes.Length;
        return Crc32CUtil.Append(crc, bytes);
    }

    /// <summary>The free space of the active buffer, issuing it first when it has less than <paramref name="bytes"/> left.</summary>
    private Span<byte> Reserve(int bytes)
    {
        if (_fill + bytes > _buffers[_active].Length)
        {
            IssueActive();
        }

        return _buffers[_active].Span[_fill..];
    }

    private long Position => _bufferFileOffset + _fill;

    private void IssueActive(bool appendToFileCrc = true)
    {
        if (_fill == 0)
        {
            return;
        }

        var buffer = _buffers[_active];
        if (appendToFileCrc)
        {
            _fileCrc = Crc32CUtil.Append(_fileCrc, buffer.Span[.._fill]);
        }

        // The other buffer's write must be done before it is filled again, and its failure surfaces here, on the producer.
        _inFlight?.GetAwaiter().GetResult();
        _inFlight = _file.WriteAsync(buffer.Memory[.._fill], _bufferFileOffset);
        _bufferFileOffset += _fill;
        _fill = 0;
        _active ^= 1;
    }

    private void SkipGap(long bytes)
    {
        IssueActive();
        _fileCrc = Crc32CUtil.AppendZeros(_fileCrc, bytes);
        _bufferFileOffset += bytes;
    }

    private void EmitTailBlock()
    {
        var raw = _tailBlock.Span[.._tailFill];
        var frame = Reserve(PackFormat.FramePrefixSize + TailBlockCodec.MaxStoredLength(raw.Length));
        var stored = TailBlockCodec.Encode(raw, frame[PackFormat.FramePrefixSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)stored);
        _blocks.Add(new TailBlockEntry { DataOffset = Position + PackFormat.FramePrefixSize, RawLength = raw.Length, StoredLength = stored });
        _fill += PackFormat.FramePrefixSize + stored;
        _tailFill = 0;
    }

    private void Require(State state, string call)
    {
        if (_state != state)
        {
            throw new InvalidOperationException($"Backup point #{BackupId}: {call} called in state {_state}.");
        }
    }

    private void Fail(string detail)
    {
        _state = State.Failed;
        throw new InvalidOperationException($"Backup point #{BackupId}: {detail}.");
    }

    private void TryDelete(string path)
    {
        try
        {
            _fs.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupLog.TempNotDeleted(_log, path, ex.Message);
        }
    }

    private void ReleaseResources()
    {
        _index?.Dispose();
        _index = null;
        if (_buffers != null)
        {
            foreach (var b in _buffers)
            {
                ((IDisposable)b).Dispose();
            }

            _buffers = null;
        }

        if (_tailBlock != null)
        {
            ((IDisposable)_tailBlock).Dispose();
            _tailBlock = null;
        }
    }
}
