using System;
using System.Runtime.CompilerServices;
using Typhon.Engine.Profiler;

namespace Typhon.Engine;

/// <summary>
/// Scheduler ↔ profiler bridge. The thin wrapper methods here forward scheduler events (tick/system/chunk boundaries) into <see cref="TyphonEvent"/>
/// so the Tracy-style profiler can capture them. Each wrapper pays zero CPU cost when <see cref="TelemetryConfig.ProfilerActive"/> is false — the JIT
/// folds the entire body away.
/// </summary>
/// <remarks>
/// <b>Chunk bracketing:</b> chunks are no longer emitted as paired Start/End instants. A per-thread-local pending-start timestamp lets
/// <see cref="InspectorChunkStart"/> record the start time, which <see cref="InspectorChunkEnd"/> then folds into a single
/// <c>SchedulerChunkEvent</c> span record (with both start and end timestamps + entitiesProcessed) via <see cref="TyphonEvent.EmitSchedulerChunk"/>.
/// Halves the record count for scheduler events, which are the highest-frequency events the profiler sees.
/// </remarks>
public partial class DagScheduler
{
    [ThreadStatic]
    private static long PendingChunkStart;

    [ThreadStatic]
    private static int PendingChunkSystemIdx;

    [ThreadStatic]
    private static int PendingChunkIndex;

    [ThreadStatic]
    private static int PendingChunkTotal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickStart(long tickNumber, long timestamp)
    {
        TyphonEvent.SetCurrentTickNumber((int)tickNumber);
        TyphonEvent.EmitTickStart(timestamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorTickEnd(long tickNumber, long timestamp)
    {
        // overloadLevel + tickMultiplier reflect the values IN EFFECT during this tick — set by the previous tick's
        // OverloadDetector.Update at the end of ComputeAndRecordTelemetry, so by the time we're here they describe
        // the regime this tick just ran under. The current tick's detector update happens AFTER this emit (line ~490
        // in ExecuteTickMultiThreaded), which is the right ordering: the change applies to the NEXT tick.
        // Issue #289 follow-up: this used to be hardcoded (0, 1) so every TickSummary recorded a healthy state.
        var overloadLevel = (byte)Math.Min((int)_overloadDetector.CurrentLevel, byte.MaxValue);
        var multiplier = (byte)Math.Min(_tickMultiplier, byte.MaxValue);
        TyphonEvent.EmitTickEnd(timestamp, overloadLevel, multiplier);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemReady(int sysIdx, long timestamp) => TyphonEvent.EmitSystemReady((ushort)sysIdx, 0, timestamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorSystemSkipped(int sysIdx, SkipReason reason, long timestamp)
    {
        // Phase 4 (#282): wire-additive payload extension carries `wouldBeChunkCount` and
        // `successorsUnblocked`. wouldBeChunkCount = chunks the system would have processed
        // (TotalChunks for parallel queries, 1 for callback/single-invocation systems, 0 if
        // a parallel-query's TotalChunks hasn't been published yet for this tick).
        // successorsUnblocked = direct successor count freed from waiting on this skip.
        var sys = Systems[sysIdx];
        var wouldBe = sys.IsParallelQuery ? sys.TotalChunks : 1;
        var unblocked = sys.Successors.Length;
        TyphonEvent.EmitSystemSkipped((ushort)sysIdx, (byte)reason, timestamp, (ushort)Math.Min(wouldBe, ushort.MaxValue), (ushort)Math.Min(unblocked, ushort.MaxValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkStart(int sysIdx, int chunkIndex, long timestamp, int totalChunks)
    {
        PendingChunkStart = timestamp;
        PendingChunkSystemIdx = sysIdx;
        PendingChunkIndex = chunkIndex;
        PendingChunkTotal = totalChunks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InspectorChunkEnd(int sysIdx, int chunkIndex, long timestamp, int entitiesProcessed)
    {
        var startTs = PendingChunkStart;
        if (startTs == 0)
        {
            return;  // no pending start — profiler was off or we missed the start
        }

        TyphonEvent.EmitSchedulerChunk(sysIdx, chunkIndex, PendingChunkTotal, startTs, timestamp, entitiesProcessed);
        PendingChunkStart = 0;
    }
}
