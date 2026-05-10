using System;
using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Zero-copy window into a <see cref="PooledEntityList"/>'s backing array.
/// Used by parallel QuerySystem dispatch to give each chunk its entity slice without copying.
/// </summary>
/// <remarks>
/// This is a value type — it boxes once when assigned to <see cref="IReadOnlyCollection{T}"/> (same cost as <see cref="PooledEntityList"/>).
/// The backing array is NOT owned by this struct; the original <see cref="PooledEntityList"/> manages its lifetime.
/// </remarks>
[PublicAPI]
public readonly struct PooledEntitySlice : IReadOnlyCollection<EntityId>
{
    private readonly EntityId[] _array;
    private readonly int _start;
    private readonly int _count;

    internal PooledEntitySlice(EntityId[] array, int start, int count)
    {
        _array = array;
        _start = start;
        _count = count;
    }

    /// <summary>Number of entities in this chunk slice.</summary>
    public int Count => _count;

    /// <summary>Access entity by index within this slice.</summary>
    public EntityId this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _array[_start + index];
        }
    }

    /// <summary>Value-type enumerator — no allocation.</summary>
    public Enumerator GetEnumerator() => new(_array, _start, _count);

    IEnumerator<EntityId> IEnumerable<EntityId>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Stack-allocated enumerator over the entity array slice.</summary>
    public struct Enumerator : IEnumerator<EntityId>
    {
        private readonly EntityId[] _array;
        private readonly int _start;
        private readonly int _count;
        private int _index;

        internal Enumerator(EntityId[] array, int start, int count)
        {
            _array = array;
            _start = start;
            _count = count;
            _index = -1;
        }

        public EntityId Current => _array[_start + _index];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
