using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests for <see cref="ChunkedCallbackSystem{TContext}"/> — typed context binding, typed ShouldRun /
/// Prepare hooks, scheduler integration, and Start-time validation.
/// </summary>
[TestFixture]
public class TypedContextSystemTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TypedContextTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    private sealed class TyCtx
    {
        public bool ShouldRunFlag = true;
        public int PrepareReturnValue = -1;
        public bool PrepareShouldThrow;
        public bool ShouldRunShouldThrow;
        public int ExecuteCount;
        public TyCtx SeenInsideShouldRun;
        public TyCtx SeenInsidePrepare;
    }

    private sealed class TypedSystem : ChunkedCallbackSystem<TyCtx>
    {
        public string SystemName = "Typed";
        public string AfterName;

        protected override void Configure(SystemBuilder<TyCtx> b)
        {
            b.Name(SystemName);
            if (AfterName != null) b.After(AfterName);
        }

        protected override bool ShouldRun(TyCtx ctx)
        {
            ctx.SeenInsideShouldRun = ctx;
            if (ctx.ShouldRunShouldThrow) throw new InvalidOperationException("ShouldRun blew up");
            return ctx.ShouldRunFlag;
        }

        protected override int Prepare(TyCtx ctx)
        {
            ctx.SeenInsidePrepare = ctx;
            if (ctx.PrepareShouldThrow) throw new InvalidOperationException("Prepare blew up");
            return ctx.PrepareReturnValue;
        }

        protected override void Execute(TickContext _) => Interlocked.Increment(ref Context.ExecuteCount);
    }

    private sealed class UntypedChunkedSystem : ChunkedCallbackSystem
    {
        public string SystemName = "UntypedChunked";
        public string AfterName;
        public int ExecuteCount;

        protected override void Configure(SystemBuilder b)
        {
            b.Name(SystemName);
            if (AfterName != null) b.After(AfterName);
        }

        protected override void Execute(TickContext _) => Interlocked.Increment(ref ExecuteCount);
    }

    private static void RunOneTick(DagScheduler scheduler)
    {
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
    }

    [Test]
    public void TypedContextSystem_BindContext_MakesContextReadable()
    {
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareReturnValue = -1 };
        var sys = new TypedSystem();

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(sys)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        Assert.That(ctx.SeenInsideShouldRun, Is.SameAs(ctx), "Context must be bound and visible inside ShouldRun");
        Assert.That(ctx.SeenInsidePrepare, Is.SameAs(ctx), "Context must be bound and visible inside Prepare");
        Assert.That(ctx.ExecuteCount, Is.GreaterThanOrEqualTo(1), "Execute should fire when ShouldRun=true and Prepare returns -1 (no opinion)");
    }

    [Test]
    public void TypedShouldRun_ReturnsFalse_SystemSkipped_SuccessorsExecute()
    {
        var ctx = new TyCtx { ShouldRunFlag = false, PrepareReturnValue = -1 };
        var typed = new TypedSystem { SystemName = "A" };
        var successor = new UntypedChunkedSystem { SystemName = "B", AfterName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(typed)
            .Add(successor)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        Assert.That(ctx.ExecuteCount, Is.EqualTo(0), "Typed A should be skipped (ShouldRun=false)");
        Assert.That(successor.ExecuteCount, Is.GreaterThanOrEqualTo(1), "Successor B must still run");
    }

    [Test]
    public void TypedPrepare_ReturnsZero_SystemSkipped_SuccessorsExecute()
    {
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareReturnValue = 0 };
        var typed = new TypedSystem { SystemName = "A" };
        var successor = new UntypedChunkedSystem { SystemName = "B", AfterName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(typed)
            .Add(successor)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        Assert.That(ctx.ExecuteCount, Is.EqualTo(0), "Typed A should be skipped (Prepare=0)");
        Assert.That(successor.ExecuteCount, Is.GreaterThanOrEqualTo(1), "Successor B must still run");
    }

    [Test]
    public void TypedPrepare_ReturnsPositive_SetsRuntimeChunkCount()
    {
        // Verifies Prepare>0 writes to SystemDefinition.RuntimeChunkCount before dispatch.
        // We don't need chunked dispatch (no TyphonRuntime here) — just verify the side-effect on the SystemDefinition.
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareReturnValue = 7 };
        var typed = new TypedSystem { SystemName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(typed)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        // Find A's definition and assert RuntimeChunkCount was set.
        SystemDefinition def = null;
        for (var i = 0; i < scheduler.Systems.Length; i++)
        {
            if (scheduler.Systems[i].Name == "A") { def = scheduler.Systems[i]; break; }
        }
        Assert.That(def, Is.Not.Null);
        Assert.That(def.RuntimeChunkCount, Is.EqualTo(7), "Prepare>0 should set RuntimeChunkCount for downstream ParallelQueryPrepareCallback to read");
    }

    [Test]
    public void TypedPrepare_Throws_SystemFailed_SuccessorsSkippedDueToFailure()
    {
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareShouldThrow = true };
        var typed = new TypedSystem { SystemName = "A" };
        var successor = new UntypedChunkedSystem { SystemName = "B", AfterName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(typed)
            .Add(successor)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        Assert.That(ctx.ExecuteCount, Is.EqualTo(0), "A's Execute must not fire when Prepare throws");
        Assert.That(successor.ExecuteCount, Is.EqualTo(0), "B is skipped because its predecessor A failed");
    }

    [Test]
    public void TypedShouldRun_Throws_SystemFailed_SuccessorsSkippedDueToFailure()
    {
        var ctx = new TyCtx { ShouldRunShouldThrow = true };
        var typed = new TypedSystem { SystemName = "A" };
        var successor = new UntypedChunkedSystem { SystemName = "B", AfterName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(typed)
            .Add(successor)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        Assert.That(ctx.ExecuteCount, Is.EqualTo(0), "A's Execute must not fire when ShouldRun throws");
        Assert.That(successor.ExecuteCount, Is.EqualTo(0), "B is skipped because its predecessor A failed");
    }

    private sealed class ChainedFluentSystem : ChunkedCallbackSystem<TyCtx>
    {
        protected override void Configure(SystemBuilder<TyCtx> b) => b
            .Name("Chained")
            .Internal()
            .ChunkedParallel(2)
            .After("Root");

        protected override void Execute(TickContext _) { }
    }

    [Test]
    public void TypedSystemBuilder_ChainedFluentCalls_ForwardToInner()
    {
        var typed = new ChainedFluentSystem();

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .CallbackSystem("Root", _ => { })
            .Add(typed)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(new TyCtx { ShouldRunFlag = true, PrepareReturnValue = 2 });

        SystemDefinition def = null;
        for (var i = 0; i < scheduler.Systems.Length; i++)
        {
            if (scheduler.Systems[i].Name == "Chained")
            {
                def = scheduler.Systems[i];
                break;
            }
        }
        Assert.That(def, Is.Not.Null, "Chained system should be registered with the name set via typed builder");
        Assert.That(def.IsInternal, Is.True, "Internal() forwarded through typed builder");
        Assert.That(def.ExplicitChunkCount, Is.EqualTo(2), "ChunkedParallel(2) forwarded through typed builder");
    }

    [Test]
    public void ContextLessChunkedSystem_StillWorks_NoTypedContextNeeded()
    {
        // Regression: an untyped ChunkedCallbackSystem must still execute correctly through the new gate.
        // No RegisterContext call; the system's OnPrepare returns -1 (no opinion), Execute runs.
        var sys = new UntypedChunkedSystem { SystemName = "Plain" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(sys)
            .Build(_registry.Runtime);

        RunOneTick(scheduler);

        Assert.That(sys.ExecuteCount, Is.GreaterThanOrEqualTo(1), "Untyped chunked system should still fire Execute");
    }

    [Test]
    public void RegisterContextBeforeStart_BindsAllMatchingSystems()
    {
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareReturnValue = -1 };
        var a = new TypedSystem { SystemName = "A" };
        var b = new TypedSystem { SystemName = "B", AfterName = "A" };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 })
            .Add(a)
            .Add(b)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        RunOneTick(scheduler);

        // Both A and B contributed to ExecuteCount → context was bound on both.
        Assert.That(ctx.ExecuteCount, Is.GreaterThanOrEqualTo(2), "Both typed systems should bind to the same context and run");
    }

    [Test]
    public void UnboundTypedSystem_StartThrows()
    {
        // Register a typed system but DO NOT call RegisterContext. Start() must throw.
        var typed = new TypedSystem();

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .Add(typed)
            .Build(_registry.Runtime);

        var ex = Assert.Throws<InvalidOperationException>(() => scheduler.Start());
        Assert.That(ex.Message, Does.Contain("TyCtx"), "Error message should name the missing context type");
    }

    [Test]
    public void RegisterContextAfterStart_Throws()
    {
        var typed = new TypedSystem();
        var ctx = new TyCtx { ShouldRunFlag = true, PrepareReturnValue = -1 };

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .Add(typed)
            .Build(_registry.Runtime);

        scheduler.RegisterContext(ctx);
        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => scheduler.RegisterContext(new TyCtx()));

        scheduler.Shutdown();
    }
}
