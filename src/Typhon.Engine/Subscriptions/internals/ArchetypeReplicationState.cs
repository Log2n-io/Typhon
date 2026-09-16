using System;
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

    /// <summary>Creates the per-archetype replication state. Nothing is committed until the first block is attached.</summary>
    /// <param name="id">Stable resource id prefix; the pool registers as <c>{id}.Pool</c>.</param>
    /// <param name="parent">Resource-graph parent for the pool.</param>
    /// <param name="allocator">Engine allocator, supplied by DI.</param>
    /// <param name="layout">The archetype's block layout.</param>
    /// <param name="options">Operator configuration carrying the pool's budget.</param>
    public ArchetypeReplicationState(string id, IResource parent, IMemoryAllocator allocator, ReplicationBlockLayout layout, SubscriptionsOptions options)
        : base(Require(id, nameof(id)), ResourceType.Node, Require(parent, nameof(parent)))
    {
        // id and parent are validated in the base-call arguments above: the base constructor registers this node with the graph, so validating them in the
        // body would run too late — a null parent would already have faulted inside ResourceNode, and a null id would leave a registered node behind.
        ArgumentNullException.ThrowIfNull(options);

        Layout = layout;

        // The pool hangs off THIS node, not off `parent`, so the graph reads Runtime -> {id} -> {id}.Pool -> slabs. That nesting is what lets this node
        // report the directory and identity bytes — which are nobody's child and were previously invisible — while the slabs stay accounted to the pool.
        //
        // The pool registers itself as it is constructed, so anything that throws after this point must dispose it or leave a registry node behind that
        // nothing owns and nothing will ever free.
        Pool = new ReplicationBlockPool($"{id}.Pool", this, allocator, layout, options);
        try
        {
            Directory = new ReplicationDirectory();
            NetIds = new NetIdAllocator();
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
    /// The directory's backing map and the identity allocator's side arrays, which are plain managed arrays owned by this node rather than resources in
    /// their own right. The pool's slabs are deliberately excluded: the pool is a CHILD of this node, and <see cref="IMemoryResource"/> requires a node to
    /// exclude its children or the graph double-counts them.
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
            var bytes = Directory.EstimatedBytes + NetIds.EstimatedBytes + 128L;
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

    /// <summary>The native block pool. Read directly by the projection passes.</summary>
    public ReplicationBlockPool Pool { get; }

    /// <summary>Chunk id to block. Read directly by the projection passes.</summary>
    public ReplicationDirectory Directory { get; }

    /// <summary>Network identities and their reuse generations.</summary>
    public NetIdAllocator NetIds { get; }

    /// <summary>Clusters currently carrying a block.</summary>
    public int WatchedClusterCount => Directory.Count;

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

        if (!registered)
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
