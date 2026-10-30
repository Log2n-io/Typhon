using System;

namespace SwgTatooine.Server;

/// <summary>What kind of thing an item template is: the catalogue's <c>item.category</c> (ASSET_FORMAT § 5.11), 0 being none.</summary>
public static class ItemCategory
{
    public const byte Material = 1;
    public const byte Core = 2;
    public const byte Addon = 3;
    public const byte Part = 4;
    public const byte Armor = 5;
    public const byte Potion = 6;
    public const byte Kit = 7;
    public const byte Container = 8;

    /// <summary>The name the catalogue and the client use.</summary>
    public static string NameOf(byte category) => category switch
    {
        Material => "material",
        Core => "core",
        Addon => "addon",
        Part => "part",
        Armor => "armor",
        Potion => "potion",
        Kit => "kit",
        Container => "container",
        _ => "unknown",
    };
}

/// <summary>One item template (inventory-loot.md § 3.1): its id, the catalogue asset it is drawn with, its name, and whether it drops now.</summary>
public readonly record struct ItemTemplate(ushort Id, string Asset, string Name, byte Category, bool HasMk, bool Lootable);

/// <summary>
/// Every item a stack can hold (inventory-loot.md § 3.1): one row per catalogue item asset (crafting-catalog § 13), in ASSET_FORMAT § 5.11's order.
/// </summary>
/// <remarks>
/// <b>Ids are append-only for ever.</b> A ledger row and a stack store them; renumbering one would rewrite history. A new item gets the next id at the end.
/// </remarks>
public static class ItemCatalog
{
    private static readonly ItemTemplate[] All =
    [
        new(1, "mat_ore", "Ore", ItemCategory.Material, true, false),
        new(2, "mat_water", "Water", ItemCategory.Material, true, false),
        new(3, "mat_metal", "Metal", ItemCategory.Material, true, true),
        new(4, "mat_crystal", "Crystal", ItemCategory.Material, true, true),
        new(5, "mat_gas", "Gas", ItemCategory.Material, true, true),
        new(6, "mat_chemical", "Chemical", ItemCategory.Material, true, true),
        new(7, "mat_fiber", "Fiber", ItemCategory.Material, true, true),
        new(8, "mat_hide", "Hide", ItemCategory.Material, true, true),
        new(9, "mat_bone", "Bone", ItemCategory.Material, true, true),
        new(10, "mat_gland", "Gland", ItemCategory.Material, true, true),
        new(11, "mat_venom", "Venom", ItemCategory.Material, true, true),
        new(12, "core_ranged", "Ranged Weapon Core", ItemCategory.Core, true, true),
        new(13, "core_melee", "Melee Weapon Core", ItemCategory.Core, true, true),
        new(14, "core_armor", "Armor Core", ItemCategory.Core, true, true),
        new(15, "core_propulsion", "Propulsion Core", ItemCategory.Core, true, true),
        new(16, "addon_weapon", "Weapon Enhancement", ItemCategory.Addon, true, true),
        new(17, "addon_armor", "Armor Enhancement", ItemCategory.Addon, true, true),
        new(18, "addon_tuning", "Tuning Kit", ItemCategory.Addon, true, true),
        new(19, "addon_additive", "Additive", ItemCategory.Addon, true, true),
        new(20, "part_barrel_pistol", "Pistol Barrel", ItemCategory.Part, false, false),
        new(21, "part_barrel_carbine", "Carbine Barrel", ItemCategory.Part, false, false),
        new(22, "part_power_cell", "Power Cell", ItemCategory.Part, false, false),
        new(23, "part_stock_bone", "Bone Stock", ItemCategory.Part, false, false),
        new(24, "part_stock_fiber", "Fiber Stock", ItemCategory.Part, false, false),
        new(25, "part_grip", "Grip", ItemCategory.Part, false, false),
        new(26, "part_blade", "Blade", ItemCategory.Part, false, false),
        new(27, "part_knuckle_plate", "Knuckle Plate", ItemCategory.Part, false, false),
        new(28, "part_vibro_unit", "Vibro Unit", ItemCategory.Part, false, false),
        new(29, "part_padded_segment", "Padded Segment", ItemCategory.Part, false, false),
        new(30, "part_composite_segment", "Composite Segment", ItemCategory.Part, false, false),
        new(31, "part_compound_restorative", "Restorative Compound", ItemCategory.Part, false, false),
        new(32, "part_compound_stimulant", "Stimulant Compound", ItemCategory.Part, false, false),
        new(33, "part_compound_hardener", "Hardener Compound", ItemCategory.Part, false, false),
        new(34, "part_compound_vital", "Vital Compound", ItemCategory.Part, false, false),
        new(35, "part_vial", "Vial", ItemCategory.Part, false, false),
        new(36, "part_structural_module", "Structural Module", ItemCategory.Part, false, false),
        new(37, "part_wall_module", "Wall Module", ItemCategory.Part, false, false),
        new(38, "part_storage_module", "Storage Module", ItemCategory.Part, false, false),
        new(39, "part_extraction_unit", "Extraction Unit", ItemCategory.Part, false, false),
        new(40, "part_assembly_unit", "Assembly Unit", ItemCategory.Part, false, false),
        new(41, "part_engine", "Engine", ItemCategory.Part, false, false),
        new(42, "part_repulsor", "Repulsor", ItemCategory.Part, false, false),
        new(43, "part_chassis", "Chassis", ItemCategory.Part, false, false),
        new(44, "armor_padded_helmet", "Padded Helmet", ItemCategory.Armor, false, false),
        new(45, "armor_padded_torso", "Padded Torso", ItemCategory.Armor, false, false),
        new(46, "armor_padded_arms", "Padded Arms", ItemCategory.Armor, false, false),
        new(47, "armor_padded_legs", "Padded Legs", ItemCategory.Armor, false, false),
        new(48, "armor_padded_gloves", "Padded Gloves", ItemCategory.Armor, false, false),
        new(49, "armor_padded_boots", "Padded Boots", ItemCategory.Armor, false, false),
        new(50, "armor_composite_helmet", "Composite Helmet", ItemCategory.Armor, false, false),
        new(51, "armor_composite_torso", "Composite Torso", ItemCategory.Armor, false, false),
        new(52, "armor_composite_arms", "Composite Arms", ItemCategory.Armor, false, false),
        new(53, "armor_composite_legs", "Composite Legs", ItemCategory.Armor, false, false),
        new(54, "armor_composite_gloves", "Composite Gloves", ItemCategory.Armor, false, false),
        new(55, "armor_composite_boots", "Composite Boots", ItemCategory.Armor, false, false),
        new(56, "potion_stim", "Stim", ItemCategory.Potion, false, false),
        new(57, "potion_regen", "Regen", ItemCategory.Potion, false, false),
        new(58, "potion_fury", "Fury", ItemCategory.Potion, false, false),
        new(59, "potion_haste", "Haste", ItemCategory.Potion, false, false),
        new(60, "potion_ironhide", "Ironhide", ItemCategory.Potion, false, false),
        new(61, "potion_vigor", "Vigor", ItemCategory.Potion, false, false),
        new(62, "kit_weapon_repair", "Weapon Repair Kit", ItemCategory.Kit, false, false),
        new(63, "kit_armor_repair", "Armor Repair Kit", ItemCategory.Kit, false, false),
        new(64, "kit_vehicle_repair", "Vehicle Repair Kit", ItemCategory.Kit, false, false),
        new(65, "kit_structure_repair", "Structure Repair Kit", ItemCategory.Kit, false, false),
        new(66, "crate", "Crate", ItemCategory.Container, false, false),
    ];

