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
using Typhon.Engine.Internals;
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
// TYPHON013 suppressed, and this is the diagnostic's own documented escape hatch rather than a workaround: 512 bytes is past the 112 a ComponentValue
// carries inline, and this component is never passed to Spawn. Every fixture that holds it spawns the item without it and supplies it through
// OpenMut(...).Enable(MkItemArch.Lore, ...), which writes into the slot's own storage and never builds a ComponentValue. The size is the point of the
// component — it is what makes the database large enough for a page served from the wrong slot to be visible — so it cannot be split.
#pragma warning disable TYPHON013
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
#pragma warning restore TYPHON013

/// <summary>One committed transfer or credit grant, appended in the same transaction as the transfer or the grant itself.</summary>
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
/// die and slots are reused while the item count stays exact. One transfer in twenty does all of its writes, then rolls back. A player who cannot pay
/// is first granted credits by the item's desk — its starting credits, or the price if that is more — in a transaction and an audit entry of their
/// own, unless the desk cannot pay either: buys at full price and sells at half drain the players, and at 75M items, where nearly every pick is a buy,
/// over half of a storm was skipped for lack of credits before grants.</para>
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
/// and durability, never to arbitrate. Any drift is then an engine fault. With <c>TYPHON_MKT_ARBITRATE</c> the desks — 64 traders that nearly every
/// transfer pays or is paid by — are left unlocked: transfers race on their wallets and item counts, and every commit passes a handler that merges the
/// committed value with its own change (<see cref="MergeDeltas"/>). Conservation and the exact replay then hold only if the engine finds every such
/// conflict, hands the handler the newest committed value, and publishes what it resolved.</para>
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
/// <c>TYPHON_MKT_OWNER_AUDIT</c> (1: audit the owner index during the storm, under each player's lock; 0 skips the auditor. The checks after the
/// storm and the reopen always run), <c>TYPHON_MKT_LONG_READERS</c> (0: threads that each hold one snapshot for <c>TYPHON_MKT_LONG_READ_SECONDS</c>,
/// 20, during the storm), <c>TYPHON_MKT_CRASH</c> (0; 1 ends the process at a random point of the storm, 2 at a random page write after that point —
/// see <i>Crashes</i>), <c>TYPHON_MKT_ROLLBACK_ONE_IN</c> (20: one transfer in that many rolls back after all its writes; 0 for none),
/// <c>TYPHON_MKT_RECOVERY_MUTANT</c> (0; 1 replays the recovery verification without one of the last run's surviving transfers, so it must
/// fail — the proof that it can), <c>TYPHON_MKT_TRACE</c> (a profiler capture of the run to that file; see <see cref="TraceCapture"/> for the
/// profiler variables it needs), <c>TYPHON_MKT_ARBITRATE</c> (0; 1 leaves the desks unlocked — see <i>Coordination</i>), <c>TYPHON_MKT_QUERY_AUDIT</c>
/// (0; 1: the owner auditor also asks the query engine for the player's items, and a query auditor counts every item and audit entry at successive
/// snapshots during the storm — see <see cref="CheckPlayerQueries"/> and <see cref="QueryAuditor"/>. Like a long reader, a broad count holds one epoch
/// for its whole scan, so the cache must hold what the writers touch meanwhile: a 64 MiB cache under 4 threads times out on back-pressure),
/// <c>TYPHON_MKT_SKEW_TENTHS</c> (10: items and players picked uniformly; above, a pick is <c>N·u^(value/10)</c>, so the low numbers are hot — 30 puts
/// a third of the picks in the first 4 % of the items), <c>TYPHON_MKT_OWNED_PERMILLE</c> (0: that share of the operations picks a player, then one of
/// its items through the owner index, so sells, trades and consumes keep their share at a scale where nearly every random item is the market's).</para>
/// <para><b>Long readers.</b> While the storm writes, each long reader holds one snapshot and sweeps every trader in it, again and again: each sweep
/// finds the credits and the item counts conserved — a transfer is whole or absent — and reads every trader, and a growing sample of items, exactly as
/// the first sweep did. The writers' revision chains grow behind the snapshot (the desks', written by most transfers, the most), and are cleaned up
/// once it ends.</para>
/// <para><b>Crashes.</b> With <c>TYPHON_MKT_CRASH</c>, the storm pauses its workers for an instant at a random point, lists the transfers committed and
/// rolled back so far, makes the WAL durable through every frame published by then and saves the list (<c>crash.oracle</c>); then it ends the process
/// with <c>TerminateProcess</c> — at once, or at a random page write to come (while armed, an interceptor is set, so checkpoint writes run serially:
/// mode 2 crashes under an I/O pattern production never uses). The next run finds its predecessor in progress and verifies the recovered
/// database before its own storm, trusting nothing the crashed run kept: item ids rebuilt by a scan, every item number held by exactly one entity, the
/// state equal to the build's with every surviving audit entry replayed over it, every listed commit present and no listed rollback, conservation, both
/// indexes and the storage integrity. Running the test repeatedly with the variable set crashes and recovers one run after another.</para>
/// <para><b>Run it:</b> <c>dotnet test test/Typhon.Engine.Tests -c Release --filter "FullyQualifiedName~MarketHardeningTests"</c>, with the variables
/// set. A 50 GiB run, for instance: <c>TYPHON_MKT_ITEMS=75000000</c>, <c>TYPHON_MKT_CACHE_MIB=8192</c>, <c>TYPHON_MKT_OPERATIONS=20000000</c>.</para>
/// <para><b>Reuse.</b> Each run continues from the previous one's state and records its totals in <c>manifest.json</c>. A run that crashes or fails
/// part-way leaves the database ahead of its manifest; the next run verifies it as after a crash, without an oracle if there is none. A database that
/// fails that check needs a rebuild: <c>TYPHON_MKT_REBUILD=1</c>.</para>
/// </remarks>
[TestFixture]
[Explicit("On-demand hardening: builds a database of up to tens of GiB. Never run by CI.")]
[Category("Manual")]
[NonParallelizable]
public class MarketHardeningTests
{
    private const long KindBuy = 1, KindSell = 2, KindTrade = 3, KindConsume = 4, KindGrant = 5;
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
        public int Crash;
        public int LongReaders;
        public int LongReadSeconds;
        public int RollbackOneIn;
        public bool RecoveryMutant;
        public bool Arbitrate;
        public bool QueryAudit;
        public double Skew;
        public int OwnedPermille;

        public int TraderCount => Desks + Players;   // trader numbers 1..TraderCount

