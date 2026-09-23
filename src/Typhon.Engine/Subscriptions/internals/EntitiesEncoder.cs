using System;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One archetype's encoding constants, resolved once at <c>Start</c>: its wire index, the offsets a record is copied from, and the section walk that
/// recovers a stored body's real length.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its reason for existing is the wire index.</b> A plan's position in <c>SubscriptionsRuntime.Plans</c> is declaration order; the index an
/// <c>ENTITIES</c> block carries is the CANONICAL one the catalog assigns (03 § 4). Resolving that per frame would be a lookup by name on the per-session
/// path; resolving it here makes it a field read.
/// </para>
/// <para>
/// <b>The group offsets are S1's, not a second opinion.</b> A stored state body is the concatenation of every group's body, each zero-padded to its
/// section's <c>MaxBodyBytes</c> — so group <c>g</c> begins at the sum of the earlier sections' bounds, which is exactly what
/// <c>ProjectionPass</c> computes when it writes them. The one number that is NOT stored is each body's real length, because a <c>varu</c> spends fewer
/// bytes than it reserves; <see cref="BodyLength"/> recovers it by walking the section's fields, which costs nothing at all for a section whose codecs are
/// all fixed-width (<see cref="SectionWalk.FixedBytes"/> ≥ 0) and a continuation-bit scan otherwise.
/// </para>
/// </remarks>
internal sealed unsafe class ArchetypeEncodePlan
{
    /// <summary>
    /// How one stored section body is measured: a fixed byte count when every field has one, or the per-field widths to walk when a <c>varu</c> is present.
    /// </summary>
    internal readonly struct SectionWalk
    {
        /// <summary>The body's length in bytes when it is the same for every value, or <c>-1</c> when a field is variable-length.</summary>
        public int FixedBytes { get; init; }

        /// <summary>Bytes of the section's leading bit pack (W12).</summary>
        public int PackBytes { get; init; }

        /// <summary>
        /// One entry per byte-aligned field, in wire order: the field's fixed width, or <c>0</c> for a variable-length codec that has to be scanned.
        /// </summary>
        public int[] FieldBytes { get; init; }

        /// <summary>Where the section's body begins inside the stored region it shares with its siblings.</summary>
        public int Offset { get; init; }

        /// <summary>The section's widest body, which is what the stored region reserves for it.</summary>
        public int MaxBytes { get; init; }
    }

    /// <summary>The archetype's canonical wire index — what an <c>ENTITIES</c> block's first <c>varu</c> carries.</summary>
    public int WireIndex { get; init; }

    /// <summary>The block layout the records are copied out of.</summary>
    public ReplicationBlockLayout Layout { get; init; }

    /// <summary>Whether the archetype declares a position at all.</summary>
    public bool HasPosition { get; init; }

    /// <summary>Whether that position travels as motion segments rather than as one value on enter (W16).</summary>
    public bool Moving { get; init; }

    /// <summary>Bytes one axis of the archetype's <c>pos</c> codec occupies, or <c>0</c> when it declares no position.</summary>
    public int PositionAxisBytes { get; init; }

    /// <summary>How many change groups the archetype declares, which bounds a state record's mask.</summary>
    public int GroupCount { get; init; }

    /// <summary>The hot-entry tick slot of each group, parallel to the mask's bits.</summary>
    public int[] GroupTickSlot { get; init; }

    /// <summary>The motion segment's tick slot, or <c>-1</c> when the archetype does not move.</summary>
    public int MotionTickSlot { get; init; }

    /// <summary>The <c>onEnter</c> section, measured out of the cold entry's enter cache.</summary>
    public SectionWalk OnEnter { get; init; }

    /// <summary>Each change group's section, measured out of the hot entry's packed state.</summary>
    public SectionWalk[] Groups { get; init; }

    /// <summary>The largest number of bytes one enter record of this archetype can occupy, gap included.</summary>
    public int MaxEnterBytes { get; init; }

