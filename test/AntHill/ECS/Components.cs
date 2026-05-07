using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace AntHill;

// ── Ant components ─────────────────────────────────────────────────────────
//
// Position used to bundle (Bounds, VelocityX, VelocityY) into one component. RFC 07 enforces
// W×W conflicts at component granularity (Q2) — bundling them meant the velocity-writers
// (Brain, FoodDetect, Metabolism on respawn) and the position-writers (MoveAll) couldn't run in
// the same phase even though they touch logically distinct fields. Splitting into WorldBounds
// (the AABB the spatial index hangs off) and Velocity (the integration delta) makes the
// dependency graph reflect what actually flows where.

[Component("AntHill.WorldBounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct WorldBounds
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

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
}

[Component("AntHill.Velocity", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Velocity
{
    [Field] public float X;  // ready-to-use: already includes speed multiplier
    [Field] public float Y;
}

[Component("AntHill.Genetics", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct Genetics
{
    [Field] public float Speed;              // base movement speed multiplier (0.5 - 1.5)
    [Field] public float HomeNestX;          // birth nest position
    [Field] public float HomeNestY;
    [Field] public float BaseEnergy;         // max energy capacity
    [Field] public int EatAmount;            // food units consumed per eat event
    [Field] public int HomeNestIndex;        // index into nest arrays
}

[Component("AntHill.AntState", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct AntState
{
    [Field] public byte State;      // 0=Foraging, >=1 = Returning (value = 1 + foodSourceIndex)
    [Field] public float Energy;    // depletes over time; 0 = death → respawn

    public const byte Foraging = 0;
    // Returning: State >= 1, food source index = State - 1
    public bool IsReturning => State >= 1;
    public int FoodSourceIndex => State - 1;
    public static byte ReturningFrom(int foodIdx) => (byte)(foodIdx + 1);
}

// ── Food components ────────────────────────────────────────────────────────

[Component("AntHill.FoodSource", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct FoodSource
{
    [Field]
    [SpatialIndex(20.0f)]
    public AABB2F Bounds;

    [Field] public float RemainingFood;

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
}

// ── Nest components ────────────────────────────────────────────────────────

[Component("AntHill.NestInfo", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct NestInfo
{
    [Field]
    [SpatialIndex(30.0f)]
    public AABB2F Bounds;

    [Field] public float FoodStored;
    [Field] public int Population;

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
}
