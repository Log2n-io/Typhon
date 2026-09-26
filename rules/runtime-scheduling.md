# Runtime Scheduling Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-09-15 |
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

## Module: EQ — Event queue production

### EQ-01: One worker slot, one producer `[fatal][silent]`
  invariant ∀ event queue Q, ∀ worker slot w: at most ONE thread appends to Q.segment[w] at any instant
  enforce a producer reaches a segment only through `TickContext.Writer(queue)`, which supplies the caller's
    `TickContext.WorkerId`. The slot-taking overloads are `internal` precisely so no caller can name another
    worker's slot.
  never cache a segment's buffer across pushes. Two live writers on one slot leave one holding an orphaned array
    after the other grows it; a later `Drain` drops `Count` below the stale length and the orphan then accepts
    writes, losing the event and re-delivering a consumed one. Slot disjointness (rule #860: `[0, WorkerCount)` are pool workers, `WorkerCount` is the
    dispatcher) is what makes the segment's non-atomic `Count`/`Produced` increments correct.
  never index a segment by `ChunkIndex`. Oversubscription (`ChunksPerWorker > 1`) makes `ChunkIndex >= WorkerCount`,
    and two chunks of one system routinely run on one worker — the same trap `_partitionViews` documents.
  never produce from a lifecycle-hook context. `OnFirstTick` / `OnShutdown` carry `TickContext.NonWorkerId`, own no
    segment, and `GetWriter` throws rather than aliasing slot 0.
  rationale: the pre-#861 implementation was a single `_buffer[_count++]`, and `AntUpdateSystem` — declared
    `.Parallel().ChunksPerWorker(2f)` AND `.WritesEvents(...)` — drove it from every chunk worker. Shipping.
    A count-only assertion does NOT catch this: two racing increments can yield the right total while one event is
    lost and another slot is written twice. Assert exact multiset equality after a drain.
  scope: EventQueue`1.GetWriter, EventWriter`1.Push, TickContext.Writer
  verified: EventQueueConcurrencyTests.ParallelProducer_EveryEventArrivesExactlyOnce [VerifiesRule],
    EventQueueConcurrencyTests.LifecycleHookContext_CannotProduce [VerifiesRule],
    EventQueueTests.SecondWriterOnASlot_SeesAGrowthPerformedByTheFirst [VerifiesRule] — the stale-buffer aliasing case
  on_violation:
    two workers sharing a slot → lost events AND duplicated slots, silently, with a plausible total
    indexing by ChunkIndex → IndexOutOfRange under oversubscription, or slot aliasing when chunks share a worker

### EQ-02: The consumer fences; the producer does not `[fatal][silent]`
  invariant ∀ Q: every consumer-side fold of segment state (`Drain`, `Count`, `IsEmpty`, `OverflowCount`) issues an
    acquire barrier BEFORE its first segment load
  enforce `EventQueue`1.AcquireSegments` — `if (!X86Base.IsSupported) Interlocked.MemoryBarrier()`, JIT-folded to
    nothing on x64.
  never rely on the DAG completion barrier alone. That was the original claim here and it is WRONG: the barrier is
    decremented with `Interlocked` but SPUN ON with a plain load (`while (_systemsRemaining.Value > 0)`), so an arm64
    reader may sink its segment loads above it. CLAUDE.md states the general form — an acquire load does not stop
    earlier plain reads from sinking below it.
  never add a release store to the push path to compensate. One fence per read is O(reads); a release per push is
    O(events), and avoiding exactly that is why the queue is segmented.
  rationale: producers 2..N issue no ordering store at all — `MarkProduced` fires only on a segment's 0 -> 1
    transition — so nothing on the write side publishes them.
  scope: EventQueue`1.Drain, EventQueue`1.Count, EventQueue`1.IsEmpty
  on_violation: a stale per-slot Count folds to 0 -> the consumer is marked EmptyInput and the tick's events are
    discarded by the next Reset, silently

### EQ-04: One consumer per queue `[fatal][silent]`
  invariant ∀ Q: at most one system declares `ReadsEvents(Q)` / `Consumes(Q)`
  enforce rejected at schedule build in `RuntimeSchedule.Build`.
  rationale: two consumers get no derived edge between them (ED-03 only relates producers to consumers), so both flip
    ready on the producer's completion and race inside `Drain` — overlapping `CopyTo` of the same prefix delivers the
    same events twice, both store `Count = 0`, and `Consumed +=` loses an update, which corrupts the derived
    `Produced` on the telemetry wire. Only the PARALLEL-consumer case was enforced before; cardinality was not.
  scope: RuntimeSchedule.Build
  verified: EventQueueValidationTests.TwoConsumers_AreRejectedAtBuild [VerifiesRule]

