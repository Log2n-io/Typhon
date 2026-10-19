using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Engine.Internals;

/// <summary>
/// The <c>.tpack</c> format, version 1 (plan 1413 §2.2, DC-5): a 256 B header written once at the freeze, a data region of frames in arrival
/// order (pages and ≤ 1 MiB WAL-tail blocks interleaved), then the TAILDIR, SUSPECT and INDEX sections and a 128 B footer. Little-endian, 64-bit
/// absolute offsets, written strictly sequentially, read by random access from the footer.
/// </summary>
internal static class PackFormat
{
    public const int HeaderSize = 256;
    public const int FooterSize = 128;
    public const int PageEntrySize = 24;
    public const int TailSegmentEntrySize = 32;
    public const int TailBlockEntrySize = 16;
    public const int SectionPrefixSize = 8;
    public const int FramePrefixSize = 4;

    /// <summary>The format this build writes, and the highest it reads.</summary>
    public const ushort FormatVersion = 1;

    public const int PageSize = 8192;

    /// <summary>The only compression of version 1: an LZ4 block (K4os).</summary>
    public const byte CompressionLz4 = 1;

    public const byte HeaderFlagOffline = 0x02;
    public const byte FooterFlagSuspect = 0x01;

    /// <summary>Raw bytes per WAL-tail block. Every block of a segment is this size except its last.</summary>
    public const int TailBlockSize = 1 << 20;

    /// <summary>The WAL segment header every tail segment starts with.</summary>
    public const int WalSegmentHeaderSize = WalSegmentHeader.SizeInBytes;

    /// <summary>LZ4's worst case for one page: the bound a page frame's payload is compressed into.</summary>
    public const int MaxCompressedPage = PageSize + (PageSize / 255) + 16;

    public const string PackExtension = ".tpack";
    public const string TempSuffix = ".tmp";
    public const string SidecarInfix = ".idx";

    // Header offsets.
    public const int HMagic = 0x000;
    public const int HFormatVersion = 0x008;
    public const int HKind = 0x00A;
    public const int HFlags = 0x00B;
    public const int HPageSize = 0x00C;
    public const int HDatabaseId = 0x010;
    public const int HStoreId = 0x020;
    public const int HChainId = 0x030;
    public const int HBackupId = 0x040;
    public const int HParentId = 0x044;
    public const int HBaseId = 0x048;
    public const int HImagePages = 0x04C;
    public const int HCheckpointLsn = 0x050;
    public const int HFreezeUtcTicks = 0x058;
    public const int HTimelineId = 0x060;
    public const int HFormatRevision = 0x070;
    public const int HCompression = 0x074;
    public const int HEngineVersion = 0x078;
    public const int EngineVersionLength = 16;
    public const int HHeaderCrc = 0x0FC;

    // Footer offsets, relative to EOF - 128.
    public const int FDataRegionEnd = 0;
    public const int FTailDirOffset = 8;
    public const int FSuspectOffset = 16;
    public const int FIndexOffset = 24;
    public const int FPageCount = 32;
    public const int FTailSegmentCount = 36;
    public const int FPointLsn = 40;
    public const int FTailEndUtcTicks = 48;
    public const int FSuspectCount = 56;
    public const int FFlags = 60;
    public const int FIndexCrc = 64;
    public const int FTailDirCrc = 68;
    public const int FSuspectCrc = 72;
    public const int FFooterCrc = 116;
    public const int FFooterMagic = 120;
    public const int FFileCrc = 124;

    public static ReadOnlySpan<byte> Magic => "TYPHPACK"u8;
    public static ReadOnlySpan<byte> FooterMagic => "PACK"u8;

    /// <summary>The <c>PageCrc</c> of an all-zero page, which is stored with no frame.</summary>
    public static readonly uint ZeroPageCrc = Crc32CUtil.Compute(new byte[PageSize]);

