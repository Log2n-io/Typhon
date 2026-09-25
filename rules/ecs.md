# ECS Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-09-25 |
| Domain | Component schema identity, archetype registry, component-type identity, tick-fence dirty bitmaps, spawn payload staging, entity handles, view lifetime |

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

## Module: MEMB — The archetype membership channel

An unfiltered `Query<TArchetype>().ToView()` subscribes to a per-archetype channel (`ArchetypeMembershipRegistry`) and is fed spawn/destroy entries from the
commit path, rather than re-running the whole archetype scan on every refresh. Two things make that sound: a publication order, and a promise about what the
entry may be used for. Both are silent when broken, which is why they are rules rather than comments.

### MEMB-01: The structural epoch is released after the entries it accounts for `[fatal][silent]`
  invariant at every instant a view can observe `StructuralEpoch == view.LastStructuralEpoch`, that view's delta buffer holds no entry it has
            not applied
  never bumping the epoch before appending this commit's membership entries, and never caching the epoch into a local ahead of the appends —
        the second is the first wearing a different hat, exactly as `CLUSTERWALK-02` describes for the active-cluster pair
  enforce `Transaction.PublishMembershipDeltas` appends to every subscribed buffer for every spawned and destroyed entity FIRST, then bumps each
          touched archetype's epoch. `Bump` is `Interlocked.Increment` — a full fence on x64 and arm64 — so the appends cannot sink past it, and
          the reader's `Volatile.Read` of the epoch pairs with that release.
  enforce it runs at the end of `FlushEcsPendingOperations`, which is inside the commit's PUBLISH phase and BEFORE `WaitAndFinalize` publishes the
          TSN. A reader whose snapshot can see the commit can therefore also see the counter move; bump it after the TSN and a view refreshing in
          between reads a visible commit against an unmoved epoch.
  enforce the refresh records the epoch as consumed only after the drain, and only when the buffer is empty. `TryPeek` stops at the first entry
          whose TSN exceeds the reader's snapshot — correct, those commits are not visible yet — and recording the epoch anyway would gate the
          next refresh away from entries that are still sitting there.
  enforce an archetype with no subscriber is skipped entirely — no append, no bump. Nothing can observe the epoch of an archetype nobody
          listens to, and bumping it would not close the registration window either (a commit that read the empty subscriber array before a
          view registered is missed by that view regardless, because its population scan is older than the commit). That window is inherited
          from `ToIncrementalView`, which has the identical one on the field channel.
  enforce the FIRST refresh after subscribing re-queries rather than draining (`EcsView._needsResync`). The seed is taken at the CREATING
          transaction's snapshot and folds in that transaction's uncommitted spawns, so it corresponds to no committed instant: a commit between
          that snapshot and the subscription is in neither the seed nor the buffer, and a creating transaction that rolls back leaves phantom ids
          that no channel entry can ever remove. `RefreshPull` re-executed at the REFRESH TSN every time and so healed both; the channel never
          re-executes, which is what makes an unrepaired seed permanent rather than transient.
  enforce a resync records the epochs it started from only if none moved while it ran, and stays in resync otherwise. A commit landing during the
          re-query is in neither its result nor the drained buffer, so recording the pre-read epochs would gate it away for good; under sustained
          churn the view simply keeps re-querying, which is the behaviour it had before the channel and the correct degradation.
  scope: Transaction.PublishMembershipDeltas, Transaction.FlushEcsPendingOperations, ArchetypeMembershipRegistry.Bump,
         ArchetypeMembershipRegistry.StructuralEpoch, EcsView.RefreshMembership, EcsView.SubscribeToMembership
  on_violation: the view reads "nothing changed", skips the drain, records the epoch as consumed, and NEVER sees those entities. Silent and
                permanent — the system runs every tick over a plausible entity count while the missing entities are committed, durable and
                queryable by everything else. This is #718's failure mode reintroduced through a different door.
  rationale: #790. The gate is what makes a view over a quiet archetype cost a load and a compare instead of a whole-archetype rescan, and a gate
             is only ever as sound as the order in which its counter is published.
  verified: SystemInputViewLivenessTests.EpochIsReleasedOnlyAfterTheEntriesItAccountsFor — CONSTRUCTS the interleaving via
            `QueryPathProbe.MembershipPrePublishBumpHook` rather than racing for it (the argument CLUSTERWALK-02 makes about a two-instruction
            window applies here too) and asserts the rule directly: entries already buffered, epoch not yet moved. MUTATION-CHECKED — moving
            `Bump()` above the appends turns it red and nothing else. The randomised differential
            `MembershipRefresh_AgreesWithTheReQuery_UnderRandomisedChurn` is complementary, not a verifier for this rule: it is sequential, so it
            has no reader between the bump and the appends and stays green against that same mutant.
            `ViewBuiltAfterACommitItsSnapshotPredates_StillConverges` covers the seed clause.

