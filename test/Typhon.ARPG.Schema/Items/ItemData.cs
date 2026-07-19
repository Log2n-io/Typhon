using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Core item data for all item types: equipment, materials, consumables, crafting results.
/// Items are standalone entities; ownership and location tracked via OwnerId and DropLocation.
/// </summary>
[Component("ARPG.ItemData", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct ItemData
{
    [Field] [Index(AllowMultiple = true)] public int ItemTypeId;
    [Field] public String64 ItemName;
    [Field] [Index(AllowMultiple = true)] public int Rarity;
    [Field] [Index(AllowMultiple = true)] public int ItemCategory;

    [Field] [Index(AllowMultiple = true)] public long OwnerId;

    [Field] public int ItemLevel;
    [Field] public int RequiredLevel;

    // Stacking (materials/consumables stack, equipment doesn't)
    [Field] public int StackCount;
    [Field] public int MaxStack;

    [Field] public bool IsEquipped;

    // Ground drop location (when OwnerId == 0)
    [Field] public Point3F DropLocation;

    // Base stats (for equipment; 0 for non-equipment)
    [Field] public int BaseMinDamage;
    [Field] public int BaseMaxDamage;
    [Field] public int BaseArmor;
    [Field] public int BaseBlockChance;
}
