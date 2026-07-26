using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Samples.Swg;

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// World Shard — Full tier. The rich counterpart to Light's single Character: a living shard of Players (with guilds,
// wallets, inventories), the Structures they own (Harvesters + Factories), the Resource taxonomy and Deposits they
// gather from, and the Recipes + Items they craft. Where Light is the minimal slice a newcomer copies, Full is the
// reference schema that gives the Workbench (Schema / Data / Query / File-Map views) and the MonitoringDemo real,
// relationship-rich data to render and drive load against.
//
// SAME DISCIPLINE AS LIGHT: each component sits in the storage mode its ACCESS PATTERN calls for — never a blanket
// default. That judgment is the point; the mode is not incidental.
//   • SingleVersion — hot / high-frequency / loss-tolerant: every spatial Position, a harvester's filling Hopper, a
//     factory's draining PowerSupply, a harvester's MaintenanceState. Lock-free, no MVCC tax — exactly Light's Transform.
//   • Versioned — durable, transactional, ACID: identity + progression (Player), the economy (Wallet, Guild.Treasury),
//     ownership + inventory (Item, StructureOwner, ItemOwner), the durable catalog (ResourceType, Recipe, Deposit).
//     These are records you must not lose or read torn — what MVCC + WAL are for, touched at event cadence not per tick.
//   • Transient — pure scratch: Session (online/offline connection state), dropped on restart by design — Light's Intent.
//
// The relational surface (EntityLink<T> FKs incl. cascade-delete and a self-referential FK, ComponentCollection,
// [ComponentFamily] grouping, polymorphic Structure) is here because a real shard HAS relationships and a fixture must
// exercise them for the Workbench to render them — NOT because you should reach for a foreign key by default. Each FK
// models a genuine ownership / membership / taxonomy edge; being fixture data, none is chased in a hot per-tick loop.
//
// NOTE on multi-value FKs: ComponentCollection<T> elements are opaque VSBS payloads and cannot be indexed FKs, so
// RecipeSlot.ClassReq is a plain resource-type id (int), not an EntityLink. NOTE on spatial: a single shared Position
// struct cannot present different Category/Mode per archetype (the attribute is per-struct, compile-time), so there are
// three distinct *Position structs; StructurePosition is still shared by Harvester + Factory.
//
// Every field carries [Field] — required by the Typhon.Shell AssemblySchemaLoader (skips unmarked fields) and harmless
// to the engine registration path (which reads all public fields regardless).
//
// Paired with SwgFullArchetypes.cs, which groups these components into the 9 archetypes. Component identities are
// prefixed "Swg."; archetype ids are engine-assigned (feature #514 — no author-set id).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// ── ComponentCollection element payloads (plain blittable structs, NOT components) ──────────────────────────────────

/// <summary>One ingredient slot of a <see cref="Recipe"/>. Carried as a <see cref="ComponentCollection{T}"/> element
/// (1..8 per recipe). ClassReq is a plain ResourceType id, not an EntityLink — CC element fields cannot be indexed FKs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RecipeSlot
{
    public int SlotIndex;
    public int ClassReq;
    public int MinUnits;
}

/// <summary>One rolled affix on an <see cref="Item"/>. Carried as a <see cref="ComponentCollection{T}"/> element
/// (0..MaxAffixesPerItem per item).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ItemAffix
{
    public int AffixType;
    public int Value;
}

// ── Social family ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A player guild. Unique by Name; queryable by Faction / MemberCount.</summary>
[Component("Swg.Guild", 1)]
[ComponentFamily("Social")]
[StructLayout(LayoutKind.Sequential)]
public struct Guild
{
    [Field] [Index] public String64 Name;
    [Field] [Index(AllowMultiple = true)] public int Faction;
    [Field] [Index(AllowMultiple = true)] public int MemberCount;
    [Field] public long Treasury;
}

/// <summary>A player's guild membership (FK → Guild) plus rank.</summary>
[Component("Swg.Membership", 1)]
[ComponentFamily("Social")]
[StructLayout(LayoutKind.Sequential)]
public struct Membership
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<GuildArch> Guild;
    [Field] public int GuildRank;
}

