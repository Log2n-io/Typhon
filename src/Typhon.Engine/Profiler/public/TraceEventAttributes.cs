// Attribute + enum surface consumed by TraceEventGenerator.
//
// These types used to be injected by the generator itself via RegisterPostInitializationOutput. That output lands in EVERY project referencing
// Typhon.Generators as an analyzer, and each of those projects also references Typhon.Engine — which exports the very same types. The result was a
// duplicate-type conflict (CS0436) in Typhon.Engine.Tests and Typhon.Workbench.Fixtures, silently resolved in favour of the injected copy.
//
// Declaring them here instead means exactly one definition exists, in the assembly that owns the profiler surface. The generator resolves [TraceEvent] against
// this source directly (real source is always visible to generators, so no post-init round-trip is needed), and every other project picks the types up from the
// engine reference.
//
// TraceEventGenerator is engine-only by design — the consumer-facing package ships Typhon.Generators.Consumer, which contains ArchetypeAccessorGenerator only
// (see Typhon.Engine.csproj). Nothing outside this assembly needs the generator to synthesize these declarations.

using System;
using Typhon.Profiler;

namespace Typhon.Engine.Profiler
{
    /// <summary>Wire-shape of a typed trace event. Determines header layout and the generated producer API.</summary>
    public enum TraceEventShape : byte
    {
        /// <summary>
        /// 37-byte span header (start+end timestamps, spanId, parent, optional trace context). Producer pattern:
        /// <c>using var e = BeginX(...); ... e.Dispose();</c>.
        /// </summary>
        Span = 0,
        /// <summary>
        /// 12-byte instant header (single timestamp, no spanId). Producer pattern: <c>EmitX(args)</c> — a single static call, no ref struct at the call site.
        /// </summary>
        Instant = 1,
    }

    /// <summary>
    /// Marks a partial ref struct as a typed event for the Typhon profiler. The generator emits the header field, kind constant, optional-property accessors,
    /// and Dispose method as a partial half; the user retains ComputeSize and EncodeTo.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class TraceEventAttribute : Attribute
    {
        /// <summary>Declares the struct as the producer of the given trace-event kind.</summary>
        /// <param name="kind">Trace-event kind this struct produces.</param>
        public TraceEventAttribute(TraceEventKind kind) { Kind = kind; }
        /// <summary>Trace-event kind this struct produces.</summary>
        public TraceEventKind Kind { get; }
        /// <summary>Codec class containing the OptX mask constants. Required only if any field is [Optional].</summary>
        public Type Codec { get; set; }
        /// <summary>Override for the generated Begin factory name. Defaults to "Begin" + Kind name.</summary>
        public string FactoryName { get; set; }
        /// <summary>Set to false to skip generating a Begin factory (for kinds emitted via custom code).</summary>
        public bool GenerateFactory { get; set; } = true;
        /// <summary>
        /// When true, the generator emits ComputeSize and EncodeTo directly, bypassing the per-kind codec Encode method. Use only for events whose wire layout
        /// is the standard span shape: header + (optional trace context) + required payload fields in declaration order + (optMask byte if any [Optional]) +
        /// optional fields in declaration order.
        /// </summary>
        public bool EmitEncoder { get; set; }

        /// <summary>
        /// Wire shape — <see cref="TraceEventShape.Span"/> (default) or <see cref="TraceEventShape.Instant"/>. Instant uses a 12-byte header and the generator
        /// emits a direct <c>EmitX(args)</c> static method instead of the Begin/Dispose ref-struct pattern (no per-call ref-struct materialization, no
        /// try/finally, ~3 ns faster than the legacy hand-written codec path).
        /// </summary>
        public TraceEventShape Shape { get; set; }

        /// <summary>
        /// Override the static gate field name used in the emitted EmitX body. Defaults to <c>"ProfilerActive"</c>. When set, the emitted check becomes
        /// <c>if (!TelemetryConfig.{Gate}) return;</c> — used for kinds with per-kind gating (e.g. <c>ConcurrencyAccessControlSharedAcquireActive</c>).
        /// </summary>
        public string Gate { get; set; }

        /// <summary>
        /// Span kinds with caller-supplied start/end timestamps (no Begin/Dispose timing). When true, the generator emits a single static
        /// <c>EmitX(long startTs, long endTs, ...payloadParams)</c> method instead of the Begin/Dispose factory pair. Internally allocates a fresh spanId and
        /// links to <c>CurrentOpenSpanId</c> as parent. Used for completion-style spans where the duration is known only at the end.
        /// </summary>
        public bool ExternalTimestamps { get; set; }

        /// <summary>
        /// When combined with <see cref="ExternalTimestamps"/>, the caller also supplies the spanId (correlation id linking back to a prior Begin span). The
        /// emitted signature becomes <c>EmitX(long startTs, ulong spanId, long endTs, ...payloadParams)</c>; parent is zero. Used for async completion events
        /// (e.g. PageCacheDiskReadCompleted carrying the originating Read span's id).
        /// </summary>
        public bool ExternalSpanId { get; set; }

        /// <summary>
        /// Instant kinds emitted from a context that already owns a thread slot (GC-ingestion thread, ThreadInfo registration). When true the generator emits
        /// <c>EmitX(byte slot, long timestamp, ...payloadParams)</c> and skips the <c>ThreadSlotRegistry.GetOrAssignSlot()</c> claim.
        /// </summary>
        public bool ExternalSlot { get; set; }
    }

    /// <summary>
    /// Marks a private backing field as carrying an optional payload value. The generator emits a public property (PascalCase of the field name without the
    /// leading underscore) whose setter assigns the backing field and flips the matching OptX bit in _optMask.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class OptionalAttribute : Attribute
    {
        /// <summary>Override for the mask constant name. Defaults to "Opt" + PascalCase(field name).</summary>
        public string MaskConstant { get; set; }
        /// <summary>Override for the public property name. Defaults to PascalCase(field name without underscore).</summary>
        public string PropertyName { get; set; }
        /// <summary>
        /// Inline mask byte. When non-zero the generator emits the literal value at every site that references the optional, removing the need for an external
        /// <c>Codec.OptX</c> constant. Required when the producer ref struct does not pin a <c>Codec = typeof(...)</c> companion.
        /// </summary>
        public byte MaskValue { get; set; }
        /// <summary>
        /// Override the wire-slot size in bytes. Defaults to the field's natural wire size (1 for byte/bool, 2 for short, 4 for int, 8 for long). Use this for
        /// slot-sharing cases where the wire format reserves a wider slot than the field itself needs — e.g. <c>EcsQueryAny._found</c> shares a 4-byte slot
        /// with <c>_resultCount</c>. The generator writes the natural value at the start of the slot and pads the remaining bytes with zeroes; the decoder
        /// reads the natural type and skips the padding.
        /// </summary>
        public byte WireSize { get; set; }
    }

    /// <summary>
    /// Marks a public payload field as a Begin-factory parameter. Fields without this attribute are not factory params (they are filled later by the caller
    /// before Dispose runs).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class BeginParamAttribute : Attribute
    {
        /// <summary>
        /// Overrides the factory parameter type. Defaults to the field's type. When set, the generator emits an explicit cast in the factory body: Field =
        /// (FieldType)param.
        /// </summary>
        public string ParamType { get; set; }
    }
}
