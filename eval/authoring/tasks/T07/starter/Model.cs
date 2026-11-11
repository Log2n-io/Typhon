// Version 1 of the game model (shipped). Version 2 is yours to write.
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Stats", 1)]
public struct Stats
{
    public int Hp;
    public int Armor;
}

[Archetype]
public sealed partial class Hero : Archetype<Hero>
{
    public static readonly Comp<Stats> Stats = Register<Stats>();
}
