using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>What a record produced by the projection pass is.</summary>
internal enum ReplicationRecordKind : byte
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>
    /// A full enter body: <c>body(onEnter) | body(group 0) | … | body(group G−1)</c>, every group and no mask (03 § 5). Produced when an entry is
    /// initialized — a new entity, a reused slot, or one watched now but not last tick.
    /// </summary>
    Enter = 1,

    /// <summary>
    /// A state body: the bodies of the groups that changed <i>this tick</i>, ascending, with the mask in <see cref="ReplicationRecordRef.GroupMask"/>.
    /// </summary>
    State = 2,
}

/// <summary>
/// One record the projection pass produced this tick: what it names, what kind it is, and where its pre-encoded body sits in the arena.
/// </summary>
/// <remarks>
/// Sixteen bytes, four to a cache line, so the frame stage walks a dense array rather than chasing per-record objects. Everything a frame needs in order to
/// place the record is here; the bytes themselves are never touched until they are copied.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal readonly struct ReplicationRecordRef
{
    /// <summary>The entity's network identity.</summary>
    public uint NetId { get; init; }

    /// <summary>Byte offset of the body inside the arena that produced this record.</summary>
    public int Offset { get; init; }

    /// <summary>The body's length in bytes.</summary>
    public ushort Length { get; init; }

    /// <summary>The identity's reuse generation, so a stale known-set entry is detectable without a second lookup (SUB-06).</summary>
    public ushort Generation { get; init; }

    /// <summary>Index of the archetype's plan in the runtime's plan list — which <c>ENTITIES</c> block the record belongs to.</summary>
    public ushort ArchetypeIndex { get; init; }

    /// <summary>What the body is.</summary>
    public ReplicationRecordKind Kind { get; init; }

    /// <summary>For a <see cref="ReplicationRecordKind.State"/> body, the groups it carries, as the wire's <c>u8</c> mask (W14); otherwise zero.</summary>
    public byte GroupMask { get; init; }
}

/// <summary>
/// One worker's records for one archetype for one tick: a native byte arena holding the pre-encoded bodies, and a dense index naming them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so appending needs no synchronization at all</b> — not a lock, not an interlocked bump. The projection pass partitions over watched blocks
/// and a block belongs to exactly one chunk, so the arena a chunk writes into is reached by one thread for the whole dispatch.
/// </para>
/// <para>
/// <b>Native, and grown by doubling, so the steady state allocates nothing managed</b> (SUB-07). The arena keeps its high-water capacity across ticks and
/// <see cref="Reset"/> only rewinds the two counts, so once the watched set stops growing no tick reaches the allocator at all — which is the exact shape
/// SUB-07's "structural growth is exempt" note describes.
/// </para>
/// <para>
/// <b>It also carries the pass's per-worker scratch</b> (<see cref="Codes"/> and <see cref="Scratch"/>). Those are working memory of the same thread with the
/// same lifetime and the same growth rule, and giving them a second owner would mean a second lifetime to get wrong for no gain.
/// </para>
/// </remarks>
internal sealed unsafe class RecordArena : IDisposable
{
    private byte* _bytes;
    private int _byteCapacity;
    private int _byteCount;

    private ReplicationRecordRef* _records;
    private int _recordCapacity;
    private int _recordCount;

    private uint* _codes;
    private int _codeCapacity;

    private byte* _scratch;
    private int _scratchCapacity;

    private bool _disposed;

    /// <summary>Records produced into this arena since the last <see cref="Reset"/>.</summary>
    public int Count => _recordCount;

    /// <summary>Bytes of body written since the last <see cref="Reset"/>.</summary>
    public int BytesUsed => _byteCount;

    /// <summary>Native bytes this arena holds, across every buffer, for the owner's resource accounting.</summary>
    public long EstimatedBytes =>
        _byteCapacity + ((long)_recordCapacity * sizeof(ReplicationRecordRef)) + ((long)_codeCapacity * sizeof(uint)) + _scratchCapacity;

