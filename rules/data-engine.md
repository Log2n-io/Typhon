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

**Scope of this file: family 1, revision elements.** The other five stamp families — `BornTSN`, `DiedTSN`, the cluster
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
  note: family-1 scope; the `Pending(owner)` marks of families 2–6 join it in Phase 5
  scope: ComponentRevisionManager.ScrubChainToHead
  on_violation: a transaction of the reopened engine whose TSN equals a crashed owner's finds that owner's flagged
                revisions as its own, or a reader resolves them through its card

### VIS-08: A pending spawn is never absent from a scan, and never persisted as occupied `[fatal]` `[silent]` `[UNBUILT]`
  invariant a spawn is placed in PREPARE: its EntityMap record with `BornTSN = Pending(owner)` (bit 47), its slot in the
            cluster's in-memory pending mask, its occupancy bit clear; PUBLISH writes `BornTSN = stamp`, sets the occupancy
            bit, then clears the pending bit. Every occupancy-driven scan visits `occupancy | pending` and resolves a
            pending slot's born through the owner's card (VIS-01); a slot claim treats a pending slot as taken
  invariant the pending mask is never written to a page: a fuzzy checkpoint can persist a pending slot's bytes, never its
            occupancy bit, so a crash rebuild from occupancy resurrects no uncommitted spawn (CM-01)
  invariant an abort after PREPARE un-places the spawn — slot, record, index and spatial entries (AP-07)
  unbuilt: plan 06 step 5.2. Today `FinalizeSpawns` places a spawn at PUBLISH, after the stamp: a reader that resolved
           the commit's revisions through the card finds no record — half a commit
  spec: tla/CommitStampFamilies.tla — SpawnRead (spawnatpublish, scanoccupancy)
  on_violation: a reader holding the commit's revision of one entity misses the entity it spawned, by lookup or by scan
  requires: VIS-01, VIS-02, CM-01

### VIS-09: A destroy is pending before the stamp, and its slot outlives every snapshot that sees the entity `[fatal]` `[silent]` `[UNBUILT]`
  invariant a destroy writes `DiedTSN = Pending(owner)` in PREPARE and the stamp at PUBLISH; of two concurrent destroys the
            first stamp wins, by CAS
  invariant the occupancy bit and the EntityMap record of a destroyed entity are released by the cleanup once MinTSN is past
            its died stamp, not at PUBLISH: an older snapshot still finds it by scan
  unbuilt: plan 06 step 5.3. Today the destroy's died stamp is written at PUBLISH, and its slot released there
  spec: tla/CommitStampFamilies.tla — DestroyRead (destroyatpublish, releaseatpublish)
  on_violation: a reader begun after the destroy's stamp still sees the entity alive while it holds the commit's other
                effects; an older reader's scan loses an entity its snapshot holds
  requires: VIS-01, VIS-06
