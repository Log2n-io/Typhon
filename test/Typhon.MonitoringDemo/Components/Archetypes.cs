using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.MonitoringDemo;

// ============================================================================
// ECS Archetypes for Monitoring Demo
// ============================================================================
// Each archetype wraps a single component type, matching the old CRUD API's
// one-component-per-entity model. IDs start at 600 to avoid collision with
// Engine.Tests (200+) and other test projects.
// ============================================================================

// --- Factory Archetypes ---

[Archetype]
class FactoryBuildingArch : Archetype<FactoryBuildingArch>
{
    public static readonly Comp<FactoryBuilding> Building = Register<FactoryBuilding>();
}

[Archetype]
class ConveyorBeltArch : Archetype<ConveyorBeltArch>
{
    public static readonly Comp<ConveyorBelt> Belt = Register<ConveyorBelt>();
}

[Archetype]
class ItemStackArch : Archetype<ItemStackArch>
{
    public static readonly Comp<ItemStack> Stack = Register<ItemStack>();
}

[Archetype]
class RecipeArch : Archetype<RecipeArch>
{
    public static readonly Comp<Recipe> Recipe = Register<Recipe>();
}

[Archetype]
class ResourceNodeArch : Archetype<ResourceNodeArch>
{
    public static readonly Comp<ResourceNode> Node = Register<ResourceNode>();
}

[Archetype]
class PowerGridArch : Archetype<PowerGridArch>
{
    public static readonly Comp<PowerGrid> Grid = Register<PowerGrid>();
}

// --- RPG Archetypes ---

[Archetype]
class CharacterArch : Archetype<CharacterArch>
{
    public static readonly Comp<Character> Character = Register<Character>();
}

[Archetype]
class WorldPositionArch : Archetype<WorldPositionArch>
{
    public static readonly Comp<WorldPosition> Position = Register<WorldPosition>();
}

[Archetype]
class CombatStatsArch : Archetype<CombatStatsArch>
{
    public static readonly Comp<CombatStats> Stats = Register<CombatStats>();
}

[Archetype]
class SkillArch : Archetype<SkillArch>
{
    public static readonly Comp<Skill> Skill = Register<Skill>();
}

[Archetype]
class QuestArch : Archetype<QuestArch>
{
    public static readonly Comp<Quest> Quest = Register<Quest>();
}

[Archetype]
class InventoryArch : Archetype<InventoryArch>
{
    public static readonly Comp<Inventory> Item = Register<Inventory>();
}
