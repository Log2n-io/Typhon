// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// A single MVCC snapshot-isolated transaction: the unit of ECS reads and mutations (Spawn / Open / Destroy, component reads and writes) executed against
/// the snapshot fixed at its <see cref="EntityAccessor.TSN"/>. Obtained from a <see cref="UnitOfWork"/> (or the convenience
/// <see cref="DatabaseEngineExtensions.CreateQuickTransaction"/>) and finished with <see cref="Commit(ConcurrencyConflictHandler)"/> or
/// <see cref="Rollback()"/>.
/// </summary>
/// <remarks>
/// Instances are pooled and reused; do not hold a reference past <see cref="Dispose"/>. Transactions are single-thread-affine — only the thread that created
/// one may call its members. A write-write conflict at commit is resolved by the optional <see cref="ConcurrencyConflictHandler"/> (default: last-writer-wins).
/// </remarks>
[MustDispose(Reason = "leaves its changes uncommitted, its epoch pinned and its chain node holding the cleanup cutoff")]
[PublicAPI]
[DebuggerDisplay("TSN {TSN}, State: {State}")]
public unsafe partial class Transaction : EntityAccessor
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int DeferredEnqueueBatchCapacity = 256;

    /// <summary>Lifecycle state of a <see cref="Transaction"/>. Transitions are one-way (see <see cref="Transaction.State"/>).</summary>
    public enum TransactionState
    {
        /// <summary>Default/unset value; not a live transaction.</summary>
        Invalid = 0,

        /// <summary>Created, but no operation has been performed yet.</summary>
        Created,            // New object, no operation done yet

        /// <summary>At least one operation has been added to the transaction.</summary>
        InProgress,         // At least one operation added to the transaction

        /// <summary>Rolled back explicitly by the caller or automatically during dispose.</summary>
        Rollbacked,         // Was rollbacked by the user or during dispose

        /// <summary>Committed by the caller.</summary>
        Committed           // Was committed by the user
    }

    /// <summary>Current lifecycle state of this transaction.</summary>
    public TransactionState State { get; private set; }

    private int? _committedOperationCount;
    private int _deletedComponentCount;

    // Reused across pooled Transaction lifetimes — collects deferred enqueue entries per commit/rollback
    // to flush in a single batch (one lock acquire instead of N). Never re-allocated after warmup.
    private List<DeferredCleanupManager.CleanupEntry> _deferredEnqueueBatch;

    // Hoisted accessors for batch index maintenance (set per component type during Commit)
    private ChunkAccessor<PersistentStore>[] _batchIndexAccessors;
    private bool _batchIndexActive;
    private int _batchEntityCount;

    // Lazy-cached accessors for CommitClusterVersionedSlot — persists across calls within same commit, keyed by archetype ID
    private ushort _clusterCommitArchId;
    private ChunkAccessor<PersistentStore> _clusterCommitMapAccessor;
    private ChunkAccessor<PersistentStore> _clusterCommitContentAccessor;

    /// <summary>
    /// The segment <see cref="_clusterCommitContentAccessor"/> was opened on. The other cluster-commit accessors belong to the ARCHETYPE, but content
    /// chunks belong to the COMPONENT, and one archetype can carry several Versioned components — so caching the content accessor on the archetype alone
    /// hands the second component's chunk id to the first component's segment.
    /// </summary>
    private ChunkBasedSegment<PersistentStore> _clusterCommitContentSegment;
    private ChunkAccessor<PersistentStore> _clusterCommitClusterAccessor;
    // Per-archetype index segments, cached on the same archetype key as the three above (#665). Before this the reconcile created and disposed an accessor
    // per entity PER FIELD; every field of an archetype shares one of these two segments, so all but the first were redundant. Two rather than one because
    // a segment serves exactly one node size and String64 nodes are wider — the same split ComponentTable has always had.
    private ChunkAccessor<PersistentStore> _clusterCommitIndexAccessor;
    private ChunkAccessor<PersistentStore> _clusterCommitIndexAccessorS64;
    private bool _hasClusterCommitAccessors;

    // AP-01 (Append-before-publish): the commit pipeline splits each component entry into a PREPARE half (all fallible work — conflict
    // detect/resolve, index/spatial B+Tree mutation, cluster Phase-A index updates, view notify) and a non-throwing PUBLISH half
    // (commit stamp + IsolationFlag clear, CommitSequence bump, cluster Phase-B HEAD→slot memcpy, handler-lock release). PrepareComponent
    // records one PublishEntry here; the entries are drained by PublishComponent AFTER the WAL Append, so no change is made visible before
    // its records are appended. ElementRevisionHandle is a ref struct (cannot be stored), so the handle is re-derived in publish from the
    // blittable (FirstChunkId, CurRevisionIndex). Pooled, reused across pooled Transaction lifetimes; cleared after each commit.
    private List<PublishEntry> _publishEntries;

    // The first supplies this transaction wrote (a Versioned component supplied to a published entity that had none), and those its commit claimed
    // (DatabaseEngine.FirstSupplyClaims), released after its publish or on any exit of the commit.
    private List<(long Pk, int ComponentTypeId)> _firstSupplies;
    private List<(long Pk, int ComponentTypeId)> _firstSupplyClaims;

    // AP-01 Debug guard: set true after the WAL Append phase, asserted when the PUBLISH phase begins so any future reorder that publishes before
    // appending trips loudly in Debug builds. Reset at the start of each commit (pooled Transaction reuse).
    private bool _appendPhaseEnteredThisCommit;

    // CK-13: the first LSN this transaction's commit appended, from inside its append (before the WAL frame is published) to the end of its
    // publish; 0 otherwise. The checkpoint keeps CheckpointLSN below the lowest one: those records' page effects are not in memory yet.
    private long _inFlightLsnFloor;

    // VIS-03: the commit card this commit holds (-1 none), from its reservation before PREPARE to the end of its publish; and its commit stamp, drawn at
    // the commit point (0 until then). Reset at the start of each commit (pooled Transaction reuse).
    private int _commitCardSlot = -1;
    private long _commitStamp;

    /// <summary>The first LSN this transaction's commit appended, while that commit is between its append and the end of its publish; 0 otherwise
    /// (CK-13).</summary>
    internal long InFlightLsnFloor => Volatile.Read(ref _inFlightLsnFloor);

    /// <summary>The commit stamp of this transaction's last commit (VIS-03), drawn at its commit point; 0 before one.</summary>
    internal long CommitStamp => _commitStamp;

    // Drain cursor into _publishEntries: entries [0, _publishDrainIndex) have been fully published (and their retained handler locks released). On a
    // mid-drain throw the abort path releases only [_publishDrainIndex, Count), so each retained lock is released exactly once.
    private int _publishDrainIndex;

    /// <summary>The commit's steps so far, for its pressured epoch refresh cadence (<see cref="RefreshBetweenCommitSteps"/>).</summary>
    private int _publishStepCount;

    /// <summary>
    /// Blittable per-component-entry publish descriptor recorded by <see cref="PrepareComponent"/> and consumed by <see cref="PublishComponent"/>
    /// after the WAL Append (AP-01). Carries everything publish needs to re-derive the revision handle and finish the commit without re-reading
    /// mutable chain state. <see cref="LockHeld"/> tracks whether PREPARE retained the per-entity revision-chain exclusive lock (handler path),
    /// which PUBLISH releases after clearing IsolationFlag (the detect→publish atomic region required by concurrent conflict resolution).
    /// </summary>
    private struct PublishEntry
    {
        public ComponentInfo Info;
        public int FirstChunkId;
        public int CurCompContentChunkId;
        public short CurRevisionIndex;
        public bool Created;
        public bool LockHeld;

        // The capture ticket of the hold LockHeld says PREPARE retained (PS-20): ended by whichever path releases it.
        public PagedMMF.CaptureTicket LockTicket;

        // Resolved physical location of the committing revision element, captured in PREPARE so PUBLISH reconstructs the handle without a locking walk.
        public int ElementChunkId;
        public bool ElementIsFirst;
        public short ElementIndexInChunk;

        // Cluster Phase-B coordinates resolved by PrepareClusterVersionedSlot (so publish needs no second EntityMap lookup).
        // ClusterCopyPending == true ⇒ a committed HEAD value must be copied into (ArchId, ClusterChunkId, SlotIndex)'s CompSlot.
        public bool ClusterCopyPending;

        // Phase A (#1262): IndexPending == true ⇒ this write moves one of CompSlot's index keys from the revision it read (ReadCompChunkId, 0 for none).
        // SlotIndexed ⇒ an index covers CompSlot: the publish reconciles from the newest published revision, which is not the read one when another commit
        // landed in between, so a write that left the keys as it read them still owes the move (#1263).
        public bool IndexPending;
        public bool SlotIndexed;

        // The newest committed revision PREPARE saw (ReadCompChunkId when it was the read one): what IndexPending and the unique-key check before the append
        // compare against, so a commit that landed after the read is accounted for there rather than only at the publish (#1263).
        public int BaseCompChunkId;
        public long Pk;
        public int ReadCompChunkId;
        public ushort ArchId;
        public int ClusterChunkId;
        public byte SlotIndex;
        public byte CompSlot;
    }

    /// <summary>The UoW that owns this transaction (null for legacy <c>CreateTransaction()</c> path, UoW ID effectively 0).</summary>
    internal UnitOfWork OwningUnitOfWork { get; private set; }

    /// <summary>When true, <see cref="Dispose"/> also disposes <see cref="OwningUnitOfWork"/>. Set by <c>CreateQuickTransaction()</c>.</summary>
    internal bool OwnsUnitOfWork { get; set; }

    /// <summary>When true, all write operations (Create/Update/Delete/Commit) are forbidden. No ChangeSet or UoW is allocated.</summary>
    public bool IsReadOnly { get; internal set; }

    /// <summary>UoW ID for revision stamping. 0 until UoW Registry (#51) assigns real IDs.</summary>
    internal ushort UowId => OwningUnitOfWork?.UowId ?? 0;

    /// <summary>Intrusive link to the next transaction in the engine's transaction chain. Managed by the engine; not intended for external use.</summary>
    public Transaction Next { get => _link.Next; internal set => _link.Next = value; }

    /// <summary>
    /// This transaction's node in the chain: the link to the next (older) node and the TSN as the chain's walkers read it (TXC-01), side by side. A cutoff
    /// walk reads both for every node, and a Transaction is several KiB: <see cref="EntityAccessor.TSN"/> sits 4.5 KiB from where the link would be on
    /// its own, a second cache line per node.
    /// </summary>
    private ChainLink _link;

#pragma warning disable TYPHON005 // A link to another chain node, never owned: the chain removes and disposes nothing through it.
    private struct ChainLink
    {
        public Transaction Next;

        /// <summary>0 while the transaction is in the chain but its TSN is not drawn yet (TXC-01).</summary>
        public long Tsn;
    }
