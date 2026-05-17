using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class SystemBuilderFluentTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "FluentBuilderTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    // ── Component types used for declaration tests ──

    private struct CompA { public int V; }
    private struct CompB { public int V; }
    private struct CompC { public int V; }
    private struct CompD { public int V; }

    // ── Test system that exposes its builder for assertion ──

    private class DeclarativeSystem : CallbackSystem
    {
        public Action<SystemBuilder> ConfigureAction;

        protected override void Configure(SystemBuilder b) => ConfigureAction?.Invoke(b);

        protected override void Execute(TickContext ctx)
        {
        }
    }

    // ─── Layer A: fluent return ────────────────────────────────────────

    [Test]
    public void AllExistingMethods_ReturnSameBuilder()
    {
        var b = new SystemBuilder();

        Assert.That(b.Name("X"), Is.SameAs(b));
        Assert.That(b.After("Y"), Is.SameAs(b));
        Assert.That(b.AfterAll("A", "B"), Is.SameAs(b));
        Assert.That(b.Before("Z"), Is.SameAs(b));
        Assert.That(b.Priority(SystemPriority.High), Is.SameAs(b));
        Assert.That(b.ShouldRun(() => true), Is.SameAs(b));
        Assert.That(b.TickDivisor(2), Is.SameAs(b));
        Assert.That(b.ThrottledTickDivisor(3), Is.SameAs(b));
        Assert.That(b.CanShed(true), Is.SameAs(b));
        Assert.That(b.Phase(Phase.Simulation), Is.SameAs(b));
    }

    [Test]
    public void DeclarationMethods_ReturnSameBuilder()
    {
        var b = new SystemBuilder();

        Assert.That(b.Reads<CompA>(), Is.SameAs(b));
        Assert.That(b.ReadsFresh<CompA>(), Is.SameAs(b));
        Assert.That(b.ReadsSnapshot<CompA>(), Is.SameAs(b));
        Assert.That(b.AdditionalReads<CompA>(), Is.SameAs(b));
        Assert.That(b.Writes<CompB>(), Is.SameAs(b));
        Assert.That(b.SideWrites<CompB>(), Is.SameAs(b));
        Assert.That(b.WritesResource("res"), Is.SameAs(b));
        Assert.That(b.ReadsResource("res"), Is.SameAs(b));
        Assert.That(b.ExclusivePhase(), Is.SameAs(b));
    }

    [Test]
    public void ChainedConfiguration_AccumulatesAllSettings()
    {
        var sys = new DeclarativeSystem
        {
            ConfigureAction = b => b
                .Name("Chained")
                .Priority(SystemPriority.High)
                .Phase(Phase.Simulation)
                .Reads<CompA>()
                .Writes<CompB>()
                .ReadsSnapshot<CompC>(),
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Phases(Phase.Input, Phase.Simulation, Phase.Output)
            .Add(sys)
            .Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "Chained");
        Assert.That(def.Priority, Is.EqualTo(SystemPriority.High));
        Assert.That(def.PhaseIndex, Is.EqualTo(1)); // Simulation in default phase order
        Assert.That(def.Access.Reads, Does.Contain(typeof(CompA)));
        Assert.That(def.Access.Writes, Does.Contain(typeof(CompB)));
        Assert.That(def.Access.ReadsSnapshot, Does.Contain(typeof(CompC)));
    }

    // ─── Layer B: Before edge ─────────────────────────────────────────

    [Test]
    public void Before_AddsEdgeFromCurrentToTarget()
    {
        var sysA = new DeclarativeSystem { ConfigureAction = b => b.Name("A").Before("B") };
        var sysB = new DeclarativeSystem { ConfigureAction = b => b.Name("B") };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(sysA)
            .Add(sysB)
            .Build(_registry.Runtime);

        var aDef = scheduler.Systems.First(s => s.Name == "A");
        var bDef = scheduler.Systems.First(s => s.Name == "B");

        // A.Before(B) means: edge A → B, so B's predecessor count increases
        Assert.That(bDef.PredecessorCount, Is.EqualTo(1));
        Assert.That(aDef.Successors, Does.Contain(bDef.Index));
    }

    [Test]
    public void BeforeAndAfter_OnSameSystem_DetectedAsCycle()
    {
        // X.After(Y) creates edge Y -> X
        // X.Before(Y) creates edge X -> Y
        // Together: cycle Y -> X -> Y
        var sysX = new DeclarativeSystem { ConfigureAction = b => b.Name("X").After("Y").Before("Y") };
        var sysY = new DeclarativeSystem { ConfigureAction = b => b.Name("Y") };

        var dag = RuntimeSchedule.Create()
            .PublicTrack.DeclareDag("Test")
            .Add(sysX)
            .Add(sysY);

        var ex = Assert.Throws<InvalidOperationException>(() => dag.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("cycle").IgnoreCase);
    }

    // ─── Layer C: declaration accumulation ────────────────────────────

    [Test]
    public void Reads_SingleCall_AddsToReadsSet()
    {
        var sys = new DeclarativeSystem { ConfigureAction = b => b.Name("R").Reads<CompA>() };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(sys)
            .Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "R");
        Assert.That(def.Access.Reads, Does.Contain(typeof(CompA)));
        Assert.That(def.Access.Reads.Count, Is.EqualTo(1));
    }

    [Test]
    public void Reads_BatchOverload_EquivalentToIndividualCalls()
    {
        var batch = new DeclarativeSystem
        {
            ConfigureAction = b => b.Name("Batch").Reads<CompA, CompB, CompC, CompD>(),
        };
        var individual = new DeclarativeSystem
        {
            ConfigureAction = b => b.Name("Individual").Reads<CompA>().Reads<CompB>().Reads<CompC>().Reads<CompD>(),
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(batch)
            .Add(individual)
            .Build(_registry.Runtime);

        var batchDef = scheduler.Systems.First(s => s.Name == "Batch");
        var individualDef = scheduler.Systems.First(s => s.Name == "Individual");

        Assert.That(batchDef.Access.Reads, Is.EquivalentTo(individualDef.Access.Reads));
        Assert.That(batchDef.Access.Reads.Count, Is.EqualTo(4));
    }

    [Test]
    public void Writes_BatchOverload_EquivalentToIndividualCalls()
    {
        var batch = new DeclarativeSystem
        {
            ConfigureAction = b => b.Name("WBatch").Writes<CompA, CompB, CompC>(),
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(batch)
            .Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "WBatch");
        Assert.That(def.Access.Writes, Is.EquivalentTo(new[] { typeof(CompA), typeof(CompB), typeof(CompC) }));
    }

    [Test]
    public void ReadsFresh_And_ReadsSnapshot_AreDistinctSets()
    {
        var sys = new DeclarativeSystem
        {
            ConfigureAction = b => b.Name("Mix").ReadsFresh<CompA>().ReadsSnapshot<CompB>(),
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(sys)
            .Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "Mix");
        Assert.That(def.Access.ReadsFresh, Does.Contain(typeof(CompA)));
        Assert.That(def.Access.ReadsFresh, Does.Not.Contain(typeof(CompB)));
        Assert.That(def.Access.ReadsSnapshot, Does.Contain(typeof(CompB)));
        Assert.That(def.Access.ReadsSnapshot, Does.Not.Contain(typeof(CompA)));
    }

    [Test]
    public void DuplicateDeclaration_IsDeduped()
    {
        var sys = new DeclarativeSystem
        {
            ConfigureAction = b => b.Name("Dup").Reads<CompA>().Reads<CompA>().Reads<CompA>(),
        };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(sys)
            .Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "Dup");
        Assert.That(def.Access.Reads.Count, Is.EqualTo(1));
    }

    [Test]
    public void EventQueueAndResource_DeclarationsAccumulate()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 }).PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("Q", 16);

        var sys = new DeclarativeSystem
        {
            ConfigureAction = b => b
                .Name("Eventy")
                .WritesEvents(queue)
                .ReadsResource("PhysicsWorld")
                .WritesResource("SpatialIndex")
                .ExclusivePhase(),
        };

        using var scheduler = dag.Add(sys).Build(_registry.Runtime);

        var def = scheduler.Systems.First(s => s.Name == "Eventy");
        Assert.That(def.Access.WritesEvents, Does.Contain(queue));
        Assert.That(def.Access.ReadsResources, Does.Contain("PhysicsWorld"));
        Assert.That(def.Access.WritesResources, Does.Contain("SpatialIndex"));
        Assert.That(def.Access.ExclusivePhase, Is.True);
    }

    [Test]
    public void HasAnyDeclaration_FalseForUndeclared_TrueWhenAccessSet()
    {
        var undeclared = new DeclarativeSystem { ConfigureAction = b => b.Name("Plain") };
        var declared = new DeclarativeSystem { ConfigureAction = b => b.Name("Declared").Reads<CompA>() };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { BaseTickRate = 1000, WorkerCount = 1 })
            .PublicTrack.DeclareDag("Test")
            .Add(undeclared)
            .Add(declared)
            .Build(_registry.Runtime);

        var plainDef = scheduler.Systems.First(s => s.Name == "Plain");
        var declaredDef = scheduler.Systems.First(s => s.Name == "Declared");

        Assert.That(plainDef.Access.HasAnyDeclaration, Is.False);
        Assert.That(declaredDef.Access.HasAnyDeclaration, Is.True);
    }

    [Test]
    public void NullArguments_ToDeclarationMethods_Throw()
    {
        var b = new SystemBuilder();

        Assert.Throws<ArgumentNullException>(() => b.WritesEvents(null));
        Assert.Throws<ArgumentNullException>(() => b.ReadsEvents(null));
        Assert.Throws<ArgumentNullException>(() => b.WritesResource(null));
        Assert.Throws<ArgumentNullException>(() => b.ReadsResource(null));
    }
}
