# Concurrency Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-08-17 |
| Domain | UnitOfWorkContext, HoldoffScope, Deadline, AccessControl, AccessControlSmall, ResourceAccessControl, EpochManager, EpochGuard |

> Invariants governing cooperative cancellation and the structural-mutation regions it must not
> interrupt. The two are a **coupled pair**: neither is safe to change without the other, which is
> why they live in one module rather than two independent facts.

---

## Module: Cooperative Cancellation ⊗ Structural Holdoff

A `UnitOfWorkContext` carries a `Deadline`. `ThrowIfCancelled()` is the yield point where an expired
deadline becomes a thrown exception; `EnterHoldoff()` opens a region in which that check is a no-op,
so a thread inside a multi-step structural mutation cannot be unwound halfway.

**The current state is safe by coincidence, not by construction.** Verified 2026-07-29:

| Mechanism | Real call sites |
|---|---|
| `ThrowIfCancelled()` | **1** — `Transactions/public/Transaction.cs:2237` |
| `EnterHoldoff()` | **2** — commit `Transaction.cs:2255`, rollback `Transaction.cs:1890` |
| `BeginHoldoff()` / `EndHoldoff()` | **0** direct callers |

Neither B+Tree descent (`FindLeaf`), node split, nor the revision-chain walkers and appenders contain
a cancellation check *or* a holdoff. Nothing can be cancelled mid-split because nothing checks
mid-split. The gaps cancel out — and stop cancelling the moment either one is closed alone.

### CX-01: A structural mutation is never observably interrupted `[fatal]` `[silent]`
  invariant ∀ region R that mutates a multi-word structure (B+Tree node split, SMO, revision-chain
            append, EntityMap rehash): ¬∃ yield point Y ∈ R where `ThrowIfCancelled()` can throw
  scope: UnitOfWorkContext.cs, HoldoffScope.cs, BTree.Insert.cs, BTree.cs (FindLeaf),
         RevisionChainReader.cs, ComponentRevisionManager.cs
  on_violation: the structure is left half-linked with no unwinder — a split that threw between
                "new node published" and "parent separator installed" loses the right sibling
                permanently. Silent: readers take the old path and see stale-but-valid data, so
                nothing surfaces until a later split or a full scan walks the orphan.

### CX-02: Adding a yield point requires adding the holdoff first `[design]`
  invariant [add holdoff to R] → [add `ThrowIfCancelled()` to any path reaching R]
  never `ThrowIfCancelled()` is introduced into a traversal while the structural regions that
        traversal can reach remain unguarded
  requires: CX-01
  rationale: this is the coupling. Today CX-01 holds **vacuously** — there is one cancellation
             check in the whole engine and it sits on the commit path, outside every structural
             region. Making long traversals interruptible is a legitimate goal (a UoW deadline
             expiring inside a deep descent is currently not observed until control returns to
             commit), but the *first* change in that direction converts CX-01 from vacuously true
             to actively violated. Order matters: holdoff first, yield points second.
  on_violation: a plausible, well-intentioned change — "make `FindLeaf` respect the deadline" —
                introduces a torn-structure bug in code that was correct before the edit and looks
                correct after it.

### CX-03: Holdoff regions are RAII and nest `[correctness]`
  invariant `EnterHoldoff()` returns a `HoldoffScope` whose `Dispose()` decrements exactly once
  invariant _holdoffCount > 0 → `ThrowIfCancelled()` is a no-op
  invariant _holdoffCount ≥ 0 at all observable points
  scope: UnitOfWorkContext.cs:126-150, HoldoffScope.cs
  on_violation: an unbalanced `BeginHoldoff` without `EndHoldoff` disables cancellation for the
                remainder of the UoW — the deadline silently stops being enforced. Prefer
                `EnterHoldoff()` over the raw pair; the raw pair currently has **zero** callers and
                should stay that way.

### CX-04: `default(UnitOfWorkContext)` is already expired `[design]`
  invariant default(UnitOfWorkContext).Deadline.IsExpired == true
  scope: UnitOfWorkContext.cs:16
  rationale: fail-safe. A context that was never initialised must not read as "infinite time
             remaining", which would make a missing-plumbing bug invisible.

---

## Module: Thread Identity

