// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsQueryExecute"/>. Lifecycle: caller constructs with required fields, assigns optional
/// fields via setters (each setter sets its bit in <c>_optMask</c>), caller invokes <see cref="EncodeTo"/> to serialize. Zero allocation — all state
/// lives in the stack frame of the using scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage pattern</b> (what the engine call sites will look like in Phase 4):
/// <code>
/// using var e = TyphonEvent.BeginEcsQueryExecute(archetypeTypeId);
/// // ... compute ...
/// e.ResultCount = results.Count;
/// e.ScanMode = EcsQueryScanMode.Targeted;
/// </code>
/// </para>
/// <para>
/// <b>Size:</b> minimum 40 B (37 B span header + 2 B archetype + 1 B mask) without trace context or optional fields. Maximum 62 B (53 B with trace
/// context + 2 B archetype + 1 B mask + 4 B result count + 1 B scan mode). Down from the old fixed 64 B struct's wasted space.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.EcsQueryExecute, EmitEncoder = true)]
internal ref partial struct EcsQueryExecuteEvent
{
    /// <summary>Required — archetype type ID.</summary>
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional(MaskValue = 0x01)]
    private int _resultCount;
    [Optional(MaskValue = 0x02)]
    private EcsQueryScanMode _scanMode;

}

[TraceEvent(TraceEventKind.EcsQueryCount, EmitEncoder = true)]
internal ref partial struct EcsQueryCountEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional(MaskValue = 0x01)]
    private int _resultCount;
    [Optional(MaskValue = 0x02)]
    private EcsQueryScanMode _scanMode;

}

/// <summary>
/// Producer for <see cref="TraceEventKind.EcsQueryAny"/>. Wire layout matches Execute/Count except the
/// <c>_found</c> bool occupies the same 4-byte slot that <c>_resultCount</c> uses on Execute/Count — the
/// <c>WireSize = 4</c> override on the optional widens the bool's natural 1-byte slot to 4 bytes (3 trailing
/// zero pad bytes), preserving the legacy codec's wire shape.
/// </summary>
[TraceEvent(TraceEventKind.EcsQueryAny, EmitEncoder = true)]
internal ref partial struct EcsQueryAnyEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional(MaskValue = 0x04, WireSize = 4)]
    private bool _found;
    [Optional(MaskValue = 0x02)]
    private EcsQueryScanMode _scanMode;
}

