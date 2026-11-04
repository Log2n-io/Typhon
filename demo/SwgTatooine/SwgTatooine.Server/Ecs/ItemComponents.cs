using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// What an item ledger row records (inventory-loot.md § 3.2). Items enter from and leave to <see cref="ItemLedger.World"/>; everything else moves between two
/// characters. Kinds 4 and 8-15 are reserved (give, craft, consume, decay, refine, pack, unpack, extract, standard), and the audit refuses them until they exist.
/// The bazaar's (bazaar.md § 2.2) are 5-7 and 16: a listing's side is the listing's raw id, owner and stack at once.
/// </summary>
public static class ItemMoveKind
{
    /// <summary>A kill's drop, from World. <see cref="ItemLedgerEntry.Ref"/> is the victim's raw id.</summary>
    public const byte Kill = 1;

    /// <summary>A broken wild lair's cache, from World. <see cref="ItemLedgerEntry.Ref"/> is the lair's raw id.</summary>
    public const byte Lair = 2;

    /// <summary>A mission lair's cache, from World, minted with the mission's reward. <see cref="ItemLedgerEntry.Ref"/> is the held mission's raw id.</summary>
    public const byte Mission = 3;

    /// <summary>Units listed on the bazaar: seller → the listing (its escrow). <see cref="ItemLedgerEntry.Ref"/> is the listing's raw id.</summary>
    public const byte List = 5;

    /// <summary>A sold listing collected: the listing → its buyer. <see cref="ItemLedgerEntry.Ref"/> is the listing's raw id.</summary>
    public const byte Buy = 6;

    /// <summary>
    /// Units sold to the vendor: seller → World, destroyed. <see cref="ItemLedgerEntry.Ref"/> is the <see cref="LedgerEntry.Seq"/> of the sale's
    /// <see cref="TransferKind.Vendor"/> credit row.
    /// </summary>
    public const byte Vendor = 7;

    /// <summary>
    /// Standard issue bought from the vendor (crafting-catalog.md § 15.3): World → buyer, minted. <see cref="ItemLedgerEntry.Ref"/> is the
    /// <see cref="LedgerEntry.Seq"/> of the sale's <see cref="TransferKind.Standard"/> credit row.
    /// </summary>
    public const byte Standard = 8;

    /// <summary>The welcome pack (crafting-build.md O-1): World → a new character, minted when its wallet opens. <see cref="ItemLedgerEntry.Ref"/> is 0.</summary>
    public const byte Grant = 9;

    /// <summary>A craft's inputs (crafting-build.md C2): the crafter → World, consumed when the craft starts. <see cref="ItemLedgerEntry.Ref"/> is the craft's raw id.</summary>
    public const byte Craft = 10;

    /// <summary>A craft's result: World → the crafter, minted when it finishes. <see cref="ItemLedgerEntry.Ref"/> is the craft's raw id.</summary>
    public const byte Make = 11;

    /// <summary>Worn out with no repair left (crafting-catalog.md § 9.2): the owner → World, destroyed. <see cref="ItemLedgerEntry.Ref"/> is 0.</summary>
    public const byte Decay = 12;

    /// <summary>A repair kit consumed by a repair: the owner → World. <see cref="ItemLedgerEntry.Ref"/> is the repaired item's raw id.</summary>
    public const byte Repair = 13;

    /// <summary>
    /// A salvage (Q10): the item, owner → World, destroyed; and the materials it gives back, World → the owner. <see cref="ItemLedgerEntry.Ref"/> is the
    /// salvaged item's raw id on every row.
    /// </summary>
    public const byte Salvage = 14;

    /// <summary>A cancelled or expired listing collected: the listing → its seller. <see cref="ItemLedgerEntry.Ref"/> is the listing's raw id.</summary>
    public const byte Return = 16;

    /// <summary>
    /// Items an operator gave a character from the back office: World → the character, minted, at any Mk. <see cref="ItemLedgerEntry.Ref"/> is 0.
    /// </summary>
    public const byte AdminGrant = 17;

