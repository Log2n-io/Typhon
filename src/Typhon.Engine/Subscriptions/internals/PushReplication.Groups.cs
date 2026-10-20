using System;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>A member's change of group key this tick (14 § 3.8): what the projection read against what its last push stored.</summary>
internal struct GroupKeyChange
{
    public uint NetId;
    public int Set;
    public long Old;
    public long New;
}

// Group sets (14 § 3): the non-generic half — the compiled sets, the tick's key changes, the per-session held keys.
internal abstract unsafe partial class PushReplication
{
    /// <summary>The runtime's compiled group sets, by set index; empty when none is declared, and then nothing below runs.</summary>
    internal CompiledGroupSet[] GroupSets = [];

    /// <summary>
    /// The most keys a session holds per set (<see cref="SubscriptionsOptions.MaxGroupsPerSession"/>): an admission bound only — a session's group state
    /// costs the keys it holds (#1407).
    /// </summary>
    internal int MaxGroupsPerSession;

    /// <summary>The explicit sets' per-session keys and answers (14 § 4), engine-wide; null when no set is explicit.</summary>
    internal GroupRequests Requests;

    /// <summary>
    /// Where <paramref name="session"/> stands with <paramref name="key"/> of <paramref name="set"/> after this frame's gather — the state its frame
    /// commits: what a <c>SubscribeRequest</c>'s answer waits for.
    /// </summary>
    internal virtual GroupKeyState GroupKeyStateOf(SessionId session, int set, long key) => GroupKeyState.NotHeld;

    /// <summary>
    /// The frame owes an enter (15 § 4.6): every <c>GROUP_COMPLETE</c> it decided is taken out of <paramref name="records"/>, and its key's pending cursor
    /// left one short of complete — <i>filling, every member held</i> — so the first frame that owes no enter completes it.
    /// </summary>
    internal virtual void WithholdCompletions(SessionId session, System.Collections.Generic.List<GroupRecord> records)
    {
    }

    /// <summary>
    /// Whether <paramref name="session"/>'s COMMITTED frame holds the member at (<paramref name="block"/>, <paramref name="slot"/>) through one of its keys:
    /// a key the frame held, filled past the member's netId (SUB-33). Read from a system, like <see cref="HoldsCommitted"/>: the frame stage commits the
    /// keys and the projection writes the member's, both after systems run.
    /// </summary>
    internal virtual bool GroupHoldsCommitted(SessionId session, int archetype, nint block, int slot, uint netId) => false;

    /// <summary>A plan's group keys: which set each is in, and its slot in the cold entry; empty for an archetype that is no set's member.</summary>
    internal CompiledGroupKey[] GroupKeysOf(int archetype) => (uint)archetype < (uint)_plans.Length ? _plans[archetype].GroupKeys : [];

    /// <summary>The member at (<paramref name="block"/>, <paramref name="slot"/>)'s projected keys, by key slot; 0 where it has none.</summary>
    internal virtual void ReadMemberKeys(int archetype, nint block, int slot, uint netId, ref GroupFold.KeySlots keys)
    {
    }

    /// <summary>
    /// The key <paramref name="netId"/> had in <paramref name="set"/> before this tick's change, when this tick's index recorded one — a move, a spawn's 0,
    /// a leave's last key. Valid from this tick's index on, until the next projection.
    /// </summary>
    internal bool TryTickOldKey(uint netId, int set, out long oldKey)
    {
        oldKey = 0;
        if (_indexedTick != _tick || !_keyChangeMap.TryGetValue(KeyChangeId(netId, set), out var change))
        {
            return false;
        }

        oldKey = change.Old;
        return true;
    }

    /// <summary>The group state <paramref name="session"/>'s gather has just written — its committed and pending keys — or null when it holds none.</summary>
    internal GroupSessionState GroupsOf(SessionId session)
    {
        var gs = GroupStateAtOrNull(L(session));
        return gs != null && gs.Generation == session.Generation ? gs : null;
    }

    /// <summary>Whether any group set is declared: the one test every group step starts with.</summary>
    internal bool HasGroups => GroupSets.Length > 0;

    // Per worker, this tick's key changes: appended by the projecting worker, merged at the index's finish. The fence's orphans have their own list.
    private protected GroupKeyChange[][] _keyChanges = [];

