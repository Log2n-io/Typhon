using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Measures CPU consumption during idle periods. Uses OS-level process CPU time
/// to capture real waste — no instrumentation gating required.
/// </summary>
[TestFixture]
public class DagSchedulerCpuTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "CpuTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry?.Dispose();
    }

    /// <summary>
    /// Measures CPU waste when workers have nothing to do within a tick.
    /// Narrow DAG: Input(CallbackSystem) → HeavyWork(PipelineSystem, 4 chunks, ~1ms each) → Output(CallbackSystem).
    /// With N workers, N-4 workers spin idle during the Pipeline phase.
    /// </summary>
    [Test]
    [Explicit("Benchmark — measures wall-clock CPU over multi-second windows. Run on demand.")]
    public void Report_CpuWaste_NarrowDag_WithinTick()
    {
        const int workerCount = 8;
        const int chunks = 4;
        const int tickRate = 200; // Fast tick rate to accumulate more within-tick time
        const int targetTicks = 200;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = tickRate })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("Input", _ => { })
            .PipelineSystem("Heavy", (chunk, total) =>
            {
                // ~500µs per chunk busy-spin
                var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000;
                while (Stopwatch.GetTimestamp() < end) { }
            }, chunks, after: "Input")
            .CallbackSystem("Output", _ => { }, after: "Heavy")
            .Build(_registry.Runtime);

        // Measure CPU time
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= targetTicks, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var wallAfter = Stopwatch.GetTimestamp();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        var wallMs = (wallAfter - wallBefore) * 1000.0 / Stopwatch.Frequency;
        var cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        // Max possible CPU = wall time × (workers + 1 timer thread)
        var maxCpuMs = wallMs * (workerCount + 1);
        var cpuUtilization = cpuMs / maxCpuMs * 100.0;

        // Theoretical useful work: chunks × 500µs × ticks = 4 × 0.5ms × 200 = 400ms
        var usefulWorkMs = chunks * 0.5 * scheduler.CurrentTickNumber;
        var wasteMs = cpuMs - usefulWorkMs;
        var wastePercent = wasteMs / cpuMs * 100.0;

        // Get tick duration from telemetry
        var ring = scheduler.Telemetry;
        var lastTick = ring.GetTick(ring.NewestTick);

        TestContext.Out.WriteLine("═══ CPU Waste: Narrow DAG (4 chunks, 8 workers) ═══");
        TestContext.Out.WriteLine($"  Workers:         {workerCount} + 1 timer thread");
        TestContext.Out.WriteLine($"  Ticks completed: {scheduler.CurrentTickNumber}");
        TestContext.Out.WriteLine($"  Wall time:       {wallMs:F1} ms");
        TestContext.Out.WriteLine($"  CPU time (all):  {cpuMs:F1} ms");
        TestContext.Out.WriteLine($"  Max possible:    {maxCpuMs:F1} ms ({workerCount + 1} cores × wall)");
        TestContext.Out.WriteLine($"  CPU utilization: {cpuUtilization:F1}% of {workerCount + 1} cores");
        TestContext.Out.WriteLine($"  Useful work est: {usefulWorkMs:F1} ms");
        TestContext.Out.WriteLine($"  Waste estimate:  {wasteMs:F1} ms ({wastePercent:F1}% of CPU time)");
        TestContext.Out.WriteLine($"  Tick duration:   {lastTick.ActualDurationMs:F3} ms");
        TestContext.Out.WriteLine();
    }

    /// <summary>
    /// Measures CPU consumption between ticks (three-phase wait should be efficient).
    /// Uses a noop DAG at 60Hz — workers should mostly Sleep, burning near-zero CPU.
    /// </summary>
    [Test]
    [Explicit("Benchmark — measures wall-clock CPU over multi-second windows. Run on demand.")]
    public void Report_CpuConsumption_BetweenTicks_60Hz()
    {
        const int workerCount = 8;
        const int tickRate = 60;
        const int durationSeconds = 3;

        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = tickRate })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("Noop", _ => { })
            .Build(_registry.Runtime);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        scheduler.Start();
        Thread.Sleep(durationSeconds * 1000);
        scheduler.Shutdown();

        var wallAfter = Stopwatch.GetTimestamp();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        var wallMs = (wallAfter - wallBefore) * 1000.0 / Stopwatch.Frequency;
        var cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        var maxCpuMs = wallMs * (workerCount + 1);
        var cpuUtilization = cpuMs / maxCpuMs * 100.0;

        // Ideal: near zero CPU (noop callback, sleeping between ticks)
        // Per-core utilization: how much CPU time per core
        var perCoreCpuPercent = cpuMs / wallMs * 100.0 / (workerCount + 1);

        TestContext.Out.WriteLine("═══ CPU Consumption: Between-Tick (60Hz Noop) ═══");
        TestContext.Out.WriteLine($"  Workers:          {workerCount} + 1 timer thread");
        TestContext.Out.WriteLine($"  Ticks completed:  {scheduler.CurrentTickNumber}");
        TestContext.Out.WriteLine($"  Wall time:        {wallMs:F1} ms");
        TestContext.Out.WriteLine($"  CPU time (all):   {cpuMs:F1} ms");
        TestContext.Out.WriteLine($"  Max possible:     {maxCpuMs:F1} ms");
        TestContext.Out.WriteLine($"  CPU utilization:  {cpuUtilization:F1}% of {workerCount + 1} cores");
        TestContext.Out.WriteLine($"  Per-core average: {perCoreCpuPercent:F2}%");
        TestContext.Out.WriteLine($"  Interpretation:   <1% = sleep working well, >5% = spinning too much");
        TestContext.Out.WriteLine();
    }

    /// <summary>
    /// Baseline: wide DAG with good parallelism. Shows expected CPU usage when workers
    /// are actually busy. Used to compare against narrow DAG waste.
    /// </summary>
    [Test]
    [Explicit("Benchmark — measures wall-clock CPU over multi-second windows. Run on demand.")]
    public void Report_CpuBaseline_WideDag()
    {
        const int workerCount = 8;
        const int chunks = 50;
        const int tickRate = 200;
        const int targetTicks = 200;

        // Wide DAG: Input → 4 parallel Pipeline systems (50 chunks each) → Output
        using var scheduler = RuntimeSchedule.Create(new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = tickRate })
            .PublicTrack.DeclareDag("Test")
            .CallbackSystem("Input", _ => { })
            .PipelineSystem("Physics", (c, t) =>
            {
                var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5000; // ~200µs
                while (Stopwatch.GetTimestamp() < end) { }
            }, chunks, after: "Input")
            .PipelineSystem("AI", (c, t) =>
            {
                var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5000;
                while (Stopwatch.GetTimestamp() < end) { }
            }, chunks, after: "Input")
            .PipelineSystem("Movement", (c, t) =>
            {
                var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5000;
                while (Stopwatch.GetTimestamp() < end) { }
            }, chunks, after: "Input")
            .PipelineSystem("Animation", (c, t) =>
            {
                var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5000;
                while (Stopwatch.GetTimestamp() < end) { }
            }, chunks, after: "Input")
            .CallbackSystem("Output", _ => { }, afterAll: ["Physics", "AI", "Movement", "Animation"])
            .Build(_registry.Runtime);

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = Stopwatch.GetTimestamp();

        scheduler.Start();
        SpinWait.SpinUntil(() => scheduler.CurrentTickNumber >= targetTicks, TimeSpan.FromSeconds(10));
        scheduler.Shutdown();

        var wallAfter = Stopwatch.GetTimestamp();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        var wallMs = (wallAfter - wallBefore) * 1000.0 / Stopwatch.Frequency;
        var cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        var maxCpuMs = wallMs * (workerCount + 1);
        var cpuUtilization = cpuMs / maxCpuMs * 100.0;

        // Useful work: 4 systems × 50 chunks × 200µs × 200 ticks = 8000ms
        var usefulWorkMs = 4 * chunks * 0.2 * scheduler.CurrentTickNumber;
        var wastePercent = (cpuMs - usefulWorkMs) / cpuMs * 100.0;

        TestContext.Out.WriteLine("═══ CPU Baseline: Wide DAG (4×50 chunks, 8 workers) ═══");
        TestContext.Out.WriteLine($"  Workers:         {workerCount} + 1 timer thread");
        TestContext.Out.WriteLine($"  Ticks completed: {scheduler.CurrentTickNumber}");
        TestContext.Out.WriteLine($"  Wall time:       {wallMs:F1} ms");
        TestContext.Out.WriteLine($"  CPU time (all):  {cpuMs:F1} ms");
        TestContext.Out.WriteLine($"  CPU utilization: {cpuUtilization:F1}% of {workerCount + 1} cores");
        TestContext.Out.WriteLine($"  Useful work est: {usefulWorkMs:F1} ms");
        TestContext.Out.WriteLine($"  Waste estimate:  {wastePercent:F1}%");
        TestContext.Out.WriteLine();
    }
}
