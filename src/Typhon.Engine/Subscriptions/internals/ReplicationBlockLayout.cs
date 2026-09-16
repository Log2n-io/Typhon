using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Header of one replication state block — the block that describes a single cluster holding at least one watched entity. Padded to a full cache line so
/// the first hot entry is line-aligned.
/// </summary>
/// <remarks>
/// <see cref="NextFree"/> is meaningful only while the block sits on the pool's intrusive free list; a live block's link is not maintained.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct ReplicationBlockHeader
{
    /// <summary>Bit <c>i</c> is set when slot <c>i</c> of this cluster is watched by at least one session this tick.</summary>
    public ulong WatchedMask;

    /// <summary>Tick at which this block last had a watched slot; drives eviction of blocks idle for ~50 ticks.</summary>
    public uint LastWatchedTick;

    /// <summary>The cluster chunk id this block describes. Checked against the directory key to catch a recycled id.</summary>
    public int ChunkId;

    /// <summary>Intrusive free-list link, used only while this block is free.</summary>
    public nint NextFree;

    /// <summary>
    /// Whether the pool considers this block rented or free. Owned by <see cref="ReplicationBlockPool"/> alone.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT folded into <see cref="ChunkId"/>. That field belongs to the directory, which resets it when an entry is removed; when the pool's
    /// free-state also lived there, a directory removal silently cleared the pool's only double-return guard, and a block could then be returned twice into a
    /// self-linked free list. Two owners, two fields.
    /// </remarks>
    public byte PoolState;
}

/// <summary>
/// Per-entity replication state read on every hit by the per-session passes. Exactly one cache line, which is the binding constraint of AC-5 — the passes
/// must never pull cold data into L1.
/// </summary>
/// <remarks>
/// <see cref="MotionSegment"/> and <see cref="PackedState"/> are reserved byte regions whose interior encoding is defined by the projection pass, not here.
/// Their <i>sizes</i> are the contract this type fixes: the motion segment carries a pre-encoded <c>p0</c> (u24 × 2), <c>v</c> (i16 × 2), <c>t0</c> (u16)
/// and an epoch byte; the packed state carries the archetype's declared state fields.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal unsafe struct ReplicationHotEntry
{
    /// <summary>The entity this slot describes. Checked against the hit and against the slot — this is what makes slot reuse safe (§ 4 R1).</summary>
    public EntityId Entity;

    /// <summary>Network identity handed to sessions. Released when the slot stops being occupied.</summary>
    public uint NetId;

    /// <summary>Bumped when a netId is reused, so every session observes a reused id as leave + enter (SUB-06).</summary>
    public ushort Generation;

    /// <summary>Replication flags for this entry.</summary>
    public ushort Flags;

    /// <summary>One change tick per field group (at most four); the motion group's entry is its segment tick.</summary>
    public fixed uint GroupTicks[4];

    /// <summary>Pre-encoded motion segment. Encoding owned by the projection pass; see the type remarks.</summary>
    public fixed byte MotionSegment[14];

    /// <summary>Packed state fields. Encoding owned by the projection pass; see the type remarks.</summary>
    public fixed byte PackedState[16];
}

/// <summary>
/// Per-entity replication state read only by the projection pass, never on the per-hit path. Half a cache line, per AC-5.
/// </summary>
/// <remarks>
/// As with <see cref="ReplicationHotEntry"/>, the byte regions here fix sizes rather than interior encodings: <see cref="PrevQuantizedPosition"/> holds the
/// previous quantized position and <see cref="RunStart"/> the run start pair <c>(p_s, t_s)</c>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal unsafe struct ReplicationColdEntry
{
    /// <summary>Previous quantized position. Encoding owned by the projection pass.</summary>
    public fixed byte PrevQuantizedPosition[6];

    /// <summary>Run start <c>(p_s, t_s)</c>. Encoding owned by the projection pass.</summary>
    public fixed byte RunStart[12];

    /// <summary>Tick at which this entry was last watched.</summary>
    public uint LastWatchedTick;
}

