using Typhon.Engine.Profiler;
using Typhon.Profiler;

namespace Typhon.Engine;

public sealed partial class TyphonRuntime
{
    /// <summary>
    /// Has the first gauge snapshot been emitted this session? Fixed-at-init capacity gauges are emitted only in the first snapshot
    /// — the ref-bool flag flows through <see cref="GaugeSnapshotEmitter.EmitSnapshot"/> so the helper can decide whether to include them.
    /// </summary>
    private bool _firstGaugeSnapshotEmitted;

    /// <summary>
    /// End-of-tick gauge snapshot emitter. Invoked by <c>DagScheduler.GaugeSnapshotCallback</c> on the scheduler thread after
    /// <c>InspectorTickEnd</c> has pushed the <see cref="TraceEventKind.TickEnd"/> marker. Delegates the actual collect+emit work to
    /// <see cref="GaugeSnapshotEmitter"/> so standalone runners (IOProfileRunner, etc.) that don't use <c>TyphonRuntime</c> can
    /// reuse the same logic.
    /// </summary>
    private void EmitGaugeSnapshotFromScheduler(DagScheduler scheduler)
    {
        // MemoryAllocator is exposed through DatabaseEngine as IMemoryAllocator. Downcast to the concrete type to reach the
        // pinned-specific stat accessors (PinnedBytes/PeakPinnedBytes/PinnedLiveBlocks); if a future test swaps in a mock the
        // pattern match fails, the helper receives null, and the three unmanaged gauges simply don't appear in the snapshot.
        var allocator = Engine?.MemoryAllocator as MemoryAllocator;
        var mmf = Engine?.MMF;
        var tx = Engine?.TransactionChain;
        var uow = Engine?.UowRegistry;
        var wal = Engine?.WalManager;
        var staging = Engine?.StagingBufferPool;

        // Aggregated transient-store used bytes across every live TransientStore (component-table + archetype-cluster). Null engine
        // (test harness) contributes 0, which is treated as "skip the gauge" inside the emitter.
        long transientBytesUsed = Engine?.GetTransientBytesTotal() ?? 0L;

        GaugeSnapshotEmitter.EmitSnapshot((uint)scheduler.CurrentTickNumber, allocator, mmf, tx, uow, wal, staging, transientBytesUsed, 
            ref _firstGaugeSnapshotEmitted); }
}
