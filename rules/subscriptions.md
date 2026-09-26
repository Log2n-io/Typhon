# Subscriptions Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-09-25 |
| Domain | Engine-owned replication: per-entity replication state, its storage, and what bounds its cost |

> Invariants that keep replication cost tied to what changed, keep what a session holds exactly what its geometry names, and keep per-entity
> replication state attached to the entity it describes across every way an entity can move. Replication is push-driven and explicit (ADR-067): the
> watched-set pipeline — the interest stage, known-sets and per-session views — was removed on 2026-09-23, and SUB-17 and SUB-18, which constrained it,
> were retired with it.

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

### SUB-10: What is projected is the push set; whether it changed is decided by comparing quantized values `[fatal][silent]`
  invariant ∀ tick T: the entities projected are exactly the push set — the slots the application pushed with `Replicate` after a write (ADR-067), plus
    the engine's own pushes: a spawn, a destroy, a `WriteSpatial`, a mutable span over the spatial column (its whole cluster), a migration, and a slot
    whose client is still extrapolating it
  invariant ∀ tick T the track runs for, following a tick it did not run for (no session connected, an aborted tick, a failed fence): the push set
    is every live entity — the fence drained the skipped ticks' structure words, so their pushes exist nowhere else
  invariant ∀ pushed entity e: e's projection is recomputed and compared with the copy its entry holds; the comparison, not the push, decides whether
    anything is sent — a redundant push costs an encode and never a byte on the wire
  invariant ∀ projected field f of e: the value compared is f's wire CODE. quantize(read(f)) is computed ONCE per entity per tick, and that
    one code is what is compared, what is encoded into the record, and what is stored for the next tick's comparison
  invariant ∀ change group g of e: [g's stored body differs from the body just encoded] ↔ [g's change tick becomes T]
  never decide that a PUSHED entity changed from a dirty bit, a modified flag or a change set: `ClusterRef.GetSpan` is "the one write path that signals
    nothing" and `ClusterRef.WriteSpatial` does not mark the slot dirty, so those signals do not describe what changed — the push set names who to look
    at, the comparison says what changed
  never quantize a value twice, and never compare DECODED values: a second rounding of the same number is how the bytes that were compared stop
    being the bytes that are sent
  never compare a group body against a stored copy that was not zero-padded to the section's widest form — the padding is what makes a
    fixed-width comparison of variable-length canonical encodings exact
  scope: ProjectionPass.ProjectBlock, ProjectionPass.ApplyHeadingDeadband, ProjectionColumnWalk.Quantize, ProjectionColumnWalk.Walk, ProjectionColumnWalk.WalkRatio,
    ReplicationHotEntry.GroupTicks, ReplicationHotEntry.PackedState, ReplicationColdEntry.PrevQuantizedPosition,
    SubscriptionsCommands.Replicate, PushReplication.PrepareBlocks, PushReplication.MarkPushed, ArchetypeClusterState.NoteStructureSlots,
    ClusterRef.GetSpan, ClusterRef.WriteSpatial
  on_violation: a write the application forgot to push leaves every client holding the old value, with no error on either side — the contract ADR-067
    accepted, narrowed by the push validator rather than closed. The quieter failure is the comparison's: comparing decoded values or quantizing a
    second time makes a record carry bytes that differ from the ones the comparison accepted, so the client's value and the server's stored copy disagree
    by a quantum that never resolves, because the next tick compares against the stored copy and finds it unchanged.
  rationale: the watched-set pipeline compared every watched entity every tick because no write path raised a signal it could trust; its cost was
    per session. Explicit pushes make the work proportional to what the application changed, and keeping the comparison is what makes a push cheap to
    over-issue: a write that lands on the same code is not a change, so an entity jittering inside one position quantum sends nothing.
  note the comparison is the ENCODE. A group's body is encoded from the codes into a scratch and compared byte-for-byte with the stored copy,
    rather than the codes being compared and the body encoded afterwards: the entry has room for the body and not for the codes, and comparing the
    bytes is a comparison of the codes by construction — canonical encodings are minimal, so no two different code sets zero-pad to the same bytes.
  note the POSITION is compared the same way and by the same rule, through the quantized copy in the cold entry, and stamps the motion group's
    tick. What it does not do is fit or emit a motion SEGMENT, which is a separate decision about when a client's extrapolation has drifted far
    enough (design/Subscriptions/02-execution.md § 4).
  note a HEADING's compare is a deadband in code space (09 § 15): before its group is encoded, a code within the declared tolerance of the one the
    client holds — kept per heading in the cold entry, and distance taken modulo the angle's full turn — is replaced by the held one, so the body does
    not change and nothing is sent; past it, the new code is held. The compare is still the encode: the code compared is the code sent. The client's
    heading trails the entity's by at most the tolerance
  note `PushDetection.Automatic` pushes every live entity of an archetype each tick and lets the comparison find the changes. It is experimental and
    refused unless `SubscriptionsOptions.AllowAutomaticPushDetection` is set (ADR-067 decision 3).
  verified: ProjectionPassTests.AWriteThroughGetSpanIsDetected and ProjectionPassTests.AWriteThroughWriteSpatialIsDetected — the two write
    paths that set no signal, each on a marked slot, each requiring the comparison to have found the change and stamped the group tick. Falsifiability is
    proved by ProjectionPassTests.AChangeNoPassEverLooksAtIsNotDetected, which performs both writes and does NOT run the pass, requiring the entry to be
    untouched — so the verifiers discriminate the comparison from the write. The push set's half: PushOracleTests.AForgottenPushLeavesTheClientStaleAndTheOracleSeesIt
    (a write with no push is not sent, and the oracle sees it) and PushOracleTests.AClientsWorldIsTheServersUnderPush in both detection modes. The
    heading note: HeadingTests.AHeadingIsSentOnlyWhenItTurnsPastItsTolerance (1° under 2° sends nothing, 3° sends one record, 0.9° across ±π nothing —
    red without the deadband).

---

## Module: Replication State Sizing

### SUB-13: Per-tick replication work follows the push set, never the archetype `[design]`
  invariant ∀ replicated archetype A: per_tick_work(A) = O(pushed_entities(A))
  invariant ∀ replicated archetype A that no profile observes: replication_memory(A) = 0 — no block, no identity, no event
  invariant ∀ observed archetype A: replication_memory(A) = O(clusters(A)) — every live entity of an observed archetype holds an entry and an identity,
    because what a session holds is geometry and the geometry needs every position (ADR-067, Consequences)
  never any per-cluster replication structure sized by A's chunk-id space, entity count, or segment capacity
  scope: ReplicationDirectory, ReplicationBlockPool, NetIdAllocator, ArchetypeReplicationState, ArchetypeReplicationState.BlockByChunk,
    SubscriptionsOptions.StatePoolBudgetBytes
  on_violation: a database holding a billion entities of a replicated type pays replication work for all of them every tick while a handful change,
    and, where a structure is keyed by chunk id, memory for every id the segment ever handed out.
  rationale: a cluster chunk id is a PERSISTED FILE ADDRESS, not a residency measure. Ids are handed out by the
    segment and freed only when a cluster truly drains — evicting a chunk's pages never releases its id. So a
    flat array indexed by chunk id costs what the DATABASE holds, and no cluster-count threshold fixes that;
    a threshold only moves where it breaks. The engine's own per-cluster side tables (`ClusterAabbs`,
    `ClusterCellMap`, `ClusterSpatialIndexSlot`) are flat arrays of exactly this shape and are the PRECEDENT
    THIS RULE REFUSES — see Typhon issue #960, which tracks that cost on the engine side.
  note the memory half was weakened by ADR-067, deliberately: under the watched set memory followed what was WATCHED; under push it follows the
    observed archetype's population, which is accepted for archetypes clients can see and not for unbounded ones.
  note a DEVIATION, recorded rather than hidden: `ArchetypeReplicationState.BlockByChunk` is a flat array indexed by chunk id, added by the push
    prototype to replace a hash probe per pushed cluster. It is the shape the `never` clause refuses, and it is kept only because an observed archetype
    has a block for every live cluster, so its fill is high; replacing it with the directory's probe is the open item.
  note a flat array remains legitimate for a specific archetype behind a self-justifying size test — take it
    when that archetype's peak cluster count makes the array smaller than the map. It is never the default.
  verified: ReplicationDirectoryTests.SparseChunkIdsCostOnlyWhatIsWatched — the directory's MEMORY shape, and
    NetIdAllocatorTests.ChurnAtAStablePopulationDoesNotGrowTheIdentitySpace for the identity space's half of it.
  verified: ProjectionPassTests.PerTickWorkFollowsThePushedSet — the PER-TICK-WORK invariant. It spawns an archetype across many clusters, marks one
    cluster's entities pushed, and requires the slots the pass addressed to equal the pushed count exactly: the pass either touched a slot or it did not.
    Falsifiability is proved by ProjectionPassTests.AWholeArchetypeWalkIsDetected, which marks the whole archetype and requires the same assertion to
    reject the resulting count.

