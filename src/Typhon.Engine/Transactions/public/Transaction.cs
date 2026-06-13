// unset

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[PublicAPI]
[DebuggerDisplay("TSN {TSN}, State: {State}")]
public unsafe partial class Transaction : EntityAccessor
{
    private const int RandomAccessCachedPagesCount = 8;
    private const int DeferredEnqueueBatchCapacity = 256;

    public enum TransactionState
    {
        Invalid = 0,
        Created,            // New object, no operation done yet
        InProgress,         // At least one operation added to the transaction
        Rollbacked,         // Was rollbacked by the user or during dispose
        Committed           // Was committed by the user
    }

    public TransactionState State { get; private set; }

    private int? _committedOperationCount;
    private int _deletedComponentCount;

    // Reused across pooled Transaction lifetimes — collects deferred enqueue entries per commit/rollback
    // to flush in a single batch (one lock acquire instead of N). Never re-allocated after warmup.
    private List<DeferredCleanupManager.CleanupEntry> _deferredEnqueueBatch;

    // Hoisted accessors for batch index maintenance (set per component type during Commit)
    private ChunkAccessor<PersistentStore>[] _batchIndexAccessors;
    private ChunkAccessor<PersistentStore> _batchTailAccessor;
    private bool _batchIndexActive;
    private int _batchEntityCount;

    // Lazy-cached accessors for CommitClusterVersionedSlot — persists across calls within same commit, keyed by archetype ID
    private ushort _clusterCommitArchId;
    private ChunkAccessor<PersistentStore> _clusterCommitMapAccessor;
    private ChunkAccessor<PersistentStore> _clusterCommitContentAccessor;
    private ChunkAccessor<PersistentStore> _clusterCommitClusterAccessor;
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
        public long Pk;
        public int FirstChunkId;
        public int CurCompContentChunkId;
        public short CurRevisionIndex;
        public short LastCommitRevisionIndex;
        public bool IsClusterEntity;
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

    public Transaction Next { get; internal set; }

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

    public void Init(DatabaseEngine dbe, long tsn, UnitOfWork uow = null, bool readOnly = false)
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
#if DEBUG
        _debugOwningThreadId = Environment.CurrentManagedThreadId;
#endif
        _committedOperationCount = null;
        _deletedComponentCount = 0;
        _entityOperationCount = 0;
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
        Debug.Assert(IsLegalTransition(State, newState),
            $"Illegal transaction state transition: {State} → {newState}");
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

    public override void Dispose()
    {
        // Dispose transient batch accessors first — safe even on double-dispose or if never created
        // (ChunkAccessor<PersistentStore>.Dispose() is idempotent: no-op when _segment==null).
        _batchTailAccessor.Dispose();
        _clusterCommitMapAccessor.Dispose();
        _clusterCommitContentAccessor.Dispose();
        _clusterCommitClusterAccessor.Dispose();
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

    public ComponentCollectionAccessor<T> CreateComponentCollectionAccessor<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ComponentCollectionAccessor<T>(_changeSet, _dbe.GetComponentCollectionVSBS<T>(), ref field);
    }

