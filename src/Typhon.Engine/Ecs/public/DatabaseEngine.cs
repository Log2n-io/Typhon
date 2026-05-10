using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using System.Linq.Expressions;
using Typhon.Engine.Profiler;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    public const string SchemaName = "Typhon.Schema.Field";

    public String64 Name;

    public int FieldId;
    public FieldType Type;
    public FieldType UnderlyingType;
    public uint IndexSPI;
    public bool IsStatic;
    public bool HasIndex;
    public bool IndexAllowMultiple;
    public int ArrayLength;
    public int OffsetInComponentStorage;
    public int SizeInComponentStorage;
    public bool IsArray => ArrayLength > 0;
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ComponentR1
{
    public const string SchemaName = "Typhon.Schema.Component";

    public String64 Name;
    public String64 POCOType;
    public int CompSize;
    public int CompOverhead;

    public int ComponentSPI;
    public int VersionSPI;
    public int DefaultIndexSPI;
    public int String64IndexSPI;
    public int TailIndexSPI;

    public ComponentCollection<FieldR1> Fields;

    public int SchemaRevision;
    public int FieldCount;
    public byte StorageMode;
}

/// <summary>
/// Persisted archetype schema. One entity per registered archetype.
/// Enables load-time validation: mismatch between persisted and runtime archetype definitions → hard error.
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ArchetypeR1
{
    public const string SchemaName = "Typhon.Schema.Archetype";

    /// <summary>Archetype CLR type name (e.g., "Building").</summary>
    public String64 Name;

    /// <summary>Globally unique archetype ID from [Archetype(Id = N)].</summary>
    public ushort ArchetypeId;

    /// <summary>Parent archetype ID (0xFFFF = no parent).</summary>
    public ushort ParentArchetypeId;

    /// <summary>Total component count (own + inherited).</summary>
    public byte ComponentCount;

    public byte _pad0, _pad1, _pad2;

    /// <summary>Schema revision from [Archetype(Id, Revision)].</summary>
    public int Revision;

    /// <summary>Component schema names in slot order, stored in VSBS.</summary>
    public ComponentCollection<String64> ComponentNames;

    /// <summary>Root page index of the EntityMap segment (0 = not persisted, rebuild from PK indexes).</summary>
    public int EntityMapSPI;

    /// <summary>Root page index of the ClusterSegment (0 = no cluster storage).</summary>
    public int ClusterSegmentSPI;

    /// <summary>Resume entity key counter on reopen (avoids scanning PK indexes).</summary>
    public long NextEntityKey;

    public const ushort NoParent = 0xFFFF;
}

/// <summary>
/// Describes the kind of schema change recorded in the audit trail.
/// </summary>
[PublicAPI]
public enum SchemaChangeKind
{
    Compatible,
    Migration,
    SystemUpgrade,
}

/// <summary>
/// Audit trail entry for schema changes. One entity is created for each component schema change (add/remove/widen fields, migration function execution, etc.).
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct SchemaHistoryR1
{
    public const string SchemaName = "Typhon.Schema.History";

    public long Timestamp;
    public String64 ComponentName;
    public int FromRevision;
    public int ToRevision;
    public int FieldsAdded;
    public int FieldsRemoved;
    public int FieldsTypeChanged;
    public int EntitiesMigrated;
    public int ElapsedMilliseconds;
    public SchemaChangeKind Kind;
}

/// <summary>
/// Configuration options for <see cref="DatabaseEngine"/>.
/// </summary>
[PublicAPI]
public class DatabaseEngineOptions
{
    /// <summary>
    /// Resource budget and limit configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contains settings for page cache size, transaction limits, WAL configuration,
    /// checkpoint behavior, and overall memory budget.
    /// </para>
    /// <para>
    /// Call <see cref="ResourceOptions.Validate"/> to verify configuration before engine creation.
    /// </para>
    /// </remarks>
    public ResourceOptions Resources { get; set; } = new();

    /// <summary>
    /// Lock acquisition timeout configuration for all engine subsystems.
    /// </summary>
    public TimeoutOptions Timeouts { get; set; } = new();

    /// <summary>
    /// Deferred cleanup subsystem configuration for MVCC revision management.
    /// </summary>
    public DeferredCleanupOptions DeferredCleanup { get; set; } = new();

    /// <summary>
    /// WAL writer configuration. Null disables WAL durability (in-memory only).
    /// </summary>
    public WalWriterOptions Wal { get; set; }

    /// <summary>
    /// Transient storage configuration (heap-backed pages for <see cref="StorageMode.Transient"/> components).
    /// </summary>
    public TransientOptions Transient { get; set; } = new();

    /// <summary>
    /// Background statistics rebuild configuration (HyperLogLog, MCV, Histogram).
    /// Null disables the background statistics worker (statistics can still be rebuilt manually).
    /// </summary>
    public StatisticsOptions Statistics { get; set; }

}

