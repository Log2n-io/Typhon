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

    /// <summary>A cancelled or expired listing collected: the listing → its seller. <see cref="ItemLedgerEntry.Ref"/> is the listing's raw id.</summary>
    public const byte Return = 16;

    /// <summary>The name a kind is shown by, over HTTP and in the audit.</summary>
    public static string NameOf(byte kind) => kind switch
    {
        Kill => "kill",
        Lair => "lair",
        Mission => "mission",
        List => "list",
        Buy => "buy",
        Vendor => "vendor",
        Return => "return",
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
