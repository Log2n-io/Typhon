using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

/// <summary>A coalesced command: where the client wants to go. Only the newest of these can matter.</summary>
/// <remarks>
/// The fields are written by the DECODER, through offsets it measured at <c>Start</c>, so nothing in this assembly ever assigns them — which is what CS0649
/// is for, and why it is suppressed here rather than worked around with a constructor the wire would never use.
/// </remarks>
#pragma warning disable CS0649
struct MoveIntent
{
    public float X;
    public float Z;
    public ushort Speed;
}

/// <summary>A queued command: what the client wants to hit. Every one of these matters, in the order it was sent.</summary>
struct FireAt
{
    public uint Target;
    public uint Shot;
}
#pragma warning restore CS0649

/// <summary>
/// P1-05 — the Engine-Pre drain: a transport thread's <c>COMMANDS</c> message reaches an application system inside one tick, once, in per-session order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The harness drives the real ingress path single-threaded, without a scheduler</b>, and that is deliberate rather than a shortcut. The properties under
/// test — exactly one tick, per-session order, coalescing, overflow, allocation — are all statements about what the drain does with what it was given, and
/// running them against the tick loop would replace a decision the test makes with one the scheduler makes. The wiring itself (that Engine-Pre really carries
/// this system and really dispatches it) is asserted separately, against a live runtime, by <see cref="TheEnginePreDrainRunsInALiveRuntime"/> — because
/// "a system that silently never runs" is exactly the failure a harness cannot see.
/// </para>
/// <para>
/// <b>Messages are encoded with <c>Typhon.Protocol</c>'s own writer</b>, never spelled out as bytes here. A test that writes its own wire format is green in
/// the same build as a red encoder.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class IngressDrainTests : TestBase<IngressDrainTests>
{
    // ── the harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ingress path, assembled exactly as <c>SubscriptionsRuntime</c> assembles it, with the tick under the test's control.</summary>
    private sealed class Harness : IDisposable
    {
        private long _tick;

        public Harness(int maxSessions = 256, int ringBytes = 4096)
        {
            Registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "IngressDrainTests" });
            Allocator = new MemoryAllocator(Registry, new MemoryAllocatorOptions { Name = "IngressDrainAllocator" });

            var options = new SubscriptionsOptions
            {
                MaxSessions = maxSessions,
                IngressRingBytes = ringBytes,
                IngressPoolBudgetBytes = 8L * 1024 * 1024,
            };

            Subs = new SubscriptionsRegistry(options);
            Subs.Sessions.Kinds("player");
            Subs.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);
            Subs.Command<MoveIntent>(c => c
                .Coalesce(CommandCoalesce.LatestPerSession)
                .Rate(10_000, 20_000)
                .Field(m => m.X, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Z, Codec.Quant(-8192, 8192, 24))
                .Field(m => m.Speed, Codec.U16));
            Subs.Command<FireAt>(c => c
                .Rate(10_000, 20_000)
                .Field(f => f.Target, Codec.EntityRef)
                .Field(f => f.Shot, Codec.VarUInt));
            Subs.Freeze();

            var export = CatalogBuilder.Build(Subs, [], CatalogBuilder.DefaultAppName, appRevision: 0, tickPeriodUs: 10_000, systemNames: []);
            Plan = CatalogPlan.Compile(export.Canonical);

            SessionTable = new SessionTable("Sessions", Registry.Runtime, Allocator, options, Subs.Sessions.SessionEvents);
            CommandTypes = CommandRegistry.Build(Subs, Plan);
            Pool = new IngressRingPool("IngressRings", Registry.Runtime, Allocator, options);
            Ingress = new SubscriptionsIngress(SessionTable, Subs, CommandTypes, new CommandTypeBuffers(CommandTypes, maxSessions), Pool, maxSessions);
            Api = new SubscriptionsCommands(Ingress);
        }

        public ResourceRegistry Registry { get; }

        public MemoryAllocator Allocator { get; }

        public SubscriptionsRegistry Subs { get; }

        public CatalogPlan Plan { get; }

        public SessionTable SessionTable { get; }

        public CommandRegistry CommandTypes { get; }

        public IngressRingPool Pool { get; }

        public SubscriptionsIngress Ingress { get; }

        public SubscriptionsCommands Api { get; }

        public SubscriptionsContext Context { get; } = new();

        /// <summary>Admits a session, as a completed handshake would.</summary>
        public SessionId Admit()
        {
            var request = new AdmissionRequest("player", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
            Assert.That(SessionTable.TryAdmit(Subs.Sessions, request, out var session, out _, out _), Is.True, "the harness could not admit a session");
            return session;
        }

        /// <summary>Runs one tick's ingress: the prologue, then every drain chunk, exactly as the Engine-Pre system dispatches them.</summary>
        /// <param name="workers">The worker pool's width, which bounds the chunk count.</param>
        /// <returns>How many chunks the drain dispatched.</returns>
        public int Tick(int workers = 1)
        {
            Context.Reset(++_tick, workers);
            var chunks = Ingress.BeginTick(Context);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                Ingress.DrainChunk(chunk, chunks);
            }

            // An `if` rather than `Assert.That`: a constraint object and an interpolated message are built on every call, and the allocation test measures
            // this method. The guard is the same guard; it just stops charging the measurement for it.
            if (Ingress.DrainFaults != 0)
            {
                Assert.Fail($"the drain threw: {Ingress.LastDrainFault}");
            }

            return chunks;
        }

        public void Dispose()
        {
            Ingress.Dispose();
            Pool.Dispose();
            SessionTable.Dispose();
            Allocator.Dispose();
            Registry.Dispose();
        }
    }

    /// <summary>Encodes a <c>COMMANDS</c> message through the protocol's own writer.</summary>
    private static byte[] Encode(CatalogPlan plan, uint clientTick, params (string Name, ushort Seq, RecordValues Values)[] commands)
    {
        var list = new List<(MessagePlan, ushort, RecordValues)>(commands.Length);
        foreach (var (name, seq, values) in commands)
        {
            list.Add((plan.CommandByName(name), seq, values));
        }

        var buffer = new byte[8192];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, clientTick, list);
        return writer.Written.ToArray();
    }

    private static RecordValues Fire(uint target, uint shot) => new()
    {
        ["Target"] = FieldValue.Of(target),
        ["Shot"] = FieldValue.Of(shot),
    };

    private static RecordValues Move(double x, double z, int speed) => new()
    {
        ["X"] = FieldValue.Of(x),
        ["Z"] = FieldValue.Of(z),
        ["Speed"] = FieldValue.Of(speed),
    };

    // ── SUB-08 ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A randomized multi-session load: every command is visible in exactly one tick, and each session's commands keep the order it sent them.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-08")]
    public void EachCommandIsVisibleInExactlyOneTickInSessionOrder()
    {
        using var harness = new Harness();
        const int Sessions = 12;

        var sessions = new SessionId[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            sessions[i] = harness.Admit();
        }

        harness.Tick();

        var random = new Random(20260917);
        var expected = new Dictionary<uint, List<uint>>();
        var observed = new List<(uint Session, uint Shot)>();
        var nextShot = new uint[Sessions];
        ushort seq = 0;

        for (var round = 0; round < 6; round++)
        {
            // A shuffled send order, so the drain cannot be right by accident of the order sessions were admitted in.
            var order = new int[Sessions];
            for (var i = 0; i < Sessions; i++)
            {
                order[i] = i;
            }

            for (var i = Sessions - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            foreach (var index in order)
            {
                var count = 1 + random.Next(4);
                var batch = new (string, ushort, RecordValues)[count];
                if (!expected.TryGetValue(sessions[index].Value, out var list))
                {
                    expected[sessions[index].Value] = list = [];
                }

                for (var c = 0; c < count; c++)
                {
                    var shot = nextShot[index]++;
                    list.Add(shot);
                    batch[c] = ("FireAt", seq++, Fire(target: 1000 + shot, shot));
                }

                harness.Ingress.OnCommands(sessions[index], Encode(harness.Plan, (uint)round, batch));
            }

            harness.Tick(workers: 4);
            Collect(harness, observed);
        }

        // Three more empty ticks: a command delivered twice would show up here, and one delivered zero times would leave the expectation short.
        for (var i = 0; i < 3; i++)
        {
            harness.Tick(workers: 4);
            Collect(harness, observed);
        }

        AssertExactlyOncePerSessionInOrder(observed, expected);
    }

    /// <summary>
    /// The genuineness proof for <see cref="EachCommandIsVisibleInExactlyOneTickInSessionOrder"/>: its own assertion must reject a duplicate and a swap.
    /// </summary>
    [Test]
    [RuleMutant("SUB-08")]
    public void ADuplicatedOrReorderedDeliveryIsDetected()
    {
        var expected = new Dictionary<uint, List<uint>> { [7] = [0, 1, 2], [9] = [0, 1] };

        Assert.DoesNotThrow(
            () => AssertExactlyOncePerSessionInOrder([(7, 0), (9, 0), (7, 1), (9, 1), (7, 2)], expected),
            "a correct observation log must pass the verifier's own assertion, or the mutants below prove nothing");

        Assert.Throws<AssertionException>(
            () => AssertExactlyOncePerSessionInOrder([(7, 0), (9, 0), (7, 1), (7, 1), (9, 1), (7, 2)], expected),
            "a command delivered twice was not detected");

        Assert.Throws<AssertionException>(
            () => AssertExactlyOncePerSessionInOrder([(7, 1), (9, 0), (7, 0), (9, 1), (7, 2)], expected),
            "a session's commands delivered out of order were not detected");

        Assert.Throws<AssertionException>(
            () => AssertExactlyOncePerSessionInOrder([(7, 0), (9, 0), (9, 1), (7, 2)], expected),
            "a command delivered zero times was not detected");
    }

    /// <summary>Appends this tick's <c>FireAt</c> commands to the observation log, in batch order.</summary>
    private static void Collect(Harness harness, List<(uint Session, uint Shot)> observed)
    {
        foreach (ref readonly var command in harness.Api.Commands<FireAt>())
        {
            observed.Add((command.Session.Value, command.Value.Shot));
        }
    }

    /// <summary>Requires every expected command to appear exactly once, and each session's to appear in the order it sent them.</summary>
    private static void AssertExactlyOncePerSessionInOrder(List<(uint Session, uint Shot)> observed, Dictionary<uint, List<uint>> expected)
    {
        var bySession = new Dictionary<uint, List<uint>>();
        foreach (var (session, shot) in observed)
        {
            if (!bySession.TryGetValue(session, out var list))
            {
                bySession[session] = list = [];
            }

            list.Add(shot);
        }

        Assert.That(bySession.Count, Is.EqualTo(expected.Count), "a session's commands went missing entirely, or arrived under another identity");
        foreach (var (session, wanted) in expected)
        {
            Assert.That(bySession.TryGetValue(session, out var got), Is.True, $"session {session} received none of its {wanted.Count} commands");
            Assert.That(got, Is.EqualTo(wanted),
                $"session {session}: expected exactly [{string.Join(", ", wanted)}] in that order, got [{string.Join(", ", got)}]");
        }
    }

    /// <summary>A coalesced type delivers one record per session per tick, and it is the newest that session sent.</summary>
    [Test]
    public void ACoalescedTypeDeliversOnlyTheNewestPerSession()
    {
        using var harness = new Harness();
        var a = harness.Admit();
        var b = harness.Admit();
        harness.Tick();

        harness.Ingress.OnCommands(a, Encode(harness.Plan, 1,
            ("MoveIntent", 1, Move(-10, -10, 1)),
            ("MoveIntent", 2, Move(20, 20, 2)),
            ("MoveIntent", 3, Move(33, 44, 3))));
        harness.Ingress.OnCommands(b, Encode(harness.Plan, 1, ("MoveIntent", 4, Move(-1, -2, 9))));

        harness.Tick();

        var batch = harness.Api.Commands<MoveIntent>();
        Assert.That(batch.Count, Is.EqualTo(2), "a coalesced type carries one record per session, not one per arrival");

        Assert.Multiple(() =>
        {
            Assert.That(batch.TryGetLatest(a, out var latestA), Is.True);
            Assert.That(latestA.Seq, Is.EqualTo(3), "the newest arrival is the one that survives");
            Assert.That(latestA.Value.Speed, Is.EqualTo(3));
            Assert.That(latestA.Value.X, Is.EqualTo(33f).Within(0.01f));

            Assert.That(batch.TryGetLatest(b, out var latestB), Is.True);
            Assert.That(latestB.Seq, Is.EqualTo(4));
            Assert.That(latestB.Value.Speed, Is.EqualTo(9));

            Assert.That(harness.Api.TryGetLastSeq(a, out var lastSeqA), Is.True);
            Assert.That(lastSeqA, Is.EqualTo(3), "lastSeq is the highest drained, coalesced-away arrivals included");
        });

        // And it is gone the next tick: a command is visible for exactly its own.
        harness.Tick();
        Assert.That(harness.Api.Commands<MoveIntent>().Count, Is.Zero, "a coalesced record must not survive into the following tick");
        Assert.That(harness.Api.Commands<MoveIntent>().TryGetLatest(a, out _), Is.False);
    }

    /// <summary>A queued type keeps every arrival, and <c>ForSession</c> hands back exactly that session's run.</summary>
    [Test]
    public void ForSessionNarrowsToOneSessionsCommandsInOrder()
    {
        using var harness = new Harness();
        var a = harness.Admit();
        var b = harness.Admit();
        harness.Tick();

        harness.Ingress.OnCommands(a, Encode(harness.Plan, 1, ("FireAt", 1, Fire(10, 0)), ("FireAt", 2, Fire(11, 1))));
        harness.Ingress.OnCommands(b, Encode(harness.Plan, 1, ("FireAt", 3, Fire(20, 100))));
        harness.Ingress.OnCommands(a, Encode(harness.Plan, 2, ("FireAt", 4, Fire(12, 2))));
        harness.Tick();

        var shotsOfA = new List<uint>();
        foreach (ref readonly var command in harness.Api.Commands<FireAt>().ForSession(a))
        {
            Assert.That(command.Session, Is.EqualTo(a), "ForSession must not hand back another session's commands");
            shotsOfA.Add(command.Value.Shot);
        }

        var shotsOfB = new List<uint>();
        foreach (ref readonly var command in harness.Api.Commands<FireAt>().ForSession(b))
        {
            shotsOfB.Add(command.Value.Shot);
        }

        Assert.Multiple(() =>
        {
            Assert.That(shotsOfA, Is.EqualTo(new List<uint> { 0, 1, 2 }), "a session's commands keep the order it sent them, across messages");
            Assert.That(shotsOfB, Is.EqualTo(new List<uint> { 100 }));
            Assert.That(harness.Api.Commands<FireAt>().Count, Is.EqualTo(4));
        });
    }

    /// <summary>
    /// <c>TryGetLatest</c> does not read the batch: its cost is the same for a session in a hundred-command tick and in a ten-thousand-command one.
    /// </summary>
    /// <remarks>
    /// The structure is what makes it O(1) — an index by session slot, stamped with the tick — and the measurement is the falsifiable part of that claim. The
    /// bound is deliberately loose: a linear lookup over a batch a hundred times larger would be a hundred times slower, so anything under an order of
    /// magnitude cannot be a scan.
    /// </remarks>
    [Test]
    public void TryGetLatestDoesNotScaleWithTheBatchSize()
    {
        const int Iterations = 20_000;

        var smallNs = MeasureTryGetLatest(sessions: 4, perSession: 25, Iterations);
        var largeNs = MeasureTryGetLatest(sessions: 100, perSession: 100, Iterations);

        Assert.That(largeNs, Is.LessThan(Math.Max(smallNs, 1) * 10),
            $"TryGetLatest cost {smallNs} ns over a 100-command tick and {largeNs} ns over a 10 000-command one; that is a scan, not an index");
    }

    private static double MeasureTryGetLatest(int sessions, int perSession, int iterations)
    {
        using var harness = new Harness(maxSessions: 512, ringBytes: 64 * 1024);
        var ids = new SessionId[sessions];
        for (var i = 0; i < sessions; i++)
        {
            ids[i] = harness.Admit();
        }

        harness.Tick();

        for (var i = 0; i < sessions; i++)
        {
            for (var c = 0; c < perSession; c++)
            {
                harness.Ingress.OnCommands(ids[i], Encode(harness.Plan, 1, ("MoveIntent", (ushort)c, Move(c, c, c))));
            }
        }

        harness.Tick();

        var batch = harness.Api.Commands<MoveIntent>();
        var last = ids[sessions - 1];

        // Warm up, so the measurement is of the lookup rather than of the tier-0 code that runs once.
        for (var i = 0; i < 1000; i++)
        {
            batch.TryGetLatest(last, out _);
        }

        var watch = Stopwatch.StartNew();
        var hits = 0;
        for (var i = 0; i < iterations; i++)
        {
            if (batch.TryGetLatest(last, out _))
            {
                hits++;
            }
        }

        watch.Stop();
        Assert.That(hits, Is.EqualTo(iterations), "the lookup has to succeed, or the measurement is of a miss");
        return watch.Elapsed.TotalNanoseconds / iterations;
    }

    /// <summary>A ring with no room drops, counts, and neither blocks the producer nor throws; the tick after, the path is working again.</summary>
    [Test]
    public void AFullRingDropsAndCountsWithoutBlockingOrThrowing()
    {
        using var harness = new Harness(ringBytes: 256);
        var session = harness.Admit();
        harness.Tick();

        // Far more than a 256-byte ring can hold, with no tick in between to drain it.
        var watch = Stopwatch.StartNew();
        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                harness.Ingress.OnCommands(session, Encode(harness.Plan, 1, ("FireAt", (ushort)i, Fire(1, (uint)i))));
            }
        }, "a full ring must never throw at the producer — it is a network thread");
        watch.Stop();

        var row = harness.Ingress.RowOf(session);
        Assert.That(row, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(row.DroppedCommands, Is.GreaterThan(0), "a ring far past its capacity has to have dropped something");
            Assert.That(row.Ring.DroppedRecords, Is.EqualTo(row.DroppedCommands), "the ring's own counter and the session's must agree");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "the producer waited on the tick, which is the one thing it must never do");
        });

        harness.Tick();
        var delivered = harness.Api.Commands<FireAt>().Count;
        Assert.That(delivered, Is.GreaterThan(0), "what fitted still has to arrive");
        Assert.That(delivered + row.DroppedCommands, Is.EqualTo(400), "every command either arrived or was counted as dropped — none vanished silently");

        // And the session is still usable: a drop is backpressure, not a broken connection.
        harness.Ingress.OnCommands(session, Encode(harness.Plan, 2, ("FireAt", 999, Fire(7, 7))));
        harness.Tick();
        Assert.That(harness.Api.Commands<FireAt>().Count, Is.EqualTo(1));
    }

    /// <summary>
    /// A session's ring lives in native memory, so a full compacting collection cannot disturb it.
    /// </summary>
    /// <remarks>
    /// The property whose absence was the SWG x64 <c>0x80131506</c> crash: the pinned object heap stops the GC MOVING an array, not freeing it, and a pointer
    /// into one outlived the array. A record written before a gen-2 compacting collection and drained after it is the observable form of "this is not a GC
    /// object".
    /// </remarks>
    [Test]
    public void TheSessionRingsBufferIsNativeMemory()
    {
        using var harness = new Harness();
        var session = harness.Admit();
        harness.Tick();

        harness.Ingress.OnCommands(session, Encode(harness.Plan, 1, ("FireAt", 1, Fire(4242, 77))));
        Assert.That(harness.Pool.CommittedBytes, Is.GreaterThanOrEqualTo(harness.Pool.RingBytes), "a leased ring means a committed native slab");

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        harness.Tick();

        var batch = harness.Api.Commands<FireAt>();
        Assert.That(batch.Count, Is.EqualTo(1), "the framed record did not survive a compacting collection — the buffer is GC memory");
        foreach (ref readonly var command in batch)
        {
            Assert.That(command.Value.Target, Is.EqualTo(4242u));
            Assert.That(command.Value.Shot, Is.EqualTo(77u));
        }
    }

    /// <summary>
    /// The acceptance criterion: 10 000 commands from 100 sessions in one tick, with no managed allocation once the buffers have grown.
    /// </summary>
    /// <remarks>
    /// <c>GetAllocatedBytesForCurrentThread</c> rather than <c>GetTotalAllocatedBytes</c>: the latter counts every thread in the process, and a test runner has
    /// plenty. That is only meaningful because the harness runs the transport side AND the drain on this thread, which is the reason it exists.
    /// </remarks>
    [Test]
    public void TenThousandCommandsFromOneHundredSessionsAllocateNothingAfterWarmup()
    {
        const int Sessions = 100;
        const int PerSession = 100;

        using var harness = new Harness(maxSessions: 256, ringBytes: 16 * 1024);
        var ids = new SessionId[Sessions];
        for (var i = 0; i < Sessions; i++)
        {
            ids[i] = harness.Admit();
        }

        harness.Tick();

        // Pre-encoded, so the measurement below covers ingress and not the test's own encoder.
        var messages = new byte[Sessions][][];
        for (var s = 0; s < Sessions; s++)
        {
            messages[s] = new byte[PerSession][];
            for (var c = 0; c < PerSession; c++)
            {
                messages[s][c] = Encode(harness.Plan, (uint)c, ("FireAt", (ushort)c, Fire((uint)(s * 1000), (uint)c)));
            }
        }

        // Three full rounds of warm-up: the per-worker segments and the ring leases grow here, and growth is the exemption SUB-07 names.
        for (var round = 0; round < 3; round++)
        {
            Feed(harness, ids, messages);
            harness.Tick();
            Assert.That(harness.Api.Commands<FireAt>().Count, Is.EqualTo(Sessions * PerSession), "the warm-up round did not deliver the whole load");
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        var before = GC.GetAllocatedBytesForCurrentThread();
        Feed(harness, ids, messages);
        harness.Tick();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(harness.Api.Commands<FireAt>().Count, Is.EqualTo(Sessions * PerSession), "the measured round did not deliver the whole load");
        Assert.That(allocated, Is.Zero, $"the steady state allocated {allocated} managed bytes for 10 000 commands");
    }

    private static void Feed(Harness harness, SessionId[] ids, byte[][][] messages)
    {
        for (var s = 0; s < ids.Length; s++)
        {
            var perSession = messages[s];
            for (var c = 0; c < perSession.Length; c++)
            {
                harness.Ingress.OnCommands(ids[s], perSession[c]);
            }
        }
    }

    /// <summary>Only a field the declaration deliberately ignored is left unbound, and the binder still says which ones those are.</summary>
    /// <remarks>
    /// <para>
    /// <b>What changed, and why this test moved with it.</b> <c>CommandBuilder.Field</c> was once the thing that made a field travel, so a declaration that
    /// mentioned one field of two produced a catalog carrying one — and the other arrived zeroed with nothing but this diagnostic array to say so. Fields now
    /// default to their raw type (01-model § 7), so <c>Field</c> is purely an override and the only way to be unbound is to have said <c>Ignore</c>.
    /// </para>
    /// <para>
    /// <b>The array stays, and stays meaningful.</b> It is what an operator reads to see which struct members the wire does not fill, and after
    /// <c>Ignore</c> that answer is a decision rather than an accident.
    /// </para>
    /// </remarks>
    [Test]
    public void OnlyAnIgnoredStructFieldIsLeftUnbound()
    {
        using var harness = new Harness();
        var info = harness.CommandTypes.ByStruct(typeof(FireAt));

        Assert.That(info, Is.Not.Null);
        Assert.That(info.UnboundStructFields, Is.Empty, "this fixture declares every field of FireAt, so nothing is unbound");

        var defaulted = new SubscriptionsRegistry();
        defaulted.Command<FireAt>(c => c.Field(f => f.Target, Codec.EntityRef));
        defaulted.Freeze();

        Assert.That(Bind(defaulted).UnboundStructFields, Is.Empty,
            "'Shot' was not declared, so it takes its raw type's codec and the binder fills it — nothing is left at its default");

        var ignored = new SubscriptionsRegistry();
        ignored.Command<FireAt>(c => c.Field(f => f.Target, Codec.EntityRef).Ignore(f => f.Shot));
        ignored.Freeze();

        Assert.That(Bind(ignored).UnboundStructFields, Is.EqualTo(new[] { nameof(FireAt.Shot) }),
            "a struct field the declaration kept off the wire has to be named, so an operator can see it is never filled");
    }

    private static CommandTypeInfo Bind(SubscriptionsRegistry registry)
    {
        var export = CatalogBuilder.Build(registry, [], CatalogBuilder.DefaultAppName, appRevision: 0, tickPeriodUs: 10_000, systemNames: []);
        return CommandRegistry.Build(registry, CatalogPlan.Compile(export.Canonical)).ByStruct(typeof(FireAt));
    }

    // ── the wiring ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Engine-Pre system really is declared, really is dispatched, and really reaches an application system in the same tick.
    /// </summary>
    /// <remarks>
    /// The one test the harness cannot replace. A system can be silently dead in several ways — never declared, declared on a track nothing dispatches, gated
    /// off for ever, preparing zero chunks — and every one of them leaves the harness above perfectly green.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void TheEnginePreDrainRunsInALiveRuntime()
    {
        using var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        SubscriptionsCommands api = null;
        var seen = new List<uint>();
        var gate = new object();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
            schedule.PublicTrack.DeclareDag("Game").CallbackSystem("Input", _ =>
            {
                var commands = Volatile.Read(ref api);
                if (commands == null)
                {
                    return;
                }

                foreach (ref readonly var command in commands.Commands<FireAt>())
                {
                    lock (gate)
                    {
                        seen.Add(command.Value.Shot);
                    }
                }
            }), new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200 });

        runtime.Subscriptions.Sessions.Kinds("player");
        runtime.Subscriptions.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);
        runtime.Subscriptions.Command<FireAt>(c => c
            .Rate(1000, 2000)
            .Field(f => f.Target, Codec.EntityRef)
            .Field(f => f.Shot, Codec.VarUInt));

        runtime.Start();
        try
        {
            var subscriptions = runtime.SubscriptionsContextForTest.Subscriptions;
            var session = AdmitInto(subscriptions);
            Volatile.Write(ref api, subscriptions.Commands);

            subscriptions.Ingress.OnCommands(session, Encode(subscriptions.CommandTypes.Plan, 1, ("FireAt", 1, Fire(5, 4242))));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (seen.Count > 0)
                    {
                        break;
                    }
                }

                Thread.Sleep(1);
            }

            lock (gate)
            {
                Assert.That(seen, Is.EqualTo(new List<uint> { 4242 }),
                    "the Engine-Pre drain never delivered a command to a system on the Public track — the system is declared but dead");
            }

            Assert.That(subscriptions.Ingress.DrainFaults, Is.Zero, $"the live drain threw: {subscriptions.Ingress.LastDrainFault}");
        }
        finally
        {
            runtime.Shutdown();
        }
    }

    private static SessionId AdmitInto(SubscriptionsRuntime subscriptions)
    {
        var request = new AdmissionRequest("player", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
        Assert.That(subscriptions.Sessions.TryAdmit(subscriptions.Registry.Sessions, request, out var session, out _, out _), Is.True);
        return session;
    }
}
