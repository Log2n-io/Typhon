// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.StatisticsRebuild"/>. Three required fields, no optionals.
/// </summary>
[TraceEvent(TraceEventKind.StatisticsRebuild, EmitEncoder = true)]
internal ref partial struct StatisticsRebuildEvent
{
    [BeginParam]
    public int EntityCount;
    [BeginParam]
    public int MutationCount;
    [BeginParam]
    public int SamplingInterval;

}

