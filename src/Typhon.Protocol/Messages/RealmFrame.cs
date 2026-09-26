using System;

namespace Typhon.Protocol;

/// <summary>
/// The realm a session's positions are framed in (<c>typhon.3</c>, 12-realms § 5.2): what the <c>REALM</c> block carries, and what every realm-framed
/// codec — <c>pos2</c>, <c>pos3</c>, the <c>AGG</c> grid — is quantized over and decoded with (SUB-30).
/// </summary>
/// <remarks>
/// <para>
/// <c>REALM := u16 realmId | u16 generation | u8 flags [unless NONE: varu kindIdx | u32 appTag | u8 posBits | f64 cellM | f64 min[3] | f64 max[3]]</c>.
/// Always three axes: a flat realm has <see cref="Deep"/> clear and a <c>pos2</c> uses axes 0 and 1.
/// </para>
/// <para>Immutable: a decoded frame can be cached by <c>(realmId, generation)</c> and shared by every session in the realm.</para>
/// </remarks>
public sealed class RealmFrame : IEquatable<RealmFrame>
{
    /// <summary>Flags bit 0: the session is in no realm; nothing follows the flags.</summary>
    public const byte FlagNone = 1;

    /// <summary>Flags bit 1: the realm is three-dimensional.</summary>
    public const byte FlagDeep = 2;

    /// <summary>The realm id a <c>REALM(NONE)</c> carries.</summary>
    public const ushort NoRealm = 0xFFFF;

    /// <summary>
    /// Bits per axis of a realm-framed field a CLIENT sends (<c>COMMANDS</c>): always 32, whatever the realm's own width, because the transport parses a
    /// command without reading the session's realm (SUB-05) and so cannot know that width.
    /// </summary>
    public const int CommandPositionBits = 32;

    private readonly double[] _min;
    private readonly double[] _max;
    private readonly double[] _step;
    private RealmFrame _forCommands;

    /// <summary>Builds a frame, validating it exactly as a decoder would.</summary>
    /// <param name="realmId">The realm.</param>
    /// <param name="generation">The realm catalog's generation of that id.</param>
    /// <param name="kindIdx">The realm kind's index in the catalog's <c>realmKinds</c>.</param>
    /// <param name="appTag">Opaque to the engine; the client picks its scene by it.</param>
    /// <param name="positionBits">16, 24 or 32.</param>
    /// <param name="cellM">The realm's replication cell, in metres.</param>
    /// <param name="deep">Whether the realm has a third axis.</param>
    /// <param name="min">Three lower bounds.</param>
    /// <param name="max">Three upper bounds.</param>
    /// <exception cref="ArgumentException">A value a decoder refuses (§ 5.2).</exception>
    public RealmFrame(ushort realmId, ushort generation, int kindIdx, uint appTag, int positionBits, double cellM, bool deep, ReadOnlySpan<double> min,
        ReadOnlySpan<double> max)
    {
        var problem = Problem(realmId, kindIdx, int.MaxValue, positionBits, cellM, min, max);
        if (problem != null)
        {
            throw new ArgumentException(problem);
        }

        RealmId = realmId;
        Generation = generation;
        KindIdx = kindIdx;
        AppTag = appTag;
        PositionBits = positionBits;
        CellM = cellM;
        Deep = deep;
        _min = min[..3].ToArray();
        _max = max[..3].ToArray();
        _step = new double[3];
        for (var i = 0; i < 3; i++)
        {
            _step[i] = WireMath.QuantStep(_min[i], _max[i], positionBits);
        }
    }

    /// <summary>The realm.</summary>
    public ushort RealmId { get; }

    /// <summary>The realm catalog's generation of <see cref="RealmId"/>: a reused id is a new realm.</summary>
    public ushort Generation { get; }

    /// <summary>The realm kind's index in the catalog's <c>realmKinds</c>.</summary>
    public int KindIdx { get; }

    /// <summary>The application's tag for the realm, opaque to the engine.</summary>
    public uint AppTag { get; }

    /// <summary>Bits per position axis: 16, 24 or 32.</summary>
    public int PositionBits { get; }

    /// <summary>The realm's replication cell side, in metres; an <c>AGG</c> tile is <c>tileCells</c> of them.</summary>
    public double CellM { get; }

    /// <summary>Whether the realm is three-dimensional.</summary>
    public bool Deep { get; }

    /// <summary>The lower bounds, three axes.</summary>
    public ReadOnlySpan<double> Min => _min;

    /// <summary>The upper bounds, three axes.</summary>
    public ReadOnlySpan<double> Max => _max;

    /// <summary>The position quantum per axis at <see cref="PositionBits"/>, exactly as <see cref="WireMath.QuantStep"/> computes it.</summary>
    public ReadOnlySpan<double> Step => _step;

    /// <summary>Bytes per position axis.</summary>
    public int PositionBytes => PositionBits / 8;

    /// <summary>
    /// This frame at <see cref="CommandPositionBits"/>: what a <c>COMMANDS</c> message's realm-framed fields are encoded and decoded over — the realm's
    /// bounds, at a width the transport knows without reading the realm.
    /// </summary>
    public RealmFrame ForCommands => PositionBits == CommandPositionBits
        ? this
        : _forCommands ??= new RealmFrame(RealmId, Generation, KindIdx, AppTag, CommandPositionBits, CellM, Deep, _min, _max);

