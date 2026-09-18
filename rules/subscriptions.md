# Subscriptions Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-09-18 |
| Domain | Engine-owned replication: per-entity replication state, its storage, and what bounds its cost |

> Invariants that keep replication cost tied to what clients are actually looking at, and keep per-entity
> replication state attached to the entity it describes across every way an entity can move.

---

## Module: Tick Placement

### SUB-02: Compute after the fence, publish after the flush `[fatal][silent]` (amends TP-01 / TP-01a)
  invariant within one tick: WriteTickFence completes → replication computes → the UoW flush completes → replication publishes
  invariant [tick aborted ∨ fence failed] → nothing is computed AND nothing is published; no baseline moves
  never publish a frame for a tick that is not yet durable — publication is gated on the flush, not merely ordered after it
  never suppress one of compute / publish without the other: they are skippable together and only together
  never let a replication failure stop the engine: a throw in any stage is logged, captured and surfaced to the host, and the track is skipped for that
    tick, but it does NOT latch the terminal fence verdict
  never publish for a tick whose compute faulted — a stage throw suppresses publication exactly as an abort or a fence failure does
  scope: TyphonRuntime.OnTickEndInternal, DagScheduler.DispatchDeferredTracks, RuntimeSchedule.EngineSubscriptionsTrack,
    SubscriptionsContext.ShouldTrackRun, SubscriptionsExecSystemBase.ShouldRun,
    Track.FailureIsTerminal, DagScheduler.RecordEngineTrackFailure,
    SendPump.PublishAndWake, SendPump.DiscardProduced, SubscriptionsRuntime.PublishFrames, SubscriptionsRuntime.DiscardFrames
  on_violation: computing for an aborted tick reads structures a partly-run fence left incomplete, and publishing from it tells a session a story the WAL
    does not carry. Suppressing publish alone is worse than either: the frames were produced, so the per-entity state has already advanced, and every
    session's baseline is now ahead of what it was actually sent — which no later frame corrects, because records describe the present rather than a delta.
  rationale: the track is a barrier (PH-01) placed after Engine-Post, so every fence phase is complete before the first stage starts; and it runs INSIDE the
    EW-01 window, which is the quiescence that snapshot-less SingleVersion and Transient reads require (AC-05, SNAP-02). Publication cannot join it there:
    a track cannot pause for the flush and resume, and PS-09 forbids holding an epoch scope across a blocking wait.
  note the gate is evaluated by EVERY stage, not by the DAG's root. `Events` is a parallel branch with no predecessor, so a root-only gate would leave it
    dispatching on an aborted tick. It is also read LIVE from the scheduler rather than snapshotted, because DispatchDeferredTracks walks Engine-Post and
    Engine-Subscriptions in one loop — a fence failure during Engine-Post latches after any per-tick reset has already run.
  note the two clauses above are a PAIR, and the second exists because the first removed the signal publication used to key on. Publication is gated on
    `!tickAborted && !fenceFailed`; a stage throw latches neither (engine tracks are already exempt from the abort latch, and the no-stop clause skips the
    fence latch), so making replication non-terminal silently made every faulted tick publishable. `SubscriptionsContext.Faulted` restores the pairing.
    Without it the isolation fix would have traded a crash for a worse failure: frames half-produced by a faulted tick, published as though the tick had
    succeeded, moving every receiving session's baseline past records it was never sent — and SUB-03 makes baselines advance only on what was carried.
  note the no-stop clause is a DEVIATION from how every other engine track behaves, and deliberate. A throw on an engine-tagged track latches
    `IsFenceFailed`, which is terminal — `ExecuteCallbacks` returns early on every later tick. That is right for the fence, whose half-finished work leaves
    cluster pages dirty and un-logged for the checkpoint to persist (TP-01a), and wrong here: replication writes only RAM-only blocks that no checkpoint or
    WAL ever sees, so nothing a later tick does can compound a replication bug. Left unchanged, a defect in the newest and least-proven subsystem in the
    engine would have been strictly more destructive than the same defect in a user system. `Track.FailureIsTerminal` carries the distinction, so the fence
    keeps its semantics exactly.
  note the serial and parallel fence paths dispatch the track identically, and both hold an EW-01 window across it. On the serial path the window opened
    inside `DatabaseEngine.WriteTickFence` closes when that returns, so `OnTickEndInternal` opens its own around the fence AND the dispatch;
    `ExclusiveWindow` keeps a depth rather than a flag, so the two nest.
  invariant [a tick produced frames ∧ it did not publish] → every session that produced one is closed with 1011
  note the discard clause is the other half of the gate, and it exists because a frame is produced BEFORE the tick is known to be publishable. A frame
    describing a tick that never becomes durable can never be sent — the committed-tick gate refuses it forever — so the session holding it fills its K slots
    and stalls, while its client's baseline is already ahead of what the WAL can prove. Closing is the only outcome that does not silently diverge. It is
    reached from four places, which are the four ways a tick can fail to publish: an abort, a fence failure, a faulted replication stage, and a flush that
    threw (the last one from inside the flush phase, because the publication gate below it is never reached).
  verified: SubscriptionsTrackTests.NormalTick_RunsFenceThenComputeThenFlushThenPublish,
    SubscriptionsTrackTests.AbortedTick_StillFencesAndFlushes_ButNeitherComputesNorPublishes,
    SubscriptionsTrackTests.AStageThatThrows_DoesNotStopTheEngine,
    SendPumpTests.NoFrameIsSentBeforeItsTickIsCommitted
  [UNBUILT] One clause states intent rather than behaviour, and is called out so a reader does not mistake the `verified:` above for the whole rule.
    FENCE FAILURE is verified only for the abort case; inducing a fence-phase throw needs a seam no test has yet, and the code path is shared with the
    abort (both read the same two latches), so the risk is a shared-path assumption rather than an untested branch.

---

## Module: Projection

