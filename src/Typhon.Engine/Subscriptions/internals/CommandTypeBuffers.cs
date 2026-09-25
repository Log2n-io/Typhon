using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>What a drained command carries besides its value: whose it is, which of that session's it is, and the client frame it belongs to.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CommandHeader
{
    /// <summary>The packed <see cref="SessionId"/> that sent it.</summary>
    public uint Session;

    /// <summary>The client's tick for the batch this command arrived in.</summary>
    public uint ClientTick;

    /// <summary>The client's sequence number.</summary>
    public ushort Seq;
}

/// <summary>An <c>ACKS</c> record: a command the server consumed and refused.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CommandAck
{
    /// <summary>The packed <see cref="SessionId"/> the answer goes to.</summary>
    public uint Session;

    /// <summary>The sequence number being answered.</summary>
    public ushort Seq;

    /// <summary>An <see cref="Typhon.Protocol.AckReasons"/> code.</summary>
    public byte Reason;
}

/// <summary>
/// This tick's rejections, waiting for the frame assembler to turn them into <c>ACKS</c> records.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shared array with an interlocked cursor, not a per-worker segment.</b> The per-worker shape is right for commands, which arrive in thousands and are
/// appended by the drain, which knows its chunk. A rejection is produced by an application system that has no chunk index and no worker id in hand — the public
/// surface is <c>ctx.Subscriptions.Reject(in command, reason)</c> — and rejections are rare by construction: a command that is simply not applied sends
/// nothing (01-model § 7). One contended increment for something that happens a handful of times per tick is cheaper than threading a worker id through the
/// public API for it.
/// </para>
/// <para>
/// <b>It is bounded and it drops.</b> The array is sized once and overflow is counted rather than grown, because growing it would mean an allocation on the
/// tick path and a rejection storm is a client problem, not a reason to make the server allocate.
/// </para>
/// </remarks>
internal sealed class CommandAckLog
{
    private readonly CommandAck[] _acks;
    private int _count;
    private long _dropped;

    /// <summary>Creates a log holding at most <paramref name="capacity"/> rejections per tick.</summary>
    /// <param name="capacity">The ceiling.</param>
    public CommandAckLog(int capacity) => _acks = new CommandAck[Math.Max(1, capacity)];

    /// <summary>Rejections recorded this tick.</summary>
    public int Count => Math.Min(Volatile.Read(ref _count), _acks.Length);

    /// <summary>Rejections that did not fit, over the log's life.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>This tick's rejections.</summary>
    /// <returns>The records.</returns>
    public ReadOnlySpan<CommandAck> AsSpan() => new(_acks, 0, Count);

    /// <summary>Records a rejection.</summary>
    /// <param name="session">Whose command it was.</param>
    /// <param name="seq">Its sequence number.</param>
    /// <param name="reason">An <see cref="Typhon.Protocol.AckReasons"/> code.</param>
    /// <returns><see langword="false"/> when the log was full and the rejection was dropped.</returns>
    public bool Add(SessionId session, ushort seq, byte reason)
    {
        var slot = Interlocked.Increment(ref _count) - 1;
        if (slot >= _acks.Length)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        _acks[slot] = new CommandAck { Session = session.Value, Seq = seq, Reason = reason };
        return true;
    }

    /// <summary>Starts a fresh tick.</summary>
    public void BeginTick() => Volatile.Write(ref _count, 0);
}

/// <summary>
/// One command type's arrivals for one tick: per-worker segments of headers and payloads, plus the per-session index that makes
/// <c>TryGetLatest</c> and <c>ForSession</c> O(1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>EventQueue</c> shape, for the same reason.</b> Each drain chunk owns one segment, so appending needs no synchronization at all; the merge is
/// hidden behind the batch's enumerator, exactly as <c>EventQueue.Drain</c> hides its own (archive/Subscriptions/foundation/05 § 4.2).
/// </para>
/// <para>
/// <b>Structure of arrays, not an array of <c>Command&lt;T&gt;</c>.</b> The payloads are raw bytes with a per-type stride because the drain is not generic —
/// it walks a table of command types resolved from the catalog and cannot name <c>T</c>. Reaching a typed record from a <see cref="Type"/> through
/// <c>MakeGenericType</c> would buy a nicer array and an AOT blocker (#409); the reinterpret happens in <c>Commands&lt;T&gt;()</c>, where <c>T</c> is a
/// compile-time argument and the stride is checked against it once.
/// </para>
/// <para>
/// <b>Staleness is a stamp, not a clear.</b> The per-session index is <see cref="SubscriptionsOptions.MaxSessions"/> entries wide, so clearing it every tick
/// would be a memset proportional to the table an operator configured rather than to the sessions that sent anything — the cost SUB-13 exists to refuse. Each
/// entry carries the tick it was written in, and an entry from an older tick reads as absent.
/// </para>
/// </remarks>
internal sealed class CommandTypeBuffer
{
    private const int InitialSegmentCapacity = 16;