/// <summary>
/// The main database engine class providing transaction-based access to component data.
/// </summary>
/// <remarks>
/// <para>
/// DatabaseEngine registers itself under the <see cref="ResourceSubsystem.DataEngine"/> subsystem in the resource tree. ComponentTables are registered
/// as children of this engine.
/// </para>
/// </remarks>
[PublicAPI]
public partial class DatabaseEngine : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    private readonly DatabaseEngineOptions      _options;

    private readonly IWalFileIO                 _walFileIO;
    private readonly IResource                  _durabilityNode;
    private WalRecoveryResult                   _lastRecoveryResult;
    internal TransientOptions                   TransientOptions => _options.Transient;
    internal WalRecoveryResult                  LastRecoveryResult => _lastRecoveryResult;

    // ReSharper disable once ConvertToAutoProperty MUST KEEP _logger for SourceGen to generate the log properly
    internal ILogger<DatabaseEngine> Logger => _logger;

    internal IMemoryAllocator                   MemoryAllocator { get; }

    /// <summary>Shared WAL staging buffer pool — exposed to the profiler's gauge emitter. Null when WAL is disabled. Do not keep references across engine lifecycle boundaries.</summary>
    internal StagingBufferPool StagingBufferPool { get; private set; }

    // Bootstrap dictionary keys (engine layer)
    // ReSharper disable InconsistentNaming
    internal const string BK_SystemSchemaRevision   = "SystemSchemaRevision";
    internal const string BK_SysComponentR1         = "sys.ComponentR1";
    internal const string BK_SysSchemaHistory       = "sys.SchemaHistory";
    internal const string BK_NextFreeTSN            = "NextFreeTSN";
    internal const string BK_UowRegistrySPI         = "UowRegistrySPI";
    internal const string BK_CollectionFieldR1      = "collection.FieldR1";
    internal const string BK_UserSchemaVersion      = "UserSchemaVersion";
    internal const string BK_LastTickFenceLSN       = "LastTickFenceLSN";
    // ReSharper restore InconsistentNaming

    // Transaction counters for observability
    private long _transactionsCreated;
    private long _transactionsCommitted;
    private long _transactionsRolledBack;
    private long _transactionConflicts;

    // Commit duration tracking
    private long _commitLastUs;
    private long _commitSumUs;
    private long _commitCount;
    private long _commitMaxUs;

    private ComponentTable _componentsTable;
    private ComponentTable _schemaHistoryTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;

    /// <summary>Component schema names that underwent migration during this engine session. Used to invalidate stale EntityMaps.</summary>
    private HashSet<string> _migratedComponents;
    private ConcurrentDictionary<ushort, ComponentTable> _componentTableByWalTypeId;
    private long _lastTickFenceLSN;
    internal long LastTickFenceLSN => _lastTickFenceLSN;
    private Dictionary<string, (int ChunkId, ComponentR1 Comp)> _persistedComponents;
    private Dictionary<ushort, (int ChunkId, ArchetypeR1 Arch)> _persistedArchetypes;

    /// <summary>Per-engine archetype runtime state, indexed by ArchetypeId. Separates per-engine mutable data from shared schema metadata.</summary>
    internal ArchetypeEngineState[] _archetypeStates;
    private Dictionary<string, FieldR1[]> _persistedFieldsByComponent;
    private ConcurrentDictionary<int, ChunkBasedSegment<PersistentStore>> _componentCollectionSegmentByStride;
    private ConcurrentDictionary<Type, VariableSizedBufferSegmentBase<PersistentStore>> _componentCollectionVSBSByType;
    private MigrationRegistry _migrationRegistry;

    // ══════════════════════════════════════════════════════════════════════════════
    // Spatial grid (issue #229 — Phase 1+2). One global grid shared by every spatial archetype.
    // Configured once via ConfigureSpatialGrid before InitializeArchetypes.
    // ══════════════════════════════════════════════════════════════════════════════
    private SpatialGrid _spatialGrid;
    private SpatialGridConfig? _pendingGridConfig;

    /// <summary>
    /// Sets the spatial grid configuration for this engine. Must be called before <see cref="InitializeArchetypes"/>. Only required when at least one
    /// cluster-eligible archetype has a spatial component — non-spatial engines never need this call.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if called after <see cref="InitializeArchetypes"/>.</exception>
    [PublicAPI]
    public void ConfigureSpatialGrid(SpatialGridConfig config)
    {
        if (_spatialGrid != null)
        {
            throw new InvalidOperationException("ConfigureSpatialGrid must be called before InitializeArchetypes. The spatial grid has already been constructed.");
        }
        if (_pendingGridConfig.HasValue)
        {
            throw new InvalidOperationException("ConfigureSpatialGrid was already called. Configuration cannot be changed after the first call.");
        }
        _pendingGridConfig = config;
    }

    /// <summary>
    /// Engine-wide spatial grid, or <c>null</c> if no grid was configured. Set by
    /// <see cref="InitializeArchetypes"/> from the pending config (if any).
    /// </summary>
    internal SpatialGrid SpatialGrid => _spatialGrid;

    /// <summary>
    /// Mark a single entity slot as dirty in the cluster dirty bitmap. Call from game systems that use the direct
    /// cluster iteration path (<see cref="ClusterRef{TArch}.GetSpan{T}"/>) and need migration detection or WAL
    /// tracking. The <paramref name="chunkId"/> comes from <see cref="ClusterRef{TArch}.ChunkId"/>.
    /// Thread-safe (uses <see cref="System.Threading.Interlocked.Or"/> internally via <c>SetDirty</c>).
    /// </summary>
    [PublicAPI]
    public void MarkClusterSlotDirty(int archetypeId, int chunkId, int slotIndex)
    {
        if (archetypeId >= 0 && archetypeId < _archetypeStates.Length)
        {
            _archetypeStates[archetypeId]?.ClusterState?.SetDirty(chunkId, slotIndex);
        }
    }

    /// <summary>Raised during schema migration to report progress to subscribers.</summary>
    [PublicAPI]
    public event EventHandler<MigrationProgressEventArgs> OnMigrationProgress;

    internal void RaiseMigrationProgress(MigrationProgressEventArgs args) => OnMigrationProgress?.Invoke(this, args);

    /// <summary>Exposes persisted component metadata for operational tooling (Inspect, tsh commands).</summary>
    internal IReadOnlyDictionary<string, (int ChunkId, ComponentR1 Comp)> PersistedComponents => _persistedComponents;

    /// <summary>Exposes persisted field definitions per component for operational tooling.</summary>
    internal IReadOnlyDictionary<string, FieldR1[]> PersistedFieldsByComponent => _persistedFieldsByComponent;

    /// <summary>Exposes the migration registry for dry-run validation.</summary>
    internal MigrationRegistry MigrationRegistry => _migrationRegistry;

    public DatabaseDefinitions DBD { get; }
    public ManagedPagedMMF MMF { get; }
    public EpochManager EpochManager { get; private set; }
    public DeadlineWatchdog Watchdog { get; }

    internal TransactionChain TransactionChain { get; }
    internal DeferredCleanupManager DeferredCleanupManager { get; }

    /// <summary>Engine-level MVCC exception dictionary for ECS EnabledBits.</summary>
    internal EnabledBitsOverrides EnabledBitsOverrides { get; private set; }

    // ── ECS Deferred Cleanup ──

    internal struct EcsCleanupEntry
    {
        public EntityId Id;
        public ArchetypeMetadata Meta;
        public long DiedTSN;
    }

    private readonly List<EcsCleanupEntry> _ecsCleanupQueue = [];
    private readonly Lock _ecsCleanupLock = new();
    private readonly ILogger<DatabaseEngine> _logger;

    /// <summary>Enqueue an ECS entity for deferred cleanup (LinearHash removal + chunk freeing).</summary>
    internal void EnqueueEcsCleanup(EntityId id, ArchetypeMetadata meta, long diedTSN)
    {
        lock (_ecsCleanupLock)
        {
            _ecsCleanupQueue.Add(new EcsCleanupEntry { Id = id, Meta = meta, DiedTSN = diedTSN });
        }
    }

    /// <summary>
    /// Process ECS deferred cleanups: remove LinearHash entries and free component chunks for entities whose DiedTSN is below minTSN
    /// (no active transaction can see them).
    /// </summary>
    internal unsafe int ProcessEcsCleanups(long minTSN)
    {
        List<EcsCleanupEntry> toProcess;
        lock (_ecsCleanupLock)
        {
            toProcess = _ecsCleanupQueue.FindAll(e => e.DiedTSN < minTSN);
            _ecsCleanupQueue.RemoveAll(e => e.DiedTSN < minTSN);
        }

        if (toProcess.Count == 0)
        {
            return 0;
        }

        using var guard = EpochGuard.Enter(EpochManager);

        // Hoist stackalloc out of loop — max record size is 78B (14B header + 16 components × 4B)
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        foreach (var entry in toProcess)
        {
            var meta = entry.Meta;
            var engineState = _archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null)
            {
                continue;
            }
            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            bool found = engineState.EntityMap.TryGet(entry.Id.EntityKey, readBuf, ref accessor);

            if (found)
            {
                // Free component chunks
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    int chunkId = EntityRecordAccessor.GetLocation(readBuf, slot);
                    if (chunkId != 0 && engineState.SlotToComponentTable != null)
                    {
                        engineState.SlotToComponentTable[slot].ComponentSegment.FreeChunk(chunkId);
                    }
                }

                // Remove from LinearHash
                engineState.EntityMap.Remove(entry.Id.EntityKey, ref accessor, null);
            }

            accessor.Dispose();
        }

        // Also prune EnabledBits overrides
        EnabledBitsOverrides.Prune(minTSN);

        return toProcess.Count;
    }

    /// <summary>
    /// Process all pending deferred cleanups. Intended for test/diagnostic use.
    /// Creates its own ChangeSet and processes ALL queued entries regardless of blockingTSN.
    /// </summary>
    /// <param name="nextMinTSN">Cutoff TSN for revision cleanup. 0 = use TransactionChain.NextFreeId + 1 (clean everything eligible).</param>
    /// <returns>Number of entities cleaned up.</returns>
    internal int FlushDeferredCleanups(long nextMinTSN = 0)
    {
        if (nextMinTSN == 0)
        {
            nextMinTSN = TransactionChain.NextFreeId + 1;
        }

        var changeSet = new ChangeSet(MMF);
        var result = DeferredCleanupManager.ProcessDeferredCleanups(long.MaxValue, nextMinTSN, this, changeSet);
        changeSet.SaveChanges();
        return result;
    }

    internal UowRegistry UowRegistry { get; private set; }

    /// <summary>
    /// Optional WAL manager for durability. Null when WAL is not configured.
    /// </summary>
    internal WalManager WalManager { get; private set; }

    /// <summary>
    /// Optional checkpoint manager. Null when WAL is not configured. Periodically flushes dirty data pages
    /// and advances CheckpointLSN to enable WAL segment recycling.
    /// </summary>
    internal CheckpointManager CheckpointManager { get; private set; }
    internal StatisticsWorker StatisticsWorker { get; private set; }

    /// <summary>
    /// Creates a new Unit of Work — the durability boundary for user operations. All transactions must be created through a UoW.
    /// </summary>
    /// <param name="durabilityMode">Controls when WAL records become crash-safe. Default is <see cref="DurabilityMode.Deferred"/>.</param>
    /// <param name="timeout">Lifetime timeout for this UoW. Default uses <see cref="TimeoutOptions.DefaultUowTimeout"/>.</param>
    /// <returns>A new <see cref="UnitOfWork"/> in <see cref="UnitOfWorkState.Pending"/> state.</returns>
    /// <exception cref="ResourceExhaustedException">All UoW registry slots are in use and the deadline expired.</exception>
    [return: TransfersOwnership]
    public UnitOfWork CreateUnitOfWork(DurabilityMode durabilityMode = DurabilityMode.Deferred, TimeSpan timeout = default)
    {
        LogUowLifecycle("CreateUnitOfWork enter");
        var effectiveTimeout = timeout == TimeSpan.Zero ? TimeoutOptions.Current.DefaultUowTimeout : timeout;
        var wc = WaitContext.FromTimeout(effectiveTimeout);

        // For Deferred/GroupCommit: create the ChangeSet early so AllocateUowId can track
        // the registry page mutation in it (avoiding a synchronous SaveChanges).
        var changeSet = durabilityMode != DurabilityMode.Immediate ? MMF.CreateChangeSet() : null;
        LogUowLifecycle("ChangeSet created");

        // Back-pressure: if registry is full, wait for a slot to be freed.
        // The admission check is a fast-path optimization — AllocateUowId's CAS provides the real atomicity (TOCTOU by design).
        var uowId = UowRegistry.AllocateUowId(ref wc, changeSet);
        LogUowIdAllocated(uowId);

        return new UnitOfWork(this, durabilityMode, uowId, effectiveTimeout, changeSet);
    }

    /// <summary>Records that a transaction was created (for observability counters).</summary>
    internal void RecordTransactionCreated() => Interlocked.Increment(ref _transactionsCreated);

    /// <summary>
    /// Triggers an immediate checkpoint cycle. Flushes all dirty data pages, advances CheckpointLSN, and recycles WAL segments.
    /// No-op if WAL/checkpoint is not configured.
    /// </summary>
    public void ForceCheckpoint() => CheckpointManager?.ForceCheckpoint();

    public DatabaseEngine(IResourceRegistry resourceRegistry, EpochManager epochManager, DeadlineWatchdog watchdog, ManagedPagedMMF mmf, 
        IMemoryAllocator memoryAllocator, DatabaseEngineOptions options, ILogger<DatabaseEngine> log, IWalFileIO walFileIO = null, string name = null) 
        : base(name ?? $"DatabaseEngine_{Guid.NewGuid():N}", ResourceType.Engine, resourceRegistry.DataEngine)
    {
        // Engine initialization
        MMF = mmf;
        EpochManager = epochManager;
        Watchdog = watchdog;
        _logger = log;
        _options = options;
        MemoryAllocator = memoryAllocator;
        _walFileIO = walFileIO;
        _durabilityNode = resourceRegistry.Durability;
        TimeoutOptions.Current = _options.Timeouts;
        _componentCollectionSegmentByStride = new ConcurrentDictionary<int, ChunkBasedSegment<PersistentStore>>();
        _componentCollectionVSBSByType = new ConcurrentDictionary<Type, VariableSizedBufferSegmentBase<PersistentStore>>();
        TransactionChain = new TransactionChain(_options.Resources.MaxActiveTransactions, this);
        DeferredCleanupManager = new DeferredCleanupManager(_options.DeferredCleanup, Logger);
        EnabledBitsOverrides = new EnabledBitsOverrides(Logger);

        DBD = new DatabaseDefinitions();
        ConstructComponentStore();
        InitializeUowRegistry();

        if (MMF.IsDatabaseFileCreating)
        {
            CreateSystemSchemaR1();
        }
        else
        {
            LoadSystemSchemaR1();
        }

        InitializeWalManager();
        InitializeCheckpointManager();
        InitializeStatisticsWorker();
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            // Statistics worker must stop before checkpoint (it holds epoch guards during scans)
            StatisticsWorker?.Dispose();
            StatisticsWorker = null;

            Logger?.LogInformation("Engine disposing: CheckpointManager");
            // Checkpoint must dispose first: runs final cycle, writes pages + advances LSN before WAL shuts down
            CheckpointManager?.Dispose();
            CheckpointManager = null;

            // Dispose staging pool after checkpoint manager (checkpoint may use it during final cycle)
            StagingBufferPool?.Dispose();
            StagingBufferPool = null;

            Logger?.LogInformation("Engine disposing: PersistArchetypeState");
            // Persist EntityMap SPIs and NextEntityKey counters so reopen can load EntityMaps directly
            PersistArchetypeState();

            Logger?.LogInformation("Engine disposing: PersistEngineState");
            // Persist final TSN counter and flush all dirty pages to disk. This ensures:
            // 1. TSN counter survives restart (MVCC visibility)
            // 2. All committed transaction data is on disk even without WAL/checkpoint
            PersistEngineState();

            Logger?.LogInformation("Engine disposing: WalManager");
            WalManager?.Dispose();
            WalManager = null;
            Logger?.LogInformation("Engine disposing: TransactionChain + cleanup");
            TransactionChain.Dispose();
            UowRegistry?.Dispose();
            MMF.Dispose();
        }
        base.Dispose(disposing);
        IsDisposed = true;
    }
    
    private void InitializeWalManager()
    {
        var walOptions = _options.Wal;
        if (walOptions == null || _walFileIO == null)
        {
            return;
        }

        var commitBufferCapacity = _options.Resources.WalRingBufferSizeBytes / 2;
        WalManager = new WalManager(walOptions, MemoryAllocator, _walFileIO, _durabilityNode, commitBufferCapacity);

        // Determine continuation point from recovery or fresh start
        var lastLSN = _lastRecoveryResult.LastValidLSN;
        var lastSegmentId = 0L; // Segment continuity is handled by WalSegmentManager scanning existing files
        WalManager.Initialize(lastSegmentId, lastLSN > 0 ? lastLSN + 1 : 1);
        WalManager.Logger = Logger;
        WalManager.Start();
    }

    private void InitializeCheckpointManager()
    {
        if (WalManager == null)
        {
            return;
        }

        // Read initial CheckpointLSN from file header
        long initialCheckpointLsn;
        using (EpochGuard.Enter(EpochManager))
        {
            initialCheckpointLsn = MMF.Bootstrap.GetLong(ManagedPagedMMF.BK_CheckpointLSN);
        }

        StagingBufferPool = new StagingBufferPool(MemoryAllocator, _durabilityNode);

        // Enable FPI capture — creates FpiBitmap internally using cache page count
        MMF.EnableFpiCapture(WalManager, _options.Wal?.EnableFpiCompression ?? false);

        // Activate CRC verification mode — recovery is complete, so OnLoad checks are now safe
        MMF.SetPageChecksumVerification(_options.Resources.PageChecksumVerification);

        CheckpointManager = new CheckpointManager(MMF, UowRegistry, WalManager, _options.Resources, EpochManager, StagingBufferPool, _durabilityNode,
            initialCheckpointLsn, () => _lastTickFenceLSN);
        CheckpointManager.Start();

        // Wire demand-driven flush: when page cache backpressure fires, immediately wake
        // the checkpoint thread instead of waiting for the 30s timer interval.
        MMF.OnBackpressure = () => CheckpointManager?.ForceCheckpoint();
    }

    private void InitializeStatisticsWorker()
    {
        var opts = _options.Statistics;
        if (opts == null || !opts.Enabled)
        {
            return;
        }

        StatisticsWorker = new StatisticsWorker(this, opts, EpochManager, this);
        StatisticsWorker.Start();
    }

    /// <summary>
    /// Returns all registered ComponentTables. Used by <see cref="StatisticsWorker"/> to iterate tables, and by external tooling (e.g., the Workbench
    /// Schema Inspector) to enumerate the schema. <see cref="ConcurrentDictionary{TKey,TValue}.Values"/> returns a stable snapshot, so concurrent
    /// registration is safe.
    /// </summary>
    public IEnumerable<ComponentTable> GetAllComponentTables() => _componentTableByType.Values;

    /// <summary>
    /// Current entity count for the given archetype in this engine. Returns 0 if the archetype has no state in this engine (not registered or not yet
    /// initialized). Used by external tooling (Workbench Schema Inspector) to populate the Archetype panel — intentionally a scalar accessor so the
    /// internal <see cref="ArchetypeEngineState"/> type does not need to leak into the public surface.
    /// </summary>
    public long GetArchetypeEntityCount(ushort archetypeId)
    {
        var states = _archetypeStates;
        if (states == null || archetypeId >= states.Length)
        {
            return 0;
        }

        var state = states[archetypeId];
        return state?.EntityMap.EntryCount ?? 0;
    }

    /// <summary>
    /// Number of active cluster chunks for the given archetype in this engine. Returns 0 for legacy archetypes (non-cluster storage) or if the archetype has
    /// no cluster state yet. Paired with <see cref="GetArchetypeEntityCount"/> the caller can derive occupancy for cluster archetypes:
    /// <c>entityCount / (chunkCount * ArchetypeClusterInfo.ClusterSize)</c>.
    /// </summary>
    public int GetArchetypeClusterChunkCount(ushort archetypeId)
    {
        var states = _archetypeStates;
        if (states == null || archetypeId >= states.Length)
        {
            return 0;
        }

        var state = states[archetypeId];
        return state?.ClusterState?.ActiveClusterCount ?? 0;
    }

    /// <summary>
    /// Sum of pinned-heap bytes currently held by every live <see cref="TransientStore"/> in this engine. Transient storage is distributed across several
    /// per-table and per-cluster stores (each <see cref="ComponentTable"/> with <see cref="StorageMode.Transient"/> owns three stores — component +
    /// default-index + string64-index — and each cluster-eligible <see cref="ArchetypeClusterState"/> owns one cluster store). This accessor walks every
    /// registered ComponentTable and every archetype's cluster state, reads the live <c>PageCount</c> off each segment's own store copy, and returns the total
    /// in bytes.
    /// </summary>
    /// <remarks>
    /// Consumed by <see cref="Profiler.GaugeSnapshotEmitter"/> once per scheduler tick; cost is O(ComponentTables + Archetypes).
    /// Reads are non-synchronized — the returned value is best-effort and can lag by a tick's worth of allocations. That's
    /// acceptable for an observability gauge but unsafe to use for allocation decisions.
    /// </remarks>
    internal long GetTransientBytesTotal()
    {
        long pageCount = 0;

        if (_componentTableByType != null)
        {
            foreach (var table in _componentTableByType.Values)
            {
                if (table.StorageMode != StorageMode.Transient)
                {
                    continue;
                }
                if (table.TransientComponentSegment != null)
                {
                    pageCount += table.TransientComponentSegment.Store.PageCount;
                }
                if (table.TransientDefaultIndexSegment != null)
                {
                    pageCount += table.TransientDefaultIndexSegment.Store.PageCount;
                }
                if (table.TransientString64IndexSegment != null)
                {
                    pageCount += table.TransientString64IndexSegment.Store.PageCount;
                }
            }
        }

        if (_archetypeStates != null)
        {
            for (int i = 0; i < _archetypeStates.Length; i++)
            {
                var state = _archetypeStates[i];
                var clusterState = state?.ClusterState;
                if (clusterState?.TransientSegment != null)
                {
                    pageCount += clusterState.TransientSegment.Store.PageCount;
                }
            }
        }

        return pageCount * PagedMMF.PageSize;
    }

    /// <summary>
    /// Serializes dirty SingleVersion component data to WAL at tick boundary. One TickFence chunk per SV ComponentTable.
    /// Called by the game loop at each tick boundary.
    /// </summary>
    /// <param name="tickNumber">Monotonic tick identifier.</param>
    /// <param name="changeSet">Caller-supplied ChangeSet for shared dirty-page tracking across the whole tick fence (typically the per-tick UoW's
    /// shared ChangeSet — see <see cref="UnitOfWork.ChangeSet"/>). When null, a one-shot local ChangeSet is created and committed by this method itself
    /// (test/admin path: tests that invoke <c>WriteTickFence</c> directly without a UoW retain their original behaviour).</param>
    /// <returns>Highest LSN written, or 0 if nothing was serialized.</returns>
    public long WriteTickFence(long tickNumber, ChangeSet changeSet = null)
    {
        // When the caller doesn't supply a ChangeSet (e.g., tests that invoke WriteTickFence outside a UoW), we own the lifecycle: create a fresh
        // ChangeSet, thread it through the per-table tick-fence callees, and commit it ourselves at the end. Production callers (TyphonRuntime)
        // pass _currentUow.ChangeSet so dirty-page tracking is consolidated with everything else this tick — UoW.Flush handles the actual writeback.
        bool ownChangeSet = changeSet == null;
        if (ownChangeSet)
        {
            changeSet = MMF.CreateChangeSet();
        }

        long highestLSN;
        try
        {
            highestLSN = WriteTickFenceCore(tickNumber, changeSet);
        }
        finally
        {
            if (ownChangeSet)
            {
                changeSet.SaveChanges();
                changeSet.ReleaseExcessDirtyMarks();
            }
        }

        return highestLSN;
    }

    private long WriteTickFenceCore(long tickNumber, ChangeSet changeSet)
    {
        long highestLSN = 0;
        using var epochGuard = EpochGuard.Enter(EpochManager);

        foreach (var table in _componentTableByType.Values)
        {
            if (table.StorageMode == StorageMode.Versioned || table.DirtyBitmap == null)
            {
                continue;
            }

            if (!table.DirtyBitmap.HasDirty)
            {
                table.PreviousTickDirtyBitmap = null;
                table.PreviousTickHadDirtyEntities = false;
                continue;
            }

            // Snapshot DirtyBitmap — atomic swap, clears bitmap for next tick
            var dirtyBits = table.DirtyBitmap.Snapshot();

            // The runtime iterates set bits at dispatch time (same pattern as ProcessSpatialEntries).
            table.PreviousTickDirtyBitmap = dirtyBits;
            table.PreviousTickHadDirtyEntities = true;

            // WAL serialization: SV only — Transient has no WAL persistence, skip straight to shadow processing.
            if (table.StorageMode == StorageMode.SingleVersion && WalManager != null)
            {
                // Count dirty entries via PopCount (needed for WAL chunk sizing)
                int entryCount = 0;
                for (int i = 0; i < dirtyBits.Length; i++)
                {
                    entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
                }

                if (entryCount > 0)
                {
                    int stride = table.ComponentStorageSize;
                    int overhead = table.ComponentOverhead;
                    int entrySize = 4 + stride; // ChunkId(4B) + ComponentData(stride)

                    // ChunkSize is ushort (max 65535). Split into multiple chunks if needed.
                    int maxEntriesPerChunk = (ushort.MaxValue - WalChunkHeader.SizeInBytes - TickFenceHeader.SizeInBytes - WalChunkFooter.SizeInBytes) / entrySize;

                    var accessor = table.ComponentSegment.CreateChunkAccessor();
                    try
                    {
                        int entriesRemaining = entryCount;
                        int wordIndex = 0;
                        long currentWord = wordIndex < dirtyBits.Length ? dirtyBits[wordIndex] : 0;

                        while (entriesRemaining > 0)
                        {
                            int batchCount = Math.Min(entriesRemaining, maxEntriesPerChunk);
                            int bodySize = TickFenceHeader.SizeInBytes + batchCount * entrySize;
                            int chunkSize = WalChunkHeader.SizeInBytes + bodySize + WalChunkFooter.SizeInBytes;

                            var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
                            var claim = WalManager.CommitBuffer.TryClaim(chunkSize, 1, ref wc);
                            if (!claim.IsValid)
                            {
                                break; // back-pressure — skip remaining entries for this table
                            }

                            try
                            {
                                int offset = 0;

                                // WalChunkHeader
                                var chunkHeader = new WalChunkHeader
                                {
                                    ChunkType = (ushort)WalChunkType.TickFence,
                                    ChunkSize = (ushort)chunkSize,
                                    PrevCRC = 0,
                                };
                                MemoryMarshal.Write(claim.DataSpan[offset..], in chunkHeader);
                                offset += WalChunkHeader.SizeInBytes;

                                // TickFenceHeader
                                var tfHeader = new TickFenceHeader
                                {
                                    TickNumber = tickNumber,
                                    LSN = claim.FirstLSN,
                                    ComponentTypeId = table.WalTypeId,
                                    EntryCount = (ushort)batchCount,
                                    PayloadStride = (ushort)stride,
                                    Reserved = 0,
                                };
                                MemoryMarshal.Write(claim.DataSpan[offset..], in tfHeader);
                                offset += TickFenceHeader.SizeInBytes;

                                // Write entries by iterating dirty bits
                                int written = 0;
                                while (written < batchCount)
                                {
                                    // Advance to next word if current is exhausted
                                    while (currentWord == 0 && wordIndex < dirtyBits.Length - 1)
                                    {
                                        wordIndex++;
                                        currentWord = dirtyBits[wordIndex];
                                    }

                                    if (currentWord == 0)
                                    {
                                        break;
                                    }

                                    int bit = BitOperations.TrailingZeroCount((ulong)currentWord);
                                    int chunkId = wordIndex * 64 + bit;
                                    currentWord &= currentWord - 1; // clear lowest set bit

                                    // Write ChunkId (4B)
                                    MemoryMarshal.Write(claim.DataSpan[offset..], in chunkId);
                                    offset += 4;

                                    // Write component data (stride bytes)
                                    var src = accessor.GetChunkAsReadOnlySpan(chunkId);
                                    src.Slice(overhead, stride).CopyTo(claim.DataSpan[offset..]);
                                    offset += stride;

                                    written++;
                                }

                                // WalChunkFooter
                                var footer = new WalChunkFooter { CRC = 0 };
                                MemoryMarshal.Write(claim.DataSpan[offset..], in footer);

                                WalManager.CommitBuffer.Publish(ref claim);
                                if (claim.FirstLSN > highestLSN)
                                {
                                    highestLSN = claim.FirstLSN;
                                }
                            }
                            catch
                            {
                                WalManager.CommitBuffer.AbandonClaim(ref claim);
                                throw;
                            }

                            entriesRemaining -= batchCount;
                        }
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
            }

            // Deferred index maintenance: process shadowed old field values for non-Versioned indexed fields.
            // Must run even without WAL (indexes are in-memory structures independent of WAL).
            if (table.HasShadowableIndexes)
            {
                ProcessShadowEntries(table, changeSet);
            }

            // Spatial index maintenance: iterate dirty entities, update R-Tree positions.
            // Uses dirtyBits snapshot (still in scope from DirtyBitmap.Snapshot above).
            // Spatial doesn't need shadows — back-pointers provide O(1) leaf lookup, and the containment check
            // uses the fat AABB stored in the tree node. Only the final position matters.
            if (table.SpatialIndex != null && table.SpatialIndex.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                ProcessSpatialEntries(table, dirtyBits, changeSet);
            }

            // Archive dirty bitmap into ring buffer for interest management delta queries
            table.SpatialIndex?.InterestSystem?.DirtyRing.Archive(tickNumber, dirtyBits, dirtyBits.Length);
        }

        // Cluster tick fence: serialize dirty cluster-backed entity data to WAL
        WriteClusterTickFence(tickNumber, ref highestLSN, changeSet);

        if (highestLSN > 0)
        {
            Interlocked.Exchange(ref _lastTickFenceLSN, highestLSN);
        }

        return highestLSN;
    }

    /// <summary>
    /// Serializes dirty cluster entity data to WAL for all cluster-eligible archetypes.
    /// Called from <see cref="WriteTickFence"/> after per-ComponentTable processing.
    /// </summary>
    /// <summary>Create a fresh CBS&lt;TransientStore&gt; for cluster Transient component storage.</summary>
    private void CreateTransientClusterSegment(int stride, out TransientStore? store, out ChunkBasedSegment<TransientStore> segment)
    {
        store = new TransientStore(TransientOptions, MemoryAllocator, EpochManager, this);
        var tsValue = store.Value;
        segment = new ChunkBasedSegment<TransientStore>(EpochManager, tsValue, stride);
        Span<int> tsPages = stackalloc int[4];
        tsValue.AllocatePages(ref tsPages, 0, null);
        segment.Create(PageBlockType.None, tsPages, false);
    }

    /// <summary>
    /// After reopening a mixed archetype with Transient components, allocate matching chunks in the fresh
    /// TransientSegment so chunk IDs stay synchronized with the persisted PersistentStore segment.
    /// </summary>
    /// <remarks>
    /// <para>Relies on the TransientSegment being freshly created (no prior allocations/frees), which guarantees
    /// sequential chunk ID assignment (1, 2, 3, ...). This is always true because TransientStore data doesn't
    /// survive restart — the segment is created fresh in every reopen path.</para>
    /// </remarks>
    private static void SyncTransientSegmentToActive(ArchetypeClusterState clusterState)
    {
        if (clusterState.TransientSegment == null)
        {
            return;
        }

        // Find max chunk ID among active clusters
        int maxChunkId = 0;
        for (int i = 0; i < clusterState.ActiveClusterCount; i++)
        {
            if (clusterState.ActiveClusterIds[i] > maxChunkId)
            {
                maxChunkId = clusterState.ActiveClusterIds[i];
            }
        }

        // Allocate chunks in TransientStore sequentially up to maxChunkId so IDs match.
        // TransientStore is always fresh — sequential allocation produces IDs 1..maxChunkId.
        for (int id = 1; id <= maxChunkId; id++)
        {
            int allocatedId = clusterState.TransientSegment.AllocateChunk(true);
            Debug.Assert(allocatedId == id, $"TransientSegment sync: expected chunk ID {id}, got {allocatedId}");
        }
    }

    private unsafe void WriteClusterTickFence(long tickNumber, ref long highestLSN, ChangeSet changeSet)
    {
        // Issue #233: drain all deferred wake requests collected during parallel system execution. Must run once BEFORE the per-archetype loop so each
        // archetype's DormancySweep (below) sees up-to-date WakePending states and skips those clusters instead of re-sleeping them.
        DormancyReporter.DrainAll(_archetypeStates);

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
            {
                continue;
            }

            var engineState = _archetypeStates[meta.ArchetypeId];
            var clusterState = engineState?.ClusterState;
            if (clusterState == null)
            {
                continue;
            }

            // Pure-Transient archetypes have no PersistentStore segment — nothing to persist to WAL
            if (clusterState.ClusterSegment == null)
            {
                // Still snapshot dirty bitmap for change-filtered dispatch
                if (clusterState.ClusterDirtyBitmap.HasDirty)
                {
                    var transientDirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
                    clusterState.PreviousTickDirtySnapshot = transientDirtyBits;
                    // Propagate dirty status to per-ComponentTable flags
                    for (int slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
                    {
                        engineState.SlotToComponentTable[slot].PreviousTickHadDirtyEntities = true;
                        engineState.SlotToComponentTable[slot].PreviousTickDirtyBitmap ??= Array.Empty<long>();
                    }
                    // Issue #233: dormancy sweep with the actual dirty snapshot
                    clusterState.DormancySweep(transientDirtyBits, tickNumber);
                }
                else
                {
                    clusterState.PreviousTickDirtySnapshot = null;
                    // Issue #233: all clusters clean — sweep with empty bitmap so counters increment
                    clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
                }
                continue;
            }

            if (!clusterState.ClusterDirtyBitmap.HasDirty)
            {
                clusterState.PreviousTickDirtySnapshot = null;
                // Issue #233: all clusters clean this tick — sweep with empty bitmap so sleep counters increment.
                // This is the most common path for dormancy (idle clusters accumulating clean ticks toward the threshold).
                clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
                continue;
            }

            // Snapshot atomically — clears bitmap for next tick
            var dirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();

            // Mask dirty bits with live occupancy to skip destroyed entities whose dirty bit remained set.
            // A destroyed entity's slot is cleared in the cluster (occupancy bit = 0), but its dirty bit
            // stays set until snapshot. Without masking we'd serialize stale/zeroed data for dead slots.
            var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                int entryCount = 0;
                for (int i = 0; i < dirtyBits.Length; i++)
                {
                    if (dirtyBits[i] == 0)
                    {
                        continue;
                    }

                    byte* occBase = accessor.GetChunkAddress(i);
                    ulong occupancy = *(ulong*)occBase;
                    dirtyBits[i] &= (long)occupancy;
                    entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
                }

                // Process per-archetype shadow entries for B+Tree index maintenance.
                // MUST run regardless of entryCount — destroyed entities with pending shadows need index cleanup.
                if (clusterState.IndexSlots != null)
                {
                    ProcessClusterShadowEntries(clusterState, engineState, changeSet);
                    RecomputeClusterZoneMaps(clusterState, dirtyBits);
                }

                // Process per-archetype spatial entries for cluster archetypes with spatial fields.
                // Spatial doesn't need shadows — back-pointers provide O(1) leaf lookup. Only Dynamic mode is updated.
                if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
                {
                    DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, dirtyBits, ref accessor);

                    // Issue #229 Phase 3: drain the per-archetype migration queue populated by detection above.
                    // ExecuteMigrations runs BEFORE the cluster tick fence WAL publish so the WAL chunks reflect post-migration cluster state (source slots
                    // cleared via dirty-bit clear, destination slots serialized via dirty-bit set).
                    if (clusterState.PendingMigrationCount > 0)
                    {
                        ExecuteMigrations(clusterState, engineState, meta.ArchetypeId, ref dirtyBits, changeSet);
                    }
                    else
                    {
                        clusterState.LastTickMigrationCount = 0;
                        clusterState.LastTickMigrationExecuteMs = 0d;
                    }

                    // Issue #230 Phase 2: tighten per-cluster AABBs for clusters that had entity writes this tick.
                    // Must run AFTER ExecuteMigrations so post-migration destination clusters get their union-loose
                    // AABB replaced with the true tight bounds in the same pass. Source clusters that emptied out
                    // during migration are already removed from the per-cell index (FinaliseEmptyClusterCellState)
                    // so their dirty bits either get masked by occupancy or fall through the indexSlot<0 guard.
                    // Passing _spatialGrid activates the Phase 3 "Max Cluster AABB Extent" outlier guard for clusters
                    // whose hysteresis-absorbed drift has inflated their AABB beyond cellSize × 1.2.
                    clusterState.RecomputeDirtyClusterAabbs(dirtyBits, ref accessor, _spatialGrid);
                }

                // Issue #233: advance dormancy sleep counters and transition idle clusters to Sleeping.
                // Runs AFTER wake requests are processed (line above drains DormancyReporter globally) and AFTER migrations/AABBs so the dirtyBits snapshot
                // is final.
                clusterState.DormancySweep(dirtyBits, tickNumber);

                // Archive dirty bitmap into per-archetype DirtyBitmapRing for spatial interest management.
                // Issue #230 Phase 3: the ring was relocated from ClusterSpatialSlot.DirtyRing to ArchetypeClusterState.ClusterDirtyRing.
                clusterState.ClusterDirtyRing?.Archive(tickNumber, dirtyBits, dirtyBits.Length);

                if (entryCount == 0)
                {
                    continue;
                }

                // Store dirty snapshot for change-filtered runtime dispatch.
                // BuildFilteredClusterEntities reads this to find dirty cluster entities.
                clusterState.PreviousTickDirtySnapshot = dirtyBits;

                // Propagate dirty status to ComponentTables for change-filtered runtime dispatch.
                // The per-ComponentTable loop in WriteTickFence sets PreviousTickHadDirtyEntities = false for tables without dirty bits. Cluster writes
                // bypass table.DirtyBitmap, so we must re-set PreviousTickHadDirtyEntities here after the per-ComponentTable loop runs.
                for (int slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
                {
                    var table = engineState.SlotToComponentTable[slot];
                    table.PreviousTickHadDirtyEntities = true;
                    table.PreviousTickDirtyBitmap ??= Array.Empty<long>();
                }

                // WAL serialization requires WalManager — skip WAL write if not available
                if (WalManager == null)
                {
                    continue;
                }

                var layout = clusterState.Layout;
                ushort transientMask = meta.TransientSlotMask;
                int perEntityPayload = 0;
                for (int slot = 0; slot < layout.ComponentCount; slot++)
                {
                    if ((transientMask & (1 << slot)) != 0)
                    {
                        continue; // Transient data not persisted to WAL
                    }
                    perEntityPayload += layout.ComponentSize(slot);
                }

                if (perEntityPayload > ushort.MaxValue)
                {
                    continue; // Archetype too large for cluster tick fence — skip (shouldn't happen with cluster eligibility constraints)
                }

                int entrySize = 4 + perEntityPayload; // EntityIndex(4B) + AllComponentData
                int maxEntriesPerChunk =
                    (ushort.MaxValue - WalChunkHeader.SizeInBytes - ClusterTickFenceHeader.SizeInBytes - WalChunkFooter.SizeInBytes) / entrySize;

                int entriesRemaining = entryCount;
                int wordIndex = 0;
                long currentWord = wordIndex < dirtyBits.Length ? dirtyBits[wordIndex] : 0;

                while (entriesRemaining > 0)
                {
                    int batchCount = Math.Min(entriesRemaining, maxEntriesPerChunk);
                    int bodySize = ClusterTickFenceHeader.SizeInBytes + batchCount * entrySize;
                    int chunkSize = WalChunkHeader.SizeInBytes + bodySize + WalChunkFooter.SizeInBytes;

                    var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
                    var claim = WalManager.CommitBuffer.TryClaim(chunkSize, 1, ref wc);
                    if (!claim.IsValid)
                    {
                        break; // back-pressure — skip remaining entries
                    }

                    try
                    {
                        int offset = 0;

                        // WalChunkHeader
                        var chunkHeader = new WalChunkHeader
                        {
                            ChunkType = (ushort)WalChunkType.ClusterTickFence,
                            ChunkSize = (ushort)chunkSize,
                            PrevCRC = 0,
                        };
                        MemoryMarshal.Write(claim.DataSpan[offset..], in chunkHeader);
                        offset += WalChunkHeader.SizeInBytes;

                        // ClusterTickFenceHeader
                        var ctfHeader = new ClusterTickFenceHeader
                        {
                            TickNumber = tickNumber,
                            LSN = claim.FirstLSN,
                            ArchetypeId = meta.ArchetypeId,
                            EntryCount = (ushort)batchCount,
                            PerEntityPayload = (ushort)perEntityPayload,
                            ComponentCount = (byte)layout.ComponentCount,
                            Reserved = 0,
                        };
                        MemoryMarshal.Write(claim.DataSpan[offset..], in ctfHeader);
                        offset += ClusterTickFenceHeader.SizeInBytes;

                        // Write entries by iterating occupancy-masked dirty bits — one word = one cluster
                        int written = 0;
                        while (written < batchCount)
                        {
                            while (currentWord == 0 && wordIndex < dirtyBits.Length - 1)
                            {
                                wordIndex++;
                                currentWord = dirtyBits[wordIndex];
                            }

                            if (currentWord == 0)
                            {
                                break;
                            }

                            int bit = BitOperations.TrailingZeroCount((ulong)currentWord);
                            int clusterChunkId = wordIndex;
                            int slotIndex = bit;
                            int entityIndex = clusterChunkId * 64 + slotIndex;
                            currentWord &= currentWord - 1; // clear lowest set bit

                            // Write EntityIndex (4B)
                            MemoryMarshal.Write(claim.DataSpan[offset..], in entityIndex);
                            offset += 4;

                            // Write non-Transient component data from cluster SoA arrays
                            byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
                            for (int slot = 0; slot < layout.ComponentCount; slot++)
                            {
                                if ((transientMask & (1 << slot)) != 0)
                                {
                                    continue; // Transient data not persisted to WAL
                                }
                                int compOffset = layout.ComponentOffset(slot);
                                int compSize = layout.ComponentSize(slot);
                                byte* src = clusterBase + compOffset + slotIndex * compSize;
                                new ReadOnlySpan<byte>(src, compSize).CopyTo(claim.DataSpan[offset..]);
                                offset += compSize;
                            }

                            written++;
                        }

                        // WalChunkFooter
                        var footer = new WalChunkFooter { CRC = 0 };
                        MemoryMarshal.Write(claim.DataSpan[offset..], in footer);

                        WalManager.CommitBuffer.Publish(ref claim);
                        if (claim.FirstLSN > highestLSN)
                        {
                            highestLSN = claim.FirstLSN;
                        }
                    }
                    catch
                    {
                        WalManager.CommitBuffer.AbandonClaim(ref claim);
                        throw;
                    }

                    entriesRemaining -= batchCount;
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Recompute zone maps for all dirty clusters in the dirty bitmap snapshot.
    /// Each dirty cluster gets a full min/max scan for each indexed field.
    /// </summary>
    private unsafe void RecomputeClusterZoneMaps(ArchetypeClusterState clusterState, long[] dirtyBits)
    {
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
            {
                if (dirtyBits[wordIdx] == 0)
                {
                    continue;
                }

                int clusterChunkId = wordIdx;

                // Guard against freed/unallocated chunks (stale dirty bits from destroyed entities)
                if (clusterChunkId == 0 || !clusterState.ClusterSegment.IsChunkAllocated(clusterChunkId))
                {
                    continue;
                }

                byte* clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                var ixSlots = clusterState.IndexSlots;

                for (int s = 0; s < ixSlots.Length; s++)
                {
                    ref var ixSlot = ref ixSlots[s];
                    for (int f = 0; f < ixSlot.Fields.Length; f++)
                    {
                        ixSlot.Fields[f].ZoneMap?.Recompute(clusterChunkId, clusterBase, clusterState.Layout, ixSlot.Slot,
                            ixSlot.Fields[f].FieldOffset);
                    }
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Iterate dirty cluster entities and (1) detect cell crossings for migration (issue #229 Phase 3) and
    /// (2) update per-archetype spatial R-Tree positions.
    /// Called at tick boundary from <see cref="WriteClusterTickFence"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Precondition:</b> <paramref name="dirtyBits"/> has already been masked against live
    /// occupancy by <see cref="WriteClusterTickFence"/> (line ~916: <c>dirtyBits[i] &amp;= occupancy</c>). Every
    /// set bit in this array therefore corresponds to a currently-occupied slot. Breaking this invariant would
    /// let destroyed or reclaimed slots pollute migration detection and R-Tree updates. Do not split the pre-mask
    /// from this iteration without refreshing the occupancy guarantee.</para>
    ///
    /// <para><b>Migration detection</b> runs only when the archetype has opted into the spatial grid
    /// (<c>ClusterCellMap != null</c>, implying a configured <see cref="SpatialGrid"/>). The detection is
    /// cluster-coherent: all entities in a cluster share the same cell (Phase 1+2 invariant), so the current
    /// cell's world bounds and the hysteresis margin are hoisted out of the inner per-slot loop. The per-entity
    /// check is an exit-by-margin axis-aligned bounds test (4 comparisons, early-exit), only falling back to
    /// <see cref="SpatialGrid.WorldToCellKey"/> when the margin is actually exceeded. The hysteresis formulation
    /// is semantically equivalent to <c>claude/design/spatial-tiers/01-spatial-clusters.md</c> §"Migration
    /// Hysteresis" but reorganized for a fast common-case "entity stayed inside" path.</para>
    ///
    /// <para><b>Non-finite positions throw.</b> If an entity's spatial field contains NaN or Infinity,
    /// this method raises <see cref="InvalidOperationException"/> with diagnostic context (entity id, cluster,
    /// slot, position). Silent-clamping a non-finite position would produce invisible data corruption in the
    /// spatial index. The contract is: upstream systems MUST write finite positions. Consistent with Phase 1+2's
    /// spawn-time <see cref="SpatialGrid.WorldToCellKey"/> guard.</para>
    /// </remarks>
    private unsafe void DetectClusterMigrations(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> clusterAccessor)
    {
        ref var ss = ref clusterState.SpatialSlot;
        var layout = clusterState.Layout;
        var compSlot = ss.Slot;
        var compSize = layout.ComponentSize(compSlot);
        var compOffset = layout.ComponentOffset(compSlot);

        // Migration detection preconditions (issue #229 Phase 3).
        // Under issue #230 Phase 3 Option B the grid is ALWAYS configured for cluster spatial archetypes (enforced at InitializeArchetypes). The
        // clusterCellMap is populated during InitializeSpatial for every cluster-spatial archetype. Both are therefore non-null on entry; the old
        // `detectMigrations` guard collapses away.
        var grid = _spatialGrid;
        var clusterCellMap = clusterState.ClusterCellMap;
        var fieldType = ss.FieldInfo.FieldType;

        // Cell-invariant values reused inside the hot path.
        ref readonly var cfg = ref grid.Config;
        var cellSize = cfg.CellSize;
        var worldMinX = cfg.WorldMin.X;
        var worldMinY = cfg.WorldMin.Y;
        var hysteresisMargin = cellSize * cfg.MigrationHysteresisRatio;

        // Pre-size the pending-migrations queue to avoid Array.Resize in the hot detection loop.
        // Upper-bound estimate: last tick's migration count + 25% headroom, minimum 16. Steady-state
        // workloads have roughly the same rate across consecutive ticks, so this eliminates resize
        // churn on the first tick of a burst AND stays allocation-free afterward.
        var expectedCapacity = Math.Max(16, clusterState.LastTickMigrationCount + (clusterState.LastTickMigrationCount >> 2));
        if (clusterState.PendingMigrations == null || clusterState.PendingMigrations.Length < expectedCapacity)
        {
            clusterState.PendingMigrations = new MigrationRequest[expectedCapacity];
        }

        var hysteresisAbsorbedCount = 0;

        for (var wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
        {
            var word = dirtyBits[wordIdx];
            if (word == 0)
            {
                continue;
            }

            // wordIdx = clusterChunkId (cluster dirty bitmap is indexed by clusterChunkId)
            var clusterChunkId = wordIdx;
            var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);

            // ───────────────────────────────────────────────────────────────────────────
            // Cluster-invariant hoist for migration detection.
            // Phase 1+2 invariant: all entities in a cluster share a single cell. Compute the current cell's world bounds (and the hysteresis margin,
            // already precomputed) once per cluster and reuse for every slot. The 99% "entity stayed inside" path then costs 4 comparisons + optional
            // early-exit per entity.
            // ───────────────────────────────────────────────────────────────────────────
            var currentCellKey = clusterCellMap[clusterChunkId];
            if (currentCellKey < 0)
            {
                // Cluster not yet in the per-cell index — skip migration detection for this iteration.
                continue;
            }
            var (cx, cy) = grid.CellKeyToCoords(currentCellKey);
            var curCellMinX = worldMinX + cx * cellSize;
            var curCellMinY = worldMinY + cy * cellSize;
            var curCellMaxX = curCellMinX + cellSize;
            var curCellMaxY = curCellMinY + cellSize;

            while (word != 0)
            {
                var slotIndex = BitOperations.TrailingZeroCount((ulong)word);
                word &= word - 1;

                // Occupancy already masked in WriteClusterTickFence (dead slots filtered out)
                var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                var fieldPtr = clusterBase + compOffset + slotIndex * compSize + ss.FieldOffset;

                // ───────────────────────────────────────────────────────────────
                // Cell-crossing detection (issue #229 Phase 3).
                // ───────────────────────────────────────────────────────────────
                SpatialGrid.ReadSpatialCenter2D(fieldPtr, fieldType, out var posX, out var posY);

                // Non-finite guard. Consistent with Phase 1+2's spawn-time WorldToCellKey throw. Surface the upstream bug with full context.
                if (!float.IsFinite(posX) || !float.IsFinite(posY))
                {
                    throw new InvalidOperationException(
                        $"Non-finite position on spatial entity: entityId=0x{entityPK:X16}, " +
                        $"clusterChunkId={clusterChunkId}, slotIndex={slotIndex}, " +
                        $"position=({posX}, {posY}). Upstream system wrote NaN or Infinity into the spatial field — fix the producing system.");
                }

                // Exit-by-margin check. Early-exit: the first crossing axis short-circuits.
                // The 99% common case is "entity stayed well inside" → all 4 checks false → fall through.
                var exited = posX < curCellMinX - hysteresisMargin
                             || posX > curCellMaxX + hysteresisMargin
                             || posY < curCellMinY - hysteresisMargin
                             || posY > curCellMaxY + hysteresisMargin;
                if (exited)
                {
                    var newCellKey = grid.WorldToCellKey(posX, posY);
                    if (newCellKey != currentCellKey)
                    {
                        TyphonEvent.EmitSpatialClusterMigrationDetect(archetypeId, clusterChunkId, currentCellKey, newCellKey);
                        clusterState.EnqueueMigration(clusterChunkId, slotIndex, newCellKey);
                        TyphonEvent.EmitSpatialClusterMigrationQueue(archetypeId, clusterChunkId, (ushort)Math.Min(clusterState.PendingMigrationCount, ushort.MaxValue));
                    }
                }
                else if (posX < curCellMinX || posX > curCellMaxX || posY < curCellMinY || posY > curCellMaxY)
                {
                    // Entity crossed the raw cell boundary but is still within the hysteresis dead-zone. No migration. Count for tuning telemetry.
                    hysteresisAbsorbedCount++;
                    if (TelemetryConfig.SpatialClusterMigrationHysteresisActive)
                    {
                        // Approximate escape distance: max-axis overflow squared.
                        float ex = posX < curCellMinX ? (curCellMinX - posX) : (posX > curCellMaxX ? (posX - curCellMaxX) : 0f);
                        float ey = posY < curCellMinY ? (curCellMinY - posY) : (posY > curCellMaxY ? (posY - curCellMaxY) : 0f);
                        TyphonEvent.EmitSpatialClusterMigrationHysteresis(archetypeId, clusterChunkId, ex * ex + ey * ey);
                    }
                }
            }
        }

        // Publish hysteresis telemetry for this tick (reset on next tick fence entry).
        clusterState.LastTickHysteresisAbsorbedCount = hysteresisAbsorbedCount;
    }

    /// <summary>
    /// In-place ClusterEntityRecord field updater consumed by <see cref="RawValuePagedHashMap{TKey,TStore}.TryUpdateInPlace"/>
    /// during migration. Patches the 4-byte ClusterChunkId and 1-byte SlotIndex fields without rewriting the rest of the record.
    /// Struct (not ref struct) so it can sit on the stack as a local in <see cref="ExecuteMigrations"/> and pass through `ref`.
    /// </summary>
    private readonly unsafe struct ClusterLocationUpdater : IRawValueUpdater
    {
        private readonly int _chunkId;
        private readonly byte _slotIndex;

        public ClusterLocationUpdater(int chunkId, byte slotIndex)
        {
            _chunkId = chunkId;
            _slotIndex = slotIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(byte* valueBytes)
        {
            ClusterEntityRecordAccessor.SetClusterChunkId(valueBytes, _chunkId);
            ClusterEntityRecordAccessor.SetSlotIndex(valueBytes, _slotIndex);
        }
    }

    /// <summary>
    /// Execute all pending cell-crossing migrations queued by <see cref="DetectClusterMigrations"/>.
    /// Called at the cluster tick fence, AFTER detection, BEFORE the cluster tick fence WAL publish loop.
    /// Issue #229 Phase 3.
    /// </summary>
    /// <remarks>
    /// <para>Per-migration pipeline:</para>
    /// <list type="number">
    ///   <item>Read entity id from source slot</item>
    ///   <item><see cref="ArchetypeClusterState.ClaimSlotInCell"/> on the destination cell (allocates a new cluster if needed)</item>
    ///   <item>Copy every component slot's bytes source → destination (Persistent + Transient; Q8)</item>
    ///   <item>Copy EntityId and EnabledBits</item>
    ///   <item>Remove the old per-archetype B+Tree index entries and insert new ones at the new <c>clusterLocation</c></item>
    ///   <item>Remove the old spatial R-Tree back-pointer and insert a new one at the new <c>clusterLocation</c></item>
    ///   <item>Upsert the EntityMap <see cref="ClusterEntityRecordAccessor"/> with the new (chunkId, slot)</item>
    ///   <item><see cref="ArchetypeClusterState.ReleaseSlot"/> on the source (clears occupancy, decrements cell.EntityCount, detaches empty clusters)</item>
    ///   <item>Update <paramref name="dirtyBits"/> in place: clear the source bit (so WAL publish does not serialize a cleared source), set the destination
    ///         bit (so the destination's new content IS serialized by the subsequent ClusterTickFence WAL publish loop)</item>
    /// </list>
    ///
    /// <para><b>WAL atomicity.</b> All writes flow through a single <see cref="ChangeSet"/> scoped to this method,
    /// so either the entire migration batch lands or none of it does (Q1 decision). The enclosing
    /// <c>OnTickEndInternal</c> ordering — <c>WriteTickFence</c> before <c>UoW.Flush</c> — ensures the migration is durable
    /// within the tick that triggered it.</para>
    ///
    /// <para><b>Destination-cluster growth.</b> If <c>ClaimSlotInCell</c> allocates a brand-new cluster whose chunk id
    /// exceeds the current <paramref name="dirtyBits"/> length, the snapshot array is grown in place via
    /// <see cref="Array.Resize{T}(ref T[], int)"/> so the destination slot bit can be set and survive the subsequent
    /// WAL publish. The caller's local reference receives the grown array via the <c>ref</c> parameter. The archived
    /// <see cref="DirtyBitmapRing"/> and <see cref="ArchetypeClusterState.PreviousTickDirtySnapshot"/> both observe
    /// the grown array, keeping interest management and next-tick change dispatch consistent.</para>
    /// </remarks>
    private unsafe void ExecuteMigrations(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, ref long[] dirtyBits, ChangeSet changeSet)
    {
        int count = clusterState.PendingMigrationCount;
        if (count == 0)
        {
            clusterState.LastTickMigrationCount = 0;
            clusterState.LastTickMigrationExecuteMs = 0d;
            return;
        }

        long startTimestamp = Stopwatch.GetTimestamp();

        var layout = clusterState.Layout;
        int componentCount = layout.ComponentCount;
        // Total component instances moved this batch — surfaces in the profiler tooltip alongside the entity count
        // so users see the actual data-shuffling cost (a 3-component archetype migrating 1300 entities moves 3900
        // component slots' worth of data, not just 1300).
        using var migrationScope = TyphonEvent.BeginClusterMigration(archetypeId, count, count * componentCount);

        var grid = _spatialGrid;
        ushort transientMask = layout.TransientSlotMask;
        ref var ss = ref clusterState.SpatialSlot;
        int spatialCompSlot = ss.Slot;
        int spatialCompOffset = layout.ComponentOffset(spatialCompSlot);
        int spatialCompSize = layout.ComponentSize(spatialCompSlot);

        // Single-assignment accessor construction (TYPHON004 forbids the default→reassign pattern).
        bool hasClusterAccessor = clusterState.ClusterSegment != null;
        ChunkAccessor<PersistentStore> clusterAccessor = hasClusterAccessor ? clusterState.ClusterSegment.CreateChunkAccessor(changeSet) : default;

        bool hasTransientClusterAccessor = clusterState.TransientSegment != null;
        ChunkAccessor<TransientStore> transientClusterAccessor = hasTransientClusterAccessor ? clusterState.TransientSegment.CreateChunkAccessor() : default;

        bool hasIdxAccessor = clusterState.IndexSegment != null;
        ChunkAccessor<PersistentStore> idxAccessor = hasIdxAccessor ? clusterState.IndexSegment.CreateChunkAccessor(changeSet) : default;

        ChunkAccessor<PersistentStore> emAccessor = engineState.EntityMap.Segment.CreateChunkAccessor(changeSet);

        // Narrowphase scratch for the #230 Phase 1 per-cell index migration hook. Hoisted out of the
        // migration loop to avoid CA2014 stack-pressure accumulation — a batch of thousands of migrations
        // would otherwise allocate 32 bytes per iteration that can't be released until ExecuteMigrations
        // returns.
        // Sized for 3D ([minX, minY, minZ, maxX, maxY, maxZ]); 2D reads only populate the first 4 slots. Issue #230 Phase 3 unified 2D/3D per-cell paths.
        Span<double> migrantCoords = stackalloc double[6];

        try
        {
            var pending = clusterState.PendingMigrations;
            for (int i = 0; i < count; i++)
            {
                var req = pending[i];
                int srcChunkId = req.SourceClusterChunkId;
                int srcSlot = req.SourceSlotIndex;
                int destCellKey = req.DestCellKey;

                // 0. Stale-source guard: verify the source slot's occupancy bit is still set.
                // The detection phase reads occupancy through a read-only accessor (no ChangeSet → DC not bumped). If
                // checkpoint decremented DC to 0 between detection and execution, the page may have been evicted and
                // reloaded from disk with stale occupancy data. Skip the migration — the entity was already migrated
                // in a previous tick and the detection saw phantom occupancy.
                byte* srcPrimaryPre = hasClusterAccessor ? clusterAccessor.GetChunkAddress(srcChunkId, true) : transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                ulong srcOcc = *(ulong*)srcPrimaryPre;
                if ((srcOcc & (1UL << srcSlot)) == 0)
                {
                    continue;
                }

                // 1. Read entity id from source slot (needed before any reallocation pointer invalidation).
                long entityPK = *(long*)(srcPrimaryPre + layout.EntityIdsOffset + srcSlot * 8);

                // 2. Claim destination slot in the target cell. May allocate a new cluster (new chunk id).
                //    ClaimSlotInCell maintains cell.EntityCount / cell.ClusterCount + ClusterCellMap.
                int dstChunkId;
                int dstSlot;
                if (hasClusterAccessor)
                {
                    (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, ref clusterAccessor, changeSet, grid);
                }
                else
                {
                    (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, ref transientClusterAccessor, grid);
                }

                // 3. Re-fetch source / destination bases after potential segment growth inside ClaimSlotInCell.
                byte* srcBase;
                byte* dstBase;
                byte* srcTransBase = null;
                byte* dstTransBase = null;
                if (hasClusterAccessor)
                {
                    srcBase = clusterAccessor.GetChunkAddress(srcChunkId, true);
                    dstBase = clusterAccessor.GetChunkAddress(dstChunkId, true);
                    if (hasTransientClusterAccessor)
                    {
                        srcTransBase = transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                        dstTransBase = transientClusterAccessor.GetChunkAddress(dstChunkId, true);
                    }
                }
                else
                {
                    // Pure-Transient archetype: primary is the transient segment itself.
                    srcBase = transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                    dstBase = transientClusterAccessor.GetChunkAddress(dstChunkId, true);
                }

                // 4. Copy component data src → dst for EVERY slot, routing Transient vs Persistent via TransientSlotMask.
                //    Transient data survives across ticks (Q8) so both must be copied.
                for (int s = 0; s < componentCount; s++)
                {
                    int compSize = layout.ComponentSize(s);
                    int compOff = layout.ComponentOffset(s);
                    byte* sBase;
                    byte* dBase;
                    if ((transientMask & (1 << s)) != 0)
                    {
                        // Mixed archetype: transient slots live in the transient store. Pure-Transient archetype: primary
                        // IS the transient store, so srcBase/dstBase already point at it.
                        sBase = (srcTransBase != null) ? srcTransBase : srcBase;
                        dBase = (dstTransBase != null) ? dstTransBase : dstBase;
                    }
                    else
                    {
                        sBase = srcBase;
                        dBase = dstBase;
                    }
                    byte* src = sBase + compOff + srcSlot * compSize;
                    byte* dst = dBase + compOff + dstSlot * compSize;
                    Unsafe.CopyBlockUnaligned(dst, src, (uint)compSize);
                }

                // 5. Copy EntityId into destination slot primary segment.
                *(long*)(dstBase + layout.EntityIdsOffset + dstSlot * 8) = entityPK;

                // 6. Copy per-component EnabledBits. For each slot, transcribe src.bit(srcSlot) → dst.bit(dstSlot).
                //    Source bits are cleared later by ReleaseSlot.
                for (int s = 0; s < componentCount; s++)
                {
                    int ebOff = layout.EnabledBitsOffset(s);
                    ulong srcEnabled = *(ulong*)(srcBase + ebOff);
                    if ((srcEnabled & (1UL << srcSlot)) != 0)
                    {
                        *(ulong*)(dstBase + ebOff) |= 1UL << dstSlot;
                    }
                }

                int oldClusterLocation = srcChunkId * 64 + srcSlot;
                int newClusterLocation = dstChunkId * 64 + dstSlot;

                // 7. Update per-archetype B+Tree index entries. Key is unchanged (data was just copied); value
                //    (clusterLocation) changes. Follow the destroy+spawn primitive pattern: Remove(key) + Add(key, newLoc).
                if (hasIdxAccessor && clusterState.IndexSlots != null)
                {
                    var ixSlots = clusterState.IndexSlots;
                    for (int ixs = 0; ixs < ixSlots.Length; ixs++)
                    {
                        ref var ixSlot = ref ixSlots[ixs];
                        int ixCompSize = layout.ComponentSize(ixSlot.Slot);
                        byte* dstCompBase = dstBase + layout.ComponentOffset(ixSlot.Slot) + dstSlot * ixCompSize;
                        for (int fi = 0; fi < ixSlot.Fields.Length; fi++)
                        {
                            ref var field = ref ixSlot.Fields[fi];
                            byte* fieldPtr = dstCompBase + field.FieldOffset;
                            var key = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);
                            // For non-unique (AllowMultiple) cluster indexes, read the srcBase elementId from the
                            // source cluster's tail and call RemoveValue — Remove(key) would wipe the entire buffer
                            // at the key and corrupt siblings. srcBase is still the source cluster's bytes (the
                            // component COPY done in step 4 is src→dst, so the source tail is intact). Issue #229 Phase 3.
                            // Regression test: ClusterIndex_NonUniqueField_MigrateOneEntity_PreservesSiblingsInIndex.
                            if (field.AllowMultiple)
                            {
                                int elementId = *(int*)(srcBase + layout.IndexElementIdOffset(field.MultiFieldIndex, srcSlot));
                                field.Index.RemoveValue(&key, elementId, oldClusterLocation, ref idxAccessor);
                                int newElementId = field.Index.Add(fieldPtr, newClusterLocation, ref idxAccessor);
                                *(int*)(dstBase + layout.IndexElementIdOffset(field.MultiFieldIndex, dstSlot)) = newElementId;
                            }
                            else
                            {
                                field.Index.Remove(&key, out _, ref idxAccessor);
                                field.Index.Add(fieldPtr, newClusterLocation, ref idxAccessor);
                            }
                            field.ZoneMap?.Widen(dstChunkId, fieldPtr);
                        }
                    }
                }

                // 8. Maintain per-cell cluster AABB index at the destination (issue #230 Phase 3 Option B: the legacy R-Tree step 8 call has been removed;
                // the per-cell index is the single source of truth).
                byte* dstFieldPtr = dstBase + spatialCompOffset + dstSlot * spatialCompSize + ss.FieldOffset;

                // Union the migrant's bounds into the dst cluster's AABB.
                // If dst is a brand-new cluster (first entity since allocation), reset the AABB to Empty first so any stale state from a prior life of
                // the chunk id is discarded. Gated on Dynamic mode (static mode is handled at spawn/destroy only — static clusters don't migrate).
                // The src cluster's AABB stays conservative (not shrunk) — Phase 1 trade-off.
                // If src becomes empty, ReleaseSlot below → FinaliseEmptyClusterCellState removes it from the per-cell index.
                if (ss.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.ClusterCellMap != null)
                {
                    if (SpatialMaintainer.ReadAndValidateBoundsFromPtr(dstFieldPtr, ss.FieldInfo, migrantCoords, ss.Descriptor))
                    {
                        clusterState.EnsureClusterAabbsCapacity(dstChunkId + 1);
                        clusterState.EnsureClusterSpatialIndexSlotCapacity(dstChunkId + 1);

                        bool wasInIndex = clusterState.ClusterSpatialIndexSlot[dstChunkId] >= 0;
                        ref var dstClusterAabb = ref clusterState.ClusterAabbs[dstChunkId];
                        if (!wasInIndex)
                        {
                            dstClusterAabb = ClusterSpatialAabb.Empty;
                        }
                        // Tier-dispatched union: 2D fields wrote [minX, minY, maxX, maxY] into the first 4 slots; 3D fields wrote the full 6-double layout.
                        // Category mask comes from the archetype-level [SpatialIndex(Category=)] attribute (issue #230 Phase 3).
                        uint archetypeCategory = ss.FieldInfo.Category;
                        if (ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F)
                        {
                            dstClusterAabb.Union3F(
                                (float)migrantCoords[0], (float)migrantCoords[1], (float)migrantCoords[2],
                                (float)migrantCoords[3], (float)migrantCoords[4], (float)migrantCoords[5],
                                archetypeCategory);
                        }
                        else
                        {
                            dstClusterAabb.Union2F(
                                (float)migrantCoords[0], (float)migrantCoords[1],
                                (float)migrantCoords[2], (float)migrantCoords[3],
                                archetypeCategory);
                        }

                        int dstCellKey = clusterState.ClusterCellMap[dstChunkId];
                        if (dstCellKey >= 0)
                        {
                            if (!wasInIndex)
                            {
                                clusterState.AddClusterToPerCellIndex(dstChunkId, dstCellKey, dstClusterAabb);
                            }
                            else
                            {
                                int indexSlot = clusterState.ClusterSpatialIndexSlot[dstChunkId];
                                clusterState.PerCellIndex[dstCellKey].DynamicIndex.UpdateAt(indexSlot, in dstClusterAabb);
                            }
                        }
                    }
                }

                // 9. Update EntityMap ClusterEntityRecord with the new (clusterChunkId, slotIndex).
                //    CRITICAL: EntityMap is keyed by EntityKey (the 52-bit top half of RawValue), NOT by the full RawValue stored in cluster slots.
                //    Passing RawValue here would silently miss every lookup — the map would never get updated, and the entity would remain resolvable via
                //    its stale (srcChunkId, srcSlot) pointer until a subsequent spawn reclaimed that slot, at which point the stale EntityMap entry would
                //    resolve to the unrelated new entity's bytes. Unpack explicitly (unsigned shift to avoid sign extension on the top bit).
                //    Regression test: Migration_ThenSubsequentSpawn_ReclaimingSourceSlot_DoesNotCorruptMigratedEntity.
                //    In-place primitive (TryUpdateInPlace) — single hash → bucket → chain scan, mutate the 5 bytes that change
                //    (4-byte ChunkId + 1-byte SlotIndex) under the bucket's OLC write lock. Halves the EntityMap stage cost vs the
                //    pre-#TBD TryGet+Upsert pair which did two chain scans + a full-record stack copy + double OLC traversal.
                //    Returns false if the entity is already gone (destroy race precondition from Q9 says the occupancy pre-mask
                //    should have filtered this out, but the no-op return preserves the same forgiving semantics as before).
                long entityKey = entityPK >>> 12;
                var clusterLocationUpdater = new ClusterLocationUpdater(dstChunkId, (byte)dstSlot);
                engineState.EntityMap.TryUpdateInPlace(entityKey, ref clusterLocationUpdater, ref emAccessor, changeSet);

                // 10. Release the source slot. Clears occupancy, EnabledBits, EntityId, decrements cell.EntityCount, detaches the cluster from its cell pool
                // if it became empty.
                if (hasClusterAccessor)
                {
                    clusterState.ReleaseSlot(ref clusterAccessor, srcChunkId, srcSlot, changeSet, grid);
                }
                else
                {
                    clusterState.ReleaseSlot(ref transientClusterAccessor, srcChunkId, srcSlot, grid);
                }

                // 11. Maintain dirtyBits so the subsequent ClusterTickFence WAL publish serializes the right slots.
                //     Clear the source bit (we just cleared the slot's data; serializing it would write stale bytes).
                //     Set the destination bit (the new content must be in the WAL for replay).
                //     If the destination is a brand-new cluster whose chunk id exceeds the snapshot length, grow the snapshot in place (doubling, at least
                //     dstChunkId+1). The caller's local reference is updated via the ref parameter so subsequent readers (DirtyRing archive, WAL publish loop,
                //     PreviousTickDirtySnapshot) see the grown array.
                if (srcChunkId < dirtyBits.Length)
                {
                    dirtyBits[srcChunkId] &= ~(1L << srcSlot);
                }
                if (dstChunkId >= dirtyBits.Length)
                {
                    int newLength = Math.Max(dirtyBits.Length * 2, dstChunkId + 1);
                    Array.Resize(ref dirtyBits, newLength);
                }
                dirtyBits[dstChunkId] |= 1L << dstSlot;
            }
        }
        finally
        {
            emAccessor.Dispose();
            if (hasIdxAccessor)
            {
                idxAccessor.Dispose();
            }
            if (hasTransientClusterAccessor)
            {
                transientClusterAccessor.Dispose();
            }
            if (hasClusterAccessor)
            {
                clusterAccessor.Dispose();
            }

            // saveChanges and ReleaseExcessDirtyMarks are deliberately NOT called here. ExecuteMigrations operates on the UoW's shared ChangeSet (passed
            // by the caller through WriteClusterTickFence → WriteTickFence). The UoW owns the commit lifecycle: in WAL mode SaveChanges is never called
            // (WAL records replace direct page writes); in WAL-less GroupCommit/Deferred modes UoW.Flush invokes SaveChanges + FlushToDisk centrally;
            // ReleaseExcessDirtyMarks happens once at UoW disposal. See claude/overview/02-execution.md §2.1 (UoW lifecycle) and §2.3 (durability modes).
            // Test/admin callers that invoke WriteTickFence without a UoW get a one-shot local ChangeSet created and committed by WriteTickFence itself.

            // Reset queue for next tick BEFORE any re-throw path can escape. Leaving entries in the queue after a mid-batch exception would cause the next
            // tick's ExecuteMigrations to re-attempt already-applied migrations, double-allocating destination slots and orphaning source clusters.
            // Losing the failing migration is the lesser evil compared to replaying partial state.
            clusterState.PendingMigrationCount = 0;
        }

        long endTimestamp = Stopwatch.GetTimestamp();
        double durationMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        clusterState.LastTickMigrationCount = count;
        clusterState.LastTickMigrationExecuteMs = durationMs;
        // Test observation hook: record the final dirtyBits length so regression tests can verify the snapshot was grown when migration allocated a new
        // destination cluster beyond the pre-migration length.
        clusterState.LastMigrationDirtyBitsWordCount = dirtyBits.Length;

        // High-migration-rate warning. Analogous to LogHighEscapeRate — surfaces potential viewport warps, teleport events, or unphysical entity speeds that
        // blow past cell boundaries en masse. 1K per tick is an arbitrary absolute threshold; revisit once AntHill provides realistic rates.
        // To silence: configure Microsoft.Extensions.Logging filter for "Typhon.Engine.Data.SpatialMaintainer" at Error level.
        if (count >= 1000)
        {
            SpatialMaintainer.LogHighMigrationRate(Logger, count, archetypeId, durationMs);
        }
    }

    /// <summary>
    /// Drains the per-archetype shadow buffers for cluster-backed indexed fields, updating per-archetype B+Trees.
    /// Reads current field values from cluster SoA, compares with captured old values, and calls B+Tree.Move for changes.
    /// Called at tick boundary from <see cref="WriteClusterTickFence"/>.
    /// </summary>
    private unsafe void ProcessClusterShadowEntries(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ChangeSet changeSet)
    {
        // Quick check: any shadow buffers non-empty? Skip allocation if all empty.
        bool anyShadow = false;
        var ixSlots = clusterState.IndexSlots;
        for (int s = 0; s < ixSlots.Length && !anyShadow; s++)
        {
            for (int f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                if (ixSlots[s].ShadowBuffers[f].Count > 0)
                {
                    anyShadow = true;
                    break;
                }
            }
        }

        if (!anyShadow)
        {
            clusterState.ClusterShadowBitmap.Clear();
            return;
        }

        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();

        try
        {
            for (int s = 0; s < ixSlots.Length; s++)
            {
                ref var ixSlot = ref ixSlots[s];
                for (int f = 0; f < ixSlot.Fields.Length; f++)
                {
                    var buffer = ixSlot.ShadowBuffers[f];
                    int count = buffer.Count;
                    if (count == 0)
                    {
                        continue;
                    }

                    ref var field = ref ixSlot.Fields[f];
                    var idxAccessor = field.Index.Segment.CreateChunkAccessor(changeSet);

                    try
                    {
                        for (int e = 0; e < count; e++)
                        {
                            ref var entry = ref buffer[e];
                            int clusterChunkId = entry.ChunkId >> 6;   // entityIndex → chunkId
                            int slotIndex = entry.ChunkId & 0x3F;      // entityIndex → slot

                            // Check occupancy (entity may have been destroyed this tick)
                            byte* clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                            ulong occupancy = *(ulong*)clusterBase;
                            if ((occupancy & (1UL << slotIndex)) == 0)
                            {
                                // Entity destroyed — remove old index entry using shadow value
                                var destroyOldKey = entry.OldKey;
                                field.Index.Remove(&destroyOldKey, out _, ref idxAccessor);

                                // Notify views of deletion (same pattern as ProcessShadowFieldEntries)
                                var table = engineState.SlotToComponentTable[ixSlot.Slot];
                                var delViews = table.ViewRegistry.GetViewsForField(f);
                                for (int v = 0; v < delViews.Length; v++)
                                {
                                    var reg = delViews[v];
                                    if (reg.View.IsDisposed)
                                    {
                                        continue;
                                    }

                                    byte delFlags = (byte)((f & 0x3F) | 0x80); // isDeletion
                                    reg.View.DeltaBuffer.TryAppend(entry.EntityPK, entry.OldKey, default, 0, delFlags, reg.ComponentTag);
                                }

                                continue;
                            }

                            // Read current (post-mutation) field value from cluster SoA
                            int compSize = clusterState.Layout.ComponentSize(ixSlot.Slot);
                            byte* compBase = clusterBase + clusterState.Layout.ComponentOffset(ixSlot.Slot) + slotIndex * compSize;
                            byte* fieldPtr = compBase + field.FieldOffset;
                            var oldKey = entry.OldKey;
                            var newKey = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);

                            if (oldKey.RawValue == newKey.RawValue)
                            {
                                continue; // Field didn't actually change
                            }

                            // Update per-archetype B+Tree: remove old key, insert new key, same ClusterLocation value
                            int clusterLocation = entry.ChunkId; // entityIndex = clusterLocation
                            field.Index.Move(&oldKey, fieldPtr, clusterLocation, ref idxAccessor);

                            // Notify registered views (same pattern as ProcessShadowFieldEntries)
                            {
                                var table = engineState.SlotToComponentTable[ixSlot.Slot];
                                var views = table.ViewRegistry.GetViewsForField(f);
                                for (int v = 0; v < views.Length; v++)
                                {
                                    var reg = views[v];
                                    if (reg.View.IsDisposed)
                                    {
                                        continue;
                                    }

                                    byte flags = (byte)(f & 0x3F);
                                    reg.View.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
                                }
                            }
                        }
                    }
                    finally
                    {
                        idxAccessor.Dispose();
                    }

                    buffer.Reset();
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
            // SaveChanges deliberately omitted: caller (WriteTickFence) owns the ChangeSet lifecycle. See ExecuteMigrations finally for full rationale.
        }

        clusterState.ClusterShadowBitmap.Clear();
    }

    /// <summary>
    /// Drains the per-field shadow buffers for a SingleVersion ComponentTable, updating indexes and notifying views for any field values that changed since
    /// the shadow was captured.
    /// Called at tick boundary from <see cref="WriteTickFence"/>.
    /// </summary>
    private void ProcessShadowEntries(ComponentTable table, ChangeSet changeSet)
    {
        var fields = table.IndexedFieldInfos;
        var buffers = table.FieldShadowBuffers;
        bool isTransient = table.StorageMode == StorageMode.Transient;

        for (int fieldIdx = 0; fieldIdx < fields.Length; fieldIdx++)
        {
            var buffer = buffers[fieldIdx];
            int count = buffer.Count;
            if (count == 0)
            {
                continue;
            }

            ref var ifi = ref fields[fieldIdx];

            if (isTransient)
            {
                var index = ifi.TransientIndex;
                var compAccessor = table.TransientComponentSegment.CreateChunkAccessor();
                var idxAccessor = index.Segment.CreateChunkAccessor();
                try
                {
                    ProcessShadowFieldEntries(table, fieldIdx, ref ifi, buffer, count, index, ref compAccessor, ref idxAccessor);
                }
                finally
                {
                    compAccessor.Dispose();
                    idxAccessor.Dispose();
                }
            }
            else
            {
                var index = ifi.PersistentIndex;

                // ChangeSet required for index write operations (Move/MoveValue may trigger TAIL segment growth for AllowMultiple indexes).
                // Reuse the caller's shared ChangeSet — UoW owns the commit lifecycle (see WriteTickFence).
                var compAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
                var idxAccessor = index.Segment.CreateChunkAccessor(changeSet);
                try
                {
                    ProcessShadowFieldEntries(table, fieldIdx, ref ifi, buffer, count, index, ref compAccessor, ref idxAccessor);
                }
                finally
                {
                    compAccessor.Dispose();
                    idxAccessor.Dispose();
                }
            }

            buffer.Reset();
        }

        table.ShadowBitmap.Clear();
        table.ClearDestroyedChunkIds();
    }

    /// <summary>
    /// Processes all shadow entries for a single indexed field, updating the B+Tree index and notifying views.
    /// Generic over TStore to support both PersistentStore (Versioned/SV) and TransientStore paths.
    /// </summary>
    private static unsafe void ProcessShadowFieldEntries<TStore>(ComponentTable table, int fieldIdx, ref IndexedFieldInfo ifi,
        FieldShadowBuffer buffer, int count, BTreeBase<TStore> index, ref ChunkAccessor<TStore> compAccessor, ref ChunkAccessor<TStore> idxAccessor)
        where TStore : struct, IPageStore
    {
        for (int e = 0; e < count; e++)
        {
            ref var entry = ref buffer[e];

            // Check if entity was destroyed this tick.
            // PrepareEcsDestroys handles non-shadowed destroys; here we handle shadowed (mutated-then-destroyed).
            if (table.IsChunkDestroyed(entry.ChunkId))
            {
                // Entity is dead — remove old index entry using shadow value (matches current index key).
                // Copy to local to allow address-of on stack variable.
                var destroyOldKey = entry.OldKey;
                if (index.AllowMultiple)
                {
                    byte* ptr = compAccessor.GetChunkAddress(entry.ChunkId);
                    int elementId = *(int*)(ptr + ifi.OffsetToIndexElementId);
                    index.RemoveValue(&destroyOldKey, elementId, entry.ChunkId, ref idxAccessor);
                }
                else
                {
                    index.Remove(&destroyOldKey, out _, ref idxAccessor);
                }

                // Notify views of deletion
                var delViews = table.ViewRegistry.GetViewsForField(fieldIdx);
                for (int v = 0; v < delViews.Length; v++)
                {
                    var reg = delViews[v];
                    if (reg.View.IsDisposed)
                    {
                        continue;
                    }

                    byte delFlags = (byte)((fieldIdx & 0x3F) | 0x80); // isDeletion
                    reg.View.DeltaBuffer.TryAppend(entry.EntityPK, entry.OldKey, default, 0, delFlags, reg.ComponentTag);
                }

                continue;
            }

            // Read current (post-mutation) field value
            byte* chunkPtr = compAccessor.GetChunkAddress(entry.ChunkId);
            byte* newFieldPtr = chunkPtr + ifi.OffsetToField;
            var oldKey = entry.OldKey;
            var newKey = KeyBytes8.FromPointer(newFieldPtr, ifi.Size);

            // Skip if field value didn't actually change
            if (oldKey.RawValue == newKey.RawValue)
            {
                continue;
            }

            // Update B+Tree index
            if (index.AllowMultiple)
            {
                int elementId = *(int*)(chunkPtr + ifi.OffsetToIndexElementId);
                int newElementId = index.MoveValue(&oldKey, newFieldPtr, elementId, entry.ChunkId, ref idxAccessor, out _, out _);
                // Write back new element ID — page is already dirty from the mutation that triggered shadowing
                *(int*)(chunkPtr + ifi.OffsetToIndexElementId) = newElementId;
            }
            else
            {
                index.Move(&oldKey, newFieldPtr, entry.ChunkId, ref idxAccessor);
            }

            // Notify registered views
            var views = table.ViewRegistry.GetViewsForField(fieldIdx);
            for (int v = 0; v < views.Length; v++)
            {
                var reg = views[v];
                if (reg.View.IsDisposed)
                {
                    continue;
                }

                byte flags = (byte)(fieldIdx & 0x3F);
                reg.View.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
            }
        }
    }

    /// <summary>
    /// Iterate dirty entities from the tick fence snapshot and update spatial R-Tree positions.
    /// For each dirty entity: if not destroyed, call UpdateSpatial (fat AABB containment check → possible reinsert).
    /// </summary>
    private unsafe void ProcessSpatialEntries(ComponentTable table, long[] dirtyBits, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;

        // Hoist accessor creation before the entity loop (same pattern as B+Tree batch index maintenance)
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
        var treeAccessor = state.ActiveTree.Segment.CreateChunkAccessor(changeSet);
        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        int dirtyCount = 0;
        int escapeCount = 0;
        try
        {
            for (int wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
            {
                long word = dirtyBits[wordIdx];
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount((ulong)word);
                    int chunkId = wordIdx * 64 + bit;
                    word &= word - 1; // clear lowest set bit

                    if (table.IsChunkDestroyed(chunkId))
                    {
                        continue;
                    }

                    long entityPK = 0;
                    if (table.Definition.EntityPKOverheadSize > 0)
                    {
                        byte* chunkPtr = compAccessor.GetChunkAddress(chunkId);
                        entityPK = *(long*)chunkPtr;
                    }

                    dirtyCount++;
                    if (SpatialMaintainer.UpdateSpatialBatch(entityPK, chunkId, table, ref compAccessor, ref treeAccessor, ref bpAccessor, changeSet))
                    {
                        escapeCount++;
                    }
                }
            }
        }
        finally
        {
            bpAccessor.Dispose();
            treeAccessor.Dispose();
            compAccessor.Dispose();
            // SaveChanges deliberately omitted: caller (WriteTickFence) owns the ChangeSet lifecycle.
        }

        // Escape rate telemetry: warn when > 10% of dirty entities escape their fat AABB.
        // To silence: configure Microsoft.Extensions.Logging filter for "Typhon.Engine.Data.SpatialMaintainer" at Error level.
        if (dirtyCount > 0)
        {
            double escapeRate = (double)escapeCount / dirtyCount;
            if (escapeRate > 0.10)
            {
                SpatialMaintainer.LogHighEscapeRate(Logger, table.Definition.Name, escapeRate, escapeCount, dirtyCount);
            }
        }
    }

    /// <summary>
    /// Persist spatial index segment root page indexes to BootstrapDictionary.
    /// Written once at component registration; segment root pages are immutable after allocation.
    /// </summary>
    private void SaveSpatialBootstrap(ComponentTable table)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        string key = $"spatial.{table.Definition.Name}";

        // Tree SPIs + config packed into Int5: treeSPI, backPtrSPI, variant|mode|stride, margin bits, hmSPI (0 if no hashmap)
        var activeTree = state.ActiveTree;
        MMF.Bootstrap.Set(key, BootstrapDictionary.Value.FromInt5(
            activeTree.Segment.RootPageIndex,
            state.BackPointerSegment.RootPageIndex,
            (int)activeTree.Variant | ((int)fi.Mode << 4) | (state.Descriptor.Stride << 8),
            BitConverter.SingleToInt32Bits(fi.Margin),
            state.OccupancyMap?.Segment.RootPageIndex ?? 0));

        MMF.SaveBootstrap();
    }

    /// <summary>
    /// Load spatial index from BootstrapDictionary and attach to the ComponentTable.
    /// Called during database reopen for components with [SpatialIndex].
    /// </summary>
    private void LoadSpatialBootstrap(ComponentTable table)
    {
        string key = $"spatial.{table.Definition.Name}";
        if (!MMF.Bootstrap.TryGet(key, out var val))
        {
            return; // No spatial index persisted (new attribute added after last save)
        }

        int treeSPI = val.GetInt();
        int backPtrSPI = val.GetInt(1);
        int variantStride = val.GetInt(2);

        var variant = (SpatialVariant)(variantStride & 0x0F);
        var mode = (SpatialMode)((variantStride >> 4) & 0x0F);
        var stride = variantStride >> 8;
        var descriptor = SpatialNodeDescriptor.FromVariant(variant, stride);

        var treeSegment = MMF.LoadChunkBasedSegment(treeSPI, descriptor.Stride);
        var backPtrSegment = MMF.LoadChunkBasedSegment(backPtrSPI, 8);

        // Load Layer 1 occupancy hashmap if persisted (Int5[4] > 0)
        PagedHashMap<long, int, PersistentStore> occupancyMap = null;
        int hmSPI = val.GetInt(4);
        if (hmSPI > 0)
        {
            int hmStride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
            var hmSegment = MMF.LoadChunkBasedSegment(hmSPI, hmStride);
            occupancyMap = PagedHashMap<long, int, PersistentStore>.Open(hmSegment);
        }

        var tree = new SpatialRTree<PersistentStore>(treeSegment, variant, true);
        tree.BackPointerSegment = backPtrSegment;

        var sf = table.Definition.SpatialField;
        var fieldInfo = new SpatialFieldInfo(table.ComponentOverhead + sf.OffsetInComponentStorage, sf.SizeInComponentStorage, sf.SpatialFieldType,
            sf.SpatialMargin, sf.SpatialCellSize, mode, sf.SpatialCategory);

        SpatialRTree<PersistentStore> staticTree = null, dynamicTree = null;
        if (mode == SpatialMode.Static)
        {
            staticTree = tree;
        }
        else
        {
            dynamicTree = tree;
        }
        table.SpatialIndex = new SpatialIndexState(staticTree, dynamicTree, backPtrSegment, fieldInfo, descriptor, occupancyMap);
    }

    private void ConstructComponentStore()
    {
        _componentTableByType = new ConcurrentDictionary<Type, ComponentTable>();
        _componentTableByWalTypeId = new ConcurrentDictionary<ushort, ComponentTable>();
    }

    private void InitializeUowRegistry()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        if (MMF.IsDatabaseFileCreating)
        {
            // Creating path: allocate a 1-page segment for the registry (150 entries)
            var cs = MMF.CreateChangeSet();
            var segment = MMF.AllocateSegment(PageBlockType.None, 1, cs);

            // Clear the data area so all entries start as Free (State = 0)
            var page = segment.GetPageExclusive(0, epoch, out var memPageIdx);
            cs.AddByMemPageIndex(memPageIdx);
            var offset = LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength;
            page.RawData<byte>(offset, PagedMMF.PageRawDataSize - offset).Clear();
            MMF.UnlatchPageExclusive(memPageIdx);

            // Write SPI to root header
            MMF.RequestPageEpoch(0, epoch, out int _);
            MMF.Bootstrap.SetInt(BK_UowRegistrySPI, segment.RootPageIndex);
            MMF.SaveBootstrap(cs);

            cs.SaveChanges();

            UowRegistry = new UowRegistry(segment, MMF, EpochManager, MemoryAllocator, this);
            UowRegistry.Initialize();
        }
        else
        {
            // Loading path: read SPIs from bootstrap
            var spi = MMF.Bootstrap.GetInt(BK_UowRegistrySPI);
            var checkpointLSN = MMF.Bootstrap.GetLong(ManagedPagedMMF.BK_CheckpointLSN);
            var segment = MMF.GetSegment(spi);
            UowRegistry = new UowRegistry(segment, MMF, EpochManager, MemoryAllocator, this);

            var walDir = _options.Wal?.WalDirectory;
            if (walDir != null && _walFileIO != null && System.IO.Directory.Exists(walDir) && System.IO.Directory.GetFiles(walDir, "*.wal").Length > 0)
            {
                // Two-phase WAL recovery: LoadFromDiskRaw preserves Pending entries for WAL cross-referencing
                UowRegistry.LoadFromDiskRaw();
                using var recovery = new WalRecovery(_walFileIO, walDir, MMF);
                // Pass null for dbe: replay is deferred until component tables are registered (system schema auto-loading, #57)
                _lastRecoveryResult = recovery.Recover(UowRegistry, checkpointLSN, null);
            }
            else
            {
                // No WAL segments — original path (voids all Pending entries)
                UowRegistry.LoadFromDisk();
            }
        }
    }

    private static int RoundToStandardStride(int size) =>
        size switch
        {
            <= 16 => 16,
            <= 32 => 32,
            <= 64 => 64,
            _ => (int)BitOperations.RoundUpToPowerOf2((uint)size)
        };

    private const int ComponentCollectionItemCountPerChunk      = 8;
    private const int ComponentCollectionSegmentStartingSize    = 8;

    internal VariableSizedBufferSegment<T, PersistentStore> GetComponentCollectionVSBS<T>() where T : unmanaged =>
        (VariableSizedBufferSegment<T, PersistentStore>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            _ => new VariableSizedBufferSegment<T, PersistentStore>(GetComponentCollectionSegment<T>()));

    internal VariableSizedBufferSegmentBase<PersistentStore> GetComponentCollectionVSBS(Type itemType, ChangeSet changeSet = null) =>
        _componentCollectionVSBSByType.GetOrAdd(itemType,
            type =>
            {
                // Create the type for ComponentCollection<T>
                var ctType = typeof(VariableSizedBufferSegment<,>).MakeGenericType(type, typeof(PersistentStore));
                // Use the actual struct size (Marshal.SizeOf) to match sizeof(T) in the generic overload.
                // DatabaseSchemaExtensions.FromType() maps [Component]-attributed types to FieldType.Component (8 bytes),// which is the storage size of a
                // component *reference*, not the struct itself.
                var fieldSize = Marshal.SizeOf(type);
                var segment = GetComponentCollectionSegment(fieldSize, changeSet);
                return (VariableSizedBufferSegmentBase<PersistentStore>)Activator.CreateInstance(ctType, segment);
            });

    unsafe internal ChunkBasedSegment<PersistentStore> GetComponentCollectionSegment<T>() where T : unmanaged =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(sizeof(T) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride));

    unsafe internal ChunkBasedSegment<PersistentStore> GetComponentCollectionSegment(int itemSize, ChangeSet changeSet = null) =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(itemSize * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride, changeSet));

    // Create the first revision of the system schema
    private unsafe void CreateSystemSchemaR1()
    {
        // Single ChangeSet tracks all structural pages (segments, BTree directories, occupancy bitmaps)
        // allocated during component registration. This replaces the old FlushAllCachedPages() nuclear approach.
        var cs = MMF.CreateChangeSet();

        // Register core system components first, then assign _componentsTable so that
        // subsequent registrations (ArchetypeR1) can persist their schema to the system table.
        RegisterComponentFromAccessor<ComponentR1>(cs);
        RegisterComponentFromAccessor<SchemaHistoryR1>(cs);
        _componentsTable = GetComponentTable<ComponentR1>();
        _schemaHistoryTable = GetComponentTable<SchemaHistoryR1>();

        // ArchetypeR1 registered AFTER _componentsTable is set — ensures its ComponentR1 row
        // is persisted to the system schema (needed for LoadPersistedArchetypes on reopen).
        RegisterComponentFromAccessor<ArchetypeR1>(cs);

        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = MMF.TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during schema save");
        MMF.GetPage(memPageIdx);

        // Save the entry points in the bootstrap dictionary
        cs.AddByMemPageIndex(memPageIdx);

        var bootstrap = MMF.Bootstrap;
        bootstrap.SetInt(BK_SystemSchemaRevision, 1);
        bootstrap.Set(BK_SysComponentR1, BootstrapDictionary.Value.FromInt4(
            _componentsTable.ComponentSegment.RootPageIndex,
            _componentsTable.CompRevTableSegment.RootPageIndex,
            _componentsTable.DefaultIndexSegment.RootPageIndex,
            _componentsTable.String64IndexSegment.RootPageIndex));
        bootstrap.Set(BK_SysSchemaHistory, BootstrapDictionary.Value.FromInt4(
            _schemaHistoryTable.ComponentSegment.RootPageIndex,
            _schemaHistoryTable.CompRevTableSegment.RootPageIndex,
            _schemaHistoryTable.DefaultIndexSegment.RootPageIndex,
            _schemaHistoryTable.String64IndexSegment.RootPageIndex));
        bootstrap.SetLong(BK_NextFreeTSN, TransactionChain.NextFreeId);

        MMF.UnlatchPageExclusive(memPageIdx);

        // Pre-allocate the FieldR1 ComponentCollection segment
        GetComponentCollectionSegment(sizeof(FieldR1), cs);

        // Save the system components schema in the database
        SaveInSystemSchema(_componentsTable);
        SaveInSystemSchema(_schemaHistoryTable);

        // Persist the FieldCollection SPI in bootstrap
        bootstrap.SetInt(BK_CollectionFieldR1, GetComponentCollectionSegment<FieldR1>().RootPageIndex);

        // Save bootstrap to page 0
        MMF.SaveBootstrap(cs);

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    private (int ChunkId, ComponentR1 Comp, FieldR1[] Fields) SaveInSystemSchema(ComponentTable table)
    {
        var definition = table.Definition;
        var cs = MMF.CreateChangeSet();

        var nonStaticCount = 0;
        foreach (var kvp in definition.FieldsByName)
        {
            if (!kvp.Value.IsStatic)
            {
                nonStaticCount++;
            }
        }

        var comp = new ComponentR1
        {
            Name                = (String64)definition.Name,
            POCOType            = (String64)definition.POCOType.FullName,
            CompSize             = definition.ComponentStorageSize,
            CompOverhead         = definition.ComponentStorageOverhead,
            ComponentSPI        = table.ComponentSegment?.RootPageIndex ?? 0,
            VersionSPI          = table.CompRevTableSegment?.RootPageIndex ?? 0,
            DefaultIndexSPI     = table.DefaultIndexSegment?.RootPageIndex ?? 0,
            String64IndexSPI    = table.String64IndexSegment?.RootPageIndex ?? 0,
            TailIndexSPI        = table.TailIndexSegment?.RootPageIndex ?? 0,
            SchemaRevision      = definition.Revision,
            FieldCount          = nonStaticCount,
            StorageMode         = (byte)table.StorageMode,
        };

        var fieldList = new List<FieldR1>();
        {
            using var guard = EpochGuard.Enter(EpochManager);
            var vsbs = GetComponentCollectionVSBS<FieldR1>();
            using var a = new ComponentCollectionAccessor<FieldR1>(cs, vsbs, ref comp.Fields);

            foreach (var kvp in table.Definition.FieldsByName)
            {
                var field = kvp.Value;
                var f = new FieldR1
                {
                    Name = (String64)field.Name,
                    FieldId = field.FieldId,
                    Type = field.Type,
                    UnderlyingType = field.UnderlyingType,
                    ArrayLength = field.ArrayLength,
                    IsStatic = field.IsStatic,
                    HasIndex = field.HasIndex,
                    IndexAllowMultiple = field.IndexAllowMultiple,
                    OffsetInComponentStorage = field.OffsetInComponentStorage,
                    SizeInComponentStorage = field.SizeInComponentStorage,
                };

                a.Add(f);
                fieldList.Add(f);
            }
        }

        var chunkId = SystemCrud.Create(_componentsTable, ref comp, EpochManager, cs);
        cs.SaveChanges();
        return (chunkId, comp, fieldList.ToArray());
    }

    /// <summary>
    /// Persists schema changes (renames, new fields, removed fields) for a component after the resolver detects that the runtime field layout differs from
    /// the persisted FieldR1 entries. When a migration has occurred, also updates the segment SPIs and component sizes.
    /// </summary>
    /// <param name="chunkId">Chunk ID of the existing ComponentR1 entity.</param>
    /// <param name="definition">The resolved component definition with updated field IDs and names.</param>
    /// <param name="migrationResult">Optional migration result containing new segment SPIs.</param>
    private void PersistSchemaChanges(int chunkId, DBComponentDefinition definition, MigrationResult? migrationResult = null)
    {
        var cs = MMF.CreateChangeSet();

        SystemCrud.Read(_componentsTable, chunkId, out ComponentR1 comp, EpochManager);

        // Reset the Fields collection — we rebuild it entirely with the resolved definitions.
        comp.Fields = default;

        var nonStaticCount = 0;
        foreach (var kvp in definition.FieldsByName)
        {
            if (!kvp.Value.IsStatic)
            {
                nonStaticCount++;
            }
        }

        comp.SchemaRevision = definition.Revision;
        comp.FieldCount = nonStaticCount;

        // Update SPIs and sizes if migration ran
        if (migrationResult.HasValue)
        {
            comp.ComponentSPI = migrationResult.Value.NewComponentSPI;
            comp.VersionSPI = migrationResult.Value.NewVersionSPI;
            comp.CompSize = definition.ComponentStorageSize;
            comp.CompOverhead = definition.ComponentStorageOverhead;
        }

        {
            using var guard = EpochGuard.Enter(EpochManager);
            var vsbs = GetComponentCollectionVSBS<FieldR1>();
            using var a = new ComponentCollectionAccessor<FieldR1>(cs, vsbs, ref comp.Fields);

            foreach (var kvp in definition.FieldsByName)
            {
                var field = kvp.Value;
                var f = new FieldR1
                {
                    Name = (String64)field.Name,
                    FieldId = field.FieldId,
                    Type = field.Type,
                    UnderlyingType = field.UnderlyingType,
                    ArrayLength = field.ArrayLength,
                    IsStatic = field.IsStatic,
                    HasIndex = field.HasIndex,
                    IndexAllowMultiple = field.IndexAllowMultiple,
                    OffsetInComponentStorage = field.OffsetInComponentStorage,
                    SizeInComponentStorage = field.SizeInComponentStorage,
                };

                a.Add(f);
            }
        }

        SystemCrud.Update(_componentsTable, chunkId, ref comp, EpochManager, cs);
        cs.SaveChanges();
    }

    /// <summary>
    /// Restores the system schema (FieldR1 and ComponentR1 tables) from persisted SPIs on database reopen.
    /// Populates <see cref="_persistedComponents"/> so that subsequent <see cref="RegisterComponentFromAccessor{T}"/>
    /// / <see cref="RegisterComponentByType"/> calls load existing segments instead of allocating fresh ones.
    /// </summary>
    private void LoadSystemSchemaR1()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var unused = guard.Epoch;

        // Read bootstrap dictionary (already loaded by MMF.OnFileLoading)
        var bootstrap = MMF.Bootstrap;

        // Restore the TSN counter so MVCC visibility works for entities from previous sessions
        var nextFreeTSN = bootstrap.GetLong(BK_NextFreeTSN);
        if (nextFreeTSN > 0)
        {
            TransactionChain.SetNextFreeId(nextFreeTSN);
        }

        _lastTickFenceLSN = bootstrap.GetLong(BK_LastTickFenceLSN);

        if (bootstrap.GetInt(BK_SystemSchemaRevision) == 0)
        {
            return;
        }

        // Register system type definitions in DBD
        DBD.CreateFromAccessor<ComponentR1>();
        DBD.CreateFromAccessor<SchemaHistoryR1>();

        var compDef    = DBD.GetComponent(ComponentR1.SchemaName, 1);
        var historyDef = DBD.GetComponent(SchemaHistoryR1.SchemaName, 1);

        // Load system tables using SPIs from bootstrap
        var compSPIs = bootstrap.Get(BK_SysComponentR1);
        var historySPIs = bootstrap.Get(BK_SysSchemaHistory);

        _componentsTable = new ComponentTable(this, compDef, this, compSPIs.GetInt(), compSPIs.GetInt(1), compSPIs.GetInt(2), compSPIs.GetInt(3));
        _schemaHistoryTable = new ComponentTable(this, historyDef, this, historySPIs.GetInt(), historySPIs.GetInt(1), historySPIs.GetInt(2), historySPIs.GetInt(3));

        _componentTableByType.TryAdd(typeof(ComponentR1), _componentsTable);
        _componentTableByType.TryAdd(typeof(SchemaHistoryR1), _schemaHistoryTable);

        var compsWalTypeId = (ushort)_componentsTable.ComponentSegment.RootPageIndex;
        _componentsTable.WalTypeId = compsWalTypeId;
        _componentTableByWalTypeId.TryAdd(compsWalTypeId, _componentsTable);

        var historyWalTypeId = (ushort)_schemaHistoryTable.ComponentSegment.RootPageIndex;
        _schemaHistoryTable.WalTypeId = historyWalTypeId;
        _componentTableByWalTypeId.TryAdd(historyWalTypeId, _schemaHistoryTable);

        // Load the ComponentCollection segment for FieldR1
        int fieldCollectionSPI = bootstrap.GetInt(BK_CollectionFieldR1);
        if (fieldCollectionSPI != 0)
        {
            unsafe
            {
                var stride = RoundToStandardStride(
                    Math.Max(sizeof(FieldR1) * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader)));
                var segment = MMF.LoadChunkBasedSegment(fieldCollectionSPI, stride);
                _componentCollectionSegmentByStride.TryAdd(stride, segment);
            }
        }

        // Read all ComponentR1 entries by scanning ComponentSegment allocated chunks
        _persistedComponents = new Dictionary<string, (int, ComponentR1)>();
        _persistedFieldsByComponent = new Dictionary<string, FieldR1[]>();
        {
            var segment = _componentsTable.ComponentSegment;
            var capacity = segment.ChunkCapacity;
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                if (SystemCrud.Read(_componentsTable, chunkId, out ComponentR1 comp, EpochManager))
                {
                    var schemaName = comp.Name.AsString;
                    _persistedComponents[schemaName] = (chunkId, comp);
                }
            }

            // Read FieldR1 entries from each persisted component's Fields collection
            if (fieldCollectionSPI != 0)
            {
                var vsbs = GetComponentCollectionVSBS<FieldR1>();
                foreach (var kvp in _persistedComponents)
                {
                    var comp = kvp.Value.Comp;
                    if (comp.Fields._bufferId != 0)
                    {
                        var fields = new List<FieldR1>();
                        foreach (var f in vsbs.EnumerateBuffer(comp.Fields._bufferId))
                        {
                            fields.Add(f);
                        }
                        _persistedFieldsByComponent[kvp.Key] = fields.ToArray();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Persists critical engine state to disk during Dispose:
    /// 1. Flushes any dirty pages left by unflushed Deferred UoWs (safety net)
    /// 2. Writes the current TSN counter to the root file header (MVCC visibility on reopen)
    /// 3. Flushes ALL changes to stable storage via SaveChanges + FlushToDisk
    /// </summary>
    private void PersistEngineState()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var epoch = guard.Epoch;

        var cs = MMF.CreateChangeSet();

        // Safety net: collect dirty pages left by unflushed Deferred UoWs and include
        // them in this ChangeSet so they are persisted during the final SaveChanges.
        var dirtyPages = MMF.CollectDirtyMemPageIndices();
        if (dirtyPages.Length > 0)
        {
            Logger?.LogWarning("Engine shutdown: flushing {Count} dirty page(s) to disk", dirtyPages.Length);
            foreach (var idx in dirtyPages)
            {
                cs.AddByMemPageIndex(idx);
            }
        }

        // Write TSN counter to root file header
        MMF.RequestPageEpoch(0, epoch, out var memPageIdx);
        var latched = MMF.TryLatchPageExclusive(memPageIdx);
        Debug.Assert(latched, "TryLatchPageExclusive failed on root page during engine state save");
        var unused = MMF.GetPage(memPageIdx);

        cs.AddByMemPageIndex(memPageIdx);

        // Update bootstrap with current TSN and tick fence LSN
        MMF.Bootstrap.SetLong(BK_NextFreeTSN, TransactionChain.NextFreeId);
        if (_lastTickFenceLSN > 0)
        {
            MMF.Bootstrap.SetLong(BK_LastTickFenceLSN, _lastTickFenceLSN);
        }
        MMF.SaveBootstrap(cs);

        MMF.UnlatchPageExclusive(memPageIdx);

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    /// <summary>
    /// Persists EntityMap segment root page indexes and NextEntityKey counters for all archetypes.
    /// Called during engine dispose so that reopen can load EntityMaps directly (O(1)) instead of
    /// rebuilding from PK index scans.
    /// </summary>
    private void PersistArchetypeState()
    {
        var archetypesTable = GetComponentTable<ArchetypeR1>();
        if (archetypesTable == null || _archetypeStates == null || _persistedArchetypes == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var cs = MMF.CreateChangeSet();
        bool anyUpdated = false;

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (meta.ArchetypeId >= _archetypeStates.Length)
            {
                continue;
            }

            var state = _archetypeStates[meta.ArchetypeId];
            if (state?.EntityMap == null)
            {
                continue;
            }

            if (!_persistedArchetypes.TryGetValue(meta.ArchetypeId, out var persisted))
            {
                continue;
            }

            var arch = persisted.Arch;
            arch.EntityMapSPI = state.EntityMap.Segment.RootPageIndex;
            arch.ClusterSegmentSPI = state.ClusterState?.ClusterSegment?.RootPageIndex ?? 0;
            arch.NextEntityKey = Interlocked.Read(ref state.NextEntityKey);

            // EntityMap's meta chunk tracks the total entry count, but FlushMetaToChunk is otherwise only called during a bucket split. For append-only
            // workloads that never split (e.g. a session with fewer entries than n0 × 0.75 × bucketCapacity), the persisted meta count stays at 0 from
            // Create() even though the bucket data is correct. Flush it here so the next InitializeOpen reads an accurate total without having to walk
            // the bucket chains.
            state.EntityMap.FlushMeta(cs);

            // Persist per-archetype cluster index segment SPI via bootstrap dictionary
            if (state.ClusterState?.IndexSegment != null)
            {
                MMF.Bootstrap.SetInt($"clusterindex.{meta.ArchetypeId}", state.ClusterState.IndexSegment.RootPageIndex);
            }

            // Issue #230 Phase 3 Option B: nothing about the per-cell cluster index is persisted. All cell-level state is transient per Phase 1 Q2/Q6 and
            // rebuilt from cluster data at startup by RebuildCellState + RebuildClusterAabbs.

            SystemCrud.Update(archetypesTable, persisted.ChunkId, ref arch, EpochManager, cs);
            anyUpdated = true;
        }

        if (anyUpdated)
        {
            cs.SaveChanges();
        }
    }

    /// <summary>
    /// Increments the UserSchemaVersion counter in the bootstrap dictionary.
    /// Called after any user component schema change is persisted.
    /// </summary>
    private void IncrementUserSchemaVersion()
    {
        var currentVersion = MMF.Bootstrap.GetInt(BK_UserSchemaVersion);
        MMF.Bootstrap.SetInt(BK_UserSchemaVersion, currentVersion + 1);
        MMF.SaveBootstrap();
    }

    /// <summary>
    /// Records a schema change in the <see cref="SchemaHistoryR1"/> audit trail.
    /// Called during <see cref="RegisterComponentFromAccessor{T}"/> / <see cref="RegisterComponentByType"/> after schema persistence.
    /// </summary>
    private void RecordSchemaHistory(string componentName, SchemaDiff diff, MigrationResult? migrationResult, int fromRevision, int toRevision)
    {
        if (_schemaHistoryTable == null)
        {
            return;
        }

        var added = 0;
        var removed = 0;
        var typeChanged = 0;

        if (diff != null)
        {
            foreach (var fc in diff.FieldChanges)
            {
                switch (fc.Kind)
                {
                    case FieldChangeKind.Added:
                        added++;
                        break;
                    case FieldChangeKind.Removed:
                        removed++;
                        break;
                    case FieldChangeKind.TypeChanged:
                    case FieldChangeKind.TypeWidened:
                        typeChanged++;
                        break;
                }
            }
        }

        var kind = diff != null && diff.HasBreakingChanges ? SchemaChangeKind.Migration : SchemaChangeKind.Compatible;

        var entry = new SchemaHistoryR1
        {
            Timestamp = DateTime.UtcNow.Ticks,
            ComponentName = (String64)componentName,
            FromRevision = fromRevision,
            ToRevision = toRevision,
            FieldsAdded = added,
            FieldsRemoved = removed,
            FieldsTypeChanged = typeChanged,
            EntitiesMigrated = migrationResult?.EntitiesMigrated ?? 0,
            ElapsedMilliseconds = (int)(migrationResult?.ElapsedMs ?? 0),
            Kind = kind,
        };

        var cs = MMF.CreateChangeSet();
        SystemCrud.Create(_schemaHistoryTable, ref entry, EpochManager, cs);
        cs.SaveChanges();
    }

    /// <summary>
    /// Returns all schema history entries from the audit trail, ordered by primary key (chronological).
    /// </summary>
    [PublicAPI]
    public IReadOnlyList<SchemaHistoryR1> GetSchemaHistory()
    {
        if (_schemaHistoryTable == null)
        {
            return [];
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var segment = _schemaHistoryTable.ComponentSegment;
        var capacity = segment.ChunkCapacity;
        var result = new List<SchemaHistoryR1>();

        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            if (SystemCrud.Read(_schemaHistoryTable, chunkId, out SchemaHistoryR1 entry, EpochManager))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Non-generic entry point for registering a component when the type is only known at runtime — e.g. types discovered via reflection from a plugin or
    /// user-supplied schema DLL, as the Workbench does when loading <c>*.schema.dll</c> into a collectible AssemblyLoadContext.
    /// </summary>
    /// <remarks>
    /// Internally invokes the generic <see cref="RegisterComponentFromAccessor{T}"/> via <see cref="MethodInfo.MakeGenericMethod"/>.
    /// Any <see cref="TargetInvocationException"/> raised by reflection is unwrapped with <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>
    /// so callers observe the real underlying exception — e.g. <c>SchemaValidationException</c>, <c>SchemaDowngradeException</c>,
    /// <c>SchemaMigrationException</c> — with its original stack trace preserved.
    /// </remarks>
    /// <param name="componentType">
    /// A closed unmanaged value type tagged with <c>[Component]</c>. The <see langword="unmanaged"/> constraint from the generic overload is verified at
    /// runtime by the CLR when the method is specialized; non-blittable or reference-type inputs will throw from deep inside <see cref="MethodInfo.MakeGenericMethod"/>.
    /// </param>
    /// <param name="changeSet">Optional transactional change set. See <see cref="RegisterComponentFromAccessor{T}"/>.</param>
    /// <param name="schemaValidation">Schema validation policy (default: <see cref="SchemaValidationMode.Enforce"/>).</param>
    /// <param name="storageModeOverride">Optional override for the storage mode declared by the component.</param>
    /// <returns>Forwarded from <see cref="RegisterComponentFromAccessor{T}"/> — <see langword="true"/> on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="componentType"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="componentType"/> is not a closed value type.</exception>
    /// <seealso cref="RegisterComponentFromAccessor{T}"/>
    public bool RegisterComponentByType(Type componentType, ChangeSet changeSet = null, SchemaValidationMode schemaValidation = SchemaValidationMode.Enforce,
        StorageMode? storageModeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (!componentType.IsValueType || componentType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Component type must be a closed unmanaged value type: {componentType.FullName}", nameof(componentType));
        }

        var method = typeof(DatabaseEngine).GetMethod(nameof(RegisterComponentFromAccessor), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{nameof(RegisterComponentFromAccessor)} not found on DatabaseEngine.");
        var generic = method.MakeGenericMethod(componentType);
        try
        {
            return (bool)generic.Invoke(this, [changeSet, schemaValidation, storageModeOverride])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Re-throw the underlying exception with its original stack trace so callers see
            // SchemaValidationException / SchemaDowngradeException directly, not wrapped.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    public bool RegisterComponentFromAccessor<T>(ChangeSet changeSet = null, SchemaValidationMode schemaValidation = SchemaValidationMode.Enforce,
        StorageMode? storageModeOverride = null) where T : unmanaged
    {
        // Look up persisted fields for the resolver (keyed by component schema name)
        FieldIdResolver resolver = null;
        var componentAttr = typeof(T).GetCustomAttribute<ComponentAttribute>();
        var schemaName = componentAttr?.Name ?? typeof(T).Name;

        FieldR1[] persistedFields = null;
        if (_persistedFieldsByComponent != null && _persistedFieldsByComponent.TryGetValue(schemaName, out persistedFields))
        {
            resolver = new FieldIdResolver(persistedFields);
        }

        var definition = DBD.CreateFromAccessor<T>(resolver);
        if (definition == null)
        {
            return false;
        }

        var storageMode = storageModeOverride ?? definition.StorageMode;

        // Apply storage mode override to definition so computed properties (EntityPKOverheadSize, etc.) reflect the override.
        // NOTE: this must happen BEFORE ComponentTable construction so the segment layout includes the correct overhead.
        if (storageModeOverride.HasValue && definition.StorageMode != storageModeOverride.Value)
        {
            definition.StorageMode = storageModeOverride.Value;
        }

        ComponentTable componentTable;

        if (_persistedComponents != null && _persistedComponents.TryGetValue(schemaName, out var persisted))
        {
            // Schema validation: compare persisted vs runtime before loading data
            SchemaDiff diff = null;
            MigrationResult? migrationResult = null;
            HashSet<int> newIndexFieldIds = null;

            if (persistedFields != null)
            {
                // Guard: refuse to open a database written by a newer application version
                var targetRevision = componentAttr?.Revision ?? 1;
                var persistedRevision = persisted.Comp.SchemaRevision;
                if (persistedRevision > targetRevision)
                {
                    throw new SchemaDowngradeException(schemaName, persistedRevision, targetRevision);
                }

                diff = SchemaValidator.ComputeDiff(schemaName, persistedFields, persisted.Comp, definition,
                    resolver.Renames ?? (IReadOnlyList<(string, string, int)>)[]);

                if (diff.HasBreakingChanges && schemaValidation != SchemaValidationMode.Skip)
                {

                    // Backward compat: databases created before schema migration have SchemaRevision=0.
                    // Try the persisted value first, then fall back to searching the registry.
                    var chain = _migrationRegistry?.GetChain(schemaName, persistedRevision, targetRevision);
                    if (chain == null && persistedRevision == 0 && _migrationRegistry != null)
                    {
                        // Legacy database: SchemaRevision was auto-incremented, not attribute-based.
                        // Scan for a viable chain by trying common starting revisions.
                        chain = _migrationRegistry.GetChain(schemaName, 1, targetRevision);
                    }

                    if (chain == null)
                    {
                        throw new SchemaValidationException(diff);
                    }

                    Logger?.LogInformation(
                        "Breaking schema change for '{Name}': {Summary}. Migration chain registered ({StepCount} step(s))",
                        schemaName, diff.Summary, chain.Value.StepCount);

                    migrationResult = SchemaEvolutionEngine.MigrateWithFunction(
                        MMF, EpochManager, diff, persistedFields, persisted.Comp, definition, chain.Value, Logger, RaiseMigrationProgress);
                }

                if (!diff.IsIdentical)
                {
                    switch (diff.Level)
                    {
                        case CompatibilityLevel.CompatibleWidening:
                            Logger?.LogWarning("Schema widening for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                        case CompatibilityLevel.Breaking:
                            // Already handled above via migration function
                            break;
                        case >= CompatibilityLevel.Compatible:
                            Logger?.LogInformation("Schema evolution for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                        case CompatibilityLevel.InformationOnly:
                            Logger?.LogInformation("Schema renames for '{Name}': {Summary}", schemaName, diff.Summary);
                            break;
                    }

                    // For compatible changes (non-breaking), use the field-map migration path
                    if (!diff.HasBreakingChanges)
                    {
                        var oldStride = persisted.Comp.CompSize + persisted.Comp.CompOverhead;
                        var newStride = definition.ComponentStorageTotalSize;

                        if (SchemaEvolutionEngine.NeedsMigration(diff, oldStride, newStride))
                        {
                            migrationResult = SchemaEvolutionEngine.Migrate(MMF, EpochManager, diff, persistedFields, persisted.Comp, definition, Logger,
                                RaiseMigrationProgress);
                        }
                    }

                    newIndexFieldIds = SchemaEvolutionEngine.GetNewIndexFieldIds(diff);
                }
            }

            // Transient: data doesn't survive restart — create fresh empty table, skip schema evolution
            var persistedModeByte = persisted.Comp.StorageMode;
            if (persistedModeByte > (byte)StorageMode.Transient)
            {
                throw new InvalidOperationException(
                    $"Invalid StorageMode byte {persistedModeByte} for component '{schemaName}'. Expected 0 (Versioned), 1 (SingleVersion), or 2 (Transient).");
            }
            var persistedMode = (StorageMode)persistedModeByte;
            if (persistedMode == StorageMode.Transient)
            {
                componentTable = new ComponentTable(this, definition, this, StorageMode.Transient);
            }
            else
            {
                // Load path: use migration constructor if migration ran, otherwise standard load from persisted SPIs
                var migrationChangeSet = (migrationResult.HasValue || newIndexFieldIds != null) ? MMF.CreateChangeSet() : null;

                if (migrationResult.HasValue)
                {
                    componentTable = new ComponentTable(this, definition, this, migrationResult.Value.NewComponentSegment, migrationResult.Value.NewRevisionSegment,
                        persisted.Comp.DefaultIndexSPI, persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI, newIndexFieldIds: newIndexFieldIds,
                        changeSet: migrationChangeSet);
                }
                else
                {
                    componentTable = new ComponentTable(this, definition, this, persisted.Comp.ComponentSPI, persisted.Comp.VersionSPI, persisted.Comp.DefaultIndexSPI,
                        persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI, storageMode: persistedMode, newIndexFieldIds: newIndexFieldIds,
                        changeSet: migrationChangeSet);
                }

                // Load spatial index from bootstrap if present
                if (definition.SpatialField != null)
                {
                    LoadSpatialBootstrap(componentTable);
                }

                // Populate newly created indexes by scanning entities
                if (newIndexFieldIds != null)
                {
                    componentTable.PopulateNewIndexes(newIndexFieldIds, migrationChangeSet);
                    migrationChangeSet?.SaveChanges();
                    MMF.FlushToDisk();
                }
            }

            // Track migrated components so InitializeArchetypes can invalidate stale EntityMaps
            if (migrationResult.HasValue)
            {
                _migratedComponents ??= [];
                _migratedComponents.Add(schemaName);
            }

            // Persist schema changes if the resolver detected changes or migration ran
            if ((resolver != null && resolver.HasChanges) || migrationResult.HasValue)
            {
                PersistSchemaChanges(persisted.ChunkId, definition, migrationResult);
                IncrementUserSchemaVersion();

                // Record in schema history audit trail
                RecordSchemaHistory(schemaName, diff, migrationResult, persisted.Comp.SchemaRevision, definition.Revision);
            }
        }
        else
        {
            // Create path: use the provided ChangeSet, or create a new one for standalone registration
            var cs = changeSet ?? MMF.CreateChangeSet();
            componentTable = new ComponentTable(this, definition, this, storageMode, changeSet: cs);

            // Save metadata for future reload (skip during initial CreateSystemSchemaR1)
            if (_componentsTable != null)
            {
                var saved = SaveInSystemSchema(componentTable);

                // Persist spatial index segment SPIs in bootstrap (segment root pages are immutable after creation)
                if (componentTable.SpatialIndex != null)
                {
                    SaveSpatialBootstrap(componentTable);
                }

                cs.SaveChanges();
                MMF.FlushToDisk();

                // Populate persisted dictionaries so schema commands work on first-run databases
                _persistedComponents ??= new Dictionary<string, (int, ComponentR1)>();
                _persistedFieldsByComponent ??= new Dictionary<string, FieldR1[]>();
                _persistedComponents[schemaName] = (saved.ChunkId, saved.Comp);
                _persistedFieldsByComponent[schemaName] = saved.Fields;
            }
        }

        _componentTableByType.TryAdd(typeof(T), componentTable);

        // Assign a stable WAL type ID derived from the component segment's persistent root page index.
        // Transient components have no persistent segments and no WAL involvement.
        if (storageMode != StorageMode.Transient)
        {
            var walTypeId = (ushort)componentTable.ComponentSegment.RootPageIndex;
            componentTable.WalTypeId = walTypeId;
            _componentTableByWalTypeId.TryAdd(walTypeId, componentTable);
        }

        return true;
    }

    /// <summary>
    /// Registers a strongly-typed migration function that transforms component data from <typeparamref name="TOld"/> to <typeparamref name="TNew"/>.
    /// Both types must have [Component] attributes with the same Name but different Revisions.
    /// Must be called before <see cref="RegisterComponentFromAccessor{T}"/> / <see cref="RegisterComponentByType"/> for the target component.
    /// </summary>
    public void RegisterMigration<TOld, TNew>(MigrationFunc<TOld, TNew> func) where TOld : unmanaged where TNew : unmanaged
    {
        _migrationRegistry ??= new MigrationRegistry();
        _migrationRegistry.Register(func);
    }

    /// <summary>
    /// Registers a byte-level migration function for scenarios where the old struct type is no longer available in code.
    /// Must be called before <see cref="RegisterComponentFromAccessor{T}"/> / <see cref="RegisterComponentByType"/> for the target component.
    /// </summary>
    public void RegisterByteMigration(string componentName, int fromRevision, int toRevision, int oldSize, int newSize, ByteMigrationFunc func)
    {
        _migrationRegistry ??= new MigrationRegistry();
        _migrationRegistry.RegisterByte(componentName, fromRevision, toRevision, oldSize, newSize, func);
    }

    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);

    /// <summary>
    /// Looks up a <see cref="ComponentTable"/> by its WAL type ID (derived from <see cref="ChunkBasedSegment<PersistentStore>.RootPageIndex"/>).
    /// Returns null if the type ID is unknown.
    /// </summary>
    internal ComponentTable GetComponentTableByWalTypeId(ushort id) => _componentTableByWalTypeId.GetValueOrDefault(id);

    /// <summary>
    /// Find a ComponentTable by the component's schema name (from [Component] attribute).
    /// Used as a fallback when the CLR type doesn't match (schema evolution: V1 type → V2 table).
    /// </summary>
    internal ComponentTable FindComponentTableBySchemaName(Type compType)
    {
        var attr = compType.GetCustomAttribute<ComponentAttribute>();
        if (attr == null)
        {
            return null;
        }
        foreach (var ct in _componentTableByType.Values)
        {
            if (ct.Definition.Name == attr.Name)
            {
                return ct;
            }
        }
        return null;
    }

    /// <summary>
    /// Initialize ECS archetype storage. For each registered archetype, allocates a per-archetype RawValueHashMap and connects component slots to their
    /// ComponentTables. Must be called after all components are registered.
    /// </summary>
    public void InitializeArchetypes()
    {
        ArchetypeRegistry.Freeze();

        // Construct the engine-wide spatial grid if one was configured. A grid is only required when at least one cluster-eligible archetype has a spatial
        // component (checked per-archetype below).
        if (_pendingGridConfig.HasValue)
        {
            _spatialGrid = new SpatialGrid(_pendingGridConfig.Value);
            _pendingGridConfig = null;
        }

        // Ensure ArchetypeR1 is registered in this session. On a new database CreateSystemSchemaR1 already registered it; on reopen LoadSystemSchemaR1 stops
        // after ComponentR1 + SchemaHistoryR1 (ArchetypeR1 is treated as a regular user-visible system component), so we pick it up here via the standard
        // registration path — which reuses the persisted SPIs via _persistedComponents.
        if (GetComponentTable<ArchetypeR1>() == null)
        {
            RegisterComponentFromAccessor<ArchetypeR1>();
        }

        // Load persisted archetype schemas for validation
        _persistedArchetypes ??= new Dictionary<ushort, (int, ArchetypeR1)>();
        LoadPersistedArchetypes();

        // Allocate per-engine state array indexed by ArchetypeId
        _archetypeStates = new ArchetypeEngineState[ArchetypeRegistry.MaxArchetypeId + 1];

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            // Connect slots to ComponentTables — skip archetypes with unregistered component types
            if (meta._slotToComponentType == null || meta.ComponentCount == 0)
            {
                continue;
            }

            var slotToTable = new ComponentTable[meta.ComponentCount];
            bool allComponentsRegistered = true;
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var compType = meta._slotToComponentType[slot];
                if (compType == null)
                {
                    allComponentsRegistered = false;
                    break;
                }

                // Schema evolution fallback: the CLR type may be from an older version (V1)
                // while the registered ComponentTable uses the newer version (V2).
                // Fall back to schema-name matching since both versions share the same name.
                var table = GetComponentTable(compType) ?? FindComponentTableBySchemaName(compType);
                if (table == null)
                {
                    allComponentsRegistered = false;
                    break;
                }
                slotToTable[slot] = table;
            }

            if (!allComponentsRegistered)
            {
                continue;
            }

            // Schema validation: compare runtime archetype against persisted schema
            ValidateArchetypeSchema(meta);

            // ═══════════════════════════════════════════════════════════════════════
            // Cluster storage eligibility: SV, Versioned, and Transient all allowed.
            // Versioned stores HEAD in cluster slot, chain separate. Transient stores component data in a parallel CBS<TransientStore> segment (zero page cache).
            // Pure-Versioned archetypes stay on legacy path (must have ≥1 SV or Transient).
            // ═══════════════════════════════════════════════════════════════════════
            bool isClusterEligible = true;
            bool hasClusterIndexableFields = false;  // Non-Transient indexed fields (for per-archetype cluster B+Trees)
            bool hasSpatialField = false;
            bool hasSvSlot = false;
            bool hasTransientSlot = false;
            ushort versionedSlotMask = 0;
            ushort transientSlotMask = 0;
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = slotToTable[slot];
                if (table.StorageMode == StorageMode.Versioned)
                {
                    versionedSlotMask |= (ushort)(1 << slot);
                }
                else if (table.StorageMode == StorageMode.SingleVersion)
                {
                    hasSvSlot = true;
                }
                else if (table.StorageMode == StorageMode.Transient)
                {
                    transientSlotMask |= (ushort)(1 << slot);
                    hasTransientSlot = true;
                    // Transient components with indexed fields stay on legacy per-entity path.
                    // Reason: cluster Write<T> returns a ref into the SoA slot — there's no hook to update TransientIndex.Move(oldKey→newKey) after the
                    // caller modifies the value. The shadow/tick-fence mechanism used by SV indexed fields reads from ClusterSegment (PersistentStore),
                    // not TransientSegment.
                    // Since Transient indexed fields are rare (most Transient components are unindexed runtime state), this exclusion has minimal impact.
                    if (table.IndexedFieldInfos != null && table.IndexedFieldInfos.Length > 0)
                    {
                        isClusterEligible = false;
                        break;
                    }
                }
                if (table.SpatialIndex != null)
                {
                    hasSpatialField = true;
                }
                if (table.IndexedFieldInfos != null && table.IndexedFieldInfos.Length > 0 && table.StorageMode != StorageMode.Transient)
                {
                    hasClusterIndexableFields = true;
                }
            }

            // Require at least one SV or Transient slot. Pure-Versioned stays on legacy path.
            if (isClusterEligible && !hasSvSlot && !hasTransientSlot)
            {
                isClusterEligible = false;
            }

            meta.IsClusterEligible = isClusterEligible;
            meta.HasClusterIndexes = isClusterEligible && hasClusterIndexableFields;
            meta.HasClusterSpatial = isClusterEligible && hasSpatialField;
            meta.VersionedSlotMask = isClusterEligible ? versionedSlotMask : (ushort)0;
            meta.VersionedSlotCount = isClusterEligible ? (byte)BitOperations.PopCount(versionedSlotMask) : (byte)0;
            meta.TransientSlotMask = isClusterEligible ? transientSlotMask : (ushort)0;
            meta.TransientSlotCount = isClusterEligible ? (byte)BitOperations.PopCount(transientSlotMask) : (byte)0;

            if (isClusterEligible)
            {
                // Compute component data sizes (pure struct size, no overhead)
                var componentSizes = new int[meta.ComponentCount];
                int multipleIndexedFieldCount = 0;
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = slotToTable[slot];
                    componentSizes[slot] = table.Definition.ComponentStorageSize;
                    // Count AllowMultiple indexed fields for the cluster tail elementId storage. Only non-Transient slots participate in cluster B+Tree
                    // indexing (Transient indexed fields stay on the legacy per-entity path — see the eligibility check above). Mirror that gate here so the
                    // tail is sized correctly.
                    if (table.StorageMode != StorageMode.Transient && table.IndexedFieldInfos != null)
                    {
                        for (int fi = 0; fi < table.IndexedFieldInfos.Length; fi++)
                        {
                            if (table.IndexedFieldInfos[fi].AllowMultiple)
                            {
                                multipleIndexedFieldCount++;
                            }
                        }
                    }
                }
                meta.ClusterLayout = ArchetypeClusterInfo.Compute(meta.ComponentCount, componentSizes, multipleIndexedFieldCount,
                    versionedSlotMask, transientSlotMask);

                // Override entity record size: base 19 bytes + 4 bytes per Versioned component slot
                meta._entityRecordSize = ClusterEntityRecordAccessor.RecordSize(meta.VersionedSlotCount);
            }

            // Allocate or reload per-archetype entity storage (RawValueHashMap) on THIS engine's MMF
            int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(meta._entityRecordSize);

            // Skip O(1) EntityMap reopen if any of this archetype's component tables underwent migration.
            // Migration creates new segments with preserved chunk IDs, but the persisted EntityMap
            // points to old chunk IDs that may not be valid in the context of the new revision chain layout.
            bool hasMigratedSlot = false;
            if (_migratedComponents != null)
            {
                for (int slot = 0; slot < meta.ComponentCount && !hasMigratedSlot; slot++)
                {
                    hasMigratedSlot = _migratedComponents.Contains(slotToTable[slot].Definition.Name);
                }
            }

            bool isFreshAllocation;
            if (!hasMigratedSlot && _persistedArchetypes.TryGetValue(meta.ArchetypeId, out var persisted) && persisted.Arch.EntityMapSPI > 0
                && MMF.TryLoadChunkBasedSegment(persisted.Arch.EntityMapSPI, stride, out var loadedSegment))
            {
                // Reload existing EntityMap from persisted segment (O(1) reopen)
                var em = RawValuePagedHashMap<long, PersistentStore>.Open(loadedSegment, 256, meta._entityRecordSize);
                _archetypeStates[meta.ArchetypeId] = new ArchetypeEngineState
                {
                    SlotToComponentTable = slotToTable,
                    EntityMap = em,
                    NextEntityKey = persisted.Arch.NextEntityKey,
                };
                isFreshAllocation = false;
            }
            else
            {
                // Fresh allocation (new archetype or legacy database without SPI)
                // n0=256 avoids excessive linear hash splits during bulk entity insertion
                // (256 buckets × ~9 entries/bucket × 0.75 load = ~1728 entities before first split)
                var segment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, stride);
                _archetypeStates[meta.ArchetypeId] = new ArchetypeEngineState
                {
                    SlotToComponentTable = slotToTable,
                    EntityMap = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, meta._entityRecordSize),
                    NextEntityKey = 0,
                };
                isFreshAllocation = true;
            }

            // Create or reload ClusterState for cluster-eligible archetypes.
            if (isClusterEligible)
            {
                bool isPureTransient = transientSlotMask != 0 && !hasSvSlot && versionedSlotMask == 0;

                if (isFreshAllocation)
                {
                    // PersistentStore segment for SV+V components (null for pure-Transient)
                    ChunkBasedSegment<PersistentStore> clusterSegment = null;
                    if (!isPureTransient)
                    {
                        clusterSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 4, meta.ClusterLayout.ClusterStride);
                        if (clusterSegment == null)
                        {
                            throw new InvalidOperationException(
                                $"Failed to allocate cluster segment for archetype {meta.ArchetypeType?.Name} (Id={meta.ArchetypeId}, Stride={meta.ClusterLayout.ClusterStride})");
                        }
                    }

                    // TransientStore segment for Transient components (null if no Transient)
                    ChunkBasedSegment<TransientStore> transientClusterSegment = null;
                    TransientStore? transientClusterStore = null;
                    if (transientSlotMask != 0)
                    {
                        CreateTransientClusterSegment(meta.ClusterLayout.ClusterStride, out transientClusterStore, out transientClusterSegment);
                    }

                    _archetypeStates[meta.ArchetypeId].ClusterState =
                        ArchetypeClusterState.Create(meta.ClusterLayout, clusterSegment, transientClusterSegment, transientClusterStore);
                }
                else if (_persistedArchetypes.TryGetValue(meta.ArchetypeId, out var clusterPersisted)
                         && clusterPersisted.Arch.ClusterSegmentSPI > 0)
                {
                    ChunkBasedSegment<PersistentStore> loadedCluster = null;
                    bool loaded = !isPureTransient && MMF.TryLoadChunkBasedSegment(
                        clusterPersisted.Arch.ClusterSegmentSPI, meta.ClusterLayout.ClusterStride, out loadedCluster);

                    // TransientStore segment always created fresh on reopen (Transient data doesn't survive restart)
                    ChunkBasedSegment<TransientStore> transientClusterSegment = default;
                    TransientStore? transientClusterStore = null;
                    if (transientSlotMask != 0)
                    {
                        CreateTransientClusterSegment(meta.ClusterLayout.ClusterStride, out transientClusterStore, out transientClusterSegment);
                    }

                    if (loaded)
                    {
                        using var clusterEpoch = EpochGuard.Enter(EpochManager);
                        var clusterState = ArchetypeClusterState.CreateFromExisting(meta.ClusterLayout, loadedCluster, transientClusterSegment, transientClusterStore);
                        _archetypeStates[meta.ArchetypeId].ClusterState = clusterState;

                        // Sync TransientSegment chunk IDs with PersistentStore's active clusters
                        if (transientSlotMask != 0 && clusterState.ActiveClusterCount > 0)
                        {
                            SyncTransientSegmentToActive(clusterState);
                        }
                    }
                    else if (!isPureTransient)
                    {
                        var fallbackSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, meta.ClusterLayout.ClusterStride);
                        _archetypeStates[meta.ArchetypeId].ClusterState =
                            ArchetypeClusterState.Create(meta.ClusterLayout, fallbackSegment, transientClusterSegment, transientClusterStore);
                    }
                    else
                    {
                        // Pure-Transient reopen: no persisted data, create fresh
                        _archetypeStates[meta.ArchetypeId].ClusterState =
                            ArchetypeClusterState.Create(meta.ClusterLayout, null, transientClusterSegment, transientClusterStore);
                    }
                }

                // Initialize per-archetype B+Tree indexes for cluster archetypes with indexed fields.
                if (meta.HasClusterIndexes)
                {
                    var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
                    var changeSet = MMF.CreateChangeSet();
                    try
                    {
                        // Try to load persisted per-archetype index segment
                        bool loadIndexes = false;
                        ChunkBasedSegment<PersistentStore> indexSegment;
                        string indexKey = $"clusterindex.{meta.ArchetypeId}";
                        int indexSPI = !isFreshAllocation ? MMF.Bootstrap.GetInt(indexKey) : 0;
                        if (indexSPI > 0 && MMF.TryLoadChunkBasedSegment(indexSPI, 256 /* sizeof(Index64Chunk) */, out var loadedIdx))
                        {
                            indexSegment = loadedIdx;
                            loadIndexes = true;
                        }
                        else
                        {
                            indexSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, 256 /* sizeof(Index64Chunk) */);
                        }

                        clusterState.InitializeIndexes(slotToTable, indexSegment, loadIndexes, changeSet);

                        // If fresh indexes on a reopened database with existing cluster data, rebuild from scan
                        if (!loadIndexes && !isFreshAllocation && clusterState.ActiveClusterCount > 0)
                        {
                            using var idxEpoch = EpochGuard.Enter(EpochManager);
                            clusterState.RebuildIndexesFromData(changeSet);
                        }
                    }
                    finally
                    {
                        changeSet.SaveChanges();
                    }
                }

                // Initialize per-archetype spatial state for cluster archetypes with spatial fields.
                if (meta.HasClusterSpatial)
                {
                    // Issue #230 Phase 3 Option B: ConfigureSpatialGrid() is REQUIRED for cluster spatial archetypes. The pre-Option-B fallback to the legacy
                    // per-entity R-Tree is gone; the per-cell cluster index is the single source of truth. Surface misconfiguration at engine startup rather
                    // than at the first spawn, when the user can still do something about it.
                    if (_spatialGrid == null)
                    {
                        throw new InvalidOperationException(
                            $"Archetype '{meta.ArchetypeType?.Name ?? meta.ArchetypeId.ToString()}' declares a [SpatialIndex] field and is cluster-eligible, " +
                            $"but no SpatialGrid was configured. After issue #230 Phase 3 Option B, cluster spatial archetypes require ConfigureSpatialGrid() " +
                            $"to be called during DatabaseEngine startup (before InitializeArchetypes). Call it during startup, or remove the [SpatialIndex] " +
                            $"attribute from the archetype field.");
                    }
                    {
                        for (int slot = 0; slot < meta.ComponentCount; slot++)
                        {
                            var spatialTable = slotToTable[slot];
                            if (spatialTable.SpatialIndex != null)
                            {
                                SpatialGrid.ValidateSupportedFieldType(spatialTable.SpatialIndex.FieldInfo.FieldType,
                                    meta.ArchetypeType?.Name ?? meta.ArchetypeId.ToString());
                            }
                        }

                        // Issue #229 Q10: the pre-Q10 "at most one spatial archetype per configured grid" gate has been removed. Each cluster-spatial
                        // archetype now owns its own per-cell CellClusterPool (allocated inside InitializeSpatial below), so N archetypes can share the
                        // same grid without colliding on cluster chunk IDs.
                    }

                    var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
                    var changeSet = MMF.CreateChangeSet();
                    try
                    {
                        // Issue #230 Phase 3 Option B: no per-archetype R-Tree + back-pointer CBS segments to allocate or load. The per-cell cluster index
                        // is transient and is rebuilt from cluster data at startup by RebuildCellState + RebuildClusterAabbs below.
                        // Issue #229 Q10: InitializeSpatial now also allocates this archetype's own CellClusterPool sized to the grid's cell count.
                        clusterState.InitializeSpatial(slotToTable, _spatialGrid, meta.ArchetypeId);

                        // Register with per-table SpatialInterestSystem for fan-out
                        for (int slot = 0; slot < meta.ComponentCount; slot++)
                        {
                            var table = slotToTable[slot];
                            if (table.SpatialIndex != null)
                            {
                                // Register cluster archetype on SpatialIndexState — interest/trigger systems
                                // access this list dynamically (they may not exist yet at init time).
                                table.SpatialIndex.RegisterClusterArchetype(clusterState);
                                break;
                            }
                        }

                        // Issue #229 Phase 1+2: rebuild cluster→cell mapping from persisted entity positions. All cell state is transient — nothing about
                        // the grid is persisted, so every reopen reconstructs it from the data. No-op on a fresh database.
                        // Issue #230 Phase 3 Option B: the legacy `RebuildSpatialFromData` call that used to re-insert every entity into the per-archetype
                        // R-Tree has been removed. RebuildCellState + RebuildClusterAabbs below are the single source of truth for per-cell index
                        // reconstruction on reopen. _spatialGrid is guaranteed non-null here (the grid-required gate runs before this block).
                        if (clusterState.ActiveClusterCount > 0)
                        {
                            using var cellEpoch = EpochGuard.Enter(EpochManager);
                            clusterState.RebuildCellState(_spatialGrid);

                            // Issue #230 Phase 1: rebuild per-cluster AABBs and the per-cell dynamic index from the same entity positions.
                            // Runs AFTER RebuildCellState so ClusterCellMap is populated. Transient state — not persisted, always reconstructed at startup.
                            // No-op for static-mode archetypes (Phase 1 supports dynamic mode only).
                            clusterState.RebuildClusterAabbs();
                        }
                    }
                    finally
                    {
                        changeSet.SaveChanges();
                    }
                }

                // Rebuild Versioned HEAD values in cluster slots from revision chains on reopen.
                // Crash between commit (chain WAL'd) and tick fence (cluster slot WAL'd) can leave stale HEADs.
                if (!isFreshAllocation && meta.VersionedSlotMask != 0)
                {
                    var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
                    if (clusterState != null && clusterState.ActiveClusterCount > 0)
                    {
                        var changeSet = MMF.CreateChangeSet();
                        try
                        {
                            using var vEpoch = EpochGuard.Enter(EpochManager);
                            clusterState.RebuildVersionedHeadFromChain(meta, _archetypeStates[meta.ArchetypeId], changeSet);
                        }
                        finally
                        {
                            changeSet.SaveChanges();
                        }
                    }
                }
            }
        }

        // Build and validate cascade delete graph (after all slots connected)
        ArchetypeRegistry.BuildAndValidateCascadeGraph();

        // Rebuild entity maps from persisted ComponentTable data (entities from prior database sessions)
        RebuildEntityMapsFromPersistedData();

        // Persist any new archetypes not yet in the database
        PersistNewArchetypes();
    }

    /// <summary>
    /// Rebuild per-archetype entity maps and NextEntityKey counters from persisted ComponentTable data.
    /// After a database reopen, the entity maps are empty (allocated fresh). This method scans each
    /// Versioned slot's CompRevTableSegment to discover chain heads via their EntityPK field,
    /// completely bypassing the PK B+Tree (which is no longer populated for archetype entities).
    /// </summary>
    /// <remarks>
    /// Algorithm (two-pass per slot):
    ///   Pass 1: Collect overflow chunk IDs (NextChunkId != 0) into a set.
    ///   Pass 2: Allocated chunks NOT in the overflow set are chain heads.
    ///           Read EntityPK from the header, filter by archetype, store compRevFirstChunkId.
    /// Then merge all slot maps to build EntityRecords and insert into EntityMap.
    ///
    /// SV limitation: SingleVersion components don't have CompRevTableSegment. SV slot locations
    /// can't be recovered by this scan. EntityMap persistence (the primary path) covers SV.
    /// </remarks>
    private unsafe void RebuildEntityMapsFromPersistedData()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        byte* recordBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var state = _archetypeStates[meta.ArchetypeId];
            if (state?.SlotToComponentTable == null)
            {
                continue;
            }

            // Skip archetypes that were loaded from persisted EntityMap segment (O(1) reopen path).
            // BUT: if migration invalidated the EntityMap (hasMigratedSlot → fresh allocation), the
            // EntityMap will be empty despite persisted SPI > 0. Check EntryCount to distinguish.
            if (_persistedArchetypes.TryGetValue(meta.ArchetypeId, out var p) && p.Arch.EntityMapSPI > 0
                && state.EntityMap.EntryCount > 0)
            {
                continue;
            }

            // Phase 1: Scan each Versioned slot's CompRevTableSegment to find chain heads
            // slotMaps[slot] = { EntityPK → compRevFirstChunkId }
            var slotMaps = new Dictionary<long, int>[meta.ComponentCount];
            bool anySlotPopulated = false;

            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = state.SlotToComponentTable[slot];
                if (table?.CompRevTableSegment == null || table.StorageMode != StorageMode.Versioned)
                {
                    slotMaps[slot] = null;
                    continue;
                }

                var segment = table.CompRevTableSegment;
                int capacity = segment.ChunkCapacity;
                if (capacity == 0 || segment.AllocatedChunkCount == 0)
                {
                    slotMaps[slot] = null;
                    continue;
                }

                // Pass 1: Collect overflow set (chunks that are NextChunkId of another chunk)
                var overflowSet = new HashSet<int>();
                var accessor = segment.CreateChunkAccessor();

                for (int chunkId = 0; chunkId < capacity; chunkId++)
                {
                    if (!segment.IsChunkAllocated(chunkId))
                    {
                        continue;
                    }

                    ref var hdr = ref accessor.GetChunk<CompRevStorageHeader>(chunkId, true);
                    if (hdr.NextChunkId != 0)
                    {
                        overflowSet.Add(hdr.NextChunkId);
                    }
                }

                // Pass 2: Chain heads = allocated chunks NOT in overflow set, filtered by archetype
                var chainHeads = new Dictionary<long, int>();

                for (int chunkId = 0; chunkId < capacity; chunkId++)
                {
                    if (!segment.IsChunkAllocated(chunkId))
                    {
                        continue;
                    }

                    if (overflowSet.Contains(chunkId))
                    {
                        continue; // Overflow chunk, not a chain head
                    }

                    ref var hdr = ref accessor.GetChunk<CompRevStorageHeader>(chunkId);
                    long pk = hdr.EntityPK;

                    // Filter: only this archetype's entities (PK lower 12 bits = ArchetypeId)
                    if ((pk & 0xFFF) != meta.ArchetypeId)
                    {
                        continue;
                    }

                    chainHeads[pk] = chunkId;
                }

                accessor.Dispose();
                slotMaps[slot] = chainHeads;

                if (chainHeads.Count > 0)
                {
                    anySlotPopulated = true;
                }
            }

            if (!anySlotPopulated)
            {
                continue;
            }

            // Phase 2: Build EntityRecords from collected slot data
            // Union all entity PKs across slots
            var allEntityPKs = new HashSet<long>();
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotMaps[slot] != null)
                {
                    foreach (var pk in slotMaps[slot].Keys)
                    {
                        allEntityPKs.Add(pk);
                    }
                }
            }

            long maxEntityKey = 0;
            var mapCs = MMF.CreateChangeSet();

            foreach (long pk in allEntityPKs)
            {
                long entityKey = pk >> 12;

                // Build locations for each Versioned slot
                bool allSlotsPresent = true;
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    if (slotMaps[slot] == null)
                    {
                        // SV or non-Versioned slot — can't recover location, set to 0
                        EntityRecordAccessor.SetLocation(recordBuf, slot, 0);
                        continue;
                    }

                    if (!slotMaps[slot].TryGetValue(pk, out int compRevFirstChunkId))
                    {
                        allSlotsPresent = false;
                        break;
                    }

                    EntityRecordAccessor.SetLocation(recordBuf, slot, compRevFirstChunkId);
                }

                if (!allSlotsPresent)
                {
                    continue; // Entity missing from a Versioned slot — inconsistent, skip
                }

                // Build entity record header
                ref var header = ref EntityRecordAccessor.GetHeader(recordBuf);
                header.BornTSN = 0; // Always visible (committed before checkpoint)
                header.DiedTSN = 0; // Live entity
                header.EnabledBits = (ushort)((1 << meta.ComponentCount) - 1); // All components enabled

                // Insert into entity map
                var mapAccessor = state.EntityMap.Segment.CreateChunkAccessor(mapCs);
                state.EntityMap.Insert(entityKey, recordBuf, ref mapAccessor, mapCs);
                mapAccessor.Dispose();

                if (entityKey > maxEntityKey)
                {
                    maxEntityKey = entityKey;
                }
            }

            // Resume entity key counter from max existing key
            if (maxEntityKey > 0)
            {
                state.NextEntityKey = maxEntityKey;
            }
        }
    }

    private void LoadPersistedArchetypes()
    {
        var archetypesTable = GetComponentTable<ArchetypeR1>();
        if (archetypesTable == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        var segment = archetypesTable.ComponentSegment;
        var capacity = segment.ChunkCapacity;

        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            if (SystemCrud.Read(archetypesTable, chunkId, out ArchetypeR1 arch, EpochManager))
            {
                _persistedArchetypes[arch.ArchetypeId] = (chunkId, arch);
            }
        }
    }

    private void ValidateArchetypeSchema(ArchetypeMetadata meta)
    {
        if (!_persistedArchetypes.TryGetValue(meta.ArchetypeId, out var persisted))
        {
            return; // new archetype, not persisted yet — OK
        }

        var arch = persisted.Arch;

        // Component count mismatch
        if (arch.ComponentCount != meta.ComponentCount)
        {
            throw new InvalidOperationException(
                $"Schema mismatch for archetype '{meta.ArchetypeType.Name}' (Id={meta.ArchetypeId}): " +
                $"persisted with {arch.ComponentCount} components, runtime has {meta.ComponentCount}. " +
                $"Run 'tsh migrate <dbpath>' to upgrade.");
        }

        // Revision mismatch
        if (arch.Revision != meta.Revision)
        {
            throw new InvalidOperationException(
                $"Schema mismatch for archetype '{meta.ArchetypeType.Name}' (Id={meta.ArchetypeId}): " +
                $"persisted revision {arch.Revision}, runtime revision {meta.Revision}. " +
                $"Run 'tsh migrate <dbpath>' to upgrade.");
        }

        // Component name mismatch (per slot)
        // Note: VSBS-persisted ComponentNames are validated by Persist_ComponentNames_StoredInVSBS test.
        // At schema validation time the VSBS buffer may have persisted lock state from SystemCrud writes,
        // so we rely on component count + revision checks above. The Persist_ComponentNames_StoredInVSBS
        // test validates that component names round-trip correctly through VSBS.
    }

    private void PersistNewArchetypes()
    {
        var archetypesTable = GetComponentTable<ArchetypeR1>();
        if (archetypesTable == null)
        {
            return;
        }

        var cs = MMF.CreateChangeSet();
        bool anyNew = false;

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = _archetypeStates[meta.ArchetypeId];
            if (engineState?.SlotToComponentTable == null)
            {
                continue;
            }

            if (_persistedArchetypes.ContainsKey(meta.ArchetypeId))
            {
                continue;
            }

            // Build and persist the ArchetypeR1 entity
            var arch = BuildArchetypeR1(meta);

            // Populate ComponentNames collection via VSBS
            var names = GetArchetypeComponentNames(meta);
            using (EpochGuard.Enter(EpochManager))
            {
                var vsbs = GetComponentCollectionVSBS<String64>();
                using var cca = new ComponentCollectionAccessor<String64>(cs, vsbs, ref arch.ComponentNames);
                foreach (var name in names)
                {
                    cca.Add(name);
                }
            }

            var chunkId = SystemCrud.Create(archetypesTable, ref arch, EpochManager, cs);
            _persistedArchetypes[meta.ArchetypeId] = (chunkId, arch);
            anyNew = true;
        }

        if (anyNew)
        {
            cs.SaveChanges();
        }
    }

    /// <summary>Build an ArchetypeR1 header from runtime metadata. ComponentNames must be populated separately via VSBS.</summary>
    internal static ArchetypeR1 BuildArchetypeR1(ArchetypeMetadata meta) => new()
    {
        Name = meta.ArchetypeType.Name,
        ArchetypeId = meta.ArchetypeId,
        ParentArchetypeId = meta.ParentArchetypeId,
        ComponentCount = meta.ComponentCount,
        Revision = meta.Revision,
        EntityMapSPI = 0,
        NextEntityKey = 0,
    };

    /// <summary>Get the component schema names for an archetype's slots (for validation/persistence).</summary>
    internal static String64[] GetArchetypeComponentNames(ArchetypeMetadata meta)
    {
        var names = new String64[meta.ComponentCount];
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var compType = meta._slotToComponentType[slot];
            var compAttr = compType.GetCustomAttribute<ComponentAttribute>();
            names[slot] = compAttr != null ? compAttr.Name : compType.Name;
        }
        return names;
    }

    /// <summary>
    /// Returns an <see cref="IndexRef"/> for the primary key index of component <typeparamref name="T"/>.
    /// Resolve once (cold path), reuse many times at zero cost (hot path).
    /// </summary>
    public IndexRef GetPKIndexRef<T>() where T : unmanaged
    {
        var ct = GetComponentTable<T>() ?? throw new InvalidOperationException($"Component '{typeof(T).Name}' is not registered.");
        return new IndexRef(-1, ct, ct.IndexLayoutVersion);
    }

    /// <summary>
    /// Returns an <see cref="IndexRef"/> for a secondary indexed field of component <typeparamref name="T"/>.
    /// Resolve once (cold path), reuse many times at zero cost (hot path).
    /// </summary>
    public IndexRef GetIndexRef<T, TKey>(Expression<Func<T, TKey>> keySelector) where T : unmanaged
    {
        var ct = GetComponentTable<T>() ?? throw new InvalidOperationException($"Component '{typeof(T).Name}' is not registered.");
        var fieldName = ExpressionParser.ExtractFieldName(keySelector);
        if (!ct.Definition.FieldsByName.TryGetValue(fieldName, out var field))
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on '{ct.Definition.Name}'.");
        }

        if (!field.HasIndex)
        {
            throw new InvalidOperationException($"Field '{fieldName}' is not indexed.");
        }

        var fieldIndex = QueryResolverHelper.FindFieldIndex(ct.Definition, field);
        return new IndexRef(fieldIndex, ct, ct.IndexLayoutVersion);
    }

    #region Instrumentation Methods

    internal void RecordCommitDuration(long durationUs)
    {
        _commitLastUs = durationUs;

        if (durationUs > _commitMaxUs)
        {
            _commitMaxUs = durationUs;
        }

        Interlocked.Add(ref _commitSumUs, durationUs);
        Interlocked.Increment(ref _commitCount);
        Interlocked.Increment(ref _transactionsCommitted);
    }

    internal void RecordRollback() => Interlocked.Increment(ref _transactionsRolledBack);

    internal void RecordConflict() => Interlocked.Increment(ref _transactionConflicts);

    [LoggerMessage(LogLevel.Warning, "Deferred UoW #{uowId} disposed with {count} committed transaction(s) without Flush/FlushAsync. Data relies on engine shutdown safety net.")]
    internal partial void LogDeferredUowNotFlushed(ushort uowId, int count);

    [LoggerMessage(LogLevel.Debug, "UoW #{uowId} ({mode}) flush: waiting for WAL durable LSN {targetLsn}")]
    internal partial void LogUowFlushStart(ushort uowId, DurabilityMode mode, long targetLsn);

    [LoggerMessage(LogLevel.Debug, "UoW #{uowId} flush complete")]
    internal partial void LogUowFlushComplete(ushort uowId);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit start: {count} component types")]
    internal partial void LogCommitStart(long tsn, int count);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: {phase}")]
    internal partial void LogCommitPhase(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} dispose: {phase}")]
    internal partial void LogTxDispose(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "UoW: {phase}")]
    internal partial void LogUowLifecycle(string phase);

    [LoggerMessage(LogLevel.Debug, "UoW: UowId allocated: {uowId}")]
    internal partial void LogUowIdAllocated(ushort uowId);

    [LoggerMessage(LogLevel.Debug, "Tx.Init #{tsn}: {phase}")]
    internal partial void LogTxInitPhase(long tsn, string phase);

    [LoggerMessage(LogLevel.Debug, "CreateQuickTransaction: Tx #{tsn} created")]
    internal partial void LogQuickTxCreated(long tsn);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CreateComponent<{componentName}> pk={pk}: {step}")]
    internal partial void LogCommitCreateComponent(long tsn, string componentName, long pk, string step);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CommitComponent {componentName} ({entryCount} entries)")]
    internal partial void LogCommitComponentEntries(long tsn, string componentName, int entryCount);

    [LoggerMessage(LogLevel.Debug, "Tx #{tsn} commit: CommitComponent {componentName} done")]
    internal partial void LogCommitComponentDone(long tsn, string componentName);

    [LoggerMessage(LogLevel.Debug, "Cascade delete: following FK on child archetype {childArchetype} slot {slotIndex} from parent {parentId}")]
    internal partial void LogCascadeStep(string childArchetype, int slotIndex, EntityId parentId);

    [LoggerMessage(LogLevel.Information, "Cascade delete complete: root {rootId}, total destroyed {totalDestroyed}")]
    internal partial void LogCascadeSummary(EntityId rootId, int totalDestroyed);

    #endregion

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Capacity: active transactions
        long activeCount = TransactionChain.ActiveCount;
        long maxCount = _options?.Resources?.MaxActiveTransactions ?? 1000;
        writer.WriteCapacity(activeCount, maxCount);

        // Throughput: transaction lifecycle
        writer.WriteThroughput("Created", _transactionsCreated);
        writer.WriteThroughput("Committed", _transactionsCommitted);
        writer.WriteThroughput("RolledBack", _transactionsRolledBack);
        writer.WriteThroughput("Conflicts", _transactionConflicts);

        // Duration: commit timing
        var avgUs = _commitCount > 0 ? _commitSumUs / _commitCount : 0;
        writer.WriteDuration("Commit", _commitLastUs, avgUs, _commitMaxUs);

        // Deferred cleanup throughput
        writer.WriteThroughput("Cleanup.Enqueued", DeferredCleanupManager.EnqueuedTotal);
        writer.WriteThroughput("Cleanup.Processed", DeferredCleanupManager.ProcessedTotal);
    }

    /// <inheritdoc />
    public void ResetPeaks()
    {
        _commitMaxUs = 0;
        _commitSumUs = 0;
        _commitCount = 0;
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties() =>
        new Dictionary<string, object>
        {
            ["TransactionChain.ActiveCount"] = TransactionChain.ActiveCount,
            ["TransactionChain.MinTSN"] = TransactionChain.MinTSN,
            ["TransactionChain.CurrentTSN"] = TransactionChain.NextFreeId,
            ["ComponentTables.Count"] = _componentTableByType?.Count ?? 0,
            ["Schema.ComponentCount"] = DBD.ComponentCount,
            ["Schema.Components"] = string.Join(", ", DBD.ComponentNames),
            ["Transactions.Created"] = _transactionsCreated,
            ["Transactions.Committed"] = _transactionsCommitted,
            ["Transactions.RolledBack"] = _transactionsRolledBack,
            ["Transactions.Conflicts"] = _transactionConflicts,
            ["Commit.LastUs"] = _commitLastUs,
            ["Commit.MaxUs"] = _commitMaxUs,
            ["Commit.Count"] = _commitCount,
            ["DeferredCleanup.QueueSize"] = DeferredCleanupManager.QueueSize,
            ["DeferredCleanup.EnqueuedTotal"] = DeferredCleanupManager.EnqueuedTotal,
            ["DeferredCleanup.ProcessedTotal"] = DeferredCleanupManager.ProcessedTotal,
        };

    #endregion
}