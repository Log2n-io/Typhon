using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Backup.DrillSchemaFixture;

/// <summary>The application's component. Layout B adds <see cref="Z"/> under the same revision and assembly version: another build, same numbers.</summary>
[Component("Typhon.DrillFixture.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DrillPosition
{
    [Field] public int X;
    [Field] public int Y;
#if DRILL_LAYOUT_B
    [Field] public int Z;
#endif
}

#if DRILL_LAYOUT_C
/// <summary>Layout C's addition: a component the database never stored, carried by <see cref="DrillUnit"/> — another archetype set, same version.</summary>
[Component("Typhon.DrillFixture.Tag", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct DrillTag
{
    [Field] public int Value;
}
#endif

/// <summary>The application's archetype: its records are what a point's WAL tail holds and only this assembly can replay.</summary>
[Archetype]
public partial class DrillUnit : Archetype<DrillUnit>
{
    public static readonly Comp<DrillPosition> Position = Register<DrillPosition>();
#if DRILL_LAYOUT_C
    public static readonly Comp<DrillTag> Tag = Register<DrillTag>();
#endif
}

/// <summary>What the application does to its database, called by the tests through reflection (this assembly is never referenced).</summary>
public static class DrillSeeder
{
    /// <summary>Registers the application's schema and completes the open.</summary>
    public static void Open(DatabaseEngine engine)
    {
        engine.RegisterComponentFromAccessor<DrillPosition>();
        engine.InitializeArchetypes();
    }

    /// <summary>Spawns <paramref name="count"/> units at X = <paramref name="seed"/> + i, durably.</summary>
    public static void Spawn(DatabaseEngine engine, int count, int seed)
    {
        using var t = engine.CreateQuickTransaction(DurabilityMode.Immediate);
        for (var i = 0; i < count; i++)
        {
            t.Spawn<DrillUnit>(DrillUnit.Position.Set(new DrillPosition { X = seed + i, Y = 1 }));
        }

        t.Commit();
    }

    /// <summary>The units' X values, sorted.</summary>
    public static int[] Xs(DatabaseEngine engine)
    {
        using var t = engine.CreateQuickTransaction();
        var xs = new System.Collections.Generic.List<int>();
        foreach (var id in t.Query<DrillUnit>().Execute())
        {
            xs.Add(t.Open(id).Read(DrillUnit.Position).X);
        }

        xs.Sort();
        return xs.ToArray();
    }
}