    /// <summary>
    /// Items an operator took from a character from the back office: the character → World, destroyed. <see cref="ItemLedgerEntry.Ref"/> is 0.
    /// </summary>
    public const byte AdminTake = 18;

    /// <summary>
    /// A refine (crafting-catalog.md § 11.1): N units of Mk <i>k</i>, the refiner → World, destroyed; and one of Mk <i>k</i>+1 for each N, World → the
    /// refiner, of the same template. <see cref="ItemLedgerEntry.Ref"/> is the seq of the refine's first row, on every row of it.
    /// </summary>
    public const byte Refine = 19;

    /// <summary>
    /// A pack (crafting-catalog.md § 11.2): an item, or a crate, into an identical crate or item of the same owner — one row from the absorbed stack to the
    /// crate, both sides the owner; the absorbed entity is destroyed. <see cref="ItemLedgerEntry.Ref"/> is the crate's raw id.
    /// </summary>
    public const byte Pack = 20;

    /// <summary>
    /// An unpack: one item out of a crate into a new instance in a free slot of the same owner, a row each. <see cref="ItemLedgerEntry.Ref"/> is the
    /// crate's raw id.
    /// </summary>
    public const byte Unpack = 21;

    /// <summary>A kit placed (crafting-build.md § 10): the kit, the owner → World, destroyed as it becomes the installation. <see cref="ItemLedgerEntry.Ref"/>
    /// is the installation's raw id.</summary>
    public const byte Place = 22;

    /// <summary>An installation's output collected: units World → its owner, minted. <see cref="ItemLedgerEntry.Ref"/> is the installation's raw id.</summary>
    public const byte Extract = 23;

    /// <summary>An installation picked up: its kit World → its owner, minted with the kit's stats. <see cref="ItemLedgerEntry.Ref"/> is the installation's
    /// raw id.</summary>
    public const byte Pickup = 24;

    /// <summary>
    /// Units moved between two stacks of one owner from the inventory window (inventory-loot.md § 10): a split into a free slot, or a merge into a stack of
    /// the same key — both sides the owner, a row per pair of stacks. Units only: an item with stats moves slot without a row, as an equip does.
    /// <see cref="ItemLedgerEntry.Ref"/> is 0.
    /// </summary>
    public const byte Move = 25;

    /// <summary>
    /// Units or an item the owner threw away from the inventory window (inventory-loot.md § 10): the owner → World, destroyed. <see
    /// cref="ItemLedgerEntry.Ref"/>
    /// is 0.
    /// </summary>
    public const byte Discard = 26;

    /// <summary>The name a kind is shown by, over HTTP and in the audit.</summary>
    public static string NameOf(byte kind) => kind switch
    {
        Kill => "kill",
        Lair => "lair",
        Mission => "mission",
        List => "list",
        Buy => "buy",
        Vendor => "vendor",
        Standard => "standard",
        Grant => "grant",
        Craft => "craft",
        Make => "make",
        Decay => "decay",
        Repair => "repair",
        Salvage => "salvage",
        Return => "return",
        AdminGrant => "admingrant",
        AdminTake => "admintake",
        Refine => "refine",
        Pack => "pack",
        Unpack => "unpack",
        Place => "place",
        Extract => "extract",
        Pickup => "pickup",
        Move => "move",
        Discard => "discard",
        _ => "unknown",
    };
}

