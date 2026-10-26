using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Concurrency;

/// <summary>
/// End-to-end stress test for the Phase 2 Concurrency tracing pipeline. Spawns N threads driving an
/// <see cref="AccessControl"/> instance under contention, attaches a <see cref="TraceRingObserver"/>,
/// and asserts that the observed event counts match the workload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Marked <see cref="ExplicitAttribute"/></b> because <see cref="TelemetryConfig"/> is a static class —
/// its <c>*Active</c> fields are baked at process load. Enabling Concurrency tracing requires either
/// (a) editing <c>test/Typhon.Engine.Tests/typhon.telemetry.json</c> to set
/// <c>Profiler:Concurrency:Enabled = true</c>, or (b) setting the env var
/// <c>TYPHON__PROFILER__CONCURRENCY__ENABLED=true</c> before running. The default test config
/// keeps Concurrency off so other profiler tests aren't affected.
/// </para>
/// <para>
/// <b>How to run:</b>
/// </para>
/// <code>
/// # Linux/macOS
/// TYPHON__PROFILER__CONCURRENCY__ENABLED=true \
///   dotnet test test/Typhon.Engine.Tests --filter "FullyQualifiedName~ConcurrencyTracingStressTests"
///
/// # Windows PowerShell
/// $env:TYPHON__PROFILER__CONCURRENCY__ENABLED = "true"
/// dotnet test test/Typhon.Engine.Tests --filter "FullyQualifiedName~ConcurrencyTracingStressTests"
/// </code>
/// </remarks>
[TestFixture]
// Needs TYPHON__PROFILER__CONCURRENCY__ENABLED=true in the PROCESS environment: TelemetryConfig reads its configuration
// in a static constructor, before the first test, so no fixture can flip it. This was [Explicit] + Manual — "the tier
// would have to fork a differently-configured process" — and therefore ran NOWHERE. The gate forks eight processes
// already; it now forks one more with this flag set (GATED_PASSES in bench/aws/shard.py), and the category keeps this
// fixture out of the parallel shards, where it would assert against a subsystem that is not recording.
[Category("TelemetryGated")]
[NonParallelizable]
public class ConcurrencyTracingStressTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = $"ConcStress-{Guid.NewGuid():N}" });
    }

    [TearDown]
    public void TearDown()
    {
        try { TyphonProfiler.Stop(); } catch { }
        TyphonProfiler.ResetForTests();
    }

    [Test]
    public void AccessControl_HighContention_EmitsExpectedEventCounts()
    {
        // Skip cleanly if the harness was run without Concurrency enabled.
        Assume.That(TelemetryConfig.ConcurrencyAccessControlSharedAcquireActive, Is.True,
            "ConcurrencyAccessControlSharedAcquireActive must be true. Enable via TYPHON__PROFILER__CONCURRENCY__ENABLED=true.");

        const int ThreadCount = 8;
        const int OpsPerThread = 1000;

        using var observer = new TraceRingObserver(_registry.Profiler);
        TyphonProfiler.AttachExporter(observer);
        var ringDropsBefore = TyphonProfiler.TotalDroppedEvents;
        TyphonProfiler.Start(_registry.Profiler, BuildMetadata());
        long ringDrops;

        try
        {
            var control = default(AccessControl);
            var threads = new Thread[ThreadCount];
            for (var i = 0; i < ThreadCount; i++)
            {
                var threadIdx = i;
                threads[i] = new Thread(() =>
                {
                    for (var op = 0; op < OpsPerThread; op++)
                    {
                        // Mix of shared and exclusive to exercise both paths and create contention.
                        if ((op + threadIdx) % 4 == 0)
                        {
                            control.EnterExclusiveAccess(ref WaitContext.Null);
                            control.ExitExclusiveAccess();
                        }
                        else
                        {
                            control.EnterSharedAccess(ref WaitContext.Null);
                            control.ExitSharedAccess();
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = $"ConcStress-{i}",
                };
                threads[i].Start();
            }

            foreach (var t in threads)
            {
                Assert.That(t.Join(TimeSpan.FromSeconds(30)), Is.True, "Worker thread should finish in 30s");
            }

            // Read before Stop: Stop collapses spillover chains, and a spillover's drop counter goes back to the pool with it.
            ringDrops = Math.Max(0, TyphonProfiler.TotalDroppedEvents - ringDropsBefore);
        }
        finally
        {
            // Stop drains every ring to empty and joins the exporter thread, so everything the workers emitted has reached the observer or been
            // counted as lost by the time it returns — no settling sleep needed.
            TyphonProfiler.Stop();
        }

        var totalAcquires = observer.CountOf(TraceEventKind.ConcurrencyAccessControlSharedAcquire)
                          + observer.CountOf(TraceEventKind.ConcurrencyAccessControlExclusiveAcquire);
        var totalReleases = observer.CountOf(TraceEventKind.ConcurrencyAccessControlSharedRelease)
                          + observer.CountOf(TraceEventKind.ConcurrencyAccessControlExclusiveRelease);

        // The pipeline may lose records under load, but only in two places, and both count what they lose: a full producer ring (ringDrops) and a
        // full exporter queue (records fanned out that this, the only exporter, never processed). Every operation's event must therefore be observed
        // or accounted for. A fixed tolerance (this test used to accept 90 %) cannot tell a counted drop from a silent one: the slot-reclaim race
        // that SlotState.Claiming closes lost a reclaiming thread's records with no counter moving, and read here as a count just under the bar.
        // Counts run above the workload: other AccessControl instances in the process emit the same kinds.
        var exporterLost = TyphonProfiler.TotalRecordsFannedOut - observer.RecordsProcessed;
        var accountedLoss = ringDrops + exporterLost;
        var expected = ThreadCount * OpsPerThread;
        var detail = $"ringDrops={ringDrops} exporterLost={exporterLost} queueDrops={TyphonProfiler.TotalDroppedExporterBatches} " +
                     $"fannedOut={TyphonProfiler.TotalRecordsFannedOut} processed={observer.RecordsProcessed}";
        Assert.Multiple(() =>
        {
            Assert.That(totalAcquires + accountedLoss, Is.GreaterThanOrEqualTo(expected),
                $"{expected - totalAcquires} acquire events missing, {accountedLoss} accounted for ({detail}).");
            Assert.That(totalReleases + accountedLoss, Is.GreaterThanOrEqualTo(expected),
                $"{expected - totalReleases} release events missing, {accountedLoss} accounted for ({detail}).");
        });
    }

    [Test]
    public void EpochGuard_NestedScopes_EmitsCorrectEnterExitCounts()
    {
        Assume.That(TelemetryConfig.ConcurrencyEpochScopeEnterActive, Is.True,
            "ConcurrencyEpochScopeEnterActive must be true.");

        using var observer = new TraceRingObserver(_registry.Profiler);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(_registry.Profiler, BuildMetadata());

        try
        {
            using var manager = new EpochManager("stress-epoch", _registry.Synchronization);

            const int Iterations = 500;
            for (var i = 0; i < Iterations; i++)
            {
                using var g1 = EpochGuard.Enter(manager);
                using var g2 = EpochGuard.Enter(manager);
                // implicit dispose → ScopeExit ×2
            }

            Thread.Sleep(200);
        }
        finally
        {
            TyphonProfiler.Stop();
        }

        var enterCount = observer.CountOf(TraceEventKind.ConcurrencyEpochScopeEnter);
        var exitCount = observer.CountOf(TraceEventKind.ConcurrencyEpochScopeExit);

        // 2 enters and 2 exits per iteration, 90 % drop tolerance.
        const long Expected = 500 * 2;
        Assert.Multiple(() =>
        {
            Assert.That(enterCount, Is.GreaterThanOrEqualTo((long)(Expected * 0.90)),
                $"Expected ~{Expected} ScopeEnter events, got {enterCount}.");
            Assert.That(exitCount, Is.GreaterThanOrEqualTo((long)(Expected * 0.90)),
                $"Expected ~{Expected} ScopeExit events, got {exitCount}.");
        });
    }

    private static ProfilerSessionMetadata BuildMetadata()
        => new ProfilerSessionMetadata(
            systems: Array.Empty<SystemDefinitionRecord>(),
            archetypes: Array.Empty<ArchetypeRecord>(),
            componentTypes: Array.Empty<ComponentTypeRecord>(),
            workerCount: 0,
            baseTickRate: 60.0f,
            startTimestamp: Stopwatch.GetTimestamp(),
            stopwatchFrequency: Stopwatch.Frequency,
            startedUtc: DateTime.UtcNow,
            samplingSessionStartQpc: 0);
}
