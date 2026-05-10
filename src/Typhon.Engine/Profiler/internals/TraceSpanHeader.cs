namespace Typhon.Engine.Internals;

/// <summary>
/// Common span-header fields shared by every typed-event ref struct. Embedded as the first field on each <c>I*Event : ITraceEventEncoder</c> so
/// the seven prologue fields ride together and a future header-layout change is a one-line edit instead of touching ~99 event types.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-format invariance.</b> This struct never appears on the wire as a single blob. The codec methods take individual fields as separate
/// arguments (ThreadSlot, StartTimestamp, ...). Phase 1 of the #294 refactor only changes how the producer-side struct lays them out in memory;
/// the codec call signatures are unchanged and the produced bytes are byte-for-byte identical. Verified by
/// <see cref="Typhon.Engine.Tests.Profiler.TraceEventEncodeEquivalenceTests"/>.
/// </para>
/// <para>
/// <b>Public field rather than property.</b> The producer-side path (<c>BeginX</c> factories, <c>EncodeTo</c>, <c>Dispose</c>) reads and writes
/// these fields on the hot path. Field access folds to a single load with zero indirection; a property would add a method call the JIT may or may
/// not eliminate. There is no behavior to encapsulate here — this is data carried between Begin and Dispose.
/// </para>
/// <para>
/// <b>Field order.</b> Fields are declared 8-byte first, 2-byte next, 1-byte last so the default sequential layout stays compact. With the
/// 8-byte fields up front, the trailing <c>SourceLocationId</c> (u16) and <c>ThreadSlot</c> (u8) pack into the tail with 5 bytes of padding to
/// the next 8-byte alignment — total ~56 bytes vs. the 64 bytes the natural-reading order would produce. Wire format is unaffected — the codec
/// methods take individual fields, never the struct as a blob.
/// </para>
/// </remarks>
internal struct TraceSpanHeader
{
    /// <summary>QPC timestamp captured at <c>BeginX</c>. The matching end timestamp is captured at <c>Dispose</c>.</summary>
    public long StartTimestamp;

    /// <summary>Unique span ID assigned at Begin. Used as the parent ID for any nested span opened from this scope.</summary>
    public ulong SpanId;

    /// <summary>SpanId of the enclosing Typhon span on this thread, or zero if none.</summary>
    public ulong ParentSpanId;

    /// <summary>SpanId of the prior open span — restored by <c>Dispose</c> as <c>CurrentOpenSpanId</c> so LIFO nesting works.</summary>
    public ulong PreviousSpanId;

    /// <summary>High 64 bits of the W3C trace context, when an enclosing <c>Activity</c> was captured. Zero when no trace context attached.</summary>
    public ulong TraceIdHi;

    /// <summary>Low 64 bits of the W3C trace context. Zero when no trace context attached.</summary>
    public ulong TraceIdLo;

    /// <summary>
    /// Compile-time call-site identifier assigned by <c>SourceLocationGenerator</c> via the C# 14 interceptor wrapper. Zero when the call site
    /// is outside the generator's scope (i.e. anywhere outside <c>Typhon.Engine</c>) or when source attribution is disabled — codecs treat the
    /// zero value as "no source attribution" and omit the optional 2-byte wire payload. See <c>claude/design/observability/10-profiler-source-attribution.md</c>.
    /// </summary>
    public ushort SourceLocationId;

    /// <summary>Producer thread slot — index into <c>ThreadSlotRegistry</c>. Set by the <c>BeginX</c> factory.</summary>
    public byte ThreadSlot;
}
