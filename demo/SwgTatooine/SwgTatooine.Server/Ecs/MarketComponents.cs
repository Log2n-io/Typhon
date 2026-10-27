using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The bazaar (claude/design/SwgTatooine/design/bazaar.md)
//
// A LISTING is its own escrow stack (B1): listing moves the units out of the seller's inventory into the listing, as item-ledger rows naming the listing's
// raw id as owner and stack, and its Quantity is that stack's count. One state machine (B3): Listed → Sold / Returned → collected (destroyed).
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>A listing's state (bazaar.md B3). 0 is none, as everywhere (Loïc's rule).</summary>
public static class ListingState
{
    /// <summary>On the board, buyable; its units in escrow.</summary>
    public const byte Listed = 1;

    /// <summary>Bought: paid for, its units reserved for <see cref="Listing.Collector"/>, the buyer, at the listing's city.</summary>
    public const byte Sold = 2;

    /// <summary>Cancelled or expired: its units reserved for its seller at the listing's city.</summary>
    public const byte Returned = 3;

    /// <summary>The name a state is shown by, over HTTP and in the audit.</summary>
    public static string NameOf(byte state) => state switch
    {
        Listed => "listed",
        Sold => "sold",
        Returned => "returned",
        _ => "unknown",
    };
}

/// <summary>
/// A listing on the bazaar (bazaar.md § 2.1): one (template, Mk), 1-99 units, at one fixed unit price, in one city. Written by the <c>Market</c> system alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is its escrow stack.</b> The item rows that fill it (<c>list</c>) and empty it (<c>buy</c>, <c>return</c>) name its raw id as the owner and the
/// stack, so the item audit replays it like any stack, and escrow and listing cannot drift.
/// </para>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial</b> — money's reasons (<see cref="Wallet"/>): it rides the WAL, and <c>/swg/market/mine</c> and the
/// audit read it off the tick under <c>EW-02</c>. Ids are never reused: a destroyed listing's id stays named by its ledger rows for ever.
/// </para>
/// </remarks>
[Component("Swg.Listing", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Listing
{
    /// <summary>The selling character's raw id.</summary>
    [Field, Index(AllowMultiple = true)] public long Seller;

    /// <summary>0 while <see cref="ListingState.Listed"/>; the buyer once <see cref="ListingState.Sold"/>; the seller once <see cref="ListingState.Returned"/>.</summary>
    [Field, Index(AllowMultiple = true)] public long Collector;

    /// <summary>Credits per unit, 1..<see cref="TatooineData.MaxUnitPrice"/>.</summary>
    [Field] public long UnitPrice;

    /// <summary>The world tick it was listed on.</summary>
    [Field] public long ListedTick;

    /// <summary>The world tick it was sold, cancelled or expired on; 0 while listed.</summary>
    [Field] public long ClosedTick;

    /// <summary>The world tick it expires on, while listed.</summary>
    [Field] public long ExpireTick;

    /// <summary>The last item ledger row that moved its units.</summary>
    [Field] public long LastSeq;

    /// <summary>The list fee paid for it, credits (its <c>listfee</c> row's amount).</summary>
    [Field] public long Fee;

    /// <summary>Units in escrow, 1-99: the escrow stack's count.</summary>
    [Field] public int Quantity;

    /// <summary>How many item ledger rows moved its units.</summary>
    [Field] public int Rows;

    /// <summary>The city it was listed in: its group key, region + 1, as <see cref="MissionTerminalRef.Region"/>.</summary>
    [Field] public ushort City;

    /// <summary>The planet it was listed on.</summary>
    [Field] public ushort Realm;

    /// <summary>The template's id (<see cref="ItemCatalog"/>).</summary>
    [Field] public ushort Template;

    /// <summary>1-5; 0 for an item without a Mk.</summary>
    [Field] public byte Mk;

    /// <summary>See <see cref="ListingState"/>.</summary>
    [Field] public byte State;
}
