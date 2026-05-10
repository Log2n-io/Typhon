using System;
using System.Runtime.InteropServices;

namespace Typhon.Profiler;

// Binary layout definitions for the `.typhon-trace-cache` sidecar file. The cache is a deterministic function of its source `.typhon-trace`:
// built once on first open, reused on subsequent opens of the same file. Invalidated (and rebuilt) when the source's fingerprint changes.
//
// File-level layout (see also claude/scratch/scalable-profiler-load-design.md §5):
//   [CacheHeader — 128 B fixed]
//   [SectionTable — N × SectionTableEntry]
//   [TickIndex]
//   [TickSummaries]
//   [GlobalMetrics]
//   [ChunkManifest]
//   [FoldedChunkData]   — LZ4-compressed record byte streams, one blob per manifest entry
//   [SpanNameTable]     — optional
//
// The header's SectionTableOffset + Length lets a reader locate any section in O(1) after reading the first 128 bytes. The layout is designed for
// append-only writes during build (sections streamed linearly) with a final rewind-and-patch to finalize header + section-table offsets.

/// <summary>
/// Fixed 128-byte header at the start of a `.typhon-trace-cache` file. Contains the source-file fingerprint (for invalidation), format versioning,
/// and a pointer to the section table.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CacheHeader
{
    /// <summary>File magic: ASCII "TPCH" (0x48_43_50_54 little-endian).</summary>
    public uint Magic;

    /// <summary>Cache format version. Current: 1. Bump on breaking layout changes.</summary>
    public ushort Version;

    /// <summary>
    /// Flag bits. See <see cref="CacheHeaderFlags"/>. Bit 0 (<see cref="CacheHeaderFlags.IsSelfContained"/>) marks a cache that doesn't need a
    /// sibling <c>.typhon-trace</c> source — its metadata tables (header / systems / archetypes / component types) are carried inside the
    /// <see cref="CacheSectionId.SourceMetadata"/> section. Live-attach session saves write self-contained caches; readers that see the flag
    /// skip the source-file open and project metadata from the embedded bytes. Bits 1-15: reserved (must write 0; readers ignore unknown bits).
    /// </summary>
    public ushort Flags;

    /// <summary>
    /// 32-byte identifier for invalidation / cache identity:
    /// <list type="bullet">
    /// <item><b>Source-derived caches</b> (default): SHA-256 of (source mtime-ticks + source length + first 4 KB + last 4 KB). If the source
    /// file changes meaningfully, the fingerprint mismatches and the cache is discarded on next open. Cheap (~1 ms) and collision-resistant.</item>
    /// <item><b>Self-contained caches</b> (<see cref="CacheHeaderFlags.IsSelfContained"/> set): no source file exists. The 32 bytes carry an
    /// arbitrary session-derived identifier (e.g. session GUID + zero-padding); readers must NOT treat the value as a file fingerprint.</item>
    /// </list>
    /// </summary>
    public unsafe fixed byte SourceFingerprint[32];

    /// <summary>
    /// Copy of the source file's <see cref="TraceFileHeader.Version"/>. If the source format revs, caches built against the old format are
    /// automatically invalidated without relying on fingerprint alone.
    /// </summary>
    public ushort SourceVersion;

    /// <summary>
    /// Version tag for the chunker / fold policy. Bump whenever <see cref="TraceFileCacheConstants.TickCap"/>, <see cref="TraceFileCacheConstants.ByteCap"/>,
    /// or the async-completion fold logic changes in a way that makes old caches incorrect. Readers that see a different ChunkerVersion treat the
    /// cache as stale and rebuild.
    /// </summary>
    public ushort ChunkerVersion;

    /// <summary>Offset (in bytes from the start of the cache file) of the section table.</summary>
    public long SectionTableOffset;

    /// <summary>Length in bytes of the section table (== <see cref="SectionTableEntry"/>.SizeInBytes × entry count).</summary>
    public long SectionTableLength;

    /// <summary>UTC timestamp when this cache was built (DateTime.UtcNow.Ticks). Informational; not used for invalidation.</summary>
    public long CreatedUtcTicks;

    /// <summary>Padding to 128 bytes. Zero-initialized; readers must ignore.</summary>
    public unsafe fixed byte Reserved[60];

    public const uint MagicValue = 0x48_43_50_54; // 'T','P','C','H' little-endian
    public const ushort CurrentVersion = 1;

    /// <summary>Byte offset of <see cref="SourceFingerprint"/> from the start of the struct. Single source of truth for callers that
    /// need to patch the field via a span (e.g. the trailer-write path which constructs the header by value and copies the identifier
    /// in via <see cref="System.Runtime.InteropServices.MemoryMarshal"/>). Computed from the field declaration order: Magic (4) +
    /// Version (2) + Flags (2) = 8.</summary>
    public const int SourceFingerprintOffset = 8;

    /// <summary>
    /// Copy the 32-byte <paramref name="identifier"/> into the <see cref="SourceFingerprint"/> slot of <paramref name="header"/>. Used
    /// by trailer-write paths that build the header by value: source-derived close (fingerprint of the source file) and self-contained
    /// save (arbitrary session-derived ID — readers must NOT treat the value as a hash; see <see cref="SourceFingerprint"/> doc).
    /// </summary>
    public static void SetIdentifier(ref CacheHeader header, ReadOnlySpan<byte> identifier)
    {
        if (identifier.Length < 32)
        {
            throw new ArgumentException("Identifier must be at least 32 bytes.", nameof(identifier));
        }
        var headerSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1));
        identifier[..32].CopyTo(headerSpan.Slice(SourceFingerprintOffset, 32));
    }
}

