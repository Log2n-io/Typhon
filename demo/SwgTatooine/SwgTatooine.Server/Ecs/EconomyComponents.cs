using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>
/// What a ledger row records (economy-foundation.md § 2). Money enters from and leaves to <see cref="EconomyLedger.World"/>; everything else moves between
/// two wallets.
/// </summary>
public static class TransferKind
{
    /// <summary>A character's starting credits, from World, once, when its wallet is opened.</summary>
    public const byte Grant = 1;

    /// <summary>A completed mission's advertised reward, from World. <see cref="LedgerEntry.Ref"/> is the held mission's raw id.</summary>
    public const byte MissionReward = 2;

    /// <summary>A player's tip to another player (<c>/tip</c>, <see cref="GiveCredits"/>).</summary>
    public const byte Tip = 3;

    /// <summary>The name a kind is shown by, over HTTP and in the audit.</summary>
    public static string NameOf(byte kind) => kind switch
    {
        Grant => "grant",
        MissionReward => "mission",
        Tip => "tip",
        _ => "unknown",
    };
}

/// <summary>
/// A character's wallet: one per character, keyed by its raw id, written by the <c>Ledger</c> system alone (economy-foundation.md § 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not on the body and not on <see cref="Character"/></b>. The body is spawned and destroyed per session; the character is written at login and on the
/// position cadence, and a balance sharing a component with those writes is a lost update waiting for the right interleaving. A component of its own, with
/// one writer, has neither problem.
/// </para>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial</b>, like <see cref="HeldMission"/> and for its reasons: it is money, so it rides the WAL, and it can
/// be read off the tick (<c>GET /swg/wallet</c>, the audit) under <c>EW-02</c>.
/// </para>
/// </remarks>
[Component("Swg.Wallet", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Wallet
{
    /// <summary>The character's raw id. Unique: one wallet per character, and the second open of one is a bug the index refuses.</summary>
    [Field, Index] public long Owner;

    /// <summary>Credits held. Never below zero: a transfer that would take it there is refused before it is written.</summary>
    [Field] public long Balance;

    /// <summary>The sequence number of the last ledger row that moved it — what the audit checks it against.</summary>
    [Field] public long LastSeq;

    /// <summary>How many ledger rows moved it.</summary>
    [Field] public int Transfers;
}

/// <summary>
/// One movement of credits, for ever (economy-foundation.md § 2): both sides, the amount, and both balances after it. Appended by the <c>Ledger</c> system
/// in the transaction that moves the balances; never updated, never destroyed.
/// </summary>
/// <remarks>
/// <b>Indexed three ways</b>: by <see cref="Seq"/>, unique, which is the order the audit replays in; and by each side, so a wallet's history is two range
/// scans rather than a walk of every row ever written. Two indexes on one component combine; the third is what makes every append real index work, which is
/// the load Loïc asked this to put on the database.
/// </remarks>
[Component("Swg.LedgerEntry", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LedgerEntry
{
    /// <summary>Position in the ledger, from 1, without a gap.</summary>
    [Field, Index] public long Seq;

    /// <summary>The world tick it was applied on (<c>TickContext.WorldTick</c>, which keeps counting across reopens).</summary>
    [Field] public long WorldTick;

    /// <summary>The paying wallet's owner, or <see cref="EconomyLedger.World"/>.</summary>
    [Field, Index(AllowMultiple = true)] public long From;

    /// <summary>The receiving wallet's owner, or <see cref="EconomyLedger.World"/>.</summary>
    [Field, Index(AllowMultiple = true)] public long To;

    /// <summary>Credits moved; always above zero.</summary>
    [Field] public long Amount;

    /// <summary>The paying side's balance after this row — World's included, which runs negative by everything it ever paid out.</summary>
    [Field] public long FromAfter;

    /// <summary>The receiving side's balance after this row.</summary>
    [Field] public long ToAfter;

    /// <summary>What it was for: the held mission of a reward; 0 when nothing else names it.</summary>
    [Field] public long Ref;

    /// <summary>See <see cref="TransferKind"/>.</summary>
    [Field] public byte Kind;
}

/// <summary>
/// The ledger's head, one entity: the last sequence number given, and World's balance (economy-foundation.md § 2). Written with every row, in its
/// transaction, so a reopened world resumes the sequence where it stopped and the audit can check World's side.
/// </summary>
[Component("Swg.LedgerHead", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LedgerHead
{
    /// <summary>Always <see cref="EconomyLedger.HeadKey"/>: what finds the one head through its index.</summary>
    [Field, Index] public int Key;

    /// <summary>The last sequence number given; 0 before the first row.</summary>
    [Field] public long LastSeq;

    /// <summary>World's balance: minus every credit that ever entered the economy, plus every one that left it.</summary>
    [Field] public long World;
}
