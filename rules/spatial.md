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
  invariant ∀ payload P in tree: BackPointer[P.payloadId] == (P.leafChunkId, P.slotIndex, treeSelector)
  invariant 🔴 the back-pointer array is the SOLE store of a payload's location, not a repair channel. EVERY path
            that places or moves an entry writes it — Insert for a new entry, ScatterLeafEntries for a split,
            Remove for the swap-with-last and the retirement. Miss the Insert and the array is silently
            incomplete: a caller must then merge it with Insert's return value, and the moment it keeps its own
            copy that copy goes stale the first time ANOTHER payload's split or removal relocates this one. The
            failure that follows is not a missed lookup — TryUpdateLeafEntryInPlace refuses on the identity
            check, which is the check working, and the escape path underneath then removes at the stale
            location, which is a live entry belonging to somebody else. (#872 step 9; found by measurement, not
            by review.)
  invariant the back-pointer KEY is stable for the payload's lifetime — invariant under MVCC revision minting,
            cluster migration, and leaf-slot swap. A key that can be re-minted while the payload is alive is
            not a valid back-pointer key, however convenient it is to reach.
  note 🔴 the key is a PAYLOAD id, not necessarily an entity id. SpatialRTree is generic over what it indexes:
       the entity-level trees keyed on EntityId, the per-cell cluster trees of #872 step 9 key on a CLUSTER chunk
       id. The field was called EntityId until step 9 renamed it to PayloadId, which read as a type guarantee
       it never made. Since #872 step 13 there is only the cluster kind — see SH-01 — so the payload id is
       always a cluster chunk id and PayloadBackPointers is the only back-pointer store left.
  scope: SpatialRTree.Insert (the new-entry write), ScatterLeafEntries, Remove,
         CellClusterTree.Add / UpdateAt / RemoveAt (which read the array rather than holding a handle)
  on_violation: the lookup misses, the update falls through to a fresh Insert, the prior leaf entry is orphaned →
    duplicate EntityIds in the tree (TreeValidator "R5 violation"); or update/remove targets the wrong leaf slot
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously keyed the back-pointer on `componentChunkId` — the key the
    implementation uses and the direct cause of confirmed bug #548. MVCC re-mints the content chunk id per revision, so
    for StorageMode.Versioned the lookup misses and every update double-inserts; SingleVersion is unaffected because
    its chunk id is stable. The design series specified `entityId` all along (03-tree-operations.md invariant B1,
    05-ecs-integration.md ReadBackPointer(entityId)). The rule had diverged from its own design and therefore RATIFIED
    the defect — which is why review never flagged the implementation. The key-stability clause is the missing half:
    re-keying alone states the what without the why, and the next storage mode reintroduces it.

