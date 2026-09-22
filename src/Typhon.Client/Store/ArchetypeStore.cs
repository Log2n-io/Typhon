using System;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// Every entity of one archetype the client currently holds, as structure-of-arrays at stable slots (05-sdks § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stable slots.</b> An entity keeps its slot from enter to leave. A slot freed during a frame is not reused before the next frame begins, so a change
/// list never names a slot that changed owner inside the frame it describes.
/// </para>
/// <para>
/// <b>Columns.</b> A numeric field is one <see cref="double"/> array of <c>capacity × components</c> values: integers up to 2³² and every quantized decode are
/// exact in a double, and a decoded value is stored exactly as decoded. Text and bytes fields are arrays of references, replaced on change — the one place a
/// decode allocates, and only when such a field changes.
/// </para>
/// <para>
/// <b>Motion</b> is a ring of the last <see cref="SegmentHistory"/> segments per slot — position, velocity per tick, start tick, epoch — in one contiguous
/// record (see <see cref="SegmentRing"/>). The ring is what lets render time, which trails the newest frame by the render delay, still find the segment in
/// force when several have arrived since; <see cref="MotionEvaluator"/> reads it.
/// </para>
/// </remarks>
public sealed class ArchetypeStore
{
    private const int InitialCapacity = 16;

    private int[] _liveIndex;
    private int[] _free;
    private int _freeCount;
    private int[] _pendingFree;
    private int _pendingFreeCount;

    internal ArchetypeStore(ArchetypePlan plan, int segmentHistory)
    {
        Plan = plan;
        Dims = plan.Position?.Dims ?? 0;
        Moving = plan.Position?.Moving ?? false;
        Linear = plan.Position?.Linear ?? false;
        Segments = new SegmentRing(Dims, segmentHistory);
        Numbers = new double[plan.Fields.Length][];
        Texts = new string[plan.Fields.Length][];
        BytesColumns = new byte[plan.Fields.Length][][];
        Grow(InitialCapacity);
    }

    /// <summary>The compiled archetype.</summary>
    public ArchetypePlan Plan { get; }

    /// <summary>Position dimensions: 0 when the archetype is not spatial, else 2 or 3.</summary>
    public int Dims { get; }

    /// <summary>Whether segments are replicated (<c>motion</c>) rather than one position sent on enter (<c>static</c>).</summary>
    public bool Moving { get; }

    /// <summary>Whether segments carry a velocity and are extrapolated; otherwise they are samples, interpolated between.</summary>
    public bool Linear { get; }

    /// <summary>The motion segments: per slot, a ring of the last <see cref="SegmentHistory"/>.</summary>
    public SegmentRing Segments { get; }

    /// <summary>Segments kept per slot; see <see cref="SegmentRing.DepthFor"/>.</summary>
    public int SegmentHistory => Segments.Depth;

    /// <summary>Doubles per slot an evaluation writes: <c>p[Dims] v[Dims]</c>.</summary>
    public int MotionStride => Segments.Stride;

    /// <summary>Slots allocated.</summary>
    public int Capacity { get; private set; }

    /// <summary>netId per slot; 0 (a reserved identity) when the slot is free.</summary>
    public uint[] NetIds { get; private set; } = [];

    /// <summary>Occupied slots, dense in <c>[0, LiveCount)</c>.</summary>
    public int[] Live { get; private set; } = [];

    /// <summary>How many slots are occupied.</summary>
    public int LiveCount { get; private set; }

    /// <summary>Per field ordinal: <c>capacity × components</c> numbers, or <see langword="null"/> for a non-numeric field.</summary>
    public double[][] Numbers { get; }

    /// <summary>Per field ordinal: the text per slot, or <see langword="null"/> for a non-text field.</summary>
    public string[][] Texts { get; }

    /// <summary>Per field ordinal: the bytes per slot, or <see langword="null"/> for a non-bytes field.</summary>
    public byte[][][] BytesColumns { get; }

    /// <summary>Slots that entered this frame, in <c>[0, EnteredCount)</c>.</summary>
    public int[] Entered { get; private set; } = [];

    /// <summary>How many slots entered this frame.</summary>
    public int EnteredCount { get; private set; }

    /// <summary>Slots updated this frame (a state record or a segment), in <c>[0, UpdatedCount)</c>.</summary>
    public int[] Updated { get; private set; } = [];

    /// <summary>How many slots were updated this frame.</summary>
    public int UpdatedCount { get; private set; }

    /// <summary>Per slot: the groups changed this frame.</summary>
    public byte[] UpdateMask { get; private set; } = [];

    /// <summary>Per slot: whether a segment arrived this frame.</summary>
    public bool[] Moved { get; private set; } = [];

    /// <summary>netIds that left this frame, in <c>[0, LeftCount)</c>.</summary>
    public uint[] Left { get; private set; } = [];

    /// <summary>How many entities left this frame.</summary>
    public int LeftCount { get; private set; }

    /// <summary>Whether <paramref name="slot"/> holds an entity.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns><see langword="true"/> when occupied.</returns>
    public bool IsLive(int slot) => (uint)slot < (uint)Capacity && _liveIndex[slot] >= 0;

    /// <summary>The newest segment's ring entry of a slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The entry index.</returns>
    public int HeadEntry(int slot) => Segments.Head(slot);

    /// <summary>The newest segment's start position.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns><see cref="Dims"/> doubles.</returns>
    public ReadOnlySpan<double> HeadPosition(int slot) => Segments.Position(slot, Segments.Head(slot));

