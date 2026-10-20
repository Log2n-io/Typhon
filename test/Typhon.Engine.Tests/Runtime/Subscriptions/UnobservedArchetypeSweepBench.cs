using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// AC-22 (#1040, 16 § 7): per-tick replication work follows the push set, and an archetype no profile observes costs nothing (SUB-13) — measured as a size
/// sweep rather than one 10 M run.
/// </summary>
/// <remarks>
/// <para>
/// A fixed observed world — 2 000 creatures, every one moving, eight world sessions — beside a replicated archetype no profile observes: N players, a tenth
/// of them moving every tick, N from 0 to 2 M. The replication track (the harness's tick without the fence) must not grow with N. The engine's tick fence
/// is timed beside it, because it is the cost #1396 records as scaling with the world rather than with what is subscribed, and a sweep that left it out
/// would hide exactly what AC-22 was written to catch elsewhere.
/// </para>
/// <para>
/// One configuration per case, an engine per test. Medians over the measured ticks, after a warm-up longer than the identification of the spawn: a mass
/// spawn is identified at <c>NetIdLeaseSet.MaxLease</c> entities a projection worker a tick, and every block still holding an unidentified entity is
/// projected again each tick until it is identified (measured with one worker: 2 M players took ~120 ticks and cost 50–70 ms a tick while it lasted).
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")]
[NonParallelizable]
class UnobservedArchetypeSweepBench : TestBase<UnobservedArchetypeSweepBench>
{
    private const string Profile = "god-world";
    private const int Creatures = 2_000;
    private const int Sessions = 8;
    private const int WarmTicks = 150;
    private const int MeasureTicks = 40;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclarePlayer(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static ProjBounds At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void Spawn(DatabaseEngine dbe, int players)
    {
        const int Batch = 8_192;
        for (var done = 0; done < players; done += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = done; i < Math.Min(players, done + Batch); i++)
            {
                var bounds = At((i % 2000 * 4f) - 4000f, (i / 2000 % 2000 * 4f) - 4000f);
                tx.Spawn<ProjPlayer>(ProjPlayer.Bounds.Set(in bounds));
            }

            tx.Commit();
        }

        using var creatures = dbe.CreateQuickTransaction();
        for (var i = 0; i < Creatures; i++)
        {
            var bounds = At(i % 50 * 30f, i / 50 * 30f);
            creatures.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(new ProjAi { Template = (byte)i }));
        }

        creatures.Commit();
    }

    /// <summary>Moves every creature, and every <paramref name="playerStride"/>-th player.</summary>
    private static void Move(DatabaseEngine dbe, long tick, int playerStride)
    {
        using var tx = dbe.CreateQuickTransaction();
        MoveAll(tx.For<ProjCreature>(), ProjCreature.Bounds, tick, 1);
        if (playerStride != int.MaxValue)
        {
            MoveAll(tx.For<ProjPlayer>(), ProjPlayer.Bounds, tick, playerStride);
        }
        tx.Commit();
    }

    private static void MoveAll<T>(ArchetypeAccessor<T> accessor, Comp<ProjBounds> component, long tick, int stride) where T : Archetype<T>
    {
        try
        {
            var n = 0L;
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (n++ % stride != tick % stride)
                    {
                        continue;
                    }

                    var b = cluster.GetReadOnly(component, slot).Bounds;
                    var angle = (tick * 0.7) + (slot * 1.3);
                    cluster.WriteSpatial(component, slot, At(b.MinX + (float)Math.Cos(angle), b.MinY + (float)Math.Sin(angle)));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // One plain test per size: TestBase reads [Property("CacheSize")] from the test method, and a [TestCase] case does not carry it.
    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void NoUnobservedEntity() => Sweep(0);

    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void AQuarterOfAMillionUnobserved() => Sweep(250_000);

    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void AMillionUnobserved() => Sweep(1_000_000);

    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void TwoMillionUnobserved() => Sweep(2_000_000);

    // The same sizes with the unobserved players still: what the archetype's size costs, apart from what its moving entities push.
    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void AQuarterOfAMillionUnobservedStill() => Sweep(250_000, moving: false);

    [Test]
    [Property("CacheSize", 2048L * 1024 * 1024)]
    public void TwoMillionUnobservedStill() => Sweep(2_000_000, moving: false);

    private void Sweep(int players, bool moving = true)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Spawn(dbe, players);
        using var harness = FrameHarness.Create(dbe, Declare, $"sweep-{players}", new SubscriptionsOptions
        {
            MaxSessions = Sessions + 2,
            StatePoolBudgetBytes = 256L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
        });
        harness.RunFence = false;

        // As the runtime runs it, with a lease per projection worker: an identity lease is at most NetIdLeaseSet.MaxLease a worker a tick, and a mass spawn
        // is identified at that rate — the warm-up outlasts it (2 M at 8 x 16 384 is 16 ticks), so the measured ticks are the steady state.
        harness.ProjectionWorkers = 8;
        var sessions = harness.OpenSessions(Sessions, Profile);
        var track = new List<double>();
        var fence = new List<double>();
        for (long tick = 1; tick <= WarmTicks + MeasureTicks; tick++)
        {
            Move(dbe, tick, playerStride: moving ? 10 : int.MaxValue);
            var start = Stopwatch.GetTimestamp();
            dbe.WriteTickFence(tick);
            var afterFence = Stopwatch.GetTimestamp();
            harness.RunTick(tick);
            var end = Stopwatch.GetTimestamp();
            foreach (var session in sessions)
            {
                harness.Drain(session);
            }

            if (tick > WarmTicks)
            {
                fence.Add(Stopwatch.GetElapsedTime(start, afterFence).TotalMicroseconds);
                track.Add(Stopwatch.GetElapsedTime(afterFence, end).TotalMicroseconds);
            }
        }

        track.Sort();
        fence.Sort();
        TestContext.Out.WriteLine(
            $"unobserved {players,9:N0} {(moving ? "moving" : "still ")}   track median {track[track.Count / 2],8:F0} µs (min {track[0]:F0})   "
            + $"fence median {fence[fence.Count / 2],8:F0} µs");
    }
}
