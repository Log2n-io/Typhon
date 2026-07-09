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
/// Header structure for a chunk of the Version table
/// </summary>
/// <remarks>
/// <p>
/// The <see cref="ComponentTable.CompRevTableSegment"/> is a <see cref="ChunkBasedSegment{PersistentStore}"/> with chunks of <see cref="ComponentRevisionManager.CompRevChunkSize"/> bytes.
/// Data is stored as a chain of chunks, the first one contains this header and is followed by <see cref="ComponentRevisionManager.CompRevCountInRoot"/> number
/// of <see cref="CompRevStorageElement"/> elements (currently 3 with 12-byte elements).
/// The following chunks in the chain have just an integer as header (giving the next chunk in the chain) and can
/// store <see cref="ComponentRevisionManager.CompRevCountInNext"/> number of <see cref="CompRevStorageElement"/> elements (currently 5).
/// </p>
/// <p>
/// The chain is a circular buffer, location of the first item is given through <see cref="FirstItemIndex"/>
/// </p>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CompRevStorageHeader
{
    /// ID of the next chunk in the chain. MUST BE THE FIRST FIELD OF THIS STRUCTURE !
    public int NextChunkId;

    /// Access control to be thread-safe
    public AccessControlSmall Control;

    /// The whole chain is a circular buffer because we remove the oldest revisions and add the new ones in chronological order. This is the index
    /// of the first item in the chain (e.g. 18 would be 3rd chunk, 2nd entry for 8 entries per chunk)
    public short FirstItemIndex;

    /// Number of items in the chain
    public short ItemCount;

    /// Total length of the chain
    public short ChainLength;

    /// Index in the chain of the last committed revision, allows us to detect concurrency conflicts
    public short LastCommitRevisionIndex;

    /// Primary key of the entity that owns this revision chain.
    /// Enables reverse lookup from secondary index results back to entity PKs.
    public long EntityPK;

    /// Monotonically increasing counter incremented on every commit to this entity.
    /// Used for conflict detection and as the public "revision number" returned by GetComponentRevision.
    public int CommitSequence;

    internal void EnterControlLockForTest() => Control.EnterExclusiveAccess(ref WaitContext.Null);
    internal void ExitControlLockForTest() => Control.ExitExclusiveAccess();

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static (int chunkIndex, int indexInChunk) GetRevisionLocation(int revisionIndex)
    {
        if (revisionIndex < ComponentRevisionManager.CompRevCountInRoot)
        {
            return (0, revisionIndex);
        }
        var chunkIndex = Math.DivRem(revisionIndex-ComponentRevisionManager.CompRevCountInRoot, ComponentRevisionManager.CompRevCountInNext, out var indexInChunk) + 1;
        return (chunkIndex, indexInChunk);
    }
}

/// <summary>
/// Stores the information of a component revision element.
/// </summary>
/// <remarks>
/// 12 bytes (Pack=2, divisible by 4 per ADR-027). Layout:
/// <code>
/// Offset  Size  Field
///   0      4    ComponentChunkId
///   4      4    _packedTickHigh     (upper 32 bits of TSN)
///   8      2    _packedTickLow      (full 16 bits of TSN)
///  10      2    _packedUowId        (bits 0-14: UowId, bit 15: IsolationFlag)
/// </code>
/// Root chunk: 3 elements ((64 − 28) / 12). Overflow chunks: 5 elements (64 / 12).
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct CompRevStorageElement
{
    private const ushort IsolationBit = 1 << 15;        // bit 15 of _packedUowId
    private const ushort UowIdMask = 0x7FFF;            // bits 0-14 of _packedUowId

    public int ComponentChunkId;
    private uint _packedTickHigh;
    private ushort _packedTickLow;
    private ushort _packedUowId;

    public void Void()
    {
        ComponentChunkId = 0;
        _packedTickHigh = 0;
        _packedTickLow = 0;
        _packedUowId = 0;
    }

    public bool IsVoid => ComponentChunkId == 0 && _packedTickHigh == 0 && _packedTickLow == 0 && _packedUowId == 0;

    public bool IsolationFlag
    {
        get => (_packedUowId & IsolationBit) != 0;
        set => _packedUowId = (ushort)(value ? (_packedUowId | IsolationBit) : (_packedUowId & ~IsolationBit));
    }

    /// <summary>UoW ID that created this revision (15 bits, max 32,767). 0 until UoW Registry (#51) lands.</summary>
    public ushort UowId
    {
        get => (ushort)(_packedUowId & UowIdMask);
        set => _packedUowId = (ushort)((_packedUowId & IsolationBit) | (value & UowIdMask));
    }

    public long TSN
    {
        get => (long)((ulong)_packedTickHigh << 16 | _packedTickLow);
        set
        {
            _packedTickHigh = (uint)(value >> 16);
            _packedTickLow = (ushort)(value & 0xFFFF);
        }
    }
}

[DebuggerDisplay("Offset: {OffsetToField} Size: {Size}")]
internal struct IndexedFieldInfo
{
    public int OffsetToField;
    public int Size;

    public int OffsetToIndexElementId;
    public IBTreeIndex Index;

    /// <summary>Cached from <see cref="IBTreeIndex.AllowMultiple"/> — avoids interface dispatch on hot path.</summary>
    public bool AllowMultiple;

    /// <summary>Returns the index cast to <see cref="BTreeBase{TStore}"/> for PersistentStore operations (Versioned and SingleVersion paths).</summary>
    internal BTreeBase<PersistentStore> PersistentIndex => (BTreeBase<PersistentStore>)Index;

    /// <summary>Returns the index cast to <see cref="BTreeBase{TStore}"/> for Transient storage operations.</summary>
    internal BTreeBase<TransientStore> TransientIndex => (BTreeBase<TransientStore>)Index;
}

/// <summary>Bit flags describing optional storage features enabled on a <see cref="ComponentTable"/>.</summary>
[PublicAPI]
[Flags]
public enum ComponentTableFlags
{
    /// <summary>No optional features.</summary>
    None                = 0x00,

    /// <summary>The component declares at least one collection-typed field (variable-sized buffer storage).</summary>
    HasCollections      = 0x01
}

/// <summary>
/// Stores all instances of a single component type with MVCC revision tracking.
/// </summary>
/// <remarks>
/// <para>
/// ComponentTable registers as a child of its owning <see cref="DatabaseEngine"/> in the resource tree.
/// Segments (ComponentSegment, CompRevTableSegment, etc.) are NOT registered as children —
/// they follow the "Owner Aggregates" pattern where ComponentTable will aggregate their metrics.
/// </para>
/// </remarks>
[PublicAPI]
public unsafe class ComponentTable : ResourceNode, IMetricSource, IDebugPropertiesProvider
{
    private const int ComponentSegmentStartingSize = 4;
    private const int MainIndexSegmentStartingSize = 4;

