using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Component types exported by the Workbench fixture schema DLL. Deliberately narrow (no test-only
/// variants sharing schema names, no intentionally-broken types) so the Workbench can load this
/// assembly cleanly and the Schema Browser shows a focused surface.
///
/// Paired with <see cref="FixtureArchetypes"/> which declares the archetypes that group these
/// components into entity shapes.
/// </summary>

[Component("Typhon.Workbench.Fixture.CompA", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompA
{
    public int A;
    public float B;
    public double C;

    public CompA(int a, float b, double c)
    {
        A = a;
        B = b;
        C = c;
    }
}

[Component("Typhon.Workbench.Fixture.CompB", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompB
{
    public int A;
    public float B;

    public CompB(int a, float b)
    {
        A = a;
        B = b;
    }
}

[Component("Typhon.Workbench.Fixture.CompC", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompC
{
    public String64 Name;

    public CompC(string name)
    {
        Name = default;
        Name.AsString = name;
    }
}

/// <summary>Component with mixed unique / non-unique indexes — exercises the Schema Inspector's Index panel.</summary>
[Component("Typhon.Workbench.Fixture.CompD", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompD
{
    [Index(AllowMultiple = true)] public float Weight;
    [Index] public int Key;
    public double Raw;
}

/// <summary>Guild — indexed by level (non-unique) and capacity (unique). Targeted by <see cref="CompPlayer"/> via FK.</summary>
[Component("Typhon.Workbench.Fixture.Guild", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompGuild
{
    [Index(AllowMultiple = true)] public int Level;
    [Index] public int MemberCap;
}

[Component("Typhon.Workbench.Fixture.Player", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompPlayer
{
    [Index(AllowMultiple = true), ForeignKey(typeof(CompGuild))] public long GuildId;
    [Index(AllowMultiple = true)] public int Active;
}

/// <summary>SingleVersion component — its archetype is cluster-eligible, so it lands in a cluster segment and
/// exercises the Database File Map's A6 intra-cluster fill / entity sub-grid rendering.</summary>
[Component("Typhon.Workbench.Fixture.Particle", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct CompParticle
{
    public float X, Y, Z;

    public CompParticle(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}
