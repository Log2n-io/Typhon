using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class AdvancedEstimatorTests : TestBase<AdvancedEstimatorTests>
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
    public void Equality_McvHit_ReturnsExactCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompF>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Gold (AllowMultiple): 80 entities with Gold=42, 20 with Gold=99
        for (int i = 0; i < 80; i++)
        {
            CreateAndCommitCompF(dbe, 42, i);
        }
        for (int i = 0; i < 20; i++)
        {
            CreateAndCommitCompF(dbe, 99, 80 + i);
        }

        // Build statistics so MCV is available
        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // MCV hit: should return exact frequency from MCV (Gold is field 0)
        var card42 = estimator.EstimateCardinality(ct, 0, CompareOp.Equal, 42);
        Assert.That(card42, Is.EqualTo(80));

        var card99 = estimator.EstimateCardinality(ct, 0, CompareOp.Equal, 99);
        Assert.That(card99, Is.EqualTo(20));
    }

    [Test]
    public void Equality_McvMiss_FallsBackToBTree()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // Insert unique values
        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // Value 50 is in MCV but B is a unique index — BTree fallback should also give 1
        var card = estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 50);
        Assert.That(card, Is.EqualTo(1));

        // Value 999 doesn't exist
        var cardMiss = estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 999);
        Assert.That(cardMiss, Is.EqualTo(0));
    }

    [Test]
    public void Equality_NoStats_FallsBackToBTree()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        CreateAndCommitCompD(dbe, 1.0f, 42, 1.0);
        CreateAndCommitCompD(dbe, 2.0f, 99, 2.0);

        // No statistics built — should fall back to B+Tree point seek
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 42), Is.EqualTo(1));
        Assert.That(estimator.EstimateCardinality(ct, 1, CompareOp.Equal, 999), Is.EqualTo(0));
    }

    [Test]
    public void Range_HistogramAvailable_UsesHistogram()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // 1000 entities with B from 0 to 999
        for (int i = 0; i < 1000; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        StatisticsRebuilder.RebuildStatistics(ct, dbe.EpochManager);

        // B > 499 should estimate ~500 (histogram available)
        var gt = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 499);
        Assert.That(gt, Is.InRange(400, 600));

        // B <= 499 should estimate ~500
        var lte = estimator.EstimateCardinality(ct, 1, CompareOp.LessThanOrEqual, 499);
        Assert.That(lte, Is.InRange(400, 600));
    }

    [Test]
    public void Range_NoHistogram_UsesUniform()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();
        var estimator = AdvancedSelectivityEstimator.Instance;

        // 100 entities with B from 0 to 99 — no statistics built
        for (int i = 0; i < 100; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // No rebuild → uniform distribution fallback
        var gt = estimator.EstimateCardinality(ct, 1, CompareOp.GreaterThan, 49);
        Assert.That(gt, Is.InRange(40, 60));
    }

    [Test]
    public void NotEqual_McvHit_ReturnsTotalMinusCount()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompF>();
        var estimator = AdvancedSelectivityEstimator.Instance;

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

        // Gold != 42 should be total - 80 = 20 (Gold is field 0)
        var card = estimator.EstimateCardinality(ct, 0, CompareOp.NotEqual, 42);
        Assert.That(card, Is.EqualTo(20));
    }

    [Test]
    public void GracefulDegradation_NoStatistics_SameAsBasic()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        var ct = dbe.GetComponentTable<CompD>();
        var advanced = AdvancedSelectivityEstimator.Instance;
        var basic = BasicSelectivityEstimator.Instance;

        for (int i = 0; i < 50; i++)
        {
            CreateAndCommitCompD(dbe, 1.0f, i, 1.0);
        }

        // Without statistics, advanced should produce identical results to basic
        for (int threshold = 0; threshold < 50; threshold += 10)
        {
            Assert.That(
                advanced.EstimateCardinality(ct, 1, CompareOp.Equal, threshold),
                Is.EqualTo(basic.EstimateCardinality(ct, 1, CompareOp.Equal, threshold)),
                $"Equality mismatch at threshold {threshold}");

            Assert.That(
                advanced.EstimateCardinality(ct, 1, CompareOp.GreaterThan, threshold),
                Is.EqualTo(basic.EstimateCardinality(ct, 1, CompareOp.GreaterThan, threshold)),
                $"GreaterThan mismatch at threshold {threshold}");
        }
    }
}
