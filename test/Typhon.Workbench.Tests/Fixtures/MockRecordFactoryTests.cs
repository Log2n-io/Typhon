using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Validates <see cref="MockRecordFactory"/> against the real <see cref="IncrementalCacheBuilder"/> — the consumer the
/// on-demand-capture filter (#805) sits in front of.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately <b>fact-finding</b> tests, not just fixture smoke tests. The #805 design asserts two claims
/// about builder behaviour that the whole filter design rests on, and neither is obvious from reading a record layout:
/// </para>
/// <list type="number">
///   <item><b>The 241 nudge is real.</b> A tick finalized <i>with</i> <c>SchedulerMetronomeWait</c> reports a longer
///   <c>DurationUs</c> than the same tick finalized without it, because the builder lets 241's timestamp push
///   <c>_currentTickLastTs</c> past <c>TickEnd</c> before sealing. If that were false, exempting 241 would be
///   cargo-cult and the design's duration-parity argument would be wrong.</item>
///   <item><b><c>PerTickSnapshot</c> carries an absolute tick number</b> that survives the wire, which is the only
///   source of true simulation tick numbers available to a live attach session.</item>
/// </list>
/// <para>
/// Proving them here, against the production builder, is what lets the filter tests downstream assert on behaviour
/// instead of on assumptions.
/// </para>
/// </remarks>
[TestFixture]
public sealed class MockRecordFactoryTests
{
    private const long TicksPerUs = 10;              // matches the mock Init header's 10 MHz TimestampFrequency
    private static readonly ProfilerHeader Header = new() { Version = TraceFileHeader.CurrentVersion, TimestampFrequency = 10_000_000 };

    private static IncrementalCacheBuilder NewBuilder(out LiveCacheTempFile temp)
    {
        temp = LiveCacheTempFile.Create(Guid.NewGuid());
        Span<byte> fingerprint = stackalloc byte[32];
        return new IncrementalCacheBuilder(temp.Sink, ownsSink: false, Header, fingerprint,
            new Dictionary<int, string>(), new Dictionary<ushort, string>());
    }

    /// <summary>Every factory record must survive the builder's walk — a bad size prefix makes it bail silently mid-buffer.</summary>
    [Test]
    public void EveryFactoryRecord_ParsesThroughTheBuilderWalk()
    {
        var records = MockRecordFactory.Concat(
            MockRecordFactory.TickStart(1000),
            MockRecordFactory.ThreadInfo(1000, threadSlot: 0, managedThreadId: 7, name: "Worker0"),
            MockRecordFactory.SchedulerChunk(1010, durationTicks: 30, systemIndex: 3),
            MockRecordFactory.GenericSpan(TraceEventKind.RuntimeTransactionLifecycle, 1020, durationTicks: 10),
            MockRecordFactory.TickEnd(1100),
            MockRecordFactory.PerTickSnapshot(1105, engineTickNumber: 41),
            MockRecordFactory.OverloadDetector(1106, tick: 41, consecutiveOverrun: 2, consecutiveUnderrun: 3),
            MockRecordFactory.MetronomeWait(1200, durationTicks: 500));

        // CountRecords mirrors the builder's own bail-out rules, so agreement with the literal count proves every
        // size prefix is self-consistent and no record silently truncates the walk.
        Assert.That(MockRecordFactory.CountRecords(records), Is.EqualTo(8), "all 8 records must be walkable");

        var kinds = MockRecordFactory.KindsIn(records);
        Assert.That(kinds, Is.EqualTo(new[]
        {
            TraceEventKind.TickStart,
            TraceEventKind.ThreadInfo,
            TraceEventKind.SchedulerChunk,
            TraceEventKind.RuntimeTransactionLifecycle,
            TraceEventKind.TickEnd,
            TraceEventKind.PerTickSnapshot,
            TraceEventKind.SchedulerOverloadDetector,
            TraceEventKind.SchedulerMetronomeWait,
        }), "kinds must round-trip in wire order");
    }

