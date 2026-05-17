using NUnit.Framework;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SampleDensityFolder"/> (#351 Phase 5, §8.2) — the non-stationarity sparkline's binning.
/// Synthetic samples + interned stacks, no on-disk traces, so bin count / width, the in-scope total, view-mode and
/// frame-root filtering, and the empty-scope path are each verified in isolation.
/// </summary>
[TestFixture]
public sealed class SampleDensityTests
{
    private const long Freq = 1_000_000; // 1 MHz → qpc == µs.

    // Leaf-first stacks: stack 0 = [leaf 1, root 0]; stack 1 = [leaf 2, root 0].
    private static readonly ushort[][] Stacks = [[1, 0], [2, 0]];

    private static CallTreeRequestDto Scope(int? frameRoot = null, string viewMode = "wall-clock")
        => new(null, null, frameRoot, viewMode);

    private static long TotalCount(SampleDensityDto d)
    {
        long total = 0;
        foreach (var b in d.Bins)
        {
            total += b.Count;
        }
        return total;
    }

    [Test]
    public void Compute_BinsSamplesEvenlyAcrossSpan()
    {
        // 4 samples at qpc 0 / 10 / 20 / 30 → span 30 → 4 bins of width 7.5 → one sample per bin.
        CpuSampleRecord[] samples = [new(0, 0, 0, 0), new(10, 0, 0, 0), new(20, 0, 0, 0), new(30, 0, 0, 0)];

        var result = SampleDensityFolder.Compute(samples, Stacks, ScopeResolver.WholeSession, Scope(), Freq, 4);

        Assert.That(result.Bins, Has.Length.EqualTo(4));
        Assert.That(result.StartUs, Is.EqualTo(0));
        Assert.That(result.BinWidthUs, Is.EqualTo(7.5));
        foreach (var bin in result.Bins)
        {
            Assert.That(bin.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Compute_TotalsEveryInScopeSample()
    {
        CpuSampleRecord[] samples = [new(100, 0, 0, 0), new(200, 0, 0, 0), new(300, 0, 1, 1)];

        var result = SampleDensityFolder.Compute(samples, Stacks, ScopeResolver.WholeSession, Scope(), Freq, 8);

        Assert.That(TotalCount(result), Is.EqualTo(3));
    }

    [Test]
    public void Compute_OnCpu_ExcludesExternalSamples()
    {
        CpuSampleRecord[] samples = [new(100, 0, 0, 0), new(200, 0, 0, 0), new(300, 0, 1, 1)];

        var result = SampleDensityFolder.Compute(samples, Stacks, ScopeResolver.WholeSession, Scope(viewMode: "on-cpu"), Freq, 8);

        Assert.That(TotalCount(result), Is.EqualTo(2)); // the External sample is dropped
    }

    [Test]
    public void Compute_FrameRoot_CountsOnlyStacksContainingThatFrame()
    {
        // Frame 2 lives only in stack 1 ([2, 0]). Of the three samples only the qpc-300 one references stack 1.
        CpuSampleRecord[] samples = [new(100, 0, 0, 0), new(200, 0, 0, 0), new(300, 0, 0, 1)];

        var result = SampleDensityFolder.Compute(samples, Stacks, ScopeResolver.WholeSession, Scope(frameRoot: 2), Freq, 8);

        Assert.That(TotalCount(result), Is.EqualTo(1));
    }

    [Test]
    public void Compute_OutOfScopeWindow_ReturnsEmpty()
    {
        CpuSampleRecord[] samples = [new(100, 0, 0, 0)];

        var result = SampleDensityFolder.Compute(samples, Stacks, [(5000, 6000)], Scope(), Freq, 8);

        Assert.That(result.Bins, Is.Empty);
    }

    [Test]
    public void Compute_ZeroBinCount_DefaultsTo64()
    {
        CpuSampleRecord[] samples = [new(0, 0, 0, 0), new(6300, 0, 0, 0)];

        var result = SampleDensityFolder.Compute(samples, Stacks, ScopeResolver.WholeSession, Scope(), Freq, 0);

        Assert.That(result.Bins, Has.Length.EqualTo(64));
    }

    [Test]
    public void Compute_NoSamples_ReturnsEmpty()
    {
        var result = SampleDensityFolder.Compute([], Stacks, ScopeResolver.WholeSession, Scope(), Freq, 8);

        Assert.That(result, Is.EqualTo(SampleDensityDto.Empty));
    }
}
