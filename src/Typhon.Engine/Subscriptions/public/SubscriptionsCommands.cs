using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// One command as a system reads it: who sent it, which of that client's it is, the client frame it belongs to, and its value.
/// </summary>
/// <typeparam name="T">The command's struct, as the application declared it.</typeparam>
[PublicAPI]
public readonly struct ClientCommand<T> where T : unmanaged
{
    internal ClientCommand(SessionId session, ushort seq, uint clientTick, in T value)
    {
        Session = session;
        Seq = seq;
        ClientTick = clientTick;
        Value = value;
    }

    /// <summary>Which session sent it.</summary>
    public SessionId Session { get; }

    /// <summary>
    /// The client's sequence number. One space per session across every command type; it wraps, and is compared with RFC 1982 serial arithmetic.
    /// </summary>
    public ushort Seq { get; }

    /// <summary>The client's tick when it produced the batch this command arrived in. Shared by every command of that batch.</summary>
    public uint ClientTick { get; }

    /// <summary>The decoded command.</summary>
    public T Value { get; }
}

/// <summary>
/// This tick's commands of one type, in per-session arrival order.
/// </summary>
/// <typeparam name="T">The command's struct.</typeparam>
/// <remarks>
/// <para>
/// <b>Order within a session is the contract; order between sessions is not.</b> A session is drained by exactly one worker, which appends its records to that
/// worker's own segment in arrival order, so walking the segments preserves each client's order without preserving any order between clients — which nothing
/// needs (SUB-08, foundation/05 § 4.2).
/// </para>
/// <para>
/// <b>Nothing here allocates.</b> The batch is a view over the drain's buffers; the enumerator is a struct whose <c>Current</c> is a reference to its own
/// field, refreshed per step, so <c>foreach (ref readonly var c in …)</c> costs one copy of the command and no heap traffic at all.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct CommandBatch<T> where T : unmanaged
{
    private readonly CommandTypeBuffer _buffer;
    private readonly int _segment;
    private readonly int _start;
    private readonly int _count;

    internal CommandBatch(CommandTypeBuffer buffer)
    {
        _buffer = buffer;
        _segment = -1;
        _start = 0;
        _count = 0;
    }

    internal CommandBatch(CommandTypeBuffer buffer, int segment, int start, int count)
    {
        _buffer = buffer;
        _segment = segment;
        _start = start;
        _count = count;
    }

    /// <summary>How many commands this batch carries.</summary>
    public int Count
    {
        get
        {
            if (_buffer == null)
            {
                return 0;
            }

            if (_segment >= 0)
            {
                return _count;
            }

            var total = 0;
            for (var i = 0; i < _buffer.SegmentCount; i++)
            {
                total += _buffer.CountIn(i);
            }

            return total;
        }
    }

    /// <summary>Walks the batch.</summary>
    /// <returns>The enumerator.</returns>
    public Enumerator GetEnumerator() => new(_buffer, _segment, _start, _count);

    /// <summary>
    /// The newest command this session sent this tick, for a type declared <see cref="CommandCoalesce.LatestPerSession"/>.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="command">The command.</param>
    /// <returns><see langword="false"/> when that session sent none this tick.</returns>
    /// <remarks>
    /// O(1): the drain overwrites the session's single record in place rather than appending, and keeps the index of it. That is also why a coalesced type's
    /// batch carries exactly one record per session — the older ones never existed as records at all.
    /// </remarks>
    public bool TryGetLatest(SessionId session, out ClientCommand<T> command)
    {
        command = default;
        if (_buffer == null || !_buffer.TryGetSessionRange(session, out var segment, out var start, out var count) || count == 0)
        {
            return false;
        }

        // The newest is the LAST of the session's run, which for a coalesced type is its only one.
        command = Read(_buffer, segment, start + count - 1);
        return true;
    }

    /// <summary>
    /// Narrows the batch to one session's commands, in the order it sent them.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The narrowed batch, empty when that session sent none this tick.</returns>
    /// <remarks>
    /// O(1), and it is what lets a parallel system apply each client's commands on the worker that already owns that client's entities, instead of one system
    /// walking the whole tick's batch (01-model § 7).
    /// </remarks>
    public CommandBatch<T> ForSession(SessionId session)
    {
        if (_buffer == null || !_buffer.TryGetSessionRange(session, out var segment, out var start, out var count))
        {
            return new CommandBatch<T>(_buffer, 0, 0, 0);
        }

        return new CommandBatch<T>(_buffer, segment, start, count);
    }

    internal static ClientCommand<T> Read(CommandTypeBuffer buffer, int segment, int index)
    {
        var header = buffer.HeadersIn(segment)[index];
        var payload = buffer.PayloadsIn(segment).Slice(index * buffer.Stride, buffer.Stride);
        return new ClientCommand<T>(SessionId.FromValue(header.Session), header.Seq, header.ClientTick, in MemoryMarshal.AsRef<T>(payload));
    }

    /// <summary>Walks a <see cref="CommandBatch{T}"/>.</summary>
    [PublicAPI]
    public struct Enumerator
    {
        private readonly CommandTypeBuffer _buffer;
        private readonly int _onlySegment;
        private readonly int _start;
        private readonly int _count;

        private int _segment;
        private int _index;
        private int _remaining;
        private ClientCommand<T> _current;

        internal Enumerator(CommandTypeBuffer buffer, int segment, int start, int count)
        {
            _buffer = buffer;
            _onlySegment = segment;
            _start = start;
            _count = count;
            _segment = segment >= 0 ? segment : 0;
            _index = segment >= 0 ? start - 1 : -1;
            _remaining = segment >= 0 ? count : -1;
            _current = default;
        }

        /// <summary>
        /// The command at the cursor.
        /// </summary>
        /// <remarks>
        /// A reference into the enumerator itself, so <c>foreach (ref readonly var c in …)</c> binds without a copy at the call site and stays valid exactly as
        /// long as <c>foreach</c> keeps it — until the next step.
        /// </remarks>
        [UnscopedRef]
        public readonly ref readonly ClientCommand<T> Current => ref _current;

        /// <summary>Advances the cursor.</summary>
        /// <returns><see langword="false"/> at the end of the batch.</returns>
        public bool MoveNext()
        {
            if (_buffer == null)
            {
                return false;
            }

            if (_onlySegment >= 0)
            {
                if (_remaining <= 0)
                {
                    return false;
                }

                _remaining--;
                _index++;
                _current = CommandBatch<T>.Read(_buffer, _onlySegment, _index);
                return true;
            }

            while (true)
            {
                _index++;
                if (_index < _buffer.CountIn(_segment))
                {
                    _current = CommandBatch<T>.Read(_buffer, _segment, _index);
                    return true;
                }

                _segment++;
                _index = -1;
                if (_segment >= _buffer.SegmentCount)
                {
                    return false;
                }
            }
        }
    }
}

