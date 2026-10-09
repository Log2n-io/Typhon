using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
/// An item. <see cref="OwnerNo"/> 0 is the market (the item then sits at desk <c>1 + ItemNo % Desks</c>); a non-unique index on it moves the item
/// between two keys on every transfer, one of which — the market's — holds most items. <see cref="LastTradeSeq"/> is the audit sequence of its last
/// transfer, or <c>-(ItemNo + 1)</c> before the first: unique, so a unique index churns on every trade.
/// </summary>
[Component("Typhon.Test.Market.Item", 1)]
public struct MkItem
{
    public long ItemNo;
    [Index(AllowMultiple = true)] public long OwnerNo;
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
/// and one item, updates both sides' item counts, re-keys a unique index and appends an audit entry, all in one transaction. A player may also
/// <i>consume</i> an item: its entity is destroyed and the market crafts a new one with the same item number, in the same transaction — so entities
/// die and slots are reused while the item count stays exact. One transfer in twenty does all of its writes, then rolls back.</para>
/// <para><b>Phases.</b> (1) <i>Build</i>, once: bulk-load the world, close. Reused by later runs while its manifest matches the configuration.
/// (2) <i>Stress</i>: open with the stress cache (default 8 GiB), snapshot and check the state, run the storm, check again. (3) <i>Reopen</i>:
/// close, reopen with a full checksum sweep, check again.</para>
/// <para><b>What is checked, exactly.</b> Items and credits are conserved. Every trader's <c>ItemsOwned</c> equals the items that name it.
/// Replaying this run's audit entries, in sequence order, over the snapshot taken before the storm reproduces every item's owner, price, trade count
/// and last trade, and every trader's credits and item count. The unique index finds exactly the items it should. The owner index — read through the
/// index itself, never a scan — finds under each player exactly the items the player counts, alive, naming the player, each the item's current
/// entity: during the storm for random players under their lock, after it for every player; the items it holds under players and the market's
/// count add up to every item. Lore read during the storm, and a sample after it, matches its item. Every entity a consume
/// destroyed is dead, and its old index key is gone. Nothing a rolled-back transfer wrote is found: not its sequence, its audit entry, nor the item
/// a consume crafted. The storage integrity check is clean. After reopen, the state equals the one before the close.</para>
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
/// progress for that long fails the run), <c>TYPHON_MKT_SAMPLES</c> (the per-second CSV; default <c>samples-&lt;start time&gt;.csv</c> in the database
/// directory), <c>TYPHON_MKT_SETTLE</c> (1: drain the storm's writeback debt before the parallel reads; 0 reproduces #1230),
/// <c>TYPHON_MKT_OWNER_AUDIT</c> (1: audit the owner index during the storm, under each player's lock; 0 skips the auditor — a read concurrent
/// with splits can miss a key's entries, #1235. The checks after the storm and the reopen always run).</para>
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
    private const long KindBuy = 1, KindSell = 2, KindTrade = 3, KindConsume = 4;
    /// <summary>
    /// Items read per short transaction. A reader's pages stay epoch-protected while it lives, and so does every page ANY thread touches meanwhile: the
    /// window is the pages all readers touch while the oldest one is open. Once consumes have scattered the items over clusters a batch touches about a
    /// page per item, so the threads' batches together must stay well inside the cache — a quarter of it here — or the readers pin it all and time out
    /// on back-pressure (a 64 MiB cache, 16 threads of 2,048 items: 8,101 of 8,192 slots epoch-held, nothing dirty).
    /// </summary>
    private int ReadBatch => (int)Math.Clamp((_c.CacheMiB << 20) / PagedMMF.PageSize / (_c.Threads * 4L), 64, 2048);

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
        public string SamplesFile;
        public bool Settle;
        public bool OwnerAudit;

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
            SamplesFile = Environment.GetEnvironmentVariable("TYPHON_MKT_SAMPLES"),
            Settle = Env("SETTLE", 1) != 0,
            OwnerAudit = Env("OWNER_AUDIT", 1) != 0,
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
        /// <summary>
        /// What this test writes and expects of a database it reuses — its schema and its id files. Bumped when either changes meaning (2: consumes
        /// replace item entities, so the id file follows them; 3: <c>OwnerNo</c> is indexed), so an older database is rebuilt rather than misread.
        /// </summary>
        public const int CurrentVersion = 3;

        public int Version { get; set; }

        /// <summary>The run whose storm has started and not yet checked out: non-zero means a run failed part-way, after committing.</summary>
        public int InProgressRun { get; set; }
        public int Items { get; set; }
        public int Players { get; set; }
        public int Desks { get; set; }
        public int Seed { get; set; }
        public long TotalCredits { get; set; }
        public long AuditEntries { get; set; }
        public long LastSeq { get; set; }
        public int Runs { get; set; }

        public bool Matches(Config c) => Version == CurrentVersion && Items == c.Items && Players == c.Players && Desks == c.Desks && Seed == c.Seed;
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
    private Sampler _sampler;

    private void Fail(string message)
    {
        if (_errors.Count < 100)
        {
            _errors.Enqueue(message);
        }
    }

