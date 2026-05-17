using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

[TestFixture]
public class DagSchedulerTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "Test" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    /// <summary>Declares a fresh single-DAG schedule on the Public track with the given worker count.</summary>
    private static Dag NewDag(int workerCount = 1, int tickRate = 1000)
        => RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = tickRate }).PublicTrack.DeclareDag("Test");

    /// <summary>
    /// Runs the scheduler for a single tick and returns. Uses a gate flag to prevent
    /// capturing data from subsequent ticks.
    /// </summary>
    private static void RunOneTick(DagScheduler scheduler)
    {
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Correctness: Single-threaded mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SingleWorker_LinearChain_CorrectOrder()
    {
        var executionOrder = new List<string>();
        var captured = 0;

        using var scheduler = NewDag(workerCount: 1)
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
        RunOneTick(scheduler);

        Assert.That(executionOrder, Is.EqualTo(new[] { "A", "B", "C" }));
    }

    [Test]
    public void SingleWorker_FanOut_AllExecute()
    {
        var executed = new ConcurrentBag<string>();
        var captured = 0;

        using var scheduler = NewDag(workerCount: 1)
            .CallbackSystem("Root", _ => { if (captured == 0) { executed.Add("Root"); } })
            .CallbackSystem("B", _ => { if (captured == 0) { executed.Add("B"); } }, after: "Root")
            .CallbackSystem("C", _ => { if (captured == 0) { executed.Add("C"); } }, after: "Root")
            .CallbackSystem("D", _ =>
            {
                if (captured == 0)
                {
                    executed.Add("D");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "Root")
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        Assert.That(executed, Has.Count.EqualTo(4));
        Assert.That(executed, Does.Contain("Root"));
        Assert.That(executed, Does.Contain("B"));
        Assert.That(executed, Does.Contain("C"));
        Assert.That(executed, Does.Contain("D"));
    }

    // ═══════════════════════════════════════════════════════════════
    // Correctness: Multi-threaded mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MultiWorker_DependencyRespected()
    {
        // A → (B, C) → D
        // D must execute after both B and C
        var timestamps = new ConcurrentDictionary<string, long>();
        var captured = 0;

        using var scheduler = NewDag(workerCount: 4)
            .CallbackSystem("A", _ =>
            {
                if (captured == 0)
                {
                    timestamps["A"] = Stopwatch.GetTimestamp();
                }
            })
            .CallbackSystem("B", _ =>
            {
                if (captured == 0)
                {
                    Thread.SpinWait(100);
                    timestamps["B"] = Stopwatch.GetTimestamp();
                }
            }, after: "A")
            .CallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    Thread.SpinWait(100);
                    timestamps["C"] = Stopwatch.GetTimestamp();
                }
            }, after: "A")
            .CallbackSystem("D", _ =>
            {
                if (captured == 0)
                {
                    timestamps["D"] = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref captured, 1);
                }
            }, afterAll: ["B", "C"])
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        Assert.That(timestamps, Has.Count.EqualTo(4), "All systems must have executed");
        Assert.That(timestamps["D"], Is.GreaterThan(timestamps["B"]), "D must execute after B");
        Assert.That(timestamps["D"], Is.GreaterThan(timestamps["C"]), "D must execute after C");
        Assert.That(timestamps["B"], Is.GreaterThan(timestamps["A"]), "B must execute after A");
        Assert.That(timestamps["C"], Is.GreaterThan(timestamps["A"]), "C must execute after A");
    }

    [Test]
    public void Callback_InlineContinuation_D3()
    {
        // A → B → C (all CallbackSystem)
        // With inline continuation (D3), B and C should run on the same thread
        var threadIds = new ConcurrentDictionary<string, int>();
        var captured = 0;

        using var scheduler = NewDag(workerCount: 4)
            .CallbackSystem("A", _ => { if (captured == 0) { threadIds["A"] = Environment.CurrentManagedThreadId; } })
            .CallbackSystem("B", _ => { if (captured == 0) { threadIds["B"] = Environment.CurrentManagedThreadId; } }, after: "A")
            .CallbackSystem("C", _ =>
            {
                if (captured == 0)
                {
                    threadIds["C"] = Environment.CurrentManagedThreadId;
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "B")
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        Assert.That(threadIds, Has.Count.EqualTo(3));
        // B is a CallbackSystem successor of A → inlined (D3)
        // C is a CallbackSystem successor of B → inlined (D3)
        Assert.That(threadIds["B"], Is.EqualTo(threadIds["C"]),
            "Inline continuation: B and C should run on the same thread");
    }

    // ═══════════════════════════════════════════════════════════════
    // Pipeline systems
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PipelineSystem_AllChunksProcessed()
    {
        var chunkCounter = 0;
        const int totalChunks = 100;

        using var scheduler = NewDag(workerCount: 4)
            .PipelineSystem("Physics", (chunk, total) =>
            {
                Interlocked.Increment(ref chunkCounter);
            }, totalChunks)
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        // After at least 1 tick, total chunks should be a multiple of totalChunks
        Assert.That(chunkCounter % totalChunks, Is.EqualTo(0), "All chunks must be processed per tick");
        Assert.That(chunkCounter, Is.GreaterThanOrEqualTo(totalChunks));
    }

    [Test]
    public void PipelineSystem_MultiWorkerDistribution()
    {
        var workerThreadIds = new ConcurrentBag<int>();
        var captured = 0;
        const int totalChunks = 100;

        using var scheduler = NewDag(workerCount: 4)
            .PipelineSystem("Physics", (chunk, total) =>
            {
                if (captured == 0)
                {
                    workerThreadIds.Add(Environment.CurrentManagedThreadId);
                    Thread.SpinWait(50);
                    if (chunk == total - 1)
                    {
                        Interlocked.Exchange(ref captured, 1);
                    }
                }
            }, totalChunks)
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        var distinctWorkers = new HashSet<int>(workerThreadIds);
        Assert.That(distinctWorkers.Count, Is.GreaterThan(1),
            "Multiple workers should have participated in chunk processing");
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple ticks
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MultipleTicks_StateReset()
    {
        var tickCount = 0;

        using var scheduler = NewDag(workerCount: 2)
            .CallbackSystem("Counter", _ => Interlocked.Increment(ref tickCount))
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 10, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(tickCount, Is.GreaterThanOrEqualTo(10),
            "CallbackSystem should execute once per tick");
    }

    [Test]
    public void PipelineSystem_ChunksResetEachTick()
    {
        var totalChunksProcessed = 0;
        const int chunksPerTick = 20;

        using var scheduler = NewDag(workerCount: 4)
            .PipelineSystem("Work", (chunk, total) =>
            {
                Interlocked.Increment(ref totalChunksProcessed);
            }, chunksPerTick)
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ticksCompleted = scheduler.CurrentTickNumber;
        Assert.That(totalChunksProcessed, Is.EqualTo(chunksPerTick * ticksCompleted),
            $"Each of {ticksCompleted} ticks should process exactly {chunksPerTick} chunks");
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Shutdown_Clean()
    {
        using var scheduler = NewDag(workerCount: 4)
            .CallbackSystem("A", _ => { })
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        // Verify the scheduler stopped (no more ticks)
        var tickAfterShutdown = scheduler.CurrentTickNumber;
        Thread.Sleep(50);
        Assert.That(scheduler.CurrentTickNumber, Is.EqualTo(tickAfterShutdown),
            "No more ticks should execute after shutdown");
    }

    [Test]
    public void SingleThreadedMode_Works()
    {
        var executionOrder = new List<string>();
        var captured = 0;

        // Complex DAG: A → (B, C) → D → E
        using var scheduler = NewDag(workerCount: 1)
            .CallbackSystem("A", _ => { if (captured == 0) { executionOrder.Add("A"); } })
            .CallbackSystem("B", _ => { if (captured == 0) { executionOrder.Add("B"); } }, after: "A")
            .CallbackSystem("C", _ => { if (captured == 0) { executionOrder.Add("C"); } }, after: "A")
            .CallbackSystem("D", _ => { if (captured == 0) { executionOrder.Add("D"); } }, afterAll: ["B", "C"])
            .CallbackSystem("E", _ =>
            {
                if (captured == 0)
                {
                    executionOrder.Add("E");
                    Interlocked.Exchange(ref captured, 1);
                }
            }, after: "D")
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        Assert.That(executionOrder, Has.Count.EqualTo(5));

        var posA = executionOrder.IndexOf("A");
        var posB = executionOrder.IndexOf("B");
        var posC = executionOrder.IndexOf("C");
        var posD = executionOrder.IndexOf("D");
        var posE = executionOrder.IndexOf("E");

        Assert.That(posA, Is.LessThan(posB));
        Assert.That(posA, Is.LessThan(posC));
        Assert.That(posB, Is.LessThan(posD));
        Assert.That(posC, Is.LessThan(posD));
        Assert.That(posD, Is.LessThan(posE));
    }

    [Test]
    public void SingleThreadedMode_PipelineSystem_AllChunksProcessed()
    {
        var processedChunks = new List<int>();
        var captured = 0;
        const int totalChunks = 10;

        using var scheduler = NewDag(workerCount: 1)
            .PipelineSystem("Work", (chunk, total) =>
            {
                if (captured == 0)
                {
                    lock (processedChunks)
                    {
                        processedChunks.Add(chunk);
                    }

                    if (chunk == total - 1)
                    {
                        Interlocked.Exchange(ref captured, 1);
                    }
                }
            }, totalChunks)
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        processedChunks.Sort();
        Assert.That(processedChunks, Has.Count.EqualTo(totalChunks));
        for (var i = 0; i < totalChunks; i++)
        {
            Assert.That(processedChunks[i], Is.EqualTo(i));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Mixed DAG (CallbackSystem + PipelineSystem)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void MixedDAG_CallbackAndPipeline_CorrectExecution()
    {
        // Input(CallbackSystem) → Physics(PipelineSystem,50) → Output(CallbackSystem)
        var inputExecuted = 0;
        var outputExecuted = 0;
        var physicsChunks = 0;
        const int totalChunks = 50;

        using var scheduler = NewDag(workerCount: 4)
            .CallbackSystem("Input", _ => Interlocked.Increment(ref inputExecuted))
            .PipelineSystem("Physics", (chunk, total) => Interlocked.Increment(ref physicsChunks), totalChunks, after: "Input")
            .CallbackSystem("Output", _ => Interlocked.Increment(ref outputExecuted), after: "Physics")
            .Build(_registry.Runtime);
        RunOneTick(scheduler);

        Assert.That(inputExecuted, Is.GreaterThanOrEqualTo(1));
        Assert.That(physicsChunks, Is.GreaterThanOrEqualTo(totalChunks));
        Assert.That(outputExecuted, Is.GreaterThanOrEqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Telemetry_TickDuration_Recorded()
    {
        using var scheduler = NewDag(workerCount: 2)
            .CallbackSystem("A", _ => Thread.SpinWait(1000))
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(3));

        ref readonly var tick = ref ring.GetTick(ring.NewestTick);
        Assert.That(tick.ActualDurationMs, Is.GreaterThan(0f));
        Assert.That(tick.ActiveSystemCount, Is.EqualTo(1));
    }

    [Test]
    public void Telemetry_TransitionLatency_RecordedForNonRoot()
    {
        // A → B: B's transition latency should be > 0
        using var scheduler = NewDag(workerCount: 2)
            .CallbackSystem("A", _ => Thread.SpinWait(500))
            .CallbackSystem("B", _ => { }, after: "A")
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var systems = ring.GetSystemMetrics(ring.NewestTick);

        // System B (index 1) should have transition latency >= 0
        Assert.That(systems[1].TransitionLatencyUs, Is.GreaterThanOrEqualTo(0f),
            "Non-root system should have measurable transition latency");
        Assert.That(systems[1].DurationUs, Is.GreaterThanOrEqualTo(0f));
    }

    [TestCase(1)] // single-threaded topological dispatch — RunSystemSingleThreaded
    [TestCase(4)] // multi-threaded fan-out dispatch — OnSystemComplete → ExecuteInline
    public void Telemetry_ReadyTick_NotInflatedBySibling(int workerCount)
    {
        // Regression for the #354 Critical-Path diagnosis: a successor's `readyUs` must reflect when
        // its predecessor completed — NOT when the scheduler got around to dispatching it.
        //
        // Both dispatch paths drifted: the multi-threaded `OnSystemComplete` captured each successor's
        // ready timestamp *inside* the fan-out loop, after any earlier CallbackSystem sibling had run
        // inline (`ExecuteInline`) to completion; the single-threaded `RunSystemSingleThreaded` stamped
        // `ReadyTick` when the topological loop *reached* the system. Either way a later sibling looked
        // gated by an earlier one and fell spuriously off the measured Critical Path.
        //
        // Root → { Fast1, Slow, Fast2 }: all three become ready the instant Root completes, so they
        // MUST share one ready timestamp. `Slow` is declared between the two fast ones and burns real
        // CPU, so pre-fix `Fast2.ReadyTick` would be inflated by `Slow`'s full duration.
        using var scheduler = NewDag(workerCount: workerCount)
            .CallbackSystem("Root", _ => { })
            .CallbackSystem("Fast1", _ => { }, after: "Root")
            .CallbackSystem("Slow", _ => Thread.SpinWait(200_000), after: "Root")
            .CallbackSystem("Fast2", _ => { }, after: "Root")
            .Build(_registry.Runtime);
        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        var systems = scheduler.Telemetry.GetSystemMetrics(scheduler.Telemetry.NewestTick);
        var slowReady = systems[2].ReadyTick;  // Slow

        // All three were made ready by the same predecessor completing — one shared ready timestamp.
        Assert.That(systems[1].ReadyTick, Is.EqualTo(slowReady),
            "Fast1 must become ready when Root completes");
        Assert.That(systems[3].ReadyTick, Is.EqualTo(slowReady),
            "Fast2 must become ready when Root completes — not after the Slow sibling ran");

        // The ready instant must precede the Slow sibling actually starting work.
        Assert.That(systems[3].ReadyTick, Is.LessThanOrEqualTo(systems[2].FirstChunkGrabTick),
            "successor readiness must be stamped before a sibling begins executing");
    }

    [Test]
    public void Telemetry_SystemCount_MatchesDag()
    {
        using var scheduler = NewDag(workerCount: 1)
            .CallbackSystem("A", _ => { })
            .CallbackSystem("B", _ => { }, after: "A")
            .CallbackSystem("C", _ => { }, after: "B")
            .Build(_registry.Runtime);
        Assert.That(scheduler.SystemCount, Is.EqualTo(3));
        Assert.That(scheduler.WorkerCount, Is.EqualTo(1));
    }
}
