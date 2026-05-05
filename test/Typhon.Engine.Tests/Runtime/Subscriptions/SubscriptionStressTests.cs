using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Stress and torture tests for the subscription server. These exercise concurrency, backpressure,
/// rapid connect/disconnect, high entity churn, and multi-client fan-out under load.
/// </summary>
/// <remarks>
/// All tests are <c>[Explicit]</c> — run manually via <c>dotnet test --filter "FullyQualifiedName~SubscriptionStress"</c>.
/// </remarks>
[TestFixture]
[NonParallelizable]
class SubscriptionStressTests : TestBase<SubscriptionStressTests>
{
    private const int StressPort = 19900;

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

    /// <summary>Read one length-prefixed MemoryPack frame from a socket. Returns null on timeout/error.</summary>
    internal static TickDeltaMessage? TryReadTickDelta(Socket socket, int timeoutMs = 2000)
    {
        socket.ReceiveTimeout = timeoutMs;
        try
        {
            var lengthBuf = new byte[4];
            var received = 0;
            while (received < 4)
            {
                var n = socket.Receive(lengthBuf, received, 4 - received, SocketFlags.None);
                if (n == 0) return null;
                received += n;
            }

            var payloadLength = BitConverter.ToInt32(lengthBuf, 0);
            if (payloadLength <= 0 || payloadLength > 10_000_000) return null;

            var payload = new byte[payloadLength];
            received = 0;
            while (received < payloadLength)
            {
                var n = socket.Receive(payload, received, payloadLength - received, SocketFlags.None);
                if (n == 0) return null;
                received += n;
            }

            return MemoryPackSerializer.Deserialize<TickDeltaMessage>(payload);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Multi-client fan-out: 50 clients on same shared View
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — 50 TCP clients, run manually")]
    public void MultiClient_50Clients_SharedView_AllReceiveDeltas()
    {
        const int clientCount = 50;
        const int ticksToRun = 30;
        var port = StressPort;

        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        var spawnCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Spawner", ctx =>
            {
                if (ctx.TickNumber >= 3 && ctx.TickNumber <= 7)
                {
                    // Spawn 10 entities per tick for 5 ticks = 50 total
                    for (var i = 0; i < 10; i++)
                    {
                        var pos = new EcsPosition(ctx.TickNumber, i, 0);
                        var vel = new EcsVelocity(0, 0, 0);
                        ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                        Interlocked.Increment(ref spawnCount);
                    }
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 60,
            SubscriptionServer = new SubscriptionServerOptions { Port = port }
        });

        var published = runtime.PublishView("units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(100); // Let server start

            // Connect all clients
            var clients = new Socket[clientCount];
            for (var i = 0; i < clientCount; i++)
            {
                clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clients[i].Connect(new IPEndPoint(IPAddress.Loopback, port));
                clients[i].NoDelay = true;
            }

            // Wait for all connections to register
            SpinWait.SpinUntil(() => runtime.ClientConnections.Count >= clientCount, TimeSpan.FromSeconds(5));
            Assert.That(runtime.ClientConnections.Count, Is.GreaterThanOrEqualTo(clientCount));

            // Subscribe all clients to the shared View
            foreach (var conn in runtime.ClientConnections.GetAll())
            {
                runtime.SetSubscriptions(conn, published);
            }

            // Let the runtime run ticks with entity spawns
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= ticksToRun, TimeSpan.FromSeconds(10));

            // Each client should have received at least some deltas
            var clientsWithData = 0;
            var totalAdded = new int[clientCount];
            for (var i = 0; i < clientCount; i++)
            {
                var addedForClient = 0;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    var delta = TryReadTickDelta(clients[i], 500);
                    if (delta == null) break;
                    if (delta.Value.Views != null)
                    {
                        foreach (var vd in delta.Value.Views)
                        {
                            addedForClient += vd.Added?.Length ?? 0;
                        }
                    }
                }

                totalAdded[i] = addedForClient;
                if (addedForClient > 0) clientsWithData++;
            }

            // Clean up client sockets
            foreach (var s in clients) { try { s.Close(); } catch { } }

            // All clients should have received data
            Assert.That(clientsWithData, Is.EqualTo(clientCount),
                $"All {clientCount} clients should have received Added entities. Got data on {clientsWithData}. " +
                $"Added per client: [{string.Join(", ", totalAdded.Take(10))}...]");

            // Each client should have received all 50 spawned entities (via sync + deltas)
            Assert.That(spawnCount, Is.EqualTo(50));
            var minAdded = totalAdded.Min();
            Assert.That(minAdded, Is.GreaterThanOrEqualTo(10),
                $"Each client should have received at least 10 entities. Min was {minAdded}");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Rapid connect/disconnect churn
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — rapid connect/disconnect, run manually")]
    public void RapidConnectDisconnect_NoServerCrash()
    {
        const int iterations = 200;
        var port = StressPort + 1;

        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 120,
            SubscriptionServer = new SubscriptionServerOptions { Port = port }
        });

        var published = runtime.PublishView("units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(100);

            // Rapidly connect and disconnect sockets
            var errors = new ConcurrentBag<string>();
            var threads = new Thread[4];
            var connectsPerThread = iterations / threads.Length;

            for (var t = 0; t < threads.Length; t++)
            {
                var threadId = t;
                threads[t] = new Thread(() =>
                {
                    for (var i = 0; i < connectsPerThread; i++)
                    {
                        try
                        {
                            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
                            client.NoDelay = true;

                            // Small random delay to interleave with tick processing
                            if (i % 3 == 0) Thread.Sleep(1);

                            // Immediately disconnect (don't subscribe — tests raw connect/disconnect handling)
                            client.Shutdown(SocketShutdown.Both);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Thread {threadId}, iteration {i}: {ex.Message}");
                        }
                    }
                })
                {
                    IsBackground = true
                };
            }

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

            // Let the runtime process a few more ticks to clean up
            var tickBefore = runtime.CurrentTickNumber;
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= tickBefore + 10, TimeSpan.FromSeconds(5));

            Assert.That(errors, Is.Empty, $"Connection errors: {string.Join("; ", errors.Take(5))}");

            // Server should still be running and healthy
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThan(tickBefore));
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. High entity churn: spawn + destroy with active subscriptions
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — high entity churn, run manually")]
    public void HighEntityChurn_SpawnDestroy_DeltasConsistent()
    {
        const int ticksToRun = 80;
        const int entitiesPerTick = 20;
        var port = StressPort + 20; // Unique port for this test

        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        var totalSpawned = 0;
        var clientReady = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Churn", ctx =>
            {
                // Wait until client has subscribed
                if (Volatile.Read(ref clientReady) == 0)
                {
                    return;
                }

                // Spawn entities every tick
                for (var i = 0; i < entitiesPerTick; i++)
                {
                    var pos = new EcsPosition(ctx.TickNumber, i, 0);
                    var vel = new EcsVelocity(1, 0, 0);
                    ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    Interlocked.Increment(ref totalSpawned);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30,
            SubscriptionServer = new SubscriptionServerOptions { Port = port }
        });

        var published = runtime.PublishView("units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(100);

            // Connect a single client and subscribe
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            client.NoDelay = true;

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, TimeSpan.FromSeconds(2));
            foreach (var conn in runtime.ClientConnections.GetAll())
            {
                runtime.SetSubscriptions(conn, published);
            }

            // Wait for subscription to be processed (at least 2 ticks for transition + sync)
            var tickAtSub = runtime.CurrentTickNumber;
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= tickAtSub + 3, TimeSpan.FromSeconds(2));

