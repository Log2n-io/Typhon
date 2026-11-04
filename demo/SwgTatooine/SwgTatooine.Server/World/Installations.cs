using System;

namespace SwgTatooine.Server;

/// <summary>What an installation is (crafting-build.md § 10): C5a's extractor; C5b's factory next.</summary>
public static class InstallationKind
{
    public const byte Extractor = 1;
}

/// <summary>
/// The rules of player installations (crafting-build.md § 10, O-14; crafting-catalog.md § 5.5, § 15.4): placed on a 64 m lot outside the city discs,
/// from a character's 10 lots; production computed lazily from the time it ran; upkeep a prepaid pool, converted to running time when it is paid.
/// </summary>
/// <remarks>
/// <para><b>An extractor makes Mk I units of the family chosen at placement</b> (Q4), at its rate a running hour, into its hopper; it stops when the hopper
/// is full or its upkeep has run out, and nothing is written while it runs: what it holds is <see cref="Held"/> of the time since it last settled.</para>
/// <para><b>The standard kit</b> is the vendor's (O-14): a fixed rate and hopper, sold as standard issue. [EST] — the catalogue prices no extractor.</para>
/// </remarks>
public static class Installations
{
    /// <summary>A lot's edge, metres (economy-loop.md § 4).</summary>
    public const float LotM = 64f;

    /// <summary>The lots a character holds for all its installations and houses (Loïc, 2026-11-03).</summary>
    public const int LotsPerCharacter = 10;

    /// <summary>The lots an extractor takes.</summary>
    public const int ExtractorLots = 1;

    /// <summary>How far from a city's edge an installation must stand, metres: its disc and a margin, so none is built in a street.</summary>
    public const float CityMarginM = 50f;

    /// <summary>How close a character stands to collect, pay or pick up, metres (economy-loop.md § 4).</summary>
    public const float ReachM = 30f;

    /// <summary>An extractor's upkeep, credits a running hour (crafting-catalog.md § 15.4).</summary>
    public const int ExtractorUpkeepPerHour = 100;

    /// <summary>The most a single payment may buy, hours: a mistyped amount is refused rather than banked for a year.</summary>
    public const int MaxPrepaidHours = 24 * 14;

    /// <summary>The vendor's standard extractor kit: units a running hour, its hopper, and its price (O-14, [EST]).</summary>
    public const int StandardRatePerHour = 360;

    public const int StandardHopper = 500;

    public const long StandardKitPrice = 3_000;

    /// <summary>What an extractor may draw: the bulk families (catalogue § 5.5) — the vendor's materials, which no beast drops early.</summary>
    public static bool Extracts(ushort family) => Equipment.IsStandardMaterial(family);

    /// <summary>The lot a point is in: its realm, then its column and row on the 64 m grid, offset so a negative coordinate keys like a positive one.</summary>
    public static long LotOf(ushort realm, float x, float z)
    {
        var gx = (long)MathF.Floor(x / LotM) + (1L << 19);
        var gz = (long)MathF.Floor(z / LotM) + (1L << 19);
        return ((long)realm << 40) | (gx << 20) | gz;
    }

    /// <summary>A lot's centre: where its installation stands.</summary>
    public static (float X, float Z) CentreOf(long lot)
    {
        var gx = ((lot >> 20) & 0xFFFFF) - (1L << 19);
        var gz = (lot & 0xFFFFF) - (1L << 19);
        return ((gx + 0.5f) * LotM, (gz + 0.5f) * LotM);
    }

    /// <summary>The stats of the vendor's kit, as an item: Stat0 its rate a running hour, Stat1 its hopper.</summary>
    public static ItemInstance StandardKit() => new() { Stat0 = StandardRatePerHour, Stat1 = StandardHopper, LifeMilli = 1000, Flags = ItemFlags.Pristine };

    /// <summary>
    /// The units an installation holds at <paramref name="now"/>: what it held when it last settled, and what it made since — at its rate, while it ran
    /// (until its upkeep ran out), up to its hopper. In thousandths of a unit, so a settle loses nothing to rounding.
    /// </summary>
    public static long HeldMilli(in InstallationCore c, long now, long ticksPerHour)
    {
        var end = Math.Min(now, c.PaidUntil);
        var ran = Math.Max(0, end - c.T0);
        var made = ran * c.RatePerHour * 1000L / Math.Max(1, ticksPerHour);
        return Math.Min((long)c.Hopper * 1000, c.BankedMilli + made);
    }

    /// <summary>Whole units held at <paramref name="now"/>.</summary>
    public static int Held(in InstallationCore c, long now, long ticksPerHour) => (int)(HeldMilli(in c, now, ticksPerHour) / 1000);

    /// <summary>The running time <paramref name="credits"/> of upkeep buy, ticks.</summary>
    public static long TicksBought(long credits, long ticksPerHour) => credits * ticksPerHour / ExtractorUpkeepPerHour;
}

/// <summary>An installation's state, as a client draws it.</summary>
public static class InstallationState
{
    public const byte Running = 1;
    public const byte Full = 2;
    public const byte Unpaid = 3;

    /// <summary>What it is doing at <paramref name="now"/>: full before unpaid, since a full one makes nothing whatever it has paid.</summary>
    public static byte Of(in InstallationCore c, long now, long ticksPerHour)
        => Installations.Held(in c, now, ticksPerHour) >= c.Hopper ? Full : now >= c.PaidUntil ? Unpaid : Running;
}