    public ReadOnlyCollectionEnumerator<T> GetReadOnlyCollectionEnumerator<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        return new ReadOnlyCollectionEnumerator<T>(_dbe.GetComponentCollectionVSBS<T>(), field._bufferId);
    }

    public int GetComponentCollectionRefCounter<T>(ref ComponentCollection<T> field) where T : unmanaged
    {
        AssertThreadAffinity();
        var vsbs = _dbe.GetComponentCollectionVSBS<T>();
        using var a = new VariableSizedBufferAccessor<T, PersistentStore>(vsbs, field._bufferId);

        return a.RefCounter;
    }
    
    [PublicAPI]
    public ref struct ReadOnlyCollectionEnumerator<T> where T : unmanaged
    {
        private BufferEnumerator<T, PersistentStore> _enumerator;

        public ReadOnlyCollectionEnumerator(VariableSizedBufferSegment<T, PersistentStore> vsbs, int bufferId)
        {
            _enumerator = vsbs.EnumerateBuffer(bufferId);
        }

        public ReadOnlyCollectionEnumerator<T> GetEnumerator() => this;

        public ref readonly T Current
        {
            get => ref _enumerator.Current;
        }
        
        public bool MoveNext() => _enumerator.MoveNext();

        public void Dispose() => _enumerator.Dispose();
    }

    /// <summary>
    /// Returns an enumerator that streams entities via a secondary index in key order, filtered by MVCC visibility at this transaction's snapshot.
    /// </summary>
    public IndexEntityEnumerator<T, TKey> EnumerateIndex<T, TKey>(IndexRef indexRef, TKey minKey, TKey maxKey) where T : unmanaged where TKey : unmanaged
    {
        AssertThreadAffinity();
        indexRef.Validate();

        var ct = indexRef.Table;

        if (indexRef.IsPrimaryKey)
        {
            throw new InvalidOperationException("PK B+Tree has been removed. Use ECS queries with EntityMap instead.");
        }

        var ifi = ct.IndexedFieldInfos[indexRef.FieldIndex];
        if (ifi.Index is not BTree<TKey, PersistentStore> skIndex)
        {
            throw new InvalidOperationException(
                $"TKey type mismatch: index uses '{ifi.Index.GetType().GenericTypeArguments[0].Name}', caller specified '{typeof(TKey).Name}'.");
        }

        BTree<TKey, PersistentStore> typedIndex = skIndex;

        return new IndexEntityEnumerator<T, TKey>(typedIndex, ct, this, minKey, maxKey, _changeSet);
    }

    /// <summary>
    /// MVCC-correct streaming enumerator over any index B+Tree leaf chain.
    /// Use <see cref="CurrentComponent"/> for zero-copy ref access into page memory, or <see cref="Current"/> for a convenience copy.
    /// Supports both unique and AllowMultiple indexes (including PK).
    /// </summary>
    [PublicAPI]
    public ref struct IndexEntityEnumerator<T, TKey> where T : unmanaged where TKey : unmanaged
    {
        // Unique index path
        private BTree<TKey, PersistentStore>.RangeEnumerator _innerUnique;
        // AllowMultiple index path
        private BTree<TKey, PersistentStore>.RangeMultipleEnumerator _innerMultiple;
        private ReadOnlySpan<int> _currentValues;
        private int _currentValueIndex;

        private ChunkAccessor<PersistentStore> _compRevAccessor;
        private ChunkAccessor<PersistentStore> _compContentAccessor;
        private readonly Transaction _tx;
        private readonly long _transactionTSN;
        private readonly int _componentOverhead;
        private readonly bool _isAllowMultiple;
        private int _entityCount;
        private long _currentPK;
        private TKey _currentKey;
        private ReadOnlySpan<byte> _currentComponentSpan;
        private bool _disposed;

        internal IndexEntityEnumerator(BTree<TKey, PersistentStore> index, ComponentTable ct, Transaction tx, TKey minKey, TKey maxKey, ChangeSet changeSet)
        {
            _isAllowMultiple = index.AllowMultiple;
            if (_isAllowMultiple)
            {
                _innerMultiple = index.EnumerateRangeMultiple(minKey, maxKey);
                _innerUnique = default;
            }
            else
            {
                _innerUnique = index.EnumerateRange(minKey, maxKey);
                _innerMultiple = default;
            }

            _compRevAccessor = ct.CompRevTableSegment.CreateChunkAccessor(changeSet);
            _compContentAccessor = ct.ComponentSegment.CreateChunkAccessor(changeSet);
            _tx = tx;
            _transactionTSN = tx.TSN;
            _componentOverhead = ct.ComponentOverhead;
            _entityCount = 0;
            _currentPK = 0;
            _currentKey = default;
            _currentComponentSpan = default;
            _currentValues = default;
            _currentValueIndex = 0;
            _disposed = false;
        }

        public IndexEntityEnumerator<T, TKey> GetEnumerator() => this;

        /// <summary>Convenience accessor — copies the component into a tuple. Prefer <see cref="CurrentComponent"/> for zero-copy.</summary>
        public (long EntityPK, TKey Key, T Component) Current => (_currentPK, _currentKey, MemoryMarshal.AsRef<T>(_currentComponentSpan));

        /// <summary>Zero-copy ref into the epoch-protected page memory. Valid until the next <see cref="MoveNext"/> call.</summary>
        public ref readonly T CurrentComponent => ref MemoryMarshal.AsRef<T>(_currentComponentSpan);

        /// <summary>The primary key of the current entity.</summary>
        public long CurrentEntityPK => _currentPK;

        /// <summary>The index key of the current entry.</summary>
        public TKey CurrentKey => _currentKey;

        public bool MoveNext()
        {
            if (_isAllowMultiple)
            {
                return MoveNextMultiple();
            }

            return MoveNextUnique();
        }

        private bool MoveNextUnique()
        {
            while (_innerUnique.MoveNext())
            {
                var kv = _innerUnique.Current;
                int compRevFirstChunkId = kv.Value;

                // MVCC visibility check
                var result = RevisionChainReader.WalkChain(ref _compRevAccessor, compRevFirstChunkId, _transactionTSN);
                if (result.IsFailure || result.Value.CurCompContentChunkId == 0)
                {
                    continue;
                }

                // Recover EntityPK from CompRevStorageHeader
                _currentPK = _compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).EntityPK;
                _currentKey = kv.Key;

                // Store span into page memory — no copy
                var src = _compContentAccessor.GetChunkAsReadOnlySpan(result.Value.CurCompContentChunkId);
                _currentComponentSpan = src.Slice(_componentOverhead);

                if (++_entityCount % EpochRefreshInterval == 0)
                {
                    _tx.EnumerateRefreshEpoch();
                }

                return true;
            }

            return false;
        }

        private bool MoveNextMultiple()
        {
            while (true)
            {
                // Try to consume remaining values from current key's VSBS buffer
                while (_currentValueIndex < _currentValues.Length)
                {
                    int compRevFirstChunkId = _currentValues[_currentValueIndex++];

                    var result = RevisionChainReader.WalkChain(ref _compRevAccessor, compRevFirstChunkId, _transactionTSN);
                    if (result.IsFailure || result.Value.CurCompContentChunkId == 0)
                    {
                        continue;
                    }

                    _currentPK = _compRevAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId).EntityPK;

                    // Store span into page memory — no copy
                    var src = _compContentAccessor.GetChunkAsReadOnlySpan(result.Value.CurCompContentChunkId);
                    _currentComponentSpan = src.Slice(_componentOverhead);

                    if (++_entityCount % EpochRefreshInterval == 0)
                    {
                        _tx.EnumerateRefreshEpoch();
                    }

                    return true;
                }

                // Try next chunk of the same key's VSBS buffer (guard: _currentValues.Length > 0 prevents calling NextChunk before the first MoveNextKey,
                // when no VSBS buffer is open yet)
                if (_currentValues.Length > 0 && _innerMultiple.NextChunk())
                {
                    _currentValues = _innerMultiple.CurrentValues;
                    _currentValueIndex = 0;
                    continue;
                }

                // Advance to the next key
                if (!_innerMultiple.MoveNextKey())
                {
                    return false;
                }

                _currentKey = _innerMultiple.CurrentKey;
                _currentValues = _innerMultiple.CurrentValues;
                _currentValueIndex = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_isAllowMultiple)
            {
                _innerMultiple.Dispose();
            }
            else
            {
                _innerUnique.Dispose();
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
    /// Read a component by PK from the ComponentTable revision chain. Used by the query engine for predicate evaluation.
    /// Performs MVCC-visible revision walk — more efficient than Open().Read() for single-component access because it doesn't resolve all archetype slots.
    /// </summary>
    /// <summary>
    /// Query-engine read primitive. Reads a single component by PK via MVCC revision chain walk.
    /// More efficient than Open().Read() for query evaluation — resolves only the requested component, not all archetype slots.
    /// </summary>
    [PublicAPI]
    public bool QueryRead<T>(long pk, out T t) where T : unmanaged
    {
        AssertThreadAffinity();
        var componentType = typeof(T);
        var info = GetComponentInfo(componentType);

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

        var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
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
        if (es.EntityMap.TryGet(entityId.EntityKey, buf, ref _entityMapCacheAccessor))
        {
            return EntityRecordAccessor.GetLocation(buf, slot);
        }

        return 0;
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

        var meta = ArchetypeRegistry.GetMetadata(entityId.ArchetypeId);
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
            var commitMeta = ArchetypeRegistry.GetMetadata(archId);
            bool isClusterEntity = commitMeta.IsClusterEligible && commitMeta.VersionedSlotMask != 0 && _dbe._archetypeStates[archId]?.ClusterState != null;

            var clusterCopyPending = false;
            var clusterChunkId = 0;
            byte slotIndex = 0;
            byte compSlot = 0;

            if (!isClusterEntity)
            {
                // Legacy path: per-ComponentTable index/spatial maintenance (unchanged)
                if (compRevInfo.CurCompContentChunkId != 0)
                {
                    if (_batchIndexActive)
                    {
                        IndexMaintainer.UpdateIndices(pk, info, compRevInfo, readCompChunkId, _changeSet, TSN, _batchIndexAccessors, ref _batchTailAccessor);
                    }
                    else
                    {
                        IndexMaintainer.UpdateIndices(pk, info, compRevInfo, readCompChunkId, _changeSet, TSN);
                    }
                }
                else if (readCompChunkId != 0)
                {
                    if (_batchIndexActive)
                    {
                        IndexMaintainer.RemoveSecondaryIndices(pk, info, readCompChunkId, compRevInfo.CompRevTableFirstChunkId, _changeSet, TSN,
                            _batchIndexAccessors, ref _batchTailAccessor);
                    }
                    else
                    {
                        IndexMaintainer.RemoveSecondaryIndices(pk, info, readCompChunkId, compRevInfo.CompRevTableFirstChunkId, _changeSet, TSN);
                    }
                }

                // Versioned spatial index maintenance — after B+Tree indices are updated
                if (info.ComponentTable.SpatialIndex != null)
                {
                    if (compRevInfo.CurCompContentChunkId != 0)
                    {
                        SpatialMaintainer.UpdateSpatial(pk, compRevInfo.CurCompContentChunkId, info.ComponentTable, ref info.CompContentAccessor, _changeSet);
                    }
                    else if (readCompChunkId != 0)
                    {
                        SpatialMaintainer.RemoveFromSpatial(pk, readCompChunkId, info.ComponentTable, _changeSet);
                    }
                }
            }
            else
            {
                // Cluster path — Phase A only here (per-archetype B+Tree index updates + view notify). Phase B (HEAD→cluster slot copy) is the
                // visibility act, deferred to PublishComponent. copyToCluster is false for spawns (FinalizeSpawns handles cluster copy for those).
                bool copyToCluster = (compRevInfo.Operations & ComponentInfo.OperationType.Created) == 0;
                PrepareClusterVersionedSlot(pk, commitMeta, compRevInfo, readCompChunkId, info.ComponentTable, info.ComponentTypeId, copyToCluster,
                    out clusterCopyPending, out clusterChunkId, out slotIndex, out compSlot);
            }

            // Periodic flush: bound dirty counter inflation for large transactions
            if (_batchIndexActive && (++_batchEntityCount & 0x3FF) == 0)
            {
                for (int i = 0; i < _batchIndexAccessors.Length; i++)
                {
                    _batchIndexAccessors[i].CommitChanges();
                }
                if (info.ComponentTable.TailVSBS != null)
                {
                    _batchTailAccessor.CommitChanges();
                }

                // Flush warm accessors: exit+enter cycle performs a single CommitChanges per cache
                ChunkBasedSegment<PersistentStore>.ExitBatchMode();
                ChunkBasedSegment<PersistentStore>.EnterBatchMode();

                _changeSet.ReleaseExcessDirtyMarks();
            }

            // Record the publish descriptor — drained by PublishComponent after the WAL Append (AP-01).
            _publishEntries ??= new List<PublishEntry>(64);
            // Capture the committing element's physical coordinates from the already-resolved handle (DetectAndResolveConflict updates elementHandle by ref
            // to point at the final CurRevisionIndex), so PUBLISH reconstructs the handle with no locking walk (AP-03).
            _publishEntries.Add(new PublishEntry
            {
                Info = info,
                Pk = pk,
                FirstChunkId = firstChunkId,
                CurCompContentChunkId = compRevInfo.CurCompContentChunkId,
                CurRevisionIndex = compRevInfo.CurRevisionIndex,
                LastCommitRevisionIndex = lastCommitRevisionIndex,
                IsClusterEntity = isClusterEntity,
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
        header.LastCommitRevisionIndex = (short)Math.Max(e.LastCommitRevisionIndex, e.CurRevisionIndex);
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
        if (!_hasClusterCommitAccessors || _clusterCommitArchId != archId)
        {
            DisposeClusterCommitAccessors();
            _clusterCommitMapAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _clusterCommitContentAccessor = table.ComponentSegment.CreateChunkAccessor();
            _clusterCommitClusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            _clusterCommitArchId = archId;
            _hasClusterCommitAccessors = true;
        }

        // Read entity's cluster location from EntityMap (once)
        int recordSize = meta._entityRecordSize;
        byte* recordBuf = stackalloc byte[recordSize];

        long entityKey = EntityId.FromRaw(pk).EntityKey;
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

            // Find the index slot for this component
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
                    var idxAccessor = field.Index.Segment.CreateChunkAccessor(_changeSet);
                    try
                    {
                        if (newComp != null && oldComp != null)
                        {
                            // Update: move from old key to new key
                            field.Index.Move(oldComp + field.FieldOffset, newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                        }
                        else if (newComp != null)
                        {
                            // Insert (first commit after spawn)
                            field.Index.Add(newComp + field.FieldOffset, clusterLocation, ref idxAccessor);
                        }
                        else if (oldComp != null)
                        {
                            // Delete
                            field.Index.Remove(oldComp + field.FieldOffset, out _, ref idxAccessor);
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
                                reg.DeltaBuffer.TryAppend(entityKey, oldKey, newKey, TSN, flags, reg.ComponentTag);
                            }
                            else if (newComp != null)
                            {
                                // Add: isCreation flag
                                var newKey = KeyBytes8.FromPointer(newComp + field.FieldOffset, field.FieldSize);
                                byte flags = (byte)((fi & 0x3F) | 0x40); // isCreation
                                reg.DeltaBuffer.TryAppend(entityKey, default, newKey, TSN, flags, reg.ComponentTag);
                            }
                            else if (oldComp != null)
                            {
                                // Remove: isDeletion flag
                                var oldKey = KeyBytes8.FromPointer(oldComp + field.FieldOffset, field.FieldSize);
                                byte flags = (byte)((fi & 0x3F) | 0x80); // isDeletion
                                reg.DeltaBuffer.TryAppend(entityKey, oldKey, default, TSN, flags, reg.ComponentTag);
                            }
                        }
                    }
                    finally
                    {
                        idxAccessor.Dispose();
                    }
                }
                break; // Found the matching index slot
            }
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
        var es = _dbe._archetypeStates[archId];
        var clusterState = es.ClusterState;
        var table = e.Info.ComponentTable;

        // Re-establish lazy-cached accessors by archetype — reused across consecutive same-archetype entries in the publish drain.
        if (!_hasClusterCommitAccessors || _clusterCommitArchId != archId)
        {
            DisposeClusterCommitAccessors();
            _clusterCommitMapAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _clusterCommitContentAccessor = table.ComponentSegment.CreateChunkAccessor();
            _clusterCommitClusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
            _clusterCommitArchId = archId;
            _hasClusterCommitAccessors = true;
        }

        var layout = clusterState.Layout;
        byte* srcAddr = _clusterCommitContentAccessor.GetChunkAddress(e.CurCompContentChunkId);
        byte* clusterBase = _clusterCommitClusterAccessor.GetChunkAddress(e.ClusterChunkId, true);

        int compSize = layout.ComponentSize(e.CompSlot);
        byte* dstSlot = clusterBase + layout.ComponentOffset(e.CompSlot) + e.SlotIndex * compSize;
        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + table.ComponentOverhead, (uint)compSize);

        clusterState.SetDirty(e.ClusterChunkId, e.SlotIndex);
    }

    private void DisposeClusterCommitAccessors()
    {
        if (_hasClusterCommitAccessors)
        {
            _clusterCommitMapAccessor.Dispose();
            _clusterCommitContentAccessor.Dispose();
            _clusterCommitClusterAccessor.Dispose();
            _hasClusterCommitAccessors = false;
        }
    }

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

                // The content chunk is laid out [ComponentOverhead][value]; the logical value lives at offset ComponentOverhead — the same offset the read and write
                // paths apply (EntityAccessor.ReadEcsComponentDataRaw / WriteEcsComponentData). Log the VALUE, not the raw chunk prefix: an overhead-bearing (indexed)
                // component otherwise logs its overhead bytes plus a truncated value, silently dropping the trailing bytes from the WAL — invisible until crash recovery.
                var overhead = info.ComponentTable.ComponentOverhead;
                var payload = info.CompContentAccessor.GetChunkAsReadOnlySpan(cri.CurCompContentChunkId);
                batch.AddSlot(cacheEntry.Key, componentTypeId, payload.Slice(overhead, storageSize));
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
                var batch = new CommitBatchBuilder(_commitBatchArena, TSN, UowId);
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

                    // Hoist accessor creation for batch index maintenance
                    var indexedFieldInfos = info.ComponentTable.IndexedFieldInfos;
                    _batchIndexAccessors = new ChunkAccessor<PersistentStore>[indexedFieldInfos.Length];
                    for (int i = 0; i < indexedFieldInfos.Length; i++)
                    {
                        _batchIndexAccessors[i] = indexedFieldInfos[i].PersistentIndex.Segment.CreateChunkAccessor(_changeSet);
                    }
                    var tailVSBS = info.ComponentTable.TailVSBS;
                    _batchTailAccessor = tailVSBS != null ? tailVSBS.Segment.CreateChunkAccessor(_changeSet) : default;
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
                        if (tailVSBS != null)
                        {
                            _batchTailAccessor.Dispose();
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

    public bool Commit(ConcurrencyConflictHandler handler = null)
    {
        var ctx = UnitOfWorkContext.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout);
        return Commit(ref ctx, handler);
    }

}