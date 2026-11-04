using System;

namespace SwgTatooine.Server;

/// <summary>What a blow is made of (crafting-catalog.md § 5.3): beasts and the ninjas' blades hit kinetic, the soldiers' blasters energy.</summary>
public static class DamageType
{
    /// <summary>No type: a hit nothing mitigates.</summary>
    public const byte None = 0;

    public const byte Kinetic = 1;

    public const byte Energy = 2;

    public static string NameOf(byte type) => type switch
    {
        Kinetic => "kinetic",
        Energy => "energy",
        _ => "none",
    };
}

/// <summary>
/// Where an equipped item sits (crafting-build.md § 2.2): <see cref="ItemStack.Slot"/> 101-107, out of the 1-40 grid. The weapon, then the six armour pieces.
/// </summary>
public static class EquipSlot
{
    public const byte Weapon = 101;
    public const byte Head = 102;
    public const byte Torso = 103;
    public const byte Arms = 104;
    public const byte Legs = 105;
    public const byte Hands = 106;
    public const byte Feet = 107;

    /// <summary>The first equip slot; slot − <see cref="First"/> indexes <see cref="Count"/>-long arrays.</summary>
    public const byte First = Weapon;

    public const int Count = 7;

    public static bool IsEquip(byte slot) => slot is >= First and < First + Count;

    /// <summary>
    /// Each armour piece's share of the set, per mille (§ 5.3, SWG's weights): mitigation is Σ share × protection, and a hit lands on one piece drawn by
    /// these. Indexed by slot − <see cref="First"/>; the weapon has none.
    /// </summary>
    public static readonly int[] SharePerMille = [0, 130, 330, 220, 200, 60, 60];

    public static string NameOf(byte slot) => slot switch
    {
        Weapon => "weapon",
        Head => "head",
        Torso => "torso",
        Arms => "arms",
        Legs => "legs",
        Hands => "hands",
        Feet => "feet",
        _ => "none",
    };
}

/// <summary>A weapon template's fixed traits: the stance it fights in, and the damage type it deals.</summary>
public readonly record struct WeaponTraits(ushort Template, byte Stance, byte DamageType);

/// <summary>An armour template's fixed traits: the slot it is worn in.</summary>
public readonly record struct ArmorTraits(ushort Template, byte Slot);

/// <summary>
/// The one-off numbers of an item with stats (crafting-build.md § 2.1): what <see cref="ItemInstance"/>'s four stat lines mean for its category, the
/// combat rolls (§ 9.3), and the vendor's standard issue (§ 15.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>A weapon's lines</b>: <c>Stat0</c> damage per hit, <c>Stat1</c> delay in milliseconds, <c>Stat2</c> range in decimetres. <b>An armour piece's</b>:
/// <c>Stat0</c> kinetic protection, <c>Stat1</c> energy protection, both per mille.
/// </para>
/// <para>
/// <b>Standard issue is a typical Mk I craft</b>, computed by <c>balance_model.py</c> (claude/design/SwgTatooine/design/balance/): the weapons' damage and
/// delay from <c>weapon_q(1)</c> and the solved ranges (§ 14.5), damage divided once by the crits' mean gain (§ 9.3); the armour's protection is the
/// on-band mitigation at CL 5 (padded 12 % kinetic and 10 % energy, composite the mirror); its condition, § 9.1's Mk I lives. Pinned by
/// <c>EquipmentChecks</c>.
/// </para>
/// </remarks>
public static class Equipment
{
    /// <summary>§ 9.3 step 1: a hit is its damage × U(1 − v, 1 + v).</summary>
    public const float Variance = 0.10f;

    /// <summary>§ 9.3 step 2: the chance of a critical, before Precision.</summary>
    public const float CritChance = 0.05f;

    /// <summary>§ 9.3 step 2: a critical's multiplier.</summary>
    public const float CritMultiplier = 1.5f;

    /// <summary>The highest mitigation, armour and tonic together (§ 5.3), per mille.</summary>
    public const int MitigationCapPerMille = 750;

    /// <summary>Unarmed with no weapon equipped (Q2): a flat 40 damage per 1.2 s, kinetic, a fist's reach.</summary>
    public const int UnarmedDamage = 40;

