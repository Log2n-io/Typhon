// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalFlush"/>. Required: <c>batchByteCount</c>, <c>frameCount</c>, <c>highLsn</c>.
/// </summary>
/// <remarks>
/// Payload: <c>[i32 batchByteCount][i32 frameCount][i64 highLsn]</c> = 16 bytes after the span header.
/// </remarks>
[TraceEvent(TraceEventKind.WalFlush, EmitEncoder = true)]
internal ref partial struct WalFlushEvent
{
    [BeginParam]
    public int BatchByteCount;
    [BeginParam]
    public int FrameCount;
    [BeginParam]
    public long HighLsn;

}

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalSegmentRotate"/>. Required: <c>newSegmentIndex</c>.
/// </summary>
/// <remarks>
/// Payload: <c>[i32 newSegmentIndex]</c> = 4 bytes after the span header.
/// </remarks>
[TraceEvent(TraceEventKind.WalSegmentRotate, EmitEncoder = true)]
internal ref partial struct WalSegmentRotateEvent
{
    [BeginParam]
    public int NewSegmentIndex;

}

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.WalWait"/>. Required: <c>targetLsn</c>.
/// Emitted on the calling thread, not the WAL writer.
/// </summary>
/// <remarks>
/// Payload: <c>[i64 targetLsn]</c> = 8 bytes after the span header.
/// </remarks>
[TraceEvent(TraceEventKind.WalWait, EmitEncoder = true)]
internal ref partial struct WalWaitEvent
{
    [BeginParam]
    public long TargetLsn;

}

