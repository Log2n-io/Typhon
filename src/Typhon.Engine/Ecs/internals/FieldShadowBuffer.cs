using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Captured old index key for a single entity-field pair, stored before the first SV in-place mutation per tick.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowEntry
{
    public int ChunkId;
    public long EntityPK;
    public KeyBytes8 OldKey;
}

/// <summary>
/// Per-indexed-field append buffer that captures old index keys before SV in-place mutations.
/// <para>
/// <b>Write path (concurrent):</b> <see cref="Append"/> is called from <c>EntityRef.Write&lt;T&gt;()</c> on the first mutation per entity per tick
/// (guarded by <see cref="DirtyBitmap.TestAndSet"/>).
/// Multiple threads may append concurrently for different entities.
/// </para>
/// <para>
/// <b>Tick boundary (single-threaded):</b> Consumer iterates <see cref="Count"/> entries via indexer, then calls <see cref="Reset"/>. No concurrent appends
/// during drain (tick boundary is a sync point).
/// </para>
/// </summary>
internal sealed class FieldShadowBuffer
{
    private ShadowEntry[] _entries;
    private int _count;
    private readonly Lock _appendLock = new();

    internal FieldShadowBuffer(int initialCapacity = 256)
    {
        _entries = new ShadowEntry[initialCapacity];
    }

    /// <summary>Append a shadow entry. Thread-safe for concurrent writers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(int chunkId, long entityPK, KeyBytes8 oldKey)
    {
        lock (_appendLock)
        {
            if (_count >= _entries.Length)
            {
                Array.Resize(ref _entries, _entries.Length * 2);
            }

            _entries[_count++] = new ShadowEntry { ChunkId = chunkId, EntityPK = entityPK, OldKey = oldKey };
        }
    }

    /// <summary>Number of entries. Read at tick boundary (no concurrent appends).</summary>
    internal int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    /// <summary>Access entry by index. Read at tick boundary (no concurrent appends).</summary>
    internal ref ShadowEntry this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _entries[index];
    }

    /// <summary>Reset count to zero for next tick. Not thread-safe — call only at tick boundary.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reset() => _count = 0;
}
