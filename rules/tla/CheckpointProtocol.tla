------------------------- MODULE CheckpointProtocol -------------------------
(***************************************************************************)
(* Minimal WAL — Spec S1 (P0.7, design 08 §6 / 04-checkpoint.md).  Models  *)
(* one checkpoint cycle against concurrent commits + crash-anywhere, and   *)
(* proves the cycle never leaves a durably-committed page unrecoverable.   *)
(*                                                                         *)
(* Invariants proven (header maps each to its rules/durability.md   *)
(* rule — the 08 §6 maintenance contract):                                 *)
(*   CK01_BarrierDurable        ->  CK-01 (capture begins at a barrier =    *)
(*                                  the post-flush DurableLsn)              *)
(*   CK02_CapturedSubsetDurable ->  CK-02 (a captured page's content is     *)
(*                                  durable: baseLsn <= DurableLsn)         *)
(*   CK04_RecycleBound          ->  CK-04 (never recycle past the persisted *)
(*                                  CheckpointLSN) + WP-01 frontier order   *)
(*   NoLostPage                 ->  CK-03 o CK-04 headline: no page has its *)
(*                                  latest committed content un-captured    *)
(*                                  AND its WAL record already recycled     *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope — see 04/08 §):       *)
(*   - capture-vs-skip is NONDETERMINISTIC: a page may be skipped this      *)
(*     cycle (an ACW>0 / odd-seqlock conflict). Seqlock-parity and ACW      *)
(*     mechanics are abstracted to that single nondeterministic choice.     *)
(*   - one in-flight (un-fsynced) write per page at a time; the latest      *)
(*     durable value is what recovery would restore (durLsn).              *)
(*   - the log is one Seq of LSNs; physical WAL segments / rotation are     *)
(*     a single recycleFrontier; protected-pair (CK-05) = an atomic flip    *)
(*     (Persist), not A/B slots; recovery itself is S2's job, not modeled.  *)
(*                                                                         *)
(* GENUINENESS: CONSTANT BreakCoverageGate toggles the CK-03 bug.           *)
(*   CheckpointProtocol.cfg sets it FALSE (green); -mutant.cfg sets it TRUE *)
(*   -> CoverageGate advances CheckpointLSN even when pages were SKIPPED -> *)
(*   a skipped page's record is recycled -> NoLostPage violated.           *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS
  NumPages,           \* pages in the model (bound, e.g. 2)
  MaxLsn,             \* highest LSN appended (bound, e.g. 3)
  MaxCrashes,         \* crashes allowed (bound, e.g. 2)
  BreakCoverageGate   \* genuineness toggle: TRUE injects the CK-03 bug

Pages == 1..NumPages

VARIABLES
  curLsn,         \* [page -> LSN]  latest content LSN (0 = never written)
  durLsn,         \* [page -> LSN]  latest DURABLE content LSN (recovery would restore this)
  baseLsn,        \* [page -> LSN]  content LSN now in the durable data file (0 = never captured)
  capturedLsn,    \* [page -> LSN]  LSN snapshotted by this cycle's capture (transient)
  lsnMax,         \* highest LSN appended to the WAL
  durableLsn,     \* highest LSN fsynced to the WAL                       [DurableLsn]
  barrierLsn,     \* DurableLsn snapshot taken at the cycle barrier        (CK-01)
  ckptLsn,        \* in-memory checkpoint frontier                       [CheckpointLSN]
  ckptPersisted,  \* persisted (meta-fsynced) checkpoint frontier
  recycleFrontier,\* WAL recycled up to here (segments <= this are gone)  (CK-04)
  phase,          \* {"idle","capture","gate","persist","recycle"}
  collected,      \* pages collected dirty at the barrier this cycle
  captured,       \* pages captured this cycle
  skipped,        \* pages skipped this cycle (ACW/seqlock conflict)
  crashes         \* crashes so far (bound)

vars == <<curLsn, durLsn, baseLsn, capturedLsn, lsnMax, durableLsn, barrierLsn,
          ckptLsn, ckptPersisted, recycleFrontier, phase, collected, captured,
          skipped, crashes>>

SetMax(S) == IF S = {} THEN 0 ELSE CHOOSE m \in S : \A x \in S : x <= m

-----------------------------------------------------------------------------
(* Initial state *)

Init ==
  /\ curLsn          = [p \in Pages |-> 0]
  /\ durLsn          = [p \in Pages |-> 0]
  /\ baseLsn         = [p \in Pages |-> 0]
  /\ capturedLsn     = [p \in Pages |-> 0]
  /\ lsnMax          = 0
  /\ durableLsn      = 0
  /\ barrierLsn      = 0
  /\ ckptLsn         = 0
  /\ ckptPersisted   = 0
  /\ recycleFrontier = 0
  /\ phase           = "idle"
  /\ collected       = {}
  /\ captured        = {}
  /\ skipped         = {}
  /\ crashes         = 0

-----------------------------------------------------------------------------
(* Commits (concurrent with the checkpoint cycle) *)

\* A commit appends one WAL record at a fresh LSN and dirties page p. One
\* in-flight write per page (its previous write must already be durable).
DirtyPage(p) ==
  /\ lsnMax < MaxLsn
  /\ curLsn[p] = durLsn[p]                 \* no pending in-flight write for p
  /\ lsnMax' = lsnMax + 1
  /\ curLsn' = [curLsn EXCEPT ![p] = lsnMax + 1]
  /\ UNCHANGED <<durLsn, baseLsn, capturedLsn, durableLsn, barrierLsn, ckptLsn,
                 ckptPersisted, recycleFrontier, phase, collected, captured,
                 skipped, crashes>>

\* The WAL writer fsyncs one more record. The page written at the newly-durable
\* LSN now has durable content (recovery would restore it).
DrainWal ==
  /\ durableLsn < lsnMax
  /\ durableLsn' = durableLsn + 1
  /\ durLsn' = [p \in Pages |-> IF curLsn[p] <= durableLsn + 1
                                THEN curLsn[p] ELSE durLsn[p]]
  /\ UNCHANGED <<curLsn, baseLsn, capturedLsn, lsnMax, barrierLsn, ckptLsn,
                 ckptPersisted, recycleFrontier, phase, collected, captured,
                 skipped, crashes>>

-----------------------------------------------------------------------------
(* Checkpoint cycle *)

\* CK-01: snapshot the post-flush durable frontier; collect every page whose durable
\* base is stale (baseLsn < curLsn). A page with an in-flight write IS collected — it
\* then gets SKIPPED (a seqlock/ACW conflict), which holds the coverage gate so its
\* uncaptured content's WAL record is never recycled.
Barrier ==
  /\ phase = "idle"
  /\ barrierLsn' = durableLsn
  /\ collected'  = {p \in Pages : baseLsn[p] < curLsn[p]}
  /\ captured'   = {}
  /\ skipped'    = {}
  /\ capturedLsn' = [p \in Pages |-> 0]
  /\ phase'      = "capture"
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, lsnMax, durableLsn, ckptLsn,
                 ckptPersisted, recycleFrontier, crashes>>

\* Capture a collected page (nondeterministic: it was capturable this cycle).
CapturePage(p) ==
  /\ phase = "capture"
  /\ p \in collected
  /\ p \notin (captured \cup skipped)
  /\ captured' = captured \cup {p}
  /\ capturedLsn' = [capturedLsn EXCEPT ![p] = curLsn[p]]
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, lsnMax, durableLsn, barrierLsn,
                 ckptLsn, ckptPersisted, recycleFrontier, phase, collected,
                 skipped, crashes>>

\* Skip a collected page (nondeterministic: ACW>0 / odd seqlock — writer in flight).
SkipPage(p) ==
  /\ phase = "capture"
  /\ p \in collected
  /\ p \notin (captured \cup skipped)
  /\ skipped' = skipped \cup {p}
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, capturedLsn, lsnMax, durableLsn,
                 barrierLsn, ckptLsn, ckptPersisted, recycleFrontier, phase,
                 collected, captured, crashes>>

\* CK-02: flush2 then data fsync. Before the captured copies enter the durable base,
\* the WAL is flushed through the high-water LSN any captured page reflects (so a page
\* re-dirtied mid-capture has its record durable before the base reflects it). THEN the
\* captured copies enter the durable base at their captured LSN.
DataFsync ==
  /\ phase = "capture"
  /\ (captured \cup skipped) = collected      \* every collected page processed
  /\ LET capMax    == SetMax({ capturedLsn[p] : p \in captured })
         newDurable == IF capMax > durableLsn THEN capMax ELSE durableLsn
     IN /\ durableLsn' = newDurable            \* flush2: WAL covers captured content first
        /\ durLsn' = [p \in Pages |-> IF curLsn[p] <= newDurable THEN curLsn[p] ELSE durLsn[p]]
  /\ baseLsn' = [p \in Pages |-> IF p \in captured THEN capturedLsn[p] ELSE baseLsn[p]]
  /\ phase' = "gate"
  /\ UNCHANGED <<curLsn, capturedLsn, lsnMax, barrierLsn, ckptLsn, ckptPersisted,
                 recycleFrontier, collected, captured, skipped, crashes>>

\* CK-03: advance CheckpointLSN to the barrier ONLY if no page was skipped.
\* BreakCoverageGate=TRUE drops the gate -> the injected bug.
CoverageGate ==
  /\ phase = "gate"
  /\ LET advance == IF BreakCoverageGate THEN TRUE ELSE (skipped = {})
     IN ckptLsn' = IF advance THEN barrierLsn ELSE ckptLsn
  /\ phase' = "persist"
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, capturedLsn, lsnMax, durableLsn,
                 barrierLsn, ckptPersisted, recycleFrontier, collected,
                 captured, skipped, crashes>>

\* The meta-pair generation flip — the cycle's atomic commit point (CK-05, abstracted).
Persist ==
  /\ phase = "persist"
  /\ ckptPersisted' = ckptLsn
  /\ phase' = "recycle"
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, capturedLsn, lsnMax, durableLsn,
                 barrierLsn, ckptLsn, recycleFrontier, collected, captured,
                 skipped, crashes>>

\* CK-04: recycle WAL up to the PERSISTED checkpoint frontier, never beyond.
Recycle ==
  /\ phase = "recycle"
  /\ recycleFrontier' = ckptPersisted
  /\ phase' = "idle"
  /\ collected' = {}
  /\ captured'  = {}
  /\ skipped'   = {}
  /\ UNCHANGED <<curLsn, durLsn, baseLsn, capturedLsn, lsnMax, durableLsn,
                 barrierLsn, ckptLsn, ckptPersisted, crashes>>

-----------------------------------------------------------------------------
(* Crash anywhere — lose in-memory cycle state + un-fsynced writes; durable
   state (base, persisted checkpoint, recycle frontier, fsynced records) survives. *)
Crash ==
  /\ crashes < MaxCrashes
  /\ curLsn'   = durLsn                       \* un-fsynced writes lost; pages revert to durable content
  /\ lsnMax'   = durableLsn                    \* torn tail discarded
  /\ ckptLsn'  = ckptPersisted                 \* the un-persisted advance is lost
  /\ barrierLsn' = 0
  /\ capturedLsn' = [p \in Pages |-> 0]
  /\ phase'    = "idle"
  /\ collected' = {}
  /\ captured'  = {}
  /\ skipped'   = {}
  /\ crashes'  = crashes + 1
  /\ UNCHANGED <<durLsn, baseLsn, durableLsn, ckptPersisted, recycleFrontier>>

\* Terminal stutter (no deadlock once nothing more can happen).
Idle ==
  /\ phase = "idle"
  /\ lsnMax = MaxLsn
  /\ durableLsn = lsnMax
  /\ crashes >= MaxCrashes
  /\ \A p \in Pages : baseLsn[p] = curLsn[p]
  /\ UNCHANGED vars

-----------------------------------------------------------------------------
Next ==
  \/ \E p \in Pages : DirtyPage(p)
  \/ DrainWal
  \/ Barrier
  \/ \E p \in Pages : CapturePage(p)
  \/ \E p \in Pages : SkipPage(p)
  \/ DataFsync
  \/ CoverageGate
  \/ Persist
  \/ Recycle
  \/ Crash
  \/ Idle

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants *)

