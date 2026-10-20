using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>One BLOCKDIR entry of a <c>.twal</c>.</summary>
internal readonly record struct ArchivedBlock(long DataOffset, int RawLength, int StoredLength);

/// <summary>
/// Reads a published <c>.twal</c>: header and footer at <see cref="Open"/> (magics, CRCs, version, structural bounds before anything else), sections on
/// demand, every byte checked against the CRC that covers it, as the pack reader does. Opened <c>Read | Delete</c>: a retention delete never fails on it.
/// </summary>
/// <remarks>Not thread-safe.</remarks>
internal sealed class ArchivedSegmentReader : IDisposable
{
    private readonly IStoreFile _file;

    private ArchivedSegmentReader(IStoreFile file, string path, long length, ArchivedSegmentHeader header, ArchivedSegmentFooter footer)
    {
        _file = file;
        Path = path;
        FileLength = length;
        Header = header;
        Footer = footer;
    }

    public string Path { get; }

    public long FileLength { get; }

    public ArchivedSegmentHeader Header { get; }

    public ArchivedSegmentFooter Footer { get; }

    /// <summary>Opens and checks an archived segment. Throws <see cref="BackupPackCorruptException"/> or <see cref="BackupFormatUnsupportedException"/>.</summary>
    public static ArchivedSegmentReader Open(IStoreFileSystem fs, string path)
    {
        var file = fs.OpenRead(path);
        try
        {
            var length = file.Length;
            if (length < ArchiveFormat.HeaderSize + ArchiveFormat.FooterSize)
            {
                Corrupt(path, "File", $"{length} bytes is shorter than a header and a footer");
            }

            Span<byte> buffer = stackalloc byte[ArchiveFormat.HeaderSize];
            if (!file.ReadExactly(buffer, 0))
            {
                Corrupt(path, "Header", "unreadable");
            }

            var header = CheckHeader(path, buffer);
            if (!file.ReadExactly(buffer[..ArchiveFormat.FooterSize], length - ArchiveFormat.FooterSize))
            {
                Corrupt(path, "Footer", "unreadable");
            }

            var footer = CheckFooter(path, buffer[..ArchiveFormat.FooterSize], length, header);
            var reader = new ArchivedSegmentReader(file, path, length, header, footer);
            file = null;
            return reader;
        }
        finally
        {
            file?.Dispose();
        }
    }

    private static ArchivedSegmentHeader CheckHeader(string path, ReadOnlySpan<byte> bytes)
    {
        if (!bytes[..8].SequenceEqual(ArchiveFormat.Magic))
        {
            Corrupt(path, "Header", "not an archived WAL segment (magic)");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[ArchiveFormat.HHeaderCrc..]) != Crc32CUtil.Compute(bytes[..ArchiveFormat.HHeaderCrc]))
        {
            Corrupt(path, "Header", "HeaderCRC mismatch");
        }

        var header = ArchivedSegmentHeader.Read(bytes);
        if (header.FormatVersion > ArchiveFormat.FormatVersion)
        {
            throw new BackupFormatUnsupportedException(path, "FormatVersion", header.FormatVersion, ArchiveFormat.FormatVersion);
        }

        if (header.FormatVersion == 0)
        {
            Corrupt(path, "Header", "FormatVersion 0");
        }

        if (header.Compression != ArchiveFormat.CompressionLz4)
        {
            throw new BackupFormatUnsupportedException(path, "Compression", header.Compression, ArchiveFormat.CompressionLz4);
        }

        if (header.BlockSize != PackFormat.TailBlockSize || header.DeclaredSize < WalSegmentHeader.SizeInBytes || header.SegmentId <= 0 || header.FirstLsn <= 0)
        {
            Corrupt(path, "Header", $"block size {header.BlockSize}, declared size {header.DeclaredSize}, segment {header.SegmentId}, first LSN {header.FirstLsn}");
        }