### SUB-07: No managed allocation in the replication steady state `[perf]`
  invariant once the pushed set has stopped growing, a replication tick allocates no managed memory
  never `new`, LINQ, lambda capture or boxing on the per-hit or per-entity replication path
  scope: ReplicationDirectory, ReplicationBlockPool, NetIdAllocator, ArchetypeReplicationState
  on_violation: replication adds GC pressure proportional to the push set every tick, which shows up as
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
  invariant every identity an entry loses is released once: a vacated or reused slot's by the projection (queued in its lease, released at the next
    tick's BeginProjectTick), an orphan's — a block released with live entries, an entry overwritten by a migration or by the parked drain, a parked entry
    dropped — by ArchetypeReplicationState.ReleaseOrphaned at the second blocks step after it, the same delay. A tick with no block to project still runs
    both releases (FlushIdleTick), so a quiet world does not keep released identities live. At a quiet point the allocator's live count is the leases'
    unspent identities plus one per described entity, and no orphan is refused (OrphanReleaseFaults, an identity already free or orphaned twice)
  scope: NetIdAllocator.Allocate, NetIdAllocator.Release, NetIdAllocator.DrainQuarantine, NetIdAllocator.GenerationOf,
    ArchetypeReplicationState.NetIds, ArchetypeReplicationState.Orphaned, ArchetypeReplicationState.ReleaseOrphaned,
    ArchetypeReplicationState.FlushIdleTick, NetIdLeaseSet.FlushReleases, ReplicationHotEntry.NetId,
    ReplicationHotEntry.Generation
  on_violation: a session that missed the release sees one identity carry a second entity's data and concludes the
    entity moved rather than that it was replaced — the client's world silently disagrees with the server's, with no
    error on either side. The double-release form is worse: two live entities share an identity, so one of them is
    unaddressable for the rest of the session.
  rationale: sessions compare the pair (netId, generation) between consecutive ticks; they do not need it unique for
    the process lifetime, which is why 16 bits suffice. A wrap needs 65 536 reuses of one identity.
  note the generation is bumped on RELEASE, not on the next allocate, so an entity that leaves and is never replaced
    still reads as gone to a session holding the old pair.
  note the window is the SKIP window and not one tick (D1, design/Subscriptions/02-execution.md § 4). One tick keeps a leave and an enter out of the same
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
    NetIdAllocatorTests.AReleasedIdentityIsHeldForTheSkipWindow; the release-once clause at every oracle quiet point (OracleHarness's identity count),
    e.g. PushOracleTests.AClientsWorldIsTheServersUnderPush — red in every detection mode and skip rate when the orphans' release is dropped, the leak
    it fixed (4 to 12 identities per point); ClientRegionObserverTests.AnIdentityReusedWhileASessionMissedFramesResetsIt, the
    push log's side with a quarantine shorter than the log (the harness's one tick): a region session that missed the release and the reissue resets
    rather than carry a leave and an enter of one identity — red, the client keeping the old entity's fields, when the catch-up's refusal is dropped

### SUB-09: State follows its entity, and never survives slot reuse `[fatal][silent]`
  invariant ∀ entity e with a replication entry: the hot/cold entry describing e is reachable from e's CURRENT (cluster, slot)
  invariant ∀ move of such an e from (c1,s1) to (c2,s2): e's entry is written to (c2,s2) and (c1,s1) is CLEARED — the entry exists at exactly one
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
    MaxObservers, FrameBytes, ClientMessageBytes or Controlled
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

## Module: Observers

