# Indexing Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-08-06 |
| Domain | Secondary-index ownership and scope; ordered index reads |

> Type-location: `Ecs/internals/ArchetypeClusterState.cs` (`IndexSlots`, `ClusterIndexSlot`, `ClusterIndexField`),
> `Indexing/internals/BTree*.cs`, `Ecs/public/EcsQuery.cs` (the index-home guard and the ordered path),
> `Ecs/internals/KWayMergeHelper.cs` (`ArchetypeSortedStream`, `KWayMergeState`),
> `Querying/internals/FkReverseLookup.cs`, `Ecs/internals/ArchetypeRegistry.cs` (`ValidateComponentDeclarations`),
> `Typhon.Generators/ArchetypeAccessorGenerator.cs` (`TPH1003` / `TPH1004`).
>
> Decision record: ADR-063 `adr/063-unconditional-cluster-storage-single-query-index-home.md`.
> Design: `design/Indexing/index-ownership-consolidation.md`, `design/Indexing/index-scope-and-uniqueness.md`.
> (Named, not linked: the design corpus is a separate private repository — see this file's `README.md`.)

---

## Module: IX — Index ownership and scope

Before #629 a secondary index could live in either of two homes: a per-`ComponentTable` tree keyed by component *name*,
or a per-archetype tree. The homes were not equivalent — they disagreed about what a **unique** constraint meant, and
neither said so — so "which home is this index in?" silently decided what guarantee the schema bought. The
per-`ComponentTable` home is deleted; these rules exist to keep it deleted, and to keep the surviving claim honestly
scoped.

### IX-01: Exactly one B+Tree per (archetype, indexed field) `[silent]`
  invariant ∀ archetype A, ∀ indexed field f of a component A carries: |{B+Trees indexing (A, f)}| == 1, located at
            `A.ClusterState.IndexSlots[s].Fields[f].Index`
  never a second index home for the same (archetype, field) — not a shared per-`ComponentTable` tree, not a fallback
        structure consulted when the per-archetype tree is missing
  scope: ArchetypeClusterState.IndexSlots, ClusterIndexSlot, ClusterIndexField, EcsQuery, FkReverseLookup
  on_violation: two homes drift because every index mutation must be applied to both, and a query answering from the
                stale one returns wrong rows with nothing raised. Realised twice before the home was removed: #670 (the
                schema-migration backfill indexed a Versioned component once per REVISION rather than once per entity)
                and #663 (a query consulted a home nothing maintained and returned empty).
  rationale: cluster index values are `ClusterLocation`, which is archetype-local (IX-02), so a shared tree would need a
             dual value format and a dispatch on which kind it just read — the alternative ADR-045 §2 rejected.

### IX-02: An index value names a slot in its own archetype `[fatal]`
  invariant every value stored in a per-archetype B+Tree is `ClusterLocation = clusterChunkId * 64 + slotIndex`,
            interpretable ONLY against the archetype that owns the tree
  never a `ClusterLocation` resolved against a different archetype's cluster segment
  scope: ArchetypeSortedStream, ClusterIndexField, EcsQuery
  on_violation: reads land on an unrelated entity's bytes — wrong data, no error
  requires IX-01 (one home per (archetype, field) is what makes the owning archetype unambiguous)

### IX-03: A query that cannot reach an index raises rather than under-reporting `[correctness]`
  invariant if an archetype CARRIES the where-component but exposes no per-archetype index for it, the query throws
  never silently skipping such an archetype, or answering from the remaining archetypes alone
  enforce (query) `EcsQuery` raises after the archetype walk when any matched archetype carried the component without an
          index home
  enforce (FK) `FkReverseLookup` raises when a reverse-lookup candidate owns no per-archetype FK index
  note an archetype that does NOT carry the component at all is skipped silently, and that is correct: a polymorphic
       query names a whole subtree and `WhereField` never narrows the mask, so a component declared on only part of the
       subtree legitimately puts a component-less archetype in front of the guard. Conflating the two turned a valid
       query into a hard throw until #678 step 1 separated them.
  on_violation: an under-reported result set. For cascade delete a missing referrer is an ORPHANED CHILD, which is why
                this raises rather than degrading — the failure is silent and permanent, and the exception is not.

### IX-04: A unique `[Index]` is enforced within one archetype; the subtree scope is designed, not built `[UNBUILT]`
  invariant (today) ∀ unique-indexed field f: uniqueness of f holds within EACH archetype separately, because the
            per-archetype tree is a unique tree and there is no structure spanning archetypes
  invariant (target) uniqueness holds across the DECLARING archetype's subtree — the scope a polymorphic query already
            uses (`ArchetypeRegistry.CollectSubtree`), and therefore the only scope the schema can express
  UNBUILT the spanning structure does not exist. `index-scope-and-uniqueness.md` §4.2 specifies a subtree-scoped
          `key → EntityId` hash per (declaring archetype, component, field), with per-bucket latching, WAL
          participation, crash recovery, rebuild-from-data and an open-time validation pass. Steps 2-5, 9 and 10 of its
          §7 are unimplemented; acceptance tests exist and are `[Ignore]`d.
  never documenting or promising database-wide uniqueness. Two archetypes in UNRELATED trees may each hold the same key
        and that is legal by design — no query spans two roots, so the duplicate is not observable.
  scope: UniqueConstraintViolationException, ClusterIndexField, CollectSubtree
  on_violation: (today) a duplicate across two archetypes of one tree is accepted and a point query returns two rows for
                a key documented as unique
  requires IX-05 (the build-time rejection is what makes this gap safe to carry: an author cannot express a constraint
           whose scope the engine would have to guess at)
  verified: UniqueIndexScopeTests — the passing control asserts the per-archetype guarantee; the `[Ignore]`d cases are
            the target scope and must go green when the structure lands

### IX-05: An unenforceable unique scope never compiles `[correctness]`
  invariant a unique `[Index]` on a component declared by 2+ archetypes **of the same archetype tree** is rejected at
            build time (`TPH1003`); a component re-declared within one inheritance chain is rejected (`TPH1004`)
  invariant the same component declared by archetypes in UNRELATED trees is ACCEPTED — each root already owns its own
            tree, so the constraints are independent, cost nothing to keep, and no query can compare them
  enforce (build) `ArchetypeAccessorGenerator` emits `TPH1003` / `TPH1004`
  enforce (runtime) `ArchetypeRegistry.ValidateComponentDeclarations`, called from `Freeze()`, is the open-world
          backstop for schemas assembled across assemblies where the generator sees only one side
  rationale the rule is per TREE, not per schema. Counting declarers across the whole schema was tried first and
            rejected THREE schemas already in this repo, none of them defective. `TPH1004` is not merely hygiene: a
            child re-declaring an inherited component silently burned a second, unaddressable slot — one of the 16.
  on_violation: a unique constraint loads whose scope the engine cannot determine, so it enforces something narrower
                than the declaration says
  verified: ComponentDeclarationValidationTests, ComponentDeclarationDiagnosticTests

---

