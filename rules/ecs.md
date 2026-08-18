# ECS Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-08-17 |
| Domain | Component schema identity, archetype registry, component-type identity, tick-fence dirty bitmaps, spawn payload staging |

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

### SCHEMA-03: An archetype catalog id and a routing id are never interchangeable `[fatal][silent]`
  invariant an archetype has TWO ids that are both `ushort` and usually differ: the per-PROCESS **catalog id** (`ArchetypeMetadata.ArchetypeId`, assigned in
            registration order by `GetOrAssignCatalogId`, capped at 4095, NEVER persisted) and the per-DATABASE **routing id** (`ArchetypeR1.RoutingId`,
            persisted, re-matched by name on reopen, embedded in the low 16 bits of every `EntityId`)
  never a catalog id is compared to, joined against, or substituted for a routing id — in either direction
  never `entityId.ArchetypeId == someEvent.ArchetypeId` (the left side is a routing id; the right side, in every profiler event, is a catalog id)
  enforce (engine) cross the boundary only through `DatabaseEngine.RoutingIdOf(meta)` / `RoutingIdForCatalog(catalogId)`; `NoRoutingId` (0xFFFF) means "this DB
          has no such archetype" and must be handled, not treated as an id
  enforce (trace) a `.typhon-trace` records BOTH: events carry the catalog id, and the v12 archetype table carries the matching routing id
          (`ArchetypeRecord.RoutingId`). Consumers resolve through `TraceArchetypeIdentity`, which is also the one place that honours
          `TraceHeaderFlags.MultipleEnginesObserved` — under which the routing ids are absent, not merely suspect (#614 D-3/D-9)
  on_violation: a plausible but WRONG archetype for every archetype whose registration order differs from its persisted routing order. **It cannot be caught by
                a fixture**: in a freshly-created database the two orders coincide, so the mistake passes every test written against one and fails only on a
                database that gained archetypes over time — i.e. on real user data.
  rationale: two dense small-integer id spaces over the same domain, with no type distinction, is a trap that reads as correct at the call site. The design that
             surfaced it is claude/design/Apps/Workbench/10-database-and-profiles.md §5.3.
  verified: RoutingIdTests, TraceV12SelfDescribingTests, CaptureIdentityFromLiveEngineTests [VerifiesRule]

### SCHEMA-04: A rename is journalled at the instant it is carried forward, or the mapping is lost forever `[silent]`
  invariant whenever a `PreviousName` match causes a persisted row to be re-keyed to a new name, a `SchemaHistoryR1` row with
            `Kind = SchemaChangeKind.Rename` is written recording `(PreviousName → ComponentName, Target, FromRevision → ToRevision)`
  never a re-key without a journal entry — that carry-forward is the ONLY moment at which both names are simultaneously known
  scope: DatabaseEngine.RecordSchemaRename; the carry-forward blocks in DatabaseEngine.RegisterComponentFromAccessor and DatabaseEngine.PersistNewArchetypes
  enforce exactly one row per hop: both call sites are reached only while the persisted row is still keyed by the old name, so a reopen after carry-forward
          cannot re-journal. Repeated renames therefore leave a chain, walkable forward oldest-first, not a single hop.
  enforce a rename that coincides with a field change writes TWO rows — the schema-change row and the rename row — so neither `Kind` is ambiguous and the
          field counters stay attached to the change that moved fields
  requires SCHEMA-05 (the system-schema gate), since the journal fields live in a system component
  on_violation: the mapping is **unrecoverable**. `[Component(PreviousName=…)]` / `[Archetype(PreviousName=…)]` are explicitly intended to be deleted from
                source once the row has been re-keyed (`DatabaseEngine.cs`, the carry-forward comments). After that the old name exists in no source file and
                in no database row, while profiling captures months older still refer to it — so every name-based bridge fails to match with no way to tell a
                rename from a deletion.
  rationale: robustness that cannot be retrofitted — the evidence is destroyed by the passage of a release, not by a bug. See
             claude/design/Apps/Workbench/10-database-and-profiles.md D-4 / §5.6.
  verified: SchemaRenameHistoryTests [VerifiesRule]

### SCHEMA-05: A system component's layout is gated by `BK_SystemSchemaRevision` `[fatal][silent]`
  invariant a database whose recorded `BK_SystemSchemaRevision` differs from `DatabaseEngine.CurrentSystemSchemaRevision` is REFUSED at open, in both
            directions (older data, and data written by a newer build)
  never a system component (`ComponentR1`, `SchemaHistoryR1`, `ArchetypeR1`, `AssemblyR1`) changes layout without bumping the constant
  scope: DatabaseEngine.CurrentSystemSchemaRevision, DatabaseEngine.LoadSystemSchemaR1, DatabaseEngine.CreateSystemSchemaR1
  requires the check runs BEFORE the system tables are constructed — everything after rebuilds them from the CLR types at the current layout
  on_violation: system components do not go through schema evolution. `LoadSystemSchemaR1` rebuilds their tables directly from the CLR types, bypassing the
                `SchemaDiff` / `FieldIdResolver` / migration machinery that user components get on registration, and their chunk stride is fixed when the
                table is created. A layout change therefore reinterprets existing rows under a new stride **with no error at all** — including in an
                otherwise-empty table, since the stride alone is enough to corrupt the read.
  rationale: the audit trail is what the Workbench consults to explain schema drift; a silently misread audit trail is worse than none. Bumping the constant
             requires no migration code, only that every existing database be recreated — the accepted pre-alpha trade (#615).
  verified: SystemSchemaRevisionGateTests [VerifiesRule]

### SCHEMA-06: A component's storage stride is the CLR size of its struct `[fatal][silent]`
  invariant ∀ component C backed by CLR struct T: `C.ComponentStorageSize == sizeof(T)`
  never a column is strided by the extent of C's fields (`lastField.Offset + lastField.Size`) when `sizeof(T)` is larger
  scope: DBComponentDefinition.Build, DBComponentDefinition.ComponentStorageSize, ArchetypeClusterInfo.ComponentSize, ClusterRef.CheckStride
  never the two accessor families disagree on the stride: `EntityRef` multiplies `ComponentSize(slot)` explicitly, while `ClusterRef` takes
        `ComponentOffset(slot)` as a base and lets `Span<T>` / `Unsafe.Add<T>` supply `sizeof(T)` implicitly. That asymmetry is exactly why the invariant is
        stated on the LAYOUT rather than on either call site — it is the only place the two can be made to agree
  on_violation: every accessor that hands out a `Span<T>` or a `ref T` steps by `sizeof(T)`, because that is what those types mean. A column strided by
                anything else mis-addresses slot i, so reads alias the wrong slot and run off the end of the column into the next component's, and a
                whole-struct write stamps the neighbouring slot's leading bytes. Nothing detects it: the bytes are structurally valid, the CRC covers what
                was written, and the flat per-entity view keeps reading correctly for interior slots — so the two views of the same memory disagree with no
                error anywhere (#816, found via #815).
  rationale: the two quantities differ exactly by the trailing padding the compiler adds to keep T's alignment inside an array, which the schema cannot see —
             `struct { long A; int B; }` has a field extent of 12 and a CLR size of 16. 14 components in this repository were in that state, including the
             engine's own `ArchetypeR1` and `SchemaHistoryR1`. Taking the CLR size costs those padding bytes per entity; a component that does not want to
             pay declares `[StructLayout(LayoutKind.Sequential, Pack = 4)]`, which caps field alignment and satisfies the invariant from the other side.
             TYPHON010 reports the choice at compile time, and reports only padding beyond a 4-byte multiple — rounding to 4 costs at most 3 bytes and is
             accepted; the 8-byte rounding one `long` imposes on a whole struct is not. `Pack` moves interior offsets, which are persisted, so a component
             with data already on disk pins `Size` to its extent instead.
  verified: ClusterPaddedComponentTests [VerifiesRule] [RuleMutant]

### SCHEMA-07: A field offset is only recorded when it was measured against the managed layout `[fatal][silent]`
  invariant ∀ field F of a component backed by CLR struct T: `F.OffsetInComponentStorage` is F's offset in the MANAGED layout of T — the layout every
            accessor reads through
  never a definition whose offsets were not measured against the managed layout is built from a CLR type that `LayoutDivergence.Detect` reports on — a `bool`
        or `char` at ANY depth, including inside a nested struct the schema does not model, which is dropped from the field list yet still displaces
        everything after it. Detection excludes the declarations that reconcile the two layouts: `CharSet.Unicode`, an explicit `[MarshalAs]`,
        `LayoutKind.Explicit`, and `fixed` buffers
  never a consumer of a `ComponentSchemaSpec` trusts its offsets without checking `ManagedOffsets` — the flag is the claim, and a spec registered by a
        pre-#819 generator or by hand through the public registry carries offsets nobody measured
  scope: LayoutDivergence.Detect, ComponentSchemaSpec.ManagedOffsets, DBComponentDefinition.OffsetsAreManaged, DBComponentDefinition.Build,
         DatabaseDefinitions.CreateFromAccessor, DatabaseDefinitions.ReflectComponentSpec, AssemblySchemaLoader.BuildSchema,
         AssemblySchemaLoader.TryBuildSchemaFromGeneratedSpec
  requires SCHEMA-06 — the stride and the offsets describe the same layout, or neither addresses the component correctly
  on_violation: the marshalled layout differs from the managed one for exactly two field types: `bool` is 4 bytes marshalled and 1 managed, `char` is 1 and 2.
                Recording marshalled offsets therefore shifts every field after the first such one, and every field-addressed consumer — index key
                extraction, WAL field decode, crash recovery, schema evolution, the integrity scanner, the Workbench raw read — reads the wrong bytes.
                Whole-struct copies are unaffected, which is what keeps it quiet. For `char` the two layouts can total the SAME size
                (`{ char; char; int }` is 8 bytes either way, with only the middle offset wrong), so no size comparison at any layer can detect it.
  rationale: the source generator measures each field with `Unsafe.ByteOffset` against a stack probe, so a generated spec describes the managed layout and
             carries `ManagedOffsets`. Runtime reflection holds only a `Type`; getting a managed offset from one needs a typed `ref` to the field, which
             means IL emit — ruled out on AOT grounds (#409). So the reflection path declares what it cannot measure and the engine refuses those components
             rather than trusting them. The refusal is deliberately BROADER than analyzer TYPHON011, which compares the two layouts in full and permits a
             shape where they coincide (a lone `bool` before an `int` sits at the same offset either way): reproducing that comparison at runtime would mean
             reimplementing managed struct layout, so presence of the type is the proxy for divergence. Both paths that reach the check first consult
             `GeneratedSchemaRegistry`, so a component built with the generator is unaffected and the refusal lands only on assemblies built without Typhon's
             tooling, where "rebuild it with the generator" is the remedy (#819).
  verified: ReflectedOffsetProvenanceTests [VerifiesRule]

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

---

## Module: DIRTY — Dirty bitmaps track published-entity mutations

Two bitmaps drive the tick fence: `ComponentTable.DirtyBitmap`, keyed by content chunk id, and
`ArchetypeClusterState.ClusterDirtyBitmap`, keyed `clusterChunkId * 64 + slotIndex`. Both answer one question — *which
already-published entities did this tick mutate?* — and both feed the same two consumers: WAL emission at the fence, and
change-filtered system dispatch.

A **spawn is not a mutation** for this purpose, and `FinalizeSpawns` says so where it publishes: *"We do NOT set
ClusterDirtyBitmap here — that bitmap tracks write mutations for change-filtered dispatch, same as per-ComponentTable
DirtyBitmap (which is also not set during FinalizeSpawns for non-cluster SV entities)."* A spawn's bytes reach disk by a
different route — page-level dirty marks and the checkpoint under TickFence discipline, its own CM-06 Slot record under
Commit discipline.

### DIRTY-01: A spawn sets no dirty bit, in either bitmap `[fatal]` `[silent]`
  invariant ∀ entity e spawned in transaction T: at commit(T), e contributes no bit to `ComponentTable.DirtyBitmap` nor
            to `ClusterDirtyBitmap` — including when T also WRITES e before committing
  never marking a spawn-staging chunk id dirty. Until `FinalizeSpawns` publishes it, a spawned entity has no cluster slot
        — which is why the write lands on the pre-publish branch in the first place — so there is no correct bit to set,
        not merely an inconvenient one.
  scope: EntityAccessor.WriteEcsComponentData, Transaction.FinalizeSpawns, DatabaseEngine.ProcessTableFence
  on_violation: a SingleVersion staging chunk reaches the fence, which reads the entity PK from its overhead and gets 0 —
                the PK is stamped into a staging chunk only for TRANSIENT slots, while `EntityPKOverheadSize` is 8 for
                every non-Versioned component. Routing id 0 is reserved and never assigned, so `GetMetaByRouting` returns
                null and the fence throws. In Release the scheduler swallows it: measured over 55 s with every tick
                poisoned — 6,602 fence exceptions, 6,602 leaked `UnitOfWork` objects (1:1 with poisoned ticks), 321 MB of
                WAL across 5 unrecycled segments, and `CurrentTickNumber` frozen at 0, because the throw escapes before
                the counter's only mutation. Systems keep running and the timer keeps firing; the whole Fence DAG is
                skipped. The engine neither crashes nor blocks — its clock stops while it burns CPU and disk (#837). A
                TRANSIENT staging chunk fails differently and just as hard: its PK IS stamped, so it survives the fence
                and reaches the change-filtered dispatch scan, which calls `ComponentSegment.CreateChunkAccessor()` —
                null on a Transient table, which builds only its transient segments.
  note: this rule constrains PRODUCERS. `ProcessTableFence` now skips a zero-PK chunk rather than dereferencing routing
        id 0 — matching what the cluster walker already did for an unoccupied slot — so a future producer degrades to a
        dropped fence record instead of a frozen clock. That guard is defence in depth, not a substitute: a zero PK in
        that bitmap still means someone violated this rule.
  rationale: nothing is lost by withholding the bit. Under TickFence discipline a spawn's SingleVersion values are
             checkpoint-durable BY DESIGN and were never WAL-logged at the fence anyway; under Commit discipline the
             spawn's own CM-06 Slot record carries them, built from the staging chunk AFTER the in-place write, because
             own-spawns deliberately skip write staging (#713). Index entries are inserted by `FinalizeSpawns` itself
             from the same final bytes.
  verified: SpawnThenWriteFenceTests.OwnSpawnWrite_LeavesTheTableDirtyBitmapClean [VerifiesRule] — asserts the bitmap
            directly rather than inferring it from the absence of a crash, with
            SpawnThenWrite_EveryTick_LeavesTheRuntimeClockAdvancing covering the symptom that made this expensive: the
            clock, not the exception.

---

## Module: STAGE — Where a spawned entity's bytes live before it is published

A spawned entity has no address until `FinalizeSpawns` claims its cluster slot at commit, so its component payloads need
somewhere to sit in the meantime. Which "somewhere" is not a free choice: it decides whether the payload can ever be
reclaimed.

### STAGE-01: A cluster-backed non-Versioned spawn allocates no content chunk `[fatal]` `[silent]`
  invariant ∀ entity e spawned into a cluster-eligible archetype, ∀ slot s of e with StorageMode ∈ {SingleVersion,
            Transient}: the spawn allocates no chunk in `ComponentTable.ComponentSegment` nor in
            `TransientComponentSegment` for s — the payload is staged in the transaction's `SpawnStagingArena` and its
            durable home is the cluster slot
  never a pre-publish location is dereferenced as a content chunk id without first asking the slot's storage mode. The
        two address spaces are both `int` and both use 0 for "none", so a site that skips the question reads an
        unrelated chunk and reports no error.
  never allocating a content chunk whose id no persisted record can hold. The `ClusterEntityRecord` is `19 + 4×V` bytes:
        header, `ClusterChunkId`, `SlotIndex`, and one `CompRevFirstChunkId` per VERSIONED slot. There is no field a
        SingleVersion or Transient content-chunk id could occupy — that is a structural impossibility, not an omission.
  scope: Transaction.SpawnInternal, Transaction.SpawnBatch, Transaction.SpawnBatchAllocate, Transaction.SpawnBatchWriteAll,
         Transaction.FinalizeSpawns, Transaction.CleanupEcsState, Transaction.SpawnSlotLocation, Transaction.ResolveEntity,
         EntityAccessor.ResolveSpawnAwarePayload, EntityAccessor.ShadowIndexedFields, EntityRef.Write,
         EcsQuery.CollectPendingSpawnsFull, SpawnStagingArena, DeferredCleanupManager.ReleaseCollectionBuffers
  on_violation: the chunk becomes unreachable the instant `FinalizeSpawns` copies the payload into the cluster, and
                nothing frees it — every free site is gated on rollback or on Versioned. The file then grows with
                CUMULATIVE spawns rather than live entities. Measured in the SpaceBattle demo before the fix: 491,930
                `Bullet` chunks against ~1,200 live shots, 900,096 `Pos` chunks against ~20,200 live entities, and a
                282 MB data file holding ~1.8 MB of live entity bytes — a 160× gap that only grew (#839). Silent: every
                read returns correct data, because the authoritative copy is in the cluster the whole time.
  note: VERSIONED is excluded and must stay excluded. There the same chunk becomes `elements[0].ComponentChunkId`, the
        first revision's content, and the cluster slot is a HEAD cache over the chain rather than its owner — an MVCC
        point read at an older TSN walks the chain, so that chunk is live data and is correctly reclaimed with the
        chain. A fix that generalised to Versioned would silently discard history.
  note: the arena's blocks are appended, never reallocated, because a write returns a `ref` into a staged payload and
        spawn-spawn-write is ordinary usage — it is what `SpawnBatch` does. The `_commitStagingBuffer` next door does
        realloc and documents that its refs die on growth; that contract is acceptable there and not here.
  verified: ClusterSpawnChunkTests.SpawnDestroyChurn_DoesNotGrowTheSegments [VerifiesRule] — spawns and destroys a batch
            repeatedly and requires the chunk count to track LIVE entities. Before the fix, four rounds of 32 left 129
            chunks behind with zero entities alive; the count is the defect, so the count is what it asserts.
            ClusterSpawnChunkTests.VersionedSpawn_StillAllocatesItsRevisionContentChunk is the guard on the note above.

### STAGE-02: A Versioned component the spawn does not supply is ABSENT, not zeroed `[fatal]` `[silent]`
  invariant ∀ entity e spawned into a cluster-eligible archetype, ∀ slot s of e with StorageMode = Versioned that the
            spawn does not supply a value for: no content chunk and no revision chain are allocated for (e, s), the
            record's `CompRevFirstChunkId[vi(s)]` stays 0, and e's EnabledBits bit for s stays clear
  invariant absence and disabled are DISTINCT persisted states, both derivable from the record alone:
            (bit set, root ≠ 0) = present; (bit clear, root ≠ 0) = supplied then disabled, value retained;
            (bit clear, root = 0) = never supplied. No fourth field is needed and none may be added.
  never deriving one of those two signals from the other. `enabled ⟺ root ≠ 0` re-enables a component the caller
        disabled; `root = 0 ⟹ defect` warns on every partial spawn.
  never inventing a value for an unsupplied component — neither the previous occupant's bytes nor zero. `Enable(comp)`
        must refuse, because it has no value to enable; `Enable(comp, in value)` is the way to supply one mid-life.
  scope: EntityRef.Enable, EntityRef.IsVersionedSlotAbsent, EntityRef.ReadRaw, Transaction.CreateVersionedContentAndWrite,
         Transaction.AllocateVersionedSlotContent, Transaction.PublishNewVersionedChainRoots,
         Transaction.SpawnBatchAllocate, Transaction.SpawnBatchWriteAll, Transaction.FinalizeSpawns,
         ArchetypeClusterState.RebuildVersionedHeadFromChain, VersionedHeadRebuildSkips
  on_violation: the slot's storage is a RECYCLED chunk, so enabling an unsupplied component serves whatever a destroyed
                entity last committed there — one live entity reading another's data through the ordinary public API
                (#845). Silent in the worst way: the values are well-formed and plausible, and a fixture that happens to
                get a fresh chunk reads zero and passes, which is why the old contract's own tests asserted
                zero-initialisation and held for the entire period the leak existed.
  note: this REVERSES design decision #14, which specified zero-init for unsupplied components. That decision bought a
        real property — every declared component always readable — but the property was false: the zero it promised was
        never written, so what it actually guaranteed was that the read would not fault, not that the bytes were zero.
        Absence is the state the engine could not previously express; `RebuildClusterFromChains` already assumed it
        (`DatabaseEngine.cs`, "a Versioned slot with no chain head for this entity genuinely carries no component").
  note: publication of a chain root created mid-life rides in `FlushPendingEnableDisable`'s existing record round trip.
        That relies on a coupling worth stating: supplying a value REQUIRES enabling, because `Write` is gated by the
        same EnabledBits as `Read`, so a mid-life creation cannot occur without a pending enable to carry its root.
  note: the pending-spawn case must record the allocation in the `SpawnEntry`, not through the live-entity path.
        `FinalizeSpawns` writes `CompRevFirstChunkId` unconditionally from `entry.Rev[slot]`, so a root published any
        other way is clobbered by a still-zero entry and the value is lost at commit with no error.
  verified: UnsuppliedComponentPayloadTests.Versioned_EnablingANeverSuppliedComponent_IsRefused [VerifiesRule] — the
            refusal itself, decided from the record's root and the resolved location, so it does not depend on what the
            slot's bytes hold. The RECYCLE (spawn a pattern, destroy, drain, re-spawn omitting the component) is what
            makes the old defect observable, and it is Versioned_EnableWithAValue_SuppliesAndEnables and the
            SingleVersion sibling that actually read through it — a fresh chunk reads zero for uninteresting reasons.
            Siblings cover the neighbouring states:
            Versioned_EnableWithAValue_SuppliesAndEnables (supply mid-life on a live entity),
            Versioned_EnableWithAValue_OnAPendingSpawn_SurvivesTheCommit (the SpawnEntry route),
            Versioned_DisableThenEnable_KeepsTheValue_AndNeedsNoNewOne (the round trip the refusal must NOT catch),
            Versioned_ComponentSuppliedMidLife_IsWritableFromALaterTransaction (the root reached the persisted record,
            not merely the transaction's cache), and
            NonGenericEntityAccessTests.ReadRaw_NeverSuppliedComponent_ReturnsAnEmptySpan (absent ≠ disabled for raw
            consumers).

## Module: REAP — Reclaiming what a destroy leaves behind

`Destroy` releases the cluster slot inline, but an entity's `EntityMap` record cannot go with it: a transaction older
than the destroy must still be able to resolve that entity. The record is therefore queued and reclaimed later, once no
live snapshot can reach it — which makes "later" a thing that has to actually happen.

### REAP-01: Every deferred-cleanup queue has a production drain `[fatal]` `[silent]`
  invariant ∀ queue Q that a committed transaction appends to: some code path reachable WITHOUT a test calling it
            removes from Q, and does so for every entry once `minTSN` passes the entry's `DiedTSN`
  invariant the drain's gate names every queue it drains. A gate that asks about one queue and then drains two is a
            drain that never runs for any workload which fills only the other one
  never gate the ECS entity drain on `DeferredCleanupManager.QueueSize`. That queue fills when a VERSIONED component is
        superseded; the ECS queue fills on every destroy in EVERY storage mode. An all-SingleVersion database supersedes
        no revision, so the first is permanently zero and the second is never reached.
  never reclaim below `TransactionChain.ComputeNextMinTSN()` — the destroying transaction can never reclaim its own
        victims, because it is itself the tail until it retires
  scope: DatabaseEngine.EnqueueEcsCleanup, DatabaseEngine.ProcessEcsCleanups, DatabaseEngine.EcsCleanupQueueSize,
         DatabaseEngine.FlushDeferredCleanups, Transaction.ProcessDeferredCleanups,
         DeferredCleanupManager.ProcessDeferredCleanups
  on_violation: the `EntityMap` retains one record per entity ever destroyed, for the life of the engine, and its
                backing segment grows with CUMULATIVE destroys rather than live entities. Measured in the SpaceBattle
                demo before the fix: 561,796 `EntityMap` chunks against a live population that never exceeded ~13,900,
                holding 8,590 of the file's 9,692 pages — 89% — while every `Component` segment stayed flat at 30. The
                cost is a smooth per-operation regression, not a crash: engine time per unit of simulation work doubled
                (971 → 1,969 ns) over 100,000 ticks while the workload itself FELL (#681). Silent: every read returns
                correct data throughout, because a tombstone resolves to "not alive" exactly as a reclaimed record does.
  note: a drain that writes to a persistent structure must be given the ChangeSet that owns its dirty marks. PS-10 in
        `rules/durability.md` is the binding rule — `ChunkAccessor.MarkSlotDirty` raises ActiveChunkWriters
        unconditionally but reaches `IncrementDirty`, which is what also records the modification, only through a
        non-null ChangeSet. A drain wired up with `CreateChunkAccessor()` would trade a leak for lost writes.
  note: a queue whose only consumer is a test is worse than an absent one, because the suite reports on it. Both tests
        that covered this called `ProcessEcsCleanups` themselves and then asserted the entities were invisible — which
        destroy alone already guarantees — so they measured the call rather than the engine and stayed green for the
        whole period the queue leaked.
  verified: EcsCleanupDrainTests.SingleVersionChurn_LeavesTheEntityMapFlat_AcrossRounds [VerifiesRule] — churns an
            all-SingleVersion archetype in rounds and requires `EntityMap.EntryCount` to stay flat. Before the fix,
            round 1 held 52 records against 26 after round 0, with zero entities alive; a leak is a slope, so the test
            measures rounds rather than a total. EcsCleanupDrainTests.CleanupQueue_DrainsWithoutAnyExplicitCall guards
            the "reachable without a test calling it" clause, and Drain_LeavesNoOutstandingDirtyMarks_AtQuiesce guards
            the ChangeSet note above.
