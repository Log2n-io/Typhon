# Concurrency Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-07-29 |
| Domain | UnitOfWorkContext, HoldoffScope, Deadline, AccessControl, AccessControlSmall, ResourceAccessControl |

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
