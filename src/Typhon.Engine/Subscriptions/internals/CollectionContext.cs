using System;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One projection worker's collection reader for one archetype (W34, 13 § 6.5): a chunk accessor per collection's buffer segment, a batch of raw elements
/// and their codes, and the netIds the collections it encoded named — what the reverse index is told of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads outside the cluster layout, in the replication track only</b> (SUB-01 amended): a collection's handle is read through the layout, its elements
/// through its buffer segment. The track runs inside the tick's exclusive window (EW-01), so nothing writes a buffer while it is read, and the chunk walk
/// takes no lock.
/// </para>
/// <para>
/// <b>Opened for a worker's share, closed after it.</b> An accessor pins the pages it touches, so it lives as long as the share; one is made only for a
/// collection the share actually reads.
/// </para>
/// </remarks>
internal sealed unsafe class CollectionContext
{
    private readonly ArchetypeReplicationState _state;
    private readonly ChunkAccessor<PersistentStore>[] _accessors;
    private readonly bool[] _open;
    private uint[] _netIds = new uint[64];

    internal CollectionContext(ArchetypeReplicationState state, int collections)
    {
        _state = state;
        _accessors = new ChunkAccessor<PersistentStore>[collections];
        _open = new bool[collections];
    }

    // Pointers, not spans (13 § 6.4): these are fields of a class shared by a worker's whole chunk, which a span cannot be. They address the worker's
    // ProjectionScratch, native memory the context never outlives, and every walk over them is bounded by MaxSlots and the element's size.

    /// <summary>A batch of raw elements: <see cref="ProjectionPass.MaxSlots"/> of the archetype's widest element.</summary>
    public byte* ElementBytes { get; private set; }

    /// <summary>The batch's codes: one row of <see cref="ProjectionPass.MaxSlots"/> per element field of the widest element.</summary>
    public ulong* ElementCodes { get; private set; }

    /// <summary>The element pack's bytes.</summary>
    public byte* ElementPack { get; private set; }

    /// <summary>Bytes of <see cref="ElementPack"/>.</summary>
    public int ElementPackBytes { get; private set; }

    /// <summary>A reference in an element named an entity with no identity yet: its entity is pushed again (13 § 5).</summary>
    public bool Unresolved { get; set; }

    /// <summary>How many netIds the collections encoded since <see cref="ResetNetIds"/> named; occurrences, not distinct ids.</summary>
    public int NetIdCount { get; private set; }

    /// <summary>The netIds named since <see cref="ResetNetIds"/>.</summary>
    public Span<uint> NetIds => _netIds.AsSpan(0, NetIdCount);

    /// <summary>Carves the batch out of the worker's collection scratch for one block.</summary>
    public void Bind(ProjectionScratch scratch, CompiledProjectionPlan plan)
    {
        var size = 0;
        var fields = 0;
        var pack = 0;
        foreach (var collection in plan.Collections)
        {
            size = Math.Max(size, collection.ElementSize);
            fields = Math.Max(fields, collection.Fields.Length);
            pack = Math.Max(pack, collection.Section.PackBytes);
        }

        var codeBytes = fields * ProjectionPass.MaxSlots * sizeof(ulong);
        var elementBytes = (size * ProjectionPass.MaxSlots + 7) & ~7;
        var bytes = scratch.Collection(codeBytes + elementBytes + pack + 8);
        ElementCodes = (ulong*)bytes;
        ElementBytes = bytes + codeBytes;
        ElementPack = bytes + codeBytes + elementBytes;
        ElementPackBytes = pack;
    }

    /// <summary>The accessor over collection <paramref name="index"/>'s buffer segment, made on first use.</summary>
    public ref ChunkAccessor<PersistentStore> Accessor(int index)
    {
        if (!_open[index])
        {
            _accessors[index] = _state.CollectionSegments[index].Segment.CreateChunkAccessor();
            _open[index] = true;
        }

        return ref _accessors[index];
    }

    /// <summary>Records a netId an element names.</summary>
    public void AddNetId(uint netId)
    {
        if (NetIdCount == _netIds.Length)
        {
            Array.Resize(ref _netIds, _netIds.Length * 2);
        }

        _netIds[NetIdCount++] = netId;
    }

    /// <summary>Starts a section's encode: no netId named yet.</summary>
    public void ResetNetIds() => NetIdCount = 0;

    private uint[] _before = new uint[64];

    /// <summary>
    /// The netIds the collections of the body stored at <paramref name="reference"/> name — decoded with the catalog's own <paramref name="section"/>,
    /// so what is read back is what a client reads. Empty when nothing is stored.
    /// </summary>
    public Span<uint> NamedBy(WideBodyArena arena, byte* reference, SectionPlan section)
    {
        var handle = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(reference);
        var length = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(reference + sizeof(uint));
        if (handle == 0 || arena == null)
        {
            return default;
        }

        var collector = new CollectionReferenceCollector { Buffer = _before };
        try
        {
            var reader = new WireReader(arena.Read(handle, length));
            FieldCodec.ReadSection(ref reader, section, 0, ref collector);
        }
        catch (WireFormatException)
        {
            // Bytes this projection wrote, read with the catalog that describes them: unreachable, and the tick path never throws.
            _state.NoteCollectionDecodeFault();
            return default;
        }

        _before = collector.Buffer;
        return _before.AsSpan(0, collector.Count);
    }

    /// <summary>Releases the accessors: the share is over.</summary>
    public void Close()
    {
        for (var i = 0; i < _accessors.Length; i++)
        {
            if (_open[i])
            {
                _accessors[i].Dispose();
                _accessors[i] = default;
                _open[i] = false;
            }
        }
    }
}

/// <summary>
/// Collects, from a decoded section body, the netIds its collections' elements name (13 § 5): what an entity's stored body named before it changed, or
/// before its entry ended. A top-level reference is the held netIds' business, not this.
/// </summary>
internal struct CollectionReferenceCollector : IFieldSink
{
    public uint[] Buffer;
    public int Count;

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (field.Parent == null || field.Kind != CodecKind.EntityRef || components[0] == 0)
        {
            return;
        }

        if (Count == Buffer.Length)
        {
            Array.Resize(ref Buffer, Buffer.Length * 2);
        }

        Buffer[Count++] = (uint)components[0];
    }

    public readonly void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
    }

    public readonly void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
    }

    public readonly void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
    }

    public readonly void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
    }

    public readonly void Collection(FieldPlan field, int total, int sent)
    {
    }

    public readonly void CollectionElement(FieldPlan field, int index)
    {
    }
}