### SUB-01: Replication reads component values only through the archetype's cluster layout `[fatal][silent]`
  invariant ∀ projected field f: value(f, cluster, slot) is read at
    ClusterLayout.ComponentOffset(f.slot) + slot × ClusterLayout.ComponentSize(f.slot) + Field.OffsetInComponentStorage
  invariant ∀ compiled plan P: P holds the archetype's own ArchetypeClusterInfo and no other engine storage structure
  never resolve a component's column, a slot's stride or a field's offset from anything but the archetype's layout and the component's
    measured schema — not from a copy, not from a recomputation, not from a structure replication owns
  never reach a value through a component segment, a chunk accessor or an entity-location map: replication addresses SLOTS OF A CLUSTER,
    never entities of a table
  never invoke a selector, a delegate or reflection per entity — a declaration's field name becomes an offset once, at Start, or it is
    refused there by name
  scope: ProjectionCompiler.Compile, ProjectionCompiler.VelocityCodec, CompiledField.ComponentSlot,
    CompiledField.ComponentOffsetInCluster, CompiledField.ComponentSize, CompiledField.FieldOffsetInComponent,
    CompiledPosition.FieldOffsetInComponent, CompiledProjectionPlan.ClusterLayout, ProjectionColumn, ProjectionColumnWalk.Quantize,
    ProjectionColumnWalk.Walk, ArchetypeMetadata.GetSlot, ArchetypeClusterInfo.ComponentOffset, ArchetypeClusterInfo.ComponentSize,
    DBComponentDefinition.SpatialField, ClusterRef.GetReadOnlySpan
  on_violation: a second reader of the storage, with its own idea of where a value lives. It does not fail — it reads the neighbouring
    field, or the neighbouring slot, and replicates that. Every client then agrees with every other client on a world the server does not
    have, and nothing on either side reports an error. The engine's own layout moves under refactors (a component gains a field, a
    cluster's N changes with the slot count); a copy of the offsets does not move with it, and the divergence is silent from the first
    tick after the refactor.
  rationale: the cluster layout is the one description of where a component value is, and every other reader — queries, the fence, the
    spatial maintenance, ClusterRef itself — already goes through it. Replication reading the same bytes by a second route buys nothing
    and costs the guarantee that all of them see one value. It is also what keeps the per-entity pass free: the layout hands back a
    contiguous column per field, so the pass walks columns rather than chasing entities, and the codec becomes a struct type parameter
    of that loop instead of a delegate per field per entity.
  note the rule constrains the PLAN as well as the read, and deliberately. A pass that reads correctly today but holds a
    `ComponentTable`, an `ArchetypeClusterState` or an entity map has the second route already wired; the next slice that needs
    something faster takes it, and the first clause is then violated by a diff that looks local.
  note Versioned components are no exception and need none: a cluster slot caches the committed HEAD (copied there at commit), so the
    projection reads it through `ClusterRef.GetReadOnlySpan` like any other column, and after the fence it is the tick's committed value.
  note defined in the design series at design/Subscriptions/01-model.md § 2 and design/Subscriptions/02-execution.md § 4.
  verified: ProjectionReadsClusterLayoutTests.ProjectedValuesEqualClusterRefSpans (the read: a component written through
    ClusterRef.GetSpan — the path that sets no dirty bit — projects to exactly what ClusterRef.GetReadOnlySpan reports, field by field
    and slot by slot), ProjectionReadsClusterLayoutTests.ThePlanHoldsNoSegmentOrEntityLocationReference (the plan: a member-type
    assertion over every compiled type, which also asserts the ArchetypeClusterInfo IS held, so the check cannot become vacuous)

---

### SUB-10: Change detection compares quantized values and depends on no write-path signal `[fatal][silent]`
  invariant ∀ watched entity e, ∀ tick T: e's projection is recomputed and compared with the copy its entry holds, whatever any write path
    did or did not signal
  invariant ∀ projected field f of e: the value compared is f's wire CODE. quantize(read(f)) is computed ONCE per entity per tick, and that
    one code is what is compared, what is encoded into the record, and what is stored for the next tick's comparison
  invariant ∀ change group g of e: [g's stored body differs from the body just encoded] ↔ [g's change tick becomes T]
  invariant an entity whose every group compared equal produces NO record
  never read a dirty bit, a modified flag, a change set or a cluster process bitmap to decide whether an entity changed — `ClusterRef.GetSpan`
    is "the one write path that signals nothing" (`ClusterRef.cs:157-162`) and `ClusterRef.WriteSpatial` does not mark the slot dirty
    (`ClusterRef.cs:342-348`), and between them they carry the traffic
  never quantize a value twice, and never compare DECODED values: a second rounding of the same number is how the bytes that were compared stop
    being the bytes that are sent
  never compare a group body against a stored copy that was not zero-padded to the section's widest form — the padding is what makes a
    fixed-width comparison of variable-length canonical encodings exact
  scope: ProjectionPass.ProjectBlock, ProjectionColumnWalk.Quantize, ProjectionColumnWalk.Walk, ProjectionColumnWalk.WalkRatio,
    ReplicationHotEntry.GroupTicks, ReplicationHotEntry.PackedState, ReplicationColdEntry.PrevQuantizedPosition,
    ClusterRef.GetSpan, ClusterRef.WriteSpatial
  on_violation: the two highest-frequency write paths in the engine set no signal, so a detector keyed on one reports NOTHING for a position that
    moved every tick and for any component written through a span. Every client then holds a world that stopped updating, with no error on either
    side and no counter that is wrong — the server believes it sent what changed, and nothing changed as far as it can tell. The reverse
    failure, comparing decoded values or quantizing a second time on the way out, is quieter still: the record carries bytes that differ from the
    ones the comparison accepted, so the client's value and the server's stored copy disagree by a quantum that never resolves, because the next
    tick compares against the stored copy and finds it unchanged.
  rationale: the signals exist for the WAL and for change-filtered dispatch, and both were deliberately withheld from these two paths so that
    high-frequency simulation state does not flood the WAL writer with one frame per entity per tick. Replication cannot ask for them back without
    re-imposing exactly that cost. Comparing the quantized value instead is also strictly better than a dirty bit would have been: a write that
    lands on the same code is not a change, so an entity jittering inside one position quantum sends nothing, which no write-path signal can
    express.
  note the comparison is the ENCODE. A group's body is encoded from the codes into a scratch and compared byte-for-byte with the stored copy,
    rather than the codes being compared and the body encoded afterwards: the entry has room for the body and not for the codes, and comparing the
    bytes is a comparison of the codes by construction — canonical encodings are minimal, so no two different code sets zero-pad to the same
    bytes.
  note the POSITION is compared the same way and by the same rule, through the quantized copy in the cold entry, and stamps the motion group's
    tick. What it does not do is fit or emit a motion SEGMENT, which is a separate decision about when a client's extrapolation has drifted far
    enough (design/Subscriptions/02-execution.md § 4).
  note defined in the design series at design/Subscriptions/02-execution.md § 4 principle 8.
  verified: ProjectionPassTests.AWriteThroughGetSpanIsDetected and ProjectionPassTests.AWriteThroughWriteSpatialIsDetected — the two write
    paths the rule names, each driven through the real engine API that sets no signal, each requiring the pass to have produced the record and
    stamped the group tick. Falsifiability is proved by ProjectionPassTests.AChangeNoPassEverLooksAtIsNotDetected, which performs both writes and
    does NOT run the pass, requiring the entry to be untouched — so the verifiers are discriminating the comparison from the write rather than
    observing something the engine would have done anyway.

---

## Module: Replication State Sizing

