using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// <c>DEBUG</c> (09 § 15): a session granted the cap is shown the replication grid with its first frame and every RESET, and its push geometry whenever it
/// changes; a session without the cap is shown nothing. Each expectation is derived from the fixture's arithmetic, not read back from the engine.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class DebugBlockTests : TestBase<DebugBlockTests>
{
    private const int Columns = 40;

    private const float Spacing = 10f;

    private const double Radius = 45d;

    private const double RegionCellM = 40;

    private long _tick;

    private static ProjBounds PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void Populate(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Columns * Columns; i++)
        {
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
        }

        tx.Commit();
    }

    private static int Cell(double v, double cellM) => (int)Math.Floor((v + ProjectionTestSchema.WorldExtentM) / cellM);

    private static void Grant(FrameHarness harness, SessionId session) => harness.Assembler.SendStateOf(session.Slot)->NoteCapsGranted(Capabilities.Debug);

    // Runs ticks, reading every frame of each session; returns each session's frames in order.
    private List<FrameLog>[] Run(FrameHarness harness, int ticks, params SessionId[] sessions)
    {
        var logs = sessions.Select(_ => new List<FrameLog>()).ToArray();
        for (var t = 0; t < ticks; t++)
        {
            harness.RunTick(++_tick);
            for (var s = 0; s < sessions.Length; s++)
            {
                while (harness.Read(sessions[s]) is { } log)
                {
                    logs[s].Add(log);
                }
            }
        }

        return logs;
    }

    private static PushGeometry LastGeometry(List<FrameLog> frames) =>
        frames.SelectMany(f => f.Debugs).Where(d => d.SubType == DebugSubTypes.PushGeometry).Select(d => PushGeometry.Read(d.Payload)).LastOrDefault();

    /// <summary>
    /// A debugging Sphere session is shown the grid once and its geometry — anchor, R′, h, level, and a window holding the cell of every point within R′ —
    /// then a new geometry when its viewpoint moves; an undebugged session beside it is shown nothing.
    /// </summary>
    [Test]
    public void ADebuggingSphereSessionIsShownTheGridAndItsGeometry()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        var cellM = ProjectionTestSchema.ReplicationCellFor(Radius);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
        }, nameof(ADebuggingSphereSessionIsShownTheGridAndItsGeometry), replicationCellM: cellM);
        harness.RunFence = true;
        _tick = 0;
        var sessions = harness.OpenSessions(2, "near");
        Grant(harness, sessions[0]);
        var centre = new Vector3D(200d, 200d, 0d);
        harness.Sessions.SetViewpoint(sessions[0], centre);
        harness.Sessions.SetViewpoint(sessions[1], centre);

        var filled = Run(harness, 6, sessions);
        var geometry = LastGeometry(filled[0]);
        var grids = filled[0].SelectMany(f => f.Debugs).Where(d => d.SubType == DebugSubTypes.Grid).Select(d => DebugGrid.Read(d.Payload)).ToArray();
        var quiet = Run(harness, 3, sessions);

        var moved = new Vector3D(300d, 250d, 0d);
        harness.Sessions.SetViewpoint(sessions[0], moved);
        var afterFrames = Run(harness, 6, sessions)[0];
        var first = afterFrames.SelectMany(f => f.Debugs).Where(d => d.SubType == DebugSubTypes.PushGeometry).Select(d => PushGeometry.Read(d.Payload)).First();
        var after = LastGeometry(afterFrames);

        Assert.Multiple(() =>
        {
            Assert.That(grids, Has.Length.EqualTo(1), "the grid once, with the first frame");
            Assert.That(grids[0], Is.EqualTo(new DebugGrid(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM, 0, cellM,
                (int)Math.Ceiling(2 * ProjectionTestSchema.WorldExtentM / cellM), (int)Math.Ceiling(2 * ProjectionTestSchema.WorldExtentM / cellM), 1)));
            Assert.That(filled[0][0].Debugs.Select(d => d.SubType), Is.EqualTo(new[] { DebugSubTypes.Grid, DebugSubTypes.PushGeometry }),
                "the first frame carries the grid, then the geometry");

            Assert.That(geometry.Shape, Is.EqualTo(PushShape.Sphere));
            Assert.That((geometry.AnchorX, geometry.AnchorY), Is.EqualTo((centre.X, centre.Y)));
            Assert.That((geometry.RadiusM, geometry.SlackM, geometry.Level), Is.EqualTo((Radius, (double)(float)(Radius / 48d), 0)));
            Assert.That(geometry.Flags & PushGeometryFlags.ViewComplete, Is.EqualTo(PushGeometryFlags.ViewComplete));
            for (var i = 0; i < Columns * Columns; i++)
            {
                var (x, y) = (i % Columns * Spacing, i / Columns * Spacing);
                if (((x - centre.X) * (x - centre.X)) + ((y - centre.Y) * (y - centre.Y)) <= Radius * Radius)
                {
                    Assert.That(geometry.Delivered(Cell(x, cellM), Cell(y, cellM), 0), Is.True, $"the cell of ({x}, {y}), within R′, is not shown delivered");
                }
            }

            Assert.That(quiet[0].SelectMany(f => f.Debugs), Is.Empty, "an unchanged geometry is not sent again");
            Assert.That((first.AnchorX, first.AnchorY), Is.EqualTo((moved.X, moved.Y)), "the frame that moves the anchor shows it: the geometry is the one it commits");
            Assert.That((after.AnchorX, after.AnchorY), Is.EqualTo((moved.X, moved.Y)), "a moved viewpoint is shown");
            Assert.That(after.Delivered(Cell(moved.X, cellM), Cell(moved.Y, cellM), 0), Is.True);
            Assert.That(filled[1].Concat(quiet[1]).SelectMany(f => f.Debugs), Is.Empty, "a session without the cap is shown nothing");
        });
    }

    /// <summary>
    /// A debugging ClientRegion session is shown the vertices of the hull it sent and a window that delivers exactly the cells the engine's own window does.
    /// </summary>
    [Test]
    public void ADebuggingRegionSessionIsShownItsHullAndItsWindow()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p =>
            {
                var region = p.ClientRegion(1000);
                region.Near(200);
                region.Of<ProjCreature>();
            });
        }, nameof(ADebuggingRegionSessionIsShownItsHullAndItsWindow), new SubscriptionsOptions
        {
            MaxSessions = 4, EnterBudgetPerFrame = 4096, ReplicationCellM = RegionCellM,
        });
        harness.RunFence = true;
        _tick = 0;
        var session = harness.OpenSessions(1, "god")[0];
        Grant(harness, session);
        RegionVertex[] polygon =
        [
            new() { X = 100, Y = 100 }, new() { X = 300, Y = 120 }, new() { X = 280, Y = 310 }, new() { X = 90, Y = 260 },
        ];
        Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, polygon, 2), Is.True);
        var frames = Run(harness, 8, session)[0];
        var geometry = LastGeometry(frames);
        var push = harness.Subscriptions.Push;

        Assert.Multiple(() =>
        {
            Assert.That(geometry.Shape, Is.EqualTo(PushShape.Region));
            Assert.That(geometry.Dims, Is.EqualTo(2));
            var hull = Enumerable.Range(0, geometry.Vertices.Length / 2).Select(i => (geometry.Vertices[2 * i], geometry.Vertices[(2 * i) + 1]));
            Assert.That(hull, Is.EquivalentTo(polygon.Select(v => (v.X, v.Y))), "the hull's vertices as the client sent them, in the engine's order");
            Assert.That(geometry.NearBudget, Is.EqualTo(200));
            Assert.That(geometry.Held, Is.EqualTo(push.RegionHeldOf(session)));
            Assert.That(geometry.Window, Is.EqualTo(push.RegionWindow));
            var delivered = 0;
            for (var cy = Cell(0, RegionCellM); cy <= Cell(Columns * Spacing, RegionCellM); cy++)
            {
                for (var cx = Cell(0, RegionCellM); cx <= Cell(Columns * Spacing, RegionCellM); cx++)
                {
                    var x = (cx * RegionCellM) - ProjectionTestSchema.WorldExtentM + (RegionCellM / 2);
                    var y = (cy * RegionCellM) - ProjectionTestSchema.WorldExtentM + (RegionCellM / 2);
                    var shown = geometry.Delivered(cx, cy, 0);
                    delivered += shown ? 1 : 0;
                    Assert.That(shown, Is.EqualTo(push.RegionDelivers(session, x, y, 0)), $"cell ({cx}, {cy})");
                }
            }

            Assert.That(delivered, Is.EqualTo(push.RegionDeliveredCells(session)).And.GreaterThan(0));
        });
    }
}