### SUB-16: A session holds exactly what its geometry names, and a session with no region holds nothing `[fatal][silent]`
  invariant ∀ session s with a Sphere observer, gathered at radius r — its profile's R′ (the band's midpoint (R + L) / 2 with a leave radius, R
    without), or the radius SetRadius gave it within [R′, max] — ∀ tick T after s's fill: s holds entity e iff e's v̂ (SUB-20) lies within r of s's
    committed anchor AND v̂'s cell is in s's delivered window — no entity outside that is held, and none inside it is left out. The distance is 3D;
    its z terms are 0 in a grid one cell deep and for a 2D-position archetype, which lies on the plane z = 0 (the spatial grid's convention), so
    a flat world's known-set is the 2D one bit for bit whichever implementation serves it
  invariant ∀ session s with a World observer: s holds every live entity of its archetypes whose cell the delivery cursor has passed
  invariant ∀ session s with a ClientRegion observer (09 § 7): s holds entity e iff e's v̂ lies in s's committed hull — the convex region it last sent,
    clamped to its profile's maxEdgeM, as half-spaces — AND v̂'s cell is in s's delivered region window. The hull is the anchor: a new one sweeps the
    delivered cells whose classification against the two hulls differs (the crescent), takes back the cells it no longer meets, and delivers the cells
    it newly meets under the enter budget; when more than half the delivered cells fall outside the new hull, the frame resets instead. A session that has
    sent no region holds nothing, and a session whose committed geometry is another shape's (a profile switch between a region and a Sphere or World)
    resets
  invariant nothing is stored per (session, entity): what s holds is recomputed from the anchor, the delivered cells and each entity's last pushed
    position, and every event that moves an anchor, moves an entity or delivers a cell emits exactly the enters and leaves that keep it true
  invariant [s has never been placed] → s holds NOTHING. A default position is a legal world position, so "never placed" and "placed at the origin"
    must be distinguishable, and the unplaced session is the one that sees nothing
  invariant a profile is served through exactly ONE entity observer, World, Sphere or ClientRegion, with at most one Aggregate beside it (09 § 5) —
    which sends AGG, never entity records, so the entity known-set stays that one observer's; a second entity observer, an aggregate alone, a near
    budget on any shape but a ClientRegion, the far tier of Far() (an Aggregate's), a centre (Bind, At, AroundControlled) on any shape but a Sphere, and an
    Aggregate radius beside anything but a Sphere are refused at Start. Profiles may declare different radii, a leave radius and a run-time maximum (09 § 3–4): one window serves them all,
    sized for the largest radius any session can take, and that radius counts against the window bound at Start
  invariant a radius change (SetRadius, or a profile change between Sphere radii) is geometry like an anchor move: the next frame sweeps the shell
    between the two radii and does not reset
  invariant a session's archetype set is its profile's, as plan indices in 256 bits (ArchetypeSet): every plan index the registry admits (at most 255)
    has its own bit, so an event reaches s only if s's profile observes that event's archetype, whatever the number of projections declared
  invariant every observed archetype has a 2D or 3D position — each refused at Start rather than served approximately; a 2D-position archetype is refused in a deep
    grid whose Z range excludes 0, where its plane would lie outside every cell
  invariant the replication grid's cell side is DECLARED (SubscriptionsOptions.ReplicationCellM), never derived: a runtime that observes an archetype
    without one is refused at Start, World-only included; so is a grid past 2²¹ cells on an axis and a window past its bound — the cells a gather
    pays for, W² ≤ 2 809 and W ≤ 15 in a flat grid (⌈R / c⌉ ≤ 5), W³ ≤ 2 809 in a deep one (W ≤ 13, ⌈R / c⌉ ≤ 4) — and a region window past the same
    bound, W = ⌈maxEdgeM / c⌉ + 5 per axis (maxEdgeM ≤ 48 c flat, 9 c deep) — each message naming the setting.
    The grid covers the spatial world's bounds, and a spatial world one cell deep gives a grid one cell deep
  never resolve a declared region shape as though it were another: a profile whose observers have different shapes is a near/far tier, and
    the tiers differ in budget, rate and record kind, so their union is a wrong answer rather than an approximation
  never centre a region somewhere the declaration did not name: a Sphere is centred on the viewpoint Place gives (the default), a fixed point (At), one
    entity (Bind) or the session's controlled entity (AroundControlled) — an entity's position read after the tick's fence, in the same tick. A followed
    entity that is gone keeps its sessions at the last position read (counted, BoundLost); a session following nothing yet is nowhere. Two centres
    declared on one Sphere are refused at Start
  scope: PushReplication.Gather, PushReplication.GatherWorld, PushReplication.GatherRegion, PushReplication.CommitRegion, PushReplication.Commit,
    SubscriptionsRuntime.ConfigureRegions, SubscriptionProfiles.TryGetProfile, SubscriptionProfiles.RadiusOf,
    SubscriptionsCommands.SetRadius, SubscriptionProfiles.SetOf, ArchetypeSet.Contains, SessionTable.ProfileIndex, SessionTable.SetViewpoint,
    SessionTable.TryGetViewpoint, SubscriptionsCommands.Place, SubscriptionsRegistry, ReplicationGrid.Resolve
  on_violation: silent in both directions. A session that holds too much is told about entities it cannot see — bandwidth, and a client that can see
    through the world; one that holds too little has players who never appear. The unplaced case is quieter still: an application that forgot to place
    its sessions would ship a subtly wrong view around the origin instead of an obviously empty one.
  rationale: under the watched set, interest was the one term that multiplied by session count. The geometric known-set removes the per-session state
    that bounded it (ADR-067): what a session holds costs nothing to store and is exact by construction.
  verified: SphereObserverTests.APlacedSessionHoldsTheDiscAroundItAndNothingElse, whose expectation is the arithmetic count of the grid points inside
    the disc rather than a number recorded from a run; SphereObserverTests.TwoSessionsPlacedApartHoldDisjointSets, which discriminates "bounded by the
    radius" from "bounded at all"; SphereObserverTests.AnUnplacedSessionHoldsNothing over a populated origin; SphereObserverTests.AWorldObserverOverTheSameEntitiesHoldsAllOfThem;
    PushOracleTests.WalkingSessionsHoldExactlyWhatTheirDiscNames, where sessions walk and teleport under seeded churn;
    SubscriptionsRegistryTests.ASphereCentredTwoWaysIsRefused, SphereObserverTests.AControlledSphereFollowsItsEntityInTheSameTick, SphereObserverTests.ASessionWhoseEntityIsDestroyedKeepsItsLastViewpoint, SphereObserverTests.BindAndAtCentreTheSphereWhereTheyName,
    PushOracleTests.TwoProfilesAndABandEachHoldTheirOwnDisc, ReplicationGridTests.AProfileWhoseRunTimeMaximumPassesTheWindowIsRefusedAtStart,
    SubscriptionsRegistryTests.AProfileWithTwoObserversIsRefused; ReplicationGridTests.AnUndeclaredCellSideIsRefused,
    ReplicationGridTests.ARuntimeThatObservesAnArchetypeWithoutACellSideRefusesToStart, ReplicationGridTests.AGridWiderThanTheCellKeyIsRefused,
    ReplicationGridTests.AWindowPastSixteenCellsIsRefusedAndTheMessageNamesTheSmallestSide, ReplicationGridTests.ADeepWindowPastThirteenCellsIsRefused,
    ReplicationGridTests.ATwoDimensionalArchetypeIsRefusedInADeepGridWhoseZRangeExcludesZero; PushOracle3DTests.AClientsWorldIsItsSphereInADeepGrid,
    the same oracle in 3D with 2D walkers on z = 0 beside 3D flyers; PushOracle3DTests.AThirdAxisChangesNothingInAFlatGrid and
    AThirdAxisChangesNothingInAFlatGridServedDeep, the degeneracy clause; PushFrameDigestTests, every flat run served by both implementations;
    ArchetypeSetTests.AnArchetypePastPlanIndexSixtyThreeReachesOnlyTheSessionsThatObserveIt, red under a 64-bit set (plan index 70 aliases 6),
    ArchetypeSetTests.AProfileObservingMoreThanSixtyFourArchetypesServesEveryOne, SubscriptionsRegistryTests.The256thArchetype_IsRefused;
    the ClientRegion clause: PushRegionTests.EachClientHoldsItsRegionUnderChurnAndMoves (regions panned, reshaped, clamped and jumped under churn and
    skipped frames, flat and served deep, against true positions and by the shadow oracle on v̂ — dropping the crescent sweep or the old hull's events
    turns every case red), ClientRegionObserverTests.ARegionHoldsExactlyTheGridPointsInItsHull (the arithmetic count, and a move swept without a reset),
    PushRegionTests.ASessionThatSentNoRegionHoldsNothing, PushRegionTests.ARegionWiderThanTheWindowBoundIsRefusedAtStart,
    SubscriptionsRegistryTests.WhatAShapeDoesNotHaveIsRefusedAtStart; in 3D, PushOracle3DTests.AClientsWorldIsItsPolyhedronInADeepGrid (frustums over
    3D flyers and 2D walkers in a volumetric world, skip 0–90 %, face by face and by the shadow oracle — red when the per-entity test or the cell
    classification drops z); ClientRegionObserverTests.ASwitchBetweenARegionAndASphereResets, both ways.

### SUB-20: Every geometric test reads v̂, and v̂ moves only by its rule `[fatal][silent]`
  invariant ∀ observed moving archetype A with slack h_A (09 § 2): each entity e keeps a visibility position v̂ₑ; every geometric test — the push
    step, the crescent sweep, the cell delivery, the log catch-up, the occupancy and the shadow check — reads v̂ₑ, never the true position
  invariant v̂ₑ := pₑ (this tick's decoded quantized position) at initialization, on a teleport (the motion epoch changed), and when |pₑ − v̂ₑ| > h_A;
    otherwise v̂ₑ is unchanged. h_A = 0 is exact: v̂ is the last projected position, and no byte is added to the cold entry
  invariant h_A ≤ the slack of every Sphere profile observing A — its half band (L − R) / 2, or R / 48 without one (Q1) — and ≤ half a cell, so each
    profile's bound holds: e is held when its true position is within R′ − h_A of the anchor, and not held past R′ + h_A
  invariant every cluster proof widens by h_A: a cell's cluster query pads by 1 m + h_A and a pruning test by 1 cm + the position quantum + h_A,
    because a cluster's box bounds true positions and v̂, quantized, lies up to h_A from them; a margin as wide as a radius proves nothing inside it
  invariant every move of v̂ makes an event; a mover whose v̂ stays and whose segment and groups did not change makes none
  scope: ProjectionPass, SubscriptionsRuntime, ReplicationBlockLayout.VisibilityPositionOffsetInColdEntry, PushReplication.AddEvent
  on_violation: silent. A test that read the true position would disagree with the events the others were fed, and an entity would be entered or left
    twice or never; a proof that did not widen would skip a cluster holding an entity whose v̂ lies in the cell, which is then never delivered.
  rationale: the observer already has a slack (the anchor); the entity side had none, so every mover was an event every tick (09 § 2, B10).
  verified: PushOracleTests.SteadyWalkersAreHeldWithinTheSlackOfTheirDisc at h ∈ {0, R / 48, 8 m} and skip rates 0 and 60 %, with every creature
    walking 1.5 m a tick; its event count per walker-tick falls with h.

---

## Module: Session Lifecycle

### SUB-14: A session the tick closes is told so before its link is closed `[fatal][silent]`
  invariant ∀ session s, ∀ close the TICK decided (silence, skip run, an unpublished tick, the application's Kick verb):
    a KICK carrying the close code reaches s's link, and the link is closed after it — KICK, then close (03 § 3; on TCP, KICK then FIN)
  invariant the KICK is sent by s's own send pump, never by the tick thread: the transport's one promise is at most one send in flight per
    session, and a tick writing to a socket breaks both that and the rule that the tick does no I/O
  invariant a protocol refusal of an OPEN session (a malformed message, 1002/1007/1009) is sent by that pump too, after what it already owes, and the pump
    closes the link after it; the connection leaves the link bound for it. Only before the session is open (a refused HELLO) does the receive thread,
    then the link's only writer, send the KICK itself. The pending KICK carries the link it was decided for, so a slot recycled before the pump runs
    neither strands it nor redirects it, and a pump that ends on an exception lowers its flag rather than leaving the session unable to send
  invariant [the client itself started the close] → no KICK: the connection unbinds its link before asking the tick to close, and an absent
    link is what tells the pump there is nobody to inform
  invariant ∀ SDK: the close code it reports is the KICK's when one arrived, and the transport's only otherwise — TCP carries no code, so a
    FIN alone says 1001 for every reason there is
  never mark a row Closing and queue its Closed event as the whole of a close: the row is the engine's bookkeeping, and the client shares
    none of it
  never leave a transport bound after a terminal close: a socket whose peer has sent FIN still reports itself connected until the next write
    fails, so an SDK that keeps it answers "connected" for a session that ended
  scope: SessionTable.CloseCore, SubscriptionsIngress.BeginTick, SendPump.RequestKick, SendPump.TryKickAsync, SubscriptionConnection.Kick,
    FrameAssembler.SweepSkipPolicy, SubscriptionConnection.CloseWithKick, SendPump.Claimable, SendPump.PumpAsync
  on_violation: the worst state a client can be in. Everything it can observe says it is connected — the socket is open, no code arrived, no
    error was raised — and no frame will ever come again. It cannot even reconnect, because nothing told it to. It is silent on the server
    too: the close counters move, the session leaves the table, and an application that kicked a player is told it worked.
  rationale: 02 § 6 and 05 § 1 already specify the message and the SDK's response to it; what was missing was any caller. The pump is the
    right sender because it is already the single writer for the slot, so the KICK is simply the last message it sends.
  verified: ClientInputFuzzTests.AnOpenSessionsRefusalIsSentByItsPumpAfterWhatItOwes (the refusal after a PONG, KICK last, no overlapped send), and
    the fuzzer's every close checked for a KICK first; BotSwarmSmokeTests.ASilentSessionIsClosedAndItsClientIsTold — a client that never pings, asserting in one place that its frames
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

### SUB-03: What a session holds advances only with a published frame, and a skipped session receives the union `[fatal][silent]`
  invariant ∀ session S, tick T: [a frame for S was PUBLISHED at T] ↔ [S's committed anchor and delivered cells become the ones that frame described]
  invariant ∀ session S, tick T: [no frame was published for S at T because it was DENIED one — slots full, lag, oversize, pool] → S's committed
    anchor, delivered cells and view-complete latch are exactly what they were at T−1
  invariant [S's frame at T would have carried nothing — no record, no reset, no stats, no newly complete view] → the frame is abandoned and S's pending
    geometry IS committed: an anchor move or a delivery of empty cells changes nothing the client holds, so there is nothing a later frame could owe
  invariant ∀ record R in a frame: R carries the entity's CURRENT value, never a delta against anything the session may or may not hold
  invariant skip = union: a session's next frame after missed ticks folds every event of the missed ticks from the push log — an entity's first old
    position, its last new one, and the union of what changed — while every missed tick is still in the log; otherwise the frame is a RESET that
    re-delivers the view cell by cell
  invariant a session's pending owner mask (SUB-11), the lastSeq its last SELF carried and the tick its acknowledgements were sent up to advance only
    with a published frame, like its geometry: a skipped frame leaves all three as they were, so its next frame carries the union of the owner groups
    changed meanwhile and every rejection the acknowledgement history still holds. They record WHICH groups and WHICH ticks, never values — the values
    are read, current, from the owner entry when SELF is written — so they are not the per-session value memory the next line forbids
  never commit a session's geometry before the frame describing it is published — the commit is the LAST step of assembling a frame, after the encode and
    after the hand-off's release
  never encode a delta, a run-length against a previous frame, or a "changed since you last acked" set: a session that missed K frames must converge on
    its next one with no retransmission and no per-session value memory
  never let a skip — no free frame slot, an exhausted frame pool, a frame above the ceiling, a lagging acknowledgement — be distinguishable from a tick
    that produced nothing: all of them leave the session untouched and are counted
  scope: FrameAssembler.NoteSkip, PushReplication.Commit, PushReplication.NoteNotPublished, PushReplication.CollectLog, PushReplication.EmitLog,
    SessionFrameState.PendingReset, SessionSendState.TryBeginFrame, SessionSendState.AbandonFrame, SessionSendState.AbandonIdleFrame,
    FrameAssembler.CommitSelf, AckHistory.Collect
  on_violation: the session diverges PERMANENTLY and in silence. Geometry committed for a frame that was never sent makes the session believe its client
    holds entities it was never told about, so no enter is ever sent for them, and leaves are sent for entities the client never had. There is no
    retransmission to fall back on, because there is no per-session value memory to retransmit from. It is the same failure SUB-02's "suppress publish
    alone" note describes, reached from the session side instead of the tick's.
  rationale: records are absolute and an entity's group stamps say what changed and when, so a session that missed ticks needs only the events of those
    ticks — which the push log keeps for LogDepth ticks — to be told the present. A delta would need per-session history, which push exists not to keep.
  note the enter BUDGET defers rather than drops: a cell the budget did not reach stays undelivered, so its entities are offered again on the next frame.
    `VIEW_COMPLETE` is the observable form of "every cell the geometry names is delivered".
  verified: FrameAssemblerTests.ASkippedSessionConvergesOnItsNextFrame — a session is skipped for six ticks across a destroy, two teleports and three
    distinct value changes, by the engine's own skip (its frames are simply never drained, so the K-in-flight rule refuses it), and its next frame is
    required to be a log catch-up that leaves its replica equal to that of a second session drained every tick — entity set and every numeric field.
    Also PushOracleTests.AClientsWorldIsTheServersUnderPush at delivery rates of 0, 30, 60 and 90 %, PushOracleTests.AProfileServedEveryFewTicksConverges
    (rate classes replay the log by design) and PushOracleTests.AFarFlushMetFirstAsASecondaryReachesACaughtUpSession (a far flush carried by catch-up).
    Falsifiability is proved by FrameAssemblerTests.ASessionCommittedOnASkippedTickIsDetected, which turns on the push path's own
    `CommitOnSkipForTest` — the one move this rule forbids, applied to the production path — and requires the verifier's assertion to reject it.
    The owner-state clause: SelfBlockTests.OwnerGroupsChangedWhileSkippedArriveAsTheirUnion (groups changed on skipped ticks arrive together, current;
    red when a skip clears the pending mask) and SelfBlockTests.LastSeqAndRejectionsReachTheClient (a rejection on a skipped tick still arrives).

### SUB-11: Owner state reaches its controlling session only, every group after a Control change, and the union after a skip `[fatal][silent]`
  invariant ∀ session s controlling entity e (Control): s's next published frame after a Control change, a RESET or its first frame naming e carries a
    SELF with every owner group of e; otherwise a SELF carries exactly the owner groups of e that changed since s's last published frame, read current
    from e's owner entry — and a frame with no changed group and no new lastSeq carries no SELF
  invariant owner values reach no session that does not control e, and never travel in ENTITIES: the owner section has its own bit space (03 W17), and
    SELF is the only block whose writer reads the owner entry — the projection compares it and migration copies it, neither sends it
  invariant a Control change marks every owner group pending, whatever the next frame finds: a flip A → B → A between two published frames sends A's every
    group, since A's changes during the B ticks were routed to nobody
  invariant ∀ session s controlling nothing, or whose entity replication cannot locate: a SELF, when one is due, names netId 0 (W17′) — an acknowledgement
    only — and s is told once, with netId 0, that the entity its last SELF named is no longer its
  invariant an owner change is routed to its sessions by the projection (SelfTracker.Notice) through a reverse Control map rebuilt only when a controlled
    entity changed (SessionTable.ControlVersion): the per-tick cost follows owner changes, not sessions (SUB-13)
  note a change needs a push like any replicated write (ADR-067): the projection compares owner groups only for pushed entities
  scope: SelfTracker.Notice, SelfTracker.Refresh, SelfTracker.Clear, FrameAssembler.PrepareSelf, FrameAssembler.WriteSelf, FrameAssembler.CommitSelf,
    EntitiesEncoder.WriteSelf, SessionTable.SetControlled, SessionTable.ControlVersion, ProjectionPass.ProjectBlock
  on_violation: silent either way. A missing group leaves a client showing an empty wallet after possessing a character until the value happens to
    change; a group sent to the wrong session shows one player another's private state — credits, inventory, cooldowns.
  rationale: private state belongs to one entity and one controller; routing its changes by push keeps SELF's cost with the changes, and the pending mask
    gives a skipped session the union the push log gives its entities.
  verified: SelfBlockTests.APrivateFieldReachesItsOwnerAndNoOneElse (every group first, then only the changed one; the session beside it never sees a
    SELF), SelfBlockTests.AControlChangeSendsEveryOwnerGroupOnceAndAReleaseSaysSo (every group of the new entity once though none changes, then netId 0
    once on release), SelfBlockTests.OwnerGroupsChangedWhileSkippedArriveAsTheirUnion, SelfBlockTests.AControlFlipBetweenTwoFramesStillSendsTheOwnerGroups
    (red when the frame only compares entities), SelfBlockTests.TwoSessionsControllingOneEntityBothReceiveItsChanges (the reverse map's chain),
    SelfBlockTests.ADestroyedControlledEntityIsReportedGoneOnce, SelfBlockTests.AResetResendsEveryOwnerGroup,
    SelfBlockTests.AControlledEntityOutsideTheSessionsGeometryStillSendsItsOwnerState.

### SUB-26: A command's entity reference resolves only to what its session holds `[fatal][silent]`
  invariant TryResolve(s, netId) = e ⟺ netId is bound to e (the projection binds each identity it assigns and every release unbinds it), e is live in
    the EntityMap and its replica still carries netId, AND (e is s's controlled entity OR s's COMMITTED geometry holds e: e's archetype is in the set of
    the profile s's last published frame was built against — not a profile switched to since — and its v̂ passes SUB-16's test against that frame's
    anchor, radius and delivered cells, or its committed hull and window, or its World cursor)
  invariant an entity a client learned of only from an event (an attacker beyond its view) does not resolve: events inform, they do not grant reach
  note the committed geometry is the last PUBLISHED frame's, which the client may not have applied yet: an entity that left in a frame still in flight is
    refused while the client may still draw it. Judging each command against the frame the client last applied (COMMANDS.clientTick) would need per-session
    geometry history, which push does not keep; a game treats the refusal as "out of reach". Likewise a session served every few ticks (a rate class,
    overload) is judged against its last published frame while v̂ is this tick's: near the edge the two differ by those ticks' motion
  note TryResolveAny keeps the liveness-only answer — bound, live, still that identity — for tools that are not clients (an admin console), under a name
    that says so
  scope: SubscriptionsCommands.TryResolve, SubscriptionsCommands.TryResolveAny, FrameAssembler.Holds, PushReplication.HoldsCommitted,
    PushReplication.TryVisibilityAt, FrameAssembler.IsLive, SessionFrameState.CommittedProfile, NetIdEntityIndex.Bind, NetIdEntityIndex.Unbind,
    NetIdEntityIndex.Reserve
  on_violation: silent. A client that names a netId it guessed — or remembered from an event — targets, inspects or trades with an entity behind a wall or
    across the map: an information leak and a cheat, and no error anywhere.
  rationale: what a session holds is geometric (SUB-16) and costs nothing to store; the same test, on the committed state, answers "was it shown" with no
    per-session memory.
  verified: TryResolveTests.TryResolveAcceptsExactlyWhatTheClientHolds (for a Sphere, a World and a ClientRegion session, TryResolve over every entity
    equals the replica the session's frames built — red when the geometric test is skipped), TryResolveTests.WhatResolvesFollowsTheViewTheClientWasSent,
    TryResolveTests.APartialWorldFillAndAnUnobservedArchetypeAreRefused (red when a World session holds everything, and for an archetype outside the
    set), TryResolveTests.AProfileSwitchResolvesTheOldViewUntilItsFrameIsPublished (red when the current profile is judged),
    TryResolveTests.ADestroyedEntityIsRefusedBeforeItsIdentityIsReleased, TryResolveTests.TheControlledEntityResolvesAnywhereAndTryResolveAnyIgnoresTheView.

### SUB-27: A command the server refuses is acknowledged, and a session refused for long enough is closed `[fatal][silent]`
  invariant every command a well-formed COMMANDS message carries is either framed into the tick or answered with an ACK — over the session's inbound
    budget (the whole message: every command RATE_LIMITED), over its type's rate (RATE_LIMITED), a role that may not send it (FORBIDDEN), the
    declaration's pre-check (REJECTED), a region the hull builder refuses (REGION_INVALID) — and its seq settles lastSeq either way
  invariant a session places at most RefusalAcksPerTick transport-side refusals in a tick's shared ACK log, and the log holds that share for every
    session up to 2 048 (CommandTypeBuffers.AckCapacity): past its share a refusal settles through lastSeq alone and is counted (RefusalAcksCapped),
    so no session's refusals crowd out another's; nothing else is unanswered but what the session's own ring overflowed, which is counted too
  invariant the inbound budget (SubscriptionsOptions.IngressBytesPerSecond) is required whenever the catalog has commands, and at least
    ClientMessageBytes — both refused at Start; every open-session message is charged, only a COMMANDS message is refused (a PING, a BYE and a
    protocol error are never delayed), and the bucket is exact (bytes × clock ticks, no fraction lost)
  invariant a session whose refused commands — over budget, per command, plus the ingress's rate and role refusals — pass AbuseRefusalsPerWindow in
    AbuseWindows adjacent windows is closed with 1008, its KICK sent by its pump (SUB-14); a single abusive window, a gap of two windows or more,
    a window at the threshold, or a client that stopped sending, is not; a pre-check or region refusal is a game outcome and is not counted
  invariant the abuse rule is always on: AbuseWindow in (0, 1 h], AbuseRefusalsPerWindow and AbuseWindows positive, or Start refuses
  scope: SubscriptionConnection.HandleOpen, SubscriptionConnection.CheckAbuse, SubscriptionsIngress.RefuseCommands, SubscriptionsIngress.Publish,
    SubscriptionsIngress.RefusalAcksPerTick, IngressCommandSink.Refuse, CommandTypeBuffers.AckCapacity, IngressPolicy,
    SubscriptionsRuntime.ValidateIngressRails, SubscriptionsOptions.IngressBytesPerSecond
  on_violation: silent. A command dropped with no answer leaves a predicting client waiting on it forever, or replaying it into a divergence; one
    session's refusals filling the shared log silently drop everyone else's; a client refused forever keeps a transport thread decoding for nothing,
    and one flooding without a budget has no bound at all.
  verified: IngressHardeningTests.AMessageOverTheBudgetIsRefusedWholeAndAcknowledged (every refused command acknowledged, lastSeq settling all, the
    session beside it untouched), IngressHardeningTests.RoleAndPrecheckRefusalsAreAcknowledged, IngressHardeningTests
    .OneSessionsRefusalsCannotCrowdOutAnothersAcknowledgements, IngressHardeningTests.SustainedRefusalsCloseTheSessionWith1008 (closed at the third
    window, not before; a single abusive window then calm stays open), IngressHardeningTests.RailsThatWouldNotHoldAreRefusedAtStart,
    IngressRailsTests (the test's clock: EverythingIsChargedAndOnlyCommandsAreRefused, ARefillInFractionalStepsLosesNothing,
    OverBudgetAByeAndAProtocolErrorStillClose, AbuseCountsCommandsFromBothSidesAndClosesAfterTheRun, AGapOrACalmWindowBreaksTheRun);
    SelfBlockTests.LastSeqAndRejectionsReachTheClient for the rate and application paths.


### SUB-30: A realm-framed value is encoded and decoded with exactly one realm's frame `[fatal][silent]`
  invariant a position (pos2/pos3: ENTITIES records, event and command fields, a region's vertices) and an AGG cell index are quantized over the
    frame of the realm they belong to — its bounds, its width (REALM.posBits), its replication cell — never over the catalog, which carries the
    codec's kind alone (typhon.3, 12-realms § 5.1); realm 0's frame is built in the same computation as its replication's, so its bytes are the
    single-realm ones
  invariant a session receives its frame before any value framed by it: its first published frame is a RESET whose first block is REALM, retried until
    one is published; a REALM travels only as the first block of a RESET frame; a decoder refuses a positioned ENTITIES, an AGG or a position field
    while it holds no realm (1002), and a REALM elsewhere or out of range (1007)
  invariant a command's realm-framed field travels at RealmFrame.CommandPositionBits (32) over the realm's bounds, so the transport parses it without
    reading the session's realm (SUB-05); until sessions are placed in realms it decodes with the served realm's frame
  invariant a velocity is realm-independent: its unit is 2^unitExp metres per tick, derived from the archetype's tolerance and MaxAge (W5)
  scope: RealmFrame, RealmCodecs, PositionFrame, ProjectionPass.QuantizePosition, ProjectionCompiler.VelocityCodec, SessionFrameState.RealmSent,
    EntitiesEncoder.WriteHeader, SubscriptionsRuntime.BuildRealm0Frame, TickReader.Read, FieldCodec.ReadNumber, CommandsMessage.Read
  on_violation: silent. A value encoded over one frame and decoded over another is a wrong position a client renders without error; a catalog that
    carried bounds could describe one realm only, and every other realm's positions would decode into it.
  verified: RealmReplicationTests.RealmFramedQuantizationEqualsTheCatalogCodecForRealmZero (realm 0's frame is the codec's; another realm's frame
    gives other codes for the same place), FrameAssemblerTests.AProfileSwitchResetsTheView (a new session's first frame is a RESET),
    EntitiesEncodingTests.TheProducedBytesDecodeIntoTheClientsReplica (the replica decodes the engine's stream over the REALM it was sent);
    protocol goldens tick-realm, tick-realm-none and wire-refusals (realm-without-reset, realm-not-first, positioned-entities-without-realm …),
    decoded bit for bit by both SDKs.

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
    ordering cannot be inherited from the tick's own barriers. The pairs are named in archive/Subscriptions/foundation/05-ingress-rings.md § 4.1 and are
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

## Module: Push Replication (ADR-067)

### SUB-19: A push entity's slot stamp is the tick of its last event, and every change makes one `[fatal][silent]`
  invariant ∀ push archetype, ∀ live slot s: cold LastEventTick(s) = the tick of the latest PushEvent recorded for the entity in s
  invariant ∀ change to an entity's projected state or motion in tick T: a PushEvent for it is recorded in T, and it names the block and slot the
    entity is in at the end of T — an arrival by migration included, even when no byte changed
  invariant distance bands (09 § 9) are declared per Sphere profile — at most three, periods N ∈ {2, 4, 8} growing outward — and folded once: at the
    smallest declared period's phase ticks, over the largest's window. For a session holding an entity in band b before and not nearer after, the entity's
    update is sent only at its flush tick for b, p with ((netId mod N_b + p mod N_b) mod N_b = 0), and carries every group whose stamp is in (p − N_b, p]
  invariant an entity a session's anchor or radius brings inward across any band's boundary, with no event since the session's last frame, gets every
    group stamped in its old band's last N ticks before that frame, gap included (the inner crescent); a catch-up's flush-only replay never speaks for
    such an entity. A window's floor is max(p − N, 0)
  never write a push slot's LastEventTick anywhere but PushReplication.AddEvent — not for a slot the change gate skipped, not on a dormant path
  never drop an arrival's event as a byte-identical no-op
  scope: PushReplication.AddEvent, PushReplication.FoldFarChunk, PushReplication.FarSweepCell, PushReplication.CollectLog, PushReplication.ConfigureFar,
    BandBuilder.Every, ProjectionPass.ProjectBlock,
    ReplicationBlockLayout.LastEventTickOffsetInColdEntry, PushEvent.Arrived, PushEvent.FarFlush
  on_violation: the distance LOD's far-flush fold picks an entity's latest event by that stamp, and the sweep, the cell delivery and the log's catch-up
    read it as "the push step owns this entity from that tick on". A stamp moved without an event makes the fold skip the entity, so a change a far
    session was never sent is never flushed; a stamp that names a slot the entity left makes a flush point at the wrong entity. Both are silent: the
    client simply keeps a stale value until the entity changes the same group again.
  rationale: an explicit push model keeps no per-session record of what a client was told, so "what changed since" has to be recoverable from the
    entity itself — its group stamps say what, the log's events say where, and this stamp ties the two to one event.
  note the field was the watched-set pipeline's "watched last tick"; that pipeline is gone, and the field now means only this.
  verified: PushOracleTests.ADeferredFarChangeReachesASessionThatWalksCloser, PushOracleTests.DeferredFarUpdatesStillConverge,
    PushOracleTests.FarFlushesConvergeThroughCatchUpWithSmallCells, PushOracleTests.AFarFlushMetFirstAsASecondaryReachesACaughtUpSession,
    PushOracleTests.ThreeBandsConvergeAtEverySkipRate, PushOracleTests.AChangeWithheldBeforeASkippedFlushReachesASessionThatCameCloser,
    PushOracleTests.AChangeInTheFirstTicksReachesAFarSession, SubscriptionsRegistryTests.ABadBandIsRefusedWhereItIsDeclared — oracle runs with the
    shadow legality check on. Falsifiability: removing the far-flush fold's
    in-place flag, its flush entries or the inner-crescent sweep each turns ADeferredFarChangeReachesASessionThatWalksCloser red; its nested-bands case
    goes red when the inner crescent serves only entities that become near. The inner crescent's window reaching back past a catch-up's gap, and a
    flush window's floor wrap-safe in the first ticks, each turn their scripted test red when removed.

### SUB-21: An event reaches exactly the sessions its route names, once, in emission order `[fatal][silent]`
  invariant an event emitted in tick T (SubscriptionsCommands.Emit) is encoded once, after T's projection, into T's event log slot; the bytes are the
    catalog plan's field encoding (FieldCodec), identical for every session, and an EntityId field is the entity's netId, 0 when it has none
  invariant a session's frame carries the events routed to it in every tick after its last committed frame (the current tick alone before its first),
    each once, in emission order (worker slot, then call order); Broadcast routes to every session, ToOwner to the session whose Control is the named
    entity when the frame is built, ToSession to the session EmitTo names; Near and ToKnown to every session that sees the event's point, or a named
    entity's v̂ — its v̂ before this tick too, when this tick moved it to another cell (was ∨ is) — against its committed or pending geometry (inside the sphere with the point's cell delivered — SUB-16's test; a World session: the cell
    delivered), and Near with a radius only within it of the session's viewpoint
  invariant an entity destroyed in the event's tick still resolves, from the projection's release, to the netId and v̂ it had
  invariant an event of a tick the log no longer holds is counted, not sent: the frame opens its EVENTS block with the built-in EventsLost (index 0)
    carrying that count over the last EventHub.SummaryDepth ticks — a lower bound beyond. Broadcast and ToSession are counted exactly; ToOwner against
    the session's Control, and Near and ToKnown against its geometry, when the frame is built (a session that moved or changed Control during the gap is
    counted as it is now). Events take at most half a frame: past it, they are counted rather than sent
  invariant a session bound to no profile has no view: Broadcast and ToSession reach it in frames of events alone, caught up from the log like any
    session's; the other routes never do. Emissions of a tick no frame stage encoded (no session, no push replication, an aborted tick)
    are discarded at the next tick's start, not delivered later
  invariant a value its codec cannot carry drops that one event, counted in EventHub.Rejected; the tick path does not throw
  never keep per-session event state: the log and its summary are per tick
  scope: EventHub, EventHub.Emit, EventHub.EmitTo, EventHub.EncodeTick, EventHub.Collect, EventHub.Write, SubscriptionsCommands.Emit,
    SubscriptionsCommands.EmitTo, BuiltInEvents, PushReplication.SeesPoint, PushReplication.WorldSeesPoint, NetIdLeaseSet.Depart, FrameAssembler.AssemblePush
  on_violation: a client acts on an event that was not its own, twice, out of order, or never learns that it missed one — silently.
  rationale: events are best effort but never ambiguous: the log makes a skipped session's union exact while the log lasts, and the loss count says
    how much it missed after.
  verified: EventDeliveryTests.EveryEventReachesExactlyItsSessionsAtEverySkipRate (random broadcast and owner events, skip 0–90 %: once each, in order,
    received + lost = routed; Near and ToKnown included), EventDeliveryTests.AnEventReachesItsSessionsWithItsValues,
    EventDeliveryTests.EveryCodecShapeRoundTripsAndABadValueDropsOnlyItsEvent, EventDeliveryTests.GeometricRoutesDedupeRespectTheirRadiusAndNameTheDestroyed,
    EventDeliveryTests.WorldSessionsLateSessionsAndWorkerOrder, EventDeliveryTests.AKnownEventReachesASessionTheEntityLeftThisTick,
    EventDeliveryTests.ASessionWithNoProfileHearsBroadcastsAndItsOwnEvents. Falsifiability: filing only the current v̂ turns the leaving-entity case red; a frame that carries only its own tick's events turns the
    30–90 % cases red; a loss count of 0 turns the 60 and 90 % cases red; dropping the dedupe turns the oracle and the dedupe case red.

### SUB-22: A session's LOD level defers updates and never loses one `[fatal][silent]`
  invariant a session's LOD level ℓ ∈ 0..3 commits with its frame (SUB-03); the frame is gathered with the committed level's bands before (what every
    held-back change was scheduled by) and the target level's after. At ℓ every declared band's period is min(N · 2^ℓ, 8) at the same boundary, and a
    profile with none has one beyond R′/2, every 2^ℓ ticks; the enter budget is max(1, (EnterBudgetPerFrame >> target level) / tick multiplier). A
    World session has no level: its frames return it to 0
  invariant overload: while the tick multiplier is above 1, every Sphere session's target level is its budget level + 1 (at most 3) and every profile
    is served at min(2 · divisor, 4); both follow the multiplier the tick started with and fall back with it
  invariant the radius shrinks only at ℓ = 3 and still over the budget, a sixteenth of the session's own radius per second, never below half of it;
    under the lower mark it grows back a step per 3 s before any level falls, when the rate scaled to the grown disc (ball) stays under the mark. A shrink is a radius
    change (a shell sweep of leaves) and commits with the frame
  invariant after a level falls from ℓ to ℓ′ < ℓ, for LogDepth ticks a band's flush carries every group stamped in its period at ℓ, not ℓ′: the first
    flush on the shorter schedule reaches back to the last one on the longer
  invariant the fold's phase divides every period in use and its window covers every flush's history: while any committed level is above zero, the
    phase is at most 2^(lowest such level) and the window is min(LogDepth, max(declared longest period, 1) · 2^(highest such level)); while no level is, but one fell in the last LogDepth ticks and a band is still
    in use (phase > 1), the window is LogDepth. The census may count too many sessions — a closed one, until the recount every RecountEvery ticks — but
    never too few
  invariant the budget loop raises the level after the bytes/s EWMA has been over the session's budget for 1 s, lowers it after 3 s under 0.7 of it,
    and returns a session with no budget to level 0. It reads the link: a frame published counts its bytes and a frame with nothing to say counts 0,
    over the live tick period; a frame refused (degraded, lagging, no slot, oversize, no pool) is not fed — congestion is not quiet
  never drop a record for a budget: a deferred change reaches the session at the entity's next flush for its committed level, or by the inner crescent;
    the only eviction is the bounded last-resort radius
  scope: PushReplication.Pace, PushReplication.ResolveFar, PushReplication.CommitLevel, PushReplication.RecountLevels, LodBands.AtLevel,
    FrameAssembler.ExecutePushSessions
  on_violation: a flush that does not reach back far enough after a fall, or a fold phase that misses a session's flush ticks, leaves a client with a
    stale group until the entity changes it again — silently.
  rationale: push keeps no per-(session, entity) record of what was sent; a level is only a change of schedule, so everything it withholds must be
    recoverable from the entity's group stamps and the log, as the declared bands' are (SUB-19).
  verified: PushLodLevelTests.LevelChangesConvergeAtEverySkipRate (levels moved at random, both ways, several steps at once, banded and bandless,
    skip 0–60 %) and LevelChangesConvergeOnTheDeepImplementation, PushLodLevelTests.ABudgetRaisesTheLevelAndRemovingItLowersIt (end to end through
    SetBudget, and the fold it implies), PushLodLevelTests.TheBudgetLoopRisesAfterASecondOverAndFallsAfterThreeUnder (the loop's timing, cadence and
    an elapsed-ticks step), PushLodLevelTests.AClosedSessionAtALevelReleasesTheFold (the census and the fold's window),
    PushLodLevelTests.ARadiusTheBudgetShrinksConverges (the last resort, never below R′/2), PushLodLevelTests.AnOverloadedTickRaisesEveryLevelAndHalvesTheRate
    (the overload step and rate). Falsifiability: a fold window sized from the lowest level turns the bandless level cases red; dropping the fall's widening turns
    LevelChangesConvergeAtEverySkipRate(0, banded) red; a fold phase blind to the levels turns the bandless cases and ABudget… red; no inner crescent
    on a band that went turns LevelChangesConvergeAtEverySkipRate(0, bandless) and ABudget… red. The refused-frame clause and the World reset have no
    test.

### SUB-23: A near budget delivers and takes back whole cells, nearest the region's centroid first `[fatal][silent]`
  invariant ∀ ClientRegion session s with near budget n (09 § 7): its estimate — the entities of its archetypes in its delivered cells, from the per-cell
    counts (SUB-24) — is an upper bound on what it holds; the cells its hull meets are delivered nearest the hull's centroid first, by Chebyshev ring,
    while the estimate plus the next cell's count stays within n — the first cell past the prefix ends the walk even when it is one this frame took back;
    past 1.1 n, the farthest delivered cells are taken back at once (a sweep of leaves)
    until the estimate is within n; a new hull first takes back the delivered cells past its own ring order's prefix within n, so a pan holds the cells
    around its new centroid; with the hull unchanged and the last fill complete, more is delivered only after the estimate has stayed under 0.9 n for a
    second (the deadband, F5). The estimate and the deadband's clock commit with the frame (SUB-03)
  invariant VIEW_COMPLETE means every cell the budget admits is delivered: a fill the near budget stopped is complete, one the enter budget stopped is not
  never deliver or take back part of a cell, and never evict a single entity for the budget: what s holds is always known(s, e) of SUB-16 over its
    delivered cells
  scope: PushReplication.GatherRegion, PushReplication.HeldCount, PushReplication.TakeBackPastThePrefix, PushReplication.NearCounts,
    SubscriptionProfiles.NearOf, SubscriptionProfiles.BindNearCounts
  on_violation: a budget that splits cells holds entities no geometry names, and one without the deadband delivers and takes back the same cell as
    counts wobble around n — a client that sees entities flicker at the edge of its view.
  rationale: push keeps no per-(session, entity) state, so a budget can act only on what the geometry names — whole cells; an upper-bound estimate makes
    it err toward fewer entities, never more (09 § 7, prior art F2 and F5).
  verified: ClientRegionObserverTests.ANearBudgetDeliversWholeCellsNearestTheCentroid (within n and short of it by less than a cell, the estimate exact,
    no cell half held, nothing nearer undelivered, a static scene changing nothing for 3 s), ClientRegionObserverTests.MoreIsDeliveredOnlyAfterASecondUnderNinetyPercent
    (red without the deadband's second), ClientRegionObserverTests.PastTenPercentOverTheFarthestCellsAreTakenBack,
    ClientRegionObserverTests.APannedRegionHoldsTheCellsNearestItsNewCentroid (red without the prefix's take-back),
    PushRegionTests.ABudgetedRegionHoldsWholeCellsAndItsAggregateTheRest (churn, moves and skips: at most 1.1 n and within the estimate at every quiet
    point, the shadow oracle exact), and its 3D form PushOracle3DTests.ABudgetedPolyhedronHoldsWholeCellsAndItsAggregateTheRest.

### SUB-24: The occupancy counts exactly the entities whose last pushed position lies in each cell `[fatal][silent]`
  invariant after every index, ∀ cell: occupancy(cell) = the live, identified entries of observed archetypes whose last pushed position lies in cell,
    and a cell with none has no entry
  invariant every tick the track runs is indexed, sessions bound to a profile or not: the tick's cell changes are the occupancy's only input
  invariant a tick whose changes the occupancy missed — one the track did not run for, or one it ran but never finished indexing — is followed by a
    recount at the next index's finish, after that tick's projection: only then do the blocks describe the fence's carried and parked entries
  invariant the aggregate grids (09 § 8) take the same deltas per archetype and tile: after every index, ∀ grid, tile, counted archetype a: count = the
    live, identified entries of a whose last pushed position lies in the tile; a recount of the occupancy recounts them. A tile is a whole number of
    replication cells over the same origin — a move within a cell never crosses a tile edge — and a grid counts only archetypes push replication serves
  invariant a session's AGG block carries, on its aggregate tick (at most the declared rate, staggered by slot), the tiles of its region (every tile,
    or those meeting the declared radius around its anchor) that changed since its last AGG or that the region newly covers; the first, or one after a
    reset, sets RESET and carries every non-empty tile. Counts are absolute, so a skipped session's next AGG is the union. A ClientRegion session's
    region is its hull less the cells its near tier delivered — the tiles with a cell inside the hull's cell box that meets the hull and was not
    delivered, a deep grid's tiles always with their z extent — and "newly covers" is measured against its region at its last AGG, kept with it; a tile
    that left the region is sent as zero, since the client knows its hull but not which cells were delivered
  invariant the near budgets' per-cell counts (SUB-23) take the same deltas, one map per distinct archetype set a budgeted profile observes: after every
    index, ∀ cell: count = the live, identified entries of the set's archetypes whose last pushed position lies in the cell; recounted with the occupancy
  never skip a cell delivery or a sweep on a cell whose count is not zero
  scope: ReplicationOccupancy, PushReplication.MergeChunk, PushReplication.FinishIndex, PushReplication.Recount, PushReplication.PrepareBlocks,
    PushReplication.DeliverCell, PushReplication.SweepCell, FrameAssembler.BeginPushTick, AggregateCounts, PushReplication.RecountAggregates,
    PushReplication.RegionAggregates, FrameAssembler.SelectRegionAggregateRows
  on_violation: an under-count skips a cell that holds entities — a session never receives them until they move, silently. An over-count only costs
    a query.
  rationale: the empty-cell skip is what makes a sparse or 3D fill cheap; it is sound only because every change of cell makes an event (SUB-19).
  verified: PushIndexTests.TheOccupancyEqualsARecountUnderChurn, PushIndexTests.ASkippedTickIsFollowedByARecount,
    PushIndexTests.AnUnindexedTickIsFollowedByARecount, PushIndexTests.ATickWithNoBoundSessionIsStillIndexed,
    ReplicationOccupancyTests (the map against a dictionary), PushAggregateTests.TheCountsEqualARecountUnderChurn (World and Sphere, skip 0 and 60 %),
    PushAggregateTests.TheClientsCountsAreTheServers, PushRegionTests.ABudgetedRegionHoldsWholeCellsAndItsAggregateTheRest (the near counts and the
    tiles against a recount at every quiet point; a region's aggregate tile for tile against a region derived in the test — red when "newly covers" or
    the zero rows are dropped), PushOracle3DTests.ABudgetedPolyhedronHoldsWholeCellsAndItsAggregateTheRest (also an aggregate one tile as tall as the
    world — red when its z extent is taken as the grid's). Falsifiability: PushIndexTests.AnOccupancyThatKeepsMoversInTheCellTheyLeftIsCaught runs the
    mutant that drops a secondary's decrement; dropping it from the aggregate deltas turns TheCountsEqualARecountUnderChurn red.

### SUB-25: The push index holds this tick's events in cell order and touches only occupied cells `[perf][silent]`
  invariant ∀ tick T, the index (= T's log slot) holds every event of T once under its primary cell and once more under the cell a mover left, cells
    ascending by packed key, a cell's primaries before its secondaries; its row table names every occupied row's first cell
  invariant building and reading the index costs O(events + occupied cells + rows read): no per-tick structure is sized by, cleared over or walked
    across the replication grid's cell count, so an empty world indexes nothing whatever its cell side (10 § 2.3, SUB-13's precedent)
  never let a read path insert into the index or the row table
  scope: PushReplication.SortRun, PushReplication.MergeChunk, PushReplication.FinishIndex, PushReplication.Gather, PushReplication.CollectLog
  on_violation: silent and slow. A dense index pays for every cell of the world every tick with nothing to index — the reason a 3D or a fine grid was
    unaffordable — and a misfiled event is an entity a session never hears about.
  rationale: 3D cubes the cell count; only a structure that follows events keeps replication's cost independent of the grid.
  verified: PushIndexTests.TheIndexHoldsExactlyTheCellsTheEventsName and PushIndexTests.ConcurrentMergeChunksBuildTheSameIndexShape — the index as a
    multiset of (identity, cell, secondary) against the runs' raw events; PushIndexTests.AnEmptyWorldIndexesNothingWhateverTheGrid.
    Falsifiability: PushIndexTests.AnIndexThatMisfilesSecondariesIsCaught. The cost clause is timed by the explicit
    PushIndexTests.AnEmptyWorldCostsTheSameWhateverTheCellSide, not in the gate; the never clause has no test.
