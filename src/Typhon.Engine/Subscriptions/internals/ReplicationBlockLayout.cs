using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Header of one replication state block — the block that describes one cluster of an observed archetype. Padded to a full cache line so
/// the first hot entry is line-aligned.
/// </summary>
/// <remarks>
/// <see cref="NextFree"/> is meaningful only while the block sits on the pool's intrusive free list; a live block's link is not maintained.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct ReplicationBlockHeader
{
    /// <summary>Bit <c>i</c> is set when slot <c>i</c> of this cluster is projected this tick: the push set.</summary>
    public ulong WatchedMask;

    /// <summary>The tick this block was last marked: the claim stamp that lists it once per tick in <see cref="WatchedBlockList"/>.</summary>
    public uint LastWatchedTick;

    /// <summary>The cluster chunk id this block describes. Checked against the directory key to catch a recycled id.</summary>
    public int ChunkId;

    /// <summary>Intrusive free-list link, used only while this block is free.</summary>
    public nint NextFree;

    /// <summary>The watched mask as it stood the last time this block was actually projected.</summary>
    /// <remarks>
    /// <b>This is what makes the dormant-cluster skip safe.</b> A sleeping cluster's bytes cannot have changed, but a slot pushed for the first time still
    /// needs its IDENTITY, and identities are minted by the projection pass. Skipping a dormant block whose marked set had grown would leave the new slot
    /// without a netId for as long as the cluster slept. Comparing against this mask costs one AND and one branch.
    /// </remarks>
    public ulong ProjectedWatchedMask;

    /// <summary>The cluster's occupancy word as it stood the last time this block was projected.</summary>
    /// <remarks>
    /// <b>The second half of what makes the dormant-cluster skip safe, and the half that is about DESTROY.</b> A slot that stops being occupied is
    /// detected nowhere else — <c>ProjectBlock</c>'s own remark says "there is no destroy hook anywhere, and a slot that stopped being occupied is
    /// detected by this AND" — and no destroy path raises a dirty bit, so a destroy inside a sleeping cluster would never wake it. The identity would
    /// never be released, the block would go on describing a dead entity, and a respawn into that slot would be served to clients as the OLD entity under
    /// the OLD netId. Comparing the occupancy
    /// word costs one load the skip has to do anyway.
    /// </remarks>
    public ulong ProjectedOccupancy;

    /// <summary>
    /// Bit <c>i</c> is set when an entity ARRIVED in slot <c>i</c> from somewhere else since this block was last projected — a migration carried in, or a
    /// parked entry drained into it. Read and cleared by the projection, which flags the slot's push event as an arrival.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It closes the one way an entity can move without anything saying so.</b> A migration copies the entry verbatim: the identity, the group stamps
    /// and the quantized state all arrive unchanged, so if the entity's projected bytes did not change on that tick its event would say nothing — and the
    /// entity's latest event would go on naming the slot it left. Flagged as an arrival, the event is never a no-op.
    /// </para>
    /// <para>
    /// <b>Set with <see cref="System.Threading.Interlocked"/> and read with an exchange</b>, because migrations run in the fence's parallel slices while
    /// nothing holds this block, and the projection that consumes it runs on one worker per block. A plain OR would drop an arrival between the read and
    /// the write.
    /// </para>
    /// </remarks>
    public ulong ArrivedSlots;

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
/// Per-entity replication state read on every hit by the per-session passes, in its <b>baseline</b> shape: a 2D archetype whose motion segment fits 14 B and
/// whose state body fits 16 B. One cache line, which is what AC-5 asks of such an archetype.
/// </summary>
/// <remarks>
/// <para>
/// <b>This struct is the baseline, not the contract.</b> The two byte regions below are sized <i>per archetype</i> by
/// <see cref="ReplicationBlockLayout"/> from the compiled plan — a 3D position or a wide state body makes the entry larger, and the layout rounds it to the
/// next whole cache line. The sizes here are the ones a 2D SWG-shaped archetype produces, kept as a declared type so the fixed head has a name and a
/// verifiable offset table; an archetype whose regions differ is carved by the layout's strides and never by <c>sizeof</c> of this type.
/// </para>
/// <para>
/// <see cref="MotionSegment"/> and <see cref="PackedState"/> are reserved byte regions whose interior encoding is defined by the projection pass, not here:
/// the motion segment carries a pre-encoded <c>p0</c> (u24 × dims), <c>v</c> (dims × the derived velocity width), <c>t0</c> (u16) and an epoch byte; the
/// packed state carries the archetype's declared state fields.
/// </para>
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
/// Per-entity replication state read only by the projection pass, never on the per-hit path, in its <b>baseline</b> shape. Half a cache line, per AC-5.
/// </summary>
/// <remarks>
/// As with <see cref="ReplicationHotEntry"/> this is the 2D baseline and not the contract: <see cref="ReplicationBlockLayout"/> sizes the two regions from
/// the compiled plan. <see cref="PrevQuantizedPosition"/> holds the previous quantized position (<c>dims × 3</c> B) and <see cref="RunStart"/> the run start
/// pair <c>(p_s, t_s)</c> — the same quantized position plus a <c>u32</c> tick, rounded to four bytes.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal unsafe struct ReplicationColdEntry
{
    /// <summary>Previous quantized position. Encoding owned by the projection pass.</summary>
    public fixed byte PrevQuantizedPosition[6];

    /// <summary>Run start <c>(p_s, t_s)</c>. Encoding owned by the projection pass.</summary>
    public fixed byte RunStart[12];

    /// <summary>The tick of the entity's last push event (SUB-19).</summary>
    public uint LastEventTick;
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
/// Block size is fixed per archetype, because <c>N</c>, the position and the declared groups are all fixed at <c>Start</c>. That is what lets the pool keep
/// one free list of identical blocks with no size classes and no fragmentation — and what lets the entry strides be sized from the compiled plan instead of
/// frozen at a struct declaration: a 3D position or a wide state body grows the hot entry to two lines for that archetype alone.
/// </para>
/// </remarks>
internal readonly struct ReplicationBlockLayout
{
    /// <summary>Size of the block header, and therefore the offset of the first hot entry. One cache line.</summary>
    public const int HeaderSize = 64;

    /// <summary>The hot entry's stride granularity, and the stride a baseline archetype lands on. AC-5's "≤ 64 B hot per watched entity".</summary>
    public const int HotEntrySize = 64;

    /// <summary>The cold entry's stride granularity, and the stride a baseline archetype lands on.</summary>
    public const int ColdEntrySize = 32;

    /// <summary>
    /// The hot entry's fixed head, ahead of the two per-archetype regions: <c>EntityId</c> (8) + <c>netId</c> (4) + generation (2) + flags (2) +
    /// <c>GroupTicks[4]</c> (16). Every archetype pays exactly this much before its segment and its state.
    /// </summary>
    public const int HotFixedBytes = 32;

    /// <summary>
    /// The cold entry's fixed head: the tick of the entity's last event (SUB-19). It sits <i>after</i> the two regions, as <see
    /// cref="ReplicationColdEntry"/> lays it out.
    /// </summary>
    public const int ColdFixedBytes = 4;

    /// <summary>The baseline motion segment: a 2D <c>p0</c> (u24 × 2), a 2D <c>v</c> (i16 × 2), <c>t0</c> and an epoch byte.</summary>
    public const int BaselineSegmentBytes = 14;

    /// <summary>The baseline packed state body — <see cref="ReplicationHotEntry.PackedState"/>.</summary>
    public const int BaselinePackedStateBytes = 16;

    /// <summary>The baseline previous quantized position: 2D at 24 bits per axis.</summary>
    public const int BaselinePrevPositionBytes = 6;

    /// <summary>The baseline run start <c>(p_s, t_s)</c>: a 2D quantized position plus a <c>u32</c> tick, rounded to four bytes.</summary>
    public const int BaselineRunStartBytes = 12;

    /// <summary>
    /// Creates the baseline layout for an archetype whose clusters hold <paramref name="slotCount"/> entities: a 64 B hot entry and a 32 B cold one.
    /// </summary>
    /// <param name="slotCount">The archetype's cluster slot count, <c>N</c>.</param>
    /// <param name="ownerEntrySize">Bytes per owner entry, or <c>0</c> when the archetype declares no owner fields.</param>
    /// <remarks>
    /// For a plan-sized layout use <see cref="ForArchetype"/>. This overload is what a fixture or a non-projecting caller wants: the shape
    /// <see cref="ReplicationHotEntry"/> and <see cref="ReplicationColdEntry"/> declare.
    /// </remarks>
    public ReplicationBlockLayout(int slotCount, int ownerEntrySize = 0)
        : this(slotCount, BaselineSegmentBytes, BaselinePackedStateBytes, BaselinePrevPositionBytes, BaselineRunStartBytes, ownerEntrySize, 0, 0)
    {
    }

    private ReplicationBlockLayout(int slotCount, int segmentBytes, int packedStateBytes, int prevPositionBytes, int runStartBytes, int ownerEntrySize,
        int enterPositionBytes, int enterBodyBytes, int visibilityPositionBytes = 0, int headingBytes = 0)
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

        ArgumentOutOfRangeException.ThrowIfNegative(segmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(packedStateBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(prevPositionBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(runStartBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(enterPositionBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(enterBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(visibilityPositionBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(headingBytes);

        SlotCount = slotCount;
        OwnerEntrySize = ownerEntrySize;
        SegmentBytes = segmentBytes;
        PackedStateBytes = packedStateBytes;
        PrevPositionBytes = prevPositionBytes;
        RunStartBytes = runStartBytes;
        EnterPositionBytes = enterPositionBytes;
        EnterBodyBytes = enterBodyBytes;
        VisibilityPositionBytes = visibilityPositionBytes;
        HeadingBytes = headingBytes;

        // The strides are the whole point of sizing here rather than in a struct declaration: an entry that straddles two cache lines costs the per-hit passes
        // a second line on every read, so the stride is rounded UP to whole lines rather than packed. An archetype whose regions fit one line keeps AC-5's
        // 64 B; one that does not pays 128 B knowingly, and the layout is where that becomes visible.
        HotStride = RoundUpTo(HotFixedBytes + segmentBytes + packedStateBytes, HotEntrySize);
        ColdStride = RoundUpTo(ColdFixedBytes + prevPositionBytes + runStartBytes + visibilityPositionBytes + enterPositionBytes + enterBodyBytes + headingBytes,
            ColdEntrySize);

        // Computed once. These are fixed for the archetype's lifetime and are read on paths that become per-cluster and then per-hit, so recomputing a
        // multiply-and-add on every access is work with a known answer.
        HotOffset = HeaderSize;
        ColdOffset = HotOffset + (slotCount * HotStride);
        OwnerOffset = ColdOffset + (slotCount * ColdStride);
        BlockSize = OwnerOffset + (slotCount * ownerEntrySize);
        BlockStride = (BlockSize + 63) & ~63;
    }

    /// <summary>
    /// Creates the layout an archetype's compiled plan asks for: the fixed fields plus <i>this</i> archetype's motion segment, state body, previous position
    /// and run start, each rounded to its entry's stride granularity.
    /// </summary>
    /// <param name="slotCount">The archetype's cluster slot count, <c>N</c>.</param>
    /// <param name="segmentBytes">Bytes of one pre-encoded motion segment, or <c>0</c> when the archetype does not move.</param>
    /// <param name="packedStateBytes">Bytes of the widest state body the archetype's public groups can produce.</param>
    /// <param name="prevPositionBytes">Bytes of one quantized position, or <c>0</c> when the archetype does not move.</param>
    /// <param name="runStartBytes">Bytes of the run start pair <c>(p_s, t_s)</c>, or <c>0</c> when the archetype does not move.</param>
    /// <param name="ownerEntrySize">Bytes per owner entry, or <c>0</c> when the archetype declares no owner fields.</param>
    /// <param name="enterPositionBytes">
    /// Bytes of the quantized position an enter record carries for an archetype that reserves no motion segment — a <c>static</c> position — or <c>0</c>.
    /// </param>
    /// <param name="enterBodyBytes">Bytes of the widest <c>onEnter</c> body the archetype can produce, or <c>0</c> when it declares no <c>onEnter</c> field.</param>
    /// <returns>The layout.</returns>
    /// <remarks>
    /// <b>It sizes; it refuses nothing.</b> A 3D archetype with four <c>varu</c> state fields needs 18 B of segment and 20 B of state, which is 70 B of hot
    /// entry — a legal declaration that the fixed 64 B shape would have silently overrun. The answer is a 128 B stride, not a refusal.
    /// </remarks>
    /// <param name="headingBytes">Bytes of the codes the client holds for the archetype's headings (09 § 15): four per heading field, or <c>0</c>.</param>
    public static ReplicationBlockLayout ForArchetype(int slotCount, int segmentBytes, int packedStateBytes, int prevPositionBytes, int runStartBytes,
        int ownerEntrySize, int enterPositionBytes = 0, int enterBodyBytes = 0, int headingBytes = 0) =>
        new(slotCount, segmentBytes, packedStateBytes, prevPositionBytes, runStartBytes, ownerEntrySize, enterPositionBytes, enterBodyBytes,
            headingBytes: headingBytes);

    /// <summary>
    /// This layout with a visibility position v̂ of its own in every cold entry (09 § 2): as many bytes as the previous quantized position, for an
    /// archetype whose slack <c>h</c> is above zero. With <c>h = 0</c> v̂ is the previous position itself and no byte is added.
    /// </summary>
    /// <returns>The widened layout; this one when the archetype keeps no previous position (a static one).</returns>
    public ReplicationBlockLayout WithVisibilityPosition() =>
        PrevPositionBytes == 0 || VisibilityPositionBytes > 0
            ? this
            : new ReplicationBlockLayout(SlotCount, SegmentBytes, PackedStateBytes, PrevPositionBytes, RunStartBytes, OwnerEntrySize, EnterPositionBytes,
                EnterBodyBytes, PrevPositionBytes, HeadingBytes);

    /// <summary>The archetype's cluster slot count, <c>N</c>.</summary>
    public int SlotCount { get; }

    /// <summary>Bytes per owner entry; <c>0</c> when the archetype declares no owner fields.</summary>
    public int OwnerEntrySize { get; }

    /// <summary>Bytes this archetype's pre-encoded motion segment reserves in every hot entry.</summary>
    public int SegmentBytes { get; }

    /// <summary>Bytes this archetype's state body reserves in every hot entry.</summary>
    public int PackedStateBytes { get; }

    /// <summary>Bytes the previous quantized position reserves in every cold entry.</summary>
    public int PrevPositionBytes { get; }

    /// <summary>Bytes the run start pair reserves in every cold entry.</summary>
    public int RunStartBytes { get; }

    /// <summary>Bytes v̂ reserves in every cold entry: the previous position's width when the archetype's slack is above zero, else 0.</summary>
    public int VisibilityPositionBytes { get; }

    /// <summary>
    /// Bytes the enter cache's position reserves in every cold entry: a <c>static</c> archetype's quantized <c>p0</c>, which no motion segment holds.
    /// </summary>
    public int EnterPositionBytes { get; }

    /// <summary>Bytes the enter cache's <c>onEnter</c> body reserves in every cold entry, zero-padded to the section's widest form.</summary>
    public int EnterBodyBytes { get; }

    /// <summary>
    /// The enter cache: the two pieces of an enter record that neither the hot entry nor a state record ever carries, kept per entity so a session that
    /// was not there when the entity was first projected can still be sent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is in the COLD entry, and that is the hot/cold split's own criterion applied.</b> Both pieces are read exactly once per session per entity —
    /// on the frame that first tells that session the entity exists — and never on the per-hit path, which is what the cold entry is for. Putting them in
    /// the hot entry would widen the line every frame reads for every hit in order to serve a read that happens once.
    /// </para>
    /// <para>
    /// <b>It is free for the archetypes this phase serves.</b> A 2D mover reserves 6 B of previous position, 12 B of run start and 4 B of tick — 22 of a
    /// 32 B cold stride — so an <c>onEnter</c> body up to 10 B costs no byte at all. A static archetype reserves neither previous position nor run start,
    /// and its position plus body fit the same stride.
    /// </para>
    /// </remarks>
    public int EnterBytes => EnterPositionBytes + EnterBodyBytes;

    /// <summary>Distance between consecutive hot entries: the fixed head plus this archetype's regions, rounded up to whole cache lines.</summary>
    public int HotStride { get; }

    /// <summary>Distance between consecutive cold entries: the fixed head plus this archetype's regions, rounded up to 32 B.</summary>
    public int ColdStride { get; }

    /// <summary>Byte offset of the motion segment inside one hot entry.</summary>
    public int SegmentOffsetInHotEntry => HotFixedBytes;

    /// <summary>Byte offset of the packed state body inside one hot entry.</summary>
    public int PackedStateOffsetInHotEntry => HotFixedBytes + SegmentBytes;

    /// <summary>Byte offset of the previous quantized position inside one cold entry.</summary>
    public int PrevPositionOffsetInColdEntry => 0;

    /// <summary>Byte offset of the run start pair inside one cold entry.</summary>
    public int RunStartOffsetInColdEntry => PrevPositionBytes;

    /// <summary>Byte offset of the last-event tick inside one cold entry (SUB-19).</summary>
    public int LastEventTickOffsetInColdEntry => PrevPositionBytes + RunStartBytes;

    /// <summary>
    /// Byte offset of the visibility position v̂ inside one cold entry — the position every geometric test reads (SUB-20). The previous position's own
    /// offset when the layout reserves no v̂: with a slack of zero, v̂ is the previous position.
    /// </summary>
    public int VisibilityPositionOffsetInColdEntry =>
        VisibilityPositionBytes > 0 ? PrevPositionBytes + RunStartBytes + ColdFixedBytes : PrevPositionOffsetInColdEntry;

    /// <summary>Byte offset of the enter cache's static position inside one cold entry.</summary>
    public int EnterPositionOffsetInColdEntry => PrevPositionBytes + RunStartBytes + ColdFixedBytes + VisibilityPositionBytes;

    /// <summary>Byte offset of the enter cache's <c>onEnter</c> body inside one cold entry.</summary>
    public int EnterBodyOffsetInColdEntry => EnterPositionOffsetInColdEntry + EnterPositionBytes;

    /// <summary>
    /// Where the codes the client holds for the archetype's headings start (09 § 15): a heading is sent only when it turns past its tolerance, so what the
    /// client holds can trail the value, and the deadband is measured from it. Four bytes per heading field, in the order of the fields.
    /// </summary>
    public int HeadingOffsetInColdEntry => EnterBodyOffsetInColdEntry + EnterBodyBytes;

    /// <summary>Bytes of the heading codes in every cold entry; <c>0</c> when the archetype declares no heading.</summary>
    public int HeadingBytes { get; }

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

    private static int RoundUpTo(int value, int granularity) => (value + granularity - 1) / granularity * granularity;
}
