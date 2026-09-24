using Microsoft.Extensions.DependencyInjection;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The schema P1-02's fixtures compile against: a spatial archetype with change groups, one with an owner section, and a static one. It is deliberately NOT
// SubscriptionsRegistryTests' schema — that one exists to prove a declaration survives the registry, and its components carry no [SpatialIndex] because it
// never starts an engine. Compiling a projection does: a position resolves to the component's spatial field, and every field resolves to a cluster offset,
// so these components have to be cluster-backed and spatially indexed for the resolution to mean anything.
//
// Every wire name is lower case and given explicitly. Field order is ordinal by name (W11), and ordinal puts every upper-case letter before every lower-case
// one — so a schema mixing `Mode` with `alerted` would order by capitalization rather than by anything a reader would predict, and the order test would be
// asserting the wrong property.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

enum ProjAiMode : byte
{
    Idle = 0,
    Wander = 1,
    Chase = 2,
    Attack = 3,
    Dead = 4,
}

[Component("Typhon.Test.Proj.Bounds", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjBounds
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public float Speed;
}

/// <summary>A 3D spatial component, so a projection can be compiled with three axes and the block layout sized for them.</summary>
[Component("Typhon.Test.Proj.Bounds3", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjBounds3
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;

    [Field]
    public float Speed;
}

[Component("Typhon.Test.Proj.Ai", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjAi
{
    [Field]
    public byte Template;

    [Field]
    public ProjAiMode Mode;

    /// <summary>A flag, stored as a byte rather than a <c>bool</c>: a reflection-measured <c>bool</c> offset is refused outright (SCHEMA-07).</summary>
    [Field]
    public byte Alerted;

    [Field]
    public ushort Level;

    /// <summary>Written every tick by the simulation and replicated by nobody — the noise a projection exists to filter.</summary>
    [Field]
    public int ThinkCooldown;
}

[Component("Typhon.Test.Proj.Vitals", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjVitals
{
    [Field]
    public int Health;

    [Field]
    public int MaxHealth;
}

[Component("Typhon.Test.Proj.Wallet", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ProjWallet
{
    [Field]
    public long Credits;

    [Field]
    public int ItemCount;
}

[Archetype]
partial class ProjCreature : Archetype<ProjCreature>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
    public static readonly Comp<ProjVitals> Vitals = Register<ProjVitals>();
}

[Archetype]
partial class ProjPlayer : Archetype<ProjPlayer>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjVitals> Vitals = Register<ProjVitals>();
    public static readonly Comp<ProjWallet> Wallet = Register<ProjWallet>();
}

[Archetype]
partial class ProjRock : Archetype<ProjRock>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
}

/// <summary>A 3D mover: three position axes, and enough <c>varu</c> state to push the hot entry past one cache line.</summary>
[Archetype]
partial class ProjFlyer : Archetype<ProjFlyer>
{
    public static readonly Comp<ProjBounds3> Bounds = Register<ProjBounds3>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
}

/// <summary>
/// The engine setup and the declarations every P1-02 fixture shares.
/// </summary>
static class ProjectionTestSchema
{
    /// <summary>The world the grid spans: ±8 192 m, so 24 bits per axis is a 2⁻¹⁰ m position quantum — SWG's numbers.</summary>
    public const float WorldExtentM = 8192f;

    /// <summary>The teleport threshold the fixtures declare, and the one the reported SWG velocity width comes from.</summary>
    public const double MaxSpeedMps = 20.0;

    /// <summary>The nominal tick period the fixtures compile against: 10 Hz, as the demo's catalog declares.</summary>
    public const double TickPeriodSeconds = 0.1;

    /// <summary>The position quantum that follows from <see cref="WorldExtentM"/> at 24 bits per axis: 2⁻¹⁰ m.</summary>
    public const double PositionStepM = 1.0 / 1024.0;

    /// <summary>
    /// The replication cell side the fixtures declare for a Sphere radius: a third of it, the 11 × 11 window. For <paramref name="radius"/> zero (World
    /// profiles only), a 48th of the world's width, so a World fixture's delivery walks the grid it was written against.
    /// </summary>
    /// <param name="radius">The largest Sphere radius the fixture declares, or zero.</param>
    /// <returns>The side, for <see cref="SubscriptionsOptions.ReplicationCellM"/>.</returns>
    public static double ReplicationCellFor(double radius) => radius > 0 ? radius / 3d : 2d * WorldExtentM / 48d;

    /// <summary>The cube the volumetric grid spans: +/-1 024 m on all three axes, so every axis shares one quantum.</summary>
    public const double VolumeExtentM = 1024.0;

    /// <summary>The position quantum that follows from <see cref="VolumeExtentM"/> at 24 bits per axis, on all three axes alike.</summary>
    public const double VolumePositionStepM = 2048.0 / 16777216.0;

    /// <param name="services">The fixture's service provider.</param>
    /// <param name="volumetric">
    /// <see langword="true"/> configures a cubic grid instead of the flat one. A flat grid's Z span is one cell, which would give the third axis a quantum
    /// hundreds of times finer than X and Y — a velocity width derived from the finest axis would then be measuring the grid's flatness rather than the
    /// archetype's motion, and the 3D case would read as an accident.
    /// </param>
    /// <returns>The engine, with its components registered and its grid configured.</returns>
    public static DatabaseEngine SetupEngine(IServiceProvider services, bool volumetric = false) =>
        SetupEngine(services, volumetric
            ? new SpatialGridConfig(
                worldMin: new Vector3D(-VolumeExtentM, -VolumeExtentM, -VolumeExtentM),
                worldMax: new Vector3D(VolumeExtentM, VolumeExtentM, VolumeExtentM),
                cellSize: 256d)
            : SpatialGridConfig.Flat(
                worldMin: new Vector2(-WorldExtentM, -WorldExtentM),
                worldMax: new Vector2(WorldExtentM, WorldExtentM),
                cellSize: 256f));

    /// <summary>The same engine over a spatial world of the caller's choosing.</summary>
    public static DatabaseEngine SetupEngine(IServiceProvider services, SpatialGridConfig spatial)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjBounds3>();
        dbe.RegisterComponentFromAccessor<ProjAi>();
        dbe.RegisterComponentFromAccessor<ProjVitals>();
        dbe.RegisterComponentFromAccessor<ProjWallet>();
        dbe.ConfigureSpatialGrid(spatial);
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// The same engine, with the partitioner driven as hard as its knobs allow, for a fixture whose subject is what repair does.
    /// </summary>
    /// <param name="services">The fixture's service provider.</param>
    /// <returns>The engine.</returns>
    /// <remarks>
    /// <para>
    /// <b>A fixture of a few thousand entities cannot reach a production repair rate on the shipped defaults, and that is why this exists.</b> The default
    /// <c>repairCooldownTicks</c> of 50 lets a cluster be repaired once in fifty ticks, so a run of forty ticks sees NO relocation at all — which is how
    /// <c>RelocationVisibilityTests</c> came to assert for weeks that relocation was invisible to a client without a single relocation having happened.
    /// </para>
    /// <para>
    /// <b>These are the fixtures' settings and nobody else's.</b> Engine defaults are not derived from one workload, and nothing here proposes changing
    /// them: this is a test reaching in twenty ticks a state a production world reaches by being large.
    /// </para>
    /// </remarks>
    public static DatabaseEngine SetupEngineWithHotRepair(IServiceProvider services)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjBounds3>();
        dbe.RegisterComponentFromAccessor<ProjAi>();
        dbe.RegisterComponentFromAccessor<ProjVitals>();
        dbe.RegisterComponentFromAccessor<ProjWallet>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-WorldExtentM, -WorldExtentM),
            worldMax: new Vector2(WorldExtentM, WorldExtentM),
            cellSize: 128f,
            clusterRepairExtentRatio: 0.3f,
            reclusterBudgetMs: 16f,
            repairWorstClustersPerUnit: 64,
            repairCooldownTicks: 1));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>The creature projection, declared in the order a reader would write it.</summary>
    public static void DeclareCreature(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjCreature>(a => a
            .Motion(ProjCreature.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
            .OnEnter(ProjCreature.Ai, x => x.Template, Codec.U8, name: "template")
            .Field(ProjCreature.Ai, x => x.Mode, Codec.Enum<ProjAiMode>(bits: 3), name: "mode")
            .Field(ProjCreature.Ai, x => x.Alerted, Codec.Bool, name: "alerted")
            .Field(ProjCreature.Ai, x => x.Level, Codec.U16, name: "level", group: "vitals")
            .Fraction(ProjCreature.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals"));
    }

    /// <summary>The same creature projection with every call reversed, which W11 says must compile to the identical plan.</summary>
    public static void DeclareCreatureReversed(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjCreature>(a => a
            .Fraction(ProjCreature.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals")
            .Field(ProjCreature.Ai, x => x.Level, Codec.U16, name: "level", group: "vitals")
            .Field(ProjCreature.Ai, x => x.Alerted, Codec.Bool, name: "alerted")
            .Field(ProjCreature.Ai, x => x.Mode, Codec.Enum<ProjAiMode>(bits: 3), name: "mode")
            .OnEnter(ProjCreature.Ai, x => x.Template, Codec.U8, name: "template")
            .Motion(ProjCreature.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps)));
    }

    /// <summary>The player projection, whose owner section has its own groups and its own bit space (W17).</summary>
    public static void DeclarePlayer(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjPlayer>(a => a
            .Motion(ProjPlayer.Bounds, m => m.Teleport(MaxSpeedMps))
            .Fraction(ProjPlayer.Vitals, v => v.Health, v => v.MaxHealth, bits: 8, name: "hp", group: "vitals")
            .Owner(o => o
                .Field(ProjPlayer.Wallet, w => w.Credits, Codec.VarUInt.Saturate(), name: "credits")
                .Field(ProjPlayer.Wallet, w => w.ItemCount, Codec.VarUInt, name: "items", group: "bag")));
    }

    /// <summary>A static archetype: sent once on enter, never updated, so it has no change groups at all.</summary>
    public static void DeclareRock(SubscriptionsRegistry subs)
    {
        subs.Static<ProjRock>(a => a
            .Position(ProjRock.Bounds)
            .Field(ProjRock.Ai, x => x.Template, Codec.U8, name: "kind"));
    }

    /// <summary>
    /// The 3D projection: a moving position on three axes plus four <c>varu</c> state fields, which is 18 B of segment and 20 B of state body.
    /// </summary>
    /// <remarks>
    /// Four <c>varu</c> fields are the point, not decoration: each reserves its 5-byte worst case, so the state body alone is 20 B and the hot entry cannot
    /// fit one cache line however the segment is sized. It is a legal declaration the fixed 64 B shape would have silently overrun.
    /// </remarks>
    public static CompiledProjectionPlan CompileFlyer(DatabaseEngine dbe, int largestTickMultiplier = 1)
    {
        var subs = new SubscriptionsRegistry();
        subs.Archetype<ProjFlyer>(a => a
            .Motion(ProjFlyer.Bounds, m => m.Teleport(MaxSpeedMps))
            .Field(ProjFlyer.Ai, x => x.Template, Codec.VarUInt, name: "aaa")
            .Field(ProjFlyer.Ai, x => x.Alerted, Codec.VarUInt, name: "bbb")
            .Field(ProjFlyer.Ai, x => x.Level, Codec.VarUInt, name: "ccc")
            .Field(ProjFlyer.Ai, x => x.ThinkCooldown, Codec.VarUInt, name: "ddd"));

        return ProjectionCompiler.Compile(subs, dbe, TickPeriodSeconds, largestTickMultiplier)[0];
    }

    /// <summary>Declares all three and compiles them at the given tick multiplier.</summary>
    public static CompiledProjectionPlan[] CompileAll(DatabaseEngine dbe, int largestTickMultiplier = 1)
    {
        var subs = new SubscriptionsRegistry();
        DeclareCreature(subs);
        DeclarePlayer(subs);
        DeclareRock(subs);
        return ProjectionCompiler.Compile(subs, dbe, TickPeriodSeconds, largestTickMultiplier);
    }

    /// <summary>The plan for a named archetype.</summary>
    public static CompiledProjectionPlan PlanFor(CompiledProjectionPlan[] plans, string name)
    {
        foreach (var plan in plans)
        {
            if (plan.Name == name)
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>The compiled field with the given wire name, or a default-valued one when there is none.</summary>
    public static CompiledField FieldNamed(CompiledField[] fields, string name)
    {
        foreach (var field in fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        return default;
    }

    /// <summary>The wire names of a compiled field list, in the order the plan holds them.</summary>
    public static string[] NamesOf(CompiledField[] fields)
    {
        var names = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        return names;
    }
}