/// <summary>Core player identity. Unique by AccountId; queryable by Level / ProfessionId. Name is an unindexed
/// String64 — PlayerArch is cluster-eligible (SV Position + Transient Session), and cluster archetypes route all
/// indexes through one fixed-stride segment that can't hold a 64-byte String64 index. AccountId (a long) is the
/// unique-index demonstration here; the String64 unique index is exercised by Guild/ResourceType/Recipe.</summary>
[Component("Swg.Player", 1)]
[ComponentFamily("Social")]
[StructLayout(LayoutKind.Sequential)]
public struct Player
{
    [Field] public String64 Name;
    [Field] [Index] public long AccountId;
    [Field] [Index(AllowMultiple = true)] public int Level;
    [Field] [Index(AllowMultiple = true)] public int ProfessionId;
    [Field] public long CreatedAt;
}

/// <summary>A player's credit balances.</summary>
[Component("Swg.Wallet", 1)]
[ComponentFamily("Social")]
[StructLayout(LayoutKind.Sequential)]
public struct Wallet
{
    [Field] public long Credits;
    [Field] public long BankCredits;
}

/// <summary>Transient (heap-only) connection state. Enabled = online, Disabled = offline. Lost on restart by design —
/// the only Transient-storage representative, and what makes Player cluster-eligible.</summary>
[Component("Swg.Session", 1, StorageMode = StorageMode.Transient)]
[ComponentFamily("Social")]
[StructLayout(LayoutKind.Sequential)]
public struct Session
{
    [Field] public long ConnectionId;
    [Field] public int LatencyMs;
}

