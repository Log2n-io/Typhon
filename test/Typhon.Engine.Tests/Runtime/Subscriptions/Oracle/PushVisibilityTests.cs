using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// Visibility tags (14 § 6, step 4.4), on the paths the two tagged oracles do not reach: a mask flip told as a leave and an enter, a session mask change
/// as a RESET, a hidden shard's orphan, an event routed to the sessions that know an entity, and a command resolving one. Two sessions hold one town: A
/// sees bit 1, B bit 2.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushVisibilityTests : TestBase<PushVisibilityTests>
{
    private const double Radius = 120d;
    private const double Reach = 80d;
    private const double Margin = 20d;
    private const int TownKey = 7;
    private const uint SeenByA = 1u;
    private const uint SeenByB = 2u;

    private static SubscriptionsOptions Options() => new()
    {
        IngressBytesPerSecond = TestIngress.Budget,
        MaxSessions = 16,
        ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        MaxGroupsPerSession = 8,
        EnterBudgetPerFrame = 500,
        PushShadow = true,
    };

    private static void Declare(SubscriptionsRegistry subs)
    {
        GroupTestSchema.Declare(subs, Radius, Reach, Margin, shards: true, tags: true);
        subs.Event<ProjDuel>(e => e.RouteToKnown(d => d.A, d => d.B));
    }

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, Declare, name, Options());
        harness.RunFence = true;
        harness.DrainNetIds = true;
        return harness;
    }

    private static void Town(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = TownKey, Extent = GroupTestSchema.Square(0f, 0f, 60f) }));
        tx.Commit();
    }

    private static EntityId Building(DatabaseEngine dbe, float x, uint mask)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(x, 0f)), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
            GroupBuilding.Key.Set(new GroupKey { Town = TownKey }), GroupBuilding.Tag.Set(new GroupTag { Mask = mask }));
        tx.Commit();
        return id;
    }

    private static EntityId Shard(DatabaseEngine dbe, uint mask)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = 1 }), GroupPositionless.Key.Set(new GroupKey { Town = TownKey }),
            GroupPositionless.Tag.Set(new GroupTag { Mask = mask }));
        tx.Commit();
        return id;
    }

    // The profile's detection is explicit: a change is told by Replicate.
    private static void SetMask(FrameHarness harness, EntityId entity, Comp<GroupTag> tag, uint mask)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var e = tx.OpenMut(entity);
        e.Set(tag, new GroupTag { Mask = mask });
        harness.Subscriptions.Commands.Replicate(in e);
        tx.Commit();
    }

    private static void Destroy(DatabaseEngine dbe, EntityId entity)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(entity);
        tx.Commit();
    }

    /// <summary>Opens A and B on the town, each with its mask, and runs until both hold it.</summary>
    private static SessionId[] Open(FrameHarness harness, ref long tick)
    {
        var sessions = harness.OpenSessions(2, GroupTestSchema.Profile);
        Assert.That(harness.Sessions.SetVisibility(sessions[0], SeenByA), Is.True);
        Assert.That(harness.Sessions.SetVisibility(sessions[1], SeenByB), Is.True);
        foreach (var session in sessions)
        {
            Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        }

        Run(harness, ref tick, 4, sessions);
        foreach (var session in sessions)
        {
            Assert.That(harness.Replica(session).Store.IsGroupComplete((uint)harness.CatalogPlan.GroupSetIndex(GroupTestSchema.Set), TownKey), Is.True);
        }

        return sessions;
    }

    /// <summary>Runs <paramref name="ticks"/> ticks, every session's frames delivered and logged; the logs, per session.</summary>
    private static List<FrameLog>[] Run(FrameHarness harness, ref long tick, int ticks, params SessionId[] sessions)
    {
        var logs = sessions.Select(_ => new List<FrameLog>()).ToArray();
        for (var t = 0; t < ticks; t++)
        {
            tick++;
            harness.RunTick(tick);
            for (var i = 0; i < sessions.Length; i++)
            {
                logs[i].AddRange(harness.DeliverLogged(sessions[i]));
            }
        }

        return logs;
    }

    private static bool Holds(FrameHarness harness, SessionId session, string archetype, uint netId) =>
        harness.Replica(session).NetIds(harness.PlanIndex(archetype)).Contains(netId);

    private static void Clean(FrameHarness harness, SessionId[] sessions) => Assert.Multiple(() =>
    {
        foreach (var session in sessions)
        {
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero, "no record names an entity the session does not hold");
        }

        Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
    });

    /// <summary>
    /// A mask flip is a leave for the session that stops seeing the entity and an enter for the one that starts — and nothing for a session that sees it
    /// both before and after.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-34")]
    public void AMaskFlipIsALeaveForOneSessionAndAnEnterForTheOther()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe);
        var flipped = Building(dbe, -20f, SeenByA);
        var shared = Building(dbe, 20f, SeenByA | SeenByB);
        using var harness = Harness(dbe, nameof(AMaskFlipIsALeaveForOneSessionAndAnEnterForTheOther));
        var tick = 0L;
        var sessions = Open(harness, ref tick);
        var flippedId = harness.NetIdOf(flipped);
        var sharedId = harness.NetIdOf(shared);
        Assert.Multiple(() =>
        {
            Assert.That(Holds(harness, sessions[0], nameof(GroupBuilding), flippedId), Is.True);
            Assert.That(Holds(harness, sessions[1], nameof(GroupBuilding), flippedId), Is.False, "B does not see bit 1");
            Assert.That(Holds(harness, sessions[1], nameof(GroupBuilding), sharedId), Is.True);
        });

        SetMask(harness, flipped, GroupBuilding.Tag, SeenByB);
        SetMask(harness, shared, GroupBuilding.Tag, SeenByB | 4u);
        var logs = Run(harness, ref tick, 3, sessions);

        Assert.Multiple(() =>
        {
            Assert.That(logs[0].SelectMany(l => l.Leaves), Is.EquivalentTo(new[] { flippedId, sharedId }), "A stops seeing both");
            Assert.That(logs[0].SelectMany(l => l.Enters), Is.Empty);
            Assert.That(logs[1].SelectMany(l => l.Enters), Is.EqualTo(new[] { flippedId }), "B starts seeing the flipped one");
            Assert.That(logs[1].SelectMany(l => l.Leaves), Is.Empty, "B saw the shared one before and after");
            Assert.That(logs.SelectMany(s => s).Any(l => (l.Flags & TickFlags.Reset) != 0), Is.False, "an entity's flip is records, never a reset");
            Assert.That(Holds(harness, sessions[0], nameof(GroupBuilding), flippedId), Is.False);
            Assert.That(Holds(harness, sessions[1], nameof(GroupBuilding), flippedId), Is.True);
        });
        Clean(harness, sessions);
    }

    /// <summary>A session's own mask change is a RESET: its next frame is the fill of what the new mask sees, nothing it saw before kept.</summary>
    [Test]
    [VerifiesRule("SUB-34")]
    public void ASessionMaskChangeResetsItsStore()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe);
        var forA = Building(dbe, -20f, SeenByA);
        var forB = Building(dbe, 20f, SeenByB);
        var open = Building(dbe, 0f, 0u);
        using var harness = Harness(dbe, nameof(ASessionMaskChangeResetsItsStore));
        var tick = 0L;
        var sessions = Open(harness, ref tick);
        var resetsBefore = harness.Subscriptions.Push.Resets;

        Assert.That(harness.Sessions.SetVisibility(sessions[0], SeenByB), Is.True);
        var logs = Run(harness, ref tick, 4, sessions);

        var buildings = harness.PlanIndex(nameof(GroupBuilding));
        Assert.Multiple(() =>
        {
            Assert.That(logs[0], Is.Not.Empty);
            Assert.That(logs[0][0].Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset), "the change's first frame resets the store");
            Assert.That(logs[1].Any(l => (l.Flags & TickFlags.Reset) != 0), Is.False, "the other session is not touched");
            Assert.That(harness.Subscriptions.Push.Resets, Is.EqualTo(resetsBefore + 1));
            Assert.That(harness.Replica(sessions[0]).NetIds(buildings), Is.EquivalentTo(new[] { harness.NetIdOf(forB), harness.NetIdOf(open) }));
            Assert.That(harness.Replica(sessions[0]).NetIds(buildings), Does.Not.Contain(harness.NetIdOf(forA)));
            Assert.That(harness.Replica(sessions[0]).Store.IsGroupComplete((uint)harness.CatalogPlan.GroupSetIndex(GroupTestSchema.Set), TownKey), Is.True);
        });

        // Setting the mask it already has is not a change.
        Assert.That(harness.Sessions.SetVisibility(sessions[0], SeenByB), Is.True);
        logs = Run(harness, ref tick, 2, sessions);
        Assert.That(logs[0].Any(l => (l.Flags & TickFlags.Reset) != 0), Is.False);
        Clean(harness, sessions);
    }

    /// <summary>
    /// A hidden shard destroyed alone in its cluster goes by the hub's orphan path (14 § 5): the session that sees it is told it left, the one that never
    /// held it is told nothing.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-34")]
    public void AHiddenShardOrphanedIsALeaveOnlyForTheSessionThatSawIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe);
        var shard = Shard(dbe, SeenByA);
        using var harness = Harness(dbe, nameof(AHiddenShardOrphanedIsALeaveOnlyForTheSessionThatSawIt));
        var tick = 0L;
        var sessions = Open(harness, ref tick);
        var netId = harness.NetIdOf(shard);
        Assert.Multiple(() =>
        {
            Assert.That(netId, Is.Not.Zero);
            Assert.That(Holds(harness, sessions[0], nameof(GroupPositionless), netId), Is.True);
            Assert.That(Holds(harness, sessions[1], nameof(GroupPositionless), netId), Is.False);
        });

        Destroy(dbe, shard);
        var logs = Run(harness, ref tick, 3, sessions);

        Assert.Multiple(() =>
        {
            Assert.That(logs[0].SelectMany(l => l.Leaves), Is.EqualTo(new[] { netId }));
            Assert.That(logs[1].SelectMany(l => l.Leaves), Is.Empty, "B never held it");
            Assert.That(harness.Replica(sessions[0]).NetIds(harness.PlanIndex(nameof(GroupPositionless))), Is.Empty);
        });
        Clean(harness, sessions);
    }

    /// <summary>
    /// An event routed to the sessions that know an entity reaches the sessions that see it: a hidden building's is heard by A alone, a public one's by
    /// both.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-34")]
    public void AnEventAboutAHiddenEntityReachesOnlyTheSessionsThatSeeIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe);
        var hidden = Building(dbe, -20f, SeenByA);
        var open = Building(dbe, 20f, 0u);
        using var harness = Harness(dbe, nameof(AnEventAboutAHiddenEntityReachesOnlyTheSessionsThatSeeIt));
        var tick = 0L;
        var sessions = Open(harness, ref tick);

        var commands = harness.Subscriptions.Commands;
        commands.Emit(new ProjDuel { A = hidden, B = hidden, Seq = 1 });
        commands.Emit(new ProjDuel { A = open, B = open, Seq = 2 });
        Run(harness, ref tick, 1, sessions);

        var a = harness.Replica(sessions[0]).Events.Received;
        var b = harness.Replica(sessions[1]).Events.Received;
        Assert.Multiple(() =>
        {
            Assert.That(a.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 1d, 2d }));
            Assert.That(a[0].Fields["A"], Is.EqualTo(harness.NetIdOf(hidden)));
            Assert.That(b.Select(e => e.Fields["Seq"]), Is.EqualTo(new[] { 2d }), "B does not see the hidden building, so it does not know it");
            Assert.That(harness.Subscriptions.Events.Rejected, Is.Zero);
        });
    }

    /// <summary>
    /// A command names what its session holds (SUB-26): a member held through a town — a building, which no observer reaches, or a positionless shard —
    /// both ways, by entity and by netId; of those, only one the session sees; and nothing of a town it does not hold.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-34")]
    [VerifiesRule("SUB-26")]
    public void ACommandNamesAGroupMemberOnlyForTheSessionsThatHoldAndSeeIt()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        Town(dbe);
        var hidden = Building(dbe, -20f, SeenByA);
        var open = Building(dbe, 20f, 0u);
        var shard = Shard(dbe, SeenByA);
        EntityId elsewhere;
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<GroupTown>(GroupTown.Zone.Set(new GroupZone { Key = 9, Extent = GroupTestSchema.Square(3000f, 0f, 60f) }));
            elsewhere = tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(3000f, 0f)), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
                GroupBuilding.Key.Set(new GroupKey { Town = 9 }), GroupBuilding.Tag.Set(new GroupTag { Mask = 0u }));
            tx.Commit();
        }

        using var harness = Harness(dbe, nameof(ACommandNamesAGroupMemberOnlyForTheSessionsThatHoldAndSeeIt));
        var tick = 0L;
        var sessions = Open(harness, ref tick);

        var commands = harness.Subscriptions.Commands;
        var hiddenId = harness.NetIdOf(hidden);
        var shardId = harness.NetIdOf(shard);
        Assert.Multiple(() =>
        {
            Assert.That(Holds(harness, sessions[0], nameof(GroupPositionless), shardId), Is.True);
            Assert.That(commands.TryNetIdOf(sessions[0], hidden, out var seen), Is.True, "a member A holds through its town");
            Assert.That(seen, Is.EqualTo(hiddenId));
            Assert.That(commands.TryNetIdOf(sessions[1], hidden, out var unseen), Is.False, "B does not see it, so was never sent it");
            Assert.That(unseen, Is.Zero);
            Assert.That(commands.TryNetIdOf(sessions[1], open, out var known), Is.True);
            Assert.That(known, Is.EqualTo(harness.NetIdOf(open)));
            Assert.That(commands.TryNetIdOf(sessions[0], shard, out var shardSeen), Is.True, "a positionless member, held through its town");
            Assert.That(shardSeen, Is.EqualTo(shardId));
            Assert.That(commands.TryNetIdOf(sessions[1], shard, out _), Is.False);
            Assert.That(commands.TryNetIdOf(sessions[0], elsewhere, out _), Is.False, "town 9 is not held");

            Assert.That(commands.TryResolve(sessions[0], hiddenId, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(hidden));
            Assert.That(commands.TryResolve(sessions[1], hiddenId, out _), Is.False);
            Assert.That(commands.TryResolve(sessions[0], shardId, out var resolvedShard), Is.True);
            Assert.That(resolvedShard, Is.EqualTo(shard));
            Assert.That(commands.TryResolve(sessions[1], shardId, out _), Is.False);
        });

        // The town released (its sessions gone 3 km away): nothing of it is named any more.
        foreach (var session in sessions)
        {
            Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(3000d, 3000d, 0d)), Is.True);
        }

        Run(harness, ref tick, 4, sessions);
        Assert.Multiple(() =>
        {
            Assert.That(commands.TryNetIdOf(sessions[0], hidden, out _), Is.False);
            Assert.That(commands.TryNetIdOf(sessions[0], shard, out _), Is.False);
            Assert.That(commands.TryNetIdOf(sessions[1], open, out _), Is.False);
        });
    }
}
