using System;
using System.Collections.Generic;

namespace Typhon.Protocol;

/// <summary>The <c>TICK</c> flags byte, bits in the order § 3 lists them.</summary>
[Flags]
public enum TickFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The initial fill under the enter budget is complete.</summary>
    ViewComplete = 1,

    /// <summary>Clear the store before applying this frame: a profile switch, a server-side reset, or a resumed session's first frame.</summary>
    Reset = 2,

    /// <summary>The server is overloaded and dilating time.</summary>
    Overload = 4,

    /// <summary>A <c>u32 periodUs</c> follows the flags: the duration of the interval that just elapsed, [N − 1, N].</summary>
    Period = 8,
}

/// <summary>The block types inside a <c>TICK</c> (03-wire-protocol § 3). An unknown type is skipped by its length.</summary>
public static class BlockTypes
{
    /// <summary>One archetype's enters, segments, state records and leaves.</summary>
    public const byte Entities = 0x01;

    /// <summary>Typed events.</summary>
    public const byte Events = 0x02;

    /// <summary>The controlled entity's owner groups and the last drained command sequence.</summary>
    public const byte Self = 0x03;

    /// <summary>Per-cell counts per archetype.</summary>
    public const byte Agg = 0x04;

    /// <summary>Catalog-declared metrics.</summary>
    public const byte Stats = 0x05;

    /// <summary>Engine-defined sub-blocks, capability-gated.</summary>
    public const byte Debug = 0x06;

    /// <summary>Command rejections.</summary>
    public const byte Acks = 0x07;

    /// <summary>Source lifecycle.</summary>
    public const byte Sources = 0x08;

    /// <summary>The session's realm frame (<c>typhon.3</c>): only in a <c>RESET</c> frame, and always its first block.</summary>
    public const byte Realm = 0x09;

    /// <summary>An application-defined payload.</summary>
    public const byte Ext = 0x7F;
}

/// <summary>A <c>SOURCES</c> entry's status.</summary>
public static class SourceStatus
{
    /// <summary>Every source the request added has delivered its initial enters.</summary>
    public const byte Applied = 0;

    /// <summary>The request failed; the previous source set is kept, and a <c>u16</c> code follows.</summary>
    public const byte Error = 1;
}

/// <summary>Which blocks of a <c>TICK</c> a <see cref="TickReader"/> hands to its sink.</summary>
[Flags]
public enum TickBlocks
{
    /// <summary>The <c>EVENTS</c> blocks.</summary>
    Events = 1,

    /// <summary>Every block but <c>EVENTS</c>, unknown types included.</summary>
    AllButEvents = 2,

    /// <summary>Every block.</summary>
    All = Events | AllButEvents,
}

/// <summary>
/// Receives a decoded <c>TICK</c>, block by block, in stream order — the blocks <see cref="TickReader.Read{TSink}"/> was asked for. Field values arrive
/// through the <see cref="IFieldSink"/> members between the record call that opened them and the next record call.
/// </summary>
public interface ITickSink : IFieldSink
{
    /// <summary>A frame begins.</summary>
    /// <param name="tick">The frame's tick.</param>
    /// <param name="flags">The frame's flags.</param>
    /// <param name="periodUs">The elapsed interval's period when <see cref="TickFlags.Period"/> is set, otherwise 0.</param>
    void BeginTick(uint tick, TickFlags flags, uint periodUs);

    /// <summary>
    /// A <c>REALM</c> block: the session's realm frame from here on, or <see langword="null"/> for <c>REALM(NONE)</c>. Delivered after the reader adopted it,
    /// so every positioned value that follows decodes over it.
    /// </summary>
    /// <param name="frame">The new frame, or <see langword="null"/>.</param>
    void Realm(RealmFrame frame);

    /// <summary>An <c>ENTITIES</c> block begins.</summary>
    /// <param name="archetype">Its archetype.</param>
    void BeginEntities(ArchetypePlan archetype);

