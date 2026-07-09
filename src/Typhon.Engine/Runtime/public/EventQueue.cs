using JetBrains.Annotations;
using System;

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

    /// <summary>Number of items currently in the queue.</summary>
    public abstract int Count { get; }

    /// <summary>Maximum items per tick before <c>Push</c> overflows. Power of 2. Static schema fact — surfaced
    /// to the trace's <see cref="Typhon.Profiler.EventQueueRecord.Capacity"/> for offline analysis (utilisation %
    /// against per-tick depth).</summary>
    public abstract int Capacity { get; }

    /// <summary>True if the queue has no items.</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>Resets the queue to empty. Called at the start of each tick. Also clears the per-tick telemetry accumulators.</summary>
    public abstract void Reset();

    // ─── Per-tick telemetry accumulators (#311) ─────────────────────────────────

    /// <summary>Maximum number of items observed in the queue at any point during the current tick (after each <c>Push</c>).</summary>
    public uint PeakDepth { get; protected set; }

    /// <summary>Number of overflow events during the current tick — each is a <c>Push</c> against a full queue.</summary>
    public uint OverflowCount { get; protected set; }

    /// <summary>Number of <c>Push</c> calls that succeeded during the current tick.</summary>
    public uint Produced { get; protected set; }

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
/// Typed event queue for inter-system communication. Producer systems push events;
/// consumer systems drain them. DAG ordering guarantees producer completes before consumer starts, so no concurrent access occurs — this is a simple SPSC buffer.
/// </summary>
/// <remarks>
/// <para>
/// Capacity must be a power of 2. If the queue fills up, <see cref="Push"/> throws.
/// The queue is reset at the start of each tick by the scheduler.
/// </para>
/// <para>
/// Push is O(1). Drain copies all items to the output span and resets the count.
/// </para>
/// </remarks>
/// <typeparam name="T">The event type. No constraints — can be any struct or class.</typeparam>
[PublicAPI]
public sealed class EventQueue<T> : EventQueueBase
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private int _count;

    /// <summary>
    /// Creates a new event queue with the specified capacity.
    /// </summary>
    /// <param name="name">Diagnostic name for this queue.</param>
    /// <param name="capacity">Maximum number of events per tick. Must be a power of 2.</param>
    public EventQueue(string name, int capacity = 1024)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (capacity < 1 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        }

        Name = name;
        _capacity = capacity;
        _buffer = new T[capacity];
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override int Count => _count;

    /// <inheritdoc />
    public override int Capacity => _capacity;

    /// <inheritdoc />
    public override bool IsEmpty => _count == 0;

    /// <summary>
    /// Pushes an event into the queue. Called by producer systems during tick execution.
    /// </summary>
    /// <param name="item">The event to enqueue.</param>
    /// <exception cref="InvalidOperationException">The queue is full.</exception>
    public void Push(T item)
    {
        if (_count >= _capacity)
        {
            OverflowCount++;
            throw new InvalidOperationException($"Event queue '{Name}' is full (capacity: {_capacity}).");
        }

        _buffer[_count++] = item;
        Produced++;
        if ((uint)_count > PeakDepth)
        {
            PeakDepth = (uint)_count;
        }
    }

    /// <summary>
    /// Drains all events into the output span. Returns the number of events copied.
    /// After drain, the queue is empty.
    /// </summary>
    /// <param name="output">Destination span. Must be large enough to hold all events.</param>
    /// <returns>Number of events copied.</returns>
    public int Drain(Span<T> output)
    {
        var count = _count;
        if (count == 0)
        {
            return 0;
        }

        _buffer.AsSpan(0, count).CopyTo(output);
        _count = 0;

        // Clear references to allow GC collection (for reference types)
        if (!typeof(T).IsValueType)
        {
            Array.Clear(_buffer, 0, count);
        }

        Consumed += (uint)count;
        return count;
    }

    /// <summary>
    /// Returns a read-only span over the current queue contents without draining.
    /// </summary>
    public ReadOnlySpan<T> AsSpan() => _buffer.AsSpan(0, _count);

    /// <inheritdoc />
    public override void Reset()
    {
        if (!typeof(T).IsValueType && _count > 0)
        {
            Array.Clear(_buffer, 0, _count);
        }

        _count = 0;
        // Clear per-tick telemetry accumulators (#311). Scheduler reads them in OnTickEnd before Reset() is called at the next tick start.
        PeakDepth = 0;
        OverflowCount = 0;
        Produced = 0;
        Consumed = 0;
    }
}
