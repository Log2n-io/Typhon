using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Fixtures;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SWG fixture archetypes — IDs 820–828. The previous fixture occupied 800–806; the gap marks the generation boundary
// (no persisted DB mixes the two — fixtures are always regenerated fresh). Avoids unit-test (200-series) and engine
// (100-101) ranges.
//
// Cluster-eligibility (≥1 SingleVersion/Transient slot, no Transient-with-indexed-field): Player (823),
// ResourceDeposit (824), Harvester (826), Factory (827) are cluster-eligible. Recipe (822) and Item (828) — the
// ComponentCollection carriers — are pure Versioned, hence NON-cluster (the original CC storage path).
//
// Polymorphic inheritance: Structure (825) is the abstract base; Harvester (826) and Factory (827) inherit it via CRTP
// (Archetype<TSelf, StructureArch>). Query<StructureArch> matches both leaves; QueryExact<HarvesterArch> only the leaf.
// Structure is never spawned directly — only Harvester / Factory are.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Archetype(820, 1, "Guild")]
public class GuildArch : Archetype<GuildArch>
{
    public static readonly Comp<Guild> Guild = Register<Guild>();
}

[Archetype(821, 1, "Resource Type")]
public class ResourceTypeArch : Archetype<ResourceTypeArch>
{
    public static readonly Comp<ResourceType> ResourceType = Register<ResourceType>();
}

[Archetype(822, 1, "Receipe")]
public class RecipeArch : Archetype<RecipeArch>
{
    public static readonly Comp<Recipe> Recipe = Register<Recipe>();
}

[Archetype(823, 1, "Player")]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<Player> Player = Register<Player>();
    public static readonly Comp<Membership> Membership = Register<Membership>();
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
    public static readonly Comp<PlayerPosition> Position = Register<PlayerPosition>();
    public static readonly Comp<Session> Session = Register<Session>();
}

[Archetype(824, 1, "Resource Deposit")]
public class ResourceDepositArch : Archetype<ResourceDepositArch>
{
    public static readonly Comp<Deposit> Deposit = Register<Deposit>();
    public static readonly Comp<DepositPosition> Position = Register<DepositPosition>();
}

[Archetype(825, 1, "Structure")]
public class StructureArch : Archetype<StructureArch>
{
    public static readonly Comp<Structure> Structure = Register<Structure>();
    public static readonly Comp<StructureOwner> Owner = Register<StructureOwner>();
}

[Archetype(826, 1, "Harvester")]
public class HarvesterArch : Archetype<HarvesterArch, StructureArch>
{
    public static readonly Comp<Hopper> Hopper = Register<Hopper>();
    public static readonly Comp<HarvesterTarget> Target = Register<HarvesterTarget>();
    public static readonly Comp<MaintenanceState> Maintenance = Register<MaintenanceState>();
    public static readonly Comp<StructurePosition> Position = Register<StructurePosition>();
}

[Archetype(827,1, "Factory")]
public class FactoryArch : Archetype<FactoryArch, StructureArch>
{
    public static readonly Comp<FactoryConfig> Config = Register<FactoryConfig>();
    public static readonly Comp<PowerSupply> Power = Register<PowerSupply>();
    public static readonly Comp<StructurePosition> Position = Register<StructurePosition>();
}

[Archetype(828, 1, "Item")]
public class ItemArch : Archetype<ItemArch>
{
    public static readonly Comp<Item> Item = Register<Item>();
    public static readonly Comp<ItemOwner> Owner = Register<ItemOwner>();
}
