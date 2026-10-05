using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>A trader: a market desk (numbers 1..Desks) or a player (after them). <see cref="ItemsOwned"/> is maintained by every transfer.</summary>
[Component("Typhon.Test.Market.Trader", 1)]
public struct MkTrader
{
    public long TraderNo;
    public long ItemsOwned;
}

[Component("Typhon.Test.Market.Wallet", 1)]
public struct MkWallet
{
    public long Credits;
}

/// <summary>
/// An item. <see cref="OwnerNo"/> 0 is the market (the item then sits at desk <c>1 + ItemNo % Desks</c>). <see cref="LastTradeSeq"/> is the audit
/// sequence of its last transfer, or <c>-(ItemNo + 1)</c> before the first: unique, so a unique index churns on every trade.
/// </summary>
[Component("Typhon.Test.Market.Item", 1)]
public struct MkItem
{
    public long ItemNo;
    public long OwnerNo;
    public long Price;
    public long TradeCount;
    [Index] public long LastTradeSeq;
}

/// <summary>
/// 512 bytes of content derived from the item's number and never written after the build: what makes the database large, and what every read
/// checks — a page served from the wrong slot, a torn write or a stale read shows up as lore that does not match its item.
/// </summary>
[Component("Typhon.Test.Market.Lore", 1, StorageMode = StorageMode.SingleVersion)]
public struct MkLore
{
    public const int Words = 64;

    public long W00, W01, W02, W03, W04, W05, W06, W07;
    public long W08, W09, W10, W11, W12, W13, W14, W15;
    public long W16, W17, W18, W19, W20, W21, W22, W23;
    public long W24, W25, W26, W27, W28, W29, W30, W31;
    public long W32, W33, W34, W35, W36, W37, W38, W39;
    public long W40, W41, W42, W43, W44, W45, W46, W47;
    public long W48, W49, W50, W51, W52, W53, W54, W55;
    public long W56, W57, W58, W59, W60, W61, W62, W63;
}

/// <summary>One committed transfer, appended in the same transaction as the transfer itself.</summary>
[Component("Typhon.Test.Market.Audit", 1)]
public struct MkAudit
{
    public long Seq;
    public long RunNo;
    public long Kind;
    public long ItemNo;
    public long From;
    public long To;
    public long Payer;
    public long Payee;
    public long Amount;
    public long NewPrice;
}

[Archetype]
class MkTraderArch : Archetype<MkTraderArch>
{
    public static readonly Comp<MkTrader> Trader = Register<MkTrader>();
    public static readonly Comp<MkWallet> Wallet = Register<MkWallet>();
}

[Archetype]
class MkItemArch : Archetype<MkItemArch>
{
    public static readonly Comp<MkItem> Item = Register<MkItem>();
    public static readonly Comp<MkLore> Lore = Register<MkLore>();
}

[Archetype]
class MkAuditArch : Archetype<MkAuditArch>
{
    public static readonly Comp<MkAudit> Audit = Register<MkAudit>();
}

#endregion

