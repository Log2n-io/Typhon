using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>How a <c>SubscribeRequest</c> ended (14 § 4, P13).</summary>
public enum SubscribeOutcome : byte
{
    /// <summary><c>SOURCE_APPLIED</c>: the key is held and complete — or, for an unsubscribe, released.</summary>
    Applied = 1,

    /// <summary><c>SOURCE_ERROR</c>: the server refused it; <see cref="SubscribeResult.Code"/> says why (<see cref="SourceErrorCodes"/>, or the application's from 100).</summary>
    Refused = 2,

    /// <summary>An <c>ACK</c> other than <c>RateLimited</c> rejected the command; <see cref="SubscribeResult.AckReason"/> says why.</summary>
    Rejected = 3,

    /// <summary>The session ended before an answer came: whether the server took it is unknown, and a new session holds none of it.</summary>
    Dropped = 4,
}

/// <summary>One <c>SubscribeRequest</c>'s answer, as <see cref="GroupSubscriptions"/> reports it.</summary>
/// <param name="RequestId">The id the request was sent under.</param>
/// <param name="Set">The explicit set's index in the catalog's <c>groupSets</c>.</param>
/// <param name="Key">The key.</param>
/// <param name="Op"><see cref="BuiltInCommands.SubscribeOp"/> or <see cref="BuiltInCommands.UnsubscribeOp"/>.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Code">The <c>SOURCE_ERROR</c> code when <see cref="SubscribeOutcome.Refused"/>, else 0.</param>
/// <param name="AckReason">The <c>ACK</c> reason when <see cref="SubscribeOutcome.Rejected"/>, else 0.</param>
public readonly record struct SubscribeResult(ushort RequestId, uint Set, long Key, byte Op, SubscribeOutcome Outcome, ushort Code, byte AckReason);

/// <summary>
/// A session's <c>SubscribeRequest</c>s, from the call to the answer (14 § 4, P13): the same state machine in the .NET, TypeScript and native SDKs, pinned
/// across them by the <c>subscribe-requests</c> vector. It sends nothing itself and keeps no clock: the caller pumps it into its command path, and shows it
/// each applied frame's <c>ACKS</c> and <c>SOURCES</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Queued, then sent, then answered.</b> <see cref="Request"/> queues a request under a fresh id. <see cref="Pump"/> sends what the command path takes,
/// oldest first, and stops at the first it refuses — the local token bucket is empty, or there is no session — so requests leave in the order they were
/// made. <see cref="Observe"/> matches a frame's <c>SOURCES</c> by request id and its <c>ACKS</c> by sequence: an <c>ACK</c> <c>RateLimited</c> puts the
/// request back at the head of the queue, to go again under a new sequence; any other <c>ACK</c> ends it <see cref="SubscribeOutcome.Rejected"/>. ACKs are
/// read before SOURCES.
/// </para>
/// <para>
/// <b>An ACK can be lost; an answer cannot.</b> A session further behind than the server's ACK history never sees a refusal's ACK, and that request stays
/// sent with no answer. Asking again is safe — a subscribe of a key held, and an unsubscribe of one not held, are answered like the first — so a caller that
/// waits too long may give up and ask again.
/// </para>
/// <para>Not thread-safe: a caller that pumps and observes from different threads serialises them.</para>
/// </remarks>
public sealed class GroupSubscriptions
{
    /// <summary>Sends one command, returning the sequence it went under, or a negative value when it was not sent (no token, no session).</summary>
    public delegate int TrySend(MessagePlan command, RecordValues values);

    private sealed class Entry
    {
        public ushort RequestId;
        public uint Set;
        public long Key;
        public byte Op;
        public ushort Seq;
    }

    private readonly CatalogPlan _plan;
    private readonly MessagePlan _command;
    private readonly List<Entry> _queued = [];
    private readonly Dictionary<ushort, Entry> _sent = [];
    private readonly Dictionary<ushort, ushort> _requestBySeq = [];
    private ushort _nextId;