// ── Industry family ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Resource-class taxonomy node. Unique by Name; self-referential FK (Parent → ResourceType) forms the tree.</summary>
[Component("Swg.ResourceType", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct ResourceType
{
    [Field] [Index] public String64 Name;
    [Field] [Index(AllowMultiple = true)] public int Tier;
    [Field] [Index(AllowMultiple = true)] public EntityLink<ResourceTypeArch> Parent;
}

/// <summary>A crafting recipe. Unique by Name; FK PrimaryClass → ResourceType. Carries 1..8 ingredient slots in a
/// <see cref="ComponentCollection{T}"/> (multi-value).</summary>
[Component("Swg.Recipe", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct Recipe
{
    [Field] [Index] public String64 Name;
    [Field] [Index(AllowMultiple = true)] public int Tier;
    [Field] [Index(AllowMultiple = true)] public int ProfessionReq;
    [Field] [Index(AllowMultiple = true)] public EntityLink<ResourceTypeArch> PrimaryClass;
    [Field] public ComponentCollection<RecipeSlot> Slots;
}

/// <summary>A resource deposit instance. FK Type → ResourceType. Enable/Disable models depletion (disabled = depleted,
/// data stays readable). Paired with DepositPosition (static spatial).</summary>
[Component("Swg.Deposit", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct Deposit
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<ResourceTypeArch> Type;
    [Field] [Index(AllowMultiple = true)] public int Quality;
    [Field] public int Concentration;
    [Field] [Index(AllowMultiple = true)] public long DepletesAt;
}

/// <summary>Abstract structure base (queried via Query&lt;StructureArch&gt; to match Harvester + Factory). Never spawned
/// directly. StructureOwner.Owner → Player cascades on player delete.</summary>
[Component("Swg.Structure", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct Structure
{
    [Field] [Index(AllowMultiple = true)] public int TypeCode;
    [Field] public long PlacedAt;
    [Field] public int Maintenance;
}

/// <summary>Structure ownership FK → Player, cascade-delete: deleting a player removes their structures.</summary>
[Component("Swg.StructureOwner", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct StructureOwner
{
    [Field] [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)] public EntityLink<PlayerArch> Owner;
}

/// <summary>A harvester's output hopper. FK Class → ResourceType. <see cref="Amount"/> is the resource level that
/// climbs every tick as the harvester extracts — a HOT, high-frequency accumulator, so this is <b>SingleVersion</b>
/// (like Light's per-tick state), not Versioned: pushing a per-tick counter through MVCC would be pure write tax for
/// data that needs no isolation or history. Contrast the Versioned Wallet, touched only on an economic event. The
/// mode changed from Versioned, so the revision is bumped to 2 (StorageMode is immutable per name+revision).</summary>
[Component("Swg.Hopper", 2, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct Hopper
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<ResourceTypeArch> Class;
    [Field] public int Amount;
    [Field] public int Rate;
}

/// <summary>The deposit a harvester is extracting from. FK → ResourceDeposit.</summary>
[Component("Swg.HarvesterTarget", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct HarvesterTarget
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<ResourceDepositArch> Deposit;
}

/// <summary>SingleVersion maintenance pool. Enable/Disable models broken (disabled) vs operational harvesters.</summary>
[Component("Swg.MaintenanceState", 1, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct MaintenanceState
{
    [Field] public long PaidUntil;
}

/// <summary>A factory's production config. FK Recipe → Recipe.</summary>
[Component("Swg.FactoryConfig", 1)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct FactoryConfig
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<RecipeArch> Recipe;
    [Field] public int RemainingRuns;
}

/// <summary>SingleVersion power reserve. Enable/Disable models idle (disabled, out of credits) factories.</summary>
[Component("Swg.PowerSupply", 1, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("Industry")]
[StructLayout(LayoutKind.Sequential)]
public struct PowerSupply
{
    [Field] public long CreditsRemaining;
}

// ── Item family ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A crafted item instance. FK Recipe → Recipe. Carries 0..MaxAffixesPerItem affixes in a
/// <see cref="ComponentCollection{T}"/>.</summary>
[Component("Swg.Item", 1)]
[ComponentFamily("Item")]
[StructLayout(LayoutKind.Sequential)]
public struct Item
{
    [Field] [Index(AllowMultiple = true)] public EntityLink<RecipeArch> Recipe;
    [Field] [Index(AllowMultiple = true)] public int ItemType;
    [Field] [Index(AllowMultiple = true)] public int Quality;
    [Field] public int Decay;
    [Field] public ComponentCollection<ItemAffix> Affixes;
}

/// <summary>Item ownership FK → Player, cascade-delete: deleting a player removes their items.</summary>
[Component("Swg.ItemOwner", 1)]
[ComponentFamily("Item")]
[StructLayout(LayoutKind.Sequential)]
public struct ItemOwner
{
    [Field] [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)] public EntityLink<PlayerArch> Owner;
}

// ── World family — three distinct spatial Position structs (one per Category/Mode combination) ───────────────────────

/// <summary>Player location — Dynamic spatial, Category=Player. SingleVersion (hot tick storage).</summary>
[Component("Swg.PlayerPosition", 1, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("World")]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerPosition
{
    [Field] [SpatialIndex(1.0f, Mode = SpatialMode.Dynamic, Category = SwgCategory.Player)] public AABB2F Bounds;
}

/// <summary>Deposit location — Static spatial (immobile, skips tick-fence), Category=Deposit. SingleVersion.</summary>
[Component("Swg.DepositPosition", 1, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("World")]
[StructLayout(LayoutKind.Sequential)]
public struct DepositPosition
{
    [Field] [SpatialIndex(1.0f, Mode = SpatialMode.Static, Category = SwgCategory.Deposit)] public AABB2F Bounds;
}

/// <summary>Structure location — Dynamic spatial, Category=Structure. SingleVersion. SHARED by Harvester + Factory
/// (exercises "same component across archetypes").</summary>
[Component("Swg.StructurePosition", 1, StorageMode = StorageMode.SingleVersion)]
[ComponentFamily("World")]
[StructLayout(LayoutKind.Sequential)]
public struct StructurePosition
{
    [Field] [SpatialIndex(1.0f, Mode = SpatialMode.Dynamic, Category = SwgCategory.Structure)] public AABB2F Bounds;
}

/// <summary>Spatial category bitmask values — one bit per spatially-distinct entity kind, so broadphase queries can
/// filter by kind (e.g. "structures near point P").</summary>
public static class SwgCategory
{
    public const uint Player = 1u << 0;
    public const uint Deposit = 1u << 1;
    public const uint Structure = 1u << 2;
}