### SUB-13: Replication cost follows the watched set, never the archetype `[design]`
  invariant ∀ replicated archetype A: replication_memory(A) = O(watched_clusters(A))
  invariant ∀ replicated archetype A: per_tick_work(A) = O(watched_entities(A))
  never any per-cluster replication structure sized by A's chunk-id space, entity count, or segment capacity
  scope: ReplicationDirectory, ReplicationBlockPool, NetIdAllocator, ArchetypeReplicationState,
    SubscriptionsOptions.StatePoolBudgetBytes
  on_violation: a database holding a billion entities of a replicated type pays replication memory for all of
    them while a handful are in view — an estimated ~381 MB of directory alone at 1 B entities to track perhaps
    ~170 k watched clusters (0.4 % fill). The engine then cannot host a large replicated archetype at all, which
    is the property replication exists to provide.
  rationale: a cluster chunk id is a PERSISTED FILE ADDRESS, not a residency measure. Ids are handed out by the
    segment and freed only when a cluster truly drains — evicting a chunk's pages never releases its id. So a
    flat array indexed by chunk id costs what the DATABASE holds, and no cluster-count threshold fixes that;
    a threshold only moves where it breaks. The engine's own per-cluster side tables (`ClusterAabbs`,
    `ClusterCellMap`, `ClusterSpatialIndexSlot`) are flat arrays of exactly this shape and are the PRECEDENT
    THIS RULE REFUSES — see Typhon issue #960, which tracks that cost on the engine side.
  note a flat array remains legitimate for a specific archetype behind a self-justifying size test — take it
    when that archetype's peak cluster count makes the array smaller than the map. It is never the default.
  verified: ReplicationDirectoryTests.SparseChunkIdsCostOnlyWhatIsWatched — the MEMORY invariant, and
    NetIdAllocatorTests.ChurnAtAStablePopulationDoesNotGrowTheIdentitySpace for the identity space's half of it.
  verified: ProjectionPassTests.PerTickWorkFollowsTheWatchedSet — the PER-TICK-WORK invariant, which had no verifier
    until a projection pass existed to measure. It spawns an archetype across many clusters, marks one cluster's
    entities watched, and requires the slots the pass addressed to equal the watched count exactly rather than
    asymptotically: the pass either touched a slot or it did not. The counter is accumulated once per block from a
    popcount, so the measurement is not itself per-entity work and is present in Release, where the claim has to hold.
    Falsifiability is proved by ProjectionPassTests.AWholeArchetypeWalkIsDetected, which marks the whole archetype and
    requires the same assertion to reject the resulting count.

### SUB-07: No managed allocation in the replication steady state `[perf]`
  invariant once the watched set has stopped growing, a replication tick allocates no managed memory
  never `new`, LINQ, lambda capture or boxing on the per-hit or per-entity replication path
  scope: ReplicationDirectory, ReplicationBlockPool, NetIdAllocator, ArchetypeReplicationState
  on_violation: replication adds GC pressure proportional to the watched set every tick, which shows up as
    tick-time jitter rather than as a failure — the hardest class of regression to attribute after the fact
  note structural growth is exempt and deliberate: the block pool commits a slab, the directory rehashes, and the
    identity allocator grows its side arrays. These happen at the track prologue AND at the single-threaded blocks
    step — the blocks step runs INSIDE the tick, so the exemption covers allocation within a tick, not merely
    between ticks. None of them happens on the per-hit path, which is the part that must stay allocation-free.
  note NOT VERIFIED. No test in the suite asserts allocation behaviour for these types, so this rule carries no
    `verified:` field rather than a verifier that cannot fail. A verifier needs an allocation probe around a
    steady-state tick, which is only meaningful once the track actually dispatches one.

---

## Module: Per-Entity State Lifetime

### SUB-06: A reused network identity is observed as leave-then-enter `[fatal][silent]`
  invariant ∀ netId n reused by a different entity: generation(n) changes between the two holders
  invariant [Release(n) in tick T] → [generation(n) incremented] → [n quarantined] → [n reachable by Allocate no earlier than T + quarantineTicks]
  invariant quarantineTicks = SubscriptionsOptions.CloseAfterSkips + 1 — the whole window a session may be skipped for, plus the release's own tick
  invariant the identity space is GLOBAL: a netId names at most one live entity across the whole database, never one per archetype
  never one netId held by two live entities at the same time
  never reissue an identity before its quarantine window has passed
  never a Release of an identity that is already free or quarantined (it would thread the list to itself, after which every
    Allocate returns that same identity and LiveCount runs negative)
  scope: NetIdAllocator.Allocate, NetIdAllocator.Release, NetIdAllocator.DrainQuarantine, NetIdAllocator.GenerationOf,
    ArchetypeReplicationState.NetIds, ReplicationHotEntry.NetId, ReplicationHotEntry.Generation
  on_violation: a session that missed the release sees one identity carry a second entity's data and concludes the
    entity moved rather than that it was replaced — the client's world silently disagrees with the server's, with no
    error on either side. The double-release form is worse: two live entities share an identity, so one of them is
    unaddressable for the rest of the session.
  rationale: sessions compare the pair (netId, generation) between consecutive ticks; they do not need it unique for
    the process lifetime, which is why 16 bits suffice. A wrap needs 65 536 reuses of one identity.
  note the generation is bumped on RELEASE, not on the next allocate, so an entity that leaves and is never replaced
    still reads as gone to a session holding the old pair.
  note the window is the SKIP window and not one tick (D1, design/Subscriptions/02-execution.md § 5). One tick keeps a leave and an enter out of the same
    frame of a session that receives every tick. It does not keep them out of the same frame of a SKIPPED session, which receives everything since its
    baseline in one message: an identity released at N and reissued at N+1 reaches such a session as a leave and an enter for the same number, in one frame,
    in an order it cannot recover. The alternatives considered and rejected were putting the generation on the wire beside every netId (a byte per record,
    for a case that never happens), splitting a skipped session's frame at reuse boundaries (the assembler would need a per-identity history it does not
    keep), and tick-stamping events (the same cost, on the block that can least afford it).
  note the quarantine is a RING of per-tick buckets rather than one list, so the hold costs one rotation per tick regardless of its width: a release joins
    the current bucket, and the drain splices the bucket filled a full ring ago onto the free list.
  note defined in the design series at design/Subscriptions/02-execution.md; it was cited by code before it was
    written down here, which neither rule gate can detect — check-rule-scopes.py validates scope symbols only, and
    audit-rule-coverage.py's UNKNOWN_RULE_ID inspects [VerifiesRule]/[RuleMutant] attributes only.
  verified: NetIdAllocatorTests.ReusingAnIdentityBumpsItsGeneration,
    NetIdAllocatorTests.ReleasingTheSameIdentityTwiceIsRejected,
    NetIdAllocatorTests.AReleasedIdentityIsHeldForTheSkipWindow