    /// <summary>A session's requests over <paramref name="plan"/>.</summary>
    /// <param name="plan">The catalog the session negotiated.</param>
    public GroupSubscriptions(CatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
        _command = plan.CommandByName(BuiltInCommands.SubscribeRequest);
    }

    /// <summary>Raised for each request that ends, in the order they end.</summary>
    public event Action<SubscribeResult> Answered;

    /// <summary>Whether the catalog lists <c>SubscribeRequest</c>: some explicit set takes a client's requests.</summary>
    public bool Available => _command != null;

    /// <summary>Requests not yet sent.</summary>
    public int QueuedCount => _queued.Count;

    /// <summary>Requests sent and not yet answered.</summary>
    public int SentCount => _sent.Count;

    /// <summary>Queues a request for one key of an explicit set.</summary>
    /// <param name="set">The set's index in the catalog's <c>groupSets</c>.</param>
    /// <param name="key">The key, never 0.</param>
    /// <param name="op"><see cref="BuiltInCommands.SubscribeOp"/> or <see cref="BuiltInCommands.UnsubscribeOp"/>.</param>
    /// <returns>The request's id, which its answer carries.</returns>
    /// <exception cref="InvalidOperationException">The catalog lists no <c>SubscribeRequest</c>.</exception>
    /// <exception cref="ArgumentException">The set is not an explicit one, the key is 0 or the op is neither.</exception>
    public ushort Request(uint set, long key, byte op)
    {
        if (_command == null)
        {
            throw new InvalidOperationException("the catalog lists no SubscribeRequest: no explicit group set takes a client's requests");
        }

        if (set >= (uint)_plan.GroupSets.Length || _plan.GroupSets[set].Selector != CatalogGroupSet.ExplicitSelector)
        {
            throw new ArgumentException($"group set {set} is not an explicit set of this catalog", nameof(set));
        }

        if (key == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "key 0 names no group");
        }

        if (op is not (BuiltInCommands.SubscribeOp or BuiltInCommands.UnsubscribeOp))
        {
            throw new ArgumentOutOfRangeException(nameof(op), op, "op is SubscribeOp or UnsubscribeOp");
        }

