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
[TraceEvent(TraceEventKind.EcsSpawn, Codec = typeof(EcsSpawnEventCodec), EmitEncoder = true)]
internal ref partial struct EcsSpawnEvent
{
    [BeginParam]
    public ushort ArchetypeId;

    [Optional]
    private ulong _entityId;
    [Optional]
    private long _tsn;
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS Destroy
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsDestroy"/>. Required: entity ID. Optional: cascade count, TSN.
/// </summary>
[TraceEvent(TraceEventKind.EcsDestroy, Codec = typeof(EcsDestroyEventCodec), EmitEncoder = true)]
internal ref partial struct EcsDestroyEvent
{
    [BeginParam]
    public ulong EntityId;

    [Optional]
    private int _cascadeCount;
    [Optional]
    private long _tsn;

}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ECS View Refresh
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.EcsViewRefresh"/>. Required: archetype type ID. Optional: mode enum, result count,
/// delta count.
/// </summary>
[TraceEvent(TraceEventKind.EcsViewRefresh, Codec = typeof(EcsViewRefreshEventCodec), EmitEncoder = true)]
internal ref partial struct EcsViewRefreshEvent
{
    [BeginParam]
    public ushort ArchetypeTypeId;

    [Optional]
    private EcsViewRefreshMode _mode;
    [Optional]
    private int _resultCount;
    [Optional]
    private int _deltaCount;

}

