using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// A server stall must not read as client silence.
/// </summary>
/// <remarks>
/// The metronome's schedule is absolute: after a tick that overran, the ticks it missed run back to back until the schedule is caught up. Silence counted in
/// ticks then advances faster than the clock does — a PING that arrived during the stall carries the stalled tick, and the catch-up can put the bound's worth
/// of ticks behind it in a few milliseconds, closing a client that spoke well inside the bound.
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class SilenceUnderStallTests : TestBase<SilenceUnderStallTests>
{
    private const string Profile = "god-world";
    private const int Clients = 8;
    private const int TickRateHz = 50;

    [Test]
    [VerifiesRule("SUB-15")]
    [TestCase(1500)]
    [TestCase(4000)]
    public void ClientsThatKeepPingingSurviveAServerStallAndItsCatchUp(int stallMs) => AssertStallSurvived(stallMs, countTicks: false);

    /// <summary>The policy clock replaced by a tick count — what the engine did before — must turn the verifier red.</summary>
    [Test]
    [RuleMutant("SUB-15")]
    public void APolicyClockThatCountsTicksIsDetected() =>
        RuleMutants.AssertDetects("SUB-15", StallMarker, () => AssertStallSurvived(1500, countTicks: true));

    private const string StallMarker = "after a server stall";

    private void AssertStallSurvived(int stallMs, bool countTicks)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var bounds = new ProjBounds { Bounds = new AABB2F { MinX = 10f, MinY = 10f, MaxX = 11f, MaxY = 11f }, Speed = 1f };
            var ai = new ProjAi { Template = 1, Level = 10, Mode = ProjAiMode.Idle };
            var vitals = new ProjVitals { Health = 8, MaxHealth = 10 };
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        long stallAt = -1;
        using var stalled = new ManualResetEventSlim(false);
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("BindProfiles", ctx =>
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

            // A world that changes every tick, so every session is owed a frame on every catch-up tick and the skip run has something to count.
            dag.CallbackSystem("Move", ctx =>
            {
                var tx = ctx.Transaction;
                if (tx == null)
                {
                    return;
                }

                var accessor = tx.For<ProjCreature>();
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                    var bounds = cluster.GetSpan(ProjCreature.Bounds);
#pragma warning restore TYPHON009
                    while (occupancy != 0)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                        occupancy &= occupancy - 1;
                        var current = bounds[slot];
                        current.Bounds.MinX = 10f + (8f * MathF.Sin(ctx.TickNumber * 0.37f));
                        current.Bounds.MaxX = current.Bounds.MinX + 1f;
                        cluster.WriteSpatial(ProjCreature.Bounds, slot, current);
                    }
                }
            });

            // The stall under test: one tick that takes 1.5 s, as a full GC or a heavy admission tick would. A sleep is the subject here, not a wait.
            dag.CallbackSystem("Stall", ctx =>
            {
                if (ctx.TickNumber == Volatile.Read(ref stallAt))
                {
                    Thread.Sleep(stallMs);
                    stalled.Set();
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = TickRateHz,
            Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
        });

        runtime.Subscriptions.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(runtime.Subscriptions);
        runtime.Subscriptions.Profile(Profile, p => p.World().Of<ProjCreature>());
        runtime.Start();
        runtime.SubscriptionsContextForTest.Subscriptions.PolicyClockCountsTicksForTest = countTicks;

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        var clients = new List<TyphonClient>(Clients);
        var disconnects = new Dictionary<ushort, int>();
        try
        {
            for (var i = 0; i < Clients; i++)
            {
                var client = new TyphonClient(new ClientOptions
                {
                    Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"),
                    Kind = "god",
                    // Each client pings on its own loop at the catalog's rate: nothing here is ever silent for longer than the server makes it.
                    PingHz = 4,
                    Reconnect = false,
                    HandshakeTimeout = TimeSpan.FromSeconds(15),
                });

                client.Disconnected += (code, _) =>
                {
                    lock (disconnects)
                    {
                        disconnects[code] = disconnects.GetValueOrDefault(code) + 1;
                    }
                };

                Assert.That(client.ConnectAsync().Wait(TimeSpan.FromSeconds(20)), Is.True, $"client {i} did not complete its handshake");
                clients.Add(client);
            }

            Volatile.Write(ref stallAt, runtime.CurrentTickNumber + 10);
            Assert.That(stalled.Wait(TimeSpan.FromSeconds(10)), Is.True, "the stall never ran");

            // Past the catch-up and a whole silence bound more, so a close the catch-up caused has been applied and told.
            var settled = runtime.CurrentTickNumber + (2 * TickRateHz);
            // Asserted: a runtime that wedged after the stall would otherwise reach the assertions before any close could be applied, and pass.
            Assert.That(SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= settled, TimeSpan.FromSeconds(10)), Is.True,
                "the runtime did not tick on after the stall, so nothing below would have been decided");

            var table = runtime.SubscriptionsContextForTest.Subscriptions.Sessions;
            var open = 0;
            foreach (var client in clients)
            {
                open += client.IsConnected ? 1 : 0;
            }

            var frames = runtime.SubscriptionsContextForTest.Subscriptions.Frames;
            TestContext.Out.WriteLine($"STALL {stallMs} ms: silent {table.ClosesBy(SessionCloseReason.Unacknowledged)} "
                + $"lagging {table.ClosesBy(SessionCloseReason.Lagging)} "
                + $"degraded {frames.SessionsDegraded} longestSkipRun {frames.LongestSkipRun} closeBound {frames.CloseBoundTicks}");
            Assert.Multiple(() =>
            {
                Assert.That(frames.SessionsDegraded, Is.Zero,
                    "sessions were degraded for a stall that was the server's: the skip run counted the catch-up's ticks at more than the rate time passed");
                Assert.That(table.ClosesBy(SessionCloseReason.Lagging), Is.Zero,
                    "sessions were closed 1013 for a stall that was the server's: the skip run counted the catch-up's ticks");
                Assert.That(table.ClosesBy(SessionCloseReason.Unacknowledged), Is.Zero,
                    "sessions that pinged every 250 ms were closed for silence after a server stall: the bound counted the catch-up's ticks, not time");
                Assert.That(open, Is.EqualTo(Clients), $"only {open} of {Clients} sessions survived the stall");
            });
        }
        finally
        {
            foreach (var client in clients)
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }

            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            runtime.Shutdown();
        }
    }
}