        var entry = new Entry { RequestId = NextId(), Set = set, Key = key, Op = op };
        _queued.Add(entry);
        return entry.RequestId;
    }

    /// <summary>Sends the queued requests, oldest first, until <paramref name="send"/> refuses one.</summary>
    /// <param name="send">The command path.</param>
    /// <param name="max">The most to send in this call.</param>
    /// <returns>How many were sent.</returns>
    public int Pump(TrySend send, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(send);
        var sent = 0;
        while (sent < max && _queued.Count > 0)
        {
            var entry = _queued[0];
            var values = new RecordValues
            {
                [BuiltInCommands.SubscribeRequestIdField] = FieldValue.Of(entry.RequestId),
                [BuiltInCommands.SubscribeSetField] = FieldValue.Of(entry.Set),
                [BuiltInCommands.SubscribeKeyField] = FieldValue.OfInt64(entry.Key),
                [BuiltInCommands.SubscribeOpField] = FieldValue.Of(entry.Op),
            };

            var seq = send(_command, values);
            if (seq < 0)
            {
                break;
            }

            _queued.RemoveAt(0);
            entry.Seq = (ushort)seq;
            _sent[entry.RequestId] = entry;
            _requestBySeq[entry.Seq] = entry.RequestId;
            sent++;
        }

        return sent;
    }

    /// <summary>Matches one applied frame's <c>ACKS</c>, then its <c>SOURCES</c>, against the requests sent.</summary>
    /// <param name="acks">The frame's <c>ACKS</c> records.</param>
    /// <param name="sources">The frame's <c>SOURCE_APPLIED</c> / <c>SOURCE_ERROR</c> answers.</param>
    public void Observe(IReadOnlyList<(ushort Seq, byte Reason)> acks, IReadOnlyList<(ushort RequestId, byte Status, ushort Code)> sources)
    {
        ArgumentNullException.ThrowIfNull(acks);
        ArgumentNullException.ThrowIfNull(sources);
        var requeued = 0;
        foreach (var (seq, reason) in acks)
        {
            if (!_requestBySeq.Remove(seq, out var id) || !_sent.Remove(id, out var entry))
            {
                continue;
            }

            if (reason == AckReasons.RateLimited)
            {
                // Back at the head, ahead of what was never sent, in the order the refused ones went: the queue's order is the order of the calls.
                _queued.Insert(requeued++, entry);
                continue;
            }

            Answered?.Invoke(new SubscribeResult(entry.RequestId, entry.Set, entry.Key, entry.Op, SubscribeOutcome.Rejected, 0, reason));
        }

        foreach (var (requestId, status, code) in sources)
        {
            if (!_sent.Remove(requestId, out var entry))
            {
                continue;
            }

            _requestBySeq.Remove(entry.Seq);
            Answered?.Invoke(status == SourceStatus.Applied
                ? new SubscribeResult(entry.RequestId, entry.Set, entry.Key, entry.Op, SubscribeOutcome.Applied, 0, 0)
                : new SubscribeResult(entry.RequestId, entry.Set, entry.Key, entry.Op, SubscribeOutcome.Refused, code, 0));
        }
    }

    /// <summary>Ends every request <see cref="SubscribeOutcome.Dropped"/> — the sent ones first, by id, then the queued in order: the session is over.</summary>
    public void Reset()
    {
        var sent = new List<Entry>(_sent.Values);
        sent.Sort((a, b) => a.RequestId.CompareTo(b.RequestId));
        var queued = new List<Entry>(_queued);
        _sent.Clear();
        _requestBySeq.Clear();
        _queued.Clear();
        foreach (var entry in sent)
        {
            Answered?.Invoke(new SubscribeResult(entry.RequestId, entry.Set, entry.Key, entry.Op, SubscribeOutcome.Dropped, 0, 0));
        }

        foreach (var entry in queued)
        {
            Answered?.Invoke(new SubscribeResult(entry.RequestId, entry.Set, entry.Key, entry.Op, SubscribeOutcome.Dropped, 0, 0));
        }
    }

    // 1 to 65 535, never one still waiting for its answer.
    private ushort NextId()
    {
        if (_queued.Count + _sent.Count >= ushort.MaxValue)
        {
            throw new InvalidOperationException("65 535 requests are waiting for their answers");
        }

        while (true)
        {
            _nextId = _nextId == ushort.MaxValue ? (ushort)1 : (ushort)(_nextId + 1);
            if (!_sent.ContainsKey(_nextId) && !_queued.Exists(e => e.RequestId == _nextId))
            {
                return _nextId;
            }
        }
    }
}

/// <summary>A command type's token bucket, from its catalog <c>rate</c>: full at first, refilled continuously — the TypeScript and native queues' own.</summary>
internal sealed class CommandBucket
{
    private readonly double _perSec;
    private readonly double _burst;
    private double _tokens;
    private double _lastMs = double.NaN;

    public CommandBucket(CatalogCommandRate rate)
    {
        _perSec = rate?.PerSec ?? 0;
        _burst = rate?.Burst ?? 0;
        _tokens = _burst;
    }

    /// <summary>Takes one token at <paramref name="nowMs"/>; false when none is left. A command with no rate always takes.</summary>
    public bool Take(double nowMs)
    {
        if (_burst <= 0)
        {
            return true;
        }

        var elapsed = double.IsNaN(_lastMs) ? 0 : Math.Max(0, nowMs - _lastMs);
        _lastMs = nowMs;
        _tokens = Math.Min(_burst, _tokens + (elapsed / 1000d * _perSec));
        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}
