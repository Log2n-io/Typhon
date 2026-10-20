using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Typhon.Engine.Internals;

/// <summary>
/// The <c>.twal</c> format, version 1 (impl/1418 §3.5): one archived WAL segment, immutable and self-describing. A 128 B header, a data region of
/// ≤ 1 MiB raw blocks (the pack tail's codec, <see cref="TailBlockCodec"/>), a block directory, the time samples of the segment's LSNs, a 128 B footer.
/// Little-endian, written strictly sequentially, read from the footer; every byte under a CRC that verify checks (BKP-10, BKP-08's discipline).
/// </summary>
internal static class ArchiveFormat
{
    public const int HeaderSize = 128;
    public const int FooterSize = 128;
    public const int SectionPrefixSize = 8;
    public const int BlockEntrySize = 16;
    public const int SampleSize = 16;
    public const int FramePrefixSize = 4;

    /// <summary>The format this build writes, and the highest it reads.</summary>
    public const ushort FormatVersion = 1;

    public const byte CompressionLz4 = 1;

    public const string Extension = ".twal";
    public const string TempSuffix = ".tmp";
    public const string WalDirectoryName = "wal";
    public const string TimelineFileName = "timeline.json";
    public const string LockFileName = "archive.lock";
    public const string LockHolderFileName = "archive.lock.json";
    public const string IndexFileName = "archive-index.bin";

    // Header flags.
    public const byte HeaderFlagAdopted = 0x01;

    // Footer flags.
    public const uint FooterFlagTornTrimmed = 0x01;
    public const uint FooterFlagDroppedApplicable = 0x02;
    public const uint FooterFlagGapBefore = 0x04;
    public const uint FooterFlagCorruptMidSegment = 0x08;

    // Header offsets.
    public const int HMagic = 0x00;
    public const int HFormatVersion = 0x08;
    public const int HCompression = 0x0A;
    public const int HFlags = 0x0B;
    public const int HBlockSize = 0x0C;
    public const int HDatabaseId = 0x10;
    public const int HStoreId = 0x20;
    public const int HTimelineId = 0x30;
    public const int HSegmentId = 0x40;
    public const int HFirstLsn = 0x48;
    public const int HDeclaredSize = 0x50;
    public const int HSealedUtcTicks = 0x58;
    public const int HPrevSegmentLsn = 0x60;
    public const int HHeaderCrc = 0x7C;

    // Footer offsets, relative to EOF - 128.
    public const int FBlockDirOffset = 0;
    public const int FSamplesOffset = 8;
    public const int FArchiveLength = 16;
    public const int FRawCrc = 24;
    public const int FBlockDirCrc = 28;
    public const int FSamplesCrc = 32;
    public const int FFlags = 36;
    public const int FLastLsn = 40;
    public const int FLastRecordLsn = 48;
    public const int FLastUnitLsn = 56;
    public const int FGapFromLsn = 64;
    public const int FGapToLsn = 72;
    public const int FGapReason = 80;
    public const int FDroppedFromLsn = 84;
    public const int FDroppedToLsn = 92;
    public const int FArchivedUtcTicks = 100;
    public const int FFooterCrc = 116;
    public const int FFooterMagic = 120;
    public const int FFileCrc = 124;

    public static ReadOnlySpan<byte> Magic => "TYPHWSEG"u8;
    public static ReadOnlySpan<byte> FooterMagic => "TWSG"u8;

    /// <summary><c>{SegmentId:D16}.twal</c>: the WAL segment's own name, so an archived segment is found by its id (and a collision is visible).</summary>
    public static string FileName(long segmentId) => segmentId.ToString("D16", CultureInfo.InvariantCulture) + Extension;

    /// <summary>Parses an archived segment's file name; <paramref name="temp"/> marks an in-progress or abandoned one.</summary>
    public static bool TryParseFileName(string name, out long segmentId, out bool temp)
    {
        segmentId = 0;
        temp = name.EndsWith(Extension + TempSuffix, StringComparison.Ordinal);
        var stem = temp ? name[..^(Extension.Length + TempSuffix.Length)] : name.EndsWith(Extension, StringComparison.Ordinal) ? name[..^Extension.Length] : null;
        return stem is { Length: 16 } && long.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out segmentId)
                                      && stem == segmentId.ToString("D16", CultureInfo.InvariantCulture);
    }

    /// <summary>The directory of a timeline's archive: <c>wal/{TimelineId:D}</c>, lower-case and hyphenated.</summary>
    public static string TimelineDirectoryName(Guid timelineId) => timelineId.ToString("D");
}

