using System;
using System.Collections.Generic;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>An answer a session's client is owed for a <c>SubscribeRequest</c> (14 § 4, P13), carried until a published frame has said it.</summary>
internal struct GroupAnswer
{
    /// <summary>When it is due: on the key's completion, at once (an error), or once the key is no longer held.</summary>
    public const byte WhenComplete = 0;
    public const byte Now = 1;
    public const byte WhenReleased = 2;

    /// <summary>The answer's number in its session, so a published frame retires exactly what it said.</summary>
    public ulong Seq;

    public ushort RequestId;
    public byte Due;
    public int Set;
    public long Key;

    /// <summary>The <c>SOURCE_ERROR</c> code; 0 for <c>SOURCE_APPLIED</c>.</summary>
    public ushort Code;
}

/// <summary>One session's keys of the explicit group sets — asked for by its client, chosen by the server, or both — and the answers it is owed.</summary>
internal sealed class SessionGroupRequests
{
    public const byte FromClient = 1;
    public const byte FromServer = 2;

    public ushort Generation;

    // Per set: the keys, ascending, and where each came from; null for a set that is not explicit.
    public readonly long[][] Keys;
    public readonly byte[][] Origin;
    public readonly int[] Count;
    public readonly List<GroupAnswer> Answers = [];
    public ulong NextSeq;

    public SessionGroupRequests(CompiledGroupSet[] sets, int max)
    {
        Keys = new long[sets.Length][];
        Origin = new byte[sets.Length][];
        Count = new int[sets.Length];
        for (var s = 0; s < sets.Length; s++)
        {
            if (sets[s].Selector == GroupSelector.Explicit)
            {
                Keys[s] = new long[max];
                Origin[s] = new byte[max];
            }
        }
    }

    public void Clear()
    {
        Array.Clear(Count);
        Answers.Clear();
    }

    /// <summary>The key's position among the set's keys, or its insertion point's complement.</summary>
    public int Find(int set, long key) => Keys[set].AsSpan(0, Count[set]).BinarySearch(key);

    /// <summary>Gives <paramref name="key"/> the origin bits <paramref name="bits"/> on top of what it has, inserting it if new. False when the set is full.</summary>
    public bool Add(int set, long key, byte bits)
    {
        var at = Find(set, key);
        if (at >= 0)
        {
            Origin[set][at] |= bits;
            return true;
        }

        var n = Count[set];
        if (n == Keys[set].Length)
        {
            return false;
        }

        at = ~at;
        Array.Copy(Keys[set], at, Keys[set], at + 1, n - at);
        Array.Copy(Origin[set], at, Origin[set], at + 1, n - at);
        Keys[set][at] = key;
        Origin[set][at] = bits;
        Count[set] = n + 1;
        return true;
    }

    /// <summary>Takes the origin bits <paramref name="bits"/> off <paramref name="key"/>, and the key off the set once it has none.</summary>
    public void Remove(int set, long key, byte bits)
    {
        var at = Find(set, key);
        if (at < 0)
        {
            return;
        }

        Origin[set][at] &= (byte)~bits;
        if (Origin[set][at] != 0)
        {
            return;
        }

        var n = Count[set] - 1;
        Array.Copy(Keys[set], at + 1, Keys[set], at, n - at);
        Array.Copy(Origin[set], at + 1, Origin[set], at, n - at);
        Count[set] = n;
    }

    public void Answer(ushort requestId, byte due, int set, long key, ushort code) =>
        Answers.Add(new GroupAnswer { Seq = ++NextSeq, RequestId = requestId, Due = due, Set = set, Key = key, Code = code });
}

/// <summary>
/// The explicit group sets' per-session state (14 § 4, P13–P14): which keys each session holds because its client asked for them or the server chose them,
/// and the <c>SOURCE_APPLIED</c> / <c>SOURCE_ERROR</c> answers its client is owed. Engine-wide, by session slot: a session keeps its subscriptions across a
/// realm switch and a <c>RESET</c>, which fill and complete them again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who touches what.</b> A client's request is taken by the ingress drain, which is chunked over sessions: one session's state is written by one chunk.
/// The server's choice (<see cref="SetServerKeys"/>) is applied by the serial session pass. The group gather and the frame assembly read a session's keys
/// and answers while gathering that session, and a published frame retires the answers it said — again on the thread assembling that session.
/// </para>
/// <para>
/// <b>Bounded.</b> A session holds at most <c>MaxGroupsPerSession</c> keys per explicit set, client and server keys together; the answers it is owed are
/// bounded by the requests the command's rate admits between two published frames.
/// </para>
/// </remarks>
internal sealed class GroupRequests
{
    private readonly CompiledGroupSet[] _sets;
    private readonly int _max;
    private readonly SessionGroupRequests[] _bySlot;

