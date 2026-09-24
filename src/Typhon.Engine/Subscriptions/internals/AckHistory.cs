using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// The last <see cref="PushReplication.LogDepth"/> ticks of command rejections, each tick sorted by session, so a session's frame collects the
/// acknowledgements of every tick since its last published frame (design/Subscriptions/11 § 2.3) — the event log's shape, for the same reason: a skipped
/// session receives the union on its next frame, and nothing is kept per session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rare by construction.</b> A command that is simply not applied sends nothing (01 § 7); only an explicit <c>Reject</c>, a rate-limited command and an
/// invalid region are answered. A tick with none costs a count of zero, and a session skips it without a search.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="Record"/> runs in the Frames stage's serial prologue; <see cref="Collect"/> from its parallel chunks, which only read.
/// </para>
/// </remarks>
internal sealed class AckHistory
{
    private readonly Slot[] _slots = new Slot[PushReplication.LogDepth];
    private ulong[] _keys = [];

    /// <summary>Creates the history, sized for <paramref name="capacity"/> rejections a tick — the ack log's own ceiling — so a storm allocates nothing.</summary>
    /// <param name="capacity">The most rejections one tick can record.</param>
    public AckHistory(int capacity)
    {
        for (var i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot { Records = new CommandAck[capacity] };
        }

        _keys = new ulong[capacity];
    }

    /// <summary>The most records one tick has held, which bounds what a session's collection can need per tick.</summary>
    public int MaxPerTick { get; private set; }

    /// <summary>The last tick that recorded any acknowledgement; a session whose last frame is newer has nothing to collect.</summary>
    public uint LastTickWithAcks { get; private set; }

    /// <summary>Records <paramref name="tick"/>'s rejections, sorted by session, in the slot the tick <see cref="PushReplication.LogDepth"/> ago held.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="acks">Its rejections, in any order.</param>
    public void Record(uint tick, ReadOnlySpan<CommandAck> acks)
    {
        var slot = _slots[tick % (uint)_slots.Length];
        slot.Tick = tick;
        slot.Count = acks.Length;
        if (acks.Length == 0)
        {
            return;
        }

        if (slot.Records.Length < acks.Length)
        {
            // Grows to the largest tick seen, then never again: a rejection storm is bounded by the ack log's own ceiling.
            var size = Math.Max(acks.Length, slot.Records.Length * 2);
            slot.Records = new CommandAck[size];
        }

        if (_keys.Length < acks.Length)
        {
            _keys = new ulong[Math.Max(acks.Length, _keys.Length * 2)];
        }

        // Sorted by session, stable: the key's low half is the arrival index, so a session's acknowledgements keep their order (seq order within a drain,
        // then Reject order). O(n log n) — a rate-limit storm can fill the ack log's whole ceiling in one tick.
        var keys = _keys.AsSpan(0, acks.Length);
        for (var i = 0; i < acks.Length; i++)
        {
            keys[i] = ((ulong)acks[i].Session << 32) | (uint)i;
        }

        keys.Sort();
        for (var i = 0; i < keys.Length; i++)
        {
            slot.Records[i] = acks[(int)(uint)keys[i]];
        }

        MaxPerTick = Math.Max(MaxPerTick, acks.Length);
        LastTickWithAcks = tick;
    }

    /// <summary>
    /// Appends to <paramref name="into"/> the acknowledgements for <paramref name="session"/> of the ticks in (<paramref name="after"/>,
    /// <paramref name="upTo"/>], oldest first.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="after">The tick of the session's last published frame; 0 for none.</param>
    /// <param name="upTo">This tick.</param>
    /// <param name="into">The destination.</param>
    /// <param name="count">Records written.</param>
    /// <param name="overflowed">Records that did not fit <paramref name="into"/>.</param>
    /// <param name="windowLost">Whether the window reached past the ticks the history holds, where a rejection may have been.</param>
    public void Collect(SessionId session, uint after, uint upTo, Span<CommandAck> into, out int count, out int overflowed, out bool windowLost)
    {
        count = 0;
        overflowed = 0;
        windowLost = false;
        if (upTo <= after || LastTickWithAcks <= after)
        {
            // Nothing recorded since the session's last frame: most sessions, most ticks, and no slot is touched.
            return;
        }

        var key = session.Value;
        var first = upTo - after > (uint)_slots.Length ? upTo - (uint)_slots.Length + 1 : after + 1;
        windowLost = after != 0 && first > after + 1;
        for (var tick = first; tick <= upTo && tick != 0; tick++)
        {
            var slot = _slots[tick % (uint)_slots.Length];
            if (slot.Tick != tick || slot.Count == 0)
            {
                continue;
            }

            var records = slot.Records.AsSpan(0, slot.Count);
            var lo = 0;
            var hi = records.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (records[mid].Session < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < records.Length && records[i].Session == key; i++)
            {
                if (count < into.Length)
                {
                    into[count++] = records[i];
                }
                else
                {
                    overflowed++;
                }
            }
        }
    }

    private sealed class Slot
    {
        public uint Tick = uint.MaxValue;
        public int Count;
        public CommandAck[] Records = [];
    }
}
