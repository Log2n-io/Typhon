using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// SUB-26: a command's entity reference resolves only to an entity the session holds — its controlled entity, or one inside the geometry its client was
/// last told about (design/Subscriptions/11 § 3). The oracle is the client itself: for every entity, <c>TryResolve</c> must agree with the replica the
/// session's frames built, whatever the observer's shape.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class TryResolveTests : TestBase<TryResolveTests>
{
    private const int Columns = 40;

    private const float Spacing = 10f;

    private const double CellM = 40;

    private long _tick;

    private static ProjBounds PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static List<EntityId> Populate(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(Columns * Columns);
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < Columns * Columns; i++)
        {
            ids.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing))));
        }

        tx.Commit();
        return ids;
    }

    private FrameHarness Harness(DatabaseEngine dbe, string name, int enterBudget = 4096)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            ProjectionTestSchema.DeclareRock(subs);
            subs.Profile("near", p => p.Sphere(75).Of<ProjCreature>());
            subs.Profile("world", p => p.World().Of<ProjCreature>());
            subs.Profile("region", p => p.ClientRegion(1000).Of<ProjCreature>());
            subs.Profile("rocks", p => p.World().Of<ProjRock>());
        }, name, new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            MaxSessions = 8,
            EnterBudgetPerFrame = enterBudget,
            ReplicationCellM = CellM,
        });
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

    private static HashSet<uint> Held(FrameHarness harness, SessionId session) =>
        [.. harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx)];

    // Every entity TryResolve accepts for the session, as netIds.
    private static HashSet<uint> Resolvable(FrameHarness harness, SessionId session, List<EntityId> ids)
    {
        var commands = harness.Subscriptions.Commands;
        var accepted = new HashSet<uint>();
        foreach (var id in ids)
        {
            var netId = harness.NetIdOf(id);
            if (netId != 0 && commands.TryResolve(session, netId, out var resolved))
            {
                Assert.That(resolved, Is.EqualTo(id), "a resolved reference names the entity the netId belongs to");
                accepted.Add(netId);
            }
        }

        return accepted;
    }

    private static RegionVertex[] Polygon(double cx, double cy, double radius, int sides)
    {
        var vertices = new RegionVertex[sides];
        for (var v = 0; v < sides; v++)
        {
            var a = 0.3 + (v * Math.PI * 2.0 / sides);
            vertices[v] = new RegionVertex { X = cx + (Math.Cos(a) * radius), Y = cy + (Math.Sin(a) * radius) };
        }

        return vertices;
    }

    /// <summary>For each observer shape, TryResolve accepts exactly the entities the client holds — no more, no fewer.</summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [VerifiesRule("SUB-26")]
    public void TryResolveAcceptsExactlyWhatTheClientHolds(int shape)
    {
        var profile = new[] { "near", "world", "region" }[shape];
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, nameof(TryResolveAcceptsExactlyWhatTheClientHolds) + profile);
        var session = harness.OpenSessions(1, profile)[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(201.3, 187.7, 0));
        if (profile == "region")
        {
            Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session, Polygon(201.3, 187.7, 90.1, 6), 2), Is.True);
        }

        Run(harness, session, 4);
        var held = Held(harness, session);

        Assert.Multiple(() =>
        {
            Assert.That(held, Is.Not.Empty, "the fixture must hold something");
            if (profile != "world")
            {
                Assert.That(held, Has.Count.LessThan(ids.Count), "and not everything, or 'held' is not being tested");
            }

            Assert.That(Resolvable(harness, session, ids), Is.EquivalentTo(held));
        });
    }

    /// <summary>
    /// The geometry judged moves with the frames: after the view moves and the client applies the frames that describe it, what resolves is the new view
    /// — the old one's entities no longer do.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-26")]
    public void WhatResolvesFollowsTheViewTheClientWasSent()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, nameof(WhatResolvesFollowsTheViewTheClientWasSent));
        var session = harness.OpenSessions(1, "near")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(100.3, 100.7, 0));
        Run(harness, session, 4);
        var before = Held(harness, session);

        harness.Sessions.SetViewpoint(session, new Vector3D(300.3, 300.7, 0));
        Run(harness, session, 4);
        var after = Held(harness, session);
        var resolvable = Resolvable(harness, session, ids);

        Assert.Multiple(() =>
        {
            Assert.That(before.Intersect(after), Is.Empty, "the two views are disjoint");
            Assert.That(resolvable, Is.EquivalentTo(after));
        });
    }

    /// <summary>
    /// A World session whose fill is still under way — a small enter budget — resolves exactly what its cursor has passed, not the whole world; and an entity
    /// of an archetype its profile does not observe (a rock another profile replicates) never resolves, even inside its view.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-26")]
    public void APartialWorldFillAndAnUnobservedArchetypeAreRefused()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        var rocks = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20; i++)
            {
                rocks.Add(tx.Spawn<ProjRock>(ProjRock.Bounds.Set(PointAt(100 + i, 100))));
            }

            tx.Commit();
        }

        using var harness = Harness(dbe, nameof(APartialWorldFillAndAnUnobservedArchetypeAreRefused), enterBudget: 60);

        // Settled first: an entity with an event this tick enters through the push step, outside the budget, and every spawn has one.
        for (var i = 0; i < 3; i++)
        {
            harness.RunTick(++_tick);
        }

        var sessions = harness.OpenSessions(2, "world");
        harness.Sessions.SetProfile(sessions[1], "rocks");
        Run(harness, sessions[0], 2);
        harness.Drain(sessions[1]);
        var held = Held(harness, sessions[0]);
        var commands = harness.Subscriptions.Commands;

        Assert.Multiple(() =>
        {
            Assert.That(held, Has.Count.GreaterThan(0).And.Count.LessThan(ids.Count), "the fill is partial");
            Assert.That(Resolvable(harness, sessions[0], ids), Is.EquivalentTo(held));
            foreach (var rock in rocks)
            {
                var netId = harness.NetIdOf(rock);
                Assert.That(netId, Is.Not.Zero, "the rock is replicated, by the other profile");
                Assert.That(commands.TryResolve(sessions[0], netId, out _), Is.False, "but not an archetype this session observes");
            }
        });
    }

    /// <summary>
    /// A profile switch is judged by the profile the last published frame was built against: until the switch's RESET frame is published, what resolves is
    /// still the old view — a World profile's entities do not all resolve the moment the application asks for it.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-26")]
    public void AProfileSwitchResolvesTheOldViewUntilItsFrameIsPublished()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, nameof(AProfileSwitchResolvesTheOldViewUntilItsFrameIsPublished));
        var session = harness.OpenSessions(1, "near")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(100.3, 100.7, 0));
        Run(harness, session, 4);
        var near = Held(harness, session);

        Assert.That(harness.Sessions.SetProfile(session, "world"), Is.True);
        var beforeFrame = Resolvable(harness, session, ids);
        Run(harness, session, 4);
        var afterFrame = Resolvable(harness, session, ids);

        Assert.Multiple(() =>
        {
            Assert.That(beforeFrame, Is.EquivalentTo(near), "the switch is not what the client holds yet");
            Assert.That(afterFrame, Is.EquivalentTo(Held(harness, session)), "and once published, the new view is");
            Assert.That(afterFrame, Has.Count.GreaterThan(near.Count));
        });
    }

    /// <summary>A destroyed entity is refused at once — by TryResolve and TryResolveAny — not only after the next projection unbinds its identity.</summary>
    [Test]
    [VerifiesRule("SUB-26")]
    public void ADestroyedEntityIsRefusedBeforeItsIdentityIsReleased()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, nameof(ADestroyedEntityIsRefusedBeforeItsIdentityIsReleased));
        var session = harness.OpenSessions(1, "near")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(100.3, 100.7, 0));
        var victim = ids[(10 * Columns) + 10];
        harness.Sessions.SetControlled(session, ids[0]);
        Run(harness, session, 4);
        var victimNetId = harness.NetIdOf(victim);
        var controlledNetId = harness.NetIdOf(ids[0]);
        var commands = harness.Subscriptions.Commands;
        Assert.That(commands.TryResolve(session, victimNetId, out _), Is.True, "the fixture's victim is in view");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(victim);
            tx.Destroy(ids[0]);
            tx.Commit();
        }

        Assert.Multiple(() =>
        {
            Assert.That(commands.TryResolve(session, victimNetId, out _), Is.False, "destroyed, still bound: refused");
            Assert.That(commands.TryResolveAny(session, victimNetId, out _), Is.False, "by TryResolveAny too");
            Assert.That(commands.TryResolve(session, controlledNetId, out _), Is.False, "the controlled entity as well");
        });
    }

    /// <summary>
    /// The controlled entity resolves wherever it is; an entity that is not live, or that the session is not shown, does not — while TryResolveAny, for
    /// tools that are not clients, resolves every live one.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-26")]
    public void TheControlledEntityResolvesAnywhereAndTryResolveAnyIgnoresTheView()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ids = Populate(dbe);
        using var harness = Harness(dbe, nameof(TheControlledEntityResolvesAnywhereAndTryResolveAnyIgnoresTheView));
        var session = harness.OpenSessions(1, "near")[0];
        harness.Sessions.SetViewpoint(session, new Vector3D(10.3, 10.7, 0));
        var far = ids[^1];
        harness.Sessions.SetControlled(session, far);
        Run(harness, session, 4);
        var commands = harness.Subscriptions.Commands;
        var farNetId = harness.NetIdOf(far);
        var other = ids[^2];
        var otherNetId = harness.NetIdOf(other);

        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Does.Not.Contain(farNetId), "the controlled entity is outside the disc");
            Assert.That(commands.TryResolve(session, farNetId, out var controlled), Is.True, "but it resolves: it is the session's own");
            Assert.That(controlled, Is.EqualTo(far));
            Assert.That(commands.TryResolve(session, otherNetId, out _), Is.False, "a far entity that is not the session's own does not");
            Assert.That(commands.TryResolveAny(session, otherNetId, out var any), Is.True, "TryResolveAny ignores the view");
            Assert.That(any, Is.EqualTo(other));
            Assert.That(commands.TryResolve(session, 0x7FFF_FFFF, out _), Is.False, "an identity nothing holds");
        });
    }
}
