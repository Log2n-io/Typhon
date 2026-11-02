using System;
using System.Diagnostics;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The real audits, unchanged, against ledgers far larger than the page cache. The dashboard keeps them off (<c>SWG_DASH_CONSISTENCY</c>) because of what
/// they do to a grown server; these say what that is. The rows are a clean ledger's shape, not a valid one: the audits' verdicts do not matter here, only
/// their walks.
/// </summary>
[Category("Manual")]
[Explicit("On-demand: bulk-loads millions of ledger rows. Never run by CI.")]
[TestFixture]
[NonParallelizable]
internal sealed class HugeAuditChecks : ServedChecks
{
    private const int Rows = 3_000_000;
    private const int SessionRows = 500_000;
    private const int Listings = 1_000_000;
    private const int Sellers = 100_000;
    private const int CacheMiB = 64;

    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.PageCacheMiB = CacheMiB;
        var sim = new TatooineSim(config);
        sim.Initialize();
        return sim;
    }

    private static void LoadCreditLedger(TatooineSim sim)
    {
        for (long first = 0; first < Rows; first += SessionRows)
        {
            using var s = sim.Dbe.BeginBulkLoad(new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) });
            for (var i = first; i < Math.Min(first + SessionRows, Rows); i++)
            {
                var row = new LedgerEntry { Seq = i + 1, WorldTick = i, From = 1000 + i % 2, To = 1001 - i % 2, Amount = 1, FromAfter = -1, ToAfter = 1 };
                s.Spawn<LedgerArch>(LedgerArch.Entry.Set(in row));
            }

            s.CompleteBulkLoad();
        }
    }

    private static void LoadItemLedger(TatooineSim sim)
    {
        for (long first = 0; first < Rows; first += SessionRows)
        {
            using var s = sim.Dbe.BeginBulkLoad(new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) });
            for (var i = first; i < Math.Min(first + SessionRows, Rows); i++)
            {
                var row = new ItemLedgerEntry { Seq = i + 1, WorldTick = i, From = 1000 + i % 2, To = 1001 - i % 2, FromStack = 2000 + i % 2,
                    ToStack = 2001 - i % 2, Count = 1 };
                s.Spawn<ItemLedgerArch>(ItemLedgerArch.Entry.Set(in row));
            }

            s.CompleteBulkLoad();
        }
    }

    /// <summary>Every listing names a seller, and the audit looks each one's wallet up by index: that loop of point reads is what the market audit adds to the
    /// other two.</summary>
    private static void LoadListings(TatooineSim sim)
    {
        using (var s = sim.Dbe.BeginBulkLoad(new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) }))
        {
            for (var i = 0; i < Sellers; i++)
            {
                var wallet = new Wallet { Owner = 1000 + i, Balance = 1 };
                s.Spawn<WalletArch>(WalletArch.Purse.Set(in wallet));
            }

            s.CompleteBulkLoad();
        }

        for (var first = 0; first < Listings; first += SessionRows)
        {
            using var s = sim.Dbe.BeginBulkLoad(new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) });
            for (var i = first; i < Math.Min(first + SessionRows, Listings); i++)
            {
                var row = new Listing { Seller = 1000 + i % Sellers, UnitPrice = 1, ListedTick = i, Quantity = 1, Rows = 1 };
                s.Spawn<ListingArch>(ListingArch.Entry.Set(in row));
            }

            s.CompleteBulkLoad();
        }
    }

    private static void Measure(TatooineSim sim, string name, Func<long> audit)
    {
        var sw = Stopwatch.StartNew();
        string outcome;
        long entries = 0;
        try
        {
            entries = audit();
            outcome = "ok";
        }
        catch (Exception e)
        {
            outcome = $"{e.GetType().Name}: {e.Message}";
        }

        TestContext.Progress.WriteLine($"{name}: {outcome}; {entries:N0} entries, {sw.Elapsed.TotalSeconds:F1} s");
        Assert.That(outcome, Is.EqualTo("ok"));
    }

    /// <summary>As the endpoint runs it: an index walk of the whole ledger.</summary>
    [Test]
    public void TheEconomyAudit_AsShipped()
    {
        using var sim = Start();
        LoadCreditLedger(sim);
        Measure(sim, "economy audit, as shipped", () => EconomyEndpoints.Audit(sim.Dbe).Entries);
    }

    /// <summary>As the endpoint runs it.</summary>
    [Test]
    public void TheItemAudit_AsShipped()
    {
        using var sim = Start();
        LoadItemLedger(sim);
        Measure(sim, "item audit, as shipped", () => ItemEndpoints.Audit(sim.Dbe).Entries);
    }

    /// <summary>As the endpoint runs it: both ledgers, every listing, and a wallet point read per listing in one read transaction.</summary>
    [Test]
    public void TheMarketAudit_AsShipped()
    {
        using var sim = Start();
        LoadCreditLedger(sim);
        LoadItemLedger(sim);
        LoadListings(sim);
        Measure(sim, "market audit, as shipped", () => MarketEndpoints.Audit(sim.Dbe, MarketSnapshot.Empty).Listings);
    }

    /// <summary>The same audit through a read-write transaction, which an engine walk must survive as well: this is what died of the slot-reference
    /// leak.</summary>
    [Test]
    public void TheItemAudit_ThroughAReadWriteTransaction()
    {
        using var sim = Start();
        LoadItemLedger(sim);
        Measure(sim, "item audit, read-write transaction", () =>
        {
            using var tx = sim.Dbe.CreateQuickTransaction();
            return ItemEndpoints.Audit(sim.Dbe, tx).Entries;
        });
    }
}
