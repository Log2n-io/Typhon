using System.Runtime.InteropServices;

namespace Typhon.Profiler;

/// <summary>
/// Fixed 64-byte header at the start of a <c>.typhon-trace</c> file. Contains session-wide metadata that lets the viewer decode the record stream
/// that follows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version 3</b> (Tracy-style typed-event rewrite): file format uses variable-size self-describing records instead of a fixed 64 B struct.
/// Block layout is size-prefixed records, LZ4-compressed per block. Older v1/v2 files are unreadable — the viewer and all tooling are updated
/// in lockstep.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TraceFileHeader
{
    /// <summary>File magic: ASCII "TYTR" (0x52_54_59_54 little-endian).</summary>
    public uint Magic;

    /// <summary>Format version. See <see cref="CurrentVersion"/> for the current value and an evolution log.</summary>
    public ushort Version;

    /// <summary>Flags (reserved for future use).</summary>
    public ushort Flags;

    /// <summary><c>Stopwatch.Frequency</c> — needed to convert timestamp ticks to real time.</summary>
    public long TimestampFrequency;

    /// <summary>Target tick rate in Hz (e.g., 60.0 for 60 fps).</summary>
    public float BaseTickRate;

    /// <summary>Number of worker threads in the DagScheduler.</summary>
    public byte WorkerCount;

    /// <summary>Number of systems in the DAG.</summary>
    public ushort SystemCount;

    /// <summary>Number of archetypes in the archetype table.</summary>
    public ushort ArchetypeCount;

    /// <summary>Number of component types in the component type table.</summary>
    public ushort ComponentTypeCount;

    /// <summary>UTC timestamp when the trace was started (DateTime.UtcNow.Ticks).</summary>
    public long CreatedUtcTicks;

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> captured when the host's EventPipe CPU-sampling session started, or <c>0</c> if no sampling companion is
    /// attached to this trace. The viewer correlates <c>.nettrace</c> CPU samples into the flame graph by mapping their relative milliseconds
    /// against this anchor in the same <see cref="TimestampFrequency"/> time base the record stream uses.
    /// </summary>
    public long SamplingSessionStartQpc;

    /// <summary>
    /// Byte offset of the trailing <c>FileTable</c> (interned source-file paths). 0 when no source-location manifest was written
    /// (e.g., the source-attribution generator emitted nothing). See claude/design/observability/10-profiler-source-attribution.md §4.6.
    /// </summary>
    public long FileTableOffset;

    /// <summary>
    /// Byte offset of the trailing <c>SourceLocationManifest</c> (id → file/line/method/kind table). 0 when absent.
    /// Bound to a non-zero <see cref="FileTableOffset"/> when the trace carries source attribution.
    /// </summary>
    public long SourceLocationManifestOffset;

    /// <summary>Padding to keep on-disk layout future-extension-friendly. Zero-initialized; readers must ignore.</summary>
    public ushort Reserved0;
    /// <summary>Padding (aligning the next field to 4 bytes); zero-initialized.</summary>
    public ushort Reserved1;

    /// <summary>File magic constant: ASCII "TYTR".</summary>
    public const uint MagicValue = 0x52_54_59_54; // 'T','Y','T','R' little-endian

    /// <summary>
    /// Current format version.
    /// v3: variable-size typed-record layout (Tracy-style profiler rewrite).
    /// v4: ThreadInfo records gained the trailing <c>ThreadKind</c> byte (#289 follow-up).
    /// v5: trailer carries <c>FileTable</c> + <c>SourceLocationManifest</c> at offsets in the header
    ///     (#302 — profiler source attribution). Reader accepts v4 files transparently — their new offset fields
    ///     are absent in the on-disk header (51 bytes vs 71) and default to 0, which downstream readers interpret
    ///     as "no source-location manifest".
    /// v6: SystemDefinitionTable carries RFC 07 access declarations (Phase, Reads, ReadsFresh,
    ///     ReadsSnapshot, AdditionalReads, Writes, SideWrites, ReadsEvents, WritesEvents, ReadsResources,
    ///     WritesResources, ExplicitAfter, ExplicitBefore, IsExclusivePhase). New PhasesTable section follows
    ///     ComponentTypeTable, listing the RuntimeOptions.Phases names in order. Reader accepted v5 files
    ///     transparently — RFC 07 fields default to empty arrays and PhasesTable was treated as absent.
    /// v7 (current): rich static-structure tables follow PhasesTable so offline analysis (Workbench schema panels
    ///     against trace sessions) has the same data a live engine offers — component definitions with full field
    ///     layout, archetype definitions with parent/child + slot map + cluster info, index catalog, runtime config,
    ///     event-queue catalog, and a resource-graph snapshot. v6 readers simply lacked the data; rather than
    ///     synthesising empty defaults (which would silently render "no schema" for old traces), the reader now
    ///     hard-rejects v6 — re-record against a v7-aware build. See the section writers in <see cref="TraceFileWriter"/>
    ///     and the matching reader methods in <see cref="TraceFileReader"/>.
    /// </summary>
    public const ushort CurrentVersion = 7;
}
