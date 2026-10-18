using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>One event of a positionless member (14 § 5): no position, so no cell — it is filed under its keys only.</summary>
internal struct UnplacedEvent
{
    public nint Block;
    public uint NetId;
    public ushort Archetype;
    public byte Slot;
    public byte Flags;
    public byte Groups;
}

/// <summary>One tick of the positionless members' events and their member log: what every realm's group gather reads, beside its own log.</summary>
internal sealed class UnplacedTick
{
    public uint Tick;
    public bool Valid;
    public UnplacedEvent[] Events = [];
    public int Count;

    /// <summary>The tick's member entries by (set, key), ascending; <see cref="MemberEntry.Event"/> indexes <see cref="Events"/>.</summary>
    public MemberEntry[] Members = [];
    public int MemberCount;

    /// <summary>
    /// The tick's visibility mask changes (14 § 6) by netId, ascending: an event with <see cref="PushEvent.TagsChanged"/> finds its masks here.
    /// </summary>
    public TagChange[] Tags = [];
    public int TagCount;
}

// Positionless members (14 § 5, P17 and P5′): the engine-wide half. A member with no position belongs to no realm and to no cell, so its events go to one
// log every realm's group gather reads; and its replication state exists only while some session holds a key it carries.
internal sealed unsafe partial class PushHub
{
    /// <summary>
    /// Blocks steps a lazy block may go unneeded before it is released: longer than the push log, so no session can still replay an event naming it, and
    /// long enough that a session walking along a region's edge does not re-identify its members on every pass.
    /// </summary>
    internal const uint LazyIdleTicks = 64;

    // How often the idle blocks are looked for, in ticks.
    private const uint LazySweepEvery = 8;

    private CompiledProjectionPlan[] _plans = [];

    /// <summary>The runtime's compiled group sets.</summary>
    internal CompiledGroupSet[] GroupSets { get; private set; } = [];

    // Per plan index: a positionless group member, whose blocks exist only while a held key needs them.
    private bool[] _lazy = [];
    private bool _anyLazy;
    private uint _tick;

    /// <summary>Binds the group sets and marks every positionless member as lazy. Serial, at <c>Start</c>.</summary>
    internal void ConfigureGroups(CompiledProjectionPlan[] plans, CompiledGroupSet[] sets)
    {
        _plans = plans;
        GroupSets = sets;
        _lazy = new bool[plans.Length];
        foreach (var set in sets)
        {
            foreach (var member in set.Members)
            {
                if (plans[member.Plan].Position == null)
                {
                    _lazy[member.Plan] = true;
                    _anyLazy = true;
                }
            }
        }

        _wanted = new long[sets.Length][];
        _wantedCount = new int[sets.Length];
        _wantedBefore = new long[sets.Length][];
        _wantedBeforeCount = new int[sets.Length];
        for (var s = 0; s < sets.Length; s++)
        {
            _wanted[s] = new long[16];
            _wantedBefore[s] = new long[16];
        }

        _lazySeen = new Dictionary<int, uint>[plans.Length];
        for (var a = 0; a < plans.Length; a++)
        {
            if (_lazy[a])
            {
                _lazySeen[a] = [];
            }
        }

        for (var t = 0; t < _unplacedLog.Length; t++)
        {
            _unplacedLog[t] = new UnplacedTick();
        }
    }

    /// <summary>Whether plan <paramref name="archetype"/> is a positionless member: no cell, no realm, state only while a held key needs it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsLazy(int archetype) => (uint)archetype < (uint)_lazy.Length && _lazy[archetype];

    /// <summary>Whether any group member has no position: the one test the positionless paths start with.</summary>
    internal bool IsAnyLazy => _anyLazy;

    // ══ The keys some session holds ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    // Per set: the keys held, sorted and unique — every served realm's sessions, committed and pending — this blocks step and the last one.
    private long[][] _wanted = [];
    private int[] _wantedCount = [];
    private long[][] _wantedBefore = [];
    private int[] _wantedBeforeCount = [];

    /// <summary>Called back by each realm's <see cref="PushReplication.CollectHeldKeys"/>: a key one of its sessions holds or is moving to.</summary>
    internal void AddWanted(int set, long key)
    {
        ref var keys = ref _wanted[set];
        var n = _wantedCount[set];
        if (n == keys.Length)
        {
            Array.Resize(ref keys, n * 2);
        }

        keys[n] = key;
        _wantedCount[set] = n + 1;
    }

