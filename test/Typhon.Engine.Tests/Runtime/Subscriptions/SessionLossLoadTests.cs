using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// Thousands of real sessions against one in-process server, with every close counted by its reason on both sides: the one-box AC-4 question asked without
/// the demo.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The SWG one-box run lost more than half of 5 000 sessions on <c>main</c> and on the Phase 5 branch alike, mostly closed 4001 for
/// silence. The demo's swarm pings only between its sequential connects and then from one serial loop, so it can manufacture that silence itself — but the
/// same swarm lost 242 on 2026-09-24, and only a client that is known to ping can say whether the server is closing sessions that were talking to it.
/// </para>
/// <para>
/// <b>Two client shapes.</b> <see cref="PingMode.Own"/> is the SDK as shipped: every client pings on its own loop at the catalog's rate.
/// <see cref="PingMode.Shared"/> is the swarm's shape: <c>PingHz = 0</c>, connects awaited one after another with the population pinged between them, then
/// one loop pinging everyone in turn. A loss under <see cref="PingMode.Own"/> is the server's; a loss under <see cref="PingMode.Shared"/> alone is the
/// generator's, and the driver's own longest gap says so.
/// </para>
/// <para>
/// Explicit: it holds thousands of sockets and runs for tens of seconds, which is a diagnosis, not a gate.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Explicit("load diagnosis: thousands of sessions for tens of seconds")]
// Manual, not Nightly: it is a diagnosis whose numbers belong to the box it ran on, and 5 000 sockets on a shared CI runner measure the runner.
[Category("Manual")]
sealed class SessionLossLoadTests : TestBase<SessionLossLoadTests>
{
    private const string Profile = "god-world";

    public enum PingMode
    {
        Own,
        Shared,
    }

