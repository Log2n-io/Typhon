using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Services;

/// <summary>
/// Scope helpers for the CPU-sample fold (#351 — H1). Samples are stored grouped per thread slot, each group a contiguous qpc-sorted run; a scope is a
/// sorted, disjoint set of QPC windows (see <see cref="ScopeResolver"/>). Intersecting the two with binary search lets a fold scan <i>only</i> the in-scope
/// samples — <c>O(runs · windows · log n + k)</c> — instead of linearly walking every sample on every request.
/// </summary>
internal static class CpuSampleScope
{
    /// <summary>
    /// Partitions a <c>(threadSlot, qpc)</c>-sorted sample array into its contiguous per-thread-slot runs. Each run is a qpc-sorted slice — which is what
    /// makes the windowed binary search in <see cref="ScopedSampleCursor"/> valid.
    /// </summary>
    public static (int Start, int Count)[] BuildThreadRuns(CpuSampleRecord[] samples)
    {
        if (samples == null || samples.Length == 0)
        {
            return [];
        }
        var runs = new List<(int, int)>();
        var runStart = 0;
        for (var i = 1; i <= samples.Length; i++)
        {
            if (i == samples.Length || samples[i].ThreadSlot != samples[runStart].ThreadSlot)
            {
                runs.Add((runStart, i - runStart));
                runStart = i;
            }
        }
        return [.. runs];
    }
}

/// <summary>
/// Zero-allocation forward cursor over the CPU samples that fall inside a scope's QPC windows. Construction does the binary-search work
/// (<c>O(runs · windows · log n)</c>); iteration is then a flat walk of the resolved index ranges, so a caller that needs two passes (sample-density)
/// just constructs it twice. Use with <c>foreach</c> — <c>Current</c> is a <c>ref readonly</c>.
/// </summary>
internal ref struct ScopedSampleCursor
{
    private readonly CpuSampleRecord[] _samples;
    private readonly (int Lo, int Hi)[] _ranges;
    private int _rangeIdx;
    private int _pos;
    private int _hi;

    /// <param name="samples">All CPU samples, grouped per thread slot, qpc-sorted within each slot.</param>
    /// <param name="threadRuns">The per-thread-slot contiguous runs (see <see cref="CpuSampleScope.BuildThreadRuns"/>).</param>
    /// <param name="windows">The scope as a <b>sorted, disjoint</b> QPC interval set (see <see cref="ScopeResolver"/>).</param>
    public ScopedSampleCursor(CpuSampleRecord[] samples, (int Start, int Count)[] threadRuns, (long Start, long End)[] windows)
    {
        _samples = samples;
        _ranges = BuildRanges(samples, threadRuns, windows);
        _rangeIdx = -1;
        _pos = 0;
        _hi = 0;
    }

    public readonly ScopedSampleCursor GetEnumerator() => this;

    public readonly ref readonly CpuSampleRecord Current => ref _samples[_pos - 1];

    public bool MoveNext()
    {
        while (_pos >= _hi)
        {
            _rangeIdx++;
            if (_rangeIdx >= _ranges.Length)
            {
                return false;
            }
            _pos = _ranges[_rangeIdx].Lo;
            _hi = _ranges[_rangeIdx].Hi;
        }
        _pos++;
        return true;
    }

    private static (int Lo, int Hi)[] BuildRanges(
        CpuSampleRecord[] samples,
        (int Start, int Count)[] runs,
        (long Start, long End)[] windows)
    {
        if (samples == null || samples.Length == 0 || runs == null || runs.Length == 0 || windows == null || windows.Length == 0)
        {
            return [];
        }
        var ranges = new List<(int, int)>();
        foreach (var run in runs)
        {
            var runStart = run.Start;
            var runEnd = run.Start + run.Count;
            // Each window is searched independently from runStart: windows are disjoint so ranges never overlap, and the per-run cost stays log-bounded.
            foreach (var w in windows)
            {
                var lo = LowerBound(samples, runStart, runEnd, w.Start);
                var hi = UpperBound(samples, lo, runEnd, w.End);
                if (lo < hi)
                {
                    ranges.Add((lo, hi));
                }
            }
        }
        return [.. ranges];
    }

    /// <summary>First index in <c>[lo, hi)</c> whose sample <c>Qpc &gt;= key</c>, or <paramref name="hi"/> if none.</summary>
    private static int LowerBound(CpuSampleRecord[] samples, int lo, int hi, long key)
    {
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (samples[mid].Qpc < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>First index in <c>[lo, hi)</c> whose sample <c>Qpc &gt; key</c>, or <paramref name="hi"/> if none.</summary>
    private static int UpperBound(CpuSampleRecord[] samples, int lo, int hi, long key)
    {
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (samples[mid].Qpc <= key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
}