#pragma warning restore TYPHON005

    /// <summary>
    /// Number of component operations this transaction carries: the sum of cached entries across every touched component type plus components deleted during
    /// rollback bookkeeping. Computed lazily on first access and cached.
    /// </summary>
    public int CommittedOperationCount
    {
        get
        {
            if (_committedOperationCount.HasValue == false)
            {
                var count = 0;
                foreach (var componentInfo in _componentInfos.Values)
                {
                    count += componentInfo.EntryCount;
                }
                _committedOperationCount = count + _deletedComponentCount;
            }
            return _committedOperationCount.Value;
        }
    }

    /// <summary>
    /// (Re)initializes this pooled transaction instance for a new lease: enters an epoch scope, binds the engine and owning unit of work, selects the commit
    /// discipline, then registers in the transaction chain, which draws the TSN that fixes the MVCC snapshot (TXC-01: in the chain first, then the TSN).
    /// Called by the engine when a transaction is created; not for direct use.
    /// </summary>
    /// <param name="dbe">Owning database engine.</param>
    /// <param name="uow">Owning unit of work, or <see langword="null"/> for the standalone path (UoW id 0).</param>
    /// <param name="readOnly">When <see langword="true"/>, no <see cref="ChangeSet"/> or UoW is allocated and all writes are forbidden.</param>
    /// <param name="discipline">Durability discipline applied to SingleVersion-layout writes for the transaction's lifetime.</param>
    /// <param name="ownChangeSet">
    /// When <see langword="true"/>, the transaction makes and releases its own <see cref="ChangeSet"/> even inside a unit of work that has a shared one: for a
    /// transaction that runs concurrently with others of the same unit of work, whose shared ChangeSet is single-thread-affine (#1116).
    /// </param>
    public void Init(DatabaseEngine dbe, UnitOfWork uow = null, bool readOnly = false, CommitDiscipline discipline = CommitDiscipline.TickFence,
        bool ownChangeSet = false)
    {
        // Residual risk: _dbe.MMF.CreateChangeSet allocates and could throw OOM in extreme conditions, dropping the span.
        // Per project policy this is acceptable for a hot per-tx path.
        // The TSN is drawn at the end, by the chain (TXC-01); the span carries it, set before the span is emitted.
        var initScope = TyphonEvent.BeginDataTransactionInit(0, uow?.UowId ?? 0);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. EnterScope/CreateChangeSet/Register are engine-internal.
        // If a future change adds a throw path, re-tag to variant B.
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        _dbe.LogTxInitPhase(0, "entering epoch");
        _ = _epochManager.EnterScope(); // Depth unused: Transaction uses ExitScopeUnordered (not LIFO)
        _dbe.LogTxInitPhase(0, "epoch entered");
        _isDisposed = false;
        IsReadOnly = readOnly;
        OwningUnitOfWork = uow;
        ChainLockWaitContextProbe = null;
        _owningThreadId = Environment.CurrentManagedThreadId;   // #422: affinity field promoted out of #if DEBUG (see EntityAccessor)
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _entityOperationCount = 0;
        // Durability discipline (SingleVersion layout only). Explicit Commit is final; TickFence (explicit or default)
        // stays open so a DefaultDiscipline=Commit component can escalate the whole tx on first touch (CM-02).
        _discipline = discipline;
        _didInPlaceSvWrite = false;
        // A unit of work in Deferred/GroupCommit mode owns a shared ChangeSet and releases it when it disposes. In
        // Immediate mode it has none, so the transaction makes its own — and must therefore release it itself, which
        // Dispose now does. Nothing did before: every mark taken by every Immediate-mode transaction was stranded for the
        // life of the process, which is a far larger leak than the per-cycle drip #824 was opened for.
        //
        // A transaction that runs beside others of its unit of work, on another thread, makes its own too (ownChangeSet): the shared one is a plain
        // Dictionary and List, and two writers corrupt it (#400, #1116). Its marks are only dirty-page accounting — the checkpoint writes the pages, never
        // the unit of work (ADR-054) — so a private ChangeSet released at dispose loses nothing, as the fence's per-chunk ChangeSets show.
        _ownsChangeSet = !readOnly && (ownChangeSet || uow?.ChangeSet == null);
        // Rented from the engine's pool and returned at dispose, so a per-chunk transaction allocates nothing in steady state.
        _changeSet = readOnly ? null : _ownsChangeSet ? _dbe.MMF.RentChangeSet() : uow.ChangeSet;
        State = TransactionState.Created;

        // TXC-01: in the chain first, its TSN pending (0), then the TSN. A cutoff computed meanwhile waits for it instead of passing this snapshot.
        _link.Tsn = 0;
        var tsn = _dbe.TransactionChain.Register(this);
        TSN = tsn;
        _dbe.LogTxInitPhase(tsn, "registered");
        initScope.Tsn = tsn;
        // PROFILING-SPAN-NO-THROW-END
        initScope.Dispose();
    }

    /// <summary>Publishes this transaction's TSN, drawn after it entered the chain (TXC-01). Pairs with <see cref="ReadPublishedTsn"/>.</summary>
    internal void PublishTsn(long tsn) => Volatile.Write(ref _link.Tsn, tsn);

    /// <summary>This transaction's TSN as another thread may read it: 0 while the creator is between its push and its draw (TXC-01).</summary>
    internal long ReadPublishedTsn() => Volatile.Read(ref _link.Tsn);

    /// <summary>Reset all state for pooling reuse. Called by TransactionChain after unlinking.</summary>
    internal void Reset() => ResetCore();

    private protected override void ResetCore()
    {
        // Clean up ECS state BEFORE nulling _dbe — CleanupEcsState needs _dbe._archetypeStates
        CleanupEcsState();

        // After CleanupEcsState, which still reads staged payloads to release ComponentCollection buffers (DC-01), and before the base reset. Rewinds only —
        // the blocks are kept for this pooled instance's next use. TransactionChain frees them when the instance is discarded rather than pooled.
        ResetSpawnArena();

        base.ResetCore();

        OwningUnitOfWork = null;
        OwnsUnitOfWork = false;
        IsReadOnly = false;
        Next = null;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _deferredEnqueueBatch?.Clear();
        _inFlightLsnFloor = 0;
        _prepareConsumed = false;
        _pendingUniqueMoves = 0;
    }

    /// <summary>
    /// Set once a commit's PREPARE has begun on its entries (#1262): from then on the transaction cannot commit again, whether its failed commit's rollback
    /// succeeded or not — PREPARE consumed each entry's read revision.
    /// </summary>
    private bool _prepareConsumed;

    /// <summary>How many of this commit's prepared Versioned writes move a key a unique index covers: <c>ValidatePublishBeforeAppend</c> checks them.</summary>
    private int _pendingUniqueMoves;

    /// <summary>
    /// <see cref="PrepareOpenMut"/> inside a profiling span — the first writable open of an <see cref="ArchetypeAccessor{TArch}"/>. Its later opens run
    /// the span-less <see cref="PrepareOpenMut"/> directly, so one body decides what "mutable" means.
    /// </summary>
    internal override void PrepareForMutation()
    {
        // Residual risk: EnsureMutable can throw on programmer-error (read-only tx mutated) — accepted per intentional-validation policy.
        var scope = TyphonEvent.BeginDataTransactionPrepare(TSN);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw on the success path. EnsureMutable is intentional validation;
        // its ThrowHelper paths terminate the operation, so dropping the span there is acceptable.
        PrepareOpenMut();
        // PROFILING-SPAN-NO-THROW-END
        scope.Dispose();
    }

    /// <summary>Throws if the transaction cannot accept new operations (read-only or already finalized).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureMutable()
    {
        if (IsReadOnly)
        {
            ThrowHelper.ThrowInvalidOp("Cannot perform write operations on a read-only transaction");
        }

        if (State > TransactionState.InProgress)
        {
            ThrowHelper.ThrowInvalidOp($"Cannot perform CRUD on a transaction in state {State}");
        }
    }

    /// <summary>Validates and performs a state transition. Illegal transitions assert in Debug.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TransitionTo(TransactionState newState)
    {
        CheckConfig.Require(CheckConfig.Enabled, IsLegalTransition(State, newState), $"Illegal transaction state transition: {State} → {newState}");
        State = newState;
    }

    private static bool IsLegalTransition(TransactionState from, TransactionState to) => (from, to) switch
    {
        (TransactionState.Created, TransactionState.InProgress) => true,
        (TransactionState.Created, TransactionState.Committed) => true,
        (TransactionState.Created, TransactionState.Rollbacked) => true,
        (TransactionState.InProgress, TransactionState.Committed) => true,
        (TransactionState.InProgress, TransactionState.Rollbacked) => true,
        _ => false,
    };

    /// <summary>
    /// Ends the transaction and returns it to the pool: auto-rolls back if it was not committed, processes any deferred cleanups it was gating, flushes its
    /// accessors, exits the epoch scope, and removes it from the transaction chain. Disposes the owning <see cref="UnitOfWork"/> when this transaction owns it
    /// (the <see cref="DatabaseEngineExtensions.CreateQuickTransaction"/> path). Idempotent.
    /// </summary>
    public override void Dispose()
    {
        // Dispose transient batch accessors first — safe even on double-dispose or if never created
        // (ChunkAccessor<PersistentStore>.Dispose() is idempotent: no-op when _segment==null).
        _clusterCommitMapAccessor.Dispose();
        _clusterCommitContentAccessor.Dispose();
        _clusterCommitClusterAccessor.Dispose();
        _clusterCommitIndexAccessor.Dispose();
        _clusterCommitIndexAccessorS64.Dispose();
        _hasClusterCommitAccessors = false;

        if (_isDisposed)
        {
            return;
        }

        AssertThreadAffinity();

        // Read-only transactions have no ChangeSet, UoW, or commit/rollback path —
        // just exit the epoch scope and remove from the chain.
        if (IsReadOnly)
        {
            // The component and cluster accessors hold a slot reference on every page cached in their slots, which no epoch releases. Reset recycles the
            // component entries without disposing them, and a transaction dropped by a full pool is never reset at all: left here, those pages could
            // never be evicted again. MarketHardeningTests' snapshot, read in short read-only transactions, left 94 % of its cache that way.
            FlushAccessors();
            DisposeArchetypeCache();

            // Before leaving the chain, not after: ComputeNextMinTSN reads the chain, and this transaction's own
            // membership is what has been holding the cutoff back. A reader that removes itself first and drains second
            // would be draining on someone else's behalf, having already lost the right to say whether it was the tail.
            // A drain that throws still leaves the epoch and the chain (#1382), as below.
            try
            {
                ProcessReadOnlyDeferredCleanups();
            }
            finally
            {
                _isDisposed = true;
                ExitEpochAndRemove();
            }

            return;
        }

        // Capture before ExitEpochAndRemove — Remove pools and Reset() nulls _dbe
        var dbe = _dbe;
        var tsn = TSN;

        // #1382: whatever the auto-rollback or the drain throws (a back-pressure timeout among them), the transaction still lets go of what it holds, and
        // the exception surfaces after. Left, its epoch pin kept every page anyone touched after it unevictable and its chain node held the cleanup
        // cutoff, for good: one transient timeout became a permanent stall. A rollback cut short leaves entries flagged by a TSN that never gets a card,
        // which every reader resolves as invisible (VIS-01).
        try
        {
            dbe.LogTxDispose(tsn, "EnsureCompleted");
            EnsureCompleted();
            dbe.LogTxDispose(tsn, "ProcessDeferredCleanups");
            ProcessDeferredCleanups();
        }
        finally
        {
            try
            {
                try
                {
                    dbe.LogTxDispose(tsn, "FlushAccessors");
                    FlushAccessors();
                    DisposeArchetypeCache();   // here, not only in Reset: a transaction dropped by a full pool is never reset
                }
                finally
                {
                    // Release the marks of a ChangeSet this transaction owns — after the flush and the deferred cleanups above, both of which may still
                    // dirty pages through it. A shared UoW ChangeSet is NOT released here: its owner does that on its own dispose, and releasing another
                    // owner's marks is the over-release that #385 was.
                    if (_ownsChangeSet && _changeSet != null)
                    {
                        _changeSet.ReleaseDirtyMarks();
                        dbe.MMF.ReturnChangeSet(_changeSet);
                        _changeSet = null;
                    }
                }
            }
            finally
            {
                // Mark disposed BEFORE ExitEpochAndRemove: Remove() pools the object, and a lock-free
                // CreateTransaction can immediately dequeue and Init it (_isDisposed = false). If we set _isDisposed = true AFTER Remove returns, we'd
                // overwrite the new owner's flag, causing their Dispose to skip Remove — leaking the chain node.
                _isDisposed = true;
                dbe.LogTxDispose(tsn, "ExitEpochAndRemove");
                ExitEpochAndRemove();
            }
        }
    }

    /// <summary>Auto-rollback if not yet committed. Phase 6: tagged with <see cref="Typhon.Profiler.TransactionRollbackReason.AutoOnDispose"/>.</summary>
    private void EnsureCompleted()
    {
        if (State != TransactionState.Committed)
        {
            var ctx = UnitOfWorkContext.None;
            Rollback(ref ctx, Typhon.Profiler.TransactionRollbackReason.AutoOnDispose);
        }
    }

    /// <summary>Process deferred cleanups if this transaction was blocking them as tail.</summary>
    /// <remarks>
    /// <para>
    /// Drains TWO independent queues against the same cutoff, and the gate must ask about both. The revision-chain GC (<see cref="DeferredCleanupManager"/>)
    /// and the ECS entity cleanup queue fill on different events: the former when a Versioned component is superseded, the latter on every destroy regardless
    /// of storage mode. Gating the whole method on the revision queue alone meant an all-SingleVersion workload — where no revision chain is ever superseded,
    /// so that queue is permanently empty — never reached the ECS drain at all, and its EntityMap tombstones accumulated for the life of the engine (#681).
    /// Measured in a demo soak: 561 796 EntityMap chunks against a live population that never exceeded ~13 900, holding 89 % of the data file.
    /// </para>
    /// <para>
    /// Both drains require <c>isTail</c>: <c>ComputeNextMinTSN</c> is only meaningful for the oldest live transaction, and it is what guarantees no surviving
    /// snapshot can still observe the records being removed.
    /// </para>
    /// </remarks>
    private void ProcessDeferredCleanups()
    {
        if (_dbe.DeferredCleanupManager.QueueSize == 0 && _dbe.EcsCleanupQueueSize == 0)
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wc))
        {
            return;
        }

        var isTail = _dbe.TransactionChain.Tail == this;
        var nextMinTSN = isTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        if (!isTail)
        {
            return;
        }

        RefreshEpochBeforeDrain();
        if (_dbe.DeferredCleanupManager.QueueSize > 0)
        {
            _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSN, _dbe, _changeSet);
        }

        // After the revision GC, not before: that pass may trim chains belonging to entities this one is about to unmap, and running it first means the chain
        // roots this reads from the entity record have already settled.
        DrainEcsCleanups(nextMinTSN);
    }

    /// <summary>
    /// Drains the ECS entity cleanup queue up to <paramref name="nextMinTSN"/>. Caller must have established that this transaction is the tail.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ProcessDeferredCleanups"/> because a READ-ONLY transaction never reaches that method — <see cref="Dispose"/> short-circuits
    /// read-only disposal before it — yet a read-only transaction is exactly the one that most often holds this queue back. A long reader opened before a burst
    /// of destroys keeps <c>ComputeNextMinTSN</c> behind them, so its retirement is what makes those records reclaimable; if it retires without draining, the
    /// queue waits for the next writer, which in a read-mostly steady state can be a long time.
    /// <para>
    /// A read-only transaction has no ChangeSet (<c>_changeSet = readOnly ? null : …</c>), so one is created here. It releases exactly the marks it took and
    /// deliberately does NOT <c>SaveChanges</c>: the release is what PS-05 requires of a locally-created set, while the pages keep the writeback debt
    /// <c>IncrementDirty</c> recorded and the next checkpoint collects them (PS-10). Saving here instead would put synchronous page I/O on a dispose path.
    /// </para>
    /// </remarks>
    private void DrainEcsCleanups(long nextMinTSN)
    {
        if (_dbe.EcsCleanupQueueSize == 0)
        {
            return;
        }

        if (_changeSet != null)
        {
            _dbe.ProcessEcsCleanups(nextMinTSN, _changeSet);
            return;
        }

        var drainSet = _dbe.MMF.CreateChangeSet(drainedByCheckpoint: true);
        try
        {
            _dbe.ProcessEcsCleanups(nextMinTSN, drainSet);
        }
        finally
        {
            drainSet.ReleaseDirtyMarks();
        }
    }

    /// <summary>
    /// The read-only disposal counterpart: establish tail-ness, then drain the ECS queue.
    /// </summary>
    /// <remarks>
    /// Only the ECS queue, deliberately. The revision-chain GC takes this transaction's ChangeSet, which a read-only transaction does not have, and extending
    /// that path is a separate change with its own analysis — this one is scoped to #681.
    /// </remarks>
    private void ProcessReadOnlyDeferredCleanups()
    {
        if (_dbe.EcsCleanupQueueSize == 0)
        {
            return;
        }

        var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
        if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wc))
        {
            return;
        }

        var isTail = _dbe.TransactionChain.Tail == this;
        var nextMinTSN = isTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
        _dbe.TransactionChain.Control.ExitSharedAccess();

        if (isTail)
        {
            RefreshEpochBeforeDrain();
            DrainEcsCleanups(nextMinTSN);
        }
    }

    /// <summary>
    /// The tail drains every transaction's queued cleanups, and as the tail its epoch is the oldest pinned (#1230): a back-pressure wait in the drain held
    /// every page any thread had touched since this transaction began — 5,000 epochs of them on a 64 MiB market — until all of them timed out together.
    /// The drain starts at a call boundary (EP-03): this transaction's reads are done, its accessors pin what they cache, so its epoch is refreshed first.
    /// Like the per-operation refresh, it releases the dirty marks of this transaction's ChangeSet — a shared UoW one too: the release is exact, the drain
    /// marks what it dirties again, and the owner releases the rest on its own dispose.
    /// </summary>
    private void RefreshEpochBeforeDrain()
    {
        if (LiveIndexCursors == 0)
        {
            FlushAndRefreshEpoch();
        }
    }

    /// <summary>Exit epoch scope, remove from chain, auto-dispose owned UoW.</summary>
    private void ExitEpochAndRemove()
    {
        // Capture before Remove() — Remove pools the transaction and Reset() nulls _dbe
        var dbe = _dbe;
        var tsn = TSN;

        dbe.LogTxDispose(tsn, "ExitEpoch");
        _epochManager.ExitScopeUnordered();
        var owningUow = OwnsUnitOfWork ? OwningUnitOfWork : null;
        dbe.LogTxDispose(tsn, "ChainRemove");
        // The pool's Reset unplaces a failed commit's spawns, which loads pages and can throw: the unit of work is disposed all the same, or its marks and
        // its registry slot were held for good (#1382).
        try
        {
            _dbe.TransactionChain.Remove(this);
        }
        finally
        {
            if (owningUow != null)
            {
                dbe.LogTxDispose(tsn, "UoWDispose");
                owningUow.Dispose();
            }
        }

        dbe.LogTxDispose(tsn, "ExitEpochAndRemoveDone");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component Collections & Enumerators
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a mutable accessor over a <see cref="ComponentCollection{T}"/> field of a component value that is not stored yet — one being built for a
    /// spawn — or to read one back; bound to this transaction's <see cref="ChangeSet"/> so edits are tracked for commit/rollback.
    /// </summary>
    /// <remarks>
    /// To change the collection of a component an entity already holds, use <see cref="EntityRefMut.CreateComponentCollectionAccessor{T, TElem}"/>: on a
    /// Versioned component it creates the new revision first, so the buffer the committed revision points to is cloned rather than edited in place.
    /// Through this method, a copy read from a Versioned component would edit that buffer in place (#1199).
    /// </remarks>
    /// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
    /// <param name="field">Reference to the collection field to wrap.</param>
    /// <returns>A mutable accessor over the collection's backing buffer.</returns>
    public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(ref ComponentCollection<T> field) where T : unmanaged =>
        CreateComponentCollectionAccessorCore(ref field);

    /// <summary>
    /// Returns a read-only enumerator that streams the elements of a <see cref="ComponentCollection{T}"/> field without allocating.
    /// </summary>
    /// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
    /// <param name="field">Reference to the collection field to enumerate.</param>
    /// <returns>A read-only, zero-copy enumerator over the collection.</returns>
    public ReadOnlyCollectionEnumerator<T> GetReadOnlyCollectionEnumerator<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ReadOnlyCollectionEnumerator<T>(_dbe.GetComponentCollectionVSBS<T>(), field._bufferId);
    }

    /// <summary>
    /// Returns the reference count of the buffer backing a <see cref="ComponentCollection{T}"/> field (number of entities currently sharing that buffer).
    /// </summary>
    /// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
    /// <param name="field">Reference to the collection field to inspect.</param>
    /// <returns>The backing buffer's reference count.</returns>
    public int GetComponentCollectionRefCounter<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        var vsbs = _dbe.GetComponentCollectionVSBS<T>();
        using var a = new VariableSizedBufferAccessor<T, PersistentStore>(vsbs, field._bufferId);

        return a.RefCounter;
    }
    
    /// <summary>Zero-copy, read-only <c>foreach</c> enumerator over the elements of a component collection buffer.</summary>
    /// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
    [MustDispose(Reason = "owns a BufferEnumerator ('_enumerator'): until disposed its pages stay pinned and registered as written, and the checkpoint cannot cover them (CP-13)")]
    [NoCopy(Reason = "holds a BufferEnumerator by value ('_enumerator'): a copy duplicates its slots and pins")]
    [PublicAPI]
    public ref struct ReadOnlyCollectionEnumerator<T> where T : unmanaged
    {
        private BufferEnumerator<T, PersistentStore> _enumerator;

        /// <summary>Creates the enumerator over the buffer identified by <paramref name="bufferId"/> within <paramref name="vsbs"/>.</summary>
        /// <param name="vsbs">Segment holding the collection's variable-sized buffers.</param>
        /// <param name="bufferId">Root chunk id of the buffer to enumerate.</param>
        public ReadOnlyCollectionEnumerator(VariableSizedBufferSegment<T, PersistentStore> vsbs, int bufferId)
        {
            _enumerator = vsbs.EnumerateBuffer(bufferId);
        }

        /// <summary>Returns this enumerator (enables the <c>foreach</c> pattern).</summary>
        [AllowCopy]   // foreach takes this enumerator: the copy is the one it walks and disposes
        public ReadOnlyCollectionEnumerator<T> GetEnumerator() => this;

        /// <summary>Zero-copy reference to the current element. Valid until the next <see cref="MoveNext"/> call.</summary>
        public ref readonly T Current
        {
            get => ref _enumerator.Current;
        }

        /// <summary>Advances to the next element; returns <see langword="false"/> when the buffer is exhausted.</summary>
        public bool MoveNext() => _enumerator.MoveNext();

        /// <summary>Releases the underlying buffer enumerator (unpins accessed pages).</summary>
        public void Dispose() => _enumerator.Dispose();
    }

    /// <summary>
    /// Returns an enumerator that streams entities via a secondary index in key order, at this transaction's snapshot: exactly the entities its field
    /// query over the range finds, at the keys and values the snapshot holds — its own destroys gone, its own spawns in, its own value writes unseen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>At the snapshot (#1247, VIS-13).</b> The trees hold the newest committed keys, and a destroy takes its entries out at its publish; the entities a
    /// commit newer than the snapshot changed are read at the snapshot beside them (<c>IndexSnapshotSide</c>), their tree rows dropped and their rows merged
    /// in key order. With nothing newer than the snapshot and no spawn or destroy of its own, that costs two loads per archetype.
    /// </para>
    /// <para>
    /// <b>Fans out across every archetype holding the component (#666).</b> Indexes live per-archetype, so a component-keyed <see cref="IndexRef"/> names K
    /// trees rather than one. Fanning out is what preserves the semantics the single shared tree used to give — the alternative, making the handle name one
    /// archetype, would silently narrow every existing caller's result set. ADR-045 §2 accepted the K-way merge when it chose per-archetype trees.
    /// </para>
    /// <para>
    /// <b>Index entries are materialised; entity resolution stays lazy.</b> The B+Tree range cursors are <c>ref struct</c>s, so K of them cannot be held side
    /// by side for a streaming merge — the language has no way to store them. Each tree's <c>(key, location)</c> pairs are therefore drained into one pooled
    /// buffer and sorted once, which costs 12 bytes per matching index entry. The expensive half — chain walks, page faults, component reads — remains
    /// per-<see cref="IndexEntityEnumerator{T,TKey}.MoveNext"/>, so this bounds memory by the number of index entries in range, not by the data behind them.
    /// Stated rather than hidden: for K == 1 the buffer holds exactly what a single-tree scan would have walked anyway.
    /// </para>
    /// <para>
    /// Transient components are not reachable here and never were: their trees are <c>BTree&lt;TKey, TransientStore&gt;</c>, which fails the cast below
    /// exactly as it did against the shared home.
    /// </para>
    /// </remarks>
    public IndexEntityEnumerator<T, TKey> EnumerateIndex<T, TKey>(IndexRef indexRef, TKey minKey, TKey maxKey) where T : unmanaged where TKey : unmanaged
    {
        AssertThreadAffinity();
        indexRef.Validate();

        var ct = indexRef.Table;

        if (indexRef.IsPrimaryKey)
        {
            throw new InvalidOperationException("PK B+Tree has been removed. Use ECS queries with EntityMap instead.");
        }

        var fieldIndex = indexRef.FieldIndex;

        // The first match is held in locals and the lists exist only once a second archetype matches: one archetype holding the component is the common
        // case, and a point lookup in a loop paid ~3 KB of lists, iterator and accessor arrays per call for it (#1514).
        IndexArchetypeSource firstSource = null;
        BTree<TKey, PersistentStore> firstTree = null;
        var firstMulti = false;
        List<IndexArchetypeSource> sources = null;
        List<BTree<TKey, PersistentStore>> trees = null;
        List<bool> multi = null;

        // The registry's array up to the highest id rather than its GetAllArchetypes iterator, which allocates.
        var maxArchetypeId = ArchetypeRegistry.MaxArchetypeId;
        for (var archetypeId = 0; archetypeId <= maxArchetypeId; archetypeId++)
        {
            var meta = ArchetypeRegistry.GetMetadata((ushort)archetypeId);
            if (meta == null)
            {
                continue;
            }

            var es = _dbe._archetypeStates[meta.ArchetypeId];
            var clusterState = es?.ClusterState;
            if (clusterState?.IndexSlots == null)
            {
                continue;
            }

            // The archetype's slot for THIS component table. A component type can sit at a different slot in each archetype, and the index slots are keyed on
            // the archetype's own slot numbering, so the table identity is the only reliable join.
            var compSlot = -1;
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (ReferenceEquals(es.SlotToComponentTable[slot], ct))
                {
                    compSlot = slot;
                    break;
                }
            }

            if (compSlot < 0)
            {
                continue;
            }

            var ixSlotIdx = -1;
            for (var s = 0; s < clusterState.IndexSlots.Length; s++)
            {
                if (clusterState.IndexSlots[s].Fields != null && clusterState.IndexSlots[s].Slot == compSlot)
                {
                    ixSlotIdx = s;
                    break;
                }
            }

            if (ixSlotIdx < 0 || fieldIndex >= clusterState.IndexSlots[ixSlotIdx].Fields.Length)
            {
                continue;
            }

            ref var field = ref clusterState.IndexSlots[ixSlotIdx].Fields[fieldIndex];
            if (field.Index is not BTree<TKey, PersistentStore> typedIndex)
            {
                throw new InvalidOperationException(
                    $"TKey type mismatch: index uses '{field.Index.GetType().GenericTypeArguments[0].Name}', caller specified '{typeof(TKey).Name}'.");
            }

            var source = IndexSourceOf(clusterState, es, meta, compSlot, ixSlotIdx, fieldIndex, field.FieldOffset);
            if (firstSource == null)
            {
                firstSource = source;
                firstTree = typedIndex;
                firstMulti = field.AllowMultiple;
                continue;
            }

            if (sources == null)
            {
                sources = [firstSource];
                trees = [firstTree];
                multi = [firstMulti];
            }

            sources.Add(source);
            trees.Add(typedIndex);
            multi.Add(field.AllowMultiple);
        }

        if (firstSource == null)
        {
            return new IndexEntityEnumerator<T, TKey>([], [], ct, this, _changeSet);
        }

        // ONE archetype holds this component — the common case, and the one an early-terminating caller depends on. Stream straight off its cursor and
        // materialise nothing: a caller that breaks after N entries must pay for N entries, not for the whole range. Draining up front turns
        // EnumerateIndex(startKey, MaxValue) + break-at-100 from O(100) into O(everything at or after startKey), which is what a YCSB-style scan does.
        if (sources == null)
        {
            var streaming = new IndexEntityEnumerator<T, TKey>(firstTree, firstMulti, minKey, maxKey, firstSource, ct, this, _changeSet);
            try
            {
                streaming.AttachSnapshotSide(minKey, maxKey);
            }
            catch
            {
                streaming.Dispose();   // its accessors and cursor (EP-02)
                throw;
            }

            return streaming;
        }

        // Genuine fan-out: K cursors cannot be held side by side (they are ref structs), so the entries are merged eagerly. Early termination degrades here
        // and that is a known limit of the multi-archetype path, recorded rather than hidden.
        var runs = new List<List<IndexMergeEntry<TKey>>>(trees.Count);
        for (var i = 0; i < trees.Count; i++)
        {
            var run = new List<IndexMergeEntry<TKey>>();
            CollectIndexEntries(trees[i], multi[i], minKey, maxKey, i, run);
            runs.Add(run);
        }

        // The trees are read; the side after them (#1247): a change folds its stamps before it touches a tree, so this read sees every one the reads missed.
        var merged = new IndexEntityEnumerator<T, TKey>(MergeRuns(runs), sources.ToArray(), ct, this, _changeSet);
        try
        {
            merged.AttachSnapshotSide(minKey, maxKey);
        }
        catch
        {
            merged.Dispose();
            throw;
        }

        return merged;
    }

    /// <summary>
    /// The source descriptor for one indexed field of one archetype, built on first use and kept on the archetype's cluster state (#1514).
    /// </summary>
    /// <remarks>
    /// Everything it holds is fixed for the cluster state's life: the layout, the record size and the field's offset are set when the state is built, and
    /// <see cref="ArchetypeClusterState.InitializeIndexes"/> drops the cache whenever it rebuilds the index slots. Two threads racing on a cold entry each
    /// build an identical descriptor and the last one published wins; the release/acquire pair is what lets a reader on arm64 see its fields.
    /// </remarks>
    private static IndexArchetypeSource IndexSourceOf(ArchetypeClusterState clusterState, ArchetypeEngineState es, ArchetypeMetadata meta, int compSlot,
        int ixSlotIdx, int fieldIndex, int fieldOffset)
    {
        var perSlot = Volatile.Read(ref clusterState.IndexEnumerationSources);
        if (perSlot == null)
        {
            perSlot = new IndexArchetypeSource[clusterState.IndexSlots.Length][];
            perSlot = Interlocked.CompareExchange(ref clusterState.IndexEnumerationSources, perSlot, null) ?? perSlot;
        }

        var row = Volatile.Read(ref perSlot[ixSlotIdx]);
        if (row == null)
        {
            row = new IndexArchetypeSource[clusterState.IndexSlots[ixSlotIdx].Fields.Length];
            row = Interlocked.CompareExchange(ref perSlot[ixSlotIdx], row, null) ?? row;
        }

        var source = Volatile.Read(ref row[fieldIndex]);
        if (source != null)
        {
            return source;
        }

        var layout = clusterState.Layout;
        source = new IndexArchetypeSource
        {
            Meta = meta,
            CompSlot = compSlot,
            ClusterState = clusterState,
            EngineState = es,
            RecordSize = meta._entityRecordSize,
            CompOffset = layout.ComponentOffset(compSlot),
            CompSize = layout.ComponentSize(compSlot),
            FieldOffset = fieldOffset,
            VersionedIndex = layout.SlotToVersionedIndex != null ? layout.SlotToVersionedIndex[compSlot] : -1,
        };
        source.Alone = [source];
        Volatile.Write(ref row[fieldIndex], source);
        return source;
    }

    /// <summary>
    /// Merges K already-ordered per-archetype runs into one ascending sequence.
    /// </summary>
    /// <remarks>
    /// A B+Tree range scan emits its keys in order, so each run arrives sorted and the combined order is a K-way MERGE — linear in the total, with one
    /// comparison per output element. Concatenating and calling <see cref="Array.Sort{T}(T[], Comparison{T})"/> instead throws that ordering away and pays
    /// O(n log n) delegate-dispatched comparisons to rediscover it; measured at 40 000 entries that mistake cost 8.1 ms of pure set-up before a single entity
    /// was read. The single-run case — one archetype holds the component, which is the common one — returns the run untouched and does no merging at all.
    /// </remarks>
    private static IndexMergeEntry<TKey>[] MergeRuns<TKey>(List<List<IndexMergeEntry<TKey>>> runs) where TKey : unmanaged
    {
        if (runs.Count == 0)
        {
            return [];
        }

        if (runs.Count == 1)
        {
            return runs[0].ToArray();
        }

        var total = 0;
        for (var r = 0; r < runs.Count; r++)
        {
            total += runs[r].Count;
        }

        var result = new IndexMergeEntry<TKey>[total];
        var cursors = new int[runs.Count];
        var comparer = Comparer<TKey>.Default;

        for (var outIdx = 0; outIdx < total; outIdx++)
        {
            var best = -1;
            for (var r = 0; r < runs.Count; r++)
            {
                if (cursors[r] >= runs[r].Count)
                {
                    continue;
                }

                // Ties break on source order so a repeated run over an unchanged index yields the same sequence.
                if (best < 0 || comparer.Compare(runs[r][cursors[r]].Key, runs[best][cursors[best]].Key) < 0)
                {
                    best = r;
                }
            }

            result[outIdx] = runs[best][cursors[best]++];
        }

        return result;
    }

    /// <summary>Drains one archetype's B+Tree over <paramref name="minKey"/>..<paramref name="maxKey"/> into <paramref name="sink"/>.</summary>
    /// <remarks>
    /// Separated so the <c>ref struct</c> range cursors live and die inside one stack frame. That is the whole reason the merge materialises: two of these
    /// cannot be alive in the same object at the same time.
    /// </remarks>
    private static void CollectIndexEntries<TKey>(BTree<TKey, PersistentStore> index, bool allowMultiple, TKey minKey, TKey maxKey, int sourceIndex,
        List<IndexMergeEntry<TKey>> sink) where TKey : unmanaged
    {
        if (allowMultiple)
        {
            var e = index.EnumerateRangeMultiple(minKey, maxKey);
            try
            {
                while (e.MoveNextKey())
                {
                    var key = e.CurrentKey;
                    do
                    {
                        var values = e.CurrentValues;
                        for (var i = 0; i < values.Length; i++)
                        {
                            sink.Add(new IndexMergeEntry<TKey> { Key = key, Location = values[i], SourceIndex = sourceIndex });
                        }
                    }
                    while (e.NextChunk());
                }
            }
            finally
            {
                e.Dispose();
            }

            return;
        }

        var u = index.EnumerateRange(minKey, maxKey);
        try
        {
            while (u.MoveNext())
            {
                var kv = u.Current;
                sink.Add(new IndexMergeEntry<TKey> { Key = kv.Key, Location = kv.Value, SourceIndex = sourceIndex });
            }
        }
        finally
        {
            u.Dispose();
        }
    }

    /// <summary>One index entry pulled from an archetype's tree, carrying the source it came from so resolution can find its cluster again.</summary>
    internal struct IndexMergeEntry<TKey> where TKey : unmanaged
    {
        public TKey Key;

        /// <summary>Packed <c>ClusterLocation</c> — <c>clusterChunkId * 64 + slotIndex</c>.</summary>
        public int Location;

        public int SourceIndex;
    }

    /// <summary>Everything needed to turn a <c>ClusterLocation</c> from one archetype's index back into an entity and its component bytes.</summary>
    internal sealed class IndexArchetypeSource
    {
        public ArchetypeMetadata Meta;

        /// <summary>The indexed component's slot in this archetype.</summary>
        public int CompSlot;

        public ArchetypeClusterState ClusterState;
        public ArchetypeEngineState EngineState;
        public int RecordSize;
        public int CompOffset;
        public int CompSize;

        /// <summary>The indexed field's offset in the component: a SingleVersion slot's key is re-checked there (IXS-10).</summary>
        public int FieldOffset;

        /// <summary>Position among the archetype's Versioned slots, or -1 when this component is SingleVersion here.</summary>
        public int VersionedIndex;

        /// <summary>A one-element array holding this source: the streaming enumerator's source list, cached with it so a lookup allocates none (#1514).</summary>
        public IndexArchetypeSource[] Alone;
    }

    /// <summary>
    /// MVCC-correct streaming enumerator over the per-archetype index B+Trees backing one component.
    /// Use <see cref="CurrentComponent"/> for zero-copy ref access into page memory, or <see cref="Current"/> for a convenience copy.
    /// Supports both unique and AllowMultiple indexes.
    /// </summary>
    [MustDispose(Reason = "owns a ChunkAccessor ('_clusterAccessor0'): until disposed its pages stay pinned and registered as written, and the checkpoint cannot cover them (CP-13)")]
    [NoCopy(Reason = "holds a ChunkAccessor by value ('_clusterAccessor0'): a copy duplicates its slots and pins")]
    [PublicAPI]
    public ref struct IndexEntityEnumerator<T, TKey> where T : unmanaged where TKey : unmanaged
    {
        private readonly IndexMergeEntry<TKey>[] _entries;
        private readonly IndexArchetypeSource[] _sources;
        private readonly ChunkAccessor<PersistentStore>[] _clusterAccessors;
        private readonly ChunkAccessor<PersistentStore>[] _mapAccessors;

        // One archetype (the streaming path): its accessors live here and the two arrays above stay null. Each accessor carries a 32-page window, so the
        // two one-element arrays they replace were most of what a point lookup allocated (#1514).
        private ChunkAccessor<PersistentStore> _clusterAccessor0;
        private ChunkAccessor<PersistentStore> _mapAccessor0;
        private int _entryIndex;

        /// <summary>Largest EntityMap record across the sources — the one buffer size that serves every archetype this enumerator spans.</summary>
        private readonly int _maxRecordSize;

        // Streaming mode (single archetype): one live B+Tree cursor, nothing materialised. A ref struct can hold ONE of these; it is holding K of them that
        // the language forbids, which is why the fan-out path materialises instead.
        private readonly bool _streaming;
        private readonly bool _isAllowMultiple;
        private BTree<TKey, PersistentStore>.RangeEnumerator _innerUnique;
        private BTree<TKey, PersistentStore>.RangeMultipleEnumerator _innerMultiple;
        private ReadOnlySpan<int> _currentValues;
        private int _currentValueIndex;

        // The current key's values, copied out of its buffer before any of them is resolved (IXS-10). Pooled; returned by Dispose.
        private int[] _keyValues;

        /// <summary>Test seam (IXS-10): runs after a key's values are copied and its buffer released, before any is resolved. Null in production.</summary>
        [ThreadStatic]
        internal static Action KeyCopiedProbe;

        private ChunkAccessor<PersistentStore> _compRevAccessor;
        private ChunkAccessor<PersistentStore> _compContentAccessor;

        // Snapshot (#1247): the entities the trees cannot give at the snapshot, merged in key order, and the tree row read and not yet merged.
        private IndexSnapshotSide<TKey> _side;
        private TKey _minKey;
        private TKey _maxKey;
        private ArchetypeClusterState _watch;   // streaming with no side yet: the archetype whose KeyChangeCount a change during the walk moves
        private long _watchMark;
        private bool _hasPending;
        private TKey _pendingKey;
        private int _pendingLocation;
        private int _pendingSource;
        private bool _treeDone;
        private bool _hasLastTreeKey;
        private TKey _lastTreeKey;

        private readonly Transaction _tx;
        private readonly long _transactionTSN;
        private readonly int _componentOverhead;
        private int _entityCount;
        private long _currentPK;
        private TKey _currentKey;

        // The AllowMultiple cursor's key, whose values are being returned: its own, since a side row merged between two of them sets _currentKey (#1247).
        private TKey _cursorKey;
        private ReadOnlySpan<byte> _currentComponentSpan;
        private bool _disposed;

        /// <summary>Streaming constructor — one archetype, one cursor, no materialisation.</summary>
        internal IndexEntityEnumerator(BTree<TKey, PersistentStore> index, bool allowMultiple, TKey minKey, TKey maxKey, IndexArchetypeSource source,
            ComponentTable ct, Transaction tx, ChangeSet changeSet) : this([], source.Alone, ct, tx, changeSet)
        {
            _streaming = true;
            _isAllowMultiple = allowMultiple;
            if (allowMultiple)
            {
                _innerMultiple = index.EnumerateRangeMultiple(minKey, maxKey);
            }
            else
            {
                _innerUnique = index.EnumerateRange(minKey, maxKey);
            }
        }

        internal IndexEntityEnumerator(IndexMergeEntry<TKey>[] entries, IndexArchetypeSource[] sources, ComponentTable ct, Transaction tx, ChangeSet changeSet)
        {
            _entries = entries;
            _sources = sources;
            _entryIndex = 0;
            _streaming = false;
            _isAllowMultiple = false;
            _innerUnique = default;
            _innerMultiple = default;
            _currentValues = default;
            _currentValueIndex = 0;
            _keyValues = [];

            if (sources.Length == 1)
            {
                _clusterAccessors = null;
                _mapAccessors = null;
                _clusterAccessor0 = sources[0].ClusterState.ClusterSegment.CreateChunkAccessor(changeSet);
                _mapAccessor0 = sources[0].EngineState.EntityMap.Segment.CreateChunkAccessor(changeSet);
            }
            else
            {
                _clusterAccessor0 = default;
                _mapAccessor0 = default;
                _clusterAccessors = new ChunkAccessor<PersistentStore>[sources.Length];
                _mapAccessors = new ChunkAccessor<PersistentStore>[sources.Length];
                for (var i = 0; i < sources.Length; i++)
                {
                    _clusterAccessors[i] = sources[i].ClusterState.ClusterSegment.CreateChunkAccessor(changeSet);
                    _mapAccessors[i] = sources[i].EngineState.EntityMap.Segment.CreateChunkAccessor(changeSet);
                }
            }

            var maxRecord = 0;
            for (var i = 0; i < sources.Length; i++)
            {
                if (sources[i].RecordSize > maxRecord)
                {
                    maxRecord = sources[i].RecordSize;
                }
            }

            _maxRecordSize = maxRecord;

            // A SingleVersion component has no revision chains and no content segment: its bytes are the cluster slot. The accessors stay default,
            // which MoveNext never touches for it and Dispose skips; created unconditionally, an index read of such a component threw here.
            _compRevAccessor = ct.CompRevTableSegment != null ? ct.CompRevTableSegment.CreateChunkAccessor(changeSet) : default;
            _compContentAccessor = ct.ComponentSegment != null ? ct.ComponentSegment.CreateChunkAccessor(changeSet) : default;
            _tx = tx;
            _transactionTSN = tx.TSN;
            _componentOverhead = ct.ComponentOverhead;
            _entityCount = 0;
            _currentPK = 0;
            _currentKey = default;
            _currentComponentSpan = default;
            _disposed = false;
        }

        /// <summary>
        /// Builds the part of the answer the trees cannot give at the snapshot (<see cref="IndexSnapshotSide{TKey}"/>), or — streaming, with nothing to give
        /// yet — reads the marks a change during the walk would move.
        /// </summary>
        internal void AttachSnapshotSide(TKey minKey, TKey maxKey)
        {
            _minKey = minKey;
            _maxKey = maxKey;
            // The mark is read inside Create, before its stamps: a change after it is one the next look at the count finds.
            _side = IndexSnapshotSide<TKey>.Create(_tx, _sources, minKey, maxKey, out var mark);
            if (_side == null && _streaming && _sources[0].Meta.VersionedSlotMask != 0)
            {
                _watch = _sources[0].ClusterState;
                _watchMark = mark;
            }
        }

        /// <summary>Returns this enumerator (enables the <c>foreach</c> pattern).</summary>
        [AllowCopy]   // foreach takes this enumerator: the copy is the one it walks and disposes
        public IndexEntityEnumerator<T, TKey> GetEnumerator() => this;

        /// <summary>Convenience accessor — copies the component into a tuple. Prefer <see cref="CurrentComponent"/> for zero-copy.</summary>
        public (long EntityPK, TKey Key, T Component) Current => (_currentPK, _currentKey, MemoryMarshal.AsRef<T>(_currentComponentSpan));

        /// <summary>Zero-copy ref into the epoch-protected page memory. Valid until the next <see cref="MoveNext"/> call.</summary>
        public ref readonly T CurrentComponent => ref MemoryMarshal.AsRef<T>(_currentComponentSpan);

        /// <summary>The primary key of the current entity.</summary>
        public long CurrentEntityPK => _currentPK;

        /// <summary>The index key of the current entry.</summary>
        public TKey CurrentKey => _currentKey;

        /// <summary>
        /// Advances to the next index entry visible at this transaction's snapshot (skipping entries hidden by MVCC), positioning <see cref="Current"/> /
        /// <see cref="CurrentComponent"/>. Returns <see langword="false"/> when the range is exhausted.
        /// </summary>
        /// <remarks>
        /// Two resolutions, chosen by how the component is stored in THIS archetype. A SingleVersion slot's bytes are the cluster slot itself, so the span
        /// points straight into the SoA. A Versioned slot's cluster bytes are only the HEAD; a transaction reading at an older TSN must go through the chain,
        /// so the entity's record supplies the chain root and <c>RevisionChainReader.WalkChain</c> picks the visible revision. Reading the SoA for a
        /// Versioned slot would return the newest value to every snapshot — visible only as a subtly wrong read, never as an error.
        /// </remarks>
        public bool MoveNext()
        {
            // Hoisted out of the loop DELIBERATELY. `stackalloc` inside the body allocates on EVERY iteration and the stack pointer is not restored
            // until the method returns, so one MoveNext that skips n entries — freed slots, tombstones, revisions invisible at this snapshot — grows
            // the frame by n * RecordSize. Over a mostly-invisible range that is unbounded, and a stack overflow cannot be caught: it takes the
            // process down instead of surfacing as an error. One buffer per call, sized for the largest archetype, costs nothing and cannot grow.
            var recordBuf = stackalloc byte[_maxRecordSize];

            var comparer = Comparer<TKey>.Default;
            while (true)
            {
                if (!_hasPending && !_treeDone)
                {
                    var read = TryNextEntry(out var key, out var location, out var sourceIndex, out var keyRead);
                    if (read)
                    {
                        _hasPending = true;
                        _pendingKey = key;
                        _pendingLocation = location;
                        _pendingSource = sourceIndex;
                    }
                    else
                    {
                        _treeDone = true;
                    }

                    // A key's entries were just read: a change published since the last read is read now, before any row of that key is merged.
                    if (_streaming && (keyRead || !read) && (_side != null || _watch != null))
                    {
                        CheckChangesSinceLastRead(read);
                    }
                }

                if (_side != null && _side.TryPeek(out var sideKey) && (!_hasPending || comparer.Compare(sideKey, _pendingKey) <= 0))
                {
                    _currentComponentSpan = _side.Take(out _currentPK, out _currentKey);
                    CountEmitted();
                    return true;
                }

                if (!_hasPending)
                {
                    return false;
                }

                _hasPending = false;
                if (TryResolve(_pendingKey, _pendingLocation, _pendingSource, recordBuf))
                {
                    CountEmitted();
                    return true;
                }
            }
        }

        /// <summary>EP-03: every EpochRefreshInterval entities, or every PressuredEpochRefreshInterval while the cache is under pressure (#1230).</summary>
        private void CountEmitted()
        {
            if ((++_entityCount & (PressuredEpochRefreshInterval - 1)) == 0
                && ((_entityCount & (EpochRefreshInterval - 1)) == 0 || _tx.DBE.MMF.UnderPressure))
            {
                _tx.EnumerateRefreshEpoch();
            }
        }

        /// <summary>
        /// A streaming walk, after each key read (<paramref name="read"/>) or its end, before any row of that key is merged: a destroy or key move published
        /// since the last look — the archetype's <see cref="ArchetypeClusterState.KeyChangeCount"/> moved — is read at the snapshot, its entity dropped from
        /// the trees from here on and merged at its snapshot key when that key is above the PREVIOUS key read. At or below it, the entity was read there,
        /// unchanged; between the two its entry may have left the stretch the cursor just crossed (<see cref="IndexSnapshotSide{TKey}"/>).
        /// </summary>
        private void CheckChangesSinceLastRead(bool read)
        {
            var hasFloor = _hasLastTreeKey;
            var floor = _lastTreeKey;
            if (read)
            {
                _hasLastTreeKey = true;
                _lastTreeKey = _pendingKey;
            }

            if (_side != null)
            {
                if (_side.NeedsRefresh())
                {
                    _side.Refresh(hasFloor, floor);
                }

                return;
            }

            if (_watch.KeyChangeCount == _watchMark)
            {
                return;
            }

            _side = IndexSnapshotSide<TKey>.Create(_tx, _sources, _minKey, _maxKey, out var mark, hasFloor, floor);
            if (_side != null)
            {
                _watch = null;
            }
            else
            {
                _watchMark = mark;
            }
        }

        /// <summary>
        /// One tree row at the snapshot: its entity, unless the snapshot side reads it; a slot hidden by a destroy newer than the snapshot, or not yet
        /// published, decided by its record; a SingleVersion component of an archetype that promises isolation gated on born and died, a Versioned one
        /// read through its chain.
        /// </summary>
        /// <remarks>
        /// Two resolutions, chosen by how the component is stored in THIS archetype. A SingleVersion slot's bytes are the cluster slot itself, so the span
        /// points straight into the SoA. A Versioned slot's cluster bytes are only the HEAD; a transaction reading at an older TSN must go through the chain,
        /// so the entity's record supplies the chain root and <c>RevisionChainReader.WalkChain</c> picks the visible revision. Reading the SoA for a
        /// Versioned slot would return the newest value to every snapshot — visible only as a subtly wrong read, never as an error.
        /// </remarks>
        private bool TryResolve(TKey key, int location, int sourceIndex, byte* recordBuf)
        {
            var src = _sources[sourceIndex];
            var clusterChunkId = location >> 6;
            var slotIndex = location & 63;
            var clusterBase = ClusterAccessor(sourceIndex).GetChunkAddress(clusterChunkId);
            var layout = src.ClusterState.Layout;
            var isolated = src.Meta.VersionedSlotMask != 0;
            var occupied = (Volatile.Read(ref *(ulong*)clusterBase) & (1UL << slotIndex)) != 0;
            var entityRaw = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
            if (entityRaw == 0 || (_side != null && _side.IsDropped(entityRaw)))
            {
                return false;
            }

            if (!occupied)
            {
                // Freed — a destroyed entity, not a visible one — unless the archetype promises isolation and the slot is claimed: a destroy newer than
                // the snapshot hid it (VIS-09), or a spawn is publishing. Its record decides, and must name this slot.
                if (!isolated || (src.ClusterState.ClaimedBits(clusterChunkId) & (1UL << slotIndex)) == 0
                    || !SnapshotSlot.IsReservedSpawnVisible(entityRaw, clusterChunkId, slotIndex, src.EngineState, recordBuf, _transactionTSN,
                        _tx.DBE.CommitCards, ref MapAccessor(sourceIndex)))
                {
                    return false;
                }
            }

            if (src.VersionedIndex < 0)
            {
                // SingleVersion in this archetype: the cluster slot IS the component. Its key is re-checked: the key's values were copied out of their
                // buffer before this (IXS-10), and a SingleVersion destroy releases its slot at once — a spawn can have taken it since, another entity under
                // another key. The buffer's lock used to hold the destroy's index removal, and so the release, off until the read was done.
                _currentComponentSpan = new ReadOnlySpan<byte>(clusterBase + src.CompOffset + slotIndex * src.CompSize, src.CompSize);
                if (!_currentComponentSpan.Slice(src.FieldOffset, sizeof(TKey)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(ref key))))
                {
                    return false;
                }

                // An archetype that promises isolation: born at or before the snapshot, and alive at it (#1247) — the cluster's summary answers for most.
                if (isolated && occupied && !src.ClusterState.IsClusterFullyVisibleAt(clusterChunkId, _transactionTSN)
                    && !SnapshotSlot.IsVisibleAtSnapshot(entityRaw, true, src.EngineState, recordBuf, src.RecordSize, _transactionTSN, _tx.DBE.CommitCards,
                        ref MapAccessor(sourceIndex)))
                {
                    return false;
                }
            }
            else
            {
                // Hinted, not plain TryGet: entity keys are dense, so the location-hint cache resolves in one bucket visit instead of a full hash +
                // directory + chain walk. Identical semantics — the hint is a pure accelerator that falls back on any mismatch. A hidden slot's record was
                // read above.
                if (occupied && !src.EngineState.EntityMap.TryGetWithHint(EntityId.FromRaw(entityRaw).EntityKey, recordBuf, ref MapAccessor(sourceIndex)))
                {
                    return false;
                }

                var chainRoot = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, src.VersionedIndex);
                if (chainRoot == 0)
                {
                    return false;
                }

                var walked = RevisionChainReader.WalkChain(ref _compRevAccessor, chainRoot, _transactionTSN, _tx);
                if (walked.IsFailure || walked.Value.CurCompContentChunkId == 0)
                {
                    return false;   // not visible at this snapshot, or tombstoned
                }

                _currentComponentSpan = _compContentAccessor.GetChunkAsReadOnlySpan(walked.Value.CurCompContentChunkId).Slice(_componentOverhead);

                // A tree row is the entity's only when its value at the snapshot carries the row's key. An update re-keys the tree before it copies its
                // HEAD, so a side read between the two took the stale HEAD key for the tree's and did not drop the entity: its row here, at the newer key,
                // resolves to the older value — refused rather than returned under a key it did not have (#1247).
                if (!_currentComponentSpan.Slice(src.FieldOffset, sizeof(TKey)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(ref key))))
                {
                    return false;
                }
            }

            _currentPK = entityRaw;
            _currentKey = key;
            return true;
        }

        [UnscopedRef]
        private ref ChunkAccessor<PersistentStore> ClusterAccessor(int sourceIndex)
        {
            if (_clusterAccessors == null)
            {
                return ref _clusterAccessor0;
            }

            return ref _clusterAccessors[sourceIndex];
        }

        [UnscopedRef]
        private ref ChunkAccessor<PersistentStore> MapAccessor(int sourceIndex)
        {
            if (_mapAccessors == null)
            {
                return ref _mapAccessor0;
            }

            return ref _mapAccessors[sourceIndex];
        }

        /// <summary>
        /// Pulls the next <c>(key, location)</c> from whichever source this enumerator has: a live B+Tree cursor when one archetype holds the component, or
        /// the pre-merged array when several do. Keeping the two shapes behind one call is what lets the resolution below stay single-bodied.
        /// <paramref name="keyRead"/>: streaming, the entry's key was read from the tree just now — a unique entry, or an
        /// AllowMultiple key's values copied — so a change published before it is one the walk must account for (<see cref="CheckChangesSinceLastRead"/>).
        /// </summary>
        private bool TryNextEntry(out TKey key, out int location, out int sourceIndex, out bool keyRead)
        {
            keyRead = false;
            if (!_streaming)
            {
                if (_entryIndex >= _entries.Length)
                {
                    key = default;
                    location = 0;
                    sourceIndex = 0;
                    return false;
                }

                ref var entry = ref _entries[_entryIndex++];
                key = entry.Key;
                location = entry.Location;
                sourceIndex = entry.SourceIndex;
                return true;
            }

            sourceIndex = 0;

            if (!_isAllowMultiple)
            {
                if (_innerUnique.MoveNext())
                {
                    var kv = _innerUnique.Current;
                    key = kv.Key;
                    location = kv.Value;
                    keyRead = true;
                    return true;
                }

                key = default;
                location = 0;
                return false;
            }

            while (true)
            {
                if (_currentValueIndex < _currentValues.Length)
                {
                    key = _cursorKey;
                    keyRead = _currentValueIndex == 0;
                    location = _currentValues[_currentValueIndex++];
                    return true;
                }

                if (!_innerMultiple.MoveNextKey())
                {
                    key = default;
                    location = 0;
                    return false;
                }

                // The whole key, copied and its buffer released, before MoveNext resolves any entity through its chain: a chain walk under the buffer's
                // lock waited on a publish that held the chain and waited on the buffer (IXS-10).
                _cursorKey = _innerMultiple.CurrentKey;
                var count = _innerMultiple.DrainCurrentKey(ref _keyValues);
                _currentValues = new ReadOnlySpan<int>(_keyValues, 0, count);
                _currentValueIndex = 0;
                KeyCopiedProbe?.Invoke();
            }
        }

        /// <summary>Releases the per-archetype and component accessors (unpins accessed pages). Idempotent.</summary>
