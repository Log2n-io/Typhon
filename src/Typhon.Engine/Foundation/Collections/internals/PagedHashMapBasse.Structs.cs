// unset

using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Meta chunk (chunk 0) for a linear hash map. Stores immutable N0, the atomic packed meta (Level:8|Next:24|BucketCount:32), entry count,
/// and inline directory chunk IDs.
/// <para>
/// The first 57 directory chunk IDs are stored inline, covering 3,648 buckets (57 × 64).
/// Beyond that, overflow dir-index chunks form a singly-linked list from <see cref="OverflowDirIndexChunkId"/>.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
struct PagedHashMapMeta
{
    public const int MaxInlineDirectoryChunks = 57;

    /// <summary>Initial bucket count (power of 2, immutable after creation).</summary>
    public int N0;

    /// <summary>First overflow dir-index chunk ID, or -1 if no overflow.</summary>
    public int OverflowDirIndexChunkId;

    /// <summary>Atomic packed meta: Level(8 bits) | Next(24 bits) | BucketCount(32 bits).</summary>
    public long PackedMeta;

    /// <summary>Total entry count across all buckets. Updated via <see cref="System.Threading.Interlocked"/>.</summary>
    public long EntryCount;

    /// <summary>Total directory chunk count (inline + overflow).</summary>
    public ushort DirectoryChunkCount;

    /// <summary>Bit 0: AllowMultiple (multi-value keys via VSBS buffer indirection).</summary>
    public byte Flags;

    public byte Reserved;

    /// <summary>Inline directory chunk IDs. Covers up to 57 × 64 = 3,648 buckets.</summary>
    public unsafe fixed int DirectoryChunkIds[MaxInlineDirectoryChunks]; // 57 × 4 = 228 bytes
    // Total: 4 + 4 + 8 + 8 + 2 + 1 + 1 + 228 = 256 bytes
}

/// <summary>
/// Directory chunk — a flat array of 64 bucket chunk IDs. Headerless; role is determined by position in the meta's <see cref="PagedHashMapMeta.DirectoryChunkIds"/> array.
/// <para>Power-of-2 entries per chunk enables shift+mask arithmetic for bucket addressing.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 256)]
struct PagedHashMapDirectory
{
    public const int EntriesPerChunk = 64; // power of 2
    public const int Shift = 6; // log2(64)

    public unsafe fixed int BucketChunkIds[EntriesPerChunk]; // 64 × 4 = 256 bytes
}

/// <summary>
/// Overflow dir-index chunk — stores additional directory chunk IDs when the hash map exceeds 3,648 buckets. Forms a singly-linked list
/// via <see cref="NextOverflowChunkId"/>.
/// <para>Each overflow chunk holds 63 directory chunk IDs, covering 63 × 64 = 4,032 additional buckets.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
struct OverflowDirIndex
{
    public const int EntriesPerChunk = 63;

    /// <summary>Next overflow dir-index chunk ID, or -1 for end of chain.</summary>
    public int NextOverflowChunkId;

    public unsafe fixed int DirectoryChunkIds[EntriesPerChunk]; // 63 × 4 = 252 bytes
    // Total: 4 + 252 = 256 bytes
}

/// <summary>
/// Bucket header — shared across all linear hash map instantiations regardless of TKey/TValue.
/// Stored at offset 0 of every bucket chunk. The SoA data region (keys then values) follows immediately.
/// <para>OlcVersion at offset 0 enables per-bucket optimistic lock coupling.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
struct PagedHashMapBucketHeader
{
    /// <summary>OLC latch: bit0=locked, bit1=obsolete, bits2-31=version.</summary>
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
