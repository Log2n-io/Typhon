# Spatial Index Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-04-10 |
| Domain | Spatial R-Tree, Queries, Trigger Volumes, Interest Management, Spatial Tiers (Clusters, Dormancy, Checkerboard, Migration) |

> Invariants that ensure spatial query correctness, tree structural integrity,
> consistent interaction with the ECS lifecycle, and spatial-tier cluster management
> (cell mapping, tier indexing, dormancy, checkerboard dispatch, migration dirty bits).

---

## Module: R-Tree Structure

### ST-01: Node MBR correctness `[fatal][silent]`
  invariant ∀ leaf L: L.NodeMBR == union(L.entries[0..count-1].coords)
  invariant ∀ internal I: I.NodeMBR == union(I.children[0..count-1].NodeMBR)
  invariant ∀ internal I, ∀ i: I.entries[i].coords == I.children[i].NodeMBR
    (the second invariant is only reachable through this one — RefitInternalMBR unions I's OWN entry
     array, so a caller that refits without first refreshing the changed entry produces a too-tight I)
  scope: every caller that refits an internal node — SpatialRTree.PropagateSplit (both the current-level
    absorb and the ancestor loop), RefitAncestors, RefitAncestorsBottomUp, WriteInternalEntry, BulkLoad,
    RemoveEmptyLeaf, SplitInternalNode
    NOT the SpatialNodeHelper primitives (RefitLeafMBR / RefitInternalMBR / ExpandLeafMBR): all three are
    correct in isolation, and scoping the rule to them is precisely what let #588 pass a scope-driven
    review — the invariant lives or dies in the callers
  on_violation:
    MBR too tight → queries miss entities (false negatives, silent data loss for game logic)
    MBR too loose → unnecessary subtree visits (performance only)
  detection: TreeValidator.ValidateInternalEntryFreshness. Recomputing a node's MBR from its own entries
    (ValidateMBRTightness alone) CANNOT see staleness — it compares a value against itself. Violations are
    also transient: the next insert descending the same path refreshes the entry and heals it, so an
    end-of-workload check comes back clean. Validate at checkpoints DURING mutation, never only after.

### ST-02: Union category mask — never under-represents `[perf]`
  invariant ∀ leaf L: L.UnionCategoryMask ⊇ OR(L.entries[0..count-1].CategoryMask)
  invariant ∀ internal I: I.UnionCategoryMask ⊇ OR(I.children[0..count-1].UnionCategoryMask)
  never UnionCategoryMask missing a bit that exists in a descendant entry
  scope: SpatialNodeHelper.RefitLeafMBR, SpatialRTree.RefitInternalUnionMask, RemoveEmptyLeaf
  on_violation: subtree pruning skips entities matching the query mask → false negatives
    (transient over-representation after remove is acceptable — causes extra visits, not missed results)

### ST-03: Parent pointer consistency `[fatal][silent]`
  invariant ∀ node N where N ≠ root: N.ParentChunkId points to an internal node containing N as a child
  invariant root.ParentChunkId == 0
  scope: SpatialRTree.Insert, Split, Remove, CreateNewRoot
  on_violation: RefitAncestorsBottomUp follows wrong chain; MBRs diverge from actual data;
    tree structure silently degrades over time

### ST-04: Entity count accuracy `[silent]`
  invariant _entityCount == sum(∀ leaf L: L.Count)
  requires: Interlocked operations on _entityCount
  scope: SpatialRTree.Insert, InsertWithSplit, Remove
  on_violation: kNN initial radius estimate degrades; TreeValidator reports inconsistency;
    persisted metadata disagrees with tree content

