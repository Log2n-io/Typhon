using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

public unsafe partial class Transaction
{

    /// <summary>
    /// Creates a Versioned slot's content chunk and its first revision. Called ONLY for a slot the spawn actually supplies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A slot nobody supplied gets nothing: no chunk, no chain, and <c>CompRevFirstChunkId</c> stays 0. That is not a special case bolted on — it is the state
    /// every chain-root reader in the engine already expects, and <c>ArchetypeClusterState</c>'s rebuild says so outright: "Legitimate for a slot never
    /// written". The spawn path used to be the one place that contradicted it, fabricating a revision stamped
    /// <see cref="ComponentInfo.OperationType.Created"/> for a component that was never created, pointing at a chunk allocated with <c>clearContent: false</c>.
    /// Against a recycled chunk, enabling that slot then read the previous entity's committed values (#845).
    /// </para>
    /// <para>
    /// This REPLACES design decision #14 ("Spawn always allocates all components… Omitted components are zero-initialized and disabled"). Zero-init was the
    /// right answer only because <see cref="EntityRefMut.Enable{T}(Comp{T})"/> had no way to tell "never supplied" from "written then disabled" — both being a
    /// clear bit with a live payload — so it had to be total. A missing chain root distinguishes them for free, in a field the record already carries, so
    /// Enable can refuse instead of inventing a value. SingleVersion and Transient keep zero-init because their bytes live in the cluster slot, which exists
    /// the moment the entity does: there is no absent state to represent.
    /// </para>
    /// </remarks>
    private int AllocateVersionedSlotContent(ArchetypeMetadata meta, ComponentTable table, int slot, EntityId entityId, out int compRevChunkId)
    {
        var compType = meta._slotToComponentType[slot];
        var info = GetComponentInfo(compType);
        var chunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
        compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, TSN, UowId, chunkId, (long)entityId.RawValue);