/// <summary>
/// Byte layout of one replication state block: a <see cref="ReplicationBlockHeader"/>, then <c>hot[N]</c>, then <c>cold[N]</c>, then <c>owner[N]</c> when
/// the archetype declares owner fields.
/// </summary>
/// <remarks>
/// <para>
/// The layout is <b>SoA within the block</b> — all hot entries, then all cold — so a per-session pass walking hits touches only hot lines. An AoS layout
/// of <c>(hot, cold)</c> pairs would pull 96 B into L1 for every 64 B actually read, which is the whole reason this type exists.
/// </para>
/// <para>
/// Block size is fixed per archetype, because <c>N</c> and the declared groups are fixed at <c>Start</c>. That is what lets the pool keep one free list of
/// identical blocks with no size classes and no fragmentation.
/// </para>
/// </remarks>
internal readonly struct ReplicationBlockLayout
{
    /// <summary>Size of the block header, and therefore the offset of the first hot entry. One cache line.</summary>
    public const int HeaderSize = 64;

    /// <summary>Size of one hot entry. Fixed by AC-5 at one cache line.</summary>
    public const int HotEntrySize = 64;

    /// <summary>Size of one cold entry. Fixed by AC-5 at half a cache line.</summary>
    public const int ColdEntrySize = 32;

    /// <summary>Creates the layout for an archetype whose clusters hold <paramref name="slotCount"/> entities.</summary>
    /// <param name="slotCount">The archetype's cluster slot count, <c>N</c>.</param>
    /// <param name="ownerEntrySize">Bytes per owner entry, or <c>0</c> when the archetype declares no owner fields.</param>
    public ReplicationBlockLayout(int slotCount, int ownerEntrySize = 0)
    {
        // A zero or negative slot count yields a block with no entries, which the pool would happily carve and hand out forever; a negative owner entry size
        // shrinks the block below its own regions. Both are construction-time mistakes, so they fail here rather than as arithmetic nonsense later.
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "A cluster holds at least one slot");
        }

        if (ownerEntrySize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerEntrySize), ownerEntrySize, "Owner entry size cannot be negative; use 0 for no owner fields");
        }

        SlotCount = slotCount;
        OwnerEntrySize = ownerEntrySize;

        // Computed once. These are fixed for the archetype's lifetime and are read on paths that become per-cluster and then per-hit, so recomputing a
        // multiply-and-add on every access is work with a known answer.
        HotOffset = HeaderSize;
        ColdOffset = HotOffset + (slotCount * HotEntrySize);
        OwnerOffset = ColdOffset + (slotCount * ColdEntrySize);
        BlockSize = OwnerOffset + (slotCount * ownerEntrySize);
        BlockStride = (BlockSize + 63) & ~63;
    }

    /// <summary>The archetype's cluster slot count, <c>N</c>.</summary>
    public int SlotCount { get; }

    /// <summary>Bytes per owner entry; <c>0</c> when the archetype declares no owner fields.</summary>
    public int OwnerEntrySize { get; }

    /// <summary>Byte offset of <c>hot[0]</c> from the start of the block.</summary>
    public int HotOffset { get; }

    /// <summary>Byte offset of <c>cold[0]</c> from the start of the block.</summary>
    public int ColdOffset { get; }

    /// <summary>Byte offset of <c>owner[0]</c> from the start of the block. Equal to <see cref="BlockSize"/> when no owner entries are declared.</summary>
    public int OwnerOffset { get; }

    /// <summary>Total bytes in one block.</summary>
    public int BlockSize { get; }

    /// <summary>
    /// Distance between consecutive blocks packed in a slab: <see cref="BlockSize"/> rounded up to a cache line.
    /// </summary>
    /// <remarks>
    /// <see cref="BlockSize"/> is not itself a multiple of 64 — at <c>N = 21</c> it is 2 080, which is 64-aligned only to 32 B. Packing blocks at the raw
    /// size would therefore put every block after the first on a 32 B boundary and misalign its <c>hot[0]</c>, defeating the header padding that exists for
    /// exactly that reason. The stride costs at most 63 B per block (1.5 % at <c>N = 21</c>) and buys a line-aligned hot region in every block.
    /// </remarks>
    public int BlockStride { get; }
}
