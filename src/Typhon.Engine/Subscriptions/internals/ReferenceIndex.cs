using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where a live entity is: its cluster chunk and slot, through its archetype's EntityMap. One accessor, kept for as long as consecutive entities share an
/// archetype.
/// </summary>
/// <remarks>
/// Safe in the replication track for the reason <see cref="BoundViewpoint"/> is: the EntityMap's lookup is lock-free, and nothing writes it between the
/// fence and the published frames (EW-01). A caller holds an epoch (PS-02). Unlike <see cref="BoundViewpoint"/> it needs no spatial index: a reference may
/// name any archetype.
/// </remarks>
internal unsafe struct EntityLocator : IDisposable
{
    private readonly DatabaseEngine _engine;
    private ArchetypeEngineState _state;
    private int _routing;
    private ChunkAccessor<PersistentStore> _map;

    /// <summary>A locator over <paramref name="engine"/>; no accessor is made until the first lookup.</summary>
    public EntityLocator(DatabaseEngine engine)
    {
        _engine = engine;
        _routing = -1;
    }

    /// <summary>The chunk and slot of <paramref name="entity"/>; <see langword="false"/> when it is null, gone, or of an archetype this engine lacks.</summary>
    public bool TryLocate(EntityId entity, out int chunk, out int slot)
    {
        chunk = 0;
        slot = 0;
        var states = _engine?._stateByRouting;
        var routing = entity.ArchetypeId;
        if (entity.IsNull || states == null || routing >= states.Length)
        {
            return false;
        }

        if (routing != _routing && !Open(states[routing], routing))
        {
            return false;
        }

        var record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];

        // A destroyed entity keeps its record, tombstoned, until the cleanup passes MinTSN; its slot may already hold another entity. Dead is gone.
        if (!_state.EntityMap.TryGet(entity.EntityKey, record, ref _map) || !ClusterEntityRecordAccessor.GetHeader(record).IsAlive)
        {
            return false;
        }

        chunk = ClusterEntityRecordAccessor.GetClusterChunkId(record);
        slot = ClusterEntityRecordAccessor.GetSlotIndex(record);
        return true;
    }

    /// <summary>Releases the accessor.</summary>
    public void Dispose() => Close();

    private bool Open(ArchetypeEngineState state, int routing)
    {
        Close();
        if (state?.EntityMap == null || state.ClusterState == null)
        {
            return false;
        }

        _state = state;
        _routing = routing;
        _map = state.EntityMap.Segment.CreateChunkAccessor();
        return true;
    }

    private void Close()
    {
        if (_routing < 0)
        {
            return;
        }

        _map.Dispose();
        _map = default;
        _state = null;
        _routing = -1;
    }
}

/// <summary>
/// One projection worker's reference reader (13 § 5): the netId an <see cref="EntityId"/> names, read from its target's replication entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic, though targets are initialized concurrently.</b> A target whose identity is taken during this projection — by another worker, or by
/// this one earlier in the tick — resolves to 0 and its referrer is pushed again next tick, whichever worker got there first: an entry that does not name
/// the target yet, one with no identity, and one whose identity is still out on a lease (<see cref="NetIdAllocator.IsLeased"/>) all read alike. A target
/// identified on an earlier tick has a stable entry for the whole projection. So two runs of a <c>DeterministicProjection</c> produce the same bytes.
/// </para>
/// <para>
/// <b>Memory ordering.</b> An initialization zeroes the entry, then publishes its <see cref="EntityId"/> with a release, then writes the netId; this reads
/// the <see cref="EntityId"/> with an acquire and only then the netId. A matching entity therefore never comes with the netId of the entry's previous
/// holder — on arm64 too. The netId it may see is 0 or the new one, and the new one is still on a lease: both read as "not yet".
/// </para>
/// </remarks>
internal sealed unsafe class ReferenceResolver
{
    private readonly ReferenceIndex _index;

    // One locator per replicated target archetype, by routing id: references into two archetypes, interleaved slot by slot, would otherwise close and
    // reopen an accessor per slot. Each makes its accessor on its first lookup.
    private readonly EntityLocator[] _locators;

    internal ReferenceResolver(ReferenceIndex index)
    {
        _index = index;
        _locators = new EntityLocator[index.RoutingCount];
    }

    /// <summary>Opens the worker's locators for its share of one archetype's blocks.</summary>
    public void Open()
    {
        for (var r = 0; r < _locators.Length; r++)
        {
            _locators[r] = new EntityLocator(_index.Engine);
        }
    }

