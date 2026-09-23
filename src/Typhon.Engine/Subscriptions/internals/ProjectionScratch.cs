using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One projection worker's working memory: the code scratch a column walk writes a whole column into, and the byte scratch a section body is encoded into
/// before it is compared with the stored copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so it needs no synchronization at all.</b> The projection pass partitions over blocks and a block belongs to exactly one chunk, so the
/// scratch a chunk uses is reached by one thread for the whole dispatch.
/// </para>
/// <para>
/// <b>Native, and grown by doubling, so the steady state allocates nothing managed</b> (SUB-07). The high-water capacity is kept across ticks.
/// </para>
/// </remarks>
internal sealed unsafe class ProjectionScratch : IDisposable
{
    private uint* _codes;
    private int _codeCapacity;

    private byte* _scratch;
    private int _scratchCapacity;

    private bool _disposed;

    /// <summary>Native bytes this scratch holds, for the owner's resource accounting.</summary>
    public long EstimatedBytes => ((long)_codeCapacity * sizeof(uint)) + _scratchCapacity;

    /// <summary>
    /// The code scratch: one <c>uint</c> per (field ordinal, slot) pair, so a column walk writes a whole column and the per-slot encoders read down it.
    /// </summary>
    /// <param name="count">How many codes are needed — <c>fieldCount × 64</c>.</param>
    /// <returns>The scratch, at least <paramref name="count"/> long.</returns>
    public uint* Codes(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary>The byte scratch a section body is encoded into before it is compared with the stored copy.</summary>
    /// <param name="count">Bytes needed.</param>
    /// <returns>The scratch, at least <paramref name="count"/> long.</returns>
    public byte* Scratch(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMemory.Free(_codes);
        NativeMemory.Free(_scratch);
        _codes = null;
        _scratch = null;
        _codeCapacity = _scratchCapacity = 0;
    }
}

/// <summary>
/// One <see cref="ProjectionScratch"/> per worker of one archetype, grown at the track's single-threaded blocks step to the chunk count the projection is
/// about to dispatch.
/// </summary>
/// <remarks>
/// Kept, never recreated: the set grows to the widest chunk count the runtime has used and stays there. Indexing is by chunk index, which is what makes
/// "one writer per scratch" true without a word of synchronization.
/// </remarks>
internal sealed class ProjectionScratchSet : IDisposable
{
    private ProjectionScratch[] _workers = [];
    private bool _disposed;

    /// <summary>Scratches currently available, one per chunk index.</summary>
    public int Count => _workers.Length;

    /// <summary>The scratch a chunk uses.</summary>
    /// <param name="chunkIndex">The chunk.</param>
    public ProjectionScratch this[int chunkIndex]
    {
        get
        {
            if ((uint)chunkIndex >= (uint)_workers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "No scratch exists for this chunk; BeginTick was not called for it");
            }

            return _workers[chunkIndex];
        }
    }

    /// <summary>Native bytes the scratches hold, for the owner's resource accounting.</summary>
    public long EstimatedBytes
    {
        get
        {
            var bytes = 0L;
            for (var i = 0; i < _workers.Length; i++)
            {
                bytes += _workers[i].EstimatedBytes;
            }

            return bytes;
        }
    }

    /// <summary>Grows the set to <paramref name="workers"/> scratches. Single-threaded, before the dispatch.</summary>
    /// <param name="workers">The chunk count the projection is about to dispatch.</param>
    public void BeginTick(int workers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(workers);

        if (workers > _workers.Length)
        {
            var grown = new ProjectionScratch[workers];
            Array.Copy(_workers, grown, _workers.Length);
            for (var i = _workers.Length; i < workers; i++)
            {
                grown[i] = new ProjectionScratch();
            }

            _workers = grown;
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
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i]?.Dispose();
        }

        _workers = [];
    }
}
