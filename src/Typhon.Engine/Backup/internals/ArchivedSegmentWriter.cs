using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>What <see cref="ArchivedSegmentWriter.Complete"/> wrote.</summary>
/// <param name="FileLength">The file's length.</param>
/// <param name="RawCrc">CRC32C of the archived WAL bytes <c>[0, ArchiveLength)</c>.</param>
/// <param name="Footer">The footer as written.</param>
internal readonly record struct ArchivedSegmentWritten(long FileLength, uint RawCrc, ArchivedSegmentFooter Footer);

/// <summary>
/// Writes one archived WAL segment as a <c>.twal</c> (impl/1418 §3.5): the header, the segment's bytes in ≤ 1 MiB blocks, the block directory, the
/// samples, the footer and the file CRC, strictly sequentially, synchronously (one write per block, no task per block). Reused for every segment of an
/// archiver's life: its two block buffers are native, allocated once.
/// </summary>
/// <remarks>Single-threaded: the archiver's worker only. It never flushes or publishes: <see cref="WalArchiveTimeline.Publish"/> does.</remarks>
internal sealed class ArchivedSegmentWriter : IDisposable
{
    private const int BlockSize = PackFormat.TailBlockSize;

    private NativeBuffer _raw;
    private NativeBuffer _frame;
    private byte[] _directory = new byte[ArchiveFormat.SectionPrefixSize + 64 * ArchiveFormat.BlockEntrySize];

    /// <summary>Creates a writer whose buffers belong to <paramref name="owner"/> (the archiver's resource), or are direct allocations without one.</summary>
    public ArchivedSegmentWriter(BufferOwner owner)
    {
        _raw = NativeBuffer.Allocate(BlockSize, owner, "WalArchive.RawBlock");
        try
        {
            _frame = NativeBuffer.Allocate(ArchiveFormat.FramePrefixSize + TailBlockCodec.MaxStoredLength(BlockSize), owner, "WalArchive.Frame");
        }
        catch
        {
            ((IDisposable)_raw).Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes the archived segment into <paramref name="file"/> (an empty temp) and returns what it wrote. The archived bytes are
    /// <paramref name="walHeader"/> (the WAL segment's 4 KiB header) followed by <paramref name="walData"/>; their length becomes
    /// <see cref="ArchivedSegmentFooter.ArchiveLength"/>. <paramref name="footer"/> carries the LSNs, flags, gap and dropped range; the offsets, CRCs and
    /// length are filled here.
    /// </summary>
    public ArchivedSegmentWritten Complete(IStoreFile file, in ArchivedSegmentHeader header, ReadOnlySpan<byte> walHeader, ReadOnlySpan<byte> walData,
        ArchivedSegmentFooter footer, ReadOnlySpan<ArchiveTimeSample> samples)
    {
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(_raw == null, this);
        var archiveLength = (long)walHeader.Length + walData.Length;
        if (walHeader.Length != WalSegmentHeader.SizeInBytes || archiveLength % 4096 != 0 || archiveLength > header.DeclaredSize)
        {
            throw new InvalidOperationException($"Archived segment {header.SegmentId}: {archiveLength} bytes is not a whole number of pages within its "
                                                + $"declared {header.DeclaredSize} bytes.");
        }

        var fileCrc = Crc32CUtil.Start;
        long position = 0;

        Span<byte> small = stackalloc byte[ArchiveFormat.HeaderSize];
        header.WriteTo(small);
        Write(file, small, ref position, ref fileCrc);

        // DATA: the raw bytes cut into blocks, each stored LZ4 or raw behind its 4 B length.
        var blockCount = (int)((archiveLength + BlockSize - 1) / BlockSize);
        EnsureDirectory(blockCount);
        var rawCrc = Crc32CUtil.Start;
        long rawDone = 0;
        for (var b = 0; b < blockCount; b++)
        {
            var length = (int)Math.Min(BlockSize, archiveLength - rawDone);
            var raw = Gather(walHeader, walData, rawDone, length);
            rawCrc = Crc32CUtil.Append(rawCrc, raw);
            var frame = _frame.Span;
            var stored = TailBlockCodec.Encode(raw, frame[ArchiveFormat.FramePrefixSize..]);
            BinaryPrimitives.WriteInt32LittleEndian(frame, stored);
            var entry = _directory.AsSpan(ArchiveFormat.SectionPrefixSize + b * ArchiveFormat.BlockEntrySize, ArchiveFormat.BlockEntrySize);
            BinaryPrimitives.WriteInt64LittleEndian(entry, position + ArchiveFormat.FramePrefixSize);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], stored);
            Write(file, frame[..(ArchiveFormat.FramePrefixSize + stored)], ref position, ref fileCrc);
            rawDone += length;
        }

        // BLOCKDIR
        footer.BlockDirOffset = position;
        var directory = _directory.AsSpan(0, ArchiveFormat.SectionPrefixSize + blockCount * ArchiveFormat.BlockEntrySize);
        BinaryPrimitives.WriteInt32LittleEndian(directory, blockCount);
        BinaryPrimitives.WriteInt32LittleEndian(directory[4..], 0);
        footer.BlockDirCrc = Crc32CUtil.Compute(directory);
        Write(file, directory, ref position, ref fileCrc);

        // SAMPLES
        footer.SamplesOffset = position;
        var samplesCrc = Crc32CUtil.Start;
        BinaryPrimitives.WriteInt32LittleEndian(small, samples.Length);
        BinaryPrimitives.WriteInt32LittleEndian(small[4..], 0);
        samplesCrc = Crc32CUtil.Append(samplesCrc, small[..ArchiveFormat.SectionPrefixSize]);
        Write(file, small[..ArchiveFormat.SectionPrefixSize], ref position, ref fileCrc);
        var sampleBytes = MemoryMarshal.AsBytes(samples);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The WAL archive's samples are written as little-endian structs.");
        }