    public const int UnarmedDelayMs = 1200;

    public const int UnarmedRangeDm = 10;

    private static readonly WeaponTraits[] Weapons =
    [
        new(ItemCatalog.Knuckler, Stances.Unarmed, DamageType.Kinetic),
        new(ItemCatalog.Sword, Stances.OneHanded, DamageType.Kinetic),
        new(ItemCatalog.Blaster, Stances.Pistol, DamageType.Energy),
    ];

    private static readonly ArmorTraits[] Armors =
    [
        new(ItemCatalog.PaddedHelmet, EquipSlot.Head),
        new(ItemCatalog.PaddedHelmet + 1, EquipSlot.Torso),
        new(ItemCatalog.PaddedHelmet + 2, EquipSlot.Arms),
        new(ItemCatalog.PaddedHelmet + 3, EquipSlot.Legs),
        new(ItemCatalog.PaddedHelmet + 4, EquipSlot.Hands),
        new(ItemCatalog.PaddedHelmet + 5, EquipSlot.Feet),
        new(ItemCatalog.CompositeHelmet, EquipSlot.Head),
        new(ItemCatalog.CompositeHelmet + 1, EquipSlot.Torso),
        new(ItemCatalog.CompositeHelmet + 2, EquipSlot.Arms),
        new(ItemCatalog.CompositeHelmet + 3, EquipSlot.Legs),
        new(ItemCatalog.CompositeHelmet + 4, EquipSlot.Hands),
        new(ItemCatalog.CompositeHelmet + 5, EquipSlot.Feet),
    ];

    /// <summary>A weapon template's traits; <see langword="false"/> for anything else.</summary>
    public static bool TryWeapon(ushort template, out WeaponTraits traits)
    {
        foreach (var w in Weapons)
        {
            if (w.Template == template)
            {
                traits = w;
                return true;
            }
        }

        traits = default;
        return false;
    }

    /// <summary>An armour template's traits; <see langword="false"/> for anything else.</summary>
    public static bool TryArmor(ushort template, out ArmorTraits traits)
    {
        foreach (var a in Armors)
        {
            if (a.Template == template)
            {
                traits = a;
                return true;
            }
        }

        traits = default;
        return false;
    }

    /// <summary>The slot a template is worn in; 0 for one that is not worn.</summary>
    public static byte SlotOf(ushort template)
        => TryWeapon(template, out _) ? EquipSlot.Weapon : TryArmor(template, out var a) ? a.Slot : (byte)0;

    /// <summary>The lowest combat level that may equip Mk <paramref name="mk"/> (B3): 10(<paramref name="mk"/> − 1) + 1.</summary>
    public static int LevelFor(byte mk) => mk <= 1 ? 1 : (10 * (mk - 1)) + 1;

    /// <summary>
    /// A hit after the § 9.3 rolls, before mitigation: <paramref name="damage"/> × U(1 − v, 1 + v), × the crit multiplier on a crit. <paramref name="u"/> and
    /// <paramref name="c"/> are two independent uniforms in [0, 1).
    /// </summary>
    public static int Roll(int damage, float u, float c, out bool crit)
    {
        crit = c < CritChance;
        var raw = damage * (1f - Variance + (2f * Variance * u));
        return (int)MathF.Round(crit ? raw * CritMultiplier : raw);
    }

    /// <summary>§ 9.3 steps 3-4: <paramref name="raw"/> less <paramref name="mitigationPerMille"/> (capped), at least 1.</summary>
    public static int Mitigate(int raw, int mitigationPerMille)
    {
        var m = Math.Clamp(mitigationPerMille, 0, MitigationCapPerMille);
        return Math.Max(1, (int)((((long)raw * (1000 - m)) + 500) / 1000));
    }

    /// <summary>The vendor's standard issue (§ 15.3): Mk I only, a typical craft, sold at twice its value. The carbine waits for its stance (O-4).</summary>
    public static ReadOnlySpan<StandardIssue> Standard => StandardItems;

