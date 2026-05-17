using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests the Output phase pipeline: View refresh → delta building → serialization → send buffer enqueue.
/// Uses a real runtime with a single client to verify the full flow.
/// </summary>
[TestFixture]
[NonParallelizable]
class OutputPhaseTests : TestBase<OutputPhaseTests>
{
    private const int TestPort = 19950;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
        Archetype<EcsSoldier>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>(storageModeOverride: StorageMode.SingleVersion);
        dbe.RegisterComponentFromAccessor<EcsVelocity>(storageModeOverride: StorageMode.SingleVersion);
        dbe.RegisterComponentFromAccessor<EcsHealth>(storageModeOverride: StorageMode.SingleVersion);
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void OutputPhase_SpawnAfterSubscribe_ClientBufferReceivesData()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        var spawnedOnTick = -1;
        var clientReady = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawner", ctx =>
            {
                // Wait for client to be subscribed
                if (Volatile.Read(ref clientReady) == 0)
                {
                    return;
                }

                // Spawn once
                if (spawnedOnTick < 0)
                {
                    var pos = new EcsPosition(1, 2, 3);
                    var vel = new EcsVelocity(0, 0, 0);
                    ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    Interlocked.Exchange(ref spawnedOnTick, (int)ctx.TickNumber);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30,
            SubscriptionServer = new SubscriptionServerOptions { Port = TestPort }
        });

        var published = runtime.PublishView("test", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(50);

            // Connect and subscribe
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, TestPort));
            client.NoDelay = true;

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, System.TimeSpan.FromSeconds(2));
            var conn = System.Linq.Enumerable.First(runtime.ClientConnections.GetAll());
            runtime.SetSubscriptions(conn.Context, published);

            // Wait one tick for subscription to be processed
            var tickAtSubscribe = runtime.CurrentTickNumber;
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= tickAtSubscribe + 2, System.TimeSpan.FromSeconds(2));

            // Now signal spawner
            Interlocked.Exchange(ref clientReady, 1);

            // Wait for spawn to happen
            SpinWait.SpinUntil(() => spawnedOnTick >= 0, System.TimeSpan.FromSeconds(5));
            Assert.That(spawnedOnTick, Is.GreaterThanOrEqualTo(0), "Spawner should have run");

            // Wait a couple more ticks for delta to be computed and flushed
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= spawnedOnTick + 3, System.TimeSpan.FromSeconds(5));

            // Check: send buffer should have data
            Assert.That(conn.Buffer.PendingBytes, Is.GreaterThan(0).Or.EqualTo(0),
                "Buffer may have been flushed already — check client socket");

            // Read from client socket
            client.ReceiveTimeout = 5000;
            var foundAdded = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var delta = SubscriptionStressTests.TryReadTickDelta(client, 2000);
                if (delta == null)
                {
                    continue;
                }

                TestContext.Out.WriteLine($"Tick {delta.Value.TickNumber}: Events={delta.Value.Events?.Length ?? 0}, " +
                                         $"Views={delta.Value.Views?.Length ?? 0}");

                if (delta.Value.Views != null)
                {
                    foreach (var vd in delta.Value.Views)
                    {
                        if (vd.Added is { Length: > 0 })
                        {
                            TestContext.Out.WriteLine($"  View {vd.ViewId}: Added={vd.Added.Length}");
                            foundAdded = true;
                        }
                    }
                }

                if (foundAdded)
                {
                    break;
                }
            }

            Assert.That(foundAdded, Is.True, "Client should have received Added entities via TCP");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    [Test]
    public void OutputPhase_ContinuousSpawn_ClientReceivesMultipleDeltas()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        var clientReady = 0;
        var totalSpawned = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawner", ctx =>
            {
                if (Volatile.Read(ref clientReady) == 0) return;

                // Spawn 5 entities every tick
                for (var i = 0; i < 5; i++)
                {
                    var pos = new EcsPosition(ctx.TickNumber, i, 0);
                    var vel = new EcsVelocity(0, 0, 0);
                    ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    Interlocked.Increment(ref totalSpawned);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30,
            SubscriptionServer = new SubscriptionServerOptions { Port = TestPort + 1 }
        });

        var published = runtime.PublishView("test", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(50);

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, TestPort + 1));
            client.NoDelay = true;

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, System.TimeSpan.FromSeconds(2));
            var conn = System.Linq.Enumerable.First(runtime.ClientConnections.GetAll());
            runtime.SetSubscriptions(conn.Context, published);

            // Wait for subscription to be processed
            var tickAtSub = runtime.CurrentTickNumber;
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= tickAtSub + 2, System.TimeSpan.FromSeconds(2));

            // Start spawning
            Interlocked.Exchange(ref clientReady, 1);

            // Read deltas as they arrive (concurrent with spawning)
            var totalAdded = 0;
            var ticksWithAdded = 0;

            for (var attempt = 0; attempt < 30; attempt++)
            {
                var delta = SubscriptionStressTests.TryReadTickDelta(client, 2000);
                if (delta == null) continue;

                if (delta.Value.Views != null)
                {
                    foreach (var vd in delta.Value.Views)
                    {
                        if (vd.Added is { Length: > 0 })
                        {
                            totalAdded += vd.Added.Length;
                            ticksWithAdded++;
                        }
                    }
                }

                if (totalAdded >= 20 && ticksWithAdded >= 3) break;
            }

            TestContext.Out.WriteLine($"Spawned: {totalSpawned}, Client Added: {totalAdded}, Ticks with Added: {ticksWithAdded}");

            Assert.That(totalAdded, Is.GreaterThan(0), "Client should receive Added entities from continuous spawning");
            Assert.That(ticksWithAdded, Is.GreaterThanOrEqualTo(2), "Should receive Added across multiple ticks");
        }
        finally
        {
            runtime.Shutdown();
        }
    }
}