### ST-05: Back-pointer consistency `[fatal]`
  invariant ∀ entity E in tree: BackPointer[E.entityId] == (E.leafChunkId, E.slotIndex, treeSelector)
  invariant the back-pointer KEY is stable for the entity's lifetime — invariant under MVCC revision minting,
            cluster migration, and leaf-slot swap. A key that can be re-minted while the entity is alive is
            not a valid back-pointer key, however convenient it is to reach.
  scope: SpatialMaintainer.InsertSpatial / UpdateSpatialCore / RemoveFromSpatial, SpatialRTree.ScatterLeafEntries, Remove
  on_violation: the lookup misses, the update falls through to a fresh Insert, the prior leaf entry is orphaned →
    duplicate EntityIds in the tree (TreeValidator "R5 violation"); or update/remove targets the wrong leaf slot
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously keyed the back-pointer on `componentChunkId` — the key the
    implementation uses and the direct cause of confirmed bug #548. MVCC re-mints the content chunk id per revision, so
    for StorageMode.Versioned the lookup misses and every update double-inserts; SingleVersion is unaffected because
    its chunk id is stable. The design series specified `entityId` all along (03-tree-operations.md invariant B1,
    05-ecs-integration.md ReadBackPointer(entityId)). The rule had diverged from its own design and therefore RATIFIED
    the defect — which is why review never flagged the implementation. The key-stability clause is the missing half:
    re-keying alone states the what without the why, and the next storage mode reintroduces it.

### ST-06: OLC version validity `[fatal]`
  invariant ∀ node: OlcVersion ≥ 4 (version ≥ 1, lock=0, obsolete=0) when not write-locked
  never OlcVersion == 0 for an allocated, non-locked node
  scope: SpatialRTree.AllocNode, OlcLatch
  on_violation: queries see version 0 → restart loop; if node permanently stuck at 0 → infinite retry

---

## Module: Queries

### SQ-01: Query completeness — no false negatives `[fatal]`
  invariant ∀ query Q, ∀ entity E:
    E geometrically matches Q ∧ (Q.categoryMask == 0 ∨ (E.CategoryMask & Q.categoryMask) == Q.categoryMask)
    → E ∈ result set
  scope: SpatialRTree.Query.cs (all enumerators), CountInAABB
  on_violation: spatial query misses entities — game logic sees incomplete world state
  requires: ST-01 (MBR correctness), ST-02 (union mask not under-representing)

### SQ-02: Category mask semantics — AND-conjunctive `[fatal]`
  invariant categoryMask == 0 → no category filtering (all entities match)
  invariant categoryMask ≠ 0 → entry matches iff (entry.CategoryMask & categoryMask) == categoryMask
  never (entry.CategoryMask & categoryMask) != 0 treated as a match (that would be OR-disjunctive)
  scope: all query enumerators, CountInAABB leaf scan
  on_violation: queries return wrong entity set — wrong enemies targeted, wrong zones triggered

### SQ-03: Count query consistency `[fatal]`
  invariant CountInAABB(region, mask) == |{ E : E ∈ QueryAABB(region, mask) }|
  scope: SpatialRTree.CountInAABB, AABBQueryEnumerator
  on_violation: count disagrees with materialized query — game logic makes wrong density decisions

### SQ-04: Subtree counting shortcut correctness `[fatal]`
  invariant fully-contained flag propagation: node fully contained → all descendants fully contained
  invariant fullyContained ∧ categoryMask == 0 → count += nodeCount (no per-entry work)
  invariant fullyContained ∧ categoryMask ≠ 0 → skip overlap tests, still check per-entry category mask
  never fullyContained ∧ categoryMask ≠ 0 → count += nodeCount (would over-count)
  scope: SpatialRTree.CountInAABB
  on_violation: count is wrong — over-count if category check skipped, under-count if entries missed

