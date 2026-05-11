// ReSharper disable once CheckNamespace
namespace Typhon.Profiler.Events;

/// <summary>
/// Base type for the typed-DTO trace event hierarchy. Each kind gets its own derived sealed record carrying
/// exactly its payload fields, dispatched at the wire boundary via System.Text.Json's polymorphic
/// serialization with the camelCase <c>kind</c> property as discriminator.
/// </summary>
/// <remarks>
/// <para>
/// The <c>[JsonPolymorphic]</c> + per-kind <c>[JsonDerivedType]</c> attributes live on a partial half of this record
/// emitted by <c>Typhon.Generators.TraceEventGenerator</c> — see <c>TraceEventDto.Polymorphism.g.cs</c>. Derived types
/// live in <c>Typhon.Profiler.Events</c> and are also generator-emitted from the <c>[TraceEvent]</c>-decorated ref
/// structs in <c>Typhon.Engine.Internals</c>.
/// </para>
/// <para>
/// <b>Common header fields</b> (every record carries these): <see cref="ThreadSlot"/>, <see cref="TickNumber"/>,
/// <see cref="TimestampUs"/>, <see cref="SourceLocationId"/>. Span-shaped events extend
/// <see cref="TraceSpanEventDto"/> for the additional duration + span-id chain + trace-context fields.
/// </para>
/// <para>
/// <b>Why a record, not a class:</b> records give value-equality + <c>with</c>-expressions cheaply, both useful in
/// tests and in viewer code that wants to construct minor variants of an event for display.
/// </para>
/// </remarks>
// OtherTraceEventDto isn't a generated DTO so the generator-emitted polymorphism partial doesn't list it.
// Register it manually here so the JsonPolymorphic resolver has it in its derived-type set — without this,
// any record that decodes to OtherTraceEventDto (instants, forward-compat) throws NotSupportedException at
// serialization time.
[System.Text.Json.Serialization.JsonDerivedType(typeof(OtherTraceEventDto), "other")]
public abstract partial record TraceEventDto
{
    /// <summary>
    /// Numeric <see cref="TraceEventKind"/> for this record, exposed as a CLR property to enable in-process
    /// filtering (e.g., the chunk-decode endpoint's <c>?kinds=20,21,30</c> query). Marked <c>[JsonIgnore]</c>
    /// so it doesn't compete with the JSON polymorphic discriminator (<c>kind</c>, camelCase string) on the
    /// wire — the wire never carries the byte, only the discriminator name. Each derived record overrides
    /// this with its kind constant; the override is generator-emitted alongside the rest of the DTO.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public abstract byte KindByte { get; }

    /// <summary>Producer thread slot (0..255). Indexes <c>ThreadSlotRegistry</c>.</summary>
    public byte ThreadSlot { get; init; }

    /// <summary>Tick number stamped by the consumer thread from the most recent <c>TickStart</c> marker.</summary>
    public int TickNumber { get; init; }

    /// <summary>Wall-clock timestamp in microseconds since trace start. Computed as <c>StartTimestamp / ticksPerUs</c>.</summary>
    public double TimestampUs { get; init; }

    /// <summary>
    /// Source-location id from the <c>SourceLocationGenerator</c> (#302) — non-zero when the event was emitted via an
    /// intercepted <c>BeginXxx</c> call that baked in the literal site id. Resolves through
    /// <c>RuntimeSourceLocationManifest</c> to file + line on the consumer side.
    /// </summary>
    public ushort SourceLocationId { get; init; }
}

/// <summary>
/// Span-shaped events — those that carry duration + span-id chain (begin/end pair) — extend this intermediate base.
/// Instant events extend <see cref="TraceEventDto"/> directly.
/// </summary>
/// <remarks>
/// Distributed-trace context (<see cref="TraceIdHi"/>/<see cref="TraceIdLo"/>) is present only when the producer
/// captured an OpenTelemetry trace id at the begin. Both fields are zero when no trace context was attached.
/// </remarks>
public abstract record TraceSpanEventDto : TraceEventDto
{
    /// <summary>Span duration in microseconds. Computed as <c>DurationTicks / ticksPerUs</c>.</summary>
    public double DurationUs { get; init; }

    /// <summary>This span's id as a decimal string (JS-safe — JavaScript <c>Number</c> can't represent the full <c>ulong</c> range).</summary>
    public string SpanId { get; init; }

    /// <summary>Enclosing span's id as a decimal string (zero ⇒ top-level span ⇒ "0").</summary>
    public string ParentSpanId { get; init; }

    /// <summary>Upper 64 bits of the OpenTelemetry trace id, decimal string. Empty / null when no trace context attached.</summary>
    public string TraceIdHi { get; init; }

    /// <summary>Lower 64 bits of the OpenTelemetry trace id, decimal string. Empty / null when no trace context attached.</summary>
    public string TraceIdLo { get; init; }
}

/// <summary>
/// Catch-all DTO for trace records whose kind doesn't map to a generator-emitted derived type. Used for
/// instant-shape records (TickStart, TickEnd, PhaseStart/End, etc.) that lack a <c>[TraceEvent]</c>
/// declaration — they go through the generic <c>InstantEventCodec</c> and don't carry typed payload —
/// and as forward-compat for kinds added to the wire after this consumer was built. Wire JSON has
/// <c>kind: "other"</c> as the discriminator and the numeric kind value in <see cref="OriginalKind"/>.
/// </summary>
public sealed record OtherTraceEventDto : TraceEventDto
{
    /// <summary>Numeric <see cref="TraceEventKind"/> value of the underlying record.</summary>
    public int OriginalKind { get; init; }

    /// <summary>True when the record carried the span-header extension (i.e., was a span, not an instant).</summary>
    public bool IsSpan { get; init; }

    /// <summary>Span duration in microseconds, when <see cref="IsSpan"/> is true.</summary>
    public double? DurationUs { get; init; }

    public override byte KindByte => (byte)OriginalKind;
}
