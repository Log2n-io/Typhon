using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

internal static class SimpleSpaceBattleSchemaIds
{
    public const ushort Ship = 3100;
}

/// <summary>
/// The only archetype. One ship = 48 bytes of components + an 8-byte entity id, which
/// <c>ArchetypeClusterInfo.SelectClusterSize</c> resolves to N=46, 3 clusters/page, 138 entities per 8 KB page.
/// <para>
/// <see cref="ClusterDurability.Checkpoint"/> stops the per-tick fence WAL emission for this archetype: every field
/// here is regenerable simulation state the sim rewrites 25×/second, so a crash losing up to one checkpoint interval
/// (30 s default) of freshness is the correct trade. Ship *existence* is unaffected — spawn and destroy are lifecycle
/// records and stay fully durable.
/// </para>
/// </summary>
[Archetype(SimpleSpaceBattleSchemaIds.Ship, ClusterDurability = ClusterDurability.Checkpoint)]
public sealed partial class Ship : Archetype<Ship>
{
    public static readonly Comp<HullComponent> Hull = Register<HullComponent>();
    public static readonly Comp<MotionComponent> Motion = Register<MotionComponent>();
    public static readonly Comp<VitalsComponent> Vitals = Register<VitalsComponent>();
    public static readonly Comp<TargetingComponent> Targeting = Register<TargetingComponent>();
}