    private void CollectWanted()
    {
        (_wanted, _wantedBefore) = (_wantedBefore, _wanted);
        (_wantedCount, _wantedBeforeCount) = (_wantedBeforeCount, _wantedCount);
        Array.Clear(_wantedCount);
        for (var r = 0; r < _activeCount; r++)
        {
            _active[r].CollectHeldKeys(this);
        }

        for (var s = 0; s < _wanted.Length; s++)
        {
            var n = _wantedCount[s];
            if (n < 2)
            {
                continue;
            }

            var keys = _wanted[s].AsSpan(0, n);
            keys.Sort();
            var kept = 1;
            for (var i = 1; i < n; i++)
            {
                if (keys[i] != keys[kept - 1])
                {
                    keys[kept++] = keys[i];
                }
            }

            _wantedCount[s] = kept;
        }
    }

    /// <summary>Whether some session holds, or is moving to, key <paramref name="key"/> of set <paramref name="set"/>.</summary>
    internal bool IsWanted(int set, long key) => key != 0 && _wanted[set].AsSpan(0, _wantedCount[set]).BinarySearch(key) >= 0;

    private bool WasWanted(int set, long key) => _wantedBefore[set].AsSpan(0, _wantedBeforeCount[set]).BinarySearch(key) >= 0;

    /// <summary>The keys of <paramref name="set"/> some session holds now, for tests.</summary>
    internal ReadOnlySpan<long> WantedForTest(int set) => _wanted[set].AsSpan(0, _wantedCount[set]);

    // ══ Lazy blocks (P5′) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    // Per lazy plan: each attached block's chunk and the last blocks step a held key needed it. A map of the attached blocks only — never an array over the
    // chunk-id space (SUB-13): a lazy archetype's attached blocks are few by design, and the sweep visits exactly them.
    private Dictionary<int, uint>[] _lazySeen = [];
    private readonly List<int> _lazyIdle = [];
    private readonly HashSet<int> _lazyChunks = [];

    /// <summary>Lazy blocks attached because a held key needed them, and released after <see cref="LazyIdleTicks"/> unneeded — cumulative.</summary>
    public long LazyAttached;

    /// <inheritdoc cref="LazyAttached"/>
    public long LazyReleased;

    /// <summary>Pushes of a lazy cluster with no block that no held key needed: nothing was attached and nothing is lost, the index still lists them.</summary>
    public long LazyPushesDropped;

