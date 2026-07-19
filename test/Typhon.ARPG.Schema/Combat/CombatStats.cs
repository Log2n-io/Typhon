using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Computed damage output and defensive ratings.
/// Derived from CharacterStats + Equipment; recalculated on gear change.
/// Present on all combat-capable entities (players, monsters, summons).
/// </summary>
[Component("ARPG.CombatStats", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CombatStats
{
    // Physical damage range
    [Field] public int MinPhysicalDamage;
    [Field] public int MaxPhysicalDamage;

    // Elemental damage ranges
    [Field] public int MinFireDamage;
    [Field] public int MaxFireDamage;
    [Field] public int MinColdDamage;
    [Field] public int MaxColdDamage;
    [Field] public int MinLightningDamage;
    [Field] public int MaxLightningDamage;

    // Resistances (% reduction, typically capped at 75)
    [Field] public int FireResistance;
    [Field] public int ColdResistance;
    [Field] public int LightningResistance;
    [Field] public int ChaosResistance;

    // Attack/defense ratings
    [Field] public int AttackRating;
    [Field] public int DefenseRating;
    [Field] public float AttackSpeed;
    [Field] public float CastSpeed;
}
