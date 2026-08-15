// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Spawn
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsSpawn"/>. Required at begin: archetype ID. Optional: entity ID (set after
/// <c>SpawnInternal</c> returns), TSN (set once the transaction is known).
/// </summary>
[TraceEvent(TraceEventKind.EcsSpawn, EmitEncoder = true)]
internal ref partial struct EcsSpawnEvent
{
    [BeginParam]
    public ushort ArchetypeId;

    [Optional(MaskValue = 0x01)]
    private ulong _entityId;
    [Optional(MaskValue = 0x02)]
    private long _tsn;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Spawn Batch
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsSpawnBatch"/> — one record for an entire batch spawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>A batch is a range, so it is stored as one.</b> <c>Transaction.SpawnBatch</c> / <c>SpawnBatchAllocate</c> reserve all N entity keys with a single
/// <c>Interlocked.Add</c> and stamp one routing id, so the batch's ids are exactly <c>new EntityId(BaseKey + n, RoutingId)</c> for <c>n</c> in
/// <c>[0, Count)</c>. That makes the range reconstruction exact rather than an approximation, and collapses what would be N × ~56 B of
/// <see cref="TraceEventKind.EcsSpawn"/> records into 24 B.
/// </para>
/// <para>
/// <b>Instant shape, not a span.</b> The consumer wants the cohort, not the batch's duration — which the enclosing transaction span already brackets. Instant
/// emits a direct <c>EmitEcsSpawnBatch(...)</c> with the gate check inlined: no ref-struct materialization, no try/finally, and nothing to place at the far
/// end of a long loop body where an early return could drop it.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.EcsSpawnBatch, Shape = TraceEventShape.Instant)]
internal ref partial struct EcsSpawnBatchEvent
{
    [BeginParam] public ushort ArchetypeId;

    /// <summary>
    /// The archetype's durable per-database routing id, which every id in the range embeds in its low 16 bits. Carried even though it is derivable from any
    /// one id, because a reader rebuilding the range from <see cref="BaseKey"/> has only <see cref="ArchetypeId"/> otherwise — and that is the *catalog* id,
    /// a different number for the same archetype (design §5.3).
    /// </summary>
    [BeginParam] public ushort RoutingId;

    /// <summary>First entity key in the reserved range. Keys, not raw ids — the raw id is <c>(key &lt;&lt; 16) | RoutingId</c>.</summary>
    [BeginParam] public long BaseKey;

    /// <summary>Number of consecutive keys reserved. Always &gt; 0; zero-length batches emit nothing.</summary>
    [BeginParam] public int Count;

    /// <summary>Transaction sequence number the batch was spawned under.</summary>
    [BeginParam] public long Tsn;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Destroy
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsDestroy"/>. Required: entity ID. Optional: cascade count, TSN.
/// </summary>
[TraceEvent(TraceEventKind.EcsDestroy, EmitEncoder = true)]
internal ref partial struct EcsDestroyEvent
{
    [BeginParam]
    public ulong EntityId;

    [Optional(MaskValue = 0x01)]
    private int _cascadeCount;
    [Optional(MaskValue = 0x02)]
    private long _tsn;

}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS View Refresh
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsViewRefresh"/>. Required: archetype type ID. Optional: mode enum, result count,
/// delta count.
/// </summary>
[TraceEvent(TraceEventKind.EcsViewRefresh, EmitEncoder = true)]
internal ref partial struct EcsViewRefreshEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional(MaskValue = 0x01)]
    private EcsViewRefreshMode _mode;
    [Optional(MaskValue = 0x02)]
    private int _resultCount;
    [Optional(MaskValue = 0x04)]
    private int _deltaCount;

}

