using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

// Four components, all SingleVersion, 48 bytes of payload per ship. Every one of them is written by
// exactly one system per phase and read cross-entity only in a phase where nobody writes it — that is
// what makes the whole tick parallel with zero synchronisation (DESIGN.md §1, §5.1).

/// <summary>
/// Position, as the spatial-indexed AABB itself. A ship is a point: <c>MinX == MaxX</c> on every axis.
/// There is deliberately no separate Position component — it would duplicate this and need keeping in sync.
/// </summary>
[Component("SimpleSpaceBattle.Hull", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct HullComponent
{
    /// <summary>Fat-AABB margin of 8 units ≈ 4 ticks of cruise movement before a re-insert is forced.</summary>
    [Field]
    [SpatialIndex(8f)]
    public AABB3F Bounds;

    public float X
    {
        readonly get => Bounds.MinX;
        set { Bounds.MinX = value; Bounds.MaxX = value; }
    }

    public float Y
    {
        readonly get => Bounds.MinY;
        set { Bounds.MinY = value; Bounds.MaxY = value; }
    }

    public float Z
    {
        readonly get => Bounds.MinZ;
        set { Bounds.MinZ = value; Bounds.MaxZ = value; }
    }
}

/// <summary>Velocity in units/second. Magnitude is folded in — there is no separate speed scalar.</summary>
[Component("SimpleSpaceBattle.Motion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct MotionComponent
{
    [Field] public float X;
    [Field] public float Y;
    [Field] public float Z;
}

/// <summary>
/// Health, as an unsigned integer. Integer specifically: damage from N attackers is summed in a
/// nondeterministic order across workers, and integer addition is associative where float is not (§9).
/// </summary>
[Component("SimpleSpaceBattle.Vitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct VitalsComponent
{
    [Field] public uint Health;
}

/// <summary>
/// The current target, as the raw packed <c>EntityId</c> the engine stores in the cluster's id array —
/// the same value <c>ClusterSpatialQueryResult.EntityId</c> carries, so a hit can be compared to it directly.
/// <para>
/// It is a raw <c>long</c> rather than an <c>EntityLink&lt;Ship&gt;</c> because <c>EntityId.FromRaw</c> is
/// <c>internal</c>: a spatial hit cannot be turned back into an <c>EntityId</c> through the public API, so a
/// typed link could not be built from an acquisition scan at all. See DESIGN.md §14.
/// </para>
/// <para><c>0</c> is the unlocked sentinel — a live entity's packed id is never zero.</para>
/// </summary>
[Component("SimpleSpaceBattle.Targeting", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct TargetingComponent
{
    [Field] public long TargetRawId;

    public const long Unlocked = 0L;

    public readonly bool IsLocked => TargetRawId != Unlocked;
}