    /// <summary>The newest segment's velocity per tick; zero for a static or <c>none</c>-model archetype.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns><see cref="Dims"/> doubles.</returns>
    public ReadOnlySpan<double> HeadVelocity(int slot) => Segments.Velocity(slot, Segments.Head(slot));

    /// <summary>The newest segment's absolute start tick.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The tick.</returns>
    public uint HeadT0(int slot) => Segments.T0(slot, Segments.Head(slot));

    /// <summary>The newest segment's motion epoch.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The epoch.</returns>
    public byte HeadEpoch(int slot) => Segments.Epoch(slot, Segments.Head(slot));

    internal void BeginFrame()
    {
        for (var i = 0; i < _pendingFreeCount; i++)
        {
            _free[_freeCount++] = _pendingFree[i];
        }

        _pendingFreeCount = 0;
        for (var i = 0; i < UpdatedCount; i++)
        {
            UpdateMask[Updated[i]] = 0;
            Moved[Updated[i]] = false;
        }

        EnteredCount = 0;
        UpdatedCount = 0;
        LeftCount = 0;
    }

    internal int Allocate(uint netId)
    {
        if (_freeCount == 0)
        {
            Grow(Capacity * 2);
        }

        var slot = _free[--_freeCount];
        NetIds[slot] = netId;
        _liveIndex[slot] = LiveCount;
        Live[LiveCount++] = slot;
        for (var f = 0; f < Numbers.Length; f++)
        {
            if (Numbers[f] != null)
            {
                Array.Clear(Numbers[f], slot * Plan.Fields[f].Components, Plan.Fields[f].Components);
            }

            if (Texts[f] != null)
            {
                Texts[f][slot] = null;
            }

            if (BytesColumns[f] != null)
            {
                BytesColumns[f][slot] = null;
            }
        }

        if (Dims > 0)
        {
            Segments.Clear(slot);
        }

        Entered[EnteredCount++] = slot;
        return slot;
    }

    internal void Release(int slot, bool immediate)
    {
        var index = _liveIndex[slot];
        var last = Live[--LiveCount];
        Live[index] = last;
        _liveIndex[last] = index;
        _liveIndex[slot] = -1;
        Left[LeftCount++] = NetIds[slot];
        NetIds[slot] = 0;
        if (immediate)
        {
            _free[_freeCount++] = slot;
        }
        else
        {
            _pendingFree[_pendingFreeCount++] = slot;
        }
    }

    internal void MarkUpdated(int slot, byte groupMask, bool moved)
    {
        if (UpdateMask[slot] == 0 && !Moved[slot])
        {
            Updated[UpdatedCount++] = slot;
        }

        UpdateMask[slot] |= groupMask;
        Moved[slot] |= moved;
    }

    /// <summary>Makes a segment the slot's only one: the position an entity enters with.</summary>
    internal void ResetMotion(int slot, ReadOnlySpan<double> position, ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
        Segments.Reset(slot, position, velocity, t0, epoch);

    /// <summary>Appends a segment to the slot's ring, keeping the older ones render time may still need.</summary>
    internal void PushSegment(int slot, ReadOnlySpan<double> position, ReadOnlySpan<double> velocity, uint t0, byte epoch) =>
        Segments.Push(slot, position, velocity, t0, epoch);

    internal void Clear()
    {
        while (LiveCount > 0)
        {
            Release(Live[LiveCount - 1], immediate: true);
        }

        LeftCount = 0;
        EnteredCount = 0;
        for (var i = 0; i < UpdatedCount; i++)
        {
            UpdateMask[Updated[i]] = 0;
            Moved[Updated[i]] = false;
        }

        UpdatedCount = 0;
    }

    private void Grow(int capacity)
    {
        var old = Capacity;
        Capacity = capacity;
        NetIds = Resize(NetIds, capacity);
        Live = Resize(Live, capacity);
        var liveIndex = new int[capacity];
        liveIndex.AsSpan().Fill(-1);
        _liveIndex?.AsSpan().CopyTo(liveIndex);
        _liveIndex = liveIndex;

        for (var f = 0; f < Plan.Fields.Length; f++)
        {
            var field = Plan.Fields[f];
            switch (field.ValueKind)
            {
                case FieldValueKind.Number:
                    Numbers[f] = Resize(Numbers[f] ?? [], capacity * field.Components);
                    break;
                case FieldValueKind.Text:
                    Texts[f] = Resize(Texts[f] ?? [], capacity);
                    break;
                case FieldValueKind.Bytes:
                    BytesColumns[f] = Resize(BytesColumns[f] ?? [], capacity);
                    break;
            }
        }

        Segments.Resize(capacity);
        Entered = Resize(Entered, capacity);
        Updated = Resize(Updated, capacity);
        UpdateMask = Resize(UpdateMask, capacity);
        Moved = Resize(Moved, capacity);
        Left = Resize(Left, capacity);
        _pendingFree = Resize(_pendingFree ?? [], capacity);

        // New slots go on the free stack highest first, so allocation hands out the lowest slot first.
        _free = Resize(_free ?? [], capacity);
        for (var slot = capacity - 1; slot >= old; slot--)
        {
            _free[_freeCount++] = slot;
        }
    }

    private static T[] Resize<T>(T[] array, int length)
    {
        var next = new T[length];
        array.AsSpan().CopyTo(next);
        return next;
    }
}
