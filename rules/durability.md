# Durability Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-10-19 |
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
  invariant the recovered LastValidLSN is the highest LSN any segment holds, not the last segment's: an open that failed before its recovery
            completed has already started its writer, which left a new segment with no record in it. Taken from it, the frontier fell back to
            CheckpointLSN, the next writer began a segment inside the window's LSNs, and the open after it read the window short (WalRecovery.Recover)
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
            path; DifferentialRecoveryOracleTests.PostRecoveryWrite_ContinuesTheLsnSequenceAboveTheReplayedFrontier [VerifiesRule];
            CrashDuringRecoveryTests.AnOpenStoppedDuringItsRecovery_LeavesTheNextOpensWalFrontierAtTheWindowsEnd [VerifiesRule] (0 without the
            max; on the market, two stopped opens lost 177 to 485 durable transfers)
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
  note: between Append and the end of publish the records can be durable while their page effects are not in memory yet; CK-13 keeps the
        checkpoint below them over that window

### AP-02: Append is the point of no return `[fatal]`
  invariant all conflict validation precedes Append; post-Append the tx reaches Committed and publish does not roll back. A
            durability-WAIT failure after publish surfaces as `CommitDurabilityUncertainException(highLsn)` (committed, durability
            unconfirmed), never as a rollback
  scope: `Transaction.AppendToWal` (point of no return), `Transaction.WaitAndFinalize` (publish-then-surface-uncertain)
  on_violation: appended-but-rolled-back state → recovery resurrects a rolled-back tx

