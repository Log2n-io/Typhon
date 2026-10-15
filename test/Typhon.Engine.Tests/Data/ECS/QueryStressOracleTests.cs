using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Every field-query path and terminal against an oracle, under concurrent spawns, destroys, enables, disables, first supplies, updates and aborts (#1259,
/// #1347, QENABLE-01, VIS-09). The oracle is the reader's own point reads — the revision chain and the record's born and died, code the scans and the
/// indexes do not share — so a path that disagrees with it is wrong at that snapshot.
/// </summary>
/// <remarks>
/// <para>The writers keep each entity's indexed tier — two concurrent first supplies of it then supply the same one — and change everything else: presence,
/// enabled state, life, the non-indexed score. A mover moves tiers (VIS-12, #674): a field predicate read the slot's current value, and an index its
/// current key, so a reader older than a move answered by the newer tier. It never checks its own pending state, whose own writes field queries do not
/// see while point reads do.</para>
/// <para>A reader queries once every commit in flight at its snapshot has published: between a commit's append and its publish the index paths read HEAD,
/// a documented non-goal (VIS-08), which a check at that instant would report.</para>
/// </remarks>
[TestFixture]
[NonParallelizable] // QueryPathProbe.Forced is per thread, but the engine and its thread count are this fixture's.
[Category("Sensitive")]
class QueryStressOracleTests : TestBase<QueryStressOracleTests>
{
    private const int Tiers = 6;
    private const int WriterCount = 3;
    private const int ReaderCount = 2;
    private static readonly bool OwnChecks = Environment.GetEnvironmentVariable("QSO_OWN") != "0";

    private static readonly ClusterScanPath[] Paths = [ClusterScanPath.Planner, ClusterScanPath.Selective, ClusterScanPath.FullScan];

    private static int TierOf(EntityId id) => (int)(((ulong)id.EntityKey * 2654435761UL >> 7) % Tiers);

    /// <summary>Every entity ever spawned (or about to be), behind one lock: a pick and a snapshot do not copy every writer's list as a bag's do.</summary>
    private sealed class EntityPool
    {
        private readonly List<EntityId> _ids = [];

        public void Add(EntityId id)
        {
            lock (_ids)
            {
                _ids.Add(id);
            }
        }

        public EntityId Pick(Random rng)
        {
            lock (_ids)
            {
                return _ids[rng.Next(_ids.Count)];
            }
        }

        public EntityId[] Snapshot()
        {
            lock (_ids)
            {
                return _ids.ToArray();
            }
        }
    }

    private sealed class Shared
    {
        public readonly EntityPool Ever = new();
        public readonly long[] Committing = new long[WriterCount + 1];   // the writer's commit number while it commits, else 0; the mover's last
        public readonly ConcurrentQueue<string> Failures = new();
        public readonly ConcurrentDictionary<string, int> Errors = new();
        public volatile bool Stop;
        public int Checks;
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    [VerifiesRule("VIS-09")]
    public void EveryPathAndTerminal_AgreeWithPointReads_UnderConcurrentChurn([Values(2_000)] int milliseconds) => Run(milliseconds, false);

    /// <summary>
    /// The same churn with a mover changing the indexed tier: the value half of snapshot isolation (VIS-12, #674) under load. Known-red on two pre-existing
    /// defects the mover exposed, #1370 (an index entry left at an old key, the QUIET check below) and #1369 (an access violation in the ordered merge's
    /// row admission), about one run in ten between them.
    /// </summary>
    [Test]
    [Category("Quarantine")]   // #1369, #1370
    [VerifiesRule("VIS-12")]
    public void EveryPathAndTerminal_AgreeWithPointReads_UnderConcurrentChurnAndTierMoves([Values(2_000)] int milliseconds) => Run(milliseconds, true);

    private void Run(int milliseconds, bool moves)
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("QSO_SEED"), out var fixedSeed) ? fixedSeed : Environment.TickCount;
        milliseconds = int.TryParse(Environment.GetEnvironmentVariable("QSO_MS"), out var ms) ? ms : milliseconds;
        TestContext.Out.WriteLine($"seed {seed}");
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<StatsRanked>();
        dbe.RegisterComponentFromAccessor<StatsTag>();
        dbe.InitializeArchetypes();

        var shared = new Shared();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 300; i++)
            {
                var id = i % 3 == 0 ? tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(i))) : SpawnSupplied(tx, i);
                shared.Ever.Add(id);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        var threads = new List<Thread>();
        for (var w = 0; w < WriterCount; w++)
        {
            var writer = w;
            threads.Add(new Thread(() => Guard(shared, () => Write(dbe, shared, writer, new Random(seed + writer)))));
        }

        if (moves)
        {
            threads.Add(new Thread(() => Guard(shared, () => Move(dbe, shared, new Random(seed + 50)))));
        }
        for (var r = 0; r < ReaderCount; r++)
        {
            var reader = r;
            threads.Add(new Thread(() => Guard(shared, () => Read(dbe, shared, new Random(seed + 100 + reader)))));
        }

        foreach (var t in threads)
        {
            t.Start();
        }

        Thread.Sleep(milliseconds);
        shared.Stop = true;
        foreach (var t in threads)
        {
            Assert.That(t.Join(30_000), Is.True, "a stress thread never finished");
        }

        // The quiet end state: every path exact for a fresh reader, no claim left, storage sound.
        Check(dbe, shared, new Random(seed), dbe.CreateQuickTransaction(), true);
        using (var quiet = dbe.CreateQuickTransaction())
        {
            foreach (var disabled in new[] { false, true })
            {
                for (var k = 0; k < Tiers; k++)
                {
                    var tier = k;
                    HashSet<EntityId> On(ClusterScanPath path)
                    {
                        QueryPathProbe.Forced = path;
                        try
                        {
                            var q = quiet.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
                            return (disabled ? q.Disabled<StatsRanked>() : q).Execute();
                        }
                        finally
                        {
                            QueryPathProbe.Forced = ClusterScanPath.Planner;
                        }
                    }

                    var byIndex = On(ClusterScanPath.Selective);
                    var byScan = On(ClusterScanPath.FullScan);
                    if (!byIndex.SetEquals(byScan))
                    {
                        shared.Failures.Enqueue($"QUIET index diverges at tier {tier}{(disabled ? " Disabled" : "")}: index extra "
                                                + $"{string.Join(",", byIndex.Except(byScan).Take(3))}, "
                                                + $"index misses {string.Join(",", byScan.Except(byIndex).Take(3))}");
                    }
                }
            }
        }
        TestContext.Out.WriteLine($"checks {shared.Checks}, errors {string.Join(", ", shared.Errors.Select(e => $"{e.Key}={e.Value}"))}");
        foreach (var kind in shared.Failures.GroupBy(f => System.Text.RegularExpressions.Regex.Replace(f, @"\d+|Entity\([^)]*\)", "#")))
        {
            TestContext.Out.WriteLine($"FAILKIND x{kind.Count()}: {kind.First()}");
        }
        Assert.That(shared.Failures, Is.Empty, string.Join(Environment.NewLine, shared.Failures.Take(10)));
        Assert.That(dbe.FirstSupplyClaims, Is.Empty, "a first-supply claim outlived its commit");
        Assert.That(dbe.RunStorageIntegrityCheck().Issues.Select(i => $"{i.Kind}: {i.Detail}"), Is.Empty, "storage integrity");
        Assert.That(shared.Checks, Is.GreaterThan(50), "premise: the readers and writers checked");
    }

    private static EntityId SpawnSupplied(Transaction tx, int marker)
    {
        // The tier is a function of the entity, so a spawn supplies a placeholder and fixes it once the id is known — in the same transaction.
        var id = tx.Spawn<StatsArch>(StatsArch.Ranked.Set(new StatsRanked(0, marker)), StatsArch.Tag.Set(new StatsTag(marker)));
        tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(TierOf(id), marker));
        return id;
    }

    private static void Guard(Shared shared, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            shared.Failures.Enqueue($"thread died: {e}");
            shared.Stop = true;
        }
    }

    private static EntityId Pick(Shared shared, Random rng) => shared.Ever.Pick(rng);

    private static void Write(DatabaseEngine dbe, Shared shared, int writer, Random rng)
    {
        long commitNumber = 0;
        while (!shared.Stop)
        {
            var tx = dbe.CreateQuickTransaction();
            try
            {
                // The commits in flight at this snapshot published before this transaction checks its own reads (see Read).
                WaitForInFlightCommits(shared);
                var spawned = new List<EntityId>();
                var ops = 1 + rng.Next(4);
                for (var o = 0; o < ops; o++)
                {
                    try
                    {
                        Mutate(tx, shared, rng, spawned);
                    }
                    catch (Exception e) when (e is InvalidOperationException or TyphonException)
                    {
                        // Counted by message, and printed: an operation the generator could not foresee is refused, an engine invariant throw would show.
                        shared.Errors.AddOrUpdate($"op {e.GetType().Name}: {e.Message}", 1, (_, n) => n + 1);
                    }
                }

                // Read-your-own-writes, now and then: the transaction's own pending state against its own point reads.
                if (OwnChecks && rng.Next(8) == 0)
                {
                    Check(dbe, shared, rng, tx, false, own: true);
                }

                if (rng.Next(10) == 0)
                {
                    tx.Rollback();
                    continue;
                }

                // Known before the commit, so a reader whose snapshot the commit precedes always has them among its candidates.
                foreach (var id in spawned)
                {
                    shared.Ever.Add(id);
                }

                Volatile.Write(ref shared.Committing[writer], ++commitNumber);
                try
                {
                    if (!tx.Commit())
                    {
                        shared.Errors.AddOrUpdate("commit returned false", 1, (_, n) => n + 1);
                    }
                }
                catch (Exception e) when (e is TimeoutException or TyphonTimeoutException)
                {
                    shared.Errors.AddOrUpdate("commit:" + e.GetType().Name, 1, (_, n) => n + 1);
                }
                finally
                {
                    Volatile.Write(ref shared.Committing[writer], 0);
                }
            }
            finally
            {
                tx.Dispose();
            }
        }
    }

    private static void Mutate(Transaction tx, Shared shared, Random rng, List<EntityId> spawned)
    {
        var op = rng.Next(100);
        if (op < 15)
        {
            spawned.Add(rng.Next(3) == 0 ? tx.Spawn<StatsArch>(StatsArch.Tag.Set(new StatsTag(op))) : SpawnSupplied(tx, op));
            if (rng.Next(4) == 0 && tx.Open(spawned[^1]).IsEnabled(StatsArch.Ranked))
            {
                tx.OpenMut(spawned[^1]).Disable(StatsArch.Ranked);
            }

            return;
        }

        var id = Pick(shared, rng);
        if (!tx.TryOpen(id, out var entity))
        {
            return;
        }

        var enabled = entity.IsEnabled(StatsArch.Ranked);
        var present = enabled || entity.TryReadForPredicate<StatsRanked>(true, out _);
        if (op < 25)
        {
            tx.Destroy(id);
        }
        else if (op < 45 && !present)
        {
            tx.OpenMut(id).Enable(StatsArch.Ranked, new StatsRanked(TierOf(id), op));   // a first supply, raced by the other writers'
        }
        else if (op < 65 && enabled)
        {
            tx.OpenMut(id).Disable(StatsArch.Ranked);
        }
        else if (op < 80 && present && !enabled)
        {
            tx.OpenMut(id).Enable(StatsArch.Ranked);
        }
        else if (enabled && entity.TryReadForPredicate<StatsRanked>(false, out var current))
        {
            // The score churns; the tier stays as this transaction reads it — the mover's, maybe. Writing TierOf back would move it in this transaction's own
            // pending state, which its field queries do not see and its point reads do: the own checks would report the difference.
            tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(current.Tier, rng.Next(1000)));
        }
    }

    /// <summary>Moves entities between tiers, a few at a commit: the indexed key the snapshot paths must read at the reader's snapshot (VIS-12).</summary>
    private static void Move(DatabaseEngine dbe, Shared shared, Random rng)
    {
        long commitNumber = 0;
        while (!shared.Stop)
        {
            using var tx = dbe.CreateQuickTransaction();
            var ever = shared.Ever.Snapshot();
            for (var k = 0; k < 4; k++)
            {
                var id = ever[rng.Next(ever.Length)];
                try
                {
                    if (tx.TryOpen(id, out var entity) && entity.IsEnabled(StatsArch.Ranked) && entity.TryReadForPredicate<StatsRanked>(false, out var value))
                    {
                        tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(rng.Next(Tiers), value.Score));
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or TyphonException)
                {
                    // Counted by message, as the writers count theirs.
                    shared.Errors.AddOrUpdate($"move {e.GetType().Name}: {e.Message}", 1, (_, n) => n + 1);
                }
            }

            Volatile.Write(ref shared.Committing[WriterCount], ++commitNumber);
            try
            {
                tx.Commit();
            }
            catch (Exception e) when (e is TimeoutException or TyphonTimeoutException)
            {
                shared.Errors.AddOrUpdate("move:" + e.GetType().Name, 1, (_, n) => n + 1);
            }
            finally
            {
                Volatile.Write(ref shared.Committing[WriterCount], 0);
            }
        }
    }

    private static void Read(DatabaseEngine dbe, Shared shared, Random rng)
    {
        while (!shared.Stop)
        {
            var reader = dbe.CreateQuickTransaction();
            try
            {
                WaitForInFlightCommits(shared);

                // Older than what commits next, now and then — once in a while much older: the snapshot paths (#1347, VIS-12).
                var spins = rng.Next(3) == 0 ? rng.Next(rng.Next(6) == 0 ? 400_000 : 20_000) : 0;
                for (var s = 0; s < spins; s++)
                {
                    Thread.SpinWait(1);
                }

                Check(dbe, shared, rng, reader, false);
            }
            finally
            {
                reader.Dispose();
            }
        }
    }

    private static void WaitForInFlightCommits(Shared shared)
    {
        var seen = new long[WriterCount + 1];
        for (var w = 0; w <= WriterCount; w++)
        {
            seen[w] = Volatile.Read(ref shared.Committing[w]);
        }

        for (var w = 0; w <= WriterCount; w++)
        {
            var spin = new SpinWait();
            while (seen[w] != 0 && Volatile.Read(ref shared.Committing[w]) == seen[w] && !shared.Stop)
            {
                spin.SpinOnce(-1);
            }
        }
    }

    /// <summary>The entity's state at HEAD, through a fresh transaction on this thread.</summary>
    private static string Head(DatabaseEngine dbe, EntityId id)
    {
        using var fresh = dbe.CreateQuickTransaction();
        return fresh.TryOpen(id, out var e)
            ? $"HEAD: alive enabled={e.IsEnabled(StatsArch.Ranked)} present={e.TryReadForPredicate<StatsRanked>(true, out var v)} tier={v.Tier}"
            : "HEAD: not alive";
    }

    /// <summary>The tiers the transaction's field queries place <paramref name="id"/> in, on each forced path.</summary>
    private static string QueryTiers(Transaction tx, EntityId id, bool disabled)
    {
        var parts = new List<string>();
        var saved = QueryPathProbe.Forced;
        try
        {
            foreach (var path in new[] { ClusterScanPath.Selective, ClusterScanPath.FullScan })
            {
                QueryPathProbe.Forced = path;
                var tiers = new List<int>();
                for (var k = 0; k < Tiers; k++)
                {
                    var tier = k;
                    var q = tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
                    if ((disabled ? q.Disabled<StatsRanked>() : q).Execute().Contains(id))
                    {
                        tiers.Add(k);
                    }
                }

                parts.Add($"{path}:[{string.Join(",", tiers)}]");
            }
        }
        finally
        {
            QueryPathProbe.Forced = saved;
        }

        return "query tiers " + string.Join(" ", parts);
    }

    private static string Describe(Transaction tx, EntityId id)
    {
        var spawned = tx.PendingSpawns?.Any(s => s.Id == id) == true;
        var destroyed = tx.PendingDestroys?.Contains(id) == true;
        var bits = tx.PendingEnableDisable != null && tx.PendingEnableDisable.TryGetValue(id, out var b) ? $"pendingBits={b}" : "noPendingBits";
        var point = tx.TryOpen(id, out var e)
            ? $"point: enabled={e.IsEnabled(StatsArch.Ranked)} present={e.TryReadForPredicate<StatsRanked>(true, out var v)} tier={v.Tier}"
            : "point: not visible";
        return $"spawnedHere={spawned} destroyedHere={destroyed} {bits} {point}";
    }

    /// <summary>One random query shape on every path and terminal, against <paramref name="tx"/>'s own point reads.</summary>
    private static void Check(DatabaseEngine dbe, Shared shared, Random rng, Transaction tx, bool disposeAfter, bool own = false)
    {
        try
        {
            var tier = rng.Next(Tiers);
            var range = rng.Next(2) == 0;
            var disabled = rng.Next(3) == 0;

            // The oracle: every entity ever spawned — this transaction's own pending spawns too — as this transaction reads it.
            var expected = new List<(EntityId Id, int Tier)>();
            var ownSpawns = tx.PendingSpawns?.Select(s => s.Id).ToHashSet() ?? [];
            var candidates = shared.Ever.Snapshot().Concat(ownSpawns).Distinct().ToArray();
            foreach (var id in candidates)
            {
                if (!tx.TryOpen(id, out var entity))
                {
                    continue;
                }

                if (!entity.TryReadForPredicate<StatsRanked>(disabled, out var value) || entity.IsEnabled(StatsArch.Ranked) == disabled)
                {
                    continue;
                }

                // A component this transaction supplied is absent to its own field queries until it commits (QENABLE-01).
                if (own && tx.FirstSuppliedHere((long)id.RawValue, ArchetypeRegistry.GetComponentTypeId<StatsRanked>()))
                {
                    continue;
                }

                if (range ? value.Tier >= tier : value.Tier == tier)
                {
                    expected.Add((id, value.Tier));
                }
            }

            expected = expected.Distinct().ToList();
            var expectedIds = expected.Select(e => e.Id).ToHashSet();
            var shape = $"{(range ? ">=" : "==")}{tier}{(disabled ? " Disabled" : "")}{(own ? " own" : "")}";
            foreach (var path in Paths)
            {
                QueryPathProbe.Forced = path;
                try
                {
                    EcsQuery<StatsArch> Q()
                    {
                        var q = range
                            ? tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier >= tier)
                            : tx.Query<StatsArch>().WhereField<StatsRanked>(x => x.Tier == tier);
                        return disabled ? q.Disabled<StatsRanked>() : q;
                    }

                    var at = $"{shape} on {path} at TSN {tx.TSN}";
                    var executed = Q().Execute();
                    if (!executed.SetEquals(expectedIds) && own)
                    {
                        foreach (var x in executed.Except(expectedIds).Take(1))
                        {
                            shared.Failures.Enqueue($"OWNDIAG {shape} extra {Describe(tx, x)} {Head(dbe, x)} {QueryTiers(tx, x, disabled)}");
                        }

                        foreach (var x in expectedIds.Except(executed).Take(1))
                        {
                            shared.Failures.Enqueue($"OWNDIAG {shape} missing {Describe(tx, x)}");
                        }
                    }

                    if (!executed.SetEquals(expectedIds))
                    {
                        shared.Failures.Enqueue($"{at}: Execute has {executed.Except(expectedIds).Count()} extra, "
                                                + $"misses {expectedIds.Except(executed).Count()} "
                                                + $"(e.g. extra {string.Join(",", executed.Except(expectedIds).Take(3))}, "
                                                + $"missing {string.Join(",", expectedIds.Except(executed).Take(3))})");
                    }

                    // Where<T> — a per-entity point read over the broad scan — answers as the oracle does for a reader without changes of its own. The broad
                    // scan used to miss live entities while the entity map split under it (#1351).
                    if (!own && path == ClusterScanPath.Planner)
                    {
                        var where = (range
                                ? tx.Query<StatsArch>().Where<StatsRanked>(x => x.Tier >= tier)
                                : tx.Query<StatsArch>().Where<StatsRanked>(x => x.Tier == tier));
                        var whereIds = (disabled ? where.Disabled<StatsRanked>() : where).Execute();
                        if (!whereIds.SetEquals(expectedIds))
                        {
                            shared.Failures.Enqueue(
                                $"{at}: Where<T> has {whereIds.Except(expectedIds).Count()} extra, misses {expectedIds.Except(whereIds).Count()}");
                        }
                    }

                    var count = Q().Count();
                    if (count != expectedIds.Count)
                    {
                        shared.Failures.Enqueue($"{at}: Count {count}, expected {expectedIds.Count}");
                    }

                    if (Q().Any() != expectedIds.Count > 0)
                    {
                        shared.Failures.Enqueue($"{at}: Any wrong");
                    }

                    var orderedExpected = expected;
                    var orderedIds = expectedIds;
                    var descending = rng.Next(2) == 0;
                    var ordered = descending
                        ? Q().OrderByFieldDescending<StatsRanked, int>(x => x.Tier).ExecuteOrdered()
                        : Q().OrderByField<StatsRanked, int>(x => x.Tier).ExecuteOrdered();
                    if (!ordered.ToHashSet().SetEquals(orderedIds) || ordered.Count != orderedIds.Count)
                    {
                        var dup = ordered.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
                        foreach (var x in ordered.ToHashSet().Except(orderedIds).Take(1))
                        {
                            shared.Failures.Enqueue($"ORDDIAG {shape} extra {Describe(tx, x)} {Head(dbe, x)}");
                        }

                        foreach (var x in orderedIds.Except(ordered).Take(1))
                        {
                            shared.Failures.Enqueue($"ORDDIAG {shape} missing {Describe(tx, x)} {Head(dbe, x)}");
                        }

                        if (!dup.IsNull)
                        {
                            shared.Failures.Enqueue($"ORDDIAG {shape} duplicate {Describe(tx, dup)} {Head(dbe, dup)}");
                        }

                        shared.Failures.Enqueue($"{at}: ExecuteOrdered returned {ordered.Count}, expected {orderedIds.Count}");
                    }
                    else
                    {
                        var tiers = ordered.Select(id => orderedExpected.First(e => e.Id == id).Tier).ToList();
                        var sorted = descending ? tiers.OrderByDescending(t => t).ToList() : tiers.OrderBy(t => t).ToList();
                        if (!tiers.SequenceEqual(sorted))
                        {
                            shared.Failures.Enqueue($"{at}: ExecuteOrdered out of order");
                        }
                    }

                    // A page: its tiers are the oracle's sorted tiers at that window.
                    var skip = rng.Next(Math.Max(1, orderedIds.Count));
                    var take = 1 + rng.Next(20);
                    var page = descending
                        ? Q().OrderByFieldDescending<StatsRanked, int>(x => x.Tier).Skip(skip).Take(take).ExecuteOrdered()
                        : Q().OrderByField<StatsRanked, int>(x => x.Tier).Skip(skip).Take(take).ExecuteOrdered();
                    var allTiers = descending
                        ? orderedExpected.Select(e => e.Tier).OrderByDescending(t => t)
                        : orderedExpected.Select(e => e.Tier).OrderBy(t => t);
                    var window = allTiers.Skip(skip).Take(take).ToList();
                    if (page.Count != window.Count || page.Any(id => !orderedIds.Contains(id))
                        || !page.Select(id => orderedExpected.First(e => e.Id == id).Tier).SequenceEqual(window))
                    {
                        shared.Failures.Enqueue($"{at}: page Skip({skip}).Take({take}) wrong ({page.Count} rows, expected {window.Count})");
                    }
                }
                finally
                {
                    QueryPathProbe.Forced = ClusterScanPath.Planner;
                }
            }

            Interlocked.Increment(ref shared.Checks);
        }
        finally
        {
            if (disposeAfter)
            {
                tx.Dispose();
            }
        }
    }
}
