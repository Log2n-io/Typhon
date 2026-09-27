using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;
using Typhon.Profiler.Events;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// #WB-02 — the push-replication operator records (kinds 68 and 69) reach the trace, including on a server with no session connected.
/// </summary>
/// <remarks>
/// <para>
/// <b>What only this fixture covers.</b> The wire LAYOUT is pinned without any flag by <c>TypedDtoRoundTripTests</c>, and the panel's arithmetic by the SPA's
/// <c>subscriptionReadings</c> tests. Neither of those can fail if the engine never emits, which is the gap this closes: a panel fed by an emission nobody
/// exercised displays zeros and reads as a working feature.
/// </para>
/// <para>
/// <b><c>[Category("TelemetryGated")]</c></b> because the <c>Subscriptions</c> telemetry subtree is a subtree root and defaults off, and
/// <c>TelemetryConfig</c> reads its configuration in a static constructor — before the first test — so no fixture can flip it. The merge gate runs this in a
/// dedicated process with the flag set (<c>GATED_PASSES</c> in <c>bench/aws/shard.py</c>) and the category keeps it out of the parallel shards, where it
/// would fail bare. Locally:
/// <code>
/// $env:TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED = 'true'; dotnet test --filter "Category=TelemetryGated"
/// </code>
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SubscriptionsOperatorTelemetryTests : TestBase<SubscriptionsOperatorTelemetryTests>
{
    private const int TickRateHz = 100;

    /// <summary>
    /// Detaches the exporter, because <c>TyphonProfiler</c>'s exporter list is static and <c>Stop</c> does not clear it — a later <c>Start</c> would otherwise
    /// spawn a consume thread over this fixture's disposed observer and kill the test host from a background thread. See <c>RealmPolicyTests</c>.
    /// </summary>
    [TearDown]
    public void DetachProfilerExporters()
    {
        try { TyphonProfiler.Stop(); } catch { /* the ungated case never starts one */ }
        TyphonProfiler.ResetForTests();
    }

    private static ProfilerSessionMetadata TraceMetadata() => new(
        systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: TickRateHz,
        startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
        startedUtc: DateTime.UtcNow);

    /// <summary>
    /// The server record arrives on a runtime that has no session and declared no metric — the two ways the emission could have been silenced.
    /// </summary>
    [Test]
    [Category("TelemetryGated")]
    public void TheServerRecordReachesTheTrace_WithNoSessionAndNoMetricDeclared()
    {
        Assume.That(TelemetryConfig.SubscriptionsServerTelemetryActive, Is.True,
            "TYPHON__PROFILER__SUBSCRIPTIONS__ENABLED must be set for this process — see the fixture's remarks");

        using var observer = new TraceRingObserver(ResourceRegistry.Profiler, captureRawBytes: true);
        TyphonProfiler.AttachExporter(observer);
        TyphonProfiler.Start(ResourceRegistry.Profiler, TraceMetadata());
        long seen;
        try
        {
            using var world = new World(ProjectionTestSchema.SetupEngine(ServiceProvider));
            seen = observer.WaitFor(TraceEventKind.SubscriptionsServerTelemetry, 3, TimeSpan.FromSeconds(5));
        }
        finally
        {
            TyphonProfiler.Stop();
        }

        Assert.That(seen, Is.GreaterThanOrEqualTo(3),
            $"records seen: {observer.RecordsProcessed}, kinds: {KindsSeen(observer)}");

        // Matched rather than asserted over every row: the profiler is a process-global singleton and the ring it drains is per THREAD, so a record another
        // fixture's runtime left queued is delivered to this exporter too. What this case claims is that a server with no session produces a record at all —
        // a neighbour's rows can only add to the set, never remove the one being looked for.
        var rows = Decoded(observer);
        Assert.That(rows.Any(r => r.Sessions == 0 && r.ReportedSessions == 0 && r.NetOutBytesPerSec == 0), Is.True,
            $"no record reports an idle server; rows: {string.Join(" ", rows.Select(r => $"({r.Sessions},{r.ReportedSessions},{r.NetOutBytesPerSec})"))}");
    }

    /// <summary>
    /// The very first emission happens before any tick has been recorded, and asking the tick-telemetry ring for that window must read zero, not throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this pins, and why it was invisible.</b> The ring is written at the END of a tick and reports both of its bounds as -1 while empty, so
    /// <c>Math.Max(oldest, tick - window)</c> was -1 on tick 0 and <c>GetTick(-1)</c> threw. The operator emission runs from
    /// <c>SubscriptionsContext.Reset</c>, which runs BEFORE that write — so the throw prevented the recording that would have made the next tick's call
    /// legal, and the condition sustained itself for every tick of the run. The symptom was not an exception anywhere visible: it was 508 ticks, 2 337 trace
    /// records, and not one record of kind 68, with the gate true and every null check passing.
    /// </para>
    /// <para>
    /// <b>Asserted on the encoder rather than through the emission, and ungated for that reason.</b> Reaching the throw through
    /// <c>EmitOperatorTelemetry</c> needs the flag on, because the arguments are only evaluated once the call is reached — so a version of this case written
    /// that way would pass vacuously in the ordinary suite, which is where a regression would land. An empty ring is attached explicitly instead of waiting
    /// for one, because a started runtime records its first tick within milliseconds and a case that raced it would pass for the wrong reason.
    /// </para>
    /// <para>
    /// The same three window passes serve the client-facing <c>STATS</c> block, whose first emission has the same empty ring under it, so the guard is not
    /// WB-02's alone.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("TR-01")]
    public void EveryTickTelemetryWindowPassSurvivesAnEmptyRing()
    {
        using var world = new World(ProjectionTestSchema.SetupEngine(ServiceProvider));
        var stats = world.Subscriptions.Stats;
        stats.AttachTelemetry(new TickTelemetryRing(capacity: 64, systemCount: 0));

        Assert.Multiple(() =>
        {
            Assert.That(() => stats.DurabilityWaitP99Ms(0, 1), Throws.Nothing, "the pass that threw");
            Assert.That(stats.DurabilityWaitP99Ms(0, 1), Is.Zero, "a window with no tick in it is zero, and zero is the truthful reading");
            Assert.That(() => stats.TrackP99Ms(0, 1), Throws.Nothing, "the track's own ring, which already clamped");
        });
    }

    private static string KindsSeen(TraceRingObserver observer) =>
        string.Join(", ", observer.GetRecords().Select(r => r.Kind).Distinct().OrderBy(k => (byte)k));

    private static List<SubscriptionsServerTelemetryEventDto> Decoded(TraceRingObserver observer)
    {
        var rows = new List<SubscriptionsServerTelemetryEventDto>();
        foreach (var (kind, bytes) in observer.GetRecords())
        {
            if (kind == TraceEventKind.SubscriptionsServerTelemetry)
            {
                rows.Add(SubscriptionsServerTelemetryEventDto.Decode(bytes, 0, 1));
            }
        }
        return rows;
    }

    /// <summary>A live runtime with replication declared, no metric catalog, no transport and therefore no session.</summary>
    private sealed class World : IDisposable
    {
        private readonly DatabaseEngine _engine;
        private readonly TyphonRuntime _runtime;

        public World(DatabaseEngine engine)
        {
            _engine = engine;
            _runtime = TyphonRuntime.Create(engine, _ => { }, new RuntimeOptions
            {
                WorkerCount = 1,
                BaseTickRate = TickRateHz,
                Subscriptions = new SubscriptionsOptions { ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0) },
            });

            _runtime.Subscriptions.Sessions.Kinds("god");
            ProjectionTestSchema.DeclareCreature(_runtime.Subscriptions);
            _runtime.Subscriptions.Profile("god-world", p => p.World().Of<ProjCreature>());
            _runtime.Start();

            Subscriptions = _runtime.SubscriptionsContextForTest.Subscriptions;

            // Every tick, so the case costs milliseconds rather than the production second. It changes WHEN a record is emitted and nothing in it.
            Subscriptions.Stats.EmissionPeriodTicksForTest = 1;
        }

        public SubscriptionsRuntime Subscriptions { get; }

        public void Dispose()
        {
            _runtime.Dispose();
            _engine.Dispose();
        }
    }
}