### IX-06: Whoever skips an index removal must be the complement of whoever performs it `[fatal]` `[silent]`
  invariant when a destroy defers an entity's index removal to the tick fence, the set of slots it defers MUST equal the set the
            fence's shadow capture actually recorded for that entity:
            `deferred(entity) == ArchetypeClusterState.FenceRecordedSlots(entity)`, and the destroy removes the complement inline, whatever the
            discipline of the destroy
  invariant a mark says what was recorded. On an archetype indexing both a SingleVersion and a Transient slot, `ClusterShadowBitmap` says the
            Transient keys are recorded and `SvShadowBitmap` the SingleVersion ones: a Commit-discipline capture records the Transient keys only,
            a TickFence one both, and a set `SvShadowBitmap` bit implies the other. On any other archetype one mark covers every fence-indexed slot
            (a Commit capture can only reach one with no indexed SingleVersion slot). Once a TickFence writer has recorded an entity's
            SingleVersion keys, the tree holds those, not HEAD's: a Commit-discipline publish leaves that entity's move to the fence
            (`Transaction.PublishStagedEntry`, `ArchetypeClusterState.SvKeysRecorded`), and the write shares the TickFence window (QFENCE)
  never deciding the deferral from a per-ENTITY signal when the thing deferred is per-SLOT. `ClusterShadowBitmap` is set per entity, by a
        write that changes a key of a fence-indexed component (`EntityRefMut.Set`, `ClusterRef.Set`, a key-changing `WriteSpatial`; a write through
        a returned ref captures unconditionally) and records every fence-maintained indexed slot of the entity; the shadow BUFFERS hold only
        the indexed non-Versioned slots that `ShadowClusterIndexedFields` captured. Before #1344 / #1346 a write to ANY component set the bit.
        Before #1339 one bit served both disciplines: set by a Commit capture over the Transient keys only, it made a later TickFence capture of
        the SingleVersion keys record nothing
  invariant a slot so released is not claimed again before that fence: its index entries are still in the trees, and the drain matches a shadow
            entry to its slot, not to its entity (#1188)
  enforce both sides read the split from the cluster state's masks (`ArchetypeClusterState.SvFenceIndexedSlotMask`,
          `TransientFenceIndexedSlotMask`, their union `FenceIndexedSlotMask`), computed once at initialisation, rather than each computing it
  enforce every claim path counts a slot marked in `ClusterShadowBitmap` as taken (`ArchetypeClusterState.FenceOwedBits`, read inside `TakenBits`,
          `ClaimFirstFree` and `ClaimExact`); the fence clears the bitmap after its drain and before Migrate claims, so the hold lasts one tick at most
  scope: ArchetypeClusterState.FenceRecordedSlots (what the fence holds a record of), ArchetypeClusterState.CaptureFirstWrite,
         ArchetypeClusterState.CaptureFirstWrites (the captures), ArchetypeClusterState.SvShadowBitmap, ArchetypeClusterState.SvKeysRecorded,
         EntityRefMut.ShadowClusterIndexedFields, Transaction.FlushPendingDestroys (the destroy hand-off; removes its complement via
         RemoveClusterIndexEntries' slot mask), Transaction.PublishStagedEntry (an entity whose SingleVersion keys are recorded is the fence's),
         Transaction.ReconcileClusterIndexAndViews (commit-scoped maintenance must not run
         for an entity the same transaction destroys), ArchetypeClusterState.FenceOwedBits, ArchetypeClusterState.TakenBits (the slot hold)
  on_violation: the index keeps an entry for a released cluster slot. Loud on the next rebuild for a `Unique` index
                (`EntryCount` exceeds the distinct keys the data holds); SILENT for `AllowMultiple` - a leaf value naming an
                unoccupied slot, served by whichever query plan reaches it. Reused before the fence, the slot's new occupant is taken for
                the destroyed entity with changed keys: the drain moves the dead entity's entries onto it, the index answers keys no
                entity holds, and until the fence an ordered query returns the newcomer twice.
  rationale: #711. One write to an UNINDEXED Transient sibling was enough to reach it - no key move required, which is why the
             issue's "mutate-then-destroy" title describes a symptom rather than the trigger. The boundary is stated by a pair of
             cases: writing only the indexed component and destroying passes; writing only the unindexed sibling and destroying
             fails identically. #1339: one entity written in one tick under both disciplines - a Commit write of a Transient key marked it over no
             SingleVersion record, so a later TickFence move or destroy of it left the SingleVersion entry on the old key; and a Commit publish after a
             TickFence move moved the entry from HEAD, a key the tree did not hold.
  requires IX-01 (one home per (archetype, field) is what makes "the set of slots" well defined)
  verified: ClusterIndexMatrixTests.MixedPublicationTimings_MutateAndDestroy_LeaveTheIndexAgreeing,
            DestroyAfterWritingOnlyAnUnindexedSibling_LeavesTheIndexAgreeing (the sharper half - no key move anywhere),
            DestroyAfterWritingOnlyTheIndexedComponent_LeavesTheIndexAgreeing (the control, green before the fix) [VerifiesRule];
            QueryBetweenWriteAndFenceTests.ADestroyedSlotOwingTheFence_IsNotReusedBeforeIt [VerifiesRule] — a key move (unique, or multi-value), a
            destroy, a spawn into a full archetype, a write to the newcomer, the fence: the newcomer is returned once before the fence and the
            index agrees with the data after it; both cases red with the hold removed. ADestroyAfterAWriteToAnotherComponent_LeavesNoStaleRow_… and
            IndexWriteRecordingTests.AWriteKeepingTheKeys_ThenADestroy_RemovesTheEntriesAtOnce: a write that changes no key records nothing, so the
            destroy removes inline and the slot owes the fence nothing; MixedDisciplineIndexTests [VerifiesRule] - both disciplines on one entity in
            one tick, moves and destroys in each order (three red before #1339)

### IX-07: No writable memory over a fence-maintained indexed field; every write to one records the old keys first `[silent]`
  invariant cluster iteration hands out no writable memory (`ClusterRef.GetSpan`, `ClusterRef.Get`) over a component slot with an `[Index]`
            field the tick fence maintains — SingleVersion or Transient (`ArchetypeClusterState.FenceIndexedSlotMask`, independent of the
            discipline). It throws, in every build
  invariant every write to such a slot goes through a path that, on the entity's first KEY-CHANGING write this tick, records the keys of ALL
            its fence-maintained indexed fields before the store (`ArchetypeClusterState.CaptureFirstWrite`, batched
            `ArchetypeClusterState.CaptureFirstWrites`): `EntityRefMut.Set`, `ClusterRef.Set` (single and batched), and `ClusterRef.WriteSpatial`. A
            write whose value keeps every indexed key of the component records nothing (#1346): the tree's keys are still the entity's, so an entity
            whose stored keys differ from its tree keys is always marked. A write through a returned ref (the value unknown) records unconditionally
  invariant the keys are recorded before the entity's shadow bit is set, and kept only by the call that sets it (per bit, for the batched
            `DirtyBitmap.TestAndSetWord`)
  invariant the same call marks the entity dirty, so the fence drains the records on its dirty branch; a fence that finds records with nothing
            dirty drains them too, every indexed column counted as written, and re-derives those clusters' zone maps — no record outlives its
            fence. No write path reaches that backstop by design
  invariant the drain's skip of components nobody wrote reads THIS tick's written-slot union: every branch takes it before its drain
  never detecting writes through handed-out memory (a copy of the keys at hand-out, diffed at the fence): it cannot know which entities a
        span will write, so it records and diffs all of them (+16 ns per entity, measured), and records left by a span only read outlived a
        fence that skipped the drain and moved a dead entity's keys onto the next occupant of its slot (the reverted first attempt at #1187)
  enforce the mask test in `GetSpan` / `Get` (one load and one bit test per call); `ClusterRef.Set` refuses what an in-place store cannot do —
          a Versioned slot, a Commit-default component, the spatial slot, the realm key, a SingleVersion slot under the Commit discipline, an
          empty slot
  scope: ArchetypeClusterState.FenceIndexedSlotMask, ArchetypeClusterState.CaptureFirstWrite, ArchetypeClusterState.CaptureFirstWrites,
         ArchetypeClusterState.IndexedKeysDiffer, DirtyBitmap.TestAndSetWord, ClusterRef.GetSpan, ClusterRef.Get, ClusterRef.Set,
         ClusterRef.WriteSpatial, EntityRefMut.ShadowClusterIndexedFields, EntityRefMut.WriteSlotRef, DatabaseEngine.PrepareArchetypeFenceCore,
         ArchetypeClusterState.FenceWrittenSlots
  on_violation: the index keeps the key the entity held before the write, for good: an index-path query returns the entity for a value it
                no longer holds and misses it for the one it does, while a scan answers correctly — which one a caller gets depends on the
                planner's estimate
  rationale: #1187. A span is "change anything, any time, without saying": an index keyed on the old value can only follow it by guessing
             with copies. ADR-070 already settled point access the same way: an explicit `Set`
  requires IX-06 (the records are what a destroy hands to the fence)
  verified: ClusterRefIndexedWriteTests [VerifiesRule] — GetSpan / Get throw on an indexed component and not on another; Set moves a unique
            and a multi-value key at the fence on every path, with nothing returned at the old key before it; Set and OpenMut in one tick, either
            order; Set, destroy, spawn, fence; a Transient indexed component under both disciplines, read off its tree; the clean-branch backstop,
            staged by draining the dirty bitmap by hand after a tick that wrote only another component (red with the stale union kept).
            ClusterMigrationTests.WriteSpatial_ChangingTheIndexedTag_MovesItsKey (single; batched, beside a neighbour keeping its tag) and
            WriteSpatial_KeepingTheIndexedTag_RecordsNothing [VerifiesRule]. ClusterPureTransientIndexTests.ClusterSet_MovesTheKeyInTheTreeAtTheFence
            (the one-base capture) and AKeyMove_AfterATickThatWroteOnlyAnotherComponent_MovesTheKey (the pure-Transient fence read the previous
            tick's union; red before) [VerifiesRule]. Without the capture in `ClusterRef.RecordWrite` (Set's and WriteSpatial's), nine cases fail;
            the others — the throws, OpenMut first, an unchanged tag, the refusals, the OpenMut-only two-tick case — do not depend on it.
            IndexWriteRecordingTests [VerifiesRule] — a write keeping every key records nothing (both APIs; red with the skip disabled in each), then a
            key change or a destroy in the same tick stays exact; a write to an unindexed component records nothing (#1344); the batched Set records
            exactly the entities whose keys it changes (red with its capture disabled), also through the per-entity fallback

### IX-08: The fence applies a field's index moves as one batch, decided on the tick's final state `[silent]`
  invariant the shadow drain stages the moves of changed keys (`FieldShadowBuffer.StageMove`) and applies a field's moves in one
            `BTreeBase.ApplyMoves` before Migrate reads the trees and the elementId tails, and before the buffers are reset: at the end of the field's
            drain on the atomic path; on the parallel fence, after the Prep slices, the IndexMoves phase applies the batch's in-place part in key
            ranges on the workers (`BTreeBase.ApplyMoveRange`) and the serial tail (`DatabaseEngine.ApplyStagedIndexMoves`) finishes it
            (`BTreeBase.FinishMoveBatch`: any range not applied, then the structural remainder)
  invariant the ranges of one batch are key intervals cut at leaf edges (`BTreeBase.PrepareMoveBatch`: sampled quantiles snapped up to their leaf's
            HighKey): every event of one key, and every key one leaf owns, fall in one range, so no two ranges reach the same leaf and each decides
            its unique collisions alone; a range that reaches a leaf beyond its cut throws. Each range gathers its events from the batch (departures
            before arrivals, in move order) and sorts them itself; an AllowMultiple arrival's new element id goes to the tree's per-move array, copied
            into the moves by the finish — the moves are where a concurrently gathering range reads departures' element ids
  invariant a unique move collides only when its new key is taken twice in the batch or held by an entity that stays; departures sort before
            arrivals for one key, so swaps, cycles and chains within a tick are legal (#1342). Only the departure of the key's holder vacates it. A
            collided move still leaves its old key — its data no longer holds it — and is not indexed under the new one, which the holder keeps; the
            collision is raised (#675's message) once the drain or every tail has finished, never mid-drain, and the entity is indexed again by its next
            key-changing write
  invariant an AllowMultiple arrival takes a departure's element in place under its key (`UpdateInBuffer`), and the entity's elementId tail is
            written with the element the batch returns, recorded per cluster run after its last store (PS-10)
  invariant a key that keeps entities gives back the buffer chunks its departures emptied, in the same batch (`VariableSizedBufferSegment.CompactEmptyChunks`:
            parked in the buffer's free list up to a reader's threshold, the rest freed; never the root or the append cursor, IXW-08) (#1343)
  invariant Migrate's location update of a unique index names the location it replaces (`BTreeValueUpdate.ExpectedValue`, the migrant's old
            location): it applies only where the key still names the migrant, and leaves alone a key the tree holds for another entity — a
            collision's holder when the migrant is the loser (#1348)
  invariant the bulk path changes no tree structure: a leaf is rewritten in place only while it stays between half full and full (its departures
            before its arrivals); a new key, an emptied key and any other leaf go through the latched single-entry path
  enforce `ApplyMoves` runs only with no other writer of the tree (EW-01: the atomic drain's thread, or the tail after every slice retired; the
          ranges of one batch only with each other, between the Prep slices and the tail), and
          licensed by it touches AllowMultiple buffers without their leaf's latch; it latches each leaf it rewrites (and bumps the version of each leaf
          whose buffers it changed, after the change), for readers that revalidate. A throw after it starts faults the tree (IXW-09); a range that
          throws on a worker is not rethrown there — that would fail every later fence phase and leave other trees' batches unfinished — but by the
          tail, once every batch is finished. The tail finishes every archetype's batches first, then runs every archetype's tail, whatever throws,
          and raises the first failure at the end: a finished batch is always reset, never replayed; an unfinished one has faulted its tree
  scope: BTreeBase.ApplyMoves, BTree.ApplyMoves, BTree.DecideUniqueCollisions, BTree.MergeLeaf, FieldShadowBuffer.StageMove,
         FieldShadowBuffer.StageMoveAt, DatabaseEngine.ApplyStagedMoves, DatabaseEngine.ApplyStagedIndexMoves,
         DatabaseEngine.PrepareArchetypeFenceTails, ArchetypeClusterState.PendingIndexCollision, BTreeBase.PrepareMoveBatch,
         BTreeBase.ApplyMoveRange, BTreeBase.FinishMoveBatch, DatabaseEngine.PrepareStagedIndexMoveBatches,
         DatabaseEngine.ApplyIndexMoveRange, FenceIndexMovesExecSystem, VariableSizedBufferSegment.CompactEmptyChunks, BTreeValueUpdate.AppliesOver,
         BTreeBase.WriteBulkEntry
  on_violation: a move applied against an intermediate state rejects a legal swap and aborts the fence half drained; a batch applied twice or never
                leaves the index on keys the data does not hold; an element id not written back sends Migrate's location update to the wrong chunk
  rationale: #1346. One `Move` / `MoveValue` per changed key cost ~350–450 ns, nearly all of it per call (descent, latch, accessor rent and return):
             1M AllowMultiple moves took ~650 ms of fence, 1M unique ~470 ms. Sorted and applied per key and per leaf, ~100 ms and ~130 ms. Applied
             whole in the parallel fence's serial tail, the unique batch took twice the per-move path's span at W = 16 (~130 against ~65 ms, which
             spread its moves over the slices); in ranges on the workers, ~50 ms, and ~35 ms once each range sorts its own events (the serial sort
             of the whole batch had been 17–28 ms of the phase)
  requires IX-07 (the records the batch is built from), EW-01 (the licence to apply it)
  verified: BTreeApplyMovesTests [VerifiesRule] — every key shifted (unique, multi), swaps / cycles / chains, collisions and their cascade, moves that
            split or merge leaves (single-entry fallback), misses, unbalanced multi-value moves creating and emptying keys, element ids that remove
            each value exactly; IndexWriteRecordingTests.UniqueKeysSwappedOrRotatedInOneTick_MoveAtTheFence [VerifiesRule] (#1342: threw before);
            ClusterUniqueIndexDrainTests.TwoClustersWrittenToOneUniqueKeyInOneTick_RejectExactlyOne_AndTheTreeHoldsTheKeyOnce and
            IndexWriteRecordingTests.AUniqueCollision_LeavesTheLoserOutOfTheIndex_UntilItsNextWrite (the loser leaves its old key; raised once).
            BTreeApplyMovesTests.Unique_APartitionedBatch_AppliedConcurrently_ReachesTheWholeBatchState (ranges on threads at once, one left for the
            finish; red with the leaf-edge snap removed: a split key run collides) and Multi_APartitionedBatch_… (element ids handed back move every
            value back with no miss), Unique_APartitionedBatch_MatchesTheWholeBatch_OnRandomMoves (same outcome, first collision and tree as the
            whole batch); IndexMovesParallelFenceTests [VerifiesRule] — moves applied in ranges by the runtime's IndexMoves phase keep the
            index in agreement with the data, with no element-id miss on later ticks; a range that fails on a worker is applied by the tail, the
            failure raised, and the next ticks' moves kept (red with the failed archetype's tail skipped: its shadow bits outlived its buffers);
            BTreeApplyMovesTests.Multi_ValuesLeavingAKey_GiveItsEmptiedChunksBack (#1343: a round trip grew the segment 46 chunks without the
            compaction); MigrateUniqueIndexTests [VerifiesRule] (#1348: the loser's migration repointed the holder's key) and
            BTreeBulkUpdateTests.UpdateValues_AnEntryExpectingAnotherValue_IsSkipped (every applier path)

## Module: IXS — Ordered index reads

An ordered query reads its index through an OLC (optimistic lock coupling) scan: no locks, a version snapshot per leaf,
validated after the fact. Writers are expected to modify a leaf mid-scan — that is what the restart machinery is for.
These rules state what the reader owes its caller under that concurrency, because the contract was never written down
and the code did not keep it: a 4 000-entry tree, scanned while a writer inserted behind the cursor, returned **18 899**
keys.

### IXS-01: A range scan emits strictly monotonic keys `[correctness]`
  invariant ∀ consecutive keys kᵢ, kᵢ₊₁ emitted by one range scan: kᵢ < kᵢ₊₁ ascending, kᵢ > kᵢ₊₁ descending
  never emitting the same entry twice, and never moving backwards
  note this is NOT a snapshot guarantee, and must not be strengthened into one. An entry inserted AHEAD of the cursor
       may or may not be seen; one inserted BEHIND it will not be. Both are legal — an OLC scan trades snapshot
       semantics for lock-free reads. Only the monotonicity is owed.
  enforce (per emission) the key is compared against the last key emitted and the scan steps forward if it is not
          strictly ahead. This is the ONLY thing that catches a writer shifting the entry array within the leaf the
          cursor is standing on — no version check runs on the intra-leaf step.
  enforce (per leaf exit) the leaf version is validated before the sibling link is followed
  scope: RangeEnumerator, RangeMultipleEnumerator, FillOrderedPage, ArchetypeSortedStream
  on_violation: a caller cannot distinguish a duplicate from a genuine second row. `Take(N)` returns N rows of which
                some are repeats; a result list silently gains entries; a `Count` over the scan over-counts.
  rationale: the previous restart reset the cursor to the leaf's first entry and replayed it, and the intra-leaf step
             had no validation at all. Both produced duplicates that every result-checking test passed, because
             emitting extra rows is not a wrong ANSWER at any single row.
  verified: BTreeRangeScanRestartTests [VerifiesRule]

### IXS-02: A parked cursor resumes by key, never by leaf position `[silent]`
  invariant the resume point of a suspended range scan is the last KEY emitted, never a leaf index or slot number
  never resuming at a remembered index into a leaf
  enforce `LeafPageCursorState.ResumeKeyBits` holds the key's raw bits; a leaf-position hint may be carried as an
          optimisation but is validated against that key before use and discarded when it does not match
  enforce a leaf whose version failed validation is re-descended from the resume key (`FindLeaf`), not resumed in place
  scope: LeafPageCursorState, FillOrderedPage, RangeEnumerator, ArchetypeSortedStream
  on_violation: a leaf index is meaningless after the leaf splits, merges, or gains an entry before the cursor — all of
                which a writer may do while the cursor is parked. The scan then skips or repeats entries depending on
                which way the array moved, with nothing to detect it.
  requires IXS-01 (resuming by key is what makes monotonicity achievable after a structural modification)

### IXS-03: An obsolete leaf is re-descended, never waited on `[fatal]`
  invariant a reader that finds a leaf's OLC version unreadable distinguishes LOCKED (transient — a writer holds it for
            nanoseconds; spinning is correct) from OBSOLETE (permanent — the node was replaced by a structure
            modification and will never become valid)
  never spinning on the obsolete bit
  enforce `OlcLatch.IsObsolete` is checked before any spin-wait; on obsolete the reader re-descends from its resume key
  scope: OlcLatch, RangeEnumerator, FillOrderedPage
  on_violation: livelock. The reader spins until the process is killed — not a wrong answer, a hang, and one that only
                appears under a concurrent structure-modifying operation.
  rationale: `ReadVersion()` returns 0 for both states because both mean "do not trust this snapshot". That is right for
             the return value and wrong as the whole protocol; the caller must separate them.
  note this rule was written for READERS and enforced at two reader sites, and the writers made the identical conflation
       unchecked for as long - see IXW-01, whose livelock (#695) is this rule's `on_violation` reached from the write path.

### IXS-04: A node's items are strictly ascending `[silent]`

  invariant ∀ node n, ∀ i in 1..count-1: n.item[i-1].Key < n.item[i].Key, for LEAF and INTERIOR nodes alike
  never assuming intra-node order because the node's endpoints look right
  enforce `BTree.ValidateNodeKeyOrder` walks every node from the root and compares each item against its predecessor
  scope: BTree.cs (`ValidateNodeKeyOrder`)
  rationale: this is the one property the key search actually depends on, and until #765 nothing checked it.
             `NodeWrapper.CheckConsistency` compares each item against the PARENT separator and pins only the endpoints,
             the chain checks read `GetFirst` and `GetLast` and nothing between, and the separator and HighKey checks read
             one key each. A leaf holding `[1, 9, 3, 5, 12]` satisfies every one of them.
  on_violation: a binary or vectorised search answers "not found" for keys that are present, non-deterministically by
                which half it lands in. That is #297's exact symptom, arriving with no instrument able to say the node
                was the reason - which is how it stayed open across two closes.
  verified: BTreeConsistencyValidatorTests.Mutant_KeysOutOfOrderWithinALeaf_AreReported (mutant)

### IXS-05: The descent and the leaf chain reach the same set of leaves `[silent]`

  invariant {leaves reachable by descending from Root} == {leaves reachable by walking `_linkList`}, and `EntryCount`
            equals the number of items materialised by that walk
  never trusting one structure to describe the other
  enforce `BTree.ValidateDescentAndChainAgree` and `BTree.ValidateEntryCountMatchesChain`, both called from
          `CheckConsistency`, both reporting which ids are in one set and not the other
  enforce for the count specifically, `InsertArguments.Added` must mean "a new leaf entry was created" and not "the value was
          read": a duplicate key on an `AllowMultiple` index stores its value by appending to the EXISTING entry's buffer, so
          every such site reads through `InsertArguments.ValueForExistingKey` and only the entry-creating sites through
          `GetValue`
  scope: BTree.cs (`ValidateDescentAndChainAgree`, `ValidateEntryCountMatchesChain`, `ValueForExistingKey`),
         `NodeWrapper.InsertLeaf` (the one duplicate-append site that reaches the counting tail)
  rationale: they are separate structures maintained by separate code, and every defect in this subsystem's history has
             been one disagreeing with the other. Each individual check walked one of them.
  on_violation: a leaf on the chain but under no ancestor holds keys no descent reaches - found only by the B-link
                right-walk, and permanently lost the moment a hop budget or an empty leaf ends that walk early. A leaf
                under the root but off the chain is invisible to every range scan while lookups still answer from it.
                A drifted `EntryCount` is worse than either, because it is the number the tests assert on: it hides a
                lost key here and manufactures a phantom failure somewhere else. It is also the number the PLANNER reads:
                `IndexStatistics.EntryCount` reports it as the index's distinct-key count, so a drifted count silently
                re-costs every query over that index.
  occurred: #783. The count drifted UP by one per duplicate row on an `AllowMultiple` index, because the general descent ends in
            `if (args.Added) { IncCount(); }` while `Added` was set as a side effect of `GetValue()` - which the duplicate-append
            branch called to read the value it appends to the existing key's buffer. Insertion ORDER decides whether it bites and
            is why it survived: a duplicate whose key is the leaf chain's current last (or first, or the tree's only leaf) is
            handled by an OLC fast path that returns before that tail, so ascending-order inserts counted correctly and every
            existing test built its trees that way. Cyclic keys route through the general descent instead - 2 000 rows over 50
            distinct keys reported 1 142. The tree itself stayed correct throughout: 50 leaf entries, no duplicate keys, every
            row returned by query. Only the counter lied, and both cited mutants stayed green because they test that the
            VALIDATOR reports an injected violation, not that the tree maintains the invariant.
  verified: BTreeEntryCountTests.EntryCountEqualsTheDistinctKeysTheChainHolds (the tree maintains it - asserts the chain BEFORE
            the counter, so "one entry per row" fails differently from "counter drifted"),
            BTreeEntryCountTests.TheStatisticsDistinctKeyCountSurvivesCyclicInsertion (asserted where the planner reads it),
            BTreeConsistencyValidatorTests.Mutant_LeavesOnTheChainButUnreachableByDescent_AreReported (mutant),
            BTreeConsistencyValidatorTests.Mutant_EntryCountDisagreeingWithTheChain_IsReported (mutant)

### IXS-06: A descent answers NOT-FOUND only for a leaf that owns the key's lower bound `[UNBUILT]`

  status WITHDRAWN 2026-08-10, same day it was written, then SUPERSEDED by IXS-07 later the same day. The invariant is right and
         the defect it names is real (#739/#297); the ENFORCEMENT shipped with it was not, and is reverted. Kept unbuilt rather
         than deleted because it states the OUTCOME (a descent must not answer for a leaf that does not own the key) while IXS-07
         states the MECHANISM that was actually missing (the hop was never atomic). If a residual overshoot is ever measured with
         IXS-07 in place, this is the rule to build, and the way to build it is a stored per-node LowKey mirroring HighKey - NOT a
         neighbour read, and NOT a carried separator. Both of those were tried; see below.
  invariant before `OptimisticDescendToLeaf` reports `keyIndex < 0` as conclusive, the leaf it stopped at must not be one the key
            provably belongs to the LEFT of. When it does, the descent overshot and the caller must restart
  never treating `key <= leaf.lastKey` as sufficient evidence that the key is not in the tree
  never enforcing this by calling `KeyBelowLeafLowerBound` from the descent - that predicate reads `leaf.GetPrevious()` and then
        DEREFERENCES it for its HighKey. On the write paths the leaf is locked and that is safe; on the lock-free descent the
        neighbour id can be torn, and dereferencing it faults before any validation can signal a restart. Measured: the test host
        died with no managed stack in 4 of 5 runs of `ChaosStressTests.Light_2T_50E_NoDelete`, against 6 of 6 passing on main and
        4 of 4 passing once the call was removed. This is the hazard `BaseNodeStorage.GetChild` documents in its own comment.
  gap REFUTED 2026-08-10. This field previously read "the descent ALREADY follows a separator to choose each child, and that
      separator IS the lower bound. Carrying it out of the descent answers the question with no neighbour read at all." The first
      half is true and the second does not follow: the descent picks the LARGEST separator <= key (`index = ~index - 1`), so
      `key >= separator` holds by construction at every level. A guard testing `key < carriedSeparator` can never fire. The
      separator is also the value that goes STALE when a borrow moves keys left, so it cannot be both the thing that is wrong and
      the thing that detects it. Do not re-propose this.
  gap what the descent is actually missing is the OLC protocol's SECOND parent validation - the paper's `readUnlockOrRestart`,
      taken AFTER the child's version is sampled rather than before. `OptimisticDescendToLeaf` validated the parent, then read the
      child's version, and never re-checked; an SMO landing in that gap is invisible to both checks taken alone, because the parent
      test has already passed and the child version is sampled after the modification. See IXS-07.
  evidence 6 Remove-NotFound events in 25,701 race-harness iterations, ALL on the general-descent branch, ALL with
           `key < landedLeaf.firstKey` - key 378 on a leaf whose first key is 381, key 88 on one starting at 89 - on leaves
           holding 14 to 21 entries. With the (unsafe) guard in place: 0 in 94,854. The defect is not in doubt.
  on_violation: `Remove` returns false for a key that is present, reachable and correctly chained (#739), and the same descent
                backs `TryGet`, so the identical false not-found is reachable on the READ path (#297).
  requires IXW-03
  requires IXS-07

### IXS-07: A descent hop validates the parent twice — once before the child pointer is used, once after the child's version is taken `[silent]`

  invariant for every hop parent -> child, the parent's version is read once and validated TWICE: after reading the child pointer,
            and again after `ReadVersion()` on the child. Only the second pair makes the hop atomic
  never treating one validation as sufficient. The first proves the child POINTER was current; it says nothing about the interval
        in which the child's own version is sampled, and that interval is where an SMO becomes undetectable
  never reusing a cached `OlcLatch` for the second validation - re-resolve it through the parent `NodeWrapper`. `GetLatch` hands
        out a reference into the chunk's page, and the child reads in between can evict that page and reuse the slot
  enforce `BTree.OptimisticDescendToLeaf` and `BTree.DescendRecordingPath` each perform
          `parent.GetLatch(ref accessor).ValidateVersion(parentVersion)` after the child's `ReadVersion()`, and restart on failure.
          `DescendRecordingPath` is the ONE descent the iterative write paths share, so this holds for insert and remove without
          either of them restating it - which is the point, and why the duplicated loops were collapsed in the same change
  never restating this per write path. The loop existed twice verbatim, and three of the six defects in PR #737 were a guard one
        copy had and its twin had lost
  scope: BTree.cs (`OptimisticDescendToLeaf`, `DescendRecordingPath`)
  note `FindLeaf`, once a fourth descent with no version validation, delegates to `OptimisticDescendToLeaf` (#887) and so
       holds this rule. What its callers then read of the leaf is theirs to validate: the range cursor's half is IXS-09
  rationale: this is the OLC paper's `readUnlockOrRestart`, and the descent shipped without it. With only the first check, a
             modification completing between it and the child's `ReadVersion()` is invisible to BOTH neighbours: the parent test
             has already passed, and the child version is sampled after the modification, so the child's own later validation
             compares against a version that never changes again. The reader then answers for a leaf the separators no longer
             route the key to. `RemoveLeaf`'s borrow-from-right is the concrete producer - it raises the right sibling's minimum
             and rewrites the separator in `RightAncestor`, moving a key LEFT, which the B-link right-walk cannot recover from
             because it can only travel right
  on_violation: a present key is reported not-found on both the read and the remove path, silently. The window is a couple of
                instructions wide, so it needs a race harness to see at all: six events in 25,701 iterations, every one with
                `key < landedLeaf.firstKey`
  verified: BTreeDescentHopAtomicityTests.ParentVersionChangedWhileTakingTheChildVersion_RestartsTheDescent

### IXS-08: A zone map bounds a cluster only after seeing every value the cluster holds `[silent]`
  invariant ∀ cluster c, ∀ indexed field f with a zone map: state(c) == Bounded → [min, max] covers every value of f in c.
    The states are Unset (nothing recorded, nothing unseen), Bounded, and Unknown (c may hold values the map never saw)
  invariant every widen form (Widen, WidenInto, WidenMasked, WidenMaskedInto) leaves Unknown unchanged; only a recompute
    (Recompute, RecomputeInto), which reads every occupied slot, takes a cluster out of Unknown. MayContain answers yes for every
    state but Bounded
  invariant zone maps are not persisted and no open path scans the data to rebuild them, so the end of every open marks each
    cluster that holds entities Unknown, in the persistent and the Transient index homes alike — after the load, the
    schema-migration rebuild (RebuildClusterFromChains) and WAL replay (RecoveryApplier), which all fill clusters without
    widening. Marking where the maps are built (InitializeIndexes) is too early: the last two run after it
  invariant State publishes a cluster's bounds to the lock-free readers: a writer stores Mins/Maxs first and Bounded last with
    release; MayContain and TryGetBounds acquire State before reading them — on arm64 a plain pair lets a reader see Bounded
    over bounds not yet written
  rationale: "no bounds" was one state meaning both "nothing here yet" and "never seen". Every widen took it for the first, so
    the first update after a reopen recorded [newValue, newValue] over a cluster holding dozens of other values, and Path B
    pruned the cluster for all of them. Without a runtime no fence recomputed it, so the rows stayed hidden for the session (#1151)
  scope: ZoneMapArray.Widen, ZoneMapArray.WidenInto, ZoneMapArray.WidenMasked, ZoneMapArray.WidenMaskedInto, ZoneMapArray.MarkUnknown,
    ZoneMapArray.MayContain, ZoneMapArray.RecomputeInto, ArchetypeClusterState.MarkActiveClustersZoneMapsUnknown,
    DatabaseEngine.InitializeArchetypes
  on_violation: rows silently missing from Path-B scans — `WhereField` equality or a range that excludes the widened value —
    while the B+Tree is intact and every structural validator passes; forcing Path A finds them. Measured: one update after a
    reopen hid 63 of 200 entities (a whole 64-slot cluster minus the updated one), and the same after a crash recovery
  verified: ZoneMapUnseenClusterTests.Widen_OnAClusterItHasNotSeen_LeavesItUnbounded (class),
            ZoneMapUnseenClusterTests.AfterAReopen_UpdatingOneIndexedField_KeepsEveryClusterMateFindable (engine, Path B forced),
            ZoneMapUnseenClusterRecoveryTests.AfterACrashRecovery_UpdatingOneIndexedField_KeepsEveryClusterMateFindable [VerifiesRule];
            mutant ZoneMapUnseenClusterTests.Widen_OnAnUnseenClusterLeftUnmarked_PrunesTheValuesItHolds [RuleMutant]

### IXS-09: A range cursor reads its position and every row it emits under one validated version of the leaf `[silent]`
  invariant the cursor's position is the key's index in the leaf it reads, taken after the leaf's version and validated against it
            (`RangeEnumerator.Position`): the version, then `Find`, then the validation, else down again. The descent's own index
            was taken under the version the descent saw; a split, a merge or a borrow landing before the read moves the key, and
            the cursor stood at a stale position
  invariant a leaf the cursor cannot read — replaced (obsolete), or write-locked past its spin limit — sends it down again from the
            key, bounded by the tree's restart budget (IXW-01's shape); it never ends the scan, which then answered empty for a key
            the tree holds
  invariant a row is emitted only after the leaf's version is validated again (`RangeEnumerator.RowStillValid`), and `Current` is
            that row, never a re-read: a row read while a writer shifts the leaf's entries is otherwise a key and a value from two
            moments; before the first row, keys outside the seek bound are skipped
  rationale: a single-key read of a multi-value index (the market's owner index, one key per player) answered empty, or with entries
             of another key, while other threads split leaves, and the same read a moment later found every entry (#1235). The
             in-storm owner auditor failed 5 of 9 market runs on it
  scope: BTree.RangeEnumerator.Position, BTree.RangeEnumerator.ParkAt, BTree.RangeEnumerator.MoveNext, BTree.RangeEnumerator.RowStillValid,
    BTree.RangeEnumerator.RestartAfterLastKey
  on_violation: an index range read — `Transaction.EnumerateIndex`, the selective query path — misses a key's entries, or returns
    another key's, with the B+Tree itself intact; the same read again finds them
  verified: BTreeChurnRaceTests.ARemovalBetweenTheSeekAndTheLeafRead_LosesNoKey (the stale position, deterministic),
            OwnerIndexChurnTests.AnOwnerIndex_ChurnedOnSeveralThreads_HoldsExactlyEachOwnersItems (the unreadable leaf, under churn);
            the row validation has no test that kills its removal — it is the OLC read discipline, not a measured defect

### IXS-10: An index reader never waits on a revision chain's lock while it holds a value buffer's `[fatal]`
  invariant a reader of a multi-value key that resolves its entities through their chains — `Transaction.EnumerateIndex`, the FK
            reverse lookup's action — copies the key's values and releases its buffer first (`RangeMultipleEnumerator.DrainCurrentKey`).
            A reader that only collects locations under the buffer (the selective query's bitmasks, the leaf page cursor) takes no
            chain lock and may hold it
  invariant a copied location is re-checked where its slot can change hands meanwhile: a SingleVersion destroy releases its slot at
            once, and a spawn may take it before the read resolves it, so a SingleVersion slot's key is compared with the copied key
            (`IndexEntityEnumerator.MoveNext`) and the FK lookup compares the slot's FK with its target and skips a freed slot
            (`FkReverseLookup.ScanClusterArchetype`). A Versioned slot is released only by the entity cleanup, and a spawn after the
            reader's snapshot fails its visibility. The buffer's lock used to hold the destroy's index removal, and so the release, off
  rationale: the buffer's shared lock, held across a chain walk, closed a cycle with a publish that holds the entity's chain
             exclusively (a conflict-handler commit always; every Versioned commit since #1263, AP-06) and moves the entity off the key —
             a move that takes the buffer exclusively, under the key's leaf latch. Each waited the other out; meanwhile every other
             writer of the key met the latched leaf on each pass and threw its retry bound (IXW-01), inside a publish: the engine
             stopped. Every concurrent index reader in the suite read keys no writer was moving entities off, so none saw it
  scope: BTree.RangeMultipleEnumerator.DrainCurrentKey, IndexEntityEnumerator.TryNextEntry, IndexEntityEnumerator.MoveNext,
    FkReverseLookup.ScanClusterArchetype
  on_violation: a read of a multi-value key times out on a chain lock, and a commit moving an entity off that key stops the engine
  verified: IndexReaderLockOrderTests.ReadersOfASharedMultiValueKey_AndMovesOffIt_NeverWaitEachOtherOut [VerifiesRule] — readers
            enumerate the key every writer moves entities on and off, both commit kinds; with the buffer held across the walk the
            handler case stopped the engine within its first moves on main, and both cases with Phase A under the chain lock;
            ASlotReusedAfterTheKeyWasCopied_IsNotYieldedUnderTheKey [VerifiesRule] — a destroy and a spawn into the freed slot staged
            between the copy and the read (`KeyCopiedProbe`): the new occupant is not yielded. Mutation killed: the key re-check removed.
            The FK lookup's re-check has no test of its own

---

## Module: IXW — Index writes under OLC

Writers participate in the same OLC protocol as readers and owe the same distinctions, or fail the same ways. IXS-01..03 were
written for the read path; these are the write-path obligations that went unwritten alongside them.

### IXW-01: A writer never waits on an obsolete node `[fatal]`
  invariant a writer whose descent finds `ReadVersion() == 0` distinguishes LOCKED (transient - wait, then restart with a fresh
            baseline) from OBSOLETE (permanent - restart immediately; the node will never become valid)
  never waiting on, or retrying against, a node whose obsolete bit is set
  never an unbounded retry loop around a step that can report "not completed" for a permanent condition
  enforce `OlcLatch.IsObsolete` is checked before the `SpinWriteLock` in the `leafVersion == 0` branch of both iterative paths;
          the pessimistic retry loops are bounded by `BTree.MaxPessimisticRestarts` and THROW on exhaustion
  scope: BTree.Insert.cs (`InsertIterative` leaf-lock branch; the `AddOrUpdateCorePessimistic` retry loop),
         BTree.Remove.cs (`RemoveIterative` leaf-lock branch; the `RemoveCorePessimistic` retry loop), OlcLatch.IsObsolete
  on_violation: livelock in the commit PUBLISH phase - after the WAL append, so the transaction is already durable and cannot be
                abandoned. Measured: four threads, 24+ minutes, CPU climbing, no progress, no exception, no timeout, and no exit
                but killing the process.
  rationale: #695. The bound is a backstop, not the fix - it sits three orders of magnitude above what real contention needs, so
             reaching it means no further retrying could have helped, and a loud diagnosable error beats a permanent silent hang
             (the same trade IX-03 makes). Measured on `ChaosStressTests.CreateDeleteRecreate_RapidLifecycle`: no result at all
             inside a 240 s cap before the fix, 147 ms after.
  requires IXS-03 (the same LOCKED-vs-OBSOLETE distinction, stated there for readers)

### IXW-02: A writer never holds the write lock on an obsolete node `[fatal]` `[silent]`
  invariant no writer performs a mutation while holding the write lock on a node whose obsolete bit is set - an obsolete node has
            been detached by a structure modification, so a write into it is unreachable from the root
  never `TryWriteLock` succeeding on an obsolete node and the caller proceeding to mutate
  enforce `OlcLatch.TryWriteLock` refuses when EITHER the locked bit or the obsolete bit is set, so the invariant is structural
          rather than something each of seventeen call sites must remember. `MarkObsolete` requires the write lock, so a node that
          is not obsolete at the instant of the CAS cannot become obsolete while that acquisition holds. `BTree.SpinWriteLock`
          reports `WriteLockOutcome.Obsolete` instead of waiting - that node never becomes lockable, which is how #695 livelocked -
          and it tests the bit INSIDE the spin, because a node that is merely locked may be locked by the very merge about to
          detach it.
  exception the four latch-coupled SMO sibling acquisitions (`InsertIterative` Phase 3 spill, `RemoveIterative` Phase 3
            borrow/merge) go through `OlcLatch.TryWriteLockOnSmoPath`, which admits an obsolete node and REPORTS it. They are
            mid-algorithm with no restart point, and skipping a sibling is worse than admitting one: `HandleChildMerge` resolves it
            again internally and its merge branch dereferences it, trading a rare lost key for a certain null dereference. Both
            phases hold the write lock on the sibling's PARENT, version-validated against the descent, so no merge can detach a
            TRUE sibling underneath them; a COUSIN is not covered by that argument and is counted in
            `BTree.ObsoleteSmoSiblingLocks`, expected 0.
  scope: OlcLatch.TryWriteLock, OlcLatch.TryWriteLockOnSmoPath, BTree.SpinWriteLock, BTree.SpinWriteLockOnSmoPath,
         BTree.ObsoleteSmoSiblingLocks
  on_violation: an insert into a node no longer reachable from the root - the key is silently lost, with no exception, and the
                tree is left inconsistent.
  rationale: measured, not inferred. Counters over a full gate suite run: 165 `MarkObsolete` calls (so the control is not
             vacuous), 737k write locks taken, and 0-2 taken on an obsolete node - every one of them through `SpinWriteLock`,
             never through a path that would then re-validate. Chasing it by re-running does not work: the stress fixture flakes
             about 1 run in 12, and 0 in 15 when run alone, which is why the enforcement is structural and the residual is a
             counter rather than a red test.
  note `MarkObsolete` now publishes with a release store. It runs under the write lock so writers are serialised, but since
       `TryWriteLock` refuses on this bit a writer's ACQUISITION decision depends on it, and on arm64 a plain store may be observed
       after stores the merge made before it.
  note this is NOT the cause of #297 or #679, and the measurement says so structurally rather than statistically: the two Add
       scenarios that produce most of their failures never call Remove, so nothing is ever marked obsolete in them. Fixing this
       left the harness rate unmoved (18/25/24 before, 26/15/30 after, 30s runs).
  verified: OlcLatchTests.TryWriteLock_Obsolete_ReturnsFalse, OlcLatchTests.TryWriteLockOnSmoPath_Obsolete_AcquiresAndReportsIt,
            OlcBTreeTests.Remove_ConcurrentMerges_NoWriterEverLocksADetachedNode
  requires IXW-01 (the same bit, read for a different purpose)

### IXW-03: A leaf's lower bound is the PREVIOUS leaf's HighKey, never its own first key `[fatal]`
  invariant any check asking "does this key belong in this leaf" compares against the previous leaf's `HighKey` - the exclusive
            bound the B-link descent itself steers by - and never against the leaf's own first key
  never treating `key < leaf.firstKey` as proof the descent is stale
  never a restart predicate strictly stronger than the invariant it protects, on a path with no other exit
  enforce `BTree.KeyBelowLeafLowerBound` short-circuits on `count == 0` and on `key >= firstKey`, then reads the previous leaf and
          returns `key < previous.HighKey`. The two extra chunk reads are paid only after the first-key comparison has failed, so
          the common in-range insert still costs one `GetCount`, one `GetFirst` and one compare.
  scope: BTree.Insert.cs (`KeyBelowLeafLowerBound` and its call sites), BTree.Remove.cs (`RemoveIterative`), BTree.Move.cs
  on_violation: every re-descent reaches the same leaf and fails the same test, so the bounded pessimistic loop of IXW-01 burns all
                10,000 restarts and throws. Measured single-threaded on the INSERT side, no contention of any kind: 2 m 34 s to the
                throw. On the REMOVE side it is concurrency-gated - `TryRemoveOlc` answers NotFound from the descent before its
                count check, so an absent key never reaches the pessimistic guard; it needs the descent to find the key, an
                underfull leaf, and a concurrent writer raising the leaf's first key in between.
  note the first version of this rule scoped itself to BTree.Insert.cs alone, while the identical defective predicate stood
       untouched in BTree.Remove.cs:610 - the rule written to stop the drift did not cover its own twin, and shipped that way for
       a day. A scope line that names one of N copies is a rule that only holds in one of N places.
  rationale: `separator == leaf.firstKey` holds only immediately AFTER a split. Removing a leaf's first key raises the leaf's
             minimum and leaves the separator where it was, so `separator <= key < firstKey` is a legitimate destination - the leaf
             IS correct, and the insert lowers its minimum back toward a separator that already routes to it. This is the same
             one-sided slack `ValidateLeafSeparators` deliberately tolerates, which is what makes the stronger form self-
             contradictory: the guard rejected states the consistency check in the same PR was written to accept. #740, shipped in
             PR #737 and caught by `BTreeMicroBenchmarks.Insert_Random` rather than by any of the 5,072 passing tests - nothing in
             the suite removed a leaf's first key and re-inserted it on a tree large enough to have interior leaves.
  verified: BtreeTests.RemoveThenReinsertLeafFirstKey_DoesNotStallInsert (2 m 34 s and failing before, 130 ms and green after)
  requires IXW-01 (its bound is what turns this into a diagnosable throw instead of a hang)

### IXW-04: A write picks its target leaf with an insert-mode descent, and proves that leaf's authority before mutating `[silent]`

  invariant a descent whose result will be WRITTEN to passes `followRightLink: false`, and the leaf it returns is checked against
            BOTH bounds - `key < leaf.HighKey` when a right sibling exists, and IXW-03's lower bound - before any mutation
  never choosing an insertion target with the reader's descent
  never asking only one of the two bounds
  enforce `BTree.KeyOutsideLeafAuthority` states the pair once; `Move`, `MoveValue` and the OLC general insert path all call it and
          restart when it holds. A bail before mutation uses `AbortWriteLock`, not `WriteUnlock` - nothing changed, so bumping the
          version would only restart other threads for free.
  enforce the PESSIMISTIC path answers the upper bound differently and must still ask it through the same predicate:
          `InsertIterative`'s B-link move-right loop is `while (KeyAboveLeafUpperBound(...))`, walking right until the leaf owns the
          key rather than restarting, because mid-SMO it has no restart point. Same question, different response.
  enforce the append and prepend fast paths answer both bounds STRUCTURALLY rather than by predicate, and that is sufficient:
          `PushLast` requires `!rl.GetNext().IsValid` (the leaf is genuinely rightmost, so no upper bound exists) plus
          `key > rl.GetLast()`; `PushFirst` requires `!ll.GetPrevious().IsValid` (genuinely leftmost, so no separator routes to it).
  scope: BTree.Insert.cs (`KeyAboveLeafUpperBound`, `KeyOutsideLeafAuthority`), BTree.Move.cs
  audit  all 14 leaf-write sites, 2026-08-10: Move.cs x4 via `KeyOutsideLeafAuthority`; Insert.cs OLC general path via both halves;
         `InsertIterative` x3 via the move-right loop and `KeyBelowLeafLowerBound`; the two `PushLast` and two `PushFirst` fast paths
         structurally; two new-root inserts have no siblings and no separator, so the question does not arise.
  rationale: the B-link right-walk answers "where does this EXISTING key live", by hopping right until the key falls inside a leaf's
             real contents. A key being inserted exists nowhere, so the walk cannot terminate on a match and instead runs one leaf
             PAST the one whose separator range owns the key. `Move` used the default `true` and inserted into that leaf, below its
             separator. Reads survived it - the same right-walk that caused it also recovers from it - which is exactly why it
             stood: the INSERT path passes `false` on purpose and cannot recover, so the damage was one stale separator away from a
             lost key.
  on_violation: separators stop bounding their leaves. Descent for every key in the resulting gap routes left and is recovered only
                by the right-walk, at one extra hop per read; `ValidateLeafSeparators` reports the leaf, and because the state is
                benign for reads the report reads as a false positive and gets suppressed. That suppression is the real cost - it is
                what made the checker unusable and kept #297/#679 undetectable for 160 days.
  measured: `Stress_MoveSameLeaf` emitted 2-3 of these on EVERY run and reported PASSED, because its `TryCheckConsistency` helper
            discarded the result. One violation was byte-identical across five consecutive runs. It reproduces with ONE thread and
            ONE key range - no concurrency of any kind - and after the fix: 0 violations in 10 consecutive runs. Fixing it also
            REMOVED work: same-leaf moves stopped being routed down the two-leaf path, taking `Stress_MoveSameLeaf` from 12 restarts
            and 109 pessimistic fallbacks to 0 and 0.
  verified: BTreeMoveLeafAuthorityTests.MoveEvenToOdd_SingleThreaded_KeepsEverySeparatorRoutingToItsLeaf
            (mutant: BTreeMoveLeafAuthorityTests.Mutant_AKeyMissingFromItsAuthoritativeLeaf_IsReported)
  requires IXW-03 (it is the lower-bound half of this pair)

### IXW-05: A writer creates a new root only while it still holds the CURRENT root `[fatal]` `[silent]`

  invariant a writer re-reads `Root` after taking its leaf's write lock and restarts unless the TOP OF ITS RECORDED PATH is still the
            root - `node` when `ctx.Depth == 0`, `ctx.PathNodes[0]` otherwise
  never building a new root over a node that is no longer the root
  never treating "the recorded node's version did not change" as "no level appeared above it" - facts about different objects
  never checking only the `Depth == 0` shape: Phase 3 rebinds `node` to `ctx.PathNodes[0]` as it unwinds, so a stale path top reaches
        Phase 4 at every depth, and the deeper case pairs a whole subtree against a promoted node one level short
  enforce `InsertIterative` computes `pathTop = ctx.Depth == 0 ? node : ctx.PathNodes[0]` and compares it to `_rootChunkId` (a single
          volatile read) immediately after `ValidateVersionLocked`, bailing with `InsertRetryExit.RootMovedUnderDescent`. Checked THERE
          and not in Phase 4: at Phase 4 the leaf split has already happened, so bailing would leave the new leaf chained and unrouted -
          the exact orphan this prevents.
  enforce the check pairs with the path-lock version validation and neither alone suffices: this one catches a path that was ALREADY
          stale when recorded (the growth happened before the descent sampled the version, so the recorded version validates cleanly),
          that one catches a path that goes stale afterwards.
  enforce the bail uses `AbortWriteLock`, not `WriteUnlock` - nothing was modified, so a version bump would only restart other
          threads for free (IXW-04 states the same discipline).
  scope: BTree.Insert.cs (`InsertIterative`), InsertRetryExit.cs (`RootMovedUnderDescent`)
  rationale: with an empty path there is no ancestor to promote into, so the insert's Phase 4 builds a new root over the leaf it
             holds - `newRoot.SetLeft(Root)` plus `Insert(0, promoted)`. Both halves assume the held leaf IS `Root`. The descent
             established that, but the descent's answer can be arbitrarily stale: the leaf's own version proves only that the leaf was
             not modified, and a tree can grow several levels above a leaf without touching it. When it has, `SetLeft` attaches an
             internal subtree while `promoted` is a leaf, and the new root's two children sit at different levels.
  on_violation: the tree is permanently unbalanced and `Height` is one too high. Leaves under the promoted side become reachable only
                through the B-link chain, never by descent, so every writer whose key routes there right-walks onto a full leaf it
                cannot split - its recorded path belongs to the leaf the descent chose - and restarts until `MaxPessimisticRestarts`
                throws. That is #738: a liveness symptom whose cause is structural, which is why five hypotheses about lock cycles
                all missed it.
  measured: 1 level-mixing root split in 7,162 on a 4-core box (`descentDepth=0 Root=#37(leaf=False) held=#4 promoted=#55(leaf=True)
            Height=3`), which reddened roughly 40% of 30-second `OlcBTreeRaceStressTests` runs and 3 of 7 nightlies. After the guard:
            0 in 70,833 root splits across 10 runs, and 0 stalls.
  verified: BTreeRetryExitInstrumentationTests.RootSplitsUnderAParkedWriter_TheParkedWriterRestartsInsteadOfBuildingASecondRoot
            DETERMINISTIC, and demonstrated to fail. It parks writer A at `OlcDescentTrace.OnDescentComplete` - descent finished, NO lock held, which is the
            defect's own window - lets writer B split that root leaf and publish a new root, then releases A. A's leaf-version read is now already
            post-split, so the validation passes on a node that is no longer the root, and only this rule's check catches it. Two ManualResetEventSlim
            handoffs, no sleeps, 6 ms. MEASURED as a mutant: commenting out the guard turns the assertion on
            `InsertRetryExitCount(RootMovedUnderDescent)` from >= 1 to 0 and reddens this test in 19 ms.
            A second test asserting only `CheckConsistency` on the same interleaving was written and DELETED: it passed with the guard disabled too, so it
            discriminated nothing. Recorded because a test that cannot fail is the exact defect this file's IXS-05 history is about.
  note: the nightly `OlcBTreeRaceStressTests` tier remains the probabilistic net for the same defect arriving by a real race - ~40% of runs red before the
        guard, 0 in 70,833 root splits after.
  requires IXS-05 (the orphaned leaf is how this violation becomes visible)

### IXW-06: A multi-value buffer is touched only under its leaf's latch, and an emptied key is dropped only if still empty there `[fatal]` `[silent]`

  invariant a buffer id read from a leaf entry is used to mutate the buffer ONLY while the write latch of that leaf is held by the same thread — it is
            never carried across a latch release
  invariant the key of a buffer one thread has emptied is removed from the tree only if, under the removing leaf's write latch, the buffer STILL holds
            no element (`RemoveArguments.OnlyIfBufferEmpty`); an append that got the latch in between keeps the key alive
  invariant a miss on a latched leaf means "absent" only if that leaf OWNS the key's range (`KeyOutsideLeafAuthority` false, both bounds); a miss on a
            leaf that does not is a descent that landed beside its target and is re-descended, never reported
  never `FindLeaf` + `GetItem` + `Append`/`RemoveFromBuffer` with no latch between them — that is what `MoveValuePessimistic` did
  never `RemoveCorePessimistic(key)` + `DeleteBuffer(bufferId)` unconditionally after an unlatched interval — that is what `RemoveValue`'s tail did
  never `FindLeaf(key)` then `Find(key) < 0` read as "not in the tree" — `FindLeaf` validates no internal-node version and right-walks on the upper bound
        alone, so a concurrent merge or borrow (keys shift LEFT) leaves it one leaf too far right; IXS-07's second parent validation closed this for the
        optimistic descent and this rule closes it for the pessimistic one
  never an unbounded retry around the authority test — it is a STATE test, and a stale separator nobody fixes would spin forever (IXW-01); the loop is
        bounded by `MaxPessimisticRestarts` and throws, as `RemoveIterative`'s `RemoveLeafNotAuthoritative` bail does
  never an optimistic `MoveValue` path that bails to the pessimistic path after its first storage write — a full leaf that must take a new key is
        decided before the element leaves its buffer. Decided after, and undone by appending the element back, it moved the element to the
        buffer's tail chunk: the pessimistic retry, holding the old element id, found nothing and dropped the move, the entity under its old key
        for good (#1232; `OwnerIndexChurnTests.MovesThatEachCreateAKey_FillingTheLeaf_LoseNoEntry` [VerifiesRule] — the 19th key lost its item)
  never an optimistic `MoveValue` path taking an entry out of a leaf that would underflow — when the move empties the old buffer and no entry comes in
        (two-leaf always; same-leaf when the new key already exists) it bails to the pessimistic path BEFORE any storage write, as `Move` always did;
        without that the two-leaf path left EMPTY leaves linked in the chain, which `CheckConsistency` reports and the census cannot see
  never a root-to-leaf descent that reads an internal node's child pointer without validating the node's version — `FindLeaf`'s own loop did, under a
        comment from the whole-tree-lock era ("internal nodes are stable"), read a slot a concurrent `PopFirstInternal` had just cleared, took chunk 0 as
        its child and cycled in the segment header's bytes forever. `FindLeaf` now delegates to `OptimisticDescendToLeaf` (IXS-07's two validations per hop,
        invalid child = restart) under `MaxPessimisticRestarts`
  enforce `BTree.RemoveElementLatched` is the only way a buffer id is obtained for a removal; `AddOrUpdateCorePessimistic` is the only way an element is
          appended on the pessimistic path (its duplicate branch appends under the leaf latch, its insert branch creates buffer, element and entry
          together); `BTree.RemoveKeyIfBufferStillEmpty` is the only way an emptied key is dropped, and it sets `OnlyIfBufferEmpty`
  enforce every removal site `RemoveCorePessimistic` can reach honours the flag: both fast paths test `BaseNodeStorage.BufferElementCount` on the item they
          are about to pop, and `NodeWrapper.RemoveLeaf` tests it before `RemoveAtInternal`
  enforce `TryLatchAuthoritativeLeaf` is the one latching descent the pessimistic writers use: it re-descends while the latched leaf is obsolete, an
          EMPTY linked leaf (the bounds cannot judge one) or `KeyOutsideLeafAuthority`, and throws past `MaxPessimisticRestarts`; `RemoveElementLatched`
          uses it, and its buffer step is under try/finally so a `LockBuffer` timeout cannot leave the leaf write-locked for the process lifetime.
          `MovePessimistic`'s unlatched pre-check re-descends on the same predicate, bounded the same way, before its `false` can be read as a unique
          collision by the fence's drain
  enforce `CanLoseAnEntryInPlace` (root, or strictly above half full) gates the entry removal on both optimistic `MoveValue` paths when
          `BufferElementCount(oldBufferId) == 1`
  scope: BTree.Move.cs (`MoveValue`, `MoveValuePessimistic`, `MovePessimistic`, `CanLoseAnEntryInPlace`), BTree.cs (`RemoveValue`,
         `RemoveElementLatched`, `TryLatchAuthoritativeLeaf`, `RemoveKeyIfBufferStillEmpty`, `RemoveArguments`), BTree.Remove.cs (`RemoveCorePessimistic`,
         `RemoveIterative`), BTree.NodeWrapper.cs (`RemoveLeaf`),
         BTree.Insert.cs (`KeyOutsideLeafAuthority`), BTree.BaseNodeStorage.cs (`BufferElementCount`),
         DatabaseEngine.ClusterMigration.cs (`DrainClusterShadowSlots`, `ProcessShadowFieldEntries`), DatabaseEngine.TickFence.cs (`RunPrepSlice`)
  on_violation: two silent shapes. (1) Element loss: a latched peer empties the key, removes the entry and frees the buffer; the allocator re-issues the
                chunk; the stale id then addresses a dead buffer or another key's, and the element vanishes from the index or lands under a key its move never
                named. (2) A spurious NOT-FOUND: `MoveValue` returns -1 for a key that is present, the tree untouched; the fence's drain then writes -1 into
                the cluster's elementId tail and leaves the index on the OLD key — the entity holds a key the index does not list it under, and the old
                leaf entry names a slot whose occupant now holds something else. Nothing throws and the structural validators pass in both — the tree is
                intact, its contents are wrong.
  measured: with `MaxOptimisticRestarts = 3`, thirty-two threads reach the pessimistic fallback for ~350 of 512 moves.
            Shape (1): `BTreeMoveValueConcurrencyTests.ConcurrentMoveValue_KeepsEveryElement_UnderItsNewKey` — 64 keys x 8 values, every pair moved by
            exactly one thread, full census — lost whole runs of a key's values in 8 runs of 12 before; 0 in 20 after, with the fallback still taken ~350
            times per run. With the fallback made unreachable (`MaxOptimisticRestarts = 1_000_000`, as an experiment) the unfixed tree lost nothing in 10
            runs, which is what located the defect in that path rather than in the latched ones.
            Shape (2): `..._UniqueKeysToFreshKeys_KeepsEveryElement` — 4 096 keys of ONE value, each moved to a key that did not exist, so every move is
            a removal plus a fresh insert with splits and merges live — returned -1 for 1 to 5 present keys per run, in 7 runs of 8, with the tree
            untouched for them; the same with every move forced pessimistic (`MaxOptimisticRestarts = 0`), 6 of 6. 0 in 10 after the authority check.
            At the fence: `PrepSliceEquivalenceTests` at W = 8, ~45 % of runs red with the drain sliced on the unfixed tree, 0 in 21 with slicing off,
            0 in 14 with only the drain serialised; 4 in 15 still red after shape (1) alone was fixed, which is how shape (2) was found.
            Shape (3), structural: `CheckConsistency` after the serial `UniqueToFresh` run reported two consecutive leaves with first key 0 — empty
            leaves the two-leaf `MoveValue` path had left linked, deterministically, single-threaded. Shape (4), a HANG: one thread alone in a quiescent
            tree, forever in `FindLeaf`, about 1 run in 10 at 32 threads; a bound on the descent turned it into "node #907 (count=25) routes to #0", a
            writer trap proved nothing STORED a zero, and the pre-fix tree at `16fa2891` hung 3 runs in 13 on the same census — pre-existing, exposed.
  rationale: #886 sliced Prep and ran the drain from W workers on the premise that the tree was multi-writer (IXW-01..05). Those rules cover the leaf and
             node protocol, and the optimistic paths honoured them; nobody had stated that the BUFFER behind a multi-value entry needs the same latch, so
             the fallback — written for a single writer — read ids the way a single writer may. The one-day guard (16fa2891) moved the drain to one thread
             and cost the fence 1.13-1.26x; this rule is what let it go back to W workers.
  verified: `BTreeMoveValueConcurrencyTests.ConcurrentMoveValue_KeepsEveryElement_UnderItsNewKey` (shape 1) and
            `..._UniqueKeysToFreshKeys_KeepsEveryElement` (shape 2), 2 x CPU threads, census — probabilistic in the way OlcBTreeStressTests is, so a single
            green run is weak and the un-quarantined nightly is the real net. Their single-threaded twins pin the oracle.
            `Mutant_AnElementRemovedBehindTheCensus_IsReported` shows the census detects the loss shape rather than passing vacuously.
  requires IXW-04 (the leaf-authority proof both paths take, and which says nothing about the buffer behind the entry)

### IXW-07: A writer never allocates storage while it holds a node or bucket latch `[fatal]`
  invariant every chunk a B+Tree split, or an entity-map append, bulk append or bucket split, writes under its latches is reserved before the first
            latch is taken (`ChunkReservation.Fill`, which grows to the exact need), its page pinned until the writer ends; under a latch
            `ChunkReservation.AllocateUnderLatch` hands out reserved chunks — anything else is counted (`UnreservedAllocations`). A bucket split's
            new bucket is its own address (EMAP-01): claimed before any latch, and an overflow chunk moved out of it lands in a chunk reserved
            before its owner is latched
  invariant a multi-value index's value-buffer chunks are reserved the same way (#1190): a new key's root chunk, and the chunk an append into a full buffer
            takes, come from the writer's reservation through `ChunkReservation.AllocateForBuffer`. The need is read under the leaf latch before the
            first write — `NewKeyBufferChunks`, `VariableSizedBufferSegmentBase.ChunksNeededToAppend`, or folded into the append itself
            (`TryAddElementReserved`, which refuses having written nothing) — and a writer short of it releases the latch, reserves holding nothing, and
            retries. A new key the optimistic descent saw is reserved for before the latch, so the common insert does not retry; an append into a buffer
            with room never opens a reservation
  invariant a writer that finds its reservation short under its latches releases every one and reserves before it latches again; a fault under an
            entity-map bucket lock releases the lock — with a version bump once anything may have been written (an upsert's in-place update, an
            append, a bulk run's entries, a split's rewrite), without one only when nothing was
  never `AllocateChunk`, a segment grow, or a page fault for a chunk being written, between a latch's acquisition and its release
  scope: ChunkReservation, BTree.AllocNode, BTree.InsertIterative, BTree.AddOrUpdateCorePessimistic, RawValuePagedHashMap.AppendUnderBucketLock,
         RawValuePagedHashMap.AppendEntry, RawValuePagedHashMap.ExecuteSplit, RawValuePagedHashMap.InsertBucketRun, PagedHashMapBase.ClaimBucketChunk,
         ChunkReservation.AllocateForBuffer, ChunkReservation.HasReserved, VariableSizedBufferSegmentBase.ChunksNeededToAppend,
         VariableSizedBufferSegment.TryAddElementReserved, BTree.MoveValue
  on_violation: an allocation can grow the segment — up to 1 024 pages — and wait seconds on page-cache back-pressure, or throw. Waiting, it holds
                every writer of the node or bucket: the B+Tree's waiters exhaust their bounded retries and report a liveness defect (IXW-01), two workers
                in one MarketHardeningTests storm. Throwing, it leaves the latch held for good: an entity-map bucket nobody would unlock froze a storm at
                175 623 operations, and the test's teardown then freed the engine under the spinning writer (AccessViolationException)
  note: not covered: the generic `PagedHashMap<TKey, TValue, TStore>`, used only by tests and benchmarks, which still allocates under its
        bucket latches
  verified: LatchFreeAllocationTests [VerifiesRule] — 20 000 inserts that split a B+Tree, and chain and split an entity map, allocate nothing under
            a latch (713 and 6 467 allocations when the reservation is ignored); after an injected allocation fault a scan and further writes
            complete. With the code before the reservation, the map's scan spins until the 15 s guard and the tree's next insert reports IXW-01
            ("made no progress in 10000 pessimistic retries") — the hardening signature, reproduced. A split of a chain longer than the initial
            reservation allocates nothing under its lock (with the reservation capped at 64 it did); a fault in a bulk insert's write phase leaves no
            bucket locked and the map's count equal to what its buckets hold (without the release, the scan spins until the 15 s guard)
  verified: ValueBufferReservationTests.AMultiValueIndex_NeverAllocatesAValueBufferChunkUnderALatch [VerifiesRule] — new keys, buffers grown to ten
            chunks, moves to new and full keys, removals and reuse: no allocation of the tree's segment while the thread holds a latch, asked at the
            segment's own allocation seam; with the reservation check bypassed, 1 943 of 9 529 were. AnAllocationFault_ForAValueBuffer_LeavesTheTreeWritable
            [VerifiesRule] — a failed buffer allocation leaves the tree unfaulted and writable; bypassed, it faults the tree (IXW-09)
  measured: about 20-35 ns per multi-value write, +3-8 % pinned to 4 CPUs (a new key pays its reserved chunk's pin and clear; an append into a full
            buffer retries once), for a leaf latch that no longer waits on the allocator (#1190)
  requires IXW-01 (the bounded retry that turned a latch held across a slow allocation into that report)

### IXW-08: A value buffer's append cursor stays in its chain `[fatal]` `[silent]`
  invariant a multi-value buffer's append cursor — `FirstStoredChunkId`, where `AddElement` appends without walking the chain — is always a chunk of
            its storage chain: a reader that unlinks the empty chunks it walks past (`VariableSizedBufferAccessor.NextChunk`) never unlinks the cursor,
            however empty it is
  scope: VariableSizedBufferAccessor.NextChunk, VariableSizedBufferSegment.AddElement
  on_violation: the cursor, unlinked and parked on the buffer's free list (or freed to the segment), keeps receiving appends: every element appended
                after the walk is in a chunk no walk reaches — an index entry that exists and is never found — and is wiped when AddElement takes the
                chunk off the free list, or the segment reissues it. Found through MarketHardeningTests' owner index (#1232): a key whose last chunk
                was emptied, then walked by a reader, lost the next append
  verified: OwnerIndexChurnTests.AKeyWhoseLastChunkWasEmptied_ThenWalked_FindsWhatIsAppendedNext [VerifiesRule] — move a key's last 1, 2, 8, 32 or
            100 items out, walk the key, move one back: the key finds it (before the fix, at 100: "finds 300, expected 301")

### IXW-09: A write that throws under its latches faults the tree; an SMO faults in its pages before its first write `[fatal]`
  invariant an exception that leaves a B+Tree write holding more latches than it started with (`OlcLatchHeld.Count`, kept by `OlcLatch`) faults
            the tree (`BTree.OnWriteException`): every later descent and latch wait throws instead of spinning on the abandoned latches or reading
            a half-modified node. A throw before any latch — a reservation that cannot grow the segment, a unique key already taken — leaves the
            tree sound and the next write proceeds
  invariant a pessimistic remove or insert faults in, before its first write, every page its SMO can write: the leaf's neighbours, a right
            merge's `rightNext`, and the siblings of each path node the SMO can reach (one removal leaves it under half full; full, for a split)
  never a page-cache miss after an SMO's first write: back-pressure throws there out of a half-done merge
  scope: OlcLatchHeld, OlcLatch.TryWriteLock, OlcLatch.WriteUnlock, OlcLatch.AbortWriteLock, BTree.OnWriteException, BTree.ThrowIfFaulted,
         BTree.PreDirtySiblings, BTree.RemoveIterative, BTree.InsertIterative
  on_violation: the latches stay held and every writer of the node spins on them for good — UltimateStress hung that way, and the teardown freed
                the engine under the spinners (AccessViolationException). A right merge that threw relinking `rightNext` left its previous pointer on
                the absorbed node; a cascade that threw admitting a level's siblings left a parent routing to a retired leaf, freed by deferred
                reclaim and reissued — a chain pointer to someone else's chunk, read as a node id (65536, 589824 in #739)
  rationale: #1321. Releasing the latches instead would hand a half-done SMO to every other writer; a B+Tree write runs in the commit's publish, past
             the append, where AP-03 stops the engine anyway — failing its index fast is what stops the halt from hanging
  verified: BTreeChurnRaceTests.AFaultUnderANodeLatch_FaultsTheTree_SoLaterWritesThrowInsteadOfSpinning [VerifiesRule] — a throw under the root's
            latch, from the collapse's probe (`OlcDescentTrace.OnRootCollapse`); the next write throws "faulted" (without the fault: it spins, and the host
            dies with an AccessViolationException at teardown). It used a new AllowMultiple key's buffer allocation under the leaf latch until #1190
            moved that allocation out of every latch. LatchFreeAllocationTests.AnAllocationFault_DuringABTreeSplit_LeavesNoNodeLatched: a fault
            before any latch leaves the tree writable. Not covered by a test: the pre-write page faults (a miss cannot be injected after the
            first write without a seam in the SMO)

### IXW-10: The root and the tail pointer change only under the latch of the node they stop naming `[fatal]` `[silent]`
  invariant a root collapse — an internal root left with no separator handing the root to its only child — runs in `RemoveIterative`'s Phase 3 with the
            root's write lock still held, re-checks there that the node is still `Root`, internal and empty, and retires the old root (`MarkObsolete`,
            `DeferredAdd`) like any merged node — stamped with a retire epoch taken AFTER the swap (`BumpEpochForRetire`), since a reader pinned later
            than Phase 2.5 can still have read the old root. `Height` is written before `Root`, whose volatile store publishes it. A split that must
            publish into that root waits on its latch, or meets it obsolete and restarts
  invariant a remove restarts when its recorded path top is no longer the root (`RemoveRootMovedUnderDescent`) — IXW-05's guard, on the remove side: a
            merge propagating to a stale top would treat a non-root node as the root, with no sibling resolved and no parent latched
  invariant `_reverseLinkList` changes only where the tail changes, under the tail leaf's latch: the split of the tail (`InsertIterative`, the right half
            becomes the tail) and a merge that absorbs it (`RemoveLeaf`, the surviving leaf becomes the tail). Every change of the tail holds the tail's
            latch, so the writes are serialised and none is stale
  invariant a split publishes its right half as the tail only once the leaf work is done — after `InsertLeaf` has placed the item — never inside
            `SplitLeafRight`: the right half is freshly allocated and unlatched, reachable until then only through the left leaf the splitter holds, and the
            tail would hand it to the append fast path while the splitter still writes into it; and it publishes after a barrier on arm64, the release half
            of the reader's TryWriteLock CAS, because the right half was written under no latch of its own
  note: two writes of `_reverseLinkList` take no latch and need none — the first leaf of an empty tree (`AddOrUpdateCorePessimistic`, before any reader
        can reach it) and the load (`BTree` constructor, single-threaded)
  never a structural pointer repaired after the write released its latches: a repair races the next change, and the stale one can win
  scope: BTree.RemoveIterative, BTree.RemoveCorePessimistic, InsertRetryExit.RemoveRootMovedUnderDescent, BTree.AddOrUpdateCorePessimistic, BTree.InsertIterative, NodeWrapper.SplitLeafRight,
         NodeWrapper.RemoveLeaf,
         OlcDescentTrace.OnRootCollapse, OlcDescentTrace.OnPessimisticWriteReturned
  on_violation: the collapse ran after the unlock: a split of the root's only child published its separator into the root between the collapse's length test
                and its swap, and the swap dropped it — a leaf on the chain under no ancestor (IXS-05), every insert routed there moving right onto it full
                until `MaxPessimisticRestarts`, every remove of its keys routed to its left neighbour until the descent gave up. The old root, never retired,
                was used as a parent with no separator by a stale descent (`LeftShift: length=-1` in `HandleChildMerge`). The tail was advanced by every
                pessimistic insert and stepped back by every pessimistic remove after the fact: the two raced, the field named a non-tail leaf, and once it
                named a leaf a merge had retired and deferred reclaim had freed, the next repair read a reused chunk — an AccessViolationException, or a
                NullReferenceException, in `RemoveCorePessimistic`
  measured: #1329, a bare `DoubleMultipleBTree` under 4 inserters and 4 removers on 4 CPUs, 500 ms runs: before, 6 of 8 loops failed within 0 to 220
            runs (lost separator, `LeftShift`, tail drift, a wedge, the AccessViolationException); after, 1 200 runs clean (three 4/4 loops and a 2/2
            loop of 300), and 30 sequential runs of the contention-split stress tests
  verified: BTreeRootAndTailTests.ASplitRacingTheRootCollapse_CannotPublishIntoTheCollapsingRoot [VerifiesRule] — DETERMINISTIC: the remover parks at
            `OnRootCollapse`, a second writer appends until the surviving leaf splits, and must not complete during the park. MEASURED as a mutant: the
            collapse moved back after the unlock reddens it (the appender splits under the collapsing root).
            BTreeRootAndTailTests.TheTailNamesTheLastLeaf_WhenEverySplitAndMergeReturns [VerifiesRule] — `ValidateLeafChain` at every
            `OnPessimisticWriteReturned`, over 3 000 ascending inserts and their removal. MEASURED as a mutant: the lazy repairs restored reddens it.
            BTreeRootAndTailTests.TheTailNamesTheLastLeaf_WhenTheLeftLeafAbsorbsIt [VerifiesRule] — the other merge branch (the leftmost leaf absorbs the
            tail). MEASURED as a mutant: that branch's publish removed reddens it.
            The publish-inside-the-split variant is caught by `OlcBTreeStressTests.Stress_ContentionSplit_TreeConsistency` (concurrent appends at the
            tail): it failed twice in one gate run, an EntryCount drift of 2 and an `ArgumentOutOfRangeException` faulting the tree
  requires IXS-05 (the orphaned leaf is how a lost separator becomes visible), IXW-05 (the root split holds the old root's latch the same way)

### IXW-11: A duplicate key is decided on the leaf, never on the last-key cache `[fatal]`
  invariant the append fast path's cached last key is a hint: a unique insert equal to it throws a duplicate only once the tail leaf's
            last key, read under the leaf's version, is the same key; otherwise the cache is cleared and the insert restarts
            (`BTree.TryInsertOlc`); the pessimistic path's append shortcut falls through to the general path, which decides it on the leaf
            (`BTree.AddOrUpdateCorePessimistic`). A move clears the cache, as a removal does (`BTree.Move`, `BTree.MoveValue`)
  rationale: a move took the tree's last key away and left it cached. The next insert of that key compared equal to the cache and
             threw a duplicate no entry held — a spawn onto the largest unique key an update had freed, past its commit's append, and
             the engine stopped (#1263's review)
  scope: BTree.TryInsertOlc, BTree.AddOrUpdateCorePessimistic, BTree.Move, BTree.MoveValue
  on_violation: an insert of a free key throws UniqueConstraintViolation; in a publish the engine stops
  verified: BTreeChurnRaceTests.AKeyMovedOffTheEndOfAUniqueTree_CanBeInsertedAgain [VerifiesRule] (red on main);
            LastWriterWinsIndexTests.ASpawnOntoTheLargestKeyAnUpdateFreed_Commits [VerifiesRule] (red on main: the engine stopped);
            AKeyMovedOffTheEndOfAMultiValueTree_CanBeAppendedAgain guards the multi-value restart, which passes on main too. Both tests
            are killed by the move's clear alone: the leaf re-check, the defence against a cache a concurrent removal re-sets stale, has
            no test that isolates it
