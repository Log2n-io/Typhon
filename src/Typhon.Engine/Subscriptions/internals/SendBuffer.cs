using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Pre-allocated circular byte buffer for a single client's outbound TCP data.
/// Single-producer (Output phase thread writes), single-consumer (I/O flush thread reads).
/// </summary>
/// <remarks>
/// <para>The buffer is allocated on the Pinned Object Heap (immovable by GC), safe for <c>Socket.Send</c>.</para>
/// <para>SPSC: producer writes <c>_tail</c> and reads <c>_head</c>; consumer writes <c>_head</c> and reads <c>_tail</c>.
/// On x64, reads and writes of ≤64-bit primitives are naturally atomic — no memory barriers needed.</para>
/// </remarks>
internal sealed class SendBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _capacity;

    // Producer (Output phase) writes _tail; consumer (I/O thread) writes _head.
    // Each only reads the other's variable. Naturally atomic on x64.
    private int _head; // Consumer advances after Socket.Send
    private int _tail; // Producer advances after writing data

    private int _disposed;

    public SendBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _capacity = capacity;
        // Pinned Object Heap — immovable by GC, safe for Socket.Send without additional pinning
        _buffer = GC.AllocateArray<byte>(capacity, true);
    }

    /// <summary>Total buffer capacity in bytes.</summary>
    public int Capacity => _capacity;

    /// <summary>Number of bytes currently pending (written but not yet sent).</summary>
    public int PendingBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var head = _head;
            var tail = _tail;
            return tail >= head ? tail - head : _capacity - head + tail;
        }
    }

    /// <summary>Fill percentage (0.0 – 1.0).</summary>
    public float FillPercentage => (float)PendingBytes / _capacity;

    /// <summary>Available space for writing.</summary>
    public int AvailableBytes => _capacity - PendingBytes - 1; // -1: full vs empty disambiguation

    /// <summary>True if the buffer has no pending data.</summary>
    public bool IsEmpty => _head == _tail;

    /// <summary>
    /// Try to write data into the buffer. Called by the Output phase (producer).
    /// </summary>
    /// <returns>True if the data was written; false if insufficient space (backpressure).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return true;
        }

        if (data.Length > AvailableBytes)
        {
            return false;
        }

        var tail = _tail;

        if (tail + data.Length <= _capacity)
        {
            // Contiguous write
            data.CopyTo(_buffer.AsSpan(tail));
        }
        else
        {
            // Wrap-around write
            var firstChunk = _capacity - tail;
            data[..firstChunk].CopyTo(_buffer.AsSpan(tail));
            data[firstChunk..].CopyTo(_buffer.AsSpan(0));
        }

        _tail = (tail + data.Length) % _capacity;
        return true;
    }

    /// <summary>
    /// Get a contiguous read segment for the I/O thread (consumer). Returns the number of bytes available in a single contiguous span (may be less
    /// than <see cref="PendingBytes"/> if data wraps around).
    /// Call <see cref="AdvanceRead"/> after successfully sending.
    /// </summary>
    public ReadOnlySpan<byte> GetReadSpan()
    {
        var head = _head;
        var tail = _tail;

        if (head == tail)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (tail > head)
        {
            return _buffer.AsSpan(head, tail - head);
        }

        // Wrap-around: return first contiguous chunk (head → end of buffer)
        return _buffer.AsSpan(head, _capacity - head);
    }

    /// <summary>
    /// Advance the read position after the I/O thread has sent data. Called by consumer only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceRead(int bytesConsumed) => _head = (_head + bytesConsumed) % _capacity;

    /// <summary>
    /// Reset the buffer to empty. NOT thread-safe — only call when no concurrent access.
    /// </summary>
    public void Reset()
    {
        _head = 0;
        _tail = 0;
    }
    
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);    // Buffer is on the Pinned Object Heap — GC handles deallocation. No manual unpin needed.
}
