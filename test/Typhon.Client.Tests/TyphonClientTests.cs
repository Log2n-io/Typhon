using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// #1363: a message the client could not apply ends the session. The store then holds part of a frame — a leave not applied, among others — and no later
/// frame repairs it, so carrying on left the server's removed entities in the store for good.
/// </summary>
[TestFixture]
public class TyphonClientTests
{
    private static readonly byte[] CatalogJson = GoldenFiles.ReadBin("catalog-kitchen-sink");
    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(CatalogJson));

    private static ArchetypePlan Beacon => Plan.ArchetypeByName("Beacon");

    /// <summary>
    /// An event handler that throws mid-frame refuses the stream with 1007 — <c>BYE 4004</c>, then the close — and the policy does not reconnect: the
    /// frames queued behind it are never applied over the partial store.
    /// </summary>
    [Test]
    public async Task AnEventHandlerThatThrowsEndsTheSessionWithoutReconnecting()
    {
        var transport = new FakeTransport(
            Welcome(),
            Frame(1, TickFlags.Reset, enters: [9], realm: true),
            Frame(2, TickFlags.None, leaves: [9], events: true),
            Frame(3, TickFlags.None, enters: [10]));

        var outcome = await RunUntilDisconnected(transport, new ThrowingHandler());
        await using var client = outcome.Client;

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Code, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(outcome.WillRetry, Is.False);
            Assert.That(outcome.Faults, Has.Count.EqualTo(1));
            Assert.That(outcome.Faults[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(outcome.Transports, Is.EqualTo(1), "1007 is not reconnected after");
            Assert.That(transport.ClosedWith, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(transport.LastSentType, Is.EqualTo(MessageTypes.Bye));
            Assert.That(ByeMessage.Parse(transport.LastSent).Code, Is.EqualTo(CloseCodes.ClientRefusedTheStream));
            Assert.That(client.IsConnected, Is.False);
            Assert.That(client.MessagesReceived, Is.EqualTo(2), "the frame behind the failed one is never read");
            Assert.That(client.Store.TryLocate(9, out _, out _), Is.True, "the store is left as the failed frame made it: its leave never applied");
            Assert.That(client.Store.TryLocate(10, out _, out _), Is.False);
        });
    }

    /// <summary>A truncated <c>TICK</c> — a length mismatch — refuses the stream with 1007, the code its <see cref="WireFormatException"/> carries.</summary>
    [Test]
    public async Task AMalformedTickEndsTheSessionWithItsOwnCode()
    {
        var good = Frame(1, TickFlags.Reset, enters: [9], realm: true);
        var truncated = good.AsSpan(0, good.Length - 3).ToArray();
        var transport = new FakeTransport(Welcome(), truncated, Frame(2, TickFlags.None, enters: [10]));

        var outcome = await RunUntilDisconnected(transport, null);
        await using var client = outcome.Client;

        Assert.That(outcome.Faults, Has.Count.EqualTo(1));
        Assert.That(outcome.Faults[0], Is.TypeOf<WireFormatException>());
        Assert.Multiple(() =>
        {
            Assert.That(((WireFormatException)outcome.Faults[0]).CloseCode, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(outcome.Code, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(outcome.WillRetry, Is.False);
            Assert.That(transport.ClosedWith, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(outcome.Transports, Is.EqualTo(1));
            Assert.That(client.MessagesReceived, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A message the transport refuses before reading it — above the cap — takes the same path: <c>BYE 4004</c>, the close with 1009, and no
    /// reconnect. It used to reach the policy as whatever the transport's close code happened to be: 0 on WebSocket.
    /// </summary>
    [Test]
    public async Task AMessageAboveTheCapEndsTheSessionWith1009()
    {
        var transport = new FakeTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [9], realm: true), FakeTransport.OverCap, Frame(2, TickFlags.None));

        var outcome = await RunUntilDisconnected(transport, null);
        await using var client = outcome.Client;

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Code, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(outcome.WillRetry, Is.False);
            Assert.That(transport.ClosedWith, Is.EqualTo(CloseCodes.MessageTooBig));
            Assert.That(ByeMessage.Parse(transport.LastSent).Code, Is.EqualTo(CloseCodes.ClientRefusedTheStream));
            Assert.That(outcome.Transports, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A handler's own <see cref="WireFormatException"/> cannot choose a retryable code: anything but 1002, 1007 or 1009 is refused as 1007, so a bug in
    /// the application never reconnects onto itself. A malformed <c>PONG</c> is refused as well, though the client reads nothing from it.
    /// </summary>
    [TestCase(true, TestName = "AHandlersWireErrorWithARetryableCodeIsRefusedWith1007")]
    [TestCase(false, TestName = "AMalformedPongIsRefusedWith1007")]
    public async Task ARefusalNeverCarriesARetryableCode(bool handler)
    {
        var intruder = handler ? Frame(2, TickFlags.None, events: true) : [MessageTypes.Pong, 0];
        var transport = new FakeTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [9], realm: true), intruder);
        var events = handler ? new ThrowingHandler(new WireFormatException(CloseCodes.InternalError, "an application's own")) : null;

        var outcome = await RunUntilDisconnected(transport, events);
        await using var client = outcome.Client;

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Code, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(outcome.WillRetry, Is.False);
            Assert.That(outcome.Transports, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// 03 § 3: an unknown message type, or a <c>WELCOME</c> on an open session, refuses the stream with 1002. The second WELCOME would otherwise have
    /// recompiled the catalog and replaced the store under the application.
    /// </summary>
    [TestCase(MessageTypes.Hello, TestName = "AClientToServerTypeIsRefusedWith1002")]
    [TestCase((byte)0x7F, TestName = "AnUnknownTypeIsRefusedWith1002")]
    [TestCase(MessageTypes.Welcome, TestName = "ASecondWelcomeIsRefusedWith1002")]
    [TestCase((byte)0, TestName = "AnEmptyMessageIsRefusedWith1002")]
    public async Task AnUnknownOrOutOfStateMessageEndsTheSessionWith1002(byte type)
    {
        var intruder = type switch
        {
            MessageTypes.Welcome => Welcome(),
            0 => [],
            _ => new byte[] { type, 0, 0, 0, 0 },
        };
        var transport = new FakeTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [9], realm: true), intruder, Frame(2, TickFlags.None, enters: [10]));

        var outcome = await RunUntilDisconnected(transport, null);
        await using var client = outcome.Client;
        var store = client.Store;

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Code, Is.EqualTo(CloseCodes.ProtocolError));
            Assert.That(outcome.WillRetry, Is.False);
            Assert.That(transport.ClosedWith, Is.EqualTo(CloseCodes.ProtocolError));
            Assert.That(outcome.Faults, Has.Count.EqualTo(1));
            Assert.That(store.TryLocate(9, out _, out _), Is.True, "the store the session built is still the one the application holds");
            Assert.That(store.TryLocate(10, out _, out _), Is.False);
        });
    }

    /// <summary>A recording holds one <c>WELCOME</c> per session it spans, so replay applies every one of them rather than refusing the second.</summary>
    [Test]
    public void ReplayAppliesTheWelcomeOfEverySessionInTheRecording()
    {
        var recording = new Recorder();
        recording.Record(Welcome());
        recording.Record(Frame(1, TickFlags.Reset, enters: [9], realm: true));
        recording.Record(Welcome());
        recording.Record(Frame(2, TickFlags.Reset, enters: [10], realm: true));

        var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, PingHz = 0 });
        var store = client.Replay(recording);

        Assert.Multiple(() =>
        {
            Assert.That(store.TryLocate(9, out _, out _), Is.False, "the second WELCOME carried a catalog, so it began a new store");
            Assert.That(store.TryLocate(10, out _, out _), Is.True);
        });
    }

    private static async Task<Outcome> RunUntilDisconnected(FakeTransport transport, IEventHandler handler)
    {
        var transports = 0;
        var client = new TyphonClient(
            new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), PingHz = 0, Events = handler, InitialBackoff = TimeSpan.FromMilliseconds(1) },
            () =>
            {
                Interlocked.Increment(ref transports);
                return transport;
            });

        var faults = new List<Exception>();
        var disconnected = new TaskCompletionSource<(ushort, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Fault += e =>
        {
            lock (faults)
            {
                faults.Add(e);
            }
        };
        client.Disconnected += (code, retry) => disconnected.TrySetResult((code, retry));

        await client.ConnectAsync();
        var finished = await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != disconnected.Task)
        {
            // Stopped before failing, so a client that went on, or began reconnecting, does not outlive the test.
            await client.DisposeAsync();
            Assert.Fail("the session did not end");
        }

        var (code, willRetry) = disconnected.Task.Result;
        return new Outcome(client, code, willRetry, faults, Volatile.Read(ref transports));
    }

    private sealed record Outcome(TyphonClient Client, ushort Code, bool WillRetry, List<Exception> Faults, int Transports);

    private static byte[] Welcome()
    {
        var w = new WireWriter(new byte[ProtocolConstants.WelcomeMaxBytes]);
        new WelcomeMessage { SessionId = 1, CatalogHash = 42, CatalogJson = CatalogJson }.Write(ref w);
        return w.Written.ToArray();
    }

    private static byte[] Frame(uint tick, TickFlags flags, uint[] enters = null, uint[] leaves = null, bool events = false, bool realm = false)
    {
        var w = new WireWriter(new byte[1 << 16]);
        TickWriter.WriteHeader(ref w, tick, flags);
        if (realm)
        {
            TickWriter.WriteRealm(ref w, TestFrames.Kitchen);
        }

        if (events)
        {
            var values = new RecordValues { ["from"] = FieldValue.Of(9), ["path"] = FieldValue.Of(0, 0), ["loud"] = FieldValue.Of(0) };
            var ping = (Plan.EventByName("Ping"), values);
            TickWriter.WriteEvents(ref w, [ping], TestFrames.Kitchen);
        }

        var enterRecords = Array.ConvertAll(enters ?? [], id => new EnterRecord
        {
            NetId = id,
            Position = [id, -(double)id],
            Values = new RecordValues { ["channel"] = FieldValue.Of(1), ["strength"] = FieldValue.Of(0.5), ["drift"] = FieldValue.Of(-2) },
        });
        TickWriter.WriteEntities(ref w, tick, Beacon, enterRecords, [], [], leaves ?? [], TestFrames.Kitchen);
        return w.Written.ToArray();
    }

    /// <summary>A transport over a fixed script of server messages; it records what the client sent and how it closed.</summary>
    private sealed class FakeTransport : IClientTransport
    {
        /// <summary>Queued in place of a message, it makes the receive throw as a transport does for one above the cap.</summary>
        public static readonly byte[] OverCap = [0xFF];

        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();

        public FakeTransport(params byte[][] messages)
        {
            foreach (var message in messages)
            {
                _inbound.Writer.TryWrite(message);
            }
        }

        public bool IsConnected { get; private set; }

        public ushort CloseCode { get; private set; }

        public ushort ClosedWith { get; private set; }

        public byte[] LastSent { get; private set; }

        public byte LastSentType => LastSent == null ? (byte)0 : LastSent[0];

        public Task ConnectAsync(CancellationToken ct)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            LastSent = message.ToArray();
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            byte[] message;
            try
            {
                message = await _inbound.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            if (ReferenceEquals(message, OverCap))
            {
                CloseCode = CloseCodes.MessageTooBig;
                throw new WireFormatException(CloseCodes.MessageTooBig, "a message above the cap");
            }

            return message;
        }

        public Task CloseAsync(ushort code, string reason)
        {
            ClosedWith = code;
            CloseCode = code;
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler(Exception exception = null) : IEventHandler
    {
        public void Event(MessagePlan type) => throw exception ?? new InvalidOperationException("an application handler bug");

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
        }

        public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }
    }
}
