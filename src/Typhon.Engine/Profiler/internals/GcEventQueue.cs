using System;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Bounded single-consumer, multi-producer ring buffer of <see cref="GcEventRecord"/> structs. Producers are CLR ETW-callback threads (serialized
/// in practice for the GC lifecycle events we subscribe to, but the lock tolerates concurrent producers regardless). The single consumer is
/// <see cref="GcIngestionThread"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Overflow policy:</b> drop-newest. If the queue is full, the incoming record is discarded and <see cref="DroppedEvents"/> is bumped. Matches
/// the existing profiler's ring-buffer philosophy ("prefer losing a sample over blocking") and guarantees that a misbehaving workload with a GC
/// storm cannot stall a CLR GC thread on our lock.
/// </para>
/// <para>
/// <b>Locking:</b> a short <see cref="System.Threading.Lock"/> on enqueue is sufficient — GC lifecycle events (Start/End/SuspendEEBegin/RestartEEEnd)
/// arrive at most a few per collection, so the lock is effectively always uncontended. A lock-free MPSC would add complexity (sequence numbers,
/// ABA management) for no measurable win at this frequency.
/// </para>
/// </remarks>
internal sealed class GcEventQueue
{
    private readonly GcEventRecord[] _buffer;
    private readonly int _mask;
    private readonly Lock _lock = new();
    private readonly AutoResetEvent _wake;
    private int _head;
    private int _tail;
    private long _droppedEvents;

    /// <summary>
    /// Create a queue with <paramref name="capacity"/> slots. Must be a power of 2.
    /// </summary>
    /// <param name="wake">Signaled every time a record is successfully enqueued. Owned externally (typically by <see cref="GcIngestionThread"/>).</param>
    public GcEventQueue(int capacity, AutoResetEvent wake)
    {
        ArgumentNullException.ThrowIfNull(wake);
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2 ≥ 2.", nameof(capacity));
        }
        _buffer = new GcEventRecord[capacity];
        _mask = capacity - 1;
        _wake = wake;
    }

    /// <summary>Number of records dropped due to overflow since construction.</summary>
    public long DroppedEvents => _droppedEvents;

    /// <summary>Try to enqueue a record. Returns <c>false</c> and bumps <see cref="DroppedEvents"/> if the queue is full.</summary>
    public bool TryEnqueue(in GcEventRecord record)
    {
        lock (_lock)
        {
            var tail = _tail;
            var head = _head;
            if (tail - head >= _buffer.Length)
            {
                Interlocked.Increment(ref _droppedEvents);
                return false;
            }
            _buffer[tail & _mask] = record;
            _tail = tail + 1;
        }
        _wake.Set();
        return true;
    }

    /// <summary>Try to dequeue one record. Single-consumer — only <see cref="GcIngestionThread"/> may call this.</summary>
    public bool TryDequeue(out GcEventRecord record)
    {
        var head = _head;
        if (head == _tail)
        {
            record = default;
            return false;
        }
        record = _buffer[head & _mask];
        _head = head + 1;
        return true;
    }
}
