using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Adapters;

// C0 ladder components — a single long value is the minimal unit of work (matched to the KV floor).
// Shape N / Shape Y (with the 8×String64 payload) arrive with the A-scenarios.

[Component("Cb.SvVal", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SvVal
{
    [Field] public long Value;
}

[Component("Cb.VVal", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct VVal
{
    [Field] public long Value;
}

[Archetype]
internal class SvValArch : Archetype<SvValArch>
{
    public static readonly Comp<SvVal> Data = Register<SvVal>();
}

[Archetype]
internal class VValArch : Archetype<VValArch>
{
    public static readonly Comp<VVal> Data = Register<VVal>();
}

// A6 (YCSB-E) — Versioned component with a B+Tree-indexed long Key, so Typhon can do an ordered range scan (its EntityMap
// is a hash; the secondary index provides the key ordering). `[Index]` makes Key stored + indexed; Value is a plain field.
[Component("Cb.YcsbRec", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct YcsbRec
{
    [Index] public long Key;
    [Field] public long Value;
}

[Archetype]
internal class YcsbArch : Archetype<YcsbArch>
{
    public static readonly Comp<YcsbRec> Data = Register<YcsbRec>();
}

// C3 — cross-component atomic commit: three distinct SingleVersion components on one entity. A Commit-discipline tx that
// writes all three produces ONE WAL record spanning them (vs the competitors' N-key batches).
[Component("Cb.T1", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct T1 { [Field] public long V; }

[Component("Cb.T2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct T2 { [Field] public long V; }

[Component("Cb.T3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct T3 { [Field] public long V; }

[Archetype]
internal class TripArch : Archetype<TripArch>
{
    public static readonly Comp<T1> A = Register<T1>();
    public static readonly Comp<T2> B = Register<T2>();
    public static readonly Comp<T3> C = Register<T3>();
}

// C5 — spatial. A SingleVersion component with a B+Tree-grid-indexed 2D bounding box ([SpatialIndex]), so Typhon serves
// AABB / radius queries from its native cluster spatial grid. A point is a degenerate box (min==max).
[Component("Cb.SpPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SpPos
{
    [Field]
    [SpatialIndex(5.0f)] // margin = fat-AABB movement hysteresis
    public AABB2F Bounds;
}

[Archetype]
internal class SpArch : Archetype<SpArch>
{
    public static readonly Comp<SpPos> Pos = Register<SpPos>();
}