/// <summary>
/// Flag bits for <see cref="CacheHeader.Flags"/>. The struct is 16 bits wide; bit 0 is currently the only assigned flag.
/// </summary>
public static class CacheHeaderFlags
{
    /// <summary>
    /// Set when the cache is self-contained (no <c>.typhon-trace</c> source needed at open time). The
    /// <see cref="CacheSectionId.SourceMetadata"/> section carries the source's header + system / archetype / component-type tables verbatim;
    /// the loader projects metadata from those bytes instead of opening a source file. Used by live-attach session saves.
    /// </summary>
    public const ushort IsSelfContained = 0x0001;
}

/// <summary>
/// One entry in the section table. Identifies a named section by <see cref="SectionId"/> and locates it by byte offset + length within the cache
/// file. Sections are always written contiguously in the file; the table gives random access without requiring the reader to walk section headers.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SectionTableEntry
{
    /// <summary>Section identifier (see <see cref="CacheSectionId"/>).</summary>
    public ushort SectionId;

    /// <summary>Per-section flags (reserved; section-specific).</summary>
    public ushort Flags;

    /// <summary>Padding to align <see cref="Offset"/> on an 8-byte boundary.</summary>
    public uint Padding;

    /// <summary>Byte offset of the section payload from the start of the cache file.</summary>
    public long Offset;

    /// <summary>Byte length of the section payload.</summary>
    public long Length;
}

/// <summary>
/// Identifies each section in the cache file. Values are wire-stable; never renumber or reuse a retired ID — only append.
/// </summary>
public enum CacheSectionId : ushort
{
    /// <summary>Not valid; guards against zero-initialized entries.</summary>
    Invalid = 0,

    /// <summary>Per-tick index (<see cref="TickIndexEntry"/>[]), sorted by tick number. Enables binary search for source-file seeks.</summary>
    TickIndex = 1,

    /// <summary>Per-tick aggregates (<see cref="TickSummary"/>[]). Drives the viewer's overview-timeline render.</summary>
    TickSummaries = 2,

    /// <summary>Global metrics (<see cref="GlobalMetricsFixed"/> + optional per-system aggregates).</summary>
    GlobalMetrics = 3,

    /// <summary>Chunk manifest (<see cref="ChunkManifestEntry"/>[]). Drives the client's cache keying + the server's chunk-serving path.</summary>
    ChunkManifest = 4,

    /// <summary>Concatenated LZ4-compressed chunk payloads. Addressed by <see cref="ChunkManifestEntry.CacheByteOffset"/> + Length.</summary>
    FoldedChunkData = 5,

    /// <summary>Flat copy of the source file's optional span-name intern table. Count prefix (u16) + entries (u16 id, short-string name).</summary>
    SpanNameTable = 6,

