# Data engine — MVCC visibility

| Field | Value |
|-------|-------|
| Status | Living — family 1 (revisions) built, plan 06 Phase 3 |
| Last Updated | 2026-10-12 |

What a snapshot sees. A transaction's **TSN** is its identity and its snapshot; a separate **commit stamp**, drawn from the
same counter once the commit can no longer fail, says when its effects became visible; while it stamps them the committer
is **registered** on a commit card, and a reader that meets one of its still-flagged effects decides by the card's stamp.
Design: `claude/design/Revision/05-commit-stamp-and-snapshot-floor.md` (option C, amended 2026-10-09); plan:
`claude/design/Revision/06-mvcc-commit-path-implementation-plan.md` §6.

**Scope of this file: families 1–3 — revisions, spawns (VIS-08) and destroys (VIS-09), plan 06 steps 3.3, 5.2, 5.3 — and the cluster
gate's pending deaths (family 4).** The other stamp families — `BornTSN`, `DiedTSN`, the cluster
born/died watermarks, enabled-bits `ChangeTSN`, view deltas — keep today's creation-TSN behaviour until plan 06 Phase 5,
and are not covered here. Plan 06 step 3.2 built VIS-03 (the cards, the commit point, the stamp on the wire), step 3.3 the
rest: revisions are published with the commit stamp and resolved by stamp and owner. Families 2–5 (born, died, enabled
bits, cluster watermarks) are written with the stamp at publish since 3.3, without staging; their staging is Phase 5.

Formal models: [`tla/CommitStamp.tla`](tla/CommitStamp.tla) (family 1) and
[`tla/CommitStampFamilies.tla`](tla/CommitStampFamilies.tla) (a spawn and a destroy beside revisions, one cut) — every rule
below names the invariant that proves it.

## Module: VIS — commit stamps and the visibility predicate

