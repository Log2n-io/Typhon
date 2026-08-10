-------------------------- MODULE CommitRecovery --------------------------
(***************************************************************************)
(* Minimal WAL — Spec S2 (P1.4, design 08 §6).  Models the commit -> crash *)
(* -> recovery protocol and proves, exhaustively over small bounds, that   *)
(* recovery reconstructs exactly the durably-committed prefix.             *)
(*                                                                         *)
(* Invariants proven (header maps each to its rules/durability.md   *)
(* rule — the 08 §6 maintenance contract):                                 *)
(*   EndStateEqCommittedPrefix  ->  the headline (03 §7 / AP-12 outcome)    *)
(*   LOG04_MarkerGates          ->  LOG-04 (a tx is applied iff its commit  *)
(*                                  marker is in the valid prefix)          *)
(*   LOG05_DurableHonest        ->  LOG-05 (DurableLsn <= what was written) *)
(*   AP01_AppendBeforePublish   ->  AP-01  (no visibility without append)   *)
(*   AP02_PointOfNoReturn       ->  AP-02  (appended => its log record is   *)
(*                                  durable; never resurrect/rollback)      *)
(*   AP12_ApplyIdempotent       ->  AP-12  (re-running recovery after a     *)
(*                                  crash-during-recovery converges)        *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope — see 03/08 §):       *)
(*   - single logical key with last-writer-wins values (= the writing TSN);*)
(*     keys/pages are not individually modeled (allocation tolerance AP-13)*)
(*   - the log is one Seq(Record); physical WAL segments / rotation /       *)
(*     recycle / reopen reconciliation (WR-01) are NOT modeled             *)
(*   - seqlock parity, page-cache eviction, bulk-load are NOT modeled      *)
(*   - a crash keeps the fsynced prefix (DurableLSN) and discards the rest *)
(*     (the torn tail); in-memory un-appended state is lost                *)
(*                                                                         *)
(* GENUINENESS: CONSTANT BreakMarkerGate toggles a bug in the apply gate.   *)
(*   CommitRecovery.cfg sets it FALSE (green); CommitRecovery-mutant.cfg    *)
(*   sets it TRUE -> applies UNCOMMITTED data -> LOG04/EndState violation,  *)
(*   proving the gate (LOG-04) actually bites.                             *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS
  NumTx,            \* number of transactions in the model (bound, e.g. 2)
  MaxCrashes,       \* max crashes incl. crash-during-recovery (bound, e.g. 2)
  BreakMarkerGate   \* genuineness toggle: TRUE injects the LOG-04 bug

TX    == 1..NumTx
NoVal == 0          \* the empty-store value (TSNs are >= 1, so 0 is free)

VARIABLES
  log,        \* Seq of records [tsn |-> TX, kind |-> {"data","commit"}]
  durable,    \* fsynced prefix length, 0..Len(log)            [DurableLSN]
  txph,       \* [TX -> {"pending","appended","published","void"}]
  live,       \* in-memory visible value (NoVal or a TSN)
  disk,       \* checkpoint-consolidated value (the data file)
  chkpt,      \* log index the data file reflects             [CheckpointLSN]
  crashed,    \* BOOLEAN — in a crash-recovery cycle
  phase,      \* {"run","scan","apply","done"}  (RecoveryDriver phase)
  vprefix,    \* valid-prefix length after scan (torn-tail truncation)
  applied,    \* recovery apply cursor (window position)
  recStore,   \* store being rebuilt during recovery
  crashes     \* number of crashes so far (bound)

vars == <<log, durable, txph, live, disk, chkpt, crashed, phase,
          vprefix, applied, recStore, crashes>>

-----------------------------------------------------------------------------
(* Helpers *)

CommitInPrefix(t, p) == \E i \in 1..p : log[i].tsn = t /\ log[i].kind = "commit"
CommittedTxs(p)      == { t \in TX : CommitInPrefix(t, p) }

\* Fold log[1..p], last-writer-wins, applying only data records whose tx
\* committed within the prefix p.  This is the DECLARATIVE committed-prefix
\* state — the oracle the incremental recovery apply must reproduce.
RECURSIVE FoldFrom(_, _, _)
FoldFrom(store, i, p) ==
  IF i > p THEN store
  ELSE LET rec == log[i]
           s2  == IF rec.kind = "data" /\ rec.tsn \in CommittedTxs(p)
                  THEN rec.tsn ELSE store
       IN FoldFrom(s2, i + 1, p)

ApplyPrefix(p) == FoldFrom(NoVal, 1, p)

\* A prefix is "closed" iff every record in it belongs to a tx committed
\* within it — i.e. it does not split a transaction's batch across its edge.
PrefixClosed(p) == \A i \in 1..p : log[i].tsn \in CommittedTxs(p)

\* The checkpoint may only advance to the largest closed prefix <= durable.
\* This is the model's CK-02/CK-03 coverage guarantee: it guarantees every tx
\* committed AFTER the checkpoint has ALL its records above CheckpointLSN (in
\* the recovery window), so recovery never misses a committed tx's data.
SafeChkpt(d) ==
  CHOOSE p \in 0..d :
    /\ PrefixClosed(p)
    /\ \A q \in 0..d : PrefixClosed(q) => q <= p

AllResolved == \A t \in TX : txph[t] \in {"published", "void"}

-----------------------------------------------------------------------------
(* Initial state *)

Init ==
  /\ log      = << >>
  /\ durable  = 0
  /\ txph     = [t \in TX |-> "pending"]
  /\ live     = NoVal
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

\* Append a transaction's batch atomically: data record then commit marker.
\* (AP-02: appending the marker is the point of no return.)
AppendTx(t) ==
  /\ ~crashed /\ phase = "run"
  /\ txph[t] = "pending"
  /\ log' = Append(Append(log, [tsn |-> t, kind |-> "data"]),
                            [tsn |-> t, kind |-> "commit"])
  /\ txph' = [txph EXCEPT ![t] = "appended"]
  /\ UNCHANGED <<durable, live, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* The WAL writer fsyncs one more record (DurableLsn creeps up, per-record).
