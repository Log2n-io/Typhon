// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (metadata-only for the generator).
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer for <see cref="TraceEventKind.GcSuspension"/>. Span shape covering the GC EE-suspend window
/// (<c>GCSuspendEEBegin</c> → <c>GCRestartEEEnd</c>). Emitted from the GC-ingestion thread, which already owns its
/// slot — hence <c>ExternalSlot=true</c>. Start/end timestamps come from the caller (ETW callbacks fire on
/// separate events). SpanId is allocated internally; parent is zero (process-level event, no Typhon ambient span).
/// </summary>
[TraceEvent(TraceEventKind.GcSuspension, ExternalTimestamps = true, ExternalSlot = true, EmitEncoder = true, GenerateFactory = false)]
internal ref partial struct GcSuspensionEvent
{
    [BeginParam] public GcSuspendReason Reason;
}
