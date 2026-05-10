using JetBrains.Annotations;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Chunk type discriminator for the generic WAL chunk envelope.
/// </summary>
[PublicAPI]
internal enum WalChunkType : ushort
{
    /// <summary>Transaction record: component Create/Update/Delete.</summary>
    Transaction = 1,

    /// <summary>Full-Page Image for torn-page repair.</summary>
    FullPageImage = 2,

    /// <summary>Tick fence: snapshot of dirty SingleVersion component data at tick boundary.</summary>
    TickFence = 3,

    /// <summary>Cluster tick fence: snapshot of dirty cluster-backed entity data at tick boundary. Per-archetype, all components per entry.</summary>
    ClusterTickFence = 4,
}

/// <summary>
/// 8-byte generic chunk header written before every WAL chunk (transaction record or FPI).
/// </summary>
/// <remarks>
/// <para>
/// Producers write <see cref="PrevCRC"/> = 0 and the footer CRC = 0 as placeholders. The single-threaded WAL writer (<see cref="WalWriter"/>) patches both
/// fields after staging buffer copy and before disk write — this centralizes CRC chain management and eliminates the PrevCRC chain break that occurred when
/// FPI records interleaved with transaction records.
/// </para>
/// <para>
/// <see cref="ChunkSize"/> enables forward-compatible skipping of unknown chunk types.
/// CRC covers bytes [0, ChunkSize - 4) — the entire header (including PrevCRC) + body.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct WalChunkHeader
{
    /// <summary>Chunk type discriminator.</summary>
    public ushort ChunkType;

    /// <summary>Total chunk size in bytes: header (8) + body + footer (4).</summary>
    public ushort ChunkSize;

    /// <summary>Footer CRC of the previous chunk. Set by WAL writer; producers write 0.</summary>
    public uint PrevCRC;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 8;
}

/// <summary>
/// 4-byte chunk footer written after every WAL chunk body.
/// </summary>
/// <remarks>
/// CRC32C is computed over [0, ChunkSize - 4) — the chunk header + body, excluding this footer.
/// Set by the WAL writer thread; producers write 0 as a placeholder.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct WalChunkFooter
{
    /// <summary>CRC32C over the chunk header + body (excludes this footer).</summary>
    public uint CRC;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 4;
}

/// <summary>
/// 24-byte header for a TickFence WAL chunk. One chunk per SingleVersion ComponentTable per tick.
/// Followed by <see cref="EntryCount"/> entries of (ChunkId:4B + ComponentData:PayloadStride bytes).
/// </summary>
/// <remarks>
/// <para>ChunkId (not EntityPK) is stored per entry: SV uses PersistentStore with stable file positions, so recovery can write directly to chunks without
/// PK lookup.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct TickFenceHeader
{
    /// <summary>Monotonic tick number identifying this tick boundary.</summary>
    public long TickNumber;

    /// <summary>WAL log sequence number assigned to this chunk.</summary>
    public long LSN;

    /// <summary>Identifies the ComponentTable via <see cref="ComponentTable.WalTypeId"/>.</summary>
    public ushort ComponentTypeId;

    /// <summary>Number of dirty entity entries in this chunk.</summary>
    public ushort EntryCount;

    /// <summary>Component data size per entry (ComponentStorageSize).</summary>
    public ushort PayloadStride;

    /// <summary>Reserved for future use.</summary>
    public ushort Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 24;
}

/// <summary>
/// 24-byte header for a ClusterTickFence WAL chunk. One chunk per cluster-eligible archetype per tick.
/// Followed by <see cref="EntryCount"/> entries of (EntityIndex:4B + AllComponentData:PerEntityPayload bytes).
/// </summary>
/// <remarks>
/// <para>EntityIndex = clusterChunkId * 64 + slotIndex. Recovery unpacks to (chunkId &gt;&gt; 6, index &amp; 0x3F) and writes each component
/// to the correct SoA offset in the cluster.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PublicAPI]
internal struct ClusterTickFenceHeader
{
    /// <summary>Monotonic tick number identifying this tick boundary.</summary>
    public long TickNumber;

    /// <summary>WAL log sequence number assigned to this chunk.</summary>
    public long LSN;

    /// <summary>Identifies the archetype via <see cref="ArchetypeMetadata.ArchetypeId"/>.</summary>
    public ushort ArchetypeId;

    /// <summary>Number of dirty entity entries in this chunk.</summary>
    public ushort EntryCount;

    /// <summary>Sum of all component sizes per entity (fixed per archetype).</summary>
    public ushort PerEntityPayload;

    /// <summary>Number of components per entity.</summary>
    public byte ComponentCount;

    /// <summary>Reserved for future use.</summary>
    public byte Reserved;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 24;
}