    /// <summary>The largest number of bytes one segment record can occupy, gap included.</summary>
    public int MaxSegmentBytes { get; init; }

    /// <summary>The largest number of bytes one state record can occupy, gap and mask included.</summary>
    public int MaxStateBytes { get; init; }

    /// <summary>The address of one slot's hot entry inside <paramref name="block"/>.</summary>
    /// <param name="block">The replication block.</param>
    /// <param name="slot">The slot.</param>
    /// <returns>The entry's first byte.</returns>
    public byte* Hot(nint block, int slot) => (byte*)block + Layout.HotOffset + (slot * Layout.HotStride);

    /// <summary>The address of one slot's cold entry inside <paramref name="block"/>.</summary>
    /// <param name="block">The replication block.</param>
    /// <param name="slot">The slot.</param>
    /// <returns>The entry's first byte.</returns>
    public byte* Cold(nint block, int slot) => (byte*)block + Layout.ColdOffset + (slot * Layout.ColdStride);

    /// <summary>
    /// The real length of a stored section body, which is at most <see cref="SectionWalk.MaxBytes"/> and is exactly it whenever no field is variable-length.
    /// </summary>
    /// <param name="walk">The section.</param>
    /// <param name="body">The stored body, zero-padded to <see cref="SectionWalk.MaxBytes"/>.</param>
    /// <returns>Bytes to copy onto the wire.</returns>
    public static int BodyLength(in SectionWalk walk, byte* body)
    {
        if (walk.FixedBytes >= 0)
        {
            return walk.FixedBytes;
        }

        var at = walk.PackBytes;
        var widths = walk.FieldBytes;
        for (var i = 0; i < widths.Length; i++)
        {
            var width = widths[i];
            if (width > 0)
            {
                at += width;
                continue;
            }

            // A canonical varint is minimal, so the stored zero padding can never be mistaken for one of its bytes: the scan stops at the first byte whose
            // continuation bit is clear, and a padding zero IS such a byte only after the varint it follows has already ended.
            while ((body[at] & 0x80) != 0)
            {
                at++;
            }

            at++;
        }

        return at;
    }
}

/// <summary>
/// Writes one session's frame: the <c>TICK</c> header, then one <c>ENTITIES</c> block per archetype the session has records for (03 § 5, § 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>It frames through <see cref="TickWriter"/> and copies bodies itself.</b> The header, the block type and the block's length prefix are
/// <c>Typhon.Protocol</c>'s — the golden vectors pin those bytes, and a second spelling of them is exactly the drift the vectors exist to catch. What this
/// type adds is the record payload, which <see cref="TickWriter.WriteEntities"/> builds from an object model: a <c>RecordValues</c> dictionary per record,
/// re-encoded per session. The engine's records are already encoded, once, in the entity's replication block, so a record here is a <c>varu</c> gap and a
/// run of <c>memcpy</c>s (02 § 7, SUB-07).
/// </para>
/// <para>
/// <b>The block contents come from the BLOCK.</b> A record names the groups it carries; their bodies are the hot entry's stored copies, which the
/// projection keeps current, so a session caught up after K skipped ticks reads the same bytes as one that missed nothing (SUB-03).
/// </para>
/// <para>
/// <b>Leaves are last in every block, and the record order inside a block is the grammar's.</b> A client applies leaves after everything else whatever order
/// the blocks travel in (03 § 10), but the four sub-lists still travel in the grammar's order — enters, segments, states, leaves — because that is what a
/// decoder reads.
/// </para>
/// </remarks>
internal static unsafe class EntitiesEncoder
{
    /// <summary>Bytes the <c>TICK</c> header can occupy: the type byte, the tick, the flags and an optional period.</summary>
    public const int MaxHeaderBytes = 1 + 4 + 1 + 4;

