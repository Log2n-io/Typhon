using NUnit.Framework;
using System.Collections.Generic;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// A value written in place into a cluster — through a span, or through the spatial barrier — survives the page cache: the write records its page as
/// modified, so the page is never evicted with the write in memory only, and the checkpoint writes it (PS-10).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins.</b> A SingleVersion column written through <c>GetSpan</c> (then <c>MarkDirty</c>) is written in place through an accessor that
/// maps the page clean; the fence then emitted the WAL record from the same bytes, also through a clean mapping. Nothing recorded the page as modified, so
/// the page cache held committed data on an evictable page: the next allocation that needed a frame took it, and the following read loaded the older image
/// from disk. The checkpoint never collected it either, so its WAL record could be recycled away. <c>WriteSpatial</c> had the same hole.
/// </para>
/// <para>
/// <b>How it was found.</b> The push-replication oracle, whose explicit mode is the one replication mode that does not re-send what the engine holds each
/// tick: its clients kept the committed value while the engine had reverted. The churn here is that oracle's, on a bare engine with no replication at all.
/// Seed 9160 lost four writes at tick 193, inside an unrelated rock spawn whose segment growth took the creatures' page.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class InPlaceClusterWriteSurvivalTests : TestBase<InPlaceClusterWriteSurvivalTests>
{
    [Test]
    [VerifiesRule("PS-10")]
    public void CommittedSpanWritesSurviveChurn([Values(9160, 9100, 42, 7, 1234)] int seed)
    {
        using var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var workload = new OracleWorkload(engine, seed);
        workload.Seed(creatures: 400, rocks: 120);
        long tick = 1;
        engine.WriteTickFence(tick);
        for (var i = 0; i < 200; i++)
        {
            workload.Step();
            var lost = Lost(engine, workload);
            Assert.That(lost, Is.Empty, $"tick {tick + 1}: committed span write(s) read back as older values after '{workload.LastAction}'");
            engine.WriteTickFence(++tick);
        }

        Assert.That(workload.LastWritten.Count, Is.GreaterThan(50), "the churn wrote too little for the check to mean anything");
    }

    /// <summary>
    /// The same for the spatial barrier: a <c>WriteSpatial</c> that moves an entity inside its cluster writes the page in place too, and the moved position
    /// must still read back after the churn's allocations have cycled the page cache.
    /// </summary>
    [Test]
    [VerifiesRule("PS-10")]
    public void SpatialWritesSurviveChurn([Values(9160, 9100, 42, 7, 1234)] int seed)
    {
        using var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var workload = new OracleWorkload(engine, seed);
        workload.Seed(creatures: 400, rocks: 120);
        var random = new System.Random(seed ^ 0x51);
        var written = new Dictionary<long, (float X, float Y)>();
        long tick = 1;
        engine.WriteTickFence(tick);
        for (var i = 0; i < 200; i++)
        {
            workload.Step();
            foreach (var moved in workload.MovedLastStep)
            {
                written.Remove(moved);
            }

            NudgeSome(engine, random, written);
            var lost = LostPositions(engine, written);
            Assert.That(lost, Is.Empty, $"tick {tick + 1}: spatial write(s) read back as older positions after '{workload.LastAction}'");
            engine.WriteTickFence(++tick);
        }

        Assert.That(written.Count, Is.GreaterThan(50), "too few spatial writes for the check to mean anything");
    }

    /// <summary>Moves a few creatures by a centimetre through <c>WriteSpatial</c> — inside their cluster, so nothing migrates — and records where.</summary>
    private static void NudgeSome(DatabaseEngine engine, System.Random random, Dictionary<long, (float X, float Y)> written)
    {
        using var tx = engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (random.Next(20) != 0)
                {
                    continue;
                }

                var slot = System.Numerics.BitOperations.TrailingZeroCount(cluster.OccupancyBits);
                var current = cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];
                var x = ((current.Bounds.MinX + current.Bounds.MaxX) * 0.5f) + 0.01f;
                var y = (current.Bounds.MinY + current.Bounds.MaxY) * 0.5f;
                var moved = current;
                moved.Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f };
                cluster.WriteSpatial(ProjCreature.Bounds, slot, moved);
                written[(long)cluster.GetEntityId(slot).RawValue] = (x, y);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static List<string> LostPositions(DatabaseEngine engine, Dictionary<long, (float X, float Y)> written)
    {
        var lost = new List<string>();
        using var tx = engine.CreateQuickTransaction();
        foreach (var (raw, at) in written)
        {
            if (!tx.IsAlive(EntityId.FromRaw(raw)))
            {
                continue;
            }

            var b = tx.Open(EntityId.FromRaw(raw)).Read(ProjCreature.Bounds).Bounds;
            var x = (b.MinX + b.MaxX) * 0.5f;
            var y = (b.MinY + b.MaxY) * 0.5f;
            if (System.Math.Abs(x - at.X) > 1e-3f || System.Math.Abs(y - at.Y) > 1e-3f)
            {
                lost.Add($"entity {raw}: wrote ({at.X}, {at.Y}), reads ({x}, {y})");
            }
        }

        return lost;
    }

    private static List<string> Lost(DatabaseEngine engine, OracleWorkload workload)
    {
        var lost = new List<string>();
        using var tx = engine.CreateQuickTransaction();
        foreach (var (raw, written) in workload.LastWritten)
        {
            var ai = tx.Open(EntityId.FromRaw(raw)).Read(ProjCreature.Ai);
            if ((int)ai.Mode != written.Mode || ai.Level != written.Level)
            {
                lost.Add($"entity {raw}: wrote mode {written.Mode} level {written.Level}, reads mode {(int)ai.Mode} level {ai.Level}");
            }
        }

        return lost;
    }
}
