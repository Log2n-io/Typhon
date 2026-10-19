using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>A pack's TAILDIR, checked: segments in ascending id, blocks contiguous per segment and inside the data region.</summary>
internal sealed class TailDirectory
{
    public TailSegmentEntry[] Segments { get; init; }

    public TailBlockEntry[] Blocks { get; init; }
}

/// <summary>
/// Reads a published <c>.tpack</c> (plan 1413 §2.4): header and footer at <see cref="Open"/>, sections on demand, every byte checked against the
/// CRC that covers it. Opened <c>Read | Delete</c> (DC-14), never with write access (BKP-08).
/// </summary>
/// <remarks>Not thread-safe: one reader per thread.</remarks>
internal sealed class PackReader : IDisposable
{
    private readonly IStoreFile _file;
    private byte[] _frame;

    private PackReader(IStoreFile file, string path, long length, PackHeader header, PackFooter footer)
    {
        _file = file;
        Path = path;
        FileLength = length;
        Header = header;
        Footer = footer;
    }

    public string Path { get; }

    public long FileLength { get; }

    public PackHeader Header { get; }

    public PackFooter Footer { get; }

    /// <summary>Bytes this reader has read from the file.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// Opens a pack: magics, <c>HeaderCRC</c>, version, compression, <c>FooterCRC</c> and the structural invariants. Throws
    /// <see cref="BackupPackCorruptException"/> or <see cref="BackupFormatUnsupportedException"/>.
    /// </summary>
    public static PackReader Open(IStoreFileSystem fs, string path)
    {
        var file = fs.OpenRead(path);
        try
        {
            var length = file.Length;
            if (length < PackFormat.HeaderSize + PackFormat.FooterSize)
            {
                PackFormat.ThrowCorrupt(path, "File", -1, $"{length} bytes is shorter than a header and a footer");
            }

            Span<byte> buffer = stackalloc byte[PackFormat.HeaderSize];
            if (!file.ReadExactly(buffer, 0))
            {
                PackFormat.ThrowCorrupt(path, "Header", -1, "unreadable");
            }

            var header = CheckHeader(path, buffer);
            if (!file.ReadExactly(buffer[..PackFormat.FooterSize], length - PackFormat.FooterSize))
            {
                PackFormat.ThrowCorrupt(path, "Footer", -1, "unreadable");
            }

            var footer = CheckFooter(path, buffer[..PackFormat.FooterSize], length, header);
            var reader = new PackReader(file, path, length, header, footer) { BytesRead = PackFormat.HeaderSize + PackFormat.FooterSize };
            file = null;
            return reader;
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Checks a header's magic, CRC, version and compression, and parses it.</summary>
    public static PackHeader CheckHeader(string path, ReadOnlySpan<byte> bytes)
    {
        if (!bytes[..8].SequenceEqual(PackFormat.Magic))
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, "not a Typhon pack (magic)");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[PackFormat.HHeaderCrc..]) != Crc32CUtil.Compute(bytes[..PackFormat.HHeaderCrc]))
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, "HeaderCRC mismatch");
        }

        var header = PackHeader.Read(bytes);
        if (header.FormatVersion > PackFormat.FormatVersion)
        {
            throw new BackupFormatUnsupportedException(path, "FormatVersion", header.FormatVersion, PackFormat.FormatVersion);
        }

        if (header.FormatVersion == 0)
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, "FormatVersion 0");
        }

        if (header.Compression != PackFormat.CompressionLz4)
        {
            throw new BackupFormatUnsupportedException(path, "Compression", header.Compression, PackFormat.CompressionLz4);
        }

        if (header.PageSize != PackFormat.PageSize)
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, $"page size {header.PageSize}");
        }

        if (header.Kind is not (BackupKind.Full or BackupKind.Incremental or BackupKind.Compacted))
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, $"kind {(byte)header.Kind}");
        }

        var isBase = header.Kind != BackupKind.Incremental;
        if (header.ImagePages < 2 || header.BackupId == 0 || (isBase ? header.ParentId != 0 || header.BaseId != header.BackupId
                : header.ParentId == 0 || header.ParentId >= header.BackupId || header.BaseId > header.ParentId))
        {
            PackFormat.ThrowCorrupt(path, "Header", -1, "inconsistent identity (BackupId/ParentId/BaseId/ImagePages)");
        }

        return header;
    }

    private static PackFooter CheckFooter(string path, ReadOnlySpan<byte> bytes, long length, in PackHeader header)
    {
        if (!bytes.Slice(PackFormat.FFooterMagic, 4).SequenceEqual(PackFormat.FooterMagic))
        {
            PackFormat.ThrowCorrupt(path, "Footer", -1, "footer magic missing: the pack is truncated or was never sealed");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[PackFormat.FFooterCrc..]) != Crc32CUtil.Compute(bytes[..PackFormat.FFooterCrc]))
        {
            PackFormat.ThrowCorrupt(path, "Footer", -1, "FooterCRC mismatch");
        }

        var f = PackFooter.Read(bytes);
        var ok = f.DataRegionEnd == f.TailDirOffset
                 && f.DataRegionEnd >= PackFormat.HeaderSize
                 && f.TailDirOffset <= f.SuspectOffset
                 && f.SuspectOffset <= f.IndexOffset
                 && f.PageCount >= 0 && f.PageCount <= header.ImagePages
                 && f.SuspectCount >= 0 && f.SuspectCount <= f.PageCount
                 && f.TailSegmentCount >= 0
                 && f.IndexOffset + ((long)PackFormat.PageEntrySize * f.PageCount) == length - PackFormat.FooterSize
                 && f.SuspectOffset + PackFormat.SectionPrefixSize + (4L * f.SuspectCount) == f.IndexOffset
                 && f.SuspectOffset - f.TailDirOffset >= PackFormat.SectionPrefixSize
                 && (f.SuspectCount > 0) == f.Suspect;
        if (!ok)
        {
            PackFormat.ThrowCorrupt(path, "Footer", -1, "section offsets or counts are inconsistent");
        }

        return f;
    }

    /// <summary>A sequential reader over the INDEX that checks every entry and, at its end, <c>IndexCRC</c>.</summary>
    public PackIndexStream OpenIndexStream(int bufferBytes = 1 << 20) => new(this, bufferBytes);

    /// <summary>The whole INDEX, checked. For small packs and tests; large ones stream it.</summary>
    public PackPageEntry[] LoadIndex()
    {
        var entries = new PackPageEntry[Footer.PageCount];
        using var stream = OpenIndexStream();
        var i = 0;
        while (stream.TryNext(out var e))
        {
            entries[i++] = e;
        }

        return entries;
    }

    /// <summary>The TAILDIR, checked against <c>TailDirCRC</c> and its structural rules.</summary>
    public TailDirectory LoadTailDirectory()
    {
        var length = Footer.SuspectOffset - Footer.TailDirOffset;
        if (length > int.MaxValue)
        {
            PackFormat.ThrowCorrupt(Path, "TailDir", -1, "too large");
        }

        var bytes = ReadSection((int)length, Footer.TailDirOffset, "TailDir");
        if (Crc32CUtil.Compute(bytes) != Footer.TailDirCrc)
        {
            PackFormat.ThrowCorrupt(Path, "TailDir", -1, "TailDirCRC mismatch");
        }

        var segmentCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (segmentCount != Footer.TailSegmentCount || segmentCount < 0 || blockCount < 0
            || PackFormat.SectionPrefixSize + ((long)segmentCount * PackFormat.TailSegmentEntrySize) + ((long)blockCount * PackFormat.TailBlockEntrySize)
            != length)
        {
            PackFormat.ThrowCorrupt(Path, "TailDir", -1, "counts do not match the section");
        }

        var segments = new TailSegmentEntry[segmentCount];
        var blocks = new TailBlockEntry[blockCount];
        var pos = PackFormat.SectionPrefixSize;
        for (var i = 0; i < segmentCount; i++, pos += PackFormat.TailSegmentEntrySize)
        {
            segments[i] = TailSegmentEntry.Read(bytes.AsSpan(pos));
        }

        for (var i = 0; i < blockCount; i++, pos += PackFormat.TailBlockEntrySize)
        {
            blocks[i] = TailBlockEntry.Read(bytes.AsSpan(pos));
        }

        var nextBlock = 0;
        for (var i = 0; i < segmentCount; i++)
        {
            var s = segments[i];
            var ok = s.FirstBlock == nextBlock && s.BlockCount >= 1 && (long)s.FirstBlock + s.BlockCount <= blockCount
                     && s.RawLength >= PackFormat.WalSegmentHeaderSize && s.RawLength % PackFormat.WalSegmentHeaderSize == 0
                     && (i == 0 || s.SegmentId > segments[i - 1].SegmentId);
            if (!ok)
            {
                PackFormat.ThrowCorrupt(Path, "TailDir", -1, $"segment entry {i} is inconsistent");
            }

            long raw = 0;
            for (var b = s.FirstBlock; b < s.FirstBlock + s.BlockCount; b++)
            {
                var blk = blocks[b];
                var last = b == s.FirstBlock + s.BlockCount - 1;
                var blockOk = blk.RawLength > 0 && blk.RawLength <= PackFormat.TailBlockSize && (last || blk.RawLength == PackFormat.TailBlockSize)
                              && blk.StoredLength > 0 && blk.StoredLength <= blk.RawLength
                              && blk.DataOffset - PackFormat.FramePrefixSize >= PackFormat.HeaderSize
                              && blk.DataOffset + blk.StoredLength <= Footer.DataRegionEnd;
                if (!blockOk)
                {
                    PackFormat.ThrowCorrupt(Path, "TailDir", -1, $"block entry {b} is inconsistent");
                }

                raw += blk.RawLength;
            }

            if (raw != s.RawLength)
            {
                PackFormat.ThrowCorrupt(Path, "TailDir", -1, $"segment {s.SegmentId}'s blocks hold {raw} bytes, not {s.RawLength}");
            }

            nextBlock = s.FirstBlock + s.BlockCount;
        }

        if (nextBlock != blockCount)
        {
            PackFormat.ThrowCorrupt(Path, "TailDir", -1, "blocks belong to no segment");
        }

        return new TailDirectory { Segments = segments, Blocks = blocks };
    }

    /// <summary>The SUSPECT section, checked: <c>SuspectCRC</c>, ascending ids inside the image.</summary>
    public uint[] LoadSuspects()
    {
        var bytes = ReadSection(PackFormat.SectionPrefixSize + (4 * Footer.SuspectCount), Footer.SuspectOffset, "Suspect");
        if (Crc32CUtil.Compute(bytes) != Footer.SuspectCrc)
        {
            PackFormat.ThrowCorrupt(Path, "Suspect", -1, "SuspectCRC mismatch");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != Footer.SuspectCount)
        {
            PackFormat.ThrowCorrupt(Path, "Suspect", -1, "count does not match the footer");
        }

        var ids = new uint[Footer.SuspectCount];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PackFormat.SectionPrefixSize + (4 * i)));
            if (ids[i] >= (uint)Header.ImagePages || (i > 0 && ids[i] <= ids[i - 1]))
            {
                PackFormat.ThrowCorrupt(Path, "Suspect", -1, "ids out of order or outside the image");
            }
        }

        return ids;
    }

    /// <summary>
    /// Reads, decodes and checks one page: frame prefix, LZ4 decode to exactly 8 KiB, <c>PageCrc</c>. Throws
    /// <see cref="BackupPackCorruptException"/>.
    /// </summary>
    public void ReadPage(in PackPageEntry entry, Span<byte> destination)
    {
        if (!TryReadPage(entry, destination, out var error))
        {
            PackFormat.ThrowCorrupt(Path, "Page", entry.PageId, error);
        }
    }

    /// <summary>As <see cref="ReadPage"/>, reporting damage instead of throwing; <paramref name="destination"/> holds the decoded bytes or zeros.</summary>
    public bool TryReadPage(in PackPageEntry entry, Span<byte> destination, out string error)
    {
        destination = destination[..PackFormat.PageSize];
        if (entry.StoredLength == 0)
        {
            destination.Clear();
            return CheckCrc(entry, destination, out error);
        }

        var frameLength = PackFormat.FramePrefixSize + (int)entry.StoredLength;
        _frame ??= new byte[PackFormat.FramePrefixSize + PackFormat.PageSize];
        var frame = _frame.AsSpan(0, frameLength);
        if (!_file.ReadExactly(frame, entry.DataOffset - PackFormat.FramePrefixSize))
        {
            destination.Clear();
            error = "frame runs past the end of the file";
            return false;
        }

        BytesRead += frameLength;
        return DecodePage(entry, frame, destination, out error);
    }

    /// <summary>Decodes a page frame already in memory (prefix included) and checks it.</summary>
    public static bool DecodePage(in PackPageEntry entry, ReadOnlySpan<byte> frame, Span<byte> destination, out string error)
    {
        destination = destination[..PackFormat.PageSize];
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != entry.StoredLength)
        {
            destination.Clear();
            error = "frame length prefix disagrees with the index";
            return false;
        }

        var payload = frame.Slice(PackFormat.FramePrefixSize, (int)entry.StoredLength);
        if (entry.StoredLength == PackFormat.PageSize)
        {
            payload.CopyTo(destination);
        }
        else if (LZ4Codec.Decode(payload, destination) != PackFormat.PageSize)
        {
            destination.Clear();
            error = "LZ4 frame does not decode to one page";
            return false;
        }

        return CheckCrc(entry, destination, out error);
    }

    private static bool CheckCrc(in PackPageEntry entry, ReadOnlySpan<byte> page, out string error)
    {
        if (Crc32CUtil.Compute(page) != entry.PageCrc)
        {
            error = "PageCrc mismatch";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Decodes tail segment <paramref name="index"/> block by block, checks every frame, <c>RawCrc</c> and the WAL segment header against the
    /// TAILDIR, and writes the raw bytes to <paramref name="target"/> at offset 0 when given. Returns the segment's declared size (its header's
    /// <c>SegmentSize</c>). Throws <see cref="BackupPackCorruptException"/>.
    /// </summary>
    public uint ReadTailSegment(TailDirectory directory, int index, IStoreFile target)
    {
        var segment = directory.Segments[index];
        var stored = ArrayPool<byte>.Shared.Rent(PackFormat.FramePrefixSize + PackFormat.TailBlockSize);
        var raw = ArrayPool<byte>.Shared.Rent(PackFormat.TailBlockSize);
        try
        {
            var crc = Crc32CUtil.Start;
            long offset = 0;
            uint declared = 0;
            for (var b = segment.FirstBlock; b < segment.FirstBlock + segment.BlockCount; b++)
            {
                var block = directory.Blocks[b];
                var frame = stored.AsSpan(0, PackFormat.FramePrefixSize + block.StoredLength);
                if (!_file.ReadExactly(frame, block.DataOffset - PackFormat.FramePrefixSize))
                {
                    PackFormat.ThrowCorrupt(Path, "Tail", -1, $"segment {segment.SegmentId}: block {b} runs past the end of the file");
                }

                BytesRead += frame.Length;
                if (BinaryPrimitives.ReadInt32LittleEndian(frame) != block.StoredLength)
                {
                    PackFormat.ThrowCorrupt(Path, "Tail", -1, $"segment {segment.SegmentId}: block {b} length prefix disagrees with the TAILDIR");
                }

                var payload = frame[PackFormat.FramePrefixSize..];
                var bytes = raw.AsSpan(0, block.RawLength);
                if (block.StoredLength == block.RawLength)
                {
                    payload.CopyTo(bytes);
                }
                else if (LZ4Codec.Decode(payload, bytes) != block.RawLength)
                {
                    PackFormat.ThrowCorrupt(Path, "Tail", -1, $"segment {segment.SegmentId}: block {b} does not decode");
                }

                if (b == segment.FirstBlock)
                {
                    declared = CheckSegmentHeader(segment, bytes);
                }

                crc = Crc32CUtil.Append(crc, bytes);
                target?.Write(bytes, offset);
                offset += bytes.Length;
            }

            if (Crc32CUtil.Finish(crc) != segment.RawCrc)
            {
                PackFormat.ThrowCorrupt(Path, "Tail", -1, $"segment {segment.SegmentId}: RawCrc mismatch");
            }

            return declared;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(stored);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private uint CheckSegmentHeader(in TailSegmentEntry segment, ReadOnlySpan<byte> firstBlock)
    {
        var header = MemoryMarshal.Read<WalSegmentHeader>(firstBlock);
        if (!header.Validate() || header.SegmentId != segment.SegmentId || header.FirstLSN != segment.FirstLsn
            || header.SegmentSize < segment.RawLength || header.SegmentSize % PackFormat.WalSegmentHeaderSize != 0)
        {
            PackFormat.ThrowCorrupt(Path, "Tail", -1, $"segment {segment.SegmentId}: its WAL header disagrees with the TAILDIR");
        }

        return header.SegmentSize;
    }

    /// <summary>Streams the whole file once and compares <c>FileCRC</c>.</summary>
    public bool VerifyFileCrc()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 << 20);
        try
        {
            var crc = Crc32CUtil.Start;
            var end = FileLength - 4;
            long pos = 0;
            while (pos < end)
            {
                var n = (int)Math.Min(buffer.Length, end - pos);
                if (!_file.ReadExactly(buffer.AsSpan(0, n), pos))
                {
                    return false;
                }

                crc = Crc32CUtil.Append(crc, buffer.AsSpan(0, n));
                pos += n;
            }

            Span<byte> stored = stackalloc byte[4];
            if (!_file.ReadExactly(stored, end))
            {
                return false;
            }

            BytesRead += FileLength;
            return Crc32CUtil.Finish(crc) == BinaryPrimitives.ReadUInt32LittleEndian(stored);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A positional read of the file, for the materialiser's read windows.</summary>
    public bool ReadAt(Span<byte> destination, long offset)
    {
        BytesRead += destination.Length;
        return _file.ReadExactly(destination, offset);
    }

    private byte[] ReadSection(int length, long offset, string section)
    {
        var bytes = new byte[length];
        if (!_file.ReadExactly(bytes, offset))
        {
            PackFormat.ThrowCorrupt(Path, section, -1, "runs past the end of the file");
        }

        BytesRead += length;
        return bytes;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Reads a pack's INDEX front to back through a bounded buffer, checking each entry as it goes (ascending unique <c>PageId</c> inside the image,
/// frame inside the data region, zero pages well-formed) and <c>IndexCRC</c> once the last entry is read. A consumer must treat the end of the
/// stream as part of success: an entry is not proven until <see cref="TryNext"/> has returned <c>false</c>.
/// </summary>
internal sealed class PackIndexStream : IDisposable
{
    private readonly PackReader _pack;
    private readonly PackPageEntry[] _buffer;
    private long _read;
    private int _pos;
    private int _filled;
    private long _last = -1;
    private uint _crc = Crc32CUtil.Start;

    public PackIndexStream(PackReader pack, int bufferBytes)
    {
        _pack = pack;
        _buffer = new PackPageEntry[Math.Max(1, Math.Min(bufferBytes / PackFormat.PageEntrySize, pack.Footer.PageCount))];
    }

    /// <summary>Entries returned so far.</summary>
    public long Position => _read - _filled + _pos;

    public bool TryNext(out PackPageEntry entry)
    {
        if (_pos == _filled)
        {
            var remaining = _pack.Footer.PageCount - _read;
            if (remaining == 0)
            {
                if (_filled >= 0 && Crc32CUtil.Finish(_crc) != _pack.Footer.IndexCrc)
                {
                    _filled = -1;
                    PackFormat.ThrowCorrupt(_pack.Path, "Index", -1, "IndexCRC mismatch");
                }

                _filled = -1;
                _pos = -1;
                entry = default;
                return false;
            }

            var n = (int)Math.Min(_buffer.Length, remaining);
            var bytes = MemoryMarshal.AsBytes(_buffer.AsSpan(0, n));
            if (!_pack.ReadAt(bytes, _pack.Footer.IndexOffset + (_read * PackFormat.PageEntrySize)))
            {
                PackFormat.ThrowCorrupt(_pack.Path, "Index", -1, "runs past the end of the file");
            }

            _crc = Crc32CUtil.Append(_crc, bytes);
            _read += n;
            _filled = n;
            _pos = 0;
        }

        entry = _buffer[_pos++];
        Check(entry);
        return true;
    }

    private void Check(in PackPageEntry e)
    {
        string problem = null;
        if (e.PageId <= _last && _last >= 0)
        {
            problem = "entries are not in ascending unique order";
        }
        else if (e.PageId >= (uint)_pack.Header.ImagePages)
        {
            problem = "page outside the image";
        }
        else if (e.StoredLength == 0)
        {
            if (e.DataOffset != 0 || e.PageCrc != PackFormat.ZeroPageCrc)
            {
                problem = "zero-page entry carries a frame or a non-zero CRC";
            }
        }
        else if (e.StoredLength > PackFormat.PageSize || e.DataOffset - PackFormat.FramePrefixSize < PackFormat.HeaderSize
                 || e.DataOffset + e.StoredLength > _pack.Footer.DataRegionEnd)
        {
            problem = "frame outside the data region";
        }

        if (e.Reserved != 0)
        {
            problem ??= "reserved field set";
        }

        if (problem != null)
        {
            PackFormat.ThrowCorrupt(_pack.Path, "Index", e.PageId, problem);
        }

        _last = e.PageId;
    }

    public void Dispose()
    {
    }
}
