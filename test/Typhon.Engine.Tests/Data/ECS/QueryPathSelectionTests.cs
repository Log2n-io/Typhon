using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Schema

/// <summary>
/// <c>Fan</c> is <c>AllowMultiple</c> so its fan-out — rows per distinct key — is whatever the test spawns; <c>Uniq</c> is a unique index, whose fan-out is 1
/// by construction and which therefore must never be selected however the rows are shaped.
/// </summary>
[Component("Typhon.Test.QSel.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QSelData
{
    [Index(AllowMultiple = true)] public int Fan;
    [Index] public int Uniq;

    public QSelData(int fan, int uniq)
    {
        Fan = fan;
        Uniq = uniq;
    }
}

[Archetype]
class QSelUnit : Archetype<QSelUnit>
{
    public static readonly Comp<QSelData> Data = Register<QSelData>();
}

#endregion

/// <summary>
/// What the planner chooses when nobody forces it. <see cref="QueryPathEquivalenceTests"/> proves the two cluster scan paths AGREE; this fixture proves the
/// planner picks between them on the property that was measured — the primary index's fan-out — and that whichever it picks answers the same question.
/// </summary>
/// <remarks>
/// <para>
/// The threshold is stated in CLUSTERS (a key's rows must fill at least two), so every case below derives its row count from the archetype's actual
/// <c>ClusterSize</c> rather than assuming 64. A test that hard-codes the row count silently stops testing the boundary the day a component's size changes
/// its cluster geometry.
/// </para>
/// <para>
/// <b>These assertions do not need statistics forced current, and that is the point.</b> The selectivity estimate this replaced was published by a background
/// worker on a mutation threshold, so a freshly-spawned fixture had none, the estimator returned its "unknown" fallback, and the planner took Path B for a
/// reason unrelated to the threshold — a naive test passed at ANY value. Fan-out is read from the tree's own entry count and the archetype's EntityMap, both
/// live, so what the planner sees here is what it sees in production.
/// </para>
/// </remarks>
[TestFixture]
class QueryPathSelectionTests : TestBase<QueryPathSelectionTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QSelData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static int ClusterSizeOf(DatabaseEngine dbe)
        => dbe._archetypeStates[ArchetypeRegistry.GetMetadata<QSelUnit>().ArchetypeId].ClusterState.Layout.ClusterSize;

    /// <summary>Spawns <paramref name="rows"/> entities spread over <paramref name="distinctKeys"/> values of <c>Fan</c>, cycling so no key is contiguous.</summary>
    private static void Spawn(DatabaseEngine dbe, int rows, int distinctKeys)
    {
        var written = 0;
        while (written < rows)
        {
            var batch = Math.Min(500, rows - written);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var idx = written + i;
                var d = new QSelData(idx % distinctKeys, idx);
                tx.Spawn<QSelUnit>(QSelUnit.Data.Set(in d));
            }

            tx.Commit();
            written += batch;
        }

        dbe.WriteTickFence(0);
    }

    /// <summary>Runs <paramref name="run"/> with the planner left alone and reports which cluster scan it chose.</summary>
    private static HashSet<EntityId> Planned(Func<HashSet<EntityId>> run, out int selectiveScans, out int fullScans)
    {
        QueryPathProbe.Reset();
        try
        {
            var result = run();
            selectiveScans = QueryPathProbe.SelectiveScans;
            fullScans = QueryPathProbe.FullScans;
            return result;
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    private static HashSet<EntityId> Forced(Func<HashSet<EntityId>> run, ClusterScanPath path)
    {
        QueryPathProbe.Reset();
        QueryPathProbe.Forced = path;
        try
        {
            return run();
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>Fan-out at exactly the threshold selects the selective scan — and answers what the full scan answers.</summary>
    [Test]
    public void AtTheFanOutThreshold_ThePlannerTakesTheSelectiveScan()
    {
        var dbe = SetupEngine();
        const int keys = 4;
        var rows = keys * 2 * ClusterSizeOf(dbe);   // fan-out == 2 clusters per key, the threshold itself
        Spawn(dbe, rows, keys);

        using var tx = dbe.CreateQuickTransaction();
        HashSet<EntityId> Run() => tx.Query<QSelUnit>().WhereField<QSelData>(d => d.Fan >= 2).Execute();

        var planned = Planned(Run, out var selective, out var full);
        var fullScan = Forced(Run, ClusterScanPath.FullScan);

        Assert.Multiple(() =>
        {
            Assert.That(selective, Is.GreaterThan(0), $"fan-out is {rows / keys} rows per key over {rows} rows — the planner should take Path A");
            Assert.That(full, Is.Zero, "and should not have also run a full scan for the same archetype");
            Assert.That(planned, Is.EquivalentTo(fullScan), "whichever path it picked must answer what the other one answers");
            Assert.That(planned, Has.Count.EqualTo(rows / 2), "sanity: Fan >= 2 of 0..3 is half the rows");
        });
    }

    /// <summary>One row per key below the threshold flips the decision — the boundary is real, not a range the test happens to sit inside.</summary>
    [Test]
    public void JustBelowTheFanOutThreshold_ThePlannerTakesTheFullScan()
    {
        var dbe = SetupEngine();
        const int keys = 4;
        var rows = (keys * 2 * ClusterSizeOf(dbe)) - 1;
        Spawn(dbe, rows, keys);

        using var tx = dbe.CreateQuickTransaction();
        HashSet<EntityId> Run() => tx.Query<QSelUnit>().WhereField<QSelData>(d => d.Fan >= 2).Execute();

        var planned = Planned(Run, out var selective, out var full);
        var fullScan = Forced(Run, ClusterScanPath.FullScan);

        Assert.Multiple(() =>
        {
            Assert.That(selective, Is.Zero, $"one row short of the threshold ({rows} rows over {keys} keys) must not select Path A");
            Assert.That(full, Is.GreaterThan(0), "and must actually have scanned — zero/zero would prove nothing");
            Assert.That(planned, Is.EquivalentTo(fullScan));
        });
    }

    /// <summary>
    /// A unique index is never selected however many rows there are: one entry per row is fan-out 1, Path A's worst case.
    /// </summary>
    [Test]
    public void AUniqueIndexNeverSelectsTheSelectiveScan()
    {
        var dbe = SetupEngine();
        const int keys = 4;
        var rows = keys * 8 * ClusterSizeOf(dbe);   // four times the threshold — on the FAN column. The predicate below is on the unique one.
        Spawn(dbe, rows, keys);

        using var tx = dbe.CreateQuickTransaction();
        HashSet<EntityId> Run() => tx.Query<QSelUnit>().WhereField<QSelData>(d => d.Uniq >= rows - 10).Execute();

        var planned = Planned(Run, out var selective, out var full);

        Assert.Multiple(() =>
        {
            Assert.That(selective, Is.Zero, "a unique index stores one entry per row, so its fan-out is 1 regardless of the archetype's size");
            Assert.That(full, Is.GreaterThan(0));
            Assert.That(planned, Is.EquivalentTo(Forced(Run, ClusterScanPath.FullScan)));
        });
    }

    /// <summary>
    /// Fan-out above the threshold is not sufficient: a range that does not exactly implement the predicate leaves Path A re-evaluating it per cluster, which
    /// is Path B's work plus a tree scan.
    /// </summary>
    /// <remarks>
    /// <c>!=</c> is the clearest case — <c>KeyRange</c> folds it into the whole key space, a strict SUPERSET of the matching rows — so the selective scan
    /// would have to test every row it collected. The planner must decline it however good the fan-out is.
    /// </remarks>
    [Test]
    public void AnInexactRangeDeclinesTheSelectiveScanEvenAtHighFanOut()
    {
        var dbe = SetupEngine();
        const int keys = 4;
        var rows = keys * 8 * ClusterSizeOf(dbe);
        Spawn(dbe, rows, keys);

        using var tx = dbe.CreateQuickTransaction();
        HashSet<EntityId> Run() => tx.Query<QSelUnit>().WhereField<QSelData>(d => d.Fan != 2).Execute();

        var planned = Planned(Run, out var selective, out _);

        Assert.Multiple(() =>
        {
            Assert.That(selective, Is.Zero, "the scan range is a superset of the matches, so Path A cannot skip the predicate and cannot win");
            Assert.That(planned, Is.EquivalentTo(Forced(Run, ClusterScanPath.FullScan)));
            Assert.That(planned, Has.Count.EqualTo(rows - (rows / keys)), "sanity: != 2 excludes exactly one key's worth of rows");
        });
    }

    /// <summary>An empty archetype selects the full scan rather than dividing by a row count of zero.</summary>
    [Test]
    public void AnEmptyArchetypeTakesTheFullScan()
    {
        var dbe = SetupEngine();
        dbe.WriteTickFence(0);

        using var tx = dbe.CreateQuickTransaction();
        var planned = Planned(() => tx.Query<QSelUnit>().WhereField<QSelData>(d => d.Fan >= 2).Execute(), out var selective, out _);

        Assert.Multiple(() =>
        {
            Assert.That(selective, Is.Zero);
            Assert.That(planned, Is.Empty);
        });
    }
}