    /// <summary>The letter a pack's file name carries for its kind.</summary>
    public static char KindLetter(BackupKind kind) => kind switch
    {
        BackupKind.Full => 'F',
        BackupKind.Incremental => 'I',
        BackupKind.Compacted => 'C',
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary><c>{id:D8}-{K}.tpack</c>.</summary>
    public static string FileName(uint backupId, BackupKind kind) =>
        backupId.ToString("D8", CultureInfo.InvariantCulture) + "-" + KindLetter(kind) + PackExtension;

    /// <summary>
    /// Parses a pack file name. <paramref name="kind"/> is <c>0</c> for a name that is not a pack; <paramref name="temp"/> marks an in-progress
    /// or abandoned pack (<c>*.tpack.tmp</c>) or one of its sidecars (<c>*.tpack.tmp.idx*</c>).
    /// </summary>
    public static bool TryParseFileName(string name, out uint backupId, out BackupKind kind, out bool temp)
    {
        backupId = 0;
        kind = 0;
        temp = false;
        // {8 digits}-{K}.tpack[.tmp[.idxN]]
        if (name.Length < 16 || name[8] != '-' || !name.AsSpan(10).StartsWith(PackExtension, StringComparison.Ordinal))
        {
            return false;
        }

        if (!uint.TryParse(name.AsSpan(0, 8), NumberStyles.None, CultureInfo.InvariantCulture, out backupId))
        {
            return false;
        }

        kind = name[9] switch
        {
            'F' => BackupKind.Full,
            'I' => BackupKind.Incremental,
            'C' => BackupKind.Compacted,
            _ => 0,
        };
        if (kind == 0)
        {
            return false;
        }

        var rest = name.AsSpan(10 + PackExtension.Length);
        if (rest.Length == 0)
        {
            return true;
        }

        if (rest.StartsWith(TempSuffix, StringComparison.Ordinal))
        {
            temp = true;
            return true;
        }

        kind = 0;
        return false;
    }

    [DoesNotReturn]
    public static void ThrowCorrupt(string pack, string section, long pageId, string detail) =>
        throw new BackupPackCorruptException(pack, section, pageId, detail);
}

/// <summary>The 256 B header: what was known at the freeze.</summary>
internal struct PackHeader
{
    public ushort FormatVersion;
    public BackupKind Kind;
    public byte Flags;
    public uint PageSize;
    public Guid DatabaseId;
    public Guid StoreId;
    public Guid ChainId;
    public uint BackupId;
    public uint ParentId;
    public uint BaseId;
    public int ImagePages;
    public long CheckpointLsn;
    public long FreezeUtcTicks;
    public Guid TimelineId;
    public int DatabaseFormatRevision;
    public byte Compression;
    public string EngineVersion;

    public readonly bool Offline => (Flags & PackFormat.HeaderFlagOffline) != 0;

    /// <summary>Serialises the header, CRC included.</summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        dst = dst[..PackFormat.HeaderSize];
        dst.Clear();
        PackFormat.Magic.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[PackFormat.HFormatVersion..], FormatVersion);
        dst[PackFormat.HKind] = (byte)Kind;
        dst[PackFormat.HFlags] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.HPageSize..], PageSize);
        DatabaseId.TryWriteBytes(dst[PackFormat.HDatabaseId..]);
        StoreId.TryWriteBytes(dst[PackFormat.HStoreId..]);
        ChainId.TryWriteBytes(dst[PackFormat.HChainId..]);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.HBackupId..], BackupId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.HParentId..], ParentId);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.HBaseId..], BaseId);
        BinaryPrimitives.WriteInt32LittleEndian(dst[PackFormat.HImagePages..], ImagePages);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.HCheckpointLsn..], CheckpointLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.HFreezeUtcTicks..], FreezeUtcTicks);
        TimelineId.TryWriteBytes(dst[PackFormat.HTimelineId..]);
        BinaryPrimitives.WriteInt32LittleEndian(dst[PackFormat.HFormatRevision..], DatabaseFormatRevision);
        dst[PackFormat.HCompression] = Compression;
        if (!string.IsNullOrEmpty(EngineVersion))
        {
            var field = dst.Slice(PackFormat.HEngineVersion, PackFormat.EngineVersionLength);
            var bytes = Encoding.UTF8.GetBytes(EngineVersion);
            var n = Math.Min(bytes.Length, field.Length);
            // Never cut a UTF-8 sequence in half.
            while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80)
            {
                n--;
            }

            bytes.AsSpan(0, n).CopyTo(field);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.HHeaderCrc..], Crc32CUtil.Compute(dst[..PackFormat.HHeaderCrc]));
    }

    /// <summary>Parses a header without validating it; <see cref="PackReader"/> checks magic, CRC and version first.</summary>
    public static PackHeader Read(ReadOnlySpan<byte> src)
    {
        var engine = src.Slice(PackFormat.HEngineVersion, PackFormat.EngineVersionLength);
        var len = engine.IndexOf((byte)0);
        return new PackHeader
        {
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(src[PackFormat.HFormatVersion..]),
            Kind = (BackupKind)src[PackFormat.HKind],
            Flags = src[PackFormat.HFlags],
            PageSize = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.HPageSize..]),
            DatabaseId = new Guid(src.Slice(PackFormat.HDatabaseId, 16)),
            StoreId = new Guid(src.Slice(PackFormat.HStoreId, 16)),
            ChainId = new Guid(src.Slice(PackFormat.HChainId, 16)),
            BackupId = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.HBackupId..]),
            ParentId = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.HParentId..]),
            BaseId = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.HBaseId..]),
            ImagePages = BinaryPrimitives.ReadInt32LittleEndian(src[PackFormat.HImagePages..]),
            CheckpointLsn = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.HCheckpointLsn..]),
            FreezeUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.HFreezeUtcTicks..]),
            TimelineId = new Guid(src.Slice(PackFormat.HTimelineId, 16)),
            DatabaseFormatRevision = BinaryPrimitives.ReadInt32LittleEndian(src[PackFormat.HFormatRevision..]),
            Compression = src[PackFormat.HCompression],
            EngineVersion = Encoding.UTF8.GetString(len < 0 ? engine : engine[..len]),
        };
    }
}

