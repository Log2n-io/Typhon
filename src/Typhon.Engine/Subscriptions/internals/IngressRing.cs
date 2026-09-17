using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One session's inbound command ring: a single-producer, single-consumer byte ring holding variable-size records, over native memory it does not own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a ring per session rather than one shared queue.</b> The transport thread that owns a connection is the ring's only producer, so the hot path needs
/// no CAS, and a chatty client cannot displace another client's commands: capacity and overflow are both per session, which is what makes the fairness story
/// trivial. A shared MPSC ring has a global capacity and a global overflow flag, so one misbehaving client degrades everyone.
/// </para>
/// <para>
/// <b>Why not <c>EventQueue&lt;T&gt;</c>.</b> That identifies a producer by <c>TickContext.WorkerId</c> and keeps one segment per worker slot; a network thread
/// has no <c>TickContext</c> at all. Rule EQ-01 rests correctness on producer/slot disjointness with no runtime check for arbitrary threads, so this is
/// structural rather than an oversight — ingress needs its own primitive, and this is it.
/// </para>
/// <para>
/// <b>Native memory, never GC.</b> The buffer comes from <c>IMemoryAllocator.AllocatePinned</c> and is passed in as a pointer; this type frames and
/// synchronises, it does not allocate or free. An earlier send buffer used <c>GC.AllocateArray(pinned: true)</c>, and the pinned object heap stops the GC
/// <i>moving</i> an array, not <i>freeing</i> it — a pointer into one was the SWG x64 <c>Internal CLR error (0x80131506)</c> crash. Splitting ownership out
/// also lets one slab back many rings, and lets a test hand in any block it likes.
/// </para>
/// <para>
/// <b>Memory ordering, as named pairs, correct on arm64 and not merely on x64.</b> The producer writes the record bytes and then releases <c>_head</c>; the
/// consumer acquires <c>_head</c> <b>once per drain</b> and then reads the bytes. The consumer releases <c>_tail</c> only after copying records out; the
/// producer acquires <c>_tail</c> before deciding it has room. One acquire per drain rather than per record is the same economy EQ-02 prescribes — and
/// EQ-02's warning applies here too, that a DAG completion barrier alone is not sufficient ordering on arm64. An earlier send buffer used plain <c>int</c>
/// cursors and called them "naturally atomic on x64"; that is the verified negative precedent this replaces.
/// </para>
/// <para>
/// <b>Framing.</b> Records are contiguous, each prefixed with its own total size as a little-endian <c>u16</c>. A size of <see cref="WrapSentinel"/> means "the
/// rest of the buffer is padding, jump to the start" — written when a record would not fit contiguously. Every reservation is rounded up to an even byte
/// count and the capacity is a power of two, so every record begins at an even offset and the pathological "one byte left, cannot write a two-byte
/// sentinel" corner cannot arise.
/// </para>
/// <para>
/// <b>Overflow drops, counts, and never blocks or throws.</b> That is EQ-03's shape, and for ingress it is a requirement rather than a preference: the producer
/// is a network thread and must never be made to wait on the tick. A token bucket runs ahead of this on the transport thread, so a full ring means a client is
/// past its rate <i>and</i> the tick is late; the drop counters surface per session, and sustained overflow closes it.
/// </para>
/// </remarks>
internal sealed unsafe class IngressRing
{
    /// <summary>Size prefix marking the remainder of the buffer as padding.</summary>
    internal const ushort WrapSentinel = 0xFFFF;

    /// <summary>Bytes of framing each record carries: its little-endian <c>u16</c> size.</summary>
    internal const int HeaderSize = sizeof(ushort);

    /// <summary>Largest record this framing can express, payload included.</summary>
    internal const int MaxRecordSize = WrapSentinel - 1;

    private readonly byte* _buffer;
    private readonly int _mask;