### SUB-09: State follows its entity, and never survives slot reuse `[fatal][silent]`
  invariant ∀ watched entity e: the hot/cold entry describing e is reachable from e's CURRENT (cluster, slot)
  invariant ∀ move of a watched e from (c1,s1) to (c2,s2): e's entry is written to (c2,s2) and (c1,s1) is CLEARED — the entry exists at exactly one
    address, never at two and never at none
  invariant [the destination cluster has no block when the move executes] → the entry is copied aside, BY VALUE, and written by the next
    single-threaded point after the blocks step; never held as a pointer to the source, whose slot can be reused in the same step
  invariant ∀ chunk id c freed by a drain: [directory entry for c cleared] → [FreeChunk(c) returns the id]
  invariant ∀ read of a slot's entry: the entry's EntityId is compared with the slot's, and a mismatch releases the identity and
    re-initialises — this is what catches slot reuse inside a LIVING cluster, which no move hook can see
  never a directory entry naming a block whose cluster has been freed
  never an entry inherited by a different entity through slot reuse or a recycled chunk id
  scope: ReplicationDirectory.TryAdd, ReplicationDirectory.TryRemove, ReplicationBlockPool.TryRent,
    ReplicationBlockHeader.ChunkId, ReplicationHotEntry.Entity,
    ArchetypeReplicationState.MigrateEntry, ArchetypeReplicationState.DrainParkedEntries, ParkedEntryList.Add,
    ArchetypeReplicationState.TryReleaseBlock, ArchetypeReplicationState.ReleaseBlockForDrain,
    ArchetypeReplicationState.AttachTo, ArchetypeClusterState.ReplicationState,
    ArchetypeClusterState.DrainPendingClusterFinalizations, ArchetypeClusterState.ReleaseSlot
  on_violation: hits into a cluster that inherited a recycled id find state describing the cluster that
    drained — a client is told about an entity that no longer exists, or told the wrong values for one that
    does, with no error anywhere. Silent because every structure involved stays internally consistent. The movement half fails more cheaply
    but just as quietly: an entry left behind makes the destination slot read as a brand-new entity, so every watching session is sent a
    leave and a full enter for something that walked over a boundary, losing its netId and the client's interpolation state. Measured before
    the hook existed: netId 54 became 32 across one 4 000 m move.
  requires: the engine's clear-at-drain convention for per-cluster side tables (`ResetClusterVisibility`
    requires every site freeing a cluster chunk to clear its side tables before the id is handed back)
  rationale: the move hook sits beside the component copy in `ExecuteMigrations` because that loop already has both addresses, already runs
    under the fence's ordering, and is already cut by destination cell so no two workers write one destination. Every kind of move —
    crossing, relocation, repair — goes through it, so there is one site rather than three.
  note (2026-09-18) the design's per-slice parked lists were NOT built, deliberately. `08 § 4` proposes one list per migration slice so that
    parking synchronises nothing, but sizing them needs the slice count before the slices run and only the fence knows it. Parking takes a
    lock on one list instead. It is affordable because parking is the rare branch of a rare branch — an entity that is watched AND that moved
    into a cluster nobody was watching — so it is not on the path most moves take; `EntriesParked` is what would show it becoming one.
  note a parked entry whose destination STILL has no block when the prologue runs is dropped, and that is correct rather than lossy: nobody
    watches that cluster, so there is nothing to read the entry out of, and the entity is initialised from current values the first time
    somebody does. `ParkedDropped` counts them.
  note the `[UNBUILT]` marker was dropped on 2026-09-18 when the move hook landed. The rule's second historically-missing item, the
    per-entry `EntityId` check on the read path, turned out to be present already (`ProjectionPass` compares `hot->Entity` with the slot's id,
    releases the identity and re-initialises on mismatch) — the note claiming it missing was stale.
  verified: MigrationIdentityTests.ANetIdAcrossAClusterChange, which asserted the OPPOSITE until the hook landed and was written inverted on
    purpose so that it would go red and force the edit; MigrationIdentityTests.TheEntryIsCarriedAcrossRatherThanReissued, which reads the
    migration counters because a netId that is unchanged is also what a LIFO allocator handing back what it just released would produce, so
    the outcome alone does not discriminate; MigrationIdentityTests.TheSlotTheEntityLeftHoldsNoEntryAfterwards, which keeps a second occupant
    in the source cluster so its block survives the move and the cleared slot can actually be read. The drain half keeps its earlier coverage:
    `ReplicationDrainHookTests` reaches the INLINE persistent site (a destroy commit passes no `deferFinalize`, so it finalizes at commit) and
    `ReplicationDrainHookParallelFenceTests` reaches the DEFERRED site through the dispatched `ArchetypeFinalize` item.
  note the pure-Transient `ReleaseSlot` overload's site cannot fire for any replication scenario that exists:
    `[SpatialIndex]` is rejected on a Transient component (`DatabaseDefinitions.cs:369-372`), so a pure-Transient
    archetype is never spatial and never holds a watched cluster. The hook is kept as one null test, defensive
    against a future non-spatial replication mode, and is untested BY CONSTRUCTION rather than by omission.
  note the placement of the drain hooks deviates from the design, deliberately: § 4 proposed a single call inside
    `FinaliseEmptyClusterCellState`, but that method early-returns when the archetype has no grid, no
    `ClusterCellMap`, or an unmapped cell, so a non-spatial or grid-less archetype would drain a cluster and
    never release its block. The three call sites are the complete set instead.
  note the differential oracle does NOT cover this and was briefly believed to: `FrameHarness.RunTick` runs the replication track but not
    the ECS tick fence, so until 2026-09-18 the oracle's teleports moved coordinates and nothing ever migrated. The fence is now called
    per tick there, which makes the oracle exercise cluster change — but it still cannot see identity STABILITY, because it compares the
    server's netIds against the client's and both agree whether an identity was carried across or reissued.

---

## Module: Session Ownership

