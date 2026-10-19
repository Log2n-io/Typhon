// Plain C# (C# 9, netstandard2.1): compiled by Unity and, linked, by the headless bot and the tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Protocol;

namespace AsteroidField.Client
{
    /// <summary>
    /// The connection to an Asteroid Field server, made for a game loop: the SDK applies frames on its own receive thread, this copies each one out into
    /// a <see cref="BeltSnapshot"/> there, and the game's thread takes the newest whenever it wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a copy.</b> <see cref="TyphonClient"/> owns its store on the thread that receives — <see cref="TyphonClient.FrameApplied"/> is the one moment
    /// it holds a whole frame, and the next frame rewrites it in place. A Unity <c>Update</c> runs on another thread at another rate, so it must never read
    /// the store; it reads the copy. The copy is small — what one ship's 300 m view holds — and taking it is all the receive thread does here.
    /// </para>
    /// <para>Thread-safe: <see cref="TryTakeLatest"/>, the drains and the commands may be called from any thread.</para>
    /// </remarks>
    public sealed class BeltLink : IAsyncDisposable
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly TyphonClient _client;
        private readonly object _gate = new object();
        private readonly Queue<ReceivedEvent> _events = new Queue<ReceivedEvent>();
        private readonly Queue<FrameArrival> _arrivals = new Queue<FrameArrival>();

        // Events are decoded on the receive thread while a frame applies; the collector keeps them until the frame is whole (OnFrame). It is the SDK's,
        // so this file implements no IFieldSink — whose `scoped` parameters a C# 9 compiler (Unity's) does not know.
        private readonly EventCollector _collector = new EventCollector();
        private readonly List<ReceivedEvent> _frameEvents = new List<ReceivedEvent>();
        private BeltSnapshot _latest;

        // The fields this demo reads, resolved once per catalog (a new WELCOME can carry a new one).
        private CatalogPlan _plan;
        private ArchetypePlan _asteroidPlan;
        private ArchetypePlan _shipPlan;
        private FieldPlan _ore;
        private FieldPlan _size;
        private FieldPlan _heading;
        private FieldPlan _target;
        private FieldPlan _drone;

        public BeltLink(string host, int port, string token = "")
        {
            _client = new TyphonClient(new ClientOptions
            {
                Endpoint = new Uri($"tcp://{host}:{port}"),
                Kind = "pilot",
                Token = token,
                Events = _collector,
            });
            _client.FrameApplied += OnFrame;
            _client.Fault += e => LastError = e.Message;
            _client.Disconnected += (code, willReconnect) => LastError = $"disconnected ({code}){(willReconnect ? ", reconnecting" : "")}";
        }

        /// <summary>Milliseconds on one monotonic clock, the one frame arrivals are stamped with: give it to the render clock.</summary>
        public static double NowMs() => Clock.Elapsed.TotalMilliseconds;

        /// <summary>The last fault or close, for the HUD; null while all is well.</summary>
        public string LastError { get; private set; }

        public Task ConnectAsync() => _client.ConnectAsync();

        /// <summary>The newest snapshot since the last call, if a frame has arrived since.</summary>
        public bool TryTakeLatest(out BeltSnapshot snapshot)
        {
            lock (_gate)
            {
                snapshot = _latest;
                _latest = null;
            }

            return snapshot != null;
        }

        /// <summary>Moves every event received since the last call into <paramref name="into"/>, in order. Entity fields hold netIds.</summary>
        public void DrainEvents(List<ReceivedEvent> into)
        {
            lock (_gate)
            {
                while (_events.Count > 0)
                {
                    into.Add(_events.Dequeue());
                }
            }
        }

        /// <summary>Moves every frame arrival since the last call into <paramref name="into"/>: the render clock needs each one, not only the newest.</summary>
        public void DrainArrivals(List<FrameArrival> into)
        {
            lock (_gate)
            {
                while (_arrivals.Count > 0)
                {
                    into.Add(_arrivals.Dequeue());
                }
            }
        }

        /// <summary>Stick input; send it every frame — the server keeps only the newest.</summary>
        public void Steer(float thrust, float turn) => Send("Steer", new RecordValues { ["thrust"] = FieldValue.Of(thrust), ["turn"] = FieldValue.Of(turn) });

        /// <summary>Mine the asteroid with this netId; 0 stops. The server refuses an asteroid this client was never shown.</summary>
        public void Mine(uint asteroid) => Send("Mine", new RecordValues { ["target"] = FieldValue.Of(asteroid) });

        public void Sell() => Send("Sell", new RecordValues { ["unused"] = FieldValue.Of(0d) });