    /// <summary>
    /// Verbatim copy of the source's metadata prefix: <see cref="TraceFileHeader"/> + system definitions table + archetypes table +
    /// component-types table, in the same wire format produced by <c>TraceFileWriter</c> (and shipped over TCP as the engine's Init frame
    /// payload during attach). Optional — present iff <see cref="CacheHeaderFlags.IsSelfContained"/> is set on the header. Loaders detect
    /// the flag and project metadata from these bytes via a <c>TraceFileReader</c> over a <see cref="System.IO.MemoryStream"/>, skipping
    /// the source-file open entirely. Source-derived caches (the default) omit this section; their metadata still comes from the parent
    /// <c>.typhon-trace</c> at open time.
    /// </summary>
    SourceMetadata = 7,

    /// <summary>
    /// Per-(tick, system) rollup records (<see cref="SystemTickSummary"/>[]) added in v12 for the Workbench Data API per-system
    /// tracks (RFC 07 surfacing). Sorted by (TickNumber, SystemIndex) for deterministic binary search. Always dense across systems
    /// in a tick (skipped systems present, with `SkipReason != NotSkipped`). Folded by <see cref="IncrementalCacheBuilder"/> from
    /// the existing <c>SystemReady</c> / <c>SystemSkipped</c> / <c>SchedulerChunk</c> wire events — no new wire-format kind needed.
    /// </summary>
    SystemTickSummaries = 8,

    /// <summary>
    /// Per-(tick, queue) rollup records (<see cref="QueueTickSummary"/>[]) added in v12. Sorted by (TickNumber, QueueId). Folded
    /// from a new <c>QueueTickEnd</c> wire event the engine emits at end-of-tick per active event queue.
    /// </summary>
    QueueTickSummaries = 9,

    /// <summary>
    /// Per-tick post-tick serial markers (<see cref="PostTickSummary"/>[]) added in v12 — one record per tick, capturing the durations
    /// of each <see cref="TickPhase"/> region that runs serially after the system DAG completes. Folded from the existing
    /// <c>RuntimePhaseSpan</c> wire events (kind 243); no new wire-format kind needed.
    /// </summary>
    PostTickSummaries = 10,

    /// <summary>
    /// Queue-name intern table written in v12. Variable-length: <c>u16 count</c> followed by <c>count × (u16 queueId + short-string name)</c>.
    /// QueueId is the index assigned at engine startup; readers map <see cref="QueueTickSummary.QueueId"/> → display name through this section.
    /// </summary>
    QueueNameTable = 11,

    /// <summary>
    /// Rich component-type definitions (v14): one record per registered component type, carrying full schema (fields with name +
    /// <c>FieldType</c> + offset + size + index flags + spatial flag), storage mode (Versioned/SingleVersion/Transient), revision,
    /// and per-component aggregates (storage size, indices count, multiple-indices count). Forwarded verbatim from the source
    /// <c>.typhon-trace</c>'s <c>ComponentDefinitionsTable</c> (v7+). Drives the Workbench <c>SchemaBrowser</c> and per-component
    /// detail panels for trace sessions — the existing thin <see cref="CacheSectionId.SourceMetadata"/>'s ComponentTypeTable carries
    /// only id→name pairs and isn't enough.
    /// </summary>
    ComponentDefinitions = 12,

    /// <summary>
    /// Rich archetype definitions (v14): one record per archetype with parent/child links, slot-ordered ComponentTypeIds,
    /// versioned/transient slot bitmasks, cascade-delete targets, cluster eligibility flags, and (when cluster-eligible) inline
    /// <c>ArchetypeClusterInfo</c> describing on-disk SoA layout. Drives the Workbench <c>ArchetypeBrowser</c> + relationship view.
    /// </summary>
    ArchetypeDefinitions = 13,

    /// <summary>
    /// Index catalog (v14): flat list keyed by (ComponentTypeId, FieldId) of every B+Tree index defined on the schema. Each entry
    /// carries the variant byte (Single/Multiple × value-type), a key-type byte, and the spatial / allow-multiple flags. Drives the
    /// <c>SchemaIndexes</c> panel; redundant with the per-field flags in <see cref="ComponentDefinitions"/> but flat-listed here for
    /// O(1) lookup independent of component selection.
    /// </summary>
    IndexCatalog = 14,

    /// <summary>
    /// Engine runtime configuration snapshot at trace start (v14): BaseTickRate, WorkerCount, TelemetryRingCapacity,
    /// ParallelQueryMinChunkSize, DefaultPhase name, and the ordered phase-name list from <c>RuntimeOptions</c>. Single record (no
    /// count prefix). Phase names duplicate the trace's <see cref="CacheSectionId.PostTickSummaries"/> phase axis but include the
    /// definition order — reader exposes both axes side-by-side in the runtime-config panel.
    /// </summary>
    RuntimeConfig = 15,

