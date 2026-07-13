using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// MPSC lock-free ring buffer with SOA layout for batching view delta entries.
/// Multiple producers append concurrently; a single consumer peeks/advances sequentially.
/// Memory is allocated as a single cache-line-aligned block via <see cref="IMemoryAllocator"/>,
/// with each SOA array starting at a 64-byte boundary for optimal prefetch and false-sharing avoidance.
/// </summary>
internal sealed unsafe class ViewDeltaRingBuffer : IDisposable
{
    public const int DefaultCapacity = 4096;
    private const int CacheLineSize = 64;

    // Cache-line padded counter to prevent false sharing between producer and consumer
    [StructLayout(LayoutKind.Explicit, Size = CacheLineSize)]
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    // Immutable configuration
    private readonly int _capacity;
    private readonly int _capacityMask;
    private long _baseTSN;

    // Single allocation block holding all SOA arrays
    private PinnedMemoryBlock _block;

    // SOA array pointers (computed offsets into _block)
    private ViewDeltaEntry* _entries;   // 24B × capacity
    private long* _deltaTSNs;          // 8B × capacity — long to prevent overflow on long-running low-traffic views
    private byte* _flags;              // 1B × capacity
    private byte* _componentTags;      // 1B × capacity — identifies source ComponentTable (0=T1, 1=T2)
    private byte* _written;            // 1B × capacity

    // Producer hot path — CAS on _tail, write _overflow on full. PaddedLong (64B) ensures _tail and _head occupy separate cache lines regardless of class
    // field layout, preventing false sharing between concurrent producers and the single consumer.
    private PaddedLong _tail;          // 64B (producer writes via CAS)
    private int _overflow;             // Sticky flag — only written when buffer is full (exceptional path)

    // Consumer hot path — plain increment on _head. Isolated from producer by PaddedLong padding.
    private PaddedLong _head;          // 64B (consumer writes)

    // Cold path — written only during Dispose
    private int _disposed;

    public ViewDeltaRingBuffer(IMemoryAllocator allocator, IResource resourceParent, int capacity = DefaultCapacity, long baseTSN = 0)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a positive power of 2.", nameof(capacity));
        }

        _capacity = capacity;
        _capacityMask = capacity - 1;
        _baseTSN = baseTSN;

        // Compute SOA layout: each sub-buffer starts at a 64-byte boundary
        var entriesSize = AlignUp(sizeof(ViewDeltaEntry) * capacity);
        var deltaTSNsSize = AlignUp(sizeof(long) * capacity);
        var flagsSize = AlignUp(capacity);
        var componentTagsSize = AlignUp(capacity);
        var writtenSize = AlignUp(capacity);
        var totalSize = entriesSize + deltaTSNsSize + flagsSize + componentTagsSize + writtenSize;

        _block = allocator.AllocatePinned("ViewDeltaRingBuffer", resourceParent, totalSize, true, CacheLineSize);

        var basePtr = _block.DataAsPointer;
        _entries = (ViewDeltaEntry*)basePtr;
        _deltaTSNs = (long*)(basePtr + entriesSize);
        _flags = basePtr + entriesSize + deltaTSNsSize;
        _componentTags = basePtr + entriesSize + deltaTSNsSize + flagsSize;
        _written = basePtr + entriesSize + deltaTSNsSize + flagsSize + componentTagsSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int size) => (size + CacheLineSize - 1) & ~(CacheLineSize - 1);

    public int Capacity => _capacity;

    public long BaseTSN => _baseTSN;

    public long Count => _tail.Value - _head.Value;

    public bool HasOverflow => _overflow != 0;

    public bool IsDisposed => _disposed != 0;

    /// <summary>
    /// Append an entry to the ring buffer. Thread-safe for multiple concurrent producers.
    /// </summary>
    /// <returns>True if the entry was appended; false if the buffer is full (overflow).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAppend(long entityPK, KeyBytes8 beforeKey, KeyBytes8 afterKey, long tsn, byte flags, byte componentTag = 0)
    {
        while (true)
        {
            var tail = _tail.Value;
            var head = _head.Value;

            if (tail - head >= _capacity)
            {
                _overflow = 1;
                return false;
            }

            if (Interlocked.CompareExchange(ref _tail.Value, tail + 1, tail) != tail)
            {
                continue;
            }

            var index = (int)(tail & _capacityMask);

            _entries[index].EntityPK = entityPK;
            _entries[index].BeforeKey = beforeKey;
            _entries[index].AfterKey = afterKey;
            _deltaTSNs[index] = tsn - _baseTSN;
            _flags[index] = flags;
            _componentTags[index] = componentTag;

            // Signal that this slot is ready to consume.
            // Release store: guarantees the preceding payload stores are visible to any core that observes _written[index] == 1
            // (paired with the acquire load in TryPeek). Free on x64 (TSO); emits stlr on arm64, where store-store ordering is not guaranteed.
            Volatile.Write(ref _written[index], (byte)1);

            return true;
        }
    }

    /// <summary>
    /// Peek at the next entry without consuming it. Single-consumer only.
    /// </summary>
    /// <param name="targetTSN">Maximum TSN to consume (entries beyond this are skipped).</param>
    /// <param name="entry">The entry data if available.</param>
    /// <param name="flags">The flags byte for the entry.</param>
    /// <param name="tsn">The absolute TSN of the entry.</param>
    /// <param name="componentTag">The component tag for two-component views.</param>
    /// <returns>True if an entry is available and within the target TSN range.</returns>
    public bool TryPeek(long targetTSN, out ViewDeltaEntry entry, out byte flags, out long tsn, out byte componentTag)
    {
        var head = _head.Value;
        var tail = _tail.Value;

        if (head >= tail)
        {
            entry = default;
            flags = 0;
            tsn = 0;
            componentTag = 0;
            return false;
        }

        var index = (int)(head & _capacityMask);

        // Spin until the producer has finished writing this slot.
        // Acquire load: orders the following payload reads after observing the flag (paired with the release store in TryPush).
        var spinner = new SpinWait();
        while (Volatile.Read(ref _written[index]) == 0)
        {
            spinner.SpinOnce();
        }

        // Check TSN: don't consume entries beyond the target
        tsn = _baseTSN + _deltaTSNs[index];
        if (tsn > targetTSN)
        {
            entry = default;
            flags = 0;
            componentTag = 0;
            return false;
        }

        entry = _entries[index];
        flags = _flags[index];
        componentTag = _componentTags[index];
        return true;
    }

    /// <summary>
    /// Advance past the current head entry. Must be called after a successful TryPeek.
    /// Single-consumer only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance()
    {
        var index = (int)(_head.Value & _capacityMask);
        _written[index] = 0;
        _head.Value++;
    }

    /// <summary>
    /// Reset the buffer to empty state. Not thread-safe — caller must ensure no concurrent access.
    /// </summary>
    /// <param name="newBaseTSN">When >= 0, reanchors the base TSN for delta computation.</param>
    public void Reset(long newBaseTSN = -1)
    {
        NativeMemory.Clear(_written, (nuint)_capacity);
        NativeMemory.Clear(_componentTags, (nuint)_capacity);
        _head.Value = 0;
        _tail.Value = 0;
        _overflow = 0;
        if (newBaseTSN >= 0)
        {
            _baseTSN = newBaseTSN;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _block?.Dispose();
        _block = null;
        _entries = null;
        _deltaTSNs = null;
        _flags = null;
        _componentTags = null;
        _written = null;
    }
}
