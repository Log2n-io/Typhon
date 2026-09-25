using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>A command whose fields cover the integer, varint, half and quantized codecs, and an entity reference.</summary>
[StructLayout(LayoutKind.Sequential)]
struct FuzzOrder
{
    public byte Small;
    public short Signed;
    public uint Count;
    public int Delta;
    public float Half;
    public double Where;
    public uint Target;
    public byte Flag;
}

/// <summary>The value set of <see cref="FuzzWide.Mode"/>: three names in two bits, so the fourth value is out of range.</summary>
enum FuzzMode : byte
{
    Idle = 0,
    Walk = 1,
    Run = 2,
}

/// <summary>A command for the remaining codec kinds — and a player-only, slow-rated one, so role refusals and rate-limit acknowledgements happen.</summary>
[StructLayout(LayoutKind.Sequential)]
struct FuzzWide
{
    public sbyte Tiny;
    public uint Big;
    public int Wide;
    public float Real;
    public float Heading;
    public float Unit;
    public float Signed;
    public byte Few;
    public FuzzMode Mode;
}

/// <summary>A coalesced command: only the newest per session survives a drain (SUB-08).</summary>
[StructLayout(LayoutKind.Sequential)]
struct FuzzMove
{
    public double X;
    public double Z;
    public ushort Speed;
}

/// <summary>
/// AC-17 (design/Subscriptions/11 § 4.1): client → server bytes, valid and mutated, through the real path — <see cref="SubscriptionConnection"/>, the
/// runtime's ingress (the transport-side decode, the built-in <c>ClientRegion</c>'s hull), the Engine-Pre drain, the commands' consumers and the frame stage.
/// Properties: nothing a client sends throws out of the server; a malformed message closes its connection with a protocol code, a <c>KICK</c> first, and
/// frames nothing; a well-formed one never closes it; the drain never faults; the tick keeps running; no session slot leaks.
/// </summary>
[TestFixture]
[NonParallelizable]
unsafe class ClientInputFuzzTests : TestBase<ClientInputFuzzTests>
{
    private const double CellM = 40;

    private long _tick;

    private static readonly HashSet<ushort> MalformedCloseCodes = [CloseCodes.ProtocolError, CloseCodes.MalformedPayload, CloseCodes.MessageTooBig];

