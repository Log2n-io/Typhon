using System.Runtime.InteropServices;

namespace Typhon.Profiler;

// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Cache schema v12 — per-system + per-queue + post-tick rollup sections.
//
// Drives the Workbench Data API v2 tracks (`system/<name>`, `queue/<name>`, `posttick/*`) per `claude/design/Apps/Workbench/10-internal-data-api.md §7.3`.
// Folded by `IncrementalCacheBuilder` from existing wire events plus the new `QueueTickEnd` event for the per-queue path.
//
// All structs are [Sequential, Pack=1] and 8-byte-aligned-on-disk. Field ordering chosen so longs/doubles sit on natural alignment (no internal
// padding holes) and the trailing pad bytes round each record up to 8.
// ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-(tick, system) rollup row in <see cref="CacheSectionId.SystemTickSummaries"/>. One row per system per tick — dense across systems so binary-search by
/// tick yields a contiguous slice of all systems for that tick. Skipped systems are present with <see cref="SkipReasonCode"/> != 0; consumers filter on that.
/// </summary>
/// <remarks>
/// Folded from the existing wire events:
/// <list type="bullet">
///   <item><c>SystemReady(systemIdx, predecessorCount, ts)</c> → <see cref="ReadyUs"/>.</item>
///   <item><c>SystemSkipped(systemIdx, skipReason, ts, ...)</c> → <see cref="SkipReasonCode"/> + <see cref="StartUs"/>=0.</item>
///   <item><c>SchedulerChunk(systemIdx, chunkIdx, totalChunks, startTs, endTs, entitiesProcessed)</c> → <see cref="StartUs"/>
///       (min over chunks), <see cref="EndUs"/> (max over chunks), <see cref="DurationUs"/> (end - start), <see cref="EntitiesProcessed"/>
///       (sum), <see cref="WorkersTouched"/> (distinct thread slots), <see cref="ChunksProcessed"/> (count).</item>
/// </list>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemTickSummary
{
    /// <summary>Tick number (matches <see cref="TickSummary.TickNumber"/>).</summary>
    public uint TickNumber;

    /// <summary>System index in the DAG (matches <see cref="SystemDefinitionRecord.Index"/>).</summary>
    public ushort SystemIndex;

    /// <summary>
    /// Skip reason byte (mirrors engine <c>SkipReason</c> enum). 0 = NotSkipped (system ran), non-zero = skipped this tick;
    /// other fields then reflect the skip event (StartUs/EndUs zero, DurationUs zero, EntitiesProcessed zero).
    /// </summary>
    public byte SkipReasonCode;

    /// <summary>Reserved for future use (e.g. bit flags for fresh/snapshot run, change-filter triggered, etc.). Zero on disk.</summary>
    public byte Flags;

    /// <summary>Tick-relative µs at which the first chunk grab happened (or callback start). Zero if skipped.</summary>
    public double StartUs;

    /// <summary>Tick-relative µs at which the last chunk completed (or callback finish). Zero if skipped.</summary>
    public double EndUs;

    /// <summary>
    /// Tick-relative µs at which the system became ready (all predecessors done). Zero if skipped or unobserved (e.g. a root
    /// system whose ready time coincides with TickStart). Used by the DAG view's worker-claim-wait diagnostic.
    /// </summary>
    public double ReadyUs;

    /// <summary>
    /// Wall-clock execution duration in µs (<c>EndUs - StartUs</c>). Zero if skipped. This is the elapsed real-time of the system span,
    /// independent of how many workers ran chunks in parallel. For latency / critical-path analysis. Pair with <see cref="TotalCpuUs"/>
    /// to compute parallelization efficiency: <c>TotalCpuUs / (DurationUs * WorkersTouched)</c>.
    /// </summary>
    public float DurationUs;

    /// <summary>Number of entities processed (sum across chunks). Zero for callbacks / skipped systems.</summary>
    public uint EntitiesProcessed;

    /// <summary>Number of distinct worker threads that ran chunks for this system this tick. Zero if skipped.</summary>
    public byte WorkersTouched;

    /// <summary>Number of chunks completed (1 for callbacks; >=1 for parallel queries / pipelines).</summary>
    public ushort ChunksProcessed;

    /// <summary>Padding to keep <see cref="TotalCpuUs"/> on a 4-byte boundary. Zero on disk.</summary>
    public byte _reserved;

    /// <summary>
    /// Total CPU time in µs consumed by this system across ALL workers — sum of every <c>SchedulerChunk</c> span's duration. Distinct from
    /// <see cref="DurationUs"/>: a parallel system using 16 workers for 690 µs of wall-clock has <c>DurationUs = 690</c> but
    /// <c>TotalCpuUs ≈ 16 * chunk_avg ≈ 5,700</c>. Drives parallelism-inefficiency / utilization calculations (workbench A1/A2). Zero if skipped.
    /// Chunker v13+ field; v12 caches default to zero on read (auto-rebuild expected).
    /// </summary>
    public uint TotalCpuUs;
}

