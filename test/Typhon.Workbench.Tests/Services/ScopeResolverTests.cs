using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ScopeResolver"/> (#351 Phase 5) — the composite-scope → QPC-interval-set resolution. Synthetic
/// metadata only, no on-disk traces, so the system tick-relative+offset arithmetic, the phase union, span-kind windows from
/// the span-instance index, and the overlap merge are each verified in isolation.
/// </summary>
[TestFixture]
public sealed class ScopeResolverTests
{
    // 1 MHz → qpc == µs, keeps the window arithmetic trivial to assert.
    private const long Freq = 1_000_000;

    private static SystemDefinitionDto Sys(ushort index, string phase) => new(
        index, $"System{index}", 0, 0, false, 0, [], [], phase, false,
        [], [], [], [], [], [], [], [], [], [], [], [], DagId: 0);

    private static TickSummaryDto Tick(uint number, double startUs) => new(
        number, startUs, 0f, 0, 0f, "0", 0, 0, 0, 0, 0, 0);

    private static SystemTickSummary SysTick(uint tick, ushort systemIndex, double startUs, double endUs, byte skip = 0)
        => new()
        {
            TickNumber = tick,
            SystemIndex = systemIndex,
            SkipReasonCode = skip,
            StartUs = startUs,
            EndUs = endUs,
        };

    private static CallTreeRequestDto Req(
        int? system = null, string phase = null, int? spanKind = null, double? startUs = null, double? endUs = null)
        => new(startUs, endUs, null, "wall-clock", system, phase, spanKind);

    [Test]
    public void Resolve_NoScope_ReturnsWholeSession()
    {
        var result = ScopeResolver.Resolve(Req(), [], [], [], () => SpanInstanceIndex.Empty, Freq);
        Assert.That(result, Is.EqualTo(ScopeResolver.WholeSession));
    }

    [Test]
    public void Resolve_Range_ProducesSingleWindow()
    {
        var result = ScopeResolver.Resolve(Req(startUs: 1000, endUs: 2000), [], [], [], () => SpanInstanceIndex.Empty, Freq);
        Assert.That(result, Is.EqualTo(new (long, long)[] { (1000, 2000) }));
    }

    [Test]
    public void Resolve_System_AbsoluteWindowIsTickStartPlusRelativeOffset()
    {
        // The SystemTickSummary StartUs/EndUs are tick-relative — the absolute window adds the owning tick's StartUs.
        TickSummaryDto[] ticks = [Tick(0, 1000), Tick(1, 5000)];
        SystemTickSummary[] sysTicks =
        [
            SysTick(tick: 0, systemIndex: 3, startUs: 10, endUs: 20),
            SysTick(tick: 1, systemIndex: 3, startUs: 30, endUs: 50),
        ];

        var result = ScopeResolver.Resolve(Req(system: 3), [], ticks, sysTicks, () => SpanInstanceIndex.Empty, Freq);

        Assert.That(result, Is.EqualTo(new (long, long)[] { (1010, 1020), (5030, 5050) }));
    }

    [Test]
    public void Resolve_System_DropsSkippedRows()
    {
        TickSummaryDto[] ticks = [Tick(0, 0)];
        SystemTickSummary[] sysTicks =
        [
            SysTick(tick: 0, systemIndex: 3, startUs: 10, endUs: 20),
            SysTick(tick: 0, systemIndex: 3, startUs: 100, endUs: 200, skip: 4), // skipped — must not produce a window
        ];

        var result = ScopeResolver.Resolve(Req(system: 3), [], ticks, sysTicks, () => SpanInstanceIndex.Empty, Freq);

        Assert.That(result, Is.EqualTo(new (long, long)[] { (10, 20) }));
    }

    [Test]
    public void Resolve_System_NeverRan_ReturnsEmpty()
    {
        TickSummaryDto[] ticks = [Tick(0, 0)];
        SystemTickSummary[] sysTicks = [SysTick(tick: 0, systemIndex: 3, startUs: 10, endUs: 20)];

        var result = ScopeResolver.Resolve(Req(system: 99), [], ticks, sysTicks, () => SpanInstanceIndex.Empty, Freq);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Resolve_Phase_UnionsSystemsInThatPhaseAndMergesOverlap()
    {
        SystemDefinitionDto[] systems = [Sys(1, "PhaseA"), Sys(2, "PhaseA"), Sys(3, "PhaseB")];
        TickSummaryDto[] ticks = [Tick(0, 0)];
        SystemTickSummary[] sysTicks =
        [
            SysTick(tick: 0, systemIndex: 1, startUs: 10, endUs: 20),
            SysTick(tick: 0, systemIndex: 2, startUs: 15, endUs: 25), // overlaps system 1 → merges
            SysTick(tick: 0, systemIndex: 3, startUs: 100, endUs: 110), // PhaseB — excluded
        ];

        var result = ScopeResolver.Resolve(Req(phase: "PhaseA"), systems, ticks, sysTicks, () => SpanInstanceIndex.Empty, Freq);

        Assert.That(result, Is.EqualTo(new (long, long)[] { (10, 25) }));
    }

    [Test]
    public void Resolve_SpanKind_ReturnsWindowsFromTheIndex()
    {
        var index = SpanInstanceIndex.FromWindows(new Dictionary<int, (long Start, long End)[]>
        {
            [10] = [(100, 200), (300, 400)],
        });

        var result = ScopeResolver.Resolve(Req(spanKind: 10), [], [], [], () => index, Freq);

        Assert.That(result, Is.EqualTo(new (long, long)[] { (100, 200), (300, 400) }));
    }

    [Test]
    public void Resolve_SpanKind_MergesOverlappingInstanceWindows()
    {
        // Two instances of the same kind whose windows overlap (e.g. nested / different threads) fold to one window.
        var index = SpanInstanceIndex.FromWindows(new Dictionary<int, (long Start, long End)[]>
        {
            [7] = [(100, 250), (200, 400)],
        });

        var result = ScopeResolver.Resolve(Req(spanKind: 7), [], [], [], () => index, Freq);

        Assert.That(result, Is.EqualTo(new (long, long)[] { (100, 400) }));
    }

    [Test]
    public void Resolve_SpanKind_UnknownKind_ReturnsEmpty()
    {
        var result = ScopeResolver.Resolve(Req(spanKind: 999), [], [], [], () => SpanInstanceIndex.Empty, Freq);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Resolve_SystemScope_DegradesToWholeSessionWhenFrequencyUnusable()
    {
        TickSummaryDto[] ticks = [Tick(0, 0)];
        SystemTickSummary[] sysTicks = [SysTick(tick: 0, systemIndex: 3, startUs: 10, endUs: 20)];

        var result = ScopeResolver.Resolve(Req(system: 3), [], ticks, sysTicks, () => SpanInstanceIndex.Empty, timestampFrequency: 0);

        Assert.That(result, Is.EqualTo(ScopeResolver.WholeSession));
    }
}
