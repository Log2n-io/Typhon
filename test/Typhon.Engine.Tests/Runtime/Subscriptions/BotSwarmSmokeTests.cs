using NUnit.Framework;
using System;
using System.Collections.Generic;
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
        using var runtime = CreateRuntime(dbe, new SubscriptionsOptions { PingHz = 30 });
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
            Assert.That(closed.Wait(TimeSpan.FromSeconds(3)), Is.True.Or.False, "waiting, not asserting");
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

            Assert.That(clients, Has.Count.EqualTo(Bots), "not every bot opened a session");

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

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, SubscriptionsOptions subscriptions = null) => TyphonRuntime.Create(dbe, schedule =>
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
    }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = TickRateHz, Subscriptions = subscriptions ?? new SubscriptionsOptions() });

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
