using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests
{
    /// <summary>
    /// #1614: a <c>[ReplicatedMessage]</c> struct is sent as itself — bound once to the negotiated catalog, encoded through its generated accessors into a
    /// reused queue — with bytes identical to the name-keyed path, every contract drift refused at bind, and nothing allocated per send.
    /// </summary>
    [TestFixture]
    public class TypedCommandTests
    {
        private static readonly byte[] CatalogJson = GoldenFiles.ReadBin("catalog-commands");
        private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(CatalogJson));

        /// <summary>
        /// MMORPG's <c>MoveCmd</c> (i8, i8, u8, f32, f32): the typed message is byte-identical to the <see cref="RecordValues"/> one, and every
        /// <c>f32</c> — NaN, −0, the smallest subnormal, the largest finite — decodes to the bits that were sent.
        /// </summary>
        [Test]
        public async Task AMoveCmdEncodesAsTheNameKeyedPathDoesAndItsFloatsTravelBitExact()
        {
            float[] edges = [float.NaN, -0f, float.Epsilon, float.MaxValue, -1.5f, 0.1f, float.NegativeInfinity];
            await using var session = await Session.OpenAsync();
            var expected = new List<byte[]>();
            ushort seq = 0;
            foreach (var fx in edges)
            {
                var move = new MoveCmd { Forward = -1, Strafe = 1, Buttons = 0xA5, Fx = fx, Fz = -fx };
                Assert.That(session.Client.Enqueue(move), Is.EqualTo(++seq));
                Assert.That(await session.Client.FlushCommandsAsync(), Is.EqualTo(1));
                expected.Add(NameKeyed("MoveCmd", seq, new RecordValues
                {
                    ["Forward"] = FieldValue.Of(move.Forward),
                    ["Strafe"] = FieldValue.Of(move.Strafe),
                    ["Buttons"] = FieldValue.Of(move.Buttons),
                    ["Fx"] = FieldValue.Of(move.Fx),
                    ["Fz"] = FieldValue.Of(move.Fz),
                }));
            }

            Assert.That(session.Transport.Sent, Is.EqualTo(expected));
            for (var i = 0; i < edges.Length; i++)
            {
                var decoded = Decode(session.Transport.Sent[i]);
                var fx = (float)decoded[0]["Fx"];
                var fz = (float)decoded[0]["Fz"];
                Assert.That(BitConverter.SingleToInt32Bits(fx), Is.EqualTo(WireBits(edges[i])), $"Fx = {edges[i]}");
                Assert.That(BitConverter.SingleToInt32Bits(fz), Is.EqualTo(WireBits(-edges[i])), $"Fz = {-edges[i]}");
            }

            // Every float travels as its own bits, but a NaN as the canonical quiet one (W1): .NET's float.NaN is 0xFFC00000, the wire's 0x7FC00000.
            static int WireBits(float value) => float.IsNaN(value) ? 0x7FC00000 : BitConverter.SingleToInt32Bits(value);
        }

        /// <summary>
        /// Every scalar kind the path reads — bool and enum into the bit pack, quantizing codecs, varints, an exact f64, both 64-bit integers, a renamed
        /// field — over 500 seeded values: byte-identical to the name-keyed path, though the catalog's wire order is not the struct's.
        /// </summary>
        [Test]
        public async Task EveryScalarKindEncodesAsTheNameKeyedPathDoes()
        {
            var random = new Random(1614);
            await using var session = await Session.OpenAsync();
            var expected = new List<byte[]>();
            for (var i = 0; i < 500; i++)
            {
                var probe = new Probe
                {
                    Boost = random.Next(2) == 1,
                    Gait = (Gait)random.Next(3),
                    Heading = (float)(random.NextDouble() * 720 - 360),
                    Speed = (float)random.NextDouble(),
                    Budget = (ushort)random.Next(ushort.MaxValue + 1),
                    Delta = random.Next(int.MinValue, int.MaxValue),
                    Exact = random.NextDouble() * 1e12 - 5e11,
                    Big = random.NextInt64(long.MinValue, long.MaxValue),
                    EntityKey = (ulong)random.NextInt64() * 3,
                };

                var seq = session.Client.Enqueue(probe);
                Assert.That(seq, Is.GreaterThan(0));
                await session.Client.FlushCommandsAsync();
                expected.Add(NameKeyed("Probe", (ushort)seq, new RecordValues
                {
                    ["Boost"] = FieldValue.Of(probe.Boost ? 1 : 0),
                    ["Gait"] = FieldValue.Of((byte)probe.Gait),
                    ["Heading"] = FieldValue.Of(probe.Heading),
                    ["Speed"] = FieldValue.Of(probe.Speed),
                    ["Budget"] = FieldValue.Of(probe.Budget),
                    ["Delta"] = FieldValue.Of(probe.Delta),
                    ["Exact"] = FieldValue.Of(probe.Exact),
                    ["Big"] = FieldValue.OfInt64(probe.Big),
                    ["id"] = FieldValue.OfUInt64(probe.EntityKey),
                }));
            }

            Assert.That(session.Transport.Sent, Is.EqualTo(expected));
        }

        /// <summary>
        /// A struct that drifted from the catalog — a field renamed on one side, one the catalog lacks, one it carries as text, one the path cannot read —
        /// is refused at bind, before a byte is sent, with every mismatch named.
        /// </summary>
        [Test]
        public async Task ADriftedStructIsRefusedAtBindWithEveryMismatchNamed()
        {
            await using var session = await Session.OpenAsync();
            var drifted = Assert.Throws<MessageBindingException>(() => session.Client.Enqueue(new Drift.MoveCmd()));
            var text = Assert.Throws<MessageBindingException>(() => session.Client.Enqueue(new Say()));
            var opaque = Assert.Throws<MessageBindingException>(() => session.Client.Enqueue(new Hold()));
            var missing = Assert.Throws<InvalidOperationException>(() => session.Client.Enqueue(new Undeclared()));
            var retyped = Assert.Throws<MessageBindingException>(() => session.Client.Enqueue(new Drift.Hold()));

            Assert.Multiple(() =>
            {
                Assert.That(drifted.Problems, Has.Count.EqualTo(2));
                Assert.That(drifted.Message, Does.Contain("the catalog's field 'Fz'"));
                Assert.That(drifted.Message, Does.Contain("the struct's field 'Fy'"));
                Assert.That(text.Message, Does.Contain("field 'text' travels as 'str'"));
                Assert.That(opaque.Message, Does.Contain("field 'Item' has a type the typed send path cannot read"));
                Assert.That(missing.Message, Does.Contain("no command named 'Undeclared'"));
                Assert.That(retyped.Message, Does.Contain("field 'Item' is a single but travels as the integer codec 'u32'"));
                Assert.That(session.Transport.Sent, Is.Empty);
                Assert.That(session.Client.Enqueue(new MoveCmd()), Is.EqualTo(1), "a refused bind spends no seq");
            });
        }

        /// <summary>
        /// A frame's commands leave in one message, in seq order, whichever path queued them — typed, then name-keyed — and a batch above the catalog's
        /// message limit is split, every part within it, the seqs still consecutive.
        /// </summary>
        [Test]
        public async Task CommandsBatchInSeqOrderAndSplitAtTheMessageLimit()
        {
            await using var session = await Session.OpenAsync();
            session.Client.Enqueue(new MoveCmd { Forward = 1 });
            await session.Client.SendCommandAsync("MoveCmd", new RecordValues
            {
                ["Forward"] = FieldValue.Of(-1), ["Strafe"] = FieldValue.Of(0), ["Buttons"] = FieldValue.Of(0), ["Fx"] = FieldValue.Of(0), ["Fz"] = FieldValue.Of(1),
            });

            Assert.That(session.Transport.Sent, Has.Count.EqualTo(1), "the name-keyed send flushed what was queued before it, in one message");
            Assert.That(Seqs(session.Transport.Sent[0]), Is.EqualTo(new[] { 1, 2 }));

            session.Transport.Sent.Clear();
            for (var i = 0; i < 200; i++)
            {
                session.Client.Enqueue(new MoveCmd { Buttons = (byte)i });
            }

            var messages = await session.Client.FlushCommandsAsync();
            var seqs = new List<int>();
            foreach (var message in session.Transport.Sent)
            {
                Assert.That(message.Length, Is.LessThanOrEqualTo(Plan.Catalog.Limits.ClientMessageBytes));
                seqs.AddRange(Seqs(message));
            }

            Assert.That(messages, Is.GreaterThan(1).And.EqualTo(session.Transport.Sent.Count));
            Assert.That(seqs, Is.EqualTo(Enumerable(3, 200)));
        }

        /// <summary>
        /// A session that ends drops what was queued and not flushed — its seqs would never be acknowledged and its type indices name the old catalog — and
        /// with no session open nothing is queued at all.
        /// </summary>
        [Test]
        public async Task ASessionThatEndsDropsItsQueueAndNothingIsQueuedWithoutOne()
        {
            await using var session = await Session.OpenAsync();
            session.Client.Enqueue(new MoveCmd());
            session.Client.Enqueue(new MoveCmd());
            Assert.That(session.Client.QueuedCommandCount, Is.EqualTo(2));

            await session.Client.CloseAsync();
            Assert.Multiple(() =>
            {
                Assert.That(session.Client.QueuedCommandCount, Is.Zero);
                Assert.That(session.Client.Enqueue(new MoveCmd()), Is.EqualTo(CommandRefused.NotConnected));
                Assert.That(session.Client.QueuedCommandCount, Is.Zero);
            });
        }

        /// <summary>A <c>latest</c> command replaces the pending one of its type, and the catalog's rate is pre-checked locally.</summary>
        [Test]
        public async Task ALatestCommandCoalescesAndTheRateIsPreChecked()
        {
            await using var session = await Session.OpenAsync();
            var first = session.Client.Enqueue(new Pulse { Budget = 1 });
            session.Client.Enqueue(new MoveCmd());
            var second = session.Client.Enqueue(new Pulse { Budget = 2 });
            var third = session.Client.Enqueue(new Pulse { Budget = 3 });
            await session.Client.FlushCommandsAsync();

            var decoded = Decode(session.Transport.Sent[0]);
            Assert.Multiple(() =>
            {
                Assert.That(third, Is.EqualTo(CommandRefused.RateLimited), "Pulse's bucket holds 2");
                Assert.That(session.Client.CommandsCoalesced, Is.EqualTo(1));
                Assert.That(decoded, Has.Count.EqualTo(2), "the replaced Pulse never travels");
                Assert.That(Seqs(session.Transport.Sent[0]), Is.EqualTo(new[] { 2, second }), "the newest travels last, so seqs stay in order");
                Assert.That(decoded[1]["Budget"], Is.EqualTo(2));
                Assert.That(first, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The steady-state send path allocates nothing: 10 000 cycles of two enqueues and a flush, measured on the calling thread. The flush is asserted
        /// in Release only: a Debug build compiles every async state machine as a class, so any async method allocates there, whatever it does.
        /// </summary>
        [Test]
        public async Task TheTypedSendPathAllocatesNothingPerCommand()
        {
            await using var session = await Session.OpenAsync();
            session.Transport.Capture = false;
            var client = session.Client;
            long enqueued = 0, flushed = 0;
            for (var i = 0; i < 1000; i++)
            {
                Cycle(client, i, ref enqueued, ref flushed);
            }

            enqueued = flushed = 0;
            for (var i = 0; i < 10_000; i++)
            {
                Cycle(client, i, ref enqueued, ref flushed);
            }

            Assert.That(enqueued, Is.Zero, "bytes allocated by 20 000 enqueues");
#if !DEBUG
            Assert.That(flushed, Is.Zero, "bytes allocated by 10 000 flushes");
#endif
            Assert.That(session.Transport.Count, Is.EqualTo(11_000));

            static void Cycle(TyphonClient client, int i, ref long enqueued, ref long flushed)
            {
                var move = new MoveCmd { Forward = 1, Strafe = -1, Buttons = (byte)i, Fx = i * 0.25f, Fz = -i };
                var t0 = GC.GetAllocatedBytesForCurrentThread();
                client.Enqueue(move);
                client.Enqueue(move);
                var t1 = GC.GetAllocatedBytesForCurrentThread();
                var flush = client.FlushCommandsAsync();
                flushed += GC.GetAllocatedBytesForCurrentThread() - t1;
                enqueued += t1 - t0;
                if (!flush.IsCompletedSuccessfully || flush.Result != 1)
                {
                    throw new InvalidOperationException("the flush did not complete synchronously with one message");
                }
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private static byte[] NameKeyed(string command, ushort seq, RecordValues values)
        {
            var w = new WireWriter(new byte[1024]);
            CommandsMessage.Write(ref w, 0, [(Plan.CommandByName(command), seq, values)]);
            return w.Written.ToArray();
        }

        private static List<Dictionary<string, double>> Decode(byte[] message)
        {
            var sink = new CapturingSink();
            CommandsMessage.Read(message, Plan, ref sink);
            return sink.Commands;
        }

        private static List<int> Seqs(byte[] message)
        {
            var sink = new CapturingSink();
            CommandsMessage.Read(message, Plan, ref sink);
            return sink.Seqs;
        }

        private static IEnumerable<int> Enumerable(int from, int count)
        {
            for (var i = 0; i < count; i++)
            {
                yield return from + i;
            }
        }

        private sealed class Session : IAsyncDisposable
        {
            private Session(TyphonClient client, CapturingTransport transport)
            {
                Client = client;
                Transport = transport;
            }

            public TyphonClient Client { get; }

            public CapturingTransport Transport { get; }

            public static async Task<Session> OpenAsync()
            {
                var w = new WireWriter(new byte[ProtocolConstants.WelcomeMaxBytes]);
                new WelcomeMessage { SessionId = 1, CatalogHash = 42, CatalogJson = CatalogJson }.Write(ref w);
                var transport = new CapturingTransport(w.Written.ToArray());
                var client = new TyphonClient(new ClientOptions { Endpoint = new Uri("tcp://127.0.0.1:1/"), PingHz = 0 }, () => transport);
                await client.ConnectAsync();
                transport.Sent.Clear(); // the HELLO
                transport.Count = 0;
                return new Session(client, transport);
            }

            public ValueTask DisposeAsync() => Client.DisposeAsync();
        }

        /// <summary>Records what the client sends; with <see cref="Capture"/> off it only counts, so it allocates nothing itself.</summary>
        private sealed class CapturingTransport(byte[] welcome) : IClientTransport
        {
            private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
            private bool _welcomed;

            public List<byte[]> Sent { get; } = [];

            public bool Capture { get; set; } = true;

            public int Count { get; set; }

            public bool IsConnected { get; private set; }

            public ushort CloseCode { get; private set; }

            public string CloseDetail => null;

            public Task ConnectAsync(CancellationToken ct)
            {
                IsConnected = true;
                return Task.CompletedTask;
            }

            public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
            {
                Count++;
                if (Capture)
                {
                    Sent.Add(message.ToArray());
                }

                return ValueTask.CompletedTask;
            }

            public async ValueTask<byte[]> ReceiveAsync(CancellationToken ct)
            {
                if (!_welcomed)
                {
                    _welcomed = true;
                    return welcome;
                }

                try
                {
                    return await _inbound.Reader.ReadAsync(ct);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }

            public Task CloseAsync(ushort code, string reason)
            {
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

        private struct CapturingSink() : ICommandSink
        {
            public List<Dictionary<string, double>> Commands { get; } = [];

            public List<int> Seqs { get; } = [];

            public readonly void Command(MessagePlan type, ushort seq, uint clientTick)
            {
                Commands.Add([]);
                Seqs.Add(seq);
            }

            public readonly void Number(FieldPlan field, scoped ReadOnlySpan<double> components) => Commands[^1][field.Name] = components[0];

            public readonly void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components) => Commands[^1][field.Name] = components[0];

            public readonly void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
            {
            }

            public readonly void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
            {
            }

            public readonly void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
            {
            }

            public readonly void Collection(FieldPlan field, int total, int sent)
            {
            }

            public readonly void CollectionElement(FieldPlan field, int index)
            {
            }
        }
    }

    /// <summary>MMORPG slice 1's movement command, field for field.</summary>
    [ReplicatedMessage]
    public partial struct MoveCmd
    {
        public sbyte Forward;
        public sbyte Strafe;
        public byte Buttons;
        public float Fx;
        public float Fz;
    }

    public enum Gait : byte
    {
        Walk,
        Run,
        Sprint,
    }

    /// <summary>Every scalar kind, declared in an order the catalog's wire order is not.</summary>
    [ReplicatedMessage]
    public partial struct Probe
    {
        public bool Boost;
        public Gait Gait;
        public float Heading;
        public float Speed;
        public ushort Budget;
        public int Delta;
        public double Exact;
        public long Big;

        [Codec(CodecKind.U64, Name = "id")]
        public ulong EntityKey;
    }

    /// <summary>A <c>latest</c>, rate-limited command.</summary>
    [ReplicatedMessage]
    public partial struct Pulse
    {
        public ushort Budget;
    }

    /// <summary>catalog-commands' <c>Say</c>, whose <c>text</c> travels as a <c>str</c> a scalar field cannot supply.</summary>
    [ReplicatedMessage]
    public partial struct Say
    {
#pragma warning disable IDE1006 // the catalog's names
        public byte channel;
        public int text;
#pragma warning restore IDE1006
    }

    public struct Opaque
    {
        public int A;
    }

    /// <summary>A field of a type the path cannot read.</summary>
    [ReplicatedMessage]
    public partial struct Hold
    {
        public Opaque Item;
    }

    /// <summary>A command the catalog does not declare.</summary>
    [ReplicatedMessage]
    public partial struct Undeclared
    {
        public int A;
    }
}

namespace Typhon.Client.Tests.Drift
{
    /// <summary>Hold with its u32 declared a float: a fraction would be refused on every send, so the binding refuses it once.</summary>
    [ReplicatedMessage]
    public partial struct Hold
    {
        public float Item;
    }

    /// <summary>MoveCmd with <c>Fz</c> mistyped <c>Fy</c>: the typo the typed path turns into a refusal at bind.</summary>
    [ReplicatedMessage]
    public partial struct MoveCmd
    {
        public sbyte Forward;
        public sbyte Strafe;
        public byte Buttons;
        public float Fx;
        public float Fy;
    }
}