/// <summary>
/// A stack of one item template at one Mk in one character's inventory (inventory-loot.md § 2): the (owner, template, Mk) identity of a fungible item, at most
/// <see cref="ItemLedger.StackMax"/> units, in one slot of <see cref="ItemLedger.Slots"/>.
/// </summary>
/// <remarks>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial, keyed on the character</b> — the wallet's reasons (<see cref="Wallet"/>): it is value, so it rides the
/// WAL, and the inventory endpoint and the audit read it off the tick under <c>EW-02</c>. Written by <c>ItemTransfer</c> alone.
/// </remarks>
[Component("Swg.ItemStack", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ItemStack
{
    /// <summary>The character's raw id; a character holds up to <see cref="ItemLedger.Slots"/> stacks.</summary>
    [Field, Index(AllowMultiple = true)] public long Owner;

    /// <summary>The last ledger row that moved it.</summary>
    [Field] public long LastSeq;

    /// <summary>Units held, 1..<see cref="ItemLedger.StackMax"/>. A stack emptied is destroyed.</summary>
    [Field] public int Count;

    /// <summary>How many ledger rows moved it.</summary>
    [Field] public int Rows;

    /// <summary>The template's id (<see cref="ItemCatalog"/>); 0 = none.</summary>
    [Field] public ushort Template;

    /// <summary>1-5; 0 for an item without a Mk.</summary>
    [Field] public byte Mk;

    /// <summary>The grid slot, 1..<see cref="ItemLedger.Slots"/>; 0 = not in the grid (equipped, later).</summary>
    [Field] public byte Slot;
}

/// <summary>
/// One movement of items, for ever (inventory-loot.md § 2): both sides, the stacks touched, their counts after, the template, Mk and count. Appended by
/// <c>ItemTransfer</c> in the transaction that moves the stacks; never updated, never destroyed. A drop that lands in two stacks is two rows.
/// </summary>
[Component("Swg.ItemLedgerEntry", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ItemLedgerEntry
{
    /// <summary>Position in the item ledger, from 1, without a gap.</summary>
    [Field, Index] public long Seq;

    /// <summary>The world tick it was applied on.</summary>
    [Field] public long WorldTick;

    /// <summary>The giving character's raw id, or <see cref="ItemLedger.World"/>.</summary>
    [Field, Index(AllowMultiple = true)] public long From;

    /// <summary>The receiving character's raw id, or <see cref="ItemLedger.World"/>.</summary>
    [Field, Index(AllowMultiple = true)] public long To;

    /// <summary>The giving stack's raw id; 0 on World's side.</summary>
    [Field] public long FromStack;

    /// <summary>The receiving stack's raw id; 0 on World's side.</summary>
    [Field] public long ToStack;

    /// <summary>What it was for: the victim, the lair or the mission.</summary>
    [Field] public long Ref;

    /// <summary>The giving stack's count after; 0 on World's side or for a stack emptied.</summary>
    [Field] public int FromAfter;

    /// <summary>The receiving stack's count after; 0 on World's side.</summary>
    [Field] public int ToAfter;

    /// <summary>Units moved; always above zero.</summary>
    [Field] public int Count;

    /// <summary>The template's id.</summary>
    [Field] public ushort Template;

    /// <summary>The Mk, 0 for an item without one.</summary>
    [Field] public byte Mk;

    /// <summary>See <see cref="ItemMoveKind"/>.</summary>
    [Field] public byte Kind;
}

/// <summary>
/// An item with stats (crafting-build.md § 2.1): the <see cref="ItemStack"/> of an <see cref="ItemInstanceArch"/>, which derives from
/// <see cref="ItemStackArch"/>, so its owner, template, Mk, slot and count (1) are the stack's and move through the one item code path. These are what make
/// it itself: who made it, its stat lines (<see cref="Equipment"/> says what they mean for its category), its condition and its life factor.
/// </summary>
/// <remarks>0 means "no data" in every field: <see cref="Crafter"/> 0 is standard issue or loot, never a character.</remarks>
[Component("Swg.ItemInstance", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ItemInstance
{
    /// <summary>The character that crafted it, raw; 0 when nobody did.</summary>
    [Field] public long Crafter;

    /// <summary>The crafter's craft count at the craft, the seed of its rolls (§ 1 rule 4); 0 when not crafted.</summary>
    [Field] public int Serial;

    /// <summary>The template's first stat line: a weapon's damage per hit, an armour piece's kinetic protection (‰).</summary>
    [Field] public int Stat0;

    /// <summary>The second: a weapon's delay (ms), an armour piece's energy protection (‰).</summary>
    [Field] public int Stat1;

    /// <summary>The third: a weapon's range (dm).</summary>
    [Field] public int Stat2;

    /// <summary>The fourth, unused by C1's templates.</summary>
    [Field] public int Stat3;

    /// <summary>What is left of its life (crafting-catalog.md § 9.1): a weapon's combat-milliseconds, an armour piece's damage points.</summary>
    [Field] public int Condition;

    /// <summary>Its full condition, × 0.75 per repair (§ 9.2).</summary>
    [Field] public int MaxCondition;

    /// <summary>The life factor, 900..1 100 per mille, rolled at the craft (§ 9.1); 1 000 for standard issue.</summary>
    [Field] public short LifeMilli;

    /// <summary>Repairs used, 0..2 (§ 9.2).</summary>
    [Field] public byte Repairs;

    /// <summary><see cref="ItemFlags"/>.</summary>
    [Field] public byte Flags;
}

/// <summary>
/// A craft under way (crafting-catalog.md § 3.4, crafting-build.md C2): its inputs already consumed, its lines' ceilings and how far each got, the points
/// left — what a restart resumes. One per crafter at a time; destroyed when it finishes, in the commit that mints its result.
/// </summary>
/// <remarks>Fractions in millionths (<see cref="Crafting.Scale"/>), as <see cref="Crafting"/> keeps them: a resumed craft rolls the same numbers.</remarks>
[Component("Swg.CraftState", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CraftState
{
    /// <summary>The crafter, a character's raw id.</summary>
    [Field, Index] public long Crafter;

    /// <summary>The seed of its rolls with the crafter (§ 1 rule 4): the item ledger's last sequence number when it started, unique.</summary>
    [Field] public long Serial;

    /// <summary>The world tick it started on.</summary>
    [Field] public long StartedTick;

    /// <summary>Each line's ceiling, millionths.</summary>
    [Field] public int Q0;

    [Field] public int Q1;

    [Field] public int Q2;

    [Field] public int Q3;

    /// <summary>How far each line got, millionths.</summary>
    [Field] public int F0;

    [Field] public int F1;

    [Field] public int F2;

    [Field] public int F3;

    /// <summary>The schematic's id (<see cref="Schematics"/>).</summary>
    [Field] public ushort Schematic;

    /// <summary>What it makes: the schematic's output, or the composite piece when its segments are composite.</summary>
    [Field] public ushort Output;

    /// <summary>The result's Mk: its core's for a weapon or an armour piece, I for a crafted core, 0 otherwise.</summary>
    [Field] public byte Mk;

    /// <summary>Experimentation points left.</summary>
    [Field] public byte PointsLeft;

    /// <summary>The next roll's step: 1 for the first point, and so on (0 was the assembly).</summary>
    [Field] public byte NextStep;

    /// <summary>The add-on's Mk, 0 for none (§ 3.4 step 3).</summary>
    [Field] public byte AddonMk;
}

/// <summary>The bits of <see cref="ItemInstance.Flags"/>.</summary>
public static class ItemFlags
{
    /// <summary>Never used: it may be crated (crafting-catalog.md § 11.2).</summary>
    public const byte Pristine = 1;

    /// <summary>At 0 condition with a repair left: unusable, still owned (§ 9.2).</summary>
    public const byte Broken = 2;
}

/// <summary>
/// The item ledger's head, one entity: the last sequence number given, and the units World minted and destroyed. Written with every row, in its transaction.
/// </summary>
[Component("Swg.ItemLedgerHead", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ItemLedgerHead
{
    /// <summary>Always <see cref="ItemLedger.HeadKey"/>.</summary>
    [Field, Index] public int Key;

    /// <summary>The last sequence number given; 0 before the first row.</summary>
    [Field] public long LastSeq;

    /// <summary>Units World ever put into inventories.</summary>
    [Field] public long Minted;

    /// <summary>Units inventories ever gave back to World.</summary>
    [Field] public long Destroyed;
}
