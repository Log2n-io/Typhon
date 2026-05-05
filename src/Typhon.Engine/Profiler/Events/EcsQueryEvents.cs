// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler;

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
[TraceEvent(TraceEventKind.EcsQueryExecute, Codec = typeof(EcsQueryEventCodec), EmitEncoder = true)]
public ref partial struct EcsQueryExecuteEvent
{
    /// <summary>Required — archetype type ID.</summary>
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional]
    private int _resultCount;
    [Optional]
    private EcsQueryScanMode _scanMode;

}

[TraceEvent(TraceEventKind.EcsQueryCount, Codec = typeof(EcsQueryEventCodec), EmitEncoder = true)]
public ref partial struct EcsQueryCountEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional]
    private int _resultCount;
    [Optional]
    private EcsQueryScanMode _scanMode;

}

// Escape-hatch from the Phase 3 generator: EcsQueryAny shares EcsQueryEventCodec with Execute / Count, but
// the codec packs `Found` (bool) and `ResultCount` (i32) into the same 4-byte slot — when OptFound is set
// the wire reserves 4 bytes (not 1) for the value. The generator's per-field standard layout doesn't model
// this slot-sharing, so this kind keeps its hand-written codec call.
[TraceEvent(TraceEventKind.EcsQueryAny, Codec = typeof(EcsQueryEventCodec))]
public ref partial struct EcsQueryAnyEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional]
    private bool _found;
    [Optional]
    private EcsQueryScanMode _scanMode;

    public readonly int ComputeSize()
        => EcsQueryEventCodec.ComputeSize(Header.TraceIdHi != 0 || Header.TraceIdLo != 0, _optMask, Header.SourceLocationId);

    public readonly void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten)
        => EcsQueryEventCodec.Encode(destination, endTimestamp, TraceEventKind.EcsQueryAny, Header.ThreadSlot, Header.StartTimestamp,
            Header.SpanId, Header.ParentSpanId, Header.TraceIdHi, Header.TraceIdLo, ArchetypeTypeId, _optMask, 0, _scanMode,
            _found, out bytesWritten, sourceLocationId: Header.SourceLocationId);
}

