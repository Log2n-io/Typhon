using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Cluster-granular interest: the broad phase answers with the CLUSTERS a disc reaches and never reads an entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test is CONTAINMENT, not identity</b>, and that is the whole difference between this shape and the cell-keyed one beside it. A
/// cluster whose bounds reach inside the disc is taken whole, so one straddling the rim contributes entities outside it: the resident view is a SUPERSET of
/// the exact view, never a subset. Asserting identity would be asserting the feature away; asserting only a count would be satisfied by a pair of
/// compensating errors, which is exactly how an entity gets silently dropped. So these fixtures compare the two paths SLOT BY SLOT and require the
/// containment to hold for every one.
/// </para>
/// <para>
/// <b>Why the direction matters more than the size.</b> An entity the resident view keeps that the exact view would not is bandwidth — the client is told
/// about something slightly too far away. An entity the resident view drops is a player who never appears, and nothing downstream can recover it: the
/// difference the frame stage takes is against the view, so an entity absent from the view is absent from every frame that view ever produces. One
/// direction is a cost, the other is a bug, and this fixture is here to keep them apart.
/// </para>
/// <para>
/// <b>Both arms run on one binary.</b> <c>SubscriptionsOptions.ResidentInterest</c> switches the shape, so a failure is the shape's and not a build's.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ResidentInterestTests : TestBase<ResidentInterestTests>
{
    private const float Spacing = 7.5f;
    private const int Columns = 133;
    private const int CreatureCount = Columns * Columns;
    private const double Radius = 192d;
    private const double WorldEdge = Columns * Spacing;

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void DeclareSphere(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>How many grid points lie within a radius of a centre — the arithmetic answer the exact arm must reproduce.</summary>
    private static int PointsWithin(double cx, double cy, double radius)
    {
        var inside = 0;
        var rsq = radius * radius;
        for (var i = 0; i < CreatureCount; i++)
        {
            var dx = (i % Columns * Spacing) - cx;
            var dy = (i / Columns * Spacing) - cy;
            if ((dx * dx) + (dy * dy) <= rsq)
            {
                inside++;
            }
        }

        return inside;
    }

    private sealed class Arm
    {
        /// <summary>Every (cluster, slot) each session resolved, keyed by session id.</summary>
        public Dictionary<uint, HashSet<long>> Slots;

        public long ClustersCollected;
        public long ClustersAccepted;
    }

    /// <summary>Runs one arm and returns each session's resolved slot set, keyed by session.</summary>
    /// <remarks>
    /// Keyed by SESSION, not by index: both arms reorder this tick's sessions so a cell's members are contiguous, so reading results by index would compare
    /// one arm's session against another's.
    /// </remarks>
    private Arm Resolve(bool resident, Vector3D[] viewpoints, string name, out SessionId[] sessions)
    {
        // A fresh provider per arm: the engine is built once per provider and its spatial grid is configured before its archetypes, so a second
        // SetupEngine on the same one is refused. Both arms need their own world, and it must be the SAME world — the comparison below is by (cluster,
        // slot), which only names the same entity while the two runs populate identically. They do: one seedless loop over a fixed lattice.
        (ServiceProvider as IDisposable)?.Dispose();
        Setup();

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var options = new SubscriptionsOptions { CellKeyedInterest = true, ResidentInterest = resident };
        var harness = InterestHarness.Create(dbe, DeclareSphere, name, options);
        try
        {
            sessions = harness.OpenSessions(viewpoints.Length, "near");
            harness.RunPass(1);
            harness.CreateRequestedBlocks();

            for (var i = 0; i < viewpoints.Length; i++)
            {
                Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
            }

            harness.RunPass(2);
            harness.CreateRequestedBlocks();
            harness.RunPass(3);

            var slots = new Dictionary<uint, HashSet<long>>();
            for (var i = 0; i < harness.Interest.TickSessionCount; i++)
            {
                var set = new HashSet<long>();
                foreach (ref readonly var run in harness.Interest.HitsOf(i))
                {
                    var bits = run.Slots;
                    while (bits != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;

                        // Cluster and slot together name the entity's storage, which both arms resolve against the same world.
                        set.Add(((long)run.ChunkId << 8) | (uint)slot);
                    }
                }

                slots[harness.Interest.SessionAt(i).Value] = set;
            }

            return new Arm { Slots = slots, ClustersCollected = harness.Interest.ClusterCandidatesCollected, ClustersAccepted = harness.Interest.ClusterCandidatesAccepted };
        }
        finally
        {
            harness.Dispose();
        }
    }

    /// <summary>
    /// Every entity the exact disc resolves is resolved by the cluster-granular pass too, for every session of a crowd.
    /// </summary>
    /// <remarks>
    /// <b>The exact arm is anchored to the geometry before it is used as a reference.</b> Its hit count is checked against the number of lattice points
    /// inside each session's own radius, computed arithmetically, so a containment that held only because the reference had itself collapsed would be
    /// caught here rather than reported as a pass.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-17")]
    public void TheClusterGranularViewContainsEveryEntityTheExactDiscResolves()
    {
        // Eight viewpoints inside ONE 64 m cell, at offsets that are not multiples of the 7.5 m lattice, so the eight discs hold distinct populations and a
        // filter centred on the cell rather than on each session would not satisfy the anchor below.
        var cell = InterestPass.CellSideFor(Radius);
        var origin = (Math.Floor(WorldEdge / 2d / cell) * cell) + (cell / 4d);
        var viewpoints = new Vector3D[8];
        for (var i = 0; i < viewpoints.Length; i++)
        {
            viewpoints[i] = new Vector3D(origin + (i % 4 * 7.1d), origin + (i / 4 * 5.3d), 0d);
        }

        var exact = Resolve(resident: false, viewpoints, nameof(TheClusterGranularViewContainsEveryEntityTheExactDiscResolves) + "-exact", out var lhs);
        var cluster = Resolve(resident: true, viewpoints, nameof(TheClusterGranularViewContainsEveryEntityTheExactDiscResolves) + "-resident", out var rhs);

        Assert.Multiple(() =>
        {
            // ANCHOR: the reference arm is right before it is used as one.
            for (var i = 0; i < viewpoints.Length; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius);
                Assert.That(exact.Slots[lhs[i].Value], Has.Count.EqualTo(expected),
                    $"the EXACT arm resolved {exact.Slots[lhs[i].Value].Count} entities for session {i} against the {expected} inside its own radius. "
                    + "Nothing below means anything until this holds");
            }

            // ANTI-VACUITY: the resident path really ran, and it ran at cluster granularity.
            Assert.That(cluster.ClustersCollected, Is.GreaterThan(0),
                "the resident arm collected no cluster candidates at all, so the shape under test never ran");
            Assert.That(exact.ClustersCollected, Is.Zero,
                "the exact arm collected cluster candidates, so the two arms are not the two shapes");

            // THE PROPERTY.
            for (var i = 0; i < viewpoints.Length; i++)
            {
                var a = exact.Slots[lhs[i].Value];
                var b = cluster.Slots[rhs[i].Value];
                Assert.That(b, Has.Count.GreaterThanOrEqualTo(a.Count),
                    $"session {i} resolved {b.Count} entities at cluster granularity against {a.Count} exactly; the coarser view cannot be the smaller one");

                var missing = new HashSet<long>(a);
                missing.ExceptWith(b);
                Assert.That(missing, Is.Empty,
                    $"session {i}: {missing.Count} entities inside its disc are absent from the cluster-granular view. Each one is a player who never "
                    + "appears — the difference the frame stage takes is against the view, so an entity missing here is missing from every frame it "
                    + "ever produces");
            }
        });
    }

    /// <summary>
    /// A session alone in its cell takes the ungrouped path, and containment holds there too.
    /// </summary>
    /// <remarks>
    /// The ungrouped path queries the session's own disc rather than an enlarged cell, so it is a different piece of code with a different box, and a
    /// containment proved only for the grouped one says nothing about it. Forty of two hundred sessions take it on the workload this was built for.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-17")]
    public void ContainmentHoldsForASessionResolvingAlone()
    {
        // Far enough apart that no two share a 64 m interest cell.
        var viewpoints = new Vector3D[4];
        for (var i = 0; i < viewpoints.Length; i++)
        {
            viewpoints[i] = new Vector3D(250d + (i * 220d), 300d + (i * 190d), 0d);
        }

        var exact = Resolve(resident: false, viewpoints, nameof(ContainmentHoldsForASessionResolvingAlone) + "-exact", out var lhs);
        var cluster = Resolve(resident: true, viewpoints, nameof(ContainmentHoldsForASessionResolvingAlone) + "-resident", out var rhs);

        Assert.Multiple(() =>
        {
            for (var i = 0; i < viewpoints.Length; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y, Radius);
                Assert.That(exact.Slots[lhs[i].Value], Has.Count.EqualTo(expected),
                    $"the EXACT arm resolved {exact.Slots[lhs[i].Value].Count} for session {i} against {expected} by geometry");

                var a = exact.Slots[lhs[i].Value];
                var b = cluster.Slots[rhs[i].Value];
                var missing = new HashSet<long>(a);
                missing.ExceptWith(b);
                Assert.That(missing, Is.Empty, $"session {i}: {missing.Count} entities inside its disc are absent from the cluster-granular view");
            }

            // The ungrouped path collects no cell candidates, so the anti-vacuity here is that it resolved anything at all.
            Assert.That(cluster.Slots[rhs[0].Value], Is.Not.Empty, "the resident arm resolved nothing, so containment is vacuous");
        });
    }
}