    // Worker w's count at w × KeyCountStride, one cache line each: every worker bumps its own on every key change.
    private protected int[] _keyChangeCount = [];
    private const int KeyCountStride = 16;
    private protected GroupKeyChange[] _orphanKeyChanges = new GroupKeyChange[16];
    private protected int _orphanKeyChangeCount;

    /// <summary>Key changes this replication recorded, summed over ticks. Diagnostic.</summary>
    internal long KeyChangesRecorded;

    /// <summary>
    /// Regions a session met past <see cref="MaxGroupsPerSession"/> and could not hold, summed over frames; each session is told its own count.
    /// </summary>
    internal long GroupsOverLimit;

    /// <summary>Releases a frame kept held past its release budget, to a later frame (#1407), summed. Diagnostic.</summary>
    internal long GroupReleasesDeferred;

    /// <summary>RESET frames gathered while the session held a key still filling: each refills it from its first member. Diagnostic.</summary>
    internal long GroupResetsMidFill;

    /// <summary>Regions filed, re-filed or removed from their set's index — a region born, gone, or whose padded box moved (#1409), summed.
    /// Diagnostic.</summary>
    internal long RegionRefiles;

    /// <summary>
    /// After every refresh, build the index from scratch beside the real one and count each difference in <see cref="RegionIndexMismatches"/> (#1409's
    /// differential check). Off outside tests: the from-scratch build is what the incremental index exists to avoid.
    /// </summary>
    internal bool VerifyRegionIndexForTest;

    /// <summary>The differential check's findings, and any region the index failed to find where it was filed; zero is the only correct value.</summary>
    internal long RegionIndexMismatches;

    /// <summary>The mutant for the check above: a region whose extent moved stays filed where it was.</summary>
    internal bool RefileSkippedForTest;

    /// <summary>The region refresh's time — the read, the compare, any rebuild — in <c>Stopwatch</c> ticks, summed (#1409). Diagnostic.</summary>
    internal long RegionRefreshTicks;

    /// <summary>Whether any session's committed keys include one still filling; a test's quiet point waits for none.</summary>
    internal virtual bool AnyGroupFillingForTest() => false;

    // Per session slot, per set: the keys the slot last counted in the hub's wanted set (#1410) — the union of its committed and pending sides, sorted —
    // and the session generation it counted them for. A slot that changed nothing since costs a comparison; one that changed costs its difference.
    private long[][][] _contributed = [];
    private int[][] _contributedCount = [];
    private ushort[] _contributedGeneration = [];
    private int _contributedHigh;
    private long[] _unionScratch = new long[16];

    /// <summary>
    /// Brings the hub's wanted set up to date with this realm's sessions (14 § 5, #1410): each slot's keys in a set with a positionless member, committed or
    /// pending, against what the slot counted last time — a key it took is wanted, one it let go of is withdrawn, a slot whose session left withdraws them
    /// all. Serial, at the blocks step.
    /// </summary>
    internal void SyncHeldKeys(PushHub hub)
    {
        if (!HasGroups)
        {
            return;
        }

        var high = Math.Max(_slotHigh, _contributedHigh);
        GrowContributions(high);
        var keep = hub.KeepWithdrawnKeysForTest;
        for (var local = 1; local < high; local++)
        {
            var owner = local < _slotHigh ? _slotOwners[local] : default;
            var gs = owner.IsValid ? GroupStateAtOrNull(local) : null;
            var live = gs != null && gs.Generation == owner.Generation;
            if (!live || _contributedGeneration[local] != owner.Generation)
            {
                // Another session in the slot, or none: what the last one counted goes, whoever it was.
                Withdraw(hub, local, keep);
                _contributedGeneration[local] = live ? owner.Generation : (ushort)0;
                if (!live)
                {
                    continue;
                }
            }

            for (var set = 0; set < gs.SetCount; set++)
            {
                if (hub.IsLazySet(set))
                {
                    Sync(hub, local, set, gs, keep);
                }
            }
        }

        _contributedHigh = _slotHigh;
    }

    /// <summary>Withdraws everything this realm's slots counted: the realm stops being served, and its sessions are gone.</summary>
    internal void WithdrawHeldKeys(PushHub hub)
    {
        for (var local = 1; local < _contributedHigh && local < _contributed.Length; local++)
        {
            Withdraw(hub, local, keep: false);
            _contributedGeneration[local] = 0;
        }

        _contributedHigh = 0;
    }

