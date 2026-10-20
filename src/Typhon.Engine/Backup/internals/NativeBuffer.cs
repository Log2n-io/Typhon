using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>Where a backup's I/O buffers come from: an engine's allocator under a resource, or <c>null</c> for an engine-free tool.</summary>
/// <param name="Allocator">The engine's allocator.</param>
/// <param name="Parent">The resource the buffers are children of (the backup coordinator).</param>
internal sealed record BufferOwner(IMemoryAllocator Allocator, IResource Parent);

/// <summary>
/// A page-aligned block of native memory, freed deterministically by <see cref="IDisposable.Dispose"/>, exposed as <see cref="Span{T}"/> and as
/// <see cref="Memory{T}"/> so an asynchronous file write can use it. The backup's I/O buffers live here, never in the GC heap: a 4 MiB array
/// per buffer per backup would be large-object garbage on every point.
/// </summary>
/// <remarks>
/// <para>
/// Two origins. Inside an engine (online capture) the block comes from <see cref="IMemoryAllocator.AllocatePinned"/>, a child of the backup
/// coordinator, so the resource tree and the unmanaged-memory gauges see it. A tool with no engine (restore, verify, offline capture from the CLI)
/// has no resource tree to account it in; it allocates directly, per operation, and frees at dispose.
/// </para>
/// <para>The owner must not dispose it while a write that uses <see cref="MemoryManager{T}.Memory"/> is in flight.</para>
/// </remarks>
internal sealed unsafe class NativeBuffer : MemoryManager<byte>
{
    private byte* _pointer;
    private PinnedMemoryBlock _block;

    private NativeBuffer(int length, byte* pointer, PinnedMemoryBlock block)
    {
        Length = length;
        _pointer = pointer;
        _block = block;
    }

    /// <summary>
    /// A buffer of <paramref name="length"/> bytes aligned on <paramref name="alignment"/>: the engine's, named <paramref name="id"/> under
    /// <paramref name="owner"/>'s parent, when there is an owner; a direct allocation otherwise.
    /// </summary>
    public static NativeBuffer Allocate(int length, BufferOwner owner, string id, int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (owner != null)
        {
            var block = owner.Allocator.AllocatePinned(id, owner.Parent, length, zeroed: false, alignment);
            return new NativeBuffer(length, block.DataAsPointer, block);
        }

        // native-alloc: an engine-free tool (CLI restore, verify, offline capture) has no allocator or resource tree; per-operation, freed at dispose.
        return new NativeBuffer(length, (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment), null);
    }

    public int Length { get; }

    public Span<byte> Span => GetSpan();

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_pointer == null, this);
        return new Span<byte>(_pointer, Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer == null)
        {
            return;
        }

        if (_block != null)
        {
            _block.Dispose();
            _block = null;
        }
        else
        {
            NativeMemory.AlignedFree(_pointer);
        }

        _pointer = null;
    }
}