/// <summary>The 128 B header of a <c>.twal</c>: what is known about the segment before its bytes.</summary>
internal struct ArchivedSegmentHeader
{
    public ushort FormatVersion;
    public byte Compression;
    public byte Flags;
    public int BlockSize;
    public Guid DatabaseId;
    public Guid StoreId;
    public Guid TimelineId;
    public long SegmentId;
    public long FirstLsn;
    public uint DeclaredSize;
    public long SealedUtcTicks;
    public long PrevSegmentLsn;

    public readonly bool Adopted => (Flags & ArchiveFormat.HeaderFlagAdopted) != 0;

    public readonly void WriteTo(Span<byte> dst)
    {
        dst = dst[..ArchiveFormat.HeaderSize];
        dst.Clear();
        ArchiveFormat.Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[ArchiveFormat.HFormatVersion..], FormatVersion);
        dst[ArchiveFormat.HCompression] = Compression;
        dst[ArchiveFormat.HFlags] = Flags;
        BinaryPrimitives.WriteInt32LittleEndian(dst[ArchiveFormat.HBlockSize..], BlockSize);
        DatabaseId.TryWriteBytes(dst[ArchiveFormat.HDatabaseId..]);
        StoreId.TryWriteBytes(dst[ArchiveFormat.HStoreId..]);
        TimelineId.TryWriteBytes(dst[ArchiveFormat.HTimelineId..]);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.HSegmentId..], SegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.HFirstLsn..], FirstLsn);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.HDeclaredSize..], DeclaredSize);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.HSealedUtcTicks..], SealedUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.HPrevSegmentLsn..], PrevSegmentLsn);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.HHeaderCrc..], Crc32CUtil.Compute(dst[..ArchiveFormat.HHeaderCrc]));
    }

    public static ArchivedSegmentHeader Read(ReadOnlySpan<byte> src) => new()
    {
        FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(src[ArchiveFormat.HFormatVersion..]),
        Compression = src[ArchiveFormat.HCompression],
        Flags = src[ArchiveFormat.HFlags],
        BlockSize = BinaryPrimitives.ReadInt32LittleEndian(src[ArchiveFormat.HBlockSize..]),
        DatabaseId = new Guid(src.Slice(ArchiveFormat.HDatabaseId, 16)),
        StoreId = new Guid(src.Slice(ArchiveFormat.HStoreId, 16)),
        TimelineId = new Guid(src.Slice(ArchiveFormat.HTimelineId, 16)),
        SegmentId = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.HSegmentId..]),
        FirstLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.HFirstLsn..]),
        DeclaredSize = BinaryPrimitives.ReadUInt32LittleEndian(src[ArchiveFormat.HDeclaredSize..]),
        SealedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.HSealedUtcTicks..]),
        PrevSegmentLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.HPrevSegmentLsn..]),
    };
}

/// <summary>The 128 B footer of a <c>.twal</c>: what the archiver learned from the segment's bytes, and where every section is.</summary>
internal struct ArchivedSegmentFooter
{
    public long BlockDirOffset;
    public long SamplesOffset;
    public long ArchiveLength;
    public uint RawCrc;
    public uint BlockDirCrc;
    public uint SamplesCrc;
    public uint Flags;
    public long LastLsn;
    public long LastRecordLsn;
    public long LastUnitLsn;
    public long GapFromLsn;
    public long GapToLsn;
    public ArchiveGapReason GapReason;
    public long DroppedFromLsn;
    public long DroppedToLsn;
    public long ArchivedUtcTicks;

    public readonly bool GapBefore => (Flags & ArchiveFormat.FooterFlagGapBefore) != 0;

