using System;
using System.Numerics;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// The copy-before-write slots of one capture: native 8 KiB page buffers a data-file writer rents, fills with a page's pre-image and hands to the backup
/// thread, which ingests and returns them. No allocation per copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handoff.</b> The producer stores the page index (plain), then sets the slot's ready bit with <c>Interlocked.Or</c> (a full fence: the page index
/// and the slot's bytes are published before the bit). The consumer takes a whole ready word with <c>Interlocked.Exchange</c>, which orders its reads
/// of the indices and the bytes after the producer's stores. Free slots are counted by a semaphore and found in a free bitmap with CAS.
/// </para>
/// <para>
/// <b>Lifetime.</b> Writers reach the pool only through the installed hook, so it may be freed only once the hook has been uninstalled under the
/// exclusive write gate (no writer can still be inside it).
/// </para>
/// </remarks>
internal sealed unsafe class CbwSlotPool : IDisposable
{
    private readonly PinnedMemoryBlock _block;
    private readonly byte* _base;
    private readonly int _count;
    private readonly SemaphoreSlim _free;
    private readonly long[] _inUse;
    private readonly long[] _ready;
    private readonly int[] _slotPage;

    /// <summary>Allocates <paramref name="slotCount"/> slots.</summary>
    public CbwSlotPool(IMemoryAllocator allocator, IResource owner, int slotCount)
    {
        _count = Math.Max(1, slotCount);
        _block = allocator.AllocatePinned("Backup.CbwSlots", owner, _count * PagedMMF.PageSize, false, 4096);
        _base = _block.DataAsPointer;
        _free = new SemaphoreSlim(_count, _count);
        _inUse = new long[(_count + 63) >> 6];
        _ready = new long[(_count + 63) >> 6];
        _slotPage = new int[_count];
    }

    /// <summary>Signalled whenever a slot is published.</summary>
    public ManualResetEventSlim Published { get; } = new(false);

    /// <summary>The bytes of slot <paramref name="slot"/>.</summary>
    public Span<byte> Span(int slot) => new(_base + (long)slot * PagedMMF.PageSize, PagedMMF.PageSize);

    /// <summary>Rents a free slot, waiting up to <paramref name="timeoutMs"/>; <c>-1</c> when none freed in time or <paramref name="abort"/> fired.</summary>
    public int TryRent(int timeoutMs, CancellationToken abort)
    {
        try
        {
            if (!_free.Wait(Math.Max(0, timeoutMs), abort))
            {
                return -1;
            }
        }
        catch (OperationCanceledException)
        {
            return -1;
        }

        // The semaphore guarantees a clear bit exists; claim one.
        while (true)
        {
            for (var w = 0; w < _inUse.Length; w++)
            {
                var word = Volatile.Read(ref _inUse[w]);
                var valid = Math.Min(64, _count - (w << 6));
                var freeBits = ~word & (valid == 64 ? -1L : (1L << valid) - 1);
                while (freeBits != 0)
                {
                    var bit = BitOperations.TrailingZeroCount(freeBits);
                    var mask = 1L << bit;
                    if ((Interlocked.Or(ref _inUse[w], mask) & mask) == 0)
                    {
                        return (w << 6) + bit;
                    }

                    freeBits &= ~mask;
                }
            }
        }
    }

    /// <summary>Hands slot <paramref name="slot"/>, holding page <paramref name="page"/>'s pre-image, to the backup thread.</summary>
    public void Publish(int slot, int page)
    {
        _slotPage[slot] = page;
        Interlocked.Or(ref _ready[slot >> 6], 1L << (slot & 63));
        Published.Set();
    }

    /// <summary>
    /// Ingests every published slot with <paramref name="ingest"/> (page, bytes), then returns it to the pool. Backup thread only. Returns how many.
    /// </summary>
    public int Drain(Action<int, ReadOnlySpan<byte>> ingest)
    {
        Published.Reset();
        var drained = 0;
        for (var w = 0; w < _ready.Length; w++)
        {
            if (Volatile.Read(ref _ready[w]) == 0)
            {
                continue;
            }

            var taken = Interlocked.Exchange(ref _ready[w], 0);
            while (taken != 0)
            {
                var bit = BitOperations.TrailingZeroCount(taken);
                taken &= taken - 1;
                var slot = (w << 6) + bit;
                try
                {
                    ingest(_slotPage[slot], Span(slot));
                }
                finally
                {
                    Release(slot);
                }

                drained++;
            }
        }

        return drained;
    }

    /// <summary>Returns a slot that was rented but never published (its writer gave up).</summary>
    public void Release(int slot)
    {
        Interlocked.And(ref _inUse[slot >> 6], ~(1L << (slot & 63)));
        _free.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _block.Dispose();
        _free.Dispose();
        Published.Dispose();
    }
}
