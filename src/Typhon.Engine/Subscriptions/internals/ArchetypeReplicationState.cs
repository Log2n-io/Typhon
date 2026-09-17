using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Everything replication owns for one replicated archetype: the block pool, the directory that indexes it, and the network-identity allocator. Owning them
/// together is what makes their lifetimes and their pairing safe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists rather than three fields on a caller.</b> The directory stores raw pointers into the pool's slabs and holds no reference to the
/// pool, so it can neither keep those slabs alive nor notice when they go. Disposing the pool first leaves every entry dangling and a later lookup hands out
/// a pointer into freed native memory. That ordering cannot be enforced by either type alone — it is a property of the pair, so the pair needs an owner.
/// </para>
/// <para>
/// <b>The two paired operations.</b> Renting a block and registering it are one logical act, as are removing it and returning it; performed separately each
/// has a leak in the middle. <see cref="TryAttachBlock"/> rolls the rent back when registration fails, and <see cref="TryReleaseBlock"/> is the single
/// primitive the cluster-drain hook will call. Callers that reach past these to <see cref="Pool"/> and <see cref="Directory"/> take the leak back on
/// themselves; the sub-objects stay exposed because the projection passes read them directly on the hot path, where a wrapper would cost an indirection for
/// nothing.
/// </para>
/// <para>
/// <b>Thread safety: none, by contract</b> — the same contract the pool and directory document. Mutated only at the replication track's prologue and its
/// single-threaded blocks step.
/// </para>
/// </remarks>
internal sealed unsafe class ArchetypeReplicationState : ResourceNode, IMemoryResource
{
    private bool _disposed;
    private ArchetypeClusterState _attachedTo;
    private long _drainFaults;

    // ── The tick's projection state ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Three objects, one lifetime, and they hang here rather than off the runtime because every one of them is per ARCHETYPE: a record names a slot of an
    // ENTITIES block, which is an archetype's block; a watched block belongs to one archetype's directory; and a lease is spent initializing this
    // archetype's entries. They are created eagerly and reset per tick — none of them allocates once the watched set has stopped growing (SUB-07).
    private readonly WatchedBlockList _watchedBlocks = new();
    private readonly RecordArenaSet _records = new();
    private readonly NetIdLeaseSet _netIdLeases = new();

    private long _blocksProjected;
    private long _slotsProjected;
    private long _recordsProduced;
    private long _identitiesReleased;
    private long _netIdStarvations;
    private long _segmentsEmitted;
    private long _shadowSegments;