        var cri = new ComponentInfo.CompRevInfo
        {
            Operations = ComponentInfo.OperationType.Created,
            PrevCompContentChunkId = 0,
            PrevRevisionIndex = -1,
            ReadCommitSequence = 1,
            CurCompContentChunkId = chunkId,
            CompRevTableFirstChunkId = compRevChunkId,
            CurRevisionIndex = 0,
        };
        info.AddNew((long)entityId.RawValue, cri);
        return chunkId;
    }

    /// <inheritdoc/>
    internal override int CreateVersionedContentAndWrite<T>(EntityId id, byte slot, in T value)
    {
        EnsureMutable();

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        var table = engineState.SlotToComponentTable[slot];
        var compType = meta._slotToComponentType[slot];
        var info = GetComponentInfo(compType);

        // An entity still pending in THIS transaction has no EntityMap record yet, so there is nothing for the mid-life publication path to write a chain root
        // into. Worse, PlaceSpawns writes SetCompRevFirstChunkId(recordPtr, vi, entry.Rev[slot]) unconditionally, so a root published any other way would be
        // clobbered by a still-zero entry.Rev. The spawn owns this entity's publication: record the allocation in its SpawnEntry and let PlaceSpawns do the
        // one thing it already does correctly.
        int spawnIdx = SpawnedIndexOf(id);
        if (spawnIdx >= 0)
        {
            ref var entry = ref CollectionsMarshal.AsSpan(_spawnedEntities)[spawnIdx];
            if (entry.VerLoc[slot] == 0)
            {
                entry.VerLoc[slot] = AllocateVersionedSlotContent(meta, table, slot, id, out var spawnRev);
                entry.Rev[slot] = spawnRev;
            }

            entry.EnabledBits |= (ushort)(1 << slot);

            var spawnDst = info.CompContentAccessor.GetChunkAddress(entry.VerLoc[slot], true);
            Unsafe.AsRef<T>(spawnDst + table.ComponentOverhead) = value;
            return entry.VerLoc[slot];
        }

        // Same construction the spawn path uses, at the CURRENT TSN rather than the spawn's, so the revision is a normal mid-life creation: a reader on an
        // older snapshot resolves no chain for this slot and correctly sees the component as absent.
        var chunkId = AllocateVersionedSlotContent(meta, table, slot, id, out _);

        // A first supply: claimed at the commit, so a concurrent one of the same component joins one chain (QENABLE-01, Transaction.ClaimFirstSupplies).
        (_firstSupplies ??= []).Add(((long)id.RawValue, info.ComponentTypeId));

        var dst = info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
        Unsafe.WriteUnaligned(ref dst[table.ComponentOverhead], value);
        return chunkId;
    }

    /// <summary>Native staging for spawned-but-unpublished payloads. Created on first spawn; rewound on reset; freed on dispose.</summary>
    private SpawnStagingArena _spawnArena;

    // The snapshot column (VIS-12): one per transaction, from the engine's allocator, made on the first query that needs one and kept across pooled uses.
    private PinnedMemoryBlock _snapshotColumn;
    private static int s_snapshotColumnIds;

    /// <summary>The snapshot column's smallest allocation. Settable for tests: no spawnable component's column outgrows the default.</summary>
    internal static int SnapshotColumnMinBytes = 4096;

    /// <summary>
    /// A buffer of at least <paramref name="bytes"/> for a cluster's component column at this snapshot (VIS-12), reused by every query of the transaction:
    /// asked for only when a cluster an update newer than the snapshot touched is met, so a query that meets none allocates and clears nothing. Native and
    /// never moved (the evaluators address it by pointer); contents are left as the last query wrote them — every slot read is written first.
    /// </summary>
    internal unsafe byte* SnapshotColumn(int bytes)
    {
        if (_snapshotColumn == null || _snapshotColumn.MemoryBlockSize < bytes)
        {
            _snapshotColumn?.Dispose();
            _snapshotColumn = null;
            // A resource id of its own: the tree registers a child by id, so a shared one registered only the first transaction's column.
            _snapshotColumn = _dbe.MemoryAllocator.AllocatePinned($"SnapshotColumn#{Interlocked.Increment(ref s_snapshotColumnIds)}", _dbe.TransactionChain,
                Math.Max(bytes, SnapshotColumnMinBytes), false, 64);
        }

        return _snapshotColumn.DataAsPointer;
    }

    /// <summary>The snapshot column's size in bytes, 0 before the first query that needed one. For tests.</summary>
    internal int SnapshotColumnBytes => _snapshotColumn?.MemoryBlockSize ?? 0;

    /// <summary>The spawn payload arena, created on first use. Only a transaction can spawn, so only a transaction owns one.</summary>
    internal SpawnStagingArena SpawnArena => _spawnArena ??= new SpawnStagingArena();

    private protected override SpawnStagingArena SpawnArenaOrNull => _spawnArena;

    /// <summary>Rewinds the arena between pooled uses, retaining one block. Frees nothing — see <see cref="DisposeSpawnArena"/>.</summary>
    private protected void ResetSpawnArena() => _spawnArena?.Reset();

    /// <summary>
    /// Releases the arena's native memory. Called when this transaction is discarded for good rather than returned to the pool.
    /// </summary>
    internal void DisposeSpawnArena()
    {
        _spawnArena?.Dispose();
        _spawnArena = null;
        _snapshotColumn?.Dispose();   // its native column too: kept across pooled uses like the arena's block, freed with it
        _snapshotColumn = null;
    }
    // ═══════════════════════════════════════════════════════════════════════
    // ECS State
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Spawned entity data — flat list for sequential iteration at commit time.</summary>
    private List<SpawnEntry> _spawnedEntities;

    /// <summary>O(1) lookup: EntityId → index into <see cref="_spawnedEntities"/>. Built lazily on first Contains/IndexOf call.</summary>
    private Dictionary<EntityId, int> _spawnedEntityIndex;
    private bool _spawnedEntityIndexStale;

    /// <summary>Lightweight spawn record: EntityId + EnabledBits + per-slot chunk IDs. No heap allocation.</summary>
    internal struct SpawnEntry
    {
        public EntityId Id;
        public ushort EnabledBits;
        /// <summary>
        /// Per-slot payload address for a spawned-but-unpublished entity. <b>What it addresses depends on the slot's storage mode</b>, which is why the two
        /// homes are separate fields rather than one overloaded <c>int</c>: a VERSIONED slot holds a real
        /// <see cref="ComponentTable.ComponentSegment"/> content chunk id — the first revision's payload, owned by the chain — while a SingleVersion or
        /// Transient slot holds a <see cref="SpawnStagingArena"/> handle, because its bytes belong in the cluster slot and allocating a content chunk for them
        /// produced one that no <c>ClusterEntityRecord</c> could address and nothing could free (#839).
        /// <para>
        /// Both use <c>0</c> for "this slot has no payload". Do not merge them: an int that means a chunk id in one archetype and an arena handle in another,
        /// distinguished only by a lookup the reader has to remember to perform, is the ST-05 bug class (<c>rules/spatial.md</c>) that caused #548.
        /// </para>
        /// </summary>
        public fixed int VerLoc[16];
        /// <summary>Per-slot spawn-arena handles for SingleVersion and Transient slots. See <see cref="VerLoc"/> for why this is a separate field.</summary>
        public fixed int Stage[16];
        /// <summary>Per-slot compRevFirstChunkIds for Versioned components (used at commit for EntityRecord).</summary>
        public fixed int Rev[16];

        /// <summary>
        /// The realm validated at Spawn for a realm-keyed archetype (Realms C1). The staged key can still be rewritten in place before commit (an own-spawn
        /// write); placement falls back to this one when that key is not a valid realm (D-2).
        /// </summary>
        public ushort SpawnRealm;

        /// <summary>Set by <see cref="PlaceSpawns"/> in PREPARE, cleared by <see cref="PublishSpawns"/> or <see cref="UnplaceSpawns"/> (VIS-08).</summary>
        public bool Placed;
        /// <summary>The reserved slot's cluster, or -1 for a legacy (non-cluster) record.</summary>
        public int PlacedClusterChunkId;
        /// <summary>The reserved slot within <see cref="PlacedClusterChunkId"/>.</summary>
        public byte PlacedSlot;
    }

    /// <summary>
    /// The location of one slot of a spawned-but-unpublished entity, in whichever address space that slot uses.
    /// </summary>
    /// <remarks>
    /// Since #839 the answer depends on storage mode — a Versioned slot has a real content chunk (the first revision's payload), everything else has a
    /// <see cref="SpawnStagingArena"/> handle. Both callers that hand these to an <see cref="EntityRef"/> go through here so the choice is made once;
    /// the ref's read and write paths then disambiguate on <c>_isOwnSpawn</c> (see <c>EntityAccessor.ResolveSpawnAwarePayload</c>).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SpawnSlotLocation(in SpawnEntry entry, ComponentTable table, int slot) =>
        table.StorageMode == StorageMode.Versioned ? entry.VerLoc[slot] : entry.Stage[slot];

    /// <summary>The per-slot component tables for <paramref name="meta"/>, for callers that must pick a spawn slot's address space.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ComponentTable[] SlotTablesFor(ArchetypeMetadata meta) => _dbe._archetypeStates[meta.ArchetypeId].SlotToComponentTable;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool SpawnedContains(EntityId id)
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return false;
        }
        RebuildSpawnedIndex();
        return _spawnedEntityIndex.ContainsKey(id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SpawnedIndexOf(EntityId id)
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return -1;
        }
        RebuildSpawnedIndex();
        return _spawnedEntityIndex.TryGetValue(id, out int idx) ? idx : -1;
    }

    private void RebuildSpawnedIndex()
    {
        if (!_spawnedEntityIndexStale)
        {
            return;
        }
        _spawnedEntityIndex ??= new Dictionary<EntityId, int>(_spawnedEntities.Count);
        _spawnedEntityIndex.Clear();
        for (int i = 0; i < _spawnedEntities.Count; i++)
        {
            _spawnedEntityIndex[_spawnedEntities[i].Id] = i;
        }
        _spawnedEntityIndexStale = false;
    }

    /// <summary>Pending entity destroys. Flushed at commit (DiedTSN set). HashSet for O(1) Contains.</summary>
    private HashSet<EntityId> _pendingDestroys;

    /// <summary>Archetype membership channels this commit structurally changed; their epochs are bumped once each, after every append (#790).</summary>
    private List<ArchetypeMembershipRegistry> _membershipTouched;

    /// <summary>
    /// True when this transaction holds ECS structural work that has not been committed — spawns or destroys visible only to itself.
    /// </summary>
    /// <remarks>
    /// A membership view refreshed against such a transaction must not take the channel: the channel carries committed entries only, and
    /// uncommitted work moves no structural epoch, so the gate would report "nothing changed" while the transaction's own reads disagree with
    /// the view it just refreshed. <c>RefreshPull</c> folds the overlay in — pending spawns included, pending destroys excluded — which is what
    /// the pull path always did.
    /// </remarks>
    /// <summary>Per-commit snapshot of each touched archetype's subscriber array, parallel to <see cref="_membershipTouched"/> (#790).</summary>
    private List<ViewRegistration[]> _membershipViewSnapshots;

    internal bool HasPendingEcsWork => _spawnedEntities is { Count: > 0 } || _pendingDestroys is { Count: > 0 };

    /// <summary>Pending EnabledBits changes — keyed by EntityId.</summary>
    private Dictionary<EntityId, ushort> _pendingEnableDisable;

    // ═══════════════════════════════════════════════════════════════════════
    // ECS Queries
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a polymorphic query matching <typeparamref name="TArchetype"/> and all descendants.
    /// Supports Tier 1 (.With, .Without, .Exclude), Tier 2 (.Enabled, .Disabled), and execution (.Execute, .Count, .Any, foreach).
    /// </summary>
    public EcsQuery<TArchetype> Query<TArchetype>(
        [CallerFilePath]   string sourceFile = null,
        [CallerLineNumber] int    sourceLine = 0,
        [CallerMemberName] string sourceMethod = null)
        where TArchetype : class
        => new(this, true, sourceFile, sourceLine, sourceMethod);

    /// <summary>Create an exact query matching only <typeparamref name="TArchetype"/>, no descendants.</summary>
    public EcsQuery<TArchetype> QueryExact<TArchetype>(
        [CallerFilePath]   string sourceFile = null,
        [CallerLineNumber] int    sourceLine = 0,
        [CallerMemberName] string sourceMethod = null)
        where TArchetype : class
        => new(this, false, sourceFile, sourceLine, sourceMethod);

    /// <summary>
    /// O(1) metadata count of live entities for <typeparamref name="TArchetype"/> and descendants.
    /// Uses LinearHash.EntryCount — fast but includes entities with DiedTSN set (not yet cleaned up).
    /// For exact counts respecting visibility, use <c>Query&lt;T&gt;().Count()</c>.
    /// </summary>
    public long EcsCount<TArchetype>() where TArchetype : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TArchetype>();
        if (meta?.SubtreeArchetypeIds == null)
        {
            return 0;
        }

        long total = 0;
        foreach (var id in meta.SubtreeArchetypeIds)
        {
            var m = ArchetypeRegistry.GetMetadata(id);
            if (m != null)
            {
                var es = _dbe._archetypeStates[m.ArchetypeId];
                if (es?.EntityMap != null)
                {
                    total += es.EntityMap.EntryCount;
                }
            }
        }
        return total;
    }

    /// <summary>
    /// Non-generic enumeration of every entity in a single exact archetype, visible at this transaction's snapshot (TSN). The runtime counterpart to
    /// <see cref="Query{TArchetype}"/> for tooling that only knows the archetype by its id at runtime (e.g. the Workbench Data Browser). Walks the archetype's
    /// entity map directly, so it works for both cluster and legacy storage. Entities pending destroy in this transaction are excluded; entities spawned (and
    /// not yet committed) in this transaction are NOT included — use the typed <see cref="Query{TArchetype}"/> path when read-your-own-writes is required.
    /// <para>
    /// Prefer the generic <see cref="Query{TArchetype}"/> whenever the archetype is known at compile time: it adds Tier-1/2/3 filtering, ordering, and paging,
    /// and avoids materializing a <see cref="List{T}"/> of every id. Reach for this overload only when the archetype type is not available statically.
    /// </para>
    /// </summary>
    /// <param name="routingId">The exact archetype to enumerate, identified by its per-DB routing id (the value carried in <see cref="EntityId.ArchetypeId"/>
    /// and persisted as <c>ArchetypeR1.RoutingId</c>). No subtree / polymorphic expansion.</param>
    /// <returns>
    /// Entity ids in entity-map iteration order — deterministic for a given snapshot. Empty when the routing id is unknown or has no engine state. Pair each
    /// id with <see cref="EntityAccessor.Open"/> + <see cref="EntityRef.ReadRaw(int, Span{byte})"/> to decode component values without a compile-time type.
    /// </returns>
    public List<EntityId> EnumerateArchetypeEntities(ushort routingId)
    {
        var results = new List<EntityId>();
        var states = _dbe._stateByRouting;
        if (states == null || routingId >= states.Length)
        {
            return results;
        }

        var engineState = states[routingId];
        if (engineState?.EntityMap == null)
        {
            return results;
        }

        // Read and released (EP-02): a whole archetype's map can be larger than the cache, and an epoch-tagged walk pinned all of it for the transaction.
        var accessor = engineState.EntityMap.Segment.CreateScanAccessor();
        var action = new ArchetypeEntityCollectAction
        {
            RoutingId = routingId,
            TxTsn = TSN,
            Results = results,
            PendingDestroys = _pendingDestroys,
            Cards = CommitCards,
            Map = engineState.EntityMap,
        };
        try
        {
            engineState.EntityMap.ForEachEntry(ref accessor, ref action, BeginScanYield());
        }
        finally
        {
            accessor.Dispose();
        }

        return results;
    }

    /// <summary>
    /// <see cref="RawValuePagedHashMap{TKey,TStore}.IEntryAction{TKey}"/> for <see cref="EnumerateArchetypeEntities"/>: collects every entity-map entry visible
    /// at <see cref="TxTsn"/> (committed and not yet died), skipping ids pending destroy in the owning transaction. Mirrors the visibility filter in EcsQuery's
    /// broad-scan action; no Tier-2 (enabled/disabled) filtering — the Data Browser shows every entity of the archetype.
    /// </summary>
    private struct ArchetypeEntityCollectAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public ushort RoutingId;
        public long TxTsn;
        public List<EntityId> Results;
        public HashSet<EntityId> PendingDestroys;
        public CommitCardTable Cards;
        public RawValuePagedHashMap<long, PersistentStore> Map;

        public bool Process(long key, byte* value)
        {
            // MVCC visibility: not-yet-born or already-died entities are invisible at this snapshot; a pending born through its owner's card (VIS-08).
            if (!EntityVisibility.IsVisibleInScan(value, TxTsn, Cards, Map, key))
            {
                return true;
            }

            var entityId = new EntityId(key, RoutingId);
            if (PendingDestroys != null && PendingDestroys.Contains(entityId))
            {
                return true;
            }

            Results.Add(entityId);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Guard: this archetype can be spawned into — it has metadata, and this database has per-archetype state for it.
    /// Unconditional, not strict-mode gated (#1095).
    /// </summary>
    /// <typeparam name="TArch">The archetype the caller named.</typeparam>
    /// <param name="meta">Its metadata, as already loaded by the caller.</param>
    /// <exception cref="InvalidOperationException">The archetype has no metadata, or this database was never initialized for it.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why this is not behind <see cref="CheckConfig.Enabled"/>.</b> That gate's bargain is "a diagnostic when strict mode is on, silence when it is
    /// off", and it holds for a check whose failure is harmless. Both subjects here are dereferenced immediately after — <c>meta!.ArchetypeId</c> on the
    /// next line, and the engine state inside <c>SpawnInternal</c> — so with the checks folded away the caller got a
    /// <see cref="NullReferenceException"/> from engine internals for the most ordinary mistake there is: never calling
    /// <see cref="DatabaseEngine.InitializeArchetypes"/>. That reads as an engine bug and gets filed as one. Same defect as the four fluent spatial
    /// predicates in #897.
    /// </para>
    /// <para>
    /// <b>The second condition is the one that fires, and the first is near-unreachable.</b> Worth writing down, because the obvious reading is backwards:
    /// <c>Archetype&lt;TArch&gt;.Metadata</c> is <c>_metadata ?? EnsureFinalized()</c>, and <c>EnsureFinalized</c> registers the archetype in the process
    /// catalog on read — so merely asking for it makes it non-null. A declared archetype therefore has metadata whether or not anyone registered its
    /// components. What "forgot to register" actually looks like is metadata present and this database's <c>_archetypeStates</c> slot empty, which is the
    /// <c>EntityMap</c> condition. The null-metadata branch stays as a belt-and-braces guard against an engine-side registry failure, not a user mistake.
    /// </para>
    /// <para>
    /// <b>And it is free on a per-entity path, which is why the cost note that used to sit here does not apply.</b> That note said the gate had to
    /// short-circuit before the array index because the index itself can throw and so cannot be folded. True, but the index is paid regardless: the very
    /// next call, <c>SpawnInternal</c>, opens with <c>_dbe._archetypeStates[meta.ArchetypeId]</c>, and so do the batch paths. The check adds one field
    /// load and one compare ahead of a B+Tree insert and an MVCC write.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequireSpawnable<TArch>(ArchetypeMetadata meta) where TArch : Archetype<TArch>
    {
        if (meta == null || _dbe._archetypeStates[meta.ArchetypeId]?.EntityMap == null)
        {
            ThrowNotSpawnable<TArch>(meta == null ? null : _dbe.SkippedArchetypeReason(meta.ArchetypeId));
        }
    }

    /// <summary>Out of line, so the message is not built into every inlined spawn.</summary>
    /// <typeparam name="TArch">The archetype the caller named.</typeparam>
    /// <param name="skipReason">Why InitializeArchetypes skipped it, when it did (#1071): the component that was never registered.</param>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotSpawnable<TArch>(string skipReason) =>
        throw new InvalidOperationException(skipReason != null
            ? $"Archetype {typeof(TArch).Name} is not registered with this database, so there is nothing to spawn into: InitializeArchetypes skipped it "
              + $"because {skipReason}."
            : $"Archetype {typeof(TArch).Name} is not registered with this database, so there is nothing to spawn into. Register its components with "
              + "DatabaseEngine.RegisterComponentFromAccessor (or RegisterComponents) and then call DatabaseEngine.InitializeArchetypes before spawning.");

    /// <summary>
    /// Spawns a new entity of archetype <typeparamref name="TArch"/> with the supplied initial component values.
    /// Components not covered by <paramref name="values"/> are zero-initialized and disabled.
    /// The entity is stored in a pending map and inserted into the LinearHash at commit with BornTSN = TSN.
    /// </summary>
    /// <typeparam name="TArch">Concrete archetype type of the entity to spawn.</typeparam>
    /// <param name="values">Initial component values; components omitted here are zero-initialized and disabled.</param>
    /// <returns>The id of the newly-spawned entity.</returns>
    public EntityId Spawn<TArch>(params ReadOnlySpan<ComponentValue> values) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        RequireSpawnable<TArch>(meta);

        var scope = TyphonEvent.BeginEcsSpawn(meta!.ArchetypeId);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. SpawnInternal is engine-internal (allocation + B+Tree insert + MVCC).
        // If a future change adds a user-callback path, re-tag to variant B.
        scope.Tsn = TSN;
        var id = SpawnInternal(meta, values);
        scope.EntityId = id.RawValue;
        // PROFILING-SPAN-NO-THROW-END
        scope.Dispose();
        return id;
    }

    /// <summary>
    /// Spawn a batch of entities. Amortizes per-call overhead: single EnsureMutable check, single Interlocked.Add for all entity keys, single epoch
    /// refresh at the end.
    /// All entities are initialized with the same component values (or zero if none provided).
    /// </summary>
    public void SpawnBatch<TArch>(Span<EntityId> ids, params ComponentValue[] sharedValues) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        RequireSpawnable<TArch>(meta);

        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        int count = ids.Length;

        // Realms: the batch shares one value set, so one validation covers every entity, before any key is allocated (D-2: throw at the call).
        ushort spawnRealm = 0;
        if (engineState.ClusterState is { SpatialSlot.HasRealmKey: true } keyedState)
        {
            spawnRealm = ValidateSpawnRealm(meta, keyedState, sharedValues);
        }

        // Allocate N entity keys in one atomic operation
        long baseKey = Interlocked.Add(ref engineState.NextEntityKey, count) - count + 1;

        // #620 — record the cohort as a range. The single-entity Spawn path emits one EcsSpawn each; doing that here would cost ~56 B per entity for data
        // the range already determines (every id is (baseKey + n, routingId)). Emitted here rather than after the loop so a mid-batch throw cannot drop it —
        // the trace records the attempt, and a rolled-back batch is correctly reported by the Workbench as spawned-but-not-alive.
        if (count > 0)
        {
            TyphonEvent.EmitEcsSpawnBatch(meta.ArchetypeId, _dbe.RoutingIdOf(meta), baseKey, count, TSN);
        }

        _spawnedEntities ??= new List<SpawnEntry>(count);
        _spawnedEntityIndexStale = true;

        for (int n = 0; n < count; n++)
        {
            var entityId = new EntityId(baseKey + n, _dbe.RoutingIdOf(meta));
            ids[n] = entityId;

            var entry = new SpawnEntry { Id = entityId, EnabledBits = 0, SpawnRealm = spawnRealm };

            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];

                // #839: a content chunk only for Versioned — see SpawnInternal for the full reasoning.
                var isVersioned = table.StorageMode == StorageMode.Versioned;

                // #845: find the supplied value BEFORE allocating, so an unsupplied Versioned slot gets no chunk and no chain.
                int slotTypeId = meta._componentTypeIds[slot];
                int sharedIndex = -1;
                for (int v = 0; v < sharedValues.Length; v++)
                {
                    if (sharedValues[v].ComponentTypeId == slotTypeId)
                    {
                        sharedIndex = v;
                        break;
                    }
                }

                int chunkId = 0;
                int stage = isVersioned ? 0 : SpawnArena.Alloc(table.ComponentOverhead + table.ComponentStorageSize);
                if (isVersioned && sharedIndex >= 0)
                {
                    chunkId = AllocateVersionedSlotContent(meta, table, slot, entityId, out var compRevChunkId);
                    entry.Rev[slot] = compRevChunkId;
                }

                if (sharedIndex >= 0)
                {
                    int overhead = table.ComponentOverhead;
                    Span<byte> dst;
                    if (isVersioned)
                    {
                        var compType = meta._slotToComponentType[slot];
                        var info = GetComponentInfo(compType);
                        dst = info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                    }
                    else
                    {
                        dst = new Span<byte>(SpawnArena.Resolve(stage), overhead + table.ComponentStorageSize);
                    }
                    int copySize = Math.Min(sharedValues[sharedIndex].DataSize, dst.Length - overhead);
                    // A span, not a pointer: sharedValues is a managed array. The payload starts 12 bytes into a ComponentValue.
                    MemoryMarshal.CreateReadOnlySpan(
                            ref Unsafe.Add(ref Unsafe.As<ComponentValue, byte>(ref sharedValues[sharedIndex]), 12), copySize)
                        .CopyTo(dst.Slice(overhead));
                    entry.EnabledBits |= (ushort)(1 << slot);
                }

                entry.VerLoc[slot] = chunkId;
                entry.Stage[slot] = stage;
            }

            _spawnedEntityIndexStale = true;
            _spawnedEntities.Add(entry);

            // Epoch refresh every 128 entities to avoid holding epoch too long
            if ((n & 127) == 127)
            {
                _epochManager.RefreshScope();
            }
        }

        CheckEpochRefresh();
    }

    /// <summary>
    /// Allocate a batch of entities with chunks but no component data (all EnabledBits = 0).
    /// Returns the base index into the internal spawn list for use with <see cref="SpawnBatchWriteAll{T}"/>.
    /// Called by source-generated SpawnBatch methods for per-entity SOA data.
    /// </summary>
    public int SpawnBatchAllocate<TArch>(int count, Span<EntityId> ids) where TArch : Archetype<TArch>
    {
        var meta = Archetype<TArch>.Metadata;
        RequireSpawnable<TArch>(meta);
        CheckConfig.Require(CheckConfig.Enabled, ids.Length >= count, $"ids span must be at least count elements");

        if (count == 0)
        {
            return _spawnedEntities?.Count ?? 0;
        }

        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];

        // Allocate N entity keys in one atomic operation
        long baseKey = Interlocked.Add(ref engineState.NextEntityKey, count) - count + 1;

        // #620 — same range record as SpawnBatch; this is the generated-SOA entry point and would otherwise be a second silent hole. `count == 0` returned
        // above, so the range is always non-empty here.
        TyphonEvent.EmitEcsSpawnBatch(meta.ArchetypeId, _dbe.RoutingIdOf(meta), baseKey, count, TSN);

        _spawnedEntities ??= new List<SpawnEntry>(count);
        // O4: ensure capacity when list already exists from prior spawns in this tx
        if (_spawnedEntities.Capacity < _spawnedEntities.Count + count)
        {
            _spawnedEntities.EnsureCapacity(_spawnedEntities.Count + count);
        }
        _spawnedEntityIndexStale = true;

        // O2: pre-extend list, then write entries in-place via span — avoids N copies of 138-byte SpawnEntry
        int baseIndex = _spawnedEntities.Count;
        CollectionsMarshal.SetCount(_spawnedEntities, baseIndex + count);
        var writeSpan = CollectionsMarshal.AsSpan(_spawnedEntities).Slice(baseIndex);

        for (int n = 0; n < count; n++)
        {
            var entityId = new EntityId(baseKey + n, _dbe.RoutingIdOf(meta));
            ids[n] = entityId;

            ref var entry = ref writeSpan[n];
            entry.Id = entityId;
            entry.EnabledBits = 0;

            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];

                // #839: a content chunk only for Versioned — see SpawnInternal for the full reasoning.
                var isVersioned = table.StorageMode == StorageMode.Versioned;

                // #845: this entry point supplies NO values — SpawnBatchWriteAll fills them in afterwards, one component at a time. So a Versioned slot gets
                // nothing here and SpawnBatchWriteAll allocates its chunk and chain on first write. A batch that writes 2 of 5 components therefore allocates
                // 2 chunks per entity rather than 5, and a component never written stays genuinely absent instead of holding a recycled chunk's bytes.
                entry.VerLoc[slot] = 0;
                entry.Stage[slot] = isVersioned ? 0 : SpawnArena.Alloc(table.ComponentOverhead + table.ComponentStorageSize);
            }

            if ((n & 127) == 127)
            {
                _epochManager.RefreshScope();
            }
        }

        CheckEpochRefresh();
        return baseIndex;
    }

    /// <summary>
    /// Write an entire component span into already-allocated spawn entries. Resolves slot/table/accessor ONCE,
    /// then loops N writes with zero dictionary lookups. Called by source-generated SpawnBatch methods.
    /// </summary>
    public void SpawnBatchWriteAll<T>(int baseIndex, int count, Comp<T> comp, ReadOnlySpan<T> values) where T : unmanaged
    {
        CheckConfig.Require(CheckConfig.Enabled, values.Length >= count, $"values span must be at least count elements");
        if (count == 0)
        {
            return;
        }

        // Resolve everything ONCE — no per-entity dictionary lookups
        var span = CollectionsMarshal.AsSpan(_spawnedEntities);
        var meta = _dbe.GetMetaByRouting(span[baseIndex].Id.ArchetypeId);
        byte slot = meta.GetSlot(comp._componentTypeId);
        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        var table = engineState.SlotToComponentTable[slot];
        var info = GetComponentInfo(typeof(T));
        int overhead = table.ComponentOverhead;
        ushort bitMask = (ushort)(1 << slot);

        // #839: SpawnBatchAllocate stages non-Versioned payloads in the transaction arena, so the write lands there. A Versioned slot still has a real content
        // chunk (the first revision's payload) and is written through the component accessor as before.
        var isVersioned = table.StorageMode == StorageMode.Versioned;
        var arena = isVersioned ? null : SpawnArena;

        for (int i = 0; i < count; i++)
        {
            ref var entry = ref span[baseIndex + i];

            // #845: SpawnBatchAllocate leaves a Versioned slot with no chunk and no chain, because it does not know which components will be written. This is
            // this first write, so the content is created here. The zero test is the same "never written" state every chain-root reader in the engine already
            // recognizes; a second write to the same slot in the same batch finds a non-zero id and reuses it.
            if (isVersioned && entry.VerLoc[slot] == 0)
            {
                entry.VerLoc[slot] = AllocateVersionedSlotContent(meta, table, slot, entry.Id, out var compRevChunkId);
                entry.Rev[slot] = compRevChunkId;
            }

            byte* ptr = isVersioned ? info.CompContentAccessor.GetChunkAddress(entry.VerLoc[slot], true) : arena.Resolve(entry.Stage[slot]);

            Unsafe.AsRef<T>(ptr + overhead) = values[i];
            entry.EnabledBits |= bitMask;
        }
    }

    /// <summary>
    /// <see cref="SpawnBatchAllocate{TArch}"/> with the entity keys supplied by the caller rather than allocated here, and without the generic parameter
    /// (#1102).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the keys come in.</b> <c>ctx.Commands.Spawn</c> hands the caller a final <see cref="EntityId"/> at the moment it is called, from a per-chunk
    /// key block (#1099), so by the time the fence drains the buffer the keys are already issued and already in the caller's components. Allocating fresh
    /// ones here would hand out a second identity for the same entity and break every reference the producing system stored.
    /// </para>
    /// <para>
    /// <b>Why a span of keys and not a base plus a count.</b> The blocks are per (archetype, chunk), so one archetype's drained keys are contiguous within
    /// a chunk's run and jump between runs. <c>baseKey + n</c> is true of <see cref="SpawnBatchAllocate{TArch}"/>'s own allocation and false of this one.
    /// </para>
    /// <para>
    /// <b>Why non-generic.</b> The drain is type-erased — it holds <see cref="ComponentValue"/>, which carries a component type id, not a <c>T</c> — so it
    /// cannot name <c>TArch</c>. The metadata it has is enough: this method uses nothing from the generic parameter that <paramref name="meta"/> does not
    /// already carry.
    /// </para>
    /// <para>
    /// Deliberately a sibling rather than a refactor: <see cref="SpawnBatchAllocate{TArch}"/> carries the #839 and #845 reasoning for what a Versioned slot
    /// gets at allocation time, and the two bodies have to stay readable against each other.
    /// </para>
    /// </remarks>
    /// <param name="meta">The archetype being spawned into. Must be registered with this database.</param>
    /// <param name="entityKeys">The already-issued entity keys, one per entity.</param>
    /// <param name="ids">Receives the entity ids. Must hold at least <paramref name="entityKeys"/>.Length.</param>
    /// <returns>The base index into the spawn list, for <see cref="SpawnWriteValueRaw"/>.</returns>
    internal int SpawnBatchAllocateRaw(ArchetypeMetadata meta, ReadOnlySpan<long> entityKeys, Span<EntityId> ids)
    {
        var count = entityKeys.Length;
        if (count == 0)
        {
            return _spawnedEntities?.Count ?? 0;
        }

        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        var routingId = _dbe.RoutingIdOf(meta);

        // No EmitEcsSpawnBatch range record here: the keys are not a range. The producing side already emitted per-command telemetry when the id was issued,
        // which is also where a trace reader wants it — at the call that caused the spawn, not at the fence that applied it.
        _spawnedEntities ??= new List<SpawnEntry>(count);
        if (_spawnedEntities.Capacity < _spawnedEntities.Count + count)
        {
            _spawnedEntities.EnsureCapacity(_spawnedEntities.Count + count);
        }

        _spawnedEntityIndexStale = true;

        var baseIndex = _spawnedEntities.Count;
        CollectionsMarshal.SetCount(_spawnedEntities, baseIndex + count);
        var writeSpan = CollectionsMarshal.AsSpan(_spawnedEntities).Slice(baseIndex);

        for (var n = 0; n < count; n++)
        {
            var entityId = new EntityId(entityKeys[n], routingId);
            ids[n] = entityId;

            ref var entry = ref writeSpan[n];
            entry.Id = entityId;
            entry.EnabledBits = 0;

            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                var isVersioned = table.StorageMode == StorageMode.Versioned;

                // #839 / #845, as SpawnBatchAllocate: a Versioned slot gets no content chunk until something writes it, so a batch that writes two of five
                // components allocates two chunks per entity rather than five.
                entry.VerLoc[slot] = 0;
                entry.Stage[slot] = isVersioned ? 0 : SpawnArena.Alloc(table.ComponentOverhead + table.ComponentStorageSize);
            }

            if ((n & 127) == 127)
            {
                _epochManager.RefreshScope();
            }
        }

        CheckEpochRefresh();
        return baseIndex;
    }

    /// <summary>
    /// Writes one <see cref="ComponentValue"/> into one already-allocated spawn entry (#1102) — the type-erased counterpart of
    /// <see cref="SpawnBatchWriteAll{T}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One value at a time, where the generic version writes one component across N entities.</b> That is the shape the drain has: a command carries its
    /// own value set, so the buffer is array-of-structures per entity, not structure-of-arrays per component. Transposing it to feed the striding version
    /// would cost a pass over the payloads and a scratch buffer to save a per-call resolve — which is worth doing when this shows up in a profile and not
    /// before.
    /// </para>
    /// <para>
    /// <b>The copy is span to pointer, and the direction matters.</b> The destination is a page-cache chunk or the transaction's native spawn arena, both
    /// engine-owned. The source is a <see cref="ComponentValue"/> living in a managed array, read through a <c>ref</c>. No pointer is ever taken over the
    /// managed side — the project rule with the <c>0x80131506</c> crash behind it.
    /// </para>
    /// </remarks>
    /// <param name="entryIndex">Index into the spawn list, from <see cref="SpawnBatchAllocateRaw"/>.</param>
    /// <param name="value">The component value to write. Its component type id selects the slot.</param>
    internal void SpawnWriteValueRaw(int entryIndex, in ComponentValue value)
    {
        var span = CollectionsMarshal.AsSpan(_spawnedEntities);
        ref var entry = ref span[entryIndex];
        var meta = _dbe.GetMetaByRouting(entry.Id.ArchetypeId);
        if (!meta.TryGetSlot(value.ComponentTypeId, out var slot))
        {
            // A value for a component this archetype does not have. Refused at the write rather than ignored: the producing side validated the archetype,
            // so a stray component id here means the buffer or the drain disagreed about which archetype a command belonged to.
            ThrowHelper.ThrowInvalidOp(
                $"Component type {value.ComponentTypeId} is not part of archetype '{meta.ArchetypeType?.Name ?? meta.ArchetypeId.ToString()}', so a "
                + "deferred spawn's value cannot be written to it.");
        }

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        var table = engineState.SlotToComponentTable[slot];
        var isVersioned = table.StorageMode == StorageMode.Versioned;

        // #845: the first write to a Versioned slot is what creates its content, exactly as in SpawnBatchWriteAll.
        if (isVersioned && entry.VerLoc[slot] == 0)
        {
            entry.VerLoc[slot] = AllocateVersionedSlotContent(meta, table, slot, entry.Id, out var compRevChunkId);
            entry.Rev[slot] = compRevChunkId;
        }

        byte* ptr;
        if (isVersioned)
        {
            var info = GetComponentInfo(meta._slotToComponentType[slot]);
            ptr = info.CompContentAccessor.GetChunkAddress(entry.VerLoc[slot], true);
        }
        else
        {
            ptr = SpawnArena.Resolve(entry.Stage[slot]);
        }

        var size = Math.Min(value.DataSize, table.ComponentStorageSize);
        value.Payload[..size].CopyTo(new Span<byte>(ptr + table.ComponentOverhead, size));
        entry.EnabledBits |= (ushort)(1 << slot);
    }

    /// <summary>
    /// Destroy a batch of entities. Single EnsureMutable check, pre-sized pending list.
    /// Cascade delete is applied per entity.
    /// </summary>
    public void DestroyBatch(ReadOnlySpan<EntityId> ids)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        _pendingDestroys ??= new HashSet<EntityId>(ids.Length);

        for (int i = 0; i < ids.Length; i++)
        {
            CheckConfig.Require(CheckConfig.Enabled, !ids[i].IsNull, $"Cannot destroy null entity");
            int cascadeCount = 0;
            DestroyInternal(ids[i], 0, ref cascadeCount);
        }
    }

    /// <summary>Core Spawn implementation shared by Spawn&lt;TArch&gt; and SpawnByArchetypeId.</summary>
    private EntityId SpawnInternal(ArchetypeMetadata meta, ReadOnlySpan<ComponentValue> values)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        var engineState = _dbe._archetypeStates[meta.ArchetypeId];

        // Realms: a realm-keyed archetype's spawn names its realm in the spatial component. Validated HERE, at the call and before anything is staged —
        // an unregistered or incompatible realm throws in application code (D-2), never at commit and never at the fence.
        ushort spawnRealm = 0;
        if (engineState.ClusterState is { SpatialSlot.HasRealmKey: true } keyedState)
        {
            spawnRealm = ValidateSpawnRealm(meta, keyedState, values);
        }

        // Generate unique EntityKey
        long entityKey = Interlocked.Increment(ref engineState.NextEntityKey);
        var entityId = new EntityId(entityKey, _dbe.RoutingIdOf(meta));

        // Pre-build slot-indexed lookup — O(values.Length) once, then O(1) per slot
        Span<int> valueBySlot = stackalloc int[meta.ComponentCount];
        valueBySlot.Fill(-1);
        for (int v = 0; v < values.Length; v++)
        {
            if (meta.TryGetSlot(values[v].ComponentTypeId, out byte targetSlot))
            {
                valueBySlot[targetSlot] = v;
            }
        }

        var entry = new SpawnEntry { Id = entityId, EnabledBits = 0, SpawnRealm = spawnRealm };

        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            var table = engineState.SlotToComponentTable[slot];

            // #839: only a VERSIONED slot gets a content chunk — there the chunk IS the first revision's payload, owned by the chain and reclaimed with it.
            // A SingleVersion or Transient slot's bytes belong in the cluster slot, so it stages in the transaction arena instead; the chunk it used to get
            // became unreachable the moment PlaceSpawns copied the payload out, because no ClusterEntityRecord field can hold its id.
            var isVersioned = table.StorageMode == StorageMode.Versioned;
            int vi = valueBySlot[slot];
            var supplied = vi >= 0;

            // #845: a Versioned slot gets its chunk and chain only when the spawn supplies a value. Unsupplied leaves CompRevFirstChunkId at 0 — the "never
            // written" state the rest of the engine already reads. SingleVersion and Transient always stage, because their bytes live in the cluster slot and
            // there is no absent state to represent there.
            int chunkId = 0;
            int stage = isVersioned ? 0 : SpawnArena.Alloc(table.ComponentOverhead + table.ComponentStorageSize);
            if (isVersioned && supplied)
            {
                chunkId = AllocateVersionedSlotContent(meta, table, slot, entityId, out var compRevChunkId);
                entry.Rev[slot] = compRevChunkId;
            }

            // Copy component value data if provided for this slot
            if (supplied)
            {
                int overhead = table.ComponentOverhead;
                Span<byte> dst;
                if (isVersioned)
                {
                    var compType = meta._slotToComponentType[slot];
                    var info = GetComponentInfo(compType);
                    dst = info.CompContentAccessor.GetChunkAsSpan(chunkId, true);
                }
                else
                {
                    dst = new Span<byte>(SpawnArena.Resolve(stage), overhead + table.ComponentStorageSize);
                }
                int copySize = Math.Min(values[vi].DataSize, dst.Length - overhead);
                // A span, not a pointer: values can be backed by a managed array. The payload starts 12 bytes into a ComponentValue.
                MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.Add(ref Unsafe.As<ComponentValue, byte>(ref Unsafe.AsRef(in values[vi])), 12), copySize)
                    .CopyTo(dst.Slice(overhead));
                entry.EnabledBits |= (ushort)(1 << slot);
            }

            entry.VerLoc[slot] = chunkId;
            entry.Stage[slot] = stage;
        }

        // Store in flat list — index rebuilt lazily on first Contains/IndexOf call
        _spawnedEntities ??= [];
        _spawnedEntityIndexStale = true;
        _spawnedEntities.Add(entry);

        CheckEpochRefresh();
        return entityId;
    }

    /// <summary>
    /// The realm a spawn's values name, validated against the registered realms: throws for an unregistered or incompatible realm. Realm 0 when the
    /// spawn supplies no spatial component (placement refuses that anyway).
    /// </summary>
    private ushort ValidateSpawnRealm(ArchetypeMetadata meta, ArchetypeClusterState clusterState, ReadOnlySpan<ComponentValue> values)
    {
        ref readonly var ss = ref clusterState.SpatialSlot;
        var archetypeName = meta.ArchetypeType?.Name ?? meta.ArchetypeId.ToString();
        for (var v = 0; v < values.Length; v++)
        {
            if (!meta.TryGetSlot(values[v].ComponentTypeId, out var slot) || slot != ss.RealmKeySlot)
            {
                continue;
            }

            // A span over the value, not a pointer: values can be backed by a managed array. The payload starts 12 bytes into a ComponentValue.
            var payload = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref Unsafe.As<ComponentValue, byte>(ref Unsafe.AsRef(in values[v])), 12),
                values[v].DataSize);
            if (payload.Length < ss.RealmKeyOffset + sizeof(ushort))
            {
                throw new InvalidOperationException(
                    $"The realm-key component value supplied to spawn '{archetypeName}' holds {payload.Length} bytes; its realm key is at byte "
                    + $"{ss.RealmKeyOffset}.");
            }

            var realm = MemoryMarshal.Read<ushort>(payload.Slice(ss.RealmKeyOffset));
            _dbe.RealmGridForEntry(realm, meta.ArchetypeId, archetypeName);
            return realm;
        }

        // No value for the key's component: the key reads 0, so realm 0 must be able to hold the entity — checked here, not at commit.
        _dbe.RealmGridForEntry(0, meta.ArchetypeId, archetypeName);
        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Open
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Mutation prep for <c>OpenMut</c> / <c>TryOpenMut</c>: refuses a read-only or finalized transaction, then moves it to InProgress.</summary>
    internal override void PrepareOpenMut()
    {
        EnsureMutable();
        State = TransactionState.InProgress;
    }

    /// <summary>
    /// Check whether an entity is alive (exists and visible at this transaction's TSN). Sees this transaction's own spawns and pending destroys.
    /// </summary>
    public override bool IsAlive(EntityId id)
    {
        AssertThreadAffinity();
        if (id.IsNull)
        {
            return false;
        }

        // Pending destroy wins for both kinds: an own spawn destroyed in this transaction, and a committed entity marked for destruction.
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return false;
        }

        // An own spawn is not in the EntityMap yet.
        if (SpawnedContains(id))
        {
            return true;
        }

        // Committed entity: one cached, hinted EntityMap lookup plus the visibility check at this TSN. No epoch guard — a transaction holds its epoch
        // scope for its whole life (see ResolveEntity).
        return IsCommittedAlive(id, needsGuard: false);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Destroy
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mark an entity for destruction, including cascade delete of children.
    /// The entity and all cascade-delete children become invisible to transactions with TSN >= commit TSN.
    /// Component data and LinearHash entries are freed later by deferred GC.
    /// </summary>
    public void Destroy(EntityId id)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        CheckConfig.Require(CheckConfig.Enabled, !id.IsNull, $"Cannot destroy null entity");

        var scope = TyphonEvent.BeginEcsDestroy(id.RawValue);
        // PROFILING-SPAN-NO-THROW-BEGIN — body MUST NOT throw. DestroyInternal is engine-internal (cascade traversal + tombstone marking).
        // If a future change adds a user-callback path, re-tag to variant B.
        scope.Tsn = TSN;

        int cascadeCount = 0;
        DestroyInternal(id, 0, ref cascadeCount);

        // Only carry CascadeCount when the cascade actually extended beyond the root entity — saves 4 B per record on the common case.
        if (cascadeCount > 1)
        {
            scope.CascadeCount = cascadeCount;
        }
        // PROFILING-SPAN-NO-THROW-END
        scope.Dispose();
    }

    /// <summary>Mark an entity link target for destruction.</summary>
    public void Destroy<T>(EntityLink<T> link) where T : class => Destroy(link.Id);

    /// <summary>
    /// Bulk-load destroy fast path: skips the per-call <see cref="IsAlive"/> check (and the random
    /// <see cref="RawValuePagedHashMap{TKey,TStore}"/> lookup it implies) that <see cref="Destroy(EntityId)"/> uses to make the operation idempotent for ECS
    /// systems.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Contract: <paramref name="id"/> MUST identify an entity that was spawned earlier in the same <see cref="BulkLoadSession"/> (committed by an earlier
    /// transaction recycle, OR pending in the current transaction). Bulk sessions are single-thread, single-user, and the caller tracks the spawned ids, so
    /// this assumption is structurally guaranteed by the API.
    /// </para>
    /// <para>
    /// What we save vs the standard path: at scale (millions of destroys against a fragmented EntityMap that no longer fits in the page cache) the per-call
    /// <c>IsAlive</c> lookup dominates wall-clock time — every call is a random hash-map probe on a cold page. The standard <see cref="Destroy(EntityId)"/>
    /// stays available for any path that genuinely needs the idempotency guarantee.
    /// </para>
    /// </remarks>
    /// <param name="id">Entity to destroy. Must be a live entity in this session.</param>
    internal void DestroyBulk(EntityId id)
    {
        EnsureMutable();
        State = TransactionState.InProgress;
        AssertThreadAffinity();

        CheckConfig.Require(CheckConfig.Enabled, !id.IsNull, $"Cannot destroy null entity");

        var scope = TyphonEvent.BeginEcsDestroy(id.RawValue);
        scope.Tsn = TSN;

        // Skip IsAlive — bulk caller guarantees the entity exists. Skip cascade traversal too —
        // bulk-spawned archetypes don't (and can't, by design) have FK cascade targets.
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            scope.Dispose();
            return;
        }
        _pendingDestroys ??= [];
        _pendingDestroys.Add(id);

        scope.Dispose();
    }

    /// <summary>Maximum cascade depth. DAG validation prevents cycles, but this guards against bugs.</summary>
    private const int MaxCascadeDepth = 32;

    /// <summary>Maximum total entities destroyed in a single cascade operation. Guards against runaway cascades from misconfigured FK relationships.</summary>
    private const int MaxCascadeEntities = 100_000;

    /// <summary>Internal recursive destroy with cascade support.
    /// <paramref name="cascadeCount"/> accumulates per top-level Destroy call (not per transaction),
    /// so destroying many independent entities in one tx doesn't trip the cascade guard.</summary>
    private void DestroyInternal(EntityId id, int depth, ref int cascadeCount)
    {
        if (depth >= MaxCascadeDepth)
        {
            throw new InvalidOperationException(
                $"Cascade delete exceeded max depth {MaxCascadeDepth} at entity {id}. " +
                "This indicates a bug in cascade graph validation — cycles should be caught at registration time.");
        }

        // Check if already pending destroy (avoid double-destroy)
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return;
        }

        // Check if already pending spawn (destroy own spawn)
        bool isPending = SpawnedContains(id);
        if (!isPending && !IsAlive(id))
        {
            // Idempotent destroy: entity was committed-destroyed by a prior transaction (or never existed). Common when a spatial query returns an entity that
            // another user system destroyed earlier in this tick — the spatial index can lag the EntityMap until fence cleanup runs. Silent no-op matches the
            // `_pendingDestroys` early return above; the operation is logically idempotent.
            return;
        }

        _pendingDestroys ??= [];
        _pendingDestroys.Add(id);
        cascadeCount++;

        // Guard against runaway cascades (exponential fan-out from misconfigured FK relationships)
        if (cascadeCount > MaxCascadeEntities)
        {
            throw new InvalidOperationException(
                $"Cascade delete exceeded {MaxCascadeEntities:N0} entities at entity {id}. " +
                "Check FK relationships for unintended cascade chains.");
        }

        // Check for cascade targets
        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta?._cascadeTargets == null || meta._cascadeTargets.Count == 0)
        {
            return;
        }

        // Cascade: find and destroy all children via FK relationships
        foreach (var target in meta._cascadeTargets)
        {
            var childMeta = ArchetypeRegistry.GetMetadata(target.ChildArchetypeId);
            if (childMeta == null)
            {
                continue;
            }
            var childEngineState = _dbe._archetypeStates[childMeta.ArchetypeId];
            if (childEngineState?.EntityMap == null)
            {
                continue;
            }

            _dbe.LogCascadeStep(target.ChildArchetypeType.Name, target.FkSlotIndex, id);

            var childIds = FindCascadeChildren(childMeta, target, id);
            foreach (var childId in childIds)
            {
                DestroyInternal(childId, depth + 1, ref cascadeCount);
            }
        }

        if (depth == 0 && cascadeCount > 1)
        {
            _dbe.LogCascadeSummary(id, cascadeCount);
        }
    }

    /// <summary>
    /// Find all entities of the child archetype that reference the given parent via FK.
    /// Scans spawned entities (via EntityMap) and committed entities (via FK index).
    /// </summary>
    private List<EntityId> FindCascadeChildren(ArchetypeMetadata childMeta, CascadeTarget target, EntityId parentId)
    {
        var result = new List<EntityId>();

        // 1. Scan spawned entities for FK matches (read component data from SpawnEntry locations directly)
        if (_spawnedEntities != null)
        {
            for (int i = 0; i < _spawnedEntities.Count; i++)
            {
                var entry = _spawnedEntities[i];
                // entry.Id.ArchetypeId is a routing id; target.ChildArchetypeId is a catalog id — compare in routing space.
                if (entry.Id.ArchetypeId != _dbe.RoutingIdOf(childMeta))
                {
                    continue;
                }

                // Resolve the slot's table first: since #839 the payload's home depends on its storage mode — a Versioned slot has a real content chunk (the
                // first revision's), everything else stages in the transaction arena.
                var spawnMeta = _dbe.GetMetaByRouting(entry.Id.ArchetypeId);
                var spawnES = _dbe._archetypeStates[spawnMeta.ArchetypeId];
                var table = spawnES.SlotToComponentTable[target.FkSlotIndex];
                var compType = spawnMeta._slotToComponentType[target.FkSlotIndex];
                var info = GetComponentInfo(compType);

                byte* ptr;
                if (table.StorageMode == StorageMode.Versioned)
                {
                    int chunkId = entry.VerLoc[target.FkSlotIndex];
                    if (chunkId == 0)
                    {
                        continue;
                    }

                    // A same-tx copy-on-write supersedes the spawn's own chunk, so prefer the cache's current content chunk.
                    int dataChunkId = info.SingleCache.TryGetValue((long)entry.Id.RawValue, out var cri) ? cri.CurCompContentChunkId : chunkId;
                    ptr = info.CompContentAccessor.GetChunkAddress(dataChunkId);
                }
                else
                {
                    int stage = entry.Stage[target.FkSlotIndex];
                    if (stage == 0)
                    {
                        continue;
                    }
                    ptr = SpawnArena.Resolve(stage);
                }

                var fkEntityId = *(EntityId*)(ptr + table.ComponentOverhead + target.FkFieldOffset);
                if (fkEntityId == parentId)
                {
                    result.Add(entry.Id);
                }
            }
        }

        // 2. Find committed children via FK index lookup (O(log n + k) instead of O(n) EntityMap scan).
        // Routed through FkReverseLookup so BOTH index homes are read (#664). This site used to resolve the per-ComponentTable tree and then deref
        // `table.CompRevTableSegment` unconditionally, which meant a cluster-backed child archetype scanned an empty tree — the cascade silently destroyed
        // nothing and orphaned its children — and a SingleVersion child component (only reachable in a non-cluster archetype, i.e. alongside a Transient
        // indexed field) hit a null CompRev segment. The helper picks the right tree per archetype and the right PK decode per home.
        var childEngineState = _dbe._archetypeStates[target.ChildArchetypeId];
        if (childEngineState?.SlotToComponentTable != null)
        {
            var table = childEngineState.SlotToComponentTable[target.FkSlotIndex];
            var fkFieldOrdinal = PipelineExecutor.FindFKIndexOrdinal(table, target.FkFieldOffset);
            var candidates = FkReverseLookup.ResolveCandidatesForArchetype(_dbe, childMeta, target.FkSlotIndex);

            using var guard = EpochGuard.Enter(_epochManager);
            var collector = new CascadeChildCollector
            {
                Result = result,
                RoutingId = _dbe.RoutingIdOf(childMeta),
            };
            FkReverseLookup.ForEachSource(_dbe, table, in candidates, fkFieldOrdinal, (long)parentId.RawValue, ref collector);
        }

        return result;
    }

    /// <summary>Collects the cascade children found by the FK reverse lookup, keeping only those of the target child archetype.</summary>
    /// <remarks>
    /// The routing filter is structurally redundant in the cluster phase — that PK is read from the scanned archetype's own entity-id array — but the
    /// ComponentTable phase scans one tree shared by every archetype holding the component, so it stays. Cheap, and dropping it would be a silent
    /// over-delete on exactly the shape that is hardest to notice.
    /// </remarks>
    private struct CascadeChildCollector : IFkSourceAction
    {
        public List<EntityId> Result;
        public ushort RoutingId;

        public bool Process(long sourcePK, ArchetypeMetadata meta)
        {
            // childId.ArchetypeId is a routing id; target.ChildArchetypeId is a catalog id — compare in routing space.
            var childId = EntityId.FromRaw(sourcePK);
            if (childId.ArchetypeId == RoutingId)
            {
                Result.Add(childId);
            }

            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Enable/Disable staging (called from EntityRef)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Stage an EnabledBits change for commit. Called from EntityRefMut.Enable/Disable.</summary>
    internal override void StageEnableDisable(EntityId id, ushort newEnabledBits)
    {
        _pendingEnableDisable ??= new Dictionary<EntityId, ushort>();
        _pendingEnableDisable[id] = newEnabledBits;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pending spawn query support (read-your-own-writes)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Pending spawns — exposed for EcsQuery read-your-own-writes support.</summary>
    internal List<SpawnEntry> PendingSpawns => _spawnedEntities;

    /// <summary>
    /// Whether this transaction supplied the component of type <paramref name="componentTypeId"/> to <paramref name="pk"/>, which had none, and has not
    /// committed: a component its own field queries take for absent (QENABLE-01).
    /// </summary>
    internal bool FirstSuppliedHere(long pk, int componentTypeId)
    {
        if (_firstSupplies is not { Count: > 0 } supplies)
        {
            return false;
        }

        foreach (var (supplyPk, typeId) in supplies)
        {
            if (supplyPk == pk && typeId == componentTypeId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Pending destroys — exposed for EcsQuery read-your-own-writes support.</summary>
    internal HashSet<EntityId> PendingDestroys => _pendingDestroys;

    /// <summary>Pending EnabledBits overrides — exposed for EcsQuery read-your-own-writes support.</summary>
    internal Dictionary<EntityId, ushort> PendingEnableDisable => _pendingEnableDisable;

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers — entity resolution
    // ═══════════════════════════════════════════════════════════════════════

    private protected override EntityRef ResolveEntity(EntityId id, bool writable, bool throwOnMiss)
    {
        AssertThreadAffinity();
        NoteEntityOpen();   // #1189: every 128 opens the transaction moves its epoch forward, read-only included

        // #1230: an open keeps nothing it read until it returns, so a back-pressure round inside it unwinds to here instead of waiting with the
        // transaction's pin. Here the epoch may move, as NoteEntityOpen moves it; the open then waits for a page and runs again, all of its restarts within
        // one back-pressure timeout of its first round (PagedMMF.RestartableOpenDepth).
        PagedMMF.RestartableOpenDepth++;
        try
        {
            while (true)
            {
                try
                {
                    return ResolveEntityCore(id, writable, throwOnMiss);
                }
                catch (BackpressureOpenRestart restart)
                {
                    _entityOperationCount = 0;
                    EnumerateRefreshEpoch();
                    AwaitAfterRestart(restart);
                }
            }
        }
        finally
        {
            if (--PagedMMF.RestartableOpenDepth == 0)
            {
                PagedMMF.ResetRestartBudgets();
            }
        }
    }

    /// <summary>
    /// After an operation unwound (<see cref="BackpressureOpenRestart"/>) and moved its epoch: a page wait waits for a page; a chain-lock wait retries at
    /// once, its brief wait on the lock being the pacing (#1382).
    /// </summary>
    private void AwaitAfterRestart(BackpressureOpenRestart restart)
    {
        if (restart.LockWait)
        {
            Thread.Yield();
        }
        else
        {
            _dbe.MMF.AwaitPageAfterOpenRestart();
        }
    }

    private EntityRef ResolveEntityCore(EntityId id, bool writable, bool throwOnMiss)
    {
        if (id.IsNull)
        {
            return Miss(id, throwOnMiss);
        }

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta == null)
        {
            return Miss(id, throwOnMiss);
        }

        // Check if this entity was spawned in this transaction (not yet in EntityMap)
        int spawnIdx = SpawnedIndexOf(id);
        bool isOwnSpawn = spawnIdx >= 0;

        // Early destroy check for own spawns
        if (isOwnSpawn && _pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return Miss(id, throwOnMiss);
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return Miss(id, throwOnMiss);
        }

        if (isOwnSpawn)
        {
            // Own spawn: build EntityRef directly from SpawnEntry (entity not in EntityMap yet)
            var entry = _spawnedEntities[spawnIdx];

            ushort enabledBits = entry.EnabledBits;
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var pendingBits))
            {
                enabledBits = pendingBits;
            }

            var result = new EntityRef(id, meta, es, this, enabledBits);
            result._isOwnSpawn = true;   // #713: no HEAD yet — a Commit-discipline write goes in place into the staging payload, not through the staging buffer
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                // #839: the location's MEANING depends on the slot's storage mode while the entity is unpublished — a spawn-arena handle for SingleVersion and
                // Transient, a real content chunk id for Versioned (that chunk is the first revision's payload). The read and write paths disambiguate on
                // EntityRef._isOwnSpawn, which is set just above; see EntityAccessor.ResolveSpawnAwarePayload.
                result.SetLocation(slot, SpawnSlotLocation(in entry, es.SlotToComponentTable[slot], slot));
            }

            // For Versioned: override from SingleCache (same as before — Spawn already populated it)
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = es.SlotToComponentTable[slot];
                if (table.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }

                var compType = meta._slotToComponentType[slot];
                var info = GetComponentInfo(compType);
                long pk = (long)id.RawValue;

                if (info.SingleCache.TryGetValue(pk, out var cached))
                {
                    result.SetLocation(slot, cached.CurCompContentChunkId);
                }
            }

            return result;
        }

        // Committed entity: read from EntityMap
        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        // Transaction already holds an epoch scope (entered during Init) — no per-call EpochGuard needed.
        // Reuse cached EntityMap accessor (same pattern as IsEntityVisible)
        if (!_hasEntityMapCache || _entityMapCacheArchId != id.ArchetypeId)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
            }
            _entityMapCacheAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _entityMapCacheArchId = id.ArchetypeId;
            _hasEntityMapCache = true;
        }
        bool found = es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref _entityMapCacheAccessor);

        if (!found)
        {
            return Miss(id, throwOnMiss);
        }

        // Check pending destroy for committed entities
        if (_pendingDestroys != null && _pendingDestroys.Contains(id))
        {
            return Miss(id, throwOnMiss);
        }

        // Visibility check — a pending born through its owner's card (VIS-08)
        if (!EntityVisibility.IsVisible(readBuf, TSN, CommitCards, es.EntityMap, id.EntityKey, ref _entityMapCacheAccessor, _epochManager))
        {
            return Miss(id, throwOnMiss);
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);

        // Resolve EnabledBits: committed entities check MVCC overrides
        {
            ushort enabledBits = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, TSN, CommitCards);

            // Check for pending enable/disable override
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(id, out var pendingBits))
            {
                enabledBits = pendingBits;
            }

            var result = new EntityRef(id, meta, es, this, enabledBits);

            if (meta.IsClusterEligible && es.ClusterState != null)
            {
                // Cluster path: read ClusterEntityRecord → resolve cluster base + slot
                int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
                byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);

                ResolveClusterBases(es, id.ArchetypeId, clusterChunkId, writable, ref result);
                result._clusterSlotIndex = slotIndex;
                result._clusterChunkId = clusterChunkId;
                result._clusterLayout = es.ClusterState.Layout;

                // For Versioned slots, walk chain and store resolved content chunkId in _locations.
                // Versioned reads via EntityRef.Read use _locations (not cluster slot) for MVCC correctness.
                // Bulk iteration (GetClusterEnumerator) reads HEAD directly from cluster SoA.
                if (meta.VersionedSlotMask != 0)
                {
                    var layout = es.ClusterState.Layout;
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int vi = layout.SlotToVersionedIndex[slot];
                        if (vi < 0)
                        {
                            continue;
                        }

                        var compTypeId = meta._componentTypeIds[slot];
                        int compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(readBuf, vi);
                        if (compRevFirstChunkId == 0)
                        {
                            // Root 0 usually means the component is absent (#845) — but it ALSO means "created by this transaction and not yet published",
                            // because the root only reaches the record at commit. Distinguishing them needs the component's SingleCache, and asking for the
                            // ComponentInfo through the creating overload would allocate one for every genuinely-absent slot of every entity opened. The
                            // non-creating lookup answers it for an array index: null ⇒ this transaction never touched the component ⇒ absent for certain.
                            var pending = TryGetExistingComponentInfo(compTypeId);
                            if (pending == null || !pending.SingleCache.TryGetValue((long)id.RawValue, out var created))
                            {
                                continue;
                            }

                            result.SetLocation(slot, created.CurCompContentChunkId);
                            result.SetChainRoot(slot, created.CompRevTableFirstChunkId);
                            continue;
                        }


                        var info = GetComponentInfoByTypeId(compTypeId, meta._slotToComponentType[slot]);
                        long pk = (long)id.RawValue;

                        // Record the chain root so a later first-write can re-resolve directly (deferred SingleCache insert).
                        result.SetChainRoot(slot, compRevFirstChunkId);

                        // Check cache first (prior Write or Spawn in this transaction — read-only resolves no longer populate the cache)
                        if (info.SingleCache.TryGetValue(pk, out var cached))
                        {
                            result.SetLocation(slot, cached.CurCompContentChunkId);
                            continue;
                        }


                        // Walk revision chain (the single-entry fast path reads no clock; a locked walk composes its own wait budget, CX-06)
                        var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, this);
                        if (chainResult.IsFailure)
                        {
                            continue;
                        }



                        // Deferred-insert: do NOT cache the read (commit/rollback/WAL would iterate dead Read entries). First write re-resolves
                        // via the chain root above and inserts then (EcsVersionedCopyOnWrite).
                        result.SetLocation(slot, chainResult.Value.CurCompContentChunkId);

                    }
                }
            }
            else
            {
                result.CopyLocationsFrom(readBuf, meta.ComponentCount);

                // For Versioned components: resolve MVCC-visible chunkId via SingleCache or revision chain walk.
                // Location[slot] from EntityMap is compRevFirstChunkId.
                // For committed entities, walk the revision chain to find the visible version.
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = es.SlotToComponentTable[slot];
                    if (table.StorageMode != StorageMode.Versioned)
                    {
                        continue;
                    }

                    var compTypeId = meta._componentTypeIds[slot];
                    var info = GetComponentInfoByTypeId(compTypeId, meta._slotToComponentType[slot]);
                    long pk = (long)id.RawValue;

                    // Location[slot] from EntityMap is the chain root — record it before it is overwritten with the resolved content chunk,
                    // so a later first-write can re-resolve directly (deferred SingleCache insert).
                    int compRevFirstChunkId = result.GetLocation(slot);
                    result.SetChainRoot(slot, compRevFirstChunkId);

                    // If already written or spawned in this transaction, reuse the cached entry (read-only resolves no longer populate the cache)
                    if (info.SingleCache.TryGetValue(pk, out var cached))
                    {
                        result.SetLocation(slot, cached.CurCompContentChunkId);
                        continue;
                    }

                    if (compRevFirstChunkId == 0)
                    {
                        continue;
                    }

                    var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, this);
                    if (chainResult.IsFailure)
                    {
                        continue;
                    }

                    // Deferred-insert: do NOT cache the read — first write re-resolves via the chain root and inserts then (EcsVersionedCopyOnWrite).
                    result.SetLocation(slot, chainResult.Value.CurCompContentChunkId);
                }
            }

            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Internal helpers — component data access (delegated from EntityRef)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copy-on-write for Versioned components: allocates new chunk, copies data, creates revision entry.
    /// Called by EntityRefMut.Write for Versioned components. Returns (newChunkId, newChunkAddress).
    /// First write per entity allocates; subsequent writes reuse the same new chunk.
    /// </summary>
    internal override (int chunkId, nint ptr) EcsVersionedCopyOnWrite(Type compType, EntityId entityId, ComponentTable table, int chainRootChunkId = 0)
    {
        // #1382: until it claims its revision a first write leaves no trace on a throw (CHAIN-02), and its caller holds nothing across this call but
        // the handle, which re-resolves (EP-03). So a contended chain, or back-pressure, before the claim unwinds it: the epoch moves here, as at an
        // open's start, and the write runs again. Waiting with its pin, a writer queued behind a publish that held the chain while it waited on
        // back-pressure kept every page anyone touched, and the publish's page never came. Past the claim it waits as before.
        PagedMMF.RestartableOpenDepth++;
        var restartScope = true;
        try
        {
            while (true)
            {
                try
                {
                    return EcsVersionedCopyOnWriteCore(compType, entityId, table, chainRootChunkId, ref restartScope);
                }
                catch (BackpressureOpenRestart restart) when (restartScope)
                {
                    _entityOperationCount = 0;
                    EnumerateRefreshEpoch();
                    AwaitAfterRestart(restart);
                }
            }
        }
        finally
        {
            if (restartScope && --PagedMMF.RestartableOpenDepth == 0)
            {
                PagedMMF.ResetRestartBudgets();
            }
        }
    }

    /// <summary>Leaves a first write's restart scope once it has claimed its revision: past that point it waits as before.</summary>
    private static void EndRestartScope(ref bool restartScope)
    {
        if (!restartScope)
        {
            return;
        }

        restartScope = false;
        if (--PagedMMF.RestartableOpenDepth == 0)
        {
            PagedMMF.ResetRestartBudgets();
        }
    }

    private (int chunkId, nint ptr) EcsVersionedCopyOnWriteCore(Type compType, EntityId entityId, ComponentTable table, int chainRootChunkId,
        ref bool restartScope)
    {
        var info = GetComponentInfo(compType);
        long pk = (long)entityId.RawValue;

        // First write per entity inserts into SingleCache here. Read-only resolves no longer populate the cache (deferred-insert — the cache holds only
        // written/spawned entries, so commit/rollback/WAL iterate nothing for pure reads); the EntityRef carries the chain root captured at resolve time
        // so the CompRevInfo is re-resolved with a direct chain walk (single-entry fast path in the steady state).
        ref var cri = ref CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var cached);

        // CHAIN-02: an entry this call inserted is removed on any throw until it claims a revision — the resolve below can time out on the chain's lock,
        // the append can fail. Left behind, it was a default entry naming chain 0, which a retried write appended to.
        var claimed = false;
        try
        {
            if (!cached)
            {
                if (chainRootChunkId != 0)
                {
                    // Re-resolve from the chain root recorded by ResolveEntity. ReadCommitSequence is the chain's CommitSequence at this walk: a commit
                    // published after it trips a handler-less commit's conflict test; one published between the snapshot and this walk does not (it is
                    // best effort, plan 06 §3.10). A handler commit tests by stamp, under the lock.
                    var walk = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, chainRootChunkId, TSN, this);
                    if (!walk.IsFailure)
                    {
                        cri = walk.Value;
                        cri.Operations = ComponentInfo.OperationType.Read;
                    }
                }

                if (cri.CompRevTableFirstChunkId == 0)
                {
                    // Fallback: Write without prior Open (edge case), or the chain-root walk failed
                    var result = GetCompRevInfoFromIndex(pk, info, TSN);
                    if (result.IsFailure)
                    {
                        throw new InvalidOperationException($"Entity {entityId} not found in PK index for {compType.Name}");   // the catch removes the entry
                    }
                    cri = result.Value;
                }
            }

            // Only allocate new revision on FIRST write. Created (from Spawn) already has a chunk.
            bool alreadyWritten = (cri.Operations & (ComponentInfo.OperationType.Updated | ComponentInfo.OperationType.Created)) != 0;

            if (!alreadyWritten)
            {
                int oldChunkId = cri.CurCompContentChunkId;

                // AddCompRev: allocates NEW chunk, adds revision entry with IsolationFlag=true. The entry claims a write only once that revision exists
                // (CHAIN-02, #1252): marked Updated before, a throw here — the chain-lock timeout, an allocation failure — left it naming the committed
                // revision as this transaction's own, and a retried write in the same transaction was handed the committed chunk to write into, in place.
                if (_dbe.FirstWriteClaimsBeforeAppendForTest)
                {
                    cri.Operations |= ComponentInfo.OperationType.Updated;
                    claimed = true;
                    EndRestartScope(ref restartScope);   // claimed: no longer restartable (#1382)
                }

                ComponentRevisionManager.AddCompRev(info, ref cri, TSN, UowId, false);
                cri.Operations |= ComponentInfo.OperationType.Updated;
                claimed = true;
                EndRestartScope(ref restartScope);   // claimed: no longer restartable (#1382)

                // Copy old data to new chunk
                byte* oldPtr = info.CompContentAccessor.GetChunkAddress(oldChunkId);
                byte* newPtr = info.CompContentAccessor.GetChunkAddress(cri.CurCompContentChunkId, true);
                Unsafe.CopyBlock(newPtr, oldPtr, (uint)table.ComponentTotalSize);

                // If the component has collections, increment RefCounters for shared collection buffers.
                // The byte copy above duplicated the _bufferId fields — both old and new revisions now
                // reference the same collection storage, so RefCounter must reflect that.
                if (table.HasCollections)
                {
                    foreach (var f in table.CollectionFields)
                    {
                        int bufferId = *(int*)(newPtr + table.ComponentOverhead + f.OffsetInComponentStorage);
                        if (bufferId != 0)
                        {
                            var accessor = f.Vsbs.Segment.CreateChunkAccessor(_changeSet);
                            f.Vsbs.BufferAddRef(bufferId, ref accessor);
                            accessor.Dispose();
                        }
                    }
                }
            }
        }
        catch
        {
            if (!cached && !claimed)
            {
                info.SingleCache.Remove(pk);
            }

            throw;
        }

        byte* ptr = info.CompContentAccessor.GetChunkAddress(cri.CurCompContentChunkId, true);
        return (cri.CurCompContentChunkId, (nint)ptr);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Commit hooks — flush pending ECS operations
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The tag of this commit's view deltas: its commit stamp (VIS-11). Only the AP-07 rule mutant (<c>PrepareReconcilesIndexesForTest</c>) appends a delta
    /// in PREPARE, before the stamp exists; it tags with the TSN, as the code it reproduces did.
    /// </summary>
    private long ViewDeltaTag => _commitStamp != 0 ? _commitStamp : TSN;

    /// <summary>
    /// The stamp this commit's effects are published with (plan 06 §3.3): the commit stamp — born, died, enabled-bit changes, the cluster watermarks and the
    /// view deltas, as the revisions. The effects themselves exist before it, flagged with the owner (VIS-08, VIS-09, VIS-10); the publish replaces the
    /// owner with this stamp.
    /// </summary>
    private long PublishStamp
    {
        get
        {
            System.Diagnostics.Debug.Assert(_commitStamp != 0, "an effect published before the commit stamp was drawn");
            return _commitStamp;
        }
    }

    /// <summary>Flush all pending ECS operations into persistent storage. Called during Commit.</summary>
    internal void FlushEcsPendingOperations()
    {
        // Enable/Disable for committed entities: directly upserts to EntityMap.
        // For spawned entities: skip here, PlaceSpawns applies the override.
        FlushPendingEnableDisable();
        // Spawns placed in PREPARE (PlaceSpawns): stamp their records, set their occupancy bits, maintain their indexes (VIS-08).
        PublishSpawns();
        _dbe.DestroysBeforeIndexRemovalProbe?.Invoke();
        FlushPendingDestroys();
        PublishMembershipDeltas();
    }

    /// <summary>
    /// Publishes this commit's structural changes to every view subscribed to the affected archetypes' membership channels, then moves those
    /// archetypes' structural epochs (#790).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a separate pass rather than a line inside each loop.</b> A membership entry must not become drainable before the entity it names
    /// is queryable — a concurrent transaction with a higher TSN can drain this buffer while <c>PublishSpawns</c> is still running, and would
    /// otherwise hand its caller an id that <c>EntityMap</c> does not yet resolve. Running after BOTH loops makes that ordering true by
    /// construction instead of by inspection of two thousand lines of spawn code.
    /// </para>
    /// <para>
    /// <b>MEMB-01 — epochs move last.</b> Every <c>TryAppend</c> completes before any <c>Bump</c>. <c>Interlocked.Increment</c> is a full fence
    /// on x64 and arm64 so the appends cannot sink past it, and the refresh gate's acquire load pairs with it. Reversed, a view reads the new
    /// epoch, drains an empty buffer, records it as consumed, and never sees those entities — silent and permanent. This also runs before
    /// <c>WaitAndFinalize</c> publishes the commit's TSN, so any reader whose snapshot can see this commit can also see the counter move.
    /// </para>
    /// <para>
    /// <b>An archetype nobody subscribes to costs one array index and a branch per entity, and nothing else.</b> No append, no epoch bump, no
    /// list. Bumping it anyway would be pure waste: nothing can observe the epoch of an archetype with no subscriber, and it does not close the
    /// registration window below either — a commit that read the empty subscriber array before a view registered is missed by that view whether
    /// or not the counter moved, because the entities are not in its buffer and its population scan is older than the commit.
    /// </para>
    /// <para>
    /// <b>That registration window is inherited, not introduced.</b> A commit publishing between the view transaction's snapshot and its
    /// <c>Register</c> call reaches neither the buffer nor the scan. <c>ToIncrementalView</c> has the identical window on the field channel and
    /// always has. Recorded rather than fixed here so it is not mistaken for new.
    /// </para>
    /// </remarks>
    private void PublishMembershipDeltas()
    {
        // Cleared at ENTRY, not at exit. A throw between an Add and the Bump loop below leaves this commit's entries in the subscriber buffers
        // with no epoch bump — the silent MEMB-01 miss — and, because Transaction instances are pooled, would carry the stale registries into
        // the next logical transaction and bump archetypes it never touched. Clearing here bounds the damage to the commit that threw.
        _membershipTouched?.Clear();
        _membershipViewSnapshots?.Clear();

        var haveSpawns = _spawnedEntities is { Count: > 0 };
        var haveDestroys = _pendingDestroys is { Count: > 0 };
        if (!haveSpawns && !haveDestroys)
        {
            return;
        }

        // MEMB-04's last enforce clause, made checkable. Every deferred buffer free is decided against the oldest epoch any thread is pinned to, so a
        // publish pass that RAISED its own pin midway would announce a floor above a stamp it is still exposed to — and the reclaimer would free a
        // block this pass can still write through. True at every publish site today only because nobody calls RefreshScope from one; an enforce
        // clause with nothing enforcing it is what the MEMB-01 episode was about.
        var pinAtEntry = _epochManager?.CurrentThreadPinnedEpoch ?? 0;

        // Resolve the subscriber list ONCE per archetype for the whole commit, before publishing anything. Re-reading it per entity would let a
        // view that registers midway through this loop receive a TORN HALF of one commit: the entities before it registered are neither in the
        // buffer nor in its population scan, yet the single Bump at the end opens its gate as though it had received all of them. The
        // registration window this does not close — a view that registers before ANY of this commit is published — is the all-or-nothing one
        // inherited from ToIncrementalView, and the view's own first-refresh resync repairs it.
        // The try MUST open before the publish loops, not after them. The shared latches are taken inside PublishMembershipEntry, i.e. DURING these
        // loops; with the try opening after them, anything that threw in between — an allocation failure, a bad routing id — stranded every latch
        // taken so far. The only reference to release them through is _membershipTouched, which the next commit clears at entry, so the latch would
        // be held for the life of the process: every later EcsView.Dispose on that archetype then blocks for a full DefaultCommitTimeout before
        // giving up and proceeding UNLATCHED, which reopens the use-after-free the latch exists to close.
        try
        {
            if (haveSpawns)
            {
                foreach (var entry in _spawnedEntities)
                {
                    // Spawned and destroyed in the same transaction: PlaceSpawns skipped the EntityMap insert, so there is nothing to announce.
                    // The matching destroy below is still announced and lands on a view that never held the id, where TryRemove reports no change.
                    if (_pendingDestroys != null && _pendingDestroys.Contains(entry.Id))
                    {
                        continue;
                    }
                    PublishMembershipEntry(entry.Id, true);
                }
            }

            if (haveDestroys)
            {
                foreach (var entityId in _pendingDestroys)
                {
                    PublishMembershipEntry(entityId, false);
                }
            }

            if (_membershipTouched == null || _membershipTouched.Count == 0)
            {
                return;
            }

            // MEMB-01's observation point: every entry for this commit is appended, no epoch has moved. One null check per commit in production.
            QueryPathProbe.MembershipPrePublishBumpHook?.Invoke();

            for (var i = 0; i < _membershipTouched.Count; i++)
            {
                _membershipTouched[i].Bump();
            }
        }
        finally
        {
            System.Diagnostics.Debug.Assert(pinAtEntry == 0 || (_epochManager?.CurrentThreadPinnedEpoch ?? 0) == pinAtEntry,
                "MEMB-04: the publishing thread's pinned epoch moved during the publish pass. Every retired view buffer is freed once no thread is "
                + "pinned below its stamp, so a pass that raises its own floor mid-flight can have a buffer it is still writing through reclaimed "
                + "underneath it. Whatever called RefreshScope from inside this pass must not.");

            // Still a finally, and the try still opens BEFORE the publish loops — not for a latch any more, but because these per-commit lists must
            // not survive a throw into the next transaction (instances are pooled), and MEMB-01's all-or-nothing snapshot depends on them.
            _membershipTouched?.Clear();
            _membershipViewSnapshots?.Clear();
        }
    }

    /// <summary>Appends one membership entry to every subscribed view of <paramref name="entityId"/>'s archetype, and records the archetype as needing an epoch bump.</summary>
    private void PublishMembershipEntry(EntityId entityId, bool isCreation)
    {
        // Unguarded indexing would be a throw on the commit path for a routing id with no state; a membership notification is never worth that.
        var routing = entityId.ArchetypeId;
        var states = _dbe._stateByRouting;
        var engineState = routing < (uint)states.Length ? states[routing] : null;
        var registry = engineState?.MembershipViews;

        // The whole cost of this feature for a database that does not use it: one array index and one branch per structurally-changed entity.
        // Checked before anything is allocated or recorded, because the overwhelmingly common case is that nobody is listening.
        if (registry == null || registry.IsEmpty)
        {
            return;
        }

        // Linear scan, not a set: a transaction touches one archetype in the overwhelming majority of cases and a handful at worst, so the
        // scan beats a hash lookup and allocates nothing beyond the list itself. The snapshot taken on first touch is what makes this commit
        // all-or-nothing for each view (see the remark in PublishMembershipDeltas).
        _membershipTouched ??= [];
        _membershipViewSnapshots ??= [];
        var slot = -1;
        for (var i = 0; i < _membershipTouched.Count; i++)
        {
            if (ReferenceEquals(_membershipTouched[i], registry))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            // Shared latch for the whole publish pass, taken once per archetype per commit and released in PublishMembershipDeltas. It excludes
            // view DISPOSAL, which would otherwise free the pinned ring buffer under the appends below.
            //
            // A timeout is not a commit failure, and it must skip the ARCHETYPE for the rest of this commit — not just this entity. Returning
            // without recording the refusal left the next entity of the same archetype to retry: entity 1 refused, entity 2 admitted, and the
            // archetype then reaching the bump loop. Every subscriber would be told a change happened while holding only half of it, drain what
            // was there, record the epoch as consumed, and never see entity 1 again. Silent and permanent — MEMB-01's on_violation, arrived at
            // through the recovery path of a different rule.
            slot = _membershipTouched.Count;
            _membershipTouched.Add(registry);
            _membershipViewSnapshots.Add(registry.ViewsSnapshot());
        }

        var views = _membershipViewSnapshots[slot];

        // BeforeKey carries the entity's cluster location for stage 2's per-cluster match bits; stage 1 leaves it zero and never reads it.
        // Populating it means threading the location out of PlaceSpawns' cluster branch, which is only worth doing when stage 2 needs it.
        byte flags = isCreation ? (byte)0x40 : (byte)0x80;

        // Hoisted: a [ThreadStatic] read is a TLS-base helper call, not a plain load, and this loop runs per view per entity on a path whose whole
        // design argument is that ~15.6 ns per append cannot afford a synchronised acquire. One read per entity instead of one per view.
        var hook = QueryPathProbe.PrePublishAppendHook;
        for (var v = 0; v < views.Length; v++)
        {
            var reg = views[v];
            if (reg.DeltaBuffer.IsClosed)
            {
                continue;
            }
            hook?.Invoke();
            reg.DeltaBuffer.TryAppend(entityId, default, default, PublishStamp, flags);
        }
    }

    /// <summary>
    /// Inserts a freshly-spawned entity into every B+Tree of one index home, records AllowMultiple element ids, widens zone maps and notifies views.
    /// </summary>
    /// <remarks>
    /// Generic over the store so both homes share one body (#655). <paramref name="dataBase"/> is where this home's component bytes live;
    /// <paramref name="primaryBase"/> is where the AllowMultiple elementId tail lives — the cluster chunk, which for a Transient slot in a mixed archetype is
    /// a different segment from its data.
    /// </remarks>
    private void InsertClusterIndexEntries<TStore>(ref SpawnContext ctx, ClusterIndexSlot<TStore>[] ixSlots, byte* dataBase, byte* primaryBase,
        ArchetypeClusterInfo layout, int clusterChunkId, int slotIdx, int clusterLocation, EntityId entityId, ref ChunkAccessor<TStore> idxAccessor,
        ref ChunkAccessor<TStore> idxAccessorS64, ChunkBasedSegment<TStore> s64Segment, ushort enabledBits) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        // One shared write per commit instead of one per indexed field (review M4): the counter is read by the StatisticsWorker thread, so each
        // increment is a store other cores may be watching.
        var mutations = 0;

        for (int ixs = 0; ixs < ixSlots.Length; ixs++)
        {
            ref var ixSlot = ref ixSlots[ixs];
            int compSize = layout.ComponentSize(ixSlot.Slot);
            byte* compBase = dataBase + layout.ComponentOffset(ixSlot.Slot) + slotIdx * compSize;
            for (int fi = 0; fi < ixSlot.Fields.Length; fi++)
            {
                ref var field = ref ixSlot.Fields[fi];
                byte* fieldPtr = compBase + field.FieldOffset;
                // Pick the accessor matching this field's segment — passing one built on the other segment resolves node chunks
                // at the wrong stride and corrupts neighbouring nodes (#658).
                mutations++;   // (#665)
                int elementId = s64Segment != null && ReferenceEquals(field.Index.Segment, s64Segment)
                    ? field.Index.Add(fieldPtr, clusterLocation, ref idxAccessorS64)
                    : field.Index.Add(fieldPtr, clusterLocation, ref idxAccessor);
                // For AllowMultiple fields, record elementId in the cluster tail so destroy/migration can call RemoveValue(key,
                // elementId, value) — removes only this entity's entry, not the entire buffer at the key (which would wipe all siblings on
                // a non-unique index).
                // Issue #229 Phase 3.
                if (field.AllowMultiple)
                {
                    *(int*)(primaryBase + layout.IndexElementIdOffset(field.MultiFieldIndex, slotIdx)) = elementId;
                }

                // Notify views of creation (isCreation flag so incremental views detect the new entity) — of an enabled component only: a view's drain
                // applies a creation from its key, and a component the spawn left disabled or never supplied matches no field predicate (QENABLE-01). Its
                // enable announces it later (NotifyViewsForEnableDisable).
                if ((enabledBits & (1 << ixSlot.Slot)) == 0)
                {
                    continue;
                }

                var spawnTable = ctx.EngineState.SlotToComponentTable[ixSlot.Slot];
                var views = spawnTable.ViewRegistry.GetViewsForField(fi);
                // Hoisted out of the per-view loop: a [ThreadStatic] read is a TLS-base helper call, not a plain load, and this loop is the spawn
                // hot path — leaving it inside measured a 46% regression on the 15.6 ns/append figure this design's whole argument rests on.
                var spawnHook = QueryPathProbe.PrePublishAppendHook;
                for (int v = 0; v < views.Length; v++)
                {
                    var reg = views[v];
                    if (reg.DeltaBuffer.IsClosed)
                    {
                        continue;
                    }

                    // Width guard, mirroring the twin at the update site below. KeyBytes8 is 8 bytes and FromPointer is a raw CopyBlockUnaligned of
                    // FieldSize — 64 for an indexed String64 field — so building one from a wider field smashes the stack over whatever locals follow it.
                    // That is the same defect fixed on the tick-fence migration path (#629 review, C4); this is its insert-side sibling. A view delta cannot
                    // carry a key it has no room for, so the honest behaviour is to skip the delta rather than truncate the key to its first eight bytes and
                    // emit a notification that names the wrong value.
                    if (field.FieldSize > sizeof(long))
                    {
                        continue;
                    }

                    var newKey = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);
                    byte flags = (byte)((fi & 0x3F) | 0x40); // isCreation
                    spawnHook?.Invoke();
                    reg.DeltaBuffer.TryAppend(entityId, default, newKey, PublishStamp, flags, reg.ComponentTag);
                }
            }
        }

        ctx.ClusterState.MutationsSinceRebuild += mutations;
    }

    /// <summary>
    /// Widens the zone maps of one index home over a placed spawn's field values (VIS-08). A bound, conservative upward, so PREPARE's: a snapshot scan of a
    /// reserved slot must not have its cluster pruned by a zone map that does not cover it yet. An aborted spawn only leaves a map wider.
    /// </summary>
    private static void WidenClusterZoneMaps<TStore>(ClusterIndexSlot<TStore>[] ixSlots, byte* dataBase, ArchetypeClusterInfo layout, int clusterChunkId,
        int slotIdx) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (int ixs = 0; ixs < ixSlots.Length; ixs++)
        {
            ref var ixSlot = ref ixSlots[ixs];
            int compSize = layout.ComponentSize(ixSlot.Slot);
            byte* compBase = dataBase + layout.ComponentOffset(ixSlot.Slot) + slotIdx * compSize;
            for (int fi = 0; fi < ixSlot.Fields.Length; fi++)
            {
                ref var field = ref ixSlot.Fields[fi];
                field.ZoneMap?.Widen(clusterChunkId, compBase + field.FieldOffset);
            }
        }
    }

    /// <summary>
    /// Removes a destroyed entity from every B+Tree of one index home and notifies views.
    /// </summary>
    /// <remarks>
    /// The destroy twin of <see cref="InsertClusterIndexEntries{TStore}"/>, generic over the store for the same reason (#655).
    /// <paramref name="dataBase"/> holds this home's component bytes; <paramref name="primaryBase"/> holds the AllowMultiple elementId tail.
    /// </remarks>
    private void RemoveClusterIndexEntries<TStore>(ClusterIndexSlot<TStore>[] ixSlots, ArchetypeEngineState engineState, byte* dataBase,
        byte* primaryBase, ArchetypeClusterInfo layout, int clusterChunkId, byte slotIndex, EntityId entityId, ref ChunkAccessor<TStore> idxAccessor,
        ref ChunkAccessor<TStore> idxAccessorS64, ChunkBasedSegment<TStore> s64Segment, ushort onlySlotMask = ushort.MaxValue)
        where TStore : struct, IPageStore
    {
        if (ixSlots == null || ixSlots.Length == 0)
        {
            return;
        }

        // One shared write per commit instead of one per indexed field (review M4): the counter is read by the StatisticsWorker thread, so each
        // increment is a store other cores may be watching.
        var mutations = 0;

        // A destroyed member leaves its group keys (14 § 5): logged before its entries go, for a replication gather that misses them in the tree.
        var keyMoves = engineState.ClusterState?.GroupKeyMoves;

        for (int s = 0; s < ixSlots.Length; s++)
        {
            ref var ixSlot = ref ixSlots[s];
            if ((onlySlotMask & (1 << ixSlot.Slot)) == 0)
            {
                continue;   // this slot's removal belongs to the tick-fence shadow drain, not here (see the destroy call site)
            }

            int compSize = layout.ComponentSize(ixSlot.Slot);
            byte* compBase = dataBase + layout.ComponentOffset(ixSlot.Slot) + slotIndex * compSize;
            int destroyClusterLocation = clusterChunkId * 64 + slotIndex;
            for (int fi = 0; fi < ixSlot.Fields.Length; fi++)
            {
                ref var field = ref ixSlot.Fields[fi];
                byte* fieldPtr = compBase + field.FieldOffset;
                // The B+Tree takes the key by raw pointer, so pass fieldPtr straight through. Copying into a KeyBytes8 first
                // (an 8-byte struct) smashed the stack for any wider key — a 64-byte String64 field memcpy'd 56 bytes past it
                // (#658) — and silently truncated the key even when it didn't crash.
                // Non-unique index: read the per-entity elementId from the cluster tail and call RemoveValue so only this entity's
                // specific (key, clusterLocation) entry is removed — Remove(key) would wipe the entire buffer at the key and corrupt
                // sibling entities sharing the same field value. Issue #229 Phase 3.
                // Regression test: ClusterIndex_NonUniqueField_DestroyOneEntity_PreservesSiblingsInIndex.
                var useS64 = s64Segment != null && ReferenceEquals(field.Index.Segment, s64Segment);
                mutations++;   // (#665)
                keyMoves?.Note(ixSlot.Slot, field.FieldOffset, destroyClusterLocation, fieldPtr);
                if (field.AllowMultiple)
                {
                    int elementId = *(int*)(primaryBase + layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex));
                    if (useS64)
                    {
                        field.Index.RemoveValue(fieldPtr, elementId, destroyClusterLocation, ref idxAccessorS64);
                    }
                    else
                    {
                        field.Index.RemoveValue(fieldPtr, elementId, destroyClusterLocation, ref idxAccessor);
                    }
                }
                else if (useS64)
                {
                    field.Index.Remove(fieldPtr, out _, ref idxAccessorS64);
                }
                else
                {
                    field.Index.Remove(fieldPtr, out _, ref idxAccessor);
                }

                // Notify views of deletion
                var destroyTable = engineState.SlotToComponentTable[ixSlot.Slot];
                var views = destroyTable.ViewRegistry.GetViewsForField(fi);
                // ViewDeltaEntry carries an 8-byte key, so a wider field cannot be reported to a view. Unreachable in practice —
                // the query layer refuses those key types for predicates, so no view can register on one — but guarded rather
                // than truncated, so widening the delta key later is an additive change (#658).
                if (views.Length > 0 && field.FieldSize <= sizeof(long))
                {
                    var key = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);
                    var destroyHook = QueryPathProbe.PrePublishAppendHook;
                    for (int v = 0; v < views.Length; v++)
                    {
                        var reg = views[v];
                        if (reg.DeltaBuffer.IsClosed)
                        {
                            continue;
                        }

                        byte flags = (byte)((fi & 0x3F) | 0x80); // isDeletion
                        destroyHook?.Invoke();
                        reg.DeltaBuffer.TryAppend(entityId, key, default, PublishStamp, flags, reg.ComponentTag);
                    }
                }
            }
        }    

        engineState.ClusterState.MutationsSinceRebuild += mutations;
    }

    private ref struct SpawnContext
    {
        public ArchetypeMetadata Meta;
        public ArchetypeEngineState EngineState;
        public int ComponentCount;
        public ushort VersionedMask;
        public ChunkAccessor<PersistentStore> MapAccessor;
        public bool HasMapAccessor;
        public ushort LastArchId;
        public bool UseCluster;
        public ArchetypeClusterState ClusterState;
        public ChunkAccessor<PersistentStore> ClusterAccessor;
        public bool HasClusterAccessor;
        public ChunkAccessor<TransientStore> ClusterTransientAccessor;
        public bool HasClusterTransientAccessor;
        public ChunkAccessor<PersistentStore> ClusterIdxAccessor;
        public bool HasClusterIdxAccessor;

        /// <summary>Accessor for the archetype's String64 index segment — a field's nodes live in whichever segment its stride requires (#658).</summary>
        public ChunkAccessor<PersistentStore> ClusterIdxAccessorS64;
        public bool HasClusterIdxAccessorS64;

        /// <summary>Accessors for the archetype's heap-backed Transient index segments — the second index home (#655).</summary>
        public ChunkAccessor<TransientStore> ClusterTransientIdxAccessor;
        public bool HasClusterTransientIdxAccessor;
        public ChunkAccessor<TransientStore> ClusterTransientIdxAccessorS64;
        public bool HasClusterTransientIdxAccessorS64;
        public ChunkAccessor<PersistentStore>[] ClusterSrcAccessors;
        public int ClusterSrcAccessorCount;
        public ChunkAccessor<TransientStore>[] ClusterTransientSrcAccessors;
        public int SvSlotCount;
        public int SvIdxAccessorTotal;
        public ChunkAccessor<PersistentStore>[] SvCompAccessors;
        public ChunkAccessor<PersistentStore>[] SvIdxAccessors;
        public int TrSlotCount;
        public int TrIdxAccessorTotal;
        public ChunkAccessor<TransientStore>[] TrCompAccessors;
        public ChunkAccessor<TransientStore>[] TrIdxAccessors;

        // Issue #229 Phase 1+2: cached spatial-slot fields used by the spawn hot path to route through ClaimSlotInCell without chasing pointers per entity.
        // Populated once per archetype switch in SetupSpawnAccessors. SpatialSlotIndexCached == -1 means either not spatial or no grid configured.
        public SpatialGrid SpatialGridCached;
        // Realms: offset of the [RealmKey] in the spatial component (-1: the archetype lives in realm 0), and the last realm resolved with its grid —
        // one realm-table load per realm change, none per entity in a single-realm batch.
        public int RealmKeyOffsetCached;
        public int RealmKeySlotCached;
        public int RealmKeyOverheadCached;
        public int LastSpawnRealm;
        public SpatialGrid LastSpawnRealmGrid;
        public int SpatialSlotIndexCached;
        public int SpatialComponentOverheadCached;
        public int SpatialFieldOffsetCached;
        public SpatialFieldType SpatialFieldTypeCached;
    }

    /// <summary>
    /// AP-03: rejects, before the append, a commit its publish would fail on. Past the append the commit is in the log (AP-02), so a failure there could
    /// only stop the engine; found here, the commit throws and rolls back clean. The checks:
    /// <list type="bullet">
    /// <item>a spawn or Commit-discipline write that would put a key in a unique index that holds it when the write publishes — committed, or put by
    /// another spawn or write of this transaction: <see cref="UniqueConstraintViolationException"/>;</item>
    /// <item>a spawn that leaves unset a Versioned component a unique index covers: <see cref="PlaceSpawns"/> would index it under whatever bytes its
    /// claimed cluster slot holds (#1259) — <see cref="InvalidOperationException"/>;</item>
    /// <item>a spawn of a spatial archetype that leaves its spatial component unset, which <see cref="PlaceSpawns"/> cannot place in a cell:
    /// <see cref="InvalidOperationException"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The checks follow the publish's order: spawns (<see cref="PlaceSpawns"/>), then destroys (<see cref="FlushPendingDestroys"/>), then the staged
    /// writes in the order <see cref="PublishStagedCommitWrites"/> visits them. A staged write may take a key a destroy of this transaction removes from the
    /// index, or one a staged write visited before it moves away. Two transactions that put one key concurrently both pass this check; the later publish
    /// then fails and stops the engine (#1258).
    /// </remarks>
    private void ValidatePublishBeforeAppend()
    {
        // The common commit checks nothing: no spawn of an archetype with a unique index or a spatial cell, no staged write to a unique-indexed component.
        // It returns here, before the epoch scope and the accessors.
        var checkVersioned = AnyVersionedWriteNeedsCheck();
        var checkSpawns = _spawnedEntities is { Count: > 0 } && AnySpawnNeedsCheck();
        var checkStaged = _commitStagingBuffer != null && AnyStagedWriteNeedsCheck();
        if (!checkVersioned && !checkSpawns && !checkStaged)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        var check = new PublishCheck();
        try
        {
            // The publish's order: the Versioned writes' index moves (PublishPreparedComponents), then the spawns, the destroys and the staged writes.
            if (checkVersioned)
            {
                CheckVersionedUniqueKeys(ref check);
            }

            if (checkSpawns)
            {
                t_validationOwnKeys ??= new HashSet<UniqueKeyRef>();
                t_validationOwnKeys.EnsureCapacity(_spawnedEntities.Count);
                CheckSpawns(ref check);
            }

            if (checkStaged)
            {
                CheckStagedUniqueKeys(ref check);
            }
        }
        finally
        {
            check.Dispose();
            RecycleKeySet(ref t_validationOwnKeys);
            RecycleKeySet(ref t_validationFreedKeys);
        }
    }

    /// <summary>
    /// The keys a commit's validation puts, and those its earlier staged writes free. Per thread, since the validation runs on the committing thread and
    /// does not nest: a bulk load's commits of thousands of spawns reuse one set instead of regrowing one per transaction.
    /// </summary>
    [ThreadStatic] private static HashSet<UniqueKeyRef> t_validationOwnKeys;

    [ThreadStatic] private static HashSet<UniqueKeyRef> t_validationFreedKeys;

    private static void RecycleKeySet(ref HashSet<UniqueKeyRef> set)
    {
        if (set == null)
        {
            return;
        }

        // Clear zeroes the entries, so no index or key pointer outlives the validation. A set grown past a large bulk commit is dropped.
        if (set.Count > 65_536)
        {
            set = null;
        }
        else
        {
            set.Clear();
        }
    }

    /// <summary>Whether a Versioned write of this commit owes a unique index a move (<see cref="CheckVersionedUniqueKeys"/>): counted by PREPARE.</summary>
    private bool AnyVersionedWriteNeedsCheck() => _pendingUniqueMoves > 0;

    /// <summary>
    /// The unique keys the Versioned writes move in the publish (#1262), in its order — <c>PublishClusterIndexes</c>, one entry after another, before the
    /// spawns. The key a write takes must be free then, or freed by a write before it, or its own entity's; the key it leaves is free for the writes after
    /// it and for the spawns. A write to an entity this commit destroys moves nothing (the destroy removes its key, after the spawns).
    /// </summary>
    private void CheckVersionedUniqueKeys(ref PublishCheck check)
    {
        foreach (ref readonly var e in CollectionsMarshal.AsSpan(_publishEntries))
        {
            if (!e.IndexPending)
            {
                continue;
            }

            var cs = _dbe._stateByRouting[e.ArchId].ClusterState;
            if ((cs.UniqueIndexedSlots & (1 << e.CompSlot)) == 0 || (_pendingDestroys != null && _pendingDestroys.Contains(EntityId.FromRaw(e.Pk))))
            {
                continue;
            }

            var table = e.Info.ComponentTable;
            var overhead = table.ComponentOverhead;
            byte* newComp = e.CurCompContentChunkId != 0
                ? check.Contents.For(table.ComponentSegment).GetChunkAddress(e.CurCompContentChunkId) + overhead
                : null;
            // Against the newest committed revision PREPARE saw, not the read one: a key a concurrent commit moved off is the one this write moves from (#1263).
            byte* oldComp = e.BaseCompChunkId != 0 ? check.Contents.For(table.ComponentSegment).GetChunkAddress(e.BaseCompChunkId) + overhead : null;
            var location = e.ClusterChunkId * 64 + e.SlotIndex;
            for (var ixs = 0; ixs < cs.IndexSlots.Length; ixs++)
            {
                ref var ixSlot = ref cs.IndexSlots[ixs];
                if (ixSlot.Slot != e.CompSlot)
                {
                    continue;
                }

                var fields = ixSlot.Fields;
                for (var fi = 0; fi < fields.Length; fi++)
                {
                    ref var field = ref fields[fi];
                    if (field.AllowMultiple)
                    {
                        continue;
                    }

                    // Unchanged: the publish moves nothing (ReconcileClusterIndexAndViews compares the same bytes).
                    if (newComp != null && oldComp != null
                        && new ReadOnlySpan<byte>(oldComp + field.FieldOffset, field.FieldSize).SequenceEqual(
                            new ReadOnlySpan<byte>(newComp + field.FieldOffset, field.FieldSize)))
                    {
                        continue;
                    }

                    if (newComp != null)
                    {
                        var key = newComp + field.FieldOffset;
                        ref var accessor = ref ReferenceEquals(field.Index.Segment, cs.IndexSegmentString64)
                            ? ref check.PersistentIndexesS64.For(cs.IndexSegmentString64)
                            : ref check.PersistentIndexes.For(field.Index.Segment);
                        var holder = field.Index.TryGet(key, ref accessor);
                        if (holder.IsSuccess && holder.Value != location && !IsFreedBeforeThisWrite(field.Index, key))
                        {
                            ThrowHelper.ThrowUniqueConstraintViolation();
                        }

                        ClaimOwnKey(field.Index, key);
                    }

                    if (oldComp != null)
                    {
                        t_validationFreedKeys ??= new HashSet<UniqueKeyRef>();
                        t_validationFreedKeys.Add(new UniqueKeyRef(field.Index, oldComp + field.FieldOffset));
                    }
                }
            }
        }
    }

    /// <summary>Whether a spawn of this commit is of an archetype with a unique index or a spatial cell: the archetypes <see cref="CheckSpawns"/>
    /// checks.</summary>
    private bool AnySpawnNeedsCheck()
    {
        var spawns = CollectionsMarshal.AsSpan(_spawnedEntities);
        var lastArchetype = -1;
        for (var i = 0; i < spawns.Length; i++)
        {
            var archetype = spawns[i].Id.ArchetypeId;
            if (archetype == lastArchetype)
            {
                continue;
            }

            lastArchetype = archetype;
            var cs = _dbe._stateByRouting[archetype]?.ClusterState;
            if (cs != null && (cs.UniqueIndexedSlots != 0 || IsSpatialRouted(cs)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a staged write of this commit is to a component a unique index covers.</summary>
    private bool AnyStagedWriteNeedsCheck()
    {
        foreach (var kvp in _componentInfos)
        {
            var info = kvp.Value;
            if (info.CommitStaged == null || info.CommitStaged.Count == 0 || info.ComponentTable.StorageMode != StorageMode.SingleVersion)
            {
                continue;
            }

            var lastArchetype = -1;
            foreach (var staged in info.CommitStaged)
            {
                var archetype = EntityId.FromRaw(staged.Key).ArchetypeId;
                if (archetype == lastArchetype)
                {
                    continue;
                }

                lastArchetype = archetype;
                if (StagedSlotIsUniqueIndexed(info, (ushort)archetype, out _, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The component's slot in the archetype, when a unique index of the persistent home covers it — the home a staged write publishes to.</summary>
    private bool StagedSlotIsUniqueIndexed(ComponentInfo info, ushort archetype, out ArchetypeMetadata meta, out int compSlot)
    {
        meta = _dbe.GetMetaByRouting(archetype);
        compSlot = -1;
        var cs = _dbe._stateByRouting[archetype]?.ClusterState;
        if (cs?.IndexSlots == null || !meta.TryGetSlot(info.ComponentTypeId, out var slot))
        {
            return false;
        }

        compSlot = slot;
        return (cs.UniqueIndexedSlots & (1 << slot)) != 0;
    }

    private bool IsSpatialRouted(ArchetypeClusterState cs)
    {
        ref readonly var ss = ref cs.SpatialSlot;
        return ss.HasSpatialIndex && (_dbe.Realm0Grid != null || ss.HasRealmKey);
    }

    /// <summary>The chunk accessors one validation reuses, so a run of keys in one index pins its root and inner nodes once.</summary>
    private struct PublishCheck : IDisposable
    {
        public SegmentAccessorCache<PersistentStore> Contents;
        public SegmentAccessorCache<PersistentStore> Clusters;
        public SegmentAccessorCache<PersistentStore> PersistentIndexes;
        public SegmentAccessorCache<PersistentStore> PersistentIndexesS64;
        public SegmentAccessorCache<TransientStore> TransientIndexes;
        public SegmentAccessorCache<TransientStore> TransientIndexesS64;

        public void Dispose()
        {
            Contents.Dispose();
            Clusters.Dispose();
            PersistentIndexes.Dispose();
            PersistentIndexesS64.Dispose();
            TransientIndexes.Dispose();
            TransientIndexesS64.Dispose();
        }
    }

    /// <summary>One chunk accessor, rebuilt only when the segment changes.</summary>
    private struct SegmentAccessorCache<TStore> : IDisposable where TStore : struct, IPageStore
    {
        private ChunkBasedSegment<TStore> _segment;
        private ChunkAccessor<TStore> _accessor;

        [UnscopedRef]
        public ref ChunkAccessor<TStore> For(ChunkBasedSegment<TStore> segment)
        {
            if (!ReferenceEquals(segment, _segment))
            {
                Dispose();
                _accessor = segment.CreateChunkAccessor();
                _segment = segment;
            }

            return ref _accessor;
        }

        public void Dispose()
        {
            if (_segment != null)
            {
                _accessor.Dispose();
                _segment = null;
            }
        }
    }

    /// <summary>The spawns' checks, the per-archetype facts resolved once per run of one archetype, as <see cref="PlaceSpawns"/> does.</summary>
    private void CheckSpawns(ref PublishCheck check)
    {
        var spawns = CollectionsMarshal.AsSpan(_spawnedEntities);
        var lastArchetype = -1;
        ArchetypeEngineState es = null;
        ArchetypeClusterState cs = null;
        var spatial = false;
        var uniquePersistent = false;
        var uniqueTransient = false;
        for (var i = 0; i < spawns.Length; i++)
        {
            ref var entry = ref spawns[i];
            if (_pendingDestroys != null && _pendingDestroys.Contains(entry.Id))
            {
                continue;
            }

            if (entry.Id.ArchetypeId != lastArchetype)
            {
                lastArchetype = entry.Id.ArchetypeId;
                es = _dbe._stateByRouting[lastArchetype];
                cs = es?.ClusterState;
                if (cs != null)
                {
                    spatial = IsSpatialRouted(cs);
                    var unique = cs.UniqueIndexedSlots;
                    uniquePersistent = (unique & 0xFFFF) != 0;
                    uniqueTransient = (unique >> 16) != 0;
                }
            }

            if (cs == null)
            {
                continue;
            }

            if (spatial)
            {
                CheckSpawnSpatialComponent(es, cs, in entry);
            }

            if (uniquePersistent)
            {
                CheckSpawnUniqueKeys(cs.IndexSlots, cs.IndexSegmentString64, es, in entry, ref check, ref check.PersistentIndexes,
                    ref check.PersistentIndexesS64);
            }

            if (uniqueTransient)
            {
                CheckSpawnUniqueKeys(cs.TransientIndexSlots, cs.TransientIndexSegmentString64, es, in entry, ref check, ref check.TransientIndexes,
                    ref check.TransientIndexesS64);
            }
        }
    }

    /// <summary>
    /// A spawn routed to a spatial cell must carry its spatial component: <see cref="PlaceSpawns"/> reads its bounds to pick the cell. The routing
    /// condition is <see cref="SetupSpawnAccessors"/>'s; only a Versioned component can be missing, as a SingleVersion one is always staged.
    /// </summary>
    private static void CheckSpawnSpatialComponent(ArchetypeEngineState es, ArchetypeClusterState cs, in SpawnEntry entry)
    {
        ref readonly var ss = ref cs.SpatialSlot;
        var missing = es.SlotToComponentTable[ss.Slot].StorageMode == StorageMode.Versioned ? entry.VerLoc[ss.Slot] == 0 : entry.Stage[ss.Slot] == 0;
        if (missing)
        {
            throw new InvalidOperationException($"Spatial archetype must provide its spatial component at spawn time (slot {ss.Slot} is missing).");
        }
    }

    /// <summary>One spawn's unique keys, in one index home of its archetype.</summary>
    private void CheckSpawnUniqueKeys<TStore>(ClusterIndexSlot<TStore>[] ixSlots, ChunkBasedSegment<TStore> s64Segment, ArchetypeEngineState es,
        in SpawnEntry entry, ref PublishCheck check, ref SegmentAccessorCache<TStore> indexes, ref SegmentAccessorCache<TStore> indexesS64)
        where TStore : struct, IPageStore
    {
        for (var ixs = 0; ixs < ixSlots.Length; ixs++)
        {
            ref var ixSlot = ref ixSlots[ixs];
            if (ixSlot.Fields == null || !HasUniqueField(ixSlot.Fields))
            {
                continue;
            }

            var table = es.SlotToComponentTable[ixSlot.Slot];
            byte* source;
            if (table.StorageMode == StorageMode.Versioned)
            {
                var chunkId = entry.VerLoc[ixSlot.Slot];
                if (chunkId == 0)
                {
                    throw new InvalidOperationException(
                        $"A spawn must provide each Versioned component a unique index covers (slot {ixSlot.Slot} is missing): unset, it would be "
                        + "indexed under whatever its claimed cluster slot holds.");
                }

                source = check.Contents.For(table.ComponentSegment).GetChunkAddress(chunkId) + table.ComponentOverhead;
            }
            else
            {
                // A SingleVersion component the spawn does not supply is staged as zeros, disabled: indexed, it held key 0 against every later spawn that
                // supplies 0, and the first two such spawns collided past their append. Refused, as the Versioned case above (QENABLE-01). Supplied means
                // the bits it publishes with: an Enable(comp, value) on the pending spawn stages its bit in the overlay.
                var spawnBits = entry.EnabledBits;
                if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(entry.Id, out var stagedBits))
                {
                    spawnBits = stagedBits;
                }

                if ((spawnBits & (1 << ixSlot.Slot)) == 0)
                {
                    throw new InvalidOperationException(
                        $"A spawn must provide each SingleVersion component a unique index covers (slot {ixSlot.Slot} is missing): unset, it would hold "
                        + "key 0 in that index.");
                }

                var stage = entry.Stage[ixSlot.Slot];
                if (stage == 0)
                {
                    continue;
                }

                source = SpawnArena.Resolve(stage) + table.ComponentOverhead;
            }

            var fields = ixSlot.Fields;
            for (var fi = 0; fi < fields.Length; fi++)
            {
                ref var field = ref fields[fi];
                if (field.AllowMultiple)
                {
                    continue;
                }

                var key = source + field.FieldOffset;
                ref var accessor = ref ReferenceEquals(field.Index.Segment, s64Segment) ? ref indexesS64.For(s64Segment) : ref indexes.For(field.Index.Segment);
                // A key a Versioned write of this commit moves away is free by then: their index moves publish before the spawns.
                if (field.Index.TryGet(key, ref accessor).IsSuccess && !IsFreedBeforeThisWrite(field.Index, key))
                {
                    ThrowHelper.ThrowUniqueConstraintViolation();
                }

                ClaimOwnKey(field.Index, key);
            }
        }
    }

    /// <summary>
    /// The Commit-discipline staged writes' unique keys: the ones a write changes, as <c>PublishStagedEntry</c> moves them, in its order. A key the index
    /// holds is free for the write when a destroy of this transaction removes its holder's entry in the publish, before the staged writes, or when a staged
    /// write visited earlier moves it away.
    /// </summary>
    private void CheckStagedUniqueKeys(ref PublishCheck check)
    {
        foreach (var kvp in _componentInfos)
        {
            var info = kvp.Value;
            if (info.CommitStaged == null || info.ComponentTable.StorageMode != StorageMode.SingleVersion)
            {
                continue;
            }

            foreach (var staged in info.CommitStaged)
            {
                var entityId = EntityId.FromRaw(staged.Key);
                if (_pendingDestroys != null && _pendingDestroys.Contains(entityId))
                {
                    continue;
                }

                if (!StagedSlotIsUniqueIndexed(info, entityId.ArchetypeId, out var meta, out var compSlot))
                {
                    continue;
                }

                var cs = _dbe._stateByRouting[entityId.ArchetypeId].ClusterState;
                var location = staged.Value.Location;
                var layout = cs.Layout;
                byte* head = null;
                for (var ixs = 0; ixs < cs.IndexSlots.Length; ixs++)
                {
                    ref var ixSlot = ref cs.IndexSlots[ixs];
                    if (ixSlot.Slot != compSlot || !HasUniqueField(ixSlot.Fields))
                    {
                        continue;
                    }

                    if (head == null)
                    {
                        head = check.Clusters.For(cs.ClusterSegment).GetChunkAddress(location >> 6) + layout.ComponentOffset(compSlot)
                               + (location & 63) * layout.ComponentSize(compSlot);
                    }

                    var value = _commitStagingBuffer + staged.Value.Offset;
                    var fields = ixSlot.Fields;
                    for (var fi = 0; fi < fields.Length; fi++)
                    {
                        ref var field = ref fields[fi];
                        var key = value + field.FieldOffset;
                        var current = head + field.FieldOffset;

                        // Unchanged: the publish moves nothing (ReconcileClusterIndexAndViews compares the same bytes).
                        if (field.AllowMultiple || new ReadOnlySpan<byte>(current, field.FieldSize).SequenceEqual(new ReadOnlySpan<byte>(key, field.FieldSize)))
                        {
                            continue;
                        }

                        ref var accessor = ref ReferenceEquals(field.Index.Segment, cs.IndexSegmentString64)
                            ? ref check.PersistentIndexesS64.For(cs.IndexSegmentString64)
                            : ref check.PersistentIndexes.For(field.Index.Segment);
                        var holder = field.Index.TryGet(key, ref accessor);
                        if (holder.IsSuccess && holder.Value != location && !IsFreedBeforeThisWrite(field.Index, key)
                            && !IsRemovedByOwnDestroy(meta, cs, ref check, holder.Value, compSlot))
                        {
                            ThrowHelper.ThrowUniqueConstraintViolation();
                        }

                        ClaimOwnKey(field.Index, key);

                        // Once this write publishes, its old key is free for the writes visited after it.
                        t_validationFreedKeys ??= new HashSet<UniqueKeyRef>();
                        t_validationFreedKeys.Add(new UniqueKeyRef(field.Index, current));
                    }
                }
            }
        }
    }

    /// <summary>Records a key this transaction puts in a unique index; a second put of one key — by the index's own comparison — is a violation.</summary>
    private static void ClaimOwnKey(IBTreeIndex index, byte* key)
    {
        if (index.SkipsKey(key))
        {
            return;   // key 0 of a sparse index is no entry, so any number of entities hold it (IX-09)
        }

        t_validationOwnKeys ??= new HashSet<UniqueKeyRef>();
        if (!t_validationOwnKeys.Add(new UniqueKeyRef(index, key)))
        {
            ThrowHelper.ThrowUniqueConstraintViolation();
        }
    }

    private static bool IsFreedBeforeThisWrite(IBTreeIndex index, byte* key) =>
        t_validationFreedKeys != null && t_validationFreedKeys.Contains(new UniqueKeyRef(index, key));

    /// <summary>
    /// Whether the entity at <paramref name="holderLocation"/> is destroyed by this transaction and its destroy removes its entry for
    /// <paramref name="compSlot"/> in the publish: <see cref="FlushPendingDestroys"/> leaves a slot the tick fence maintains to the fence when the entity has
    /// a pending shadow, the same mask computed the same way.
    /// </summary>
    private bool IsRemovedByOwnDestroy(ArchetypeMetadata meta, ArchetypeClusterState cs, ref PublishCheck check, int holderLocation, int compSlot)
    {
        if (_pendingDestroys == null)
        {
            return false;
        }

        var holderBase = check.Clusters.For(cs.ClusterSegment).GetChunkAddress(holderLocation >> 6);
        var holder = EntityId.FromRaw(*(long*)(holderBase + cs.Layout.EntityIdsOffset + (holderLocation & 63) * 8));
        if (!_pendingDestroys.Contains(holder))
        {
            return false;
        }

        var removeHereMask = (ushort)~cs.FenceRecordedSlots(holderLocation);
        return (removeHereMask & (1 << compSlot)) != 0;
    }

    private static bool HasUniqueField<TStore>(ClusterIndexField<TStore>[] fields) where TStore : struct, IPageStore
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (!fields[i].AllowMultiple)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A key in one unique index, by reference: the bytes stay put while the validation's epoch scope lives. Equal and hashed by the index's own key
    /// comparison, so a floating-point -0 and +0 are one key, as are two NaNs.
    /// </summary>
    private readonly struct UniqueKeyRef : IEquatable<UniqueKeyRef>
    {
        private readonly IBTreeIndex _index;
        private readonly byte* _key;
        private readonly int _hash;

        public UniqueKeyRef(IBTreeIndex index, byte* key)
        {
            _index = index;
            _key = key;
            _hash = HashCode.Combine(RuntimeHelpers.GetHashCode(index), index.KeyHash(key));
        }

        public bool Equals(UniqueKeyRef other) => ReferenceEquals(_index, other._index) && _index.KeysEqual(_key, other._key);

        public override bool Equals(object obj) => obj is UniqueKeyRef other && Equals(other);

        public override int GetHashCode() => _hash;
    }

    /// <summary>
    /// PREPARE half of a spawn (VIS-08, plan 06 step 5.2): the slot claimed and reserved — taken, its occupancy bit clear — the payload copied into it, and
    /// the EntityMap record inserted with <c>BornTSN = Pending(owner)</c>, so the entity exists, flagged with its owner, before the commit point. A reader
    /// resolves the pending born through the owner's commit card (VIS-01). PUBLISH (<see cref="PublishSpawns"/>) stamps the record, sets the occupancy bit,
    /// and adds the B+Tree entries; an abort before it un-places the spawn (<see cref="UnplaceSpawns"/>, AP-07).
    /// </summary>
    private void PlaceSpawns()
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return;
        }

        // Nothing placed yet: a reused list (SetCount) keeps stale placement fields, and an undo after a throw part-way must see only this pass's.
        foreach (ref var spawn in CollectionsMarshal.AsSpan(_spawnedEntities))
        {
            spawn.Placed = false;
        }

        // Pre-size EntityMaps to avoid per-insert splits
        if (_spawnedEntities.Count >= 64)
        {
            Span<ushort> seenArchetypes = stackalloc ushort[16];
            int seenCount = 0;
            foreach (var entry in _spawnedEntities)
            {
                var archId = entry.Id.ArchetypeId;
                bool alreadySeen = false;
                for (int i = 0; i < seenCount; i++)
                {
                    if (seenArchetypes[i] == archId) { alreadySeen = true; break; }
                }
                if (alreadySeen) continue;
                if (seenCount < 16) seenArchetypes[seenCount++] = archId;

                var es = _dbe._stateByRouting[archId];
                if (es?.EntityMap != null)
                {
                    es.EntityMap.EnsureCapacity(es.EntityMap.EntryCount + _spawnedEntities.Count, _changeSet);
                }
            }
        }

        using var guard = EpochGuard.Enter(_epochManager);

        // Hoist stackalloc outside the loop — cluster record is the largest: 19B base + 16 × 4B Versioned = 83B (≥ legacy 78B)
        byte* recordPtr = stackalloc byte[ClusterEntityRecordAccessor.BaseRecordSize + EntityRecordAccessor.MaxComponentCount * sizeof(int)];

        // Hoist all accessors outside the per-entity loop.
        // Track last-used archetype — covers the dominant case (single archetype per TX).
        // When archetype changes, dispose old accessors and create new ones.
        var ctx = new SpawnContext();
        Span<int> svSlots = stackalloc int[16];
        Span<int> svIdxAccessorBase = stackalloc int[16]; // offset into svIdxAccessors for each slot
        Span<int> trSlots = stackalloc int[16];
        Span<int> trIdxAccessorBase = stackalloc int[16];
        // Narrowphase scratch for ReadAndValidateBoundsFromPtr in the cluster spatial spawn hook (#230
        // Phase 1). Hoisted out of the per-entity loop to avoid CA2014 stack-pressure accumulation when
        // spawning many entities in one transaction — the per-iteration allocation would not release
        // until PlaceSpawns returns.
        // Sized for 3D ([minX, minY, minZ, maxX, maxY, maxZ]); 2D reads only populate the first 4 slots. Issue #230 Phase 3 unified 2D/3D per-cell index paths.
        Span<double> spawnSpatialCoords = stackalloc double[6];

        // Step 15 (D4): a batch of spatial spawns is placed in per-cell Morton order, so a bulk load is born at the packing bound instead of at ~100 %
        // of every cell it touches. The list itself is left alone — _spawnedEntityIndex maps ids into it — and only the visiting order changes.
        // The threshold is a batch property, not a per-entity one: realm 0's, or the first registered realm's in a world made only of named realms.
        var realms = _dbe.RealmTable;
        var sortRealm = realms == null ? null : realms.Default ?? (realms.Registered.Length > 0 ? realms.Registered[0] : null);
        var sortThreshold = sortRealm != null ? sortRealm.GridConfig.BatchSpawnSortThreshold : 0;
        var spawnOrder = sortThreshold > 0 && _spawnedEntities.Count >= sortThreshold ? BuildSpatialSpawnOrder() : null;

        try
        {
            var spawnCount = _spawnedEntities.Count;
            for (var spawnIndex = 0; spawnIndex < spawnCount; spawnIndex++)
            {
                var entry = _spawnedEntities[spawnOrder != null ? spawnOrder[spawnIndex] : spawnIndex];

                // Skip entities that were also destroyed in this transaction — no EntityMap insert needed
                if (_pendingDestroys != null && _pendingDestroys.Contains(entry.Id))
                {
                    continue;
                }

                // Build EntityRecord on stack from SpawnEntry
                ref var header = ref *(EntityRecordHeader*)recordPtr;
                header = default;
                header.BornTSN = EntityRecordHeader.PendingBorn(TSN);

                ushort enabledBits = entry.EnabledBits;
                if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(entry.Id, out var newBits))
                {
                    enabledBits = newBits;
                }
                header.EnabledBits = enabledBits;

                // Hoist all per-archetype state — recycle when archetype changes
                if (!ctx.HasMapAccessor || entry.Id.ArchetypeId != ctx.LastArchId)
                {
                    // Dispose previous archetype's accessors
                    DisposeSpawnAccessors(ref ctx);

                    SetupSpawnAccessors(ref ctx, entry.Id.ArchetypeId, svSlots, svIdxAccessorBase, trSlots, trIdxAccessorBase);
                }

                if (ctx.UseCluster)
                {
                    // ═══════════════════════════════════════════════════════════════
                    // Cluster path: claim slot, copy data to cluster, write ClusterEntityRecord
                    // ═══════════════════════════════════════════════════════════════
                    var layout = ctx.ClusterState.Layout;
                    int clusterChunkId, slotIdx;
                    byte* clusterBase; // Primary segment base (has metadata: OccupancyBits, EnabledBits, EntityIds)
                    byte* clusterTransientBase = null; // TransientStore base (only for mixed archetypes)

                    // Issue #229 Phase 1+2: when the engine has a configured SpatialGrid AND this archetype has a spatial field, route the claim through
                    // ClaimSlotInCell so the new entity lands in a cluster belonging to its spatial cell. SpawnContext caches the spatial-slot routing info
                    // once per archetype switch (see SetupSpawnAccessors) so this hot branch is a single field read.
                    int spatialSlotIdx = ctx.SpatialSlotIndexCached;
                    bool useCellClaim = spatialSlotIdx >= 0;
                    SpatialGrid spawnGrid = null;
                    var rankedSpawn = false;
                    int computedCellKey = -1;
                    float spawnPx = 0f;
                    float spawnPy = 0f;
                    float spawnPz = 0f;
                    if (useCellClaim)
                    {
                        // #839: the spatial payload is in the spawn arena unless the component is Versioned, in which case it still has a content chunk.
                        // [SpatialIndex] is rejected on Transient at schema build, so only these two cases exist here.
                        var spatialTable = ctx.EngineState.SlotToComponentTable[spatialSlotIdx];
                        byte* spatialSrcAddr;
                        if (spatialTable.StorageMode == StorageMode.Versioned)
                        {
                            int spatialSrcChunkId = entry.VerLoc[spatialSlotIdx];
                            if (spatialSrcChunkId == 0)
                            {
                                throw new InvalidOperationException(
                                    $"Spatial archetype must provide its spatial component at spawn time (slot {spatialSlotIdx} is missing).");
                            }
                            spatialSrcAddr = ctx.ClusterSrcAccessors[spatialSlotIdx].GetChunkAddress(spatialSrcChunkId);
                        }
                        else
                        {
                            int spatialStage = entry.Stage[spatialSlotIdx];
                            if (spatialStage == 0)
                            {
                                throw new InvalidOperationException(
                                    $"Spatial archetype must provide its spatial component at spawn time (slot {spatialSlotIdx} is missing).");
                            }
                            spatialSrcAddr = SpawnArena.Resolve(spatialStage);
                        }
                        // Realms: the entity's realm (its [RealmKey], validated at Spawn) names the grid it is placed in; realm 0 without a key.
                        spawnGrid = ctx.SpatialGridCached;
                        if (ctx.RealmKeyOffsetCached >= 0)
                        {
                            // The key's own staged row (its component may be the spatial one or another); no stage = no value = realm 0.
                            var keyStage = entry.Stage[ctx.RealmKeySlotCached];
                            ushort keyFallback = 0;
                            var key = keyStage != 0
                                ? (ushort*)(SpawnArena.Resolve(keyStage) + ctx.RealmKeyOverheadCached + ctx.RealmKeyOffsetCached)
                                : &keyFallback;
                            var realm = *key;
                            if (realm != ctx.LastSpawnRealm)
                            {
                                // The staged key can have been rewritten in place since Spawn validated it (a write to an own spawn). An invalid one is
                                // placed in the validated realm and the key corrected, counted — the commit never throws for it (D-2).
                                if (!ctx.ClusterState.IsValidRealmForEntity(realm))
                                {
                                    realm = ctx.ClusterState.SpawnFallbackRealm(entry.SpawnRealm);
                                    *key = realm;
                                    Interlocked.Increment(ref ctx.ClusterState.LastTickRealmKeyReverts);
                                }

                                ctx.LastSpawnRealm = realm;
                                ctx.LastSpawnRealmGrid = _dbe.RealmTable.Get(realm).Grid;
                            }

                            spawnGrid = ctx.LastSpawnRealmGrid;
                        }

                        byte* spatialFieldPtr = spatialSrcAddr + ctx.SpatialComponentOverheadCached + ctx.SpatialFieldOffsetCached;
                        computedCellKey = spawnGrid.WorldToCellKeyFromSpatialField(spatialFieldPtr, ctx.SpatialFieldTypeCached);

                        // The same field, read once more for its centre and rebased into the cell's frame (C15): the claim ranks the cell's clusters by
                        // how far each stored bound grows to admit it. Cheap — the field is in the line WorldToCellKeyFromSpatialField just read.
                        // A Morton-ordered batch is placed first fit: the order IS the placement, and ranking each arrival against every open cluster
                        // would let it join an older open cluster whose box happens to be nearer and mix the sorted runs (measured: fill clusters
                        // spanning the cell with the cap on). The ranked overload is for spawns that arrive one at a time or in small batches.
                        rankedSpawn = spawnOrder == null
                            && (spawnGrid.Config.LeastEnlargementPlacement || spawnGrid.Config.GrowthCapPlacement);
                        if (rankedSpawn)
                        {
                            SpatialGrid.ReadSpatialCenter3D(spatialFieldPtr, ctx.SpatialFieldTypeCached, out var worldX, out var worldY, out var worldZ);
                            spawnGrid.CellOrigin(computedCellKey, out var originX, out var originY, out var originZ);
                            spawnPx = (float)(worldX - originX);
                            spawnPy = (float)(worldY - originY);
                            spawnPz = (float)(worldZ - originZ);
                        }
                    }

                    if (ctx.ClusterState.ClusterSegment != null)
                    {
                        // Mixed or pure-SV/V: PersistentStore is primary
                        if (useCellClaim)
                        {
                            (clusterChunkId, slotIdx) = rankedSpawn
                                ? ctx.ClusterState.ClaimSlotInCell(computedCellKey, spawnPx, spawnPy, spawnPz, ref ctx.ClusterAccessor, _changeSet,
                                    spawnGrid, TSN)
                                : ctx.ClusterState.ClaimSlotInCell(computedCellKey, ref ctx.ClusterAccessor, _changeSet, spawnGrid, TSN);
                        }
                        else
                        {
                            (clusterChunkId, slotIdx) = ctx.ClusterState.ClaimSlot(ref ctx.ClusterAccessor, _changeSet, TSN);
                        }
                        PlacedSpawn(spawnOrder != null ? spawnOrder[spawnIndex] : spawnIndex, clusterChunkId, slotIdx);
                        clusterBase = ctx.ClusterAccessor.GetChunkAddress(clusterChunkId, true);
                        if (ctx.HasClusterTransientAccessor)
                        {
                            clusterTransientBase = ctx.ClusterTransientAccessor.GetChunkAddress(clusterChunkId, true);
                        }
                    }
                    else
                    {
                        // Pure-Transient: TransientStore is primary
                        if (useCellClaim)
                        {
                            (clusterChunkId, slotIdx) = rankedSpawn
                                ? ctx.ClusterState.ClaimSlotInCell(computedCellKey, spawnPx, spawnPy, spawnPz, ref ctx.ClusterTransientAccessor,
                                    spawnGrid, TSN)
                                : ctx.ClusterState.ClaimSlotInCell(computedCellKey, ref ctx.ClusterTransientAccessor, spawnGrid, TSN);
                        }
                        else
                        {
                            (clusterChunkId, slotIdx) = ctx.ClusterState.ClaimSlot(ref ctx.ClusterTransientAccessor, TSN);
                        }
                        PlacedSpawn(spawnOrder != null ? spawnOrder[spawnIndex] : spawnIndex, clusterChunkId, slotIdx);
                        clusterBase = ctx.ClusterTransientAccessor.GetChunkAddress(clusterChunkId, true);
                    }

                    // The slot is claimed and not occupied: out of the published set until PUBLISH, so no page ever holds its occupancy bit (VIS-08, CM-01).
                    // Its index lock taken before the card makes the entity visible, held until its PUBLISH has written its index entries: another commit that
                    // sees it waits before maintaining them or hiding its slot (ArchetypeClusterState.LockSlotIndexes). A slot just claimed: at most the
                    // previous occupant's destroy is still releasing the lock.
                    ctx.ClusterState.LockSlotIndexes(clusterChunkId, slotIdx);
                    _dbe.SpawnClaimedProbe?.Invoke();

                    // Copy component data from per-component chunks to cluster SoA slots.
                    // Transient slots are copied to TransientSegment; SV/V to ClusterSegment.
                    ushort transientMask = ctx.Meta.TransientSlotMask;
                    for (int slot = 0; slot < ctx.ComponentCount; slot++)
                    {
                        var table = ctx.EngineState.SlotToComponentTable[slot];
                        int overhead = table.ComponentOverhead;
                        int compSize = layout.ComponentSize(slot);

                        // #839: the source is the spawn arena for SingleVersion and Transient, and a real content chunk only for Versioned — where the chunk
                        // is the first revision's payload and the cluster slot is a HEAD cache over the chain.
                        byte* srcAddr;
                        byte* dstBase;
                        if (table.StorageMode == StorageMode.Versioned)
                        {
                            int srcChunkId = entry.VerLoc[slot];
                            if (srcChunkId == 0)
                            {
                                continue;
                            }
                            srcAddr = ctx.ClusterSrcAccessors[slot].GetChunkAddress(srcChunkId);
                            dstBase = clusterBase;
                        }
                        else
                        {
                            int srcStage = entry.Stage[slot];
                            if (srcStage == 0)
                            {
                                continue;
                            }
                            srcAddr = SpawnArena.Resolve(srcStage);
                            // A Transient slot's bytes go to the TransientSegment; a pure-Transient archetype has no separate one, so clusterBase IS it.
                            dstBase = (transientMask & (1 << slot)) != 0 ? (clusterTransientBase != null ? clusterTransientBase : clusterBase) : clusterBase;
                        }
                        byte* dstAddr = dstBase + layout.ComponentOffset(slot) + slotIdx * compSize;
                        Unsafe.CopyBlockUnaligned(dstAddr, srcAddr + overhead, (uint)compSize);
                    }

                    // Write full EntityId to cluster primary segment
                    *(long*)(clusterBase + layout.EntityIdsOffset + slotIdx * 8) = (long)entry.Id.RawValue;

                    // Write EnabledBits in cluster from the ABSOLUTE mask — clearing as well as setting. A freed slot is not guaranteed clean: a publish
                    // that passed its occupancy check while a concurrent destroy was clearing the slot (ClearSlotMetadata clears the bits BEFORE the
                    // occupancy bit) can leave one of the previous occupant's bits behind, and a spawn that only ORs would inherit it — possibly for a
                    // component this entity never supplied (#998 review, ENABLE-01). A bit already right costs a plain read, no atomic.
                    // Interlocked, not |= / &=: the word is one per component per CLUSTER, and two transactions committing spawns into the same cluster
                    // (their claims are independent CASes on the occupancy word) each read-modify-write it — a plain write lets one drop the other's bit.
                    for (int slot = 0; slot < ctx.ComponentCount; slot++)
                    {
                        ref var word = ref *(long*)(clusterBase + layout.EnabledBitsOffset(slot));
                        var entityBit = 1L << slotIdx;
                        var wanted = (enabledBits & (1 << slot)) != 0;
                        if (((Volatile.Read(ref word) & entityBit) != 0) == wanted)
                        {
                            continue;
                        }

                        if (wanted)
                        {
                            Interlocked.Or(ref word, entityBit);
                        }
                        else
                        {
                            Interlocked.And(ref word, ~entityBit);
                        }
                    }

                    // Build ClusterEntityRecord (19 bytes base + 4 bytes per Versioned slot). Born pending; the H1 born fold is PUBLISH's, with the stamp,
                    // before the occupancy bit (BIND-04, ArchetypeClusterState.PublishReservedSlot): a fresh cluster stays unestablished until then.
                    ClusterEntityRecordAccessor.InitializeRecord(recordPtr, ctx.Meta.VersionedSlotCount);
                    ref var clusterHeader = ref ClusterEntityRecordAccessor.GetHeader(recordPtr);
                    clusterHeader.BornTSN = EntityRecordHeader.PendingBorn(TSN);
                    clusterHeader.EnabledBits = enabledBits;
                    ClusterEntityRecordAccessor.SetClusterChunkId(recordPtr, clusterChunkId);
                    ClusterEntityRecordAccessor.SetSlotIndex(recordPtr, (byte)slotIdx);

                    // Store compRevFirstChunkId for each Versioned slot
                    if (ctx.Meta.VersionedSlotMask != 0)
                    {
                        for (int slot = 0; slot < ctx.ComponentCount; slot++)
                        {
                            int vi = layout.SlotToVersionedIndex[slot];
                            if (vi >= 0)
                            {
                                ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordPtr, vi, entry.Rev[slot]);
                            }
                        }
                    }

                    // Insert ClusterEntityRecord into EntityMap
                    ctx.EngineState.EntityMap.InsertNew(entry.Id.EntityKey, recordPtr, ref ctx.MapAccessor, _changeSet);

                    // The bounds now, the B+Tree entries at PUBLISH with the record's stamp and the occupancy bit (PublishSpawns).
                    PlaceClusterSpawnSpatial(ref ctx, in entry, clusterChunkId, slotIdx, clusterBase, clusterTransientBase, trSlots, spawnSpatialCoords);
                }
                else
                {
                    // ═══════════════════════════════════════════════════════════════
                    // Legacy path: build location array from SpawnEntry
                    // ═══════════════════════════════════════════════════════════════
                    // A persisted EntityRecord location is a chunk id, so ONLY a Versioned slot has anything to put here since #839 — a SingleVersion or
                    // Transient payload lives in the spawn arena, whose handles are transaction-scoped and must never reach the file. This whole branch is
                    // dead in practice (every archetype has been cluster-eligible since #666); the guard states the constraint rather than trusting it.
                    var locDest = (int*)(recordPtr + EntityRecordAccessor.HeaderSize);
                    for (int slot = 0; slot < ctx.ComponentCount; slot++)
                    {
                        var isVersionedSlot = (ctx.VersionedMask & (1 << slot)) != 0;
                        CheckConfig.Require(CheckConfig.Enabled, isVersionedSlot || entry.Stage[slot] == 0,
                            $"Legacy flat spawn: slot {slot} is staged in the spawn arena, so it has no chunk id to persist (#839).");
                        locDest[slot] = isVersionedSlot ? entry.Rev[slot] : entry.VerLoc[slot];
                    }

                    // Insert into EntityMap — skip duplicate check (EntityKey is freshly generated, guaranteed unique)
                    PlacedSpawn(spawnOrder != null ? spawnOrder[spawnIndex] : spawnIndex, -1, 0);
                    ctx.EngineState.EntityMap.InsertNew(entry.Id.EntityKey, recordPtr, ref ctx.MapAccessor, _changeSet);
                }

                // Insert shared ComponentTable secondary indexes — ONLY for non-cluster (legacy) entities.
                // Cluster entities use per-archetype B+Trees (inserted in the cluster path above).
                // Accessors are hoisted: created once when archetype changes (alongside mapAccessor),
                // reused across all entities of the same archetype.
                // Insert Transient secondary indexes (hoisted accessors, same pattern as SV).
                // Cluster archetypes are always all-SV, so trSlotCount == 0. Guard for safety.
                // Insert SV spatial indexes (Transient excluded by schema validation).
                // Must iterate all component slots (not just svSlots) because spatial-only components
                // without B+Tree indexes are not in the svSlots array.
                // Skip for cluster entities — per-archetype R-Tree is used instead.

                // No epoch refresh between placements, unlike the publish and the rollback (#831). The pin is what stops an oversized commit HERE,
                // before its append: it keeps every page the placement touched, so the commit meets the wall as soon as the publish would, and is refused
                // (TransactionExceedsPageCacheException) with nothing to undo but itself. Moved on, it placed 50 000 spawns into 8 MiB, appended, and its
                // publish — which keeps every mark it takes, ~380 pages more than PREPARE's 542 — stopped the engine (CommitPublishIncompleteException).
            }
        }
        finally
        {
            DisposeSpawnAccessors(ref ctx);
            if (spawnOrder != null)
            {
                ArrayPool<int>.Shared.Return(spawnOrder);
            }
        }
    }

    private void PlacedSpawn(int index, int clusterChunkId, int slotIndex)
    {
        ref var e = ref CollectionsMarshal.AsSpan(_spawnedEntities)[index];
        e.PlacedClusterChunkId = clusterChunkId;
        e.PlacedSlot = (byte)slotIndex;
        e.Placed = true;
    }

    /// <summary>
    /// PUBLISH half of a spawn (VIS-08): for every spawn <see cref="PlaceSpawns"/> placed, the record's born takes the commit stamp, then the cluster's
    /// born watermark folds it and the occupancy bit is set (<see cref="ArchetypeClusterState.PublishReservedSlot"/>), then the per-archetype B+Tree entries
    /// — a structure of HEAD values (design 05's non-goal, #674), maintained at publish as before (AP-07: an abort leaves no key no commit made). Then the
    /// spawn's index lock is released: another commit that saw the entity through the card waited for it before touching its index entries or its slot —
    /// and before taking the entity's chain lock, which this commit's component publish, run before this one, may need.
    /// </summary>
    private void PublishSpawns()
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        var ctx = new SpawnContext();
        Span<int> svSlots = stackalloc int[16];
        Span<int> svIdxAccessorBase = stackalloc int[16];
        Span<int> trSlots = stackalloc int[16];
        Span<int> trIdxAccessorBase = stackalloc int[16];
        var stamp = PublishStamp;
        var completed = false;

        try
        {
            var spawns = CollectionsMarshal.AsSpan(_spawnedEntities);
            for (var i = 0; i < spawns.Length; i++)
            {
                ref var entry = ref spawns[i];
                if (!entry.Placed)
                {
                    continue;
                }

                RefreshBetweenCommitSteps();
                if (!ctx.HasMapAccessor || entry.Id.ArchetypeId != ctx.LastArchId)
                {
                    DisposeSpawnAccessors(ref ctx);
                    SetupSpawnAccessors(ref ctx, entry.Id.ArchetypeId, svSlots, svIdxAccessorBase, trSlots, trIdxAccessorBase);
                }

                // The record first, in place under its bucket's write latch: a reader that finds the occupancy bit reads a stamped born.
                var stamper = new BornStamper { Stamp = stamp };
                if (!ctx.EngineState.EntityMap.TryUpdateInPlace(entry.Id.EntityKey, ref stamper, ref ctx.MapAccessor))
                {
                    System.Diagnostics.Debug.Fail("VIS-08: a placed spawn's EntityMap record vanished before its publish");
                }

                if (ctx.UseCluster && entry.PlacedClusterChunkId >= 0)
                {
                    var clusterChunkId = entry.PlacedClusterChunkId;
                    int slotIdx = entry.PlacedSlot;
                    byte* clusterBase;
                    byte* clusterTransientBase = null;
                    if (ctx.ClusterState.ClusterSegment != null)
                    {
                        clusterBase = ctx.ClusterAccessor.GetChunkAddress(clusterChunkId, true);
                        if (ctx.HasClusterTransientAccessor)
                        {
                            clusterTransientBase = ctx.ClusterTransientAccessor.GetChunkAddress(clusterChunkId, true);
                        }
                    }
                    else
                    {
                        clusterBase = ctx.ClusterTransientAccessor.GetChunkAddress(clusterChunkId, true);
                    }

                    ctx.ClusterState.PublishReservedSlot(clusterBase, clusterChunkId, slotIdx, stamp);
                    _dbe.SpawnBeforeIndexesProbe?.Invoke(entry.Id);
                    PublishClusterSpawnIndexes(ref ctx, in entry, clusterChunkId, slotIdx, clusterBase, clusterTransientBase);
                    ctx.ClusterState.UnlockSlotIndexes(clusterChunkId, slotIdx);
                }

                entry.Placed = false;
            }

            completed = true;
        }
        finally
        {
            DisposeSpawnAccessors(ref ctx);
            if (!completed)
            {
                // A publish that failed past the commit point stops the engine (AP-03); its waiters must not wait for it for good.
                ReleasePendingSpawnLocks();
            }
        }
    }

    /// <summary>A revision chain's exclusive lock a destroy holds across its index removal (<see cref="LockUntombstonedChains"/>).</summary>
    private sealed class ChainHold
    {
        public ChunkAccessor<PersistentStore> Accessor;
        public int FirstChunkId;
        public PagedMMF.CaptureTicket Ticket;
    }

    /// <summary>
    /// The exclusive locks of the chains of <paramref name="entityId"/>'s indexed Versioned components that this commit did not tombstone — present in the
    /// record at the publish, absent at the destroy's snapshot: a supply's, published since (VIS-09). A tombstoned chain needs none: its tombstone's publish
    /// took it, and an update publishing after that moves nothing over the tombstone. Null when there is none, the common case. Taken in component order,
    /// unbounded, as the publish's own chain locks (AP-03), holding no slot lock. Released by <see cref="ReleaseChainHolds"/>.
    /// </summary>
    private List<ChainHold> LockUntombstonedChains(ArchetypeMetadata meta, ArchetypeEngineState es, byte* record, EntityId entityId)
    {
        var layout = meta.ClusterLayout;
        var ixSlots = es.ClusterState?.IndexSlots;
        if (ixSlots == null || layout?.SlotToVersionedIndex == null || meta.VersionedSlotMask == 0)
        {
            return null;
        }

        List<ChainHold> holds = null;
        var pk = (long)entityId.RawValue;
        for (var s = 0; s < ixSlots.Length; s++)
        {
            var slot = ixSlots[s].Slot;
            var vi = layout.SlotToVersionedIndex[slot];
            if (vi < 0)
            {
                continue;
            }

            var root = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, vi);
            if (root == 0 || (_componentInfos.TryGetValue(meta._slotToComponentType[slot], out var info) && info.SingleCache.TryGetValue(pk, out var cri)
                              && (cri.Operations & ComponentInfo.OperationType.Deleted) != 0 && cri.CompRevTableFirstChunkId == root))
            {
                continue;
            }

            var hold = new ChainHold { Accessor = es.SlotToComponentTable[slot].CompRevTableSegment.CreateChunkAccessor(_changeSet), FirstChunkId = root };
            ref var header = ref hold.Accessor.GetChunk<CompRevStorageHeader>(root, true);
            hold.Ticket = ComponentRevisionManager.ChainLockTicket(ref hold.Accessor, ref header);
            _dbe.DestroyChainLockProbe?.Invoke();
            var unbounded = new WaitContext(Deadline.Infinite, default);
            header.Control.EnterExclusiveAccess(ref unbounded);
            (holds ??= []).Add(hold);
        }

        return holds;
    }

    /// <summary>The chain locks <see cref="LockUntombstonedChains"/> took, released in reverse order, their accessors disposed.</summary>
    private static void ReleaseChainHolds(List<ChainHold> holds)
    {
        if (holds == null)
        {
            return;
        }

        for (var i = holds.Count - 1; i >= 0; i--)
        {
            var hold = holds[i];
            ref var header = ref hold.Accessor.GetChunk<CompRevStorageHeader>(hold.FirstChunkId, true);
            header.Control.ExitExclusiveAccess();
            ComponentRevisionManager.EndChainLockHold(ref hold.Accessor, in hold.Ticket);
            hold.Accessor.Dispose();
        }
    }

    /// <summary>The index locks of the spawns still placed, released: a PUBLISH that failed part-way, whose waiters must not wait for good.</summary>
    private void ReleasePendingSpawnLocks()
    {
        foreach (ref var entry in CollectionsMarshal.AsSpan(_spawnedEntities))
        {
            if (entry.Placed && entry.PlacedClusterChunkId >= 0)
            {
                _dbe._stateByRouting[entry.Id.ArchetypeId]?.ClusterState?.UnlockSlotIndexes(entry.PlacedClusterChunkId, entry.PlacedSlot);
            }
        }
    }

    /// <summary>A placed spawn's publish: its record's pending born replaced by the commit stamp (VIS-08).</summary>
    private struct BornStamper : IRawValueUpdater
    {
        public long Stamp;

        public void Update(byte* valueBytes) => EntityRecordAccessor.GetHeader(valueBytes).BornTSN = Stamp;
    }

    /// <summary>The per-archetype B+Tree entries of one published cluster spawn.</summary>
    private void PublishClusterSpawnIndexes(ref SpawnContext ctx, in SpawnEntry entry, int clusterChunkId, int slotIdx, byte* clusterBase,
        byte* clusterTransientBase)
    {
        var layout = ctx.ClusterState.Layout;
        // Note: cluster pages are marked dirty at page level (GetChunkAddress(dirty:true) above).
        // Checkpoint persists them. We do NOT set ClusterDirtyBitmap here — that bitmap tracks write mutations for change-filtered dispatch,
        // same as per-ComponentTable DirtyBitmap (which is also not set during PlaceSpawns for non-cluster SV entities).

        // Insert per-archetype B+Tree entries for cluster entity, in both index homes (#655). The elementId tail always lives on the PRIMARY
        // (cluster) base even for a Transient slot, whose component bytes live in the Transient segment — see ProcessClusterShadowEntries.
        // Measured in Morton order and in arrival order (step 15 review, 4 K spawns of an archetype with one AllowMultiple index, warm,
        // Release): 16.6–18.6 ms inline against 18.4–20.0 ms deferred and replayed in key order — the tree does not care, and the
        // ordering itself costs nothing measurable warm (unsorted 17–18 ms). The matrix's "+90 % spawn" is a one-shot cold number paying
        // the ordering path's JIT. So the inserts stay where they are.
        {
            int clusterLocation = clusterChunkId * 64 + slotIdx;
            // The bits the spawn publishes with, as PlaceSpawns records them: an enable or disable staged on the pending spawn wins.
            var enabledBits = entry.EnabledBits;
            if (_pendingEnableDisable != null && _pendingEnableDisable.TryGetValue(entry.Id, out var pendingBits))
            {
                enabledBits = pendingBits;
            }

            InsertClusterIndexEntries(ref ctx, ctx.ClusterState.IndexSlots, clusterBase, clusterBase, layout, clusterChunkId, slotIdx,
                clusterLocation, entry.Id, ref ctx.ClusterIdxAccessor, ref ctx.ClusterIdxAccessorS64, ctx.ClusterState.IndexSegmentString64, enabledBits);

            if (ctx.ClusterState.TransientIndexSlots != null)
            {
                // Pure-Transient archetypes have no separate Transient base — clusterBase already IS the TransientStore chunk (see the dstBase
                // selection above), so fall back to it rather than skipping the insert.
                byte* transientBase = clusterTransientBase != null ? clusterTransientBase : clusterBase;
                InsertClusterIndexEntries(ref ctx, ctx.ClusterState.TransientIndexSlots, transientBase, clusterBase, layout, clusterChunkId,
                    slotIdx, clusterLocation, entry.Id, ref ctx.ClusterTransientIdxAccessor, ref ctx.ClusterTransientIdxAccessorS64,
                    ctx.ClusterState.TransientIndexSegmentString64, enabledBits);
            }
        }
    }

    /// <summary>
    /// The bounds of a cluster spawn's placement, in PREPARE with the claim: the zone maps, the cluster's AABB and the per-cell index widened over it. All
    /// are bounds, conservative upward — a pending or aborted spawn only widens them — and readers rely on them: a snapshot scan prunes by the zone maps, and
    /// the next spawn of the same commit chooses its cluster by how much each box grows (least enlargement, the growth cap). Also the Transient payloads' PK
    /// stamp.
    /// </summary>
    private void PlaceClusterSpawnSpatial(ref SpawnContext ctx, in SpawnEntry entry, int clusterChunkId, int slotIdx, byte* clusterBase,
        byte* clusterTransientBase, scoped Span<int> trSlots, scoped Span<double> spawnSpatialCoords)
    {
        var layout = ctx.ClusterState.Layout;
        WidenClusterZoneMaps(ctx.ClusterState.IndexSlots, clusterBase, layout, clusterChunkId, slotIdx);
        WidenClusterZoneMaps(ctx.ClusterState.TransientIndexSlots, clusterTransientBase != null ? clusterTransientBase : clusterBase, layout, clusterChunkId,
            slotIdx);
        // Maintain the per-cell cluster AABB index for cluster spatial archetypes (issue #230 Phase 3 Option B).
        // The legacy per-archetype R-Tree + back-pointer insert is gone — the per-cell index is now the single source of truth. Populates
        // both DynamicIndex and StaticIndex depending on the archetype's SpatialMode (see AddClusterToPerCellIndex for the split).
        if (ctx.ClusterState.SpatialSlot.HasSpatialIndex)
        {
            ref var ss = ref ctx.ClusterState.SpatialSlot;
            int spatialCompSize = layout.ComponentSize(ss.Slot);
            byte* spatialFieldPtr = clusterBase + layout.ComponentOffset(ss.Slot) + slotIdx * spatialCompSize + ss.FieldOffset;

            if (ctx.ClusterState.ClusterCellMap != null)
            {
                if (SpatialMaintainer.ReadAndValidateBoundsFromPtr(spatialFieldPtr, ss.FieldInfo, spawnSpatialCoords))
                {
                    ctx.ClusterState.EnsureClusterAabbsCapacity(clusterChunkId + 1);
                    ctx.ClusterState.EnsureClusterSpatialIndexSlotCapacity(clusterChunkId + 1);

                    // Latched when a cell tree could be in flight (#940): PromoteCellHalf transiently retires every back-pointer, and a plain
                    // read crossing that window answers "not indexed" for a cluster that is — which resets its box to Empty below, wiping
                    // concurrent spawners' widening, and then re-adds it to a tree that already holds it.
                    ctx.ClusterState.SpawnIndexReadProbe?.Invoke();
                    bool wasInIndex = ctx.ClusterState.IsClusterIndexed(clusterChunkId);
                    // Tier-dispatched union: 2D fields wrote [minX, minY, maxX, maxY] into the first 4 slots; 3D fields wrote the full
                    // [minX, minY, minZ, maxX, maxY, maxZ] layout. Prior to issue #230 Phase 3 this site was hardcoded to the 2D layout
                    // regardless of tier — a latent bug that was masked because 3D archetypes only reach this hook when ConfigureSpatialGrid
                    // was called, and the trigger/interest tests (the only 3D cluster callers) didn't call it.
                    // Category mask comes from the archetype-level [SpatialIndex(Category=)] attribute (issue #230 Phase 3). It's the same value
                    // for every entity in the archetype, so the cluster-level OR trivially converges to the archetype value. Defaults to
                    // uint.MaxValue when the attribute doesn't set Category, matching pre-Phase-3 behavior.
                    uint archetypeCategory = ss.FieldInfo.Category;
                    bool is3D = ss.FieldInfo.FieldType.Is3D();

                    // C15 (#872 step 9): the union is built in the CELL's frame, so the cell has to be resolved before it rather than after.
                    // A cluster with no cell has no frame to be expressed in — its AABB is left at Empty rather than filled with world-space
                    // values that would be indistinguishable from cell-relative ones on read.
                    int cellKey = ctx.ClusterState.ClusterCellMap[clusterChunkId];
                    double cellOriginX = 0d, cellOriginY = 0d, cellOriginZ = 0d;
                    if (cellKey >= 0)
                    {
                        // The realm the cluster is in (Realms SP-3): its grid is the frame of this cell key.
                        var clusterGrid = ctx.ClusterState.SpatialOfCluster(clusterChunkId).Grid;
                        clusterGrid.CellOrigin(cellKey, out cellOriginX, out cellOriginY, out cellOriginZ);

                        // Before the index widen below, which is what makes the entity queryable: every query must already reach as far past
                        // this cell as the entity does (SQ-01). Between fences a spawn is the only thing that can push that reach out.
                        ctx.ClusterState.RaiseClusterReachForSpawn(clusterGrid, cellKey, cellOriginX, cellOriginY, cellOriginZ,
                            spawnSpatialCoords, is3D);
                    }

                    // Stamped: a concurrent grow of ClusterAabbs copies the array, and a widen into the old one after the copy read this entry
                    // would be lost — see ArchetypeClusterState._clusterAabbsGrowth. The reset and the widen are redone in the new array.
                    int aabbsStamp;
                    do
                    {
                        aabbsStamp = ctx.ClusterState.BeginClusterAabbsWrite();
                        ref var clusterAabb = ref Volatile.Read(ref ctx.ClusterState.ClusterAabbs)[clusterChunkId];
                        if (!wasInIndex)
                        {
                            // A cell-claimed cluster is indexed, with an empty box, by its allocation site under the latch (step 15 review), so
                            // this branch is for a cluster that reached here without one. The reset then has no concurrent widener to wipe.
                            clusterAabb = ClusterSpatialAabb.Empty;
                        }

                        if (cellKey < 0)
                        {
                            // No cell, no frame: the entry stays at Empty. Nothing to widen, but the reset above is still checked below.
                        }
                        else if (is3D)
                        {
                            ClusterSpatialAabb.WidenCas3F(ref clusterAabb,
                                ClusterSpatialAabb.ToCellRelativeMin(spawnSpatialCoords[0], cellOriginX),
                                ClusterSpatialAabb.ToCellRelativeMin(spawnSpatialCoords[1], cellOriginY),
                                ClusterSpatialAabb.ToCellRelativeMin(spawnSpatialCoords[2], cellOriginZ),
                                ClusterSpatialAabb.ToCellRelativeMax(spawnSpatialCoords[3], cellOriginX),
                                ClusterSpatialAabb.ToCellRelativeMax(spawnSpatialCoords[4], cellOriginY),
                                ClusterSpatialAabb.ToCellRelativeMax(spawnSpatialCoords[5], cellOriginZ),
                                archetypeCategory);
                        }
                        else
                        {
                            ClusterSpatialAabb.WidenCas2F(ref clusterAabb,
                                ClusterSpatialAabb.ToCellRelativeMin(spawnSpatialCoords[0], cellOriginX),
                                ClusterSpatialAabb.ToCellRelativeMin(spawnSpatialCoords[1], cellOriginY),
                                ClusterSpatialAabb.ToCellRelativeMax(spawnSpatialCoords[2], cellOriginX),
                                ClusterSpatialAabb.ToCellRelativeMax(spawnSpatialCoords[3], cellOriginY),
                                archetypeCategory);
                        }
                    }
                    while (!ctx.ClusterState.ClusterAabbsWriteLanded(aabbsStamp));

                    if (cellKey >= 0)
                    {
                        // Read back from the LIVE array: a grow after the loop copied the widen, and the index must never hold a box its
                        // ClusterAabbs entry does not — RefreshClusterReach computes the reach from the entries (SQ-01).
                        ref var liveAabb = ref Volatile.Read(ref ctx.ClusterState.ClusterAabbs)[clusterChunkId];
                        if (!wasInIndex)
                        {
                            ctx.ClusterState.AddClusterToPerCellIndex(clusterChunkId, cellKey, liveAabb);
                        }
                        else
                        {
                            // Routed rather than dereferenced: the mode split (Static vs Dynamic) is only half of it since #872 step 9 — a
                            // promoted cell has no linear index at all, so reaching for one is a null deref above the threshold. WIDEN, not
                            // update: this runs on a user thread beside other spawns into the same cluster and a latched grow of the same
                            // cell's index, and only the CAS form survives both (step 15 review).
                            ctx.ClusterState.WidenClusterInPerCellIndex(clusterChunkId, cellKey, in liveAabb);
                        }
                    }
                }
            }
        }

        // The per-ComponentTable TransientIndex insert that used to follow is gone (#629). Its own comment said Transient-indexed archetypes were
        // "excluded from cluster eligibility" and that the block only covered "the theoretical case where eligibility rules are relaxed in the
        // future" — the flip relaxed exactly that, so the block had quietly become live and was double-indexing: the per-archetype transient tree
        // is maintained above by InsertClusterIndexEntries(ClusterState.TransientIndexSlots, …), and that is the one reads consult.
        //
        // The entity-PK write into the chunk overhead stays: it is a layout invariant of an indexed non-Versioned component, not part of the tree.
        if (ctx.TrSlotCount > 0 && ctx.TrCompAccessors != null)
        {
            for (int si = 0; si < ctx.TrSlotCount; si++)
            {
                int trSlot = trSlots[si];
                var table = ctx.EngineState.SlotToComponentTable[trSlot];
                int srcStage = entry.Stage[trSlot];
                if (srcStage == 0 || table.Definition.EntityPKOverheadSize == 0)
                {
                    continue;
                }

                // #839: the staged payload now lives in the spawn arena, so the PK lands there rather than in a Transient content chunk. This
                // stamp keeps the staged bytes a well-formed [PK][value] payload, which is what the comment above means by a layout invariant.
                // NOTE for review: with the chunk gone, the arena slot is discarded at commit and no reader outlives it, so this write may now be
                // dead. It is kept because proving that negative is a separate exercise from relocating the payload, and it costs one store.
                *(long*)SpawnArena.Resolve(srcStage) = (long)entry.Id.RawValue;
            }
        }
    }

    /// <summary>
    /// Gives back what <see cref="PlaceSpawns"/> placed for a commit that ended before its commit point (AP-07): each record removed from the EntityMap, each
    /// reserved slot released (<see cref="ArchetypeClusterState.UnreserveSlot"/>). Idempotent: a spawn it handled is no longer placed.
    /// </summary>
    private void UnplaceSpawns()
    {
        if (_spawnedEntities == null || _spawnedEntities.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        var ctx = new SpawnContext();
        Span<int> svSlots = stackalloc int[16];
        Span<int> svIdxAccessorBase = stackalloc int[16];
        Span<int> trSlots = stackalloc int[16];
        Span<int> trIdxAccessorBase = stackalloc int[16];
        try
        {
            var spawns = CollectionsMarshal.AsSpan(_spawnedEntities);
            for (var i = 0; i < spawns.Length; i++)
            {
                ref var entry = ref spawns[i];
                if (!entry.Placed)
                {
                    continue;
                }

                if (!ctx.HasMapAccessor || entry.Id.ArchetypeId != ctx.LastArchId)
                {
                    DisposeSpawnAccessors(ref ctx);
                    SetupSpawnAccessors(ref ctx, entry.Id.ArchetypeId, svSlots, svIdxAccessorBase, trSlots, trIdxAccessorBase);
                }

                ctx.EngineState.EntityMap.Remove(entry.Id.EntityKey, ref ctx.MapAccessor, _changeSet);
                if (ctx.UseCluster && entry.PlacedClusterChunkId >= 0)
                {
                    var clusterChunkId = entry.PlacedClusterChunkId;
                    var clusterBase = ctx.ClusterState.ClusterSegment != null
                        ? ctx.ClusterAccessor.GetChunkAddress(clusterChunkId, true)
                        : ctx.ClusterTransientAccessor.GetChunkAddress(clusterChunkId, true);
                    // No card was registered, so no commit saw the entity: the lock only has to be released before the slot can be claimed again.
                    ctx.ClusterState.UnlockSlotIndexes(clusterChunkId, entry.PlacedSlot);
                    ctx.ClusterState.UnreserveSlot(clusterBase, clusterChunkId, entry.PlacedSlot, ctx.ClusterState.SpatialOfCluster(clusterChunkId).Grid);
                }

                entry.Placed = false;
                RefreshBetweenCommitSteps();
            }
        }
        finally
        {
            DisposeSpawnAccessors(ref ctx);
        }
    }

    /// <summary>
    /// The order <see cref="PlaceSpawns"/> visits <c>_spawnedEntities</c> in: grouped by archetype, then by destination cell, then along the
    /// intra-cell Morton curve — so first fit fills each cluster with spatial neighbours and a random-order bulk load is born packed rather than at
    /// the full extent of its cell. Rented from <see cref="ArrayPool{T}"/>; the caller returns it.
    /// </summary>
    /// <remarks>
    /// <para><b>Accessor-free, on purpose.</b> The main loop rents chunk accessors per archetype; doing that twice per batch would double the setup
    /// cost of every bulk spawn. The pre-pass reads only what it can reach without one: the archetype's spatial slot, the component's overhead and
    /// field offset, and the staged bytes in the spawn arena. A <c>Versioned</c> spatial component stages through a cluster accessor instead, so
    /// its entities keep their archetype-and-cell grouping and skip the Morton term — still better than arrival order, and never wrong.</para>
    /// <para><b>Cells are resolved with create.</b> The main loop resolves the same key with create a moment later, so nothing is materialised that
    /// would not have been. Grouping by archetype first is a free win in its own right: every archetype switch in the main loop re-rents accessors.</para>
    /// </remarks>
    private int[] BuildSpatialSpawnOrder()
    {
        var count = _spawnedEntities.Count;
        var order = ArrayPool<int>.Shared.Rent(count);
        var primary = ArrayPool<ulong>.Shared.Rent(count);
        var morton = ArrayPool<ulong>.Shared.Rent(count);
        var runKeys = ArrayPool<ulong>.Shared.Rent(count);
        try
        {
            var realm0Grid = _dbe.Realm0Grid;
            var lastArchId = -1;
            var spatialSlot = -1;
            var componentOverhead = 0;
            var fieldOffset = 0;
            var realmKeyOffset = -1;
            var fieldType = SpatialFieldType.AABB2F;
            var orderRealm = -1;
            SpatialGrid orderGrid = null;
            ArchetypeClusterState orderClusterState = null;
            var realmKeySlot = 0;
            var realmKeyOverhead = 0;

            for (var i = 0; i < count; i++)
            {
                var entry = _spawnedEntities[i];
                int archId = entry.Id.ArchetypeId;
                if (archId != lastArchId)
                {
                    lastArchId = archId;
                    spatialSlot = -1;
                    var meta = _dbe.GetMetaByRouting((ushort)archId);
                    var engineState = meta != null && meta.IsClusterEligible ? _dbe._archetypeStates[meta.ArchetypeId] : null;
                    ArchetypeClusterState clusterState = engineState?.ClusterState;
                    orderClusterState = clusterState;
                    orderRealm = -1;   // validity is per archetype: a realm cached for another archetype decides nothing here
                    orderGrid = null;
                    if (clusterState != null && clusterState.SpatialSlot.HasSpatialIndex && (realm0Grid != null || clusterState.SpatialSlot.HasRealmKey))
                    {
                        ref readonly var ss = ref clusterState.SpatialSlot;
                        var table = engineState.SlotToComponentTable[ss.Slot];
                        if (table.StorageMode != StorageMode.Versioned)
                        {
                            spatialSlot = ss.Slot;
                            componentOverhead = table.ComponentOverhead;
                            fieldOffset = ss.FieldOffset;
                            realmKeyOffset = ss.RealmKeyOffset;
                            realmKeySlot = ss.RealmKeySlot;
                            realmKeyOverhead = ss.HasRealmKey ? engineState.SlotToComponentTable[ss.RealmKeySlot].ComponentOverhead : 0;
                            fieldType = ss.FieldInfo.FieldType;
                        }
                    }
                }

                order[i] = i;
                var cellKey = 0;
                ushort realm = 0;
                var mortonKey = 0UL;
                if (spatialSlot >= 0)
                {
                    var stage = entry.Stage[spatialSlot];
                    if (stage != 0)
                    {
                        var row = SpawnArena.Resolve(stage) + componentOverhead;
                        // Each entity in its own realm's grid: two realms share cell keys, and a cell size is per realm (Realms C1).
                        var grid = realm0Grid;
                        if (realmKeyOffset >= 0)
                        {
                            var keyStage = entry.Stage[realmKeySlot];
                            realm = keyStage != 0 ? *(ushort*)(SpawnArena.Resolve(keyStage) + realmKeyOverhead + realmKeyOffset) : (ushort)0;
                            if (realm != orderRealm)
                            {
                                // An invalid staged key sorts where PlaceSpawns will place it: in the realm Spawn validated.
                                // The RESOLVED state (meta.ArchetypeId), not _archetypeStates[archId]: archId is the routing id, and indexing by it made this
                                // check always false — every spawn sorted by the fallback (review #4).
                                var valid = orderClusterState?.IsValidRealmForEntity(realm) ?? false;
                                orderRealm = valid ? realm : -1;
                                orderGrid = valid ? _dbe.RealmTable.Get(realm).Grid : null;
                            }

                            if (orderGrid == null)
                            {
                                realm = orderClusterState!.SpawnFallbackRealm(entry.SpawnRealm);
                                grid = _dbe.RealmTable.Get(realm).Grid;
                            }
                            else
                            {
                                grid = orderGrid;
                            }
                        }

                        SpatialGrid.ReadSpatialCenter3D(row + fieldOffset, fieldType, out var x, out var y, out var z);
                        cellKey = grid!.WorldToCellKey(x, y, z);
                        grid.CellOrigin(cellKey, out var ox, out var oy, out var oz);
                        mortonKey = ArchetypeClusterState.EncodeIntraCellMorton((float)(x - ox), (float)(y - oy), (float)(z - oz),
                            (float)grid.Config.InverseCellSize);
                    }
                }

                // (archetype, realm, cell): runs never mix two realms' cells, which share keys.
                primary[i] = ((ulong)(ushort)archId << 48) | ((ulong)realm << 32) | (uint)cellKey;
                morton[i] = mortonKey;
            }

            // Two-level: archetype-and-cell first, then the Morton curve inside each run. Both sorts carry the index array along.
            Array.Sort(primary, order, 0, count);
            var runStart = 0;
            while (runStart < count)
            {
                var runEnd = runStart + 1;
                while (runEnd < count && primary[runEnd] == primary[runStart])
                {
                    runEnd++;
                }

                if (runEnd - runStart > 1)
                {
                    // The Morton keys are in ORIGINAL index order; gather the run's keys through the permuted indices into a scratch, then sort the
                    // scratch with the run's slice of the order array riding along.
                    var span = order.AsSpan(runStart, runEnd - runStart);
                    var keys = runKeys.AsSpan(0, span.Length);
                    for (var k = 0; k < span.Length; k++)
                    {
                        keys[k] = morton[span[k]];
                    }

                    keys.Sort(span);
                }

                runStart = runEnd;
            }

            return order;
        }
        catch
        {
            ArrayPool<int>.Shared.Return(order);   // the caller never sees it, so the caller cannot return it
            throw;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(primary);
            ArrayPool<ulong>.Shared.Return(morton);
            ArrayPool<ulong>.Shared.Return(runKeys);
        }
    }

    /// <summary>
    /// Dispose all hoisted accessors in the spawn context. Called on archetype change and in the finally block.
    /// </summary>
    private void DisposeSpawnAccessors(ref SpawnContext ctx)
    {
        if (!ctx.HasMapAccessor)
        {
            return;
        }
        ctx.MapAccessor.Dispose();
        if (ctx.HasClusterAccessor)
        {
            ctx.ClusterAccessor.Dispose();
            ctx.HasClusterAccessor = false;
        }
        if (ctx.HasClusterTransientAccessor)
        {
            ctx.ClusterTransientAccessor.Dispose();
            ctx.HasClusterTransientAccessor = false;
        }
        for (int ci = 0; ci < ctx.ClusterSrcAccessorCount; ci++)
        {
            var table = ctx.EngineState.SlotToComponentTable[ci];
            if (table.StorageMode == StorageMode.Transient)
            {
                if (ctx.ClusterTransientSrcAccessors != null)
                {
                    ctx.ClusterTransientSrcAccessors[ci].Dispose();
                }
            }
            else
            {
                ctx.ClusterSrcAccessors[ci].Dispose();
            }
        }
        ctx.ClusterSrcAccessorCount = 0;
        if (ctx.HasClusterTransientIdxAccessorS64)
        {
            ctx.ClusterTransientIdxAccessorS64.Dispose();
            ctx.HasClusterTransientIdxAccessorS64 = false;
        }

        if (ctx.HasClusterTransientIdxAccessor)
        {
            ctx.ClusterTransientIdxAccessor.Dispose();
            ctx.HasClusterTransientIdxAccessor = false;
        }

        if (ctx.HasClusterIdxAccessorS64)
        {
            ctx.ClusterIdxAccessorS64.Dispose();
            ctx.HasClusterIdxAccessorS64 = false;
        }

        if (ctx.HasClusterIdxAccessor)
        {
            ctx.ClusterIdxAccessor.Dispose();
            ctx.HasClusterIdxAccessor = false;
        }
        for (int si = 0; si < ctx.SvSlotCount; si++)
        {
            ctx.SvCompAccessors[si].Dispose();
        }
        for (int ai = 0; ai < ctx.SvIdxAccessorTotal; ai++)
        {
            ctx.SvIdxAccessors[ai].Dispose();
        }
        for (int si = 0; si < ctx.TrSlotCount; si++)
        {
            ctx.TrCompAccessors[si].Dispose();
        }
        for (int ai = 0; ai < ctx.TrIdxAccessorTotal; ai++)
        {
            ctx.TrIdxAccessors[ai].Dispose();
        }
        ctx.HasMapAccessor = false;
    }

    /// <summary>
    /// Set up all hoisted accessors for a new archetype: metadata caching, cluster accessors, SV/Transient index accessors.
    /// </summary>
    private void SetupSpawnAccessors(ref SpawnContext ctx, ushort archetypeId, scoped Span<int> svSlots, scoped Span<int> svIdxAccessorBase, 
        scoped Span<int> trSlots, scoped Span<int> trIdxAccessorBase)
    {
        // Cache archetype metadata + compute versioned slot mask
        ctx.Meta = _dbe.GetMetaByRouting(archetypeId);
        ctx.EngineState = _dbe._archetypeStates[ctx.Meta.ArchetypeId];
        ctx.ComponentCount = ctx.Meta.ComponentCount;
        ctx.VersionedMask = 0;
        for (int slot = 0; slot < ctx.ComponentCount; slot++)
        {
            if (ctx.EngineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
            {
                ctx.VersionedMask |= (ushort)(1 << slot);
            }
        }

        ctx.MapAccessor = ctx.EngineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
        ctx.LastArchId = archetypeId;
        ctx.HasMapAccessor = true;

        // Set up cluster accessors if this archetype uses cluster storage
        ctx.UseCluster = ctx.Meta.IsClusterEligible && ctx.EngineState.ClusterState != null;
        if (ctx.UseCluster)
        {
            ctx.ClusterState = ctx.EngineState.ClusterState;

            // PersistentStore cluster accessor (null for pure-Transient)
            if (ctx.ClusterState?.ClusterSegment != null)
            {
                ctx.ClusterAccessor = ctx.ClusterState.ClusterSegment.CreateChunkAccessor(_changeSet);
                ctx.HasClusterAccessor = true;
            }

            // TransientStore cluster accessor (for archetypes with Transient components)
            if (ctx.ClusterState!.TransientSegment != null)
            {
                ctx.ClusterTransientAccessor = ctx.ClusterState.TransientSegment.CreateChunkAccessor();
                ctx.HasClusterTransientAccessor = true;
            }

            // Create per-component accessors for reading from per-component spawn chunks.
            // Transient slots use TransientComponentSegment; SV/V use ComponentSegment.
            ctx.ClusterSrcAccessorCount = ctx.ComponentCount;
            if (ctx.ClusterSrcAccessors == null || ctx.ClusterSrcAccessors.Length < ctx.ComponentCount)
            {
                ctx.ClusterSrcAccessors = new ChunkAccessor<PersistentStore>[ctx.ComponentCount];
            }
            bool hasTransientSlots = ctx.Meta.TransientSlotMask != 0;
            if (hasTransientSlots)
            {
                if (ctx.ClusterTransientSrcAccessors == null || ctx.ClusterTransientSrcAccessors.Length < ctx.ComponentCount)
                {
                    ctx.ClusterTransientSrcAccessors = new ChunkAccessor<TransientStore>[ctx.ComponentCount];
                }
            }
            for (int slot = 0; slot < ctx.ComponentCount; slot++)
            {
                var table = ctx.EngineState.SlotToComponentTable[slot];
                if (table.StorageMode == StorageMode.Transient)
                {
                    ctx.ClusterTransientSrcAccessors[slot] = table.TransientComponentSegment.CreateChunkAccessor();
                }
                else
                {
                    ctx.ClusterSrcAccessors[slot] = table.ComponentSegment.CreateChunkAccessor(_changeSet);
                }
            }

            // Per-archetype index accessor for cluster B+Tree insertion
            if (ctx.ClusterState.IndexSegment != null)
            {
                ctx.ClusterIdxAccessor = ctx.ClusterState.IndexSegment.CreateChunkAccessor(_changeSet);
                ctx.HasClusterIdxAccessor = true;
            }

            if (ctx.ClusterState.IndexSegmentString64 != null)
            {
                ctx.ClusterIdxAccessorS64 = ctx.ClusterState.IndexSegmentString64.CreateChunkAccessor(_changeSet);
                ctx.HasClusterIdxAccessorS64 = true;
            }

            // The Transient index home (#655). No ChangeSet: a heap-backed segment has nothing to log or checkpoint.
            if (ctx.ClusterState.TransientIndexSegment != null)
            {
                ctx.ClusterTransientIdxAccessor = ctx.ClusterState.TransientIndexSegment.CreateChunkAccessor();
                ctx.HasClusterTransientIdxAccessor = true;
            }

            if (ctx.ClusterState.TransientIndexSegmentString64 != null)
            {
                ctx.ClusterTransientIdxAccessorS64 = ctx.ClusterState.TransientIndexSegmentString64.CreateChunkAccessor();
                ctx.HasClusterTransientIdxAccessorS64 = true;
            }

            // Issue #229 Phase 1+2: cache spatial-cell routing info once per archetype. The hot spawn path reads SpatialSlotIndexCached once per entity to
            // decide between ClaimSlot and ClaimSlotInCell — no per-entity pointer chasing through EngineState → table → overhead.
            ctx.SpatialGridCached = _dbe.Realm0Grid;
            ctx.RealmKeyOffsetCached = ctx.ClusterState.SpatialSlot.RealmKeyOffset;
            ctx.RealmKeySlotCached = ctx.ClusterState.SpatialSlot.RealmKeySlot;
            ctx.RealmKeyOverheadCached = ctx.ClusterState.SpatialSlot.HasRealmKey
                ? ctx.EngineState.SlotToComponentTable[ctx.ClusterState.SpatialSlot.RealmKeySlot].ComponentOverhead
                : 0;
            ctx.LastSpawnRealm = -1;
            ctx.LastSpawnRealmGrid = null;
            var realmKeyed = ctx.ClusterState.SpatialSlot.HasSpatialIndex && ctx.ClusterState.SpatialSlot.HasRealmKey;
            if ((ctx.SpatialGridCached != null || realmKeyed) && ctx.ClusterState.SpatialSlot.HasSpatialIndex)
            {
                ref readonly var ss = ref ctx.ClusterState.SpatialSlot;
                ctx.SpatialSlotIndexCached = ss.Slot;
                ctx.SpatialComponentOverheadCached = ctx.EngineState.SlotToComponentTable[ss.Slot].ComponentOverhead;
                ctx.SpatialFieldOffsetCached = ss.FieldOffset;
                ctx.SpatialFieldTypeCached = ss.FieldInfo.FieldType;
            }
            else
            {
                ctx.SpatialSlotIndexCached = -1;
            }
        }
        else
        {
            ctx.SpatialSlotIndexCached = -1;
            ctx.SpatialGridCached = null;
        }

        // Build SV indexed slot accessors for this archetype (Transient handled separately below).
        // First pass: count SV indexed slots, then allocate exact sizes.
        ctx.SvSlotCount = 0;
        ctx.SvIdxAccessorTotal = 0;
        int idxCount = 0;
        for (int slot = 0; slot < ctx.Meta.ComponentCount; slot++)
        {
            var table = ctx.EngineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.SingleVersion)
            {
                continue;
            }
            var ifi = table.IndexedFieldInfos;
            if (ifi == null || ifi.Length == 0)
            {
                continue;
            }
            ctx.SvSlotCount++;
            idxCount += ifi.Length;
        }

        if (ctx.SvSlotCount > 0)
        {
            // Reuse arrays if large enough, otherwise allocate exact size
            if (ctx.SvCompAccessors == null || ctx.SvCompAccessors.Length < ctx.SvSlotCount)
            {
                ctx.SvCompAccessors = new ChunkAccessor<PersistentStore>[ctx.SvSlotCount];
            }
            if (ctx.SvIdxAccessors == null || ctx.SvIdxAccessors.Length < idxCount)
            {
                ctx.SvIdxAccessors = new ChunkAccessor<PersistentStore>[idxCount];
            }
        }

        ctx.SvSlotCount = 0;
        ctx.SvIdxAccessorTotal = 0;
        for (int slot = 0; slot < ctx.Meta.ComponentCount; slot++)
        {
            var table = ctx.EngineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.SingleVersion)
            {
                continue;
            }
            var indexedFieldInfos = table.IndexedFieldInfos;
            if (indexedFieldInfos == null || indexedFieldInfos.Length == 0)
            {
                continue;
            }

            svSlots[ctx.SvSlotCount] = slot;
            ctx.SvCompAccessors[ctx.SvSlotCount] = table.ComponentSegment.CreateChunkAccessor(_changeSet);
            svIdxAccessorBase[ctx.SvSlotCount] = ctx.SvIdxAccessorTotal;
            ctx.SvSlotCount++;
        }

        // Build Transient indexed slot accessors — same two-pass pattern.
        ctx.TrSlotCount = 0;
        ctx.TrIdxAccessorTotal = 0;
        int trIdxCount = 0;
        for (int slot = 0; slot < ctx.Meta.ComponentCount; slot++)
        {
            var table = ctx.EngineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.Transient)
            {
                continue;
            }
            var ifi = table.IndexedFieldInfos;
            if (ifi == null || ifi.Length == 0)
            {
                continue;
            }
            ctx.TrSlotCount++;
            trIdxCount += ifi.Length;
        }

        if (ctx.TrSlotCount > 0)
        {
            if (ctx.TrCompAccessors == null || ctx.TrCompAccessors.Length < ctx.TrSlotCount)
            {
                ctx.TrCompAccessors = new ChunkAccessor<TransientStore>[ctx.TrSlotCount];
            }
            if (ctx.TrIdxAccessors == null || ctx.TrIdxAccessors.Length < trIdxCount)
            {
                ctx.TrIdxAccessors = new ChunkAccessor<TransientStore>[trIdxCount];
            }
        }

        ctx.TrSlotCount = 0;
        ctx.TrIdxAccessorTotal = 0;
        for (int slot = 0; slot < ctx.Meta.ComponentCount; slot++)
        {
            var table = ctx.EngineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.Transient)
            {
                continue;
            }
            var indexedFieldInfos = table.IndexedFieldInfos;
            if (indexedFieldInfos == null || indexedFieldInfos.Length == 0)
            {
                continue;
            }

            trSlots[ctx.TrSlotCount] = slot;
            ctx.TrCompAccessors[ctx.TrSlotCount] = table.TransientComponentSegment.CreateChunkAccessor();
            trIdxAccessorBase[ctx.TrSlotCount] = ctx.TrIdxAccessorTotal;
            ctx.TrSlotCount++;
        }
    }

    private void FlushPendingDestroys()
    {
        if (_pendingDestroys == null || _pendingDestroys.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        // Hoist stackalloc out of loop. Sized on the CLUSTER record (83B), not the legacy one (78B): this loop reads records of both shapes, and TryGet fills
        // meta._entityRecordSize bytes — so a cluster archetype whose every component is Versioned overflowed the legacy size by 5 bytes.
        byte* readBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];

        // Hoist EntityMap accessor — reuse when archetype matches (same pattern as PlaceSpawns)
        ushort lastArchId = 0;
        var accessor = default(ChunkAccessor<PersistentStore>);
        bool hasAccessor = false;

        // Cluster accessor — hoisted per-archetype
        var clusterAccessor = default(ChunkAccessor<PersistentStore>);
        bool hasClusterAccessor = false;
        var destroyTransientClusterAccessor = default(ChunkAccessor<TransientStore>);
        bool hasDestroyTransientClusterAccessor = false;
        bool destroyUseCluster = false;
        ArchetypeClusterState destroyClusterState = null;
        var destroyClusterIdxAccessor = default(ChunkAccessor<PersistentStore>);
        bool hasDestroyClusterIdxAccessor = false;
        // A field's nodes live in whichever segment its stride requires; String64 fields use the archetype's second segment (#658).
        var destroyClusterIdxAccessorS64 = default(ChunkAccessor<PersistentStore>);
        bool hasDestroyClusterIdxAccessorS64 = false;
        // The Transient index home (#655). No ChangeSet — a heap-backed segment has nothing to log or checkpoint.
        var destroyClusterTransientIdxAccessor = default(ChunkAccessor<TransientStore>);
        bool hasDestroyClusterTransientIdxAccessor = false;
        var destroyClusterTransientIdxAccessorS64 = default(ChunkAccessor<TransientStore>);
        bool hasDestroyClusterTransientIdxAccessorS64 = false;

        try
        {
            foreach (var entityId in _pendingDestroys)
            {
                RefreshBetweenCommitSteps();
                var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
                if (meta == null)
                {
                    continue;
                }
                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                if (engineState?.EntityMap == null)
                {
                    continue;
                }

                if (!hasAccessor || entityId.ArchetypeId != lastArchId)
                {
                    if (hasAccessor)
                    {
                        accessor.Dispose();
                        if (hasClusterAccessor)
                        {
                            clusterAccessor.Dispose();
                            hasClusterAccessor = false;
                        }
                        if (hasDestroyTransientClusterAccessor)
                        {
                            destroyTransientClusterAccessor.Dispose();
                            hasDestroyTransientClusterAccessor = false;
                        }
                        if (hasDestroyClusterIdxAccessor)
                        {
                            destroyClusterIdxAccessor.Dispose();
                            hasDestroyClusterIdxAccessor = false;
                        }
                        if (hasDestroyClusterIdxAccessorS64)
                        {
                            destroyClusterIdxAccessorS64.Dispose();
                            hasDestroyClusterIdxAccessorS64 = false;
                        }
                        if (hasDestroyClusterTransientIdxAccessor)
                        {
                            destroyClusterTransientIdxAccessor.Dispose();
                            hasDestroyClusterTransientIdxAccessor = false;
                        }
                        if (hasDestroyClusterTransientIdxAccessorS64)
                        {
                            destroyClusterTransientIdxAccessorS64.Dispose();
                            hasDestroyClusterTransientIdxAccessorS64 = false;
                        }
                    }
                    accessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                    lastArchId = entityId.ArchetypeId;
                    hasAccessor = true;

                    // Set up cluster accessor if applicable. Reset FIRST: a destroy batch walks several archetypes, and leaving the previous archetype's
                    // state in place means a later non-cluster-eligible one folds a chunk id decoded from ITS record into the wrong archetype's summary —
                    // sizing that archetype's visibility arrays to a garbage index. The old code never reset it either; the difference is that the fold
                    // guard below now selects precisely the stale case.
                    destroyClusterState = null;
                    destroyUseCluster = meta.IsClusterEligible && engineState.ClusterState != null;
                    if (destroyUseCluster)
                    {
                        destroyClusterState = engineState.ClusterState;
                        if (destroyClusterState.ClusterSegment != null)
                        {
                            clusterAccessor = destroyClusterState.ClusterSegment.CreateChunkAccessor(_changeSet);
                            hasClusterAccessor = true;
                        }

                        // Opened whenever the archetype HAS a Transient segment, not only when it is the primary. A mixed archetype's Transient component
                        // bytes live here, and the destroy path must read this slot's current key from them to remove the right index entry — reading it off
                        // the cluster base instead removes a key the entity never had, leaving the real one in the tree (#655).
                        if (destroyClusterState.TransientSegment != null)
                        {
                            destroyTransientClusterAccessor = destroyClusterState.TransientSegment.CreateChunkAccessor();
                            hasDestroyTransientClusterAccessor = true;
                        }
                        if (destroyClusterState.IndexSegment != null)
                        {
                            destroyClusterIdxAccessor = destroyClusterState.IndexSegment.CreateChunkAccessor(_changeSet);
                            hasDestroyClusterIdxAccessor = true;
                        }
                        if (destroyClusterState.IndexSegmentString64 != null)
                        {
                            destroyClusterIdxAccessorS64 = destroyClusterState.IndexSegmentString64.CreateChunkAccessor(_changeSet);
                            hasDestroyClusterIdxAccessorS64 = true;
                        }
                        if (destroyClusterState.TransientIndexSegment != null)
                        {
                            destroyClusterTransientIdxAccessor = destroyClusterState.TransientIndexSegment.CreateChunkAccessor();
                            hasDestroyClusterTransientIdxAccessor = true;
                        }
                        if (destroyClusterState.TransientIndexSegmentString64 != null)
                        {
                            destroyClusterTransientIdxAccessorS64 = destroyClusterState.TransientIndexSegmentString64.CreateChunkAccessor();
                            hasDestroyClusterTransientIdxAccessorS64 = true;
                        }
                    }
                }

                if (engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
                {
                    // Clear cluster bits if cluster storage is active
                    if (destroyUseCluster)
                    {
                        int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
                        byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);

                        // H1 ordering: the died watermark is folded BEFORE ReleaseSlot clears the occupancy bit, and the order is the whole point. A reader
                        // that sees the cleared bit must also see the watermark, or it drops an entity whose death postdates its snapshot — the occupancy
                        // word is current state while a reader is at a snapshot, and only the watermark separates them. Folding early is free because the
                        // watermark is conservative UPWARD: a value recorded before the bit is cleared, or for a destroy that then fails, costs a per-entity
                        // probe and can never make the gate say "visible" when it is not (#722 review). And before the B+Tree entries go (#1347): an index
                        // path whose tree read misses them reads the archetype's destroy stamp and this watermark above its snapshot, and looks at the cluster.
                        destroyClusterState?.NoteDestroyPublished(clusterChunkId, PublishStamp);

                        // Under the slot's index lock (VIS-08): a spawn still publishing holds it until its entries and tails are written and its slot
                        // occupied — removed before, nothing was removed, and the spawn then indexed, and occupied, a dead entity. An update of an indexed
                        // component holds it across its move and its slot copy, so the keys read here are the ones the index holds. And a slot that no
                        // longer holds the entity alive is skipped: another destroy removed its entries and hid or released the slot, which may hold
                        // another entity since (#1370).
                        // A spawn still publishing holds the slot's lock, and its own component publish takes chain locks: waited for before the chains
                        // below are taken. Then the chains of the entity's indexed Versioned components that this destroy did not tombstone — created
                        // after its snapshot, by a supply: an update of one moves its keys under that chain lock and no other (Transaction.PublishComponent).
                        destroyClusterState.WaitSlotIndexesUnlocked(clusterChunkId, slotIndex);
                        _dbe.DestroyAfterSpawnWaitProbe?.Invoke();
                        var chainHolds = LockUntombstonedChains(meta, engineState, readBuf, entityId);
                        destroyClusterState.LockSlotIndexes(clusterChunkId, slotIndex);
                        try
                        {
                            var liveBase = hasClusterAccessor
                                ? clusterAccessor.GetChunkAddress(clusterChunkId)
                                : destroyTransientClusterAccessor.GetChunkAddress(clusterChunkId);
                            if (destroyClusterState.HoldsLive(liveBase, slotIndex, entityId))
                            {
                                // Remove per-archetype B+Tree entries before releasing the slot. If the entity was written this tick (shadow bitmap set),
                                // the tick fence's ProcessClusterShadowEntries detects occupancy=0 and removes the OLD key — which is the only correct
                                // key, because the cluster data may already hold the post-mutation value while the B+Tree still holds the pre-mutation one
                                // (the Move has not happened yet).
                                //
                                // #711: that hand-off is only valid for the slots the fence actually shadows. ShadowClusterIndexedFields captures every
                                // indexed NON-Versioned slot and skips Versioned ones (their index is maintained on the commit path instead), while the
                                // shadow BITMAP is per-entity — set, since #1344 / #1346, by a key-changing write to any fence-indexed component. So one
                                // such write was enough to make this branch skip a Versioned slot's removal and hand it to a fence that never had an entry
                                // for it — nobody removed it, and the index kept an entry for a released slot. Silent for AllowMultiple, loud on the next
                                // rebuild for Unique. No key move is needed to reach it.
                                //
                                // So the skip is scoped to the shadowed slots: when a shadow is pending, Versioned slots are still removed here, and only
                                // those. Remove from BOTH index homes (#655). The elementId tail lives on the cluster (primary) base even for a Transient
                                // slot, whose component bytes live in the Transient segment.
                                {
                                    int entityIndex = clusterChunkId * 64 + slotIndex;
                                    // The exact complement of what the captures recorded (#1339: on an archetype with two marks, a Commit writer records the
                                    // Transient keys only).
                                    var recorded = destroyClusterState.FenceRecordedSlots(entityIndex);
                                    bool hasPendingShadow = recorded != 0;
                                    ushort removeHereMask = (ushort)~recorded;

                                    if (removeHereMask != 0)
                                    {
                                        byte* clusterBase = hasClusterAccessor
                                            ? clusterAccessor.GetChunkAddress(clusterChunkId)
                                            : destroyTransientClusterAccessor.GetChunkAddress(clusterChunkId);
                                        var layout = destroyClusterState.Layout;

                                        RemoveClusterIndexEntries(destroyClusterState.IndexSlots, engineState, clusterBase, clusterBase, layout, clusterChunkId,
                                            slotIndex, entityId, ref destroyClusterIdxAccessor, ref destroyClusterIdxAccessorS64,
                                            hasDestroyClusterIdxAccessorS64 ? destroyClusterState.IndexSegmentString64 : null, removeHereMask);

                                        // Transient slots are the fence's under every discipline, so under a pending shadow this home is entirely its business.
                                        if (destroyClusterState.TransientIndexSlots != null && !hasPendingShadow)
                                        {
                                            byte* transientBase = hasDestroyTransientClusterAccessor
                                                ? destroyTransientClusterAccessor.GetChunkAddress(clusterChunkId)
                                                : clusterBase;
                                            RemoveClusterIndexEntries(destroyClusterState.TransientIndexSlots, engineState, transientBase, clusterBase, layout,
                                                clusterChunkId, slotIndex, entityId, ref destroyClusterTransientIdxAccessor,
                                                ref destroyClusterTransientIdxAccessorS64, destroyClusterState.TransientIndexSegmentString64);
                                        }
                                    }
                                }

                                // Issue #230 Phase 3 Option B: the per-archetype R-Tree remove call is gone; ReleaseSlot below handles per-cell index cleanup
                                // via FinaliseEmptyClusterCellState when the source cluster becomes empty.
                                // The cell bookkeeping is the cluster's realm's (Realms C1): an entity destroyed in realm 3 frees a slot in realm 3's cell.
                                var releaseGrid = destroyClusterState.SpatialOfCluster(clusterChunkId).Grid;
                                if (hasClusterAccessor && meta.VersionedSlotMask != 0)
                                {
                                    // VIS-09: an archetype that promises snapshot isolation keeps the slot, hidden from HEAD and reserved, for a snapshot
                                    // older than the destroy to still find; the cleanup releases it once no snapshot can
                                    // (DatabaseEngine.ProcessEcsCleanupEntry).
                                    destroyClusterState.HideDestroyedSlot(clusterAccessor.GetChunkAddress(clusterChunkId, true), clusterChunkId, slotIndex,
                                        releaseGrid);
                                }
                                else if (hasClusterAccessor)
                                {
                                    destroyClusterState.ReleaseSlot(ref clusterAccessor, clusterChunkId, slotIndex, _changeSet, releaseGrid);
                                }
                                else if (hasDestroyTransientClusterAccessor)
                                {
                                    destroyClusterState.ReleaseSlot(ref destroyTransientClusterAccessor, clusterChunkId, slotIndex, releaseGrid);
                                }
                            }
                        }
                        finally
                        {
                            destroyClusterState.UnlockSlotIndexes(clusterChunkId, slotIndex);
                            ReleaseChainHolds(chainHolds);
                        }
                    }

                    // Set DiedTSN (header layout is the same for both cluster and legacy records): the pending died PREPARE wrote takes the stamp, in place
                    // under the bucket's latch — of two destroys of one entity, the lowest stamp (VIS-09).
                    // No watermark fold here. It moved ABOVE ReleaseSlot — see the H1 ordering note there — and the guarded copy that briefly remained,
                    // for "the legacy non-cluster-backed shape", was unreachable: destroyClusterState is non-null only when destroyUseCluster is, and every
                    // ArchetypeClusterState carries at least one segment (a non-pure-Transient archetype throws if its ClusterSegment cannot be allocated;
                    // a pure-Transient one has transientSlotMask != 0 by construction), so one of the two accessor flags is always set. In the genuine
                    // legacy shape destroyClusterState is null and the branch was excluded by its own first conjunct. Had it ever run it would have decoded
                    // a cluster chunk id out of a LEGACY record's bytes and folded that garbage index into a cluster summary.
                    var diedStamper = new DiedStamper { Stamp = PublishStamp, Owner = TSN, Cards = CommitCards };
                    engineState.EntityMap.TryUpdateInPlace(entityId.EntityKey, ref diedStamper, ref accessor);

                    // Last: the watermark folded and the slot hidden above, the cluster's gate may vouch for it again (VIS-09) — the cluster PREPARE counted.
                    if (destroyClusterState != null && _pendingDeathClusters != null && _pendingDeathClusters.Remove(entityId, out var markedCluster))
                    {
                        destroyClusterState.AddPendingDeath(markedCluster, -1);
                    }

                    // Enqueue for deferred GC (LinearHash removal, chunk freeing, the hidden slot's release) once MinTSN is past the died STAMP: keyed by the
                    // writer's TSN it was released while a snapshot between the two still saw the entity alive (VIS-06, VIS-09).
                    _dbe.EnqueueEcsCleanup(entityId, meta, PublishStamp);
                }
            }
        }
        finally
        {
            if (hasAccessor)
            {
                accessor.Dispose();
            }
            if (hasClusterAccessor)
            {
                clusterAccessor.Dispose();
            }
            if (hasDestroyTransientClusterAccessor)
            {
                destroyTransientClusterAccessor.Dispose();
            }
            if (hasDestroyClusterIdxAccessor)
            {
                destroyClusterIdxAccessor.Dispose();
            }
            if (hasDestroyClusterIdxAccessorS64)
            {
                destroyClusterIdxAccessorS64.Dispose();
            }
            if (hasDestroyClusterTransientIdxAccessor)
            {
                destroyClusterTransientIdxAccessor.Dispose();
            }
            if (hasDestroyClusterTransientIdxAccessorS64)
            {
                destroyClusterTransientIdxAccessorS64.Dispose();
            }
        }
    }

    /// <summary>
    /// VIS-09, in PREPARE: every committed entity this transaction destroys gets <c>DiedTSN = Pending(owner)</c> on its record, so the destroy exists before
    /// the commit point and a reader resolves it through the owner's card. A died already set — another destroy pending or published — is left. Undone by
    /// <see cref="UnmarkDestroysPending"/> on an abort; stamped by <see cref="FlushPendingDestroys"/>.
    /// </summary>
    private void MarkDestroysPending() => ForEachCommittedDestroyRecord(pending: true);

    /// <summary>An abort before the commit point clears the pending died <see cref="MarkDestroysPending"/> wrote — this transaction's own only.</summary>
    private void UnmarkDestroysPending() => ForEachCommittedDestroyRecord(pending: false);

    private void ForEachCommittedDestroyRecord(bool pending)
    {
        if (_pendingDestroys == null || _pendingDestroys.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        ArchetypeEngineState state = null;
        ushort lastArch = 0;
        var accessor = default(ChunkAccessor<PersistentStore>);
        var has = false;
        try
        {
            foreach (var entityId in _pendingDestroys)
            {
                if (SpawnedContains(entityId))
                {
                    continue;
                }

                var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
                if (meta == null)
                {
                    continue;
                }

                if (!has || entityId.ArchetypeId != lastArch)
                {
                    if (has)
                    {
                        accessor.Dispose();
                        has = false;
                    }

                    state = _dbe._archetypeStates[meta.ArchetypeId];
                    if (state?.EntityMap == null)
                    {
                        continue;
                    }

                    accessor = state.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                    lastArch = entityId.ArchetypeId;
                    has = true;
                }

                var marker = new DiedMarker { Owner = TSN, Pending = pending };
                var updated = state.EntityMap.TryUpdateInPlace(entityId.EntityKey, ref marker, ref accessor);

                // The count is keyed by the cluster PREPARE found, and given back by this transaction whatever the died holds by then: another destroy's
                // publish may have replaced this one's pending value, and a migration before the publish moves the record, not the count.
                if (pending && updated && marker.Changed && meta.IsClusterEligible && state.ClusterState != null)
                {
                    (_pendingDeathClusters ??= new Dictionary<EntityId, int>()).Add(entityId, marker.ClusterChunkId);
                    state.ClusterState.AddPendingDeath(marker.ClusterChunkId, 1);
                }
                else if (!pending && _pendingDeathClusters != null && _pendingDeathClusters.Remove(entityId, out var marked))
                {
                    state.ClusterState?.AddPendingDeath(marked, -1);
                }
            }
        }
        finally
        {
            if (has)
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// PREPARE staged effects a failure before the commit point must undo (VIS-08–10): set before the first, cleared once they are published or undone. Gates
    /// the undo passes so a rollback that never reached PREPARE pays nothing for them.
    /// </summary>
    private bool _prepareStaged;

    /// <summary>The cluster each destroy marked pending was counted in (VIS-09), for the decrement at publish or abort.</summary>
    private Dictionary<EntityId, int> _pendingDeathClusters;

    /// <summary>Writes (<see cref="Pending"/>) or clears this owner's pending died — a died already holding another value is left.</summary>
    private struct DiedMarker : IRawValueUpdater
    {
        public long Owner;
        public bool Pending;
        public bool Changed;
        public int ClusterChunkId;

        public void Update(byte* valueBytes)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(valueBytes);
            var mine = EntityRecordHeader.PendingBorn(Owner);
            Changed = Pending ? header.DiedTSN == 0 : header.DiedTSN == mine;
            if (Changed)
            {
                header.DiedTSN = Pending ? mine : 0;
                ClusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(valueBytes);
            }
        }
    }

    /// <summary>
    /// A destroy's publish: the died takes <see cref="Stamp"/> — over this owner's pending value or none, and below any other: a published died keeps the
    /// lower stamp, and another owner's pending one is weighed by that owner's card (VIS-09: of two destroys, the first stamp wins).
    /// </summary>
    private struct DiedStamper : IRawValueUpdater
    {
        public long Stamp;
        public long Owner;
        public CommitCardTable Cards;
        public bool WasMarked;

        public void Update(byte* valueBytes)
        {
            ref var header = ref EntityRecordAccessor.GetHeader(valueBytes);
            var current = header.DiedTSN;
            WasMarked = current == EntityRecordHeader.PendingBorn(Owner);
            if (current == 0 || WasMarked)
            {
                header.DiedTSN = Stamp;
            }
            else if (header.IsDiedPending)
            {
                var other = current & ~EntityRecordHeader.PendingBit;
                header.DiedTSN = Cards != null && Cards.TryGetStamp(other, out var otherStamp) && otherStamp < Stamp ? otherStamp : Stamp;
            }
            else if (Stamp < current)
            {
                header.DiedTSN = Stamp;
            }
        }
    }

    /// <summary>
    /// Prepare component-level tombstone revisions for pending destroys. Called BEFORE CommitComponentCore so it can handle secondary index removal,
    /// WAL delete entries, and view notifications. The archetype-level DiedTSN is set later in FlushPendingDestroys (post-commit).
    /// </summary>
    private void PrepareEcsDestroys()
    {
        if (_pendingDestroys == null || _pendingDestroys.Count == 0)
        {
            return;
        }

        // Hoist EntityMap accessor for SV entity record reads — reuse when archetype matches
        ushort lastArchId = 0;
        var emAccessor = default(ChunkAccessor<PersistentStore>);
        bool hasEmAccessor = false;
        byte* readBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];

        try
        {
            foreach (var entityId in _pendingDestroys)
            {
                // Skip entities that were spawned in this same transaction — they have no committed component data to delete
                // (PlaceSpawns skips spawn+destroy entities).
                if (SpawnedContains(entityId))
                {
                    continue;
                }

                var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
                if (meta == null)
                {
                    continue;
                }

                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                if (engineState?.SlotToComponentTable == null)
                {
                    continue;
                }

                long pk = (long)entityId.RawValue;

                // Check if this archetype has SV indexed or spatial-indexed components requiring entity record lookup.
                // Cluster-eligible archetypes never need the legacy record here: their SV/spatial/index removal is done by the cluster destroy path
                // (FlushPendingDestroys + ProcessClusterShadowEntries), and reading a ClusterEntityRecord through the legacy EntityRecord layout would be
                // incorrect.
                bool needsEntityRecord = false;
                if (!meta.IsClusterEligible)
                {
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        var table = engineState.SlotToComponentTable[slot];
                        if (table?.HasShadowableIndexes == true || table?.SpatialIndex != null)
                        {
                            needsEntityRecord = true;
                            break;
                        }
                    }
                }

                // Read entity record from EntityMap (lazy, per-archetype accessor)
                bool hasRecord = false;
                if (needsEntityRecord)
                {
                    if (!hasEmAccessor || entityId.ArchetypeId != lastArchId)
                    {
                        if (hasEmAccessor)
                        {
                            emAccessor.Dispose();
                        }

                        emAccessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
                        lastArchId = entityId.ArchetypeId;
                        hasEmAccessor = true;
                    }

                    hasRecord = engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref emAccessor);
                }

                // Cluster Versioned components address their revision chain via the cluster EntityMap record (CompRevFirstChunkId), NOT the per-component PK
                // index that MarkComponentDeleted's fallback uses — so a destroy-without-Open would fail to resolve them and never tombstone the chain.
                // Pre-resolve them into the CompRevInfo cache here so the tombstone (below) is created and the chain — with any ComponentCollection
                // buffers it holds — is cleaned via the CC-aware revision path.
                if (meta.IsClusterEligible && meta.ClusterLayout?.SlotToVersionedIndex != null)
                {
                    ResolveClusterVersionedForDestroy(entityId, meta, engineState, pk);
                }

                // Versioned components in a cluster-eligible archetype still own a revision chain (HEAD cached in the cluster slot, chain in CompRevTable),
                // so they MUST be tombstoned here — that routes the chain (and any ComponentCollection buffers it holds) through the CC-aware revision
                // cleanup (FreeCompContentChunk). The legacy per-ComponentTable SV/spatial index removal below reads the entity record and is the cluster
                // destroy path's responsibility (FlushPendingDestroys + ProcessClusterShadowEntries) — skip it for clusters.
                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = engineState.SlotToComponentTable[slot];
                    if (table == null)
                    {
                        continue;
                    }

                    if (table.StorageMode == StorageMode.Versioned)
                    {
                        MarkComponentDeleted(meta._slotToComponentType[slot], pk);
                    }
                    else if (!meta.IsClusterEligible && (table.HasShadowableIndexes || table.SpatialIndex != null) && hasRecord)
                    {
                        int chunkId = EntityRecordAccessor.GetLocation(readBuf, slot);
                        table.TrackDestroyedChunkId(chunkId);

                        // The per-ComponentTable index removal that stood here is gone (#629). Destroy removes per-archetype entries — including the view
                        // deletion delta — through FlushPendingDestroys / RemoveClusterIndexEntries, for both the persistent and the transient home.
                        //
                        // The entity-level spatial removal that stood beside it is gone too (#872 step 13): this branch is guarded on
                        // `!meta.IsClusterEligible`, which #666 made unreachable by making every archetype cluster-backed, so the tree it removed from had
                        // been empty for as long as this code could not run.
                    }
                }
            }
        }
        finally
        {
            if (hasEmAccessor)
            {
                emAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Mark a component as deleted in the ComponentInfo cache for a destroyed entity.
    /// Creates a tombstone revision (CurCompContentChunkId = 0) so CommitComponentCore can handle index removal, WAL entries, and deferred cleanup.
    /// </summary>
    private void MarkComponentDeleted(Type compType, long pk)
    {
        var info = GetComponentInfo(compType);

        ref var cri = ref CollectionsMarshal.GetValueRefOrAddDefault(info.SingleCache, pk, out var cached);

        if (cached)
        {
            // Already in cache (from Open/Write in same tx)
            if ((cri.Operations & ComponentInfo.OperationType.Deleted) != 0)
            {
                return;
            }

            // Free the chunk a Spawn or Write in this transaction allocated — and only that one. An entry that was only READ holds the COMMITTED
            // revision's content chunk, which the chain owns: older snapshots still read it, and the revision GC frees it once the tombstone below makes
            // it unreachable. Freeing it here as well freed it twice. The first free let a spawn reuse it at once, and the GC's free then took it from
            // under that new entity, so the next spawn got the same chunk and two live entities shared one payload: a consume-and-craft market storm read
            // another item's data within a few thousand operations, and 100 destroy-then-respawn rounds on 2,000 entities were enough to show it.
            if (cri.CurCompContentChunkId != 0 && (cri.Operations & (ComponentInfo.OperationType.Created | ComponentInfo.OperationType.Updated)) != 0)
            {
                // This transaction's revision — a write's, or a component enabled in it — becomes the tombstone, element first: freed while its element
                // still named it, the chunk stayed listed by a committed revision, a dangling reference in a chain that could never reduce to its
                // tombstone. Not found, the chunk is left allocated: a leak, never a free under an element that still names it.
                if (ComponentRevisionManager.MakeOwnRevisionTombstone(info, ref cri, cri.CurCompContentChunkId))
                {
                    // CC-aware: a copy-on-write took a reference on every collection buffer it shares with the committed revision.
                    DeferredCleanupManager.FreeContentChunk(info.ComponentTable, cri.CurCompContentChunkId);
                }
                else
                {
                    System.Diagnostics.Debug.Fail(
                        $"REAP-02: no uncommitted revision names chunk {cri.CurCompContentChunkId}; left allocated rather than freed under it");
                }

                cri.CurCompContentChunkId = 0;
            }
        }
        else
        {
            // Not in cache — read from index. A throw (the walk's lock timeout) takes the entry this call inserted with it (CHAIN-02).
            ComponentInfo.CompRevInfo resolved;
            try
            {
                var result = GetCompRevInfoFromIndex(pk, info, TSN);
                if (result.IsFailure)
                {
                    info.SingleCache.Remove(pk);
                    return;
                }

                resolved = result.Value;
            }
            catch
            {
                info.SingleCache.Remove(pk);
                throw;
            }

            cri = resolved;
        }

        // Create the tombstone revision only on the first mutation — UpdateComponent's guard, which this used to paraphrase as "was read". An entry that
        // was read and then WRITTEN carries both bits: it already has this transaction's revision, which the cleared chunk id above turns into the
        // tombstone. Adding a second one left that first revision isolated for ever, its chunk freed above, so neither the GC nor the entity cleanup could
        // reduce the chain to its tombstone: every write-then-destroy leaked the chain and the committed content it still listed.
        //
        // The tombstone is appended before the entry claims the delete (CHAIN-02): claimed first, a failed append (the chain-lock timeout, at commit) left
        // an entry marked Deleted that named the committed revision — a retried commit skipped the tombstone and took that revision for its own.
        if (!cached || (cri.Operations & (ComponentInfo.OperationType.Created | ComponentInfo.OperationType.Updated)) == 0)
        {
            try
            {
                ComponentRevisionManager.AddCompRev(info, ref cri, TSN, UowId, true);
            }
            catch
            {
                if (!cached)
                {
                    info.SingleCache.Remove(pk);
                }

                throw;
            }
        }

        cri.Operations |= ComponentInfo.OperationType.Deleted;
    }

    /// <summary>
    /// Populate the per-component <see cref="ComponentInfo"/> revision cache with the visible revision for each Versioned slot of a cluster entity being
    /// destroyed. Cluster Versioned components address their revision chain through the cluster EntityMap record (<c>CompRevFirstChunkId</c>), not the
    /// per-component PK index that <see cref="MarkComponentDeleted"/>'s fallback uses, so without this a destroy-without-Open cannot resolve them and never
    /// tombstones the chain (leaking the chain and any ComponentCollection buffers it holds). Mirrors <c>ArchetypeAccessor.ResolveClusterVersionedSlots</c>.
    /// </summary>
    private void ResolveClusterVersionedForDestroy(EntityId entityId, ArchetypeMetadata meta, ArchetypeEngineState engineState, long pk)
    {
        var layout = meta.ClusterLayout;
        byte* record = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        var emAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            if (!engineState.EntityMap.TryGet(entityId.EntityKey, record, ref emAccessor))
            {
                return;
            }

            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                int vi = layout.SlotToVersionedIndex[slot];
                if (vi < 0)
                {
                    continue;
                }

                int firstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, vi);
                if (firstChunkId == 0)
                {
                    continue;
                }

                var info = GetComponentInfo(meta._slotToComponentType[slot]);
                if (info.SingleCache.ContainsKey(pk))
                {
                    continue; // already resolved by an Open/Write earlier in this transaction
                }

                var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, firstChunkId, TSN, CommitCards, true);
                if (chainResult.IsFailure)
                {
                    continue;
                }

                var cri = chainResult.Value;
                cri.Operations = ComponentInfo.OperationType.Read;
                info.AddNew(pk, cri);
            }
        }
        finally
        {
            emAccessor.Dispose();
        }
    }

    private void FlushPendingEnableDisable()
    {
        if (_pendingEnableDisable == null || _pendingEnableDisable.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);

        // Hoist stackalloc out of loop. Sized on the CLUSTER record (83B), not the legacy one (78B): this loop reads records of both shapes, and TryGet fills
        // meta._entityRecordSize bytes — so a cluster archetype whose every component is Versioned overflowed the legacy size by 5 bytes.
        byte* readBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];

        // One cluster accessor per archetype, not per entity: each creation resolves and pins a page and re-registers it in the change set.
        ChunkAccessor<PersistentStore> clusterAccessor = default;
        ArchetypeClusterState clusterAccessorOwner = null;
        try
        {
            foreach (var kvp in _pendingEnableDisable)
            {
                FlushPendingEnableDisableEntry(kvp.Key, kvp.Value, readBuf, ref clusterAccessor, ref clusterAccessorOwner);
            }
        }
        finally
        {
            if (clusterAccessorOwner != null)
            {
                clusterAccessor.Dispose();
            }
        }
    }

    /// <summary>Publishes one entity's committed enabled mask to both copies — the EntityMap record, then the cluster words.</summary>
    private void FlushPendingEnableDisableEntry(EntityId entityId, ushort newBits, byte* readBuf, ref ChunkAccessor<PersistentStore> clusterAccessor,
        ref ArchetypeClusterState clusterAccessorOwner)
    {
        // Skip spawned entities — PlaceSpawns applies the enable/disable override
        if (SpawnedContains(entityId))
        {
            return;
        }

        var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
        if (meta == null)
        {
            return;
        }
        var engineState = _dbe._archetypeStates[meta.ArchetypeId];
        if (engineState?.EntityMap == null)
        {
            return;
        }

        var accessor = engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
        if (engineState.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
        {
            ushort oldBits = EntityRecordAccessor.GetHeader(readBuf).EnabledBits;

            if (oldBits != newBits)
            {
                // Notify views: enable/disable changes component visibility.
                // Enable (0→1) emits isCreation so the view re-evaluates the entity.
                // Disable (1→0) emits isDeletion so the view removes the entity.
                NotifyViewsForEnableDisable(entityId, meta, engineState, oldBits, newBits);
            }

            // Both copies of the enabled state are published here, at commit, and nowhere earlier: the EntityMap record read by Open and queries, then
            // the cluster EnabledBits words read by bulk iteration and the rebuilds (ENABLE-01). In place, the bits and the chain roots this commit
            // created only: a whole-record write from this copy would undo a field another commit wrote meanwhile — a concurrent destroy's pending died.
            EntityRecordAccessor.GetHeader(readBuf).EnabledBits = newBits;
            PublishNewVersionedChainRoots(entityId, meta, readBuf);
            var publisher = new EnabledBitsPublisher { Bits = newBits, Source = readBuf, VersionedCount = meta.IsClusterEligible ? meta.VersionedSlotCount : 0 };
            engineState.EntityMap.TryUpdateInPlace(entityId.EntityKey, ref publisher, ref accessor);

            // Nothing to publish when the mask did not change (a Disable+Enable in one transaction), and nothing worth publishing for an entity this
            // commit destroys: FlushPendingDestroys runs next, and its ReleaseSlot clears the slot (or HideDestroyedSlot takes it out of occupancy, VIS-09).
            if (oldBits != newBits && (_pendingDestroys == null || !_pendingDestroys.Contains(entityId)))
            {
                PublishClusterEnabledBits(engineState.ClusterState, meta.ComponentCount, entityId, readBuf, newBits, ref clusterAccessor,
                    ref clusterAccessorOwner);
            }

        }
        accessor.Dispose();

        // The change PREPARE recorded pending takes the stamp — after both inline copies, before the card goes (VIS-10) — the record found or not: left
        // pending, it would never be stamped or pruned.
        _dbe.EnabledBitsOverrides.Publish(entityId.EntityKey, TSN, PublishStamp);
    }

    /// <summary>An enable/disable's publish: the record's enabled bits, and each chain root this commit created where the record has none, in place.</summary>
    private struct EnabledBitsPublisher : IRawValueUpdater
    {
        public ushort Bits;
        public byte* Source;
        public int VersionedCount;

        public void Update(byte* valueBytes)
        {
            EntityRecordAccessor.GetHeader(valueBytes).EnabledBits = Bits;
            for (var vi = 0; vi < VersionedCount; vi++)
            {
                var root = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(Source, vi);
                if (root != 0 && ClusterEntityRecordAccessor.GetCompRevFirstChunkId(valueBytes, vi) == 0)
                {
                    ClusterEntityRecordAccessor.SetCompRevFirstChunkId(valueBytes, vi, root);
                }
            }
        }
    }

    /// <summary>
    /// VIS-10, in PREPARE: every enable/disable this transaction commits on an existing entity is recorded pending under its owner, with the bits it
    /// changes from and to, so the change exists before the commit point: a reader begun after it resolves it through the owner's card. Stamped at publish
    /// (<see cref="FlushPendingEnableDisableEntry"/>); dropped on an abort (<see cref="UnmarkEnableDisablePending"/>).
    /// </summary>
    private void MarkEnableDisablePending()
    {
        if (_pendingEnableDisable == null || _pendingEnableDisable.Count == 0)
        {
            return;
        }

        using var guard = EpochGuard.Enter(_epochManager);
        byte* readBuf = stackalloc byte[ClusterEntityRecordAccessor.MaxRecordSize];
        ArchetypeEngineState state = null;
        ushort lastArch = 0;
        var accessor = default(ChunkAccessor<PersistentStore>);
        var has = false;
        try
        {
            foreach (var kvp in _pendingEnableDisable)
            {
                var entityId = kvp.Key;
                if (SpawnedContains(entityId))
                {
                    continue;
                }

                if (!has || entityId.ArchetypeId != lastArch)
                {
                    if (has)
                    {
                        accessor.Dispose();
                        has = false;
                    }

                    var meta = _dbe.GetMetaByRouting(entityId.ArchetypeId);
                    state = meta == null ? null : _dbe._archetypeStates[meta.ArchetypeId];
                    if (state?.EntityMap == null)
                    {
                        continue;
                    }

                    accessor = state.EntityMap.Segment.CreateChunkAccessor();
                    lastArch = entityId.ArchetypeId;
                    has = true;
                }

                if (state.EntityMap.TryGet(entityId.EntityKey, readBuf, ref accessor))
                {
                    var oldBits = EntityRecordAccessor.GetHeader(readBuf).EnabledBits;
                    if (oldBits != kvp.Value)
                    {
                        _dbe.EnabledBitsOverrides.AddPending(entityId.EntityKey, TSN, oldBits, kvp.Value);
                    }
                }
            }
        }
        finally
        {
            if (has)
            {
                accessor.Dispose();
            }
        }
    }

    /// <summary>An abort before the commit point drops the pending enable/disable changes <see cref="MarkEnableDisablePending"/> recorded.</summary>
    private void UnmarkEnableDisablePending()
    {
        if (_pendingEnableDisable == null || _pendingEnableDisable.Count == 0)
        {
            return;
        }

        foreach (var kvp in _pendingEnableDisable)
        {
            _dbe.EnabledBitsOverrides.RemovePending(kvp.Key.EntityKey, TSN);
        }
    }

    /// <summary>
    /// Writes the committed <paramref name="enabledBits"/> into the entity's per-component cluster <c>EnabledBits</c> words (#998, rule ENABLE-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to happen in <c>EntityRefMut.Enable/Disable</c>, at staging. That published an uncommitted change to every concurrent bulk scan, and nothing
    /// undid it on rollback, so a rolled-back Disable hid the component from cluster iteration while the record still said enabled — and the next
    /// checkpoint persisted the divergence, which the crash rebuild then copies back into the record when it has no snapshot.
    /// </para>
    /// <para>
    /// Every slot's bit is set from the absolute mask; a bit already right is left alone, so a commit costs one atomic per component that actually changed.
    /// Those are <c>Interlocked</c> because the word is shared by every entity of the cluster: two transactions committing enable/disable on different
    /// entities of one cluster each read-modify-write it, and a plain <c>|=</c> / <c>&amp;=</c> drops the other's bit — the race <see cref="PlaceSpawns"/>
    /// documents for the same words.
    /// </para>
    /// <para>
    /// The record can be a tombstone: another transaction destroyed the entity and committed after this one read it, released the slot at commit, and a
    /// spawn may already hold it. Writing then would stamp this entity's mask onto a DIFFERENT live entity, and a checkpoint would make that durable. So the
    /// write happens only while the slot is still occupied by this entity's id. The check and the write are not one atomic step — a destroy and a re-spawn
    /// of the same slot landing inside those few instructions remains the concurrent same-entity residual ENABLE-01 states.
    /// </para>
    /// </remarks>
    private void PublishClusterEnabledBits(ArchetypeClusterState clusterState, int componentCount, EntityId entityId, byte* recordBuf, ushort enabledBits,
        ref ChunkAccessor<PersistentStore> clusterAccessor, ref ArchetypeClusterState clusterAccessorOwner)
    {
        if (clusterState == null)
        {
            return;
        }

        var clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(recordBuf);
        var slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(recordBuf);

        // PersistentStore for a mixed or SingleVersion archetype, bound to the change set so the checkpoint persists the page; TransientStore for a
        // pure-Transient one, whose cluster metadata lives there and is never persisted.
        if (clusterState.ClusterSegment != null)
        {
            if (!ReferenceEquals(clusterAccessorOwner, clusterState))
            {
                if (clusterAccessorOwner != null)
                {
                    clusterAccessor.Dispose();
                }

                clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor(_changeSet);
                clusterAccessorOwner = clusterState;
            }

            WriteEnabledWords(clusterAccessor.GetChunkAddress(clusterChunkId, true), clusterState.Layout, componentCount, slotIndex, (long)entityId.RawValue,
                enabledBits);
        }
        else if (clusterState.TransientSegment != null)
        {
            using var transientAccessor = clusterState.TransientSegment.CreateChunkAccessor();
            WriteEnabledWords(transientAccessor.GetChunkAddress(clusterChunkId, true), clusterState.Layout, componentCount, slotIndex, (long)entityId.RawValue,
                enabledBits);
        }

        static void WriteEnabledWords(byte* clusterBase, ArchetypeClusterInfo layout, int componentCount, int slotIndex, long entityIdRaw, ushort enabledBits)
        {
            var entityBit = 1L << slotIndex;
            if ((Volatile.Read(ref *(long*)clusterBase) & entityBit) == 0
                || Volatile.Read(ref *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8)) != entityIdRaw)
            {
                return;   // the slot no longer holds this entity (see remarks)
            }

            for (var slot = 0; slot < componentCount; slot++)
            {
                ref var word = ref *(long*)(clusterBase + layout.EnabledBitsOffset(slot));
                var wanted = (enabledBits & (1 << slot)) != 0;
                if (((Volatile.Read(ref word) & entityBit) != 0) == wanted)
                {
                    continue;
                }

                if (wanted)
                {
                    Interlocked.Or(ref word, entityBit);
                }
                else
                {
                    Interlocked.And(ref word, ~entityBit);
                }
            }
        }
    }

    /// <summary>
    /// Writes the revision-chain root into <paramref name="recordBuf"/> for every Versioned slot this transaction CREATED mid-life (#845).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PlaceSpawns"/> is the commit path's only other writer of <c>CompRevFirstChunkId</c>, and it runs solely for entities spawned in the same
    /// transaction. Without this, a component created on a LIVE entity got a correct chain and a correct content chunk that nothing pointed at, and every
    /// point-read resolver treats root 0 as absence, leaves the location 0, and the read returns zeros.
    /// </para>
    /// <para>
    /// It runs here rather than in the publish phase's per-component path because this method already holds the record between a <c>TryGet</c> and its
    /// <c>Upsert</c> — the root rides along in the write the enable bits were going to make anyway, costing no extra EntityMap lookup.
    /// </para>
    /// <para>
    /// The condition is "this transaction CREATED a chain for (entity, slot)", read from the component's <c>SingleCache</c> — deliberately not "the slot's
    /// enable bit went 0 → 1". Those differ whenever a caller supplies a value and disables it again before committing: the enable delta is then empty, so a
    /// delta-driven publication skips the slot while the commit's component pipeline still indexes the created revision and copies it into the cluster. That
    /// leaves index entries and cluster bytes for a component the record calls absent, and an orphaned chain the entity can never reach again.
    /// </para>
    /// <para>
    /// Reaching this at all still depends on the entity being in <c>_pendingEnableDisable</c>, which holds because supplying a value REQUIRES enabling —
    /// <c>Write</c> is gated by the same EnabledBits as <c>Read</c> — and a subsequent disable updates that entry rather than removing it.
    /// </para>
    /// </remarks>
    private void PublishNewVersionedChainRoots(EntityId entityId, ArchetypeMetadata meta, byte* recordBuf)
    {
        var slotToVi = meta.ClusterLayout?.SlotToVersionedIndex;
        if (slotToVi == null)
        {
            return;
        }

        var pk = (long)entityId.RawValue;
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            var vi = slotToVi[slot];
            if (vi < 0 || ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, vi) != 0)
            {
                continue;
            }

            // Root 0: either genuinely absent, or created by this transaction and not yet published. Only the component's own cache can tell, and the
            // non-creating lookup keeps the absent case at one array index instead of materialising a ComponentInfo per untouched slot.
            var info = TryGetExistingComponentInfo(meta._componentTypeIds[slot]);
            if (info?.SingleCache == null || !info.SingleCache.TryGetValue(pk, out var cri) || cri.CompRevTableFirstChunkId == 0)
            {
                continue;
            }

            ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordBuf, vi, cri.CompRevTableFirstChunkId);
        }
    }

    /// <summary>
    /// Emit ring buffer entries for enable/disable changes so views can update entity membership.
    /// Enable (bit 0→1) emits isCreation; Disable (bit 1→0) emits isDeletion.
    /// Emits per-field to each registered view — redundant entries are idempotent in ProcessEntry.
    /// </summary>
    private void NotifyViewsForEnableDisable(EntityId entityId, ArchetypeMetadata meta, ArchetypeEngineState engineState, ushort oldBits, ushort newBits)
    {
        ushort changedBits = (ushort)(oldBits ^ newBits);

        for (int slot = 0; slot < meta.ComponentCount && changedBits != 0; slot++)
        {
            if ((changedBits & (1 << slot)) == 0)
            {
                continue;
            }

            var table = engineState.SlotToComponentTable[slot];
            if (table?.ViewRegistry == null || table.ViewRegistry.ViewCount == 0)
            {
                continue;
            }

            bool wasEnabled = (oldBits & (1 << slot)) != 0;

            // Iterate all fields that have registered views and emit one entry per view per field.
            for (int fi = 0; fi < table.ViewRegistry.FieldCount; fi++)
            {
                var views = table.ViewRegistry.GetViewsForField(fi);
                for (int v = 0; v < views.Length; v++)
                {
                    var reg = views[v];
                    if (reg.DeltaBuffer.IsClosed)
                    {
                        continue;
                    }

                    // isDeletion (0x80) for disable, isCreation (0x40) for enable
                    byte flags = wasEnabled ? (byte)((fi & 0x3F) | 0x80) : (byte)((fi & 0x3F) | 0x40);
                    QueryPathProbe.PrePublishAppendHook?.Invoke();
                    reg.DeltaBuffer.TryAppend(entityId, default, default, PublishStamp, flags, reg.ComponentTag);
                }
            }
        }
    }

    /// <summary>
    /// Gives back, at commit, the storage of every entity this transaction spawned and then destroyed (#1229): its Versioned content chunks and chain roots,
    /// and the collection buffers its SingleVersion and Transient payloads hold — what a rollback frees. Both commit paths skip such an entity (no EntityMap
    /// insert, no committed data to delete), so nothing else ever did: each chain stayed allocated in no record, and a reopen that rebuilt the map from the
    /// chain heads brought the entity back (#1231). Its cache entries go too, so PREPARE neither publishes nor logs a revision for it.
    /// </summary>
    /// <remarks>
    /// Runs before PREPARE and is final: a commit that fails after it, or a rollback, finds no cache entry and no arena handle to free again. Destroyed, the
    /// entity is no longer reachable through this transaction (<see cref="ResolveEntity"/> misses it), so nothing reads what is freed here.
    /// </remarks>
    private void ReleaseSpawnsDestroyedHere()
    {
        if (_spawnedEntities is not { Count: > 0 } || _pendingDestroys is not { Count: > 0 } || _dbe.KeepSpawnDestroyedStorageForTest)
        {
            return;
        }

        // Walks the destroys, not the spawns: a bulk spawn with one unrelated destroy pays one lookup, not one per spawn.
        using var epochGuard = EpochGuard.Enter(_epochManager);
        var spawns = CollectionsMarshal.AsSpan(_spawnedEntities);
        foreach (var destroyed in _pendingDestroys)
        {
            var index = SpawnedIndexOf(destroyed);
            if (index < 0)
            {
                continue;
            }

            ref var entry = ref spawns[index];

            var meta = _dbe.GetMetaByRouting(entry.Id.ArchetypeId);
            var engineState = meta == null ? null : _dbe._archetypeStates[meta.ArchetypeId];
            if (engineState?.SlotToComponentTable == null)
            {
                continue;
            }

            var pk = (long)entry.Id.RawValue;
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                if (table.StorageMode == StorageMode.Versioned)
                {
                    // RollbackComponent's Created branch: the chain is this transaction's alone, and so is the content chunk its one revision names.
                    var compType = meta._slotToComponentType[slot];
                    if (!_componentInfos.TryGetValue(compType, out var info) || !info.SingleCache.Remove(pk, out var cri))
                    {
                        continue;
                    }

                    if (cri.CurCompContentChunkId > 0)
                    {
                        DeferredCleanupManager.FreeContentChunk(table, cri.CurCompContentChunkId, neverCommitted: true);
                    }

                    if (cri.CompRevTableFirstChunkId > 0)
                    {
                        table.CompRevTableSegment.FreeChunk(cri.CompRevTableFirstChunkId);
                    }

                    entry.VerLoc[slot] = 0;
                }
                else
                {
                    // The arena itself is dropped by the reset; the collection buffers its payload names are not (DC-01).
                    var stage = entry.Stage[slot];
                    if (stage != 0 && table.HasCollections)
                    {
                        DeferredCleanupManager.ReleaseCollectionBuffers(
                            table, new ReadOnlySpan<byte>(SpawnArena.Resolve(stage), table.ComponentOverhead + table.ComponentStorageSize));
                    }

                    entry.Stage[slot] = 0;
                }
            }
        }
    }

    /// <summary>Clean up ECS-specific state on transaction reset/dispose. Frees orphaned chunks on rollback.</summary>
    internal void CleanupEcsState()
    {
        // Rollback freeing below calls FreeContentChunk, which creates a ChunkAccessor and therefore needs an epoch scope.
        // Entering one here is cheap and nesting-safe; on a committed transaction the freeing blocks are skipped, only the tail clears run.
        using var epochGuard = EpochGuard.Enter(_epochManager);

        // If transaction was NOT committed, free component chunks for spawned entities. A spawn PREPARE placed and nothing un-placed yet (a failure of the
        // rollback that should have) is given back first: its record names the chain freed below (VIS-08).
        if (State != TransactionState.Committed && _prepareStaged)
        {
            UnplaceSpawns();
            UnmarkDestroysPending();
            UnmarkEnableDisablePending();
            _prepareStaged = false;
        }

        if (_spawnedEntities is { Count: > 0 } && State != TransactionState.Committed)
        {
            foreach (var entry in _spawnedEntities)
            {
                var meta = _dbe.GetMetaByRouting(entry.Id.ArchetypeId);
                if (meta == null)
                {
                    continue;
                }
                var engineState = _dbe._archetypeStates[meta.ArchetypeId];
                if (engineState?.SlotToComponentTable == null)
                {
                    continue;
                }

                for (int slot = 0; slot < meta.ComponentCount; slot++)
                {
                    var table = engineState.SlotToComponentTable[slot];

                    if (table.StorageMode == StorageMode.Versioned)
                    {
                        // Versioned: the spawn's content chunk and chain — unless the rollback already freed them. RollbackComponent frees both for every
                        // Created entry and then drops it from the cache, so an entry still cached is one it never processed. Freeing them here regardless
                        // freed the content chunk twice (REAP-02).
                        var compType = meta._slotToComponentType[slot];
                        if (!_componentInfos.TryGetValue(compType, out var info) || !info.SingleCache.TryGetValue((long)entry.Id.RawValue, out var cri))
                        {
                            continue;
                        }

                        int chunkId = entry.VerLoc[slot];
                        if (chunkId > 0)
                        {
                            // CC-aware free: release any ComponentCollection buffers the rolled-back spawn chunk holds before freeing it.
                            DeferredCleanupManager.FreeContentChunk(table, chunkId, neverCommitted: true);
                        }

                        if (cri.CompRevTableFirstChunkId > 0)
                        {
                            table.CompRevTableSegment.FreeChunk(cri.CompRevTableFirstChunkId);
                        }

                        RefreshBetweenCommitSteps();
                    }
                    else
                    {
                        // SV/Transient: nothing to free — since #839 the payload is a slot in the transaction's spawn arena, which the reset drops wholesale.
                        // But the CC-aware half of the old FreeContentChunk call still has to happen: a rolled-back spawn that populated a ComponentCollection
                        // holds a VSBS buffer id in its payload, and dropping the arena would leak that buffer. DC-01 is [fatal][silent], so this is the one
                        // piece of the old free path that must survive the chunk's removal.
                        int stage = entry.Stage[slot];
                        if (stage != 0 && table.HasCollections)
                        {
                            DeferredCleanupManager.ReleaseCollectionBuffers(
                                table, new ReadOnlySpan<byte>(SpawnArena.Resolve(stage), table.ComponentOverhead + table.ComponentStorageSize));
                        }
                    }
                }
            }
        }

        // Rollback Versioned writes (copy-on-write): free chunks allocated by AddCompRev
        if (State != TransactionState.Committed && _componentInfos.Count > 0)
        {
            foreach (var kvp in _componentInfos)
            {
                var info = kvp.Value;
                if (info.ComponentTable.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }

                if (info.SingleCache != null)
                {
                    foreach (var cacheKvp in info.SingleCache)
                    {
                        var cri = cacheKvp.Value;

                        // Free copy-on-write chunks (Updated but not Created — Created chunks are freed above)
                        if ((cri.Operations & ComponentInfo.OperationType.Updated) != 0 &&
                            (cri.Operations & ComponentInfo.OperationType.Created) == 0 &&
                            cri.CurCompContentChunkId > 0)
                        {
                            // CC-aware free: release the cloned ComponentCollection buffer of the rolled-back COW chunk (the committed head keeps its own).
                            DeferredCleanupManager.FreeContentChunk(info.ComponentTable, cri.CurCompContentChunkId, neverCommitted: true);
                        }
                    }
                }
            }
        }

        _spawnedEntities?.Clear();
        _spawnedEntityIndex?.Clear();
        _pendingDestroys?.Clear();
        _pendingDeathClusters?.Clear();
        _pendingEnableDisable?.Clear();
        _firstSupplies?.Clear();
    }
}