    private readonly int _stride;
    private readonly bool _coalesced;

    private CommandHeader[][] _headers = [];
    private byte[][] _payloads = [];
    private int[] _counts = [];

    // Per session slot. Sized once from MaxSessions: the index is what makes the per-session lookups O(1), and a growing structure would put an indirection
    // on every one of them.
    private readonly int[] _sessionSegment;
    private readonly int[] _sessionStart;
    private readonly int[] _sessionCount;
    private readonly long[] _sessionTick;

    private long _tick = long.MinValue;
    private long _delivered;

    /// <summary>Creates a buffer for a command type.</summary>
    /// <param name="stride">Bytes one decoded command occupies.</param>
    /// <param name="coalesced">Whether only the newest per session survives.</param>
    /// <param name="maxSessions">The session table's width, which sizes the per-session index.</param>
    public CommandTypeBuffer(int stride, bool coalesced, int maxSessions)
    {
        _stride = Math.Max(1, stride);
        _coalesced = coalesced;
        _sessionSegment = new int[maxSessions];
        _sessionStart = new int[maxSessions];
        _sessionCount = new int[maxSessions];
        _sessionTick = new long[maxSessions];
        Array.Fill(_sessionTick, long.MinValue);
    }

    /// <summary>Bytes one decoded command occupies.</summary>
    public int Stride => _stride;

    /// <summary>Whether only the newest per session survives a tick.</summary>
    public bool IsCoalesced => _coalesced;

    /// <summary>Segments this buffer currently holds, one per drain chunk.</summary>
    public int SegmentCount => _counts.Length;

    /// <summary>Commands delivered over this buffer's life, for diagnostics and for the acceptance test's count.</summary>
    public long DeliveredCount => _delivered;

    /// <summary>The tick this buffer's contents belong to.</summary>
    public long Tick => _tick;

    /// <summary>Commands in a segment.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The count.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CountIn(int segment) => (uint)segment < (uint)_counts.Length ? _counts[segment] : 0;

    /// <summary>A segment's headers.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The headers, as many as <see cref="CountIn"/> reports.</returns>
    public ReadOnlySpan<CommandHeader> HeadersIn(int segment) => new(_headers[segment], 0, _counts[segment]);

    /// <summary>A segment's payload bytes, <see cref="Stride"/> per command.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The payloads.</returns>
    public ReadOnlySpan<byte> PayloadsIn(int segment) => new(_payloads[segment], 0, _counts[segment] * _stride);

    /// <summary>Starts a tick, keeping the segments so a steady state allocates nothing.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="segmentCount">How many drain chunks may append this tick.</param>
    public void BeginTick(long tick, int segmentCount)
    {
        _tick = tick;
        if (_counts.Length < segmentCount)
        {
            Array.Resize(ref _counts, segmentCount);
            Array.Resize(ref _headers, segmentCount);
            Array.Resize(ref _payloads, segmentCount);
            for (var i = 0; i < segmentCount; i++)
            {
                _headers[i] ??= new CommandHeader[InitialSegmentCapacity];
                _payloads[i] ??= new byte[InitialSegmentCapacity * _stride];
            }
        }

        Array.Clear(_counts);
    }

    /// <summary>
    /// Appends one command to a drain chunk's segment, coalescing onto the session's existing record when the type asked for it.
    /// </summary>
    /// <param name="segment">The drain chunk's segment.</param>
    /// <param name="session">Whose command it is.</param>
    /// <param name="seq">Its sequence number.</param>
    /// <param name="clientTick">The client frame it belongs to.</param>
    /// <param name="payload">The decoded command, exactly <see cref="Stride"/> bytes.</param>
    public void Append(int segment, SessionId session, ushort seq, uint clientTick, scoped ReadOnlySpan<byte> payload)
    {
        var slot = session.Slot;
        var fresh = _sessionTick[slot] != _tick;

        int index;
        if (_coalesced && !fresh)
        {
            // In place: the newest overwrites the previous one, so the batch carries exactly one record per session and TryGetLatest needs no second store.
            index = _sessionStart[slot];
            _sessionSegment[slot] = segment;
        }
        else
        {
            index = _counts[segment];
            EnsureCapacity(segment, index + 1);
            _counts[segment] = index + 1;

            if (fresh)
            {
                _sessionSegment[slot] = segment;
                _sessionStart[slot] = index;
                _sessionCount[slot] = 0;
                _sessionTick[slot] = _tick;
            }

            _sessionCount[slot]++;
        }

        _headers[segment][index] = new CommandHeader { Session = session.Value, ClientTick = clientTick, Seq = seq };
        payload[.._stride].CopyTo(new Span<byte>(_payloads[segment], index * _stride, _stride));
        _delivered++;
    }