    /// <summary>
    /// Event-queue catalog (v14): for each registered queue, QueueIndex (matches <see cref="QueueTickSummary.QueueId"/>), display
    /// name (mirrors <see cref="QueueNameTable"/>), capacity (power-of-2), and event type's CLR name. Adds the static schema
    /// (capacity / event-type) on top of the existing name table so the queue panel can show capacity utilisation in % terms
    /// against per-tick depth from <see cref="QueueTickSummaries"/>.
    /// </summary>
    EventQueueCatalog = 16,

    /// <summary>
    /// Resource graph snapshot (v14): pre-order tree walk of the <c>ResourceGraph</c> at trace start — node id, name, type byte,
    /// parent id (-1 for root), creation timestamp, and exhaustion-policy byte. Static (resource topology doesn't change at runtime
    /// in any way the trace cares about). Drives the resource-tree panel for trace sessions.
    /// </summary>
    ResourceGraphSnapshot = 17,

    /// <summary>
    /// Per-(tick, system, archetype) entity-touch rollup (<see cref="SystemArchetypeTouchSummary"/>[]) added in v15 for the Workbench
    /// Data Flow module (#327). Folded from the new <c>SchedulerSystemArchetype</c> wire event the engine emits at parallel-query
    /// completion. Sparse — most systems target one archetype, and most ticks emit one row per active system. Sorted by
    /// (TickNumber, SystemIndex, ArchetypeId). Drives the <c>archetype/*</c>, <c>system-archetype/*</c>, and <c>component-family/*</c>
    /// track families surfaced through <c>AggregationService</c>.
    /// </summary>
    SystemArchetypeTouches = 18,
}

/// <summary>
/// One entry in the tick index. Locates a given tick's byte range inside the *source* `.typhon-trace` file, for fast seek-to-tick during cache
/// rebuild or future features (scrub-to-timestamp). Separate from the <see cref="ChunkManifestEntry"/> which addresses the *cache* file.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TickIndexEntry
{
    /// <summary>Offset in the source file where this tick's events begin. First field so the 8-byte value is 8-aligned within an array.</summary>
    public long ByteOffsetInSource;

    /// <summary>Tick number (1-based per the source decoder's counter).</summary>
    public uint TickNumber;

    /// <summary>Length in bytes of this tick's event stream in the source (spans one or more compressed blocks in practice).</summary>
    public uint ByteLengthInSource;

    /// <summary>Number of events in this tick (after fold — completion records collapsed into their kickoffs).</summary>
    public uint EventCount;

    /// <summary>Padding to keep the struct 8-byte-aligned (sizeof == 24).</summary>
    public uint Padding;
}

