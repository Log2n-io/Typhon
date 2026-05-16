using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Unit tests for <see cref="FenceWorkPlan.ComputeMaxChunks"/> — the <c>max(1, min(2 × workerCount × oversubscription, floor(totalCost / 200µs)))</c>
/// chunk-count formula. Verifies edge cases the integration tests can't easily probe:
/// zero cost, sub-floor cost, the worker-oversubscription ceiling, and ceiling clamping.
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
    public void Cost_Below_Cap_Is_Not_Clamped()
    {
        // 1000µs / 200 = 5 cost-based slices, well under the 2 × 8 × 2 = 32 ceiling → cost wins.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 8, 2), Is.EqualTo(5));
    }

    [Test]
    public void Zero_Workers_Treated_As_One()
    {
        // Defensive: workerCount/oversubscription clamp to 1 → ceiling 2 × 1 × 1 = 2; cost-based 1000/200 = 5 → min = 2.
        Assert.That(FenceWorkPlan.ComputeMaxChunks(1000f, 0, 0), Is.EqualTo(2));
    }

    [Test]
    public void Negative_Cost_Clamped_To_One()
    {
        Assert.That(FenceWorkPlan.ComputeMaxChunks(-100f, 8, 2), Is.EqualTo(1));
    }
}
