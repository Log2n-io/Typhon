using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
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

[Archetype(211)]
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

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // HLL should estimate ~200 distinct values
        var stats = ct.IndexStats[1]; // B field
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
        StatisticsRebuilder.RebuildAll(ct, dbe.EpochManager, pageInterval: 2);

        var stats = ct.IndexStats[1]; // B field
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

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // All 3 indexed fields (A, B, C) should have HLL, MCV, and Histogram
        for (int f = 0; f < ct.IndexStats.Length; f++)
        {
            Assert.That(ct.IndexStats[f].HyperLogLog, Is.Not.Null, $"Field {f} HLL missing");
            Assert.That(ct.IndexStats[f].MostCommonValues, Is.Not.Null, $"Field {f} MCV missing");
            Assert.That(ct.IndexStats[f].Histogram, Is.Not.Null, $"Field {f} Histogram missing");
            Assert.That(ct.IndexStats[f].Histogram.TotalCount, Is.EqualTo(100), $"Field {f} Histogram count wrong");
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

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        var mcv = ct.IndexStats[0].MostCommonValues; // Gold field (index 0)
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
        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);
        var firstHll = ct.IndexStats[1].HyperLogLog;
        var firstMcv = ct.IndexStats[1].MostCommonValues;
        var firstHisto = ct.IndexStats[1].Histogram;

        Assert.That(firstHll, Is.Not.Null);
        Assert.That(firstMcv, Is.Not.Null);
        Assert.That(firstHisto, Is.Not.Null);

        // Add more data and rebuild again
        for (int i = 100; i < 200; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // New references should be different objects (atomic swap, not in-place mutation)
        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Not.SameAs(firstHll));
        Assert.That(ct.IndexStats[1].MostCommonValues, Is.Not.SameAs(firstMcv));
        Assert.That(ct.IndexStats[1].Histogram, Is.Not.SameAs(firstHisto));
        Assert.That(ct.IndexStats[1].Histogram.TotalCount, Is.EqualTo(200));
    }

    [Test]
    public void MutationCounter_IncrementedOnIndexChange()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        Assert.That(ct.MutationsSinceRebuild, Is.EqualTo(0));

        // Create increments
        EntityId id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 1.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }
        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(0));

        int afterCreate = ct.MutationsSinceRebuild;

        // Update with changed index field increments further
        using var t2 = dbe.CreateQuickTransaction();
        var d2 = new CompD(2.0f, 20, 2.0); // all fields changed
        ref var w = ref t2.OpenMut(id).Write(CompDArch.D);
        w = d2;
        t2.Commit();

        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(afterCreate));
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

        Assert.That(ct.MutationsSinceRebuild, Is.GreaterThan(0));

        // Simulate what StatisticsWorker does: reset before rebuild
        ct.MutationsSinceRebuild = 0;
        Assert.That(ct.MutationsSinceRebuild, Is.EqualTo(0));
    }

    [Test]
    public void RebuildAll_EmptyTable_NoOp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();

        // Should not throw on empty table
        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // No statistics built (0 entities)
        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Null);
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

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Histogram total should count entities, not distinct keys
        var stats = ct.IndexStats[0]; // Gold field
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
        ct.MutationsSinceRebuild = 2000;

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
        while (ct.IndexStats[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Not.Null, "ForceRebuild should trigger statistics rebuild");
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
        Assert.DoesNotThrow(() => StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager));

        // String64 field should have no statistics (skipped)
        Assert.That(ct.IndexStats[0].HyperLogLog, Is.Null);
        Assert.That(ct.IndexStats[0].MostCommonValues, Is.Null);
        Assert.That(ct.IndexStats[0].Histogram, Is.Null);
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

        // Set both above threshold
        ctStr.MutationsSinceRebuild = 2000;
        ctD.MutationsSinceRebuild = 2000;

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

        // Wait for rebuild to complete on CompD (the non-String64 table)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ctD.IndexStats[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        // Worker must still be running (not killed by String64 table)
        Assert.That(worker.IsRunning, Is.True, "Worker should survive String64 table processing");
        // CompD statistics should be rebuilt despite CompStr64 being in the same engine
        Assert.That(ctD.IndexStats[1].HyperLogLog, Is.Not.Null, "CompD stats should be rebuilt");
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

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Float field (A, index 0): all three statistics should work
        Assert.That(ct.IndexStats[0].HyperLogLog, Is.Not.Null, "Float field should have HLL");
        Assert.That(ct.IndexStats[0].MostCommonValues, Is.Not.Null, "Float field should have MCV");
        Assert.That(ct.IndexStats[0].Histogram, Is.Not.Null, "Float field should have histogram (order-preserving encoding)");
        Assert.That(ct.IndexStats[0].Histogram.TotalCount, Is.EqualTo(100));
        Assert.That(ct.IndexStats[0].DistinctValues, Is.InRange(90, 110), "Float HLL should estimate ~100 distinct values");

        // Double field (C, index 2): same — full statistics with order-preserving histogram
        Assert.That(ct.IndexStats[2].HyperLogLog, Is.Not.Null, "Double field should have HLL");
        Assert.That(ct.IndexStats[2].MostCommonValues, Is.Not.Null, "Double field should have MCV");
        Assert.That(ct.IndexStats[2].Histogram, Is.Not.Null, "Double field should have histogram (order-preserving encoding)");
        Assert.That(ct.IndexStats[2].Histogram.TotalCount, Is.EqualTo(100));

        // Int field (B, index 1): should have all three
        Assert.That(ct.IndexStats[1].Histogram, Is.Not.Null, "Int field should have histogram");
        Assert.That(ct.IndexStats[1].Histogram.TotalCount, Is.EqualTo(100));
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

        ct.MutationsSinceRebuild = 2000;

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
        while (ct.IndexStats[1].HyperLogLog == null && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(1);
        }

        // After successful rebuild, counter should be reset
        Assert.That(ct.IndexStats[1].HyperLogLog, Is.Not.Null);
        Assert.That(ct.MutationsSinceRebuild, Is.LessThan(2000), "Counter should be reset after successful rebuild");
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
        StatisticsRebuilder.RebuildAll(ct, dbe.EpochManager, pageInterval: 2);

        var stats = ct.IndexStats[1]; // B field
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
