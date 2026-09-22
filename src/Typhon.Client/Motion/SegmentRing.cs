using System;
using System.Runtime.InteropServices;

namespace Typhon.Client;

/// <summary>
/// The motion segments of one archetype: per slot, a ring of the last <see cref="Depth"/> segments <c>(p0, v, t0, epoch)</c> in one contiguous record
/// (03-wire-protocol § 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a ring and not the latest segment.</b> Render time trails the newest frame by up to the render delay, and a mover may take a segment every tick, so
/// several segments can arrive before render time reaches the first one's <c>t0</c>. The ring keeps them all: <c>max(4, ⌈maxRenderDelayMs / tickPeriod⌉ + 2)</c>
/// entries — the delay in ticks, plus the segment render time is inside and the next one an interpolated sample needs. The depth follows the catalog's tick
/// period, never one application's frame rate.
/// </para>
/// <para>
/// <b>The record, per slot, exactly as the TypeScript SDK lays it out</b> (<c>store/archetype-store.ts</c>), so the two can be compared field by field:
/// </para>
/// <code>
///   0 .. 4H          t0 of each ring entry (u32, integer ticks)
///   4H               head: ring index of the newest segment (u8)
///   4H + 1           count: valid segments, 1..H (u8)
///   4H + 2 .. 5H + 2 epoch of each ring entry (u8)
///   padding to a multiple of 8
///   segments         H × p0[dims] v[dims] (f64: metres and metres per tick)
/// </code>
/// <para>
/// One record per slot, contiguous: evaluating a slot touches the <c>t0</c>s, the epoch it lands on and one segment, which sit within a few cache lines of each
/// other instead of in three separate arrays a slot apart. Doubles, not floats: a 24-bit position over a 16 km world is exact in a float32, but the store
/// serves any catalog, and a finer quantization over a larger world is not.
/// </para>
/// </remarks>
public sealed class SegmentRing
{
    /// <summary>The largest render delay the ring is sized for by default, in milliseconds: the <see cref="Clock"/>'s own default ceiling (05-sdks § 1).</summary>
    public const double DefaultMaxRenderDelayMs = 300;

    /// <summary>The deepest ring, bounded by the <c>u8</c> head and count of the record.</summary>
    public const int MaxDepth = 255;

    private byte[] _records = [];