### SUB-05: Session state has one writer — the tick; transport threads touch rings, leases and counters `[fatal][silent]`
  invariant ∀ session row r whose State has been published: ∀ field f ∉ {SendsInFlight, PendingClose}: writer(f) = the tick side
  invariant a transport thread's WHOLE write set on the session table is exactly:
    [TryLease: the free stack, never a row] → [Open: a row no other thread can reach yet, ending in one release store on State]
    → [RequestClose ∨ BeginSend ∨ EndSend: one Interlocked word each]
  invariant ∀ value a transport thread must publish to the tick: it is a NUMBER written atomically, never a managed reference
  never a transport thread writes a published row's State, Role, Flags, CloseCode, CloseReason, Resumable, BytesPerSecond,
    MaxObservers, MaxKnownEntities, FrameBytes, ClientMessageBytes or Controlled
  never a transport thread calls a tick-side member — Close, BeginTick, ApplyPendingCloses, SetProfile, SetControlled, SetBudget —
    nor writes a slot-indexed side array (the kind, profile, close-reason, appData and limits arrays) after publication
  never the tick and a transport thread inside one guarded member at the same time (ReplicationThreadAffinity)
  scope: SessionTable.TryLease, SessionTable.Open, SessionTable.TryAdmit, SessionTable.RequestClose, SessionTable.BeginSend,
    SessionTable.EndSend, SessionTable.Close, SessionTable.ApplyPendingCloses, SessionTable.BeginTick, SessionTable.SetProfile,
    SessionTable.SetControlled, SessionTable.SetBudget, SessionRow.State, SessionRow.SendsInFlight, SessionRow.PendingClose,
    SessionEvents.Append, SessionEvents.BeginTick, ReplicationThreadAffinity.Enter,
    SubscriptionConnection.OnMessage, SubscriptionConnection.OnClosed, SubscriptionConnection.Kick,
    SubscriptionConnection.LastAppliedTick, SubscriptionAcceptor.Accept
  on_violation: nothing throws. Every replication stage reads session rows with no synchronization at all, on the strength of this
    rule, so a second writer produces a row that is half old and half new for exactly as long as the stage takes to read it — a
    frame sized by the previous limits, sent to a session that has already closed, or skipped for a budget nobody set. On arm64 it
    is worse than a torn value: without the release store the reader can see the identity of a row whose fields have not landed,
    and every field it reads is whatever the slot's last occupant left there.
  rationale: the alternative is a lock per session on the tick path, taken by every stage for every session of every tick, to
    protect against writes that happen a handful of times per connection. Instead the two things a transport thread genuinely has
    to tell the tick — "a frame is still on my socket" and "please close this" — are each one word it can publish atomically, and
    everything else it wants to say goes through the lifecycle stream, which is a locked producer and a lock-free consumer
    because it is written once per connection and read once per tick.
  note the close request carries NO reason string, and that is the rule rather than an omission: a code and a reason enum pack into
    one word, while a managed string would be a second write the tick could read before the first — one row, two writers.
  note the tick is not one THREAD. The guard is a re-entrancy guard, not a thread-identity one: replication legitimately runs on the
    driver thread one tick and a pool worker the next, and what must never happen is two callers inside at once (see
    `ReplicationThreadAffinity`'s own note, and CX-05).
  note a value the transport learns and the tick wants but that is not on the allow-list lives on the CONNECTION, not on the row —
    `SubscriptionConnection.LastAppliedTick` is the first of them, read by the lag skip.
  note defined in the design series at design/Subscriptions/04-transport.md § 2-§ 3 and design/Subscriptions/01-model.md § 3.
  verified: SessionWriterOwnershipTests.TransportThreadTouchesOnlyItsAllowList — drives admission, PING, the send counter and BYE
    from a thread that is not the tick's, with the tick quiescent, and requires that no field outside the allow-list moved; then
    runs the tick's guarded members and the transport path concurrently and requires the DEBUG re-entrancy guard never to fire.
    Falsifiability is proved by SessionWriterOwnershipTests.ARogueTransportThatWritesATickOwnedFieldIsDetected, which reaches an
    ordinary tick-side mutator from the transport thread and requires the verifier's own assertion to reject it.

---

## Module: Interest

### SUB-16: A bounded observer watches what its region contains, and a session with no region watches nothing `[fatal][silent]`
  invariant ∀ session s with a bounded observer of radius r centred at c, ∀ tick T: s's watched set is exactly the entities whose
    position lies within r of c — no entity outside it is marked watched, and none inside it is left unmarked
  invariant the region is resolved through the ENGINE's spatial index, never by testing the archetype's active-cluster list: a walk over
    every cluster is the cost the observer exists to remove, and a second narrowphase written here is a second answer to a question the
    engine already answers
  invariant [s has never been placed] → s watches NOTHING. A default position is a legal world position, so "never placed" and "placed at
    the origin" must be distinguishable, and the unplaced session is the one that sees nothing
  invariant the walked region contains the hysteresis band: an entity between the enter and leave radii stays watched, or the band cannot
    stop it being reported as a leave on the tick it crosses
  invariant a session's region is fixed for the tick — every archetype it reaches is resolved around one centre, read once where the tick's
    sessions are partitioned rather than per chunk
  never resolve a declared region shape as though it were another: a profile whose observers have different shapes is a near/far tier, and
    the tiers differ in budget, rate and record kind, so their union is a wrong answer rather than an approximation
  never centre a region somewhere the declaration did not name — an observer that asked to follow an entity and got the session's viewpoint
    instead is a silent substitution; refuse it while the follow is unbuilt
  scope: InterestPass.WalkSphere, InterestPass.FlushSphereRun, InterestPass.CompileProfiles, SessionTable.SetViewpoint,
    SessionTable.TryGetViewpoint, SubscriptionsCommands.Place, ArchetypeClusterState.QueryRadius
  on_violation: the failure is in cost, not in correctness, and that is what makes it silent. A region resolved as the whole world still
    sends every client a correct view; it simply costs what a whole-world view costs, and every per-session measurement taken through it
    describes a client no game has. Measured before this existed: two hundred "player" sessions performed ~295 000 entity visits to deliver
    ~13 000 records, 22 visits per record, and replication took 43.7 % of the tick. The reverse — a region resolved too small — is not
    silent at all: the client is simply missing entities that are in front of it.
  rationale: interest is the one term that multiplies by session count, so it is the only place where a wrong constant becomes a wrong
    exponent. Everything else in the track is per-record or shared.
  verified: SphereObserverTests.APlacedSessionWatchesTheDiscAroundItAndNothingElse, whose expectation is the arithmetic count of the grid
    points inside the disc rather than a number recorded from a run; SphereObserverTests.TwoSessionsPlacedApartWatchDisjointSets, which is
    what discriminates "bounded by the radius" from "bounded at all"; SphereObserverTests.AnUnplacedSessionWatchesNothing over a populated
    origin, so the sentinel cannot be faked; SphereObserverTests.AWorldObserverOverTheSameEntitiesWatchesAllOfThem as the contrast the
    narrowing is measured against; SubscriptionsRegistryTests.ASphereThatFollowsAnEntityIsRefusedUntilTheEngineSideFollowExists.

---

## Module: Session Lifecycle

### SUB-14: A session the tick closes is told so before its link is closed `[fatal][silent]`
  invariant ∀ session s, ∀ close the TICK decided (silence, skip run, an unpublished tick, the application's Kick verb):
    a KICK carrying the close code reaches s's link, and the link is closed after it — KICK, then close (03 § 3; on TCP, KICK then FIN)
  invariant the KICK is sent by s's own send pump, never by the tick thread: the transport's one promise is at most one send in flight per
    session, and a tick writing to a socket breaks both that and the rule that the tick does no I/O
  invariant [the client itself started the close] → no KICK: the connection unbinds its link before asking the tick to close, and an absent
    link is what tells the pump there is nobody to inform
  invariant ∀ SDK: the close code it reports is the KICK's when one arrived, and the transport's only otherwise — TCP carries no code, so a
    FIN alone says 1001 for every reason there is
  never mark a row Closing and queue its Closed event as the whole of a close: the row is the engine's bookkeeping, and the client shares
    none of it
  never leave a transport bound after a terminal close: a socket whose peer has sent FIN still reports itself connected until the next write
    fails, so an SDK that keeps it answers "connected" for a session that ended
  scope: SessionTable.CloseCore, SubscriptionsIngress.BeginTick, SendPump.RequestKick, SendPump.TryKickAsync, SubscriptionConnection.Kick,
    FrameAssembler.SweepSkipPolicy
  on_violation: the worst state a client can be in. Everything it can observe says it is connected — the socket is open, no code arrived, no
    error was raised — and no frame will ever come again. It cannot even reconnect, because nothing told it to. It is silent on the server
    too: the close counters move, the session leaves the table, and an application that kicked a player is told it worked.
  rationale: 02 § 6 and 05 § 1 already specify the message and the SDK's response to it; what was missing was any caller. The pump is the
    right sender because it is already the single writer for the slot, so the KICK is simply the last message it sends.
  verified: BotSwarmSmokeTests.ASilentSessionIsClosedAndItsClientIsTold — a client that never pings, asserting in one place that its frames
    stopped, that a close arrived, that the code was 4001 rather than 1001, and that it no longer believes itself connected.

### SUB-15: A skip run counts back-pressure only, and a mark of "never heard from" is not a tick `[fatal][silent]`
  invariant ∀ session s, ∀ tick T: s's skip run advances only where the ENGINE denied s a frame it had something to put in — K slots full, an
    acknowledgement further behind than the lag bound, a frame over the wire cap, a block the pool would not lend
  invariant [s had nothing to say on T] → s's skip run is unchanged: neither advanced nor reset — a session genuinely behind, whose world then
    goes quiet, is still behind
  invariant the last-heard-from mark distinguishes "never bound" from "bound on tick zero": tick zero is a real tick, and a mark that cannot
    say so exempts from the silence policy every session admitted in a server's first tick
  invariant ∀ bound that names a DURATION — the stall close, the degrade, the lag allowance, the silence bound — the option carries the duration
    and the engine converts it ONCE, at its own tick rate: no policy reads an operator's number as a tick count
  invariant the converted stall bound exceeds the skip run a fully degraded session reaches on its own, (1 << MaxDegradeLevel) - 1, so that
    degrading a session never becomes the reason it is closed
  invariant the degrade bound is DERIVED from the stall bound and is strictly below it, so degradation always gets its turn
  invariant the netId quarantine is sized from the SAME converted stall bound, at the same tick period: two conversions that round differently
    are a SUB-06 hole with nothing in the code to mark it
  never fold "the world was quiet" into the counter SkipPolicy.Evaluate reads: its subject is a client that cannot keep up, and it closes with
    1013, which tells an SDK to back off
  never express a time bound as a tick count in an option: 50 ticks is five seconds at 10 Hz and half a second at 100 Hz, and nothing at the
    reading end can tell which was meant
  scope: SessionSendState.AbandonFrame, SessionSendState.AbandonIdleFrame, SessionSendState.NotePing, SessionSendState.PingStamp,
    SkipPolicy.Evaluate, SkipPolicy.CloseBoundTicks, SkipPolicy.DegradeBoundTicks, SkipPolicy.MinimumCloseTicks,
    SubscriptionsOptions.CloseStalledAfter, SubscriptionsRuntime.NominalTickPeriodUsFor, FrameAssembler.SweepSkipPolicy
  on_violation: every session in a world that goes quiet for fifty ticks — half a second at 100 Hz — is closed, and the reason it is given is
    1013, which tells its SDK to back off as though the server were overloaded. Measured on 2026-09-18: fifty healthy sessions, 1 637 quiet
    ticks, zero real skips, all fifty closed. Its twin is quieter still: a session whose mark reads zero is never closed for silence at all,
    so a client that stops talking is served forever.
  rationale: the two states are indistinguishable from the producer's side — a claimed sequence and no frame — which is how they came to share
    one counter. They are opposite conditions: one is a client that cannot take what it is offered, the other a client that was offered
    everything there was.
  verified: FrameHandoffTests.AnIdleTickCostsASequenceButNotASkip and FrameHandoffTests.ASessionHeardFromOnTickZeroIsDistinguishableFromOneNeverBound
    for the two counters; SendPumpTests.TheStallBoundIsTheSameDurationAtEveryTickRate and SendPumpTests.AStallBoundTooShortForADegradedSessionIsFloored
    for the conversion and its floor; SendPumpTests.DegradationAlwaysPrecedesTheClose across five durations and five tick rates. The end-to-end
    readings are BotSwarmSmokeTests.FiftyBotsSurviveTwoHundredTicks — which passed before the fix only because the sessions it lost were never
    told, which is SUB-14 — and BotSwarmSmokeTests.TheNaturalSkipRunOfHealthySessionsStaysFarBelowTheCloseBound, which measures the headroom
    rather than asserting it from a number, and carries its own guard against measuring an idle server.

---

## Module: Per-Session Frames

### SUB-03: A baseline advances only on a frame that was produced, and every record is absolute `[fatal][silent]`
  invariant ∀ session S, tick T: [a frame for S was PUBLISHED at T] ↔ [S.baseline becomes T]
  invariant ∀ session S, tick T: [no frame was published for S at T] → S.baseline, S.knownSet and S.viewComplete are exactly what they were at T−1
  invariant ∀ record R in a frame: R carries the entity's CURRENT value, never a delta against anything the session may or may not hold
  invariant emit rule: a group g of a known entity is carried iff groupTick(g) > S.baseline; an unknown entity's hit is a full enter; a known entity the
    hits did not reach is a leave
  never advance a baseline, add to a known-set or drop from one before the frame carrying those records is published — the mutation is the LAST step of
    assembling a frame, after the encode and after the hand-off's release
  never encode a delta, a run-length against a previous frame, or a "changed since you last acked" set: a session that missed K frames must converge on
    its next one with no retransmission and no per-session value memory
  never let a skip — no free frame slot, an exhausted frame pool, a frame above the ceiling, a session the tick did not reach — be distinguishable from
    a tick that produced nothing: all of them leave the session untouched and are counted
  scope: FrameAssembler.Assemble, FrameAssembler.Gather, FrameAssembler.Commit, FrameAssembler.NoteSkip, SessionFrameState.Baseline,
    SessionSendState.TryBeginFrame, SessionSendState.AbandonFrame, KnownSet.AddSource, KnownSet.Remove, KnownEntry.LastSentTick
  on_violation: the session diverges PERMANENTLY and in silence. A baseline moved past records that were never sent makes every group stamped at or
    before it invisible to every later frame, so the client renders values the server abandoned ticks ago and no later frame corrects them — there is no
    retransmission to fall back on, because there is no per-session value memory to retransmit from. It is the same failure SUB-02's "suppress publish
    alone" note describes, reached from the session side instead of the tick's.
  rationale: one baseline per session is the whole of a session's value memory (02 § 5), which is what makes 110 identical World sessions cost one
    encode and a memcpy each (SUB-07, AC-1). That only works while records are absolute: a delta would need per-session history, and a session skipped by
    the K-in-flight rule (SUB-04) or by an exhausted pool would have to be repaired rather than simply sent the present.
  note the enter BUDGET defers rather than drops, and a deferred enter is not a skipped record: the entity stays unknown, so the next frame offers it
    again. `VIEW_COMPLETE` is the observable form of "nothing is owed" — neither a deferred enter nor a hit the engine could not describe yet.
  note Phase 2 moves the baseline INTO the known-set as `KnownEntry.LastSentTick` and the emit rule becomes `groupTick > entry.lastSentTick` (02 § 5).
    The rule is unchanged by that: it holds per entry instead of per session, and the per-entity field is already reserved.
  verified: FrameAssemblerTests.ASkippedSessionConvergesOnItsNextFrame — a session is skipped for six ticks across a destroy, two teleports and three
    distinct value changes, by the engine's own skip (its frames are simply never drained, so the K-in-flight rule refuses it), and its next frame is
    required to leave its replica equal to that of a second session on the same profile that was drained every tick — entity set and every numeric field.
    The reference is a real replica rather than a number the fixture computes, so the comparison is "two sessions sent different subsets of the frames
    agree", which is what the rule is about. Falsifiability is proved by FrameAssemblerTests.ABaselineThatAdvancesOnASkippedTickIsDetected, which turns
    on the assembler's own `BaselineAdvancesOnSkipForTest` — the one move this rule forbids, applied to the production path — and requires the verifier's
    assertion to reject it.
    Also DifferentialOracleTests.AClientsWorldIsTheServersAfterSeededChurn, the randomized form of the same claim: 200 ticks of seeded churn — spawns,
    destroys, drifts below the motion tolerance, teleports across the world, writes to both change groups — at delivery rates of 0, 30, 60 and 90 %, with
    the client's decoded world compared field for field against the engine's own state every 50 ticks. The scripted verifier above fixes one order; this
    one covers orders nobody chose, which is where a baseline bug that depends on WHEN the skip fell would hide. Its own falsifiability is
    DifferentialOracleTests.TheOracleDetectsABaselineThatAdvancesOnASkippedTick, the same production mutant, and DifferentialOracleTests asserts a floor on
    how many entity comparisons it actually made — an oracle whose truth walk returned an empty set is green, fast and worthless, and has no other symptom.

---

## Module: Ingress and Frame Hand-off

### SUB-04: Every thread hand-off publishes with release/acquire, on both halves of every pair `[fatal][silent]`
  invariant writer: [every byte of the record written] → [Volatile.Write(head)] — the store that publishes is a RELEASE and nothing else publishes
  invariant reader: [Volatile.Read(head)] → [record bytes read] — the load that observes the publication is an ACQUIRE, taken ONCE per drain
  invariant reader: [record bytes copied out of the ring] → [Volatile.Write(tail)] — space is freed only after it has been read
  invariant writer: [Volatile.Read(tail)] → [the decision that the record fits] → [bytes written]
  never a plain load or store on either cursor: both are cross-thread ordering points, and a relaxed store on arm64 can expose a cursor ahead of the
    bytes it guards
  never one acquire per RECORD in place of one per drain: it costs a fence per command and buys nothing, since a record published mid-drain is simply
    drained next time
  never rely on the DAG's completion barrier for this ordering. The drain runs on a worker the scheduler woke, and EQ-02's warning applies unchanged:
    a track barrier orders the engine's own phases, not a network thread's stores
  invariant outbound producer: [every byte of the frame written] → [slot[seq mod K] filled] → [Volatile.Write(ReadySeq, seq + 1)] — the store that
    publishes is a RELEASE and nothing else publishes
  invariant outbound send loop: [Volatile.Read(ReadySeq)] → [the slot's fields read] → [the frame's bytes read] — the load that observes the publication
    is an ACQUIRE, taken before either
  invariant outbound send loop: [the link no longer needs the bytes] → [Volatile.Write(SentSeq, seq + 1)] — a slot is freed only after it has been sent
  invariant outbound producer: [Volatile.Read(SentSeq)] → [the decision that the slot is free] → [its block handed back] → [the slot overwritten]
  invariant outbound send loop: a frame is sendable only while frame.Tick ≤ Volatile.Read(CommittedTick), which the tick driver releases after the
    flush — the durability gate of SUB-02, read as an acquire on the send side
  invariant outbound: NextSeq − SentSeq ≥ K → the session is SKIPPED for the tick: nothing is encoded and nothing is queued
  invariant outbound: a slot is indexed by seq mod K, never by tick mod K — a skipped tick makes tick parity alias a frame still in flight
  never a plain load or store on ReadySeq, SentSeq, AckedTick or CommittedTick: each is a cross-thread ordering point, and a relaxed store on arm64 can
    expose a counter ahead of the bytes it guards
  never a byte of a frame written after its ReadySeq release, and never a slot overwritten before the SentSeq acquire that freed it
  never a transport thread publish anything to the tick here but a NUMBER written atomically — SentSeq and AckedTick, both on SUB-05's allow-list
  never the tick side wait for a send, and never a send pump wait for the tick: each answers "not now" and moves on
  scope: IngressRing.TryWrite, IngressRing.Drain, IngressRing.IsEmpty, IngressRing.BytesPending, IngressRing.Reset,
    CacheLinePaddedLong, SubscriptionsIngress.Publish, SubscriptionsIngress.DrainChunk, SubscriptionsIngress.OnCommands,
    SessionSendState.TryBeginFrame, SessionSendState.AbandonFrame, SessionSendState.PublishFrame, SessionSendState.TryClaimFrame,
    SessionSendState.CompleteSend, SessionSendState.ReportAppliedTick, SessionSendState.NextSequence, SessionSendState.ReadySequence,
    SessionSendState.SentSequence, SessionSendState.AckedTick, SessionSendState.FramesInFlight, SessionSendState.IsDrained, SessionSendState.K,
    FrameSlot, FrameView, FrameBlock, FramePublicationGate.Publish, FramePublicationGate.CommittedTick,
    FramePool.TryRent, FramePool.TryRentOrKeep, FramePool.Return, NativeFrameMemoryManager.Reset
  on_violation: the drain reads a record's size prefix from a slot whose bytes have not landed, so it frames garbage — a length that runs past the
    published head (caught, and the whole drain stops, losing every later record) or one that does not (not caught, and a client's command is decoded
    from another command's bytes). Both are silent on x64, where the hardware supplies the ordering the code failed to ask for, and both appear on
    arm64 under load, which is the worst possible place to first meet them.
  on_violation: outbound, the same shape with the roles swapped. A frame claimed through a ReadySeq that ran ahead of its bytes is decoded from
    whatever the slot last held — a record count that overruns the frame (caught, and the SDK closes 1007) or one that does not (not caught, and the
    client renders a blend of two ticks with no error on either side). Reusing a slot before the SentSeq acquire is worse: the bytes change under a
    socket that is already writing them, so what arrives is the tail of one frame behind the head of another. Sending past CommittedTick is a third
    failure with a different cost — the session is told a story the WAL does not carry, and a crash leaves the client ahead of the database.
  rationale: this is the only structure in the engine a thread outside the scheduler writes into on a per-message basis, so it is the only one where the
    ordering cannot be inherited from the tick's own barriers. The pairs are named in design/Subscriptions/foundation/05-ingress-rings.md § 4.1 and are
    deliberately the same shape as TraceRecordRing's, which is the engine's existing SPSC precedent.
  rationale: outbound, the same argument reaches the other way — a send pump is a ThreadPool task, not a scheduler worker, so nothing the tick does
    orders its reads either. K = 2 is the smallest window that keeps a link busy (one frame on the socket, one ready behind it) without letting a slow
    client accumulate a backlog, and the counters are what make that window safe with no lock on the tick path. They count FRAMES PAST A STAGE rather
    than naming the last index, so "nothing yet" needs no sentinel and the whole protocol is the arithmetic SentSeq ≤ ReadySeq ≤ NextSeq ≤ SentSeq + K.
    Skipping rather than queueing costs a lagging session nothing permanent, because records are absolute (SUB-03) and its next frame carries everything
    the skipped ones would have. The pairs are named in design/Subscriptions/02-execution.md § 6, whose table this rule is the enforceable form of.
  note the rule covers BOTH directions and each half has its own verifier, because they are one invariant met twice: a byte crosses a thread boundary
    only behind a release, and is read only behind the matching acquire. The inbound half is the ingress ring (a network thread to the tick); the
    outbound half is the frame hand-off (the tick to a send pump).
  note the outbound half's LAYOUT is part of the rule rather than a tuning choice. SessionSendState puts the producer's counters on one cache line and
    the send side's on another, so the two writers never share a line — a structure touched once per session per tick by two cores would otherwise bounce
    that line on every completion and on every ping.
  note the frame POOL is bound by this rule only where it hands memory across the boundary. Its own free lists are guarded by a lock, and the steady
    state does not reach them at all: FramePool.TryRentOrKeep keeps a block that is already the right size class, so a session rents at open and at a
    class change, never per frame.
  note the negative precedent is concrete: the deleted `SendBuffer` used plain `int` cursors and called them "naturally atomic on x64" (verified defect
    E3). Being right about x64 is not being right.
  verified: IngressRingOrderingTests.BytesArePublishedBeforeTheHeadRelease — a two-thread case in which the producer frames records whose every payload
    byte is derived from the record's own sequence number, and the consumer requires each drained record to be internally consistent, so a payload
    observed through a head that ran ahead of its bytes is caught rather than inferred. Falsifiability is proved by
    IngressRingOrderingTests.AHeadPublishedBeforeItsBytesIsDetected, which drives the same checking loop over a buffer that advances its cursor first and
    requires the check to reject it.
  verified: FrameHandoffTests.FrameBytesAreVisibleBeforeTheReadySeqRelease — the OUTBOUND half, proved by exhaustive exploration rather than by a race:
    the harness drives the real SessionSendState through every reachable interleaving of the producer's steps, the send loop's steps and the tick
    driver's, and in every state requires that no byte is read while the producer is still writing it, that no slot is reused while the send side still
    holds it, and that no frame is claimed for a tick the driver has not committed. A two-thread case cannot establish that — on x64 total store order
    hides a missing release — so the threaded complement,
    FrameHandoffTests.AMillionHandoffsUnderRandomizedDelayNeverTearAFrameOrReuseALiveSlot, is what runs in the arm64 nightly.
    Falsifiability is proved by FrameHandoffTests.EachClauseOfTheHandoffIsProvenFalsifiable, which breaks each of the three clauses in turn — the release
    moved in front of the bytes, the completion moved in front of the link's reads, and the committed-tick gate ignored — and requires the verifier's own
    assertion to reject each one.

### SUB-08: A command is drained once, is visible for exactly its tick, and keeps its session's order `[fatal][silent]`
  invariant ∀ command c framed into session s's ring before tick T's Engine-Pre drain: c is visible to systems in T, and in no other tick
  invariant ∀ session s, ∀ c1, c2 of s with c1 framed before c2: [both delivered] → [c1 precedes c2 in Commands<T>() and in ForSession(s)]
  invariant ∀ coalesced type C, ∀ session s, ∀ tick T: at most ONE record of C for s exists in T, and it is the newest s framed before T's drain
  invariant TryGetLatest(s) and ForSession(s) are O(1) — in the tick's command count and in the session table's width alike
  never a command drained twice: the tail is advanced past every record copied out, before the next drain reads the head
  never a session touched by two drain chunks in one tick — the partition over sessions is what per-session order rests on
  never clear the per-session index by walking MaxSessions: it carries the tick it was written in, and an older stamp reads as absent
  never let a drain failure reach the scheduler: the drain is on Engine-Pre, whose failure IS terminal, so a fault is counted and the tick continues
  scope: SubscriptionsIngress.OnCommands, SubscriptionsIngress.BeginTick, SubscriptionsIngress.DrainChunk, SubscriptionsIngress.Publish,
    SubscriptionsIngress.DrainFaults, SubscriptionsIngressExecSystem, SubscriptionsDagBuilder.DeclareSubscriptionsDag, RuntimeSchedule.EnginePreTrack,
    CommandTypeBuffer.Append, CommandTypeBuffer.TryGetSessionRange, CommandTypeBuffers.BeginTick, CommandBatch, CommandAckLog,
    SubscriptionsCommands.Commands, IngressRing.Drain, CommandsMessage.Read
  on_violation: a client's inputs stop being a sequence. Drained twice, a move is applied twice and a purchase charges twice; drained zero times, an
    intent is silently lost and the client's prediction diverges with nothing to reconcile against; out of order, a "stop" lands before the "go" it was
    meant to cancel. None of it throws, and all of it looks like a simulation bug rather than a transport one.
  rationale: order per session is free if a session is drained by exactly one worker and its records stay in that worker's segment, so the partition is
    over SESSIONS rather than over records. Order BETWEEN sessions is deliberately undefined — nothing needs it, and defining it would cost the
    partition. Coalescing overwrites the session's record in place rather than appending and filtering later, which is what makes "only the newest
    survives" true of the batch itself instead of true only of TryGetLatest.
  note Engine-Pre is the placement, not an implementation detail: it runs before the application's track, so a command framed before this tick's drain
    is applied in this tick, and one framed afterwards waits exactly one tick rather than racing the systems that read it.
  note the whole COMMANDS message is validated before any of its commands is framed, by CommandsMessage.Read and by nothing else
    (design/Subscriptions/03-wire-protocol.md § 8, § 10). A second parser here would be a second opinion about the wire.
  note overflow is not a violation of this rule: a record the ring had no room for was never framed, so it was never a command. It is dropped, counted
    on the session, and the producer neither blocks nor throws (EQ-03's shape).
  verified: IngressDrainTests.EachCommandIsVisibleInExactlyOneTickInSessionOrder — a randomized multi-session load over several ticks, requiring every
    command to appear exactly once across every tick observed, and each session's commands to appear in the order that session framed them.
    Falsifiability is proved by IngressDrainTests.ADuplicatedOrReorderedDeliveryIsDetected, which drives the same assertion over an observation log that
    repeats one record and swaps two of a session's, and requires it to reject both.
