using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>A command only a player may send: a spectator sending it is refused by role.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PlayerOnly
{
    public ushort Value;
}

/// <summary>
/// The inbound rails (design/Subscriptions/11 § 4.2) through the real runtime: each session's byte budget — a <c>COMMANDS</c> message over it refused
/// whole, each command acknowledged <c>RATE_LIMITED</c> so the client's <c>lastSeq</c> still settles it — every other refusal acknowledged too, each
/// session's share of the tick's acknowledgement log, and the rule that closes a session whose commands keep being refused (1008). The clock is the
/// test's, so none of it depends on the machine's timing.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class IngressHardeningTests : TestBase<IngressHardeningTests>
{
    private long _tick;
    private long _now;

    private FrameHarness Harness(DatabaseEngine dbe, string name, int budget, int moveRate = 1_000_000, TimeSpan abuseWindow = default, int abuseRefusals = 64,
        int abuseWindows = 3, int clientMessageBytes = 256)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p => p.World().Of<ProjCreature>());
            subs.Command<FuzzMove>(c => c
                .Roles(SessionRole.Spectator, SessionRole.Player)
                .Rate(moveRate, moveRate)
                .Precheck((in FuzzMove m) => m.Speed != 999)
                .Field(m => m.X, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Z, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Speed, Codec.U16));
            subs.Command<PlayerOnly>(c => c
                .Roles(SessionRole.Player)
                .Rate(1_000_000, 1_000_000)
                .Field(m => m.Value, Codec.U16));
        }, name, new SubscriptionsOptions
        {
            MaxSessions = 8,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
            IngressBytesPerSecond = budget,
            ClientMessageBytes = clientMessageBytes,
            AbuseWindow = abuseWindow == default ? TimeSpan.FromSeconds(1) : abuseWindow,
            AbuseRefusalsPerWindow = abuseRefusals,
            AbuseWindows = abuseWindows,
        });
        harness.RunFence = true;
        harness.RunIngress = true;
        _tick = 0;
        _now = 1_000_000;
        return harness;
    }

    private (SubscriptionConnection Connection, InProcessLink Link) Connect(FrameHarness harness)
    {
        var host = (ISubscriptionsHost)harness.Subscriptions;
        var link = new InProcessLink();
        var connection = new SubscriptionConnection(host, link, new LinkInfo { Transport = "test", SubProtocol = ProtocolConstants.WebSocketSubprotocol },
            Timeout.InfiniteTimeSpan) { Clock = () => _now };
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god", clientCatalogHash: host.CatalogHash));
        Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
        return (connection, link);
    }

    private void Advance(TimeSpan by) => _now += (long)(by.TotalSeconds * Stopwatch.Frequency);

    private static byte[] Moves(CatalogPlan plan, ushort firstSeq, int count, int speed = 3)
    {
        var list = new List<(MessagePlan, ushort, RecordValues)>();
        for (var i = 0; i < count; i++)
        {
            list.Add((plan.CommandByName(nameof(FuzzMove)), (ushort)(firstSeq + i), new RecordValues
            {
                ["X"] = FieldValue.Of(1.0), ["Z"] = FieldValue.Of(2.0), ["Speed"] = FieldValue.Of(speed),
            }));
        }

        return Encode(list);
    }

    private static byte[] PlayerOnlyCommands(CatalogPlan plan, ushort firstSeq, int count)
    {
        var list = new List<(MessagePlan, ushort, RecordValues)>();
        for (var i = 0; i < count; i++)
        {
            list.Add((plan.CommandByName(nameof(PlayerOnly)), (ushort)(firstSeq + i), new RecordValues { ["Value"] = FieldValue.Of(7) }));
        }

        return Encode(list);
    }

    private static byte[] Encode(List<(MessagePlan, ushort, RecordValues)> list)
    {
        var buffer = new byte[4096];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, 1, list);
        return writer.Written.ToArray();
    }

    private static List<FrameLog> Drain(FrameHarness harness, SessionId session)
    {
        var frames = new List<FrameLog>();
        while (harness.Read(session) is { } log)
        {
            frames.Add(log);
        }

        return frames;
    }

    private static List<byte[]> Sent(InProcessLink link)
    {
        var sent = new List<byte[]>();
        while (link.TryTake(out var message, 0))
        {
            sent.Add(message);
        }

        return sent;
    }

    /// <summary>
    /// A session sending past its budget has the excess refused whole — every command of a refused message acknowledged RATE_LIMITED — while its lastSeq
    /// still settles them; a session within its budget beside it gets no acknowledgement, and neither is closed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void AMessageOverTheBudgetIsRefusedWholeAndAcknowledged()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        const int budget = 256;
        using var harness = Harness(dbe, nameof(AMessageOverTheBudgetIsRefusedWholeAndAcknowledged), budget);
        var flood = Connect(harness);
        var calm = Connect(harness);
        harness.RunTick(++_tick);
        Drain(harness, flood.Connection.Session);
        Drain(harness, calm.Connection.Session);

        // The bucket starts full and the clock does not move: exactly the whole messages that fit pass, the rest of the burst is refused.
        var size = Moves(harness.CatalogPlan, 1, 2).Length;
        var passing = budget / size;
        var burst = passing + 3;
        ushort seq = 1;
        for (var i = 0; i < burst; i++)
        {
            flood.Connection.OnMessage(Moves(harness.CatalogPlan, seq, 2));
            seq += 2;
        }

        calm.Connection.OnMessage(Moves(harness.CatalogPlan, 1, 2));
        harness.RunTick(++_tick);
        var floodFrames = Drain(harness, flood.Connection.Session);
        var calmFrames = Drain(harness, calm.Connection.Session);

        var acks = floodFrames.SelectMany(f => f.Acks).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(passing, Is.GreaterThan(0), "the fixture must pass some messages");
            Assert.That(flood.Link.IsClosed || calm.Link.IsClosed, Is.False, "refused, not closed");
            Assert.That(acks, Has.Length.EqualTo((burst - passing) * 2), "every command of every refused message acknowledged");
            Assert.That(acks.All(a => a.Reason == AckReasons.RateLimited), Is.True);
            Assert.That(acks.Select(a => a.Seq).Min(), Is.EqualTo((ushort)(1 + (passing * 2))), "the refused ones are the burst's tail");
            Assert.That(floodFrames.SelectMany(f => f.Selves).Last().LastSeq, Is.EqualTo((ushort)(burst * 2)), "lastSeq settles them all");
            Assert.That(calmFrames.SelectMany(f => f.Acks), Is.Empty, "the calm session is untouched");
        });
    }

    /// <summary>
    /// A refusal by role and one by the declaration's pre-check are acknowledged, each with its own reason (SUB-27: every refusal is answered).
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void RoleAndPrecheckRefusalsAreAcknowledged()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(RoleAndPrecheckRefusalsAreAcknowledged), TestIngress.Budget, clientMessageBytes: 1024);
        var spectator = Connect(harness);
        harness.RunTick(++_tick);
        Drain(harness, spectator.Connection.Session);

        spectator.Connection.OnMessage(PlayerOnlyCommands(harness.CatalogPlan, 1, 1));
        spectator.Connection.OnMessage(Moves(harness.CatalogPlan, 2, 1, speed: 999));
        spectator.Connection.OnMessage(Moves(harness.CatalogPlan, 3, 1));
        harness.RunTick(++_tick);
        var frames = Drain(harness, spectator.Connection.Session);
        var acks = frames.SelectMany(f => f.Acks).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(acks, Is.EquivalentTo(new[] { ((ushort)1, AckReasons.Forbidden), ((ushort)2, AckReasons.Rejected) }));
            Assert.That(frames.SelectMany(f => f.Selves).Last().LastSeq, Is.EqualTo((ushort)3));
            Assert.That(spectator.Link.IsClosed, Is.False);
        });
    }

    /// <summary>
    /// One session refused far more commands in a tick than its share of the acknowledgement log gets its share acknowledged and the rest settled by
    /// lastSeq and counted; another session's refusal in the same tick is still acknowledged.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void OneSessionsRefusalsCannotCrowdOutAnothersAcknowledgements()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = Harness(dbe, nameof(OneSessionsRefusalsCannotCrowdOutAnothersAcknowledgements), TestIngress.Budget, moveRate: 1,
            abuseRefusals: 1_000_000, clientMessageBytes: 4096);
        var flood = Connect(harness);
        var other = Connect(harness);
        harness.RunTick(++_tick);
        Drain(harness, flood.Connection.Session);
        Drain(harness, other.Connection.Session);

        // A rate of one a second: the first command passes, every later one is refused.
        const int count = 100;
        flood.Connection.OnMessage(Moves(harness.CatalogPlan, 1, count));
        other.Connection.OnMessage(Moves(harness.CatalogPlan, 1, 2));
        harness.RunTick(++_tick);
        var floodFrames = Drain(harness, flood.Connection.Session);
        var otherAcks = Drain(harness, other.Connection.Session).SelectMany(f => f.Acks).ToArray();
        var row = harness.Subscriptions.Ingress.RowOf(flood.Connection.Session);

        Assert.Multiple(() =>
        {
            Assert.That(floodFrames.SelectMany(f => f.Acks).Count(), Is.EqualTo(SubscriptionsIngress.RefusalAcksPerTick), "its share, no more");
            Assert.That(row.RefusalAcksCapped, Is.EqualTo(count - 1 - SubscriptionsIngress.RefusalAcksPerTick), "the rest counted");
            Assert.That(floodFrames.SelectMany(f => f.Selves).Last().LastSeq, Is.EqualTo((ushort)count), "and settled by lastSeq");
            Assert.That(otherAcks, Is.EqualTo(new[] { ((ushort)2, AckReasons.RateLimited) }), "the other session's refusal still acknowledged");
        });
    }

    /// <summary>
    /// A session whose commands keep being refused — here, over its budget — for the configured run of windows is closed with 1008 at the last of them and
    /// not before, its KICK sent by its send pump; one refused for a single window and then well-behaved is not.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void SustainedRefusalsCloseTheSessionWith1008()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var window = TimeSpan.FromSeconds(1);
        using var harness = Harness(dbe, nameof(SustainedRefusalsCloseTheSessionWith1008), budget: 256, abuseWindow: window, abuseRefusals: 3, abuseWindows: 3);
        var brief = Connect(harness);
        var abuser = Connect(harness);
        harness.RunTick(++_tick);

        // A burst far past the budget (the clock is still), so each yields refusals beyond the threshold.
        void Flood((SubscriptionConnection Connection, InProcessLink Link) s, ref ushort seq)
        {
            for (var i = 0; i < 20; i++)
            {
                s.Connection.OnMessage(Moves(harness.CatalogPlan, seq, 2));
                seq += 2;
            }
        }

        ushort abuserSeq = 1;
        ushort briefSeq = 1;
        var closedAfter = new List<bool>();
        Flood(brief, ref briefSeq);
        for (var w = 0; w < 3; w++)
        {
            Flood(abuser, ref abuserSeq);
            Advance(window);

            // The first message of the next window is what evaluates the window that just ended.
            abuser.Connection.OnMessage(Moves(harness.CatalogPlan, abuserSeq++, 1));
            brief.Connection.OnMessage(Moves(harness.CatalogPlan, briefSeq++, 1));
            closedAfter.Add(abuser.Connection.State == SubscriptionConnectionState.Closed);
        }

        harness.RunTick(++_tick);
        var deadline = Environment.TickCount64 + 5000;
        while (!abuser.Link.IsClosed && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        var sent = Sent(abuser.Link);
        Assert.Multiple(() =>
        {
            Assert.That(closedAfter, Is.EqualTo(new[] { false, false, true }), "closed at the third abusive window, not before");
            Assert.That(abuser.Link.IsClosed, Is.True);
            Assert.That(abuser.Link.CloseCode, Is.EqualTo(CloseCodes.PolicyViolation));
            Assert.That(abuser.Link.CloseCode, Is.EqualTo(1008));
            Assert.That(sent.LastOrDefault()?[0], Is.EqualTo(MessageTypes.Kick), "told why first");
            Assert.That(brief.Connection.State, Is.EqualTo(SubscriptionConnectionState.Open), "one abusive window is not sustained abuse");
        });
    }

    /// <summary>
    /// Q6 and the rails' own sanity: Start refuses a catalog with commands and no budget, a budget below the message cap, and an abuse rule off.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void RailsThatWouldNotHoldAreRefusedAtStart()
    {
        static string Refusal(SubscriptionsOptions options, int commandTypes = 1)
        {
            try
            {
                SubscriptionsRuntime.ValidateIngressRails(options, commandTypes);
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(Refusal(new SubscriptionsOptions()), Does.Contain(nameof(SubscriptionsOptions.IngressBytesPerSecond)));
            Assert.That(Refusal(new SubscriptionsOptions(), commandTypes: 0), Is.Null, "no commands, no budget needed");
            Assert.That(Refusal(new SubscriptionsOptions { IngressBytesPerSecond = 512, ClientMessageBytes = 1024 }),
                Does.Contain(nameof(SubscriptionsOptions.ClientMessageBytes)));
            Assert.That(Refusal(new SubscriptionsOptions { IngressBytesPerSecond = 4096, AbuseWindow = TimeSpan.Zero }),
                Does.Contain(nameof(SubscriptionsOptions.AbuseWindow)));
            Assert.That(Refusal(new SubscriptionsOptions { IngressBytesPerSecond = 4096, AbuseWindows = 0 }),
                Does.Contain(nameof(SubscriptionsOptions.AbuseWindows)));
            Assert.That(Refusal(new SubscriptionsOptions { IngressBytesPerSecond = 4096, AbuseRefusalsPerWindow = -1 }),
                Does.Contain(nameof(SubscriptionsOptions.AbuseRefusalsPerWindow)));
            Assert.That(Refusal(new SubscriptionsOptions { IngressBytesPerSecond = 4096 }), Is.Null);
        });

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var ex = Assert.Catch(() => Harness(dbe, nameof(RailsThatWouldNotHoldAreRefusedAtStart), budget: 0).Dispose());
        Assert.That(ex?.ToString(), Does.Contain(nameof(SubscriptionsOptions.IngressBytesPerSecond)), "and Start is where it is checked");
    }
}

