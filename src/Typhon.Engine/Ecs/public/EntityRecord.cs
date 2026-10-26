using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Header of an entity record stored in the per-archetype LinearHash.
/// 14 bytes: BornTSN (6B) + DiedTSN (6B) + EnabledBits (2B).
/// Component locations (4B × N) follow immediately after in raw byte storage.
/// </summary>
/// <remarks>
/// TSN packing follows the same pattern as <c>CompRevStorageElement</c>: upper 32 bits + lower 16 bits = 48-bit TSN.
/// Non-aligned 6-byte fields are safe because the per-bucket OLC latch serializes writers and detects torn reads.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
[PublicAPI]
public struct EntityRecordHeader
{
    // BornTSN: 48-bit transaction sequence number when entity was created+committed
    // 0 = genesis entity (always visible)
    /// <summary>Upper 32 bits of the 48-bit BornTSN; combine with <see cref="BornTsnLow"/> via <see cref="BornTSN"/>.</summary>
    public uint BornTsnHigh;
    /// <summary>Lower 16 bits of the 48-bit BornTSN; combine with <see cref="BornTsnHigh"/> via <see cref="BornTSN"/>.</summary>
    public ushort BornTsnLow;

    // DiedTSN: 48-bit transaction sequence number when entity was destroyed+committed
    // 0 = alive
    /// <summary>Upper 32 bits of the 48-bit DiedTSN; combine with <see cref="DiedTsnLow"/> via <see cref="DiedTSN"/>. 0 = alive.</summary>
    public uint DiedTsnHigh;
    /// <summary>Lower 16 bits of the 48-bit DiedTSN; combine with <see cref="DiedTsnHigh"/> via <see cref="DiedTSN"/>. 0 = alive.</summary>
    public ushort DiedTsnLow;

    // Per-component enable mask: bit N set = component slot N is enabled
    /// <summary>Per-component enable mask: bit N set means component slot N is enabled.</summary>
    public ushort EnabledBits;