    /// <summary>An <c>AGG</c> grid's cell count on <paramref name="axis"/>: <c>⌈(max − min) / (tileCells · cellM)⌉</c>, and 1 on axis 2 of a flat realm.</summary>
    /// <param name="axis">0, 1 or 2.</param>
    /// <param name="tileCells">The grid's tile, in replication cells.</param>
    /// <returns>The cell count, at least 1.</returns>
    public int AggregateDim(int axis, int tileCells)
    {
        if (axis == 2 && !Deep)
        {
            return 1;
        }

        var dims = Math.Ceiling((_max[axis] - _min[axis]) / (tileCells * CellM));
        return dims < 1 ? 1 : dims > int.MaxValue ? int.MaxValue : (int)dims;
    }

    /// <summary>An <c>AGG</c> grid's cell count over all three axes, or <see cref="long.MaxValue"/> when it overflows.</summary>
    public long AggregateCellCount(int tileCells)
    {
        long count = 1;
        for (var axis = 0; axis < 3; axis++)
        {
            count *= AggregateDim(axis, tileCells);
            if (count > int.MaxValue)
            {
                return long.MaxValue;
            }
        }

        return count;
    }

    /// <summary>Decodes a <c>REALM</c> block's content.</summary>
    /// <param name="reader">The block's reader.</param>
    /// <param name="realmKindCount">How many kinds the catalog declares; a <c>kindIdx</c> at or past it is refused.</param>
    /// <returns>The frame, or <see langword="null"/> for <c>REALM(NONE)</c>.</returns>
    /// <exception cref="WireFormatException">A value § 5.2 refuses (1007).</exception>
    public static RealmFrame Read(ref WireReader reader, int realmKindCount)
    {
        var realmId = reader.ReadU16();
        var generation = reader.ReadU16();
        var flags = reader.ReadU8();
        if ((flags & ~(FlagNone | FlagDeep)) != 0)
        {
            throw WireFormatException.Malformed($"REALM flags 0x{flags:x2} set a reserved bit");
        }

        if ((flags & FlagNone) != 0)
        {
            if (flags != FlagNone)
            {
                throw WireFormatException.Malformed("REALM(NONE) sets another flag");
            }

            return null;
        }

        var kindIdx = reader.ReadVaruAtMost(int.MaxValue, "realm kind index");
        var appTag = reader.ReadU32();
        var bits = reader.ReadU8();
        var cellM = reader.ReadF64();
        Span<double> min = stackalloc double[3];
        Span<double> max = stackalloc double[3];
        for (var i = 0; i < 3; i++)
        {
            min[i] = reader.ReadF64();
        }

        for (var i = 0; i < 3; i++)
        {
            max[i] = reader.ReadF64();
        }

        var problem = Problem(realmId, kindIdx, realmKindCount, bits, cellM, min, max);
        if (problem != null)
        {
            throw WireFormatException.Malformed(problem);
        }

        return new RealmFrame(realmId, generation, kindIdx, appTag, bits, cellM, (flags & FlagDeep) != 0, min, max);
    }

    /// <summary>Encodes this frame as a <c>REALM</c> block's content.</summary>
    public void Write(ref WireWriter writer)
    {
        writer.WriteU16(RealmId);
        writer.WriteU16(Generation);
        writer.WriteU8(Deep ? FlagDeep : (byte)0);
        writer.WriteVaru((uint)KindIdx);
        writer.WriteU32(AppTag);
        writer.WriteU8((byte)PositionBits);
        writer.WriteF64(CellM);
        for (var i = 0; i < 3; i++)
        {
            writer.WriteF64(_min[i]);
        }

        for (var i = 0; i < 3; i++)
        {
            writer.WriteF64(_max[i]);
        }
    }

    /// <summary>Encodes <c>REALM(NONE)</c>: the session is in no realm.</summary>
    public static void WriteNone(ref WireWriter writer)
    {
        writer.WriteU16(NoRealm);
        writer.WriteU16(0);
        writer.WriteU8(FlagNone);
    }

    /// <inheritdoc/>
    public bool Equals(RealmFrame other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return RealmId == other.RealmId && Generation == other.Generation && KindIdx == other.KindIdx && AppTag == other.AppTag
            && PositionBits == other.PositionBits && CellM.Equals(other.CellM) && Deep == other.Deep && Min.SequenceEqual(other.Min)
            && Max.SequenceEqual(other.Max);
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => Equals(obj as RealmFrame);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(RealmId, Generation, KindIdx, AppTag, PositionBits, CellM, Deep);

    private static string Problem(ushort realmId, int kindIdx, int realmKindCount, int bits, double cellM, ReadOnlySpan<double> min,
        ReadOnlySpan<double> max)
    {
        if (realmId == NoRealm)
        {
            return $"realm id {NoRealm} is REALM(NONE)'s";
        }

        if (kindIdx < 0 || kindIdx >= realmKindCount)
        {
            return $"realm kind index {kindIdx} is out of range ({realmKindCount} kind(s))";
        }

        if (bits is not (16 or 24 or 32))
        {
            return $"REALM posBits {bits} is not 16, 24 or 32";
        }

        if (!double.IsFinite(cellM) || cellM <= 0)
        {
            return $"REALM cellM {cellM} is not a positive finite number";
        }

        if (min.Length < 3 || max.Length < 3)
        {
            return "REALM needs three bounds per side";
        }

        for (var i = 0; i < 3; i++)
        {
            if (!double.IsFinite(min[i]) || !double.IsFinite(max[i]) || min[i] >= max[i])
            {
                return $"REALM bounds on axis {i} are not a finite min < max ({min[i]}, {max[i]})";
            }
        }

        return null;
    }
}
