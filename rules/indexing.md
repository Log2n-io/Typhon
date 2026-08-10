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
            `deferred(entity) == ArchetypeMetadata.FenceMaintainedSlotsUnder(tx.Discipline)`, and the destroy removes the
            complement inline
  never deciding the deferral from a per-ENTITY signal when the thing deferred is per-SLOT. `ClusterShadowBitmap` is set by a
        write to ANY component; the shadow BUFFERS hold only the indexed non-Versioned slots that `ShadowClusterIndexedFields`
        captured, and under `CommitDiscipline.Commit` not even those - the SingleVersion members are reconciled by the commit
        publish instead
  enforce both sides read the split from ONE method (`ArchetypeMetadata.FenceMaintainedSlotsUnder`) rather than each computing it
  scope: ArchetypeMetadata.FenceMaintainedSlotsUnder (the single definition), EntityRef.ShadowClusterIndexedFields (skips its
         complement), Transaction.FlushEcsPendingOperations (the destroy hand-off; removes its complement via
         RemoveClusterIndexEntries' slot mask), Transaction.ReconcileClusterIndexAndViews (commit-scoped maintenance must not run
         for an entity the same transaction destroys)
  on_violation: the index keeps an entry for a released cluster slot. Loud on the next rebuild for a `Unique` index
                (`EntryCount` exceeds the distinct keys the data holds); SILENT for `AllowMultiple` - a leaf value naming an
                unoccupied slot, served by whichever query plan reaches it.
  rationale: #711. One write to an UNINDEXED Transient sibling was enough to reach it - no key move required, which is why the
             issue's "mutate-then-destroy" title describes a symptom rather than the trigger. The boundary is stated by a pair of
             cases: writing only the indexed component and destroying passes; writing only the unindexed sibling and destroying
             fails identically.
  requires IX-01 (one home per (archetype, field) is what makes "the set of slots" well defined)
  verified: ClusterIndexMatrixTests.MixedPublicationTimings_MutateAndDestroy_LeaveTheIndexAgreeing,
            DestroyAfterWritingOnlyAnUnindexedSibling_LeavesTheIndexAgreeing (the sharper half - no key move anywhere),
            DestroyAfterWritingOnlyTheIndexedComponent_LeavesTheIndexAgreeing (the control, green before the fix) [VerifiesRule]

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
  scope: BTree.cs (`ValidateDescentAndChainAgree`, `ValidateEntryCountMatchesChain`)
  rationale: they are separate structures maintained by separate code, and every defect in this subsystem's history has
             been one disagreeing with the other. Each individual check walked one of them.
  on_violation: a leaf on the chain but under no ancestor holds keys no descent reaches - found only by the B-link
                right-walk, and permanently lost the moment a hop budget or an empty leaf ends that walk early. A leaf
                under the root but off the chain is invisible to every range scan while lookups still answer from it.
                A drifted `EntryCount` is worse than either, because it is the number the tests assert on: it hides a
                lost key here and manufactures a phantom failure somewhere else.
  verified: BTreeConsistencyValidatorTests.Mutant_LeavesOnTheChainButUnreachableByDescent_AreReported (mutant),
            BTreeConsistencyValidatorTests.Mutant_EntryCountDisagreeingWithTheChain_IsReported (mutant)

### IXS-06: A descent answers NOT-FOUND only for a leaf that owns the key's lower bound `[UNBUILT]`

  status WITHDRAWN 2026-08-10, same day it was written. The invariant is right and the defect it names is real (#739/#297);
         the ENFORCEMENT shipped with it was not, and is reverted. Left here so the next attempt starts from the finding
         rather than rediscovering it.
  invariant before `OptimisticDescendToLeaf` reports `keyIndex < 0` as conclusive, the leaf it stopped at must not be one the key
            provably belongs to the LEFT of. When it does, the descent overshot and the caller must restart
  never treating `key <= leaf.lastKey` as sufficient evidence that the key is not in the tree
  never enforcing this by calling `KeyBelowLeafLowerBound` from the descent - that predicate reads `leaf.GetPrevious()` and then
        DEREFERENCES it for its HighKey. On the write paths the leaf is locked and that is safe; on the lock-free descent the
        neighbour id can be torn, and dereferencing it faults before any validation can signal a restart. Measured: the test host
        died with no managed stack in 4 of 5 runs of `ChaosStressTests.Light_2T_50E_NoDelete`, against 6 of 6 passing on main and
        4 of 4 passing once the call was removed. This is the hazard `BaseNodeStorage.GetChild` documents in its own comment.
  gap the descent ALREADY follows a separator to choose each child, and that separator IS the lower bound. Carrying it out of the
      descent answers the question with no neighbour read at all - cheaper than the reverted version and safe by construction.
      That is the next attempt.
  evidence 6 Remove-NotFound events in 25,701 race-harness iterations, ALL on the general-descent branch, ALL with
           `key < landedLeaf.firstKey` - key 378 on a leaf whose first key is 381, key 88 on one starting at 89 - on leaves
           holding 14 to 21 entries. With the (unsafe) guard in place: 0 in 94,854. The defect is not in doubt.
  on_violation: `Remove` returns false for a key that is present, reachable and correctly chained (#739), and the same descent
                backs `TryGet`, so the identical false not-found is reachable on the READ path (#297).
  requires IXW-03

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