/// <summary>The 128 B footer: what was known at the end of the capture, and where every section is.</summary>
internal struct PackFooter
{
    public long DataRegionEnd;
    public long TailDirOffset;
    public long SuspectOffset;
    public long IndexOffset;
    public int PageCount;
    public int TailSegmentCount;
    public long PointLsn;
    public long TailEndUtcTicks;
    public int SuspectCount;
    public byte Flags;
    public uint IndexCrc;
    public uint TailDirCrc;
    public uint SuspectCrc;

    public readonly bool Suspect => (Flags & PackFormat.FooterFlagSuspect) != 0;

    /// <summary>
    /// Serialises bytes <c>[0, 124)</c> of the footer: every field, the <c>FooterCRC</c> and the magic. The last four bytes, <c>FileCRC</c>,
    /// cover everything before them and are the writer's to append.
    /// </summary>
    public readonly void WriteTo(Span<byte> dst)
    {
        dst = dst[..PackFormat.FooterSize];
        dst.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FDataRegionEnd..], DataRegionEnd);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FTailDirOffset..], TailDirOffset);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FSuspectOffset..], SuspectOffset);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FIndexOffset..], IndexOffset);
        BinaryPrimitives.WriteInt32LittleEndian(dst[PackFormat.FPageCount..], PageCount);
        BinaryPrimitives.WriteInt32LittleEndian(dst[PackFormat.FTailSegmentCount..], TailSegmentCount);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FPointLsn..], PointLsn);
        BinaryPrimitives.WriteInt64LittleEndian(dst[PackFormat.FTailEndUtcTicks..], TailEndUtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(dst[PackFormat.FSuspectCount..], SuspectCount);
        dst[PackFormat.FFlags] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.FIndexCrc..], IndexCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.FTailDirCrc..], TailDirCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.FSuspectCrc..], SuspectCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[PackFormat.FFooterCrc..], Crc32CUtil.Compute(dst[..PackFormat.FFooterCrc]));
        PackFormat.FooterMagic.CopyTo(dst[PackFormat.FFooterMagic..]);
    }

    public static PackFooter Read(ReadOnlySpan<byte> src) => new()
    {
        DataRegionEnd = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FDataRegionEnd..]),
        TailDirOffset = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FTailDirOffset..]),
        SuspectOffset = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FSuspectOffset..]),
        IndexOffset = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FIndexOffset..]),
        PageCount = BinaryPrimitives.ReadInt32LittleEndian(src[PackFormat.FPageCount..]),
        TailSegmentCount = BinaryPrimitives.ReadInt32LittleEndian(src[PackFormat.FTailSegmentCount..]),
        PointLsn = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FPointLsn..]),
        TailEndUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(src[PackFormat.FTailEndUtcTicks..]),
        SuspectCount = BinaryPrimitives.ReadInt32LittleEndian(src[PackFormat.FSuspectCount..]),
        Flags = src[PackFormat.FFlags],
        IndexCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.FIndexCrc..]),
        TailDirCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.FTailDirCrc..]),
        SuspectCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[PackFormat.FSuspectCrc..]),
    };
}

