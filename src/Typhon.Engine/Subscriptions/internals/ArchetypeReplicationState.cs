using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    private long _blocksDormant;

    // ── The tick's projection state ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Three objects, one lifetime, and they hang here rather than off the runtime because every one of them is per ARCHETYPE: a projected block belongs to
    // one archetype's directory, the scratch is sized to this archetype's fields, and a lease is spent initializing this archetype's entries. They are
    // created eagerly and reset per tick — none of them allocates once the pushed set has stopped growing (SUB-07).
    private readonly WatchedBlockList _watchedBlocks = new();
    private readonly Lock _parkLock = new();
    private ParkedEntryList _parked;
    private long _entriesMigrated;
    private long _entriesParked;
    private long _parkedDropped;
    private long _migrationsAbandoned;
    private readonly ProjectionScratchSet _scratch = new();
    private readonly NetIdLeaseSet _netIdLeases = new();

    // The identities the orphan path took — entries that vanished without a projection to release them: a block released with live entries, an entry
    // overwritten by a migration or by the parked drain, a parked entry dropped. Each became a leave event; each is handed back to the allocator at the
    // SECOND BeginProjectTick after it, which is when the projection's own releases of a tick are (one tick queued in a lease, then released): taken during
    // tick N's fence or blocks step, moved aside at N's BeginProjectTick, released at N+1's. Locked: a fence's migration slices and a commit's inline block
    // release reach it from other threads.
    private readonly Lock _orphanedLock = new();
    private List<uint> _orphaned = [];
    private List<uint> _orphanedPrevious = [];

    // The list being released, outside the lock; touched by the track alone, then kept as the next tick's empty list.
    private List<uint> _orphanedSpare = [];
    private long _orphanReleaseFaults;

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
            var bytes = Directory.EstimatedBytes + _watchedBlocks.EstimatedBytes + _scratch.EstimatedBytes + _netIdLeases.EstimatedBytes + 128L;
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

    /// <summary>Blocks the projection pass declined because their cluster was dormant, across every tick since start.</summary>
    /// <remarks>
    /// Zero whenever the application has not enabled dormancy, which is what makes it usable as an anti-vacuity check rather than merely a diagnostic:
    /// a measurement that shows no change means one thing if the pass declined most of the work and the opposite if it declined none.
    /// </remarks>
    public long BlocksDormant => Volatile.Read(ref _blocksDormant);

    /// <summary>Records that the pass declined one block because its cluster was dormant.</summary>
    /// <remarks>
    /// One atomic per DECLINED block, beside the one <c>NoteProjected</c> already pays per projected block. It rides the cheap path by construction —
    /// a declined block does no slot loop at all — so it cannot cost more than the work it replaces.
    /// </remarks>
    public void NoteBlockDormant() => Interlocked.Increment(ref _blocksDormant);

    /// <summary>The blocks this tick's pushes marked, and the list S1 is partitioned over (SUB-13).</summary>
    public WatchedBlockList WatchedBlocks => _watchedBlocks;

    /// <summary>
    /// The projection's shared block cursor (unless <see cref="SubscriptionsOptions.DeterministicProjection"/>): chunks claim watched blocks from it in small batches instead of a fixed stride, so a worker that
    /// joins the stage late finds the work already shared out rather than its whole stride waiting for it. The live slot is element
    /// <see cref="ProjectCursorSlot"/>, alone on its cache line; the rest is padding.
    /// </summary>
    internal readonly int[] ProjectCursor = new int[32];

    internal const int ProjectCursorSlot = 16;

    // Diagnostic only: skips the scan of a released block's live entries, and with it their leaves AND the release of their identities — which then leak.
    private static readonly bool NoOrphanScan = Environment.GetEnvironmentVariable("TYPHON_PUSH_NO_ORPHAN_SCAN") == "1";

    /// <summary>The push path, when this archetype is push-served; <see langword="null"/> otherwise.</summary>
    internal PushReplication Push;

    /// <summary>Where owner-group changes are routed to their controlling sessions (11 § 2.2); set only when the archetype declares owner fields.</summary>
    internal SelfTracker Self;

    /// <summary>netId → entity, for a command's entity reference (SUB-26): bound where an identity is assigned, unbound where it is released.</summary>
    internal NetIdEntityIndex EntityIndex;

    /// <summary>
    /// Every block by chunk id. A push archetype has a block for every live cluster and looks one up per pushed cluster per tick and per
    /// cluster a sweep reaches, so the directory's hash probe is replaced by an index. Written only by attach and release, which are serial.
    /// </summary>
    internal nint[] BlockByChunk = [];

    /// <summary>This archetype's plan index, for the push path's events.</summary>
    internal int PushArchetypeIndex;

    /// <summary>One projection scratch per S1 chunk.</summary>
    public ProjectionScratchSet Scratch => _scratch;

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

    /// <summary>Entities the projection pass found entered or changed, cumulative. Never a function of how many sessions are connected.</summary>
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
    /// Carries a watched entity's replication entry from the slot it left to the slot it arrived in.
    /// </summary>
    /// <param name="srcChunkId">The cluster it left.</param>
    /// <param name="srcSlot">The slot it left.</param>
    /// <param name="dstChunkId">The cluster it arrived in.</param>
    /// <param name="dstSlot">The slot it arrived in.</param>
    /// <param name="worker">Unused; kept so the call site reads as the per-slice operation it is.</param>
    /// <returns>What happened, for the counters and for the tests that have to tell "nothing to carry" from "carried".</returns>
    /// <remarks>
    /// <para>
    /// <b>SUB-09's movement half.</b> An entry is reachable from the entity's CURRENT cluster and slot or it is not reachable at all: leaving it behind means
    /// the destination slot reads as a brand-new entity, which is published to every watching session as a leave and a full enter for something that merely
    /// walked over a boundary. It costs an enter record per session per crossing and throws away the client's interpolation state — measured before this
    /// existed as a netId changing from 54 to 32 across one 4 000 m move.
    /// </para>
    /// <para>
    /// <b>Most moves carry nothing, and that is what keeps this cheap.</b> An entity nobody watches has no block behind its cluster, so the first lookup
    /// fails and the call is two branches. In a large archetype that is almost every move.
    /// </para>
    /// <para>
    /// <b>A destination with no block parks a COPY of the bytes, never a pointer to the source.</b> The source slot can be reused inside the same migration
    /// step, so a pointer would be read after the bytes under it had become another entity's. The parked entry is written into its block by the replication
    /// prologue, which is single-threaded and runs before anything reads the directory.
    /// </para>
    /// <para>
    /// <b>Parking takes a lock, and the design's per-worker lists do not exist.</b> <c>08 § 4</c> proposes one list per migration slice precisely so that
    /// nothing synchronises — but sizing those lists needs the slice count before the slices run, and the only place that knows it is inside the fence, which
    /// would have to reach into replication to say so. The deviation is affordable because <b>parking is the rare branch of a rare branch</b>: it needs an
    /// entity that is watched AND that moved into a cluster nobody was watching, so the lock is not on the path most moves take. Contention here would be a
    /// signal worth acting on, and <see cref="EntriesParked"/> is what would show it.
    /// </para>
    /// <para>
    /// The drain runs at a single-threaded point separated from the slices by the fence's own barrier, so the bytes a slice copied are published to it
    /// without anything further on arm64 or x64.
    /// </para>
    /// </remarks>
    public ReplicationMigrationOutcome MigrateEntry(int srcChunkId, int srcSlot, int dstChunkId, int dstSlot, int worker)
    {
        // TOTAL, because this one runs on the FENCE. Every other replication entry point is called from the replication track, which SUB-02 deliberately
        // exempts from the tick's terminal latch — a defect in the newest subsystem degrades replication rather than stopping the database. Step 6b puts
        // replication code on the fence, where that exemption does not apply: ExecuteMigrations has a finally and no catch, so anything thrown here
        // latches IsFenceFailed and every later tick returns early. The reachable throw is a disposal cascade — this object's own _disposed is checked
        // below, but Directory.TryGetBlock checks the DIRECTORY's, a different flag on a different object that Dispose clears later in the same
        // sequence, so a slice already past the first check can still enter a disposed directory.
        try
        {
            return MigrateEntryCore(srcChunkId, srcSlot, dstChunkId, dstSlot, worker);
        }
        catch (ObjectDisposedException)
        {
            // The runtime is going away underneath a fence that is still in flight. Losing the entry costs the entity a leave and an enter to whoever is
            // still watching, which is the behaviour this method exists to remove — but it is the safe direction, and nobody is watching a runtime that
            // is being disposed.
            Interlocked.Increment(ref _migrationsAbandoned);
            return ReplicationMigrationOutcome.NothingToCarry;
        }
    }

    /// <summary>The migration itself. See <see cref="MigrateEntry"/>, which is what makes it total.</summary>
    /// <param name="srcChunkId">The cluster it left.</param>
    /// <param name="srcSlot">The slot it left.</param>
    /// <param name="dstChunkId">The cluster it arrived in.</param>
    /// <param name="dstSlot">The slot it arrived in.</param>
    /// <param name="worker">Unused; kept so the call site reads as the per-slice operation it is.</param>
    /// <returns>What happened.</returns>
    private ReplicationMigrationOutcome MigrateEntryCore(int srcChunkId, int srcSlot, int dstChunkId, int dstSlot, int worker)
    {
        if (_disposed || srcChunkId < 0 || dstChunkId < 0 || (uint)srcSlot >= (uint)Layout.SlotCount || (uint)dstSlot >= (uint)Layout.SlotCount)
        {
            return ReplicationMigrationOutcome.NothingToCarry;
        }

        // An arrival is a push. The claim that received the entity raises no membership mark of its own, and without one the arrival —
        // which carries the entity's new position — would sit unprojected until something else pushed it. The fence publishes the marks after migrations.
        if (Push != null)
        {
            _attachedTo?.NoteStructureSlots(dstChunkId, 1UL << dstSlot);
        }

        if (!Directory.TryGetBlock(srcChunkId, out var source))
        {
            // Nobody watches the cluster it left, so there is no entry to carry. The destination will initialise one the first time it is projected.
            return ReplicationMigrationOutcome.NothingToCarry;
        }

        var srcBytes = (byte*)source;
        var hotSource = srcBytes + Layout.HotOffset + (srcSlot * Layout.HotStride);
        var coldSource = srcBytes + Layout.ColdOffset + (srcSlot * Layout.ColdStride);

        if (((ReplicationHotEntry*)hotSource)->NetId == 0)
        {
            // The cluster is watched but this slot never was: an entry with no identity describes nothing, and carrying it would only move zeroes.
            return ReplicationMigrationOutcome.NothingToCarry;
        }

        if (Directory.TryGetBlock(dstChunkId, out var destination))
        {
            var dstBytes = (byte*)destination;
            if (Push != null)
            {
                var overwritten = ((ReplicationHotEntry*)(dstBytes + Layout.HotOffset + (dstSlot * Layout.HotStride)))->NetId;
                if (overwritten != NetIdAllocator.NoNetId)
                {
                    Orphaned(destination, dstBytes + Layout.ColdOffset + (dstSlot * Layout.ColdStride), overwritten, 1);
                }
            }

            Unsafe.CopyBlockUnaligned(dstBytes + Layout.HotOffset + (dstSlot * Layout.HotStride), hotSource, (uint)Layout.HotStride);
            Unsafe.CopyBlockUnaligned(dstBytes + Layout.ColdOffset + (dstSlot * Layout.ColdStride), coldSource, (uint)Layout.ColdStride);

            // The owner region too. A slot's entry is hot + cold + owner, and carrying two thirds of it would leave the owner fields of whoever previously
            // occupied the destination slot attached to the arriving entity — the SELF data a client is sent about the entity it controls.
            if (Layout.OwnerEntrySize > 0)
            {
                Unsafe.CopyBlockUnaligned(
                    dstBytes + Layout.OwnerOffset + (dstSlot * Layout.OwnerEntrySize),
                    srcBytes + Layout.OwnerOffset + (srcSlot * Layout.OwnerEntrySize),
                    (uint)Layout.OwnerEntrySize);
            }

            ClearEntry(srcBytes, srcSlot);

            // The arrival, named for the projection. Without it an entity whose projected bytes did not change on this tick moves clusters invisibly, and
            // the session watching the cluster it LEFT emits a leave for an entity still inside its own view (see ReplicationBlockHeader.ArrivedSlots).
            Interlocked.Or(ref destination->ArrivedSlots, 1UL << dstSlot);
            Interlocked.Increment(ref _entriesMigrated);
            return ReplicationMigrationOutcome.Carried;
        }

        if (!Park(dstChunkId, dstSlot, hotSource, coldSource))
        {
            // Counted as a DROP, not a park. Reporting it as parked would break the one identity that reveals the disposal window happening at all:
            // everything parked is either written by the drain or counted as dropped.
            ClearEntry(srcBytes, srcSlot);
            return ReplicationMigrationOutcome.NothingToCarry;
        }

        ClearEntry(srcBytes, srcSlot);
        Interlocked.Increment(ref _entriesParked);
        return ReplicationMigrationOutcome.Parked;
    }

    /// <summary>Zeroes a slot's entries, so the slot the entity left describes nothing rather than describing it twice.</summary>
    /// <param name="blockBytes">The block.</param>
    /// <param name="slot">The slot.</param>
    /// <remarks>
    /// Leaving the source populated is the failure this half of SUB-09 exists to prevent from the other direction: the next entity to take that slot would
    /// inherit an identity, a baseline and a motion segment belonging to something else, and would be published under them.
    /// </remarks>
    private void ClearEntry(byte* blockBytes, int slot)
    {
        NativeMemory.Clear(blockBytes + Layout.HotOffset + (slot * Layout.HotStride), (nuint)Layout.HotStride);
        NativeMemory.Clear(blockBytes + Layout.ColdOffset + (slot * Layout.ColdStride), (nuint)Layout.ColdStride);

        // Including the owner region: a slot the entity left must describe NOTHING, and owner fields left behind would be inherited by the next occupant
        // exactly as a stale identity would.
        if (Layout.OwnerEntrySize > 0)
        {
            NativeMemory.Clear(blockBytes + Layout.OwnerOffset + (slot * Layout.OwnerEntrySize), (nuint)Layout.OwnerEntrySize);
        }
    }

    /// <summary>Copies an entry aside until the prologue can create the block it belongs in.</summary>
    /// <param name="chunkId">The destination cluster.</param>
    /// <param name="slot">The destination slot.</param>
    /// <param name="hot">The hot entry's bytes.</param>
    /// <param name="cold">The cold entry's bytes.</param>
    /// <returns><see langword="false"/> when the entry could not be kept, which makes it a DROP rather than a park.</returns>
    private bool Park(int chunkId, int slot, byte* hot, byte* cold)
    {
        lock (_parkLock)
        {
            if (_disposed)
            {
                // Checked INSIDE the lock. MigrateEntry's guard is outside it and is a plain read, so a slice can win this lock after Dispose has already
                // disposed and nulled the list — and constructing a fresh one here would allocate native memory whose only owner has just given up its
                // field and will never run again. A leak rather than a use-after-free, and silent either way.
                Interlocked.Increment(ref _parkedDropped);
                return false;
            }

            _parked ??= new ParkedEntryList(Layout.HotStride + Layout.ColdStride);
            return _parked.Add(chunkId, slot, hot, Layout.HotStride, cold, Layout.ColdStride);
        }
    }

    /// <summary>
    /// Writes every parked entry into the block it belongs in, creating nothing: a destination that still has no block drops its entry.
    /// </summary>
    /// <returns>How many entries were written.</returns>
    /// <remarks>
    /// Single-threaded, at the prologue, after the blocks step has created the blocks this tick's interest asked for. An entry whose destination STILL has no
    /// block belongs to a cluster nobody watches, so there is nothing for it to be read out of and dropping it is correct rather than lossy — the entity will
    /// be initialised from current values the first time somebody does watch it.
    /// </remarks>
    public int DrainParkedEntries()
    {
        // The SAME lock parking takes, and not because the two are expected to overlap — they are not: parking happens in the fence's migration slices
        // and this runs at the track's blocks step, after the fence. The lock is here because what Read hands back is a raw pointer into the list's
        // native buffer, and EnsureCapacity FREES that buffer when it grows. A park racing a drain would therefore not give this a stale count, it would
        // give it an address that has been freed, and the copies below would read it. The ordering that prevents it is a property of where these two are
        // called from, which nothing here enforces and nothing would notice being changed; the lock is uncontended at a single-threaded point, so it
        // costs nothing to stop depending on it. It closes the same window against Dispose, which frees the buffer under this same lock.
        lock (_parkLock)
        {
            return DrainLocked();
        }
    }

    /// <summary>The drain itself, with <see cref="_parkLock"/> already held.</summary>
    /// <returns>How many entries were written.</returns>
    private int DrainLocked()
    {
        var list = _parked;
        if (list == null || list.Count == 0)
        {
            return 0;
        }

        var written = 0;
        for (var i = 0; i < list.Count; i++)
        {
            list.Read(i, out var chunkId, out var slot, out var bytes);

            // The identity the entry describes, taken from the hot entry that was copied aside. It is compared with whoever occupies the destination NOW,
            // because the chunk id was captured during the fence and a cluster that drained since then returns its id to a LIFO free list: the blocks step
            // runs immediately before this and can hand a brand-new cluster that same id. Writing the entry then would attach one entity's identity, baseline
            // and motion segment to another — SUB-09's "an entry inherited through a recycled chunk id", silent exactly as that rule's on_violation says.
            // It is the same compare the read path already makes per slot, paid once per parked entry.
            // KNOWN HAZARD, deliberately unguarded and recorded rather than closed. The chunk id was captured during the fence, and a cluster that
            // drained since then returns its id to a LIFO free list — the blocks step runs immediately before this and could hand a brand-new cluster
            // that same id, so this write would attach one entity's identity and baseline to another (SUB-09's "an entry inherited through a recycled
            // chunk id"). Nobody has constructed the interleaving: it needs a destination cluster to receive a migrant AND be emptied in the same
            // fence. The obvious guard — comparing the parked entry's EntityId with whoever occupies the destination slot — was tried and REVERTED:
            // reading the cluster segment from here takes a chunk accessor inside the replication prologue and broke the track wholesale, dropping
            // a bot swarm from about two hundred frames per session to one. Closing this needs a discriminator that does not touch the segment.
            if ((uint)slot < (uint)Layout.SlotCount && Directory.TryGetBlock(chunkId, out var block))
            {
                var dstBytes = (byte*)block;
                if (Push != null)
                {
                    var overwritten = ((ReplicationHotEntry*)(dstBytes + Layout.HotOffset + (slot * Layout.HotStride)))->NetId;
                    if (overwritten != NetIdAllocator.NoNetId)
                    {
                        Orphaned(block, dstBytes + Layout.ColdOffset + (slot * Layout.ColdStride), overwritten, 2);
                    }
                }

                Unsafe.CopyBlockUnaligned(dstBytes + Layout.HotOffset + (slot * Layout.HotStride), bytes, (uint)Layout.HotStride);
                Unsafe.CopyBlockUnaligned(dstBytes + Layout.ColdOffset + (slot * Layout.ColdStride), bytes + Layout.HotStride, (uint)Layout.ColdStride);
                if (Layout.OwnerEntrySize > 0)
                {
                    Unsafe.CopyBlockUnaligned(
                        dstBytes + Layout.OwnerOffset + (slot * Layout.OwnerEntrySize),
                        bytes + Layout.HotStride + Layout.ColdStride,
                        (uint)Layout.OwnerEntrySize);
                }

                // Same reason as the carried path: a parked entry lands with every stamp it left with, so nothing else would name the slot.
                Interlocked.Or(ref block->ArrivedSlots, 1UL << slot);
                written++;
            }
            else
            {
                // Dropped: every session holding the entity is told it left, and the occupancy stops counting it where it was last described. It is
                // initialised afresh, under a new identity, when a block next covers it.
                if (Push != null)
                {
                    var droppedId = ((ReplicationHotEntry*)bytes)->NetId;
                    if (droppedId != NetIdAllocator.NoNetId)
                    {
                        Orphaned(null, bytes + Layout.HotStride, droppedId, 2);
                    }
                }

                Interlocked.Increment(ref _parkedDropped);
            }
        }

        list.Clear();
        return written;
    }

    /// <summary>Entries carried straight into a destination block that already existed.</summary>
    public long EntriesMigrated => Volatile.Read(ref _entriesMigrated);

    /// <summary>Entries copied aside because their destination had no block yet.</summary>
    public long EntriesParked => Volatile.Read(ref _entriesParked);

    /// <summary>Migrations abandoned because the runtime was being disposed under a fence still in flight. Non-zero is a teardown race, not data loss.</summary>
    public long MigrationsAbandoned => Volatile.Read(ref _migrationsAbandoned);

    /// <summary>Parked entries whose destination still had no block when the prologue ran, so they were dropped.</summary>
    /// <remarks>
    /// Not a defect: a destination nobody watches has nothing to read the entry out of, and the entity is initialised from current values the first time
    /// somebody does watch it. It costs that entity one enter record, which is exactly what the migration hook saves in the case that DOES have a block.
    /// </remarks>
    public long ParkedDropped => Volatile.Read(ref _parkedDropped);

    /// <summary>
    /// Opens the tick for the projection pass: hands back last tick's released identities, refills the leases and sizes the scratch. Single-threaded, at
    /// the track's blocks step, before S1 dispatches.
    /// </summary>
    /// <param name="workers">The chunk count S1 will dispatch.</param>
    /// <remarks>
    /// It deliberately does NOT touch <see cref="WatchedBlocks"/>. The list is filled by whoever marks — the push path, which runs <i>before</i> this — so
    /// resetting it here would drop the tick's marks a moment after they were made. <see cref="BeginWatchedBlocks"/> is the reset, and its caller is the one
    /// that is about to refill the list.
    /// </remarks>
    public void BeginProjectTick(int workers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ProjectCursor[ProjectCursorSlot] = 0;

        ReleaseOrphaned();

        // The cold estimate is the pushed slots' blocks, an upper bound on the identities this tick can need: an entity gets one only when its entry has none,
        // and only a pushed slot is ever reached. It sizes the very first refill and nothing after it — see NetIdLeaseSet.BeginTick.
        _netIdLeases.BeginTick(NetIds, workers, _watchedBlocks.Count * Layout.SlotCount);

        // Every identity a lease can hand out this tick is at or below the high-water mark the refill just moved: reserved here, bound from the chunks.
        EntityIndex?.Reserve(NetIds.HighWaterMark);
        _scratch.BeginTick(workers);
    }

    /// <summary>
    /// Empties the watched-block list for <paramref name="tick"/> and sizes it so no append can find it full. Called only by a caller that refills it in the
    /// same breath.
    /// </summary>
    /// <param name="tick">The tick about to be projected; it doubles as the list's claim stamp.</param>
    public void BeginWatchedBlocks(uint tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Sized by the directory: at most one listing per block that exists, so an append can never find the list full.
        _watchedBlocks.BeginTick(tick, Directory.Count);
    }

    /// <summary>An entry vanished with no projection to see it go: every session holding it is told it left, and its identity goes back to the allocator.</summary>
    private void Orphaned(ReplicationBlockHeader* block, byte* cold, uint netId, int cause)
    {
        // Filed in the replication of the realm the entry was last described in; a realm nobody serves holds no session to tell. A parked drop has no
        // block, and until entries cross realms (R4.5) a parked entry is realm 0's.
        var push = block == null || Push.Hub == null ? Push : Push.Hub.For(block->Realm);
        push?.Orphan(PushArchetypeIndex, block, cold, Layout, netId, cause);
        lock (_orphanedLock)
        {
            _orphaned.Add(netId);
        }
    }

    /// <summary>
    /// Serial, at the track's blocks step (<see cref="BeginProjectTick"/>, or <see cref="FlushIdleTick"/> on a tick with nothing to project): releases the
    /// identities orphaned before the previous step, and sets aside those orphaned since. The lists are swapped under the lock and released outside it, so a
    /// committing thread that orphans a block is not held up. An identity the allocator refuses — already free — and an identity orphaned twice before its
    /// release (an entry exists in one place, so twice is a defect) are counted in <see cref="OrphanReleaseFaults"/>, not thrown: this runs on the track.
    /// </summary>
    private void ReleaseOrphaned()
    {
        List<uint> releasing;
        lock (_orphanedLock)
        {
            releasing = _orphanedPrevious;
            _orphanedPrevious = _orphaned;
            _orphaned = _orphanedSpare;
        }

        try
        {
            releasing.Sort();
            var last = NetIdAllocator.NoNetId;
            foreach (var netId in releasing)
            {
                if (netId == last)
                {
                    Interlocked.Increment(ref _orphanReleaseFaults);
                    continue;
                }

                last = netId;
                try
                {
                    NetIds.Release(netId);
                    EntityIndex?.Unbind(netId);
                    Interlocked.Increment(ref _identitiesReleased);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _orphanReleaseFaults);
                }
            }
        }
        finally
        {
            releasing.Clear();
            _orphanedSpare = releasing;
        }
    }

    /// <summary>
    /// A tick the track runs with no block to project (the blocks step returns before <see cref="BeginProjectTick"/>): the releases a projection tick would
    /// make still happen — the leases' queued ones and the orphans' — so identities do not stay live in a quiet world.
    /// </summary>
    public void FlushIdleTick()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseOrphaned();
        _netIdLeases.FlushReleases(NetIds);
    }

    /// <summary>Orphaned identities the allocator refused to release — already free. Zero when every orphan names a live identity once.</summary>
    public long OrphanReleaseFaults => Volatile.Read(ref _orphanReleaseFaults);

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
        Volatile.Write(ref _blocksDormant, 0);
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
            // A block released with live entries takes identities clients may hold; each becomes a leave.
            if (Push != null && !NoOrphanScan && Directory.TryGetBlock(chunkId, out var releasing))
            {
                var rb = (byte*)releasing;
                for (var s = 0; s < Layout.SlotCount; s++)
                {
                    var netId = ((ReplicationHotEntry*)(rb + Layout.HotOffset + (s * Layout.HotStride)))->NetId;
                    if (netId != NetIdAllocator.NoNetId)
                    {
                        Orphaned(releasing, rb + Layout.ColdOffset + (s * Layout.ColdStride), netId, 0);
                    }
                }
            }

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

            // The cluster's realm, fixed from its claim to its drain (Realms C1), which is exactly this block's lifetime (R4.2).
            rented->Realm = RealmOfCluster(chunkId);
            if (Push != null)
            {
                if (chunkId >= BlockByChunk.Length)
                {
                    Array.Resize(ref BlockByChunk, Math.Max(chunkId + 1, BlockByChunk.Length * 2));
                }

                BlockByChunk[chunkId] = (nint)rented;
            }
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

    /// <summary>The realm cluster <paramref name="chunkId"/> is in: realm 0 for an unattached state or a single-realm archetype.</summary>
    internal ushort RealmOfCluster(int chunkId)
    {
        var map = _attachedTo == null ? null : Volatile.Read(ref _attachedTo.ClusterRealmMap);
        return map != null && (uint)chunkId < (uint)map.Length ? map[chunkId] : RealmId.Default.Value;
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

        if (Push != null && (uint)chunkId < (uint)BlockByChunk.Length)
        {
            BlockByChunk[chunkId] = 0;
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
        _scratch.Dispose();
        _netIdLeases.Dispose();

        // The parked entries hold native memory of their own, and nothing else names it.
        lock (_parkLock)
        {
            _parked?.Dispose();
            _parked = null;
        }

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
