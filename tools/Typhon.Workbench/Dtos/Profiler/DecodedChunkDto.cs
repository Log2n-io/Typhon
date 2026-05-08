using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// JSON projection of a single profiler chunk. The binary <c>GET /chunks/{idx}</c> endpoint returns
/// the LZ4-compressed packed records (cheap on the wire, requires the codec to interpret); this DTO
/// is what <c>GET /chunks/{idx}/decoded</c> returns — a fully-deserialized event list ready for
/// inspection from any HTTP client without needing the Typhon profiler library.
///
/// Filtering happens server-side via <c>?kinds=</c> (CSV of <see cref="TraceEventKind"/> ints) and
/// <c>?tick=</c> (single tick), so callers can scope a 50K-event chunk down to a workable slice
/// without paying for the full payload over the wire.
/// </summary>
/// <param name="FromTick">First tick covered by the chunk (matches <c>X-Chunk-From-Tick</c> on the binary endpoint).</param>
/// <param name="ToTick">Exclusive upper bound (matches <c>X-Chunk-To-Tick</c>).</param>
/// <param name="EventCount">Total events in the chunk on disk, regardless of any active filters.</param>
/// <param name="UncompressedBytes">Decompressed payload size — useful for sizing concerns when paginating manually.</param>
/// <param name="IsContinuation">True for mid-tick split chunks (no leading TickStart record).</param>
/// <param name="TimestampFrequency">Source Stopwatch frequency. <see cref="LiveTraceEvent.TimestampUs"/> and <see cref="LiveTraceEvent.DurationUs"/> are already converted; this is provided for cross-checks.</param>
/// <param name="FilteredEventCount">Number of events in <see cref="Events"/> after filters applied. Equals <see cref="EventCount"/> when no filters are passed.</param>
/// <param name="Events">Decoded event records. Each carries its <see cref="TraceEventKind"/> as <see cref="LiveTraceEvent.Kind"/> plus only the fields relevant for that kind (nulls elided by <c>JsonIgnoreCondition.WhenWritingNull</c>).</param>
public record DecodedChunkDto(
    int FromTick,
    int ToTick,
    int EventCount,
    int UncompressedBytes,
    bool IsContinuation,
    long TimestampFrequency,
    int FilteredEventCount,
    IReadOnlyList<LiveTraceEvent> Events);