    /// <summary>An enter record: its position, then every public field in wire order through the field members.</summary>
    /// <param name="netId">The entity.</param>
    /// <param name="position">The position, empty when the archetype is not spatial.</param>
    /// <param name="velocity">The velocity per tick, empty unless the archetype moves linearly.</param>
    /// <param name="t0">The segment's start tick, 0 unless the archetype moves.</param>
    /// <param name="epoch">The teleport counter, 0 unless the archetype moves.</param>
    void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch);

    /// <summary>A motion segment.</summary>
    /// <param name="netId">The entity.</param>
    /// <param name="position">The segment's start position.</param>
    /// <param name="velocity">The velocity per tick, empty for the <c>none</c> model.</param>
    /// <param name="t0">The segment's start tick.</param>
    /// <param name="epoch">The teleport counter; never interpolate across a change.</param>
    void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch);

    /// <summary>A state record: the fields of the groups in <paramref name="groupMask"/> follow through the field members.</summary>
    /// <param name="netId">The entity.</param>
    /// <param name="groupMask">Bit i set when group i is carried.</param>
    void State(uint netId, byte groupMask);

    /// <summary>A leave. Applied last in the frame by a store, so an event can still resolve the entity.</summary>
    /// <param name="netId">The entity.</param>
    void Leave(uint netId);

    /// <summary>An event: its fields follow through the field members.</summary>
    /// <param name="type">The event type.</param>
    void Event(MessagePlan type);

    /// <summary>
    /// The <c>SELF</c> block: the owner groups in <paramref name="ownerMask"/> follow through the field members. <paramref name="archetype"/> is
    /// <see langword="null"/> and <paramref name="netId"/> 0 when the session controls no entity (W17′): an acknowledgement only, which also tells the
    /// client to drop the owner state it holds.
    /// </summary>
    /// <param name="archetype">The controlled entity's archetype, or <see langword="null"/> when it controls none (W17′).</param>
    /// <param name="netId">The controlled entity, or 0 for none.</param>
    /// <param name="lastSeq">The highest command sequence drained into a tick at or before this frame's.</param>
    /// <param name="ownerMask">Bit i set when owner group i is carried.</param>
    void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask);

    /// <summary>A command rejection.</summary>
    /// <param name="seq">The rejected command's sequence.</param>
    /// <param name="reason">The reason code (<see cref="AckReasons"/>).</param>
    void Ack(ushort seq, byte reason);

    /// <summary>A source lifecycle entry.</summary>
    /// <param name="requestId">The client-chosen request id.</param>
    /// <param name="status"><see cref="SourceStatus.Applied"/> or <see cref="SourceStatus.Error"/>.</param>
    /// <param name="code">The error code, 0 when applied.</param>
    void Source(ushort requestId, byte status, ushort code);

    /// <summary>An <c>AGG</c> block begins.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="reset">Whether every cell not listed is now empty.</param>
    void BeginAggregate(CatalogGrid grid, bool reset);

    /// <summary>One changed cell's counts, one per <see cref="CatalogGrid.Archetypes"/> entry.</summary>
    /// <param name="cell">The row-major cell index.</param>
    /// <param name="counts">The counts; valid only for the duration of the call.</param>
    void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts);

    /// <summary>One metric value.</summary>
    /// <param name="metric">The metric.</param>
    /// <param name="valueIndex">Which value of a labelled metric, 0 for a scalar.</param>
    /// <param name="value">The value.</param>
    void Metric(MetricPlan metric, int valueIndex, double value);

    /// <summary>A <c>DEBUG</c> sub-block.</summary>
    /// <param name="subType">The sub-block type.</param>
    /// <param name="payload">Its bytes; valid only for the duration of the call.</param>
    void Debug(byte subType, scoped ReadOnlySpan<byte> payload);

    /// <summary>An <c>EXT</c> block.</summary>
    /// <param name="appTypeId">The application's payload type.</param>
    /// <param name="payload">Its bytes; valid only for the duration of the call.</param>
    void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload);

    /// <summary>A block of a type this library does not know, skipped.</summary>
    /// <param name="blockType">The type byte.</param>
    void UnknownBlock(byte blockType);

    /// <summary>The frame ends.</summary>
    void EndTick();
}

/// <summary>
/// Decodes a <c>TICK</c> message into an <see cref="ITickSink"/>: the header, then every block, each isolated by its length so a block can neither read
/// past its end nor leave bytes unread.
/// </summary>
public static class TickReader
{
    /// <summary>
    /// Decodes one self-contained <c>TICK</c> message: the session holds no realm frame before it, so any positioned value must follow a <c>REALM</c> block of
    /// the same message. A session decoder keeps its frame across messages with the other overload.
    /// </summary>
    /// <typeparam name="TSink">The sink type.</typeparam>
    /// <param name="message">The whole message.</param>
    /// <param name="plan">The compiled catalog.</param>
    /// <param name="sink">Receives the frame.</param>
    /// <param name="blocks">Which blocks reach the sink.</param>
    /// <exception cref="WireFormatException">The message is malformed.</exception>
    public static void Read<TSink>(ReadOnlySpan<byte> message, CatalogPlan plan, ref TSink sink, TickBlocks blocks = TickBlocks.All)
        where TSink : ITickSink, allows ref struct
    {
        RealmFrame frame = null;
        Read(message, plan, ref frame, ref sink, blocks);
    }

