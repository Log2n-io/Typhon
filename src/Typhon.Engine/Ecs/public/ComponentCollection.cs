using JetBrains.Annotations;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Mutable accessor for a collection-typed component field — the variable-sized element list backing a <see cref="ComponentCollection{T}"/> value. Appends
/// and replacements reach the owning field by <c>ref</c>, transparently performing copy-on-write when the buffer is shared across MVCC revisions. Must be
/// disposed; disposal commits the pending buffer writes.
/// </summary>
/// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
[MustDispose(Reason = "owns a ChunkAccessor ('_ca'): until disposed its pages stay pinned and registered as written, and the checkpoint cannot cover them (CP-13)")]
[NoCopy(Reason = "holds a ChunkAccessor by value ('_ca'): a copy duplicates its slots and pins")]
[PublicAPI]
public unsafe ref struct ComponentCollectionAccessor<T> : IDisposable where T : unmanaged
{
    private VariableSizedBufferSegment<T, PersistentStore> _vsbs;
    private ref ComponentCollection<T> _field;
    private ChunkAccessor<PersistentStore> _ca;
    private readonly int _initialBufferId;
    private readonly ChangeSet _changeSet;

    // Write-back (#1199): an accessor made by EntityRefMut.CreateComponentCollectionAccessor edits a COPY of a stored component, so it stores the buffer it
    // moves to into the component itself, at the moment it moves (#1362) — not at Dispose. An exception between a move and the Dispose (the accessor is a
    // ref struct, disposed by a `using` the exception skips past) rolled the transaction back with the stored value naming a buffer the move had released,
    // and a Versioned rollback releases what the new revision names: the committed revision's buffer. Dispose remains mandatory — it gives the page holds
    // back. Null for an accessor on a value not stored yet.
    private readonly EntityAccessor _writeBackOwner;
    private readonly EntityId _writeBackEntity;
    private readonly int _writeBackComponentTypeId;
    private readonly int _writeBackOffset;
    private readonly delegate*<EntityAccessor, EntityId, int, int, int, void> _writeBack;

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

    /// <summary>An accessor on a copy of a stored component, which writes every buffer id it moves to back into that component (#1199, #1362).</summary>
    internal ComponentCollectionAccessor(ChangeSet changeSet, VariableSizedBufferSegment<T, PersistentStore> vsbs, ref ComponentCollection<T> field,
        EntityAccessor owner, EntityId entity, int componentTypeId, int fieldOffset, delegate*<EntityAccessor, EntityId, int, int, int, void> writeBack)
        : this(changeSet, vsbs, ref field)
    {
        _writeBackOwner = owner;
        _writeBackEntity = entity;
        _writeBackComponentTypeId = componentTypeId;
        _writeBackOffset = fieldOffset;
        _writeBack = writeBack;
    }

    /// <summary>Commits the pending buffer writes and releases the underlying chunk accessor.</summary>
    public void Dispose()
    {
        _ca.CommitChanges();
        _ca.Dispose();
    }

    /// <summary>
    /// Appends <paramref name="value"/> to the collection, allocating the backing buffer on first use. A buffer shared with another revision (Versioned
    /// copy-on-write) or with the committed value (a SingleVersion write staged under the Commit discipline) is cloned before mutation; a solely-owned buffer
    /// is appended in place.
    /// </summary>
    /// <param name="value">Element to append.</param>
    public void Add(T value)
    {
        var bufferId = _field._bufferId;

        // First time adding an item?
        if (bufferId == 0)
        {
            MoveTo(_vsbs.AllocateBuffer(ref _ca));
        }

        // Clone before mutating only if the buffer is SHARED — RefCounter > 1: Versioned copy-on-write, where EcsVersionedCopyOnWrite's AddRef shares it with
        // the committed revision, or a Commit-discipline staged value, whose staging AddRef shares it with HEAD (#1362). When the buffer is solely owned
        // (SingleVersion in place; or a buffer freshly built this transaction), mutate it in place: cloning would orphan the original, which non-Versioned
        // storage has no revision cleanup to release — a leak.
        else if (bufferId == _initialBufferId && _vsbs.GetRefCounter(bufferId, ref _ca) > 1)
        {
            MoveTo(_vsbs.CloneBuffer(bufferId, ref _ca));

            // This revision now references the clone, not the shared original. Release the AddRef that EcsVersionedCopyOnWrite took on the original — otherwise
            // its RefCounter stays inflated and it leaks once the old revision is cleaned up (Bug #1).
            _vsbs.BufferRelease(bufferId, ref _ca);
        }

        _vsbs.AddElement(_field._bufferId, value, ref _ca);
    }

    /// <summary>
    /// Replaces the collection's whole content with <paramref name="elements"/>, in order; an empty span is <see cref="Clear"/>. The previous buffer is never
    /// written, only given back one reference: one shared with an older revision or with the committed value stays theirs, so a snapshot reading it still
    /// sees the old elements; a solely-owned one is freed.
    /// </summary>
    /// <remarks>
    /// O(<paramref name="elements"/>.Length): the content goes into a fresh buffer. Through an entity handle the stored component names the new buffer as soon
    /// as this returns, and the write is recorded as a <c>Set</c>'s is. On a SingleVersion component outside the Commit discipline the previous buffer is freed
    /// at once and a rollback does not bring it back — TickFence writes are not undone.
    /// </remarks>
    /// <param name="elements">The new content.</param>
    /// <exception cref="InvalidOperationException">An accessor made by <c>Transaction.CreateComponentCollectionAccessor</c>, over a collection it did not
    /// build: that accessor cannot tell a value being built from a copy of a stored one, whose buffer the release would free under it.</exception>
    public void Replace(ReadOnlySpan<T> elements)
    {
        var bufferId = _field._bufferId;
        if (_writeBack == null && bufferId != 0 && bufferId == _initialBufferId)
        {
            ThrowHelper.ThrowInvalidOp("Replace or Clear on a collection this accessor did not build: change a stored component's collection through "
                + "EntityRefMut.CreateComponentCollectionAccessor, which knows who holds the buffer.");
        }

        // The new buffer first, stored; the old one released last. A write-back that throws then leaves a leak rather than a stored value naming a buffer
        // this accessor gave back.
        var newBufferId = _vsbs.SetElements(0, elements, ref _ca);
        MoveTo(newBufferId);
        if (bufferId != 0)
        {
            _vsbs.BufferRelease(bufferId, ref _ca);
        }
    }

    /// <summary>Removes every element: the collection ends with no buffer. The previous buffer is released as <see cref="Replace"/> releases it.</summary>
    public void Clear() => Replace(ReadOnlySpan<T>.Empty);

    /// <summary>
    /// Points the field at <paramref name="bufferId"/> and, for an accessor made through an entity handle, the stored component too — at once, so no exit
    /// leaves the stored value naming a buffer this accessor has released (#1362).
    /// </summary>
    private void MoveTo(int bufferId)
    {
        _field._bufferId = bufferId;
        if (_writeBack != null)
        {
            _writeBack(_writeBackOwner, _writeBackEntity, _writeBackComponentTypeId, _writeBackOffset, bufferId);
        }
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