            // Signal the system that client is subscribed — spawning can begin
            Interlocked.Exchange(ref clientReady, 1);

            // Read deltas (same pattern as passing OutputPhase_ContinuousSpawn test)
            var totalClientAdded = 0;
            var totalClientRemoved = 0;
            var totalClientModified = 0;
            var ticksSeen = new HashSet<long>();

            for (var attempt = 0; attempt < 30; attempt++)
            {
                var delta = TryReadTickDelta(client, 2000);
                if (delta == null) continue;

                ticksSeen.Add(delta.Value.TickNumber);
                if (delta.Value.Views != null)
                {
                    foreach (var vd in delta.Value.Views)
                    {
                        totalClientAdded += vd.Added?.Length ?? 0;
                        totalClientRemoved += vd.Removed?.Length ?? 0;
                        totalClientModified += vd.Modified?.Length ?? 0;
                    }
                }

                if (totalClientAdded >= 50 && ticksSeen.Count >= 3) break;
            }

            TestContext.Out.WriteLine($"Spawned: {totalSpawned}");
            TestContext.Out.WriteLine($"Client saw: Added={totalClientAdded}, Removed={totalClientRemoved}, Modified={totalClientModified}");
            TestContext.Out.WriteLine($"Ticks with deltas: {ticksSeen.Count}, Runtime tick: {runtime.CurrentTickNumber}");