### VIS-01: A reader decides a flagged effect by its owner's card `[fatal]` `[silent]`
  invariant visible(e, snap):
              ¬e.flagged → e.stamp ≤ snap                                     (every published effect: unchanged)
              e.flagged  → loop over the 4 cards of the owner's window [e.owner & (Size − 1) ..]:
                             o1 = Volatile.Read(card.Owner); o1 ≠ e.owner → next card
                             st = Volatile.Read(card.Stamp); o2 = Volatile.Read(card.Owner); o2 ≠ e.owner → next card
                             st ≠ 0 → visible ↔ st ≤ snap;  st = 0 → spin (the owner is inside steps 1–3 of VIS-03)
                           no card holds the owner → not committing: revalidate the effect (re-read it) — a reader that
                           holds the chain's shared lock needs none, publication of a revision takes it exclusively
  invariant the reader's snapshot is drawn by its own RMW on `_nextFreeId` (the TSN's Interlocked.Increment): with the
            register-before-draw order of VIS-03 and Volatile card loads, a reader whose snapshot is above a stamp finds
            its card — on x64 and on arm64. A constant snapshot (`WalkChain(…, long.MaxValue)`) or the public
            `IsVisibleAt(long)` with an arbitrary value is outside the argument
  invariant (Owner, Stamp, Owner): a stamp read between two equal owner reads is that owner's — TSNs are unique and a
            reservation resets Stamp before Owner can equal its new owner (VIS-03), so a reused card is never read as
            its previous owner's
  invariant the newest visible revision is the highest stamp (VIS-05), at all three walker sites — the skip-timeout
            fast path, the seqlock fast path and the full walk
  invariant a reader with no chain lock loads the element's flag word first, by acquire, then the TSN's high word; the publish
            writes the high word, then the flag word by release (#1149): a clear flag comes with the stamp it carries
  scope: RevisionChainReader.WalkChain, RevisionChainReader.TryWalkSingleEntryOptimistic, RevisionChainReader.WalkChainCore,
         TransactionChain.Register, ComponentRevisionManager.TryResolveStamp, CommitCardTable.TryGetStamp,
         CompRevStorageElement.ReadTsnAcquire
  spec: tla/CommitStamp.tla — SnapshotRead, RepeatableRead, AtomicVisibility (mutants singleownerread, nostampreset)
  on_violation: retroactive visibility (#1146): a writer created before a reader commits after its first read and its
                value appears in the reader's snapshot — a non-repeatable read, measured 13 in 500 runs of
                ChaosStressTests.RevisionChain_RapidUpdatesWithLongReaders at 4 vCPUs; with per-entity publication, half
                a commit
  requires: VIS-03, VIS-04, SNAP-01

### VIS-02: Every revision of a commit exists, flagged with its owner, before the commit point `[fatal]`
  invariant a commit's revisions are staged in PREPARE (ComponentRevisionManager.AddCompRev: TSN = owner, IsolationFlag),
            before the commit point; PUBLISH only replaces the flag with the stamp
  note: family-1 scope. For revisions this holds today; families 2–6 (spawns, destroys, cluster watermarks, enabled bits,
        view deltas) place effects at publish and are pending plan 06 Phase 5 — until then a reader can see a commit's
        revisions and not its spawn, or the reverse
  scope: ComponentRevisionManager.AddCompRev, Transaction.PublishComponent
  spec: tla/CommitStamp.tla — AtomicVisibility (the spec stages every revision before the commit point)
  on_violation: an effect created after the stamp is absent for a reader that resolved the commit's other effects as
                visible: half a commit

### VIS-03: A committer reserves before any chain lock, registers before the stamp, draws it after the last fallible step `[fatal]` `[silent]`
  invariant 0  reserve a card of the window, at the start of the commit and before PREPARE takes any chain lock:
               CAS Owner 0 → −TSN (a marker readers take for "not committing"), then Stamp = 0
               PREPARE (fallible) · APPEND (fallible: TryClaim)
               ─── the commit point: nothing below fails ───
            1  Owner = TSN (release)   2  stamp = Interlocked.Increment(_nextFreeId)   3  Stamp = stamp (release)
            4  the records written into the claimed span, the stamp on TxCommit (an 8-byte trailer after the record's body,
               outside BodyLength); the frame published
            5  publish (VIS-04)   6  Owner = 0   7  Commit() returns
  invariant every exit before step 1 releases the reservation (Owner = 0, in a finally): a conflict, a lock timeout,
            back-pressure, a unique collision, an abort at APPEND. A full window waits at step 0 alone — holding no chain
            lock and no WAL claim, bounded by the unit of work's deadline (a clean abort), its epoch refreshed
  invariant a commit with no WAL record draws its stamp at the end of PREPARE
  invariant recovery maps each committed identity to the stamp on its TxCommit (commit fate stays keyed on the identity,
            LOG-04), and the TSN counter resumes above every stamp it scanned, at or below the checkpoint too (RB-05)
  scope: Transaction.Commit, Transaction.AppendToWal, DurabilityLog.Append, CommitCardTable.TryReserve, CommitCardTable.Register,
         RecordCodec.Write, RecordCodec.TryReadRecord, RecoveryDriver.Run
  spec: tla/CommitStamp.tla — SnapshotRead (registerafterdraw), NoDirtyRead (drawbeforeappend), Causality, and no
        deadlock (reserveafterprepare: a handler-path committer waiting for a card while holding its chain locks)
  on_violation: register after the draw — a reader whose snapshot is above the stamp finds no card and misses the commit;
                the stamp before APPEND — a reader takes a registered commit that then aborts (a dirty read); the
                reservation after PREPARE — a committer waits on a card holding chain locks the card's owner needs to
                publish: a deadlock; a leaked reservation — the window is blocked for good, and a retried commit of the
                same transaction waits on itself
  requires: AP-03, AP-05, CK-13

### VIS-04: A committer leaves its card only once its last revision is stamped `[fatal]`
  invariant step 5 publishes every revision — stamp written, IsolationFlag cleared, under the chain's exclusive lock —
            before step 6 releases the card (release order); born, died, enabled-bit changes and the cluster watermarks are
            written with the same stamp in step 5
  invariant a publish that fails past the append keeps its card registered: what it left flagged belongs to a commit whose
            records are in the log, which a reader resolves by the card's stamp, and the engine commits no more (AP-03)
  scope: Transaction.PublishComponent, Transaction.Commit, CompRevStorageElement.Publish, Transaction.PublishStamp
  spec: tla/CommitStamp.tla — AtomicVisibility (releaseearly)
  on_violation: a reader finds one revision published, another still flagged with no card: it sees part of the commit
  requires: VIS-03

### VIS-05: The newest revision is the highest commit stamp, everywhere `[fatal]` `[silent]`
  invariant one version order — the walker keeps the highest visible stamp, not the last visible in chain order; the
            cluster HEAD copy at publish writes only above the chain's highest published stamp; conflict detection weighs
            the newest by stamp (a handler commit: a published or registered stamp above its snapshot), and a last-writer-wins
            commit publishes its entry where it sits, ahead of the commit it won against — no relocation; the compaction's
            sentinel is the highest-stamp skipped revision; the crash scrub (RB-03) keeps the highest stamp; the replay
            (AP-11) keeps the highest stamp per (entity, slot) for Versioned records, against the base HEAD's stamp too.
            Fence and Commit-discipline records keep CM-03's LSN order
  scope: RevisionChainReader.WalkChainCore, ComponentRevisionManager.CleanUpUnusedEntriesCore, ComponentRevisionManager.ScrubChainToHead,
         ComponentRevisionManager.FindNewestByStamp, ComponentRevisionManager.HighestPublishedStamp, RecoveryDriver.Run,
         SchemaEvolutionEngine.FindHeadElement
  spec: tla/CommitStamp.tla — SnapshotRead (chainorder, with two commits in flight: CommitStamp-twoslots.cfg)
  on_violation: chain order, highest TSN and highest LSN disagree whenever two writers of one entity commit concurrently:
                the live read, the HEAD, the scrub and the replay pick different newest values, and a snapshot holding one
                commit without another that precedes it has no serial equivalent

### VIS-06: A revision is reclaimed by stamp, never past a live snapshot `[fatal]` `[silent]`
  invariant the revision GC's cutoff is MinTSN, the oldest live snapshot; a revision superseded at stamp s is reclaimed
            only once MinTSN > s, and the one kept for the oldest snapshot is the highest-stamp revision at or below the
            cutoff — chain [A(12), B(10)] with cutoff 13 keeps A, and A alone: a committed revision below the cutoff and older
            than the kept one is reclaimed wherever it sits, the chain's last entry included
  scope: ComponentRevisionManager.CleanUpUnusedEntriesCore, DeferredCleanupManager.CleanupEntityRevisionsBatched
  spec: tla/CommitStamp.tla — GCSafety (gcbytsn)
  on_violation: the GC keyed by identity frees the highest-stamp revision a snapshot needs: the reader reads an older value
  requires: SNAP-01, VIS-05

### VIS-07: No staged revision survives an open `[fatal]`
  invariant a crash open discards every revision still flagged with an owner (the recovery scrub drops isolated
            elements, RB-03): a resumed TSN equal to a dead owner must not adopt its staged revisions
  note: the `Pending(owner)` marks of families 2–3 on EntityMap records do not survive either: the crash open re-derives the
        EntityMap from occupancy (DatabaseEngine.RebuildEntityMapOnCrash), and no pending slot is occupied on a page
        (VIS-08). Family 5's pending changes and the claim word are memory only
  scope: ComponentRevisionManager.ScrubChainToHead
  on_violation: a transaction of the reopened engine whose TSN equals a crashed owner's finds that owner's flagged
                revisions as its own, or a reader resolves them through its card

### VIS-08: A pending spawn is never absent from a snapshot, and never persisted as occupied `[fatal]` `[silent]`
  invariant a slot is claimed in the cluster's in-memory claim word, by CAS, from its claim to its final release; the page
            occupancy word is the published set, written only by the slot's owner. A spawn is placed in PREPARE
            (Transaction.PlaceSpawns): its slot claimed, its occupancy bit cleared again at once (ReserveClaimedSlot), its SoA
            bytes copied, its EntityMap record inserted with `BornTSN = Pending(owner)` (bit 47), and the bounds widened —
            zone maps, cluster AABB, per-cell index: conservative upward, and placement reads them. PUBLISH
            (Transaction.PublishSpawns) writes the record's stamp in place, then folds the born watermark and sets the
            occupancy bit, the claim kept (PublishReservedSlot), then adds the B+Tree entries
  invariant a reader resolves a pending born through the owner's card (EntityVisibility, EntityRecordHeader.IsVisibleAt
            with the cards), re-reading the record when no card holds the owner. A snapshot scan of the cluster SoA reads the
            occupancy word, then the claim word, visits both, and decides a slot claimed and not occupied
            (ArchetypeClusterState.PendingBits) through its record, which must exist — the spawn's claim outlives its
            publish, so a scan whose occupancy read precedes the publish still finds the slot claimed. The occupancy count
            bails to the map probe while a cluster holds such a slot. A HEAD reader takes a pending born for not alive
            (EntityRecordHeader.IsAlive)
  invariant a slot is free when clear in both words (TakenBits): occupancy covers the slots a load or a recovery set
            without a claim. A claim only sets claim bits and only an owner clears them, so a stale claimer's CAS takes only
            a slot free again — no ABA (ArchetypeClusterState.ClaimFirstFree, ClaimExact); a cluster with a claimed slot is
            never drained
  invariant the claim word is never written to a page: a fuzzy checkpoint can persist a pending slot's bytes and its
            record, never its occupancy bit — the page the claim set it on has an active writer until the placement clears
            it — so the crash open's EntityMap rebuild from occupancy resurrects no uncommitted spawn (CM-01)
  invariant an abort before the commit point un-places the spawn — record removed, claim released, a cluster left empty
            freed (Transaction.UnplaceSpawns, ArchetypeClusterState.UnreserveSlot; AP-07). The widened bounds stay wider,
            which is safe
  note the B+Tree and the spatial enumerators (AabbClusterEnumerator, ClusterRadiusBatch) read HEAD (design 05's non-goal,
       #674): an index-path or spatial query between a commit's append and its publish does not find the spawn its
       revisions announce, and an older snapshot's spatial query does not find a hidden destroy. The cluster-free race of
       a drain outside the fence is #1315
  scope: Transaction.PlaceSpawns, Transaction.PublishSpawns, Transaction.UnplaceSpawns, ArchetypeClusterState.ReserveClaimedSlot,
         ArchetypeClusterState.PublishReservedSlot, ArchetypeClusterState.UnreserveSlot, ArchetypeClusterState.ClaimFirstFree,
         ArchetypeClusterState.ClaimExact, ArchetypeClusterState.TakenBits, ArchetypeClusterState.PendingBits, EntityVisibility.IsVisible,
         EntityVisibility.IsVisibleInScan, EntityRecordHeader.IsAlive
  spec: tla/CommitStampFamilies.tla — SpawnRead (spawnatpublish, scanoccupancy, publishclearsclaim: the publish clearing the claim
        lets a scan that read occupancy before it and the claim after it miss the spawn)
  on_violation: a reader holding the commit's revision of one entity misses the entity it spawned, by lookup or by scan
  requires: VIS-01, VIS-02, CM-01

### VIS-09: A destroy is pending before the stamp, and its slot outlives every snapshot that sees the entity `[fatal]` `[silent]`
  invariant a destroy writes `DiedTSN = Pending(owner)` in PREPARE (Transaction.MarkDestroysPending, under the record's bucket
            latch; a died already set is left), undone on an abort (UnmarkDestroysPending), and the stamp at PUBLISH
            (FlushPendingDestroys): over its own pending value or none, and of two destroys the lowest stamp — another
            owner's pending died weighed by that owner's card. A reader resolves a pending died through the card; a HEAD
            reader takes it for alive
  invariant while a destroy is pending, its cluster's gate denies (ArchetypeClusterState.PendingDeaths, read by
            IsClusterFullyVisibleAt, and by the scans BEFORE the occupancy word): its slot is still occupied and its died
            watermark not folded. The publish folds the watermark, then hides the slot, then lifts the count — counted and
            lifted on the cluster the PREPARE found, whatever the record says by the publish
  invariant in an archetype with a Versioned component — the one that promises snapshot isolation — the publish hides the slot
            rather than release it (ArchetypeClusterState.HideDestroyedSlot): out of HEAD (change signals, cell count,
            shrink flag, occupancy bit) and still claimed, its bytes and entity id kept, so an older snapshot still finds the
            entity by scan. The cleanup, keyed by the died STAMP, releases it once MinTSN is past that stamp
            (DatabaseEngine.ReleaseHiddenDestroySlot, ArchetypeClusterState.ReleaseHiddenSlot), then removes the record.
            SingleVersion and Transient archetypes release at publish, as before: they promise no isolation
  scope: Transaction.MarkDestroysPending, Transaction.UnmarkDestroysPending, Transaction.FlushPendingDestroys,
         ArchetypeClusterState.HideDestroyedSlot, ArchetypeClusterState.ReleaseHiddenSlot, ArchetypeClusterState.PendingDeaths,
         ArchetypeClusterState.IsClusterFullyVisibleAt, DatabaseEngine.ReleaseHiddenDestroySlot, DatabaseEngine.ProcessEcsCleanups
  note the lowest-stamp rule for two concurrent destroys of one entity has no test of its own
  spec: tla/CommitStampFamilies.tla — DestroyRead (destroyatpublish, releaseatpublish)
  on_violation: a reader begun after the destroy's stamp still sees the entity alive while it holds the commit's other
                effects; an older reader's scan loses an entity its snapshot holds
  requires: VIS-01, VIS-06

### VIS-10: An enable/disable change exists before the stamp, and a snapshot reads it by stamp order `[fatal]` `[silent]`
  invariant a commit's enable/disable change of an existing entity is recorded in PREPARE as a pending history entry — its
            owner's TSN with the pending bit, the bits it changes from and to (Transaction.MarkEnableDisablePending) —
            dropped on an abort (UnmarkEnableDisablePending), and stamped at publish after the record's and the cluster's
            inline bits, before the card goes (FlushPendingEnableDisableEntry, EnabledBitsOverrides.Publish)
  invariant the bits at a snapshot are the new bits of the highest-stamp change at or below it; with none, the old bits of the
            lowest-stamp change above it; with neither, the inline bits (EnabledBitsHistory.ResolveAt). A pending change
            counts by its owner's card; with no card holding the owner the history is read again. Ordered by stamp: with
            two changes after a snapshot, the newest one's old bits are the wrong answer (#1248)
  invariant the history is an immutable array a writer replaces under a lock; readers take no lock (#1248)
  note two concurrent changes of one entity's bits keep last-publisher-wins on the inline copy, and a change recorded against
       bits another commit changed after this one's PREPARE carries stale old bits
  scope: Transaction.MarkEnableDisablePending, Transaction.UnmarkEnableDisablePending, EnabledBitsHistory.ResolveAt,
         EnabledBitsHistory.AddPending, EnabledBitsHistory.Publish, EnabledBitsOverrides.ResolveEnabledBits
  on_violation: a reader between a commit's append and its publish sees its revisions and the entity's old enabled bits; an
                older reader with two later changes sees bits the entity had only between them
  requires: VIS-01, VIS-04

### VIS-11: A view delta carries its commit's stamp `[fatal]` `[silent]`
  invariant every view delta a commit appends — index moves, spawns, destroys, membership, enable/disable — is appended at
            publish and tagged with the commit stamp (Transaction.ViewDeltaTag, PublishStamp); a view refreshed at
            snapshot S folds the entries tagged at or below S
  note the ring is FIFO in publish order, not stamp order: a commit with a stamp at or below S that publishes after a
       higher-stamp one, or after the refresh, is folded by a later refresh — a view lags, it never holds a change its
       snapshot does not see. Reserving deltas in PREPARE and draining by stamp (plan 06 §8 step 5.6 as sketched) is
       not built: since step 1.4 PREPARE appends no delta, which is what keeps an abort free of view work (AP-07)
  scope: Transaction.ViewDeltaTag, Transaction.ReconcileClusterIndexAndViews, Transaction.PublishMembershipDeltas,
         ViewDeltaRingBuffer.TryPeek
  on_violation: tagged with the writer's TSN, a view refreshed at a snapshot between that TSN and the stamp applies a change
                committed after the snapshot
  requires: VIS-01
