// unset

using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Meta chunk (chunk 0) of a linear hash map: the immutable N0, the format, the hash state (the bucket count alone — level and split pointer derive from
/// it), and the entry count.
/// <para>
/// There is no directory. Bucket <c>b</c> lives at chunk <c>b + 1</c> of the map's segment, so a bucket is found by arithmetic (#1205). The format before
/// that kept a directory of bucket chunk ids — 57 inline, the rest in a linked list of chunks walked on every lookup — counted in 16 bits, which capped a
/// map at 4 194 240 buckets and made every lookup O(buckets).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
struct PagedHashMapMeta
{
    /// <summary>"LHA2": linear hashing with arithmetic bucket addressing. Any other value is a map written before #1205, or a damaged meta.</summary>
    public const uint FormatMagic = 0x3241484C;

    /// <summary>The most buckets a map holds (see <see cref="PagedHashMapBase{TStore}.MaxBucketCount"/>).</summary>
    internal const int MaxBucketCount = 1 << 30;

    /// <summary>Bit 0 of <see cref="Flags"/>: multi-value keys. No other bit is defined.</summary>
    internal const byte AllowMultipleFlag = 1;

    /// <summary>
    /// Whether <paramref name="meta"/> can describe a map of <paramref name="expectedN0"/> initial buckets over a segment of
    /// <paramref name="chunkCapacity"/> chunks: this format, that N0, a bucket count in [N0, <see cref="MaxBucketCount"/>] whose chunks
    /// <c>1 … BucketCount</c> lie in the segment, a non-negative entry count, no undefined flag, and the multi-value flag
    /// <paramref name="allowMultiple"/>. One definition for the open and the integrity check, so they cannot disagree on which
    /// metas a map can be opened from (#1205).
    /// </summary>
    internal static bool IsUsable(in PagedHashMapMeta meta, int expectedN0, long chunkCapacity, bool allowMultiple, out string reason)
    {
        reason = meta.Format != FormatMagic ? $"format 0x{meta.Format:X8}, expected 0x{FormatMagic:X8}"
            : meta.N0 != expectedN0 ? $"N0 {meta.N0}, expected {expectedN0}"
            : meta.BucketCount < meta.N0 || meta.BucketCount > MaxBucketCount ? $"bucket count {meta.BucketCount} outside [{meta.N0}, {MaxBucketCount}]"
            : meta.BucketCount >= chunkCapacity ? $"bucket count {meta.BucketCount} past the segment's {chunkCapacity} chunks"
            : meta.EntryCount < 0 ? $"entry count {meta.EntryCount}"
            : (meta.Flags & ~AllowMultipleFlag) != 0 ? $"undefined flags 0x{meta.Flags:X2}"
            : ((meta.Flags & AllowMultipleFlag) != 0) != allowMultiple ? $"multi-value flag {(meta.Flags & AllowMultipleFlag) != 0}, expected {allowMultiple}"
            : null;
        return reason == null;
    }

    /// <summary>Initial bucket count (power of 2, immutable after creation).</summary>
    public int N0;

    /// <summary><see cref="FormatMagic"/>.</summary>
    public uint Format;

    /// <summary>The hash state: buckets <c>0 … BucketCount − 1</c> exist, at chunks <c>1 … BucketCount</c>.</summary>
    public long BucketCount;

    /// <summary>Total entry count across all buckets. Updated via <see cref="System.Threading.Interlocked"/>.</summary>
    public long EntryCount;

    /// <summary>Bit 0: AllowMultiple (multi-value keys via VSBS buffer indirection).</summary>
    public byte Flags;
}

/// <summary>
/// Bucket header — shared across all linear hash map instantiations regardless of TKey/TValue.
/// Stored at offset 0 of every bucket chunk. The SoA data region (keys then values) follows immediately.
/// <para>OlcVersion at offset 0 enables per-bucket optimistic lock coupling.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
struct PagedHashMapBucketHeader
{
    /// <summary>
    /// On a bucket's primary chunk, the OLC latch: bit0=locked, bit1=obsolete, bits2-31=version. On an overflow chunk, which is never latched (the primary's
    /// version covers the whole chain), the <b>owning bucket + 1</b>, or 0 while the chunk is linked to no chain: what a split needs to move an overflow chunk
    /// out of the chunk the next bucket takes (<see cref="PagedHashMapBase{TStore}.TagOverflowOwner"/>).
    /// </summary>
    public int OlcVersion;

    /// <summary>Number of live entries in this bucket chunk.</summary>
    public byte EntryCount;

    public byte Flags;
    public short Reserved;

    /// <summary>Overflow chunk ID, or -1 if no overflow.</summary>
    public int OverflowChunkId;
}

/// <summary>
/// Diagnostic statistics for a linear hash map.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PagedHashMapStats
{
    public int BucketCount;
    public long EntryCount;

    /// <summary>Primary buckets with OverflowChunkId != -1.</summary>
    public int OverflowBucketCount;

    /// <summary>Longest chain (1 = primary only, 2+ = has overflow).</summary>
    public int MaxChainLength;

    public double LoadFactor;

    /// <summary>Bucket fill distribution: empty buckets.</summary>
    public int FillEmpty;

    /// <summary>Bucket fill distribution: 1-25% full.</summary>
    public int FillQuarter;

    /// <summary>Bucket fill distribution: 26-50% full.</summary>
    public int FillHalf;

    /// <summary>Bucket fill distribution: 51-75% full.</summary>
    public int FillThreeQuarter;

    /// <summary>Bucket fill distribution: 76-100% full.</summary>
    public int FillFull;
}