    // By profile name: which sets the profile (or any of its realm-kind variants) names.
    private readonly Dictionary<string, bool[]> _named = new(StringComparer.Ordinal);

    /// <summary>Requests taken, refused and answered, cumulative. Diagnostics.</summary>
    public long Requested;

    /// <inheritdoc cref="Requested"/>
    public long Refused;

    /// <inheritdoc cref="Requested"/>
    public long Answered;

    /// <summary>Server keys a <c>SetSources</c> could not add because the set was full, cumulative.</summary>
    public long ServerKeysOverLimit;

    public GroupRequests(CompiledGroupSet[] sets, int maxGroupsPerSession, int maxSessions, SubscriptionsRegistry registry)
    {
        _sets = sets;
        _max = Math.Max(1, maxGroupsPerSession);
        _bySlot = new SessionGroupRequests[Math.Max(1, maxSessions) + 1];
        foreach (var profile in registry.Profiles)
        {
            var named = new bool[sets.Length];
            Name(named, profile.Observers);
            foreach (var variant in profile.Variants.Values)
            {
                Name(named, variant.Observers);
            }

            _named[profile.Name] = named;
        }
    }

    /// <summary>The index of the set named <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name)
    {
        for (var s = 0; s < _sets.Length; s++)
        {
            if (string.Equals(_sets[s].Name, name, StringComparison.Ordinal))
            {
                return s;
            }
        }

        return -1;
    }