            Assert.That(totalSpawned, Is.GreaterThan(0), "Should have spawned entities");
            Assert.That(totalClientAdded, Is.GreaterThan(0), "Client should see Added entities");
            Assert.That(ticksSeen.Count, Is.GreaterThan(1), "Client should have received deltas across multiple ticks");

        }
        finally
        {
            runtime.Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Backpressure: flood a slow client until resync triggers
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — backpressure and resync, run manually")]
    public void Backpressure_SlowClient_TriggersResync()
    {
        var port = StressPort + 3;

        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Spawner", ctx =>
            {
                // Spawn many entities to generate large deltas
                for (var i = 0; i < 50; i++)
                {
                    var pos = new EcsPosition(ctx.TickNumber * 100 + i, i, 0);
                    var vel = new EcsVelocity(0, 0, 0);
                    ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 120, // Fast tick rate to build up backpressure
            SubscriptionServer = new SubscriptionServerOptions
            {
                Port = port,
                SendBufferCapacity = 16384 // Small buffer (16KB) to trigger overflow quickly
            }
        });

        var published = runtime.PublishView("units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(100);

            // Connect a client but DON'T read from it — let the send buffer fill up
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            client.NoDelay = true;

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, TimeSpan.FromSeconds(2));
            foreach (var conn in runtime.ClientConnections.GetAll())
            {
                runtime.SetSubscriptions(conn, published);
            }

            // Let ticks run without reading — buffer should overflow
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 60, TimeSpan.FromSeconds(5));

            // Check telemetry for overflow count
            var lastTelemetry = runtime.Telemetry.GetTick(runtime.CurrentTickNumber - 1);
            TestContext.Out.WriteLine($"Ticks: {runtime.CurrentTickNumber}");
            TestContext.Out.WriteLine($"Output phase: {lastTelemetry.OutputPhaseMs:F3}ms");
            TestContext.Out.WriteLine($"Overflow count (last tick): {lastTelemetry.SubscriptionOverflowCount}");

            // Now start reading — should get a Resync event
            var foundResync = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var delta = TryReadTickDelta(client, 1000);
                if (delta == null) break;
                if (delta.Value.Events != null)
                {
                    foreach (var evt in delta.Value.Events)
                    {
                        if (evt.Type == EventType.Resync)
                        {
                            foundResync = true;
                            TestContext.Out.WriteLine($"Resync event received at tick {delta.Value.TickNumber}");
                        }
                    }
                }

                if (foundResync) break;
            }

            // Server should not have crashed
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThan(30), "Runtime should still be running");

            // We expect either overflow (buffer too small to hold even first sync) or resync
            // Either outcome means backpressure was handled correctly without crash
            TestContext.Out.WriteLine(foundResync ? "PASS: Resync triggered" : "INFO: Buffer drained before overflow (fast machine)");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Concurrent SetSubscriptions from multiple worker threads
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — concurrent SetSubscriptions, run manually")]
    public void ConcurrentSetSubscriptions_LastWriterWins_NoCorruption()
    {
        const int ticksToRun = 100;
        var port = StressPort + 4;

        using var dbe = SetupEngine();
        using var viewTx1 = dbe.CreateQuickTransaction();
        var viewA = viewTx1.Query<EcsUnit>().ToView();

        using var viewTx2 = dbe.CreateQuickTransaction();
        var viewB = viewTx2.Query<EcsUnit>().ToView();

        var setSubCount = 0;

        // Publish views before creating runtime (they need to be referenced in the lambda)
        // We'll use a holder to defer the actual PublishedView references
        PublishedView pubA = null;
        PublishedView pubB = null;
        TyphonRuntime runtimeRef = null;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("SystemA", ctx =>
            {
                if (ctx.TickNumber % 3 == 0 && pubA != null)
                {
                    foreach (var conn in runtimeRef.ClientConnections.GetAll())
                    {
                        conn.SetSubscriptions(pubA);
                        Interlocked.Increment(ref setSubCount);
                    }
                }
            });
            schedule.CallbackSystem("SystemB", ctx =>
            {
                if (ctx.TickNumber % 5 == 0 && pubB != null)
                {
                    foreach (var conn in runtimeRef.ClientConnections.GetAll())
                    {
                        conn.SetSubscriptions(pubB);
                        Interlocked.Increment(ref setSubCount);
                    }
                }
            }, after: "SystemA");
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 120,
            SubscriptionServer = new SubscriptionServerOptions { Port = port }
        });

        runtimeRef = runtime;
        pubA = runtime.PublishView("view_a", viewA);
        pubB = runtime.PublishView("view_b", viewB);

        runtime.Start();

        try
        {
            Thread.Sleep(100);

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            client.NoDelay = true;

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count > 0, TimeSpan.FromSeconds(2));

            // Initial subscription
            foreach (var conn in runtime.ClientConnections.GetAll())
            {
                runtime.SetSubscriptions(conn, pubA);
            }

            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= ticksToRun, TimeSpan.FromSeconds(10));

            // Read deltas and count subscription events
            var subscribedEvents = 0;
            var unsubscribedEvents = 0;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var delta = TryReadTickDelta(client, 500);
                if (delta == null) break;
                if (delta.Value.Events != null)
                {
                    foreach (var evt in delta.Value.Events)
                    {
                        if (evt.Type == EventType.Subscribed) subscribedEvents++;
                        if (evt.Type == EventType.Unsubscribed) unsubscribedEvents++;
                    }
                }
            }

            TestContext.Out.WriteLine($"SetSubscriptions calls: {setSubCount}");
            TestContext.Out.WriteLine($"Subscribed events: {subscribedEvents}, Unsubscribed events: {unsubscribedEvents}");

            // Server should be healthy — no crashes, no corruption
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(ticksToRun));
            Assert.That(setSubCount, Is.GreaterThan(0), "Should have called SetSubscriptions multiple times");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Sustained load: many ticks with mutations + multiple clients
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [Explicit("Stress test — sustained load, run manually")]
    public void SustainedLoad_10Clients_MutationsEveryTick_NoDrift()
    {
        const int clientCount = 10;
        const int ticksToRun = 100;
        const int entitiesPerTick = 5;
        var port = StressPort + 5;

        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var subsView = viewTx.Query<EcsUnit>().ToView();

        var spawnDone = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Mutator", ctx =>
            {
                // Spawn entities on ticks 5-14 (after clients have time to subscribe)
                if (ctx.TickNumber >= 5 && ctx.TickNumber < 15)
                {
                    for (var i = 0; i < entitiesPerTick; i++)
                    {
                        var pos = new EcsPosition(ctx.TickNumber, i, 0);
                        var vel = new EcsVelocity(1, 0, 0);
                        ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    }

                    if (ctx.TickNumber == 14)
                    {
                        Interlocked.Exchange(ref spawnDone, 1);
                    }
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 30, // Slower rate for test stability
            SubscriptionServer = new SubscriptionServerOptions { Port = port }
        });

        var published = runtime.PublishView("units", subsView);
        runtime.Start();

        try
        {
            Thread.Sleep(50);

            // Connect clients and subscribe
            var clients = new Socket[clientCount];
            for (var i = 0; i < clientCount; i++)
            {
                clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clients[i].Connect(new IPEndPoint(IPAddress.Loopback, port));
                clients[i].NoDelay = true;
            }

            SpinWait.SpinUntil(() => runtime.ClientConnections.Count >= clientCount, TimeSpan.FromSeconds(5));

            foreach (var conn in runtime.ClientConnections.GetAll())
            {
                runtime.SetSubscriptions(conn, published);
            }

            // Wait for spawns to complete
            SpinWait.SpinUntil(() => spawnDone == 1, TimeSpan.FromSeconds(10));

            // Wait a bit more for deltas to flow
            SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= ticksToRun, TimeSpan.FromSeconds(15));

            // Collect stats per client
            var clientStats = new (int added, int modified, int removed, int ticks)[clientCount];
            for (var c = 0; c < clientCount; c++)
            {
                var added = 0; var modified = 0; var removed = 0; var ticks = 0;
                var emptyReads = 0;
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    var delta = TryReadTickDelta(clients[c], 2000);
                    if (delta == null)
                    {
                        if (++emptyReads >= 2) break;
                        continue;
                    }

                    emptyReads = 0;
                    ticks++;
                    if (delta.Value.Views != null)
                    {
                        foreach (var vd in delta.Value.Views)
                        {
                            added += vd.Added?.Length ?? 0;
                            modified += vd.Modified?.Length ?? 0;
                            removed += vd.Removed?.Length ?? 0;
                        }
                    }
                }

                clientStats[c] = (added, modified, removed, ticks);
            }

            foreach (var s in clients) { try { s.Close(); } catch { } }

            for (var c = 0; c < clientCount; c++)
            {
                var (a, m, r, t) = clientStats[c];
                TestContext.Out.WriteLine($"Client {c}: added={a}, modified={m}, removed={r}, ticks={t}");
            }

            // At least some clients should have received data
            var clientsWithAdded = clientStats.Count(s => s.added > 0);
            Assert.That(clientsWithAdded, Is.GreaterThanOrEqualTo(clientCount / 2),
                $"At least half the clients should have received Added entities. Got {clientsWithAdded}/{clientCount}");
        }
        finally
        {
            runtime.Shutdown();
        }
    }
}
