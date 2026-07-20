using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// SingleVersion twins of the EcsPosition/Velocity/Health + EcsUnit/EcsSoldier schema.
//
// StorageMode is fixed per (component name, revision) — there is no per-registration
// override (see rules/ecs.md). Tests that need the SingleVersion storage path (change
// filters, view deltas, subscription/output deltas — all of which require the SV
// DirtyBitmap) use THESE declared-SV types instead of overriding the Versioned Ecs*
// components. Field layouts and constructors mirror the Versioned originals.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.SvEcs.Position", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvEcsPosition
{
    public float X, Y, Z;

    public SvEcsPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
}

[Component("Typhon.Test.SvEcs.Velocity", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvEcsVelocity
{
    public float Dx, Dy, Dz;

    public SvEcsVelocity(float dx, float dy, float dz) { Dx = dx; Dy = dy; Dz = dz; }
}

[Component("Typhon.Test.SvEcs.Health", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvEcsHealth
{
    public int Current, Max;

    public SvEcsHealth(int current, int max) { Current = current; Max = max; }
}

[Archetype]
partial class SvEcsUnit : Archetype<SvEcsUnit>
{
    public static readonly Comp<SvEcsPosition> Position = Register<SvEcsPosition>();
    public static readonly Comp<SvEcsVelocity> Velocity = Register<SvEcsVelocity>();
}

[Archetype]
partial class SvEcsSoldier : Archetype<SvEcsSoldier, SvEcsUnit>
{
    public static readonly Comp<SvEcsHealth> Health = Register<SvEcsHealth>();
}
