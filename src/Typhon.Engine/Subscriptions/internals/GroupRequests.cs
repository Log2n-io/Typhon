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

    // Per set: the keys, ascending, and where each came from; null for a set that is not explicit. Grown with the keys held, to at most the bound (#1407).
    public readonly long[][] Keys;
    public readonly byte[][] Origin;
    public readonly int[] Count;
    public readonly List<GroupAnswer> Answers = [];
    public ulong NextSeq;

    // MergeServerKeys' target, copied back into the set's own arrays — which grow to the set's own size — so no allocation follows once each has grown.
    private long[] _spareKeys = [];
    private byte[] _spareOrigin = [];

    private readonly int _max;

    // A set's first capacity, and the size past which a slot handed to a new session gives its arrays back.
    private const int InitialCapacity = 8;
    private const int RetainedCapacity = 64;

    // Requests the drain took this tick, decided by the next tick's: (requestId, set, key, op).
    public readonly List<(ushort RequestId, uint Set, long Key, byte Op)> Parked = [];

    public SessionGroupRequests(CompiledGroupSet[] sets, int max)
    {
        _max = max;
        Keys = new long[sets.Length][];
        Origin = new byte[sets.Length][];
        Count = new int[sets.Length];
        for (var s = 0; s < sets.Length; s++)
        {
            if (sets[s].Selector == GroupSelector.Explicit)
            {
                Keys[s] = [];
                Origin[s] = [];
            }
        }
    }

    /// <summary>A new session in the slot: nothing held, and arrays a large previous session grew given back (#1407).</summary>
    public void Clear()
    {
        Array.Clear(Count);
        Answers.Clear();
        Parked.Clear();
        for (var s = 0; s < Keys.Length; s++)
        {
            if (Keys[s] != null && Keys[s].Length > RetainedCapacity)
            {
                Keys[s] = [];
                Origin[s] = [];
            }
        }

        if (_spareKeys.Length > RetainedCapacity)
        {
            _spareKeys = [];
            _spareOrigin = [];
        }
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
        if (n == _max)
        {
            return false;
        }

        if (n == Keys[set].Length)
        {
            var capacity = Math.Min(_max, Math.Max(InitialCapacity, n * 2));
            Array.Resize(ref Keys[set], capacity);
            Array.Resize(ref Origin[set], capacity);
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

    /// <summary>
    /// Replaces the server's keys of <paramref name="set"/> with <paramref name="keys"/> (sorted; 0 and duplicates skipped) in one merge: the client's keys
    /// kept, the server's it no longer names dropped, new ones added in key order while the set has room.
    /// </summary>
    /// <returns>How many new keys did not fit.</returns>
    public int MergeServerKeys(int set, ReadOnlySpan<long> keys)
    {
        var old = Keys[set];
        var origin = Origin[set];
        var n = Count[set];

        // What stays of the old keys: the client's, and the server's it names again. The room left is the new ones'.
        var kept = 0;
        var newKeys = 0;
        var walk = 0;
        for (var j = 0; j < keys.Length; j++)
        {
            if (keys[j] == 0 || (j > 0 && keys[j] == keys[j - 1]))
            {
                continue;
            }

            while (walk < n && old[walk] < keys[j])
            {
                kept += (origin[walk] & ~FromServer) != 0 ? 1 : 0;
                walk++;
            }

            if (walk < n && old[walk] == keys[j])
            {
                kept++;
                walk++;
            }
            else
            {
                newKeys++;
            }
        }

        for (; walk < n; walk++)
        {
            kept += (origin[walk] & ~FromServer) != 0 ? 1 : 0;
        }

        var room = _max - kept;
        var needed = kept + Math.Min(room, newKeys);
        if (_spareKeys.Length < needed)
        {
            var capacity = Math.Min(_max, Math.Max(InitialCapacity, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)needed)));
            _spareKeys = new long[capacity];
            _spareOrigin = new byte[capacity];
        }

        var w = 0;
        var i = 0;
        var k = 0;
        var overLimit = 0;
        while (i < n || k < keys.Length)
        {
            if (k < keys.Length && (keys[k] == 0 || (k > 0 && keys[k] == keys[k - 1])))
            {
                k++;
                continue;
            }

            if (k >= keys.Length || (i < n && old[i] < keys[k]))
            {
                // Not named by the server now: kept for its other origins.
                var bits = (byte)(origin[i] & ~FromServer);
                if (bits != 0)
                {
                    _spareKeys[w] = old[i];
                    _spareOrigin[w++] = bits;
                }

                i++;
            }
            else if (i >= n || keys[k] < old[i])
            {
                if (room > 0)
                {
                    _spareKeys[w] = keys[k];
                    _spareOrigin[w++] = FromServer;
                    room--;
                }
                else
                {
                    overLimit++;
                }

                k++;
            }
            else
            {
                _spareKeys[w] = old[i];
                _spareOrigin[w++] = (byte)(origin[i++] | FromServer);
                k++;
            }
        }

        // Back into the set's own arrays, grown to its own size when they no longer fit: the spare stays the session's scratch.
        if (old.Length < w)
        {
            var capacity = Math.Min(_max, Math.Max(InitialCapacity, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)w)));
            Keys[set] = old = new long[capacity];
            Origin[set] = origin = new byte[capacity];
        }

        _spareKeys.AsSpan(0, w).CopyTo(old);
        _spareOrigin.AsSpan(0, w).CopyTo(origin);

        Count[set] = w;
        return overLimit;
    }
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
/// <b>Bounded.</b> A session holds at most <c>MaxGroupsPerSession</c> keys per explicit set, client and server keys together, and is owed at most
/// <see cref="MaxOwedAnswers"/> answers: a request past that is not taken, and the drain ACKs it <c>RateLimited</c> like one the command's rate refused.
/// The command's burst is <see cref="SubscribeBurstOf"/> the bound (the catalog says so), so a login subscribing every key it may hold, up to 1 024, is
/// admitted at once. A session's keys cost what it holds, never the bound (#1407).
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

    /// <summary>Requests whose application handler threw, refused <see cref="SourceErrorCodes.Denied"/>, cumulative. Diagnostic: a handler must not throw.</summary>
    public long AdmitFaults;

    /// <summary>
    /// The most answers a session is owed at once: twice the <c>SubscribeRequest</c> burst, and at least 64 — room for a login that subscribes a burst's
    /// keys while the first ones fill. A frame not published — the link congested, the session degraded — keeps its answers owed; a client that goes on
    /// asking meanwhile is refused at the drain rather than grow them. Follows the burst, not the bound, so a large bound grows no answer list (#1407).
    /// </summary>
    public int MaxOwedAnswers { get; }

    /// <summary>The <c>SubscribeRequest</c> burst a bound gives: the bound, clamped to what the catalog validators accept.</summary>
    public static int SubscribeBurstOf(int maxGroupsPerSession) =>
        Math.Clamp(maxGroupsPerSession, BuiltInCommands.MinSubscribeBurst, BuiltInCommands.MaxSubscribeBurst);

    public GroupRequests(CompiledGroupSet[] sets, int maxGroupsPerSession, int maxSessions, SubscriptionsRegistry registry)
    {
        _sets = sets;
        _max = Math.Max(1, maxGroupsPerSession);
        MaxOwedAnswers = Math.Max(64, 2 * SubscribeBurstOf(_max));
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
    /// <returns>False when the request was not taken because the session is owed <see cref="MaxOwedAnswers"/> answers: the drain ACKs it.</returns>
    /// <remarks>Decided at once. The drain parks a request instead (<see cref="Park"/>) and decides it the next tick.</remarks>
    public bool Request(SessionId session, string profileName, ushort requestId, uint set, long key, byte op)
    {
        var state = Ensure(session);
        if (state == null)
        {
            return true;
        }

        if (state.Answers.Count >= MaxOwedAnswers)
        {
            return false;
        }

        System.Threading.Interlocked.Increment(ref Requested);
        Decide(state, session, profileName, requestId, set, key, op);
        return true;
    }

    /// <summary>
    /// Takes one client request from the ingress drain, to be decided by the next tick's (<see cref="DecideParked"/>). A request sent right after
    /// <c>WELCOME</c> is drained in the tick that publishes its session's <c>Opened</c>, before the application's handler binds the profile, which only
    /// applies at the next tick's start: decided then, it is decided under that profile — never refused <c>NotInProfile</c> for having been quick.
    /// </summary>
    /// <returns>False when the session is owed, or has parked, <see cref="MaxOwedAnswers"/> answers: the drain ACKs it.</returns>
    public bool Park(SessionId session, ushort requestId, uint set, long key, byte op)
    {
        var state = Ensure(session);
        if (state == null)
        {
            return true;
        }

        if (state.Answers.Count + state.Parked.Count >= MaxOwedAnswers)
        {
            return false;
        }

        state.Parked.Add((requestId, set, key, op));
        return true;
    }

    /// <summary>Decides the requests the last tick's drain parked for <paramref name="session"/>, in order. Called by the drain, on the session's chunk.</summary>
    public void DecideParked(SessionId session, string profileName)
    {
        var state = Of(session);
        if (state == null || state.Parked.Count == 0)
        {
            return;
        }

        foreach (var (requestId, set, key, op) in state.Parked)
        {
            System.Threading.Interlocked.Increment(ref Requested);
            Decide(state, session, profileName, requestId, set, key, op);
        }

        state.Parked.Clear();
    }

    private void Decide(SessionGroupRequests state, SessionId session, string profileName, ushort requestId, uint set, long key, byte op)
    {
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

        if (key == 0 || !ClusterFieldReader.Fits(key, _sets[s].KeyType))
        {
            // 0 names no group; a key no member's field can hold names one nothing is ever in.
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
        if (admit == null)
        {
            // A set the server alone fills (SetSources): not one a client may ask for, so not one it knows as such.
            Refuse(state, requestId, s, key, SourceErrorCodes.UnknownSet);
            return;
        }

        GroupDecision decision;
        try
        {
            decision = admit(new GroupRequest(session, _sets[s].Name, key));
        }
        catch (Exception)
        {
            // An application's handler runs on the ingress drain: its fault refuses the one request, never the tick.
            System.Threading.Interlocked.Increment(ref AdmitFaults);
            decision = GroupDecision.Deny();
        }

        if (!decision.Allowed)
        {
            // default(GroupDecision) carries code 0, which the wire reads as APPLIED: refused as Denied.
            Refuse(state, requestId, s, key, decision.Code == 0 ? SourceErrorCodes.Denied : decision.Code);
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

        // Off every key the server no longer names, then on every one it does, in one merge over the sorted copy: O(keys), not a shift per key.
        Array.Sort(keys);
        ServerKeysOverLimit += state.MergeServerKeys(set, keys);
        return true;
    }

    /// <summary>How many answers a session is owed: the most <see cref="Emit"/> can add to its next frame, which the frame budget reserves (15 § 4.4).</summary>
    public int OwedAnswers(SessionId session) => Of(session)?.Answers.Count ?? 0;

    /// <summary>
    /// The answers due in the frame being assembled for <paramref name="session"/>, after its group gather: each error, each subscription whose key is now
    /// complete (or no longer asked for), each unsubscription whose key is no longer held. Appended to the frame's group records — after the gather's own, so
    /// an answer follows the completion it reports — and noted, so a published frame retires them.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="push">Its track, or null for a session bound to no profile: no key of its is held.</param>
    /// <param name="scratch">The worker's scratch.</param>
    /// <param name="profileName">The profile the session is bound to now, or null.</param>
    /// <remarks>
    /// A subscription still waiting on its key when the session's profile stops naming the set — switched to another, or unbound — would wait for ever,
    /// since nothing fills it: it is answered <see cref="SourceErrorCodes.NotInProfile"/> and the client's key dropped, as if asked under the new profile.
    /// A subscription already answered stays: the session keeps its keys across profiles like across realms, and they fill again under one naming the set.
    /// </remarks>
    /// <param name="holdCompletions">
    /// The frame leaves an enter owed (15 § 4.6): an answer waiting for its key's completion is held, for a key already complete included — its store
    /// lacks a member.
    /// </param>
    public void Emit(SessionId session, PushReplication push, FrameWorkerScratch scratch, string profileName, bool holdCompletions = false)
    {
        scratch.Answered.Clear();
        var state = Of(session);
        if (state == null || state.Answers.Count == 0)
        {
            return;
        }

        var named = profileName != null && _named.TryGetValue(profileName, out var n) ? n : null;
        var answers = state.Answers;
        for (var i = 0; i < answers.Count; i++)
        {
            var answer = answers[i];
            bool due;
            switch (answer.Due)
            {
                case GroupAnswer.Now:
                    due = true;
                    break;
                case GroupAnswer.WhenComplete:
                    if (state.Find(answer.Set, answer.Key) >= 0 && (named == null || !named[answer.Set]))
                    {
                        state.Remove(answer.Set, answer.Key, SessionGroupRequests.FromClient);
                        answer.Code = SourceErrorCodes.NotInProfile;
                        answers[i] = answer;
                        due = true;
                        break;
                    }

                    due = state.Find(answer.Set, answer.Key) < 0
                          || (!holdCompletions && push?.GroupKeyStateOf(session, answer.Set, answer.Key) == GroupKeyState.Complete);
                    break;
                default:
                    due = state.Find(answer.Set, answer.Key) >= 0 || push == null
                        || push.GroupKeyStateOf(session, answer.Set, answer.Key) == GroupKeyState.NotHeld;
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

        // Both in answer order (Emit walks the answers in order): one walk, not a search per answer.
        var kept = 0;
        var walk = 0;
        for (var i = 0; i < state.Answers.Count; i++)
        {
            var seq = state.Answers[i].Seq;
            while (walk < answered.Count && answered[walk] < seq)
            {
                walk++;
            }

            if (walk < answered.Count && answered[walk] == seq)
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
