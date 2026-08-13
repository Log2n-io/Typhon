# ECS Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-07-20 |
| Domain | Component schema identity, archetype registry, component-type identity |

> Type-location: `Ecs/internals/ArchetypeRegistry.cs`, `Ecs/internals/ArchetypeMetadata.cs` (+ `ArchetypeEngineState`), `Ecs/public/DatabaseEngine.cs`
> (`RegisterComponentFromAccessor`, the reopen schema-load path), `Schema.Definition/Attributes.cs` (`[Component]`).

---

## Module: SCHEMA — Component schema identity

A component's durable identity is `(schema name, revision)` — persisted in `ComponentR1` and re-matched by name on reopen. `StorageMode`
(Versioned / SingleVersion / Transient) is part of the schema for that identity, not a per-engine or per-registration choice: it decides the physical storage
discipline (MVCC revision chains vs in-place SV vs heap-backed Transient), so reinterpreting persisted bytes under a different mode is silent corruption.

### SCHEMA-01: StorageMode is fixed per (name, revision) `[fatal]`
  invariant ∀ component identity (name, rev): StorageMode(name, rev) is immutable across the schema's lifetime — the value declared on `[Component]` is the sole
            source of truth; there is NO per-registration override
  never two registrations (in one process or across a reopen) resolve the same (name, rev) to different StorageModes
  requires to change how a component is stored, the author increases the `[Component]` revision (a new identity), which routes through schema evolution/migration
  enforce (registration) StorageMode comes only from the `[Component]` attribute — `RegisterComponentFromAccessor` has no `storageModeOverride`; the definition's
          mode is never mutated post-build (DatabaseEngine.cs)
  enforce (reopen) if a persisted component is re-declared at the SAME revision with a different StorageMode, registration throws
          (`definition.Revision == persisted.Comp.SchemaRevision ∧ declared ≠ persisted → throw`) — DatabaseEngine.cs reopen load path
  on_violation: persisted data is read under the wrong storage discipline (e.g. Versioned revision-chain heads parsed as SingleVersion in-place) → silent wrong
                data. Cross-engine, a peer's divergent mode also stomped the shared cluster layout keyed off StorageMode (the #530/#514 flaky-fixture bug).
  rationale: the old `storageModeOverride` (test-only) let one process hold two contradictory definitions of the same (name, rev); removing it makes the invariant
             hold by construction and lets cluster-eligibility/layout (derived from StorageMode) stay safely on the process-shared `ArchetypeMetadata`.
  verified: StorageModeRevisionLockTests [VerifiesRule]

### SCHEMA-02: ComponentTypeId is a process-global in-memory handle `[fatal]`
  invariant a component type's dense ComponentTypeId is assigned once per process (first `Register<T>()` / DeclareComponent), stable for the type's lifetime,
            deduped by schema name (V1/V2 of a name share one id); it is NEVER persisted — durability addresses `(routingId, slot)` (see durability LOG-06)
  never a per-engine or per-DB ComponentTypeId — the static `Comp<T>` handle captures the id once and every engine resolves the same slot from it
  scope: ArchetypeRegistry.DeclareComponent (ComponentTypeIds / ComponentTypeById / ComponentTypeIdsBySchemaName / NextComponentTypeId), Comp<T>
  on_violation: static handles disagree with an engine's slot map → wrong-component reads/writes

---

## Module: CLUSTERWALK — Concurrent cluster enumeration vs structural mutation

Cluster *topology* changes (migration, AABB refresh) and spatial-index updates are **fence-deferred**: `WriteSpatial` only flags, and the post-track parallel
fence drains. That makes a concurrent read-walk of an archetype's clusters structurally safe for those operations. Entity **destroy** is the exception — it is
applied synchronously inside `Commit()`, not at the fence.

