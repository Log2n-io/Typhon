// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.SchedulerChunk"/>. Four required fields (SystemIndex, ChunkIndex, TotalChunks,
/// EntitiesProcessed), no optionals. Span duration covers the chunk execution bracket; emitted via
/// <see cref="TyphonEvent.EmitSchedulerChunk"/> with caller-supplied start/end timestamps.
/// </summary>
[TraceEvent(TraceEventKind.SchedulerChunk, GenerateFactory = false, EmitEncoder = true, ExternalTimestamps = true)]
internal ref partial struct SchedulerChunkEvent
{
    [BeginParam(ParamType = "int")] public ushort SystemIndex;
    [BeginParam(ParamType = "int")] public ushort ChunkIndex;
    [BeginParam(ParamType = "int")] public ushort TotalChunks;
    [BeginParam] public int EntitiesProcessed;
}