    public const ushort Metal = 3;
    public const ushort Crystal = 4;
    public const ushort Gas = 5;
    public const ushort Chemical = 6;
    public const ushort Fiber = 7;
    public const ushort Hide = 8;
    public const ushort Bone = 9;
    public const ushort Gland = 10;
    public const ushort Venom = 11;
    public const ushort CoreRanged = 12;
    public const ushort CoreMelee = 13;
    public const ushort CoreArmor = 14;
    public const ushort CorePropulsion = 15;
    public const ushort AddonWeapon = 16;
    public const ushort AddonArmor = 17;
    public const ushort AddonTuning = 18;
    public const ushort AddonAdditive = 19;

    /// <summary>Every template, id 1 first: <c>Templates[id - 1]</c>.</summary>
    public static ReadOnlySpan<ItemTemplate> Templates => All;

    /// <summary>The template of an id; <see langword="false"/> for 0 and for an id past the table.</summary>
    public static bool TryGet(ushort id, out ItemTemplate template)
    {
        if (id == 0 || id > All.Length)
        {
            template = default;
            return false;
        }

        template = All[id - 1];
        return true;
    }

    /// <summary>The id of an asset; 0 when no template draws with it.</summary>
    public static ushort IdOf(string asset)
    {
        foreach (var t in All)
        {
            if (t.Asset == asset)
            {
                return t.Id;
            }
        }

        return 0;
    }

    /// <summary>Whether <paramref name="mk"/> is legal for <paramref name="template"/>: 1-5 for an item with a Mk, 0 for one without.</summary>
    public static bool MkFits(in ItemTemplate template, byte mk) => template.HasMk ? mk is >= 1 and <= 5 : mk == 0;
}

/// <summary>The item ledger's fixed points (inventory-loot.md § 2-3).</summary>
public static class ItemLedger
{
    /// <summary>The side items enter from and leave to. No character has raw id 0.</summary>
    public const long World = 0;

    /// <summary>The one <see cref="ItemLedgerHead"/>'s key.</summary>
    public const int HeadKey = 1;

    /// <summary>The most units one stack holds.</summary>
    public const int StackMax = 99;

    /// <summary>A character's inventory grid: slots 1..30.</summary>
    public const int Slots = 30;
}
