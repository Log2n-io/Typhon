// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 9 Subscription dispatch ref structs (all spans).
// ═════════════════════════════════════════════════════════════════════════════

[TraceEvent(TraceEventKind.RuntimeSubscriptionSubscriber, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionSubscriberEvent
{
    [BeginParam]
    public uint SubscriberId;
    [BeginParam]
    public ushort ViewId;
    [BeginParam]
    public int DeltaCount;
}

[TraceEvent(TraceEventKind.RuntimeSubscriptionDeltaBuild, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionDeltaBuildEvent
{
    [BeginParam]
    public ushort ViewId;
    public int Added;
    public int Removed;
    public int Modified;
}

[TraceEvent(TraceEventKind.RuntimeSubscriptionDeltaSerialize, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionDeltaSerializeEvent
{
    [BeginParam]
    public uint ClientId;
    [BeginParam]
    public ushort ViewId;
    public int Bytes;
    [BeginParam]
    public byte Format;
}

[TraceEvent(TraceEventKind.RuntimeSubscriptionTransitionBeginSync, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionTransitionBeginSyncEvent
{
    [BeginParam]
    public uint ClientId;
    [BeginParam]
    public ushort ViewId;
    [BeginParam]
    public int EntitySnapshot;
}

[TraceEvent(TraceEventKind.RuntimeSubscriptionOutputCleanup, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionOutputCleanupEvent
{
    public int DeadCount;
    public int DeregCount;
}

[TraceEvent(TraceEventKind.RuntimeSubscriptionDeltaDirtyBitmapSupplement, EmitEncoder = true)]
internal ref partial struct RuntimeSubscriptionDeltaDirtyBitmapSupplementEvent
{
    public int ModifiedFromRing;
    public int SupplementCount;
    public int UnionSize;
}
