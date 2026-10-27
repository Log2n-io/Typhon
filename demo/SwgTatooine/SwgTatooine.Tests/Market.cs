using System.Collections.Generic;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// What the checks read of the bazaar, and the commands they send: the listings, as stored, and the five market commands as a client sends them. Every read is
/// one quick transaction off the tick, which <c>EW-02</c> allows for this Versioned, non-spatial archetype — the way <c>/swg/market/mine</c> reads it.
/// </summary>
internal static class Market
{
    /// <summary>Every listing, as stored, by its raw id.</summary>
    public static Dictionary<long, Listing> Listings(TatooineSim sim)
    {
        var all = new Dictionary<long, Listing>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<Listing, long>(sim.Dbe.GetIndexRef<Listing, long>(l => l.Seller), long.MinValue, long.MaxValue);
        while (found.MoveNext())
        {
            all[found.CurrentEntityPK] = found.CurrentComponent;
        }

        return all;
    }

    /// <summary>Units of (template, Mk) a character holds, every stack summed.</summary>
    public static int Units(TatooineSim sim, EntityId character, ushort template, byte mk)
    {
        var n = 0;
        foreach (var (_, s) in Items.Stacks(sim, character))
        {
            if (s.Template == template && s.Mk == mk)
            {
                n += s.Count;
            }
        }

        return n;
    }

    public static void List(FakeLink link, ushort template, byte mk, int quantity, uint unitPrice)
        => link.SendCommand(nameof(MarketList), new RecordValues
        {
            ["template"] = FieldValue.Of(template),
            ["mk"] = FieldValue.Of(mk),
            ["quantity"] = FieldValue.Of(quantity),
            ["unitPrice"] = FieldValue.Of(unitPrice),
        });

    public static void Cancel(FakeLink link, long listing)
        => link.SendCommand(nameof(MarketCancel), new RecordValues { ["listing"] = FieldValue.OfUInt64(unchecked((ulong)listing)) });

    public static void Buy(FakeLink link, long listing, uint unitPrice)
        => link.SendCommand(nameof(MarketBuy), new RecordValues
        {
            ["listing"] = FieldValue.OfUInt64(unchecked((ulong)listing)),
            ["unitPrice"] = FieldValue.Of(unitPrice),
        });

    public static void Collect(FakeLink link, long listing)
        => link.SendCommand(nameof(MarketCollect), new RecordValues { ["listing"] = FieldValue.OfUInt64(unchecked((ulong)listing)) });

    public static void Vendor(FakeLink link, ushort template, byte mk, int quantity)
        => link.SendCommand(nameof(SellToVendor), new RecordValues
        {
            ["template"] = FieldValue.Of(template),
            ["mk"] = FieldValue.Of(mk),
            ["quantity"] = FieldValue.Of(quantity),
        });

    /// <summary>The three audits, clean or not, with their violations for the failure message.</summary>
    public static (bool Ok, string Why) Audits(TatooineSim sim)
    {
        var credits = EconomyEndpoints.Audit(sim.Dbe);
        var items = ItemEndpoints.Audit(sim.Dbe);
        var market = MarketEndpoints.Audit(sim.Dbe, sim.Bridge.MarketBoard);
        return (credits.Ok && items.Ok && market.Ok,
            $"credits: {string.Join("; ", credits.Violations)} | items: {string.Join("; ", items.Violations)} | market: {string.Join("; ", market.Violations)}");
    }
}
