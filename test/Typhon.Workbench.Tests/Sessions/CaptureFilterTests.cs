using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// On-demand tick capture (#805) — the record filter in <c>AttachSessionRuntime.HandleBlock</c>.
/// Covers AC-1 (exempt set), AC-2 (summary fidelity while idle) and AC-3 (duration parity).
/// </summary>
[TestFixture]
public sealed class CaptureFilterTests
{
    private static CancellationToken Timeout10s => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>
    /// AC-1 — while idle, detail records are dropped and the exempt per-tick skeleton is retained. Asserted through the
    /// byte counters the filter maintains: retained must be strictly less than received, and the surviving ticks must
    /// still be finalized (which can only happen if the spine got through).
    /// </summary>
    [Test]
    public async Task Idle_DropsDetailRecords_ButKeepsTheTickSpine()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        for (var i = 0; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 20, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(3);

        var state = h.Runtime.CaptureState;
        Assert.Multiple(() =>
        {
            Assert.That(state.State, Is.EqualTo("Idle"), "cherry-pick mode starts idle");
            Assert.That(state.BytesReceived, Is.GreaterThan(0), "the engine's bytes must have been counted");
            Assert.That(state.BytesRetained, Is.LessThan(state.BytesReceived), "detail records must have been dropped");
            Assert.That(state.RecordedTicks, Is.Zero, "no window was armed, so nothing was recorded");
        });

        // The spine survived: ticks were finalized at all, and each carries ONLY the exempt skeleton.
        // Note EventCount does not go to zero — the builder counts every record it is fed, and the five exempt records
        // per tick genuinely are in the chunk. What must be gone is the 20 detail records.
        Assert.That(h.Summaries, Is.Not.Empty, "ticks must still be finalized while idle — the spine is exempt");
        Assert.That(h.Summaries.All(s => s.EventCount == CaptureHarness.ExemptRecordsPerTick), Is.True,
            $"idle ticks must carry exactly the {CaptureHarness.ExemptRecordsPerTick} exempt records and none of the 20 detail records; "
            + $"got [{string.Join(", ", h.Summaries.Select(s => s.EventCount))}]");
        Assert.That(h.Summaries.All(s => s.ActiveSystemsBitmask == "0"), Is.True,
            "no SchedulerChunk may have reached the builder while idle");
    }

    /// <summary>
    /// AC-1 (direct) — the exempt set is exactly the six kinds the design names. Asserted against the filter's own
    /// predicate rather than through the pipeline, so a kind added or removed by accident fails loudly here.
    /// </summary>
    [Test]
    public void ExemptSet_IsExactlyTheSixPerTickKinds()
    {
        var expected = new[]
        {
            TraceEventKind.TickStart,
            TraceEventKind.TickEnd,
            TraceEventKind.PerTickSnapshot,
            TraceEventKind.ThreadInfo,
            TraceEventKind.SchedulerMetronomeWait,
            TraceEventKind.SchedulerOverloadDetector,
        };

        foreach (var kind in expected)
        {
            Assert.That(AttachSessionRuntime.IsExemptKind(kind), Is.True, $"{kind} must be exempt");
        }

        // QueueTickEnd is the deliberate exclusion: one record per (tick x active queue), scales with queue count,
        // and is not a TickSummary input.
        Assert.That(AttachSessionRuntime.IsExemptKind(TraceEventKind.QueueTickEnd), Is.False,
            "QueueTickEnd must NOT be exempt — it scales with queue count");
        Assert.That(AttachSessionRuntime.IsExemptKind(TraceEventKind.SchedulerChunk), Is.False,
            "SchedulerChunk is the canonical filterable detail record");

        var exemptCount = Enumerable.Range(0, 256)
            .Count(i => AttachSessionRuntime.IsExemptKind((TraceEventKind)i));
        Assert.That(exemptCount, Is.EqualTo(expected.Length), "the exempt set must contain exactly the six named kinds");
    }

