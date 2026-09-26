using System;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// What one realm gives one archetype's position codec (12-realms § 5.3, SUB-30): the bounds and the width a <c>pos2</c> / <c>pos3</c> value is quantized
/// over and decoded with.
/// </summary>
/// <remarks>
/// A position is realm-framed: its bits mean a place only together with the frame of the realm it was quantized in. The velocity is not (its unit is
/// absolute, W5), so a frame carries nothing of it.
/// </remarks>
internal sealed class PositionFrame
{
    /// <summary>Axes the archetype's position carries: 2 or 3.</summary>
    public readonly int Dims;

    /// <summary>Bits per axis: 16, 24 or 32.</summary>
    public readonly int Bits;

    /// <summary>Bytes per axis, <c>Bits / 8</c>.</summary>
    public readonly int AxisBytes;

    /// <summary>Per axis: the lower bound.</summary>
    public readonly double[] Min;

    /// <summary>Per axis: the upper bound.</summary>
    public readonly double[] Max;

    /// <summary>Per axis: the quantum, exactly as <see cref="WireMath.QuantStep"/> computes it.</summary>
    public readonly double[] Step;

    /// <summary>The finest axis quantum.</summary>
    public readonly double FinestStep;

    private PositionFrame(int dims, int bits, double[] min, double[] max)
    {
        Dims = dims;
        Bits = bits;
        AxisBytes = bits / 8;
        Min = min;
        Max = max;
        Step = new double[dims];
        FinestStep = double.MaxValue;
        for (var axis = 0; axis < dims; axis++)
        {
            Step[axis] = WireMath.QuantStep(min[axis], max[axis], bits);
            FinestStep = Math.Min(FinestStep, Step[axis]);
        }
    }

    /// <summary>The frame a realm's grid gives a position of <paramref name="dims"/> axes at <paramref name="bits"/> bits.</summary>
    public static PositionFrame Over(in SpatialGridConfig grid, int dims, int bits)
    {
        var min = new double[dims];
        var max = new double[dims];
        for (var axis = 0; axis < dims; axis++)
        {
            min[axis] = axis == 0 ? grid.WorldMin.X : axis == 1 ? grid.WorldMin.Y : grid.WorldMin.Z;
            max[axis] = axis == 0 ? grid.WorldMax.X : axis == 1 ? grid.WorldMax.Y : grid.WorldMax.Z;
        }

        return new PositionFrame(dims, bits, min, max);
    }

    /// <summary>Whether this frame quantizes exactly as <paramref name="codec"/> does: same width, same bounds on every axis.</summary>
    public bool Matches(CatalogCodec codec)
    {
        if (codec == null || codec.Bits != Bits || codec.Min == null || codec.Max == null || codec.Min.Length != Dims || codec.Max.Length != Dims)
        {
            return false;
        }

        for (var axis = 0; axis < Dims; axis++)
        {
            if (codec.Min[axis] != Min[axis] || codec.Max[axis] != Max[axis])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One realm's replication codecs (Realms R4.2): per compiled plan, the <see cref="PositionFrame"/> its positions are quantized over in this realm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built from the realm's grid, never from the catalog.</b> The catalog is fixed at <c>WELCOME</c> and realms come and go at run time, so the catalog's
/// bounds can only ever describe one of them; for realm 0 they coincide, which is what keeps a single-realm application's bytes unchanged.
/// </para>
/// <para>
/// <b>One served realm today.</b> Replication serves realm 0 only until sessions are placed in realms (R4.3): a cluster of another realm is never projected,
/// so its entities are never known to a session (SUB-28, from the side that already holds).
/// </para>
/// </remarks>
internal sealed class RealmCodecs
{
    /// <summary>The realm these codecs frame.</summary>
    public readonly ushort Realm;

    /// <summary>Per plan index: the position frame, or <see langword="null"/> for an archetype that replicates no position.</summary>
    public readonly PositionFrame[] ByPlan;

    private RealmCodecs(ushort realm, PositionFrame[] byPlan)
    {
        Realm = realm;
        ByPlan = byPlan;
    }

    /// <summary>The codecs of <paramref name="realm"/>, whose grid is <paramref name="grid"/>, for every plan that replicates a position.</summary>
    /// <param name="realm">The realm.</param>
    /// <param name="grid">The realm's spatial grid: its bounds are the frame's.</param>
    /// <param name="plans">The compiled plans, indexed as everywhere in replication.</param>
    /// <param name="positionBits">The realm's position width: 16, 24 or 32 (12-realms § 5.5).</param>
    public static RealmCodecs Create(ushort realm, in SpatialGridConfig grid, CompiledProjectionPlan[] plans, int positionBits)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (positionBits is not (16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(positionBits), positionBits, "A position is 16, 24 or 32 bits per axis");
        }

        var byPlan = new PositionFrame[plans.Length];
        for (var a = 0; a < plans.Length; a++)
        {
            var position = plans[a].Position;
            if (position != null)
            {
                // The block layout and the entities encoder use the plan's width per axis for the previous and enter positions: a wider realm would cache
                // codes truncated, a narrower one would be copied past its bytes. Per-realm widths need the layout and the encoder to take the realm's
                // (12-realms § 5.5, V2); until then a realm's width is the plan's.
                if (position.Frame != null && positionBits != position.Frame.AxisBytes * 8)
                {
                    throw new NotSupportedException(
                        $"Realm {realm}: {positionBits}-bit positions, where the block layout of plan {a} is {position.Frame.AxisBytes * 8} bits per axis");
                }

                byPlan[a] = PositionFrame.Over(in grid, position.Dims, positionBits);
            }
        }

        return new RealmCodecs(realm, byPlan);
    }

    /// <summary>
    /// The codecs every plan's own frame describes — realm 0's, compiled with the plans. What replication uses when nothing gave it a realm's grid.
    /// </summary>
    public static RealmCodecs FromPlans(CompiledProjectionPlan[] plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var byPlan = new PositionFrame[plans.Length];
        for (var a = 0; a < plans.Length; a++)
        {
            byPlan[a] = plans[a].Position?.Frame;
        }

        return new RealmCodecs(RealmId.Default.Value, byPlan);
    }
}
