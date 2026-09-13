using JetBrains.Annotations;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Non-generic base class for typed event queues. Allows the scheduler to reset all queues at tick start without knowing their generic type.
/// </summary>
/// <remarks>
/// Per-tick telemetry accumulators (peak depth, overflow count, produced/consumed counts) live here so the scheduler can read them without
/// caring about the concrete <see cref="EventQueue{T}"/>. The accumulators are reset at tick start by <see cref="Reset"/>; readers that want the previous
/// tick's data must read before <c>Reset</c> runs (the scheduler reads them in its end-of-tick QueueTickEnd emission, then resets).
/// </remarks>
[PublicAPI]
public abstract class EventQueueBase
{
    /// <summary>Name of this event queue (for diagnostics).</summary>
    public abstract string Name { get; }

    /// <summary>Number of items currently in the queue, summed across every worker slot.</summary>
    public abstract int Count { get; }

    /// <summary>Expected events per tick, as declared at construction — surfaced to the trace's
    /// <see cref="Typhon.Profiler.EventQueueRecord.Capacity"/> for offline analysis (utilisation % against per-tick depth). Each worker segment grows
    /// on demand up to this figure, so a skewed tick can legitimately exceed it; see <c>AllocatedCapacity</c> for what is actually reserved.</summary>
    public abstract int Capacity { get; }

    /// <summary>True if the queue has no items.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Resets the queue to empty. Called at the start of each tick. Also clears the per-tick telemetry accumulators.</summary>
    public abstract void Reset();

    /// <summary>
    /// Sizes the queue's per-worker segments. Called once by <see cref="DagScheduler"/> during construction, when the resolved worker count is first
    /// known (#861). A queue that is never registered with a scheduler stays on its single-slot default.
    /// </summary>
    /// <param name="slotCount"><see cref="DagScheduler.WorkerSlotCount"/> — worker threads plus the dispatcher slot.</param>
    internal abstract void BindWorkerSlots(int slotCount);

    // ─── Per-tick telemetry accumulators (#311) ─────────────────────────────────

    /// <summary>Maximum number of items observed in the queue at any point during the current tick.</summary>
    public abstract uint PeakDepth { get; }

    /// <summary>Number of overflow events during the current tick — each is a <c>Push</c> dropped because the calling worker's segment was at its growth ceiling.</summary>
    public abstract uint OverflowCount { get; }

    /// <summary>Number of <c>Push</c> calls that succeeded during the current tick.</summary>
    public abstract uint Produced { get; }

    /// <summary>Total items returned by <c>Drain</c> calls during the current tick.</summary>
    public uint Consumed { get; protected set; }

    /// <summary>
    /// Stable identifier assigned by the runtime at registration. Used as <see cref="Typhon.Profiler.QueueTickSummary.QueueId"/> and as the index into
    /// the <see cref="Typhon.Profiler.CacheSectionId.QueueNameTable"/>. Set by the runtime when the queue is registered with the scheduler;
    /// 0xFFFF means "unassigned" (queue created outside a scheduler context — telemetry not emitted for it).
    /// </summary>
    public ushort QueueId { get; internal set; } = ushort.MaxValue;
}

