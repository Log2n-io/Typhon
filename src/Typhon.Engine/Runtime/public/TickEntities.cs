using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// The entities a system run receives (<see cref="TickContext.Entities"/>): a range of a pooled array, or a view that walks clusters or a partition.
/// </summary>
/// <remarks>
/// <para>
/// <b>A value, so a system run allocates nothing for it.</b> <see cref="TickContext.Entities"/> was an <c>IReadOnlyCollection&lt;EntityId&gt;</c>, and every
/// serial run boxed the <see cref="PooledEntityList"/> it was handed into it, and every parallel chunk its <see cref="PooledEntitySlice"/>: one object per
/// system per tick, a steady MMORPG shard's last avoidable allocation (#1623 residual). <c>foreach</c> binds to the struct enumerator below and allocates
/// nothing either; going through the interface (LINQ) still boxes, as it always did.
/// </para>
/// <para>The backing array is pooled: do not hold this, or anything read from it by reference, beyond the system's Execute scope.</para>
/// </remarks>
[PublicAPI]
public readonly struct TickEntities : IReadOnlyCollection<EntityId>
{
    private readonly EntityId[] _array;
    private readonly int _start;
    private readonly int _count;
    private readonly IReadOnlyCollection<EntityId> _view;

    private TickEntities(EntityId[] array, int start, int count, IReadOnlyCollection<EntityId> view)
    {
        _array = array;
        _start = start;
        _count = count;
        _view = view;
    }

    /// <summary>No entities.</summary>
    public static TickEntities Empty => default;

    /// <summary>Entities read through a view the runtime keeps per system and worker (a cluster walk, a partition), so wrapping it allocates nothing.</summary>
    internal static TickEntities Of(IReadOnlyCollection<EntityId> view) => new(null, 0, 0, view);

    /// <summary>A pooled list, whole.</summary>
    public static implicit operator TickEntities(PooledEntityList list) => new(list.BackingArray, 0, list.Count, null);

    /// <summary>A range of a pooled list (a parallel chunk's share).</summary>
    public static implicit operator TickEntities(PooledEntitySlice slice) => new(slice.BackingArray, slice.Start, slice.Count, null);

    /// <summary>How many entities.</summary>
    public int Count => _view?.Count ?? _count;

    /// <summary>
    /// The entity at <paramref name="index"/>, for an array-backed set. A cluster or partition view has no random access: enumerate it instead.
    /// </summary>
    /// <param name="index">0-based.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    /// <exception cref="NotSupportedException">The set is a view, which only enumerates.</exception>
    public EntityId this[int index]
    {
        get
        {
            if (_view != null)
            {
                throw new NotSupportedException("this system's entities come from a cluster or partition view, which has no random access: enumerate it");
            }

            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"the set holds {_count} entities");
            }

            return _array[_start + index];
        }
    }

    /// <summary>Whether the set is a pooled array range, which <see cref="AsSpan"/> and the indexer read directly.</summary>
    public bool IsArrayBacked => _view == null;

    /// <summary>The entities as a span, for an array-backed set; empty for a view (check <see cref="IsArrayBacked"/>).</summary>
    public ReadOnlySpan<EntityId> AsSpan() => _view == null && _array != null ? _array.AsSpan(_start, _count) : default;

    /// <summary>The allocation-free enumerator <c>foreach</c> binds to.</summary>
    public Enumerator GetEnumerator() => new(_array, _start, _count, _view);

    IEnumerator<EntityId> IEnumerable<EntityId>.GetEnumerator() => _view?.GetEnumerator() ?? GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<EntityId>)this).GetEnumerator();

    /// <summary>Walks the array range, or the view through its own enumerator.</summary>
    public struct Enumerator : IEnumerator<EntityId>
    {
        private readonly EntityId[] _array;
        private readonly int _end;
        private readonly IEnumerator<EntityId> _view;
        private int _index;

        internal Enumerator(EntityId[] array, int start, int count, IReadOnlyCollection<EntityId> view)
        {
            _array = array;
            _index = start - 1;
            _end = start + count;

            // A view's enumerator: the runtime's views enumerate themselves (they are their own IEnumerator), so this allocates nothing for them.
            _view = view?.GetEnumerator();
        }

        /// <inheritdoc />
        public EntityId Current => _view != null ? _view.Current : _array[_index];

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext() => _view?.MoveNext() ?? ++_index < _end;

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException();

        /// <inheritdoc />
        public void Dispose() => _view?.Dispose();
    }
}
