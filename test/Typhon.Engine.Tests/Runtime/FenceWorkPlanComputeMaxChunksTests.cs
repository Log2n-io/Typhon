using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="FenceWorkPlan.ComputeMaxChunks"/> — the 200µs-floor + 2× worker-oversubscription
/// chunk-count formula. Verifies edge cases the integration tests can't easily probe:
/// zero cost, sub-floor cost, abundance, and ceiling clamping.
/// </summary>
[TestFixture]
class FenceWorkPlanComputeMaxChunksTests
{
    [Test]
    public void Zero_Cost_Returns_One_Chunk()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(0f, workerCount: 8, chunkOversubscription: 2), Is.EqualTo(1));
    }

    [Test]
    public void Sub_Floor_Cost_Returns_One_Chunk()
    {
        // 199.99 µs / 200 µs/chunk = 0 → clamped to 1
        Assert.That(FenceWorkPlan.ComputeMaxChunks(199.99f, 8, 2), Is.EqualTo(1));
    }

    [Test]
    public void At_Floor_Returns_One_Chunk()
    {
        // 200 µs / 200 = 1 exactly
        Assert.That(FenceWorkPlan.ComputeMaxChunks(200f, 8, 2), Is.EqualTo(1));
    }

    [Test]
    public void Mid_Range_Floors_To_Cost_Slices()
    {
        // 1000 µs / 200 = 5
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 8, 2), Is.EqualTo(5));
    }

    [Test]
    public void Abundance_NoWorkerCeiling_ScalesWithCost()
    {
        // Policy: chunkCount governed by 200µs floor only. 1e9µs / 200 = 5_000_000 chunks. Worker count irrelevant.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1e9f, 8, 2), Is.EqualTo(5_000_000));
    }

    [Test]
    public void Worker_Count_Does_Not_Cap()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1e9f, 16, 2), Is.EqualTo(5_000_000));
    }

    [Test]
    public void Zero_Workers_Treated_As_One()
    {
        // Defensive: max(1, ...) prevents 0 cap.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 0, 0), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Negative_Cost_Clamped_To_One()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(-100f, 8, 2), Is.EqualTo(1));
    }
}