/// <summary>
/// Typed event queue for inter-system communication. Producer systems push events; consumer systems drain them.
/// Multi-producer / single-consumer: any number of workers may push concurrently, and the DAG guarantees every producer has completed before the
/// consumer runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-worker segments, no atomics (#861).</b> The queue owns one segment per worker slot, indexed by <see cref="TickContext.WorkerId"/>. A worker
/// only ever touches its own segment, so <c>Push</c> is a bounds-checked store and two plain increments — no <c>Interlocked</c>, no shared cache line.
/// Correctness rests on slot disjointness, which <see cref="DagScheduler.DispatcherWorkerId"/> establishes: no two threads that can run system code
/// concurrently share a slot. Measured against the alternative (one shared tail advanced by <c>Interlocked.Increment</c>), segments are flat with
/// worker count while the shared tail degrades roughly 545× at 8 producers.
/// </para>
/// <para>
/// <b>Ordering.</b> <see cref="Drain"/> returns each slot's events in push order, slots in ascending order. Order ACROSS slots is not meaningful — which
/// worker ran which chunk is not reproducible — so consumers must not depend on inter-event ordering.
/// </para>
/// <para>
/// <b>Capacity is a hint, not a cap.</b> A full segment doubles up to <see cref="Capacity"/>; beyond that a push is dropped and counted in
/// <see cref="EventQueueBase.OverflowCount"/>. Growth happens on the owning worker, replacing only that worker's own buffer, and the high-water allocation survives
/// <see cref="Reset"/> — so a workload reaches its true working set within a few ticks and then never allocates again.
/// </para>
/// <para>
/// <b>Visibility.</b> Segment state is written with plain stores — a release per push is exactly the cost segmentation exists to avoid. The consumer
/// instead issues ONE acquire fence per read (<see cref="AcquireSegments"/>), which is free on x64 (JIT-folded) and an <c>dmb ishld</c> on arm64. That
/// is necessary, not belt-and-braces: the scheduler's completion barrier is decremented with <c>Interlocked</c> but SPUN ON with a plain load, so
/// without a fence here an arm64 reader may sink its segment loads above it and fold stale counts. Do not read a queue from outside system dispatch.
/// </para>
/// </remarks>
/// <typeparam name="T">The event type. No constraints — can be any struct or class.</typeparam>
[PublicAPI]
public sealed class EventQueue<T> : EventQueueBase
{
    /// <summary>Floor on a segment's allocation — below this, growth churns on trivially small queues.</summary>
    private const int MinSegmentCapacity = 16;

    // Hot per-slot scalars, one cache line each. Written only by the slot's owning worker.
    private EventQueueSegmentState[] _slots;

    // Per-slot buffers. The outer array is fixed at bind time and NEVER grown from a worker (rule MD-02); a worker replaces only its own element when
    // its segment doubles, which no other thread reads before the completion barrier.
    private T[][] _buffers;

    private readonly int _requestedCapacity;
    private readonly bool _allowGrowth;
    private int _initialSegmentCapacity;

    // O(1) "anything pushed this tick?" gate for the reactive-skip path, which polls IsEmpty once per consumed queue per system per tick and would
    // otherwise touch every slot's (cold, padded) cache line. A single "anything here?" flag. Every writer stores the same value, so the
    // race is benign; it is monotonic within a tick and cleared by Reset.
    private int _anyProduced;

    // Queue-level high-water depth, stamped by Drain before it removes anything. Consumer-only, single-threaded.
    private uint _peakBeforeDrains;

    /// <summary>
    /// Creates a new event queue.
    /// </summary>
    /// <param name="name">Diagnostic name for this queue.</param>
    /// <param name="capacity">Expected events per tick across all workers. A power of 2. This is the initial allocation and the growth ceiling per
    /// worker segment — not a hard cap on the queue, which may grow to <c>capacity</c> per slot under skew.</param>
    /// <param name="allowGrowth">When false, a full segment drops instead of doubling — a fixed bound with loud telemetry, for callers who prefer it.</param>
    public EventQueue(string name, int capacity = 1024, bool allowGrowth = true)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (capacity < 1 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        }

        Name = name;
        _requestedCapacity = capacity;
        _allowGrowth = allowGrowth;
        BindWorkerSlots(1);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <summary>
    /// Acquire fence paired with the producers' plain segment stores. JIT-folded to nothing on x64 (TSO already orders loads); emits a load barrier on
    /// arm64. Call before folding any segment state from the consumer side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AcquireSegments()
    {
        if (!X86Base.IsSupported)
        {
            Interlocked.MemoryBarrier();
        }
    }

    /// <inheritdoc />
    public override int Count
    {
        get
        {
            AcquireSegments();
            var total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].Count;
            }

            return total;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The value passed at construction, unchanged. It must stay a construction-time constant: the profiler builds its one-shot
    /// <see cref="Typhon.Profiler.EventQueueRecord"/> catalog inside <c>TyphonRuntime.Create</c>, before the first tick and before any segment is
    /// allocated, and the Workbench divides per-tick depth by it. A fold over live buffers reported 0 for every queue in every trace.
    /// </remarks>
    public override int Capacity => _requestedCapacity;