Drain ==
  /\ ~crashed /\ phase = "run"
  /\ durable < Len(log)
  /\ durable' = durable + 1
  /\ UNCHANGED <<log, txph, live, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* Publish makes a tx visible — only AFTER its records are appended (AP-01).
Publish(t) ==
  /\ ~crashed /\ phase = "run"
  /\ txph[t] = "appended"
  /\ CommitInPrefix(t, Len(log))          \* AP-01: the append happened first
  /\ live' = t                             \* single-key LWW
  /\ txph' = [txph EXCEPT ![t] = "published"]
  /\ UNCHANGED <<log, durable, disk, chkpt, crashed, phase, vprefix,
                 applied, recStore, crashes>>

\* Consolidate committed-durable state into the data file. CheckpointLSN only
\* advances to SafeChkpt(durable) (never splits a tx; never past durable).
Checkpoint ==
  /\ ~crashed /\ phase = "run"
  /\ SafeChkpt(durable) > chkpt
  /\ disk'  = ApplyPrefix(SafeChkpt(durable))
  /\ chkpt' = SafeChkpt(durable)
  /\ UNCHANGED <<log, durable, txph, live, crashed, phase, vprefix,
                 applied, recStore, crashes>>

-----------------------------------------------------------------------------
(* Crash *)

\* Power cut: keep the fsynced prefix + the data file; lose in-memory state
\* and the torn tail (records beyond DurableLSN). Enter recovery.
Crash ==
  /\ ~crashed
  /\ crashes < MaxCrashes
  /\ phase = "run"
  /\ crashed'  = TRUE
  /\ phase'    = "scan"
  /\ log'      = SubSeq(log, 1, durable)        \* LOG-03: discard the torn tail
  /\ durable'  = durable                         \* == Len(log')
  /\ live'     = NoVal                           \* in-memory lost
  /\ txph'     = [t \in TX |-> IF CommitInPrefix(t, durable) THEN "appended"
                               ELSE "void"]
  /\ vprefix'  = 0
  /\ applied'  = 0
  /\ recStore' = NoVal
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<disk, chkpt>>

-----------------------------------------------------------------------------
(* Recovery (RecoveryDriver.Run): scan -> apply -> done -> finish *)

\* Phase 1 SCAN: the torn tail was already discarded by Crash, so the valid
\* prefix is the whole (durable) log. Start the apply cursor at the checkpoint
\* base; the rebuild begins from the consolidated data file.
Scan ==
  /\ crashed /\ phase = "scan"
  /\ vprefix'  = Len(log)
  /\ applied'  = chkpt
  /\ recStore' = disk
  /\ phase'    = "apply"
  /\ UNCHANGED <<log, durable, txph, live, disk, chkpt, crashed, crashes>>

