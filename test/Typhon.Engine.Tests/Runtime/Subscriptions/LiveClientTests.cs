using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-20b end to end: <c>Typhon.Client</c> connects to a live engine over TCP, decodes the world it is sent, and replays a recording of that session into an
/// identical store.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first test in which a real client talks to a real server over a real socket.</b> Everything before it drove the engine through an
/// in-process link — correct, and deliberately so, because the transport is a seam the protocol does not depend on. But "the seam is not supposed to matter"
/// is a claim, and until something crosses it the framing, the preamble, the handshake ordering and the client's own receive loop are only tested against
/// themselves.
/// </para>
/// <para>
/// <b>The replay assertion is DoD item 6's .NET half.</b> A recording replays through the same decoder over the same bytes, so a live store and a replayed
/// one can only disagree if the decoder is not deterministic — which is exactly the property worth pinning, and the one that makes a recorded stream usable
/// as a regression fixture later.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class LiveClientTests : TestBase<LiveClientTests>
{
    private const int TickRateHz = 50;
    private const string Profile = "god-world";
    private const int CreatureCount = 12;

    /// <summary>A .NET client connects over TCP, receives the world, and its store matches the server's population.</summary>
    [Test]
    public void AClientConnectsOverTcpAndDecodesTheWorld()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        try
        {
            var recorder = new Recorder();
            var client = Connect(transport.BoundEndPoint.Port, recorder);
            try
            {
                var creatures = ArchetypeIndex(client, nameof(ProjCreature));

                // Spin on the decoded world rather than on a tick count: what is under test is that entities ARRIVE, and a tick count would pass against a
                // client that connected and then received nothing at all.
                var arrived = SpinWait.SpinUntil(() => LiveCount(client, creatures) >= CreatureCount, TimeSpan.FromSeconds(15));

                Assert.Multiple(() =>
                {
                    Assert.That(client.Plan, Is.Not.Null, "the handshake produced no catalog");
                    Assert.That(client.SessionId, Is.Not.Zero, "the server assigned no session id");
                    Assert.That(arrived, Is.True, $"the client decoded {LiveCount(client, creatures)} creatures of {CreatureCount} within fifteen seconds");
                    Assert.That(client.Store.Anomalies, Is.Zero, "the decoder could not apply something the engine sent it");
                    Assert.That(client.MessagesReceived, Is.GreaterThan(0));
                });

                // The recording has to be usable, which means the WELCOME that carries the catalog is in it.
                Assert.That(recorder.Count, Is.GreaterThan(1), "the recorder captured no session worth replaying");
            }
            finally
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }
    }

    /// <summary>A recorded session replays into a store holding the same entities as the live one did.</summary>
    /// <remarks>
    /// The replayed client is constructed with no endpoint reachable and never connects: everything it knows comes from the recording, which is what makes
    /// the comparison meaningful rather than two clients agreeing because they both talked to the same server.
    /// </remarks>
    [Test]
    public void ARecordedSessionReplaysToAnIdenticalStore()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        uint[] liveNetIds;
        Recorder recorder = new();
        try
        {
            var client = Connect(transport.BoundEndPoint.Port, recorder);
            try
            {
                var creatures = ArchetypeIndex(client, nameof(ProjCreature));
                Assert.That(SpinWait.SpinUntil(() => LiveCount(client, creatures) >= CreatureCount, TimeSpan.FromSeconds(15)), Is.True,
                    "the live client never received the world, so there is nothing to replay");

                liveNetIds = NetIds(client, creatures);
            }
            finally
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        var replayed = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false });
        replayed.Replay(recorder);

        var replayedIndex = ArchetypeIndex(replayed, nameof(ProjCreature));
        Assert.Multiple(() =>
        {
            Assert.That(replayed.Store, Is.Not.Null, "the replay produced no store, so the recording did not carry its WELCOME");
            Assert.That(replayed.Store.Anomalies, Is.Zero, "the replay could not apply something the live client applied");
            Assert.That(NetIds(replayed, replayedIndex), Is.EqualTo(liveNetIds).AsCollection,
                "a recorded stream replayed through the same decoder produced a different world from the live one");
        });
    }

    /// <summary>A client whose server is not there fails its connect rather than hanging or silently retrying forever.</summary>
    [Test]
    public void AConnectToNothingFailsRatherThanHanging()
    {
        var client = new TyphonClient(new ClientOptions
        {
            Endpoint = new Uri("tcp://127.0.0.1:1/"),
            Reconnect = false,
            HandshakeTimeout = TimeSpan.FromSeconds(2),
        });

        Assert.That(async () => await client.ConnectAsync(), Throws.Exception, "a connect to a closed port has to fail, and fail promptly");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static TyphonClient Connect(int port, Recorder recorder)
    {
        var client = new TyphonClient(new ClientOptions
        {
            Endpoint = new Uri($"tcp://127.0.0.1:{port}/"),
            Kind = "god",
            Reconnect = false,
            Recorder = recorder,
            HandshakeTimeout = TimeSpan.FromSeconds(10),
        });

        client.ConnectAsync().Wait(TimeSpan.FromSeconds(15));
        return client;
    }

    private static int ArchetypeIndex(TyphonClient client, string name)
    {
        var plan = client.Plan?.ArchetypeByName(name);
        Assert.That(plan, Is.Not.Null, $"the catalog holds no archetype named {name}");
        return plan.Idx;
    }

    private static int LiveCount(TyphonClient client, int archetype)
    {
        var store = client.Store;
        return store == null ? 0 : store.Archetypes[archetype].LiveCount;
    }

    private static uint[] NetIds(TyphonClient client, int archetype)
    {
        var store = client.Store.Archetypes[archetype];
        var ids = new uint[store.LiveCount];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = store.NetIds[store.Live[i]];
        }

        Array.Sort(ids);
        return ids;
    }

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe) => TyphonRuntime.Create(dbe, schedule =>
    {
        schedule.PublicTrack.DeclareDag("Test").CallbackSystem("BindProfiles", ctx =>
        {
            var subs = ctx.Subscriptions;
            if (subs == null)
            {
                return;
            }

            foreach (ref readonly var e in subs.SessionEvents)
            {
                if (e.Kind == SessionEventKind.Opened)
                {
                    subs.Session(e.Session).Profile(Profile);
                }
            }
        });
    }, new RuntimeOptions
    {
        WorkerCount = 1, BaseTickRate = TickRateHz, Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
    });

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                var bounds = new ProjBounds
                {
                    Bounds = new AABB2F { MinX = i * 5f, MinY = 10f, MaxX = (i * 5f) + 1f, MaxY = 11f },
                    Speed = 1f,
                };

                var ai = new ProjAi { Template = (byte)(i + 1), Level = (ushort)(50 + i), Mode = ProjAiMode.Idle };
                var vitals = new ProjVitals { Health = 7, MaxHealth = 10 };
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }
}