    /// <summary>Decodes one <c>TICK</c> message, type byte included.</summary>
    /// <typeparam name="TSink">The sink type; a struct keeps the calls inlined.</typeparam>
    /// <param name="message">The whole message.</param>
    /// <param name="plan">The session's compiled catalog.</param>
    /// <param name="frame">
    /// The session's realm frame, held across messages by the caller: every positioned value decodes over it, and a <c>REALM</c> block replaces it (SUB-30).
    /// </param>
    /// <param name="sink">Receives the frame.</param>
    /// <param name="blocks">
    /// Which blocks reach the sink; the others are skipped by their length, unread. A store reads a frame twice — every block but <c>EVENTS</c>, then
    /// <c>EVENTS</c> alone — to apply § 5's order whatever order the blocks travel in.
    /// </param>
    /// <exception cref="WireFormatException">The message is malformed.</exception>
    public static void Read<TSink>(ReadOnlySpan<byte> message, CatalogPlan plan, ref RealmFrame frame, ref TSink sink, TickBlocks blocks = TickBlocks.All)
        where TSink : ITickSink, allows ref struct
    {
        var reader = new WireReader(message);
        if (reader.ReadU8() != MessageTypes.Tick)
        {
            throw WireFormatException.Protocol("not a TICK message");
        }

        var tick = reader.ReadU32();
        var flags = (TickFlags)reader.ReadU8();
        var periodUs = (flags & TickFlags.Period) != 0 ? reader.ReadU32() : 0;
        sink.BeginTick(tick, flags, periodUs);

        // One ENTITIES block per archetype (03 § 10): a record in one block for an entity entering in another would apply out of order.
        Span<ulong> seenArchetypes = stackalloc ulong[(ProtocolConstants.MaxArchetypes + 63) / 64];
        seenArchetypes.Clear();
        var first = true;
        while (!reader.IsAtEnd)
        {
            var type = reader.ReadU8();
            var length = reader.ReadVaruAtMost(reader.Remaining, "block length");
            var block = reader.Slice(length);

            // Refused whichever pass reads the frame (12-realms § 5.2): only a RESET frame may carry a REALM, and only as its first block.
            if (type == BlockTypes.Realm && (!first || (flags & TickFlags.Reset) == 0))
            {
                throw WireFormatException.Malformed(first ? "a REALM block in a frame without RESET" : "a REALM block that is not the frame's first");
            }

            first = false;
            if ((blocks & (type == BlockTypes.Events ? TickBlocks.Events : TickBlocks.AllButEvents)) == 0)
            {
                continue;
            }

            switch (type)
            {
                case BlockTypes.Realm:
                    frame = ReadRealm(ref block, plan);
                    sink.Realm(frame);
                    break;
                case BlockTypes.Entities:
                    ReadEntities(ref block, plan, tick, seenArchetypes, frame, ref sink);
                    break;
                case BlockTypes.Events:
                    ReadEvents(ref block, plan, tick, frame, ref sink);
                    break;
                case BlockTypes.Self:
                    ReadSelf(ref block, plan, tick, frame, ref sink);
                    break;
                case BlockTypes.Agg:
                    ReadAggregate(ref block, plan, frame, ref sink);
                    break;
                case BlockTypes.Stats:
                    ReadStats(ref block, plan, tick, ref sink);
                    break;
                case BlockTypes.Debug:
                    while (!block.IsAtEnd)
                    {
                        var subType = block.ReadU8();
                        sink.Debug(subType, block.ReadBytes(block.ReadVaruAtMost(block.Remaining, "DEBUG sub-block length")));
                    }

                    break;
                case BlockTypes.Acks:
                    for (var n = block.ReadVaru(); n > 0; n--)
                    {
                        sink.Ack(block.ReadU16(), block.ReadU8());
                    }

                    break;
                case BlockTypes.Sources:
                    for (var n = block.ReadVaru(); n > 0; n--)
                    {
                        var requestId = block.ReadU16();
                        var status = block.ReadU8();
                        if (status > SourceStatus.Error)
                        {
                            throw WireFormatException.Malformed($"SOURCES status {status} is unknown");
                        }

                        sink.Source(requestId, status, status == SourceStatus.Error ? block.ReadU16() : (ushort)0);
                    }

                    break;
                case BlockTypes.Ext:
                    var appType = block.ReadVaru();
                    sink.Ext(appType, block.ReadBytes(block.Remaining));
                    break;
                default:
                    block.Skip(block.Remaining);
                    sink.UnknownBlock(type);
                    break;
            }

            // Checked inline rather than through ExpectEnd: an interpolated message argument would be built on every block, not only on failure.
            if (!block.IsAtEnd)
            {
                throw WireFormatException.Malformed($"block 0x{type:x2}: {block.Remaining} unread byte(s) after the declared content");
            }
        }

        sink.EndTick();
    }

    private static RealmFrame ReadRealm(ref WireReader r, CatalogPlan plan)
    {
        var frame = RealmFrame.Read(ref r, plan.RealmKinds.Length);

        // An AGG grid over this frame must stay within the cell bound a store allocates for (W28): refused with the frame, before any AGG of it.
        foreach (var grid in frame == null ? [] : plan.Grids)
        {
            if (frame.AggregateCellCount(grid.TileCells) > CatalogValidator.MaxGridCells)
            {
                throw WireFormatException.Malformed($"grid {grid.Idx} over this REALM has more than {CatalogValidator.MaxGridCells} cells");
            }
        }

        return frame;
    }

