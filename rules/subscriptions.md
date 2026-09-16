# Subscriptions Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-09-16 |
| Domain | Engine-owned replication (Subscriptions v2): per-entity replication state, its storage, and what bounds its cost |

> Invariants that keep replication cost tied to what clients are actually looking at, and keep per-entity
> replication state attached to the entity it describes across every way an entity can move.

> **Scope note.** These rules constrain engine-owned replication (v2). The v1 subscription server
> (`SubscriptionServerOptions`, `TcpSubscriptionServer`, `SubscriptionOutputPhase`) is a separate transport
> layer and is not covered here.

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
    is the property v2 exists to provide.
  rationale: a cluster chunk id is a PERSISTED FILE ADDRESS, not a residency measure. Ids are handed out by the
    segment and freed only when a cluster truly drains — evicting a chunk's pages never releases its id. So a
    flat array indexed by chunk id costs what the DATABASE holds, and no cluster-count threshold fixes that;
    a threshold only moves where it breaks. The engine's own per-cluster side tables (`ClusterAabbs`,
    `ClusterCellMap`, `ClusterSpatialIndexSlot`) are flat arrays of exactly this shape and are the PRECEDENT
    THIS RULE REFUSES — see Typhon issue #960, which tracks that cost on the engine side.
  note a flat array remains legitimate for a specific archetype behind a self-justifying size test — take it
    when that archetype's peak cluster count makes the array smaller than the map. It is never the default.
  verified: ReplicationDirectoryTests.SparseChunkIdsCostOnlyWhatIsWatched — the MEMORY invariant only. The
    per-tick-work invariant is asserted by nothing yet: it needs a projection pass to measure, and until one exists a
    verifier for it would be a false assurance. NetIdAllocatorTests.ChurnAtAStablePopulationDoesNotGrowTheIdentitySpace
    covers the identity space's half of the memory bound.

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
  invariant [Release(n)] → [generation(n) incremented] → [n reachable by Allocate]
  never one netId held by two live entities at the same time
  never a Release of an identity that is already free (it would thread the free list to itself, after which every
    Allocate returns that same identity and LiveCount runs negative)
  scope: NetIdAllocator.Allocate, NetIdAllocator.Release, NetIdAllocator.GenerationOf,
    ReplicationHotEntry.NetId, ReplicationHotEntry.Generation
  on_violation: a session that missed the release sees one identity carry a second entity's data and concludes the
    entity moved rather than that it was replaced — the client's world silently disagrees with the server's, with no
    error on either side. The double-release form is worse: two live entities share an identity, so one of them is
    unaddressable for the rest of the session.
  rationale: sessions compare the pair (netId, generation) between consecutive ticks; they do not need it unique for
    the process lifetime, which is why 16 bits suffice. A wrap needs 65 536 reuses of one identity.
  note the generation is bumped on RELEASE, not on the next allocate, so an entity that leaves and is never replaced
    still reads as gone to a session holding the old pair.
  note defined in the design series at design/Subscriptions/V2/02-execution.md; it was cited by code before it was
    written down here, which neither rule gate can detect — check-rule-scopes.py validates scope symbols only, and
    audit-rule-coverage.py's UNKNOWN_RULE_ID inspects [VerifiesRule]/[RuleMutant] attributes only.
  verified: NetIdAllocatorTests.ReusingAnIdentityBumpsItsGeneration,
    NetIdAllocatorTests.ReleasingTheSameIdentityTwiceIsRejected

### SUB-09: State follows its entity, and never survives slot reuse `[fatal][silent][UNBUILT]`
  invariant ∀ watched entity e: the hot/cold entry describing e is reachable from e's CURRENT (cluster, slot)
  invariant ∀ chunk id c freed by a drain: [directory entry for c cleared] → [FreeChunk(c) returns the id]
  never a directory entry naming a block whose cluster has been freed
  never an entry inherited by a different entity through slot reuse or a recycled chunk id
  scope: ReplicationDirectory.TryAdd, ReplicationDirectory.TryRemove, ReplicationBlockPool.TryRent,
    ReplicationBlockHeader.ChunkId, ReplicationHotEntry.Entity,
    ArchetypeReplicationState.TryReleaseBlock, ArchetypeReplicationState.ReleaseBlockForDrain,
    ArchetypeReplicationState.AttachTo, ArchetypeClusterState.ReplicationState,
    ArchetypeClusterState.DrainPendingClusterFinalizations, ArchetypeClusterState.ReleaseSlot
  on_violation: hits into a cluster that inherited a recycled id find state describing the cluster that
    drained — a client is told about an entity that no longer exists, or told the wrong values for one that
    does, with no error anywhere. Silent because every structure involved stays internally consistent.
  requires: the engine's clear-at-drain convention for per-cluster side tables (`ResetClusterVisibility`
    requires every site freeing a cluster chunk to clear its side tables before the id is handed back)
  [UNBUILT] Partly built. BUILT: the drain half. `ArchetypeClusterState.ReplicationState` is null-conditionally
    released at all three sites where a cluster chunk id becomes reusable, each immediately after
    `ResetClusterVisibility` and before `FreeChunk` — the deferred drain in `DrainPendingClusterFinalizations`,
    and the inline branches of both `ReleaseSlot` overloads, reached through the shared `RetireClusterId` helper.
    Coverage of the built half is complete for what can run, and is worth stating per site rather than per fence
    mode: `ReplicationDrainHookTests` reaches the INLINE persistent site — a destroy commit passes no
    `deferFinalize` (`Transaction.ECS.cs:2991`), so it finalizes at commit, not on the deferred path;
    `ReplicationDrainHookParallelFenceTests` reaches the DEFERRED site through the dispatched `ArchetypeFinalize`
    item, and provably so — the driver-slice path needs ≥ 2 populated dirty ranges (`TickFence.cs:1762-1763`) and
    that geometry yields one, so it cannot be taken. The third site is unreachable (next NOTE).
    No `verified:` field is claimed: this rule still covers entity MOVEMENT, which is unbuilt, and a `verified:`
    naming tests that exercise only the drain half would report coverage the rule does not have.
    NOTE the placement deviates from the design, deliberately: § 4 proposed a single call inside
    `FinaliseEmptyClusterCellState`, but that method early-returns when the archetype has no grid, no
    `ClusterCellMap`, or an unmapped cell, so a non-spatial or grid-less archetype would drain a cluster and
    never release its block. The three call sites are the complete set instead.
    NOTE the pure-Transient `ReleaseSlot` overload's site cannot fire for any replication scenario that exists:
    `[SpatialIndex]` is rejected on a Transient component (`DatabaseDefinitions.cs:369-372`), so a pure-Transient
    archetype is never spatial and never holds a watched cluster. The hook is kept as one null test, defensive
    against a future non-spatial replication mode, and is untested BY CONSTRUCTION rather than by omission.
    STILL MISSING: (1) the migration hook beside the component-copy loop in
    `DatabaseEngine.ClusterMigration.ExecuteMigrations` that carries an entry across a cluster change, with
    per-worker parking when the destination has no block — blocked on the Subscriptions track (#955), whose
    prologue is what drains the parked lists; (2) the per-entry `EntityId` check on the read path that catches
    slot reuse inside a LIVING cluster, which the drain half does not cover. Until both exist this rule states
    intent for entity movement, and behaviour only for cluster drain.
  note the id is reserved rather than omitted so it is not mistaken for a deleted rule, per `rules/README.md`.
