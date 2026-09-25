using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// P1-22's smoke criterion: fifty sessions against one in-process server, driven for two hundred ticks, with no unexpected disconnect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fifty clients, one timer, and that shape is the thing under test.</b> The load generator's purpose is to measure the server, which only works while the
/// generator is not itself the expensive part — so the clients run with <c>PingHz = 0</c> and one loop here drives the ping cadence the server's lag skip
/// depends on. A test that gave each client its own timer would pass while proving the opposite of what M1-1 needs.
/// </para>
/// <para>
/// <b>Zero UNEXPECTED disconnects, counted by close code.</b> "No disconnects" is too weak a criterion to be useful and too strong to be honest: what matters
/// is that nobody was shed under load (1013), nobody starved of pings (4001), and nobody was refused. The assertion therefore reads the codes rather than a
/// count, because each one names a different defect and a bare number names none of them.
/// </para>
/// <para>
/// <b>The bot logic is duplicated here rather than referenced.</b> <c>demo/SwgTatooine.Bots</c> is a demo, and a test project referencing a demo would put
/// the demo into the engine suite's build closure — which <c>scripts/check-gate-filters.py</c> checks and which would make every gate run rebuild it. What is
/// under test is that fifty concurrent <c>TyphonClient</c> sessions survive, and that needs the client, not the demo's wrapper around it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class BotSwarmSmokeTests : TestBase<BotSwarmSmokeTests>
{
    private const int Bots = 50;
    private const int TickRateHz = 100;
    private const int DrivenTicks = 200;
    private const int CreatureCount = 40;
    private const string Profile = "god-world";

    /// <summary>
    /// A session that stops talking is closed with 4001, and <b>its client is told</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half that matters is the second one. A server that stops serving a silent session and leaves its socket open produces the worst failure a client
    /// can have: everything it can see says it is connected — the socket is open, no close code arrived, no error was raised — and no data ever comes again.
    /// It cannot even reconnect, because nothing told it to.
    /// </para>
    /// <para>
    /// <b>Why this fixture exists at all.</b> The first 110-session load run showed ninety sessions frozen at eight frames each while the generator reported
    /// them all healthy and connected. That was the generator's own fault — it connected a hundred and ten sessions over a two-second ramp without pinging
    /// any of them, and 3 s / <c>PingHz</c> = 750 ms of silence is exactly eight ticks at 10 Hz, so the server was right to drop them. What the run could not
    /// answer is whether the clients were ever told, because a load generator that manufactures the silence cannot also be trusted about the consequence.
    /// This asks the question directly.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-14")]
    public void ASilentSessionIsClosedAndItsClientIsTold()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        // PingHz 30 puts the silence bound at 3 s / 30 = 100 ms, which is ten ticks here — long enough to be the real policy, short enough to be a test.
        using var runtime = CreateRuntime(dbe, new SubscriptionsOptions { PingHz = 30, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) });
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        ushort closeCode = 0;
        var closed = new ManualResetEventSlim(false);

        var client = new TyphonClient(new ClientOptions
        {
            Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"),
            Kind = "god",

            // The point of the test: this client never pings.
            PingHz = 0,
            Reconnect = false,
            HandshakeTimeout = TimeSpan.FromSeconds(15),
        });

        client.Disconnected += (code, _) =>
        {
            closeCode = code;
            closed.Set();
        };

        try
        {
            Assert.That(client.ConnectAsync().Wait(TimeSpan.FromSeconds(20)), Is.True, "the client did not complete its handshake");

            // Well past the silence bound, then a second window, so "was it dropped" and "was it told" are two separate readings rather than one inference.
            // A wait, written as one. `Is.True.Or.False` is satisfied by every bool, so dressing this as an assertion only taught the next reader that
            // such a thing is worth writing.
            closed.Wait(TimeSpan.FromSeconds(3));
            var framesAfterBound = client.Store?.Frames ?? 0;
            Thread.Sleep(1000);
            var framesLater = client.Store?.Frames ?? 0;

            Assert.Multiple(() =>
            {
                Assert.That(framesLater, Is.EqualTo(framesAfterBound), "the session is still being served, so the silence policy did not drop it and this "
                    + "fixture is measuring something else");
                Assert.That(closed.IsSet, Is.True,
                    "the session was dropped for silence and its client was NEVER TOLD: the socket is still open, no close code arrived, and no frame will "
                    + "ever come again. A client cannot even reconnect from this state, because nothing told it to.");
                Assert.That(closeCode, Is.EqualTo(CloseCodes.NoAcknowledgement),
                    "a session dropped for silence must close 4001, which is what tells an SDK to reconnect rather than to back off as 1013 does");
                Assert.That(client.IsConnected, Is.False, "the client still believes it is connected");
            });
        }
        finally
        {
            client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            runtime.Shutdown();
            closed.Dispose();
        }
    }

    /// <summary>Fifty sessions open, stay open for two hundred server ticks, and receive a world.</summary>
    [Test]
    public void FiftyBotsSurviveTwoHundredTicks()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        using var runtime = CreateRuntime(dbe);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        var clients = new List<TyphonClient>(Bots);
        var disconnects = new Dictionary<ushort, int>();
        var faults = 0;

        try
        {
            for (var i = 0; i < Bots; i++)
            {
                var client = new TyphonClient(new ClientOptions
                {
                    Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"),
                    Kind = "god",

                    // The swarm drives the cadence from one loop; see the class remarks.
                    PingHz = 0,
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

                client.Fault += _ => Interlocked.Increment(ref faults);

                Assert.That(client.ConnectAsync().Wait(TimeSpan.FromSeconds(20)), Is.True, $"bot {i} did not complete its handshake");
                clients.Add(client);
            }



            // One driver for all fifty, pinging at the catalog's rate while the server runs its two hundred ticks.
            var target = runtime.CurrentTickNumber + DrivenTicks;
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var driver = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                try
                {
                    while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                    {
                        foreach (var client in clients)
                        {
                            try
                            {
                                await client.SendPingAsync(stopping.Token).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // One socket going away must not stop the other forty-nine being driven; the disconnect handler counts it.
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopping is how this loop ends. Letting the cancellation out would fault the task and make the Wait below throw during a teardown that
                    // is going exactly as intended — which is how the first version of this test failed.
                }
            });

            var ticked = SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(60));
            stopping.Cancel();
            driver.Wait(TimeSpan.FromSeconds(5));

            var creatures = clients[0].Plan.ArchetypeByName(nameof(ProjCreature)).Idx;
            var received = 0;
            var anomalies = 0L;
            foreach (var client in clients)
            {
                if (client.Store != null && client.Store.Archetypes[creatures].LiveCount > 0)
                {
                    received++;
                }

                anomalies += client.Store?.Anomalies ?? 0;
            }

            var stillOpen = 0;
            foreach (var client in clients)
            {
                if (client.IsConnected)
                {
                    stillOpen++;
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(ticked, Is.True, $"the server did not reach {DrivenTicks} ticks under fifty sessions");
                Assert.That(Unexpected(disconnects), Is.Empty, "sessions were lost: " + Describe(disconnects));
                Assert.That(stillOpen, Is.EqualTo(Bots), $"only {stillOpen} of {Bots} sessions were still open at the end");
                Assert.That(received, Is.EqualTo(Bots), $"only {received} of {Bots} bots decoded any entity, so some sessions were open and starving");
                Assert.That(anomalies, Is.Zero, "a client could not apply something the server sent it");
                Assert.That(faults, Is.Zero, "a receive loop faulted");
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

    /// <summary>
    /// How long a HEALTHY session actually stalls, measured, so that the close bound is argued from behaviour rather than chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The question the close bound cannot answer about itself.</b> "How long may a client stall before we give up on it" is circular if it is asked of
    /// the bound: a session survives a stall exactly when the stall is shorter than the bound. The non-circular question is what stalls occur in a client
    /// that is <i>working</i> — a garbage collection, a frame that took long to apply, scheduler jitter, a socket that backed up for a moment — and the
    /// bound has to sit above the tail of that. This runs the swarm with the close bound set out of reach, so nothing is ever closed and every session's
    /// natural skip run is free to show itself, and reads the high-water mark back.
    /// </para>
    /// <para>
    /// It asserts the headroom rather than a number: a measurement that hard-codes what it measured stops being a measurement the first time the machine
    /// changes. What must hold is that the default bound has room over what healthy sessions do, and the failure message carries the figures so a run that
    /// gets close is legible rather than merely red.
    /// </para>
    /// </remarks>
    [Test]
    [TestCase(10, 60)]
    [TestCase(50, 60)]
    [TestCase(50, 10)]
    [TestCase(110, 60)]
    public void TheNaturalSkipRunOfHealthySessionsStaysFarBelowTheCloseBound(int bots, int tickRateHz)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        // Out of reach on purpose: nothing may be closed, so every session's run is free to climb to whatever the machine and the load make it.
        var options = new SubscriptionsOptions
        {
            CloseStalledAfter = TimeSpan.FromMinutes(10), ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
        };
        using var runtime = CreateRuntime(dbe, options, tickRateHz, moving: true);
        Declare(runtime.Subscriptions);
        runtime.Start();

        var transport = new TcpSubscriptionTransport(null);
        runtime.StartSubscriptionTransport(transport);

        var clients = new List<TyphonClient>(bots);
        var disconnects = new Dictionary<ushort, int>();

        try
        {
            for (var i = 0; i < bots; i++)
            {
                var client = new TyphonClient(new ClientOptions
                {
                    Endpoint = new Uri($"tcp://127.0.0.1:{transport.BoundEndPoint.Port}/"),
                    Kind = "god",
                    PingHz = 0,
                    Reconnect = false,
                    HandshakeTimeout = TimeSpan.FromSeconds(20),
                });

                client.Disconnected += (code, _) =>
                {
                    lock (disconnects)
                    {
                        disconnects[code] = disconnects.GetValueOrDefault(code) + 1;
                    }
                };

                Assert.That(client.ConnectAsync().Wait(TimeSpan.FromSeconds(20)), Is.True, $"bot {i} did not complete its handshake");
                clients.Add(client);
            }

            var target = runtime.CurrentTickNumber + DrivenTicks;
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var driver = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                try
                {
                    while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                    {
                        foreach (var client in clients)
                        {
                            try
                            {
                                await client.SendPingAsync(stopping.Token).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Counted by the disconnect handler; one bad socket must not stop the others being driven.
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // How the loop ends.
                }
            });

            var ticked = SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= target, TimeSpan.FromSeconds(90));
            stopping.Cancel();
            driver.Wait(TimeSpan.FromSeconds(5));

            var frames = runtime.SubscriptionsContextForTest.Subscriptions.Frames;
            var longest = frames.LongestSkipRun;
            var periodUs = (int)Math.Round(1_000_000.0 / tickRateHz, MidpointRounding.AwayFromZero);
            var closeBound = SkipPolicy.CloseBoundTicks(new SubscriptionsOptions(), (uint)periodUs);

            // The MINIMUM across the population, not client zero. This fixture's own remarks describe "ninety sessions frozen at eight frames each while
            // the generator reported them all healthy" — and a guard that reads one client is blind to exactly that: client zero being served while the
            // other hundred and nine starve satisfies it.
            var clientFrames = long.MaxValue;
            var clientRecords = long.MaxValue;
            foreach (var client in clients)
            {
                clientFrames = Math.Min(clientFrames, client.Store?.Frames ?? 0);
                clientRecords = Math.Min(clientRecords, client.Store?.Records ?? 0);
            }

            TestContext.Out.WriteLine(
                $"STALL bots={bots} hz={tickRateHz} ticks={DrivenTicks} longestSkipRun={longest} ({longest * periodUs / 1000.0:F1} ms) "
                + $"defaultCloseBound={closeBound} ticks ({closeBound * periodUs / 1000.0:F1} ms) degraded={frames.SessionsDegraded} "
                + $"closedLagging={frames.SessionsClosedLagging} closedSilent={frames.SessionsClosedSilent} "
                + $"clientFrames={clientFrames} clientRecords={clientRecords}");

            Assert.Multiple(() =>
            {
                Assert.That(ticked, Is.True, $"the server did not reach {DrivenTicks} ticks under {bots} sessions");

                // THE GUARD THAT MAKES THE REST MEAN ANYTHING. A skip run of zero is the answer both to "nothing was ever denied" and to "nothing was ever
                // asked for", and only the second is a broken measurement. A world that moved every tick owes each session a frame every tick, so a run that
                // delivered a handful was measuring an idle server and its headroom figure is worth nothing.
                Assert.That(clientFrames, Is.GreaterThan(DrivenTicks / 2),
                    $"only {clientFrames} frames reached a client over {DrivenTicks} moving ticks, so this run measured an idle server rather than a loaded "
                    + "one and its skip-run figure proves nothing");
                Assert.That(clientRecords, Is.GreaterThan((long)DrivenTicks * CreatureCount / 2),
                    $"only {clientRecords} records reached a client, against the {(long)DrivenTicks * CreatureCount} a moving world owes it");

                Assert.That(frames.SessionsClosedLagging, Is.Zero, "the bound was put out of reach, so a closure here means something other than the bound");
                Assert.That(Unexpected(disconnects), Is.Empty, "sessions were lost: " + Describe(disconnects));
                Assert.That(longest, Is.LessThan(closeBound),
                    $"a healthy session's own skip run reached {longest} ticks against a default close bound of {closeBound}: the default would have closed "
                    + "clients that were doing nothing wrong");
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

    /// <summary>Every close code is unexpected here except the ones the teardown itself causes.</summary>
    private static List<string> Unexpected(Dictionary<ushort, int> disconnects)
    {
        var findings = new List<string>();
        lock (disconnects)
        {
            foreach (var (code, count) in disconnects)
            {
                findings.Add(code switch
                {
                    CloseCodes.TryAgainLater => $"{count} shed under load (1013)",
                    CloseCodes.NoAcknowledgement => $"{count} starved of pings (4001) — the shared driver is not keeping up",
                    0 => $"{count} never completed a handshake",
                    _ => $"{count} closed with {code}",
                });
            }
        }

        return findings;
    }

    private static string Describe(Dictionary<ushort, int> disconnects) => string.Join(", ", Unexpected(disconnects));

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, SubscriptionsOptions subscriptions = null, int tickRateHz = TickRateHz,
        bool moving = false)
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

        if (moving)
        {
            dag.CallbackSystem("MoveCreatures", ctx => MoveCreatures(ctx));
        }
    }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = tickRateHz, Subscriptions = subscriptions
            ?? new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
    });

    /// <summary>
    /// Nudges every creature, so that every session has something to send on every tick.
    /// </summary>
    /// <param name="ctx">The tick.</param>
    /// <remarks>
    /// <b>A static world measures nothing about back-pressure.</b> A session is skipped when the engine denies it a frame it had something to put in, and a
    /// world that never changes gives it nothing to put in — so the skip run stays at zero however many sessions are connected, and a sweep over it reports
    /// that the close bound has infinite headroom. The movement is what puts frames on the wire every tick and makes the K slots the binding constraint they
    /// are in a real game.
    /// </remarks>
    private static void MoveCreatures(in TickContext ctx)
    {
        var tx = ctx.Transaction;
        if (tx == null)
        {
            return;
        }

        // NOT a straight line. Constant velocity is the one trajectory a motion segment predicts exactly, so a linear mover makes the server correctly
        // send nothing and the measurement reports an idle world under the name of a loaded one — which is what the first version of this did at 10 Hz.
        // A per-slot sine is cheap and is new information every tick at any tick rate.
        var phase = ctx.TickNumber * 0.37f;
        // NOT disposed: this accessor comes from the TICK's transaction, which owns it. Disposing one taken from a transaction this method did not
        // create releases the cached EntityMap and chunk accessors out from under the rest of the tick — it silently stopped this mover writing, and
        // the fixture's own "did a client actually receive frames" guard is what caught it.
        var accessor = tx.For<ProjCreature>();
        {
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

                    // WriteSpatial, not a write through the span. TYPHON009 exists to catch a spatial field mutated any other way, and suppressing it
                    // around a WRITE silences the one guard that would object: the cluster's AABB and the spatial index never learn of the move. It happened
                    // to work here only because this profile is World() and interest never consults the index.
                    var width = bounds[slot].Bounds.MaxX - bounds[slot].Bounds.MinX;
                    var minX = (slot * 9f) + (8f * MathF.Sin(phase + slot));
                    var current = bounds[slot];
                    current.Bounds.MinX = minX;
                    current.Bounds.MaxX = minX + width;
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, current);
                }
            }
        }
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        // The session cap lives on SubscriptionsOptions and defaults to 8192, so fifty needs no configuration; saying so here stops the next
        // reader assuming the default is small and adding a knob that changes nothing.
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
                    Bounds = new AABB2F { MinX = i * 9f, MinY = 10f, MaxX = (i * 9f) + 1f, MaxY = 11f },
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