    private static void ReadEntities<TSink>(ref WireReader r, CatalogPlan plan, uint tick, scoped Span<ulong> seenArchetypes, RealmFrame frame,
        ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        var archetype = plan.Archetype(r.ReadVaruAtMost(int.MaxValue, "archetype index"));
        ref var seen = ref seenArchetypes[archetype.Idx >> 6];
        var bit = 1UL << (archetype.Idx & 63);
        if ((seen & bit) != 0)
        {
            throw WireFormatException.Malformed($"a second ENTITIES block for archetype '{archetype.Name}'");
        }

        seen |= bit;
        var position = archetype.Position;
        if (position != null && frame == null)
        {
            throw WireFormatException.Protocol($"an ENTITIES block for positioned archetype '{archetype.Name}' while the session holds no realm");
        }

        sink.BeginEntities(archetype);
        Span<double> p = stackalloc double[3];
        Span<double> v = stackalloc double[3];

        // ── enters ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        for (var runs = r.ReadVaru(); runs > 0; runs--)
        {
            var prev = -1L;
            for (var n = ReadRunLength(ref r); n > 0; n--)
            {
                var netId = NextNetId(ref r, ref prev);
                uint t0 = 0;
                byte epoch = 0;
                var dims = 0;
                var velDims = 0;
                if (position != null)
                {
                    dims = position.Dims;
                    FieldCodec.ReadNumber(ref r, position.Pos, tick, p, frame);
                    if (position.Moving)
                    {
                        velDims = ReadSegmentTail(ref r, position, tick, v, out t0, out epoch);
                    }
                }

                sink.Enter(netId, p[..dims], v[..velDims], t0, epoch);
                FieldCodec.ReadSection(ref r, archetype.OnEnter, tick, ref sink, frame);
                foreach (var section in archetype.GroupSections)
                {
                    FieldCodec.ReadSection(ref r, section, tick, ref sink, frame);
                }
            }
        }

        // ── segments ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        var segmentRuns = r.ReadVaru();
        if (segmentRuns > 0 && (position == null || !position.Moving))
        {
            throw WireFormatException.Malformed($"archetype '{archetype.Name}' does not move but its block carries segment(s)");
        }

        for (var runs = segmentRuns; runs > 0; runs--)
        {
            var prev = -1L;
            for (var n = ReadRunLength(ref r); n > 0; n--)
            {
                var netId = NextNetId(ref r, ref prev);
                FieldCodec.ReadNumber(ref r, position.Pos, tick, p, frame);
                var velDims = ReadSegmentTail(ref r, position, tick, v, out var t0, out var epoch);
                sink.Segment(netId, p[..position.Dims], v[..velDims], t0, epoch);
            }
        }

        // ── states ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        var groupCount = archetype.Groups.Length;
        for (var runs = r.ReadVaru(); runs > 0; runs--)
        {
            var prev = -1L;
            for (var n = ReadRunLength(ref r); n > 0; n--)
            {
                var netId = NextNetId(ref r, ref prev);
                var mask = r.ReadU8();
                if (mask == 0 || (mask >> groupCount) != 0)
                {
                    throw WireFormatException.Malformed($"state record mask 0x{mask:x2} is invalid for {groupCount} group(s)");
                }

                sink.State(netId, mask);
                for (var g = 0; g < groupCount; g++)
                {
                    if ((mask & (1 << g)) != 0)
                    {
                        FieldCodec.ReadSection(ref r, archetype.GroupSections[g], tick, ref sink, frame);
                    }
                }
            }
        }

        // ── leaves ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        for (var runs = r.ReadVaru(); runs > 0; runs--)
        {
            var prev = -1L;
            for (var n = ReadRunLength(ref r); n > 0; n--)
            {
                sink.Leave(NextNetId(ref r, ref prev));
            }
        }
    }

    /// <summary>
    /// Reads one sub-list run's record count, which the grammar forbids to be zero.
    /// </summary>
    /// <param name="r">The reader, positioned at the run's count.</param>
    /// <returns>The run's record count.</returns>
    /// <remarks>
    /// <b>A canonical encoding has no empty run.</b> A sub-list with nothing to say spends one <c>varu</c> zero on its RUN count and stops; an empty run
    /// inside a non-empty sub-list is therefore never produced, and admitting it would give two byte strings for one frame. The check also bounds the
    /// decoder's work against a hostile stream, which could otherwise spend a megabyte of run counts on no records at all.
    /// </remarks>
    private static uint ReadRunLength(ref WireReader r)
    {
        var n = r.ReadVaru();
        if (n == 0)
        {
            throw WireFormatException.Malformed("an ENTITIES sub-list run carries no record");
        }

        return n;
    }

    private static int ReadSegmentTail(ref WireReader r, PositionPlan position, uint tick, scoped Span<double> v, out uint t0, out byte epoch)
    {
        var velDims = 0;
        if (position.Linear)
        {
            FieldCodec.ReadNumber(ref r, position.Vel, tick, v);
            velDims = position.Dims;
        }

        t0 = WireMath.DecodeTickLo(r.ReadU16(), tick);
        epoch = r.ReadU8();
        return velDims;
    }

    private static uint NextNetId(ref WireReader r, ref long prev)
    {
        var next = prev + 1 + r.ReadVaru();
        if (next > uint.MaxValue)
        {
            throw WireFormatException.Malformed("netId gap overflows 32 bits");
        }

        prev = next;
        return (uint)next;
    }

    private static void ReadEvents<TSink>(ref WireReader r, CatalogPlan plan, uint tick, RealmFrame frame, ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        for (var n = r.ReadVaru(); n > 0; n--)
        {
            var type = plan.Event(r.ReadVaruAtMost(int.MaxValue, "event index"));
            sink.Event(type);
            FieldCodec.ReadSection(ref r, type.Body, tick, ref sink, frame);
        }
    }

