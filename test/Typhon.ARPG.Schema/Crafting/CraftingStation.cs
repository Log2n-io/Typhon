using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.ARPG.Schema;

/// <summary>
/// Physical crafting station placed in the world: forge, oven, alchemy lab, enchanting table.
/// Paired with Position and Inventory. When IsAutomatic=true, auto-processes recipes
/// from connected inventory (like a furnace auto-smelting ore).
/// </summary>
[Component("ARPG.CraftingStation", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CraftingStation
{
    [Field] /*[Index(AllowMultiple = true)]*/ public int StationTypeId;
    [Field] public String64 StationName;

    [Field] /*[Index]*/ public long OwnerEntityId;

    [Field] public float CraftingSpeedMultiplier;

    // Current crafting state
    [Field] public int CurrentRecipeId;
    [Field] public float Progress;

    // Automation
    [Field] /*[Index]*/ public bool IsAutomatic;

    // Fuel (0 = no fuel needed, e.g. workbench)
    [Field] public int FuelTypeId;
    [Field] public float FuelRemaining;
    [Field] public float FuelBurnRate;

    [Field] /*[Index]*/ public bool IsActive;
}
