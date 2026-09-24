using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Typhon.Schema.Definition;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// The ClientRegion observer and its near budget over a grid of point entities (09 § 7), so every expectation is arithmetic: what lies in the hull, which
/// cell a point is in, how far a cell is from the hull's centroid.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class ClientRegionObserverTests : TestBase<ClientRegionObserverTests>
{
    private const int Columns = 40;

    private const float Spacing = 10f;

    private const int CreatureCount = Columns * Columns;

    /// <summary>The replication cell: 4 × 4 grid points in most cells.</summary>
    private const double CellM = 40;

    private const double MaxEdgeM = 1000;

    private const int Budget = 200;

    /// <summary>The fixtures' tick: 10 Hz, so the deadband's second is ten ticks.</summary>
    private const int TicksPerSecond = 10;

    private long _tick;

    private static ProjBounds PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static (float X, float Y) GridPoint(int i) => (i % Columns * Spacing, i / Columns * Spacing);

    private static List<EntityId> Populate(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(CreatureCount);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            ids.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(x, y))));
        }

        tx.Commit();
        return ids;
    }

    private FrameHarness Harness(DatabaseEngine dbe, int budget, string name)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p =>
            {
                var region = p.ClientRegion(MaxEdgeM);
                if (budget > 0)
                {
                    region.Near(budget);
                }

                region.Of<ProjCreature>();
            });
        }, name, new SubscriptionsOptions { MaxSessions = 16, EnterBudgetPerFrame = 4096, ReplicationCellM = CellM });
        harness.RunFence = true;
        _tick = 0;
        return harness;
    }

    private void Run(FrameHarness harness, SessionId session, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(++_tick);
            harness.Deliver(session);
        }
    }

    private static RegionVertex[] Polygon(double cx, double cy, double radius, int sides, double angle)
    {
        var vertices = new RegionVertex[sides];
        for (var v = 0; v < sides; v++)
        {
            var a = angle + (v * Math.PI * 2.0 / sides);
            vertices[v] = new RegionVertex { X = cx + (Math.Cos(a) * radius), Y = cy + (Math.Sin(a) * radius) };
        }

        return vertices;
    }

    /// <summary>Point in a counter-clockwise convex polygon, from its vertices — not from the planes the engine derives.</summary>
    private static bool Inside(RegionVertex[] polygon, double x, double y)
    {
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            if (((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X)) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Cell(double v) => (int)Math.Floor((v + ProjectionTestSchema.WorldExtentM) / CellM);

    private static HashSet<uint> Held(FrameHarness harness, SessionId session) =>
        [.. harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx)];

    /// <summary>A session holds exactly the grid points its region's hull contains, and nothing before it sends one (SUB-16).</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ARegionHoldsExactlyTheGridPointsInItsHull()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = Harness(dbe, 0, nameof(ARegionHoldsExactlyTheGridPointsInItsHull));
        var session = harness.OpenSessions(1, "god")[0];
        Run(harness, session, 3);
        Assert.That(Held(harness, session), Is.Empty, "a session that sent no region holds nothing");

        var polygon = Polygon(201.3, 187.7, 150.1, 5, 0.3);
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, polygon, 2), Is.True);
        Run(harness, session, 4);

        var expected = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            expected += Inside(polygon, x, y) ? 1 : 0;
        }

        Assert.That(expected, Is.GreaterThan(100).And.LessThan(CreatureCount / 2), "a meaningful minority of the grid lies in the hull");
        Assert.That(Held(harness, session), Has.Count.EqualTo(expected));

        // The region moves by half its width: the client follows it, by the crescent and not by a reset.
        var resets = harness.Subscriptions.Push.Resets;
        var moved = Polygon(281.3, 207.7, 150.1, 5, 0.3);
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, moved, 2), Is.True);
        Run(harness, session, 3);
        expected = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            expected += Inside(moved, x, y) ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Has.Count.EqualTo(expected), "the moved hull");
            Assert.That(harness.Subscriptions.Push.Resets, Is.EqualTo(resets), "a move that keeps most delivered cells is swept, not reset");
        });
    }

    /// <summary>
    /// A session that switches between a ClientRegion profile and a Sphere one is reset each time (09 § 7): what it held named another shape's geometry. It
    /// ends up holding exactly the new shape's set, both ways.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void ASwitchBetweenARegionAndASphereResets()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p => p.ClientRegion(MaxEdgeM).Of<ProjCreature>());
            subs.Profile("near", p => p.Sphere(100).Of<ProjCreature>());
        }, nameof(ASwitchBetweenARegionAndASphereResets), new SubscriptionsOptions { MaxSessions = 16, EnterBudgetPerFrame = 4096, ReplicationCellM = CellM });
        harness.RunFence = true;
        _tick = 0;
        var session = harness.OpenSessions(1, "god")[0];
        var polygon = Polygon(201.3, 187.7, 150.1, 5, 0.3);
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, polygon, 2), Is.True);
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(100.3, 100.7, 0)), Is.True);
        Run(harness, session, 4);

        var inHull = 0;
        var inDisc = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            inHull += Inside(polygon, x, y) ? 1 : 0;
            var (dx, dy) = (x - 100.3, y - 100.7);
            inDisc += (dx * dx) + (dy * dy) <= 100 * 100 ? 1 : 0;
        }

        var push = harness.Subscriptions.Push;
        Assert.That(Held(harness, session), Has.Count.EqualTo(inHull), "the region");

        var resets = push.Resets;
        Assert.That(harness.Sessions.SetProfile(session, "near"), Is.True);
        Run(harness, session, 4);
        Assert.Multiple(() =>
        {
            Assert.That(push.Resets, Is.GreaterThan(resets), "the switch to a Sphere reset the session");
            Assert.That(Held(harness, session), Has.Count.EqualTo(inDisc), "the disc");
        });

        resets = push.Resets;
        Assert.That(harness.Sessions.SetProfile(session, "god"), Is.True);
        Run(harness, session, 4);
        Assert.Multiple(() =>
        {
            Assert.That(push.Resets, Is.GreaterThan(resets), "the switch back to the region reset the session");
            Assert.That(Held(harness, session), Has.Count.EqualTo(inHull), "the region again");
        });
    }

    /// <summary>
    /// A near budget delivers whole cells nearest the hull's centroid while the counted entities fit (SUB-23): the client holds every entity of the cells it
    /// was delivered and none of the others, the count is within the budget and short of it by less than a cell, and no undelivered cell is nearer the
    /// centroid than a delivered one. A static scene then changes nothing.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-23")]
    public void ANearBudgetDeliversWholeCellsNearestTheCentroid()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, Budget, nameof(ANearBudgetDeliversWholeCellsNearestTheCentroid));
        var session = harness.OpenSessions(1, "god")[0];
        var polygon = Polygon(195.3, 195.7, 290, 4, Math.PI / 4);
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, polygon, 2), Is.True);
        Run(harness, session, 4);

        var push = harness.Subscriptions.Push;
        var held = Held(harness, session);
        var netIds = NetIdsByEntity(harness, ids);
        var focusCx = Cell(195.3);
        var focusCy = Cell(195.7);
        var nearestUnheld = int.MaxValue;
        var farthestHeld = 0;
        var partial = new Dictionary<(int, int), (int Held, int Total)>();
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            var cell = (Cell(x), Cell(y));
            var ring = Math.Max(Math.Abs(cell.Item1 - focusCx), Math.Abs(cell.Item2 - focusCy));
            var isHeld = held.Contains(netIds[ids[i]]);
            var tally = partial.GetValueOrDefault(cell);
            partial[cell] = (tally.Held + (isHeld ? 1 : 0), tally.Total + 1);
            if (isHeld)
            {
                farthestHeld = Math.Max(farthestHeld, ring);
            }
            else
            {
                nearestUnheld = Math.Min(nearestUnheld, ring);
            }
        }

        var cellsHalfHeld = 0;
        foreach (var (h, t) in partial.Values)
        {
            cellsHalfHeld += h != 0 && h != t ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(held.Count, Is.LessThanOrEqualTo(Budget).And.GreaterThan(Budget - 16), "within the budget, and short of it by less than a cell");
            Assert.That(held.Count, Is.EqualTo(push.RegionHeldOf(session)), "the estimate counts exactly what a hull covering every cell holds");
            Assert.That(cellsHalfHeld, Is.Zero, "a budget delivers whole cells, never part of one");
            Assert.That(farthestHeld, Is.LessThanOrEqualTo(nearestUnheld), "no undelivered cell is nearer the centroid than a delivered one");
        });

        // A static scene: nothing delivered or taken back for three seconds.
        var delivered = push.RegionCellsDelivered;
        var undelivered = push.RegionCellsUndelivered;
        Run(harness, session, 3 * TicksPerSecond);
        Assert.That((push.RegionCellsDelivered, push.RegionCellsUndelivered), Is.EqualTo((delivered, undelivered)), "a static scene changes no delivery");
    }

    /// <summary>
    /// A region that pans holds the cells nearest its new centroid (09 § 7): the cells past the new ring order's prefix are taken back and the prefix is
    /// delivered — the deadband does not hold a moving camera to the cells around where it was.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-23")]
    public void APannedRegionHoldsTheCellsNearestItsNewCentroid()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, Budget, nameof(APannedRegionHoldsTheCellsNearestItsNewCentroid));
        var session = harness.OpenSessions(1, "god")[0];
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(195.3, 195.7, 290, 4, Math.PI / 4), 2), Is.True);
        Run(harness, session, 4);

        // Five cells to the side: the hull still covers the grid, its centroid does not stay.
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(395.3, 195.7, 290, 4, Math.PI / 4), 2), Is.True);
        Run(harness, session, 3);

        var held = Held(harness, session);
        var netIds = NetIdsByEntity(harness, ids);
        var focusCx = Cell(395.3);
        var focusCy = Cell(195.7);
        var nearestUnheld = int.MaxValue;
        var farthestHeld = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            var ring = Math.Max(Math.Abs(Cell(x) - focusCx), Math.Abs(Cell(y) - focusCy));
            if (held.Contains(netIds[ids[i]]))
            {
                farthestHeld = Math.Max(farthestHeld, ring);
            }
            else
            {
                nearestUnheld = Math.Min(nearestUnheld, ring);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(farthestHeld, Is.LessThanOrEqualTo(nearestUnheld), "no undelivered cell is nearer the new centroid than a delivered one");
            Assert.That(held.Count, Is.LessThanOrEqualTo(Budget).And.GreaterThan(Budget - 16));
        });
    }

    /// <summary>
    /// The deadband (09 § 7, F5): when the estimate falls under 0.9 × budget, more is delivered only once it has stayed there for a second — not on the next
    /// frame, which would deliver and take back the same cell as counts wobble around the budget.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-23")]
    public void MoreIsDeliveredOnlyAfterASecondUnderNinetyPercent()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, Budget, nameof(MoreIsDeliveredOnlyAfterASecondUnderNinetyPercent));
        var session = harness.OpenSessions(1, "god")[0];
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(195.3, 195.7, 290, 4, Math.PI / 4), 2), Is.True);
        Run(harness, session, 4);

        // A third of what is held destroyed: the estimate falls under 0.9 × budget.
        var push = harness.Subscriptions.Push;
        var held = Held(harness, session);
        var netIds = NetIdsByEntity(harness, ids);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var destroyed = 0;
            foreach (var id in ids)
            {
                if (held.Contains(netIds[id]) && destroyed++ < held.Count / 3)
                {
                    tx.Destroy(id);
                }
            }

            tx.Commit();
        }

        var delivered = push.RegionCellsDelivered;
        Run(harness, session, TicksPerSecond - 1);
        Assert.That(push.RegionHeldOf(session), Is.LessThan(Budget * 9 / 10), "the estimate is under the deadband's lower mark");
        Assert.That(push.RegionCellsDelivered, Is.EqualTo(delivered), "nothing more is delivered within the second");

        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(push.RegionCellsDelivered, Is.GreaterThan(delivered), "a second under the mark delivers more");
            Assert.That(Held(harness, session).Count, Is.LessThanOrEqualTo(Budget).And.GreaterThan(Budget - 16));
        });
    }

    /// <summary>
    /// Past 1.1 × budget, the farthest cells are taken back at once, whole, until the estimate is within the budget (SUB-23): what the client holds stays
    /// within 1.1 × budget on every frame, and no cell it still holds is farther from the centroid than one taken back.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-23")]
    public void PastTenPercentOverTheFarthestCellsAreTakenBack()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, Budget, nameof(PastTenPercentOverTheFarthestCellsAreTakenBack));
        var session = harness.OpenSessions(1, "god")[0];
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(195.3, 195.7, 290, 4, Math.PI / 4), 2), Is.True);
        Run(harness, session, 4);

        // A hundred more entities in the centroid's own cell: the estimate passes 1.1 × budget.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(197f + (i % 10 * 0.5f), 197f + (i / 10 * 0.5f))));
            }

            tx.Commit();
        }

        var push = harness.Subscriptions.Push;
        var undelivered = push.RegionCellsUndelivered;
        var most = 0;
        for (var t = 0; t < 4; t++)
        {
            Run(harness, session, 1);
            most = Math.Max(most, Held(harness, session).Count);
        }

        var held = Held(harness, session);
        var netIds = NetIdsByEntity(harness, ids);
        var focusCx = Cell(195.3);
        var focusCy = Cell(195.7);
        var nearestUnheld = int.MaxValue;
        var farthestHeld = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var (x, y) = GridPoint(i);
            var ring = Math.Max(Math.Abs(Cell(x) - focusCx), Math.Abs(Cell(y) - focusCy));
            if (held.Contains(netIds[ids[i]]))
            {
                farthestHeld = Math.Max(farthestHeld, ring);
            }
            else
            {
                nearestUnheld = Math.Min(nearestUnheld, ring);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(push.RegionCellsUndelivered, Is.GreaterThan(undelivered), "cells were taken back");
            Assert.That(most, Is.LessThanOrEqualTo(Budget * 11 / 10), "the client never held more than 1.1 × budget");
            Assert.That(held.Count, Is.EqualTo(push.RegionHeldOf(session)), "whole cells: the estimate is what the client holds");
            Assert.That(farthestHeld, Is.LessThanOrEqualTo(nearestUnheld), "the farthest cells went first");
        });
    }

    /// <summary>
    /// A netId reused inside the log window (SUB-06, 09 § 7): a session misses frames while entities it holds are destroyed and new ones, spawned in its
    /// region, are given their identities. The catch-up would carry an identity's leave and enter in one frame, which a client cannot order, so the frame
    /// resets instead — and the client ends up holding the new entities under the reused identities, with the new entities' fields.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-06")]
    public void AnIdentityReusedWhileASessionMissedFramesResetsIt()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, 0, nameof(AnIdentityReusedWhileASessionMissedFramesResetsIt));
        harness.DrainNetIds = true;
        var session = harness.OpenSessions(1, "god")[0];
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(195.3, 195.7, 290, 4, Math.PI / 4), 2), Is.True);
        Run(harness, session, 4);
        var netIds = NetIdsByEntity(harness, ids);
        Assert.That(Held(harness, session), Has.Count.EqualTo(CreatureCount));

        // The session stops draining: its frames fill its send slots, and from then on every frame is skipped until it drains again.
        // A held entity kept moving gives every frame something to say: an idle frame takes no slot, and a static scene would never fill them.
        var mover = ids[CreatureCount - 1];
        var skipped = harness.Assembler.FramesSkipped;
        for (var i = 0; i < 8 && harness.Assembler.FramesSkipped == skipped; i++)
        {
            Move(dbe, mover, 390f, 380f - (i * 2f));
            harness.RunTick(++_tick);
        }

        Assert.That(harness.Assembler.FramesSkipped, Is.GreaterThan(skipped), "the session's slots filled and its frames are being skipped");

        // The free list emptied first — it holds the identities the leases gave back after the fill — so the destroyed entities' are the only ones on it.
        var allocator = harness.Replication.NetIds;
        var mark = allocator.HighWaterMark;
        while (allocator.HighWaterMark == mark)
        {
            allocator.Allocate();
        }

        // Three hundred held entities go; their identities pass the one-tick quarantine onto the free list.
        var victims = new HashSet<uint>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            // Every fifth point, so no cluster empties: a cluster that does releases its block, and its identities come back a tick later, through the
            // orphan path (ArchetypeReplicationState.ReleaseOrphaned) — after the lease refill this test counts on.
            for (var i = 0; i < 1500; i += 5)
            {
                tx.Destroy(ids[i]);
                victims.Add(netIds[ids[i]]);
            }

            tx.Commit();
        }

        harness.RunTick(++_tick);
        harness.RunTick(++_tick);

        // A hundred new ones in the region: past the lease they hold, they are starved a tick, and the refill hands them the freed identities.
        var replacements = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ai = new ProjAi { Template = 77 };
            for (var i = 0; i < 100; i++)
            {
                replacements.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(195f + (i % 10), 195f + (i / 10))), ProjCreature.Ai.Set(in ai)));
            }

            tx.Commit();
        }

        harness.RunTick(++_tick);
        harness.RunTick(++_tick);
        var reused = new List<uint>();
        foreach (var id in NetIdsByEntity(harness, replacements).Where(p => replacements.Contains(p.Key)).Select(p => p.Value))
        {
            if (victims.Contains(id))
            {
                reused.Add(id);
            }
        }

        Assert.That(reused, Is.Not.Empty, "a new entity was given a destroyed one's identity inside the session's missed ticks");

        var push = harness.Subscriptions.Push;
        var ambiguous = push.LogAmbiguous;
        var resets = push.Resets;
        Run(harness, session, 3);
        var plan = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var held = Held(harness, session);
        Assert.Multiple(() =>
        {
            Assert.That(push.LogAmbiguous, Is.GreaterThan(ambiguous), "the catch-up saw the reuse");
            Assert.That(push.Resets, Is.GreaterThan(resets), "and reset instead");
            Assert.That(push.ShadowIllegal, Is.Zero, "no frame carried a leave and an enter of one identity");
            Assert.That(held, Has.Count.EqualTo(CreatureCount - 300 + 100), "the client holds exactly the region's entities");
            foreach (var id in reused)
            {
                Assert.That(held, Does.Contain(id), "a reused identity is held");
                Assert.That(harness.Replica(session).Value(plan, id, "template"), Is.EqualTo(77d), "with the new entity's fields, not the old one's");
            }
        });
    }

    private static void Move(DatabaseEngine dbe, EntityId entity, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (cluster.GetEntityId(slot) == entity)
                {
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(x, y));
                }
            }
        }

        accessor.Dispose();
        tx.Commit();
    }

    /// <summary>Each entity's network identity, read from the replication blocks as the oracle's server truth is.</summary>
    private static Dictionary<EntityId, uint> NetIdsByEntity(FrameHarness harness, List<EntityId> ids)
    {
        var plan = harness.PlanIndex(nameof(ProjCreature));
        var state = harness.Subscriptions.ReplicationStates[plan];
        var layout = state.Layout;
        var byEntity = new Dictionary<EntityId, uint>();
        foreach (var (chunkId, occupancy) in harness.Replication.LiveClusters(plan))
        {
            if (!state.Directory.TryGetBlock(chunkId, out var block))
            {
                continue;
            }

            var live = occupancy;
            while (live != 0)
            {
                var slot = BitOperations.TrailingZeroCount(live);
                live &= live - 1;
                var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
                byEntity[hot->Entity] = hot->NetId;
            }
        }

        foreach (var id in ids)
        {
            byEntity.TryAdd(id, uint.MaxValue);
        }

        return byEntity;
    }
}
