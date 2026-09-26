using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms R4.2: replication knows the realm of every block it describes, quantizes positions over that realm's frame, and — until sessions are placed in
/// realms (R4.3) — serves realm 0 only: nothing of another realm is ever projected, identified or sent.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class RealmReplicationTests : TestBase<RealmReplicationTests>
{
    private const string Profile = "world";

    private static SpatialGridConfig Realm0Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), 10);

    // Another realm with other bounds, so a frame taken from the wrong realm quantizes differently.
    private static SpatialGridConfig Realm1Grid() => SpatialGridConfig.Flat(new Vector2(-40, -40), new Vector2(40, 40), 10);

    private static RealmPos At(float x, float y, ushort realm, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Realm = realm, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Realm0Grid());
        dbe.InitializeArchetypes();
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Realm1Grid()));
        return dbe;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Archetype<RealmUnit>(a => a.Motion(RealmUnit.Pos, m => m.Teleport(20)));
        subs.Profile(Profile, p => p.World().Of<RealmUnit>());
    }

    private static FrameHarness CreateHarness(DatabaseEngine dbe)
    {
        var harness = FrameHarness.Create(dbe, Declare, nameof(RealmReplicationTests), replicationCellM: 10);
        harness.RunFence = true;
        return harness;
    }

    private static EntityId[] Spawn(DatabaseEngine dbe, ushort realm, int count)
    {
        var ids = new EntityId[count];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            ids[i] = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5 + (10 * i), 25, realm, i)));
        }

        tx.Commit();
        return ids;
    }

    private static void Run(FrameHarness harness, SessionId session, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            harness.RunTick(harness.Tick + 1);
            harness.Deliver(session);
        }
    }

    private static int Held(FrameHarness harness, SessionId session) =>
        harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(RealmUnit)).Idx).Length;

    [Test]
    [VerifiesRule("SUB-30")]
    public void RealmFramedQuantizationEqualsTheCatalogCodecForRealmZero()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var plans = harness.Subscriptions.Plans;
        var a = harness.PlanIndex(nameof(RealmUnit));
        var catalogPos = plans[a].Position.Pos;
        var realm0 = RealmCodecs.Create(0, in dbe.RealmTable.Get(0).GridConfig, plans, catalogPos.Bits);
        var realm1 = RealmCodecs.Create(1, in dbe.RealmTable.Get(1).GridConfig, plans, catalogPos.Bits);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Subscriptions.Push.Codecs.ByPlan[a].Matches(catalogPos), Is.True, "replication's frame is the catalog codec's");
            Assert.That(realm0.ByPlan[a].Matches(catalogPos), Is.True, "realm 0's grid gives exactly the catalog codec: single-realm bytes are unchanged");
            Assert.That(realm1.ByPlan[a].Matches(catalogPos), Is.False, "another realm's grid is another frame");
            Assert.That(realm1.ByPlan[a].Min, Is.EqualTo(new[] { -40d, -40d }));
            Assert.That(harness.Subscriptions.Push.CodecsFor(1), Is.Null, "a realm replication does not serve has no codecs here");
        });

        // The same world value, quantized over each frame, decodes back within half its own quantum — and the two codes differ.
        var rng = new Random(20260926);
        for (var i = 0; i < 1_000; i++)
        {
            var x = (rng.NextDouble() * 80) - 40;
            var q0 = WireMath.EncodeQuant(x, realm0.ByPlan[a].Min[0], realm0.ByPlan[a].Max[0], realm0.ByPlan[a].Bits);
            var q1 = WireMath.EncodeQuant(x, realm1.ByPlan[a].Min[0], realm1.ByPlan[a].Max[0], realm1.ByPlan[a].Bits);
            Assert.That(WireMath.DecodeQuantWithStep(q1, realm1.ByPlan[a].Min[0], realm1.ByPlan[a].Step[0]), Is.EqualTo(x).Within(realm1.ByPlan[a].Step[0]));
            if (x > 1)
            {
                Assert.That(q1, Is.Not.EqualTo(q0), "a code means a place only with its realm's frame (SUB-30)");
            }
        }
    }

    [Test]
    public void ABlockCarriesItsClustersRealm()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        Spawn(dbe, 0, 3);
        Spawn(dbe, 1, 3);
        dbe.WriteTickFence(1);

        var state = harness.Subscriptions.ReplicationStates[harness.PlanIndex(nameof(RealmUnit))];
        var cs = state.ClusterState;
        var ids = cs.ReadActiveClusterList(out var count);
        var seen = new bool[2];
        for (var i = 0; i < count; i++)
        {
            var chunk = ids[i];
            var realm = cs.ClusterRealmMap[chunk];
            Assert.That(state.TryAttachBlock(chunk, out var block), Is.True);
            Assert.That(block->Realm, Is.EqualTo(realm), $"cluster {chunk} is in realm {realm}");
            seen[realm] = true;
            Assert.That(state.TryReleaseBlock(chunk), Is.True);
        }

        Assert.That(seen, Is.EqualTo(new[] { true, true }), "both realms had a cluster to check");
    }

    [Test]
    public void AnEntityOfARealmNotServedIsNeverKnownToASession()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Profile)[0];

        // Same local coordinates in both realms: geometry cannot hide a leak.
        var inZero = Spawn(dbe, 0, 3);
        var inOne = Spawn(dbe, 1, 3);
        Run(harness, session, 4);

        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.EqualTo(3), "the session holds realm 0's entities, and only them");
            foreach (var id in inZero)
            {
                Assert.That(harness.NetIdOf(id), Is.Not.Zero);
            }

            foreach (var id in inOne)
            {
                Assert.That(harness.NetIdOf(id), Is.Zero, "an entity of an unserved realm is never given an identity");
            }
        });
    }

    [Test]
    public void ATeleportOutOfTheServedRealmIsALeave_AndBackAnEnter()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Profile)[0];
        var ids = Spawn(dbe, 0, 3);
        Run(harness, session, 4);
        Assert.That(Held(harness, session), Is.EqualTo(3));

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[1], RealmUnit.Pos, new RealmId(1), At(0, 0, 1, 1));
            tx.Commit();
        }

        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.EqualTo(2), "it left the realm the session is in");
            Assert.That(harness.NetIdOf(ids[1]), Is.Zero);
        });

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(ids[1], RealmUnit.Pos, RealmId.Default, At(50, 50, 0, 1));
            tx.Commit();
        }

        Run(harness, session, 3);
        Assert.Multiple(() =>
        {
            Assert.That(Held(harness, session), Is.EqualTo(3), "and entered it again on its way back");
            Assert.That(harness.NetIdOf(ids[1]), Is.Not.Zero);
        });
    }

    [Test]
    [VerifiesRule("SUB-30")]
    public void ASessionsFirstPublishedFrameIsAResetWhoseFirstBlockIsItsRealm()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var session = harness.OpenSessions(1, Profile)[0];

        Spawn(dbe, 0, 2);
        harness.RunTick(1);
        var log = harness.Read(session);
        Assert.Multiple(() =>
        {
            Assert.That(log, Is.Not.Null);
            Assert.That(log.Flags & TickFlags.Reset, Is.EqualTo(TickFlags.Reset), "the first published frame is a RESET");
            Assert.That(log.Calls[1], Is.EqualTo("realm 0"), "whose first block is the session's REALM");
            Assert.That(harness.StateOf(session).RealmSent, Is.True);
        });

        // Later frames carry no REALM and no RESET.
        Spawn(dbe, 0, 1);
        harness.RunTick(2);
        var next = harness.Read(session);
        Assert.That(next == null || ((next.Flags & TickFlags.Reset) == 0 && !next.Calls.Contains("realm 0")), Is.True);
    }

    [Test]
    public void TheValidatorChecksOnlyClustersOfServedRealms()
    {
        using var dbe = SetupEngine();
        using var harness = CreateHarness(dbe);
        var hub = harness.Subscriptions.Hub;
        hub.ValidateClustersPerTick = 64;
        var session = harness.OpenSessions(1, Profile)[0];

        // Only realm 1 — not served — has clusters: a validator walking them would count slots it can never push, and forgotten pushes it never saw.
        Spawn(dbe, 1, 3);
        Run(harness, session, 6);

        Assert.Multiple(() =>
        {
            Assert.That(hub.ValidatedSlots, Is.Zero, "no cluster of an unserved realm is validated");
            Assert.That(hub.ForgottenPushes, Is.Zero);
        });

        // A served realm's clusters still are.
        Spawn(dbe, 0, 3);
        Run(harness, session, 6);
        Assert.That(hub.ValidatedSlots, Is.GreaterThan(0), "realm 0's clusters are validated");
    }
}
