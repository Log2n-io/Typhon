using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using System.Linq.Expressions;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Persisted schema descriptor for a single field of a component (revision-1 layout). Stored inside the owning component's
/// <see cref="ComponentR1.Fields"/> collection to make the database self-describing.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct FieldR1
{
    /// <summary>Fully-qualified schema name of this field-descriptor record ("Typhon.Schema.Field").</summary>
    public const string SchemaName = "Typhon.Schema.Field";

    /// <summary>Field name as declared on the component POCO.</summary>
    public String64 Name;

    /// <summary>Stable numeric id of the field within its component.</summary>
    public int FieldId;

    /// <summary>Logical field type.</summary>
    public FieldType Type;

    /// <summary>For an enum field, the primitive type backing the enum; equal to <see cref="Type"/> for non-enum fields.</summary>
    public FieldType UnderlyingType;

    /// <summary>Root page index (SPI) of this field's dedicated index segment; 0 when the field has no such segment.</summary>
    public uint IndexSPI;

    /// <summary><c>true</c> when the field is declared static — not stored per entity, and excluded from <see cref="ComponentR1.FieldCount"/>.</summary>
    public bool IsStatic;

    /// <summary><c>true</c> when the field is indexed.</summary>
    public bool HasIndex;

    /// <summary><c>true</c> when the field's index permits multiple entries per key (multi-value index).</summary>
    public bool IndexAllowMultiple;

    /// <summary>Element count when the field is a fixed-length array; 0 for scalar fields (see <see cref="IsArray"/>).</summary>
    public int ArrayLength;

    /// <summary>Byte offset of the field within the component's per-entity storage.</summary>
    public int OffsetInComponentStorage;

    /// <summary>Byte size of the field within the component's per-entity storage.</summary>
    public int SizeInComponentStorage;

    /// <summary><c>true</c> when <see cref="ArrayLength"/> &gt; 0, i.e. the field is a fixed-length array.</summary>
    public bool IsArray => ArrayLength > 0;
}

/// <summary>
/// Persisted schema descriptor for a registered component (revision-1 layout). One row per component; makes the database self-describing and
/// enables load-time schema validation against the runtime component definitions.
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct ComponentR1
{
    /// <summary>Fully-qualified schema name of this record ("Typhon.Schema.Component").</summary>
    public const string SchemaName = "Typhon.Schema.Component";

    /// <summary>Registered component schema name.</summary>
    public String64 Name;

    /// <summary>Full CLR type name of the POCO backing this component.</summary>
    public String64 POCOType;

    /// <summary>Size in bytes of the component's per-entity data (pure struct, excluding overhead).</summary>
    public int CompSize;

    /// <summary>Per-entity storage overhead in bytes for this component's layout; 0 when the layout carries no overhead.</summary>
    public int CompOverhead;

    /// <summary>Root page index (SPI) of the component data segment.</summary>
    public int ComponentSPI;

    /// <summary>Root page index (SPI) of the component's revision-table segment; 0 when the component has no revision chain (non-Versioned).</summary>
    public int VersionSPI;

    /// <summary>Root page index (SPI) of the default value index segment; 0 when the component has no such index.</summary>
    public int DefaultIndexSPI;

    /// <summary>Root page index (SPI) of the <see cref="String64"/> value index segment; 0 when absent.</summary>
    public int String64IndexSPI;

    /// <summary>Root page index (SPI) of the tail (multi-value) index segment; 0 when absent.</summary>
    public int TailIndexSPI;

    /// <summary>Field descriptors for this component in declaration order, stored inline as a variable-size collection.</summary>
    public ComponentCollection<FieldR1> Fields;

    /// <summary>Schema revision of the component definition, from its <c>[Component(..., revision)]</c> attribute.</summary>
    public int SchemaRevision;

    /// <summary>Number of non-static fields (static fields are not counted).</summary>
    public int FieldCount;

    /// <summary>The component's <see cref="Typhon.Schema.Definition.StorageMode"/>, persisted as its underlying byte value.</summary>
    public byte StorageMode;

    /// <summary>AssemblyR1 row id (chunkId) of the assembly that declares this component. 0 = core engine assembly (implicit, never in the manifest).</summary>
    public ushort AssemblyId;
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
    /// <summary>Fully-qualified schema name of this record ("Typhon.Schema.Archetype").</summary>
    public const string SchemaName = "Typhon.Schema.Archetype";

    /// <summary>Archetype CLR type name (e.g., "Building").</summary>
    public String64 Name;

    /// <summary>Per-process catalog id (from <c>[Archetype(Id = N)]</c> today; generator-assigned after Phase 5). Identity re-match on reopen is by
    /// <see cref="Name"/>, not this value — it is not stable across processes once the generator owns numbering.</summary>
    public ushort ArchetypeId;

    /// <summary>Per-DB, engine-assigned archetype routing id — the value embedded in every <see cref="EntityId"/> of this archetype. Assigned monotonically
    /// (dense) on first registration into this database, persisted here, and restored by <see cref="Name"/> match on reopen so existing EntityIds keep
    /// resolving. This is the durable per-DB archetype identity used for EntityId routing.</summary>
    public ushort RoutingId;

    /// <summary>Parent archetype ID (0xFFFF = no parent).</summary>
    public ushort ParentArchetypeId;

    /// <summary>Total component count (own + inherited).</summary>
    public byte ComponentCount;

    /// <summary>Reserved padding to preserve field alignment; unused.</summary>
    public byte _pad0;

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

    /// <summary>AssemblyR1 row id (chunkId) of the assembly that declares this archetype. 0 = core engine assembly (implicit, never in the manifest).</summary>
    public ushort AssemblyId;

    /// <summary>Sentinel <see cref="ParentArchetypeId"/> value meaning "no parent" (a root archetype).</summary>
    public const ushort NoParent = 0xFFFF;
}