    /// <summary>The standard issue of a template; <see langword="false"/> for one the vendor does not sell.</summary>
    public static bool TryStandard(ushort template, out StandardIssue item)
    {
        foreach (var s in StandardItems)
        {
            if (s.Template == template)
            {
                item = s;
                return true;
            }
        }

        item = default;
        return false;
    }

    /// <summary>What the vendor asks for a standard-issue item, credits: twice its Mk I market value (§ 15.3); 0 for one it does not sell.</summary>
    public static long StandardPrice(ushort template)
        => TryStandard(template, out _) ? MarketValues.MilliOf(template, 1) * TatooineData.VendorSellsPerMille / 1000 / 1000 : 0;

    /// <summary>
    /// The bulk materials the vendor sells at Mk I (Loïc, 2026-11-02): the ones no low-level creature drops — Metal, Crystal, Gas and Chemical only from
    /// the camps (CL 25+), Fiber from one CL 18+ beast, Ore and Water from nothing before the extractors (C5) — so a new crafter can start. Higher Mks
    /// still come from loot.
    /// </summary>
    public static readonly ushort[] StandardMaterials =
        [ItemCatalog.Ore, ItemCatalog.Water, ItemCatalog.Metal, ItemCatalog.Crystal, ItemCatalog.Gas, ItemCatalog.Chemical, ItemCatalog.Fiber];

    /// <summary>Whether the vendor sells units of <paramref name="template"/> at Mk I.</summary>
    public static bool IsStandardMaterial(ushort template) => Array.IndexOf(StandardMaterials, template) >= 0;

    /// <summary>One unit's vendor price in milli-credits: twice its Mk I value (§ 15.3); 0 for a material it does not sell.</summary>
    public static long StandardMaterialMilli(ushort template)
        => IsStandardMaterial(template) ? MarketValues.MilliOf(template, 1) * TatooineData.VendorSellsPerMille / 1000 : 0;

    /// <summary>What <paramref name="quantity"/> units cost, whole credits, rounded down; 0 for none or a material it does not sell.</summary>
    public static long StandardMaterialPrice(ushort template, int quantity) => StandardMaterialMilli(template) * Math.Max(0, quantity) / 1000;

    // Weapons: damage ÷ 1.025 (crits' mean gain), delay ms, range dm; condition in combat-milliseconds, a Mk I's hour (§ 9.1).
    // Armour: kinetic and energy ‰; condition in damage points, the Mk I set's 120 combat-minutes at 15 hits a minute of a CL 5 beast's 36, by share.
    private static readonly StandardIssue[] StandardItems =
    [
        new(ItemCatalog.Knuckler, 98, 1061, 10, 3_600_000),
        new(ItemCatalog.Sword, 145, 1503, 30, 3_600_000),
        new(ItemCatalog.Blaster, 81, 1021, 300, 3_600_000),
        new(ItemCatalog.PaddedHelmet, 120, 100, 0, 8_509),
        new(ItemCatalog.PaddedHelmet + 1, 120, 100, 0, 21_600),
        new(ItemCatalog.PaddedHelmet + 2, 120, 100, 0, 14_400),
        new(ItemCatalog.PaddedHelmet + 3, 120, 100, 0, 13_091),
        new(ItemCatalog.PaddedHelmet + 4, 120, 100, 0, 3_927),
        new(ItemCatalog.PaddedHelmet + 5, 120, 100, 0, 3_927),
        new(ItemCatalog.CompositeHelmet, 100, 120, 0, 8_509),
        new(ItemCatalog.CompositeHelmet + 1, 100, 120, 0, 21_600),
        new(ItemCatalog.CompositeHelmet + 2, 100, 120, 0, 14_400),
        new(ItemCatalog.CompositeHelmet + 3, 100, 120, 0, 13_091),
        new(ItemCatalog.CompositeHelmet + 4, 100, 120, 0, 3_927),
        new(ItemCatalog.CompositeHelmet + 5, 100, 120, 0, 3_927),
    ];
}

/// <summary>One standard-issue item: its template, three stat lines (<see cref="Equipment"/>'s meaning for its category) and its condition when new.</summary>
public readonly record struct StandardIssue(ushort Template, int Stat0, int Stat1, int Stat2, int MaxCondition);