    /// <summary>Releases them: an accessor pins pages, and a projection's share ends here.</summary>
    public void Close()
    {
        for (var r = 0; r < _locators.Length; r++)
        {
            _locators[r].Dispose();
        }
    }

    /// <summary>The netId <paramref name="target"/> is known by, or 0.</summary>
    /// <param name="target">The entity a reference field names.</param>
    /// <param name="pending">
    /// Set when the target is replicated, alive and located, but has no identity a client could hold yet: the referrer is pushed again next tick.
    /// </param>
    /// <returns>The netId; 0 for a null, gone or unreplicated target, or a pending one.</returns>
    public uint Resolve(EntityId target, out bool pending)
    {
        pending = false;
        if (target.IsNull)
        {
            return NetIdAllocator.NoNetId;
        }

        // An archetype no profile replicates holds no identity, ever: 0, and nothing to wait for.
        var state = _index.StateOf(target.ArchetypeId);
        if (state == null || !_locators[target.ArchetypeId].TryLocate(target, out var chunk, out var slot))
        {
            return NetIdAllocator.NoNetId;
        }

        // Every pushed cluster has a block by now (the blocks step attached them). One without was never pushed — a realm nobody serves, or a pool that ran
        // dry — and waiting would re-push the referrer every tick for nothing. The realm case costs no client anything: a session holds its own realm only
        // (SUB-28), so a target in a realm nobody serves is one no referrer's client could hold. The pool case is 06 § 8's open item.
        var table = state.BlockByChunk;
        var block = (uint)chunk < (uint)table.Length ? (byte*)table[chunk] : null;
        if (block == null || (uint)slot >= (uint)state.Layout.SlotCount)
        {
            return NetIdAllocator.NoNetId;
        }

        var layout = state.Layout;
        var hot = (ReplicationHotEntry*)(block + layout.HotOffset + (slot * layout.HotStride));
        if (Volatile.Read(ref Unsafe.AsRef<ulong>(hot)) != target.RawValue)
        {
            pending = true;
            return NetIdAllocator.NoNetId;
        }

        var netId = hot->NetId;
        if (netId == NetIdAllocator.NoNetId || _index.NetIds.IsLeased(netId))
        {
            pending = true;
            return NetIdAllocator.NoNetId;
        }

        return netId;
    }
}