    /// <summary>For the hub's differential check: every live slot's keys, counted from its two sides as the collection did before #1410.</summary>
    internal void RecountHeldKeysForTest(Dictionary<long, int>[] into)
    {
        for (var local = 1; local < _slotHigh; local++)
        {
            var owner = _slotOwners[local];
            var gs = owner.IsValid ? GroupStateAtOrNull(local) : null;
            if (gs == null || gs.Generation != owner.Generation)
            {
                continue;
            }

            for (var set = 0; set < gs.SetCount; set++)
            {
                if (into[set] == null)
                {
                    continue;
                }

                gs.Sides(set, out var committed, out var pending);
                var keys = new SortedSet<long>();
                foreach (var key in committed.KeySpan)
                {
                    keys.Add(key);
                }

                foreach (var key in pending.KeySpan)
                {
                    keys.Add(key);
                }

                foreach (var key in keys)
                {
                    into[set][key] = into[set].GetValueOrDefault(key) + 1;
                }
            }
        }
    }

    private void GrowContributions(int high)
    {
        if (_contributed.Length >= high)
        {
            return;
        }

        var size = Math.Max(high, _contributed.Length * 2);
        Array.Resize(ref _contributed, size);
        Array.Resize(ref _contributedCount, size);
        Array.Resize(ref _contributedGeneration, size);
    }

