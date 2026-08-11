------------------------ MODULE CommittedDiscipline ------------------------
(***************************************************************************)
(* Minimal WAL — Spec S3 (P2, design 08 §6).  Models the Committed         *)
(* durability discipline (issue #392, Variant A: deferred-apply/staging)   *)
(* against the fuzzy checkpoint, and proves — exhaustively over small      *)
(* bounds — that NO uncommitted bytes can ever become durable, and that an *)
(* acknowledged commit is recovered exactly.                               *)
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
(* Invariants proven (header maps each to its rules/durability.md   *)
(* CM rule — the 08 §6 maintenance contract):                              *)
(*   CM01_HeadCommitted        ->  CM-01 (the HEAD never holds an          *)
(*                                 uncommitted/staged value — Variant A)   *)
(*   CM01_NoUncommittedDurable  ->  CM-01 (the data file never holds a      *)
(*                                 value with no appended commit record)    *)
(*   AP01_PublishAfterAppend    ->  AP-01 (no visibility before append)    *)
(*   AckDurable                 ->  acknowledged (published+fsynced) =>     *)
(*                                 recovered (end-state == durable prefix)  *)
(*   CM03_RecoveryConverges      ->  CM-03 (the dirty-after-publish re-emit  *)
(*                                 is benign: re-apply / crash-during-      *)
(*                                 recovery converges to the same value)    *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope):                     *)
(*   - one logical slot, last-writer-wins values (= the writing TSN); the  *)
(*     private staging buffer is modeled by NOT touching `live` until       *)
(*     publish (Variant A) — its bytes are invisible and non-durable        *)
(*   - the checkpoint captures the HEAD page memory (`live`), the FUZZY     *)
(*     checkpoint, and advances CheckpointLSN only to the LSN that HEAD     *)
(*     actually reflects (`liveLsn`) — the CK-02/CK-03 coverage gate        *)
(*   - the log is one Seq of commit records; segments/rotation/CRC NOT      *)
(*     modeled (covered by S2); index B+Tree reconcile NOT modeled          *)
(*   - a crash keeps the fsynced prefix + the data file, loses in-memory    *)
(*     state (staging buffer + HEAD + torn tail)                           *)
(*                                                                         *)
(* GENUINENESS: CONSTANT BreakStaging toggles Variant B (in-place write).   *)
(*   CommittedDiscipline.cfg sets it FALSE (green); -mutant.cfg sets it     *)
(*   TRUE -> Stage writes `live` in place BEFORE the commit record exists,  *)
(*   so the HEAD holds (and a checkpoint can persist) an uncommitted value  *)
(*   -> CM01 violation, proving Variant A (staging) is WHY CM-01 holds.     *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS
  NumTx,         \* number of Commit-discipline transactions (bound, e.g. 2)
  MaxCrashes,    \* max crashes incl. crash-during-recovery (bound, e.g. 2)
  BreakStaging   \* genuineness toggle: TRUE injects Variant B (in-place write)

TX    == 1..NumTx
NoVal == 0          \* the empty-slot value (TSNs are >= 1, so 0 is free)

VARIABLES
  log,        \* Seq of commit records [tsn |-> TX]
  durable,    \* fsynced prefix length, 0..Len(log)              [DurableLSN]
  txph,       \* [TX -> {"pending","staged","appended","published","void"}]
  live,       \* the HEAD page-memory value (NoVal or a TSN) — what a fuzzy
              \* checkpoint would capture.  Variant A: only Publish sets it.
  liveLsn,    \* the log index the HEAD value reflects (0 if HEAD empty) — the
              \* coverage CheckpointLSN may advance to when HEAD is persisted
  disk,       \* checkpoint-consolidated value (the data file)
  chkpt,      \* log index the data file is trusted to reflect  [CheckpointLSN]
  crashed,    \* BOOLEAN — in a crash-recovery cycle
  phase,      \* {"run","scan","apply","done"}  (RecoveryDriver phase)
  vprefix,    \* valid-prefix length after scan (torn-tail truncation)
  applied,    \* recovery apply cursor (window position)
  recStore,   \* slot value being rebuilt during recovery
  crashes     \* number of crashes so far (bound)

vars == <<log, durable, txph, live, liveLsn, disk, chkpt, crashed, phase,
          vprefix, applied, recStore, crashes>>

-----------------------------------------------------------------------------
(* Helpers *)

\* A tx's commit record is present in log[1..p].
CommitInPrefix(t, p) == \E i \in 1..p : log[i].tsn = t
\* A value has an appended commit record anywhere in the (current) log.
Committed(v) == \E i \in 1..Len(log) : log[i].tsn = v
\* The (unique — each tx appends once) log index of t's commit record.
LsnOf(t) == CHOOSE i \in 1..Len(log) : log[i].tsn = t

\* Last-writer-wins fold of the committed prefix log[1..p].  Each record is one
\* tx's commit carrying value = tsn, so the fold is just the last record's value.
\* This is the DECLARATIVE durable-committed state recovery must reproduce.
ExpectedAfter(p) == IF p = 0 THEN NoVal ELSE log[p].tsn

AllResolved == \A t \in TX : txph[t] \in {"published", "void"}

-----------------------------------------------------------------------------
(* Initial state *)

Init ==
  /\ log      = << >>
  /\ durable  = 0
  /\ txph     = [t \in TX |-> "pending"]
  /\ live     = NoVal
  /\ liveLsn  = 0
  /\ disk     = NoVal
  /\ chkpt    = 0
  /\ crashed  = FALSE
  /\ phase    = "run"
  /\ vprefix  = 0
  /\ applied  = 0
  /\ recStore = NoVal
  /\ crashes  = 0

-----------------------------------------------------------------------------
(* Normal operation (phase = "run") *)

\* Stage a Commit-discipline write into the private per-tx buffer (Variant A:
\* the HEAD `live` is NOT touched — the bytes are invisible and non-durable).
\* Variant B (BreakStaging) writes `live` IN PLACE here, before any commit
\* record exists — the unsafe path D7 rejects.  (liveLsn is left stale so the
\* uncommitted HEAD value has no honest coverage — CM01_HeadCommitted bites.)
Stage(t) ==
  /\ ~crashed /\ phase = "run"
  /\ txph[t] = "pending"
  /\ txph' = [txph EXCEPT ![t] = "staged"]
  /\ live' = IF BreakStaging THEN t ELSE live
  /\ UNCHANGED <<log, durable, liveLsn, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* Commit: append the tx's commit record (AP-02 point of no return).
AppendCommit(t) ==
  /\ ~crashed /\ phase = "run"
  /\ txph[t] = "staged"
  /\ log' = Append(log, [tsn |-> t])
  /\ txph' = [txph EXCEPT ![t] = "appended"]
  /\ UNCHANGED <<durable, live, liveLsn, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* The WAL writer fsyncs one more record (DurableLsn creeps up, per-record).
Drain ==
  /\ ~crashed /\ phase = "run"
  /\ durable < Len(log)
  /\ durable' = durable + 1
  /\ UNCHANGED <<log, txph, live, liveLsn, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* Publish: copy the staged value into the HEAD (the visibility act) — only
\* AFTER the commit record is appended (AP-01).  This is the FIRST time `live`
\* gets the value on the correct (Variant A) path; record the LSN it reflects.
Publish(t) ==
  /\ ~crashed /\ phase = "run"
  /\ txph[t] = "appended"
  /\ CommitInPrefix(t, Len(log))          \* AP-01: the append happened first
  /\ live'    = t                          \* single-slot LWW
  /\ liveLsn' = LsnOf(t)
  /\ txph' = [txph EXCEPT ![t] = "published"]
  /\ UNCHANGED <<log, durable, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* Fuzzy checkpoint: consolidate the HEAD page memory (`live`) into the data
\* file.  Two gates make it honest:
\*   (1) WAL rule — the HEAD value's record must be fsynced (liveLsn <= durable);
\*   (2) coverage (CK-02/CK-03) — CheckpointLSN advances only to the LSN the
\*       persisted HEAD actually reflects (liveLsn), never blindly to durable.
\* So the data file never gains uncommitted bytes, and recovery replays the WAL
\* from chkpt to repair any records past the HEAD's coverage (idempotent, CM-03).
Checkpoint ==
  /\ ~crashed /\ phase = "run"
  /\ liveLsn <= durable
  /\ disk'  = live
  /\ chkpt' = liveLsn
  /\ UNCHANGED <<log, durable, txph, live, liveLsn, crashed, phase, vprefix,
                 applied, recStore, crashes>>

-----------------------------------------------------------------------------
(* Crash *)

\* Power cut: keep the fsynced prefix + the data file; lose the staging buffer,
\* the HEAD page memory, and the torn tail (records beyond DurableLSN).
Crash ==
  /\ ~crashed
  /\ crashes < MaxCrashes
  /\ phase = "run"
  /\ crashed'  = TRUE
  /\ phase'    = "scan"
  /\ log'      = SubSeq(log, 1, durable)        \* LOG-03: discard the torn tail
  /\ durable'  = durable                         \* == Len(log')
  /\ live'     = NoVal                           \* HEAD page memory lost
  /\ liveLsn'  = 0
  /\ txph'     = [t \in TX |-> IF CommitInPrefix(t, durable) THEN "appended"
                               ELSE "void"]      \* staged-only txs are gone
  /\ vprefix'  = 0
  /\ applied'  = 0
  /\ recStore' = NoVal
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<disk, chkpt>>

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
  /\ UNCHANGED <<log, durable, txph, live, liveLsn, disk, chkpt, crashed, crashes>>