/// <summary>
/// The reverse index of references, <c>netId → referrers</c> (13 § 5, SUB-31): when an identity is released, every entity whose reference field names it is
/// pushed again, and re-resolves to 0 — or to its target's new identity — before the quarantine lets the number be reissued.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fed by changes, not by archetypes</b> (SUB-13). Each referrer keeps, in its cold entry, the netId each of its reference fields last resolved to; the
/// projection logs <c>(netId, referrer, ±1)</c> only where a resolution differs from it, and every path that ends an entry logs its −1s. The logs are
/// per projection worker, plus one locked list for the fence's and the blocks step's entry ends, which are rare.
/// </para>
/// <para>
/// <b>Applied serially, then consumed.</b> <see cref="Step"/> runs at the blocks step before the push set is collected: it merges the last projection's logs,
/// then takes the referrers of every identity released since — the projection's (still queued in the leases) and the orphans — locates each through the
/// EntityMap and marks it for this tick's push. A referrer whose re-projection resolves differently logs its −1 for the released identity; the pair is
/// already gone, so the −1 is dropped.
/// </para>
/// <para>
/// <b>Exact counts.</b> A pair counts the referrer's fields naming the netId, so one field moving off it leaves the pair for the other. Nothing here can
/// grow without a live pair behind it: a pair exists only while some live entry holds the netId in a reference slot, or until the netId's release.
/// </para>
/// </remarks>
internal sealed unsafe class ReferenceIndex : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Delta
    {
        public ulong Referrer;
        public uint NetId;
        public int Sign;
    }

    // A worker's log. Padded to a cache line: two workers appending must not bounce one line between them.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Log
    {
        public Delta* Items;
        public int Count;
        public int Capacity;
    }

    private struct Node
    {
        public ulong Referrer;
        public uint NetId;
        public int Count;
        public int Prev;
        public int Next;
    }

    private readonly ArchetypeReplicationState[] _stateByRouting;
    private readonly int[] _planByRouting;
    private readonly Lock _sharedLock = new();
    private List<Delta> _shared = [];
    private List<Delta> _sharedSpare = [];
    private ReferenceResolver[] _resolvers = [];
    private Log* _logs;
    private int _logCount;

    // The pairs: a node per (netId, referrer), chained per netId from _head (index + 1, 0 for none), freed nodes reused through _freeNode.
    private readonly Dictionary<(uint NetId, ulong Referrer), int> _pairs = new();
    private Node[] _nodes = new Node[64];
    private int _nodeCount;
    private int _freeNode = -1;
    private int[] _head = new int[256];
    private readonly List<(ulong Referrer, uint NetId)> _referrers = [];

    // The identities taken at this step and the previous one: a collection's body still names a taken netId until its referrer is projected again, and
    // the −1 that projection logs is expected, not a dropped decrement.
    private HashSet<uint> _takenNow = [];
    private HashSet<uint> _takenBefore = [];
    private bool _disposed;

    /// <summary>The index of a runtime whose plans project references.</summary>
    /// <param name="engine">The engine whose EntityMaps locate targets and referrers.</param>
    /// <param name="netIds">The database's identity allocator, whose lease bits make a resolution deterministic.</param>
    /// <param name="plans">The compiled plans.</param>
    /// <param name="states">Their replication states, by plan index.</param>
    public ReferenceIndex(DatabaseEngine engine, NetIdAllocator netIds, CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(netIds);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(states);
        Engine = engine;
        NetIds = netIds;

        // A reference carries its target's routing id, not its plan index: both maps are by routing, sized to the largest one replicated.
        var size = 0;
        for (var a = 0; a < plans.Length; a++)
        {
            var routing = engine.RoutingIdForCatalog(plans[a].ArchetypeCatalogId);
            if (routing != DatabaseEngine.NoRoutingId)
            {
                size = Math.Max(size, routing + 1);
            }
        }

        _stateByRouting = new ArchetypeReplicationState[size];
        _planByRouting = new int[size];
        Array.Fill(_planByRouting, -1);
        for (var a = 0; a < plans.Length; a++)
        {
            var routing = engine.RoutingIdForCatalog(plans[a].ArchetypeCatalogId);
            if (routing != DatabaseEngine.NoRoutingId)
            {
                _stateByRouting[routing] = states[a];
                _planByRouting[routing] = a;
            }
        }
    }

    /// <summary>The engine.</summary>
    public DatabaseEngine Engine { get; }

    /// <summary>The database's identity allocator.</summary>
    public NetIdAllocator NetIds { get; }

    /// <summary>The (netId, referrer) pairs held: references live entries hold, one pair however many of an entity's fields name the same netId.</summary>
    public int PairCount => _pairs.Count;

    /// <summary>Referrers pushed again because an identity they named was released, cumulative.</summary>
    public long ReferrersRepushed { get; private set; }

    /// <summary>Deltas applied, cumulative.</summary>
    public long DeltasApplied { get; private set; }

    /// <summary>
    /// −1s for a pair that no longer exists, cumulative. A taken referrer's held netIds are zeroed when it is located, so this counts only a referrer that
    /// could not be — parked between a migration and its drain — and is otherwise a defect in the accounting: zero in a healthy run.
    /// </summary>
    public long DroppedDecrements { get; private set; }

    /// <summary>Routing ids the maps cover: one past the largest replicated archetype's.</summary>
    public int RoutingCount => _stateByRouting.Length;

    /// <summary>The replication state of the archetype a routing id names, or <see langword="null"/> when it is not replicated.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArchetypeReplicationState StateOf(ushort routing) => routing < (uint)_stateByRouting.Length ? _stateByRouting[routing] : null;

    /// <summary>Whether <paramref name="netId"/> is counted as named by <paramref name="referrer"/>, and how many times — for tests.</summary>
    public int CountOf(uint netId, EntityId referrer) => _pairs.TryGetValue((netId, referrer.RawValue), out var node) ? _nodes[node].Count : 0;

    /// <summary>Sizes the per-worker logs and resolvers for a projection of <paramref name="workers"/> chunks. Serial, before the dispatch.</summary>
    public void BeginTick(int workers)
    {
        // On the tick path, like DrainQuarantine: a runtime disposed under a tick still in flight is not a fence failure.
        if (_disposed)
        {
            return;
        }

        if (workers > _logCount)
        {
            // native-alloc: per-worker log table, sized from the worker count and freed with the index
            var grown = (Log*)NativeMemory.AllocZeroed((nuint)workers, (nuint)sizeof(Log));
            if (_logs != null)
            {
                Buffer.MemoryCopy(_logs, grown, (long)workers * sizeof(Log), (long)_logCount * sizeof(Log));
                NativeMemory.Free(_logs);
            }

            _logs = grown;
            _logCount = workers;
        }

        if (_resolvers.Length < workers)
        {
            var resolvers = new ReferenceResolver[workers];
            Array.Copy(_resolvers, resolvers, _resolvers.Length);
            for (var i = _resolvers.Length; i < workers; i++)
            {
                resolvers[i] = new ReferenceResolver(this);
            }

            _resolvers = resolvers;
        }
    }

    /// <summary><paramref name="worker"/>'s resolver; sized by <see cref="BeginTick"/>.</summary>
    public ReferenceResolver ResolverFor(int worker) => (uint)worker < (uint)_resolvers.Length ? _resolvers[worker] : null;

    /// <summary>Logs, from projection chunk <paramref name="worker"/>, that <paramref name="referrer"/> names <paramref name="netId"/> once more, or less.
    /// </summary>
    public void Note(int worker, uint netId, ulong referrer, int sign)
    {
        if ((uint)worker >= (uint)_logCount)
        {
            NoteShared(netId, referrer, sign);
            return;
        }

        ref var log = ref _logs[worker];
        if (log.Count == log.Capacity)
        {
            var capacity = log.Capacity == 0 ? 64 : log.Capacity * 2;
            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            log.Items = (Delta*)NativeMemory.Realloc(log.Items, (nuint)capacity * (nuint)sizeof(Delta));
            log.Capacity = capacity;
        }

        log.Items[log.Count++] = new Delta { Referrer = referrer, NetId = netId, Sign = sign };
    }

    /// <summary>As <see cref="Note"/>, from the fence or the blocks step: under a lock, since a fence's migration slices run in parallel.</summary>
    public void NoteShared(uint netId, ulong referrer, int sign)
    {
        lock (_sharedLock)
        {
            _shared.Add(new Delta { Referrer = referrer, NetId = netId, Sign = sign });
        }
    }

    /// <summary>
    /// The blocks step's half (13 § 5): merges the logs, then pushes again — this tick — every referrer of an identity released since the last step. Serial,
    /// before <see cref="PushHub.PrepareBlocks"/>.
    /// </summary>
    /// <param name="states">The replication states, whose leases and orphan lists hold the releases.</param>
    /// <param name="hub">The push hub the referrers are marked in; <see langword="null"/> merges only, and takes nothing.</param>
    /// <returns>How many referrers were marked.</returns>
    /// <remarks>
    /// A taken referrer's held netIds equal to the released one are zeroed, so its next projection compares against 0: a +1 for a target that holds the
    /// number next is never mistaken for "unchanged", and no −1 comes back for the pair just dropped. Its block's projected mask loses the slot too, so a
    /// dormant cluster is projected for it.
    /// </remarks>
    public int Step(ArchetypeReplicationState[] states, PushHub hub)
    {
        if (_disposed)
        {
            return 0;
        }

        Merge();
        if (hub == null)
        {
            return 0;
        }

        (_takenBefore, _takenNow) = (_takenNow, _takenBefore);
        _takenNow.Clear();

        _referrers.Clear();
        foreach (var state in states)
        {
            if (state == null)
            {
                continue;
            }

            var leases = state.NetIdLeases;
            for (var w = 0; w < leases.Count; w++)
            {
                foreach (var netId in leases.PendingReleasesOf(w))
                {
                    Take(netId);
                }
            }

            foreach (var netId in state.TakeOrphanedForReferences())
            {
                Take(netId);
            }
        }

        if (_referrers.Count == 0)
        {
            return 0;
        }

        var marked = 0;
        using var epoch = EpochGuard.Enter(Engine.EpochManager);
        var locator = new EntityLocator(Engine);
        try
        {
            foreach (var (raw, netId) in _referrers)
            {
                var referrer = EntityId.FromRaw((long)raw);
                var routing = referrer.ArchetypeId;
                var plan = routing < (uint)_planByRouting.Length ? _planByRouting[routing] : -1;

                // A referrer that died since is not pushed: its entry's end already took its −1s, or will.
                if (plan < 0 || !locator.TryLocate(referrer, out var chunk, out var slot))
                {
                    continue;
                }

                Forget(_stateByRouting[routing], chunk, slot, referrer, netId);
                hub.RepushFromBlocksStep(plan, chunk, 1UL << slot);
                marked++;
            }
        }
        finally
        {
            locator.Dispose();
        }

        ReferrersRepushed += marked;
        return marked;
    }

    // Zeroes the referrer's held netIds equal to the released one, when its entry is in its block, and clears the slot from the block's projected mask so a
    // dormant cluster's skip does not pass it by. A parked entry is not reachable here; its −1 is then dropped (DroppedDecrements).
    private static void Forget(ArchetypeReplicationState state, int chunk, int slot, EntityId referrer, uint netId)
    {
        var table = state.BlockByChunk;
        var block = (uint)chunk < (uint)table.Length ? (ReplicationBlockHeader*)table[chunk] : null;
        var layout = state.Layout;
        if (block == null || (uint)slot >= (uint)layout.SlotCount)
        {
            return;
        }

        var bytes = (byte*)block;
        if (((ReplicationHotEntry*)(bytes + layout.HotOffset + (slot * layout.HotStride)))->Entity != referrer)
        {
            return;
        }

        var held = bytes + layout.ColdOffset + (slot * layout.ColdStride) + layout.ReferenceOffsetInColdEntry;
        for (var at = 0; at < layout.ReferenceBytes; at += sizeof(uint))
        {
            if (Unsafe.ReadUnaligned<uint>(held + at) == netId)
            {
                Unsafe.WriteUnaligned(held + at, NetIdAllocator.NoNetId);
            }
        }

        block->ProjectedWatchedMask &= ~(1UL << slot);
    }

    // Applies every logged delta: the workers' (they finished with the last projection) and the shared list's (swapped out under its lock).
    private void Merge()
    {
        for (var w = 0; w < _logCount; w++)
        {
            ref var log = ref _logs[w];
            for (var i = 0; i < log.Count; i++)
            {
                Apply(log.Items[i]);
            }

            log.Count = 0;
        }

        List<Delta> shared;
        lock (_sharedLock)
        {
            shared = _shared;
            _shared = _sharedSpare;
        }

        foreach (var delta in shared)
        {
            Apply(delta);
        }

        shared.Clear();
        _sharedSpare = shared;
    }

    private void Apply(Delta delta)
    {
        DeltasApplied++;
        var key = (delta.NetId, delta.Referrer);
        if (_pairs.TryGetValue(key, out var node))
        {
            ref var n = ref _nodes[node];
            n.Count += delta.Sign;
            if (n.Count <= 0)
            {
                Unlink(node);
                _pairs.Remove(key);
            }

            return;
        }

        // A −1 for a pair already gone: a collection's re-projection after its netId was taken — expected — or a taken referrer that could not be
        // located in its block when its pair was dropped.
        if (delta.Sign <= 0)
        {
            if (!_takenNow.Contains(delta.NetId) && !_takenBefore.Contains(delta.NetId))
            {
                DroppedDecrements++;
            }

            return;
        }

        node = NewNode();
        _nodes[node] = new Node { Referrer = delta.Referrer, NetId = delta.NetId, Count = delta.Sign, Prev = -1, Next = HeadOf(delta.NetId) - 1 };
        if (_nodes[node].Next >= 0)
        {
            _nodes[_nodes[node].Next].Prev = node;
        }

        SetHead(delta.NetId, node + 1);
        _pairs.Add(key, node);
    }

    // Every pair of a released identity: its referrers are listed for a push, and the pairs dropped. The chain's order follows the parallel fence's lock
    // order; nothing depends on it, since a referrer's mark is a bitwise OR.
    private void Take(uint netId)
    {
        var at = HeadOf(netId) - 1;
        if (at < 0)
        {
            return;
        }

        _takenNow.Add(netId);

        while (at >= 0)
        {
            var next = _nodes[at].Next;
            _referrers.Add((_nodes[at].Referrer, netId));
            _pairs.Remove((netId, _nodes[at].Referrer));
            Free(at);
            at = next;
        }

        SetHead(netId, 0);
    }

    private void Unlink(int node)
    {
        ref var n = ref _nodes[node];
        if (n.Prev >= 0)
        {
            _nodes[n.Prev].Next = n.Next;
        }
        else
        {
            SetHead(n.NetId, n.Next + 1);
        }

        if (n.Next >= 0)
        {
            _nodes[n.Next].Prev = n.Prev;
        }

        Free(node);
    }

    private int NewNode()
    {
        if (_freeNode >= 0)
        {
            var reused = _freeNode;
            _freeNode = _nodes[reused].Next;
            return reused;
        }

        if (_nodeCount == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        return _nodeCount++;
    }

    private void Free(int node)
    {
        _nodes[node] = new Node { Next = _freeNode, Prev = -1 };
        _freeNode = node;
    }

    private int HeadOf(uint netId) => netId < (uint)_head.Length ? _head[netId] : 0;

    private void SetHead(uint netId, int value)
    {
        if (netId >= (uint)_head.Length)
        {
            if (value == 0)
            {
                return;
            }

            var length = _head.Length;
            while (length <= netId)
            {
                length *= 2;
            }

            Array.Resize(ref _head, length);
        }

        _head[netId] = value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var w = 0; w < _logCount; w++)
        {
            NativeMemory.Free(_logs[w].Items);
        }

        NativeMemory.Free(_logs);
        _logs = null;
        _logCount = 0;
    }
}