### CX-05: Thread ids fit 16 bits and stay below 32,767 `[fatal]`
  invariant ∀ stored thread id t: 0 ≤ t < 32_767
  scope: AccessControl.cs, AccessControlSmall.cs:22-29, ResourceAccessControl.cs
  rationale: `AccessControlSmall` packs the id into bits 16-31 of a **signed** `int`, so 32,768+
             sets the sign bit and `LockedByThreadId` sign-extends. The wider primitives happen to
             tolerate more; the tightest one binds the standard.
  note: managed thread ids are allocated lowest-available-first and recycled on thread death, so
        this bounds **simultaneously-live** threads, not threads over process lifetime.
  on_violation: `LockedByThreadId` returns a negative id; ownership comparisons fail; a lock is
                released by a thread that does not hold it.

---

## Module: SNAP — MVCC snapshot retention

A TSN is not a snapshot. Revisions are retained for the OLDEST TSN the transaction chain can see, so holding a TSN that is
not in the chain buys nothing — and the reader cannot tell the difference by looking at the data it gets back.

### SNAP-01: A read at an unretained snapshot fails; it never returns a default `[fatal]` `[silent]`
  invariant a revision-chain walk that finds nothing visible at TSN `t`, on an accessor whose snapshot is NOT registered in
            the transaction chain, raises `SnapshotExpiredException` when `t < TransactionChain.RetainedMinTSN`
  never returning a zeroed component for a trimmed snapshot. It is wrong data, and at the call site it is indistinguishable
        from legitimately-zero data — there is no value a caller can test for.
  never leaving the chain ROOT in the slot's location on walk failure. That is worse than the zero: the root is a CompRev
        chunk id, dereferenced as a CONTENT chunk id, so the reader gets whatever occupies that id in the content segment —
        a wrong VALUE. Set the location to 0.
  enforce the check lives in the walk's FAILURE branch, not ahead of the read: a successful read pays nothing, not even the
          watermark load, and a walk that fails for a legitimate reason is untouched unless the snapshot is also
          demonstrably below the floor
  scope: TransactionChain.RetainedMinTSN + ComputeNextMinTSN (publication), EntityAccessor.ThrowIfSnapshotExpired,
         EntityAccessor.ResolveVersionedContentChunk and the legacy per-slot walk in ResolveEntity, SnapshotExpiredException
  on_violation: silent wrong data on a public API.
  rationale: #672. `PointInTimeAccessor.Create` calls `AllocateTSN()` — a bare interlocked increment — and registers
             NOTHING, so `ComputeNextMinTSN` cannot see it and any committing writer may trim below a live snapshot.
  verified: PointInTimeAccessorTests.AccessorReadingATrimmedVersionedRevision_ThrowsSnapshotExpired,
            TwoAccessorsAtDifferentTSNs_SeeDifferentSnapshots, MultipleAccessorsConcurrently_IndependentSnapshots,
            MultipleSnapshotsSequential_MVCCVisibility [VerifiesRule]

### SNAP-02: Retention is not the fix for an unregistered snapshot `[design]`
  invariant the engine does NOT extend retention to cover unregistered snapshots
  rationale: retention means chains stop being trimmed while a snapshot is live, and
             `RevisionChainReader.TryWalkSingleEntryOptimistic` — the lock-free fast path for EVERY Versioned read in the
             engine — is predicated on chains being one entry long (`design/Revision/02-mvcc-visibility.md` §3.2). Granting
             retention hands any caller a way to silently degrade MVCC read performance engine-wide by holding an accessor
             too long, or by leaking one. Fail-fast is the cheaper and safer contract, and the guarantee is already
             available: a read-only `Transaction` registers in the chain.
  enforce a caller needing a snapshot that survives concurrent commits uses a read-only Transaction; the exception message
          says so
  verified: PointInTimeAccessorTests.ReadOnlyTransaction_KeepsItsSnapshotAcrossTheSameWrite [VerifiesRule] — the control
            that stops SNAP-01's throw from reading as a limitation of MVCC rather than of the accessor
  note the cost of the defect was hidden by a coincidence for a long time: before #629 the flat read path left the chain
       root in the slot location, and CompRev chunk id 1 happened to name content chunk id 1, which still held the
       pre-update value. Three tests passed on that. The eligibility flip did not break them; it stopped a coincidence from
       covering for them.

---