/// <summary>
/// What an application system reads commands through, and answers them with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here describes exactly one tick.</b> The Engine-Pre drain fills the buffers before the application's track runs, so a command is visible for
/// the tick it was drained into and for no other — never zero ticks, never two (SUB-08). A system that wants a command to outlive its tick copies it into
/// state, which is what state is for.
/// </para>
/// <para>
/// <b>Semantic validation belongs to the caller.</b> What reaches here has passed the wire's own checks, the declaration's role list, its rate limit and its
/// stateless pre-check. Range against the current world, cooldowns and ownership are the system's, because the state they must agree with is the system's
/// (01-model § 7).
/// </para>
/// </remarks>
[PublicAPI]
public sealed class SubscriptionsCommands
{
    private readonly SubscriptionsIngress _ingress;

    internal SubscriptionsCommands(SubscriptionsIngress ingress)
    {
        ArgumentNullException.ThrowIfNull(ingress);
        _ingress = ingress;
    }

    /// <summary>
    /// This tick's session lifecycle events — <c>Opened</c>, <c>Closed</c> — delivered in Engine-Pre, so a system reacts to one with an ordinary transaction in
    /// the same tick.
    /// </summary>
    public ReadOnlySpan<SessionEvent> SessionEvents => _ingress.Sessions.Events.AsSpan();

