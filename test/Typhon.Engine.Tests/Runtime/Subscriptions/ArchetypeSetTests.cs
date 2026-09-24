using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Seventy archetypes that exist only to push a real one's plan index past 63 (09 § 12, D1). A plan index runs over every declared projection, observed or
// not, and the 64-bit mask a session carried before step 2.0 aliased index 70 onto index 6: a shift count is masked to six bits, 1UL << 70 == 1UL << 6.
//
// Their position component is theirs alone and only this fixture registers it: an engine initialises an archetype only when every component it holds is
// registered, so every other engine in the assembly skips all seventy. Sharing ProjBounds made each one carry them, and the heaviest push fixtures then
// hit page-cache back-pressure.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Proj.WideBounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjWideBounds
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public float Speed;
}

[Archetype]
partial class ProjWide00 : Archetype<ProjWide00>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide01 : Archetype<ProjWide01>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide02 : Archetype<ProjWide02>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide03 : Archetype<ProjWide03>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide04 : Archetype<ProjWide04>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide05 : Archetype<ProjWide05>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide06 : Archetype<ProjWide06>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide07 : Archetype<ProjWide07>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide08 : Archetype<ProjWide08>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide09 : Archetype<ProjWide09>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide10 : Archetype<ProjWide10>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide11 : Archetype<ProjWide11>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide12 : Archetype<ProjWide12>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide13 : Archetype<ProjWide13>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide14 : Archetype<ProjWide14>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide15 : Archetype<ProjWide15>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide16 : Archetype<ProjWide16>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide17 : Archetype<ProjWide17>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide18 : Archetype<ProjWide18>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide19 : Archetype<ProjWide19>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide20 : Archetype<ProjWide20>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide21 : Archetype<ProjWide21>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide22 : Archetype<ProjWide22>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide23 : Archetype<ProjWide23>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide24 : Archetype<ProjWide24>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide25 : Archetype<ProjWide25>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide26 : Archetype<ProjWide26>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide27 : Archetype<ProjWide27>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide28 : Archetype<ProjWide28>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide29 : Archetype<ProjWide29>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide30 : Archetype<ProjWide30>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide31 : Archetype<ProjWide31>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide32 : Archetype<ProjWide32>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide33 : Archetype<ProjWide33>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide34 : Archetype<ProjWide34>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide35 : Archetype<ProjWide35>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide36 : Archetype<ProjWide36>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide37 : Archetype<ProjWide37>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide38 : Archetype<ProjWide38>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide39 : Archetype<ProjWide39>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide40 : Archetype<ProjWide40>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide41 : Archetype<ProjWide41>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide42 : Archetype<ProjWide42>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide43 : Archetype<ProjWide43>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide44 : Archetype<ProjWide44>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide45 : Archetype<ProjWide45>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide46 : Archetype<ProjWide46>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide47 : Archetype<ProjWide47>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide48 : Archetype<ProjWide48>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide49 : Archetype<ProjWide49>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide50 : Archetype<ProjWide50>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide51 : Archetype<ProjWide51>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide52 : Archetype<ProjWide52>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide53 : Archetype<ProjWide53>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide54 : Archetype<ProjWide54>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide55 : Archetype<ProjWide55>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide56 : Archetype<ProjWide56>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide57 : Archetype<ProjWide57>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide58 : Archetype<ProjWide58>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide59 : Archetype<ProjWide59>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide60 : Archetype<ProjWide60>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide61 : Archetype<ProjWide61>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide62 : Archetype<ProjWide62>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide63 : Archetype<ProjWide63>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide64 : Archetype<ProjWide64>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide65 : Archetype<ProjWide65>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide66 : Archetype<ProjWide66>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide67 : Archetype<ProjWide67>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide68 : Archetype<ProjWide68>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

[Archetype]
partial class ProjWide69 : Archetype<ProjWide69>
{
    public static readonly Comp<ProjWideBounds> Bounds = Register<ProjWideBounds>();
}

/// <summary>
/// A session's archetype set is its profile's plan indices, 256 bits wide (09 § 12, step 2.0): an event reaches only sessions whose profile observes its
/// archetype, whatever the plan index, and a profile may observe more than 64 archetypes.
/// </summary>
[TestFixture]
[NonParallelizable]
class ArchetypeSetTests : TestBase<ArchetypeSetTests>
{
    private const double Radius = 45d;

    private const int FillTicks = 6;

