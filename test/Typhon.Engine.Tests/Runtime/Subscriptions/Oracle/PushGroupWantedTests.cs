using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// #1410 — the engine-wide set of keys some session holds, kept by count (16 § 5): each realm's sessions contribute the keys they take and withdraw the ones
/// they let go of, instead of the set being rebuilt from every session's keys each tick. The counts are held to a full recount every tick through walks,
/// teleports, skipped frames, RESETs, key changes and sessions leaving and arriving in reused slots; and a key no session holds any more lets its
/// positionless members' state go.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushGroupWantedTests : TestBase<PushGroupWantedTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;
    private const string NotReleasedMarker = "a shard's block was kept after no session held its town";

    private static SubscriptionsOptions Options() => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 16,
        PushShadow = true,
    };

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, s => GroupTestSchema.Declare(s, Radius, Reach, Margin, shards: true), name, Options());
        harness.RunFence = true;
        harness.DrainNetIds = true;
        return harness;
    }

    private static (float X, float Y) TownAt(int key) => ((float)(600d * Math.Cos(key * Math.PI / 6d)), (float)(600d * Math.Sin(key * Math.PI / 6d)));

    private static List<EntityId> Populate(DatabaseEngine dbe)
    {
        var shards = new List<EntityId>();
        using var tx = dbe.CreateQuickTransaction();
        for (var key = 1; key <= 12; key++)
        {
            var (x, y) = TownAt(key);
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = key, Extent = GroupTestSchema.Square(x, y, 40f) }));
            for (var i = 0; i < 3; i++)
            {
                shards.Add(tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)((key * 3) + i) }),
                    GroupPositionless.Key.Set(new GroupKey { Town = key })));
            }
        }

        tx.Commit();
        return shards;
    }

    /// <summary>
    /// Two hundred ticks of six sessions walking between twelve towns, teleporting near one or 5 km away (a RESET), skipping three frames in ten, shards
    /// changing town, and a session leaving and another taking a slot every 25 ticks: after every blocks step, each key's count equals a recount.
    /// </summary>
    [Test]
    public void TheWantedCountsEqualARecountEveryTick() => AssertTheCountsHold(keepReleased: false);

    /// <summary>The differential check catches a session that lets go of a key and keeps counting it.</summary>
    [Test]
    public void TheDifferentialCheckCatchesAKeyCountedPastItsRelease() => AssertTheCountsHold(keepReleased: true);

    private void AssertTheCountsHold(bool keepReleased)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var shards = Populate(dbe);
        var name = keepReleased ? nameof(TheDifferentialCheckCatchesAKeyCountedPastItsRelease) : nameof(TheWantedCountsEqualARecountEveryTick);
        using var harness = Harness(dbe, name);
        var hub = harness.Subscriptions.Hub;
        hub.VerifyWantedForTest = true;
        hub.KeepWithdrawnKeysForTest = keepReleased;
        var random = new Random(1410);
        var sessions = harness.OpenSessions(6, GroupTestSchema.Profile).ToList();
        var at = new Vector3D[sessions.Count];
        for (var i = 0; i < sessions.Count; i++)
        {
            var (x, y) = TownAt(1 + random.Next(12));
            at[i] = new Vector3D(x, y, 0d);
            harness.Sessions.SetViewpoint(sessions[i], at[i]);
        }

        var townSet = harness.CatalogPlan.GroupSetIndex(GroupTestSchema.Set);
        var tick = 0L;
        var maxWanted = 0;
        for (var t = 0; t < 200; t++)
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                var roll = random.Next(100);
                if (roll < 5)
                {
                    at[i] = new Vector3D(5000d, 5000d, 0d);
                }
                else if (roll < 20)
                {
                    var (x, y) = TownAt(1 + random.Next(12));
                    at[i] = new Vector3D(x, y, 0d);
                }
                else
                {
                    at[i] = new Vector3D(at[i].X + (random.NextDouble() * 60d) - 30d, at[i].Y + (random.NextDouble() * 60d) - 30d, 0d);
                }

                harness.Sessions.SetViewpoint(sessions[i], at[i]);
            }

            if (t % 7 == 3)
            {
                using var tx = dbe.CreateQuickTransaction();
                tx.OpenMut(shards[random.Next(shards.Count)]).Set(GroupPositionless.Key, new GroupKey { Town = 1 + random.Next(12) });
                tx.Commit();
            }

            if (t % 25 == 12)
            {
                var leaving = random.Next(sessions.Count);
                Assert.That(harness.Sessions.Close(sessions[leaving], SessionCloseReason.Kicked, 4100), Is.True);
                sessions[leaving] = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
                harness.Sessions.SetViewpoint(sessions[leaving], at[leaving]);
            }

            harness.RunTick(++tick);
            foreach (var session in sessions)
            {
                if (random.Next(10) >= 3)
                {
                    harness.Deliver(session);
                }
            }

            maxWanted = Math.Max(maxWanted, hub.WantedForTest(townSet).Length);
            if (!keepReleased)
            {
                Assert.That(hub.WantedMismatches, Is.Zero, $"the wanted counts differ from a recount at tick {tick}");
            }
        }

        if (keepReleased)
        {
            Assert.That(hub.WantedMismatches, Is.GreaterThan(0), "the differential check accepted a key counted past its release");
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(maxWanted, Is.GreaterThan(1), "sessions held several towns at once");
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// A town held, then let go by its only session: once no key needs them for <c>LazyIdleTicks</c>, its shards' blocks are released — which happens only
    /// if the session's withdrawal of the key reaches the count.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-33")]
    public void AShardsStateGoesOnceNoSessionHoldsItsTown() => AssertTheShardsAreReleased(keepReleased: false);

    /// <summary>The same with the withdrawal lost: the key stays wanted, the blocks are never released, and the test above must say so.</summary>
    [Test]
    [RuleMutant("SUB-33")]
    public void AKeyCountedPastItsReleaseIsDetected() =>
        RuleMutants.AssertDetects("SUB-33", NotReleasedMarker, () => AssertTheShardsAreReleased(keepReleased: true));

    private void AssertTheShardsAreReleased(bool keepReleased)
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = Harness(dbe, keepReleased ? nameof(AKeyCountedPastItsReleaseIsDetected) : nameof(AShardsStateGoesOnceNoSessionHoldsItsTown));
        harness.Subscriptions.Hub.KeepWithdrawnKeysForTest = keepReleased;
        var plan = harness.PlanIndex(nameof(GroupPositionless));
        var session = harness.OpenSessions(1, GroupTestSchema.Profile)[0];
        var (x, y) = TownAt(3);
        harness.Sessions.SetViewpoint(session, new Vector3D(x, y, 0d));
        var tick = 0L;
        for (var t = 0; t < 4; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.That(harness.Subscriptions.ReplicationStates[plan].Directory.Count, Is.GreaterThan(0), "the held town's shards have state");

        // Walked out of the ring, away from every town, by steps under a cell: the town is let go of by records, not by a RESET.
        var at = new Vector3D(x, y, 0d);
        while (at.Y < y + 400d)
        {
            at = new Vector3D(at.X, at.Y + 20d, 0d);
            harness.Sessions.SetViewpoint(session, at);
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        for (var t = 0; t < (int)Engine.Internals.PushHub.LazyIdleTicks + 16; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        Assert.That(harness.Subscriptions.ReplicationStates[plan].Directory.Count, Is.Zero, NotReleasedMarker);
    }
}