\* Phase 3 APPLY (strict ascending order, AP-11): fold one window record LWW.
ApplyStep ==
  /\ crashed /\ phase = "apply"
  /\ applied < vprefix
  /\ recStore' = log[applied + 1].tsn
  /\ applied'  = applied + 1
  /\ UNCHANGED <<log, durable, txph, live, liveLsn, disk, chkpt, crashed, phase,
                 vprefix, crashes>>

ApplyDone ==
  /\ crashed /\ phase = "apply"
  /\ applied = vprefix
  /\ phase' = "done"
  /\ UNCHANGED <<log, durable, txph, live, liveLsn, disk, chkpt, crashed, vprefix,
                 applied, recStore, crashes>>

\* Seal: publish the rebuilt slot; the recovered state becomes the new base.
Finish ==
  /\ crashed /\ phase = "done"
  /\ crashed'  = FALSE
  /\ phase'    = "run"
  /\ live'     = recStore
  /\ liveLsn'  = vprefix
  /\ disk'     = recStore
  /\ chkpt'    = vprefix
  /\ txph'     = [t \in TX |-> IF CommitInPrefix(t, vprefix) THEN "published"
                               ELSE "void"]
  /\ UNCHANGED <<log, durable, vprefix, applied, recStore, crashes>>

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
  /\ recStore' = recStore
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<log, durable, txph, live, liveLsn, chkpt, crashed, vprefix>>

