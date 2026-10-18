using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// <see cref="TyphonClient.SubscribeAsync"/> end to end (14 § 4, P13): a .NET client over TCP asks a live engine for keys of an explicit set, and each call
/// settles with the server's answer once the key's members are in its store — or with the application's refusal, or dropped when the session ends first.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class LiveGroupSubscriptionTests : TestBase<LiveGroupSubscriptionTests>
{
    private const string Lists = "lists";
    private const string Profile = "p";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Test]
    [VerifiesRule("SUB-33")]
    public async Task SubscribeAsyncSettlesWithTheServersAnswer()
    {
        var dbe = GroupTestSchema.SetupEngine(ServiceProvider);
        var anchor = Populate(dbe);
        using var runtime = CreateRuntime(dbe, anchor);
        Declare(runtime.Subscriptions);
        runtime.Start();
        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);
        var client = new TyphonClient(new ClientOptions
        {
            Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"), Kind = "god", Reconnect = false, HandshakeTimeout = Patience,
        });

        try
        {
            await client.ConnectAsync().WaitAsync(Patience);
            var set = (uint)client.Plan.GroupSetIndex(Lists);
            var shards = client.Plan.ArchetypeByName(nameof(GroupPositionless)).Idx;
            int Held() => client.Store.Archetypes[shards].LiveCount;

            var applied = await client.SubscribeAsync(Lists, 7).WaitAsync(Patience);
            Assert.Multiple(() =>
            {
                Assert.That(applied.Outcome, Is.EqualTo(SubscribeOutcome.Applied));
                Assert.That(client.Store.IsGroupComplete(set, 7), Is.True, "APPLIED follows the completion it reports");
                Assert.That(Held(), Is.EqualTo(4));
            });

            var refused = await client.SubscribeAsync(Lists, 9).WaitAsync(Patience);
            Assert.That((refused.Outcome, refused.Code), Is.EqualTo((SubscribeOutcome.Refused, (ushort)150)), "the application's own code");

            // A login: twelve at once, inside the burst the catalog gives (MaxGroupsPerSession), none sent again.
            var login = await Task.WhenAll(Enumerable.Range(21, 12).Select(k => client.SubscribeAsync(Lists, k))).WaitAsync(Patience);
            Assert.Multiple(() =>
            {
                Assert.That(login.Select(r => r.Outcome), Is.All.EqualTo(SubscribeOutcome.Applied));
                Assert.That(login.Select(r => r.Key), Is.EqualTo(Enumerable.Range(21, 12).Select(k => (long)k)));
                Assert.That(Held(), Is.EqualTo(16));
            });

            var released = await client.UnsubscribeAsync(Lists, 7).WaitAsync(Patience);
            Assert.Multiple(() =>
            {
                Assert.That(released.Outcome, Is.EqualTo(SubscribeOutcome.Applied));
                Assert.That(released.Op, Is.EqualTo(BuiltInCommands.UnsubscribeOp));
                Assert.That(Held(), Is.EqualTo(12), "its members left with the release");
                Assert.That(client.Store.Anomalies, Is.Zero);
            });

            Assert.ThrowsAsync<ArgumentException>(() => client.SubscribeAsync("nowhere", 1));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SubscribeAsync(Lists, 0));

            // The session ends with a request still waiting: it is dropped, never left pending.
            await client.CloseAsync();
            var pending = client.SubscribeAsync(Lists, 40);
            await client.DisposeAsync();
            var dropped = await pending.WaitAsync(Patience);
            Assert.That(dropped.Outcome, Is.EqualTo(SubscribeOutcome.Dropped));
        }
        finally
        {
            await client.DisposeAsync();
            transport.StopAsync().AsTask().Wait(Patience);
            runtime.Shutdown();
        }
    }

    // A building at the origin for the sphere to follow, and shards: four of list 7, two of list 9, one each of lists 21 to 32.
    private static EntityId Populate(DatabaseEngine dbe)
    {
        EntityId anchor;
        using (var tx = dbe.CreateQuickTransaction())
        {
            anchor = tx.Spawn<GroupBuilding>(GroupBuilding.Bounds.Set(GroupTestSchema.At(0f, 0f)), GroupBuilding.Ai.Set(new ProjAi { Template = 1 }),
                GroupBuilding.Key.Set(new GroupKey { Town = 0 }));
            void Shard(int key, int n)
            {
                for (var i = 0; i < n; i++)
                {
                    tx.Spawn<GroupPositionless>(GroupPositionless.Ai.Set(new ProjAi { Template = (byte)(key + i) }),
                        GroupPositionless.Key.Set(new GroupKey { Town = key }));
                }
            }

            Shard(7, 4);
            Shard(9, 2);
            for (var k = 21; k <= 32; k++)
            {
                Shard(k, 1);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return anchor;
    }

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, EntityId anchor) => TyphonRuntime.Create(dbe, schedule =>
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
                    subs.Session(e.Session).Profile(Profile).Follow(anchor);
                }
            }
        });
    }, new RuntimeOptions
    {
        WorkerCount = 1, BaseTickRate = 50,
        Subscriptions = new SubscriptionsOptions
        {
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(120), MaxGroupsPerSession = 16, IngressBytesPerSecond = TestIngress.Budget,
        },
    });

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        subs.Static<GroupBuilding>(a => a.Position(GroupBuilding.Bounds));
        subs.Archetype<GroupPositionless>(a => a.Field(GroupPositionless.Ai, x => x.Template, Codec.U8, name: "kind"));
        subs.Groups(Lists, g => g.Explicit((in GroupRequest r) => r.Key == 9 ? GroupDecision.Deny(150) : GroupDecision.Allow)
            .Member<GroupPositionless>(m => m.Key(GroupPositionless.Key, k => k.Town)));
        subs.Profile(Profile, p => p.Sphere(20).Of<GroupBuilding>().Groups(Lists));
    }
}