    // ── Storage mode (immutable after construction) ──
    /// <summary>
    /// Storage discipline for this component, fixed at construction: <see cref="StorageMode.Versioned"/> (MVCC revision chains),
    /// <see cref="StorageMode.SingleVersion"/> (in-place, tick-boundary maintenance), or <see cref="StorageMode.Transient"/> (heap-backed, not persisted).
    /// </summary>
    public StorageMode StorageMode { get; private set; }

    /// <summary>
    /// Default durability discipline for this component, resolved from <c>[Component(DefaultDiscipline=…)]</c>.
    /// Only consulted for <see cref="StorageMode.SingleVersion"/>; a transaction writing a
    /// <see cref="DurabilityDiscipline.Commit"/> component is committed-durable for all of its writes (CM-02).
    /// </summary>
    public DurabilityDiscipline Discipline { get; private set; }

    // ── Persistent segments (Versioned & SingleVersion) ──
    /// <summary>Segment holding the component data chunks (the field payloads). Null in <see cref="StorageMode.Transient"/> mode.</summary>
    public ChunkBasedSegment<PersistentStore> ComponentSegment { get; private set; }

    /// <summary>Segment holding the MVCC revision chains. Non-null only for <see cref="StorageMode.Versioned"/> storage.</summary>
    public ChunkBasedSegment<PersistentStore> CompRevTableSegment { get; private set; }

    /// <summary>Segment backing the primary-key B+Tree and every non-<see cref="String64"/> secondary index.</summary>
    public ChunkBasedSegment<PersistentStore> DefaultIndexSegment { get; private set; }

    /// <summary>Segment backing the <see cref="String64"/>-keyed secondary indexes.</summary>
    public ChunkBasedSegment<PersistentStore> String64IndexSegment { get; private set; }

    /// <summary>Segment backing the version-history tail for AllowMultiple secondary indexes. Null when the component has no multi-value index.</summary>
    public ChunkBasedSegment<PersistentStore> TailIndexSegment { get; private set; }

    /// <summary>
    /// Surfaces the entity count as <see cref="IResource.Count"/> so the Workbench can render a
    /// live badge on the ComponentTable tree node without a second round-trip.
    /// </summary>
    public override int? Count => EstimatedEntityCount;

    /// <summary>
    /// Estimated total entity count. Sums EntityMap entry counts across archetypes that include this component.
    /// </summary>
    public int EstimatedEntityCount
    {
        get
        {
            int typeId = ArchetypeRegistry.GetComponentTypeId(Definition.POCOType);
            if (typeId < 0)
            {
                return 0;
            }
            int total = 0;
            foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
            {
                if (!meta.TryGetSlot(typeId, out _))
                {
                    continue;
                }
                var dbe = (DatabaseEngine)Parent; // ComponentTable is a child of DatabaseEngine in the resource tree
                var state = dbe._archetypeStates[meta.ArchetypeId];
                if (state?.EntityMap != null)
                {
                    total += (int)state.EntityMap.EntryCount;
                }
            }
            return total;
        }
    }
    internal VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> TailVSBS { get; private set; }

    // ── Transient segments (non-null only when StorageMode == Transient) ──
    internal ChunkBasedSegment<TransientStore> TransientComponentSegment { get; private set; }
    internal ChunkBasedSegment<TransientStore> TransientDefaultIndexSegment { get; private set; }
    internal ChunkBasedSegment<TransientStore> TransientString64IndexSegment { get; private set; }

    // ── Transient stores (one per CBS — struct-copy of _pageCount requires independent instances) ──
    private TransientStore? _transientComponentStore;
    private TransientStore? _transientDefaultIndexStore;
    private TransientStore? _transientString64IndexStore;

    // ── SingleVersion dirty tracking (non-null only when StorageMode == SingleVersion) ──
    internal DirtyBitmap DirtyBitmap { get; private set; }

    /// <summary>
    /// Raw dirty bitmap snapshot from the previous tick, captured at tick fence time via <see cref="DirtyBitmap.Snapshot()"/>.
    /// Each set bit represents a chunkId with dirty component data. Used by the runtime's change-filtered
    /// system inputs (#197): the runtime iterates set bits, reads entity PK from chunk offset 0, and intersects with the View.
    /// Null before the first tick fence runs.
    /// </summary>
    internal long[] PreviousTickDirtyBitmap { get; set; }

    /// <summary>
    /// Whether any entity was dirty in the previous tick. Reliable regardless of EntityPK overhead.
    /// Used as a fast skip check by ReactiveSkip closures.
    /// Defaults to true so the first tick (before any tick fence) is conservative.
    /// </summary>
    internal bool PreviousTickHadDirtyEntities { get; set; } = true;

    // ── Shadow tracking for SV tick-boundary index/view maintenance ──
    // Non-null only when StorageMode == SingleVersion AND IndexedFieldInfos.Length > 0.
    // ShadowBitmap tracks which chunkIds have been shadowed this tick (TestAndSet guard).
    // FieldShadowBuffers[i] stores old KeyBytes8 values for IndexedFieldInfos[i].
    internal bool HasShadowableIndexes { get; private set; }
    internal DirtyBitmap ShadowBitmap { get; private set; }
    internal FieldShadowBuffer[] FieldShadowBuffers { get; private set; }

    // ── Spatial index state (non-null only when a [SpatialIndex] field exists) ──
    internal SpatialIndexState SpatialIndex { get; set; }

    // ── Destroyed chunk tracking for SV index cleanup ──
    // Accumulates chunkIds of destroyed SV entities during commits this tick.
    // Checked by ProcessShadowEntries/BuildFilteredEntitySet to distinguish Remove vs Move. Cleared at tick boundary.
    // Fully lock-free: ConcurrentHashMap uses OLC for reads (~5ns) and per-stripe CAS locks for writes (no global lock).
    private readonly ConcurrentHashMap<int> _destroyedChunkIds = new(64);

    internal void TrackDestroyedChunkId(int chunkId) => _destroyedChunkIds.TryAdd(chunkId);

    internal bool IsChunkDestroyed(int chunkId) => _destroyedChunkIds.Contains(chunkId);

    internal void ClearDestroyedChunkIds() => _destroyedChunkIds.Clear();

    /// <summary>Size in bytes of a single component's field payload (excludes MVCC and index overhead).</summary>
    public int ComponentStorageSize => Definition.ComponentStorageSize;

    /// <summary>Schema definition (fields, indexes, layout) of the component type stored in this table.</summary>
    public DBComponentDefinition Definition { get; private set; }

    /// <summary>Optional storage features enabled on this table (see <see cref="ComponentTableFlags"/>).</summary>
    public ComponentTableFlags Flags => _flags;

    /// <summary><c>true</c> when the component declares at least one collection-typed field (<see cref="ComponentTableFlags.HasCollections"/> is set).</summary>
    public bool HasCollections => (_flags & ComponentTableFlags.HasCollections) != 0;