    // Producer-written / consumer-written, each on its own cache line: without the padding the two cursors share a line and every publish invalidates the
    // consumer's copy and vice versa, which is the textbook SPSC false-sharing ping-pong.
    private CacheLinePaddedLong _head;
    private CacheLinePaddedLong _tail;

    // Producer-owned. The consumer reads them for diagnostics only, so plain access is correct — there is exactly one writer.
    private long _droppedRecords;
    private long _droppedBytes;

    /// <summary>Frames records into <paramref name="buffer"/>, which must remain valid for this ring's lifetime.</summary>
    /// <param name="buffer">Native memory, from <c>IMemoryAllocator.AllocatePinned</c>. Not owned, not freed here.</param>
    /// <param name="capacity">Buffer length in bytes; a power of two, at least 64.</param>
    /// <param name="poolSlot">The carving pool's slot for this ring, or <c>-1</c> for a ring that belongs to no pool. Fixed for the ring's lifetime.</param>
    public IngressRing(byte* buffer, int capacity, int poolSlot = -1)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (capacity < 64 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two and at least 64");
        }

        _buffer = buffer;
        _mask = capacity - 1;
        Capacity = capacity;
        PoolSlot = poolSlot;
    }

    /// <summary>Bytes the ring can hold, framing included.</summary>
    public int Capacity { get; }

    /// <summary>
    /// This ring's slot in the pool that carved it, or <c>-1</c> when it has none. Assigned once at construction, so a ring can be returned by handing back
    /// the ring itself rather than a slot the caller has to keep beside it — the pairing that goes wrong.
    /// </summary>
    public int PoolSlot { get; }

    /// <summary>Records refused because the ring was full. Never resets; a rising count is the session's own backpressure signal.</summary>
    public long DroppedRecords => _droppedRecords;

    /// <summary>Payload bytes refused because the ring was full.</summary>
    public long DroppedBytes => _droppedBytes;

    /// <summary>True when nothing is waiting to be drained.</summary>
    /// <remarks>Acquires <c>_head</c>, so a record published but not yet drained is never mistaken for an empty ring.</remarks>
    public bool IsEmpty => _tail.Value == Volatile.Read(ref _head.Value);

    /// <summary>Bytes published and not yet drained. Approximate across threads; exact on either side alone.</summary>
    public long BytesPending => Volatile.Read(ref _head.Value) - Volatile.Read(ref _tail.Value);

    /// <summary>
    /// Producer side: frames <paramref name="record"/> into the ring. Returns <see langword="false"/> when it does not fit, having counted the drop.
    /// </summary>
    /// <param name="record">The record's payload. Must be non-empty and within <see cref="MaxRecordSize"/> once framed.</param>
    /// <returns><see langword="true"/> when the record was published and is visible to the consumer.</returns>
    /// <remarks>
    /// Never blocks and never throws for a full ring — the caller is a network thread, and making it wait on the tick is the failure this design exists to
    /// avoid. An out-of-range record size IS an exception, because that is a programming error on the transport rather than a runtime condition.
    /// </remarks>
    public bool TryWrite(ReadOnlySpan<byte> record)
    {
        var size = HeaderSize + record.Length;
        if (record.IsEmpty || size > MaxRecordSize)
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.Length, $"A record must be non-empty and at most {MaxRecordSize - HeaderSize} bytes");
        }

        var evenSize = (size + 1) & ~1;
        if (evenSize > Capacity - HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.Length, $"A record of {evenSize} framed bytes cannot fit a ring of {Capacity}");
        }

        var head = _head.Value;                     // the producer owns _head: a plain read of its own cursor
        var tail = Volatile.Read(ref _tail.Value);  // acquire, pairing with Drain's release: see the consumer's freed space before deciding to use it
        var headOffset = (int)(head & _mask);
        var wrapBytes = 0;

        if (headOffset + evenSize > Capacity)
        {
            wrapBytes = Capacity - headOffset;      // even, because headOffset and Capacity both are
        }

        if ((head - tail) + wrapBytes + evenSize > Capacity)
        {
            _droppedRecords++;
            _droppedBytes += record.Length;
            return false;
        }

        if (wrapBytes > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_buffer + headOffset, HeaderSize), WrapSentinel);
            head += wrapBytes;
            headOffset = 0;
        }

        var slot = new Span<byte>(_buffer + headOffset, evenSize);
        BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)size);
        record.CopyTo(slot[HeaderSize..]);

        // Release: everything written above is visible to the consumer before it can observe the advanced head. A plain store would be relaxed on arm64 and
        // could expose the cursor ahead of the bytes it guards.
        Volatile.Write(ref _head.Value, head + evenSize);
        return true;
    }

    /// <summary>
    /// Consumer side: copies pending records into <paramref name="destination"/>, each still carrying its <c>u16</c> size prefix so the caller can walk them.
    /// </summary>
    /// <param name="destination">Where to copy. A record that does not fit is left for the next drain.</param>
    /// <param name="records">Records copied out.</param>
    /// <returns>Bytes written into <paramref name="destination"/>.</returns>
    /// <remarks>
    /// Wrap sentinels are skipped here, so the caller sees a clean contiguous stream. <c>_head</c> is acquired exactly ONCE, before the loop: per-record
    /// acquires would be the economy EQ-02 warns against, and buy nothing, since a record published mid-drain is simply drained next time.
    /// </remarks>
    public int Drain(Span<byte> destination, out int records)
    {
        var tail = _tail.Value;                     // the consumer owns _tail
        var head = Volatile.Read(ref _head.Value);  // acquire, once: pairs with TryWrite's release so the bytes copied below are fully visible
        var bytesWritten = 0;
        records = 0;

        while (tail < head)
        {
            var tailOffset = (int)(tail & _mask);
            var recordSize = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(_buffer + tailOffset, HeaderSize));

            if (recordSize == WrapSentinel)
            {
                tail += Capacity - tailOffset;
                continue;
            }

            // A record cannot be smaller than its own header, nor extend past what the producer published. Either would mean corrupt framing, so stop draining
            // rather than propagate it — the same defensive stop TraceRecordRing takes.
            if (recordSize < HeaderSize || tail + recordSize > head)
            {
                break;
            }

            if (bytesWritten + recordSize > destination.Length)
            {
                break;
            }

            new ReadOnlySpan<byte>(_buffer + tailOffset, recordSize).CopyTo(destination[bytesWritten..]);
            bytesWritten += recordSize;
            records++;
            tail += (recordSize + 1) & ~1;
        }

        // Release: the producer may only observe the freed space after the bytes have been copied out, or it could overwrite a record mid-copy.
        Volatile.Write(ref _tail.Value, tail);
        return bytesWritten;
    }

    /// <summary>Returns the payload of a framed record produced by <see cref="Drain"/>.</summary>
    /// <param name="framed">A drained buffer positioned at a record's size prefix.</param>
    /// <param name="consumed">Total framed bytes this record occupies, to advance to the next one.</param>
    /// <returns>The record's payload.</returns>
    /// <remarks>
    /// The length comes back through an <c>out</c> rather than as a tuple because a <c>ValueTuple</c> cannot hold a ref struct, so
    /// <c>(ReadOnlySpan&lt;byte&gt;, int)</c> will not compile. Stated here so the tidier shape is not attempted again.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ReadOnlySpan<byte> ReadFramed(ReadOnlySpan<byte> framed, out int consumed)
    {
        var size = BinaryPrimitives.ReadUInt16LittleEndian(framed);
        consumed = size;
        return framed[HeaderSize..size];
    }

    /// <summary>Clears the cursors and counters so a recycled buffer carries nothing from its previous session.</summary>
    /// <remarks>Must not run concurrently with a producer or consumer; the session lifecycle guarantees that by retiring the ring before reuse.</remarks>
    public void Reset()
    {
        _head.Value = 0;
        _tail.Value = 0;
        _droppedRecords = 0;
        _droppedBytes = 0;
    }
}
