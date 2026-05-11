using BenchmarkDotNet.Attributes;
using Typhon.Engine.Internals;
using Typhon.Engine.Profiler;
using Typhon.Profiler;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// TyphonEvent: on-path emit cost — proof-of-concept for instant-event
// generator migration. Measures the per-call cost of Emit* publishers
// when ProfilerActive=true so we can quantify regression / improvement
// when individual kinds get flipped from hand-written codec.Write* paths
// to generator-emitted `[TraceEvent(Shape=Instant)]` ref-struct paths.
//
// Acceptance bar: the migrated path must match the hand-written path
// within ±2 ns per call on x64.
//
// Run:  dotnet run -c Release --filter '*TyphonEventOnPath*'
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 5, iterationCount: 15)]
[MemoryDiagnoser]
[BenchmarkCategory("Profiler", "OnPath")]
public class TyphonEventOnPathBenchmarks
{
    [Params(1024)]
    public int LoopCount;

    private long _timestamp;

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            throw new InvalidOperationException(
                "On-path benchmark requires ProfilerActive=true. Check typhon.telemetry.json in the bench bin directory.");
        }
        // Pre-claim a thread slot so the inner loop doesn't pay first-claim cost.
        _ = ThreadSlotRegistry.GetOrAssignSlot();
        _timestamp = Stopwatch.GetTimestamp();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Drain the ring between iterations so we don't run out of space in the inner loop.
        var slot = ThreadSlotRegistry.GetOrAssignSlot();
        var ring = ThreadSlotRegistry.GetSlot(slot).Buffer;
        ring?.Reset();
    }

    [Benchmark(Baseline = true)]
    public long EmptyMethod()
    {
        long sum = 0;
        for (int i = 0; i < LoopCount; i++)
        {
            sum += EmptyInline(i);
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EmptyInline(int i) => i;

    /// <summary>Generator-emitted instant path: direct EmitX(args) static method, inline wire write.</summary>
    [Benchmark]
    public void EmitTickStart_OnPath()
    {
        var ts = _timestamp;
        for (int i = 0; i < LoopCount; i++)
        {
            TyphonEvent.EmitTickStart(ts);
        }
    }
}