    private static void ReadSelf<TSink>(ref WireReader r, CatalogPlan plan, uint tick, RealmFrame frame, ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        var archetypeIdx = r.ReadVaruAtMost(int.MaxValue, "archetype index");
        var netId = r.ReadVaru();
        var lastSeq = r.ReadU16();
        var mask = r.ReadU8();

        // netId 0 is never an entity: it is "no controlled entity" (W17′), an acknowledgement only, so it names no archetype and carries no group.
        if (netId == 0)
        {
            if (archetypeIdx != 0 || mask != 0)
            {
                throw WireFormatException.Malformed(
                    $"SELF with no controlled entity must name archetype 0 and no owner group (archetype {archetypeIdx}, mask 0x{mask:x2})");
            }

            sink.Self(null, 0, lastSeq, 0);
            return;
        }

        var archetype = plan.Archetype(archetypeIdx);
        var groupCount = archetype.OwnerGroups.Length;
        if ((mask >> groupCount) != 0)
        {
            throw WireFormatException.Malformed($"SELF owner mask 0x{mask:x2} is invalid for {groupCount} owner group(s)");
        }

        sink.Self(archetype, netId, lastSeq, mask);
        for (var g = 0; g < groupCount; g++)
        {
            if ((mask & (1 << g)) != 0)
            {
                FieldCodec.ReadSection(ref r, archetype.OwnerSections[g], tick, ref sink, frame);
            }
        }
    }

    private static void ReadAggregate<TSink>(ref WireReader r, CatalogPlan plan, RealmFrame frame, ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        var gridIdx = r.ReadVaruAtMost(int.MaxValue, "grid index");
        if (gridIdx >= plan.Grids.Length)
        {
            throw WireFormatException.Malformed($"grid index {gridIdx} does not exist");
        }

        var grid = plan.Grids[gridIdx];
        var flags = r.ReadU8();
        sink.BeginAggregate(grid, (flags & 1) != 0);
        // The grid's dimensions are the frame's (typhon.3, 12-realms § 5.3): no realm, no grid.
        if (frame == null)
        {
            throw WireFormatException.Protocol($"an AGG block for grid {gridIdx} while the session holds no realm");
        }

        var cellCount = frame.AggregateCellCount(grid.TileCells);

        var archetypes = grid.Archetypes?.Length ?? 0;
        if (archetypes > ProtocolConstants.MaxArchetypes)
        {
            throw WireFormatException.Malformed($"grid {gridIdx} lists {archetypes} archetypes");
        }

        Span<uint> counts = stackalloc uint[ProtocolConstants.MaxArchetypes];
        counts = counts[..archetypes];
        var prev = -1L;
        for (var n = r.ReadVaru(); n > 0; n--)
        {
            var cell = prev + 1 + r.ReadVaru();
            if (cell >= cellCount)
            {
                throw WireFormatException.Malformed($"AGG cell {cell} is outside the grid's {cellCount} cells");
            }

            prev = cell;
            for (var a = 0; a < archetypes; a++)
            {
                counts[a] = r.ReadVaru();
            }

            sink.AggregateCell((uint)cell, counts);
        }
    }

    private static void ReadStats<TSink>(ref WireReader r, CatalogPlan plan, uint tick, ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        Span<double> value = stackalloc double[4];
        ReadMetrics(ref r, plan.ServerMetrics, tick, value, ref sink);
        ReadMetrics(ref r, plan.SessionMetrics, tick, value, ref sink);
    }

    private static void ReadMetrics<TSink>(ref WireReader r, MetricPlan[] metrics, uint tick, scoped Span<double> value, ref TSink sink)
        where TSink : ITickSink, allows ref struct
    {
        foreach (var m in metrics)
        {
            for (var i = 0; i < m.ValueCount; i++)
            {
                if (m.Value.ValueKind == FieldValueKind.Skipped)
                {
                    r.Skip(m.Value.Codec.FixedBytes);
                    continue;
                }

                FieldCodec.ReadNumber(ref r, m.Value, tick, value);
                sink.Metric(m, i, value[0]);
            }
        }
    }
}

/// <summary>A value source for encoding a record: a field's value by its wire name.</summary>
public sealed class RecordValues : Dictionary<string, FieldValue>
{
    /// <summary>Creates an empty record.</summary>
    public RecordValues() : base(StringComparer.Ordinal)
    {
    }

    internal FieldValue For(FieldPlan field) => TryGetValue(field.Name, out var v) ? v : throw new ArgumentException($"no value for field '{field.Name}'");
}

/// <summary>An enter record to encode.</summary>
public sealed class EnterRecord
{
    /// <summary>The entity.</summary>
    public uint NetId { get; init; }

    /// <summary>The position, when the archetype is spatial.</summary>
    public double[] Position { get; init; }

    /// <summary>The velocity per tick, when the archetype moves linearly.</summary>
    public double[] Velocity { get; init; }