    /// <summary>Creates the per-archetype replication state. Nothing is committed until the first block is attached.</summary>
    /// <param name="id">Stable resource id prefix; the pool registers as <c>{id}.Pool</c>.</param>
    /// <param name="parent">Resource-graph parent for the pool.</param>
    /// <param name="allocator">Engine allocator, supplied by DI.</param>
    /// <param name="layout">The archetype's block layout.</param>
    /// <param name="options">Operator configuration carrying the pool's budget.</param>
    /// <param name="netIds">
    /// The database's identity allocator, SHARED with every other replicated archetype. Injected rather than constructed here because netIds are global: the
    /// wire encodes an event once and memcpy's it to every receiver on the strength of that, and an <c>entityRef</c> arrives with no archetype to disambiguate
    /// it. This state uses the allocator but does not own it, so it neither reports its bytes nor disposes it.
    /// </param>
    public ArchetypeReplicationState(string id, IResource parent, IMemoryAllocator allocator, ReplicationBlockLayout layout, SubscriptionsOptions options,
        NetIdAllocator netIds)
        : base(Require(id, nameof(id)), ResourceType.Node, Require(parent, nameof(parent)))
    {
        // id and parent are validated in the base-call arguments above: the base constructor registers this node with the graph, so validating them in the
        // body would run too late — a null parent would already have faulted inside ResourceNode, and a null id would leave a registered node behind.
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(netIds);

        Layout = layout;
        NetIds = netIds;

        // The pool hangs off THIS node, not off `parent`, so the graph reads Runtime -> {id} -> {id}.Pool -> slabs. That nesting is what lets this node
        // report the directory and identity bytes — which are nobody's child and were previously invisible — while the slabs stay accounted to the pool.
        //
        // The pool registers itself as it is constructed, so anything that throws after this point must dispose it or leave a registry node behind that
        // nothing owns and nothing will ever free.
        Pool = new ReplicationBlockPool($"{id}.Pool", this, allocator, layout, options);
        try
        {
            Directory = new ReplicationDirectory();
        }
        catch
        {
            Pool.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The directory's backing map, a plain managed array owned by this node rather than a resource in its own right. The pool's slabs are deliberately
    /// excluded: the pool is a CHILD of this node, and <see cref="IMemoryResource"/> requires a node to exclude its children or the graph double-counts them.
    /// <para>
    /// The identity allocator's side arrays were counted here until netIds went global, and stopped being countable here the moment the allocator became
    /// shared: every replicated archetype would have added the same bytes to its own total. It is a node in its own right now and reports them once.
    /// </para>
    /// </para>
    /// <para>
    /// This exists because <c>SubscriptionsOptions.StatePoolBudgetBytes</c> bounds the block pool alone. The bytes reported here follow the same untrusted
    /// input — connections, observer radii, client-supplied regions — and were previously in no snapshot, no health check and no budget. Reporting is not
    /// charging: they are visible here, but nothing yet refuses to grow them.
    /// </para>
    /// </remarks>
    public int EstimatedMemorySize
    {
        get
        {
            var bytes = Directory.EstimatedBytes + _watchedBlocks.EstimatedBytes + _records.EstimatedBytes + _netIdLeases.EstimatedBytes + 128L;
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }
    }

    /// <summary>Argument validation usable from a base-constructor argument, where a statement cannot run.</summary>
    private static T Require<T>(T value, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }

    /// <summary>The block layout this archetype's blocks are carved to.</summary>
    public ReplicationBlockLayout Layout { get; }

    /// <summary>
    /// The tick period the motion rule measures against, in seconds: the teleport threshold is a declared speed times this, the heartbeat is
    /// <c>MaxAge</c> divided by it, and a declared velocity is scaled by it.
    /// </summary>
    /// <remarks>
    /// <b>The current period, not the nominal one</b> ([02 § 4](../../../claude/design/Subscriptions/02-execution.md)): under overload a tick covers more
    /// time, so the teleport threshold rises with it and time dilation can only make the rule more lenient. It defaults to
    /// <see cref="MotionTracker.DefaultTickPeriodSeconds"/> — <c>RuntimeOptions.BaseTickRate</c>'s own default — because
    /// <see cref="SubscriptionsOptions"/> carries no tick rate; the runtime assigns its <c>NominalTickPeriodSeconds</c> over it at start.
    /// </remarks>
    public double TickPeriodSeconds { get; set; } = MotionTracker.DefaultTickPeriodSeconds;

    /// <summary>The native block pool. Read directly by the projection passes.</summary>
    public ReplicationBlockPool Pool { get; }

    /// <summary>Chunk id to block. Read directly by the projection passes.</summary>
    public ReplicationDirectory Directory { get; }

    /// <summary>
    /// The database's network identities and their reuse generations. Shared with every other replicated archetype and owned by neither — disposing this
    /// state leaves it alone.
    /// </summary>
    public NetIdAllocator NetIds { get; }

    /// <summary>Clusters currently carrying a block.</summary>
    public int WatchedClusterCount => Directory.Count;

    /// <summary>
    /// The cluster state this replication state is attached to, which is where the projection pass takes a cluster's bytes from.
    /// <see langword="null"/> until <see cref="AttachTo"/> has run.
    /// </summary>
    public ArchetypeClusterState ClusterState => _attachedTo;

    /// <summary>The blocks this tick's interest hits marked, and the list S1 is partitioned over (SUB-13).</summary>
    public WatchedBlockList WatchedBlocks => _watchedBlocks;

    /// <summary>One record arena per S1 chunk: this tick's pre-encoded enter and state bodies.</summary>
    public RecordArenaSet Records => _records;

    /// <summary>Per-worker slices of the database's identity space, so S1 can name new entities from several workers at once.</summary>
    public NetIdLeaseSet NetIdLeases => _netIdLeases;

    /// <summary>Blocks the projection pass has walked, cumulative.</summary>
    public long BlocksProjected => Volatile.Read(ref _blocksProjected);

    /// <summary>
    /// Slots the projection pass has addressed, cumulative — the measure SUB-13's per-tick-work invariant is asserted against.
    /// </summary>
    /// <remarks>
    /// Counted once per block rather than once per slot: a block contributes the popcount of its live watched mask in one interlocked add, so the number is
    /// exact and the counting is not itself per-entity work. A <c>[Conditional("DEBUG")]</c> counter would have made the assertion unavailable in Release,
    /// which is where the claim actually has to hold.
    /// </remarks>
    public long SlotsProjected => Volatile.Read(ref _slotsProjected);

    /// <summary>Records the projection pass has produced, cumulative. Never a function of how many sessions are connected.</summary>
    public long RecordsProduced => Volatile.Read(ref _recordsProduced);

    /// <summary>Identities the projection pass has given back, cumulative: destroyed entities and reused slots.</summary>
    public long IdentitiesReleased => Volatile.Read(ref _identitiesReleased);

    /// <summary>Entities deferred by a tick because a worker's identity lease ran dry. Non-zero means the lease is sized behind its demand.</summary>
    public long NetIdStarvations => Volatile.Read(ref _netIdStarvations);

    /// <summary>Motion segments the projection pass emitted, cumulative — the adopted trigger set of <see cref="MotionTracker"/>.</summary>
    public long SegmentsEmitted => Volatile.Read(ref _segmentsEmitted);

    /// <summary>
    /// What the rejected "the quantized velocity changed" trigger would have emitted over the same run, counted beside
    /// <see cref="SegmentsEmitted"/> so one recorded run reports both (AC-21).
    /// </summary>
    /// <remarks>
    /// <b>A shadow, not a second engine.</b> It fires on the ticks the rejected trigger would have had to send a segment for a client to be holding this
    /// tick's quantized step velocity — plus the enters and teleports both triggers share. No second binary, no second measurement pass, and no per-entity
    /// state of its own: the reference it compares against is the velocity the entity's current segment carries.
    /// </remarks>
    public long ShadowSegmentsEmitted => Volatile.Read(ref _shadowSegments);

    /// <summary>
    /// Marks slot <paramref name="slot"/> of cluster <paramref name="chunkId"/> watched for this tick — the seam the interest stage reaches S1 through.
    /// </summary>
    /// <param name="chunkId">The hit's cluster.</param>
    /// <param name="slot">The hit's slot within it.</param>
    /// <returns>
    /// <see langword="false"/> when the cluster carries no block yet, which is not an error: the hit belongs on the worker's new-block list and the block is
    /// created at the track's single-threaded blocks step.
    /// </returns>
    /// <remarks>Safe from any number of workers at once; see <see cref="WatchedBlockList.Mark"/>.</remarks>
    public bool MarkWatched(int chunkId, int slot)
    {
        if (_disposed || chunkId < 0 || (uint)slot >= (uint)Layout.SlotCount)
        {
            return false;
        }

        if (!Directory.TryGetBlock(chunkId, out var block))
        {
            return false;
        }

        _watchedBlocks.Mark(block, slot);
        return true;
    }

    /// <summary>
    /// Opens the tick for the projection pass: hands back last tick's released identities, refills the leases and rewinds the record arenas. Single-threaded,
    /// at the track's blocks step, before S1 dispatches.
    /// </summary>
    /// <param name="workers">The chunk count S1 will dispatch.</param>
    /// <remarks>
    /// It deliberately does NOT touch <see cref="WatchedBlocks"/>. The list is filled by whoever marks — the interest stage, which runs <i>before</i> this —
    /// so resetting it here would drop the tick's marks a moment after they were made. <see cref="BeginWatchedBlocks"/> is the reset, and its caller is the
    /// one that is about to refill the list.
    /// </remarks>
    public void BeginProjectTick(int workers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The cold estimate is the watched slots, which is an exact upper bound on the identities this tick can need: an entity gets one only when its entry
        // has none, and only a watched slot is ever reached. It sizes the very first refill and nothing after it — see NetIdLeaseSet.BeginTick.
        _netIdLeases.BeginTick(NetIds, workers, _watchedBlocks.Count * Layout.SlotCount);
        _records.BeginTick(workers);
    }

    /// <summary>
    /// Empties the watched-block list for <paramref name="tick"/> and sizes it so no append can find it full. Called only by a caller that refills it in the
    /// same breath.
    /// </summary>
    /// <param name="tick">The tick about to be projected; it doubles as the list's claim stamp.</param>
    public void BeginWatchedBlocks(uint tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Sized by the directory, which is sized by the watched set: at most one listing per block that exists, so an append can never find the list full.
        _watchedBlocks.BeginTick(tick, Directory.Count);
    }

    /// <summary>Accumulates one block's contribution to the pass's counters.</summary>
    /// <param name="blocks">Blocks walked.</param>
    /// <param name="slots">Slots addressed.</param>
    /// <param name="records">Records produced.</param>
    /// <param name="releases">Identities released.</param>
    public void NoteProjected(int blocks, int slots, int records, int releases)
    {
        if (blocks != 0)
        {
            Interlocked.Add(ref _blocksProjected, blocks);
        }

        if (slots != 0)
        {
            Interlocked.Add(ref _slotsProjected, slots);
        }

        if (records != 0)
        {
            Interlocked.Add(ref _recordsProduced, records);
        }

        if (releases != 0)
        {
            Interlocked.Add(ref _identitiesReleased, releases);
        }
    }

    /// <summary>Records that a worker's identity lease ran dry and one entity was deferred to the next tick.</summary>
    public void NoteNetIdStarvation() => Interlocked.Increment(ref _netIdStarvations);

    /// <summary>Accumulates one block's motion segments, and what the rejected trigger would have emitted over the same block.</summary>
    /// <param name="emitted">Segments this rule emitted.</param>
    /// <param name="shadow">Segments the rejected "the quantized velocity changed" trigger would have emitted.</param>
    public void NoteSegments(int emitted, int shadow)
    {
        if (emitted != 0)
        {
            Interlocked.Add(ref _segmentsEmitted, emitted);
        }

        if (shadow != 0)
        {
            Interlocked.Add(ref _shadowSegments, shadow);
        }
    }

    /// <summary>Resets the cumulative pass counters. For fixtures that measure one tick's work, never for production.</summary>
    public void ResetProjectionCounters()
    {
        Volatile.Write(ref _blocksProjected, 0);
        Volatile.Write(ref _slotsProjected, 0);
        Volatile.Write(ref _recordsProduced, 0);
        Volatile.Write(ref _identitiesReleased, 0);
        Volatile.Write(ref _netIdStarvations, 0);
        Volatile.Write(ref _segmentsEmitted, 0);
        Volatile.Write(ref _shadowSegments, 0);
    }

    /// <summary>Drain releases that were refused or faulted. Non-zero means a block outlived its cluster; see <see cref="ReleaseBlockForDrain"/>.</summary>
    public long DrainFaults => Volatile.Read(ref _drainFaults);

    /// <summary>The last exception swallowed by <see cref="ReleaseBlockForDrain"/>, for diagnosis. <see langword="null"/> when none.</summary>
    public Exception LastDrainFault { get; private set; }

    /// <summary>
    /// Publishes this state to <paramref name="clusterState"/> so the ECS drain sites can reach it, and remembers the attachment so
    /// <see cref="Dispose(bool)"/> can undo it.
    /// </summary>
    /// <remarks>
    /// Assigning <c>clusterState.ReplicationState</c> directly works and is what the ECS reads, but it leaves the ECS holding a raw reference that nothing
    /// clears when this node is disposed — and since this type became a resource-graph node, a parent can dispose it by cascade without the ECS ever
    /// knowing. Every drain after that would then fault. Going through here is what makes detachment automatic.
    /// </remarks>
    public void AttachTo(ArchetypeClusterState clusterState)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(clusterState);

        _attachedTo = clusterState;
        clusterState.ReplicationState = this;
    }

    /// <summary>
    /// Releases <paramref name="chunkId"/>'s block on behalf of the ECS cluster-drain sites. <b>Never throws.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain loop it is called from has no <c>try/finally</c>: an exception raised here skips the <c>FreeChunk</c> two lines later — orphaning a chunk
    /// id permanently, since the cluster is already off the active list and detached from its cell — and skips the <c>_drainedCount = 0</c> reset, so every
    /// id already processed in that pass is reprocessed next tick against a segment that has moved on. The inline release sites are worse still: they
    /// propagate out through <c>Transaction.Commit</c>.
    /// </para>
    /// <para>
    /// Swallowing is therefore the lesser failure, but it must not be silent: a refusal is counted in <see cref="DrainFaults"/> and the exception kept in
    /// <see cref="LastDrainFault"/>. A non-zero count means a block has outlived its cluster and the directory may name a recycled id — SUB-09's failure,
    /// visible rather than inferred.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when a block was released; <see langword="false"/> when there was none, or the attempt failed.</returns>
    public bool ReleaseBlockForDrain(int chunkId)
    {
        if (_disposed || chunkId < 0)
        {
            return false;
        }

        try
        {
            return TryReleaseBlock(chunkId);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _drainFaults);
            LastDrainFault = ex;
            return false;
        }
    }

