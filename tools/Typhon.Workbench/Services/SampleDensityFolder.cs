using System;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Services;

/// <summary>
/// Bins the in-scope CPU samples of a root frame over time (#351 Phase 5, §8.2) — the data behind the Call Tree's
/// non-stationarity sparkline. A flat profile means the scope is statistically stationary; spikes mean behavioral blending
/// (warm-up vs steady-state averaged together) and a narrower scope should be considered.
/// </summary>
public static class SampleDensityFolder
{
    private const int DefaultBinCount = 64;
    private const int MaxBinCount = 512;

    /// <summary>
    /// Counts the samples that fall in <paramref name="scopeWindows"/> (∩ view mode ∩ optional frame-root containment) into
    /// <paramref name="binCount"/> equal-width time-bins spanning the in-scope sample span. Both passes visit only the
    /// in-scope samples via a <see cref="ScopedSampleCursor"/> (#351 — H1). Returns <see cref="SampleDensityDto.Empty"/>
    /// when no sample is in scope.
    /// </summary>
    /// <param name="threadRuns">
    /// The per-thread-slot contiguous runs of <paramref name="samples"/> (see <see cref="CpuSampleScope.BuildThreadRuns"/>).
    /// Pass the session's pre-built table; <c>null</c> derives it inline (one O(n) pass — used by tests only).
    /// </param>
    public static SampleDensityDto Compute(
        CpuSampleRecord[] samples,
        ushort[][] stacks,
        (long Start, long End)[] scopeWindows,
        CallTreeRequestDto scope,
        long timestampFrequency,
        int binCount,
        (int Start, int Count)[] threadRuns = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (samples.Length == 0 || timestampFrequency <= 0)
        {
            return SampleDensityDto.Empty;
        }
        threadRuns ??= CpuSampleScope.BuildThreadRuns(samples);

        binCount = binCount <= 0 ? DefaultBinCount : Math.Min(binCount, MaxBinCount);
        var onCpuOnly = string.Equals(scope.ViewMode, "on-cpu", StringComparison.OrdinalIgnoreCase);
        var frameRoot = scope.FrameRoot;

        // Pass 1 — bounds of the in-scope sample set.
        var minQpc = long.MaxValue;
        var maxQpc = long.MinValue;
        var inScopeCount = 0;
        foreach (ref readonly var s in new ScopedSampleCursor(samples, threadRuns, scopeWindows))
        {
            if (!MatchesNonTime(in s, stacks, onCpuOnly, frameRoot))
            {
                continue;
            }
            inScopeCount++;
            if (s.Qpc < minQpc)
            {
                minQpc = s.Qpc;
            }
            if (s.Qpc > maxQpc)
            {
                maxQpc = s.Qpc;
            }
        }
        if (inScopeCount == 0)
        {
            return SampleDensityDto.Empty;
        }

        // A zero-width span (every in-scope sample at the same qpc) collapses to a single bin.
        var spanQpc = maxQpc - minQpc;
        if (spanQpc <= 0)
        {
            binCount = 1;
        }
        var binWidthQpc = spanQpc <= 0 ? 1.0 : (double)spanQpc / binCount;

        // Pass 2 — bin the in-scope samples.
        var counts = new long[binCount];
        foreach (ref readonly var s in new ScopedSampleCursor(samples, threadRuns, scopeWindows))
        {
            if (!MatchesNonTime(in s, stacks, onCpuOnly, frameRoot))
            {
                continue;
            }
            var idx = (int)((s.Qpc - minQpc) / binWidthQpc);
            if (idx < 0)
            {
                idx = 0;
            }
            else if (idx >= binCount)
            {
                idx = binCount - 1;
            }
            counts[idx]++;
        }

        var qpcToUs = 1_000_000.0 / timestampFrequency;
        var bins = new SampleDensityBinDto[binCount];
        for (var i = 0; i < binCount; i++)
        {
            bins[i] = new SampleDensityBinDto((minQpc + i * binWidthQpc) * qpcToUs, counts[i]);
        }
        return new SampleDensityDto(minQpc * qpcToUs, binWidthQpc * qpcToUs, bins);
    }

    /// <summary>The non-time scope filters — view mode and optional frame-root containment. Time-window scoping is the <see cref="ScopedSampleCursor"/>'s job.</summary>
    private static bool MatchesNonTime(in CpuSampleRecord s, ushort[][] stacks, bool onCpuOnly, int? frameRoot)
    {
        if (onCpuOnly && s.SampleType != 0)
        {
            return false;
        }
        if (frameRoot.HasValue)
        {
            if (s.StackIndex >= (uint)stacks.Length)
            {
                return false;
            }
            var stack = stacks[s.StackIndex];
            var contains = false;
            for (var k = 0; k < stack.Length; k++)
            {
                if (stack[k] == frameRoot.Value)
                {
                    contains = true;
                    break;
                }
            }
            if (!contains)
            {
                return false;
            }
        }
        return true;
    }
}