    /// <summary>Where a session's commands of this type live this tick.</summary>
    /// <param name="session">The session.</param>
    /// <param name="segment">The segment they are in.</param>
    /// <param name="start">The first one's index.</param>
    /// <param name="count">How many there are.</param>
    /// <returns><see langword="false"/> when that session sent none this tick.</returns>
    public bool TryGetSessionRange(SessionId session, out int segment, out int start, out int count)
    {
        var slot = session.Slot;
        if (slot >= _sessionTick.Length || _sessionTick[slot] != _tick)
        {
            segment = start = count = 0;
            return false;
        }

        // The identity is checked too, not just the slot: a slot recycled between two sessions in one tick would otherwise hand the new occupant the previous
        // one's commands.
        segment = _sessionSegment[slot];
        start = _sessionStart[slot];
        count = _sessionCount[slot];
        return count > 0 && _headers[segment][start].Session == session.Value;
    }

    private void EnsureCapacity(int segment, int required)
    {
        var headers = _headers[segment];
        if (headers.Length >= required)
        {
            return;
        }

        var capacity = headers.Length == 0 ? InitialSegmentCapacity : headers.Length * 2;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _headers[segment], capacity);
        Array.Resize(ref _payloads[segment], capacity * _stride);
    }
}

/// <summary>
/// Every command type's per-tick buffers, indexed by the wire index the catalog gave each type, plus the tick's rejections.
/// </summary>
/// <remarks>
/// Built once at <c>Start</c> and reused every tick. It holds no per-session state of its own — that lives on the session's ingress row — so a session opening
/// or closing costs nothing here beyond the stamp that makes its old index read as absent.
/// </remarks>
internal sealed class CommandTypeBuffers
{
    private readonly CommandTypeBuffer[] _byWireIdx;

    /// <summary>Creates one buffer per command type the catalog declares.</summary>
    /// <param name="registry">The bound command types.</param>
    /// <param name="maxSessions">The session table's width.</param>
    public CommandTypeBuffers(CommandRegistry registry, int maxSessions)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _byWireIdx = new CommandTypeBuffer[registry.WireIdxCount];
        for (var idx = 0; idx < _byWireIdx.Length; idx++)
        {
            var info = registry.ByWireIdx(idx);
            if (info is { IsClientRegion: false })
            {
                _byWireIdx[idx] = new CommandTypeBuffer(info.PayloadSize, info.Coalesce == CommandCoalesce.LatestPerSession, maxSessions);
            }
        }

        Acks = new CommandAckLog(AckCapacity(maxSessions));
    }

    /// <summary>This tick's rejections.</summary>
    public CommandAckLog Acks { get; }

    /// <summary>
    /// The most rejections one tick can record: the ack log's ceiling, which the acknowledgement history is sized to. Room for every session's share of
    /// transport-side refusals (<see cref="SubscriptionsIngress.RefusalAcksPerTick"/>) up to 2 048 sessions, so below that no session's refusals can
    /// crowd out another's; the application's own rejections share what is left.
    /// </summary>
    /// <param name="maxSessions">The session table's width.</param>
    /// <returns>The capacity.</returns>
    public static int AckCapacity(int maxSessions) => (int)Math.Clamp((long)maxSessions * SubscriptionsIngress.RefusalAcksPerTick, 64, 16384);

    /// <summary>The tick the buffers currently describe.</summary>
    public long Tick { get; private set; } = long.MinValue;

    /// <summary>The buffer of a command type, or <see langword="null"/> for the built-in region, which is not delivered as a typed command.</summary>
    /// <param name="wireIdx">The command's wire index.</param>
    /// <returns>The buffer.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandTypeBuffer ByWireIdx(int wireIdx) => (uint)wireIdx < (uint)_byWireIdx.Length ? _byWireIdx[wireIdx] : null;

    /// <summary>Starts a tick on every buffer.</summary>
    /// <param name="tick">The tick number.</param>
    /// <param name="segmentCount">How many drain chunks may append this tick.</param>
    public void BeginTick(long tick, int segmentCount)
    {
        Tick = tick;
        Acks.BeginTick();
        var segments = Math.Max(1, segmentCount);
        foreach (var buffer in _byWireIdx)
        {
            buffer?.BeginTick(tick, segments);
        }
    }
}
