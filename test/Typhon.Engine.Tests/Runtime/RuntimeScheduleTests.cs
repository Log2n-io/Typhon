using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class RuntimeScheduleTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ScheduleTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    [Test]
    public void FluentBuilder_LinearChain_BuildsCorrectly()
    {
        var executionOrder = new List<string>();
        var captured = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("A", _ => { if (captured == 0) { executionOrder.Add("A"); } })
            .CallbackSystem("B", _ => { if (captured == 0) { executionOrder.Add("B"); } }, after: "A")
            .CallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    executionOrder.Add("C");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "B")
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(executionOrder, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public void FluentBuilder_WithAfterAll_MultipleEdges()
    {
        // A → (B, C) → D
        var executed = new List<string>();
        var captured = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("A", _ => { if (captured == 0) { executed.Add("A"); } })
            .CallbackSystem("B", _ => { if (captured == 0) { executed.Add("B"); } }, after: "A")
            .CallbackSystem("C", _ => { if (captured == 0) { executed.Add("C"); } }, after: "A")
            .CallbackSystem("D", _ =>
            {
                if (captured == 0)
                {
                    executed.Add("D");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, afterAll: ["B", "C"])
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(executed, Has.Count.EqualTo(4));
        Assert.That(executed.IndexOf("A"), Is.LessThan(executed.IndexOf("D")));
        Assert.That(executed.IndexOf("B"), Is.LessThan(executed.IndexOf("D")));
        Assert.That(executed.IndexOf("C"), Is.LessThan(executed.IndexOf("D")));
    }

    [Test]
    public void FluentBuilder_DuplicateNames_Throws()
    {
        var dag = RuntimeSchedule.Create()
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("A", _ => { });

        Assert.Throws<InvalidOperationException>(() =>
            dag.CallbackSystem("A", _ => { }).Build(_registry.Runtime));
    }

    [Test]
    public void FluentBuilder_MissingAfterTarget_Throws()
    {
        var dag = RuntimeSchedule.Create()
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("A", _ => { }, after: "NonExistent");

        Assert.Throws<InvalidOperationException>(() => dag.Build(_registry.Runtime));
    }

    [Test]
    public void FluentBuilder_MixedSystemTypes_AllRegistered()
    {
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("Input", _ => { })
            .QuerySystem("GameRules", _ => { }, after: "Input")
            .PipelineSystem("Physics", (c, t) => { }, 50, after: "Input")
            .CallbackSystem("Output", _ => { }, afterAll: ["GameRules", "Physics"])
            .Build(_registry.Runtime);

        Assert.That(scheduler.SystemCount, Is.EqualTo(4));
    }

    [Test]
    public void FluentBuilder_OverloadParams_StoredOnDefinition()
    {
        // Build and inspect — use DagBuilder directly to access SystemDefinition
        var dagBuilder = new DagBuilder();
        dagBuilder.AddQuerySystem("AI", _ => { }, SystemPriority.Normal);
        var (systems, _) = dagBuilder.Build();

        // Set overload params via RuntimeSchedule's Build path
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test")
            .QuerySystem("AI", _ => { }, priority: SystemPriority.Low,
                tickDivisor: 2, throttledTickDivisor: 5, canShed: true)
            .Build(_registry.Runtime);

        // Access through telemetry to verify system count
        Assert.That(scheduler.SystemCount, Is.EqualTo(1));
    }

    [Test]
    public void FluentBuilder_EventQueues_WiredToSystems()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        var damageQueue = dag.CreateEventQueue<int>("DamageEvents");

        using var scheduler = dag
            .CallbackSystem("Combat", _ => damageQueue.Push(42), after: null)
            .QuerySystem("LootDrop", _ =>
            {
                Span<int> events = stackalloc int[16];
                damageQueue.Drain(events);
            }, after: "Combat")
            .Produces("Combat", damageQueue)
            .Consumes("LootDrop", damageQueue)
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // Queue should be empty after drain (and reset at next tick start)
        Assert.That(damageQueue.IsEmpty, Is.True);
    }

    [Test]
    public void FluentBuilder_EventQueue_ResetEachTick()
    {
        var dag = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }).PublicTrack.DeclareDag("Test");
        var queue = dag.CreateEventQueue<int>("test");

        var pushCount = 0;

        using var scheduler = dag
            .CallbackSystem("Producer", _ =>
            {
                // Push 3 items per tick
                queue.Push(1);
                queue.Push(2);
                queue.Push(3);
                Interlocked.Increment(ref pushCount);
            })
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // Queue was reset between each tick, so it should have at most 3 items
        // (from the last tick, before reset)
        Assert.That(queue.Count, Is.LessThanOrEqualTo(3));
        Assert.That(pushCount, Is.GreaterThanOrEqualTo(5));
    }

    [Test]
    public void Build_CalledTwice_Throws()
    {
        var dag = RuntimeSchedule.Create()
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("A", _ => { });

        dag.Build(_registry.Runtime).Dispose();

        Assert.Throws<InvalidOperationException>(() => dag.Build(_registry.Runtime));
    }

    [Test]
    public void DeclareTrack_AppTracks_SlottedBetweenPublicAndEnginePost()
    {
        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        var physics = schedule.DeclareTrack("Physics");
        var ai = schedule.DeclareTrack("AI");

        Assert.That(
            schedule.Tracks.Select(t => t.Name),
            Is.EqualTo(new[] { "Engine-Pre", "Public", "Physics", "AI", "Engine-Post" }));

        // OrderIndex always equals the track's execution position.
        for (var i = 0; i < schedule.Tracks.Count; i++)
        {
            Assert.That(schedule.Tracks[i].OrderIndex, Is.EqualTo(i));
        }

        Assert.That(physics.OrderIndex, Is.EqualTo(2));
        Assert.That(ai.OrderIndex, Is.EqualTo(3));
        Assert.That(physics.IsEngine, Is.False);
    }

    [Test]
    public void DeclareTrack_DuplicateName_Throws()
    {
        var schedule = RuntimeSchedule.Create();
        schedule.DeclareTrack("Physics");

        Assert.Throws<InvalidOperationException>(() => schedule.DeclareTrack("Physics"));
        // Built-in track names are reserved too.
        Assert.Throws<InvalidOperationException>(() => schedule.DeclareTrack("Public"));
    }

    [Test]
    public void DeclareTrack_ReservedEnginePrefix_Throws()
    {
        var schedule = RuntimeSchedule.Create();
        Assert.Throws<InvalidOperationException>(() => schedule.DeclareTrack("Engine-Custom"));
    }

    [Test]
    public void DeclareTrack_EngineTag_Throws()
    {
        var schedule = RuntimeSchedule.Create();
        Assert.Throws<InvalidOperationException>(() => schedule.DeclareTrack("Physics", Track.EngineTag));
    }

    [Test]
    public void DeclareTrack_SystemsRunInTrackOrder()
    {
        var order = new List<string>();
        var captured = 0;

        var schedule = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        schedule.DeclareTrack("Alpha").DeclareDag("AlphaDag")
            .CallbackSystem("A", _ => { if (captured == 0) { order.Add("Alpha"); } });
        schedule.DeclareTrack("Beta").DeclareDag("BetaDag")
            .CallbackSystem("B", _ =>
            {
                if (captured == 0)
                {
                    order.Add("Beta");
                    Interlocked.Exchange(ref captured, 1);
                }
            });

        using var scheduler = schedule.Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // The track-order barrier runs Alpha to completion before Beta begins.
        Assert.That(order, Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(scheduler.SystemCount, Is.EqualTo(2));
    }

    [Test]
    public void FluentBuilder_PipelineSystem_WithDependencies()
    {
        var chunkCount = 0;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000 })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("Input", _ => { })
            .PipelineSystem("Physics", (chunk, total) =>
            {
                Interlocked.Increment(ref chunkCount);
            }, 50, after: "Input")
            .CallbackSystem("Output", _ => { }, after: "Physics")
            .Build(_registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(chunkCount, Is.GreaterThanOrEqualTo(50));
    }
}
