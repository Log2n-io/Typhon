using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Core character attributes, resources, and level progression.
/// Present on all player characters and NPCs that participate in combat.
/// </summary>
[Component("ARPG.CharacterStats", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CharacterStats
{
    // Core attributes (affect damage/defense formulas)
    [Field] public int Strength;
    [Field] public int Dexterity;
    [Field] public int Intelligence;
    [Field] public int Vitality;

    // Health & Mana
    [Field] public int CurrentHealth;
    [Field] public int MaxHealth;
    [Field] public int CurrentMana;
    [Field] public int MaxMana;

    // Combat modifiers
    [Field] public int Armor;
    [Field] public int EvasionRating;
    [Field] public float CriticalChance;
    [Field] public float CriticalMultiplier;

    // Progression
    [Field] /*[Index]*/ public int Level;
    [Field] public long Experience;
    [Field] public long ExperienceToNextLevel;
}
