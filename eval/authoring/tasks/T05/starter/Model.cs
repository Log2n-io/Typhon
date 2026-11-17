// DO NOT MODIFY — given model for this task.
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Position", 1, StorageMode = StorageMode.SingleVersion)]
public struct Position
{
    public float X;
    public float Y;
}

[Component("Eval.Velocity", 1, StorageMode = StorageMode.SingleVersion)]
public struct Velocity
{
    public float X;
    public float Y;
}

[Archetype]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Velocity> Velocity = Register<Velocity>();
}