## Module: EP — epoch pinning and page eviction

`RequestPageEpoch` raises a page's `AccessEpoch` by CAS-max, and the **only** thing that lowers it is
`UnlatchPageExclusive`, which resets it to 0 (PS-03, `durability.md:1119-1124`). So under PS-01 a page that is written —
stamped, latched, unlatched — ends up costing nothing, while a page that is merely **read** stays unevictable until the
scope that read it lets go. Reading a page therefore does not cost a pin *for the read*; it costs a pin **for the whole
enclosing scope**.

That asymmetry is the trap. Write loops look expensive and are not; a read-only verification pass looks free and is not.

And the scope is the caller's. `Transaction.Init` enters at `Transaction.cs:189` and exits only at `:388`; a nested
`EpochGuard.Enter` inside a callee does **not** re-pin, because `EpochThreadRegistry.PinCurrentThread` stamps only at
`depth == 0`. So every page any callee touches is pinned for the entire transaction, and a callee cannot buy itself
relief by opening a scope of its own. `EpochManager.RefreshScope` would, but it is not available to a callee holding
live page pointers: it makes exactly the pages behind those pointers evictable (PS-01/PS-02 use-after-free).

This is the module `README.md`'s roadmap calls out as high priority because "the epoch/eviction interaction spans
multiple subsystems". It starts with the one invariant #838 cost a P0 to establish; the AccessControl state machine,
lock ordering and deadlock prevention are still to come.

### EP-01: A post-condition check reads only what its operation wrote `[fatal]`
  invariant ∀ operation O with a post-condition check C running inside O's caller epoch scope: pages(C) ⊆ pages(O)
  never proving a local mutation correct by re-reading the whole structure it lives in
  scope: LogicalSegment.cs, VerifyGrownChainLinks, ChainLinkMatches, WalkForwardChainPageCount, RequestPageEpoch
  on_violation: the check pins O(structure) pages the operation never touched, for the caller's whole scope. Once the
                structure outgrows the page cache the operation blocks waiting for eviction of pages its own scope
                protects — a self-deadlock, surfaced as `PageCacheBackpressureTimeoutException`, whose summary says
                "Transient — IO will eventually complete and free pages" and sends the reader hunting the disk. #838
                measured it as a commit holding a 5071.9 ms pin against a 5000 ms back-pressure timeout.
  rationale: a bounded check is not a weaker check — PROVIDED the bound is drawn around what the operation WRITES and
             not around a convenient index range. `CreateOrGrow` writes the chain FIELD in exactly two places, the
             old-tail patch and the data-page-init loop, both inside `[growFrom-1, end]`. But it also rewrites the ROOT
             page's `LogicalSegmentHeader` when a grow pushes the directory past `RootHeaderIndexSectionCount` and needs
             a map-extension page, and `LogicalSegmentNextMapPBID` is the field ADJACENT to
             `LogicalSegmentNextRawDataPBID` in that struct — so a wrong-field write there is squarely this
             post-condition's bug class, while with `growFrom` past 2000 the root sits outside the index range. Hence
             index 0 is ALWAYS verified as well; it costs no extra fetch, because `VerifyDirectoryAgainst` faults the
             root immediately afterwards. Over the verified set the positional comparison that replaced the whole-chain
             count is strictly stronger: it also rejects "right count, wrong target".
  note: the check is NOT free, and the earlier claim that it was ("the pages were already latched by this call") was
        wrong in a way worth recording: the write loops UNLATCH, which resets `AccessEpoch` to 0, so the grow's own
        writes leave nothing pinned and this post-condition is the ONLY thing a grow leaves pinned. The bound therefore
        does not remove the cost, it makes the cost proportional to the grow instead of to the segment — a caller that
        grows by N has already committed to touching N pages, so O(N) is the right ceiling and O(segment) is not.
  enforce: no strict-mode escape hatch re-runs the exhaustive walk on this path. `CheckConfig.Enabled` is what a user
           turns on to diagnose a stall, and this walk is what produces the stall; the engine's own test suite runs
           strict mode on for every fixture, so "nobody will enable it in anger" is already false. Exhaustive structural
           walks belong where no caller scope spans them — `LogicalSegment.Load` on reopen, `RunStorageIntegrityCheck`
           on demand.
  note: what the bound gives up, accepted deliberately — prefix damage this method did NOT cause (a stale forward
        pointer below `growFrom-1` from a lost write, or a cycle in the prefix) is no longer noticed at the next grow.
        Both are still caught, by `Load` on the next reopen and by `RunStorageIntegrityCheck` on demand; the detection
        LATENCY grows from "next grow" to "next restart". Accepted because the old check mis-attributed that damage to
        `CreateOrGrow` anyway — #840 is a live example, a loss that happened in memory and was reported as "not
        persistence".
  verified: SegmentGrowEpochPinTests.Grow_InsideCallerEpochScope_PinsOnlyTheGrownRange [VerifiesRule] — counts pinned
            pages rather than waiting for the timeout, so it measures the invariant instead of a downstream symptom:
            13 pins for a 10-page grow, against 2410 (the whole segment) before the fix. It asserts a LOWER bound too,
            because zero pins would mean `RequestPageEpoch` had stopped stamping — a PS-01 use-after-free passing as a
            success.
  note: the walk sites #838 tabulates as latent copies are NOT covered by this rule and remain O(segment) today —
        `RunStorageIntegrityCheck`, `LogicalSegment.Load`, `Clear`/`Fill`, `EnumerateVersionedChainHeads`,
        `SchemaEvolutionEngine.MigrateEntities`, `RederiveOccupancyOnCrash`, `StatisticsRebuilder.RebuildClusterAll`,
        `GetSchemaHistory`, `LoadPersistedArchetypes`. None is a post-condition and none is on the commit path. Widening
        the rule to cover them would make it false on the day it was written, which is worse than not covering them.

