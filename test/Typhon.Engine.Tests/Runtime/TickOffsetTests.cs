using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// DSEL-04: a divided system's tick phase. With every offset at 0 — which is what the scheduler did before #1294 — every system sharing a divisor fires on
/// the same tick, so a schedule's slow work lands together as a periodic spike that the overload detector then reacts to.
/// </summary>
/// <remarks>
/// The assertions here are about WHICH ticks a system runs on and which systems share one, because that is the whole of the feature. The load-bearing half is
/// negative: an offset must never separate two divided systems the DAG joins, since a producer and consumer that stop sharing a tick change what is in an
/// event queue when its drain runs.
/// </remarks>
[TestFixture]
[NonParallelizable]
class TickOffsetTests : TestBase<TickOffsetTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("timed out waiting for the runtime");
            }

            Thread.Yield();
        }
    }

    private static SystemDefinition ByName(TyphonRuntime runtime, string name) =>
        runtime.Scheduler.Systems.First(s => s != null && s.Name == name);

    /// <summary>Builds a schedule and returns the runtime without starting it — the offsets are decided at build, so most cases need no ticks at all.</summary>
    private TyphonRuntime Built(DatabaseEngine dbe, Action<RuntimeSchedule> declare) =>
        TyphonRuntime.Create(dbe, declare, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

    // ═══════════════════════════════════════════════════════════════════════
    // Assignment
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-04")]
    public void IndependentSystemsSharingADivisor_AreSpreadAcrossItsPhases()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("A", _ => { }, tickDivisor: 2);
            dag.CallbackSystem("B", _ => { }, tickDivisor: 2);
        });

        var offsets = new[] { ByName(runtime, "A").TickOffset, ByName(runtime, "B").TickOffset };
        Assert.That(offsets, Is.EquivalentTo(new[] { 0, 1 }), "two independent divisor-2 systems must not both sit on phase 0");
    }

    /// <summary>
    /// The negative half, and the reason the unit of assignment is the dependency closure rather than the system: separating these two would stop them
    /// sharing a tick.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void SystemsTheDagJoins_KeepOneOffset()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Producer", _ => { }, tickDivisor: 4);
            dag.CallbackSystem("Consumer", _ => { }, after: "Producer", tickDivisor: 4);
        });

        Assert.That(ByName(runtime, "Consumer").TickOffset, Is.EqualTo(ByName(runtime, "Producer").TickOffset));
    }

    /// <summary>
    /// Asserted over the WHOLE graph rather than a sampled pair, because the property that matters is "no edge anywhere spans two phases" and a schedule
    /// large enough to be interesting is exactly where a per-pair check stops being convincing.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void NoEdgeInTheGraph_SpansTwoOffsets()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Root", _ => { }, tickDivisor: 6);
            dag.CallbackSystem("Mid", _ => { }, after: "Root", tickDivisor: 3);
            dag.CallbackSystem("Leaf", _ => { }, after: "Mid", tickDivisor: 6);
            dag.CallbackSystem("Lonely", _ => { }, tickDivisor: 3);
            dag.CallbackSystem("EveryTick", _ => { });
            dag.CallbackSystem("Downstream", _ => { }, after: "EveryTick", tickDivisor: 2);
        });

        AssertNoEdgeSpansTwoOffsets(Graph(runtime));
    }

    /// <summary>One node per system: what it is called, how often it runs, and on which phase.</summary>
    private readonly record struct Node(string Name, int Divisor, int Offset, int[] Successors);

    /// <summary>The built schedule as a graph of nodes, so the verifier below can be driven by a synthetic one too.</summary>
    private static Node[] Graph(TyphonRuntime runtime) =>
        runtime.Scheduler.Systems
            .Select(s => s == null ? new Node(string.Empty, 1, 0, []) : new Node(s.Name, s.TickDivisor, s.TickOffset, s.Successors))
            .ToArray();

    /// <summary>The marker <see cref="AssertNoEdgeSpansTwoOffsets"/> fails with, so the mutant can prove it was THIS assertion that rejected.</summary>
    private const string SpanMarker = "spans two phases";

    /// <summary>
    /// DSEL-04's verifier: no edge between two divided systems may cross a phase boundary. Factored out so a <c>[RuleMutant]</c> can hand it a graph that
    /// violates the rule — a verifier that cannot fail is worse than no verifier.
    /// </summary>
    private static void AssertNoEdgeSpansTwoOffsets(Node[] nodes)
    {
        var divided = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Divisor <= 1)
            {
                continue;
            }

            divided++;

            // Reachability, per node. Together with the edge check below this also gives the closure bound for free: every offset in a component is the
            // same value, and each is below its own divisor, so it is below the smallest of them.
            Assert.That(nodes[i].Offset, Is.InRange(0, nodes[i].Divisor - 1),
                $"'{nodes[i].Name}' holds phase {nodes[i].Offset} of divisor {nodes[i].Divisor}, which no tick can satisfy");

            foreach (var next in nodes[i].Successors)
            {
                if (nodes[next].Divisor > 1)
                {
                    Assert.That(nodes[next].Offset, Is.EqualTo(nodes[i].Offset),
                        $"'{nodes[i].Name}' -> '{nodes[next].Name}' {SpanMarker}, so they can never run in the same tick");
                }
            }
        }

        Assert.That(divided, Is.GreaterThan(0), "precondition: the graph has no divided system, so it cannot witness anything");
    }

    /// <summary>
    /// A graph whose producer and consumer sit on different phases — what the scheduler would produce if offsets were assigned per system instead of per
    /// dependency closure. The verifier must reject it.
    /// </summary>
    [Test]
    [RuleMutant("DSEL-04")]
    public void AnEdgeSpanningTwoPhases_IsCaughtByTheVerifier() =>
        RuleMutants.AssertDetects("DSEL-04", SpanMarker, () => AssertNoEdgeSpansTwoOffsets(
        [
            new Node("Producer", 4, 0, [1]),
            new Node("Consumer", 4, 2, []),
        ]));

    /// <summary>
    /// A closure's offset has to be reachable by every member, so it is bounded by the SMALLEST divisor in it — 2 here, not 6.
    /// </summary>
    /// <remarks>
    /// The bound is what is under test, so the closure's minimum is 2 and three other bound-2 closures are declared ahead of it: the round-robin cursor has
    /// then advanced, so an implementation that bounded the offset by the divisor-6 member instead would be free to pick 2..5 and would be caught. An earlier
    /// version used a (6, 3) closure with nothing else declared, and passed against a deliberately broken implementation because the cursor happened to be at
    /// 0 — an assertion that can only be satisfied is not one.
    /// </remarks>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AClosuresOffset_IsBelowItsSmallestDivisor()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Filler1", _ => { }, tickDivisor: 2);
            dag.CallbackSystem("Filler2", _ => { }, tickDivisor: 2);
            dag.CallbackSystem("Filler3", _ => { }, tickDivisor: 2);
            dag.CallbackSystem("Big", _ => { }, tickDivisor: 6);
            dag.CallbackSystem("Small", _ => { }, after: "Big", tickDivisor: 2);
        });

        var offset = ByName(runtime, "Big").TickOffset;
        Assert.That(offset, Is.InRange(0, 1), "an offset of 2+ is unreachable for the divisor-2 member of the same closure");
        Assert.That(ByName(runtime, "Small").TickOffset, Is.EqualTo(offset));
    }

    /// <summary>
    /// The transitive shape, asserted as the DOCUMENTED behaviour rather than left untested: <c>A(÷2) → B(÷1) → C(÷2)</c> gives A and C no edge of their
    /// own, so they may hold different phases.
    /// </summary>
    /// <remarks>
    /// This looks like the damage DSEL-04 prevents, one hop removed, and a review called it critical. It is not, and the next test is the reason: every
    /// relationship whose freshness is DECLARED carries a direct edge, so it unions. What is left here is a chain of PLAIN reads, which the DAG only ever
    /// ordered. Unioning through undivided interiors instead would collapse any real schedule into one component through some every-tick system and leave
    /// the stagger doing nothing — so the behaviour is deliberate, and this test exists to make a future change to it deliberate too.
    /// </remarks>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void ATransitivePlainReadPath_MayBeStaggered()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("A", _ => { }, tickDivisor: 2);
            dag.CallbackSystem("B", _ => { }, after: "A");
            dag.CallbackSystem("C", _ => { }, after: "B", tickDivisor: 2);
        });

        Assert.That(ByName(runtime, "B").TickOffset, Is.Zero, "B runs every tick and holds no phase");
        Assert.That(ByName(runtime, "A").TickOffset, Is.Not.EqualTo(ByName(runtime, "C").TickOffset),
            "A and C are joined only through an every-tick system, so the stagger is free to separate them — and does");
    }

    /// <summary>
    /// The claim the previous test rests on: a queue between two gated systems unions them even with an undivided system in between, because
    /// <c>AccessDagDeriver</c> emits the producer→consumer edge directly from <c>WritesEvents ∩ ReadsEvents</c> (ED-05c) rather than from adjacency.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AQueueBetweenTwoGatedSystems_UnionsThemAcrossAnUndividedMiddle()
    {
        using var dbe = SetupEngine();
        EventQueue<int> queue = null;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            queue = dag.CreateEventQueue<int>("Across");
            dag.Add(new QueueProducer(queue));
            dag.CallbackSystem("Middle", _ => { }, after: "QueueProducer");
            dag.Add(new QueueConsumer(queue));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        Assert.That(ByName(runtime, "QueueConsumer").TickOffset, Is.EqualTo(ByName(runtime, "QueueProducer").TickOffset),
            "a producer and its consumer must keep sharing a tick — the drain is what the stagger must not move");
    }

    private sealed class QueueProducer : CallbackSystem
    {
        private readonly EventQueue<int> _queue;

        public QueueProducer(EventQueue<int> queue) => _queue = queue;

        protected override void Configure(SystemBuilder b) => b
            .Name("QueueProducer")
            .TickDivisor(2)
            .WritesEvents(_queue);

        protected override void Execute(TickContext ctx) => ctx.Writer(_queue).Push(1);
    }

    private sealed class QueueConsumer : CallbackSystem
    {
        private readonly EventQueue<int> _queue;

        public QueueConsumer(EventQueue<int> queue) => _queue = queue;

        protected override void Configure(SystemBuilder b) => b
            .Name("QueueConsumer")
            .TickDivisor(2)
            .ReadsEvents(_queue);

        protected override void Execute(TickContext ctx)
        {
        }
    }

    /// <summary>
    /// The canonical "cheap normally, throttled under load" shape — <c>TickDivisor 1</c>, <c>ThrottledTickDivisor 4</c> — must get a phase too. Bounding the
    /// offset by TickDivisor alone would leave it at 0 for every such system, so they would all re-synchronise the moment overload engaged.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AThrottleOnlySystem_StillGetsAPhase()
    {
        using var dbe = SetupEngine();
        var ticksA = new ConcurrentQueue<long>();
        var ticksB = new ConcurrentQueue<long>();
        using (var runtime = Built(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   dag.CallbackSystem("A", ctx => ticksA.Enqueue(ctx.TickNumber), throttledTickDivisor: 2);
                   dag.CallbackSystem("B", ctx => ticksB.Enqueue(ctx.TickNumber), throttledTickDivisor: 2);
               }))
        {
            var offsets = new[] { ByName(runtime, "A").TickOffset, ByName(runtime, "B").TickOffset };
            Assert.That(offsets, Is.EquivalentTo(new[] { 0, 1 }), "a throttle-only system has a gate, so it has a phase");

            runtime.Scheduler.ForceOverloadLevelForTests(OverloadLevel.SystemThrottling);
            runtime.Start();
            WaitFor(() => ticksA.Count >= 3 && ticksB.Count >= 3);
            runtime.Shutdown();
        }

        Assert.That(ticksA.ToHashSet().Intersect(ticksB.ToHashSet()), Is.Empty, "under overload they must stay apart rather than re-align on phase 0");
    }

    /// <summary>An explicit 0 is as wrong as an explicit 5 on a system no gate applies to, and the guard has to test against the sentinel to catch it.</summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnOffsetOnAnUngatedSystem_IsRefusedAtBuild()
    {
        using var dbe = SetupEngine();
        foreach (var asked in new[] { 0, -3, 2 })
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Built(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Bad", _ => { }, tickOffset: asked)));

            Assert.That(ex?.Message, Does.Contain("needs a tickDivisor or throttledTickDivisor above 1"), $"tickOffset {asked} must be refused");
        }
    }

    [Test]
    public void AnUndividedSystem_SitsOnPhaseZero()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Plain", _ => { }));

        Assert.That(ByName(runtime, "Plain").TickOffset, Is.Zero);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // What the offsets do at run time
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The offset decides which ticks a system runs on, and a non-zero one must not starve it. Phase 1 of divisor 2 means the ODD ticks — a system that ran
    /// on no tick at all would satisfy "never two on the same tick" vacuously, so this is the case that makes the spread claim mean something.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void EachSystemRunsOnItsOwnPhase_AndNeverOnTheOthers()
    {
        using var dbe = SetupEngine();
        var ticksA = new ConcurrentQueue<long>();
        var ticksB = new ConcurrentQueue<long>();
        int offsetA;
        int offsetB;
        using (var runtime = Built(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   dag.CallbackSystem("A", ctx => ticksA.Enqueue(ctx.TickNumber), tickDivisor: 2);
                   dag.CallbackSystem("B", ctx => ticksB.Enqueue(ctx.TickNumber), tickDivisor: 2);
               }))
        {
            offsetA = ByName(runtime, "A").TickOffset;
            offsetB = ByName(runtime, "B").TickOffset;
            runtime.Start();
            WaitFor(() => ticksA.Count >= 4 && ticksB.Count >= 4);
            runtime.Shutdown();
        }

        Assert.That(ticksA, Is.All.Matches<long>(t => t % 2 == offsetA), $"A holds phase {offsetA}");
        Assert.That(ticksB, Is.All.Matches<long>(t => t % 2 == offsetB), $"B holds phase {offsetB}");
        Assert.That(ticksA.ToHashSet().Intersect(ticksB.ToHashSet()), Is.Empty, "the two must never share a tick — that is the whole point of the stagger");
    }

    /// <summary>
    /// Overload level 1 must not re-synchronise the throttled systems onto one tick: a spike at the moment the server least wants one is self-inflicted.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void UnderOverload_ThrottledSystemsAreStaggeredToo()
    {
        using var dbe = SetupEngine();
        var ticksA = new ConcurrentQueue<long>();
        var ticksB = new ConcurrentQueue<long>();
        using (var runtime = Built(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   // Divided at normal load as well, because the offset is assigned from the declared divisor — a system that is only throttled under load
                   // has no phase to be given. That is the shape a schedule wanting staggered throttling has to declare.
                   dag.CallbackSystem("A", ctx => ticksA.Enqueue(ctx.TickNumber), tickDivisor: 2, throttledTickDivisor: 2);
                   dag.CallbackSystem("B", ctx => ticksB.Enqueue(ctx.TickNumber), tickDivisor: 2, throttledTickDivisor: 2);
               }))
        {
            // Staged rather than provoked: making ticks genuinely overrun needs a slow body, which is a timing-dependent precondition for a test about
            // which tick numbers a gate admits.
            runtime.Scheduler.ForceOverloadLevelForTests(OverloadLevel.SystemThrottling);
            runtime.Start();
            WaitFor(() => ticksA.Count >= 3 && ticksB.Count >= 3);
            runtime.Shutdown();
        }

        Assert.That(ticksA.ToHashSet().Intersect(ticksB.ToHashSet()), Is.Empty, "under overload the throttled gate must keep them apart, not re-align them");
    }

    /// <summary>
    /// An offset a system's THROTTLED gate could not reach is refused at build, rather than reduced at run time.
    /// </summary>
    /// <remarks>
    /// This was a live hazard under the first cut, which bounded the offset by <c>TickDivisor</c> alone: a system at divisor 10 could hold phase 7 while its
    /// throttled divisor was 2, and <c>tick % 2 != 7</c> is true for every tick, so it would have run on no tick at all under load. The fix is not to reduce
    /// the offset on the hot path — that is a per-system integer division on exactly the ticks already short of budget — but to bound it by the TIGHTEST gate
    /// at build, so every gate can reach it and none has to reduce it. The hazard is then unreachable rather than handled.
    /// </remarks>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnOffsetItsThrottledGateCannotReach_IsRefusedAtBuild()
    {
        using var dbe = SetupEngine();
        var ex = Assert.Throws<InvalidOperationException>(() => Built(dbe, schedule =>
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Wide", _ => { }, tickDivisor: 10, throttledTickDivisor: 2, tickOffset: 7)));

        Assert.That(ex?.Message, Does.Contain("[0, 2)"), "the TIGHTEST gate is the bound, not the tick divisor");
        Assert.That(ex?.Message, Does.Contain("never run"));
    }

    /// <summary>And a reachable one on the same shape runs, on its own phase, under both gates.</summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnOffsetBothGatesCanReach_RunsUnderBoth()
    {
        using var dbe = SetupEngine();
        var ran = new ConcurrentQueue<long>();
        using (var runtime = Built(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test")
                       .CallbackSystem("Wide", ctx => ran.Enqueue(ctx.TickNumber), tickDivisor: 10, throttledTickDivisor: 2, tickOffset: 1);
               }))
        {
            Assert.That(ByName(runtime, "Wide").TickOffset, Is.EqualTo(1));
            runtime.Scheduler.ForceOverloadLevelForTests(OverloadLevel.SystemThrottling);
            runtime.Start();
            WaitFor(() => ran.Count >= 2);
            runtime.Shutdown();
        }

        Assert.That(ran, Is.Not.Empty, "an offset both gates can reach must still admit ticks under overload");
        Assert.That(ran, Is.All.Matches<long>(t => t % 10 == 1 && t % 2 == 1), "and only the ticks both gates admit");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Explicit offsets, and what is refused
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnExplicitOffset_IsHonoured()
    {
        using var dbe = SetupEngine();
        using var runtime = Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Pinned", _ => { }, tickDivisor: 4, tickOffset: 3);
        });

        Assert.That(ByName(runtime, "Pinned").TickOffset, Is.EqualTo(3));
    }

    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnOffsetOutsideItsDivisor_IsRefusedAtBuild()
    {
        using var dbe = SetupEngine();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Built(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Bad", _ => { }, tickDivisor: 4, tickOffset: 4)));

        Assert.That(ex?.Message, Does.Contain("tickOffset 4").And.Contain("[0, 4)").And.Contain("never run"));
    }

    [Test]
    [VerifiesRule("DSEL-04")]
    public void TwoJoinedSystemsAskingForDifferentOffsets_AreRefusedAtBuild()
    {
        using var dbe = SetupEngine();
        var ex = Assert.Throws<InvalidOperationException>(() => Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Producer", _ => { }, tickDivisor: 4, tickOffset: 0);
            dag.CallbackSystem("Consumer", _ => { }, after: "Producer", tickDivisor: 4, tickOffset: 2);
        }));

        Assert.That(ex?.Message, Does.Contain("Producer").And.Contain("Consumer"), "the message must name both, or there is nowhere to start looking");
        Assert.That(ex?.Message, Does.Contain("same tick"));
    }

    /// <summary>An offset below the closure's smallest divisor is the binding bound, and asking above it is refused rather than silently narrowed.</summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void AnOffsetUnreachableByItsClosure_IsRefusedAtBuild()
    {
        using var dbe = SetupEngine();
        var ex = Assert.Throws<InvalidOperationException>(() => Built(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Big", _ => { }, tickDivisor: 6, tickOffset: 4);
            dag.CallbackSystem("Small", _ => { }, after: "Big", tickDivisor: 2);
        }));

        Assert.That(ex?.Message, Does.Contain("closure").And.Contain("tightest bound").And.Contain("Big"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Observability
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An auto-assigned stagger that cannot be observed cannot be debugged: "why did this system run on tick 7" has to be answerable from outside the engine.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-04")]
    public void TheAssignedOffset_IsReportedInTheStatsSnapshot()
    {
        using var dbe = SetupEngine();
        RuntimeStatsSnapshot snapshot;
        using (var runtime = Built(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   dag.CallbackSystem("A", _ => { }, tickDivisor: 2);
                   dag.CallbackSystem("B", _ => { }, tickDivisor: 2);
               }))
        {
            runtime.Start();
            WaitFor(() => runtime.CurrentTickNumber >= 4);
            snapshot = runtime.ReadStats();
            runtime.Shutdown();
        }

        var reported = snapshot.Systems.Where(s => s.Name is "A" or "B").ToArray();
        Assert.That(reported.Length, Is.EqualTo(2));
        Assert.That(reported, Is.All.Matches<SystemStat>(s => s.TickDivisor == 2));
        Assert.That(reported.Select(s => s.TickOffset), Is.EquivalentTo(new[] { 0, 1 }));
    }
}
