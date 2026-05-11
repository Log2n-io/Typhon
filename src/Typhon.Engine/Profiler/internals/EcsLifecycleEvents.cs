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

