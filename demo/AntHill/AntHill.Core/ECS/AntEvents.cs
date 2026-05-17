namespace AntHill.Core;

/// <summary>
/// Telemetry events emitted by the simulation. Replace the previous <c>Interlocked.Increment</c>
/// counter writes with proper RFC 07 event flow: producers declare <c>WritesEvents</c> on the
/// queue, the consumer (<c>AntStatsAggregator</c>) declares <c>ReadsEvents</c>, and the auto-DAG
/// renders the producer→consumer arrow in the Workbench System DAG view.
/// </summary>
public readonly struct AntDiedEvent
{
    public readonly uint EntityId;
    public readonly int NestIdx;
    public AntDiedEvent(uint entityId, int nestIdx) { EntityId = entityId; NestIdx = nestIdx; }
}

public readonly struct FoodPickedUpEvent
{
    public readonly uint EntityId;
    public readonly int FoodIdx;
    public FoodPickedUpEvent(uint entityId, int foodIdx) { EntityId = entityId; FoodIdx = foodIdx; }
}

public readonly struct FoodDeliveredEvent
{
    public readonly uint EntityId;
    public readonly int NestIdx;
    public readonly int Amount;
    public FoodDeliveredEvent(uint entityId, int nestIdx, int amount) { EntityId = entityId; NestIdx = nestIdx; Amount = amount; }
}
