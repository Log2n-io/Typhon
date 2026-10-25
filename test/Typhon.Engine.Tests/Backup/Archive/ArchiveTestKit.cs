using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Backup;

/// <summary>Builds archived segments without an engine: synthetic WAL segment bytes (a valid header, compressible content) and the publish path.</summary>
internal static class ArchiveTestKit
{
    public static readonly Guid Database = Guid.Parse("a1c41000-0000-0000-0000-000000000001");
    public static readonly Guid Store = Guid.Parse("a1c41000-0000-0000-0000-000000000002");
    public static readonly Guid Timeline = Guid.Parse("a1c41000-0000-0000-0000-000000000003");

    /// <summary>
    /// A WAL segment's first <paramref name="archiveLength"/> bytes: its header (id, first LSN, declared size, previous LSN) and content derived from
    /// <paramref name="seed"/> — half runs (LZ4 shrinks them), half noise (stored raw), so both block encodings occur.
    /// </summary>
    public static byte[] Segment(long segmentId, long firstLsn, int archiveLength, uint declaredSize = 4 << 20, long prevSegmentLsn = 0, int seed = 1)
    {
        var bytes = new byte[archiveLength];
        var header = new WalSegmentHeader();
        header.Initialize(segmentId, firstLsn, prevSegmentLsn, declaredSize);
        header.ComputeAndSetCrc();
        MemoryMarshal.Write(bytes, in header);
        var rng = new Random(seed);
        for (var i = WalSegmentHeader.SizeInBytes; i < archiveLength; i++)
        {
            bytes[i] = (i / 4096) % 2 == 0 ? (byte)(i / 64) : (byte)rng.Next(256);
        }

        return bytes;
    }

    public static ArchivedSegmentHeader Header(byte[] segment, Guid? timeline = null, long sealedUtcTicks = 638_600_000_000_000_000)
    {
        var wal = MemoryMarshal.Read<WalSegmentHeader>(segment);
        return new ArchivedSegmentHeader
        {
            FormatVersion = ArchiveFormat.FormatVersion,
            Compression = ArchiveFormat.CompressionLz4,
            BlockSize = PackFormat.TailBlockSize,
            DatabaseId = Database,
            StoreId = Store,
            TimelineId = timeline ?? Timeline,
            SegmentId = wal.SegmentId,
            FirstLsn = wal.FirstLSN,
            DeclaredSize = wal.SegmentSize,
            SealedUtcTicks = sealedUtcTicks,
            PrevSegmentLsn = wal.PrevSegmentLSN,
        };
    }

    public static ArchivedSegmentFooter Footer(long lastLsn) => new()
    {
        LastLsn = lastLsn,
        LastRecordLsn = lastLsn,
        LastUnitLsn = lastLsn,
        ArchivedUtcTicks = 638_600_000_100_000_000,
    };

    /// <summary>Samples, one per <paramref name="step"/> LSNs over the segment's LSNs, a second apart.</summary>
    public static ArchiveTimeSample[] Samples(long firstLsn, long lastLsn, long step = 10)
    {
        var samples = new List<ArchiveTimeSample>();
        var t = 638_600_000_000_000_000L;
        for (var l = firstLsn; l <= lastLsn; l += step)
        {
            samples.Add(new ArchiveTimeSample(l, t));
            t += TimeSpan.TicksPerSecond;
        }

        return samples.ToArray();
    }

    /// <summary>Publishes segment bytes into <paramref name="root"/>'s archive under its lock; returns the outcome.</summary>
    public static (ArchivePublishOutcome Outcome, ArchivedSegmentInfo Segment) Publish(IStoreFileSystem fs, string root, byte[] segment, long lastLsn,
        ArchiveTimeSample[] samples = null, Guid? timeline = null, Action<ArchivedSegmentFooter> footerCheck = null)
    {
        using var writer = new ArchivedSegmentWriter(null);
        using var t = WalArchiveTimeline.TryOpen(fs, root, timeline ?? Timeline);
        Assert.That(t, Is.Not.Null, "the archive lock is free");
        var header = Header(segment, timeline);
        return t.Publish(writer, header, segment.AsSpan(0, WalSegmentHeader.SizeInBytes), segment.AsSpan(WalSegmentHeader.SizeInBytes), Footer(lastLsn),
            samples ?? Samples(header.FirstLsn, lastLsn));
    }

    /// <summary>Everything verify checks of one archived segment; null when it is sound, else what failed. Decoded bytes are compared when given.</summary>
    public static string FullCheck(IStoreFileSystem fs, string path, byte[] expected = null)
    {
        try
        {
            using var reader = ArchivedSegmentReader.Open(fs, path);
            reader.ReadBlockDirectory();
            reader.ReadSamples();
            var raw = new byte[reader.Footer.ArchiveLength];
            reader.Decode(raw);
            if (!reader.CheckFileCrc())
            {
                return "FileCRC mismatch";
            }

            if (expected != null && !raw.AsSpan().SequenceEqual(expected))
            {
                return "decoded bytes differ";
            }

            return null;
        }
        catch (BackupException e)
        {
            return e.Message;
        }
    }
}