    /// <summary>Creates a ring.</summary>
    /// <param name="dims">Position dimensions: 2 or 3, or 0 when the archetype is not spatial.</param>
    /// <param name="depth">Segments kept per slot, 1 to <see cref="MaxDepth"/>; see <see cref="DepthFor"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The depth is outside 1..255.</exception>
    public SegmentRing(int dims, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, MaxDepth);
        Dims = dims;
        Depth = depth;
        Stride = 2 * dims;
        HeadOffset = 4 * depth;
        CountOffset = HeadOffset + 1;
        EpochOffset = HeadOffset + 2;
        SegmentsOffset = SegmentsOffsetFor(depth);
        RecordBytes = dims == 0 ? 0 : RecordBytesFor(dims, depth);
    }

    /// <summary>Position dimensions: 0 when the archetype is not spatial, else 2 or 3.</summary>
    public int Dims { get; }

    /// <summary>Segments kept per slot.</summary>
    public int Depth { get; }

    /// <summary>Doubles per ring entry an evaluation writes: <c>p[Dims] v[Dims]</c>.</summary>
    public int Stride { get; }

    /// <summary>Bytes per slot; 0 when the archetype is not spatial.</summary>
    public int RecordBytes { get; }

    /// <summary>Byte offset of the head within a record.</summary>
    public int HeadOffset { get; }

    /// <summary>Byte offset of the count within a record.</summary>
    public int CountOffset { get; }

    /// <summary>Byte offset of the first epoch within a record.</summary>
    public int EpochOffset { get; }

    /// <summary>Byte offset of the first segment within a record, 8-aligned.</summary>
    public int SegmentsOffset { get; }

    /// <summary>Slots the ring holds records for.</summary>
    public int Capacity { get; private set; }

    /// <summary>
    /// Segments to keep per slot for a tick period and a render delay: <c>max(4, ⌈maxRenderDelayMs · 1000 / tickPeriodUs⌉ + 2)</c>, capped at
    /// <see cref="MaxDepth"/>.
    /// </summary>
    /// <param name="tickPeriodUs">The catalog's nominal tick period in microseconds.</param>
    /// <param name="maxRenderDelayMs">The largest render delay motion will be evaluated at.</param>
    /// <returns>The depth, 4 to 255.</returns>
    /// <remarks>
    /// Past the cap — a delay longer than about 253 ticks — the ring covers less than the delay, and render time older than the oldest segment held evaluates
    /// that oldest segment (extrapolated backwards for <c>linear</c>, held for <c>none</c>) rather than throwing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The tick period is not positive, or the delay is not positive and finite.</exception>
    public static int DepthFor(int tickPeriodUs, double maxRenderDelayMs = DefaultMaxRenderDelayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickPeriodUs);
        if (!(maxRenderDelayMs > 0 && double.IsFinite(maxRenderDelayMs)))
        {
            throw new ArgumentOutOfRangeException(nameof(maxRenderDelayMs), maxRenderDelayMs, "the render delay must be positive and finite");
        }

        var ticks = Math.Ceiling(maxRenderDelayMs * 1000 / tickPeriodUs) + 2;
        return (int)Math.Min(MaxDepth, Math.Max(4, ticks));
    }

    /// <summary>Where the segments start in a record of <paramref name="depth"/> entries: after the <c>t0</c>s, the head, the count and the epochs, 8-aligned.</summary>
    /// <param name="depth">The ring depth.</param>
    /// <returns>The byte offset.</returns>
    public static int SegmentsOffsetFor(int depth) => ((5 * depth) + 2 + 7) & ~7;

    /// <summary>Bytes of one slot's record.</summary>
    /// <param name="dims">Position dimensions.</param>
    /// <param name="depth">The ring depth.</param>
    /// <returns>The record size in bytes.</returns>
    public static int RecordBytesFor(int dims, int depth) => SegmentsOffsetFor(depth) + (depth * 2 * dims * 8);

    /// <summary>Grows the ring to hold <paramref name="capacity"/> slots, keeping what it holds.</summary>
    /// <param name="capacity">The new slot count, never smaller than the current one.</param>
    public void Resize(int capacity)
    {
        Capacity = capacity;
        if (RecordBytes == 0)
        {
            return;
        }

        var records = new byte[(long)capacity * RecordBytes];
        _records.AsSpan().CopyTo(records);
        _records = records;
    }

    /// <summary>The newest segment's ring entry.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The entry index.</returns>
    public int Head(int slot) => _records[(slot * RecordBytes) + HeadOffset];

    /// <summary>How many entries of the ring hold a segment, 1 to <see cref="Depth"/>.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The count.</returns>
    public int Held(int slot) => _records[(slot * RecordBytes) + CountOffset];

    /// <summary>A ring entry's absolute start tick.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="entry">The ring entry.</param>
    /// <returns>The tick.</returns>
    public uint T0(int slot, int entry) => Ticks(slot)[entry];

    /// <summary>A ring entry's motion epoch: a change means the entity teleported.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="entry">The ring entry.</param>
    /// <returns>The epoch.</returns>
    public byte Epoch(int slot, int entry) => _records[(slot * RecordBytes) + EpochOffset + entry];

    /// <summary>A ring entry's start position and velocity: <c>p[Dims]</c> then <c>v[Dims]</c>.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="entry">The ring entry.</param>
    /// <returns><see cref="Stride"/> doubles.</returns>
    public ReadOnlySpan<double> Segment(int slot, int entry) => Segments(slot).Slice(entry * Stride, Stride);

    /// <summary>A ring entry's start position.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="entry">The ring entry.</param>
    /// <returns><see cref="Dims"/> doubles.</returns>
    public ReadOnlySpan<double> Position(int slot, int entry) => Segments(slot).Slice(entry * Stride, Dims);

    /// <summary>A ring entry's velocity per tick; zero for a static archetype or a <c>none</c>-model sample.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="entry">The ring entry.</param>
    /// <returns><see cref="Dims"/> doubles.</returns>
    public ReadOnlySpan<double> Velocity(int slot, int entry) => Segments(slot).Slice((entry * Stride) + Dims, Dims);

    /// <summary>Empties a slot's ring to one zeroed segment: the state an entering entity starts from.</summary>
    /// <param name="slot">The slot.</param>
    /// <remarks>
    /// Entry 0 only: evaluation never reads an entry beyond the count, and the whole record is large at a high tick rate.
    /// </remarks>
    public void Clear(int slot)
    {
        var at = slot * RecordBytes;
        _records[at + HeadOffset] = 0;
        _records[at + CountOffset] = 1;
        _records[at + EpochOffset] = 0;
        Ticks(slot)[0] = 0;
        Segments(slot)[..Stride].Clear();
    }

    /// <summary>Makes a segment the slot's only one: the position an entity enters with.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="position"><see cref="Dims"/> values.</param>
    /// <param name="velocity">As many, or empty for a static position or a <c>none</c>-model sample.</param>
    /// <param name="t0">The segment's absolute start tick.</param>
    /// <param name="epoch">The motion epoch.</param>
    public void Reset(int slot, ReadOnlySpan<double> position, ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        var at = slot * RecordBytes;
        _records[at + HeadOffset] = 0;
        _records[at + CountOffset] = 1;
        Write(slot, 0, position, velocity, t0, epoch);
    }

    /// <summary>
    /// Appends a segment, overwriting the oldest once the ring is full. Older segments stay, so render time — which trails the newest frame by the render
    /// delay — keeps finding the segment it needs even when several arrive before it reaches their start ticks.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="position"><see cref="Dims"/> values.</param>
    /// <param name="velocity">As many, or empty for a <c>none</c>-model sample.</param>
    /// <param name="t0">The segment's absolute start tick.</param>
    /// <param name="epoch">The motion epoch.</param>
    /// <returns>The ring entry written.</returns>
    public int Push(int slot, ReadOnlySpan<double> position, ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        var at = slot * RecordBytes;
        var head = _records[at + HeadOffset] + 1;
        if (head == Depth)
        {
            head = 0;
        }

        _records[at + HeadOffset] = (byte)head;
        if (_records[at + CountOffset] < Depth)
        {
            _records[at + CountOffset]++;
        }

        Write(slot, head, position, velocity, t0, epoch);
        return head;
    }

    private void Write(int slot, int entry, ReadOnlySpan<double> position, ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        Ticks(slot)[entry] = t0;
        _records[(slot * RecordBytes) + EpochOffset + entry] = epoch;
        var segment = Segments(slot).Slice(entry * Stride, Stride);
        position[..Dims].CopyTo(segment);
        var v = segment[Dims..];
        if (velocity.IsEmpty)
        {
            v.Clear();
        }
        else
        {
            velocity[..Dims].CopyTo(v);
        }
    }

    // The three views of one record, as the TypeScript store's three typed arrays over one ArrayBuffer. A byte[]'s payload is 8-aligned, and both the t0
    // block (offset 0) and the segment block (SegmentsOffset, itself 8-aligned) start at a multiple of RecordBytes, which is a multiple of 8.
    private Span<uint> Ticks(int slot) => MemoryMarshal.Cast<byte, uint>(_records.AsSpan(slot * RecordBytes, 4 * Depth));

    private Span<double> Segments(int slot) => MemoryMarshal.Cast<byte, double>(_records.AsSpan((slot * RecordBytes) + SegmentsOffset, Depth * Stride * 8));
}
