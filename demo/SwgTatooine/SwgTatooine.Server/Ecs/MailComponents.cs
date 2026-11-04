using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>What a mail is (mail.md § 2): one from a player, or one the game sends about an installation or a listing.</summary>
public static class MailKind
{
    /// <summary>From a player.</summary>
    public const byte Player = 1;

    /// <summary>An installation's hopper is full: it stopped extracting.</summary>
    public const byte HopperFull = 2;

    /// <summary>An installation's paid upkeep fell under an hour.</summary>
    public const byte UpkeepLow = 3;

    /// <summary>An installation stopped: its upkeep ran out.</summary>
    public const byte Stopped = 4;

    /// <summary>A listing sold.</summary>
    public const byte Sold = 5;

    /// <summary>A listing expired, its goods reserved for the seller.</summary>
    public const byte Expired = 6;

    /// <summary>The name a kind is shown by, over HTTP and in the stats.</summary>
    public static string NameOf(byte kind) => kind switch
    {
        Player => "player",
        HopperFull => "hopperfull",
        UpkeepLow => "upkeeplow",
        Stopped => "stopped",
        Sold => "sold",
        Expired => "expired",
        _ => "unknown",
    };
}

/// <summary>
/// A mail in a character's inbox (mail.md § 2): who it is for and from, when, what about, and its text.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="StorageMode.Versioned"/>, non-spatial, keyed on the owner</b> — an item stack's reasons: the inbox endpoint reads it off the tick under
/// <c>EW-02</c>, and a mail is what a player expects to find again.
/// </para>
/// <para>
/// <b>The text is a collection, out of the cluster.</b> A <c>String1024</c> field would make the entity 1 136 bytes, and a cluster holds eight at least on an
/// 8 000-byte page (<c>ArchetypeClusterInfo.SelectClusterSize</c>), so no archetype can carry one. A collection is the engine's variable-sized field: the
/// cluster keeps its four-byte handle, the bytes live in its buffer segment, and the list, which reads this component, never touches them. 102 bytes, under the
/// 112 a spawn carries inline (TYPHON013).
/// </para>
/// </remarks>
[Component("Swg.MailCore", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MailCore
{
    /// <summary>The recipient's character raw id.</summary>
    [Field, Index(AllowMultiple = true)] public long Owner;

    /// <summary>The sender's character raw id; 0 for the game.</summary>
    [Field] public long From;

    /// <summary>The world tick it was sent at.</summary>
    [Field] public long SentTick;

    /// <summary>What a system mail is about — the installation, the listing — as a raw id; 0 for a player's.</summary>
    [Field] public long Ref;

    /// <summary>See <see cref="MailKind"/>.</summary>
    [Field] public byte Kind;

    /// <summary><see cref="MailFlags"/>.</summary>
    [Field] public byte Flags;

    /// <summary>1 to <see cref="Mail.SubjectMaxBytes"/> UTF-8 bytes.</summary>
    [Field] public String64 Subject;

    /// <summary>0 to <see cref="Mail.TextMaxBytes"/> UTF-8 bytes, in order; read when the mail is opened.</summary>
    [Field] public ComponentCollection<byte> Text;
}

/// <summary>A mail's <see cref="MailCore.Flags"/>.</summary>
public static class MailFlags
{
    /// <summary>Opened by its owner.</summary>
    public const byte Read = 1;
}