    internal DatabaseEngine DBE { get; private set; }
    internal int ComponentOverhead => Definition.ComponentStorageOverhead;
    internal int ComponentTotalSize => Definition.ComponentStorageTotalSize;

    /// <summary>
    /// Stable WAL type identifier derived from <see cref="LogicalSegment{PersistentStore}.RootPageIndex"/>. Set during registration.
    /// Used to identify component types in WAL records for crash recovery replay.
    /// </summary>
    internal ushort WalTypeId { get; set; }
    internal IndexedFieldInfo[] IndexedFieldInfos { get; private set; }
    internal IndexStatistics[] IndexStats { get; private set; }
    internal ViewRegistry ViewRegistry { get; private set; }

    internal Dictionary<int, VariableSizedBufferSegmentBase<PersistentStore>> ComponentCollectionVSBSByOffset { get; private set; }

    /// <summary>
    /// Monotonically increasing counter incremented each time index layout changes (e.g., schema migration adds/removes indexes).
    /// Used by <see cref="IndexRef"/> for O(1) staleness detection without touching page 0.
    /// </summary>
    private int _indexLayoutVersion;
    internal int IndexLayoutVersion => _indexLayoutVersion;

    private ComponentTableFlags _flags;

    /// <summary>
    /// Approximate mutation count since last statistics rebuild. Non-atomic — intentional races are acceptable since this is only used as a threshold
    /// trigger by <see cref="StatisticsWorker"/>. Reset to zero by the worker after initiating a rebuild.
    /// </summary>
    internal int MutationsSinceRebuild;

    #region IMetricSource Implementation

    /// <inheritdoc />
    public void ReadMetrics(IMetricWriter writer)
    {
        // Aggregate capacity from all segments (persistent + transient, null-safe for mode-specific segments)
        long totalAllocatedChunks =
            (ComponentSegment?.AllocatedChunkCount ?? 0) +
            (TransientComponentSegment?.AllocatedChunkCount ?? 0) +
            (CompRevTableSegment?.AllocatedChunkCount ?? 0) +
            (DefaultIndexSegment?.AllocatedChunkCount ?? 0) +
            (TransientDefaultIndexSegment?.AllocatedChunkCount ?? 0) +
            (String64IndexSegment?.AllocatedChunkCount ?? 0) +
            (TransientString64IndexSegment?.AllocatedChunkCount ?? 0) +
            (TailIndexSegment?.AllocatedChunkCount ?? 0);

        long totalCapacityChunks =
            (ComponentSegment?.ChunkCapacity ?? 0) +
            (TransientComponentSegment?.ChunkCapacity ?? 0) +
            (CompRevTableSegment?.ChunkCapacity ?? 0) +
            (DefaultIndexSegment?.ChunkCapacity ?? 0) +
            (TransientDefaultIndexSegment?.ChunkCapacity ?? 0) +
            (String64IndexSegment?.ChunkCapacity ?? 0) +
            (TransientString64IndexSegment?.ChunkCapacity ?? 0) +
            (TailIndexSegment?.ChunkCapacity ?? 0);

        writer.WriteCapacity(totalAllocatedChunks, totalCapacityChunks);
    }

    /// <inheritdoc />
    /// <remarks>No high-water-mark fields on this resource — body intentionally empty.</remarks>
    public void ResetPeaks()
    {
    }

    #endregion

    #region IDebugPropertiesProvider Implementation

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetDebugProperties()
    {
        var props = new Dictionary<string, object>
        {
            ["StorageMode"] = StorageMode,
            ["ComponentSegment.ChunkSize"] = ComponentTotalSize,
        };

        // Persistent segments (Versioned / SingleVersion)
        if (ComponentSegment != null)
        {
            props["ComponentSegment.AllocatedChunks"] = ComponentSegment.AllocatedChunkCount;
            props["ComponentSegment.Capacity"] = ComponentSegment.ChunkCapacity;
        }

        if (CompRevTableSegment != null)
        {
            props["CompRevTableSegment.AllocatedChunks"] = CompRevTableSegment.AllocatedChunkCount;
            props["CompRevTableSegment.Capacity"] = CompRevTableSegment.ChunkCapacity;
        }

        if (DefaultIndexSegment != null)
        {
            props["DefaultIndexSegment.AllocatedChunks"] = DefaultIndexSegment.AllocatedChunkCount;
            props["DefaultIndexSegment.Capacity"] = DefaultIndexSegment.ChunkCapacity;
        }

        if (String64IndexSegment != null)
        {
            props["String64IndexSegment.AllocatedChunks"] = String64IndexSegment.AllocatedChunkCount;
            props["String64IndexSegment.Capacity"] = String64IndexSegment.ChunkCapacity;
        }

        if (TailIndexSegment != null)
        {
            props["TailIndexSegment.AllocatedChunks"] = TailIndexSegment.AllocatedChunkCount;
            props["TailIndexSegment.Capacity"] = TailIndexSegment.ChunkCapacity;
        }

        // Transient segments
        if (TransientComponentSegment != null)
        {
            props["TransientComponentSegment.AllocatedChunks"] = TransientComponentSegment.AllocatedChunkCount;
            props["TransientComponentSegment.Capacity"] = TransientComponentSegment.ChunkCapacity;
        }

        if (TransientDefaultIndexSegment != null)
        {
            props["TransientDefaultIndexSegment.AllocatedChunks"] = TransientDefaultIndexSegment.AllocatedChunkCount;
            props["TransientDefaultIndexSegment.Capacity"] = TransientDefaultIndexSegment.ChunkCapacity;
        }

        return props;
    }

    #endregion
    
