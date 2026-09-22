using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The four quantities the incremental-interest design is argued from, measured rather than estimated
/// (<c>design/Subscriptions/15-incremental-interest.md</c> § 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist.</b> <c>14-as-built.md</c> § 7 could not state a per-hit cost, because nothing in the engine reports how many entities a session's
/// observer actually reaches in a tick. Every per-session figure in <c>13-density.md</c> is therefore a cost with no denominator, and the cell-keyed design
/// proposed to replace the current pass rests on three more numbers nobody has: how many clusters a disc spans, how many sessions share a cell, and how much
/// a cell-conservative broad phase would over-send.
/// </para>
/// <para>
/// <b>The world is sized to the density that matters, not to the shipped one.</b> Entities sit on a 7.5 m grid, which puts ~2 060 of them inside a 192 m
/// disc — the density <c>13-density.md</c>'s d05 measured and the point at which replication passes half the tick. A sparse fixture would report the numbers
/// that made the sparse measurements look fine.
/// </para>
/// <para>
/// <b>These are counts, never timings.</b> Every assertion here is a property of the geometry or of the pass's own bookkeeping; a wall-clock assertion in a
/// unit test measures the box. The perf claim lives in the demo's A/B, which is the only place it can.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class InterestCostTests : TestBase<InterestCostTests>
{
    /// <summary>Metres between neighbouring entities — chosen so a 192 m disc holds ~2 060, matching 13-density.md's d05.</summary>
    private const float Spacing = 7.5f;

    /// <summary>Entities per row. 133² = 17 689 entities over 1 km², the same population as the shipped x1 world at 64× its density.</summary>
    private const int Columns = 133;

    private const int CreatureCount = Columns * Columns;

    /// <summary>The demo's player radius.</summary>
    private const double Radius = 192d;

    private const double WorldEdge = Columns * Spacing;

    /// <summary>The spatial grid cell the demo ships with, kept in the sweep because 13-density.md quotes its result.</summary>
    private const double DemoGridCell = 256d;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    /// <summary>
    /// The per-session pass, explicitly — these fixtures describe what it costs, so they must not be measuring its replacement.
    /// </summary>
    /// <remarks>
    /// <c>CellKeyedInterest</c> defaults to ON. Left at the default, "two sessions in the same place each pay the full cost" would have run on the path
    /// where they share one query, and its claim would have been the opposite of what the code does — a remark contradicting its own fixture.
    /// </remarks>
    private static SubscriptionsOptions PullOptions => new() { CellKeyedInterest = false };

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

    /// <summary>
    /// How many grid points lie within a radius of a centre — arithmetic, so the measured hit count has an independent answer to be checked against.
    /// </summary>
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

    /// <summary>
    /// <b>Q1 and Q2 — hits per session per tick, and clusters per disc.</b> The denominator every per-session cost in 13-density.md is missing, and the
    /// span the cell-keyed broad phase would replace.
    /// </summary>
    [Test]
    public void ASessionsDiscCostsItsWholePopulationInHitsEveryTick()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(ASessionsDiscCostsItsWholePopulationInHitsEveryTick), PullOptions);
        var sessions = harness.OpenSessions(1, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        var centre = new Vector3D(WorldEdge / 2d, WorldEdge / 2d, 0d);
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], centre), Is.True);

        harness.RunPass(2);
        harness.CreateRequestedBlocks();

        // THREE consecutive ticks with the session standing still, because the claim is that the pass costs the same every tick regardless of how little
        // changed — not that it costs that once. A single tick would be consistent with a pass that caches.
        var hits = new int[3];
        var runs = new int[3];
        for (var t = 0; t < 3; t++)
        {
            harness.RunPass(3 + t);
            hits[t] = harness.Interest.HitsCountOf(0);
            runs[t] = harness.Interest.RunCountOf(0);
        }

        var expected = PointsWithin(centre.X, centre.Y, Radius);
        TestContext.Out.WriteLine($"Q1/Q2: disc holds {expected} points; hits per tick {string.Join(", ", hits)}; clusters spanned {string.Join(", ", runs)}");

        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.GreaterThan(1500),
                "the fixture must reach the density the design is argued at, or its numbers describe the sparse world that already measured fine");
            Assert.That(hits, Is.All.EqualTo(expected),
                $"every tick must cost the disc's whole population in hits. A tick costing less would mean the pass already exploits temporal coherency, "
                + "which is the premise this measurement exists to check");
            // A MAGNITUDE, because the claim is a magnitude: the merge scanned this list once per hit, so "many clusters" is the whole argument and
            // "more than one" would be satisfied by two.
            Assert.That(runs, Is.All.GreaterThan(20),
                $"a disc holding {expected} entities spans {runs[0]} clusters. The linear merge this measurement retired scanned that list once per hit, so "
                + "a disc spanning a handful would mean the scan was never the cost 14 § 5.1 attributes to it");
        });
    }

    /// <summary>
    /// <b>Q3 — two sessions standing on the same spot repeat each other's work exactly.</b> The spatial half of the argument, stated as the equality it is.
    /// </summary>
    [Test]
    public void TwoSessionsInTheSamePlaceEachPayTheFullCost()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(TwoSessionsInTheSamePlaceEachPayTheFullCost), PullOptions);
        var sessions = harness.OpenSessions(2, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        // Two metres apart: close enough that their discs are 99 % the same entities, far enough that they are genuinely two viewpoints.
        var centre = new Vector3D(WorldEdge / 2d, WorldEdge / 2d, 0d);
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], centre), Is.True);
        Assert.That(harness.Sessions.SetViewpoint(sessions[1], new Vector3D(centre.X + 2d, centre.Y, centre.Z)), Is.True);

        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var a = harness.Interest.HitsCountOf(0);
        var b = harness.Interest.HitsCountOf(1);
        var shared = PointsWithin(centre.X, centre.Y, Radius);

        TestContext.Out.WriteLine(
            $"Q3: two sessions 2 m apart cost {a} + {b} = {a + b} hits to resolve ~{shared} shared entities, and shared {harness.SessionsSharedLastPass} "
            + "resolutions");

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.GreaterThan(1500), "anti-vacuity: the first session must actually see the crowd");
            Assert.That(b, Is.GreaterThan(1500), "anti-vacuity: so must the second");

            // The POINT of the fixture: under the PER-SESSION pass the total is the SUM, not the union.
            Assert.That(a + b, Is.GreaterThan(shared * 19 / 10),
                $"two sessions two metres apart cost {a + b} hits between them against {shared} distinct entities in view. Anything approaching {shared} "
                + "would mean co-located observers share their resolution, which the per-session pass does not do");

            // And the hit counts CANNOT tell you whether anything was shared — the cell-keyed path credits each session with its own disc too. This is the
            // measurement that distinguishes the two shapes, and it is here so that a future reader does not mistake the sum above for evidence about
            // sharing in general rather than about this pass.
            Assert.That(harness.SessionsSharedLastPass, Is.Zero,
                "this fixture is pinned to the per-session pass, so nothing may be shared; the hit totals above cannot detect sharing on their own");
        });
    }

    /// <summary>
    /// <b>Q4 — the over-send a cell-conservative broad phase would cause, and how much of a disc it could share without any per-observer test.</b>
    /// </summary>
    /// <remarks>
    /// Pure geometry over the fixture's own grid, so it is exact and independent of the engine. It decides § 3.4 of the design: a cell size whose
    /// fully-contained fraction is near zero cannot share anything, because every cell would need a per-observer distance test.
    /// </remarks>
    [Test]
    public void CellConservativeInterestOverSendsByAKnownFactorThatDependsOnCellSize()
    {
        var centre = new Vector3D(WorldEdge / 2d, WorldEdge / 2d, 0d);
        var inDisc = PointsWithin(centre.X, centre.Y, Radius);

        var report = new List<(double Cell, int Covered, int Interior, double OverSend, double InteriorShare)>();
        // DemoGridCell is in there because the demo's spatial grid defaults to it and 13-density.md quotes the result; the rest are radius-relative, so
        // they move with the observer rather than with one app's map.
        foreach (var cell in new[] { DemoGridCell, Radius, Radius / 2d, InterestPass.CellSideFor(Radius), Radius / 6d })
        {
            var covered = 0;
            var interior = 0;
            var half = cell * Math.Sqrt(2d) / 2d;

            for (var i = 0; i < CreatureCount; i++)
            {
                var x = i % Columns * Spacing;
                var y = i / Columns * Spacing;

                // The cell this entity sits in, and that cell's centre.
                var cx = (Math.Floor(x / cell) + 0.5d) * cell;
                var cy = (Math.Floor(y / cell) + 0.5d) * cell;
                var dx = cx - centre.X;
                var dy = cy - centre.Y;
                var d = Math.Sqrt((dx * dx) + (dy * dy));

                // Touched: the cell's nearest corner is inside the disc, so a broad phase keyed on cells includes every entity in it.
                if (d - half <= Radius)
                {
                    covered++;

                    // Interior: the cell's FARTHEST corner is inside the disc, so every entity in it is visible to every observer registered on that cell —
                    // no per-observer distance test is needed and the work can be shared outright.
                    if (d + half <= Radius)
                    {
                        interior++;
                    }
                }
            }

            report.Add((cell, covered, interior, (double)covered / inDisc, (double)interior / inDisc));
        }

        TestContext.Out.WriteLine($"Q4: disc holds {inDisc} entities at radius {Radius}");
        foreach (var r in report)
        {
            TestContext.Out.WriteLine(
                $"  cell {r.Cell,4:F0} m: covered {r.Covered,6} (over-send {r.OverSend:F2}x), shareable without a test {r.Interior,6} ({r.InteriorShare:P0})");
        }

        // Located by VALUE, not by position: inserting a cell size into the sweep would silently retarget assertions written about two particular ones.
        // `fine` is the size the engine actually uses, so a change to CellsPerRadius moves this assertion with it instead of leaving it behind.
        var demo = report.Find(r => r.Cell == DemoGridCell);
        var fineCell = InterestPass.CellSideFor(Radius);
        var fine = report.Find(r => r.Cell == fineCell);
        Assert.That(fine.Covered, Is.GreaterThan(0),
            $"the sweep does not include the engine's own cell size of {fineCell} m, so this fixture is describing sizes nothing uses");

        Assert.Multiple(() =>
        {
            Assert.That(demo.OverSend, Is.GreaterThan(1d),
                "anti-vacuity: a conservative broad phase must cover MORE than the disc, or it is not conservative");

            // The finding that decided the cell size, stated as the fact it is: at the grid cell the demo ships, a disc of this radius has NO fully
            // contained cell at all, so every cell it touches would need a per-observer test and there is no interior left to share.
            Assert.That(demo.InteriorShare, Is.LessThan(0.05d),
                $"at the demo's {demo.Cell} m grid cell, {demo.InteriorShare:P0} of a {Radius} m disc sits in fully-contained cells. A broad phase keyed on "
                + "cells that size shares nothing, which is why the interest cell is derived from the radius instead");

            // And the engine's own cell must be materially better on BOTH axes, not merely different — a cell that over-sent less while sharing no more
            // would buy nothing, and one that shared more while over-sending more might not pay for itself.
            Assert.That(fine.InteriorShare, Is.GreaterThan(0.4d),
                $"at {fine.Cell} m cells {fine.InteriorShare:P0} of the disc must be shareable without any test, or the two-phase design has no interior "
                + "to share and reduces to the per-session pass it replaces");
            Assert.That(fine.InteriorShare, Is.GreaterThan(demo.InteriorShare * 4d),
                $"the engine's cell shares {fine.InteriorShare:P0} against the demo grid's {demo.InteriorShare:P0}; the whole reason for a separate interest "
                + "cell is that the difference is large");
            Assert.That(fine.OverSend, Is.LessThan(demo.OverSend),
                $"the engine's cell over-sends {fine.OverSend:F2}x against {demo.OverSend:F2}x, so it must also be the cheaper broad phase");
        });
    }
}