    private static void Log(string message) => TestContext.Progress.WriteLine($"[market {DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>Logs a diagnostic built on a worker thread, where an exception would end the test process: a failure becomes a log line.</summary>
    private static void TryLog(Func<string> message)
    {
        try
        {
            Log(message());
        }
        catch (Exception e)
        {
            try
            {
                Log($"diagnostic failed: {e.GetType().Name}: {e.Message}");
            }
            catch (Exception)
            {
                // Nothing left to report it to.
            }
        }
    }

    /// <summary>
    /// After a stall: gives the stopped workers 30 s to leave, then ends the process if one is still inside the engine. Disposing the engine under a
    /// worker would free the cache it is reading, so failing the test normally — which disposes it — is only safe once they have all left.
    /// </summary>
    private void AbandonIfStuck(Thread[] workers)
    {
        var deadline = Stopwatch.StartNew();
        foreach (var t in workers)
        {
            var left = TimeSpan.FromSeconds(30) - deadline.Elapsed;
            if (!t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                var message = $"{t.Name} is still inside the engine 30 s after the stall; ending the process rather than disposing the engine under it.\n"
                              + string.Join("\n", _errors);
                Log(message);
                Environment.FailFast(message);
            }
        }
    }

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
        var samplesFile = _c.SamplesFile is { Length: > 0 } f ? f : Path.Combine(_c.Directory, $"samples-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var sampler = new Sampler(samplesFile);
        _sampler = sampler;
        Log($"sampling every second to {samplesFile}");

        var manifest = LoadOrBuild();
        var traderIds = ReadIds(TraderIdsFile(_c), _c.TraderCount + 1);
        var itemIds = ReadIds(ItemIdsFile(_c), _c.Items);

        // ── Stress ──
        State after;
        long runAudits;
        List<DestroyedItem> destroyed;
        List<RolledBack> rolledBack;
        sampler.Phase = "open";
        using (var dbe = Open(_c.CacheMiB, OpenVerification.Spine))
        {
            using var tracked = sampler.Track(dbe, "snapshot");
            var runNo = manifest.Runs + 1;
            Log($"census after open: {dbe.MMF.DescribeEvictionBlockers()}");
            var sw = Stopwatch.StartNew();
            var state = ReadState(dbe, traderIds, itemIds, lorePermille: 0);
            Log($"snapshot read in {sw.Elapsed.TotalSeconds:F1} s");
            Log($"census after the snapshot: {dbe.MMF.DescribeEvictionBlockers()}");
            CheckConservation(state, manifest, "before the storm");
            CheckAuditCount(dbe, manifest.AuditEntries, "before the storm");
            AssertNoErrors("before the storm");

            var firstSeq = manifest.LastSeq + 1;

            // From here the run commits: until it checks out, the database runs ahead of this manifest and of the id files.
            manifest.InProgressRun = runNo;
            File.WriteAllText(ManifestFile(_c), JsonSerializer.Serialize(manifest));
            sampler.Phase = "storm";
            var auditIds = Storm(dbe, traderIds, itemIds, runNo, firstSeq, out var lastSeq, out destroyed, out rolledBack);
            AssertNoErrors("during the storm");
            runAudits = auditIds.Count;

            // The storm leaves the cache full of writeback debt. Let the checkpoint drain it before the parallel reads: a reader that waits for a slot keeps
            // its epoch pinned, every page the others touch meanwhile is then protected, and on a small cache they all time out together (#1230).
            // TYPHON_MKT_SETTLE=0 skips it, to reproduce that. Remove once #1230 is fixed.
            if (_c.Settle)
            {
                sampler.Phase = "settle";
                sw.Restart();
                Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromMinutes(10)), Is.True,
                    "the checkpoint did not settle the storm: timed out, or checkpointing halted");
                Log($"storm settled in {sw.Elapsed.TotalSeconds:F1} s");
            }

            sampler.Phase = "state read";
            sw.Restart();
            after = ReadState(dbe, traderIds, itemIds, _c.LoreSamplePermille);
            Log($"state read in {sw.Elapsed.TotalSeconds:F1} s");

            // `state` becomes the expected state: the snapshot with this run's audit replayed over it.
            sampler.Phase = "replay";
            var traded = Replay(dbe, state, auditIds);
            CompareStates(state, after, "replayed audit vs database");
            CheckConservation(after, manifest, "after the storm");
            CheckAuditCount(dbe, manifest.AuditEntries + runAudits, "after the storm");
            sampler.Phase = "index check";
            CheckIndex(dbe, after, itemIds, firstSeq, traded);
            CheckOwnerIndex(dbe, after, itemIds, "after the storm");
            CheckDestroyed(dbe, destroyed, "after the storm");
            CheckRolledBack(dbe, rolledBack, "after the storm");
            sampler.Phase = "integrity check";
            CheckIntegrity(dbe, "after the storm");
            ReportCache(dbe);
            AssertNoErrors("after the storm");

            manifest.AuditEntries += runAudits;
            manifest.LastSeq = lastSeq;
            manifest.Runs = runNo;
            manifest.InProgressRun = 0;
            WriteIds(ItemIdsFile(_c), itemIds);   // consumed items are new entities: the next run reads them by their new ids
            File.WriteAllText(ManifestFile(_c), JsonSerializer.Serialize(manifest));
            Log("closing");
            sampler.Phase = "close";
        }

        // ── Reopen ──
        sampler.Phase = "reopen";
        var reopen = Stopwatch.StartNew();
        using (var dbe = Open(_c.CacheMiB, _c.ReopenVerify))
        {
            using var tracked = sampler.Track(dbe, "reread");
            Log($"reopened with {_c.ReopenVerify} verification in {reopen.Elapsed.TotalSeconds:F1} s");
            var reread = ReadState(dbe, traderIds, itemIds, _c.LoreSamplePermille);
            CompareStates(after, reread, "after reopen vs before close");
            CheckAuditCount(dbe, manifest.AuditEntries, "after reopen");
            CheckOwnerIndex(dbe, after, itemIds, "after reopen");
            CheckDestroyed(dbe, destroyed, "after reopen");
            CheckRolledBack(dbe, rolledBack, "after reopen");
            sampler.Phase = "integrity check (reopen)";
            CheckIntegrity(dbe, "after reopen");
            AssertNoErrors("after reopen");
            sampler.Phase = "close (reopen)";
        }

