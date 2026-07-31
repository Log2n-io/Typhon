using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>
/// Bit flags carried in <see cref="TraceFileHeader.Flags"/>.
/// </summary>
[Flags]
public enum TraceHeaderFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>
    /// More than one <c>DatabaseEngine</c> was live at some point during this capture, so the capture interleaves events from two (or more) archetype
    /// routing-id spaces and no single routing id is meaningful for the file. When this is set the archetype table's <see cref="ArchetypeRecord.RoutingId"/>
    /// values have been overwritten with <see cref="ArchetypeRecord.UnknownRoutingId"/> at close — the trace carries less rather than carrying something
    /// plausible and wrong. Correlation degrades to name-based joins, which still work. See design D-9.
    /// </summary>
    MultipleEnginesObserved = 1 << 0,
}

/// <summary>
/// Fixed 64-byte UTF-8 buffer holding <see cref="TraceFileHeader.DatabaseName"/>. Inline (rather than a trailing string section) so the whole header stays a
/// single blittable read — a profiles list must render from headers alone without building a sidecar cache per capture (design D-5).
/// </summary>
[InlineArray(Length)]
public struct TraceDatabaseName
{
    /// <summary>Capacity in bytes. Longer names are truncated on a UTF-8 character boundary when written.</summary>
    public const int Length = 64;

#pragma warning disable IDE0044, CS0169 // the single field IS the inline array's storage; the runtime repeats it Length times
    private byte _element0;
#pragma warning restore IDE0044, CS0169
}