LsnVal == 0..MaxLsn

TypeOK ==
  /\ curLsn          \in [Pages -> LsnVal]
  /\ durLsn          \in [Pages -> LsnVal]
  /\ baseLsn         \in [Pages -> LsnVal]
  /\ capturedLsn     \in [Pages -> LsnVal]
  /\ lsnMax          \in LsnVal
  /\ durableLsn      \in LsnVal
  /\ barrierLsn      \in LsnVal
  /\ ckptLsn         \in LsnVal
  /\ ckptPersisted   \in LsnVal
  /\ recycleFrontier \in LsnVal
  /\ phase           \in {"idle","capture","gate","persist","recycle"}
  /\ crashes         \in 0..MaxCrashes

\* CK-01: the barrier never exceeds the durable frontier.
CK01_BarrierDurable == barrierLsn <= durableLsn

\* CK-02: a captured page's content is durable; the checkpoint never passes the barrier.
CK02_CapturedSubsetDurable ==
  /\ \A p \in Pages : baseLsn[p] <= durableLsn
  /\ ckptLsn <= durableLsn

\* CK-04 (+ WP-01): frontier ordering; never recycle past the persisted checkpoint.
CK04_RecycleBound ==
  /\ recycleFrontier <= ckptPersisted
  /\ ckptPersisted <= ckptLsn
  /\ ckptLsn <= durableLsn

\* HEADLINE (CK-03 o CK-04): no page has its latest committed content un-captured
\* AND its WAL record already recycled — i.e. every acknowledged write is recoverable.
NoLostPage ==
  \A p \in Pages : ~(baseLsn[p] < curLsn[p] /\ curLsn[p] <= recycleFrontier)

=============================================================================