        /// <summary>
        /// The storm's discipline for its SingleVersion writes. A crash run needs <see cref="CommitDiscipline.Commit"/>: under the default TickFence a
        /// spawn's SingleVersion value is checkpoint-durable only (CM-06, DIRTY-01), so an item crafted after the last checkpoint would recover with
        /// default lore — documented behaviour, not a defect. Other runs keep TickFence, the workload every earlier run measured.
        /// </summary>
        // A staged publish failure (TYPHON_MKT_FAIL_PUBLISH_AT) is a crash too: the stopped engine closes as one (AP-03), and under TickFence its reopen
        // recovered the items crafted since the last checkpoint with default lore — the documented behaviour above, which once read as a corruption (#1271).
        public CommitDiscipline Discipline =>
            Crash != 0 || Environment.GetEnvironmentVariable("TYPHON_MKT_FAIL_PUBLISH_AT") != null
                       || Environment.GetEnvironmentVariable("TYPHON_MKT_COMMIT_DISCIPLINE") == "1"
                ? CommitDiscipline.Commit
                : CommitDiscipline.TickFence;

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
            Crash = (int)Env("CRASH", 0),
            LongReaders = (int)Env("LONG_READERS", 0),
            LongReadSeconds = (int)Env("LONG_READ_SECONDS", 20),
            RollbackOneIn = (int)Env("ROLLBACK_ONE_IN", 20),
            RecoveryMutant = Env("RECOVERY_MUTANT", 0) != 0,
            Arbitrate = Env("ARBITRATE", 0) != 0,
            QueryAudit = Env("QUERY_AUDIT", 0) != 0,
            Skew = Math.Max(1.0, Env("SKEW_TENTHS", 10) / 10.0),
            OwnedPermille = (int)Math.Clamp(Env("OWNED_PERMILLE", 0), 0, 1000),
        };

        private static long Env(string name, long fallback) =>
            long.TryParse(Environment.GetEnvironmentVariable("TYPHON_MKT_" + name), out var v) ? v : fallback;

        public override string ToString() =>
            $"items={Items:N0} players={Players:N0} desks={Desks} cache={CacheMiB} MiB build-cache={BuildCacheMiB} MiB threads={Threads} " +
            $"operations={Operations:N0} seed={Seed} crash={Crash} discipline={Discipline} long-readers={LongReaders}x{LongReadSeconds}s " +
            $"arbitrate={Arbitrate} query-audit={QueryAudit} skew={Skew} owned-permille={OwnedPermille} dir={Directory}";
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

        /// <summary>
        /// The run whose storm has started and not yet checked out: non-zero means a run crashed or failed part-way, after committing. The next run then
        /// verifies what the database holds (<see cref="VerifyRecovered"/>) before its own storm.
        /// </summary>
        public int InProgressRun { get; set; }

        /// <summary>Runs that ended in a crash (<c>TYPHON_MKT_CRASH</c>) and were verified recovered.</summary>
        public int Crashes { get; set; }

        /// <summary>Runs that failed part-way without a crash oracle and were verified recovered.</summary>
        public int Recovered { get; set; }
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
    private static string CrashOracleFile(Config c) => Path.Combine(c.Directory, "crash.oracle");

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

    /// <summary>A number in [0, <paramref name="n"/>): uniform, or skewed toward 0 by <c>TYPHON_MKT_SKEW_TENTHS</c>.</summary>
    private int Pick(Random rng, int n) => _c.Skew <= 1.0 ? rng.Next(n) : Math.Min(n - 1, (int)(n * Math.Pow(rng.NextDouble(), _c.Skew)));

    /// <summary>
    /// One of a player's items, through the owner index (<c>TYPHON_MKT_OWNED_PERMILLE</c>): a player picked as buyers are, then one of the first items the
    /// index holds under it; -1 when it holds none. Its owner is read again under the item's lock, as for any pick.
    /// </summary>
    private int PickOwned(DatabaseEngine dbe, IndexRef ownerIndex, Random rng)
    {
        var player = _c.Desks + 1 + Pick(rng, _c.Players);
        Span<int> found = stackalloc int[16];
        var n = 0;
        using var tx = dbe.CreateReadOnlyTransaction();
        using (var e = tx.EnumerateIndex<MkItem, long>(ownerIndex, player, player))
        {
            while (n < found.Length && e.MoveNext())
            {
                found[n++] = (int)e.CurrentComponent.ItemNo;
            }
        }

        return n == 0 ? -1 : found[rng.Next(n)];
    }

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

    // TYPHON_MKT_ARBITRATE: the conflicts the engine found on a desk's wallet and count, and the handler merged; the desks' entities, the only ones a
    // conflict may name.
    private long _walletMerges, _countMerges;
    private ConcurrencyConflictHandler _merge;
    private HashSet<long> _deskEntities;

    /// <summary>
    /// The handler of a storm that does not lock the desks (<c>TYPHON_MKT_ARBITRATE</c>): two transfers through one desk race, the engine finds the conflict
    /// on its wallet or count, and this merges the committed value with this commit's own change — what a game server does with a shared balance. Nothing
    /// else races: items and players stay locked, so a conflict on anything else is a failure.
    /// </summary>
    private void MergeDeltas(ref ConcurrencyConflictSolver solver)
    {
        // A player is locked by every transfer it is in: a conflict on one means a commit was handed a stale snapshot, which merging would quietly repair.
        if (!_deskEntities.Contains(solver.PrimaryKey))
        {
            Fail($"a conflict on {solver.ComponentType.Name} of entity {solver.PrimaryKey:X}, which is not a desk: only the desks are left to the engine");
            return;
        }

        if (solver.ComponentType == typeof(MkWallet))
        {
            solver.ToCommitData<MkWallet>().Credits =
                solver.CommittedData<MkWallet>().Credits + solver.CommittingData<MkWallet>().Credits - solver.ReadData<MkWallet>().Credits;
            Interlocked.Increment(ref _walletMerges);
        }
        else if (solver.ComponentType == typeof(MkTrader))
        {
            solver.ToCommitData<MkTrader>().ItemsOwned =
                solver.CommittedData<MkTrader>().ItemsOwned + solver.CommittingData<MkTrader>().ItemsOwned - solver.ReadData<MkTrader>().ItemsOwned;
            Interlocked.Increment(ref _countMerges);
        }
        else
        {
            Fail($"a conflict on {solver.ComponentType.Name} of entity {solver.PrimaryKey:X}: only the desks are left to the engine to arbitrate");
        }
    }

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

    [TearDown]
    public void ResetQuarantineSwitch()
    {
        DatabaseEngine.DisableFreeQuarantineForTest = false;
        DatabaseEngine.RecoveryBatchPointProbe = null;
        DatabaseEngine.RecoveryBatchSize = DatabaseEngine.DefaultRecoveryBatchSize;
    }

    [Test]
    public void AMarketLargerThanItsCache_StaysExactlyCoherent_ThroughAStormAndAReopen()
    {
        _c = Config.FromEnvironment();
        _merge = _c.Arbitrate ? MergeDeltas : null;

        // TYPHON_MKT_NO_QUARANTINE=1: frees reused at once, as before CK-17 — the A/B switch of its measurements, on the same binary.
        DatabaseEngine.DisableFreeQuarantineForTest = Environment.GetEnvironmentVariable("TYPHON_MKT_NO_QUARANTINE") == "1";

        // TYPHON_MKT_RECOVERY_BATCH: a crash open's batch size, for TYPHON_MKT_STOP_RECOVERY to stop inside a short window's passes.
        DatabaseEngine.RecoveryBatchSize = int.TryParse(Environment.GetEnvironmentVariable("TYPHON_MKT_RECOVERY_BATCH"), out var batch) && batch > 0
            ? batch
            : DatabaseEngine.DefaultRecoveryBatchSize;
        _errors = new ConcurrentQueue<string>();
        Log(_c.ToString());
        Directory.CreateDirectory(_c.Directory);
        var samplesFile = _c.SamplesFile is { Length: > 0 } f ? f : Path.Combine(_c.Directory, $"samples-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        using var sampler = new Sampler(samplesFile);
        _sampler = sampler;
        Log($"sampling every second to {samplesFile}");
        using var trace = TraceCapture.Start(Environment.GetEnvironmentVariable("TYPHON_MKT_TRACE"), Log);

        var manifest = LoadOrBuild();
        var traderIds = ReadIds(TraderIdsFile(_c), _c.TraderCount + 1);
        var itemIds = manifest.InProgressRun != 0 ? VerifyRecovered(manifest, traderIds) : ReadIds(ItemIdsFile(_c), _c.Items);

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
            WriteManifest(manifest);
            sampler.Phase = "storm";
            Log($"revision chunks before the storm: {DescribeRevisionChunks(dbe)}");
            var auditIds = Storm(dbe, traderIds, itemIds, runNo, firstSeq, manifest.TotalCredits, out var lastSeq, out destroyed, out rolledBack);
            Log($"revision chunks after the storm: {DescribeRevisionChunks(dbe)}");
            Log($"free quarantine (CK-17){(DatabaseEngine.DisableFreeQuarantineForTest ? " OFF" : "")}: {dbe.MMF.QuarantinedBytes / 1048576.0:F1} MiB held now, " +
                $"peak {dbe.MMF.QuarantinePeakBytes / 1048576.0:F1} MiB of a {dbe.MMF.FreeQuarantineBudgetBytes / 1048576.0:F0} MiB budget, " +
                $"{dbe.MMF.QuarantineCheckpointRequests:N0} checkpoint(s) asked, {dbe.CheckpointManager.TotalCheckpoints:N0} checkpoint(s) in all");
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
                Log($"storm settled in {sw.Elapsed.TotalSeconds:F1} s; revision chunks: {DescribeRevisionChunks(dbe)}");
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

            // An armed crash that has not fired by now does not: the run checks out.
            dbe.MMF.PageWriteInterceptor = null;
            manifest.AuditEntries += runAudits;
            manifest.LastSeq = lastSeq;
            manifest.Runs = runNo;
            manifest.InProgressRun = 0;
            WriteIds(ItemIdsFile(_c), itemIds);   // consumed items are new entities: the next run reads them by their new ids
            WriteManifest(manifest);
            File.Delete(CrashOracleFile(_c));
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
        Log($"run {manifest.Runs} coherent: {runAudits:N0} transfers and grants, {manifest.AuditEntries:N0} audit entries in total");
    }

    /// <summary>
    /// A profiler capture of the whole run to <c>TYPHON_MKT_TRACE</c>, with what the enabled categories emit. The engine starts an exporter only for a
    /// <see cref="TyphonRuntime"/>, which this test does not create, so it attaches one itself.
    /// </summary>
    /// <remarks>
    /// The test project's <c>typhon.telemetry.json</c> turns the profiler on. Every page-cache disk read and write with its completion, every WAL write and
    /// every checkpoint, as verified: <c>TYPHON__PROFILER__STORAGE__PAGECACHE__ENABLED=true</c>, <c>TYPHON__PROFILER__DURABILITY__ENABLED=true</c> and
    /// <c>TYPHON__PROFILER__STORAGE__PAGECACHE__COMPLETIONTHRESHOLDMS=0</c> — without it only completions slower than 1 ms are recorded. The trace is
    /// finalised when the test ends normally: a run a crash mode ends with <c>TerminateProcess</c> leaves it unfinished.
    /// </remarks>
    private sealed class TraceCapture : IDisposable
    {
        private readonly ResourceRegistry _registry;
        private readonly FileExporter _exporter;
        private readonly Action<string> _log;

        private TraceCapture(string path, Action<string> log)
        {
            _log = log;
            _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "market-trace" });
            try
            {
                _exporter = new FileExporter(path, _registry.Profiler);
                TyphonProfiler.AttachExporter(_exporter);
                TyphonProfiler.Start(_registry.Profiler, new ProfilerSessionMetadata(
                    systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: 60.0f, startTimestamp: Stopwatch.GetTimestamp(),
                    stopwatchFrequency: Stopwatch.Frequency, startedUtc: DateTime.UtcNow, samplingSessionStartQpc: 0));
            }
            catch
            {
                if (_exporter != null)
                {
                    TyphonProfiler.DetachExporter(_exporter);
                }

                _registry.Dispose();
                throw;
            }

            log($"profiler capture to {path} (QPC {Stopwatch.GetTimestamp()} at {DateTime.Now:O})");
        }

        public static TraceCapture Start(string path, Action<string> log)
        {
            if (path is not { Length: > 0 })
            {
                return null;
            }

            if (!TelemetryConfig.ProfilerActive)
            {
                log($"no profiler capture to {path}: the profiler is off (Typhon:Profiler:Enabled)");
                return null;
            }

            return new TraceCapture(path, log);
        }

        public void Dispose()
        {
            TyphonProfiler.Stop();
            TyphonProfiler.DetachExporter(_exporter);
            _log($"profiler capture closed: {TyphonProfiler.TotalDroppedEvents:N0} event(s) dropped by the producer rings, " +
                 $"{TyphonProfiler.TotalDroppedExporterBatches:N0} batch(es) by the exporter");
            _registry.Dispose();
        }
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
            "t_s,phase,slots_used,debt,acw,slot_ref,epoch_held,marked,epoch_only,unevictable,pages_read,pages_written,bp_rounds,bp_peak_debt," +
            "bp_peak_epoch_held," +
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
                    var (debt, acw, slotRef, epochHeld, marked, epochOnly, unevictable, total) = mmf.CountUnevictablePages();
                    var m = mmf.GetMetrics();
                    var ck = dbe.CheckpointManager;
                    var waitMs = Stopwatch.GetElapsedTime(0, Volatile.Read(ref mmf.PeakBackpressureWaitTicks)).TotalMilliseconds;
                    row.Append($",{total},{debt},{acw},{slotRef},{epochHeld},{marked},{epochOnly},{unevictable}");
                    row.Append($",{m.ReadFromDiskCount},{m.PageWrittenToDiskCount}");
                    row.Append($",{m.BackpressureWaitCount},{mmf.PeakBackpressureDebt},{mmf.PeakBackpressureEpochHeld},{waitMs:F0}");
                    row.Append($",{ck?.TotalCheckpoints},{ck?.TotalPressureCheckpoints},{ck?.TotalPagesWritten},{_operations?.Invoke()}");
                }
                else
                {
                    row.Append(",,,,,,,,,,,,,,,,,,");
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
                Log($"reusing the database built for this configuration ({existing.Runs} run(s) so far, {existing.Crashes} crash(es) recovered)");
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

        File.Delete(CrashOracleFile(_c));   // an oracle names a run of the database it was written for, never of a rebuilt one

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
                    var credits = InitialCredits(t);
                    var owned = InitialItemsOwned(t);
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
        WriteManifest(manifest);
        return manifest;
    }

    /// <summary>Writes the manifest to a temporary file, then renames it over the manifest: a crash part-way leaves the old one whole.</summary>
    private void WriteManifest(Manifest manifest)
    {
        var path = ManifestFile(_c);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest));
        File.Move(path + ".tmp", path, overwrite: true);
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

    #region Crash and recovery

    /// <summary>
    /// Ends the process on the spot, as a crash does: <c>TerminateProcess</c> — no dispose, no finalizer, no final checkpoint, no clean-close marker. What
    /// the operating system already holds of the data file and the WAL survives; the page cache and the WAL's unwritten buffer are lost. The reason goes
    /// to <c>crash.log</c> first, since the test's own output may not reach the console.
    /// </summary>
    /// <remarks>
    /// Never over a failure: with a problem queued the run must fail as itself, not come back as a crash for the next run to call recovered. The crash is
    /// then refused, and the problems go to <c>crash.log</c>.
    /// </remarks>
    private void Crash(string why)
    {
        var log = Path.Combine(_c.Directory, "crash.log");
        if (!_errors.IsEmpty)
        {
            var first = _errors.TryPeek(out var problem) ? problem : "?";
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} NOT crashed {why}: {_errors.Count} problem(s) queued, first: {first}" +
                                    Environment.NewLine);
            TryLog(() => $"crash refused: {_errors.Count} problem(s) queued");
            return;
        }

        File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} crashed {why}{Environment.NewLine}");
        TryLog(() => $"CRASHING {why}");
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>The crash's oracle: the run, the sequences committed before the WAL was made durable, and those rolled back by then. Flushed to disk.</summary>
    private void WriteCrashOracle(int runNo, List<long> committed, List<long> rolledBack)
    {
        var path = CrashOracleFile(_c);
        var temporary = path + ".tmp";
        using (var f = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            using var w = new BinaryWriter(f, Encoding.UTF8, leaveOpen: true);
            w.Write(runNo);
            w.Write(committed.Count);
            w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(committed)));
            w.Write(rolledBack.Count);
            w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(rolledBack)));
            w.Flush();
            f.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The oracle of the crash of <paramref name="runNo"/>, or null: the run failed rather than crashed, or crashed before saving it.</summary>
    private (long[] Committed, long[] RolledBack)? ReadCrashOracle(int runNo)
    {
        var path = CrashOracleFile(_c);
        if (!File.Exists(path))
        {
            return null;
        }

        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadInt32() != runNo)
        {
            return null;
        }

        var committed = new long[r.ReadInt32()];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(committed.AsSpan()));
        var rolledBack = new long[r.ReadInt32()];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(rolledBack.AsSpan()));
        return (committed, rolledBack);
    }

    private long InitialCredits(int trader) => trader <= _c.Desks ? _c.DeskCredits : _c.PlayerCredits;

    /// <summary>A desk starts with the items whose number falls on it (<see cref="DeskOf"/>); a player with none.</summary>
    private long InitialItemsOwned(int trader) => trader <= _c.Desks ? _c.Items / _c.Desks + (trader - 1 < _c.Items % _c.Desks ? 1 : 0) : 0;

    /// <summary>
    /// The state the build leaves — the build itself spawns from <see cref="InitialCredits"/> and <see cref="InitialItemsOwned"/>: every item at the
    /// market at its base price, untraded; every trader with its starting credits and desk items.
    /// </summary>
    private State InitialState()
    {
        var s = new State(_c);
        for (var t = 1; t <= _c.TraderCount; t++)
        {
            s.Credits[t] = InitialCredits(t);
            s.ItemsOwned[t] = InitialItemsOwned(t);
        }

        for (var i = 0; i < _c.Items; i++)
        {
            s.Price[i] = PriceOf(i);
            s.LastTradeSeq[i] = -(i + 1L);
        }

        return s;
    }

    private void StopRecoveries(string spec)
    {
        if (string.IsNullOrEmpty(spec))
        {
            return;
        }

        foreach (var entry in spec.Split(','))
        {
            var parts = entry.Split(':');
            var pass = Enum.Parse<DatabaseEngine.RecoveryPass>(parts[0]);
            var at = int.Parse(parts[1]);
            var seen = 0;
            DatabaseEngine.RecoveryBatchPointProbe = (p, _) =>
            {
                if (p == pass && ++seen == at)
                {
                    throw new InvalidOperationException($"recovery stopped inside {pass}, at its batch point {at}");
                }
            };
            try
            {
                using var dbe = Open(_c.CacheMiB, OpenVerification.Spine);
                Log($"the open meant to stop inside {pass} at batch point {at} completed: {seen} batch point(s) of the pass");
            }
            catch (Exception e)
            {
                Log($"recovery stopped: {e.GetBaseException().Message}");
            }
            finally
            {
                DatabaseEngine.RecoveryBatchPointProbe = null;
            }

            ScanOffline("after the stop");
        }
    }

    // What a stopped recovery left in the data file, read offline: the WAL findings in full (a stopped open's writer once broke the log, LOG-08).
    private void ScanOffline(string when)
    {
        using var source = new OfflineBundlePageSource(DatabaseFile(_c));
        var report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Deep });
        var codes = new Dictionary<string, int>();
        foreach (var f in report.Findings)
        {
            codes[f.Code] = codes.GetValueOrDefault(f.Code) + 1;
        }

        Log($"offline scan {when}: {report.Verdict}, {report.Findings.Count} finding(s): {string.Join(", ", codes.Select(kv => kv.Key + " x" + kv.Value))}");
        foreach (var f in report.Findings)
        {
            if (f.Code.StartsWith("CHK-WAL"))
            {
                Log($"  {f.Code} @ {f.Locus}: {f.Summary} {f.Detail}");
            }
        }

    }

    /// <summary>
    /// The run before this one did not check out — it crashed (<c>TYPHON_MKT_CRASH</c>) or failed — so the database holds whatever recovery made of it.
    /// Checked with nothing taken from that run but its crash oracle:
    /// <list type="bullet">
    /// <item>the item ids, rebuilt by a scan: every item number held by exactly one live entity — a consume's destroy and craft are atomic;</item>
    /// <item>the audit: every entry of the runs before it, none of a later run, no sequence twice;</item>
    /// <item>the state against the build's initial state with every audit entry replayed over it, in sequence order — each transfer whole or absent,
    /// none applied without its audit entry, none missing below one that survived (the replay checks each item moves from where it was);</item>
    /// <item>after a crash, the oracle: every transfer committed before the WAL was made durable is there, none rolled back is;</item>
    /// <item>conservation, both indexes, the storage integrity check.</item>
    /// </list>
    /// The manifest and the id files then take the recovered run as done, and this run goes on with its own storm.
    /// </summary>
    private ulong[] VerifyRecovered(Manifest manifest, ulong[] traderIds)
    {
        var run = manifest.InProgressRun;
        var oracle = ReadCrashOracle(run);
        Log(oracle == null
            ? $"run {run} did not check out and left no crash oracle: verifying the recovered database without one"
            : $"run {run} crashed: verifying the recovered database against its oracle ({oracle.Value.Committed.Length:N0} committed, " +
              $"{oracle.Value.RolledBack.Length:N0} rolled back)");
        _sampler.Phase = "recovery";

        // TYPHON_MKT_STOP_RECOVERY=Pass:N[,Pass:N…]: before the verified open, one open per entry stopped at its pass's Nth batch point, as a crash during
        // the recovery would stop it — the next open replays the window over its partial output (AP-12, plan 06 step 2.4).
        if (Environment.GetEnvironmentVariable("TYPHON_MKT_STOP_RECOVERY") is { Length: > 0 })
        {
            ScanOffline("before the stops");
        }

        StopRecoveries(Environment.GetEnvironmentVariable("TYPHON_MKT_STOP_RECOVERY"));
        var sw = Stopwatch.StartNew();
        var itemIds = new ulong[_c.Items];
        MkAudit[] entries;
        State actual;
        using (var dbe = Open(_c.CacheMiB, OpenVerification.Spine))
        {
            using var tracked = _sampler.Track(dbe, "recovery check");
            var m = dbe.MMF.GetMetrics();
            Log($"recovered in {sw.Elapsed.TotalSeconds:F1} s: {m.ReadFromDiskCount:N0} pages read, {m.PageWrittenToDiskCount:N0} written; "
                + $"{dbe.RecoveryBatchPoints:N0} batch points, peak {dbe.PeakRecoveryEpochHeldPages:N0} pages epoch-held, "
                + $"{dbe.CheckpointManager.TotalFlushOnlyCycles:N0} flush-only cycle(s)");

            sw.Restart();
            actual = ScanState(dbe, traderIds, itemIds);
            Log($"state scanned in {sw.Elapsed.TotalSeconds:F1} s");

            sw.Restart();
            entries = ReadAudits(dbe, ScanIds<MkAuditArch>(dbe));
            long older = 0, fromRun = 0;
            var survivors = new HashSet<long>();
            foreach (ref readonly var a in entries.AsSpan())
            {
                if (a.RunNo < run)
                {
                    older++;
                    if (a.Seq > manifest.LastSeq)
                    {
                        Fail($"recovery: audit {a.Seq} of run {a.RunNo} lies past the last sequence of the runs that checked out, {manifest.LastSeq}");
                    }
                }
                else if (a.RunNo == run)
                {
                    fromRun++;
                    survivors.Add(a.Seq);
                    if (a.Seq <= manifest.LastSeq)
                    {
                        Fail($"recovery: audit {a.Seq} of run {run} reuses a sequence of an earlier run");
                    }
                }
                else
                {
                    Fail($"recovery: audit {a.Seq} names run {a.RunNo}, after the run that crashed ({run})");
                }
            }

            if (older != manifest.AuditEntries)
            {
                Fail($"recovery: {older:N0} audit entries of the runs that checked out, expected {manifest.AuditEntries:N0}");
            }

            Log($"audit read in {sw.Elapsed.TotalSeconds:F1} s: {entries.Length:N0} entries, {fromRun:N0} of them from run {run}");

            if (oracle is { } o)
            {
                var (committed, rolledBack) = o;
                long lost = 0, back = 0;
                foreach (var seq in committed)
                {
                    if (!survivors.Contains(seq) && lost++ < 10)
                    {
                        Fail($"recovery: transfer {seq} committed before the WAL was made durable, and is gone");
                    }
                }

                foreach (var seq in rolledBack)
                {
                    if (survivors.Contains(seq) && back++ < 10)
                    {
                        Fail($"recovery: transfer {seq} was rolled back, and its audit entry survived");
                    }
                }

                if (lost > 10 || back > 10)
                {
                    Fail($"recovery: {lost:N0} durable transfers lost, {back:N0} rolled-back ones present");
                }

                Log($"oracle: {committed.Length - lost:N0} of {committed.Length:N0} durable transfers and grants present, {back:N0} of " +
                    $"{rolledBack.Length:N0} rolled back present; {fromRun:N0} of the run's audit entries survived in all");
            }

            var replayed = entries;
            if (_c.RecoveryMutant && fromRun > 0)
            {
                // TYPHON_MKT_RECOVERY_MUTANT: the replay — and only the replay — misses one of the run's surviving transfers, so the state comparison
                // must go red on its own (the audit count still sees every entry): the proof it can.
                var dropped = Array.FindLastIndex(entries, a => a.RunNo == run);
                Log($"RECOVERY MUTANT: replaying without audit {entries[dropped].Seq}; the state comparison must fail");
                replayed = [.. entries[..dropped], .. entries[(dropped + 1)..]];
            }

            var expected = InitialState();
            var traded = Apply(expected, replayed);
            Log($"replayed {entries.Length:N0} audit entries since the build over {traded:N0} items");
            CompareStates(expected, actual, "replayed audit vs recovered database");
            CheckConservation(actual, manifest, "after recovery");
            CheckAuditCount(dbe, entries.Length, "after recovery");
            var firstSeq = manifest.LastSeq + 1;
            var tradedInRun = 0;
            for (var i = 0; i < _c.Items; i++)
            {
                tradedInRun += actual.LastTradeSeq[i] >= firstSeq ? 1 : 0;
            }

            CheckIndex(dbe, actual, itemIds, firstSeq, tradedInRun);
            CheckOwnerIndex(dbe, actual, itemIds, "after recovery");
            CheckIntegrity(dbe, "after recovery");
            AssertNoErrors("after recovery");
        }

        manifest.AuditEntries = entries.Length;
        foreach (ref readonly var a in entries.AsSpan())
        {
            manifest.LastSeq = Math.Max(manifest.LastSeq, a.Seq);
        }
        manifest.Runs = run;
        manifest.InProgressRun = 0;
        manifest.Crashes += oracle != null ? 1 : 0;
        manifest.Recovered += oracle == null ? 1 : 0;
        WriteIds(ItemIdsFile(_c), itemIds);
        WriteManifest(manifest);
        File.Delete(CrashOracleFile(_c));
        Log($"run {run} recovered coherent: {manifest.AuditEntries:N0} audit entries in total");
        return itemIds;
    }

    /// <summary>Every live entity of an archetype, by a scan — for the checks after a crash, which cannot trust a list the crashed run kept.</summary>
    private static List<ulong> ScanIds<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        var ids = new List<ulong>();
        foreach (var id in tx.Query<TArch>().Execute())
        {
            ids.Add(id.RawValue);
        }

        return ids;
    }

    /// <summary>
    /// The state read without the item id file: the items found by a scan, each placed by the number it holds. Fills <paramref name="itemIds"/>; a number
    /// held twice, or by none, is a failure.
    /// </summary>
    private State ScanState(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds)
    {
        var s = new State(_c);
        ReadTraders(dbe, traderIds, s);
        var scanned = ScanIds<MkItemArch>(dbe);
        if (scanned.Count != _c.Items)
        {
            Fail($"recovery: the scan finds {scanned.Count:N0} items, expected {_c.Items:N0}");
        }

        var batches = (scanned.Count + ReadBatch - 1) / ReadBatch;
        long twice = 0;
        ReadInParallel(dbe, batches, b =>
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var end = Math.Min(scanned.Count, (b + 1) * ReadBatch);
            for (var k = b * ReadBatch; k < end; k++)
            {
                var e = tx.Open(EntityId.FromRawValue(scanned[k]));
                var item = e.Read(MkItemArch.Item);
                var i = item.ItemNo;
                if (i < 0 || i >= _c.Items)
                {
                    Fail($"recovery: entity {EntityId.FromRawValue(scanned[k])} holds item number {i}");
                    continue;
                }

                var other = Interlocked.CompareExchange(ref itemIds[i], scanned[k], 0);
                if (other != 0)
                {
                    if (Interlocked.Increment(ref twice) <= 10)
                    {
                        Fail($"recovery: item {i} is held by two live entities, {EntityId.FromRawValue(other)} and {EntityId.FromRawValue(scanned[k])}");
                    }

                    continue;
                }

                s.Owner[i] = (int)item.OwnerNo;
                s.Price[i] = item.Price;
                s.TradeCount[i] = (int)item.TradeCount;
                s.LastTradeSeq[i] = item.LastTradeSeq;
                if (Mix((ulong)i ^ 0x10EEUL) % 1000 < (ulong)_c.LoreSamplePermille && !LoreMatches(i, e.Read(MkItemArch.Lore)))
                {
                    Fail($"recovery: item {i}: its lore does not match it");
                }
            }
        });

        var missing = 0;
        for (var i = 0; i < _c.Items; i++)
        {
            if (itemIds[i] == 0 && missing++ < 10)
            {
                Fail($"recovery: no live entity holds item {i}");
            }
        }

        return s;
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

    private void ReadTraders(DatabaseEngine dbe, ulong[] traderIds, State s)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
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

    private State ReadState(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds, int lorePermille)
    {
        var s = new State(_c);
        ReadTraders(dbe, traderIds, s);
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

    /// <summary>
    /// The revision chunks each versioned component holds: what long readers make the writers' chains grow by, and what the cleanup gives back.
    /// </summary>
    private static string DescribeRevisionChunks(DatabaseEngine dbe) =>
        $"trader {dbe.GetComponentTable<MkTrader>().CompRevTableSegment.AllocatedChunkCount:N0}, " +
        $"wallet {dbe.GetComponentTable<MkWallet>().CompRevTableSegment.AllocatedChunkCount:N0}, " +
        $"item {dbe.GetComponentTable<MkItem>().CompRevTableSegment.AllocatedChunkCount:N0}, " +
        $"audit {dbe.GetComponentTable<MkAudit>().CompRevTableSegment.AllocatedChunkCount:N0}";

    /// <summary>
    /// Grants <paramref name="player"/> credits from <paramref name="desk"/> when it holds fewer than <paramref name="amount"/>: its starting credits, or the
    /// amount if that is more, in one committed transaction with an audit entry of <see cref="KindGrant"/>. The caller holds both traders' locks — the
    /// player's only under TYPHON_MKT_ARBITRATE, whose merge handler resolves a race on the desk. A desk
    /// that cannot pay grants nothing, and the transfer is skipped as before. False only when the commit failed. A grant's commit counts toward
    /// <c>TYPHON_MKT_FAIL_PUBLISH_AT</c> like any other.
    /// </summary>
    private bool Grant(DatabaseEngine dbe, ulong[] traderIds, long player, long desk, long amount, int runNo, ref long seq, List<ulong> mine,
        List<long> myCommitted, ref long grants)
    {
        // Checked through a read handle, like the item's owner in the storm: the caller holds the player's lock, so the balance stays what it reads, and a
        // write transaction — a unit-of-work slot and its writes — is paid only for an actual grant.
        using (var r = dbe.CreateReadOnlyTransaction())
        {
            if (r.Open(EntityId.FromRawValue(traderIds[player])).Read(MkTraderArch.Wallet).Credits >= amount)
            {
                return true;
            }
        }

        using var tx = dbe.CreateQuickTransaction(discipline: _c.Discipline);

        var grant = Math.Max(amount, _c.PlayerCredits);
        var deskEntity = tx.OpenMut(EntityId.FromRawValue(traderIds[desk]));
        var deskWallet = deskEntity.Read(MkTraderArch.Wallet);
        if (deskWallet.Credits < grant)
        {
            return true;
        }

        var s = Interlocked.Increment(ref seq);
        deskWallet.Credits -= grant;
        deskEntity.Set(MkTraderArch.Wallet, deskWallet);
        var playerEntity = tx.OpenMut(EntityId.FromRawValue(traderIds[player]));
        var playerWallet = playerEntity.Read(MkTraderArch.Wallet);
        playerWallet.Credits += grant;
        playerEntity.Set(MkTraderArch.Wallet, playerWallet);
        var auditId = tx.Spawn<MkAuditArch>(MkAuditArch.Audit.Set(new MkAudit
        {
            Seq = s, RunNo = runNo, Kind = KindGrant, ItemNo = -1, Payer = desk, Payee = player, Amount = grant,
        }));
        bool committed;
        try
        {
            committed = tx.Commit(_merge);
        }
        catch (CommitPublishIncompleteException)
        {
            Log($"publish incomplete: grant {s} from desk {desk} to player {player}");
            throw;
        }

        if (!committed)
        {
            Fail($"grant {s} to player {player}: commit returned false");
            return false;
        }

        mine.Add(auditId.RawValue);
        myCommitted?.Add(s);
        Interlocked.Increment(ref grants);
        return true;
    }

    /// <remarks>
    /// <paramref name="itemIds"/> follows the storm: a consume replaces an item's entity, and its slot in the table is rewritten under the item's lock,
    /// which is also the only place it is read during the storm.
    /// </remarks>
    private List<ulong> Storm(DatabaseEngine dbe, ulong[] traderIds, ulong[] itemIds, int runNo, long firstSeq, long totalCredits, out long lastSeq,
        out List<DestroyedItem> destroyed, out List<RolledBack> rolledBack)
    {
        // TYPHON_MKT_FAIL_PUBLISH_AT=N: the Nth commit's publish throws midway — its Versioned components published, its SingleVersion writes not — and
        // the engine stops (AP-03). The next open recovers it; the market checks that recovery.
        // TYPHON_MKT_KILL_AFTER_PUBLISH_FAIL_MS=d, with it: the process ends d ms after that failure, as a power cut after the stop — no close, nothing more
        // written (#1271). The next open recovers a data file the stopped engine was still writing.
        var failPublishAt = long.TryParse(Environment.GetEnvironmentVariable("TYPHON_MKT_FAIL_PUBLISH_AT"), out var at) ? at : 0;
        var killAfterMs = int.TryParse(Environment.GetEnvironmentVariable("TYPHON_MKT_KILL_AFTER_PUBLISH_FAIL_MS"), out var kms) ? kms : -1;
        if (failPublishAt > 0)
        {
            var publishes = 0L;
            dbe.CommitBeforeStagedPublishProbe = () =>
            {
                if (Interlocked.Increment(ref publishes) == failPublishAt)
                {
                    throw new InvalidOperationException("staged (TYPHON_MKT_FAIL_PUBLISH_AT): a publish fails midway");
                }
            };
        }

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
        _walletMerges = _countMerges = 0;
        _deskEntities = [];
        for (var d = 1; d <= _c.Desks; d++)
        {
            _deskEntities.Add((long)traderIds[d]);
        }

        long started = 0, done = 0, skipped = 0, buys = 0, sells = 0, trades = 0, consumes = 0, rollbacks = 0, ownerAudits = 0, grants = 0;
        var stop = 0;
        var stormOver = 0;
        var censusTaken = 0;
        var audits = new List<ulong>[_c.Threads];
        var destroyedBy = new List<DestroyedItem>[_c.Threads];
        var rolledBackBy = new List<RolledBack>[_c.Threads];
        var committedBy = new List<long>[_c.Threads];   // the sequences each worker committed, for the crash oracle
        var threads = new Thread[_c.Threads];
        _sampler.CountOperations(() => Interlocked.Read(ref done));
        var pickIndex = _c.OwnedPermille > 0 ? dbe.GetIndexRef<MkItem, long>(x => x.OwnerNo) : default;
        long ownedPicks = 0;

        // The crasher's pause: workers park at the top of their loop, outside every lock, until it has listed what they committed.
        var pause = 0;
        var parked = 0;
        var exited = 0;
        using var resume = new ManualResetEventSlim(false);

        for (var w = 0; w < _c.Threads; w++)
        {
            var worker = w;
            audits[w] = new List<ulong>();
            destroyedBy[w] = new List<DestroyedItem>();
            rolledBackBy[w] = new List<RolledBack>();
            committedBy[w] = new List<long>();
            threads[w] = new Thread(() =>
            {
                var rng = new Random(_c.Seed * 7919 + runNo * 104729 + worker);
                var mine = audits[worker];
                var myDestroyed = destroyedBy[worker];
                var myRolledBack = rolledBackBy[worker];
                var myCommitted = _c.Crash != 0 ? committedBy[worker] : null;
                var stripes = new int[5];
                try
                {
                    while (Volatile.Read(ref stop) == 0 && Interlocked.Increment(ref started) <= _c.Operations)
                    {
                        if (Volatile.Read(ref pause) != 0)
                        {
                            Interlocked.Increment(ref parked);
                            resume.Wait();
                        }

                        var owned = _c.OwnedPermille > 0 && rng.Next(1000) < _c.OwnedPermille ? PickOwned(dbe, pickIndex, rng) : -1;
                        var itemNo = owned >= 0 ? owned : Pick(rng, _c.Items);
                        if (owned >= 0)
                        {
                            Interlocked.Increment(ref ownedPicks);
                        }
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
                                to = _c.Desks + 1 + Pick(rng, _c.Players);
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
                                    to = _c.Desks + 1 + Pick(rng, _c.Players);
                                } while (to == owner);

                                payer = to;
                                payee = owner;
                                newPrice = Math.Max(1, price * (80 + rng.Next(41)) / 100);
                                amount = newPrice;
                            }

                            var fromSlot = CountSlot(_c, owner, itemNo);
                            var toSlot = CountSlot(_c, to, itemNo);

                            // The traders involved, and the item's desk, which may grant the payer credits: locked in stripe order (an item lock is always
                            // taken first and alone: no cycle). Under TYPHON_MKT_ARBITRATE the desks are not locked: the engine arbitrates their races.
                            var n = 0;
                            foreach (var t in new[] { payer, payee, fromSlot, toSlot, desk })
                            {
                                if (_c.Arbitrate && t <= _c.Desks)
                                {
                                    continue;
                                }

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
                                // A player who cannot pay is first granted credits by the item's desk, in a transaction of its own with an audit entry of
                                // its own: a transfer between two traders, so credits stay conserved and the replay reproduces it. Without it, players ran
                                // dry at scale — buys at full price, sells back at half — and over half of a 75M-item storm was skipped, doing nothing.
                                if (payer > _c.Desks && !Grant(dbe, traderIds, payer, desk, amount, runNo, ref seq, mine, myCommitted, ref grants))
                                {
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                using var tx = dbe.CreateQuickTransaction(discipline: _c.Discipline);

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
                                if (_c.RollbackOneIn > 0 && rng.Next(_c.RollbackOneIn) == 0)
                                {
                                    tx.Rollback();
                                    myRolledBack.Add(new RolledBack(s, auditId.RawValue, kind == KindConsume ? newItemId.RawValue : 0, itemNo));
                                    Interlocked.Increment(ref rollbacks);
                                    Interlocked.Increment(ref done);
                                    continue;
                                }

                                bool committed;
                                try
                                {
                                    committed = tx.Commit(_merge);
                                }
                                catch (CommitPublishIncompleteException)
                                {
                                    // Whose publish stopped the engine: the reopen's findings are read against it (#1271).
                                    Log($"publish incomplete: worker {worker}, kind {kind}, item {itemNo}, entity {itemId.RawValue:X}"
                                        + (kind == KindConsume ? $", crafted entity {newItemId.RawValue:X}" : ""));
                                    throw;
                                }

                                if (!committed)
                                {
                                    Fail($"item {itemNo}: commit returned false");
                                    Volatile.Write(ref stop, 1);
                                    break;
                                }

                                mine.Add(auditId.RawValue);
                                myCommitted?.Add(s);
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
                    Fail($"worker {worker}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}"
                         + (e.InnerException == null ? "" : $"\n  inner: {e.InnerException.GetType().Name}: {e.InnerException.Message}\n{e.InnerException.StackTrace}"));
                    Volatile.Write(ref stop, 1);
                }
                finally
                {
                    Interlocked.Increment(ref exited);
                }
            }) { IsBackground = true, Name = $"market-{w}" };
        }

        // The crash (TYPHON_MKT_CRASH), at a random point of the storm. The workers pause for an instant, at the top of their loop, while it lists the
        // transfers committed and rolled back so far; it resumes them, makes the WAL durable through every frame published by then — each listed commit
        // published its frame before the pause — and saves the list. Then it ends the process: at once (1), or at a random page write to come, nearly
        // always a checkpoint's (2). The next run verifies what recovery made of it against the list.
        Thread crasher = null;
        if (_c.Crash != 0)
        {
            var crashRng = new Random(_c.Seed * 131 + runNo);
            var crashAt = (long)(_c.Operations * (0.1 + 0.8 * crashRng.NextDouble()));
            crasher = new Thread(() =>
            {
                try
                {
                    while (Interlocked.Read(ref done) < crashAt)
                    {
                        if (Volatile.Read(ref stop) != 0)
                        {
                            return;
                        }

                        Thread.Sleep(1);
                    }

                    Volatile.Write(ref pause, 1);
                    while (Volatile.Read(ref parked) + Volatile.Read(ref exited) < _c.Threads)
                    {
                        if (Volatile.Read(ref stop) != 0)
                        {
                            // A worker failed, or is stuck inside the engine: let the parked ones go, and crash nothing.
                            Volatile.Write(ref pause, 0);
                            resume.Set();
                            return;
                        }

                        Thread.Sleep(1);
                    }

                    var committed = new List<long>();
                    foreach (var list in committedBy)
                    {
                        committed.AddRange(list);
                    }

                    var rolled = new List<long>();
                    foreach (var list in rolledBackBy)
                    {
                        foreach (var r in list)
                        {
                            rolled.Add(r.Seq);
                        }
                    }

                    Volatile.Write(ref pause, 0);
                    resume.Set();

                    var lsn = dbe.WalManager.LastPublishedLsn;
                    dbe.WalManager.RequestFlush();
                    var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(60));
                    dbe.WalManager.WaitForDurable(lsn, ref ctx);
                    if (dbe.WalManager.DurableLsn < lsn)
                    {
                        Fail($"crash: the WAL did not become durable through LSN {lsn} within 60 s (durable: {dbe.WalManager.DurableLsn})");
                        Volatile.Write(ref stop, 1);
                        return;
                    }

                    if (!_errors.IsEmpty)
                    {
                        // A run already failing is reported as itself: no oracle, so the next run does not count it as a crash.
                        return;
                    }

                    WriteCrashOracle(runNo, committed, rolled);
                    var at = $"run {runNo}, {Interlocked.Read(ref done):N0} operations, {committed.Count:N0} transfers committed and {rolled.Count:N0} " +
                             $"rolled back before the WAL was made durable through LSN {lsn}";
                    if (_c.Crash == 1)
                    {
                        Crash(at);
                        return;
                    }

                    // Armed for the rest of the storm only: Storm disarms it when its workers are done, and Crash refuses over a failure.
                    var writesLeft = (long)crashRng.Next(1, 4096);
                    Log($"crash armed: at the {writesLeft:N0}th page write from now ({at})");
                    dbe.MMF.PageWriteInterceptor = page =>
                    {
                        if (Interlocked.Decrement(ref writesLeft) == 0)
                        {
                            try
                            {
                                Crash($"at a write of file page {page} ({at})");
                            }
                            catch (Exception)
                            {
                                // A failed crash.log write must not become a checkpoint failure; the kill is what matters, and it did not happen.
                            }
                        }
                    };
                }
                catch (Exception e)
                {
                    Fail($"crasher: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    Volatile.Write(ref stop, 1);
                    Volatile.Write(ref pause, 0);
                    resume.Set();
                }
            }) { IsBackground = true, Name = "market-crasher" };
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
                        CheckPlayerItems(dbe, ownerIndex, traderIds, itemIds, player, "during the storm", (Interlocked.Read(ref ownerAudits) & 15) == 0);
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

        // Long readers (TYPHON_MKT_LONG_READERS): each holds one snapshot for LongReadSeconds while the storm writes, and sweeps the traders over and over
        // in it. In a snapshot every transfer is whole or absent, so each sweep finds the credits and the item counts conserved; and a snapshot never
        // changes, so every sweep reads each trader — and a sample of items, the desks' revision chains growing behind it — exactly as the first did.
        long longReads = 0, longSweeps = 0;
        var longReaders = new Thread[_c.LongReaders];
        for (var r = 0; r < _c.LongReaders; r++)
        {
            var readerNo = r;
            longReaders[r] = new Thread(() =>
            {
                var rng = new Random(_c.Seed * 977 + runNo * 31 + readerNo);
                var credits = new long[_c.TraderCount + 1];
                var owned = new long[_c.TraderCount + 1];
                var seen = new Dictionary<ulong, MkItem>();
                var sample = new List<ulong>();
                try
                {
                    while (Volatile.Read(ref stormOver) == 0 && Volatile.Read(ref stop) == 0)
                    {
                        seen.Clear();
                        sample.Clear();
                        var held = Stopwatch.StartNew();
                        var sweeps = 0;
                        using (var tx = dbe.CreateReadOnlyTransaction())
                        {
                            do
                            {
                                long creditSum = 0, ownedSum = 0, changed = 0;
                                for (var t = 1; t <= _c.TraderCount; t++)
                                {
                                    var e = tx.Open(EntityId.FromRawValue(traderIds[t]));
                                    var c = e.Read(MkTraderArch.Wallet).Credits;
                                    var o = e.Read(MkTraderArch.Trader).ItemsOwned;
                                    if (sweeps == 0)
                                    {
                                        credits[t] = c;
                                        owned[t] = o;
                                    }
                                    else if ((c != credits[t] || o != owned[t]) && changed++ < 3)
                                    {
                                        Fail($"long reader {readerNo}: trader {t} read {credits[t]} credits / {owned[t]} items, then {c} / {o} " +
                                             $"in sweep {sweeps} " +
                                             $"of the same snapshot, {held.Elapsed.TotalSeconds:F1} s in");
                                    }

                                    creditSum += c;
                                    ownedSum += o;
                                }

                                if (creditSum != totalCredits || ownedSum != _c.Items)
                                {
                                    Fail($"long reader {readerNo}: sweep {sweeps} of one snapshot finds {creditSum:N0} credits " +
                                         $"(expected {totalCredits:N0}) and " +
                                         $"{ownedSum:N0} items counted (expected {_c.Items:N0}), {held.Elapsed.TotalSeconds:F1} s in");
                                }

                                // A new sample each sweep, kept to 4,096 items; every item seen before is read again. An item crafted after the snapshot
                                // began is not in it: skipped.
                                for (var k = 0; k < 256 && sample.Count < 4096; k++)
                                {
                                    var i = rng.Next(_c.Items);
                                    var id = Volatile.Read(ref itemIds[i]);
                                    if (!seen.ContainsKey(id) && tx.TryOpen(EntityId.FromRawValue(id), out var opened))
                                    {
                                        var item = opened.Read(MkItemArch.Item);
                                        if (item.ItemNo != i)
                                        {
                                            Fail($"long reader {readerNo}: entity {EntityId.FromRawValue(id)} of item {i} holds item {item.ItemNo}");
                                        }

                                        seen[id] = item;
                                        sample.Add(id);
                                    }
                                }

                                foreach (var id in sample)
                                {
                                    var first = seen[id];
                                    if (!tx.TryOpen(EntityId.FromRawValue(id), out var again))
                                    {
                                        Fail($"long reader {readerNo}: item {first.ItemNo}, read earlier in this snapshot, is no longer in it " +
                                             $"(sweep {sweeps})");
                                        break;
                                    }

                                    var item = again.Read(MkItemArch.Item);
                                    if (item.OwnerNo != first.OwnerNo || item.Price != first.Price || item.TradeCount != first.TradeCount
                                        || item.LastTradeSeq != first.LastTradeSeq)
                                    {
                                        Fail($"long reader {readerNo}: item {first.ItemNo} read owner {first.OwnerNo} / last trade " +
                                             $"{first.LastTradeSeq}, then " +
                                             $"{item.OwnerNo} / {item.LastTradeSeq} in sweep {sweeps} of the same snapshot");
                                        break;
                                    }
                                }

                                sweeps++;
                            } while (held.Elapsed.TotalSeconds < _c.LongReadSeconds && Volatile.Read(ref stormOver) == 0 && Volatile.Read(ref stop) == 0);
                        }

                        Interlocked.Increment(ref longReads);
                        Interlocked.Add(ref longSweeps, sweeps);
                    }
                }
                catch (PageCacheBackpressureTimeoutException) when (Interlocked.Exchange(ref censusTaken, 1) == 0 && LogReadCensus(dbe))
                {
                    // Unreachable: the filter logs and returns false.
                }
                catch (Exception e)
                {
                    Fail($"long reader {readerNo}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    Volatile.Write(ref stop, 1);
                }
            }) { IsBackground = true, Name = $"market-long-reader-{r}" };
        }

        Log($"storm: {_c.Operations:N0} operations on {_c.Threads} threads, an owner-index auditor and {_c.LongReaders} long reader(s)");
        var sw = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            t.Start();
        }

        auditor.Start();
        long querySnapshots = 0;
        var queryAuditor = new Thread(() =>
        {
            try
            {
                if (_c.QueryAudit)
                {
                    querySnapshots = QueryAuditor(dbe, ref stormOver, ref stop);
                }
            }
            catch (Exception e)
            {
                Fail($"query auditor: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                Volatile.Write(ref stop, 1);
            }
        }) { IsBackground = true, Name = "market-query-auditor" };
        queryAuditor.Start();
        crasher?.Start();
        foreach (var t in longReaders)
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
                    $"{Interlocked.Read(ref sells):N0}, trades {Interlocked.Read(ref trades):N0}, consumed {Interlocked.Read(ref consumes):N0}, " +
                    $"rolled back {Interlocked.Read(ref rollbacks):N0}, skipped {Interlocked.Read(ref skipped):N0}, " +
                    $"grants {Interlocked.Read(ref grants):N0}; " +
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
                    Thread[] crasherThread = crasher == null ? [] : [crasher];
                    AbandonIfStuck([.. threads, auditor, .. longReaders, .. crasherThread]);
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

        if (!queryAuditor.Join(TimeSpan.FromSeconds(120)))
        {
            Fail("the query auditor did not stop within 120 s of the storm's end");
            AbandonIfStuck([queryAuditor]);
        }

        if (_c.QueryAudit)
        {
            Log($"query auditor: {querySnapshots:N0} snapshot(s) counted");
        }

        if (crasher != null && !crasher.Join(TimeSpan.FromSeconds(90)))
        {
            Fail("the crasher did not finish within 90 s of the storm's end");
            AbandonIfStuck([crasher]);
        }

        if (dbe.MMF.PageWriteInterceptor != null)
        {
            dbe.MMF.PageWriteInterceptor = null;
            Log("note: the crash armed at a page write did not fire before the storm ended; the run goes on as an ordinary one");
        }

        foreach (var t in longReaders)
        {
            if (!t.Join(TimeSpan.FromSeconds(60)))
            {
                Fail($"{t.Name} did not stop within 60 s of the storm's end");
                AbandonIfStuck(longReaders);
            }
        }

        if (_c.LongReaders > 0)
        {
            Log($"long readers: {longReads:N0} snapshots held, {longSweeps:N0} sweeps of the traders in them");
        }

        // The power cut after the stop (TYPHON_MKT_KILL_AFTER_PUBLISH_FAIL_MS): the workers have stopped, the engine has not closed.
        if (killAfterMs >= 0 && dbe.CommitPipelineFailure != null)
        {
            Thread.Sleep(killAfterMs);
            File.AppendAllText(Path.Combine(_c.Directory, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} crashed {killAfterMs} ms after the storm stopped on a staged publish failure{Environment.NewLine}");
            Process.GetCurrentProcess().Kill();
        }

        lastSeq = Interlocked.Read(ref seq);
        Log($"storm done in {sw.Elapsed.TotalSeconds:F1} s: {done:N0} ops ({done / sw.Elapsed.TotalSeconds:N0}/s), buys {buys:N0}, sells {sells:N0}, " +
            $"trades {trades:N0}, consumed {consumes:N0}, rolled back {rollbacks:N0}, skipped for lack of credits {skipped:N0}, " +
            $"credit grants {grants:N0}; " +
            $"{ownerAudits:N0} owner audits");
        if (_c.OwnedPermille > 0)
        {
            Log($"owned picks: {ownedPicks:N0} operation(s) picked a player's item through the owner index");
        }

        if (_c.Arbitrate)
        {
            Log($"arbitration: {_walletMerges:N0} wallet and {_countMerges:N0} count conflict(s) found by the engine and merged");
            if (_walletMerges == 0 && done >= 50_000)
            {
                Fail("arbitration: the engine reported no conflict on a desk's wallet, so nothing was arbitrated");
            }
        }

        if (_c.OwnerAudit && ownerAudits == 0)
        {
            Log("note: the storm ended before the owner auditor completed an audit");
        }

        var all = new List<ulong>();
        foreach (var list in audits)
        {
            all.AddRange(list);
        }

        if (all.Count != buys + sells + trades + consumes + grants)
        {
            Fail($"{all.Count:N0} audit entries recorded for {buys + sells + trades + consumes:N0} committed transfers and {grants:N0} credit grants");
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
        var traded = Apply(s, ReadAudits(dbe, auditIds));
        Log($"replayed {auditIds.Count:N0} audit entries over {traded:N0} items");
        return traded;
    }

    private MkAudit[] ReadAudits(DatabaseEngine dbe, List<ulong> auditIds)
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

        return entries;
    }

    /// <summary>
    /// Applies audit entries to <paramref name="s"/> in sequence order (sorts them), checking each item moves from where the replay has it. Returns how
    /// many distinct items were traded.
    /// </summary>
    private int Apply(State s, MkAudit[] entries)
    {
        Array.Sort(entries, (a, b) => a.Seq.CompareTo(b.Seq));
        var traded = new bool[_c.Items];
        var tradedCount = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var a = ref entries[i];
            if (i > 0 && a.Seq == entries[i - 1].Seq)
            {
                Fail($"audit sequence {a.Seq} appears twice");
            }

            // Each trader's sequences are taken under its lock, so in sequence order its balance is what it was when it paid: never short. A lost grant
            // shows here, where the item checks below cannot see it. Not a desk's under TYPHON_MKT_ARBITRATE: unlocked, its sequences are not taken in
            // its commit order.
            if (s.Credits[a.Payer] < a.Amount && !(_c.Arbitrate && a.Payer <= _c.Desks))
            {
                Fail($"audit {a.Seq}: trader {a.Payer} pays {a.Amount}, but the replay has it at {s.Credits[a.Payer]}");
            }

            if (a.Kind == KindGrant)
            {
                s.Credits[a.Payer] -= a.Amount;
                s.Credits[a.Payee] += a.Amount;
                continue;
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
            tradedCount += traded[item] ? 0 : 1;
            traded[item] = true;
        }

        return tradedCount;
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

            // An unlocked desk (TYPHON_MKT_ARBITRATE) checks its balance on its snapshot, and a merge cannot refuse: two payments from one desk may overdraw
            // it — snapshot isolation's write skew, which the merge reproduces exactly. Conservation still holds.
            if (s.Credits[t] < 0 && !(_c.Arbitrate && t <= _c.Desks))
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
    private void CheckPlayerItems(DatabaseEngine dbe, IndexRef ownerIndex, ulong[] traderIds, ulong[] itemIds, long player, string phase, bool ordered)
    {
        using var tx = dbe.CreateReadOnlyTransaction();
        var counted = tx.Open(EntityId.FromRawValue(traderIds[player])).Read(MkTraderArch.Trader).ItemsOwned;
        var found = 0;
        var indexed = _c.QueryAudit ? new HashSet<ulong>() : null;
        using (var e = tx.EnumerateIndex<MkItem, long>(ownerIndex, player, player))
        {
            while (e.MoveNext())
            {
                found++;
                var item = e.CurrentComponent;
                var id = (ulong)e.CurrentEntityPK;
                indexed?.Add(id);
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

        if (indexed != null)
        {
            CheckPlayerQueries(tx, player, indexed, phase, ordered);
        }
    }

    /// <summary>
    /// The query engine's answer for a player's items, in the same snapshot as the owner index's walk (<c>TYPHON_MKT_QUERY_AUDIT</c>): a field query's
    /// <c>Count</c> and <c>Execute</c> find exactly the entities the index holds, and the same query ordered by the item's last trade — a unique index every
    /// trade re-keys — returns them once each, in that order. The ordered query is asked only when <paramref name="ordered"/>: filtered on one field and
    /// ordered by another, it sorts after reading the ordering field's whole tree, so it costs a pass over every item.
    /// </summary>
    private void CheckPlayerQueries(Transaction tx, long player, HashSet<ulong> indexed, string phase, bool ordered)
    {
        var count = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.OwnerNo == player).Count();
        var executed = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.OwnerNo == player).Execute();
        var executedIds = new HashSet<ulong>();
        foreach (var id in executed)
        {
            executedIds.Add(id.RawValue);
        }

        if (count != indexed.Count || !executedIds.SetEquals(indexed))
        {
            Fail($"{phase}: player {player}: the owner index holds {indexed.Count} item(s); a field query counts {count}, executes {executed.Count} " +
                 $"({executedIds.Except(indexed).Count()} not in the index, {indexed.Except(executedIds).Count()} missing)");
        }

        if (!ordered)
        {
            return;
        }

        var byLastTrade = tx.Query<MkItemArch>().WhereField<MkItem>(x => x.OwnerNo == player).OrderByField<MkItem, long>(x => x.LastTradeSeq)
            .ExecuteOrdered();
        var lastKey = long.MinValue;
        var orderedIds = new HashSet<ulong>();
        foreach (var id in byLastTrade)
        {
            var key = tx.Open(id).Read(MkItemArch.Item).LastTradeSeq;
            if (!orderedIds.Add(id.RawValue) || key <= lastKey)
            {
                Fail($"{phase}: player {player}: the query ordered by last trade returns entity {id} twice or out of order ({key} after {lastKey})");
                return;   // the set check below would only repeat this, short of the rows not walked
            }

            lastKey = key;
        }

        if (!orderedIds.SetEquals(indexed))
        {
            Fail($"{phase}: player {player}: the query ordered by last trade returns {byLastTrade.Count} item(s), the owner index holds {indexed.Count} " +
                 $"({orderedIds.Except(indexed).Count()} not in the index, {indexed.Except(orderedIds).Count()} missing)");
        }
    }

    /// <summary>
    /// The query auditor (<c>TYPHON_MKT_QUERY_AUDIT</c>): at successive snapshots during the storm, a broad <c>Count</c> of the items is exactly the
    /// configured number — a consume destroys an item and crafts it anew in one transaction, so no snapshot sees one more or one less — and the audit
    /// entries never count fewer than at an earlier snapshot. Small worlds also check a broad <c>Execute</c> against the count. Returns the snapshots
    /// checked.
    /// </summary>
    private long QueryAuditor(DatabaseEngine dbe, ref int stormOver, ref int stop)
    {
        long snapshots = 0, lastAudits = 0;
        while (Volatile.Read(ref stormOver) == 0 && Volatile.Read(ref stop) == 0)
        {
            using var tx = dbe.CreateReadOnlyTransaction();
            var items = tx.Query<MkItemArch>().Count();
            if (items != _c.Items)
            {
                Fail($"query auditor: a broad count finds {items:N0} item(s) at snapshot {tx.TSN}, not {_c.Items:N0}");
            }

            if (_c.Items <= 2_000_000 && (snapshots & 7) == 0 && tx.Query<MkItemArch>().Execute().Count != _c.Items)
            {
                Fail($"query auditor: a broad execute does not find {_c.Items:N0} item(s) at snapshot {tx.TSN}");
            }

            var audits = tx.Query<MkAuditArch>().Count();
            if (audits < lastAudits)
            {
                Fail($"query auditor: {audits:N0} audit entries at snapshot {tx.TSN}, {lastAudits:N0} at an earlier one");
            }

            lastAudits = audits;
            snapshots++;
        }

        return snapshots;
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
            Fail($"{phase}: storage integrity — {report.Issues.Count} issue(s), {report.OrphanPageCount} orphan, " +
                 $"{report.PhantomPageCount} phantom page(s): " +
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
