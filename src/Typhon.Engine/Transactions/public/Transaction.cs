// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    // Composed once per transaction (lazily), reused for every revision-chain lock acquisition on the read/resolve path
    // so Stopwatch.GetTimestamp() is not re-armed per Versioned slot per entity. Reset in Init(). See ChainLockWaitContext().
    private WaitContext _chainLockWc;
    private bool _chainLockWcResolved;

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
    // (IsolationFlag clear + TSN stamp, LCRI/CommitSequence bump, cluster Phase-B HEAD→slot memcpy, handler-lock release). PrepareComponent
    // records one PublishEntry here; the entries are drained by PublishComponent AFTER the WAL Append, so no change is made visible before
    // its records are appended. ElementRevisionHandle is a ref struct (cannot be stored), so the handle is re-derived in publish from the
    // blittable (FirstChunkId, CurRevisionIndex). Pooled, reused across pooled Transaction lifetimes; cleared after each commit.
    private List<PublishEntry> _publishEntries;

    // AP-01 Debug guard: set true after the WAL Append phase, asserted when the PUBLISH phase begins so any future reorder that publishes before
    // appending trips loudly in Debug builds. Reset at the start of each commit (pooled Transaction reuse).
    private bool _appendPhaseEnteredThisCommit;

    // Drain cursor into _publishEntries: entries [0, _publishDrainIndex) have been fully published (and their retained handler locks released). On a
    // mid-drain throw the abort path releases only [_publishDrainIndex, Count), so each retained lock is released exactly once.
    private int _publishDrainIndex;

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
        public short LastCommitRevisionIndex;
        public bool Created;
        public bool LockHeld;

        // Resolved physical location of the committing revision element, captured in PREPARE so PUBLISH reconstructs the handle without a locking walk.
        public int ElementChunkId;
        public bool ElementIsFirst;
        public short ElementIndexInChunk;

        // Cluster Phase-B coordinates resolved by PrepareClusterVersionedSlot (so publish needs no second EntityMap lookup).
        // ClusterCopyPending == true ⇒ a committed HEAD value must be copied into (ArchId, ClusterChunkId, SlotIndex)'s CompSlot.
        public bool ClusterCopyPending;
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
    public Transaction Next { get; internal set; }

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
    /// (Re)initializes this pooled transaction instance for a new lease: enters an epoch scope, binds the engine and owning unit of work, fixes the MVCC
    /// snapshot at <paramref name="tsn"/>, and selects the commit discipline. Called by the engine when a transaction is created; not for direct use.
    /// </summary>
    /// <param name="dbe">Owning database engine.</param>
    /// <param name="tsn">Transaction sequence number that fixes this transaction's MVCC read snapshot.</param>
    /// <param name="uow">Owning unit of work, or <see langword="null"/> for the standalone path (UoW id 0).</param>
    /// <param name="readOnly">When <see langword="true"/>, no <see cref="ChangeSet"/> or UoW is allocated and all writes are forbidden.</param>
    /// <param name="discipline">Durability discipline applied to SingleVersion-layout writes for the transaction's lifetime.</param>
    public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null, bool readOnly = false, CommitDiscipline discipline = CommitDiscipline.TickFence)
    {
        // Residual risk: _dbe.MMF.CreateChangeSet allocates and could throw OOM in extreme conditions, dropping the span.
        // Per project policy this is acceptable for a hot per-tx path.
        var initScope = TyphonEvent.BeginDataTransactionInit(tsn, uow?.UowId ?? 0);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. EnterScope/CreateChangeSet/PushHead are engine-internal.
        // If a future change adds a throw path, re-tag to variant B.
        _dbe = dbe;
        _epochManager = _dbe.EpochManager;
        _dbe.LogTxInitPhase(tsn, "entering epoch");
        _ = _epochManager.EnterScope(); // Depth unused: Transaction uses ExitScopeUnordered (not LIFO)
        _dbe.LogTxInitPhase(tsn, "epoch entered");
        _isDisposed = false;
        IsReadOnly = readOnly;
        OwningUnitOfWork = uow;
        _chainLockWcResolved = false;   // recompute the composed chain-lock deadline for this logical transaction
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
        _ownsChangeSet = !readOnly && uow?.ChangeSet == null;
        _changeSet = readOnly ? null : (uow?.ChangeSet ?? _dbe.MMF.CreateChangeSet());
        State = TransactionState.Created;
        TSN = tsn;

        _dbe.TransactionChain.PushHead(this);
        // PROFILING-SPAN-NO-THROW-END
        initScope.Dispose();
    }

    /// <summary>Reset all state for pooling reuse. Called by TransactionChain after unlinking.</summary>
    internal void Reset() => ResetCore();

    private protected override void ResetCore()
    {
        // Clean up ECS state BEFORE nulling _dbe — CleanupEcsState needs _dbe._archetypeStates
        CleanupEcsState();

        base.ResetCore();

        OwningUnitOfWork = null;
        OwnsUnitOfWork = false;
        IsReadOnly = false;
        Next = null;
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _deferredEnqueueBatch?.Clear();
    }

    /// <summary>Prepare for mutation via ArchetypeAccessor. Sets state to InProgress so Commit processes writes.</summary>
    internal override void PrepareForMutation()
    {
        // Residual risk: EnsureMutable can throw on programmer-error (read-only tx mutated) — accepted per intentional-validation policy.
        var scope = TyphonEvent.BeginDataTransactionPrepare(TSN);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw on the success path. EnsureMutable is intentional validation;
        // its ThrowHelper paths terminate the operation, so dropping the span there is acceptable.
        EnsureMutable();
        State = TransactionState.InProgress;
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
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
                _hasEntityMapCache = false;
            }
            return;
        }

        AssertThreadAffinity();

        // Read-only transactions have no ChangeSet, UoW, or commit/rollback path —
        // just exit the epoch scope and remove from the chain.
        if (IsReadOnly)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
                _hasEntityMapCache = false;
            }
            _isDisposed = true;
            ExitEpochAndRemove();
            return;
        }

        // Capture before ExitEpochAndRemove — Remove pools and Reset() nulls _dbe
        var dbe = _dbe;
        var tsn = TSN;

        dbe.LogTxDispose(tsn, "EnsureCompleted");
        EnsureCompleted();
        dbe.LogTxDispose(tsn, "ProcessDeferredCleanups");
        ProcessDeferredCleanups();
        dbe.LogTxDispose(tsn, "FlushAccessors");
        FlushAccessors();

        // Release the marks of a ChangeSet this transaction owns — after the flush and the deferred cleanups above, both of which may still dirty pages through
        // it. A shared UoW ChangeSet is NOT released here: its owner does that on its own dispose, and releasing another owner's marks is the over-release that
        // #385 was.
        if (_ownsChangeSet)
        {
            _changeSet?.ReleaseDirtyMarks();
        }
        // Mark disposed BEFORE ExitEpochAndRemove: Remove() pools the object, and a lock-free
        // CreateTransaction can immediately dequeue and Init it (_isDisposed = false). If we set _isDisposed = true AFTER Remove returns, we'd overwrite the
        // new owner's flag, causing their Dispose to skip Remove — leaking the chain node.
        _isDisposed = true;
        dbe.LogTxDispose(tsn, "ExitEpochAndRemove");
        ExitEpochAndRemove();
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
    private void ProcessDeferredCleanups()
    {
        if (_dbe.DeferredCleanupManager.QueueSize > 0)
        {
            var wc = WaitContext.FromTimeout(TimeoutOptions.Current.TransactionChainLockTimeout);
            if (_dbe.TransactionChain.Control.EnterSharedAccess(ref wc))
            {
                var isTail = _dbe.TransactionChain.Tail == this;
                var nextMinTSN = isTail ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
                _dbe.TransactionChain.Control.ExitSharedAccess();

                if (isTail)
                {
                    _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSN, _dbe, _changeSet);
                }
            }
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
        _dbe.TransactionChain.Remove(this);
        if (owningUow != null)
        {
            dbe.LogTxDispose(tsn, "UoWDispose");
            owningUow.Dispose();
        }
        dbe.LogTxDispose(tsn, "ExitEpochAndRemoveDone");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component Collections & Enumerators
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a mutable accessor over a <see cref="ComponentCollection{T}"/> field, bound to this transaction's <see cref="ChangeSet"/> so edits are tracked
    /// for commit/rollback.
    /// </summary>
    /// <typeparam name="T">Unmanaged element type of the collection.</typeparam>
    /// <param name="field">Reference to the collection field to wrap.</param>
    /// <returns>A mutable accessor over the collection's backing buffer.</returns>
    public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ComponentCollectionAccessor<T>(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field);
    }

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
    /// Returns an enumerator that streams entities via a secondary index in key order, filtered by MVCC visibility at this transaction's snapshot.
    /// </summary>
    /// <remarks>
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
        var sources = new List<IndexArchetypeSource>();
        var trees = new List<BTree<TKey, PersistentStore>>();
        var multi = new List<bool>();

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
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

            var layout = clusterState.Layout;
            sources.Add(new IndexArchetypeSource
            {
                ClusterState = clusterState,
                EngineState = es,
                RecordSize = meta._entityRecordSize,
                CompOffset = layout.ComponentOffset(compSlot),
                CompSize = layout.ComponentSize(compSlot),
                VersionedIndex = layout.SlotToVersionedIndex != null ? layout.SlotToVersionedIndex[compSlot] : -1,
            });

            trees.Add(typedIndex);
            multi.Add(field.AllowMultiple);
        }

        // ONE archetype holds this component — the common case, and the one an early-terminating caller depends on. Stream straight off its cursor and
        // materialise nothing: a caller that breaks after N entries must pay for N entries, not for the whole range. Draining up front turns
        // EnumerateIndex(startKey, MaxValue) + break-at-100 from O(100) into O(everything at or after startKey), which is what a YCSB-style scan does.
        if (trees.Count == 1)
        {
            return new IndexEntityEnumerator<T, TKey>(trees[0], multi[0], minKey, maxKey, sources[0], ct, this, _changeSet);
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

        return new IndexEntityEnumerator<T, TKey>(MergeRuns(runs), sources.ToArray(), ct, this, _changeSet);
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
        public ArchetypeClusterState ClusterState;
        public ArchetypeEngineState EngineState;
        public int RecordSize;
        public int CompOffset;
        public int CompSize;

        /// <summary>Position among the archetype's Versioned slots, or -1 when this component is SingleVersion here.</summary>
        public int VersionedIndex;
    }

    /// <summary>
    /// MVCC-correct streaming enumerator over the per-archetype index B+Trees backing one component.
    /// Use <see cref="CurrentComponent"/> for zero-copy ref access into page memory, or <see cref="Current"/> for a convenience copy.
    /// Supports both unique and AllowMultiple indexes.
    /// </summary>
    [PublicAPI]
    public ref struct IndexEntityEnumerator<T, TKey> where T : unmanaged where TKey : unmanaged
    {
        private readonly IndexMergeEntry<TKey>[] _entries;
        private readonly IndexArchetypeSource[] _sources;
        private readonly ChunkAccessor<PersistentStore>[] _clusterAccessors;
        private readonly ChunkAccessor<PersistentStore>[] _mapAccessors;
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
        private bool _multipleStarted;

        private ChunkAccessor<PersistentStore> _compRevAccessor;
        private ChunkAccessor<PersistentStore> _compContentAccessor;
        private readonly Transaction _tx;
        private readonly long _transactionTSN;
        private readonly int _componentOverhead;
        private int _entityCount;
        private long _currentPK;
        private TKey _currentKey;
        private ReadOnlySpan<byte> _currentComponentSpan;
        private bool _disposed;

        /// <summary>Streaming constructor — one archetype, one cursor, no materialisation.</summary>
        internal IndexEntityEnumerator(BTree<TKey, PersistentStore> index, bool allowMultiple, TKey minKey, TKey maxKey, IndexArchetypeSource source,
            ComponentTable ct, Transaction tx, ChangeSet changeSet) : this([], [source], ct, tx, changeSet)
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
            _multipleStarted = false;

            _clusterAccessors = new ChunkAccessor<PersistentStore>[sources.Length];
            _mapAccessors = new ChunkAccessor<PersistentStore>[sources.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                _clusterAccessors[i] = sources[i].ClusterState.ClusterSegment.CreateChunkAccessor(changeSet);
                _mapAccessors[i] = sources[i].EngineState.EntityMap.Segment.CreateChunkAccessor(changeSet);
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

            _compRevAccessor = ct.CompRevTableSegment.CreateChunkAccessor(changeSet);
            _compContentAccessor = ct.ComponentSegment.CreateChunkAccessor(changeSet);
            _tx = tx;
            _transactionTSN = tx.TSN;
            _componentOverhead = ct.ComponentOverhead;
            _entityCount = 0;
            _currentPK = 0;
            _currentKey = default;
            _currentComponentSpan = default;
            _disposed = false;
        }

        /// <summary>Returns this enumerator (enables the <c>foreach</c> pattern).</summary>
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

            while (TryNextEntry(out var key, out var location, out var sourceIndex))
            {
                var src = _sources[sourceIndex];

                var clusterChunkId = location >> 6;
                var slotIndex = location & 63;

                var clusterBase = _clusterAccessors[sourceIndex].GetChunkAddress(clusterChunkId);
                var occupancy = *(ulong*)clusterBase;
                if ((occupancy & (1UL << slotIndex)) == 0)
                {
                    continue;   // slot freed since the entry was written — a destroyed entity, not a visible one
                }

                var layout = src.ClusterState.Layout;
                var entityRaw = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                if (entityRaw == 0)
                {
                    continue;
                }


                if (src.VersionedIndex < 0)
                {
                    // SingleVersion in this archetype: the cluster slot IS the component.
                    _currentComponentSpan = new ReadOnlySpan<byte>(clusterBase + src.CompOffset + slotIndex * src.CompSize, src.CompSize);
                }
                else
                {
                    // Hinted, not plain TryGet: entity keys are dense, so the location-hint cache resolves in one bucket visit instead of a full
                    // hash + directory + chain walk. Identical semantics — the hint is a pure accelerator that falls back on any mismatch.
                    if (!src.EngineState.EntityMap.TryGetWithHint(EntityId.FromRaw(entityRaw).EntityKey, recordBuf, ref _mapAccessors[sourceIndex]))
                    {
                        continue;
                    }

                    var chainRoot = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, src.VersionedIndex);
                    if (chainRoot == 0)
                    {
                        continue;
                    }


                    var walked = RevisionChainReader.WalkChain(ref _compRevAccessor, chainRoot, _transactionTSN);
                    if (walked.IsFailure || walked.Value.CurCompContentChunkId == 0)
                    {
                        continue;   // not visible at this snapshot, or tombstoned
                    }


                    _currentComponentSpan = _compContentAccessor.GetChunkAsReadOnlySpan(walked.Value.CurCompContentChunkId).Slice(_componentOverhead);
                }

                _currentPK = entityRaw;
                _currentKey = key;

                if (++_entityCount % EpochRefreshInterval == 0)
                {
                    _tx.EnumerateRefreshEpoch();
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Pulls the next <c>(key, location)</c> from whichever source this enumerator has: a live B+Tree cursor when one archetype holds the component, or
        /// the pre-merged array when several do. Keeping the two shapes behind one call is what lets the resolution below stay single-bodied.
        /// </summary>
        private bool TryNextEntry(out TKey key, out int location, out int sourceIndex)
        {
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
                    key = _currentKey;
                    location = _currentValues[_currentValueIndex++];
                    return true;
                }

                // Remaining chunks of the current key's buffer before advancing the key. The guard mirrors the pre-#666 enumerator: NextChunk must not be
                // called before the first MoveNextKey, when no buffer is open yet.
                if (_multipleStarted && _innerMultiple.NextChunk())
                {
                    _currentValues = _innerMultiple.CurrentValues;
                    _currentValueIndex = 0;
                    continue;
                }

                if (!_innerMultiple.MoveNextKey())
                {
                    key = default;
                    location = 0;
                    return false;
                }

                _multipleStarted = true;
                _currentKey = _innerMultiple.CurrentKey;
                _currentValues = _innerMultiple.CurrentValues;
                _currentValueIndex = 0;
            }
        }

        /// <summary>Releases the per-archetype and component accessors (unpins accessed pages). Idempotent.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (var i = 0; i < _clusterAccessors.Length; i++)
            {
                _clusterAccessors[i].Dispose();
                _mapAccessors[i].Dispose();
            }

            if (_streaming)
            {
                if (_isAllowMultiple)
                {
                    _innerMultiple.Dispose();
                }
                else
                {
                    _innerUnique.Dispose();
                }
            }

            _compRevAccessor.Dispose();
            _compContentAccessor.Dispose();
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
    /// <see cref="EntityRef.TryRead{T}"/>, which resolve the cluster-or-flat slot location and apply
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
            // Couldn't find in the cache, get it from the index
            var result = GetCompRevInfoFromIndex(pk, info, TSN);
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
        int size = info.ComponentTable.ComponentStorageSize;
        var src = info.CompContentAccessor.GetChunkAsReadOnlySpan(compRevInfo.CurCompContentChunkId);
        src.Slice(info.ComponentTable.ComponentOverhead).CopyTo(new Span<byte>(Unsafe.AsPointer(ref t), size));

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
        ref var target = ref entity.Write<T>();
        target = comp;
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
        if (!_hasEntityMapCache || _entityMapCacheArchId != entityId.ArchetypeId)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
            }
            _entityMapCacheAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _entityMapCacheArchId = entityId.ArchetypeId;
            _hasEntityMapCache = true;
        }

        int recordSize = meta._entityRecordSize;
        byte* buf = stackalloc byte[recordSize];
        if (!es.EntityMap.TryGet(entityId.EntityKey, buf, ref _entityMapCacheAccessor))
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
        if (!_hasEntityMapCache || _entityMapCacheArchId != entityId.ArchetypeId)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
            }
            _entityMapCacheAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _entityMapCacheArchId = entityId.ArchetypeId;
            _hasEntityMapCache = true;
        }

        // TryGet copies full record, but we only need the header (first 14 bytes).
        // Use full record size to satisfy TryGet's contract, but stackalloc min header.
        int recordSize = meta._entityRecordSize;
        byte* fullBuf = stackalloc byte[recordSize];
        if (!es.EntityMap.TryGet(entityId.EntityKey, fullBuf, ref _entityMapCacheAccessor))
        {
            return false;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(fullBuf);
        return header.IsVisibleAt(TSN);
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
            return RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, chunkId, TSN);
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
    /// The <see cref="WaitContext"/> used to acquire a revision-chain lock while READING (resolve/walk), composed ONCE
    /// per transaction as the tighter of the owning UoW deadline and <c>RevisionChainLockTimeout</c>. Caching it collapses
    /// the per-walk <c>Stopwatch.GetTimestamp()</c> — previously paid for every Versioned slot of every entity opened — to
    /// a single QPC per transaction. The deadline is only consulted under lock contention, so a transaction-scoped budget
    /// is an adequate (and stricter) liveness backstop than a fresh per-acquisition one.
    /// </summary>
    internal WaitContext ChainLockWaitContext()
    {
        if (!_chainLockWcResolved)
        {
            _chainLockWc = OwningUnitOfWork != null
                ? OwningUnitOfWork.CreateContext(TimeoutOptions.Current.RevisionChainLockTimeout).WaitContext
                : WaitContext.FromTimeout(TimeoutOptions.Current.RevisionChainLockTimeout);
            _chainLockWcResolved = true;
        }
        return _chainLockWc;
    }

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

        // Get the chunk storing the revision we want to roll back as well as the index of the element
        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Validate CurRevisionIndex — chain compaction by another transaction's cleanup may have
        // shifted entry positions since our read. Best-effort (no lock).
        if (elementHandle.Element.ComponentChunkId != compRevInfo.CurCompContentChunkId)
        {
            var fixedIndex = ComponentRevisionManager.FindRevisionIndexByChunkId(ref compRevTableAccessor, firstChunkId, compRevInfo.CurCompContentChunkId, TSN);
            if (fixedIndex >= 0)
            {
                compRevInfo.CurRevisionIndex = fixedIndex;
                elementHandle = compRev.GetRevisionElement(fixedIndex);
            }
        }

        // Free the chunk storing the content (if any)
        if (compRevInfo.CurCompContentChunkId != 0)
        {
            componentSegment.FreeChunk(compRevInfo.CurCompContentChunkId);
        }

        // If we roll back a created component, we must delete the revision table chunk
        if ((compRevInfo.Operations & ComponentInfo.OperationType.Created) == ComponentInfo.OperationType.Created)
        {
            revTableSegment.FreeChunk(firstChunkId);

            // Early exit: the RevTable chunk is gone — continuing would access freed memory.
            return true;
        }

        // In case of update or delete, mark void the revision entry we added
        if ((compRevInfo.Operations & (ComponentInfo.OperationType.Updated | ComponentInfo.OperationType.Deleted)) != 0)
        {
            compRev.VoidElement(elementHandle);
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
    /// Detects write-write conflicts and resolves them via handler invocation or "last wins" semantics.
    /// Must be called inside the per-entity revision chain lock (when handler is provided) or outside (last-wins path).
    /// </summary>
    /// <remarks>
    /// Two complementary checks for the handler path:
    /// <list type="number">
    /// <item>CommitSequence: detects any commit since our read (monotonic counter, immune to revision index ordering and cleanup compaction).</item>
    /// <item>Invisible-commit TSN check: detects committed entries by higher-TSN transactions invisible to our snapshot.</item>
    /// </list>
    /// Without handler: uses the original index-based check as best-effort for "last wins".
    /// </remarks>
    private static void DetectAndResolveConflict(ref CommitContext context, long pk, ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, 
        ComponentRevision compRev, ref ComponentRevisionManager.ElementRevisionHandle elementHandle, short lastCommitRevisionIndex, int readCompChunkId, 
        bool lockHeld, long tsn, ushort uowId, DatabaseEngine dbe)
    {
        var conflictSolver = context.Solver;

        var hasConflict = (conflictSolver != null) ? 
            compRev.CommitSequence != compRevInfo.ReadCommitSequence || 
            (lastCommitRevisionIndex >= 0 && compRev.GetRevisionElement(lastCommitRevisionIndex).Element.TSN > tsn) : 
            lastCommitRevisionIndex >= compRevInfo.CurRevisionIndex;

        if (!hasConflict)
        {
            return;
        }

        // Record conflict for observability
        dbe?.RecordConflict();

        // Phase 6: Data:Transaction:Conflict instant. componentTypeId left as 0 — the enclosing
        // TransactionCommitComponent span (kind 22) carries the typeId, so the viewer correlates by parent.
        // conflictType encodes which detection path fired: 0 = handler-based (sequence/lcri), 1 = index-based fallback.
        var conflictType = (byte)(conflictSolver != null ? 0 : 1);
        TyphonEvent.EmitDataTransactionConflict(tsn, pk, 0, conflictType);

        // Save the orphan index before AddCompRev changes CurRevisionIndex
        var conflictOrphanIndex = compRevInfo.CurRevisionIndex;

        // Create a new revision for the resolved data (under existing lock when handler is provided)
        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, false, lockHeld);

        // Copy the dirty-write data to the new revision as starting point
        var dstChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
        var srcChunk = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId);
        var sizeToCopy = info.ComponentTable.ComponentTotalSize;
        new Span<byte>(srcChunk, sizeToCopy).CopyTo(new Span<byte>(dstChunk, sizeToCopy));

        // Update elementHandle to point to the new revision
        elementHandle = compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);

        // Invoke the handler to resolve the conflict (under lock, so committedData is guaranteed fresh)
        if (conflictSolver != null)
        {
            var lastCommitHandle = compRev.GetRevisionElement(lastCommitRevisionIndex);
            var overhead = info.ComponentTable.ComponentOverhead;
            var readChunkAddr = info.CompContentAccessor.GetChunkAddress(readCompChunkId) + overhead;
            var committingChunkAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.PrevCompContentChunkId) + overhead;
            var toCommitChunkAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + overhead;
            var committedChunkAddr = info.CompContentAccessor.GetChunkAddress(lastCommitHandle.Element.ComponentChunkId) + overhead;

            conflictSolver.Setup(pk, info, readChunkAddr, committedChunkAddr, committingChunkAddr, toCommitChunkAddr);
            context.Handler(ref conflictSolver);
        }

        // Void the orphaned entry at the old position and free its content chunk
        var conflictOrphan = compRev.GetRevisionElement(conflictOrphanIndex);
        if (conflictOrphan.Element.ComponentChunkId > 0)
        {
            info.CompContentSegment.FreeChunk(conflictOrphan.Element.ComponentChunkId);
        }
        conflictOrphan.Element.Void();
    }

    /// <summary>
    /// Relocates a revision entry to the end of the chain when our entry is at or behind LCRI.
    /// This happens when a later-created transaction committed at a higher index — without relocation,
    /// the chain walk would shadow our value with the older commit at the higher index.
    /// </summary>
    /// <remarks>Must be called under the per-entity revision chain exclusive lock.</remarks>
    private static ComponentRevisionManager.ElementRevisionHandle RelocateRevisionEntry(
        ComponentInfo info, ref ComponentInfo.CompRevInfo compRevInfo, ComponentRevision compRev, long tsn, ushort uowId)
    {
        // Save the chunk that holds our modified data
        var oldContentChunkId = compRevInfo.CurCompContentChunkId;

        // Create new entry at end of chain (under existing lock)
        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, uowId, false, true);

        // Copy our data to the new content chunk
        var srcAddr = info.CompContentAccessor.GetChunkAddress(oldContentChunkId);
        var dstAddr = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
        new Span<byte>(srcAddr, info.ComponentTable.ComponentTotalSize)
            .CopyTo(new Span<byte>(dstAddr, info.ComponentTable.ComponentTotalSize));

        // Free the old content chunk and void the orphaned entry. The old entry at PrevRevisionIndex retains
        // IsolationFlag=true which blocks deferred cleanup compaction. Voiding it makes it a harmless placeholder.
        info.CompContentSegment.FreeChunk(oldContentChunkId);
        compRev.GetRevisionElement(compRevInfo.PrevRevisionIndex).Element.Void();

        return compRev.GetRevisionElement(compRevInfo.CurRevisionIndex);
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
        public void Process(ref CommitContext ctx) => Tx.RollbackComponent(ref ctx);
    }

    /// <summary>
    /// PREPARE half of the AP-01 commit split: all fallible work for one component revision — acquires the revision chain lock (when a handler is
    /// provided; the lock is RETAINED for <see cref="PublishComponent"/> so detect→publish stays atomic for concurrent conflict resolution), detects
    /// and resolves write-write conflicts, mutates secondary/spatial/cluster (Phase-A) indexes, and notifies views — then records a
    /// <see cref="PublishEntry"/>. It does NOT clear IsolationFlag, does NOT bump LCRI/CommitSequence, and does NOT copy to the cluster slot: those are
    /// the visibility acts, deferred to PublishComponent AFTER the WAL Append (AP-01: append before publish).
    /// </summary>
    private void PrepareComponent(ref CommitContext context)
    {
        var pk = context.PrimaryKey;
        var info = context.Info;
        ref var compRevInfo = ref context.CompRevInfo;
        var conflictSolver = context.Solver;
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;
        var firstChunkId = compRevInfo.CompRevTableFirstChunkId;

        var compRev = new ComponentRevision(info, ref compRevInfo, firstChunkId, ref compRevTableAccessor, UowId);
        var lastCommitRevisionIndex = compRev.LastCommitRevisionIndex;
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
        if (conflictSolver != null)
        {
            ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(firstChunkId, true);
            var wcCommit = ComposeWaitContext(ref context.Ctx, TimeoutOptions.Current.RevisionChainLockTimeout);
            if (!lockHeader.Control.EnterExclusiveAccess(ref wcCommit))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/CommitConflict", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
            lockHeld = true;
            lastCommitRevisionIndex = lockHeader.LastCommitRevisionIndex;

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

            // Relocate if our entry is at or behind LCRI
            if (lastCommitRevisionIndex >= 0 && compRevInfo.CurRevisionIndex <= lastCommitRevisionIndex)
            {
                elementHandle = RelocateRevisionEntry(info, ref compRevInfo, compRev, TSN, UowId);
            }
        }

        var publishRecorded = false;
        try
        {
            DetectAndResolveConflict(ref context, pk, info, ref compRevInfo, compRev,
                ref elementHandle, lastCommitRevisionIndex, readCompChunkId, lockHeld, TSN, UowId, _dbe);

            // Maintain indices/spatial (fallible PREPARE work). The visibility acts — IsolationFlag clear, LCRI/CommitSequence bump, cluster HEAD copy —
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
            // visibility act, deferred to PublishComponent. copyToCluster is false for spawns (FinalizeSpawns handles cluster copy for those).
            bool copyToCluster = (compRevInfo.Operations & ComponentInfo.OperationType.Created) == 0;
            PrepareClusterVersionedSlot(pk, commitMeta, compRevInfo, readCompChunkId, info.ComponentTable, info.ComponentTypeId, copyToCluster,
                out clusterCopyPending, out clusterChunkId, out slotIndex, out compSlot);

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
                LastCommitRevisionIndex = lastCommitRevisionIndex,
                Created = (compRevInfo.Operations & ComponentInfo.OperationType.Created) != 0,
                LockHeld = lockHeld,
                ElementChunkId = elementHandle.ChunkId,
                ElementIsFirst = elementHandle.IsFirst,
                ElementIndexInChunk = elementHandle.ElementIndex,
                ClusterCopyPending = clusterCopyPending,
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
    /// TSN + clears IsolationFlag (the publication act), bumps LCRI/CommitSequence, copies the committed HEAD value into the cluster slot (Phase B), and
    /// releases the per-entity revision-chain lock retained by <see cref="PrepareComponent"/>. MUST run only after the transaction's WAL Append.
    /// </summary>
    private void PublishComponent(in PublishEntry e)
    {
        var info = e.Info;
        ref var compRevTableAccessor = ref info.CompRevTableAccessor;

        // Reconstruct the revision handle from the coordinates resolved in PREPARE (no locking walk — AP-03) and clear IsolationFlag (THE publication act).
        var elementHandle = new ComponentRevisionManager.ElementRevisionHandle(ref compRevTableAccessor, e.ElementChunkId, e.ElementIsFirst, e.ElementIndexInChunk);
        elementHandle.Commit(TSN);

        // LCRI / CommitSequence bookkeeping — header field writes (under the retained handler lock when LockHeld).
        ref var header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(e.FirstChunkId, true);
        header.LastCommitRevisionIndex = Math.Max(e.LastCommitRevisionIndex, e.CurRevisionIndex);
        if (!e.Created)
        {
            header.CommitSequence++;
        }

        // Cluster Phase B: copy the committed HEAD value into the cluster slot (visible to bulk iteration).
        if (e.ClusterCopyPending)
        {
            PublishClusterVersionedSlot(e);
        }

        // Release the per-entity revision-chain lock retained by PrepareComponent (handler path).
        if (e.LockHeld)
        {
            ref var lockHeader = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(e.FirstChunkId);
            lockHeader.Control.ExitExclusiveAccess();
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
        while (_publishDrainIndex < _publishEntries.Count)
        {
            PublishComponent(_publishEntries[_publishDrainIndex]);
            _publishDrainIndex++;
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
            }
        }

        _publishEntries.Clear();
        _publishDrainIndex = 0;
    }

    /// <summary>
    /// PREPARE half of the cluster commit (AP-01): updates per-archetype B+Tree indexes (Phase A) and notifies views, and resolves the cluster-slot
    /// coordinates for the deferred Phase-B copy. Phase B (the HEAD→cluster-slot memcpy — the visibility act) is performed by
    /// <see cref="PublishClusterVersionedSlot"/> after the WAL Append. Single EntityMap lookup; the resolved coordinates are returned so publish does
    /// not repeat it. <paramref name="copyToCluster"/> is false for spawns (FinalizeSpawns handles cluster copy for those).
    /// </summary>
    private void PrepareClusterVersionedSlot(long pk, ArchetypeMetadata meta, ComponentInfo.CompRevInfo compRevInfo, int readCompChunkId,
        ComponentTable table, int componentTypeId, bool copyToCluster,
        out bool clusterCopyPending, out int outClusterChunkId, out byte outSlotIndex, out byte outCompSlot)
    {
        clusterCopyPending = false;
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

        int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(recordBuf);
        byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(recordBuf);
        int clusterLocation = clusterChunkId * 64 + slotIndex;
        outClusterChunkId = clusterChunkId;
        outSlotIndex = slotIndex;

        // Phase B (HEAD→cluster slot copy) is required iff a committed value exists and this is not a spawn — deferred to PublishClusterVersionedSlot.
        clusterCopyPending = copyToCluster && compRevInfo.CurCompContentChunkId != 0;

        // Phase A: Update per-archetype B+Tree indexes for this component's indexed fields
        if (hasIndexes)
        {
            // Read new and old field values from CONTENT CHUNKS (not cluster slot — cluster hasn't been updated yet).
            byte* newComp = compRevInfo.CurCompContentChunkId != 0 ?
                _clusterCommitContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId) + table.ComponentOverhead : null;
            byte* oldComp = readCompChunkId != 0 ?
                _clusterCommitContentAccessor.GetChunkAddress(readCompChunkId) + table.ComponentOverhead : null;

            ReconcileClusterIndexAndViews(es, clusterState, compSlot, clusterChunkId, clusterLocation, entityId, oldComp, newComp);
        }

        // Phase B (HEAD→cluster slot copy) is deferred to PublishClusterVersionedSlot (AP-01). The cluster dirty bit it sets is what later drives
        // DetectClusterMigrations at tick fence (spatial per-cell index — same deferred-update pattern as SV cluster spatial, Issue #230 Phase 3).
    }

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
        _clusterCommitClusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
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
    /// Cost is one extra optimistic descent per unique-index write. That is not free on a commit path, and it can be removed later by surfacing the
    /// <c>InsertArguments.Added</c> flag that <c>AddOrUpdateCore</c> already computes — the check is here rather than inside <c>Add</c> because that method's
    /// body is contractually throw-free (see its PROFILING-SPAN-NO-THROW markers).
    /// </para>
    /// </remarks>
    internal static unsafe void RejectUniqueIndexCollision<TStore>(ref ClusterIndexField<TStore> field, byte* newKeyPtr, int clusterLocation,
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
                        *elementIdPtr = field.Index.MoveValue(oldComp + field.FieldOffset, newComp + field.FieldOffset, *elementIdPtr, clusterLocation,
                            ref idxAccessor, out _, out _);
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
                    // cluster tail slot so destroy/update can target this entity's entry (mirrors FinalizeSpawns).
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
                for (int v = 0; v < views.Length; v++)
                {
                    var reg = views[v];
                    if (reg.View.IsDisposed)
                    {
                        continue;
                    }

                    if (newComp != null && oldComp != null)
                    {
                        // Move: emit old and new keys
                        var oldKey = KeyBytes8.FromPointer(oldComp + field.FieldOffset, field.FieldSize);
                        var newKey = KeyBytes8.FromPointer(newComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)(fi & 0x3F);
                        reg.DeltaBuffer.TryAppend(entityId, oldKey, newKey, TSN, flags, reg.ComponentTag);
                    }
                    else if (newComp != null)
                    {
                        // Add: isCreation flag
                        var newKey = KeyBytes8.FromPointer(newComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)((fi & 0x3F) | 0x40); // isCreation
                        reg.DeltaBuffer.TryAppend(entityId, default, newKey, TSN, flags, reg.ComponentTag);
                    }
                    else if (oldComp != null)
                    {
                        // Remove: isDeletion flag
                        var oldKey = KeyBytes8.FromPointer(oldComp + field.FieldOffset, field.FieldSize);
                        byte flags = (byte)((fi & 0x3F) | 0x80); // isDeletion
                        reg.DeltaBuffer.TryAppend(entityId, oldKey, default, TSN, flags, reg.ComponentTag);
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
    /// per-component publish cost can be measured in isolation from BUILD/APPEND/WAIT. Idempotent for non-indexed components (re-memcpy +
    /// re-SetDirty).</summary>
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
                PublishStagedEntry(info, staged.Key, staged.Value);
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

        // Exact-index reconcile BEFORE the HEAD memcpy: old key still lives in the HEAD slot, new key in the staged slot (CM-05/AC-11).
        if (clusterState.IndexSlots != null)
        {
            ReconcileClusterIndexAndViews(es, clusterState, compSlot, clusterChunkId, clusterLocation, entityId, headPtr, staged);
        }

        // Visibility act: publish the staged value to the cluster HEAD, then mark dirty (CM-03: memcpy THEN dirty).
        Unsafe.CopyBlockUnaligned(headPtr, staged, (uint)compSize);
        // Same as the Versioned publish above: compSlot is the component being published, so name it rather than falling back to "emit everything" (M31).
        clusterState.SetDirty(clusterChunkId, slotIndex, compSlot);
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

        // Nothing to do if the transaction is empty
        if (State is TransactionState.Created)
        {
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
    private void BuildCommitBatch(ref CommitBatchBuilder batch)
    {
        // Spawn lifecycle.
        if (_spawnedEntities != null)
        {
            for (int i = 0; i < _spawnedEntities.Count; i++)
            {
                var s = _spawnedEntities[i];
                batch.AddSpawn((long)s.Id.RawValue, s.Id.ArchetypeId, s.EnabledBits);
            }

            // #395 Face B (design D5): under Commit discipline a spawn must be ATOMICALLY durable — also WAL-log its SingleVersion component VALUES
            // (one Slot upsert each). Without this the spawn lifecycle is logged but the SV values are not (they only ride the cluster SoA / content
            // chunk, persisted by the next checkpoint), so a hard crash before that checkpoint recovers the entity alive-but-default — a phantom.
            // Recovery aggregates the Spawn + these Slots by EntityId and applies them together (ApplySpawnedEntity → cluster slot claim + SoA value
            // write), so the entity recovers with its values. TickFence spawns stay checkpoint-durable by design (no per-commit SV WAL). Versioned
            // spawn values are already logged via their revision chains (SingleCache loop below); Transient is never logged. Read from the
            // spawn-staging content chunk (entry.Loc[slot]) — FinalizeSpawns runs later (PUBLISH), so the cluster SoA is not populated yet, but the
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

                        int locChunkId = s.Loc[slot];
                        if (locChunkId <= 0)
                        {
                            continue;   // component not provided at spawn
                        }

                        var info = GetComponentInfo(table.Definition.POCOType);
                        var payload = info.CompContentAccessor.GetChunkAsReadOnlySpan(locChunkId);
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
                batch.AddDestroy((long)id.RawValue);
            }
        }

        // Enable/disable lifecycle (absolute bits).
        if (_pendingEnableDisable != null)
        {
            foreach (var enableEntry in _pendingEnableDisable)
            {
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
                    walHighLsn = _dbe.DurabilityLog.Append(ref batch, ref wc);
                }

                persistScope.WalLsn = walHighLsn;
            }
            finally
            {
                persistScope.Dispose();
            }
        }

        _appendPhaseEnteredThisCommit = true; // AP-01 guard: the append phase has run; publish is now permitted.
        return walHighLsn;
    }

    /// <summary>
    /// AP-01 WAIT/finalize step: for Immediate durability, waits until the appended LSN is durable, then transitions the transaction to Committed and
    /// records metrics. Runs AFTER publish (so the per-entity handler locks are already released — the wait never spans an fsync while holding them).
    /// A wait timeout here occurs post-publish: the transaction is logically committed (AP-02), so the exception means "durability uncertain", not
    /// "rolled back" (the publish-then-surface contract; the exception type is refined in the AP-02/AP-03 hardening step).
    /// </summary>
    private void WaitAndFinalize(ref UnitOfWorkContext ctx, long walHighLsn, long startTicks)
    {
        TyphonException durabilityUncertain = null;

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
            catch (TyphonException ex)
            {
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

        // Read-only transactions have nothing to commit — trivially succeed
        if (IsReadOnly)
        {
            return true;
        }

        // Nothing to commit if the transaction is empty, but still process deferred cleanups
        // in case this transaction (as the tail) is blocking cleanup of entities modified by others.
        if (State is TransactionState.Created)
        {
            if (_dbe.DeferredCleanupManager.QueueSize > 0)
            {
                var wcDeferred = ComposeWaitContext(ref ctx, TimeoutOptions.Current.TransactionChainLockTimeout);
                if (_dbe.TransactionChain.Control.EnterSharedAccess(ref wcDeferred))
                {
                    var isTailForDeferred = _dbe.TransactionChain.Tail == this;
                    var nextMinTSNDeferred = isTailForDeferred ? _dbe.TransactionChain.ComputeNextMinTSN() : 0;
                    _dbe.TransactionChain.Control.ExitSharedAccess();

                    if (isTailForDeferred)
                    {
                        _dbe.DeferredCleanupManager.ProcessDeferredCleanups(TSN, nextMinTSNDeferred, _dbe, _changeSet);
                    }
                }
            }

            return true;
        }

        // Can't commit a transaction already processed
        if (State is TransactionState.Rollbacked or TransactionState.Committed)
        {
            return false;
        }

        // ── Yield point: safe to cancel before any modifications ──
        ctx.ThrowIfCancelled();

        var scope = TyphonEvent.BeginTransactionCommit(TSN);
        // Cumulative commit counter — monotonic. Incrementing BEFORE the commit body means a commit that throws mid-way still gets
        // counted; the rollback counter will NOT fire for that case (Commit and Rollback are mutually exclusive paths). For gauge
        // purposes "attempted commit" vs "successful commit" is close enough; a more precise variant would move this to a success
        // path but currently Transaction.Commit has no single success-return point.
        _dbe.TransactionChain.IncrementCommitTotal();
        try
        {
            // Must sit inside the try so the finally still runs if this property access ever gains side effects that can throw.
            scope.ComponentCount = _componentInfos.Count;

            var startTicks = Stopwatch.GetTimestamp();

            _dbe.LogCommitStart(TSN, _componentInfos.Count);

            // ── Holdoff: entire commit loop runs to completion ──
            using var holdoff = ctx.EnterHoldoff();

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

            // Prepare ECS destroy operations: create component-level tombstone revisions in the PREPARE pass so it can handle index removal, WAL, and
            // cleanup. The entity-level destroy visibility (DiedTSN) is applied later, in the PUBLISH pass (FlushEcsPendingOperations).
            PrepareEcsDestroys();

            _dbe.LogCommitPhase(TSN, "CommitComponentCore");

            // Phase 6: Data:Transaction:Validate span over the per-component-type validation pass.
            using var validateScope = TyphonEvent.BeginDataTransactionValidate(TSN, _componentInfos.Count);

            // Process every Component Type and their components (old CRUD path — Versioned only). AP-01: this is the PREPARE pass — all fallible work,
            // no visibility. The matching PUBLISH pass (PublishPreparedComponents) runs after the WAL Append.
            var prepareAction = new PrepareAction { Tx = this };
            foreach (var kvp in _componentInfos)
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
                        kvp.Value.ForEachMutableEntry(ref context, ref prepareAction);
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

            // ── AP-01: APPEND before PUBLISH. Build + append the WAL batch from the prepared (conflict-resolved) state. This is the point of no
            //    return (AP-02): nothing above made any change visible. ──
            _dbe.LogCommitPhase(TSN, "Append");
            var walHighLsn = AppendToWal(ref ctx);

            // ── PUBLISH (AP-01): now make the transaction's changes visible. Drain the component publish descriptors (clear IsolationFlag, bump
            //    LCRI/CommitSequence, copy HEAD→cluster slot, release retained handler locks), then flush ECS pending operations (EntityMap spawn
            //    inserts via FinalizeSpawns, DiedTSN destroys, EnabledBits). Every visibility act happens strictly after the Append. ──
            _dbe.LogCommitPhase(TSN, "Publish");
            PublishPreparedComponents();
            FlushEcsPendingOperations();
            // Commit-discipline (SingleVersion) staged writes: publish to cluster HEAD + reconcile exact indexes (after FinalizeSpawns so a staged
            // write to a same-tx-spawned entity resolves in the EntityMap). Variant A / issue #392.
            PublishStagedCommitWrites();

            // ── WAIT (Immediate) + transition to Committed. Publish already ran and released handler locks, so the durability wait never spans a held
            //    lock; a wait timeout here means durability-uncertain, not rollback (AP-02). ──
            _dbe.LogCommitPhase(TSN, "WaitAndFinalize");
            WaitAndFinalize(ref ctx, walHighLsn, startTicks);
            _dbe.LogCommitPhase(TSN, "Complete");
            scope.ConflictDetected = hasConflict;
            return true;

        }
        finally
        {
            // AP-01 safety net: if the commit threw between PREPARE and PUBLISH, retained handler locks would otherwise be orphaned. On the success
            // path PublishPreparedComponents already drained and cleared _publishEntries, so this is a no-op.
            ReleaseRetainedPublishLocks();
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