### SQ-05: Traversal buffer safety `[silent]`
  invariant stackTop < 256 for all DFS-based queries
  invariant RayEnumerator never drops a child that hits within maxDist while below MaxRayHeapCapacity
  scope: AABBQueryEnumerator, OccupantQueryEnumerator, FrustumEnumerator, CountInAABB, RayEnumerator
  on_violation: children silently dropped → incomplete results with no error indication.
    The overflow branch records SpatialRTreeDiagnostics.RecordDfsStackOverflow — an always-on counter, deliberately
    non-throwing because an optimistic read latch is held. Corrected 2026-07-27: this rule previously said
    "Debug.Fail fires in debug builds only"; there are no Debug.Fail calls on this path, and the drop is observable
    in every build via that counter.
  ray heap (added 2026-07-31, #589): the ray priority queue is NOT a fixed buffer. Its 64-entry inline array is a
    fast path that spills to pooled arrays on demand, so a dense scene stays complete; only MaxRayHeapCapacity
    (a corrupt/cyclic-tree backstop, not a scene limit) drops children, and it records like the DFS sites.
    Ordinary growth increments RayHeapSpillCount — a perf signal, not a correctness one.
    The rule previously scoped only the four DFS enumerators, so nothing covered the ray path at all: it folded
    `_heapSize < 64` into its push condition with no else and no counter, and a dense scene silently lost ~80% of
    its hits. Frontier size is NOT bounded by tree depth — a ray whose subtrees share an entry distance holds them
    all pending at once — so no depth-derived constant may be used to bound it.
  detection: a scene that partitions ALONG the ray axis cannot reach this. Front-to-back popping then drains
    depth-first (siblings get well-separated entry distances) and the frontier stays at 5-15 nodes, which is why
    the pre-existing 200-entity ray test never filled the heap. Partition PERPENDICULAR to the ray so every node
    shares an entry distance and siblings pile up unconsumed.

---

## Module: Fat AABB Updates

### SF-01: Fat AABB containment — fast vs slow path `[perf]`
  invariant E.tightAABB ⊂ E.fatAABB → no tree mutation (fast path, ~25ns)
  invariant E.tightAABB ⊄ E.fatAABB → remove + reinsert with new fat AABB (slow path, ~500ns)
  scope: SpatialMaintainer.UpdateSpatial
  on_violation: entity position diverges from tree position; queries return stale spatial data

### SF-02: Static tree skip `[perf]`
  never tick fence / batch update processing visits static tree entities
  scope: SpatialMaintainer, SpatialIndexState
  on_violation: wasted CPU on immutable data; potential accidental mutation of bulk-loaded tree

---

## Module: Trigger Volumes

### TV-01: Event completeness `[fatal]`
  invariant ∀ entity transition (outside→inside) between consecutive evaluations → exactly one Enter event
  invariant ∀ entity transition (inside→outside) between consecutive evaluations → exactly one Leave event
  never an entity inside at both evaluations produces Enter or Leave (only Stay if subscribed)
  scope: SpatialTriggerSystem.EvaluateRegion
  on_violation: game logic misses zone transitions or fires duplicate events

### TV-02: Frequency contract `[perf]`
  invariant region with EvaluationFrequency = N evaluated at most once every N ticks
  scope: SpatialTriggerSystem.Evaluate
  on_violation: region evaluated too often (wasted CPU) or not often enough (missed transitions)

---

## Module: Interest Management

### IM-01: No missed changes `[fatal]`
  invariant ∀ entity E mutated at tick T, ∀ observer O where O.LastConsumedTick < T ≤ currentTick:
    E within O.InterestRegion ∧ (E.CategoryMask & O.CategoryMask) matches
    → E ∈ O.ChangeBuffer
  scope: SpatialInterestSystem.GetSpatialChanges
  on_violation: observer misses entity update → client sees stale state → desync

### IM-02: Ring buffer safety `[fatal]`
  invariant currentTick - observer.LastConsumedTick > RingSize → observer flagged for full sync
  never stale (recycled) bitmaps used for dirty accumulation
  scope: SpatialInterestSystem.GetSpatialChanges, DirtyBitmapRing
  on_violation: observer uses recycled bitmap data → phantom changes or missed changes

### IM-03: SV-only scope `[fatal]`
  invariant interest management dirty tracking only applies to SingleVersion ComponentTables
  never Versioned tables participate in ring buffer system
  scope: SpatialInterestSystem
  on_violation: DirtyBitmap infrastructure doesn't exist for Versioned → crash or undefined behavior

---

## Module: Cluster Spatial AABBs (Issue #230)

### CA-01: Per-cluster AABB containment `[fatal][silent]`
  invariant ∀ active cluster C with ClusterAabbs[C.chunkId] = A:
    A ⊇ union(∀ occupied entity E in C: E.spatialAABB)
    (degenerate entities with NaN/Inf bounds are excluded from the union)
  invariant RecomputeClusterAabb scans all occupied slots via TZCNT loop
    over the 64-bit occupancy word and calls ReadAndValidateBoundsFromPtr per slot
  post after the refresh pass: ∀ cluster C selected by ClusterProcessBitmap:
    ClusterAabbs[C.chunkId] ⊇ union of C's live entity AABBs, and == the exact union when a shrink was pending
    (corrected 2026-07-28: the pass no longer reads dirtyBits — it is driven by ClusterProcessBitmap +
     ClusterShrinkPendingAxes; and on the common grow path the CAS-grown superset is kept, never re-tightened,
     so "exact union" holds only when shrinkMask != 0)
  invariant 🔴 ClusterAabbs[chunkId] has EXACTLY ONE writer class at any instant:
    (a) CAS-grow from ClusterRef.WriteSpatial / MaybeGrowAndFlagShrink, during system dispatch
    (b) blind full-struct store from the AabbRefresh fence phase
    The tick barrier separating dispatch from the fence is what makes (b) safe, and it is LOAD-BEARING. Relax it and
    the fence store silently discards concurrent grows → AABB too tight → this rule's own containment fails.
  scope: ArchetypeClusterState.RecomputeClusterAabb, RecomputeDirtyClusterAabbs, RecomputeDirtyClusterAabbsSlice
         (the parallel path that performs the store), RebuildClusterAabbs,
         ClusterRef.WriteSpatial / MaybeGrowAndFlagShrink (the concurrent writer class)
  note the store is a blind `stored = fresh`, not a grow-merge (issue #573). Safe only under the barrier above; any
       overlap scheme must make it a union first. Neither ArchetypeClusterState nor TyphonRuntime contains a single
       Volatile, so the cluster-side read path is not a valid lock-free protocol on a weak memory model regardless of
       load order — arm64 is a supported target.
  on_violation:
    AABB too tight → per-cell cluster spatial queries miss entities (false negatives, silent)
    AABB too loose → extra overlap tests (performance only)
  requires: occupancy word accurately reflects live slots

---

## Module: ClusterCellMap (Issue #229)

### CC-01: ClusterCellMap validity `[fatal]`
  invariant ∀ active cluster C:
    ClusterCellMap == null ∨ C.chunkId ≥ ClusterCellMap.Length ∨
    ClusterCellMap[C.chunkId] ∈ [-1, CellCount)
  invariant ClusterCellMap[chunkId] ≥ 0 → cluster is assigned to a valid cell
  invariant ClusterCellMap[chunkId] < 0 → cluster is unassigned (skipped by TierClusterIndex)
  scope: ArchetypeClusterState.ClaimSlotInCell, RebuildCellState, TierClusterIndex.Rebuild
  on_violation:
    cellKey ≥ CellCount → IndexOutOfRangeException in SpatialGrid.GetCell
    cellKey corrupted → cluster bucketed into wrong cell → wrong tier assignment, wrong query results

---

## Module: TierClusterIndex (Issue #231)

### TI-01: Rebuild-before-dispatch ordering `[fatal]`
  invariant [RebuildIfStale] → [any parallel system reads per-tier arrays]
  invariant RebuildIfStale runs single-threaded at tick start (BuildTierIndexesAtTickStart)
    before any parallel system dispatch begins
  invariant Debug: Interlocked.CompareExchange(_rebuildInProgress, 1, 0) == 0
    asserts no concurrent Rebuild calls
  scope: TierClusterIndex.Rebuild, TierClusterIndex.RebuildIfStale, TyphonRuntime.BuildTierIndexesAtTickStart
  on_violation: parallel readers see partially-written tier arrays → torn reads, wrong cluster lists,
    clusters dispatched to wrong systems

### TI-02: Single-bit tier byte at rebuild `[fatal]`
  invariant ∀ cell processed during Rebuild:
    BitOperations.PopCount(cell.Tier) == 1
  invariant Debug.Assert validates single-bit at rebuild (TZCNT maps directly to array index)
  requires: SC-01 (SetCellTier rejects multi-bit flags)
  scope: TierClusterIndex.Rebuild
  on_violation: PopCount > 1 → TZCNT maps to wrong tier index → cluster assigned to wrong tier array

### TI-03: TierVersion monotonicity `[silent]`
  invariant _tierVersion only increments (never decremented or reset to 0)
  invariant SetCellTier: no-op when cell.Tier == (byte)tier (no spurious version bump)
  invariant ResetAllTiers: bumps _tierVersion at most once, only when at least one cell actually changed
  scope: SpatialGrid.SetCellTier, SpatialGrid.ResetAllTiers, SpatialGrid.SetCellTierMin
  on_violation:
    spurious bumps → unnecessary TierClusterIndex rebuilds (perf only, not correctness)
    missed bumps → TierClusterIndex uses stale tier assignment → systems process wrong clusters

---

## Module: Migration Dirty Bits (Issue #232)

### MD-01: Post-migration dirty bit consistency `[fatal][silent]`
  invariant after ExecuteMigrations for each migrated entity (src → dst):
    dirtyBits[srcChunkId] bit srcSlot is cleared
    dirtyBits[dstChunkId] bit dstSlot is set
  invariant if dstChunkId ≥ dirtyBits.Length:
    Array.Resize(ref dirtyBits, max(dirtyBits.Length * 2, dstChunkId + 1))
  invariant caller's ref parameter is updated so subsequent readers
    (DirtyRing archive, WAL publish loop, PreviousTickDirtySnapshot) see the grown array
  scope: DatabaseEngine.ExecuteMigrations (lines 1630-1645)
  on_violation:
    source bit not cleared → WAL serializes stale/zeroed source slot → corrupt replay
    dest bit not set → WAL misses new slot data → entity lost on crash recovery
    array not grown → IndexOutOfRangeException or silent skip of new destination cluster

### MD-02: Parallel migration apply — concurrent-mutation primitives `[fatal][silent]`
  invariant during ExecuteMigrationsSlice (parallel-fence Migrate phase, multiple workers per archetype):
    src occupancy bit clear uses Interlocked.And on the cluster's u64 occupancy word
    src per-component EnabledBits clear uses Interlocked.And per component slot
    src cell.EntityCount decrement uses Interlocked.Decrement(ref CellState.EntityCount)
    dst cell.EntityCount / ClusterCount increment uses Interlocked.Increment(ref CellState.*)
    src/dst dirtyBits flips use Interlocked.And / Interlocked.Or on the per-cluster long word
    cluster-fully-drains path (popcount(prev & ~mask) == 0) enters per-archetype _finalizeLock
      exclusive section before CellClusterPool.RemoveCluster, RemoveClusterFromPerCellIndex,
      RemoveFromActiveList, ClusterSegment.FreeChunk
  invariant FenceDirtyBits is pre-sized to PrimarySegmentCapacity + PendingMigrationCount
    in PrepareArchetypeFence's tail BEFORE any Migrate-phase worker observes it — no Array.Resize
    from worker threads
  invariant PendingMigrations is sorted by DestCellKey (SortPendingMigrationsByDestCellKey)
    by TickDriver between Prep and Migrate dispatches, so each worker slice owns disjoint dst cells
  invariant PendingMigrationCount = 0 reset happens once per fence in FinalizeArchetypeFence
    AFTER all Migrate-phase slices complete, never inside ExecuteMigrationsSlice
  scope: DatabaseEngine.ExecuteMigrations, ArchetypeClusterState.ClearSlotMetadata,
    ReleaseSlot (Persistent + Transient overloads), DecrementCellEntityCountOnRelease,
    FinaliseEmptyClusterCellState, ClaimSlotInCell (both overloads)
  on_violation:
    plain ++/-- on cell counters → torn updates across workers → drift in EntityCount/ClusterCount
    plain occupancy clear → lost concurrent slot release → ghost entity in cluster
    finalize without lock → CellClusterPool race → corrupted per-cell cluster list
    Array.Resize from worker → lost Interlocked.Or writes from siblings holding old reference

### MD-03: False-sharing avoidance for concurrent-mutation state `[perf][fatal]`
  invariant any data structure mutated concurrently from multiple workers isolates each
    independently-mutated element onto its own ≥64-byte cache line
  forbid bit-packed latch arrays (one bit per latch in a shared long[]) — adjacent latches
    share cache lines → catastrophic ping-pong on every CAS even under no logical contention
  prefer AoS (cluster all per-element state into one padded struct) over SoA + parallel
    padded arrays when padding is required — same memory cost, fewer cache-line fetches
  note CellState is [StructLayout(Explicit, Size = 64)] with 12 bytes of fields + 52 reserved
    (corrected 2026-07-27: previously stated 16 bytes of fields, which does not sum to 64)
  canonical example: CellState (16 bytes of fields, [StructLayout(Explicit, Size=64)],
    52 bytes reserved tail) — Tier, Flags, EntityCount, ClusterCount all on one line per cell
  applies to: CellState array, ArchetypeClusterState._finalizeLock (PaddedFinalizeLock 64B
    struct), any future per-cell or per-cluster latch arrays
  scope: SpatialGrid._cells, ArchetypeClusterState._finalizeLock, any new per-element
    Interlocked-mutated array
  on_violation:
    bit-packed latches → 8× ping-pong amplification, parallel speedup collapses
    16-byte cell descriptors in flat array → 4-cell ping-pong per migration
    unpadded shared latch field → adjacent hot fields invalidate the line on every acquire

---

## Module: Dormancy (Issue #233)

### DM-01: Wake guarantee — max one-tick latency `[fatal]`
  invariant ∀ cluster C in Sleeping state:
    SetDirty(C.chunkId, _) → DormancyReporter.RequestWake(archetypeId, C.chunkId)
  invariant DormancyReporter.DrainAll runs single-threaded at tick fence (WriteClusterTickFence),
    processes all thread-local wake requests, calls ProcessWakeRequest per entry
  invariant ProcessWakeRequest: Sleeping → WakePending (no-op if already WakePending)
  invariant TransitionWakePendingToActive: WakePending → Active at next tick start
    (BuildTierIndexesAtTickStart, before tier index rebuild)
  post maximum latency: dirty write at tick T → WakePending at tick T fence → Active at tick T+1 start
  scope: ArchetypeClusterState.SetDirty, DormancyReporter.RequestWake, DrainAll,
    ArchetypeClusterState.ProcessWakeRequest, TransitionWakePendingToActive
  on_violation: sleeping cluster with dirty writes never wakes → entity changes never dispatched to systems

### DM-02: SleepingClusterCount consistency `[fatal]`
  invariant SleepingClusterCount == |{ C ∈ ActiveClusterIds : SleepStates[C] ∈ {Sleeping, WakePending} }|
  invariant incremented: Active → Sleeping (DormancySweep, counter ≥ SleepThresholdTicks)
  invariant decremented: WakePending → Active (TransitionWakePendingToActive)
  invariant decremented: RemoveFromActiveList when SleepStates[chunkId] ∈ {Sleeping, WakePending}
  invariant SleepingClusterCount == 0 → all dormancy filtering in OnParallelQueryPrepare is skipped
    (zero overhead fast path)
  scope: ArchetypeClusterState.DormancySweep, TransitionWakePendingToActive, RemoveFromActiveList
  on_violation:
    count too high → false filtering, active clusters skipped by systems
    count too low → sleeping clusters dispatched to systems (wasted CPU)
    count stuck > 0 → dormancy filter overhead even when no clusters are sleeping

### DM-03: Sleep counter overflow guard `[perf]`
  invariant SleepCounters is ushort[] (range [0, 65535])
  invariant SleepThresholdTicks clamped to [0, ushort.MaxValue] via property setter:
    _sleepThresholdTicks = Math.Clamp(value, 0, ushort.MaxValue)
  invariant counter can never exceed ushort.MaxValue because transition fires at SleepThresholdTicks
    (which is ≤ ushort.MaxValue), so the counter is consumed before overflow
  scope: ArchetypeClusterState.SleepThresholdTicks (property), DormancySweep
  on_violation: counter wraps to 0 → cluster oscillates between Active and Sleeping every 65536 ticks
    instead of staying asleep

---

## Module: Checkerboard Partition (Issue #234)

### CB-01: Exhaustive disjoint partition `[fatal]`
  invariant ∀ filtered cluster set S for a checkerboard system:
    Red ∪ Black == S ∧ Red ∩ Black == ∅
  invariant Red = { C ∈ S : (cellX + cellY) % 2 == 0 } where (cellX, cellY) = grid.CellKeyToCoords(ClusterCellMap[C])
  invariant Black = { C ∈ S : (cellX + cellY) % 2 == 1 }
  invariant fallback: ClusterCellMap == null ∨ grid == null → Red = S, Black = ∅
    (non-spatial archetype degenerates to single-phase dispatch)
  invariant unmapped cluster (cellKey < 0) → assigned to Red (fallback)
  scope: TyphonRuntime.SplitCheckerboardClusters
  on_violation:
    non-exhaustive → some clusters never processed → entity state stale
    non-disjoint → some clusters processed twice → double-apply side effects

### CB-02: Two-phase dispatch protocol `[fatal]`
  invariant phase 0 → 1: split into Red/Black, serve Red cluster list
  invariant phase 1 → 2: serve Black cluster list (triggered by re-dispatch after Red completes)
  invariant phase 2 → 0: reset for next tick
  never phase 0 serves Black (Black only served after Red completes)
  scope: TyphonRuntime.OnParallelQueryPrepare (checkerboard section, phase 0→1 / 1→2),
         TyphonRuntime.OnParallelQueryCleanup (phase 2→0 reset + Red→Black re-dispatch)
  note corrected 2026-07-27 — `OnParallelQueryEnd` does not exist in the engine
  on_violation: both phases see same partition → clusters processed twice or zero times

---

## Module: SetCellTier Validation (Issue #231)

### SC-01: Single-bit SimTier enforcement `[fatal]`
  invariant SetCellTier(cellKey, tier): tier must be SimTier.None or a single-bit flag
  invariant tier ≠ SimTier.None ∧ ¬tier.IsSingleTier() → throw ArgumentException
  invariant TierClusterIndex.Rebuild Debug.Assert validates PopCount(cell.Tier) == 1
    for every non-zero tier byte encountered
  never a CellDescriptor.Tier byte has more than one bit set
  scope: SpatialGrid.SetCellTier, SpatialGrid.SetCellTierMin, TierClusterIndex.Rebuild
  on_violation: multi-bit tier stored → TZCNT at rebuild produces wrong index →
    cluster routed to wrong tier array → system processes wrong cluster set
