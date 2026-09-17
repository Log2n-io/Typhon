using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A <see cref="ReadOnlyMemory{T}"/> view over engine-owned native memory, so an encoded frame reaches a link without a pointer into GC memory anywhere on
/// the path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists at all.</b> <see cref="ISubscriptionLink.SendAsync"/> takes a <see cref="ReadOnlyMemory{T}"/>, because every asynchronous socket and
/// WebSocket API in .NET does, and a <see cref="Memory{T}"/> cannot be built over a raw pointer by any other route. The alternatives are both wrong here:
/// copying each frame into a pooled managed array doubles the bytes touched per send for no gain, and pinning a managed array to hand out its address is the
/// pattern the engine forbids outright — the pinned object heap stops the GC <i>moving</i> an array, not <i>freeing</i> it, and a buffer referenced only
/// through such a pointer has already been collected mid-use once in this codebase.
/// </para>
/// <para>
/// <b>Two lifetimes, one type.</b> A frame's bytes belong to the frame pool, which rents and returns blocks on its own schedule, so the common case is a
/// <b>non-owning</b> view: <see cref="Reset"/> re-points one manager at the next block and the pool decides when the memory dies. A control message — a
/// <c>WELCOME</c>, a <c>PONG</c>, a <c>KICK</c> — has no pool behind it and no reuse worth arranging, so <see cref="Allocate"/> makes an <b>owning</b> view
/// that frees its allocation on <see cref="Release"/>. Which one a manager is, is fixed when it is built.
/// </para>
/// <para>
/// <b>Lifetime is the caller's whole responsibility.</b> A <see cref="Memory{T}"/> handed out here stays valid exactly as long as the memory behind it: until
/// the pool takes the block back, or until this manager is disposed. That is the same contract <see cref="ISubscriptionLink.SendAsync"/> already states, which
/// is why the send path awaits the task before it releases anything.
/// </para>
/// </remarks>
internal sealed unsafe class NativeFrameMemoryManager : MemoryManager<byte>
{
    private byte* _pointer;
    private int _length;
    private readonly bool _owned;

    /// <summary>
    /// Creates a view over memory somebody else owns.
    /// </summary>
    /// <param name="pointer">The first byte. Native memory — page cache, the engine's allocator, or a native heap block — never a managed object.</param>
    /// <param name="length">How many bytes are addressable from it.</param>
    public NativeFrameMemoryManager(byte* pointer, int length)
    {
        Validate(pointer, length);
        _pointer = pointer;
        _length = length;
        _owned = false;
    }

    private NativeFrameMemoryManager(byte* pointer, int length, bool owned)
    {
        _pointer = pointer;
        _length = length;
        _owned = owned;
    }

    /// <summary>How many bytes this view covers. Zero once it has been released or reset to nothing.</summary>
    public int Length => _length;

    /// <summary>
    /// Drops the view, freeing the memory when this manager owns it.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryManager{T}"/> implements <see cref="IDisposable"/> explicitly, so <c>Dispose()</c> is not reachable on the concrete type and
    /// <c>buffer.Dispose()</c> binds to the protected overload instead — a compile error that reads as an accessibility problem rather than as the interface
    /// dispatch it is. Naming the operation avoids that trap at every call site, and reads better beside <see cref="Allocate"/>.
    /// </remarks>
    public void Release() => ((IDisposable)this).Dispose();

    /// <summary>
    /// Allocates <paramref name="length"/> bytes of native memory and returns a view that frees them when it is disposed.
    /// </summary>
    /// <param name="length">The size, in bytes. Must be positive.</param>
    /// <returns>The owning view.</returns>
    /// <remarks>
    /// Aligned to a cache line, so a message the engine is writing never shares a line with anything else being written at the same time. This is for the
    /// handful of control messages a connection sends, not for frames: frames come from the frame pool, under its budget, and are viewed with the
    /// non-owning constructor.
    /// </remarks>
    public static NativeFrameMemoryManager Allocate(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A message buffer is at least one byte.");
        }

        var pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, 64);
        return new NativeFrameMemoryManager(pointer, length, owned: true);
    }

    /// <summary>
    /// Re-points a non-owning view at another block.
    /// </summary>
    /// <param name="pointer">The first byte of the new block.</param>
    /// <param name="length">Its length in bytes.</param>
    /// <remarks>
    /// <b>Only safe once the previous block's send has completed</b>, because every <see cref="Memory{T}"/> this manager ever handed out now addresses the new
    /// block. That is exactly the rule the send path already obeys — one send in flight per session — which is what makes one manager per session enough and
    /// keeps a per-frame allocation off the path.
    /// </remarks>
    public void Reset(byte* pointer, int length)
    {
        if (_owned)
        {
            throw new InvalidOperationException("An owning frame view cannot be re-pointed: it would leak the allocation it is holding.");
        }

        Validate(pointer, length);
        _pointer = pointer;
        _length = length;
    }

    /// <inheritdoc />
    public override Span<byte> GetSpan() => new(_pointer, _length);

    /// <inheritdoc />
    /// <remarks>The memory is native and never moves, so pinning is a bounds check and an address. There is nothing to unpin.</remarks>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex), elementIndex, "The index is outside the frame.");
        }

        return new MemoryHandle(_pointer + elementIndex);
    }

    /// <inheritdoc />
    public override void Unpin()
    {
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        var pointer = _pointer;
        _pointer = null;
        _length = 0;

        if (_owned && pointer != null)
        {
            NativeMemory.AlignedFree(pointer);
        }
    }

    private static void Validate(byte* pointer, int length)
    {
        if (pointer == null)
        {
            throw new ArgumentNullException(nameof(pointer));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A frame view cannot be negative.");
        }
    }
}