/// <summary>
/// Per-tick rollup shipped to the client as the overview feed. Small enough that all ticks for a 500K-tick trace fit in ~16 MB (40 B × 400K),
/// so the client fetches the entire summary on open and renders the timeline from it without any chunk loads.
/// </summary>
/// <remarks>
/// <b>Wire size 40 bytes</b> (was 32 before chunker v9). Bumped in v9 (issue #289 follow-up) to surface per-tick OverloadDetector
/// decisions + metronome wait diagnostics. Old v8 caches don't carry these fields and must be rebuilt; <see cref="TraceFileCacheConstants.CurrentChunkerVersion"/>
/// gates the upgrade.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TickSummary
{
    /// <summary>Tick number.</summary>
    public uint TickNumber;

    /// <summary>Total wall-clock duration of the tick in microseconds.</summary>
    public float DurationUs;

    /// <summary>Event count for this tick.</summary>
    public uint EventCount;

    /// <summary>Longest single-system duration observed in this tick (µs). Drives the color-scale normalization on the timeline.</summary>
    public float MaxSystemDurationUs;

    /// <summary>Bitmask of system indices that ran in this tick. Bit N set iff system index N had any activity. Caps at 64 systems; systems beyond
    /// index 63 don't set bits (overview still accurate for count/duration; bitmask is just a rough "did this system run" indicator).</summary>
    public ulong ActiveSystemsBitmask;

    /// <summary>
    /// Absolute start timestamp of this tick in microseconds (relative to the same origin as <see cref="GlobalMetricsFixed.GlobalStartUs"/>).
    /// Added in chunker v2 so the viewer can map viewRange-in-µs back to a tickNumber range without reading any chunk payload. Ticks with idle
    /// gaps between them (duration &lt; scheduler period) are handled correctly — this is the true wall-clock start, not a cumulative sum of
    /// durations.
    /// </summary>
    public double StartUs;

    // ── v9 fields (issue #289 follow-up) ──────────────────────────────────────────

    /// <summary>OverloadDetector level at the end of this tick (0=Normal, 1/2=Level1/2, 3=TickRateModulation, 4=PlayerShedding). From <c>TickEnd</c> payload.</summary>
    public byte OverloadLevel;

    /// <summary>Effective tick-rate multiplier for this tick (chain values: 1, 2, 3, 4, 6). multiplier &gt; 1 means engine voluntarily slowed itself.</summary>
    public byte TickMultiplier;

    /// <summary>
    /// Duration of the metronome wait that <i>preceded</i> this tick, saturating at <see cref="ushort.MaxValue"/> µs (≈65 ms — well past any realistic gap).
    /// Captured by observing the <c>SchedulerMetronomeWait</c> span (kind 241) that ended just before this tick's <c>TickStart</c>. Zero for tick 0.
    /// </summary>
    public ushort MetronomeWaitUs;

    /// <summary>Intent classification of the preceding metronome wait — 0 = CatchUp (target already past, no real wait), 1 = Throttled (mult&gt;1), 2 = Headroom (normal idle).</summary>
    public byte MetronomeIntentClass;

    // ── v11 fields (issue #289 follow-up — diagnose stuck-throttle attractor) ─────

    /// <summary>
    /// OverloadDetector's <c>_consecutiveOverrunTicks</c> at end-of-tick — number of consecutive ticks above
    /// <c>OverrunThreshold</c> (1.2× by default). Saturates at <see cref="ushort.MaxValue"/>. Captured from
    /// the <see cref="TraceEventKind.SchedulerOverloadDetector"/> instant (kind 242). v11+, zero on older.
    /// </summary>
    public ushort ConsecutiveOverrun;

    /// <summary>
    /// OverloadDetector's <c>_consecutiveUnderrunTicks</c> at end-of-tick — number of consecutive ticks
    /// below <c>DeescalationRatio</c> (0.6× by default). Climbs toward <c>DeescalationTicks</c> (20 default)
    /// before deescalation fires; resets on any overrun tick. Saturates at <see cref="ushort.MaxValue"/>.
    /// </summary>
    public ushort ConsecutiveUnderrun;

    // 3 bytes of reserved padding to keep the struct 8-byte-aligned (44 B total) and headroom for future v11.x additions
    // without a chunker version bump. Zero on disk.
    public byte _reservedByte;
    public ushort _reservedUshort;
}

/// <summary>
/// One entry in the chunk manifest. Addresses a chunk's folded byte range inside the cache file's FoldedChunkData section. A chunk covers a
/// half-open tick range [FromTick, ToTick). Both endpoints are stored explicitly so the manifest can be consumed in any order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChunkManifestEntry
{
    /// <summary>First tick included in this chunk (inclusive).</summary>
    public uint FromTick;

    /// <summary>First tick NOT included (exclusive). <c>ToTick - FromTick</c> is the chunk's tick count.</summary>
    public uint ToTick;

    /// <summary>Byte offset of the compressed chunk payload within the cache file's FoldedChunkData section (absolute cache-file offset).</summary>
    public long CacheByteOffset;

    /// <summary>Length of the compressed payload (input to LZ4 decompress).</summary>
    public uint CacheByteLength;

    /// <summary>Total number of records in this chunk (after fold).</summary>
    public uint EventCount;

    /// <summary>Uncompressed payload size (output size of LZ4 decompress; needed to pre-size the client's decode buffer).</summary>
    public uint UncompressedBytes;

    /// <summary>
    /// Per-chunk flag bits. Also serves as the 4-byte tail padding keeping the struct 8-byte-aligned (sizeof = 32); the wire size and field
    /// offset match the former <c>Padding</c> field exactly, so v7 caches built with Flags=0 are upward-compatible as "normal, non-continuation"
    /// chunks — the reader just sees zero bits, which is the no-flags-set state.
    /// <para>
    /// <b>Bit 0: <see cref="TraceFileCacheConstants.FlagIsContinuation"/></b> — set when this chunk starts mid-tick (continuation of the tick
    /// whose first events lived in the PREVIOUS chunk). Continuation chunks have NO <c>TickStart</c> record at their head; the decoder must
    /// seed its tick counter to <c>FromTick</c> directly rather than <c>FromTick - 1</c>. See the chunker-version-8 changelog entry for the
    /// reason mid-tick splitting exists.
    /// </para>
    /// <para>
    /// Bits 1-31: reserved for future per-chunk flags. Builders MUST write zero for reserved bits; readers MUST ignore unknown bits so that
    /// a future v8+ feature that sets a new bit doesn't break older readers on the same version.
    /// </para>
    /// </summary>
    public uint Flags;
}