    /// <summary>48-bit BornTSN packed as upper 32 + lower 16.</summary>
    public long BornTSN
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => ((long)BornTsnHigh << 16) | BornTsnLow;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            // 48 bits: TransactionChain refuses transactions well before a TSN outgrows them (#1208).
            Debug.Assert((ulong)value < 1UL << 48, "a BornTSN outgrew its 48-bit storage");
            BornTsnHigh = (uint)(value >> 16);
            BornTsnLow = (ushort)(value & 0xFFFF);
        }
    }

    /// <summary>48-bit DiedTSN packed as upper 32 + lower 16.</summary>
    public long DiedTSN
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => ((long)DiedTsnHigh << 16) | DiedTsnLow;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            Debug.Assert((ulong)value < 1UL << 48, "a DiedTSN outgrew its 48-bit storage");
            DiedTsnHigh = (uint)(value >> 16);
            DiedTsnLow = (ushort)(value & 0xFFFF);
        }
    }

    /// <summary>
    /// True if the entity is published and its destroy is not (a HEAD reader's view). A spawn placed in PREPARE and not yet published (its born pending,
    /// VIS-08) is not alive: its commit may still abort. An entity whose destroy is pending (VIS-09) still is, for the same reason.
    /// </summary>
    public readonly bool IsAlive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (BornTsnHigh & PendingHighBit) == 0 && ((DiedTsnHigh == 0 && DiedTsnLow == 0) || (DiedTsnHigh & PendingHighBit) != 0);
    }

    /// <summary>Bit 47 of a 48-bit stamp field: the value is an owner's TSN, not a commit stamp (VIS-08). TSNs stay below it (#1208).</summary>
    internal const long PendingBit = 1L << 47;

    private const uint PendingHighBit = 1u << 31;   // bit 47 of the 48-bit value, in its upper 32 bits

    /// <summary>The born value of a spawn placed in PREPARE by <paramref name="ownerTsn"/>: above every snapshot, so a test that ignores it reads "not born".</summary>
    internal static long PendingBorn(long ownerTsn) => PendingBit | ownerTsn;

    /// <summary>True while the spawn's commit has not published: <see cref="BornTSN"/> holds its owner's TSN.</summary>
    internal readonly bool IsBornPending
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (BornTsnHigh & PendingHighBit) != 0;
    }

    /// <summary>True while a destroy's commit has not published: <see cref="DiedTSN"/> holds its owner's TSN (VIS-09).</summary>
    internal readonly bool IsDiedPending
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (DiedTsnHigh & PendingHighBit) != 0;
    }

    /// <summary>Either stamp field holds an owner: the record needs the owner's card to be read at a snapshot.</summary>
    internal readonly bool HasPending
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ((BornTsnHigh | DiedTsnHigh) & PendingHighBit) != 0;
    }

    /// <summary>
    /// <see cref="IsVisibleAt(long)"/> with a pending born resolved through its owner's commit card (VIS-01, VIS-08): visible when the owner is registered
    /// with a stamp at or below <paramref name="txTsn"/>, or is <paramref name="txTsn"/> itself; a pending died the same way (VIS-09), the entity dead from its
    /// owner's stamp on. With no card holding the owner the answer is in the live record, not this copy — it was published since, or its commit aborted:
    /// <paramref name="reread"/> is set, and the caller reads the record again and asks once more; a value still pending then is not committed.
    /// </summary>
    internal readonly bool IsVisibleAt(long txTsn, CommitCardTable cards, out bool reread)
    {
        reread = false;
        if (IsBornPending)
        {
            var owner = BornTSN & ~PendingBit;
            if (owner != txTsn)
            {
                if (cards == null || !cards.TryGetStamp(owner, out var stamp))
                {
                    reread = true;
                    return false;
                }

                if (stamp > txTsn)
                {
                    return false;
                }
            }
        }
        else
        {
            var born = BornTSN;
            if (born != 0 && born > txTsn)
            {
                return false;
            }
        }

        if (IsDiedPending)
        {
            // Dead at txTsn once its owner's stamp is at or below it; an owner with no card is not committed — unless the live record says otherwise.
            var owner = DiedTSN & ~PendingBit;
            if (owner == txTsn)
            {
                return false;
            }

            if (cards == null || !cards.TryGetStamp(owner, out var diedStamp))
            {
                reread = true;
                return true;
            }

            return diedStamp > txTsn;
        }

        var died = DiedTSN;
        return died == 0 || died > txTsn;
    }

    /// <summary>
    /// The died half of <see cref="IsVisibleAt(long, CommitCardTable, out bool)"/> alone: whether a destroy has removed this entity for a reader at
    /// <paramref name="txTsn"/> — a published died at or below it, or a pending one whose owner is <paramref name="txTsn"/> or holds a card stamped at or
    /// below it. The born side is not asked: a cluster walk judges only destroys against its snapshot (CLUSTERWALK-04), its spawns are committed HEAD.
    /// <paramref name="reread"/> as there: no card for the owner, the live record has the answer.
    /// </summary>
    internal readonly bool IsDeadAt(long txTsn, CommitCardTable cards, out bool reread)
    {
        reread = false;
        if (IsDiedPending)
        {
            var owner = DiedTSN & ~PendingBit;
            if (owner == txTsn)
            {
                return true;
            }

            if (cards == null || !cards.TryGetStamp(owner, out var diedStamp))
            {
                reread = true;
                return false;
            }

            return diedStamp <= txTsn;
        }

        var died = DiedTSN;
        return died != 0 && died <= txTsn;
    }

    /// <summary>
    /// Check whether this entity is visible to a transaction with the given TSN.
    /// </summary>
    /// <remarks>
    /// Visibility rules (snapshot isolation):
    /// <list type="bullet">
    /// <item>BornTSN == 0: genesis entity, always visible</item>
    /// <item>BornTSN != 0 and BornTSN > txTsn: not born yet → invisible</item>
    /// <item>DiedTSN == 0: alive → visible (if born)</item>
    /// <item>DiedTSN != 0 and DiedTSN &lt;= txTsn: dead at this snapshot → invisible</item>
    /// </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsVisibleAt(long txTsn)
    {
        var born = BornTSN;
        if (born != 0 && born > txTsn)
        {
            return false;
        }

        var died = DiedTSN;
        if (died != 0 && died <= txTsn)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Static accessor for entity records stored as raw bytes in the per-archetype RawValueHashMap.
/// Record layout: [EntityRecordHeader (14B)] [Location₀ (4B)] [Location₁ (4B)] ... [Location_{N-1} (4B)]
/// </summary>
[PublicAPI]
public static unsafe class EntityRecordAccessor
{
    /// <summary>Size of the <see cref="EntityRecordHeader"/> in bytes.</summary>
    public const int HeaderSize = 14;

    /// <summary>Maximum component count per archetype (16-bit EnabledBits).</summary>
    public const int MaxComponentCount = 16;

    /// <summary>Maximum entity record size in bytes: HeaderSize + MaxComponentCount × 4 = 78.</summary>
    public const int MaxRecordSize = HeaderSize + MaxComponentCount * sizeof(int);

    /// <summary>Total entity record size for an archetype with <paramref name="componentCount"/> components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RecordSize(int componentCount) => HeaderSize + componentCount * sizeof(int);

    /// <summary>Get a reference to the header at the start of the record.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref EntityRecordHeader GetHeader(byte* record) => ref Unsafe.AsRef<EntityRecordHeader>(record);

    /// <summary>Read the component chunk ID at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLocation(byte* record, int slot) => *(int*)(record + HeaderSize + slot * sizeof(int));

    /// <summary>Write the component chunk ID at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLocation(byte* record, int slot, int chunkId) => *(int*)(record + HeaderSize + slot * sizeof(int)) = chunkId;

    /// <summary>Bulk copy all component locations from one record to another.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyLocations(byte* src, byte* dst, int componentCount) => 
        Unsafe.CopyBlock(dst + HeaderSize, src + HeaderSize, (uint)(componentCount * sizeof(int)));

    /// <summary>Zero-initialize an entire entity record (header + locations).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitializeRecord(byte* record, int componentCount) => Unsafe.InitBlock(record, 0, (uint)RecordSize(componentCount));

    /// <summary>Copy component locations from a raw record into an <see cref="EntityLocations"/> struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CopyLocationsTo(byte* record, ref EntityLocations locs, int componentCount) =>
        // A span over locs, not a pinned pointer: locs can live inside a class. The record itself is page memory.
        new System.ReadOnlySpan<int>(record + HeaderSize, componentCount).CopyTo(MemoryMarshal.CreateSpan(ref locs.Values[0], componentCount));
}

/// <summary>
/// Inline storage for up to 16 component location ChunkIds. Value type — stored contiguously in List backing arrays,
/// eliminating per-entity heap allocations during query enumeration.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EntityLocations
{
    public fixed int Values[EntityRecordAccessor.MaxComponentCount];
}

/// <summary>
/// Static accessor for cluster entity records stored as raw bytes in the per-archetype RawValueHashMap.
/// </summary>
/// <remarks>
/// <para>Base record layout: [EntityRecordHeader (14B)] [ClusterChunkId (4B)] [SlotIndex (1B)] = 19 bytes.</para>
/// <para>For archetypes with Versioned components, the record extends with per-Versioned-slot compRevFirstChunkId (4B each):
/// [Base (19B)] [CompRevFirstChunkId₀ (4B)] ... [CompRevFirstChunkId_{V-1} (4B)]</para>
/// <para>Total: 19 + 4 * versionedSlotCount bytes.</para>
/// <para>The EntityRecordHeader is identical to the legacy format (same BornTSN/DiedTSN/EnabledBits).</para>
/// </remarks>
[PublicAPI]
public static unsafe class ClusterEntityRecordAccessor
{
    /// <summary>Size of the <see cref="EntityRecordHeader"/> in bytes (same as legacy).</summary>
    public const int HeaderSize = 14;

    /// <summary>Base cluster entity record size (no Versioned slots): Header(14) + ClusterChunkId(4) + SlotIndex(1) = 19.</summary>
    public const int BaseRecordSize = 19;

    /// <summary>Byte offset of ClusterChunkId within the record.</summary>
    public const int ClusterChunkIdOffset = HeaderSize;

    /// <summary>Byte offset of SlotIndex within the record.</summary>
    public const int SlotIndexOffset = HeaderSize + sizeof(int);

    /// <summary>Byte offset where compRevFirstChunkId array starts (immediately after SlotIndex).</summary>
    public const int CompRevOffset = BaseRecordSize;

    /// <summary>Maximum cluster entity record size in bytes: BaseRecordSize(19) + 16 Versioned slots × 4 = 83.</summary>
    /// <remarks>
    /// Larger than <see cref="EntityRecordAccessor.MaxRecordSize"/> (78), because a cluster record carries a 5-byte cluster locator the legacy record does
    /// not. A buffer that must hold EITHER shape has to be sized on this constant; sizing it on the legacy one overflows by 5 bytes for an archetype whose
    /// every component is Versioned.
    /// </remarks>
    public const int MaxRecordSize = BaseRecordSize + EntityRecordAccessor.MaxComponentCount * sizeof(int);

    /// <summary>Compute the total record size for a cluster archetype with <paramref name="versionedSlotCount"/> Versioned components.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RecordSize(int versionedSlotCount) => BaseRecordSize + versionedSlotCount * sizeof(int);

    /// <summary>Get a reference to the header at the start of the record.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref EntityRecordHeader GetHeader(byte* record) => ref Unsafe.AsRef<EntityRecordHeader>(record);

    /// <summary>Read the cluster chunk ID (the cluster's chunkId in the ClusterSegment).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetClusterChunkId(byte* record) => *(int*)(record + ClusterChunkIdOffset);

    /// <summary>Write the cluster chunk ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetClusterChunkId(byte* record, int chunkId) => *(int*)(record + ClusterChunkIdOffset) = chunkId;

    /// <summary>Read the slot index within the cluster (0..63).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetSlotIndex(byte* record) => *(record + SlotIndexOffset);

    /// <summary>Write the slot index within the cluster.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetSlotIndex(byte* record, byte slotIndex) => *(record + SlotIndexOffset) = slotIndex;

    /// <summary>Read the compRevFirstChunkId for the given versioned index (0-based within Versioned slots only).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetCompRevFirstChunkId(byte* record, int versionedIndex) => *(int*)(record + CompRevOffset + versionedIndex * sizeof(int));

    /// <summary>Write the compRevFirstChunkId for the given versioned index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCompRevFirstChunkId(byte* record, int versionedIndex, int chunkId) =>
        *(int*)(record + CompRevOffset + versionedIndex * sizeof(int)) = chunkId;

    /// <summary>Zero-initialize an entire cluster entity record (base + versioned extensions).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitializeRecord(byte* record, int versionedSlotCount) =>
        Unsafe.InitBlock(record, 0, (uint)RecordSize(versionedSlotCount));

}

/// <summary>
/// Packed ClusterLocation encoding for B+Tree values and spatial entries.
/// Uses a fixed multiplier of 64 (max cluster size) for uniform encoding across all archetypes.
/// </summary>
[PublicAPI]
public static class ClusterLocation
{
    /// <summary>Maximum cluster size. Used as multiplier for packed encoding.</summary>
    public const int MaxClusterSize = 64;

    /// <summary>Pack a cluster chunk ID and slot index into a single int.</summary>
    /// <remarks>Max capacity: int.MaxValue / 64 = 33.5M clusters.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Pack(int clusterChunkId, int slotIndex) => clusterChunkId * MaxClusterSize + slotIndex;

    /// <summary>Unpack a packed ClusterLocation into (clusterChunkId, slotIndex).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int clusterChunkId, int slotIndex) Unpack(int packed) => (packed >> 6, packed & 0x3F);
}