        sampler.Phase = "done";
        Log($"run {manifest.Runs} coherent: {runAudits:N0} transfers, {manifest.AuditEntries:N0} audit entries in total");
    }

    private void AssertNoErrors(string phase) =>
        Assert.That(_errors, Is.Empty, $"{phase}: {_errors.Count} problem(s)\n  " + string.Join("\n  ", _errors));

    #region Sampling

    /// <summary>
    /// One CSV row per second for the whole run, so a run can be plotted and set against another instead of judged by its end totals: the phase, the
    /// cache's unevictable slots by reason, its disk traffic and back-pressure, the checkpoint's progress, the storm's operations and the process's memory.
    /// </summary>
    /// <remarks>
    /// The slot columns (<c>debt</c> to <c>unevictable</c>) are gauges. The others are cumulative counters of the engine being sampled, so a rate is the
    /// difference between two rows divided by the difference of their <c>t_s</c> — and they restart with each engine: build, stress, reopen. The engine
    /// columns are empty while no engine is tracked (opening, closing). A <c># start=</c> line gives the wall-clock time of <c>t_s</c> 0, to line rows up
    /// with the log.
    /// </remarks>
    private sealed class Sampler : IDisposable
    {
        private const string Header =
            "t_s,phase,slots_used,debt,acw,slot_ref,epoch_held,unevictable,pages_read,pages_written,bp_rounds,bp_peak_debt,bp_peak_epoch_held," +
            "bp_longest_wait_ms,ckpt_cycles,ckpt_pressure_cycles,ckpt_pages_written,ops,private_mib,working_set_mib,managed_mib";

        private readonly StreamWriter _out;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new();
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private DatabaseEngine _dbe;
        private Func<long> _operations;

        public volatile string Phase = "start";

        public Sampler(string path)
        {
            _out = new StreamWriter(path) { AutoFlush = true };
            _out.WriteLine(Header);
            _out.WriteLine($"# start={DateTime.Now:O}");
            _thread = new Thread(Run) { IsBackground = true, Name = "market-sampler" };
            _thread.Start();
        }

        /// <summary>Samples <paramref name="dbe"/> until the returned scope ends, which must come before the engine is disposed.</summary>
        public Tracking Track(DatabaseEngine dbe, string phase)
        {
            lock (_gate)
            {
                _dbe = dbe;
                Phase = phase;
            }

            return new Tracking(this);
        }

        /// <summary>The storm's completed operations, read every sample while the engine stays tracked.</summary>
        public void CountOperations(Func<long> read)
        {
            lock (_gate)
            {
                _operations = read;
            }
        }

        public sealed class Tracking(Sampler owner) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._dbe = null;
                    owner._operations = null;
                }
            }
        }

        private void Run()
        {
            do
            {
                try
                {
                    Sample();
                }
                catch (Exception e)
                {
                    // A background thread's exception would end the test process; the row says what went wrong instead — unless writing is what failed.
                    try
                    {
                        _out.WriteLine($"# sample failed at {_clock.Elapsed.TotalSeconds:F2} s: {e.GetType().Name}: {e.Message}");
                    }
                    catch (Exception)
                    {
                        // Nothing left to report it to: the run goes on without samples.
                    }
                }
            } while (!_stop.Wait(1000));
        }

        private void Sample()
        {
            var row = new StringBuilder(256);
            lock (_gate)
            {
                // Under the gate: the engine is untracked, then disposed, never the other way round; and the phase is the tracked engine's.
                row.Append($"{_clock.Elapsed.TotalSeconds:F2},{Phase}");
                var dbe = _dbe;
                if (dbe != null)
                {
                    var mmf = dbe.MMF;
                    var (debt, acw, slotRef, epochHeld, unevictable, total) = mmf.CountUnevictablePages();
                    var m = mmf.GetMetrics();
                    var ck = dbe.CheckpointManager;
                    var waitMs = Stopwatch.GetElapsedTime(0, Volatile.Read(ref mmf.PeakBackpressureWaitTicks)).TotalMilliseconds;
                    row.Append($",{total},{debt},{acw},{slotRef},{epochHeld},{unevictable},{m.ReadFromDiskCount},{m.PageWrittenToDiskCount}");
                    row.Append($",{m.BackpressureWaitCount},{mmf.PeakBackpressureDebt},{mmf.PeakBackpressureEpochHeld},{waitMs:F0}");
                    row.Append($",{ck?.TotalCheckpoints},{ck?.TotalPressureCheckpoints},{ck?.TotalPagesWritten},{_operations?.Invoke()}");
                }
                else
                {
                    row.Append(",,,,,,,,,,,,,,,,");
                }
            }

            using var process = Process.GetCurrentProcess();
            row.Append($",{process.PrivateMemorySize64 >> 20},{process.WorkingSet64 >> 20},{GC.GetTotalMemory(false) >> 20}");
            _out.WriteLine(row.ToString());
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            Sample();
            _out.Dispose();
            _stop.Dispose();
        }
    }

    #endregion

    #region Build

    /// <summary>
    /// The build's page writes, counted per file page through <see cref="PagedMMF.PageWriteInterceptor"/>. Every page a checkpoint writes counts once;
    /// the async structural path calls the interceptor once per run, so a multi-page run counts at its first page only; and the build engine's close,
    /// after the interceptor is removed, is not counted.
    /// </summary>
    private sealed class WriteCounter(int pages)
    {
        private readonly int[] _counts = new int[pages];
        private readonly Dictionary<int, int> _beyond = new();

        public void Count(int filePage)
        {
            if ((uint)filePage < (uint)_counts.Length)
            {
                Interlocked.Increment(ref _counts[filePage]);
                return;
            }

            lock (_beyond)
            {
                _beyond[filePage] = _beyond.TryGetValue(filePage, out var n) ? n + 1 : 1;
            }
        }

        /// <summary>Every page written and its writes.</summary>
        public List<(int Page, int Writes)> Pages()
        {
            var pages = new List<(int, int)>();
            for (var p = 0; p < _counts.Length; p++)
            {
                var n = Volatile.Read(ref _counts[p]);
                if (n > 0)
                {
                    pages.Add((p, n));
                }
            }

            lock (_beyond)
            {
                foreach (var (p, n) in _beyond)
                {
                    pages.Add((p, n));
                }
            }

            return pages;
        }
    }

    /// <summary>The build's page writes per segment kind: writes, distinct pages written, and writes per page.</summary>
    private static void LogWriteCensus(DatabaseEngine dbe, WriteCounter writes)
    {
        // The segment kind of every file page, as an index into `kinds` (0: no segment).
        var kinds = new List<string> { "unowned" };
        var kindOf = new byte[dbe.MMF.FileSize / PagedMMF.PageSize + 1];
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            var name = seg.Kind.ToString();
            var kind = kinds.IndexOf(name);
            if (kind < 0)
            {
                kind = kinds.Count;
                kinds.Add(name);
            }

            foreach (var page in seg.Pages.Span)
            {
                if ((uint)page < (uint)kindOf.Length)
                {
                    kindOf[page] = (byte)kind;
                }
            }
        }

        var kindWrites = new long[kinds.Count];
        var kindPages = new long[kinds.Count];
        long total = 0;
        var pages = writes.Pages();
        foreach (var (page, n) in pages)
        {
            var kind = (uint)page < (uint)kindOf.Length ? kindOf[page] : 0;
            kindWrites[kind] += n;
            kindPages[kind]++;
            total += n;
        }

        var parts = new List<string>();
        foreach (var kind in Enumerable.Range(0, kinds.Count).Where(k => kindPages[k] > 0).OrderByDescending(k => kindWrites[k]))
        {
            parts.Add($"{kinds[kind]} {kindWrites[kind] * PagedMMF.PageSize / (1024.0 * 1024 * 1024):F2} GiB written over {kindPages[kind]:N0} pages "
                      + $"(x{(double)kindWrites[kind] / kindPages[kind]:F1})");
        }

        Log($"build writes: {total * PagedMMF.PageSize / (1024.0 * 1024 * 1024):F2} GiB over {pages.Count:N0} pages — {string.Join("; ", parts)}");
    }

    private Manifest LoadOrBuild()
    {
        if (!_c.Rebuild && File.Exists(ManifestFile(_c)) && Directory.Exists(DatabaseFile(_c)))
        {
            var existing = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestFile(_c)));
            if (existing != null && existing.Matches(_c))
            {
                if (existing.InProgressRun != 0)
                {
                    Assert.Fail($"run {existing.InProgressRun} failed part-way after committing: the database is ahead of its manifest and its id files. "
                                + "Rebuild it with TYPHON_MKT_REBUILD=1.");
                }

                Log($"reusing the database built for this configuration ({existing.Runs} run(s) so far)");
                return existing;
            }

            Log(existing == null
                ? "the manifest is unreadable: rebuilding"
                : $"the database was built for another configuration or layout (version {existing.Version}, current {Manifest.CurrentVersion}): rebuilding");
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
            using var tracked = _sampler.Track(dbe, "build");

            // Every page write of the build, by file page: what the build writes, segment by segment, against what it ends up holding. Sized for the
            // file the build is expected to make (~0.8 KiB per item), with room; pages past it are counted aside.
            var writes = new WriteCounter((int)Math.Min(int.MaxValue, (long)_c.Items * 1024 / PagedMMF.PageSize + (1 << 20)));
            dbe.MMF.PageWriteInterceptor = writes.Count;
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
            dbe.MMF.PageWriteInterceptor = null;
            LogWriteCensus(dbe, writes);
            _sampler.Phase = "build close";
        }

        WriteIds(TraderIdsFile(_c), traderIds);
        WriteIds(ItemIdsFile(_c), itemIds);
        var manifest = new Manifest
        {
            Version = Manifest.CurrentVersion, Items = _c.Items, Players = _c.Players, Desks = _c.Desks, Seed = _c.Seed, TotalCredits = totalCredits,
            AuditEntries = 0, LastSeq = 0, Runs = 0,
        };
        File.WriteAllText(ManifestFile(_c), JsonSerializer.Serialize(manifest));
        return manifest;
    }

    /// <summary>Writes the ids to a temporary file, then renames it over <paramref name="path"/>: a crash part-way leaves the old file whole.</summary>
    private static void WriteIds(string path, ulong[] ids)
    {
        var temporary = path + ".tmp";
        using (var f = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            f.Write(MemoryMarshal.AsBytes(ids.AsSpan()));
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static ulong[] ReadIds(string path, int count)
    {
        var ids = new ulong[count];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read);
        f.ReadExactly(MemoryMarshal.AsBytes(ids.AsSpan()));
        return ids;
    }

    #endregion

    /// <summary>
    /// A parallel pass over read batches. The first back-pressure timeout in it logs who holds the cache — the eviction census and the epoch pins — from
    /// the failing reader, before it unwinds, while the other readers still hold what they held.
    /// </summary>
    private void ReadInParallel(DatabaseEngine dbe, int batches, Action<int> body, int threads = 0)
    {
        var censusTaken = 0;
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : _c.Threads }, b =>
        {
            try
            {
                body(b);
            }
            catch (PageCacheBackpressureTimeoutException) when (Interlocked.Exchange(ref censusTaken, 1) == 0 && LogReadCensus(dbe))
            {
                // Unreachable: the filter logs and returns false, so the exception propagates untouched.
            }
        });
    }

    /// <summary>The census of a back-pressure timeout, for an exception filter: logs, then returns false so the exception is not caught.</summary>
    private static bool LogReadCensus(DatabaseEngine dbe)
    {
        TryLog(() => $"back-pressure census: {dbe.MMF.DescribeEvictionBlockers()}");
        TryLog(() => $"epoch pins: {dbe.EpochManager.DescribePinnedThreads()}");
        return false;
    }

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
        ReadInParallel(dbe, batches, b =>
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

    /// <summary>An item entity a consume destroyed, and the unique-index key it held: neither may be found again.</summary>
    private readonly record struct DestroyedItem(ulong Id, long Key, int ItemNo);

    /// <summary>A transfer that did all of its writes, then rolled back: its sequence, its audit entry and the item a consume crafted never existed.</summary>
    private readonly record struct RolledBack(long Seq, ulong AuditId, ulong CraftedId, int ItemNo);

    /// <remarks>
    /// <paramref name="itemIds"/> follows the storm: a consume replaces an item's entity, and its slot in the table is rewritten under the item's lock,
    /// which is also the only place it is read during the storm.
    /// </remarks>
    private List<ulong> Storm(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds, int runNo, long firstSeq, out long lastSeq,
        out List<DestroyedItem> destroyed, out List<RolledBack> rolledBack)
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
        long started = 0, done = 0, skipped = 0, buys = 0, sells = 0, trades = 0, consumes = 0, rollbacks = 0, ownerAudits = 0;
        var stop = 0;
        var stormOver = 0;
        var censusTaken = 0;
        var audits = new List<ulong>[_c.Threads];
        var destroyedBy = new List<DestroyedItem>[_c.Threads];
        var rolledBackBy = new List<RolledBack>[_c.Threads];
        var threads = new Thread[_c.Threads];
        _sampler.CountOperations(() => Interlocked.Read(ref done));

        for (var w = 0; w < _c.Threads; w++)
        {
            var worker = w;
            audits[w] = new List<ulong>();
            destroyedBy[w] = new List<DestroyedItem>();
            rolledBackBy[w] = new List<RolledBack>();
            threads[w] = new Thread(() =>
            {
                var rng = new Random(_c.Seed * 7919 + runNo * 104729 + worker);
                var mine = audits[worker];
                var myDestroyed = destroyedBy[worker];
                var myRolledBack = rolledBackBy[worker];
                var stripes = new int[4];
                try
                {
                    while (Volatile.Read(ref stop) == 0 && Interlocked.Increment(ref started) <= _c.Operations)
                    {
                        var itemNo = rng.Next(_c.Items);
                        lock (itemLocks[itemNo & (itemLocks.Length - 1)])
                        {
                            // Under the lock: a consume replaces the item's entity, and rewrites this slot before releasing it.
                            var itemId = EntityId.FromRawValue(itemIds[itemNo]);

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
                            var roll = owner == 0 ? 0 : rng.Next(10);
                            if (owner == 0)
                            {
                                kind = KindBuy;
                                to = _c.Desks + 1 + rng.Next(_c.Players);
                                payer = to;
                                payee = desk;
                                amount = price;
                            }
                            else if (roll < 2)
                            {
                                // Consumed: the item leaves the world, and its desk crafts it anew, at its base price, for a one-credit fee.
                                kind = KindConsume;
                                to = 0;
                                payer = owner;
                                payee = desk;
                                amount = 1;
                                newPrice = PriceOf(itemNo);
                            }
                            else if (roll < 5)
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
                                var oldKey = item.LastTradeSeq;
                                item.OwnerNo = to;
                                item.Price = newPrice;
                                item.TradeCount++;
                                item.LastTradeSeq = s;
                                var newItemId = itemId;
                                if (kind == KindConsume)
                                {
                                    // Destroyed and crafted anew in one transaction: the new entity carries the item's number, its count of transfers and
                                    // this transfer's sequence; its lore is derived from the number again.
                                    tx.Destroy(itemId);
                                    newItemId = tx.Spawn<MkItemArch>(MkItemArch.Item.Set(item));
                                    tx.OpenMut(newItemId).Enable(MkItemArch.Lore, LoreOf(itemNo));
                                }
                                else
                                {
                                    tx.OpenMut(itemId).Set(MkItemArch.Item, item);
                                }

                                // One entity at a time: read, change the copy, set it back.
                                var target = tx.OpenMut(EntityId.FromRawValue(traderIds[payer]));
                                var walletCopy = target.Read(MkTraderArch.Wallet);
                                walletCopy.Credits -= amount;
                                target.Set(MkTraderArch.Wallet, walletCopy);
                                var opened = tx.OpenMut(EntityId.FromRawValue(traderIds[payee]));
                                var wallet = opened.Read(MkTraderArch.Wallet);
                                wallet.Credits += amount;
                                opened.Set(MkTraderArch.Wallet, wallet);
                                var entity2 = tx.OpenMut(EntityId.FromRawValue(traderIds[fromSlot]));
                                var traderCopy = entity2.Read(MkTraderArch.Trader);
                                traderCopy.ItemsOwned--;
                                entity2.Set(MkTraderArch.Trader, traderCopy);
                                var entity3 = tx.OpenMut(EntityId.FromRawValue(traderIds[toSlot]));
                                var trader = entity3.Read(MkTraderArch.Trader);
                                trader.ItemsOwned++;
                                entity3.Set(MkTraderArch.Trader, trader);

                                var auditId = tx.Spawn<MkAuditArch>(MkAuditArch.Audit.Set(new MkAudit
                                {
                                    Seq = s, RunNo = runNo, Kind = kind, ItemNo = itemNo, From = owner, To = to, Payer = payer, Payee = payee,
                                    Amount = amount, NewPrice = newPrice,
                                }));

                                // One in twenty changes its mind with every write done: the item, both wallets, both counts, the audit entry — and, for
                                // a consume, a destroy, a spawn and an enable. None of it may be seen, and its sequence number stays unused.
                                if (rng.Next(20) == 0)
                                {
                                    tx.Rollback();
                                    myRolledBack.Add(new RolledBack(s, auditId.RawValue, kind == KindConsume ? newItemId.RawValue : 0, itemNo));
                                    Interlocked.Increment(ref rollbacks);
                                    Interlocked.Increment(ref done);
                                    continue;
                                }

                                if (!tx.Commit())
                                {
                                    Fail($"item {itemNo}: commit returned false");
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                mine.Add(auditId.RawValue);
                                if (kind == KindConsume)
                                {
                                    itemIds[itemNo] = newItemId.RawValue;
                                    myDestroyed.Add(new DestroyedItem(itemId.RawValue, oldKey, itemNo));
                                    Interlocked.Increment(ref consumes);
                                }
                                else if (kind == KindBuy)
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
                catch (PageCacheBackpressureTimeoutException) when (Interlocked.Exchange(ref censusTaken, 1) == 0 && LogReadCensus(dbe))
                {
                    // Unreachable: the filter takes the census — every reason a page stays in the cache, and who pins the epoch — from inside the failing
                    // worker before it unwinds, then returns false, so the exception reaches the handler below untouched.
                }
                catch (Exception e)
                {
                    Fail($"worker {worker}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    Volatile.Write(ref stop, 1);
                }
            }) { IsBackground = true, Name = $"market-{w}" };
        }

        // The owner index's auditor: a player's items found through it, against the count the player keeps, under the player's stripe lock — so no
        // transfer of theirs is in flight and both sides are at the same commit. It holds that one lock alone: no cycle with the workers' order.
        var ownerIndex = _c.OwnerAudit ? dbe.GetIndexRef<MkItem, long>(x => x.OwnerNo) : default;
        var auditor = new Thread(() =>
        {
            var rng = new Random(_c.Seed * 31 + runNo);
            try
            {
                while (_c.OwnerAudit && Volatile.Read(ref stormOver) == 0 && Volatile.Read(ref stop) == 0)
                {
                    var player = _c.Desks + 1 + rng.Next(_c.Players);
                    lock (traderLocks[player & (traderLocks.Length - 1)])
                    {
                        CheckPlayerItems(dbe, ownerIndex, traderIds, itemIds, player, "during the storm");
                    }

                    Interlocked.Increment(ref ownerAudits);
                }
            }
            catch (PageCacheBackpressureTimeoutException) when (Interlocked.Exchange(ref censusTaken, 1) == 0 && LogReadCensus(dbe))
            {
                // Unreachable: the filter logs and returns false.
            }
            catch (Exception e)
            {
                Fail($"owner auditor: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                Volatile.Write(ref stop, 1);
            }
        }) { IsBackground = true, Name = "market-auditor" };

        Log($"storm: {_c.Operations:N0} operations on {_c.Threads} threads, and an owner-index auditor");
        var sw = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            t.Start();
        }

        auditor.Start();

        // Progress, and a stall watchdog: no completed operation for StallSeconds fails the run instead of hanging it.
        long lastDone = 0;
        var lastProgress = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            while (!t.Join(TimeSpan.FromSeconds(10)))
            {
                var now = Interlocked.Read(ref done);
                Log($"  {now:N0} ops, {now / sw.Elapsed.TotalSeconds:N0}/s (buys {Interlocked.Read(ref buys):N0}, sells " +
                    $"{Interlocked.Read(ref sells):N0}, trades {Interlocked.Read(ref trades):N0}, consumed {Interlocked.Read(ref consumes):N0}, " +
                    $"rolled back {Interlocked.Read(ref rollbacks):N0}, skipped {Interlocked.Read(ref skipped):N0}; " +
                    $"{Interlocked.Read(ref ownerAudits):N0} owner audits)");
                if (now != lastDone)
                {
                    lastDone = now;
                    lastProgress.Restart();
                }
                else if (lastProgress.Elapsed.TotalSeconds > _c.StallSeconds)
                {
                    Fail($"the storm made no progress for {_c.StallSeconds} s at {now:N0} operations");
                    TryLog(() => $"stall census: {dbe.MMF.DescribeEvictionBlockers()}");
                    Volatile.Write(ref stop, 1);
                    AbandonIfStuck([.. threads, auditor]);
                    Assert.Fail(string.Join("\n", _errors));
                }
            }
        }

        Volatile.Write(ref stormOver, 1);
        if (!auditor.Join(TimeSpan.FromSeconds(60)))
        {
            Fail("the owner auditor did not stop within 60 s of the storm's end");
            AbandonIfStuck([auditor]);
        }

        lastSeq = Interlocked.Read(ref seq);
        Log($"storm done in {sw.Elapsed.TotalSeconds:F1} s: {done:N0} ops ({done / sw.Elapsed.TotalSeconds:N0}/s), buys {buys:N0}, sells {sells:N0}, " +
            $"trades {trades:N0}, consumed {consumes:N0}, rolled back {rollbacks:N0}, skipped for lack of credits {skipped:N0}; " +
            $"{ownerAudits:N0} owner audits");
        if (_c.OwnerAudit && ownerAudits == 0)
        {
            Log("note: the storm ended before the owner auditor completed an audit");
        }

        var all = new List<ulong>();
        foreach (var list in audits)
        {
            all.AddRange(list);
        }

        if (all.Count != buys + sells + trades + consumes)
        {
            Fail($"{all.Count:N0} audit entries recorded for {buys + sells + trades + consumes:N0} committed transfers");
        }

        destroyed = new List<DestroyedItem>();
        foreach (var list in destroyedBy)
        {
            destroyed.AddRange(list);
        }

        rolledBack = new List<RolledBack>();
        foreach (var list in rolledBackBy)
        {
            rolledBack.AddRange(list);
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
        ReadInParallel(dbe, batches, b =>
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

    /// <summary>
    /// Every entity a consume destroyed is dead — checked for all of them, in short transactions like the state read — and, for one in sixteen, the
    /// unique index finds nothing under the key it held: keys are sequence numbers, never reused, so any hit is a stale entry.
    /// </summary>
    private void CheckDestroyed(DatabaseEngine dbe, List<DestroyedItem> destroyed, string phase)
    {
        var sw = Stopwatch.StartNew();
        var batches = (destroyed.Count + ReadBatch - 1) / ReadBatch;
        long alive = 0, indexed = 0;
        ReadInParallel(dbe, batches, b =>
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var end = Math.Min(destroyed.Count, (b + 1) * ReadBatch);
            for (var i = b * ReadBatch; i < end; i++)
            {
                var d = destroyed[i];
                var id = EntityId.FromRawValue(d.Id);
                if (tx.IsAlive(id) || tx.TryOpen(id, out _))
                {
                    if (Interlocked.Increment(ref alive) <= 10)
                    {
                        Fail($"{phase}: item {d.ItemNo}'s destroyed entity {id} is still alive");
                    }
                }

                // Sampled: a point lookup is a B+Tree descent and a cluster read.
                if ((i & 15) == 0)
                {
                    var key = d.Key;
                    var found = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.LastTradeSeq == key).Execute();
                    if (found.Count != 0 && Interlocked.Increment(ref indexed) <= 10)
                    {
                        Fail($"{phase}: the unique index still finds {found.Count} entit(y|ies) under item {d.ItemNo}'s destroyed key {key}");
                    }
                }
            }
        });

        if (alive > 10 || indexed > 10)
        {
            Fail($"{phase}: {alive:N0} destroyed entities alive, {indexed:N0} still indexed, of {destroyed.Count:N0}");
        }

        Log($"{destroyed.Count:N0} destroyed entities checked dead ({phase}) in {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// The items the owner index finds under <paramref name="player"/> are exactly the player's: as many as the player counts, each alive, naming the
    /// player, and the item's current entity. Read through the index itself — a query may scan the clusters instead, which tests nothing here and sweeps
    /// the whole cache — in one transaction, so both sides are one snapshot; the caller makes sure no transfer of the player's is in flight.
    /// </summary>
    private void CheckPlayerItems(DatabaseEngine dbe, IndexRef ownerIndex, ulong[] traderIds, ulong[] itemIds, long player, string phase)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        var counted = tx.Open(EntityId.FromRawValue(traderIds[player])).Read(MkTraderArch.Trader).ItemsOwned;
        var found = 0;
        using (var e = tx.EnumerateIndex<MkItem, long>(ownerIndex, player, player))
        {
            while (e.MoveNext())
            {
                found++;
                var item = e.CurrentComponent;
                var id = (ulong)e.CurrentEntityPK;
                if (item.OwnerNo != player)
                {
                    Fail($"{phase}: the owner index finds item {item.ItemNo} under player {player}, but it names owner {item.OwnerNo}");
                }
                else if (Volatile.Read(ref itemIds[item.ItemNo]) != id)
                {
                    Fail($"{phase}: the owner index finds entity {EntityId.FromRawValue(id)} for item {item.ItemNo} under player {player}, which is not the "
                         + "item's entity");
                }
            }
        }

        if (found != counted)
        {
            Fail($"{phase}: the owner index finds {found} item(s) under player {player}, who counts {counted}");
        }
    }

    /// <summary>
    /// The owner index after the storm, or the reopen, for every player: each player's key holds exactly the items the state gives them, each naming
    /// them and the item's current entity — read through the index in ranges of player keys, a short transaction each, like the state read. And the raw
    /// count of every key's value buffer, the market's included (most of the items, one huge buffer, never walked): no stale entry for a dead entity,
    /// which a read through the index skips, and no append that went where no read reaches (IXW-08).
    /// </summary>
    private void CheckOwnerIndex(DatabaseEngine dbe, State s, ulong[] itemIds, string phase)
    {
        var sw = Stopwatch.StartNew();
        var ownerIndex = dbe.GetIndexRef<MkItem, long>(x => x.OwnerNo);
        long atMarket = 0;
        for (var i = 0; i < _c.Items; i++)
        {
            atMarket += s.Owner[i] == 0 ? 1 : 0;
        }

        var archetype = EntityId.FromRawValue(itemIds[0]).ArchetypeId;
        var rawMarket = RawOwnerEntries(dbe, archetype, 0);
        if (rawMarket != atMarket)
        {
            Fail($"{phase}: the owner index's market key holds {rawMarket:N0} entries; {atMarket:N0} items are at the market");
        }

        // ReadBatch is a budget in ITEMS at about a page each — what the state read touches. An item resolved through the index touches about four (its
        // leaf and value buffer, its cluster, its map record, its revision and content), and every key a raw count's descent: a batch takes a quarter of
        // the players that hold ReadBatch items. And ONE reader: with several, every page any of them touches is held for the oldest (#1230), and on a
        // cold cache after the reopen four of them pinned 8,188 of 8,192 slots.
        long firstPlayer = _c.Desks + 1;
        var perPlayer = Math.Max(1L, (_c.Items - atMarket) / Math.Max(1, _c.Players));
        var playersPerBatch = (int)Math.Max(1, ReadBatch / (4 * perPlayer));
        var batches = (_c.Players + playersPerBatch - 1) / playersPerBatch;
        long held = 0, wrong = 0;
        ReadInParallel(dbe, batches, b =>
        {
            var lo = firstPlayer + (long)b * playersPerBatch;
            var hi = Math.Min(_c.TraderCount, lo + playersPerBatch - 1);
            var counts = new int[hi - lo + 1];
            long here = 0;
            using var tx = dbe.CreateReadOnlyTransaction();
            using (var e = tx.EnumerateIndex<MkItem, long>(ownerIndex, lo, hi))
            {
                while (e.MoveNext())
                {
                    here++;
                    var key = e.CurrentKey;
                    var item = e.CurrentComponent;
                    var id = (ulong)e.CurrentEntityPK;
                    if (key < lo || key > hi || item.OwnerNo != key || itemIds[item.ItemNo] != id)
                    {
                        if (Interlocked.Increment(ref wrong) <= 10)
                        {
                            Fail($"{phase}: the owner index holds entity {EntityId.FromRawValue(id)} (item {item.ItemNo}, owner {item.OwnerNo}) "
                                 + $"under key {key}");
                        }

                        continue;
                    }

                    counts[key - lo]++;
                }
            }

            for (var p = lo; p <= hi; p++)
            {
                if (counts[p - lo] != s.ItemsOwned[p] && Interlocked.Increment(ref wrong) <= 10)
                {
                    Fail($"{phase}: the owner index holds {counts[p - lo]} item(s) under player {p}, who owns {s.ItemsOwned[p]}");
                }

                var raw = RawOwnerEntries(dbe, archetype, p);
                if (raw != s.ItemsOwned[p] && Interlocked.Increment(ref wrong) <= 10)
                {
                    Fail($"{phase}: the owner index's key {p} holds {raw} raw entries; the player owns {s.ItemsOwned[p]}");
                }
            }

            Interlocked.Add(ref held, here);
        }, threads: 1);

        if (wrong > 10)
        {
            Fail($"{phase}: {wrong:N0} owner-index problems in all");
        }

        Log($"owner index checked ({phase}): {held:N0} items under {_c.Players:N0} players, {rawMarket:N0} at the market, in {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>
    /// The raw number of entries the owner index holds under <paramref name="key"/>: its value buffer's count, with no MVCC filter — a stale entry for a
    /// dead entity counts, where a read through the index skips it. O(1) per key. Only while no transfer is in flight.
    /// </summary>
    private static unsafe int RawOwnerEntries(DatabaseEngine dbe, ushort archetype, long key)
    {
        var ownerOffset = (int)Marshal.OffsetOf<MkItem>(nameof(MkItem.OwnerNo));
        foreach (var slot in dbe._stateByRouting[archetype].ClusterState.IndexSlots)
        {
            foreach (var field in slot.Fields)
            {
                if (!field.AllowMultiple || field.FieldOffset != ownerOffset)
                {
                    continue;
                }

                using var guard = EpochGuard.Enter(dbe.EpochManager);
                var accessor = field.Index.Segment.CreateChunkAccessor();
                try
                {
                    var buffer = field.Index.TryGetMultiple(&key, ref accessor);
                    var count = buffer.IsValid ? buffer.TotalCount : 0;
                    buffer.Dispose();
                    return count;
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }

        throw new InvalidOperationException("the item archetype has no OwnerNo index");
    }

    /// <summary>
    /// Nothing a rolled-back transfer wrote is found: its audit entry was never born, nor was the item a consume crafted — for every one — and, for one in
    /// sixteen (a point lookup is a B+Tree descent), no item is keyed by its sequence. The rest — the item, the wallets, the counts — is checked by the
    /// replay, which never sees the transfer.
    /// </summary>
    private void CheckRolledBack(DatabaseEngine dbe, List<RolledBack> rolledBack, string phase)
    {
        var sw = Stopwatch.StartNew();
        var batches = (rolledBack.Count + ReadBatch - 1) / ReadBatch;
        long found = 0;
        ReadInParallel(dbe, batches, b =>
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var end = Math.Min(rolledBack.Count, (b + 1) * ReadBatch);
            for (var i = b * ReadBatch; i < end; i++)
            {
                var r = rolledBack[i];
                var seq = r.Seq;
                string what = null;
                if (tx.IsAlive(EntityId.FromRawValue(r.AuditId)))
                {
                    what = $"its audit entry {EntityId.FromRawValue(r.AuditId)} is alive";
                }
                else if (r.CraftedId != 0 && tx.IsAlive(EntityId.FromRawValue(r.CraftedId)))
                {
                    what = $"the item it crafted, {EntityId.FromRawValue(r.CraftedId)}, is alive";
                }
                else if ((i & 15) == 0 && tx.Query<MkItemArch>().WhereField<MkItem>(x => x.LastTradeSeq == seq).Count() != 0)
                {
                    what = "an item is keyed by its sequence";
                }

                if (what != null && Interlocked.Increment(ref found) <= 10)
                {
                    Fail($"{phase}: transfer {seq} of item {r.ItemNo} was rolled back, yet {what}");
                }
            }
        });

        if (found > 10)
        {
            Fail($"{phase}: {found:N0} of {rolledBack.Count:N0} rolled-back transfers left something behind");
        }

        Log($"{rolledBack.Count:N0} rolled-back transfers checked ({phase}) in {sw.Elapsed.TotalSeconds:F1} s");
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
            $"peaks: {dbe.MMF.PeakBackpressureDebt:N0} owed / {dbe.MMF.PeakBackpressureEpochHeld:N0} epoch-held, longest back-pressure wait " +
            $"{Stopwatch.GetElapsedTime(0, Volatile.Read(ref dbe.MMF.PeakBackpressureWaitTicks)).TotalMilliseconds:N0} ms");
        if (fileBytes > 2 * (_c.CacheMiB << 20) && m.ReadFromDiskCount <= cachePages)
        {
            Fail($"the file is over twice the cache, yet only {m.ReadFromDiskCount:N0} pages were read: the run never churned the cache");
        }
    }

    #endregion
}
