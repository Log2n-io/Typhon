using NUnit.Framework;
using System;
using System.Linq;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Diagnostic tests that measure and report actual scheduler latencies.
/// These validate acceptance criteria: inter-system transition &lt; 1µs, tick jitter ±1ms at 60Hz.
/// </summary>
[TestFixture]
public class DagSchedulerLatencyTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "LatencyTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    /// <summary>
    /// Measures inter-system transition latency for inline CallbackSystem→CallbackSystem chains (D3).
    /// POC target: 0.1-0.4µs. This should be even faster since no SimulateWork.
    /// </summary>
    [Test]
    [Explicit("Benchmark — measures wall-clock latency histograms. Run on demand.")]
    public void Report_InlineCallbackTransitionLatency()
    {
        // Linear chain: A → B → C → D → E (all CallbackSystems)
        // B through E are inline continuations — transition latency should be near zero
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => Thread.SpinWait(10))
            .AddCallbackSystem("B", _ => Thread.SpinWait(10))
            .AddCallbackSystem("C", _ => Thread.SpinWait(10))
            .AddCallbackSystem("D", _ => Thread.SpinWait(10))
            .AddCallbackSystem("E", _ => Thread.SpinWait(10))
            .AddEdge("A", "B")
            .AddEdge("B", "C")
            .AddEdge("C", "D")
            .AddEdge("D", "E");

        var (systems, topo) = builder.Build();
        using var scheduler = new DagScheduler(systems, topo, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            TelemetryRingCapacity = 1024
        }, _registry.Runtime);

        scheduler.Start();

        // Warmup (20 ticks) + measure (200 ticks)
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 220, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var measuredTicks = Math.Min(200, ring.TotalTicksRecorded - 20);

        // Collect transition latencies for systems B, C, D, E across all measured ticks
        var latencies = new double[measuredTicks * 4]; // 4 non-root systems
        var idx = 0;

        for (long t = ring.NewestTick - measuredTicks + 1; t <= ring.NewestTick; t++)
        {
            var sysMetrics = ring.GetSystemMetrics(t);
            for (var s = 1; s <= 4; s++) // systems 1-4 (B through E)
            {
                latencies[idx++] = sysMetrics[s].TransitionLatencyUs;
            }
        }

        Array.Sort(latencies, 0, idx);

        var p50 = latencies[(int)(idx * 0.50)];
        var p90 = latencies[(int)(idx * 0.90)];
        var p99 = latencies[(int)(idx * 0.99)];
        var max = latencies[idx - 1];
        var mean = latencies.Take(idx).Average();

        TestContext.Out.WriteLine("═══ Inline CallbackSystem→CallbackSystem Transition Latency (D3) ═══");
        TestContext.Out.WriteLine($"  Samples: {idx} ({measuredTicks} ticks × 4 systems)");
        TestContext.Out.WriteLine($"  Mean:    {mean:F3} µs");
        TestContext.Out.WriteLine($"  P50:     {p50:F3} µs");
        TestContext.Out.WriteLine($"  P90:     {p90:F3} µs");
        TestContext.Out.WriteLine($"  P99:     {p99:F3} µs");
        TestContext.Out.WriteLine($"  Max:     {max:F3} µs");
        TestContext.Out.WriteLine($"  Target:  < 1.0 µs (POC: 0.1-0.4 µs)");
        TestContext.Out.WriteLine();

        Assert.That(p90, Is.LessThan(1.0),
            $"P90 inline transition latency must be < 1.0µs, was {p90:F3}µs");
    }

    /// <summary>
    /// Measures inter-system transition latency when a PipelineSystem successor must be discovered
    /// via FindReadySystem scan (not inlined). This is the slower path.
    /// </summary>
    [Test]
    [Ignore("Flaky — latency measurement sensitive to system load, passes in isolation")]
    public void Report_DiscoveryPipelineTransitionLatency()
    {
        // A(CallbackSystem) → B(PipelineSystem,50 chunks) → C(CallbackSystem)
        // B's transition latency = time from A completing to first B chunk grabbed
        var builder = new DagBuilder()
            .AddCallbackSystem("A", _ => Thread.SpinWait(100))
            .AddPipelineSystem("B", (chunk, total) => Thread.SpinWait(50), 50)
            .AddCallbackSystem("C", _ => Thread.SpinWait(10))
            .AddEdge("A", "B")
            .AddEdge("B", "C");

        var (systems, topo) = builder.Build();
        using var scheduler = new DagScheduler(systems, topo, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            TelemetryRingCapacity = 1024
        }, _registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 220, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var measuredTicks = Math.Min(200, ring.TotalTicksRecorded - 20);

        // System B (index 1) — PipelineSystem successor discovered via scan
        var latenciesB = new double[measuredTicks];
        // System C (index 2) — CallbackSystem successor of PipelineSystem (inline)
        var latenciesC = new double[measuredTicks];
        var idx = 0;

        for (long t = ring.NewestTick - measuredTicks + 1; t <= ring.NewestTick; t++)
        {
            var sysMetrics = ring.GetSystemMetrics(t);
            latenciesB[idx] = sysMetrics[1].TransitionLatencyUs;
            latenciesC[idx] = sysMetrics[2].TransitionLatencyUs;
            idx++;
        }

        Array.Sort(latenciesB, 0, idx);
        Array.Sort(latenciesC, 0, idx);

        TestContext.Out.WriteLine("═══ Discovery Path: CallbackSystem → PipelineSystem Transition Latency ═══");
        TestContext.Out.WriteLine($"  Samples:      {idx} ticks");
        TestContext.Out.WriteLine($"  B (PipelineSystem discovered via scan):");
        TestContext.Out.WriteLine($"    Mean:  {latenciesB.Take(idx).Average():F3} µs");
        TestContext.Out.WriteLine($"    P50:   {latenciesB[(int)(idx * 0.50)]:F3} µs");
        TestContext.Out.WriteLine($"    P90:   {latenciesB[(int)(idx * 0.90)]:F3} µs");
        TestContext.Out.WriteLine($"    P99:   {latenciesB[(int)(idx * 0.99)]:F3} µs");
        TestContext.Out.WriteLine($"    Max:   {latenciesB[idx - 1]:F3} µs");
        TestContext.Out.WriteLine($"  C (CallbackSystem inlined after PipelineSystem):");
        TestContext.Out.WriteLine($"    Mean:  {latenciesC.Take(idx).Average():F3} µs");
        TestContext.Out.WriteLine($"    P50:   {latenciesC[(int)(idx * 0.50)]:F3} µs");
        TestContext.Out.WriteLine($"    P90:   {latenciesC[(int)(idx * 0.90)]:F3} µs");
        TestContext.Out.WriteLine($"    P99:   {latenciesC[(int)(idx * 0.99)]:F3} µs");
        TestContext.Out.WriteLine($"    Max:   {latenciesC[idx - 1]:F3} µs");
        TestContext.Out.WriteLine($"  Target:  < 1.0 µs");
        TestContext.Out.WriteLine();

        Assert.That(latenciesB[(int)(idx * 0.90)], Is.LessThan(1.0),
            "P90 PipelineSystem discovery transition must be < 1.0µs");
    }

    /// <summary>
    /// Measures realistic game DAG transition latencies with mixed system types.
    /// </summary>
    [Test]
    [Explicit("Benchmark — measures wall-clock latency histograms. Run on demand.")]
    public void Report_RealisticGameDAGLatencies()
    {
        // Input(CB) → Movement(Pipeline,200) → Physics(Pipeline,200) → Combat(CB) → Output(CB)
        //           → AI(Pipeline,100) ────────────────────────────┘
        var builder = new DagBuilder()
            .AddCallbackSystem("Input", _ => Thread.SpinWait(100))
            .AddPipelineSystem("Movement", (c, t) => Thread.SpinWait(50), 200)
            .AddPipelineSystem("AI", (c, t) => Thread.SpinWait(80), 100)
            .AddPipelineSystem("Physics", (c, t) => Thread.SpinWait(40), 200)
            .AddCallbackSystem("Combat", _ => Thread.SpinWait(60))
            .AddCallbackSystem("Output", _ => Thread.SpinWait(10))
            .AddEdge("Input", "Movement")
            .AddEdge("Input", "AI")
            .AddEdge("Movement", "Physics")
            .AddEdge("Physics", "Combat")
            .AddEdge("AI", "Combat")
            .AddEdge("Combat", "Output");

        var (systems, topo) = builder.Build();
        using var scheduler = new DagScheduler(systems, topo, new RuntimeOptions
        {
            WorkerCount = Math.Max(4, Environment.ProcessorCount - 4),
            BaseTickRate = 1000,
            TelemetryRingCapacity = 1024
        }, _registry.Runtime);

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 220, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;
        var measuredTicks = Math.Min(200, ring.TotalTicksRecorded - 20);

        TestContext.Out.WriteLine("═══ Realistic Game DAG — Per-System Metrics ═══");
        TestContext.Out.WriteLine($"  Workers: {scheduler.WorkerCount}, Measured ticks: {measuredTicks}");
        TestContext.Out.WriteLine();

        var sysNames = new[] { "Input", "Movement", "AI", "Physics", "Combat", "Output" };

        for (var s = 0; s < 6; s++)
        {
            var transitions = new double[measuredTicks];
            var durations = new double[measuredTicks];
            var idx = 0;

            for (long t = ring.NewestTick - measuredTicks + 1; t <= ring.NewestTick; t++)
            {
                var sm = ring.GetSystemMetrics(t);
                transitions[idx] = sm[s].TransitionLatencyUs;
                durations[idx] = sm[s].DurationUs;
                idx++;
            }

            Array.Sort(transitions, 0, idx);
            Array.Sort(durations, 0, idx);

            TestContext.Out.WriteLine($"  {sysNames[s],-12}  transition: P50={transitions[(int)(idx * 0.50)]:F3}µs  P90={transitions[(int)(idx * 0.90)]:F3}µs  P99={transitions[(int)(idx * 0.99)]:F3}µs  |  duration: P50={durations[(int)(idx * 0.50)]:F3}µs  P90={durations[(int)(idx * 0.90)]:F3}µs");
        }

        // Report tick-level metrics
        var tickDurations = new double[measuredTicks];
        var tickIdx = 0;
        for (long t = ring.NewestTick - measuredTicks + 1; t <= ring.NewestTick; t++)
        {
            tickDurations[tickIdx++] = ring.GetTick(t).ActualDurationMs;
        }

        Array.Sort(tickDurations, 0, tickIdx);
        TestContext.Out.WriteLine();
        TestContext.Out.WriteLine($"  Tick duration:  P50={tickDurations[(int)(tickIdx * 0.50)]:F3}ms  P90={tickDurations[(int)(tickIdx * 0.90)]:F3}ms  P99={tickDurations[(int)(tickIdx * 0.99)]:F3}ms  Max={tickDurations[tickIdx - 1]:F3}ms");
    }

    /// <summary>
    /// Measures tick timing accuracy at 60Hz — validates ±1ms jitter acceptance criterion.
    /// </summary>
    [Test]
    [Explicit("Benchmark — runs ~5s of 60Hz ticks to measure jitter histogram. Run on demand.")]
    public void Report_TickTimingJitter_60Hz()
    {
        // Minimal DAG — just measure tick timing accuracy
        var builder = new DagBuilder()
            .AddCallbackSystem("Noop", _ => { });

        var (systems, topo) = builder.Build();
        using var scheduler = new DagScheduler(systems, topo, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 60,
            TelemetryRingCapacity = 1024
        }, _registry.Runtime);

        scheduler.Start();

        // Run for ~5 seconds at 60Hz = ~300 ticks
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= 320, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var ring = scheduler.Telemetry;

        // Use HRTSB timing error metrics (inherited)
        TestContext.Out.WriteLine("═══ Tick Timing Accuracy at 60Hz ═══");
        TestContext.Out.WriteLine($"  Ticks completed:   {scheduler.CurrentTickNumber}");
        TestContext.Out.WriteLine($"  Mean timing error: {scheduler.MeanTimingErrorUs:F1} µs");
        TestContext.Out.WriteLine($"  Max timing error:  {scheduler.MaxTimingErrorUs:F1} µs");
        TestContext.Out.WriteLine($"  Missed ticks:      {scheduler.MissedTicks}");
        TestContext.Out.WriteLine($"  Sleep calibration: {scheduler.CalibratedSleepResolution.TotalMilliseconds:F2} ms");
        TestContext.Out.WriteLine($"  Target jitter:     < 1000 µs (1ms)");
        TestContext.Out.WriteLine();

        // Measure jitter from ring buffer: |tick-to-tick interval - target period|
        var measuredTicks = Math.Min(200, ring.TotalTicksRecorded - 20);
        if (measuredTicks > 0)
        {
            var jitters = new double[measuredTicks];
            var intervals = new double[measuredTicks];
            var idx = 0;
            for (long t = ring.NewestTick - measuredTicks + 1; t <= ring.NewestTick; t++)
            {
                ref readonly var tick = ref ring.GetTick(t);
                if (tick.TickIntervalMs > 0) // skip first tick (no interval)
                {
                    intervals[idx] = tick.TickIntervalMs;
                    jitters[idx] = Math.Abs(tick.TickIntervalMs - tick.TargetDurationMs);
                    idx++;
                }
            }

            Array.Sort(jitters, 0, idx);
            Array.Sort(intervals, 0, idx);
            TestContext.Out.WriteLine($"  Tick interval (tick-to-tick):");
            TestContext.Out.WriteLine($"    P50:  {intervals[(int)(idx * 0.50)]:F3} ms  (target: {1000f / 60:F3} ms)");
            TestContext.Out.WriteLine($"    P90:  {intervals[(int)(idx * 0.90)]:F3} ms");
            TestContext.Out.WriteLine($"    P99:  {intervals[(int)(idx * 0.99)]:F3} ms");
            TestContext.Out.WriteLine($"  Tick jitter (|interval - target|):");
            TestContext.Out.WriteLine($"    P50:  {jitters[(int)(idx * 0.50)]:F3} ms");
            TestContext.Out.WriteLine($"    P90:  {jitters[(int)(idx * 0.90)]:F3} ms");
            TestContext.Out.WriteLine($"    P99:  {jitters[(int)(idx * 0.99)]:F3} ms");
            TestContext.Out.WriteLine($"    Max:  {jitters[idx - 1]:F3} ms");
        }

        Assert.That(scheduler.MeanTimingErrorUs, Is.LessThan(1000),
            "Mean timing error at 60Hz should be < 1ms");
    }
}