/// <summary>
/// An on-demand hardening run: a market economy over a database much larger than its page cache, checked for exact coherence after a concurrent
/// trading storm and again after a reopen. Never run by CI.
/// </summary>
/// <remarks>
/// <para><b>The world.</b> Market desks and players hold credits; items are listed at the market or owned by a player, and carry 512 bytes of lore
/// derived from their number. Every transfer — a player buying from the market, selling back to it, or trading with another player — moves credits
/// and one item, updates both sides' item counts, re-keys a unique index and appends an audit entry, all in one transaction.</para>
/// <para><b>Phases.</b> (1) <i>Build</i>, once: bulk-load the world, close. Reused by later runs while its manifest matches the configuration.
/// (2) <i>Stress</i>: open with the stress cache (default 8 GiB), snapshot and check the state, run the storm, check again. (3) <i>Reopen</i>:
/// close, reopen with a full checksum sweep, check again.</para>
/// <para><b>What is checked, exactly.</b> Items and credits are conserved. Every trader's <c>ItemsOwned</c> equals the items that name it.
/// Replaying this run's audit entries, in sequence order, over the snapshot taken before the storm reproduces every item's owner, price, trade count
/// and last trade, and every trader's credits and item count. The unique index finds exactly the items it should. Lore read during the storm, and
/// a sample after it, matches its item. The storage integrity check is clean. After reopen, the state equals the one before the close.</para>
/// <para><b>Coordination.</b> Concurrent writes to one entity resolve last-writer-wins in Typhon, and a commit cannot be refused by a conflict
/// handler. So, like a game server, the storm serialises per item and per trader with ordered application locks: the engine is asked for isolation
/// and durability, never to arbitrate. Any drift is then an engine fault.</para>
/// <para><b>Size.</b> An item takes roughly 0.6–0.7 KiB on disk with its lore and index entry; about 75 M items make a 50 GiB database. The test prints
/// the measured size after the build. Memory: about 40 bytes per item for ids and state arrays (3 GiB at 75 M), plus the page caches.</para>
/// <para><b>Configuration</b> (environment variables, all optional):
/// <c>TYPHON_MKT_DIR</c> (database directory; default under the temp folder), <c>TYPHON_MKT_ITEMS</c> (2 000 000), <c>TYPHON_MKT_PLAYERS</c>
/// (100 000), <c>TYPHON_MKT_DESKS</c> (64), <c>TYPHON_MKT_PLAYER_CREDITS</c> (10 000), <c>TYPHON_MKT_DESK_CREDITS</c> (10 000 000),
/// <c>TYPHON_MKT_BUILD_CACHE_MIB</c> (4096), <c>TYPHON_MKT_BUILD_SESSION_ITEMS</c> (2 000 000 per bulk-load session),
/// <c>TYPHON_MKT_CACHE_MIB</c> (8192), <c>TYPHON_MKT_THREADS</c> (half the cores, at least 2), <c>TYPHON_MKT_OPERATIONS</c> (1 000 000),
/// <c>TYPHON_MKT_LORE_SAMPLE_PERMILLE</c> (lore checked after the storm, per thousand items; 50), <c>TYPHON_MKT_REOPEN_VERIFY</c>
/// (0 None … 3 Standard; 3), <c>TYPHON_MKT_REBUILD</c> (1 forces a new build), <c>TYPHON_MKT_SEED</c> (945), <c>TYPHON_MKT_STALL_SECONDS</c> (120: no
/// progress for that long fails the run).</para>
/// <para><b>Run it:</b> <c>dotnet test test/Typhon.Engine.Tests -c Release --filter "FullyQualifiedName~MarketHardeningTests"</c>, with the variables
/// set. A 50 GiB run, for instance: <c>TYPHON_MKT_ITEMS=75000000</c>, <c>TYPHON_MKT_CACHE_MIB=8192</c>, <c>TYPHON_MKT_OPERATIONS=20000000</c>.</para>
/// <para><b>Reuse.</b> Each run continues from the previous one's state and records its totals in <c>manifest.json</c>. A run that fails part-way
/// leaves the database ahead of its manifest, and the next run says so before its storm: rebuild with <c>TYPHON_MKT_REBUILD=1</c>.</para>
/// </remarks>
[TestFixture]
[Explicit("On-demand hardening: builds a database of up to tens of GiB. Never run by CI.")]
[Category("Manual")]
[NonParallelizable]
public class MarketHardeningTests
{
    private const long KindBuy = 1, KindSell = 2, KindTrade = 3;
    private const int ReadBatch = 2048;

    #region Configuration and manifest

    private sealed class Config
    {
        public string Directory;
        public int Items, Players, Desks;
        public long PlayerCredits, DeskCredits;
        public long BuildCacheMiB, CacheMiB;
        public int BuildSessionItems, Threads;
        public long Operations;
        public int LoreSamplePermille;
        public OpenVerification ReopenVerify;
        public bool Rebuild;
        public int Seed;
        public int StallSeconds;

        public int TraderCount => Desks + Players;   // trader numbers 1..TraderCount

        public static Config FromEnvironment() => new()
        {
            Directory = Environment.GetEnvironmentVariable("TYPHON_MKT_DIR") is { Length: > 0 } d
                ? d
                : Path.Combine(Path.GetTempPath(), "Typhon.MarketHardening"),
            Items = (int)Env("ITEMS", 2_000_000),
            Players = (int)Env("PLAYERS", 100_000),
            Desks = (int)Env("DESKS", 64),
            PlayerCredits = Env("PLAYER_CREDITS", 10_000),
            DeskCredits = Env("DESK_CREDITS", 10_000_000),
            BuildCacheMiB = Env("BUILD_CACHE_MIB", 4096),
            BuildSessionItems = (int)Env("BUILD_SESSION_ITEMS", 2_000_000),
            CacheMiB = Env("CACHE_MIB", 8192),
            Threads = (int)Env("THREADS", Math.Max(2, Environment.ProcessorCount / 2)),
            Operations = Env("OPERATIONS", 1_000_000),
            LoreSamplePermille = (int)Env("LORE_SAMPLE_PERMILLE", 50),
            ReopenVerify = (OpenVerification)Env("REOPEN_VERIFY", (long)OpenVerification.Standard),
            Rebuild = Env("REBUILD", 0) != 0,
            Seed = (int)Env("SEED", 945),
            StallSeconds = (int)Env("STALL_SECONDS", 120),
        };

        private static long Env(string name, long fallback) =>
            long.TryParse(Environment.GetEnvironmentVariable("TYPHON_MKT_" + name), out var v) ? v : fallback;

        public override string ToString() =>
            $"items={Items:N0} players={Players:N0} desks={Desks} cache={CacheMiB} MiB build-cache={BuildCacheMiB} MiB threads={Threads} " +
            $"operations={Operations:N0} seed={Seed} dir={Directory}";
    }