#pragma warning disable TYPHON006 // Its _tx is borrowed: the transaction that created the enumerator, its caller's to dispose
        public void Dispose()
#pragma warning restore TYPHON006
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_clusterAccessors == null)
            {
                _clusterAccessor0.Dispose();
                _mapAccessor0.Dispose();
            }
            else
            {
                for (var i = 0; i < _clusterAccessors.Length; i++)
                {
                    _clusterAccessors[i].Dispose();
                    _mapAccessors[i].Dispose();
                }
            }

            if (_streaming)
            {
                if (_isAllowMultiple)
                {
                    _innerMultiple.Dispose();
                    if (_keyValues.Length > 0)
                    {
                        System.Buffers.ArrayPool<int>.Shared.Return(_keyValues);
                    }

                    _keyValues = [];
                }
                else
                {
                    _innerUnique.Dispose();
                }
            }

            _compRevAccessor.Dispose();
            _compContentAccessor.Dispose();
            _side?.Release();
        }
    }

    internal ref CompRevStorageHeader GetCompRevStorageHeader<T>(long entity)
    {
        var ci = GetComponentInfo(typeof(T));
        var result = GetCompRevTableFirstChunkId(entity, ci);
        if (result.IsFailure)
        {
            return ref Unsafe.NullRef<CompRevStorageHeader>();
        }

        return ref ci.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(result.Value);
    }

    internal int GetRevisionCount<T>(long entity)
    {
        ref var header = ref GetCompRevStorageHeader<T>(entity);
        if (Unsafe.IsNullRef(ref header))
        {
            return -1;
        }

        return header.ItemCount;
    }

    /// <summary>
    /// Read-only transactions just refresh the epoch scope (no dirty state to flush).
    /// </summary>
    internal override void EnumerateRefreshEpoch()
    {
        if (IsReadOnly)
        {
            _epochManager.RefreshScope();
            return;
        }

        FlushAndRefreshEpoch();
    }

    /// <summary>
    /// <typeparamref name="T"/> of <paramref name="pk"/> as COMMITTED at this transaction's snapshot — never its own unpublished writes, nor its own pending
    /// spawns (#1247). The read a view's refresh and a navigation predicate make: a refresh folded a write it would later roll back, and kept it for good.
    /// False when the entity is not visible at the snapshot, has no such component, a Versioned one has no revision there, or — with
    /// <paramref name="requireEnabled"/> — it is disabled at the snapshot.
    /// </summary>
    /// <remarks>
    /// A Versioned component is read through its record and its chain, as a snapshot fill reads it (EcsQuery.FillSnapshotColumn), and with the same care: no
    /// epoch refresh under the point read (#1230). A SingleVersion or Transient one promises no isolation and has no history; its bytes are the transaction's
    /// open of it, enabled required, as the read this replaced required it. The record is read through the accessor's warm EntityMap cache.
    /// </remarks>
    internal unsafe bool TryReadCommitted<T>(long pk, bool requireEnabled, out T value) where T : unmanaged
    {
        value = default;
        var id = EntityId.FromRaw(pk);
        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta == null || !meta.TryGetSlot(ArchetypeRegistry.GetComponentTypeId<T>(), out var slot))
        {
            return false;
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        var versionedIndex = meta.ClusterLayout?.SlotToVersionedIndex != null ? meta.ClusterLayout.SlotToVersionedIndex[slot] : -1;
        if (es?.EntityMap == null)
        {
            return false;
        }

        var record = stackalloc byte[meta._entityRecordSize];
        var bytes = stackalloc byte[sizeof(T)];

        // As the open: a refresh normally runs inside the transaction's epoch scope already, and a default guard must not be disposed.
        var needsGuard = NeedsEpochGuard();
        var guard = needsGuard ? EpochGuard.Enter(_epochManager) : default;
        ref var entityMap = ref EntityMapAccessor(es, id.ArchetypeId);

        var refreshable = PagedMMF.RefreshableWaitDepth;
        PagedMMF.RefreshableWaitDepth = 0;
        try
        {
            if (!es.EntityMap.TryGetWithHint(id.EntityKey, record, ref entityMap)
                || !EntityVisibility.IsVisibleInScan(record, TSN, CommitCards, es.EntityMap, id.EntityKey))
            {
                return false;
            }

            if (versionedIndex < 0)
            {
                // Committed and visible: the transaction's open reads the slot — no history, no isolation promised.
                return TryOpen(id, out var entity) && entity.TryRead(out value);
            }

            if (requireEnabled)
            {
                var enabled = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, EntityRecordAccessor.GetHeader(record).EnabledBits, TSN, CommitCards);
                if ((enabled & (1 << slot)) == 0)
                {
                    return false;
                }
            }

            if (!TryCopyCommittedAtSnapshot(meta, slot, ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, versionedIndex), bytes, sizeof(T)))
            {
                return false;
            }

            value = MemoryMarshal.Read<T>(new ReadOnlySpan<byte>(bytes, sizeof(T)));
            return true;
        }
        finally
        {
            PagedMMF.RefreshableWaitDepth = refreshable;
            if (needsGuard)
            {
                guard.Dispose();
            }
        }
    }

    /// <summary>
    /// Query-engine read primitive. Reads a single component by primary key with MVCC visibility applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <see cref="StorageMode.Versioned"/> this walks the revision chain directly — cheaper than
    /// <c>Open().Read()</c> because it resolves only the requested component instead of every archetype slot.
    /// </para>
    /// <para>
    /// For <see cref="StorageMode.SingleVersion"/> there is no revision chain to walk (<c>ComponentTable</c> allocates
    /// <c>CompRevTableSegment</c> only for Versioned), so the read goes through <see cref="EntityAccessor.TryOpen"/> +
    /// <see cref="EntityRef.TryRead{T}(out T)"/>, which resolve the cluster-or-flat slot location and apply
    /// BornTSN/DiedTSN visibility. Before issue #623 this method assumed the Versioned layout unconditionally and threw a
    /// bare <see cref="NullReferenceException"/> from the chain walker on any SingleVersion component — which is what made
    /// FK navigation unusable on the storage mode the engine steers hot ECS data toward.
    /// </para>
    /// <para>
    /// The SingleVersion path pays the full slot resolution this method exists to avoid. That is a correctness-first
    /// trade, not a settled design: a dedicated by-PK SingleVersion read would skip it. No benchmark covers this path
    /// today (issue #623).
    /// </para>
    /// </remarks>
    [PublicAPI]
    public bool QueryRead<T>(long pk, out T t) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var info = GetComponentInfo(componentType);

        if (info.ComponentTable.StorageMode == StorageMode.SingleVersion)
        {
            if (!TryOpen(EntityId.FromRaw(pk), out var entity))
            {
                t = default;
                return false;
            }

            return entity.TryRead(out t);
        }

        // Check if we already have this component in the cache
        ref var compRevInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var exists);
        if (!exists)
        {
            // Couldn't find in the cache, get it from the index. A throw (the walk's lock timeout) takes the entry with it, as a failure does below.
            Result<ComponentInfo.CompRevInfo, RevisionReadStatus> result;
            try
            {
                result = GetCompRevInfoFromIndex(pk, info, TSN);
            }
            catch
            {
                info.SingleCache.Remove(pk);
                throw;
            }

            if (result.IsFailure)
            {
                // NotFound, SnapshotInvisible, or Deleted — all mean no readable component. Remove the default entry that GetValueRefOrAddDefault added to
                // avoid leaving a zombie CompRevInfo (all zeros) that would corrupt subsequent operations on the same PK within this transaction.
                info.SingleCache.Remove(pk);
                t = default;
                return false;
            }
            compRevInfo = result.Value;
            compRevInfo.Operations |= ComponentInfo.OperationType.Read;
        }

        // Deleted component ?
        if (compRevInfo.CurCompContentChunkId == 0)
        {
            t = default;
            return false;
        }

        // If there is a valid component, copy its content to the destination.
        // No shared lock needed: deferred chunk freeing guarantees content chunks remain valid for the transaction's lifetime.
        t = default;
        // A span over t, not a pointer: t is the caller's variable and can be a field or an array element on the GC heap. Clamped to sizeof(T), so a
        // storage size larger than T cannot write past it.
        int size = Math.Min(info.ComponentTable.ComponentStorageSize, Unsafe.SizeOf<T>());
        var src = info.CompContentAccessor.GetChunkAsReadOnlySpan(compRevInfo.CurCompContentChunkId);
        src.Slice(info.ComponentTable.ComponentOverhead, size).CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref t)));

        return true;
    }

    /// <summary>
    /// A navigation predicate's read (#1247): <typeparamref name="T"/> of <paramref name="pk"/> committed at the snapshot, as
    /// <see cref="TryReadCommitted{T}"/> reads it, but at <see cref="QueryRead{T}"/>'s cost when the transaction has not written it — and its own pending
    /// spawn on its own values, as a field query reads one (QENABLE-01).
    /// </summary>
    /// <remarks>
    /// A Versioned write of this transaction lives in the component's <c>SingleCache</c> (Created, Updated, Deleted): no entry, or a read-only one, is the
    /// committed revision at the snapshot, so <see cref="QueryRead{T}"/>'s cached walk answers. Reading every target through the committed path instead
    /// cost a navigation twice its time: an EntityMap lookup and a chain walk per target per query, where the cache held the answer.
    /// </remarks>
    internal bool QueryReadCommitted<T>(long pk, out T value) where T : unmanaged
    {
        if (IsOwnPendingSpawn(pk))
        {
            return QueryRead(pk, out value);
        }

        var info = GetComponentInfo(typeof(T));
        if (info.ComponentTable.StorageMode != StorageMode.Versioned)
        {
            return TryReadCommitted(pk, false, out value);
        }

        ref var cri = ref CollectionsMarshal.GetValueRefOrNullRef(info.SingleCache, pk);
        if (Unsafe.IsNullRef(ref cri))
        {
            return QueryRead(pk, out value);
        }

        if ((cri.Operations & ~ComponentInfo.OperationType.Read) != 0)
        {
            return TryReadCommitted(pk, false, out value);   // its own write: the committed revision under it
        }

        value = default;
        if (cri.CurCompContentChunkId == 0)
        {
            return false;
        }

        var size = Math.Min(info.ComponentTable.ComponentStorageSize, Unsafe.SizeOf<T>());
        var src = info.CompContentAccessor.GetChunkAsReadOnlySpan(cri.CurCompContentChunkId);
        src.Slice(info.ComponentTable.ComponentOverhead, size).CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return true;
    }

    /// <summary>
    /// Write a component by PK. Reconstructs EntityId from the raw PK, opens the entity for mutation, and writes the component data.
    /// Generic — the Shell CLI calls this via <c>MakeGenericMethod</c> for runtime-resolved component types.
    /// </summary>
    [PublicAPI]
    public bool WriteComponent<T>(long pk, ref T comp) where T : unmanaged
    {
        var entityId = Unsafe.As<long, EntityId>(ref pk);
        var entity = OpenMut(entityId);
        entity.Set(in comp);
        return true;
    }

    /// <summary>
    /// Spawn an entity using an archetype ID (non-generic). Enables runtime callers (Shell CLI) to create entities
    /// without compile-time archetype type parameters.
    /// </summary>
    [PublicAPI]
    public EntityId SpawnByArchetypeId(ushort archetypeId, params ComponentValue[] values)
    {
        var meta = ArchetypeRegistry.GetMetadata(archetypeId);
        if (meta == null)
        {
            throw new InvalidOperationException($"Archetype ID {archetypeId} not registered");
        }
        // Delegate to the core Spawn logic (shared with Spawn<TArch>)
        return SpawnInternal(meta, values);
    }

    /// <summary>
    /// Resolve compRevFirstChunkId for an archetype entity via EntityMap lookup.
    /// Uses a Transaction-level cached accessor (same-archetype calls reuse the accessor's MRU warmth).
    /// </summary>
    private int ResolveEntityMapSlotChunkId(long pk, ComponentInfo info)
    {
        var entityId = EntityId.FromRaw(pk);
        if (entityId.ArchetypeId == 0)
        {
            return 0;
        }

        var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
        if (meta == null)
        {
            return 0;
        }

        // O(1) slot lookup via cached componentTypeId (replaces O(C) linear scan)
        if (!meta.TryGetSlot(info.ComponentTypeId, out byte slot))
        {
            return 0;
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return 0;
        }

        // Reuse cached EntityMap accessor when archetype matches
        ref var entityMap = ref EntityMapAccessor(es, entityId.ArchetypeId);

        int recordSize = meta._entityRecordSize;
        byte* buf = stackalloc byte[recordSize];
        if (!es.EntityMap.TryGet(entityId.EntityKey, buf, ref entityMap))
        {
            return 0;
        }

        // The two record shapes put DIFFERENT things at offset 14, so the shape has to be decided before the record is read, not after. A flat record has
        // Location[0] there; a cluster record has ClusterChunkId, with the chain roots starting at CompRevOffset (19) and indexed by VERSIONED ordinal rather
        // than by component slot. Reading a cluster record with the flat accessor therefore returns the cluster chunk id as if it were a chain root — which is
        // the same value for every entity sharing that cluster, so all of them resolve to whichever entity's content that chunk id happens to name. Same
        // condition as the EntityRef resolve path in Transaction.ECS.cs, deliberately (#629).
        if (meta.IsClusterEligible && es.ClusterState != null)
        {
            int versionedIndex = es.ClusterState.Layout.SlotToVersionedIndex[slot];
            return versionedIndex < 0 ? 0 : ClusterEntityRecordAccessor.GetCompRevFirstChunkId(buf, versionedIndex);
        }

        return EntityRecordAccessor.GetLocation(buf, slot);
    }

    /// <summary>
    /// Lightweight MVCC visibility check via EntityRecord BornTSN/DiedTSN.
    /// Uses the cached EntityMap accessor — no CompRev chain walk, no component data read.
    /// For query Count/Execute paths where the primary index scan already guarantees field predicates.
    /// </summary>
    internal bool IsEntityVisible(long pk)
    {
        var entityId = EntityId.FromRaw(pk);
        if (entityId.ArchetypeId == 0)
        {
            return false;
        }

        var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
        if (meta == null)
        {
            return false;
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return false;
        }

        // Reuse cached EntityMap accessor
        ref var entityMap = ref EntityMapAccessor(es, entityId.ArchetypeId);

        // TryGet copies full record, but we only need the header (first 14 bytes).
        // Use full record size to satisfy TryGet's contract, but stackalloc min header.
        int recordSize = meta._entityRecordSize;
        byte* fullBuf = stackalloc byte[recordSize];
        if (!es.EntityMap.TryGet(entityId.EntityKey, fullBuf, ref entityMap))
        {
            return false;
        }

        return EntityVisibility.IsVisible(fullBuf, TSN, CommitCards, es.EntityMap, entityId.EntityKey, ref entityMap, _epochManager);
    }

    private Result<int, BTreeLookupStatus> GetCompRevTableFirstChunkId(long pk, ComponentInfo info)
    {
        int chunkId = ResolveEntityMapSlotChunkId(pk, info);
        return chunkId != 0 ? new Result<int, BTreeLookupStatus>(chunkId) : new Result<int, BTreeLookupStatus>(BTreeLookupStatus.NotFound);
    }

    private Result<ComponentInfo.CompRevInfo, RevisionReadStatus> GetCompRevInfoFromIndex(long pk, ComponentInfo info, long tick)
    {
        int chunkId = ResolveEntityMapSlotChunkId(pk, info);
        if (chunkId != 0)
        {
            return RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, chunkId, TSN, this);
        }
        return new Result<ComponentInfo.CompRevInfo, RevisionReadStatus>(RevisionReadStatus.NotFound);
    }

    /// <summary>
    /// Create a WaitContext that respects both the UoW deadline and a subsystem-specific timeout.
    /// The tighter deadline wins.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WaitContext ComposeWaitContext(ref UnitOfWorkContext ctx, TimeSpan subsystemTimeout)
        => WaitContext.FromDeadline(Deadline.Min(ctx.WaitContext.Deadline, Deadline.FromTimeout(subsystemTimeout)));

    /// <summary>
    /// The <see cref="WaitContext"/> for ONE revision-chain lock acquisition while reading (resolve/walk): the tighter of the owning UoW's deadline and
    /// <c>RevisionChainLockTimeout</c> from now. Composed per acquisition, and only when that acquisition has to wait: the single-entry fast path takes
    /// no lock, and the locked walk first tries the lock without waiting (<c>AccessControlSmall.TryEnterSharedAccess</c>), which reads no clock
    /// (<see cref="RevisionChainReader.WalkChain(ref ChunkAccessor{PersistentStore}, int, long, Transaction)"/>, <see cref="RevisionEnumerator"/>).
    /// </summary>
    /// <remarks>
    /// Never cached for the transaction (CX-06). It was, to save a <c>Stopwatch.GetTimestamp()</c> per walk: the budget then ran from the transaction's
    /// first locked walk, so once the transaction was older than the timeout every contended walk threw at once — a reader holding its snapshot past
    /// 5 s failed on its first wait (MarketHardeningTests' long readers, on the desks' chains).
    /// </remarks>
    internal WaitContext ChainLockWaitContext()
    {
        if (ChainLockWaitContextProbe is { } probe)
        {
            return probe();
        }

        // A unit of work already disposed has no deadline left to compose with (and its token source is gone): the lock's own budget applies.
        return OwningUnitOfWork is { IsDisposed: false } uow
            ? uow.CreateContext(TimeoutOptions.Current.RevisionChainLockTimeout).WaitContext
            : WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
    }

    /// <summary>
    /// Test seam (CX-06): when set, supplies the chain-lock budget in place of <see cref="ChainLockWaitContext"/>'s own, so the rule's mutant can bring
    /// back a budget cached for the transaction and show the verifier rejects it. Cleared by <c>Init</c>. Null in production: one null check per locked
    /// walk, none on the single-entry fast path.
    /// </summary>
    internal Func<WaitContext> ChainLockWaitContextProbe { get; set; }

    /// <summary>
    /// Rolls back a single component revision: frees content chunks, voids revision entries, and enqueues for deferred cleanup.
    /// </summary>
    /// <returns><c>true</c> if the component was created (rev table chunk freed — caller must remove from cache); <c>false</c> otherwise.</returns>
    private bool RollbackComponent(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;

        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var componentSegment = info.CompContentSegment;
        var revTableSegment = info.CompRevTableSegment;

        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        _dbe.ChainVoidProbe?.Invoke(info.ComponentTable, firstChunkId);

        // If we roll back a created component, we must delete the revision table chunk. No other transaction can reach a chain this one created.
        if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            // A root of 0: an earlier pass of this rollback freed the chain, then threw on a later entry, and this is the retry (Dispose). The entry
            // leaves the cache only once the whole pass is done, so the retry meets it again; freeing the root twice took the chain of whoever had been
            // given the chunk in between (REAP-02).
            if (firstChunkId != 0)
            {
                FreeOwnContentChunk(info, ref compRevInfo);
                revTableSegment.FreeChunk(firstChunkId);
                compRevInfo.CompRevTableFirstChunkId = 0;
            }

            // Early exit: the RevTable chunk is gone — continuing would access freed memory.
            return true;
        }

        // In case of update or delete, void the revision entry we added (CHAIN-01): under the chain's exclusive lock, found by its identity — a compaction
        // may have moved it, and another transaction's revision may now sit where it was, or after it. Its content chunk is freed only when the entry was
        // this transaction's: when the first write failed before adding one, CurCompContentChunkId still names the committed revision's chunk (#1252).
        if ((compRevInfo.Operations & (ComponentInfo.OperationType.Updated | ComponentInfo.OperationType.Deleted)) != 0)
        {
            if (_dbe.RollbackVoidsRecordedIndexForTest)
            {
                new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId)
                    .VoidElement(ComponentRevisionManager.GetRevisionElement(ref compRevTableAccessor, firstChunkId, compRevInfo.CurRevisionIndex));
                FreeOwnContentChunk(info, ref compRevInfo);
            }
            else if (ComponentRevisionManager.VoidOwnPendingEntry(info, firstChunkId, compRevInfo.CurCompContentChunkId, TSN, compRevInfo.CurRevisionIndex))
            {
                FreeOwnContentChunk(info, ref compRevInfo);
            }

            compRevInfo.CurCompContentChunkId = 0;
        }

        // Enqueue for deferred cleanup
        _deferredEnqueueBatch ??= new List<DeferredCleanupManager.CleanupEntry>(16);
        _deferredEnqueueBatch.Add(new DeferredCleanupManager.CleanupEntry { Table = info.ComponentTable, PrimaryKey = pk, FirstChunkId = firstChunkId });

        if (_deferredEnqueueBatch.Count >= DeferredEnqueueBatchCapacity)
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        // Clear stale revision tracking
        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;

        return false;
    }

    /// <summary>
    /// Frees the content chunk this transaction's revision named — this rollback is its one owner (REAP-02). CC-aware, for the collection buffers a
    /// copy-on-write or a spawn took a reference on; and the id cleared, which is what tells CleanupEcsState, at the reset that follows, that it is gone.
    /// It was left set, and that cleanup freed it a second time: harmless only until another thread took the chunk in between, then two owners of one
    /// payload.
    /// </summary>
    private static void FreeOwnContentChunk(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo)
    {
        if (compRevInfo.CurCompContentChunkId != 0)
        {
            DeferredCleanupManager.FreeContentChunk(info.ComponentTable, compRevInfo.CurCompContentChunkId, neverCommitted: true);
            compRevInfo.CurCompContentChunkId = 0;
        }
    }

    /// <summary>
    /// Detects write-write conflicts and resolves them through the handler, under the chain lock PREPARE holds since it took it; without a handler the
    /// last writer wins and there is nothing to resolve.
    /// </summary>
    /// <remarks>
    /// With a handler, a conflict is a revision another transaction committed past this writer's snapshot (plan 06 §3.10): published with a stamp above
    /// <paramref name="tsn"/>, or flagged by a committer registered on its commit card with one — past its commit point, not yet published, which a
    /// CommitSequence test misses (it is bumped at publish). Chain position is no version order (VIS-05), so the old test of the last commit's index is
    /// gone.
    /// <para>
    /// Without a handler the conflict is only counted: a CommitSequence changed since the read. This transaction's entry needs no relocation — its stamp is
    /// drawn at its commit point, after the commit it lost to published, so it is the newest revision by stamp wherever it sits in the chain (VIS-05), a
    /// tombstone as well as a value. Before option C the entry had to be re-added at the chain's end, under the chain lock, with a copy of its content.
    /// </para>
    /// </remarks>
    private static void DetectAndResolveConflict(ref CommitContext context, long pk, ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo,
        ComponentRevision compRev, ref ComponentRevisionManager.ElementRevisionHandle elementHandle, int readCompChunkId, long tsn, ushort uowId,
        DatabaseEngine dbe)
    {
        var conflictSolver = context.Solver;

        short newestIndex = -1;
        bool hasConflict;
        if (conflictSolver != null)
        {
            newestIndex = ComponentRevisionManager.FindNewestByStamp(ref info.CompRevTableAccessor, compRevInfo.CompRevTableFirstChunkId, dbe?.CommitCards,
                tsn, out var newestStamp);
            hasConflict = newestStamp > tsn;
        }
        else
        {
            hasConflict = compRev.CommitSequence != compRevInfo.ReadCommitSequence;
        }

        if (!hasConflict)
        {
            return;
        }

        // Record conflict for observability
        dbe?.RecordConflict();

        // Phase 6: Data:Transaction:Conflict instant. componentTypeId left as 0 — the enclosing
        // TransactionCommitComponent span (kind 22) carries the typeId, so the viewer correlates by parent.
        // conflictType encodes which detection path fired: 0 = handler-based (by stamp), 1 = handler-less (CommitSequence changed).
        var conflictType = (byte)(conflictSolver != null ? 0 : 1);
        TyphonEvent.EmitDataTransactionConflict(tsn, pk, 0, conflictType);

        if (conflictSolver != null)
        {
            ResolveConflictUnderLock(ref context, pk, info, ref compRevInfo, compRev, ref elementHandle, newestIndex, readCompChunkId, tsn, uowId);
        }
    }

    /// <summary>
    /// The first supplies this transaction wrote — a Versioned component supplied to a published entity that had none (STAGE-02, QENABLE-01) — claimed before
    /// PREPARE (<see cref="DatabaseEngine.FirstSupplyClaims"/>), so one supply of an (entity, component) commits at a time: the write created a chain of its
    /// own, and two supplies would each publish theirs — the record keeping one, the slot and the index the other's value. A second waits for the claim,
    /// bounded by <see cref="TimeoutOptions.RevisionChainLockTimeout"/>, then finds the root the first published (<see cref="PrepareFirstSupply"/>).
    /// </summary>
    /// <remarks>
    /// In one global order — (entity, component type) — and before PREPARE takes any chain lock: two commits supplying two components in crossed order
    /// cannot each hold one claim and wait for the other's, and a waiter holds nothing a claim holder needs. A first supply aborted meanwhile publishes no
    /// root, and the waiter commits as the first.
    /// </remarks>
    private void ClaimFirstSupplies(ref UnitOfWorkContext ctx)
    {
        if (_firstSupplies is not { Count: > 0 } supplies)
        {
            return;
        }

        supplies.Sort();
        var claims = _dbe.FirstSupplyClaims;
        for (var i = 0; i < supplies.Count; i++)
        {
            var key = supplies[i];
            if (i > 0 && supplies[i - 1] == key)
            {
                continue;
            }

            if (!claims.TryAdd(key, TSN))
            {
                _dbe.FirstSupplyWaitProbe?.Invoke();
                var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.RevisionChainLockTimeout);
                var waiter = new AdaptiveWaiter();
                while (!claims.TryAdd(key, TSN))
                {
                    if (!waiter.Wait(ref wc))
                    {
                        ThrowHelper.ThrowLockTimeout("FirstSupply/Claim", TimeoutOptions.Current.RevisionChainLockTimeout);
                    }
                }
            }

            (_firstSupplyClaims ??= []).Add(key);
            _dbe.FirstSupplyClaimedProbe?.Invoke(key.Pk, key.ComponentTypeId);
        }
    }

    /// <summary>
    /// PREPARE of a claimed first supply (<see cref="ClaimFirstSupplies"/>): when a supply that committed meanwhile published the record's root, this one
    /// joins that chain as an update would (<see cref="JoinPublishedChain"/>), so the later commit's value wins, or the conflict handler decides, as for any
    /// write-write conflict.
    /// </summary>
    private void PrepareFirstSupply(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;

        // The root a supply that committed meanwhile published: its publish set it before releasing the claim this one took.
        var entityId = EntityId.FromRaw(pk);
        var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.TryGetSlot(info.ComponentTypeId, out var slot) || meta.ClusterLayout?.SlotToVersionedIndex is not { } slotToVi || slotToVi[slot] < 0)
        {
            return;
        }

        // Through the commit's cached accessors, which the rest of this entry's PREPARE uses next (PrepareClusterVersionedSlot).
        byte* record = stackalloc byte[meta._entityRecordSize];
        EnsureClusterCommitAccessors(meta.ArchetypeId, es, info.ComponentTable.ComponentSegment, es.ClusterState);
        if (!es.EntityMap.TryGet(entityId.EntityKey, record, ref _clusterCommitMapAccessor))
        {
            return;
        }

        var root = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, slotToVi[slot]);
        if (root != 0 && root != context.CompRevInfo.CompRevTableFirstChunkId)
        {
            JoinPublishedChain(info, ref context.CompRevInfo, root);
        }
    }

    /// <summary>
    /// A first supply that lost the race gives its own chain up for <paramref name="root"/>, the one the winner published: its value becomes a revision of
    /// that chain, pending as any update's, and the supply an update that read the component absent — no read value, and a commit since (the winner's). A
    /// supply this transaction then destroyed joins as the tombstone its own revision had become (REAP-02). Its own chain, which no other transaction can
    /// reach, is freed.
    /// </summary>
    /// <remarks>
    /// Built on a copy and swapped in only once it holds: a throw before — the chain lock's timeout, a page access — leaves the entry naming its own Created
    /// chain, which the rollback frees, with the entry added to the winner's chain voided and its chunk freed here. Swapped in first, the rollback took the
    /// entry for a Created one and freed the winner's chain, still named by the record.
    /// </remarks>
    private void JoinPublishedChain(ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, int root)
    {
        var ownRoot = compRevInfo.CompRevTableFirstChunkId;
        var ownContent = compRevInfo.CurCompContentChunkId;
        var tombstone = ownContent == 0;

        var joined = compRevInfo;
        joined.CompRevTableFirstChunkId = root;
        joined.CurCompContentChunkId = 0;
        joined.CurRevisionIndex = -1;
        var added = false;
        try
        {
            ComponentRevisionManager.AddCompRev(info, ref joined, TSN, UowId, tombstone);
            added = true;
            _dbe.FirstSupplyJoinProbe?.Invoke();

            // The bytes, collection handles included, move to the new revision's chunk, which takes over the buffers they name: the old chunk is freed raw,
            // as the conflict resolution frees its orphan.
            if (!tombstone)
            {
                var size = info.ComponentTable.ComponentTotalSize;
                new ReadOnlySpan<byte>(info.CompContentAccessor.GetChunkAddress(ownContent), size)
                    .CopyTo(new Span<byte>(info.CompContentAccessor.GetChunkAddress(joined.CurCompContentChunkId, true), size));
            }
        }
        catch
        {
            if (added)
            {
                ComponentRevisionManager.VoidOwnPendingEntry(info, root, joined.CurCompContentChunkId, TSN, joined.CurRevisionIndex);
                if (joined.CurCompContentChunkId != 0)
                {
                    info.CompContentSegment.FreeChunk(joined.CurCompContentChunkId);
                }
            }

            throw;
        }

        if (!tombstone)
        {
            info.CompContentSegment.FreeChunk(ownContent);
        }

        info.CompRevTableSegment.FreeChunk(ownRoot);

        joined.Operations = (joined.Operations & ~ComponentInfo.OperationType.Created) | (tombstone ? 0 : ComponentInfo.OperationType.Updated);
        joined.PrevCompContentChunkId = 0;
        joined.PrevRevisionIndex = -1;
        joined.ReadCommitSequence = 0;
        compRevInfo = joined;
    }

    /// <summary>The first supplies this commit claimed, released. Idempotent: after the publish, and on every exit of the commit.</summary>
    private void ReleaseFirstSupplyClaims()
    {
        if (_firstSupplyClaims is not { Count: > 0 } claimed)
        {
            return;
        }

        _dbe.FirstSupplyReleaseProbe?.Invoke();
        foreach (var key in claimed)
        {
            // Only this transaction's: a claim another commit took since is not this one's to drop.
            _dbe.FirstSupplyClaims.TryRemove(new KeyValuePair<(long Pk, int ComponentTypeId), long>(key, TSN));
        }

        claimed.Clear();
    }

    /// <summary>
    /// <see cref="DetectAndResolveConflict"/>'s resolution, under the chain's exclusive lock PREPARE holds: a new revision carrying this transaction's value
    /// resolved by the handler against <paramref name="newestIndex"/>, the newest revision by stamp, and this transaction's first entry, the orphan, voided
    /// where it now is.
    /// </summary>
    private static void ResolveConflictUnderLock(ref CommitContext context, long pk, ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo,
        ComponentRevision compRev, ref ComponentRevisionManager.ElementRevisionHandle elementHandle, short newestIndex, int readCompChunkId,
        long tsn, ushort uowId)
    {
        var conflictSolver = context.Solver;
        ref var accessor = ref info.CompRevTableAccessor;
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        // The orphan, found by identity: a compaction may have moved it since CurRevisionIndex was recorded.
        var orphanChunkId = compRevInfo.CurCompContentChunkId;
        var conflictOrphanIndex = ComponentRevisionManager.FindOwnPendingEntry(ref accessor, firstChunkId, orphanChunkId, tsn, compRevInfo.CurRevisionIndex);
        if (conflictOrphanIndex < 0)
        {
            ThrowHelper.ThrowInvalidOp("DetectAndResolveConflict: the transaction's revision entry is not in its chain");
        }

        compRevInfo.CurRevisionIndex = conflictOrphanIndex;

        // A destroy wins the conflict as a destroy: a fresh tombstone after the commit it lost to, ours voided. There is no payload to carry over and nothing
        // for a handler to resolve — the generic path below would publish a LIVE revision copied from chunk 0 (REAP-02).
        if (orphanChunkId == 0 && (compRevInfo.Operations & ComponentInfo.OperationType.Deleted) != 0)
        {
            ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, true, true);
            ComponentRevisionManager.VoidEntry(ref accessor, firstChunkId, conflictOrphanIndex);   // before anything that can throw (CHAIN-01)
            elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
            return;
        }

        // Create a new revision for the resolved data, under the lock
        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, false, true);

        // The orphan is voided as soon as the new entry exists, before anything that can throw — the copy's page access, the handler: a throw aborts the
        // commit, whose rollback finds only the new entry, and left isolated the orphan would stay so for good (no compaction passes an isolated entry).
        // Its content chunk is the copy's source and the handler's committing value, so it is freed last, whatever happens; the new revision's copy took
        // the collection buffers it named.
        ComponentRevisionManager.VoidEntry(ref accessor, firstChunkId, conflictOrphanIndex);
        try
        {
            // Copy the dirty-write data to the new revision as starting point
            var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
            var srcChunk = info.CompContentAccessor.GetChunkAddress(orphanChunkId);
            var sizeToCopy = info.ComponentTable.ComponentTotalSize;
            new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));

            // Update elementHandle to point to the new revision
            elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

            // Invoke the handler to resolve the conflict (under lock, so committedData is guaranteed fresh)
            var lastCommitHandle = compRev.GetRevisionElement(newestIndex);
            var overhead = info.ComponentTable.ComponentOverhead;
            // A first supply that joined the chain another published read the component absent (JoinPublishedChain): its read value is zeroes, and says so.
            var absentSize = readCompChunkId == 0 ? info.ComponentTable.ComponentStorageSize : 0;
            byte* absentRead = stackalloc byte[absentSize];
            new Span<byte>(absentRead, absentSize).Clear();
            var readChunkAddr = readCompChunkId == 0 ? absentRead : info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
            var committingChunkAddr = info.CompContentAccessor.GetChunkAddress(orphanChunkId) + overhead;
            var toCommitChunkAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
            var committedChunkAddr = info.CompContentAccessor.GetChunkAddress(lastCommitHandle.Element.ComponentChunkId) + overhead;

            conflictSolver.Setup(pk, info, readChunkAddr, committedChunkAddr, committingChunkAddr, toCommitChunkAddr, readCompChunkId == 0);
            context.Handler(ref conflictSolver);
        }
        finally
        {
            if (orphanChunkId > 0)
            {
                info.CompContentSegment.FreeChunk(orphanChunkId);
            }
        }
    }

    private KeyValuePair<Type, ComponentInfo>[] _prepareOrder;

    /// <summary>
    /// The component types in the order PREPARE visits them. A handler commit holds every chain lock it takes there until its publish, so it takes them
    /// in one global order — component type id, then primary key (<see cref="ComponentInfo.ForEachMutableEntryOrdered{TAction}"/>). In insertion order,
    /// two commits writing the same entities in opposite orders each held one lock and waited for the other's until RevisionChainLockTimeout (#1321).
    /// </summary>
    private ReadOnlySpan<KeyValuePair<Type, ComponentInfo>> PrepareOrder(bool ordered)
    {
        var count = _componentInfos.Count;
        if (_prepareOrder == null || _prepareOrder.Length < count)
        {
            _prepareOrder = new KeyValuePair<Type, ComponentInfo>[Math.Max(count, 8)];
        }

        ((ICollection<KeyValuePair<Type, ComponentInfo>>)_componentInfos).CopyTo(_prepareOrder, 0);
        var span = _prepareOrder.AsSpan(0, count);
        if (ordered && count > 1)
        {
            span.Sort(static (a, b) => a.Value.ComponentTypeId != b.Value.ComponentTypeId
                ? a.Value.ComponentTypeId.CompareTo(b.Value.ComponentTypeId)
                : string.CompareOrdinal(a.Key.FullName, b.Key.FullName));
        }

        return span;
    }

    /// <summary>Action struct for commit PREPARE iteration: delegates to <see cref="PrepareComponent"/>.</summary>
    private struct PrepareAction : IEntryAction
    {
        public Transaction Tx;
        public void Process(ref CommitContext ctx) => Tx.PrepareComponent(ref ctx);
    }

    /// <summary>Action struct for rollback iteration: delegates to <see cref="RollbackComponent"/>.</summary>
    private struct RollbackAction : IEntryAction
    {
        public Transaction Tx;
        public void Process(ref CommitContext ctx)
        {
            Tx.RollbackComponent(ref ctx);
            Tx.RefreshBetweenCommitSteps();
        }
    }

    /// <summary>
    /// PREPARE half of the AP-01 commit split: all fallible work for one component revision — acquires the revision chain lock (when a handler is
    /// provided; the lock is RETAINED for <see cref="PublishComponent"/> so detect→publish stays atomic for concurrent conflict resolution), detects
    /// and resolves write-write conflicts, mutates secondary/spatial/cluster (Phase-A) indexes, and notifies views — then records a
    /// <see cref="PublishEntry"/>. It does NOT clear IsolationFlag, does NOT bump CommitSequence, and does NOT copy to the cluster slot: those are
    /// the visibility acts, deferred to PublishComponent AFTER the WAL Append (AP-01: append before publish).
    /// </summary>
    private void PrepareComponent(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;

        // A component supplied to a published entity that had none (STAGE-02): before its chain is read below, as it may be another's by then.
        if (_firstSupplyClaims is { Count: > 0 } && (compRevInfo.Operations & ComponentInfo.OperationType.Created) != 0
            && !SpawnedContains(EntityId.FromRaw(pk)))
        {
            PrepareFirstSupply(ref context);
        }

        var conflictSolver = context.Solver;
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Validate CurRevisionIndex — chain compaction may have shifted entry positions since our read
        if (elementHandle.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
        {
            var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
            if (fixedIndex >= 0)
            {
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }
        }

        // Capture readCompChunkId before AddCompRev shifts PrevCompContentChunkId
        var readCompChunkId = compRevInfo.PrevCompContentChunkId;

        // When a handler is provided, hold the per-entity revision chain lock during detect-resolve-commit to prevent TOCTOU races.
        // AP-01: the lock is RETAINED past Append and released by PublishComponent, so [detect, resolve-against-committed, IsolationFlag clear] is
        // one atomic region — required for concurrent conflict resolution to compose (ConcurrencyConflictTests #8).
        var lockHeld = false;
        var lockTicket = default(PagedMMF.CaptureTicket);
        var publishRecorded = false;
        try
        {
            // Inside the try (#1278): from the lock on, a throw — the re-validation's, the conflict resolution's — reaches the finally, which releases
            // the lock while no publish entry owns it.
            if (conflictSolver != null)
            {
                var wcCommit = ComposeWaitContext(ref context.Ctx, TimeoutOptions.Current.RevisionChainLockTimeout);
                AwaitSpawnPublished(pk, info, ref wcCommit);
                ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
                lockTicket = ComponentRevisionManager.ChainLockTicket(ref compRevTableAccessor, ref lockHeader);
                if (!lockHeader.Control.EnterExclusiveAccess(ref wcCommit))
                {
                    ThrowHelper.ThrowLockTimeout("RevisionChain/CommitConflict", TimeoutOptions.Current.RevisionChainLockTimeout);
                }
                lockHeld = true;
                _dbe.PrepareRetainedLockProbe?.Invoke();

                // Re-validate CurRevisionIndex under lock (race-free)
                var curElement = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
                if (curElement.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
                {
                    var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(
                        ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
                    if (fixedIndex < 0)
                    {
                        ThrowHelper.ThrowInvalidOp("PrepareComponent: revision entry lost after chain compaction");
                    }
                    compRevInfo.CurRevisionIndex = fixedIndex;
                    elementHandle = compRev.GetRevisionElement(fixedIndex);
                }
            }

            // No relocation to the chain's end (plan 06 §3.3): the newest revision is the highest stamp wherever it sits (VIS-05).
            DetectAndResolveConflict(ref context, pk, info, ref compRevInfo, compRev, ref elementHandle, readCompChunkId, TSN, UowId, _dbe);

            // Maintain indices/spatial (fallible PREPARE work). The visibility acts — the stamp and IsolationFlag clear, CommitSequence bump, cluster HEAD copy —
            // are deferred to PublishComponent (AP-01). Cluster entities use per-archetype B+Trees/R-Trees, NOT per-ComponentTable shared indexes.
            var archId = EntityId.FromRaw(pk).ArchetypeId;
            var commitMeta = _dbe.GetMetaByRouting(archId);
            bool isClusterEntity = commitMeta.IsClusterEligible && commitMeta.VersionedSlotMask != 0 && _dbe._stateByRouting[archId]?.ClusterState != null;

            var clusterCopyPending = false;
            var clusterChunkId = 0;
            byte slotIndex = 0;
            byte compSlot = 0;

            // The per-ComponentTable branch that used to sit here — IndexMaintainer.UpdateIndices / RemoveSecondaryIndices plus the matching
            // SpatialMaintainer calls — is gone (#629). Every archetype is cluster-backed, so a Versioned commit always maintains the per-ARCHETYPE
            // B+Trees. Established by instrumenting the branch and running the full suite: it was never entered once.
            if (CheckConfig.Enabled && !isClusterEntity)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"Versioned commit reached a non-cluster archetype ({commitMeta?.Name}) — the per-ComponentTable index home no longer exists");
            }

            // Cluster path — Phase A only here (per-archetype B+Tree index updates + view notify). Phase B (HEAD→cluster slot copy) is the
            // visibility act, deferred to PublishComponent.
            //
            // The exemption is PENDING SPAWNS, not the Created flag. PlaceSpawns does the cluster copy for entities spawned in this transaction, so
            // repeating it here would be redundant — but since #845 a component can also be CREATED mid-life on an already-published entity, via
            // EntityRefMut.Enable(comp, in value) on a slot the spawn never supplied. That carries Created too, and testing the flag alone silently skipped its
            // Phase B: the new value reached the revision chain but never the cluster slot the read resolves through, so it read as zero.
            bool copyToCluster = (compRevInfo.Operations & ComponentInfo.OperationType.Created) == 0 || !SpawnedContains(EntityId.FromRaw(pk));
            PrepareClusterVersionedSlot(pk, commitMeta, compRevInfo, readCompChunkId, info.ComponentTable, info.ComponentTypeId, copyToCluster,
                ref compRevTableAccessor, firstChunkId, lockHeld, out clusterCopyPending, out var indexPending, out var slotIndexed, out var baseCompChunkId,
                out clusterChunkId, out slotIndex, out compSlot);
            if (indexPending && (_dbe._stateByRouting[archId].ClusterState.UniqueIndexedSlots & (1 << compSlot)) != 0)
            {
                _pendingUniqueMoves++;
            }

            if (indexPending && _dbe.PrepareReconcilesIndexesForTest)
            {
                // AP-07's mutant: the pre-#1262 order, Phase A in PREPARE — under the slot's index lock, as the publish takes it (VIS-08).
                var mutantClusterState = _dbe._stateByRouting[archId].ClusterState;
                mutantClusterState.LockSlotIndexes(clusterChunkId, slotIndex);
                try
                {
                    PublishClusterIndexes(new PublishEntry
                    {
                        Info = info, CurCompContentChunkId = compRevInfo.CurCompContentChunkId, ArchId = archId, ClusterChunkId = clusterChunkId,
                        SlotIndex = slotIndex, CompSlot = compSlot, Pk = pk, ReadCompChunkId = readCompChunkId,
                    }, readCompChunkId);
                }
                finally
                {
                    mutantClusterState.UnlockSlotIndexes(clusterChunkId, slotIndex);
                }
                indexPending = false;
                slotIndexed = false;
            }

            // Periodic flush: bound dirty counter inflation for large transactions
            if (_batchIndexActive && (++_batchEntityCount & 0x3FF) == 0)
            {
                for (int i = 0; i < _batchIndexAccessors.Length; i++)
                {
                    _batchIndexAccessors[i].CommitChanges();
                }

                // Flush warm accessors: exit+enter cycle performs a single CommitChanges per cache
                ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                ChunkBasedSegment<PersistentStore>.EnterBatchMode();

                _changeSet.ReleaseDirtyMarks();
            }

            // Record the publish descriptor — drained by PublishComponent after the WAL Append (AP-01).
            _publishEntries ??= new List<PublishEntry>(64);
            // Capture the committing element's physical coordinates from the already-resolved handle (DetectAndResolveConflict updates elementHandle by ref
            // to point at the final CurRevisionIndex), so PUBLISH reconstructs the handle with no locking walk (AP-03).
            _publishEntries.Add(new PublishEntry
            {
                Info = info,
                FirstChunkId = firstChunkId,
                CurCompContentChunkId = compRevInfo.CurCompContentChunkId,
                CurRevisionIndex = compRevInfo.CurRevisionIndex,
                Created = (compRevInfo.Operations & ComponentInfo.OperationType.Created) != 0,
                LockHeld = lockHeld,
                LockTicket = lockTicket,
                ElementChunkId = elementHandle.ChunkId,
                ElementIsFirst = elementHandle.IsFirst,
                ElementIndexInChunk = elementHandle.ElementIndex,
                ClusterCopyPending = clusterCopyPending,
                IndexPending = indexPending,
                SlotIndexed = slotIndexed,
                BaseCompChunkId = baseCompChunkId,
                Pk = pk,
                ReadCompChunkId = readCompChunkId,
                ArchId = archId,
                ClusterChunkId = clusterChunkId,
                SlotIndex = slotIndex,
                CompSlot = compSlot,
            });
            publishRecorded = true;
        }
        finally
        {
            // If PREPARE failed before recording the descriptor, PublishComponent will not run for this entry — release the retained lock here so it
            // is not orphaned. On success the lock stays held until PublishComponent clears IsolationFlag and releases it.
            if (lockHeld && !publishRecorded)
            {
                ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId);
                lockHeader.Control.ExitExclusiveAccess();
                ComponentRevisionManager.EndChainLockHold(ref compRevTableAccessor, in lockTicket);
            }
        }

        // Enqueue for deferred cleanup
        _deferredEnqueueBatch ??= new List<DeferredCleanupManager.CleanupEntry>(16);
        _deferredEnqueueBatch.Add(new DeferredCleanupManager.CleanupEntry { Table = info.ComponentTable, PrimaryKey = pk, FirstChunkId = firstChunkId });
        if (_deferredEnqueueBatch.Count >= DeferredEnqueueBatchCapacity)
        {
            _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
            _deferredEnqueueBatch.Clear();
        }

        compRevInfo.PrevCompContentChunkId = -1;
        compRevInfo.PrevRevisionIndex = 0;
    }

    /// <summary>
    /// PUBLISH half of the AP-01 commit split: makes one prepared component revision visible. Non-throwing by construction (AP-03) — re-derives the
    /// revision handle (an <see cref="ComponentRevisionManager.ElementRevisionHandle"/> is a ref struct and cannot be carried across the Append), stamps
    /// the commit stamp + clears IsolationFlag (the publication act), bumps CommitSequence, copies the committed HEAD value into the cluster slot (Phase B), and
    /// releases the per-entity revision-chain lock retained by <see cref="PrepareComponent"/>. MUST run only after the transaction's WAL Append.
    /// </summary>
    private void PublishComponent(in PublishEntry e)
    {
        var info = e.Info;
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        _dbe.PublishComponentProbe?.Invoke(info.ComponentTable, e.FirstChunkId);

        ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(e.FirstChunkId, true);

        // The chain's exclusive lock, for the publication (AP-05, #1158). Without a conflict handler PREPARE does not hold it, and between PREPARE and here
        // a cleanup on another thread can compact this chain: CleanUpUnusedEntries rewrites every kept entry from index 0, the still-isolated one included.
        // Stamping the coordinates PREPARE recorded then cleared IsolationFlag on whatever had moved there — often the previous committed entry, re-stamped
        // with this TSN — and left this transaction's own entry isolated, invisible to every reader for good.
        //
        // The wait is unbounded, so publish stays non-throwing (AP-03), and it ends: a cleanup only ever TRIES the lock, another publish holds it for a few
        // writes and one slot copy, and a conflict-handler transaction holding it from its PREPARE releases it at the end of its own publish — or, if it is
        // itself waiting on a chain this transaction holds, when its bounded PREPARE wait times out and it rolls back. Readers hold it SHARED while they walk
        // a chain that has a pending entry, and AccessControlSmall gives a waiting writer no preference: an entity read without pause can delay this publish
        // for as long as the reads overlap. It does not deadlock — a reader holds no lock this transaction waits for — but the delay is not bounded.
        var lockTaken = false;
        var ticket = e.LockTicket;
        if (!e.LockHeld)
        {
            // A spawn of another commit still publishing (VIS-08) holds the entity's slot index lock, and its own component publish, which runs first, takes
            // chain locks: waited for under this one, the spawner could be waiting for it. So before it, holding no chain lock — the handler path waited in
            // its PREPARE (AwaitSpawnPublished). Once the spawn is published nothing locks the slot for long, and never while waiting for a chain lock.
            if (e.SlotIndexed)
            {
                _dbe._stateByRouting[e.ArchId].ClusterState.WaitSlotIndexesUnlocked(e.ClusterChunkId, e.SlotIndex);
            }

            ticket = ComponentRevisionManager.ChainLockTicket(ref compRevTableAccessor, ref header);
            var unbounded = new WaitContext(Deadline.Infinite, default);
            header.Control.EnterExclusiveAccess(ref unbounded);
            lockTaken = true;
        }

        var completed = false;
        try
        {
            // Reconstruct the revision handle from the coordinates resolved in PREPARE. Held since PREPARE (handler path), the lock kept every compaction
            // out and they are exact. Otherwise they are trusted only while they still name this transaction's pending entry: the root chunk outlives every
            // compaction, but an overflow chunk may have been freed and reissued still holding a stale copy of the entry, so its coordinates are never trusted.
            var revisionIndex = e.CurRevisionIndex;
            var elementHandle = new ComponentRevisionManager.ElementRevisionHandle(ref compRevTableAccessor, e.ElementChunkId, e.ElementIsFirst,
                e.ElementIndexInChunk);
            if (!e.LockHeld && !_dbe.PublishTrustsPrepareCoordinatesForTest
                && (!e.ElementIsFirst || !IsThisTransactionsPendingEntry(elementHandle.Element, e.CurCompContentChunkId)))
            {
                // The lock this thread holds makes the walk take no lock of its own.
                var found = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, e.FirstChunkId, e.CurCompContentChunkId, TSN);
                if (found < 0)
                {
                    // Compaction keeps isolated entries, so this means something else removed the entry — a damaged chain, or an unlocked writer. Stamping
                    // the recorded slot anyway would publish another transaction's entry under this TSN, so nothing more is published: no stamp, no CommitSequence,
                    // no cluster copy, no index move (Phase A runs below since #1263). The commit is lost either way; this keeps it from taking a neighbour down
                    // with it, and says so.
                    Debug.Fail("AP-05: the committing entry vanished from its chain between PREPARE and PUBLISH");
                    _dbe.LogPublishEntryNotFound(info.ComponentTable.Definition.Name, e.FirstChunkId, TSN);
                    completed = true;
                    return;
                }

                revisionIndex = found;
                elementHandle = ComponentRevisionManager.GetRevisionElement(ref compRevTableAccessor, e.FirstChunkId, found);
            }

            Debug.Assert(_commitStamp != 0, "VIS-04: a revision published before its commit stamp was drawn");

            // The newest revision published before this one, by stamp (VIS-05). This publish makes the entity's newest committed value only if its stamp is
            // above it: two last-writer-wins updates publish in either order of their stamps.
            long newestStamp = 0;
            var newestChunkId = 0;
            if (e.SlotIndexed || e.ClusterCopyPending)
            {
                newestStamp = ComponentRevisionManager.NewestPublished(ref compRevTableAccessor, e.FirstChunkId, revisionIndex, out newestChunkId);
            }

            var becomesNewest = _commitStamp > newestStamp;
            // A destroy published before this revision, at a lower stamp: the entity is dead, and its destroy owns its index entries — it removes them by
            // the keys its cluster slot holds (VIS-09).
            var overTombstone = newestStamp != 0 && newestChunkId == 0;

            // VIS-12 (#674): an update that moves this entity's index entries or its HEAD says so first — its cluster's watermark, then its archetype's stamp
            // and count — so a reader older than it that finds either moved reads the cluster at its snapshot instead. A destroy's tombstone (no content)
            // moves neither: its slot is hidden with its bytes kept, and its entries' removal is announced by the destroy stamp (VIS-09). Folding it here sent
            // every older reader through a whole-cluster snapshot fill per destroyed cluster, ~8.5 µs each (#1247).
            if (becomesNewest && e.CurCompContentChunkId != 0 && (e.SlotIndexed || e.ClusterCopyPending))
            {
                // A key move as Phase A below decides one — conservative: a commit that landed since the read may have moved the keys (#1263).
                NoteVersionedUpdate(in e, e.SlotIndexed && !overTombstone && (e.IndexPending || newestChunkId != e.BaseCompChunkId));
            }

            // Phase A (#1262, #1263): the indexes and views, under the chain lock and before the stamp, so the index never lags a visible revision. They
            // move from the newest published value, not the one this transaction read: another update may have committed in between, and its publish moved
            // the entry off the read key — moving from it then found nothing and changed nothing, and the index kept the other writer's key for good. A
            // publish below the newest moves nothing, as Phase B copies nothing (AP-06); one over a tombstone neither — the destroy owns the entity's entries.
            // Readers of this entity wait out the index update, which the conflict-handler path already paid.
            //
            // A move, and the slot copy after it, serialized with a destroy of another commit, which reads the keys to remove from the slot (VIS-08,
            // VIS-09): by this chain lock, which the destroy's tombstone publish takes — or, for a chain the destroy did not tombstone (created after its
            // snapshot), which its index removal takes. A first supply's chain may not be in the entity's record yet, so a supply takes the slot's index
            // lock too. Either way a slot that no longer holds the entity alive moves nothing and copies nothing: its destroy removed the entries, and a
            // move from the key it removed missed and reindexed a dead entity (#1370). The slot lock on every move cost a key-moving update 3 %
            // (measured: a lock-prefixed CAS waits for the move's stores), and on every indexed update the unchanged-key one 4 %.
            var moves = e.SlotIndexed && becomesNewest && !overTombstone && (e.IndexPending || newestChunkId != e.BaseCompChunkId);
            var slotLocked = moves && e.Created;
            var live = !moves || (slotLocked ? LockLiveSlotIndexes(in e) : SlotHoldsEntityAlive(in e));
            try
            {
                if (moves && live)
                {
                    PublishClusterIndexes(in e, newestChunkId);
                    _dbe.PublishAfterIndexesProbe?.Invoke();
                }

                elementHandle.Publish(_commitStamp);   // THE publication act: the commit stamp replaces the owner's TSN, IsolationFlag cleared (VIS-04)
                _dbe.PublishUnderLockProbe?.Invoke();

                // The revision number, under the chain lock — a compaction rewrites the header too, and a lock-free increment raced it.
                if (!e.Created)
                {
                    header.CommitSequence++;
                }

                // Cluster Phase B: copy the committed HEAD value into the cluster slot (visible to bulk iteration) — under the lock, and only when this
                // revision is the newest published one by stamp (VIS-05). Two transactions updating one entity publish in either order of their stamps:
                // the later publish of the older stamp must not land in the slot. A writer still unpublished copies at its own publish if its stamp is the
                // higher. Nor over a tombstone, as Phase A: the destroy reads the keys to remove from the slot, and an update prepared before the destroy
                // and stamped after it copied keys the index never held there — the destroy removed those, missing its own (#1370).
                if (e.ClusterCopyPending && becomesNewest && !overTombstone && live)
                {
                    PublishClusterVersionedSlot(e);
                }
            }
            finally
            {
                if (slotLocked)
                {
                    _dbe._stateByRouting[e.ArchId].ClusterState.UnlockSlotIndexes(e.ClusterChunkId, e.SlotIndex);
                }
            }

            completed = true;
        }
        finally
        {
            // The lock taken above, always. The one PrepareComponent retained (handler path) only on a normal exit: on a throw the drain's cursor has not
            // passed this entry, and ReleaseRetainedPublishLocks — the abort path — releases it. Released here too, it was released twice, and the second
            // release's InvalidOperationException replaced the CommitPublishIncompleteException (AP-03).
            if (lockTaken || (e.LockHeld && completed))
            {
                header.Control.ExitExclusiveAccess();
                ComponentRevisionManager.EndChainLockHold(ref compRevTableAccessor, in ticket);
            }
        }
    }

    /// <summary>
    /// The handler path's wait for a spawn of another commit still publishing the entity (VIS-08), before PREPARE takes the entity's chain lock and retains
    /// it to the publish: held, the spawner — whose component publish runs before its spawns' — could wait for it while this commit's publish waited for the
    /// spawn's slot index lock. Bounded by the chain lock's own wait: a commit that holds another chain this spawner needs times out and rolls back, as a
    /// handler PREPARE waiting on a chain the other holds does (PublishComponent's remarks).
    /// </summary>
    private void AwaitSpawnPublished(long pk, ComponentInfo info, ref WaitContext wc)
    {
        var id = EntityId.FromRaw(pk);
        var es = _dbe._stateByRouting[id.ArchetypeId];
        var clusterState = es?.ClusterState;
        if (clusterState?.IndexSlots == null || es.EntityMap == null || SpawnedContains(id))
        {
            return;
        }

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta == null || !meta.IsClusterEligible)
        {
            return;
        }

        // The commit's cached accessors, which this PREPARE's cluster pass (PrepareClusterVersionedSlot) reads the same record through next.
        EnsureClusterCommitAccessors(id.ArchetypeId, es, info.ComponentTable.ComponentSegment, clusterState);
        byte* recordBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        if (!es.EntityMap.TryGet(id.EntityKey, recordBuf, ref _clusterCommitMapAccessor))
        {
            return;
        }

        if (!clusterState.WaitSlotIndexesUnlocked(ClusterEntityRecordAccessor.GetClusterChunkId(recordBuf), ClusterEntityRecordAccessor.GetSlotIndex(recordBuf),
                ref wc))
        {
            ThrowHelper.ThrowLockTimeout("RevisionChain/CommitConflict", TimeoutOptions.Current.RevisionChainLockTimeout);
        }
    }

    /// <summary>
    /// Takes the index lock of <paramref name="e"/>'s cluster slot (VIS-08) and says whether the slot still holds the entity alive
    /// (<see cref="ArchetypeClusterState.HoldsLive"/>). The caller releases the lock.
    /// </summary>
    private bool LockLiveSlotIndexes(in PublishEntry e)
    {
        _dbe._stateByRouting[e.ArchId].ClusterState.LockSlotIndexes(e.ClusterChunkId, e.SlotIndex);
        return SlotHoldsEntityAlive(in e);
    }

    /// <summary>Whether <paramref name="e"/>'s cluster slot still holds the entity alive (<see cref="ArchetypeClusterState.HoldsLive"/>).</summary>
    private bool SlotHoldsEntityAlive(in PublishEntry e)
    {
        var es = _dbe._stateByRouting[e.ArchId];
        EnsureClusterCommitAccessors(e.ArchId, es, e.Info.ComponentTable.ComponentSegment, es.ClusterState);
        return es.ClusterState.HoldsLive(_clusterCommitClusterAccessor.GetChunkAddress(e.ClusterChunkId), e.SlotIndex, EntityId.FromRaw(e.Pk));
    }

    /// <summary>
    /// Whether <paramref name="element"/> is this transaction's still-pending revision: isolated, and carrying the content chunk it committed — or, for a
    /// delete, no chunk and this transaction's TSN, the same identity <see cref="ComponentRevisionManager.FindRevisionIndexByChunkId"/> matches on.
    /// </summary>
    private bool IsThisTransactionsPendingEntry(in CompRevStorageElement element, int contentChunkId) =>
        element.IsolationFlag && (contentChunkId != 0 ? element.ComponentChunkId == contentChunkId : element.ComponentChunkId == 0 && element.TSN == TSN);

    /// <summary>
    /// Between two entries of a commit's publish — component entries, spawns, destroys, staged writes — and of the rollback of a commit that failed before
    /// its append (#831), while the cache is under pressure, every <see cref="EntityAccessor.PressuredEpochRefreshInterval"/> entries: moves the thread's
    /// epoch, with its warm accessors' (#1382), the transactions' pressured cadence (EP-03), and gives back the change set's dirty marks (#831). An entry
    /// keeps nothing it read for the next: the accessors it shares with the next re-resolve every address. What it wrote stays safe unmarked: a page
    /// registered as modified owes a write, which only a durable write discharges and which keeps it from eviction till then (PS-10), and the checkpoint
    /// collects it by that debt, not by its mark; a page still in an accessor's dirty slot is held by its live writer until registered.
    /// Held for the whole publish, the pin kept every page the other threads tagged meanwhile, for as long as the publish's back-pressure waits lasted
    /// (AP-03); the marks kept the publish's whole write set resident, and a publish larger than the cache — 10 000 updates of a component with three
    /// indexes into 8 MiB, 300 000 into 256 MiB — waited out its bound and stopped the engine. Every entry would bump the global epoch 1.3 million times a
    /// storm, each one sending every other thread's warm accessors down their cold path. Never under a live index cursor of this thread's (#1199): a
    /// commit made inside an index scan.
    /// </summary>
    private void RefreshBetweenCommitSteps()
    {
        if ((++_publishStepCount & (EntityAccessor.PressuredEpochRefreshInterval - 1)) == 0 && _dbe.MMF.UnderPressure
            && EntityAccessor.LiveIndexCursors == 0)
        {
            ChunkBasedSegment<PersistentStore>.RefreshWarmCacheEpoch(_epochManager.RefreshScope());
            _changeSet?.ReleaseDirtyMarks();
            Interlocked.Increment(ref PagedMMF.PublishStepRefreshes);
        }
    }

    /// <summary>
    /// Drains the prepared component publish descriptors (AP-01 PUBLISH pass). Runs after the WAL Append. Also releases any retained handler locks.
    /// </summary>
    private void PublishPreparedComponents()
    {
        // AP-01 [fatal][silent]: no change may be made visible before its records are appended. This guard trips loudly in Debug if a future change
        // reorders the publish phase ahead of AppendToWal.
        Debug.Assert(_appendPhaseEnteredThisCommit, "AP-01 violation: PUBLISH reached before the WAL Append phase ran");

        if (_publishEntries == null || _publishEntries.Count == 0)
        {
            return;
        }

        // Advance the cursor only after each entry is FULLY published (PublishComponent releases its retained lock last), so a mid-drain throw leaves
        // [_publishDrainIndex, Count) for the abort path to release — never double-releasing an already-published entry's lock.
        //
        // Batch mode, as PREPARE's pass: the index moves made here since #1262 (Phase A) return their B+Tree warm accessors without a CommitChanges each,
        // and one flush per 1 024 entries, and at the end, registers their dirty pages. The end is before CK-13's floor is withdrawn, so a checkpoint
        // that passes these records collects those pages. Under pressure the marks are given back between entries (RefreshBetweenCommitSteps, #831).
        ChunkBasedSegment<PersistentStore>.EnterBatchMode();
        try
        {
            while (_publishDrainIndex < _publishEntries.Count)
            {
                PublishComponent(_publishEntries[_publishDrainIndex]);
                _publishDrainIndex++;
                RefreshBetweenCommitSteps();
                if ((_publishDrainIndex & 0x3FF) == 0)
                {
                    ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                    ChunkBasedSegment<PersistentStore>.EnterBatchMode();
                }
            }
        }
        finally
        {
            ChunkBasedSegment<PersistentStore>.ExitBatchMode();
        }

        // Cluster Phase-B may have created lazy-cached accessors during this drain — release them.
        DisposeClusterCommitAccessors();
        _publishEntries.Clear();
        _publishDrainIndex = 0;
    }

    /// <summary>
    /// Releases any per-entity revision-chain locks retained by <see cref="PrepareComponent"/> that have not yet been published. Called on the commit
    /// abort/exception path so a failure between PREPARE and PUBLISH never orphans a held lock. Idempotent: clears <see cref="_publishEntries"/>.
    /// </summary>
    private void ReleaseRetainedPublishLocks()
    {
        if (_publishEntries == null || _publishEntries.Count == 0)
        {
            return;
        }

        // Release only the entries not yet published this commit ([_publishDrainIndex, Count)); entries below the cursor already released their lock in
        // PublishComponent. This makes lock release exactly-once across the success path, the pre-publish throw, and a mid-drain throw.
        for (int i = _publishDrainIndex; i < _publishEntries.Count; i++)
        {
            var e = _publishEntries[i];
            if (e.LockHeld)
            {
                ref var lockHeader = ref e.Info.CompRevTableAccessor.GetChunk<CompRevStorageHeader>(e.FirstChunkId);
                lockHeader.Control.ExitExclusiveAccess();
                ComponentRevisionManager.EndChainLockHold(ref e.Info.CompRevTableAccessor, in e.LockTicket);
            }
        }

        _publishEntries.Clear();
        _publishDrainIndex = 0;
    }

    /// <summary>
    /// PREPARE half of the cluster commit (AP-01): resolves the cluster-slot coordinates for the publish, and says what it owes. Both the index and view
    /// reconcile (Phase A, <see cref="PublishClusterIndexes"/>) and the HEAD→cluster-slot copy (Phase B, <see cref="PublishClusterVersionedSlot"/>) run
    /// after the WAL Append: Phase A here would survive an abort before the append, which reverts revisions only (#1262). Single EntityMap lookup; the
    /// resolved coordinates are returned so publish does not repeat it. <paramref name="copyToCluster"/> is false for spawns (PlaceSpawns handles
    /// cluster copy for those).
    /// </summary>
    private void PrepareClusterVersionedSlot(long pk, ArchetypeMetadata meta, ComponentInfo.CompRevInfo compRevInfo, int readCompChunkId, ComponentTable table,
        int componentTypeId, bool copyToCluster, ref ChunkAccessor<PersistentStore> compRevAccessor, int firstChunkId, bool lockHeld,
        out bool clusterCopyPending, out bool indexPending, out bool slotIndexed, out int baseCompChunkId, out int outClusterChunkId, out byte outSlotIndex,
        out byte outCompSlot)
    {
        clusterCopyPending = false;
        indexPending = false;
        slotIndexed = false;
        baseCompChunkId = readCompChunkId;
        outClusterChunkId = 0;
        outSlotIndex = 0;
        outCompSlot = 0;

        var archId = meta.ArchetypeId;
        var es = _dbe._archetypeStates[archId];
        var clusterState = es.ClusterState;
        bool hasIndexes = clusterState.IndexSlots != null;
        bool hasSpatial = clusterState.SpatialSlot.HasSpatialIndex;

        if (!hasIndexes && !hasSpatial && !copyToCluster)
        {
            return; // Nothing to do
        }

        // O(1) component slot lookup via archetype's typeId→slot table
        if (!meta.TryGetSlot(componentTypeId, out byte compSlotByte))
        {
            return;
        }
        int compSlot = compSlotByte;
        outCompSlot = compSlotByte;

        // Lazy-cache accessors by archetype — reused across all entities in the same commit batch
        EnsureClusterCommitAccessors(archId, es, table.ComponentSegment, clusterState);

        // Read entity's cluster location from EntityMap (once)
        int recordSize = meta._entityRecordSize;
        byte* recordBuf = stackalloc byte[recordSize];

        var entityId = EntityId.FromRaw(pk);
        // The EntityMap is keyed on the bare 48-bit key; view deltas are keyed on the full EntityId (#660). Both are needed here.
        long entityKey = entityId.EntityKey;
        if (!es.EntityMap.TryGet(entityKey, recordBuf, ref _clusterCommitMapAccessor))
        {
            return;
        }

        outClusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(recordBuf);
        outSlotIndex = ClusterEntityRecordAccessor.GetSlotIndex(recordBuf);

        // Phase B (HEAD→cluster slot copy) is required iff a committed value exists and this is not a spawn — deferred to PublishClusterVersionedSlot.
        clusterCopyPending = copyToCluster && compRevInfo.CurCompContentChunkId != 0;

        // Phase A — the archetype's B+Tree indexes and the views over them — is owed when an index covers this component and the write moves one of its
        // keys: an insert, a delete, or a changed field. The publish does it. The unchanged-field guard (#665) runs here, where it always ran, so a write
        // that leaves every indexed field alone costs the publish nothing.
        if (hasIndexes && (clusterState.IndexedSlots & (1 << compSlot)) != 0)
        {
            slotIndexed = true;

            // The keys move from the newest committed revision, which a last-writer-wins commit landing after this transaction's read made another one
            // (#1263): measured against it, the write owes the move, and the unique-key check before the append covers the key it moves to. The publish
            // re-reads it under the chain's lock for a commit landing after this one. Committed revisions newer than the read stay allocated while this
            // transaction lives: cleanup keeps every revision from the one its snapshot sees.
            if (readCompChunkId != 0)
            {
                var newestStamp = lockHeld
                    ? ComponentRevisionManager.NewestPublished(ref compRevAccessor, firstChunkId, -1, out var newestChunkId)
                    : ComponentRevisionManager.NewestPublishedUnlocked(ref compRevAccessor, firstChunkId, this, out newestChunkId);
                if (newestStamp != 0 && newestChunkId != 0)
                {
                    baseCompChunkId = newestChunkId;
                }
            }

            if (compRevInfo.CurCompContentChunkId == 0 || baseCompChunkId == 0)
            {
                indexPending = true;
            }
            else
            {
                var overhead = table.ComponentOverhead;
                indexPending = AnyIndexedFieldChanged(clusterState, compSlot,
                    _clusterCommitContentAccessor.GetChunkAddress(baseCompChunkId) + overhead,
                    _clusterCommitContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead);
            }
        }

        // Phase B (HEAD→cluster slot copy) is deferred to PublishClusterVersionedSlot (AP-01). The cluster dirty bit it sets is what later drives
        // DetectClusterMigrations at tick fence (spatial per-cell index — same deferred-update pattern as SV cluster spatial, Issue #230 Phase 3).
    }

    /// <summary>Whether a write from <paramref name="oldComp"/> to <paramref name="newComp"/> changes a field an index of the slot covers — the guard
    /// <see cref="ReconcileClusterIndexAndViews"/> applies field by field, so a write it would skip entirely owes the publish nothing.</summary>
    private static bool AnyIndexedFieldChanged(ArchetypeClusterState clusterState, int compSlot, byte* oldComp, byte* newComp)
    {
        var ixSlots = clusterState.IndexSlots;
        for (var ixs = 0; ixs < ixSlots.Length; ixs++)
        {
            ref var ixSlot = ref ixSlots[ixs];
            if (ixSlot.Slot != compSlot)
            {
                continue;
            }

            var fields = ixSlot.Fields;
            for (var fi = 0; fi < fields.Length; fi++)
            {
                ref var field = ref fields[fi];
                if (!FieldBytesEqual(oldComp + field.FieldOffset, newComp + field.FieldOffset, field.FieldSize))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Phase A of the cluster commit, in its publish (#1262): moves the entity's entries in the archetype's B+Tree indexes from the value in
    /// <paramref name="oldChunkId"/> (0 for none) — the newest published revision's (#1263) — to the committing one's, and appends the matching view
    /// deltas. Values come from the content chunks — the cluster slot is Phase B's, and not copied yet. Runs before the revision is stamped, so the index
    /// never lags a visible revision.
    /// </summary>
    /// <remarks>
    /// In PREPARE this survived an abort before the append — the rollback reverts revisions only — leaving the index on a key no commit made and the
    /// views with a delta no commit made. A unique key it would collide on is rejected before the append (<c>ValidatePublishBeforeAppend</c>);
    /// <see cref="RejectUniqueIndexCollision"/> here is the backstop for a concurrent commit that took the key in between (#1258).
    /// </remarks>
    private void PublishClusterIndexes(in PublishEntry e, int oldChunkId)
    {
        var es = _dbe._stateByRouting[e.ArchId];
        var clusterState = es.ClusterState;
        var table = e.Info.ComponentTable;
        EnsureClusterCommitAccessors(e.ArchId, es, table.ComponentSegment, clusterState);

        byte* newComp = e.CurCompContentChunkId != 0
            ? _clusterCommitContentAccessor.GetChunkAddress(e.CurCompContentChunkId) + table.ComponentOverhead
            : null;
        byte* oldComp = oldChunkId != 0 ? _clusterCommitContentAccessor.GetChunkAddress(oldChunkId) + table.ComponentOverhead : null;
        if (oldComp == null && e.Created)
        {
            // A component supplied mid-life to an entity whose spawn left it absent (STAGE-02). Its slot was indexed at the spawn under the bytes it held,
            // which nothing has written since — the cluster copy comes after this — so the move starts there. Added instead, the spawn's entry outlived
            // the supply, under a key the entity never had (#1259, QENABLE-01).
            var layout = clusterState.Layout;
            oldComp = _clusterCommitClusterAccessor.GetChunkAddress(e.ClusterChunkId) + layout.ComponentOffset(e.CompSlot)
                      + e.SlotIndex * layout.ComponentSize(e.CompSlot);
        }

        ReconcileClusterIndexAndViews(es, clusterState, e.CompSlot, e.ClusterChunkId, e.ClusterChunkId * 64 + e.SlotIndex, EntityId.FromRaw(e.Pk), oldComp,
            newComp);
    }

    /// <summary>The update's cluster watermark, its archetype's update stamp and count, at this commit's stamp (VIS-12).</summary>
    private void NoteVersionedUpdate(in PublishEntry e, bool keyMoved) =>
        _dbe._stateByRouting[e.ArchId].ClusterState.NoteUpdatePublished(e.ClusterChunkId, e.SlotIndex, _commitStamp, keyMoved);

    /// <summary>
    /// PUBLISH half of the cluster commit (AP-01): copies the committed HEAD value into the entity's cluster slot (visible to bulk iteration) and marks
    /// the cluster chunk dirty. Non-throwing — uses the coordinates resolved by <see cref="PrepareClusterVersionedSlot"/> (no EntityMap re-lookup) and
    /// re-establishes the lazy-cached cluster accessors (the PREPARE pass disposes them per component type). MUST run only after the WAL Append.
    /// </summary>
    private void PublishClusterVersionedSlot(in PublishEntry e)
    {
        var archId = e.ArchId;
        var es = _dbe._stateByRouting[archId];
        var clusterState = es.ClusterState;
        var table = e.Info.ComponentTable;

        // Re-establish lazy-cached accessors by archetype — reused across consecutive same-archetype entries in the publish drain.
        EnsureClusterCommitAccessors(archId, es, table.ComponentSegment, clusterState);

        var layout = clusterState.Layout;
        byte* srcAddr = _clusterCommitContentAccessor.GetChunkAddress(e.CurCompContentChunkId);
        byte* clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(e.ClusterChunkId, true);

        int compSize = layout.ComponentSize(e.CompSlot);
        byte* dstSlot = clusterBase + layout.ComponentOffset(e.CompSlot) + e.SlotIndex * compSize;
        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + table.ComponentOverhead, (uint)compSize);

        // Three-arg overload, not the fail-safe two-arg one: e.CompSlot is right there, and the component-less overload poisons WrittenSlotUnion to
        // AllSlotsWritten, which tells the fence to emit every durable column of the cluster and undoes #559 §4.5's narrowing for the whole archetype
        // (review M31). Over-emission is not a wrong answer, which is why nothing caught it — see VersionedPublish_RecordsTheComponentSlot_NotAllSlots.
        clusterState.SetDirty(e.ClusterChunkId, e.SlotIndex, e.CompSlot);
    }

    private void DisposeClusterCommitAccessors()
    {
        if (_hasClusterCommitAccessors)
        {
            _clusterCommitMapAccessor.Dispose();
            _clusterCommitContentAccessor.Dispose();
            _clusterCommitClusterAccessor.Dispose();
            _clusterCommitIndexAccessor.Dispose();
            _clusterCommitIndexAccessorS64.Dispose();
            _clusterCommitContentSegment = null;
            _hasClusterCommitAccessors = false;
        }
    }

    /// <summary>
    /// Establishes the per-archetype commit accessors, reused across every entity of that archetype in the batch. Idempotent for the archetype already
    /// cached; switching archetypes disposes the previous set first.
    /// </summary>
    /// <remarks>
    /// One method rather than the three byte-identical blocks it replaced (#665) — the set has to stay in step with
    /// <see cref="DisposeClusterCommitAccessors"/>, and three copies is three chances for it not to. An index segment the archetype does not own leaves its
    /// accessor <c>default</c>, which disposes as a no-op.
    /// </remarks>
    private void EnsureClusterCommitAccessors(ushort archId, ArchetypeEngineState es, ChunkBasedSegment<PersistentStore> contentSegment,
        ArchetypeClusterState clusterState)
    {
        if (_hasClusterCommitAccessors && _clusterCommitArchId == archId)
        {
            // Archetype unchanged, so the map / cluster / index accessors stand. The CONTENT accessor may not: it is scoped to the component, and a drain
            // covering several Versioned components of one archetype passes a different segment each time. Swapping just that one keeps the common case
            // (consecutive entries of the same component) free while making the mixed case correct.
            if (!ReferenceEquals(_clusterCommitContentSegment, contentSegment))
            {
                _clusterCommitContentAccessor.Dispose();
                _clusterCommitContentAccessor = contentSegment.CreateChunkAccessor();
                _clusterCommitContentSegment = contentSegment;
            }

            return;
        }

        DisposeClusterCommitAccessors();
        _clusterCommitMapAccessor = es.EntityMap.Segment.CreateChunkAccessor();
        _clusterCommitContentAccessor = contentSegment.CreateChunkAccessor();
        _clusterCommitContentSegment = contentSegment;
        // The publish writes through this accessor — the committed Versioned HEAD (PublishClusterVersionedSlot), a Commit-discipline staged value
        // (PublishStagedEntry) and an AllowMultiple element id (ReconcileClusterIndexAndViews) — so it registers its pages with the transaction's ChangeSet,
        // like the index accessors below (AP-04). It had none: MarkSlotDirty then only toggled ActiveChunkWriters, the page never owed a write, and nothing
        // ever wrote it — not the checkpoint, not the close's flush, not the fence (#559 stopped emitting Versioned slots). A clean reopen then trusted the
        // stale HEAD (CS-03) and Path-B scans returned the old value while point reads, which walk the chain, returned the new one (#1159).
        _clusterCommitClusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor(_changeSet);
        _clusterCommitIndexAccessor = clusterState.IndexSegment?.CreateChunkAccessor(_changeSet) ?? default;
        _clusterCommitIndexAccessorS64 = clusterState.IndexSegmentString64?.CreateChunkAccessor(_changeSet) ?? default;
        _clusterCommitArchId = archId;
        _hasClusterCommitAccessors = true;
    }

    /// <summary>
    /// The cached commit accessor for whichever per-archetype index segment <paramref name="segment"/> is. Returned by reference — the B+Tree mutation
    /// methods take <c>ref ChunkAccessor</c> and record page state in it.
    /// </summary>
    /// <remarks>
    /// Passing an accessor built on the other segment resolves node chunks at the wrong stride and corrupts neighbouring nodes (#658), so this is a
    /// reference comparison rather than a guess from the field type. When the archetype has no String64 segment the comparison is against <c>null</c>, which
    /// a live tree's segment never equals — no extra guard needed.
    /// </remarks>
    private ref ChunkAccessor<PersistentStore> ClusterCommitIndexAccessor(ArchetypeClusterState clusterState, ChunkBasedSegment<PersistentStore> segment)
        => ref ReferenceEquals(segment, clusterState.IndexSegmentString64) ? ref _clusterCommitIndexAccessorS64 : ref _clusterCommitIndexAccessor;

    /// <summary>
    /// Whether <paramref name="size"/> bytes at <paramref name="a"/> and <paramref name="b"/> are identical — the unchanged-field guard's test.
    /// </summary>
    /// <remarks>
    /// Scalar compares for the widths an index key actually has, with <see cref="MemoryExtensions.SequenceEqual{T}(ReadOnlySpan{T},ReadOnlySpan{T})"/> only
    /// for the <c>String64</c> tail. Measured, not assumed: going through spans for every width cost ~2.9 % on the path where the key DOES change (95.4 µs
    /// against a 92.7 µs baseline, reproduced twice) — the guard has to pay for itself on both sides, not just the one it optimises. The comparison must
    /// stay width-exact: an 8-byte shortcut for a 64-byte key silently skips a real move (#665).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FieldBytesEqual(byte* a, byte* b, int size) => size switch
    {
        1 => *a == *b,
        2 => *(short*)a == *(short*)b,
        4 => *(int*)a == *(int*)b,
        8 => *(long*)a == *(long*)b,
        _ => new ReadOnlySpan<byte>(a, size).SequenceEqual(new ReadOnlySpan<byte>(b, size)),
    };
    /// <summary>
    /// Refuses to write a key into a UNIQUE per-archetype index when a DIFFERENT entity already holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A unique B+Tree stores one value per key, and the underlying primitive is <c>AddOrUpdate</c> — so admitting a duplicate does not fail, it silently
    /// OVERWRITES the incumbent. The loser then exists in the data but is unreachable through the index, and the two disagree permanently: a scan of the data
    /// finds it, an index lookup does not. That is the defect behind #675, and it is why the index could be wrong while ~4 000 tests passed — queries take the
    /// SoA scan, which reads component data and never consults the tree.
    /// </para>
    /// <para>
    /// Rejecting is the only honest option of the three available. Silently overwriting corrupts. Auto-promoting the field to <c>AllowMultiple</c> would change
    /// a declared constraint at run time and cost every unique index 4 bytes per entity plus a buffer indirection. So the engine enforces what the attribute
    /// promises: <c>[Index]</c> without <c>AllowMultiple</c> is documented as "one entity per key" (<c>IndexAttribute.AllowMultiple</c>), and a field whose
    /// values genuinely collide — a health total, a category, a bucket — must say <c>[Index(AllowMultiple = true)]</c>.
    /// </para>
    /// <para>
    /// Cost is one extra optimistic descent per unique-index write, and it is a read followed by a write with no lock spanning the two — correct on a path
    /// where nothing else moves keys in the same tree concurrently, which is the two commit-path callers' situation today. <b>The tick-fence shadow drain
    /// no longer calls this first (#886):</b> it reads
    /// <c>BTree.Move</c>'s own verdict, which is taken under the leaf's write latch and so survives a parallel drain, and calls this only on the cold path
    /// where <c>Move</c> returned <c>false</c>, to tell "old key absent" from "new key held by another" and raise the message below for the second.
    /// </para>
    /// </remarks>
    internal static void RejectUniqueIndexCollision<TStore>(ref ClusterIndexField<TStore> field, byte* newKeyPtr, int clusterLocation,
        ref ChunkAccessor<TStore> idxAccessor)
        where TStore : struct, IPageStore
    {
        var existing = field.Index.TryGet(newKeyPtr, ref idxAccessor);
        if (!existing.IsSuccess || existing.Value == clusterLocation)
        {
            return;
        }

        ThrowHelper.ThrowInvalidOp(
            $"unique index collision: cluster location {clusterLocation} is being indexed under a key already held by the entity at cluster location "
            + $"{existing.Value}. A unique index stores one entity per key, so writing this would make the incumbent unreachable through the index while it "
            + "remains present in the data. Declare the field [Index(AllowMultiple = true)] if its values are meant to repeat. See "
            + "https://github.com/Log2n-io/Typhon/issues/675.");
    }


    /// <summary>
    /// Reconciles the per-archetype B+Tree index(es) and view delta buffers for one component slot of one cluster entity, given the field-value
    /// base pointers BEFORE (<paramref name="oldComp"/>) and AFTER (<paramref name="newComp"/>) the change (each already past the component overhead;
    /// null means absent). Move when both are present, Add on insert, Remove on delete. Shared by the Versioned cluster commit
    /// (<see cref="PrepareClusterVersionedSlot"/>, value pointers into content chunks) and the Commit-discipline staged publish
    /// (<see cref="PublishStagedCommitWrites"/>, old = cluster HEAD, new = staging buffer).
    /// </summary>
    private void ReconcileClusterIndexAndViews(ArchetypeEngineState es, ArchetypeClusterState clusterState, int compSlot, int clusterChunkId,
        int clusterLocation, EntityId entityId, byte* oldComp, byte* newComp)
    {
        // #711: an entity this transaction also DESTROYS has no index entry to maintain — it has one to remove, and the destroy path owns that. Moving its
        // key here would insert the post-move key onto a slot that is about to be released, and the removal (whether it runs here or at the tick fence) keys
        // off the PRE-move value, so the moved-to entry survives with nothing pointing at it. Both callers are commit-scoped and both run in the same commit
        // as the destroy: PrepareClusterVersionedSlot before FlushEcsPendingOperations, PublishStagedCommitWrites after it. Guarding the shared method rather
        // than each caller keeps the two from drifting.
        if (_pendingDestroys != null && _pendingDestroys.Contains(entityId))
        {
            return;
        }

        var layout = clusterState.Layout;
        int slotIndex = clusterLocation & 63;

        // The caller is serialized with the spawn and the destroys of this entity (VIS-08): an entity another commit spawned is visible from its card before
        // its PUBLISH writes its entries and tails, and moved then, the move missed, reindexed, and the spawn's PUBLISH added its own entry — two entries,
        // one stale (#1370).
        // Cluster chunk base holding the per-entity index element-id tail slot for AllowMultiple fields. Resolved lazily on the first AllowMultiple field
        // (fetching it forWrite marks the cluster chunk dirty — only wanted when we actually write an element id back). Both callers of this method have just
        // (re)established _clusterCommitClusterAccessor for this archetype before calling.
        byte* clusterBase = null;

        // One shared write per commit instead of one per indexed field (review M4). Accumulated locally and added once below: the counter is read by the
        // StatisticsWorker thread, so every increment is a store other cores may be watching, and N of them per commit bought nothing over one.
        var mutations = 0;

        for (int ixs = 0; ixs < clusterState.IndexSlots.Length; ixs++)
        {
            ref var ixSlot = ref clusterState.IndexSlots[ixs];
            if (ixSlot.Slot != compSlot)
            {
                continue;
            }

            for (int fi = 0; fi < ixSlot.Fields.Length; fi++)
            {
                ref var field = ref ixSlot.Fields[fi];

                // Unchanged-field guard (#665). An update that leaves an indexed field alone — the common shape: index the classification, write the value
                // that churns — used to pay two optimistic B+Tree descents, a leaf write-lock and a dirtied index page per field per commit, plus a dirtied
                // CLUSTER page for an AllowMultiple field, to move a key onto itself. The legacy path has short-circuited here since it was written
                // (IndexMaintainer.cs:29) and so does the flat twin below (ReconcileFlatIndexAndViews); only the per-archetype path never got it.
                //
                // Compared as raw bytes over FieldSize, NOT through KeyBytes8: a Versioned component may index a String64 field (64 bytes), and comparing
                // the first 8 would silently skip a real key move. Skipping the view notification with it is correct — nothing changed — and so is skipping
                // the zone-map Widen, since an unchanged value is already inside the bounds that admitted it.
                if (newComp != null && oldComp != null && FieldBytesEqual(oldComp + field.FieldOffset, newComp + field.FieldOffset, field.FieldSize))
                {
                    continue;
                }

                ref var idxAccessor = ref ClusterCommitIndexAccessor(clusterState, field.Index.Segment);
                mutations++;   // past the guard, so this is real tree work (#665)

                // A group key leaving its value (14 § 5): logged BEFORE the index moves, so a replication gather that misses the entry in the tree finds it here.
                if (oldComp != null)
                {
                    clusterState.GroupKeyMoves?.Note(compSlot, field.FieldOffset, clusterLocation, oldComp + field.FieldOffset);
                }

                if (newComp != null && oldComp != null)
                {
                    // Update: move this entity's entry from the old key to the new key.
                    if (field.AllowMultiple)
                    {
                        // The leaf value for an AllowMultiple index is a VSBS buffer-root id, NOT the clusterLocation — the entity's location lives
                        // inside the buffer. Plain Move() is unique-only: it overwrites the leaf value with the raw clusterLocation, which the read path
                        // then treats as a buffer id (finds nothing → the entity vanishes from every scan of that key). Use MoveValue with this entity's
                        // element id (kept in the cluster tail slot exactly as spawn/destroy do) and write the returned new element id back so a later
                        // update/destroy still targets the right buffer entry. Mirrors ReconcileFlatIndexAndViews / IndexMaintainer.UpdateIndices.
                        if (clusterBase == null)
                        {
                            clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(clusterChunkId, true);
                        }
                        int* elementIdPtr = (int*)(clusterBase + layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex));
                        *elementIdPtr = field.Index.MoveValueOrReindex(oldComp + field.FieldOffset, newComp + field.FieldOffset, *elementIdPtr,
                            clusterLocation, ref idxAccessor);
                    }
                    else
                    {
                        RejectUniqueIndexCollision(ref field, newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                        field.Index.Move(oldComp + field.FieldOffset, newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                    }
                }
                else if (newComp != null)
                {
                    // Insert (first commit after spawn): Add returns the per-entity element id for an AllowMultiple index, which must be recorded in the
                    // cluster tail slot so destroy/update can target this entity's entry (mirrors PlaceSpawns).
                    if (!field.AllowMultiple)
                    {
                        RejectUniqueIndexCollision(ref field, newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                    }

                    int elementId = field.Index.Add(newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                    if (field.AllowMultiple)
                    {
                        if (clusterBase == null)
                        {
                            clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(clusterChunkId, true);
                        }
                        *(int*)(clusterBase + layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex)) = elementId;
                    }
                }
                else if (oldComp != null)
                {
                    // Delete: for an AllowMultiple index, RemoveValue removes only this entity's (key, clusterLocation) entry — Remove(key) would wipe the
                    // whole buffer and drop siblings sharing the value (the same rule the cluster destroy path follows).
                    if (field.AllowMultiple)
                    {
                        if (clusterBase == null)
                        {
                            clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(clusterChunkId, true);
                        }
                        int elementId = *(int*)(clusterBase + layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex));
                        field.Index.RemoveValue(oldComp + field.FieldOffset, elementId, clusterLocation, ref idxAccessor);
                    }
                    else
                    {
                        field.Index.Remove(oldComp + field.FieldOffset, out _, ref idxAccessor);
                    }
                }

                // Widen zone map with new value
                if (newComp != null)
                {
                    field.ZoneMap?.Widen(clusterChunkId, newComp + field.FieldOffset);
                }

                // Notify views of index change (delta buffer for incremental views)
                var viewTable = es.SlotToComponentTable[ixSlot.Slot];
                var views = viewTable.ViewRegistry.GetViewsForField(fi);
                // Hoisted out of the per-view loop: a [ThreadStatic] read is a TLS-base helper call, and this is the engine's busiest publisher.
                var appendHook = QueryPathProbe.PrePublishAppendHook;
                for (int v = 0; v < views.Length; v++)
                {
                    var reg = views[v];
                    if (reg.DeltaBuffer.IsClosed)
                    {
                        continue;
                    }

                    // The #864 window: everything below writes through reg.DeltaBuffer's raw pointers, and the IsDisposed check above is a filter,
                    // never a guarantee. A verifier disposes the view from here to prove the buffer is still mapped when the write lands.
                    appendHook?.Invoke();

                    if (newComp != null && oldComp != null)
                    {
                        // Move: emit old and new keys
                        var oldKey = KeyBytes8.FromPointer(oldComp + field.FieldOffset, field.FieldSize);
                        var newKey = KeyBytes8.FromPointer(newComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)(fi & 0x3F);
                        reg.DeltaBuffer.TryAppend(entityId, oldKey, newKey, ViewDeltaTag, flags, reg.ComponentTag);
                    }
                    else if (newComp != null)
                    {
                        // Add: isCreation flag
                        var newKey = KeyBytes8.FromPointer(newComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)((fi & 0x3F) | 0x40); // isCreation
                        reg.DeltaBuffer.TryAppend(entityId, default, newKey, ViewDeltaTag, flags, reg.ComponentTag);
                    }
                    else if (oldComp != null)
                    {
                        // Remove: isDeletion flag
                        var oldKey = KeyBytes8.FromPointer(oldComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)((fi & 0x3F) | 0x80); // isDeletion
                        reg.DeltaBuffer.TryAppend(entityId, oldKey, default, ViewDeltaTag, flags, reg.ComponentTag);
                    }
                }
            }
            break; // Found the matching index slot
        }

        clusterState.MutationsSinceRebuild += mutations;
    }

    /// <summary>
    /// PUBLISH pass for Commit-discipline (SingleVersion) staged writes (issue #392, Variant A). Runs AFTER the WAL Append (AP-01). For each staged
    /// (component, entity): re-resolves the cluster slot, reconciles the exact B+Tree index (old key from the still-unpublished HEAD, new key from the
    /// staged slot — matching Versioned, CM-05/AC-11) and notifies views, copies the staged bytes into the cluster HEAD (the visibility act), then marks
    /// the slot dirty (drives spatial migration at the fence and a benign last-writer-wins fence re-emit — CM-03). Spatial / zone maps stay fence-batched
    /// for all modes (decision #31). No-op when nothing was staged. Commit discipline currently targets cluster-eligible archetypes only.
    /// </summary>
    /// <summary>Benchmark-only hook (#392 AC-5): runs just the staged-commit PUBLISH pass on an already-staged, not-yet-committed transaction, so the
    /// per-component publish cost can be measured in isolation from BUILD/APPEND/WAIT. Idempotent for non-indexed components without collections
    /// (re-memcpy + re-SetDirty); a second publish of a collection gives HEAD's reference back twice.</summary>
    internal void PublishStagedForBenchmark() => PublishStagedCommitWrites();

    private void PublishStagedCommitWrites()
    {
        if (_commitStagingBuffer == null)
        {
            return;
        }

        foreach (var kvp in _componentInfos)
        {
            var info = kvp.Value;
            if (info.CommitStaged == null || info.ComponentTable.StorageMode != StorageMode.SingleVersion)
            {
                continue;
            }

            foreach (var staged in info.CommitStaged)
            {
                // An entity this commit destroys is skipped, as the validation before the append skips it (CheckStagedUniqueKeys): FlushPendingDestroys
                // released its slot, and a concurrent spawn may hold it now (#1261).
                if (_pendingDestroys != null && _pendingDestroys.Contains(EntityId.FromRaw(staged.Key)))
                {
                    continue;
                }

                PublishStagedEntry(info, staged.Key, staged.Value);
                RefreshBetweenCommitSteps();
            }
        }

        DisposeClusterCommitAccessors();
    }

    /// <summary>Publishes one Commit-discipline staged write to its HEAD (index reconcile → HEAD memcpy → dirty mark), dispatching to the cluster SoA
    /// slot or, for a non-cluster archetype, the entity's content chunk. Uses the HEAD location captured at stage time (<see cref="ComponentInfo.StagedSlot"/>)
    /// — no EntityMap re-lookup (the writing tx never relocates the entity it is writing). See <see cref="PublishStagedCommitWrites"/>.</summary>
    private void PublishStagedEntry(ComponentInfo info, long pk, ComponentInfo.StagedSlot stagedSlot)
    {
        var componentTypeId = info.ComponentTypeId;
        var entityId = EntityId.FromRaw(pk);
        var archId = entityId.ArchetypeId;
        var meta = _dbe.GetMetaByRouting(archId);
        var es = _dbe._stateByRouting[archId];
        if (!meta.TryGetSlot(componentTypeId, out byte compSlotByte))
        {
            return;
        }
        int compSlot = compSlotByte;
        byte* staged = _commitStagingBuffer + stagedSlot.Offset;
        var clusterState = es?.ClusterState;

        // The flat publish this used to dispatch to is gone with the per-ComponentTable home (#629). Everything below assumes a cluster, so a null ClusterState
        // must fail here rather than NRE three lines down inside EnsureClusterCommitAccessors.
        if (clusterState == null || !meta.IsClusterEligible)
        {
            ThrowHelper.ThrowInvalidOp(
                $"Commit-discipline publish for archetype '{meta?.Name}' found no cluster state. Every archetype is cluster-backed since #629, so there is no "
                + "flat HEAD to publish to.");
        }

        // Lazy-cache the per-archetype cluster accessor (reused across consecutive same-archetype staged entries; map/content accessors kept for parity
        // with the Versioned publish that shares these fields).
        EnsureClusterCommitAccessors(archId, es, es.SlotToComponentTable[compSlot].ComponentSegment, clusterState);

        // Coords captured at stage time — no per-component EntityMap re-lookup (Location = clusterChunkId*64 + slotIndex).
        int clusterLocation = stagedSlot.Location;
        int clusterChunkId = clusterLocation >> 6;
        byte slotIndex = (byte)(clusterLocation & 63);

        var layout = clusterState.Layout;
        byte* clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(clusterChunkId, true);
        int compSize = layout.ComponentSize(compSlot);
        byte* headPtr = clusterBase + layout.ComponentOffset(compSlot) + slotIndex * compSize;

        // Exact-index reconcile BEFORE the HEAD memcpy: old key still lives in the HEAD slot, new key in the staged slot (CM-05/AC-11). Not for an entity
        // whose SingleVersion keys a TickFence writer recorded this tick: the tree holds those, not HEAD's, and the fence moves the entry from there to
        // whatever HEAD holds then (#1339). A move from HEAD here would miss the tree's key, and the fence's from the recorded one would find the entry
        // already moved. The entity is in the TickFence window already (QFENCE).
        //
        // Under the slot's index lock (VIS-08) with the HEAD copy, as an update's move: a destroy of another commit reads the keys to remove from HEAD. A slot
        // that no longer holds the entity alive takes nothing — its destroy removed the entries, and a released slot may hold another entity since — and the
        // staged value's collection references are given back instead of HEAD's.
        var slotLocked = clusterState.IndexSlots != null;
        if (slotLocked)
        {
            clusterState.LockSlotIndexes(clusterChunkId, slotIndex);
        }

        try
        {
            if (slotLocked && !clusterState.HoldsLive(clusterBase, slotIndex, entityId))
            {
                ReleaseStagedCollections(info.ComponentTable, staged);
                return;
            }

            if (slotLocked && !clusterState.SvKeysRecorded(clusterLocation))
            {
                ReconcileClusterIndexAndViews(es, clusterState, compSlot, clusterChunkId, clusterLocation, entityId, headPtr, staged);
            }

            PublishStagedValue(info, clusterState, compSlot, clusterChunkId, slotIndex, headPtr, staged, compSize);
        }
        finally
        {
            if (slotLocked)
            {
                clusterState.UnlockSlotIndexes(clusterChunkId, slotIndex);
            }
        }
    }

    /// <summary>The staged value's collection references given back: a staged write <see cref="PublishStagedEntry"/> drops, its entity destroyed.</summary>
    private static void ReleaseStagedCollections(ComponentTable table, byte* staged)
    {
        if (!table.HasCollections)
        {
            return;
        }

        foreach (var field in table.CollectionFields)
        {
            ReleaseCollectionBuffer(field, Unsafe.ReadUnaligned<int>(staged + field.OffsetInComponentStorage));
        }
    }

    /// <summary>The HEAD half of <see cref="PublishStagedEntry"/>: the staged value copied over HEAD, HEAD's collection references given back.</summary>
    private void PublishStagedValue(ComponentInfo info, ArchetypeClusterState clusterState, int compSlot, int clusterChunkId, byte slotIndex, byte* headPtr,
        byte* staged, int compSize)
    {
        // HEAD's collection handles, read before the memcpy replaces them: HEAD's references are given back once the staged value — which holds its own
        // (ShareStagedCollections) — has taken HEAD's place (#1362). Never before: HEAD would name a buffer freed under it.
        var table = info.ComponentTable;
        var collectionFields = table.HasCollections ? table.CollectionFields : null;
        var replacedHandles = collectionFields != null ? stackalloc int[collectionFields.Length] : default;
        for (var i = 0; i < replacedHandles.Length; i++)
        {
            replacedHandles[i] = Unsafe.ReadUnaligned<int>(headPtr + collectionFields[i].OffsetInComponentStorage);
        }

        // Visibility act: publish the staged value to the cluster HEAD, then mark dirty (CM-03: memcpy THEN dirty).
        Unsafe.CopyBlockUnaligned(headPtr, staged, (uint)compSize);
        for (var i = 0; i < replacedHandles.Length; i++)
        {
            ReleaseCollectionBuffer(collectionFields[i], replacedHandles[i]);
        }
        // Same as the Versioned publish above: compSlot is the component being published, so name it rather than falling back to "emit everything" (M31).
        clusterState.SetDirty(clusterChunkId, slotIndex, compSlot);

        // A spatial value or a realm key landing NOW: flagged for the fence as the barrier would, after the memcpy, so a fence that consumes the flag reads
        // the new value. The flag raised when the write was staged may already have been consumed against the old one (CC-02).
        ref var spatial = ref clusterState.SpatialSlot;
        if (spatial.HasSpatialIndex && spatial.FieldInfo.Mode == SpatialMode.Dynamic
            && (compSlot == spatial.Slot || (spatial.HasRealmKey && compSlot == spatial.RealmKeySlot)))
        {
            clusterState.FlagOutOfBarrierSpatialWrite(clusterChunkId, slotIndex);
        }
    }

    /// <summary>Gives back one reference on collection buffer <paramref name="bufferId"/> of field <paramref name="field"/>; 0 is no buffer.</summary>
    private static void ReleaseCollectionBuffer(in ComponentTable.CollectionFieldInfo field, int bufferId)
    {
        if (bufferId == 0)
        {
            return;
        }

        var accessor = field.Vsbs.Segment.CreateChunkAccessor();
        try
        {
            field.Vsbs.BufferRelease(bufferId, ref accessor);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Gives back the references the Commit-discipline staged values hold on their collection buffers (#1362), which no committed value will take over:
    /// every staged write's on a rollback, or only those of the entities this commit destroys — their staged value is never published, and the destroy
    /// releases HEAD's own. A buffer the staged value shares with HEAD loses the staging's reference only; one an accessor moved it to is freed. Each handle
    /// is zeroed once released, so a second pass — a rollback after a failed commit — releases nothing twice.
    /// </summary>
    /// <param name="destroyedOnly">Only the staged writes of entities in this transaction's pending destroys.</param>
    private void ReleaseStagedCollections(bool destroyedOnly)
    {
        if (_commitStagingBuffer == null || (destroyedOnly && _pendingDestroys is not { Count: > 0 }))
        {
            return;
        }

        foreach (var info in _componentInfos.Values)
        {
            var table = info.ComponentTable;
            if (info.CommitStaged is not { Count: > 0 } || table.StorageMode != StorageMode.SingleVersion || !table.HasCollections)
            {
                continue;
            }

            foreach (var staged in info.CommitStaged)
            {
                if (destroyedOnly && !_pendingDestroys.Contains(EntityId.FromRaw(staged.Key)))
                {
                    continue;
                }

                var value = _commitStagingBuffer + staged.Value.Offset;
                foreach (var f in table.CollectionFields)
                {
                    var bufferId = Unsafe.ReadUnaligned<int>(value + f.OffsetInComponentStorage);
                    Unsafe.WriteUnaligned(value + f.OffsetInComponentStorage, 0);
                    ReleaseCollectionBuffer(f, bufferId);
                }
            }
        }
    }

    /// <summary>
    /// Discards all changes made by this transaction, tagging the rollback as <see cref="Typhon.Profiler.TransactionRollbackReason.Explicit"/>.
    /// </summary>
    /// <param name="ctx">Unit-of-work context carrying the deadline / cancellation used for any lock waits during rollback.</param>
    /// <returns>
    /// <see langword="true"/> if the transaction was rolled back (or had nothing to do because no operation was performed); <see langword="false"/> if it was
    /// already committed or rolled back.
    /// </returns>
    public bool Rollback(ref UnitOfWorkContext ctx) => Rollback(ref ctx, Typhon.Profiler.TransactionRollbackReason.Explicit);

    /// <summary>Phase 6: rollback with an explicit <paramref name="reason"/> threaded into the kind 21 payload (D3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Rollback(ref UnitOfWorkContext ctx, Typhon.Profiler.TransactionRollbackReason reason)
    {
        // The hot fast path here is read-only-tx auto-rollback on Dispose: state is already Created/Committed/Rollbacked, returns immediately. By keeping
        // the holdoff `using` and span try/finally inside RollbackCore (slow), this fast-path shim stays EH-free and inlinable into Dispose.
        AssertThreadAffinity();

        // Nothing to undo if the transaction is empty, but it ends here all the same: left in Created, it kept accepting writes (TX-01, #1056)
        if (State is TransactionState.Created)
        {
            State = TransactionState.Rollbacked;
            return true;
        }

        // Can't roll back a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed)
        {
            return false;
        }

        return RollbackCore(ref ctx, reason);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RollbackCore(ref UnitOfWorkContext ctx, Typhon.Profiler.TransactionRollbackReason reason)
    {
        // No yield point — rollback/cleanup must always complete
        using var holdoff = ctx.EnterHoldoff();

        var scope = TyphonEvent.BeginTransactionRollback(TSN);
        scope.ComponentCount = _componentInfos.Count;
        scope.Reason = reason;
        // Cumulative rollback counter — monotonic, sampled by the profiler's per-tick gauge snapshot to derive per-tick rollback rate.
        _dbe.TransactionChain.IncrementRollbackTotal();
        try
        {
            var context = new CommitContext();
#pragma warning disable CS9093 // ref-assign is safe: CommitContext is a ref struct that never escapes this method
            context.Ctx = ref ctx;
#pragma warning restore CS9093

            // Determine tail status once for the entire rollback — saves N-1 shared lock acquires on TransactionChain
            var wcTail = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
            if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wcTail))
            {
                ThrowHelper.ThrowLockTimeout("TransactionChain/RollbackTailCheck", TimeoutOptions.Current.TransactionChainLockTimeout);
            }
            context.IsTail = _dbe.TransactionChain.Tail == this;
            context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
            context.TailTSN = _dbe.TransactionChain.MinTSN;
            _dbe.TransactionChain.Control.ExitSharedAccess();

            var rollbackAction = new RollbackAction { Tx = this };
            // Hoisted outside loop to avoid per-iteration stackalloc accumulation (CA2014)
            Span<long> createdPkBuffer = stackalloc long[128];
            // Process every Component Type and their components
            foreach (var componentInfo in _componentInfos.Values)
            {
                context.Info = componentInfo;

                componentInfo.ForEachMutableEntry(ref context, ref rollbackAction);

                // Remove rolled-back Created entities from the cache.
                // Can't modify dictionary during ForEachMutableEntry, so do a second pass.
                var cacheCount = componentInfo.SingleCache.Count;
                Span<long> toRemove = cacheCount <= 128 ? createdPkBuffer[..cacheCount] : new long[cacheCount];
                var removeCount = 0;
                foreach (var kvp in componentInfo.SingleCache)
                {
                    if ((kvp.Value.Operations & ComponentInfo.OperationType.Created) != 0)
                    {
                        toRemove[removeCount++] = kvp.Key;
                    }
                }
                for (var i = 0; i < removeCount; i++)
                {
                    componentInfo.SingleCache.Remove(toRemove[i]);
                    _deletedComponentCount++;
                }
            }

            // The Commit-discipline staged writes are dropped with the transaction, and the references their collection handles hold with them (#1362).
            ReleaseStagedCollections(destroyedOnly: false);

            // Flush batched deferred enqueue entries (non-tail path: single lock acquire for all entities)
            if (_deferredEnqueueBatch is { Count: > 0 })
            {
                using var cleanupScope = TyphonEvent.BeginDataTransactionCleanup(TSN, _deferredEnqueueBatch.Count);
                _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
                _deferredEnqueueBatch.Clear();
            }

            // New state
            TransitionTo(TransactionState.Rollbacked);
            _dbe?.RecordRollback();
            return true;

        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Discards all changes made by this transaction, using an unbounded (no-timeout) context.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the transaction was rolled back (or had nothing to do because no operation was performed); <see langword="false"/> if it was
    /// already committed or rolled back.
    /// </returns>
    public bool Rollback()
    {
        var ctx = UnitOfWorkContext.None;
        return Rollback(ref ctx);
    }

    /// <summary>Pooled per-transaction arena backing <see cref="CommitBatchBuilder"/>; reset (not realloc) each commit.</summary>
    private CommitBatchArena _commitBatchArena;

    /// <summary>
    /// Builds this transaction's WAL v2 record batch (M1, 02 §3): entity-lifecycle records (spawn/destroy/enable) plus one Slot
    /// upsert per modified durable component, its committed value read from the content chunk. Component deletes are NOT emitted
    /// as Slot records — an entity destroy is a single <see cref="CommitBatchBuilder.AddDestroy"/> (the builder enforces LOG-07
    /// ordering). The schema-stable <see cref="ComponentInfo.ComponentTypeId"/> is the WAL identity (LOG-06). SV components flow
    /// through the fence path, not here.
    /// </summary>
    /// <summary>
    /// ECS-STAGE-03: whether this transaction's Spawn lifecycle record for <paramref name="archetypeId"/> is suppressed. True only for a
    /// <see cref="ClusterDurability.Checkpoint"/> archetype, spawned under a non-<see cref="CommitDiscipline.Commit"/> transaction, whose components are all
    /// non-Versioned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> <see cref="ClusterDurability.Checkpoint"/> declares its entities checkpoint-durable — D5, <c>03-recovery.md:172</c>: <i>"A plain TickFence
    /// spawn stays checkpoint-durable only — the documented non-guarantee, not a bug."</i> But emitting the Spawn record while suppressing the fence that
    /// would carry the values delivers something WORSE than that guarantee: the entity recovers alive with default values — the phantom this file's own #395
    /// Face B comment names below. Measured, both cells, by <c>CheckpointDurabilityCrashTests</c>. Suppressing the record makes a window spawn ABSENT after a
    /// crash, which is exactly what "checkpoint-durable only" means.
    /// </para>
    /// <para>
    /// <b>Why Destroy is NOT suppressed, and must never be.</b> Suppression is safe here only because it is MONOTONE: with no Spawn record the replay window
    /// can only remove entities, never create one, so no ordering of mixed-discipline transactions can resurrect a destroyed entity. Suppressing Destroy
    /// instead would need proof that the entity is absent from the base — a decision made at emit time that races the checkpoint which actually lands (born
    /// TSN 100, last checkpoint 50, destroy at 150 ⇒ "born after, suppress"; a checkpoint completing at 120 then puts it IN the base, and the crash resurrects
    /// it silently). Not decidable locally. The one exception is decided locally: an entity this transaction spawned and destroyed is never placed, so no
    /// checkpoint can hold it, and neither its Spawn nor its Destroy is logged (#1229).
    /// </para>
    /// <para>
    /// <b>The three terms.</b> <c>Checkpoint</c> — the archetype opted into the weaker window. Non-<c>Commit</c> discipline — CM-06 <c>[fatal]</c> requires a
    /// Commit-discipline spawn to be atomically durable per commit, and the branch below logs its values, which would be orphaned without the Spawn.
    /// No Versioned slots — the <c>SingleCache</c> loop below emits revision content for a Versioned component at commit regardless of cluster durability
    /// (§2.4: the chain stays authoritative, so no <c>ClusterDurability</c> setting may lose a Versioned value); dropping the Spawn would strand it, since
    /// <c>ApplySlotToExisting</c> no-ops for an entity absent from the base map rather than failing loudly.
    /// </para>
    /// <para>
    /// <b>Recovery tolerates the gap by construction.</b> Every consumer of a record whose Spawn never arrived is an explicit no-op, not a loud failure:
    /// <c>ApplyDestroyToExisting</c> (<c>RecoveryApplier.cs:293</c>), <c>ApplySlotToExisting</c> (<c>:344</c>, AP-12 idempotence),
    /// <c>ApplySetEnabledBitsToExisting</c> (<c>:695</c>). And recovery never needed the record to ENUMERATE: cluster pages are self-describing, the EntityMap
    /// is discarded and re-derived from the occupancy walk before WAL apply, and <c>NextEntityKey</c> comes from that same walk.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SuppressSpawnRecord(ushort archetypeId)
    {
        if (_discipline == CommitDiscipline.Commit)
        {
            return false;
        }

        var meta = _dbe.GetMetaByRouting(archetypeId);
        return meta.ClusterDurability == ClusterDurability.Checkpoint && meta.VersionedSlotCount == 0;
    }

    private void BuildCommitBatch(ref CommitBatchBuilder batch)
    {
        // Spawn lifecycle.
        if (_spawnedEntities != null)
        {
            for (int i = 0; i < _spawnedEntities.Count; i++)
            {
                var s = _spawnedEntities[i];
                // Spawned and destroyed here: nothing of it is logged, its spawn or its destroy — it never existed past this transaction (#1229).
                if (SuppressSpawnRecord(s.Id.ArchetypeId) || (_pendingDestroys != null && _pendingDestroys.Contains(s.Id)))
                {
                    continue;
                }

                batch.AddSpawn((long)s.Id.RawValue, s.Id.ArchetypeId, s.EnabledBits);
            }

            // #395 Face B (design D5): under Commit discipline a spawn must be ATOMICALLY durable — also WAL-log its SingleVersion component VALUES
            // (one Slot upsert each). Without this the spawn lifecycle is logged but the SV values are not (they only ride the cluster SoA / content
            // chunk, persisted by the next checkpoint), so a hard crash before that checkpoint recovers the entity alive-but-default — a phantom.
            // Recovery aggregates the Spawn + these Slots by EntityId and applies them together (ApplySpawnedEntity → cluster slot claim + SoA value
            // write), so the entity recovers with its values. TickFence spawns stay checkpoint-durable by design (no per-commit SV WAL). Versioned
            // spawn values are already logged via their revision chains (SingleCache loop below); Transient is never logged. Read from the
            // spawn-staging content chunk (entry.Loc[slot]) — PlaceSpawns runs later (the end of PREPARE), so the cluster SoA is not populated yet, but the
            // value already sits at ComponentOverhead in the staging chunk.
            if (_discipline == CommitDiscipline.Commit)
            {
                for (int i = 0; i < _spawnedEntities.Count; i++)
                {
                    var s = _spawnedEntities[i];
                    if (_pendingDestroys != null && _pendingDestroys.Contains(s.Id))
                    {
                        continue;   // spawned and destroyed in the same tx — nothing to restore
                    }

                    var slotTables = _dbe._stateByRouting[s.Id.ArchetypeId].SlotToComponentTable;
                    for (int slot = 0; slot < slotTables.Length; slot++)
                    {
                        var table = slotTables[slot];
                        if (table == null || table.StorageMode != StorageMode.SingleVersion)
                        {
                            continue;   // Versioned already logged (chains); Transient never logged
                        }

                        // #839: a SingleVersion spawn payload is staged in the transaction's arena, not in a content chunk. This read is unaffected by the
                        // move — it wants the [ComponentOverhead][value] bytes, and the arena slot has exactly that layout — so CM-06's emission keeps its
                        // existing position BEFORE PlaceSpawns rather than needing to be re-ordered after it.
                        int stage = s.Stage[slot];
                        if (stage <= 0)
                        {
                            continue;   // component not provided at spawn
                        }

                        var info = GetComponentInfo(table.Definition.POCOType);
                        var payload = new ReadOnlySpan<byte>(SpawnArena.Resolve(stage), table.ComponentOverhead + table.ComponentStorageSize);
                        // Wire identity is the per-archetype slot (LOG-06); here it's the spawn-iteration slot index.
                        var value = payload.Slice(info.ComponentOverhead, table.ComponentStorageSize);
                        batch.AddSlot((long)s.Id.RawValue, (ushort)slot, value, table.CollectionHandleRanges);
                        if (table.HasCollections)
                        {
                            CollectionContentEmitter.Emit(ref batch, table, (long)s.Id.RawValue, (ushort)slot, value);
                        }
                    }
                }
            }
        }

        // Component values: one Slot upsert per modified component (skip reads and deletes — deletes ride the entity-destroy record).
        foreach (var kvp in _componentInfos)
        {
            var info = kvp.Value;
            var componentTypeId = (ushort)info.ComponentTypeId;
            var storageSize = info.ComponentTable.ComponentStorageSize;
            foreach (var cacheEntry in info.SingleCache)
            {
                var cri = cacheEntry.Value;
                if (cri.Operations == ComponentInfo.OperationType.Read || (cri.Operations & ComponentInfo.OperationType.Deleted) != 0)
                {
                    continue;
                }

                if (cri.CurCompContentChunkId <= 0)
                {
                    continue;
                }

                // The content chunk is laid out [ComponentOverhead][value]; the logical value lives at offset ComponentOverhead — the same offset the read and
                // write paths apply (EntityAccessor.ReadEcsComponentDataRaw / WriteEcsComponentData). Log the VALUE, not the raw chunk prefix: an
                // overhead-bearing (indexed) component otherwise logs its overhead bytes plus a truncated value, silently dropping the trailing bytes from the
                // WAL — invisible until crash recovery.
                var overhead = info.ComponentTable.ComponentOverhead;
                var payload = info.CompContentAccessor.GetChunkAsReadOnlySpan(cri.CurCompContentChunkId);
                
                // Wire identity is the per-archetype slot (LOG-06); the same component sits at different slots in different archetypes, so resolve it from
                // THIS entity's archetype (routing id embedded in the PK).
                var slot = (ushort)_dbe.GetMetaByRouting(EntityId.FromRaw(cacheEntry.Key).ArchetypeId).GetSlot(componentTypeId);
                var value = payload.Slice(overhead, storageSize);
                batch.AddSlot(cacheEntry.Key, slot, value, info.ComponentTable.CollectionHandleRanges);
                if (info.ComponentTable.HasCollections)
                {
                    // The value read above is the NEW revision's content chunk, so its handle is the post-copy-on-write buffer — this transaction's own view.
                    CollectionContentEmitter.Emit(ref batch, info.ComponentTable, cacheEntry.Key, slot, value);
                }
            }

            // Commit-discipline (SingleVersion) staged writes: the value lives in the native staging buffer (offset is 1-based), already sliced past
            // the component overhead. Logged as an ordinary Slot record; the batch's Committed flag is telemetry (recovery applies it like any
            // tick-fence slot record — last-writer-wins by LSN, AC-7).
            if (info.CommitStaged != null)
            {
                foreach (var staged in info.CommitStaged)
                {
                    var slot = (ushort)_dbe.GetMetaByRouting(EntityId.FromRaw(staged.Key).ArchetypeId).GetSlot(componentTypeId);
                    var value = new ReadOnlySpan<byte>(_commitStagingBuffer + staged.Value.Offset, storageSize);
                    batch.AddSlot(staged.Key, slot, value, info.ComponentTable.CollectionHandleRanges);
                    if (info.ComponentTable.HasCollections)
                    {
                        CollectionContentEmitter.Emit(ref batch, info.ComponentTable, staged.Key, slot, value);
                    }
                }
            }
        }

        // Destroy lifecycle (one record per entity; per-component tombstones are not logged separately).
        if (_pendingDestroys != null)
        {
            foreach (var id in _pendingDestroys)
            {
                if (!SpawnedContains(id))
                {
                    batch.AddDestroy((long)id.RawValue);
                }
            }
        }

        // Enable/disable lifecycle (absolute bits).
        if (_pendingEnableDisable != null)
        {
            foreach (var enableEntry in _pendingEnableDisable)
            {
                // An own spawn's enable or disable rides its Spawn record; one it also destroyed has none (#1229).
                if (_pendingDestroys != null && _pendingDestroys.Contains(enableEntry.Key) && SpawnedContains(enableEntry.Key))
                {
                    continue;
                }

                batch.AddEnabledBits((long)enableEntry.Key.RawValue, enableEntry.Value);
            }
        }
    }

    /// <summary>
    /// AP-01 APPEND step: builds the transaction's WAL batch from prepared state and appends it to the commit buffer, returning the batch's highest LSN
    /// (0 when nothing was appended). This is the point of no return (AP-02) — it runs AFTER all PREPARE work (so conflict-resolved values are logged)
    /// and BEFORE any publish. BL-01: a SuppressWalSerialization UoW (BulkLoad path) appends nothing; page dirty marking still happens via the PREPARE
    /// pass so the checkpoint flushes the data, and BulkLoadSession.CompleteBulkLoad emits BulkEnd as the bulk's durability anchor.
    /// </summary>
    private long AppendToWal(ref UnitOfWorkContext ctx)
    {
        long walHighLsn = 0;
        var cards = _dbe.CommitCards;
        if (State != TransactionState.Created && OwningUnitOfWork?.SuppressWalSerialization != true)
        {
            var persistScope = TyphonEvent.BeginTransactionPersist(TSN);
            try
            {
                _commitBatchArena ??= new CommitBatchArena();
                _commitBatchArena.Reset();
                var batch = new CommitBatchBuilder(_commitBatchArena, TSN, UowId, committedDiscipline: _discipline == CommitDiscipline.Commit);
                BuildCommitBatch(ref batch);
                if (!batch.IsEmpty)
                {
                    var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
                    walHighLsn = _dbe.DurabilityLog.Append(ref batch, ref wc, ref _inFlightLsnFloor, cards, _commitCardSlot);
                    _commitStamp = batch.CommitStamp;
                }

                persistScope.WalLsn = walHighLsn;
            }
            finally
            {
                persistScope.Dispose();
            }
        }

        // VIS-03: a commit with no WAL record (bulk-loaded, or an empty batch) has its commit point here, at the end of PREPARE.
        if (_commitStamp == 0)
        {
            _commitStamp = cards.Register(_commitCardSlot, TSN);
        }

        _appendPhaseEnteredThisCommit = true; // AP-01 guard: the append phase has run; publish is now permitted.
        return walHighLsn;
    }

    /// <summary>
    /// VIS-03 step 0: a commit card of this transaction's window, before PREPARE takes any chain lock. A full window waits here, holding no chain lock and
    /// no WAL claim, its epoch refreshed so the cards' owners can load pages; it gives up at the unit of work's deadline with
    /// <see cref="LockTimeoutException"/>, before anything is staged: the transaction can commit again.
    /// </summary>
    private void ReserveCommitCard(ref UnitOfWorkContext ctx)
    {
        var cards = _dbe.CommitCards;
        if (cards.TryReserve(TSN, out _commitCardSlot))
        {
            return;
        }

        Interlocked.Increment(ref cards.FullWindowWaits);
        var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
        var waiter = new AdaptiveWaiter();
        var refreshedAt = Stopwatch.GetTimestamp();
        while (!cards.TryReserve(TSN, out _commitCardSlot))
        {
            if (!waiter.Wait(ref wc))
            {
                ThrowHelper.ThrowLockTimeout("CommitCard/Reserve", TimeoutOptions.Current.DefaultCommitTimeout);
            }

            // The waiter is often an old writer, the oldest epoch pin: held while the cards' owners need page loads, it would make a cycle only the
            // back-pressure timeout breaks (design 05 §3.2). It holds no page reference across the wait (EP-03).
            if (LiveIndexCursors == 0 && Stopwatch.GetElapsedTime(refreshedAt) >= CommitCardEpochRefreshInterval)
            {
                FlushAndRefreshEpoch();
                refreshedAt = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>How often a commit waiting for a commit card refreshes its epoch.</summary>
    private static readonly TimeSpan CommitCardEpochRefreshInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>VIS-03 step 6, and every exit before the commit point: the card given back. Idempotent.</summary>
    private void ReleaseCommitCard()
    {
        if (_commitCardSlot >= 0)
        {
            _dbe.CommitCards.Release(_commitCardSlot);
            _commitCardSlot = -1;
        }
    }

    /// <summary>
    /// Rolls back a commit that failed before its append, once PREPARE had begun on its entries (#1262), without the unit of work's deadline: the failure
    /// may be that deadline (the transaction chain's lock keeps its own timeout). Reported as
    /// <see cref="Typhon.Profiler.TransactionRollbackReason.AutoOnDispose"/>, the rollback such a commit used to get from <see cref="Dispose"/>. A rollback
    /// that throws is logged, and the commit's own failure is the one that propagates; the transaction still cannot commit again
    /// (<see cref="_prepareConsumed"/>).
    /// </summary>
    private void RollbackAfterFailedCommit()
    {
        try
        {
            // The spawns PREPARE placed go first: their records name the chain roots the rollback frees (AP-07, VIS-08). The destroys' pending died with them.
            UnplaceSpawns();
            UnmarkDestroysPending();
            UnmarkEnableDisablePending();
            _prepareStaged = false;
            var unbounded = UnitOfWorkContext.None;
            RollbackCore(ref unbounded, Typhon.Profiler.TransactionRollbackReason.AutoOnDispose);
        }
        catch (Exception rollbackFailure)
        {
            _dbe.LogRollbackAfterFailedCommitFailed(TSN, rollbackFailure);
        }
    }

    /// <summary>
    /// The commit failed after its append (AP-02/AP-03): it reports Committed and is never rolled back. When its publish had completed, only the durability
    /// wait or the finalization failed: <see cref="CommitDurabilityUncertainException"/>. Otherwise memory holds part of it: the engine stops committing and
    /// pins its records below the checkpoint (<see cref="DatabaseEngine.LatchCommitPipelineFailure"/>), its records are made durable when they can be, and
    /// <see cref="CommitPublishIncompleteException"/> tells the caller to reopen. Always throws.
    /// </summary>
    private void CompleteAfterFailedPublish(long walHighLsn, bool published, Exception failure)
    {
        if (published)
        {
            TransitionTo(TransactionState.Committed);
            throw new CommitDurabilityUncertainException(walHighLsn, failure);
        }

        // Latch, then Committed, then anything that can throw: from here on, a throw must not leave the transaction for Dispose to roll back.
        var firstLsn = Volatile.Read(ref _inFlightLsnFloor);
        _dbe.LatchCommitPipelineFailure(firstLsn, failure);
        TransitionTo(TransactionState.Committed);

        // The chain locks of entries the drain did not reach are released before the durability wait, which must not span them (as WaitAndFinalize's
        // never does). The drain cursor makes the commit's own finally a no-op after this.
        ReleaseRetainedPublishLocks();
        _dbe.LogCommitPipelineStopped(TSN, firstLsn, failure);

        // The records' durability decides whether the restart recovers this commit: confirmed when it can be, on a budget of its own (the unit of work's
        // may be what ran out).
        var durable = false;
        if (walHighLsn > 0 && _dbe.WalManager != null)
        {
            try
            {
                _dbe.WalManager.RequestFlush();
                var wc = WaitContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
                _dbe.WalManager.WaitForDurable(walHighLsn, ref wc);
                durable = true;
            }
            catch (Exception)
            {
                // Unconfirmed: the exception says so. Never a rollback.
            }
        }

        throw new CommitPublishIncompleteException(walHighLsn, durable, failure);
    }

    /// <summary>
    /// AP-01 WAIT/finalize step: for Immediate durability, waits until the appended LSN is durable, then transitions the transaction to Committed and
    /// records metrics. Runs AFTER publish (so the per-entity handler locks are already released — the wait never spans an fsync while holding them).
    /// A wait timeout here occurs post-publish: the transaction is logically committed (AP-02), so the exception means "durability uncertain", not
    /// "rolled back" (the publish-then-surface contract; the exception type is refined in the AP-02/AP-03 hardening step).
    /// </summary>
    private void WaitAndFinalize(ref UnitOfWorkContext ctx, long walHighLsn, long startTicks)
    {
        Exception durabilityUncertain = null;

        // Durability wait for Immediate mode. Publish already ran (AP-01), so the transaction is logically committed: a wait failure here (back-pressure
        // timeout or fatal writer error) MUST NOT roll back (AP-02). Capture it, finish the Committed transition, then surface it as
        // CommitDurabilityUncertainException so the caller learns "committed, durability unconfirmed" rather than "rolled back".
        if (walHighLsn > 0 && OwningUnitOfWork?.DurabilityMode == DurabilityMode.Immediate)
        {
            Debug.Assert(_dbe.WalManager != null);
            _dbe.WalManager.RequestFlush();
            var wc = ComposeWaitContext(ref ctx, TimeoutOptions.Current.DefaultCommitTimeout);
            try
            {
                _dbe.WalManager.WaitForDurable(walHighLsn, ref wc);
            }
            catch (Exception ex)
            {
                // Any failure, not only a TyphonException: the transaction is published, so the wait's failure is uncertainty, never a rollback.
                durabilityUncertain = ex;
            }
        }

        // New state — the transaction reaches Committed even when durability is unconfirmed (AP-02: Append is the point of no return).
        TransitionTo(TransactionState.Committed);

        // Record commit duration for observability
        var elapsedUs = (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;
        _dbe?.RecordCommitDuration(elapsedUs);

        if (durabilityUncertain != null)
        {
            throw new CommitDurabilityUncertainException(walHighLsn, durabilityUncertain);
        }
    }

    /// <summary>
    /// Commits this transaction: processes every modified component, appends the WAL batch (the point of no return), then publishes the changes so they become
    /// visible to later transactions. When <paramref name="handler"/> is supplied, each write-write conflict is surfaced to it for resolution; otherwise the
    /// last write wins.
    /// </summary>
    /// <param name="ctx">Unit-of-work context carrying the deadline / cancellation used for lock and durability waits.</param>
    /// <param name="handler">Optional write-write conflict resolver. <see langword="null"/> to accept the default last-writer-wins behavior.</param>
    /// <returns>
    /// <see langword="true"/> if the transaction committed (including read-only and empty transactions); <see langword="false"/> if it was already committed or
    /// rolled back.
    /// </returns>
    /// <exception cref="CommitDurabilityUncertainException">
    /// The commit is durably past its point of no return (records were appended) but the durability wait did not confirm — the write is committed, its
    /// on-media durability unconfirmed. Only reachable on the FUA/Immediate durability path.
    /// </exception>
    public bool Commit(ref UnitOfWorkContext ctx, ConcurrencyConflictHandler handler = null)
    {
        AssertThreadAffinity();

        // Can't commit a transaction already processed — read-only included: one that was rolled back is not committed by this call (TX-01) — nor
        // one a failed commit's PREPARE consumed (#1262), should its rollback have failed
        if (State is TransactionState.Rollbacked or TransactionState.Committed || _prepareConsumed)
        {
            return false;
        }

        // Read-only transactions have nothing to commit — trivially succeed
        if (IsReadOnly)
        {
            return true;
        }

        // Nothing to commit if the transaction is empty, but it is committed all the same: left in Created, it kept accepting writes its caller believed
        // came after the commit (TX-01, #1056). Its deferred cleanups wait for its Dispose: the cutoff a tail computes leaves its own snapshot out, and
        // trimming to it here took revisions this transaction can still read (TXC-01).
        if (State is TransactionState.Created)
        {
            State = TransactionState.Committed;
            return true;
        }

        // AP-03: an engine whose commit pipeline stopped holds a partial publish in memory. Nothing more is appended over it: the restart that recovers
        // that commit replays the log, and a later commit would build on what the partial publish left. Refused before PREPARE, so this one rolls back
        // clean.
        if (_dbe.CommitPipelineFailure is { } stopped)
        {
            throw new DurabilityException(TyphonErrorCode.CommitPipelineStopped,
                "The engine stopped committing after a commit failed between its append and the end of its publish; reopen the database to recover it.",
                stopped);
        }

        // ── Yield point: safe to cancel before any modifications ──
        ctx.ThrowIfCancelled();

        var scope = TyphonEvent.BeginTransactionCommit(TSN);
        // Cumulative commit counter — monotonic. Incrementing BEFORE the commit body means a commit that throws mid-way still gets
        // counted. A commit that fails before its append, once PREPARE has begun on its entries, also counts a rollback (#1262: it rolls back
        // itself); one that fails earlier counts its rollback when Dispose rolls it back. For gauge purposes "attempted commit" vs "successful
        // commit" is close enough; a more precise variant would move this to a success path but currently Transaction.Commit has no single
        // success-return point.
        _dbe.TransactionChain.IncrementCommitTotal();
        var preparedEntries = false;

        // #831: a back-pressure wait inside this commit, up to its append, can tell that the commit itself holds the cache (PagedMMF.CommitMarks).
        var outerCommitMarks = PagedMMF.CommitMarks;
        PagedMMF.CommitMarks = _changeSet;
        try
        {
            // Must sit inside the try so the finally still runs if this property access ever gains side effects that can throw.
            scope.ComponentCount = _componentInfos.Count;

            var startTicks = Stopwatch.GetTimestamp();

            _dbe.LogCommitStart(TSN, _componentInfos.Count);

            // ── Holdoff: entire commit loop runs to completion ──
            using var holdoff = ctx.EnterHoldoff();

            // CASC-01, CASC-02 — before the card, so a destroyer's wait holds none: the children of every cascade parent this transaction destroys
            // resolved again at HEAD, each parent marked dying and its earlier referrers waited for; then every cascade foreign key this commit writes
            // checked and announced until it has published. A failure here leaves the commit retryable; the finally gives the marks back.
            ResolveCascadesAtCommit(ref ctx);
            AnnounceCascadeReferences();

            // VIS-03 step 0, before any chain lock: the commit card. The finally gives it back on every exit before the commit point.
            _commitStamp = 0;
            ReserveCommitCard(ref ctx);
            _dbe.CommitAfterReserveProbe?.Invoke();

            var conflictSolver = handler != null ? ConcurrencyConflictSolver.GetConflictSolver() : null;
            var context = new CommitContext { Solver = conflictSolver, Handler = handler };
#pragma warning disable CS9093 // ref-assign is safe: CommitContext is a ref struct that never escapes this method
            context.Ctx = ref ctx;
#pragma warning restore CS9093
            var hasConflict = false;

            // Determine tail status once for the entire commit — saves N-1 shared lock acquires on TransactionChain
            var wcTail = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
            if (!_dbe.TransactionChain.Control.EnterSharedAccess(ref wcTail))
            {
                ThrowHelper.ThrowLockTimeout("TransactionChain/CommitTailCheck", TimeoutOptions.Current.TransactionChainLockTimeout);
            }
            context.IsTail = _dbe.TransactionChain.Tail == this;
            context.NextMinTSN = context.IsTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
            context.TailTSN = _dbe.TransactionChain.MinTSN;
            _dbe.TransactionChain.Control.ExitSharedAccess();

            _appendPhaseEnteredThisCommit = false; // AP-01 guard reset (pooled Transaction reuse).
            _publishDrainIndex = 0;
            _pendingUniqueMoves = 0;

            // QENABLE-01: the first supplies, claimed before any chain lock and in one order (ClaimFirstSupplies). A timeout here leaves the commit retryable.
            ClaimFirstSupplies(ref ctx);

            // An entity this transaction spawned and destroyed never exists for anyone else: its storage goes back now, as a rollback's would (#1229).
            ReleaseSpawnsDestroyedHere();

            // Likewise the references a destroyed entity's staged collections hold: that value is never published, the destroy releases HEAD's (#1362).
            ReleaseStagedCollections(destroyedOnly: true);

            // Prepare ECS destroy operations: create component-level tombstone revisions in the PREPARE pass so it can handle index removal, WAL, and
            // cleanup. The entity-level destroy visibility (DiedTSN) is applied later, in the PUBLISH pass (FlushEcsPendingOperations).
            PrepareEcsDestroys();

            // From here PREPARE changes per-entry state a second attempt could not reuse (each entry's read revision is consumed, a conflict's orphan
            // freed), so a failure before the append ends the transaction: see the catch below (#1262). A failure above stays retryable.
            preparedEntries = true;
            _prepareConsumed = true;

            _dbe.LogCommitPhase(TSN, "CommitComponentCore");

            // Phase 6: Data:Transaction:Validate span over the per-component-type validation pass.
            using var validateScope = TyphonEvent.BeginDataTransactionValidate(TSN, _componentInfos.Count);

            // Process every Component Type and their components (old CRUD path — Versioned only). AP-01: this is the PREPARE pass — all fallible work,
            // no visibility. The matching PUBLISH pass (PublishPreparedComponents) runs after the WAL Append.
            var prepareAction = new PrepareAction { Tx = this };
            foreach (var kvp in PrepareOrder(handler != null))
            {
                var info = kvp.Value;

                // Skip non-Versioned components — the old CRUD commit path only applies to Versioned.
                // SV/Transient components in _componentInfos were added by the ECS path (Spawn/Read/Write)
                // and don't have old CRUD mutations to commit.
                if (info.ComponentTable.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }

                context.Info = info;
                _dbe.LogCommitComponentEntries(TSN, kvp.Key.Name, info.EntryCount);

                // Start a sub-span for this component type. The int ID comes from the archetype registry — -1 means "unregistered"
                // (can happen for schema-less tests), which is fine to carry through as a placeholder. Phase 6: rowCount field
                // is wire-additive on kind 22 — set from info.EntryCount before the loop runs. We use try/finally instead of
                // `using var` because setting fields on a `using var` ref struct is forbidden by the language (CS1654).
                var componentScope = TyphonEvent.BeginTransactionCommitComponent(TSN, ArchetypeRegistry.GetComponentTypeId(kvp.Key));
                componentScope.RowCount = info.EntryCount;
                try
                {

                    // The hoisted per-ComponentTable index accessors that used to be built here are gone with those indexes (#629). Batch mode itself stays:
                    // it still bounds dirty-page inflation for the component and revision segments over a large transaction.
                    _batchIndexAccessors = [];
                    _batchIndexActive = true;
                    _batchEntityCount = 0;
                    ChunkBasedSegment<PersistentStore>.EnterBatchMode();

                    try
                    {
                        if (handler != null)
                        {
                            kvp.Value.ForEachMutableEntryOrdered(ref context, ref prepareAction);
                        }
                        else
                        {
                            kvp.Value.ForEachMutableEntry(ref context, ref prepareAction);
                        }
                    }
                    finally
                    {
                        // Exit batch mode + dispose hoisted accessors
                        ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                        _batchIndexActive = false;
                        for (int i = 0; i < _batchIndexAccessors.Length; i++)
                        {
                            _batchIndexAccessors[i].Dispose();
                        }
                        _batchIndexAccessors = null;

                        // Dispose cluster commit accessors between component types — the next type may target a different archetype
                        DisposeClusterCommitAccessors();
                    }

                    _dbe.LogCommitComponentDone(TSN, kvp.Key.Name);
                }
                finally
                {
                    componentScope.Dispose();
                }
            }

            // AP-03: what the publish would fail on — a unique key taken when it publishes, a spawn without the component a unique index or its spatial
            // cell needs — is checked here, after PREPARE and before the point of no return: past it a failure could only stop the engine.
            ValidatePublishBeforeAppend();

            // VIS-08: every spawn exists before the commit point — its slot reserved, its record in the EntityMap with a pending born. VIS-09: every destroy
            // too — its record's died pending. An exit before the append un-places and un-marks them (RollbackAfterFailedCommit).
            _prepareStaged = true;
            PlaceSpawns();
            MarkDestroysPending();
            MarkEnableDisablePending();

            // ── PREPARE complete. Deferred-cleanup enqueue is prepare-side bookkeeping (no visibility) — it runs before Append. ──
            _dbe.LogCommitPhase(TSN, "DeferredCleanup");

            // Enqueue current transaction's entities for deferred cleanup (single lock acquire for all entities).
            // Processing happens in Dispose (after cached indices are no longer relevant) or via FlushDeferredCleanups.
            if (_deferredEnqueueBatch is { Count: > 0 })
            {
                using var cleanupScope = TyphonEvent.BeginDataTransactionCleanup(TSN, _deferredEnqueueBatch.Count);
                _dbe.DeferredCleanupManager.EnqueueBatch(context.TailTSN, _deferredEnqueueBatch);
                _deferredEnqueueBatch.Clear();
            }

            // Conflicts are detected during the PREPARE pass — capture the flag before Append (AP-02: all validation precedes the point of no return).
            if (conflictSolver is { HasConflict: true })
            {
                hasConflict = true;
            }

            // AP-03: an engine that stopped while this commit prepared takes no more records: checked again at the last moment the commit can roll back.
            if (_dbe.CommitPipelineFailure is { } stoppedMeanwhile)
            {
                throw new DurabilityException(TyphonErrorCode.CommitPipelineStopped,
                    "The engine stopped committing after a commit failed between its append and the end of its publish; reopen the database to recover it.",
                    stoppedMeanwhile);
            }

            // ── AP-01: APPEND before PUBLISH. Build + append the WAL batch from the prepared (conflict-resolved) state. This is the point of no
            //    return (AP-02): nothing above made any change visible. ──
            _dbe.LogCommitPhase(TSN, "Append");
            // CK-13: the append records the batch's first LSN in _inFlightLsnFloor before publishing its frame. It stays there, keeping the checkpoint
            // below these records, until the publish below has put their page effects in memory.
            _dbe.CommitBeforeAppendProbe?.Invoke();
            var walHighLsn = AppendToWal(ref ctx);
            var published = false;

            // AP-03: past the append, page-cache back-pressure is waited out rather than thrown (up to CommitPublishBackpressureTimeout from here): a throw
            // could not roll this commit back, only stop the engine.
            var outerPublishSince = PagedMMF.PublishSince;
            PagedMMF.PublishSince = _dbe.PublishGivesUpOnBackpressureForTest ? 0 : Stopwatch.GetTimestamp();
            try
            {
                _dbe.CommitAfterAppendProbe?.Invoke();

                // ── PUBLISH (AP-01): now make the transaction's changes visible. Drain the component publish descriptors (clear IsolationFlag, bump
                //    CommitSequence, copy HEAD→cluster slot, release retained handler locks), then flush ECS pending operations (EntityMap spawn
                //    publication via PublishSpawns, DiedTSN destroys, EnabledBits). Every visibility act happens strictly after the Append. ──
                _dbe.LogCommitPhase(TSN, "Publish");
                PublishPreparedComponents();
                FlushEcsPendingOperations();
                ReleaseFirstSupplyClaims();   // the roots are published: a waiting supply finds them
                _prepareStaged = false;   // every staged effect published
                // Commit-discipline (SingleVersion) staged writes: publish to cluster HEAD + reconcile exact indexes (after PublishSpawns so a staged
                // write to a same-tx-spawned entity resolves in the EntityMap). Variant A / issue #392.
                _dbe.CommitBeforeStagedPublishProbe?.Invoke();
                PublishStagedCommitWrites();

                // CK-13: every page effect is in memory and marked dirty, so a checkpoint that passes these records now also collects their pages.
                published = true;
                Volatile.Write(ref _inFlightLsnFloor, 0);

                // CASC-02: its children and its destroys are published — a destroyer need not wait for this commit's durability.
                ReleaseCascadeMarks();

                // VIS-04, step 6: every effect is stamped, the card goes back: before the durability wait, and before Commit() returns (causality).
                ReleaseCommitCard();

                // ── WAIT (Immediate) + transition to Committed. Publish already ran and released handler locks, so the durability wait never spans a held
                //    lock; a wait timeout here means durability-uncertain, not rollback (AP-02). ──
                _dbe.LogCommitPhase(TSN, "WaitAndFinalize");
                WaitAndFinalize(ref ctx, walHighLsn, startTicks);
            }
            catch (Exception failure) when (State != TransactionState.Committed)
            {
                // AP-02/AP-03: past the append the records are in the log, and the commit is never rolled back. A publish that did not complete keeps its
                // card registered: what it left flagged is that commit's, which a reader resolves by the card's stamp, and the engine commits no more.
                if (!published)
                {
                    _commitCardSlot = -1;
                }

                CompleteAfterFailedPublish(walHighLsn, published, failure);
                throw;   // unreachable: CompleteAfterFailedPublish always throws
            }
            finally
            {
                PagedMMF.PublishSince = outerPublishSince;
            }

            _dbe.LogCommitPhase(TSN, "Complete");
            scope.ConflictDetected = hasConflict;
            return true;

        }
        catch (Exception) when (preparedEntries && State == TransactionState.InProgress)
        {
            // #1262: a commit that fails after PREPARE began on its entries, and before its append, rolls the transaction back here — the rollback Dispose
            // would give it — so it is over: Commit again returns false. Retried, PREPARE would run on entries the first attempt consumed. Past the append
            // the transaction is Committed (AP-03) and this does not apply.
            //
            // Its retained chain locks go first, as they did when the finally released them before Dispose's rollback: the rollback frees a spawn's chain
            // (its lock word with it) and voids, with no deadline, on chains another aborting commit may hold — rolled back holding them, two conflict-
            // handler commits that took their chains in crossed order wait for each other for good.
            ReleaseRetainedPublishLocks();
            RollbackAfterFailedCommit();
            throw;
        }
        finally
        {
            // First, so nothing below can leave it set: a commit that threw part-way stops holding the checkpoint back (CK-13). One whose publish failed
            // past its append has pinned its floor on the engine (AP-03).
            Volatile.Write(ref _inFlightLsnFloor, 0);

            // Next, and it cannot throw: a claim left behind would make every later supply of its component time out for the engine's life.
            ReleaseFirstSupplyClaims();

            // The same shape for unique keys (#1390), and deliberately here rather than anywhere earlier: a commit that published has its keys in the tree
            // by now, so the next commit's descent finds them with no claim to help it, and one that did not publish has nothing to find. A claim left
            // behind would refuse that key to every later commit for the engine's life.
            ReleaseUniqueKeyClaims();

            // Likewise the cascade handshake (CASC-02): an announcement or a dying mark left behind would make every later destroyer of a parent on its
            // stripe time out.
            ReleaseCascadeMarks();

            // AP-01 safety net: if the commit threw between PREPARE and PUBLISH, retained handler locks would otherwise be orphaned. On the success
            // path PublishPreparedComponents already drained and cleared _publishEntries, so this is a no-op.
            ReleaseRetainedPublishLocks();

            // VIS-03: a commit that failed before its commit point gives its card back. A leaked reservation would block its window for good, and a
            // retried commit of this transaction would wait on itself. A no-op on success (released after the publish).
            ReleaseCommitCard();
            PagedMMF.CommitMarks = outerCommitMarks;
            scope.Dispose();
        }
    }

    /// <summary>
    /// Commits this transaction using the default commit timeout (<see cref="TimeoutOptions.DefaultCommitTimeout"/>). See
    /// <see cref="Commit(ref UnitOfWorkContext, ConcurrencyConflictHandler)"/> for the full contract.
    /// </summary>
    /// <param name="handler">Optional write-write conflict resolver. <see langword="null"/> to accept the default last-writer-wins behavior.</param>
    /// <returns>
    /// <see langword="true"/> if the transaction committed (including read-only and empty transactions); <see langword="false"/> if it was already committed or
    /// rolled back.
    /// </returns>
    /// <exception cref="CommitDurabilityUncertainException">
    /// The write is committed but its on-media durability was not confirmed within the wait. Only reachable on the FUA/Immediate durability path.
    /// </exception>
    public bool Commit(ConcurrencyConflictHandler handler = null)
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
        return Commit(ref ctx, handler);
    }

}