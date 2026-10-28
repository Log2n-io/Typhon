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
            4  the records written into the claimed span, the stamp on TxCommit (a trailer after the record's body, outside
               BodyLength, followed by the append's world tick, TICK-01); the frame published
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
  never the replay refuses a record's HEAD copy for the record's own stamp in the chain: a torn checkpoint can carry the
        commit's chain element without its content, the open's HEAD rebuild copies that stale content into the HEAD, and
        the cluster indexes are rebuilt from the HEAD — the entity stays under its old key. Only a later stamp refuses
        the copy (RecoveryApplier; TornCaptureRecoveryTests.AnUpdateWhosePagesReachedTheDiskInPart_Recovers)
  scope: RevisionChainReader.WalkChainCore, ComponentRevisionManager.CleanUpUnusedEntriesCore, ComponentRevisionManager.ScrubChainToHead,
         ComponentRevisionManager.FindNewestByStamp, ComponentRevisionManager.HighestPublishedStamp, RecoveryDriver.Run,
         SchemaEvolutionEngine.FindHeadElement, RecoveryApplier
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
            occupancy word is the published set, written only by the slot's owner. A claim sets no occupancy bit
            (ArchetypeClusterState.ClaimFirstFree): its owner publishes it once the slot holds its data — recovery, the open's
            rebuild and a migration at once (ArchetypeClusterState.PublishClaimedSlot), a spawn at its PUBLISH. A spawn is
            placed in PREPARE (Transaction.PlaceSpawns): its slot claimed and left unoccupied, its SoA bytes copied, its EntityMap record inserted with `BornTSN = Pending(owner)` (bit 47), and the bounds widened —
            zone maps, cluster AABB, per-cell index: conservative upward, and placement reads them. PUBLISH
            (Transaction.PublishSpawns) writes the record's stamp in place, then folds the born watermark and sets the
            occupancy bit, the claim kept (PublishReservedSlot), then adds the B+Tree entries and their element-id tails, then releases
            the slot's index lock
  invariant a reader resolves a pending born through the owner's card (EntityVisibility, EntityRecordHeader.IsVisibleAt
            with the cards), re-reading the record when no card holds the owner. A snapshot scan of the cluster SoA reads the
            occupancy word, then the claim word, visits both, and decides a slot claimed and not occupied
            (ArchetypeClusterState.PendingBits) through its record, which must exist — the spawn's claim outlives its
            publish, so a scan whose occupancy read precedes the publish still finds the slot claimed. The occupancy count
            bails to the map probe while a cluster holds such a slot. A HEAD reader takes a pending born for not alive
            (EntityRecordHeader.IsAlive)
  invariant a spawn is visible from its commit's card, before its PUBLISH writes its index entries: every commit that maintains an
            existing entity's entries is serialized with the spawn, and with a destroy of the entity, and decides under that serialization
            whether the slot still holds the entity alive (ArchetypeClusterState.HoldsLive: occupied, its entity id the entity's) — a slot
            that does not moves, copies and removes nothing: its destroy removed the entries, and a released slot may hold another entity
            since. The slot's index lock (ArchetypeClusterState.LockSlotIndexes), which the spawn takes when it is placed and releases at
            the end of its PUBLISH, serializes a first supply's move and slot copy (Transaction.LockLiveSlotIndexes, under the chain lock),
            a Commit-discipline staged write's (Transaction.PublishStagedEntry) and a destroy's removal and hide or release (VIS-09). An
            update of an existing chain is serialized with a destroy by its chain lock alone (Transaction.SlotHoldsEntityAlive): the
            destroy's tombstone publish takes it, and for a chain the destroy did not tombstone — a supply's, after its snapshot — its index
            removal does (Transaction.LockUntombstonedChains). The slot lock on every move cost a key-moving update 3 %: a lock-prefixed CAS
            waits for the move's stores. An in-place write whose keys the fence moves waits for
            the lock to be free before it records them (WaitSlotIndexesUnlocked); a fence migration skips a locked slot. Order: chain lock,
            slot lock, the trees' latches. A spawner's component publish runs before its spawns' and takes chain locks while it holds its
            spawns' slot locks, so a commit about to take an entity's chain lock first waits, holding none, for the slot to be free
            (Transaction.PublishComponent) — on the handler path, which takes it in PREPARE and keeps it, there, bounded by the chain
            lock's own wait (Transaction.AwaitSpawnPublished). Once a spawn is published nothing holds its slot lock while waiting for a
            chain lock
  invariant a slot is free when clear in both words (TakenBits): occupancy covers the slots a load or a recovery set
            without a claim. A claim only sets claim bits and only an owner clears them, so a stale claimer's CAS takes only
            a slot free again — no ABA (ArchetypeClusterState.ClaimFirstFree, ClaimExact); a cluster with a claimed slot is
            never drained
  invariant the claim word is never written to a page: a fuzzy checkpoint can persist a pending slot's bytes and its
            record, never its occupancy bit, which no page holds before the PUBLISH — so the crash open's EntityMap rebuild from
            occupancy resurrects no uncommitted spawn (CM-01)
  invariant an abort before the commit point un-places the spawn — record removed, claim released, a cluster left empty
            freed (Transaction.UnplaceSpawns, ArchetypeClusterState.UnreserveSlot; AP-07). The widened bounds stay wider,
            which is safe
  note the B+Tree and the spatial enumerators (AabbClusterEnumerator, ClusterRadiusBatch) read HEAD (design 05's non-goal,
       #674): an index-path or spatial query between a commit's append and its publish does not find the spawn its
       revisions announce. Every index reader and the spatial queries find a hidden destroy (VIS-09, #1347, VIS-13, #1247). The cluster-free race of
       a drain outside the fence is #1315
  never a claim sets the occupancy bit of the slot it takes: the claim folds the claimer's TSN into the born watermark first,
        so a reader whose snapshot is above that TSN passes the cluster gate and counts, or enumerates, a slot with no data and
        no record (CommitStampVisibilityTests.AReaderDuringASpawnsClaim_NeitherCountsNorFindsTheUnplacedSlot; the claim
        published the bit and PREPARE cleared it after, until 2026-10-12)
  scope: Transaction.PlaceSpawns, Transaction.PublishSpawns, Transaction.UnplaceSpawns, ArchetypeClusterState.PublishClaimedSlot,
         Transaction.SlotHoldsEntityAlive, Transaction.LockUntombstonedChains,
         ArchetypeClusterState.LockSlotIndexes, ArchetypeClusterState.UnlockSlotIndexes, ArchetypeClusterState.IsSlotIndexLocked,
         ArchetypeClusterState.WaitSlotIndexesUnlocked, ArchetypeClusterState.HoldsLive, Transaction.LockLiveSlotIndexes,
         Transaction.AwaitSpawnPublished, Transaction.PublishComponent, Transaction.PublishStagedEntry,
         ArchetypeClusterState.PublishReservedSlot, ArchetypeClusterState.UnreserveSlot, ArchetypeClusterState.ClaimFirstFree,
         ArchetypeClusterState.ClaimExact, ArchetypeClusterState.TakenBits, ArchetypeClusterState.PendingBits, EntityVisibility.IsVisible,
         EntityVisibility.IsVisibleInScan, EntityRecordHeader.IsAlive
  spec: tla/CommitStampFamilies.tla — SpawnRead (spawnatpublish, scanoccupancy, publishclearsclaim: the publish clearing the claim
        lets a scan that read occupancy before it and the claim after it miss the spawn)
  on_violation: a reader holding the commit's revision of one entity misses the entity it spawned, by lookup or by scan; an entity
                indexed twice, or a dead one indexed — a stale entry the slot's next occupant inherits (#1370) — and a removal from
                another key's value buffer, its count off by one (#1369)
  verified: LastWriterWinsIndexTests [VerifiesRule] — an update, a destroy (Versioned and SingleVersion) staged inside a spawn's PUBLISH;
            an update, with and without a conflict handler, from inside the spawner's revision publish, on its own thread (no deadlock); a
            first supply published after a destroy of its entity; a destroy between a first supply's move and its slot copy; a second
            destroy, and a Commit-discipline staged write, published after the slot was reused. Mutants: no spawn lock, no update lock, no
            liveness check on update, on a supply, on destroy or on a staged write, no destroy lock, no chain lock on a chain the destroy
            did not tombstone (a destroy between an update's move and its copy; an update after the destroy), no wait before the chain
            lock at publish or in a
            handler's PREPARE (deadlock) — each red. The staged write's lock alone has no staged race. Under load: QueryStressOracleTests
            ...UnderConcurrentChurnAndTierMoves
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
  invariant the publish removes the entity's B+Tree entries, by the keys and tails its slot holds, and hides or releases the slot, after
            a spawn still publishing finishes, and under the chain locks of the entity's indexed Versioned components it did not tombstone
            (taken first, in component order) and the slot's index lock (VIS-08): a supply or an update moving the entity's entries and
            copying its slot finishes first. A slot that no longer holds the entity alive (ArchetypeClusterState.HoldsLive) is left
            alone — a second destroy of one entity found it hidden, or released and holding another entity, whose entries it removed and
            whose slot it released (#1370)
  invariant in an archetype with a Versioned component — the one that promises snapshot isolation — the publish hides the slot
            rather than release it (ArchetypeClusterState.HideDestroyedSlot): out of HEAD (change signals, cell count,
            shrink flag, occupancy bit) and still claimed, its bytes and entity id kept, so an older snapshot still finds the
            entity by scan. The cleanup, keyed by the died STAMP, releases it once MinTSN is past that stamp
            (DatabaseEngine.ReleaseHiddenDestroySlot, ArchetypeClusterState.ReleaseHiddenSlot), then removes the record.
            SingleVersion and Transient archetypes release at publish, as before: they promise no isolation. The cleanup releases
            only in an archetype whose destroys hide, and only a slot still claimed, unoccupied and holding the destroyed id: a
            SingleVersion slot freed at publish may be claimed by a spawn since, claimed and unoccupied until its own publish (#1247)
  invariant the index paths find a hidden destroy too (#1347): the publish folds its stamp — the archetype's
            (ArchetypeClusterState.NoteDestroyPublished, MaxDestroyStamp) and the cluster's died watermark — BEFORE it removes the
            entity's B+Tree entries, and the slot's change stamp before both (VIS-12). The selective scan reads the archetype stamp after its tree
            read; above its snapshot, in each cluster whose died watermark is above it, the slots whose KEY stamp is above it are read as the
            SoA scan reads them — every slot of a cluster with a destroy pending — and a tree candidate whose slot is hidden by the cluster read
            is decided by its record. The ordered merge reads the stamp before its streams
            open — its tree reads are lazy — reads the changed entities of such clusters (VIS-12's stamps) into a sorted side stream whose entities
            the tree stream drops,
            tests every row's born and died at the snapshot (ArchetypeSortedStream.TryAdmitCurrent, as the merge consumes it), and answers again when the stamp moved
            during the merge; the sort fallback keys a row absent from the tree by its slot's bytes
  scope: Transaction.MarkDestroysPending, Transaction.UnmarkDestroysPending, Transaction.FlushPendingDestroys,
         ArchetypeClusterState.HideDestroyedSlot, ArchetypeClusterState.ReleaseHiddenSlot, ArchetypeClusterState.PendingDeaths,
         ArchetypeClusterState.IsClusterFullyVisibleAt, DatabaseEngine.ReleaseHiddenDestroySlot, DatabaseEngine.ProcessEcsCleanups,
         ArchetypeClusterState.NoteDestroyPublished, EcsQuery.ScanPerArchetypeBTreeSelective, EcsQuery.MergeOrderedClustered,
         EcsQuery.ReadClustersWhole, ArchetypeSortedStream.TryAdmitCurrent, EcsQuery.TryReadHiddenOrderKey
  note the lowest-stamp rule for two concurrent destroys of one entity has no test of its own. Of the index-path invariant, the fold
       before the tree removal has no staged test — no hook sits inside one commit's publish. A row the merge's tree read found
       and a destroy hid before the merge consumed it is decided by its record, which only spares the merge its second answer: the
       destroy moved the stamp, and the revalidation answers right without it (mutant dropping such rows: survives, by design)
  note the occupancy bit is cleared at PUBLISH; until then the destroying transaction's own cluster walks subtract it (CLUSTERWALK-04) — no other
       reader's
  spec: tla/CommitStampFamilies.tla — DestroyRead (destroyatpublish, releaseatpublish)
  on_violation: a reader begun after the destroy's stamp still sees the entity alive while it holds the commit's other
                effects; an older reader's scan loses an entity its snapshot holds
  verified: SnapshotIndexVisibilityTests [VerifiesRule] — a cleanup removing the record between a scan's occupancy read and its probe; the slot gate
            on a missing record; the slot gate on a zero id, gated and not (a SingleVersion scan reading a slot's id after a destroy's release); a
            SingleVersion destroy's cleanup
            run inside a later spawn's PREPARE → PUBLISH window on the slot it freed. Mutants: the gate failing open on a missing record (the gate and
            the scan cases), without the zero-id test (the zero-id case), the old gate (both, the scan case), the cleanup without its archetype and id
            guards (the spawn case) — each red. Each cleanup guard alone survives by design: either is enough
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

### VIS-12: A field query evaluates every entity at its snapshot's value, on every path `[fatal]` `[silent]`
  invariant ∀ field query Q at snapshot S on a Versioned component T, ∀ entity e: Q evaluates its predicates on the value of e's T committed at
            or below S, never on one committed after S — not the cluster HEAD's bytes, not the index tree's keys, which both hold the newest
            committed value — and never on Q's transaction's own unpublished write (QENABLE-01)
  invariant a destroy's tombstone is not an update: it moves no HEAD bytes (its slot is hidden whole, VIS-09) and its entries' removal is
            announced by the destroy stamp, so it folds no update watermark — folding it sent every older reader through a snapshot fill of each
            destroyed cluster, ~8.5 µs a cluster (#1247)
  invariant an update publishing a new newest value (one that moves the entity's index entries or copies its HEAD) says so first, under the
            entity's chain lock and before either move (ArchetypeClusterState.NoteUpdatePublished, from Transaction.NoteVersionedUpdate): its
            cluster's update watermark (a CAS max of the commit stamp), the archetype's MaxUpdateStamp (a CAS max), then its UpdateCount
            (an increment) — once per update, not per commit: a merge whose first read falls between two updates of one commit must see the second
  invariant every change an older snapshot must not read from the HEAD or the trees records its commit stamp in its SLOT's change stamps
            (ArchetypeClusterState.NoteSlotChanged, CAS maxes) BEFORE it folds its cluster's died or update watermark: the VALUE stamp for a change of
            the HEAD bytes — a destroy (VIS-09), an update that copies its HEAD — and the KEY stamp for a change of the trees — a destroy, which takes
            the entity's entries out, an update that moves one of its index keys (as Phase A decides one, conservatively: a commit landed since the
            read counts); a migration carries both. In an archetype with a Versioned component, a key-moving update raises the archetype's
            MaxKeyMoveStamp, and it and every destroy then increment its KeyChangeCount — a line of their own — before either touches a tree: a
            count, since a destroy with a lower stamp can publish after one with a higher stamp and move no maximum. A reader that read the watermark by acquire reads the stamps after it (ArchetypeClusterState.ChangedSlotsAfter,
            KeyChangedSlotsAfter). A stamp only rises and is never cleared — a stale one, from a slot released and reused since, reads as a change and
            costs that slot a read at S, never a wrong answer — so no clearer races a writer. In memory only (#1247)
  invariant a cluster whose update watermark is above S is evaluated on a snapshot column: the slots whose change stamp is above S read at S
            through their record and chain (EcsQuery.FillSnapshotColumn, EntityAccessor.TryCopyCommittedAtSnapshot), a slot with no value at S
            dropped; every other slot from the HEAD, its last change at or below S. The HEAD copy is validated after it: the stamps re-read behind a
            load barrier, and a slot whose stamp rose meanwhile read at S — an update stamps its slot before it copies its HEAD (#1247). The column
            is the transaction's (Transaction.SnapshotColumn): taken from the engine's allocator at the first cluster that needs one, kept across
            pooled uses, never cleared — every slot read is written first. The SoA scan and the selective scan validate their HEAD read AFTER it,
            seqlock-style — the watermark re-read (behind a full barrier on arm64) and the cluster evaluated again on the snapshot column when it
            moved above S — because an update can fold its watermark after the first read and copy its HEAD before the evaluation
  invariant the index paths find the clusters from the archetype (EcsQuery.ClustersUpdatedAfterSnapshot): its UpdateCount first (the mark), then
            its MaxUpdateStamp; at or below S, no cluster — one load for every reader newer than the archetype's last update; above it, a walk
            of every active cluster's watermark (EcsQuery.WatermarksAbove). The selective scan reads them after its tree read and, in each listed
            cluster, re-tests at S every predicate of the entities whose slot's KEY stamp is above S, beside the tree's candidates — an entity whose
            update moved no key is a candidate at its snapshot key, verified on the snapshot column. The ordered
            merge reads them before its lazy streams open, reads those entities into the side stream whose entities the tree stream drops — every
            changed entity, matching or not — and after the merge compares the count with its mark (EcsQuery.UpdatedSince): changed, it walks the
            watermarks again and answers again when any slot above S is one its side stream did not read, in a cluster it read or not. The count,
            not the stamp: an older commit can publish after a newer one and leave the stamp where it was. A cluster whose died watermark is
            unknown (a recovery or migration recorded a death without a stamp), or with a destroy pending, is read in every slot, as before
  invariant every path that moves an entity's HEAD to another cluster carries the update watermark with it: a fence migration folds the source
            slot's change stamp into the destination slot's, then the source cluster's watermark into the destination's, and the archetype's stamp
            and count, before it copies the entity's bytes
            (DatabaseEngine.ExecuteMigrations)
  invariant a cluster above S is never pruned by its zone maps: the fence re-tightens them from the HEAD, so they may exclude S's value. The SoA
            scan re-reads the watermark after a prune verdict (behind a full barrier on arm64) and keeps the cluster when it is above S
  scope: Transaction.NoteVersionedUpdate, Transaction.SnapshotColumn, ArchetypeClusterState.NoteUpdatePublished, ArchetypeClusterState.NoteClusterUpdated,
         ArchetypeClusterState.NoteSlotChanged, ArchetypeClusterState.ChangedSlotsAfter, ArchetypeClusterState.KeyChangedSlotsAfter,
         ArchetypeClusterState.SlotChangeStamp, ArchetypeClusterState.SlotKeyStamp, ArchetypeClusterState.MaxKeyMoveStamp,
         ArchetypeClusterState.KeyChangeCount, EcsQuery.SnapshotChangedSlots,
         ArchetypeClusterState.MaxUpdatedOf, ArchetypeClusterState.MaxUpdateStamp, ArchetypeClusterState.UpdateCount,
         EcsQuery.ClustersUpdatedAfterSnapshot, EcsQuery.WatermarksAbove, EcsQuery.UpdatedSince,
         EcsQuery.FillSnapshotColumn, EcsQuery.MatchingSlots, EcsQuery.ScanClusterSoa, EcsQuery.ScanPerArchetypeBTreeSelective,
         EcsQuery.MergeOrderedClustered, EcsQuery.ReadClustersWhole, EntityAccessor.TryCopyCommittedAtSnapshot, DatabaseEngine.ExecuteMigrations
  note an update stamped at or below S but not yet published when the query runs (between VIS-03's stamp and its publish) is not covered: the
       query reads the HEAD and tree as they were before it, while a point read finds the new value through the commit card — the same window
       VIS-08's note leaves open for spawns. Closing it needs a per-cluster pending-update count read as PendingDeaths is
  note point reads (EntityRef) always walked the chain at S; only the bulk paths read HEAD or tree keys. The SoA scan's watermark check before
       the evaluation is an economy, not a guard: the validation after it answers alone (mutant: survives, by design). The walk is O(active
       clusters) per index-path query of a reader older than an update: a selective point lookup over a very large archetype under constant
       updates pays it on every query. An engine-wide update log once listed the clusters instead; dropped as unmeasured weight (352 KB per
       engine, a global interlocked head per update) until a measurement says the walk costs more
  on_violation: a reader older than an update matched the entity by its newer value — counted, listed and ordered by a value committed after
                its snapshot, and its predicate on another field verified on the newer bytes (#674); field queries disagreed with point reads
  requires: VIS-05, VIS-06, QENABLE-01
  verified: QueryUpdateSnapshotTests [VerifiesRule] — an older reader on every path and terminal; a predicate on a second field; the own write in a
            cluster read at the snapshot; updated then destroyed; an update after the merge's first read; an update between the watermark read and
            the HEAD read; the watermark walk only for a reader older than an update; an entity updated then migrated at the fence; a cluster the
            fence re-tightened after the update. PipelineExecutorCombinedPathTests.Versioned_SnapshotHidesLaterUpdate; OrderedQueryByAnotherFieldTests
            ...OrdersByTheValueItSees_OnTheMerge. Mutants: no watermark fold (nine red), no stamp fold, the walk skipped (eight each), no count, the
            merge's second read removed (the merge case), either validation removed (the between-reads case), the merge dropping only matching rows,
            a migration that does not carry the watermark, a prune that ignores it — each red. A destroy leaving the watermark alone: SnapshotIndexVisibilityTests.ADestroy_LeavesTheUpdateWatermarkAlone
            (mutant: the tombstone folding it — red). Per slot (#1247): QueryUpdateSnapshotTests — one changed slot read at S on every path
            (counted: one point read a cluster), an update between the fill's stamp read and its HEAD copy (scan and selective), an update of another
            slot of a cluster the ordered side stream read. Mutants: the fill reading every slot, no stamp re-read after the HEAD copy, the merge's
            check by cluster, a migration not carrying the slot stamp, the selective scan not re-testing changed entities, an update or a destroy not
            stamping its slot (QueryIndexPathSnapshotTests), the side stream dropping only its matching rows — each red. Under load: QueryStressOracleTests
            ...UnderConcurrentChurnAndTierMoves, quarantined on #1381 (#1369/#1370, which it exposed, are VIS-08's slot index lock)

### VIS-13: An index read at a snapshot answers what the snapshot holds, as the snapshot's queries do `[fatal]` `[silent]`
  invariant ∀ index read R of transaction t at snapshot S over keys [a, b] — `Transaction.EnumerateIndex`, the FK reverse lookup of a navigation and
            of a navigation view's refresh (FkReverseLookup.ForEachSource with an FkSnapshot), and a spatial query (SQ-07) — R yields exactly the entities t's field query over [a, b] finds — alive at S, at the key and value S holds;
            t's own destroys gone, t's own spawns in (at their own values), t's own value writes unseen (QENABLE-01) — in key order, each once
  invariant the trees hold the newest committed keys, and a destroy takes its entries out at its publish (VIS-09), so the entities a change newer
            than S destroyed or re-keyed are read at S beside them (IndexSnapshotSide`1): the slots whose KEY stamp is above S (VIS-12), in each
            cluster whose died or update watermark is — every slot with a destroy pending or a died watermark recorded without a stamp — each decided
            at S by its record and read at S (a Versioned component through its chain, a SingleVersion one from the slot a hidden destroy kept);
            their tree rows are dropped and their rows merged back in key order. An entity whose update moved no key stays a tree row, at its
            snapshot key, its value read through its chain. In a cluster no update newer than S touched, a slot's bytes are its value at S: its key
            is tested before its record is probed. An entity is dropped only when its tree rows can be in [a, b] (the slot's bytes hold their key),
            read beside them only when its key at S is: the work follows the changes the range can see, a refresh re-reading what it passed over.
            Three loads per archetype (KeyChangeCount, MaxDestroyStamp, MaxKeyMoveStamp) and nothing allocated when nothing newer than S
            destroyed or re-keyed an entity and t has no spawn or destroy. A Versioned tree row is returned only when its value at S carries the
            row's key: an update re-keys the trees before it copies its HEAD, so a side read between the two takes the stale HEAD key for the
            tree's (defense in depth; the window sits inside the writer's chain lock and has no staged test)
  invariant the FK reverse lookup answers "which sources' foreign key named the target at S" (FkSnapshot): built once per query or per view refresh,
            per candidate archetype, over the whole key range, the side's rows at the target's key merged with the tree's candidates; a navigation
            counts t's own spawns and destroys, a view's refresh committed state only. The cascade alone reads HEAD (no FkSnapshot): it destroys the
            children committed now (CASC-01). A navigation's predicates and a view's refresh read committed values at S (Transaction.TryReadCommitted), never
            t's own unpublished writes — a refresh folded the refresher's write and kept it past its rollback; a navigation view built from t's query
            is populated at t's snapshot (NavigationQueryBuilder.ToViewAt)
  invariant a spatial query, in each archetype that promises isolation, takes out of its HEAD answer every slot whose VALUE stamp is above S
            (a move of a Versioned spatial field is an update: it moves no index key) — every slot with a destroy pending or a died watermark
            recorded without a stamp — and decides it again: visible at S, in the queried realm, its bounds at S (a Versioned field through its
            chain, a SingleVersion one from the slot a hidden destroy kept) tested with the narrowphase's own decoder against the shape — box
            overlap, closest-point distance, the ray's slab, the frustum's planes (EcsQuery.ApplySpatialSnapshot). Two loads when nothing in the
            archetype is newer than S
  invariant a row's own resolution answers at S: a slot hidden by a destroy, or claimed by a spawn still publishing, is decided by its record, which
            must name the slot; a SingleVersion key of an archetype with a Versioned component is gated on born and died (the cluster's summary
            first); a Versioned one is read through its chain
  invariant against the tree reads: KeyChangeCount is read first and the trees after; a materialized read (several archetypes) builds the side
            after its tree reads. A streaming read (one archetype) reads its trees as it goes, a key's entries at once: after each read, and at
            the end, before it merges any row of that key, it re-reads the count and, when it moved, reads the slots changed since. Such an entity
            was unchanged at the previous read: at or below the PREVIOUS key read its row was read then, at its snapshot key, and is not added
            again; above it — between that key and the one just read its entry may have left the stretch the cursor crossed — it is added, and its
            tree rows from there on are dropped. The FK lookup reads each target's entries, then the count, before it decides any (FkSnapshot.Sync),
            once per target: an exact key needs no floor. Early termination stays proportional to what is read
  scope: Transaction.EnumerateIndex, Transaction.NoteVersionedUpdate, IndexEntityEnumerator.MoveNext, IndexEntityEnumerator.TryResolve,
         IndexEntityEnumerator.CheckChangesSinceLastRead, FkSnapshot.Admits, FkReverseLookup.ForEachSource, NavigationView.SourcesAt,
         NavigationQueryBuilder.ToViewAt, Transaction.TryReadCommitted, EcsViewFieldReader.EvaluateAllFields, EcsQuery.ApplySpatialSnapshot,
         EcsQuery.MatchesSpatialShape,
         IndexEntityEnumerator.AttachSnapshotSide, IndexSnapshotSide.Create, IndexSnapshotSide.Refresh, IndexSnapshotSide.IsDropped
  on_violation: an older reader lost an entity a destroy newer than its snapshot took out of the tree, returned an updated entity under its newer key
                with its older value, returned spawns newer than its snapshot (a SingleVersion key), and disagreed with its own queries on its own
                spawns and destroys. Live: the SWG item audit, reading ItemStack through EnumerateIndex, reported 5-21 phantom violations a pass
  verified: EnumerateIndexSnapshotTests [VerifiesRule] — a destroy newer than the snapshot on both index kinds; an update at its snapshot key and value;
            a later spawn; the transaction's own changes against its query; a SingleVersion key in an isolated archetype; a fan-out over two
            archetypes; during a streaming walk: a destroy ahead of the cursor (both kinds), an update moving an entity behind it, one moving a
            returned entity ahead of it, a destroy of an entity of the key being walked; a random oracle against the query (six seeds); counted:
            only re-keyed and destroyed entities in range read beside the trees; a destroy of the next key during the walk (both kinds); a side row
            merged inside a multi-value key; a navigation's own spawn under a source predicate. Mutants: the floor at the key just read, the cursor
            key shared with the current key, an own spawn's predicate read committed (each red); no side, no refresh during the walk, the refresh's floor
            ignored, changed entities not dropped, a hidden slot skipped, no born/died gate on a SingleVersion key, own spawns not read, own destroys
            not dropped, every update stamping the key (the counted case), none (twenty red), no key prefilter, a migration not carrying the key
            stamp — each red. NavigationSnapshotTests [VerifiesRule] — a navigation through spawns, destroys and retargets after its snapshot,
            with a source predicate too; its own spawns and destroys, not its own retarget; a view's fan-out against a source retargeted onto and
            away from the target, destroyed and spawned after the refresh's snapshot; a view populated at its creator's snapshot; a navigation view's
            and an EcsView's refresh made with the refresher's own unpublished write, then rolled back; a navigation's target predicate
            over targets the transaction wrote, read before or not (mutant: the cached read under its own write — red). Mutants: the navigation at HEAD, the view's
            fan-out at HEAD, the committed read seeing own writes, a fresh transaction populating, a tree candidate not gated, the side's rows not
            added — each red. SpatialSnapshotTests [VerifiesRule] — a destroy newer than the snapshot and a later spawn, on every shape; a Versioned
            spatial field moved after the snapshot, on every shape. Mutants: no spatial snapshot, Versioned bounds from the HEAD, the HEAD answer
            kept, the shape test admitting all — each red
  requires: VIS-09, VIS-12, VIS-11, QENABLE-01