### AP-03: A commit past its append completes its publish, or the engine stops `[fatal]`
  invariant what the publish could reject is rejected before the append, after PREPARE (`ValidatePublishBeforeAppend`), and
            the commit then rolls back clean — no record, nothing visible (AP-07):
            - a key a unique index holds when the write publishes (`UniqueConstraintViolationException`), followed in the
              publish's order: the Versioned writes' index moves first (`CheckVersionedUniqueKeys`, #1262), then spawns,
              then destroys, then the staged Commit-discipline writes in their visiting order. A Versioned write or a spawn
              may take a key a Versioned write before it moves away.
              A staged write may take a key its transaction's destroy removes in the publish (the destroy path's own
              shadow mask decides which slots it removes; under the Commit discipline, the only one with staged writes,
              it removes every persistent slot), one a staged write visited earlier moves away, or one its own entity
              holds. Keys are compared by the index's own comparison (`IBTreeIndex.KeysEqual` / `KeyHash`): -0 and +0
              are one float key;
            - a spawn that leaves unset a Versioned component a unique index covers: `PublishSpawns` would index it under
              whatever its claimed cluster slot holds (`InvalidOperationException`; the same stale key under an AllowMultiple
              index is #1259);
            - a spawn of a spatial archetype without its spatial component (`InvalidOperationException`)
  invariant past the append, page-cache back-pressure is waited out: the committing thread publishes with
            `PagedMMF.PublishSince` set when its append returns, so an allocation that outlasts `PageCacheBackpressureTimeout`
            logs and waits on, a fresh timeout at a time, until `TimeoutOptions.CommitPublishBackpressureTimeout` (60 s) after
            it — one bound for the whole publish. It waits without its pin where it can (#1382, EP-02): between its entries while
            the cache is under pressure, and in a B+Tree write that has taken no latch, reservation or node yet, which unwinds to its
            entry and runs again within the same bound
  invariant any other failure between the append and the end of the publish (`CompleteAfterFailedPublish`): the engine latches
            `CommitPipelineFailure` and pins the commit's first LSN below the checkpoint (`PinnedCommitFloor`, CK-13), then
            the transaction is Committed — never rolled back (AP-02) — and only then is anything that can throw done: the
            chain locks the drain did not reach are released, then the log, then the durability wait on a fresh budget. The caller gets `CommitPublishIncompleteException` (`Durable` says
            whether the records were confirmed). A reopen replays the commit whole. A failure once the publish completed —
            the durability wait, the finalization — is `CommitDurabilityUncertainException` (AP-02)
  invariant a stopped engine appends nothing more: the latch stops `DurabilityLog` itself (`DurabilityLog.Stop`), which
            refuses every later append — a commit, a fence, a bulk manifest. The callers check first: a writable commit is
            refused (`DurabilityException`, `CommitPipelineStopped`) at its start, again just before its append, and once more
            when its WAL slot is claimed — a claim that waited out back-pressure while another commit stopped the engine is
            abandoned (#1269) — so one that was in PREPARE when the engine stopped rolls back clean; both fence paths — the serial `WriteTickFence` and
            the runtime's parallel fence, whose appends never pass through it — return without appending
            (`FenceMustNotAppend`), since a tick path must not throw; a fence whose claim meets a stop latched while it waited
            returns as well (`AppendFenceBatch`, `AppendFenceBlockBatch`). The engine closes as a crash (no clean flag)
  invariant a stopped engine writes nothing more to its data file: the latch crash-stops the checkpointer
            (`CheckpointManager.PrepareCrashStop`); a cycle in flight stops before its next write wave — its unwritten pages left
            owed, the cycle not covered — and before its metadata hook, which saves its pages directly; the realm catalog is not
            written either (`RemoveEmptyClosingRealms`) (#1271). A SingleVersion write under the tick fence still lands in place
            after the stop, and no fence logs it: persisted, the reopen read it beside the log's pre-stop state. Nothing durable
            is lost — the checkpoint was held below the commit's records, and the replay starts there. What is not durable is lost as
            in any crash: under TickFence a spawn's SingleVersion value is checkpoint-durable only (CM-06), so an item crafted since
            the last checkpoint reopens with default lore — measured with MarketHardeningTests' staged stop (TYPHON_MKT_FAIL_PUBLISH_AT):
            coherent under Commit discipline, 4 of 4, with the stop or with a power cut right after it. The cost: no writeback
            debt is discharged after the stop, so a commit past its append that waits on back-pressure runs out its publish
            timeout — its records are in the WAL, and the reopen replays it
  never roll back a commit whose records were appended: recovery replays them, so the rollback is undone at the next open,
        and until then readers have seen part of it and other commits have built on its absence
  never go on appending over a partial publish: a later record builds on what the partial publish left, and recovery replays
        the log over the pages, not over what memory held
  scope: Transaction.Commit, Transaction.ValidatePublishBeforeAppend, Transaction.CheckStagedUniqueKeys,
         Transaction.IsRemovedByOwnDestroy, Transaction.CompleteAfterFailedPublish, Transaction.WaitAndFinalize,
         PagedMMF.PublishSince, TimeoutOptions.CommitPublishBackpressureTimeout, IBTreeIndex.KeysEqual,
         DatabaseEngine.LatchCommitPipelineFailure, DatabaseEngine.CommitPipelineFailure, DatabaseEngine.PinnedCommitFloor,
         DatabaseEngine.WriteTickFence, DatabaseEngine.FenceMustNotAppend, DurabilityLog.Stop, DurabilityLog.Append,
         CheckpointManager.PrepareCrashStop, DatabaseEngine.AppendFenceBatch, DatabaseEngine.AppendFenceBlockBatch,
         DatabaseEngine.RemoveEmptyClosingRealms, Transaction.RefreshBetweenCommitSteps, BackpressureIndexRestart
  status: the component publish's acts under the chain lock cannot throw: its handle is rebuilt from PREPARE's coordinates,
          re-checked under the lock taken with an unbounded wait (AP-05), and its acts are field writes and copies. Its Phase A
          (indexes and views, AP-07), under the lock since #1263, allocates and can throw: B+Tree nodes past the publish limit,
          segment growth, a unique collision (#1258). Any throw is the failed publish above; a retained lock (handler path) is released
          once — by the publish on a normal exit, by the abort path (`ReleaseRetainedPublishLocks`) after a throw. The spawn and pending-operation publishes allocate (cluster claim and growth, EntityMap insert, index
          nodes): their back-pressure is waited out, and a failure that is not back-pressure — a page-cache or segment lock
          timeout (5-10 s, a stall rather than pressure), the data file failing to grow — stops the engine as above. A
          lock is rarely held across an allocation (the page-occupancy map's, whose pages stay resident), so a publisher
          waiting out back-pressure seldom makes another one time out; when it does, that one stops the engine. A stopped
          engine's deferred cleanups go on, its checkpoint does not (#1271).
          **RESIDUAL:** a concurrent change between the check and the publish is not seen: two transactions that put one
          unique key both pass, and the later publish stops the engine (#1258). A commit whose claim was re-checked just before
          another commit latched still appends and publishes; its claim precedes the latch. A wave or a hook save already
          writing when the stop latched writes what it took, which may hold a write made after the stop; a cycle whose waves had
          all been written completes, its advance held below the pinned floor (CK-13). A staged
          write's mover whose index entry differs from its HEAD bytes (an in-place write of the same tick, pending the
          fence) is not modelled; the publish's own reconcile shares the assumption. The publish's backstop for a unique
          collision on a Versioned write, `RejectUniqueIndexCollision`, throws `InvalidOperationException` (#1260); the check
          before the append throws the documented type
  rationale: #396's recommendation was to reserve, before the append, every page the publish would consume. The reservation
             would have to size cluster growth, EntityMap splits and index node splits per transaction, and still would not cover
             a lock timeout or an I/O failure; the fail-stop covers every case, and the wait-out keeps the one transient case,
             back-pressure, from reaching it
  on_violation: an appended commit rolled back while recovery will replay it, or a partial publish other records build on
                (TXW-8 class)
  verified: PostAppendFailureTests — AThrowAfterTheAppend_KeepsTheCommit_StopsTheEngine_AndARestartRecoversIt (a throw
            injected after the append: Committed, the engine stopped, a later commit refused, no cycle covered after it, the
            checkpoint below the record, the value back after a disposal or a hard crash), AWriteAfterTheEngineStopped_NeverReachesTheDataFile
            (#1271), AStopLatchedMidCycle_StopsTheCycleBeforeItsNextWave; CommittedDisciplineTests.ACommitThatClaimsItsSlotAfterTheEngineStopped_IsRefused
            (#1269), AFenceWhoseClaimMeetsAStop_AppendsNothing_AndDoesNotThrow, AUniqueKeyAlreadyTaken_IsRejectedBeforeTheAppend
            (a Versioned or SingleVersion spawn over a committed key, two spawns of one key, a Commit-discipline write moved onto
            a held key), AKeyTakenByTheIndexsOwnComparison_IsRejectedBeforeTheAppend (+0/-0 float spawns; a second unique field),
            AStagedWrite_OntoAKeyFreedEarlierInThePublish_Commits (the holder destroyed, or moved away first, in the same commit),
            AStagedWrite_OntoAKeyMovedAwayLaterInThePublish_IsRejectedBeforeTheAppend,
            ASpawnWithoutItsUniqueIndexedComponent_IsRejectedBeforeTheAppend, ASpatialSpawnWithoutItsSpatialComponent_IsRejectedBeforeTheAppend,
            BackpressureDuringTheSpawnPublish_IsWaitedOut_AndTheCommitCompletes (the publishing thread alone held under
            back-pressure until its publish outlasts the allocation timeout: every one of 3 000 spawns visible and complete),
            BackpressureOutlastingThePublishLimit_StopsTheEngine_AndARestartRecoversTheSpawns (the partial spawn publish comes
            back whole), AnEngineThatStopsDuringACommitsPrepare_RefusesItsAppend, AStoppedEnginesTickFence_AppendsNothing,
            AStoppedEnginesParallelFence_AppendsNothing (the runtime's default fence; without the stop it logs the pending
            write), AStagedWrite_OntoTheKeyItsEntityHolds_Commits (-0f to +0f), AThrowUnderAHandlerCommitsChainLock_ReleasesItOnce_AndSurfacesAsIncomplete
            (a throw under a retained chain lock, after the append), and a String64 key among the comparison shapes
            [VerifiesRule]; mutant APublishThatGivesUpOnBackpressure_StopsTheEngine (`PublishGivesUpOnBackpressureForTest`)
            [RuleMutant]. Real mutations, each failing its test: no catch after the append, the floor not pinned, a stopped
            engine closing clean, the unique pre-check removed, an unset unique component let through, the spatial pre-check
            removed, keys freed earlier in the publish or by the commit's destroy not accepted, byte equality instead of the
            index's, the parallel fence not gated (it then meets `DurabilityLog`'s refusal and the runtime stalls), the
            own-entity holder not accepted, the publish not waiting out, the wait-out ignoring its limit, a retained lock
            released by the publish on a throw too. Not covered by a test: the latch-then-Committed-then-log order (a throwing
            logger), the locks released before the durability wait, the publish bound starting at the append, the commit's
            re-check before the append and the serial fence's own gate (each redundant with `DurabilityLog`'s refusal, which
            throws the same), the serial fence's maintenance after a stop

### AP-04: Every byte a publish writes is owed to the disk `[fatal]` `[silent]`
  invariant ∀ page p a commit writes through its cluster accessor — in PUBLISH the Versioned HEAD copy into the cluster slot and a
            Commit-discipline staged value, in PREPARE an AllowMultiple element id: after the write, p owes a writeback
            (WritebackGen ≠ CapturedGen) until a durable write discharges it
  invariant the commit's cluster accessor carries the transaction's ChangeSet, like its index accessors
  note a dirty write through an accessor WITHOUT a ChangeSet owes nothing, and that is by design for the in-place SingleVersion writes made
       through the transaction's own cluster accessor: their durability is the tick fence's (cluster-page-durability.md). Making every such
       write owe its page at write time was tried and rejected — the page is then collected while the writer still holds it, and checkpoint
       cycles gate on it (CommittedDisciplineRecoveryTests turned red). The publish is different: it runs after the append, as the last act
       of a commit nothing else will make durable
  rationale: the cluster accessor had no ChangeSet, so marking a slot dirty only toggled ActiveChunkWriters. The page owed nothing: no
             checkpoint and no close wrote it, the cache could evict it and reload the old bytes, and the fence does not help — it
             stopped emitting Versioned slots in #559. A clean reopen then trusted the stale HEAD (CS-03): Path-B scans and bulk
             iteration returned the old value while point reads, which walk the chain, returned the new one (#1159)
  scope: Transaction.EnsureClusterCommitAccessors, Transaction.PublishClusterVersionedSlot, Transaction.PublishStagedEntry
  on_violation: a committed update vanishes from the cluster slot across a clean reopen or an eviction — silently, while the revision
                chain and the index keep the new value
  verified: VersionedPublishDurabilityTests.AVersionedUpdate_OnACleanPage_IsWrittenAndSurvivesACleanReopen [VerifiesRule];
            mutant VersionedPublishDurabilityTests.APublishThroughAnAccessorWithoutAChangeSet_LeavesThePageClean [RuleMutant]

### AP-05: A publish stamps the entry it prepared, wherever it now is `[fatal]` `[silent]`
  invariant the publication acts on a revision chain — TSN stamp + IsolationFlag clear, LastCommitRevisionIndex, CommitSequence —
            run under the chain's exclusive lock: the one PREPARE retained (conflict-handler path), else one PUBLISH takes with an
            unbounded wait (so AP-03 still holds — no bounded timeout, nothing to throw)
  invariant under a lock retained from PREPARE no compaction can have run, and the recorded coordinates are exact. Otherwise they
            are used only while they still name this transaction's pending entry (isolated, and its content chunk — or, for a
            delete, no chunk and its TSN); else the entry is found again by that identity, under the same lock. A root-chunk slot
            can be checked in place: a compaction rewrites the root chunk whole and zeroes what it vacates. An overflow chunk's
            coordinates are never trusted: compaction may have freed and reissued the chunk with a stale copy of the entry in it
  invariant an entry that cannot be found is not published at all — no stamp, no LCRI, no CommitSequence, no cluster copy —
            and the loss is logged (DatabaseEngine.LogPublishEntryNotFound). Compaction keeps isolated entries, so only an unlocked
            writer elsewhere can cause it (#1161); stamping the recorded slot anyway would publish another transaction's entry
  rationale: without a handler PREPARE holds no chain lock, and a deferred cleanup on another thread could compact the chain
             between PREPARE and PUBLISH — CleanUpUnusedEntriesCore rewrites every kept entry from index 0, the pending one
             included. The publish stamped the recorded position: it committed whatever had moved there (often the previous
             revision, re-stamped with the new TSN) and left its own entry isolated forever. The commit returned success and no
             reader ever saw it; under last-writer-wins the next read-modify-write built on the old value. Measured before the fix:
             ~1 read in 200 stale at 4 threads. A stamp landing in a reissued overflow chunk that had become another chain's root
             overwrote that chain's header (its indexes and item count), seen as lock timeouts on that chain (#1158)
  scope: Transaction.PublishComponent, Transaction.IsThisTransactionsPendingEntry, ComponentRevisionManager.CleanUpUnusedEntriesCore,
         ComponentRevisionManager.FindRevisionIndexByChunkId
  on_violation: a committed revision is invisible to every later reader, silently — lost updates, and corrupted neighbouring
                chain state when the stale position belongs to a reissued chunk
  verified: VersionedPublishCompactionRaceTests: ACompactionBetweenPrepareAndPublish_LeavesTheCommitVisible (overflow chunk),
            ..._InTheRootChunk_LeavesTheCommitVisible, ..._OfADestroy_LeavesTheEntityGone (the race staged on one thread through
            DatabaseEngine.PublishComponentProbe; each also checks no entry stays isolated, LCRI and CommitSequence) [VerifiesRule];
            mutant APublishThatTrustsItsPrepareCoordinates_LosesTheCommit, root and overflow [RuleMutant];
            SerializedReadModifyWrites_AcrossThreads_AlwaysSeeThePreviousCommit (the shape found)

### AP-06: The cluster slot holds the newest committed revision `[fatal]` `[silent]`
  invariant a publish copies its value into the entity's cluster slot only under the chain's exclusive lock (AP-05), and only when
            its revision is the newest committed one: its stamp above every other published revision's (VIS-05). Over a tombstone it
            copies nothing: the destroy reads the keys to remove from the slot, and an update prepared before the destroy and stamped
            after it copied keys the index never held — the destroy removed those, and its own entries stayed (#1370)
  invariant so two publishes of one entity never copy concurrently, and the slot never moves back to an older revision
  invariant the entity's index entries follow the same revision: a publish's index and view reconcile (Phase A) runs under the chain's
            exclusive lock, before its stamp, and moves the entries from the newest published revision's value — not the one its transaction
            read — to its own, only when its stamp is above that revision's; over a tombstone it moves nothing, the destroy owning the entries
            (Transaction.PublishComponent, ComponentRevisionManager.NewestPublished). A write that left every indexed field as it read them still
            moves them when another commit landed in between (PublishEntry.SlotIndexed)
  invariant PREPARE measures the write against the newest committed revision it sees (PublishEntry.BaseCompChunkId, under the chain's
            shared lock, or its exclusive one on the handler path), not the read one: whether it owes a move, and the unique-key check
            before the append (Transaction.CheckVersionedUniqueKeys), see the key the entity holds now. A write back onto a key another
            entity took since the read is rejected before the append; against the read, nothing checked it and the publish's backstop
            stopped the engine. A commit landing between that check and the publish is not seen there (#1258's shape)
  requires IXS-10: the reconcile now runs under the chain's exclusive lock, so no index reader may wait on that lock while holding a
            value buffer the reconcile takes
  rationale: two transactions may update one entity concurrently — without a handler each prepares its own revision — and their
             appends need not finish in revision order. The copy ran after the lock was released, unconditionally: the older
             revision publishing last overwrote the newer, so point reads, which walk the chain, saw the newer value while bulk
             iteration and Path-B scans, which read the slot, saw the older; a clean close then persisted the slot (CS-03). The index went
             the same way from the other side (#1263): each publish moved the entries from its READ value, the first moved them off it, and
             the second found nothing to move — the entity read the second writer's value under the first writer's unique key, unreachable
             by its own, with the first writer's key held by an entity that no longer had it
  scope: Transaction.PublishComponent, Transaction.PublishClusterVersionedSlot, Transaction.PublishClusterIndexes,
    ComponentRevisionManager.NewestPublished, ComponentRevisionManager.NewestPublishedUnlocked, Transaction.PrepareClusterVersionedSlot,
    Transaction.CheckVersionedUniqueKeys
  on_violation: the cluster slot silently disagrees with the chain's HEAD until the entity's next commit; an index entry names a value the
    entity no longer has, and a query by its value misses it
  verified: VersionedPublishOrderTests.TwoUpdatesPublishedOutOfOrder_LeaveTheNewerValueInTheClusterSlot (the older publish held
            after its append while the newer one commits) [VerifiesRule]; LastWriterWinsIndexTests [VerifiesRule] — two updates from one read
            leave the unique and the multi-value index on the last writer's key alone; a write that keeps its read key moves the entry back
            from an intervening commit; an older update published last moves nothing; an update prepared before a destroy and
            published between its tombstone and its index removal leaves no entry (mutant copying over the tombstone: red).
            Mutations killed: the move from the read value, the intervening commit ignored, the older publish moving; a write back onto a
            key taken since its read is rejected before the append, the engine going on (mutation killed: PREPARE measuring against the
            read); a commit landing between PREPARE and the append is moved back from (mutation killed: the publish's intervening-commit
            term dropped)

### AP-07: An abort before the append leaves no trace `[fatal]` `[silent]`
  invariant PREPARE changes nothing another transaction reads or builds on beyond the transaction's own pending effects, each
            flagged with its owner and undone by the rollback: its revisions, the spawns it places (VIS-08), its pending died
            marks (VIS-09) and its pending enabled-bit changes (VIS-10) — `UnplaceSpawns`, `UnmarkDestroysPending`,
            `UnmarkEnableDisablePending`, run by `RollbackAfterFailedCommit` while `_prepareStaged`. A Versioned commit's
            index and view reconcile (Phase A) runs in its publish (`PublishClusterIndexes`), not in PREPARE.
            PREPARE only decides it is owed (`IndexPending`: an index covers the slot and the write inserts, deletes or changes
            an indexed field). It runs in `PublishComponent`, under the chain lock and before the stamp, so the index never
            lags a visible revision, and after AP-05's vanished-entry check, so no index moves for an unstamped revision.
            Readers of the entity wait out the index update: before the lock, its move started from the value its transaction
            read, which a concurrent last-writer-wins commit had already moved off (#1263, AP-06). A destroy's index removal
            and deletion deltas are in the publish already (`FlushPendingDestroys`)
  invariant a commit that fails before its append, once PREPARE has begun on its entries (after `PrepareEcsDestroys`), ends the
            transaction: it releases its retained chain locks, then rolls back without the unit of work's deadline
            (`RollbackAfterFailedCommit`), and `Commit` again returns false — even if that rollback failed
            (`_prepareConsumed`). A failure before that point — the chain's tail check, a destroy's tombstone — leaves the
            transaction open, and a retried `Commit` is safe
  never move an index entry or append a view delta before the append: the rollback reverts the pending effects only, so the index keeps a
        key no commit made and loses the committed one, and a view refreshed after the abort applies a delta no commit made.
        Undone after the fact, another transaction could take the freed key in between, and the inverse move would collide or
        overwrite it
  never retry PREPARE on entries a failed attempt consumed: it resets each entry's read revision (`PrevCompContentChunkId = -1`),
        so a second pass reads chunk -1 as the old value
  invariant a conflict-handler commit takes the chain locks it retains in one global order: component type id
            (`Transaction.PrepareOrder`), then primary key (`ComponentInfo.ForEachMutableEntryOrdered`). Taken in write order, two
            commits writing the same entities in opposite orders each held one and waited out the other's for
            RevisionChainLockTimeout, and every reader of those chains with them (#1321)
  never roll back while holding the retained chain locks: the rollback frees a spawn's chain, lock word included, and voids
        with no deadline on chains another aborting conflict-handler commit may hold. Two that held their chains in crossed
        order would wait for each other for good; the global order above keeps that from arising, this is the second line
  scope: Transaction.PrepareClusterVersionedSlot, Transaction.AnyIndexedFieldChanged, Transaction.PublishClusterIndexes,
         Transaction.PrepareOrder, ComponentInfo.ForEachMutableEntryOrdered,
         Transaction.PublishComponent, Transaction.PublishPreparedComponents, Transaction.Commit,
         Transaction.RollbackAfterFailedCommit, Transaction.CheckVersionedUniqueKeys, ArchetypeClusterState.IndexedSlots,
         Transaction.UnplaceSpawns, Transaction.UnmarkDestroysPending, Transaction.UnmarkEnableDisablePending
  rationale: #1262. A Versioned commit maintained its indexes in PREPARE, so that a unique collision there could throw before
             the append (AP-03). That made an abort before the append — a lock timeout, back-pressure on the claim, AP-03's own
             pre-append rejections — leave the index moved and the views notified. Since #396 the pre-append check rejects such
             commits routinely, so it was no longer an edge case. The unique check now moves with Phase A into
             `ValidatePublishBeforeAppend`, in publish order: the Versioned writes first, then spawns, destroys and staged writes.
             The publish drain runs in batch mode, as PREPARE's pass did, and the unchanged-field guard stays in PREPARE, so
             a write that moves no key costs the publish nothing — unless another commit landed after its read (AP-06)
  status: **RESIDUAL:**
          - a Versioned unique key taken by two concurrent commits now passes the check in both, and the later publish stops the
            engine (#1258, which this widens: in PREPARE the loser failed clean);
          - a last-writer-wins commit landing between PREPARE's unique-key check and the publish can make the publish move a key
            back onto one another entity took in between: the backstop stops the engine, as #1258;
          - a write over an entity another commit destroyed is judged before the append against its read key, while its publish moves
            nothing: it can be refused a key it would never have taken;
          - a changed Versioned unique key costs two descents, the check and the publish's backstop
  on_violation: the index loses an entity's committed key and maps a key no commit made — it is unreachable by its value — and
                incremental views report removals and moves that never happened; or two aborting commits deadlock
  verified: AbortBeforeAppendTests — AnAbortedKeyMove_LeavesTheIndexAndTheView (a unique key moved, the commit rejected after
            PREPARE: the index keeps the committed key, a refreshed view keeps the entity, the same change then commits),
            AnAbortedWrite_LeavesNoPhantomInAView (an AllowMultiple field: no phantom, then the same write committed and the
            entity destroyed), AVersionedWriteOntoAHeldKey_IsRejectedBeforeTheAppend,
            AnAbortedHandlerCommit_ReleasesItsChainLocksBeforeRollingBack, ConcurrencyConflictTests.OppositeOrderHandlerCommits_DoNotDeadlock
            (each commit held at its first chain lock until the other has its own: in write order both get there),
            AnAbortedDestroy_LeavesTheIndexAndTheView (a guard: the destroy's index work was already in the publish); each
            also checks the failed commit ended the transaction [VerifiesRule]; mutants
            APrepareThatReconcilesIndexes_LeavesTheAbortedKey and APrepareThatAppendsViewDeltas_LeavesAPhantom
            (`PrepareReconcilesIndexesForTest`) [RuleMutant]. Real mutations: the terminal rollback removed fails the abort
            tests; the write-order PREPARE times the opposite-order test out. Not covered by a test: a rollback that throws
            (`_prepareConsumed` then refuses the retry), and the locks released after the rollback (no API reaches crossed holds
            under the global order)

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
  invariant ∀ base B, window W, prefix P ⊆ W: Apply(Apply(B,P), W) ≡ Apply(B, W) — spawn re-applied over a present entity,
            destroy-if-present (a spawn and its destroy in one window included), absolute EnabledBits, value overwrite, collections
            folded-then-Set
  invariant the base B is any subset of a transaction's pages as of after it, the rest as of before (a fuzzy checkpoint's capture):
            a present entity's spawn is re-applied on fresh chain roots, not taken as applied; an update's replayed element wins the
            scrub over the on-disk element of the same TSN; no chunk an on-disk chain of a live record names is handed out by the replay;
            the scrub's roots are the live records' — and, for an archetype the file holds that the open did not load, the chunks with a
            root's header the scan finds; a named chunk is a root only when a live entity's EntityPK adopts it, and no scrub frees a live
            root as an overflow chunk; a live chain whose links end before its length (its root's page after a growth, the linking chunk's
            before), or link to a chunk the disk has as free or to another live record's root, is collapsed to its newest reachable committed
            revision before the replay, so the replay appends onto a chain it can walk (#1251, #1297). A compaction writes the root alone and
            allocates nothing (REAP-02, #1298): it used to move the newest revisions of a chain it compacted after the barrier into fresh chunks,
            unlogged, and a crash that wrote the root and not them had the collapse keep an older value
  scope: RecoveryApplier.ApplySpawnedEntity / ApplyDestroyToExisting / ApplySetEnabledBitsToExisting / ApplyCollectionFolds
         VariableSizedBufferSegmentBase.SetElementsRaw (the "Set" the collections clause names)
         RecoveryApplier.ReapplySpawnedEntityToCluster, ComponentRevisionManager.ScrubChainToHead / FreeDroppedContent /
         ReserveChainChunksAfterCrash / EnumerateVersionedChainHeads / CollapseTornChainsBeforeReplay, DatabaseEngine.CollectLiveChainRoots
  ✅ collections clause BUILT (2026-08-11, #389). Was UNBUILT on both sides: AddCollectionDelta had no production caller
     and the driver deferred CollectionDelta apply. Now the emitter logs a collection's full content behind every Slot
     record (Clear + Append x N) and the driver folds it per (EntityId, slot, FieldId), flushing via SetElementsRaw AFTER
     the entity's Slot apply. Idempotence holds by construction: Set allocates a fresh buffer and releases the previous
     one, so a re-applied window converges in content and refcount — only the buffer id differs, which AP-13 permits.
  🔴 FLUSH ORDER IS LOAD-BEARING and the design does not state it: FoldFlush must run AFTER the Slot apply for the same
     entity. A Slot apply overwrites the whole component value, so an earlier flush is clobbered; and because LOG-06
     zeroes the handle in every Slot payload, the apply leaves the row pointing at NO buffer — the flush is what gives
     it one back. A Slot record applied with no fold behind it empties the collection.
  🔴 A NULL HANDLE EMITS ITS CLEAR TOO (#1362). The fold spans the window, so a Slot whose collection was cleared, logged
     with nothing behind it, left an earlier record's Clear + Append x N in the fold — and the flush gave the cleared
     collection its old content back (CollectionDurabilityTests.ReplacedAndClearedCollections_SurviveAHardCrash, red
     when CollectionContentEmitter.Emit skips a zero handle).
  🔴 TEST-TAG WARNING (2026-07-28, still true for the four pre-existing tags): four tests carry `[VerifiesRule("AP-12")]`
     and NONE of them re-applies anything — they are single-pass crash → reopen → assert. Do not copy their shape.
     CollectionDurabilityTests.ReapplyingTheWindow_ConvergesInContentAndRefcount does re-apply: it replays
     RecoveryDriver.Run over a SNAPSHOT of the segments taken at crash time, because CK-04 recycles the live WAL once
     the first recovery seals — without the snapshot a "re-run" silently replays nothing and passes vacuously.
  spec: rules/tla/CommitRecovery.tla (S2) — AP12_ApplyIdempotent (where idempotence is also proven)
  on_violation: a crash during recovery corrupts on re-run
  verified: CollectionDurabilityTests.ReapplyingTheWindow_ConvergesInContentAndRefcount (collections clause),
    VsbsSetElementsTests.Set_AppliedTwice_ConvergesInContentAndRefcount (the Set primitive itself),
    UnsuppliedComponentPayloadTests.Recovery_SetEnabledBits_ReapplyHealsASoACopyThePriorPassLeftStale (absolute EnabledBits
    clause: a re-run over a record already holding the mask still rewrites the cluster SoA copy, #847)

### AP-13: Allocation tolerance
  invariant physical placement chosen at apply may differ from pre-crash; all references (EntityMap, cluster bookkeeping) are
            updated through the same path; orphans are swept in Phase 4
  scope: RecoveryDriver.cs Phase 3–4
  note a recovered ComponentCollection's bufferId is a fresh allocation and will NOT equal the pre-crash one. That is this
       rule, not a defect — the oracle masks collection-handle bytes and compares the ELEMENTS instead
       (RecoveryShadowModel.MaskCollectionHandles). A verifier that byte-compared the handle would fail on correct recovery.

### DC-01: No dangling durable reference `[fatal]` `[silent]`
  invariant ∀ record R in the log, ∀ value X referenced by R via handle | id | index | cursor:
            X is reconstructible from the log alone
            OR X is durable strictly before R becomes durable
  scope: RecordCodec.ZeroHandleRanges, RecordCodec.PackColumnHandleRange (the codec's handle-zeroing paths);
         ComponentTable.CollectionHandleRanges; CollectionContentEmitter.Emit; RecoveryApplier.ApplyCollectionFolds;
         RecoveryDriver.Run (the allocator watermarks it restores)
  on_violation: dangling durable reference — silent data loss, escalating to cross-entity data disclosure when the
                referent's allocator re-issues the reference (#389, #697)
  rationale a reference and its referent on different durability timelines cannot both survive an arbitrary crash point.
            Either the log can rebuild the referent, or the referent must already be on disk when the reference lands.
            Derived from three defects with one shape: #389 (a _bufferId in a Slot payload vs VSBS contents on the
            checkpoint timeline), #697 (a recovered EntityId vs the entity-key high-water mark), #389b/RB-06 (a recovered
            buffer handle vs the VSBS free-list cursor). The 104 pre-existing rules describe MECHANISMS — how the WAL
            frames, how the checkpoint pairs slots — and none describes COMPOSITION between subsystems, which is exactly
            where all three live.
  note satisfiable two ways, and both already exist in the engine: (a) log-reconstructible — a revision chain's
       ComponentChunkId is safe precisely because the chunk's content IS the WAL payload, which is why chains were never
       on the bug list; (b) durable-first — checkpoint ordering for protected pages (CK-05 A/B slot pairing). #389 chose
       (a): the handle is zeroed out of the log and the collection's full content is emitted behind the Slot record, so
       replay rebuilds the buffer and re-points the handle.
  note OUT OF SCOPE, both real and tracked separately: (1) the VSBS free-list is not restored at recovery (RB-06) — it
       restarts empty and re-issues handles live recovered entities still hold; (2) schema-catalog collections stay
       checkpoint-only by design, because SaveInSystemSchema / PersistSchemaChanges / PersistNewArchetypes construct
       their accessors OUTSIDE any transaction and so have no commit to ride. The second is a decision, not an oversight;
       it is stated here so the next reader does not re-file it.
  verified: CollectionDurabilityTests.Commit_WithCollections_PutsNoBufferIdOnTheWire [VerifiesRule],
    Log06Verifier_RejectsABufferIdOnTheWire [RuleMutant], Collections_SurviveAHardCrash,
    AxisArchetypesTests.EveryDurableCollectionCell_KeepsItsElementsAcrossAReopen,
    DifferentialRecoveryOracleTests.PayloadAxes_SurviveACrash

---

## Module: CHAIN — revision-chain mutation

Every mutation a transaction or the revision cleanup makes to a live revision chain — an append, a publish, a compaction,
a void — runs under the chain's exclusive lock. A transaction's own entry is found again by its identity under that lock,
never by a position recorded before it: a compaction moves every kept entry toward the root, and other transactions
append after it.

### CHAIN-01: A transaction voids its own entry, found by identity under the chain's lock, and only it `[fatal]` `[silent]`
  invariant a rollback's void, and a handler conflict's void of its orphan, run under the chain's exclusive lock, on
            the transaction's own pending entry: isolated, naming its content chunk — or, for a delete, no chunk and its TSN
            (`FindOwnPendingEntry`)
  invariant a void leaves every other entry in the chain: the item count drops only when the voided entry is the chain's last
            (`VoidEntry`); a void anywhere else is a hole. Readers skip a hole, and the compaction drops it wherever it is:
            it counts for nothing — not the guard that keeps the last entry, not the sentinel's decision — and when only
            holes follow the sentinel, the sentinel is kept as the chain's newest
            revision; a chain of nothing but holes keeps one, as the recovery scrub leaves a chain with no commit
  invariant a rollback that finds no pending entry of its transaction voids nothing and frees nothing: the entry its
            `CurCompContentChunkId` names, if any, is committed (#1252)
  invariant a conflict's orphan is void before the conflict handler runs: a handler that throws aborts the commit, whose
            rollback finds only the commit's new entry
  invariant a conflict's first entry is void as soon as its replacement is appended, before anything that can throw: the
            rollback of an aborted commit finds only the replacement
  invariant a last-writer-wins conflict (no handler) voids nothing and appends nothing: its entry publishes in place with its
            commit stamp, the newest by stamp (VIS-05) — drawn after the commit it lost to published
  enforce the rollback waits for the lock without a deadline (it has nothing to fail into; AP-05's argument for why the
          wait ends), or voids under the lock when its thread already holds the chain (a conflict handler disposing another
          transaction of the same entity); a handler commit's PREPARE, a fallible step, waits with the chain-lock timeout
          composed with the unit of work's deadline. The rollback's wait runs in slices (DatabaseEngine.RollbackVoidReportInterval,
          30 s), each one that runs out reported with the lock's holder (`EnterChainLockForRollback`): the one wait that does
          not end is on a conflict handler that waits on another transaction of the same entity while its commit holds the
          lock, which handler code must not do (ConcurrencyConflictSolver's contract, #1273)
  never void by a position recorded before the lock: a compaction in between puts another revision there — the commit
        appended after this one moves into its slot
  never drop the item count for a void that is not the chain's last entry: the count then ends the chain before its last
        entry, another transaction's revision appended after this one
  scope: ComponentRevisionManager.VoidOwnPendingEntry, ComponentRevisionManager.FindOwnPendingEntry,
         ComponentRevisionManager.EnterChainLockForRollback, DatabaseEngine.NoteRollbackVoidWaiting,
         ComponentRevisionManager.VoidEntry, ComponentRevisionManager.CleanUpUnusedEntriesCore, Transaction.RollbackComponent,
         Transaction.DetectAndResolveConflict, Transaction.ResolveConflictUnderLock
  rationale: without a conflict handler, concurrent writers of one entity are allowed (last writer wins), so a transaction's
             pending entry can have a newer commit after it. The rollback voided its entry through a handle located without
             the lock, then ran `--ItemCount`: with no compaction at all, the decrement cut the newer commit out of the chain;
             with a compaction between the lookup and the void, the void zeroed the newer commit itself. The last-writer-wins
             conflict path (before option C, which removed it) voided, and freed the content chunk of, its orphan's recorded
             slot the same way. A first write that failed
             before adding its entry left the rollback holding the committed revision's chunk id, which the lookup by chunk
             alone then voided and freed (#1252). The compaction treated a void as an entry: a hole left last (two writers
             rolled back in reverse order) ended its skip and freed the sentinel, the chain's last commit
  on_violation: a committed update vanishes — a fresh reader reads the previous value, or zeros — and the rolled-back entry can
                stay isolated for good, so the chain never shrinks (#1233)
  verified: VersionedVoidRaceTests.ARollback_AfterANewerCommit_KeepsThatCommit (no compaction),
            ACompactionBeforeARollbacksVoid_LeavesTheNewerCommit (the compaction staged through DatabaseEngine.ChainVoidProbe;
            it also checks no entry stays isolated),
            TwoRollbacksInReverseOrder_ThenACompaction_KeepTheCommittedRevision, ACompactionOverAHole_LeavesTheLastCommitOnACommit,
            AConflictHandlerThatThrows_LeavesNoOrphan, ARollbackWaitingOnAHeldChainLock_ReportsItsHolder_AndVoidsOnceReleased (#1273)
            [VerifiesRule]; mutant
            ARollbackThatVoidsItsRecordedIndex_LosesTheNewerCommit (`RollbackVoidsRecordedIndexForTest`) [RuleMutant]. Real
            mutations: the count dropped on every void fails the first two; the rollback at its recorded index without the lock fails both rollback tests; the compaction
            keeping voids fails the hole, reverse-rollback and handler tests; the orphan voided after the handler fails the
            handler test. Not covered: the rollback of a delete entry (a destroy's tombstone is added at its commit; a
            commit that throws after PrepareEcsDestroys leaves one to roll back, a stage step 1.4's seams can build)
            and the handler conflict's destroy branch (its tombstone is added after any newer commit, so no conflict is
            detected without a concurrent commit inside that PREPARE)

### CHAIN-02: A transaction's cache entry claims a revision only once the revision exists `[fatal]` `[silent]`
  invariant a first write of a Versioned component marks its cache entry written (`Operations |= Updated`) only after
            `AddCompRev` appended its revision; a destroy marks it deleted only once its tombstone exists — appended, or its
            own revision converted (`MakeOwnRevisionTombstone`)
  invariant an entry a first touch inserted (`GetValueRefOrAddDefault`) is removed when anything throws before the entry is
            complete — resolved, and for a write or a destroy its revision appended: the resolve's walk timing out on the
            chain lock, the append failing. An entry that was already there keeps what it said. (Resolving into a local and
            inserting after, as `ResolveClusterVersionedForDestroy` does, holds this by construction)
  never mark the entry before the append: a throw there — the chain-lock timeout (readers can starve the append, #1253),
        an allocation failure, back-pressure — leaves an entry naming the committed revision as this transaction's own. A
        retried write in the same transaction is then handed the committed revision's chunk and writes into it in place,
        visible to every reader before it commits; a retried destroy skips its tombstone; before CHAIN-01, the rollback
        freed and voided the committed revision
  never leave an inserted entry behind a throw: a default entry names chain 0, and a retried write appends there
  scope: Transaction.EcsVersionedCopyOnWrite, Transaction.MarkComponentDeleted, Transaction.QueryRead,
         ComponentRevisionManager.AddCompRev
  on_violation: an uncommitted write is visible to every snapshot, a rolled-back one stays, a retried destroy does not commit,
                a retried write lands in another chain (#1252)
  verified: FirstWriteFailureTests — AFailedFirstWrite_ThenARetry_WritesItsOwnRevision (a reader holds the chain shared, so
            the first write's append times out past its own walk; the retry is invisible until it commits),
            ADestroyWhoseTombstoneFails_CommitsWhenRetried, AFailedResolve_ThenARetry_WritesItsOwnRevision (the chain held
            exclusive fails the copy-on-write's own walk), AStormOfFailingFirstWrites_LosesNoCommitAndKeepsNoRollback (a 10 µs
            chain-lock timeout and long readers, hundreds of appends retried a run, one transfer in four rolled back, every
            account where the committed transfers put it) [VerifiesRule]; mutant
            AFirstWriteThatClaimsBeforeAppending_LeaksItsRetry (`FirstWriteClaimsBeforeAppendForTest`) [RuleMutant]. Real
            mutations: the write's mark before the append fails the retry test and the storm; the destroy's mark before its
            tombstone fails the destroy test; the inserted entry kept after a throw fails the resolve test. Not covered by a
            test: QueryRead's guard and `MarkComponentDeleted`'s not-cached guards (the destroy's own pre-resolve inserts the
            entry first, so the cached path is the one reached). AFailedFirstWriteThatClaimedTheCommittedRevision_ThenARollback_LeavesIt
            verifies CHAIN-01's backstop: the claim forced, the rollback finds no pending entry of its own and touches nothing

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
  exception (#936): on the crash path only, a segment ROOT with both slots invalid whose first sector — torn-safe,
        like the identity fields — records `PairGeneration` 1 is unloadable, not corrupt: the tear hit the pair's first persist (the twin is
        written from generation 2, and zeroed before generation 1, #1129), so the root never had a durable copy. The segment is replaced and
        refilled from the WAL, as a root never written; its root and twin stay claimed until then (CS-08). A map-extension page, a root at any
        other generation, and every page on the clean path still fail loudly
  invariant a pair's first persist zeroes its twin first, with its own fsync, unless the twin lies past the file's end: a twin page number is
            reused from freed pages, and its disk image — a deleted directory's, at any generation — outranked the new pair's generation 1 at the
            next open, which loaded the deleted segment (#1129)
  invariant every directory page of a segment has a twin — the map page a directory needs only for its terminator, when its
            entries end at a page's end, included — and the open walk registers the current slot of every page in the chain
  scope: META PAIR (C1): `ManagedPagedMMF.PersistMetaNow` (write: alternate slot + gen + CRC + fsync + flip), `LoadMeta` (read:
         both slots, pick highest valid gen), `MapReadOffset` (page 0 → current slot), `IsExternallyPersisted` (meta pair excluded
         from the checkpoint dirty-write). DIRECTORY TWINS (C2): `PersistProtectedPage` (the atomic write protocol under `_pairLock`,
         shared by `WritePagesForCheckpoint` + `SavePages` via the `TryPersistProtectedPage` hook), `GetOrAllocateDirectoryTwin` +
         `LogicalSegment.CreateOrGrow` (stamp `TwinPageIndex` on root + map-ext pages; occupancy directory twins pre-reserved at
         fixed pages 3/7 — v4 genesis — to break the genesis chicken-and-egg), `ResolveDirectoryPairsForLoad` (read: physical
         both-slots walk before every `Load`, registers `_pairState`), `MapReadOffset` (directory page → `_pairState.CurrentSlot`),
         `DeleteSegment` (free every directory page's twin + the map-ext pages AND clear its `_pairState` entry — else the twin
         leaks and a stale pair mis-routes a cold read after the primary is reallocated), `ReleaseUnpublishedPages` (the same for the
         pages a failed grow allocated, PS-11), `TryLoadChunkBasedSegment` (a segment a load abandons — the crash path replacing a torn one —
         drops the pairs its load registered, `DropDirectoryPairs`: else its twins stayed owned for good and a reused primary kept the paired
         routing, #1302). All drop pairs under `_pairLock`, which `PersistProtectedPage` re-checks the pair under, so
         a checkpoint persisting a page being freed neither throws nor re-adds its pair. Twin discovery survives a torn primary
         because the `IsLogicalSegment` flag + `TwinPageIndex` are immutable and in the first 4 KiB sector.
  on_violation: a torn write to the only persisted copy → database unopenable / segment unreadable (STO-4); a leaked/stale pair
                on delete → silent mis-route of a reallocated page (STO-4)
  verified: MetaPairTests (meta) + DirectoryPairTests (directory): AlternatesSlots_GenerationMonotonic,
            TornCurrentSlot_ReopenSelectsSibling, BothSlotsCorrupt_OpenFailsLoudly, MultiExtensionSegment_RoundTripsReopen,
            RootGetsTwin_OccupancyMarkedAndAccounted, DeleteSegment_FreesTwinAndClearsPairState (A1.10),
            ATerminatorOnlyMapPage_IsPaired_AndTheMapPagesPastItReopenFromTheirCurrentSlot (#1206) + falsification;
            TornCaptureRecoveryTests.ATornSegmentReplacedAtACrashOpen_GivesUpItsDirectoryPair [VerifiesRule] (#1302);
            DirPair_AReusedTwinHoldingADeletedSegmentsDirectory_DoesNotWinAtReopen [VerifiesRule] (#1129, red without the zeroing).
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
  note 🔴 VIOLATION FOUND + FIXED 2026-10-07 (#1206, found by #1205): a directory whose entries ended exactly at a page's end got its
        terminator on a fresh map page with no twin, and `ResolveDirectoryPairsForLoad` stopped at the first page without
        one — every later directory page went unregistered and was read from its PRIMARY slot, whatever its current slot.
        A page persisted an even number of times holds its latest bytes in its twin, so the segment reopened short, at an
        earlier terminator. Latent while every grow rewrote every directory page (both slots stayed within one grow of each
        other); exposed when grows started writing only the pages they change: a 200M-entry entity map reopened 800 000
        pages short, caught by the chain↔directory cross-check at load. Fixed: the terminator-only page gets its twin, so
        every page the walk meets is paired.
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
            LastPublishedLsn after capture]` strictly precedes `[data-file fsync]`; `CheckpointLSN` advances only to `barrierLsn`
            (the post-flush `DurableLsn` taken before capture), never beyond
  requires: AP-01, WP-16
  scope: `CheckpointManager.RunCheckpointCycle` — step-1 barrier (`RequestFlush` + `WaitForDurable(LastPublishedLsn)` →
         `barrierLsn`); step-3 flush2 (`RequestFlush` + `WaitForDurable(LastPublishedLsn)`) before each batch's `FlushToDisk` (`SettleBatch`)
  note the target was `LastAppendedLsn` until #937. The published frontier is not a weakening: AP-01 orders a commit's
       page effects strictly after its append returns, so a page captured now can only reflect records already published.
       What it drops is the unreachable part of the old target — LSNs allocated to claims that produced no frame, which
       stalled every cycle on an idle engine for the full CheckpointBarrierTimeoutMs.
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
  invariant a rotation makes the new segment active AND adds the old one to `_sealedSegments` in one critical section, so a
            reader holding the lock sees the old segment as active or as sealed, never as neither (online backup's tail
            snapshot, BKP-04)
  scope: `WalSegmentManager._sealedLock` — guards `RotateSegment` (ActiveSegment assignment + Add), `MarkReclaimable`,
         `SealedSegmentCount`, `GetAllSegmentPaths`, `WalSegmentManager.SnapshotForTail`, reopen-reconcile Add, and the WAL
         archive's reads: `WalSegmentManager.ArchiveSnapshot` (the archiver's step, a few field copies, no I/O under the lock) and
         `WalSegmentManager.BytesHeldBetween` (step 8's backlog sum, BKP-10)
  verified: SegmentSnapshotTests.SnapshotDuringARotation_SeesTheSegmentBeingSealed (a rotation paused between creating and
            publishing the new segment; assigning ActiveSegment outside the lock turns it red)
  on_violation: TXW-7 — `List<T>` corruption under concurrent structural mutation → a fatal latch in a background thread

### CK-08: Flush-only cycles during recovery `[fatal]`
  invariant a flush-only cycle (CheckpointManager.RunFlushOnlyCycle) captures and writes the owed pages — protected directory pages first,
            the WAL flushed through what the copies can reflect (CK-02, CK-16) — and discharges their debt; it never advances CheckpointLSN,
            never flips the meta pair, never recycles segments, never runs the metadata hook, never seals or releases held frees (CK-17)
  invariant it is the ONLY cycle a crash open runs before its seal: CheckpointManager.RecoveryInProgress is set when the manager is created on a
            crash open and again when InitializeArchetypesCore starts (a repeat InitializeArchetypes recovers again), and cleared by SealRecovery and,
            for an open that recovered nothing, at the end of InitializeArchetypes. Every cycle requested meanwhile — the timer's, the dirty-page
            pressure's, a back-pressured allocation's — runs flush-only, its failure classified as a cycle's (CK-06). A suspect page (its checksum
            failed at load) is left owed by the flush-only cycle and by SavePages until the suspect resolution ran (DrainSuspectPages): written,
            its torn bytes were re-checksummed for the next open to serve (#1304); held back after it, the close's flush skipped it under a clean
            marker. A flush-only cycle after a fatal cycle failure writes nothing
  invariant the recovery's passes work in bounded batches (DatabaseEngine.RecoveryBatchSize entities, chains or records) and call
            DatabaseEngine.RecoveryBatchPoint between them: the pass's ChangeSet saved, the thread's epoch pin refreshed so the batch's pages stop
            being epoch-held, and a flush-only cycle once the owed pages pass the dirty-page threshold. The passes: the entity-map rebuild, the
            Versioned HEAD rebuild, the replay, the scrub (and its dropped-content free and orphan sweep), the cluster index rebuild. A pass
            calls it only where it holds no page address across it, and records every page it writes through an accessor without a ChangeSet as
            owed (the HEAD rebuild's clusters): refreshed, an unrecorded page could be evicted unwritten (PS-10). The replay's ChangeSet is saved
            when the replay completes, its marks released when it throws. OPEN: the spatial passes still epoch-tag (#1305); a partial entity map
            on disk loses preserved EnabledBits (#1306)
  invariant a crash open fails once its checkpointer has latched a fatal failure (CK-06): at the next batch point, and at its seal whether latched
            before it or by the seal's own cycle (DurabilityException, TyphonErrorCode.RecoveryCheckpointFailed). It could neither write what the
            recovery owes nor seal it, and an open that succeeded handed the application an engine that never checkpointed again. A seal cycle that
            only times out stays soft: the next open replays the window. The failed open closes as a crash (CS-04): CheckpointLSN has not
            moved, or — a fatal failure after the seal's own advance, in its WAL recycling — moved only over data the cycle had fsynced
  requires: CK-04, AP-12
  scope: CheckpointManager.RunFlushOnlyCycle, CheckpointManager.RunCheckpointCycle, CheckpointManager.RecoveryInProgress,
         DatabaseEngine.RecoveryBatchPoint, DatabaseEngine.SealRecovery, DatabaseEngine.ThrowIfRecoveryCheckpointerFailed, RecoveryApplier.BatchPoint,
         RecoveryDriver.Run
  on_violation: recovery either deadlocks on cache back-pressure (no valve) — the crash open of a 0.4 GiB market over a 64 MiB cache timed out
                with every slot epoch-held (#1183) — or destroys its own re-runnability: a full cycle mid-recovery advances CheckpointLSN over a
                half-recovered file, and a second crash replays nothing over it
  verified: CrashDuringRecoveryTests [VerifiesRule] — a cycle forced inside the replay is flush-only and leaves CheckpointLSN alone (a full one
            advances it); the HEAD rebuild's clusters are owed when the replay starts; a recovery stopped at a batch point of any pass, or two in a
            row, converges on the next open; a fatal cycle failure latched inside the replay fails the open at the next batch point, before the
            index pass, a seal cycle failing fatally fails it too, and a batch point's own flush-only cycle failing fatally fails it at that batch
            point (classified, not thrown raw), each followed by an open that recovers whole; MarketHardeningTests at 64 MiB
            (TYPHON_MKT_STOP_RECOVERY): five recoveries stopped in a row, then coherent, every durable transfer present

### CK-09: Occupancy bitmap is derived — re-derived on crash `[fatal]` `[silent]`
  invariant the occupancy bitmap is a DERIVED structure: on the crash path it is never trusted, but rebuilt WHOLESALE from the
            authoritative page ownership — `owned[p] = 1` iff page `p` belongs to any segment the FILE records, its
            directory-map-extension chain, the reserved root range, the occupancy reserves, or a CK-05 directory twin
            (`DatabaseEngine.BuildOwnedPageBitmap`). "Any segment the file records" is load-bearing and was violated until
            2026-08-11 (#771): the reconstruction enumerated `MMF.RegisteredSegments`, which holds only the segments THIS
            session loaded, and `InitializeArchetypes` loads them by iterating `ArchetypeRegistry.GetAllArchetypes()` — the
            CLR types the CALLER registered. Opening with a subset of the schema is supported (a repair or forensic tool has
            no schema assembly), so ownership was caller-dependent: a plain open-and-close of a healthy database freed 36
            live pages. The persisted `ArchetypeR1` / `ComponentR1` segment pointers are now walked in addition, so `owned`
            is a function of the file alone. A reconstruction that cannot read a pointer it knows exists is PARTIAL and MUST
            NOT be adopted — `RederiveOccupancyOnCrash` refuses rather than freeing pages it merely failed to attribute.
            The heal runs only after an unclean close: it is gated on `CrashRecoveryAtOpen` (WAL segments on disk AND the clean
            flag unset at open, CS-06). It used to be gated on "WAL segments exist on disk", which a clean close does not
            preclude, and its own clean-shutdown guard read the on-disk flag after CS-02 had cleared it, so it never fired.
            The persisted L0 words are overwritten with `owned`
            (`BitmapL3.OverwriteFromDerived`) and the L1/L2 skip summaries recomputed — a full replacement, NOT a read-then-diff,
            so a CRC-torn occupancy page is healed by replacement (the FPI substitute) and any page a torn checkpoint leaked
            (bit set, no claimant) is reclaimed
  timing: runs AFTER the recovery seal checkpoint (`SealRecovery`), because the seal can still grow segments as it flushes
          deferred work (e.g. EntityMap bucket pages) — page ownership is final only afterwards. The corrected bitmap is held
          dirty (DC > 0 ⇒ never evicted stale) and consolidated by the next checkpoint / clean shutdown; a re-crash before that
          simply re-derives (idempotent — `owned` depends only on persisted segment directories)
  scope: `DatabaseEngine.RederiveOccupancyOnCrash` (call site after `SealRecovery`), `BuildOwnedPageBitmap` and its
         persisted walk (`ClaimPersistedSegment`, `ClaimDirectoryTwin`), `ManagedPagedMMF.RederiveOccupancy`,
         `BitmapL3.OverwriteFromDerived` + `RecomputeSummariesFromL0`. Gated on `CrashRecoveryAtOpen`; replaces FPI repair of
         occupancy pages (kills STO-5 / STO-11 class once FPI is retired in D)
  on_violation: a torn / stale occupancy page survives recovery → a clear bit over a live page double-allocates it (data
                corruption), or a stale set bit leaks the page forever
  note (2026-10-05, #850): the occupancy reserves and the CK-05 twins (both walks) are claimed up to the bitmap's extent, not
        the file's page count. Both are bit-set when handed out and written only on first use, so a fresh one legitimately
        sits past the written end of the file; bounding them by it dropped them, and the wholesale adoption wrote them free
        while the metadata still named them. Segment pages, directory-map extensions and the reserved roots stay bounded by
        the file.
  verified: TornOccupancyPage_WithFpiDisabled_RecoversViaRederive (FPI off + torn checkpointed occupancy page ⇒
            `RunStorageIntegrityCheck` reports 0 orphans / 0 phantoms; `LastOpenOccupancyRederiveWordsChanged > 0` genuineness)
            [VerifiesRule]; OwnedBitmapIsIdenticalWithAndWithoutSchema (#771 — the file-only property itself: the derived
            `owned` set is bit-identical with and without the schema registered) [VerifiesRule];
            CrashRederiveKeepsTheUnwrittenReserveAndTwinsOwned (#850 — after a hard crash, the occupancy data reserve and a
            directory twin both past the written end stay allocated through the re-derive; genuineness: the re-derive reclaims
            a leaked page) [VerifiesRule], falsified for each half by Mutant_ARederiveClippedToTheWrittenEndIsCaught [RuleMutant];
            UnwrittenTwinsAreOwnedWithAndWithoutSchema (#850 — the registered and the persisted walk each claim a twin past the
            written end) [VerifiesRule]; RederiveRefusesWhenAPersistedSpiCannotBeAccounted (a partial reconstruction refuses
            and leaves the bitmap untouched); CleanShutdownReopenDoesNotRederive (the heal does not run on the clean path)

### CK-10: A checkpoint persists the per-archetype segment pointers it consolidates `[fatal]` `[silent]`
  invariant a checkpoint that consolidates a cluster / EntityMap / per-archetype-index segment's DATA pages into the data file MUST
            also record that segment's persistent identity (its `RootPageIndex` — the `ClusterSegmentSPI` / `EntityMapSPI` /
            `ClusterIndexSPI` / `ClusterString64IndexSPI` in the durable `ArchetypeR1` table, plus `NextEntityKey`) in the same
            cycle. The SPIs are stable once allocated, so this is idempotent and skips archetypes whose persisted values are
            unchanged — the skip test MUST cover every SPI it is responsible for, so that no pointer's persistence is conditional
            on an unrelated field having changed. Runs at cycle START (before the durability barrier) so the SPI update's WAL
            records and dirty `ArchetypeR1` pages ride THIS cycle's barrier + page flush — the pointer becomes durable together
            with the data it points at
  invariant a component-collection segment's pointer — the bootstrap's stride → root-page map — is durable from the segment's creation: its pages are
            saved, then the map, before any collection data can be written into it, so no checkpoint consolidates a collection the open cannot
            find (#1283)
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
  scope: `CheckpointManager.RunCheckpointCycle` (the `PersistDurableMetadataHook` invocation), `DatabaseEngine.PersistArchetypeState`,
         `DatabaseEngine.GetComponentCollectionSegment`
         (skip-unchanged + cache writeback), `DatabaseEngine.DropLegacyClusterIndexBootstrapKeys` (retires the pre-#661 home),
         wired at `DatabaseEngine` checkpoint-manager construction. **Includes the recovery seal** (`DatabaseEngine.SealRecovery`,
         itself a `ForceCheckpointAndWait`): `_archetypeSpiPersistArmed` is set BEFORE it, in `RunWalV2Recovery`. It used to be set only
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
            every other guard field holds is still written — dropping the index SPIs from the skip test fails it) [VerifiesRule];
            ComponentCollectionCrashTests.ACollectionWhoseSegmentTheCrashedSessionCreated_SurvivesTheCrash [VerifiesRule] — a collection
            checkpointed in the session that created its segment, then a hard crash: before the fix the reopen allocated a fresh segment
            (root 140 for the data's 79) and the collection read back empty, 3 of 3

### CK-11: The page cache is drained on dirty-page pressure, not only on the clock `[fatal]`
  invariant a cycle runs as soon as writeback debt reaches ResourceOptions.CheckpointDirtyPageThresholdPercent of the
            page cache's SLOTS, without waiting for CheckpointIntervalMs
  invariant a pressure-triggered cycle is an ORDINARY cycle: same CK-02 barrier, same CK-03 coverage gate, same
            watermark advance, same CK-04 reclamation. It is not a second, weaker durability path — CK-08 describes the
            only form permitted to do less, and this is not it
  invariant pressure bypasses the "no new durable WAL records" early-out, on the same reasoning as the shutdown flush:
            CreateOrGrow and the other structural-write paths dirty segment pages WITHOUT writing WAL records, so a cache
            filled by structural work alone has durableLsn == checkpointLsn and gating on the log would skip exactly the
            pages causing the pressure
  invariant a poll wake that finds no pressure is NOT a timer tick — the durability cadence stays CheckpointIntervalMs
            however often the loop looks
  invariant threshold 0 disables the trigger, leaving timer + explicit force as the only causes
  note: WritebackDebtPercent reads the count of set debt bits (PS-16, #1127), not a scan of every slot. At quiescence it is an upper
        bound — a bit can outlive its debt until the next collect clears it, so pressure may start a cycle early. In flight it can trail
        for an instant (a writer between its generation bump and its bit is not counted yet), which a 250 ms poll does not notice
  scope: CheckpointManager.CheckpointLoop, CheckpointManager.WaitMs, CheckpointManager.IsDirtyPagePressureReached,
         PagedMMF.WritebackDebtPercent, ResourceOptions.CheckpointDirtyPageThresholdPercent
  verified: CheckpointManagerTests.DirtyPagePressure_TriggersCheckpoint (interval = int.MaxValue, so a cycle at all
            proves the trigger fired), CheckpointManagerTests.TriggerDisabled_LeavesCacheToTheTimer (the negative arm
            that makes the first one evidence), CheckpointManagerTests.PressureCycle_AdvancesTheWatermark
  on_violation: a page is reclaimable only once a checkpoint has written it (PS-10), so the cache's reclaim rate IS the
                checkpoint rate. Without this trigger the peak debt is "everything dirtied inside one interval"; when
                that exceeds the cache the next allocation has nothing to evict and the engine dies on
                PageCacheBackpressureTimeout with a ~100%-dirty cache. Measured (#830): 32 758 of 32 768 pages owed
  requires: PS-10 — the trigger exists because debt is only discharged by a durable write
  rationale: NEW 2026-08-16 (#830). The overview's trigger list already claimed a dirty-page threshold
    (`claude/overview/06-durability.md`) and the code had only the timer and ForceCheckpoint; the sole pressure-driven
    path was OnBackpressure → ForceCheckpoint, which fires at 100% saturation and then has 5 s to drain a full cache
    from a standing start. ADR-025 named the consequence — "checkpoint can be expensive if many pages are dirty
    (mitigated by configurable interval)" — but the interval is a TIME knob and the constraint is a CAPACITY one, and
    nothing derives one from the other. It stayed invisible while DirtyCounter leaked (#824): leaked marks made every
    cycle rewrite pages that were already clean, which kept clean pages continuously available by accident.
    Deliberately NOT a second writer thread: WritePagesForCheckpoint claims each page with CAS(ACW, -1, 0) and SKIPS on
    failure, so a concurrent writer holding that sentinel makes the checkpoint skip the page, stillSkipped > 0, the
    CK-03 gate stays shut and no WAL segment is ever recycled — #817's exact failure mode, by design.

### CK-12: A forced wait is released only by a cycle that started after it and covered it `[fatal]` `[silent]`
  invariant ForceCheckpointAndWait returns true ⟹ some cycle c finished, c started after the call's request, and c wrote every page it
            collected (the CK-03 gate opened). So every page the caller dirtied before the call is on the data file, fsynced, and
            CheckpointLSN ≥ every LSN durable at the call, except the records of another thread's commit still between its append and its
            publish, which CK-13 keeps it below. With minCheckpointLsn the wait also requires CheckpointLSN ≥ that LSN (CompleteBulkLoad
            passes its BulkBegin LSN)
  invariant neither a cycle already running at the call, nor a gated or failed cycle, releases it. After either, the wait gives a later cycle
            one retry pause (the CK-11 poll floor, 250 ms) to cover it, then forces another, until its timeout: a page the gate keeps skipping
            must not drive back-to-back cycles, each one a WAL flush
  invariant it returns false at once when checkpointing has halted: a fatal error, a simulated hard crash, the manager shutting down, or its
            loop exiting. Each wakes a sleeping waiter. A shutdown cycle may still cover the request, and a waiter that sees it finish returns
            true (coverage is tested first), but none waits for one
  invariant the wait keys on the number of the first cycle to start after its request, never on a counter read after the force: an idle
            engine finishes the forced cycle before the caller gets to wait, and that cycle must still count
  invariant a force request is never lost: the loop reads and clears it in one exchange, so a request landing between the wake and the read
            runs the next pass. A request posted while a cycle runs is kept (AForcedWait_IsNotReleasedByTheCycleAlreadyRunning); no test
            reaches the window between the wake and the read, which is two adjacent statements
  requires: CK-03 (the gate decides "covered"), CK-01/CK-02 (the covering cycle's barrier, taken after the request, is what puts
            CheckpointLSN at or past every LSN durable at the call), PS-10 (only the covering write discharges a page's debt), CK-13 (which can cap
            that barrier)
  scope: CheckpointManager.ForceCheckpointAndWait, CheckpointManager.ForceCheckpoint, CheckpointManager.RequestCycle,
         CheckpointManager.PublishCycleEnd, CheckpointManager.RunCheckpointCycle, CheckpointManager.CheckpointLoop,
         CheckpointManager.PrepareCrashStop, CheckpointManager.Dispose, BulkLoadSession.CompleteBulkLoad, DatabaseEngine.SealRecovery
  on_violation: the caller proceeds on a checkpoint that never covered its writes. CompleteBulkLoad makes BulkEnd durable over bulk pages that
                reached no disk and have no WAL records (BL-01), so a crash loses them with no error (NEW-CK-2 in the 2026-07-06 assessment).
                Or the wait misses the cycle it forced and times out: measured 7 runs in 8 of a quiescent-engine test (`before=0 after=1`, the
                forced cycle already over when the wait read the count); in production a BulkLoadCheckpointTimeoutException, or a 30 s stall
                in the recovery seal
  verified: CheckpointManagerTests.AForcedWait_CountsItsOwnCycle_EvenIfItEndsFirst (a seam holds the caller until the forced cycle is
            over), AForcedWait_IsNotReleasedByTheCycleAlreadyRunning, AForcedWait_IsNotReleasedByAGatedCycle,
            AForcedWait_AsksAgainAfterAFailedCycle, AForcedWait_ReturnsAtOnceWhenCheckpointingHasHalted, AForcedWait_ReturnsAtOnceAfterACrashStop,
            AForcedWait_ReturnsAtOnceOnACrashMidCycle, AForcedWait_ReturnsWhenTheLoopStops, AForcedWait_ReturnsWhenDisposedUnstarted,
            AForcedWait_PausesBetweenGatedCycles, AForcedWait_WaitsForTheWatermarkItAskedFor.
            Mutants: AWaitThatSamplesAfterItsOwnForce_IsRejected (the replaced pairing); AWaitReleasedByTheNextCycle_IsRejected,
            AWaitReleasedByAGatedCycle_IsRejected, AWaitThatNeverAsksAgain_IsRejected and AWaitThatOutlastsAHaltedCheckpoint_IsRejected
            (reading the count before the force, which fixes the measured race and none of these)
  note: "covered" is the whole-cache CK-03 gate, not "the caller's pages were written": under writers that keep some page live, a forced
        wait can time out although its own pages reached disk (the #817 class). CK-03's planned per-page refinement would let it require
        only pages dirtied before the request

### CK-13: A checkpoint never passes a commit that is between its append and its publish `[fatal]` `[silent]`
  invariant CheckpointLSN < the first LSN appended by any commit still between its WAL append and the end of its publish
  invariant a commit records its first LSN (its floor) inside the append, after claiming it and before publishing the WAL frame, and
            withdraws it once every publish-time page effect is in memory and registered as modified — owed a write, which is what the cycle
            collects (PS-10), whether or not the publish still holds its dirty mark on it (it gives marks back between entries under pressure, #831) —
            or when the commit throws. A commit that
            throws after its append, before its publish completed, first pins its floor on the engine (`PinnedCommitFloor`, AP-03),
            which holds the checkpoint below its records until the engine is reopened
  invariant the cycle reads the lowest floor AFTER its barrier and BEFORE collecting dirty pages. A barrier that covers a record has drained
            its frame (the drain stops at the first unpublished frame, and LSN order is buffer order, WP-06), so the floor, stored before the
            frame was published, is visible; a commit that has withdrawn has already dirtied the pages the collection finds
  invariant the cap never lowers CheckpointLSN: segments at or below it may already be recycled. Cycles are serialized (RunCheckpointCycle
            takes a lock), so a fixture that drives one directly waits for the loop's and the watermark only moves forward. The Math.Max in the
            cap is defence in depth: a stored floor is always above CheckpointLSN, and S3 holds CK13_NeverLowers without it.
            CheckpointManagerTests.InFlightFloor_CapsTheWatermark exercises it with an artificial floor
  invariant any path that appends records and then mutates pages passes a floor to DurabilityLog.Append. Only Transaction.Commit does so
            today: the tick fence writes its pages before it appends, a bulk manifest mutates no page after its own, and PersistArchetypeState
            appends no record
  requires: AP-01 (nothing is published before its records are appended), CK-01/CK-02 (the barrier the floor caps), WP-06 (LSN order is
            buffer order)
  scope: DurabilityLog.Append, Transaction.Commit, Transaction.InFlightLsnFloor, Transaction.ResetCore, TransactionChain.LowestInFlightLsn,
         DatabaseEngine.PinnedCommitFloor, CheckpointManager.InFlightCommitFloor, CheckpointManager.RunCheckpointCycle,
         DatabaseEngine.InitializeCheckpointManager (the wiring)
  on_violation: a commit's publish-time page effects (DiedTSN, EnabledBits, the HEAD copy into the cluster slot, spawn finalize,
                Commit-discipline staged writes) are in no page the cycle writes, yet CheckpointLSN passes their record. Recovery skips
                records at or below it, so a crash loses the committed change with no error. Measured: a Commit-discipline write and a
                Commit-discipline destroy, each held between append and publish while a forced checkpoint ran, were lost after a hard crash
                (CheckpointLSN reached the record's LSN)
  spec: rules/tla/CommittedDiscipline.tla (S3) — CK13_BelowUnpublished, CK13_NeverLowers (an action property) and AckDurable, green with one
        slot and serialized writers and with two slots written concurrently (-twoslots.cfg). Six mutants each violate: the floors ignored
        (-mutant-nofloor, which is NEW-CK-1), the floor stored after the frame is published (-mutant-floorafterframe), withdrawn before the
        publish (-mutant-withdrawearly), read before the barrier (-mutant-readbeforebarrier), read after the capture (-mutant-readaftercapture),
        or the highest floor used instead of the lowest (-mutant-latestfloor, on two slots)
  verified: CommittedDisciplineRecoveryTests.CommitDiscipline_ACheckpointDuringAPublish_KeepsTheCommitInTheRecoveryWindow (Write and Destroy,
            each held right after the append and just before the staged writes' publish: CheckpointLSN stays below the held commit's record, and the
            change survives a crash), with its mutant CommitDiscipline_ACheckpointThatIgnoresTheFloor_IsRejected (the floor unwired);
            CommittedDisciplineRecoveryTests.CommitDiscipline_AnAppendThatFailsAfterItsClaim_WithdrawsItsFloor (the append throws after its claim:
            the floor was stored before the frame's publish and is withdrawn when the commit throws, so the checkpoint reads no floor while the
            failed transaction is still alive);
            CheckpointManagerTests.InFlightFloor_CapsTheWatermark;
            PostAppendFailureTests.AThrowAfterTheAppend_KeepsTheCommit_StopsTheEngine_AndARestartRecoversIt (the pinned floor: a
            checkpoint forced after the failed publish stays below its record; with the pin removed, it passes it)
  note: NEW-CK-1 in the 2026-07-06 assessment. For Versioned commits the CK-03 gate already held the watermark back: in the measured destroy,
        update and spawn every cycle skipped a page a live chunk writer held (skip cause A). TrueCrashE2ETests.ACheckpointDuringAPublish_* guards
        that but cannot fail on this rule. A Commit-discipline write or destroy touches no page before publish (CM-01), so nothing did
  note: S3 could not find NEW-CK-1 before 2026-09-14: its checkpoint advanced CheckpointLSN to the LSN the page memory reflected, not to its
        barrier as the engine does. S1 (CheckpointProtocol.tla) still models the append and the dirtying as one step; the floor does not depend
        on pages, so S3 carries this rule
  note: store-before-publish is pinned by CommitDiscipline_AnAppendThatFailsAfterItsClaim_WithdrawsItsFloor, whose probe sits between the store
        and the frame's publish, and by -mutant-floorafterframe. No test reaches read-after-barrier-before-collect, because the verifiers hold the
        commit across the whole cycle; the S3 mutants cover it. Withdraw-after-the-last-page-effect is pinned by the hold just before the staged
        writes' publish; -mutant-withdrawearly pins only withdraw-after-publish, since S3's publish is one step

### CK-14: A checkpoint never waits for a cache slot only its own writes can free `[fatal]`
  invariant a step of a cycle that loads a page into the cache runs after the cycle has written the pages the cache owes, whenever the cache
            owes enough that no other slot may be free: CheckpointManager.WriteAheadOfTheHook writes them before PersistDurableMetadataHook
            once the writeback debt reaches the dirty-page trigger's threshold, capped at 50 % of the cache
  invariant that early write keeps CK-02 (the WAL is durable through what the copies can reflect before the data fsync) and CP-03 (a page is
            settled only after it), and does not move CheckpointLSN: the hook's records still precede the cycle's own barrier and advance
  invariant the write ahead is not enough under contention, so the hook also never waits (#1382): it runs with PagedMMF.NoBackpressureWaitDepth
            set, a back-pressure round there throws BackpressureCheckpointDeferral, and the cycle completes as a flush-only one (CK-08's mode: it
            writes and settles every owed page, runs no barrier, moves no CheckpointLSN); the next cycle runs the hook again. Once the hook has not
            run for CheckpointManager.HookDeferralWindow (10 s) it must run, and waits for its pages outside its epoch
            (CheckpointManager.RunHookWaitingOutsideItsEpoch): it gives up at a lap's end as a deferral would, waits for a page with its scope unwound,
            and runs again — idempotent — while pages are served (PS-22); past that, the back-pressure timeout is thrown between two runs, never
            after a wait with its pin (a last run that waited as before held the oldest pin for a whole timeout). Waiting inside its allocation,
            the checkpoint thread's pin was the oldest of a 32 MiB cache gone epoch-held (3,841 epochs behind). So CheckpointLSN cannot stall behind a hook never served — a time bound, not a count of cycles, which back-to-back cycles under a storm used up in a second. The
            shutdown cycle never defers. The deferral is taken at a lap's end, so on a cache above the sweep floor the clock is searched whole first A hook given up midway leaves some archetypes updated — records and dirty pages, as a full run's — and
            releases its ChangeSet's marks (PS-05)
  scope: CheckpointManager.RunCheckpointCycleCore, CheckpointManager.WriteAheadOfTheHook, CheckpointManager.WriteDirtyPages,
         CheckpointManager.PersistDurableMetadataHook, DatabaseEngine.PersistArchetypeState, CheckpointManager.RunHookOrDefer,
         PagedMMF.NoBackpressureWaitDepth, BackpressureCheckpointDeferral, CheckpointManager.RunHookWaitingOutsideItsEpoch
  on_violation: the checkpoint is the only thing that turns owed slots into free ones. When a step of its own cycle waits for a free slot while every
                slot is owed, it waits for itself until the back-pressure timeout: the cycle ends Degraded and every writer waiting behind it times out
                too. Measured (#1184): MarketHardeningTests, a 0.76 GiB file over a 256 MiB cache, 16 threads. PersistArchetypeState, which writes
                every cycle while entities are being created, waited in AllocateMemoryPageCore for the archetype table's evicted page (stack captured
                twice); the storm died at 36 639 operations on main
  verified: CheckpointManagerTests.HookLoadingAPage_OnAFullyOwedCache_DoesNotWait [VerifiesRule] — every slot owes a write, the loop is not
            running, and the hook loads a page not in the cache: 56 ms, healthy; with the write ahead disabled the cycle fails after 5 s
  verified: CheckpointHookDeferralTests [VerifiesRule] — back-pressure forced while the hook loads the evicted archetype-table page: the hook gives
            up, the cycle only writes, CheckpointLSN does not move, no page is left marked, well inside the timeout (5 s and a Degraded cycle with
            the no-wait mode removed); the next cycle advances; past its window the hook waits; past its window and served, it waits outside its
            epoch — no round inside its allocation — and completes, advancing CheckpointLSN (mutant: the hook run plainly, waiting with its pin)
  on_violation (#1382): with the write ahead in place, the freed slots went to the 16 writers waiting for them first; the hook still waited out the
                5 s with writeback at zero, in 3 of 3 instrumented market failures (64 MiB, 16 writers), and every writer timed out behind it

### CK-15: A checkpoint pass frees pages while it writes `[correctness]`
  invariant a pass is written in batches — at most WriteBatchMaxPages pages, or WriteBatchMaxTicks (100 ms) of writing — and each batch is settled
            before the next is written: WAL flushed through LastPublishedLsn (CK-02), data fsync, every written page's captured generation published
            (CP-03). So the cache owes less after each batch's fsync, and a writer back-pressure holds waits for one batch, not for the whole pass
  invariant the plain pages of a batch are written in waves of up to WriteWorkers disjoint ranges at once — the first by the checkpoint thread, the
            others by its dedicated wave writers, never the thread pool, whose threads may be the very writers the cache holds back. A failure surfaces
            unwrapped, a fatal one before any transient one, so CK-06 classifies the cycle by the worst fault and never by which writer threw first
  invariant with a PageWriteInterceptor installed (crash simulation) the waves run one after the other on the checkpoint thread, so a simulated crash
            at write k leaves nothing written after it
  scope: CheckpointManager.WriteDirtyPages, CheckpointManager.SettleBatch, CheckpointManager.WriteWave
  on_violation: settled only at its end, a pass under a write storm wrote the whole cache before freeing one page — 8 GiB at ~22k pages/s — and
                every writer waiting for a slot timed out after 5 s with 1,046,917 of 1,048,576 pages owed (MarketHardeningTests, 75M items over an 8 GiB
                cache). Batched, the same storm over a 1.3 GiB cache had the debt at 99.7 % of the cache and its longest wait at 77 ms
  verified: CheckpointBatchSettleTests [VerifiesRule] — the cache's debt falls between successive fsyncs of one forced cycle written in parallel waves
            ([RuleMutant]: one batch for the whole pass keeps it flat); an intercepted pass writes no parallel wave; pages written by parallel waves read
            back, checksums verified, after a reopen

### CK-16: A checkpoint wave writes one kind of page `[fatal]` `[silent]`
  invariant a pass is ordered protected directory pages first, then by file page (PagedMMF.OrderForCheckpointWrite), and each wave writes one kind:
            the protected pages that lead the pass, on the checkpoint thread alone, or plain pages, in parallel. A page whose kind changed after the order
            was taken is left owed for the next pass, which orders it again (CheckpointWriteFilter)
  invariant so a protected page's persist, whose fsync is file-wide, never runs beside or after a plain write its batch has not settled: it can only
            make durable what an earlier batch already flushed the WAL for (CK-02)
  scope: PagedMMF.OrderForCheckpointWrite, PagedMMF.WritePagesForCheckpoint, CheckpointManager.WriteDirtyPages
  on_violation: a commit published after the cycle's barrier reaches the data file while its WAL record is still in the ring buffer — a partial write
                of a transaction that may never become durable, which Typhon cannot undo (#585). Hoisting protected pages inside each write call stopped
                being enough once a pass became many calls on several threads
  verified: CheckpointProtectedPageOrderingTests [VerifiesRule] — a pass is ordered protected first and then by file page, and a plain wave given the
            whole pass leaves every protected page owed and persists none ([RuleMutant]: a wave writing every kind is reported)

### CK-17: A freed chain chunk is reused only once the data file no longer names it `[fatal]` `[silent]`
  invariant a revision or content chunk of a Versioned table, once freed, stays allocated until a checkpoint cycle that started after the free has
            written every page it collected (FreeQuarantine): the page that stopped naming the chunk was dirtied before the free, so that cycle wrote
            it. A crash open holds from the reservation of the on-disk chains on — the replay's, the scrub's and the sweep's frees too — and its seal
            releases them once covered; a seal that does not complete leaves them held. Any other open holds from the end of InitializeArchetypes
  invariant a cycle seals the frees made so far before it collects its dirty pages, and marks them releasable only when it was covered (CK-03); a
            gated cycle leaves them sealed for the next. The release worker frees them — never the checkpoint thread, whose own cycle would wait on
            the dirty pages the frees make — segment by segment, one that throws leaving the others' to run, and counting what it freed before a
            throw. The close releases every held free on the disposing thread before its final cycle, which writes them freed; a close the engine
            treats as a crash releases nothing, and one whose release failed marks the sweep owed (DurabilityWatermarks.MarkSweepOwed, CS-04), so the
            next open recovers, with strict segment loads, and its sweep frees them
  invariant a chunk is held once: a second free of a held chunk, in either generation, is a double free (ChunkBasedSegment.DoubleFreeCount), never a
            second hold. A chunk the release freed in the bitmap and has not yet stopped holding is not handed out: the allocator gives the bit back
            and moves to the next page (ChunkBasedSegment.ClaimOnPage). Recovery's chain scans skip held chunks (ChunkBasedSegment.IsChunkLive,
            which reads the hold before the bitmap: the release frees the bitmap first)
  invariant a content chunk a rolled-back transaction allocated is freed at once: no committed page ever named it
  invariant held frees past PagedMMFOptions.FreeQuarantineCacheFraction of the cache ask once for a checkpoint (CheckpointManager.ForceCheckpoint),
            again only after a release: a trigger, never a cap — and not during a crash open's recovery, whose cycle is its seal: one mid-scrub
            would write a half-scrubbed file. The hold is never persisted: after a crash a held chunk is still named by the disk's stale pages,
            which the reservation takes, or by nothing, which the orphan sweep frees
  invariant a component-collection segment holds its frees the same way (DatabaseEngine.GetComponentCollectionSegment,
            DatabaseEngine.PoolLoadedCollectionSegment): a buffer is named by content chunks the data file's chains name. Freed at once, a
            transaction took it while the disk could still name the revision that dropped it, and a crash's scrub of that revision released it
            under its new owner (#1300). A buffer released after the checkpoint that covered it, its bitmap page not yet written, is allocated
            after a crash and named by nothing, as are the buffers of orphan content chunks the orphan sweep frees: the crash open's collection
            sweep frees them (REAP-02, #1307)
  scope: FreeQuarantine, ChunkBasedSegment.FreeChunk, ChunkBasedSegment.QuarantineFrees, ChunkBasedSegment.ClaimOnPage, ChunkBasedSegment.IsChunkLive,
         PagedMMF.SealQuarantinedFrees, PagedMMF.ReleaseQuarantinedFrees, PagedMMF.NoteQuarantined, PagedMMF.EndFreeQuarantine,
         CheckpointManager.RunCheckpointCycleCore, ComponentTable.QuarantineChainFrees, DatabaseEngine.SealRecovery,
         DatabaseEngine.GetComponentCollectionSegment, DatabaseEngine.PoolLoadedCollectionSegment, DeferredCleanupManager.FreeContentChunk
  on_violation: a chunk free is not logged, and the data file holds each page as of its last capture: a chain's root as of before its compaction named
                an overflow chunk another chain had taken, as of after. The market recovered desk traders holding another trader's record, and credits
                off, in 2 cycles of 10 (#1251)
  verified: FreeQuarantineTests [VerifiesRule] — held until a covered cycle that started after the free, a gated cycle releases nothing, a second
            free is counted once, frees racing seals and releases free every chunk exactly once (without the allocator's skip, 2 runs in 6 leak one),
            the budget asks the checkpoint manager for a cycle, the release worker frees a held chunk after covered cycles (without its signal,
            nothing does); TornCaptureRecoveryTests [VerifiesRule] — an overflow chunk freed by a compaction is
            not reused before a checkpoint (with DatabaseEngine.DisableFreeQuarantineForTest the next growth takes it and the first item recovers an
            old value), recovery's frees are held until its seal covered them, a free held at a clean close is freed in the file, a collection buffer
            a cleanup released is not reused before a checkpoint, in a segment created or loaded by the open

---

---

## Module: CM — Committed durability discipline

Migrated from `design/Durability/MinimalWal/07-rules.md` on 2026-07-28. ADR-057 cites `CM-01..CM-06` as living in this
file; until now the module did not exist here, so an immutable ADR pointed at nothing. Variant A shipped; Variant B was
rejected and is the TLA+ mutant.

### CM-01: No uncommitted staged bytes in page memory `[fatal]`
  never a Commit-discipline transaction writes staged values into cluster/page memory before its Append. A spawn's bytes placed in
        PREPARE (VIS-08) are not staged values: they sit in a slot claimed and not occupied, which no page's occupancy word
        names and the crash open's rebuild from occupancy never reads
  never a staged write published to an entity its own commit destroys: the destroy released the slot first
        (FlushPendingDestroys), and a concurrent spawn may hold it — the staged value overwrote that spawn's component, with no
        error (#1261). The publish skips such entries, as the check before the append does (Transaction.PublishStagedCommitWrites)
  scope: EntityRefMut write path, Transaction staging arena (Variant A), Transaction.PublishStagedCommitWrites
  verified: CommittedDisciplineTests.AStagedWriteToAnEntityItsCommitDestroys_LeavesTheSpawnInItsReleasedSlotAlone [VerifiesRule] —
            the commit held between the destroy's release and the staged publish, another transaction's spawn takes the slot
  on_violation: a fuzzy checkpoint persists uncommitted data with no compensating record; a staged write published over a released slot
                overwrites the component of the spawn that took it, with no error (#1261)
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
  note S3 assumes the runtime DAG serializes same-entity writers (ADR-057). With that guard removed, TLC finds two commits to one slot
       publishing out of LSN order: the page memory keeps the lower-LSN value while last-writer-wins by LSN says the higher one, so what a
       crash recovers depends on where CheckpointLSN landed. Staging (EntityAccessor.WriteEcsComponentData) and publish
       (Transaction.PublishStagedEntry) take no per-entity lock, so two ad-hoc Commit-discipline transactions outside the DAG can do this

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

### CM-07: A Checkpoint archetype's non-Commit spawn emits NO Spawn record `[fatal]` `[silent]`
  invariant ∀ entity e spawned in transaction t into archetype a: t emits a Spawn lifecycle record for e UNLESS
            (a.ClusterDurability = Checkpoint ∧ t.discipline ≠ Commit ∧ a.VersionedSlotCount = 0), in which case it
            emits none. A crash therefore returns e ABSENT, never present-with-default-values.
  invariant the suppression is MONOTONE: Destroy and SetEnabledBits are ALWAYS emitted, for every archetype and every
            discipline. With no Spawn record the replay window can only REMOVE entities, never create one, so no
            interleaving of mixed-discipline transactions can resurrect a destroyed entity.
  never suppressing the Destroy record as well. Safety would require proving e absent from the checkpoint base — a
        decision taken at emit time that races the checkpoint which actually lands (born TSN 100, last checkpoint 50,
        destroy at 150 ⇒ "born after, suppress"; a checkpoint completing at 120 puts e IN the base and the crash
        resurrects it). Not decidable locally, and the error direction is silent.
  never suppressing when the archetype has a Versioned slot. The SingleCache loop emits that component's revision
        content at commit regardless of cluster durability (the chain stays authoritative — no ClusterDurability
        setting may lose a Versioned value), and ApplySlotToExisting NO-OPS for an entity absent from the base map
        rather than failing loudly, so a dropped Spawn would strand the revision silently.
  requires: recovery tolerates every record whose Spawn never arrived, as an explicit no-op and never a loud failure —
            RecoveryApplier.ApplyDestroyToExisting, .ApplySlotToExisting (AP-12 idempotence), .ApplySetEnabledBitsToExisting.
  requires: enumeration never depends on the record — cluster pages are self-describing, the EntityMap is discarded and
            re-derived from the occupancy walk BEFORE WAL apply, and NextEntityKey comes from that same walk (CK-09/RB-06).
  rationale: D5 declares a plain TickFence spawn "checkpoint-durable only — the documented non-guarantee, not a bug".
             Emitting the Spawn while the mode suppresses the fence that would carry the values delivers something
             STRICTLY WORSE than that guarantee: the entity recovers alive with default values. "Checkpoint-durable
             only" means ABSENT when it was not checkpointed, and this rule is what makes the implementation say what
             the contract says. It is not a bandwidth optimisation; the WAL saving is a consequence.
  scope: Transaction.SuppressSpawnRecord, Transaction.BuildCommitBatch
  on_violation: a window-spawned entity recovers as a valueless phantom at the origin of every field — well-formed,
                plausible, and flagged by open-time integrity as DataLoss while the database opens anyway. The
                pre-CM-07 engine returned 32 entities of which 16 were zeroed, measured in both cells of
                CheckpointDurabilityCrashTests.
  verified: WindowSpawn_SurvivesHardCrash_ButWithWhatValues [VerifiesRule] + [RuleMutant]

## Module: WAL Pipeline

The temporal chain that all durability depends on:

```
[page modified in memory] → [WAL record in ring buffer]          (the leading [FPI written] step is removed — FPI retired, increment D)
  → [WAL FUA to disk] → [revision visible] → [page flushed to data file]
```

Any short-circuit — skipping a step, reversing two adjacent steps — breaks
either durability, recoverability, or consistency.

### WP-01: LSN watermark ordering `[fatal]`
  invariant CheckpointLSN ≤ DurableLSN ≤ LastPublishedLsn ≤ LastAppendedLsn (= NextLsn - 1, the allocation frontier)
  scope: WalWriter.cs, CheckpointManager.cs, WalSegmentManager.cs, WalCommitBuffer.Publish
  on_violation:
    CheckpointLSN > DurableLSN → checkpoint flushes pages whose WAL is not yet on disk;
      crash loses data with no recovery path
    DurableLSN > LastPublishedLsn → the watermark names an LSN no frame carries (LOG-05)
    LastPublishedLsn > LastAppendedLsn → logically impossible (a frame published an LSN nobody allocated)
  note the chain gained LastPublishedLsn in #937. The two middle terms are NOT interchangeable, and which one a
    WAIT targets is the whole of WP-16: the gap between them is the set of LSNs that were allocated and will
    never be written, so it is reachable only from below.

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
  note the `<` is load-bearing for online backup (BKP-04): a crash with meta = C keeps exactly the segments with
    `lastLSN ≥ C`, recovery resumes the TSN counter above every commit stamp it scans in them (RB-05), and a backup's
    tail copies that same set so a restore is that crash state
  note the WAL archive's floor (BKP-10, W1′) joins the trim with the same `<`: with archiving configured step 8 and the open's
    reconcile also keep every segment with `lastLSN ≥ archiveFloor` (= the archive's end + 1), unless the backlog policy released
    it. A third floor, never a weaker one: the checkpoint frontier is unchanged, the floors combine by `min`
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
  scope: WalWriter.WriterLoop, WalWriter.DrainRemaining, WalWriter.DrainAndWriteSync — i.e. every PatchChunkCrcs caller;
         WalCommitBuffer.AssertNothingLeftToDrain — the swap-point guard that the buffer reset cannot be reached with a
         published-but-unwritten frame below it
  on_violation: committed records are discarded from the ring buffer while the watermark advances past them; nothing
    downstream can detect the gap because recovery starts after the watermark
  rationale: WP-03 states the ORDERING (write before advance) and LOG-05 bounds what the watermark may CLAIM, but
    neither requires the write to have been attempted. Issue #580 falls through exactly that gap — DrainRemaining lacks
    the WriteInChunks fallback its two sibling drain paths have, so an oversized final batch is dropped on a graceful
    shutdown and the watermark advances anyway. Naming every PatchChunkCrcs caller in scope is what makes the
    asymmetry visible on inspection.

### WP-16: A durability wait targets a PUBLISHED LSN, never an allocated one `[fatal]`
  invariant every `WaitForDurable(lsn)` whose lsn means "everything the WAL holds as of now" passes
            `LastPublishedLsn` — the max over frames that reached `WalCommitBuffer.Publish` — and NEVER
            `LastAppendedLsn` / `NextLsn - 1`
  never a waiter is given an LSN that no frame owns
  rationale: allocation and publication are different events, and the gap between them is not always closed.
    `TryClaim` allocates the byte range and the LSN range in ONE atomic (WP-06), BEFORE it knows whether the
    claim fits the active buffer, so an LSN is consumed on paths that produce no frame at all:
      - the append throws after its claim → `AbandonClaim` publishes a SKIP frame, whose `LastLsn` is 0, so
        draining it advances no watermark;
      - the claim straddles the buffer end and its producer then times out in the back-pressure park → there
        is no frame and no `AbandonClaim` either, and `PerformSwap` folds the consumed offsets into
        `_lsnBase` regardless.
    An interior gap is harmless — `AdvanceDurable` takes a MAX, so a later frame covers it. A gap at the TAIL
    is not: nothing will ever carry that LSN, so the wait can only end when an unrelated later commit drains
    past it. A busy engine hides this; an idle one runs the wait to its deadline.
  composes_with: AP-01 (append, including the frame's publish, strictly precedes any page effect), which is
    what lets CK-02 use the published frontier: a captured page can only reflect records whose frames are
    already published, so `LastPublishedLsn` bounds "records a captured page copy can reflect" from above.
  scope: `WalCommitBuffer.Publish` (advances it, BEFORE the FrameLength release — see the note),
    `WalCommitBuffer.SeedNextLsn` (seeds it on reopen so DurableLSN never starts above it),
    `WalManager.LastPublishedLsn`, `DurabilityLog.LastPublishedLsn`, `UnitOfWork.Flush`, `UnitOfWork.FlushAsync`,
    `CheckpointManager.RunCheckpointCycle` (the step-1 barrier and the CK-02 flush2)
  on_violation: #937 — `Transaction.Dispose` blocks for the UoW's deadline and then throws
    `WalBackPressureTimeoutException` FROM the dispose, replacing the in-flight exception that caused the
    failure; every checkpoint cycle, the shutdown cycle included, fails transiently for
    `CheckpointBarrierTimeoutMs`
  verified: WalFlushTargetTests — DisposingTheFailedTransaction_DoesNotWaitForAnLsnNoFrameOwns,
    ACheckpointCycle_CompletesWithoutALaterCommit, TheFlushTarget_NeverNamesAnLsnNoFrameOwns [VerifiesRule];
    AFlushTargetingTheAllocationFrontier_IsRejected [RuleMutant], which drives the pre-fix behaviour through
    `WalManager.FlushTargetProbe`
  note the stalling budget is `DefaultUowTimeout`, NOT `DefaultCommitTimeout`. A quick transaction's UoW is
    created with a BOUNDED deadline (`CreateUnitOfWork` falls back to `DefaultUowTimeout`), so
    `UnitOfWork.FlushAsync` takes its `FromDeadline` branch and never reads the commit timeout. Both default
    to 30 s, which is how the issue came to name the wrong one.
  note the max-CAS in `Publish` runs BEFORE the `FrameLength` release store, not after. AP-01 puts a commit's
    page effects strictly after its append returns, so a frontier read taken after `Publish` returns covers
    every record a captured page can reflect. Ordered the other way there is a window where the frame is
    drainable — and its page effects therefore imminent — while the frontier still omits it, which is exactly
    the hole CK-02 forbids.
  note `LastAppendedLsn` is NOT retired. It remains the correct thing to read for "what has the allocator
    handed out" (LOG-08 monotonicity, the reopen seed, integrity's NextLsn check). It is only unfit as a WAIT
    TARGET.

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

### CP-03: Writeback debt discharged only after fsync `[fatal]`
  invariant ∀path that calls MarkCaptured: the data file fsync covering the sampled generation must complete first
  impl checkpoint: Step 4 (fsync) before Step 5 (MarkCaptured)
  impl SavePages: FlushToDisk() in ContinueWith before the MarkCaptured loop
  scope: CheckpointManager.cs, PagedMMF.SavePages, PagedMMF.MarkCaptured
  on_violation: page marked clean before durably on disk;
    eviction discards modifications; crash loses data
  requires: PS-01, PS-10
  rationale: 🔴 CORRECTED 2026-08-16. Was phrased on DecrementDirty, which no longer marks a page clean — writers do not
    touch the mutator's counter at all (PS-05). The ordering constraint is unchanged; the operation it constrains moved.

### CP-04: Re-dirty safety — a modification racing the capture keeps the page owed `[fatal][silent]`
  invariant a page modified after the checkpoint sampled its generation still has WritebackGen != CapturedGen when the
            cycle publishes that sample, so the next cycle rewrites it
  impl the checkpoint samples WritebackGen BEFORE the seqlock copy, under the ACW sentinel, and publishes THAT value
    after its fsync. Any modification in between advances the generation past it, so MarkCaptured's monotonic CAS leaves
    the page owed. No counter floor, and no dependence on how many writes are in flight.
  scope: CheckpointManager.cs, ChunkAccessor.MarkSlotDirty, ChangeSet.AddByMemPageIndex,
         PagedMMF.WritePagesForCheckpoint, PagedMMF.MarkCaptured
  verified: ChangeSetDirtyMarkConservationTests; ChangeSetConservationTests.Add_TwiceForSamePage_TakesOneMark_ButRecordsBothModifications;
            ChangeSetCallSiteRecordingTests (a write after a capture leaves the page owed, through AllocateChunk and a ChunkAccessor rental,
            first registration and re-dirty — #1245)
  on_violation: concurrent modification lost — page appears clean,
    eviction discards the re-dirty, data silently gone
  rationale: 🔴 REWRITTEN 2026-08-16. The old formulation was arithmetic on the mutator's counter — "re-dirty pushes DC
    to ≥2 so the checkpoint's decrement leaves ≥1" — and it carried its own contradiction: the impl notes recorded that
    EnsureDirtyAtLeast(1) is a no-op at DC=1 and EnsureDirtyAtLeast(2) LIVELOCKS (checkpoint 2→1, re-dirty 1→2, never
    0), while four live call sites used exactly those forms. Comparing generations removes the arithmetic and with it
    both failure modes.

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
         is owned by ChangeSet, not ChunkAccessor — corrected 2026-07-28); ChunkBasedSegment.ReturnWarmAccessor /
         ReturnWarmSiblingAccessor / ExitBatchMode (a cached warm accessor drops its renter's set once flushed, or its
         next cold rent flushes the deferrals of whoever owns the set next — #946)
  verified: ChunkAccessorTests.AReturnedWarmAccessor_NeverFlushesTheChangeSetItsRenterHandedOn (four cases, one per drop site;
            removing any one drop reddens exactly its case)
  on_violation: early decrement allows checkpoint to snapshot page
    while caller still holds OLC write lock on evicted slot's node;
    same corruption as CP-11

### CP-13: ActiveChunkWriters conservation `[fatal][silent]`
  invariant every IncrementActiveChunkWriters is matched by exactly one DecrementActiveChunkWriters
  invariant no writer in flight → ∀ page: page.ACW == 0
  note CP-11 and CP-12 constrain when ACW may be observed non-zero and when the decrement may be
       deferred; NEITHER requires that it ever happens. Every ChunkAccessor that dirties a slot must
       therefore reach CommitChanges() or Dispose() — an accessor abandoned without either leaks the
       registration permanently, because the release is driven only by those two paths.
  scope: ChunkAccessor.MarkSlotDirty, ChunkAccessor.CommitChanges, ChunkAccessor.Dispose,
         ChangeSet.FlushDeferredEvictions, ArchetypeClusterState.DrainPendingClusterFinalizations
  on_violation: the leaked page is skipped by CP-11 in EVERY checkpoint cycle, so CK-03's coverage gate
    never opens, CheckpointLSN never advances and CK-04 recycles no WAL segment. The log then grows at
    the full write rate until the writer stalls and the engine raises WalBackPressureTimeout — observed
    as 22 GB across 86 segments in four minutes against a 120 MB data file (#817). Silent: nothing is
    corrupted and nothing is slow; a counter simply never returns to zero.

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
  invariant page evictable ↔ (DirtyCounter == 0 ∧ WritebackGen == CapturedGen ∧ ActiveChunkWriters == 0
    ∧ SlotRefCount == 0 ∧ AccessEpoch < MinActiveEpoch)
  never evict page with DirtyCounter > 0 (a unit of work still holds a mutator mark on it)
  never evict page with WritebackGen != CapturedGen (bytes not yet durable on the data file — PS-10)
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
  invariant a fetched page is tagged with max(the epoch its caller read, the caller's pin at the tag): a wait inside
            the fetch may move the pin past the value read before it (#1230); tagged with that value, the page is
            older than every pin and evictable while the caller uses it (#1382, PagedMMF.TagEpoch)
  scope: PagedMMF.RequestPageEpoch, PagedMMF.RequestPageEpochUnchecked, PagedMMF.RequestPageEpochNoSweep, PagedMMF.TagEpoch,
         PagedMMF.UnlatchPageExclusive
  on_violation: decreasing epoch makes page appear stale;
    eviction while transaction still holds pointer
  note: the reset clears every thread's tag, not the unlatcher's alone: an address kept into a page for longer than the
        epoch holds it needs a hold of its own — a buffer reader keeps its root's slot in its window
        (ChunkAccessor.KeepInWindow), or its release wrote into a page that had left the cache (#1290).

### PS-04: DirtyCounter mutations are atomic read-modify-write `[fatal]`
  invariant every DirtyCounter mutation uses an Interlocked RMW (Increment / Decrement / CAS-retry loop)
  never read-then-write DirtyCounter non-atomically
  scope: PagedMMF.IncrementDirty / DecrementDirty / DecrementDirtyByDelta
  rationale: 🔴 CORRECTED 2026-07-27. This rule previously required the per-page StateSyncRoot lock. The implementation
    moved to lock-free atomics, which is STRICTLY STRONGER for the failure mode the rule names (negative DC) and is not
    taken under StateSyncRoot on any path — so the rule as written described neither the code nor the better guarantee.
  on_violation: racing increment/decrement → negative DC;
    clock-sweep interprets as clean → premature eviction of dirty page

### PS-05: DirtyCounter marks are owner-scoped and exactly conserved `[fatal]`
  invariant only a ChangeSet may raise DirtyCounter, and only via AddByMemPageIndex — one mark per page, taken on its first
            registration; a re-dirty of a page it already tracks records the modification and takes no second mark
  invariant a ChangeSet releases exactly the marks it took, one for every page it took one on — no more, no fewer
  invariant every ChangeSet is released by whoever created it: a UoW-owned set on UoW dispose, a
            transaction-owned set on transaction dispose, a locally-created set before its creator returns
  never release marks taken by another owner (that is #385's lost write)
  never retain a mark "for the checkpoint to consume" (that is #824's leak)
  never let the checkpoint, SavePages, or any writer touch DirtyCounter at all
  invariant an owner may give its marks back before it ends, at a point where it holds no address a mark alone protects: a transaction at its
            epoch refresh, PREPARE's component pass every 1 024 entries, a publish and a failed commit's rollback between entries under
            pressure (Transaction.RefreshBetweenCommitSteps, #831). The page stays owed a write (PS-10), so it is not evicted unwritten
  invariant at quiesce — no unit of work open, no checkpoint running — every page has DirtyCounter == 0
  invariant an owner releases its marks on its throw path too (#1382): a transaction's and a unit of work's dispose release them in a finally,
            whatever their rollback, drain or registry write threw, and so does the checkpoint's metadata hook for its local set. A cleanup drain
            releases the marks of the entries it has committed whenever it moves its epoch (DeferredCleanupManager.EpochYield.Refresh): it is
            unbounded, and kept to its end its marks held every page it had touched — 3,434 of a 64 MiB cache, while it waited on back-pressure
  scope: ChangeSet.AddByMemPageIndex / ReleaseDirtyMarks / Reset, PagedMMF.DecrementDirtyByDelta, Transaction.RefreshBetweenCommitSteps,
         UnitOfWork.Dispose, Transaction.Dispose, EntityAccessor, ChunkBasedSegment.GrowChunkCapacity (its local set, released in a finally),
         DeferredCleanupManager.EpochYield, DatabaseEngine.PersistArchetypeState
  verified: ChangeSetDirtyMarkConservationTests; SegmentGrowAtomicityTests.AChunkSegmentGrowThatThrows_StillReleasesItsLocalChangeSet (the
            throw path of a locally-created set), with its mutant AMarkLeftOnALocalChangeSet_IsRejected;
            ChangeSetCallSiteRecordingTests.ReleasingOneOwnersMarks_LeavesTheOtherOwnersMarkInPlace (an over-release, which Release's clamp
            hides on a lone owner, shows on a co-owned page)
  on_violation: under-release → page permanently unevictable, cache starves after tens of minutes (#824);
    over-release → page evictable with unwritten bytes, data lost before reaching stable media (#385)
  rationale: 🔴 REWRITTEN 2026-08-16. This rule used to REQUIRE the defect: "issues exactly (N-1) decrements, leaving one
    outstanding mark from this UoW". That balances only if exactly one unit of work touches a page per checkpoint cycle.
    A cycle is wall-clock (30 s by default) and says nothing about how many units of work run inside it, so K of them
    strand K-1 marks that nothing will ever release — the page can never be evicted, and the cache starves (#824).
    Measured directly: K=2 left 3, K=8 left 9, K=26 left 27.
    The retained mark was standing in for "the bytes are not on disk yet". That is a real obligation, but it is not a
    count and it does not belong to the mutator — it now lives in the page's writeback generation (see PS-10), which
    only a durable write can discharge. With the two separated, the counter is exactly conserved and the protection is
    strictly stronger than the mark it replaced.
    Six earlier attempts tried to reconcile the two meanings inside one integer — acking the observed count at capture,
    sequence pairs, generation pairs bolted onto the counter — and every one either leaked or lost writes. The lesson is
    the rule: one field, one owner, one meaning.

### PS-05a: A ChangeSet has one writer at a time; a transaction running beside its unit of work's others owns its own `[fatal]` `[silent]`
  invariant a ChangeSet's tracking (a HashSet and a List) is mutated by one thread at a time; hand-off between threads is legal, overlap is not
            (ChangeSet.EnterMutation detects overlap and throws, #400)
  invariant a Deferred or GroupCommit unit of work's shared ChangeSet serves only transactions that do not run concurrently: one transaction per
            system per tick, on that system's thread
  invariant a transaction that runs concurrently with others of its unit of work — the per-chunk transactions of a parallel QuerySystem that
            WritesVersioned (ExecuteChunkWithTransaction) — is created by UnitOfWork.CreateConcurrentTransaction and owns its ChangeSet: rented at Init,
            its marks released and the set returned to the pool at its Dispose (PS-05)
  note owning loses nothing: the marks are dirty-page accounting only — the checkpoint writes the pages, never the unit of work (ADR-054), and the
       page's writeback generation, not the counter, keeps unwritten bytes from eviction (PS-10). The fence's per-chunk ChangeSets are the same pattern
  on_violation: silent under the default Isolate policy — concurrent chunks corrupt the shared set and fail ("ChangeSet concurrent mutation", and
    downstream "not found in PK index" in Versioned copy-on-write); the failed chunks are drained, so the system processes a fraction of its entities
    with nothing reported (#1116: 12 to 192 of 600 per tick)
  scope: UnitOfWork.CreateConcurrentTransaction, Transaction.Init (ownChangeSet), Transaction.Dispose, TransactionChain.CreateTransaction,
         TyphonRuntime.ExecuteChunkWithTransaction, ChangeSet.EnterMutation, ChunkBasedSegment.ReturnWarmAccessor / ReturnWarmSiblingAccessor /
         ExitBatchMode (a [ThreadStatic] warm accessor must not keep the set past its rent: the set's owner hands it on — #946)
  verified: ParallelVersionedWriteTests (1, 4 and 8 workers, and a change-filtered dispatch: 600 entities, 16 chunks, every entity paid once per
            dispatch over ten dispatches). Run against the per-chunk transaction taking the shared set: the three concurrent cases fail.
            ChunkAccessorTests.AReturnedWarmAccessor_NeverFlushesTheChangeSetItsRenterHandedOn covers the warm caches (see CP-12)

### PS-10: A page's writeback debt is discharged only by a durable write `[fatal][silent]`
  invariant every path that modifies a page's bytes records it (MarkPageModified, or IncrementDirty which implies it)
  invariant WritebackGen != CapturedGen ⟺ the page holds bytes that are not on the data file
  invariant a page with writeback debt is never evicted, whatever its DirtyCounter says
  invariant a page with writeback debt is always collected by the next checkpoint
  invariant MarkCaptured is called only after the fsync that made the sampled generation's bytes durable (CP-03)
  invariant MarkCaptured is monotonic — a stale publication never lowers CapturedGen
  invariant the generation is sampled BEFORE the page is copied, so a modification racing the capture stays owed
  invariant the pair carries over when a slot is reused: TryAcquire reclaims only a slot whose two generations are equal, and leaves them
            so. CapturedGen ≤ WritebackGen holds across occupants, so a capture published late for a previous occupant is a no-op
  never record the mark without recording the modification — a repeated AddByMemPageIndex takes no second mark but
        must still record the write, or a checkpoint that settled the page between the two calls loses it
  scope: PagedMMF.MarkPageModified / MarkCaptured / HasWritebackDebt / WritePagesForCheckpoint / SavePages /
         CollectDirtyMemPageIndices / TryAcquire, IPageStore.MarkPageModified, CheckpointManager.RunCheckpointCycle,
         ChunkBasedSegment.MarkChunkModified, ArchetypeClusterState.NoteClusterPageModified, ClusterRef.MarkDirty, ClusterRef.WriteSpatial,
         ClusterEnumerator.MarkCurrentDirty, ClusterEnumerator.MarkSlotDirty, DatabaseEngine.EmitArchetypeFenceRange,
         DatabaseEngine.DrainClusterShadowSlots, EntityRefMut.WriteRef, EntityAccessor.NoteSvInPlacePageWrite, PagedMMF.MemPageIndexOf,
         DatabaseEngine.MarkClusterSlotDirty, ArchetypeClusterState.RebuildIndexesFromData
  verified: ChangeSetDirtyMarkConservationTests; InPlaceClusterWriteSurvivalTests (a span write and a spatial write, each through a path that maps the
            page clean, must still read back after allocations have cycled the page cache — both fail with the page record removed);
            PageSlotRecordTests.ACaptureLandingAfterTheSlotWasReused_LeavesItsNextPageClean [VerifiesRule] (fails with the reset to 0 restored);
            PrepSliceEquivalenceTests.AnIndexMoveWrittenByASlice_SurvivesTheCacheEvictingItsPage [VerifiesRule] (a checkpoint settles the writes' own
            records, then every evictable page goes between Prep and Migrate; fails 5 of 5 with the drain's page record removed);
            InPlaceClusterWriteSurvivalTests.CommittedPointWritesSurviveEviction [VerifiesRule] (both Set overloads; 400 of 400 committed point writes
            revert with the write's page record removed, and the by-type case fails with only its own record removed)
  note a SingleVersion point write (EntityRefMut.Set, through WriteRef) records its page at the call (#1172), by address with no page lookup
       (MemPageIndexOf), since it runs once per written component. Before #1172 nothing recorded it: a committed value reverted whenever the cache evicted
       the page before the next fence. Since #1199 the store happens in the same call, right after the record, through a base the handle re-resolves
       (mapped dirty again) if its accessor had let go of the page — so a checkpoint can no longer copy the page between the record and the store. That
       window was #1176: a store through a ref held across an archetype switch, or 31 other pages, landed after a checkpoint had settled the page
  note the fence's shadow drain writes an AllowMultiple field's new element id into the cluster tail through the slice's accessor, which holds no
       ChangeSet, and records the page at that write (#1171). The WAL emit's record comes later, at Finalize: in between, Migrate's own loads can evict a
       page nothing marks, and the element id went with it — Migrate read the old id, its location update was dropped, and the index kept the entity at
       the slot it had left
  note the in-place cluster writers hold no ChangeSet: a span over a cluster column (declared by MarkDirty, or by DatabaseEngine.MarkClusterSlotDirty),
       WriteSpatial, and the index rebuild's element-id writes (RebuildIndexesFromData, once per cluster). They record the page at the
       write (MarkPageModified), and the fence's WAL emit records every cluster page it serialises besides — as writeback debt, not as a dirty mark in a
       ChangeSet, because the serial fence SAVES its own ChangeSet and would then write data pages outside the checkpoint's WAL barrier (CK-02). Before 2026-09-23 neither did: committed values reverted to the
       on-disk image whenever an unrelated allocation evicted the page (found by the push-replication oracle; seed 9160 reverted four creatures inside a
       rock spawn), and the checkpoint never collected those pages.
  on_violation: an unrecorded modification is never written and is lost at eviction (#385, #301, #30);
    a debt never discharged pins the page for ever (#824). Until #1127, TryAcquire reset a reused slot's pair to 0, so a checkpoint's
    capture landing after a SavePages had settled the page and the slot was reused set CapturedGen above the new page's WritebackGen:
    debt nothing could discharge, the slot pinned and rewritten every cycle
  rationale: NEW 2026-08-16. Replaces the counter-floor idiom (EnsureDirtyAtLeast(memPage, 2)) that the paths without a
    ChangeSet used to protect themselves. A floor is a no-op whenever the counter already sits above it, so on exactly
    the busy pages that needed it the protection was silently absent; and it depended on flushes of a given page being
    serialised, which MarkOccupancyPageDurable's own comment had already flagged as needing "a write-generation stamp
    instead of a counter" if that ever stopped holding. This is that stamp.

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
            post-`FlushToDisk` continuation, BEFORE any page in the batch becomes evictable (MarkCaptured).
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
         CreateOrGrow data/map/end/old-tail pages, PinForPublish, GetPageAddressExclusive)
  on_violation: PS-06 TOCTOU — the slot is evicted and reused for another file page in the gap; the grow then clears/rewrites it
                and calls UnlatchPageExclusive, releasing the OTHER thread's latch and forcing PageState=Idle + a stray seqlock
                bump → cross-thread page-cache corruption (a native host crash in Debug; silent heap corruption in Release).
                A leaked SlotRefCount (decrement skipped on a latch-throw) permanently pins a page → cache fills → back-pressure
                stalls grows holding latches → the checkpoint spins forever in CopyPageWithSeqlock (hence the helper's try/finally).
  verified: empirically (latch-fail assert captures 11→0; SimdKwayMergeTests passes isolated; full suite no longer host-crashes);
            a deterministic eviction-in-the-gap fault-injection test is a follow-up

### PS-11: A segment grow publishes all of its pages or none `[fatal]` `[silent]`
  invariant a grow that throws leaves the segment as it was: its in-memory page list, its directory (entries and terminator, on the root and
            map-extension pages) and its data-page forward chain (the old tail's `LogicalSegmentNextRawDataPBID`) all still describe the old length
  invariant [allocate data pages, map pages, twins] → [initialize every new data page] → [slot-pin every directory page and the old tail] →
            [latch them all] → [publish: directory entries, map links, old-tail link] → [`_pages` = new list]. Every step that can fail (wait
            for the page cache, allocate, or lose a latch to another thread) comes before the publish; the publish only re-enters latches the
            grow already holds, on resident pages
  invariant the pages a failed grow allocated (data pages, map-extension pages, their twins) go back to the occupancy map, and the twins' pair
            state goes with them
  never write a page the published segment can reach (the root, an existing map page, the old tail) before every step that can throw has
        succeeded
  requires: PS-09 (the latch helper the pins go through), PS-05 (a grow's local ChangeSet is released on the throw too)
  scope: LogicalSegment.CreateOrGrow, LogicalSegment.Grow, PinForPublish, InitDataPages, ReleaseUnpublished,
         IPageStore.ReleaseUnpublishedPages, ManagedPagedMMF.ReleaseUnpublishedPages, ManagedPagedMMF.PersistProtectedPage (re-checks a pair a
         failed grow may have just dropped), ManagedPagedMMF.GrowOccupancySegment (its reserved map page counts as consumed only once the grow
         publishes), ChunkBasedSegment.GrowChunkCapacity
  on_violation: a page-cache back-pressure timeout, or any other throw, mid-grow leaves the directory listing pages the segment never adopted
                and the old tail linked into them. Nothing notices while the process runs, since the next grow rewrites both; if it stops first,
                the shutdown checkpoint persists them and the next open's strict load throws "integrity check failed at Load" (measured:
                directory=900 chain=800 for a 300-page segment grown to 900 behind an 800-page cache). The failed attempt's pages also leak.
  verified: SegmentGrowAtomicityTests.AGrowThatFailsInitializingItsPages_LeavesTheSegmentAsItWas [VerifiesRule] (a probe fails the grow half-way
            through its new pages; asserts the length, the directory, the chain, a fresh load, every allocated page freed and no slot reference
            left), with its mutant APublishThatRanBeforeThePagesWereInitialized_IsRejected. AGrowThatFailsPreparingItsPublish_LeavesTheSegmentAsItWas
            fails it at the old tail's pin, then at its latch; AGrowPastTheRootDirectoryThatFails_GivesBackItsMapPageAndTwin covers a
            map-extension page, its twin and its pair; SegmentGrowBackpressureTests.AGrowThatRunsOutOfPageCache_LeavesTheSegmentAsItWas drives the
            real back-pressure timeout
  note: releasing needs the page cache too, so it can fail in turn. That failure is recorded on the grow's exception, which is still the one
        thrown; the pages then leak, which corrupts nothing since nothing references them.
  note: not covered. A Create that throws part-way leaves its pages allocated and its segment registered with no page list (nothing
        references its root, so nothing is torn, but the pages leak). ChunkBasedSegment.GrowChunkCapacity sets the new pages' room bits before `base.Grow`
        and publishes only the capacity after it; nothing but a failed CreateOrGrow post-condition is known to throw there.

### PS-12: A mem-page's address is computed in 64 bits, never in `int` `[fatal]` `[silent]`
  invariant ∀ expression of the form `memPagesBaseAddr + (memPageIndex × PageSize)`: the product is
            evaluated as `long` — `memPageIndex * (long)PageSize` — never as `int × int`
  invariant an `int` product is exact only while `memPageIndex < 262 144` (2 GiB ÷ 8 KiB), so a narrow
            product is a LATENT defect whose trigger is the configured cache size, not the code path
  scope: PagedMMF.SavePages, PagedMMF.GetMemPageAddress, PagedMMF.WritePagesForCheckpoint,
         ChunkAccessor.GetMemPageIndexFromSlot
  rationale: the page cache is one contiguous native block addressed as `base + index × PageSize`.
    `PageSize` is an `int` constant and `MemPageIndex` is an `int` field, so their product is an `int`
    unless one side is widened — and the compiler gives no warning for it. Past 262 143 pages the product
    wraps negative, and `byte* + int` sign-extends, so the pointer lands up to 2 GiB BELOW the cache base,
    in memory the engine does not own.
  on_violation: a wild-pointer WRITE, not a read. Both `SavePages` sites do `++headerAddr->ChangeRevision`
    — a 4-byte read-modify-write at the foreign address — and then `StampPageForWrite` over 8 KiB of it.
    An access violation if that page is unmapped; silent heap corruption if it is mapped. This is the
    structural write path: bootstrap, schema write, segment grow, v1 replay.
  verified: PageCacheAddressArithmeticTests.AMemPageOffsetPastTwoGibIsComputedIn64Bits [VerifiesRule] —
            asserts the offset for `memPageIndex` 262 144 is 2 147 483 648, and that the narrow `int`
            product it replaced wraps to −2 147 483 648, so the test fails if the widening is removed;
            TheFirstPagePastTwoGib_IsAddressedAtItsSixtyFourBitOffset_ByTheIOWindows does the same for the disk
            I/O path's window; LargePageCacheTests (nightly) writes and reads back pages past index 262 143 of a
            real 3 GiB cache
  note: the 2 GiB − 8 KiB cache-size ceiling is what kept this unreachable. It was fixed BEFORE the ceiling
        went (#945: S1 here, the ceiling in S7), deliberately: until then the ceiling was the only thing
        standing between this expression and live heap corruption.

### PS-13: The page cache is one contiguous native block, and a page address maps back to its slot exactly `[fatal]` `[silent]`
  invariant the cache is ONE allocation: slot i's page is at `MemPagesBaseAddress + i × PageSize`, for every i in [0, MemPagesCount)
  invariant `MemPageIndexOfRawData(GetMemPageAddress(i) + PageHeaderSize, MemPagesBaseAddress) == i`, for every i: the reverse map is two
            instructions of pointer arithmetic, with no search and no table
  invariant the disk I/O path addresses the same bytes: each page's window slice (`PageCacheWindow.PageMemory`) starts at
            `GetMemPageAddress(i)`, and a write run never leaves its window
  scope: PagedMMF.MemPagesBaseAddress, PagedMMF.GetMemPageAddress, PagedMMF.MemPageIndexOfRawData, PagedMMF.AllocateCacheBlock,
         ChunkAccessor.GetMemPageIndexFromSlot, PersistentStore.MemPagesBaseAddress, PageCacheWindow.PageMemory, PageCacheWindow.PageRunMemory
  rationale: #945. The cache passed 2 GiB as one 64-bit block with 1 GiB windows for the two async I/O paths, rather than as several blocks,
    because ChunkAccessor recovers a page's slot from its address on every first write, commit, latch and unlatch of a B+Tree page. Split
    into blocks, that map needs a search (20-30× slower) or a per-accessor index table (+128 B on the hottest struct). This rule writes the
    design's load-bearing assumption down, so that "just split it into blocks" fails a rule instead of corrupting accounting silently.
  on_violation: a wrong slot index on the write path: the dirty counter, the active-writer count and the seqlock counter of ANOTHER page
    move (PS-05, CP-13, SL-*), so a page is evicted while written, or written out half-modified, or its checkpoint skipped. Nothing fails at
    the faulty line; the damage surfaces later as a torn page or a lost write.
  verified: PageCacheContiguityTests.EveryPage_MapsBackToItsSlot_AndTheIOWindowsAgreeOnItsAddress [VerifiesRule] (every slot of a real cache;
            eight windows under the small-window nightly); LargePageCacheTests.AThreeGibCache_WritesAndReadsBackPagesPastTheOldBoundary
            checks the map at a slot past index 262 143 of a real 3 GiB cache (nightly)

### PS-14: A page the engine did not read from disk reads as zero before anyone can reach it `[silent]`
  invariant ∀ slot assigned to a file page that is NOT loaded from the data file: all 8 192 bytes are zero before the slot is ready (PS-15)
            — its owner clears it after publishing it in the page directory, while no other thread may use it, so the "not on disk" decision
            is taken when nothing else can bring the page in
  invariant every page a segment is given is cleared in full before any of it is written, header included: each data page, the root and
            every new map-extension page, including the one that holds only the terminating 0. Its page number may be reused from a deleted
            segment, so the slot holds that page's bytes — resident, or read back from disk — and the slot clear above never runs for it
  invariant two header counters survive that clear: the seqlock `ModificationCounter`, and `ChangeRevision`, so a reused page number's
            revision never goes backwards (`PageSectorFooter` stamps each sector with its low 16 bits). Not claimed: that the next stamp
            differs from every stale sector — a checkpoint bumps only its staging copy (CP-08), so the live revision can trail the disk's
  invariant the page-cache block itself is never zeroed: a slot's content before it is first assigned is undefined
  scope: PagedMMF.FetchPageToMemoryOnMiss, LogicalSegment.InitHeader, LogicalSegment.InitDataPages,
         LogicalSegment.CreateOrGrow, PagedMMF.PoisonCacheForProcess
  rationale: a slot handed to a new page holds its previous occupant's bytes, and two directory sites used to clear only the 192-byte
    header before writing a few entries; data pages of a segment created without a clear got nothing. Whatever the caller does not
    write survives — and the clear has to come before the slot is ready, because a new page has no read task a second requester would
    wait on.
  on_violation: unrelated page content — component data, index keys, string-table bytes — reaches the data file inside a page's unwritten
    part, and `StampPageForWrite` CRCs the whole page, so it reloads as valid content. A disclosure into the file, its backups and copies;
    two logically identical databases differ byte for byte. Not `[fatal]`: no write is lost and no reader faults.
  verified: PageInitialContentTests.ANewPageInANeverUsedSlot_ReadsAsZero [VerifiesRule] (the slot is poisoned, so a missing clear reads
            0xA5, not zero) and ANewPageInARecycledSlot_ReadsAsZero (a slot that held a 0x5A page); SegmentPageInitialContentTests covers a
            reused page number as a data page, as a root (in memory and reloaded from disk) and as the terminator-only map page, and
            AReusedDataPage_KeepsItsChangeRevision the surviving revision. Each fails with its fix removed (#1126)
  note: `PagedMMF.PoisonCacheForProcess`, set once by the test assembly, fills every test page cache with 0xA5 at allocation. A fresh
        multi-MiB allocation reads zero on every supported platform, so without it a reader of undefined content would pass by accident.
  note: the block is not zeroed so that a large cache does not become resident at startup; #945 builds on this.
  note: known gap, open. A directory pair's twin is never cleared, and a reused twin's old image can win at open (#1129).

### PS-15: A slot in the page directory is used only once its owner has prepared it `[fatal]` `[silent]`
  invariant a slot claimed for file page X is published (`GetOrAdd`) before it is prepared, so it carries `SlotReady == false` from its claim
            (`TryAcquire`) until its owner — the thread whose `GetOrAdd` published it — has reset `CrcVerified` and either cleared the page (not
            on disk, PS-14) or started its read and recorded it in the read table, then set `ReadPending`; the owner then sets `SlotReady` with a
            release write
  invariant every `RequestPageEpoch*` that finds a slot not ready waits — acquire reads — until it is ready (then goes on) or no longer holds
            X (its owner abandoned it, or it was reclaimed — then looks the page up again)
  invariant after tagging the slot's epoch, a requester re-validates `FilePageIndex == X && SlotReady` before using it, and only then takes it
            out of `Allocating`, under `StateSyncRoot`. The tag (a full-fence CAS, or an acquire read when no CAS is needed) pairs with
            `TryAcquire`, which withdraws `SlotReady` and fences BEFORE re-checking the epoch, and restores it when it backs off: of a reclaim and
            a tag that race, at least one sees the other, so the reclaim backs off or the requester retries — never a slot being reclaimed
  invariant `TryAcquire` writes `SlotReady`, and drops a completed read task, only on a slot its lock finds `Free` or `Idle`. A slot
            another thread claimed after the reclaim's unlocked first pass is `Allocating`, and its owner sets `SlotReady` without that lock:
            the reclaim backs off without touching it, since a restore of the value it read before the owner's write would erase that write,
            and the slot's read is for its requesters to complete (#1201)
  invariant a thread whose `GetOrAdd` returns another slot takes that slot untouched: no read into it, no `CrcVerified` reset, no read-task
            replacement
  invariant an owner that throws before the slot is prepared unpublishes it first — a waiter sees it no longer holds X and looks the page
            up again at once — then frees it. If it had started a read (only an allocation can fail after that: `AsTask`, the trace
            continuation, the read-table insert), it frees the slot only once that read has landed: it is the read's only observer, and until
            then the slot stays `Allocating`, which `TryAcquire` never takes. It drops any read-table entry the slot has. It does NOT mark the
            slot ready: once Free it may already be another owner's unprepared slot. An owner that throws after the slot is prepared (the trace
            scope's close) still marks it ready
  invariant `ReadPending` is cleared only by a thread that holds the slot against a new read: a requester after tagging and validating it,
            `TryAcquire` under the slot's lock, or the slot's owner. The checkpoint and `SavePages`, which hold the slot by its debt, only wait
  scope: PagedMMF.AllocateMemoryPageCore, PagedMMF.FetchPageToMemoryOnMiss, PagedMMF.WaitForSlotReady, PagedMMF.ValidateTaggedSlot,
         PagedMMF.AbandonUnpreparedSlot, PagedMMF.WaitForOrphanedRead, PagedMMF.RequestPageEpoch, PagedMMF.RequestPageEpochUnchecked,
         PagedMMF.RequestPageEpochNoSweep, PagedMMF.TryAcquire, PagedMMF.CompletePendingRead, PagedMMF.WaitForPendingRead,
         PagedMMF.DropReadTask, SlotReady, ReadPending
  requires: PS-17 — the directory publishes one slot per page, and its lookups are hints the requester validates
  rationale: a miss publishes its slot so that concurrent misses on the same page converge on one slot, and the slot is published before
    the read because the read's target must be decided by the thread that owns it. Between the two, the slot's bytes are its previous
    occupant's (or undefined), its CRC flag may be the previous occupant's `true`, and a new page has no read task to wait on.
  on_violation: (3) a reclaim backing off a slot claimed since its first pass restores a stale `false` over its owner's `SlotReady`: the
    page stays published and never ready, and every requester that comes after throws `PageCache/SlotReady` after the lock timeout — for
    the rest of the process if the slot is still `Allocating`, which nothing reclaims (#1201: `EntityHandleLifetimeTests`, a 4 MiB cache
    read by two threads, about 1 run in 4). (1) a requester uses the page before its read lands — on `main` it silently got zeros (a stored CRC of 0 skips
    verification): `PrepSliceEquivalenceTests` under 6 concurrent processes failed 22 of 60 with 2 001 index entries missing; with the test
    poison it is a CRC failure on 0xA5 bytes, or a lock word that looks held (a 10 s `SegmentAllocation/LockBuffer` timeout); (2) the loser's
    second read overwrites the winner's slot after a writer changed it — a lost write, a seqlock counter left odd, a false CRC failure
  verified: PageSlotPublicationTests [VerifiesRule]: ARequesterOfAPublishedSlot_WaitsUntilItsOwnerHasStartedTheRead (the owner is held
            between publishing and preparing; a second requester must still be waiting, then see the disk content),
            TheLoserOfAConcurrentMiss_LeavesTheWinnersSlotAlone (a loser held before publishing must not overwrite a write made after the
            winner's read, and the page is read once), AnOwnerThatFailsBeforeItsRead_LeavesNoWaiterStuck and
            AnOwnerThatFailsAfterStartingItsRead_LeavesNoWaiterStuck. Each fails with its part of the fix removed (#1128).
            AReclaimThatBacksOffAClaimedSlot_LeavesItsOwnersReadyAlone holds a reclaim between its first pass and its lock while another
            requester claims, publishes and prepares the slot; with the withdraw-and-restore on an Allocating slot, the reclaimer then waits
            on a page never marked ready and times out, 3 runs of 3 (#1201). Contended:
            `PrepSliceEquivalenceTests`, 6 concurrent processes × 10 rounds — 22 of 60 failing on `main`, 0 of 60 with this rule held. Two
            parts have no deterministic test and are argued above: the reclaim/tag pairing (covered by that stress run), and the owner waiting
            for its started read before freeing the slot (a real file read cannot be held in flight)
  note: the wait is bounded by `PageCacheLockTimeout`. The owner's preparation is a few microseconds and waits on nothing, and an owner that
        fails unpublishes the slot before waiting for its read, so the bound only turns a defect into a loud `LockTimeoutException` instead
        of a hang.

### PS-16: A slot that owes a writeback has its debt bit set `[fatal]` `[silent]`
  invariant WritebackGen != CapturedGen ⟹ the slot's bit in the debt bitmap is set, wherever no writer is between its generation bump and
            its bit: a MarkPageModified or IncrementDirty that has returned is always covered
  invariant the bit is a hint, never the truth: eviction and every "is this page owed" decision read the generations (PS-10)
  invariant a writer bumps WritebackGen (Interlocked, a full fence), THEN reads the bit and sets it if clear. A discharge that finds the slot
            owing nothing clears the bit, THEN after a full fence re-reads the generations and sets the bit again if the slot owes. A
            store-buffer pairing: of a writer and a discharge that cross, at least one sees the other
  invariant the debt count moves only on a bit transition its mover observed (the word Interlocked.Or / And returned). It equals the set
            bits at quiescence and may dip for an instant while a writer and a discharge cross, hence WritebackDebtPercent's clamp
  invariant a bit with no debt behind it — two discharges crossing can leave one — is cleared by the next CollectDirtyMemPageIndices that
            visits it, and by TryAcquire when it reclaims the slot
  scope: PagedMMF.RaiseDebtBit, PagedMMF.ClearDebtBit, PagedMMF.SettleDebtBit, PagedMMF.MarkPageModified, PagedMMF.IncrementDirty,
         PagedMMF.MarkCaptured, PagedMMF.CollectDirtyMemPageIndices, PagedMMF.WritebackDebtPercent, PagedMMF.TryAcquire
  requires: PS-10 — the generations are the debt; the bit only lets the checkpoint find it
  rationale: #1127. CollectDirtyMemPageIndices and WritebackDebtPercent scanned every slot: every checkpoint, and every 250 ms while the
    dirty-page trigger is armed. At #945's 512 GiB (67 M slots) that is 4 GiB of reads per call. The bitmap reads one word per 64 slots
    and visits only owed ones; the count makes the percentage O(1). A maintained counter of modifications was rejected before because it
    drifts; this one counts bit transitions, each observed by exactly the thread that made it.
  on_violation: a slot that owes a writeback and has no bit is never collected. Its bytes never reach the data file and it is never
    evictable, while the checkpoint's coverage gate (CK-03) opens without it and the log behind it is recycled: the next crash loses a
    committed write. Nothing reports it before then.
  verified: PageSlotRecordTests [VerifiesRule]: AWriterBetweenADischargesCheckAndItsClear_LeavesTheOwedPageWithItsBit (a probe runs the
            writer between the discharge's check and its clear; fails with the re-check removed),
            AWriterBetweenADischargesClearAndItsReCheck_LeavesTheOwedPageWithItsBit (the other window) and
            AModificationAfterTheSample_KeepsThePageOwedAndCollected, each also asserting count = set bits.
            ABitWithNoDebtBehindIt_IsClearedByTheNextCollect fails with the collect's clean-up removed;
            Collecting_VisitsOnlyTheOwedSlots_AndFindsWhatAFullScanFinds counts 3 slot visits and 4 words on a 256-slot cache and matches a
            scan of every slot. Not staged: two discharges crossing (the source of stale bits) — its outcome, a bit with no debt, is staged
            directly instead

### PS-17: The page directory publishes at most one slot per file page, and a lookup is only a hint `[fatal]` `[silent]`
  invariant at most one slot is published for a file page: `GetOrAdd` links a slot under its bucket's lock only if no slot in the chain
            holds the page, and otherwise returns that slot, which the caller takes untouched (PS-15)
  invariant a slot's key — its own FilePageIndex — changes only while the slot is in no chain: its owner writes it just before `GetOrAdd`,
            and it is reset only after `TryRemove`. So under a bucket's lock every key in its chain is stable
  invariant `TryRemove(X, S)` unlinks S and nothing else, and leaves S's own link intact, so a reader paused on S finishes its walk down
            the rest of the old chain
  invariant `TryGet(X)` returns a slot only after reading that slot's key equal to X, and the caller validates the slot after tagging it
            (PS-15): a lookup is a hint. "Absent" is exact: an empty bucket read with acquire, or a chain walked under the bucket's lock. A
            walk that followed links without the lock never answers "absent" by itself
  invariant no managed reference per entry: the buckets are one native block, and the chains run through the slot records
            (`DirectoryNext`)
  scope: PageDirectory.TryGet, PageDirectory.GetOrAdd, PageDirectory.TryRemove, PagedMMF.AllocateMemoryPageCore, PagedMMF.TryAcquire,
         PagedMMF.AbandonUnpreparedSlot, DirectoryNext
  rationale: #1136. The directory was a ConcurrentDictionary: one managed node per resident page, 67 M at #945's 512 GiB target for every
    gen-2 collection to mark. Chaining through the slot records needs no node, no tombstone and no rehash, and a hit reads the bucket and
    then the slot record it was about to read anyway. Its one subtlety is that a slot moves between chains when it is reused: a lock-free
    reader paused on it would follow the slot into its new chain and miss the page it was looking for.
  on_violation: (1) two slots published for one page: two copies of it diverge, and the one written last to disk wins — a lost write;
    (2) another slot's mapping removed: a resident page becomes unreachable and a second copy is loaded beside it; (3) "absent" for a
    resident page: its requester evicts a page for nothing and, with the cache exhausted, can time out on back-pressure for a page it
    already has. None of them reports itself.
  verified: PageDirectoryTests [VerifiesRule]: ConcurrentMissesOnOnePage_ConvergeOnOneSlot (8 threads × 300 pages, one winner each),
            AReaderPausedOnANodeThatMovesToAnotherChain_StillFindsThePage (a probe reclaims the node and publishes it in another bucket
            between the reader's key read and its link read; fails without the locked confirmation) and
            AReaderChasingReusedNodes_Ends_AndFindsThePage (two nodes moved to the chain's head forever: the step bound ends the walk).
            APublishedPage_IsAlwaysFound_WhileOtherSlotsChurnThroughItsChain (two threads churn slots through its bucket while two read;
            fails, unstaged, without the locked confirmation). EveryPublishedPage_IsFound_AndRemovingOneSlot_UnpublishesOnlyIt fails
            with a remove that ignores the slot (#1136)

### PS-18: A chunk id names exactly one place in its segment `[fatal]` `[silent]`
  invariant ∀ chunk id c < ChunkCapacity: GetChunkLocation(c) = (⌊(c − root) / d⌋ + 1, (c − root) mod d) with d the chunks per page and root the
            root page's chunk count — exact division for every 32-bit id, so the offset lies in [0, d)
  invariant ChunkCapacity = root + (pages − 1) · d ≤ MaxChunkCount (int.MaxValue − 1): a grow is clamped there, and an allocation that finds the
            segment at the cap throws ResourceExhausted — the capacity never overflows an int
  invariant growth costs the pages it adds: the page list keeps spare room (copied only when it runs out, doubling), the allocator's room bits live in
            blocks that are added, never copied, and a grow pins, latches and re-verifies only the directory pages it writes (LogicalSegment: from the
            one holding the old terminator on)
  invariant the allocator's room bits are a superset of the truth: a page with a free chunk has its bit set. A free clears its chunk bit, then sets the
            page's; an allocation that finds a page full clears the page's bit, then reads the page again and sets the bit back if a chunk was freed in
            between — interlocked on both sides, so whichever runs second sees the other. An allocation that found no room grows the segment only if no
            room or summary bit went from clear to set during its search (a set-back counts) and no clear is in flight; a free chunk above the
            allocation floor is never grown past
  invariant an allocation returns an id below the capacity published when its search began: it searches the pages that capacity covers, never the
            page count — a grow publishes its pages, room bits set, before their capacity
  scope: ChunkBasedSegment.GetChunkLocation, ChunkPageDivider, ChunkBasedSegment.ComputeCapacity, ChunkBasedSegment.GrowChunkCapacity,
         ChunkBasedSegment.AllocateChunkInternal, ChunkBasedSegment.PagesCovering,
         ChunkBasedSegment.TryAllocateOnPage, ChunkBasedSegment.MarkRoom, ChunkBasedSegment.NextPageWithRoom, ChunkBasedSegment.FreeChunk,
         LogicalSegment.Grow, LogicalSegment.CreateOrGrow, LogicalSegment.VerifyDirectoryFrom
  on_violation: the 32-bit magic multiplier it replaced (⌈2³²/d⌉, (c · m) ≫ 32) was exact only while c · (m · d − 2³²) < 2³²: past that, ids of
                remainder d − 1 resolved to the next page at offset −1 — one stride before its chunk area, inside its header — and every read and
                write of them landed there, silently: from chunk 6 100 999 at an 8-byte stride, 54 366 749 for revision chains (#1204). A grow that
                copied the page list and re-read the whole directory each step was O(segment) per grow, quadratic over a segment's life (#1205). The
                lock-free free list the room bits replaced lost pages to races by design, and under an allocation floor a lost page was grown past
                for good; patching it took a loss counter, then a count for the rebuild's own race, and review still found two more (#1205)
  verified: ChunkAddressingTests [VerifiesRule] — the divider against / for every chunks-per-page count from 1 to 4 000, at the ids the old multiplier
            got wrong, at the top of the range and at random; a 6 200-page stride-8 segment locates chunk 6 100 999 on page 6 101 at offset 999, where
            the old division read page 6 102 offset −1; the capacity stops at the chunk-id space at every geometry. SegmentGrowthTests [VerifiesRule]
            — a segment grown to exact lengths across four directory pages, persisted at each, keeps its directory and its chain and reopens to the
            same page list; a one-page grow of a three-page directory touches neither the root nor the first extension; a directory whose chain
            ends early is refused at load. ChunkBasedSegmentBitmapL3Tests — a free between "page full" and the room bit's clear is not lost; a free behind the search is
            taken rather than grown past; a bulk free from a mid-word start keeps every chunk below it; an allocation while a grow is held between
            publishing its pages and their capacity returns an id below the capacity it sees.
            EntityMapAddressingTests — every free chunk above the floor is used before the segment grows, stale bits on full pages included

### PS-19: Every slot reference is released exactly once `[correctness]`
  invariant a ChunkAccessor takes a slot reference on each page it loads into a slot (LoadIntoSlot) and releases it when the slot is
            evicted or the accessor disposed, so an accessor is never copied to be used: a copy loads into slots of its own, takes their
            references and is dropped with them — accessors travel by ref (EntityAccessor.ResolveSpawnAwarePayload)
  invariant a transaction's dispose releases every accessor it holds — its component accessors (FlushAccessors), its cluster cache
            (DisposeClusterCache) and its entity-map accessor — on the read-only path too, and whether the pool takes the transaction
            back or drops it: a pool reset recycles component entries without disposing them, and a dropped transaction is never reset
  invariant so with no transaction or accessor alive, no page is slot-referenced — apart from the per-thread warm accessors, which keep their
            slots between operations by design (ChunkBasedSegment.ReturnWarmAccessor): a bounded number per thread, never one per operation
  scope: EntityAccessor.ResolveSpawnAwarePayload, Transaction.Dispose, ChunkAccessor.LoadIntoSlot, ChunkAccessor.Dispose
  on_violation: the page can never be evicted again (PS-01), silently — no epoch, checkpoint or back-pressure round releases a slot
                reference. Every read of a Versioned or non-cluster component went through a copy of the component accessor and leaked one;
                read-only transactions never disposed their component accessors. A database six times its cache then had 94 % of the cache
                slot-referenced after a snapshot read in short read-only transactions, and its next writers timed out on back-pressure
                with only 12 % of the cache dirty (MarketHardeningTests, 75M items over 8 GiB)
  verified: ReadOnlyTransactionSlotPinTests [VerifiesRule] — read-only and writing transactions, read-only and writing transactions dropped by a
            full pool, and parallel read-only batches each leave no page slot-referenced once disposed ([RuleMutant]: a copied accessor is reported)

### PS-20: A lock word held while its page is captured never stays on disk held `[fatal]` `[silent]`
  invariant a capture of a page — the checkpoint's copy, a structural save's write from live memory — is bracketed on its cache slot's fence word:
            BeginCapture before its first read of the page, EndCapture after its last (a save's, at its continuation, once the OS has read the page)
  invariant a revision chain's lock is held only between a capture ticket taken BEFORE the acquire and EndLockHold AFTER the release; at that
            release, a capture in flight at the ticket or started since marks the page modified, so the next checkpoint writes it with the lock
            free and no one evicts it before. A capture that copied the held word began before the copy, the copy came before the release and the
            release before the post-release read — so the holder sees it, unless it began before the ticket: then the ticket saw it in flight, or it
            had ended before the lock was taken and never saw it held. Acquire and release are interlocked; the two reads are volatile
  invariant a variable-sized buffer's lock, in its root chunk, is held the same way: the write's exclusive hold (LockBuffer to ReleaseLockOnBuffer),
            the read accessor's and the raw read's shared ones each take a ticket before and end it after
  invariant a hold no capture overlapped writes nothing: the page stays as clean as the hold found it
  invariant the hold's page stays in its slot while the lock is held (the holder's epoch), so ticket and release read the same slot's word; a
            retained hold (PREPARE to PUBLISH) carries its ticket in the publish entry to whichever path releases it
  invariant a crash leaves no time for the rewrite: a crash open frees every chain root's lock word as its table loads, before anything of the open
            takes a chain lock — the cluster HEAD rebuild walks chains, the log replay appends revisions, the scrub walks them all; each would wait for
            the holder that died with the process, and the open would fail. The component-collection segments' buffer roots are freed as those
            segments load; the index value buffers need nothing, as a crash open recreates every index empty (RB-01). A buffer's root is a chunk
            no other names as its next that its header check accepts — a buffer's first free chunk is unnamed, and its bytes at the lock's offset
            are data. A revision segment's is every chunk no other names: a root, or a chunk no chain will link (an overflow chunk whose
            predecessor's page is as of before its growth), whose bytes no one reads — filtered by a header check, a root whose overflow chunk the
            disk has as free kept its hold. A named chunk the crash rebuild of an EntityMap adopts as its entity's root is freed as it is adopted
            (#1251). Read through a scan accessor: a segment may outgrow the cache
  scope: PagedMMF.TakeCaptureTicket, PagedMMF.EndLockHold, PagedMMF.BeginCapture, PagedMMF.EndCapture, PagedMMF.WritePagesForCheckpoint,
         PagedMMF.SavePages, ComponentRevisionManager.ChainLockTicket, ComponentRevisionManager.EndChainLockHold,
         ComponentRevisionManager.ResetChainLocksAfterCrash, ComponentRevisionManager.ResetChainLocks, LockWordRecovery.ResetLocks,
         RevisionEnumerator.Dispose, Transaction.PublishComponent,
         Transaction.ReleaseRetainedPublishLocks, VariableSizedBufferSegmentBase.LockBuffer, VariableSizedBufferSegmentBase.ReleaseLockOnBuffer,
         VariableSizedBufferSegmentBase.ReadAllElementsRaw, VariableSizedBufferAccessor.Dispose, LockWordRecovery.ResetRootLocks,
         VariableSizedBufferRecovery.ResetRootLocksAfterCrash, DatabaseEngine.ResetCollectionBufferLocksAfterCrash
  on_violation: the release changes the page in memory only; once the clean page is evicted and read back, the lock is held by no one, for good,
                and a clean close — which rewrites no clean page — hands it to the next process: every writer of the entity times out on the
                chain's lock (#1253, MarketHardeningTests at a 64 MiB cache with long readers: chain-lock timeouts within 19 to 56 s of the storm).
                Moving the lock words out of the page into a per-slot shadow block fixed it too, but neighbouring chains' locks then shared
                cache lines across threads: locked walks 2x slower at 8 and 16 threads on entities 8 lines apart, with no room to pad. The first
                version of this fix reset the lock words in the recovery scrub, which runs last: a crash open then timed out on the chain's lock in
                the HEAD rebuild (an exclusive hold) or in the replay of a later update (a shared one)
  verified: ChainLockWordPersistenceTests [VerifiesRule] — a shared or exclusive walk held while a neighbour's spawn dirties the page and the
            checkpoint writes it leaves the page owed, rewritten free and read back free; the page stays cached until then; a hold that overlapped
            no capture leaves it clean; a hold overlapping a structural save is found, and the save's capture ends; a commit's retained lock (a
            conflict handler) is found at its publish; another thread's hold captured before a hard crash, with an update after it in the log, is
            free after recovery, the update replayed and the entity writable; the holds of a write's append, a rollback's void and its dispose's
            cleanup, a destroy's tombstone and a last-writer-wins resolution are found under a staged capture; the fence marks the holds a capture
            could have copied, and not the others. Mutations killed: no mark, no in-flight check, no started check, the checkpoint copy or the save
            not bracketed, a save never ending, the reset left to the scrub, the enumerator, the publish, the append, the void, the cleanup, the
            tombstone or the resolution never ending its hold, the retained ticket not carried; and, since #1284, an element lookup past the root,
            the abort path's release of retained locks and a destroyed chain's release never ending theirs. GetRevisionLocation's own hold is
            never taken: its one caller, AddCompRev, holds the chain's lock.
            BufferLockWordPersistenceTests [VerifiesRule] — a buffer read held while a neighbour's write leaves the root's page owed and the
            checkpoint writes it: read back held before the fix, (0, 1) on 3 of 3 runs; now the page stays owed, is rewritten and reads back free;
            a hold copied before a hard crash is free after recovery and the buffer writable; a write's and a raw read's holds are found under a
            staged capture. Mutations killed: the write's release, the raw read and the read accessor never ending their holds, no reset at the
            crash open. LockWordRecoveryTests [VerifiesRule] — the reset clears a held root and leaves an unnamed data chunk's bytes at the lock's
            offset untouched, in both layouts; with the root check removed both fail

### PS-21: A segment's grow holds none of its new data pages for the unit of work that grew it `[fatal]`
  invariant under a change set the checkpoint drains (ChangeSet.DrainedByCheckpoint: a unit of work's shared set — a bulk load's too — every rented
            one, a transaction's own or a fence chunk's, an entity accessor's, a cleanup drain's, and the local set of a grow with none), a grow's data pages are recorded owed a write (MarkPageModified) and take no mark: the checkpoint
            writes them, and they may leave the cache before the unit of work ends. A page that then receives data is marked by the write that puts
            it there. The new map pages and a Create's root keep the change set: they are the directory, few, and the grow publishes through them
  invariant a change set that writes its own pages (SaveChanges: bootstrap, schema evolution, a standalone segment) marks the grow's data pages as
            before — nothing else writes them; a drained set is never saved (asserted)
  invariant a grow's data page holds nothing the commit depends on before it is written to: cleared in full, its header and its forward link (PS-14);
            a crash before the checkpoint writes it is repaired forward from the directory (#1296)
  never mark a grow's data pages in the caller's ChangeSet: a segment grows by doubling, so a grow can be larger than the cache's free room, and its
        marks hold every slot until the change set ends — which it cannot, if the commit needs a slot to end
  scope: LogicalSegment.CreateOrGrow, LogicalSegment.InitDataPages, ChunkBasedSegment.GrowChunkCapacity, ChangeSet.DrainedByCheckpoint,
         PagedMMF.RentChangeSet, PagedMMF.CreateChangeSet, DatabaseEngine.CreateUnitOfWork, DatabaseEngine.CreateUnitOfWorkForBulkLoad
  on_violation: an EntityMap past half an 8 MiB cache grew inside a spawn commit's FinalizeSpawns with 1 022 of 1 024 slots marked, and the commit
                timed out on back-pressure after its partial publish — a fail-stop (AP-03). At 64 MiB the market's audit map reaches the same size.
                Dropping the marks under every change set lost a standalone segment's grow: SaveChanges wrote 11 of its 8 500 pages
  verified: ClusterVisibilitySummaryIntegrityTests.TheAudit_OfAnEntityMapLargerThanTheCache_Completes [VerifiesRule] — 240 000 spawns in commits of
            1 000 grow an EntityMap past an 8 MiB cache. Mutation killed: the grow's data pages marked in the ChangeSet again (the spawns stop).
            SegmentGrowthTests — a grow under a saved change set is read back whole

---

### PS-22: A back-pressure wait gives up only when nothing is being served `[correctness]`
  invariant a page allocation waiting on back-pressure, and the restarts of an open or a first write (EP-02), go on past
            PageCacheBackpressureTimeout one timeout at a time for as long as a page was served during the previous one, up to
            PageCacheOverloadWaitCeiling (60 s): the engine is slow, not stuck, and the work goes through (#1382, S1). A whole timeout with no page
            served is a stall, and the wait throws PageCacheBackpressureTimeoutException; so does the ceiling. A commit's publish keeps its own
            bound (AP-03)
  invariant the page cache publishes how it keeps up (StorageHealthState, PagedMMF.StorageHealth, DatabaseEngine.StorageHealth): Healthy without
            back-pressure; Pressured while the pressure flag is set; Overloaded once it has lasted OverloadedAfter (1 s); Starved once no page has been
            served for StarvedAfter (1 s) under pressure — a clock that runs only under pressure, so an idle stretch before a burst is not starvation. It escalates at once and steps down after CalmBeforeStepDown (1 s), so a burst does not flap
            it. Every change is logged (a warning entering Overloaded or Starved), raised (DatabaseEngine.StorageHealthChanged), and published as the profiler gauge
            PageCacheHealthState with the cumulative back-pressure rounds and overload extensions, and in the resource tree. Evaluated at most every
            100 ms, from the back-pressure rounds, from allocations while degraded and from the checkpoint loop's wakes: nothing on the healthy path
  invariant a commit before its append whose wait can never end is refused at once, not left to time out (#831): after
            CommitRefusalUnservedLaps (2) whole laps in which no page was served to anyone, its wait counts the slots held for good — the pages its
            own change set marks (PagedMMF.CommitMarks, set for the commit's length), every page tagged since its pin when that pin is the cache's
            floor (a wait that keeps its pin never moves the floor), and the pages held only by slot references or a live chunk writer, which carry no
            owner and which a thread still working would have let go of within those laps. When they fill every slot but the reserve's, it throws
            TransactionExceedsPageCacheException (StorageCapacityExceeded, not transient), and the commit rolls back. Debt alone and other owners'
            marks count as able to come back, so the check errs toward the wait. Never past the append (AP-03), nor in a wait that moved its epoch
  scope: PagedMMF.OverloadBudget, PagedMMF.ServedUnderPressure, PagedMMF.OverloadWaitExtensions, PagedMMF.EvaluateStorageHealth, PagedMMF.StorageHealth,
         TimeoutOptions.PageCacheOverloadWaitCeiling, StorageHealthState, DatabaseEngine.StorageHealth, DatabaseEngine.StorageHealthChanged,
         PagedMMF.CommitMarks, PagedMMF.ThrowIfTheCommitHoldsTheCache, PagedMMF.CommitRefusalUnservedLaps, TransactionExceedsPageCacheException
  verified: StorageOverloadTests [VerifiesRule] — the budget: its first timeout, an extension per timeout with a page served, none without, the
            ceiling, no grace period past a caller's own timeout; an open, and a page request outside one, held on back-pressure for three timeouts
            go through while pages are served and give up while it lasts when none are; the state escalates Healthy, Pressured, Overloaded,
            Starved and steps down once calm, raising the public event; an engine falling idle after a burst steps back to Healthy, never Starved.
            Mutants killed: no extension (the served cases fail), an extension without progress (the unserved cases), the main loop's extension
            removed (the page request), the starved clock running while idle (the idle case)
  verified: CommitExceedingThePageCacheTests [VerifiesRule] — 100 000 spawns committed at once into an 8 MiB cache are refused in under a second, at
            the default 5 s timeout: the exception holds the cache's 1 024 pages, nothing is left marked or visible, and the next commit goes through;
            40 000, which fit, commit and none is refused. Without the check, the commit waits its timeout out and throws a back-pressure timeout
  on_violation: a commit too large for the cache waited out its back-pressure timeout — 25 s for 100 000 spawns into 8 MiB — and threw one that
                advised running fewer concurrent transactions, with one running;
                a wait that throws while the cache is serving pages fails work the engine would have completed a moment later — under a 16-writer
                storm over 64 MiB, the timeouts that made 35-45 % of the market's runs fail; a state never published leaves an application that
                gave the engine too little cache or disk with slowness and no reason

---

### PS-23: Work is paced at its start, holding nothing, and never refused for being slow `[correctness]`
  invariant while the storage gate is on (AdmissionLimit > 0), DatabaseEngine.CreateUnitOfWork admits a unit of work only once fewer than the limit
            run, waiting before it creates its ChangeSet or claims a registry slot: no pin, latch or lock is held, and no append has happened (#1382, S4)
  invariant a thread already inside an epoch scope is never held at the gate: it would wait with its pin, protecting the pages every other thread tags;
            nor is a thread that holds an admitted unit already: it would wait holding that unit's marks and registry slot, at a limit of one for
            itself. Units made outside CreateUnitOfWork (a bulk load's) are not gated
  invariant past its own deadline (the unit of work's timeout) a waiting unit is admitted all the same: the gate slows work, it never refuses it
  invariant every admitted unit is released exactly once, at its dispose or when its creation throws; the limit is never below one, so the gate alone
            never stops the engine
  invariant the limit follows the health state at each evaluation (S5): off when Healthy; Overloaded turns it on at three quarters of the units
            running and holds it, never below a quarter of the cores; Starved halves it, down to one, once what runs is within it; Pressured
            raises it by a quarter. Every change is logged and published as the gauge
            PageCacheAdmissionLimit, with PageCacheAdmissionWaitsTotal
  scope: PagedMMF.Admit, PagedMMF.ReleaseAdmission, PagedMMF.AdjustAdmission, PagedMMF.AdmissionLimit, DatabaseEngine.CreateUnitOfWork, UnitOfWork.Admitted
  verified: StorageAdmissionTests [VerifiesRule] — a unit waits at a full gate until one ends; a thread inside an epoch scope, and a thread holding
            a unit, pass a full gate; past its deadline a unit is admitted; the limit's steps per state, and every unit released at its dispose.
            Mutants killed: no epoch-scope bypass, no held-unit bypass, no deadline admit, no release at dispose
  on_violation: a unit made to wait while it holds a pin keeps the cache epoch-held for the work already running — the stall the gate exists to
                prevent; a unit refused for slowness fails work the engine would have completed

### PS-24: A wait that holds the cache's floor is served from a reserve `[correctness]`
  invariant a page cache of SlotReserveMinPages (1,024) pages or more keeps one slot in SlotReserveDivisor (32), at most SlotReserveMaxSlots (256),
            out of every sweep (PageState.Reserved). An allocation that would wait on back-pressure with its thread's epoch pinned, its wait not
            moving the epoch (EP-02), takes a reserved slot instead and is served at once; a wait that moves its epoch never takes one
  invariant the last 1 / SlotReserveFloorShareDivisor (a quarter) of the reserve is drawn only by the thread whose pin is the cache's floor
            (MinActiveEpoch): a younger pin can be served once the floor moves, the floor-holder by nothing a sweep finds, so younger waiters never
            empty the reserve before it
  invariant while the reserve is short, an allocation that finds a slot on its first try gives it to the reserve and looks for another; a slot that
            ends a back-pressure wait serves its waiter. A reserved slot counts as free (the gauges and FreeMemPageCount agree), holds no page, no
            epoch tag and no debt, and under the eviction guard it has sat out its quarantine before the reserve takes it
  invariant that waiter holds the cache's floor: no page tagged after its pin can be evicted, and every thread then touching pages tags them after
            it, so on an epoch-held cache nothing a sweep can find frees it; served, it finishes and its epoch moves. Waits that hold their pin
            are the publish's B+Tree writes past their first latch (EP-02), the checkpoint's meta persist, a transaction's own reads, a compaction
            under its chain lock; served first, they cannot hold every other wait behind them for a timeout. The reserve emptied, they wait as
            before
  invariant a thread holding a lock that pinned threads wait on draws as the floor-holder does (PagedMMF.FloorInheritDepth), its share included and
            whatever its own pin or a refresh of it: a segment's grow (ChunkBasedSegment.GrowChunkCapacity) holds its grow lock while it reads and
            creates pages, and every allocation of that segment that found no room waits on the lock, publishes among them with pins at the floor.
            They wait on the lock, not on a page, so they never draw; the grow, a younger pin, would wait for its pages behind their pins
  scope: PagedMMF.TryDrawReserve, PagedMMF.TryStockReserve, PagedMMF.SlotReserveTarget, PagedMMF.ReservedSlotCount, PagedMMF.ReserveDraws,
         PagedMMF.AllocateMemoryPageCore, PagedMMF.FloorInheritDepth, ChunkBasedSegment.GrowChunkCapacity
  verified: SlotReserveTests [VerifiesRule] — a cache under 1,024 pages reserves nothing; the first allocation fills the reserve and 4,096 more
            never take a reserved slot, every slot counted free or serving; with every round finding nothing, a pinned request is served at once
            from the reserve, which the next allocation refills, and with no reserve the same request times out; a wait that moves its epoch draws
            nothing; a younger pin draws down to the floor's share and then waits, and the floor-holder draws from it; a slot that ends a wait
            serves its waiter, not the reserve; under the eviction guard every reserved slot that held a page was quarantined first; a lock holder
            under FloorInheritDepth draws the floor's share on its first round, and SlotReserveGrowTests' segment grow, a younger pin behind the
            floor with the reserve down to that share and every page forced to wait, completes from it. Mutants
            killed: no draw (the pinned request times out), a draw for a wait that moved its epoch, no floor share, a waiter's slot stocked, a slot
            stocked unquarantined, a stock or a draw left out of the free count, a grow that does not inherit the floor, a draw that ignores
            the inheritance; [RuleMutant] Mutant_WithNoReserve_APinnedWaiterTimesOut
  on_violation: the 32 MiB market (16 threads) failed every run of nine: after the storm, the checkpoint's CheckpointLSN flip (PersistMetaNow)
                waited for the meta page with its pin, 4,399 epochs behind, and every reader timed out behind it; during the storm, publishes waiting
                past their first latch, their pins up to 4,000 epochs behind, held the whole cache. With the reserve, 15 of 15 runs pass and the
                longest back-pressure wait is 68-352 ms. Without the inheritance, ChaosStressTests.UltimateStress_AllSubsystems at the 8 MiB floor
                timed out about one run in six on four Linux cores: a grow waiting for a page, the publishes blocked on its lock 170,000 epochs behind

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

### WR-03: Every written record lies inside its segment's declared size `[fatal][silent]`
  context a drain is written at `segment.WriteOffset` and the offset advances by the padded length. A positioned write
    past EOF simply EXTENDS the file (the segment is opened FILE_FLAG_NO_BUFFERING|WriteThrough with sector-aligned
    offsets and lengths), so nothing fails — the file just grows past what its header declares. But
    `WalSegmentReader.OpenSegment` computes its data region as `header.SegmentSize - WalSegmentHeader.SizeInBytes`,
    so any byte past the declared size is UNREACHABLE to recovery.
  invariant before writing a drain of `n` padded bytes, `WalWriter.WriterLoop` requires
            `activeSegment.WriteOffset + n <= activeSegment.SegmentSize`; when it does not hold it rotates FIRST
            (`WalSegmentManager.ActiveSegmentCanHold` → `RotateSegment(..., minDataBytes: n)`) and only then writes
  invariant the rotation target is sized to hold the batch: `RotateSegment` allocates
            `max(SegmentSize, AlignUp(header + minDataBytes))`, so a batch larger than a WHOLE configured segment
            still lands inside a declared region rather than overflowing a fresh one
  invariant the header declares the segment's OWN size, not the configured `SegmentSize` — segments are not uniformly
            sized (the first segment of a lifecycle uses `InitialSegmentSize`; an oversized batch gets a bespoke size),
            and recovery bounds its scan by what the header says
  invariant `ActiveSegmentUtilization` divides by the ACTIVE segment's declared size, never the configured one — a
            16 MiB segment measured against 64 MiB reaches the 75% rotation threshold only at 48 MiB, which it cannot hold
  scope: WalWriter.WriterLoop, WalSegmentManager.ActiveSegmentCanHold, WalSegmentManager.RotateSegment,
         WalSegmentManager.ActiveSegmentUtilization, WalSegmentManager.WriteSegmentHeader, WalSegmentReader.OpenSegment
  on_violation: a drain larger than the room left is appended past the declared end → the records are durable, acked,
    and silently dropped at replay (measured: a segment declaring 65,536 bytes grown to 114,688 — 49,152 bytes lost)
  note the pre-#785 code checked rotation only AFTER writing (utilization >= 75%), which cannot prevent an overrun by
    construction. Safety rested on `(1 - threshold) * SegmentSize` happening to exceed the commit-buffer half — an
    accident of two defaults in DIFFERENT option classes (`WalWriterOptions.SegmentSize`,
    `ResourceOptions.WalRingBufferSizeBytes`) that know nothing about each other, and one that the shipped defaults
    ALREADY failed (16 MiB of worst-case headroom against a 32 MiB maximum drain). Fit-driven rotation removes the
    coupling instead of documenting it, which is what makes a variable `InitialSegmentSize` (#784) safe.
  verified: WalSegmentSizingTests [VerifiesRule]

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
  pre the chunk summary written and fsynced (CS-05) — it describes the data file as that flush left it
  post MarkCleanShutdown: BK_CleanShutdown = 1 and the summary's nonce, in ONE meta flip with its OWN fsync (never bundled with
       the data flush)
  invariant runs only on graceful Dispose, after final checkpoint → PersistArchetypeState → PersistEngineState (CPO-06), and only
            for an engine whose open completed (CS-04)
  scope: DatabaseEngine.Dispose, DurabilityWatermarks.MarkCleanShutdown, ManagedPagedMMF.PersistMetaNow
  requires: CK-05 — the flag rides the meta-pair generation flip, so its durability is atomic and torn-slot
            detected. That is STRONGER than this rule originally claimed.
  note the bootstrap key `BK_CleanShutdown` this module used to name is DEAD — the const is declared and never
       read or written. Live storage is the packed DurabilityWatermarks value.
  on_violation: flag durable ahead of the cluster pages it vouches for → a torn close lets the next open trust
    stale HEADs and skip the repair rebuild → silent stale reads

### CS-02: Clean-shutdown flag cleared on open before any mutation `[fatal][silent]`
  pre bootstrap loaded; flag value captured for the trust decision (ctor loading path)
  post the ctor's loading path (InitializeUowRegistry): flag cleared + fsync, before the engine accepts any write and before
       registration can migrate a schema (#583) — after the chunk summary has been adopted (CS-05)
  invariant the clear is the authoritative dirtying step — a session that mutates then crashes leaves the flag = 0
  scope: DatabaseEngine.InitializeUowRegistry, DurabilityWatermarks.BeginOpenAfterCleanClose
  on_violation: flag survives an unclean session → next open trusts stale HEADs

### CS-03: HEAD rebuild skipped iff the clean flag was set `[fatal]`
  invariant trusted ⇔ (BK_CleanShutdown == 1 at open ∧ no component migrated this session) — independent of CheckpointLSN
  invariant trusted ⇒ skip RebuildVersionedHeadFromChain (persisted cluster-slot HEADs are current)
  invariant ¬trusted ⇒ rebuild runs exactly as before (the crash-window repair path is preserved)
  scope: DatabaseEngine.InitializeArchetypes, ArchetypeClusterState.RebuildVersionedHeadFromChain
  requires AP-04 — "the flag says clean, therefore the HEADs are current" holds only if every published HEAD owes its page a write;
    before #1159 a Versioned update on a page nothing else had dirtied was never written, and this rule trusted it
  on_violation: skipping when not provably clean → stale HEAD served from the cluster slot

### CS-04: An open that did not complete closes as a crash `[fatal][silent]`
  invariant an engine whose InitializeArchetypes threw is disposed as a crash would leave it: no PersistArchetypeState, no
            PersistEngineState, no chunk summary, no MarkCleanShutdown, and no final checkpoint cycle (the checkpoint manager is
            crash-stopped). CS-02 cleared the flag at open, so the next open recovers
  invariant an open that follows a clean close (or an open that never completed after one) sets the failed-open marker in the same
            meta flip that clears the clean flag (DurabilityWatermarks.BeginOpenAfterCleanClose), and only a completed
            InitializeArchetypes clears it (ClearFailedOpen). An open that dies anywhere in between — constructor, InitializeArchetypes,
            or killed — leaves it set, and the next open loads its segments without tolerating a torn one: the WAL holds nothing past
            the clean close, so a segment that fails to load is damage to refuse, not a crash to refill (RB-01)
  invariant DatabaseEngine.TolerateTornSegmentsAtOpen = CrashRecoveryAtOpen ∧ ¬clean flag at open ∧ ¬marker: a repair's forced
            recovery open (CS-06) after a clean close refuses a damaged segment too, for the same reason
  invariant a repeat InitializeArchetypes disarms the checkpoint's segment-pointer persistence until it completes
  invariant a close that leaves chunks in no chain — frees still held (CK-17), chunk frees or destroyed entities left queued, a drain that threw, a
            session whose cleanup threw holding frees (REAP-02) — marks the sweep owed where the clean marker would go
            (DurabilityWatermarks.MarkSweepOwed): the failed-open marker without the clean flag. The next open recovers, so its orphan sweep frees
            them, and loads its segments strictly: the close flushed the data file whole, and the WAL holds nothing to refill a segment that fails
            to load. Without the marker, that open tolerated torn segments and replaced one that failed to load with an empty one (#1268 review)
  rationale: hosts (DI, DatabaseEngine.Open) dispose an engine whose initialization threw, and the dispose ran the full close: it
             persisted the archetype table as far as the open had built it — an archetype whose cluster state was never built was
             written with ClusterSegmentSPI = 0, orphaning every entity it held — and set the clean flag over it, so the next open
             trusted all of it (#1147). Any exception in InitializeArchetypes did it: back-pressure, a schema error, an I/O fault.
             The final checkpoint cycle could do the same through its segment-pointer hook. And closing as a crash alone was not
             enough: the retry of an open that failed on a damaged segment replaced the segment with an empty one and opened. The
             marker was first cleared at the start of the open and written again only by the close of an open that failed in
             InitializeArchetypes, so a kill or a constructor failure erased it; and a forced repair open after a clean close
             tolerated a torn segment, emptying it while the repair reported success
  scope: DatabaseEngine.InitializeArchetypes, DatabaseEngine.DisposeCore, DatabaseEngine.TolerateTornSegmentsAtOpen,
         DurabilityWatermarks.BeginOpenAfterCleanClose, DurabilityWatermarks.ClearFailedOpen, DurabilityWatermarks.MarkSweepOwed,
         DatabaseEngine.MarkSweepOwed
  on_violation: silent loss of every entity of the archetypes the failed open had not reached, behind a clean flag; or, on the
                retry, of every entity of a damaged segment
  verified: HealthyOpenTests.AnOpenThatFailsHalfway_ClosesAsACrash_AndLosesNothing (fails the open between two archetypes through
            DatabaseEngine.ArchetypeClusterInitProbe), HealthyOpenTests.ADamagedClusterSegment_IsRefusedOnTheRetry_AndCompletedAfterACrash (the
            retry of a failed open refuses; after a real crash the chain is completed at load, CS-08, its entities kept — it used to be replaced),
            HealthyOpenTests.AnOpenKilledBeforeItCompletes_LeavesTheMarker_AndTheNextOpenRefusesADamagedSegment,
            HealthyOpenTests.ARepairOpenAfterACleanClose_RefusesADamagedClusterSegment_InsteadOfEmptyingIt [VerifiesRule],
            RevisionChunkRetentionTests.ACloseAfterACleanupLostFrees_MarksTheSweepOwed_AndTheNextOpenSweepsStrictly [VerifiesRule]

### CS-05: The chunk summary is trusted only after the close that wrote it `[fatal][silent]`
  invariant a clean close writes {bundle}/chunk-summary — every chunk segment's allocated count and page room bits, every
            archetype's active-cluster list and free-cluster head — after PersistEngineState's data fsync, with a fresh random
            nonce; the nonce reaches the data file in the same meta flip as the clean flag (CS-01). A close that cannot write the
            file, for any reason, records nonce 0, which nothing matches. Clearing the flag clears the nonce in the same flip
  invariant an open adopts the file iff the clean flag was set at open ∧ the nonces agree ∧ the header and payload CRC32C hold ∧
            every entry is one a close can write (cluster ids ascending from 1, the head among them, no root twice); otherwise every
            load scans. Each segment entry is used only when it fits the segment just loaded (same page count, count within
            capacity), and a cluster list only together with its segment's own entry and within its allocated count and capacity
  invariant nothing checks a summary's content against the pages: a summary that passes every guard above but is wrong — a bug in
            the capture — is trusted. Deliberate: the chunk bitmaps stay authoritative for allocation, so a wrong count never reuses a
            chunk; a page recorded with room it lacks is cleared by the first allocation to look at it (the bits are a superset).
            What a wrong count still misleads is AllocatedChunkCount's readers, and the exposure the pages cannot catch is the
            active-cluster list, which ASummaryLoadedOpen_HoldsTheStateAScanWould pins
  invariant the summary is consumed by the open only and released when InitializeArchetypes ends, completed or not; the pages stay
            the truth. Repair deletes the file once, after its backup and before its first offline write; a regeneration step's own
            clean close then writes a fresh one, which describes the file as that close left it
  rationale: the count and the free list were rebuilt by reading every page of every chunk segment, and the active-cluster list by
             reading every cluster's occupancy word — most of the file on every open, each page pinned by a bare epoch tag, so a
             segment larger than the cache deadlocked the open (#1143). The summary replaces those reads; the nonce and the flag make
             a stale, copied-in or damaged file cost a scan, never a wrong count
  scope: DatabaseEngine.WriteChunkSummary, DatabaseEngine.AdoptChunkSummary, DatabaseEngine.TakeClusterSummaryFor,
         DurabilityWatermarks.MarkCleanShutdown, DurabilityWatermarks.BeginOpenAfterCleanClose, ChunkSummaryFile.Parse,
         ChunkBasedSegment.Load, ChunkBasedSegment.AllocateChunkInternal,
         ChunkBasedSegment.CaptureSummary, ArchetypeClusterState.CreateFromExisting, ArchetypeClusterState.CaptureClusterSummary,
         DatabaseRepair.Repair
  on_violation: a summary from another state of the file is trusted — allocated counts wrong, so the segment grows instead of
                reusing its free chunks; clusters holding entities missing from the active list and so from every iteration
  verified: HealthyOpenTests.ASummaryLoadedOpen_HoldsTheStateAScanWould, HealthyOpenTests.ASummaryThatDoesNotDescribeTheFile_IsNotUsed
            (unclean close, a stale file whose counts really differ, damage, missing file),
            HealthyOpenTests.AnEntryThatDoesNotFitItsSegment_IsScanned_AndItsClusterListIgnored,
            ChunkSegmentSummaryLoadTests.ASummaryWithAShortCount_GrowsAFullSegment_InsteadOfSpinning [VerifiesRule];
            ChunkSummaryFileTests (the format)

### CS-06: Crash recovery runs iff the last close was unclean `[fatal]`
  invariant CrashRecoveryAtOpen = WAL segments on disk ∧ ¬clean flag at open, and every crash-path gate reads it: RB-01's index
            clear + rebuild and EntityMap rebuild, RecoverySuspect checksum mode, WAL replay, CK-09's occupancy re-derive. The
            tolerate-torn segment loads read TolerateTornSegmentsAtOpen, which is the same flag minus a failed open (CS-04)
  invariant repair forces it (DatabaseEngineOptions.ForceCrashRecoveryAtOpen) and then ignores the chunk summary: the rebuild net
            is what regenerates derived structures, and it runs on the crash path only. OPEN (#1179): the force takes effect only
            when WAL segments are on disk; on a bundle without any, the forced open rebuilds nothing
  invariant the ctor's WAL scan still runs whenever segments exist: it reads the log, not the data file, and gives LOG-08 its
            LSN frontier
  rationale: the flag used to mean "WAL segment files exist", which a clean close leaves true, so every reopen of a database with
             a disk WAL ran the whole recovery pipeline — replay, scrub, rebuilds, seal, re-derive — over a file CS-01 had already
             made current (#1143). Repair-by-reopen silently relied on that
  scope: DatabaseEngine.InitializeUowRegistry, DatabaseEngine.RunWalV2Recovery, DatabaseEngine.RederiveOccupancyOnCrash,
         DatabaseEngineOptions.ForceCrashRecoveryAtOpen
  on_violation: (narrower) a crash is not recovered — torn derived structures trusted; (wider) every clean open reads the whole
                file; (repair) a regeneration that rebuilds nothing and reports success
  verified: HealthyOpenTests.WalFilesOnDisk_RunRecoveryOnlyAfterAnUncleanClose,
            HealthyOpenTests.ARepairOpen_RunsRecoveryAfterACleanClose_AndIgnoresTheSummary [VerifiesRule]

### CS-07: A clean open reads no data page `[perf]`
  invariant after a clean close (CS-05 summary adopted), an open reads segment directories, the bootstrap and the schema — not the
            pages of the chunk segments: every chunk segment loads its allocator from the summary, every cluster state its active
            list, and no chain is walked (LogicalSegment.Load reads the directory only; the chain cross-check runs on the crash
            path inside the scan, and offline at Quick depth)
  exception the spatial cell layer of a cluster-spatial archetype is still rebuilt from every active cluster at every open
            (RebuildSpatialStateFromData), O(entities) and under one epoch — its persistence is #1148
  rationale: a 1.9 GB database read 251 053 pages (13 s) per open and could not open at all with a cache smaller than one segment
  scope: ChunkBasedSegment.Load, LogicalSegment.Load, ArchetypeClusterState.CreateFromExisting
  on_violation: open cost grows with the database instead of with its schema; a database larger than its cache cannot open
  verified: HealthyOpenTests.ACleanReopen_ReadsNoDataPage (disk reads during the open under a tenth of the file),
            HealthyOpenTests.ADatabaseLargerThanItsCache_ReopensCleanly (a cache smaller than one segment) [VerifiesRule]

### CS-08: A segment whose directory and chain disagree is refused unless recovery rebuilds or completes it `[fatal]`
  invariant the open no longer cross-checks each segment's directory against its forward chain (CS-07). Open-time verification at
            Quick depth and deeper does (CHK-SEG-05, counted from pointers its sweep records, compared position by position), and
            refuses the open on a disagreement after a clean close, and after an unclean one for every kind but the three the
            crash path replaces and refills from the WAL — cluster, entity map, index (DatabaseIntegrityException.RefusesOpen)
  invariant the Spine tier, on by default, reads directories only and says so (CHK-SEG-05 skipped). On the crash path a chunk-based
            segment checks its links during the free-chunk scan (ChunkBasedSegment.ScanForAllocatorState); plain logical segments
            (occupancy map, UoW registry) are checked offline only
  invariant a crash open that may refill from the WAL (TolerateTornSegmentsAtOpen) completes a chunk segment whose chain stops short of its
            directory — a grow whose directory append reached the disk and whose old tail's link did not (#1296) — instead of refusing or
            replacing it, when every directory page is a resolved CK-05 pair (a directory torn itself is not trusted to name the pages): the link
            patched, each following page kept when it carries the segment's incarnation (a random 32-bit value fixed at the segment's creation and
            written on every page it initializes, SegmentGeometry bytes [58,62)), its segment flag and a valid checksum, initialized empty
            otherwise. A page the grow had not written holds only what came after the last completed checkpoint's barrier, which the replay
            brings back; a page it had written may hold data no log window does — an old lost write of the link alone — and is kept. A suspect
            page (its checksum failed) is judged by its first sector, which holds the header and the incarnation. Past the disagreeing page it is the
            grow's, written after the barrier, and is initialized. The disagreeing page itself, torn and this segment's, may hold a checkpoint's
            data: the grow is not completed and the segment is refused — never emptied with its suspicion cleared, and never patched, which dirtied
            it for a cycle to re-checksum its torn bytes. A directory's last page whose link agrees is checked too: one the grow had not written
            had no header and another owner's bits, and is initialized. A completion that initialized the page holding chunk 0 (the null id)
            reserves it again
  invariant DELIBERATE (#1303): a torn page past the tear is taken for the grow's. Under an old lost link — the double fault — it was a
            checkpointed page, and its data is lost silently; kept suspect instead, a single torn grow made an open that could not complete. The
            exact rule needs each page's creation generation
  invariant a crash open claims in the occupancy bitmap every page its persisted segments own (DatabaseEngine.BuildOwnedPageBitmap) right after
            the schema, the archetype catalog read with it, before any registration can allocate, and every page a crash-path segment load finds
            named: the bitmap on disk may predate the grow, and the open's own allocations took its pages for a second segment before CK-09's
            re-derive. Read later, in InitializeArchetypes, the catalog came after a component registered before it had taken an archetype's
            pages, and the archetype's committed entities were gone with the open silent (#1163). A persisted root past the file's end — a
            checkpoint persisted the pointer and died before the root landed — is claimed too: unclaimed, an allocation made it another segment's
            page, and the load read that page as the root. A directory is read for a claim through its resolved CK-05 pair
            (ManagedPagedMMF.EnsureDirectoryPairsResolved), never its primary slot: read before its load,
            the older generation stayed cached and the load found the directory as of before its last grow — the chain ahead of it, refused
  invariant the opposite tear — the directory's last page, written as the segment's, linking past it: the chain ahead of the directory — is
            ended where the directory ends when the page it links to was never written as the segment's (past the file's end, another
            incarnation or none): a cycle that took the old tail before a grow and not the directory wrote it, and the grow's pages, never
            checkpointed (CK-16), hold only what the log window holds (#1299). Onto a page written as the segment's — an old lost directory
            append — it is refused. A Versioned head naming a content chunk past its segment's end (a revision on such a page) is skipped by the
            open's HEAD rebuild (VersionedHeadRebuildSkips.HeadPastSegment) and brought back by the replay
  invariant open-time verification at Quick depth and deeper still refuses such a segment of a kind the crash path does not replace: the
            finding does not say which way the chain disagrees, and the refusal is the conservative reading
  rationale: the check used to run in LogicalSegment.Load on every open, which read every data page (#1143). Moving it offline kept
             its strength only if a finding still refuses what the open used to refuse; rating it a mere divergence would let a
             clean database with a lost write open with pages its segment can no longer address
  scope: DatabaseIntegrityException.RefusesOpen, SegmentChecks.Run, SegmentWalker.WalkSegment, IntegrityScanner.Scan,
         ChunkBasedSegment.Load, ChunkBasedSegment.ScanForAllocatorState, LogicalSegment.RepairTornGrow, SegmentGeometry.ReadIncarnation,
         ManagedPagedMMF.ClaimSegmentPagesAtLoad, ManagedPagedMMF.ClaimOwnedPagesAtOpen, ManagedPagedMMF.EnsureDirectoryPairsResolved,
         LogicalSegment.NeverWrittenAsOurs, ArchetypeClusterState.RebuildVersionedHeadFromChain
  on_violation: a lost write found offline is ignored by the open, which serves a segment that cannot address all its pages
  verified: TornCaptureRecoveryTests — AChainAheadOfItsDirectory_IsEndedWhereTheDirectoryEnds, AChainAheadOntoAPageWrittenAsTheSegments_IsRefused,
            AContentChunkPastATruncatedSegment_IsTorn_AndTheReplayBringsItBack (spawns of two transactions interleaved, committed in reverse)
            [VerifiesRule] (#1299). Mutations killed: no truncation, every page past the directory taken for unwritten, no HEAD-rebuild guard
  verified: IntegrityScannerTests.AShortenedChain_IsFoundAtQuick_AndRefusesTheOpenUnlessRecoveryRebuildsIt [VerifiesRule];
            IntegrityScannerTests.QuickTier_ReportsAChainThatLoopsBackOnItself_AsFatal; TornCaptureRecoveryTests — a grow whose old tail missed
            the disk is completed at a crash open, its pages claimed (without the claims the open's index rebuild took one of them); a grow whose
            first new page missed the disk is completed from that page; a page of another incarnation is initialized; an old lost link over
            checkpointed pages keeps them (initialized empty, the items on them were gone); a torn old tail fails the open loudly (it was emptied,
            silently); a torn page of the grow is initialized and the open completes; an unwritten last page is initialized; chunk 0 is reserved
            again after its page is initialized [VerifiesRule]; PostAppendFailureTests.BackpressureOutlastingThePublishLimit_StopsTheEngine_AndARestartRecoversTheSpawns
            (a claim over an unresolved pair failed this reopen);
            TornCaptureRecoveryTests.AComponentRegisteredBeforeTheArchetypesLoad_DoesNotTakeTheirPages_FromAStaleOccupancyPage (#1163; red with
            the catalog read in InitializeArchetypes) [VerifiesRule]; WalCrashSweepTests.PageAxis_LifecycleChurn_N4 (the root past the file's end;
            red without its claim). NOT mutation-proven: the directory's trust — its trigger, a torn map-extension page, needs a segment past the
            root's ~2 000 page indices

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
  pre  Step 2: ForceCheckpointAndWait — released only by a cycle that started after the call and covered it (CK-12)
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
            page holds chunk-id POINTERS (overflow links; a bucket count that addresses buckets by position, EMAP-01) —
            trusting it dereferences garbage into a hard process crash
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
    The spatial cell layer is rebuilt the same way, after apply (DatabaseEngine.RebuildSpatialLayerAfterRecovery, #1054):
    the only rebuild before it runs in InitializeArchetypes, BEFORE the WAL window is applied, and a crash before the
    first checkpoint leaves the cluster segment empty there — every replayed spawn was then invisible to spatial queries.
    Every rebuild files a cluster in the realm its data names (Realms C1d/C5): an unkeyed archetype's in realm 0, a realm-keyed one's
    in the realm of its first entity whose [RealmKey] is valid, created in the serial reduce; other-realm slots go to the first fence
    and invalid keys are reverted (RM-06); a cluster with no valid key refuses the open (RLM-01)
    (ArchetypeClusterState.RebuildSpatialStateFromData).
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
    so the crash flag — consulted in six places, `CrashRecoveryAtOpen` since #1143 (CS-06) — reached none of it. Now the same shape as the flat home: the init
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
    it past that point. Running it earlier dereferences a torn EntityMap's chunk-id pointers into a hard process crash,
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
    re-created in-window and scrub freed the old chunk) ⟹ healed. NEVER a silent open over corrupt primary data. A chunk recovery freed is held
    (CK-17) and still allocated until the seal: "live" is allocated and not held (ChunkBasedSegment.IsChunkLive), or a page the scrub emptied
    could never heal
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
                                        max TSN over surviving SCRUBBED CHAIN HEADS,
                                        max commit stamp on any TxCommit scanned )
  note the fourth term (VIS-03): a commit stamp is drawn from the TSN counter at the commit point, after every TSN its
       records carry, so a commit whose stamp is the counter's last draw leaves no other trace of it. Every scanned
       TxCommit counts, at or below the checkpoint too.
  note 🔴 the third term is load-bearing and was missing from this rule until 2026-07-28. A consolidating
       checkpoint can advance committed TSNs into the data file WITHOUT leaving them in the WAL window, and
       BK_NextFreeTSN is refreshed only on clean shutdown — so on a hard crash the first two terms can both land
       BELOW the newest consolidated revision, and every post-recovery reader then snapshots beneath it while
       MVCC hides the latest value. PS-07 depends on this rule for exactly that reason.
  scope: DatabaseEngine.ScrubVersionedChains (tracks the max surviving head and calls SetNextFreeId — the
         DECISIVE restore), RecoveryDriver (watermarks the replayed window and every scanned commit stamp)
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
       | VSBS buffer free-list | the segment's own chunk bitmap (durable to the checkpoint), found at open through the bootstrap stride → root map, which is persisted when a segment is created (`GetComponentCollectionSegment`, #1283) | ✅ #770 — before #1283 the map reached disk only at a clean close, so a crash reopened a fresh empty segment that re-issued live handles |
       | Cluster slot cursors | derived from the cluster occupancy at rebuild | ✅ |

       The open row is the same defect shape as #697, on another allocator. #697's own acceptance asked
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

## Module: BKP — Online backup capture

A backup point is a frozen image of the data file plus the WAL tail that makes it consistent (ADR-075; design
`design/Durability/PitBackup/`, implementation plans `impl/1412-capture-plan.md` and `impl/1413-store-restore-plan.md`). This
module covers capture (#1412), the store and restore (#1413: BKP-05, BKP-08) and WAL archiving (#1418: BKP-10, BKP-11).

### BKP-01: Every data-file write is tracked and gated `[silent]`
  invariant ∀ engine write w to the data file: w is issued by PagedMMF.IssueDataFileWrite or
            PagedMMF.IssueDataFileWriteAsync, w holds the DataFileWriteGate shared from before its change-map update until
            w has completed (async: until the SavePages batch's writes have all completed), and the PageChangeMap bit of
            every page of w is set BEFORE w is issued
  invariant Storage/ contains exactly two RandomAccess.Write*/WriteAsync call sites, one inside each of those methods
  invariant the map and the gate exist before the PagedMMF constructor's first write (field initialisers; genesis writes
            run from CreateFile inside the constructor)
  scope: PagedMMF.IssueDataFileWrite, PagedMMF.IssueDataFileWriteAsync, PagedMMF.SavePages (batch token, exited in the
    continuation or in the issue loop's catch), PagedMMF.BeforeDataFileWrite, PageChangeMap.Set, DataFileWriteGate.EnterShared,
    DataFileWriteGate.ExitShared, DataFileWriteTracking (ops escape hatches, warned at open: TYPHON_DATAFILE_TRACKING=0 turns the
    map off, every point is then full, never wrong; TYPHON_DATAFILE_GATE=0 also drops the gate and copy-before-write, and online
    capture is then refused, BackupCoordinator.CaptureAsync)
  on_violation: a write outside the map → its page is missing from the next incremental → a restore serves a stale page with
    no error; a write outside the gate → an image torn at the freeze
  note the gate is a dedicated writer-preferring striped primitive, not AccessControl: AccessControl's shared count is 8 bits
       (Release overflow is silent) and AccessControlSmall would starve a pending freeze behind a stream of wave writes
  verified: DataFileWriteChokepointGateTests.Storage_HasExactlyTheTwoChokepointWrites (+ the merge gate's invariants step),
            ChokepointCompletenessTests.EveryWriteOfALifecycle_IsInTheMap (witness = the data file's bytes before and after
            each session: create, crash, recovery open with a schema migration, clean closes), with
            ChokepointCompletenessTests.Mutant_ARawWrite_IsMissingFromTheMap and
            DataFileWriteChokepointGateTests.Mutant_AThirdWriteSite_IsReported; the gate itself:
            DataFileWriteGateTests.Stress_NoWriterIsEverInsideAnExclusiveHold (two freezers × 2 500 exclusive holds against
            four writers; source mutant seen red: a shared enter that does not re-check the freeze)
  spec: rules/tla/BackupCapture.tla (S6) — ChangeMapComplete; mutants -mutant-mapafterwrite, -mutant-nogate

### BKP-02: A captured point is the data file at its freeze `[silent]`
  invariant ∀ point X that completes: ∀ p ∈ [0, N_X) held by X: X's image of p = the bytes of the data file at F_X (zeros
            for a page never written); a full point holds every p ∈ [0, N_X); an incremental holds B_X ∪ {0, 1}, where
            B_X ⊇ the pages written in (F_{X-1}, F_X)
  invariant F_X is an instant with no data-file write in flight (the write gate held exclusive); N_X = the file's PHYSICAL
            length then (the tracked size lags an async batch until its fsync); C_X is read from the meta pair copied
            under the freeze, with the slot selection an open makes (BootstrapReader.Read over FrozenMetaPageSource)
  requires: BKP-01, BKP-03
  scope: BackupCapture.Freeze, BackupCapture.Scan, BackupCapture.BeforeWrite, PagedMMF.TryEnterWriteGateExclusive,
    PagedMMF.DataFileLengthUnderGate, PagedMMF.SwapChangeMap, PagedMMF.InstallWriteHook, FrozenMetaPageSource
  on_violation: a restore yields a data file that no crash could have produced (pages from different instants), and
    recovery over it is unproven; a short N drops pages an async batch already wrote
  verified: FreezeTests.FullThenIncremental_EachIsTheFileAtItsFreeze (every page of a full and of an incremental equals the
            file read at the freeze; the incremental holds every page a write probe saw between the freezes),
            FreezeTests.N_IsThePhysicalLength_NotTheTrackedSize (an async batch held between its write and its fsync);
            source mutant seen red: N from the tracked size
  spec: rules/tla/BackupCapture.tla (S6) — ImageFidelity; mutant -mutant-nogate

### BKP-03: Copy-before-write `[silent]`
  invariant from F_X until the hook is uninstalled: ∀ write to p with p < N_X ∧ p in X's image set: the write is issued only
            after Done(p) — p's pre-image read into capture memory — or after the capture aborted
  invariant whoever sets Claimed(p) first (scanner or writer) reads p, then sets Done(p); a writer finding p claimed and
            not done waits for Done(p), bounded (BKP-12); Done(p) never waits for the store (C8)
  invariant the hook is uninstalled under the exclusive write gate, and only once every page is in the sink (no writer can
            wait on the backup thread any more) or after an abort (which releases every waiter) — C7; the copy-before-write
            slots are freed only after that uninstall
  invariant the meta pair is copied under the freeze and pre-marked claimed ∧ done: a later meta flip never waits
  scope: BackupCapture.BeforeWrite, BackupCapture.Scan, BackupCapture.Uninstall, BackupCapture.Abort, CbwSlotPool,
    PagedMMF.BeforeDataFileWrite (the hook call inside the shared gate), PagedMMF.SavePages (the hook runs on the caller's
    thread before any async write is issued)
  on_violation: a page of the point holds bytes written after F_X while others hold bytes from before → a silently
    corrupt point, discovered at restore
  verified: CbwRaceTests.ScannerClaimedFirst_TheWriterWaits (scanner paused between its claim and its read while a
            checkpoint rewrites the page), CbwRaceTests.WriterClaimedFirst_TheScannerTakesItsPreImage (a checkpoint ahead of
            the scanner), with CbwRaceTests.Mutant_TheWriterSkipsTheClaimWait_IsReported; CbwRaceTests.Abort_ReleasesAWriterWaitingOnAClaimedPage;
            source mutant seen red: no copy-before-write at all
  spec: rules/tla/BackupCapture.tla (S6) — ImageFidelity; mutant -mutant-noclaimwait

### BKP-04: The tail holds every record the image can reflect, and stays reachable until copied `[fatal]`
  invariant the tail of point X = every WAL segment with lastLSN ≥ C_X plus the active one, each copied from offset 0, after
            WaitForDurable(PubF) where PubF = LastPublishedLsn sampled under the freeze; hence it holds every durable
            record with LSN in (C_X, PubF] — every record any image page can reflect, since a checkpoint wave writes a page
            BEFORE its flush2 (CK-02 orders flush2 before the data fsync, not before the data write)
  invariant from the freeze until the copy ends, WAL reclaim never deletes a segment with lastLSN ≥ the floor sampled
            under the freeze (CheckpointManager.CheckpointLsn, ≤ C_X)
  invariant the segment snapshot sees the segment a rotation is sealing (CK-07: the assignment and the Add share one
            critical section)
  invariant the active segment is copied up to its batch end (WalSegmentContext.BatchEndOffset), which the writer
            publishes once a batch is wholly written and before the durable LSN moves over it: the copy never ends inside a
            batch, though one larger than the staging buffer is written in slices cut anywhere in a chunk, each advancing
            the write offset
  invariant a tail segment is restored at its header's declared size, zero-filled past the copied prefix (the reader
            scans the declared size and ignores short reads)
  invariant P_X = the highest LSN in (C_X, end of the valid prefix] recovery applies as a unit — a TxCommit marker or a
            fence record — computed by the scan RecoveryDriver.Run makes (path order, stop for good at the first
            corruption); C_X when there is none
  invariant P is computed over the segments the copy read, read again through WalTailView (the copied length, zeros past
            it), under the same floor: the tail is streamed to the sink, never held in managed memory. The floor is enforced,
            not assumed: CaptureTail refuses to start without it, and every open and read of the live segments by the view
            checks it (a floor released before P throws rather than reading a reclaimed file). Nothing but reclaim removes or
            rewrites a tail segment: ids are monotonic, preallocation creates only ids past the active one, headers are
            written only into new files, the writer appends only past the batch end. The offline copy holds the database
            lock and the data file unshared: no writer, no reclaim
  scope: WalTailInspector.CaptureTail, WalTailInspector.SelectTail, WalTailInspector.ComputePointLsn,
    WalTailInspector.CaptureTailOffline, WalTailView, WalSegmentContext.CompleteBatch, WalWriter.WriteInChunks,
    CheckpointManager.SetReclaimFloor, CheckpointManager.ClearReclaimFloor
    (read at step 8 beside the tick-fence floor), WalSegmentManager.SnapshotForTail, WalSegmentManager.RotateSegment,
    WalWriter.RequestSegmentSwitch (W2: shortens the tail, never decides its content)
  on_violation: a page reflects a transaction whose commit marker is not in the tail → recovery applies nothing of it and
    the page keeps part of it (a phantom partial transaction, no undo); or the segment ending at C is missing → the
    restore resumes the TSN counter below a commit stamp → MVCC visibility corruption; or a needed segment reclaimed
    mid-copy → the capture fails (or, worse, copies a hole)
  requires: WP-09 (reclaim keeps lastLSN ≥ C), RB-05, LOG-03, BKP-01 (the floor is read after a gate enter that follows
    the freeze)
  verified: WalTailCaptureTests.FlushBeforeTail_CopiesARecordPublishedButNotDurableAtTheFreeze (the WAL writer held with a
            commit published: the copy waits, then holds it), WalTailCaptureTests.SegmentEndingAtTheCheckpoint_IsInTheTail
            (+ Mutant_ATailStartingOneSegmentLate_IsReported), WalTailCaptureTests.FloorHoldsDuringTheCopy_WhileACheckpointRuns
            (a checkpoint inside the copy), SegmentSnapshotTests.SnapshotDuringARotation_SeesTheSegmentBeingSealed;
            TailFloorTests.AReclaimStagedBeforeTheReadOfP_LeavesTheTailSegments (a checkpoint past a sealed tail segment, run
            between the copy and the read of P), WalTailCaptureTests.ATailCopyWithoutTheFloor_Refuses,
            WalTailCaptureTests.AFloorReleasedBeforeTheReadOfP_IsCaught, WalTailViewTests.OverASource_RefusesEveryReadOnceTheFloorIsGone,
            WalTailCaptureTests.ATailTakenBetweenTheSlicesOfABatch_EndsWhereTheBatchBegan (the writer held after a batch's
            first slice), WalTailCaptureTests.TheTailCopy_MakesNoManagedCopyOfTheTail, WalTailViewTests;
            P: WalTailCaptureTests.PointLsn_IsTheLastCommitOrFence_AndStopsAtTheFirstCorruption (and P over the stored
            bytes = P over the live segments). Source mutants seen red: no flush before the copy, `>` instead of `≥`, no
            floor in step 8, P without fence records, no stop at the first corruption, ActiveSegment assigned outside the
            lock, the snapshot reading WriteOffset, a batch end published per slice, a managed copy per segment, a view
            that reads past the copied length, the floor released before P (caught by the view; with the view's check
            removed too, the staged checkpoint deletes the segment under the read), the view's check removed, the copy's
            precondition removed
  spec: rules/tla/BackupCapture.tla (S6) — TailCoversImage, NoNeededRecordReclaimed; mutants -mutant-noflush, -mutant-nofloor

### BKP-05: Opening a bundle restored from a point yields exactly that point's committed prefix `[fatal]` `[silent]`
  invariant ∀ published point X, R = restore(X): state(open(R)) = I_X + every transaction whose TxCommit lies in the valid
            prefix of T_X's copied bytes, and nothing else — the state a hard crash at F_X leaves when the data file took no
            write after F_X and the WAL stayed durable to the end of the tail copy (LOG-04, AP-12)
  invariant R/data = materialise(X): the chain's newest image of each page, zeros for a page no point holds, exactly
            N_X × 8192 bytes, written ascending and completely (BKP-02 carried through the store)
  invariant R/wal holds exactly T_X's segments, each {SegmentId:D16}.wal: bytes [0, RawLength) the copied bytes,
            [RawLength, header.SegmentSize) zero — never shorter than its header declares, since the segment reader scans
            the declared size and ignores a short read
  invariant I_X unclean ⇒ T_X holds ≥ 1 segment (the writer refuses to publish otherwise; offline capture always takes
            the newest segment): CrashRecoveryAtOpen needs a *.wal, and without one the open skips replay, the RB-01
            rebuild and the RB-04 net over an image that needs them
  invariant R holds nothing another session wrote beside the data (no chunk-summary, backup-state or db.lock), so every
            nonce in I_X's meta matches nothing and the open trusts nothing outside the image (CS-05)
  invariant an open that finds {R}/restore.json reads its WAL from {R}/wal or refuses; it adopts NewTimelineId into
            BK_TimelineId (one fsynced meta flip, skipped when already adopted) before the WAL manager starts, then deletes
            the marker, so a crash anywhere repeats both; a marker that is unreadable or names another DatabaseId refuses
            the open before anything is written; a marker without a data file is never mistaken for a new database
  invariant a restore never deletes a database: an existing target is renamed {name}.typhon.pre-restore-{utc}; the only
            directory it deletes is a staging directory carrying its own restore.staging marker; it refuses a target a
            live process holds (lock file OR an exclusive probe of its data file) whatever it was asked
  requires: BKP-02, BKP-04, BKP-08, LOG-03, LOG-04, AP-12, CS-05, WR-01, CK-04 — the sealed segments T_X omits hold only
            records below C_X, exactly those CK-04 may reclaim once C_X is persisted, so R/wal is the WAL of a reachable
            crash state
  scope: RestoreStager.Run, RestoreStager.WriteWalSegment, RestoreStager.Swap, ChainMaterializer.Materialize,
    PackWriter.Complete, OfflineCapture.Create, WalTailInspector.CaptureTailOffline, RestoreMarker.TryRead,
    DatabaseEngine.CheckRestoreMarkerAtOpen, DatabaseEngine.AdoptRestoredTimeline, DatabaseEngine.AdoptTimeline,
    PagedMMF.RefuseRestoreMarkerWithoutData
  on_violation: a restored database that opens without error and holds another state than the point it names —
    transactions missing or half-applied (an image replayed with no tail, or none), replayed from a foreign WAL directory,
    or writing its new history under the original's timeline; found when the data is needed
  verified: BackupCaptureRoundTripTests.EveryPoint_RestoredFromItsPacks_OpensToTheCommittedStateAtItsPointLsn (T1a over
            real packs: bytes = what capture handed the store, crash path, timeline, committed state at P; mutant
            Mutant_ARestoreWithoutItsWal_IsReported), OfflineCaptureRestoreTests (T9), TimelineAdoptionTests (T10),
            RestoredBundleShapeTests, RestoreGuardTests — [VerifiesRule("BKP-05")]; source mutants seen red: no SetLength,
            delete instead of rename, adopt after the WAL manager, delete the marker before the flip, ignore the marker's
            DatabaseId, no WalDirectory check, no create guard, newest segment not always taken offline

### BKP-06: An incremental only on a trusted map and an unbroken chain `[silent]`
  invariant a point is captured incremental iff the request allows it ∧ BackupLineage.Trusted ∧ lineage.StoreId = the
            store's id ∧ the store's head of lineage.ChainId is lineage.LastBackupId ∧ that head is on the database's current
            timeline or on the timeline it forked from (#1418: a fork for a write the WAL cannot replay keeps the lineage, the map
            having tracked every write; a restore's new timeline does not); otherwise full (a new ChainId, minted by the store)
  invariant Trusted at open iff the last close was clean ∧ backup-state's nonce = BackupStateNonce ≠ 0 ∧ both CRCs hold ∧
            DatabaseId ∧ TimelineId ∧ DataFilePages = the file's length at open; then its map is ORed into the live one
  invariant backup-state is written only by a Trusted session's clean close, its nonce recorded in the flip that sets the
            clean flag; an untrusted close deletes it and records 0 (C13); every open after a clean close, SetCleanShutdown(false)
            and MarkSweepOwed zero the nonce, so after a crash, a failed open or a sweep-owed close nothing vouches for the file;
            adopting a new timeline makes the lineage untrusted
  scope: BackupAdmission.Decide, BackupStateFile.TryRead, BackupStateFile.Write, DatabaseEngine.AdoptBackupState,
    DatabaseEngine.WriteBackupState, DurabilityWatermarks.MarkCleanShutdown, DurabilityWatermarks.BeginOpenAfterCleanClose,
    DurabilityWatermarks.SetCleanShutdown, DatabaseEngine.AdoptTimeline, DatabaseEngine.ForkTimeline (keepLineage), OfflineCapture.Create
    (the image's BK_TimelineParent)
  on_violation: an incremental over a map that missed writes → the restore serves stale pages, discovered at restore
  verified: BackupAdmissionTests (T6: a clean close → incremental holding exactly the pages a write probe saw between the
            freezes, AC-C2; a crash, an offline repair, a copied bundle behind its chain's head, a wiped store and a new
            timeline → full; the decision matrix + Mutant_ADecisionIgnoringTrust_IsReported), BackupStateFileTests (round
            trip and damage; trusted close → trusted open → nonce cleared; a stale file; a matching nonce with another database,
            timeline or length; an untrusted close deletes the file; a write failure costs a full point; Mutant_AFileTrustedAfterACrash_IsReported;
            Adoption_IsReportedLoaded_OnlyWhenTheSessionAdopts: tracking off is TrackingOff, never Loaded),
            TimelineForkTests.AFork_KeepsTheLineage_TheNextPointIsIncremental (the parent clause; the decision matrix holds a
            head on the parent and on another timeline's fork).
            Source mutants seen red: no nonce check, the open not clearing the nonce, Decide ignoring trust, an untrusted close
            writing the file, the adoption reporting Loaded with tracking off

### BKP-09: Every offline writer of the data file deletes backup-state first `[silent]`
  invariant ∀ process that writes the data file without an engine: backup-state is deleted before its first write (beside
            chunk-summary), so the next open finds no file and the next point is full; a file that cannot be deleted
            stops the writer before that write (the engine's own deletes may fail silently: its open and close reset the
            nonce that vouches for the file, an offline writer does not)
  scope: OfflineWrite.InvalidateDerivedFiles (the one gate), DatabaseRepair.Apply (in front of OccupancyRepair and
    PairSlotRepair), FixturesController.Integrity (the Workbench damage fixture, dev only)
  on_violation: a stale trusted map vouches for a file it no longer describes → a wrong incremental
  verified: BackupAdmissionTests.RepairApply_DeletesTheBackupState_AndTheNextPointIsFull,
            BackupAdmissionTests.RepairApply_ThatCannotDeleteTheBackupState_RefusesBeforeWriting; source mutants seen red:
            Apply without the delete, a delete that swallows its failure

### BKP-07: A point that does not complete forgets no page `[silent]`
  invariant a capture that does not complete ORs every page of its image set back into the live PageChangeMap (a full
            capture ORs back the map it replaced) before the next freeze can detach it, so the next incremental covers
            every page written since the last COMPLETED point's freeze; its lineage is left as it was
  invariant an engine closed during a capture stops it first (before any drain or the final checkpoint), so the close sees
            the merged map; a backup thread that cannot be joined makes the session's lineage untrusted
  scope: BackupCapture.Fail, PageChangeMap.OrWith, BackupCoordinator.StopAndJoin, DatabaseEngine.DisposeCore
  on_violation: the next incremental omits pages written before the failed freeze → a restore serves stale pages
  verified: BackupFailurePathTests.SinkThrowsMidScan_TheNextIncrementalCoversEverything (+ Mutant_APageOfTheFailedCaptureIsMissing_IsReported),
            BackupFailurePathTests.DisposeDuringCapture_ClosesCleanly_AndTheMapHoldsThePageSet (each phase),
            CbwStallTests (the incremental after a stall holds the aborted set); source mutant seen red: no merge on failure
  spec: rules/tla/BackupCapture.tla (S6) — ChangeMapComplete over the abort's four steps (flag, uninstall request, removal under
    the drained gate, merge of the DETACHED map); mutant -mutant-mergeimageset (merging the image set, null for a full capture)

### BKP-08: A published pack is never modified, and its id names one content for the store's life `[fatal]`
  invariant a pack is visible under {BackupId:D8}-{K}.tpack only complete: written as *.tpack.tmp, fsynced, then moved
            without overwrite by a durable rename (Windows MoveFileExW WRITE_THROUGH; Linux rename + fsync of packs/); an
            existing final name fails the publish; a rename whose directory flush fails is undone, so Complete throws with
            nothing visible
  invariant no code opens a published pack with write access; readers open it Read | Delete sharing
  invariant ids are allocated under store.lock (held open FileShare.None) as max(store.hwm, every pack and temp id) + 1,
            with store.hwm durable before the temp exists: a deleted id is never reused (a lost or damaged store.hwm
            degrades to the higher of the packs present and the high-water this store's catalog recorded, and says so)
  invariant a temp is never part of a chain; the holder of store.lock deletes every temp it finds before it writes
  invariant every byte of a pack is covered by a CRC that verify --level full checks: HeaderCRC, FooterCRC, IndexCRC,
            TailDirCRC, SuspectCRC, each page's PageCrc after decode, each tail segment's RawCrc after decode, and
            FileCRC over [0, EOF-4); restore checks every one it reads and never returns a byte it read wrong
  invariant (#1415) a pack is deleted only under store.lock, only when no kept point's chain resolves through it
  scope: PackWriter.Complete, PackWriter.Abort, BackupStore.Publish, BackupStore.AllocateBackupId,
    BackupStore.DeleteAbandonedTemps, StoreFileSystem.MoveDurable, PackReader.Open, BackupVerifier.VerifyStore
  on_violation: a point whose bytes changed after it was verified, a restore of "#12" that returns another point, or a
    visible pack missing its tail bytes — an unrestorable backup, discovered at restore
  verified: StorePublishCrashTests (a crash after every file-system operation of a backup, Windows and Linux directory
            durability models), PackCorruptionPropertyTests (T7: every byte under a checked CRC, no restore ever
            wrong), BackupIdAllocationTests — [VerifiesRule("BKP-08")]; mutant
            BackupIdAllocationTests.Mutant_AStoreThatLostEveryHighWater_ReusesAnId; source mutants seen red: rename before
            flush, no directory flush (Linux model), publish with overwrite, allocation ignoring the high-water, verify
            skipping FileCRC, restore skipping PageCrc

### BKP-10: No WAL segment leaves the database un-archived, unless the backlog policy released it, and every loss is a visible gap `[fatal]`
  invariant with archiving configured, reclaim — CheckpointManager step 8 and the open's reconcile — deletes a sealed segment s only
            when s.LastLsn < archiveFloor; archiveFloor = ArchivedThroughLsn + 1, raised by the archiver only after s's .twal is
            durable (flushed, renamed without replace, its directory flushed); it is engaged at 0 before the first cycle (the
            recovery seal's included), and the open reclaims nothing below it (WA-3)
  invariant the floor is released only at step 8, by a CAS, only when the bytes it alone holds (floor ≤ LastLsn < trim) exceed
            BacklogLimitBytes, each release counted; it is re-engaged only by the archiver's raise after a successful publish; a
            raise racing the CAS wins and the cycle honours the floor it read. The engine never waits on the archive and holds at
            most the limit plus one segment for it; archiving off costs step 8 one read
  invariant the archive holds only durable, valid WAL: s is archived only once s.LastLsn ≤ DurableLsn (WA-15), and only up to the
            end of its last frame read whole, the rest of that page zeroed (no byte of a torn frame is ever archived); a break in
            a segment is recorded as a dropped range no restore may cross (CorruptMidSegment)
  invariant a timeline's archive is ordered by LSN; wherever next.FirstLsn ≠ prev.LastLsn + 1 the next segment's footer records
            the gap with its cause (BacklogReleased, ArchivingOff, Unknown) and every restore re-derives continuity from the
            files; a segment starting inside the archive, a local segment whose archived copy under its id has other bytes, or a
            name holding other bytes stops archiving that timeline (Diverged, WA-14) — the existing file is never replaced
  invariant an archived segment is immutable, self-describing and wholly CRC-covered (.twal: HeaderCRC, BlockDirCrc, SamplesCrc,
            RawCrc after decode, FooterCRC, FileCRC); archive-index.bin is a cache, used only intact and for the listing it was
            written for; a timeline's archive.lock is held for one operation, and a file system that does not enforce it is refused
  invariant the segment the archiver reads is opened with delete sharing (WA-5): a released cycle that reclaims it deletes it
            instead of failing under the sealed-list lock
  invariant a segment the archive floor may keep past a crash open never holds bytes recovery would stop at (WA-9): when the crash
            open's recovery stopped at a break in the LAST segment holding data, the open writes a PaddingSentinel frame header
            (RecordCount = CrashSealRecordCount, LastLsn = the highest fence LSN recovery applied from the torn frame) at the
            first invalid frame and flushes it, after recovery read the WAL and before the session's first append; the segment
            then ends cleanly for recovery, captures and the archiver (archived TornTrimmed, the applied fences a dropped range).
            A break followed by a segment holding data is mid-log corruption (#587): left as it is
  scope: CheckpointManager.EngageArchiveFloor, CheckpointManager.RaiseArchiveFloor, CheckpointManager.ArchiveFloorReleased
    (step 8, beside the backup floor W1), WalSegmentManager.Initialize (reclaimFloorAtOpen), WalSegmentManager.BytesHeldBetween,
    WalSegmentManager.ArchiveSnapshot, IWalFileIO.OpenSegmentForArchive, WalFileIO.OpenSegmentForArchive,
    WalSegmentReader.OpenSegmentForArchive, WalArchiveScanner.Scan, WalArchiver.Step, WalArchiver.ArchivePending,
    WalArchiver.ArchiveOne, WalArchiver.CheckCovered, WalArchiveTimeline.Publish, WalArchiveTimeline.TryOpen,
    ArchivedSegmentWriter.Complete, ArchivedSegmentReader.Decode, ArchiveIndex.TryRead, DatabaseEngine.InitializeWalManager,
    DatabaseEngine.CreateWalArchiver, WalTornTailSeal.Seal, DatabaseEngine.SealTornTailForArchive
  on_violation: a segment deleted un-archived with no gap → a restore replays across a hole as if continuous → a state no run
    produced, found when it is needed; a torn frame in the store → every restore across it stops there for good; un-durable
    bytes archived → the next session reuses their LSNs and the timeline's archive diverges from the database; a torn segment kept
    unsealed → the next crash's recovery stops at it and loses every commit of the session after it
  requires: WP-09, CK-07, WP-06, LOG-03
  verified: WalArchiveFloorTests (held through checkpoints until the raise; released once, counted, re-engaged only by a raise; a
            raise beating the release CAS; Mutant_AReclaimIgnoringTheArchiveFloor_IsReported), WalArchiveOpenReclaimTests (AC-A5,
            Mutant_AnOpenReclaimingBelowTheCheckpointOnly_IsReported), ArchiveShareDeleteTests (Windows, real files;
            Mutant_AReadHandleWithoutDeleteSharing_FailsTheReclaim), WalArchiveScannerTests, WalArchiverStepTests (the floor is
            still 1 when the copy becomes durable), WalArchiveBacklogTests (AC-A1), ArchivedSegmentCorruptionTests (T7: every byte
            outside the data and every 8th inside; Mutant_ADecodeWithoutTheRawCrc_IsReported), ArchiveStoreCrashTests (AC-A4, a
            crash after every operation of a publish, both directory models), ArchiveIndexTests
            (Mutant_AnIndexTrustedWithoutItsFingerprint_IsReported), ArchiveDivergenceTests, ArchiveTornTailTests (AC-A10:
            Mutant_ACrashOpenThatDoesNotSealTheTornTail_IsReported; a mid-log break left unsealed), WalArchiveScannerTests
            (ASealedTornTail_EndsCleanlyAtItsSentinel_NamingTheFenceRecordsRecoveryApplied) — [VerifiesRule("BKP-10")]. Source
            mutants seen red: the floor raised before the publish, a release not counted, no flush before the rename, no
            directory flush (Linux model), a seal over a mid-log break
  spec: rules/tla/WalArchive.tla (S7) — ArchivedOrReleased, NoSilentHole; mutants -mutant-raisebeforepublish, -mutant-openreclaim,
    -mutant-nofloor, -mutant-continuityunchecked, -mutant-releasenocount

### BKP-11: A restore to an LSN or a time yields exactly the committed prefix at its cut, on one timeline, from one history `[fatal]` `[silent]`
  invariant restore(L) on timeline t: L' = the highest LSN ≤ L that ends a whole frame of t's WAL; state(open(R)) = every
            transaction whose TxCommit LSN ≤ L' and nothing else — BKP-05 with the tail extended by t's archived segments and cut
            at L' (the cut segment's bytes past the frame end zeroed to its declared size); L' ≥ the base's P, and L' is what the
            restore reports (EffectiveLsn)
  invariant an instant resolves to the highest LSN sampled durable at or before it on t's ancestry (t's samples, then its
            parent's at or below t's origin); never a frame durable after the instant; none ⇒ BeforeCoverage
  invariant the base is the newest restorable point of t with P ≤ L whose tail agrees with t's archive: each tail segment that
            has an archived copy under its id equals it over the bytes both hold, the tail's rest zeros; a disagreeing base is
            never mixed with the archive (another history, WA-11), an older one is tried; none ⇒ NoBase
  invariant the restored WAL is continuous from the base's tail to L': every hop next.FirstRecordLsn ≤ prev.End + 1, re-derived
            from the files, never the index; a recorded drop (BacklogReleased, CorruptMidSegment, TornAtCrash) or a missing
            range below L refuses with its range (Gap) and the LSNs still restorable; L past the archive ⇒ BeyondArchive
  invariant a restore never crosses a fork: L ≤ a child's OriginLsn is restored on the parent (ancestry walk), L > it on the
            child from a child point only; a fork (SchemaChange at registration, BulkLoad before BulkEnd, Restore at adoption)
            flips BK_TimelineId/Parent/OriginLsn/Reason in one fsynced meta write before any record of the new timeline
  invariant a clean base restored with archived WAL opens on the crash path: the marker (v2) carries ForceCrashRecovery, and the
            open replays the restored WAL whatever the image's clean flag; a v1 marker is still read, a newer one refused
  invariant a refused or planned restore writes nothing: Preview/PlanRestore report every refusal Run would raise; a Run held to
            an ApprovedPlan whose fingerprint (point, target, segments and their CRCs, cut) no longer matches refuses
            (BackupPlanChanged) before the first write
  requires: BKP-05, BKP-10, BKP-06, BKP-08, LOG-03, LOG-04
  scope: ArchiveRestorePlanner.Plan, ArchiveRestorePlanner.ResolveTime, ArchiveRestorePlanner.Ancestor, WalArchiveScanner.FindCut,
    RestoreStager.Run, RestoreStager.Preview, RestoreStager.WriteArchivedWal, RestoreMarker.TryRead,
    DatabaseEngine.CheckRestoreMarkerAtOpen, DatabaseEngine.ForkTimeline, DatabaseEngine.AdoptTimeline
  on_violation: a restore that opens without error to a state no run produced — a prefix past the target, a frame cut in half, two
    histories spliced at a segment, a fork crossed, or a clean base whose archived WAL is silently skipped; found when the data
    is needed
  verified: RestoreToLsnOracleTests (T12: every frame end as a target, from either base, oracle state; forced crash recovery),
            FrameCutTests, ArchiveRestorePlannerTests (every refusal; a disagreeing base never mixed; never across a fork),
            TimelineForkTests, RestoreMarkerV2Tests, RestoreCleanBaseTests
            (Mutant_AMarkerWithoutForceCrashRecovery_IsReported), RestorePlanTests (AC-A11) — [VerifiesRule("BKP-11")]. Source
            mutants seen red: fork flipped after FlushToDisk
  spec: rules/tla/WalArchive.tla (S7) — RestoreIsPrefix

### BKP-12: The engine never waits on a backup past its budget
  invariant a data-file writer spends at most CbwStallTimeout in BackupCapture.BeforeWrite per write, and per SavePages
            batch (the slot wait, the wait for a claimed page and every run of one batch share one deadline); past it the
            capture aborts and the write proceeds
  invariant the bound is cumulative: the freeze's exclusive attempts draw on one FreezeAcquireTimeout in all, the hook's
            removal on one UninstallTimeout; each attempt holds writers back at most GateAttemptSlice, and between
            attempts the gate stays open until the writers the failed attempt held back have entered
            (DataFileWriteGate.AdmitWaiters); past the budget the freeze fails, and an uninstall leaves the (inert) hook
            and leaks its buffers rather than freeing them under a writer
  invariant a closing engine waits for the backup thread at most BackupCaptureOptions.StopTimeout: every capture wait
            but a store call and the gate attempts observes the close; past it the lineage is revoked and the capture's
            native memory is left to the still-running thread, never freed under it
  scope: BackupCapture.BeforeWrite, BackupCapture.Abort, BackupCapture.Uninstall, BackupCoordinator.StopAndJoin,
         DataFileWriteGate.TryEnterExclusive, DataFileWriteGate.AdmitWaiters, CbwSlotPool
  on_violation: a slow store (or a stuck backup thread) stalls the checkpoint → page-cache back-pressure → transactions stall
  invariant a slot wait returns at its deadline, not on the OS timer tick after it: it blocks until one tick before the
            deadline (16 ms on Windows, 1 ms elsewhere) and polls the rest (a timed semaphore wait returned 9-16 ms late)
  note: the budget bounds the writer's waiting, not the scheduler: on a saturated box a writer polling its last tick was
            measured off the CPU for 12 ms between two of its checks. The copy-before-write budget is therefore verified on a
            logical clock (BackupCoordinator.CbwClock): the writer waits while the clock is held, gives up once it reaches
            the deadline having spent exactly the budget, and no blocking wait reaches into the last tick
  verified: CbwStallTests.SlotPoolExhausted_TheWriterReturnsWithinBudget_AndTheCaptureAborts [Category("Sensitive")] (two
            slots, an undrained backup thread, more rewrites than slots; on the logical clock: no give-up while held, the
            slowest write = the budget exactly once moved; abort CbwStall, the checkpoint advances),
            CbwSlotPoolTests.ASlotWait_NeverBlocksIntoTheLastTimerTick_AndGivesUpAtItsDeadline (source mutants seen red: a
            wait blocking to its deadline, a give-up one tick late, a give-up before the deadline),
            DataFileWriteGateTests.ExclusiveTimeout_IsBounded_AndReopensTheGate,
            DataFileWriteGateTests.AWriterHeldBackByAFailedAttempt_EntersBeforeTheNextOne,
            CbwSlotPoolTests.AnAbort_ReleasesAWriterWaitingOnAnEmptyPool (and CbwSlotPoolTests.ConcurrentRentPublishDrain_
            DeliversEveryPreImageOnce: no slot or pre-image lost under four writers and one drainer),
            BackupStopTests.AnUninstallThatCannotGetTheGate_StallsWritersWithinOneBudget (a writer's stall summed over the
            attempts ≤ UninstallTimeout + 250 ms, no single stall > slice + 50 ms), SavePagesGateTests
            .EveryRunOfABatch_SharesOneCopyBeforeWriteBudget, BackupStopTests.ACloseThatCannotJoin_LeavesTheCaptureItsMemory_
            AndThePublishCannotRetrust, BackupStopTests.ACloseDuringTheForcedCheckpoint_DoesNotWaitForIt,
            BackupStopTests.ACloseDuringTheTailFlush_DoesNotWaitForIt; source mutants seen red: an unbounded slot wait,
            ten uninstall budgets, an uncapped attempt, AdmitWaiters returning at once, a per-run deadline, a slot wait
            without the abort token, a plain publish or drain of the ready bits, the checkpoint
            and WAL waits without the close's token, the coordinator disposed (blocks freed) after a failed join
  spec: rules/tla/BackupCapture.tla (S6) — HookNotRemovedUnderWriter, and under weak fairness without any timeout
    (BackupCapture-liveness.cfg) WritersProgress and GateReopens; mutants -mutant-uninstallnogate, -mutant-noabortrelease,
    -mutant-timeoutkeepsgate