    /// <summary>
    /// Rents a block and registers it for <paramref name="chunkId"/> as one act, returning the block to the pool if registration does not take.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the pool's budget binds, or when the cluster already has a block. In both cases nothing is leaked and nothing is
    /// committed that was not already.
    /// </returns>
    public bool TryAttachBlock(int chunkId, out ReplicationBlockHeader* block)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Validated BEFORE renting. The directory throws on a negative chunk id, and a throw after the rent would strand the block — unreachable because no
        // directory names it, and unreturnable because the caller never received it.
        if (chunkId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkId), chunkId, "A cluster chunk id is never negative");
        }

        if (!Pool.TryRent(out var rented))
        {
            block = null;
            return false;
        }

        bool registered;
        try
        {
            registered = Directory.TryAdd(chunkId, rented);
        }
        catch
        {
            // The directory can still throw here for reasons this method cannot pre-empt — an already-stamped block, or an allocation failure inside the
            // map's inline resize on the normal growth path. Either way the rent must not survive the exception.
            Pool.Return(rented);
            throw;
        }

        if (registered)
        {
            // ZEROED HERE, and it has to be somewhere. The pool hands back a block whose HEADER is clean and whose ENTRIES still hold the previous cluster's
            // bytes, on the reasoning that the per-entry EntityId check initializes them on first use. That check is not enough on its own: it reads the
            // entry's netId in order to release it, and a stale non-zero word there would release an identity this entry never held — which the allocator
            // rejects as a double release, from inside a parallel pass. One memset per newly watched cluster, on a path that has just rented a block, buys
            // the whole initialization path a known starting state.
            NativeMemory.Clear((byte*)rented + ReplicationBlockLayout.HeaderSize, (nuint)(Layout.BlockSize - ReplicationBlockLayout.HeaderSize));
        }
        else
        {
            // The cluster already had a block. Hand this one straight back rather than dropping it on the floor: a rented block that reaches no directory is
            // unreachable and unreturnable, which is a leak the pool cannot detect.
            Pool.Return(rented);
            block = null;
            return false;
        }

        block = rented;
        return true;
    }

    /// <summary>
    /// Drops <paramref name="chunkId"/>'s block from the directory and returns it to the pool as one act.
    /// </summary>
    /// <remarks>
    /// This is the primitive the cluster-drain hook calls, before <c>FreeChunk</c> hands the chunk id back to the segment allocator. Splitting it would
    /// reintroduce exactly the window the drain convention exists to close.
    /// </remarks>
    /// <returns><see langword="false"/> when the cluster had no block, which is not an error.</returns>
    public bool TryReleaseBlock(int chunkId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Directory.TryRemove(chunkId, out var block))
        {
            return false;
        }

        Pool.Return(block);
        return true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Before the directory and the pool, because each of these names memory of its own and none of them names a block.
        _watchedBlocks.Dispose();
        _records.Dispose();
        _netIdLeases.Dispose();

        // Detach FIRST. A resource-graph parent can dispose this node by cascade without the ECS knowing, and the ECS holds a raw reference through
        // ArchetypeClusterState.ReplicationState; leaving it set would point every later cluster drain at a disposed state.
        if (_attachedTo != null)
        {
            _attachedTo.ReplicationState = null;
            _attachedTo = null;
        }

        // ORDER IS LOAD-BEARING. The directory's values are pointers into the pool's slabs, so it goes first: disposing the pool first would leave entries
        // naming freed native memory, and anything that read the directory in between would be handed a dangling pointer. The pool is a child of this node,
        // so base.Dispose is what takes it down — after the directory, which is the order this needs.
        Directory.Dispose();

        base.Dispose(disposing);

        Parent?.RemoveChild(this);
    }
}