/// <summary>
/// Fixed-size global metrics header. Followed in-section by a variable-length tail of per-system aggregates
/// (<see cref="SystemAggregateDuration"/>[]), with count == <see cref="SystemAggregateCount"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GlobalMetricsFixed
{
    public double GlobalStartUs;
    public double GlobalEndUs;
    public double MaxTickDurationUs;
    public double MaxSystemDurationUs;
    public double P95TickDurationUs;
    public long TotalEvents;
    public uint TotalTicks;
    public uint SystemAggregateCount;
}

/// <summary>
/// Aggregate duration for one system across the whole trace. Written after <see cref="GlobalMetricsFixed"/> in the GlobalMetrics section.
/// Used by the viewer to color-rank systems in the legend or to compute "which system dominates the trace" queries without loading any chunks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemAggregateDuration
{
    public ushort SystemIndex;
    public ushort Padding;
    public uint InvocationCount;
    public double TotalDurationUs;
}

/// <summary>
/// Wire envelope shipped at the start of each HTTP chunk response (uncompressed prefix, then the LZ4 payload). Identifies the chunk for the
/// client's cache key without requiring a LZ4 decode first.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ChunkWireHeader
{
    public uint Magic;
    public uint FromTick;
    public uint ToTick;
    public uint RecordCount;

    public const uint MagicValue = 0x4B_43_50_54; // 'T','P','C','K' little-endian
}

/// <summary>
/// Compile-time constants for the cache format. Kept in one place so writer, reader, builder, and tests all agree.
/// </summary>
public static class TraceFileCacheConstants
{
    /// <summary>Maximum ticks per chunk — upper bound on how coarse a chunk can be in tick count.</summary>
    public const int TickCap = 100;

    /// <summary>Maximum uncompressed bytes per chunk — upper bound on how big a chunk can be in payload size.</summary>
    public const int ByteCap = 1 * 1024 * 1024;

    /// <summary>
    /// Maximum events per chunk — closes a chunk at the next tick boundary when the running event count reaches this threshold.
    /// Complements <see cref="ByteCap"/> for regions where records are small and numerous (dense allocation bursts, high-frequency
    /// scheduler chunks, etc.): byte-cap alone could let tens of thousands of small records pile into one chunk that decodes slowly
    /// and dominates the client-side LRU budget. Splitting on event count instead caps the per-chunk decode cost at a bounded
    /// number of record decodes, regardless of their compressed byte ratio.
    ///
    /// 50 000 chosen empirically: at ~300 bytes average decoded per event (span + alloc mix), this is ~15 MB of resident heap per
    /// chunk — comfortable against the client's 500 MB LRU budget (30+ chunks headroom) and decodes in roughly 100-200 ms on a modern
    /// CPU. For ticks that fit under this cap, they're emitted as single whole chunks — no intra-tick splitting, no client-side merge.
    /// </summary>
    public const int EventCap = 50_000;

    /// <summary>
    /// Mid-tick byte cap. A SINGLE tick's accumulated record bytes exceeding this trigger the builder to close the current chunk
    /// in the middle of the tick and start a new continuation chunk (marked with <see cref="FlagIsContinuation"/>). Deliberately
    /// larger than <see cref="ByteCap"/> (2×) so that well-sized ticks never trip it — only genuinely pathological single ticks
    /// (e.g., >1 MiB of records in one tick) get split, keeping the client-side <c>mergeTickData</c> path cold for the common case.
    /// </summary>
    public const int IntraTickByteCap = 2 * ByteCap;

