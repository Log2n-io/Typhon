using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>Test component with an indexed String64 field — used to verify statistics gracefully handle unsupported key types.</summary>
[Component("Typhon.Schema.UnitTest.CompStr64", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompStr64
{
    [Index]
    public String64 Name;
    public int Value;

    public CompStr64(string name, int value)
    {
        Name.AsString = name;
        Value = value;
    }
}

[Archetype]
class CompStr64Arch : Archetype<CompStr64Arch>
{
    public static readonly Comp<CompStr64> Str64 = Register<CompStr64>();
}

class StatisticsRebuildTests : TestBase<StatisticsRebuildTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private static void CreateAndCommitCompD(DatabaseEngine dbe, float a, int b, double c)
    {
        using var t = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        t.Spawn<CompDArch>(CompDArch.D.Set(in d));
        t.Commit();
    }

    private static void CreateAndCommitCompF(DatabaseEngine dbe, int gold, int rank)
    {
        using var t = dbe.CreateQuickTransaction();
        var f = new CompF(gold, rank);
        t.Spawn<CompFArch>(CompFArch.F.Set(in f));
        t.Commit();
    }
    /// <summary>The statistics the worker actually publishes into — it rebuilds per archetype (StatisticsWorker:188-207), not per ComponentTable.</summary>
    private static IndexStatistics[] ArchStats(DatabaseEngine dbe, ComponentTable ct)
    {
        // Resolved by ARCHETYPE, not by searching for any archetype holding the component: CompD alone sits in three archetypes, and the search returned
        // whichever came first — typically one with no entities, so every distribution came back empty.
        var name = ct.Definition.Name;
        if (name.Contains("CompStr64")) { return IndexTestHelpers.ArchetypeIndexStats<CompStr64Arch>(dbe, ct); }
        return name.Contains("CompF")
            ? IndexTestHelpers.ArchetypeIndexStats<CompFArch>(dbe, ct)
            : IndexTestHelpers.ArchetypeIndexStats<CompDArch>(dbe, ct);
    }

    /// <summary>
    /// Rebuilds through the ARCHETYPE. <c>StatisticsRebuilder.RebuildAll(ct, …)</c> scans the ComponentTable's segment, where a cluster-backed archetype has
    /// no entities — the scan samples nothing and publishes statistics describing an empty population (#665).
    /// </summary>
    private static void RebuildStats(DatabaseEngine dbe, ComponentTable ct, int interval = 1)
    {
        // The cluster scan reads the archetype's ACTIVE cluster list, which is settled at the tick fence — without one, a just-spawned population is not yet
        // visible to the rebuilder and it publishes statistics over nothing.
        dbe.WriteTickFence(Interlocked.Increment(ref _tick));
        StatisticsRebuilder.RebuildClusterAll(ClusterOf(dbe, ct), dbe.EpochManager, interval);
    }

    private static int _tick;

    /// <summary>
    /// The cluster state whose counter the write path actually moves. <c>ComponentTable.MutationsSinceRebuild</c> is only incremented by the flat index
    /// maintainer, which a cluster-backed archetype never reaches — so it reads 0 no matter how much index work happened (#665).
    /// </summary>
    private static ArchetypeClusterState ClusterOf(DatabaseEngine dbe, ComponentTable ct)
    {
        var archetypeId = ct.Definition.Name.Contains("CompF")
            ? ArchetypeRegistry.GetMetadata<CompFArch>().ArchetypeId
            : ArchetypeRegistry.GetMetadata<CompDArch>().ArchetypeId;
        return dbe._archetypeStates[archetypeId].ClusterState;
    }


    [Test]
    public void RebuildAll_PopulatesHLL_MCV_Histogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with B from 0 to 199
        for (int i = 0; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        RebuildStats(dbe, ct);

        // HLL should estimate ~200 distinct values
        var stats = ArchStats(dbe, ct)[1]; // B field
        Assert.That(stats.HyperLogLog, Is.Not.Null);
        Assert.That(stats.DistinctValues, Is.InRange(180, 220));

        // MCV should be populated
        Assert.That(stats.MostCommonValues, Is.Not.Null);

        // Histogram should be populated and correct
        Assert.That(stats.Histogram, Is.Not.Null);
        Assert.That(stats.Histogram.TotalCount, Is.EqualTo(200));
    }

    [Test]
    public void RebuildAll_SmallSegment_UnderStride_SamplesDataPage1_NotEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // ~50 entities — few enough that they all live on segment DATA page 1 (the v4 directory-only root holds no chunks, so the
        // first data page is page 1).
        for (int i = 0; i < 50; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // Sample with an EVEN page stride. Under v4 the root (page 0) holds zero chunks, so a stride starting at page 0 would sample
        // pages 0,2,4,... and entirely SKIP page 1 — where every entity lives — yielding zero samples and empty statistics. The fix
        // starts sampling at the first DATA page. This proves the small-segment sampled path produces non-empty stats.
        RebuildStats(dbe, ct, interval: 2);

        var stats = ArchStats(dbe, ct)[1]; // B field
        Assert.That(stats.HyperLogLog, Is.Not.Null, "sampling a small segment must reach data page 1 — not skip it under an even stride starting at the empty root");
        Assert.That(stats.DistinctValues, Is.GreaterThan(0), "the sampled small segment must yield distinct values, not an empty estimate");
        Assert.That(stats.Histogram, Is.Not.Null);
        Assert.That(stats.Histogram.TotalCount, Is.GreaterThan(0), "the histogram must be populated from data page 1");
    }

    [Test]
    public void RebuildAll_AllIndexedFields_SinglePass()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, i * 1.5f, i, i * 2.5);
        }

        RebuildStats(dbe, ct);

        // All 3 indexed fields (A, B, C) should have HLL, MCV, and Histogram
        for (int f = 0; f < ArchStats(dbe, ct).Length; f++)
        {
            Assert.That(ArchStats(dbe, ct)[f].HyperLogLog, Is.Not.Null, $"Field {f} HLL missing");
            Assert.That(ArchStats(dbe, ct)[f].MostCommonValues, Is.Not.Null, $"Field {f} MCV missing");
            Assert.That(ArchStats(dbe, ct)[f].Histogram, Is.Not.Null, $"Field {f} Histogram missing");
            Assert.That(ArchStats(dbe, ct)[f].Histogram.TotalCount, Is.EqualTo(100), $"Field {f} Histogram count wrong");
        }
    }

    [Test]
    public void RebuildAll_SkewedData_MCVCapturesTopValues()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompF>();

        // Gold (AllowMultiple): 80 entities with Gold=42, 20 with Gold=99
        for (int i = 0; i < 80; i++)
        {
            CreateAndCommitCompF(dbe, 42, i);
        }
        for (int i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 99, 80 + i);
        }

        RebuildStats(dbe, ct);

        var mcv = ArchStats(dbe, ct)[0].MostCommonValues; // Gold field (index 0)
        Assert.That(mcv, Is.Not.Null);

        Assert.That(mcv.TryGetCount(42, out long count42), Is.True);
        Assert.That(count42, Is.EqualTo(80));

        Assert.That(mcv.TryGetCount(99, out long count99), Is.True);
        Assert.That(count99, Is.EqualTo(20));
    }

    [Test]
    public void RebuildAll_AtomicSwap_NoTornReads()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // First rebuild
        RebuildStats(dbe, ct);
        var firstHll = ArchStats(dbe, ct)[1].HyperLogLog;
        var firstMcv = ArchStats(dbe, ct)[1].MostCommonValues;
        var firstHisto = ArchStats(dbe, ct)[1].Histogram;

        Assert.That(firstHll, Is.Not.Null);
        Assert.That(firstMcv, Is.Not.Null);
        Assert.That(firstHisto, Is.Not.Null);

        // Add more data and rebuild again
        for (int i = 100; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        RebuildStats(dbe, ct);

        // New references should be different objects (atomic swap, not in-place mutation)
        Assert.That(ArchStats(dbe, ct)[1].HyperLogLog, Is.Not.SameAs(firstHll));
        Assert.That(ArchStats(dbe, ct)[1].MostCommonValues, Is.Not.SameAs(firstMcv));
        Assert.That(ArchStats(dbe, ct)[1].Histogram, Is.Not.SameAs(firstHisto));
        Assert.That(ArchStats(dbe, ct)[1].Histogram.TotalCount, Is.EqualTo(200));
    }

    [Test]
    public void MutationCounter_IncrementedOnIndexChange()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.EqualTo(0));

        // Create increments
        EntityId id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 1.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }
        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.GreaterThan(0));

        int afterCreate = ClusterOf(dbe, ct).MutationsSinceRebuild;

        // Update with changed index field increments further
        using var t2 = dbe.CreateQuickTransaction();
        var d2 = new CompD(2.0f, 20, 2.0); // all fields changed
        ref var w = ref t2.OpenMut(id).Write(CompDArch.D);
        w = d2;
        t2.Commit();

        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.GreaterThan(afterCreate));
    }

    [Test]
    public void MutationCounter_ResetByWorkerSimulation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 10; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.GreaterThan(0));

        // Simulate what StatisticsWorker does: reset before rebuild
        ClusterOf(dbe, ct).MutationsSinceRebuild = 0;
        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.EqualTo(0));
    }

    [Test]
    public void RebuildAll_EmptyTable_NoOp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // Should not throw on empty table
        RebuildStats(dbe, ct);

        // No statistics built (0 entities)
        Assert.That(ArchStats(dbe, ct)[1].HyperLogLog, Is.Null);
    }

    [Test]
    public void RebuildAll_AllowMultiple_CountsEntities()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompF>();

        // Gold is AllowMultiple: 30 entities with Gold=10, 20 with Gold=50
        for (int i = 0; i < 30; i++)
        {
            CreateAndCommitCompF(dbe, 10, i);
        }
        for (int i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 50, 30 + i);
        }

        RebuildStats(dbe, ct);

        // Histogram total should count entities, not distinct keys
        var stats = ArchStats(dbe, ct)[0]; // Gold field
        Assert.That(stats.Histogram.TotalCount, Is.EqualTo(50));

        // MCV should capture both values
        Assert.That(stats.MostCommonValues.TryGetCount(10, out long c10), Is.True);
        Assert.That(c10, Is.EqualTo(30));
        Assert.That(stats.MostCommonValues.TryGetCount(50, out long c50), Is.True);
        Assert.That(c50, Is.EqualTo(20));
    }

    [Test]
    public void Worker_StartsAndStops_Lifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var options = new StatisticsOptions { Enabled = true, PollIntervalMs = 100 };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();

        Assert.That(worker.IsRunning, Is.True);

        worker.Dispose();

        // After dispose, thread should have stopped (allow brief delay)
        Assert.That(worker.IsRunning, Is.False);
    }

    [Test]
    public void Worker_ForceRebuild_WakesImmediately()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // Set mutation count above threshold
        ClusterOf(dbe, ct).MutationsSinceRebuild = 2000;

        var options = new StatisticsOptions
        {
            Enabled = true,
            PollIntervalMs = 60000, // Very long poll — won't trigger naturally
            MutationThreshold = 1000,
            MinEntitiesForRebuild = 50
        };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();

        // Force rebuild should wake the thread immediately
        worker.ForceRebuild();

        // Wait briefly for the rebuild to complete
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ArchStats(dbe, ct)[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        Assert.That(ArchStats(dbe, ct)[1].HyperLogLog, Is.Not.Null, "ForceRebuild should trigger statistics rebuild");
    }

    // ═══════════════════════════════════════════════════════════════
    // C1 regression: String64 indexed fields must not crash the rebuilder
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void RebuildAll_String64IndexedField_SkipsGracefully()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompStr64>();
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompStr64>();

        // Insert some entities with String64 indexed field
        for (int i = 0; i < 50; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var c = new CompStr64($"name_{i}", i);
            t.Spawn<CompStr64Arch>(CompStr64Arch.Str64.Set(in c));
            t.Commit();
        }

        // RebuildAll must NOT throw — it should skip the String64 field
        Assert.DoesNotThrow(() => RebuildStats(dbe, ct));

        // String64 field should have no statistics (skipped)
        Assert.That(ArchStats(dbe, ct)[0].HyperLogLog, Is.Null);
        Assert.That(ArchStats(dbe, ct)[0].MostCommonValues, Is.Null);
        Assert.That(ArchStats(dbe, ct)[0].Histogram, Is.Null);
    }

    [Test]
    public void Worker_String64Table_DoesNotKillWorker()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompStr64>();
        dbe.InitializeArchetypes();

        var ctStr = dbe.GetComponentTable<CompStr64>();
        var ctD = dbe.GetComponentTable<CompD>();

        // Populate both tables
        for (int i = 0; i < 100; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var s = new CompStr64($"name_{i}", i);
            t.Spawn<CompStr64Arch>(CompStr64Arch.Str64.Set(in s));
            t.Commit();
        }
        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // Drive the counters the worker actually reads. Its ComponentTable sweep is gone (#629); the surviving sweep is per ARCHETYPE, so the threshold has to
        // be crossed on each archetype's ClusterState.
        // Resolved BY ARCHETYPE, not by searching for "whichever archetype indexes CompD" — three of them do, and a search returns the first, which is not
        // the one this test spawns into.
        dbe._archetypeStates[Archetype<CompStr64Arch>.Metadata.ArchetypeId].ClusterState.MutationsSinceRebuild = 2000;
        dbe._archetypeStates[Archetype<CompDArch>.Metadata.ArchetypeId].ClusterState.MutationsSinceRebuild = 2000;

        var options = new StatisticsOptions
        {
            Enabled = true,
            PollIntervalMs = 60000,
            MutationThreshold = 1000,
            MinEntitiesForRebuild = 50
        };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();
        worker.ForceRebuild();

        // Wait for the per-ARCHETYPE rebuild on CompD. The worker's ComponentTable sweep is gone (#629): it scanned ComponentSegment, where a cluster-backed
        // archetype keeps no entities, into an array no estimator reads — and it could never fire anyway, since ComponentTable.MutationsSinceRebuild was
        // never incremented by any write path. What the test still proves is the thing it was written for: a String64 table in the same engine does not kill
        // the worker before it reaches the others.
        var statsD = IndexTestHelpers.ArchetypeIndexStats<CompDArch>(dbe, ctD);
        Assert.That(statsD, Is.Not.Null, "premise: CompDArch owns statistics for CompD");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (statsD[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        Assert.That(worker.IsRunning, Is.True, "Worker should survive String64 table processing");
        Assert.That(statsD[1].HyperLogLog, Is.Not.Null, "CompD stats should be rebuilt");
    }

    // ═══════════════════════════════════════════════════════════════
    // C2 regression: Float/double fields get full statistics via order-preserving encoding
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void RebuildAll_FloatField_GetsFullStatistics_WithOrderPreservingHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // Insert entities with float A spanning negative-to-positive range
        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, -50.0f + i, i, -25.0 + i * 0.5);
        }

        RebuildStats(dbe, ct);

        // Float field (A, index 0): all three statistics should work
        Assert.That(ArchStats(dbe, ct)[0].HyperLogLog, Is.Not.Null, "Float field should have HLL");
        Assert.That(ArchStats(dbe, ct)[0].MostCommonValues, Is.Not.Null, "Float field should have MCV");
        Assert.That(ArchStats(dbe, ct)[0].Histogram, Is.Not.Null, "Float field should have histogram (order-preserving encoding)");
        Assert.That(ArchStats(dbe, ct)[0].Histogram.TotalCount, Is.EqualTo(100));
        Assert.That(ArchStats(dbe, ct)[0].DistinctValues, Is.InRange(90, 110), "Float HLL should estimate ~100 distinct values");

        // Double field (C, index 2): same — full statistics with order-preserving histogram
        Assert.That(ArchStats(dbe, ct)[2].HyperLogLog, Is.Not.Null, "Double field should have HLL");
        Assert.That(ArchStats(dbe, ct)[2].MostCommonValues, Is.Not.Null, "Double field should have MCV");
        Assert.That(ArchStats(dbe, ct)[2].Histogram, Is.Not.Null, "Double field should have histogram (order-preserving encoding)");
        Assert.That(ArchStats(dbe, ct)[2].Histogram.TotalCount, Is.EqualTo(100));

        // Int field (B, index 1): should have all three
        Assert.That(ArchStats(dbe, ct)[1].Histogram, Is.Not.Null, "Int field should have histogram");
        Assert.That(ArchStats(dbe, ct)[1].Histogram.TotalCount, Is.EqualTo(100));
    }

    // ═══════════════════════════════════════════════════════════════
    // C3 regression: NavigationView requires target predicates for ToView
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void NavigationView_ToView_NoTargetPredicates_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
        dbe.InitializeArchetypes();

        // Create a guild and player
        using (var t = dbe.CreateQuickTransaction())
        {
            var g = new CompGuild(10, 100);
            var guildEid = t.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(in g));
            var p = new CompPlayer((long)guildEid.RawValue, true);
            t.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(in p));
            t.Commit();
        }

        // Attempting to create a navigation view with only source predicates should throw
        using var txNav = dbe.CreateQuickTransaction();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            txNav.Query<CompPlayerArch>()
                .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
                .Where((p, g) => p.Active == 1)
                .ToView();
        });

        Assert.That(ex.Message, Does.Contain("target predicate"));
    }

    [Test]
    public void NavigationQuery_OneShot_NoTargetPredicates_Works()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<CompGuild>();
        dbe.RegisterComponentFromAccessor<CompPlayer>();
        dbe.InitializeArchetypes();

        long guildPk;
        using (var t = dbe.CreateQuickTransaction())
        {
            var g = new CompGuild(10, 100);
            var guildEid = t.Spawn<CompGuildArch>(CompGuildArch.Guild.Set(in g));
            guildPk = (long)guildEid.RawValue;
            var p = new CompPlayer(guildPk, true);
            t.Spawn<CompPlayerArch>(CompPlayerArch.Player.Set(in p));
            t.Commit();
        }

        // One-shot Execute with only source predicates should work (no incremental tracking needed)
        using var tx = dbe.CreateQuickTransaction();
        var result = tx.Query<CompPlayerArch>()
            .NavigateField<CompPlayer, CompGuild>(p => p.GuildId)
            .Where((p, g) => p.Active == 1)
            .Execute();

        Assert.That(result.Count, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // C4 regression: Worker per-table isolation and counter reset after rebuild
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Worker_CounterResetAfterRebuild_NotBefore()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        ClusterOf(dbe, ct).MutationsSinceRebuild = 2000;

        var options = new StatisticsOptions
        {
            Enabled = true,
            PollIntervalMs = 60000,
            MutationThreshold = 1000,
            MinEntitiesForRebuild = 50
        };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);
        worker.Start();
        worker.ForceRebuild();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ArchStats(dbe, ct)[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        // After successful rebuild, counter should be reset
        Assert.That(ArchStats(dbe, ct)[1].HyperLogLog, Is.Not.Null);
        Assert.That(ClusterOf(dbe, ct).MutationsSinceRebuild, Is.LessThan(2000), "Counter should be reset after successful rebuild");
    }

    [Test]
    public void Worker_LastError_ExposedForDiagnostics()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var options = new StatisticsOptions { Enabled = true, PollIntervalMs = 100 };
        using var worker = new StatisticsWorker(dbe, options, dbe.EpochManager, dbe);

        // Before any errors, LastError should be null
        Assert.That(worker.LastError, Is.Null);
    }

    [Test]
    public void RebuildAll_WithSampling_ReasonableEstimate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // Insert 200 entities with B from 0 to 199
        for (int i = 0; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, i * 1.0f, i, i * 2.0);
        }

        // Sample every other page (pageInterval: 2)
        RebuildStats(dbe, ct, interval: 2);

        var stats = ArchStats(dbe, ct)[1]; // B field
        Assert.That(stats.HyperLogLog, Is.Not.Null, "HLL should be populated even with sampling");

        // HLL only sees sampled pages (~half the entities with pageInterval=2), so its raw estimate is ~100.
        // The key invariant: HLL is populated and gives a positive estimate.
        long hllEstimate = stats.DistinctValues;
        Assert.That(hllEstimate, Is.GreaterThan(40), $"HLL estimate {hllEstimate} should reflect sampled entities");
        Assert.That(hllEstimate, Is.LessThan(200), $"HLL estimate {hllEstimate} should be less than total (only sampled half)");

        // Histogram should be populated with scaled counts (scaleFactor ~ 2x)
        Assert.That(stats.Histogram, Is.Not.Null, "Histogram should be populated even with sampling");
        Assert.That(stats.Histogram.TotalCount, Is.InRange(120, 280), $"Histogram total {stats.Histogram.TotalCount} should be scaled toward 200");
    }
}