    /// <summary>The segment's absolute start tick, when the archetype moves.</summary>
    public uint T0 { get; init; }

    /// <summary>The teleport counter, when the archetype moves.</summary>
    public byte Epoch { get; init; }

    /// <summary>Every public field's value.</summary>
    public RecordValues Values { get; init; } = new();
}

/// <summary>A motion segment to encode.</summary>
public sealed class SegmentRecord
{
    /// <summary>The entity.</summary>
    public uint NetId { get; init; }

    /// <summary>The start position.</summary>
    public double[] Position { get; init; }

    /// <summary>The velocity per tick, for the linear model.</summary>
    public double[] Velocity { get; init; }

    /// <summary>The absolute start tick.</summary>
    public uint T0 { get; init; }

    /// <summary>The teleport counter.</summary>
    public byte Epoch { get; init; }
}

/// <summary>A state record to encode: the groups in <see cref="GroupMask"/>, with the values of their fields.</summary>
public sealed class StateRecord
{
    /// <summary>The entity.</summary>
    public uint NetId { get; init; }

    /// <summary>Bit i set when group i is carried.</summary>
    public byte GroupMask { get; init; }

    /// <summary>The values of the carried groups' fields.</summary>
    public RecordValues Values { get; init; } = new();
}

/// <summary>
/// Encodes a <c>TICK</c> message block by block. The object model here is the reference encoder the golden vectors are generated with; the engine's own
/// encoder writes the same bytes straight from cluster data.
/// </summary>
public static class TickWriter
{
    /// <summary>Writes the type byte and the frame header.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="tick">The frame's tick.</param>
    /// <param name="flags">The flags; <see cref="TickFlags.Period"/> is set when <paramref name="periodUs"/> is non-zero.</param>
    /// <param name="periodUs">The elapsed interval's period, or 0.</param>
    public static void WriteHeader(ref WireWriter w, uint tick, TickFlags flags, uint periodUs = 0)
    {
        w.WriteU8(MessageTypes.Tick);
        w.WriteU32(tick);
        if (periodUs != 0)
        {
            flags |= TickFlags.Period;
        }

        w.WriteU8((byte)flags);
        if ((flags & TickFlags.Period) != 0)
        {
            w.WriteU32(periodUs);
        }
    }

    /// <summary>Starts a block; the payload is written next, then <see cref="EndBlock"/>.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="blockType">The block type.</param>
    /// <returns>The mark for <see cref="EndBlock"/>.</returns>
    public static int BeginBlock(ref WireWriter w, byte blockType)
    {
        w.WriteU8(blockType);
        return w.BeginLengthPrefixed();
    }

    /// <summary>Ends a block, patching its length.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="mark">The value <see cref="BeginBlock"/> returned.</param>
    public static void EndBlock(ref WireWriter w, int mark) => w.EndLengthPrefixed(mark);