    [Test]
    [TestCase(1000, PingMode.Own, 1, 100, 50, 8)]
    [TestCase(5000, PingMode.Own, 1, 100, 50, 8)]
    // 1 000, not 5 000: the shared loop pings everyone between connects, so its ramp slows as the population grows (~5 connects/s by 2 600).
    [TestCase(1000, PingMode.Shared, 1, 100, 50, 8)]
    [TestCase(5000, PingMode.Own, 64, 100, 50, 8)]
    public void SessionsSurviveTheRampAndTheRun(int bots, PingMode mode, int connectConcurrency, int creatures, int tickRateHz, int workers)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe, creatures);

        using var runtime = CreateRuntime(dbe, tickRateHz, workers);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(new TcpSubscriptionOptions { MaxPendingConnections = 4096 });
        runtime.StartSubscriptionTransport(transport);

        var subs = runtime.SubscriptionsContextForTest.Subscriptions;
        var frames = subs.Frames;
        var table = subs.Sessions;
        var clients = new List<TyphonClient>(bots);
        var disconnects = new Dictionary<ushort, int>();
        var handshakeMs = new List<double>(bots);
        var failedConnects = 0;
        var driverLongestGapMs = 0.0;
        var driverLongestLapMs = 0.0;
        // Progress, not Out: a run that hangs or is killed still shows how far it got.
        var log = TestContext.Progress;
        var clock = Stopwatch.StartNew();

        TyphonClient NewClient() =>
            new(new ClientOptions
            {
                Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"),
                Kind = "god",
                PingHz = mode == PingMode.Own ? 4 : 0,
                Reconnect = false,
                HandshakeTimeout = TimeSpan.FromSeconds(30),
                SegmentHistory = 1,
            });

        void Sample(string phase)
        {
            int open;
            lock (clients)
            {
                open = 0;
                foreach (var c in clients)
                {
                    open += c.IsConnected ? 1 : 0;
                }
            }

            var stats = runtime.ReadStats(tickRateHz);
            log.WriteLine($"  {phase,-6} t+{clock.Elapsed.TotalSeconds,5:F1}s open {open,5}  "
                + $"closed silent {table.ClosesBy(SessionCloseReason.Unacknowledged),5} "
                + $"lagging {table.ClosesBy(SessionCloseReason.Lagging),5} linkLost {table.ClosesBy(SessionCloseReason.LinkLost),5}  "
                + $"tick p50 {stats.TickP50Ms,6:F2} p99 {stats.TickP99Ms,6:F2} ms  longestSkipRun {frames.LongestSkipRun}");
        }

        using var stopping = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(connectConcurrency);
        Task driver = null;
        try
        {
            // ── the ramp ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
            var rampPing = Stopwatch.StartNew();
            var rampPingPeriod = TimeSpan.FromMilliseconds(125);
            var lastSample = 0L;
            var connects = new List<Task>(bots);
            for (var i = 0; i < bots; i++)
            {
                var client = NewClient();
                client.Disconnected += (code, _) =>
                {
                    lock (disconnects)
                    {
                        disconnects[code] = disconnects.GetValueOrDefault(code) + 1;
                    }
                };

                gate.Wait();
                var connect = ConnectAsync(client);
                connects.Add(connect);
                if (connectConcurrency == 1)
                {
                    connect.Wait();
                }

                // The swarm's ramp keepalive: the connected population pinged between connects, at half the ping period.
                if (mode == PingMode.Shared && rampPing.Elapsed >= rampPingPeriod)
                {
                    driverLongestGapMs = Math.Max(driverLongestGapMs, rampPing.Elapsed.TotalMilliseconds);
                    rampPing.Restart();
                    PingAll(clients, CancellationToken.None).Wait();
                }

                if (clock.ElapsedMilliseconds - lastSample >= 2000)
                {
                    lastSample = clock.ElapsedMilliseconds;
                    Sample("ramp");
                }
            }

            Task.WaitAll(connects.ToArray(), TimeSpan.FromSeconds(120));
            Sample("opened");

            // ── the run ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
            if (mode == PingMode.Shared)
            {
                driver = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                    var lap = Stopwatch.StartNew();
                    try
                    {
                        while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                        {
                            driverLongestGapMs = Math.Max(driverLongestGapMs, lap.Elapsed.TotalMilliseconds);
                            lap.Restart();
                            await PingAll(clients, stopping.Token).ConfigureAwait(false);
                            driverLongestLapMs = Math.Max(driverLongestLapMs, lap.Elapsed.TotalMilliseconds);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // How the loop ends.
                    }
                });
            }

            for (var s = 0; s < 10; s++)
            {
                Thread.Sleep(2000);
                Sample("run");
            }

            stopping.Cancel();
            driver?.Wait(TimeSpan.FromSeconds(10));

            handshakeMs.Sort();
            var final = runtime.ReadStats(tickRateHz * 10);
            log.WriteLine($"LOSS bots={bots} mode={mode} concurrency={connectConcurrency} creatures={creatures} hz={tickRateHz} workers={workers}");
            log.WriteLine($"  handshake ms p50 {Pct(handshakeMs, 0.50):F1} p99 {Pct(handshakeMs, 0.99):F1} max {Pct(handshakeMs, 1.0):F1}  "
                + $"failed {failedConnects}");
            log.WriteLine($"  driver longest gap {driverLongestGapMs:F0} ms, longest lap {driverLongestLapMs:F0} ms (silence bound 750 ms)");
            log.WriteLine($"  server closes: silent {table.ClosesBy(SessionCloseReason.Unacknowledged)} lagging {table.ClosesBy(SessionCloseReason.Lagging)} "
                + $"linkLost {table.ClosesBy(SessionCloseReason.LinkLost)} protocol {table.ClosesBy(SessionCloseReason.ProtocolError)} "
                + $"abusive {table.ClosesBy(SessionCloseReason.Abusive)} left {table.ClosesBy(SessionCloseReason.ClientLeft)}  "
                + $"degraded {frames.SessionsDegraded}");
            log.WriteLine($"  skips: gated {frames.SkipsBy(FrameSkipCause.Gated)} lagging {frames.SkipsBy(FrameSkipCause.Lagging)} "
                + $"inFlightFull {frames.SkipsBy(FrameSkipCause.InFlightFull)} oversize {frames.SkipsBy(FrameSkipCause.Oversize)} "
                + $"poolRefused {frames.SkipsBy(FrameSkipCause.PoolRefused)} owedReset {frames.SkipsBy(FrameSkipCause.OwedReset)}  "
                + $"produced {frames.FramesProduced}");
            log.WriteLine($"  tick p50 {final.TickP50Ms:F2} p99 {final.TickP99Ms:F2} ms (period {1000.0 / tickRateHz:F1}), "
                + $"longestSkipRun {frames.LongestSkipRun}");
            lock (disconnects)
            {
                foreach (var (code, count) in disconnects)
                {
                    log.WriteLine($"  client saw {code}: {count}");
                }
            }

            if (mode == PingMode.Own)
            {
                Assert.That(table.ClosesBy(SessionCloseReason.Unacknowledged), Is.Zero,
                    "sessions whose clients ping on their own loop were closed for silence: the server did not hear PINGs that were sent");
            }
        }
        finally
        {
            stopping.Cancel();
            foreach (var client in clients)
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }

            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            runtime.Shutdown();
        }

        async Task ConnectAsync(TyphonClient client)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await client.ConnectAsync().ConfigureAwait(false);
                lock (clients)
                {
                    clients.Add(client);
                    handshakeMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failedConnects);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static async Task PingAll(List<TyphonClient> clients, CancellationToken ct)
    {
        TyphonClient[] snapshot;
        lock (clients)
        {
            snapshot = clients.ToArray();
        }

        foreach (var client in snapshot)
        {
            if (!client.IsConnected)
            {
                continue;
            }

            try
            {
                await client.SendPingAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Counted by the disconnect handler.
            }
        }
    }

    private static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * (sorted.Count - 1)))];

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, int tickRateHz, int workers)
        => TyphonRuntime.Create(dbe, schedule =>
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
        dag.CallbackSystem("MoveCreatures", ctx => MoveCreatures(ctx));
    }, new RuntimeOptions
    {
        WorkerCount = workers,
        BaseTickRate = tickRateHz,
        Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
    });

    /// <summary>A per-slot sine on every creature, so every session is owed a frame every tick (see BotSwarmSmokeTests.MoveCreatures).</summary>
    private static void MoveCreatures(in TickContext ctx)
    {
        var tx = ctx.Transaction;
        if (tx == null)
        {
            return;
        }

        var phase = ctx.TickNumber * 0.37f;
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var bounds = cluster.GetSpan(ProjCreature.Bounds);
#pragma warning restore TYPHON009
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var width = bounds[slot].Bounds.MaxX - bounds[slot].Bounds.MinX;
                var minX = (slot * 9f) + (8f * MathF.Sin(phase + slot));
                var current = bounds[slot];
                current.Bounds.MinX = minX;
                current.Bounds.MaxX = minX + width;
                cluster.WriteSpatial(ProjCreature.Bounds, slot, current);
            }
        }
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds("god");
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe, int creatures)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < creatures; i++)
            {
                var bounds = new ProjBounds
                {
                    Bounds = new AABB2F { MinX = (i % 800) * 9f, MinY = 10f + (i / 800) * 9f, MaxX = ((i % 800) * 9f) + 1f, MaxY = 11f + (i / 800) * 9f },
                    Speed = 1f,
                };

                var ai = new ProjAi { Template = (byte)(i + 1), Level = (ushort)(10 + i), Mode = ProjAiMode.Idle };
                var vitals = new ProjVitals { Health = 8, MaxHealth = 10 };
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }
}