    /// <summary>What a build produced and every run since has added: the totals each run checks against.</summary>
    private sealed class Manifest
    {
        public int Items { get; set; }
        public int Players { get; set; }
        public int Desks { get; set; }
        public int Seed { get; set; }
        public long TotalCredits { get; set; }
        public long AuditEntries { get; set; }
        public long LastSeq { get; set; }
        public int Runs { get; set; }

        public bool Matches(Config c) => Items == c.Items && Players == c.Players && Desks == c.Desks && Seed == c.Seed;
    }

    private static string DatabaseFile(Config c) => Path.Combine(c.Directory, "market.typhon");
    private static string ManifestFile(Config c) => Path.Combine(c.Directory, "manifest.json");
    private static string TraderIdsFile(Config c) => Path.Combine(c.Directory, "traders.ids");
    private static string ItemIdsFile(Config c) => Path.Combine(c.Directory, "items.ids");

    #endregion

    #region State

    /// <summary>Everything the checks compare: per trader (index = trader number) and per item (index = item number).</summary>
    private sealed class State
    {
        public readonly long[] Credits, ItemsOwned;
        public readonly int[] Owner, TradeCount;
        public readonly long[] Price, LastTradeSeq;

        public State(Config c)
        {
            Credits = new long[c.TraderCount + 1];
            ItemsOwned = new long[c.TraderCount + 1];
            Owner = new int[c.Items];
            TradeCount = new int[c.Items];
            Price = new long[c.Items];
            LastTradeSeq = new long[c.Items];
        }
    }

    private static int DeskOf(Config c, long itemNo) => 1 + (int)(itemNo % c.Desks);

    /// <summary>The trader whose item count an owner value moves: the item's desk while it is at the market.</summary>
    private static int CountSlot(Config c, long owner, long itemNo) => owner == 0 ? DeskOf(c, itemNo) : (int)owner;

    private static long PriceOf(long itemNo) => 10 + (long)(Mix((ulong)itemNo ^ 0x5EEDUL) % 991);

    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static MkLore LoreOf(long itemNo)
    {
        var lore = default(MkLore);
        var words = MemoryMarshal.CreateSpan(ref Unsafe.As<MkLore, long>(ref lore), MkLore.Words);
        for (var k = 0; k < MkLore.Words; k++)
        {
            words[k] = (long)Mix(((ulong)itemNo << 6) | (uint)k);
        }

        return lore;
    }

    private static bool LoreMatches(long itemNo, in MkLore lore)
    {
        var words = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<MkLore, long>(ref Unsafe.AsRef(in lore)), MkLore.Words);
        for (var k = 0; k < MkLore.Words; k++)
        {
            if (words[k] != (long)Mix(((ulong)itemNo << 6) | (uint)k))
            {
                return false;
            }
        }

