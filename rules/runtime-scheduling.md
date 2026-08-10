# Runtime Scheduling Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-05-17 |
| Domain | DagScheduler, RuntimeSchedule, Track, Dag, AccessDagDeriver, SystemBuilder, Phase |

> Invariants codifying the auto-DAG model from RFC 07. These rules describe what must
> hold for system scheduling to be correct: phase resolution, access-conflict detection,
> derived edge construction, and runtime write validation.
>
> **Track → DAG hierarchy (#354).** Phases are **DAG-local**: each DAG declares its own ordered
> phase list, and phase resolution + access-conflict detection + edge derivation all run
> **per-DAG**. Cross-DAG `.After()` / `.Before()` edges are rejected at `Build()`. Where a rule
> below says "phase P", read it as "phase P within one DAG"; the cross-phase passes never span a
> DAG boundary.

---

## Module: Phase Semantics

> **ID prefix `PH-` (renamed from `PS-`, 2026-07-28).** `PS-` collided with the
> durability Page Safety module (`PS-01..PS-09`). Rule IDs must be unique across the whole database.

Phases form a **logical ordering contract**, not a runtime barrier. A system in phase N+1 is
guaranteed to observe the *committed* effects of any system in phase N that it has a derived
data-dependency on; if no such dependency exists, phase N+1 systems may run concurrently with
still-executing phase N systems on a free worker (cross-phase eager dispatch).

This is a deliberate change from the original design (07-system-access-declarations.md, Q-Phase-1
"all systems in phase N complete before any system in phase N+1 starts"): the all-to-all
bipartite cross-phase chain caused stragglers to gate the entire pool, costing 30-50% pool
utilisation on production traces (AntHill measured ~50% wait per worker).

### PH-01: Phases are ordering contracts, not barriers `[design]`
  invariant phase order P_a < P_b implies, for any (sys_a ∈ P_a, sys_b ∈ P_b) with a derived
            data dependency, sys_a.completion < sys_b.start
  invariant ¬(derived data dependency) ⟹ sys_a and sys_b may overlap in time
  scope: AccessDagDeriver.DeriveAndValidate cross-phase pass + DagScheduler dispatch
  rationale: phases stay as the human contract for "Sense after Lifecycle" and as the freshness
    contract for ReadsFresh / ReadsSnapshot WITHIN a phase; the runtime scheduler never sees
    phases — only the derived DAG edges.
  on_violation: pool utilisation regresses to barrier-mode levels; tooling DAG view
    misrepresents which systems can actually overlap.

## Module: Phase Resolution

The total-order skeleton for system scheduling. Every system lands in *some* phase.

### PR-01: Every system has a resolved phase index `[fatal]`
  invariant ∀sys ∈ scheduler.Systems: sys.PhaseIndex >= 0
  note PhaseIndex is DAG-local — an index into the owning DAG's declared phase list (#354).
  scope: RuntimeSchedule.Build (per-DAG phase resolution), AccessDagDeriver.DeriveAndValidate
  enforced_by:
    - reg.PhaseSet == true → resolved via the owning DAG's phaseIndexMap[reg.Phase.Name]
    - reg.PhaseSet == false → resolved via the owning DAG's resolved default phase
    - resolution validates the DAG's default phase ∈ that DAG's phase list at Build() (early-fail)
  on_violation: AccessDagDeriver skips the system, breaking the total-order property;
    cross-phase edges fail to form; conflict detection is silently bypassed

### PR-02: Each DAG's phases form a total order, deduped, non-empty `[fatal]`
  invariant ∀ dag: dag.ResolvedPhases.Length > 0
            (a DAG that declares no phases is given a single implicit phase)
  invariant ∀ dag, ∀i,j: i ≠ j → dag.ResolvedPhases[i].Name ≠ dag.ResolvedPhases[j].Name
  scope: RuntimeSchedule.Build (per-DAG phase-index map construction)
  on_violation: ambiguous index resolution; cross-phase ordering breaks

### PR-03: Declared phase must exist in its DAG's phase list `[fatal]`
  pre  reg.PhaseSet == true
  post the owning DAG's phaseIndexMap.ContainsKey(reg.Phase.Name)
  scope: RuntimeSchedule.Build (per-DAG phase resolution)
  on_violation: thrown immediately at Build() with the offending system + phase name

---

## Module: Access Conflict Detection

Build()-time guarantees against silent races. All run in `AccessDagDeriver.DerivePhase`.

### AC-01: Same-phase W×W requires explicit ordering `[fatal][silent]`
  invariant ∀ phase P, ∀ component T:
            (writers_of_T_in_P.Count > 1) →
            (∀ pair (a, b) ∈ writers: explicitEdges contains EXACTLY ONE of (a→b) ⊕ (b→a))
  note corrected 2026-07-27: the relation is exclusive-or, not or — declaring BOTH directions is also rejected
       (cycle error). Direct adjacency only: A.Before(B).Before(C) does NOT resolve the (A,C) pair, so with 3+
       writers every pair must be directly ordered.
  scope: AccessDagDeriver.DerivePhase
  on_violation: thrown at Build() with names of conflicting systems + suggestion

### AC-02: Same-phase R×W with plain Reads<T> is forbidden `[fatal][silent]`
  invariant ∀ phase P, ∀ component T:
            ¬(∃ reader: Reads<T> ∈ reader.Access ∧ ∃ writer: Writes<T> ∈ writer.Access ∧ both in P)
  scope: AccessDagDeriver.DerivePhase
  on_violation: thrown at Build(); reader must upgrade to ReadsFresh<T> or ReadsSnapshot<T>

### AC-03: Resource W×W requires explicit ordering `[fatal][silent]`
  invariant ∀ phase P, ∀ resource_name R:
            (resource_writers_of_R_in_P.Count > 1) →
            (∀ pair (a, b): explicitEdges contains EXACTLY ONE of (a→b) ⊕ (b→a))
  note same exclusive-or and direct-adjacency-only corrections as AC-01 (2026-07-27)
  scope: AccessDagDeriver.DerivePhase
  same on_violation form as AC-01

### AC-04: ExclusivePhase forbids co-location `[fatal][silent]`
  invariant ∀ phase P: (∃ sys ∈ P: sys.Access.ExclusivePhase) → P.Count == 1
  scope: AccessDagDeriver.DerivePhase
  on_violation: thrown at Build(); user must remove ExclusivePhase or move other systems out

### AC-05: ReadsSnapshot requires a Versioned component `[fatal][silent]` (CM-04, issue #392)
  invariant ∀ system S, ∀ component T ∈ S.Access.ReadsSnapshot:
            StorageMode(T) == Versioned
  scope: RuntimeSchedule.Build (Phase 2a.5)
  rationale: a snapshot read freezes to MVCC history; SingleVersion (under either TickFence or Commit discipline)
             and Transient layouts have no history, so ReadsSnapshot has nothing to freeze to.
  on_violation: thrown at Build() naming the system + component; user must use Reads/ReadsFresh or make the component Versioned

---

## Module: Edge Derivation

Every same-phase access relationship that is *allowed* produces a derived edge.

### ED-01: ReadsFresh derives writer→reader `[correctness]`
  inputs ∀ T: writers_of_T_in_P, fresh_readers_of_T_in_P
  output ∀ (writer, reader) ∈ writers × freshReaders, writer ≠ reader: derived.Add((writer, reader))
  scope: AccessDagDeriver.DerivePhase
  semantic_meaning: reader sees this-tick value of T (after writer commits)

### ED-02: ReadsSnapshot derives reader→writer `[correctness]`
  inputs ∀ T: writers_of_T_in_P, snapshot_readers_of_T_in_P
  output ∀ (reader, writer): derived.Add((reader, writer))
  scope: AccessDagDeriver.DerivePhase
  semantic_meaning: reader sees previous-tick value (executes before writer commits new state)

### ED-03: Event producer→consumer same phase `[correctness]`
  inputs ∀ Q: producers_of_Q_in_P, consumers_of_Q_in_P
  output ∀ (p, c): derived.Add((p, c))
  scope: AccessDagDeriver.DerivePhase
  semantic_meaning: consumer drains queue after producer fills it

### ED-04: Resource R×W derives writer→reader `[correctness]`
  inputs ∀ R: resource_writers_in_P, resource_readers_in_P
  output ∀ (w, r): derived.Add((w, r))
  scope: AccessDagDeriver.DerivePhase
  note: resources have no Fresh/Snapshot distinction in v1; reader always sees writer's output

### ED-05: Cross-phase edges are conflict-driven, not all-to-all `[correctness]`
  ∀ (sys_a ∈ P_a, sys_b ∈ P_b) where P_a < P_b:
    derived.Add((sys_a.Name, sys_b.Name)) iff any of ED-05a..ED-05e holds
  scope: AccessDagDeriver.DeriveAndValidate (cross-phase pass)
  rationale: chaining every system in P_a to every system in P_b serialises stragglers across
    the pool. Conflict-driven edges preserve the "phase N+1 sees phase N effects" contract for
    systems that *actually* depend on those effects, while letting independent systems overlap.
  on_violation: either (a) over-constraint regresses utilisation to barrier behaviour, or
    (b) under-constraint surfaces as torn reads / lost-update bugs in cross-phase data flows.

### ED-05a: Cross-phase write→reader / write→writer (component) `[correctness]`
  ∀ (sys_a ∈ P_a, sys_b ∈ P_b, P_a < P_b), ∀ T:
    T ∈ sys_a.Writes ∧ T ∈ (sys_b.Writes ∪ sys_b.Reads ∪ sys_b.ReadsFresh ∪ sys_b.ReadsSnapshot)
    ⟹ derived.Add((sys_a, sys_b))
  semantic_meaning: sys_a's commit of T must happen-before any sys_b access that depends on T.
    For sys_b.ReadsSnapshot<T> with sys_a in an earlier phase: phase order forces "writer first",
    so the snapshot reader observes sys_a's this-tick value (not previous-tick). This is the
    documented semantic shift relative to the v1 design (see PH-01 rationale).
  scope: AccessDagDeriver.DeriveCrossPhase

### ED-05b: Cross-phase reader→writer (component) `[correctness]`
  ∀ (sys_a ∈ P_a, sys_b ∈ P_b, P_a < P_b), ∀ T:
    T ∈ (sys_a.Reads ∪ sys_a.ReadsFresh ∪ sys_a.ReadsSnapshot) ∧ T ∈ sys_b.Writes
    ⟹ derived.Add((sys_a, sys_b))
  semantic_meaning: sys_a (earlier phase) reads T must complete before sys_b (later phase) writes
    T, so sys_a does not race against the in-progress write. Edge points "reader-first" exactly
    as the within-phase ReadsSnapshot rule (ED-02), preserving the snapshot semantic across
    phases — sys_a sees the value as of the start of its read, not partial mid-write data.
  scope: AccessDagDeriver.DeriveCrossPhase

### ED-05c: Cross-phase event producer→consumer `[correctness]`
  ∀ (sys_a ∈ P_a, sys_b ∈ P_b, P_a < P_b), ∀ Q:
    Q ∈ sys_a.WritesEvents ∧ Q ∈ sys_b.ReadsEvents ⟹ derived.Add((sys_a, sys_b))
  semantic_meaning: identical to ED-03 but spans phases. Without this edge a consumer could
    drain the queue concurrently with a producer's write, missing or tearing events.
  scope: AccessDagDeriver.DeriveCrossPhase

### ED-05d: Cross-phase resource conflicts `[correctness]`
  ∀ (sys_a ∈ P_a, sys_b ∈ P_b, P_a < P_b), ∀ R:
    (R ∈ sys_a.WritesResources ∧ R ∈ sys_b.WritesResources) ∨
    (R ∈ sys_a.WritesResources ∧ R ∈ sys_b.ReadsResources) ∨
    (R ∈ sys_a.ReadsResources ∧ R ∈ sys_b.WritesResources)
    ⟹ derived.Add((sys_a, sys_b))
  semantic_meaning: resources have no Fresh/Snapshot variants in v1; any access combination
    that includes at least one writer must serialise. Edge always points earlier-phase →
    later-phase regardless of which side is the writer (phase order disambiguates).
  scope: AccessDagDeriver.DeriveCrossPhase

### ED-05e: Cross-phase explicit edges preserved `[design]`
  invariant explicit `.After("X")` / `.Before("X")` declarations spanning phases survive
            verbatim — they are never elided by the conflict-driven pass.
  scope: SystemBuilder.After/Before, RuntimeSchedule.Build (Phase 2 explicit-edge merge)
  rationale: explicit edges remain the escape hatch for non-access ordering constraints
    (e.g. "TierAssignment must run before MoveAll for spatial-grid invariants the engine
    cannot infer from declarations"). Cross-phase explicit edges are syntactically identical
    to within-phase ones and merge into the derived set unchanged.

### ED-05f: Cross-phase W×W needs no disambiguation `[design]`
  Two writers of T in different phases do NOT trigger the AC-01 hard error. Phase order is the
  disambiguator; the cross-phase edge from ED-05a serialises them in phase-index order.
  scope: AccessDagDeriver.DerivePhase (skip W×W check across phases — only same-phase pairs)

---

## Module: Debug-Runtime Write Validation

Compile-time stripped in RELEASE; active in DEBUG to catch declaration drift.

### DV-01: Write<T> requires declared Writes<T> or SideWrites<T> `[strict-mode][opt-in]`
  pre  EntityRef.Write<T>() called from inside dispatched system body
  pre  the check is ENABLED — it is gated on CheckConfig.DeclaredAccessActive, a static readonly bool read from
       configuration key `Typhon:Checks:DeclaredAccess`, which defaults to FALSE (including in Debug builds)
  invariant when enabled: SystemAccessValidator.Current is set to the executing system's descriptor
  invariant typeof(T) ∈ descriptor.Writes ∪ descriptor.SideWrites OR descriptor.HasAnyDeclaration == false
  scope: SystemAccessValidator.AssertWrite, EntityRef.Write
  on_violation: throws InvalidAccessException with system name + undeclared type + declared set
  release_behavior: available in Release; when the gate is off the JIT constant-folds the branch away — zero overhead
  rationale: 🔴 CORRECTED 2026-07-27. This rule was tagged [debug-only] and claimed `[Conditional("DEBUG")] strips the
    call site`. Neither is true: there is no Conditional attribute, the mechanism is runtime strict mode (#422), and it
    is OFF by default — so the old text implied Debug test runs enforce this when they do not. Field name was `_current`;
    the actual field is `Current`.

### DV-02: Per-thread descriptor isolation `[fatal]`
  invariant the [ThreadStatic] descriptor is set/cleared deterministically around each system invocation
  scope: DagScheduler dispatch sites (7 wrap points — corrected 2026-07-27, was stated as 5)
  on_violation: descriptor leaks between systems → false positives or missed violations

### DV-03: Push/pop pairing `[fatal]`
  invariant ∀ EnterSystem(d, n): paired with a parameterless LeaveSystem() in finally
  note corrected 2026-07-27 — LeaveSystem takes no argument; the saved frame lives on the thread-static frame stack
  scope: DagScheduler dispatch wrappers
  on_violation: descriptor stays set after system exits, leaks into next system's execution

---

---

## Module: Tick Phase Ordering

### TP-01: Tick phases run fence → flush → output `[fatal]`
  invariant within one tick: WriteTickFence completes, THEN the UoW flush, THEN the output/subscription phase
  never flush before the fence — the fence publishes WAL records for the tick's dirty cluster content and runs the
        migration fence, so running it first is what lets the flush wait on a currentLsn that already covers them
  never run output before the flush — output requires a complete ring buffer, this tick's dirty bitmap, and quiescence
  scope: TyphonRuntime tick loop (WriteTickFence → flush → output)
  on_violation: inverting fence and flush makes migration writes durable only at the NEXT tick's flush — a persistent
    cluster mutation acknowledged this tick can be lost by a crash before the next one
  rationale: the ordering is deliberate (issue #229) and the code documents it inline, but no rule stated it. A design
    doc had the order backwards with nothing in the rule database to contradict it.

### TP-01a: The fence and the flush are mandatory on EVERY tick `[fatal][silent]` (issue #567)
  invariant WriteTickFence and the UoW flush run on every tick, including a tick aborted by a fatal system exception
  never skip WriteTickFence to "avoid establishing a durability boundary" — that reasoning is inverted, see below
  may the output/subscription phase be suppressed for a tick — it is the ONLY one of the three that may be skipped,
      because publication is the only tick-end act carrying tick-wide "this was a good tick" semantics
  scope: TyphonRuntime.OnTickEndInternal; RuntimeOptions.SystemExceptionPolicy = AbortTickAndStop
  on_violation: SingleVersion / Transient writes are made IN PLACE into cluster pages and receive their WAL record at
    the fence. Skipping the fence leaves the page mutated, dirty and un-logged; the checkpoint thread then persists it
    on its own schedule, producing a durable mutation with no WAL record behind it. CK-02's WAL-before-data ordering
    does not help — it flushes to the global high-water LSN and there is no record covering this page to order
    against. This is exactly AP-01's failure mode: "a checkpoint can capture never-durable state → phantom data
    after crash". Skipping the fence therefore CREATES an illegal durability boundary rather than avoiding one.
  rationale: issue #567 requested skipping fence+flush so a failed tick would not become a durability boundary. The
    engine relied on the fence being unconditional but nothing said so, and the request was reasonable from outside.
    Recorded so a future "abort the tick" variant cannot re-derive the same wrong conclusion. See
    design/Runtime/08-strict-tick-abort.md §"Why the fence must still run".

### TP-02: Parallel cluster dispatch binds to the system's own view archetype `[fatal][silent]`
  invariant a system's cluster-range dispatch binds to the ArchetypeClusterState of THAT system's queried archetype,
            never the first cluster-eligible archetype found globally
  invariant the ActiveClusterCount > 0 guard is load-bearing and must not be removed as dead code — binding a cluster
            state switches the system to cluster-RANGE dispatch, which walks ActiveClusterIds and IGNORES view-level
            filtering
  scope: TyphonRuntime parallel-dispatch binding, ViewBase.QueriedArchetypeId / EcsView override
  on_violation: bound to an archetype with MORE clusters → page-index throw every tick and successors DependencyFailed;
    with FEWER → the system silently processes a SUBSET of its own entities. Removing the guard double-processes every
    entity in a tier-filtered or dormancy-filtered view.
  rationale: fixed in #566; before that the binding took the first cluster-eligible archetype globally. Recorded so a
    cleanup does not reintroduce either half.

## Module: API Contract Stability

### AS-01: `.After()` / `.Before()` survive auto-DAG `[design]`
  invariant: explicit edge declarations remain functional
  rationale: needed for W×W disambiguation (AC-01), explicit pure-ordering escape hatch
  scope: SystemBuilder.After, SystemBuilder.Before, RuntimeSchedule.Build (Phase 2)

### AS-02: Backwards compatibility for non-fluent callers `[design]`
  invariant: pre-RFC code calling `b.Name(...); b.After(...);` (return value discarded) compiles unchanged
  rationale: SystemBuilder methods now return `this`; old callers ignore the return value transparently

---

## Cross-references

- Implementation: `design/Runtime/07-system-access-declarations.md` (private knowledge base — named, not linked)
- Related rules: [`durability.md`](./durability.md) (the WAL/checkpoint pipeline runs orthogonal to scheduler concerns)
- Source files (two-namespace split — both `public/` and `internals/` folders sit under the flat namespaces `Typhon.Engine` (public) and `Typhon.Engine.Internals` (internal); TYPHON008 enforces accessibility-vs-namespace alignment, not per-subsystem namespaces):
  - `src/Typhon.Engine/Runtime/public/Phase.cs`
  - `src/Typhon.Engine/Runtime/public/Track.cs` / `Dag.cs` (#354 Track → DAG hierarchy)
  - `src/Typhon.Engine/Runtime/public/SystemAccessDescriptor.cs`
  - `src/Typhon.Engine/Runtime/public/RuntimeSchedule.cs` (Build orchestration — per-DAG resolution)
  - `src/Typhon.Engine/Runtime/public/DagScheduler.cs` (dispatch wrappers)
  - `src/Typhon.Engine/Runtime/internals/AccessDagDeriver.cs`
  - `src/Typhon.Engine/Runtime/internals/SystemAccessValidator.cs`

---

## Module: BIND — Parallel system ↔ archetype binding

A parallel `QuerySystem` is bound to one `ArchetypeClusterState` at runtime construction. That binding decides two things at
once: whether the system takes cluster-RANGE dispatch, and whether the #327 per-(system, archetype) touch rollup can emit at
all. Both failures are silent.

### BIND-01: A permanent binding is never decided by a transient condition `[fatal]` `[silent]`
  invariant the binding is resolved from facts that cannot change after construction — the system's input-view archetype and
            whether that archetype is cluster-eligible. Population is NOT such a fact.
  never gating the binding on `ActiveClusterCount > 0`. It is a runtime population count read once, in the constructor; an
        application that builds its runtime before loading its data — the order every sample and every capture harness uses —
        is then unbound for the life of the session.
  enforce bind whenever `ArchetypeClusterState != null`; both dispatch paths already read the count LIVE per tick
          (`PrepareFullNonVersioned` → 0 chunks → `EmptyInput` skip; `ExecuteChunkWithAccessor` → an empty range), so an
          archetype empty at construction and empty forever behaves exactly as before.
  scope: TyphonRuntime.ResolveChangeFilters (construction), TyphonRuntime.BuildTierIndexesAtTickStart (late recovery),
         TyphonRuntime.SystemArchetypeIdOf
  on_violation: the system silently falls back to materializing a per-entity id list from its view instead of taking
                cluster-range dispatch — a permanent performance regression on precisely the storage mode cluster dispatch
                exists for — and gate 1 of the touch rollup stays shut, so the Workbench Data Flow panel is empty forever.
  rationale: #631. A bound state whose count is zero was ALWAYS reachable — nothing un-binds a system whose archetype is
             later drained — so requiring the count at bind time was checking a condition the rest of the code already
             tolerates.
  verified: SystemArchetypeTouchTests.ParallelSystem_OnAnArchetypePopulatedAfterConstruction_StillBindsToIt [VerifiesRule],
            at 1 and 4 workers. Asserts the BINDING only, deliberately: the obvious follow-on — that the system then walks
            cluster ids — cannot be asserted in this ordering, because the input view was necessarily built before the spawns
            and an unfiltered pull view is frozen at construction (#718).

### BIND-02: A system binds to ITS OWN view's archetype `[fatal]`
  invariant every resolution site derives the archetype from `_systemViews[i].QueriedArchetypeId`, never from a scan of the
            global registry
  never taking "the first cluster-eligible archetype found". Correct only in a world with exactly one, which is why it
        survived so long.
  enforce both sites resolve by id and require `IsClusterEligible`; both set `_systemArchetypeIds[i]`, not just
          `_systemClusterStates[i]` — a system bound without its archetype id keeps the touch rollup shut
  scope: TyphonRuntime.ResolveChangeFilters, TyphonRuntime.BuildTierIndexesAtTickStart
  on_violation: the system receives ANOTHER archetype's cluster ids — a page-index-out-of-range throw when the counts
                differ, silent double- or zero-processing when they happen to match.
  rationale: #662, twice. The construction site was fixed; an identical copy survived in the late-spawn recovery path and
             was found only because #631 sent someone back to the same file. A rule stated once for one call site is a rule
             enforced at one call site — the same lesson IXS-03/IXW-01 record for readers and writers.
  requires BIND-01 (the recovery path exists only because the binding could fail at construction)

### BIND-03: The entity count a system reports is the one it processed `[silent]`
  invariant `SystemTelemetry.EntitiesProcessed` equals the entities the system's dispatch actually covered this tick
  scope: TyphonRuntime.PrepareFullNonVersioned / PrepareFilteredNonVersioned / PrepareVersionedFallback / OnSystemStartInternal,
         TyphonRuntime.EmitSchedulerSystemArchetypeIfActive (gate 2), DagScheduler.InspectorChunkEnd, tier-budget rollup
  on_violation: under-report ⟹ the touch rollup, the tier-budget rollup and the Execution Inspector all silently flatten to
                zero. Over-report ⟹ worse: the Data Flow panel shows numbers nobody did, which is not distinguishable from
                real work by anyone reading it.
  rationale: #631 hypothesised that this was structurally 0 for cluster-native parallel systems — that gate 1 selected
             exactly the systems for which gate 2 could never pass. Measured and REFUTED: both gates pass together, and the
             count is exact. Recorded as a rule anyway because the hypothesis was plausible, and the only reason it is known
             to be false is that someone finally asserted on the number.
  verified: SystemArchetypeTouchTests.ParallelClusterNativeSystem_ReportsTheEntitiesItProcessed [VerifiesRule], at 1 and 4
            workers, asserting EQUALITY with the entities the walk visited rather than `> 0`.

### BIND-04: A system's input View reflects membership as of the tick it runs in `[fatal]` `[silent]`
  invariant for every system with an input View, the entity set the system dispatches over is the query's result as of THIS
            tick, not as of the moment the View was constructed
  never a View passed as `input:` whose membership is never re-evaluated after construction
  enforce `TyphonRuntime.RefreshSystemInputViewsAtTickStart` re-queries every PULL-mode system input once per tick, on the
          scheduler thread, BEFORE the tier-index rebuild and before any dispatch — both read the entity set, so a set
          refreshed after them is a set they do not know about. Incremental views are excluded because they already receive
          spawns and destroys as ViewRegistry deltas, and draining their ring buffer here would consume entries the
          per-system consumption path expects to still be there. A View shared by two systems refreshes once.
  scope: TyphonRuntime.RefreshSystemInputViewsAtTickStart, ViewBase.IsPullMode, ViewBase.LastSystemInputRefreshTick,
         EcsQuery.ToPullView, EcsView.RefreshPull
  on_violation: no system ever processes an entity spawned after startup. Silent — the system runs every tick and reports a
                plausible entity count, while the missing entities are committed, durable and queryable by everything else.
  rationale: #718. `ToView()` has two modes and only one is live: with a `WhereField` predicate it subscribes to the
             ViewRegistry, without one — the plain `Query<T>().ToView()` every sample and every doc uses to mean "all
             entities of this archetype" — it registers nothing. The API rewarded NARROWING with correctness. It is also
             #631's actual cause: a harness that builds its views before loading its data reports zero entities processed on
             every tick, and the Data Flow panel is then honestly empty.
  note the cost is an O(N) re-query per pull system input per tick, against O(deltas) on the incremental path, plus the set
       `EcsQuery.Execute` allocates. This is direction 2 of the three the issue lists — correct for every pull view including
       the `.Where(lambda)` and spatial ones. Direction 1, a lifecycle-level channel views subscribe to BY ARCHETYPE, is the
       endpoint and keeps the O(1) prepare; it needs a design, because ViewRegistry is keyed per indexed FIELD and an
       unfiltered query has no field to register under.
  note a pull View held by USER code and never refreshed is still a snapshot. This rule binds system inputs only, which is
       why `ViewCreatedBeforeTheSpawns_ConvergesWithOneCreatedAfter` stays quarantined — against #722, not #718: the parent
       closed with this rule's half shipped, and a suppression may only cite an OPEN issue (the #703 lint enforces exactly
       that, and caught the stale reference on main within minutes of the merge).
  verified: SystemInputViewLivenessTests.SystemInputView_SeesEntitiesSpawnedWhileTheRuntimeIsRunning — spawns while the
            runtime is ticking, which no fixture anywhere did before, and asserts the system sees 20 rather than 10.
  requires BIND-01 (a system with no input View has no membership to keep fresh)