        samplesCrc = Crc32CUtil.Append(samplesCrc, sampleBytes);
        Write(file, sampleBytes, ref position, ref fileCrc);
        footer.SamplesCrc = Crc32CUtil.Finish(samplesCrc);

        // FOOTER, then the FileCRC over everything before its four bytes.
        footer.ArchiveLength = archiveLength;
        footer.RawCrc = Crc32CUtil.Finish(rawCrc);
        footer.WriteTo(small);
        fileCrc = Crc32CUtil.Append(fileCrc, small[..ArchiveFormat.FFileCrc]);
        BinaryPrimitives.WriteUInt32LittleEndian(small[ArchiveFormat.FFileCrc..], Crc32CUtil.Finish(fileCrc));
        file.Write(small[..ArchiveFormat.FooterSize], position);
        position += ArchiveFormat.FooterSize;
        return new ArchivedSegmentWritten(position, footer.RawCrc, footer);
    }

    /// <summary>Raw bytes <c>[offset, offset + length)</c> of header ++ data, contiguous: a span of one of them, or a copy into the raw block.</summary>
    private ReadOnlySpan<byte> Gather(ReadOnlySpan<byte> walHeader, ReadOnlySpan<byte> walData, long offset, int length)
    {
        if (offset >= walHeader.Length)
        {
            return walData.Slice((int)(offset - walHeader.Length), length);
        }

        var raw = _raw.Span[..length];
        var fromHeader = walHeader.Length - (int)offset;
        walHeader[(int)offset..].CopyTo(raw);
        walData[..(length - fromHeader)].CopyTo(raw[fromHeader..]);
        return raw;
    }

    private void EnsureDirectory(int blockCount)
    {
        var needed = ArchiveFormat.SectionPrefixSize + blockCount * ArchiveFormat.BlockEntrySize;
        if (_directory.Length < needed)
        {
            _directory = new byte[needed];
        }
    }

    private static void Write(IStoreFile file, ReadOnlySpan<byte> bytes, ref long position, ref uint fileCrc)
    {
        file.Write(bytes, position);
        fileCrc = Crc32CUtil.Append(fileCrc, bytes);
        position += bytes.Length;
    }

    public void Dispose()
    {
        if (_raw != null)
        {
            ((IDisposable)_raw).Dispose();
            ((IDisposable)_frame).Dispose();
            _raw = null;
            _frame = null;
        }
    }
}
