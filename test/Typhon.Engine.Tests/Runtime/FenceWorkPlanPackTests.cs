using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Direct tests for the bin-packer's chunk-count policy. Drives synthetic items and asserts the resulting chunk count against the documented policy
/// (#889): spread the phase over <c>W × O</c> chunks as soon as each carries <see cref="FenceWorkPlan.MinUsefulChunkUs"/> of work, keep a 200 µs grain
/// beyond that, never more chunks than items.
/// </summary>
[TestFixture]
[NonParallelizable]   // mutates the planner's static switches
class FenceWorkPlanPackTests
{
    private float _floor;
    private bool _workerAware;

    [SetUp]
    public void SetUp()
    {
        _floor = FenceWorkPlan.MinUsefulChunkUs;
        _workerAware = FenceWorkPlan.WorkerAwareChunking;
        FenceWorkPlan.MinUsefulChunkUs = 32f;
        FenceWorkPlan.WorkerAwareChunking = true;
    }

    [TearDown]
    public void TearDown()
    {
        FenceWorkPlan.MinUsefulChunkUs = _floor;
        FenceWorkPlan.WorkerAwareChunking = _workerAware;
    }

    private static float[] Repeat(int count, float costEach)
    {
        var a = new float[count];
        for (int i = 0; i < count; i++) a[i] = costEach;
        return a;
    }

    [Test]
    public void HeavyAabbScenario_16W_2O_ManySlices_Targets32Chunks()
    {
        // Mimics 1800 clusters × 3.3µs = 5940µs spread across 60 slices (so ItemCount doesn't bind).
        // With W=16, O=2 → 5940 / 32 = 185.6 µs per chunk, inside [32, 200] → 32 chunks of ~186 µs each.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(60, 99f), workerCount: 16, chunkOversubscription: 2);
        TestContext.WriteLine($"chunks={chunks} (expected 32)");
        Assert.That(chunks, Is.EqualTo(32));
    }

    [Test]
    public void HeavyAabbScenario_TenFatItems_TenChunks()
    {
        // Same total cost (~6000µs) but only 10 ITEMS (one per word), each 600 µs. The 600 µs item is fatter than the 187.5 µs target, so it sets the
        // grain: ceil(6000 / 600) = 10 — one item per chunk. The ItemCount cap is not what binds here, and with the fat-item rule it hardly ever can: the
        // fattest item is at least total / ItemCount, so ceil(total / maxAtomic) ≤ ItemCount up to float rounding.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(10, 600f), workerCount: 16, chunkOversubscription: 2);
        TestContext.WriteLine($"chunks={chunks} (ten 600 µs items, expected 10)");
        Assert.That(chunks, Is.EqualTo(10), "a fat item sets the grain — one chunk per item");
    }

    /// <summary>
    /// The #889 case: 300 µs of work on 16 workers used to be TWO chunks (the 200 µs rule), so fourteen workers idled at the barrier. It is now as many
    /// useful chunks as the work fills — 300 / 16 × 2 = 9.4 µs each would be below the floor, so the floor is the grain: ceil(300 / 32) = 10.
    /// </summary>
    [Test]
    public void LightLoad_SpreadsOverAsManyUsefulChunksAsItFills()
    {
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(60, 5f), 16, 2);
        TestContext.WriteLine($"chunks={chunks}");
        Assert.That(chunks, Is.EqualTo(10));
    }

    /// <summary>The rule before #889, behind its switch: 300 µs → ceil(300 / 200) = 2 chunks whatever W is.</summary>
    [Test]
    public void LightLoad_LegacyRule_TwoChunks()
    {
        FenceWorkPlan.WorkerAwareChunking = false;
        var plan = new FenceWorkPlan();
        Assert.That(plan.PackSyntheticForTest(Repeat(60, 5f), 16, 2), Is.EqualTo(2));
    }

    [Test]
    public void HeavyLoadMany_ItemsAllowTarget_Returns30Chunks()
    {
        // 5940µs total spread across 30 items of 198µs each. The target would be 185.6 µs, but a 198 µs item is fatter than that and sets the grain:
        // ceil(5940 / 198) = 30 chunks of one item each.
        var plan = new FenceWorkPlan();
        int chunks = plan.PackSyntheticForTest(Repeat(30, 198f), 16, 2);
        TestContext.WriteLine($"chunks={chunks}");
        Assert.That(chunks, Is.EqualTo(30));
    }

    /// <summary>Seven workers, seventy items of 3.2952 µs: the per-worker share (32.95 µs) is inside the clamp, so the count is the width — seven — and
    /// not the eight that <c>ceil(230.66 / 32.95)</c> reads in single precision.</summary>
    [Test]
    public void NonPowerOfTwoWidth_PacksToTheWidth()
    {
        var plan = new FenceWorkPlan();
        Assert.That(plan.PackSyntheticForTest(Repeat(70, 3.2952f), 7, 1), Is.EqualTo(7));
    }

    /// <summary>An item bigger than the grain sets the grain — it cannot be split, so the count follows it rather than the target.</summary>
    [Test]
    public void OneFatItem_SetsTheGrain()
    {
        var plan = new FenceWorkPlan();
        // 1 × 900 + 30 × 10 = 1200 µs on 8 × 2: target would be 75 µs, but the 900 µs item forces a 900 µs grain → ceil(1200 / 900) = 2 chunks.
        var costs = Repeat(31, 10f);
        costs[0] = 900f;
        Assert.That(plan.PackSyntheticForTest(costs, 8, 2), Is.EqualTo(2));
    }
}