    /// <summary>
    /// Before the push set's blocks are attached: the clusters of every key some session began to hold since the last blocks step, from the index — a
    /// key filled for the first time has members whose clusters may have no block. Serial.
    /// </summary>
    private void PushNewlyWanted(int a, ulong slotMask)
    {
        var state = _states[a];
        _lazyChunks.Clear();
        foreach (var set in GroupSets)
        {
            var member = set.MemberOf(a);
            if (member == null)
            {
                continue;
            }

            var tree = member.Tree ??= GroupSetCompiler.TreeOf(member, state);
            if (tree == null)
            {
                continue;
            }

            var keys = _wanted[set.Index];
            for (var i = 0; i < _wantedCount[set.Index]; i++)
            {
                var key = keys[i];
                if (WasWanted(set.Index, key))
                {
                    continue;
                }

                using var epoch = EpochGuard.Enter(tree.Segment.Store.EpochManager);
                var accessor = tree.Segment.CreateChunkAccessor();
                try
                {
                    using var buffer = tree.TryGetMultiple(&key, ref accessor);
                    if (!buffer.IsValid)
                    {
                        continue;
                    }

                    do
                    {
                        foreach (var location in buffer.ReadOnlyElements)
                        {
                            var chunk = location >> 6;
                            var table = state.BlockByChunk;
                            if (((uint)chunk >= (uint)table.Length || table[chunk] == 0) && _lazyChunks.Add(chunk))
                            {
                                AddPush(a, chunk, slotMask, anyPass: true);
                            }
                        }
                    }
                    while (buffer.NextChunk());
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Whether a lazy cluster with no block is needed: some pushed, occupied slot carries a key some session holds. Read from the cluster, which is the
    /// entity's current value — the projection about to read it reads the same. Serial, at the blocks step.
    /// </summary>
    private bool LazyNeeded(int a, int chunk, ulong mask, ref ChunkAccessor<PersistentStore> accessor)
    {
        var keys = _plans[a].GroupKeys;
        var clusterBase = accessor.GetChunkAddress(chunk);
        var slots = mask & *(ulong*)clusterBase;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;
            for (var g = 0; g < keys.Length; g++)
            {
                if (IsWanted(keys[g].Set, keys[g].Field.ReadKey(clusterBase, slot)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The blocks of a positionless member's push set: a cluster with one keeps it; one without gets one only when some pushed slot carries a held key, and
    /// is then pushed whole, so every entity of an attached block is described. A push no held key needs is dropped — the index still names the entity,
    /// and a key that comes to be held pushes it again. Returns the clusters a held key needed that the pool could not give a block to (SUB-32).
    /// </summary>
    private int AttachLazy(int a, ArchetypeReplicationState state, ulong slotMask)
    {
        var denied = 0;
        var cs = state.ClusterState;
        var segment = cs.ClusterSegment;
        if (segment == null)
        {
            // No pushes reach here without a segment; the blocks are still written, so the mark that follows never reads last tick's.
            Array.Clear(_pushBlocks[a], 0, _pushCount[a]);
            return 0;
        }

        using var epoch = EpochGuard.Enter(segment.Store.EpochManager);
        var accessor = segment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < _pushCount[a]; i++)
            {
                var chunk = _pushChunks[a][i];
                var table = state.BlockByChunk;
                var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
                if (block == null)
                {
                    if (!LazyNeeded(a, chunk, _pushMasks[a][i], ref accessor))
                    {
                        LazyPushesDropped++;
                    }
                    else if (state.TryAttachBlock(chunk, out block))
                    {
                        _pushMasks[a][i] = slotMask;
                        NoteLazyAttached(a, chunk, ref accessor);
                    }
                    else
                    {
                        block = null;
                        if (!DropDeniedPushesForTest)
                        {
                            RepushFromBlocksStep(a, chunk, _pushMasks[a][i]);
                        }

                        denied++;
                    }
                }

                _pushBlocks[a][i] = (nint)block;
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return denied;
    }

    /// <summary>A lazy block just attached: needed now, and the referrers waiting for its entities are pushed again so their references resolve.</summary>
    private void NoteLazyAttached(int a, int chunk, ref ChunkAccessor<PersistentStore> accessor)
    {
        _lazySeen[a][chunk] = _tick;
        LazyAttached++;
        if (_waitingReferrers.Count == 0)
        {
            return;
        }

        var clusterBase = accessor.GetChunkAddress(chunk);
        var occupancy = *(ulong*)clusterBase;
        var entityIds = (long*)(clusterBase + _states[a].ClusterState.Layout.EntityIdsOffset);
        while (occupancy != 0)
        {
            var slot = BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;
            WakeReferrers((ulong)entityIds[slot]);
        }
    }

    /// <summary>
    /// Every <see cref="LazySweepEvery"/> ticks: a lazy block none of whose entities carries a held key is stamped idle, and released once idle for
    /// <see cref="LazyIdleTicks"/> — its entities' identities go back through the quarantine and their referrers re-resolve (SUB-31). Serial.
    /// </summary>
    private void SweepLazy(int a)
    {
        if (_tick % LazySweepEvery != 0)
        {
            return;
        }

        var state = _states[a];
        var layout = state.Layout;
        var keys = _plans[a].GroupKeys;
        var table = state.BlockByChunk;
        var seen = _lazySeen[a];
        _lazyIdle.Clear();
        foreach (var (chunk, last) in seen)
        {
            // A cluster the fence drained took its block with it: its entry goes too.
            var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
            if (block == null || Needed(block, layout, keys) || _tick - last >= LazyIdleTicks)
            {
                _lazyIdle.Add(chunk);
            }
        }

        foreach (var chunk in _lazyIdle)
        {
            var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
            if (block == null)
            {
                seen.Remove(chunk);
            }
            else if (Needed(block, layout, keys))
            {
                seen[chunk] = _tick;
            }
            else
            {
                seen.Remove(chunk);
                if (state.ReleaseBlockForDrain(chunk))
                {
                    LazyReleased++;
                }
            }
        }
    }

    // Whether some described entity of a lazy block carries a held key, as its last projection read it.
    private bool Needed(ReplicationBlockHeader* block, in ReplicationBlockLayout layout, CompiledGroupKey[] keys)
    {
        var bytes = (byte*)block;
        var occupancy = block->ProjectedOccupancy;
        while (occupancy != 0)
        {
            var slot = BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;
            var cold = bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.GroupKeyOffsetInColdEntry;
            for (var g = 0; g < keys.Length; g++)
            {
                if (IsWanted(keys[g].Set, *(long*)(cold + (8 * keys[g].Slot))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ══ The off-DAG key moves (14 § 5) ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private readonly HashSet<(int Location, int Watch, ulong Raw)> _seenMoves = [];

    /// <summary>
    /// Prunes each positionless archetype's log of key moves (GroupKeyMoveLog): a move stays while the entity's projected key is still the one it left —
    /// the gather needs it — and goes once the projection has read the move, the entity has no state, or it repeats another. Serial, at the blocks step.
    /// </summary>
    private void PruneKeyMoves()
    {
        foreach (var set in GroupSets)
        {
            foreach (var member in set.Members)
            {
                if (member.Moves == null)
                {
                    continue;
                }

                _seenMoves.Clear();
                var filter = new MoveFilter(_states[member.Plan], member, _seenMoves);
                member.Moves.Retain(ref filter);
            }
        }
    }

    // One member's watch: the other watches of the log pass through untouched — their own member prunes them.
    private readonly struct MoveFilter : IGroupKeyMoveFilter
    {
        private readonly ArchetypeReplicationState _state;
        private readonly CompiledGroupMember _member;
        private readonly HashSet<(int, int, ulong)> _seen;

        public MoveFilter(ArchetypeReplicationState state, CompiledGroupMember member, HashSet<(int, int, ulong)> seen)
        {
            _state = state;
            _member = member;
            _seen = seen;
        }

        public bool Keep(in GroupKeyMove move)
        {
            if (move.Watch != _member.MoveWatch)
            {
                return true;
            }

            var table = _state.BlockByChunk;
            var chunk = move.Location >> 6;
            var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
            if (block == null)
            {
                return false;   // no state, so no projected key: nothing a fill could pass unsent
            }

            var layout = _state.Layout;
            var slot = move.Location & 63;
            var bytes = (byte*)block;
            var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
            if (hot->NetId == NetIdAllocator.NoNetId || (block->ProjectedOccupancy & (1UL << slot)) == 0)
            {
                return false;
            }

            var projected = *(long*)(bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.GroupKeyOffsetInColdEntry + (8 * _member.KeySlot));
            return projected == ClusterFieldReader.DecodeRaw(move.Raw, _member.Field.Type) && _seen.Add((move.Location, move.Watch, move.Raw));
        }
    }

    // ══ Referrers of an entity with no block yet (SUB-31) ══════════════════════════════════════════════════════════════════════════════════════════

    // A referrer whose reference named a lazy entity with no block resolved it to 0 and was not pushed again; it is, when the entity's block attaches. Keyed
    // by the target's raw EntityId, each list the referrers' raw EntityIds; fed from the projection workers' resolvers at the blocks step.
    private readonly Dictionary<ulong, List<ulong>> _waitingReferrers = [];
    private readonly HashSet<(ulong Target, ulong Referrer)> _waitingPairs = [];
    private readonly Stack<List<ulong>> _spareReferrerLists = new();

    /// <summary>Referrers pushed again because the lazy entity they name got a block — cumulative.</summary>
    public long ReferrersWoken;

    /// <summary>Referrers waiting for a lazy entity's block, now.</summary>
    internal int WaitingReferrers => _waitingPairs.Count;

    /// <summary>
    /// Every <see cref="LazyIdleTicks"/> ticks: the waits on an entity that no longer exists, and the waits of a referrer that no longer exists, are dropped.
    /// Either would otherwise be kept for as long as the target goes unheld — for ever, under churning referrers. Serial.
    /// </summary>
    private void PurgeWaitingReferrers()
    {
        if (_waitingReferrers.Count == 0 || _tick % LazyIdleTicks != 0 || References?.Engine == null)
        {
            return;
        }

        _purgeScratch.Clear();
        using var epoch = EpochGuard.Enter(References.Engine.EpochManager);
        var locator = new EntityLocator(References.Engine);
        try
        {
            foreach (var (target, list) in _waitingReferrers)
            {
                if (!locator.TryLocate(EntityId.FromRaw((long)target), out _, out _))
                {
                    _purgeScratch.Add(target);
                    continue;
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!locator.TryLocate(EntityId.FromRaw((long)list[i]), out _, out _))
                    {
                        _waitingPairs.Remove((target, list[i]));
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    _purgeScratch.Add(target);
                }
            }
        }
        finally
        {
            locator.Dispose();
        }

        foreach (var target in _purgeScratch)
        {
            if (_waitingReferrers.Remove(target, out var list))
            {
                foreach (var referrer in list)
                {
                    _waitingPairs.Remove((target, referrer));
                }

                list.Clear();
                _spareReferrerLists.Push(list);
            }
        }
    }

    private readonly List<ulong> _purgeScratch = [];

    /// <summary>Records that <paramref name="referrer"/> names <paramref name="target"/>, a lazy entity with no block. Serial (the blocks step).</summary>
    internal void WaitForBlock(ulong target, ulong referrer)
    {
        if (!_waitingPairs.Add((target, referrer)))
        {
            return;
        }

        if (!_waitingReferrers.TryGetValue(target, out var list))
        {
            list = _spareReferrerLists.Count > 0 ? _spareReferrerLists.Pop() : [];
            _waitingReferrers[target] = list;
        }

        list.Add(referrer);
    }

    private void WakeReferrers(ulong target)
    {
        if (!_waitingReferrers.Remove(target, out var list))
        {
            return;
        }

        var engine = References?.Engine;
        if (engine != null)
        {
            var locator = new EntityLocator(engine);
            try
            {
                foreach (var referrer in list)
                {
                    _waitingPairs.Remove((target, referrer));
                    var id = EntityId.FromRaw((long)referrer);
                    var state = References.StateOf(id.ArchetypeId);
                    if (state != null && locator.TryLocate(id, out var chunk, out var slot))
                    {
                        RepushFromBlocksStep(state.PushArchetypeIndex, chunk, 1UL << slot);
                        ReferrersWoken++;
                    }
                }
            }
            finally
            {
                locator.Dispose();
            }
        }
        else
        {
            foreach (var referrer in list)
            {
                _waitingPairs.Remove((target, referrer));
            }
        }

        list.Clear();
        _spareReferrerLists.Push(list);
    }

    /// <summary>The runtime's reverse reference index, whose engine locates a woken referrer; null when no plan projects a reference.</summary>
    internal ReferenceIndex References;

    // ══ The positionless members' event log ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    private readonly UnplacedTick[] _unplacedLog = new UnplacedTick[PushReplication.LogDepth];

    // Per worker, this tick's events and key changes; worker w's counts at w × CountStride, a cache line each.
    private UnplacedEvent[][] _unplacedEvents = [];
    private GroupKeyChange[][] _unplacedKeys = [];
    private int[] _unplacedCount = [];
    private int[] _unplacedKeyCount = [];
    private const int CountStride = 16;

    // The orphans — entries ended outside a projection (a released block, a drained cluster) — under a lock: the fence's slices and the blocks step reach it.
    private readonly Lock _unplacedOrphanLock = new();
    private UnplacedEvent[] _unplacedOrphans = new UnplacedEvent[16];
    private int _unplacedOrphanCount;
    private GroupKeyChange[] _unplacedOrphanKeys = new GroupKeyChange[16];
    private int _unplacedOrphanKeyCount;

    private readonly Dictionary<ulong, GroupKeyChange> _unplacedKeyMap = [];

    // Visibility tags (14 § 6): per worker, this tick's mask changes; the orphans' under the orphan lock.
    private TagChange[][] _unplacedTags = [];
    private int[] _unplacedTagCount = [];
    private TagChange[] _unplacedOrphanTags = new TagChange[16];
    private int _unplacedOrphanTagCount;
    // No tick yet: the first tick the track finishes is never mistaken for one already finished.
    private uint _unplacedFinished = uint.MaxValue;

    /// <summary>Positionless member events filed, cumulative.</summary>
    public long UnplacedEvents;

    /// <summary>The log slot of tick <paramref name="tick"/>, or null when it is not held: what a realm's group gather folds beside its own slot.</summary>
    internal UnplacedTick UnplacedAt(uint tick)
    {
        if (!_anyLazy)
        {
            return null;
        }

        var slot = _unplacedLog[tick % PushReplication.LogDepth];
        return slot.Valid && slot.Tick == tick ? slot : null;
    }

    // Sized and emptied for this tick's projection, from MarkPushed.
    private void BeginUnplaced(int workers)
    {
        if (!_anyLazy)
        {
            return;
        }

        if (_unplacedEvents.Length < workers)
        {
            Array.Resize(ref _unplacedEvents, workers);
            Array.Resize(ref _unplacedKeys, workers);
            Array.Resize(ref _unplacedCount, workers * CountStride);
            Array.Resize(ref _unplacedKeyCount, workers * CountStride);
            Array.Resize(ref _unplacedTags, workers);
            Array.Resize(ref _unplacedTagCount, workers * CountStride);
        }

        for (var w = 0; w < _unplacedEvents.Length; w++)
        {
            _unplacedEvents[w] ??= new UnplacedEvent[64];
            _unplacedKeys[w] ??= new GroupKeyChange[16];
            _unplacedTags[w] ??= new TagChange[16];
            _unplacedCount[w * CountStride] = 0;
            _unplacedKeyCount[w * CountStride] = 0;
            _unplacedTagCount[w * CountStride] = 0;
        }
    }

    /// <summary>A positionless member's event, from the worker projecting it: filed under its keys at the index's finish, never under a cell.</summary>
    internal void AddUnplaced(int worker, int archetype, ReplicationBlockHeader* block, int slot, uint netId, byte flags, int groups)
    {
        if (block != null)
        {
            // The push step owns the entity from this tick on: the fill and the release skip it by this stamp, as they do a positioned member's (SUB-19).
            var layout = _states[archetype].Layout;
            *(uint*)((byte*)block + layout.ColdOffset + (slot * layout.ColdStride) + layout.LastEventTickOffsetInColdEntry) = _tick;
        }

        ref var list = ref _unplacedEvents[worker];
        var n = _unplacedCount[worker * CountStride];
        if (n == list.Length)
        {
            Array.Resize(ref list, n * 2);
        }

        list[n] = new UnplacedEvent
        {
            Block = (nint)block, NetId = netId, Archetype = (ushort)archetype, Slot = (byte)slot, Flags = flags, Groups = (byte)groups,
        };
        _unplacedCount[worker * CountStride] = n + 1;
    }

    /// <summary>A positionless member's key change, from the worker projecting it.</summary>
    internal void NoteUnplacedKeyChange(int worker, uint netId, int set, long oldKey, long newKey)
    {
        ref var list = ref _unplacedKeys[worker];
        var n = _unplacedKeyCount[worker * CountStride];
        if (n == list.Length)
        {
            Array.Resize(ref list, n * 2);
        }

        list[n] = new GroupKeyChange { NetId = netId, Set = set, Old = oldKey, New = newKey };
        _unplacedKeyCount[worker * CountStride] = n + 1;
    }

    /// <summary>A positionless member's visibility mask change (14 § 6), from the worker projecting it.</summary>
    internal void NoteUnplacedTagChange(int worker, uint netId, uint oldMask, uint newMask)
    {
        ref var list = ref _unplacedTags[worker];
        var n = _unplacedTagCount[worker * CountStride];
        if (n == list.Length)
        {
            Array.Resize(ref list, n * 2);
        }

        list[n] = new TagChange { NetId = netId, Old = oldMask, New = newMask };
        _unplacedTagCount[worker * CountStride] = n + 1;
    }

    /// <summary>A positionless member's entry ended with no projection to see it: every session holding it through a key is told it left.</summary>
    internal void OrphanUnplaced(int archetype, ReplicationBlockHeader* block, byte* cold, in ReplicationBlockLayout layout, uint netId)
    {
        var keys = _plans[archetype].GroupKeys;
        lock (_unplacedOrphanLock)
        {
            var keyChanged = false;
            for (var k = 0; k < keys.Length; k++)
            {
                var old = *(long*)(cold + layout.GroupKeyOffsetInColdEntry + (8 * keys[k].Slot));
                if (old == 0)
                {
                    continue;
                }

                if (_unplacedOrphanKeyCount == _unplacedOrphanKeys.Length)
                {
                    Array.Resize(ref _unplacedOrphanKeys, _unplacedOrphanKeyCount * 2);
                }

                _unplacedOrphanKeys[_unplacedOrphanKeyCount++] = new GroupKeyChange { NetId = netId, Set = keys[k].Set, Old = old, New = 0 };
                keyChanged = true;
            }

            if (_unplacedOrphanCount == _unplacedOrphans.Length)
            {
                Array.Resize(ref _unplacedOrphans, _unplacedOrphanCount * 2);
            }

            // A hidden member leaves with its mask (14 § 6), so a session that never saw it is told nothing.
            var tagged = false;
            if (_plans[archetype].HasTagMask)
            {
                var mask = *(uint*)(cold + layout.TagMaskOffsetInColdEntry);
                if (mask != 0)
                {
                    if (_unplacedOrphanTagCount == _unplacedOrphanTags.Length)
                    {
                        Array.Resize(ref _unplacedOrphanTags, _unplacedOrphanTagCount * 2);
                    }

                    _unplacedOrphanTags[_unplacedOrphanTagCount++] = new TagChange { NetId = netId, Old = mask, New = 0 };
                    tagged = true;
                }
            }

            var flags = keyChanged ? (byte)(PushEvent.HasOld | PushEvent.KeyChanged) : PushEvent.HasOld;
            _unplacedOrphans[_unplacedOrphanCount++] = new UnplacedEvent
            {
                Block = (nint)block, NetId = netId, Archetype = (ushort)archetype, Flags = tagged ? (byte)(flags | PushEvent.TagsChanged) : flags,
            };
        }
    }

    /// <summary>
    /// The tick's positionless events into the log slot, and their member log: under each event's new key, and its old one when it changed. Serial, once
    /// per tick, at the index's finish — before any frame is gathered.
    /// </summary>
    internal void FinishUnplaced()
    {
        if (!_anyLazy || _unplacedFinished == _tick)
        {
            return;
        }

        _unplacedFinished = _tick;
        try
        {
            FinishUnplacedCore();
        }
        finally
        {
            // Between this tick's projection and its gather: where an off-DAG commit can move a key the projection already read (14 § 5).
            AfterIndexForTest?.Invoke(_tick);
        }
    }

    /// <summary>A test's seam between the projection and the gather, once per tick: what it does there, the gather must survive.</summary>
    internal Action<uint> AfterIndexForTest;

    private void FinishUnplacedCore()
    {
        var slot = _unplacedLog[_tick % PushReplication.LogDepth];
        var count = 0;
        for (var w = 0; w < _unplacedEvents.Length; w++)
        {
            count += _unplacedCount[w * CountStride];
        }

        lock (_unplacedOrphanLock)
        {
            count += _unplacedOrphanCount;
            if (slot.Events.Length < count)
            {
                slot.Events = new UnplacedEvent[Math.Max(64, (int)BitOperations.RoundUpToPowerOf2((uint)count))];
            }

            var at = 0;
            for (var w = 0; w < _unplacedEvents.Length; w++)
            {
                var n = _unplacedCount[w * CountStride];
                Array.Copy(_unplacedEvents[w], 0, slot.Events, at, n);
                at += n;
            }

            Array.Copy(_unplacedOrphans, 0, slot.Events, at, _unplacedOrphanCount);
            _unplacedOrphanCount = 0;

            _unplacedKeyMap.Clear();
            for (var w = 0; w < _unplacedKeys.Length; w++)
            {
                for (var i = 0; i < _unplacedKeyCount[w * CountStride]; i++)
                {
                    AddUnplacedKeyChange(in _unplacedKeys[w][i]);
                }
            }

            for (var i = 0; i < _unplacedOrphanKeyCount; i++)
            {
                AddUnplacedKeyChange(in _unplacedOrphanKeys[i]);
            }

            _unplacedOrphanKeyCount = 0;

            // The mask changes: the orphans' first (recorded before this tick's projection), then the workers', folded per identity.
            var tags = _unplacedOrphanTagCount;
            for (var w = 0; w < _unplacedTags.Length; w++)
            {
                tags += _unplacedTagCount[w * CountStride];
            }

            if (slot.Tags.Length < tags)
            {
                slot.Tags = new TagChange[Math.Max(16, (int)BitOperations.RoundUpToPowerOf2((uint)tags))];
            }

            Array.Copy(_unplacedOrphanTags, 0, slot.Tags, 0, _unplacedOrphanTagCount);
            var t = _unplacedOrphanTagCount;
            _unplacedOrphanTagCount = 0;
            for (var w = 0; w < _unplacedTags.Length; w++)
            {
                var n = _unplacedTagCount[w * CountStride];
                Array.Copy(_unplacedTags[w], 0, slot.Tags, t, n);
                t += n;
            }

            slot.TagCount = PushReplication.FoldTagChanges(slot.Tags, t);
        }

        slot.Count = count;
        slot.MemberCount = 0;
        for (var i = 0; i < count; i++)
        {
            ref var e = ref slot.Events[i];
            var keys = _plans[e.Archetype].GroupKeys;
            for (var g = 0; g < keys.Length; g++)
            {
                long ko, kn;
                if ((e.Flags & PushEvent.KeyChanged) != 0 && _unplacedKeyMap.TryGetValue(KeyChangeId(e.NetId, keys[g].Set), out var change))
                {
                    ko = change.Old;
                    kn = change.New;
                }
                else
                {
                    ko = kn = LiveKey(e.Archetype, e.Block, e.Slot, e.NetId, e.Flags, keys[g].Slot);
                }

                if (kn != 0)
                {
                    AddUnplacedMember(slot, keys[g].Set, kn, i, ko, kn);
                }

                if (ko != 0 && ko != kn)
                {
                    AddUnplacedMember(slot, keys[g].Set, ko, i, ko, kn);
                }
            }
        }

        if (slot.MemberCount > 1)
        {
            slot.Members.AsSpan(0, slot.MemberCount).Sort(default(MemberEntryOrder));
        }

        slot.Tick = _tick;
        slot.Valid = true;
        UnplacedEvents += count;
    }

    /// <summary>
    /// A positionless member's key in <paramref name="set"/> before this tick's change, when it had one: <see cref="PushReplication.TryTickOldKey"/>'s
    /// twin.
    /// </summary>
    internal bool TryUnplacedOldKey(uint netId, int set, out long oldKey)
    {
        oldKey = 0;
        if (!_anyLazy || _unplacedFinished != _tick || !_unplacedKeyMap.TryGetValue(KeyChangeId(netId, set), out var change))
        {
            return false;
        }

        oldKey = change.Old;
        return true;
    }

    private void AddUnplacedKeyChange(in GroupKeyChange change)
    {
        // Changed twice in one tick (a leave and an orphan): the first old key and the last new one, as the log's fold does across ticks.
        var id = KeyChangeId(change.NetId, change.Set);
        if (_unplacedKeyMap.TryGetValue(id, out var existing))
        {
            existing.New = change.New;
            _unplacedKeyMap[id] = existing;
            return;
        }

        _unplacedKeyMap[id] = change;
    }

    private static void AddUnplacedMember(UnplacedTick slot, int set, long key, int eventIndex, long ko, long kn)
    {
        if (slot.MemberCount == slot.Members.Length)
        {
            Array.Resize(ref slot.Members, Math.Max(64, slot.MemberCount * 2));
        }

        slot.Members[slot.MemberCount++] = new MemberEntry { Set = set, Key = key, Event = eventIndex, Old = ko, New = kn };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong KeyChangeId(uint netId, int set) => ((ulong)netId << 8) | (uint)set;

    // A described member's key in one set: its cold entry's, when the entry still names the identity; 0 for a leave or a reused slot.
    private long LiveKey(int archetype, nint block, int slot, uint netId, byte flags, int keySlot)
    {
        if ((flags & PushEvent.HasNew) == 0 || block == 0)
        {
            return 0;
        }

        var layout = _states[archetype].Layout;
        var bytes = (byte*)block;
        var hot = (ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride));
        return hot->NetId != netId ? 0 : *(long*)(bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.GroupKeyOffsetInColdEntry + (8 * keySlot));
    }
}
