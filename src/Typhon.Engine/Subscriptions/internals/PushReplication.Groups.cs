using System;
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

    /// <summary>The most keys a session holds per set (<see cref="SubscriptionsOptions.MaxGroupsPerSession"/>).</summary>
    internal int MaxGroupsPerSession;

    /// <summary>The explicit sets' per-session keys and answers (14 § 4), engine-wide; null when no set is explicit.</summary>
    internal GroupRequests Requests;

    /// <summary>
    /// Where <paramref name="session"/> stands with <paramref name="key"/> of <paramref name="set"/> after this frame's gather — the state its frame
    /// commits: what a <c>SubscribeRequest</c>'s answer waits for.
    /// </summary>
    internal virtual GroupKeyState GroupKeyStateOf(SessionId session, int set, long key) => GroupKeyState.NotHeld;

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

    /// <summary>Regions a session met past <see cref="MaxGroupsPerSession"/> and could not hold, summed over frames.</summary>
    internal long GroupsOverLimit;

    /// <summary>RESET frames gathered while the session held a key still filling: each refills it from its first member. Diagnostic.</summary>
    internal long GroupResetsMidFill;

    /// <summary>Whether any session's committed keys include one still filling; a test's quiet point waits for none.</summary>
    internal virtual bool AnyGroupFillingForTest() => false;

    /// <summary>
    /// Every key a session here holds or is moving to, to <paramref name="hub"/> (14 § 5): what decides which positionless members need replication state.
    /// Serial, at the blocks step.
    /// </summary>
    internal void CollectHeldKeys(PushHub hub)
    {
        if (!HasGroups)
        {
            return;
        }

        for (var local = 1; local < _slotHigh; local++)
        {
            var owner = _slotOwners[local];
            if (!owner.IsValid)
            {
                continue;
            }

            var gs = GroupStateAtOrNull(local);
            if (gs != null && gs.Generation == owner.Generation)
            {
                gs.CollectKeys(hub);
            }
        }
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
