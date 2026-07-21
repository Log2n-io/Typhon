using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Direct tests for the bin-packer's chunk-count policy. Drives synthetic items and asserts the resulting chunk count
/// against the documented policy: target W×O chunks of ≥200µs each; collapse if work is too light.
/// </summary>
[TestFixture]
class FenceWorkPlanPackTests
{
    private static float[] Repeat(int count, float costEach)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = costEach;
        return a;
    }

    [Test]
    public void HeavyAabbScenario_16W_2O_ManySlices_Targets32Chunks()
    {
        // Mimics 1800 clusters × 3.3µs = 5940µs spread across 30+ slices (so ItemCount doesn't bind).
        // With W=16, O=2 → target 32 chunks of ~185µs each, floored to 200µs each.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(60, 99f), workerCount: 16, chunkOversubscription: 2);
        // totalCost = 5940; floor target 200 → expected chunks ≈ ceil(5940/200) = 30
        TestContext.WriteLine($"chunks={chunks} (expected ~30)");
        Assert.That(chunks, Is.InRange(28, 32));
    }

    [Test]
    public void HeavyAabbScenario_ItemCountBindsToTen_OnlyTenChunks()
    {
        // Reproduces the user's reported case: same total cost (~6000µs) but only 10 ITEMS available (one per word).
        // Result: chunkCount capped at ItemCount=10 regardless of target. Per-chunk = ~600µs.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(10, 600f), workerCount: 16, chunkOversubscription: 2);
        TestContext.WriteLine($"chunks={chunks} (with only 10 items, expected 10)");
        Assert.That(chunks, Is.EqualTo(10), "ItemCount cap binds chunkCount when emitter produces few items");
    }

    [Test]
    public void LightLoad_AbovePartialFloor_TwoChunks()
    {
        var plan = new FenceWorkPlan();
        // totalCost = 300µs → ceil(300/200) = 2 chunks of ~150µs each. Each item is 5µs so bin-packer can
        // group 60 items into 2 chunks freely (ItemCount=60 doesn't bind).
        int chunks = plan.PackSyntheticForTest(Repeat(60, 5f), 16, 2);
        TestContext.WriteLine($"chunks={chunks}");
        Assert.That(chunks, Is.EqualTo(2));
    }

    [Test]
    public void HeavyLoadMany_ItemsAllowTarget_Returns30Chunks()
    {
        // 5940µs total spread across 30 items of 198µs each. target = 200µs (floor) → ceil(5940/200) = 30.
        // ItemCount=30. Bin-packer returns 30 chunks of ~198µs each.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(30, 198f), 16, 2);
        TestContext.WriteLine($"chunks={chunks}");
        Assert.That(chunks, Is.EqualTo(30));
    }
}
