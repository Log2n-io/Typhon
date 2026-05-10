// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.RuntimePhaseSpan"/>. Wraps one <see cref="TickPhase"/> region inside
/// <c>TyphonRuntime.OnTickEndInternal</c> as a real span, so child spans (PageCacheFlush, BTreeInsert, …) attach via <c>parentSpanId</c>.
/// </summary>
[TraceEvent(TraceEventKind.RuntimePhaseSpan, FactoryName = "BeginRuntimePhase", EmitEncoder = true)] internal ref partial struct RuntimePhaseSpanEvent
{
    [BeginParam(ParamType = "TickPhase")]
    public byte Phase;

}