## Module: SIGNAL — Wake signals versus resource counts

A counting semaphore can model two different things: how many of a resource are available, or that something has
happened. The two have opposite correctness conditions, and the type does not distinguish them.

### SIGNAL-01: A permit is produced only when there is a consumer for it `[fatal]`
  invariant ∀ counting primitive S used as a WAKE SIGNAL: a `Release` on S happens only when at least one thread is
            registered as waiting on S
  invariant a waiter registers BEFORE its final availability check, and re-checks after registering — the registration
            and the check must bracket each other or the lost wakeup the semaphore was chosen to prevent returns
  invariant outstanding permits are bounded by a quantity that does not grow with elapsed time: live waiters, or live
            threads. Never by cumulative operations
  never gate a `Release` on nothing. `Wait` on a wake signal is reached only under contention, so an unconditional
        `Release` produces a permit per operation and consumes none — the count then rises for the life of the process
  scope: UowRegistry.Release / WaitForSlotFreed / AllocateUowId / HasFreeSlot / SignalPermitCount / WaiterCount
  on_violation: the count reaches `int.MaxValue` and `SemaphoreSlim.Release` throws `SemaphoreFullException` from
                whatever ordinary call happened to be next, killing the process. Measured: a SpaceBattle soak died at
                tick 1 454 985 after 6 h 40 m — 2 147 483 647 / 1 454 985 ≈ 1 476 UoW frees per tick, which is the
                demo's actual transaction rate. The engine was HEALTHY at death (883 of 32 768 pages resident, zero
                gated cycles, `health Ok`), so every gauge read normal until the instant it died (#844)
  note: this is a property of the ROLE, not the type. `StagingBufferPool._available` is the same `SemaphoreSlim` used
        correctly, because it models a RESOURCE COUNT: `Rent` waits unconditionally and `Return` releases
        unconditionally, one for one, so the count is conserved and bounded by the pool capacity. `_slotFreed` models an
        EVENT, and an event has no level to store.
  note: a latch cannot have this defect at all. `ManualResetEventSlim.Set` and `AutoResetEvent.Set` are idempotent —
        there is no count to accumulate — which is why the other twelve signalling primitives in the engine are immune
        by construction rather than by review.
  verified: UowRegistryTests.Registry_UncontendedAllocateFree_DoesNotAccumulateSemaphorePermits [VerifiesRule] — 550
            uncontended allocate/free cycles must leave the permit count where 50 did. Before the fix it read exactly
            550, one per free, which is the defect stated as a slope; the real ceiling is unreachable in a test, and
            that is precisely why this reached production. The saturation and cross-thread wake cases already in that
            fixture are the guard on the other direction — that gating the Release did not reintroduce a lost wakeup.
