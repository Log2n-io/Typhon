// Version 2: Stats gains Mana. Adding a field is a compatible change (zero-filled), so the revision bump migrates the data;
// existing heroes then get Mana = 50 through a one-time seed step (TyphonOptions.Seed runs each revision once per database).
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Eval;

[Component("Eval.Stats", 2)]
public struct Stats
{
    public int Hp;
    public int Armor;
    public int Mana;
    private int _pad;
}

[Archetype]
public sealed partial class Hero : Archetype<Hero>
{
    public static readonly Comp<Stats> Stats = Register<Stats>();
}
