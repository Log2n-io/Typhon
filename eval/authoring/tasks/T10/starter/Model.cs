// DO NOT MODIFY — given model for this task.
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.UnitBounds", 1, StorageMode = StorageMode.SingleVersion)]
public struct UnitBounds
{
    [SpatialIndex]
    public AABB2F Box;
}

[Component("Eval.UnitHealth", 1, StorageMode = StorageMode.SingleVersion)]
public struct UnitHealth
{
    [Replicate(CodecKind.Varu, Name = "hp")]
    public int Value;

    public int Max;
}

[Archetype, Replicated]
public sealed partial class Unit : Archetype<Unit>
{
    [Motion(ToleranceM = 0.05, TeleportMps = 20)]
    public static readonly Comp<UnitBounds> Bounds = Register<UnitBounds>();

    public static readonly Comp<UnitHealth> Health = Register<UnitHealth>();
}