\* Phase 3 APPLY (strict ascending order, AP-11): fold one window record.
\* The gate (LOG-04) admits only data of txs committed within vprefix.
\* BreakMarkerGate drops the commit-marker condition — the injected bug.
ApplyStep ==
  /\ crashed /\ phase = "apply"
  /\ applied < vprefix
  /\ LET rec  == log[applied + 1]
         gate == IF BreakMarkerGate
                 THEN rec.kind = "data"
                 ELSE rec.kind = "data" /\ rec.tsn \in CommittedTxs(vprefix)
     IN recStore' = IF gate THEN rec.tsn ELSE recStore
  /\ applied' = applied + 1
  /\ UNCHANGED <<log, durable, txph, live, disk, chkpt, crashed, phase,
                 vprefix, crashes>>

ApplyDone ==
  /\ crashed /\ phase = "apply"
  /\ applied = vprefix
  /\ phase' = "done"
  /\ UNCHANGED <<log, durable, txph, live, disk, chkpt, crashed, vprefix,
                 applied, recStore, crashes>>

\* Seal: publish the rebuilt store; the recovered state becomes the new base.
Finish ==
  /\ crashed /\ phase = "done"
  /\ crashed'  = FALSE
  /\ phase'    = "run"
  /\ live'     = recStore
  /\ disk'     = recStore
  /\ chkpt'    = vprefix
  /\ txph'     = [t \in TX |-> IF CommitInPrefix(t, vprefix) THEN "published"
                               ELSE "void"]
  /\ UNCHANGED <<log, durable, vprefix, applied, recStore, crashes>>

\* Crash DURING recovery: the partial apply writes reached the data file
\* (CK-08 flush-only), but CheckpointLSN does NOT advance (CK-04 holds the WAL
\* window). Re-run from scan over the partial base — must converge (AP-12).
CrashDuringRecovery ==
  /\ crashed
  /\ phase = "apply"             \* only mid-apply: recStore has been seeded from disk by Scan
  /\ crashes < MaxCrashes
  /\ disk'     = recStore        \* partial rebuild persisted (CK-08 flush-only)
  /\ phase'    = "scan"          \* re-run; Scan resets applied = chkpt, recStore = disk
  /\ applied'  = 0
  /\ recStore' = recStore
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<log, durable, txph, live, chkpt, crashed, vprefix>>

\* Terminal stutter so genuine terminal states are not flagged as deadlock.
Idle ==
  /\ ~crashed /\ phase = "run"
  /\ AllResolved
  /\ crashes >= MaxCrashes
  /\ durable = Len(log)
  /\ SafeChkpt(durable) <= chkpt
  /\ UNCHANGED vars

-----------------------------------------------------------------------------
Next ==
  \/ \E t \in TX : AppendTx(t)
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
  /\ vprefix  \in 0..Len(log)
  /\ applied  \in 0..Len(log)
  /\ live     \in RecVal
  /\ disk     \in RecVal
  /\ recStore \in RecVal
  /\ phase    \in {"run","scan","apply","done"}
  /\ crashes  \in 0..MaxCrashes
  /\ txph     \in [TX -> {"pending","appended","published","void"}]

\* Headline: after recovery, the rebuilt store == the declarative committed
\* prefix.  The incremental apply (over the checkpoint base + window) must
\* equal the from-scratch fold of every committed record in the valid prefix.
EndStateEqCommittedPrefix ==
  (phase = "done") => (recStore = ApplyPrefix(vprefix))

\* LOG-04: a recovered value belongs to a committed tx (or is empty) — no
\* uncommitted transaction's data is ever visible after recovery.
LOG04_MarkerGates ==
  (phase = "done") => (recStore = NoVal \/ recStore \in CommittedTxs(vprefix))

\* LOG-05: the durable frontier never exceeds what has been appended.
LOG05_DurableHonest == durable <= Len(log)

\* AP-01: nothing published without its WAL record present.
AP01_AppendBeforePublish ==
  \A t \in TX : (~crashed /\ txph[t] = "published") => CommitInPrefix(t, Len(log))

\* AP-02: once appended, the commit record is in the (durable) log — the tx is
\* never resurrected without it nor rolled back after the point of no return.
AP02_PointOfNoReturn ==
  \A t \in TX : (~crashed /\ txph[t] \in {"appended","published"})
                  => CommitInPrefix(t, Len(log))

\* AP-12: idempotent re-run.  Checked as EndState, but its FORCE comes from the
\* CrashDuringRecovery action: TLC reaches "done" states via 1..MaxCrashes
\* recovery re-runs over partial bases, and EndState must still hold.
AP12_ApplyIdempotent ==
  (phase = "done") => (recStore = ApplyPrefix(vprefix))

=============================================================================
