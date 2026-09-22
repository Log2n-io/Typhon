using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="FenceWorkPlan.ComputeMaxChunks"/> and <see cref="FenceWorkPlan.TargetChunkCost"/> — the
/// <c>max(1, min(2 × workerCount × oversubscription, ceil(totalCost / target)))</c> chunk-count formula, where the target is
/// <c>clamp(totalCost / (W × O), MinUsefulChunkUs, 200 µs)</c> (#889). Verifies the edge cases the integration tests can't easily probe: zero cost, a phase
/// too light to spread, the worker-oversubscription ceiling, and the rule before #889 behind its switch.
/// </summary>
[TestFixture]
[NonParallelizable]   // mutates the planner's static switches
class FenceWorkPlanComputeMaxChunksTests
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

    [Test]
    public void Zero_Cost_Returns_One_Chunk()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(0f, workerCount: 8, chunkOversubscription: 2), Is.EqualTo(1));
    }

    [Test]
    public void Negative_Cost_Clamped_To_One()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(-100f, 8, 2), Is.EqualTo(1));
    }

    /// <summary>The measurement behind #889: 551 µs of index work on 8 workers got one chunk under the old rule. It must now get the whole width.</summary>
    [Test]
    public void A_Phase_Lighter_Than_The_Old_Floor_Still_Spreads_Over_The_Workers()
    {
        // 551 / (8 × 2) = 34.4 µs per chunk, above the 32 µs useful floor → 16 chunks of ~34 µs.
        Assert.That(FenceWorkPlan.TargetChunkCost(551f, 8, 2), Is.EqualTo(551f / 16f).Within(0.01f));
        Assert.That(FenceWorkPlan.ComputeMaxChunks(551f, 8, 2), Is.EqualTo(16));
    }

    /// <summary>Below the useful floor the chunk count shrinks rather than dispatching chunks that cost more than they carry.</summary>
    [Test]
    public void A_Phase_Too_Light_To_Spread_Gets_As_Many_Useful_Chunks_As_It_Can_Fill()
    {
        // 100 / 16 = 6.25 µs per chunk would be below the floor → target is the floor → ceil(100 / 32) = 4 chunks.
        Assert.That(FenceWorkPlan.TargetChunkCost(100f, 8, 2), Is.EqualTo(32f));
        Assert.That(FenceWorkPlan.ComputeMaxChunks(100f, 8, 2), Is.EqualTo(4));
        Assert.That(FenceWorkPlan.ComputeMaxChunks(20f, 8, 2), Is.EqualTo(1), "less than one useful chunk of work is one chunk");
    }

    /// <summary>Plenty of work: the target saturates at 200 µs and the count grows past W × O, for jitter absorption as before.</summary>
    [Test]
    public void Heavy_Work_Keeps_The_200us_Grain()
    {
        // 6000 / 16 = 375 → clamped to 200 → ceil(6000 / 200) = 30, under the 2 × 8 × 2 = 32 cap.
        Assert.That(FenceWorkPlan.TargetChunkCost(6000f, 8, 2), Is.EqualTo(200f));
        Assert.That(FenceWorkPlan.ComputeMaxChunks(6000f, 8, 2), Is.EqualTo(30));
    }

    [Test]
    public void Abundance_CappedAtWorkerOversubscription()
    {
        // Huge cost: cost-based would be 1e9/200 = 5_000_000, but the ceiling 2 × 8 × 2 = 32 clamps it.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1e9f, 8, 2), Is.EqualTo(32));
    }

    [Test]
    public void Worker_Count_Scales_The_Cap()
    {
        // 16 workers → ceiling 2 × 16 × 2 = 64.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1e9f, 16, 2), Is.EqualTo(64));
    }

    [Test]
    public void Oversubscription_Scales_The_Cap()
    {
        // 8 workers, oversubscription 3 → ceiling 2 × 8 × 3 = 48.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1e9f, 8, 3), Is.EqualTo(48));
    }

    [Test]
    public void Zero_Workers_Treated_As_One()
    {
        // Defensive: workerCount/oversubscription clamp to 1 → ceiling 2 × 1 × 1 = 2; a single worker keeps the 200 µs grain → 5 → min = 2.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 0, 0), Is.EqualTo(2));
    }

    /// <summary>A single worker keeps the legacy grain AND its floor division: at W = 1, 300 µs is one chunk, not two, whatever the switch says.</summary>
    [Test]
    public void One_Worker_Keeps_The_Legacy_Grain_And_Its_Floor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FenceWorkPlan.TargetChunkCost(100f, 1, 2), Is.EqualTo(200f));
            Assert.That(FenceWorkPlan.ComputeMaxChunks(300f, 1, 2), Is.EqualTo(1),
                "floor(300 / 200) = 1 — two chunks for one worker is pure dispatch overhead");
            Assert.That(FenceWorkPlan.ComputeMaxChunks(199f, 1, 2), Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The width is stated, not derived: <c>ceil(230.66 / (230.66 / 7))</c> is 8 in single precision. At a width that is not a power of two the count must
    /// still be the width when the per-worker share sits inside the clamp.
    /// </summary>
    [Test]
    public void Non_Power_Of_Two_Width_Is_Exact()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FenceWorkPlan.ComputeMaxChunks(230.66f, 7, 1), Is.EqualTo(7));
            Assert.That(FenceWorkPlan.ComputeMaxChunks(230.66f, 7, 2), Is.EqualTo(8),
                "14 wanted, but 230.66 / 14 = 16.5 µs is below the floor → ceil(230.66 / 32) = 8");
            Assert.That(FenceWorkPlan.ComputeMaxChunks(450f, 3, 1), Is.EqualTo(3), "150 µs a worker is inside the clamp → the width");
            Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 3, 1), Is.EqualTo(5), "333 µs a worker is above the 200 µs grain → ceil(1000 / 200)");
        });
    }

    /// <summary>A harness floor above the 200 µs grain must not invert the clamp and throw inside the fence.</summary>
    [Test]
    public void A_Floor_Above_The_Grain_Is_Clamped_Not_Thrown()
    {
        FenceWorkPlan.MinUsefulChunkUs = 500f;
        Assert.That(FenceWorkPlan.TargetChunkCost(551f, 8, 2), Is.EqualTo(200f));
        Assert.That(FenceWorkPlan.ComputeMaxChunks(551f, 8, 2), Is.EqualTo(3));
    }

    /// <summary>The rule before #889, kept behind <see cref="FenceWorkPlan.WorkerAwareChunking"/> for the harness A/B: 200 µs per chunk whatever W
    /// is.</summary>
    [Test]
    public void Legacy_Rule_Ignores_The_Worker_Count()
    {
        FenceWorkPlan.WorkerAwareChunking = false;
        Assert.Multiple(() =>
        {
            Assert.That(FenceWorkPlan.ComputeMaxChunks(199.99f, 8, 2), Is.EqualTo(1), "199.99 µs / 200 µs/chunk = 0 → clamped to 1");
            Assert.That(FenceWorkPlan.ComputeMaxChunks(200f, 8, 2), Is.EqualTo(1));
            Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 8, 2), Is.EqualTo(5), "1000 µs / 200 = 5");
            Assert.That(FenceWorkPlan.ComputeMaxChunks(551f, 8, 2), Is.EqualTo(2), "the index phase's one-to-two chunks that motivated #889");
        });
    }
}