    /// <summary>Items actually reserved across all segments right now — diagnostics only. Grows toward <see cref="Capacity"/> per segment.</summary>
    public int AllocatedCapacity
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _buffers.Length; i++)
            {
                total += _buffers[i]?.Length ?? 0;
            }

            return total;
        }
    }

    /// <inheritdoc />
    /// <remarks>O(1) in the overwhelmingly common "nothing was pushed" case; only a queue that saw traffic this tick pays the per-slot fold, and only
    /// because a mid-tick <c>Drain</c> can empty it again.</remarks>
    public override bool IsEmpty => Volatile.Read(ref _anyProduced) == 0 || Count == 0;

    /// <inheritdoc />
    /// <remarks>
    /// A genuine queue-level high-water mark. Summing per-slot maxima was wrong in both directions: it added maxima observed at unrelated instants
    /// (slot 0 peaking at 100 and draining, then slot 1 doing the same, reported 200 for a queue that never held more than 100), and the partial-drain
    /// path never folded at all, under-reporting by 125x on a <c>stackalloc[16]</c> drain loop. <see cref="Drain"/> now stamps total depth before
    /// removing anything, which is exact because Drain is single-consumer and single-threaded.
    /// </remarks>
    public override uint PeakDepth => Math.Max(_peakBeforeDrains, (uint)Count);

    /// <inheritdoc />
    public override uint OverflowCount
    {
        get
        {
            AcquireSegments();
            uint total = 0;
            for (var i = 0; i < _slots.Length; i++)
            {
                total += _slots[i].Overflow;
            }

            return total;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Derived, not accumulated. Every accepted push is either still live or has already been drained, and a dropped push never reaches a buffer — so
    /// <c>Produced == Count + Consumed</c> holds exactly. Maintaining a separate counter would add a second store to the hottest line in the engine to
    /// re-derive a number both operands already carry.
    /// </remarks>
    public override uint Produced => (uint)Count + Consumed;

    /// <inheritdoc />
    internal override void BindWorkerSlots(int slotCount)
    {
        if (slotCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, "Worker slot count must be at least 1.");
        }

        if (_slots != null && _slots.Length == slotCount)
        {
            return;
        }

        // Split the requested per-tick budget evenly, floored so a many-worker runtime does not degenerate into per-slot buffers of 1-2 entries.
        // Skew is absorbed by growth rather than by over-allocating every slot up front.
        var perSlot = Math.Max(MinSegmentCapacity, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, _requestedCapacity / slotCount)));
        _initialSegmentCapacity = Math.Min(perSlot, _requestedCapacity);

        _slots = new EventQueueSegmentState[slotCount];
        _buffers = new T[slotCount][];
        _anyProduced = 0;
    }

    /// <summary>
    /// Returns a writer bound to one worker slot. Resolve it ONCE per system body and push through it — the per-push cost is then a bounds check and
    /// two increments, with no lookup.
    /// </summary>
    /// <param name="workerSlot"><see cref="TickContext.WorkerId"/> of the calling worker.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside <c>[0, WorkerSlotCount)</c> — most likely
    /// <see cref="TickContext.NonWorkerId"/> from a lifecycle hook, which must not produce events.</exception>
    internal EventWriter<T> GetWriter(int workerSlot)
    {
        if ((uint)workerSlot >= (uint)_slots.Length)
        {
            var reason = workerSlot == TickContext.NonWorkerId
                ? "A lifecycle-hook context (OnFirstTick / OnShutdown) owns no worker slot — produce from a system body instead."
                : $"Slot {workerSlot} is outside [0, {_slots.Length}). If the queue was built with `new EventQueue<T>(...)` rather than "
                  + "`Dag.CreateEventQueue<T>(...)`, it was never registered with the scheduler and so never sized to the worker count.";
            throw new ArgumentOutOfRangeException(nameof(workerSlot), workerSlot, $"Event queue '{Name}': {reason}");
        }

        return new EventWriter<T>(this, workerSlot);
    }

    /// <summary>
    /// Pushes one event into the calling worker's segment. Convenience for call sites that push once; prefer <see cref="GetWriter"/> in a loop.
    /// </summary>
    /// <param name="workerSlot"><see cref="TickContext.WorkerId"/> of the calling worker.</param>
    /// <param name="item">The event to enqueue.</param>
    /// <returns><c>true</c> if stored; <c>false</c> if dropped at the growth ceiling — see <see cref="EventWriter{T}.Push"/>.</returns>
    internal bool Push(int workerSlot, T item) => GetWriter(workerSlot).Push(item);

    /// <summary>
    /// Drains all events into the output span. Returns the number of events copied. After drain, the queue is empty.
    /// </summary>
    /// <param name="output">Destination span. Must be large enough to hold <see cref="Count"/> events; a short span drains what fits and leaves the
    /// remainder in place.</param>
    /// <returns>Number of events copied.</returns>
    /// <remarks>Single-consumer, and events arrive grouped by worker slot — see the ordering note on <see cref="EventQueue{T}"/>.</remarks>
    public int Drain(Span<T> output)
    {
        AcquireSegments();

        var pending = Count;
        if (pending == 0)
        {
            return 0;
        }

        if (output.Length < pending)
        {
            // Deliberately loud. Truncating instead would be drain-side loss that OverflowCount does not count and the Workbench cannot show, in
            // exactly the `stackalloc[16]` shape the docs teach.
            throw new ArgumentException(
                $"Event queue '{Name}': destination span holds {output.Length} but {pending} events are pending. Size it from Count.",
                nameof(output));
        }

        // Queue-level high-water mark, stamped before anything is removed (#861 review). Exact: Drain is single-consumer and single-threaded.
        if ((uint)pending > _peakBeforeDrains)
        {
            _peakBeforeDrains = (uint)pending;
        }

        var written = 0;
        for (var i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            var count = slot.Count;
            if (count == 0)
            {
                continue;
            }

            var buffer = _buffers[i];
            buffer.AsSpan(0, count).CopyTo(output[written..]);
            written += count;

            slot.Count = 0;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                Array.Clear(buffer, 0, count);
            }
        }

        Consumed += (uint)written;
        return written;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        // Early-out on an untouched queue. Reset runs per queue per tick on the timer thread, and `slot = default` dirties a line the producing core
        // last held exclusively — paying that for every slot of every idle queue was measured at ~107x the old cost.
        if (Volatile.Read(ref _anyProduced) != 0)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                ref var slot = ref _slots[i];
                if (slot.Count > 0 && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    Array.Clear(_buffers[i], 0, slot.Count);
                }

                // Grown buffers are deliberately KEPT: the high-water allocation is the point of growth, and re-shrinking would reallocate every tick.
                slot = default;
            }

            Volatile.Write(ref _anyProduced, 0);
        }

        _peakBeforeDrains = 0;
        // Clear per-tick telemetry accumulators (#311). Scheduler reads them in OnTickEnd before Reset() is called at the next tick start.
        Consumed = 0;
    }

    /// <summary>Slot-owner-only append. Returns false when the segment is at its ceiling and the event was dropped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal bool PushSlow(int workerSlot, T item)
    {
        ref var slot = ref _slots[workerSlot];
        var buffer = _buffers[workerSlot];

        if (buffer == null)
        {
            // First push into this slot — allocate lazily, so a queue only pays for the workers that actually produce into it.
            buffer = new T[_initialSegmentCapacity];
            _buffers[workerSlot] = buffer;
        }
        else if (slot.Count >= buffer.Length)
        {
            if (!_allowGrowth || buffer.Length >= _requestedCapacity)
            {
                slot.Overflow++;
                return false;
            }

            // Grow only THIS worker's own buffer. The outer _buffers array is never resized (rule MD-02); no other thread reads this element before
            // the producing system's completion barrier.
            var grown = Math.Min(buffer.Length * 2, _requestedCapacity);
            Array.Resize(ref buffer, grown);
            _buffers[workerSlot] = buffer;
        }

        buffer[slot.Count++] = item;
        MarkProduced();
        return true;
    }

    /// <summary>Raises the O(1) emptiness gate. Every writer stores the same value, so concurrent stores are benign.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkProduced()
    {
        if (Volatile.Read(ref _anyProduced) == 0)
        {
            Volatile.Write(ref _anyProduced, 1);
        }
    }

    internal ref EventQueueSegmentState SlotState(int workerSlot) => ref _slots[workerSlot];

    internal T[][] SlotBuffers() => _buffers;

}