### ST-07: Escape-bound in-place update `[fatal][silent]`
  invariant TryUpdateLeafEntryInPlace writes ONLY when all three hold: the target is a leaf, the slot is live,
    and the entry's payload id equals the caller's. The identity check is not defensive coding — without it a
    stale handle writes cluster X's bounds into cluster Y's slot, breaking CA-01 in both directions with no
    exception raised anywhere near the cause.
  invariant it writes only when the new bounds are CONTAINED by the leaf's current MBR on every axis. That is
    what makes skipping the refit sound: an entry that stays inside its leaf cannot change the union's outer
    edge outward, so no ancestor can become too tight.
  invariant 🔴 the handle comes from PayloadBackPointers, never from a caller-held copy — see ST-05. The escape
    path removes at the handle it was given, so a handle that has gone stale removes a stranger.
  invariant 🔴 ST-01's leaf equality is SUSPENDED between an in-place write and the end-of-pass refit, and only
    there. Not refitting is the entire saving, and its cost is that the leaf MBR becomes a strict SUPERSET of
    the union of its entries. Too-loose is ST-01's performance-only direction, so the window is safe — but it
    must not outlive the exclusive window, because a query running against a loose MBR is merely slower while
    a REBUILD or a validator run against one reports a violation that is real.
  post after the pass closes: ∀ leaf touched by an in-place update, L.NodeMBR == union(L.entries) once more,
    i.e. ST-01 holds unconditionally outside the window.
  scope: SpatialRTree.TryUpdateLeafEntryInPlace, CellClusterTree.UpdateAt
  verified: CellClusterTreeDifferentialTests (EscapeBoundUpdate_KeepsTheTreeAgreeingWithTheScan carries the
    attribute; HandlesStayValid_AcrossUpdatesAndRemovals covers the handle half)
  on_violation:
    wrote through a stale handle → two clusters hold each other's bounds → CA-01 fails silently, SQ-01 false
      negatives for both, and TreeValidator passes throughout because the TREE is structurally perfect —
      only the addresses held outside it are wrong
    refit skipped and never made up → ST-01 equality permanently false; queries stay correct, validators do not
  requires: ST-05 (the handle is where the array says it is)

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
  invariant 🔴 ∀ two enumerators live on one thread at once: their traversal stacks are DISTINCT arrays
  scope: AABBQueryEnumerator, FrustumEnumerator, CountInAABB, RayEnumerator, QueryStackPool
  pooled stack (added 2026-09-08, #916 O1): AABBQueryEnumerator's stack is no longer the inline
    QueryStackBuffer. It is an int[256] rented from QueryStackPool, a per-thread FREE LIST, and returned in
    Dispose. Capacity is unchanged, so the 256 bound above still reads against the same number — PushChild
    tests QueryStackPool.Capacity.
    The distinctness invariant is new and is the whole reason the pool is a list rather than one buffer.
    Nested spatial queries are legal — query A, and for each hit query B — and were safe by CONSTRUCTION while
    each enumerator embedded its own 1 KB array. A single [ThreadStatic] buffer does not crash: the inner query
    overwrites the outer's stack, the outer resumes describing a different subtree, and it returns a SUBSET.
    That is an SQ-01 false negative arriving through this rule, which is why it is 🔴 and not a note.
    Two mechanisms hold it, and both are needed. (1) The enumerator rents LAZILY, on first descent rather than
    in the constructor, because GetEnumerator() returns a COPY — a constructor-time rent would put one array on
    both the copy and the discarded original. (2) Every buffer carries an ownership TOKEN past its DFS slots
    (QueryStackPool.TokenSlot); a rent stamps a fresh value and hands the same value out, and Return is accepted
    only while the two agree, zeroing the stamp on the way.
    An identity scan of the free list is NOT sufficient and was the first attempt: it catches a double return
    only while the buffer is still parked, and misses the case that matters — a copy that already rented returns
    the buffer, another query rents it, then the original returns it again. At that moment the buffer is
    legitimately on loan, so the scan finds nothing and parks a stack that is still being written. The token
    fails that return on the stamp instead. No in-repo caller does this today, but AabbClusterEnumerator is
    public and reaches game code through ClusterSpatialQuery, so "no caller does that" is not load-bearing.
    Buffers are returned DIRTY and must stay that way. The DFS protocol writes a slot before reading it and
    stackTop is the only liveness marker, so zeroing on rent or return would reintroduce the 1 KB memset the
    change exists to remove (measured: query setup 75.45 -> 58.09 ns on an M4, medians of interleaved sets).
    verified by: QueryStackPoolTests (incl. StaleReturnAfterReRentIsIgnored),
    CellTreePromotionTests.NestedQueriesOverPromotedCells_AnswerAsTheyDoAlone,
    CellTreePromotionTests.WarmQueryPathAllocatesNothing_AndNeverOverflowsTheStack
  note OccupantQueryEnumerator was a fifth DFS enumerator here until #872 step 13. It yielded the payload id AND
       the owning component's chunk id, which only the entity-level tree could supply; its last two callers were
       the interest and trigger systems' entity-tree paths, removed with that tree.
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

### SQ-06: The cluster query answers at the tier's declared width, in both frames `[fatal][silent]`
  invariant 🔴 an archetype's dimensionality is DERIVED from its SpatialFieldType, never enumerated:
    SpatialFieldTypeExtensions.Is3D(fieldType), not `fieldType == AABB3F || fieldType == BSphere3F`
    the enumerated form was correct only while ValidateSupportedFieldType rejected the f64 tiers. Opening that
    gate (#914) turned all nine sites that spelled it that way into a silent misclassification: an AABB3D
    archetype reads as 2D, AabbClusterEnumerator pins _cellMinZ = _cellMaxZ = grid.FlatPlaneZ, the walk sweeps
    ONE Z plane of a deep grid, and every entity elsewhere on Z is never visited. SQ-01's own failure mode,
    reached through a predicate rather than through the traversal.
  invariant the query runs in TWO frames and each has its own width, deliberately:
    broadphase — cluster bound vs query box, CELL-RELATIVE f32 (C15). SetCellQueryFrame converts once per cell;
                 the ray and kNN paths convert the CLUSTER bound outward instead, through ToWorldExact.
    narrowphase — entity bound vs query box, WORLD f64. Both sides come from the component and the caller
                  unnarrowed; ReadAndValidateBoundsFromPtr has always produced doubles.
    never narrow the world frame to f32 anywhere on the query path, in ANY of the four shapes — AABB, radius, ray,
    frustum, kNN. At 2^36 one f32 step is 8 192 units — wider than a 1 000-unit cell — so an f32 narrowphase
    compares two coordinates that are the SAME number and accepts every entity in the cell. That is a false
    POSITIVE set containing the true one, so SQ-01 still holds and nothing throws; the query simply stops
    discriminating, which is why this is [silent]. The frustum's failure is coarser and worth stating separately:
    its planes were always f64, but its caller-supplied BOUNDING BOX resolves the cell range, so narrowing that
    collapses the range and skips whole cells of the view — an SQ-01 false negative, not a loss of discrimination.
  invariant an f32-TIER archetype may not be registered on a world f32 cannot address. The check is
    SpatialGrid.ValidateWorldExtentForFieldType, at InitializeArchetypes, and it is a configuration error rather
    than a runtime branch: the archetype's own component stores f32 WORLD coordinates, so once one f32 step at the
    world's extreme EXCEEDS the cell size, two entities a cell apart round to the same value and the grid files them
    together. Nothing downstream can repair that — the precision was gone before the engine saw the value. The
    remedy the message names is the f64 tier, not a wider internal representation.
    the line is `ulp_f32(extreme) < cellSize`, NOT "every cell origin is exactly representable in f32". The second
    is stricter and wrong: a world spanning 0.1..1000.1 with 100-unit cells has no exactly-representable origin and
    resolves ~1e-8, four orders finer than a cell. Failing startup for a world that works is the worse bug.
    an accepted world may still be COARSE and that is deliberate — at 2^30 with 1 000-unit cells an f32 step is 128,
    so the application's own component is quantised to 128 while the cell-relative stored bounds (C15) keep full
    resolution. That is the trade an f32 tier is, not a defect the engine should refuse.
  invariant a query box's tier must equal the archetype's storage tier — ClusterSpatialQuery.AABB<TBox> and the
    four Radius overloads throw InvalidOperationException rather than converting. An f32 box would widen
    implicitly and silently answer a different question at a precision the caller did not choose.
  invariant ClusterSpatialQueryResult carries WORLD f64 bounds for every tier. For an f32 tier the widening is
    exact, so an AABB2F archetype reads back precisely what it stored; for an f64 tier it is the only way the
    caller sees the coordinate the component holds.
  scope: SpatialFieldTypeExtensions.Is3D, AabbClusterEnumerator (constructor, MoveNext, SetCellQueryFrame),
         ArchetypeClusterState.QueryAabb, ArchetypeClusterState.QueryRadius, ArchetypeClusterState.QueryRay,
         ArchetypeClusterState.QueryFrustum, ArchetypeClusterState.QueryNearest,
         ClusterSpatialQuery`1.AABB, ClusterSpatialQuery`1.Radius,
         SpatialGrid.ReadSpatialCenter3D, SpatialGrid.ValidateSupportedFieldType,
         SpatialGrid.ValidateWorldExtentForFieldType, SpatialGrid.AxisIsResolvableInF32,
         ClusterSpatialAabb.ToWorldExact, ClusterWorldAabb, Vector3Like
  verified: F64SpatialTierTests — NarrowQueryBoxAtExtent_SelectsOneOfTwoEntitiesInTheSameCell,
    OneUnitQueryBoxAtExtent_ReturnsExactlyTheEntityInsideIt and ResultBoundsComeBackAtFullPrecision cover the
    width invariant for AABB; RayAtExtent_HitsTheEntityInItsPathAndNotTheOneBesideIt,
    FrustumAtExtent_SelectsTheHalfSpaceItNames_FromATightBoxSeveralCellsOut and
    KnnAtExtent_OrdersNeighboursByAnF64Distance cover the other three shapes;
    ZSeparatedEntities_AreBothFoundAndSeparatelySelectable covers the dimensionality one; and
    QueryingAnF64ArchetypeWithAnF32Box_StillThrows covers the tier check. F64WorldLifecycleTests covers the
    configuration gate (F32Archetype_OnAWorldF32CannotAddress_FailsAtStartup, and the two positive cases that keep
    it from being a blanket ban, one of which — F32Archetype_OnASmallWorldWithFractionalBounds_IsAccepted — is the
    regression guard for the over-strict first version) plus
    TheResolutionCriterion_AgreesWithWhetherF32CanSeparateAdjacentCells, which pins the O(1) criterion against the
    property it stands for, quantified over position rather than sampled at one point. Each precision case carries a PRECONDITION assertion
    that f32 cannot represent it, so a fixture that drifted to a smaller magnitude fails loudly instead of passing
    for the wrong reason.
  note no [RuleMutant]: every mutant for this rule is an EDIT TO ENGINE CODE — narrow the narrowphase, the ray
    origin, the kNN operands or the frustum's bounding box to f32; restore the two-way dimensionality test; make
    the extent check always pass — not an input that can be driven through a verifier's assertion path.
    All six were run by hand when the rule was written (2026-09-08) and each reddens exactly the cases above.
    RuleMutants.AssertDetects deliberately requires the verifier's own failure marker, and there is no such helper
    here to drive.
  note (performance, not correctness) ClusterSpatialQuery`1.AABB carries [AggressiveInlining] because adding the
    two f64 dispatch branches took its IL from 716 to 814 bytes. The JIT sizes an inlining candidate BEFORE folding
    the typeof(TBox) tests, so the specialised body stayed small while the method stopped being inlined — and it
    returns a 1.6 KB ref struct by value, so every query paid an extra full-struct copy. Measured interleaved
    against the pre-#914 build: +44 ns fixed per query, a tenth of a 3x3-cell query. Adding a further box variant
    spends more of that budget; re-measure if one is added.
  on_violation:
    dimensionality misread → a 3D f64 archetype answers only from one Z plane (SQ-01 false negative, silent)
    world frame narrowed → the query stops discriminating inside a cell; every hit, no error
    result narrowed → the caller reads a coordinate quantised to ~64-unit steps at 10^9 and cannot tell
  requires: C15 (stored bounds are cell-relative f32 — this rule is why that is not a limitation), CA-01

---

## Module: Fat AABB Updates

> 🔴 **RETIRED by #872 step 13, kept as a record of what the rules used to constrain.** `SF-01` and `SF-02`
> described the entity-level R-Tree's per-entity maintenance: a fat-AABB containment test deciding between an
> in-place update and a remove-plus-reinsert, and a static-mode skip at the tick fence. Both scoped
> `SpatialMaintainer` methods that had had no caller since #666 made every archetype cluster-backed, and step 13
> deleted them along with the tree. The equivalent invariants on the surviving mechanism are `ST-07` (the
> escape-bound in-place update, which is the same fast/slow split one level up, on CLUSTER bounds) and `CA-01`
> (containment). Nothing was weakened; the subject moved.

---

## Module: Trigger Volumes

### TV-01: Event completeness `[fatal]`
  invariant ∀ entity transition (outside→inside) between consecutive evaluations → exactly one Enter event
  invariant ∀ entity transition (inside→outside) between consecutive evaluations → exactly one Leave event
  never an entity inside at both evaluations produces Enter or Leave (only Stay if subscribed)
  invariant 🔴 occupancy is tracked by ENTITY ID, never by a bitmap over component chunk ids. Cluster storage has
            its own chunk-id namespace, so a bitmap indexed by the component table's would collide two different
            entities onto one bit and report neither transition. The bitmap form was correct for the entity-level
            tree, whose payload WAS a component chunk id; #872 step 13 removed that tree and with it the last
            thing that could populate one.
  scope: SpatialTriggerSystem.EvaluateRegion, CollectClusterOccupants
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

### IM-04: Both systems read the cluster index, and both are reachable from production `[fatal]`
  invariant SpatialInterestSystem and SpatialTriggerSystem resolve entities ONLY through the per-cell cluster
            index — QueryAabb for a region, the per-ARCHETYPE ClusterDirtyRing for a delta. Neither may acquire
            an entity-level index of its own (SH-01 forbids one existing).
  invariant 🔴 each has a PUBLIC entry point, and a test drives it through that entry point rather than through
            the internal factory. This is not a style preference: before #872 step 13 the only callers were tests
            and benchmarks reaching `GetOrCreate…System` directly, and the one production reference was a
            null-conditional read of a field production never assigned. A subsystem whose sole exercise is a test
            that constructs it by hand cannot be distinguished from a subsystem that has quietly stopped working,
            which is exactly what had happened: the half of each that read the entity tree had been querying an
            empty index since #666.
  scope: SpatialObserverExtensions.SpatialObservers, SpatialObserverExtensions.SpatialTriggers,
         SpatialInterestSystem.GetSpatialChanges, SpatialTriggerSystem.EvaluateRegion
  on_violation: an observer or region silently reports nothing, and no test notices because none reaches the code
    the way a caller would

---

## Module: Cluster Spatial AABBs (Issue #230)

### CA-01: Per-cluster AABB containment `[fatal][silent]`
  invariant 🔴 FRAME (#872 step 9, decision C15): A is stored CELL-RELATIVE — offsets from the world-space minimum
    corner of C's cell, which SpatialGrid.CellOrigin derives from ClusterCellMap[C.chunkId]. E.spatialAABB is
    world-space. The containment below therefore only holds once both sides are in the SAME frame, and the rule is
    read literally false for every cluster in a cell whose origin is non-zero if that is forgotten. C13
    (cluster→cell exclusivity) is what makes the origin unique per cluster and hence what makes the frame
    well-defined at all — CA-01 depends on C13 and not merely alongside it.
  invariant ∀ active cluster C with ClusterAabbs[C.chunkId] = A:
    toCellRelative(union(∀ occupied entity E in C: E.spatialAABB), origin(C)) ⊆ A
    (degenerate entities with NaN/Inf bounds are excluded from the union)
    conversion rounds AWAY from the entity — min down, max up (ClusterSpatialAabb.ToCellRelativeMin/Max). The
    narrowing to f32 is where the error is: round-to-nearest can place a bound INSIDE the entity it must contain,
    which is this rule's own silent failure mode.
  invariant 🔴 the QUERY side shares the frame or the rule is unobservable: AabbClusterEnumerator.SetCellQueryFrame
    converts the query box into the cell's frame with the OPPOSITE-signed rounding (outward on both sides). A box
    narrowed by one ULP there drops a cluster grazing its edge — an SQ-01 false negative that no containment check
    on the storage side can see.
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
         ClusterRef.WriteSpatial / MaybeGrowAndFlagShrink (the concurrent writer class),
         ClusterRef.TryGetCellOrigin, ClusterSpatialAabb.ToCellRelativeMin, ClusterSpatialAabb.ToCellRelativeMax,
         SpatialGrid.CellOrigin (the frame the containment is expressed in — C15),
         AabbClusterEnumerator.SetCellQueryFrame (the query half; without it the invariant is unobservable)
  note the store is a blind `stored = fresh`, not a grow-merge (issue #573). Safe only under the barrier above; any
       overlap scheme must make it a union first. Neither ArchetypeClusterState nor TyphonRuntime contains a single
       Volatile, so the cluster-side read path is not a valid lock-free protocol on a weak memory model regardless of
       load order — arm64 is a supported target.
  on_violation:
    AABB too tight → per-cell cluster spatial queries miss entities (false negatives, silent)
    AABB too loose → extra overlap tests (performance only)
  requires: occupancy word accurately reflects live slots

### CA-02: The per-cell index tracks ClusterAabbs, and equality is not the test for it `[fatal][silent]`
  invariant the broadphase prunes on the bound held by the PER-CELL structure — CellSpatialIndex.MinX[] et al, or
    the leaf entry in a promoted CellClusterTree — never on ClusterAabbs. So:
    ∀ active cluster C, after the AabbRefresh phase and its promoted-cell drain:
      indexBound(C) ⊇ ClusterAabbs[C.chunkId]
    Too loose costs an overlap test; too tight is an SQ-01 false negative that no containment check on ClusterAabbs
    can observe, because ClusterAabbs is correct in that failure.
  invariant 🔴 the fence may NOT decide whether to write the index by comparing ClusterAabbs against itself.
    CA-01 names two writer classes for ClusterAabbs; the index has ONE, the fence. When writer class (a) — the
    CAS-grow in ClusterRef.MaybeGrowAndFlagShrink — has already applied the grow, the fence's `stored == fresh`
    test answers "the fence learned nothing", which is TRUE and not the question. The index is still a tick behind.
    ClusterRef.MaybeGrowAndFlagShrink says so in its own summary: it returns true "in which case the cluster needs
    a fence-time PerCellIndex.UpdateAt with the fresh AABB".
    On the SpatialBarrierOnly branch the comparison was worse than wrong, it was a TAUTOLOGY: `fresh` is assigned
    from `stored` when no shrink is pending, so a grow-only tick could never update the index at all — and both
    demos run barrier-only.
  invariant the signal is ClusterProcessBitmap, which WriteSpatial sets on exactly `aabbChanged || migrationFlagged`
    and ClearAabbRefreshBookkeeping zeroes once per tick. A writer that leaves ClusterAabbs alone for the fence to
    recompute (OpenMut / GetSpan) sets no bit, which is precisely the case where equality does mean nothing changed.
  invariant 🔴 OUTSIDE the fence, on user threads (#872 step 15 review): a cluster is in its cell's index from the moment
    it is in the cell's pool — the allocation site adds it under _finalizeLock with an EMPTY box before AddCluster
    publishes it (AddClusterToPerCellIndexLocked) — and every spawner thereafter only WIDENS: ClusterAabbs by per-axis
    CAS (ClusterSpatialAabb.WidenCas2F/3F), the linear index slot by per-axis CAS that re-validates against a concurrent
    grow (CellSpatialIndex.WidenAt), a promoted half under _finalizeLock (WidenClusterInPerCellIndex). Before this a
    fresh cluster was published first, so two spawners both took the "first entity" branch — two index entries, one
    orphaned, and a reset that wiped the other's widening — and a plain-store widen landed in an array a grow had
    abandoned. Verified before any fence by
    ClusterPlacementTests.ConcurrentSpawnsIntoOneCellLeaveTheIndexExactBeforeAnyFence
  scope: ArchetypeClusterState.RecomputeDirtyClusterAabbsSlice, ArchetypeClusterState.IsClusterProcessBitSet,
    ArchetypeClusterState.ApplyOrDeferClusterUpdate, ArchetypeClusterState.UpdateClusterInPerCellIndex,
    ClusterRef.MaybeGrowAndFlagShrink
  verified: CellTreeParallelFenceTests.CellIndexTracksClusterAabbs_AfterAWriteTimeGrow (both slicing branches,
    50 serial fence ticks of rotation, queries compared against entity positions read straight out of cluster
    storage). Pre-fix it failed on both branches with the index one to two ticks inside ClusterAabbs on every axis.
  on_violation:
    index bound tighter than ClusterAabbs → the cell prunes a cluster the query overlaps → every entity in it
      disappears from the result, silently, and CA-01 holds throughout because ClusterAabbs is right
  requires: CA-01 (ClusterAabbs itself contains the entities)

---

## Module: VDB Cell Grid (Issue #872 step 8)

### VG-01: A cell key names a live cell, or nothing `[fatal][silent]`
  invariant a cell key is a POOL SLOT in SpatialGrid's CellState pool, not a coordinate:
    ∀ key k handed out by ComputeCellKey / WorldToCellKey / TryGetCellKey: k ∈ [0, SpatialGrid.CellCount)
    ∀ such k: CellKeyToCoords(k) == the (x, y, z) the key was resolved from
  invariant a coordinate with no cell resolves to ABSENT, never to another cell's key:
    TryGetCellKey(x, y, z, out k) == false → k < 0
    TryGetNeighbourCellKey over a block that has not been created → false, and true once it is
  invariant cell keys are NOT stable across a rebuild or a ResetCellState — slots are handed out in
    creation order, so nothing may cache a key across either
  invariant creation is monotonic: a cell, once created, is never removed or renumbered while the grid
    lives (step 8 ships no destruction path; §3.5's windowed sweep is deferred)
  scope: SpatialGrid.ComputeCellKey, SpatialGrid.TryGetCellKey, SpatialGrid.TryGetNeighbourCellKey,
    SpatialGrid.CellKeyToCoords, SpatialGrid.ResetCellState, VdbBlockKey.Pack
  verified: VdbSpatialGridTests (AC82_NeighbourAcrossAnAbsentBlockBoundary_IsAbsentThenAppears carries the
    attribute; ResetCellState_InvalidatesTheBlockCacheOnEveryThread and AC84_RebuildFromTheSamePopulation
    cover the stability and monotonicity clauses). VdbBlockKeyTests covers the packing clause but carries no
    attribute of its own — the packing is reached through every one of these.
  on_violation:
    a remembered "absent" that later has a cell → query misses every cluster in it → SQ-01 false negative
    a block key that truncates an axis → two regions alias one block → each query returns the other's clusters
    a cached key surviving a rebuild → counters read against a cell that now belongs to a different position

### VG-02: Read paths never create cells `[perf][fatal]`
  invariant only a path that must PLACE something resolves with create — entity spawn, migration
    destination, and the rebuild's serial reduce. Everything else uses TryGetCellKey:
    query broadphase (AabbClusterEnumerator), tier assignment (SetTierInAABB, the accessor's
    coordinate-keyed setters), and every diagnostic or demo sweep
  invariant the rebuild's PARALLEL map phase resolves to COORDINATES only (ReadCellCoordsFromSpatialField).
    Creating from the map would make each pool slot a function of the worker count, which
    RebuildSpatialStateFromData's serial reduce exists to prevent
  invariant SpatialGridAccessor.GetCell(x, y, z) is the ONE coordinate-keyed accessor that DOES create, and
    exists for tests and diagnostics that need a cell to write into. Game code reading grid state must not use it
  note the determinism guarantee is REBUILD-ONLY. Migration destinations are created from fence workers, so a
    pool slot's index depends on worker interleaving there; only RebuildSpatialStateFromData promises a
    worker-count-independent numbering, and it does so by creating in its serial reduce
  scope: AabbClusterEnumerator.MoveNext, SpatialGrid.SetTierInAABB, SpatialGridAccessor.SetCellTier,
    SpatialGridAccessor.SetCellTierMin, SpatialGridAccessor.ComputeCellKey, SpatialGridAccessor.WorldToCell,
    SpatialGridAccessor.GetCell, ArchetypeClusterState.MapClusterForRebuild
  verified: VdbSpatialGridTests (ReadPaths_DoNotCreateCells carries the attribute and covers
    SpatialGrid.TryGetCellKey / TryGetCellKeyAt / SetTierInAABB). The accessor surface, the query broadphase and
    the parallel map phase are NOT directly verified — they are covered only indirectly, by the suite staying
    green with sparse memory
  on_violation:
    a read path resolving with create → one cell per swept coordinate → the grid silently becomes dense
      again, with correct answers and the memory C2 exists to avoid. Nothing fails; ResidentBytes is the
      only observable
    creating from the parallel map → pool slots depend on thread interleaving → rebuild output stops being
      bit-identical across worker counts

---

## Module: Per-cell R-Tree promotion (Issue #872 step 9)

### PC-01: A promoted cell's tree has exactly one writer `[fatal][silent]`
  invariant SpatialRTree is single-writer by specification (ADR-044; O2 in
    claude/design/Spatial/SpatialIndex/03-tree-operations.md). Nothing in CellClusterTree adds a latch, so
    every mutation of a promoted cell half must be serialised by the CALLER
  invariant the fence's AabbRefresh phase slices by CLUSTER id, not by cell (FenceWorkPlan.EmitAabbRefreshSliceItems
    emits bitmap-word or active-index ranges), so two workers routinely carry clusters of one cell. Neither
    slicing branch may therefore write a promoted cell's tree:
    ∀ slice worker w, ∀ cluster c whose cell is promoted:
      w appends (c.chunkId, cellKey) to its OWN List<PromotedAabbApply> and writes nothing
      the buffer is merged under _finalizeLock (EnqueuePromotedAppliesBulk), one acquisition per slice
      DrainPromotedAabbApplies replays them on ONE thread, in FinalizeArchetypeFence, after the phase barrier
  invariant 🔴 the deferral is conditional on a sink being supplied, and the fallback is MANDATORY: a null
    buffer means the caller is already the single writer (the serial whole-archetype recompute), so the write
    happens inline. Written as `buffer?.Add(...)` with no else, a null sink DISCARDS the update — ClusterAabbs
    advances, the tree does not, and the two diverge into SQ-01 false negatives with nothing raised. That
    shape is reachable on the serial fence, where no sink is supplied
  invariant replay order is by cluster id, not arrival order. Arrival order depends on how the planner sliced
    and which worker finished first, so replaying in it would make the tree — and every handle in
    ClusterSpatialIndexSlot — a function of the worker count
  invariant promotion itself (MaybePromoteCellHalf) runs from AddClusterToPerCellIndex, which the parallel
    Migrate phase reaches. Cell-disjoint slicing does NOT protect the per-archetype resources it touches, and
    that is MD-02's concern rather than this rule's: the growers now serialise on _finalizeLock and refuse to
    reallocate from inside a slice, and TryEnsureCellTreeSegment creates the shared segment under the same
    latch. A startup guard used to refuse promotion alongside a parallel fence instead; it is gone, because
    what it was protecting is now protected
  invariant the tree build itself is PromoteCellHalf, reached from the gate (MaybePromoteCellHalf, under
    _finalizeLock) and from ForceCellHalfStructure — the #917 benchmark's in-place switch — which takes
    _finalizeLock itself, ensuring the tree segment BEFORE it (the latch is not re-entrant), and is called
    with no fence and no query in flight
  scope: ArchetypeClusterState.ApplyOrDeferClusterUpdate, ArchetypeClusterState.EnqueuePromotedAppliesBulk,
    ArchetypeClusterState.DrainPromotedAabbApplies, ArchetypeClusterState.UpdateClusterInPerCellIndex,
    ArchetypeClusterState.MaybePromoteCellHalf, ArchetypeClusterState.PromoteCellHalf,
    ArchetypeClusterState.ForceCellHalfStructure, ArchetypeClusterState.DemoteCellHalf
  verified: CellTreeParallelFenceTests (both slicing branches, 50 parallel-fence ticks with motion),
    CellTreeDensityTransitionTests (the switch in both directions, and promotion under a parallel fence with
    clusters migrating between cells). Ablated:
    reverting the divert to an unconditional UpdateClusterInPerCellIndex reddens it 3 runs of 3, on the
    membership comparison — "cell 1 holds cluster 46 twice", the duplicate a second concurrent
    remove-and-reinsert leaves behind
  on_violation:
    two workers in one tree → a leaf entry duplicated or lost, or RemoveChecked raising an identity mismatch
      out of a fence worker. The tree stays STRUCTURALLY valid, so TreeValidator passes over corruption
    deferral without a fallback → the promoted cells silently stop tracking their clusters' bounds
    replay in arrival order → handles depend on worker count, and a rebuild stops reproducing them
  requires: ST-05 (handles live in PayloadBackPointers), ST-07 (the in-place window closes inside the fence)

---

## Module: Intra-cell relocation (Issue #872 step 10)

### CR-01: The pending-migration queue drains its prefix and keeps the rest `[fatal][silent]`
  invariant the queue has PRODUCERS ON BOTH SIDES OF ITS CONSUMER, and that is the whole rule.
    DetectClusterMigrations files during Prep, which precedes Migrate; FlagOutliersForMigration and
    DetectDriftersInCluster file during AabbRefresh, which FOLLOWS it. So:
      let P = PendingMigrationCount when PrepareArchetypeFence returns
      Migrate executes exactly PendingMigrations[0 .. P)
      Finalize removes exactly that prefix and shifts the remainder down
      ∀ tick: PendingMigrationCount after Finalize == (requests filed during this tick's AabbRefresh)
  invariant the prefix must be recorded on EVERY exit path of Prep, not at the bottom of its body. Prep has
    three returns, and the clean-bitmap `return true` is the one an archetype written through the spatial
    barrier takes on every ordinary tick — the barrier sets ClusterProcessBitmap and leaves
    ClusterDirtyBitmap clean. A prefix of zero there means Migrate executes the whole queue while Finalize
    compacts nothing away
  invariant neither failure is loud, and they fail in opposite directions:
    prefix too LARGE (the pre-#872 `PendingMigrationCount = 0`) → every request filed by the AabbRefresh
      producers is discarded before it is ever drained. The outlier guard had been detecting, counting itself
      in telemetry, merging under the finalize lock and dropping the result since #230; its own comment says
      those requests "execute next tick", and none of them ever did
    prefix too SMALL → executed requests stay queued and re-execute against slots their entities have already
      left, and the queue grows without bound. Measured: 16 000 entities produced 17 234 migrations on the
      first tick and 224 854 on the twentieth, against ~10 900 genuine drifters per tick
  scope: DatabaseEngine.PrepareArchetypeFence, ArchetypeClusterState.CompactPendingMigrations,
    ArchetypeClusterState.PendingMigrationDrainCount, DatabaseEngine.FinalizeArchetypeFence
  verified: ClusterRelocationTests.PendingQueue_KeepsOnlyWhatTheCurrentTickFiled (nine ticks of continuous
    intra-cell motion, asserting per tick that what remains queued is at most what that tick detected).
    Ablated: forcing the prefix to zero reddens it, and also reddens
    ClusterDriftParallelTests.DriftDetection_YieldsTheRulesDrifterSet_WhicheverFenceRunsIt
  on_violation:
    prefix too large → intra-cell drift is detected forever and repaired never; the ~24x selectivity win the
      issue exists for simply does not arrive, with every counter reporting healthy detection
    prefix too small → unbounded queue growth and repeated migration of slots whose occupants have moved on
  requires: MD-01 (dirty bits reflect both source and destination after each executed request)

### CR-02: A relocation destination is a preference, and every consumer must treat it as one `[fatal]`
  invariant MigrationRequest.DestClusterChunkId names the least-enlargement cluster detection chose, or
    AnyCluster (-1). It is computed a whole phase before the drain, so between the two the pinned cluster can
    fill up, or be drained and freed and its chunk id reallocated to a DIFFERENT cell
  invariant the claim must therefore validate identity, not bounds:
    TryClaimPinnedSlot requires ClusterCellMap[pin] == the request's DestCellKey before claiming
    on failure it falls back to the first-fit ClaimSlotInCell — it must NOT refuse the migration, which would
      strand the entity in a cluster it no longer belongs to
  invariant a pinned claim is a FOURTH success site for CellState.EntityCount. TryClaimSlotInCluster
    deliberately does not touch it and the scan overloads bump it at three separate sites, so the pinned path
    owes its own increment; without it a cell under-counts by one per relocation
  invariant placement reads ClusterAabbs for candidate clusters while the AabbRefresh phase is concurrently
    writing that array for clusters other slices own, so under W > 1 a candidate's box may be read either side
    of its own refresh. That is TOLERATED, and only because the pin is advisory: losing the race costs a
    slightly worse box, never a wrong one. It follows that the chosen DESTINATION is not reproducible across
    worker counts, while the DRIFTER SET is — the latter is decided from a cluster's own freshly computed
    bound and its own entities, both slice-local
  scope: ArchetypeClusterState.ClaimSlotInCell, ArchetypeClusterState.TryClaimPinnedSlot,
    ArchetypeClusterState.ChooseRelocationTarget, MigrationRequest.DestClusterChunkId
  verified: ClusterRelocationTests (Placement_ChoosesTheLeastEnlargementCandidate against an independent
    least-enlargement computation, Placement_NeverChoosesTheSourceCluster,
    Placement_TreatsAnEmptyClusterAsZeroEnlargement, Relocation_LeavesEveryEntityResidentExactlyOnce for the
    cell count). Ablated: ignoring the pin, and dropping the source exclusion, each redden their own test
  on_violation:
    no identity check → the entity lands in a cluster belonging to another cell; C13 broken silently, with
      every counter still balancing
    refusing instead of falling back → the entity stays in a cluster whose cell it has left
    missing EntityCount increment → cells report fewer entities than they hold
  requires: CC-01 (ClusterCellMap validity), CA-01 (the destination's bound must cover what it admits)

### CR-03: A drifter is defined by the target region alone `[silent]`
  invariant the rule is two-level, and the levels are not interchangeable:
    gate    — a cluster whose largest axis extent ≤ the cell's TARGET EXTENT is skipped whole; no entity in
              it can be improved by moving, so a tight world does three float compares per written cluster
              and no per-entity work. The target is per cell (step 14): CellSize * clamp(
              ClusterTargetPackingSlack * (slotsPerCluster / CellState.EntityCount)^(1/d),
              ClusterTargetExtentRatio, 1) × the throttle's boost, where a value of the cell itself means OFF;
              a slack of 0 makes ClusterTargetExtentRatio the constant it used to be. A cluster above the
              repair-nomination gate max(target, ClusterRepairExtentRatio) is not drift-scanned at all
    entity  — inside a gated cluster, an entity whose centre lies outside the target box — the SAME target
              extent the gate used, never the configured constant — by more than
              CellSize * ClusterDriftMarginRatio is a drifter
  invariant 🔴 the target box is centred on the cluster's CENTROID, never on the midpoint of its AABB. A box
    midpoint sits halfway between the two extremes, so ONE far outlier drags it half the distance to itself:
    thirty entities at x≈12 plus one at x=90 put the midpoint at 50, where nothing lives, and the whole core
    then reads as drifting. Relocating the majority to chase a point defined by the entity that should have
    left is the inverse of the intended repair. The centroid moves by 1/N instead of 1/2
  invariant DriftersDetected counts DETECTION, not outcome — it is incremented before placement is attempted.
    A cell whose every other cluster is full yields drifters and no migrations, and that gap is the signal
    step 11 needs; folding a placement outcome into a detection number destroys it
  invariant the intra-cell margin gets its own counter (DriftAbsorbedCount) and must not reuse
    HysteresisAbsorbedCount. That one is about cell-boundary oscillation and tunes MigrationHysteresisRatio;
    this one tunes ClusterDriftMarginRatio. The margins move independently, so one number tunes neither
  invariant 🔴 SCOPED EXCEPTION on the legacy (ActiveClusterIds) refresh branch: a cluster is examined only when its
    bound moved or its ClusterProcessBitmap bit is set. An entity moved by a writer that sets no process bit — OpenMut,
    or a raw GetSpan mutation — to a position INSIDE its cluster's existing bound satisfies neither, and is never
    tested. A real AC-10.1 false negative, kept because the same gate is what makes a quiet tick cost nothing on a
    branch that walks every active cluster with no dirty-bit filter; lifting it reddens AMotionlessTick_DetectsNothing.
    Closing it needs a per-cluster written-this-tick signal that branch does not carry. An archetype whose spatial
    writes all go through WriteSpatial is unaffected, and TYPHON009 flags the sites that do not
  invariant an entity the outlier guard has queued for ANOTHER cell is not also a drifter here. Two requests
    naming one source slot would have the second drain find it empty, and the guard's escape outranks an
    intra-cell quality move. Running the guard first only made the collision unlikely; since the two share one
    gather pass the guard returns the slots it claimed and detection excludes them by mask
  invariant one gather pass per written cluster feeds both consumers (D1). The cluster is walked at most twice
    per tick — once for the BOUND, through the double-precision directed-rounding reader C15 needs, and once
    for the CENTRES, cached SoA for the guard and the drift test to scan. The two readers stay separate on
    purpose: for a BSphere field the stored centre is not the midpoint of the derived bounds, only equal to it
    up to a rounding step, so deriving one from the other would move drift decisions by an ULP at the
    target-region boundary and decouple production from the oracle that reads the component the same way
  scope: ArchetypeClusterState.DetectDriftersInCluster, ArchetypeClusterState.GatherClusterCentres,
    SpatialGridConfig.ClusterTargetExtentRatio, SpatialGridConfig.ClusterDriftMarginRatio,
    SpatialMigrationTelemetry.DriftAbsorbedCount
  verified: ClusterDriftDetectionTests against ClusterDriftOracle (an independent implementation of the rule,
    not a call to the production predicate), plus ClusterDriftParallelTests for the serial ≡ oracle ≡ parallel
    equality at W in {1,2,8} under a real TyphonRuntime. Ablated: swapping the centroid for the AABB midpoint,
    and disabling the margin, each redden the differential
  on_violation:
    midpoint instead of centroid → a one-entity repair becomes a full cluster shuffle, away from where the
      cluster actually is
    gate removed → the per-entity walk runs on every written cluster, against a budget of 0.576 ns/entity
    counters merged → neither margin can be tuned from telemetry

---

---

### CR-05: One source slot, one request `[fatal][silent]`
  invariant no two entries of a tick's drain prefix — PendingMigrations[0, PendingMigrationDrainCount) — may name
    the same (SourceClusterChunkId, SourceSlotIndex). The prefix has four producers (the cell-crossing detector and
    the outlier guard in Prep's core, relocations carried from last tick's AabbRefresh, and the repair planner), and
    nothing downstream reconciles them
  invariant 🔴 ExecuteMigrations does NOT enforce this and must not be read as if it did. Its step-0 stale-source
    guard tests OCCUPANCY — (srcOcc & (1UL << srcSlot)) == 0 — not IDENTITY. It therefore covers only the case where
    the freed slot is still empty when the second request drains. When any ClaimSlotInCell in the same Migrate pass
    has handed that slot to an unrelated migrant, the second request migrates THAT entity to a destination chosen
    for someone else, and the victim's EntityMap entry and index rows then describe storage it does not occupy
  invariant the two exclusions are separate mechanisms because the collisions have different lifetimes, and neither
    subsumes the other:
    cross-tick — a relocation is detected in AabbRefresh and decided by the NEXT Prep, by which time the crossing
                 detector may have filed a CellCrossing for the same entity. The throttle DROPS the relocation:
                 mandatory outranks quality, and the relocation's destination was the least-enlargement choice
                 against last tick's AABBs for an entity that has since left the cell entirely
    same-tick  — the crossing detector and the repair planner both run in Prep, so the planner must not GATHER a
                 slot the queue already claims. Excluded at UnitPopulation as well as at the gather, so the budget
                 projection matches the work that will actually happen
  invariant a superseded relocation is counted separately from a throttled one (RelocationsSuperseded, not
    RelocationsThrottled). A throttled relocation is one the BUDGET refused and is the signal that the budget is too
    small; a superseded one was refused by nothing — its entity migrates regardless. Folding them reports budget
    pressure that does not exist, against the number step 11's controller is tuned on. TH-02's identity therefore
    reads DriftersDetected = admitted + throttled + superseded + unplaced
  invariant a repair unit whose clusters held an excluded slot must NOT be memoised as a no-op. The no-op memo keys
    on the unit's bounds hash, and exclusions change the partition without changing any bound — so memoising would
    drop the cell from the queue against a hash that still matches next tick, and nomination only re-queues a cell
    whose bounds move. Such a unit is left in the queue, unmemoised, to age
  invariant only MANDATORY requests supersede. Detection visits each slot of each cluster once and a throttled
    relocation is dropped rather than carried, so the queue never holds two relocations for one slot; recording
    relocation sources as claims would drop a legitimate re-filing on some later tick
  scope: ArchetypeClusterState.ApplyMigrationThrottle, ArchetypeClusterState.BuildRepairSourceExclusions,
    ArchetypeClusterState.ExcludedSourceSlots, ArchetypeClusterState.UnitHasExcludedSources,
    ArchetypeClusterState.AssertNoDuplicateMigrationSources, SpatialMigrationTelemetry.RelocationsSuperseded
  verified: ClusterMigrationSourceExclusivityTests — the supersede rule and its two negative controls driven
    directly against the queue, plus an end-to-end arm on a world where crossings, drift and repair all fire, whose
    own guard fails if the workload never produced a collision. AssertNoDuplicateMigrationSources runs on every Prep
    in DEBUG, so the whole suite is a net for this. Ablated: disabling the throttle's supersede filter reddens two
    of the three; disabling the planner's exclusion reddens the end-to-end arm with the CR-05 message
  on_violation:
    the common outcome is benign — the crossing drains first and the stale-source guard skips the second request —
      which is what let this survive step 12, step 11 and a 5 800-test suite
    when the slot has been refilled: an entity is silently moved by a request filed for a different entity. Observed
      as three unrelated-looking symptoms from one cause — "Entity(...) not found or not visible" on a later
      OpenMut, "B+Tree bulk update reached an invalid child", and an AccessViolationException (#877)
    peak damage is at a MIDDLING budget, not a large one: below the cliff the planner admits nothing and above it a
      cell is repaired in one unit. Measured 0/12 seeds at 0.5 ms, 6/12 at 1.0 ms, 3/12 at 4.0 ms, 0/12 at 16 ms —
      so a fixture at an arbitrary budget would very likely have sat in a clean band

---

## Module: Cell repair — the full Morton re-sort (Issue #872 step 12)

### RP-01: A repair unit is admitted whole or not at all `[fatal][silent]`
  invariant the budget gates ADMISSION, never progress. A Morton sort cannot be halved: a partly re-sorted cell
    has paid the cost and banked part of the benefit, and its downstream batches have already formed. So a unit
    whose projected cost exceeds the remaining budget is not begun — no entity of it is gathered, sorted or
    moved — and the refusal is counted
  invariant the projection precedes the work and uses only the population: entities * estimateNsPerEntity, where
    the population is a popcount of the unit's occupancy words. Measuring instead of projecting would decide
    admission after the cost was already paid, which is the thing the whole-unit rule exists to prevent
  invariant (step 11) estimateNsPerEntity is MEASURED, and RepairNsPerEntity is its seed and floor rather than
    the operative value. The estimate is an EWMA over the previous tick's whole migration cost per migrant plus
    the planner's own measured cost. "Whole" is load-bearing: LastTickMigrationExecuteMs brackets the migrant
    loop, which merely STAGES the index update and the EntityMap patch, and the secondary index alone is ~48 %
    of a migration — an estimator built on it over-admits by about 2x every tick. Use LastTickMigrationTotalMs
  invariant (step 11) exactly one admission per archetype per tick may exceed the remaining budget: the safety
    valve, on a cell whose degradation has reached ClusterRepairCriticalExtentRatio. Per ARCHETYPE, because the
    planner runs one work item per archetype — an engine with N cluster-spatial archetypes can overshoot N times
    in one tick, each by one capped unit
  invariant (step 11) that bound is STRUCTURAL, not stateful: PlanCellRepairs pre-scans the ranked candidates for
    the first critical one, services it at the head, and passes valveAvailable:true from that ONE call site.
    Everything else in the loop is passed false. It was a _valveFiredThisTick flag until the pre-scan replaced
    it, and the flag then sat assigned-and-reset with no reader for a while — which is worse than no flag, since
    it reads as the thing enforcing the bound while enforcing nothing. One call site is provable by inspection
  invariant (step 11) a SECOND critical cell in the same tick gets no valve. It is refused like any other
    candidate, ages, keeps its queue place and is the hoisted one on a later tick — so AC-11.2's "within N ticks"
    holds with a larger N when several cells are critical at once, which is the case the budget is already losing
  invariant (step 11) the valve's unit is capped in CLUSTERS — RepairWorstClustersPerUnit, or ValveClustersPerUnit
    when the configuration asks for the whole cell. AC-11.1's "by more than one indivisible unit" is not licence
    for an uncapped one: a whole cell IS one indivisible unit, and a 100 K-entity cell was measured at ~133 ms, a
    133x overrun that would still claim compliance. An earlier version capped with MaxSlotsPerCluster, which is 64
    SLOTS used as a clusters count — 4 096 entities, eight times the documented bound
  invariant (step 11) a valve admission that MOVES NOTHING does not consume the tick's one overshoot. Marking it
    before the unit runs spends the allowance on a re-pack the no-op guard then declines, and refuses a genuinely
    critical cell later in the ranking for work that never happened
  invariant ReclusterBudgetUsedMs reports the PROJECTED spend of admitted units, not elapsed time. The number
    that gates has to be the number that is reported, or the two drift and neither can be reasoned about
  invariant a refusal must not spend budget. remainingNs is decremented only after a unit has emitted requests
  scope: ArchetypeClusterState.PlanCellRepairs, ArchetypeClusterState.RepairOneCell,
    ArchetypeClusterState.RepairCostEstimateNs, ArchetypeClusterState.ObserveMigrationCost,
    SpatialGridConfig.ReclusterBudgetMs, SpatialGridConfig.RepairNsPerEntity,
    SpatialGridConfig.ClusterRepairCriticalExtentRatio,
    SpatialMigrationTelemetry.RepairUnitsRefused, SpatialMigrationTelemetry.ReclusterBudgetUsedMs,
    SpatialMigrationTelemetry.RepairValveFires, SpatialMigrationTelemetry.MeasuredNsPerEntity
  verified: ClusterRepairTests.ARepairIsNeverBegunWithoutTheBudgetToFinishIt drives the budget to 99 % of the
    projected cost and asserts nothing moved, nothing was spent and the refusal was counted;
    TheSameCellIsRepairedOnceTheBudgetCoversTheUnit is its control at 150 %, so the pair separates "the rule
    fired" from "nothing was ever nominated".
    ClusterRepairQueueTests.ACriticalCellIsServicedEvenWhenTheBudgetCannotAffordIt covers the valve and asserts
    it fires at most once per tick; WithTheValveDisabledAnUnderBudgetQueueServicesNobody is its ablation arm, and
    without it "nothing was repaired" cannot be told from "nothing needed repairing".
    ClusterCostEstimatorTests covers the measured estimate: it tracks the machine from a contradicted seed and
    settles (asserted as an ABSOLUTE spread, because two post-transient windows measure the same noise and noise
    is not monotone), stays inside its clamp band, and does not move once the world has genuinely settled —
    detected as three consecutive migration-free ticks, not assumed after a fixed count
  on_violation:
    budget stops a unit mid-way → a cell left in a state strictly worse than the one it started in
    refusal spends budget → later units in the same tick are starved by work that never happened

### RP-02: The repair plan is produced serially and its destinations are pinned exactly `[fatal][silent]`
  invariant NOMINATION is parallel (AabbRefresh workers) and its output is consumed as a SET — sorted, with
    repeats skipped — so no permutation of the same cells changes the plan
  invariant PLANNING is single-threaded: it runs once per archetype in Prep's serial tail — inside the archetype's
    one ArchetypePrep item, or, when Prep ran as PrepSlice items (#886), in FenceMigrateExecSystem.Prepare, which is
    single-threaded by construction. The cluster ranking, the Morton sort and the destination assignment all live
    there; no slice plans
  invariant 🔴 a repair request pins the destination SLOT as well as the cluster, and the reason WAS the SORT, not
    the slicing. Until #889, SortPendingMigrationsByDestCellKey ran an Array.Sort — introsort, UNSTABLE — over a
    comparer reading DestCellKey alone, so every request a repair emits for one cell compared equal and the
    planner's emission order within that cell was permuted arbitrarily. That sort runs only on the parallel path,
    so first fit would have given the serial and parallel fences different packings from identical input. NOT
    slicing: FenceWorkPlan.EmitMigrationApplyItems advances each boundary until DestCellKey changes, so one cell's
    run is never split and two workers can never claim into the same fresh cluster.
  invariant #889 made the sort STABLE (ArchetypeClusterState.RadixSortByDestCellKey — LSD radix by DestCellKey,
    enqueue order kept inside a cell), so the pin is no longer what makes the packing deterministic. The pin is
    KEPT: every consumer is written against the fallback chain below, and retiring it is a change to the planner's
    contract, not to the sort. Whoever retires it has this paragraph as the licence, and must keep the serial
    and parallel packings equal without it
  invariant both pins remain PREFERENCES. The fallback chain is exact slot -> the pinned cluster's first free
    slot -> ClaimSlotInCell. A lost pin costs a worse box, never a wrong cell — CR-02 governs the rest
  invariant the sort's comparator is a TOTAL order — (mortonKey, sourceLocation) — so equal keys do not leave
    the tie to the sort's internals. sourceLocation is chunkId * 64 + slot and is unique across a unit
  scope: ArchetypeClusterState.PlanCellRepairs, ArchetypeClusterState.ExecuteRepairPlan,
    ArchetypeClusterState.TryClaimExactSlotInCluster, MigrationRequest.DestSlotIndex
  verified: ClusterRepairParallelTests.ARepairProducesTheSamePacking_WhicheverFenceRunsIt compares the
    per-cluster tag sequence across the serial fence and a real TyphonRuntime at W in {1,2,8}, having first
    asserted that each arm actually repacked and did not collapse to one cluster
  on_violation:
    slot not pinned → the packing depends on where slice boundaries fell; identical input, different layout
    planning parallelised → the cluster ranking races and no two runs agree

### RP-03: A repair allocates EMPTY destinations and re-sorts only when the sort would change something `[fatal][silent]`
  invariant destinations are FRESH clusters, never the unit's own. A re-pack is a permutation of the unit's
    slots, and ExecuteMigrations claims one slot at a time, so a destination still holding an entity yet to move
    fails its claim and falls back to first fit — the placement #872 exists to repair
  invariant 🔴 a fresh destination is published with occupancy ZERO and no CellState.EntityCount bump. Pre-setting
    the bits would be cheaper and is what "no claim on the repair path" would literally mean, but occupancy is
    authoritative: the unfiltered Count() fast path sums occupancy popcounts, so a set bit with no entity behind
    it over-reports the database for the width of a tick
  invariant every fresh destination is recorded via RecordClusterDrain at allocation. If every request targeting
    it is skipped it stays empty, and nothing else would ever schedule it for freeing — a release is the only
    event that normally does. The existing Finalize pass re-reads occupancy and frees only what is still empty
  invariant 🔴 the planner must top up _drainedClusterIds after it emits. PreSizeMigrationBuffers sizes that list
    from PendingMigrationCount, on the premise that one migration releases at most one source slot, and it runs in
    Prep's CORE — before the planner, which then files `count` more migrations AND consumes `destinationCount`
    drain entries of its own. Without the top-up the Migrate phase overflows into RecordClusterDrain's fallback
    grow, which parallel workers reach and which writes its entry AFTER releasing the lock, re-reading the field:
    an entry can be discarded by a concurrent resize, and a discarded drain record is this rule's leaked chunk id
  invariant destinations are allocated BEFORE any request is emitted. Interleaving lets an allocation fail partway
    and leave the unit half re-packed — the state RP-01 calls strictly worse than untouched — where allocating up
    front makes the failure atomic: nothing emitted, and the clusters already taken are freed by Finalize because
    they are still empty
  invariant 🔴 the no-op check must also stop the SCAN, not only the moves. Nomination fires on extent alone and a
    Morton packing does not in general bring every cluster under the threshold, so a converged cell is nominated
    again every tick and would pay a full GatherClusterCentres walk plus an Array.Sort to reach the same verdict —
    on Prep, single-threaded, and charged to no budget, since a unit that emits nothing is never debited. The memo
    is the hash of the unit's ranked bounds, which the ranking loop has already read. Heuristic in one direction:
    entities shuffling strictly inside their clusters' existing bounds change the key order without changing any
    bound, and that re-sort is skipped — the same exposure CR-03 records, and the delta path's population
  invariant 🔴 a unit already packed in sort order is NOT re-packed. Nomination fires on extent alone and a Morton
    packing does not in general bring every cluster under the threshold, so the same cell is nominated on every
    subsequent tick; without the check it is re-packed forever at full cost and zero gain. The test is exact and
    one pass: if every group of the packing already draws from a single source cluster, the sorted partition and
    the current one coincide
  scope: ArchetypeClusterState.AllocateEmptyClusterForCell, ArchetypeClusterState.IsAlreadyPackedInSortOrder,
    ArchetypeClusterState.ExecuteRepairPlan, ArchetypeClusterState.PreSizeDrainedClusterIds,
    ArchetypeClusterState.HashUnitGeometry
  verified: ClusterRepairTests.ARepairPreservesEveryEntityAndEveryInvariant (population, CA-01, C13, EntityMap
    resolution and CellState.EntityCount after the re-pack); ClusterRepairCrashTests (the rebuilt cell layer
    agrees with cluster storage across a reopen); ClusterRepairConvergenceTests, which pins TERMINATION — that a
    repaired cell is not repaired again while nothing moves, that it re-converges after destroys punch holes in
    the packing, and that IsAlreadyPackedInSortOrder answers partition equality on hand-built inputs including a
    partial trailing group. The no-op guard was added against a measurement: a 2 000-entity cell re-packed all
    2 000 on five consecutive ticks with its mean extent pinned at 23.0 throughout
  on_violation:
    destinations reused → claims fail and the sorted packing degrades to first fit
    occupancy pre-set → Count() over-reports for a tick
    drain not recorded → a cluster that never received an entity leaks its chunk id
    no-op check removed → a converged cell re-packs every tick, forever

### RP-04: The repair trigger is its own threshold, and it must see a still cell `[silent]`
  invariant 🔴 P7's stated value cannot fire. The design says to start at "the existing cellSize x 1.2 extent
    check", but that check belongs to the OUTLIER GUARD and looks for a bound that has escaped its own cell,
    which happens only when a cluster holds entities that should have migrated out. A cluster whose entities all
    belong to its cell tops out near 1.05 x cellSize (the hysteresis margin), so 1.2 is unreachable and AC-12.1's
    own scenario — AABBs at ~90 % of the cell — sits below it. ClusterRepairExtentRatio (0.75) replaces it
  invariant the repair threshold sits strictly between the drift gate (ClusterTargetExtentRatio, 0.25) and the
    outlier guard (1.2), so a nominating cluster has always been drift-gated too. Nominating at the drift gate
    would ask for a re-sort of every cluster the delta path is already working on, which is the opposite of rare
  invariant only the UPPER half is enforced by a throw. At or above 1.2 the threshold can never be reached, so the
    value silently disables the feature — a configuration error. The lower relation is a tuning guideline about
    two mechanisms competing and stops applying the moment the drift gate is switched off (the fixtures do that by
    setting the target ratio to 100, which no cluster can exceed), so throwing on it would reject a legal
    configuration in which repair is the only mechanism running
  invariant 🔴 nomination on the legacy refresh branch runs BEFORE the boundsMoved / process-bit skip. The design's
    own trigger list for repair includes "initial load / rebuild" — a cell laid out badly and then never written —
    and below the skip that cell is invisible. Measured: a cell spawned in scattered order sat at a mean extent of
    86.4 of 100 for six consecutive ticks with clustersScanned = 0 and no nomination. It is free there because the
    branch has already recomputed the bound over every occupied slot
  invariant 🔴 KNOWN GAP, barrier-only mode: that branch iterates ClusterProcessBitmap, which by construction holds
    only clusters written this tick, so a still cell is never nominated. Closing it needs a signal that ranks CELLS
    rather than reacting to cluster writes — step 11's priority queue
  scope: SpatialGridConfig.ClusterRepairExtentRatio, ArchetypeClusterState.RecomputeDirtyClusterAabbsSlice,
    ArchetypeClusterState.EnqueueRepairNominationsBulk
  verified: ClusterRepairTests.ARepairPassTightensADegradedCell drives a still, never-moved cell and asserts the
    repair both fires and tightens it; the fixture would go green with the nomination deleted only if the cell
    were also moving, which it is not
  on_violation:
    trigger left at 1.2 → the repair path never runs, and its test suite says nothing
    nomination below the skip → a degraded cell that stops moving is never repaired

### TH-01: The throttle truncates the queue; it never defers `[fatal][silent]`
  invariant re-clustering runs to a BUDGET, never to completion (design 5.6). Deferring a pass costs a tick of
    looser bounds; overrunning the frame costs the frame
  invariant the throttle lowers PendingMigrationCount itself, so the drain prefix still equals the count. It must
    NOT shorten the prefix and leave the tail queued:
      the serial fence passes PendingMigrationCount, not the prefix, so it would execute the tail AND retain it
      SortPendingMigrationsByDestCellKey sorts [0, PendingMigrationCount) by destination cell (stably since
        #889, but the key is the cell, not the position), so it would move tail entries into the prefix
      both land on CR-01's "prefix too SMALL" failure, measured at 224 854 migrations on the twentieth tick
  invariant a throttled relocation is DROPPED, not carried. Its DestClusterChunkId was the least-enlargement
    choice against the AABBs of the tick that DETECTED it; a tick later TryClaimPinnedSlot rejects the stale pin
    (CR-02) and falls back to first fit, which is the placement #872 exists to repair. Re-detecting recomputes
    the choice against current bounds
  invariant a CELL-CROSSING is charged to the budget and never refused. It is a correctness move (design 5.7), so
    a heavy crossing tick starves repair rather than the reverse
  invariant the partition is single-threaded and order-preserving WITHIN each class, so given the same queue, the
    same budget and the same estimate it admits the same set — every time
  invariant 🔴 that does NOT make the admitted set worker-independent, and the two must not be conflated. Two
    inputs to the decision vary with scheduling: AabbRefresh's worker-local drifter buffers merge in COMPLETION
    order, and the budget is divided by a MEASURED per-entity cost. Measured: two W=1 runs of one motion schedule
    agreed for four ticks and then separated, and the divergence compounds because a different set of entities
    moved leaves different bounds to detect against. What holds at every W is the RULE — spend never exceeds what
    the budget pays for, and every drifter is accounted for
  invariant ReclusterBudgetMs == 0 means NO BUDGET ENFORCEMENT, not "do no re-clustering". It disabled repair
    before step 11 and must keep meaning only that: dropping every relocation at zero would silently turn one
    knob into a switch for step 10 as well, reverting placement to first fit
  scope: ArchetypeClusterState.ApplyMigrationThrottle, ArchetypeClusterState.PendingMigrationCount,
    MigrationRequest.Kind, MigrationKind, SpatialMigrationTelemetry.RelocationsThrottled
  verified: ClusterThrottleBudgetTests.NoTickAdmitsMoreRelocationsThanTheBudgetPaysFor (bounded spend, guarded
    against a zero estimate — the division saturates to int.MaxValue rather than throwing, so an unguarded bound
    is no bound); RelocationsSurviveATickThatAlsoCarriesCellCrossings is the arm that catches an in-place
    partition END TO END: every other engine-level fixture confines its entities to one cell and so produces no
    crossings, which is exactly the case the bug cannot fire in. It is an INEQUALITY whose slack is the tick's
    crossing count, so the exact net is ThePartitionIsAPureFunctionOfItsInputs below;
    ThePartitionIsAPureFunctionOfItsInputs drives ApplyMigrationThrottle directly over a mixed queue, five times;
    AZeroBudgetKeepsRelocatingAndKeepsEveryQueueBounded (the zero meaning, over thirty ticks);
    ClusterThrottleParallelTests.TheThrottleEnforcesItsBudgetAtEveryWorkerCount drives a real TyphonRuntime at
    W in {1,2,8} against the serial fence and checks the rule on every tick of each
  on_violation:
    prefix shortened instead of the count → CR-01's unbounded queue growth, with stale re-execution
    partition done IN PLACE → the compaction overwrites relocations the second pass has yet to read, so
      min(relocations, crossings) of them are destroyed with RelocationsThrottled reading zero. Not a corner:
      AabbRefresh appends relocations and the next Prep appends crossings behind them, so the relocations are
      exactly the entries at risk
    crossing refused → an entity stranded in a cell it no longer belongs to; C13 and RebuildCellState both lie
    zero read as "disable relocation" → placement silently reverts to first fit with every counter reading zero

### TH-02: Every detected drifter is accounted for exactly once `[silent]`
  invariant over one tick's detection: DriftersDetected == admitted + throttled + superseded + unplaced, where
    `admitted`, `throttled` and `superseded` are observed on the FOLLOWING tick — detection runs in AabbRefresh,
    which follows Migrate, so its requests are decided by the next tick's Prep
  invariant `superseded` (CR-05) is a term of its own and not a kind of throttling. A relocation dropped because a
    cell crossing already claims its source slot was refused by nothing; counting it as throttled would report
    budget pressure that does not exist. It is zero on any tick with no crossings, which is why the verifying
    fixture — a single-cell world — never observes it and a fixture that means to must cross cells
  invariant 🔴 `admitted` means RELOCATIONS admitted. MigrationCount stands in for it only where the tick carries
    no cell crossings and no repair moves, because that counter sums all three kinds; the verifying fixture pins
    both away deliberately. State the precondition rather than reading the identity as unconditional
  invariant an entity absorbed by the intra-cell drift margin is in NONE of those terms: DetectDriftersInCluster
    increments driftAbsorbed and continues BEFORE driftersDetected. Design 5.6 requires that hysteresis and
    throttling not both discount the same migration, or the budget model over-counts what it defers
  invariant `unplaced` — a drifter for which placement found no better cluster — must be counted, not merely
    skipped. Without it the identity cannot close, and "the world drifts faster than the budget" cannot be told
    from "this cell has run out of room"; those have opposite remedies
  scope: ArchetypeClusterState.DetectDriftersInCluster, SpatialMigrationTelemetry.DriftersDetected,
    SpatialMigrationTelemetry.DriftAbsorbedCount, SpatialMigrationTelemetry.DriftersUnplaced,
    SpatialMigrationTelemetry.RelocationsThrottled, SpatialMigrationTelemetry.RelocationsSuperseded
  verified: ClusterThrottleBudgetTests.EveryDetectedDrifterIsAccountedForExactlyOnce asserts the lagged identity
    on every tick of a nine-tick run, having first established that both absorption and throttling occurred
  on_violation:
    absorbed leaks into throttled → the budget model over-states what it is deferring and under-admits for ever
    unplaced uncounted → a saturated cell is indistinguishable from a saturated budget

### TH-03: The repair queue is persistent, ranked and bounded `[silent]`
  invariant a nomination SURVIVES its tick. Step 12 discarded whatever the budget refused, so a cell could be
    nominated on every tick of a run and never once serviced. The queue keeps the candidate, with the worst
    degradation seen and the tick it began waiting
  invariant candidates are RANKED by expected selectivity gain, not round-robin — design 5.6 names round-robin as
    the wrong policy: a region nobody queries never needs tight clusters. score = degradation * tierWeight *
    clusterCount * ageFactor
  invariant the age factor is UNBOUNDED in the tick count, so ranking cannot starve. Ranking alone always can:
    whatever a candidate's base score, enough waiting must carry it to the head
  invariant SimTier is a BIT FLAG (None=0, Tier0=1, Tier1=2, Tier2=4, Tier3=8), so the weight is over the INDEX,
    and SimTier.None must weigh 1.0 rather than fall out of the formula. TrailingZeroCount(0) is 32, so the naive
    1/(1+index) scores every cell in an untiered world at 1/33 — and an untiered world is the default, every
    fixture, and any deployment with no SpatialInterestSystem. Absent information discounts nothing
  invariant the queue is CAPPED at RepairQueueMaxCells and evicts by score, with the eviction count published. A
    per-tick list could not leak; a persistent one can
  invariant re-ranking is LAZY — on new nominations or a SpatialGrid.TierVersion change, never on a timer — and
    its cost is reported. A queue that costs more to maintain than the work it schedules is a net loss
  invariant 🔴 KNOWN GAP, narrowed not closed: PrepareArchetypeFenceCore returns false for an archetype nothing
    wrote to, and the wrapper then skips planning entirely, because a plan allocates clusters THIS tick's Migrate
    and Finalize must consume. So a queue full of candidates in a world that has gone completely still is not
    drained. Closing it needs the fence re-armed by queue depth alone, which collides with AC-10.8
  scope: CellRepairQueue, ArchetypeClusterState.RepairQueue, ArchetypeClusterState.AbsorbRepairNominations,
    SpatialGridConfig.RepairAgingRatePerTick, SpatialGridConfig.RepairQueueMaxCells,
    SpatialMigrationTelemetry.RepairQueueDepth, SpatialMigrationTelemetry.RepairQueueEvicted,
    SpatialMigrationTelemetry.RepairQueueMaintenanceMs
  verified: ClusterRepairQueueTests.AgeingCarriesEveryCandidateToTheHeadOfTheQueue drives CellRepairQueue
    DIRECTLY — one service per tick over a lopsided candidate set — because the engine-level form cannot be made
    machine-independent: the budget is spent against a MEASURED cost, Debug migrates at ~24 us and Release at
    ~1.5 us, so one budget admits one unit per tick in one configuration and eighteen in the other. Ablated by
    WithoutAgeingTheWorstCandidateStarvesEveryoneElse, which shows the same set starves at agingRate 0;
    AnUntieredCellOutranksAnEquallyDegradedTieredOne — one Tier2 cell against five untiered ones of identical
    degradation, because a UNIFORMLY untiered world cannot show the defect at all: the wrong weight is then a
    constant factor and the ordering is bit-identical; TheQueueStopsAtItsCapAndReportsTheEvictions and
    AHighScoringLateArrivalDisplacesAWeakerIncumbent (the bound, then the policy the bound does not pin);
    QueueMaintenanceIsASmallFractionOfTheWorkItSchedules
  on_violation:
    nomination discarded on refusal → a cell nominated every tick and serviced never, with no counter to show it
    SimTier.None mishandled → the ranking collapses in every untiered world, which is the default one
    queue uncapped → unbounded growth under a permanently over-subscribed workload

### RP-05: A repair is the only thing that narrows a zone map `[perf][silent]`
  invariant ZoneMapArray.Widen is the only writer on the hot path and never narrows, so a cluster accumulates the
    union of every value it has held, and a RECYCLED chunk id inherits its previous tenant's bounds because
    nothing invalidates them on free. ZoneMapArray.Invalidate existed with no caller at all before this step
  invariant the direction of the error is conservative — MayContain over-reports, so queries open clusters they
    need not and never miss one — which is why it is a [perf] rule and not a [fatal] one
  invariant a repair invalidates every indexed field's zone map for each destination cluster it allocates, so
    Widen rebuilds from the re-packed contents. Without it the narrowing AC-12.2 measures cannot occur at all
  invariant the narrowing is real only for a field CORRELATED with position. Locality-grouping tightens the min/max
    of a coordinate, or of something tracking one; for an index on an unrelated quantity a re-sort neither helps
    nor harms, and claiming otherwise would be claiming magic
  scope: ArchetypeClusterState.InvalidateClusterZoneMaps, ZoneMapArray.Invalidate, ZoneMapArray.TryGetBounds
  verified: ClusterRepairTests.ARepairNarrowsTheZoneMapsOfTheCellItRepacks measures total recorded width before
    and after over every cluster of the cell and every indexed field; measured 3 541 -> 796 (22 %) at 2 000
    entities in 41 clusters. It also asserts the total is non-zero afterwards, so "narrower" cannot be satisfied
    by "invalidated and never re-widened"
  on_violation:
    invalidate omitted → the re-packed cluster inherits a stale wide bound and prunes nothing
    invalidate without a following widen → the map reads "unknown", which is conservative but buys no pruning

---

## Module: One spatial index home (Issue #872 step 13)

### SH-01: There is exactly one spatial index, and it is the per-cell cluster index `[fatal][silent]`
  invariant no SpatialRTree is constructed, held or handed out anywhere outside the per-cell cluster layer.
    CellClusterTree owns the only instances; SpatialRTree itself may return one (bulk load); TreeValidator may be
    handed one to check. Nothing else — no ComponentTable, no SpatialIndexState, no query path, no maintenance
    helper — may acquire one
  invariant a component carrying [SpatialIndex] allocates NO StorageSegmentKind.Spatial segment. Three used to be
    allocated per spatial component — the tree, its back-pointer segment, and a Layer-1 occupancy hashmap —
    persisted into the file and reloaded on open
  invariant SpatialIndexState carries FIELD metadata (offset, shape, mode, category) and the cluster-archetype
    fan-out list. It carries no index. Everything it describes is derived from the schema attribute, so nothing
    about it is persisted and the load path builds it exactly as the create path does
  invariant every query shape — AABB, radius, ray, frustum — resolves through the cluster path. None may throw
    NotSupportedException for want of a cluster implementation, which is what EcsQuery did for ray and frustum
    until this step wired in the step-9 implementations
  note 🔴 the danger of a SECOND home is not that it is slow, it is that it is EMPTY and nobody notices. #666 made
       IsClusterEligible unconditionally true, which left every entity-tree writer either behind a
       `!IsClusterEligible` guard or with no caller at all — verified at runtime with throw probes at
       InsertSpatial / RemoveFromSpatial / UpdateSpatialBatch: zero trips across 5 771 tests. The tree stayed
       allocated, persisted, reloaded and TRAVERSED BY EVERY SPATIAL QUERY for three issues, contributing nothing
       and costing a full descent of an empty structure each time. A rule stating "one home" is the only thing
       that makes a second one a violation rather than an oversight
  scope: ComponentTable.BuildSpatialIndex, SpatialIndexState, EcsQuery.ExecuteSpatial, CellClusterTree,
    SpatialRTree, PagedMMF.DatabaseFormatRevision
  verified: EntityIndexRetirementTests.NoTypeOutsideTheCellLayerHoldsASpatialRTree walks every field, property,
    method return and parameter in the engine assembly by REFLECTION rather than by text search — a grep for
    `new SpatialRTree` misses a factory and stops matching on a rename, where naming the type through typeof
    breaks the build instead; ASpatialComponentAllocatesNoSpatialSegment counts segments by kind, which is what
    makes "allocated but unread" observable at all; EveryQueryShape_AgreesWithBruteForce and the promoted /
    unpromoted arms of Ray_MatchesBruteForce and Frustum_MatchesBruteForce establish SQ-01 across the whole
    surface against an independent oracle; SpatialGridReopenTests asserts the NEGATIVE of what it asserted before
    the step, which is the polarity flip that makes the removal a fact rather than a claim
  on_violation:
    a second home appears → it is empty, every query pays to traverse it, and no test fails
    a Spatial segment is still allocated → the file carries pages nothing reads, and the format is a lie
    a shape throws instead of resolving → the capability is lost with the tree that used to provide it

### SH-02: The leaf entry carries a payload id and a category mask, and nothing else `[fatal][silent]`
  invariant leafEntrySize == CoordCount * CoordSize + 8 + 4. A 4-byte ComponentChunkId column sat between the two
    until this step; it named the owning component's chunk so a two-pass compound query could reach component
    storage without an EntityMap lookup — a service only the ENTITY-level tree could offer, since a cluster tree's
    payload IS a cluster chunk id and it wrote zero there
  invariant removing it RAISES LeafCapacity, and the numbers are stated rather than derived at review time:
    R2Df32 15 -> 17, R3Df32 11 -> 13, R2Df64 9 -> 10, R3Df64 11 -> 11 (its 704-byte entry area divides by 60 the
    same way it divided by 64 — the freed bytes are not yet a whole entry)
  invariant the layout is now PROCESS-LOCAL: after #872 step 13 no persisted structure uses it. CellClusterTree is
    SpatialRTree<TransientStore> over a heap-backed segment, so the leaf entry is never written to a file
  invariant 🔴 the step still carries a DatabaseFormatRevision bump, for the SEGMENTS rather than the layout. A v7 file
    holds up to three StorageSegmentKind.Spatial segments per spatial component plus a `spatial.<component>` bootstrap
    entry; this build allocates, reads and frees none of them, so those pages would be allocated and owned by nothing —
    unnameable by the page classifier, unreachable by the integrity checker, never reclaimed
  note 🔴 CORRECTED. This rule first justified the bump by the leaf layout, asserting that the cluster trees share it
       and "ARE written". They are not. The bump is right and the reason was wrong, which is the worse failure of the
       two: the next person weighing a layout change weighs it against a hazard that does not exist
  scope: SpatialNodeDescriptor, SpatialNodeHelper.ReadLeafEntityId / WriteLeafEntityId / CopyLeafEntry,
    SpatialRTree.Insert, ScatterLeafEntries, BulkLoad, PagedMMF.DatabaseFormatRevision
  verified: SpatialNodeDescriptorTests.KnownCapacities_MatchDesignDoc states all four capacities as literals;
    LeafSoaLayout_HasNoGapBetweenPayloadIdsAndCategoryMasks asserts the OFFSETS rather than the derived capacity,
    so a future change that removes one column and adds another of the same width cannot leave the fan-out numbers
    looking right; SegmentGeometryPersistenceTests.ABundleFromAnOlderRevisionIsRefusedByTheVersionGate forges an
    older revision and requires the refusal to name both revisions — reading the expected one from the engine
    constant rather than as a literal, because hard-coding it made every bump redden a test about message shape
  on_violation:
    layout changed without the bump → an old file opens and serves wrong clusters, silently
    capacity numbers drift from the rule → the arithmetic is no longer reviewable and the next change is a guess

---

## Module: ClusterCellMap (Issue #229)

### CC-01: ClusterCellMap validity `[fatal]`
  invariant ∀ active cluster C:
    ClusterCellMap == null ∨ C.chunkId ≥ ClusterCellMap.Length ∨
    ClusterCellMap[C.chunkId] ∈ [-1, SpatialGrid.CellCount)
  invariant ClusterCellMap[chunkId] ≥ 0 → cluster is assigned to a valid cell
  invariant ClusterCellMap[chunkId] < 0 → cluster is unassigned (skipped by TierClusterIndex)
  note the bound moved in #872 step 8. CellCount was the whole world's cell count, fixed at config time;
    it is now the number of cells that EXIST, which grows as cells are first touched. The invariant is
    unchanged in FORM but WEAKER as a detector: against a world-sized bound a stale or corrupted key was
    usually out of range and threw, whereas a small growing bound accepts it silently. A key is meaningful
    only against the grid instance that issued it, and only until a rebuild (VG-01)
  note transiently false between ResetCellState and the rebuild that follows it — the map still holds the
    old keys while the grid holds no cells. RebuildSpatialStateFromData refills both; nothing may read
    ClusterCellMap in between
  scope: ArchetypeClusterState.ClaimSlotInCell, RebuildCellState, RebuildSpatialStateFromData, TierClusterIndex.Rebuild
  on_violation:
    cellKey ≥ CellCount → IndexOutOfRangeException in SpatialGrid.GetCell
    cellKey corrupted → cluster bucketed into wrong cell → wrong tier assignment, wrong query results
    cellKey held across a rebuild → names a different position's cell → silently wrong counters and tiers

### CC-02: Cluster→cell exclusivity — every entity sits in the cell its cluster is mapped to `[fatal][silent]`
  invariant ∀ active cluster C with ClusterCellMap[C.chunkId] = k ≥ 0, ∀ occupied slot E of C, once the fence has
    drained the tick's crossings:
    SpatialGrid.WorldToCellKey(E.position) == k
      ∨ E.position lies within MigrationHysteresisRatio × CellSize of cell k on every axis
    The second disjunct is the write barrier's dead zone (ClusterRef.MaybeFlagMigration): a write that lands in the
    band is absorbed on purpose and never becomes a crossing, so the entity legitimately sits a margin outside k.
    A test wanting the exact form sets the ratio to 0
  invariant this is decision C13 of the VDB cell-grid design: a cluster belongs to exactly one cell. It is what
    makes the per-cell index (SH-01), CellState.EntityCount and the cell-relative frame of CA-01 well-defined.
    Between a spatial write and the next fence an entity may sit outside k — that is a crossing awaiting its
    drain (CR-05, TH-01), not a violation
  invariant the paths that PLACE an entity in a cell — spawn (ClaimSlotInCell resolves k from the position) and
    the drain-time claim of a crossing (its DestCellKey) — and the path that PUBLISHES a fresh cluster to a cell
    (AddClusterToPerCellIndex) run from user threads concurrently under commit, and must be safe against each
    other. Before #872 step 15 AddClusterToPerCellIndex ran latch-free, so two commits opening clusters in one
    cell could overwrite each other's PerCellSpatialSlot (every cluster published into the loser invisible to the
    index), and every allocation site published a fresh cluster into the cell's pool BEFORE writing its occupancy
    word, so a concurrent claim's CAS could be clobbered — an entity lost with every counter still balancing
  invariant a write-time crossing flag (ClusterRef.MaybeFlagMigration) names the SLOT that moved; the cell it
    records is a hint. Nothing un-flags a slot, so an entity written out and back within a tick, two writes to
    two cells, or a write that reached a spawn's slot before its data landed (the in-flight window NoteClusterBorn
    documents as a known residual) all leave a flag whose destination is not where the entity is. The drain
    (DatabaseEngine.DrainPreFlaggedMigrations) therefore re-reads the position and decides from it — dropping the
    flag when the entity is home (LastTickStaleFlagsDropped) — exactly as the dirty-bits scan does
  invariant CellClusterPool's per-cell (head, count) pair and its backing array are published and read in a fixed
    order (release: pool → head → entry → count; acquire: count → head → pool). A reader pairing a new count with
    an old head runs past its cell's segment into the next cell's, and a claim lands in a cluster of another cell
  scope: ArchetypeClusterState.AddClusterToPerCellIndex, ArchetypeClusterState.AddClusterToPerCellIndexLocked,
    ArchetypeClusterState.ClaimSlotInCell, ArchetypeClusterState.TryClaimPinnedSlot, ArchetypeClusterState.ClusterCellMap,
    DatabaseEngine.DrainPreFlaggedMigrations, CellClusterPool.GetClusters, CellClusterPool.AddCluster
  verified: ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell — eight writers spawning
    into two adjacent cells race eight writers moving entities across their boundary; after the fence every
    occupied slot resolves to its cluster's mapped cell and the two cells count what was spawned (7 of 30 runs
    failed before the latch and the occupancy-before-publish ordering; about 1 cold launch in 10 before the drain
    re-derived the destination). ClusterMigrationTests.WriteSpatial_CrossAndReturnInOneTick_StaysInItsCell and
    WriteSpatial_TwoCrossingsInOneTick_LandsWhereItIs pin the drain's decision with two writes, no race
  on_violation:
    an entity in a cluster mapped to another cell → invisible to its own cell's index → SQ-01 false negative,
      counters balanced
    a PerCellSpatialSlot overwritten → every cluster published into it invisible to the index
  requires: CC-01 (the key is valid), CR-05 and TH-01 (crossings are drained every tick, so the window closes)

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
    src/dst dirtyBits flips are NOT written by the worker on the parallel path: it appends a
      DirtyBitDelta to its own chunk-local buffer, and OnAfterChunk applies the whole buffer through
      ArchetypeClusterState.ApplyDirtyBitDeltas under _finalizeLock, one acquisition per (chunk x
      archetype). Plain bit ops are correct there BECAUSE the lock excludes sibling workers. Only the
      serial WriteTickFence path (dirtyBuffer == null) writes the array directly, with Interlocked.And /
      Interlocked.Or plus GrowFenceDirtyBitsForChunkId — safe because it is the only thread.
      CORRECTED 2026-08-14: this rule previously specified Interlocked flips from the worker, which the
      implementation has never done — the buffered-plus-lock design shipped in the same PR as the rule.
      A rule that describes a design nobody built cannot catch a regression in the one that exists
    the per-chunk delta buffers are sized in FenceMigrateExecSystem.Prepare, never grown from a worker:
      growing the shared buffer array from concurrent DispatchItem calls loses a bucket when two growers
      race, and the plain reference store is unordered against its Array.Copy on arm64
    NO shared per-archetype array is REALLOCATED from inside a Migrate slice. ClusterAabbs,
      ClusterSpatialIndexSlot, ClusterCellMap, PerCellIndex and the write-bookkeeping quartet are all
      indexed by cluster chunk id or cell key, neither of which the cell-disjoint slice boundary separates.
      Their growers take _finalizeLock (double-checked, so the fast path stays a lock-free length compare)
      and refuse outright while ArchetypeClusterState.InMigrateSlice is set, which
      FenceMigrateExecSystem.DispatchItem sets around each slice. Serialising the growers against each
      other is NOT sufficient on its own: a sibling that already loaded the reference writes into the
      abandoned copy, and ExecuteMigrations holds `ref ClusterAabbs[dstChunkId]` across a whole union.
      For ClusterSpatialIndexSlot the resize additionally drags RebindCellTreeBackPointers behind it,
      which cannot be made safe against a sibling's tree.Add by any lock the GROWER holds
    the refusal is reachable only if the pre-size is wrong, and the pre-size runs from the tail of
      FinishArchetypeFencePrep — the one point EVERY Prep exit passes through, and after the repair
      planner has allocated its destination clusters and filed its requests. It used to sit at the
      branch-2 exit of PrepareArchetypeFenceCore, which is one of three exits and is ahead of the planner;
      an archetype leaving through the clean-bitmap exit was then sized by whichever earlier tick had
      taken branch 2. Measured 2026-09-06: 3 000 entities, 464 pending migrations, ClusterSpatialIndexSlot
      64 long and a destination chunk id of 64 to record
    TryEnsureCellTreeSegment creates the archetype's shared cell-tree segment under _finalizeLock and
      publishes it with Volatile.Write. Promotion is decided per CELL and slices are cell-disjoint, so two
      workers can promote two cells at once; two unsynchronised creators each build a segment, the later
      store wins, and every tree the loser's cells built is left reading an orphaned structure
    cluster-fully-drains path (popcount(prev & ~mask) == 0) does NOT finalize in the worker:
      it records the chunkId via RecordClusterDrain (Interlocked.Increment slot reservation)
      and returns. CellClusterPool.RemoveCluster, RemoveClusterFromPerCellIndex,
      RemoveFromActiveList and ClusterSegment.FreeChunk run later, in
      DrainPendingClusterFinalizations, which re-checks occupancy and skips refilled clusters
  invariant DrainPendingClusterFinalizations runs exactly once per archetype per fence, from
    FinalizeArchetypeFence, AFTER the Migrate and AabbRefresh phase barriers. It takes no lock,
    and must not: its safety is that the barriers exclude every concurrent ClaimSlotInCell and
    ReleaseSlot for this archetype, so the occupancy re-check and the free are single-threaded.
    A lock here would not substitute for the barrier — finalizing while a claimer holds a
    CAS-won slot frees a live chunk no matter who holds what
  invariant FenceDirtyBits is pre-sized in PrepareArchetypeFence's tail BEFORE any Migrate-phase worker
    observes it. The bound is a deliberate over-estimate (max(PrimarySegmentCapacity, existingLen) +
    2*PendingMigrationCount + 64), NOT the strict PrimarySegmentCapacity + PendingMigrationCount this rule
    used to state — that one was observed to under-estimate under AntHill loads. It is a performance
    measure, not the safety argument: the parallel path never touches the array, and the on-demand grow in
    ApplyDirtyBitDeltas / GrowFenceDirtyBitsForChunkId is what actually makes an under-estimate survivable
  invariant PendingMigrations is sorted by DestCellKey (SortPendingMigrationsByDestCellKey)
    by TickDriver between Prep and Migrate dispatches, so each worker slice owns disjoint dst cells
  invariant PendingMigrationCount = 0 reset happens once per fence in FinalizeArchetypeFence
    AFTER all Migrate-phase slices complete, never inside ExecuteMigrationsSlice
  scope: DatabaseEngine.ExecuteMigrations, DatabaseEngine.FinalizeArchetypeFence,
    ArchetypeClusterState.ClearSlotMetadata, ArchetypeClusterState.ApplyDirtyBitDeltas,
    ReleaseSlot (Persistent + Transient overloads), DecrementCellEntityCountOnRelease,
    FinaliseEmptyClusterCellState, ClaimSlotInCell (both overloads),
    RecordClusterDrain, DrainPendingClusterFinalizations
  verified: FenceDirtyBitApplyTests, CellTreeDensityTransitionTests
  on_violation:
    plain ++/-- on cell counters → torn updates across workers → drift in EntityCount/ClusterCount
    plain occupancy clear → lost concurrent slot release → ghost entity in cluster
    worker finalizes inline instead of recording the drain → frees a chunk a concurrent
      ClaimSlotInCell has already CAS-claimed → live entity written into a freed chunk
    finalize pass moved before the phase barriers → same race, now unconditional
    Array.Resize from worker → lost writes from siblings holding the old array reference
    a per-archetype array grown from a slice while a cell tree is promoted → the tree's back-pointer
      rebind races the sibling's tree.Add; the sibling's handle lands in the abandoned array and the
      cluster is unreachable through that cell for as long as the tree lives (ST-05, silent)
    two workers creating the cell-tree segment → one archetype ends with two segments and the cells that
      promoted against the loser answer queries from a structure nothing else refers to
    ApplyDirtyBitDeltas called without _finalizeLock while siblings run → its plain bit ops lose
      concurrent flips and its grow drops their writes
    the chunk-buffer array grown from a worker → two growers race, one bucket is dropped from the array,
      and its deltas are lost the moment anything reads buckets back out of the array rather than through
      the reference the worker already holds

### MD-03: False-sharing avoidance for concurrent-mutation state `[perf][fatal]`
  invariant any data structure mutated concurrently from multiple workers isolates each
    independently-mutated element onto its own ≥64-byte cache line
  forbid bit-packed latch arrays (one bit per latch in a shared long[]) — adjacent latches
    share cache lines → catastrophic ping-pong on every CAS even under no logical contention
  prefer AoS (cluster all per-element state into one padded struct) over SoA + parallel
    padded arrays when padding is required — same memory cost, fewer cache-line fetches
  note CellState is [StructLayout(Explicit, Size = 64)] with 24 bytes of fields + 40 reserved
    (corrected 2026-07-27 from a stated 16; corrected again 2026-09-02 when #872 step 8 added
     CellX/CellY/CellZ at offsets 12/16/20 — a cell key became a pool slot and carries no position,
     so the cell has to hold its own coordinates)
  canonical example: CellState (24 bytes of fields, [StructLayout(Explicit, Size=64)],
    40 bytes reserved tail) — Tier, Flags, EntityCount, ClusterCount, CellX/Y/Z all on one line per cell
  applies to: CellState array, ArchetypeClusterState._finalizeLock (PaddedFinalizeLock 64B
    struct), any future per-cell or per-cluster latch arrays
  invariant ONE shared word taken per element of work is the same defect as one bit per latch, and the rule names it
    because it does not look like false sharing at the call site. ZoneMapArray._growLatch is a single padded word per
    archetype-and-field; a shared acquire still takes its line EXCLUSIVE, so a fence phase calling ZoneMapArray.Widen
    once per migrated entity issues two locked read-modify-writes into one line from every worker, under no logical
    contention at all. Padding cannot fix it — there is only one word — so the remedy is to stop taking it per element:
    hold it once per slice through BeginBatch/BeginBatchAtCapacity and write through the pinned Store
  forbid reaching ZoneMapArray's per-write path — Widen, WidenMasked, Invalidate — from inside a sliced fence phase.
    Prep goes through BeginBatch (#886); Migrate goes through BeginBatchAtCapacity (#926); the commit-path writers
    outside the fence window keep the per-write form, which is where it is correct
  invariant a batch pins ONE Store generation for its whole run, so batching moves the coverage guarantee off the
    write and onto the pre-size: PreSizeArchetypeFence sizes every zone map to the bound the Migrate phase cannot
    exceed, ZoneMapArray.Grow REFUSES inside a Migrate slice rather than abandoning stores its siblings hold, and
    WidenInto returns a verdict instead of writing when an index falls past the batch anyway. The failure this
    triple-guards is a false negative, which is the one error a zone map may never produce
  invariant the count that proves it is SpatialMigrationTelemetry.ZoneMapBatchOpens, and it is checkable rather than
    trusted: ZoneMapBatchOpens == MigrationSliceCount x indexed fields, an identity that does not mention the
    migration count. A number that starts tracking the migration count is the per-element acquire having come back,
    and no timing is needed to see it
  scope: SpatialGrid.GetCell, ArchetypeClusterState._finalizeLock, any new per-element
    Interlocked-mutated array, ZoneMapArray.Widen, ZoneMapArray.WidenInto, ZoneMapArray.BeginBatchAtCapacity,
    ZoneMapArray.Grow, ZoneMapArray.ThreadBatchDepth, SpatialMigrationTelemetry.ZoneMapBatchOpens,
    ArchetypeClusterState.LastTickZoneMapBatchOpens
  invariant the batch is a PARALLEL-path construct and the serial fence must open none. A batch holds the grow latch
    shared, so a destination past the pinned generation has nowhere to go: the parallel path answers that with a fence
    failure it can afford because its pre-size makes it unreachable, while the serial path has to keep the growing
    `Widen` available — and growing while holding the batch's own shared count waits for that count to drain from the
    thread that holds it, under an unbounded wait. That is a hang, not a slow path, so ZoneMapBatchOpens is ZERO on a
    serial fence by construction
  invariant the growth refusal is keyed on THIS THREAD HOLDING A BATCH (`ZoneMapArray.ThreadBatchDepth`), never on
    being inside a Migrate slice. A slice holding no batch may grow safely — it exits shared first and the exclusive
    acquire excludes every sibling's shared window, which is how the engine worked before batching and how the
    batching-off comparison arm still has to work. Keying it on the phase instead of on the held resource breaks that
    arm and proves nothing about the one that matters
  verified: ClusterMigrationTests.ZoneMapBatchOpens_TrackTheSliceCountAndFields_NotTheMigrationCount pins the identity
    across parallel ticks whose migration counts vary by design; ZoneMapBatchOpens_AreZeroOnTheSerialFence_WhichMustKeepItsGrowingFallback
    pins the serial zero, so the deadlock cannot be reintroduced silently;
    MigrantsLandingInFreshlyAllocatedClusters_AreStillRecordedInTheZoneMap pins that a cluster the slice itself
    allocated is still covered;
    ZoneMapConcurrentGrowthTests.WidenInto_RefusesAnIndexPastTheBatchStore_RatherThanWritingIntoAnAbandonedGeneration
    and WidenInto_RefusesAnIndexTheMapHasGrownToCover_WhileTheBatchStillPinsTheOlderGeneration pin the refusal, the
    second against a generation the map has since grown past — the only case that can actually lose a widen;
    Grow_IsRefusedWhileThisThreadHoldsABatch_ButAllowedInAMigrateSliceThatHoldsNone pins both halves of the guard
  note the dense CellState[] became a CHUNKED pool in #872 step 8. The 64-byte layout is unchanged, and
    the chunking is what keeps the `ref CellState` a stable interior pointer while the pool grows — a
    resize would hand a concurrent worker a doomed array, which is MD-02's concern rather than this one
  on_violation:
    bit-packed latches → 8× ping-pong amplification, parallel speedup collapses
    16-byte cell descriptors in flat array → 4-cell ping-pong per migration
    unpadded shared latch field → adjacent hot fields invalidate the line on every acquire
    one shared word acquired per element of work → the zone-map step's CPU tripled under a sliced Prep (#886) and the
      Migrate loop's per-entity cost carried a 44 % surcharge at W = 8 (#926); both read as "the parallel fence does
      not scale" rather than as a latch, because the operation count is unchanged and only the coherence traffic moves
    a fence slice growing a structure its siblings hold → their writes land in an abandoned generation and are lost
      silently, which for a zone map is the false negative the type promises cannot happen

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
  invariant Red = { C ∈ S : (cellX + cellY + cellZ) % 2 == 0 } where (cellX, cellY, cellZ) = grid.CellKeyToCoords(ClusterCellMap[C])
  invariant Black = { C ∈ S : (cellX + cellY + cellZ) % 2 == 1 }
  note the grid gained a Z axis in #872 step 8; a flat world has cellZ == 0 throughout, so its partition is unchanged.
    The three-dimensional parity is still a proper 2-colouring for 6-neighbour adjacency, which is the property the
    two-phase dispatch actually relies on.
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

---

## Module: Spatial maintenance telemetry (Issue #911)

### SO-01: The telemetry surface has two clocks, and zero is a value `[silent]`
  invariant the surface carries THREE kinds of member, and reading one as another is the failure mode:
    RATES — the `...Count` / `...Ms` members produced by a tick's fence, reset at the top of every fence. A consumer
      polling at its own rate reads one arbitrary tick out of hundreds, so a per-second figure must be differentiated
      from the cumulative members, never read off one of these
    CUMULATIVE — `Total...` and `RepairQueueEvicted`, which only grow; these are what a rate is differentiated FROM
    LEVELS — `ActiveClusterCount`, `RepairQueueDepth`, `MaxClusterOverhang`, `MeasuredNsPerEntity`: a standing value,
      neither reset per tick nor monotonically accumulating. Differentiating a level yields nonsense — "clusters per
      second" off `ActiveClusterCount` is the concrete misuse this clause exists to name
  invariant MaxClusterOverhang is the one LEVEL that is also monotonic: a running maximum that never falls and
    never resets, because every kNN ring widens by it and too small loses results while too large only widens a
    search. GetSpatialTelemetryTotal therefore MAXES it across archetypes; summing would widen every ring by the sum
    of bounds no single archetype ever had
  invariant zero means zero, never "unknown". An archetype with no cluster state, an out-of-range id and a quiet
    tick all report zero, and no consumer may invent a distinction the API does not make
  invariant the tightness triple is one reading, not three numbers. MeanClusterExtentRatio and MeanPackingBound are
    means over TightnessSampleCount clusters — the clusters the fence WROTE this tick, not the clusters that exist —
    so a settled world reports zero samples and both means read zero. Publishing the sample count is what keeps that
    distinguishable from "the clusters are points", which is the whole reason it is on the surface
  invariant GetSpatialTelemetryTotal folds by KIND, not uniformly: extensive counters sum, MaxClusterOverhang maxes,
    and the two tightness means are re-derived from summed numerators over the summed sample count. Averaging the
    per-archetype means would weight an archetype that scanned one cluster equally with one that scanned ten thousand
  invariant MigrationTotalMs is CPU-milliseconds SUMMED ACROSS WORKERS, not a span: W workers each busy for 1 ms
    report W. Any surface displaying it must label it as such — and must not present it as the cost of the fence
  invariant the number that answers "how long did the fence block the engine" is a SPAN, and it is a different
    member: `DatabaseEngine.LastFenceSpanMs`, published from the runtime's `TyphonRuntime.LastFenceWallTicks` —
    Prep's start to the last phase that dispatched, so the six phase spans PLUS the scheduler's gaps between them.
    The sum of the six is what the partitioning COSTS; the span is what the host WAITS, and a frame budget is spent
    in the second. `MigrationTotalMs / LastFenceSpanMs` is roughly the parallelism the work achieved
  invariant the span is not the whole interruption, and the surface carries BOTH because the difference is the part
    no worker count removes. `LastFenceSpanMs` starts at Prep's Prepare; the fence call also runs a serial prep first
    on the tick thread — context reset, dormancy drain, `ProcessTableFence` over every component table — inside the
    same epoch fence window, with no user system running. `DatabaseEngine.LastFenceStallMs` times the whole call and
    is what a host budgets a frame against; `LastFenceStallMs - LastFenceSpanMs` is the serial remainder, which is
    Amdahl's fraction for the fence. A worker-count sweep reporting only the span claims a speed-up on part of the
    stall, so the two are never alternative measurements of one thing and a surface showing one must name the other
  invariant the naming actively misleads and the rule says so once rather than letting each reader rediscover it:
    `FenceExecSystem.TotalWallTicks` says "wall" and is a SUM across chunks (a CPU-per-unit figure feeding
    `LiveFenceCostModel`), while `PhaseSpanTicks` is the elapsed one. A sum cannot express a speed-up, so reading
    `TotalWallTicks` as a latency is wrong in both directions — it falls with more chunks when memory-stall-bound
    and rises with more once per-chunk setup dominates
  invariant `LastFenceSpanMs` is ZERO on a host that drives `WriteTickFence` itself instead of running the parallel
    fence, because the phase-exec systems that time it never run. Zero means "the parallel fence did not drive this
    tick", which is the same zero-means-zero discipline as the rest of the surface and not a missing measurement
  invariant MigrationExecuteMs is a sum over SLICES, and MigrationSliceCount is what makes it divisible. The parallel
    fence sizes the Migrate phase's slices from the worker count, so the number of spans summed into it rises with W
    while the workload fixes the entity count — every per-slice fixed cost inside the bracket is then charged again to
    every entity. `MigrationExecuteMs / MigrationCount` is a per-entity cost only at MigrationSliceCount == 1;
    otherwise it also carries `slices x per-slice fixed / entities`, and MigrationPrologueMs + MigrationEpilogueMs is
    that term, measured. Both are PART of MigrationExecuteMs, never additional to it
  invariant a summed-CPU member rising with the worker count is not, by itself, contention. The comparison that
    decides it is against the achieved parallelism — CPU over span, which the engine already computes as
    `DatabaseEngine.LastFenceMigrationParallelism` — because CPU per entity rising by the same factor the parallelism
    rises is work being SPREAD at unchanged wall cost. Only the excess over that is contention, and a surface
    presenting one without the other cannot express the difference
  invariant "cannot express the difference" binds the EXPORT, not only the accessor: every surface carrying a
    summed-CPU member must carry `LastFenceMigrationParallelism` too, which is why it is public and why
    `typhon.ecs.spatial.fence_migration_parallelism` is exported beside `migration_duration_ms`. A consumer that can
    read the summed figure and not the ratio is in exactly the position the clause above describes, and reads every
    added worker as a regression
  invariant the ratio is stored AS MEASURED, including below 1. A phase whose span exceeded its own summed CPU is
    dispatch overhead swallowing the work — the single most useful reading the member has, and the one a floor at 1
    makes indistinguishable from a healthy serial tick. The consumer needing a floor applies its own:
    `ArchetypeClusterState.ObserveMigrationCost` divides by `max(parallelism, 1)`
  invariant it divides only a numerator its own denominator covers. The ratio spans Migrate + IndexMassUpdate +
    EntityMapUpdate, so the matching numerator is MigrationTotalMs. MigrationExecuteMs brackets the migrant loop
    ALONE — roughly half a migration since the two applies moved into their own phases — and dividing it by this
    ratio yields the elapsed time of nothing
  invariant `LastFenceMigrationParallelism` is the one member that is deliberately STALE rather than reset: a tick
    whose migration phases did no work leaves the previous value standing, because zero here would read as
    "infinitely parallel" wherever it is used as a divisor. That is the single documented exception to zero-means-zero
    on this surface, and it is why this member alone can describe a tick other than the last one
  invariant CrossingsExecuted + RelocationsExecuted + RepairsExecuted == MigrationCount, exactly. A Migrate slice
    mixes all three kinds by construction — the queue is sorted by destination cell key, not by kind — so a per-kind
    cost is unattributable without the split, and the identity is what makes it checkable rather than trusted. These
    are ALWAYS counted, unlike the same split on the trace record: needing the profiler on to attribute a cost would
    perturb the bracket the cost is measured in
  invariant FinalizeLockAcquisitions counts EVERY exclusive acquisition of the archetype-wide latch, which is why all
    of them go through `PaddedFinalizeLock.Enter`/`Exit` rather than through the latch directly. A site added straight
    onto `.Lock` would be invisible to the count, and a partial count is worse than none because it reads as evidence
    of an uncontended latch
  invariant reading is allocation-free and lock-free — plain field reads of live engine state, torn only across a
    fence boundary. No accessor may take a lock or allocate to serialise against the fence
  scope: SpatialMigrationTelemetry.MaxClusterOverhang, SpatialMigrationTelemetry.TightnessSampleCount,
    SpatialMigrationTelemetry.MeanClusterExtentRatio, SpatialMigrationTelemetry.MeanPackingBound,
    SpatialMigrationTelemetry.MeanTightnessToBound, SpatialMigrationTelemetry.CellTreePromotions,
    SpatialMigrationTelemetry.CellTreeDemotions, SpatialMigrationTelemetry.MigrationSliceCount,
    SpatialMigrationTelemetry.MigrationPrologueMs, SpatialMigrationTelemetry.MigrationEpilogueMs,
    SpatialMigrationTelemetry.CrossingsExecuted, SpatialMigrationTelemetry.RelocationsExecuted,
    SpatialMigrationTelemetry.RepairsExecuted, SpatialMigrationTelemetry.FinalizeLockAcquisitions,
    ArchetypeClusterState.FinalizeLockAcquisitions, DatabaseEngine.LastFenceMigrationParallelism,
    DatabaseEngine.GetSpatialTelemetry,
    DatabaseEngine.GetSpatialTelemetryTotal, DatabaseEngine.LastFenceSpanMs, DatabaseEngine.LastFenceStallMs,
    TyphonRuntime.LastFenceWallTicks,
    FenceExecSystem.PhaseSpanTicks, FenceExecSystem.TotalWallTicks, ArchetypeClusterState.ClusterTightnessSample
  verified: SpatialMigrationTelemetryTests.Tightness_ReportsNoSamples_RatherThanAStaleMean_OnAQuietTick pins the
    zero-samples case; MaxClusterOverhang_IsPublished_AndIsARunningMaximumRatherThanAPerTickValue pins the third
    clock; Total_MaxesTheOverhang_AndWeightsTheTightnessMeansBySample pins the per-kind fold across two archetypes;
    Accessor_AllocatesNothing pins the allocation-free read; FenceSpanMs_IsZero_WhenTheHostDrivesTheFenceItself pins
    the serial-fence zero, so it cannot be read as a fence that cost nothing;
    ExecutedKinds_SumExactlyToTheMigrationCount pins the per-kind identity;
    MigrationSliceCount_IsPublished_AndBoundsThePrologueAndEpilogueWithinTheExecuteSpan pins the divisibility of the
    summed span; FinalizeLockAcquisitions_CountEveryAcquisition_AndResetPerTick pins the latch count;
    FenceStallMs_CoversTheSerialPrep_ThatTheSpanExcludes pins the stall-against-span relation;
    MigrationParallelism_IsExportedBesideTheSummedCpuGauges_AndIsNotFlooredAtOne pins both the export and the
    unclamped storage, so neither can be dropped without a red test
  on_violation:
    a per-tick member read as a rate → a number sampled from one tick of hundreds, presented as throughput
    the overhang summed rather than maxed → every kNN ring widens by a bound no archetype has
    the sample count dropped → "nothing moved" becomes indistinguishable from "the clusters are points"
    the tightness means averaged per archetype → a quiet archetype halves a busy one's reading
    summed CPU shown where the span belongs → a frame budget compared against a number W times too large, which is
      how an 8 ms budget bought one repair unit
    the slice count dropped → a per-slice fixed cost divided by entities, read as per-entity contention that grows
      with the worker count; this is #912, which stood as an unowned anomaly in the design for three steps
    summed CPU compared across worker counts without the parallelism beside it → work being spread reads as work
      getting slower, in the direction that condemns the parallel fence for scaling
    an acquisition added straight onto `.Lock` → a latch that reads as uncontended because its busiest caller is
      not counted
