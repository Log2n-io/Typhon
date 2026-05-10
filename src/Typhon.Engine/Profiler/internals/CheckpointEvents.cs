// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.CheckpointCycle"/>. Required: targetLsn, reason. Optional: dirtyPageCount (set after
/// dirty-page collection completes).
/// </summary>
[TraceEvent(TraceEventKind.CheckpointCycle, Codec = typeof(CheckpointEventCodec), EmitEncoder = true)]
internal ref partial struct CheckpointCycleEvent
{
    [BeginParam]
    public long TargetLsn;
    [BeginParam(ParamType = "CheckpointReason")]
    public byte Reason;

    [Optional]
    private int _dirtyPageCount;

}

/// <summary>Checkpoint collect-dirty-pages phase — no typed payload (span header only).</summary>
[TraceEvent(TraceEventKind.CheckpointCollect, EmitEncoder = true)]
internal ref partial struct CheckpointCollectEvent
{

}

/// <summary>
/// Checkpoint write-dirty-pages phase. Optional: writtenCount (set after pages are written).
/// </summary>
[TraceEvent(TraceEventKind.CheckpointWrite, Codec = typeof(CheckpointEventCodec), EmitEncoder = true)]
internal ref partial struct CheckpointWriteEvent
{
    [Optional]
    private int _writtenCount;

}

/// <summary>Checkpoint fsync phase — no typed payload (span header only).</summary>
[TraceEvent(TraceEventKind.CheckpointFsync, EmitEncoder = true)]
internal ref partial struct CheckpointFsyncEvent
{

}

/// <summary>
/// Checkpoint transition-UoW-entries phase. Optional: transitionedCount (set after transition completes).
/// </summary>
[TraceEvent(TraceEventKind.CheckpointTransition, Codec = typeof(CheckpointEventCodec), EmitEncoder = true)]
internal ref partial struct CheckpointTransitionEvent
{
    [Optional]
    private int _transitionedCount;

}

/// <summary>
/// Checkpoint recycle-WAL-segments phase. Optional: recycledCount (set after recycling completes).
/// </summary>
[TraceEvent(TraceEventKind.CheckpointRecycle, Codec = typeof(CheckpointEventCodec), EmitEncoder = true)]
internal ref partial struct CheckpointRecycleEvent
{
    [Optional]
    private int _recycledCount;

}

