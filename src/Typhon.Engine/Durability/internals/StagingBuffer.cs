using JetBrains.Annotations;
using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// RAII wrapper for a staging buffer rented from <see cref="StagingBufferPool"/>. Provides access
/// to an 8KB page-sized buffer for checkpoint and backup operations. Automatically returns the
/// buffer to the pool on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Declared as <c>ref struct</c> to prevent heap escape — the buffer reference is only valid during
/// the current stack frame. Use with a <c>using</c> statement to ensure timely return.
/// </para>
/// </remarks>
[PublicAPI]
internal readonly ref struct StagingBuffer
{
    private readonly StagingBufferPool _pool;

    /// <summary>Slot index within the owning pool. Internal — used for testing and diagnostics.</summary>
    internal readonly int SlotIndex;

    /// <summary>Writable 8KB buffer for staging page data.</summary>
    public readonly Span<byte> Span;

    /// <summary>Raw pointer to the buffer start, for unsafe interop.</summary>
    public readonly unsafe byte* Pointer;

    /// <summary>
    /// <c>true</c> if this buffer was successfully rented and is ready for use.
    /// </summary>
    public bool IsValid => _pool != null;

    internal unsafe StagingBuffer(StagingBufferPool pool, int slotIndex, byte* pointer, int bufferSize)
    {
        _pool = pool;
        SlotIndex = slotIndex;
        Pointer = pointer;
        Span = new Span<byte>(pointer, bufferSize);
    }

    /// <summary>
    /// Returns the buffer to the owning pool. Must be called exactly once (typically via <c>using</c>).
    /// </summary>
    public void Dispose()
    {
        if (_pool != null)
        {
            _pool.Return(SlotIndex);
        }
    }
}
