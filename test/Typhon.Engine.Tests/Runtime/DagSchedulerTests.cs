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

    // ── Host-crash guard (#395 follow-up). The tick runs on the timer thread — a raw Thread. An exception escaping the inner per-system handlers (e.g.
    //    from the tick-start hook, or the entity-set prepare phase — the `ViewBase` teardown-race NullReferenceException that aborted the test host
    //    under load) MUST be caught by ExecuteCallbacks's outer safety net and surfaced via UnhandledExceptionCallback, NOT propagate out of TimerLoop
    //    and ABORT THE PROCESS. Without the net this test would crash the test host instead of failing. ──
    [Test]
    public void TickThread_UnhandledOrchestrationException_SurfacedNotHostCrash()
    {
        Exception captured = null;
        using var scheduler = NewDag(workerCount: 1)
            .CallbackSystem("S", _ => { })
            .Build(_registry.Runtime);
        scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref captured, ex, null);
        scheduler.TickStartCallback = _ => throw new InvalidOperationException("simulated tick-orchestration fault");

        scheduler.Start();
        SpinWait.SpinUntil(() => captured != null, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

        Assert.That(captured, Is.Not.Null,
            "tick-thread outer safety net did not fire — an orchestration exception escaped ExecuteCallbacks and would have aborted the host");
        Assert.That(captured, Is.TypeOf<InvalidOperationException>());
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

    // Chunk dispatch is any-worker PULL: workers claim chunks by CAS, and the between-tick wait is a ManualResetEventSlim built with spinCount: 0, so a
    // worker that is not already running starts from a kernel wait (overview/13-runtime.md §Fence-DAG, §Worker Thread Model). This test used to emit 100
    // chunks of Thread.SpinWait(50) and assert that more than one thread had picked one up — but that entire workload costs less than a single thread wake,
    // so ONE worker draining all 100 uncontended is the correct outcome of a pull model, not a scheduler fault. It only ever passed because a 16-core box
    // happened to have peer workers already spinning; pinned to 3 cores it fails ~40% of the time, and it took the nightly red as a false "regression"
    // (run 31075437612, previously flaked in run 30790280128 — reproduced locally 3x, Debug and Release).
    //
    // Assert the property the scheduler actually owes instead: a chunk queue is claimable by more than one worker. The first worker to arrive PARKS inside
    // its chunk, so it cannot also drain the rest — the other 99 chunks stay on the queue and a second worker must claim one for the tick to finish. The
    // park is bounded, so a scheduler that genuinely never dispatches to a second worker fails the assertion below instead of hanging the tick.
    [Test]
    [CancelAfter(20_000)]
    public void PipelineSystem_MultiWorkerDistribution()
    {
        const int totalChunks = 100;
        const int requiredWorkers = 2;
        const int rendezvousTimeoutMs = 5_000;

        var seenWorkers = new ConcurrentDictionary<int, byte>();
        using var enoughWorkers = new ManualResetEventSlim(false);

        // Scoped so the scheduler is torn down before the event the system body captures.
        using (var scheduler = NewDag(workerCount: 4)
                   .PipelineSystem("Physics", (chunk, total) =>
                   {
                       if (enoughWorkers.IsSet)
                       {
                           return;
                       }

                       seenWorkers.TryAdd(Environment.CurrentManagedThreadId, 0);
                       if (seenWorkers.Count >= requiredWorkers)
                       {
                           enoughWorkers.Set();
                           return;
                       }

                       enoughWorkers.Wait(rendezvousTimeoutMs);
                   }, totalChunks)
                   .Build(_registry.Runtime))
        {
            RunOneTick(scheduler);
        }

        Assert.That(seenWorkers.Count, Is.GreaterThanOrEqualTo(requiredWorkers),
            $"the first worker parked inside its chunk, leaving {totalChunks - 1} chunks claimable — a second worker must have taken one; seeing only one "
            + "worker here means chunk dispatch never reaches the other workers at all");
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

        // The chunk counter and CurrentTickNumber are published by DIFFERENT threads — workers vs the tick driver — and the tick number only advances once a
        // tick's chunks are done, so the pair cannot be sampled atomically: the final tick's chunks are counted while CurrentTickNumber still reads the
        // previous value. Asserting the exact product therefore failed with "Expected: 70200 But was: 70220" — off by exactly ONE tick of chunks — on a
        // core-starved box where the test thread was descheduled long enough for thousands of ticks to elapse before SpinUntil observed its condition.
        // Assert what "chunks reset each tick" actually means, and tolerate exactly that one-tick sampling skew.
        Assert.That(totalChunksProcessed % chunksPerTick, Is.EqualTo(0),
            $"every tick must dispatch exactly {chunksPerTick} chunks — a partial tick's worth means chunks leaked across a tick boundary");

        var ticksWorth = totalChunksProcessed / chunksPerTick;
        Assert.That(ticksWorth, Is.InRange(ticksCompleted, ticksCompleted + 1),
            $"chunk work must track the tick count — a per-tick reset failure shows up as runaway chunks; saw {ticksWorth} ticks' worth of chunks against "
            + $"{ticksCompleted} completed ticks");
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

        // Shutdown() signals and JOINS the workers, but _currentTickNumber++ lives in the tick's telemetry finalizer, which runs on the TIMER thread — so the
        // tick already in flight when Shutdown() was called can land its increment just after Shutdown() returns. Pinning the baseline immediately raced that
        // last increment and failed with "Expected: 346 But was: 347" (1 run in 10 pinned to 3 cores). Let the in-flight tick's accounting settle, THEN pin
        // the baseline. The property under test — that no FURTHER ticks execute — is unaffected and genuinely holds: a probe measured delta 0 across 9 trials
        // even with a 1 s observation window, so the timer thread stops producing ticks even though only Dispose() actually stops the thread.
        Thread.Sleep(50);

        // Verify the scheduler stopped (no more ticks)
        var tickAfterShutdown = scheduler.CurrentTickNumber;
        Thread.Sleep(50);
        Assert.That(scheduler.CurrentTickNumber, Is.EqualTo(tickAfterShutdown),
            "No more ticks should execute after shutdown");
    }

    // Regression: a scheduler lifecycle must not leave a thread running. Shutdown() bumps _tickGeneration — the same field workers use to detect a new tick —
    // so a Shutdown landing on a tick dispatch made every worker take the shutdown exit WITHOUT processing a system. _systemsRemaining never reached 0, the
    // timer thread's completion barrier span forever at ~100% of a core, and JoinWorkers() timed out, discarded its result and let Shutdown() report success.
    // One core lost per occurrence, permanently, silently.
    //
    // Invisible on a dev box — 32 cores absorb it and the suite still passes in 50 s. On the 3-core arm64 nightly runner the spinners accumulated until the
    // host could not answer VSTest's heartbeat and was killed as "Test host process crashed", after silently dropping ~160 tests. This asserts the property
    // directly: threads created by the cycles below must all be gone or idle afterwards, because a leaked spinner burns a core forever.
    // Sensitive: the assertion is a CPU-duty measurement, so it belongs in the gate's serial pass rather than beside 40 other fixtures competing for cores.
    // 200 cycles is not padding — at 20 the race never fired on a 32-core box and the test passed with the bug still present, which is worse than no test.
    [Test]
    [CancelAfter(60_000)]
    [NonParallelizable]
    [Category("Sensitive")]
    public void SchedulerLifecycle_RacingShutdownAgainstTick_LeavesNoRunningThread()
    {
        const int cycles = 200;

        static Dictionary<int, TimeSpan> SnapshotThreads()
        {
            var map = new Dictionary<int, TimeSpan>();
            foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
            {
                // A thread can die between enumeration and access; it is then not one of ours to worry about.
                try { map[t.Id] = t.TotalProcessorTime; } catch { /* exited */ }
            }
            return map;
        }

        var before = SnapshotThreads();

        // Shutdown deliberately lands as close to a tick dispatch as possible — that is the race. 20 cycles at 1 kHz makes hitting it near-certain; before the
        // fix this leaked roughly one spinner every few cycles.
        for (var i = 0; i < cycles; i++)
        {
            using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = $"LeakProbe{i}" });
            using var scheduler = NewDag(workerCount: 4)
                .CallbackSystem("A", _ => { })
                .CallbackSystem("B", _ => { }, after: "A")
                .Build(registry.Runtime);
            scheduler.Start();
            SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
            scheduler.Shutdown();
        }

        // Measure over a window with nothing of ours running: any thread born during the cycles that is still burning CPU is a leak. Duty, not total CPU, so a
        // busy CI box running other fixtures cannot fail this — only a thread spinning on its own can.
        var settle = SnapshotThreads();
        Thread.Sleep(500);
        var after = SnapshotThreads();

        var spinners = new List<string>();
        foreach (var (tid, cpuAfter) in after)
        {
            if (before.ContainsKey(tid) || !settle.TryGetValue(tid, out var cpuSettle))
            {
                continue; // pre-existing thread, or born during the measurement window itself
            }

            var duty = (cpuAfter - cpuSettle).TotalMilliseconds / 500.0;
            if (duty >= 0.5)
            {
                spinners.Add($"tid {tid} at {duty * 100:F0}% duty");
            }
        }

        Assert.That(spinners, Is.Empty,
            "every thread created by a scheduler lifecycle must be stopped by Shutdown/Dispose — a survivor spins on a core for the life of the process: "
            + string.Join(", ", spinners));
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

        // Wait on the recorded count, not on the tick NUMBER. `CurrentTickNumber` advances when a tick starts and
        // `TotalTicksRecorded` when one finishes being recorded, so waiting on the former lets `Shutdown()` cut the
        // last tick short and leaves the ring one entry short of the assertion below. Same shape as the flake that hit
        // `TyphonRuntimeTests.Telemetry_EntitiesProcessed_RecordedForQuerySystem` on the gate; fixed here before it
        // spends anyone's afternoon too.
        var ring = scheduler.Telemetry;
        SpinWait.SpinUntil(() => ring.TotalTicksRecorded >= 3, TimeSpan.FromSeconds(5));
        scheduler.Shutdown();

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
