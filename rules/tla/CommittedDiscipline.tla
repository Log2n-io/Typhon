------------------------ MODULE CommittedDiscipline ------------------------
(***************************************************************************)
(* Minimal WAL — Spec S3 (P2, design 08 §6).  Models the Committed         *)
(* durability discipline (issue #392, Variant A: deferred-apply/staging)   *)
(* against the fuzzy checkpoint, and proves — exhaustively over small      *)
(* bounds — that NO uncommitted bytes can ever become durable, that an     *)
(* acknowledged commit is recovered exactly, and that a checkpoint never   *)
(* passes a commit that is between its WAL append and its publish.         *)
(*                                                                         *)
(* The crux this spec exists to prove (the D7 rationale for choosing       *)
(* Variant A over Variant B): a Commit-discipline write STAGES into a       *)
(* private per-tx buffer and only touches the chunk/cluster HEAD at commit, *)
(* AFTER its WAL record is appended.  Because the fuzzy checkpoint captures *)
(* the HEAD page memory at any time, an in-place pre-commit write (Variant  *)
(* B) could let the checkpoint persist uncommitted bytes with no            *)
(* compensating WAL record — unrecoverable corruption.  The genuineness    *)
(* mutant models exactly that.                                             *)
(*                                                                         *)
(* The same window seen from the checkpoint (CK-13, NEW-CK-1): between the *)
(* append and the publish, the commit's record can be durable while the    *)
(* HEAD holds nothing of it.  A cycle that sets CheckpointLSN to its        *)
(* barrier there passes a record whose effect it never wrote, and recovery *)
(* skips the record.  The commit's floor — its LSN, stored after the claim *)
(* and before the frame is published, withdrawn after the publish — caps   *)
(* the barrier.  Until 2026-09-14 this spec's checkpoint advanced          *)
(* CheckpointLSN to the LSN the HEAD reflected, which the engine never     *)
(* did, so it could not see the window.  It now advances to the barrier.   *)
(*                                                                         *)
(* Invariants proven (header maps each to its rules/durability.md   *)
(* rule — the 08 §6 maintenance contract):                                 *)
(*   CM01_HeadCommitted        ->  CM-01 (the HEAD never holds an          *)
(*                                 uncommitted/staged value — Variant A)   *)
(*   CM01_NoUncommittedDurable  ->  CM-01 (the data file never holds a      *)
(*                                 value with no appended commit record)    *)
(*   AP01_PublishAfterAppend    ->  AP-01 (no visibility before append)    *)
(*   CK13_BelowUnpublished      ->  CK-13 (CheckpointLSN stays below the    *)
(*                                 record of every commit that has         *)
(*                                 appended and not yet published)         *)
(*   CK13_NeverLowers (action)  ->  CK-13 (the cap never lowers            *)
(*                                 CheckpointLSN).  It holds even without  *)
(*                                 Capped's Max: a stored floor is always  *)
(*                                 above CheckpointLSN, so the Max is      *)
(*                                 defence in depth                        *)
(*   AckDurable                 ->  acknowledged (published+fsynced) =>     *)
(*                                 recovered (end-state == durable prefix)  *)
(*   CM03_RecoveryConverges      ->  CM-03 (the dirty-after-publish re-emit  *)
(*                                 is benign: re-apply / crash-during-      *)
(*                                 recovery converges to the same value)    *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope):                     *)
(*   - one record per commit; last-writer-wins values (= the writing TSN)  *)
(*     per slot.  SameSlot=TRUE: every commit writes the one slot, and the  *)
(*     runtime DAG serializes them (ADR-057: it forbids concurrent         *)
(*     same-entity write x write), so a writer stages only once the        *)
(*     previous one has finished.  SameSlot=FALSE: commit t writes slot t  *)
(*     and the commits run concurrently.                                   *)
(*   - the private staging buffer is modeled by NOT touching `live` until   *)
(*     publish (Variant A) — its bytes are invisible and non-durable        *)
(*   - a claim appends the record with its WAL frame unpublished; the      *)
(*     writer drains in LSN order and stops at the first unpublished frame *)
(*     (WP-06)                                                             *)
(*   - the checkpoint cycle is barrier (DurableLsn), floor read, capture of *)
(*     the whole HEAD, data write, then CheckpointLSN := the capped barrier.*)
(*     CK-02's flush2 is abstracted as waiting until every captured value's *)
(*     record is durable.  CK-03's skip (a live writer) is NOT modeled: it  *)
(*     only holds CheckpointLSN back, and S1 covers it                      *)
(*   - the log is one Seq of commit records; segments/rotation/CRC NOT      *)
(*     modeled (covered by S2); index B+Tree reconcile NOT modeled          *)
(*   - a crash keeps the fsynced prefix + the data file, loses in-memory    *)
(*     state (staging buffers, HEAD, torn tail, floors, the running cycle) *)
(*     and ends every commit whose record is not durable, including those  *)
(*     not yet started, so no commit runs after a recovery                 *)
(*   - a failed append (AbandonClaim) is NOT modeled: every claim publishes *)
(*   - each step is atomic and memory is sequentially consistent.  The     *)
(*     engine walks the transaction chain for the floor, captures page by  *)
(*     page, and relies on the release/acquire chain from the floor store  *)
(*     through the frame's publish and the drain to the floor read         *)
(*                                                                         *)
(* GENUINENESS — every mutant MUST violate:                                 *)
(*   BreakStaging=TRUE (-mutant.cfg): Variant B, Stage writes `live` in     *)
(*     place before the record exists -> CM01 violation, proving staging is *)
(*     WHY CM-01 holds.                                                    *)
(*   FloorMutant (-mutant-<name>.cfg) breaks one CK-13 ordering:           *)
(*     "nofloor"            the cycle ignores the floors (NEW-CK-1 itself)  *)
(*     "floorafterframe"    the floor is stored after the frame is          *)
(*                          published, so a drain can pass an unfloored     *)
(*                          record                                          *)
(*     "withdrawearly"      the floor is withdrawn before the publish       *)
(*     "readbeforebarrier"  the cycle reads the floors before its barrier   *)
(*     "readaftercapture"   the cycle reads the floors after its capture    *)
(*     "latestfloor"        the cycle caps at the highest floor, not the    *)
(*                          lowest (run on two slots: two floors at once)   *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS
  NumTx,         \* number of Commit-discipline transactions (bound, e.g. 2)
  MaxCrashes,    \* max crashes incl. crash-during-recovery (bound, e.g. 2)
  SameSlot,      \* TRUE: every commit writes the one slot, serialized; FALSE: commit t writes slot t
  BreakStaging,  \* genuineness toggle: TRUE injects Variant B (in-place write)
  FloorMutant    \* genuineness toggle for CK-13: "none", or one broken ordering or choice (see the header)

TX        == 1..NumTx
Slots     == IF SameSlot THEN {1} ELSE TX
SlotOf(t) == IF SameSlot THEN 1 ELSE t
NoVal     == 0            \* the empty-slot value (TSNs are >= 1, so 0 is free)
NoFloor   == NumTx + 1    \* above every LSN: each commit appends one record

\* A commit's steps, in program order.  The floor mutants move one step.
CommitSteps ==
  CASE FloorMutant = "floorafterframe" -> <<"stage", "claim", "framePub", "storeFloor", "publish", "withdraw">>
    [] FloorMutant = "withdrawearly"   -> <<"stage", "claim", "storeFloor", "framePub", "withdraw", "publish">>
    [] OTHER                           -> <<"stage", "claim", "storeFloor", "framePub", "publish", "withdraw">>

\* A checkpoint cycle's steps, in program order.
CycleSteps ==
  CASE FloorMutant = "readbeforebarrier" -> <<"readFloor", "barrier", "capture", "write", "meta">>
    [] FloorMutant = "readaftercapture"  -> <<"barrier", "capture", "readFloor", "write", "meta">>
    [] OTHER                             -> <<"barrier", "readFloor", "capture", "write", "meta">>

Void == 0                     \* pc of a commit a crash ended before its record was durable
Done == Len(CommitSteps) + 1  \* pc of a finished (or recovered) commit

VARIABLES
  log,        \* Seq of commit records [tsn |-> TX]; the index is the LSN
  pub,        \* Seq of BOOLEAN, one per record: its WAL frame is published (drainable)
  durable,    \* fsynced prefix length, 0..Len(log)              [DurableLSN]
  pc,         \* [TX -> Void..Done] each commit's next step in CommitSteps
  floor,      \* [TX -> LSN] the commit's stored floor, 0 if none [Transaction._inFlightLsnFloor]
  live,       \* [Slots -> value] the HEAD page memory — what a fuzzy checkpoint
              \* captures.  Variant A: only Publish sets it.
  liveLsn,    \* [Slots -> LSN] the log index each HEAD value reflects (0 if empty)
  disk,       \* [Slots -> value] checkpoint-consolidated values (the data file)
  chkpt,      \* log index the data file is trusted to reflect  [CheckpointLSN]
  cpc,        \* the running checkpoint cycle's next step in CycleSteps
  bar,        \* this cycle's barrier: DurableLsn at its barrier step
  fl,         \* the lowest floor this cycle read (NoFloor if none)
  cap,        \* [Slots -> value] this cycle's captured copy of the HEAD
  crashed,    \* BOOLEAN — in a crash-recovery cycle
  phase,      \* {"run","scan","apply","done"}  (RecoveryDriver phase)
  vprefix,    \* valid-prefix length after scan (torn-tail truncation)
  applied,    \* recovery apply cursor (window position)
  recStore,   \* [Slots -> value] being rebuilt during recovery
  crashes     \* number of crashes so far (bound)

vars == <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt, cpc, bar, fl, cap,
          crashed, phase, vprefix, applied, recStore, crashes>>

cycleVars    == <<cpc, bar, fl, cap>>
recoveryVars == <<crashed, phase, vprefix, applied, recStore, crashes>>

-----------------------------------------------------------------------------
(* Helpers *)

Running == ~crashed /\ phase = "run"

\* A tx's commit record is present in log[1..p].
CommitInPrefix(t, p) == \E i \in 1..p : log[i].tsn = t
\* A value has an appended commit record anywhere in the (current) log.
Committed(v) == \E i \in 1..Len(log) : log[i].tsn = v
\* The (unique — each tx appends once) log index of t's commit record.
LsnOf(t) == CHOOSE i \in 1..Len(log) : log[i].tsn = t

\* Commit t's next step is s / commit t has done step s.
At(t, s)  == pc[t] \in 1..Len(CommitSteps) /\ CommitSteps[pc[t]] = s
Did(t, s) == pc[t] # Void /\ \E i \in 1..(pc[t] - 1) : CommitSteps[i] = s
Step(t)   == pc' = [pc EXCEPT ![t] = @ + 1]

\* The runtime DAG serializes same-entity writers: slot s has a commit between its stage and its end.
Busy(s) == \E u \in TX : SlotOf(u) = s /\ pc[u] \in 2..Len(CommitSteps)

CycleAt(s)   == CycleSteps[cpc] = s
AdvanceCycle == cpc' = IF cpc = Len(CycleSteps) THEN 1 ELSE cpc + 1

Max(a, b) == IF a > b THEN a ELSE b
\* The lowest stored floor (TransactionChain.LowestInFlightLsn).
MinFloor == LET F == {floor[t] : t \in TX} \ {0}
            IN IF F = {} THEN NoFloor ELSE CHOOSE m \in F : \A x \in F : m <= x
\* The highest stored floor: what the "latestfloor" mutant reads instead.
MaxFloor == LET F == {floor[t] : t \in TX} \ {0}
            IN IF F = {} THEN NoFloor ELSE CHOOSE m \in F : \A x \in F : m >= x
\* CK-13's cap on the barrier, never lowering CheckpointLSN.
Capped(b, f) == IF f <= b THEN Max(f - 1, chkpt) ELSE b

\* The last record in log[1..p] that writes slot s (0 if none).
LastFor(s, p) ==
  IF \E i \in 1..p : SlotOf(log[i].tsn) = s
  THEN CHOOSE i \in 1..p : SlotOf(log[i].tsn) = s /\ \A j \in (i + 1)..p : SlotOf(log[j].tsn) # s
  ELSE 0

\* Last-writer-wins fold of the committed prefix log[1..p], per slot.  This is
\* the DECLARATIVE durable-committed state recovery must reproduce.
ExpectedAfter(p) == [s \in Slots |-> IF LastFor(s, p) = 0 THEN NoVal ELSE log[LastFor(s, p)].tsn]

-----------------------------------------------------------------------------
(* Initial state *)

Init ==
  /\ log      = << >>
  /\ pub      = << >>
  /\ durable  = 0
  /\ pc       = [t \in TX |-> 1]
  /\ floor    = [t \in TX |-> 0]
  /\ live     = [s \in Slots |-> NoVal]
  /\ liveLsn  = [s \in Slots |-> 0]
  /\ disk     = [s \in Slots |-> NoVal]
  /\ chkpt    = 0
  /\ cpc      = 1
  /\ bar      = 0
  /\ fl       = NoFloor
  /\ cap      = [s \in Slots |-> NoVal]
  /\ crashed  = FALSE
  /\ phase    = "run"
  /\ vprefix  = 0
  /\ applied  = 0
  /\ recStore = [s \in Slots |-> NoVal]
  /\ crashes  = 0

-----------------------------------------------------------------------------
(* A commit (phase = "run") *)

\* Stage a Commit-discipline write into the private per-tx buffer (Variant A:
\* the HEAD `live` is NOT touched — the bytes are invisible and non-durable).
\* Variant B (BreakStaging) writes `live` IN PLACE here, before any commit
\* record exists — the unsafe path D7 rejects.  (liveLsn is left stale so the
\* uncommitted HEAD value has no honest coverage — CM01_HeadCommitted bites.)
Stage(t) ==
  /\ Running /\ At(t, "stage")
  /\ ~Busy(SlotOf(t))
  /\ Step(t)
  /\ live' = IF BreakStaging THEN [live EXCEPT ![SlotOf(t)] = t] ELSE live
  /\ UNCHANGED <<log, pub, durable, floor, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* TryClaim: the commit record takes the next LSN (AP-02 point of no return);
\* its frame is not published yet, so the writer cannot drain past it.
Claim(t) ==
  /\ Running /\ At(t, "claim")
  /\ log' = Append(log, [tsn |-> t])
  /\ pub' = Append(pub, FALSE)
  /\ Step(t)
  /\ UNCHANGED <<durable, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* CK-13: DurabilityLog.Append stores the commit's LSN as its floor.
StoreFloor(t) ==
  /\ Running /\ At(t, "storeFloor")
  /\ floor' = [floor EXCEPT ![t] = LsnOf(t)]
  /\ Step(t)
  /\ UNCHANGED <<log, pub, durable, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* WalCommitBuffer.Publish: the frame becomes drainable.
FramePub(t) ==
  /\ Running /\ At(t, "framePub")
  /\ pub' = [pub EXCEPT ![LsnOf(t)] = TRUE]
  /\ Step(t)
  /\ UNCHANGED <<log, durable, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* Publish: copy the staged value into the HEAD (the visibility act) — only
\* AFTER the commit record is appended (AP-01).  This is the FIRST time `live`
\* gets the value on the correct (Variant A) path; record the LSN it reflects.
Publish(t) ==
  /\ Running /\ At(t, "publish")
  /\ CommitInPrefix(t, Len(log))          \* AP-01: the append happened first
  /\ live'    = [live EXCEPT ![SlotOf(t)] = t]
  /\ liveLsn' = [liveLsn EXCEPT ![SlotOf(t)] = LsnOf(t)]
  /\ Step(t)
  /\ UNCHANGED <<log, pub, durable, floor, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* CK-13: the commit withdraws its floor once its page effects are in memory.
Withdraw(t) ==
  /\ Running /\ At(t, "withdraw")
  /\ floor' = [floor EXCEPT ![t] = 0]
  /\ Step(t)
  /\ UNCHANGED <<log, pub, durable, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

\* The WAL writer fsyncs one more record (DurableLsn creeps up, per-record). It
\* drains in LSN order and stops at the first unpublished frame (WP-06).
Drain ==
  /\ Running
  /\ durable < Len(log)
  /\ pub[durable + 1]
  /\ durable' = durable + 1
  /\ UNCHANGED <<log, pub, pc, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED recoveryVars

-----------------------------------------------------------------------------
(* The checkpoint cycle (phase = "run"), one at a time (RunCheckpointCycle's lock) *)

\* CK-01: the barrier is the durable frontier.
CkBarrier ==
  /\ Running /\ CycleAt("barrier")
  /\ bar' = durable
  /\ AdvanceCycle
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt, fl, cap>>
  /\ UNCHANGED recoveryVars

\* CK-13: read the lowest floor over the live commits.
CkReadFloor ==
  /\ Running /\ CycleAt("readFloor")
  /\ fl' = CASE FloorMutant = "nofloor"     -> NoFloor
             [] FloorMutant = "latestfloor" -> MaxFloor
             [] OTHER                       -> MinFloor
  /\ AdvanceCycle
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt, bar, cap>>
  /\ UNCHANGED recoveryVars

\* The fuzzy capture of the HEAD page memory.  CK-02's flush2 (the WAL covers
\* what a captured page reflects before the data write) is abstracted as
\* waiting until every HEAD value's record is durable.
CkCapture ==
  /\ Running /\ CycleAt("capture")
  /\ \A s \in Slots : liveLsn[s] <= durable
  /\ cap' = live
  /\ AdvanceCycle
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt, bar, fl>>
  /\ UNCHANGED recoveryVars

\* The captured copy reaches the data file (fsynced).
CkWrite ==
  /\ Running /\ CycleAt("write")
  /\ disk' = cap
  /\ AdvanceCycle
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, chkpt, bar, fl, cap>>
  /\ UNCHANGED recoveryVars

\* The meta persist: CheckpointLSN := the barrier, capped by CK-13.  The
\* transient cycle state is reset so finished cycles do not multiply states.
CkMeta ==
  /\ Running /\ CycleAt("meta")
  /\ chkpt' = Capped(bar, fl)
  /\ AdvanceCycle
  /\ bar' = 0
  /\ fl'  = NoFloor
  /\ cap' = [s \in Slots |-> NoVal]
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk>>
  /\ UNCHANGED recoveryVars

-----------------------------------------------------------------------------
(* Crash *)

\* Power cut: keep the fsynced prefix + the data file; lose the staging
\* buffers, the HEAD page memory, the torn tail (records beyond DurableLSN),
\* the floors and the running cycle.  A commit whose record is durable is
\* recovered; every other commit is gone.
Crash ==
  /\ ~crashed
  /\ crashes < MaxCrashes
  /\ phase = "run"
  /\ crashed'  = TRUE
  /\ phase'    = "scan"
  /\ log'      = SubSeq(log, 1, durable)        \* LOG-03: discard the torn tail
  /\ pub'      = SubSeq(pub, 1, durable)
  /\ live'     = [s \in Slots |-> NoVal]       \* HEAD page memory lost
  /\ liveLsn'  = [s \in Slots |-> 0]
  /\ pc'       = [t \in TX |-> IF CommitInPrefix(t, durable) THEN Done ELSE Void]
  /\ floor'    = [t \in TX |-> 0]
  /\ cpc'      = 1
  /\ bar'      = 0
  /\ fl'       = NoFloor
  /\ cap'      = [s \in Slots |-> NoVal]
  /\ vprefix'  = 0
  /\ applied'  = 0
  /\ recStore' = [s \in Slots |-> NoVal]
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<durable, disk, chkpt>>

-----------------------------------------------------------------------------
(* Recovery (RecoveryDriver.Run): scan -> apply -> done -> finish *)

\* Phase 1 SCAN: torn tail already discarded; the valid prefix is the durable
\* log.  Start the apply cursor at the checkpoint base; rebuild from the data
\* file (which reflects the committed prefix up to chkpt).
Scan ==
  /\ crashed /\ phase = "scan"
  /\ vprefix'  = Len(log)
  /\ applied'  = chkpt
  /\ recStore' = disk
  /\ phase'    = "apply"
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED <<crashed, crashes>>

\* Phase 3 APPLY (strict ascending order, AP-11): fold one window record LWW.
ApplyStep ==
  /\ crashed /\ phase = "apply"
  /\ applied < vprefix
  /\ recStore' = [recStore EXCEPT ![SlotOf(log[applied + 1].tsn)] = log[applied + 1].tsn]
  /\ applied'  = applied + 1
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED <<crashed, phase, vprefix, crashes>>

ApplyDone ==
  /\ crashed /\ phase = "apply"
  /\ applied = vprefix
  /\ phase' = "done"
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, disk, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED <<crashed, vprefix, applied, recStore, crashes>>

\* Seal: publish the rebuilt slots; the recovered state becomes the new base.
Finish ==
  /\ crashed /\ phase = "done"
  /\ crashed'  = FALSE
  /\ phase'    = "run"
  /\ live'     = recStore
  /\ liveLsn'  = [s \in Slots |-> LastFor(s, vprefix)]
  /\ disk'     = recStore
  /\ chkpt'    = vprefix
  /\ UNCHANGED <<log, pub, durable, pc, floor>>
  /\ UNCHANGED cycleVars /\ UNCHANGED <<vprefix, applied, recStore, crashes>>

\* Crash DURING recovery: the partial apply reached the data file (CK-08
\* flush-only) but CheckpointLSN does NOT advance (CK-04 holds the WAL window).
\* Re-run from scan over the partial base — must converge (CM-03 / AP-12).
CrashDuringRecovery ==
  /\ crashed
  /\ phase = "apply"
  /\ crashes < MaxCrashes
  /\ disk'     = recStore        \* partial rebuild persisted (CK-08 flush-only)
  /\ phase'    = "scan"
  /\ applied'  = 0
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<log, pub, durable, pc, floor, live, liveLsn, chkpt>>
  /\ UNCHANGED cycleVars /\ UNCHANGED <<crashed, vprefix, recStore>>

-----------------------------------------------------------------------------
\* No terminal stutter: a checkpoint cycle can always run, so a state with no
\* successor is a real deadlock and TLC reports it.
Next ==
  \/ \E t \in TX : Stage(t) \/ Claim(t) \/ StoreFloor(t) \/ FramePub(t) \/ Publish(t) \/ Withdraw(t)
  \/ Drain
  \/ CkBarrier \/ CkReadFloor \/ CkCapture \/ CkWrite \/ CkMeta
  \/ Crash
  \/ Scan
  \/ ApplyStep
  \/ ApplyDone
  \/ Finish
  \/ CrashDuringRecovery

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants *)

RecVal == 0..NumTx

TypeOK ==
  /\ Len(pub) = Len(log)
  /\ durable  \in 0..Len(log)
  /\ chkpt    \in 0..Len(log)
  /\ bar      \in 0..Len(log)
  /\ vprefix  \in 0..Len(log)
  /\ applied  \in 0..Len(log)
  /\ fl       \in 1..NoFloor
  /\ pc       \in [TX -> Void..Done]
  /\ floor    \in [TX -> 0..NumTx]
  /\ cpc      \in 1..Len(CycleSteps)
  /\ live     \in [Slots -> RecVal]
  /\ liveLsn  \in [Slots -> 0..Len(log)]
  /\ disk     \in [Slots -> RecVal]
  /\ cap      \in [Slots -> RecVal]
  /\ recStore \in [Slots -> RecVal]
  /\ phase    \in {"run","scan","apply","done"}
  /\ crashes  \in 0..MaxCrashes

\* CM-01 (HEAD): while running, the HEAD page memory never holds an
\* uncommitted/staged value.  Variant A keeps staged bytes out of `live`;
\* Variant B writes them in place at Stage -> this invariant fires immediately.
CM01_HeadCommitted ==
  Running => \A s \in Slots : live[s] = NoVal \/ Committed(live[s])

\* CM-01 (durable): the data file never holds a value with no appended commit
\* record — no uncommitted bytes ever become durable.  Variant B lets a
\* checkpoint capture an in-place staged value before its record exists.
CM01_NoUncommittedDurable ==
  \A s \in Slots : disk[s] = NoVal \/ Committed(disk[s])

\* AP-01: nothing published without its WAL commit record present.
AP01_PublishAfterAppend ==
  \A t \in TX : (~crashed /\ Did(t, "publish")) => Committed(t)

\* CK-13: CheckpointLSN stays below the record of every commit that has
\* appended and not yet published: recovery must still replay that record.
CK13_BelowUnpublished ==
  \A t \in TX : (Running /\ Did(t, "claim") /\ ~Did(t, "publish")) => chkpt < LsnOf(t)

\* CK-13: the cap never lowers CheckpointLSN (segments at or below it may
\* already be recycled).
CK13_NeverLowers == [][chkpt' >= chkpt]_vars

\* Acknowledged => durable: after recovery completes, the rebuilt slots equal
\* the declarative durable-committed prefix — every acknowledged (appended +
\* fsynced) Commit-discipline write is restored exactly, last-writer-wins.
AckDurable ==
  (phase = "done") => (recStore = ExpectedAfter(vprefix))

\* CM-03: the dirty-after-publish re-emit (and a crash mid-recovery) is benign —
\* re-applying converges to the same value.  Forced by CrashDuringRecovery:
\* TLC reaches "done" via 1..MaxCrashes re-runs over partial bases, and the
\* end state must still equal the durable-committed prefix.
CM03_RecoveryConverges ==
  (phase = "done") => (recStore = ExpectedAfter(vprefix))

=============================================================================
