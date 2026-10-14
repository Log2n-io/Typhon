using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// <see cref="TyphonClient.FrameApplied"/> (#1361): raised once per <c>TICK</c>, after its events and its leaves, with the store the frame went into — and
/// never for a frame whose apply threw, nor for a message that is not a frame.
/// </summary>
[TestFixture]
public class FrameAppliedTests
{
    private static readonly byte[] CatalogJson = GoldenFiles.ReadBin("catalog-kitchen-sink");
    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(CatalogJson));

    private static ArchetypePlan Beacon => Plan.ArchetypeByName("Beacon");

    /// <summary>
    /// The handler sees the frame whole: the leave its event preceded has applied, and the frame's change lists are still there to copy. The WELCOME raises
    /// nothing, and the store handed over is the client's.
    /// </summary>
    [Test]
    public void TheFrameIsCompleteWhenTheHandlerRuns()
    {
        var events = new LocatingHandler();
        var client = Client(events);
        var seen = new List<(long Tick, bool NineHeld, int Entered, uint[] Left, int EventsBefore, bool SameStore)>();
        client.FrameApplied += store =>
        {
            var beacons = store.Archetypes[Beacon.Idx];
            seen.Add((store.Tick, store.TryLocate(9, out _, out _), beacons.EnteredCount, beacons.Left[..beacons.LeftCount], events.Count,
                ReferenceEquals(store, client.Store)));
        };
        events.Store = () => client.Store;

        client.Replay(Recording(Welcome(), Frame(1, TickFlags.Reset, enters: [5, 9]), Frame(2, TickFlags.None, enters: [8], leaves: [9], pingFrom: 9)));

        Assert.Multiple(() =>
        {
            Assert.That(seen, Has.Count.EqualTo(2), "one per TICK, none for the WELCOME");
            Assert.That(seen[0].Tick, Is.EqualTo(1));
            Assert.That(seen[0].NineHeld, Is.True);
            Assert.That(seen[0].Entered, Is.EqualTo(2));
            Assert.That(seen[0].Left, Is.Empty);
            Assert.That(seen[0].SameStore, Is.True);
            Assert.That(events.NineHeldAtEvent, Is.True, "the event ran before the leave");
            Assert.That(seen[1].Tick, Is.EqualTo(2));
            Assert.That(seen[1].NineHeld, Is.False, "the leave had applied when the handler ran");
            Assert.That(seen[1].Entered, Is.EqualTo(1));
            Assert.That(seen[1].Left, Is.EqualTo(new uint[] { 9 }));
            Assert.That(seen[1].EventsBefore, Is.EqualTo(1), "the event was dispatched before the handler");
            Assert.That(seen[1].SameStore, Is.True);
        });
    }

    /// <summary>A frame whose apply throws is partly applied: the handler is not told it is complete.</summary>
    [Test]
    public void AFrameWhoseApplyThrowsRaisesNothing()
    {
        var events = new LocatingHandler { Throw = true };
        var client = Client(events);
        var raised = 0;
        client.FrameApplied += _ => raised++;
        events.Store = () => client.Store;

        Assert.Catch(() => client.Replay(Recording(Welcome(), Frame(1, TickFlags.Reset, enters: [5]), Frame(2, TickFlags.None, pingFrom: 5))));
        Assert.That(raised, Is.EqualTo(1), "the first frame only");
    }

    /// <summary>A WELCOME carrying a catalog replaces the store; the handler is handed the new one, not the store a subscriber may still hold.</summary>
    [Test]
    public void TheHandlerIsHandedTheStoreACatalogChangeReplacedItWith()
    {
        var client = Client(null);
        var stores = new List<WorldStore>();
        client.FrameApplied += stores.Add;

        client.Replay(Recording(Welcome(), Frame(1, TickFlags.Reset, enters: [5]), Welcome(), Frame(1, TickFlags.Reset, enters: [6])));

        Assert.Multiple(() =>
        {
            Assert.That(stores, Has.Count.EqualTo(2));
            Assert.That(stores[1], Is.Not.SameAs(stores[0]));
            Assert.That(stores[1], Is.SameAs(client.Store));
        });
    }

    /// <summary>On the live receive loop, a handler may end the session with <see cref="TyphonClient.CloseAsync"/>: the BYE goes out, no later frame
    /// is applied, and the session reports its close.</summary>
    [Test]
    public async Task AHandlerMayCloseTheSession()
    {
        var transport = new MemoryTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [5]), Frame(2, TickFlags.None, enters: [6]));
        var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, PingHz = 0 }, () => transport);
        var raised = 0;
        var closed = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (code, _) => closed.TrySetResult(code);
        client.FrameApplied += _ =>
        {
            raised++;
            client.CloseAsync().GetAwaiter().GetResult();
        };

        await client.ConnectAsync();
        var finished = await Task.WhenAny(closed.Task, Task.Delay(Timeout));

        Assert.That(finished, Is.SameAs(closed.Task), "the session reported its close");
        await client.DisposeAsync();
        Assert.Multiple(() =>
        {
            Assert.That(raised, Is.EqualTo(1), "no frame after the close");
            Assert.That(transport.Sent[^1][0], Is.EqualTo(MessageTypes.Bye));
        });
    }

    /// <summary>
    /// A handler may dispose the client, even blocking on it: the dispose cannot wait for the receive loop, whose thread is the one running the handler,
    /// so it does not — and the loop ends as soon as the handler returns.
    /// </summary>
    [Test]
    public async Task AHandlerMayDisposeTheClientWithoutDeadlocking()
    {
        var transport = new MemoryTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [5]), Frame(2, TickFlags.None, enters: [6]));
        var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, PingHz = 0 }, () => transport);
        var raised = 0;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameApplied += _ =>
        {
            raised++;
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };

        await client.ConnectAsync();
        var finished = await Task.WhenAny(disposed.Task, Task.Delay(Timeout));

        Assert.Multiple(() =>
        {
            Assert.That(finished, Is.SameAs(disposed.Task), "the dispose returned inside the handler");
            Assert.That(raised, Is.EqualTo(1), "no frame after the dispose");
            Assert.That(transport.Disposed, Is.True);
        });
    }

    /// <summary>
    /// A handler that throws is the application's failure, not the frame's: <see cref="TyphonClient.Fault"/> reports it, the frame stays applied, and the
    /// next frame is applied and raised as usual.
    /// </summary>
    [Test]
    public async Task AHandlerThatThrowsIsAFaultAndTheSessionContinues()
    {
        var transport = new MemoryTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [5]), Frame(2, TickFlags.None, enters: [6]));
        var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, PingHz = 0 }, () => transport);
        var ticks = new List<long>();
        var faults = new List<Exception>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Fault += faults.Add;
        client.FrameApplied += store =>
        {
            ticks.Add(store.Tick);
            if (store.Tick == 1)
            {
                throw new InvalidOperationException("the application's handler failed");
            }

            second.TrySetResult();
        };

        await client.ConnectAsync();
        var finished = await Task.WhenAny(second.Task, Task.Delay(Timeout));
        await client.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(finished, Is.SameAs(second.Task));
            Assert.That(ticks, Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0], Is.InstanceOf<InvalidOperationException>());
            Assert.That(client.Store.TryLocate(5, out _, out _), Is.True, "the frame whose handler threw stayed applied");
        });
    }

    /// <summary>The guard covers every handler the loop raises, not only <see cref="TyphonClient.FrameApplied"/>: disposing on a server's close, blocking,
    /// does not deadlock either.</summary>
    [Test]
    public async Task ADisconnectedHandlerMayDisposeTheClientWithoutDeadlocking()
    {
        var transport = new MemoryTransport(Welcome(), Frame(1, TickFlags.Reset, enters: [5])) { ServerCloses = true };
        var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, PingHz = 0 }, () => transport);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) =>
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };

        await client.ConnectAsync();
        var finished = await Task.WhenAny(disposed.Task, Task.Delay(Timeout));

        Assert.That(finished, Is.SameAs(disposed.Task), "the dispose returned inside the handler");
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TyphonClient Client(IEventHandler events) =>
        new(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), Reconnect = false, Events = events });

    private static Recorder Recording(params byte[][] messages)
    {
        var recorder = new Recorder();
        foreach (var message in messages)
        {
            recorder.Record(message);
        }

        return recorder;
    }

    private static byte[] Welcome()
    {
        var w = new WireWriter(new byte[CatalogJson.Length + 256]);
        new WelcomeMessage { SessionId = 1, Tick = 1, TickPeriodUs = 50_000, CatalogJson = CatalogJson }.Write(ref w);
        return w.Written.ToArray();
    }

    // A RESET frame opens with the REALM its positions decode over; an event, when given, travels before the ENTITIES block.
    private static byte[] Frame(uint tick, TickFlags flags, uint[] enters = null, uint[] leaves = null, uint? pingFrom = null)
    {
        var w = new WireWriter(new byte[1 << 16]);
        TickWriter.WriteHeader(ref w, tick, flags);
        if ((flags & TickFlags.Reset) != 0)
        {
            TickWriter.WriteRealm(ref w, TestFrames.Kitchen);
        }

        if (pingFrom is { } from)
        {
            var values = new RecordValues { ["from"] = FieldValue.Of(from), ["path"] = FieldValue.Of(0, 0), ["loud"] = FieldValue.Of(0) };
            TickWriter.WriteEvents(ref w, [(Plan.EventByName("Ping"), values)], TestFrames.Kitchen);
        }

        var enterRecords = Array.ConvertAll(enters ?? [], id => new EnterRecord
        {
            NetId = id,
            Position = [id, -(double)id],
            Values = new RecordValues
            {
                ["channel"] = FieldValue.Of(1), ["strength"] = FieldValue.Of(0.5), ["drift"] = FieldValue.Of(-2),
            },
        });
        TickWriter.WriteEntities(ref w, tick, Beacon, enterRecords, [], [], leaves ?? [], TestFrames.Kitchen);
        return w.Written.ToArray();
    }

    /// <summary>A server in memory: queued messages in order, then a close; a client's close ends the stream at once, whatever is still queued.</summary>
    private sealed class MemoryTransport(params byte[][] inbound) : IClientTransport
    {
        private readonly Channel<byte[]> _inbound = Inbound(inbound);
        private volatile bool _closed;

        public List<byte[]> Sent { get; } = [];

        /// <summary>Ends the stream once the queued messages are read, as a server closing the session would.</summary>
        public bool ServerCloses
        {
            init
            {
                if (value)
                {
                    _inbound.Writer.TryComplete();
                }
            }
        }

        public bool Disposed { get; private set; }

        public bool IsConnected => !_closed && !Disposed;

        public ushort CloseCode => _closed ? CloseCodes.Normal : (ushort)0;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(message.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken ct)
        {
            if (_closed || !await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false) || _closed)
            {
                return null;
            }

            return _inbound.Reader.TryRead(out var message) ? message : null;
        }

        public Task CloseAsync(ushort code, string reason)
        {
            _closed = true;
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _closed = true;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        // The queue stays open after the last message: a server that has said everything still holds the session until someone closes it.
        private static Channel<byte[]> Inbound(byte[][] messages)
        {
            var channel = Channel.CreateUnbounded<byte[]>();
            foreach (var message in messages)
            {
                channel.Writer.TryWrite(message);
            }

            return channel;
        }
    }

    private sealed class LocatingHandler : IEventHandler
    {
        public Func<WorldStore> Store { get; set; }

        public bool Throw { get; init; }

        public int Count { get; private set; }

        public bool NineHeldAtEvent { get; private set; }

        public void Event(MessagePlan type)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the application's handler failed");
            }

            Count++;
            NineHeldAtEvent = Store().TryLocate(9, out _, out _);
        }

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