    /// <summary>
    /// Mid-tick event cap. Parallels <see cref="IntraTickByteCap"/> but counts records instead of bytes. A single tick whose event
    /// count hits this value triggers a mid-tick chunk close. 2× <see cref="EventCap"/> (100 000) so that a tick marginally over the
    /// normal event cap still emits as a single chunk — only genuinely pathological dense ticks split. Tuned independently of
    /// <see cref="EventCap"/> — they're separate dials so we can tighten one without affecting the other as workload data arrives.
    /// </summary>
    public const int IntraTickEventCap = 2 * EventCap;

    /// <summary>Bit 0 of <see cref="ChunkManifestEntry.Flags"/> — chunk starts mid-tick (continuation of the previous chunk's last tick).</summary>
    public const uint FlagIsContinuation = 0x1;

    /// <summary>
    /// Current chunker policy version. Incremented when <see cref="TickCap"/>, <see cref="ByteCap"/>, or the fold logic changes in a way that
    /// invalidates existing caches. Readers that see a different value must rebuild the cache.
    /// v2: added <c>TickSummary.StartUs</c>.
    /// v3: server-side async-completion fold — kickoff records carry the full async duration; completion records dropped from the stream.
    /// v4: tick duration computed from TickStart→TickEnd wall time only (no span-endTs extension) so folded kickoffs whose end extends past
    ///     TickEnd don't bloat the summary and cause adjacent ticks to appear overlapping in the viewer's selection math.
    /// v5: also stopped extending lastTs inside the fold path itself — there was a second duration-extension site that v4 missed. With this,
    ///     tick durations are purely wall-clock TickStart→TickEnd, regardless of whether fold fires within the chunk.
    /// v6: pre-first-tick events (MemoryAllocEvent, GcStart, GcEnd, GcSuspension) are buffered and prepended to the first chunk's byte
    ///     stream instead of being silently dropped. Old caches built with v5 are missing engine-startup memory events that land before
    ///     the first TickStart — readers that see v5 must rebuild against a v6 builder to surface them.
    /// v7: added <c>TraceEventKind.ThreadInfo</c> (kind 77). Emitted at slot claim — typically pre-first-tick — and added to the pre-tick
    ///     buffer path. Old v6 caches don't surface thread names; readers must rebuild against v7 to populate lane labels.
    /// v8: two combined changes, both invalidating prior caches:
    ///     (a) <see cref="EventCap"/> as a tick-boundary chunk-close trigger — shrinks the worst-case per-chunk decode cost in dense
    ///         multi-tick regions that previously squeaked under <see cref="ByteCap"/> because of small record sizes.
    ///     (b) Intra-tick splitting — a single pathologically dense tick (e.g., 2 M events in one tick) can now be split across multiple
    ///         chunks via <see cref="IntraTickByteCap"/> / <see cref="IntraTickEventCap"/>. Continuation chunks are marked with
    ///         <see cref="FlagIsContinuation"/>. The decoder must seed its tick counter to FromTick directly for continuation chunks
    ///         (vs FromTick - 1 for normal chunks). The former <c>ChunkManifestEntry.Padding</c> u32 is now <c>Flags</c>; offset and
    ///         size are unchanged, so v7-on-disk entries read back with Flags=0 which correctly means "normal, non-continuation."
    /// v9: <see cref="TickSummary"/> grew from 32 B to 40 B with four new fields — <c>OverloadLevel</c>, <c>TickMultiplier</c>,
    ///     <c>MetronomeWaitUs</c>, <c>MetronomeIntentClass</c> — captured by the builder from <c>TickEnd</c> payload (overload byte +
    ///     multiplier byte) and from observed <c>SchedulerMetronomeWait</c> (kind 241) spans. v8 caches don't carry these and must
    ///     rebuild against a v9 builder. Issue #289 follow-up: makes per-tick throttle decisions and metronome idle visible to the viewer.
    /// v10: same on-disk layout as v9, but v9 caches were built while <c>InspectorTickEnd</c> still hardcoded
    ///      <c>(overloadLevel: 0, tickMultiplier: 1)</c> on the wire — meaning every v9 <see cref="TickSummary"/> on disk has zeroed
    ///      throttle fields regardless of actual engine state. Bumping the version forces those caches to rebuild from source so
    ///      the new TickEnd payload (with real values) is captured. No struct shape change — readers built against v10 should be
    ///      bit-compatible with v9 wire format if the source happened to be re-traced post-fix, but the version bump removes any
    ///      ambiguity for users who still hold v9 sidecars from earlier in the same dev cycle.
    /// v11: <see cref="TickSummary"/> grew from 40 B to 44 B with two new fields — <c>ConsecutiveOverrun</c> + <c>ConsecutiveUnderrun</c>
    ///      (the OverloadDetector's per-tick streak counters, saturating u16). Captured by the builder from the
    ///      <c>SchedulerOverloadDetector</c> instant (kind 242). Drives the Workbench OverloadStrip tooltip so users can see the
    ///      deescalation streak climb toward 20 (or reset on any overrun) — the answer to "why didn't multiplier go down?".
    ///      v10 caches must rebuild against v11.
    /// v12 (current): four new sections added (<see cref="CacheSectionId.SystemTickSummaries"/>, <see cref="CacheSectionId.QueueTickSummaries"/>,
    ///      <see cref="CacheSectionId.PostTickSummaries"/>, <see cref="CacheSectionId.QueueNameTable"/>) populated by
    ///      <see cref="IncrementalCacheBuilder"/> from existing wire events plus a new <c>QueueTickEnd</c> event for the per-queue path.
    ///      Drives the Workbench Data API v2 tracks (#311) — <c>system/&lt;name&gt;</c>, <c>queue/&lt;name&gt;</c>, <c>posttick/*</c>.
    ///      <see cref="TickSummary.ActiveSystemsBitmask"/> is no longer populated in v12 builds (zeroed) — the per-system rows in
    ///      <see cref="CacheSectionId.SystemTickSummaries"/> are the authoritative answer to "which systems ran in tick T", with no
    ///      u64 cap. The field is retained on disk for v11 reader back-compat but consumers should migrate. v11 caches must rebuild.
    /// </summary>
    /// <remarks>
    /// v13: <see cref="SystemTickSummary"/> grew a <c>TotalCpuUs</c> field — total CPU time consumed across all workers (sum of chunk durations),
    /// distinct from <c>DurationUs</c> (wall-clock). Enables correct parallelism-inefficiency math in the workbench (A1/A2 in `09-system-dag.md`)
    /// without requiring per-chunk decode. v12 caches must rebuild.
    /// v14: six new sections forwarded from the source's v7 static-structure tables —
    /// <see cref="CacheSectionId.ComponentDefinitions"/>, <see cref="CacheSectionId.ArchetypeDefinitions"/>,
    /// <see cref="CacheSectionId.IndexCatalog"/>, <see cref="CacheSectionId.RuntimeConfig"/>,
    /// <see cref="CacheSectionId.EventQueueCatalog"/>, <see cref="CacheSectionId.ResourceGraphSnapshot"/>. Drives the Workbench schema
    /// panels for trace sessions (SchemaBrowser, ArchetypeBrowser, SchemaIndexes, et al.). v13 caches must rebuild — and since the source
    /// also bumped to v7, the source itself must be re-recorded; v6 source files are hard-rejected by the reader.
    /// v15: one new section <see cref="CacheSectionId.SystemArchetypeTouches"/> folded from the new
    /// <c>SchedulerSystemArchetype</c> wire event (kind 245). Drives the <c>archetype/*</c>, <c>system-archetype/*</c>, and
    /// <c>component-family/*</c> track families in the Workbench Data Flow module (#327). v14 caches must rebuild.
    /// v16 (current, 2026-05-10): no schema change, but <see cref="TraceEventKind.NamedSpan"/> reassigned from value 200 to 246
    /// (was colliding with <see cref="TraceEventKind.EcsQueryMaskAnd"/>). Cached records reference the kind ID directly, so
    /// v15 caches must rebuild to re-emit NamedSpan records under the new ID. v15 caches load against v8 source files would
    /// mis-decode kind=200 records; bump enforces rebuild.
    /// </remarks>
    public const ushort CurrentChunkerVersion = 16;

    /// <summary>Sidecar file extension, appended to the source path (e.g., <c>foo.typhon-trace</c> → <c>foo.typhon-trace-cache</c>).</summary>
    public const string CacheFileExtension = "-cache";

    /// <summary>Size of the prefix + suffix regions read from the source file to feed the fingerprint hash. 4 KB each side.</summary>
    public const int FingerprintEdgeBytes = 4 * 1024;
}