### CLUSTERWALK-01: Destroy mutates the active-cluster list inside Commit, not at the fence `[fatal][silent]`
  invariant ∀ walk over `ActiveClusterIds[0 .. ActiveClusterCount)`: no concurrent `Transaction.Destroy` + `Commit` may target the same archetype
  scope: `Transactions/public/Transaction.ECS.cs` (`FlushEcsPendingOperations` → `FlushPendingDestroys`, :1297/:2219),
         `Ecs/internals/ArchetypeClusterState.cs` (`ReleaseSlot` :2493/:2544 → `RemoveFromActiveList` :2406),
         `Runtime/public/TyphonRuntime.cs` (`ExecuteChunkWithAccessor` :1507/:1564, `ExecuteChunkWithTransaction` :1724, `OnParallelQueryPrepare` :1193),
         `Querying/internals/StatisticsRebuilder.cs` (`RebuildClusterAll` :118-127) reached from `Querying/internals/StatisticsWorker.cs` :154-172
  requires `RemoveFromActiveList` performs a swap-with-last followed by a separate decrement —
           `ActiveClusterIds[i] = ActiveClusterIds[ActiveClusterCount - 1]; ActiveClusterCount--;` (:2424-2425) — two non-atomic steps with no version gate
           on the reader side beyond `ClusterSetVersion++` (:2434), which is bumped *after* the mutation
  on_violation: a walker interleaving between the swap and the decrement either visits the moved cluster twice or misses the tail cluster entirely →
                silently skipped or double-processed entities, with no error surfaced
  rationale: unlike migration/AABB (fence-drained behind a phase barrier), destroy releases the slot on the committing thread. Systems that enumerate clusters
             while another system destroys entities in the same archetype are therefore unsafe today. The AntHill decomposition resolved this by removing all
             entity destroys (respawn-as-larva) rather than by fencing — i.e. the hazard was avoided, not fixed.
  note: the `StatisticsRebuilder` reader is the widest exposure and the newest (#629 review M3, added in `cf476099`). Every other reader is a DAG worker, so a
        tick phase bounds when it can overlap a destroy; this one runs on the `Typhon-Statistics` BACKGROUND thread on a timer, so nothing bounds it at all —
        it reads `ActiveClusterIds` / `ActiveClusterCount` with plain loads and dereferences the chunks they name. Its blast radius is narrower than the
        others' in exchange: the throw is swallowed (`StatisticsWorker.cs:174-177`) so the cost is stale statistics, a garbage sample yields a bad plan rather
        than wrong rows, and `EpochGuard` keeps the pages mapped so the freed-chunk read cannot fault.
  verified: NOT COVERED — no test exercises concurrent walk vs destroy on one archetype

### CLUSTERWALK-02: The active-cluster list is one value, read count-first `[fatal]`
  invariant `(ActiveClusterIds, ActiveClusterCount)` is a PAIR. A reader acquires them in the order
            count → array; a writer releases them in the mirror order array → count. `count <= ids.Length` then holds for
            every reader, always.
  never loading the array before the count. That yields an array SHORTER than the count about to index it, and it needs no
        instruction reordering to fault — a plain interleaving suffices: read the length-16 array, let a concurrent spawn
        resize and bump the count to 17, read 17, index 16.
  never a call site reading the pair directly. All five go through `TyphonRuntime.ReadActiveClusterList`, because the two
        sites that already loaded count-first were right by ACCIDENT and nothing stopped the next one from being written
        either way.
  enforce `AddToActiveList` stores the grown array plainly and publishes the count with `Volatile.Write`; the release
          cannot let the preceding array store sink past it, so acquiring the count guarantees seeing the array. Caching
          either into a local first is what must NOT be done — it widens the writer's own window and reintroduces the fault.
  scope: ArchetypeClusterState.AddToActiveList / RemoveFromActiveList (writer), ArchetypeClusterState.ReadActiveClusterList
         (the one reader) and its callers — TyphonRuntime.ReadActiveClusterList, which now only delegates, and through it the
         dormancy promote, the checkerboard promote and three chunk-partition sites, plus EcsQuery.TryCountViaOccupancy
  on_violation: `IndexOutOfRangeException` out of the parallel-query prepare, on a worker thread. LOUD, which is the only
                good thing about it.
  rationale: #582 face 2. Note what this rule does NOT give: it makes the pair CONSISTENT, not the walk SAFE. A walker
             racing `RemoveFromActiveList` can still see one cluster twice and skip the destroyed one, whose chunk is freed
             two lines later — CLUSTERWALK-01, which needs a snapshot or epoch protocol and is unfixed.
  requires CLUSTERWALK-01 (same pair, the other hazard on it)
  verified: ActiveClusterListPublicationTests [VerifiesRule] — four DETERMINISTIC cases, not a stress loop. Racing for this
            does not work: a 40 000-add spin, about twelve resizes, landed inside the two-instruction window zero times in
            three runs, so a stress test would assert only that a safe order is safe. One case positively demonstrates the
            removed order producing `count > ids.Length` rather than merely asserting the new one does not.

## Module: CLUSTERVIS — The per-cluster MVCC visibility summary (H1)

A cluster carries two watermarks, `ClusterMaxBornTsn` and `ClusterMaxDiedTsn`, and `IsClusterFullyVisibleAt(c, txTsn)` is
true only when a reader at `txTsn` may skip the per-entity `EntityMap` probe for that whole cluster. It is a conservative
approximation: false is always safe and merely slower. Its consumers differ in what a wrong TRUE costs them — `EcsQuery`'s
SoA and Path-A scans keep a per-entity probe, so a bad grant only loses performance, while `EcsQuery.TryCountViaOccupancy`
popcounts the occupancy word on the strength of the grant alone and has nothing to catch it.

### CLUSTERVIS-01: The watermark is folded before the store that publishes the slot `[fatal][silent]`
  invariant at every instant a slot's occupancy bit is observable as SET, the cluster's summary already bounds the entity
            occupying it
  never folding the watermark after the claim returns. The claim is what publishes the bit, so a caller-side fold leaves a
        window in which a reader sees the bit paired with a maximum that predates the entity.
  enforce `NoteClusterBorn` runs inside the claim, ahead of the publishing CAS; `NoteClusterDied` runs ahead of
          `ReleaseSlot`'s clear. The reader's half is the mirror — acquire-read the occupancy word, THEN the watermarks.
  enforce a FRESHLY allocated cluster is left at `VisibilityUnknown` by the claim and established by the caller once the
          slot has contents. The two directions need opposite treatment: for an existing cluster the hazard is an OLDER
          reader (fix: raise before publishing), for a fresh one it is a NEWER reader seeing a bit whose EntityId tail and
          EntityMap record do not exist yet (fix: deny until established). Establishing from the sentinel is sound ONLY on a
          fresh cluster, which holds exactly one entity, so the value is exact.
  enforce `ResetClusterVisibility` runs at every site that frees a cluster chunk, before the id can be recycled. Chunk ids
          come from a free list, so without it a "fresh" cluster inherits the previous occupant's watermarks and the clause
          above silently does nothing.
  enforce both folds are `Interlocked` on the element AND re-read the array reference after the CAS. Concurrent claimants
          fold into the same cluster (#708: Transient spawns commit concurrently), and a fold can otherwise land in an array
          a grower has already copied and is about to replace.
  scope: ArchetypeClusterState.NoteClusterBorn, ArchetypeClusterState.NoteClusterDied, ArchetypeClusterState.ClaimSlot,
         ArchetypeClusterState.ClaimSlotInCell, ArchetypeClusterState.ResetClusterVisibility,
         ArchetypeClusterState.IsClusterFullyVisibleAt, ArchetypeClusterState.EnsureClusterVisibilityCapacity
  on_violation: `Count()` returns a number no scan agrees with, and the scans emit an entity that does not exist at the
                reader's snapshot. Silent both ways — every value looks plausible.
  rationale: found in review, not by tests. 5 300 tests pass with the fold on either side of the publish, because both
             states are momentary and both settle correct.
  verified: ClusterVisibilitySummaryIntegrityTests.ClaimingASlot_BoundsTheClusterBeforeItPublishesTheOccupancyBit
            [VerifiesRule] — calls the claim and reads the summary with NOTHING in between, so the ordering is asserted
            single-threaded instead of raced for. Move the fold back to the caller and it fails every run, together with the
            from-scratch audit in the same fixture.

### CLUSTERVIS-02: A tombstone that keeps its occupancy bit must deny the gate outright `[fatal][silent]`
  invariant a cluster holding a slot whose entity is dead while its bit is still set never reports fully-visible
  never folding a real `DiedTSN` at a site that does not clear the bit. The died watermark's entire argument is that a
        reader past the last death is exact BECAUSE occupancy already reflects it; where the bit survives, that is false and
        every reader past that TSN is granted over a tombstone.
  enforce the two sites in that shape — WAL replay (`RecoveryApplier.ApplyDestroyToExisting`, whose cleanup is deferred to
          the orphan sweep) and cluster migration (which sets a dst bit and releases only the src slot) — fold
          `VisibilityUnknown`, restoring the permanent deny the pre-#722 sticky flag gave for free.
  scope: RecoveryApplier.ApplyDestroyToExisting, DatabaseEngine.ExecuteMigrations, ArchetypeClusterState.NoteClusterDied
  on_violation: permanent over-count after any recovery that replays a below-frontier destroy; `Count()` returns N+1 while
                the scan returns N.
  rationale: #722 replaced a sticky "has anything ever died here" bit with a recovering maximum, which is the point — a
             churning archetype used to latch onto the per-entity probe forever. The cost is that "the bit is cleared at
             destroy" became load-bearing for every caller, and two callers never held it.
  requires CLUSTERVIS-01 (same summary, the publication half)
