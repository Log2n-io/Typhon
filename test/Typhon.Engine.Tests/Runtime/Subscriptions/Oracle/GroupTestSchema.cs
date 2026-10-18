using Microsoft.Extensions.DependencyInjection;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>A group member's key (14 § 3.1): the town it belongs to, 0 for none — the engine's sparse multi-value index is the relation.</summary>
[Component("Typhon.Test.Group.Key", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupKey
{
    [Field]
    [Index(AllowMultiple = true, Sparse = true)]
    public int Town;
}

/// <summary>A region (14 § 3.3): the town's key and the extent a session's observer meets it by.</summary>
[Component("Typhon.Test.Group.Zone", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupZone
{
    [Field]
    public int Key;

    [Field]
    public AABB2F Extent;
}

/// <summary>A static member: replicated through its town alone, never by geometry.</summary>
[Archetype]
partial class GroupBuilding : Archetype<GroupBuilding>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
    public static readonly Comp<GroupKey> Key = Register<GroupKey>();
}

/// <summary>A moving member: replicated by geometry and by its town, the union (P9).</summary>
[Archetype]
partial class GroupCitizen : Archetype<GroupCitizen>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
    public static readonly Comp<GroupKey> Key = Register<GroupKey>();
}

/// <summary>The region archetype: one entity per town, never replicated.</summary>
[Archetype]
partial class GroupTown : Archetype<GroupTown>
{
    public static readonly Comp<GroupZone> Zone = Register<GroupZone>();
}

/// <summary>A key field whose index is not sparse: 0 would be indexed, so a set refuses it.</summary>
[Component("Typhon.Test.Group.DenseKey", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupDenseKey
{
    [Field]
    [Index(AllowMultiple = true)]
    public int Town;
}

/// <summary>A key on a Transient component, refused until Transient indexes follow migration (#1162).</summary>
[Component("Typhon.Test.Group.TransientKey", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupTransientKey
{
    [Field]
    [Index(AllowMultiple = true, Sparse = true)]
    public int Town;
}

[Archetype]
partial class GroupDenseMember : Archetype<GroupDenseMember>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<GroupDenseKey> Key = Register<GroupDenseKey>();
}

[Archetype]
partial class GroupTransientMember : Archetype<GroupTransientMember>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<GroupTransientKey> Key = Register<GroupTransientKey>();
}

/// <summary>A second key on one member: its district, a set of its own beside its town.</summary>
[Component("Typhon.Test.Group.District", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupDistrictKey
{
    [Field]
    [Index(AllowMultiple = true, Sparse = true)]
    public int District;
}

/// <summary>A key on a Versioned component: refused in this phase.</summary>
[Component("Typhon.Test.Group.VersionedKey", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupVersionedKey
{
    [Field]
    [Index(AllowMultiple = true, Sparse = true)]
    public int Town;
}

/// <summary>An unsigned key beside the region's signed one: refused.</summary>
[Component("Typhon.Test.Group.UnsignedKey", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct GroupUnsignedKey
{
    [Field]
    [Index(AllowMultiple = true, Sparse = true)]
    public uint Town;
}

/// <summary>A member of two sets: its town and its district.</summary>
[Archetype]
partial class GroupResident : Archetype<GroupResident>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
    public static readonly Comp<GroupKey> Key = Register<GroupKey>();
    public static readonly Comp<GroupDistrictKey> District = Register<GroupDistrictKey>();
}

/// <summary>The district set's region archetype.</summary>
[Archetype]
partial class GroupDistrictRegion : Archetype<GroupDistrictRegion>
{
    public static readonly Comp<GroupZone> Zone = Register<GroupZone>();
}

[Archetype]
partial class GroupVersionedMember : Archetype<GroupVersionedMember>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<GroupVersionedKey> Key = Register<GroupVersionedKey>();
}

[Archetype]
partial class GroupUnsignedMember : Archetype<GroupUnsignedMember>
{
    public static readonly Comp<ProjBounds> Bounds = Register<ProjBounds>();
    public static readonly Comp<GroupUnsignedKey> Key = Register<GroupUnsignedKey>();
}

/// <summary>A member with no position: refused until step 4.2 builds positionless members.</summary>
[Archetype]
partial class GroupPositionless : Archetype<GroupPositionless>
{
    public static readonly Comp<ProjAi> Ai = Register<ProjAi>();
    public static readonly Comp<GroupKey> Key = Register<GroupKey>();
}

/// <summary>The group fixtures' engine and declarations.</summary>
static class GroupTestSchema
{
    /// <summary>The group set's name; its catalog index is 0, the only set.</summary>
    public const string Set = "town";

    /// <summary>The Sphere profile every group session is bound to.</summary>
    public const string Profile = "group-sphere";

    /// <summary>The engine, with the projection test components and the group ones registered.</summary>
    /// <param name="services">The fixture's service provider.</param>
    /// <param name="deep">A volumetric grid (±1 024 m, 128 m cells) instead of the flat ±8 192 m one.</param>
    public static DatabaseEngine SetupEngine(IServiceProvider services, bool deep = false)
    {
        var dbe = services.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ProjBounds>();
        dbe.RegisterComponentFromAccessor<ProjBounds3>();
        dbe.RegisterComponentFromAccessor<ProjAi>();
        dbe.RegisterComponentFromAccessor<ProjVitals>();
        dbe.RegisterComponentFromAccessor<ProjWallet>();
        dbe.RegisterComponentFromAccessor<GroupKey>();
        dbe.RegisterComponentFromAccessor<GroupZone>();
        dbe.RegisterComponentFromAccessor<GroupDenseKey>();
        dbe.RegisterComponentFromAccessor<GroupTransientKey>();
        dbe.RegisterComponentFromAccessor<GroupDistrictKey>();
        dbe.RegisterComponentFromAccessor<GroupVersionedKey>();
        dbe.RegisterComponentFromAccessor<GroupUnsignedKey>();
        dbe.ConfigureSpatialGrid(deep
            ? new SpatialGridConfig(worldMin: new Vector3D(-1024d, -1024d, -1024d), worldMax: new Vector3D(1024d, 1024d, 1024d), cellSize: 128d)
            : SpatialGridConfig.Flat(
                worldMin: new Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
                worldMax: new Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
                cellSize: 256f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Buildings (static, town only), citizens (moving, sphere and town), the town set, and a Sphere profile observing citizens and holding towns.
    /// </summary>
    /// <param name="subs">The registry.</param>
    /// <param name="radiusM">The Sphere's radius.</param>
    /// <param name="reachM">The reach towns are met at.</param>
    /// <param name="leaveMarginM">The set's hysteresis.</param>
    public static void Declare(SubscriptionsRegistry subs, double radiusM, double reachM, double leaveMarginM)
    {
        subs.Static<GroupBuilding>(a => a
            .Position(GroupBuilding.Bounds)
            .Field(GroupBuilding.Ai, x => x.Template, Codec.U8, name: "kind"));
        subs.Archetype<GroupCitizen>(a => a
            .Motion(GroupCitizen.Bounds, m => m.Tolerance(0.05).Teleport(ProjectionTestSchema.MaxSpeedMps))
            .OnEnter(GroupCitizen.Ai, x => x.Template, Codec.U8, name: "template"));
        subs.Groups(Set, g => g
            .Region<GroupTown>(r => r.Key(GroupTown.Zone, z => z.Key).Extent(GroupTown.Zone, z => z.Extent))
            .Member<GroupBuilding>(m => m.Key(GroupBuilding.Key, k => k.Town))
            .Member<GroupCitizen>(m => m.Key(GroupCitizen.Key, k => k.Town))
            .Leave(leaveMarginM));
        subs.Profile(Profile, p => p.Detection(PushDetection.Explicit).Sphere(radiusM).Of<GroupCitizen>().Groups(Set, reachM));
    }

    /// <summary>A square extent centred on (x, y).</summary>
    public static AABB2F Square(float x, float y, float halfM) => new() { MinX = x - halfM, MinY = y - halfM, MaxX = x + halfM, MaxY = y + halfM };

    /// <summary>A point's bounds, as the projection fixtures write them.</summary>
    public static ProjBounds At(float x, float y) => new() { Bounds = Square(x, y, 0.5f) };
}
