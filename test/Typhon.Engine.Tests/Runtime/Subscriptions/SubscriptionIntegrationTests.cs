using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// End-to-end integration tests for the subscription server.
/// Starts a real TyphonRuntime with TCP subscription server, connects a client, and verifies TickDelta messages.
/// </summary>
[TestFixture]
class SubscriptionIntegrationTests : TestBase<SubscriptionIntegrationTests>
{
    private const int TestPort = 19876; // High port to avoid conflicts

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Read a single length-prefixed MemoryPack frame from a TCP socket.</summary>
    private static TickDeltaMessage ReadTickDelta(Socket socket, int timeoutMs = 5000)
    {
        socket.ReceiveTimeout = timeoutMs;

        // Read 4-byte length prefix
        var lengthBuf = new byte[4];
        var received = 0;
        while (received < 4)
        {
            var n = socket.Receive(lengthBuf, received, 4 - received, SocketFlags.None);
            if (n == 0)
            {
                throw new Exception("Connection closed while reading length prefix");
            }

            received += n;
        }

        var payloadLength = BitConverter.ToInt32(lengthBuf, 0);
        Assert.That(payloadLength, Is.GreaterThan(0).And.LessThan(1_000_000), "Payload length sanity check");

        // Read payload
        var payload = new byte[payloadLength];
        received = 0;
        while (received < payloadLength)
        {
            var n = socket.Receive(payload, received, payloadLength - received, SocketFlags.None);
            if (n == 0)
            {
                throw new Exception("Connection closed while reading payload");
            }

            received += n;
        }

        return MemoryPackSerializer.Deserialize<TickDeltaMessage>(payload);
    }

    [Test]
    public void Client_Connects_And_ReceivesTickDelta_WithSpawnedEntity()
    {
        using var dbe = SetupEngine();

        // Create View for subscriptions
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        EntityId spawnedId = default;
        var spawnedOnTick = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawner", ctx =>
            {
                var tick = (int)ctx.TickNumber;
                if (tick == 2) // Spawn on tick 2 to give client time to subscribe
                {
                    var pos = new EcsPosition(1.0f, 2.0f, 3.0f);
                    var vel = new EcsVelocity(0, 0, 0);
                    spawnedId = ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    Interlocked.Exchange(ref spawnedOnTick, tick);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30, // Slower tick rate for test stability
            SubscriptionServer = new SubscriptionServerOptions { Port = TestPort }
        });

        // Publish the View
        var published = runtime.PublishView("test_units", subsView);

        runtime.Start();

        try
        {
            // Wait for server to start
            Thread.Sleep(100);

            // Connect TCP client
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, TestPort));
            client.NoDelay = true;

            // Wait for the connection to register
            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, TimeSpan.FromSeconds(2));
            Assert.That(runtime.ClientConnections.Count, Is.EqualTo(1), "One client should be connected");

            // Set subscriptions for the connected client
            var clientConn = runtime.ClientConnections.GetAll().First();
            runtime.SetSubscriptions(clientConn.Context, published);

            // Wait for entity to be spawned
            SpinWait.SpinUntil(() => spawnedOnTick > 0, TimeSpan.FromSeconds(5));
            Assert.That(spawnedId.IsNull, Is.False, "Entity should have been spawned");

            // Read TickDelta messages until we find one with Added entities
            TickDeltaMessage? deltaWithAdded = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var delta = ReadTickDelta(client, 2000);
                    if (delta.Views != null)
                    {
                        var viewDelta = delta.Views.FirstOrDefault(v => v.Added is { Length: > 0 });
                        if (viewDelta.Added != null)
                        {
                            deltaWithAdded = delta;
                            break;
                        }
                    }
                }
                catch (SocketException)
                {
                    // Timeout — try again
                }
            }

            Assert.That(deltaWithAdded, Is.Not.Null, "Should have received a TickDelta with Added entities");

            // Verify the Added entity
            var addedView = deltaWithAdded.Value.Views.First(v => v.Added is { Length: > 0 });
            Assert.That(addedView.Added.Length, Is.GreaterThanOrEqualTo(1));

            // At least one Added entity should have component snapshots
            var addedEntity = addedView.Added[0];
            Assert.That(addedEntity.Id, Is.Not.EqualTo(0), "Added entity should have a valid ID");
            Assert.That(addedEntity.Components, Is.Not.Null.And.Not.Empty, "Added entity should have component data");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    [Test]
    public void PublishView_WithNoClients_DoesNotThrow()
    {
        using var dbe = SetupEngine();

        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 100,
            SubscriptionServer = new SubscriptionServerOptions { Port = TestPort + 1 }
        });

        runtime.PublishView("test_units", subsView);
        runtime.Start();

        // Run a few ticks with published View but no clients — should not crash
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));

        runtime.Shutdown();
        Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(5));
    }
}