/// <summary>
/// Per-(tick, queue) rollup row in <see cref="CacheSectionId.QueueTickSummaries"/>. One row per queue per tick. Folded from the new <c>QueueTickEnd</c> wire
/// event the engine emits at end-of-tick per active event queue.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct QueueTickSummary
{
    /// <summary>Tick number.</summary>
    public uint TickNumber;

    /// <summary>Queue identifier — index into the <see cref="CacheSectionId.QueueNameTable"/> for display-name resolution.</summary>
    public ushort QueueId;

    /// <summary>Padding to align <see cref="PeakDepth"/> on a 4-byte boundary.</summary>
    public ushort _reserved;

    /// <summary>Maximum number of items present in the queue at any point during the tick (after <c>Enqueue</c>, before <c>Drain</c>).</summary>
    public uint PeakDepth;

    /// <summary>Item count remaining at end-of-tick (after <c>Drain</c>). Sustained &gt;0 means consumer is chronically lagging.</summary>
    public uint EndOfTickDepth;

    /// <summary>Number of <c>Push</c> calls that were rejected because the queue was at capacity (overflow).</summary>
    public uint OverflowCount;

    /// <summary>Number of items successfully enqueued during the tick.</summary>
    public uint Produced;

    /// <summary>Number of items drained (consumed) during the tick.</summary>
    public uint Consumed;
}

/// <summary>
/// Per-(tick, system, archetype) entity-touch rollup row in <see cref="CacheSectionId.SystemArchetypeTouches"/>. One row per active
/// (system, archetype) pair per tick — sparse by definition (most systems target one archetype, callbacks emit no rows). Folded from
/// the new <c>SchedulerSystemArchetype</c> wire event the engine emits at parallel-query completion. Sorted by (TickNumber, SystemIndex,
/// ArchetypeId) for binary-search range scans. Wire size 16 bytes — packs tightly so a 100k-tick / 200-system trace at one row per
/// (system, tick) lands at ~320 MB worst case (typical: ~30-50 MB after sparsity).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SystemArchetypeTouchSummary
{
    /// <summary>Tick number.</summary>
    public uint TickNumber;

    /// <summary>System index (matches <see cref="SystemTickSummary.SystemIndex"/>).</summary>
    public ushort SystemIndex;

    /// <summary>Archetype id (matches <c>ArchetypeRecord.ArchetypeId</c>; 0–4095 per the 12-bit allocation).</summary>
    public ushort ArchetypeId;

    /// <summary>Entities the system processed for this archetype during this tick.</summary>
    public uint EntityCount;

    /// <summary>Chunks dispatched for the parallel-query bracket (sum across workers).</summary>
    public uint ChunkCount;
}

/// <summary>
/// Per-tick post-tick serial markers in <see cref="CacheSectionId.PostTickSummaries"/>. One row per tick, capturing the duration of each <see cref="TickPhase"/>
/// region that runs after the system DAG completes — wraps the existing <c>InspectorPhase</c> blocks in <c>TyphonRuntime.OnTickEndInternal</c>. Zero µs means
/// the phase ran with no measurable work (e.g. no subscriptions active for <see cref="SubscriptionOutputUs"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PostTickSummary
{
    /// <summary>Tick number.</summary>
    public uint TickNumber;

    /// <summary>Padding for 8-byte alignment of the following floats. Zero on disk.</summary>
    public uint _reserved;

    /// <summary>Duration in µs of <see cref="TickPhase.WriteTickFence"/>. Cluster migrations + WAL publish + spatial index update.</summary>
    public float WriteTickFenceUs;

    /// <summary>Duration in µs of <see cref="TickPhase.UowFlush"/>. Includes <c>WaitForDurable</c> on the WAL fsync — typically the largest "where did my tick go" surprise.</summary>
    public float WalFlushUs;

    /// <summary>Duration in µs of <see cref="TickPhase.OutputPhase"/>. Refresh published Views, compute deltas, push via TCP.</summary>
    public float SubscriptionOutputUs;

    /// <summary>Duration in µs of <see cref="TickPhase.TierIndexRebuild"/>. Rebuild per-archetype tier cluster indexes at tick start (rolled into post-tick for now per design §10).</summary>
    public float TierIndexRebuildUs;

    /// <summary>Duration in µs of <see cref="TickPhase.DormancySweep"/>. Advance sleep counters, transition idle clusters.</summary>
    public float DormancySweepUs;

    /// <summary>Reserved for the per-tier budget metrics phase (not yet wrapped in <c>InspectorPhase</c>; tracks at zero until the engine wraps it). Design §5.3.</summary>
    public float TierBudgetUs;
}

