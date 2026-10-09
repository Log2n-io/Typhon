using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.internals;

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
    private readonly long _baseTSN;

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

    /// <summary>
    /// True when a producer has dropped at least one entry because the ring was full. Sticky until a consumer clears it.
    /// </summary>
    /// <remarks>
    /// Acquire-read, paired with the release in <see cref="TryAppend"/>. The consumer's whole self-healing story rests on this flag surviving until a resync
    /// has observed it — a stale read here is a resync that concludes it is clean while entries have been dropped, and those entries are then gone with
    /// nothing left to repair them.
    /// </remarks>
    public bool HasOverflow => Volatile.Read(ref _overflow) != 0;

    public bool IsDisposed => _disposed != 0;

    /// <summary>
    /// Append an entry to the ring buffer. Thread-safe for multiple concurrent producers.
    /// </summary>
    /// <returns>True if the entry was appended; false if the buffer is full (overflow).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAppend(EntityId entityPK, KeyBytes8 beforeKey, KeyBytes8 afterKey, long tsn, byte flags, byte componentTag = 0)
    {
        while (true)
        {
            var tail = _tail.Value;
            var head = _head.Value;

            if (tail - head >= _capacity)
            {
                // Release, so a consumer that acquire-reads HasOverflow cannot see the flag set without also seeing the state that preceded it.
                Volatile.Write(ref _overflow, 1);
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
        var head = _head.Value;
        _written[(int)(head & _capacityMask)] = 0;

        // Release: a producer that sees the advanced head may claim this slot and set its written flag; on arm64 a plain store here could become visible
        // before the clear above, which would then overwrite the producer's flag and leave the consumer spinning on a slot that was written.
        Volatile.Write(ref _head.Value, head + 1);
    }

    /// <summary>
    /// Atomically clears the sticky overflow flag without discarding the ring's contents, and reports whether it had been set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way to clear <c>_overflow</c>. A resync — membership or full — re-queries and then wants the flag gone but every entry kept: those above its
    /// snapshot are for commits it cannot see yet, and those below it for commits whose value its re-scan may have missed (VDELTA-01).
    /// </para>
    /// <para>
    /// <b>Interlocked, and it returns the previous value, because a plain write here loses entries.</b> A producer sets the flag AFTER dropping entries and
    /// bumps the structural epoch after that; a consumer that clears the flag with a plain store can therefore swallow an overflow whose epoch bump has not
    /// landed yet, conclude from the unchanged epoch that its resync was clean, and leave the drain arm to run next refresh with the flag already gone. The
    /// dropped entries are then unrecoverable. Handing back the prior value lets the caller keep itself in resync instead.
    /// </para>
    /// </remarks>
    public bool ClearOverflow() => Interlocked.Exchange(ref _overflow, 0) != 0;

    /// <summary>True while the pinned block behind this buffer is still mapped — false once it has actually been freed.</summary>
    /// <remarks>Exists so a verifier can assert that a write racing a disposal lands in live memory, which is the whole claim of #864's fix.</remarks>
    internal bool BlockIsLive => _block is { IsDisposed: false };

    /// <summary>
    /// Marks this buffer dead to consumers and hands its pinned block to <paramref name="reclaimer"/>, WITHOUT freeing it.
    /// </summary>
    /// <remarks>
    /// The pointers stay valid on purpose. A publisher already past its <c>IsDisposed</c> check writes 24 bytes into a ring nobody will drain,
    /// in memory that is still mapped and still owned — harmless, where a free would have made it silent heap corruption. The block is freed
    /// later, once no thread can still hold the registration that names it. See <c>ViewBufferReclaimer</c>.
    /// </remarks>
    internal void Retire(ViewBufferReclaimer reclaimer)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (reclaimer == null)
        {
            // No reclaimer (a buffer built outside an engine, e.g. a unit test on the type itself): nothing can be publishing into it, so the
            // immediate free is safe and leaking it would not be.
            _block?.Dispose();
            _block = null;
            _entries = null;
            _deltaTSNs = null;
            _flags = null;
            _componentTags = null;
            _written = null;
            return;
        }

        // The pointers are DELIBERATELY left valid, and this is the whole mechanism — nulling them here would recreate the very fault the deferral
        // exists to remove. A publisher already past its IsDisposed check writes 24 bytes through _entries; if that is null the write faults, and in
        // unsafe pointer arithmetic a null base is an access violation, not a catchable NullReferenceException. Leaving them pointing at the retired
        // block makes the write land in mapped, owned memory that nobody will ever drain.
        //
        // The buffer goes WITH the block so the reclaimer can null them at the moment it frees. Leaving them dangling afterwards would trade a loud
        // null fault for a silent write into whatever the allocator re-issued that address to — and Reset in particular memsets 8 KB through them.
        reclaimer.Retire(this, _block);
    }

    /// <summary>
    /// Invoked by <c>ViewBufferReclaimer</c> at the instant it frees this buffer's block, once no thread can still reach it.
    /// </summary>
    /// <remarks>
    /// Nulling here rather than in <see cref="Retire"/> is the difference between a loud fault and silent corruption. Before the free the pointers
    /// must stay valid (a late publisher writes through them); after it they must NOT, or any later use writes into memory the allocator has re-issued.
    /// </remarks>
    internal void OnBlockReclaimed()
    {
        _block = null;
        _entries = null;
        _deltaTSNs = null;
        _flags = null;
        _componentTags = null;
        _written = null;
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
