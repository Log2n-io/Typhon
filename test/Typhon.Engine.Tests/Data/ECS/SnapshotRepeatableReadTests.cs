using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.SnapshotRead.Account", 1)]
public struct SrAccount
{
    public long No;
    public long Credits;
}

[Component("Typhon.Test.SnapshotRead.Count", 1)]
public struct SrCount
{
    public long Items;
}

[Archetype]
class SrAccountArch : Archetype<SrAccountArch>
{
    public static readonly Comp<SrAccount> Account = Register<SrAccount>();
    public static readonly Comp<SrCount> Count = Register<SrCount>();
}

/// <summary>
/// A read-only transaction reads its snapshot, whatever commits after it began: the values before those commits, the same on every read.
/// </summary>
[TestFixture]
class SnapshotRepeatableReadTests : TestBase<SnapshotRepeatableReadTests>
{
    private DatabaseEngine Engine(int count, bool bulkLoaded, out EntityId[] ids)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SrAccount>();
        dbe.RegisterComponentFromAccessor<SrCount>();
        dbe.InitializeArchetypes();
        ids = new EntityId[count];
        if (bulkLoaded)
        {
            using var session = dbe.BeginBulkLoad();
            for (var i = 0; i < count; i++)
            {
                ids[i] = session.Spawn<SrAccountArch>(SrAccountArch.Account.Set(new SrAccount { No = i, Credits = 1000 }),
                    SrAccountArch.Count.Set(new SrCount { Items = 10 }));
            }

            session.CompleteBulkLoad();
        }
        else
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < count; i++)
            {
                ids[i] = tx.Spawn<SrAccountArch>(SrAccountArch.Account.Set(new SrAccount { No = i, Credits = 1000 }),
                    SrAccountArch.Count.Set(new SrCount { Items = 10 }));
            }

            tx.Commit();
        }

        return dbe;
    }

    private static void Bump(DatabaseEngine dbe, EntityId[] ids, int round)
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            var e = tx.OpenMut(id);
            var a = e.Read(SrAccountArch.Account);
            a.Credits += 1;
            e.Set(SrAccountArch.Account, a);
            var c = e.Read(SrAccountArch.Count);
            c.Items += 1;
            e.Set(SrAccountArch.Count, c);
        }

        tx.Commit();
    }

    private static string Sweep(Transaction reader, EntityId[] ids, long credits, long items, string what)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            var e = reader.Open(ids[i]);
            var a = e.Read(SrAccountArch.Account);
            var c = e.Read(SrAccountArch.Count);
            if (a.No != i || a.Credits != credits || c.Items != items)
            {
                return $"{what}: entity {i} reads credits {a.Credits} / items {c.Items}, expected {credits} / {items}";
            }
        }

        return null;
    }

    [Test]
    public void ASnapshot_ReadAfterCommits_SeesTheValuesBeforeThem([Values(10, 1000)] int count, [Values(false, true)] bool bulkLoaded,
        [Values(1, 3)] int rounds)
    {
        using var dbe = Engine(count, bulkLoaded, out var ids);
        using var reader = dbe.CreateReadOnlyTransaction();
        for (var r = 0; r < rounds; r++)
        {
            Bump(dbe, ids, r);
        }

        Assert.That(Sweep(reader, ids, 1000, 10, "first read after the commits"), Is.Null);
        Assert.That(Sweep(reader, ids, 1000, 10, "second read"), Is.Null);
    }

    [Test]
    public void ASnapshot_ReadAroundCommits_ReadsTheSameValues([Values(10, 1000)] int count, [Values(false, true)] bool bulkLoaded,
        [Values(1, 3)] int rounds)
    {
        using var dbe = Engine(count, bulkLoaded, out var ids);
        using var reader = dbe.CreateReadOnlyTransaction();
        Assert.That(Sweep(reader, ids, 1000, 10, "before the commits"), Is.Null);
        for (var r = 0; r < rounds; r++)
        {
            Bump(dbe, ids, r);
        }

        Assert.That(Sweep(reader, ids, 1000, 10, "after the commits"), Is.Null);
    }

    /// <summary>
    /// The writer began BEFORE the reader and commits after the reader's first read: its commit is not in the reader's snapshot, though its TSN is
    /// below the reader's.
    /// </summary>
    [Test]
    [Category("Quarantine")] // #1146: commit stamps the writer's begin TSN, so an older writer's commit becomes visible mid-snapshot. Red until Phase 3.
    public void ASnapshot_ReadAroundTheCommitOfAnOlderWriter_ReadsTheSameValues([Values(10, 1000)] int count, [Values(false, true)] bool bulkLoaded,
        [Values(false, true)] bool readBeforeTheCommit)
    {
        using var dbe = Engine(count, bulkLoaded, out var ids);
        using var writer = dbe.CreateQuickTransaction();
        using var reader = dbe.CreateReadOnlyTransaction();
        Assert.That(writer.TSN, Is.LessThan(reader.TSN), "premise: the writer began first");
        if (readBeforeTheCommit)
        {
            Assert.That(Sweep(reader, ids, 1000, 10, "before the commit"), Is.Null);
        }

        foreach (var id in ids)
        {
            var e = writer.OpenMut(id);
            var a = e.Read(SrAccountArch.Account);
            a.Credits += 1;
            e.Set(SrAccountArch.Account, a);
        }

        Assert.That(writer.Commit(), Is.True);
        Assert.That(Sweep(reader, ids, 1000, 10, "after the older writer's commit"), Is.Null);
    }

    /// <summary>An entity an older writer spawns, or destroys, after the reader began: neither shows in the reader's snapshot.</summary>
    [Test]
    [Category("Quarantine")] // #1146: BornTSN/DiedTSN carry the writer's begin TSN (families 2-3). Red until Phase 5.
    public void ASnapshot_AfterAnOlderWritersSpawnAndDestroy_KeepsItsEntities([Values(false, true)] bool bulkLoaded)
    {
        using var dbe = Engine(10, bulkLoaded, out var ids);
        using var writer = dbe.CreateQuickTransaction();
        using var reader = dbe.CreateReadOnlyTransaction();
        Assert.That(reader.Query<SrAccountArch>().Count(), Is.EqualTo(10), "premise");
        var spawned = writer.Spawn<SrAccountArch>(SrAccountArch.Account.Set(new SrAccount { No = 99, Credits = 1 }),
            SrAccountArch.Count.Set(new SrCount { Items = 1 }));
        writer.Destroy(ids[3]);
        Assert.That(writer.Commit(), Is.True);

        var problems = new System.Collections.Generic.List<string>();
        if (reader.TryOpen(spawned, out _))
        {
            problems.Add("the entity the older writer spawned is visible");
        }

        if (!reader.TryOpen(ids[3], out _))
        {
            problems.Add("the entity the older writer destroyed is gone");
        }

        // The set, not a count: the spawn's +1 and the destroy's -1 cancel in a count. A query's scan path may also lose the destroyed entity on its
        // own, through the occupancy bit the destroy clears (#1247).
        var listed = reader.Query<SrAccountArch>().Execute();
        if (!listed.SetEquals(ids))
        {
            problems.Add($"the reader's query lists {listed.Count} entities, not exactly its 10: " +
                         $"{(listed.Contains(spawned) ? "the spawn is in, " : "")}{(listed.Contains(ids[3]) ? "" : "the destroyed one is out")}");
        }

        Assert.That(problems, Is.Empty);
    }

    [Test]
    [CancelAfter(10_000)]
    public void ASnapshot_ReadWhileAnotherThreadCommits_ReadsTheSameValues([Values(100, 1000)] int count, [Values(false, true)] bool bulkLoaded)
    {
        using var dbe = Engine(count, bulkLoaded, out var ids);
        using var reader = dbe.CreateReadOnlyTransaction();
        var stop = 0;
        var commits = 0;
        Exception writerFault = null;
        using var firstCommit = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            try
            {
                for (var r = 0; Volatile.Read(ref stop) == 0; r++)
                {
                    Bump(dbe, ids, r);
                    Interlocked.Increment(ref commits);
                    firstCommit.Set();
                }
            }
            catch (Exception e)
            {
                writerFault = e;   // a raw thread's exception would end the test host
                firstCommit.Set();
            }
        });
        writer.Start();

        // The writer is younger than the reader, so none of its commits may show. Sweep only once it has committed, and count the commits that land
        // during the sweeps: they are the overlap the test is about.
        string problem;
        int during;
        try
        {
            Assert.That(firstCommit.Wait(TimeSpan.FromSeconds(5)), Is.True, "premise: the writer committed");
            var before = Volatile.Read(ref commits);
            problem = null;
            var sweeping = System.Diagnostics.Stopwatch.StartNew();
            for (var sweep = 0; problem == null && Volatile.Read(ref commits) - before < 5 && sweeping.Elapsed < TimeSpan.FromSeconds(5); sweep++)
            {
                problem = Sweep(reader, ids, 1000, 10, $"sweep {sweep}");
            }

            during = Volatile.Read(ref commits) - before;
        }
        finally
        {
            // Every path stops the writer before the engine is disposed under it.
            Volatile.Write(ref stop, 1);
            Assert.That(writer.Join(TimeSpan.FromSeconds(10)), Is.True, "the writer did not stop");
        }

        Assert.That(writerFault, Is.Null, $"the writer failed: {writerFault}");
        Assert.That(problem, Is.Null);
        Assert.That(during, Is.GreaterThan(0), "premise: commits landed while the reader swept");
    }
}
