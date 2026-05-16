using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Integration tests for event queue wiring through TyphonRuntime — verifies TickContext.ConsumedQueues
/// and reactive skip behavior when queues are empty/populated.
/// </summary>
[NonParallelizable]
[TestFixture]
class EventQueueIntegrationTests : TestBase<EventQueueIntegrationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void ConsumedQueues_ExposedInTickContext()
    {
        using var dbe = SetupEngine();

        EventQueue<int> queue = null;
        EventQueueBase[] capturedQueues = null;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            queue = schedule.CreateEventQueue<int>("TestEvents");

            schedule
                .CallbackSystem("Producer", _ => queue.Push(42))
                .CallbackSystem("Consumer", ctx =>
                {
                    if (captured == 0)
                    {
                        capturedQueues = ctx.ConsumedQueues;
                        Interlocked.Exchange(ref captured, 1);
                    }
                }, after: "Producer")
                .Produces("Producer", queue)
                .Consumes("Consumer", queue);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(capturedQueues, Is.Not.Null, "Consumer should receive ConsumedQueues");
        Assert.That(capturedQueues.Length, Is.EqualTo(1));
        Assert.That(capturedQueues[0], Is.SameAs(queue));
    }

    [Test]
    public void NoConsumedQueues_FieldIsNull()
    {
        using var dbe = SetupEngine();

        EventQueueBase[] capturedQueues = new EventQueueBase[1]; // Non-null to detect change
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("System", ctx =>
            {
                if (captured == 0)
                {
                    capturedQueues = ctx.ConsumedQueues;
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(capturedQueues, Is.Null, "System with no consumed queues should have null ConsumedQueues");
    }

    [Test]
    public void Producer_Consumer_EventsFlowBetweenSystems()
    {
        using var dbe = SetupEngine();

        EventQueue<int> queue = null;
        var receivedSum = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            queue = schedule.CreateEventQueue<int>("Damage");

            schedule
                .CallbackSystem("Combat", _ =>
                {
                    queue.Push(10);
                    queue.Push(20);
                    queue.Push(30);
                })
                .CallbackSystem("LootDrop", ctx =>
                {
                    if (ctx.ConsumedQueues != null)
                    {
                        var q = (EventQueue<int>)ctx.ConsumedQueues[0];
                        Span<int> events = stackalloc int[16];
                        var count = q.Drain(events);
                        for (var i = 0; i < count; i++)
                        {
                            Interlocked.Add(ref receivedSum, events[i]);
                        }
                    }

                    Interlocked.Increment(ref ticksSeen);
                }, after: "Combat")
                .Produces("Combat", queue)
                .Consumes("LootDrop", queue);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Each tick: 10 + 20 + 30 = 60. After 2+ ticks: >= 120
        Assert.That(receivedSum, Is.GreaterThanOrEqualTo(120),
            "Consumer should receive events pushed by producer");
    }

    [Test]
    public void Systems_ExposedPublicly()
    {
        using var dbe = SetupEngine();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Alpha", _ => { })
                .CallbackSystem("Beta", _ => { }, after: "Alpha");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        var systems = runtime.UserSystems;

        Assert.That(systems, Is.Not.Null);
        Assert.That(systems.Length, Is.EqualTo(2));
        Assert.That(systems[0].Name, Is.EqualTo("Alpha"));
        Assert.That(systems[1].Name, Is.EqualTo("Beta"));
        Assert.That(systems[0].Type, Is.EqualTo(SystemType.CallbackSystem));
    }
}
