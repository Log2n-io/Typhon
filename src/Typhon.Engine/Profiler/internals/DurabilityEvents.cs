// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// ═════════════════════════════════════════════════════════════════════════════
// Phase 8 Durability ref structs (span events only — instants emit via EmitX).
// ═════════════════════════════════════════════════════════════════════════════

// ── WAL spans ──

[TraceEvent(TraceEventKind.DurabilityWalQueueDrain, EmitEncoder = true)]
internal ref partial struct DurabilityWalQueueDrainEvent
{
    [BeginParam]
    public int BytesAligned;
    [BeginParam]
    public int FrameCount;
}

[TraceEvent(TraceEventKind.DurabilityWalOsWrite, EmitEncoder = true)]
internal ref partial struct DurabilityWalOsWriteEvent
{
    [BeginParam]
    public int BytesAligned;
    [BeginParam]
    public int FrameCount;
    [BeginParam]
    public long HighLsn;
}

[TraceEvent(TraceEventKind.DurabilityWalSignal, EmitEncoder = true)]
internal ref partial struct DurabilityWalSignalEvent
{
    [BeginParam]
    public long HighLsn;
}

[TraceEvent(TraceEventKind.DurabilityWalBuffer, EmitEncoder = true)]
internal ref partial struct DurabilityWalBufferEvent
{
    [BeginParam]
    public int BytesAligned;
    [BeginParam]
    public int Pad;
}

[TraceEvent(TraceEventKind.DurabilityWalBackpressure, EmitEncoder = true)]
internal ref partial struct DurabilityWalBackpressureEvent
{
    [BeginParam]
    public uint WaitUs;
    [BeginParam]
    public int ProducerThread;
}

// ── Checkpoint depth spans ──

[TraceEvent(TraceEventKind.DurabilityCheckpointWriteBatch, EmitEncoder = true)]
internal ref partial struct DurabilityCheckpointWriteBatchEvent
{
    [BeginParam]
    public int WriteBatchSize;
    [BeginParam]
    public int StagingAllocated;
}

[TraceEvent(TraceEventKind.DurabilityCheckpointBackpressure, EmitEncoder = true)]
internal ref partial struct DurabilityCheckpointBackpressureEvent
{
    [BeginParam]
    public uint WaitMs;
    [BeginParam]
    public byte Exhausted;
}

[TraceEvent(TraceEventKind.DurabilityCheckpointSleep, EmitEncoder = true)]
internal ref partial struct DurabilityCheckpointSleepEvent
{
    [BeginParam]
    public uint SleepMs;
    [BeginParam]
    public byte WakeReason;
}

// ── Recovery spans ──

[TraceEvent(TraceEventKind.DurabilityRecoveryDiscover, EmitEncoder = true)]
internal ref partial struct DurabilityRecoveryDiscoverEvent
{
    [BeginParam]
    public int SegCount;
    [BeginParam]
    public long TotalBytes;
    [BeginParam]
    public int FirstSegId;
}

[TraceEvent(TraceEventKind.DurabilityRecoverySegment, EmitEncoder = true)]
internal ref partial struct DurabilityRecoverySegmentEvent
{
    [BeginParam]
    public int SegId;
    public int RecCount;
    public long Bytes;
    public byte Truncated;
}

[TraceEvent(TraceEventKind.DurabilityRecoveryFpi, EmitEncoder = true)]
internal ref partial struct DurabilityRecoveryFpiEvent
{
    [BeginParam]
    public int FpiCount;
    public int RepairedCount;
    public int Mismatches;
}

[TraceEvent(TraceEventKind.DurabilityRecoveryRedo, EmitEncoder = true)]
internal ref partial struct DurabilityRecoveryRedoEvent
{
    public int RecordsReplayed;
    public int UowsReplayed;
    public uint DurUs;
}

[TraceEvent(TraceEventKind.DurabilityRecoveryUndo, EmitEncoder = true)]
internal ref partial struct DurabilityRecoveryUndoEvent
{
    [BeginParam]
    public int VoidedUowCount;
}

[TraceEvent(TraceEventKind.DurabilityRecoveryTickFence, EmitEncoder = true)]
internal ref partial struct DurabilityRecoveryTickFenceEvent
{
    public int TickFenceCount;
    public int Entries;
    public long TickNumber;
}
