# Durability Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-08-02 |
| Domain | WAL, Checkpoint, Crash Recovery, Page Safety, Versioned HEAD Reopen |

> Type-location notes (post-#329 layout): WAL/Checkpoint/Recovery internals live in
> `src/Typhon.Engine/Durability/internals/` (`WalWriter.cs`, `WalCommitBuffer.cs`,
> `WalManager.cs`, `WalSegmentManager.cs`, `WalFileIO.cs`, `IWalFileIO.cs`,
> `RecordCodec.cs` (the sole WAL-record byte owner), `RecoveryDriver.cs` +
> `RecoveryApplier.cs` (the v2 logical-record apply), the surviving v1 `WalRecovery.cs`
> scan, `CheckpointManager.cs`, `StagingBufferPool.cs`). Public surface is two types in
> `Durability/public/`: `WalWriterOptions`, `WalRecoveryResult`. `IWalFileIO` is
> `internal` — `DatabaseEngine` constructs `WalFileIO` directly inside
> `InitializeWalManager` and during recovery (`Recover` path). Page seqlock,
> ACW counters, and DirtyCounter live in `Storage/internals/PagedMMF.cs`.
> Scope fields below cite class/method names; folder paths follow the layout above.

> Invariants that protect Typhon against data loss, silent corruption,
> and unrecoverable states across crashes.

---

## Module: LOG — WAL v2 log append & format

WAL v2 records carry **logical truth only** — component identity is the per-archetype **slot** (0..15) under the per-DB **routing id** carried by the record's
`EntityId` (i.e. `(routingId, slot)`), never pages/chunks/bufferIds and never the process-global dense `ComponentTypeId` (#514 Phase 2).
Exactly one codec (`RecordCodec`) owns all record bytes. Source: `claude/design/Durability/MinimalWal/02-wal-format.md`,
`07-rules.md`. Landed in P1.1 (codec + emitters M1/M3/M4/M6/M7). LOG-03 (torn-tail truncation) and LOG-04 (commit-marker
gating) land with the `RecoveryDriver` in P1.2; LOG-05 (DurableLsn honesty) shipped in P0.2 — see `WP-03`.

### LOG-01: Append failure throws `[fatal]`
  invariant ∀ batch: IDurabilityLog.Append either appends every record or throws — never a sentinel
  scope: EVERY append entry point on IDurabilityLog — Append and AppendFenceBlocks (the columnar path added by
         #559, which obeys the same discipline); WalCommitBuffer.cs (TryClaim throws WalBackPressureTimeout /
         WalClaimTooLarge)
  on_violation: an acknowledged commit with missing records → silent data loss

### LOG-02: Single codec `[fatal]`
  invariant ∀ code that writes or reads WAL record bytes: that code ∈ RecordCodec
  scope: RecordCodec.cs, RecordFormat.cs
  enforced_by: `.github/workflows/merge-gate.yml` job `invariants`, step "LOG-02" — fails the merge gate if
     `RecordFormat` (the byte-offset layout) is referenced from anywhere outside `Durability/internals/`. Anyone
     hand-rolling a record needs those offsets, so that reference is the greppable tell for a second byte writer.
     Built 2026-07-29; the gate was claimed in the docs from an earlier design but did not exist until then, and
     the invariant held by convention alone for the whole interval.
  verified: RecordCodecPropertyTests, FenceBlockCodecTests (both already tag this rule)
  on_violation: format drift between producers and recovery → unreplayable records

### LOG-03: Torn tail truncation `[fatal]`
  invariant recovery truncates at the last chunk whose CRC chain validates; records past it are ignored
  invariant the stop is unconditional — a CRC break ANYWHERE (mid-log in a sealed segment, not only a torn tail on the
            last one) ends the scan; REC-01 states the general form
  scope: RecoveryDriver.cs, WalSegmentReader.cs
  on_violation: records past the boundary have no CRC-chain guarantee — partially flushed, uncommitted, or stale bytes
                from a recycled segment get applied as committed
  spec: rules/tla/CommitRecovery.tla, modelled at :164. NOTE the spec models the log as ONE Seq truncated to a prefix,
        so multi-segment mid-log corruption is unrepresentable there; a green check does not cover it.
  note migrated from design/Durability/MinimalWal/07-rules.md, 2026-07-28

### LOG-04: Commit-marker gating `[fatal]`
  invariant tx T's records applied ↔ T's TxCommit record ∈ valid prefix
  requires: LOG-03
  scope: RecoveryDriver.cs (:107-110 collect committed TSNs, :175 apply gate)
  on_violation: partial transaction applied → atomicity break (TXW-4 class)
  spec: rules/tla/CommitRecovery.tla (S2) — LOG04_MarkerGates
  note migrated 2026-07-28. This is the invariant deciding whether a transaction is applied at all, and it sat in a
       design-doc staging file from P1.2 onward — cited by code and two TLA+ specs but absent from the oracle.

### LOG-05: DurableLsn honesty `[fatal]`
  invariant DurableLsn ≤ max LSN contained in frames physically written and fsynced
  scope: WalWriter.cs
  on_violation: false durability acknowledgment (TXW-2)
  spec: rules/tla/CommitRecovery.tla (S2) — LOG05_DurableHonest
  note this is a MAX BOUND on the watermark, not a completeness property. It does NOT require the write to have been
       attempted, and it does NOT give prefix-completeness — see WP-03 (drain completeness) and WP-02 (Immediate
       acknowledgment). It supersedes neither, contrary to the migration plan.

### LOG-06: No physical identifiers in records; identity = (routingId, slot)
  never page index, chunk id, bufferId, or chain topology appears in any record payload or header
  invariant component identity on the wire = the per-archetype slot (0..15) resolved under the EntityId's per-DB routing id — (routingId, slot), BOTH durable
           (routing id persisted in ArchetypeR1.RoutingId + re-matched by name on reopen; slot order durable via ArchetypeR1.ComponentNames).
           NEVER the process-global dense ComponentTypeId — that is an in-memory-only handle, not persisted, and may be renumbered across runs.
  rationale (#514 Phase 2): the old dense ComponentTypeId-on-wire coupled replay to registration order → a crash→reopen under a shifted order could mis-map
           post-checkpoint records (silent data loss). (routingId, slot) is stable across crash→reopen by construction — this dissolves that risk.
  scope: RecordCodec.cs, RecordFormat.cs (SlotRecordBody.SlotIndex, CollectionDeltaRecordBody.SlotIndex); recovery RecoveryApplier maps (routingId, slot) → table
         ComponentTable.CollectionHandleRanges (the packed ranges the codec zeroes); RecordCodec.PackColumnHandleRange (the columnar twin)
  on_violation: the log is coupled to physical layout OR to registration order → replay breaks under page relocation / compaction / shifted schema order
  verified: CollectionDurabilityTests.Commit_WithCollections_PutsNoBufferIdOnTheWire [VerifiesRule],
    Log06Verifier_RejectsABufferIdOnTheWire [RuleMutant] — re-covered 2026-08-11 (#389) after the 2026-08-07
    retirement (#703). The retired fixtures (RecordCodecPropertyTests, FenceBlockCodecTests) are "Pure — no engine,
    no recovery": they construct records themselves and round-trip them, so neither can observe the EMITTER placing
    a physical identifier on the wire, which is what this rule constrains. They stayed green in the same build as a
    red production probe (#389). The verifier now drives a LIVE commit and reads the bytes back off the on-disk WAL
    (WalScanner), asserting no bufferId appears at any collection-handle offset. It covers BOTH emitters — the
    per-entity CommitBatchBuilder path and the columnar FenceBlock path (#559), which bulk-copies component columns
    straight out of the cluster page and had no zeroing at all until #389 — and fails if a run inspected only one.
  note the emit side was UNWIRED, not merely unverified, until #389: all four production AddSlot sites omitted the
       optional handleRanges argument, so RecordCodec.ZeroHandleRanges iterated an empty span on every commit. The
       zeroing code was correct and never executed. This is why the rule's coverage and its implementation landed
       together — a verifier written first would have been red, and a fix without one would have been unfalsifiable.

### LOG-07: Batch internal order
  invariant within one transaction's batch the record order is Spawn lifecycle → Slot/CollectionDelta →
            Destroy/SetEnabledBits → BulkManifest; enforced at build time by CommitBatchBuilder bucketing
  scope: CommitBatchBuilder.cs (a mis-ordered batch is unconstructible by API shape)
  on_violation: apply encounters a SlotRecord for an unspawned entity → recovery must fail loudly (03 §9.e)
  verified: BatchOrderTests, RecordCodecPropertyTests [VerifiesRule]

### LOG-08: LSN is globally monotonic across sessions `[fatal]` `[silent]`
  invariant on every open the WAL LSN allocator continues STRICTLY ABOVE the durability frontier:
            `CommitBuffer.NextLsn ≥ max(recovered-WAL-LastValidLSN, persisted-CheckpointLSN) + 1`. A reopened writer must
            NOT restart record LSNs at 1.
  invariant (crash path) the frontier is the one this session's WAL v2 recovery REPLAYED, not merely what was persisted before it.
            On a crash reopen both terms above are 0 at WAL-init time — no checkpoint ran, and v2 recovery has not started — so the
            floor MUST be raised again once `RecoveryDriver.Result.MaxLsn` exists, and BEFORE the engine accepts its first
            transaction. There is exactly one point in the open sequence that satisfies both, at the end of `RunWalV2Recovery`.
  invariant (seed-pairing) the durable watermark MUST be seeded to the same frontier alongside the allocator:
            `DurableLsn := NextLsn - 1` (= frontier) at reopen, BEFORE the writer starts. Seeding `NextLsn` forward without
            seeding `DurableLsn` is itself a violation — it leaves `DurableLsn < LastAppendedLsn` (= `NextLsn-1`) on an idle
            reopened session, where no new frame will ever publish that LSN.
  rationale: recovery applies exactly the records with `Lsn > CheckpointLSN` (03 §3 — records at/below are already in the
             data file). If a reopened session's records fall at/below a PRIOR session's persisted CheckpointLSN, the entire
             post-reopen window is skipped as already-consolidated — a durably-acked (Immediate) commit is silently lost.
             The same gap leaves `durableLsn ≤ checkpointLsn`, so the checkpoint thread stays dormant after reopen (post-reopen
             data never consolidates, WAL never recycles). The frontier's LSNs were durable in the prior session (recovered from
             disk), so seeding `DurableLsn` to it is sound — and necessary: the CK-02 barrier blocks on `WaitForDurable(LastAppendedLsn)`,
             and an unseeded `DurableLsn` makes that an unreachable target on an idle reopen (a `WalBackPressureTimeout` per cycle).
  scope: WalCommitBuffer.SeedNextLsn (the allocator seed); WalWriter.SeedDurableLsn / WalManager.SeedDurableLsn (the durable seed);
         WalManager.Initialize (seeds NextLsn from firstLSN, matching the active segment header); DatabaseEngine.InitializeWalManager
         (`frontierLsn = max(LastValidLSN, CheckpointLsnWatermark)`, then BOTH `SeedNextLsn(frontier+1)` AND `SeedDurableLsn(frontier)`);
         the crash-recovery path also calls `SeedDurableLsn(replayed-frontier)` — AdvanceDurable is a monotonic max, so the two are
         idempotent. Monotonic + pre-Start, so a fresh DB (frontier 0 → firstLSN 1) and a bulk-load DB (CheckpointLSN 0) are unaffected.
         `DatabaseEngine.SeedWalFrontierAfterRecovery` / `WalManager.SeedRecoveryFrontier` (the crash-path re-seed, called from
         `RunWalV2Recovery` once `MaxLsn` is known). `SeedNextLsn`'s own contract is "before the writer thread starts"; that call
         site has it running, so `SeedRecoveryFrontier` asserts the equivalent quiescence (`WalCommitBuffer.NothingClaimedYet`) and
         THROWS rather than rebasing over records that were already claimed — those would stay below the new floor, still colliding.
  on_violation: (allocator) hard-crash after a clean-shutdown-then-reopen loses every post-reopen commit (the One True Crash Test's
                blind spot — it crashes on a FRESH open, never after a reopen). (crash path) the same loss for every engine that
                keeps running after recovering — i.e. every real deployment, since recovery is followed by serving traffic. Silent:
                the commits are acknowledged as durable, reads see them, and the next recovery discards the whole window as
                already-consolidated. Measured (#712): frontier 16, `LastAppendedLsn` 9, `DurableLsn` 16 — `LastAppendedLsn <
                DurableLsn` is itself the tell, the writer believing LSNs it never appended are durable — and the next open scanned
                8 records and applied 0, losing BOTH sessions' entities rather than just the post-recovery ones. (seed-pairing) a clean reopen with a persisted
                CheckpointLSN > 0 stalls every engine's dispose ~30 s (the CK-02 barrier times out waiting for `NextLsn-1`), turning
                the test suite into a crawl and the checkpoint thread's `Thread.Join` at shutdown into a near-deadlock.
  verified: PostReopenWindow_AfterPriorSessionCheckpoint_SurvivesCrash [VerifiesRule], asserting recovery MaxLsn > checkpointLsn
            and RecordsApplied > 0 (cross-session) — its session 1 shuts down CLEANLY, which is why it never covered the crash
            path; DifferentialRecoveryOracleTests.PostRecoveryWrite_ContinuesTheLsnSequenceAboveTheReplayedFrontier [VerifiesRule]
            is the crash-path half, asserting on the watermarks rather than on entity survival so it fails if and only if the LSN
            floor is wrong; CheckpointFrontier_BelowAndWindow_BothRecoverWithIndex is the same-session
            control (LSN already monotonic within a session). The seed-pairing clause is currently verified only empirically (the
            full suite stops stalling/host-crashing on dispose); a deterministic reopen-idle-dispose barrier test is a follow-up.

---

## Module: AP — Commit pipeline & apply

Append-before-publish (AP-01) and the point-of-no-return discipline (AP-02/03) govern `Transaction.Commit`; the apply rules
(AP-10..13) govern recovery. Source: `claude/design/Durability/MinimalWal/07-rules.md`, `01-architecture.md §5`. AP-01/02/03
landed in P1.1 #395 (commit pipeline reorder, 2026-06-13); AP-10..13 landed in P1.2 (`RecoveryDriver` /
`RecoveryApplier`).

> 🔴 **status: PARTIAL (2026-07-28).** This header previously asserted AP-10..13 as closed. One residual is open and
> `[fatal][silent]`: when a recovered entity has no Spawn in the window, `RecoveryDriver` applies only Destroy and
> SetEnabledBits — **the aggregated Slot values are silently discarded**, so durably-logged, marker-committed value
> updates to pre-existing (checkpointed) entities are dropped. The in-code comment concedes it ("a base-entity value
> update … is a later increment"). Tracked as #569. Every recovery workload puts a spawn and its updates in the SAME
> window, so nothing crosses the checkpoint frontier on one entity and no test catches it.

### AP-01: Append before publish `[fatal]` `[silent]`
  invariant `[log.Append(tx batch)]` strictly precedes `[any visibility of the tx's changes: IsolationFlag clear, HEAD→cluster
            memcpy, EntityMap spawn insert, EnabledBits, DiedTSN, cluster slot ops]`
  scope: `Transaction.Commit` pipeline — `PrepareComponent` (fallible work, no visibility) → `AppendToWal` →
         `PublishPreparedComponents` + `FlushEcsPendingOperations` (publish). A Debug guard `_appendPhaseEnteredThisCommit`
         asserts the order at the start of the publish phase
  on_violation: a checkpoint can capture never-durable state → phantom data after crash (UOW-4/TXW-3 class)
  verified: AppendBeforePublishTests (A1.5), DifferentialRecoveryOracleTests (flat workloads)
  note: handler-conflict commits hold the per-entity revision-chain lock from PREPARE through PUBLISH (spanning the staging
        Append) so `[detect, resolve-against-committed, IsolationFlag clear]` stays one atomic region — required by
        ConcurrencyConflictTests concurrent delta-rebase. This refines 07-rules' "re-acquire in publish" wording

### AP-02: Append is the point of no return `[fatal]`
  invariant all conflict validation precedes Append; post-Append the tx reaches Committed and publish does not roll back. A
            durability-WAIT failure after publish surfaces as `CommitDurabilityUncertainException(highLsn)` (committed, durability
            unconfirmed), never as a rollback
  scope: `Transaction.AppendToWal` (point of no return), `Transaction.WaitAndFinalize` (publish-then-surface-uncertain)
  on_violation: appended-but-rolled-back state → recovery resurrects a rolled-back tx

### AP-03: Publish is non-throwing `[fatal]`
  invariant the publish phase performs no fallible allocation and no bounded-timeout lock acquisition
  scope: `Transaction.PublishComponent` / `PublishClusterVersionedSlot` (component publish); `FlushEcsPendingOperations` /
         `FinalizeSpawns` (spawn publish)
  status: **PARTIAL.** Component publish is non-throwing — the revision handle is reconstructed in publish from coordinates
          captured in PREPARE (no locking `GetRevisionElement` walk), and the publish acts are field writes + memcpy; the publish
          drain releases each retained lock exactly once via a drain cursor. **RESIDUAL:** the spawn publish (`FinalizeSpawns`)
          can still throw on allocation (cluster `ClaimSlot` grow, EntityMap `InsertNew` grow, index B+Tree node alloc under
          page-cache backpressure). Full closure is P2-entangled — the clean sentinel-`BornTSN` flip is blocked for cluster by
          the SoA occupancy-based iteration (it would leak prepared, not-yet-published spawns to bulk SoA scans), so eliminating
          spawn-publish throws requires pre-growing all spawn segments before Append and/or unbounded-watchdog insert locks.
          Tracked: **#396** (to be done with the P2 cluster-durability rework)
  on_violation: partial publish with no compensation (TXW-8 class)

### AP-10: Single apply routine `[fatal]`
  invariant recovery mutates engine state only via the RecoveryApplier ops → the engine's normal write paths
  scope: RecoveryApplier.cs (ApplySpawnedEntity / ApplyDestroyToExisting / ApplySetEnabledBitsToExisting
         — the file that performs every mutation), RecoveryDriver.cs, DatabaseEngine.cs (recovery
         orchestration). Corrected 2026-07-28: the old scope named `ApplyCommitted` and
         `DatabaseEngine.Recovery.cs`, neither of which exists.
  on_violation: a divergent second write path (the WalReplayHelper class of bugs, TXW-6)

### AP-11: Records are CONSUMED in ascending LSN order `[fatal]`
  invariant committed records are consumed in ascending LSN order, so per-(entity, slot) last-write-wins is
            order-equivalent to a strict per-record replay
  invariant apply order ACROSS entities is unconstrained — entities are independent
  scope: RecoveryDriver.cs (LSN sort, then per-entity aggregation, then apply)
  rationale: 🔴 CORRECTED 2026-07-28. This rule previously read "applied in ascending LSN order; no coalescing (D4)".
    The driver deliberately DOES coalesce: it sorts by LSN and folds records into a per-entity aggregate with per-slot
    overwrite, collapsing each component's history to its final value, then applies once per entity over dictionary
    order. That is "approach B" — the live engine has no in-place Versioned location update, so recovery must
    build-then-insert. The old text read as a prohibition the code openly breaks, which is worse than silence.

### AP-12: Apply idempotence `[fatal]`
  invariant ∀ base B, window W, prefix P ⊆ W: Apply(Apply(B,P), W) ≡ Apply(B, W) — spawn-if-absent, destroy-if-present,
            absolute EnabledBits, value overwrite, collections folded-then-Set
  scope: RecoveryApplier.ApplySpawnedEntity / ApplyDestroyToExisting / ApplySetEnabledBitsToExisting
  🔵 collections clause UNBUILT: "collections folded-then-Set" is not implemented on either side — the builder's
     AddCollectionDelta has no production caller and the driver defers CollectionDelta apply.
  🔴 TEST-TAG WARNING (2026-07-28): four tests carry `[VerifiesRule("AP-12")]` and NONE of them re-applies anything —
     they are single-pass crash → reopen → assert. The one genuine idempotence test drives RecoveryApplier directly and
     applies the same spawn twice, and it carries NO tag. The coverage gate therefore scores AP-12 as covered four
     times over while the property is tested once, for spawn-if-absent only. Move the tag; do not trust the count.
  spec: rules/tla/CommitRecovery.tla (S2) — AP12_ApplyIdempotent (where idempotence is actually proven today)
  on_violation: a crash during recovery corrupts on re-run

### AP-13: Allocation tolerance
  invariant physical placement chosen at apply may differ from pre-crash; all references (EntityMap, cluster bookkeeping) are
            updated through the same path; orphans are swept in Phase 4
  scope: RecoveryDriver.cs Phase 3–4

---

## Module: CK — Checkpoint v2

The checkpoint cycle (`CheckpointManager`; a planned rename to `SnapshotStore` was never carried out) advances `CheckpointLSN` over durably-written
pages and recycles WAL segments. Source: `claude/design/Durability/MinimalWal/04-checkpoint.md §1/§5/§7`, `07-rules.md`. Status:
CK-03 (coverage gate) + CK-04 (recycle bound) landed in P0.1; **CK-02 (durability barrier), CK-06 (failure classification), and
CK-07 (sealed-segment lock) landed in P1.3 Increment A (2026-06-13); **CK-05 (A/B slot-pairing) — meta pair (C1) + segment-directory
twins (C2) — landed in P1.3 (2026-06-13).** CK-01 is covered by the barrier below; FPI retirement (D — **unblocked by C2**) and
CK-08 (flush-only cycles) are later increments.

### CK-03: Coverage gate `[fatal]`
  invariant CheckpointLSN advances only when every collected dirty page was written this cycle
            (v1 binary gate; a refinement may use per-page firstDirtyLsn)
  scope: CheckpointManager.cs — the stillSkipped == 0 gate wrapping Steps 6/7/8, after at most MaxCoveragePasses passes
  on_violation: STO-1 — committed-data loss, permanent once the segment is recycled
  spec: rules/tla/CheckpointProtocol.tla (S1) — NoLostPage. The -mutant.cfg (BreakCoverageGate=TRUE) removes exactly
        this gate, and TLC proves the result violates NoLostPage.
  requires_reciprocal: CP-11 — skipping an ACW>0 page is only safe BECAUSE this gate holds the watermark
  note migrated 2026-07-28. Its absence was load-bearing: CP-05 omitted the gate too, so the coverage requirement was
       asserted NOWHERE in the oracle — implementing from the rules alone reproduced the model-checked mutant.

### CK-04: Recycle bound `[fatal]`
  invariant segment reclaimable ↔ segment.lastLSN ≤ persisted CheckpointLSN ∧ no recovery in progress
  requires: CK-03
  scope: WalSegmentManager.cs, RecoveryDriver.cs
  on_violation: needed records destroyed (STO-1, second half)
  spec: rules/tla/CheckpointProtocol.tla (S1) — CK04_RecycleBound + NoLostPage
  note migrated 2026-07-28. Two deviations recorded rather than silently reconciled: the implementation uses `<` not
       `≤` (WalSegmentManager.cs:321 — strictly more conservative, see WP-09), and the "no recovery in progress"
       conjunct is implemented by NOTHING (no recoveryInProgress / IsRecovering guard exists anywhere). It may be
       structurally unreachable if the checkpoint thread cannot run during recovery; that is UNVERIFIED.

### CK-05: Protected-page alternation `[fatal]`
  invariant a protected page (meta pair = file pages 0–1; segment-directory pairs in C2) is persisted ONLY by writing the
            NON-current slot with `PairGeneration = current+1` + a valid CRC, then fsync; the current-valid slot is NEVER
            overwritten
  post: at open, ∃ ≥1 CRC-valid slot per protected page; selection = highest valid `PairGeneration`; both-invalid → open
        fails loudly (never a silent fallback)
  scope: META PAIR (C1): `ManagedPagedMMF.PersistMetaNow` (write: alternate slot + gen + CRC + fsync + flip), `LoadMeta` (read:
         both slots, pick highest valid gen), `MapReadOffset` (page 0 → current slot), `IsExternallyPersisted` (meta pair excluded
         from the checkpoint dirty-write). DIRECTORY TWINS (C2): `PersistProtectedPage` (the atomic write protocol under `_pairLock`,
         shared by `WritePagesForCheckpoint` + `SavePages` via the `TryPersistProtectedPage` hook), `GetOrAllocateDirectoryTwin` +
         `LogicalSegment.CreateOrGrow` (stamp `TwinPageIndex` on root + map-ext pages; occupancy directory twins pre-reserved at
         fixed pages 3/7 — v4 genesis — to break the genesis chicken-and-egg), `ResolveDirectoryPairsForLoad` (read: physical
         both-slots walk before every `Load`, registers `_pairState`), `MapReadOffset` (directory page → `_pairState.CurrentSlot`),
         `DeleteSegment` (free every directory page's twin + the map-ext pages AND clear its `_pairState` entry — else the twin
         leaks and a stale pair mis-routes a cold read after the primary is reallocated). Twin discovery survives a torn primary
         because the `IsLogicalSegment` flag + `TwinPageIndex` are immutable and in the first 4 KiB sector.
  on_violation: a torn write to the only persisted copy → database unopenable / segment unreadable (STO-4); a leaked/stale pair
                on delete → silent mis-route of a reallocated page (STO-4)
  verified: MetaPairTests (meta) + DirectoryPairTests (directory): AlternatesSlots_GenerationMonotonic,
            TornCurrentSlot_ReopenSelectsSibling, BothSlotsCorrupt_OpenFailsLoudly, MultiExtensionSegment_RoundTripsReopen,
            RootGetsTwin_OccupancyMarkedAndAccounted, DeleteSegment_FreesTwinAndClearsPairState (A1.10) + falsification.
            MetaPairStructuralFlushTests — the SOLE-WRITER property: after a full engine lifecycle both slots verify,
            their generations are consecutive, and shutdown writes strictly alternate. That is the property the
            violation below broke, and no earlier test covered it: every one above checks the pair's READ selection or
            its write protocol in isolation, and the bug was a second writer bypassing that protocol entirely.
            v4 format bump refuses v1–v3 files; v6 adds per-sector verification (`PageSectorFooter`), so pair-slot
            validity is geometry-aware via `PagedMMF.VerifyPageImage`.
  note 🔴 VIOLATION FOUND + FIXED 2026-08-09 (#729): `IsExternallyPersisted` excluded the meta pair from
        `CollectDirtyMemPageIndices`, but `SavePages` — the structural ChangeSet flush — is fed by a ChangeSet rather
        than by that scan, so the exclusion never applied there. Its checksum-stamping step is guarded by
        `FilePageIndex > 0`; the WRITE was not. A flush carrying logical page 0 therefore overwrote meta slot 0 with an
        image whose stored checksum no longer matched its content, silently reducing the pair to a single copy on
        ORDINARY SHUTDOWNS — the exact precondition this rule exists to make impossible. `[silent]` because the
        surviving slot still opened the database and every structure was individually well-formed; it would have
        surfaced only as a permanently unopenable database after a second, unrelated tear. Found by the offline
        integrity scanner on its first run against a HEALTHY database — nothing inside the engine could observe it,
        which is the argument for the scanner made by the scanner. Fixed by extending `SavePages`'s existing CK-05
        partition to skip externally-persisted pages entirely. Regression: `MetaPairStructuralFlushTests`.
  note: the durability watermarks (CheckpointLSN + CleanShutdown) are packed in `BK_DurabilityWatermarks` and flip atomically
        with the meta generation — the generation bump is the cycle's atomic commit point (M12). `BK_LastTickFenceLSN`
        consolidation is deferred (fence-as-records, M5).
  note (2026-08-10, #752): the two slots are NOT interchangeable to a reader. Clobbering the CURRENT slot leaves the
        database on the previous metadata write — one generation back, with `CleanShutdown` clear and an older
        `CheckpointLSN` — while clobbering the stale slot leaves the watermarks untouched. Both report the same finding,
        so a test that does not distinguish them measures less than it appears to (`DamageKit.MetaSlot`).
  note (v4, directory-only root): the root page now holds ONLY its page directory (whole `PageRawDataSize` = 2000 entries), so
        the twin protects exactly the immutable directory — never live data — and the root's per-page fsync is genuinely cold
        (create/grow only). The occupancy bitmap's L0 words consequently move off the root onto a dedicated first data page
        (genesis page 4); occupancy bit mutations keep that page dirty-until-checkpoint even with a `null` ChangeSet, so an
        evict→reload can never silently drop a freshly-set bit (which previously double-allocated the page). Segments span ≥ 2
        pages (allocators clamp).

### CK-01: Cycle barrier `[fatal]`
  invariant capture pass begins only after barrierLsn = DurableLsn post-flush is established
  scope: CheckpointManager.cs (barrier :369, barrierLsn sampled :375-377)
  spec: rules/tla/CheckpointProtocol.tla (S1) — CK01_BarrierDurable
  note migrated 2026-07-28; the draft scoped this to SnapshotStore.cs, a rename that never happened

### CK-02: Captured ⊆ durable `[fatal]` `[silent]`
  invariant the WAL is flushed through the high-water of records a captured page copy can reflect `[flush through
            LastAppendedLsn after capture]` strictly precedes `[data-file fsync]`; `CheckpointLSN` advances only to `barrierLsn`
            (the post-flush `DurableLsn` taken before capture), never beyond
  requires: AP-01
  scope: `CheckpointManager.RunCheckpointCycle` — step-1 barrier (`RequestFlush` + `WaitForDurable(LastAppendedLsn)` →
         `barrierLsn`); step-3 flush2 (`RequestFlush` + `WaitForDurable(LastAppendedLsn)`) before each pass's `FlushToDisk`
  on_violation: never-durable bytes persisted in the data file → phantom data after crash
  verified: Barrier_DrivesCheckpointAdvance_IgnoringStaleTarget (CheckpointResilienceTests); the deep crash property
            (captured ⊆ durable across a power cut) is proven by the A1.2 crash sweep (P1.5)
  spec: CheckpointProtocol.tla (S1) — CK02_CapturedSubsetDurable (flush2 before data fsync)

### CK-06: Checkpoint failure classification
  invariant a transient cycle exception (a `TyphonException` with `IsTransient` — WAL/page-cache back-pressure, lock/IO timeout)
            sets `Health = Degraded` and is retried on the next cycle, NEVER latching the subsystem; a non-transient exception
            latches `Health = Fatal` + `HasFatalError` (halting periodic cycles), but the shutdown path STILL attempts one
            last-chance flush cycle
  scope: `CheckpointManager.ClassifyCycleFailure`; the `CheckpointLoop` shutdown guard (`if (!_crashStop)`)
  on_violation: STO-12 — a transient stall permanently and silently disables checkpointing
  verified: TransientCycleFault_NextCycleRecovers, FatalCycleFault_LatchesFatal_ButShutdownStillFlushes (CheckpointResilienceTests; A1.12)

### CK-07: Sealed-segment list synchronization
  invariant all `_sealedSegments` access (writer-thread rotate-Add, checkpoint-thread reclaim-Remove, introspection readers) is
            serialized under one private lock
  scope: `WalSegmentManager._sealedLock` — guards `RotateSegment` Add, `MarkReclaimable`, `SealedSegmentCount`,
         `GetAllSegmentPaths`, reopen-reconcile Add
  on_violation: TXW-7 — `List<T>` corruption under concurrent structural mutation → a fatal latch in a background thread

### CK-08: Flush-only cycles `[UNBUILT]`
  UNBUILT (verified 2026-07-28): no FlushOnlyCycle exists — searching src/ for FlushOnlyCycle or "flush-only" returns
    nothing. Migrated here so the ID is not mistaken for an accidentally deleted rule. Both CommitRecovery.tla and
    CommittedDiscipline.tla justify their CrashDuringRecovery step by citing CK-08; the modelled behaviour is a
    conservative superset of ordinary eviction so the proofs stand, but their stated justification is design-only.
  invariant (intended) a FlushOnlyCycle (capture + write + DecrementDirty) never advances CheckpointLSN, never flips
            the meta pair, never recycles segments; it is the ONLY checkpoint form permitted during recovery
  requires: CK-04, AP-12
  scope: CheckpointManager.cs, RecoveryDriver.cs
  on_violation: recovery either deadlocks on cache backpressure (no valve) or destroys its own re-runnability

### CK-09: Occupancy bitmap is derived — re-derived on crash `[fatal]` `[silent]`
  invariant the occupancy bitmap is a DERIVED structure: on the crash path it is never trusted, but rebuilt WHOLESALE from the
            authoritative page ownership — `owned[p] = 1` iff page `p` belongs to a registered segment's `Pages`, its
            directory-map-extension chain, the reserved root range, the occupancy reserves, or a CK-05 directory twin
            (`DatabaseEngine.BuildOwnedPageBitmap`). The persisted L0 words are overwritten with `owned`
            (`BitmapL3.OverwriteFromDerived`) and the L1/L2 skip summaries recomputed — a full replacement, NOT a read-then-diff,
            so a CRC-torn occupancy page is healed by replacement (the FPI substitute) and any page a torn checkpoint leaked
            (bit set, no claimant) is reclaimed
  timing: runs AFTER the recovery seal checkpoint (`SealRecovery`), because the seal can still grow segments as it flushes
          deferred work (e.g. EntityMap bucket pages) — page ownership is final only afterwards. The corrected bitmap is held
          dirty (DC > 0 ⇒ never evicted stale) and consolidated by the next checkpoint / clean shutdown; a re-crash before that
          simply re-derives (idempotent — `owned` depends only on persisted segment directories)
  scope: `DatabaseEngine.RederiveOccupancyOnCrash` (call site after `SealRecovery`), `BuildOwnedPageBitmap`,
         `ManagedPagedMMF.RederiveOccupancy`, `BitmapL3.OverwriteFromDerived` + `RecomputeSummariesFromL0`. Gated on
         `WalFilesPresentAtOpen`; replaces FPI repair of occupancy pages (kills STO-5 / STO-11 class once FPI is retired in D)
  on_violation: a torn / stale occupancy page survives recovery → a clear bit over a live page double-allocates it (data
                corruption), or a stale set bit leaks the page forever
  verified: TornOccupancyPage_WithFpiDisabled_RecoversViaRederive (FPI off + torn checkpointed occupancy page ⇒
            `RunStorageIntegrityCheck` reports 0 orphans / 0 phantoms; `LastOpenOccupancyRederiveWordsChanged > 0` genuineness)
            [VerifiesRule]

### CK-10: A checkpoint persists the per-archetype segment pointers it consolidates `[fatal]` `[silent]`
  invariant a checkpoint that consolidates a cluster / EntityMap / per-archetype-index segment's DATA pages into the data file MUST
            also record that segment's persistent identity (its `RootPageIndex` — the `ClusterSegmentSPI` / `EntityMapSPI` /
            `ClusterIndexSPI` / `ClusterString64IndexSPI` in the durable `ArchetypeR1` table, plus `NextEntityKey`) in the same
            cycle. The SPIs are stable once allocated, so this is idempotent and skips archetypes whose persisted values are
            unchanged — the skip test MUST cover every SPI it is responsible for, so that no pointer's persistence is conditional
            on an unrelated field having changed. Runs at cycle START (before the durability barrier) so the SPI update's WAL
            records and dirty `ArchetypeR1` pages ride THIS cycle's barrier + page flush — the pointer becomes durable together
            with the data it points at
  rationale: a checkpoint's job is a self-contained durable base. Persisting the data pages but not the pointer leaves the base
             durable-but-UNREACHABLE: on reopen `ArchetypeR1.ClusterSegmentSPI == 0` ⇒ the fresh-allocation path runs ⇒ an empty
             cluster (`ActiveClusterCount == 0`) ⇒ the EntityMap rebuild walks nothing ⇒ every entity is LOST (#395 Face A).
             Previously the SPIs were recorded only at clean shutdown (`PersistArchetypeState` in `Dispose`), so a hard crash
             after a consolidating checkpoint orphaned the cluster.
             The two index SPIs joined this rule in #661. They were outside it because they were not in `ArchetypeR1` at all —
             they lived in the bootstrap dictionary under `clusterindex.{ArchetypeId}`, keyed on a per-process catalog id that
             `ArchetypeR1.ArchetypeId` itself documents as unstable across processes, and written BELOW the skip guard rather
             than being part of it. That survived only because RB-01 permits a lost index to be rebuilt from cluster data — not
             a defence, since that rebuild is itself broken (#656), leaving a torn cluster-index page neither loud-failed nor
             rebuilt but silently served
  scope: `CheckpointManager.RunCheckpointCycle` (the `PersistDurableMetadataHook` invocation), `DatabaseEngine.PersistArchetypeState`
         (skip-unchanged + cache writeback), `DatabaseEngine.DropLegacyClusterIndexBootstrapKeys` (retires the pre-#661 home),
         wired at `DatabaseEngine` checkpoint-manager construction. **Includes the recovery seal** (`DatabaseEngine.SealRecovery`,
         itself a `ForceCheckpoint`): `_archetypeSpiPersistArmed` is set BEFORE it, in `RunWalV2Recovery`. It used to be set only
         at the end of `InitializeArchetypes`, which carved the seal out of this rule on the grounds that doing so "keeps its
         original behaviour" — a mechanical reason where the rule gives a correctness one. Distinct from #395 Face B
         (a plain SV cluster *spawn* value is not WAL-durable per-commit — the Committed discipline's concern, not this rule)
  on_violation: cluster / flat archetype entities consolidated by a checkpoint vanish on the next hard-crash reopen, even though
                their data pages are on disk — silent total data loss for that archetype. For an index SPI the loss is bounded to
                a rebuild, but only for as long as RB-01's rebuild is trustworthy (#656)
  verified: MixedDiscipline_WalWindow_MidCheckpoint_OracleHolds (consolidating checkpoint + hard crash ⇒ the cluster entities
            recover exactly; was a `KnownIssue-395` red before the fix) [VerifiesRule];
            DifferentialRecoveryOracleTests.RecoveredEngine_CrashingWithNoWrite_StillHasEverythingItRecovered [VerifiesRule] — the
            SEAL's half (#715): recover, crash again with not one write in between, and require the consolidated base to still be
            reachable. It writes nothing deliberately, so no post-recovery WAL window exists and LOG-08 cannot be the cause of a
            failure; what is left under test is this rule alone. Pre-fix all three storage shapes reported `scanned=0 applied=0`
            with every entity lost — this rule's `on_violation` sentence, measured;
            ClusterIndexSpiPersistenceTests.Checkpoint_IndexSpiChangedAlone_IsStillPersisted (an index root that changes while
            every other guard field holds is still written — dropping the index SPIs from the skip test fails it) [VerifiesRule]

---

---

## Module: CM — Committed durability discipline

Migrated from `design/Durability/MinimalWal/07-rules.md` on 2026-07-28. ADR-057 cites `CM-01..CM-06` as living in this
file; until now the module did not exist here, so an immutable ADR pointed at nothing. Variant A shipped; Variant B was
rejected and is the TLA+ mutant.

### CM-01: No uncommitted staged bytes in page memory `[fatal]`
  never a Commit-discipline transaction writes staged values into cluster/page memory before its Append
  scope: EntityRef write path, Transaction staging arena (Variant A)
  on_violation: a fuzzy checkpoint persists uncommitted data with no compensating record
  spec: rules/tla/CommittedDiscipline.tla — CM01_HeadCommitted + CM01_NoUncommittedDurable. The BreakStaging mutant is
        Variant B and violates at depth 2 — this is the machine-checked argument behind ADR-057.

### CM-02: Discipline uniform per transaction `[fatal]`
  invariant durability discipline is fixed at tx start; it never varies per-write within one tx
  scope: EntityAccessor.ECS.cs — a mixed-discipline write within one transaction is a hard error
  note the draft scoped this to a "DurabilityOverride extension"; that enum is orphan code with no call site anywhere
       in src/ or test/ (see ADR-005), so the real enforcement point is recorded instead

### CM-03: Fence/commit duplicate is value-safe
  invariant publish MARKS the slot dirty after the HEAD memcpy (the standard fence path); the tick fence may then
            re-emit the already-committed value as a duplicate Slot record at a higher LSN. Recovery is
            last-writer-wins by LSN, so the duplicate is benign — at most one redundant fence record, never
            double-durability semantics
  note publish does NOT clear the bit; it issues the same SetDirty call every TickFence write makes
  scope: Transaction publish (PublishStagedEntry), fence snapshot
  spec: rules/tla/CommittedDiscipline.tla — CM03_RecoveryConverges

### CM-04: ReadsSnapshot rejection
  invariant Build() rejects ReadsSnapshot declarations on SV-layout components
  scope: RuntimeSchedule build validation
  note cross-referenced from rules/runtime-scheduling.md AC-05, which records that the check fires only when the type
       carries [Component]

### CM-06: A Commit-discipline spawn is atomically durable per-commit `[fatal]`
  invariant a SPAWN in a Commit-discipline transaction WAL-logs each SingleVersion component's spawn VALUE (a Slot
            upsert keyed by EntityId.RawValue), not just the Spawn lifecycle record. Recovery aggregates Spawn + Slots
            by entity and applies them together, so the entity recovers WITH its values across a hard crash and no
            checkpoint
  rationale: without this the spawn lifecycle is logged but the SV value is not — it rides the cluster SoA only, which
             is checkpoint-durable — so a window spawn plus hard crash recovers the entity alive-but-DEFAULT: a torn,
             half-recovered entity. This is the SV counterpart of what Versioned spawns get for free.
  scope: Transaction.BuildCommitBatch (spawn-value emission, gated on _discipline == Commit); recovery reuse is
         AP-12 + RecoveryApplier.ApplySpawnedEntity. A TickFence spawn is unchanged — checkpoint-durable only.
  on_violation: a Commit-discipline-spawned entity recovers alive-but-default → silent value loss masquerading as a
                successful recovery
  verified: ClusterAllSv_PrimaryAxis_SurvivesCrash [VerifiesRule]

## Module: WAL Pipeline

The temporal chain that all durability depends on:

```
[page modified in memory] → [WAL record in ring buffer]          (the leading [FPI written] step is removed — FPI retired, increment D)
  → [WAL FUA to disk] → [revision visible] → [page flushed to data file]
```

Any short-circuit — skipping a step, reversing two adjacent steps — breaks
either durability, recoverability, or consistency.

### WP-01: LSN watermark ordering `[fatal]`
  invariant CheckpointLSN ≤ DurableLSN ≤ CurrentLSN
  scope: WalWriter.cs, CheckpointManager.cs, WalSegmentManager.cs
  on_violation:
    CheckpointLSN > DurableLSN → checkpoint flushes pages whose WAL is not yet on disk;
      crash loses data with no recovery path
    DurableLSN > CurrentLSN → logically impossible (signals writes not yet issued)

### WP-02: WAL-before-visibility `[fatal][silent]`
  invariant ∀rev: rev.Visible → rev.WalRecord.LSN ≤ DurableLSN
            (for DurabilityMode.Immediate)
  invariant ∀rev: rev.Visible → rev.WalRecord ∈ RingBuffer
            (for DurabilityMode.Deferred/GroupCommit, weaker guarantee)
  scope: Transaction.Commit, WalCommitBuffer.cs, WalWriter.cs
  on_violation: reader sees a revision with no WAL backing;
    crash permanently loses committed data the caller was told exists

### WP-03: FUA before DurableLSN advance `[fatal]`
  pre IWalFileIO.WriteAligned() has returned (FUA complete)
  post Interlocked.Exchange(ref _durableLSN, newValue)
  post signal _durabilityEvent (wakes Immediate waiters)
  scope: WalWriter.cs
  on_violation: Immediate-mode callers wake before write reaches stable media;
    crash loses data despite Immediate guarantee

### WP-04: Single-consumer WAL writer `[fatal][silent]`
  invariant ∃! thread consuming WalCommitBuffer (the WAL writer thread)
  scope: WalWriter.cs, WalCommitBuffer.cs
  on_violation: concurrent consumers interleave records out of LSN order;
    CRC chain breaks; recovery truncates valid records or replays garbage

### WP-05: CRC chain patched by writer only `[fatal]`
  invariant ∀producer: chunk.PrevCRC == 0 ∧ chunk.CRC == 0 (placeholders)
  invariant WalWriter.PatchChunkCrcs() is the sole writer of CRC values
  pre PatchChunkCrcs() runs after staging, before disk write
  scope: WalWriter.cs, WalChunkHeader, WalChunkFooter
  on_violation: broken CRC chain → recovery truncates at first mismatch,
    discarding valid subsequent records; or accepts corrupt chunk as valid

### WP-06: Buffer claim ordering = LSN ordering `[fatal][silent]`
  invariant LSN assignment order == buffer position order
  invariant position and LSN are allocated by ONE atomic operation — never two, in either order
  invariant WalWriter flushes in LSN order, stops at first uncommitted claim
  scope: WalCommitBuffer.TryClaim, WalWriter.cs
  on_violation: the DURABLE WATERMARK becomes dishonest, which is the whole of the damage:
    TryDrain walks frames in POSITION order and stops at the first unpublished one, so position order IS
    durability order and DurableLsn is only a valid proxy for it while the two agree. Diverged, a
    position-earlier frame carrying a HIGHER LSN lets the drain advance DurableLsn past a frame whose bytes
    were never written. Then:
      - WP-02: a DurabilityMode.Immediate commit waiting on the lower LSN returns success with its record
        still in volatile memory — a crash loses an acknowledged commit, silently.
      - CK-02: CheckpointManager uses DurableLsn as targetLsn, so an overstated watermark can trim WAL that
        is not on stable media.
  note corrected 2026-07-31 (#581). This rule previously said the damage was "out-of-order LSN in WAL file →
    recovery replays B before A". That is FALSE and was masking the real consequence: RecoveryDriver.Run sorts
    records by LSN before applying (03-recovery.md §3, 07-rules.md), so replay order is safe regardless. Fixing
    the stated consequence matters because it is what tells a reader the watermark — not the file — is at risk.
  note monotonic AdvanceDurable is defence-in-depth, NOT a fix. It stops the watermark going backwards; it
    cannot make an overstated watermark honest.
  note the claim path and the buffer swap are ONE protocol, not two. Allocating position and LSN atomically is
    insufficient on its own: the generation's LSN base is folded at the swap, so a producer that claims across
    a swap would read a stale base and duplicate an LSN. WalCommitBuffer gates claims (_claimsInProgress vs
    SwapDraining, a store-load pair with full fences on both sides) so the swap point is genuinely quiescent.
    The gate check must precede the position claim — backing out after claiming would leave an unpublished
    frame header, and TryDrain stops at the first of those forever.

### WP-13: Back-pressure wake-up keys on a MONOTONIC generation, never on the buffer index `[correctness]`
  context a producer whose claim overflows the active buffer parks in WalCommitBuffer.TryClaim until the buffer is
    swapped. The wake-up condition is the whole of this rule: what the producer waits ON.
  invariant the parked producer's wait condition is `_swapGeneration == <value sampled under the claim gate>`,
    where `_swapGeneration` is a 64-bit counter incremented exactly once per completed swap
  never wait on `_activeBufferIndex` (or any other value that can return to a previously-observed state)
  invariant `_swapGeneration` is sampled while the claim gate is HELD — the Dekker pair with SwapDraining (WP-06)
    is what guarantees no swap can complete between the sample and the park, so the sampled value is necessarily
    the generation the claim belongs to
  invariant PerformSwap increments `_swapGeneration` LAST — after the new index, the reset claim word and the
    folded `_lsnBase` are published — so observing a new generation implies observing everything it depends on
  invariant a producer that backed out at the claim gate (claimed NOTHING) does not wait on the generation at all;
    it waits only while `_swapState == SwapDraining`, then retries
    (it is owed no swap, and any generation it could sample is already racing the in-progress swap)
  scope: WalCommitBuffer.TryClaim (back-pressure loop), WalCommitBuffer.WaitingForSwap, WalCommitBuffer.PerformSwap
  on_violation: ABA. `_activeBufferIndex` ping-pongs 0↔1, so a producer descheduled across TWO swaps finds it back
    at the captured value and keeps waiting for an edge that has already passed twice. Nothing recovers it — only
    another producer overflowing the buffer advances the index — so once every live producer is parked this way the
    buffer sits DRAINED AND IDLE (swapState=Normal, inflight=0, space available) until each WaitContext deadline
    expires and throws WalBackPressureTimeoutException on a commit that could have proceeded the whole time.
    Not silent, but arbitrarily delayed: the damage is a spurious commit failure plus a full-deadline stall.
  note found by the macOS arm64 nightly (3 cores), NOT by the 16-core x64 gate, and it is NOT arm64-specific —
    it reproduces on x64/Windows under a 2-CPU affinity mask. Core count is the variable, not architecture: at
    1 CPU strict serialisation keeps another producer runnable to trigger the next swap, and at ≥3 a producer is
    rarely off-CPU across two whole swaps. The gate's hardware is exactly what hid it.
  note the loop's bounded `_swapCompletedEvent.Wait(2ms)` backstop does NOT bound this. A backstop caps a lost
    WAKE-UP; a missed generation is never re-observable however often the condition is re-checked.
  verified: WalCommitBufferConcurrencyTests.BackPressureWait_ProducerLappedByTwoSwaps_IsNotStillWaiting
  requires: WP-06

### WP-07: LSN at body offset 0 for all chunk types `[fatal][silent]`
  invariant ∀chunkType: chunk.Body[0..8] == chunk.LSN
  scope: Durability/internals/RecordFormat.cs (RecordHeader), WalSegmentReader.cs
  note the v1 `WalRecordHeader` survives only in WalRecordHeader.cs + WalRecovery.cs; the live type is RecordFormat.RecordHeader
  on_violation: generic LSN extraction reads wrong bytes;
    recovery misidentifies truncation point, selects wrong FPI (old over new)

### WP-08: Ring buffer is volatile `[silent]`
  invariant DurabilityMode.Deferred ∧ ¬Flushed → data lost on crash
  never report a ring-buffer-only record as durable (the `DurabilityGuarantee` enum this rule named no longer exists in src/;
        the live types are DurabilityMode + the transaction's durable-LSN wait)
  scope: WalCommitBuffer.cs, UnitOfWork.cs
  on_violation: callers trust data survived crash when it did not;
    silent data loss with no detection mechanism

### WP-09: Segment recycling gated on CheckpointLSN `[fatal]`
  invariant ∀segment: segment.LastLSN ≤ CheckpointLSN → recyclable
  invariant ∀segment: segment.LastLSN > CheckpointLSN → must retain
  note the implementation is strictly more conservative than the rule permits: `WalSegmentManager.cs:321` recycles on
    `lastLSN < checkpointLSN`, so a segment whose LastLSN equals CheckpointLSN is retained for one extra cycle. Safe
    direction (never recycles a needed segment); the divergence is deliberate-by-effect, not a violation. Do not "fix"
    the code to `≤` without a reason — the retained segment costs one segment of disk and removes a boundary case.
  scope: WalSegmentManager.cs, CheckpointManager.cs
  on_violation: premature recycling → WAL records needed for crash recovery
    are gone; data loss without detection

### WP-10: O_DIRECT alignment `[fatal]`
  invariant WriteOffset % 4096 == 0
  invariant WriteSize % 4096 == 0
  invariant BufferAddress aligned via NativeMemory.AlignedAlloc(size, 4096)
  scope: WalFileIO.cs, IWalFileIO.cs
  on_violation: OS rejects write (ERROR_INVALID_PARAMETER on Windows);
    WAL data lost, committed transactions silently non-durable

### WP-11: CRC32C polynomial (Castagnoli), not CRC-32 IEEE `[fatal]`
  invariant polynomial == 0x1EDC6F41 (bit-reversed: 0x82F63B78)
  never use System.IO.Hashing.Crc32 (wrong polynomial)
  scope: Foundation/Hashing/internals/Crc32CUtil.cs, PagedMMF.cs
  on_violation: all pages and WAL records appear corrupt after polynomial mismatch;
    every CRC check fails on startup

### WP-12: Segment headers written once, never modified `[fatal]`
  invariant WalSegmentHeader is immutable after creation
  scope: WalSegmentManager.cs
  on_violation: overwriting header corrupts start of WAL stream;
    CRC chain broken at segment start

### WP-15: Drain is all-or-nothing with respect to the watermark `[fatal]`
  invariant every byte of a drained batch is issued to IWalFileIO.WriteAligned BEFORE CompleteDrain discards the batch
  invariant DurableLsn advances only over batches for which that write was issued
  never CompleteDrain / AdvanceDurable on a path that did not attempt the write (e.g. a size guard with no chunked
        fallback)
  scope: WalWriter.WriterLoop, WalWriter.DrainRemaining, WalWriter.DrainAndWriteSync — i.e. every PatchChunkCrcs caller
  on_violation: committed records are discarded from the ring buffer while the watermark advances past them; nothing
    downstream can detect the gap because recovery starts after the watermark
  rationale: WP-03 states the ORDERING (write before advance) and LOG-05 bounds what the watermark may CLAIM, but
    neither requires the write to have been attempted. Issue #580 falls through exactly that gap — DrainRemaining lacks
    the WriteInChunks fallback its two sibling drain paths have, so an oversized final batch is dropped on a graceful
    shutdown and the watermark advances anyway. Naming every PatchChunkCrcs caller in scope is what makes the
    asymmetry visible on inspection.

### WP-14: CRC patched over the WHOLE drained batch, never per write-slice `[fatal][silent]`
  context a drained batch can exceed the staging buffer (commit-buffer half = 2 MB, staging = 256 KB default),
    so WalWriter.WriteInChunks streams it to disk in staging-sized writes. A record-batch chunk (≤ ~64 KB) or an
    FPI chunk routinely STRADDLES a staging-sized write boundary — its header is in one slice, its footer in the next.
  invariant WalWriter.PatchChunkCrcs() runs over the ENTIRE drained batch in ONE pass before ANY byte of it reaches
    stable media; the writer NEVER patches CRCs per write-slice
    (per-slice patching breaks at the straddling chunk — PatchChunkCrcs sees frameEnd > sliceLength and stops,
     leaving that chunk's footer CRC at its 0 placeholder, AND the PrevCRC chain de-syncs for every later chunk)
  invariant streamed writes preserve the single-write byte layout: intermediate slices are a whole staging buffer
    (a 4096 multiple) so the batch lands CONTIGUOUS on disk, with zero-padding only after the FINAL slice
    (a chunk's bytes are never split across a padding gap — the reader requires a frame's chunks contiguous to frameEnd)
  scope: WalWriter.WriteInChunks, WalWriter.DrainAndWriteSync, WalWriter.PatchChunkCrcs
  on_violation: the straddling chunk keeps footer CRC == 0 → recovery computes a non-zero CRC, reads the mismatch as a
    torn tail (REC truncation), and stops at that chunk — silently discarding every record after it. Triggered by any
    transaction or page-flush large enough to overflow the staging buffer (e.g. a large commit, or an FPI flood from
    dirtying many pages in one tick) → silent crash-recovery data loss. The writer dual of WR-02 (reader-side padding
    traversal) and a tightening of WP-05 (CRC is the writer's job — and it must cover the whole batch atomically).
  note: invisible to hand-written crash tests that never exceed 256 KB; surfaced by the T-5 differential oracle at scale (#395).

---

## Module: Checkpoint

The 8-step checkpoint pipeline. Step ordering is load-bearing.

```
[1: Capture DurableLSN] → [2: Collect dirty pages]                       (Step 0 "Reset FPI bitmap" removed — FPI retired, increment D)
  → [3: Write pages to data file] → [4: fsync data file]
  → [5: Decrement DirtyCounter] → [6: UoW WalDurable→Committed]
  → [7: Advance CheckpointLSN + fsync header] → [8: Recycle WAL segments]
```

### CP-03: DirtyCounter decremented only after fsync `[fatal]`
  invariant ∀path that calls DecrementDirty: data file fsync must complete first
  impl checkpoint: Step 4 (fsync) before Step 5 (DecrementDirty)
  impl SavePages: FlushToDisk() in ContinueWith before DecrementDirty loop
  scope: CheckpointManager.cs, PagedMMF.SavePages, PagedMMF.cs
  on_violation: page marked clean before durably on disk;
    eviction discards modifications; crash loses data
  requires: PS-01

### CP-04: Re-dirty safety via DirtyCounter > 1 `[fatal][silent]`
  invariant page re-dirtied during Step 3 has DirtyCounter > 1
  post after Step 5 decrement: DirtyCounter ≥ 1 (stays dirty for next cycle)
  impl: ChunkAccessor.MarkSlotDirty uses IncrementDirty (not EnsureDirtyAtLeast)
    on re-registration — always +1, so DC survives pending checkpoint decrement.
    EnsureDirtyAtLeast(1) is wrong: no-op when DC=1, checkpoint decrements to 0.
    EnsureDirtyAtLeast(2) is wrong: livelock — checkpoint 2→1, re-dirty bumps 1→2, never 0.
    ReleaseExcessDirtyMarks SUBTRACTS this ChangeSet's excess marks (N → 1 of its own) on UoW dispose, preventing
    inflation. 🔴 CORRECTED 2026-07-27: it does NOT clamp DC — a clamp races the pending checkpoint decrement (#385).
    ChunkAccessor.MarkSlotDirty now routes through ChangeSet.RegisterReDirty, not a direct IncrementDirty.
  never DecrementDirtyToMin on the UoW-dispose path
  scope: CheckpointManager.cs, ChunkAccessor.MarkSlotDirty,
         ChangeSet.RegisterReDirty / ReleaseExcessDirtyMarks / Reset, PagedMMF.DecrementDirtyByDelta
  on_violation: concurrent modification lost — page appears clean,
    eviction discards the re-dirty, data silently gone

### CP-05: CheckpointLSN advanced only after data fsync AND full coverage (Step 7 after Step 4) `[fatal]`
  pre data file fsync complete for every page written this cycle (Step 4)
  pre COVERAGE GATE: zero pages remain skipped after the retry passes — a page collected at the barrier that could not
      be captured BLOCKS the advance (CheckpointManager gates Steps 6/7/8 on `stillSkipped == 0`, after at most
      MaxCoveragePasses retry passes)
  pre UoW transitions complete (Step 6)
  post flip the meta pair carrying the CheckpointLSN watermark — the generation bump is the atomic, fsynced commit
       point (Step 7). There is no "file header" for this value; it lives in the bootstrap meta-pair (see CK-05).
  never advance CheckpointLSN while any collected page is uncaptured — an uncaptured page's committed records may not
        have reached the data file, and Step 8 would recycle their segment
  scope: CheckpointManager.RunCheckpointCycle (the coverage gate), DurabilityWatermarks.UpdateCheckpointLsn
  spec: rules/tla/CheckpointProtocol.tla — CoverageGate / NoLostPage. The -mutant.cfg (BreakCoverageGate=TRUE) is
        precisely this rule with the coverage precondition removed, and TLC proves it violates NoLostPage.
  on_violation: WAL segments recycled for data not yet on disk; crash → permanent data loss, no WAL to replay
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously listed only the fsync and UoW-transition preconditions and
    omitted the coverage gate entirely. Together with CP-11 (skip pages with active chunk writers) it therefore
    specified the model-checked MUTANT: advance after fsync with skipped pages outstanding. Implementing from the rule
    text alone reproduced the data-loss bug the spec exists to exclude.

### CP-06: WAL recycling after CheckpointLSN durably on disk (Step 8 after Step 7) `[fatal]`
  pre file header fsync complete (Step 7)
  post MarkReclaimable(checkpointLsn) (Step 8)
  scope: CheckpointManager.cs, WalSegmentManager.cs
  on_violation: crash resets CheckpointLSN to old value;
    engine tries to read recycled (overwritten) segments → recovery fails

### CP-07: CRC computed consistently for each write path `[fatal][silent]`
  invariant checkpoint: CRC = Crc32CUtil.ComputeSkipping(stagingBuffer, PageChecksumOffset, PageChecksumSize) — never live page
    (concurrent writers exist; staging prevents torn CRC)
  invariant SavePages: CRC = Crc32CUtil.ComputeSkipping(livePage, PageChecksumOffset, PageChecksumSize) after ChangeRevision increment
    (safe: called during structural operations with no concurrent page writers)
  scope: CheckpointManager.cs, PagedMMF.SavePages, StagingBufferPool
  on_violation: CRC computed over torn intermediate state;
    stored CRC doesn't match any valid page state;
    appears corrupt on every future load, falsely triggers FPI repair

### CP-08: ChangeRevision incremented on staging copy, not live page `[fatal]`
  invariant staging.ChangeRevision = live.ChangeRevision + 1
  never modify live page's ChangeRevision during checkpoint
  scope: CheckpointManager.cs
  on_violation: modifying live page outside seqlock protocol creates race;
    live CRC diverges from on-disk version

### CP-09: Data file fsync ownership `[fatal]`
  invariant WalWriter owns WAL file FUA
  invariant CheckpointManager is the ONLY fsync owner that gates the LSN watermarks (CheckpointLSN advance + WAL
            segment recycling); every other data-file fsync is watermark-neutral and must never advance CheckpointLSN
            nor call MarkReclaimable
  invariant the watermark-neutral fsync owners are exhaustive:
            (a) PagedMMF.SavePages — the structural-write path (bootstrap / schema / segment-grow / v1 replay), which
                fsyncs in its post-write continuation before DecrementDirty (CP-03)
            (b) ManagedPagedMMF.PersistMetaNow / PersistProtectedPage — the CK-05 pre-flip fsync, MANDATORY, reached
                from both the checkpoint thread and runtime SaveChanges
  never a direct MMF.FlushToDisk() outside (a)/(b), other than a redundant flush immediately after ChangeSet.SaveChanges
        (which has already fsynced)
  requires: CK-05
  scope: CheckpointManager.cs (the watermark-gating fsync), WalWriter.cs (WAL FUA), PagedMMF.SavePages +
         PagedMMF.FlushToDisk, ManagedPagedMMF.PersistMetaNow + PersistProtectedPage; redundant post-SaveChanges
         flushes in DatabaseEngine (bootstrap / PersistEngineState / component registration ×2), SchemaEvolutionEngine
         (×2) and the v1 WalRecovery replay (×2)
  note corrected 2026-07-28 — the old text named only SavePages as a second owner. CK-05 introduced a third
       (PersistMetaNow / PersistProtectedPage) and this rule was never updated; nine direct FlushToDisk callers exist,
       all watermark-neutral.
  on_violation: fsync ordering relative to CheckpointLSN advance;
    segments recycled based on inconsistent checkpoint state

### CP-10: Staging buffers always returned to pool `[perf→fatal]`
  invariant ∀staging ∈ rented: eventually returned (RAII via ref struct wrapper)
  scope: StagingBufferPool, CheckpointManager.cs
  on_violation: pool starves → checkpoint stalls indefinitely;
    WAL grows without bound → disk fills → all commits fail

### CP-11: ActiveChunkWriters gate on checkpoint snapshot `[fatal][silent]`
  pre CAS(page.ACW, -1, 0) succeeds (ACW was 0)
  invariant page.ACW > 0 → skip page this checkpoint cycle (writer in flight)
  invariant page.ACW = -1 sentinel blocks new writers (they spin in IncrementActiveChunkWriters)
  post after CopyPageWithSeqlock: Exchange(page.ACW, 0) releases sentinel
  requires: CK-03 — skipping an active-writer page is only SAFE because the coverage gate holds the watermark
            until that page is captured. Remove the gate and this rule becomes a data-loss mechanism.
  scope: PagedMMF.WritePagesForCheckpoint, PagedMMF.CopyPageWithSeqlock (the retry), 
         PagedMMF.IncrementActiveChunkWriters, ChunkAccessor.cs
  note the CK-05 protected-page redirect executes INSIDE the ACW=-1 window
  on_violation: checkpoint snapshots page with OLC write lock held;
    torn B+Tree node persisted; structural corruption unrecoverable via WAL
    (WAL replays component-level changes, not raw page bytes)

### CP-12: Deferred ACW decrements `[fatal][silent]`
  invariant when dirty slot evicted from ChunkAccessor 16-slot cache,
    ACW decrement deferred until CommitChanges() or Dispose()
  scope: ChunkAccessor.CommitChanges; ChangeSet.DeferEviction / FlushDeferredEvictions (the deferral queue
         is owned by ChangeSet, not ChunkAccessor — corrected 2026-07-28)
  on_violation: early decrement allows checkpoint to snapshot page
    while caller still holds OLC write lock on evicted slot's node;
    same corruption as CP-11

---

## Module: Seqlock

> **ID prefix `SL-` (renamed from `SQ-`, 2026-07-28).** `SQ-` collided with the spatial
> Queries module, which defines a different `SQ-01..SQ-05`. Rule IDs must be unique across the whole database —
> any tool indexing by ID (including the coverage gate) conflated them.

### SL-01: ModificationCounter parity `[fatal]`
  invariant page.ModCounter % 2 == 0  ↔  ¬page.ExclusiveLatchHeld
  scope: PagedMMF.TryLatchPageExclusive, PagedMMF.UnlatchPageExclusive
  on_violation:
    stuck odd → checkpoint spins forever on that page, stalling all checkpoints
    even while writing → checkpoint snapshots torn page with valid-looking counter

### SL-02: Pre-modification increment before any page write `[fatal][silent]`
  pre ++ModCounter (even → odd) inside TryLatchPageExclusive
  post caller modifies page data
  scope: PagedMMF.TryLatchPageExclusive
  on_violation: checkpoint snapshots page mid-write;
    torn data gets valid CRC on staging buffer → silent corruption on disk

### SL-03: Post-modification increment after all writes complete `[fatal]`
  pre all page data writes complete
  post ++ModCounter (odd → even) inside UnlatchPageExclusive
  scope: PagedMMF.UnlatchPageExclusive
  on_violation: counter stays odd → checkpoint spins indefinitely

### SL-04: No increment on re-entrant latch `[fatal][silent]`
  invariant ExclusiveLatchDepth++ does NOT increment ModCounter
  invariant counter stays odd for all nested acquisitions
  invariant final increment only on outermost release (depth → 0)
  scope: PagedMMF.TryLatchPageExclusive, PagedMMF.UnlatchPageExclusive
  on_violation: counter goes even mid-write (between nested operations);
    checkpoint grabs inconsistent intermediate snapshot

### SL-05: Seqlock counter only under exclusive latch `[fatal]`
  never increment ModCounter under shared latch or without any latch
  scope: PagedMMF.cs
  on_violation: multiple writers race on counter;
    counter can appear even while page is actually mid-modification

### SL-06: Checkpoint seqlock read protocol `[fatal]`
  pre v1 = read(ModCounter)
  pre v1 is odd → skip (retry later)
  post memcpy page to staging buffer
  post v2 = read(ModCounter)
  post v1 ≠ v2 → discard copy, retry
  post v1 == v2 ∧ v1 even → copy is consistent
  scope: PagedMMF.CopyPageWithSeqlock, CheckpointManager.cs
  on_violation: skipping re-read → torn snapshot accepted as consistent;
    CRC matches torn data → silent corruption stored to disk

### SL-07: Barrier placement is part of the protocol `[fatal][silent]`
  invariant writer open:  ++ModCounter  →  StoreStore  →  page data writes
  invariant writer close: page data writes  →  StoreStore  →  ++ModCounter
  invariant reader: EVERY protocol load is Volatile.Read, AND a barrier separates the copy from the validating re-read
  never a plain `++ModCounter`, and never a plain load of ModCounter
  scope: PagedMMF.TryLatchPageExclusive, PagedMMF.UnlatchPageExclusive, PagedMMF.CopyPageWithSeqlock
  on_violation: SL-02/SL-03/SL-06 are satisfied in program order yet violated in execution order.
    The reader case is worst: if the memcpy's loads sink below the validating re-read, the check compares a
    counter read that predates the data it is validating — the protocol degenerates to "read, copy, hope" and
    the validation CANNOT fail. The torn copy is then CRC-stamped over the torn bytes and written, so ADR-015
    page-checksum validation passes on reload and every downstream integrity check is defeated. RB-04 does not
    catch it either — RB-04 detects a torn page only by CRC MISMATCH, and this page's CRC is valid over the torn
    bytes. Since FPI was deleted there is no backstop behind it.
  note added 2026-07-31 (#579). Ordering was previously left entirely to the hardware — `grep -c "Volatile\."
    PagedMMF.cs` returned 0 — which is accidentally correct under x64 TSO and wrong under arm64. The rules
    SL-02/SL-03/SL-06 were already right; only the code was wrong, so they were NOT weakened to match.
  note the two writer sites need DIFFERENT primitives despite looking symmetric — do not "unify" them:
    open  (counter -> odd, THEN data): needs later stores held back, i.e. StoreStore-AFTER. No release store
          gives that direction, so this site needs a full fence: Interlocked.Increment.
    close (data, THEN counter -> even): needs prior stores flushed first, which IS release semantics, so
          Volatile.Write is exactly the requirement and is free on x64 (plain mov) / stlr on arm64.
  note neither writer site uses Interlocked for ATOMICITY — SL-05 already guarantees a single writer, so there
    is no RMW race. It is a fence. Reviewers reading Interlocked as "contended counter" will misjudge the code.
  note a plain store plus `if (!X86Base.IsSupported) MemoryBarrier()` is NOT sufficient on the WRITER side.
    That guards the hardware model only; the .NET memory model independently permits the JIT to reorder
    ordinary writes ("the effects of ordinary reads and writes can be reordered as long as that preserves
    single-thread consistency"), and the guard folds to nothing on x64.
    The reader may use the arch-conditional form because its protocol loads are Volatile.Read, which constrains
    the JIT; the conditional barrier only supplies the hardware LoadLoad that acquire semantics do not give.
  reference implementations: OlcLatch.ValidateVersion (arch-conditional barrier),
    RevisionChainReader.TryWalkSingleEntryOptimistic (all-Volatile variant)
  testing x64 CI cannot detect a violation of this rule. On the reader side acquire loads ARE plain mov and the
    arch-conditional barrier folds to nothing, so conforming and violating code emit identical machine code
    there. On the writer side the code differs (lock inc vs inc) but x64 TSO supplies the missing ordering
    anyway, so the violating version still passes. A green x64 suite is not evidence either way, and the
    functional suite is no better on arm64 — the failure mode is a torn page carrying a VALID CRC, so nothing
    asserts on it. Verification is conformance review against the reference implementations above, plus an
    arm64 run. See #624 for the CI leg that would automate the latter.
    A reduced litmus harness (protocol isolated from the engine, so the reorder window is most of the loop
    rather than a few instructions buried under I/O) is the only thing that turns a silent reorder into an
    observable signal. Such a harness MUST self-test by stripping the protocol entirely and confirming tearing
    IS detected — otherwise a clean run cannot be told apart from a harness that never overlapped its threads.
  measured 2026-07-31, both barriers vs unfenced, on the shipping primitives:
    x64 (7950X, 32 logical): ~0% on both sides. Single runs ranged to 16% and went NEGATIVE — noise.
    arm64 (Apple M-series, 10 logical): writer ~4%, reader ~4% in the isolated harness; consistently positive
      across 5 runs, so real. Expect well under 1% in the engine, where each page copy also carries a 4 KB
      memcpy, a CRC32C pass and a write syscall.
    No tearing was reproduced on Apple Silicon in 290M validated copies (1.28M of which entered the retry
    window), which says that chip does not exercise the reordering — NOT that the barriers are unnecessary.
    Neoverse N1/V1/V2 are the reference weak-memory implementations and are the target that matters.

---

## Module: UoW Registry

### UR-01: State transitions are strictly forward `[fatal]`
  invariant Free → Pending → WalDurable → Committed → Free
  invariant Pending → Void → Free (crash recovery only)
  never reverse a transition (e.g., Committed → WalDurable)
  scope: UowRegistry.cs
  on_violation: backward transition confuses WAL segment recycling and recovery;
    duplicate-apply corruption or phantom data

### UR-02: WAL record durable before revision references UoW ID `[fatal][silent]`
  invariant "UoW Created" WAL record must exist before any revision
    is stamped with that UoW ID
  scope: UowRegistry.cs, WalWriter.cs, Transaction.Commit
  on_violation: crash → recovery has no knowledge of UoW;
    ghost revisions undetectable; registry slot reused → ABA corruption

### UR-03: Pending → Void is crash-recovery exclusive `[fatal]`
  never set UoW status to Void during normal operation
  scope: UowRegistry.cs, WalRecovery
  on_violation: voiding a live UoW makes all its revisions instantly invisible;
    committed in-memory state corrupted

### UR-04: UoW ID recycling gated on MinTSN `[fatal][silent]`
  invariant Committed → Free only when MinTSN > entry.MaxTSN
  invariant Void → Free only when MinTSN > entry.MaxTSN
  🔵 UNBUILT (verified 2026-07-28): Release has no MinTSN gate, and the field the gate would read is dead —
     entry.MaxTSN is written only as the literal 0 by the sole production caller, and is read by nothing. Implementing
     this rule requires populating MaxTSN first. Marked rather than deleted: the ABA is latent, not absent — the
     committed-bitmap path is consulted while CommittedBeforeTSN == 0 (the post-crash window), where a reader whose
     snapshot spans a revision stamped uowId=X can observe X released, reallocated and its committed bit cleared.
  scope: UowRegistry.cs, TransactionChain.cs
  on_violation: active reader's snapshot includes a revision whose UoW ID is now reassigned to a new UoW → ABA;
    incorrect visibility

### UR-05: CommittedBeforeTSN = 0 after crash recovery `[fatal][silent]`
  pre crash recovery completes, Void entries exist
  post CommittedBeforeTSN = 0
  invariant forces all reads through committed bitmap (Layer 4)
  scope: UowRegistry.cs, IsVisible read path
  on_violation: CommittedBeforeTSN = long.MaxValue → Layer 2 always passes;
    ghost revisions from voided UoWs visible as committed data;
    permanent silent data corruption

### UR-06: CommittedBeforeTSN restored to MaxValue only after all Void entries GC'd `[fatal][silent]`
  pre ∀entry ∈ Registry: entry.Status ≠ Void
  pre all ghost revisions cleaned up by DeferredCleanupManager
  post CommittedBeforeTSN = long.MaxValue
  scope: UowRegistry.cs, DeferredCleanupManager
  on_violation: premature restoration → ghost revisions bypass bitmap;
    same corruption as UR-05

### UR-07: UoW ID 0 is reserved sentinel `[fatal][silent]`
  never assign UoW ID 0 to a new UnitOfWork
  invariant UowId == 0 → legacy "always committed" (Layer 3 of IsVisible)
  scope: UowRegistry.AllocateUowId
  on_violation: after crash, voided UoW 0's ghost revisions bypass both
    CommittedBeforeTSN check and bitmap (legacy path hit first);
    ghost data permanently visible

### UR-08: IsolationFlag cleared exactly at commit `[fatal][silent]`
  pre Transaction.Commit() conflict detection passed
  post IsolationFlag = 0 (atomically with commit)
  never clear IsolationFlag before commit (dirty read)
  never leave IsolationFlag = 1 after commit (invisible committed data)
  scope: Transaction.Commit, CompRevStorageElement
  on_violation:
    too early → other readers see uncommitted write (isolation violation)
    too late → committed revision invisible; crash in window → committed-but-invisible
      revision on disk (undetectable by standard checks)

---

## Module: Page Safety

### PS-01: Eviction predicate `[fatal]`
  invariant page evictable ↔ (DirtyCounter == 0 ∧ ActiveChunkWriters == 0
    ∧ SlotRefCount == 0 ∧ AccessEpoch < MinActiveEpoch)
  never evict page with DirtyCounter > 0 (uncommitted/unflushed data)
  never evict page with ActiveChunkWriters > 0 (OLC write in progress)
  never evict page with SlotRefCount > 0 (live ChunkAccessor slot reference)
  never evict page with AccessEpoch ≥ MinActiveEpoch (epoch-protected)
  scope: PagedMMF.TryAcquire (clock-sweep), EpochManager.cs
  on_violation:
    dirty eviction → committed data lost, unrecoverable
    ACW eviction → checkpoint snapshots torn page
    SlotRefCount eviction → dangling pointer in ChunkAccessor slot
    epoch-protected eviction → use-after-free; transaction reads from memory
      now serving a different file page; arbitrary corruption

### PS-02: All page access within EpochGuard `[fatal]`
  invariant ∀page access (read or write): enclosed in EpochGuard scope
  scope: EpochGuard.cs, PagedMMF.cs
  on_violation: page's AccessEpoch not stamped;
    page evictable immediately → dangling pointer; use-after-free

### PS-03: AccessEpoch uses CAS-max, never decreases `[correctness]`
  invariant AccessEpoch = max(existing, globalEpoch) via CAS
  invariant AccessEpoch only reset to 0 inside UnlatchPageExclusive
  scope: PagedMMF.RequestPageEpoch, PagedMMF.UnlatchPageExclusive
  on_violation: decreasing epoch makes page appear stale;
    eviction while transaction still holds pointer

### PS-04: DirtyCounter mutations are atomic read-modify-write `[fatal]`
  invariant every DirtyCounter mutation uses an Interlocked RMW (Increment / Decrement / CAS-retry loop)
  never read-then-write DirtyCounter non-atomically
  scope: PagedMMF.IncrementDirty / DecrementDirty / DecrementDirtyByDelta / DecrementDirtyToMin
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously required the per-page StateSyncRoot lock. The implementation
    moved to lock-free atomics, which is STRICTLY STRONGER for the failure mode the rule names (negative DC) and is not
    taken under StateSyncRoot on any path — so the rule as written described neither the code nor the better guarantee.
  on_violation: racing increment/decrement → negative DC;
    clock-sweep interprets as clean → premature eviction of dirty page

### PS-05: UoW dispose releases its OWN excess dirty marks — it never clamps DC `[fatal]`
  invariant for a page with N marks tracked by this ChangeSet, ReleaseExcessDirtyMarks issues exactly (N-1)
            conservation-respecting DecrementDirty calls, leaving one outstanding mark from this UoW
  never clamp DC to a floor (DecrementDirtyToMin) on the UoW-dispose path
  never drive DC to 0 before the checkpoint has written the page
  scope: ChangeSet.ReleaseExcessDirtyMarks / RegisterReDirty / Reset, PagedMMF.DecrementDirtyByDelta, UnitOfWork.Dispose
  on_violation: DC = 0 before checkpoint → page evictable; dirty data lost before reaching stable media
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously read "ReleaseExcessDirtyMarks caps DC at 1", which is the
    cap-to-1 implementation (DecrementDirtyToMin(p, 1)) that RACED the background checkpoint's DecrementDirty and caused
    the lost-write durability bug #385 — see the doc comment on ChangeSet.ReleaseExcessDirtyMarks, which names the issue.
    DecrementDirtyToMin still exists on PagedMMF, so "fixing" code to match the old rule text was a one-line regression
    back into #385. The conservation property (subtract exactly the marks you added, minus one) is the fix.

### PS-06: Page state transitions under StateSyncRoot `[fatal]`
  invariant all PageState transitions performed under per-page StateSyncRoot lock
  invariant exception: AccessEpoch (lock-free CAS-max, monotonically increasing)
  scope: PagedMMF.cs
  on_violation: TOCTOU race between clock-sweep and latching;
    two threads simultaneously evict and exclusively-latch same page

### PS-07: NextFreeTSN survives restart `[fatal][silent]`
  invariant NextFreeTSN is persisted in the bootstrap dictionary under `BK_NextFreeTSN` — NOT in RootFileHeader (no such
            field exists) and NOT per checkpoint: it is written on clean shutdown only (PersistEngineState)
  invariant on reopen: TSN sequence resumes from persisted value
  scope: DatabaseEngine.cs (BK_NextFreeTSN write/read), DatabaseEngine.ScrubVersionedChains (crash-path floor)
  requires: RB-05 — on the crash path the persisted value can be BELOW the newest consolidated revision; the floor is
            re-derived from surviving scrubbed chain heads. Resuming from the persisted value alone is the bug.
  on_violation: TSN reuse across restarts;
    new transaction shares TSN with old committed transaction;
    MVCC snapshot isolation broken — reads see wrong revision

### PS-08: Read-authorization watermark reflects only durable bytes `[fatal]`
  invariant the file-size watermark that gates disk reads (`loadPage = (offset + PageSize) ≤ _fileSize` in FetchPageToMemory)
            MUST only ever be advanced AFTER the bytes are physically on disk. `_fileSize` is advanced post-write on every
            path: the synchronous paths (WritePageDirect, WritePagesForCheckpoint) advance it after `RandomAccess.Write`
            returns; the ASYNC path (SavePageInternal) must NOT advance it at write-issue — SavePages advances it once in its
            post-`FlushToDisk` continuation, BEFORE any page in the batch becomes evictable (DecrementDirty).
  rationale: `_fileSize` is the sole gate that authorizes a disk read. If it is advanced before the async `WriteAsync`
             physically extends the file, a reader that cache-misses a page in the not-yet-written region issues a read past
             the real EOF → 0 bytes → a torn/zero page. Coupling "durable AND covered by `_fileSize`" before evictability
             guarantees any non-resident page below `_fileSize` is genuinely on disk.
  scope: PagedMMF.SavePages (post-FlushToDisk `TrackFileGrowth(batchEndOffset)`), PagedMMF.SavePageInternal (no growth tracking),
         PagedMMF.FetchPageToMemoryOnMiss (the gate), WritePageDirect / WritePagesForCheckpoint (already post-write)
  on_violation: short disk read (`got 0, expected 8192`) under concurrent checkpoint+fault load → in Release (assert elided)
                a CRC failure on a zero page, or stale/torn content served to a reader
  verified: empirically (the short-read assert stops firing under the full suite); a deterministic fault-injection test is a follow-up

### PS-09: Segment-grow fault→latch is eviction-protected across the gap `[fatal]` `[silent]`
  invariant the grow path's `RequestPageEpoch(Unchecked) → TryLatchPageExclusive` sequence MUST hold the slot pinned across the
            gap (`IncrementSlotRefCount` before the request returns through to the latch, released after — PS-01 makes
            SlotRefCount > 0 non-evictable), and MUST NOT proceed past a failed latch (retry the fault, bounded; never write or
            UnlatchPageExclusive a slot it does not own). The grow tags pages with a bare `GlobalEpoch` snapshot (it cannot hold
            an EpochGuard across the back-pressure-blocking fetch — an EBR pin held across a blocking wait deadlocks reclamation),
            and that snapshot does NOT pin the slot: once GlobalEpoch advances, MinActiveEpoch climbs past it and the just-faulted
            slot becomes evictable in the request→latch gap.
  requires: PS-01 (SlotRefCount blocks eviction), PS-06 (the TOCTOU this prevents)
  scope: LogicalSegment.RequestExclusiveForGrow (the pinned, bounded-retry helper) routing every grow latch site (GetPageExclusive[Unchecked],
         CreateOrGrow data/map/end/old-tail pages, GetPageAddressExclusive)
  on_violation: PS-06 TOCTOU — the slot is evicted and reused for another file page in the gap; the grow then clears/rewrites it
                and calls UnlatchPageExclusive, releasing the OTHER thread's latch and forcing PageState=Idle + a stray seqlock
                bump → cross-thread page-cache corruption (a native host crash in Debug; silent heap corruption in Release).
                A leaked SlotRefCount (decrement skipped on a latch-throw) permanently pins a page → cache fills → back-pressure
                stalls grows holding latches → the checkpoint spins forever in CopyPageWithSeqlock (hence the helper's try/finally).
  verified: empirically (latch-fail assert captures 11→0; SimdKwayMergeTests passes isolated; full suite no longer host-crashes);
            a deterministic eviction-in-the-gap fault-injection test is a follow-up

---

## Module: WAL Recovery

> **Scope note (post-#399, post-increment-D):** this module describes the **surviving v1 `WalRecovery` scan** only — the
> phases that still run (1–3 discover/scan/cross-ref, 6 TickFence replay, 7 finalize). **Phase 4 (FPI torn-page repair) is
> gone** (FPI deleted, increment D, 2026-06-16) and **Phase 5 (committed-transaction replay) moved to the v2 `RecoveryDriver`**
> (logical-record apply through the engine's own write paths — see Module AP, AP-10..13). For the in-depth, code-accurate split
> see `doc/in-depth-overview/11-durability.md §7.1`, which documents exactly the surviving phases.

### REC-01: Recovery stops at first corruption boundary `[fatal]`
  invariant Phase 2: stop scanning at first CRC chain break or torn chunk
  never attempt to read past corruption boundary
  scope: WalRecovery, WalSegmentReader.cs
  on_violation: partially-flushed records from uncommitted UoW
    are replayed as committed → atomicity violation; phantom data

### REC-02: Phase ordering `[fatal]`
  invariant (surviving v1 scan) [Phase 1: scan segments] → [Phase 2: validate CRC chains]
    → [Phase 3: cross-reference UoW registry] → [Phase 6: TickFence / ClusterTickFence replay]
    → [Phase 7: finalize]
  note Phase 4 (FPI torn-page repair) is RETIRED (FPI deleted, increment D); Phase 5 (committed-record replay)
    is no longer part of this scan — the v2 `RecoveryDriver` owns logical-record apply (Module AP), which runs
    after archetype init, then scrub/rebuild/seal (Module RB)
  scope: WalRecovery
  on_violation: any reordering corrupts recovery;
    see UR-03/UR-05 (registry before downstream apply)

### REC-03: Pending UoW → Void; WalDurable UoW → replay `[fatal]`
  invariant Pending = no durable WAL records confirmed → discard all
  invariant WalDurable = FUA-confirmed WAL records → replay all
  scope: WalRecovery, UowRegistry.cs
  on_violation:
    WalDurable treated as Void → discards committed-and-flushed data
    Pending treated as WalDurable → reintroduces uncommitted data

### REC-04: WAL replay starts from CheckpointLSN `[fatal]`
  invariant recovery reads CheckpointLSN from the bootstrap meta-pair (DurabilityWatermarks), not RootFileHeader —
            RootFileHeader still exists but carries only signature / format revision / chunk size / database name
  invariant scans WAL segments starting at CheckpointLSN, not from LSN 0
  scope: RecoveryDriver.cs, DurabilityWatermarks.cs, DatabaseEngine.cs (watermark seed)
  on_violation:
    starting too late → committed changes below CheckpointLSN are missed → data loss
    starting too early → re-applies already-checkpointed records;
      usually benign but risks edge-case double-apply corruption

### WR-01: Reopen reconciles the WAL directory against the CheckpointLSN frontier `[fatal][silent]`
  invariant on open, WalSegmentManager.Initialize scans the WAL directory and:
    - continues segment numbering from (max existing id + 1) — NEVER restarts at 1
      (restart re-collides filenames and strands higher-id files; it also makes on-disk id order
       diverge from LSN order, which DiscoverSegments sorts by — see REC-02 ordering)
    - deletes files with no valid header (empty/pre-allocated placeholders): recovery's
      WalSegmentReader.OpenSegment rejects them and Phase 2 skips them, so they hold zero records
    - deletes valid segments whose records are all < CheckpointLSN, AND segments covering no live LSN
      (computed LastLSN < FirstLSN — header-only / never-written), via the existing MarkReclaimable gate
    - adopts the remaining valid segments (records ≥ CheckpointLSN) into _sealedSegments for normal
      checkpoint-gated reclaim
  invariant NEVER delete a segment that may hold records ≥ CheckpointLSN (recovery replays from it — REC-04)
  scope: WalSegmentManager.Initialize, DatabaseEngine.InitializeWalManager, WalSegmentManager.MarkReclaimable
  on_violation:
    skip the reconcile → prior-lifecycle segment files + pre-allocated placeholders orphan forever
      (never in _sealedSegments) → unbounded WAL disk growth per engine lifecycle
    delete a ≥CheckpointLSN segment → recovery loses committed records → data loss
  note: extends ADR-037 (checkpoint-driven WAL recycling) to the reopen path — same MarkReclaimable
    frontier, applied once at open to adopt/trim prior-lifecycle segments (no separate ADR)

### WR-02: The segment reader traverses zero-padding gaps between page-aligned drain blocks `[fatal][silent]`
  context the WAL writer issues each drain as its own O_DIRECT block: bytesToWrite = AlignUp(data.Length, PageSize)
    and segment.WriteOffset advances by that padded length (WalWriter.WriterLoop step 5). A frame whose length is
    not a page multiple therefore leaves a ZERO TAIL before the next page-aligned block. Blocks are written
    consecutively (append-only, no inter-block gaps) and each block's first frame sits at its page-aligned start.
  invariant WalSegmentReader.AdvanceToNextFrame, on reading FrameLength == 0 at offset O:
    - if O is NOT page-aligned → it is an intra-block padding tail: jump to AlignUp(O, PageSize) and keep scanning
    - if O IS page-aligned → no block was ever written there → genuine end-of-data (stop)
  invariant the chunk CRC chain (PrevCRC linkage) is continuous across blocks (WalWriter._lastFooterCrc is writer-
    thread state carried across drains), so a reader that skips a padding tail still validates the chain end-to-end
  scope: WalSegmentReader.AdvanceToNextFrame, WalWriter.WriterLoop (block-padding write), RecoveryDriver, WalRecovery
  on_violation: the reader stops at the FIRST padding gap (e.g. after the opening frame of the first drain block) and reports
    end-of-data while committed records sit durable in later blocks → recovery scans zero commit records → silent data loss
    across a crash (exactly the One-True-Crash-Test failure mode before this fix)
  note: surfaced by the multi-drain crash test (each Immediate commit flushes its own padded block); the watermark
    (DurableLsn) was honest throughout — the bytes were on disk, only unreachable by the contiguous frame walk

---

## Module: Versioned HEAD Reopen

Versioned-component HEAD values live in-place in the persisted cluster slot (07-versioned-overlay.md §Write-Path),
so a graceful close leaves them current on disk and `RebuildVersionedHeadFromChain` (~49% of a large DB's open) is
pure waste on a clean reopen. It exists only to repair the commit↔tick-fence crash window (chain WAL durable, slot
WAL not). A clean-shutdown FLAG (`BK_CleanShutdown`) lets a graceful reopen skip the rebuild safely. The flag is a pure
clean/dirty bit, NOT keyed on CheckpointLSN — a bulk-generated DB closes cleanly with CheckpointLSN == 0, and gating
trust on a non-zero LSN wrongly forced a full rebuild for exactly those DBs (the data is current in the .bin regardless
of the LSN value).

> **Depends on `SL-07` (Module: Seqlock).** A clean reopen trusts the HEAD values sitting in the persisted cluster
> slot, and those slots are written through the page seqlock — so the barrier-placement invariant is what makes
> "the flag says clean, therefore the bytes are current" true rather than hopeful. `SL-07` was **restated in full
> here** until 2026-08-07 (#703): one id defining two blocks, which `README.md` forbids and which
> `scripts/audit-rule-coverage.py` now fails CI on. The canonical statement — strictly more complete than the copy
> that lived here — is in **Module: Seqlock**; the two clauses this copy had that it did not (RB-04 cannot see a
> valid-CRC torn page; no FPI backstop) were folded into its `on_violation`.

### CS-01: Clean-shutdown flag written strictly after data fsync `[fatal][silent]`
  pre PersistEngineState data fsync complete (every dirty cluster page with current HEADs is durable)
  post MarkCleanShutdown: BK_CleanShutdown = 1 + its OWN fsync (never bundled with the data flush)
  invariant runs only on graceful Dispose, after final checkpoint → PersistArchetypeState → PersistEngineState (CPO-06)
  scope: DatabaseEngine.Dispose, DurabilityWatermarks.SetCleanShutdown, ManagedPagedMMF.PersistMetaNow
  requires: CK-05 — the flag rides the meta-pair generation flip, so its durability is atomic and torn-slot
            detected. That is STRONGER than this rule originally claimed.
  note the bootstrap key `BK_CleanShutdown` this module used to name is DEAD — the const is declared and never
       read or written. Live storage is the packed DurabilityWatermarks value.
  on_violation: flag durable ahead of the cluster pages it vouches for → a torn close lets the next open trust
    stale HEADs and skip the repair rebuild → silent stale reads

### CS-02: Clean-shutdown flag cleared on open before any mutation `[fatal][silent]`
  pre bootstrap loaded; flag value captured for the trust decision (ctor loading path)
  post InitializeArchetypes: SetInt(BK_CleanShutdown, 0) + fsync, before the engine accepts any write
  invariant the clear is the authoritative dirtying step — a session that mutates then crashes leaves the flag = 0
  scope: DatabaseEngine.InitializeArchetypes
  on_violation: flag survives an unclean session → next open trusts stale HEADs

### CS-03: HEAD rebuild skipped iff the clean flag was set `[fatal]`
  invariant trusted ⇔ (BK_CleanShutdown == 1 at open ∧ no component migrated this session) — independent of CheckpointLSN
  invariant trusted ⇒ skip RebuildVersionedHeadFromChain (persisted cluster-slot HEADs are current)
  invariant ¬trusted ⇒ rebuild runs exactly as before (the crash-window repair path is preserved)
  scope: DatabaseEngine.InitializeArchetypes, ArchetypeClusterState.RebuildVersionedHeadFromChain
  on_violation: skipping when not provably clean → stale HEAD served from the cluster slot

---

## Cross-Cutting: Commit Path Ordering

The steps inside `tx.Commit()` must occur in this exact order:

> **Renamed `CX-` → `CPO-` (2026-08-07, #703).** These ids collided with `concurrency.md`'s cancellation
> family `CX-01..CX-05`, which the README indexes and which other documents cite — two different meanings
> under one id, exactly what `README.md` forbids ("Rule IDs must be unique across the entire database") and
> what `SQ-01..05`/`PS-01` were renamed for before. `CPO` = Commit Path Ordering, this section's own name.
> The collision was found by `scripts/audit-rule-coverage.py`, which now fails CI on any duplicate id.

### CPO-01: Conflict detection before UoW ID stamping `[fatal]`
  pre Step 1: MVCC conflict detection
  post Step 2: stamp UowId on pending revisions
  scope: Transaction.Commit
  on_violation: stamping before conflict check → revision visible under UoW ID
    even if transaction is about to be rolled back

### CPO-02: UoW ID stamping before WAL serialization `[fatal]`
  pre Step 2: stamp UowId
  post Step 3: serialize to ring buffer (contains UowEpoch)
  scope: Transaction.Commit, WalCommitBuffer.cs
  on_violation: WAL record contains invalid/zero UoW ID;
    recovery cannot associate record with its UoW

### CPO-03: WAL serialization before durability wait `[correctness]`
  pre Step 3: ring buffer write (returns LSN)
  post Step 4: WaitForLSN(lsn) (Immediate mode)
  scope: Transaction.Commit, WalWriter.cs
  on_violation: nothing to wait for; wait returns immediately
    or waits on wrong LSN

### CPO-04: DurabilityOverride can only escalate `[correctness]`
  invariant DurabilityOverride ∈ {Default, Immediate}
  invariant Immediate UoW cannot be downgraded per-transaction
  scope: Transaction.Commit
  on_violation: downgrading creates data-at-risk within an Immediate UoW,
    violating the user's explicit durability contract

### CPO-05: Holdoff regions cover WAL flush and structural operations `[fatal]`
  invariant WAL flush, index split/merge, commit loop, rollback
    are all enclosed in holdoff regions (cancellation suppressed)
  scope: UnitOfWorkContext.cs, Transaction.cs, BTree.cs
  on_violation: cancellation mid-WAL-flush → partial WAL record;
    CRC chain broken → recovery truncates, losing all subsequent records

### CPO-06: Graceful shutdown ordering `[fatal]`
  invariant [signal workers stop] → [WAL writer drains + final FUA]
    → [final checkpoint] → [registry: WalDurable→Committed] → [close files]
  scope: DatabaseEngine.Dispose
  on_violation: files closed before final checkpoint →
    next startup is crash recovery (acceptable if Step 2 completed);
    WAL writer not drained → ring buffer data lost

---

## Module: BulkLoad

The opt-in throughput-first write path. Skips per-row WAL; brackets a bulk with a
`BulkBegin` / `BulkEnd` manifest pair; on crash without `BulkEnd` durable, the recovery
state machine discards the bulk's segments wholesale (Phase 3b in `WalRecovery`).
Full design at `claude/design/Durability/BulkLoad/`. ADR: `claude/adr/053-bulk-load-write-path.md`.

These invariants are **additive** — the standard-path WAL / Checkpoint / Recovery
invariants continue to hold during a bulk session.

### BL-01: No per-row WAL during a bulk session `[fatal]`
  invariant ∀ chunk written on behalf of an open BulkLoadSession
            between BulkBegin.LSN and BulkEnd.LSN:
              chunk.Type ∉ { WalChunkType.Transaction }
  invariant chunks of type TickFence, ClusterTickFence MAY appear
            (they are not bulk-session records — orthogonal infrastructure;
             FullPageImage is RETIRED — increment D — a deliberate gap at chunk-type 2, skipped as unknown)
  scope: BulkLoadSession.{Spawn, Update, Destroy},
         Transactions/public/BulkLoadSession.cs
  on_violation: per-row record emitted → recovery's Phase 3b discards the bulk's
    pages but Phase 5 replay writes the per-row record into newly-freed pages,
    corrupting future allocations.

### BL-02: BulkBegin → BulkEnd pairing `[fatal]`
  invariant BulkSession durability ↔
            (∃ BulkBegin ∧ ∃ BulkEnd ∧ BulkEnd.LSN ≤ DurableLSN)
  invariant CompleteBulkLoad returns ⟹ all three conjuncts hold
  🔵 RECOVERY HALF UNBUILT (verified 2026-07-28): there is no Phase 3b. Both manifest arms in the v1 WalRecovery
     switch are explicit no-ops, and they key on chunk types that NOTHING emits (only Transaction chunks are ever
     written), so the manifest counters are structurally always zero. The v2 RecoveryDriver defers BulkManifest
     entirely. What actually provides v1 correctness is that the bulk UoW stays Pending and VoidRemainingPending voids
     it (UR-03/UR-05) — visibility-safe, but pages leak and BitmapL3.FreeRange has no caller on this path.
  scope: BulkLoadSession.CompleteBulkLoad (writer half — holds)
  on_violation: CompleteBulkLoad returns without BulkEnd durable → caller
    is told the bulk succeeded but a crash loses everything; Phase 3b on
    the next reopen discards the bulk wholesale, violating the API's
    "completed = durable" contract.

### BL-04: CompleteBulkLoad is a synchronous durability barrier `[fatal]`
  invariant CompleteBulkLoad returns
    ⟹ CheckpointLSN ≥ BulkBegin.LSN
       ∧ BulkEnd.LSN ≤ DurableLSN
       ∧ (UNBUILT) every page in the bulk allocation log is on the data file — no such log exists; the manifest's
         page-range count is hard-zero in v1
  pre  Step 1: drain ChangeSet (DC ≥ 1 on every bulk page)
  pre  Step 2: ForceCheckpoint + WaitForCheckpoint
  pre  Step 3: verify CheckpointLSN ≥ BulkBegin.LSN
  pre  Step 4: emit BulkEnd
  pre  Step 5: WaitForDurable(BulkEnd.LSN)
  post bulk session State = Closed
  scope: Transactions/public/BulkLoadSession.cs::CompleteBulkLoad
  on_violation: returning before any conjunct holds → next reopen may
    discard data the caller thought was committed. Returning before
    Step 2's fsync leaves dirty pages with no WAL backing; returning
    before Step 5 leaves the manifest in-memory only.
  related: WP-01, WP-03, CP-03, CP-05

### REC-06: WalRecovery skips unknown chunk types `[correctness]`
  invariant ∀ chunk c: c.Type ∉ {Transaction, TickFence,
                                  ClusterTickFence, BulkBegin, BulkEnd}
            ⟹ Phase 2 dispatch hits `default:` and continues
  note chunk-type 2 (`FullPageImage`) is a RETIRED gap (FPI deleted, increment D): it is intentionally absent from the
    known-set, so a legacy segment carrying an FPI chunk is skipped as unknown-but-CRC-valid (NOT mis-parsed, NOT a throw).
  scope: src/Typhon.Engine/Durability/internals/WalRecovery.cs::Recover (Phase 2)
  on_violation: unknown future chunk types cause recovery to throw,
    leaving the engine unable to open databases written by a future Typhon.
  rationale: forward compatibility with future chunk types (`BulkLoadV2 = 7`,
    compression markers, etc.) plus backward tolerance of the retired FPI gap; the validity boundary is REC-01's
    "stop at first corruption" — unknown-but-CRC-valid is not corruption.

## Module: RB — Rebuild & repair (P1.2 recovery net; FPI's replacement)

The net that lets WAL-v2 recovery survive torn pages WITHOUT FPI. **Increment D (2026-06-16) deleted FPI** — this net is now the SOLE
torn-page protection. Derived structures are rebuilt from primary data (never trusted/repaired); primary pages heal-by-replacement or fail the
open loudly (RB-04). Every primary segment is `ChunkBasedSegment`-backed, so `ResolveSuspectPrimaryPages` loud-fails any torn primary page
uniformly (no silent acceptance) — proven by `SuspectPageClassification_PartitionsDerivedVsPrimary` (classification) + the tear gates (end-to-end).

### RB-01: Derived structures are rebuilt, never repaired `[fatal]`
  invariant ∀ derived structure (secondary B+Tree indexes + their multi-value HEAD/TAIL buffers, occupancy bitmap,
            spatial index, AND the EntityMap of a *rebuildable* archetype): integrity doubt post-crash ⟹ rebuilt from
            primary data; never page-repaired, never trusted. The EntityMap is derived-on-crash because a torn EntityMap
            page holds a hash directory of chunk-id POINTERS — trusting it dereferences garbage into a hard process crash
            *before* any loud-fail can fire (unlike opaque-byte component pages, which RB-04 catches post-hoc). An archetype
            is "rebuildable" iff it is cluster-eligible (cluster slots persist EntityKeys[N] + EnabledBits[C] +
            OccupancyBits — fully self-describing) OR all its non-Transient slots are Versioned (chain heads carry every
            location).
            🔴 The old residual — "the rare non-cluster archetype that still owns a SingleVersion slot, reached via a
            Transient-indexed slot" — NO LONGER EXISTS as of #655. That shape was produced solely by the cluster-eligibility
            rule that disqualified any archetype holding an indexed Transient field; with the rule gone the archetype is
            cluster-backed, so its SV locations have a persisted source like everyone else's. Every archetype is now
            rebuildable.
            🔴 The trailing claim "and the only flat shape left is pure-Versioned" was STALE and is removed (#704).
            #629 inverted it: a Versioned-only archetype is cluster-backed like every other — the cluster slot holds the
            published HEAD while the history stays in the revision chain. Measured, not argued:
            ClusterStorageMatrixTests.EveryShape_IsClusterEligible_WithTheSegmentsItsCompositionImplies asserts a
            non-null ClusterState AND a non-null PersistentStore cluster segment for PureVersioned, across every
            durability mode and index kind, and passes. There is no flat shape left.
  scope: crash path clears + recreates indexes empty at open (ComponentTable.BuildIndexedFieldInfo /
    BTreeBase.ClearSharedSegment) and repopulates from final HEADs after apply+scrub via
    ArchetypeClusterState.RebuildIndexesFromData, driven by DatabaseEngine.RebuildClusterIndexes on the crash path.
    ClearMultiValueTail is gone with the shared index home (#629); the per-archetype tree is cleared and rebuilt whole
    rather than having its multi-value tail cleared separately.
  🔴 The EntityMap half has a second precondition that is NOT integrity doubt: a schema migration allocates a fresh
    EntityMap AND a fresh cluster, so there is nothing stale to discard and the crash rebuild has no primary data to
    derive from — its occupancy walk sees an empty cluster. Re-deriving there produced an EMPTY map and skipped the
    re-placement pass that is the actual rebuild (RebuildClusterFromChains), losing every entity of the archetype. So
    "integrity doubt ⟹ rebuild" must read "integrity doubt AND the primary data still exists ⟹ rebuild"; a migrating
    open is repopulation, not recovery. Gate: DatabaseEngine.WillRebuildEntityMapOnCrash && !HasMigratedSlot.
    (ComponentTable.RebuildSecondaryIndexEntriesFromHeads, DatabaseEngine.RebuildSecondaryIndexes).
    🔴 BOTH index homes, since #656. A cluster-backed archetype keeps its field indexes on the ARCHETYPE, and that home did
    NOT implement this rule: the cluster-index init block read the persisted SPI, loaded the segment and skipped its rebuild,
    so `WalFilesPresentAtOpen` — consulted in six places — reached none of it. Now the same shape as the flat home: the init
    block clears (ClearSharedSegment over both stride segments) and DatabaseEngine.RebuildClusterIndexes repopulates from the
    cluster SoA in Phase 5, via ArchetypeClusterState.RebuildIndexesFromData. Rebuilding from the SoA rather than from chain
    heads is what makes it storage-mode-agnostic: the cluster slot IS the head for SingleVersion and carries the published
    head for Versioned (D1). The EntityMap is
    discarded (RawValuePagedHashMap.ClearForRebuild) and re-derived from authoritative data on the crash path
    (DatabaseEngine.RebuildEntityMapsFromPersistedData crash branch: cluster occupancy walk → RebuildClusterEntityMapEntries,
    or Versioned chain heads → BuildFlatEntityMapEntries via InsertDuringRebuild), BEFORE WAL apply. A CRC-failing derived
    page in RecoverySuspect mode is accepted (it will be discarded+rebuilt), not repaired; a rebuilt EntityMap segment is
    skipped by ResolveSuspectPrimaryPages (its torn page is healed by the rebuild, keyed on RootPageIndex).
  on_violation: a repaired-but-stale index diverges from the data it indexes (the v1 index-divergence class) — queries
    return wrong/missing rows with no error; a trusted torn EntityMap dereferences garbage chunk-id pointers → hard crash.
    🔴 A trusted torn PER-ARCHETYPE index page does the same. Its leaf values are packed ClusterLocations — POINTERS into the
    cluster SoA — so the failure is not "wrong rows" but an AccessViolationException on the first decode, observed directly by
    reverting #656 under TornCheckpointedClusterIndexPage_RecoversViaRebuild. This home belongs in the EntityMap's category,
    not the opaque-bytes one: it cannot be caught post-hoc, because the process is already gone.
  verified_by: DifferentialRecoveryOracleTests (IndexedFlat_IndexAxis, AtScale, LargeDrain, CheckpointFrontier,
    MultiValueIndex_DuplicateKeys, TornCheckpointedIndexPage_WithFpiRepairDisabled_RecoversViaRebuild,
    TornFlatVersionedEntityMapPage_AfterPriorShutdown_RecoversViaRebuild,
    TornClusterEntityMapPage_AfterPriorShutdown_RecoversViaRebuild) + RawValuePagedHashMapTests.ClearForRebuild_EmptiesAndAllowsRebuild;
    per-archetype home (#656): ClusterIndexed_IndexAxis_MatchesBroadScan, ClusterMultiValueIndex_DuplicateKeys_AllRebuiltAfterCrash,
    TornCheckpointedClusterIndexPage_RecoversViaRebuild — `[VerifiesRule("RB-01")]`.

### RB-02: Rebuild ordering `[fatal]`
  invariant [Phase-3 apply + Phase-4 scrub complete] → [Phase-5 index/derived rebuild] → [Phase-6 suspect resolution]
    → [seal checkpoint]. Indexes are rebuilt from FINAL HEAD data, never over un-scrubbed chains. This is why the
    per-archetype home's rebuild could not simply be re-enabled where its CLEAR lives: the cluster-index init block runs
    inside InitializeArchetypes, BEFORE RunWalV2Recovery, so a rebuild there indexes pre-apply state — RB-01's letter met and
    this rule broken, yielding an index that is confidently wrong rather than merely stale (#656).
  scope: DatabaseEngine.RunWalV2Recovery (ScrubVersionedChains → RebuildSecondaryIndexes → RebuildClusterIndexes →
    ResolveSuspectPrimaryPages → SealRecovery). Exactly one of the two cluster-index rebuild sites runs per open: the init
    block's, on a clean reopen, or RebuildClusterIndexes, on the crash path — RunWalV2Recovery returns immediately when no
    WAL window exists.
    A THIRD ordering applies on a migrating open: RebuildClusterFromChains must place the entities BEFORE anything reads
    the cluster, and the cluster head rebuild (ArchetypeClusterState.RebuildVersionedHeadFromChain) must run AFTER the
    EntityMap it reads has been re-derived — DatabaseEngine.DrainDeferredVersionedHeadRebuilds exists precisely to defer
    it past that point. Running it earlier dereferences a torn EntityMap's hash directory into a hard process crash,
    which is RB-01's own rationale applied one level down.
  on_violation: an index built over pre-scrub MVCC history carries stale/duplicate keys; a suspect resolved before
    rebuild misclassifies a to-be-discarded derived page; a cluster index built at open covers only the checkpointed half of
    the data and silently omits every entity recovered from the WAL window.
  verified_by: DifferentialRecoveryOracleTests.ClusterIndexed_MidCheckpoint_IndexAxisHolds (rebuilding at open instead of in
    Phase 5 reports exactly the checkpointed subset) — `[VerifiesRule("RB-02")]`.

### RB-03: Chain scrub postcondition `[fatal]`
  post ∀ Versioned (entity, component) chain: exactly one committed HEAD element (highest TSN, IsolationFlag==0),
    ItemCount==1, NextChunkId==0, UowId cleared; non-head revision/overflow chunks freed; cluster HEAD ≡ chain HEAD (D1).
  scope: ComponentRevisionManager.ScrubChainToHead / SweepTableOrphans; DatabaseEngine.ScrubVersionedChains.
  on_violation: pre-crash MVCC history survives into the consolidated base → visibility corruption / orphan leak.
  verified_by: DeferredCleanupTests.Scrub_CollapsesMultiRevisionChain_ToHeadValue_Idempotent.

### RB-04: Suspect primary pages heal or fail loudly `[fatal]`
  invariant a CRC-failing PRIMARY page (component/revision content, collections, cluster, string table, system, AND a
    NON-rebuildable EntityMap — a non-cluster archetype that still owns a SingleVersion slot) in RecoverySuspect mode is
    recorded suspect, NOT repaired/thrown at load. Post-rebuild resolution: if the page still backs a live (allocated)
    chunk ⟹ the open FAILS LOUDLY with a diagnostic bundle {segment, filePage}; if no live chunk remains (the entity was
    re-created in-window and scrub freed the old chunk) ⟹ healed. NEVER a silent open over corrupt primary data.
  🔴 DETECTION BOUNDARY (2026-07-28): this net detects a torn page ONLY via CRC mismatch. A page whose tear is
     produced UNDER a valid checksum is outside its reach — it never enters the suspect set, so the loud-fail cannot
     fire. See issue #579 (the seqlock has no memory barriers, so on arm64 the checkpoint can CRC and persist torn
     bytes). Before increment D this was partially masked: FPI repaired checkpointed pages by replacement regardless of
     how the tear arose. FPI is deleted and Module RB is now the SOLE torn-page protection, so the gap is unmasked with
     nothing behind it.
  note the sweep only reaches pages backing an ALLOCATED chunk of a registered ChunkBasedSegment<PersistentStore>; any
       suspect not matched falls through as healed. Bounded in practice (a page only becomes suspect if it was read),
       but state the precondition rather than implying universal coverage.
  scope: PagedMMF.EnsurePageVerified (RecoverySuspect branch + STO-10 page-state lock); DatabaseEngine.
    ResolveSuspectPrimaryPages (forward live-chunk→page map; ThrowHelper.ThrowCorruption). A suspect EntityMap page of a
    REBUILDABLE archetype is rebuilt (RB-01) and skipped here, NOT loud-failed. The documented residual — a non-rebuildable
    EntityMap belonging to the flat-SV-via-Transient-indexed archetype — was retired by #655, which made that shape
    cluster-backed; no archetype's EntityMap loud-fails on this path any more.
    🔴 DatabaseEngine.IsDerivedSegmentKind skips StorageSegmentKind.Index on the premise that RB-01 discarded and rebuilt it.
    Until #656 that premise held for the per-ComponentTable home ONLY, so a torn PER-ARCHETYPE index page was skipped here and
    also never rebuilt — neither loud-failed nor healed, but silently served, the one outcome this rule's preamble says cannot
    exist. The premise now holds for both homes. A skip predicate that names a segment KIND is only as true as the weakest
    implementation behind that kind: adding a second owner of a derived kind silently widened the skip.
  on_violation: serving an entity's torn content as if intact → silent data corruption visible to queries.
  verified_by: DifferentialRecoveryOracleTests.TornReachablePrimaryPage_WithFpiDisabled_FailsOpenLoudly +
    EntityMapRebuildability_Classifier (the rebuildable/non-rebuildable boundary) — `[VerifiesRule("RB-04")]`.

### RB-05: TSN resumption `[fatal]`
  post NextFreeTSN(after recovery) > max(TSN persisted at last checkpoint, max TSN applied from the replayed window).
  invariant the resumption floor is max( TSN persisted at last clean shutdown,
                                        max TSN applied from the replayed window,
                                        max TSN over surviving SCRUBBED CHAIN HEADS )
  note 🔴 the third term is load-bearing and was missing from this rule until 2026-07-28. A consolidating
       checkpoint can advance committed TSNs into the data file WITHOUT leaving them in the WAL window, and
       BK_NextFreeTSN is refreshed only on clean shutdown — so on a hard crash the first two terms can both land
       BELOW the newest consolidated revision, and every post-recovery reader then snapshots beneath it while
       MVCC hides the latest value. PS-07 depends on this rule for exactly that reason.
  scope: DatabaseEngine.ScrubVersionedChains (tracks the max surviving head and calls SetNextFreeId — the
         DECISIVE restore), RecoveryDriver (watermarks the replayed window only)
  on_violation: TSN reuse across restart → two distinct writes share a TSN → MVCC visibility corruption.

### RB-06: recovery restores EVERY allocation watermark, not only the TSN `[fatal]`
  post ∀ allocator A reachable after recovery:
         next_value(A) > max(value persisted at the last checkpoint, max value applied from the replayed window)
  invariant RB-05 is one instance of this rule, not the whole of it. Recovery reconstructs STATE and COUNTERS
            independently, so an allocator whose counter is restored below its own restored population hands out
            an identifier that a live recovered object already holds — and the collision is silent, because both
            sides are individually well-formed.
  note the audit, as of 2026-08-07 (#705). Kept as a list because "which allocators exist" is the part that is
       easy to get wrong; a rule naming only the ones we remembered would read as complete.

       | Allocator | Restored by | State |
       |---|---|---|
       | `NextFreeTSN` | `ScrubVersionedChains` + `RecoveryDriver` | ✅ RB-05 |
       | `ArchetypeEngineState.NextEntityKey` | `RebuildEntityMaps*` (persisted base) + `RecoveryDriver` (replayed window) | ✅ fixed #697 / #705 |
       | WAL LSN (`WalCommitBuffer._lsnBase`) | `DatabaseEngine.InitializeWal` via `Math.Max(lastValidLSN, checkpointLsn)` | 🔴 **#712** — both terms are 0 when the prior session crashed without checkpointing, because WAL v2 recovery has not run yet at that point; the writer restarts at 1 and collides with the window it is about to replay |
       | VSBS buffer free-list | — | 🔴 **#389** — restarts from empty, re-issuing handles live recovered entities still hold |
       | Cluster slot cursors | derived from the cluster occupancy at rebuild | ✅ |

       The two open rows are the same defect shape as #697, on different allocators. #697's own acceptance asked
       for this audit precisely because fixing one instance says nothing about the others.
  scope: RecoveryDriver (window watermarks), DatabaseEngine.RebuildEntityMaps* / InitializeWal (persisted base),
         RecoveryApplier.MaxEntityKeyByArchetype (the window's entity-key half)
  on_violation: an identifier is re-issued to a second object while the first is still live → the first is
                silently overwritten (entity key), or its records are discarded as already-consolidated (LSN).
### RB-07: A rebuild whose primary data cannot satisfy the constraint must still open the database `[fatal]`
  invariant a rebuild that cannot represent the primary data in the derived structure DROPS what it cannot represent and
            REPORTS the count; it never fails the open. The entity is the primary datum and survives; the index entry is
            derived and does not.
  never throwing out of `InitializeArchetypes` because recovered data violates a derived structure's constraint — the
        state is on disk, so the next open repeats it and no caller can catch its way out
  never dropping the ENTITIES to satisfy the derived structure. They are valueless, not absent, and deleting user data to
        make an index representable inverts which of the two is primary.
  enforce the rebuild counts the drops and the caller logs them at Warning naming the archetype
        (`ArchetypeClusterState.RebuildIndexesFromData` returns the count, `DatabaseEngine.NoteUniqueIndexRebuildConflicts`
        reports it and accumulates `LastOpenUniqueIndexRebuildConflicts`)
  scope: ArchetypeClusterState.RebuildIndexesFromData (the UniqueConstraintViolationException catch), the three
         DatabaseEngine rebuild call sites, DatabaseEngine.LastOpenUniqueIndexRebuildConflicts
  on_violation: the database is PERMANENTLY unopenable — measured: a hard crash on a SingleVersion archetype carrying a
                unique `[Index]` under TickFence returned all 64 entities with all 64 keys zeroed, and the second `Add`
                threw `UniqueConstraintViolationException` out of `InitializeArchetypes` on every subsequent open.
  rationale: #710. Steps 1-4 of that path are all the contract working as designed — TickFence does not make SingleVersion
             VALUES durable per commit (that is what `CommitDiscipline.Commit` exists to opt out of), lifecycle records
             ARE durable, so the archetype legitimately returns with every entity and every key zeroed. RB-01 says derived
             structures are rebuilt from primary data and has no clause for primary data that CANNOT satisfy the constraint
             the derived structure carries. Losing ≤1 tick of values is the documented trade; a database that will not open
             is not, and that is the only step worth changing.
             Registration-time rejection was considered and does not work: `CommitDiscipline` is per-transaction, not
             per-archetype, so registration cannot know whether this archetype will ever be written under `Commit`. That is
             #568's territory.
  requires RB-01 (this is its missing clause, not a competing rule)
  note the surviving index is INCOMPLETE, not wrong-by-omission-only: a later insert of the same key will be accepted,
       because the unique tree holds one copy. The Warning is what makes that state discoverable; silence would trade an
       unopenable database for a quietly incomplete index, which is the worse of the two.
  verified: AxisArchetypesTests.SvWithUniqueIndex_AfterHardCrash_StillOpens [VerifiesRule] — asserts the open succeeds, the
            drop count is NON-ZERO, and every entity is still reachable by scan. The drop-count assertion is load-bearing:
            "it opens" is equally true of a rebuild that never ran and of an archetype with nothing to index, and both would
            satisfy every other assertion in the test.

---

## Module: IR — Offline integrity check & repair

The out-of-engine scanner and repair tool (#729). Distinct from Module RB, which is the engine's own recovery net running on
the crash path: RB repairs a database it is *opening*, IR inspects one nobody has opened and may decline to touch it. The two
answer to different constraints — RB must always end with a usable database, IR must never make one worse.

### IR-01: Repair refuses any on-disk format revision but its own `[fatal]` `[silent]`
  invariant ∀ mutation of a database by `DatabaseRepair`:
              file.DatabaseFormatRevision == PagedMMF.DatabaseFormatRevision
            The comparison is EQUALITY, not `≤`. Older is not a safe subset of newer: a revision bump may re-mean bytes an
            earlier revision left unused, so an older page does not fail to decode — it decodes to a confident wrong answer.
            Revision 7 is the standing example (#753): it claimed `[54,56)` for the chunk stride, and those bytes read as
            zero on a revision-6 page, which is this build's sentinel for "this segment holds no chunks".
  never a `--force`, `--ignore-version` or equivalent override on the mutating path. An escape hatch here exists only to let
        someone write to a layout the tool cannot interpret, on a day they are already recovering from something.
  invariant the refusal is by VERB, not by tool: `IntegrityScanner.Scan` still scans a mismatched revision and reports the
            mismatch as a finding, and `IntegrityReportText`/`Json` still render it. Refusing to DIAGNOSE an unfamiliar
            revision defeats the scanner's whole premise — the operator reaching for it has already lost the happy path.
            Diagnosis degrades; mutation does not.
  scope: DatabaseRepair.DescribeRevisionRefusal (the single policy), DatabaseRepair.Apply (gate — placed before the
    fingerprint drift check and before the pre-repair copy, so a refusal writes nothing at all), DatabaseRepair.Plan
    (emits zero steps and sets RepairPlan.BlockedReason), BootstrapChecks.CheckIdentity (the CHK-BOO-02 finding),
    RepairCommand (renders the refusal instead of offering `--apply`).
  on_violation: the tool writes engine-format-N structures into a format-M database while reporting success. The
    pre-repair copy is taken but the operator has been told the repair worked, so the copy is the thing they delete. This is
    `[silent]`: a repair that "succeeded" is the last place anyone looks for the corruption.
  note `Plan` blocking is distinct from `Plan` being empty, and the distinction is user-visible: empty means nothing needs
       repairing, blocked means this build must not be the one to try. Collapsing them prints "nothing to repair" over a
       database full of findings.
  requires the fingerprint to carry the revision (`DatabaseRepair.Fingerprint` folds in `Identity.FormatRevision`), so a plan
           built against one revision cannot be applied to a database that has since been converted to another.
  verified: FormatRevisionGuardTests — a genuine revision-6 forgery (revision patched in both meta slots, every touched page
            re-stamped through `PagedMMF.StampPageForWrite`, so the file is checksum-valid in every respect except the one
            under test) proves both halves against ONE fixture: the scan still produces a report and names the mismatch, and
            `Apply` refuses it without writing a byte. `Mutant_ApplyWithoutTheRevisionGate` shows the assertion can fail.

