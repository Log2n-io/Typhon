using System.Collections.Generic;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>An installation as <c>GET /swg/installations</c> lists it: what the bot decides by.</summary>
public readonly record struct BotInstallation(ulong Id, float X, float Z, int Held, int Hopper, int PaidSeconds);

/// <summary>
/// What an installation owner does (crafting-build.md § 10, O-7: 10 % of the trading bots): buys the vendor's extractor kit, places it on a lot just out of
/// its city, keeps its upkeep paid a few hours ahead, and collects its hopper before it fills — the parity rule, as a person does from the window.
/// </summary>
/// <remarks>The bot's copies of the server's rules (<c>Installations</c>): a refusal is the authority.</remarks>
public static class InstallPlan
{
    /// <summary>The extractor kit's template, and its price at the vendor.</summary>
    public const ushort Kit = 70;

    public const long KitPrice = 3_000;

    /// <summary>An extractor's upkeep, credits a running hour.</summary>
    public const int UpkeepPerHour = 100;

    /// <summary>How many hours of upkeep it keeps ahead, and below how many it pays again.</summary>
    public const int PayHours = 4;

    public const int PayBelowHours = 2;

    /// <summary>A hopper this full is collected, thousandths.</summary>
    public const int CollectAtPerMille = 400;

    /// <summary>How close counts as at an installation, metres: the server's reach, with a margin.</summary>
    public const double ReachM = 25;

    /// <summary>What it keeps in its wallet past a payment or a kit: a few repairs' and fees' worth.</summary>
    public const long Reserve = 1_500;

    /// <summary>The families an owner draws, by its index: the bulk materials crafts use most first.</summary>
    private static readonly ushort[] Families = [3, 1, 4, 5, 6, 7, 2];

    /// <summary>What an owner draws, by its index.</summary>
    public static ushort FamilyFor(int index) => Families[index % Families.Length];

    /// <summary>The installations an answer lists; empty for anything else.</summary>
    public static List<BotInstallation> Parse(string json)
    {
        var list = new List<BotInstallation>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("installations", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var i in items.EnumerateArray())
        {
            list.Add(new BotInstallation(
                MarketJson.Id(i.GetProperty("id")),
                i.GetProperty("x").GetSingle(),
                i.GetProperty("z").GetSingle(),
                i.GetProperty("held").GetInt32(),
                i.GetProperty("hopper").GetInt32(),
                (int)System.Math.Min(int.MaxValue, i.GetProperty("paidSeconds").GetInt64())));
        }

        return list;
    }

    /// <summary>Whether an installation needs a visit: its hopper filling, or its upkeep running low.</summary>
    public static bool Due(in BotInstallation i)
        => (long)i.Held * 1000 >= (long)i.Hopper * CollectAtPerMille || i.PaidSeconds < PayBelowHours * 3600;

    /// <summary>The first installation due, or null.</summary>
    public static BotInstallation? NextDue(IReadOnlyList<BotInstallation> installations)
    {
        foreach (var i in installations)
        {
            if (Due(in i))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The commands for an installation it stands at: its hopper collected when it holds anything, its upkeep paid up to <see cref="PayHours"/> when it is
    /// below <see cref="PayBelowHours"/> and the wallet keeps <see cref="Reserve"/> — the credits it pays, 0 for none.
    /// </summary>
    public static (bool Collect, uint Pay) Tend(in BotInstallation i, long wallet)
    {
        var pay = 0L;
        if (i.PaidSeconds < PayBelowHours * 3600)
        {
            var hours = PayHours - (i.PaidSeconds / 3600);
            pay = System.Math.Min((long)hours * UpkeepPerHour, wallet - Reserve);
            pay = pay < UpkeepPerHour ? 0 : pay - (pay % UpkeepPerHour);
        }

        return (i.Held > 0, (uint)pay);
    }

    /// <summary>Whether it buys a kit at the vendor: an owner with no installation and no kit, the wallet keeping its reserve past the price.</summary>
    public static bool BuysKit(bool owner, int installations, bool holdsKit, long wallet)
        => owner && installations == 0 && !holdsKit && wallet >= KitPrice + Reserve;
}
