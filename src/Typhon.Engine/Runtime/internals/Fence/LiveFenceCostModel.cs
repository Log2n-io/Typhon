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

    /// <summary>Microseconds per dirty cluster of a sliced Prep (#886 lead D). Seeded from the shadow and spatial per-cluster costs; learned like the others.</summary>
    public float PrepCost;

    /// <summary>µs per staged index value update. Calibrated exactly like <see cref="MigrationCost"/>, from the IndexMassUpdate phase's own chunk
    /// wall-time and unit counts.</summary>
    public float IndexUpdateCost;

    /// <summary>µs per staged EntityMap location patch, calibrated from the EntityMapUpdate phase's own chunk wall time and unit counts.</summary>
    public float EntityMapUpdateCost;

    /// <summary>µs per dirty cluster of a sliced Finalize emit (#889), calibrated from the Finalize phase's chunk wall time over the slices' dirty-cluster
    /// counts. Atomic Finalize items carry no units, so a phase with only those never moves it; a tick that mixes sliced and atomic archetypes charges
    /// the atomic items' wall to the slices' units and reads high — the same bias <see cref="PrepCost"/> has carried since #886, bounded by the
    /// <c>2 × W × O</c> chunk cap, so it yields smaller slices and never a wrong plan.</summary>
    public float FinalizeEmitCost;

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

    private readonly long[] _idxWall = new long[WindowSize];
    private readonly long[] _idxUnits = new long[WindowSize];
    private int _idxCursor;
    private long _idxSumWall;
    private long _idxSumUnits;

    private readonly long[] _emWall = new long[WindowSize];
    private readonly long[] _emUnits = new long[WindowSize];
    private int _emCursor;
    private long _emSumWall;
    private long _emSumUnits;

    private readonly long[] _prepWall = new long[WindowSize];
    private readonly long[] _prepUnits = new long[WindowSize];
    private int _prepCursor;
    private long _prepSumWall;
    private long _prepSumUnits;

    private readonly long[] _finWall = new long[WindowSize];
    private readonly long[] _finUnits = new long[WindowSize];
    private int _finCursor;
    private long _finSumWall;
    private long _finSumUnits;

    public LiveFenceCostModel(FenceCostModel seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        MigrationCost = seed.MigrationCost;
        AabbCost = seed.AabbCost;
        IndexUpdateCost = seed.IndexUpdateCost;
        EntityMapUpdateCost = seed.EntityMapUpdateCost;
        ShadowCost = seed.ShadowCost;
        SpatialCost = seed.SpatialCost;
        PrepCost = seed.ShadowCost + seed.SpatialCost;
        FinalizeEmitCost = seed.FinalizeEmitCost;
    }

    public void UpdatePhase(FencePhase phase, long wallTicks, long unitCount)
    {
        if (unitCount <= 0 || wallTicks <= 0) return;
        switch (phase)
        {
            case FencePhase.Prep:
                _prepSumWall  -= _prepWall[_prepCursor];
                _prepSumUnits -= _prepUnits[_prepCursor];
                _prepWall[_prepCursor]  = wallTicks;
                _prepUnits[_prepCursor] = unitCount;
                _prepSumWall  += wallTicks;
                _prepSumUnits += unitCount;
                _prepCursor = (_prepCursor + 1) & WindowMask;
                if (_prepSumUnits > 0)
                {
                    PrepCost = (float)((_prepSumWall * TicksToMicros) / _prepSumUnits);
                }
                break;
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

            case FencePhase.IndexMassUpdate:
                _idxSumWall  -= _idxWall[_idxCursor];
                _idxSumUnits -= _idxUnits[_idxCursor];
                _idxWall[_idxCursor]  = wallTicks;
                _idxUnits[_idxCursor] = unitCount;
                _idxSumWall  += wallTicks;
                _idxSumUnits += unitCount;
                _idxCursor = (_idxCursor + 1) & WindowMask;
                if (_idxSumUnits > 0)
                {
                    IndexUpdateCost = (float)((_idxSumWall * TicksToMicros) / _idxSumUnits);
                }
                break;

            case FencePhase.Finalize:
                _finSumWall  -= _finWall[_finCursor];
                _finSumUnits -= _finUnits[_finCursor];
                _finWall[_finCursor]  = wallTicks;
                _finUnits[_finCursor] = unitCount;
                _finSumWall  += wallTicks;
                _finSumUnits += unitCount;
                _finCursor = (_finCursor + 1) & WindowMask;
                if (_finSumUnits > 0)
                {
                    FinalizeEmitCost = (float)((_finSumWall * TicksToMicros) / _finSumUnits);
                }
                break;

            case FencePhase.EntityMapUpdate:
                _emSumWall  -= _emWall[_emCursor];
                _emSumUnits -= _emUnits[_emCursor];
                _emWall[_emCursor]  = wallTicks;
                _emUnits[_emCursor] = unitCount;
                _emSumWall  += wallTicks;
                _emSumUnits += unitCount;
                _emCursor = (_emCursor + 1) & WindowMask;
                if (_emSumUnits > 0)
                {
                    EntityMapUpdateCost = (float)((_emSumWall * TicksToMicros) / _emSumUnits);
                }
                break;
        }
    }
}