    /// <summary>
    /// Bytes one <c>ENTITIES</c> block costs before its records: the type, a five-byte length reservation, the index, and each sub-list's run count and
    /// the record count of its one private run.
    /// </summary>
    public const int MaxBlockOverheadBytes = 1 + 5 + 5 + (8 * 5);

    /// <summary>The largest <c>varu</c> a netId gap can spend.</summary>
    public const int MaxGapBytes = 5;

    /// <summary>Writes the frame header.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="tick">The frame's tick, truncated to the wire's <c>u32</c>.</param>
    /// <param name="flags">The frame's flags.</param>
    public static void WriteHeader(ref WireWriter w, uint tick, TickFlags flags) => TickWriter.WriteHeader(ref w, tick, flags);

    /// <summary>
    /// Writes one archetype's <c>ENTITIES</c> block. Each sub-list must already be sorted ascending by netId and hold no netId twice.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="plan">The archetype's encoding constants.</param>
    /// <param name="enters">Enter records.</param>
    /// <param name="segments">Motion segments; must be empty when the archetype does not move.</param>
    /// <param name="states">State records, each carrying a non-zero group mask.</param>
    /// <param name="leaves">Leaving entities.</param>
    public static void WriteEntities(ref WireWriter w, ArchetypeEncodePlan plan, ReadOnlySpan<FrameRecord> enters, ReadOnlySpan<FrameRecord> segments,
        ReadOnlySpan<FrameRecord> states, ReadOnlySpan<FrameRecord> leaves)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Entities);
        w.WriteVaru((uint)plan.WireIndex);
        var layout = plan.Layout;

        // ── enters ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        WriteRunHeader(ref w, enters.Length);
        var prev = -1L;
        for (var i = 0; i < enters.Length; i++)
        {
            ref readonly var record = ref enters[i];
            WriteGap(ref w, ref prev, record.NetId);
            var hot = plan.Hot(record.Block, record.Slot);
            var cold = plan.Cold(record.Block, record.Slot);

            if (plan.HasPosition)
            {
                // enterPos: a moving archetype's is the whole segment the motion rule maintains; a static one's is the quantized p0 the enter cache holds,
                // because a static position reserves no segment at all (03 § 5's `enterPos`).
                if (plan.Moving)
                {
                    w.WriteBytes(new ReadOnlySpan<byte>(hot + layout.SegmentOffsetInHotEntry, layout.SegmentBytes));
                }
                else
                {
                    w.WriteBytes(new ReadOnlySpan<byte>(cold + layout.EnterPositionOffsetInColdEntry, layout.EnterPositionBytes));
                }
            }

            WriteSection(ref w, plan.OnEnter, cold + layout.EnterBodyOffsetInColdEntry);

            // Every group, in canonical order, with no mask: an enter is the whole entity (W16).
            var state = hot + layout.PackedStateOffsetInHotEntry;
            for (var g = 0; g < plan.Groups.Length; g++)
            {
                WriteSection(ref w, plan.Groups[g], state);
            }
        }

        // ── segments ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        WriteRunHeader(ref w, segments.Length);
        prev = -1;
        for (var i = 0; i < segments.Length; i++)
        {
            ref readonly var record = ref segments[i];
            WriteSegmentRecord(ref w, plan, ref prev, record.NetId, record.Block, record.Slot);
        }

        // ── state records ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        WriteRunHeader(ref w, states.Length);
        prev = -1;
        for (var i = 0; i < states.Length; i++)
        {
            ref readonly var record = ref states[i];
            WriteStateRecord(ref w, plan, ref prev, record.NetId, record.Block, record.Slot, record.GroupMask);
        }

        // ── leaves, last ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        WriteRunHeader(ref w, leaves.Length);
        prev = -1;
        for (var i = 0; i < leaves.Length; i++)
        {
            WriteGap(ref w, ref prev, leaves[i].NetId);
        }

        TickWriter.EndBlock(ref w, mark);
    }

    /// <summary>
    /// Writes one motion segment record: the netId gap, then the segment the projection keeps in the hot entry.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="plan">The archetype's encoding constants.</param>
    /// <param name="prev">The run's previous netId; <c>-1</c> at the start of a run.</param>
    /// <param name="netId">The entity.</param>
    /// <param name="block">The replication block.</param>
    /// <param name="slot">The slot.</param>
    /// <remarks>
    /// <b>One writer, two callers, and that is a correctness property rather than tidiness.</b> A per-session frame and a run encoded once per cluster have
    /// to produce the same bytes for the same slot, or a client would see one entity described two ways depending on which path its session happened to
    /// take. Sharing the statement that does the writing makes that true by construction instead of by a comparison test that can only sample.
    /// </remarks>
    public static void WriteSegmentRecord(ref WireWriter w, ArchetypeEncodePlan plan, ref long prev, uint netId, nint block, int slot)
    {
        var layout = plan.Layout;
        WriteGap(ref w, ref prev, netId);
        w.WriteBytes(new ReadOnlySpan<byte>(plan.Hot(block, slot) + layout.SegmentOffsetInHotEntry, layout.SegmentBytes));
    }

    /// <summary>
    /// Writes one state record: the netId gap, the group mask, then the body of each group the mask names.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="plan">The archetype's encoding constants.</param>
    /// <param name="prev">The run's previous netId; <c>-1</c> at the start of a run.</param>
    /// <param name="netId">The entity.</param>
    /// <param name="block">The replication block.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="mask">The groups this record carries.</param>
    public static void WriteStateRecord(ref WireWriter w, ArchetypeEncodePlan plan, ref long prev, uint netId, nint block, int slot, byte mask)
    {
        var state = plan.Hot(block, slot) + plan.Layout.PackedStateOffsetInHotEntry;
        WriteGap(ref w, ref prev, netId);
        w.WriteU8(mask);
        for (var g = 0; g < plan.Groups.Length; g++)
        {
            if ((mask & (1 << g)) != 0)
            {
                WriteSection(ref w, plan.Groups[g], state);
            }
        }
    }

    /// <summary>
    /// Writes a sub-list made of one run this session owns: the run count, then that run's record count.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="count">The run's record count; zero writes an empty sub-list and no run at all.</param>
    /// <remarks>
    /// <b>The run is what a netId gap is relative to</b> (03 § 5). A record's gap is a delta from the record before it, so a sub-list that was one
    /// ascending sequence could never be assembled from bytes encoded somewhere else — the first record's gap would depend on a predecessor the encoder
    /// of those bytes never saw. A run restarts the delta; a frame this encoder writes has at most one run per sub-list.
    /// </remarks>
    private static void WriteRunHeader(ref WireWriter w, int count)
    {
        if (count == 0)
        {
            w.WriteVaru(0);
            return;
        }

        w.WriteVaru(1);
        w.WriteVaru((uint)count);
    }

    private static void WriteSection(ref WireWriter w, in ArchetypeEncodePlan.SectionWalk walk, byte* region)
    {
        if (walk.MaxBytes == 0)
        {
            return;
        }

        var body = region + walk.Offset;
        w.WriteBytes(new ReadOnlySpan<byte>(body, ArchetypeEncodePlan.BodyLength(in walk, body)));
    }

    private static void WriteGap(ref WireWriter w, ref long prev, uint netId)
    {
        // The frame stage sorts every sub-list and names each entity once per sub-list, so an out-of-order or repeated id here is a defect in this engine
        // rather than bad input — and one that would silently shift every following record, since the gap is relative.
        if (netId <= prev)
        {
            throw new InvalidOperationException(
                $"ENTITIES records must be ascending and distinct by netId: {netId} follows {prev}. The sub-list reached the encoder unsorted.");
        }

        w.WriteVaru((uint)(netId - prev - 1));
        prev = netId;
    }
}