    /// <summary>Rewinds the arena for a new tick, keeping every byte of capacity it has earned.</summary>
    public void Reset()
    {
        _byteCount = 0;
        _recordCount = 0;
    }

    /// <summary>
    /// The per-worker code scratch: one <c>uint</c> per (field ordinal, slot) pair, so a column walk writes a whole column and the per-slot encoders read
    /// down it.
    /// </summary>
    /// <param name="count">How many codes are needed — <c>fieldCount × 64</c>.</param>
    /// <returns>The scratch, at least <paramref name="count"/> long.</returns>
    public uint* Codes(int count)
    {
        if (count > _codeCapacity)
        {
            var capacity = _codeCapacity == 0 ? 256 : _codeCapacity;
            while (capacity < count)
            {
                capacity *= 2;
            }

            _codes = (uint*)NativeMemory.Realloc(_codes, (nuint)capacity * sizeof(uint));
            _codeCapacity = capacity;
        }

        return _codes;
    }

    /// <summary>The per-worker byte scratch a section body is encoded into before it is compared with the stored copy.</summary>
    /// <param name="count">Bytes needed.</param>
    /// <returns>The scratch, at least <paramref name="count"/> long.</returns>
    public byte* Scratch(int count)
    {
        if (count > _scratchCapacity)
        {
            var capacity = _scratchCapacity == 0 ? 256 : _scratchCapacity;
            while (capacity < count)
            {
                capacity *= 2;
            }

            _scratch = (byte*)NativeMemory.Realloc(_scratch, (nuint)capacity);
            _scratchCapacity = capacity;
        }

        return _scratch;
    }

    /// <summary>Reserves <paramref name="bytes"/> of body and returns where they will live.</summary>
    /// <param name="bytes">The body's length.</param>
    /// <param name="offset">Receives the body's offset in this arena, which is what <see cref="Append"/> is given.</param>
    /// <returns>The body's bytes, to be written in full before <see cref="Append"/> is called.</returns>
    public Span<byte> Reserve(int bytes, out int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        if (_byteCount + bytes > _byteCapacity)
        {
            var capacity = _byteCapacity == 0 ? 4096 : _byteCapacity;
            while (capacity < _byteCount + bytes)
            {
                capacity *= 2;
            }

            _bytes = (byte*)NativeMemory.Realloc(_bytes, (nuint)capacity);
            _byteCapacity = capacity;
        }

        offset = _byteCount;
        _byteCount += bytes;
        return new Span<byte>(_bytes + offset, bytes);
    }

    /// <summary>
    /// Gives back the unused tail of the most recent <see cref="Reserve"/>, so a writer that could only BOUND what it would produce does not leave the
    /// difference behind.
    /// </summary>
    /// <param name="offset">The offset that reservation returned.</param>
    /// <param name="used">Bytes actually written.</param>
    /// <remarks>
    /// <b>Only the last reservation can be trimmed</b>, which the offset check enforces rather than assumes: the arena is a bump allocator, so rewinding
    /// past anything else would hand the same bytes out twice. A caller that has reserved again since is silently left alone, because the alternative —
    /// throwing on a path the projection takes per cluster — would turn a wasted kilobyte into a faulted stage.
    /// </remarks>
    public void TrimReserve(int offset, int used)
    {
        if (offset >= 0 && used >= 0 && offset + used <= _byteCount)
        {
            _byteCount = offset + used;
        }
    }

    /// <summary>Names a body that has just been written through <see cref="Reserve"/>.</summary>
    /// <param name="record">The record.</param>
    public void Append(in ReplicationRecordRef record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recordCount == _recordCapacity)
        {
            var capacity = _recordCapacity == 0 ? 64 : _recordCapacity * 2;
            _records = (ReplicationRecordRef*)NativeMemory.Realloc(_records, (nuint)capacity * (nuint)sizeof(ReplicationRecordRef));
            _recordCapacity = capacity;
        }

