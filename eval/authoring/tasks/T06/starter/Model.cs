// DO NOT MODIFY — given model for this task.
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Purse", 1, StorageMode = StorageMode.SingleVersion)]
public struct Purse
{
    public long Gold;
}

[Archetype]
public sealed partial class Player : Archetype<Player>
{
    public static readonly Comp<Purse> Purse = Register<Purse>();
}
