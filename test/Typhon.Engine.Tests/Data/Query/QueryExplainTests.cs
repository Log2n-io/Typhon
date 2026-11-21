using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Explain.Levels", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ExLevels
{
    [Index(AllowMultiple = true)] public int Level;
    public int Payload;
}

[Component("Typhon.Test.Explain.Tag", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ExTag
{
    [Index(AllowMultiple = true)] public int Code;
}

[Archetype]
class ExArch : Archetype<ExArch>
{
    public static readonly Comp<ExLevels> Levels = Register<ExLevels>();
}

[Archetype]
class ExChild : Archetype<ExChild, ExArch>
{
    public static readonly Comp<ExTag> Tag = Register<ExTag>();
}

/// <summary>
/// <see cref="EcsQuery{TArchetype}.Explain"/> reports the path the run took, cross-checked against the engine's own path counters (#1643).
/// </summary>
[TestFixture]
[NonParallelizable]   // QueryPathProbe is thread-static, but Forced must not leak into another test on this worker
class QueryExplainTests : TestBase<QueryExplainTests>
{
    private const int RowCount = 200;
    private const string LevelIndex = "Typhon.Test.Explain.Levels.Level";

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ExLevels>();
        dbe.RegisterComponentFromAccessor<ExTag>();
        dbe.InitializeArchetypes();
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < RowCount; i++)
        {
            tx.Spawn<ExArch>(ExArch.Levels.Set(new ExLevels { Level = i % 10, Payload = i }));
        }
        for (var i = 0; i < 20; i++)
        {
            tx.Spawn<ExChild>(ExArch.Levels.Set(new ExLevels { Level = i % 10 }), ExChild.Tag.Set(new ExTag { Code = i }));
        }
        tx.Commit();
        return dbe;
    }

    [TearDown]
    public void ResetProbe() => QueryPathProbe.Reset();

    [Test]
    public void ForcedSelective_EqualityIsAnIndexSeek_RangeIsAnIndexRange()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        QueryPathProbe.Forced = ClusterScanPath.Selective;

        var seek = tx.QueryExact<ExArch>().WhereField<ExLevels>(l => l.Level == 3).Explain();
        var entry = seek.Archetypes.Single();
        Assert.That(entry.Path, Is.EqualTo(QueryAccessPath.IndexSeek), seek.ToString());
        Assert.That(entry.Index, Is.EqualTo(LevelIndex));
        Assert.That(entry.PushedDown, Is.EqualTo(new[] { "Level == 3" }));
        // The seek enforces Level == 3 by itself: the selective scan skips that evaluator, so it is pushed down and not evaluated per entity.
        Assert.That(entry.EvaluatedPerEntity, Is.Empty);
        Assert.That(entry.Note, Does.Contain("re-checked only on entities written since the last tick fence"));
        Assert.That(entry.ActualRows, Is.EqualTo(20));
        Assert.That(seek.ResultCount, Is.EqualTo(20));

        var range = tx.QueryExact<ExArch>().WhereField<ExLevels>(l => l.Level >= 3 && l.Level < 6).Explain();
        Assert.That(range.Archetypes.Single().Path, Is.EqualTo(QueryAccessPath.IndexRange), range.ToString());
        Assert.That(range.Archetypes.Single().PushedDown, Is.EqualTo(new[] { "Level in [3..5]" }));
        Assert.That(range.ResultCount, Is.EqualTo(60));

        // A != cannot narrow a range, so the range no longer vouches for its field: every predicate on it is evaluated per entity again.
        var widened = tx.QueryExact<ExArch>().WhereField<ExLevels>(l => l.Level >= 3 && l.Level != 4).Explain();
        var widenedEntry = widened.Archetypes.Single();
        Assert.That(widenedEntry.EvaluatedPerEntity, Is.EquivalentTo(new[] { "Level >= 3", "Level != 4" }), widened.ToString());
        Assert.That(widened.ResultCount, Is.EqualTo(120));
    }

    [Test]
    public void ForcedFullScan_IsAClusterScanWithNoIndexRange()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        QueryPathProbe.Forced = ClusterScanPath.FullScan;

        var x = tx.QueryExact<ExArch>().WhereField<ExLevels>(l => l.Level == 3).Explain();
        var entry = x.Archetypes.Single();
        Assert.That(entry.Path, Is.EqualTo(QueryAccessPath.ClusterScan), x.ToString());
        Assert.That(entry.Index, Is.Null);
        Assert.That(entry.PushedDown, Is.Empty);
        Assert.That(entry.ActualRows, Is.EqualTo(20));
    }

    /// <summary>Unforced, the planner decides; every recorded entry must be the branch the engine's own counters saw run.</summary>
    [Test]
    public void Planner_EveryEntryMatchesTheBranchThatRan([Values(0, 3)] int level)
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        foreach (var q in new[] { "eq", "range" })
        {
            QueryPathProbe.SelectiveScans = 0;
            QueryPathProbe.FullScans = 0;
            var x = q == "eq"
                ? tx.Query<ExArch>().WhereField<ExLevels>(l => l.Level == level).Explain()
                : tx.Query<ExArch>().WhereField<ExLevels>(l => l.Level >= level).Explain();

            var selective = x.Archetypes.Count(a => a.Path is QueryAccessPath.IndexSeek or QueryAccessPath.IndexRange);
            var cluster = x.Archetypes.Count(a => a.Path == QueryAccessPath.ClusterScan);
            Assert.That(selective, Is.EqualTo(QueryPathProbe.SelectiveScans), x.ToString());
            Assert.That(cluster, Is.EqualTo(QueryPathProbe.FullScans), x.ToString());
            Assert.That(selective + cluster, Is.EqualTo(2), "both archetypes of the subtree carry Levels");
            Assert.That(x.ResultCount, Is.EqualTo(x.Archetypes.Sum(a => a.ActualRows)), "no pending writes, no post-filter: the rows are the result");
        }
    }

    [Test]
    public void WhereFunc_IsABroadScanWithAPostFilter()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var x = tx.QueryExact<ExArch>().Where<ExLevels>(l => l.Payload < 50).Explain();
        var entry = x.Archetypes.Single();
        Assert.That(entry.Path, Is.EqualTo(QueryAccessPath.BroadScan), x.ToString());
        Assert.That(entry.EvaluatedPerEntity, Is.EqualTo(new[] { "Where(Func) post-filter" }));
        Assert.That(entry.ActualRows, Is.EqualTo(RowCount), "the map walk visits every entity before the filter");
        Assert.That(x.WhereFilterRemoved, Is.EqualTo(RowCount - 50));
        Assert.That(x.ResultCount, Is.EqualTo(50));
    }

    [Test]
    public void Count_TakesTheOccupancyPath_OrTheMapProbeWhenForced()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        QueryPathProbe.ForcedCount = ClusterCountPath.Planner;
        var fast = tx.QueryExact<ExArch>().Explain(ExplainTerminal.Count);
        Assert.That(fast.Archetypes.Single().Path, Is.EqualTo(QueryAccessPath.OccupancyCount), fast.ToString());
        Assert.That(fast.ResultCount, Is.EqualTo(RowCount));

        QueryPathProbe.ForcedCount = ClusterCountPath.MapProbe;
        var probe = tx.QueryExact<ExArch>().Explain(ExplainTerminal.Count);
        Assert.That(probe.Archetypes.Single().Path, Is.EqualTo(QueryAccessPath.BroadScan), probe.ToString());
        Assert.That(probe.Archetypes.Single().ActualRows, Is.EqualTo(RowCount));
    }

    [Test]
    public void Polymorphic_AnArchetypeWithoutTheComponentIsReportedSkipped()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var x = tx.Query<ExArch>().WhereField<ExTag>(t => t.Code < 5).Explain();
        Assert.That(x.Archetypes, Has.Count.EqualTo(2), x.ToString());
        var parent = x.Archetypes.Single(a => a.Path == QueryAccessPath.Skipped);
        Assert.That(parent.Note, Does.Contain("does not carry"));
        Assert.That(x.Archetypes.Single(a => a.Path != QueryAccessPath.Skipped).ActualRows, Is.EqualTo(5));
        Assert.That(x.ResultCount, Is.EqualTo(5));
    }

    [Test]
    public void PendingSpawns_AreCountedApart_AndAnEmptyMaskIsReported()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<ExArch>(ExArch.Levels.Set(new ExLevels { Level = 3 }));

        var x = tx.QueryExact<ExArch>().WhereField<ExLevels>(l => l.Level == 3).Explain();
        Assert.That(x.PendingSpawnRows, Is.EqualTo(1), x.ToString());
        Assert.That(x.ResultCount, Is.EqualTo(21));

        var broad = tx.QueryExact<ExArch>().Explain();
        Assert.That(broad.PendingSpawnRows, Is.EqualTo(1), broad.ToString());

        var empty = tx.Query<ExArch>().Without<ExLevels>().Explain();
        Assert.That(empty.QueryPath, Is.EqualTo(QueryAccessPath.Empty));
        Assert.That(empty.Archetypes, Is.Empty);
    }
}