    /// <summary>Writes a whole <c>REALM</c> block: <paramref name="frame"/>, or <c>REALM(NONE)</c> when it is <see langword="null"/>. First block of a
    /// <c>RESET</c> frame only.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="frame">The session's new realm frame, or <see langword="null"/>.</param>
    public static void WriteRealm(ref WireWriter w, RealmFrame frame)
    {
        var mark = BeginBlock(ref w, BlockTypes.Realm);
        if (frame == null)
        {
            RealmFrame.WriteNone(ref w);
        }
        else
        {
            frame.Write(ref w);
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes a whole <c>ENTITIES</c> block. Each list must be sorted by ascending, distinct netId.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="frameTick">The frame's tick: every segment's start tick must be at or before it, and less than 2¹⁶ ticks older.</param>
    /// <param name="archetype">The archetype.</param>
    /// <param name="enters">Enter records.</param>
    /// <param name="segments">Motion segments.</param>
    /// <param name="states">State records.</param>
    /// <param name="leaves">Leaving netIds.</param>
    /// <param name="frame">The realm frame positions are quantized over; required for a positioned archetype.</param>
    public static void WriteEntities(
        ref WireWriter w,
        uint frameTick,
        ArchetypePlan archetype,
        IReadOnlyList<EnterRecord> enters,
        IReadOnlyList<SegmentRecord> segments,
        IReadOnlyList<StateRecord> states,
        IReadOnlyList<uint> leaves,
        RealmFrame frame = null)
    {
        var mark = BeginBlock(ref w, BlockTypes.Entities);
        w.WriteVaru((uint)archetype.Idx);
        var position = archetype.Position;

        WriteRunHeader(ref w, enters.Count);
        var prev = -1L;
        foreach (var e in enters)
        {
            WriteGap(ref w, ref prev, e.NetId);
            if (position != null)
            {
                FieldCodec.WriteNumber(ref w, position.Pos, e.Position, frame);
                if (position.Moving)
                {
                    WriteSegmentTail(ref w, frameTick, position, e.Velocity, e.T0, e.Epoch);
                }
            }

            FieldCodec.WriteSection(ref w, archetype.OnEnter, e.Values.For, frame);
            foreach (var section in archetype.GroupSections)
            {
                FieldCodec.WriteSection(ref w, section, e.Values.For, frame);
            }
        }

        if (segments.Count > 0 && position is not { Moving: true })
        {
            throw new ArgumentException($"archetype '{archetype.Name}' does not move and cannot carry segments");
        }

        WriteRunHeader(ref w, segments.Count);
        prev = -1;
        foreach (var s in segments)
        {
            WriteGap(ref w, ref prev, s.NetId);
            FieldCodec.WriteNumber(ref w, position.Pos, s.Position, frame);
            WriteSegmentTail(ref w, frameTick, position, s.Velocity, s.T0, s.Epoch);
        }

        WriteRunHeader(ref w, states.Count);
        prev = -1;
        foreach (var s in states)
        {
            if (s.GroupMask == 0 || (s.GroupMask >> archetype.Groups.Length) != 0)
            {
                throw new ArgumentException($"state mask 0x{s.GroupMask:x2} is invalid for {archetype.Groups.Length} group(s)");
            }

            WriteGap(ref w, ref prev, s.NetId);
            w.WriteU8(s.GroupMask);
            for (var g = 0; g < archetype.Groups.Length; g++)
            {
                if ((s.GroupMask & (1 << g)) != 0)
                {
                    FieldCodec.WriteSection(ref w, archetype.GroupSections[g], s.Values.For, frame);
                }
            }
        }

        WriteRunHeader(ref w, leaves.Count);
        prev = -1;
        foreach (var netId in leaves)
        {
            WriteGap(ref w, ref prev, netId);
        }

        EndBlock(ref w, mark);
    }

    /// <summary>
    /// Writes a sub-list that is one run: the run count, then that run's record count. An empty sub-list writes a single zero and no run.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="count">The sub-list's record count.</param>
    /// <remarks>
    /// <b>This writer always produces one run</b>, because it builds a frame from an object model and has no shared blocks to reference. The engine's
    /// encoder is the one that produces several (17 § 18); the grammar is the same either way, which is what lets the golden vectors pin both.
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

    /// <summary>Writes a whole <c>EVENTS</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="events">Each event's type and field values, in emission order.</param>
    /// <param name="frame">The realm frame a position field is quantized over.</param>
    public static void WriteEvents(ref WireWriter w, IReadOnlyList<(MessagePlan Type, RecordValues Values)> events, RealmFrame frame = null)
    {
        var mark = BeginBlock(ref w, BlockTypes.Events);
        w.WriteVaru((uint)events.Count);
        foreach (var (type, values) in events)
        {
            w.WriteVaru((uint)type.Idx);
            FieldCodec.WriteSection(ref w, type.Body, values.For, frame);
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes a <c>SELF</c> block for a session that controls no entity: <paramref name="lastSeq"/> only (W17′).</summary>
    /// <param name="w">The writer.</param>
    /// <param name="lastSeq">The highest drained command sequence.</param>
    public static void WriteSelfNone(ref WireWriter w, ushort lastSeq)
    {
        var mark = BeginBlock(ref w, BlockTypes.Self);
        w.WriteVaru(0);
        w.WriteVaru(0);
        w.WriteU16(lastSeq);
        w.WriteU8(0);
        EndBlock(ref w, mark);
    }

    /// <summary>Writes a <c>SELF</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="archetype">The controlled entity's archetype.</param>
    /// <param name="netId">The controlled entity.</param>
    /// <param name="lastSeq">The highest drained command sequence.</param>
    /// <param name="ownerMask">The owner groups carried.</param>
    /// <param name="values">Their fields' values.</param>
    /// <param name="frame">The realm frame a position field is quantized over.</param>
    public static void WriteSelf(ref WireWriter w, ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask, RecordValues values,
        RealmFrame frame = null)
    {
        if (netId == 0)
        {
            throw new ArgumentException("netId 0 is no controlled entity; write it with WriteSelfNone", nameof(netId));
        }

        if ((ownerMask >> archetype.OwnerGroups.Length) != 0)
        {
            throw new ArgumentException($"owner mask 0x{ownerMask:x2} is invalid for {archetype.OwnerGroups.Length} owner group(s)");
        }

        var mark = BeginBlock(ref w, BlockTypes.Self);
        w.WriteVaru((uint)archetype.Idx);
        w.WriteVaru(netId);
        w.WriteU16(lastSeq);
        w.WriteU8(ownerMask);
        for (var g = 0; g < archetype.OwnerGroups.Length; g++)
        {
            if ((ownerMask & (1 << g)) != 0)
            {
                FieldCodec.WriteSection(ref w, archetype.OwnerSections[g], values.For, frame);
            }
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes an <c>ACKS</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="acks">Each rejection's sequence and reason.</param>
    public static void WriteAcks(ref WireWriter w, IReadOnlyList<(ushort Seq, byte Reason)> acks)
    {
        var mark = BeginBlock(ref w, BlockTypes.Acks);
        w.WriteVaru((uint)acks.Count);
        foreach (var (seq, reason) in acks)
        {
            w.WriteU16(seq);
            w.WriteU8(reason);
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes a <c>SOURCES</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="entries">Each entry's request id, status and error code.</param>
    public static void WriteSources(ref WireWriter w, IReadOnlyList<(ushort RequestId, byte Status, ushort Code)> entries)
    {
        var mark = BeginBlock(ref w, BlockTypes.Sources);
        w.WriteVaru((uint)entries.Count);
        foreach (var (requestId, status, code) in entries)
        {
            w.WriteU16(requestId);
            w.WriteU8(status);
            if (status == SourceStatus.Error)
            {
                w.WriteU16(code);
            }
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes an <c>AGG</c> block. Cells must be in ascending order.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="grid">The grid.</param>
    /// <param name="reset">Whether unlisted cells are now empty.</param>
    /// <param name="cells">Each changed cell's index and its counts, one per grid archetype.</param>
    public static void WriteAggregate(ref WireWriter w, CatalogGrid grid, bool reset, IReadOnlyList<(uint Cell, uint[] Counts)> cells)
    {
        var mark = BeginBlock(ref w, BlockTypes.Agg);
        w.WriteVaru((uint)grid.Idx);
        w.WriteU8(reset ? (byte)1 : (byte)0);
        w.WriteVaru((uint)cells.Count);
        var prev = -1L;
        foreach (var (cell, counts) in cells)
        {
            WriteGap(ref w, ref prev, cell);
            if (counts.Length != (grid.Archetypes?.Length ?? 0))
            {
                throw new ArgumentException($"cell {cell} needs one count per grid archetype");
            }

            foreach (var c in counts)
            {
                w.WriteVaru(c);
            }
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes a <c>STATS</c> block: every server metric's values, then every session metric's, in index order.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="plan">The compiled catalog.</param>
    /// <param name="values">Each metric's values by name, <see cref="MetricPlan.ValueCount"/> of them.</param>
    public static void WriteStats(ref WireWriter w, CatalogPlan plan, IReadOnlyDictionary<string, double[]> values)
    {
        var mark = BeginBlock(ref w, BlockTypes.Stats);
        WriteMetrics(ref w, plan.ServerMetrics, values);
        WriteMetrics(ref w, plan.SessionMetrics, values);
        EndBlock(ref w, mark);
    }

    /// <summary>Writes a <c>DEBUG</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="subBlocks">Each sub-block's type and payload.</param>
    public static void WriteDebug(ref WireWriter w, IReadOnlyList<(byte SubType, byte[] Payload)> subBlocks)
    {
        var mark = BeginBlock(ref w, BlockTypes.Debug);
        foreach (var (subType, payload) in subBlocks)
        {
            w.WriteU8(subType);
            w.WriteVaru((uint)payload.Length);
            w.WriteBytes(payload);
        }

        EndBlock(ref w, mark);
    }

    /// <summary>Writes an <c>EXT</c> block.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="appTypeId">The application's payload type.</param>
    /// <param name="payload">The payload.</param>
    public static void WriteExt(ref WireWriter w, uint appTypeId, ReadOnlySpan<byte> payload)
    {
        var mark = BeginBlock(ref w, BlockTypes.Ext);
        w.WriteVaru(appTypeId);
        w.WriteBytes(payload);
        EndBlock(ref w, mark);
    }

    private static void WriteMetrics(ref WireWriter w, MetricPlan[] metrics, IReadOnlyDictionary<string, double[]> values)
    {
        foreach (var m in metrics)
        {
            if (!values.TryGetValue(m.Name, out var v) || v.Length != m.ValueCount)
            {
                throw new ArgumentException($"metric '{m.Name}' needs {m.ValueCount} value(s)");
            }

            foreach (var x in v)
            {
                FieldCodec.WriteNumber(ref w, m.Value, [Saturate(x, m.Value.Kind)]);
            }
        }
    }

    // W25: a metric never carries NaN or an infinity — a HUD would render "NaN ms" — and a half saturates at its largest finite value.
    private static double Saturate(double x, CodecKind kind)
    {
        if (kind is not (CodecKind.F16 or CodecKind.F32))
        {
            return x;
        }

        var max = kind == CodecKind.F16 ? 65504.0 : float.MaxValue;
        return double.IsNaN(x) ? 0 : x > max ? max : x < -max ? -max : x;
    }

    private static void WriteSegmentTail(ref WireWriter w, uint frameTick, PositionPlan position, double[] velocity, uint t0, byte epoch)
    {
        // W9: tickLo is exact only for a past tick less than 2¹⁶ old; a stale segment would otherwise decode 65 536 ticks off, silently.
        if (t0 > frameTick || frameTick - t0 >= 65536)
        {
            throw new ArgumentException($"segment start tick {t0} is not within the 2¹⁶ ticks before frame tick {frameTick}");
        }

        if (position.Linear)
        {
            FieldCodec.WriteNumber(ref w, position.Vel, velocity);
        }

        w.WriteU16((ushort)(t0 & 0xFFFF));
        w.WriteU8(epoch);
    }

    private static void WriteGap(ref WireWriter w, ref long prev, uint id)
    {
        if (id <= prev)
        {
            throw new ArgumentException($"ids must be ascending and distinct: {id} after {prev}");
        }

        w.WriteVaru((uint)(id - prev - 1));
        prev = id;
    }
}