/// <summary>
/// Persisted identity of a .NET assembly that declares one or more components/archetypes stored in this database — the self-describing schema manifest.
/// One entity per assembly. Stores identity (simple name + version + public-key-token), never a filename/path: the Workbench resolves the assembly by simple
/// name at open time. The core engine assembly (Typhon.Engine) is intentionally excluded — it is always loaded — so it never gets a row.
/// </summary>
[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct AssemblyR1
{
    /// <summary>Fully-qualified schema name of this record ("Typhon.Schema.Assembly").</summary>
    public const string SchemaName = "Typhon.Schema.Assembly";

    /// <summary>Assembly simple name (e.g. "AntHill.Core") — the resolution key.</summary>
    public String64 SimpleName;

    /// <summary>Assembly version, major component.</summary>
    public int VerMajor;

    /// <summary>Assembly version, minor component.</summary>
    public int VerMinor;

    /// <summary>Assembly version, build component.</summary>
    public int VerBuild;

    /// <summary>Assembly version, revision component.</summary>
    public int VerRevision;

    /// <summary>Public-key-token packed little-endian into a u64; 0 = unsigned assembly.</summary>
    public ulong PublicKeyToken;
}

/// <summary>
/// Describes the kind of schema change recorded in the audit trail.
/// </summary>
[PublicAPI]
public enum SchemaChangeKind
{
    /// <summary>Backward-compatible change with no breaking edits; existing data is read as-is, no migration ran.</summary>
    Compatible,

    /// <summary>Breaking change that required migrating existing entities to the new layout.</summary>
    Migration,

    /// <summary>Change originating from an engine/system-component upgrade.</summary>
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
    /// <summary>Fully-qualified schema name of this record ("Typhon.Schema.History").</summary>
    public const string SchemaName = "Typhon.Schema.History";

    /// <summary>When the change was recorded, as <see cref="System.DateTime.UtcNow"/> ticks.</summary>
    public long Timestamp;

    /// <summary>Schema name of the component whose definition changed.</summary>
    public String64 ComponentName;

    /// <summary>Component schema revision before the change.</summary>
    public int FromRevision;

    /// <summary>Component schema revision after the change.</summary>
    public int ToRevision;

    /// <summary>Number of fields added by the change.</summary>
    public int FieldsAdded;

    /// <summary>Number of fields removed by the change.</summary>
    public int FieldsRemoved;

    /// <summary>Number of fields whose type changed or widened.</summary>
    public int FieldsTypeChanged;

    /// <summary>Number of entities migrated to the new layout; 0 when no migration ran.</summary>
    public int EntitiesMigrated;

    /// <summary>Wall-clock duration of the migration in milliseconds; 0 when no migration ran.</summary>
    public int ElapsedMilliseconds;

    /// <summary>Classification of the change (see <see cref="SchemaChangeKind"/>).</summary>
    public SchemaChangeKind Kind;
}

/// <summary>
/// Configuration options for <see cref="DatabaseEngine"/>.
/// </summary>
[PublicAPI]
public class DatabaseEngineOptions
{
    /// <summary>
    /// Resource knobs for the engine subsystems: max concurrent transactions, WAL ring-buffer size, checkpoint cadence, and page-CRC policy.
    /// </summary>
    /// <remarks>
    /// Range-validated at DI resolution by the engine's options validator — no separate pre-flight call is required. Page-cache sizing lives on
    /// <see cref="PagedMMFOptions.DatabaseCacheSize"/>, not here.
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
    /// WAL writer configuration. WAL + checkpoint are mandatory: this always resolves to a non-null configuration. To run without disk I/O (tests,
    /// benchmarks, throwaway sessions), register an in-memory <see cref="IWalFileIO"/> in DI rather than disabling the WAL.
    /// </summary>
    public WalWriterOptions Wal { get; set; } = new();

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

    private readonly IResource                  _durabilityNode;
    private WalRecoveryResult                   _lastRecoveryResult;
    internal TransientOptions                   TransientOptions => _options.Transient;
    internal WalRecoveryResult                  LastRecoveryResult => _lastRecoveryResult;

    // ReSharper disable once ConvertToAutoProperty MUST KEEP _logger for SourceGen to generate the log properly
    internal ILogger<DatabaseEngine> Logger => _logger;

    internal IMemoryAllocator                   MemoryAllocator { get; }

    /// <summary>
    /// Shared WAL staging buffer pool — exposed to the profiler's gauge emitter. Present for the engine's lifetime and cleared to null on disposal;
    /// do not keep references across engine lifecycle boundaries.
    /// </summary>
    internal StagingBufferPool StagingBufferPool { get; private set; }

    // Bootstrap dictionary keys (engine layer)
    // ReSharper disable InconsistentNaming
    internal const string BK_SystemSchemaRevision   = "SystemSchemaRevision";
    internal const string BK_SysComponentR1         = "sys.ComponentR1";
    internal const string BK_SysSchemaHistory       = "sys.SchemaHistory";
    internal const string BK_SysAssemblyR1          = "sys.AssemblyR1";
    internal const string BK_SpatialGridConfig      = "spatial.GridConfig";
    internal const string BK_NextFreeTSN            = "NextFreeTSN";
    internal const string BK_UowRegistrySPI         = "UowRegistrySPI";
    internal const string BK_CollectionFieldR1      = "collection.FieldR1";
    internal const string BK_CollectionCount        = "collection.count";
    internal const string BK_UserSchemaVersion      = "UserSchemaVersion";
    internal const string BK_LastTickFenceLSN       = "LastTickFenceLSN";
    internal const string BK_CleanShutdown          = "CleanShutdown";
    internal const string BK_SeedRevision           = "SeedRevision";
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
    private ComponentTable _assembliesTable;
    private ConcurrentDictionary<Type, ComponentTable> _componentTableByType;

    // ─── ArchetypeRegistry lifecycle tracking ───────────────────────────────────────────────────────────
    //
    // Every CLR archetype + component <see cref="Type"/> this engine causes to be inserted into the global <c>ArchetypeRegistry</c> is recorded here.
    // On <see cref="Dispose"/> these sets are passed to <c>ArchetypeRegistry.UnregisterEngineUse</c>, which decrements per-Type refcounts and removes the
    // registry entry when the count reaches zero. That release-on-zero is what lets the owning AssemblyLoadContext be GC'd between Workbench sessions — without
    // it, the registry pinned the first ALC's Types for the lifetime of the process and any later session loading the same DLL into a fresh collectible ALC
    // saw stale state.
    private readonly HashSet<Type> _registeredArchetypeTypes = [];
    private readonly HashSet<Type> _registeredComponentTypes = [];
    private bool _unregisteredFromRegistry;

    /// <summary>Component schema names that underwent migration during this engine session. Used to invalidate stale EntityMaps.</summary>
    private HashSet<string> _migratedComponents;
    private ConcurrentDictionary<ushort, ComponentTable> _componentTableByWalTypeId;
    private long _lastTickFenceLSN;
    internal long LastTickFenceLSN => _lastTickFenceLSN;

    // ─── Clean-shutdown HEAD marker (open-time fast path) ─────────────────────────────────────────────────────────
    // Versioned HEAD values live in-place in the persisted cluster slot (07-versioned-overlay.md §Write-Path step 4),
    // so on a graceful close the on-disk slots are already current and RebuildVersionedHeadFromChain — ~49% of a large
    // DB's open cost — is pure waste. A graceful Dispose sets a clean-shutdown FLAG (BK_CleanShutdown = 1) via
    // MarkCleanShutdown (a separate fsync strictly after the data flush). On open we trust the persisted HEADs iff that
    // flag was set and no component migrated this session, then clear the flag before any mutation so a crash this
    // session forces a rebuild on the next open. The flag is deliberately NOT keyed on CheckpointLSN: a bulk-generated DB
    // closes cleanly with CheckpointLSN == 0 (its data went straight to the .bin, nothing checkpointed through the WAL),
    // and its HEADs are still current — gating trust on a non-zero LSN wrongly forced a full rebuild for exactly those
    // DBs. CheckpointLSN is kept only for the diagnostic log line. See claude/rules/durability.md (CS-01..CS-03).
    private bool _cleanShutdownAtOpen;
    private long _checkpointLsnAtOpen;
    private bool _headsTrusted;

    /// <summary>Diagnostic + test oracle: the number of archetypes whose Versioned HEADs were rebuilt during the last
    /// <see cref="InitializeArchetypes"/>. 0 on a trusted (clean) reopen; &gt;0 after a crash or on a legacy database.</summary>
    internal int LastOpenVersionedHeadRebuildCount;

    /// <summary>True when WAL segment files exist at open (a crash left a recovery window). Captured ONCE in <see cref="InitializeArchetypes"/> before any
    /// ComponentTable loads. Gates the crash-path secondary-index clear+rebuild (RB-01): the load ctors read it to clear+recreate indexes fresh (torn-safe),
    /// and <see cref="RunWalV2Recovery"/> reads the SAME flag to fire the Phase-5 rebuild — so clear and rebuild always agree (clearing without rebuilding would
    /// leave indexes empty). Distinct from <see cref="_headsTrusted"/>, which can be false on a clean migration reopen with no WAL window (indexes load normally).</summary>
    internal bool WalFilesPresentAtOpen { get; private set; }

    /// <summary>Gates the checkpoint-time <c>PersistArchetypeState</c> hook (#395 / CK-10). False during open + recovery (so the recovery seal — a
    /// ForceCheckpoint — does NOT persist segment SPIs mid-rebuild); set true at the end of <c>InitializeArchetypes</c> so every steady-state
    /// checkpoint records them.</summary>
    private volatile bool _archetypeSpiPersistArmed;

    /// <summary>Test-only: when set, <see cref="Dispose"/> skips <c>MarkCleanShutdown</c>, reproducing an unclean shutdown
    /// (a real crash also never writes the marker). Unit tests cannot abort the process — same convention as the
    /// <c>BulkLoadRecoveryTests</c> incomplete-bulk path.</summary>
    internal bool SimulateUncleanShutdownForTest;

    private bool _simulateHardCrash;

    /// <summary>
    /// Test-only "power cut": tears the engine down WITHOUT any final persistence — no shutdown checkpoint cycle, no <c>PersistArchetypeState</c>, no
    /// <c>PersistEngineState</c>, no clean-shutdown marker. The managed page cache (dirty, uncheckpointed pages) is discarded by <see cref="PagedMMF.Dispose"/>
    /// exactly as volatile RAM is lost on power loss, so only data already on stable media survives: prior checkpoints and fsynced WAL records (Immediate commits).
    /// The next open of the same directory must therefore recover committed data via WAL replay. This is the in-process equivalent of killing the process at a
    /// moment of true data loss (which <c>TerminateProcess</c> cannot reproduce — the OS flushes its caches). See <c>claude/design/Durability/crash-recovery-testing.md</c> §1.
    /// </summary>
    internal void SimulateHardCrash()
    {
        if (IsDisposed)
        {
            return;
        }

        _simulateHardCrash = true;
        CheckpointManager?.PrepareCrashStop(); // suppress the checkpoint thread's shutdown flush before we stop it
        Dispose();
    }

    /// <summary>
    /// Tick-scoped state shared across the four fence phases. Reset by <c>TyphonRuntime.RunParallelFence</c>
    /// at fence entry; populated progressively by each phase's <c>Prepare</c>.
    /// </summary>
    internal FenceContext FenceContext { get; } = new();

    /// <summary>
    /// Lock-free atomic-max update of <see cref="_lastTickFenceLSN"/>. Used by the parallel fence path (<c>TyphonRuntime.OnTickEndInternal</c>) to publish the
    /// highest LSN observed across all fence chunks once they all complete. Equivalent in effect to the legacy serial path's <c>Interlocked.Exchange</c>, but
    /// tolerates the possibility that a future change layers concurrent publishers — atomic-max is the right primitive here.
    /// </summary>
    internal void UpdateLastTickFenceLSNAtomic(long candidate)
    {
        while (true)
        {
            var current = _lastTickFenceLSN;
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastTickFenceLSN, candidate, current) == current)
            {
                return;
            }
        }
    }
    internal Dictionary<string, (int ChunkId, ComponentR1 Comp)> _persistedComponents;
    /// <summary>Persisted archetype rows, keyed by archetype <see cref="ArchetypeR1.Name"/> (reopen re-match is by name —
    /// see claude/design/Ecs/SourceGeneratedRegistry/04-solution-design.md §7.1).</summary>
    internal Dictionary<string, (int ChunkId, ArchetypeR1 Arch)> _persistedArchetypes;

    /// <summary>Persisted schema-assembly manifest, keyed by AssemblyId (= AssemblyR1 row chunkId). Loaded eagerly on open so it is readable schemaless.</summary>
    internal Dictionary<ushort, (int ChunkId, AssemblyR1 Asm)> _persistedAssemblies;

    /// <summary>Dedup index: assembly simple name → AssemblyId. Seeded from <see cref="_persistedAssemblies"/> on open; appended as new assemblies are persisted.</summary>
    private Dictionary<string, ushort> _assemblyIdByName;

    /// <summary>Per-engine archetype runtime state, indexed by per-process catalog id (<see cref="ArchetypeMetadata.ArchetypeId"/>). Separates per-engine
    /// mutable data from shared schema metadata. Reached from an <see cref="EntityId"/> via <see cref="GetMetaByRouting"/> then the meta's catalog id.</summary>
    internal ArchetypeEngineState[] _archetypeStates;

    // ── Per-DB archetype routing (claude/design/Ecs/SourceGeneratedRegistry/04-solution-design.md §2/§6) ───────────
    // The EntityId carries a per-DB routing id (low 16 bits), NOT the per-process catalog id. These two per-engine tables translate between the two id spaces:
    //   _metaByRouting[routingId]        → ArchetypeMetadata   (resolve an EntityId to its archetype)
    //   _routingByCatalog[catalogId]     → routingId           (compose an EntityId for a known archetype)
    // Both are fixed-size (RoutingTableSize) so a concurrent registration in another engine can never overflow them — this removes Face A's IndexOutOfRange
    // sizing coupling (the definitive race closure lands with the Phase 3 registration lock + own-archetype scoping).

    /// <summary>Fixed size for the per-DB routing tables. Matches the per-process catalog cap (12-bit legacy id space); routing ids stay dense below it.</summary>
    private const int RoutingTableSize = 4096;

    /// <summary>Sentinel for "no routing id assigned to this catalog id in this DB".</summary>
    internal const ushort NoRoutingId = 0xFFFF;

    /// <summary>routingId → archetype metadata (per-DB). Populated in <see cref="InitializeArchetypes"/>.</summary>
    internal ArchetypeMetadata[] _metaByRouting;

    /// <summary>routingId → per-engine state (same objects as <see cref="_archetypeStates"/>, routing-indexed). Keeps the hot EntityId→state path single-hop.</summary>
    internal ArchetypeEngineState[] _stateByRouting;

    /// <summary>catalog id (<see cref="ArchetypeMetadata.ArchetypeId"/>) → per-DB routing id; <see cref="NoRoutingId"/> when this DB has no such archetype.</summary>
    internal ushort[] _routingByCatalog;

    /// <summary>Monotonic per-DB routing-id high-water mark; resumes above the max persisted routing id on reopen.</summary>
    private ushort _nextRoutingId;

    /// <summary>Resolve an <see cref="EntityId"/>'s routing id to its archetype metadata. O(1). Returns null for an unknown routing id.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ArchetypeMetadata GetMetaByRouting(ushort routingId) => routingId < (uint)_metaByRouting.Length ? _metaByRouting[routingId] : null;

    /// <summary>The per-DB routing id for a known archetype (for composing an <see cref="EntityId"/>). O(1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort RoutingIdOf(ArchetypeMetadata meta) => _routingByCatalog[meta.ArchetypeId];

    /// <summary>The per-DB routing id for a catalog archetype id — for tooling (Workbench) that holds catalog ids from the schema and needs the routing id
    /// that routing-based APIs (e.g. <see cref="Transaction.EnumerateArchetypeEntities"/>) expect. Returns <see cref="NoRoutingId"/> if unmapped.</summary>
    internal ushort RoutingIdForCatalog(ushort catalogId) => catalogId < (uint)_routingByCatalog.Length ? _routingByCatalog[catalogId] : NoRoutingId;
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

    /// <summary>
    /// Declare that <typeparamref name="TArch"/> uses <c>ClusterRef.WriteSpatial</c> as the exclusive writer of its spatial component. Skips the fence-time
    /// legacy scan for this archetype — both <c>DetectClusterMigrations</c>'s dirtyBits sweep and <c>RecomputeDirtyClusterAabbs</c>'s <c>ActiveClusterIds</c>
    /// iteration are replaced with sparse iteration over <see cref="ArchetypeClusterState.ClusterProcessBitmap"/>.
    /// <para>
    /// Only set when EVERY spatial-field write on this archetype goes through <c>WriteSpatial</c>. Mutations via raw <c>GetSpan</c> or <c>OpenMut + Write</c>
    /// will be invisible to the engine's spatial maintenance after this is enabled. The <c>TYPHON009</c> analyzer flags non-WriteSpatial mutation sites — once
    /// those are zero, it's safe to opt in.
    /// </para>
    /// </summary>
    [PublicAPI]
    public void SetSpatialBarrierOnly<TArch>(bool value = true) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        if (meta == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} not registered. Call after InitializeArchetypes.");
        }

        var state = _archetypeStates[meta.ArchetypeId]?.ClusterState
                    ?? throw new InvalidOperationException($"Archetype {typeof(TArch).Name} is not a cluster archetype.");
        if (!state.SpatialSlot.HasSpatialIndex)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} has no spatial-indexed component.");
        }

        state.SpatialBarrierOnly = value;
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

    /// <summary>Registry of the component and archetype schema definitions registered on this engine instance.</summary>
    public DatabaseDefinitions DBD { get; }

    /// <summary>Backing paged memory-mapped file store holding all persisted segments of this database.</summary>
    public ManagedPagedMMF MMF { get; }

    /// <summary>
    /// <see langword="true"/> when this open <b>created</b> the database bundle (it did not exist before); <see langword="false"/>
    /// when it reopened an existing one. Use it to gate one-time bootstrap work (initial data, first-run migrations). Note the
    /// engine already offers a crash-safe, declarative alternative via <see cref="TyphonOptions.Seed"/>; prefer that for
    /// seeding, and this flag for cheaper conditional logic. It reflects file existence at open, so after a crash mid-initialisation
    /// it reads <see langword="false"/> — it is not, by itself, a "seeding completed" signal.
    /// </summary>
    public bool IsNewlyCreated => MMF.IsDatabaseFileCreating;

    /// <summary>Epoch manager coordinating safe, lock-free memory reclamation across concurrent readers and writers.</summary>
    public EpochManager EpochManager { get; private set; }
    internal DeadlineWatchdog Watchdog { get; }

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
        var readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        foreach (var entry in toProcess)
        {
            var meta = entry.Meta;
            var engineState = _archetypeStates[meta.ArchetypeId];
            if (engineState?.EntityMap == null)
            {
                continue;
            }
            var accessor = engineState.EntityMap.Segment.CreateChunkAccessor();
            var found = engineState.EntityMap.TryGet(entry.Id.EntityKey, readBuf, ref accessor);

            if (found)
            {
                // Free component chunks
                for (var slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var chunkId = EntityRecordAccessor.GetLocation(readBuf, slot);
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
    /// WAL manager driving durability. WAL is mandatory, so this is present for the engine's lifetime; it is cleared to null only on disposal.
    /// </summary>
    internal WalManager WalManager { get; private set; }

    /// <summary>
    /// The WAL v2 durability seam (01 §3) — the single path every emitter appends records through. Composes <see cref="WalManager"/>.
    /// </summary>
    internal IDurabilityLog DurabilityLog { get; private set; }

    /// <summary>
    /// Checkpoint manager. WAL is mandatory, so this is present for the engine's lifetime (cleared to null only on disposal). Periodically flushes dirty
    /// data pages and advances CheckpointLSN to enable WAL segment recycling.
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

    // Optional WAL file-IO backend supplied by the host (DI). Null = the engine owns a production WalFileIO. Tests register an InMemoryWalFileIO to run
    // the full WAL pipeline with zero disk I/O. When injected, the engine does NOT own/dispose it here — the DI scope's lifetime governs it.
    private readonly IWalFileIO _injectedWalIo;

    internal DatabaseEngine(IResourceRegistry resourceRegistry, EpochManager epochManager, DeadlineWatchdog watchdog, ManagedPagedMMF mmf,
        IMemoryAllocator memoryAllocator, DatabaseEngineOptions options, ILogger<DatabaseEngine> log, string name = null, IWalFileIO injectedWalIo = null)
        : base(name ?? $"DatabaseEngine_{Guid.NewGuid():N}", ResourceType.Engine, resourceRegistry.DataEngine)
    {
        // Engine initialization
        MMF = mmf;
        EpochManager = epochManager;
        Watchdog = watchdog;
        _logger = log;
        // Register a process-wide sink for the always-on spatial DFS-overflow warning (#422, Tier-0). First non-null wins;
        // the counter records regardless, so this only enables the human-readable warning.
        SpatialRTreeDiagnostics.DiagnosticsLogger ??= log;
        _options = options;
        _injectedWalIo = injectedWalIo;
        MemoryAllocator = memoryAllocator;

        // Resolve the WAL directory to {bundle}/wal when the caller left it null (the bundle-format default). This MUST run HERE — before
        // InitializeUowRegistry() below — because the reopen path reads _options.Wal.WalDirectory to decide whether WAL segments are present and recovery must
        // run (WalFilesPresentAtOpen). Deriving it later (in InitializeWalManager) would leave that read seeing null, silently skipping crash recovery under
        // the default config. Keeps each database's WAL private to its .typhon bundle; an explicit WalDirectory is honored as-is. (This writes back into
        // _options.Wal — safe because every DI path resolves ONE engine per DatabaseEngineOptions instance, and two databases each get their own
        // provider/options; sharing one options across two engines is not a supported path.)
        _options.Wal?.WalDirectory ??= Path.Combine(MMF.BundleDirectory, "wal");

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

    /// <summary><c>true</c> once the engine has been disposed; further operations on it are invalid.</summary>
    public bool IsDisposed { get; private set; }

    // Test-only seam: when set, DisposeCore throws at the very start of teardown (simulates a failing step, e.g. a full
    // disk during the final checkpoint) so tests can prove Dispose()'s finally still releases the owned provider. §11 / #147.
    internal bool ThrowInDisposeCoreForTest { get; set; }

    /// <summary>
    /// Releases engine resources following the standard dispose pattern. Idempotent — a no-op once <see cref="IsDisposed"/> is set. Runs the core teardown
    /// inside a try/finally so an owned service provider is still released even if a teardown step throws.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <see cref="System.IDisposable.Dispose"/>; <c>false</c> when called from the finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            DisposeCore(disposing);
        }
        finally
        {
            // Set BEFORE the owned-provider disposal so the provider's re-entrant disposal of this same singleton
            // short-circuits at the IsDisposed guard above.
            IsDisposed = true;

            // Open() path only: dispose the private container this engine owns (null on the DI path — the host owns it
            // there). In a FINALLY so a throw from a teardown step in DisposeCore (e.g. PersistEngineState on a full disk)
            // still releases the container — otherwise the owned provider's threads (watchdog, timer) + native memory would
            // leak. The provider also disposes the rest of the engine-graph singletons (ResourceRegistry, EpochManager,
            // MMF, ...); MMF was already disposed in DisposeCore and its Dispose is idempotent. Guarded so a dispose-time
            // provider fault can't mask an in-flight teardown exception.
            if (disposing)
            {
                var ownedProvider = _ownedProvider;
                _ownedProvider = null;
                try
                {
                    ownedProvider?.Dispose();
                }
                catch
                {
                    // ignored — a teardown exception (if any) is the diagnostic one; cleanup must not mask it.
                }
            }
        }
    }

    // Engine teardown, split out so Dispose() can guarantee owned-provider disposal in a finally (see there). A throw from
    // any step here propagates out of Dispose() after the finally has released the owned container.
    private void DisposeCore(bool disposing)
    {
        if (disposing)
        {
            if (ThrowInDisposeCoreForTest)
            {
                throw new InvalidOperationException("Simulated teardown-step failure (ThrowInDisposeCoreForTest).");
            }

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

            // Hard-crash simulation (power cut): skip EVERY final-persistence step. PersistEngineState would flush uncheckpointed dirty pages to the data file and
            // PersistArchetypeState would persist EntityMap state — both would smuggle committed data onto disk that a real crash would have lost, masking the
            // dependency on WAL replay. The clean-shutdown marker is likewise never written. Only what is already fsynced (prior checkpoints + WAL) survives.
            if (!_simulateHardCrash)
            {
                Logger?.LogInformation("Engine disposing: PersistArchetypeState");
                // Persist EntityMap SPIs and NextEntityKey counters so reopen can load EntityMaps directly
                PersistArchetypeState();

                Logger?.LogInformation("Engine disposing: PersistEngineState");
                // Persist final TSN counter and flush all dirty pages to disk. This ensures:
                // 1. TSN counter survives restart (MVCC visibility)
                // 2. All committed transaction data is on disk even without WAL/checkpoint
                PersistEngineState();

                // Clean-shutdown HEAD marker: STRICTLY AFTER PersistEngineState's data fsync (own separate fsync, never
                // bundled), so a torn close can never leave the marker durable ahead of the cluster pages it vouches for.
                // Skipped by SimulateUncleanShutdownForTest to reproduce a crash (which also never writes the marker).
                if (!SimulateUncleanShutdownForTest)
                {
                    MarkCleanShutdown();
                }
            }

            Logger?.LogInformation("Engine disposing: WalManager");
            WalManager?.Dispose();
            WalManager = null;
            Logger?.LogInformation("Engine disposing: TransactionChain + cleanup");
            TransactionChain.Dispose();
            UowRegistry?.Dispose();
            MMF.Dispose();

            // ─── Release the global ArchetypeRegistry's references to this engine's Types ──────────────
            // Done last so the disposal pipeline above (PersistArchetypeState, PersistEngineState, etc.) still has access to the registry while it needs to
            // read archetype metadata. After this call returns, the registry no longer holds Type references on behalf of THIS engine — and once the GC
            // reclaims this engine instance, the collectible AssemblyLoadContext (Workbench) can also be reclaimed. Guarded by `_unregisteredFromRegistry` so
            // a double-dispose doesn't double-decrement (the underlying API is idempotent anyway, but the flag is cheaper).
            if (!_unregisteredFromRegistry)
            {
                ArchetypeRegistry.UnregisterEngineUse(_registeredArchetypeTypes, _registeredComponentTypes);
                _unregisteredFromRegistry = true;
            }
        }
        base.Dispose(disposing);
    }

    private void InitializeWalManager()
    {
        var walOptions = _options.Wal;
        if (walOptions == null)
        {
            throw new InvalidOperationException(
                "WAL is mandatory: DatabaseEngineOptions.Wal must not be null. For no-disk-I/O scenarios (tests, benchmarks), register an in-memory IWalFileIO instead.");
        }

        // WalDirectory was already resolved to {bundle}/wal (when left null) early in the constructor — it MUST be done
        // before InitializeUowRegistry() reads it for the reopen-recovery decision, so it is not re-derived here.

        // Use the host-injected WAL file-IO when supplied (tests register an InMemoryWalFileIO to exercise the full WAL pipeline with no disk I/O);
        // otherwise construct the production file-based implementation. WalManager does NOT dispose this backend: production WalFileIO is stateless
        // (no-op Dispose; segment handles are owned by WalSegmentManager), and an injected backend's lifetime is governed by the DI scope (see _injectedWalIo).
        IWalFileIO walFileIO = _injectedWalIo ?? new WalFileIO();

        var commitBufferCapacity = _options.Resources.WalRingBufferSizeBytes / 2;
        WalManager = new WalManager(walOptions, MemoryAllocator, walFileIO, _durabilityNode, commitBufferCapacity);
        DurabilityLog = new DurabilityLog(WalManager);

        // Determine continuation point from recovery or fresh start
        var lastLSN = _lastRecoveryResult.LastValidLSN;
        var lastSegmentId = 0L; // Floor only — WalSegmentManager.Initialize scans the on-disk directory and continues past the highest existing id.
        // Checkpoint frontier for the reopen reconcile: WAL segments whose records are all below this are already in the
        // data file and get reclaimed; segments with records ≥ this are retained for crash recovery (REC-04 / WR-01).
        var checkpointLsn = DurabilityWatermarks.ReadCheckpointLsn(MMF);
        // LSN must stay globally monotonic across sessions. Continue strictly above the durability frontier — the higher of the recovered WAL frontier (crash path) and
        // the persisted CheckpointLSN (clean-reopen path, where NO WAL recovery ran so lastLSN is 0). Using lastLSN alone restarts the reopened writer at LSN 1, below a
        // prior session's CheckpointLSN; RecoveryDriver then skips the entire post-reopen window as already-consolidated (LOG-08 — silent loss of durably-acked commits).
        var frontierLsn = Math.Max(lastLSN, checkpointLsn);
        WalManager.Initialize(lastSegmentId, frontierLsn > 0 ? frontierLsn + 1 : 1, checkpointLsn);
        // Seed the durable watermark to the reopen frontier so it MATCHES LastAppendedLsn (= NextLsn-1 = frontierLsn). Initialize advances NextLsn to
        // frontierLsn+1 (LOG-08, LSN monotonic across sessions); without a matching durable seed, DurableLsn stays 0 while LastAppendedLsn=frontierLsn, so
        // the very first checkpoint barrier (CK-02 waits DurableLsn ≥ LastAppendedLsn) blocks for an LSN no frame will ever publish on an idle reopened
        // engine — a 30 s WalBackPressureTimeout per dispose. These LSNs were durable in the prior session (recovered from disk), so seeding is correct.
        // The crash-recovery path also seeds DurableLsn to its replayed frontier; AdvanceDurable is a monotonic max, so the two are idempotent.
        if (frontierLsn > 0)
        {
            WalManager.SeedDurableLsn(frontierLsn);
        }
        WalManager.Logger = Logger;
        WalManager.Start();
    }

    private void InitializeCheckpointManager()
    {
        // WAL is mandatory, so WalManager is always present and the checkpoint manager is always created.

        // Read initial CheckpointLSN from file header
        long initialCheckpointLsn;
        using (EpochGuard.Enter(EpochManager))
        {
            initialCheckpointLsn = DurabilityWatermarks.ReadCheckpointLsn(MMF);
        }

        StagingBufferPool = new StagingBufferPool(MemoryAllocator, _durabilityNode);

        // CRC verification mode. CLEAN reopen → activate the configured mode (OnLoad) now; the in-ctor WalRecovery is done and on-load corruption detection is
        // wanted. CRASH path → stay in RecoveryOnly through the v2 recovery: the ComponentTable index clear (at registration) and RunWalV2Recovery's
        // apply/scrub/rebuild load persisted pages, and a torn index/occupancy page must NOT throw before the rebuild net replaces it (RB-01/CK-09) — FPI has been
        // retired (increment D), so there is no on-load repair fallback. InitializeArchetypes restores the configured mode after RunWalV2Recovery completes.
        MMF.SetPageChecksumVerification(WalFilesPresentAtOpen ? PageChecksumVerification.RecoverySuspect : _options.Resources.PageChecksumVerification);

        CheckpointManager = new CheckpointManager(MMF, UowRegistry, WalManager, _options.Resources, EpochManager, StagingBufferPool, _durabilityNode,
            initialCheckpointLsn, () => _lastTickFenceLSN);
        CheckpointManager.Logger = Logger;
        // Persist per-archetype segment SPIs at every checkpoint so a consolidated cluster/EntityMap base is reachable on reopen after a hard crash
        // (#395). Idempotent and skip-unchanged, so a steady-state cycle is nearly free. Runs at cycle start (before the barrier) so its WAL records +
        // dirty pages ride the same cycle. Armed only AFTER InitializeArchetypes completes (incl. the recovery seal), so the seal — itself a
        // ForceCheckpoint — keeps its original behaviour and does NOT persist SPIs mid-recovery (the rebuilt segments are sealed first; the first
        // steady-state checkpoint then records them). #395.
        CheckpointManager.PersistDurableMetadataHook = () =>
        {
            if (_archetypeSpiPersistArmed)
            {
                PersistArchetypeState();
            }
        };
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
    /// Consumed by <see cref="GaugeSnapshotEmitter"/> once per scheduler tick; cost is O(ComponentTables + Archetypes).
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
            for (var i = 0; i < _archetypeStates.Length; i++)
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
    /// Iterate dirty entities from the tick fence snapshot and update spatial R-Tree positions.
    /// For each dirty entity: if not destroyed, call UpdateSpatial (fat AABB containment check → possible reinsert).
    /// </summary>
    private unsafe int ProcessSpatialEntries(ComponentTable table, long[] dirtyBits, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;

        // Hoist accessor creation before the entity loop (same pattern as B+Tree batch index maintenance)
        var compAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
        var treeAccessor = state.ActiveTree.Segment.CreateChunkAccessor(changeSet);
        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        var dirtyCount = 0;
        var escapeCount = 0;
        try
        {
            for (var wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
            {
                var word = dirtyBits[wordIdx];
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount((ulong)word);
                    var chunkId = wordIdx * 64 + bit;
                    word &= word - 1; // clear lowest set bit

                    if (table.IsChunkDestroyed(chunkId))
                    {
                        continue;
                    }

                    long entityPK = 0;
                    if (table.Definition.EntityPKOverheadSize > 0)
                    {
                        var chunkPtr = compAccessor.GetChunkAddress(chunkId);
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
            var escapeRate = (double)escapeCount / dirtyCount;
            if (escapeRate > 0.10)
            {
                SpatialMaintainer.LogHighEscapeRate(Logger, table.Definition.Name, escapeRate, escapeCount, dirtyCount);
            }
        }

        return escapeCount;
    }

    /// <summary>
    /// Persist spatial index segment root page indexes to BootstrapDictionary.
    /// Written once at component registration; segment root pages are immutable after allocation.
    /// </summary>
    private void SaveSpatialBootstrap(ComponentTable table)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        var key = $"spatial.{table.Definition.Name}";

        // Tree SPIs + config packed into Int5: treeSPI, backPtrSPI, variant|mode|stride, margin bits, hmSPI (0 if no hashmap)
        var activeTree = state.ActiveTree;
        MMF.Bootstrap.Set(key, BootstrapDictionary.Value.FromInt5(activeTree.Segment.RootPageIndex, state.BackPointerSegment.RootPageIndex, 
            (int)activeTree.Variant | ((int)fi.Mode << 4) | (state.Descriptor.Stride << 8), BitConverter.SingleToInt32Bits(fi.Margin),
            state.OccupancyMap?.Segment.RootPageIndex ?? 0));

        MMF.SaveBootstrap();
    }

    /// <summary>
    /// Persists the engine-wide <see cref="SpatialGridConfig"/> (world bounds, cell size, hysteresis — the 6 source floats; the rest is derived) so a generic
    /// opener that never calls <see cref="ConfigureSpatialGrid"/> can reconstruct the grid and fully initialize cluster-spatial archetypes. Floats are stored as
    /// their raw bit patterns in an Int6 bootstrap value.
    /// </summary>
    private void SaveSpatialGridConfig(SpatialGridConfig config)
    {
        MMF.Bootstrap.Set(BK_SpatialGridConfig, BootstrapDictionary.Value.FromInt6(
            BitConverter.SingleToInt32Bits(config.WorldMin.X),
            BitConverter.SingleToInt32Bits(config.WorldMin.Y),
            BitConverter.SingleToInt32Bits(config.WorldMax.X),
            BitConverter.SingleToInt32Bits(config.WorldMax.Y),
            BitConverter.SingleToInt32Bits(config.CellSize),
            BitConverter.SingleToInt32Bits(config.MigrationHysteresisRatio)));
        MMF.SaveBootstrap();
    }

    /// <summary>Reads the persisted <see cref="SpatialGridConfig"/> written by <see cref="SaveSpatialGridConfig"/>; <see langword="false"/> when none was persisted.</summary>
    private bool TryLoadSpatialGridConfig(out SpatialGridConfig config)
    {
        config = default;
        if (!MMF.Bootstrap.TryGet(BK_SpatialGridConfig, out var v))
        {
            return false;
        }
        config = new SpatialGridConfig(
            new Vector2(BitConverter.Int32BitsToSingle(v.GetInt()), BitConverter.Int32BitsToSingle(v.GetInt(1))),
            new Vector2(BitConverter.Int32BitsToSingle(v.GetInt(2)), BitConverter.Int32BitsToSingle(v.GetInt(3))),
            BitConverter.Int32BitsToSingle(v.GetInt(4)),
            BitConverter.Int32BitsToSingle(v.GetInt(5)));
        return true;
    }

    /// <summary>
    /// Load spatial index from BootstrapDictionary and attach to the ComponentTable.
    /// Called during database reopen for components with [SpatialIndex].
    /// </summary>
    private void LoadSpatialBootstrap(ComponentTable table)
    {
        var key = $"spatial.{table.Definition.Name}";
        if (!MMF.Bootstrap.TryGet(key, out var val))
        {
            return; // No spatial index persisted (new attribute added after last save)
        }

        var treeSPI = val.GetInt();
        var backPtrSPI = val.GetInt(1);
        var variantStride = val.GetInt(2);

        var variant = (SpatialVariant)(variantStride & 0x0F);
        var mode = (SpatialMode)((variantStride >> 4) & 0x0F);
        var stride = variantStride >> 8;
        var descriptor = SpatialNodeDescriptor.FromVariant(variant, stride);

        var treeSegment = MMF.LoadChunkBasedSegment(treeSPI, descriptor.Stride);
        var backPtrSegment = MMF.LoadChunkBasedSegment(backPtrSPI, 8);

        // Load Layer 1 occupancy hashmap if persisted (Int5[4] > 0)
        PagedHashMap<long, int, PersistentStore> occupancyMap = null;
        var hmSPI = val.GetInt(4);
        if (hmSPI > 0)
        {
            var hmStride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
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
            // Creating path: allocate the registry segment. AllocateSegment clamps to the 2-page minimum (directory-only
            // root, v4), so the root holds the page directory and the entries live on the data page(s) — 200 per page.
            var cs = MMF.CreateChangeSet();
            var segment = MMF.AllocateSegment(PageBlockType.None, 1, cs, StorageSegmentKind.System);

            // Clear the data pages so all entries start as Free (State = 0). With a directory-only root the registry entries
            // live on the data pages (segment page 1+), not the root — clear each of them.
            for (int sp = 1; sp < segment.Length; sp++)
            {
                var page = segment.GetPageExclusive(sp, epoch, out var memPageIdx);
                cs.AddByMemPageIndex(memPageIdx);
                page.RawData<byte>().Clear();
                MMF.UnlatchPageExclusive(memPageIdx);
            }

            // Write SPI to root header
            MMF.RequestPageEpoch(0, epoch, out _);
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
            var checkpointLSN = DurabilityWatermarks.ReadCheckpointLsn(MMF);
            // Clean-shutdown HEAD marker (see field docs): capture both LSNs now; InitializeArchetypes decides trust.
            _cleanShutdownAtOpen = DurabilityWatermarks.ReadCleanShutdown(MMF);
            _checkpointLsnAtOpen = checkpointLSN;
            var segment = MMF.GetSegment(spi);
            UowRegistry = new UowRegistry(segment, MMF, EpochManager, MemoryAllocator, this);

            var walDir = _options.Wal?.WalDirectory;
            if (walDir != null && Directory.Exists(walDir) && Directory.GetFiles(walDir, "*.wal").Length > 0)
            {
                // A crash left a WAL window. Gate the crash-path secondary-index clear+rebuild (RB-01) on this, captured HERE at open — before component
                // registration builds the ComponentTables — so the clear in BuildIndexedFieldInfo sees it. RunWalV2Recovery reads the same flag for the
                // matching Phase-5 rebuild, so clear and rebuild always agree.
                WalFilesPresentAtOpen = true;

                // Two-phase WAL recovery: LoadFromDiskRaw preserves Pending entries for WAL cross-referencing
                UowRegistry.LoadFromDiskRaw();
                // Reuse the injected WAL IO when present (same backend that wrote the segments reads them back); otherwise a throwaway production IO.
                // Critical: when injected we must NOT dispose it here — InitializeWalManager (later in this ctor) reuses the same instance (R6).
                var recoveryFileIO = _injectedWalIo ?? new WalFileIO();
                try
                {
                    using var recovery = new WalRecovery(recoveryFileIO, walDir, MMF);
                    // Pass null for dbe: replay is deferred until component tables are registered (system schema auto-loading, #57)
                    // Open-time instrumentation (#diagnose-open): the WAL scan reads every retained segment, so its cost is
                    // O(accumulated WAL since last checkpoint) — a candidate contributor to a slow open. Time it.
                    var walStart = Stopwatch.GetTimestamp();
                    _lastRecoveryResult = recovery.Recover(UowRegistry, checkpointLSN, null);
                    var walMs = (Stopwatch.GetTimestamp() - walStart) * 1000.0 / Stopwatch.Frequency;
                    long walBytes = 0;
                    foreach (var f in Directory.GetFiles(walDir, "*.wal"))
                    {
                        walBytes += new FileInfo(f).Length;
                    }
                    LogWalRecoveryTiming(walMs, walBytes);
                }
                finally
                {
                    if (_injectedWalIo == null)
                    {
                        recoveryFileIO.Dispose();
                    }
                }
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

    // Type→typed-delegate dispatch table replacing MakeGenericType+Activator for ComponentCollection<T> backing stores (AOT blocker B2, #409 §1 / #514 Phase 6).
    // The generated [ModuleInitializer] registers each ComponentCollection<T> element type here — T is known at compile time, so the closed generic is
    // JIT/AOT-resolvable with no runtime code generation. Process-static (the delegate takes the engine as an argument, so it is engine-independent).
    // Read during ComponentTable build; reflection is used only as a fallback for non-registered element types.
    //
    // Weak-keyed (ConditionalWeakTable, not ConcurrentDictionary): the module-init registers eagerly at assembly load, so a collectible-ALC schema's element
    // types must NOT be pinned by this process-static (design §5.2 hard rule / AC5.4 — a strong Type key would leak the ALC). The entry lives exactly as long as
    // the element Type does; the factory delegate references T only as an ephemeron value, which does not keep the weak key alive.
    private static readonly ConditionalWeakTable<Type, Func<DatabaseEngine, ChangeSet, VariableSizedBufferSegmentBase<PersistentStore>>> CollectionVsbsFactories
        = new();

    /// <summary>
    /// Registers the AOT-safe backing-store factory for a <see cref="ComponentCollection{T}"/> element type. Called by source-generated component schema
    /// providers (feature #514) for each collection field — the element type is a compile-time generic argument, so no <c>MakeGenericType</c>/<c>Activator</c>
    /// is needed. Idempotent.
    /// </summary>
    /// <typeparam name="T">The collection's element type.</typeparam>
    public static void RegisterComponentCollectionFactory<T>() where T : unmanaged =>
        CollectionVsbsFactories.AddOrUpdate(typeof(T), static (engine, changeSet) => engine.GetComponentCollectionVSBS<T>(changeSet));

    /// <summary>
    /// Finalizes an <c>[Archetype]</c> into the process-global catalog (feature #514 Phase 5). Called by the per-assembly generated <c>[ModuleInitializer]</c> for
    /// every archetype at assembly load — the barrier that replaces the manual <c>Archetype&lt;T&gt;.Touch()</c> startup calls. Runs the archetype's static ctor
    /// (declaring its <see cref="Comp{T}"/> components) then finalizes it and its parent chain. Idempotent — an already-finalized archetype is a no-op. Public
    /// because the generated registrar lives in the consumer's own assembly and cannot reach engine internals.
    /// </summary>
    /// <param name="archetypeType">The <c>[Archetype]</c> class type to finalize.</param>
    public static void RegisterArchetype(Type archetypeType) => ArchetypeRegistry.EnsureFinalized(archetypeType, fromBarrier: true);

    internal VariableSizedBufferSegment<T, PersistentStore> GetComponentCollectionVSBS<T>() where T : unmanaged => GetComponentCollectionVSBS<T>(null);

    internal unsafe VariableSizedBufferSegment<T, PersistentStore> GetComponentCollectionVSBS<T>(ChangeSet changeSet) where T : unmanaged =>
        (VariableSizedBufferSegment<T, PersistentStore>)_componentCollectionVSBSByType.GetOrAdd(typeof(T),
            _ => new VariableSizedBufferSegment<T, PersistentStore>(GetComponentCollectionSegment(sizeof(T), changeSet)));

    internal VariableSizedBufferSegmentBase<PersistentStore> GetComponentCollectionVSBS(Type itemType, ChangeSet changeSet = null)
    {
        // Preferred path: a source-generated provider registered an AOT-safe factory for this element type (T known at compile time).
        if (CollectionVsbsFactories.TryGetValue(itemType, out var factory))
        {
            return factory!(this, changeSet);
        }

        // Fallback for component types without a source-generated schema provider — constructs the closed generic reflectively (not AOT-safe; see attribute).
        return CreateComponentCollectionVsbsReflective(itemType, changeSet);
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "Constructs VariableSizedBufferSegment<T, PersistentStore> via MakeGenericType for a runtime element type. Only reached for component types without a "
        + "source-generated schema provider; generated components register an AOT-safe factory via RegisterComponentCollectionFactory<T>().")]
    private VariableSizedBufferSegmentBase<PersistentStore> CreateComponentCollectionVsbsReflective(Type itemType, ChangeSet changeSet) =>
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
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride, null, 
                StorageSegmentKind.ComponentCollection));

    unsafe internal ChunkBasedSegment<PersistentStore> GetComponentCollectionSegment(int itemSize, ChangeSet changeSet = null) =>
        _componentCollectionSegmentByStride.GetOrAdd(
            RoundToStandardStride(Math.Max(itemSize * ComponentCollectionItemCountPerChunk, sizeof(VariableSizedBufferRootHeader))),
            stride => MMF.AllocateChunkBasedSegment(PageBlockType.None, ComponentCollectionSegmentStartingSize, stride, changeSet, 
                StorageSegmentKind.ComponentCollection));

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

        // AssemblyR1 — the schema-assembly manifest. Registered after _componentsTable so its own ComponentR1 row persists during registration. Its rows are
        // populated lazily as user components/archetypes are persisted (system components are core → AssemblyId 0, no rows).
        RegisterComponentFromAccessor<AssemblyR1>(cs);
        _assembliesTable = GetComponentTable<AssemblyR1>();

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
        bootstrap.Set(BK_SysAssemblyR1, BootstrapDictionary.Value.FromInt4(
            _assembliesTable.ComponentSegment.RootPageIndex,
            _assembliesTable.CompRevTableSegment.RootPageIndex,
            _assembliesTable.DefaultIndexSegment.RootPageIndex,
            _assembliesTable.String64IndexSegment.RootPageIndex));
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
            AssemblyId          = GetOrCreateAssemblyId(definition.POCOType.Assembly, cs),
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
    /// Returns the AssemblyR1 row id (chunkId) for <paramref name="asm"/>, creating the row on first use. The core engine assembly is excluded (returns 0) —
    /// it is always loaded by any host, so it never belongs in the manifest, and excluding it also avoids a system-component bootstrap self-reference. Dedups on
    /// simple name via <see cref="_assemblyIdByName"/> (seeded on open), so the same assembly is persisted once. Rides on the caller's <paramref name="cs"/>.
    /// </summary>
    private ushort GetOrCreateAssemblyId(Assembly asm, ChangeSet cs)
    {
        if (asm == null || asm == typeof(DatabaseEngine).Assembly)
        {
            return 0; // core / implicit — never recorded in the manifest
        }

        _assemblyIdByName ??= new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        _persistedAssemblies ??= new Dictionary<ushort, (int, AssemblyR1)>();

        var an = asm.GetName();
        var name = an.Name ?? asm.FullName ?? "";
        if (_assemblyIdByName.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (_assembliesTable == null)
        {
            return 0; // pre-manifest database (file written before AssemblyR1 existed) — cannot record; degrade gracefully rather than fault
        }

        var v = an.Version ?? new Version(0, 0, 0, 0);
        var row = new AssemblyR1
        {
            SimpleName     = (String64)name,
            VerMajor       = v.Major,
            VerMinor       = v.Minor,
            VerBuild       = v.Build < 0 ? 0 : v.Build,
            VerRevision    = v.Revision < 0 ? 0 : v.Revision,
            PublicKeyToken = TokenToULong(an.GetPublicKeyToken()),
        };

        var chunkId = SystemCrud.Create(_assembliesTable, ref row, EpochManager, cs);
        var id = (ushort)chunkId;
        _assemblyIdByName[name] = id;
        _persistedAssemblies[id] = (chunkId, row);
        return id;
    }

    /// <summary>Packs an 8-byte public-key-token little-endian into a u64; 0 for an unsigned (empty/null) token.</summary>
    internal static ulong TokenToULong(byte[] token) =>
        token is { Length: 8 } ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(token) : 0UL;

    /// <summary>Unpacks a u64 public-key-token back into 8 little-endian bytes; empty array for 0 (unsigned).</summary>
    internal static byte[] ULongToToken(ulong token)
    {
        if (token == 0)
        {
            return [];
        }
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, token);
        return b;
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

        // AssemblyR1 — the schema-assembly manifest. Loaded eagerly here (like ComponentR1, unlike ArchetypeR1) so GetRequiredAssemblies works on a schemaless
        // open. Absent on databases written before the manifest existed — then the manifest stays empty and the open is simply schemaless.
        _persistedAssemblies = new Dictionary<ushort, (int, AssemblyR1)>();
        _assemblyIdByName = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        if (bootstrap.ContainsKey(BK_SysAssemblyR1))
        {
            DBD.CreateFromAccessor<AssemblyR1>();
            var assemblyDef = DBD.GetComponent(AssemblyR1.SchemaName, 1);
            var asmSPIs = bootstrap.Get(BK_SysAssemblyR1);
            _assembliesTable = new ComponentTable(this, assemblyDef, this, asmSPIs.GetInt(), asmSPIs.GetInt(1), asmSPIs.GetInt(2), asmSPIs.GetInt(3));
            _componentTableByType.TryAdd(typeof(AssemblyR1), _assembliesTable);

            var asmWalTypeId = (ushort)_assembliesTable.ComponentSegment.RootPageIndex;
            _assembliesTable.WalTypeId = asmWalTypeId;
            _componentTableByWalTypeId.TryAdd(asmWalTypeId, _assembliesTable);

            var asmSeg = _assembliesTable.ComponentSegment;
            var asmCapacity = asmSeg.ChunkCapacity;
            for (var chunkId = 1; chunkId < asmCapacity; chunkId++)
            {
                if (!asmSeg.IsChunkAllocated(chunkId))
                {
                    continue;
                }
                if (SystemCrud.Read(_assembliesTable, chunkId, out AssemblyR1 asm, EpochManager))
                {
                    var id = (ushort)chunkId;
                    _persistedAssemblies[id] = (chunkId, asm);
                    _assemblyIdByName[asm.SimpleName.AsString] = id;
                }
            }
        }

        // Load the ComponentCollection segment for FieldR1
        var fieldCollectionSPI = bootstrap.GetInt(BK_CollectionFieldR1);
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

        // Load every persisted component-collection segment into the pool (keyed by stride) so later accesses — ArchetypeR1.ComponentNames, user component
        // collections — reload the existing segment instead of allocating a fresh one (which would orphan the original). Runs before any collection is touched.
        var collectionCount = bootstrap.GetInt(BK_CollectionCount);
        for (var i = 0; i < collectionCount; i++)
        {
            if (!bootstrap.TryGet($"collection.{i}", out var cv))
            {
                continue;
            }
            var collectionStride = cv.GetInt();
            var collectionSPI = cv.GetInt(1);
            if (collectionSPI != 0 && !_componentCollectionSegmentByStride.ContainsKey(collectionStride))
            {
                _componentCollectionSegmentByStride.TryAdd(collectionStride, MMF.LoadChunkBasedSegment(collectionSPI, collectionStride));
            }
        }

        // Read all ComponentR1 entries by scanning ComponentSegment allocated chunks
        _persistedComponents = new Dictionary<string, (int, ComponentR1)>();
        _persistedFieldsByComponent = new Dictionary<string, FieldR1[]>();
        {
            var segment = _componentsTable.ComponentSegment;
            var capacity = segment.ChunkCapacity;
            for (var chunkId = 1; chunkId < capacity; chunkId++)
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

        // Persist every component-collection segment (stride → root page). Only FieldR1 had a dedicated key before; the rest (e.g. the String64 collection
        // backing ArchetypeR1.ComponentNames) were re-allocated fresh on reopen, orphaning the originals — a page leak that also left those pages Unknown in
        // storage introspection. Persisting the whole pool lets the reopen reload them in place.
        var collections = _componentCollectionSegmentByStride;
        MMF.Bootstrap.SetInt(BK_CollectionCount, collections.Count);
        var collectionIndex = 0;
        foreach (var kv in collections)
        {
            MMF.Bootstrap.Set($"collection.{collectionIndex}", BootstrapDictionary.Value.FromInt2(kv.Key, kv.Value.RootPageIndex));
            collectionIndex++;
        }

        MMF.SaveBootstrap(cs);

        MMF.UnlatchPageExclusive(memPageIdx);

        cs.SaveChanges();
        MMF.FlushToDisk();
    }

    /// <summary>
    /// Records the clean-shutdown flag so the next open can trust the persisted Versioned-component HEAD values and skip
    /// the O(entities) <see cref="ArchetypeClusterState.RebuildVersionedHeadFromChain"/> walk. Sets
    /// <see cref="BK_CleanShutdown"/> = 1 and fsyncs it on its own. The flag is deliberately NOT keyed on the checkpoint LSN watermark
    /// (<see cref="DurabilityWatermarks.ReadCheckpointLsn"/>): a bulk-generated DB closes cleanly with CheckpointLSN == 0 yet its
    /// HEADs are current in the data file, so trust must not depend on the LSN value.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="Dispose"/> STRICTLY AFTER <see cref="PersistEngineState"/> has flushed all dirty data pages,
    /// in its own <c>FlushToDisk</c> — never bundled with the data flush. This ordering is the safety contract: the flag
    /// is only durable once every cluster page whose HEADs it vouches for is already durable, so a torn close leaves the
    /// flag unwritten and the next open conservatively rebuilds. See claude/rules/durability.md (CS-01).
    /// </remarks>
    private void MarkCleanShutdown()
    {
        DurabilityWatermarks.SetCleanShutdown(MMF, true);
        var checkpointLsn = DurabilityWatermarks.ReadCheckpointLsn(MMF);
        LogCleanShutdownMarked(checkpointLsn);
        LogWalWatermarksSnapshot("close", checkpointLsn);
    }

    /// <summary>
    /// Diagnostic (issue: bulk-generated DBs leave a 640 MiB WAL that never recycles). Snapshots the WAL LSN watermarks
    /// and segment count so a single open/close pair reveals WHY: low currentLSN ⇒ empty pre-allocated segments (trim
    /// problem); high currentLSN with low checkpointLSN ⇒ records written but never made durable (reclaim-gate problem).
    /// Reads are cheap and WalManager is alive at both call sites (open: post-ctor; close: before WalManager.Dispose).
    /// </summary>
    private void LogWalWatermarksSnapshot(string phase, long checkpointLsn)
    {
        var wal = WalManager;
        if (wal?.SegmentManager == null)
        {
            return;
        }
        LogWalWatermarks(
            phase,
            wal.CommitBuffer?.NextLsn ?? 0,
            wal.DurableLsn,
            checkpointLsn,
            wal.SegmentManager.SealedSegmentCount,
            wal.SegmentManager.TotalWalBytes);
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
        var anyUpdated = false;

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

            if (!_persistedArchetypes.TryGetValue(meta.ArchetypeType.Name, out var persisted))
            {
                continue;
            }

            var arch = persisted.Arch;
            var newEntityMapSpi = state.EntityMap.Segment.RootPageIndex;
            var newClusterSpi = state.ClusterState?.ClusterSegment?.RootPageIndex ?? 0;
            var newNextKey = Interlocked.Read(ref state.NextEntityKey);

            // Skip archetypes whose persisted state is already current. The segment SPIs are stable once allocated, so a steady-state checkpoint with no
            // spawns persists nothing — this is what makes it cheap enough to run at EVERY checkpoint (#395), not just at clean shutdown.
            if (arch.EntityMapSPI == newEntityMapSpi && arch.ClusterSegmentSPI == newClusterSpi && arch.NextEntityKey == newNextKey)
            {
                continue;
            }

            arch.EntityMapSPI = newEntityMapSpi;
            arch.ClusterSegmentSPI = newClusterSpi;
            arch.NextEntityKey = newNextKey;

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
            // refresh the cache so the next checkpoint's skip-check sees the persisted values
            _persistedArchetypes[meta.ArchetypeType.Name] = (persisted.ChunkId, arch);
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

        for (var chunkId = 1; chunkId < capacity; chunkId++)
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

    /// <summary>
    /// Registers component type <typeparamref name="T"/> with the engine: builds its schema definition from the component accessor, validates it against any
    /// persisted schema for the same component name, and creates or loads the backing <see cref="ComponentTable"/>.
    /// </summary>
    /// <remarks>
    /// When a persisted schema exists for this component, <paramref name="schemaValidation"/> governs how differences are reconciled. A Transient component may
    /// not declare a <c>ComponentCollection</c> field — that combination is rejected at registration.
    /// </remarks>
    /// <typeparam name="T">A closed unmanaged value type tagged with <c>[Component]</c>.</typeparam>
    /// <param name="changeSet">Optional change set to enlist the registration writes in.</param>
    /// <param name="schemaValidation">How a persisted schema is reconciled with the runtime type; default <see cref="SchemaValidationMode.Enforce"/>.</param>
    /// <param name="storageModeOverride">Optional <see cref="StorageMode"/> overriding the mode declared on the component.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> when the component definition could not be built.</returns>
    /// <exception cref="InvalidOperationException">A Transient component declares a <c>ComponentCollection</c> field.</exception>
    public bool RegisterComponentFromAccessor<T>(ChangeSet changeSet = null, SchemaValidationMode schemaValidation = SchemaValidationMode.Enforce,
        StorageMode? storageModeOverride = null) where T : unmanaged
    {
        // Track this component Type for the registry lifecycle pairing in Dispose. Adding even on early-return / failure branches below is safe:
        // UnregisterEngineUse is idempotent on Types it doesn't know about, and any Type the engine touched MAY have ended up in
        // `ArchetypeRegistry.ComponentTypeIds` via the static-constructor + `DeclareComponent` cascade before this method's body inspected anything.
        _registeredComponentTypes.Add(typeof(T));

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

        // Transient ComponentCollection is not supported (out of scope; its buffers live in a persistent VSBS while the component is heap-volatile, which would
        // orphan them on restart). Fail fast at registration rather than leaking silently. Versioned and SingleVersion ComponentCollection are supported.
        if (storageMode == StorageMode.Transient)
        {
            foreach (var field in definition.FieldsByName.Values)
            {
                if (field.Type == FieldType.Collection)
                {
                    throw new InvalidOperationException(
                        $"Component '{definition.Name}' is Transient but declares a ComponentCollection field '{field.Name}'. " +
                        "ComponentCollection is only supported on Versioned and SingleVersion components.");
                }
            }
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
                        changeSet: migrationChangeSet, restoreCollectionInfo: true);
                }
                else
                {
                    componentTable = new ComponentTable(this, definition, this, persisted.Comp.ComponentSPI, persisted.Comp.VersionSPI, persisted.Comp.DefaultIndexSPI,
                        persisted.Comp.String64IndexSPI, persisted.Comp.TailIndexSPI, storageMode: persistedMode, newIndexFieldIds: newIndexFieldIds,
                        changeSet: migrationChangeSet, restoreCollectionInfo: true);
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

    /// <summary>Returns the <see cref="ComponentTable"/> registered for <typeparamref name="T"/>, or <see langword="null"/> if none is registered.</summary>
    /// <typeparam name="T">The registered unmanaged component type.</typeparam>
    public ComponentTable GetComponentTable<T>() where T : unmanaged => GetComponentTable(typeof(T));

    /// <summary>Returns the <see cref="ComponentTable"/> registered for <paramref name="type"/>, or <see langword="null"/> if it is not registered.</summary>
    /// <param name="type">The registered component type.</param>
    public ComponentTable GetComponentTable(Type type) => _componentTableByType.GetValueOrDefault(type);

    /// <summary>
    /// Looks up a <see cref="ComponentTable"/> by its WAL type ID (derived from <see cref="LogicalSegment{PersistentStore}.RootPageIndex"/>).
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

        // Open-time latency instrumentation (#diagnose-open): the three reopen rebuilds below are O(entities) and run on
        // EVERY open (their state is intentionally not persisted — ADR-045). Accumulate per-phase elapsed across the
        // per-archetype loop and log one summary at the end, so a slow open shows exactly where the time went. The
        // Stopwatch.GetTimestamp() reads are ~nanoseconds — negligible against the work they bracket.
        var initStart = Stopwatch.GetTimestamp();
        long cellStateTicks = 0;
        long clusterAabbTicks = 0;
        long versionedHeadTicks = 0;

        // Clean-shutdown HEAD fast path (see _headsTrusted field docs): trust the persisted cluster-slot HEADs — and so
        // skip the O(entities) RebuildVersionedHeadFromChain below — iff the last close set the clean-shutdown flag AND no
        // component migrated this session (a migration changes cluster layout, so those HEADs must be rebuilt). The flag
        // is independent of CheckpointLSN, so a bulk-generated DB (CheckpointLSN == 0) is trusted too. Then durably clear
        // the flag, BEFORE any mutation, so a crash this session forces a rebuild on the next open — the real crash-safety.
        LastOpenVersionedHeadRebuildCount = 0;
        _headsTrusted = _cleanShutdownAtOpen
            && (_migratedComponents == null || _migratedComponents.Count == 0);
        if (DurabilityWatermarks.ReadCleanShutdown(MMF))
        {
            DurabilityWatermarks.SetCleanShutdown(MMF, false);
        }
        LogVersionedHeadReopenDecision(_headsTrusted, _cleanShutdownAtOpen, _checkpointLsnAtOpen);
        LogWalWatermarksSnapshot("open", _checkpointLsnAtOpen);

        // Construct the engine-wide spatial grid. A grid is only required when at least one cluster-eligible archetype has a spatial component (checked
        // per-archetype below). The config is persisted so a generic opener (e.g. the Workbench) that never calls ConfigureSpatialGrid can still reconstruct
        // the grid and fully initialize the cluster-spatial archetypes — otherwise their cluster / entity-map segments stay unattributed in introspection.
        if (_pendingGridConfig.HasValue)
        {
            var gridConfig = _pendingGridConfig.Value;
            _spatialGrid = new SpatialGrid(gridConfig);
            _pendingGridConfig = null;
            if (!MMF.Bootstrap.ContainsKey(BK_SpatialGridConfig))
            {
                SaveSpatialGridConfig(gridConfig);
            }
        }
        else if (TryLoadSpatialGridConfig(out var persistedGridConfig))
        {
            _spatialGrid = new SpatialGrid(persistedGridConfig);
        }

        // Ensure ArchetypeR1 is registered in this session. On a new database CreateSystemSchemaR1 already registered it; on reopen LoadSystemSchemaR1 stops
        // after ComponentR1 + SchemaHistoryR1 (ArchetypeR1 is treated as a regular user-visible system component), so we pick it up here via the standard
        // registration path — which reuses the persisted SPIs via _persistedComponents.
        if (GetComponentTable<ArchetypeR1>() == null)
        {
            RegisterComponentFromAccessor<ArchetypeR1>();
        }

        // Load persisted archetype schemas for validation (keyed by name — reopen re-match is by name)
        _persistedArchetypes ??= new Dictionary<string, (int, ArchetypeR1)>();
        LoadPersistedArchetypes();

        // Allocate per-engine state array indexed by per-process catalog id, plus the per-DB routing tables. Fixed sizing (RoutingTableSize) so a concurrent
        // registration in a peer engine can never overflow these — removes Face A's IndexOutOfRange sizing coupling.
        _archetypeStates = new ArchetypeEngineState[RoutingTableSize];
        _metaByRouting = new ArchetypeMetadata[RoutingTableSize];
        _stateByRouting = new ArchetypeEngineState[RoutingTableSize];
        _routingByCatalog = new ushort[RoutingTableSize];
        Array.Fill(_routingByCatalog, NoRoutingId);

        // Resume the routing-id counter above the max already persisted, so archetypes new since the last open get fresh, non-colliding ids while existing
        // ones keep the routing id embedded in their on-disk EntityIds. Routing id 0 is RESERVED (mirrors the legacy author-set ArchetypeId invariant): an
        // all-zero EntityId must remain uniquely EntityId.Null, and engine code uses `entityId.ArchetypeId == 0` as a null/invalid sentinel. So ids start at 1.
        _nextRoutingId = 1;
        foreach (var kv in _persistedArchetypes)
        {
            if (kv.Value.Arch.RoutingId >= _nextRoutingId)
            {
                _nextRoutingId = (ushort)(kv.Value.Arch.RoutingId + 1);
            }
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            // Connect slots to ComponentTables — skip archetypes with unregistered component types
            if (meta._slotToComponentType == null || meta.ComponentCount == 0)
            {
                continue;
            }

            var slotToTable = new ComponentTable[meta.ComponentCount];
            var allComponentsRegistered = true;
            for (var slot = 0; slot < meta.ComponentCount; slot++)
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
            var isClusterEligible = true;
            var hasClusterIndexableFields = false;  // Non-Transient indexed fields (for per-archetype cluster B+Trees)
            var hasSpatialField = false;
            var hasSvSlot = false;
            var hasTransientSlot = false;
            ushort versionedSlotMask = 0;
            ushort transientSlotMask = 0;
            for (var slot = 0; slot < meta.ComponentCount; slot++)
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
                var multipleIndexedFieldCount = 0;
                for (var slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = slotToTable[slot];
                    componentSizes[slot] = table.Definition.ComponentStorageSize;
                    // Count AllowMultiple indexed fields for the cluster tail elementId storage. Only non-Transient slots participate in cluster B+Tree
                    // indexing (Transient indexed fields stay on the legacy per-entity path — see the eligibility check above). Mirror that gate here so the
                    // tail is sized correctly.
                    if (table.StorageMode != StorageMode.Transient && table.IndexedFieldInfos != null)
                    {
                        for (var fi = 0; fi < table.IndexedFieldInfos.Length; fi++)
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
            var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(meta._entityRecordSize);

            // Skip O(1) EntityMap reopen if any of this archetype's component tables underwent migration.
            // Migration creates new segments with preserved chunk IDs, but the persisted EntityMap
            // points to old chunk IDs that may not be valid in the context of the new revision chain layout.
            var hasMigratedSlot = false;
            if (_migratedComponents != null)
            {
                for (var slot = 0; slot < meta.ComponentCount && !hasMigratedSlot; slot++)
                {
                    hasMigratedSlot = _migratedComponents.Contains(slotToTable[slot].Definition.Name);
                }
            }

            // Reopen re-match by name (claude/design/Ecs/SourceGeneratedRegistry/04-solution-design.md §7.1):
            // restore this archetype's persisted routing id, or assign a fresh one for a newly-added archetype.
            var hasPersisted = _persistedArchetypes.TryGetValue(meta.ArchetypeType.Name, out var persisted);
            var routingId = hasPersisted ? persisted.Arch.RoutingId : _nextRoutingId++;
            _metaByRouting[routingId] = meta;
            _routingByCatalog[meta.ArchetypeId] = routingId;

            bool isFreshAllocation;
            if (!hasMigratedSlot && hasPersisted && persisted.Arch.EntityMapSPI > 0
                && MMF.TryLoadChunkBasedSegment(persisted.Arch.EntityMapSPI, stride, out var loadedSegment, WalFilesPresentAtOpen))
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
                var segment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, stride, null, StorageSegmentKind.EntityMap);
                _archetypeStates[meta.ArchetypeId] = new ArchetypeEngineState
                {
                    SlotToComponentTable = slotToTable,
                    EntityMap = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, meta._entityRecordSize),
                    NextEntityKey = 0,
                };
                isFreshAllocation = true;
            }

            // Routing-indexed view of the same state object (populated for both reload and fresh paths).
            _stateByRouting[routingId] = _archetypeStates[meta.ArchetypeId];

            // Create or reload ClusterState for cluster-eligible archetypes.
            if (isClusterEligible)
            {
                var isPureTransient = transientSlotMask != 0 && !hasSvSlot && versionedSlotMask == 0;

                if (isFreshAllocation)
                {
                    // PersistentStore segment for SV+V components (null for pure-Transient)
                    ChunkBasedSegment<PersistentStore> clusterSegment = null;
                    if (!isPureTransient)
                    {
                        clusterSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 4, meta.ClusterLayout.ClusterStride, null, 
                            StorageSegmentKind.Cluster);
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
                else if (_persistedArchetypes.TryGetValue(meta.ArchetypeType.Name, out var clusterPersisted)
                         && clusterPersisted.Arch.ClusterSegmentSPI > 0)
                {
                    ChunkBasedSegment<PersistentStore> loadedCluster = null;
                    var loaded = !isPureTransient && MMF.TryLoadChunkBasedSegment(
                        clusterPersisted.Arch.ClusterSegmentSPI, meta.ClusterLayout.ClusterStride, out loadedCluster, WalFilesPresentAtOpen);

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
                        var fallbackSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, meta.ClusterLayout.ClusterStride, null, 
                            StorageSegmentKind.Cluster);
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

                // Build the SV ComponentCollection descriptor so destroy can release CC buffers held in cluster slots (SV CC has no revision chain — the slot
                // is the sole owner). No-op for archetypes without an SV CC field.
                _archetypeStates[meta.ArchetypeId].ClusterState?.InitializeCollections(slotToTable);

                // Initialize per-archetype B+Tree indexes for cluster archetypes with indexed fields.
                if (meta.HasClusterIndexes)
                {
                    var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
                    var changeSet = MMF.CreateChangeSet();
                    try
                    {
                        // Try to load persisted per-archetype index segment
                        var loadIndexes = false;
                        ChunkBasedSegment<PersistentStore> indexSegment;
                        var indexKey = $"clusterindex.{meta.ArchetypeId}";
                        var indexSPI = !isFreshAllocation ? MMF.Bootstrap.GetInt(indexKey) : 0;
                        if (indexSPI > 0 && MMF.TryLoadChunkBasedSegment(indexSPI, 256 /* sizeof(Index64Chunk) */, out var loadedIdx))
                        {
                            indexSegment = loadedIdx;
                            loadIndexes = true;
                        }
                        else
                        {
                            indexSegment = MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, 256 /* sizeof(Index64Chunk) */, null, 
                                StorageSegmentKind.Index);
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
                        for (var slot = 0; slot < meta.ComponentCount; slot++)
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
                        for (var slot = 0; slot < meta.ComponentCount; slot++)
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
                            var cellStart = Stopwatch.GetTimestamp();
                            clusterState.RebuildCellState(_spatialGrid);
                            cellStateTicks += Stopwatch.GetTimestamp() - cellStart;

                            // Issue #230 Phase 1: rebuild per-cluster AABBs and the per-cell dynamic index from the same entity positions.
                            // Runs AFTER RebuildCellState so ClusterCellMap is populated. Transient state — not persisted, always reconstructed at startup.
                            // No-op for static-mode archetypes (Phase 1 supports dynamic mode only).
                            var aabbStart = Stopwatch.GetTimestamp();
                            clusterState.RebuildClusterAabbs();
                            clusterAabbTicks += Stopwatch.GetTimestamp() - aabbStart;
                        }
                    }
                    finally
                    {
                        changeSet.SaveChanges();
                    }
                }

                // Rebuild Versioned HEAD values in cluster slots from revision chains on reopen.
                // Crash between commit (chain WAL'd) and tick fence (cluster slot WAL'd) can leave stale HEADs — so the
                // rebuild repairs them. On a graceful reopen (_headsTrusted), the persisted cluster slots are already
                // current and this O(entities) walk is pure waste, so it is skipped. See _headsTrusted field docs.
                if (!isFreshAllocation && meta.VersionedSlotMask != 0 && !_headsTrusted)
                {
                    var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
                    if (clusterState != null && clusterState.ActiveClusterCount > 0)
                    {
                        var changeSet = MMF.CreateChangeSet();
                        try
                        {
                            using var vEpoch = EpochGuard.Enter(EpochManager);
                            var vStart = Stopwatch.GetTimestamp();
                            clusterState.RebuildVersionedHeadFromChain(meta, _archetypeStates[meta.ArchetypeId], changeSet);
                            versionedHeadTicks += Stopwatch.GetTimestamp() - vStart;
                            LastOpenVersionedHeadRebuildCount++;
                        }
                        finally
                        {
                            changeSet.SaveChanges();
                        }
                    }
                }
            }
        }

        // Cascade-delete graph is built + validated once, under the registration lock, inside ArchetypeRegistry.Freeze() (called above) — NOT per-engine.
        // The old per-engine rebuild here double-added CascadeTargets on the shared metadata under parallel init (Face B of the flaky race). #514 Phase 3.

        // Rebuild entity maps from persisted ComponentTable data (entities from prior database sessions). On a clean
        // reopen this is the O(1) persisted-EntityMap fast path; it only walks entities for legacy / migrated DBs.
        var entityMapStart = Stopwatch.GetTimestamp();
        RebuildEntityMapsFromPersistedData();
        var entityMapTicks = Stopwatch.GetTimestamp() - entityMapStart;

        // Persist any new archetypes not yet in the database
        PersistNewArchetypes();

        // Open-time breakdown — emitted at Information so a slow open is visible in the Workbench log without a debug
        // build. Each figure is summed across all archetypes; the WAL-recovery cost is logged separately at its own
        // call site (it runs in the engine ctor, before this method).
        var toMs = 1000.0 / Stopwatch.Frequency;
        LogInitArchetypesTiming(
            (Stopwatch.GetTimestamp() - initStart) * toMs,
            versionedHeadTicks * toMs,
            clusterAabbTicks * toMs,
            cellStateTicks * toMs,
            entityMapTicks * toMs);

        // ─── Register this engine's use of every archetype Type currently in the registry ──────────────
        // Snapshot AFTER all archetypes are registered (Touch() / DeclareComponent / EnsureFinalized cascade) so we hold a reference to every Type this engine
        // is now consuming. The matching `Dispose` decrements the same set; the registry releases the Type on refcount=0, which is what lets the owning ALC be
        // GC'd between sessions.
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            _registeredArchetypeTypes.Add(meta.ArchetypeType);
        }
        ArchetypeRegistry.RegisterEngineUse(_registeredArchetypeTypes, _registeredComponentTypes);

        // WAL v2 crash recovery (P1.2): replay committed records that postdate the last checkpoint, now that archetypes,
        // EntityMaps, and the page cache are online — the correct place, unlike the never-wired in-ctor WalRecovery(dbe:null)
        // that runs before component metadata exists (TXW-1). No-op on a clean reopen (the WAL window is empty).
        RunWalV2Recovery();

        // Recovery is now complete — restore the configured CRC verification mode (deferred to RecoveryOnly at open on the crash path, see
        // InitializeCheckpointManager) so normal operation gets on-load corruption detection again.
        if (WalFilesPresentAtOpen)
        {
            MMF.SetPageChecksumVerification(_options.Resources.PageChecksumVerification);
        }

        // Open + recovery (incl. the seal) are done — arm the checkpoint-time SPI persistence (#395 / CK-10). From here every steady-state checkpoint
        // records the per-archetype segment SPIs so a consolidated cluster/EntityMap base is reachable on reopen after a hard crash.
        _archetypeSpiPersistArmed = true;
    }

    /// <summary>
    /// Runs <see cref="RecoveryDriver"/> over the retained WAL segments after archetype initialization. Applies every committed
    /// record past the persisted CheckpointLSN through the engine's own write primitives (P1.2). Guarded on WAL files existing,
    /// so a clean reopen (recycled WAL) skips it entirely.
    /// </summary>
    private void RunWalV2Recovery()
    {
        var walDir = _options.Wal?.WalDirectory;
        if (!WalFilesPresentAtOpen)
        {
            return;
        }

        long checkpointLsn;
        using (EpochGuard.Enter(EpochManager))
        {
            checkpointLsn = DurabilityWatermarks.ReadCheckpointLsn(MMF);
        }

        // Read with a throwaway IO when no backend is injected; the WAL writer's handles coexist (segments open with sharing).
        var walIO = _injectedWalIo ?? new WalFileIO();
        RecoveryDriver.Result result;
        try
        {
            result = new RecoveryDriver().Run(walIO, walDir, this, checkpointLsn);
        }
        finally
        {
            if (_injectedWalIo == null)
            {
                walIO.Dispose();
            }
        }

        LastWalV2RecoveryResult = result;
        LastWalV2RecoveryCheckpointLsn = checkpointLsn;

        // Phase 4 — SCRUB (03-recovery.md §6, D1): now that the WAL window is applied, collapse every Versioned revision chain
        // to its HEAD so the consolidated base carries no pre-crash MVCC history. Runs before the seal so its mutations are
        // consolidated into the data file by the same checkpoint.
        ScrubVersionedChains();

        // Phase 5 — REBUILD (03-recovery.md §7, RB-01): repopulate every Versioned table's secondary indexes from the now-final chain HEADs. The indexes were
        // emptied at open (ComponentTable.BuildIndexedFieldInfo crash path); this rebuild replaces FPI repair of torn checkpointed index pages. Before the seal so
        // the same checkpoint consolidates the rebuilt index pages.
        RebuildSecondaryIndexes();

        // Phase 6 — SUSPECT RESOLUTION (03-recovery.md §9, RB-04): now that derived structures are rebuilt and chains scrubbed, classify every page that failed
        // CRC during recovery (RecoverySuspect mode). Derived/orphaned suspects are already healed (rebuilt / freed by scrub); a suspect page still holding a live
        // primary chunk is unhealable torn data → fail the open loudly. Before the seal so a loud failure aborts before the data file is rewritten.
        ResolveSuspectPrimaryPages();

        SealRecovery(result.MaxLsn, checkpointLsn);

        // Phase 6b — OCCUPANCY RE-DERIVE (03-recovery.md §7, rule CK-09): the occupancy bitmap is a DERIVED structure — post-crash it is never trusted but rebuilt
        // wholesale from the authoritative page ownership. Replaces FPI repair of a torn checkpointed occupancy page and reclaims pages a torn checkpoint leaked. Runs
        // AFTER the seal because the seal checkpoint can still grow segments (e.g. EntityMap bucket pages allocated as it flushes deferred work), so page ownership is
        // final only afterwards. The corrected bitmap is held dirty (DC > 0, so it can't be evicted stale) and consolidated by the next checkpoint / clean shutdown;
        // if this session crashes again first, recovery simply re-derives (idempotent).
        RederiveOccupancyOnCrash();
    }

    /// <summary>
    /// Phase 6b — OCCUPANCY RE-DERIVE (03-recovery.md §7, rule CK-09). Rebuilds the occupancy bitmap from the authoritative page ownership
    /// (<see cref="BuildOwnedPageBitmap"/>) and adopts it wholesale via <see cref="ManagedPagedMMF.RederiveOccupancy"/>. The occupancy bitmap is derived, so a
    /// CRC-torn occupancy page is healed by replacement (the FPI substitute) and any page a torn checkpoint leaked (bit set, no claimant) is reclaimed. Builds the
    /// owned set first (it takes its own short-lived epoch scope for the directory-map walk), then performs the overwrite under a fresh epoch guard so the page writes
    /// see a stable epoch. Crash-path only; runs after the seal (see call site) so it sees the final page ownership, and the dirtied bitmap pages are held dirty until
    /// the next checkpoint / clean shutdown consolidates them.
    /// </summary>
    private void RederiveOccupancyOnCrash()
    {
        if (DisableOccupancyRederiveForTest)
        {
            return;
        }

        var owned = BuildOwnedPageBitmap(out _);

        using var guard = EpochGuard.Enter(EpochManager);
        var changeSet = MMF.CreateChangeSet();
        try
        {
            LastOpenOccupancyRederiveWordsChanged = MMF.RederiveOccupancy(owned, changeSet);
        }
        finally
        {
            changeSet.SaveChanges();
        }
    }

    /// <summary>
    /// Phase 6 — SUSPECT RESOLUTION (03-recovery.md §9, RB-04). Drains the pages that failed CRC during recovery (recorded by <see cref="PagedMMF"/> in
    /// <see cref="PageChecksumVerification.RecoverySuspect"/> mode) and decides each one's fate from the POST-apply/scrub/rebuild state:
    /// <list type="bullet">
    /// <item><b>derived</b> (Index/Spatial/Occupancy) → healed: rebuilt unconditionally (RB-01), so a torn one was discarded.</item>
    /// <item><b>orphaned primary</b> → healed: the entity was re-created in-window and scrub freed the old (torn) chunk, so the page holds no live chunk.</item>
    /// <item><b>live primary</b> → a torn page still backing an allocated chunk is unhealable lost data → <b>fail the open loudly</b> with a diagnostic bundle
    /// (RB-04); never a silent open.</item>
    /// </list>
    /// "Live primary page" is computed forward: every file page that backs an allocated chunk of a primary <see cref="ChunkBasedSegment{TStore}"/> — the same
    /// chunk→page map the rebuild uses. EntityMap pages fall out naturally (their bucket chunks are allocated ⇒ live ⇒ loud-fail; rebuild is deferred).
    /// </summary>
    private void ResolveSuspectPrimaryPages()
    {
        var suspects = MMF.DrainSuspectPages();
        if (suspects.Length == 0)
        {
            return;
        }

        var suspectSet = new HashSet<int>(suspects);
        using var guard = EpochGuard.Enter(EpochManager);

        foreach (var seg in MMF.RegisteredSegments)
        {
            if (IsDerivedSegmentKind(seg.Kind) || seg is not ChunkBasedSegment<PersistentStore> cbs)
            {
                continue; // derived → rebuilt; non-chunk segments carry no live primary chunk addressable this way
            }

            // A rebuilt EntityMap segment (crash path, RebuildEntityMapOnCrash) was discarded by ClearForRebuild and re-derived from authoritative cluster /
            // chain data, so a CRC-torn page on it is already healed — it must not trip the RB-04 loud-fail. (A non-rebuildable EntityMap — a non-cluster
            // archetype with an SV slot — is NOT in this set, so it still loud-fails: never silent-heal to a lossy map.)
            if (seg.Kind == StorageSegmentKind.EntityMap && _crashRebuiltEntityMapSegments.Contains(cbs.RootPageIndex))
            {
                continue;
            }

            var capacity = cbs.ChunkCapacity;
            for (var chunkId = 0; chunkId < capacity; chunkId++)
            {
                if (!cbs.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                var (segPage, _) = cbs.GetChunkLocation(chunkId);
                var filePage = cbs.Pages[segPage];
                if (suspectSet.Contains(filePage))
                {
                    // RB-04: a CRC-failing primary page still backs a live chunk — its content is genuinely lost (not covered/replaced by the recovery window).
                    ThrowHelper.ThrowCorruption(
                        $"{seg.Kind}Segment",
                        filePage,
                        $"suspect {seg.Kind} page {filePage} still backs live chunk {chunkId} — unhealable torn primary data, not covered by the recovery window; "
                        + "failing the open rather than serving corrupt data (RB-04)");
                }
            }
        }

        // Any suspect not matched above is derived (rebuilt) or an orphaned primary page (in-window-replaced, scrub-freed) → healed.
    }

    /// <summary>Page classes whose CRC-failing pages are HEALED by unconditional rebuild during recovery (RB-01) rather than repaired/feared: secondary indexes,
    /// spatial indexes, and the occupancy bitmap. Everything else (component/revision content, EntityMap, collections, cluster, string table, system) is primary —
    /// a CRC failure there is heal-or-loud-fail (RB-04). Post-FPI (increment D) this predicate is the ONLY thing standing between a torn page and silent corruption,
    /// so its boundary is asserted directly by <c>SuspectPageClassification_PartitionsDerivedVsPrimary</c>. Internal for that test.</summary>
    internal static bool IsDerivedSegmentKind(StorageSegmentKind kind)
        => kind is StorageSegmentKind.Index or StorageSegmentKind.Spatial or StorageSegmentKind.Occupancy;

    /// <summary>
    /// Phase 5 — REBUILD (03-recovery.md §7, RB-01). After apply+scrub, rebuild every Versioned table's secondary indexes from the final chain HEADs. The indexes
    /// were emptied at open on the crash path (<see cref="ComponentTable.BuildIndexedFieldInfo"/>), so a torn checkpointed index page is replaced by rebuild rather
    /// than FPI repair. Mirrors <see cref="RebuildEntityMapsFromPersistedData"/>'s archetype/slot walk; a table shared across archetypes accumulates each
    /// archetype's heads into the one (already-empty) index. SingleVersion indexed components are cluster-eligible and rebuilt on the cluster path, not here.
    /// </summary>
    private void RebuildSecondaryIndexes()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var changeSet = MMF.CreateChangeSet();
        try
        {
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                var state = _archetypeStates[meta.ArchetypeId];
                if (state?.SlotToComponentTable == null)
                {
                    continue;
                }

                for (var slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = state.SlotToComponentTable[slot];
                    if (table == null || table.StorageMode != StorageMode.Versioned || table.IndexedFieldInfos.Length == 0)
                    {
                        continue;
                    }

                    var heads = ComponentRevisionManager.EnumerateVersionedChainHeads(table, RoutingIdOf(meta));
                    if (heads.Count > 0)
                    {
                        table.RebuildSecondaryIndexEntriesFromHeads(heads, changeSet);
                    }
                }
            }
        }
        finally
        {
            changeSet.SaveChanges();
        }
    }

    /// <summary>
    /// Phase 6 — SEAL (03-recovery.md §9). After the recovery window has been applied (its pages are dirty in the cache), run one
    /// checkpoint that consolidates them into the data file and advances CheckpointLSN past the window. The cycle's target LSN is
    /// the WAL's <see cref="WalManager.DurableLsn"/>, which is 0 on a freshly-opened writer — so first seed it to the replayed
    /// frontier (which IS durable on disk). The advance lets the now-redundant WAL segments recycle (CK-04), and makes the
    /// recovered state survive a SECOND crash without re-replaying. No-op when nothing past the checkpoint was replayed.
    /// </summary>
    private void SealRecovery(long frontierLsn, long checkpointLsn)
    {
        if (frontierLsn <= checkpointLsn || CheckpointManager == null)
        {
            return;
        }

        WalManager.SeedDurableLsn(frontierLsn);
        CheckpointManager.ForceCheckpoint();
        // A timeout here is non-fatal: the recovered state is already correct in the page cache for this session's reads — it just
        // isn't consolidated to the data file yet, so it falls back to being re-replayed on the next open (soft recovery).
        CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(30));
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
    private void RebuildEntityMapsFromPersistedData()
    {
        using var guard = EpochGuard.Enter(EpochManager);

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var state = _archetypeStates[meta.ArchetypeId];
            if (state?.SlotToComponentTable == null)
            {
                continue;
            }

            // Crash path (03-recovery.md §7): the EntityMap is a derived-on-crash structure. The persisted EntityMap is NOT trusted on a crash — it may be
            // CRC-torn, or stale relative to this session's post-shutdown checkpoints (RebuildEntityMapsFromPersistedData's clean-reopen skip would otherwise
            // keep a stale loaded map and silently drop those entities). Discard it and re-derive every entry from the authoritative source: the cluster
            // occupancy walk for cluster archetypes, the Versioned chain heads for flat archetypes. A NON-rebuildable archetype (a non-cluster archetype that
            // still owns an SV slot) falls through to the clean/legacy path below and keeps the RB-04 loud-fail on a torn EntityMap page (never silent-heal to
            // a lossy map). This runs before WAL apply (RunWalV2Recovery), so every downstream consumer sees a freshly-derived map. (Mixed cluster archetypes:
            // RebuildVersionedHeadFromChain in InitializeArchetypes runs earlier and reads the not-yet-rebuilt EntityMap — harmless on the common
            // no-prior-shutdown crash, where the map is fresh and that pass no-ops; a prior-shutdown mixed-cluster ordering refinement is a documented residual.)
            if (WalFilesPresentAtOpen && IsEntityMapRebuildable(meta) && !DisableEntityMapRebuildForTest)
            {
                RebuildEntityMapOnCrash(meta, state);
                continue;
            }

            // Clean / legacy reopen: skip archetypes that were loaded from a persisted EntityMap segment (O(1) reopen path). BUT: if migration invalidated the
            // EntityMap (hasMigratedSlot → fresh allocation), the EntityMap will be empty despite persisted SPI > 0. Check EntryCount to distinguish.
            if (_persistedArchetypes.TryGetValue(meta.ArchetypeType.Name, out var p) && p.Arch.EntityMapSPI > 0
                && state.EntityMap.EntryCount > 0)
            {
                continue;
            }

            // Flat (legacy / non-cluster) chain-head rebuild, shared with the crash-path rebuild (RebuildEntityMapOnCrash) so the two never drift — the only
            // difference is the insert primitive (plain Insert here vs InsertDuringRebuild after a ClearForRebuild on the crash path).
            var mapCs = MMF.CreateChangeSet();
            BuildFlatEntityMapEntries(meta, state, mapCs, duringRebuild: false);
        }
    }

    /// <summary>
    /// Scan this archetype's Versioned revision chains (<see cref="ComponentRevisionManager.EnumerateVersionedChainHeads"/>) and insert one EntityRecord per
    /// chain head into the EntityMap, keyed by entity key with the chain root as each Versioned slot's location. SV / non-Versioned slots get location 0 (no
    /// chain to recover from). Shared by the clean/legacy reopen path (<see cref="RebuildEntityMapsFromPersistedData"/>, <paramref name="duringRebuild"/> =
    /// false) and the crash-recovery rebuild (<see cref="RebuildEntityMapOnCrash"/>, <paramref name="duringRebuild"/> = true, where the map was just emptied
    /// by <c>ClearForRebuild</c> so the faster split-aware <c>InsertDuringRebuild</c> is used).
    /// </summary>
    private unsafe void BuildFlatEntityMapEntries(ArchetypeMetadata meta, ArchetypeEngineState state, ChangeSet mapCs, bool duringRebuild,
        Dictionary<long, ushort> enabledSnapshot = null)
    {
        var recordBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        // Phase 1: Scan each Versioned slot's CompRevTableSegment to find chain heads. slotMaps[slot] = { EntityPK → compRevFirstChunkId }.
        var slotMaps = new Dictionary<long, int>[meta.ComponentCount];
        var anySlotPopulated = false;

        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = state.SlotToComponentTable[slot];
            if (table?.CompRevTableSegment == null || table.StorageMode != StorageMode.Versioned)
            {
                slotMaps[slot] = null;
                continue;
            }

            var segment = table.CompRevTableSegment;
            if (segment.ChunkCapacity == 0 || segment.AllocatedChunkCount == 0)
            {
                slotMaps[slot] = null;
                continue;
            }

            // Shared two-pass chain-head scan (overflow set → heads), reused by recovery scrub (03-recovery.md §6) so the two never drift. Returns
            // EntityPK → root-chunk-id for this archetype's chains in this Versioned slot.
            slotMaps[slot] = ComponentRevisionManager.EnumerateVersionedChainHeads(table, RoutingIdOf(meta));
            if (slotMaps[slot].Count > 0)
            {
                anySlotPopulated = true;
            }
        }

        if (!anySlotPopulated)
        {
            return;
        }

        // Phase 2: Union all entity PKs across slots, then build + insert one record each.
        var allEntityPKs = new HashSet<long>();
        for (var slot = 0; slot < meta.ComponentCount; slot++)
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
        foreach (var pk in allEntityPKs)
        {
            var entityKey = EntityId.FromRaw(pk).EntityKey;

            var allSlotsPresent = true;
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotMaps[slot] == null)
                {
                    EntityRecordAccessor.SetLocation(recordBuf, slot, 0); // SV / non-Versioned slot — no chain location to recover
                    continue;
                }

                if (!slotMaps[slot].TryGetValue(pk, out var compRevFirstChunkId))
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

            ref var header = ref EntityRecordAccessor.GetHeader(recordBuf);
            header.BornTSN = 0; // Always visible (committed before checkpoint)
            header.DiedTSN = 0; // Live entity
            // Preserve the persisted (non-derivable) EnabledBits when available; fall back to all-enabled only when the entity has no snapshot entry
            // (fresh/legacy rebuild, or a torn EntityMap page). A WAL replay window re-applies any enable/disable that post-dates the snapshot.
            header.EnabledBits = enabledSnapshot != null && enabledSnapshot.TryGetValue(entityKey, out var preservedBits) ? 
                preservedBits : (ushort)((1 << meta.ComponentCount) - 1);

            var mapAccessor = state.EntityMap.Segment.CreateChunkAccessor(mapCs);
            if (duringRebuild)
            {
                state.EntityMap.InsertDuringRebuild(entityKey, recordBuf, ref mapAccessor, mapCs);
            }
            else
            {
                state.EntityMap.Insert(entityKey, recordBuf, ref mapAccessor, mapCs);
            }
            mapAccessor.Dispose();

            if (entityKey > maxEntityKey)
            {
                maxEntityKey = entityKey;
            }
        }

        if (maxEntityKey > 0)
        {
            state.NextEntityKey = maxEntityKey;
        }
    }

    /// <summary>
    /// Root page indexes of EntityMap segments rebuilt from authoritative data during this crash recovery (<see cref="RebuildEntityMapOnCrash"/>). A suspect
    /// (CRC-torn) page on one of these segments is healed — it was discarded by <c>ClearForRebuild</c> and re-derived — so <see cref="ResolveSuspectPrimaryPages"/>
    /// must not loud-fail it (RB-04). Keyed by <see cref="LogicalSegment{TStore}.RootPageIndex"/> (stable across reload) rather than instance identity, since the
    /// segment iterated at resolution may be a different wrapper than the one rebuilt. Populated only on the crash path, read once at suspect resolution.
    /// </summary>
    private readonly HashSet<int> _crashRebuiltEntityMapSegments = new();

    /// <summary>Diagnostic: number of archetypes whose EntityMap was rebuilt on the crash path during the last open. Test-observable genuineness signal.</summary>
    internal int LastOpenCrashEntityMapRebuildCount;

    /// <summary>Diagnostic: number of occupancy L0 words the crash-path re-derive (<see cref="RederiveOccupancyOnCrash"/>) corrected on the last open. Test-observable
    /// genuineness signal — &gt; 0 with FPI disabled proves the re-derive (not FPI) healed the torn / stale occupancy bitmap (CK-09).</summary>
    internal int LastOpenOccupancyRederiveWordsChanged;

    /// <summary>Diagnostic: the <see cref="RecoveryDriver.Result"/> of the last crash-path WAL v2 recovery (design 03 §1: every result field is test-asserted — a
    /// RecordsScanned-only assertion hides a recovery that never applies). Default when no crash recovery ran this open.</summary>
    internal RecoveryDriver.Result LastWalV2RecoveryResult;

    /// <summary>Diagnostic: the checkpoint-LSN threshold used by the last WAL v2 recovery (records at/below it are skipped as already-consolidated). Test-observable so a
    /// regression can assert the recovery window's record LSNs sit ABOVE it (the post-reopen-window-loss class: a reopened session whose record LSNs fall below a prior
    /// session's persisted CheckpointLSN is silently dropped).</summary>
    internal long LastWalV2RecoveryCheckpointLsn;

    /// <summary>Test-only kill switch for the crash-path EntityMap rebuild (genuineness probe): when set, recovery falls back to trusting the persisted EntityMap so a
    /// proof-gate test can confirm the rebuild — not FPI or the loaded map — is what recovers a torn EntityMap.</summary>
    internal static bool DisableEntityMapRebuildForTest;

    /// <summary>Test-only kill switch for the crash-path occupancy re-derive (genuineness probe): when set, recovery trusts the persisted occupancy bitmap so a
    /// proof-gate test can confirm the re-derive — not FPI — is what heals a torn occupancy page (<see cref="RederiveOccupancyOnCrash"/>).</summary>
    internal static bool DisableOccupancyRederiveForTest;

    /// <summary>
    /// Whether this archetype's EntityMap can be fully re-derived from persisted data on a crash. True for cluster archetypes (the cluster slots persist
    /// EntityKeys[N] + EnabledBits[C] + the live OccupancyBits — fully self-describing) and for non-cluster archetypes whose non-Transient slots are all
    /// Versioned (chain heads carry every location). False only for the rare non-cluster archetype that still owns a SingleVersion slot (reachable via a
    /// Transient-*indexed* slot, see InitializeArchetypes cluster-eligibility): its SV slot location has no persisted source, so a torn EntityMap page there
    /// must loud-fail (RB-04) rather than silent-heal to a lossy map. (03-recovery.md §7.)
    /// </summary>
    internal bool IsEntityMapRebuildable(ArchetypeMetadata meta)
    {
        if (meta.IsClusterEligible)
        {
            return true;
        }

        var state = _archetypeStates[meta.ArchetypeId];
        if (state?.SlotToComponentTable == null)
        {
            return false;
        }

        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = state.SlotToComponentTable[slot];
            if (table != null && table.StorageMode == StorageMode.SingleVersion)
            {
                return false; // non-cluster SV slot — unrecoverable location
            }
        }

        return true;
    }

    /// <summary>
    /// Crash-path EntityMap rebuild (03-recovery.md §7): discard the persisted (possibly CRC-torn, FPI-only-protected) EntityMap and re-derive it from the
    /// authoritative source — the cluster occupancy walk for cluster archetypes, the Versioned chain heads for flat archetypes. The EntityMap analogue of the
    /// Phase 2 index clear+rebuild, making the EntityMap a derived-on-crash structure. Runs from <see cref="RebuildEntityMapsFromPersistedData"/> (over every
    /// archetype) before WAL apply, so the applier sees a clean map. Only called for rebuildable archetypes (<see cref="IsEntityMapRebuildable"/>).
    /// </summary>
    private void RebuildEntityMapOnCrash(ArchetypeMetadata meta, ArchetypeEngineState state)
    {
        LastOpenCrashEntityMapRebuildCount++;
        var cs = MMF.CreateChangeSet();
        try
        {
            using var guard = EpochGuard.Enter(EpochManager);

            // EnabledBits are NON-derivable authoritative state (orthogonal to the chain/cluster data the rebuild re-derives), so snapshot them from
            // the persisted EntityMap BEFORE it is discarded — re-deriving the map alone resets them (flat: hardcoded all-enabled; cluster: from the
            // denormalized EnabledBits[C]) and silently loses every enable/disable not re-applied by a WAL replay window. A torn EntityMap page yields
            // garbage keys that won't match the authoritative keys the rebuild looks up, so torn entries simply fall back (WAL-corrected on a hard crash).
            var enabledSnapshot = SnapshotEntityMapEnabledBits(state);

            // Discard the persisted EntityMap (a torn page is reclaimed by bitmap, never parsed) and re-derive every entry from authoritative data.
            state.EntityMap.ClearForRebuild(cs);

            if (meta.IsClusterEligible)
            {
                RebuildClusterEntityMapEntries(meta, state, cs, enabledSnapshot);
            }
            else
            {
                BuildFlatEntityMapEntries(meta, state, cs, duringRebuild: true, enabledSnapshot);
            }

            _crashRebuiltEntityMapSegments.Add(state.EntityMap.Segment.RootPageIndex);
        }
        finally
        {
            cs.SaveChanges();
        }
    }

    /// <summary>
    /// Collects per-entity <c>EnabledBits</c> from the persisted EntityMap (keyed by EntityKey) so the crash rebuild can preserve this non-derivable state.
    /// Best-effort: a torn EntityMap page produces garbage keys that the rebuild's authoritative-key lookup will not match, so those entries fall back.
    /// </summary>
    private static Dictionary<long, ushort> SnapshotEntityMapEnabledBits(ArchetypeEngineState state)
    {
        var snapshot = new Dictionary<long, ushort>();
        if (state?.EntityMap == null || state.EntityMap.EntryCount == 0)
        {
            // Nothing persisted to preserve (e.g. a no-checkpoint crash where the map was never flushed); the WAL replay window is the
            // authoritative source for enabled-bits in that case. Skipping the empty-map walk also avoids perturbing the replay path.
            return snapshot;
        }

        var accessor = state.EntityMap.Segment.CreateChunkAccessor();
        var action = new EnabledBitsSnapshotAction { Snapshot = snapshot };
        state.EntityMap.ForEachEntry(ref accessor, ref action);
        accessor.Dispose();
        return snapshot;
    }

    private struct EnabledBitsSnapshotAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public Dictionary<long, ushort> Snapshot;

        public unsafe bool Process(long key, byte* value)
        {
            Snapshot[key] = EntityRecordAccessor.GetHeader(value).EnabledBits;
            return true;
        }
    }

    /// <summary>
    /// Re-derive a cluster archetype's EntityMap from the cluster segment alone. Walks every live slot of every active cluster (the same occupancy-bit walk
    /// as <see cref="ArchetypeClusterState.RebuildIndexesFromData"/>) and rebuilds the <c>ClusterEntityRecord</c> from self-describing cluster state: the
    /// EntityKey from <c>EntityKeys[slot]</c>, the per-entity enabled mask reconstructed from the per-component <c>EnabledBits[C]</c> bitmaps, and each
    /// Versioned slot's compRevFirstChunkId from the chain-head scan. BornTSN/DiedTSN = 0 (live, committed before checkpoint — same convention as the flat
    /// rebuild). Inserted via the split-aware <c>InsertDuringRebuild</c> into the just-cleared map.
    /// </summary>
    private unsafe void RebuildClusterEntityMapEntries(ArchetypeMetadata meta, ArchetypeEngineState state, ChangeSet cs,
        Dictionary<long, ushort> enabledSnapshot = null)
    {
        var clusterState = state.ClusterState;
        if (clusterState?.ClusterSegment == null)
        {
            return; // pure-Transient cluster — no persistent data to rebuild from
        }

        var layout = clusterState.Layout;
        var slotToVi = layout.SlotToVersionedIndex;

        // Versioned chain heads per slot (compRevFirstChunkId keyed by EntityPK) — the same source the flat rebuild uses for the per-slot location.
        var chainHeads = new Dictionary<long, int>[meta.ComponentCount];
        if (meta.VersionedSlotMask != 0 && slotToVi != null)
        {
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotToVi[slot] < 0)
                {
                    continue;
                }

                var table = state.SlotToComponentTable[slot];
                if (table?.CompRevTableSegment != null && table.StorageMode == StorageMode.Versioned
                    && table.CompRevTableSegment.ChunkCapacity > 0 && table.CompRevTableSegment.AllocatedChunkCount > 0)
                {
                    chainHeads[slot] = ComponentRevisionManager.EnumerateVersionedChainHeads(table, RoutingIdOf(meta));
                }
            }
        }

        var recordBuf = stackalloc byte[ClusterEntityRecordAccessor.RecordSize(meta.VersionedSlotCount)];
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        long maxEntityKey = 0;
        try
        {
            for (var c = 0; c < clusterState.ActiveClusterCount; c++)
            {
                var chunkId = clusterState.ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var entityKey = EntityId.FromRaw(entityPK).EntityKey;

                    ClusterEntityRecordAccessor.InitializeRecord(recordBuf, meta.VersionedSlotCount);
                    ref var header = ref ClusterEntityRecordAccessor.GetHeader(recordBuf);
                    header.BornTSN = 0; // committed before checkpoint → always visible
                    header.DiedTSN = 0; // live (occupancy bit set)

                    // Prefer the preserved (non-derivable) EnabledBits from the persisted EntityMap; otherwise reconstruct the per-entity 16-bit mask from
                    // the cluster's per-component EnabledBits[c] (bit slotIndex set ⇒ component c enabled), written by EntityRef.Enable/Disable. NOTE: the
                    // durable crash-survival of that cluster copy is the open gap tracked in #398 — this fallback is only as good as what was checkpointed.
                    ushort enabledMask;
                    if (enabledSnapshot != null && enabledSnapshot.TryGetValue(entityKey, out var preservedBits))
                    {
                        enabledMask = preservedBits;
                    }
                    else
                    {
                        enabledMask = 0;
                        for (var comp = 0; comp < meta.ComponentCount; comp++)
                        {
                            var compEnabled = *(ulong*)(clusterBase + layout.EnabledBitsOffset(comp));
                            if ((compEnabled & (1UL << slotIndex)) != 0)
                            {
                                enabledMask |= (ushort)(1 << comp);
                            }
                        }
                    }
                    header.EnabledBits = enabledMask;

                    ClusterEntityRecordAccessor.SetClusterChunkId(recordBuf, chunkId);
                    ClusterEntityRecordAccessor.SetSlotIndex(recordBuf, (byte)slotIndex);

                    if (slotToVi != null)
                    {
                        for (var slot = 0; slot < meta.ComponentCount; slot++)
                        {
                            var vi = slotToVi[slot];
                            if (vi < 0)
                            {
                                continue;
                            }

                            var head = 0;
                            chainHeads[slot]?.TryGetValue(entityPK, out head);
                            ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordBuf, vi, head);
                        }
                    }

                    var mapAccessor = state.EntityMap.Segment.CreateChunkAccessor(cs);
                    state.EntityMap.InsertDuringRebuild(entityKey, recordBuf, ref mapAccessor, cs);
                    mapAccessor.Dispose();

                    if (entityKey > maxEntityKey)
                    {
                        maxEntityKey = entityKey;
                    }
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }

        if (maxEntityKey > 0)
        {
            state.NextEntityKey = maxEntityKey;
        }
    }

    /// <summary>
    /// Recovery Phase-4 SCRUB (03-recovery.md §6, D1): after the WAL window has been applied, collapse every Versioned revision chain to its HEAD — the
    /// highest-TSN committed element — freeing all older revisions' content chunks and the chain's overflow table chunks. The history horizon resets at
    /// crash (D1): post-recovery there are no readers of pre-crash snapshots, so no MVCC history is retained. Chain roots (the first chunks the EntityMap
    /// references) are preserved in place, so the EntityMap stays valid and locations are unchanged. Cluster HEAD values are unaffected — scrub keeps the
    /// head's content chunk, so the values written by <see cref="ArchetypeClusterState.RebuildVersionedHeadFromChain"/> + the WAL apply remain correct.
    /// Invoked only on the crash path (WAL files present); a clean reopen keeps its chains for lazy cleanup.
    /// </summary>
    private void ScrubVersionedChains()
    {
        using var guard = EpochGuard.Enter(EpochManager);
        var changeSet = MMF.CreateChangeSet();

        // RB-05: a consolidating checkpoint can advance committed revision TSNs into the data file WITHOUT leaving them in the WAL window (which then recovers
        // empty). The persisted BK_NextFreeTSN is only refreshed on a clean shutdown, so on a hard crash NextFreeTSN can land BELOW the newest consolidated
        // revision — every post-recovery reader then snapshots before it and MVCC hides the latest value. The scrub already visits every committed chain head,
        // so track the max surviving TSN here and advance the allocator past it.
        long maxRecoveredTsn = 0;
        try
        {
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                var state = _archetypeStates[meta.ArchetypeId];
                if (state?.SlotToComponentTable == null)
                {
                    continue;
                }

                for (var slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = state.SlotToComponentTable[slot];

                    // Shared chain-head scan (same source as the EntityMap rebuild). Empty for null / non-Versioned / empty tables.
                    var heads = ComponentRevisionManager.EnumerateVersionedChainHeads(table, RoutingIdOf(meta));
                    if (heads.Count == 0)
                    {
                        continue;
                    }

                    var revAccessor = table.CompRevTableSegment.CreateChunkAccessor(changeSet);
                    var contentAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
                    try
                    {
                        foreach (var firstChunkId in heads.Values)
                        {
                            ComponentRevisionManager.ScrubChainToHead(table, firstChunkId, ref revAccessor, ref contentAccessor, out var headTsn);
                            if (headTsn > maxRecoveredTsn)
                            {
                                maxRecoveredTsn = headTsn;
                            }
                        }

                        // Orphan sweep (§6): every chain is now collapsed to its root, so reclaim any chunk leaked by an
                        // interrupted pre-crash op — allocated but unreachable from a chain root or a surviving head's content.
                        ComponentRevisionManager.SweepTableOrphans(table, new HashSet<int>(heads.Values), ref revAccessor);
                    }
                    finally
                    {
                        revAccessor.Dispose();
                        contentAccessor.Dispose();
                    }
                }
            }

            // Advance the TSN allocator past the newest committed revision found in the persisted chains, so post-recovery readers can see a consolidated
            // revision whose WAL window recovered empty (it would otherwise be MVCC-invisible at a too-low snapshot — RB-05).
            if (maxRecoveredTsn > TransactionChain.NextFreeId)
            {
                TransactionChain.SetNextFreeId(maxRecoveredTsn);
            }
        }
        finally
        {
            changeSet.SaveChanges();
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

        for (var chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            if (SystemCrud.Read(archetypesTable, chunkId, out ArchetypeR1 arch, EpochManager))
            {
                _persistedArchetypes[arch.Name.AsString] = (chunkId, arch);
            }
        }
    }

    private void ValidateArchetypeSchema(ArchetypeMetadata meta)
    {
        if (!_persistedArchetypes.TryGetValue(meta.ArchetypeType.Name, out var persisted))
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
        var anyNew = false;

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var engineState = _archetypeStates[meta.ArchetypeId];
            if (engineState?.SlotToComponentTable == null)
            {
                continue;
            }

            if (_persistedArchetypes.ContainsKey(meta.ArchetypeType.Name))
            {
                continue;
            }

            // Build and persist the ArchetypeR1 entity
            var arch = BuildArchetypeR1(meta);
            arch.AssemblyId = GetOrCreateAssemblyId(meta.ArchetypeType.Assembly, cs);
            arch.RoutingId = RoutingIdOf(meta); // per-DB routing id assigned in InitializeArchetypes

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
            _persistedArchetypes[meta.ArchetypeType.Name] = (chunkId, arch);
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
        for (var slot = 0; slot < meta.ComponentCount; slot++)
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

    // Open-time latency breakdown (#diagnose-open). Information level so it surfaces on a normal open without a Debug
    // build — these run on every reopen and are the prime suspects for a slow large-DB open.
    [LoggerMessage(LogLevel.Information,
        "Open: InitializeArchetypes {totalMs:F0} ms — versionedHeadRebuild {versionedHeadMs:F0} ms, clusterAabbRebuild {clusterAabbMs:F0} ms, cellStateRebuild {cellStateMs:F0} ms, entityMapRebuild {entityMapMs:F0} ms")]
    internal partial void LogInitArchetypesTiming(double totalMs, double versionedHeadMs, double clusterAabbMs, double cellStateMs, double entityMapMs);

    [LoggerMessage(LogLevel.Information, "Open: WAL recovery {walMs:F0} ms over {walBytes} WAL bytes")]
    internal partial void LogWalRecoveryTiming(double walMs, long walBytes);

    [LoggerMessage(LogLevel.Information,
        "Open: total {totalMs:F0} ms — engineConstruct {engineConstructMs:F0} ms (incl. WAL recovery + system-schema load), schemaDllLoad {schemaDllMs:F0} ms, initializeArchetypes {initArchetypesMs:F0} ms")]
    internal partial void LogOpenTiming(double totalMs, double engineConstructMs, double schemaDllMs, double initArchetypesMs);

    [LoggerMessage(LogLevel.Information,
        "Open: Versioned-HEAD reopen {decision} — cleanShutdownFlag {cleanFlag}, checkpointLSN {checkpointLsn} ({detail})")]
    private partial void LogVersionedHeadReopenDecisionCore(string decision, bool cleanFlag, long checkpointLsn, string detail);

    private void LogVersionedHeadReopenDecision(bool trusted, bool cleanFlag, long checkpointLsn)
        => LogVersionedHeadReopenDecisionCore(
            trusted ? "TRUSTED (rebuild skipped)" : "REBUILD",
            cleanFlag,
            checkpointLsn,
            trusted ? "persisted cluster-slot HEADs are current" : "no clean-shutdown flag or migration this session");

    [LoggerMessage(LogLevel.Information, "Close: clean-shutdown HEAD marker written at checkpointLSN {checkpointLsn}")]
    internal partial void LogCleanShutdownMarked(long checkpointLsn);

    [LoggerMessage(LogLevel.Information,
        "WAL watermarks @{phase}: currentLSN {currentLsn}, durableLSN {durableLsn}, checkpointLSN {checkpointLsn}, sealedSegments {sealedSegments}, totalWalBytes {totalWalBytes}")]
    internal partial void LogWalWatermarks(string phase, long currentLsn, long durableLsn, long checkpointLsn, int sealedSegments, long totalWalBytes);

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

    [LoggerMessage(LogLevel.Information,
        "Tx #{tsn} escalated to Commit discipline by component '{componentName}' (DefaultDiscipline=Commit) — all writes in this " +
        "transaction are now commit-durable (CM-02)")]
    internal partial void LogDisciplineEscalated(long tsn, string componentName);

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