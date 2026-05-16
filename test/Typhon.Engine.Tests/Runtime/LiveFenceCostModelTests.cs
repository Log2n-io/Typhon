using System.Diagnostics;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="LiveFenceCostModel"/>: 64-tick sliding window, sum-weighted µs/unit, phase isolation.
/// </summary>
[TestFixture]
class LiveFenceCostModelTests
{
    private static readonly FenceCostModel Seed = new(MigrationCost: 33.3f, AabbCost: 2.4f, ShadowCost: 1f, SpatialCost: 1f);

    private static long TicksForMicros(double us) => (long)(us * Stopwatch.Frequency / 1_000_000.0);

    [Test]
    public void Update_FirstSample_UsesMeasuredValue()
    {
        var model = new LiveFenceCostModel(Seed);
        long wallTicks = TicksForMicros(500.0); // 500 µs of work
        const long units = 100;                  // 100 entities
        model.UpdatePhase(FencePhase.Migrate, wallTicks, units);
        Assert.That(model.MigrationCost, Is.EqualTo(5f).Within(0.05f), "first sample should yield ~5 µs/entity, not the 33.3 seed");
    }

    [Test]
    public void Update_StableLoad_ConvergesToTruth()
    {
        var model = new LiveFenceCostModel(Seed);
        long wallTicks = TicksForMicros(200.0); // 2 µs/entity × 100 entities
        for (int i = 0; i < 64; i++)
        {
            model.UpdatePhase(FencePhase.AabbRefresh, wallTicks, 100);
        }
        Assert.That(model.AabbCost, Is.EqualTo(2f).Within(0.02f));
    }

    [Test]
    public void Update_LoadShifts_WindowTracks()
    {
        var model = new LiveFenceCostModel(Seed);
        long phaseA = TicksForMicros(500.0); // 5 µs/entity × 100
        long phaseB = TicksForMicros(150.0); // 1.5 µs/entity × 100
        for (int i = 0; i < 64; i++) model.UpdatePhase(FencePhase.Migrate, phaseA, 100);
        Assert.That(model.MigrationCost, Is.EqualTo(5f).Within(0.05f), "after 64 of phase A, cost = 5");
        for (int i = 0; i < 64; i++) model.UpdatePhase(FencePhase.Migrate, phaseB, 100);
        Assert.That(model.MigrationCost, Is.EqualTo(1.5f).Within(0.02f), "after 64 of phase B (full eviction), cost = 1.5");
    }

    [Test]
    public void Update_OutlierTick_DampenedByWindow()
    {
        var model = new LiveFenceCostModel(Seed);
        long normal = TicksForMicros(300.0); // 3 µs/entity × 100
        long spike = TicksForMicros(3000.0); // 30 µs/entity × 100 (10× outlier)
        for (int i = 0; i < 63; i++) model.UpdatePhase(FencePhase.Migrate, normal, 100);
        model.UpdatePhase(FencePhase.Migrate, spike, 100);
        // Expected: sum_wall = 63*300 + 3000 = 21900 µs over 64*100=6400 units → 3.42 µs/unit. Within 15% of 3.
        Assert.That(model.MigrationCost, Is.EqualTo(3.42f).Within(0.45f));
    }

    [Test]
    public void Update_ZeroUnits_KeepsPreviousCost()
    {
        var model = new LiveFenceCostModel(Seed);
        model.UpdatePhase(FencePhase.Migrate, TicksForMicros(500.0), 100); // sets to ~5
        float before = model.MigrationCost;
        model.UpdatePhase(FencePhase.Migrate, TicksForMicros(500.0), 0);
        Assert.That(model.MigrationCost, Is.EqualTo(before), "zero-units sample must be ignored");
    }

    [Test]
    public void Update_Migrate_DoesntAffectAabbCoefficient()
    {
        var model = new LiveFenceCostModel(Seed);
        float aabbBefore = model.AabbCost;
        for (int i = 0; i < 64; i++)
        {
            model.UpdatePhase(FencePhase.Migrate, TicksForMicros(500.0), 100);
        }
        Assert.That(model.AabbCost, Is.EqualTo(aabbBefore), "Migrate updates must not touch AabbCost");
    }

    [Test]
    public void Seed_FromFenceCostModel_PreservesAllFourFields()
    {
        var seed = new FenceCostModel(MigrationCost: 11.1f, AabbCost: 2.2f, ShadowCost: 3.3f, SpatialCost: 4.4f);
        var model = new LiveFenceCostModel(seed);
        Assert.That(model.MigrationCost, Is.EqualTo(11.1f));
        Assert.That(model.AabbCost, Is.EqualTo(2.2f));
        Assert.That(model.ShadowCost, Is.EqualTo(3.3f));
        Assert.That(model.SpatialCost, Is.EqualTo(4.4f));
    }

    [Test]
    public void UpdatePhase_HotLoop_AllocatesNothing()
    {
        var model = new LiveFenceCostModel(Seed);
        // Warm up the ring once so the constant-time path is taken on the measured loop.
        for (int i = 0; i < 64; i++) model.UpdatePhase(FencePhase.Migrate, 1000, 100);

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            model.UpdatePhase(FencePhase.Migrate, 1000, 100);
            model.UpdatePhase(FencePhase.AabbRefresh, 2000, 50);
        }
        long after = System.GC.GetAllocatedBytesForCurrentThread();

        Assert.That(after - before, Is.Zero, "UpdatePhase must allocate zero bytes in steady state");
    }
}