        _records[_recordCount++] = record;
    }

    /// <summary>The record at <paramref name="index"/>.</summary>
    public ref readonly ReplicationRecordRef Record(int index)
    {
        if ((uint)index >= (uint)_recordCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the records produced this tick");
        }

        return ref _records[index];
    }

    /// <summary>
    /// The byte at <paramref name="offset"/>, for a caller that named a region of this arena by offset rather than by record.
    /// </summary>
    /// <param name="offset">An offset a <see cref="Reserve"/> returned this tick.</param>
    /// <returns>Its address, valid until the next <see cref="Reset"/> or growth.</returns>
    /// <remarks>
    /// <b>Resolved on read and never cached</b>, because <see cref="Reserve"/> reallocates. The frame stage calls this after the projection stage has
    /// joined, so nothing can grow the arena between the address being taken and the bytes being copied out of it.
    /// </remarks>
    public byte* At(int offset) => _bytes + offset;

    /// <summary>The pre-encoded bytes of <paramref name="record"/>, valid until the next <see cref="Reset"/>.</summary>
    /// <param name="record">A record this arena produced.</param>
    /// <returns>Its body.</returns>
    public ReadOnlySpan<byte> Body(in ReplicationRecordRef record) => new(_bytes + record.Offset, record.Length);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.Free(_bytes);
        NativeMemory.Free(_records);
        NativeMemory.Free(_codes);
        NativeMemory.Free(_scratch);
        _bytes = null;
        _records = null;
        _codes = null;
        _scratch = null;
        _byteCapacity = _recordCapacity = _codeCapacity = _scratchCapacity = 0;
        _byteCount = _recordCount = 0;
    }
}

/// <summary>
/// One <see cref="RecordArena"/> per worker of one archetype, sized at the track's single-threaded blocks step from the chunk count S1 is about to dispatch.
/// </summary>
/// <remarks>
/// The arenas are kept, never recreated: the set grows to the widest chunk count the runtime has ever used and stays there, so a tick's cost is a pair of
/// counter resets per worker. Indexing is by chunk index, which is what makes "one writer per arena" true without a word of synchronization.
/// </remarks>
internal sealed class RecordArenaSet : IDisposable
{
    private RecordArena[] _arenas = [];
    private bool _disposed;

    /// <summary>Arenas currently available, one per chunk index.</summary>
    public int Count => _arenas.Length;

    /// <summary>The arena a chunk writes into.</summary>
    public RecordArena this[int chunkIndex]
    {
        get
        {
            if ((uint)chunkIndex >= (uint)_arenas.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "No arena exists for this chunk; EnsureWorkers was not called for it");
            }

            return _arenas[chunkIndex];
        }
    }

    /// <summary>Records across every arena. Read after the dispatch.</summary>
    public int TotalRecords
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _arenas.Length; i++)
            {
                total += _arenas[i].Count;
            }

            return total;
        }
    }

    /// <summary>Native bytes the arenas hold, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                bytes += _arenas[i].EstimatedBytes;
            }

            return bytes;
        }
    }

    /// <summary>Grows the set to <paramref name="workers"/> arenas and rewinds every one of them. Single-threaded, before the dispatch.</summary>
    /// <param name="workers">The chunk count S1 is about to dispatch.</param>
    public void BeginTick(int workers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(workers);

        if (workers > _arenas.Length)
        {
            var grown = new RecordArena[workers];
            Array.Copy(_arenas, grown, _arenas.Length);
            for (var i = _arenas.Length; i < workers; i++)
            {
                grown[i] = new RecordArena();
            }

            _arenas = grown;
        }

        for (var i = 0; i < _arenas.Length; i++)
        {
            _arenas[i].Reset();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _arenas.Length; i++)
        {
            _arenas[i]?.Dispose();
        }

        _arenas = [];
    }
}
