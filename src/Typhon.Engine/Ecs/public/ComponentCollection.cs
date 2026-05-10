using JetBrains.Annotations;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
public ref struct ComponentCollectionAccessor<T> : IDisposable where T : unmanaged
{
    private VariableSizedBufferSegment<T, PersistentStore> _vsbs;
    private ref ComponentCollection<T> _field;
    private ChunkAccessor<PersistentStore> _ca;
    private readonly int _initialBufferId;
    private readonly ChangeSet _changeSet;

    public ComponentCollectionAccessor(ChangeSet changeSet, VariableSizedBufferSegment<T, PersistentStore> vsbs, ref ComponentCollection<T> field)
    {
        _vsbs = vsbs;
        _changeSet = changeSet;
        _field = ref field;
        _ca = _vsbs.Segment.CreateChunkAccessor(changeSet);
        _initialBufferId = field._bufferId;
    }

    public void Dispose()
    {
        _ca.CommitChanges();
        _ca.Dispose();
    }

    public void Add(T value)
    {
        // First time adding an item?
        if (_field._bufferId == 0)
        {
            _field._bufferId = _vsbs.AllocateBuffer(ref _ca);
        }

        // Need to clone the buffer as we mutate its content
        else if (_initialBufferId == _field._bufferId)
        {
            _field._bufferId = _vsbs.CloneBuffer(_initialBufferId, ref _ca);
        }

        _vsbs.AddElement(_field._bufferId, value, ref _ca);
    }

    public int ElementCount
    {
        get
        {
            using var a = new VariableSizedBufferAccessor<T, PersistentStore>(_vsbs, _field._bufferId);
            return a.TotalCount;
        }
    }

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
