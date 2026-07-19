using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo;

// ============================================================================
// Factory Game Components
// ============================================================================
// These components model a factory/automation game similar to Factorio or
// Satisfactory. Each component represents an ECS aspect of the game world.
// ============================================================================

/// <summary>
/// A factory building that produces items from recipes.
/// Examples: Assembler, Furnace, Chemical Plant
/// </summary>
[Component("Factory.Building", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct FactoryBuilding
{
    /// <summary>
    /// Name of the building type (e.g., "Assembler Mk2")
    /// </summary>
    [Field]
    public String64 Name;

    /// <summary>
    /// Building type enumeration
    /// </summary>
    [Field]
    public int BuildingType;

    /// <summary>
    /// Current recipe being produced (entity ID, 0 if none)
    /// </summary>
    [Field]
    public long CurrentRecipeId;

    /// <summary>
    /// Production progress (0.0 to 1.0)
    /// </summary>
    [Field]
    public float Progress;

    /// <summary>
    /// Power consumption in MW
    /// </summary>
    [Field]
    public float PowerConsumption;

    /// <summary>
    /// Grid X coordinate
    /// </summary>
    [Field]
    public int GridX;

    /// <summary>
    /// Grid Y coordinate
    /// </summary>
    [Field]
    public int GridY;

    /// <summary>
    /// Is the building currently active?
    /// </summary>
    [Field]
    public bool IsActive;

    /// <summary>
    /// Building efficiency (0.0 to 1.0+)
    /// </summary>
    [Field]
    public float Efficiency;

    public static FactoryBuilding Create(Random rand, int type)
    {
        var names = new[] { "Assembler", "Furnace", "Chemical Plant", "Refinery", "Constructor" };
        return new FactoryBuilding
        {
            Name = (String64)names[type % names.Length],
            BuildingType = type,
            CurrentRecipeId = 0,
            Progress = 0f,
            PowerConsumption = 1.5f + (float)(rand.NextDouble() * 10),
            GridX = rand.Next(-1000, 1000),
            GridY = rand.Next(-1000, 1000),
            IsActive = true,
            Efficiency = 1.0f
        };
    }
}

/// <summary>
/// A conveyor belt segment that moves items between buildings.
/// </summary>
[Component("Factory.ConveyorBelt", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct ConveyorBelt
{
    /// <summary>
    /// Belt tier (affects speed)
    /// </summary>
    [Field]
    public int Tier;

    /// <summary>
    /// Items per minute throughput
    /// </summary>
    [Field]
    public int ItemsPerMinute;

    /// <summary>
    /// Source building entity ID
    /// </summary>
    [Field]
    public long SourceBuildingId;

    /// <summary>
    /// Destination building entity ID
    /// </summary>
    [Field]
    public long DestBuildingId;

    /// <summary>
    /// Belt length in tiles
    /// </summary>
    [Field]
    public int Length;

    /// <summary>
    /// Current item count on belt
    /// </summary>
    [Field]
    public int ItemCount;

    /// <summary>
    /// Item type being transported (0 = empty)
    /// </summary>
    [Field]
    public int ItemType;

    public static ConveyorBelt Create(Random rand, int tier, long sourceId, long destId)
    {
        var throughputs = new[] { 60, 120, 270, 480, 780 };
        return new ConveyorBelt
        {
            Tier = tier,
            ItemsPerMinute = throughputs[Math.Min(tier, throughputs.Length - 1)],
            SourceBuildingId = sourceId,
            DestBuildingId = destId,
            Length = rand.Next(1, 20),
            ItemCount = rand.Next(0, 10),
            ItemType = rand.Next(1, 100)
        };
    }
}

/// <summary>
/// A stack of items in storage or transit.
/// </summary>
[Component("Factory.ItemStack", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct ItemStack
{
    /// <summary>
    /// Item name
    /// </summary>
    [Field]
    public String64 ItemName;

    /// <summary>
    /// Item type ID
    /// </summary>
    [Field]
    public int ItemType;

    /// <summary>
    /// Current quantity
    /// </summary>
    [Field]
    public int Quantity;

    /// <summary>
    /// Maximum stack size
    /// </summary>
    [Field]
    public int MaxStackSize;

    /// <summary>
    /// Container entity ID (building, chest, etc.)
    /// </summary>
    [Field]
    public long ContainerId;

    /// <summary>
    /// Slot index in container
    /// </summary>
    [Field]
    public int SlotIndex;

    public static ItemStack Create(Random rand, int itemType, long containerId)
    {
        var items = new[] { "Iron Ore", "Copper Ore", "Iron Plate", "Copper Wire", "Circuit", "Steel" };
        return new ItemStack
        {
            ItemName = (String64)items[itemType % items.Length],
            ItemType = itemType,
            Quantity = rand.Next(1, 100),
            MaxStackSize = 100,
            ContainerId = containerId,
            SlotIndex = rand.Next(0, 48)
        };
    }
}

/// <summary>
/// A production recipe that transforms inputs into outputs.
/// </summary>
[Component("Factory.Recipe", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct Recipe
{
    /// <summary>
    /// Recipe name
    /// </summary>
    [Field]
    public String64 Name;

    /// <summary>
    /// Production time in seconds
    /// </summary>
    [Field]
    public float CraftingTime;

    /// <summary>
    /// Primary input item type
    /// </summary>
    [Field]
    public int InputType1;

    /// <summary>
    /// Primary input quantity
    /// </summary>
    [Field]
    public int InputQty1;

    /// <summary>
    /// Secondary input item type (0 = none)
    /// </summary>
    [Field]
    public int InputType2;

    /// <summary>
    /// Secondary input quantity
    /// </summary>
    [Field]
    public int InputQty2;

    /// <summary>
    /// Output item type
    /// </summary>
    [Field]
    public int OutputType;

    /// <summary>
    /// Output quantity per craft
    /// </summary>
    [Field]
    public int OutputQty;

    public static Recipe Create(Random rand, string name, int outputType)
    {
        return new Recipe
        {
            Name = (String64)name,
            CraftingTime = 0.5f + (float)(rand.NextDouble() * 5),
            InputType1 = rand.Next(1, 50),
            InputQty1 = rand.Next(1, 10),
            InputType2 = rand.Next(0, 2) == 0 ? 0 : rand.Next(1, 50),
            InputQty2 = rand.Next(0, 5),
            OutputType = outputType,
            OutputQty = rand.Next(1, 4)
        };
    }
}

/// <summary>
/// A production queue for a building.
/// </summary>
[Component("Factory.ProductionQueue", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct ProductionQueue
{
    /// <summary>
    /// Building this queue belongs to
    /// </summary>
    [Field]
    public long BuildingId;

    /// <summary>
    /// Recipe to produce
    /// </summary>
    [Field]
    public long RecipeId;

    /// <summary>
    /// Quantity to produce (-1 = infinite)
    /// </summary>
    [Field]
    public int TargetQuantity;

    /// <summary>
    /// Quantity already produced
    /// </summary>
    [Field]
    public int ProducedQuantity;

    /// <summary>
    /// Priority (lower = higher priority)
    /// </summary>
    [Field]
    public int Priority;

    public static ProductionQueue Create(Random rand, long buildingId, long recipeId)
    {
        return new ProductionQueue
        {
            BuildingId = buildingId,
            RecipeId = recipeId,
            TargetQuantity = rand.Next(-1, 1000),
            ProducedQuantity = rand.Next(0, 100),
            Priority = rand.Next(0, 10)
        };
    }
}

/// <summary>
/// A natural resource node that can be mined.
/// </summary>
[Component("Factory.ResourceNode", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct ResourceNode
{
    /// <summary>
    /// Resource type name
    /// </summary>
    [Field]
    public String64 ResourceName;

    /// <summary>
    /// Resource type ID
    /// </summary>
    [Field]
    public int ResourceType;

    /// <summary>
    /// Remaining quantity (-1 = infinite)
    /// </summary>
    [Field]
    public long RemainingQuantity;

    /// <summary>
    /// Extraction rate per minute
    /// </summary>
    [Field]
    public int ExtractionRate;

    /// <summary>
    /// Purity level (0 = impure, 1 = normal, 2 = pure)
    /// </summary>
    [Field]
    public int Purity;

    /// <summary>
    /// World X coordinate
    /// </summary>
    [Field]
    public float WorldX;

    /// <summary>
    /// World Y coordinate
    /// </summary>
    [Field]
    public float WorldY;

    /// <summary>
    /// Is this node being mined?
    /// </summary>
    [Field]
    public bool IsBeingMined;

    public static ResourceNode Create(Random rand, int resourceType)
    {
        var resources = new[] { "Iron Ore", "Copper Ore", "Coal", "Limestone", "Crude Oil", "Uranium" };
        return new ResourceNode
        {
            ResourceName = (String64)resources[resourceType % resources.Length],
            ResourceType = resourceType,
            RemainingQuantity = rand.Next(0, 2) == 0 ? -1 : rand.Next(10000, 1000000),
            ExtractionRate = rand.Next(30, 120),
            Purity = rand.Next(0, 3),
            WorldX = (float)(rand.NextDouble() * 10000 - 5000),
            WorldY = (float)(rand.NextDouble() * 10000 - 5000),
            IsBeingMined = rand.Next(0, 2) == 1
        };
    }
}

/// <summary>
/// A power grid segment tracking electricity distribution.
/// </summary>
[Component("Factory.PowerGrid", 1)]
[StructLayout(LayoutKind.Sequential)]
public partial struct PowerGrid
{
    /// <summary>
    /// Grid identifier
    /// </summary>
    [Field]
    public int GridId;

    /// <summary>
    /// Total power production in MW
    /// </summary>
    [Field]
    public float Production;

    /// <summary>
    /// Total power consumption in MW
    /// </summary>
    [Field]
    public float Consumption;

    /// <summary>
    /// Maximum capacity in MW
    /// </summary>
    [Field]
    public float Capacity;

    /// <summary>
    /// Battery storage in MJ
    /// </summary>
    [Field]
    public float BatteryStored;

    /// <summary>
    /// Maximum battery capacity in MJ
    /// </summary>
    [Field]
    public float BatteryCapacity;

    /// <summary>
    /// Is the grid overloaded?
    /// </summary>
    [Field]
    public bool IsOverloaded;

    public static PowerGrid Create(Random rand, int gridId)
    {
        var production = (float)(rand.NextDouble() * 1000);
        var consumption = production * (0.5f + (float)(rand.NextDouble() * 0.8));
        return new PowerGrid
        {
            GridId = gridId,
            Production = production,
            Consumption = consumption,
            Capacity = production * 1.2f,
            BatteryStored = (float)(rand.NextDouble() * 10000),
            BatteryCapacity = 10000f,
            IsOverloaded = consumption > production
        };
    }
}