/// <summary>
/// Version-stamped header at the start of a <c>.typhon-trace</c> file. Contains session-wide metadata that lets the viewer decode the record stream
/// that follows. The on-disk size grows with the format version as trailer-offset fields are appended — readers parse it version-conditionally.
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

    /// <summary>Number of tracks in the tracks table (v11+).</summary>
    public ushort TrackCount;

    /// <summary>Number of DAGs in the DAGs table (v11+).</summary>
    public ushort DagCount;

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
    /// (e.g., the source-attribution generator emitted nothing). See claude/design/Profiler/10-profiler-source-attribution.md §4.6.
    /// </summary>
    public long FileTableOffset;

    /// <summary>
    /// Byte offset of the trailing <c>SourceLocationManifest</c> (id → file/line/method/kind table). 0 when absent.
    /// Bound to a non-zero <see cref="FileTableOffset"/> when the trace carries source attribution.
    /// </summary>
    public long SourceLocationManifestOffset;

    /// <summary>
    /// Byte offset of the trailing <c>QuerySourceStringTable</c> (deduped file paths + method names referenced by
    /// Query Definition Export events, #342). 0 when no Query Definition Export sections were written
    /// (e.g., the session emitted no QueryDefinitionDescribe events). v8 traces have this field absent on disk
    /// (the on-disk header pre-v9 ends after <see cref="SourceLocationManifestOffset"/>) and the reader
    /// fills it with 0.
    /// </summary>
    public long QuerySourceStringTableOffset;

    /// <summary>
    /// Byte offset of the trailing <c>QueryDefinitionTable</c> (materialized definition catalog accumulated
    /// from <see cref="TraceEventKind.QueryDefinitionDescribe"/> events, #342). 0 when absent. v8 reader compat:
    /// pre-v9 traces lack this on-disk field; the reader defaults it to 0.
    /// </summary>
    public long QueryDefinitionTableOffset;

    /// <summary>
    /// Byte offset of the trailing <c>CpuSampleSection</c> (interned CPU stack samples captured by the in-process EventPipe sampler, #351). 0 when
    /// absent — no CPU sampling companion, or the parse produced nothing. v9 and earlier traces lack this on-disk field; the reader defaults it to 0.
    /// </summary>
    public long CpuSampleSectionOffset;

    // ── Self-describing capture identity + listing fields (v12, #614) ────────────────────────────────────────────────────────────────────────────────────
    // Everything below is written so a capture can name its own database and render a rich list row with nothing opened. All fixed-size and blittable on
    // purpose: the profiles list reads one header per file and nothing else.

    /// <summary>
    /// Durable identity of the database this capture ran against (<c>DatabaseEngine.DatabaseId</c>), or <see cref="Guid.Empty"/> when the profiler ran with no
    /// engine attached. Survives the bundle being moved, renamed or restored — co-location is not provenance, so the trace records what it saw rather than
    /// relying on where it happens to sit (design D-2 / D-1).
    /// </summary>
    public Guid DatabaseId;

    /// <summary>
    /// The database's bundle name (<c>{name}.typhon</c> without the extension), UTF-8, zero-padded, truncated to <see cref="TraceDatabaseName.Length"/> bytes.
    /// Empty when no engine was attached. Present so a trace opened outside its bundle still says something a human can act on — <see cref="DatabaseId"/> alone
    /// is the identity, but a GUID is not a readable answer to "which database is this?".
    /// </summary>
    public TraceDatabaseName DatabaseName;

    /// <summary>
    /// The engine's next-free TSN sampled when the capture started. With <see cref="TsnMax"/> this bounds the transaction window the capture covers, and is
    /// the left-hand side of the drift readout ("this profile is N transactions behind the database"). 0 when no engine was attached.
    /// </summary>
    /// <remarks>
    /// This is the engine's global TSN counter, not a min over emitted events — a deliberate superset. It costs nothing on the hot path (two reads of an
    /// existing counter) and it is the exact quantity the drift measure compares against the database's persisted <c>NextFreeTSN</c>.
    /// </remarks>
    public long TsnMin;

    /// <summary>The engine's next-free TSN sampled when the capture closed. Patched in at close. 0 when no engine was attached. See <see cref="TsnMin"/>.</summary>
    public long TsnMax;

    /// <summary>Wall-clock length of the capture in <c>Stopwatch</c> ticks (divide by <see cref="TimestampFrequency"/> for seconds). Patched in at close.</summary>
    public long DurationTicks;

    /// <summary>Number of runtime ticks the capture spans. Patched in at close; 0 when the capture ran without a scheduler.</summary>
    public uint TickCount;

    /// <summary>
    /// Order-independent 64-bit digest of the schema the capture ran against — FNV-1a over the ordinal-sorted <c>(name, revision)</c> pairs of every component
    /// and archetype. Equal fingerprints mean the schemas match; unequal means consult the database's <c>SchemaHistoryR1</c> for what actually moved. 0 when no
    /// engine was attached.
    /// </summary>
    public ulong SchemaFingerprint;

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
    /// v7: rich static-structure tables follow PhasesTable so offline analysis (Workbench schema panels
    ///     against trace sessions) has the same data a live engine offers — component definitions with full field
    ///     layout, archetype definitions with parent/child + slot map + cluster info, index catalog, runtime config,
    ///     event-queue catalog, and a resource-graph snapshot. v6 readers simply lacked the data; rather than
    ///     synthesising empty defaults (which would silently render "no schema" for old traces), the reader now
    ///     hard-rejects v6 — re-record against a v7-aware build. See the section writers in <see cref="TraceFileWriter"/>
    ///     and the matching reader methods in <see cref="TraceFileReader"/>.
    /// v8 (2026-05-10): <see cref="TraceEventKind.NamedSpan"/> reassigned from value 200 to 246 to break a
    ///     latent collision with <see cref="TraceEventKind.EcsQueryMaskAnd"/>. v7 traces with NamedSpan records (kind=200)
    ///     would mis-decode as EcsQueryMaskAnd under a v8 reader; the reader hard-rejects v7 to surface the break loudly.
    ///     Re-record against a v8-aware build.
    /// v9 (2026-05-11): Query Definition Export (#342). Adds two trailing sections:
    ///     <c>QuerySourceStringTable</c> (deduped file paths + method names referenced by query events)
    ///     at offset <see cref="QuerySourceStringTableOffset"/>, and <c>QueryDefinitionTable</c>
    ///     (materialized definitions accumulated from <see cref="TraceEventKind.QueryDefinitionDescribe"/>
    ///     events) at offset <see cref="QueryDefinitionTableOffset"/>. Adds two new instant kinds —
    ///     <see cref="TraceEventKind.QueryDefinitionDescribe"/> (247) and <see cref="TraceEventKind.QueryArgs"/> (248)
    ///     — and extends <see cref="TraceEventKind.QueryPlan"/> with 5 trailing fields
    ///     (QueryInstanceKind, QueryInstanceLocalId, ExecutionSourceFileId, ExecutionSourceLine,
    ///     ExecutionSourceMethodId). v8 traces continue to load: absent sections produce empty catalogs;
    ///     v8 QueryPlan records (without the trailing fields) decode with zero/sentinel defaults for the
    ///     new fields. See claude/design/Profiler/11-query-definition-export.md.
    /// v10 (2026-05-16): CPU Sampling Integration (#351). Adds one trailing section,
    ///     <c>CpuSampleSection</c> (interned CPU stack samples from the in-process EventPipe sampler) at offset
    ///     <see cref="CpuSampleSectionOffset"/>. v9 and earlier traces continue to load — the absent on-disk
    ///     field defaults to 0, which downstream readers interpret as "no CPU samples".
    ///     See claude/design/Profiler/11-cpu-sampling-integration.md.
    /// v11 (2026-05-17): Track→DAG partitioning hierarchy (#354). SystemDefinitionTable records gain a DagId
    ///     field; the global PhasesTable is replaced by a TracksTable + DagsTable (each DAG carries its own
    ///     ordered phase names). RuntimeConfigRecord drops Phases/DefaultPhase. v10-and-older traces are
    ///     hard-rejected (layout-breaking SystemDefinitionTable change).
    /// v12 (2026-07-31): Self-describing captures (#614) — one revision covering four decisions of
    ///     claude/design/Apps/Workbench/10-database-and-profiles.md. <b>D-2</b>: the header gains <see cref="DatabaseId"/>,
    ///     <see cref="DatabaseName"/> and the <see cref="TsnMin"/>/<see cref="TsnMax"/> window, so a capture names the database it ran against instead of
    ///     leaving the pairing to inference. <b>D-3</b>: <see cref="ArchetypeRecord.RoutingId"/> is added to the archetype table, giving every event's
    ///     per-process catalog id a path to the database's durable identity — see the §5.3 warning on <see cref="ArchetypeRecord"/>. <b>D-5</b>:
    ///     <see cref="DurationTicks"/>, <see cref="TickCount"/> and <see cref="SchemaFingerprint"/> join the existing <see cref="CreatedUtcTicks"/> so a list
    ///     of captures renders from headers alone, with no sidecar cache built per row. <b>D-9</b>: <see cref="Flags"/> gains
    ///     <see cref="TraceHeaderFlags.MultipleEnginesObserved"/>. v11-and-older traces are hard-rejected — the ArchetypeTable layout change would otherwise
    ///     mis-decode silently, which is precisely the class of bug this revision exists to close.
    /// </summary>
    public const ushort CurrentVersion = 12;

    /// <summary>True when <see cref="TraceHeaderFlags.MultipleEnginesObserved"/> is set — routing ids in this trace are absent, not merely suspect.</summary>
    public bool MultipleEnginesObserved => ((TraceHeaderFlags)Flags & TraceHeaderFlags.MultipleEnginesObserved) != 0;

    /// <summary>Decodes <see cref="DatabaseName"/> to a string, stopping at the first NUL. Returns an empty string when no name was recorded.</summary>
    public string GetDatabaseName()
    {
        ReadOnlySpan<byte> bytes = DatabaseName;
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    /// <summary>
    /// Encodes <paramref name="name"/> into <see cref="DatabaseName"/>, truncating on a UTF-8 character boundary if it does not fit. Truncating a display name
    /// is harmless; splitting a multi-byte sequence would produce a mojibake name in the profiles list, so the encoder is asked not to.
    /// </summary>
    public void SetDatabaseName(string name)
    {
        Span<byte> dest = DatabaseName;
        dest.Clear();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        Encoding.UTF8.GetEncoder().Convert(name.AsSpan(), dest, flush: true, out _, out _, out _);
    }
}
