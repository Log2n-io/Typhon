using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// On-demand tick capture (#805) — arming semantics. Covers AC-4 (exactly N ticks), AC-5 (arming mid-tick does not
/// retro-arm the tick in flight), AC-6 (a block straddling the arm boundary is filtered per record) and AC-7 (two
/// windows in one session, with true contiguous tick numbering across the idle gap).
/// </summary>
[TestFixture]
public sealed class CaptureArmingTests
{
    private static CancellationToken Timeout10s => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>A tick carries recorded detail iff a SchedulerChunk reached the builder for it.</summary>
    private static bool HasDetail(TickSummaryDto s) => s.ActiveSystemsBitmask != "0";

    /// <summary>AC-4 — <c>Arm(N)</c> records exactly N consecutive ticks, then stops on its own.</summary>
    [Test]
    public async Task ArmN_RecordsExactlyNConsecutiveTicks_ThenStops()
    {
        const int windowTicks = 3;
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        // Two idle ticks first, so the window is provably bounded on both sides.
        await h.SendTickAsync(0, engineTick: 1, detailRecords: 4, ct: Timeout10s);
        await h.SendTickAsync(1, engineTick: 2, detailRecords: 4, ct: Timeout10s);
        await h.WaitForSummariesAsync(1);

        h.Runtime.Arm(windowTicks);

        for (var i = 2; i < 10; i++)
        {
            await h.SendTickAsync(i, engineTick: (uint)(i + 1), detailRecords: 4, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(9);

        var summaries = h.Summaries;
        var recorded = summaries.Where(HasDetail).Select(s => s.TickNumber).ToArray();

        Assert.That(recorded, Has.Length.EqualTo(windowTicks),
            $"exactly {windowTicks} ticks must carry detail; got [{string.Join(", ", recorded)}]");

        // ...and they must be consecutive.
        for (var i = 1; i < recorded.Length; i++)
        {
            Assert.That(recorded[i] - recorded[i - 1], Is.EqualTo(1u), "the recorded window must be contiguous");
        }

        Assert.That(h.Runtime.CaptureState.State, Is.EqualTo("Idle"), "the session must fall back to idle once the window closes");
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.EqualTo(windowTicks), "exactly the window's ticks count as recorded");
    }

    /// <summary>
    /// AC-5 — arming while a tick is already in flight must not retro-arm it. The window opens at the NEXT
    /// <c>TickStart</c>, so a capture can never contain a partial tick.
    /// </summary>
    [Test]
    public async Task ArmingMidTick_LeavesTheTickInFlightUntouched()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        // Open tick 1 and deliver some of its detail, but do not close it.
        await h.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.TickStart(CaptureHarness.At(0)),
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(100), durationTicks: 50, systemIndex: 1)), Timeout10s);

        // Arm mid-tick. Tick 1 is already open and must stay idle.
        h.Runtime.Arm(1);

        // Close tick 1, then run tick 2 (which is the one that should be armed) and tick 3 to finalize it.
        await h.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(200), durationTicks: 50, systemIndex: 2),
            MockRecordFactory.TickEnd(CaptureHarness.At(5_000)),
            MockRecordFactory.MetronomeWait(CaptureHarness.At(9_000), durationTicks: 200)), Timeout10s);
        await h.SendTickAsync(1, engineTick: 2, detailRecords: 4, ct: Timeout10s);
        await h.SendTickAsync(2, engineTick: 3, detailRecords: 4, ct: Timeout10s);
        await h.WaitForSummariesAsync(2);

        var summaries = h.Summaries;
        var first = summaries.First();
        Assert.That(HasDetail(first), Is.False,
            "the tick already in flight when Arm() was called must NOT be recorded — a window may not contain a partial tick");

        var recorded = summaries.Where(HasDetail).ToArray();
        Assert.That(recorded, Has.Length.EqualTo(1), "exactly the one tick after the arm must be recorded");
        Assert.That(recorded[0].TickNumber, Is.EqualTo(first.TickNumber + 1), "the window must open on the very next tick");
    }

    /// <summary>
    /// AC-6 — a single Block frame that straddles the arm boundary must be filtered <b>per record</b>. Block frames are
    /// a timestamp-ordered merge across all thread slots on a 1 ms drain cadence, so they do not align to ticks;
    /// dropping or passing one wholesale would leak pre-arm detail or lose post-arm detail.
    /// </summary>
    [Test]
    public async Task BlockStraddlingTheArmBoundary_IsFilteredPerRecord()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        // Open tick 1 (idle).
        await h.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.TickStart(CaptureHarness.At(0)),
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(100), durationTicks: 50, systemIndex: 1)), Timeout10s);

        h.Runtime.Arm(1);

        // ONE block carrying the tail of idle tick 1 AND the whole of armed tick 2. The arm decision happens in the
        // middle of this buffer.
        await h.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(200), durationTicks: 50, systemIndex: 2),   // still tick 1 — must be dropped
            MockRecordFactory.TickEnd(CaptureHarness.At(5_000)),
            MockRecordFactory.MetronomeWait(CaptureHarness.At(9_000), durationTicks: 200),
            MockRecordFactory.TickStart(CaptureHarness.At(10_000)),                                        // tick 2 opens — arm takes effect
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(10_100), durationTicks: 50, systemIndex: 5), // must be kept
            MockRecordFactory.SchedulerChunk(CaptureHarness.At(10_200), durationTicks: 50, systemIndex: 6), // must be kept
            MockRecordFactory.TickEnd(CaptureHarness.At(15_000)),
            MockRecordFactory.MetronomeWait(CaptureHarness.At(19_000), durationTicks: 200)), Timeout10s);

        // Tick 3 finalizes tick 2.
        await h.SendTickAsync(2, engineTick: 3, detailRecords: 0, ct: Timeout10s);
        await h.WaitForSummariesAsync(2);

        var summaries = h.Summaries;
        Assert.That(summaries, Has.Count.GreaterThanOrEqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(HasDetail(summaries[0]), Is.False, "the idle tick's detail, carried in the same block, must be dropped");
            Assert.That(HasDetail(summaries[1]), Is.True, "the armed tick's detail, carried in the same block, must be kept");
        });
    }

    /// <summary>
    /// AC-7 — two windows in one session both survive, and tick numbering stays contiguous and monotonic across the
    /// idle gap between them. This is the property the whole exempt-spine decision exists to protect: the builder
    /// derives tick numbers by counting <c>TickStart</c> markers, so a gap in the spine would renumber everything after it.
    /// </summary>
    [Test]
    public async Task TwoWindows_BothRecorded_WithContiguousTickNumbersAcrossTheGap()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        var tickIndex = 0;
        async Task RunTicks(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await h.SendTickAsync(tickIndex, engineTick: (uint)(tickIndex + 1), detailRecords: 3, ct: Timeout10s);
                tickIndex++;
            }
        }

        await RunTicks(2);                       // idle
        h.Runtime.Arm(2);
        await RunTicks(2);                       // window 1
        await RunTicks(3);                       // idle gap
        h.Runtime.Arm(2);
        await RunTicks(2);                       // window 2
        await RunTicks(2);                       // idle tail (also finalizes window 2)
        await h.WaitForSummariesAsync(10);

        var summaries = h.Summaries;
        var numbers = summaries.Select(s => s.TickNumber).ToArray();

        // Contiguity is the headline assertion: no renumbering, no gaps, no repeats.
        for (var i = 1; i < numbers.Length; i++)
        {
            Assert.That(numbers[i], Is.EqualTo(numbers[i - 1] + 1),
                $"tick numbers must be contiguous across idle gaps; got [{string.Join(", ", numbers)}]");
        }

        var recorded = summaries.Where(HasDetail).Select(s => s.TickNumber).ToArray();
        Assert.That(recorded, Has.Length.EqualTo(4), $"both windows must be recorded; got [{string.Join(", ", recorded)}]");

        // Two runs of two, separated by an idle gap — not one run of four.
        var runs = new List<List<uint>>();
        foreach (var n in recorded)
        {
            if (runs.Count == 0 || n != runs[^1][^1] + 1)
            {
                runs.Add([n]);
            }
            else
            {
                runs[^1].Add(n);
            }
        }
        Assert.That(runs, Has.Count.EqualTo(2), $"the two windows must be separated by idle ticks; runs were [{string.Join(" | ", runs.Select(r => string.Join(",", r)))}]");
        Assert.That(runs[0], Has.Count.EqualTo(2));
        Assert.That(runs[1], Has.Count.EqualTo(2));
        Assert.That(h.Runtime.CaptureState.RecordedTicks, Is.EqualTo(4), "both windows count toward the recorded total");
    }
}