### EQ-05: `Capacity` is a construction constant `[correctness]`
  invariant `EventQueue`1.Capacity` returns the value passed to the constructor, never a fold over live allocations
  rationale: the profiler builds its one-shot `EventQueueRecord` catalog inside `TyphonRuntime.Create` — before the
    first tick, before any segment is allocated — and the Workbench divides per-tick depth by it. A fold over lazily
    allocated buffers reported 0 for every queue in every trace. Live allocation is `AllocatedCapacity`.
  scope: EventQueue`1.Capacity
  verified: EventQueueTests.Capacity_IsTheConstructionConstant_NotTheLiveAllocation [VerifiesRule]

### EQ-03: Overflow drops and counts — it never throws `[correctness]`
  invariant a `Push` that cannot be stored returns false and increments `OverflowCount`; it raises no exception
  rationale: `Push` runs inside parallel chunks, where an exception is caught into `_systemFailed` and, under
    `SystemExceptionPolicy.AbortTickAndStop` (#567), cancels the rest of the tick — a queue sizing mistake must not be
    able to stop the simulation. `OverflowCount` keeps its wire meaning ("events were lost"): the Workbench DAG paints
    an edge deep red on `overflowSum > 0` and says so in its legend.
  never truncate on the DRAIN side to match. A short destination span throws: silent truncation is event loss that
    `OverflowCount` does not count and the Workbench cannot show.
  scope: EventWriter`1.Push, EventQueue`1.PushSlow, EventQueue`1.Drain
  verified: EventQueueTests.Push_WhenAtCeiling_DropsAndCounts_NeverThrows [VerifiesRule],
    EventQueueTests.Drain_IntoAShortSpan_Throws [VerifiesRule]

## Module: Debug-Runtime Write Validation

A runtime strict-mode check, opt-in in every build (see DV-01), that catches declaration drift.

### DV-01: Write<T> requires declared Writes<T> or SideWrites<T> `[strict-mode][opt-in]`
  pre  EntityRefMut.Write<T>() called from inside dispatched system body
  pre  the check is ENABLED — it is gated on CheckConfig.DeclaredAccessActive, a static readonly bool read from
       configuration key `Typhon:Checks:DeclaredAccess`, which defaults to FALSE (including in Debug builds)
  invariant when enabled: SystemAccessValidator.Current is set to the executing system's descriptor
  invariant typeof(T) ∈ descriptor.Writes ∪ descriptor.SideWrites OR descriptor.HasAnyDeclaration == false
  scope: SystemAccessValidator.AssertWrite, EntityRefMut.Write
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
  note AMENDED by SUB-02 (`rules/subscriptions.md`, #955): "output" splits into replication COMPUTE, which runs after the
    fence and before the flush on the Engine-Subscriptions track, and replication PUBLISH, which runs after the flush.
    That four-step form is the only reading: nothing remains that runs "output" as a single post-flush step.
  verified: SubscriptionsTrackTests.NormalTick_RunsFenceThenComputeThenFlushThenPublish [VerifiesRule]

### TP-01a: The fence and the flush are mandatory on EVERY tick `[fatal][silent]` (issue #567)
  invariant WriteTickFence and the UoW flush run on every tick, including a tick aborted by a fatal system exception
  never skip WriteTickFence to "avoid establishing a durability boundary" — that reasoning is inverted, see below
  may the output/subscription phase be suppressed for a tick — it is the ONLY one of the three that may be skipped,
      because publication is the only tick-end act carrying tick-wide "this was a good tick" semantics
  may a tick that does NOT RUN skip both — the terminal gates in DagScheduler.ExecuteCallbacks (tick abort, and fence failure since #890)
      return before TickStartCallback, so no UoW is created and no SV/Transient write happens. This rule's failure mode is mutate-then-skip;
      with no mutation there is nothing for a fence to cover. The damage a fence failure DOES leave is on its own tick, and stopping bounds
      it rather than undoing it.
  scope: TyphonRuntime.OnTickEndInternal; RuntimeOptions.SystemExceptionPolicy = AbortTickAndStop; DagScheduler.ExecuteCallbacks
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
  note the skippable "output" step is BOTH halves of replication and nothing else — compute and publish are skipped
    together and only together. Engine-tagged systems are exempt from the scheduler's tick-abort guard, which is what
    makes the fence run on an aborted tick, so the replication track does NOT inherit the suppression: it opts out
    itself, in every stage's ShouldRun.
  verified: SubscriptionsTrackTests.AbortedTick_StillFencesAndFlushes_ButNeitherComputesNorPublishes [VerifiesRule]

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

## Module: Tick Fence Exclusivity

The interval in which the tick fence owns the structures it maintains. Everything the spatial partitioning update
is allowed to do cheaply — and steps 4-7 of `design/Spatial/vdb-cell-grid-and-migration.md` are built on it —
descends from this one property.

### EW-01: The tick fence runs with no concurrent mutation of the structures it maintains `[fatal]` `[silent]`
  invariant ∀ t ∈ fence_window: ¬∃ thread ≠ fence_thread mutating (cluster B+Trees ∪ EntityMap ∪
            per-cell spatial index ∪ ClusterCellMap ∪ ClusterAabbs)
  invariant under TyphonRuntime the window opens at Scheduler.TickEndCallback, so every system has completed;
            a system's own Transaction is committed and disposed in its epilogue before the tick can end
  licences: within the window a writer of those structures may skip OLC version validation, the write latch,
            the B-link right-walk and the epoch guard, and may use plain reads and plain counter increments on
            disjoint partitions. This is the whole economic point of the window — roughly 15-20% on top of what
            batching alone buys — and none of it is safe if the invariant does not hold.
  never a side transaction that writes any of the structures above is committed while the window is open.
        TickContext.CreateSideTransaction returns an ORDINARY transaction (TyphonRuntime.CreateSideTransactionInternal
        -> DatabaseEngine.CreateQuickTransaction), so it CAN write an indexed field and therefore mutate a B+Tree, and
        its caller owns Commit and Dispose — nothing joins it to the tick. One that touches none of those structures is
        harmless, but the distinction is not checkable at the fence and not obvious at the call site, so the API states
        the conservative form: commit and dispose it before the creating system returns. It is the one path inside the
        runtime that can overlap the window.
  scope: DatabaseEngine.TickFence.cs (WriteTickFence, WriteClusterTickFence), TyphonRuntime.cs (OnTickEndInternal),
         TickContext.cs (CreateSideTransaction)
  rationale: the window is not built, it already exists — OnTickEndInternal is the scheduler's TickEndCallback.
    What was missing is that nothing STATED it, so nothing protected it: a licence nobody wrote down is one a
    later change silently revokes. Phases cannot supply this property and never could — see PH-01, which makes
    them ordering contracts rather than barriers, and ExclusivePhase, which is Build()-time validation that no
    other system shares a phase and says nothing about adjacent ones.
  host_mode: WriteTickFence is public and a runtime-less host drives it directly (demo/SpaceBattle
    TyphonHost.RunTickFence). For such a host this is a DOCUMENTED CALLER OBLIGATION, not an enforced one, and it
    is stated on the method's own XML doc. Trivially satisfied single-threaded; unchecked for a host that runs its
    own worker threads. Enforcing it would require the engine to track every application thread, which it does not
    and should not.
  on_violation: silent. A concurrent writer racing a fence that has taken the licences above corrupts index
    structure with no exception at the point of damage — the B+Tree's own validators find it later, or a query
    returns a wrong answer and nothing finds it at all.
  verified: ExclusiveWindowTests.LiveWorkload_TheFenceNeverSeesAForeignWriter — a spawn / destroy / indexed-field
            rewrite workload over 6+ ticks asserting zero foreign writers, and asserting ObservedFenceMutation too
            so a fence that wrote no guarded structure fails as loudly as one that was raced. Mutant:
            ForeignThreadMutatingAnIndexInsideTheWindow_IsCaught.
            The detector is ExclusiveWindow, an assertion at the MUTATION SITES: armed on the seven typed B+Tree
            mutators and the EntityMap's five, ALWAYS COMPILED (the merge gate runs Release, so a
            [Conditional("DEBUG")] guard is one the gate never executes), and scoped PER ENGINE off EpochManager
            rather than to a process-wide static — under the parallel fixtures a global would let one engine's
            fence indict another engine's legal write.
            Two cheaper proxies were tried in step 3 and rejected: TransactionChain.ActiveCount counts handles that
            exist rather than threads that are mutating, and reddens 21 tests that legitimately hold a
            committed-or-idle transaction across the fence — `using var tx = ...; tx.Commit();` before the scope
            ends, and the long-lived read transaction that owns a pull View. Asserting "no system runs
            concurrently" verifies the half that was never in doubt. A rule whose verifier cannot fail is worse
            than a rule with no verifier.

## Module: Chunk Dispatch

### CD-01: A chunk is claimed only from the dispatch it belongs to `[fatal]` `[silent]`
  invariant a system's claim word packs the live dispatch's chunk count (high 32 bits) and the next index (low 32 bits); a claim is
            ONE Interlocked.Increment, and it names chunk c of dispatch D only when c < D's count read from that same word
  invariant the word holds count 0 from ResetTickState; a dispatch publishes (count << 32) as its LAST store (OpenChunkClaims, a
            release after _remainingChunks and the prepared state); a completed dispatch leaves its word exhausted — every chunk was
            claimed — so it refuses every claim and nothing has to close it between two dispatches of one tick
  invariant a failed system's chunks are counted down one claim at a time by the ordinary claim loop (DrainClaimedChunk), the
            failure flag read after the claim — never by a loop that cached the dispatch's size
  invariant a claim is compared UNSIGNED against its count, so no number of increments on an exhausted word reads as a chunk
  invariant a parallel query's dispatch ends in CompleteParallelDispatch whether its last chunk ran or was drained: its cleanup runs
            once, and a failed system starts no further phase, whatever the cleanup asks for — the single-threaded path's rule. The
            drain used to complete a failed system without its cleanup (its entity list never returned, its checkerboard phase left
            behind), and a failed phase A still re-dispatched phase B
  invariant a prepare that throws fails THAT system (DispatchParallelQuery, #1063): the failure is recorded against it, its cleanup runs
            once, no further phase starts and it is completed, so its successors are skipped and the tick ends. It runs from its
            predecessor's completion (or the root marking), and escaping from there the worker's safety net blamed the predecessor —
            already complete — and the parallel system was never completed: the tick waited on it forever
  never a chunk index judged against a count read separately from its claim
  rationale: nothing fences workers out between dispatches. A parallel system's ready flag stays set once it completes, and a worker
    can be preempted anywhere in its claim loop and resume one or more dispatches later. With the count and the index held apart,
    three windows let such a worker run a chunk of a dispatch it was never part of: (1) the ready flag read in tick N and a counter
    that ResetTickState had put back to 0 read in tick N+1 — the finished dispatch's chunks re-run mid-tick, and their decrements
    complete the system a second time; (2) an increment past the end of one dispatch judged against the NEXT dispatch's larger count —
    a chunk run twice and the system completed one chunk early; (3) a drainer that had cached a failed dispatch's count swallowing
    the next dispatch's chunks, or hanging its tick. With one word a claim either belongs to the live dispatch — legitimate, whatever
    the worker did before — or answers "nothing left".
  on_violation: silent for an application system — a chunk re-run or skipped outside its dispatch. Observed on the fence through
    window 1: a stale FencePrep repair allocated a cluster, and a stale Finalize drain freed one, under the next tick's systems;
    CellClusterPool's single-writer detector aborted 5 of 40 SWG Tatooine x64/w16 runs on it, and a counting build logged ~2 stale
    claims in one ordinary run. A fence chunk also refuses to run with the fence window closed (FencePhaseExecSystemBase.Execute):
    that names fence work running outside its tick whatever the cause — one of these windows, or a worker left behind when
    shutdown abandons a stalled tick — but it cannot see a stale claim landing inside the NEXT fence window. The claim word is what
    prevents those.
  scope: DagScheduler.cs (ResetTickState, DispatchParallelQuery, OpenChunkClaims, FindReadySystem, ProcessParallelQuery,
         ProcessPipeline, DrainClaimedChunk, CompleteParallelDispatch, AbortSystemFromChunkZero, MarkTrackRootsReady, OnSystemComplete),
         FenceExecSystem.cs (ThrowOutsideFenceWindow)
  verified: ChunkClaimStragglerTests, one deterministic test per window, each reproduced on the pre-fix code —
            AWorkerCaughtMidScan_DoesNotRunChunksOfASystemTheNextTickHasNotDispatched (window 1: FindReadySystemProbe parks a worker
            between P's ready flag and its claim word in tick 1 and releases it in tick 2 before P is dispatched);
            AClaimPastTheEndOfOneDispatch_DoesNotRunAChunkOfTheNext (window 2: ClaimProbe parks a worker after a tick-1 claim past the
            end and releases it once tick 2's larger dispatch is live — chunk 4 ran twice);
            AClaimPastTheEndOfPhaseA_DoesNotRunAChunkOfPhaseB (window 2 within one tick: the same across the #234 checkerboard
            re-dispatch, which is what shows an exhausted word needs no close between two dispatches);
            ADrainerParkedInAFailedTick_DoesNotSwallowChunksOfTheNext (window 3: the worker that threw is parked before its next
            drain claim — 13 of tick 2's 16 chunks were swallowed). Mutant: ACounterOpenAcrossTheTickBoundary_IsCaughtByTheVerifier
            reopens P's finished dispatch's claims before the release. FenceWindowTripwireTests covers the tripwire, and
            ExceptionHandlingTests.AFailedParallelSystem_RunsItsCleanupOnce_AndStartsNoFurtherPhase the completion of a drained system,
            ExceptionHandlingTests.AParallelSystemWhosePrepareThrows_FailsItself_AndTheTickCompletes a prepare that throws (hung before).

### CD-02: A dispatch's chunks tile the cluster list Prepare counted `[fatal]` `[silent]`
  invariant the cluster ranges a parallel QuerySystem's chunks walk tile the list its dispatch splits exactly: chunk k of n walks its share of an
            equal split (ChunkClusterRange), the first (length mod n) chunks taking one cluster more, so every cluster is walked by exactly one chunk
  invariant the list and its length are read once, in Prepare (OnParallelQueryPrepare), and every chunk walks that array and splits that length,
            never the live pair: a spawn can append to an archetype's list while the chunks run (AddToActiveList, under its latch), and chunks that
            read two lengths do not tile. An append leaves the array's first entries as they are, even when it moves the list to a larger array.
            Clusters appended during a dispatch are walked from the next tick
  requires: CLUSTERWALK-01 (no Destroy commit on the archetype overlaps the walk: a removal swaps the last cluster into the hole, which no snapshot
            of the length protects against)
  on_violation: silent: a cluster walked twice (its entities updated twice, its queries counted twice) or not at all (a tick of work skipped for
    its entities), with nothing raised
  scope: TyphonRuntime.cs (OnParallelQueryPrepare, ChunkUnits, ChunkClusterRange, ExecuteChunkWithAccessor, ExecuteChunkWithTransaction)
  verified: ChunkClusterRangeTests.AListThatGrowsDuringTheDispatch_IsStillTiled (the first chunk spawns a new cluster before the next reads its
            range, on the accessor path and on the per-chunk Transaction path; against the code that split the live length it fails:
            "[0,18) [19,37)" for a list of 36 on both paths, the second chunk splitting the 37 clusters the spawn left). The change-filtered path reaches the
            same ChunkClusterRange but no test drives it
  note: no RuleMutant. Putting the live read back on the chunk path would take a seam there; the verifier was run against the code that did it,
        and failed as quoted

## Module: RT — Epoch scope around system bodies

### RT-01: Every system body runs inside an epoch scope, and must not block in it `[fatal]` `[silent]`
  invariant ∀ system S dispatched by the runtime: S's body executes with a live EpochGuard scope on the executing thread, whatever S's shape —
            a serial CallbackSystem / non-parallel QuerySystem gets it from the transaction OnSystemStartInternal creates (Transaction.Init calls
            EnterScope unconditionally); a parallel QuerySystem from the per-worker EntityAccessor (InitLightweight, for the accessor's lifetime);
            a ChunkedCallbackSystem from the dispatcher itself, once per chunk (ExecuteChunkedCallback); and a parallel QuerySystem that WritesVersioned
            from its per-chunk Transaction (ExecuteChunkWithTransaction) rather than from an accessor — four mechanisms, not three
  invariant a parallel QuerySystem's PREPARE — the runtime's own work before its chunks: the change filter's dirty scan, the tier, sleep and
            descendant materializations — runs inside a scope the runtime opens (OnParallelQueryPrepare, #1063). It reads cluster pages
            through ChunkAccessors like a body does, and outside a scope it read pages nothing protected (Debug asserted, which hung the tick)
  note the parallel-query accessor pins for the ACCESSOR'S lifetime and never exits inside the tick (EntityAccessor.InitLightweight, "No epoch exit
       here"): a standing pin rather than a scope. The guarantee holds, but this rule now makes that pin load-bearing for a public contract
  note the dispatcher's own EpochGuard.Dispose THROWS on a depth mismatch (EpochThreadRegistry.UnpinCurrentThread), so a body that leaks a scope —
       an undisposed Transaction, say — fails loudly here rather than corrupting reclamation silently. That is deliberate: swallowing it would hide
       epoch-depth corruption, which is worse than a loud failure, but it does put a throw on the tick path
  invariant the scopes NEST: EpochGuard.Enter increments a depth and only the outermost scope advances the global epoch, so a body that opens its
            own guard — or the fence, which opens one in its own Execute override — stays correct and costs one atomic pair
  never a system body that blocks — a lock held across I/O, a wait on another tick's work — because the scope pins an epoch for the body's whole
        duration and page eviction plus view-buffer reclamation wait on the oldest live epoch (PS-09)
  note the guarantee is scoped to a runtime WITH a live engine. ExecuteChunkedCallback runs the body unscoped when Engine or its EpochManager is
       null, because throwing an NRE on the tick path would be worse and this file guards Engine at ten other sites (TyphonRuntime.cs:588, :1064
       and the Engine?.SpatialGrid reads). That case is pre-#909 behaviour for this shape, not a regression — but it IS the one hole in the ∀, and
       a body that reaches it can take no page access safely
  requires: PS-02 (every page access sits inside an EpochGuard scope — this rule is how a system body satisfies it without saying so)
  rationale: ClusterSpatialQuery is PUBLIC and its enumerator builds a ChunkAccessor over cluster pages, so it needs the pages pinned. EpochGuard is
    internal and stays internal (a public RAII pin is the footgun PS-09 describes), so the guarantee has to come from the framework rather than from
    the caller. Before this rule, ChunkedCallbackSystem — also public — was the one shape the dispatcher gave no scope: a user could write that shape
    and then had NO legal way to call the public spatial query from it, because the only way to satisfy the documented precondition was a friend
    declaration. Making the dispatcher open the scope costs one Interlocked pair per chunk against a per-dispatch overhead already in the 10-30 µs
    range, and it is what lets the query's XML doc stop naming a precondition its caller cannot express.
  on_violation: a body reading cluster or component pages with no live scope can have those pages reclaimed under it mid-read — a torn read or a
    use-after-free, silent, and only under eviction pressure. The reverse violation, blocking inside the scope, is silent too: reclamation stalls
    behind the oldest live epoch and the page cache grows until something else fails
  scope: TyphonRuntime.cs (ExecuteChunkedCallback, ExecuteChunkWithAccessor, ExecuteChunkWithTransaction, OnParallelQueryChunk, OnParallelQueryPrepare),
         Transaction.cs (Init),
         ChunkedCallbackSystem.cs,
         EntityAccessor.cs (InitLightweight), EpochGuard.cs (Enter, Dispose), ClusterSpatialQuery.cs
  verified: EpochScopeAroundSystemBodiesTests — one test per mechanism, each asserting a live scope from inside the body:
            ASerialCallbackSystemBody_RunsInsideAnEpochScope, AParallelQuerySystemBody_RunsInsideAnEpochScope,
            AChunkedCallbackSystemBody_RunsInsideAnEpochScope (the last fails on the pre-fix dispatcher, which called CallbackAction with no guard), plus
            AChunkedCallbackBody_CanRunThePublicClusterSpatialQuery, the case the rule exists for;
            ParallelChangeFilterEpochTests.AParallelChangeFilterOverAnIndexedComponent_KeepsTicking_AndSeesItsChanges the prepare (red without its
            scope: the Debug assert, then the hung tick)

## Module: Worker Wake

Between dispatches every worker parks in a kernel wait. These rules say how a dispatch gets each one back.

### WK-01: Every dispatch wakes every parked worker `[perf]` `[silent]`
  invariant each worker parks between dispatches on its OWN event (_workerWake[workerId], one per worker, made by the DagScheduler constructor);
            every dispatch Sets every worker's event (WakeWorkers, called by DispatchTrackMultiThreaded), and so do Shutdown and Dispose
  invariant the generation is bumped BEFORE any Set — an Interlocked.Increment, a full fence — so a worker a Set wakes reads a generation at least
            as new as that Set's: the one it last joined only when the Set was stale, which the next invariant consumes. The dispatcher never Resets
            an event
  invariant only the owning worker Resets its event: after EVERY return from its Wait, and BEFORE it re-checks the generation, with a full barrier
            between the two (a store, then a load, which x64 reorders too; the barrier is explicit rather than left to Reset's own implementation).
            Never between the check and the Wait, where the Reset would swallow a Set that landed in between. Any Set the Reset clears then belongs
            to a generation the re-check sees
  invariant a worker starts from the generation Start found (_generationAtStart), not the one its thread first reads: a thread that first
            runs after the first bump still joins that dispatch
  invariant the backstop (BetweenTickWaitBackstop, 50 ms) is a shutdown-liveness net, not a way to be woken
  rationale: one event shared by the pool made every released worker re-take the event's lock to leave its Wait, a convoy: with 32 workers the
    median ran 293 µs after the Set and the last 5.9 ms. With an event each nothing queues, and a wake can only be lost through the orderings
    above
  on_violation: a worker sits a dispatch out until its backstop, or spins on a Set it has already seen until the next dispatch (the first
    per-worker cut did: 114 k loop iterations in one gap). It costs latency, not correctness: the dispatch completes on the other workers, only
    later. Silent: a lost wake the next dispatch's Set rescues leaves no trace (WK-02)
  scope: DagScheduler.cs (DagScheduler, WorkerLoop, WakeWorkers, DispatchTrackMultiThreaded, Start, Shutdown, Dispose)
  verified: WorkerWakeTests — EveryDispatch_WakesEveryWorker (two dispatches a tick, each chunk waiting until every worker is inside one);
            AWakeLandingAsAWorkerParks_IsNotLost (a worker held between the check and the Wait until the next dispatch has Set its event; it also
            fails, on no worker ever reaching the Wait with its event clear, when the Reset comes after the check instead of before it);
            AWokenWorker_FindsTheDispatch_BeforeTheLastSet (the dispatcher held before the last Set: a worker already Set must not park again;
            it sees a bump placed after the first Set, which the wake latency hides from every other test);
            AWakeAlreadySeen_IsConsumedNotSpunOn (a stale Set costs one round, not a spin, bounded in ticks);
            AWorkerThatStartsLate_JoinsTheFirstDispatch; Shutdown_WakesEveryParkedWorker. Mutant:
            ASetSwallowedBeforeTheWait_IsCaughtByTheVerifier (the held worker Resets its own event once the dispatch has Set it)
  note: hand-made mutants, each caught by its test: the last worker never woken, no Reset after a return, the generation read at thread start,
        Shutdown without a wake (when the per-worker wake landed); the bump after the Sets, and a second Reset just before the Wait (2026-09-14).
        No test pins the dispatcher never Resetting, the barrier after the Reset, or Dispose's wake (every test calls Shutdown first)

### WK-02: A lost wake is counted once, and nothing else is counted
  invariant the dispatcher publishes _wokenGeneration once a round's Sets are all done. A worker whose backstop fires while _wokenGeneration is
            newer than the generation it last joined, with its own event still clear, missed that round's Set: it is counted once
            (LostWakeCount, and TickTelemetry.LostWakes of the tick that catches it, which may be the one after) and warned about once per scheduler
  never counting a backstop that fires between a bump and the worker's own Set (_wokenGeneration still older), nor one whose Set landed after
        the backstop and before the check (the event is set)
  requires: WK-01 (the bump before every Set, and only the owning worker Resetting its event, after each return and before the re-check: together
            they make a clear event after a completed round mean a lost Set)
  note: the count sees only a lost wake the backstop resumes. One the next dispatch's Set rescues first leaves no trace, so at tick rates faster
        than the backstop a broken protocol mostly reads zero: non-zero proves a defect, zero proves nothing, and WK-01's tests remain the check.
        No tag: a wrong count is neither corruption nor detectable. The once-per-scheduler warning is not asserted by any test
  scope: DagScheduler.cs (WorkerLoop, OnLostWake, DispatchTrackMultiThreaded, LostWakeCount), DagScheduler.Telemetry.cs (ComputeAndRecordTelemetry)
  verified: WorkerWakeTests — ALostWake_IsCountedOnce (a wake taken away before the worker parks, counted once in total and in the ticks'
            telemetry); ABackstopFiringBeforeItsSetArrives_IsNotALostWake; ASetLandingAfterTheBackstop_IsNotALostWake
  note: no RuleMutant. When the counter landed, hand-made mutants of the check, one of them dropping the clear-event clause, were each caught
        by these tests

### WK-03: A worker parked inside a dispatch is woken for the work it is needed for, and when the dispatch ends `[perf]` `[silent]`
  invariant under the parking policy (HotSpinners >= 0) an idle worker spins with PAUSE, never a yield, and after ParkAfterUs parks on its OWN in-tick
            event (_parkWake[workerId]) unless fewer than HotSpinners other workers are spinning; its state (_idleState) is busy, spinning or parked
  invariant a worker parks by storing "parked" and counting itself (_parkedCount, an interlocked increment: a full fence) BEFORE it re-checks for work,
            the dispatch's end and shutdown; a publisher stores the ready flag and claim word BEFORE a full fence and only then reads the parked count
            and the states (WakeParked). One of the two sees the other: the fenced store-buffer pattern
  invariant a wake is claimed by compare-exchanging a state from parked to busy; the claimer alone Sets the event, and the worker alone Resets it, only
            after consuming that Set (also when it un-parks itself and loses the race to a claimer): an event is set exactly when a claimed wake is
            unconsumed
  invariant a multi-chunk dispatch (DispatchParallelQuery, a pipeline's successor publish) wakes one parked worker per chunk beyond the publishing worker
            and the workers spinning at that moment (WakeForChunks); the completion that takes _systemsRemaining to zero wakes every parked worker, and so do
            the end of DispatchTrackMultiThreaded, Shutdown and Dispose
  invariant the park wait's backstop (ParkBackstop, 2 ms) is a liveness net, not a way to be woken
  rationale: the legacy policy spun, then yielded forever. Thread.Yield gives the core up only to a thread ready on it and otherwise returns at once, so
    an idle worker was a tight loop at 100 % of a core — measured on the SWG demo at 1 000 sessions as ~36 % of worker time inside dispatches — which
    starved the thread pool running the sends and ASP.NET Core. Parking (no hot spinner, park after 10 µs) returned ~2.9 cores and made the tick 4.3 %
    shorter at P50 and 6.2 % at P99 over six interleaved pairs, with the same worker work per tick. A worker left parked when its dispatch ends is still
    inside it: the next dispatch's wake Sets the between-tick events (WK-01), not the park events, so it stays there until its backstop or until some later
    multi-chunk dispatch happens to wake it — which is why the end of a track must wake it
  on_violation: a worker sleeps through work it was needed for until the backstop — latency, not corruption: the dispatch completes on the other
    workers, later. Silent: nothing counts a late wake
  scope: DagScheduler.Idle.cs (ParkIdleWorker, WakeParked, WakeForChunks, CountSpinning), DagScheduler.cs (WorkerLoop, DispatchParallelQuery,
         OnSystemComplete, DispatchTrackMultiThreaded, Shutdown, Dispose)
  verified: WorkerParkingTests — every test with no hot spinner, parking at once and a 30 s backstop, over all-hands parallel systems whose chunks wait
            for the whole pool: AParallelDispatchAfterASerialGap_WakesTheParkedPool (the pool parks during a serial gate, the dispatch after it must wake
            it); TheEndOfADispatch_ReturnsEveryParkedWorkerToTheBetweenTickWait (one serial system per tick: every worker must reach the between-tick
            wait on most ticks); NoWakeIsLost_AcrossThousandsOfParks
  note: no RuleMutant. Hand-made mutants, each caught (2026-09-22): WakeForChunks made a no-op fails the serial-gap and churn tests; both track-end
        wakes removed fails the end-of-dispatch test. That test replaced one that ended a track and needed all hands in the next, which the mutant
        passed: the next track's multi-chunk root dispatch woke the leftover workers itself

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

## Module: DSEL — Dispatch selection (Realms RT-1)

Which clusters a QuerySystem runs over this tick — its tier, its `cellAmortize` bucket, the awake clusters — is decided in ONE place,
`TyphonRuntime.SelectDispatchClusters`, and every dispatch path draws from it. Before RT-1 only the parallel non-Versioned path applied
dormancy and amortization; the non-parallel path, the Versioned paths and the change-filter scans each re-derived their own list.

### DSEL-01: Every QuerySystem path dispatches the one selection `[fatal]` `[silent]`
  invariant ∀ QuerySystem S with a bound archetype, ∀ run: the entities S's callback receives come from the clusters
            SelectDispatchClusters returned for that run (or, for a checkerboard system, the half of them its phase serves),
            whatever S's shape — parallel or not, Versioned or not
  invariant a null selection means "nothing narrows S": it covers its whole view (the zero-copy path); a non-null one is
            materialized from those clusters, never from a fresh walk of the tier index
  invariant ONE selection per run: made at the run's entry point (the parallel Prepare's first phase, OnSystemStartInternal) and
            only read afterwards — every materialization, including a change filter's first-tick fallback, reads it
            (BuildFullViewEntitySet); a second selection would rewrite the buffer the dispatch already handed out
  invariant the tier selected is the EFFECTIVE tier: the system's filter AND its view's (ViewBase.TierFilter), on every path;
            without a grid no tier applies, on every path; a disjoint pair is refused at runtime construction
  invariant every tier a system can select is rebuilt and its multi-tier merge prepared at tick start (TI-01); dispatch never
            rebuilds and never fills the shared merge cache (a set not prepared is merged into the system's own buffer)
  invariant sleeping clusters are absent from every path, including the change-filter dirty scans (both branches)
  never cellAmortize together with a change filter (refused at build: striding a once-delivered dirty set drops changes)
  never checkerboard together with a change filter (refused at build: the dirty list has no half, both phases would process it)
  scope: TyphonRuntime.SelectDispatchClusters, TyphonRuntime.OnParallelQueryPrepare, TyphonRuntime.BuildFullViewEntitySet,
         TyphonRuntime.PrepareVersionedFallback, TyphonRuntime.ScanClusterDirtyEntities, TyphonRuntime.ScanClusterDirtyEntitiesIntoSet,
         RuntimeSchedule.ValidateRegistration
  on_violation: a non-parallel or Versioned tier system integrates every entity of every tier each tick — with cellAmortize N,
                with N× dt (AmortizedDeltaTime) — and dormant clusters are simulated; a Versioned checkerboard system
                processes its whole tier in both phases. Nothing raises.
  verified: DispatchSelectionTests

### DSEL-02: The cellAmortize bucket is keyed on the system's run count `[silent]`
  invariant bucket(S, run) = runIndex(S) mod cellAmortize, where runIndex counts the runs S actually made (advanced once per run at
            its entry point, never for a tick the scheduler skipped, never twice for a checkerboard's second phase)
  never keyed on the tick number: a TickDivisor sharing a factor with cellAmortize then never visits some buckets
        (TickDivisor 2 × cellAmortize 2 → tick always even → bucket 0 only → half the clusters never run)
  scope: TyphonRuntime.SelectDispatchClusters, TyphonRuntime._systemRunCount
  on_violation: clusters starved for the life of the process, silently
  verified: DispatchSelectionTests.TickDivisor2_CellAmortize2_EveryClusterVisited

## Module: BIND — QuerySystem ↔ archetype binding

A `QuerySystem` — parallel or not (RT-1, Realms) — is bound to one `ArchetypeClusterState` at runtime construction. That binding decides two things at
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
  note direction 1 — the lifecycle-level channel views subscribe to BY ARCHETYPE — shipped in #790 and is the `MEMB` module in
       `rules/ecs.md`. This rule still names `IsPullMode` rather than `IsMembershipEligible`, and deliberately: it must go on
       driving all THREE pull shapes, and only the archetype-only one has a channel. For that one the per-tick refresh is now
       O(changed) with an O(1) gate when the archetype is untouched (measured ~3 100 us -> 0.25 us at 50 000 entities, three runs); for the
       `.Where(lambda)` and spatial ones the cost is unchanged — an O(N) re-query plus the set `EcsQuery.Execute` allocates —
       because a membership notification cannot re-evaluate an opaque delegate or a position. See `MEMB-03`.
  note a pull View held by USER code and never refreshed is still a snapshot — that part is unchanged and is deliberate
       (ADR-042: a View holds no transaction, so it has no snapshot to become live against).
       `ViewCreatedBeforeTheSpawns_ConvergesWithOneCreatedAfter` is no longer quarantined: #790 gave it the channel it was
       waiting for, and it now asserts convergence after ONE refresh plus which PATH delivered it, because convergence alone
       is satisfied by the O(N) rescan the channel exists to remove.
  verified: SystemInputViewLivenessTests.SystemInputView_SeesEntitiesSpawnedWhileTheRuntimeIsRunning — spawns while the
            runtime is ticking, which no fixture anywhere did before, and asserts the system sees 20 rather than 10.
  requires BIND-01 (a system with no input View has no membership to keep fresh)