/// <summary>Which side of an entity's life an <see cref="EntityLifecycleRun"/> records.</summary>
public enum EntityLifecycleKind : byte
{
    /// <summary>Entities came into existence. <see cref="EntityLifecycleRun.Count"/> may exceed 1 (a batch spawn).</summary>
    Spawn = 0,

    /// <summary>An entity was destroyed. <see cref="EntityLifecycleRun.Count"/> is always 1 — destroys are recorded per entity.</summary>
    Destroy = 1,
}

/// <summary>
/// One spawn or destroy **run** in <see cref="CacheSectionId.EntityLifecycle"/> — the trace-side index behind the Workbench entity lens (#620,
/// design §4.4). Sorted by <c>(TickNumber, FirstEntityKey)</c> so a tick range resolves by binary search.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run, not a row per entity.</b> <c>Transaction.SpawnBatch</c> reserves all its keys with one <c>Interlocked.Add</c> and stamps one routing id,
/// so a batch's ids are exactly <c>(FirstEntityKey + n, RoutingId)</c> for <c>n</c> in <c>[0, Count)</c>. The range is therefore an *exact*
/// representation, not a summary, and a 200K bulk load costs one 24-byte row instead of 200K of them. Single spawns and destroys are runs of length 1.
/// </para>
/// <para>
/// <b>Keys, not raw ids.</b> A raw <c>EntityId</c> is <c>(key &lt;&lt; 16) | routingId</c>, so consecutive entities are 65,536 apart in raw value but
/// adjacent in key. Storing the key is what makes the run contiguous.
/// </para>
/// <para>
/// <b>Two archetype identifiers, deliberately.</b> <see cref="RoutingId"/> is the durable per-database id embedded in every entity id;
/// <see cref="ArchetypeId"/>
/// is the per-process catalog id the trace's archetype table is keyed by. They are usually *different numbers for the same archetype* — design §5.3's
/// landmine. Spawns know both. Destroys know only the routing id (the wire event carries just the entity id), and set <see cref="ArchetypeId"/> to
/// <see cref="UnknownArchetypeId"/> rather than guessing: a fabricated catalog id would join to the wrong archetype and look plausible doing it.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EntityLifecycleRun
{
    /// <summary>Sentinel for <see cref="ArchetypeId"/> when the source event did not carry a catalog id (every destroy).</summary>
    public const ushort UnknownArchetypeId = 0xFFFF;

    /// <summary>Tick number the run was recorded in. Zero for pre-tick activity — bulk loads during setup land here, and are kept, not dropped.</summary>
    public uint TickNumber;

    /// <summary>Per-process catalog archetype id (matches <c>ArchetypeRecord.ArchetypeId</c>), or <see cref="UnknownArchetypeId"/> for destroys.</summary>
    public ushort ArchetypeId;

    /// <summary>Durable per-database routing id — the low 16 bits of every entity id in this run, and the one identifier safe to join on directly.</summary>
    public ushort RoutingId;

    /// <summary>First entity key in the run. The n-th entity's raw id is <c>((FirstEntityKey + n) &lt;&lt; 16) | RoutingId</c>.</summary>
    public long FirstEntityKey;

    /// <summary>Number of consecutive keys in the run. Always &gt;= 1.</summary>
    public uint Count;

    /// <summary>Spawn or destroy — see <see cref="EntityLifecycleKind"/>.</summary>
    public byte Kind;

    /// <summary>Reserved; zero on disk. Keeps the record at 24 B and 8-byte aligned.</summary>
    public byte _reserved0;

    /// <summary>Reserved; zero on disk.</summary>
    public ushort _reserved1;
}