        return header;
    }

    private static ArchivedSegmentFooter CheckFooter(string path, ReadOnlySpan<byte> bytes, long length, in ArchivedSegmentHeader header)
    {
        if (!bytes.Slice(ArchiveFormat.FFooterMagic, 4).SequenceEqual(ArchiveFormat.FooterMagic))
        {
            Corrupt(path, "Footer", "magic");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[ArchiveFormat.FFooterCrc..]) != Crc32CUtil.Compute(bytes[..ArchiveFormat.FFooterCrc]))
        {
            Corrupt(path, "Footer", "FooterCRC mismatch");
        }

        var f = ArchivedSegmentFooter.Read(bytes);
        var footerStart = length - ArchiveFormat.FooterSize;
        // Structural bounds before anything is read through them: the sections in order, inside the file, the archived length whole pages of the segment.
        if (f.BlockDirOffset < ArchiveFormat.HeaderSize || f.SamplesOffset < f.BlockDirOffset + ArchiveFormat.SectionPrefixSize
            || f.SamplesOffset + ArchiveFormat.SectionPrefixSize > footerStart || f.ArchiveLength < WalSegmentHeader.SizeInBytes
            || f.ArchiveLength % 4096 != 0 || f.ArchiveLength > header.DeclaredSize)
        {
            Corrupt(path, "Footer", $"sections out of bounds (blockdir {f.BlockDirOffset}, samples {f.SamplesOffset}, archived {f.ArchiveLength}, file {length})");
        }

        if (f.LastLsn < header.FirstLsn - 1 || f.LastRecordLsn > f.LastLsn || f.LastUnitLsn > f.LastRecordLsn)
        {
            Corrupt(path, "Footer", $"LSNs out of order (first {header.FirstLsn}, last {f.LastLsn}, record {f.LastRecordLsn}, unit {f.LastUnitLsn})");
        }

        return f;
    }

    /// <summary>The block directory, checked: CRC, then blocks contiguous from the header to the directory, raw lengths adding up to the archived length.</summary>
    public ArchivedBlock[] ReadBlockDirectory()
    {
        var prefix = new byte[ArchiveFormat.SectionPrefixSize];
        ReadOrThrow(prefix, Footer.BlockDirOffset, "BlockDir");
        var count = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var sectionLength = (long)ArchiveFormat.SectionPrefixSize + (long)count * ArchiveFormat.BlockEntrySize;
        if (count < 1 || Footer.BlockDirOffset + sectionLength != Footer.SamplesOffset)
        {
            Corrupt(Path, "BlockDir", $"{count} blocks do not fill the directory");
        }

        var bytes = new byte[sectionLength];
        ReadOrThrow(bytes, Footer.BlockDirOffset, "BlockDir");
        if (Crc32CUtil.Compute(bytes) != Footer.BlockDirCrc)
        {
            Corrupt(Path, "BlockDir", "BlockDirCRC mismatch");
        }

        var blocks = new ArchivedBlock[count];
        var expected = (long)ArchiveFormat.HeaderSize + ArchiveFormat.FramePrefixSize;
        long raw = 0;
        for (var i = 0; i < count; i++)
        {
            var e = bytes.AsSpan(ArchiveFormat.SectionPrefixSize + i * ArchiveFormat.BlockEntrySize);
            var block = new ArchivedBlock(BinaryPrimitives.ReadInt64LittleEndian(e), BinaryPrimitives.ReadInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(e[12..]));
            var full = i + 1 < count;
            if (block.DataOffset != expected || block.RawLength <= 0 || block.RawLength > PackFormat.TailBlockSize
                || (full && block.RawLength != PackFormat.TailBlockSize) || block.StoredLength <= 0 || block.StoredLength > block.RawLength)
            {
                Corrupt(Path, "BlockDir", $"block {i} is out of place or of a wrong size");
            }

            blocks[i] = block;
            expected = block.DataOffset + block.StoredLength + ArchiveFormat.FramePrefixSize;
            raw += block.RawLength;
        }

        if (raw != Footer.ArchiveLength || expected - ArchiveFormat.FramePrefixSize != Footer.BlockDirOffset)
        {
            Corrupt(Path, "BlockDir", $"the blocks hold {raw} raw bytes, the footer {Footer.ArchiveLength}");
        }

        return blocks;
    }

    /// <summary>The time samples, checked: CRC, count, LSNs inside the segment, both columns ascending.</summary>
    public ArchiveTimeSample[] ReadSamples()
    {
        var prefix = new byte[ArchiveFormat.SectionPrefixSize];
        ReadOrThrow(prefix, Footer.SamplesOffset, "Samples");
        var count = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var footerStart = FileLength - ArchiveFormat.FooterSize;
        if (count < 0 || Footer.SamplesOffset + ArchiveFormat.SectionPrefixSize + (long)count * ArchiveFormat.SampleSize != footerStart)
        {
            Corrupt(Path, "Samples", $"{count} samples do not fill the section");
        }

        var samples = new ArchiveTimeSample[count];
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
        ReadOrThrow(bytes, Footer.SamplesOffset + ArchiveFormat.SectionPrefixSize, "Samples");
        var crc = Crc32CUtil.Append(Crc32CUtil.Start, prefix);
        if (Crc32CUtil.Finish(Crc32CUtil.Append(crc, bytes)) != Footer.SamplesCrc)
        {
            Corrupt(Path, "Samples", "SamplesCRC mismatch");
        }

        for (var i = 0; i < count; i++)
        {
            var s = samples[i];
            if (s.Lsn < Header.FirstLsn || s.Lsn > Footer.LastLsn || (i > 0 && (s.Lsn <= samples[i - 1].Lsn || s.UtcTicks < samples[i - 1].UtcTicks)))
            {
                Corrupt(Path, "Samples", $"sample {i} (LSN {s.Lsn}) is out of the segment or out of order");
            }
        }

        return samples;
    }

    /// <summary>
    /// Decodes the archived WAL bytes <c>[0, ArchiveLength)</c> into <paramref name="destination"/>, checking every frame, the <c>RawCrc</c> and the WAL
    /// segment header against this file's header. Never returns a byte it read wrong.
    /// </summary>
    public void Decode(Span<byte> destination)
    {
        if (destination.Length < Footer.ArchiveLength)
        {
            throw new ArgumentException($"{destination.Length} bytes cannot hold {Footer.ArchiveLength}.", nameof(destination));
        }

        var blocks = ReadBlockDirectory();
        var frame = ArrayPool<byte>.Shared.Rent(TailBlockCodec.MaxStoredLength(PackFormat.TailBlockSize));
        try
        {
            var crc = Crc32CUtil.Start;
            long offset = 0;
            for (var i = 0; i < blocks.Length; i++)
            {
                var block = blocks[i];
                var stored = frame.AsSpan(0, block.StoredLength + ArchiveFormat.FramePrefixSize);
                ReadOrThrow(stored, block.DataOffset - ArchiveFormat.FramePrefixSize, "Data");
                if (BinaryPrimitives.ReadInt32LittleEndian(stored) != block.StoredLength)
                {
                    Corrupt(Path, "Data", $"block {i}'s length prefix disagrees with the directory");
                }

                var raw = destination.Slice((int)offset, block.RawLength);
                if (!TailBlockCodec.Decode(stored[ArchiveFormat.FramePrefixSize..], raw))
                {
                    Corrupt(Path, "Data", $"block {i} does not decode");
                }

                crc = Crc32CUtil.Append(crc, raw);
                offset += block.RawLength;
            }

            if (Crc32CUtil.Finish(crc) != Footer.RawCrc)
            {
                Corrupt(Path, "Data", "RawCrc mismatch");
            }

            var wal = MemoryMarshal.Read<WalSegmentHeader>(destination);
            if (!wal.Validate() || wal.SegmentId != Header.SegmentId || wal.FirstLSN != Header.FirstLsn || wal.SegmentSize != Header.DeclaredSize
                || wal.PrevSegmentLSN != Header.PrevSegmentLsn)
            {
                Corrupt(Path, "Data", "the archived WAL header disagrees with the file's header");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Whether the <c>FileCRC</c> holds over <c>[0, EOF-4)</c>: one read of the whole file.</summary>
    public bool CheckFileCrc()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            var crc = Crc32CUtil.Start;
            var end = FileLength - 4;
            for (long offset = 0; offset < end;)
            {
                var n = (int)Math.Min(buffer.Length, end - offset);
                ReadOrThrow(buffer.AsSpan(0, n), offset, "File");
                crc = Crc32CUtil.Append(crc, buffer.AsSpan(0, n));
                offset += n;
            }

            Span<byte> stored = stackalloc byte[4];
            ReadOrThrow(stored, end, "File");
            return Crc32CUtil.Finish(crc) == BinaryPrimitives.ReadUInt32LittleEndian(stored);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReadOrThrow(Span<byte> destination, long offset, string section)
    {
        if (!_file.ReadExactly(destination, offset))
        {
            Corrupt(Path, section, $"runs past the end of the file at {offset}");
        }
    }

    private static void Corrupt(string path, string section, string detail) => PackFormat.ThrowCorrupt(path, section, -1, detail);

    public void Dispose() => _file.Dispose();
}