/// <summary>One INDEX entry: where page <see cref="PageId"/>'s frame is, and the CRC of its uncompressed image.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PackPageEntry
{
    public uint PageId;

    /// <summary><c>0</c> = all-zero page (no frame) · <c>8192</c> = raw · else LZ4.</summary>
    public uint StoredLength;

    /// <summary>Absolute offset of the frame's payload (after its 4 B length prefix); <c>0</c> for a zero page.</summary>
    public long DataOffset;

    /// <summary>CRC32C of the uncompressed 8 KiB image.</summary>
    public uint PageCrc;

    public uint Reserved;

    public PackPageEntry(uint pageId, uint storedLength, long dataOffset, uint pageCrc)
    {
        PageId = pageId;
        StoredLength = storedLength;
        DataOffset = dataOffset;
        PageCrc = pageCrc;
        Reserved = 0;
    }
}

/// <summary>One TAILDIR segment entry (32 B).</summary>
internal struct TailSegmentEntry
{
    public long SegmentId;
    public long FirstLsn;
    public int RawLength;
    public uint RawCrc;
    public int FirstBlock;
    public int BlockCount;

    public readonly void WriteTo(Span<byte> dst)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dst, SegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(dst[8..], FirstLsn);
        BinaryPrimitives.WriteInt32LittleEndian(dst[16..], RawLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[20..], RawCrc);
        BinaryPrimitives.WriteInt32LittleEndian(dst[24..], FirstBlock);
        BinaryPrimitives.WriteInt32LittleEndian(dst[28..], BlockCount);
    }

    public static TailSegmentEntry Read(ReadOnlySpan<byte> src) => new()
    {
        SegmentId = BinaryPrimitives.ReadInt64LittleEndian(src),
        FirstLsn = BinaryPrimitives.ReadInt64LittleEndian(src[8..]),
        RawLength = BinaryPrimitives.ReadInt32LittleEndian(src[16..]),
        RawCrc = BinaryPrimitives.ReadUInt32LittleEndian(src[20..]),
        FirstBlock = BinaryPrimitives.ReadInt32LittleEndian(src[24..]),
        BlockCount = BinaryPrimitives.ReadInt32LittleEndian(src[28..]),
    };
}

/// <summary>One TAILDIR block entry (16 B): a frame of up to 1 MiB raw WAL bytes.</summary>
internal struct TailBlockEntry
{
    /// <summary>Absolute offset of the frame's payload.</summary>
    public long DataOffset;
    public int RawLength;

    /// <summary>Equal to <see cref="RawLength"/> for a block stored raw, smaller for an LZ4 block.</summary>
    public int StoredLength;

    public readonly void WriteTo(Span<byte> dst)
    {
        BinaryPrimitives.WriteInt64LittleEndian(dst, DataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(dst[8..], RawLength);
        BinaryPrimitives.WriteInt32LittleEndian(dst[12..], StoredLength);
    }

    public static TailBlockEntry Read(ReadOnlySpan<byte> src) => new()
    {
        DataOffset = BinaryPrimitives.ReadInt64LittleEndian(src),
        RawLength = BinaryPrimitives.ReadInt32LittleEndian(src[8..]),
        StoredLength = BinaryPrimitives.ReadInt32LittleEndian(src[12..]),
    };
}

/// <summary>The identity a pack is written under, fixed before the freeze.</summary>
internal readonly record struct PackIdentity(Guid DatabaseId, Guid StoreId, Guid ChainId, uint BackupId, uint ParentId, uint BaseId, BackupKind Kind,
    Guid TimelineId, int DatabaseFormatRevision, string EngineVersion, bool Offline);
