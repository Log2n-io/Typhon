// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.SchedulerChunk"/>. Four required fields (SystemIndex, ChunkIndex, TotalChunks,
/// EntitiesProcessed), no optionals. Span duration covers the chunk execution bracket.
/// </summary>
[TraceEvent(TraceEventKind.SchedulerChunk, GenerateFactory = false, EmitEncoder = true)]
internal ref partial struct SchedulerChunkEvent
{
    public ushort SystemIndex;
    public ushort ChunkIndex;
    public ushort TotalChunks;
    public int EntitiesProcessed;

    // Intentionally no Dispose() method: SchedulerChunkEvent is emitted in one shot via TyphonEvent.EmitSchedulerChunk, never via `using var`.
    // Not exposing Dispose prevents a future maintainer from writing `using var chunk = new SchedulerChunkEvent { ... };` and silently dropping
    // the record (the ref struct would go out of scope with no ring publish ever happening).
}