    /// <summary>
    /// AC-2 — an idle tick's summary still carries everything the timeline draws: true-shaped tick numbers, start,
    /// duration, overload level, multiplier and the metronome wait. Only the detail-derived fields go empty.
    /// </summary>
    [Test]
    public async Task Idle_TickSummary_RetainsEveryFieldExceptDetailDerivedOnes()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        for (var i = 0; i < 4; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 10, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(3);

        var summaries = h.Summaries;
        // Tick numbers must be contiguous — the spine guarantees no renumbering.
        var numbers = summaries.Select(s => s.TickNumber).ToArray();
        Assert.That(numbers, Is.EqualTo(numbers.OrderBy(n => n).ToArray()), "tick numbers must be monotonic");
        for (var i = 1; i < numbers.Length; i++)
        {
            Assert.That(numbers[i] - numbers[i - 1], Is.EqualTo(1u), "tick numbers must be contiguous while idle");
        }

        // The metronome wait describes the gap PRECEDING a tick, so the first tick of a session legitimately has none.
        // Skipping it here is not tolerance for a flaky field — it is the field's definition.
        foreach (var s in summaries.Skip(1))
        {
            Assert.Multiple(() =>
            {
                Assert.That(s.DurationUs, Is.GreaterThan(0), "an idle tick still has a real duration");
                Assert.That(s.TickMultiplier, Is.EqualTo(1), "TickEnd payload must survive the filter");
                Assert.That(s.MetronomeWaitUs, Is.GreaterThan(0), "SchedulerMetronomeWait must survive the filter");
                Assert.That(s.ConsecutiveUnderrun, Is.EqualTo(2), "SchedulerOverloadDetector payload must survive the filter");
                Assert.That(s.EventCount, Is.EqualTo(CaptureHarness.ExemptRecordsPerTick),
                    "only the exempt skeleton may reach the builder — the 10 detail records must be gone");
                Assert.That(s.MaxSystemDurationUs, Is.Zero, "MaxSystemDuration is detail-derived, so it goes empty");
                Assert.That(s.ActiveSystemsBitmask, Is.EqualTo("0"), "the active-systems bitmask is detail-derived");
            });
        }
    }

    /// <summary>
    /// AC-3 — <b>duration parity</b>, the subtlest requirement in the design. An idle tick and an armed tick built from
    /// identical timings must report the identical duration.
    /// </summary>
    /// <remarks>
    /// This guards the <c>SchedulerMetronomeWait</c> trap. The builder lets 241's timestamp push the tick's end past
    /// <c>TickEnd</c> before sealing, so filtering 241 would make every idle bar shorter than every armed bar by the
    /// post-TickEnd setup time — a step at both window edges that reads to an operator as "the profiler slowed my app
    /// down when I hit Record". <c>MockRecordFactoryTests.MetronomeWait_ExtendsTickDuration_BeyondTickEnd</c> proves the
    /// nudge is real (50 µs vs 90 µs); this proves the filter does not reintroduce it.
    /// </remarks>
    [Test]
    public async Task IdleAndArmedTicks_ReportIdenticalDurations()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        // Ticks 0-2 idle, ticks 3-5 armed, ticks 6-8 idle again. Every tick has identical timing.
        await h.SendTickAsync(0, engineTick: 1, detailRecords: 5, ct: Timeout10s);
        await h.SendTickAsync(1, engineTick: 2, detailRecords: 5, ct: Timeout10s);
        await h.SendTickAsync(2, engineTick: 3, detailRecords: 5, ct: Timeout10s);
        await h.WaitForSummariesAsync(2);

        h.Runtime.Arm(3);
        for (var i = 3; i < 9; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 5, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(8);

        var summaries = h.Summaries;
        // Armed vs idle is discriminated on a genuinely detail-derived field. EventCount cannot serve here: it counts
        // the exempt skeleton too, so an idle tick reports a small non-zero number rather than zero.
        var armed = summaries.Where(s => s.ActiveSystemsBitmask != "0").ToArray();
        var idle = summaries.Where(s => s.ActiveSystemsBitmask == "0").ToArray();

        Assert.That(armed, Is.Not.Empty, "some ticks must have been recorded");
        Assert.That(idle, Is.Not.Empty, "some ticks must have stayed idle");

        var armedDuration = armed[0].DurationUs;
        foreach (var s in summaries)
        {
            Assert.That(s.DurationUs, Is.EqualTo(armedDuration).Within(0.01),
                $"tick {s.TickNumber} (events={s.EventCount}) must report the same duration as an armed tick — "
                + "a systematic difference is the 241 filtering bug the exempt set exists to prevent");
        }

        // And the expected absolute value: TickStart -> metronome-wait start, not TickStart -> TickEnd.
        Assert.That(armedDuration, Is.EqualTo(CaptureHarness.TickSealTicks / (double)CaptureHarness.TicksPerUs).Within(0.01),
            "the tick must extend to the metronome wait, matching an unfiltered session");
    }
}