        public ValueTask DisposeAsync() => _client.DisposeAsync();

        private void Send(string command, RecordValues values)
        {
            if (_client.Plan == null)
            {
                return;
            }

            // Fire and forget: the SDK serializes sends itself. A failure is the HUD's to show, never the game loop's to wait on.
            _client.SendCommandAsync(command, values).ContinueWith(t => LastError = t.Exception?.GetBaseException().Message,
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // ── The receive thread ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private void OnFrame(WorldStore store)
        {
            Resolve(store.Plan);
            var snapshot = new BeltSnapshot
            {
                Tick = store.Tick,
                PeriodMs = store.PeriodUs / 1000.0,
                SelfNetId = store.Self.NetId,
                BytesReceived = _client.BytesReceived,
                Frames = store.Frames,
            };

            _collector.TakeFrame(_frameEvents);
            Copy(store, _asteroidPlan, snapshot.Asteroids, ship: false);
            Copy(store, _shipPlan, snapshot.Ships, ship: true);
            ReadOwner(store.Self, snapshot);

            lock (_gate)
            {
                _latest = snapshot;
                _arrivals.Enqueue(new FrameArrival(store.Tick, NowMs()));
                foreach (var e in _frameEvents)
                {
                    _events.Enqueue(e);
                }
            }

            _frameEvents.Clear();
        }

        private void Resolve(CatalogPlan plan)
        {
            if (ReferenceEquals(plan, _plan))
            {
                return;
            }

            _plan = plan;
            _asteroidPlan = plan.ArchetypeByName("Asteroid");
            _shipPlan = plan.ArchetypeByName("Ship");
            _ore = Field(_asteroidPlan, "ore");
            _size = Field(_asteroidPlan, "size");
            _heading = Field(_shipPlan, "heading");
            _target = Field(_shipPlan, "target");
            _drone = Field(_shipPlan, "drone");
        }

        private void Copy(WorldStore store, ArchetypePlan plan, List<EntityView> into, bool ship)
        {
            if (plan == null)
            {
                return;
            }

            var a = store.Archetypes[plan.Idx];
            for (var i = 0; i < a.LiveCount; i++)
            {
                var slot = a.Live[i];
                var position = a.HeadPosition(slot);
                var velocity = a.HeadVelocity(slot);
                var view = new EntityView
                {
                    NetId = a.NetIds[slot],
                    X0 = position.Length > 0 ? position[0] : 0,
                    Y0 = position.Length > 1 ? position[1] : 0,
                    Vx = velocity.Length > 0 ? velocity[0] : 0,
                    Vy = velocity.Length > 1 ? velocity[1] : 0,
                    T0 = a.HeadT0(slot),
                };
                if (ship)
                {
                    view.Heading = Number(a, _heading, slot);
                    view.Target = (uint)Number(a, _target, slot);
                    view.Drone = Number(a, _drone, slot) != 0;
                }
                else
                {
                    view.Ore = (int)Number(a, _ore, slot);
                    view.Size = (int)Number(a, _size, slot);
                }

                into.Add(view);
            }
        }

        private static void ReadOwner(SelfState self, BeltSnapshot into)
        {
            if (!self.Received || self.Archetype == null)
            {
                return;
            }

            foreach (var f in self.Archetype.OwnerFields)
            {
                double value;
                if (f.ValueKind == FieldValueKind.Number && self.Numbers[f.Ordinal] is double[] numbers && numbers.Length > 0)
                {
                    value = numbers[0];
                }
                else if (f.ValueKind == FieldValueKind.Integer64 && self.Integers[f.Ordinal] is ulong[] integers && integers.Length > 0)
                {
                    value = unchecked((long)integers[0]);
                }
                else
                {
                    continue;
                }

                if (f.Name == "cargo")
                {
                    into.Cargo = (int)value;
                }
                else if (f.Name == "credits")
                {
                    into.Credits = (int)value;
                }
            }
        }

        private static FieldPlan Field(ArchetypePlan plan, string name) => plan == null ? null : Array.Find(plan.Fields, f => f.Name == name);

        private static double Number(ArchetypeStore a, FieldPlan f, int slot)
        {
            if (f == null)
            {
                return 0;
            }

            if (f.ValueKind == FieldValueKind.Number)
            {
                var column = a.Numbers[f.Ordinal];
                return column == null ? 0 : column[slot * f.Components];
            }

            if (f.ValueKind == FieldValueKind.Integer64)
            {
                var column = a.Integers[f.Ordinal];
                return column == null ? 0 : unchecked((long)column[slot * f.Components]);
            }

            return 0;
        }
    }
}
