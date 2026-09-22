using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>What a migration did with an entity's replication entry.</summary>
internal enum ReplicationMigrationOutcome
{
    /// <summary>Nobody was watching it, so there was no entry to carry.</summary>
    NothingToCarry = 0,

    /// <summary>It was written straight into the destination block.</summary>
    Carried = 1,

    /// <summary>The destination had no block yet, so a copy is waiting for the prologue.</summary>
    Parked = 2,
}

/// <summary>
/// One migration slice's entries whose destination cluster had no replication block yet, copied aside until the prologue can place them.
/// </summary>
/// <remarks>
/// <para>
/// <b>It stores the BYTES, not a pointer to them.</b> A slot the entity left can be filled again inside the same migration step, so a list of source
/// addresses would be read after the memory under it had become another entity's — the entry would be written into the destination describing whoever
/// arrived in the source slot afterwards. Copying is what makes the parked entry mean what it meant when it was parked.
/// </para>
/// <para>
/// <b>One list per slice, so nothing here synchronises.</b> Migration slices are cut by destination cell, so no two of them park for the same destination,
/// and the drain runs at a single-threaded point separated from the slices by the track's own dispatch barrier.
/// </para>
/// <para>
/// <b>Native memory, because the bytes it holds are the ones a block is built from.</b> A managed array would be a GC allocation on a path the fence takes,
/// and — more to the point — every address this hands out would be an address into GC memory, which the engine's pointer rule forbids outright.
/// </para>
/// </remarks>
internal sealed unsafe class ParkedEntryList : IDisposable
{
    /// <summary>The destination of one parked entry. Its bytes live in <see cref="_bytes"/> at the matching index.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParkedSlot
    {
        public int ChunkId;
        public int Slot;
    }

    private readonly int _stride;

    private ParkedSlot[] _slots = new ParkedSlot[16];
    private byte* _bytes;
    private int _capacity;
    private int _count;
    private bool _disposed;

    /// <summary>Creates a list whose entries are <paramref name="stride"/> bytes each.</summary>
    /// <param name="stride">The hot entry's stride plus the cold entry's, which is what one parked entry occupies.</param>
    public ParkedEntryList(int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        _stride = stride;
    }

    /// <summary>How many entries are waiting.</summary>
    public int Count => _count;

    /// <summary>
    /// Copies one entry aside.
    /// </summary>
    /// <param name="chunkId">The destination cluster.</param>
    /// <param name="slot">The destination slot.</param>
    /// <param name="hot">The hot entry's first byte.</param>
    /// <param name="hotBytes">Its stride.</param>
    /// <param name="cold">The cold entry's first byte.</param>
    /// <param name="coldBytes">Its stride.</param>
    /// <returns><see langword="false"/> when the entry was NOT kept, so the caller can count a drop rather than a park.</returns>
    public bool Add(int chunkId, int slot, byte* hot, int hotBytes, byte* cold, int coldBytes)
    {
        if (_disposed || hotBytes + coldBytes != _stride)
        {
            return false;
        }

        EnsureCapacity(_count + 1);

        _slots[_count] = new ParkedSlot { ChunkId = chunkId, Slot = slot };
        var destination = _bytes + ((nint)_count * _stride);
        Unsafe.CopyBlockUnaligned(destination, hot, (uint)hotBytes);
        Unsafe.CopyBlockUnaligned(destination + hotBytes, cold, (uint)coldBytes);
        _count++;
        return true;
    }

    /// <summary>
    /// Reads back one parked entry.
    /// </summary>
    /// <param name="index">Its position in the list.</param>
    /// <param name="chunkId">The destination cluster.</param>
    /// <param name="slot">The destination slot.</param>
    /// <param name="bytes">The entry's bytes: the hot entry followed by the cold one.</param>
    public void Read(int index, out int chunkId, out int slot, out byte* bytes)
    {
        if (_disposed || (uint)index >= (uint)_count)
        {
            chunkId = -1;
            slot = -1;
            bytes = null;
            return;
        }

        ref var parked = ref _slots[index];
        chunkId = parked.ChunkId;
        slot = parked.Slot;
        bytes = _bytes + ((nint)index * _stride);
    }

    /// <summary>Empties the list without freeing what it holds, so the next fence reuses the allocation.</summary>
    public void Clear() => _count = 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _count = 0;
        _capacity = 0;
        if (_bytes != null)
        {
            NativeMemory.AlignedFree(_bytes);
            _bytes = null;
        }
    }

    private void EnsureCapacity(int wanted)
    {
        if (wanted <= _capacity)
        {
            return;
        }

        var capacity = Math.Max(16, _capacity == 0 ? 16 : _capacity * 2);
        while (capacity < wanted)
        {
            capacity *= 2;
        }

        var grown = (byte*)NativeMemory.AlignedAlloc((nuint)((nint)capacity * _stride), 64);
        if (_bytes != null)
        {
            Unsafe.CopyBlockUnaligned(grown, _bytes, (uint)((nint)_count * _stride));
            NativeMemory.AlignedFree(_bytes);
        }

        _bytes = grown;
        _capacity = capacity;

        if (_slots.Length < capacity)
        {
            Array.Resize(ref _slots, capacity);
        }
    }
}
