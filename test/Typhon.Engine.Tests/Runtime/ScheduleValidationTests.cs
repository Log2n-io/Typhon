using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
class ScheduleValidationTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ValidationTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    [Test]
    public void Build_DuplicateSystemNames_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.CallbackSystem("Dup", _ => { });
        schedule.CallbackSystem("Dup", _ => { });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("Duplicate system name"));
    }

    [Test]
    public void Build_DuplicateClassBasedNames_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new NamedCallback("Dup"));
        schedule.Add(new NamedCallback("Dup"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("Duplicate system name"));
    }

    [Test]
    public void Build_ChangeFilterOnCallbackSystem_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new CallbackWithChangeFilter());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChangeFilter"));
    }

    [Test]
    public void Build_ParallelOnCallbackSystem_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new CallbackWithParallel());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("Parallel"));
    }

    [Test]
    public void Build_ChangeFilterWithoutInput_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new QueryWithChangeFilter());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChangeFilter requires an Input View"));
    }

    [Test]
    public void Build_ParallelOnQuerySystem_DoesNotThrow()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new QueryWithParallel());

        using var scheduler = schedule.Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(1));
    }

    [Test]
    public void Build_ParallelWithoutInput_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.Add(new QueryWithParallelNoInput());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("Parallel requires an Input View"));
    }

    [Test]
    public void Build_ChunksPerWorker_Zero_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: 0f);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
    }

    [Test]
    public void Build_ChunksPerWorker_Negative_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: -1f);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
    }

    [Test]
    public void Build_ChunksPerWorker_NaN_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: float.NaN);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
    }

    [Test]
    public void Build_ChunksPerWorker_NonDefaultWithoutParallel_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, chunksPerWorker: 2f);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
        Assert.That(ex.Message, Does.Contain("parallel"));
    }

    [Test]
    public void Build_ChunksPerWorker_DefaultWithoutParallel_Succeeds()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Good", _ => { }, chunksPerWorker: 1f);

        using var scheduler = schedule.Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(1));
    }

    [Test]
    public void Build_ChunksPerWorker_FractionalParallel_Succeeds()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Good", _ => { }, input: () => null, parallel: true, chunksPerWorker: 1.5f);

        using var scheduler = schedule.Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(1));
        Assert.That(scheduler.Systems[0].ChunksPerWorker, Is.EqualTo(1.5f));
    }

    [Test]
    public void Build_ChunksPerWorker_BelowOne_Throws()
    {
        // ChunksPerWorker is an oversubscription multiplier — values below 1 would *reduce* parallelism below the
        // worker count, which is confusing given the name and not a use case the knob was designed for. Reject.
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: 0.5f);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
        Assert.That(ex.Message, Does.Contain("[1.0, 64.0]"));
    }

    [Test]
    public void Build_ChunksPerWorker_AboveUpperBound_Throws()
    {
        // Absurd factors like 1e10 cast unchecked to int.MinValue (then Math.Max(1, MIN) = 1), silently collapsing
        // the chunk cap to 1 chunk — the exact opposite of what the user asked for. The 64 ceiling keeps the (int)
        // round well clear of overflow and is already past the point where ParallelQueryMinChunkSize kicks in.
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: 100f);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
        Assert.That(ex.Message, Does.Contain("[1.0, 64.0]"));
    }

    [Test]
    public void Build_ChunksPerWorker_AtBoundaries_Succeeds()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Lower", _ => { }, input: () => null, parallel: true, chunksPerWorker: 1f);
        schedule.QuerySystem("Upper", _ => { }, input: () => null, parallel: true, chunksPerWorker: 64f);

        using var scheduler = schedule.Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(2));
    }

    [Test]
    public void Build_ChunksPerWorker_PositiveInfinity_Throws()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.QuerySystem("Bad", _ => { }, input: () => null, parallel: true, chunksPerWorker: float.PositiveInfinity);

        var ex = Assert.Throws<InvalidOperationException>(() => schedule.Build(_registry.Runtime));
        Assert.That(ex.Message, Does.Contain("ChunksPerWorker"));
    }

    [Test]
    public void Build_UniqueNames_Succeeds()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.CallbackSystem("A", _ => { });
        schedule.CallbackSystem("B", _ => { });

        using var scheduler = schedule.Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(2));
    }

    // ── Test system implementations ──

    class NamedCallback : CallbackSystem
    {
        private readonly string _name;

        public NamedCallback(string name)
        {
            _name = name;
        }

        protected override void Configure(SystemBuilder b)
        {
            b.Name(_name);
        }

        protected override void Execute(TickContext ctx) { }
    }

    class CallbackWithChangeFilter : CallbackSystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("BadCallback");
            b.ChangeFilter(typeof(int));
        }

        protected override void Execute(TickContext ctx) { }
    }

    class CallbackWithParallel : CallbackSystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("BadCallback");
            b.Parallel();
        }

        protected override void Execute(TickContext ctx) { }
    }

    class QueryWithChangeFilter : QuerySystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("GoodQuery");
            b.ChangeFilter(typeof(int));
        }

        protected override void Execute(TickContext ctx) { }
    }

    class QueryWithParallel : QuerySystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("ParallelQuery");
            b.Parallel();
            b.Input(() => null); // Dummy — InputFactory stored but not called during Build
        }

        protected override void Execute(TickContext ctx) { }
    }

    class QueryWithParallelNoInput : QuerySystem
    {
        protected override void Configure(SystemBuilder b)
        {
            b.Name("BadParallelQuery");
            b.Parallel();
            // No Input — should be rejected
        }

        protected override void Execute(TickContext ctx) { }
    }
}
