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
    /// the store; it reads the copy. The copy holds what one ship's view holds, with the motion segments and headings render time can still reach — render
    /// time trails the newest frame, so the newest segment is not the one to draw. Snapshots a game gives back (<see cref="Recycle"/>) are refilled, so a
    /// steady stream allocates nothing.
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
        private readonly Stack<BeltSnapshot> _pool = new Stack<BeltSnapshot>();
        private BeltSnapshot _latest;

        // Receive thread only: each ship's recent headings (a field holds only its newest value), and how far back render time can reach.
        private readonly Dictionary<uint, HeadingTrack> _headings = new Dictionary<uint, HeadingTrack>();
        private readonly List<uint> _gone = new List<uint>();
        private long _cutoff;

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

        /// <summary>Gives back a snapshot the game is done with, to be refilled for a later frame. Nothing may read it afterwards.</summary>
        public void Recycle(BeltSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_pool.Count < 4)
                {
                    _pool.Push(snapshot);
                }
            }
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

        /// <summary>How far this pilot wants to see, in metres. The server clamps it to the view's range, 300–1 200 m.</summary>
        public void ViewRadius(float radiusM) => Send("ViewRadius", new RecordValues { ["radius"] = FieldValue.Of(radiusM) });

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
            BeltSnapshot snapshot = null;
            lock (_gate)
            {
                if (_pool.Count > 0)
                {
                    snapshot = _pool.Pop();
                }
            }

            snapshot ??= new BeltSnapshot();
            snapshot.Clear();
            snapshot.Tick = store.Tick;
            snapshot.PeriodMs = store.PeriodUs / 1000.0;
            snapshot.SelfNetId = store.Self.NetId;
            snapshot.BytesReceived = _client.BytesReceived;
            snapshot.Frames = store.Frames;

            // Render time trails the newest tick by the clock's render delay, at most this many ticks; a segment or a heading superseded before it is
            // never drawn again.
            _cutoff = store.Tick - ((long)Math.Ceiling(SegmentRing.DefaultMaxRenderDelayMs * 1000.0 / Math.Max(1, store.PeriodUs)) + 2);

            _collector.TakeFrame(_frameEvents);
            Copy(store, _asteroidPlan, snapshot, snapshot.Asteroids, ship: false);
            Copy(store, _shipPlan, snapshot, snapshot.Ships, ship: true);
            ForgetGoneShips(snapshot);
            ReadOwner(store.Self, snapshot);

            lock (_gate)
            {
                // A frame nobody took is replaced: it was never handed out, so it is free to refill.
                if (_latest != null && _pool.Count < 4)
                {
                    _pool.Push(_latest);
                }

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

        private void Copy(WorldStore store, ArchetypePlan plan, BeltSnapshot snapshot, List<EntityView> into, bool ship)
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
                CopySegments(a, slot, snapshot, ref view);
                if (ship)
                {
                    view.Heading = Number(a, _heading, slot);
                    view.Target = (uint)Number(a, _target, slot);
                    view.Drone = Number(a, _drone, slot) != 0;
                    CopyHeadings(store.Tick, snapshot, ref view);
                }
                else
                {
                    view.Ore = (int)Number(a, _ore, slot);
                    view.Size = (int)Number(a, _size, slot);
                }

                into.Add(view);
            }
        }

        // The slot's segments, oldest first, from the one in force at the cutoff on: a steady drifter is one segment, a turning ship a handful.
        private void CopySegments(ArchetypeStore a, int slot, BeltSnapshot snapshot, ref EntityView view)
        {
            var ring = a.Segments;
            var held = ring.Held(slot);
            view.SegmentFirst = snapshot.Segments.Count;
            if (ring.Dims < 2 || held == 0)
            {
                return;
            }

            var entry = (ring.Head(slot) - held + 1 + ring.Depth) % ring.Depth;
            for (var k = 0; k < held; k++, entry = (entry + 1) % ring.Depth)
            {
                var next = (entry + 1) % ring.Depth;
                if (k < held - 1 && ring.T0(slot, next) <= _cutoff)
                {
                    continue;
                }

                var p = ring.Position(slot, entry);
                var v = ring.Velocity(slot, entry);
                snapshot.Segments.Add(new MotionSegment
                {
                    T0 = ring.T0(slot, entry), Epoch = ring.Epoch(slot, entry), X = p[0], Y = p[1], Vx = v[0], Vy = v[1],
                });
            }

            view.SegmentCount = snapshot.Segments.Count - view.SegmentFirst;
        }

        // A field holds only its newest value, so the link keeps each ship's recent headings itself: drawn at render time, the nose turns with the path.
        private void CopyHeadings(long tick, BeltSnapshot snapshot, ref EntityView view)
        {
            if (!_headings.TryGetValue(view.NetId, out var track))
            {
                track = new HeadingTrack();
                _headings.Add(view.NetId, track);
            }

            track.SeenAt = tick;
            var samples = track.Samples;
            if (samples.Count == 0 || samples[samples.Count - 1].Heading != view.Heading)
            {
                samples.Add(new HeadingSample { Tick = tick, Heading = view.Heading });
            }

            var stale = 0;
            while (stale < samples.Count - 1 && samples[stale + 1].Tick <= _cutoff)
            {
                stale++;
            }

            samples.RemoveRange(0, stale);
            view.HeadingFirst = snapshot.Headings.Count;
            view.HeadingCount = samples.Count;
            snapshot.Headings.AddRange(samples);
        }

        private void ForgetGoneShips(BeltSnapshot snapshot)
        {
            _gone.Clear();
            foreach (var pair in _headings)
            {
                if (pair.Value.SeenAt != snapshot.Tick)
                {
                    _gone.Add(pair.Key);
                }
            }

            foreach (var netId in _gone)
            {
                _headings.Remove(netId);
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

        private sealed class HeadingTrack
        {
            public readonly List<HeadingSample> Samples = new List<HeadingSample>();
            public long SeenAt;
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