    /// <summary>Seventy projections, plan indices 0–69, then the creature at 70.</summary>
    private static void DeclareWide(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjWide00>(a => a.Motion(ProjWide00.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide01>(a => a.Motion(ProjWide01.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide02>(a => a.Motion(ProjWide02.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide03>(a => a.Motion(ProjWide03.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide04>(a => a.Motion(ProjWide04.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide05>(a => a.Motion(ProjWide05.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide06>(a => a.Motion(ProjWide06.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide07>(a => a.Motion(ProjWide07.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide08>(a => a.Motion(ProjWide08.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide09>(a => a.Motion(ProjWide09.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide10>(a => a.Motion(ProjWide10.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide11>(a => a.Motion(ProjWide11.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide12>(a => a.Motion(ProjWide12.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide13>(a => a.Motion(ProjWide13.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide14>(a => a.Motion(ProjWide14.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide15>(a => a.Motion(ProjWide15.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide16>(a => a.Motion(ProjWide16.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide17>(a => a.Motion(ProjWide17.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide18>(a => a.Motion(ProjWide18.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide19>(a => a.Motion(ProjWide19.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide20>(a => a.Motion(ProjWide20.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide21>(a => a.Motion(ProjWide21.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide22>(a => a.Motion(ProjWide22.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide23>(a => a.Motion(ProjWide23.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide24>(a => a.Motion(ProjWide24.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide25>(a => a.Motion(ProjWide25.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide26>(a => a.Motion(ProjWide26.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide27>(a => a.Motion(ProjWide27.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide28>(a => a.Motion(ProjWide28.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide29>(a => a.Motion(ProjWide29.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide30>(a => a.Motion(ProjWide30.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide31>(a => a.Motion(ProjWide31.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide32>(a => a.Motion(ProjWide32.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide33>(a => a.Motion(ProjWide33.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide34>(a => a.Motion(ProjWide34.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide35>(a => a.Motion(ProjWide35.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide36>(a => a.Motion(ProjWide36.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide37>(a => a.Motion(ProjWide37.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide38>(a => a.Motion(ProjWide38.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide39>(a => a.Motion(ProjWide39.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide40>(a => a.Motion(ProjWide40.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide41>(a => a.Motion(ProjWide41.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide42>(a => a.Motion(ProjWide42.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide43>(a => a.Motion(ProjWide43.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide44>(a => a.Motion(ProjWide44.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide45>(a => a.Motion(ProjWide45.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide46>(a => a.Motion(ProjWide46.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide47>(a => a.Motion(ProjWide47.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide48>(a => a.Motion(ProjWide48.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide49>(a => a.Motion(ProjWide49.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide50>(a => a.Motion(ProjWide50.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide51>(a => a.Motion(ProjWide51.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide52>(a => a.Motion(ProjWide52.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide53>(a => a.Motion(ProjWide53.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide54>(a => a.Motion(ProjWide54.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide55>(a => a.Motion(ProjWide55.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide56>(a => a.Motion(ProjWide56.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide57>(a => a.Motion(ProjWide57.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide58>(a => a.Motion(ProjWide58.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide59>(a => a.Motion(ProjWide59.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide60>(a => a.Motion(ProjWide60.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide61>(a => a.Motion(ProjWide61.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide62>(a => a.Motion(ProjWide62.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide63>(a => a.Motion(ProjWide63.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide64>(a => a.Motion(ProjWide64.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide65>(a => a.Motion(ProjWide65.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide66>(a => a.Motion(ProjWide66.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide67>(a => a.Motion(ProjWide67.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide68>(a => a.Motion(ProjWide68.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        subs.Archetype<ProjWide69>(a => a.Motion(ProjWide69.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps)));
        ProjectionTestSchema.DeclareCreature(subs);
    }

    /// <summary>The fixtures' flat engine, with the seventy archetypes' own component registered too.</summary>
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjBounds3>();
        dbe.RegisterComponentFromAccessor<ProjAi>();
        dbe.RegisterComponentFromAccessor<ProjVitals>();
        dbe.RegisterComponentFromAccessor<ProjWallet>();
        dbe.RegisterComponentFromAccessor<ProjWideBounds>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ProjWideBounds WideAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static ProjBounds PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void Fill(FrameHarness harness, params SessionId[] sessions)
    {
        for (var tick = 1; tick <= FillTicks; tick++)
        {
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    private static int Held<T>(FrameHarness harness, SessionId session) =>
        harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(typeof(T).Name).Idx).Length;

    /// <summary>Every bit of the 256 is its own: a set holding 0, 63, 64, 127 and 255 holds nothing else, on either side of each word boundary.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void EveryPlanIndexHasItsOwnBit()
    {
        var set = new ArchetypeSet();
        int[] held = [0, 63, 64, 127, 255];
        foreach (var a in held)
        {
            set.Add(a);
        }

        for (var a = 0; a < ArchetypeSet.Capacity; a++)
        {
            Assert.That(set.Contains(a), Is.EqualTo(System.Array.IndexOf(held, a) >= 0), $"plan index {a}");
        }
    }

    /// <summary>
    /// The creature sits at plan index 70; a second profile observes the archetype at plan index 6. Before step 2.0 the creature's events and cells reached
    /// the second profile's session through the aliased bit.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AnArchetypePastPlanIndexSixtyThreeReachesOnlyTheSessionsThatObserveIt()
    {
        var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 9; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i * 5f, 0f)));
            }

            tx.Spawn<ProjWide06>(ProjWide06.Bounds.Set(WideAt(0f, 5f)));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
            {
                DeclareWide(subs);
                subs.Profile("creatures", p => p.Sphere(Radius).Of<ProjCreature>());
                subs.Profile("sixth", p => p.Sphere(Radius).Of<ProjWide06>());
            },
            nameof(AnArchetypePastPlanIndexSixtyThreeReachesOnlyTheSessionsThatObserveIt), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        Assert.That(harness.Subscriptions.Plans[70].ArchetypeType, Is.EqualTo(typeof(ProjCreature)), "the creature's plan index is 70, which aliases 6");
        Assert.That(harness.Subscriptions.Plans[6].ArchetypeType, Is.EqualTo(typeof(ProjWide06)));

        var creatures = harness.OpenSessions(1, "creatures")[0];
        var sixth = harness.OpenSessions(1, "sixth")[0];
        harness.Sessions.SetViewpoint(creatures, default);
        harness.Sessions.SetViewpoint(sixth, default);
        Fill(harness, creatures, sixth);

        Assert.Multiple(() =>
        {
            Assert.That(Held<ProjCreature>(harness, creatures), Is.EqualTo(9), "the control: the creatures' own session holds them");
            Assert.That(Held<ProjWide06>(harness, sixth), Is.EqualTo(1), "the control: the sixth archetype's session holds its entity");
            Assert.That(Held<ProjCreature>(harness, sixth), Is.Zero, "no creature reaches a session whose profile does not observe it");
            Assert.That(Held<ProjWide06>(harness, creatures), Is.Zero, "nor the other way round");
        });
    }

    /// <summary>A profile observing all seventy-one archetypes serves every one of them, below plan index 64 and above it.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AProfileObservingMoreThanSixtyFourArchetypesServesEveryOne()
    {
        var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ProjWide00>(ProjWide00.Bounds.Set(WideAt(0f, 0f)));
            tx.Spawn<ProjWide63>(ProjWide63.Bounds.Set(WideAt(5f, 0f)));
            tx.Spawn<ProjWide64>(ProjWide64.Bounds.Set(WideAt(10f, 0f)));
            tx.Spawn<ProjWide69>(ProjWide69.Bounds.Set(WideAt(15f, 0f)));
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(20f, 0f)));
            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
            {
                DeclareWide(subs);
                subs.Profile("all", p => p.Sphere(Radius)
            .Of<ProjWide00>().Of<ProjWide01>().Of<ProjWide02>().Of<ProjWide03>().Of<ProjWide04>().Of<ProjWide05>().Of<ProjWide06>()
            .Of<ProjWide07>().Of<ProjWide08>().Of<ProjWide09>().Of<ProjWide10>().Of<ProjWide11>().Of<ProjWide12>().Of<ProjWide13>()
            .Of<ProjWide14>().Of<ProjWide15>().Of<ProjWide16>().Of<ProjWide17>().Of<ProjWide18>().Of<ProjWide19>().Of<ProjWide20>()
            .Of<ProjWide21>().Of<ProjWide22>().Of<ProjWide23>().Of<ProjWide24>().Of<ProjWide25>().Of<ProjWide26>().Of<ProjWide27>()
            .Of<ProjWide28>().Of<ProjWide29>().Of<ProjWide30>().Of<ProjWide31>().Of<ProjWide32>().Of<ProjWide33>().Of<ProjWide34>()
            .Of<ProjWide35>().Of<ProjWide36>().Of<ProjWide37>().Of<ProjWide38>().Of<ProjWide39>().Of<ProjWide40>().Of<ProjWide41>()
            .Of<ProjWide42>().Of<ProjWide43>().Of<ProjWide44>().Of<ProjWide45>().Of<ProjWide46>().Of<ProjWide47>().Of<ProjWide48>()
            .Of<ProjWide49>().Of<ProjWide50>().Of<ProjWide51>().Of<ProjWide52>().Of<ProjWide53>().Of<ProjWide54>().Of<ProjWide55>()
            .Of<ProjWide56>().Of<ProjWide57>().Of<ProjWide58>().Of<ProjWide59>().Of<ProjWide60>().Of<ProjWide61>().Of<ProjWide62>()
            .Of<ProjWide63>().Of<ProjWide64>().Of<ProjWide65>().Of<ProjWide66>().Of<ProjWide67>().Of<ProjWide68>().Of<ProjWide69>()
                    .Of<ProjCreature>());
            },
            nameof(AProfileObservingMoreThanSixtyFourArchetypesServesEveryOne), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "all")[0];
        harness.Sessions.SetViewpoint(session, default);
        Fill(harness, session);

        Assert.Multiple(() =>
        {
            Assert.That(Held<ProjWide00>(harness, session), Is.EqualTo(1), "plan index 0");
            Assert.That(Held<ProjWide63>(harness, session), Is.EqualTo(1), "plan index 63, the old mask's last bit");
            Assert.That(Held<ProjWide64>(harness, session), Is.EqualTo(1), "plan index 64, the first past it");
            Assert.That(Held<ProjWide69>(harness, session), Is.EqualTo(1), "plan index 69");
            Assert.That(Held<ProjCreature>(harness, session), Is.EqualTo(1), "plan index 70");
        });
    }
}
