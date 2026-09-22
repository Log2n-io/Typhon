using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The interest pass never stops REACHING an entity that has not moved, for an observer that has not moved, however hard the spatial layer churns.
/// </summary>
/// <remarks>
/// <para>
/// <b>About the runs, not about the client.</b> This drives the interest pass alone, so what it can assert is that the runs a session gets name exactly the
/// entities geometry says they should, on every tick, while thousands of others move and repair rearranges the clusters underneath. It cannot say anything
/// about enters or leaves: those are a difference against the mask a session's last PUBLISHED frame committed, and with no frame stage nothing is ever
/// committed. <c>StationaryChurnTests</c> and <c>WalkingObserverChurnTests</c> drive the whole track and settle the client-visible question.
/// </para>
/// <para>
/// <b>Pinned entities are the probe, and their answer is a constant.</b> They are spawned once and never written again, and the observers never move, so the
/// set inside a session's radius is fixed for the whole run and computable by arithmetic. Everything else drifts, which is what gives the repair path
/// clusters to nominate. A pinned entity in a session's runs on one tick and not the next is unambiguous: nothing about it changed, and the answer did.
/// </para>
/// <para>
/// <b>What it adds over <c>CellKeyedInterestTests</c>.</b> That fixture compares one tick's hit COUNT against arithmetic over a world that never moves,
/// which catches a filter centred on the wrong point; it cannot catch membership that is right on tick five and wrong on tick six. This one runs the world
/// forward under repair and compares the SET, every tick, under both broad phases.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class InterestStabilityTests : TestBase<InterestStabilityTests>
{
    private const double Radius = 192d;
    private const int Ticks = 20;

    /// <summary>Metres between pinned entities. Fine enough that several hundred sit inside the disc, coarse enough that the lattice stays small.</summary>
    private const float PinSpacing = 16f;

    /// <summary>Pinned lattice width. 37 x 16 m = 592 m, so the lattice reaches well past the 192 m radius on every side of the observers.</summary>
    private const int PinColumns = 37;

    private const int Movers = 3000;
    private const float MoverSpread = 640f;
    private const float MoverStepM = 18f;

    /// <summary>The marker. <c>ProjBounds.Speed</c> travels with the entity through every relocation, which no coordinate-based identification does.</summary>
    private const float PinnedSpeed = 1f;
    private const float MoverSpeed = 2f;

    /// <summary>Where the lattice is centred, and roughly where the observers stand.</summary>
    private const double Centre = (PinColumns - 1) / 2 * PinSpacing;

    private static ProjBounds PointAt(float x, float y, float speed) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = speed };

    /// <summary>A pinned entity's identity: the exact bits of the coordinates it was spawned at and is never written away from.</summary>
    private static long PinKey(float x, float y) =>
        ((long)BitConverter.SingleToInt32Bits(x) << 32) | (uint)BitConverter.SingleToInt32Bits(y);

    private static void DeclareSphere(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
    }

    /// <summary>
    /// A stationary observer's view of stationary entities is identical on every tick, while thousands of others move and the partitioner repairs.
    /// </summary>
    /// <param name="cellKeyed">Which broad phase resolves the sessions — the shared cell query, or one query per session.</param>
    [Test]
    public void AStationaryObserverNeverLosesAStationaryEntity([Values(false, true)] bool cellKeyed)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(20260920);

        // -- The world --------------------------------------------------------------------------------------------------------------------------------
        var pinned = new List<(float X, float Y)>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = 0; c < PinColumns; c++)
            {
                for (var r = 0; r < PinColumns; r++)
                {
                    var x = c * PinSpacing;
                    var y = r * PinSpacing;
                    pinned.Add((x, y));
                    tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(x, y, PinnedSpeed)));
                }
            }

            for (var i = 0; i < Movers; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(
                    (float)(Centre + (((random.NextDouble() * 2d) - 1d) * MoverSpread)),
                    (float)(Centre + (((random.NextDouble() * 2d) - 1d) * MoverSpread)),
                    MoverSpeed)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // -- The observers ----------------------------------------------------------------------------------------------------------------------------
        //
        // Four of them, inside ONE interest cell, so the cell-keyed arm actually shares a resolution. A single session forms a group of one and takes the
        // direct path whatever the switch says, which would have made both arms the same code.
        var cell = InterestPass.CellSideFor(Radius);
        var cellOrigin = Math.Floor(Centre / cell) * cell;
        var viewpoints = new[]
        {
            new Vector3D(cellOrigin + 6.5d, cellOrigin + 4.5d, 0d),
            new Vector3D(cellOrigin + 19.1d, cellOrigin + 11.7d, 0d),
            new Vector3D(cellOrigin + 33.3d, cellOrigin + 27.3d, 0d),
            new Vector3D(cellOrigin + 47.9d, cellOrigin + 41.1d, 0d),
        };

        var options = new SubscriptionsOptions { CellKeyedInterest = cellKeyed };
        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(AStationaryObserverNeverLosesAStationaryEntity) + cellKeyed, options);
        var sessions = harness.OpenSessions(viewpoints.Length, "near");
        for (var i = 0; i < viewpoints.Length; i++)
        {
            Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
        }

        // The arithmetic answer, once: which pinned entities each observer is obliged to reach, on every tick, forever.
        var expected = new HashSet<long>[viewpoints.Length];
        for (var s = 0; s < viewpoints.Length; s++)
        {
            expected[s] = [];
            foreach (var (x, y) in pinned)
            {
                var dx = x - viewpoints[s].X;
                var dy = y - viewpoints[s].Y;
                if ((dx * dx) + (dy * dy) <= Radius * Radius)
                {
                    expected[s].Add(PinKey(x, y));
                }
            }
        }

        Assert.That(expected[0], Has.Count.GreaterThan(350), "the fixture must put a real population inside the disc or it proves nothing");

        // -- Two priming ticks, then the run ----------------------------------------------------------------------------------------------------------
        //
        // The first pass discovers every cluster with no block; the blocks step attaches them; the second pass is the first whose runs all carry one.
        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);
        harness.CreateRequestedBlocks();

        var failures = new StringBuilder();
        var ticksShort = 0;
        var worstMissing = 0;
        var totalLeaves = 0L;
        var noBlockRuns = 0L;
        var departedRuns = 0L;

        for (var tick = 4L; tick <= Ticks; tick++)
        {
            DriftMovers(dbe, random);
            dbe.WriteTickFence(tick);

            // Where every PINNED entity sits right now, keyed by the cluster and slot an interest run names it by.
            var placement = SnapshotPinned(dbe);
            Assert.That(placement, Has.Count.EqualTo(pinned.Count),
                $"tick {tick}: {placement.Count} pinned entities are reachable through the cluster walk, of {pinned.Count} spawned. They were never written "
                + "again, so any shortfall is the ECS losing them and not subscriptions");

            harness.RunPass(tick);
            harness.CreateRequestedBlocks();

            for (var i = 0; i < harness.Interest.TickSessionCount; i++)
            {
                var s = IndexOf(sessions, harness.Interest.SessionAt(i));
                if (s < 0)
                {
                    continue;
                }

                var reached = new HashSet<long>();
                foreach (var run in harness.Interest.HitsOf(i))
                {
                    if ((run.Flags & InterestRunFlags.NoBlock) != 0)
                    {
                        noBlockRuns++;
                    }

                    if ((run.Flags & InterestRunFlags.Departed) != 0)
                    {
                        departedRuns++;
                    }

                    var bits = run.Slots;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (placement.TryGetValue(((long)run.ChunkId << 8) | (uint)slot, out var pin))
                        {
                            reached.Add(pin);
                        }
                    }
                }

                totalLeaves += harness.Interest.LeavesOf(i).Length;

                var missing = 0;
                var firstMissing = 0L;
                foreach (var want in expected[s])
                {
                    if (!reached.Contains(want))
                    {
                        if (missing == 0)
                        {
                            firstMissing = want;
                        }

                        missing++;
                    }
                }

                if (missing > 0)
                {
                    ticksShort++;
                    worstMissing = Math.Max(worstMissing, missing);
                    if (failures.Length < 3000)
                    {
                        var mx = BitConverter.Int32BitsToSingle((int)(firstMissing >> 32));
                        var my = BitConverter.Int32BitsToSingle((int)firstMissing);
                        var dx = mx - viewpoints[s].X;
                        var dy = my - viewpoints[s].Y;
                        failures.AppendLine(
                            $"  tick {tick} session {s}: {missing} of {expected[s].Count} pinned entities went unreached. "
                            + $"First is at ({mx}, {my}), {Math.Sqrt((dx * dx) + (dy * dy)):F1} m from the viewpoint - inside a {Radius} m radius.");
                    }
                }

                var surplus = 0;
                foreach (var got in reached)
                {
                    if (!expected[s].Contains(got))
                    {
                        surplus++;
                    }
                }

                if (surplus > 0 && failures.Length < 3000)
                {
                    failures.AppendLine($"  tick {tick} session {s}: {surplus} pinned entities were reached that are OUTSIDE the radius.");
                }
            }
        }

        TestContext.Out.WriteLine(
            $"cellKeyed={cellKeyed}: {pinned.Count} pinned + {Movers} movers, {viewpoints.Length} stationary observers, ticks 4..{Ticks}. "
            + $"{ticksShort} (tick, session) pairs lost at least one pinned entity; worst {worstMissing}. "
            + $"{totalLeaves} leaves raised, {noBlockRuns} NoBlock runs, {departedRuns} Departed runs.");

        // The leave total above is ZERO BY CONSTRUCTION and is printed only so that a reader does not mistake it for a result. A leave is raised by
        // comparing this tick's mask against the one the session's last PUBLISHED frame committed, and this harness runs the interest pass alone — no
        // frame stage, so nothing is ever committed, so the committed mask is always empty and `held & ~mask` is always zero. What this fixture does
        // assert is the half that does not need the frame stage: that the runs name exactly the entities geometry says they should, on every tick.
        // The client-visible question is settled in StationaryChurnTests and WalkingObserverChurnTests, which drive the whole track.
        Assert.That(totalLeaves, Is.Zero,
            "the interest pass raised a leave with no frame stage to have committed a mask for it to differ from, which should be impossible");

        Assert.That(ticksShort, Is.Zero,
            $"a stationary observer stopped reaching entities that have not moved since they were spawned, on {ticksShort} (tick, session) pairs, the worst "
            + $"losing {worstMissing}. Nothing about those entities changed and the observers never moved, so the interest pass is dropping what it is "
            + $"obliged to return.{Environment.NewLine}{failures}");
    }

    /// <summary>Moves every mover and nothing else. Pinned entities are identified by the marker they carry, not by their coordinates.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="random">The drift source.</param>
    private static void DriftMovers(DatabaseEngine dbe, Random random)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    ref readonly var b = ref cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];
                    if (b.Speed == PinnedSpeed)
                    {
                        continue;
                    }

                    var x = (float)Clamp(b.Bounds.MinX + (((random.NextDouble() * 2d) - 1d) * MoverStepM));
                    var y = (float)Clamp(b.Bounds.MinY + (((random.NextDouble() * 2d) - 1d) * MoverStepM));
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(x, y, MoverSpeed));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>Every pinned entity's current cluster and slot, keyed <c>(chunkId &lt;&lt; 8) | slot</c>, mapped to the identity it was spawned with.</summary>
    /// <param name="dbe">The engine.</param>
    /// <returns>The placement map.</returns>
    private static Dictionary<long, long> SnapshotPinned(DatabaseEngine dbe)
    {
        var map = new Dictionary<long, long>();
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var chunkId = cluster.ChunkId;
                var occupancy = cluster.OccupancyBits;
                var span = cluster.GetReadOnlySpan(ProjCreature.Bounds);
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    ref readonly var b = ref span[slot];
                    if (b.Speed != PinnedSpeed)
                    {
                        continue;
                    }

                    map[((long)chunkId << 8) | (uint)slot] = PinKey(b.Bounds.MinX, b.Bounds.MinY);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return map;
    }

    private static int IndexOf(SessionId[] sessions, SessionId session)
    {
        for (var i = 0; i < sessions.Length; i++)
        {
            if (sessions[i].Value == session.Value)
            {
                return i;
            }
        }

        return -1;
    }

    private static double Clamp(double v)
    {
        var lo = Centre - MoverSpread;
        var hi = Centre + MoverSpread;
        return v < lo ? lo : v > hi ? hi : v;
    }
}
