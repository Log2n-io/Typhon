using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Sessions;

/// <summary>
/// On-demand tick capture (#805) — absolute tick numbering. Covers AC-8 (attach mid-run reports true simulation ticks),
/// AC-9 (no gauges ⇒ relative numbering, honestly flagged) and AC-10 (a dropped <c>TickStart</c> is detected rather than
/// silently renumbering the rest of the session).
/// </summary>
/// <remarks>
/// The builder derives tick numbers by counting <c>TickStart</c> markers from zero, so a session that attaches to an
/// engine already an hour into its run calls the first tick it sees "tick 1". The only absolute tick number anywhere on
/// the live wire is the one <c>PerTickSnapshot</c> carries at record offset 12, written straight from
/// <c>scheduler.CurrentTickNumber</c> — which is why the gauge record is exempt from filtering even though it is the
/// fattest thing in the exempt set.
/// </remarks>
[TestFixture]
public sealed class CaptureTickNumberingTests
{
    private static CancellationToken Timeout10s => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    /// <summary>AC-8 — attaching to an engine mid-run reports the engine's tick numbers, not attach-relative ones.</summary>
    [Test]
    public async Task AttachMidRun_ReportsTrueSimulationTickNumbers()
    {
        const uint firstEngineTick = 216_000;   // ~1 hour in at 60 Hz
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        for (var i = 0; i < 5; i++)
        {
            await h.SendTickAsync(i, engineTick: firstEngineTick + (uint)i, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(4);

        var summaries = h.Summaries;
        Assert.That(h.Runtime.CaptureState.TickNumbersAbsolute, Is.True, "the gauge record must have established the offset");

        Assert.That(summaries[0].TickNumber, Is.EqualTo(firstEngineTick),
            "the first reported tick must be the engine's tick, not 1");

        for (var i = 0; i < summaries.Count; i++)
        {
            Assert.That(summaries[i].TickNumber, Is.EqualTo(firstEngineTick + (uint)i),
                $"tick {i} must carry the engine's absolute number; got [{string.Join(", ", summaries.Select(s => s.TickNumber))}]");
        }

        Assert.That(h.Runtime.CaptureState.TickNumberingSuspect, Is.False, "numbering must be trusted when nothing was dropped");
    }

    /// <summary>
    /// AC-8 (manifest coherence) — the chunk manifest must be translated with the summaries. The client maps a
    /// microsecond range to a tick range via <c>tickSummaries</c> and then selects chunks by comparing against the
    /// manifest's tick range, so translating one and not the other would silently mis-map chunks to ticks.
    /// </summary>
    [Test]
    public async Task ChunkManifest_UsesTheSameAbsoluteTickNumbersAsTheSummaries()
    {
        const uint firstEngineTick = 500_000;
        await using var h = await CaptureHarness.StartAsync(CaptureMode.Everything, Timeout10s);

        for (var i = 0; i < 6; i++)
        {
            await h.SendTickAsync(i, engineTick: firstEngineTick + (uint)i, detailRecords: 2, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(5);

        // Force the in-flight chunk out so the manifest has an entry to inspect.
        var metadata = h.Runtime.Metadata;
        Assert.That(metadata, Is.Not.Null);

        var summaryMin = h.Summaries.Min(s => s.TickNumber);
        Assert.That(summaryMin, Is.GreaterThanOrEqualTo(firstEngineTick),
            "summaries must be in absolute tick space");

        foreach (var entry in metadata.ChunkManifest)
        {
            Assert.That(entry.FromTick, Is.GreaterThanOrEqualTo(firstEngineTick),
                $"chunk manifest must be in the same absolute tick space as the summaries; got fromTick={entry.FromTick}");
        }
    }

    /// <summary>
    /// AC-9 — an engine running without gauges publishes no absolute tick number anywhere, so numbering stays
    /// attach-relative. That must be reported honestly rather than presented as if it were absolute.
    /// </summary>
    [Test]
    public async Task WithoutGauges_NumberingStaysRelative_AndSaysSo()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        for (var i = 0; i < 5; i++)
        {
            await h.SendTickAsync(i, engineTick: 900_000 + (uint)i, includeGauges: false, ct: Timeout10s);
        }
        await h.WaitForSummariesAsync(4);

        var state = h.Runtime.CaptureState;
        Assert.Multiple(() =>
        {
            Assert.That(state.TickNumbersAbsolute, Is.False, "no gauge record ⇒ no absolute tick number is available");
            Assert.That(state.TickNumberingSuspect, Is.False, "relative numbering is a known limitation, not a fault");
        });

        Assert.That(h.Summaries[0].TickNumber, Is.EqualTo(1u),
            "without gauges the timeline is numbered from the attach point, and the UI must label it as such");
    }

    /// <summary>
    /// AC-10 — a lost <c>TickStart</c> is the one silent-corruption mode this design could introduce: the derived count
    /// falls behind and every later tick number is wrong, with nothing in the data looking unusual. The gauge record's
    /// absolute tick number is used as an independent oracle so the session says so instead.
    /// </summary>
    [Test]
    public async Task DroppedTickStart_IsDetected_RatherThanSilentlyRenumbering()
    {
        await using var h = await CaptureHarness.StartAsync(CaptureMode.CherryPick, Timeout10s);

        // Two well-formed ticks establish the offset.
        await h.SendTickAsync(0, engineTick: 100, ct: Timeout10s);
        await h.SendTickAsync(1, engineTick: 101, ct: Timeout10s);
        await h.WaitForSummariesAsync(1);
        Assert.That(h.Runtime.CaptureState.TickNumberingSuspect, Is.False, "no drift yet");

        // A tick whose TickStart never arrived — the engine advanced, our derived counter did not.
        await h.SendBlockAsync(MockRecordFactory.Concat(
            MockRecordFactory.TickEnd(CaptureHarness.At(25_000)),
            MockRecordFactory.PerTickSnapshot(CaptureHarness.At(25_010), engineTickNumber: 102),
            MockRecordFactory.MetronomeWait(CaptureHarness.At(29_000), durationTicks: 200)), Timeout10s);

        Assert.That(h.Runtime.CaptureState.TickNumberingSuspect, Is.True,
            "a TickStart gap must be surfaced — a plausible-looking renumbered timeline is worse than an admitted one");
    }
}