### MEMB-02: A membership refresh never dereferences a cluster chunk `[fatal][silent]`
  invariant the refresh path reads only its own ring buffer and its own entity set — never `ActiveClusterIds`, never a cluster chunk address
  never resolving the entry's `BeforeKey` cluster location to a chunk pointer at refresh time. It is carried as an OPAQUE value, for stage 2's
        per-cluster match bits, and reading it is what would make it dangerous
  enforce `EcsView.ProcessMembershipEntry` takes the entity id from the entry and touches nothing else. Everything the refresh needs was captured
          at commit, where the publishing thread already held it
  scope: EcsView.ProcessMembershipEntry, EcsView.RefreshMembership, Transaction.PublishMembershipEntry
  on_violation: reintroduces `CLUSTERWALK-01` on a path that has none. A cluster drained by a concurrent destroy is freed inline on the committing
                thread and its chunk id is immediately reusable (`ChunkBasedSegment.AllocateChunkInternal` takes the lowest clear bit), so the read
                lands in another archetype's cluster interpreted through this one's layout. No exception, no assert.
  rationale: #790, and it is the whole reason the feature does not wait on #582. The 2026-08-13 occupancy prototype put exactly this walk on the
             refresh path and was rejected for it. Stage 2 keeps the property by a different route — the view supplies match bits, the runtime
             supplies the cluster list it read at dispatch.
  requires CLUSTERWALK-01 (the hazard this rule exists to stay out of)
  verified: NOT COVERED — an absence is hard to assert directly. `MembershipRefresh_OnAQuietArchetype_TakesTheEpochGate` and
            `MembershipRefresh_Destroy_RemovesFromTheView` pin that the refresh takes the channel rather than any scan, which is the observable
            half; the invariant itself is held by review of one small method.
  note the sticky overflow flag is cleared by `ViewDeltaRingBuffer.ClearOverflow`, NOT by `Reset`. Reset also discards entries a producer appended
       for a commit the resyncing reader cannot see yet, which would lose them outright; clearing the flag alone keeps that tail.
       `MembershipRefresh_BurstBeyondTheRingBuffer_FallsBackAndStaysExact` asserts the view returns to the channel afterwards rather than latching
       onto the re-query for the life of the process.

### MEMB-04: A disposed view's delta buffer is retired, never freed under a publisher `[fatal][silent]`
  invariant ∀ append into a view's delta buffer: the pinned block behind it is still MAPPED, whether or not that view has been disposed
  never freeing the block inside `ViewBase.Dispose`. The publisher reads `IsDisposed` and then writes 24 bytes through raw pointers; those are two
        steps with nothing sequencing them, and `Thread.SpinWait(100)` — ~200 ns — is not a happens-before edge. Reasoning about the DURATION of an
        append against the duration of a spin is not reasoning about ordering
  never nulling the buffer's pointers on the retire path. A null base in unsafe pointer arithmetic is an access violation, not a catchable
        NullReferenceException — leaving them valid is the entire mechanism, not an oversight
  enforce `ViewBase.Dispose` deregisters from every registry FIRST, then calls `ViewDeltaRingBuffer.Retire`, which hands the block to the engine's
          `ViewBufferReclaimer` with a stamp from `EpochManager.BumpEpochForRetire`. A late publisher then writes into a ring nobody will drain, in
          mapped memory that is still owned. Harmless by construction rather than by exclusion
  enforce the stamp is taken AFTER the deregistration. That ordering carries the whole safety argument: a publisher whose registry read preceded the
          deregistration necessarily pinned an epoch below the stamp, and one that pinned above it cannot have read the registration at all
  enforce `ViewBufferReclaimer.Drain` frees only blocks whose stamp is at or below `EpochManager.MinActiveEpoch`
  enforce the Dekker pair this rests on: `EpochThreadRegistry.PinCurrentThread` stores the pin SEQ-CST (`Interlocked.Exchange`) and
          `BumpEpochForRetire` is an `Interlocked.Increment`, with `ComputeMinActiveEpoch` acquire-reading each slot. Release alone is NOT enough —
          the reordering that breaks it is StoreLoad, which x64 also permits, so this is not an arm64-only obligation
  enforce no epoch refresh between reading a view registration and the last append through it. All publish sites satisfy this today; a
          `RefreshPinnedEpoch` inside a publish pass would raise the thread's floor above a stamp it is still exposed to
  scope: ViewBase.Dispose, ViewDeltaRingBuffer.Retire, ViewDeltaRingBuffer.BlockIsLive, ViewBufferReclaimer.Retire, ViewBufferReclaimer.Drain,
         EpochManager.BumpEpochForRetire, EpochThreadRegistry.PinCurrentThread, EpochThreadRegistry.ComputeMinActiveEpoch
  on_violation: `TryAppend` writes 24 bytes through a pointer into freed pinned memory. `PinnedMemoryBlock.Dispose` calls
                `NativeMemory.AlignedFree`, so the bytes are immediately re-issuable to any other allocation in the process. Silent heap corruption
                with no exception and no trace back to the view.
  rationale: #864. #790 did not invent this race — `ViewRegistry`'s field-channel publishers have the same unguarded shape and predate it — but it
             changed WHICH views have a producer: before it a plain `Query<T>().ToView()` registered with nothing, so disposing one was race-free by
             construction, and the race needed the narrower `WhereField` shape. One mechanism now covers both channels.
             An earlier attempt excluded the disposer with a shared/exclusive latch instead. It was withdrawn: it cost the publisher a synchronised
             acquire on a path measured at ~15.6 ns per append, it had to be hoisted out of loops the field-channel sites are buried three deep in,
             and on exclusive-acquire timeout it waited a full `DefaultCommitTimeout` PER ARCHETYPE and then freed the buffer unlatched anyway —
             trading a memory-safety hazard for a liveness hazard while keeping both. Deferral costs the publisher nothing and `Dispose` never waits.
  note the free is deferred, so memory is held for the length of the longest epoch scope live at retire time — the same trade ADR-033 and ADR-035
       already accept. `ViewBufferReclaimer.PendingCount` / `PendingBytes` / `FreedTotal` surface it, because a cost nothing reports is a cost nobody
       can diagnose.
  verified: SystemInputViewLivenessTests.ViewDisposedMidPublish_IsWrittenThroughLiveMemory_AndFreedOnlyAfterTheEpochPasses — disposes the view from
            `QueryPathProbe.PrePublishAppendHook`, i.e. INSIDE the window, and asserts the block is mapped at the append and freed only once nothing
            is pinned below the stamp. Deterministic and single-threaded, which the latch design could not be: disposing from that hook under a
            shared latch was a self-deadlocking upgrade, which is why this rule previously read NOT COVERED.
            .FieldChannelView_DisposedMidPublish_IsAlsoWrittenThroughLiveMemory covers the other channel; .DisposingManyViews_NeverBlocks covers the
            liveness half; Memb04Verifier_RejectsAFreeAtDisposalTime is the mutant.

