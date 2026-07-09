using JetBrains.Annotations;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Mutable accessor for a collection-typed component field — the variable-sized element list backing a <see cref="ComponentCollection{T}"/> value. Appends
/// reach the owning field by <c>ref</c>, transparently performing copy-on-write when the buffer is shared across MVCC revisions. Must be disposed; disposal
/// commits the pending buffer writes.
/// </summary>
/// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
[PublicAPI]
public ref struct ComponentCollectionAccessor<T> : IDisposable where T : unmanaged
{
    private VariableSizedBufferSegment<T, PersistentStore> _vsbs;
    private ref ComponentCollection<T> _field;
    private ChunkAccessor<PersistentStore> _ca;
    private readonly int _initialBufferId;
    private readonly ChangeSet _changeSet;

    /// <summary>Binds an accessor to collection field <paramref name="field"/> and its backing buffer segment, capturing a chunk accessor from <paramref name="changeSet"/>.</summary>
    /// <param name="changeSet">Change set the buffer mutations are threaded through (dirty tracking and commit).</param>
    /// <param name="vsbs">Backing variable-sized buffer segment that stores the collection's elements.</param>
    /// <param name="field">Reference to the collection field being accessed; <see cref="Add"/> updates its buffer id in place.</param>
    public ComponentCollectionAccessor(ChangeSet changeSet, VariableSizedBufferSegment<T, PersistentStore> vsbs, ref ComponentCollection<T> field)
    {
        _vsbs = vsbs;
        _changeSet = changeSet;
        _field = ref field;
        _ca = _vsbs.Segment.CreateChunkAccessor(changeSet);
        _initialBufferId = field._bufferId;
    }

    /// <summary>Commits the pending buffer writes and releases the underlying chunk accessor.</summary>
    public void Dispose()
    {
        _ca.CommitChanges();
        _ca.Dispose();
    }

    /// <summary>
    /// Appends <paramref name="value"/> to the collection, allocating the backing buffer on first use. Under <see cref="StorageMode.Versioned"/> storage, a
    /// buffer shared with another revision (copy-on-write) is cloned before mutation; a solely-owned buffer is appended in place.
    /// </summary>
    /// <param name="value">Element to append.</param>
    public void Add(T value)
    {
        // First time adding an item?
        if (_field._bufferId == 0)
        {
            _field._bufferId = _vsbs.AllocateBuffer(ref _ca);
        }

        // Clone before mutating only if the buffer is SHARED with another revision — i.e. Versioned copy-on-write, where EcsVersionedCopyOnWrite's AddRef made
        // RefCounter > 1. When the buffer is solely owned (SingleVersion: no MVCC, no COW; or a buffer freshly built this transaction), mutate it in
        // place: cloning would orphan the original, which non-Versioned storage has no revision cleanup to release — a leak.
        else if (_initialBufferId == _field._bufferId && _vsbs.GetRefCounter(_initialBufferId, ref _ca) > 1)
        {
            _field._bufferId = _vsbs.CloneBuffer(_initialBufferId, ref _ca);

            // This revision now references the clone, not the shared original. Release the AddRef that EcsVersionedCopyOnWrite took on the original — otherwise
            // its RefCounter stays inflated and it leaks once the old revision is cleaned up (Bug #1).
            _vsbs.BufferRelease(_initialBufferId, ref _ca);
        }

        _vsbs.AddElement(_field._bufferId, value, ref _ca);
    }

    /// <summary>Total number of elements currently in the collection.</summary>
    public int ElementCount
    {
        get
        {
            using var a = new VariableSizedBufferAccessor<T, PersistentStore>(_vsbs, _field._bufferId);
            return a.TotalCount;
        }
    }

    /// <summary>Copies every element into <paramref name="dest"/>.</summary>
    /// <param name="dest">Destination span; must be at least <see cref="ElementCount"/> elements long.</param>
    /// <returns>The number of elements copied, or <c>0</c> when <paramref name="dest"/> is too small (in which case nothing is copied).</returns>
    public int GetAllElements(Span<T> dest)
    {
        using var a = new VariableSizedBufferAccessor<T, PersistentStore>(_vsbs, _field._bufferId);
        if (dest.Length < a.TotalCount)
        {
            return 0;
        }
        var destI = 0;
        do
        {
            var elements = a.ReadOnlyElements;
            elements.CopyTo(dest.Slice(destI));
            destI += elements.Length;
        } while (a.NextChunk());

        return destI;
    }

    /// <summary>Allocates and returns a new array containing every element of the collection.</summary>
    /// <returns>A newly allocated array of length <see cref="ElementCount"/>.</returns>
    public T[] GetAllElements()
    {
        using var a = new VariableSizedBufferAccessor<T, PersistentStore>(_vsbs, _field._bufferId);
        var dest = new T[a.TotalCount];

        var destI = 0;
        var destSpan = dest.AsSpan();
        do
        {
            var elements = a.ReadOnlyElements;
            elements.CopyTo(destSpan.Slice(destI));
            destI += elements.Length;
        } while (a.NextChunk());

        return dest;
    }
}
