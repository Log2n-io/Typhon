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

    /// <summary>
    /// The same for a point write: <c>Set</c> on a SingleVersion component writes the cluster slot in place, and the committed
    /// value must read back after the cache has evicted every page its own rules let it evict, before any fence has logged the write (#1172). It lost 2 338
    /// of 4 096 writes in the fence fixture that found it. Both overloads: by handle and by type.
    /// </summary>
    [Test]
    [VerifiesRule("PS-10")]
    public void CommittedPointWritesSurviveEviction([Values] bool byType)
    {
        using var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var workload = new OracleWorkload(engine, 42);
        workload.Seed(creatures: 400, rocks: 120);
        engine.WriteTickFence(1);
        Assert.That(CheckpointNow(engine), Is.True, "premise: a checkpoint wrote the seed's pages");

        var written = new Dictionary<EntityId, int>();
        using (var tx = engine.CreateQuickTransaction())
        {
            foreach (var id in workload.Creatures)
            {
                var entity = tx.OpenMut(id);
                var ai = byType ? entity.Read<ProjAi>() : entity.Read(ProjCreature.Ai);
                ai.ThinkCooldown += 1_000_003;
                if (byType)
                {
                    entity.Set(in ai);
                }
                else
                {
                    entity.Set(ProjCreature.Ai, ai);
                }
                written[id] = ai.ThinkCooldown;
            }

            tx.Commit();
        }

        var evicted = engine.MMF.EvictEvictablePagesForTest();
        var lost = LostCooldowns(engine, written);
        Assert.That(evicted, Is.GreaterThan(0), "premise: the cache evicted pages between the commit and the read");
        var sample = string.Join(" | ", lost.GetRange(0, System.Math.Min(5, lost.Count)));
        Assert.That(lost, Is.Empty, $"{lost.Count} of {written.Count} committed writes reverted: {sample}");
    }

    public enum HandedOutWrite
    {
        /// <summary>Stores through <c>GetSpan</c>.</summary>
        Span,

        /// <summary>Stores through the <c>ref</c> <c>Get</c> returns.</summary>
        Ref,

        /// <summary>
        /// Stores through <c>GetSpan</c>, with a checkpoint between handing each cluster's span out and the first store into it. Every cluster, not one: a
        /// later cluster on the same page would otherwise record the page again after the earlier one's stores and hide a record taken too early.
        /// </summary>
        SpanThenCheckpointThenStore,

        /// <summary>
        /// Stores through <c>GetSpan</c> into the first cluster only, then leaves the loop: no <c>MoveNext</c> leaves that cluster, so the enumerator's
        /// <c>Dispose</c> is what records its page.
        /// </summary>
        SpanThenBreak,
    }

    /// <summary>
    /// A SingleVersion column written through memory a <see cref="ClusterRef{TArch}"/> hands out — <c>GetSpan</c> or <c>Get</c> — with NO <c>MarkDirty</c>
    /// must still read back after the cache has evicted every page it may (#1531). Nothing recorded the page: the write reverted to the disk image at the
    /// first eviction. The enumerator now records it on leaving the cluster, after the stores; recording it when the span is handed out instead fails the
    /// checkpoint case, whose capture lands between that record and the stores and settles the page with the old bytes.
    /// </summary>
    [Test]
    [VerifiesRule("PS-10")]
    public void HandedOutMemoryWritesSurviveEviction_WithoutMarkDirty([Values] HandedOutWrite how) => HandedOutWriteScenario(how, skipRecord: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: the enumerator skips the page record of a cluster whose memory it handed out — the pre-#1531
    /// behaviour — and the writes must revert at eviction, which the verifier must reject. Every variant, so the Dispose record is proven too.
    /// </summary>
    [Test]
    [RuleMutant("PS-10")]
    public void HandedOutMemoryWrites_WithTheRecordSkipped_AreRejected([Values] HandedOutWrite how) =>
        RuleMutants.AssertDetects("PS-10", HandedOutLostMarker, () => HandedOutWriteScenario(how, skipRecord: true));

    private const string HandedOutLostMarker = "writes through handed-out memory reverted";

    private void HandedOutWriteScenario(HandedOutWrite how, bool skipRecord)
    {
        using var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var workload = new OracleWorkload(engine, 42);
        workload.Seed(creatures: 400, rocks: 120);
        engine.WriteTickFence(1);
        Assert.That(CheckpointNow(engine), Is.True, "premise: a checkpoint wrote the seed's pages");
        engine._archetypeStates[Archetype<ProjCreature>.Metadata.ArchetypeId].ClusterState.SkipHandedOutPageRecordForTest = skipRecord;

        var written = new Dictionary<EntityId, int>();
        using (var tx = engine.CreateQuickTransaction())
        {
            var accessor = tx.For<ProjCreature>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                if (how == HandedOutWrite.Ref)
                {
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        ref var ai = ref cluster.Get(ProjCreature.Ai, slot);
                        ai.ThinkCooldown += 1_000_003;
                        written[cluster.GetEntityId(slot)] = ai.ThinkCooldown;
                    }

                    continue;
                }

                var span = cluster.GetSpan(ProjCreature.Ai);
                if (how == HandedOutWrite.SpanThenCheckpointThenStore)
                {
                    Assert.That(CheckpointNow(engine), Is.True, "premise: the mid-walk checkpoint ran");
                }

                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    span[slot].ThinkCooldown += 1_000_003;
                    written[cluster.GetEntityId(slot)] = span[slot].ThinkCooldown;
                }

                if (how == HandedOutWrite.SpanThenBreak)
                {
                    break;
                }
            }

            tx.Commit();
        }

        var evicted = engine.MMF.EvictEvictablePagesForTest();
        var lost = LostCooldowns(engine, written);
        Assert.That(evicted, Is.GreaterThan(0), "premise: the cache evicted pages between the writes and the read");
        Assert.That(written.Count, Is.GreaterThan(how == HandedOutWrite.SpanThenBreak ? 0 : 100), "premise: the walk wrote the seeded creatures");
        var sample = string.Join(" | ", lost.GetRange(0, System.Math.Min(5, lost.Count)));
        Assert.That(lost, Is.Empty, $"{lost.Count} of {written.Count} writes through handed-out memory reverted: {sample}");
    }

    private static bool CheckpointNow(DatabaseEngine engine) => engine.CheckpointManager.ForceCheckpointAndWait(System.TimeSpan.FromSeconds(30));

    private static List<string> LostCooldowns(DatabaseEngine engine, Dictionary<EntityId, int> written)
    {
        var lost = new List<string>();
        using var tx = engine.CreateQuickTransaction();
        foreach (var (id, value) in written)
        {
            var read = tx.Open(id).Read(ProjCreature.Ai).ThinkCooldown;
            if (read != value)
            {
                lost.Add($"entity {id.RawValue}: wrote {value}, reads {read}");
            }
        }

        return lost;
    }

    /// <summary>Moves a few creatures by a centimetre through <c>WriteSpatial</c> — inside their cluster, so nothing migrates — and records where.</summary>
    private static void NudgeSome(DatabaseEngine engine, System.Random random, Dictionary<long, (float X, float Y)> written)
    {
        using var tx = engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
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