    private static bool IsSortedUnique(ReadOnlySpan<long> keys)
    {
        for (var i = 1; i < keys.Length; i++)
        {
            if (keys[i - 1] >= keys[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One slot's keys in one set against what it counted: unchanged — the steady state — costs one or two vectorized comparisons.</summary>
    private void Sync(PushHub hub, int local, int set, GroupSessionState gs, bool keep)
    {
        gs.Sides(set, out var committed, out var pending);
        ReadOnlySpan<long> now;
        if (ReferenceEquals(pending, committed) || pending.KeySpan.SequenceEqual(committed.KeySpan))
        {
            now = committed.KeySpan;
        }
        else
        {
            now = Union(committed.KeySpan, pending.KeySpan);
        }

        var sets = _contributed[local] ??= new long[GroupSets.Length][];
        var counts = _contributedCount[local] ??= new int[GroupSets.Length];
        var mine = sets[set] ?? [];
        var count = counts[set];
        if (now.SequenceEqual(mine.AsSpan(0, count)))
        {
            return;
        }

        // Both sorted and unique: one walk finds what was taken and what was let go of. A side out of order would skew the counts silently, so a debug build
        // checks the one this does not build itself (the explicit sets' server-set keys are sorted by GroupRequests.SetServerKeys).
        System.Diagnostics.Debug.Assert(IsSortedUnique(now), "a session's held keys must be sorted and unique for the merge walk");
        var a = 0;
        var b = 0;
        while (a < now.Length || b < count)
        {
            if (b == count || (a < now.Length && now[a] < mine[b]))
            {
                hub.WantKey(set, now[a++]);
            }
            else if (a == now.Length || mine[b] < now[a])
            {
                if (!keep)
                {
                    hub.UnwantKey(set, mine[b]);
                }

                b++;
            }
            else
            {
                a++;
                b++;
            }
        }

        if (mine.Length < now.Length)
        {
            mine = new long[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(8, now.Length))];
            sets[set] = mine;
        }

        now.CopyTo(mine);
        counts[set] = now.Length;
    }

    /// <summary>Withdraws every key slot <paramref name="local"/> counted, in every set.</summary>
    private void Withdraw(PushHub hub, int local, bool keep)
    {
        if (local >= _contributed.Length || _contributed[local] is not { } sets)
        {
            return;
        }

        var counts = _contributedCount[local];
        for (var set = 0; set < sets.Length; set++)
        {
            if (!keep)
            {
                for (var i = 0; i < counts[set]; i++)
                {
                    hub.UnwantKey(set, sets[set][i]);
                }
            }

            counts[set] = 0;
        }
    }

    /// <summary>The sorted union of two sorted, unique key lists, in a scratch reused across slots.</summary>
    private ReadOnlySpan<long> Union(ReadOnlySpan<long> x, ReadOnlySpan<long> y)
    {
        if (_unionScratch.Length < x.Length + y.Length)
        {
            _unionScratch = new long[(int)BitOperations.RoundUpToPowerOf2((uint)(x.Length + y.Length))];
        }

        var n = 0;
        var i = 0;
        var j = 0;
        while (i < x.Length || j < y.Length)
        {
            if (j == y.Length || (i < x.Length && x[i] < y[j]))
            {
                _unionScratch[n++] = x[i++];
            }
            else if (i == x.Length || y[j] < x[i])
            {
                _unionScratch[n++] = y[j++];
            }
            else
            {
                _unionScratch[n++] = x[i++];
                j++;
            }
        }

        return _unionScratch.AsSpan(0, n);
    }

    // A slot's group state, or null when it has none; the geometry owns the array.
    private protected virtual GroupSessionState GroupStateAtOrNull(int local) => null;

    /// <summary>Sizes and empties the per-worker key-change lists for this tick's projection.</summary>
    private protected void ResetKeyChanges(int workers)
    {
        if (!HasGroups)
        {
            return;
        }

        if (_keyChanges.Length < workers)
        {
            Array.Resize(ref _keyChanges, workers);
            Array.Resize(ref _keyChangeCount, workers * KeyCountStride);
        }

        for (var w = 0; w < _keyChanges.Length; w++)
        {
            _keyChanges[w] ??= new GroupKeyChange[64];
            _keyChangeCount[w * KeyCountStride] = 0;
        }
    }

    /// <summary>Records a member's key change. Called by the worker projecting the member, so the list is the worker's own.</summary>
    internal void NoteKeyChange(int worker, int archetype, uint netId, int set, long oldKey, long newKey)
    {
        if (_unplaced[archetype])
        {
            Hub.NoteUnplacedKeyChange(worker, netId, set, oldKey, newKey);
            return;
        }

        var list = _keyChanges[worker];
        var n = _keyChangeCount[worker * KeyCountStride];
        if (n == list.Length)
        {
            Array.Resize(ref _keyChanges[worker], n * 2);
            list = _keyChanges[worker];
        }

        list[n] = new GroupKeyChange { NetId = netId, Set = set, Old = oldKey, New = newKey };
        _keyChangeCount[worker * KeyCountStride] = n + 1;
    }

    /// <summary>An orphaned member leaves its groups: its keys, read from the cold entry before the entry is lost, go to 0. Under the orphan lock.</summary>
    private protected void NoteOrphanKeys(int archetype, byte* cold, in ReplicationBlockLayout layout, uint netId)
    {
        var keys = _plans[archetype].GroupKeys;
        for (var k = 0; k < keys.Length; k++)
        {
            var old = *(long*)(cold + layout.GroupKeyOffsetInColdEntry + (8 * keys[k].Slot));
            if (old == 0)
            {
                continue;
            }

            if (_orphanKeyChangeCount == _orphanKeyChanges.Length)
            {
                Array.Resize(ref _orphanKeyChanges, _orphanKeyChangeCount * 2);
            }

            _orphanKeyChanges[_orphanKeyChangeCount++] = new GroupKeyChange { NetId = netId, Set = keys[k].Set, Old = old, New = 0 };
        }
    }

    // The tick's key changes by (netId, set), merged at the index's finish from every list: what the member log reads an event's old key from.
    private protected readonly Dictionary<ulong, GroupKeyChange> _keyChangeMap = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static ulong KeyChangeId(uint netId, int set) => ((ulong)netId << 8) | (uint)set;   // the registry admits at most 256 sets

    private protected void MergeKeyChanges()
    {
        _keyChangeMap.Clear();
        for (var w = 0; w < _keyChanges.Length; w++)
        {
            var list = _keyChanges[w];
            for (var i = 0; i < _keyChangeCount[w * KeyCountStride]; i++)
            {
                AddKeyChange(in list[i]);
            }
        }

        // The fence's, recorded before this tick's projection began; consumed here, as its orphan events are by the merge.
        for (var i = 0; i < _orphanKeyChangeCount; i++)
        {
            AddKeyChange(in _orphanKeyChanges[i]);
        }

        _orphanKeyChangeCount = 0;

        KeyChangesRecorded += _keyChangeMap.Count;
    }

    private void AddKeyChange(in GroupKeyChange change)
    {
        // An identity that changed twice in one tick — a leave recorded by the projection and an orphan by the fence — keeps its first old key and its
        // last new one, as the log's fold does across ticks.
        var id = KeyChangeId(change.NetId, change.Set);
        if (_keyChangeMap.TryGetValue(id, out var existing))
        {
            existing.New = change.New;
            _keyChangeMap[id] = existing;
        }
        else
        {
            _keyChangeMap[id] = change;
        }
    }
}
