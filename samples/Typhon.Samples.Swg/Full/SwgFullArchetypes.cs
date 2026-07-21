using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Samples.Swg;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SWG Full archetypes — 9 archetypes grouping the SwgFullComponents. Ids are engine-assigned (feature #514 — no
// author-set id); the [Archetype(revision, alias)] arguments are the schema revision and the Workbench display alias.
//
// Cluster-eligibility (≥1 SingleVersion/Transient slot, no Transient-with-indexed-field): Player, ResourceDeposit,
// Harvester, Factory are cluster-eligible. Recipe and Item — the ComponentCollection carriers — are pure Versioned,
// hence NON-cluster.
//
// Polymorphic inheritance: Structure is the abstract base; Harvester and Factory inherit it via CRTP
// (Archetype<TSelf, StructureArch>). Query<StructureArch> matches both leaves; QueryExact<HarvesterArch> only the leaf.
// Structure is never spawned directly — only Harvester / Factory are.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Archetype(1, "Guild")]
public class GuildArch : Archetype<GuildArch>
{
    public static readonly Comp<Guild> Guild = Register<Guild>();
}

[Archetype(1, "Resource Type")]
public class ResourceTypeArch : Archetype<ResourceTypeArch>
{
    public static readonly Comp<ResourceType> ResourceType = Register<ResourceType>();
}

[Archetype(1, "Receipe")]
public class RecipeArch : Archetype<RecipeArch>
{
    public static readonly Comp<Recipe> Recipe = Register<Recipe>();
}

[Archetype(1, "Player")]
public class PlayerArch : Archetype<PlayerArch>
{
    public static readonly Comp<Player> Player = Register<Player>();
    public static readonly Comp<Membership> Membership = Register<Membership>();
    public static readonly Comp<Wallet> Wallet = Register<Wallet>();
    public static readonly Comp<PlayerPosition> Position = Register<PlayerPosition>();
    public static readonly Comp<Session> Session = Register<Session>();
}

[Archetype(1, "Resource Deposit")]
public class ResourceDepositArch : Archetype<ResourceDepositArch>
{
    public static readonly Comp<Deposit> Deposit = Register<Deposit>();
    public static readonly Comp<DepositPosition> Position = Register<DepositPosition>();
}

[Archetype(1, "Structure")]
public class StructureArch : Archetype<StructureArch>
{
    public static readonly Comp<Structure> Structure = Register<Structure>();
    public static readonly Comp<StructureOwner> Owner = Register<StructureOwner>();
}

[Archetype(1, "Harvester")]
public class HarvesterArch : Archetype<HarvesterArch, StructureArch>
{
    public static readonly Comp<Hopper> Hopper = Register<Hopper>();
    public static readonly Comp<HarvesterTarget> Target = Register<HarvesterTarget>();
    public static readonly Comp<MaintenanceState> Maintenance = Register<MaintenanceState>();
    public static readonly Comp<StructurePosition> Position = Register<StructurePosition>();
}

[Archetype(1, "Factory")]
public class FactoryArch : Archetype<FactoryArch, StructureArch>
{
    public static readonly Comp<FactoryConfig> Config = Register<FactoryConfig>();
    public static readonly Comp<PowerSupply> Power = Register<PowerSupply>();
    public static readonly Comp<StructurePosition> Position = Register<StructurePosition>();
}

[Archetype(1, "Item")]
public class ItemArch : Archetype<ItemArch>
{
    public static readonly Comp<Item> Item = Register<Item>();
    public static readonly Comp<ItemOwner> Owner = Register<ItemOwner>();
}