    /// <summary>The explicit sets' names: what a system's <see cref="SessionRequest.SetSources"/> may name.</summary>
    public HashSet<string> ExplicitNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in _sets)
        {
            if (set.Selector == GroupSelector.Explicit)
            {
                names.Add(set.Name);
            }
        }

        return names;
    }

    /// <summary>Whether any declared set is explicit: the one test a session's frame starts its answers with.</summary>
    public bool HasExplicit
    {
        get
        {
            foreach (var set in _sets)
            {
                if (set.Selector == GroupSelector.Explicit)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void Name(bool[] named, IReadOnlyList<ObserverDeclaration> observers)
    {
        foreach (var observer in observers)
        {
            foreach (var interest in observer.GroupInterests)
            {
                for (var s = 0; s < _sets.Length; s++)
                {
                    named[s] |= string.Equals(_sets[s].Name, interest.Set, StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>A session's state, or null when it has none — never asked, never chosen for, or its slot now another session's.</summary>
    public SessionGroupRequests Of(SessionId session)
    {
        var slot = session.Slot;
        var state = (uint)slot < (uint)_bySlot.Length ? _bySlot[slot] : null;
        return state != null && state.Generation == session.Generation ? state : null;
    }

    private SessionGroupRequests Ensure(SessionId session)
    {
        var slot = session.Slot;
        if ((uint)slot >= (uint)_bySlot.Length)
        {
            return null;
        }

        var state = _bySlot[slot] ??= new SessionGroupRequests(_sets, _max);
        if (state.Generation != session.Generation)
        {
            state.Clear();
            state.Generation = session.Generation;
        }

        return state;
    }

    /// <summary>The keys of explicit set <paramref name="set"/> the session holds, ascending; empty when none.</summary>
    public ReadOnlySpan<long> KeysOf(SessionId session, int set)
    {
        var state = Of(session);
        return state == null || state.Keys[set] == null ? default : state.Keys[set].AsSpan(0, state.Count[set]);
    }

    /// <summary>
    /// Takes one client request (P13): validated against the set and the session's profile and limit, then decided by the application. Its answer is owed
    /// from now on — an error at once, an applied one once the key is complete, or released. Called by the ingress drain, on the session's own chunk.
    /// </summary>
    public void Request(SessionId session, string profileName, ushort requestId, uint set, long key, byte op)
    {
        var state = Ensure(session);
        if (state == null)
        {
            return;
        }

        System.Threading.Interlocked.Increment(ref Requested);
        var s = (int)set;
        if (op is not (BuiltInCommands.SubscribeOp or BuiltInCommands.UnsubscribeOp))
        {
            Refuse(state, requestId, s, key, SourceErrorCodes.InvalidOp);
            return;
        }

        if (set >= (uint)_sets.Length || _sets[s].Selector != GroupSelector.Explicit)
        {
            Refuse(state, requestId, s, key, SourceErrorCodes.UnknownSet);
            return;
        }

        if (profileName == null || !_named.TryGetValue(profileName, out var named) || !named[s])
        {
            Refuse(state, requestId, s, key, SourceErrorCodes.NotInProfile);
            return;
        }

        if (key == 0)
        {
            Refuse(state, requestId, s, key, SourceErrorCodes.InvalidKey);
            return;
        }

        if (op == BuiltInCommands.UnsubscribeOp)
        {
            state.Remove(s, key, SessionGroupRequests.FromClient);
            state.Answer(requestId, GroupAnswer.WhenReleased, s, key, 0);
            return;
        }

        var at = state.Find(s, key);
        if (at >= 0 && (state.Origin[s][at] & SessionGroupRequests.FromClient) != 0)
        {
            // Asked again: idempotent, answered like the first.
            state.Answer(requestId, GroupAnswer.WhenComplete, s, key, 0);
            return;
        }

        if (at < 0 && state.Count[s] == _max)
        {
            Refuse(state, requestId, s, key, SourceErrorCodes.Limit);
            return;
        }

        var admit = _sets[s].Admit;
        var decision = admit == null ? GroupDecision.Deny() : admit(new GroupRequest(session, _sets[s].Name, key));
        if (!decision.Allowed)
        {
            Refuse(state, requestId, s, key, decision.Code);
            return;
        }

        state.Add(s, key, SessionGroupRequests.FromClient);
        state.Answer(requestId, GroupAnswer.WhenComplete, s, key, 0);
    }

    private void Refuse(SessionGroupRequests state, ushort requestId, int set, long key, ushort code)
    {
        System.Threading.Interlocked.Increment(ref Refused);
        state.Answer(requestId, GroupAnswer.Now, set, key, code);
    }

    /// <summary>
    /// Replaces the keys the server chose for the session in an explicit set (P14): diffed against what it chose before, idempotent; the client's own keys
    /// are untouched. Keys past the set's limit are not added, and counted. Serial: the session pass.
    /// </summary>
    public bool SetServerKeys(SessionId session, int set, long[] keys)
    {
        if ((uint)set >= (uint)_sets.Length || _sets[set].Selector != GroupSelector.Explicit)
        {
            return false;
        }

        var state = Ensure(session);
        if (state == null)
        {
            return false;
        }

        // Off every key the server no longer names, then on every one it does.
        for (var i = state.Count[set] - 1; i >= 0; i--)
        {
            var key = state.Keys[set][i];
            if ((state.Origin[set][i] & SessionGroupRequests.FromServer) != 0 && Array.IndexOf(keys, key) < 0)
            {
                state.Remove(set, key, SessionGroupRequests.FromServer);
            }
        }

        foreach (var key in keys)
        {
            if (key != 0 && !state.Add(set, key, SessionGroupRequests.FromServer))
            {
                ServerKeysOverLimit++;
            }
        }

        return true;
    }

    /// <summary>
    /// The answers due in the frame being assembled for <paramref name="session"/>, after its group gather: each error, each subscription whose key is now
    /// complete (or no longer asked for), each unsubscription whose key is no longer held. Appended to the frame's group records — after the gather's own, so
    /// an answer follows the completion it reports — and noted, so a published frame retires them.
    /// </summary>
    public void Emit(SessionId session, PushReplication push, FrameWorkerScratch scratch)
    {
        scratch.Answered.Clear();
        var state = Of(session);
        if (state == null || state.Answers.Count == 0)
        {
            return;
        }

        foreach (var answer in state.Answers)
        {
            bool due;
            switch (answer.Due)
            {
                case GroupAnswer.Now:
                    due = true;
                    break;
                case GroupAnswer.WhenComplete:
                    due = state.Find(answer.Set, answer.Key) < 0 || push.GroupKeyStateOf(session, answer.Set, answer.Key) == GroupKeyState.Complete;
                    break;
                default:
                    due = state.Find(answer.Set, answer.Key) >= 0 || push.GroupKeyStateOf(session, answer.Set, answer.Key) == GroupKeyState.NotHeld;
                    break;
            }

            if (!due)
            {
                continue;
            }

            scratch.GroupRecords.Add(answer.Code == 0
                ? new GroupRecord(SourceStatus.Applied, answer.Set, answer.RequestId)
                : new GroupRecord(SourceStatus.Error, answer.Set, answer.RequestId | ((long)answer.Code << 16)));
            scratch.Answered.Add(answer.Seq);
        }
    }

    /// <summary>The answers a published frame said, retired. On the thread assembling the session.</summary>
    public void Retire(SessionId session, List<ulong> answered)
    {
        if (answered.Count == 0)
        {
            return;
        }

        var state = Of(session);
        if (state == null)
        {
            return;
        }

        var kept = 0;
        for (var i = 0; i < state.Answers.Count; i++)
        {
            if (answered.Contains(state.Answers[i].Seq))
            {
                continue;
            }

            state.Answers[kept++] = state.Answers[i];
        }

        System.Threading.Interlocked.Add(ref Answered, state.Answers.Count - kept);
        state.Answers.RemoveRange(kept, state.Answers.Count - kept);
        answered.Clear();
    }
}

/// <summary>Where a session stands with one key after its frame's gather: what an answer waits for.</summary>
internal enum GroupKeyState : byte
{
    NotHeld = 0,
    Filling = 1,
    Complete = 2,
}