    /// <summary>
    /// FACT #1 — the 241 nudge. Two identical ticks, one with a trailing <c>SchedulerMetronomeWait</c> and one without.
    /// The builder must report a LONGER duration for the one that has it. This is the trap the design's exempt set exists
    /// to avoid: filter 241 while idle and every idle bar shrinks relative to every armed bar.
    /// </summary>
    [Test]
    public void MetronomeWait_ExtendsTickDuration_BeyondTickEnd()
    {
        const long tickStartTs = 1_000;
        const long tickEndTs = 1_500;      // 500 ticks = 50 us
        const long metronomeTs = 1_900;    // 900 ticks = 90 us from tick start

        var withoutWait = FinalizeOneTick(
            MockRecordFactory.TickStart(tickStartTs),
            MockRecordFactory.TickEnd(tickEndTs));

        var withWait = FinalizeOneTick(
            MockRecordFactory.TickStart(tickStartTs),
            MockRecordFactory.TickEnd(tickEndTs),
            MockRecordFactory.MetronomeWait(metronomeTs, durationTicks: 200));

        Assert.Multiple(() =>
        {
            Assert.That(withoutWait.DurationUs, Is.EqualTo((tickEndTs - tickStartTs) / (double)TicksPerUs).Within(0.01),
                "without 241 the tick ends at TickEnd");
            Assert.That(withWait.DurationUs, Is.EqualTo((metronomeTs - tickStartTs) / (double)TicksPerUs).Within(0.01),
                "with 241 the tick extends to the metronome wait start");
        });

        Assert.That(withWait.DurationUs, Is.GreaterThan(withoutWait.DurationUs),
            "241 must extend the tick — if this ever fails, the design's duration-parity argument for exempting 241 is void");
    }

    /// <summary>
    /// FACT #2 — <c>PerTickSnapshot</c> carries an absolute tick number that survives the wire. This is the sole source
    /// of true simulation tick numbers for a live attach session (the common header has none, and TickStart has no
    /// payload at all), so #805's numbering offset depends on it being readable at wire offset 12.
    /// </summary>
    [Test]
    public void PerTickSnapshot_CarriesAbsoluteTickNumber_AtWireOffset12()
    {
        const uint engineTick = 216_000;
        var record = MockRecordFactory.PerTickSnapshot(timestamp: 5_000, engineTickNumber: engineTick);

        var decoded = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
        Assert.That(decoded, Is.EqualTo(engineTick), "the absolute tick number must sit at offset 12 of the record");

        // And it must survive a real round-trip through the production codec, not just our own write.
        var data = PerTickSnapshotEventCodec.DecodePerTickSnapshot(record);
        Assert.That(data.TickNumber, Is.EqualTo(engineTick), "the production decoder must read back the same value");
    }

    /// <summary>Detail records must actually register as events, otherwise "EventCount == 0 when idle" proves nothing.</summary>
    [Test]
    public void DetailRecords_IncrementTickEventCount()
    {
        var bare = FinalizeOneTick(
            MockRecordFactory.TickStart(1_000),
            MockRecordFactory.TickEnd(1_500));

        var withDetail = FinalizeOneTick(
            MockRecordFactory.TickStart(1_000),
            MockRecordFactory.SchedulerChunk(1_100, durationTicks: 50, systemIndex: 2),
            MockRecordFactory.SchedulerChunk(1_200, durationTicks: 50, systemIndex: 3),
            MockRecordFactory.TickEnd(1_500));

        Assert.That(withDetail.EventCount, Is.GreaterThan(bare.EventCount),
            "SchedulerChunk records must count as events so an idle tick's EventCount is meaningfully zero-ish");
        Assert.That(withDetail.ActiveSystemsBitmask, Is.Not.EqualTo(0UL),
            "SchedulerChunk must register its system index — proves the span payload offset is correct");
    }

    /// <summary>
    /// Feeds one tick's records plus a following <c>TickStart</c> (the builder finalizes tick N only when it sees
    /// TickStart of N+1) and returns the resulting summary.
    /// </summary>
    private static TickSummary FinalizeOneTick(params byte[][] tickRecords)
    {
        LiveCacheTempFile temp = null;
        try
        {
            var builder = NewBuilder(out temp);
            try
            {
                builder.FeedRawRecords(MockRecordFactory.Concat(tickRecords));
                builder.FeedRawRecords(MockRecordFactory.TickStart(9_000_000));
                Assert.That(builder.TickSummaries, Is.Not.Empty, "a tick must have been finalized");
                return builder.TickSummaries.Last();
            }
            finally
            {
                builder.Dispose();
            }
        }
        finally
        {
            temp?.Dispose();
        }
    }
}
