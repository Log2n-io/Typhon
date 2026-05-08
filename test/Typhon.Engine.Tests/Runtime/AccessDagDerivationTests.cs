using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class AccessDagDerivationTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "AccessDerivTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    // ── Component fixtures ──

    private struct CompA { public int V; }
    private struct CompB { public int V; }
    private struct CompC { public int V; }

    private class Sys : CallbackSystem
    {
        public Action<SystemBuilder> ConfigureAction;
        protected override void Configure(SystemBuilder b) => ConfigureAction?.Invoke(b);
        protected override void Execute(TickContext ctx) { }
    }

    private static RuntimeOptions Options() => new() { BaseTickRate = 1000, WorkerCount = 1 };

    // ── W×W detection ──────────────────────────────────────────────────

    [Test]
    public void WW_SamePhase_NoOrdering_Throws()
    {
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>() });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("'A'"));
        Assert.That(ex.Message, Does.Contain("'B'"));
        Assert.That(ex.Message, Does.Contain("Writes<CompA>"));
        Assert.That(ex.Message, Does.Contain(".After("));
    }

    [Test]
    public void WW_SamePhase_ResolvedByAfter_Builds()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>().After("A") })
            .Build(_registry.Runtime);

        var aDef = scheduler.Systems.First(s => s.Name == "A");
        var bDef = scheduler.Systems.First(s => s.Name == "B");
        Assert.That(aDef.Successors, Does.Contain(bDef.Index));
    }

    [Test]
    public void WW_SamePhase_ResolvedByBefore_Builds()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>().Before("B") })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>() })
            .Build(_registry.Runtime);

        var aDef = scheduler.Systems.First(s => s.Name == "A");
        var bDef = scheduler.Systems.First(s => s.Name == "B");
        Assert.That(aDef.Successors, Does.Contain(bDef.Index));
    }

    [Test]
    public void WW_DifferentPhases_NoConflict()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Output).Writes<CompA>() })
            .Build(_registry.Runtime);

        var aDef = scheduler.Systems.First(s => s.Name == "A");
        var bDef = scheduler.Systems.First(s => s.Name == "B");
        // Cross-phase edge: A → B (Simulation index 1 → Output index 2)
        Assert.That(aDef.Successors, Does.Contain(bDef.Index));
    }

    // ── R×W plain detection ───────────────────────────────────────────

    [Test]
    public void RW_PlainReadWithSamePhaseWriter_Throws()
    {
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Simulation).Reads<CompA>() });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("'Reader'"));
        Assert.That(ex.Message, Does.Contain("Reads<CompA>"));
        Assert.That(ex.Message, Does.Contain("ReadsFresh"));
        Assert.That(ex.Message, Does.Contain("ReadsSnapshot"));
    }

    [Test]
    public void RW_PlainReadInDifferentPhase_OK()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Output).Reads<CompA>() })
            .Build(_registry.Runtime);

        Assert.That(scheduler.Systems, Has.Length.EqualTo(2));
    }

    // ── R×W fresh / snapshot derivation ───────────────────────────────

    [Test]
    public void ReadsFresh_DerivesWriterToReaderEdge()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Simulation).ReadsFresh<CompA>() })
            .Build(_registry.Runtime);

        var writerDef = scheduler.Systems.First(s => s.Name == "Writer");
        var readerDef = scheduler.Systems.First(s => s.Name == "Reader");
        Assert.That(writerDef.Successors, Does.Contain(readerDef.Index));
    }

    [Test]
    public void ReadsSnapshot_DerivesReaderToWriterEdge()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Simulation).ReadsSnapshot<CompA>() })
            .Build(_registry.Runtime);

        var writerDef = scheduler.Systems.First(s => s.Name == "Writer");
        var readerDef = scheduler.Systems.First(s => s.Name == "Reader");
        Assert.That(readerDef.Successors, Does.Contain(writerDef.Index));
    }

    // ── Cross-phase edges (conflict-driven, post 2026-05-07 amendment) ──
    //
    // The deriver no longer chains every system in P_a to every system in P_b. Edges across
    // phases are emitted ONLY when a real read/write/event/resource conflict exists between
    // the pair. Independent systems in adjacent phases are allowed to overlap on the worker
    // pool — see `claude/design/runtime/07-system-access-declarations.md` §"Amendment 2026-05-07".

    [Test]
    public void CrossPhase_NoConflict_NoEdgeDerived()
    {
        // Two systems in adjacent phases with no declared access overlap → no derived edge.
        // This is the "phase boundaries are not barriers" property: independent systems may run
        // concurrently across the phase line.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("InputSys").Phase(Phase.Input) })
            .Add(new Sys { ConfigureAction = b => b.Name("SimSys").Phase(Phase.Simulation) })
            .Build(_registry.Runtime);

        var inputDef = scheduler.Systems.First(s => s.Name == "InputSys");
        var simDef = scheduler.Systems.First(s => s.Name == "SimSys");
        Assert.That(inputDef.Successors, Does.Not.Contain(simDef.Index));
        Assert.That(simDef.PredecessorCount, Is.EqualTo(0));
    }

    [Test]
    public void CrossPhase_NoConflictChain_AllPredecessorCountsZero()
    {
        // 4-phase chain (Input → Simulation → Output → Cleanup), no declared access on any
        // system. Result: zero derived edges. Phase order survives only as a logical contract;
        // the runtime DAG would dispatch all four to free workers concurrently.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("In").Phase(Phase.Input) })
            .Add(new Sys { ConfigureAction = b => b.Name("Sim").Phase(Phase.Simulation) })
            .Add(new Sys { ConfigureAction = b => b.Name("Out").Phase(Phase.Output) })
            .Add(new Sys { ConfigureAction = b => b.Name("Clean").Phase(Phase.Cleanup) })
            .Build(_registry.Runtime);

        foreach (var sys in scheduler.Systems)
        {
            Assert.That(sys.PredecessorCount, Is.EqualTo(0),
                $"System '{sys.Name}' has unexpected predecessor count {sys.PredecessorCount} — no declared conflicts should produce no derived edges.");
        }
    }

    [Test]
    public void CrossPhase_WriteToWriteAcrossPhases_DerivesEarlierToLaterEdge()
    {
        // Same component written in two different phases. Phase order disambiguates (no AC-01
        // error, unlike same-phase W×W); the deriver emits a single earlier→later edge so the
        // later writer's commit is sequenced after the earlier one's.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Early").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Late").Phase(Phase.Output).Writes<CompA>() })
            .Build(_registry.Runtime);

        var early = scheduler.Systems.First(s => s.Name == "Early");
        var late = scheduler.Systems.First(s => s.Name == "Late");
        Assert.That(early.Successors, Does.Contain(late.Index));
    }

    [Test]
    public void CrossPhase_WriterToReader_DerivesEdge()
    {
        // ED-05a: writer in earlier phase, reader (any flavour) in later phase → edge.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("ReaderFresh").Phase(Phase.Output).ReadsFresh<CompA>() })
            .Build(_registry.Runtime);

        var writer = scheduler.Systems.First(s => s.Name == "Writer");
        var reader = scheduler.Systems.First(s => s.Name == "ReaderFresh");
        Assert.That(writer.Successors, Does.Contain(reader.Index));
    }

    [Test]
    public void CrossPhase_ReaderInEarlierPhaseToWriterInLaterPhase_DerivesEdge()
    {
        // ED-05b: reader in earlier phase must complete before later-phase writer touches T.
        // Direction earlier→later regardless of role (phase order is the disambiguator).
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("EarlyReader").Phase(Phase.Input).ReadsSnapshot<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("LateWriter").Phase(Phase.Simulation).Writes<CompA>() })
            .Build(_registry.Runtime);

        var reader = scheduler.Systems.First(s => s.Name == "EarlyReader");
        var writer = scheduler.Systems.First(s => s.Name == "LateWriter");
        Assert.That(reader.Successors, Does.Contain(writer.Index));
    }

    [Test]
    public void CrossPhase_EventProducerConsumer_DerivesEdge()
    {
        // ED-05c: producer in earlier phase, consumer in later phase. Without an explicit edge
        // the consumer could drain concurrently with the producer's writes; the deriver inserts
        // the producer→consumer ordering automatically.
        var schedule = RuntimeSchedule.Create(Options());
        var queue = schedule.CreateEventQueue<int>("EmitQ", 16);

        using var scheduler = schedule
            .Add(new Sys { ConfigureAction = b => b.Name("Producer").Phase(Phase.Simulation).WritesEvents(queue) })
            .Add(new Sys { ConfigureAction = b => b.Name("Consumer").Phase(Phase.Output).ReadsEvents(queue) })
            .Build(_registry.Runtime);

        var producer = scheduler.Systems.First(s => s.Name == "Producer");
        var consumer = scheduler.Systems.First(s => s.Name == "Consumer");
        Assert.That(producer.Successors, Does.Contain(consumer.Index));
    }

    [Test]
    public void CrossPhase_ResourceWriteRead_DerivesEdge()
    {
        // ED-05d: any cross-phase resource access pair with at least one writer.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Input).WritesResource("Physics") })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Simulation).ReadsResource("Physics") })
            .Build(_registry.Runtime);

        var writer = scheduler.Systems.First(s => s.Name == "Writer");
        var reader = scheduler.Systems.First(s => s.Name == "Reader");
        Assert.That(writer.Successors, Does.Contain(reader.Index));
    }

    [Test]
    public void CrossPhase_DisjointAccess_NoEdgeEvenWithDeclarations()
    {
        // The AntHill-shape regression test: MoveAll (writes WorldBounds, Velocity in Movement)
        // and a hypothetical SoundSystem (writes AudioState in Lifecycle) declare disjoint
        // component sets. Phase order says SoundSystem comes after MoveAll, but with
        // conflict-driven cross-phase derivation there's no edge — the runtime can dispatch
        // SoundSystem on a free worker while MoveAll is still running.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("MoveLike").Phase(Phase.Simulation).Writes<CompA>().Writes<CompB>() })
            .Add(new Sys { ConfigureAction = b => b.Name("SoundLike").Phase(Phase.Output).Writes<CompC>() })
            .Build(_registry.Runtime);

        var move = scheduler.Systems.First(s => s.Name == "MoveLike");
        var sound = scheduler.Systems.First(s => s.Name == "SoundLike");
        Assert.That(move.Successors, Does.Not.Contain(sound.Index),
            "MoveLike and SoundLike share no components — they must not be ordered across phases.");
        Assert.That(sound.PredecessorCount, Is.EqualTo(0));
    }

    [Test]
    public void CrossPhase_ExplicitAfterAcrossPhases_PreservedVerbatim()
    {
        // ED-05e: explicit `.After("X")` spanning phases must not be elided. Even with no
        // declared access conflict, the explicit edge survives the conflict-driven pass.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Up").Phase(Phase.Input) })
            .Add(new Sys { ConfigureAction = b => b.Name("Down").Phase(Phase.Simulation).After("Up") })
            .Build(_registry.Runtime);

        var up = scheduler.Systems.First(s => s.Name == "Up");
        var down = scheduler.Systems.First(s => s.Name == "Down");
        Assert.That(up.Successors, Does.Contain(down.Index));
    }

    // ── Event queue derivation ────────────────────────────────────────

    [Test]
    public void Events_ProducerToConsumer_DerivesEdge()
    {
        var schedule = RuntimeSchedule.Create(Options());
        var queue = schedule.CreateEventQueue<int>("Q", 16);

        using var scheduler = schedule
            .Add(new Sys { ConfigureAction = b => b.Name("Producer").Phase(Phase.Simulation).WritesEvents(queue) })
            .Add(new Sys { ConfigureAction = b => b.Name("Consumer").Phase(Phase.Simulation).ReadsEvents(queue) })
            .Build(_registry.Runtime);

        var producerDef = scheduler.Systems.First(s => s.Name == "Producer");
        var consumerDef = scheduler.Systems.First(s => s.Name == "Consumer");
        Assert.That(producerDef.Successors, Does.Contain(consumerDef.Index));
    }

    // ── Resource derivation ───────────────────────────────────────────

    [Test]
    public void Resource_WW_SamePhase_NoOrdering_Throws()
    {
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).WritesResource("X") })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).WritesResource("X") });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("WritesResource"));
        Assert.That(ex.Message, Does.Contain("\"X\""));
    }

    [Test]
    public void Resource_RW_DerivesWriterToReaderEdge()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Writer").Phase(Phase.Simulation).WritesResource("Physics") })
            .Add(new Sys { ConfigureAction = b => b.Name("Reader").Phase(Phase.Simulation).ReadsResource("Physics") })
            .Build(_registry.Runtime);

        var writerDef = scheduler.Systems.First(s => s.Name == "Writer");
        var readerDef = scheduler.Systems.First(s => s.Name == "Reader");
        Assert.That(writerDef.Successors, Does.Contain(readerDef.Index));
    }

    // ── ExclusivePhase enforcement ────────────────────────────────────

    [Test]
    public void ExclusivePhase_WithOtherSystemInPhase_Throws()
    {
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Excl").Phase(Phase.Cleanup).ExclusivePhase() })
            .Add(new Sys { ConfigureAction = b => b.Name("Other").Phase(Phase.Cleanup) });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ExclusivePhase"));
        Assert.That(ex.Message, Does.Contain("'Excl'"));
    }

    [Test]
    public void ExclusivePhase_AloneInPhase_OK()
    {
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Excl").Phase(Phase.Cleanup).ExclusivePhase() })
            .Add(new Sys { ConfigureAction = b => b.Name("Other").Phase(Phase.Simulation) })
            .Build(_registry.Runtime);

        Assert.That(scheduler.Systems, Has.Length.EqualTo(2));
    }

    // ── Undeclared systems land in DefaultPhase and ARE conflict-detected (Unit 5) ──

    [Test]
    public void NoPhaseDeclared_LandsInDefaultPhase_AndConflictsDetected()
    {
        // Two systems both Writes<CompA>, neither calls b.Phase(). Both default to Phase.Simulation → W×W triggers.
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Writes<CompA>() });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("'A'"));
        Assert.That(ex.Message, Does.Contain("'B'"));
        Assert.That(ex.Message, Does.Contain("Writes<CompA>"));
    }

    [Test]
    public void NoPhaseDeclared_NoDeclarations_StillBuilds()
    {
        // Two systems with no phase AND no declarations land in Phase.Simulation but conflict detection no-ops (HasAnyDeclaration == false).
        // Cross-phase edges: none (both in same default phase).
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A") })
            .Add(new Sys { ConfigureAction = b => b.Name("B") })
            .Build(_registry.Runtime);

        var aDef = scheduler.Systems.First(s => s.Name == "A");
        var bDef = scheduler.Systems.First(s => s.Name == "B");
        Assert.That(aDef.PhaseIndex, Is.EqualTo(1)); // Phase.Simulation
        Assert.That(bDef.PhaseIndex, Is.EqualTo(1));
        // Same phase, no declarations → no derived edges
        Assert.That(aDef.Successors, Does.Not.Contain(bDef.Index));
        Assert.That(bDef.Successors, Does.Not.Contain(aDef.Index));
    }

    // ── Multiple components, mixed access ─────────────────────────────

    // ── XOR resolution: both .After AND .Before between same pair → cycle, hard error ──

    [Test]
    public void WW_BothBeforeAndAfterBetweenSamePair_ThrowsCycleError()
    {
        // A.Before("B").After("B") — both edges declared between A and B forms a cycle.
        // The W×W check should detect this as ambiguous (XOR rule) before the cycle detector kicks in.
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>().After("B").Before("B") })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>() });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("both directions").Or.Contain("cycle"));
    }

    // ── Empty system list builds cleanly ──────────────────────────────

    [Test]
    public void EmptySchedule_BuildsCleanly()
    {
        using var scheduler = RuntimeSchedule.Create(Options()).Build(_registry.Runtime);
        Assert.That(scheduler.Systems, Is.Empty);
    }

    // ── Direct-adjacency limitation: 3+ writers force pairwise edges ──

    [Test]
    public void WW_ThreeWriters_LinearChain_ForcesPairwiseEdges()
    {
        // C2 limitation: A.Before(B).Before(C) does NOT implicitly resolve (A,C) — user must add `.After(A)` to C.
        // This test documents the limitation rather than asserting better behavior.
        var schedule = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>().Before("B") })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>().Before("C") })
            .Add(new Sys { ConfigureAction = b => b.Name("C").Phase(Phase.Simulation).Writes<CompA>() });

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        // Error should name A and C (the unordered pair); the chain through B is not transitively traced
        Assert.That(ex.Message, Does.Contain("'A'").And.Contain("'C'"));
    }

    [Test]
    public void WW_ThreeWriters_AllPairwiseEdgesDeclared_Builds()
    {
        // With explicit edges for every pair, the deriver accepts the configuration.
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("A").Phase(Phase.Simulation).Writes<CompA>().Before("B") })
            .Add(new Sys { ConfigureAction = b => b.Name("B").Phase(Phase.Simulation).Writes<CompA>().Before("C") })
            .Add(new Sys { ConfigureAction = b => b.Name("C").Phase(Phase.Simulation).Writes<CompA>().After("A") })
            .Build(_registry.Runtime);

        Assert.That(scheduler.Systems, Has.Length.EqualTo(3));
    }

    [Test]
    public void MixedAccess_DerivesAllEdges()
    {
        // Movement writes Position. Render reads Position fresh (after Movement). Replay reads Position snapshot (before Movement).
        using var scheduler = RuntimeSchedule.Create(Options())
            .Add(new Sys { ConfigureAction = b => b.Name("Movement").Phase(Phase.Simulation).Writes<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Render").Phase(Phase.Simulation).ReadsFresh<CompA>() })
            .Add(new Sys { ConfigureAction = b => b.Name("Replay").Phase(Phase.Simulation).ReadsSnapshot<CompA>() })
            .Build(_registry.Runtime);

        var movement = scheduler.Systems.First(s => s.Name == "Movement");
        var render = scheduler.Systems.First(s => s.Name == "Render");
        var replay = scheduler.Systems.First(s => s.Name == "Replay");

        Assert.That(movement.Successors, Does.Contain(render.Index));
        Assert.That(replay.Successors, Does.Contain(movement.Index));
    }
}
