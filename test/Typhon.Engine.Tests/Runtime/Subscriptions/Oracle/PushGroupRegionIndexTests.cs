using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// #1409 — a region set's index kept current incrementally (16 § 4): every region holds a stable slot, each cell's bucket lists the slots filed under it in
/// key order, and a region is re-filed only in the cells it gained or lost; a region that dies frees its slot after the walk. These tests hold the
/// incremental index to a from-scratch build tick by tick, and hold a walking region to being met where it is now.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupRegionIndexTests : TestBase<PushGroupRegionIndexTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;
    private const string NotMetMarker = "a walking town was not met where it is now";

    private static SubscriptionsOptions Options() => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 64,
        PushShadow = true,
    };

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, Radius, Reach, Margin), name, Options());
        harness.RunFence = true;
        return harness;
    }

    private static EntityId Town(Transaction tx, int key, float x, float y, float half) =>
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, half) }));

    private static void Move(Transaction tx, EntityId town, float dx, float dy, float grow = 0f)
    {
        var zone = tx.Open(town).Read(GroupTown.Zone);
        var e = zone.Extent;
        zone.Extent = new AABB2F { MinX = e.MinX + dx - grow, MinY = e.MinY + dy - grow, MaxX = e.MaxX + dx + grow, MaxY = e.MaxY + dy + grow };
        tx.OpenMut(town).Set(GroupTown.Zone, zone);
    }

    private static void Step(FrameHarness harness, ref long tick, IReadOnlyList<SessionId> sessions)
    {
        harness.RunTick(++tick);
        foreach (var session in sessions)
        {
            harness.Deliver(session);
        }
    }

    /// <summary>
    /// Sixty-four towns walking at random — some a few metres a tick, under a cell, some across cells — while towns spawn, die, and one grows past the
    /// oversized bound and shrinks back, a second region appears under a held key and goes, a key changes in place and back, and an oversized region dies:
    /// after every tick the incremental index equals a from-scratch build, and only the towns whose cells changed were re-filed.
    /// </summary>
    [Test]
    public void TheIncrementalIndexEqualsAFromScratchBuildEveryTick() => AssertTheIndexStaysExact(staleRefile: false);

    /// <summary>The differential check catches a region left filed where it was after its extent moved.</summary>
    [Test]
    public void TheDifferentialCheckCatchesAStaleRefile() => AssertTheIndexStaysExact(staleRefile: true);

    private void AssertTheIndexStaysExact(bool staleRefile)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var random = new Random(1409);
        var towns = new List<EntityId>();
        var nextKey = 1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                towns.Add(Town(tx, nextKey++, (float)(random.NextDouble() * 2000d - 1000d), (float)(random.NextDouble() * 2000d - 1000d), 15f));
            }

            tx.Commit();
        }

        var name = staleRefile ? nameof(TheDifferentialCheckCatchesAStaleRefile) : nameof(TheIncrementalIndexEqualsAFromScratchBuildEveryTick);
        using var harness = Harness(dbe, name);
        var push = harness.Subscriptions.Push;
        push.VerifyRegionIndexForTest = true;
        push.RefileSkippedForTest = staleRefile;
        var sessions = harness.OpenSessions(4, GroupTestSchema.Profile);
        for (var i = 0; i < sessions.Length; i++)
        {
            harness.Sessions.SetViewpoint(sessions[i], new Vector3D(-600d + (i * 400d), 0d, 0d));
        }

        var tick = 0L;
        Step(harness, ref tick, sessions);
        var refilesAtStart = push.RegionRefiles;
        var twin = default(EntityId);
        var hasTwin = false;
        for (var t = 0; t < 120; t++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                // The cases a random walk does not reach: a second region under a key already held (the first reading keeps the slot), a key changed in
                // place and back, and an oversized region dying.
                if (t % 30 == 5)
                {
                    twin = Town(tx, 1, 500f, 500f, 15f);
                    hasTwin = true;
                }
                else if (t % 30 == 25 && hasTwin)
                {
                    tx.Destroy(twin);
                    hasTwin = false;
                }

                if (t == 60 || t == 90)
                {
                    var zone = tx.Open(towns[2]).Read(GroupTown.Zone);
                    zone.Key = t == 60 ? 9000 : 3;
                    tx.OpenMut(towns[2]).Set(GroupTown.Zone, zone);
                }

                if (t == 75)
                {
                    Move(tx, towns[3], 0f, 0f, 1300f);
                }
                else if (t == 76)
                {
                    tx.Destroy(towns[3]);
                    towns.RemoveAt(3);
                }

                foreach (var town in towns)
                {
                    // Most towns a few metres, under a 40 m cell; one in eight a jump across cells; one in eight not at all.
                    var roll = random.Next(8);
                    var step = roll == 0 ? 0f : roll == 1 ? 90f : 4f;
                    var angle = random.NextDouble() * Math.PI * 2d;
                    Move(tx, town, step * (float)Math.Cos(angle), step * (float)Math.Sin(angle));
                }

                if (t % 30 == 10)
                {
                    towns.Add(Town(tx, nextKey++, 0f, 0f, 15f));
                }
                else if (t % 30 == 20)
                {
                    tx.Destroy(towns[^1]);
                    towns.RemoveAt(towns.Count - 1);
                }
                else if (t == 45 || t == 46)
                {
                    // Past MaxGroupRegionCells and back: the oversized list changes.
                    Move(tx, towns[0], 0f, 0f, t == 45 ? 1300f : -1300f);
                }

                tx.Commit();
            }

            Step(harness, ref tick, sessions);
            if (!staleRefile)
            {
                Assert.That(push.RegionIndexMismatches, Is.Zero, $"the incremental index differs from a from-scratch build at tick {tick}");
            }
        }

        if (staleRefile)
        {
            Assert.That(push.RegionIndexMismatches, Is.GreaterThan(0), "the differential check accepted an index that left moved regions where they were");
            return;
        }

        Assert.Multiple(() =>
        {
            // A 4 m step crosses a padded box's edge about one tick in five; a 90 m jump always does; a still town never.
            Assert.That(push.RegionRefiles - refilesAtStart, Is.InRange(120 * 64 / 8, 120 * 64 / 2), "only the towns whose cells changed were re-filed");
            Assert.That(push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// A town walking 1.2 km past a session, 12 m a tick through thirty cells, while others walk too: the session holds it while it is within reach and lets
    /// it go once it is past the margin — met where it is now, through each re-filing.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AWalkingTownIsMetWhereItIsNow() => AssertTheWalkerIsMet(staleRefile: false);

    /// <summary>The same walk with a moved town left filed where it was: the session never meets it, and the test above must say so.</summary>
    [Test]
    [RuleMutant("SUB-33")]
    public void ARefileThatLosesNewCellsIsDetected() =>
        RuleMutants.AssertDetects("SUB-33", NotMetMarker, () => AssertTheWalkerIsMet(staleRefile: true));

    private void AssertTheWalkerIsMet(bool staleRefile)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        EntityId walker;
        var others = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            walker = Town(tx, 7, -600f, 0f, 10f);
            for (var i = 0; i < 8; i++)
            {
                others.Add(Town(tx, 100 + i, -900f + (i * 250f), 600f, 10f));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var bounds = GroupTestSchema.At(0f, -1500f);
            tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(in bounds), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
                GroupBuilding.Key.Set(new GroupKey { Town = 7 }));
            tx.Commit();
        }

        using var harness = Harness(dbe, staleRefile ? nameof(ARefileThatLosesNewCellsIsDetected) : nameof(AWalkingTownIsMetWhereItIsNow));
        harness.Subscriptions.Push.RefileSkippedForTest = staleRefile;
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d));
        var sessions = new[] { session };

        var tick = 0L;
        var x = -600f;
        var heldWhileNear = 0;
        var nearTicks = 0;
        var heldWhenFar = 0;
        while (x < 600f)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                Move(tx, walker, 12f, 0f);
                foreach (var other in others)
                {
                    Move(tx, other, 0f, 3f);
                }

                tx.Commit();
            }

            x += 12f;
            Step(harness, ref tick, sessions);

            // The extent's nearest point: its centre less its half side. Well inside the reach, and well past reach plus margin.
            var distance = Math.Abs(x) - 10f;
            var state = harness.Subscriptions.Push.GroupKeyStateOf(session, 0, 7);
            if (distance < Reach - 30d)
            {
                nearTicks++;
                heldWhileNear += state != Engine.Internals.GroupKeyState.NotHeld ? 1 : 0;
            }
            else if (distance > Reach + Margin + 30d)
            {
                heldWhenFar += state != Engine.Internals.GroupKeyState.NotHeld ? 1 : 0;
            }
        }

        Assert.That(nearTicks, Is.GreaterThan(5), "the walk passed close by");
        Assert.That(heldWhileNear, Is.EqualTo(nearTicks), NotMetMarker);
        Assert.That(heldWhenFar, Is.Zero, "a town past reach and margin was still held");
    }
}