    /// <summary>
    /// This tick's commands of one type.
    /// </summary>
    /// <typeparam name="T">The command's struct, as the application declared it.</typeparam>
    /// <returns>The batch, empty when nothing arrived.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> was never declared as a command type.</exception>
    public CommandBatch<T> Commands<T>() where T : unmanaged
    {
        var info = _ingress.Commands.ByStruct(typeof(T))
            ?? throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not a declared command. Declare it with runtime.Subscriptions.Command<{typeof(T).Name}>(…) before Start, or the " +
                "catalog clients negotiate against would not name it and no client could ever send one.");

        var buffer = _ingress.Buffers.ByWireIdx(info.WireIdx);
        if (buffer != null && buffer.Stride != Unsafe.SizeOf<T>())
        {
            throw new InvalidOperationException(
                $"Command '{typeof(T).Name}' was bound at {buffer.Stride} bytes and measures {Unsafe.SizeOf<T>()} here. The struct's layout has to be the one " +
                "the decoder measured at Start.");
        }

        return new CommandBatch<T>(buffer);
    }

    /// <summary>
    /// Answers a command with a rejection, which reaches the client as an <c>ACK</c> record.
    /// </summary>
    /// <typeparam name="T">The command's struct.</typeparam>
    /// <param name="command">The command being refused.</param>
    /// <param name="reasonCode">An <see cref="AckReasons"/> code; 128-255 are the application's own.</param>
    /// <returns><see langword="false"/> when the tick's rejection log was full and the answer was dropped.</returns>
    /// <remarks>
    /// A command that is simply not applied sends nothing — the client learns only that its <c>seq</c> was consumed. Rejecting is for telling it WHY, and it
    /// costs a record in the next frame, so it is a decision rather than a default.
    /// </remarks>
    public bool Reject<T>(in ClientCommand<T> command, byte reasonCode) where T : unmanaged => Reject(command.Session, command.Seq, reasonCode);

    /// <summary>
    /// Answers a session's command by sequence number.
    /// </summary>
    /// <param name="session">Whose command it was.</param>
    /// <param name="seq">Its sequence number.</param>
    /// <param name="reasonCode">An <see cref="AckReasons"/> code.</param>
    /// <returns><see langword="false"/> when the tick's rejection log was full.</returns>
    public bool Reject(SessionId session, ushort seq, byte reasonCode) => _ingress.Buffers.Acks.Add(session, seq, reasonCode);

    /// <summary>
    /// Resolves an entity reference a client sent — a <c>netId</c> on the wire — back to the entity it names.
    /// </summary>
    /// <param name="session">The session that sent the reference.</param>
    /// <param name="netId">The network identity, as the command carried it.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="false"/> when nothing live holds that identity, or the session is gone.</returns>
    /// <remarks>
    /// <para>
    /// <b>An unknown identity is not an error.</b> A client may name an entity that has since left, or one it was never shown; the answer is "no", and the
    /// system decides what that means. Treating it as malformed input would let one stale reference close a connection.
    /// </para>
    /// <para>
    /// <b>The known-set half of this check is not built yet.</b> 01-model § 7 requires that a client can only target what it was shown, which needs the
    /// per-session known-set that P1-13a builds. Today the identity must merely be live and bound, so a client that guesses a valid netId is not refused for
    /// it. That gap is stated here rather than hidden: the clause is added where the known-set arrives, and nothing above this line has to change for it.
    /// </para>
    /// </remarks>
    public bool TryResolve(SessionId session, uint netId, out EntityId entity)
    {
        entity = EntityId.Null;
        return _ingress.Sessions.IsOpen(session) && _ingress.NetIds.TryGet(netId, out entity);
    }

    /// <summary>
    /// The highest sequence number drained into a tick for a session, which is what <c>SELF.lastSeq</c> reports.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="lastSeq">The sequence number.</param>
    /// <returns><see langword="false"/> when that session has never sent a command.</returns>
    /// <remarks>
    /// Every sequence at or below it was applied, rejected or coalesced away, which is exactly what a client's prediction reconciliation needs
    /// (03-wire-protocol § 12 W31).
    /// </remarks>
    public bool TryGetLastSeq(SessionId session, out ushort lastSeq)
    {
        var row = _ingress.RowOf(session);
        lastSeq = row?.LastSeq ?? 0;
        return row is { HasLastSeq: true };
    }
}