\* Terminal stutter so genuine terminal states are not flagged as deadlock.
Idle ==
  /\ ~crashed /\ phase = "run"
  /\ AllResolved
  /\ crashes >= MaxCrashes
  /\ durable = Len(log)
  /\ disk = live
  /\ chkpt = liveLsn
  /\ UNCHANGED vars

-----------------------------------------------------------------------------
Next ==
  \/ \E t \in TX : Stage(t)
  \/ \E t \in TX : AppendCommit(t)
  \/ Drain
  \/ \E t \in TX : Publish(t)
  \/ Checkpoint
  \/ Crash
  \/ Scan
  \/ ApplyStep
  \/ ApplyDone
  \/ Finish
  \/ CrashDuringRecovery
  \/ Idle

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants *)

RecVal == 0..NumTx

TypeOK ==
  /\ durable  \in 0..Len(log)
  /\ chkpt    \in 0..Len(log)
  /\ liveLsn  \in 0..Len(log)
  /\ vprefix  \in 0..Len(log)
  /\ applied  \in 0..Len(log)
  /\ live     \in RecVal
  /\ disk     \in RecVal
  /\ recStore \in RecVal
  /\ phase    \in {"run","scan","apply","done"}
  /\ crashes  \in 0..MaxCrashes
  /\ txph     \in [TX -> {"pending","staged","appended","published","void"}]

\* CM-01 (HEAD): while running, the HEAD page memory never holds an
\* uncommitted/staged value.  Variant A keeps staged bytes out of `live`;
\* Variant B writes them in place at Stage -> this invariant fires immediately.
CM01_HeadCommitted ==
  (~crashed /\ phase = "run") => ((live = NoVal) \/ Committed(live))

\* CM-01 (durable): the data file never holds a value with no appended commit
\* record — no uncommitted bytes ever become durable.  Variant B lets a
\* checkpoint capture an in-place staged value before its record exists.
CM01_NoUncommittedDurable ==
  (disk = NoVal) \/ Committed(disk)

\* AP-01: nothing published without its WAL commit record present.
AP01_PublishAfterAppend ==
  \A t \in TX : (~crashed /\ txph[t] = "published") => Committed(t)

\* Acknowledged => durable: after recovery completes, the rebuilt slot equals
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