### MEMB-03: The epoch gate binds membership queries only, never every pull view `[fatal][silent]`
  invariant only a query whose result IS the whole live membership of its archetype set may take the channel or the structural-epoch gate
  never keying either on `ViewBase.IsPullMode`. That is `Evaluators.Length == 0`, which is true for THREE query shapes — archetype-only,
        `.Where(lambda)` and the spatial predicates — and only the first has membership that changes exclusively on spawn and destroy
  enforce the SUBSCRIPTION is gated by `EcsQuery.IsMembershipQuery` — no field predicates, no `_whereFilter`, no `_spatialQueryType`, no
          enabled/disabled constraint. `ViewBase.IsMembershipEligible` is a CONSEQUENCE of having subscribed, set only by
          `EcsView.SubscribeToMembership` alongside the registry array it dispatches on, never a precondition anything else may assert. Setting
          the flag from a second construction path without routing through that method is how the two fall out of step
  enforce a membership refresh falls back to the re-query when the refreshing transaction holds its own uncommitted spawns or destroys
          (`Transaction.HasPendingEcsWork`). The channel carries committed entries only and uncommitted work moves no epoch, so the gate would
          otherwise make the view contradict the transaction that refreshed it — `tx.Destroy(id); view.Refresh(tx);` still showing the entity
  enforce `TyphonRuntime.RefreshSystemInputViewsAtTickStart` keeps using `IsPullMode`, because `BIND-04` obliges it to drive all three shapes
  scope: EcsQuery.IsMembershipQuery, EcsQuery.ToPullView, ViewBase.IsMembershipEligible, ViewBase.IsPullMode, EcsView.RefreshMembership
  on_violation: a `.Where(lambda)` or spatial view is gated on a counter that does not move when its membership changes. An entity whose component
                write flipped the predicate, or which moved out of the query radius, produces no spawn and no destroy — so the view reads "nothing
                happened" and goes quietly stale. Usually right, silently wrong, which is strictly worse than the honest O(N) it replaced.
  rationale: #790 decision D2. The channel reports entities APPEARING and DISAPPEARING; it knows nothing about entities CHANGING. For "all ships"
             that is the complete story and for the other two it is a fraction of it.
  verified: SystemInputViewLivenessTests.WhereLambdaView_IsNotMembershipEligible_AndStillReQueries — asserts a lambda view takes neither the gate
            nor the drain and still re-queries, which is what stops a future "IsPullMode is close enough" simplification.
            MembershipRefresh_AgainstATransactionWithItsOwnPendingWork_SeesTheOverlay covers the second enforce clause.

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
  never a call site OUTSIDE the fence window reading the pair directly. Every such reader goes through
        `ArchetypeClusterState.ReadActiveClusterList` — `TyphonRuntime.ReadActiveClusterList` now only delegates to it — because the
        sites that already loaded count-first were right by ACCIDENT and nothing stopped the next one from being written either way.
        The exceptions are named rather than implied, because "every one" was false when this clause was first written and a rule that
        overstates its reach gets discounted wholesale:
          reopen / rebuild — `RebuildCellState`, `RebuildSpatialStateFromData`, `RebuildClusterAabbs` and
            `DatabaseEngine.RebuildClusterEntityMapEntries` walk the pair directly and may, because they run at open or on the crash-rebuild
            path, with no concurrent writer in existence
          fence-phase passes — `DormancySweep`, `TransitionWakePendingToActive`, `RecomputeDirtyClusterAabbsSlice`,
            `DatabaseEngine.SyncTransientSegmentToActive` and `DatabaseEngine.BuildLegacyScanChangeList` likewise, because
            EW-01's window excludes user-thread structural mutation for the fence's duration. Note that `RecomputeDirtyClusterAabbsSlice` is a
            PARALLEL slice, not a serial pass: what makes it safe is the window, not single-threadedness, and it clamps its end to the live count anyway
        CORRECTED 2026-09-17: this clause was FALSE when first written, at `EcsQuery.ScanClusterSoa` and `EcsQuery.ScanPerArchetypeBTreeSelective`.
        Both read the pair directly with plain loads, re-reading the count every iteration, on a query path that is neither reopen/rebuild nor
        fence-phase — so neither exception covered them. Count-first is the safe ORDER and x64 does not reorder loads, so they could not fault there;
        on arm64 the two plain loads may be satisfied out of order and reach the (new count, old array) quadrant this rule exists to remove. Both now
        hoist one `ReadActiveClusterList` out of the loop. Three further sites were exempt but UNNAMED, which is the same failure in a quieter form:
        the list above was written from the sites the fixing commit happened to touch, not from an enumeration of every reader. The enumeration is
        mechanical — `grep -rn ActiveClusterIds src/Typhon.Engine` outside `ArchetypeClusterState.cs` must return only comments, this rule's named
        exceptions, and null-guards that never index (`EcsQuery.TryCountViaOccupancy` tests the field for null before calling the reader, which cannot
        tear) — and that is the check, not a reviewer's memory.
  enforce `AddToActiveList` publishes the GROWN ARRAY with `Volatile.Write`, then the count with `Volatile.Write` — TWO
          releases, and the first is not redundant. A reader that acquires the OLD count and loads the NEW array derives no
          ordering from the count release at all, yet it walks entries `Array.Copy` wrote with plain stores; that quadrant is
          reachable and the array release is the only thing ordering it. `Array.Resize` cannot carry it — the `ref` assignment
          inside it is a plain store — so the grow is written out by hand.
          CORRECTED 2026-09-17: this clause said the grown array is stored PLAINLY and that holding it in a local is what must
          not be done. Both were written against `Array.Resize`, where there is no local to hold; under the hand-written grow
          the local is REQUIRED and is safe because it is reassigned to the grown array. What must not be done is narrower
          than the old text claimed: caching the array ACROSS the resize and appending afterwards, which puts the append in the
          copy `Array.Resize` abandoned — the regression `be05c594` fixed, and the reason this shape is spelled out.
  scope: ArchetypeClusterState.AddToActiveList / RemoveFromActiveList (writer), ArchetypeClusterState.ReadActiveClusterList
         (the one reader) and its callers — TyphonRuntime.ReadActiveClusterList, which now only delegates, and through it the
         dormancy promote, the checkerboard promote and the dispatch capture in OnParallelQueryPrepare (the chunk-partition sites
         read that capture, never the pair: CD-02), plus EcsQuery.TryCountViaOccupancy, EcsQuery.ScanClusterSoa and
         EcsQuery.ScanPerArchetypeBTreeSelective (the last two hoist it out of their scan loop), ClusterEnumerator.Create and
         ClusterEnumerator.CreateScoped (reached from the PUBLIC ArchetypeAccessor.GetClusterEnumerator), TierClusterIndex and
         StatisticsRebuilder — the last of these being the only reader on a thread no tick phase bounds
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
            NOT covered by any of them: that the array publication is a RELEASE rather than a plain store. All four assert
            single-threaded observations after the fact — the element is present, the prior elements survived, the count is
            within the array — and every one of those holds identically whether the array is published by `Volatile.Write`
            or by `Array.Resize`'s plain `ref` assignment. The clause is upheld by the enforce text and by review, not by a
            test, because x64 gives the same answer either way and the quadrant it protects needs a weak model to observe.
            Reverting the hand-written grow to `Array.Resize` would leave all four cases green.

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
         ArchetypeClusterState.IsClusterFullyVisibleAt, ArchetypeClusterState.EnsureClusterVisibilityCapacity,
         ArchetypeClusterState.FreeClusterHead, ArchetypeClusterState.ClaimSlotHeadReadProbe
  on_violation: `Count()` returns a number no scan agrees with, and the scans emit an entity that does not exist at the
                reader's snapshot. Silent both ways — every value looks plausible.
  rationale: found in review, not by tests. 5 300 tests pass with the fold on either side of the publish, because both
             states are momentary and both settle correct.
  enforce a claim resolves FreeClusterHead ONCE and uses the value it read. Re-resolving it lets a peer that filled the
          cluster store -1 between the two reads, and the loser claims into cluster -1 (#842) — which reaches the fold as a
          negative id, and reached it for weeks as an IndexOutOfRangeException that read as a grow failure (#807).
          NoteClusterBorn rejects a negative id by name so the next occurrence accuses the caller rather than the array.
  verified: ClusterVisibilitySummaryIntegrityTests.ClaimingASlot_BoundsTheClusterBeforeItPublishesTheOccupancyBit
            [VerifiesRule] — calls the claim and reads the summary with NOTHING in between, so the ordering is asserted
            single-threaded instead of raced for. Move the fold back to the caller and it fails every run, together with the
            from-scratch audit in the same fixture. AFoldAfterThePublishingStore_IsRejected [RuleMutant] drives that same
            assertion helper with the state a caller-side fold leaves and requires it to reject — the rule had a verifier and
            no mutant until then, which is the one shape the coverage audit cannot distinguish from real cover.
            APeerEmptyingTheFreeClusterHead_DoesNotMakeTheClaimResolveItTwice pins the single-read clause through
            ClaimSlotHeadReadProbe, which performs the peer's store at the vulnerable instant: raced, the defect reproduced
            about 3 times in 40, a rate at which a green run is not evidence. ANegativeClusterId_IsRejectedByName pins the
            message, since its whole value is naming the cause.

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
         EntityAccessor.ResolveSpawnAwarePayload, EntityAccessor.ShadowIndexedFields, EntityRefMut.Write,
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
  scope: EntityRefMut.Enable, EntityRef.IsVersionedSlotAbsent, EntityRef.ReadRaw, Transaction.CreateVersionedContentAndWrite,
         Transaction.AllocateVersionedSlotContent, Transaction.PublishNewVersionedChainRoots,
         Transaction.SpawnBatchAllocate, Transaction.SpawnBatchWriteAll, Transaction.FinalizeSpawns,
         ArchetypeClusterState.RebuildVersionedHeadFromChain, VersionedHeadRebuildSkips, DatabaseEngine.RebuildClusterFromChains,
         DatabaseEngine.CapturePreMigrationEnabledBits
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
  note: the migrating-open rebuild (`RebuildClusterFromChains`) re-places every entity and used to set a Versioned slot's
        bit from `head ≠ 0` — the forbidden derivation, re-enabling every disabled component on each schema migration
        (#846). It now takes presence from the head and the bit from the PRE-MIGRATION RECORD, snapshotted from the old
        EntityMap before the migration replaces it (`CapturePreMigrationEnabledBits`). The record, not the old cluster:
        the old cluster is kept only for archetypes with a SingleVersion slot, and its geometry is reconstructed, while
        the record exists for every archetype and its layout does not depend on component sizes or indexes. The snapshot
        keeps every entry, so a missing one means unknown, never all-enabled. Residual: when the old EntityMap cannot be
        loaded, when the persisted storage modes do not confirm the Versioned slot count its records were written at, or
        when an entity's entry was lost to a torn page, the rebuild falls back to the old cluster's bits where one was
        kept, and otherwise to `head ≠ 0`.
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
            not merely the transaction's cache),
            SchemaEvolutionStorageModeTests.Migration_KeepsAVersionedComponentsEnabledState_PresentDisabledAndAbsent
            [VerifiesRule] (all three states across a migrating open, on the record and the cluster copy),
            SchemaEvolutionStorageModeTests.Migration_OfTheVersionedComponentItself_KeepsItsEnabledState_WithoutASingleVersionSlot
            [VerifiesRule] (the same, where no pre-migration cluster is kept and the migrating component is the one under
            test), and
            NonGenericEntityAccessTests.ReadRaw_NeverSuppliedComponent_ReturnsAnEmptySpan (absent ≠ disabled for raw
            consumers).

---

## Module: ENABLE — The two copies of a cluster entity's enabled state

A cluster entity's enabled state is stored twice: the EntityMap record's 16-bit `EnabledBits`, which point reads and
queries resolve (with the MVCC overrides and the transaction's pending overlay), and one bit per component in the
cluster's `EnabledBits[C]` words, which bulk iteration reads raw and the open-time rebuilds treat as durable data. The
second copy is not derived on demand, so every path that changes one copy has to change the other (#847, #998, #846).

### ENABLE-01: The cluster EnabledBits mirror the committed record, and nothing earlier `[fatal]` `[silent]`
  invariant ∀ cluster entity e, ∀ component slot s, once the transaction that changed e's enabled state has published:
            bit(cluster(e).EnabledBits[s], slotIndex(e)) = bit(record(e).EnabledBits, s), where record(e) is the COMMITTED
            EntityMap record
  invariant a change reaches both copies at commit, on every path that commits one: `FlushPendingEnableDisable` for a
            live entity, `FinalizeSpawns` for a spawn, `RecoveryApplier` when the WAL is replayed
  invariant every other writer of the words keeps the mirror: a slot's bits are cleared when it is released
            (`ReleaseSlot` → `ClearSlotMetadata`; `ClearSlotBits` for an orphaned migrant's destination), and every CLAIM
            writes the occupant's full mask, clearing as well as setting — `FinalizeSpawns`, `ApplySpawnedEntityToCluster`,
            and a cluster migration's transcription to its destination slot (`ExecuteMigrations`, step 6). A freed slot is
            therefore never trusted to be clean. The migration rebuild writes the bits from the pre-migration record
            (`RebuildClusterFromChains`); the crash rebuild without a snapshot goes the other way and copies the cluster bits
            INTO the record (`RebuildClusterEntityMapEntries`), which is why the cluster copy has to be exact.
  never writing a slot the entity no longer occupies. The record a commit reads can be a TOMBSTONE — another transaction
        destroyed the entity and committed first, releasing the slot at commit — and a spawn may already hold that slot.
        The publish writes only while the slot's occupancy bit is set and its EntityIds tail holds this entity's id.
  never writing the cluster bit at staging. `EntityRefMut.Enable/Disable` stage the change and nothing else; a staged bit
        is visible to every concurrent bulk scan (the words are shared and unversioned) and nothing restores it on
        rollback.
  never a plain read-modify-write of an `EnabledBits` word on a live commit path. One word holds a component's bit for
        every entity of the cluster, so commits on DIFFERENT entities race on it; `Interlocked.Or` / `Interlocked.And`
        only. Plain writes are allowed exactly where no concurrent writer can exist: recovery and the open-time rebuilds
        (single-threaded), and the fence's cluster migration — the tick-fence window excludes every committing
        transaction (EW-01; an enable/disable commit writes the EntityMap), and the Migrate slices are carved on
        destination cell, so no two workers write the same destination cluster.
  scope: EntityRefMut.Enable, EntityRefMut.Disable, Transaction.StageEnableDisable, Transaction.FlushPendingEnableDisable,
         Transaction.PublishClusterEnabledBits, Transaction.FinalizeSpawns, RecoveryApplier.ApplySetEnabledBitsToExisting,
         RecoveryApplier.ApplySpawnedEntityToCluster, DatabaseEngine.RebuildClusterFromChains,
         DatabaseEngine.RebuildClusterEntityMapEntries, DatabaseEngine.ExecuteMigrations, ArchetypeClusterState.ClearSlotBits,
         ArchetypeClusterState.ReleaseSlot, ArchetypeClusterState.ClearSlotMetadata, ClusterRef.EnabledBits,
         ClusterRef.ActiveBits
  on_violation: bulk iteration sees a state the record does not hold. An uncommitted change reaches every concurrent
                scan, a rolled-back one stays for good, and a checkpoint persists it. The crash rebuild without an
                enabled-bits snapshot (`RebuildClusterEntityMapEntries`) then copies the cluster value INTO the record,
                and the migration rebuild (`RebuildClusterFromChains`) reads it as the authority for a Versioned slot's
                enabled state. Silent: point reads and queries use the record and look right throughout (#998).
  note: bulk iteration shows COMMITTED enabled state only. A transaction's own staged change is visible to its point
        reads and queries through the pending overlay, not through `ClusterRef` — the same treatment the cluster gives a
        Versioned write, whose HEAD is copied into the slot at commit.
  note: residual — two transactions changing the enabled state of the SAME entity is last-writer-wins on the record,
        with no conflict detection. Per-slot atomics cannot keep the cluster copy equal to the record under that race.
        The tombstone check above is not atomic with its write either. A publish that passes the check while a concurrent
        destroy is clearing the slot (`ClearSlotMetadata` clears the bits before the occupancy bit) can leave a bit on the
        freed slot; that bit is invisible (bulk iteration masks by occupancy) and the next claim overwrites it with the
        new occupant's full mask. What remains is a release, a claim AND the new occupant's spawn commit all landing
        between one publish's check and its write — a few instructions. Closing that needs the slot release serialised
        with the publish.
  verified: EnableDisableTests.RolledBackChange_LeavesTheClusterBitAtTheCommittedState [VerifiesRule] (rollback, both
            directions, explicit and by dispose), EnableDisableTests.StagedChange_ReachesTheClusterBitOnlyAtCommit
            [VerifiesRule] (not before commit, and at commit),
            EnableDisableTests.ACommitAgainstADestroyedEntity_LeavesTheSlotsNextOccupantAlone [VerifiesRule] (the tombstone
            check, with the slot's reuse asserted as a premise), EnableDisableTests.PureTransientArchetype_CommitPublishesTheClusterBit
            [VerifiesRule] (the TransientStore home), and
            LifecycleDurabilityBugTests.Issue847_EnabledChange_RecoveredFromWal_ReachesTheClusterSoA (the replay path).
            EnableDisableTests.ConcurrentCommitsInOneCluster_LoseNoBit guards the atomicity clause but is not counted as
            a verifier: a timing race can be caught, never forced.

## Module: ACCESS — Entity handles

An entity is reached by point access through a handle: `EntityRef` (read-only) or `EntityRefMut` (read + write). Which one a
caller holds decides what it may do, and the write paths rely on preconditions only a writable open establishes.

### ACCESS-01: A component write is reachable only from a writable open `[fatal]`
  invariant `EntityRef` has no member that writes, enables or disables: `Write`, `Enable` and `Disable` exist on
            `EntityRefMut` only, and `EntityRefMut` is produced only by `OpenMut` / `TryOpenMut` (on `EntityAccessor` and
            every subclass, `ArchetypeAccessor`, `BulkLoadSession`) — no public constructor, no conversion into it
  invariant ∀ writable open o on accessor A: o runs A's mutation prep BEFORE resolving the entity, found or not, on
            EVERY open — `PrepareOpenMut` (a `Transaction`: `EnsureMutable`, then `InProgress`); `ArchetypeAccessor`
            runs it inside the `PrepareForMutation` span on its first writable open and directly on every later one —
            and resolves the entity's cluster page with `GetChunkAddress(…, dirty: true)`
  invariant `EntityRefMut` is exactly one `EntityRef` field: resolvers reinterpret with `Unsafe.BitCast`, which throws
            on every writable open once the sizes differ (a wrap instead of the cast copies the ~200-byte handle,
            +10 ns per open, measured)
  invariant no defensive copy of a handle: every member of `EntityRef` / `EntityRefMut` other than `Write`, `Enable`,
            `Disable` is `readonly` (the read path's Versioned memo writes through `Unsafe.AsRef(in this)`), and a
            non-`readonly` member called on a read-only receiver is a compile error (analyzer TYPHON012)
  never a runtime access flag. Read-only-ness is the handle's TYPE; the check costs nothing and cannot be switched off.
        The conversion goes one way only: `EntityRefMut` → `EntityRef` (implicit, a copy).
  never a once-per-accessor prep: an `ArchetypeAccessor` taken before its transaction commits and used after would
        write in place into HEAD from a finished transaction.
  scope: EntityRef, EntityRefMut, EntityHandleDefensiveCopyAnalyzer, EntityAccessor.OpenMut, EntityAccessor.TryOpenMut, EntityAccessor.PrepareOpenMut,
         EntityAccessor.PrepareForMutation, Transaction.PrepareOpenMut, Transaction.PrepareForMutation,
         ArchetypeAccessor.OpenMut, ArchetypeAccessor.TryOpenMut, ArchetypeAccessor.PrepareMutation,
         BulkLoadSession.OpenMut, BulkLoadSession.TryOpenMut
  on_violation: before #997 one `EntityRef` served both kinds of open, told apart by a `_writable` flag checked only
                when `CheckConfig.Enabled` (off by default). With checks off, `Open(id).Write(...)` wrote in place into
                HEAD from a transaction that never ran `EnsureMutable` — a read-only or already finalized one included —
                and never moved to InProgress. The value was not lost (the fence's PS-10 backstop records the page); the
                transaction's own contract was.
  note: `Commit()` on a transaction that did nothing returns true and leaves it in `Created`, so it still accepts a
        writable open afterwards. That is the commit path's behaviour, not a gap in the prep.
  verified: EntityRefMutTests.EntityRef_ExposesNoWriteMember_EntityRefMutDoes [VerifiesRule] (the type split as an
            allowlist, no producer of `EntityRefMut` but the opens, no `_writable` left),
            EntityRefMutTests.EntityRefMut_IsExactlyOneEntityRef [VerifiesRule] (the BitCast layout),
            EntityRefMutTests.EveryNonWritingMember_IsReadonly [VerifiesRule] (no defensive copy on a read-only receiver;
            fails with one `readonly` removed, checked), EntityHandleDefensiveCopyAnalyzerTests (TYPHON012),
            EntityRefMutTests.WritableOpens_RunTheMutationPrep [VerifiesRule] (read-only and finished transactions refuse
            `TryOpenMut` / `OpenMut`, even for a missing id, on `Transaction` and `ArchetypeAccessor`; an
            `ArchetypeAccessor` reused after commit is refused — fails with a once-per-accessor prep, checked; a writable
            open moves the transaction to InProgress).
            TickFenceE2ETests.TickFence_SV_TryOpenMutWrite_SurvivesCheckpointAndReopen covers the end-to-end write but
            does not isolate the dirty resolve: since #1009 the fence records the page too, so it would pass without it.

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

## Module: VIEWLIFE — What a long-lived view may retain

An `EcsView` outlives the `Transaction` that built it: a caller may construct the view in a scoped setup transaction,
dispose it, and refresh against fresh transactions for the rest of the process. `Transaction` instances are pooled and
reset on `Dispose`, so anything a view keeps past construction has to be something whose lifetime is the database's,
not a lease's. `EcsView` holds its `EcsQuery` BY VALUE, which makes the query's transaction field the one place this is
easy to get wrong — the copy is invisible at the call site and outlives everything the caller can see.

### VIEW-01: A retained view query holds no transaction between operations `[correctness]`
  invariant ∀ view V, at every point where V is not inside one of its own snapshot-dependent operations:
            ¬V.RetainedQueryHoldsATransaction
  invariant ∀ operation that needs an MVCC snapshot: it binds the transaction it was HANDED and releases it before
            returning — `UpdateTransaction(tx)` → work → `DetachTransaction()` in a `finally`
  never reading routing or catalog metadata through the retained query's transaction. `routing id → ArchetypeMetadata →
        archetype id` is a property of the `DatabaseEngine`, not of any snapshot, so the view caches the engine (`_dbe`)
        at construction and the mask test takes it explicitly
  never rebinding at the top of `Refresh` and leaving it bound. That fixes the dereference and keeps the defect: the
        view then retains the most recent refresher instead of the creator, and correctness rests on every future
        `_tx` reader remembering to rebind first
  enforce every `EcsView` constructor calls `EcsQuery.DetachTransaction` on its own copy — the creator's lease ends at
          construction, not at first refresh
  enforce the four snapshot-dependent view paths — `RefreshPull`, `RefreshFull`, `RefreshFullOr`, `PopulateInitialOr` —
          bind and detach as a scoped borrow. The initial population in `ToPullView` / `ToIncrementalView` / `ToOrView`
          runs on the CALLER's query copy, which legitimately holds the creator, and is not covered by this rule
  scope: EcsView, EcsQuery.DetachTransaction, EcsQuery.UpdateTransaction, EcsQuery.HoldsTransaction,
         EcsQuery.MaskTestPublicByRouting, EcsView.RefreshPull, EcsView.RefreshFull, EcsView.RefreshFullOr,
         EcsView.PopulateInitialOr, EcsView.ProcessEntry, EcsView.ProcessEntryOr
  on_violation: the view dereferences a pooled object outside its lease. Observed as a `NullReferenceException` on the
                incremental drain when the creator stayed reset (#862), or as accidental correctness once the pool
                re-issued that object. The blast radius on the routing path was bounded — the pool is owned by the
                `DatabaseEngine`, so a recycled `Transaction` carries the same `DBE` and `GetMetaByRouting` answers the
                same — but bounded is a property of that one lookup, not of the escape. Any future `_tx` read on a view
                path inherits the escape and none of the bound.
  rationale: #862, and ADR-042's statement that a view holds no transaction is what this makes checkable. The benchmark
             `ViewFanOutProfile` had kept its creating transaction open for the whole run as a documented workaround,
             which is how a lifetime defect becomes a house style.
  verified: EcsViewCreatorTransactionLifetimeTests.RetainedQuery_HoldsNoTransaction_AcrossEveryViewShape [VerifiesRule]
            — asserts the invariant itself on all three shapes, after construction AND after a refresh, which is what
            the three regression tests beside it do NOT do: they assert the CONSEQUENCE (a view still refreshes once its
            creator is gone) and would stay green against a fix that merely rebound at the top of `Refresh`.
            Mutant_AQueryStillBoundToItsTransaction_IsReported is the mutant.
