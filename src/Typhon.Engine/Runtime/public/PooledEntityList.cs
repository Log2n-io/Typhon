using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// <see cref="ArrayPool{T}"/>-backed read-only entity collection used by <see cref="TickContext.Entities"/>.
/// Avoids per-tick allocations after warm-up. The backing array is rented from the shared pool at system dispatch
/// and returned via <see cref="Return"/> after the system completes.
/// </summary>
[PublicAPI]
public struct PooledEntityList : IReadOnlyCollection<EntityId>
{
    /// <summary>Singleton empty list — no pool rental, safe to Return() multiple times.</summary>
    public static readonly PooledEntityList Empty = new([], 0);

    private EntityId[] _array;
    private int _count;

    internal PooledEntityList(EntityId[] array, int count)
    {
        _array = array;
        _count = count;
    }

    /// <summary>Number of entities in the filtered set.</summary>
    public int Count => _count;

    /// <summary>Access entity by index.</summary>
    public EntityId this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _array[index];
        }
    }

    /// <summary>
    /// Rent an array from the pool, populate it with the given entities, and return a <see cref="PooledEntityList"/>.
    /// </summary>
    internal static PooledEntityList Rent(int count)
    {
        if (count == 0)
        {
            return Empty;
        }

        var array = ArrayPool<EntityId>.Shared.Rent(count);
        return new PooledEntityList(array, count);
    }

    /// <summary>Direct access to the backing array for bulk population. Valid indices: 0..<see cref="Count"/>-1.</summary>
    internal Span<EntityId> AsSpan() => _array.AsSpan(0, _count);

    /// <summary>Direct access to the backing array. Used by <see cref="PooledEntitySlice"/> for zero-copy parallel chunk slicing.</summary>
    internal EntityId[] BackingArray => _array;

    /// <summary>
    /// Return the backing array to the pool. Safe to call multiple times (idempotent).
    /// Must be called after the system's Execute completes.
    /// </summary>
    internal void Return()
    {
        if (_array is { Length: > 0 })
        {
            ArrayPool<EntityId>.Shared.Return(_array);
        }

        _array = [];
        _count = 0;
    }

    /// <summary>Value-type enumerator — no allocation.</summary>
    public Enumerator GetEnumerator() => new(_array, _count);

    IEnumerator<EntityId> IEnumerable<EntityId>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Stack-allocated enumerator over the entity array segment.</summary>
    public struct Enumerator : IEnumerator<EntityId>
    {
        private readonly EntityId[] _array;
        private readonly int _count;
        private int _index;

        internal Enumerator(EntityId[] array, int count)
        {
            _array = array;
            _count = count;
            _index = -1;
        }

        public EntityId Current => _array[_index];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
