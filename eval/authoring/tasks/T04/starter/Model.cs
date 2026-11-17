// DO NOT MODIFY — given model for this task.
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Health", 1, StorageMode = StorageMode.SingleVersion)]
public struct Health
{
    public int Value;
}

[Component("Eval.Poison", 1, StorageMode = StorageMode.SingleVersion)]
public struct Poison
{
    public int DamagePerTick;
}

[Component("Eval.Status", 1, StorageMode = StorageMode.SingleVersion)]
public struct Status
{
    public int Dead;
}

[Archetype]
public sealed partial class Creature : Archetype<Creature>
{
    public static readonly Comp<Health> Health = Register<Health>();
    public static readonly Comp<Poison> Poison = Register<Poison>();
    public static readonly Comp<Status> Status = Register<Status>();
}