        return true;
    }

    #endregion

    private Config _c;
    private ConcurrentQueue<string> _errors;

    private void Fail(string message)
    {
        if (_errors.Count < 100)
        {
            _errors.Enqueue(message);
        }
    }

    private static void Log(string message) => TestContext.Progress.WriteLine($"[market {DateTime.Now:HH:mm:ss}] {message}");

    private DatabaseEngine Open(long cacheMiB, OpenVerification verify) =>
        DatabaseEngine.Open(DatabaseFile(_c), o => o
            .Register<MkTrader>()
            .Register<MkWallet>()
            .Register<MkItem>()
            .Register<MkLore>()
            .Register<MkAudit>()
            .PageCacheSize((ulong)cacheMiB << 20)
            .ConfigureStorage(s => s.VerifyOnOpen = verify));

    [Test]
    public void AMarketLargerThanItsCache_StaysExactlyCoherent_ThroughAStormAndAReopen()
    {
        _c = Config.FromEnvironment();
        _errors = new ConcurrentQueue<string>();
        Log(_c.ToString());
        Directory.CreateDirectory(_c.Directory);

        var manifest = LoadOrBuild();
        var traderIds = ReadIds(TraderIdsFile(_c), _c.TraderCount + 1);
        var itemIds = ReadIds(ItemIdsFile(_c), _c.Items);

        // ── Stress ──
        State after;
        long runAudits;
        using (var dbe = Open(_c.CacheMiB, OpenVerification.Spine))
        {
            var runNo = manifest.Runs + 1;
            var sw = Stopwatch.StartNew();
            var state = ReadState(dbe, traderIds, itemIds, lorePermille: 0);
            Log($"snapshot read in {sw.Elapsed.TotalSeconds:F1} s");
            CheckConservation(state, manifest, "before the storm");
            CheckAuditCount(dbe, manifest.AuditEntries, "before the storm");
            AssertNoErrors("before the storm");

            var firstSeq = manifest.LastSeq + 1;
            var auditIds = Storm(dbe, traderIds, itemIds, runNo, firstSeq, out var lastSeq);
            AssertNoErrors("during the storm");
            runAudits = auditIds.Count;

            sw.Restart();
            after = ReadState(dbe, traderIds, itemIds, _c.LoreSamplePermille);
            Log($"state read in {sw.Elapsed.TotalSeconds:F1} s");

            // `state` becomes the expected state: the snapshot with this run's audit replayed over it.
            var traded = Replay(dbe, state, auditIds);
            CompareStates(state, after, "replayed audit vs database");
            CheckConservation(after, manifest, "after the storm");
            CheckAuditCount(dbe, manifest.AuditEntries + runAudits, "after the storm");
            CheckIndex(dbe, after, itemIds, firstSeq, traded);
            CheckIntegrity(dbe, "after the storm");
            ReportCache(dbe);
            AssertNoErrors("after the storm");

            manifest.AuditEntries += runAudits;
            manifest.LastSeq = lastSeq;
            manifest.Runs = runNo;
            File.WriteAllText(ManifestFile(_c), JsonSerializer.Serialize(manifest));
            Log("closing");
        }

        // ── Reopen ──
        var reopen = Stopwatch.StartNew();
        using (var dbe = Open(_c.CacheMiB, _c.ReopenVerify))
        {
            Log($"reopened with {_c.ReopenVerify} verification in {reopen.Elapsed.TotalSeconds:F1} s");
            var reread = ReadState(dbe, traderIds, itemIds, _c.LoreSamplePermille);
            CompareStates(after, reread, "after reopen vs before close");
            CheckAuditCount(dbe, manifest.AuditEntries, "after reopen");
            CheckIntegrity(dbe, "after reopen");
            AssertNoErrors("after reopen");
        }

        Log($"run {manifest.Runs} coherent: {runAudits:N0} transfers, {manifest.AuditEntries:N0} audit entries in total");
    }

    private void AssertNoErrors(string phase) =>
        Assert.That(_errors, Is.Empty, $"{phase}: {_errors.Count} problem(s)\n  " + string.Join("\n  ", _errors));

    #region Build

    private Manifest LoadOrBuild()
    {
        if (!_c.Rebuild && File.Exists(ManifestFile(_c)) && Directory.Exists(DatabaseFile(_c)))
        {
            var existing = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestFile(_c)));
            if (existing != null && existing.Matches(_c))
            {
                Log($"reusing the database built for this configuration ({existing.Runs} run(s) so far)");
                return existing;
            }
        }

        if (Directory.Exists(DatabaseFile(_c)))
        {
            Directory.Delete(DatabaseFile(_c), recursive: true);
        }

        Log("building");
        var sw = Stopwatch.StartNew();
        var traderIds = new ulong[_c.TraderCount + 1];
        var itemIds = new ulong[_c.Items];
        long totalCredits = 0;
        using (var dbe = Open(_c.BuildCacheMiB, OpenVerification.Spine))
        {
            var options = new BulkLoadOptions { CheckpointTimeout = TimeSpan.FromMinutes(30) };

            using (var session = dbe.BeginBulkLoad(options))
            {
                for (var t = 1; t <= _c.TraderCount; t++)
                {
                    var isDesk = t <= _c.Desks;
                    var credits = isDesk ? _c.DeskCredits : _c.PlayerCredits;
                    var owned = isDesk ? _c.Items / _c.Desks + (t - 1 < _c.Items % _c.Desks ? 1 : 0) : 0;
                    totalCredits += credits;
                    traderIds[t] = session.Spawn<MkTraderArch>(
                        MkTraderArch.Trader.Set(new MkTrader { TraderNo = t, ItemsOwned = owned }),
                        MkTraderArch.Wallet.Set(new MkWallet { Credits = credits })).RawValue;
                }

                session.CompleteBulkLoad();
            }

            for (var first = 0; first < _c.Items; first += _c.BuildSessionItems)
            {
                var end = (int)Math.Min((long)first + _c.BuildSessionItems, _c.Items);
                using var session = dbe.BeginBulkLoad(options);
                for (var i = first; i < end; i++)
                {
                    // The lore is larger than a spawn value carries (112 bytes): spawn without it, then supply and enable it in one step.
                    var id = session.Spawn<MkItemArch>(
                        MkItemArch.Item.Set(new MkItem { ItemNo = i, OwnerNo = 0, Price = PriceOf(i), TradeCount = 0, LastTradeSeq = -(i + 1L) }));
                    session.OpenMut(id).Enable(MkItemArch.Lore, LoreOf(i));
                    itemIds[i] = id.RawValue;
                }

                session.CompleteBulkLoad();
                Log($"  {end:N0} / {_c.Items:N0} items ({sw.Elapsed.TotalSeconds:F0} s)");
            }

            Log($"built in {sw.Elapsed.TotalSeconds:F0} s: data file {dbe.MMF.FileSize / (1024.0 * 1024 * 1024):F2} GiB, " +
                $"{(double)dbe.MMF.FileSize / Math.Max(1, _c.Items):F0} bytes per item");
        }

        WriteIds(TraderIdsFile(_c), traderIds);
        WriteIds(ItemIdsFile(_c), itemIds);
        var manifest = new Manifest
        {
            Items = _c.Items, Players = _c.Players, Desks = _c.Desks, Seed = _c.Seed, TotalCredits = totalCredits, AuditEntries = 0, LastSeq = 0, Runs = 0,
        };
        File.WriteAllText(ManifestFile(_c), JsonSerializer.Serialize(manifest));
        return manifest;
    }

    private static void WriteIds(string path, ulong[] ids)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        f.Write(MemoryMarshal.AsBytes(ids.AsSpan()));
    }

    private static ulong[] ReadIds(string path, int count)
    {
        var ids = new ulong[count];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read);
        f.ReadExactly(MemoryMarshal.AsBytes(ids.AsSpan()));
        return ids;
    }

    #endregion

    #region Reading the state — in short transactions, so no reader pins more of the cache than a batch

    private State ReadState(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds, int lorePermille)
    {
        var s = new State(_c);
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            for (var t = 1; t <= _c.TraderCount; t++)
            {
                var e = tx.Open(EntityId.FromRawValue(traderIds[t]));
                var trader = e.Read(MkTraderArch.Trader);
                if (trader.TraderNo != t)
                {
                    Fail($"trader {t}: entity holds trader {trader.TraderNo}");
                }

                s.ItemsOwned[t] = trader.ItemsOwned;
                s.Credits[t] = e.Read(MkTraderArch.Wallet).Credits;
            }
        }

        var batches = (_c.Items + ReadBatch - 1) / ReadBatch;
        var loreChecked = 0L;
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = _c.Threads }, b =>
        {
            var checkedHere = 0;
            using var tx = dbe.CreateReadOnlyTransaction();
            var end = Math.Min(_c.Items, (b + 1) * ReadBatch);
            for (var i = b * ReadBatch; i < end; i++)
            {
                var e = tx.Open(EntityId.FromRawValue(itemIds[i]));
                var item = e.Read(MkItemArch.Item);
                if (item.ItemNo != i)
                {
                    Fail($"item {i}: entity holds item {item.ItemNo}");
                }

                s.Owner[i] = (int)item.OwnerNo;
                s.Price[i] = item.Price;
                s.TradeCount[i] = (int)item.TradeCount;
                s.LastTradeSeq[i] = item.LastTradeSeq;
                if (lorePermille > 0 && Mix((ulong)i ^ 0x10EEUL) % 1000 < (ulong)lorePermille)
                {
                    checkedHere++;
                    if (!LoreMatches(i, e.Read(MkItemArch.Lore)))
                    {
                        Fail($"item {i}: its lore does not match it");
                    }
                }
            }

            Interlocked.Add(ref loreChecked, checkedHere);
        });

        if (lorePermille > 0)
        {
            Log($"  lore checked on {loreChecked:N0} items");
        }

        return s;
    }

    #endregion

    #region The storm

    private List<ulong> Storm(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds, int runNo, long firstSeq, out long lastSeq)
    {
        var itemLocks = new object[1 << 16];
        var traderLocks = new object[1 << 12];
        for (var i = 0; i < itemLocks.Length; i++)
        {
            itemLocks[i] = new object();
        }

        for (var i = 0; i < traderLocks.Length; i++)
        {
            traderLocks[i] = new object();
        }

        var seq = firstSeq - 1;
        long started = 0, done = 0, skipped = 0, buys = 0, sells = 0, trades = 0;
        var stop = 0;
        var audits = new List<ulong>[_c.Threads];
        var threads = new Thread[_c.Threads];

        for (var w = 0; w < _c.Threads; w++)
        {
            var worker = w;
            audits[w] = new List<ulong>();
            threads[w] = new Thread(() =>
            {
                var rng = new Random(_c.Seed * 7919 + runNo * 104729 + worker);
                var mine = audits[worker];
                var stripes = new int[4];
                try
                {
                    while (Volatile.Read(ref stop) == 0 && Interlocked.Increment(ref started) <= _c.Operations)
                    {
                        var itemNo = rng.Next(_c.Items);
                        var itemId = EntityId.FromRawValue(itemIds[itemNo]);
                        lock (itemLocks[itemNo & (itemLocks.Length - 1)])
                        {
                            // Who owns it. Read after taking the item's lock: every earlier transfer of this item committed before releasing it.
                            long owner, price;
                            using (var r = dbe.CreateReadOnlyTransaction())
                            {
                                var it = r.Open(itemId).Read(MkItemArch.Item);
                                owner = it.OwnerNo;
                                price = it.Price;
                            }

                            // The plan: who pays whom, where the item goes.
                            var desk = DeskOf(_c, itemNo);
                            long kind, to, payer, payee, amount, newPrice = price;
                            if (owner == 0)
                            {
                                kind = KindBuy;
                                to = _c.Desks + 1 + rng.Next(_c.Players);
                                payer = to;
                                payee = desk;
                                amount = price;
                            }
                            else if (rng.Next(10) < 3)
                            {
                                kind = KindSell;
                                to = 0;
                                payer = desk;
                                payee = owner;
                                amount = price / 2;
                            }
                            else
                            {
                                kind = KindTrade;
                                do
                                {
                                    to = _c.Desks + 1 + rng.Next(_c.Players);
                                } while (to == owner);

                                payer = to;
                                payee = owner;
                                newPrice = Math.Max(1, price * (80 + rng.Next(41)) / 100);
                                amount = newPrice;
                            }

                            var fromSlot = CountSlot(_c, owner, itemNo);
                            var toSlot = CountSlot(_c, to, itemNo);

                            // The traders involved, locked in stripe order (an item lock is always taken first and alone: no cycle).
                            var n = 0;
                            foreach (var t in new[] { payer, payee, fromSlot, toSlot })
                            {
                                var stripe = (int)(t & (traderLocks.Length - 1));
                                if (Array.IndexOf(stripes, stripe, 0, n) < 0)
                                {
                                    stripes[n++] = stripe;
                                }
                            }

                            Array.Sort(stripes, 0, n);
                            for (var k = 0; k < n; k++)
                            {
                                Monitor.Enter(traderLocks[stripes[k]]);
                            }

                            try
                            {
                                using var tx = dbe.CreateQuickTransaction();

                                // The item: still the owner we planned for, and its lore intact. Read through a read handle; reopened to write it.
                                var itemRef = tx.Open(itemId);
                                var item = itemRef.Read(MkItemArch.Item);
                                if (item.OwnerNo != owner || item.ItemNo != itemNo)
                                {
                                    Fail($"item {itemNo}: owner {item.OwnerNo} / number {item.ItemNo} changed under its lock (expected owner {owner})");
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                if (!LoreMatches(itemNo, itemRef.Read(MkItemArch.Lore)))
                                {
                                    Fail($"item {itemNo}: its lore does not match it during the storm");
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                var payerWallet = tx.Open(EntityId.FromRawValue(traderIds[payer])).Read(MkTraderArch.Wallet);
                                if (payerWallet.Credits < amount)
                                {
                                    tx.Rollback();
                                    Interlocked.Increment(ref skipped);
                                    Interlocked.Increment(ref done);
                                    continue;
                                }

                                var s = Interlocked.Increment(ref seq);
                                item.OwnerNo = to;
                                item.Price = newPrice;
                                item.TradeCount++;
                                item.LastTradeSeq = s;
                                tx.OpenMut(itemId).Write(MkItemArch.Item) = item;

                                // One entity at a time: no ref is held across the next OpenMut.
                                tx.OpenMut(EntityId.FromRawValue(traderIds[payer])).Write(MkTraderArch.Wallet).Credits -= amount;
                                tx.OpenMut(EntityId.FromRawValue(traderIds[payee])).Write(MkTraderArch.Wallet).Credits += amount;
                                tx.OpenMut(EntityId.FromRawValue(traderIds[fromSlot])).Write(MkTraderArch.Trader).ItemsOwned--;
                                tx.OpenMut(EntityId.FromRawValue(traderIds[toSlot])).Write(MkTraderArch.Trader).ItemsOwned++;

                                var auditId = tx.Spawn<MkAuditArch>(MkAuditArch.Audit.Set(new MkAudit
                                {
                                    Seq = s, RunNo = runNo, Kind = kind, ItemNo = itemNo, From = owner, To = to, Payer = payer, Payee = payee,
                                    Amount = amount, NewPrice = newPrice,
                                }));

                                if (!tx.Commit())
                                {
                                    Fail($"item {itemNo}: commit returned false");
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                mine.Add(auditId.RawValue);
                                if (kind == KindBuy)
                                {
                                    Interlocked.Increment(ref buys);
                                }
                                else if (kind == KindSell)
                                {
                                    Interlocked.Increment(ref sells);
                                }
                                else
                                {
                                    Interlocked.Increment(ref trades);
                                }

                                Interlocked.Increment(ref done);
                            }
                            finally
                            {
                                for (var k = n - 1; k >= 0; k--)
                                {
                                    Monitor.Exit(traderLocks[stripes[k]]);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Fail($"worker {worker}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    Volatile.Write(ref stop, 1);
                }
            }) { IsBackground = true, Name = $"market-{w}" };
        }

        Log($"storm: {_c.Operations:N0} operations on {_c.Threads} threads");
        var sw = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            t.Start();
        }

        // Progress, and a stall watchdog: no completed operation for StallSeconds fails the run instead of hanging it.
        long lastDone = 0;
        var lastProgress = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            while (!t.Join(TimeSpan.FromSeconds(10)))
            {
                var now = Interlocked.Read(ref done);
                Log($"  {now:N0} ops, {now / sw.Elapsed.TotalSeconds:N0}/s (buys {Interlocked.Read(ref buys):N0}, sells " +
                    $"{Interlocked.Read(ref sells):N0}, trades {Interlocked.Read(ref trades):N0}, skipped {Interlocked.Read(ref skipped):N0})");
                if (now != lastDone)
                {
                    lastDone = now;
                    lastProgress.Restart();
                }
                else if (lastProgress.Elapsed.TotalSeconds > _c.StallSeconds)
                {
                    Fail($"the storm made no progress for {_c.StallSeconds} s at {now:N0} operations");
                    Volatile.Write(ref stop, 1);
                    Assert.Fail(string.Join("\n", _errors));
                }
            }
        }

        lastSeq = Interlocked.Read(ref seq);
        Log($"storm done in {sw.Elapsed.TotalSeconds:F1} s: {done:N0} ops ({done / sw.Elapsed.TotalSeconds:N0}/s), buys {buys:N0}, sells {sells:N0}, " +
            $"trades {trades:N0}, skipped for lack of credits {skipped:N0}");

        var all = new List<ulong>();
        foreach (var list in audits)
        {
            all.AddRange(list);
        }

        if (all.Count != buys + sells + trades)
        {
            Fail($"{all.Count:N0} audit entries recorded for {buys + sells + trades:N0} committed transfers");
        }

        return all;
    }

    #endregion

    #region Checks

    /// <summary>Applies this run's audit entries, in sequence order, to <paramref name="s"/>. Returns how many distinct items were traded.</summary>
    private int Replay(DatabaseEngine dbe, State s, List<ulong> auditIds)
    {
        var entries = new MkAudit[auditIds.Count];
        var batches = (auditIds.Count + ReadBatch - 1) / ReadBatch;
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = _c.Threads }, b =>
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var end = Math.Min(auditIds.Count, (b + 1) * ReadBatch);
            for (var i = b * ReadBatch; i < end; i++)
            {
                entries[i] = tx.Open(EntityId.FromRawValue(auditIds[i])).Read(MkAuditArch.Audit);
            }
        });

        Array.Sort(entries, (a, b) => a.Seq.CompareTo(b.Seq));
        var traded = new HashSet<long>();
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var a = ref entries[i];
            if (i > 0 && a.Seq == entries[i - 1].Seq)
            {
                Fail($"audit sequence {a.Seq} appears twice");
            }

            var item = (int)a.ItemNo;
            if (s.Owner[item] != a.From)
            {
                Fail($"audit {a.Seq}: item {item} moves from {a.From}, but the replay has it at {s.Owner[item]}");
            }

            s.Owner[item] = (int)a.To;
            s.Price[item] = a.NewPrice;
            s.TradeCount[item]++;
            s.LastTradeSeq[item] = a.Seq;
            s.Credits[a.Payer] -= a.Amount;
            s.Credits[a.Payee] += a.Amount;
            s.ItemsOwned[CountSlot(_c, a.From, item)]--;
            s.ItemsOwned[CountSlot(_c, a.To, item)]++;
            traded.Add(item);
        }

        Log($"replayed {entries.Length:N0} audit entries over {traded.Count:N0} items");
        return traded.Count;
    }

    private void CompareStates(State expected, State actual, string what)
    {
        var mismatches = 0;
        for (var t = 1; t <= _c.TraderCount; t++)
        {
            if (expected.Credits[t] != actual.Credits[t] || expected.ItemsOwned[t] != actual.ItemsOwned[t])
            {
                if (mismatches++ < 10)
                {
                    Fail($"{what}: trader {t} credits {actual.Credits[t]} (expected {expected.Credits[t]}), items {actual.ItemsOwned[t]} " +
                         $"(expected {expected.ItemsOwned[t]})");
                }
            }
        }

        for (var i = 0; i < _c.Items; i++)
        {
            if (expected.Owner[i] != actual.Owner[i] || expected.Price[i] != actual.Price[i] || expected.TradeCount[i] != actual.TradeCount[i]
                || expected.LastTradeSeq[i] != actual.LastTradeSeq[i])
            {
                if (mismatches++ < 10)
                {
                    Fail($"{what}: item {i} owner {actual.Owner[i]} price {actual.Price[i]} trades {actual.TradeCount[i]} last {actual.LastTradeSeq[i]} " +
                         $"(expected {expected.Owner[i]} / {expected.Price[i]} / {expected.TradeCount[i]} / {expected.LastTradeSeq[i]})");
                }
            }
        }

        if (mismatches > 10)
        {
            Fail($"{what}: {mismatches:N0} mismatches in all");
        }
    }

    /// <summary>Credits and items conserved; every trader's item count is the number of items that name it.</summary>
    private void CheckConservation(State s, Manifest m, string phase)
    {
        long credits = 0;
        for (var t = 1; t <= _c.TraderCount; t++)
        {
            credits += s.Credits[t];
            if (s.Credits[t] < 0)
            {
                Fail($"{phase}: trader {t} has {s.Credits[t]} credits");
            }
        }

        if (credits != m.TotalCredits)
        {
            Fail($"{phase}: {credits:N0} credits in all, expected {m.TotalCredits:N0}");
        }

        var counted = new long[_c.TraderCount + 1];
        for (var i = 0; i < _c.Items; i++)
        {
            var owner = s.Owner[i];
            if (owner < 0 || owner > _c.TraderCount || (owner >= 1 && owner <= _c.Desks))
            {
                Fail($"{phase}: item {i} is owned by {owner}, which is neither the market nor a player");
                continue;
            }

            counted[CountSlot(_c, owner, i)]++;
        }

        long total = 0;
        for (var t = 1; t <= _c.TraderCount; t++)
        {
            total += counted[t];
            if (counted[t] != s.ItemsOwned[t])
            {
                Fail($"{phase}: trader {t} is named by {counted[t]} items but counts {s.ItemsOwned[t]}");
            }
        }

        if (total != _c.Items)
        {
            Fail($"{phase}: {total:N0} items accounted for, expected {_c.Items:N0}");
        }
    }

    private void CheckAuditCount(DatabaseEngine dbe, long expected, string phase)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        var items = tx.Query<MkItemArch>().Count();
        var audits = tx.Query<MkAuditArch>().Count();
        if (items != _c.Items)
        {
            Fail($"{phase}: the item archetype holds {items:N0} entities, expected {_c.Items:N0}");
        }

        if (audits != expected)
        {
            Fail($"{phase}: {audits:N0} audit entries, expected {expected:N0}");
        }
    }

    /// <summary>
    /// The unique index on <c>LastTradeSeq</c>, re-keyed by every transfer: a range count finds exactly the items traded this run, and point lookups
    /// for a sample of items — traded or not — find exactly that item.
    /// </summary>
    private void CheckIndex(DatabaseEngine dbe, State s, ulong[] itemIds, long firstSeq, int tradedThisRun)
    {
        using (var tx = dbe.CreateReadOnlyTransaction())
        {
            var count = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.LastTradeSeq >= firstSeq).Count();
            if (count != tradedThisRun)
            {
                Fail($"index: {count:N0} items keyed at or after sequence {firstSeq}, but {tradedThisRun:N0} were traded this run");
            }
        }

        var rng = new Random(_c.Seed);
        var samples = Math.Min(2000, _c.Items);
        for (var k = 0; k < samples; k++)
        {
            var i = rng.Next(_c.Items);
            var key = s.LastTradeSeq[i];
            using var tx = dbe.CreateReadOnlyTransaction();
            var found = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.LastTradeSeq == key).Execute();
            if (found.Count != 1 || !found.Contains(EntityId.FromRawValue(itemIds[i])))
            {
                Fail($"index: key {key} should find item {i} alone, found {found.Count} entit(y|ies)");
            }
        }

        Log($"index checked: range count and {samples:N0} point lookups");
    }

    private void CheckIntegrity(DatabaseEngine dbe, string phase)
    {
        var report = dbe.RunStorageIntegrityCheck();
        if (report.Issues.Count != 0 || report.OrphanPageCount != 0 || report.PhantomPageCount != 0)
        {
            Fail($"{phase}: storage integrity — {report.Issues.Count} issue(s), {report.OrphanPageCount} orphan, {report.PhantomPageCount} phantom page(s): " +
                 string.Join("; ", report.Issues));
        }
    }

    /// <summary>The page cache's activity: a database much larger than the cache must have been read from disk far more than the cache holds.</summary>
    private void ReportCache(DatabaseEngine dbe)
    {
        var m = dbe.MMF.GetMetrics();
        var cachePages = (_c.CacheMiB << 20) / PagedMMF.PageSize;
        var fileBytes = dbe.MMF.FileSize;
        Log($"page cache: {cachePages:N0} pages over a {fileBytes / (1024.0 * 1024 * 1024):F2} GiB file; {m.ReadFromDiskCount:N0} pages read, " +
            $"{m.PageWrittenToDiskCount:N0} written in {m.WrittenOperationCount:N0} writes, {m.BackpressureWaitCount:N0} back-pressure rounds, " +
            $"peaks: {dbe.MMF.PeakBackpressureDebt:N0} owed / {dbe.MMF.PeakBackpressureEpochHeld:N0} epoch-held");
        if (fileBytes > 2 * (_c.CacheMiB << 20) && m.ReadFromDiskCount <= cachePages)
        {
            Fail($"the file is over twice the cache, yet only {m.ReadFromDiskCount:N0} pages were read: the run never churned the cache");
        }
    }

    #endregion
}
