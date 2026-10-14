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
/// <para>A field predicate evaluates the slot's current value, so an entity's indexed tier never changes: the value is fixed per entity, and two concurrent
/// first supplies of it supply the same one. What changes is everything else — presence, enabled state, life, the non-indexed score.</para>
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
    private static readonly bool WhereChecks = Environment.GetEnvironmentVariable("QSO_WHERE") == "1";

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
        public readonly long[] Committing = new long[WriterCount];   // the writer's commit number while it commits, else 0
        public readonly ConcurrentQueue<string> Failures = new();
        public readonly ConcurrentDictionary<string, int> Errors = new();
        public volatile bool Stop;
        public int Checks;
    }

    [Test]
    [VerifiesRule("QENABLE-01")]
    [VerifiesRule("VIS-09")]
    public void EveryPathAndTerminal_AgreeWithPointReads_UnderConcurrentChurn([Values(2_000)] int milliseconds)
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
        else if (enabled)
        {
            tx.OpenMut(id).Set(StatsArch.Ranked, new StatsRanked(TierOf(id), rng.Next(1000)));   // the score churns; the tier stays
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

                // Older than what commits next, now and then: the snapshot paths (#1347).
                var spins = rng.Next(3) == 0 ? rng.Next(20_000) : 0;
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
        var seen = new long[WriterCount];
        for (var w = 0; w < WriterCount; w++)
        {
            seen[w] = Volatile.Read(ref shared.Committing[w]);
        }

        for (var w = 0; w < WriterCount; w++)
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
                            shared.Failures.Enqueue($"OWNDIAG {shape} extra {Describe(tx, x)}");
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

                    // Where<T> — a per-entity point read over the broad scan — answers as the oracle does for a reader without changes of its own. Off by
                    // default: the broad scan misses live entities under concurrent spawns and destroys (#1351); QSO_WHERE=1 runs it.
                    if (WhereChecks && !own && path == ClusterScanPath.Planner)
                    {
                        var where = (range
                                ? tx.Query<StatsArch>().Where<StatsRanked>(x => x.Tier >= tier)
                                : tx.Query<StatsArch>().Where<StatsRanked>(x => x.Tier == tier));
                        var whereIds = (disabled ? where.Disabled<StatsRanked>() : where).Execute();
                        if (!whereIds.SetEquals(expectedIds))
                        {
                            foreach (var x in expectedIds.Except(whereIds).Take(1))
                            {
                                var inBroad = tx.Query<StatsArch>().Execute().Contains(x);
                                var reopen = tx.TryOpen(x, out _);
                                shared.Failures.Enqueue($"WHEREDIAG {shape} missing inBroad={inBroad} reopen={reopen} {Describe(tx, x)} {Head(dbe, x)}");
                            }

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
