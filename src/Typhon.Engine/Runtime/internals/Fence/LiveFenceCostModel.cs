using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-runtime mutable fence cost model. Calibrates <see cref="MigrationCost"/> and <see cref="AabbCost"/> from a 64-tick sliding window of measured µs/unit,
/// fed by <c>FencePhaseExecSystemBase</c>'s per-chunk wall-time totals. <see cref="ShadowCost"/> / <see cref="SpatialCost"/> stay at the seed values (no clean
/// per-unit attribution).
///
/// <para>The window stores raw (wall-ticks, unit-count) pairs and computes <c>sum(wall) / sum(units)</c>. This naturally weights samples by their unit count —
/// a tick that migrated 1000 entities contributes 10× more than a tick that migrated 100, which is the correct behaviour for averaging a per-unit rate.
/// Outlier ticks (GC pause, page fault) are not rejected; the window size is large enough that a single 10× spike pulls the average up by a bounded fraction.</para>
///
/// <para>Update is single-threaded — called once per tick from <c>TyphonRuntime.RunParallelFence</c> after the fence sub-DAG completes. Reads (the four float
/// fields) happen during the next tick's plan build and are memory-safe as long as the writer and readers don't overlap, which the tick-fence design guarantees.</para>
/// </summary>
internal sealed class LiveFenceCostModel
{
    private const int WindowSize = 64;
    private const int WindowMask = WindowSize - 1;

    private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

    public float MigrationCost;
    public float AabbCost;
    public readonly float ShadowCost;
    public readonly float SpatialCost;

    private readonly long[] _migWall = new long[WindowSize];
    private readonly long[] _migUnits = new long[WindowSize];
    private int _migCursor;
    private long _migSumWall;
    private long _migSumUnits;

    private readonly long[] _aabbWall = new long[WindowSize];
    private readonly long[] _aabbUnits = new long[WindowSize];
    private int _aabbCursor;
    private long _aabbSumWall;
    private long _aabbSumUnits;

    public LiveFenceCostModel(FenceCostModel seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        MigrationCost = seed.MigrationCost;
        AabbCost = seed.AabbCost;
        ShadowCost = seed.ShadowCost;
        SpatialCost = seed.SpatialCost;
    }

    public void UpdatePhase(FencePhase phase, long wallTicks, long unitCount)
    {
        if (unitCount <= 0 || wallTicks <= 0) return;
        switch (phase)
        {
            case FencePhase.Migrate:
                _migSumWall  -= _migWall[_migCursor];
                _migSumUnits -= _migUnits[_migCursor];
                _migWall[_migCursor]  = wallTicks;
                _migUnits[_migCursor] = unitCount;
                _migSumWall  += wallTicks;
                _migSumUnits += unitCount;
                _migCursor = (_migCursor + 1) & WindowMask;
                if (_migSumUnits > 0)
                {
                    MigrationCost = (float)((_migSumWall * TicksToMicros) / _migSumUnits);
                }
                break;

            case FencePhase.AabbRefresh:
                _aabbSumWall  -= _aabbWall[_aabbCursor];
                _aabbSumUnits -= _aabbUnits[_aabbCursor];
                _aabbWall[_aabbCursor]  = wallTicks;
                _aabbUnits[_aabbCursor] = unitCount;
                _aabbSumWall  += wallTicks;
                _aabbSumUnits += unitCount;
                _aabbCursor = (_aabbCursor + 1) & WindowMask;
                if (_aabbSumUnits > 0)
                {
                    AabbCost = (float)((_aabbSumWall * TicksToMicros) / _aabbSumUnits);
                }
                break;
        }
    }
}