    private FrameHarness Harness(DatabaseEngine dbe, string name)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("god", p => p.ClientRegion(1000).Of<ProjCreature>());
            subs.Command<FuzzOrder>(c => c
                .Roles(SessionRole.Spectator, SessionRole.Player)
                .Rate(1_000_000, 1_000_000)
                .Field(m => m.Small, Codec.U8)
                .Field(m => m.Signed, Codec.I16)
                .Field(m => m.Count, Codec.VarUInt)
                .Field(m => m.Delta, Codec.VarInt)
                .Field(m => m.Half, Codec.F16)
                .Field(m => m.Where, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Target, Codec.EntityRef)
                .Field(m => m.Flag, Codec.Bool));
            subs.Command<FuzzWide>(c => c
                .Roles(SessionRole.Player)
                .Rate(20, 20)
                .Field(m => m.Tiny, Codec.I8)
                .Field(m => m.Big, Codec.U32)
                .Field(m => m.Wide, Codec.I32)
                .Field(m => m.Real, Codec.F32)
                .Field(m => m.Heading, Codec.Angle(16))
                .Field(m => m.Unit, Codec.Unorm(8))
                .Field(m => m.Signed, Codec.Snorm(16))
                .Field(m => m.Few, Codec.Bits(3))
                .Field(m => m.Mode, Codec.Enum<FuzzMode>(2)));
            subs.Command<FuzzMove>(c => c
                .Roles(SessionRole.Spectator, SessionRole.Player)
                .Coalesce(CommandCoalesce.LatestPerSession)
                .Rate(1_000_000, 1_000_000)
                .Field(m => m.X, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Z, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Speed, Codec.U16));
        }, name, new SubscriptionsOptions { MaxSessions = 64, EnterBudgetPerFrame = 256, ReplicationCellM = CellM });
        harness.RunFence = true;
        harness.RunIngress = true;
        _tick = 0;
        return harness;
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < 400; i++)
        {
            var b = new ProjBounds { Bounds = new AABB2F { MinX = i % 20 * 10f, MinY = i / 20 * 10f, MaxX = i % 20 * 10f, MaxY = i / 20 * 10f }, Speed = 1f };
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in b));
        }

        tx.Commit();
    }

    // ── valid messages ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private enum TemplateKind
    {
        Commands,
        Ping,
        Bye,
        Oversize,
    }

    private sealed class Templates
    {
        private readonly CatalogPlan _plan;
        private readonly Random _rng;
        private readonly int _messageCap;
        private ushort _seq;

        public Templates(CatalogPlan plan, Random rng, int messageCap)
        {
            _plan = plan;
            _rng = rng;
            _messageCap = messageCap;
        }

        public byte[] Next(out TemplateKind kind)
        {
            var roll = _rng.Next(1000);
            if (roll < 60)
            {
                kind = TemplateKind.Ping;
                return ClientMessages.Ping((uint)_rng.Next(), (uint)_rng.Next());
            }

            if (roll < 64)
            {
                kind = TemplateKind.Bye;
                return ClientMessages.Bye(_rng.Next(2) == 0 ? CloseCodes.Normal : (ushort)_rng.Next(4000, 5000));
            }

            if (roll < 70)
            {
                // Past the session's message cap: 1009, checked before a byte is decoded.
                kind = TemplateKind.Oversize;
                return ClientMessages.Commands(_messageCap + 1 + _rng.Next(64));
            }

            kind = TemplateKind.Commands;
            var count = 1 + _rng.Next(6);
            var list = new List<(MessagePlan, ushort, RecordValues)>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(_rng.Next(4) switch
                {
                    0 => (_plan.CommandByName(nameof(FuzzOrder)), ++_seq, Order()),
                    1 => (_plan.CommandByName(nameof(FuzzWide)), ++_seq, Wide()),
                    2 => (_plan.CommandByName(nameof(FuzzMove)), ++_seq, Move()),
                    _ => (_plan.CommandByName(BuiltInCommands.ClientRegion), ++_seq, Region()),
                });
            }

            var buffer = new byte[4096];
            var writer = new WireWriter(buffer);
            CommandsMessage.Write(ref writer, (uint)_rng.Next(), list);
            return writer.Written.ToArray();
        }

        private RecordValues Order() => new()
        {
            ["Small"] = FieldValue.Of(_rng.Next(256)),
            ["Signed"] = FieldValue.Of(_rng.Next(-32768, 32767)),
            ["Count"] = FieldValue.Of((uint)_rng.Next()),
            ["Delta"] = FieldValue.Of(_rng.Next(int.MinValue, int.MaxValue)),
            ["Half"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 1000),
            ["Where"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 16000),
            ["Target"] = FieldValue.Of((uint)_rng.Next(0, 500)),
            ["Flag"] = FieldValue.Of(_rng.Next(2)),
        };

        private RecordValues Wide() => new()
        {
            ["Tiny"] = FieldValue.Of(_rng.Next(-128, 127)),
            ["Big"] = FieldValue.Of((uint)_rng.Next()),
            ["Wide"] = FieldValue.Of(_rng.Next(int.MinValue, int.MaxValue)),
            ["Real"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 1e6),
            ["Heading"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 6.28),
            ["Unit"] = FieldValue.Of(_rng.NextDouble()),
            ["Signed"] = FieldValue.Of((_rng.NextDouble() * 2) - 1),
            ["Few"] = FieldValue.Of(_rng.Next(8)),
            ["Mode"] = FieldValue.Of(_rng.Next(3)),
        };

        private RecordValues Move() => new()
        {
            ["X"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 16000),
            ["Z"] = FieldValue.Of((_rng.NextDouble() - 0.5) * 16000),
            ["Speed"] = FieldValue.Of(_rng.Next(65536)),
        };

        // Hostile but well-framed hulls: the shapes random byte edits rarely produce, straight into the hull builder and the plane derivation.
        private RecordValues Region()
        {
            var shape = _rng.Next(10);
            var n = shape == 0 ? BuiltInCommands.MaxRegionVertices : 3 + _rng.Next(6);
            var (cx, cy) = (_rng.NextDouble() * 400, _rng.NextDouble() * 400);
            var r = shape switch
            {
                1 => 0.0005,                                   // sub-millimetre: every vertex quantizes onto one point
                2 => 20_000,                                   // far wider than the profile's max edge: clamped
                _ => 20 + (_rng.NextDouble() * 300),
            };
            var coords = new double[n * 2];
            for (var v = 0; v < n; v++)
            {
                var a = v * Math.PI * 2 / n;
                (coords[v * 2], coords[(v * 2) + 1]) = shape switch
                {
                    3 => (cx + (v * 10), cy + (v * 10)),        // collinear
                    4 => (cx, cy),                             // every vertex the same
                    5 => (v % 2 == 0 ? cx : cx + r, cy),       // two points, repeated
                    6 => (cx + (Math.Cos(a * 2) * r), cy + (Math.Sin(a * 2) * r)), // self-intersecting order
                    _ => (cx + (Math.Cos(a) * r), cy + (Math.Sin(a) * r)),
                };
            }

            return new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(coords),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(_rng.NextDouble() * 500),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(_rng.Next(1, 4096)),
            };
        }
    }

    // ── mutations ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[][] Extremes =
    [
        [0xFF, 0xFF, 0xFF, 0xFF, 0x0F], [0x80, 0x80, 0x80, 0x80, 0x80, 0x00], [0xFF, 0xFF, 0xFF, 0xFF, 0x7F],
        [0x00, 0x00, 0xC0, 0x7F], [0x00, 0x00, 0x80, 0x7F], [0x00, 0x00, 0x80, 0xFF], [0x01, 0x00, 0x00, 0x00],
        [0x00, 0x7C], [0x00, 0xFC], [0x01, 0x7E], [0x01, 0x00],                     // f16: +∞, −∞, NaN, a subnormal
        [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF8, 0x7F], [0xFF, 0xFF], [0x00], [0x7F], [0x80],
    ];

    private static byte[] Mutate(byte[] message, byte[] other, Random rng)
    {
        var bytes = new List<byte>(message);
        var edits = 1 + rng.Next(3);
        for (var e = 0; e < edits && bytes.Count > 0; e++)
        {
            var at = rng.Next(bytes.Count);
            switch (rng.Next(9))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1:
                    bytes[at] = (byte)rng.Next(256);
                    break;
                case 2:
                    bytes.Insert(at, (byte)rng.Next(256));
                    break;
                case 3:
                    bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + rng.Next(8)));
                    break;
                case 4:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 5:
                    var extreme = Extremes[rng.Next(Extremes.Length)];
                    for (var i = 0; i < extreme.Length && at + i < bytes.Count; i++)
                    {
                        bytes[at + i] = extreme[i];
                    }

                    break;
                case 6:
                    var from = rng.Next(other.Length);
                    bytes.InsertRange(at, other.Skip(from).Take(1 + rng.Next(16)));
                    break;
                case 7:
                    var start = rng.Next(bytes.Count);
                    bytes.InsertRange(at, bytes.Skip(start).Take(1 + rng.Next(8)).ToArray());
                    break;
                default:
                    // A count or an index near the head — the message's command count, a command index, a list length — set to 0, 1, ±1 of itself or 0xFF.
                    var head = Math.Min(bytes.Count - 1, 5 + rng.Next(6));
                    bytes[head] = rng.Next(5) switch
                    {
                        0 => 0,
                        1 => 1,
                        2 => (byte)(bytes[head] + 1),
                        3 => (byte)(bytes[head] - 1),
                        _ => 0xFF,
                    };
                    break;
            }
        }

        if (bytes.Count == 0)
        {
            bytes.Add(MessageTypes.Commands);
        }

        return [.. bytes];
    }

    // ── the run ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Client
    {
        public SubscriptionConnection Connection;
        public InProcessLink Link;

        /// <summary>How often this client's messages are mutated: most clients are nearly well-behaved, so sessions live long enough to reach deep states.</summary>
        public int MutatePercent;
    }

    private void Fuzz(int messages, int seed)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = Harness(dbe, $"{nameof(ClientInputFuzzTests)}_{seed}");
        var rng = new Random(seed);
        var host = (ISubscriptionsHost)harness.Subscriptions;
        var templates = new Templates(harness.CatalogPlan, rng, new SubscriptionsOptions().ClientMessageBytes);
        var info = new LinkInfo { Transport = "fuzz", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var clients = new List<Client>();
        var closes = new Dictionary<ushort, int>();
        var hellosMutated = 0;
        var previous = templates.Next(out _);

        // What a system does with what it is handed: every command read, every entity reference resolved — so the typed buffers built from fuzzed payloads
        // are consumed, not only filled.
        var commands = harness.Subscriptions.Commands;
        harness.AfterIngress = tick =>
        {
            foreach (ref readonly var c in commands.Commands<FuzzOrder>())
            {
                commands.TryResolve(c.Session, c.Value.Target, out _);
            }

            foreach (ref readonly var c in commands.Commands<FuzzWide>())
            {
                _ = c.Value.Mode;
            }

            foreach (ref readonly var c in commands.Commands<FuzzMove>())
            {
                _ = c.Value.X;
            }
        };

        void Send(Client client, byte[] message, bool valid, int m)
        {
            var row = client.Connection.State == SubscriptionConnectionState.Open ? harness.Subscriptions.Ingress.RowOf(client.Connection.Session) : null;
            var pending = row?.Ring.BytesPending ?? 0;
            try
            {
                client.Connection.OnMessage(message);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed}, message {m}: {ex.GetType().Name} escaped OnMessage for {Convert.ToHexString(message)}\n{ex}");
            }

            if (client.Connection.State == SubscriptionConnectionState.Closed && !client.Link.IsClosed)
            {
                // An open session's refusal is sent by its send pump — after whatever the pump is writing — and the pump closes the link: wait for it.
                var deadline = Environment.TickCount64 + 5000;
                while (!client.Link.IsClosed && Environment.TickCount64 < deadline)
                {
                    Thread.Yield();
                }

                var pump = harness.Subscriptions.SendPump;
                Assert.That(client.Link.IsClosed, Is.True, $"seed {seed}, message {m}: the refusal's close never reached the link (kicks sent {pump.KicksSent}, "
                    + $"send failures {pump.SendFailures}, active pumps {pump.ActivePumps}, link pending {client.Link.PendingCount}, slot "
                    + $"{pump.SlotStateForTest(client.Connection.Session.Slot)}, code {client.Connection.CloseCode}, pump faults {pump.PumpFaults}: "
                    + $"{pump.LastPumpFault})");
            }

            if (!client.Link.IsClosed)
            {
                return;
            }

            var code = client.Link.CloseCode;
            closes[code] = closes.GetValueOrDefault(code) + 1;
            if (message.Length > 0 && message[0] == MessageTypes.Bye && (code == CloseCodes.Normal || code is >= 4000 and <= 4999))
            {
                return;
            }

            Assert.That(valid, Is.False, $"seed {seed}, message {m}: a well-formed message closed its connection with {code}: {Convert.ToHexString(message)}");
            // Before admission, a HELLO the application refuses (an undeclared kind: 4003) is closed with the admission's code rather than the protocol's.
            var hello = m < 0 && code is >= 4000 and <= 4999;
            Assert.That(hello || MalformedCloseCodes.Contains(code), Is.True, $"seed {seed}, message {m}: close code {code} for {Convert.ToHexString(message)}");
            Assert.That(client.Link.CloseCount, Is.EqualTo(1), $"seed {seed}, message {m}: closed more than once");
            byte[] last = null;
            while (client.Link.TryTake(out var sent, 0))
            {
                last = sent;
            }

            // A HELLO naming another protocol major is closed with no KICK, by design: a KICK would oblige every future major to speak this one's framing
            // (03 § 10). Every other refusal says why first.
            if (m < 0 && last == null)
            {
                return;
            }

            Assert.That(last?[0], Is.EqualTo(MessageTypes.Kick),
                $"seed {seed}, message {m}: a malformed message is answered with a KICK before the close (code {code}, last sent "
                + $"{(last == null ? "nothing" : Convert.ToHexString(last.AsSpan(0, Math.Min(8, last.Length))))}): {Convert.ToHexString(message)}");
            Assert.That(KickMessage.Parse(last).Code, Is.EqualTo(code));
            if (row != null)
            {
                Assert.That(row.Ring.BytesPending, Is.EqualTo(pending), $"seed {seed}, message {m}: a refused message framed commands");
            }
        }

        Client Open()
        {
            while (true)
            {
                var link = new InProcessLink();
                var connection = new SubscriptionConnection(host, link, info, Timeout.InfiniteTimeSpan);
                link.Connection = connection;
                var client = new Client { Connection = connection, Link = link, MutatePercent = rng.Next(5) == 0 ? 70 : 5 };
                var hello = ClientMessages.Hello("god", clientCatalogHash: host.CatalogHash);
                if (rng.Next(20) == 0)
                {
                    // A HELLO mutated before admission: refused with a protocol code, or admitted — never a throw.
                    hellosMutated++;
                    Send(client, Mutate(hello, previous, rng), valid: false, -1);
                    if (!link.IsClosed && connection.State == SubscriptionConnectionState.Open)
                    {
                        return client;
                    }

                    continue;
                }

                connection.OnMessage(hello);
                Assert.That(connection.State, Is.EqualTo(SubscriptionConnectionState.Open), $"seed {seed}: a valid HELLO was not admitted (close {link.CloseCode})");
                return client;
            }
        }

        for (var i = 0; i < 8; i++)
        {
            clients.Add(Open());
        }

        harness.RunTick(++_tick);
        for (var m = 0; m < messages; m++)
        {
            var index = rng.Next(clients.Count);
            var client = clients[index];
            if (client.Link.IsClosed)
            {
                // Closed by the tick, not by a message (a hostile client's commands never ping): replaced, not blamed on what comes next.
                client = clients[index] = Open();
            }

            var valid = templates.Next(out var kind);
            var mutate = rng.Next(100) < client.MutatePercent;
            var message = mutate ? Mutate(valid, previous, rng) : valid;
            previous = valid;
            Send(client, message, !mutate && kind is TemplateKind.Commands or TemplateKind.Ping, m);
            if (!mutate && kind == TemplateKind.Oversize)
            {
                Assert.That(client.Link.CloseCode, Is.EqualTo(CloseCodes.MessageTooBig), $"seed {seed}, message {m}: an oversize message");
            }

            if (client.Link.IsClosed)
            {
                clients[index] = Open();
            }

            if (m % 16 == 15)
            {
                // Every live client pings once a tick, as an SDK does: the silence sweep closes a session it has not heard from (SUB-15). What its link
                // received — frames, PONGs — is read and dropped, as a client applies it.
                foreach (var live in clients)
                {
                    if (!live.Link.IsClosed)
                    {
                        live.Connection.OnMessage(ClientMessages.Ping((uint)m, (uint)_tick));
                        while (live.Link.TryTake(out _, 0))
                        {
                        }
                    }
                }

                Tick(harness, seed, m);
            }
        }

        Tick(harness, seed, messages);
        Tick(harness, seed, messages);
        TestContext.Out.WriteLine($"seed {seed}: {messages} messages, {hellosMutated} mutated HELLOs, closes "
            + $"{string.Join(", ", closes.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}×{kv.Value}"))}, tick {_tick}, acks {harness.Assembler.AcksWritten}");

        Assert.Multiple(() =>
        {
            Assert.That(closes.Where(kv => MalformedCloseCodes.Contains(kv.Key)).Sum(kv => kv.Value), Is.GreaterThan(0), "the refusal paths were reached");
            Assert.That(OpenCount(harness), Is.EqualTo(clients.Count), "every live client still has its session, and no slot leaked");
        });
    }

    private void Tick(FrameHarness harness, int seed, int m)
    {
        try
        {
            harness.RunTick(++_tick);
        }
        catch (Exception ex)
        {
            Assert.Fail($"seed {seed}, after message {m}: the tick threw {ex}");
        }

        Assert.That(harness.Subscriptions.Ingress.DrainFaults, Is.Zero,
            $"seed {seed}, after message {m}: the drain faulted: {harness.Subscriptions.Ingress.LastDrainFault}");

        // The frames go out the way they do in production: the tick publishes and wakes each session's pump, the link's only writer. Claiming them here as
        // well would make two claimers of one hand-off, which the send state rightly refuses (frames completed out of order).
        var pump = harness.Subscriptions.SendPump;
        pump.PublishAndWake(_tick);
        Assert.That(pump.PumpFaults, Is.Zero, $"seed {seed}, after message {m}: a send pump faulted: {pump.LastPumpFault}");
    }

    private static int OpenCount(FrameHarness harness)
    {
        var open = 0;
        foreach (var _ in harness.Sessions)
        {
            open++;
        }

        return open;
    }

    /// <summary>The CI budget: 20 000 client messages over rotating connections — most nearly well-behaved, a fifth hostile — with ticks between.</summary>
    [Test]
    public void TwentyThousandMutatedClientMessagesNeverEscapeTheServer() => Fuzz(20_000, 20250925);

    /// <summary>AC-17's budget, in the nightly tier: a million messages, 100 000 per seed.</summary>
    [Test]
    [Explicit("Nightly: AC-17's million messages")]
    [Category("Nightly")]
    public void AMillionMutatedClientMessagesNeverEscapeTheServer([Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)] int seed) => Fuzz(100_000, seed * 7919);

    /// <summary>
    /// SUB-14: an open session's protocol refusal is sent by its send pump — the link's only writer — after whatever the pump owes first (here a PONG), and
    /// the link is closed after it: KICK last, one close. Written from the receive thread instead, it could overlap the pump's own send, which a WebSocket
    /// refuses outright.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-14")]
    public void AnOpenSessionsRefusalIsSentByItsPumpAfterWhatItOwes()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = Harness(dbe, nameof(AnOpenSessionsRefusalIsSentByItsPumpAfterWhatItOwes));
        var host = (ISubscriptionsHost)harness.Subscriptions;
        var link = new InProcessLink();
        var connection = new SubscriptionConnection(host, link, new LinkInfo { Transport = "fuzz", SubProtocol = ProtocolConstants.WebSocketSubprotocol },
            Timeout.InfiniteTimeSpan);
        link.Connection = connection;
        connection.OnMessage(ClientMessages.Hello("god", clientCatalogHash: host.CatalogHash));
        harness.RunTick(++_tick);
        var kicksBefore = harness.Subscriptions.SendPump.KicksSent;

        connection.OnMessage(ClientMessages.Ping(1, 1));
        connection.OnMessage([MessageTypes.Commands, 0, 0, 0, 0, 0x01, 0x7F, 0x00, 0x00]);   // one command, index 127: none such — 1007
        var deadline = Environment.TickCount64 + 5000;
        while (!link.IsClosed && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        var sent = new List<byte[]>();
        while (link.TryTake(out var message, 0))
        {
            sent.Add(message);
        }

        Assert.Multiple(() =>
        {
            Assert.That(link.IsClosed, Is.True, "the pump closed the link");
            Assert.That(link.CloseCode, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(link.CloseCount, Is.EqualTo(1));
            Assert.That(link.OverlappedSends, Is.Zero, "no two sends in flight on one link");
            Assert.That(sent.Select(m => m[0]), Does.Contain(MessageTypes.Pong), "what the pump owed went first");
            Assert.That(sent[^1][0], Is.EqualTo(MessageTypes.Kick), "the KICK is the last thing the link carried");
            Assert.That(KickMessage.Parse(sent[^1]).Code, Is.EqualTo(CloseCodes.MalformedPayload));
            Assert.That(harness.Subscriptions.SendPump.KicksSent, Is.EqualTo(kicksBefore + 1), "sent by the pump");
        });
    }

    /// <summary>
    /// Well-formed commands, through the same path, keep each session's order and are coalesced where declared (SUB-08): each session's queued commands are
    /// seen once, exactly in the order it sent them, and a coalesced command appears at most once per session per tick — the newest that session sent.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-08")]
    public void WellFormedCommandsKeepTheirOrderAndCoalescing()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);
        using var harness = Harness(dbe, nameof(WellFormedCommandsKeepTheirOrderAndCoalescing));
        var rng = new Random(7);
        var host = (ISubscriptionsHost)harness.Subscriptions;
        var info = new LinkInfo { Transport = "fuzz", SubProtocol = ProtocolConstants.WebSocketSubprotocol };
        var links = new List<(SubscriptionConnection Connection, InProcessLink Link)>();
        for (var i = 0; i < 4; i++)
        {
            var link = new InProcessLink();
            var connection = new SubscriptionConnection(host, link, info, Timeout.InfiniteTimeSpan);
            link.Connection = connection;
            connection.OnMessage(ClientMessages.Hello("god", clientCatalogHash: host.CatalogHash));
            links.Add((connection, link));
        }

        var seen = new Dictionary<uint, List<ushort>>();
        var coalesced = new List<(uint Session, double X)>();
        var coalescedTwice = 0;
        var commands = harness.Subscriptions.Commands;
        harness.AfterIngress = _ =>
        {
            foreach (ref readonly var c in commands.Commands<FuzzOrder>())
            {
                (seen.TryGetValue(c.Session.Value, out var l) ? l : seen[c.Session.Value] = []).Add(c.Seq);
            }

            var thisTick = new HashSet<uint>();
            foreach (ref readonly var c in commands.Commands<FuzzMove>())
            {
                coalescedTwice += thisTick.Add(c.Session.Value) ? 0 : 1;
                coalesced.Add((c.Session.Value, c.Value.X));
            }
        };

        harness.RunTick(++_tick);
        var plan = harness.CatalogPlan;
        var sent = links.ToDictionary(l => l.Connection.Session.Value, _ => new List<ushort>());
        var newestMove = new Dictionary<uint, double>();
        var expectedMoves = new List<(uint Session, double X)>();
        var seqs = new ushort[links.Count];
        for (var round = 0; round < 50; round++)
        {
            for (var s = 0; s < links.Count; s++)
            {
                var session = links[s].Connection.Session.Value;
                var list = new List<(MessagePlan, ushort, RecordValues)>();
                var count = 1 + rng.Next(4);
                for (var k = 0; k < count; k++)
                {
                    var seq = ++seqs[s];
                    if (rng.Next(2) == 0)
                    {
                        list.Add((plan.CommandByName(nameof(FuzzOrder)), seq, new RecordValues
                        {
                            ["Small"] = FieldValue.Of(1), ["Signed"] = FieldValue.Of(-2), ["Count"] = FieldValue.Of(seq), ["Delta"] = FieldValue.Of(-3),
                            ["Half"] = FieldValue.Of(0.5), ["Where"] = FieldValue.Of(10.0), ["Target"] = FieldValue.Of(0), ["Flag"] = FieldValue.Of(1),
                        }));
                        sent[session].Add(seq);
                    }
                    else
                    {
                        list.Add((plan.CommandByName(nameof(FuzzMove)), seq, new RecordValues
                        {
                            ["X"] = FieldValue.Of(seq), ["Z"] = FieldValue.Of(1.0), ["Speed"] = FieldValue.Of(3),
                        }));
                        newestMove[session] = seq;
                    }
                }

                var buffer = new byte[2048];
                var writer = new WireWriter(buffer);
                CommandsMessage.Write(ref writer, (uint)round, list);
                links[s].Connection.OnMessage(writer.Written.ToArray());
                links[s].Connection.OnMessage(ClientMessages.Ping((uint)round, (uint)_tick));
            }

            if (round % 3 == 2 || round == 49)
            {
                expectedMoves.AddRange(newestMove.Select(kv => (kv.Key, kv.Value)));
                newestMove.Clear();
                harness.RunTick(++_tick);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(links.Any(l => l.Link.IsClosed), Is.False, "well-formed input closes nothing");
            Assert.That(OpenCount(harness), Is.EqualTo(links.Count), "and the tick closed no session");
            foreach (var (session, list) in sent)
            {
                Assert.That(seen.GetValueOrDefault(session) ?? [], Is.EqualTo(list), $"session {session}: every queued command once, in the order it was sent");
            }

            Assert.That(coalescedTwice, Is.Zero, "at most one coalesced command per session per tick");
            Assert.That(coalesced, Is.EquivalentTo(expectedMoves), "and it is the newest the session sent before the drain");
        });
    }
}