    /// <summary>Serialises bytes <c>[0, 124)</c>: every field, the <c>FooterCRC</c> and the magic. The <c>FileCRC</c> is the writer's to append.</summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        dst = dst[..ArchiveFormat.FooterSize];
        dst.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FBlockDirOffset..], BlockDirOffset);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FSamplesOffset..], SamplesOffset);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FArchiveLength..], ArchiveLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.FRawCrc..], RawCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.FBlockDirCrc..], BlockDirCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.FSamplesCrc..], SamplesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.FFlags..], Flags);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FLastLsn..], LastLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FLastRecordLsn..], LastRecordLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FLastUnitLsn..], LastUnitLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FGapFromLsn..], GapFromLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FGapToLsn..], GapToLsn);
        dst[ArchiveFormat.FGapReason] = (byte)GapReason;
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FDroppedFromLsn..], DroppedFromLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FDroppedToLsn..], DroppedToLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[ArchiveFormat.FArchivedUtcTicks..], ArchivedUtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[ArchiveFormat.FFooterCrc..], Crc32CUtil.Compute(dst[..ArchiveFormat.FFooterCrc]));
        ArchiveFormat.FooterMagic.CopyTo(dst[ArchiveFormat.FFooterMagic..]);
    }

    public static ArchivedSegmentFooter Read(ReadOnlySpan<byte> src) => new()
    {
        BlockDirOffset = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FBlockDirOffset..]),
        SamplesOffset = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FSamplesOffset..]),
        ArchiveLength = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FArchiveLength..]),
        RawCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[ArchiveFormat.FRawCrc..]),
        BlockDirCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[ArchiveFormat.FBlockDirCrc..]),
        SamplesCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[ArchiveFormat.FSamplesCrc..]),
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(src[ArchiveFormat.FFlags..]),
        LastLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FLastLsn..]),
        LastRecordLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FLastRecordLsn..]),
        LastUnitLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FLastUnitLsn..]),
        GapFromLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FGapFromLsn..]),
        GapToLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FGapToLsn..]),
        GapReason = (ArchiveGapReason)src[ArchiveFormat.FGapReason],
        DroppedFromLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FDroppedFromLsn..]),
        DroppedToLsn = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FDroppedToLsn..]),
        ArchivedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[ArchiveFormat.FArchivedUtcTicks..]),
    };
}

/// <summary>One time sample: the WAL was durable through <see cref="Lsn"/> at <see cref="UtcTicks"/> (06 §3).</summary>
internal readonly record struct ArchiveTimeSample(long Lsn, long UtcTicks);

/// <summary>What an archived segment is, from its header and footer (and its samples' bounds): what the index caches and the listing reports.</summary>
internal sealed record ArchivedSegmentInfo
{
    public required string File { get; init; }

    public required long FileLength { get; init; }

    public required ArchivedSegmentHeader Header { get; init; }

    public required ArchivedSegmentFooter Footer { get; init; }

    public long FirstSampleLsn { get; init; }

    public long FirstSampleUtcTicks { get; init; }

    public long LastSampleLsn { get; init; }

    public long LastSampleUtcTicks { get; init; }

    public long SegmentId => Header.SegmentId;

    public long FirstLsn => Header.FirstLsn;

    public long LastLsn => Footer.LastLsn;

    /// <summary>The first segment of an engine session: no previous segment, and past the database's first LSN.</summary>
    public bool SessionStart => Header.PrevSegmentLsn == 0 && Header.FirstLsn > 1;
}

/// <summary>Why a timeline exists.</summary>
internal enum TimelineReason
{
    /// <summary>The database's first timeline.</summary>
    Genesis,

    /// <summary>A restore started it.</summary>
    Restore,

    /// <summary>A schema change forked it: replay across it from an earlier base would apply records to the wrong layout (Q4).</summary>
    SchemaChange,

    /// <summary>A completed bulk load forked it: replay cannot recreate the bulk's pages (Q4).</summary>
    BulkLoad,

    /// <summary>Not known: the parent of the oldest timeline an engine knew, whose own origin its meta does not record.</summary>
    Unknown,
}

/// <summary>Where a restored timeline came from.</summary>
internal sealed class TimelineRestoredFrom
{
    public uint BackupId { get; set; }

    public long PointLsn { get; set; }

    public long TargetLsn { get; set; }

    public long EffectiveLsn { get; set; }
}

/// <summary><c>wal/&lt;t&gt;/timeline.json</c>: written once, from the database's meta (impl/1418 §3.6, WA-22).</summary>
internal sealed class TimelineFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid TimelineId { get; set; }

    public Guid DatabaseId { get; set; }

    public Guid StoreId { get; set; }

    /// <summary>The timeline it forked or restored from; null for the genesis timeline.</summary>
    public Guid? ParentTimelineId { get; set; }

    /// <summary>The last LSN it shares with its parent; 0 for the genesis timeline.</summary>
    public long OriginLsn { get; set; }

    public TimelineReason Reason { get; set; }

    public DateTime CreatedUtc { get; set; }

    public TimelineRestoredFrom RestoredFrom { get; set; }
}

/// <summary>Source-generated serialisation of the archive's JSON files.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(TimelineFile))]
internal sealed partial class ArchiveJsonContext : JsonSerializerContext;