    /// <summary>
    /// Creates a new (empty) component table, allocating its data, revision, and index segments. For <see cref="StorageMode.Transient"/> the segments are
    /// heap-backed and no MVCC revision chain is allocated.
    /// </summary>
    /// <param name="dbe">Owning database engine.</param>
    /// <param name="definition">Schema definition of the component type stored here.</param>
    /// <param name="parent">Resource-tree parent (the owning <see cref="DatabaseEngine"/>).</param>
    /// <param name="storageMode">Storage discipline: <see cref="StorageMode.Versioned"/> (default, MVCC), <see cref="StorageMode.SingleVersion"/>, or <see cref="StorageMode.Transient"/>.</param>
    /// <param name="exhaustionPolicy">Resource-exhaustion policy forwarded to the base <see cref="ResourceNode"/>.</param>
    /// <param name="changeSet">Change set threading segment-growth dirty marks through the allocation; may be <c>null</c>.</param>
    public ComponentTable(DatabaseEngine dbe, DBComponentDefinition definition, IResource parent, StorageMode storageMode = StorageMode.Versioned,
        ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None, ChangeSet changeSet = null) :
        base($"ComponentTable_{definition.Name}", ResourceType.ComponentTable, parent, exhaustionPolicy)
    {
        DBE = dbe;
        Definition = definition;
        StorageMode = storageMode;
        Discipline = definition.DefaultDiscipline;

        if (storageMode == StorageMode.Transient)
        {
            CreateTransientSegments(dbe);
            return;
        }

        // Versioned and SingleVersion both use PersistentStore (SV needs MMF checkpoint for clean entity recovery)
        var mmf = DBE.MMF;
        ComponentSegment    = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentTotalSize, changeSet, 
            StorageSegmentKind.Component);
        DefaultIndexSegment  = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(Index64Chunk), changeSet, 
            StorageSegmentKind.Index);
        String64IndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, sizeof(IndexString64Chunk), changeSet, 
            StorageSegmentKind.Index);

        // Versioned only: allocate revision chain segment for MVCC
        if (storageMode == StorageMode.Versioned)
        {
            CompRevTableSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, ComponentRevisionManager.CompRevChunkSize, 
                changeSet, StorageSegmentKind.Revision);
        }

        // Allocate TAIL version-history segment for AllowMultiple secondary indexes
        if (Definition.MultipleIndicesCount > 0)
        {
            TailIndexSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, 512, changeSet, StorageSegmentKind.Index);
            TailVSBS = new VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore>(TailIndexSegment);
        }

        BuildIndexedFieldInfo(false, changeSet);
        ViewRegistry = new ViewRegistry(IndexedFieldInfos.Length);
        BuildComponentCollectionInfo(changeSet);

        // Allocate spatial index segments and construct R-Tree if [SpatialIndex] is present
        if (definition.SpatialField != null)
        {
            BuildSpatialIndex(false, changeSet);
        }

        if (storageMode == StorageMode.SingleVersion)
        {
            DirtyBitmap = new DirtyBitmap(ComponentSegment.ChunkCapacity);
            InitializeShadowTracking();
        }
    }

    /// <summary>
    /// Load constructor: restores a ComponentTable from previously persisted segment root page indices.
    /// Used during database reopen to reconnect to existing on-disk data instead of allocating fresh segments.
    /// </summary>
    /// <param name="dbe">Owning database engine.</param>
    /// <param name="definition">Schema definition of the component type stored here.</param>
    /// <param name="parent">Resource-tree parent (the owning <see cref="DatabaseEngine"/>).</param>
    /// <param name="componentSPI">Persisted root-page index (SPI) of the component data segment to reload.</param>
    /// <param name="versionSPI">Persisted SPI of the MVCC revision-chain segment; used only for <see cref="StorageMode.Versioned"/>.</param>
    /// <param name="defaultIndexSPI">Persisted SPI of the default index segment (primary key and non-<see cref="String64"/> secondary indexes).</param>
    /// <param name="string64IndexSPI">Persisted SPI of the <see cref="String64"/> index segment.</param>
    /// <param name="tailIndexSPI">Persisted SPI of the multi-value index version-history tail; <c>0</c> when the component has no AllowMultiple index.</param>
    /// <param name="storageMode">Storage mode from persisted ComponentR1 metadata.</param>
    /// <param name="exhaustionPolicy">Resource-exhaustion policy forwarded to the base <see cref="ResourceNode"/>.</param>
    /// <param name="newIndexFieldIds">Optional set of FieldIds for newly added indexes that need creating instead of loading.
    /// When non-null, indexes for these fields are created fresh; all other indexes are loaded from disk.</param>
    /// <param name="changeSet">Change set threading segment-load dirty marks through the allocation; may be <c>null</c>.</param>
    /// <param name="restoreCollectionInfo">When <c>true</c>, reconnect the component-collection buffer map to the persisted segments (user tables); when
    /// <c>false</c>, start with an empty map (system tables, which re-derive their collection info from runtime registration).</param>
    internal ComponentTable(DatabaseEngine dbe, DBComponentDefinition definition, IResource parent, int componentSPI, int versionSPI, int defaultIndexSPI,
        int string64IndexSPI, int tailIndexSPI = 0, StorageMode storageMode = StorageMode.Versioned, ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None,
        HashSet<int> newIndexFieldIds = null, ChangeSet changeSet = null, bool restoreCollectionInfo = false) :
        base($"ComponentTable_{definition.Name}", ResourceType.ComponentTable, parent, exhaustionPolicy)
    {
        DBE = dbe;
        Definition = definition;
        StorageMode = storageMode;
        Discipline = definition.DefaultDiscipline;

        // Transient data doesn't survive restart — create a fresh empty table
        if (storageMode == StorageMode.Transient)
        {
            CreateTransientSegments(dbe);
            return;
        }

        var mmf = DBE.MMF;

        ComponentSegment     = mmf.LoadChunkBasedSegment(componentSPI, ComponentTotalSize);
        DefaultIndexSegment  = mmf.LoadChunkBasedSegment(defaultIndexSPI, sizeof(Index64Chunk));
        String64IndexSegment = mmf.LoadChunkBasedSegment(string64IndexSPI, sizeof(IndexString64Chunk));

        // Versioned only: load revision chain segment
        if (storageMode == StorageMode.Versioned)
        {
            CompRevTableSegment = mmf.LoadChunkBasedSegment(versionSPI, ComponentRevisionManager.CompRevChunkSize);
        }

        // Restore TAIL version-history segment for AllowMultiple secondary indexes
        if (Definition.MultipleIndicesCount > 0)
        {
            TailIndexSegment = mmf.LoadChunkBasedSegment(tailIndexSPI, 512);
            TailVSBS = new VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore>(TailIndexSegment);
        }

        BuildIndexedFieldInfo(true, changeSet, newIndexFieldIds);
        ViewRegistry = new ViewRegistry(IndexedFieldInfos.Length);

        // On reopen, restore HasCollections + the offset→VSBS map — but ONLY for user tables (restoreCollectionInfo). User-table load sites pass true; they run
        // AFTER the component-collection segment pool has been reloaded, so GetComponentCollectionVSBS reconnects to the existing segment. The system tables
        // (e.g. ComponentR1.Fields) are constructed BEFORE that reload and re-derive their CC from runtime registration, so they pass false — calling
        // BuildComponentCollectionInfo there would fresh-allocate and orphan the persisted segment (losing the field definitions → corrupt migration). See #387.
        if (restoreCollectionInfo)
        {
            BuildComponentCollectionInfo(changeSet);
        }
        else
        {
            ComponentCollectionVSBSByOffset = new Dictionary<int, VariableSizedBufferSegmentBase<PersistentStore>>();
        }

        if (storageMode == StorageMode.SingleVersion)
        {
            DirtyBitmap = new DirtyBitmap(ComponentSegment.ChunkCapacity);
            InitializeShadowTracking();
        }
    }

    /// <summary>
    /// Migration constructor: uses pre-created component and revision segments from schema migration, while loading index segments from their persisted SPIs.
    /// Only valid for Versioned components.
    /// </summary>
    internal ComponentTable(DatabaseEngine dbe, DBComponentDefinition definition, IResource parent, ChunkBasedSegment<PersistentStore> componentSegment,
        ChunkBasedSegment<PersistentStore> revisionSegment, int defaultIndexSPI, int string64IndexSPI, int tailIndexSPI = 0,
        ExhaustionPolicy exhaustionPolicy = ExhaustionPolicy.None, HashSet<int> newIndexFieldIds = null, ChangeSet changeSet = null, bool restoreCollectionInfo = false) :
        base($"ComponentTable_{definition.Name}", ResourceType.ComponentTable, parent, exhaustionPolicy)
    {
        Debug.Assert(definition.StorageMode == StorageMode.Versioned, "Schema migration only applies to Versioned components");
        DBE = dbe;
        Definition = definition;
        StorageMode = StorageMode.Versioned;
        var mmf = DBE.MMF;

        ComponentSegment = componentSegment;
        CompRevTableSegment = revisionSegment;
        DefaultIndexSegment = mmf.LoadChunkBasedSegment(defaultIndexSPI, sizeof(Index64Chunk));
        String64IndexSegment = mmf.LoadChunkBasedSegment(string64IndexSPI, sizeof(IndexString64Chunk));

        if (Definition.MultipleIndicesCount > 0)
        {
            TailIndexSegment = mmf.LoadChunkBasedSegment(tailIndexSPI, 512);
            TailVSBS = new VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore>(TailIndexSegment);
        }

        BuildIndexedFieldInfo(true, changeSet, newIndexFieldIds);
        ViewRegistry = new ViewRegistry(IndexedFieldInfos.Length);

        // See the load ctor: restore CC info only for user tables (restoreCollectionInfo). Migration may shift field offsets, so the map is rebuilt from the NEW
        // definition. Migrated user tables are constructed after the segment-pool reload, so reconnection is safe; system tables never use this ctor.
        if (restoreCollectionInfo)
        {
            BuildComponentCollectionInfo(changeSet);
        }
        else
        {
            ComponentCollectionVSBSByOffset = new Dictionary<int, VariableSizedBufferSegmentBase<PersistentStore>>();
        }
    }

    /// <summary>
    /// Creates heap-backed segments for Transient storage mode. Each CBS gets its own TransientStore
    /// instance to avoid struct-copy divergence of mutable <c>_pageCount</c> field.
    /// </summary>
    private void CreateTransientSegments(DatabaseEngine dbe)
    {
        var opts = dbe.TransientOptions;
        var em = dbe.EpochManager;

        // Component data segment.
        // Order matters: allocate the initial pages BEFORE constructing the segment. TransientStore is a struct and the segment's base LogicalSegment copies it
        // by value in its ctor — allocating after construction would leave the segment's copy at _pageCount=0, so the first Grow re-allocates duplicate page
        // indices and corrupts the forward chain. Allocating first means the ctor captures the correct _pageCount.
        _transientComponentStore = new TransientStore(opts, dbe.MemoryAllocator, em, this);
        var compStore = _transientComponentStore.Value;
        Span<int> compPages = stackalloc int[ComponentSegmentStartingSize];
        compStore.AllocatePages(ref compPages, 0, null);
        TransientComponentSegment = new ChunkBasedSegment<TransientStore>(em, compStore, ComponentTotalSize);
        TransientComponentSegment.Create(PageBlockType.None, StorageSegmentKind.Component, compPages, false);

        // Default index segment (for PK B+Tree and non-String64 secondary indexes). Allocate-before-construct, see note above.
        _transientDefaultIndexStore = new TransientStore(opts, dbe.MemoryAllocator, em, this);
        var idxStore = _transientDefaultIndexStore.Value;
        Span<int> idxPages = stackalloc int[MainIndexSegmentStartingSize];
        idxStore.AllocatePages(ref idxPages, 0, null);
        TransientDefaultIndexSegment = new ChunkBasedSegment<TransientStore>(em, idxStore, sizeof(Index64Chunk));
        TransientDefaultIndexSegment.Create(PageBlockType.None, StorageSegmentKind.Index, idxPages, false);

        // String64 index segment. Allocate-before-construct, see note above.
        _transientString64IndexStore = new TransientStore(opts, dbe.MemoryAllocator, em, this);
        var s64Store = _transientString64IndexStore.Value;
        Span<int> s64Pages = stackalloc int[MainIndexSegmentStartingSize];
        s64Store.AllocatePages(ref s64Pages, 0, null);
        TransientString64IndexSegment = new ChunkBasedSegment<TransientStore>(em, s64Store, sizeof(IndexString64Chunk));
        TransientString64IndexSegment.Create(PageBlockType.None, StorageSegmentKind.Index, s64Pages, false);

        BuildIndexedFieldInfo(false);
        ViewRegistry = new ViewRegistry(IndexedFieldInfos.Length);
        ComponentCollectionVSBSByOffset = new Dictionary<int, VariableSizedBufferSegmentBase<PersistentStore>>();

        if (IndexedFieldInfos.Length > 0)
        {
            HasShadowableIndexes = true;
            DirtyBitmap = new DirtyBitmap(TransientComponentSegment.ChunkCapacity);
            ShadowBitmap = new DirtyBitmap(TransientComponentSegment.ChunkCapacity);
            FieldShadowBuffers = new FieldShadowBuffer[IndexedFieldInfos.Length];
            for (int i = 0; i < IndexedFieldInfos.Length; i++)
            {
                FieldShadowBuffers[i] = new FieldShadowBuffer();
            }
        }
    }

    private void BuildIndexedFieldInfo(bool load, ChangeSet changeSet = null, HashSet<int> newIndexFieldIds = null)
    {
        // Crash recovery (RB-01): persisted secondary indexes are never trusted post-crash. On the crash path (a WAL window exists at open), clear the shared
        // index segments torn-safely — FreeChunk by bitmap, never reading a (possibly torn) node page — and force EVERY indexed field into create-mode so the
        // trees are recreated EMPTY here. Phase-5 (DatabaseEngine.RebuildSecondaryIndexes, after apply+scrub) repopulates them from the final HEAD data.
        if (load && DBE.WalFilesPresentAtOpen)
        {
            BTreeBase<PersistentStore>.ClearSharedSegment(DefaultIndexSegment, changeSet);
            BTreeBase<PersistentStore>.ClearSharedSegment(String64IndexSegment, changeSet);
            ClearMultiValueTail(changeSet);
            newIndexFieldIds = CollectAllIndexedFieldIds(newIndexFieldIds);
        }

        var l = new List<IndexedFieldInfo>();

        var ro = ComponentOverhead;

        // Each secondary index uses Field.FieldId as its stable directory key.
        // This is order-independent and survives schema evolution (FieldIds are immutable once assigned).
        for (int i = 0, j = 0; i < Definition.MaxFieldId; i++)
        {
            var f = Definition[i];
            if (f == null || !f.HasIndex)
            {
                continue;
            }

            // During schema evolution: newly added indexes use create mode; existing indexes use load mode
            var useLoad = load && (newIndexFieldIds == null || !newIndexFieldIds.Contains(f.FieldId));

            var index = CreateIndexForField(f, (short)f.FieldId, useLoad, changeSet);
            var fi = new IndexedFieldInfo
            {
                OffsetToField = ro + f.OffsetInComponentStorage,
                Size          = f.SizeInComponentStorage,
                Index         = index,
                AllowMultiple = index.AllowMultiple,
            };
            fi.OffsetToIndexElementId = fi.AllowMultiple ? (Definition.EntityPKOverheadSize + j++ * sizeof(int)) : 0;
            l.Add(fi);
        }

        IndexedFieldInfos = l.ToArray();
        _indexLayoutVersion++;

        IndexStats = new IndexStatistics[IndexedFieldInfos.Length];
        for (var i = 0; i < IndexedFieldInfos.Length; i++)
        {
            IndexStats[i] = new IndexStatistics(IndexedFieldInfos[i].Index);
        }
    }

    /// <summary>
    /// Crash-path clear of the multi-value (AllowMultiple) index version-history tail (<see cref="TailVSBS"/>). The current-value HEAD buffers live in the
    /// DefaultIndexSegment and are cleared with the BTree nodes by <see cref="BTreeBase{TStore}.ClearSharedSegment"/>; the TAIL holds superseded
    /// VersionedIndexEntry history (temporal queries) which collapses at crash (D1). Freeing its data chunks (chunkId &gt;= 1; chunk 0 is segment-reserved) leaves
    /// an empty tail — the Phase-5 rebuild re-Adds each entity once, producing fresh HEAD buffers and no history. Torn-safe: FreeChunk touches only the page
    /// occupancy bitmap, and the crash-path RecoveryOnly mode defers CRC verification.
    /// </summary>
    private void ClearMultiValueTail(ChangeSet changeSet)
    {
        if (TailIndexSegment == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(DBE.EpochManager);
        var capacity = TailIndexSegment.ChunkCapacity;
        for (var chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (TailIndexSegment.IsChunkAllocated(chunkId))
            {
                TailIndexSegment.FreeChunk(chunkId);
            }
        }
    }

    /// <summary>Returns every indexed FieldId in this table's schema, unioned with <paramref name="seed"/> (any migration-new fields). Used on the crash path to
    /// force all secondary indexes into create-mode so they are rebuilt from data rather than loaded (RB-01).</summary>
    private HashSet<int> CollectAllIndexedFieldIds(HashSet<int> seed)
    {
        var all = seed != null ? new HashSet<int>(seed) : new HashSet<int>();
        for (var i = 0; i < Definition.MaxFieldId; i++)
        {
            var f = Definition[i];
            if (f != null && f.HasIndex)
            {
                all.Add(f.FieldId);
            }
        }

        return all;
    }

    // Per-index-segment accessor box for the Phase-5 rebuild: a class field gives a stable `ref box.Accessor` (a Dictionary value can't be passed by ref), and
    // several indexed fields can share one index segment, so one accessor per segment matches the live commit's hoisted-accessor pattern.
    private sealed class IndexAccessorBox
    {
        public ChunkAccessor<PersistentStore> Accessor;
    }

    /// <summary>
    /// Phase-5 crash-recovery rebuild (03-recovery.md §7, RB-01) of this Versioned table's secondary indexes. For each chain head <c>(EntityPK → rootChunkId)</c>
    /// in <paramref name="heads"/>, reads the head's content chunk (root element[0].ComponentChunkId) and re-inserts <c>key → rootChunkId</c> into every secondary
    /// index — the index value for a Versioned component is the chain ROOT (queries resolve it through CompRevTable), never the content chunk. A tombstone head
    /// (ComponentChunkId == 0, a deleted entity) carries no index entry. Called once per containing archetype on an index that was emptied at open
    /// (<see cref="BuildIndexedFieldInfo"/> crash path), so the union across archetypes forms the complete index. Multi-value writes the element id back to the
    /// component tail so later removals resolve. Reads only — never parses a persisted index node page (the precondition for retiring FPI on index pages).
    /// </summary>
    internal void RebuildSecondaryIndexEntriesFromHeads(Dictionary<long, int> heads, ChangeSet changeSet)
    {
        if (IndexedFieldInfos.Length == 0 || heads.Count == 0 || StorageMode != StorageMode.Versioned)
        {
            return;
        }

        // A multi-value index writes the returned elementId back into the component tail, so the content chunk must be dirty-marked (and committed) — but only when
        // some field is AllowMultiple; a unique-only table reads the content read-only.
        var anyMultiValue = false;
        for (var i = 0; i < IndexedFieldInfos.Length; i++)
        {
            if (IndexedFieldInfos[i].AllowMultiple)
            {
                anyMultiValue = true;
                break;
            }
        }

        using var guard = EpochGuard.Enter(DBE.EpochManager);
        var revAccessor = CompRevTableSegment.CreateChunkAccessor(changeSet);
        var contentAccessor = ComponentSegment.CreateChunkAccessor(changeSet);
        var idxAccessors = new Dictionary<ChunkBasedSegment<PersistentStore>, IndexAccessorBox>();
        try
        {
            foreach (var kv in heads)
            {
                var rootChunkId = kv.Value;
                revAccessor.GetChunkAsSpan(rootChunkId).Split(out Span<CompRevStorageHeader> _, out Span<CompRevStorageElement> els);
                var contentChunkId = els[0].ComponentChunkId;
                if (contentChunkId == 0)
                {
                    continue; // tombstone head (deleted entity) — no index entry
                }

                var contentBase = contentAccessor.GetChunkAddress(contentChunkId, anyMultiValue);
                for (var i = 0; i < IndexedFieldInfos.Length; i++)
                {
                    ref var ifi = ref IndexedFieldInfos[i];
                    var index = ifi.PersistentIndex;
                    if (!idxAccessors.TryGetValue(index.Segment, out var box))
                    {
                        box = new IndexAccessorBox { Accessor = index.Segment.CreateChunkAccessor(changeSet) };
                        idxAccessors[index.Segment] = box;
                    }

                    var keyAddr = contentBase + ifi.OffsetToField;
                    if (ifi.AllowMultiple)
                    {
                        *(int*)(contentBase + ifi.OffsetToIndexElementId) = index.Add(keyAddr, rootChunkId, ref box.Accessor, out _);
                    }
                    else
                    {
                        index.Add(keyAddr, rootChunkId, ref box.Accessor);
                    }
                }
            }
        }
        finally
        {
            foreach (var box in idxAccessors.Values)
            {
                box.Accessor.CommitChanges();
                box.Accessor.Dispose();
            }

            if (anyMultiValue)
            {
                contentAccessor.CommitChanges(); // persist the elementId back-writes
            }

            contentAccessor.Dispose();
            revAccessor.Dispose();
        }
    }

    /// <summary>
    /// Allocate spatial index segments and construct the R-Tree + SpatialIndexState.
    /// Called from both the create constructor and the load constructor.
    /// </summary>
    private void BuildSpatialIndex(bool load, ChangeSet changeSet = null, ChunkBasedSegment<PersistentStore> existingTreeSegment = null,
        ChunkBasedSegment<PersistentStore> existingBackPtrSegment = null, PagedHashMap<long, int, PersistentStore> existingOccupancyMap = null)
    {
        var sf = Definition.SpatialField;
        var fieldInfo = new SpatialFieldInfo(ComponentOverhead + sf.OffsetInComponentStorage, sf.SizeInComponentStorage, sf.SpatialFieldType,
            sf.SpatialMargin, sf.SpatialCellSize, sf.SpatialMode, sf.SpatialCategory);

        var variant = fieldInfo.ToVariant();
        var descriptor = SpatialNodeDescriptor.ForVariant(variant);

        ChunkBasedSegment<PersistentStore> treeSegment;
        ChunkBasedSegment<PersistentStore> backPtrSegment;
        PagedHashMap<long, int, PersistentStore> occupancyMap = null;

        if (!load)
        {
            var mmf = DBE.MMF;
            treeSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, descriptor.Stride, changeSet, 
                StorageSegmentKind.Spatial);
            backPtrSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, ComponentSegmentStartingSize, 8, changeSet, StorageSegmentKind.Spatial);

            // Allocate Layer 1 occupancy hashmap when CellSize > 0
            if (fieldInfo.CellSize > 0)
            {
                int hmStride = PagedHashMap<long, int, PersistentStore>.RecommendedStride();
                var hmSegment = mmf.AllocateChunkBasedSegment(PageBlockType.None, MainIndexSegmentStartingSize, hmStride, changeSet, StorageSegmentKind.Spatial);
                occupancyMap = PagedHashMap<long, int, PersistentStore>.Create(hmSegment, initialBuckets: 64, changeSet: changeSet);
            }
        }
        else
        {
            treeSegment = existingTreeSegment;
            backPtrSegment = existingBackPtrSegment;
            occupancyMap = existingOccupancyMap;
        }

        var tree = new SpatialRTree<PersistentStore>(treeSegment, variant, load, changeSet);
        tree.BackPointerSegment = backPtrSegment;

        SpatialRTree<PersistentStore> staticTree = null, dynamicTree = null;
        if (fieldInfo.Mode == SpatialMode.Static)
        {
            staticTree = tree;
        }
        else
        {
            dynamicTree = tree;
        }
        SpatialIndex = new SpatialIndexState(staticTree, dynamicTree, backPtrSegment, fieldInfo, descriptor, occupancyMap);
    }

    /// <summary>
    /// Initializes shadow tracking infrastructure for SV tick-boundary index/view maintenance.
    /// Called after BuildIndexedFieldInfo when StorageMode == SingleVersion.
    /// </summary>
    private void InitializeShadowTracking()
    {
        if (IndexedFieldInfos.Length == 0)
        {
            return;
        }

        HasShadowableIndexes = true;
        ShadowBitmap = new DirtyBitmap(ComponentSegment.ChunkCapacity);
        FieldShadowBuffers = new FieldShadowBuffer[IndexedFieldInfos.Length];
        for (int i = 0; i < IndexedFieldInfos.Length; i++)
        {
            FieldShadowBuffers[i] = new FieldShadowBuffer();
        }
    }

    /// <summary>
    /// Populates newly created secondary indexes by scanning all occupied entities.
    /// Called after schema migration creates empty indexes that need backfilling.
    /// </summary>
    internal void PopulateNewIndexes(HashSet<int> newIndexFieldIds, ChangeSet changeSet)
    {
        if (newIndexFieldIds == null || newIndexFieldIds.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(DBE.EpochManager);
        var accessor = ComponentSegment.CreateChunkAccessor(changeSet);
        try
        {
            var capacity = ComponentSegment.ChunkCapacity;
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!ComponentSegment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                // Insert into each new index
                foreach (var ifi in IndexedFieldInfos)
                {
                    if (!newIndexFieldIds.Contains(GetFieldIdForIndex(ifi)))
                    {
                        continue;
                    }

                    var chunkAddr = accessor.GetChunkAddress(chunkId);
                    var keyAddr = chunkAddr + ifi.OffsetToField;
                    ifi.PersistentIndex.Add(keyAddr, chunkId, ref accessor);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Returns the FieldId associated with an IndexedFieldInfo by reverse lookup.
    /// </summary>
    private int GetFieldIdForIndex(IndexedFieldInfo ifi)
    {
        var ro = ComponentOverhead;
        for (int i = 0; i < Definition.MaxFieldId; i++)
        {
            var f = Definition[i];
            if (f != null && f.HasIndex && ro + f.OffsetInComponentStorage == ifi.OffsetToField)
            {
                return f.FieldId;
            }
        }
        return -1;
    }

    private void BuildComponentCollectionInfo(ChangeSet changeSet = null)
    {
        ComponentCollectionVSBSByOffset = new Dictionary<int, VariableSizedBufferSegmentBase<PersistentStore>>();
        foreach (var field in Definition.FieldsByName.Values)
        {
            if (field.Type != FieldType.Collection)
            {
                continue;
            }

            var vsbs = DBE.GetComponentCollectionVSBS(field.DotNetUnderlyingType, changeSet);
            ComponentCollectionVSBSByOffset.Add(field.OffsetInComponentStorage, vsbs);
            _flags |= ComponentTableFlags.HasCollections;
        }
    }

    /// <summary>
    /// Creates a B+Tree index for a field on the given segment. Used by schema evolution to pre-create indexes
    /// on existing segments before the ComponentTable is fully loaded.
    /// </summary>
    internal static BTreeBase<PersistentStore> CreateIndexForFieldStatic(DBComponentDefinition.Field field, short stableId, bool load, ChunkBasedSegment<PersistentStore> segment, 
        ChangeSet changeSet = null) => CreateIndexForFieldCore(field, stableId, load, segment, changeSet);

    private IBTreeIndex CreateIndexForField(DBComponentDefinition.Field field, short stableId, bool load = false, ChangeSet changeSet = null)
    {
        if (StorageMode == StorageMode.Transient)
        {
            return CreateIndexForFieldTransient(field, stableId);
        }

        var s = field.Type == FieldType.String64 ? String64IndexSegment : DefaultIndexSegment;
        return CreateIndexForFieldCore(field, stableId, load, s, changeSet);
    }

    private BTreeBase<TransientStore> CreateIndexForFieldTransient(DBComponentDefinition.Field field, short stableId)
    {
        var s = field.Type == FieldType.String64 ? TransientString64IndexSegment : TransientDefaultIndexSegment;
        BTreeBase<TransientStore> index = field.Type switch
        {
            FieldType.Byte     => field.IndexAllowMultiple ? new ByteMultipleBTree<TransientStore>      (s, false, stableId) : new ByteSingleBTree<TransientStore>    (s, false, stableId),
            FieldType.Short    => field.IndexAllowMultiple ? new ShortMultipleBTree<TransientStore>     (s, false, stableId) : new ShortSingleBTree<TransientStore>   (s, false, stableId),
            FieldType.Int      => field.IndexAllowMultiple ? new IntMultipleBTree<TransientStore>       (s, false, stableId) : new IntSingleBTree<TransientStore>     (s, false, stableId),
            FieldType.Long     => field.IndexAllowMultiple ? new LongMultipleBTree<TransientStore>      (s, false, stableId) : new LongSingleBTree<TransientStore>    (s, false, stableId),
            FieldType.UByte    => field.IndexAllowMultiple ? new UByteMultipleBTree<TransientStore>     (s, false, stableId) : new UByteSingleBTree<TransientStore>   (s, false, stableId),
            FieldType.UShort   => field.IndexAllowMultiple ? new UShortMultipleBTree<TransientStore>    (s, false, stableId) : new UShortSingleBTree<TransientStore>  (s, false, stableId),
            FieldType.UInt     => field.IndexAllowMultiple ? new UIntMultipleBTree<TransientStore>      (s, false, stableId) : new UIntSingleBTree<TransientStore>    (s, false, stableId),
            FieldType.ULong    => field.IndexAllowMultiple ? new ULongMultipleBTree<TransientStore>     (s, false, stableId) : new ULongSingleBTree<TransientStore>   (s, false, stableId),
            FieldType.Float    => field.IndexAllowMultiple ? new FloatMultipleBTree<TransientStore>     (s, false, stableId) : new FloatSingleBTree<TransientStore>   (s, false, stableId),
            FieldType.Double   => field.IndexAllowMultiple ? new DoubleMultipleBTree<TransientStore>    (s, false, stableId) : new DoubleSingleBTree<TransientStore>  (s, false, stableId),
            FieldType.Char     => field.IndexAllowMultiple ? new CharMultipleBTree<TransientStore>      (s, false, stableId) : new CharSingleBTree<TransientStore>    (s, false, stableId),
            FieldType.String64 => field.IndexAllowMultiple ? new String64MultipleBTree<TransientStore>  (s, false, stableId) : new String64SingleBTree<TransientStore>(s, false, stableId),
            _                  => null
        };
        return index;
    }

    internal static BTreeBase<PersistentStore> CreateIndexForFieldCore(DBComponentDefinition.Field field, short stableId, bool load, ChunkBasedSegment<PersistentStore> s, ChangeSet changeSet = null)
    {
        BTreeBase<PersistentStore> index = field.Type switch
        {
            FieldType.Byte     => field.IndexAllowMultiple ? new ByteMultipleBTree<PersistentStore>      (s, load, stableId, changeSet) : new ByteSingleBTree<PersistentStore>    (s, load, stableId, changeSet),
            FieldType.Short    => field.IndexAllowMultiple ? new ShortMultipleBTree<PersistentStore>     (s, load, stableId, changeSet) : new ShortSingleBTree<PersistentStore>   (s, load, stableId, changeSet),
            FieldType.Int      => field.IndexAllowMultiple ? new IntMultipleBTree<PersistentStore>       (s, load, stableId, changeSet) : new IntSingleBTree<PersistentStore>     (s, load, stableId, changeSet),
            FieldType.Long     => field.IndexAllowMultiple ? new LongMultipleBTree<PersistentStore>      (s, load, stableId, changeSet) : new LongSingleBTree<PersistentStore>    (s, load, stableId, changeSet),
            FieldType.UByte    => field.IndexAllowMultiple ? new UByteMultipleBTree<PersistentStore>     (s, load, stableId, changeSet) : new UByteSingleBTree<PersistentStore>   (s, load, stableId, changeSet),
            FieldType.UShort   => field.IndexAllowMultiple ? new UShortMultipleBTree<PersistentStore>    (s, load, stableId, changeSet) : new UShortSingleBTree<PersistentStore>  (s, load, stableId, changeSet),
            FieldType.UInt     => field.IndexAllowMultiple ? new UIntMultipleBTree<PersistentStore>      (s, load, stableId, changeSet) : new UIntSingleBTree<PersistentStore>    (s, load, stableId, changeSet),
            FieldType.ULong    => field.IndexAllowMultiple ? new ULongMultipleBTree<PersistentStore>     (s, load, stableId, changeSet) : new ULongSingleBTree<PersistentStore>   (s, load, stableId, changeSet),
            FieldType.Float    => field.IndexAllowMultiple ? new FloatMultipleBTree<PersistentStore>     (s, load, stableId, changeSet) : new FloatSingleBTree<PersistentStore>   (s, load, stableId, changeSet),
            FieldType.Double   => field.IndexAllowMultiple ? new DoubleMultipleBTree<PersistentStore>    (s, load, stableId, changeSet) : new DoubleSingleBTree<PersistentStore>  (s, load, stableId, changeSet),
            FieldType.Char     => field.IndexAllowMultiple ? new CharMultipleBTree<PersistentStore>      (s, load, stableId, changeSet) : new CharSingleBTree<PersistentStore>    (s, load, stableId, changeSet),
            FieldType.String64 => field.IndexAllowMultiple ? new String64MultipleBTree<PersistentStore>  (s, load, stableId, changeSet) : new String64SingleBTree<PersistentStore>(s, load, stableId, changeSet),
            _                  => null
        };
        return index;
    }

    /// <summary>Releases the table's owned persistent and transient segments (and the heap-backed transient stores). Idempotent — a second call is a no-op.</summary>
    /// <param name="disposing"><c>true</c> when disposing deterministically; <c>false</c> when running from the finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (ComponentSegment == null && TransientComponentSegment == null)
        {
            return;
        }

        if (disposing)
        {
            // Persistent segments
            TailIndexSegment?.Dispose();
            String64IndexSegment?.Dispose();
            DefaultIndexSegment?.Dispose();
            CompRevTableSegment?.Dispose();
            ComponentSegment?.Dispose();

            // Transient segments
            TransientString64IndexSegment?.Dispose();
            TransientDefaultIndexSegment?.Dispose();
            TransientComponentSegment?.Dispose();

            // Transient stores (release heap-pinned memory blocks)
            _transientString64IndexStore?.Dispose();
            _transientDefaultIndexStore?.Dispose();
            _transientComponentStore?.Dispose();

            ComponentSegment = null;
            TransientComponentSegment = null;
        }
        base.Dispose(disposing);
    }
}