# Concurrency Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-10-09 |
| Domain | UnitOfWorkContext, HoldoffScope, Deadline, WaitContext, AccessControl, AccessControlSmall, ResourceAccessControl, EpochManager, EpochGuard |

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

## Module: Lock waits

### CX-06: A lock wait's budget starts when the wait does `[correctness]`
  invariant ∀ acquisition A of a revision-chain lock through `RevisionChainReader.WalkChain(…, Transaction)` — a transaction's
            point read and resolve, its first write's re-resolve, its index-scan enumerator — A waits until
            min(the owning UoW's deadline, now_at_A + RevisionChainLockTimeout). Callers that pass their own budget compose it per
            call (`WalkChain(…, bool skipTimeout)`: a fresh timeout, or infinite for point-in-time accessors and for a destroy's
            resolve of a cluster entity's Versioned slots, `Transaction.ResolveClusterVersionedForDestroy`)
  never a per-acquisition timeout composed once and reused for every acquisition of a transaction, or of any longer-lived scope:
        the budget then runs from the scope's first acquisition, and a scope older than the timeout fails its first contended wait
        at once
  scope: Transaction.ChainLockWaitContext, RevisionChainReader.WalkChain, RevisionEnumerator, AccessControlSmall.TryEnterSharedAccess
  rationale: the budget costs a clock read, so it is composed only when an acquisition has to wait. The single-entry fast path takes
             no lock; the locked walk first tries the lock without waiting (`TryEnterSharedAccess` / `TryEnterExclusiveAccess`: no
             clock, no contention flag, no contention event), which succeeds whenever nobody holds the chain exclusively. Only a
             failed try composes the budget and waits. Caching the budget per transaction saved the clock read too, but turned a lock
             timeout into a transaction lifetime. Composing it on every locked walk cost +12 ns per walk (95 -> 107 ns,
             `RevisionLockedWalkBenchmarks`); trying first costs nothing measurable (99.4 / 102.0 ns against main's
             100.3 / 100.4).
  on_violation: a reader holding a snapshot longer than `RevisionChainLockTimeout` (5 s) throws `LockTimeoutException` on its
                first walk of a chain a writer holds for microseconds. MarketHardeningTests' long readers failed that way on the
                desks' chains, 5 s into their snapshot (#1250).
  verified: ChainLockBudgetTests — a reader 1.5 budgets old waits out a hold on a chain it walks; the holder releases only after
            it has seen the reader waiting (the contention flag a waiting acquisition sets), so each walk is proven to have waited
            [VerifiesRule]; with the budget composed at the first contended walk and reused (the seam `ChainLockWaitContextProbe`),
            the same wait throws [RuleMutant]

---

## Module: TXC — the transaction chain

The chain is what the revision cleanup's cutoff reads, and a snapshot the cutoff cannot see is not retained (SNAP). TXC is
the chain's side of that contract: a transaction's snapshot is visible to every cutoff from the moment it exists.

### TXC-01: A cutoff never passes a snapshot that exists or is being created `[fatal]` `[silent]`
  invariant ∀ cutoff C = `TransactionChain.ComputeNextMinTSN()`, computed by the tail L, ∀ transaction T ≠ L alive when C is
            computed or created concurrently with it: C ≤ T.TSN. (L's own snapshot is not retained past its cutoff: L computes
            C to clean up after itself, at its dispose. An empty commit computes nothing: it ends L (TX-01), but L still reads.)
  enforce (1) a transaction is in the chain before its TSN exists: `Register` pushes it with its TSN pending (0), then draws
          the TSN and publishes it (`Volatile.Write`);
          (2) the cutoff is the minimum over every node but the leaving tail, and over `issued + 1`, the counter read BEFORE
          the head. A creator the walk does not see pushed after that read, so it draws above it; a pending node the walk
          does see is awaited (`AwaitTsn`), its creator two instructions from publishing it
  never draw a TSN before the push: a cutoff computed in between cannot see that snapshot
  never take the second-to-last node's TSN for the minimum: concurrent creations push in any order, so the chain is not in
        TSN order, whether the draw comes before the push or after it
  scope: TransactionChain.Register, TransactionChain.ComputeNextMinTSN, TransactionChain.AwaitTsn, TransactionChain.Remove,
         Transaction.Init
  rationale: a pending node costs a cutoff walker the creator's two remaining instructions (a spin that yields — `Sleep(0)`
             or `Yield`, never `Sleep(1)`), and a creator never waits. The minimum walks the nodes the second-to-last search
             already walked; the TSN it reads sits beside the link it follows (`Transaction._link`), so the walk still reads one
             cache line per node. Considered and not taken: the creator stores a lower bound (the counter + 1) before its push, so no
             walker ever waits — at the price of a read of the counter's contended line on every begin.
  on_violation: the cleanup trims a revision a live snapshot still needs, and that snapshot reads the component as zeros, with
                no error: SNAP-01's exception covers unregistered snapshots only (#1254).
  verified: TransactionCutoffTests.ACutoffNeverPassesASnapshot — a creator held between its push and its draw, in three
            interleavings: the tail trims while it is held, staged on the tail finishing or waiting on its pending TSN
            (writer first); or after its release, the writer before or after a younger reader. Every snapshot reads its own
            value [VerifiesRule]; a cutoff above every TSN issued is caught [RuleMutant]. Real mutations: the draw moved before
            the push fails `HeldAcrossTheCleanup`; the second-to-last cutoff fails `ReleasedBeforeTheCleanup_ReaderFirst`.
            TransactionCutoffTests.AnEmptyCommitByTheTail_KeepsItsOwnSnapshot — the tail still reads its value after an empty
            commit [VerifiesRule]; the empty commit's cleanup restored fails it.
            TransactionChainTests.Stress_ConcurrentCreation_NoCutoffPassesALiveSnapshot — 8 readers and a writer for 300 ms,
            each reader finding the retention floor at or below its TSN [VerifiesRule]; it catches each real mutation 3/3.

### TXC-02: A chain holding a transaction has a tail `[silent]`
  invariant whenever no Remove is in progress and no push that found the chain empty is between its head CAS and its tail
            CAS: head ≠ null ⇒ tail = the chain's last node, the first pushed of the live transactions; and once the tail's
            own registration has returned, MinTSN = its TSN. (Such a push leaves the chain briefly with a head and no tail; it
            sets the tail itself, one instruction later, and MinTSN when its TSN is published.)
  enforce a Remove of the tail alone in the chain clears the tail BEFORE the CAS that empties the head; that CAS orders the
          clear, so a push that finds the chain empty finds the tail cleared too, and its own CAS makes it the tail. If pushes
          beat that CAS, the Remove re-scans and makes the node now before it the tail. A Remove of a tail with a predecessor
          makes the predecessor the tail
  never clear the tail after the head's CAS: a push in between fails its tail CAS against the leaving transaction, and the
        chain is left with a head and no tail
  scope: TransactionChain.Remove, TransactionChain.SetTail, TransactionChain.PushHead
  rationale: only the tail computes a cutoff (TXC-01). With no tail, nothing computes one: the revision cleanup and the ECS
             cleanup queue stop until the chain empties again, which under steady traffic may be never. Ordering the clear
             before the CAS costs nothing; a full fence and a re-read of the head after it cost +7 % on a lone read-only
             transaction's create and dispose, which empties the chain every time. The predecessor known from the unlink
             also replaces the walk to the chain's end the tail's Remove used to make.
  on_violation: liveness and space: queued cleanups wait and revision chains grow (#1255).
  verified: TransactionCutoffTests.APushIntoAnEmptyingChain_BecomesItsTail — a Remove of the lone tail held right after its
            head CAS emptied the chain, or right before that CAS, and a push meanwhile: the pushed transaction is the tail,
            with its TSN as MinTSN [VerifiesRule]; with the tail cleared after the CAS (`TailClearedAfterCasMutantForTest`),
            the chain has no tail [RuleMutant]. Real mutation: the clear moved after the CAS fails it. The code before #1255
            (a plain clear after the head read) failed the same test with the probe at that window.

### TXC-03: A TSN never outgrows its 48-bit storage `[fatal]` `[silent]`
  invariant every TSN draw — a transaction's (`CreateTransaction`) and a point-in-time snapshot's (`AllocateTSN`) — first compares the counter
            with 2^46 (`TsnWarningThreshold`): one predictable branch, a plain read. Past it, the engine warns once (`WarnTsnHeadroomLow`); at
            `TsnRefusalThreshold` = 2^47 − 2^24 it refuses the draw with `TransactionSequenceExhaustedException` — not transient — drawing nothing
  invariant the margin is fixed, not per live transaction: draws pass the check and then draw, so concurrent ones overshoot it by up to one per
            thread, and the commit-stamp design (option C) will draw a stamp at a commit point that cannot be refused. 2^24 covers both; bit 47
            stays free for that design's pending mark
  invariant a counter restored at open (`SetNextFreeId`) past the refusal threshold refuses the open; past 2^46 it warns then
  invariant the 48-bit packed setters (`CompRevStorageElement.TSN`, `EntityRecordHeader.BornTSN` / `DiedTSN`) assert the range in Debug
  never let a TSN pass 2^48: the packed setters keep its low 48 bits, and a wrapped TSN reads as "born at genesis, alive" to every MVCC check
  scope: TransactionChain.CreateTransaction, TransactionChain.AllocateTSN, TransactionChain.SetNextFreeId, TransactionChain.TsnRefusalThreshold,
         CompRevStorageElement.TSN, EntityRecordHeader.BornTSN, EntityRecordHeader.DiedTSN
  rationale: #1208. The 2^46 check was an empty TODO. At a million transactions a second (read-only ones draw a TSN too) the warning comes after
             about 2.2 years, the refusal after 4.5, and 2^48 itself would be 8.9
  on_violation: visibility silently wrong for every revision and entity stamped past the wrap
  verified: TsnHeadroomTests — BelowTheWarningThreshold_NothingIsLoggedOrRefused, PastTheWarningThreshold_ItWarnsOnce (a transaction's draw or a
            snapshot's first), NearTheLimit_ATsnStoresWhole (a snapshot taken before a spawn near the limit does not see it),
            AtTheRefusalThreshold_NoTsnIsDrawn, ACounterRestoredPastTheLimit_IsRefusedAtOpen (the counter seeded by `SetNextFreeIdForTest`)
            [VerifiesRule]. Real mutations: the refusal removed, the check dropped from `CreateTransaction`, the open-time refusal removed — each
            fails
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
`depth == 0`. So every page any callee touches is pinned until the transaction next refreshes its epoch (EP-03), and a callee cannot buy itself
relief by opening a scope of its own. `EpochManager.RefreshScope` would, but it is not available to a callee holding
live page pointers: it makes exactly the pages behind those pointers evictable (PS-01/PS-02 use-after-free). That is
why EP-03 forbids handing such pointers out: point access copies, and the one pointer a handle keeps is held by a slot,
not the epoch — so the accessors themselves refresh at every 128th open, spawn or enumerated entity.

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
  note: the write loops UNLATCH, which resets `AccessEpoch` to 0, so the grow's own writes leave nothing pinned, and
        while this post-condition epoch-tagged what it read it was the ONLY thing a grow left pinned: the whole segment
        before the bound, then one page per page grown. Segments grow by doubling up to 1 024 pages, so the bounded
        check still pinned as much as the segment held (a 128-page cluster segment growing in a 256-page cache
        stalled its commit with 128 pages pinned). It now reads and releases each page (EP-02) and a grow pins
        nothing; the bound is what keeps its reads proportional to the grow.
  enforce: no strict-mode escape hatch re-runs the exhaustive walk on this path. `CheckConfig.Enabled` is what a user
           turns on to diagnose a stall, and this walk is what produces the stall; the engine's own test suite runs
           strict mode on for every fixture, so "nobody will enable it in anger" is already false. Exhaustive structural
           walks read and release each page instead (EP-02): `RunStorageIntegrityCheck` on demand, and the crash path's
           allocator scan.
  note: what the bound gives up, accepted deliberately — prefix damage this method did NOT cause (a stale forward
        pointer below `growFrom-1` from a lost write, or a cycle in the prefix) is no longer noticed at the next grow.
        Both are still caught, by the crash path's allocator scan after an unclean close and by `RunStorageIntegrityCheck`
        on demand (`Load` stopped walking chains in #1143); the detection LATENCY grows from "next grow" to "next crash or
        next check". Accepted because the old check mis-attributed that damage to
        `CreateOrGrow` anyway — #840 is a live example, a loss that happened in memory and was reported as "not
        persistence".
  verified: SegmentGrowEpochPinTests.Grow_InsideCallerEpochScope_PinsNothing [VerifiesRule] — counts pinned
            pages rather than waiting for the timeout, so it measures the invariant instead of a downstream symptom:
            0 pins for a 10-page grow, against 13 while the check epoch-tagged and 2410 (the whole segment) before the
            bound. A control in the same scope reads one page through `RequestPageEpoch` and requires it counted,
            because zero pins could otherwise mean `RequestPageEpoch` had stopped stamping — a PS-01 use-after-free
            passing as a success.
  note: the walk sites #838 tabulates as latent copies are NOT covered by this rule. `RunStorageIntegrityCheck`'s chain
        walk now reads and releases (EP-02), and `LogicalSegment.Load` no longer walks (#1143); the rest remain O(segment) —
        `Clear`/`Fill`, `EnumerateVersionedChainHeads`,
        `SchemaEvolutionEngine.MigrateEntities`, `RederiveOccupancyOnCrash`, `StatisticsRebuilder.RebuildClusterAll`,
        `GetSchemaHistory`, `LoadPersistedArchetypes`. None is a post-condition and none is on the commit path. Widening
        the rule to cover them would make it false on the day it was written, which is worse than not covering them.

### EP-02: A read whose length grows with the data holds a bounded set of pages `[fatal]`
  invariant a read that visits every page of a structure — a segment's chunk bitmaps, its forward chain, a grow's post-condition, a segment's
            directory — takes each page with AcquirePageForRead and releases it (ReleasePageForRead) before it takes the next. It never epoch-tags them.
            One such walk writes: ChunkBasedSegment.FreeAllChunksFrom clears bitmap words through the read pin, with nothing allocating or freeing
            concurrently, and records the page modified (CP-04) before it releases it. It clears and counts only the bits that name a chunk: a
            torn page's padding bits past its last chunk, which the load's count masks, would drive the allocated count below the bitmap's
  invariant an open's clear of a shared index segment (BTreeBase.ClearSharedSegment) — a crash open's, and a clean open's when a component gained
            an index — frees its node chunks through ChunkBasedSegment.FreeAllChunksFrom(DirectoryChunkCount), keeping the directory chunks, and
            checks chunk 0 with IsChunkAllocatedUnpinned. The pages it frees are owed a write, not held by dirty marks: the checkpoint cycle that
            back-pressure forces writes them — flush-only on a crash open (CK-08). Asking IsChunkAllocatedUnpinned per chunk is not enough —
            FreeChunk epoch-tags the page it frees, and a full segment larger than the cache pinned all of it again (#1463). It frees at once:
            an index segment's frees are never held (CK-17)
  invariant the query scans in scope — the occupancy count, the entity-map count, existence check and collect, the SoA cluster scan, the B+Tree
            range enumeration and Path A's cluster walk, and the spatial query's visibility probes — read through a scan accessor
            (ChunkBasedSegment.CreateScanAccessor): its 32-slot window pins each page by SlotRefCount, taken with AcquirePageForRead, and releases
            it the moment it leaves the window. It marks the page recently used, as the epoch path does, so the change is only in what holds the
            page. Every exit disposes it, a fault included — the entity-map scans in a finally, the range enumerator's constructor if
            positioning faults: a window's pins are slot references, and nothing else releases them
  invariant AcquirePageForRead pins by SlotRefCount, taken under the slot's StateSyncRoot after re-checking that the slot still holds the
            page and is ready (PS-15). TryAcquire withdraws "ready", re-checks SlotRefCount and reclaims under the same lock, so a pin never
            lands on a slot being reclaimed. It never tags AccessEpoch, and bumps the clock counter only for a query (warm): a structural scan
            passes through the cache cold
  invariant an address a scan accessor returns is valid only until its next window miss, not for the epoch scope: the clock hand may reclaim any slot
            but the most recently used one. A hash-map bucket read therefore validates against its head's latch resolved again after the chain walk
            (RawValuePagedHashMap.HeadLatch), never a reference taken before it
  invariant a long broad walk does not hold its transaction's epoch for its whole length (#1364): the entity-map walks call a yield point every
            256 buckets, the SoA scan and the occupancy count every 64 clusters (EntityAccessor.BeginScanYield, the interval counted from the walk's start), where the walk holds only its scan accessors' pins — a call
            boundary (EP-03) — and the epoch is refreshed at most every millisecond, never under a live index cursor. Held for the whole walk, the
            reader's epoch kept every page the writers tagged meanwhile protected: at 12M entities a count stalled every writer with the cache
            epoch-held and nothing dirty
  invariant a back-pressure wait inside such a walk moves the waiting thread's epoch, with its warm accessors' (#1230): PagedMMF.RefreshableWaitDepth,
            entered by the walks a query passes a yield point to. Every page such a walk holds is a scan accessor's slot-reference pin, so the epoch
            protects none of them; left at the minimum, it protected every page every other thread tagged while it waited, until nothing could be
            evicted and all of them timed out together. A wait outside such a walk and outside an entity open — a commit's, a B+Tree operation's —
            still holds its epoch, unless it is a publish's B+Tree write that has changed nothing yet (below): its frames may keep an address the
            epoch alone protects (#1199). So does a point read INSIDE such a walk — a chain walk through the transaction's accessors,
            a regular accessor's map read — which sets the depth to 0 for its length (EcsQuery.FillSnapshotColumn, EnabledGate.IsPresentAtSnapshot,
            EntityVisibility.IsVisiblePendingInScan)
  invariant an entity open that meets back-pressure does not wait with its transaction's pin (#1230): the round throws BackpressureOpenRestart
            (PagedMMF.RestartableOpenDepth), Transaction.ResolveEntity catches it at the open's start, moves the epoch there as NoteEntityOpen
            does, waits for a page and runs the open again, its restarts within one back-pressure timeout of its first round. The open keeps nothing
            it read until it returns, and every lock it takes is released by the throw, so the unwind loses nothing the retry needs. Not under a live
            index cursor, nor in a commit's publish (AP-03): those still wait with their pins. Waiting, one reader's pin kept every page the others
            touched meanwhile: 16 readers over 32 MiB had the whole cache epoch-held, nothing dirty, the pin 1,177 epochs behind
  invariant a contended revision-chain lock is not waited on with a pin either, inside an open or inside a first write before it claims its revision
            (#1382): the lock is waited for PagedMMF.LockWaitBeforeRestart (5 ms), then the operation unwinds (BackpressureOpenRestart, LockWait),
            moves its epoch and tries again, within RevisionChainLockTimeout (PagedMMF.RestartForLockWithin; the shared lock of RevisionEnumerator's
            walk, the exclusive one of ComponentRevisionManager.AddCompRev). Transaction.EcsVersionedCopyOnWrite is a restart boundary as the open is:
            until its claim a first write leaves no trace on a throw (CHAIN-02), and its caller holds nothing across it but the handle, which
            re-resolves (EP-03). Waiting with its pin, a writer queued behind a publish that held the chain while it waited on back-pressure (AP-03)
            kept every page anyone touched, so the publish's page never came: the market's last two failures in 20 seeds, a pin 4,239 epochs behind
  invariant a transaction's dispose leaves its epoch scope and the chain whatever its auto-rollback or its drain throws (#1382), and the exception
            surfaces after: left, its pin kept every page anyone touched after it unevictable and its chain node held the cleanup cutoff, for good —
            one transient back-pressure timeout made a permanent stall. A rollback cut short leaves entries flagged by a TSN that never gets a card,
            which every reader resolves as invisible (VIS-01) — but nothing reclaims them before the next crash open's scrub (VIS-07): the revision GC
            stops at a flagged entry and a destroyed entity's chain release defers on it, so that entity's chain keeps its later revisions. Accepted
            against a permanent stall; bounded by how rarely a rollback itself fails. The UoW registry gives a claimed slot back when its entry's
            write throws, and frees a released one when its page write throws; a transaction whose pool reset throws is dropped with its spawn arena
            given back
  invariant the tail transaction refreshes its epoch before it drains the deferred cleanups (Transaction.RefreshEpochBeforeDrain), at the call
            boundary its dispose is (EP-03), and between the drain's entries at most every millisecond (DeferredCleanupManager.EpochYield — a
            batch of thousands outlived the first refresh, its pin 6,800 to 15,700 epochs behind): the drain runs every transaction's cleanups, and the tail's pin is the oldest there is — a back-pressure
            wait under it held every page touched since the tail began (#1230: 16 threads over 64 MiB, every worker timed out with 8,190 of 8,192
            slots epoch-held; refreshed, the same seeds pass)
  invariant the free-quarantine release waits with its epoch moved on too (#1382, FreeQuarantine.Free): a free holds no page address past
            its own fetch and write, and the page it fetched is tagged with the moved pin (PS-03). Waiting with its pin, the release worker was
            one of the oldest pins of the 32 MiB market's epoch-held cache, 3,991 epochs behind
  invariant so do the drain's chunk frees (DeferredCleanupManager.FreeChunkWaitingRefreshed) and a revision-GC entry up to its chain lock — the
            liveness check and the header read (DeferredCleanupManager.CleanupEntityRevisionsBatched) — for the same reason (#1382): between two
            entries the drain holds only its accessors' slot-reference pins, and up to its fetch a free, or an entry up to its lock, holds nothing
            more. Past the lock, the compaction waits with its pin. Waiting with its pin, the drain made 70 % of the 32 MiB market's pinned waits
  invariant a B+Tree write a commit's publish makes (AP-03) does not wait out back-pressure with its pin while it has made no logical change
            (#1382; a dirty mark before the latch, or a cache dropped, is idempotent). From its entry (BTree.Add, Remove, RemoveValue, Move,
            MoveValue; PagedMMF.BeginRestartableIndexWrite) until its first latch (OlcLatch.TryWriteLock, TryWriteLockOnSmoPath), chunk
            reservation (ChunkReservation.Begin, Fill), node (BTree.AllocNode) or deferred-node reclaim (BTree.DeferredReclaim, which frees), each
            of which disarms it (PagedMMF.DisarmIndexRestart), a back-pressure round throws BackpressureIndexRestart, and disarms it as it throws:
            the unwind's finally blocks then wait, never throw a second one through a half-done free. A chunk allocation or free asserts that no
            write is armed (Debug). The entry moves the thread's epoch with its warm accessors', waits for a page and runs the write again
            (PagedMMF.AwaitPageAfterIndexRestart), within CommitPublishBackpressureTimeout of the append; past it the round waits, and the publish
            reports as AP-03 says. Never with a latch held, under a live index cursor or in the checkpoint's hook, and only the outermost write
            catches it. The unwind loses nothing: the write holds its keys by value, a node it read is reclaimed only once every pin has passed
            it, its accessors and reservation are returned in finally, and a throw with no latch held leaves the tree sound (#1321). The frames
            above it, the publish's, keep only addresses a dirty mark, an accessor's window slot or native memory holds: the cluster slot and
            chain header (dirty), the content chunks (Transaction._clusterCommitContentAccessor, which no other access moves while an entry
            reconciles), the staging buffer. Never one the epoch alone protects: a ChangeSet accessor's evicted slots are released when any
            B+Tree write returns its warm accessors in batch mode (ChangeSet.FlushDeferredEvictions), so an address from one is valid only while
            it is still in the window. Between its entries the publish moves its epoch too while the cache is under pressure, every
            PressuredEpochRefreshInterval entries and never under a live index cursor (Transaction.RefreshBetweenCommitSteps): an entry keeps
            nothing it read for the next. At the same points it gives back its change set's dirty marks (#831): what it published stays owed a
            write, which keeps it from eviction until the checkpoint writes it (PS-10) and is what the cycle collects (CK-13). Kept to the end, the
            marks held the publish's whole write set: 10 000 updates of a component with three indexes into 8 MiB, 300 000 into 256 MiB, filled the
            cache with them and stopped the engine. Waiting with their pins, the publishes held the 32 MiB market's cache: 4,599 of the 4,649 waits 2,000
            or more epochs behind, and the engine stopped (CommitPublishIncompleteException) in two of six storms; refreshed and restarted, in
            none of six. A write past its first latch still waits with its pin (about 17 % of the publish's rounds, measured)
  invariant the rollback of a commit that failed before its append moves its epoch between its entries while the cache is under pressure, as the
            publish does (Transaction.RefreshBetweenCommitSteps): the spawns it un-places (UnplaceSpawns), the revisions it voids (RollbackComponent)
            and the spawned chunks the dispose frees (CleanupEcsState). An entry keeps nothing it read for the next. Held for the whole rollback, the
            failed commit's pin kept the cache its commit had filled, so the rollback met the same wall and so did the dispose's retry (#831). The
            placement before the append does NOT move its epoch, on purpose: its pin makes an oversized commit meet the wall in PREPARE, where it is
            refused (PS-22); moved on, the commit reached its publish, which keeps every mark it takes, and stopped the engine (AP-03)
  never epoch-tag the pages of a read whose length grows with the data: the tags pin every page for the caller's whole scope, and a
        transaction's scope lasts until its next epoch refresh, up to 128 opens, spawns or enumerated entities later (EP-03)
  scope: PagedMMF.AcquirePageForRead, PagedMMF.ReleasePageForRead, IPageStore.AcquirePageForRead, IPageStore.ReleasePageForRead,
         EntityAccessor.BeginScanYield, PagedMMF.RefreshableWaitDepth, PagedMMF.RestartableOpenDepth, BackpressureOpenRestart,
         Transaction.ResolveEntity, PagedMMF.AwaitPageAfterOpenRestart, Transaction.Dispose, UowRegistry.TryClaimFreeSlot,
         PagedMMF.RestartForLockWithin, PagedMMF.OpenRestartAllowed, Transaction.EcsVersionedCopyOnWrite, RevisionEnumerator,
         ComponentRevisionManager.AddCompRev,
         RawValuePagedHashMap.ForEachEntry, RawValuePagedHashMap.CountEntries,
         RawValuePagedHashMap.AnyEntry, EcsQuery.ScanClusterSoa, EcsQuery.FillSnapshotColumn, EnabledGate.IsPresentAtSnapshot,
         EntityVisibility.IsVisiblePendingInScan, Transaction.RefreshEpochBeforeDrain, DeferredCleanupManager.EpochYield,
         LogicalSegment.AcquirePageForRead, LogicalSegment.WalkForwardChainPageCount, LogicalSegment.VerifyGrownChainLinks,
         LogicalSegment.VerifyDirectoryAgainst, ChunkBasedSegment.PageHasFreeChunkFrom, ChunkBasedSegment.FreeAllChunksFrom, BTreeBase.ClearSharedSegment,
         ChunkBasedSegment.ScanForAllocatorState, ChunkBasedSegment.AllocatedChunks, ChunkBasedSegment.IsChunkAllocatedUnpinned,
         ArchetypeClusterState.RebuildActiveList, ComponentRevisionManager.EnumerateVersionedChainHeads,
         ChunkBasedSegment.CreateScanAccessor, ChunkAccessor.LoadIntoSlot, RawValuePagedHashMap.HeadLatch, EcsQuery.TryCountViaOccupancy,
         EcsQuery.CountMatchingCore, EcsQuery.AnyMatchingCore, EcsQuery.CollectMatchingCore, EcsQuery.CollectMatchingFullCore,
         EcsQuery.ScanPerArchetypeBTree, EcsQuery.ScanPerArchetypeBTreeSelective, EcsQuery.ExecuteSpatial, RangeEnumerator,
         DatabaseEngine.CheckClusterVisibilitySummaries, Transaction.EnumerateArchetypeEntities, ArchetypeClusterState.RebuildSpatialStateFromData,
         FreeQuarantine.Free, PagedMMF.BeginRestartableIndexWrite, PagedMMF.EndRestartableIndexWrite, PagedMMF.DisarmIndexRestart,
         PagedMMF.AwaitPageAfterIndexRestart, BackpressureIndexRestart, Transaction.RefreshBetweenCommitSteps, BTree.DeferredReclaim,
         DeferredCleanupManager.FreeChunkWaitingRefreshed, DeferredCleanupManager.CleanupEntityRevisionsBatched
  on_violation: once the data outgrows the page cache, the read waits for evictions its own tags forbid —
                `PageCacheBackpressureTimeoutException` with every slot epoch-protected and none dirty. MarketHardeningTests' trading
                storm stalled that way after about 96 000 transfers (the free-list rebuild the room bits replaced, under a Versioned
                copy-on-write); a load without
                a chunk summary — every crash-path load — could not load a segment larger than the cache (#1144); and once the storm had
                completed, counting its 300 000 audit entries stalled with 12 600 pages epoch-protected and none dirty: the count held every
                cluster page it had read for its transaction
  invariant a crash open's whole-segment scans read and release (#1183): the allocated chunks through ChunkBasedSegment.AllocatedChunks (each
            page's bitmap copied under a read pin), a point check through IsChunkAllocatedUnpinned — IsChunkAllocated epoch-tags its bitmap
            page, and a walk asking it of every chunk pinned the segment — and chunk content through a scan accessor: the active-cluster list,
            the chain-head enumeration, the reservation, the collapse, the sweep, the entity-map rebuild's cluster walk and its EnabledBits
            snapshot, and the spatial layer's rebuild map, serial and parallel (ArchetypeClusterState.RebuildSpatialStateFromData, #1305). Its
            reduce files a cluster's stray slots through an epoch-tagged accessor of its own: one page per cluster that has strays
  note: read-only reads only. Crash recovery's write passes (replay, rebuilds, scrubs) release their pages at batch points (CK-08). A write
        outside recovery keeps its pages resident by its ChangeSet's marks until its next release point: a transaction's epoch refresh (every
        128 operations), PREPARE's component pass (every 1 024 entries), the publish and a failed commit's rollback between entries under
        pressure (#831). The spawn placement has none (above), so a commit whose PREPARE needs more than the cache is refused (PS-22). EP-01's other latent sites are not converted, nor are three query reads that still
        epoch-tag what they touch: an `OrderBy` merge's streams (KWayMergeHelper), the spatial cluster walk (AabbClusterEnumerator) and an
        `AllowMultiple` key's VSBS buffer (RangeEnumerator, through GetBufferReadOnlyAccessor, which keeps chunk addresses across loads)
  verified: WholeSegmentScanTests — a segment four times the cache: the load scan, an allocation's search across room bits on full pages and
            the chain walk complete inside
            one epoch scope, and each fails with every one of the 128 slots epoch-protected when its reads are epoch-tagged again
            [VerifiesRule]; the same fixture pins the pair's semantics (not evicted until released, no epoch tag, no clock bump, readers
            racing eviction always get their page)
  verified: WholeSegmentScanTests.TheSharedIndexClear_ReleasesASegmentLargerThanTheCache [VerifiesRule] — 256-byte and String64 index
            segments four times the cache, sparse and full, cleared inside one epoch scope: only directory chunks 0-3 stay allocated and the
            directory is empty; each case fails with the 128 slots epoch-protected when the clear walks per chunk again.
            PublishLargerThanTheCacheRecoveryTests.APublishThatGaveBackItsMarks_IsRecoveredWhole_AfterAHardCrash [VerifiesRule] — the crash open of
            an index segment larger than its 8 MiB cache completes, every key and value the committed one (timed out on back-pressure before).
            ChunkBasedSegmentBitmapL3Tests.FreeAllChunksFrom_IgnoresTheBitsPastAPagesLastChunk [VerifiesRule] — padding bits set on two pages:
            the reset leaves the count at chunk 0's alone, equal to the bitmap's, and the padding bits as they were
  verified: QueryScanCacheBoundTests.PagesAWriterTagsDuringALongWalk_AreNotHeldByTheWalksEpoch [VerifiesRule] — a writer commits 8 000 updates at
            a walk's second yield; by its fourth, fewer than 16 pages are epoch-protected, the entity-map count and the SoA scan alike (604 with the
            yield's refresh removed). AWalkWaitingOnBackpressure_DoesNotHoldTheCacheWithItsEpoch [VerifiesRule] — the yield refresh off, a writer
            fills the cache (1 012 of 1 024 pages epoch-held) and stalls; the walk's next miss waits, moves its epoch, and both finish (with the wait's
            refresh removed: the back-pressure timeout, nothing dirty, 1 012 epoch-protected)
  verified: RevisionChunkRetentionTests.ATailsDrainThatThrows_StillLeavesItsEpochAndTheChain_AndGivesBackItsMarks [VerifiesRule] — the drain throws
            on its third entry: the dispose rethrows, the thread is in no epoch scope, the chain is empty, no page is marked, the engine goes on (the
            thread stays pinned with the finally removed); TheTailsDrain_GivesBackItsMarksAsItGoes [VerifiesRule] — refreshed between entries the
            drain holds 1 marked page at most, 10 without the release; TheDrainsChunkFrees_WaitWithTheirEpochMoved [VerifiesRule] — three
            back-pressure rounds forced on a drain's frees each move its epoch and draw nothing from the slot reserve (none move it with the scope
            removed); ARevisionCleanupsChainCheck_WaitsWithItsEpochMoved [VerifiesRule] — three rounds forced on a revision cleanup's ownership check
            before its chain lock each move the drain's epoch and draw nothing (the scope removed, none moves it and the check draws)
  verified: LockReleaseOnThrowTests.AnOpenWaitingOnAHeldChain_MovesItsEpochWhileItWaits_AndGoesOnOnceItIsReleased [VerifiesRule] — another thread
            holds the chain: the open restarts, its pin moves, and the write commits once the chain is released (no restart and no epoch move with the
            lock restart removed)
  verified: EpochRefreshCadenceTests.AnOpenMeetingBackpressure_UnwindsToItsStart_MovesItsEpoch_AndRunsAgain [VerifiesRule] — three forced rounds
            of back-pressure under an open that misses: three restarts, the epoch moved by each, the open's answer unchanged
  verified: QueryScanCacheBoundTests — the occupancy count, the entity-map count and existence check, the SoA scan, the index range scan and the
            unfiltered collect over a 625-page archetype, inside one read-only transaction, leave at most 5 pages epoch-protected; with the scans
            epoch-tagged again they leave 619 (count), 467 (SoA scan), and 41 with only the index range scan reverted; after each read no page
            is left held by a slot reference [VerifiesRule]
  verified: WholeSegmentScanTests — the allocated-chunk cursor and the unpinned check walk a segment four times the cache inside one epoch scope
            (the check epoch-tagged again times out) [VerifiesRule]; MarketHardeningTests — the crash open of a 0.4 GiB market over a 64 MiB cache
            completes (it timed out with all 8 192 slots epoch-held)
  verified: ClusterVisibilitySummaryIntegrityTests.TheAudit_OfAnEntityMapLargerThanTheCache_Completes — the integrity check's cluster-visibility
            audit and a transaction's EnumerateArchetypeEntities walk an EntityMap larger than an 8 MiB cache [VerifiesRule]. Each walked it
            through an epoch-tagged accessor, and the market's integrity check timed out once its audit map outgrew 64 MiB; both mutations killed
  verified: CommitExceedingThePageCacheTests.APublishLargerThanTheCache_GivesBackItsMarks_AndCommits [VerifiesRule] — 20 000 updates of a
            component with three indexes commit into 8 MiB, every value and index entry the new one (without the marks given back, the publish
            fails and the engine stops); PublishLargerThanTheCacheRecoveryTests — the same publish, then a hard crash: recovered whole
  verified: CommitExceedingThePageCacheTests.ACommitLargerThanTheCache_IsRefusedBeforeItsTimeout_AndRollsBackClean — the refused commit's rollback
            completes and its dispose throws nothing (with the rollback's refreshes removed, the dispose throws a back-pressure timeout after 5 s);
            ACommitJustOverTheLimit_IsRefusedBeforeItsAppend — 50 000 spawns into 8 MiB are refused before the append (with a refresh between the
            placements, the publish failed and stopped the engine)
  verified: ClusterRebuildMergeTests.TheRebuild_OfAClusterSegmentLargerThanTheCache_Completes — the spatial rebuild of a cluster segment larger than an
            8 MiB cache, under one epoch scope as the open's, serial and on 4 workers [VerifiesRule]; epoch-tagged again, both time out

### EP-03: No reference the engine hands out outlives the call that produced it, except a fast-path one within its step `[fatal]`
  invariant the point-access handles hand out values, never references into a page: EntityRef.Read / TryRead / ReadRaw copy the
            component out, EntityRefMut.Set copies it in and does the component's bookkeeping in the same call, and the generated
            ReadAll / WriteAll copy every component. The zero-copy cores (EntityRef.ReadSlotRef, EntityRefMut.WriteRef) are internal and
            used only within one call
  invariant a handle may keep a pointer across calls only while something other than the epoch holds its page: an EntityRef's cluster
            base is held by the slot of the accessor that resolved it (SlotRefCount). Every such accessor reports a page it lets go of —
            a slot evicted to make room, the accessor disposed (its cluster-cache entry given to another archetype: four are kept, least
            recently used replaced) — to its EntityAccessor's ClusterGeneration, and the handle re-resolves its base from the cluster id
            it kept (EntityRef.ClusterBase) once the generation has moved. Transient pages never move: their accessors report nothing
  invariant a fast-path reference — a ClusterRef and every span or ref it hands out, an index enumerator's CurrentComponent — is valid
            until its enumerator's next MoveNext or Dispose; the enumerator's own accessor holds the current page for that long. Under
            strict mode (CheckConfig.Enabled) a ClusterRef used after its step throws, from every data member
  invariant so the epoch may be refreshed at any call boundary: a transaction refreshes it every 128 entity opens
            (EntityAccessor.NoteEntityOpen, read-only transactions included), every 128 spawns and every 128 enumerated entities. Its
            pinned set is then bounded by what it touched since the last refresh, not by everything it read. A PTA worker accessor is
            refreshed once per system instead (PointInTimeAccessor.FlushWorker), which also releases its cluster cache: a refresh
            increments the global epoch, which sends every other thread's warm B+Tree accessor down its cold path, so what a worker's
            epoch and cluster cache hold is bounded by the system's run
  invariant while the page cache is under back-pressure, and for PagedMMF.PressureHold after its last back-pressure round, those cadences
            shorten to every 8 (EntityAccessor.PressuredEpochRefreshInterval; PagedMMF.UnderPressure, read every 8 operations by
            EntityAccessor.EpochRefreshDue). The epoch keeps every page any thread touched since the oldest pin — about pinned threads × the
            interval × the pages an operation touches — so at 128, 32 threads over a 64 MiB cache needed twice the cache, epoch-held with
            no skewed pin (#1230). A back-pressure timeout names the pinned threads and the oldest pin's lag, and when the cache is mostly
            epoch-held, the two causes they tell apart (PageCacheBackpressureTimeoutException)
  invariant a stored component's collection is changed through EntityRefMut.CreateComponentCollectionAccessor(comp, ref copy,
            ref copy.Field), which creates a Versioned component's new revision before the accessor exists, so a buffer the committed
            revision still points to is cloned, never edited in place. Opening it records the write in every storage mode — the
            copy-on-write, the SingleVersion dirty bit the fence logs from, or the Commit-discipline staged slot — and refuses a copy whose
            handle is not the stored one. Every buffer the accessor moves to (first allocation, clone, Replace, Clear) is stored into the
            component at that moment, not at dispose, so no Set is needed and no exit leaves the stored value naming a buffer the accessor
            released; a Replace stores its new buffer before it gives back one reference on the old one, and never writes the old one
  invariant a Commit-discipline staged value holds its own reference on every collection buffer it names, taken when it is seeded from HEAD
            (EntityAccessor.ShareStagedCollections): the publish hands those to HEAD and then gives back HEAD's previous ones, after the
            memcpy (Transaction.PublishStagedEntry); a rollback or a same-transaction destroy gives back the staged value's
            (Transaction.ReleaseStagedCollections). Neither reads the other's state — a release decided from HEAD at publish time freed a
            buffer a second Commit transaction's staged value still named
  invariant no whole-value store changes a collection handle: it counts no reference, so an empty handle in the value keeps the stored
            collection and a handle naming another buffer throws (CollectionHandles.Keep — EntityRefMut.Set, ClusterRef.Set,
            EntityAccessor.Teleport). Enable(comp, value) alone adopts one, into a component holding none
  never cache a pointer into a page across calls on the strength of the epoch alone, and never hand one to a caller: a refresh at the
        next call boundary makes the page evictable while the pointer lives on
  scope: EntityRef.Read, EntityRef.TryRead, EntityRef.ReadRaw, EntityRef.ReadSlotRef, EntityRef.ClusterBase, EntityRefMut.Set,
         EntityRefMut.WriteRef, EntityRefMut.CreateComponentCollectionAccessor, CollectionHandles.Keep, ComponentCollectionAccessor.Replace,
         EntityAccessor.ShareStagedCollections, Transaction.ReleaseStagedCollections, Transaction.PublishStagedEntry, ClusterRef.Set,
         EntityAccessor.Teleport, EntityAccessor.ResolveClusterBases,
         EntityAccessor.ReresolveClusterBase, EntityAccessor.EnsureClusterCache, EntityAccessor.NoteEntityOpen,
         EntityAccessor.EpochRefreshDue, PagedMMF.UnderPressure, PointInTimeAccessor.FlushWorker, ChunkAccessor.TrackGeneration, ClusterRef.CheckStep, ClusterEnumerator.MoveNext
  on_violation: the cache gives the slot to another page while the pointer lives on — reads return another entity's values, and a
                write through the pointer lands in another entity while the intended write is lost (#1199: a handle opened, then an
                archetype switch and 130 spawns or a 200-entity enumeration, then another thread's reads: wrong values in 2 to 4 runs of
                4, the write case in 4 of 4). Bounded the other way — no refresh — a read-only transaction pinned every page it read
                until disposed and timed out on back-pressure with 510 of 512 slots epoch-protected (#1189)
  note: the 128 refresh was added (2026-03-06) when reads copied, so nothing handed out could be left over an evictable page. The
        zero-copy EntityRef API that replaced them (2026-03-23) kept the refresh, and the deferred SlotRefCount release built for the
        engine's own pointers covered neither handles nor read-only transactions
  verified: EntityHandleLifetimeTests — a handle opened, its accessor's hold on the page dropped (its archetype pushed out of the cluster
            cache, 100 other clusters, or the For<T>() accessor disposed), the epoch moved (enumeration, spawns, or a worker flush), then
            the page evicted and its slot given to another page: the handle reads and Sets its own entity, as do a For<T>() handle and a
            PTA worker's; a Transient Set captures the entity's own index key and a Commit-discipline partial write seeds from it. With
            ClusterBase trusting the cached pointer, all nine fail [VerifiesRule]; one read-only transaction opens 45 000 entities over a
            512-page cache and peaks at 45 epoch-protected pages (sampled every 1 024 opens), and without the open refresh times out with
            510 [VerifiesRule]; a ClusterRef used after its enumerator's next MoveNext or Dispose throws under strict mode [VerifiesRule];
            handles of two archetypes used in turn leave the generation unmoved, which a one-entry cluster cache fails
  verified: EpochRefreshCadenceTests — a transaction refreshes every 8 opens while the cache is under pressure and every 128 once the hold has
            passed and an allocation found a slot; red with the cadence fixed at 128 [VerifiesRule]; the timeout message names the pinned
            threads, the oldest pin's lag and the working-set cause when the cache is mostly epoch-held
  verified: ComponentCollectionTests.Collection_ChangedThroughAHandle_LeavesTheCommittedRevisionsBufferAlone — an older snapshot still sees
            ten elements after a handle-made change commits eleven; without the copy-on-write first, it sees eleven [VerifiesRule];
            Collection_ChangedThroughAHandle_WithoutASet_IsStored — the change is stored with no Set, and fails without the write-back
  verified: ComponentCollectionReplaceTests — a rollback past an undisposed accessor (Add or Replace) keeps the committed buffer, red with
            the write-back at dispose [VerifiesRule]; a SingleVersion append with no Set marks the entity dirty, red when only Versioned
            records its write [VerifiesRule]; under the Commit discipline another transaction sees the old content until the commit, and
            the publish, a rollback and a same-transaction destroy each release exactly the buffers no committed value names [VerifiesRule];
            two Commit transactions staging one entity end last-publish-wins with no double free, red without the staging reference or with
            a release decided from HEAD [VerifiesRule]; a Set or a ClusterRef.Set with an empty handle keeps the collection, a stale or
            foreign one throws, and an accessor refuses a stale copy [VerifiesRule]
  verified: EntityRefMutTests.EntityRef_ExposesNoWriteMember_EntityRefMutDoes — no public member of either handle returns a reference
            [VerifiesRule]

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
                whatever ordinary call happened to be next, killing the process. Measured: a demo soak died at
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
