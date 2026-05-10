namespace Typhon.Engine.Internals;

/// <summary>
/// Discriminant for a <see cref="GcEventRecord"/> in the ingestion queue.
/// </summary>
internal enum GcEventRecordKind : byte
{
    None = 0,
    GcStart = 1,
    GcEnd = 2,
    SuspendBegin = 3,
    RestartEnd = 4,
}

/// <summary>
/// In-memory queue record carrying a single GC-related event from the CLR <see cref="System.Diagnostics.Tracing.EventListener"/> callback thread
/// to the profiler's <see cref="GcIngestionThread"/>. Blittable, fixed-size (24 bytes), no references — so the <see cref="GcEventQueue"/>'s
/// backing array never pressures the GC itself.
/// </summary>
/// <remarks>
/// <para>
/// The record is a loose tagged union: <see cref="Kind"/> selects which fields are valid. Heap-size snapshots are <i>not</i> carried here — the
/// ingestion thread calls <see cref="System.GC.GetGCMemoryInfo()"/> on <c>GcEnd</c> dequeue, keeping this struct small and payload-type-agnostic.
/// </para>
/// </remarks>
internal struct GcEventRecord
{
    public GcEventRecordKind Kind;
    public byte Generation;     // GcStart, GcEnd
    public byte Reason;         // GcStart (GcReason) or SuspendBegin (GcSuspendReason)
    public byte Type;           // GcStart (GcType)
    public uint Count;          // GcStart, GcEnd
    public long Timestamp;      // Stopwatch.GetTimestamp() at the moment OnEventWritten observed the event

    public static GcEventRecord ForGcStart(long ts, byte gen, byte reason, byte type, uint count) => new()
    {
        Kind = GcEventRecordKind.GcStart,
        Timestamp = ts,
        Generation = gen,
        Reason = reason,
        Type = type,
        Count = count,
    };

    public static GcEventRecord ForGcEnd(long ts, byte gen, uint count) => new()
    {
        Kind = GcEventRecordKind.GcEnd,
        Timestamp = ts,
        Generation = gen,
        Count = count,
    };

    public static GcEventRecord ForSuspendBegin(long ts, byte reason) => new()
    {
        Kind = GcEventRecordKind.SuspendBegin,
        Timestamp = ts,
        Reason = reason,
    };

    public static GcEventRecord ForRestartEnd(long ts) => new()
    {
        Kind = GcEventRecordKind.RestartEnd,
        Timestamp = ts,
    };
}
