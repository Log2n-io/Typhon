using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// WAL segment file header — 4096 bytes (one aligned page). Written once when segment is created; never modified after sealing.
/// </summary>
/// <remarks>
/// <para>
/// The header occupies the first 4096 bytes of every WAL segment file, ensuring the first WAL record starts at a 4096-byte aligned offset
/// (required for O_DIRECT / <c>FILE_FLAG_NO_BUFFERING</c>).
/// </para>
/// <para>
/// <see cref="HeaderCRC"/> is computed using <see cref="Crc32CUtil.ComputeSkipping"/> with the CRC field treated as zeros (self-referencing CRC pattern).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct WalSegmentHeader
{
    /// <summary>Magic number identifying a Typhon WAL file: "TYFW" (TYphon File Wal).</summary>
    public const uint MagicValue = 0x54594657;

    /// <summary>Current WAL format version.</summary>
    public const uint CurrentVersion = 1;

    /// <summary>Total size of this header struct in bytes (one page).</summary>
    public const int SizeInBytes = 4096;

    /// <summary>Byte offset of <see cref="HeaderCRC"/> within the struct, for <see cref="Crc32CUtil.ComputeSkipping"/>.</summary>
    public const int HeaderCrcOffset = 36; // 4 + 4 + 8 + 8 + 8 + 4 = 36

    /// <summary>File magic number. Must equal <see cref="MagicValue"/>.</summary>
    public uint Magic;

    /// <summary>WAL format version. Must equal <see cref="CurrentVersion"/>.</summary>
    public uint Version;

    /// <summary>Monotonically increasing segment identifier.</summary>
    public long SegmentId;

    /// <summary>LSN of the first record in this segment.</summary>
    public long FirstLSN;

    /// <summary>Last LSN of the previous segment (chain link for segment ordering).</summary>
    public long PrevSegmentLSN;

    /// <summary>Total file size in bytes (including header).</summary>
    public uint SegmentSize;

    /// <summary>CRC32C of this header with HeaderCRC treated as zeros.</summary>
    public uint HeaderCRC;

    /// <summary>Reserved space padding to 4096 bytes total.</summary>
    public fixed byte Reserved[4056];

    /// <summary>
    /// Initializes the header fields for a new segment.
    /// </summary>
    public void Initialize(long segmentId, long firstLsn, long prevSegmentLsn, uint segmentSize)
    {
        Magic = MagicValue;
        Version = CurrentVersion;
        SegmentId = segmentId;
        FirstLSN = firstLsn;
        PrevSegmentLSN = prevSegmentLsn;
        SegmentSize = segmentSize;
        HeaderCRC = 0;
    }

    /// <summary>
    /// Computes and stores the CRC32C for this header.
    /// </summary>
    public void ComputeAndSetCrc()
    {
        HeaderCRC = 0;
        fixed (WalSegmentHeader* self = &this)
        {
            var span = new ReadOnlySpan<byte>(self, SizeInBytes);
            HeaderCRC = Crc32CUtil.ComputeSkipping(span, HeaderCrcOffset, sizeof(uint));
        }
    }

    /// <summary>
    /// Validates the header magic, version, and CRC integrity.
    /// </summary>
    /// <returns>True if the header is valid.</returns>
    public bool Validate()
    {
        if (Magic != MagicValue)
        {
            return false;
        }

        if (Version != CurrentVersion)
        {
            return false;
        }

        var storedCrc = HeaderCRC;
        fixed (WalSegmentHeader* self = &this)
        {
            var span = new ReadOnlySpan<byte>(self, SizeInBytes);
            var computedCrc = Crc32CUtil.ComputeSkipping(span, HeaderCrcOffset, sizeof(uint));
            return computedCrc == storedCrc;
        }
    }
}
