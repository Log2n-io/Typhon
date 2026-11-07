using System;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// Commands encoded and waiting for the next flush: each one's <c>varu cmdTypeIdx | u16 seq | body</c> bytes, back to back in one reusable buffer, so a
/// flush only copies byte ranges behind a <c>COMMANDS</c> header (03 § 8). Grows to the largest frame's worth and stays there: no allocation once warm.
/// </summary>
/// <remarks>Not thread-safe: <see cref="TyphonClient"/> holds its lock around every call.</remarks>
internal sealed class CommandOutbox
{
    private struct Entry
    {
        public int Offset;
        public int Length;
        public int TypeIdx;
    }

    private byte[] _bytes = new byte[256];
    private Entry[] _entries = new Entry[8];
    private int _used;

    /// <summary>Commands waiting.</summary>
    public int Count { get; private set; }

    /// <summary>A writable span of at least <paramref name="bytes"/> at the end of the buffer, for the next command.</summary>
    public Span<byte> Reserve(int bytes)
    {
        if (_used + bytes > _bytes.Length)
        {
            Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _used + bytes));
        }

        return _bytes.AsSpan(_used, bytes);
    }

    /// <summary>
    /// Keeps the command just written into <see cref="Reserve"/>'s span. A <paramref name="latest"/> command first removes the pending one of its type: only
    /// the newest travels, as the server's own slot would keep it, and it travels last, so seqs stay in order on the wire.
    /// </summary>
    /// <returns>Whether a pending command of the same type was replaced.</returns>
    public bool Commit(int typeIdx, int length, bool latest)
    {
        var replaced = false;
        if (latest)
        {
            for (var i = 0; i < Count; i++)
            {
                if (_entries[i].TypeIdx == typeIdx)
                {
                    // The new command's bytes already sit past _used (Reserve wrote them there): they shift down with the tail.
                    Remove(i, pendingTail: length);
                    replaced = true;
                    break;
                }
            }
        }

        if (Count == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        _entries[Count++] = new Entry { Offset = _used, Length = length, TypeIdx = typeIdx };
        _used += length;
        return replaced;
    }

    /// <summary>Drops every pending command.</summary>
    public void Clear()
    {
        Count = 0;
        _used = 0;
    }

    /// <summary>
    /// Writes one <c>COMMANDS</c> message from the entries starting at <paramref name="from"/>, as many as <paramref name="destination"/> holds, and advances
    /// <paramref name="from"/> past them.
    /// </summary>
    /// <returns>The message's length.</returns>
    /// <exception cref="InvalidOperationException">The next entry alone does not fit: a caller bug, since every entry is checked at enqueue.</exception>
    public int WriteMessage(ref int from, uint clientTick, Span<byte> destination)
    {
        var to = from;
        var body = 0;
        // One more command may also widen the count's varint (1 B to 2 B at 128): the widening is part of what must fit.
        while (to < Count && HeaderBytes(to - from + 1) + body + _entries[to].Length <= destination.Length)
        {
            body += _entries[to].Length;
            to++;
        }

        if (to == from)
        {
            throw new InvalidOperationException($"a {_entries[from].Length} B command does not fit a {destination.Length} B message");
        }

        var w = new WireWriter(destination);
        w.WriteU8(MessageTypes.Commands);
        w.WriteU32(clientTick);
        w.WriteVaru((uint)(to - from));
        var at = w.Written.Length;
        for (var i = from; i < to; i++)
        {
            var e = _entries[i];
            _bytes.AsSpan(e.Offset, e.Length).CopyTo(destination.Slice(at));
            at += e.Length;
        }

        from = to;
        return at;
    }

    /// <summary>A <c>COMMANDS</c> header carrying <paramref name="count"/> commands: <c>u8 type | u32 clientTick | varu count</c>.</summary>
    public static int HeaderBytes(int count) => 5 + (count < 0x80 ? 1 : count < 0x4000 ? 2 : 3);

    private void Remove(int index, int pendingTail)
    {
        var e = _entries[index];
        var tail = _used + pendingTail - (e.Offset + e.Length);
        Buffer.BlockCopy(_bytes, e.Offset + e.Length, _bytes, e.Offset, tail);
        _used -= e.Length;
        for (var i = index + 1; i < Count; i++)
        {
            var moved = _entries[i];
            moved.Offset -= e.Length;
            _entries[i - 1] = moved;
        }

        Count--;
    }
}

/// <summary>What <see cref="TyphonClient.Enqueue{T}"/> returns instead of a sequence number.</summary>
public static class CommandRefused
{
    /// <summary>The local token bucket for the command's type is empty: the server would have counted a drop (<c>ACKS</c>, reason 1).</summary>
    public const int RateLimited = -1;

    /// <summary>No session is open: a command queued now would belong to no session, so none is.</summary>
    public const int NotConnected = -2;
}

/// <summary>A dense per-process index for each command struct type, so a client finds its binding in an array instead of a dictionary.</summary>
internal static class CommandTypeSlots
{
    private static int _next = -1;

    internal static int Next() => Interlocked.Increment(ref _next);
}

/// <summary>The slot of <typeparamref name="T"/>, assigned on first use.</summary>
internal static class CommandTypeSlot<T>
{
    internal static readonly int Index = CommandTypeSlots.Next();
}