/// <summary>
/// The connection's half of the inbound rails, against a fake host and the test's clock: what is charged, what is refused, what the abuse rule counts, and
/// when a run of abusive windows breaks.
/// </summary>
[TestFixture]
class IngressRailsTests
{
    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private SessionTable _table;
    private FakeSubscriptionsHost _host;
    private InProcessLink _link;
    private long _now;
    private static readonly long Second = Stopwatch.Frequency;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "IngressRailsTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "IngressRailsTestAllocator" });
        _sessions = new SubscriptionsSessions();
        _table = new SessionTable("Sessions", _registry.Runtime, _allocator, new SubscriptionsOptions { MaxSessions = 4 }, _sessions.SessionEvents);
        _host = new FakeSubscriptionsHost
        {
            Sessions = _sessions,
            SessionTable = _table,
            IngressPolicy = new IngressPolicy(BytesPerSecond: 100, WindowTicks: Second, RefusalsPerWindow: 3, Windows: 2),
        };
        _link = new InProcessLink();
        _now = 1_000_000;
    }

    [TearDown]
    public void TearDown()
    {
        _table?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private SubscriptionConnection Open()
    {
        var connection = new SubscriptionConnection(_host, _link, new LinkInfo { Transport = "fake" }, Timeout.InfiniteTimeSpan) { Clock = () => _now };
        _link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello());
        _link.Take();
        Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open));
        return connection;
    }

    /// <summary>The bucket is one second deep; every message is charged, and only a COMMANDS message is ever refused.</summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void EverythingIsChargedAndOnlyCommandsAreRefused()
    {
        var connection = Open();

        connection.OnMessage(ClientMessages.Commands(60));
        connection.OnMessage(ClientMessages.Commands(60));
        var afterBurst = (_host.CommandMessages, _host.RefusedMessages);

        // 40 bytes left; five PINGs 40 ms apart, each refilling 4 bytes and costing its own length.
        for (var i = 0; i < 5; i++)
        {
            _now += Second / 25;
            connection.OnMessage(ClientMessages.Ping(1, 1));
        }

        var pingsAnswered = _host.PongRequests.Count;
        connection.OnMessage(ClientMessages.Commands(60));

        Assert.Multiple(() =>
        {
            Assert.That(afterBurst, Is.EqualTo((1, 1)), "the second 60 bytes did not fit the 40 left");
            Assert.That(pingsAnswered, Is.EqualTo(5), "a PING is never refused");
            Assert.That(_host.CommandMessages, Is.EqualTo(1), "the pings were charged: 40 + 20 refilled − 5 pings is under 60");
            Assert.That(_host.RefusedMessages, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// A refill loses no fraction of a byte: 1.5 bytes per step for 60 steps is 90 bytes, and a 90-byte message fits. (A bucket kept in whole bytes that
    /// restamps on every refill would have credited 60.)
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void ARefillInFractionalStepsLosesNothing()
    {
        var connection = Open();
        connection.OnMessage(ClientMessages.Commands(100));
        for (var i = 0; i < 60; i++)
        {
            // Refused (it is deeper than the bucket) and therefore not charged — but it makes the connection refill.
            _now += Second * 15 / 1000;
            connection.OnMessage(ClientMessages.Commands(101));
        }

        connection.OnMessage(ClientMessages.Commands(90));

        Assert.Multiple(() =>
        {
            Assert.That(_host.RefusedMessages, Is.EqualTo(60));
            Assert.That(_host.CommandMessages, Is.EqualTo(2), "90 bytes refilled in 1.5-byte steps");
        });
    }

    /// <summary>Over budget, a BYE still closes cleanly and an unknown type still closes with 1002: the budget never delays a close.</summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void OverBudgetAByeAndAProtocolErrorStillClose()
    {
        var connection = Open();
        connection.OnMessage(ClientMessages.Commands(100));
        connection.OnMessage([0xEE, 1, 2, 3]);

        Assert.Multiple(() =>
        {
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed));
            Assert.That(_link.CloseCode, Is.EqualTo(CloseCodes.ProtocolError));
        });

        var second = new InProcessLink();
        var other = new SubscriptionConnection(_host, second, new LinkInfo { Transport = "fake" }, Timeout.InfiniteTimeSpan) { Clock = () => _now };
        second.Connection = other;
        other.OnMessage(ClientMessages.Hello());
        second.Take();
        other.OnMessage(ClientMessages.Commands(100));
        other.OnMessage(ClientMessages.Bye());
        Assert.That(second.CloseCode, Is.EqualTo(CloseCodes.Normal));
    }

    /// <summary>
    /// The abuse rule counts refused commands — the connection's over-budget ones, per command, and the ingress's rate and role ones — and closes after
    /// the configured run of adjacent abusive windows.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void AbuseCountsCommandsFromBothSidesAndClosesAfterTheRun()
    {
        var connection = Open();
        _host.CommandsPerRefusedMessage = 4;

        // Window 1: one over-budget message of 4 commands — past the threshold of 3 on its own.
        connection.OnMessage(ClientMessages.Commands(100));
        connection.OnMessage(ClientMessages.Commands(10));
        _now += Second;

        // The first message of window 2 evaluates window 1; during window 2 nothing is refused here, but the ingress refuses 5 by rate or role.
        connection.OnMessage(ClientMessages.Ping(1, 1));
        var afterOne = connection.State;
        _host.PolicyRefusals = 5;
        _now += Second;
        connection.OnMessage(ClientMessages.Ping(1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(afterOne, Is.EqualTo(SubscriptionConnectionState.Open), "one abusive window");
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Closed), "two adjacent ones");
            Assert.That(_link.CloseCode, Is.EqualTo(CloseCodes.PolicyViolation));
        });
    }

    /// <summary>A gap of two windows or more between abusive windows breaks the run; so does a window at or under the threshold.</summary>
    [Test]
    [VerifiesRule("SUB-27")]
    public void AGapOrACalmWindowBreaksTheRun()
    {
        var connection = Open();
        _host.CommandsPerRefusedMessage = 4;

        // Window A: 4 refused, evaluated promptly — one abusive window.
        connection.OnMessage(ClientMessages.Commands(100));
        connection.OnMessage(ClientMessages.Commands(10));
        _now += Second;
        connection.OnMessage(ClientMessages.Ping(1, 1));

        // Window B: 4 refused, then silence for three windows — B is abusive, but too far from A to extend its run.
        connection.OnMessage(ClientMessages.Commands(100));
        _now += 3 * Second;
        connection.OnMessage(ClientMessages.Ping(1, 1));
        var afterGap = connection.State;

        // Window C: the ingress refuses exactly 3 — at the threshold, not past it.
        _host.PolicyRefusals = 3;
        _now += Second;
        connection.OnMessage(ClientMessages.Ping(1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(afterGap, Is.EqualTo(SubscriptionConnectionState.Open), "the gap broke the run");
            Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open), "a window at the threshold breaks it too");
        });
    }
}
