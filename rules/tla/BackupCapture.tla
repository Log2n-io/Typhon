---------------------------- MODULE BackupCapture ----------------------------
(***************************************************************************)
(* Online backup capture — Spec S6 (#1412, design PitBackup 08 §2,         *)
(* impl/1412-capture-plan.md §10).  A backup point is the data file frozen *)
(* at an instant F plus the WAL tail that makes it consistent.  This spec  *)
(* proves, exhaustively over small bounds, that the capture's image is the *)
(* file at F page for page while data-file writers keep running, that the  *)
(* tail holds every record any image page can reflect, that WAL reclaim    *)
(* never deletes a record the tail still needs, and that the change map    *)
(* never loses a written page, aborted captures included.                  *)
(*                                                                         *)
(* Invariants proven (each mapped to its rules/durability.md rule):        *)
(*   ChangeMapComplete         ->  BKP-01 (every page written since the     *)
(*                                 last COMPLETED freeze is in the live map *)
(*                                 or in the running capture's set) and     *)
(*                                 BKP-07 (an abort merges its set back)    *)
(*   ImageFidelity             ->  BKP-02 / BKP-03 (a completed point holds *)
(*                                 every page of its set as the file had    *)
(*                                 it at F, writers notwithstanding)         *)
(*   TailCoversImage           ->  BKP-04 (every record between C and what  *)
(*                                 an image page reflects is in the tail)   *)
(*   NoNeededRecordReclaimed   ->  BKP-04 (before the tail copy, reclaim    *)
(*                                 stays at or below C)                     *)
(*   HookNotRemovedUnderWriter ->  BKP-12 (the hook, and the memory behind  *)
(*                                 it, goes only once no writer that took   *)
(*                                 it is still before its write)            *)
(* Liveness (FairSpec, under weak fairness of every progress step):        *)
(*   WritersProgress           ->  BKP-12 (a writer inside the gate         *)
(*                                 eventually writes and leaves, without    *)
(*                                 any timeout: an abort never leaves one   *)
(*                                 waiting on a page for good)              *)
(*   GateReopens               ->  BKP-12 (a freeze or an uninstall that    *)
(*                                 closes the gate to new writers always    *)
(*                                 reopens it: a writer waiting to enter    *)
(*                                 eventually can)                          *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope):                     *)
(*   - a data-file write is one atomic step inside the gate; sync and      *)
(*     async writes are the same shared section.  The gate is a count and  *)
(*     a freeze flag (writer preference: no shared enter once requested);  *)
(*     its stripes, fences and bounded acquire are DataFileWriteGateTests' *)
(*   - a page is a version number plus the highest LSN it reflects (a      *)
(*     write reflects everything published so far, AP-01).  Checksums,    *)
(*     the page cache, the seqlock and the meta pair are NOT modeled       *)
(*     (C is a variable the checkpoint advances)                           *)
(*   - every LSN is its own segment, so "segments with lastLSN >= C" is    *)
(*     "records >= C", and reclaim deletes a prefix (WP-09's strict <)     *)
(*   - the tail copy is one step after the flush; rotation, the snapshot   *)
(*     lock and the sink's I/O are SegmentSnapshotTests' and the store's   *)
(*   - a writer's copy-before-write (claim, read, done) is one step; the   *)
(*     scanner's claim and read are two, which is the race that matters    *)
(*   - an abort may happen at any point of a capture.  It is not atomic,  *)
(*     as in the code: the flag (waiting writers are released, the scanner  *)
(*     stops), the hook's removal under the exclusive gate (freezeReq       *)
(*     raised, writers drained), then the merge of the DETACHED map (the    *)
(*     live map the freeze swapped out; a full capture's set is every page, *)
(*     its detached map is not).  The timeouts (CbwStallTimeout, the        *)
(*     uninstall's budget) are not modeled: liveness is proven without      *)
(*     them, and a timed-out uninstall leaves an inert hook, which passes   *)
(*     every write as a removed one does                                   *)
(*   - no crash: a crash makes the next point full (BKP-06), which needs   *)
(*     no proof of the image                                               *)
(*                                                                         *)
(* GENUINENESS — every mutant MUST violate (Mutant constant):              *)
(*   "noclaimwait"  a writer finding a page claimed and not done writes    *)
(*                  anyway -> ImageFidelity                                *)
(*   "nofloor"      the freeze sets no reclaim floor -> NoNeededRecord...  *)
(*                  and TailCoversImage                                    *)
(*   "noflush"      the tail is copied without waiting for PubF to be      *)
(*                  durable -> TailCoversImage                             *)
(*   "mapafterwrite" the map bit is set after the write, outside the gate  *)
(*                  -> ChangeMapComplete                                   *)
(*   "nogate"       writers ignore the freeze and the freeze ignores them  *)
(*                  -> ChangeMapComplete (a write mapped before F lands    *)
(*                  after it), ImageFidelity on other paths                *)
(*   "mergeimageset" an abort merges the image set back instead of the     *)
(*                  detached map: nothing for a full capture (the code's   *)
(*                  image set is null there) -> ChangeMapComplete          *)
(*   "uninstallnogate" the hook is removed without draining the writers    *)
(*                  -> HookNotRemovedUnderWriter                           *)
(*   "noabortrelease" an abort does not release a writer waiting on a page *)
(*                  the stopped scanner claimed -> WritersProgress (it     *)
(*                  holds the gate the uninstall waits for: a deadlock)    *)
(*   "timeoutkeepsgate" a freeze that times out does not reopen the gate   *)
(*                  -> GateReopens                                         *)
(*                                                                         *)
(* BOUNDS: BackupCapture.cfg (the gate's), 2 pages / 2 writers up to     *)
(* permutation / 1 version / 2 captures (2.76 M distinct, ~8 s on 32      *)
(* threads); -liveness, 2 writers / 1 capture under FairSpec (194 K, ~18 s);*)
(* -full, 2 versions (32.2 M, ~2 min, skipped by the gate).                *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS Pages, Writers, MaxVersion, MaxLsn, MaxCaptures, Mutant

ASSUME Mutant \in {"none", "noclaimwait", "nofloor", "noflush", "mapafterwrite", "nogate", "mergeimageset", "uninstallnogate", "noabortrelease",
                   "timeoutkeepsgate"}

NoLsn == MaxLsn + 1          \* "no floor": above every LSN
AnyPage == CHOOSE p \in Pages : TRUE   \* an idle writer's page and hook reference are reset, so idle writers do not multiply states

VARIABLES
  file, reflects,            \* [Pages -> version], [Pages -> highest LSN the version reflects]
  pub, dur, ckpt, reclaimed, \* published / durable frontier, CheckpointLSN, every LSN < reclaimed is deleted
  map, wsLast, wsCur,        \* live change map; ghosts: written since the last completed freeze / since this freeze
  wpc, wpage, hookRef,       \* writer pc, target page, whether the hook was installed when it entered the gate
  freezeReq, gateCount, hook,\* the write gate; the copy-before-write hook is installed
  phase, full, aborted,      \* capture phase; full or incremental; the capture failed
  B, detached, Fimg, Fref, C, PubF, floor,
  claimed, done, ingested, scanPage, tail, captures

vars == <<file, reflects, pub, dur, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount, hook, phase,
          full, aborted, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The capture holds a set (detached from the live map, or every page): from the freeze until its end.
Active == phase \in {"frozen", "flushed", "scan", "done", "aborting", "unhooking", "merging"}
\* The capture is copying: the hook copies pages of its set before they are overwritten.
Live == phase \in {"frozen", "flushed", "scan"}

TypeOK ==
  /\ file \in [Pages -> 0..(MaxVersion + Cardinality(Writers))]   \* writers may enter one page together
  /\ reflects \in [Pages -> 0..MaxLsn]
  /\ pub \in 0..MaxLsn /\ dur \in 0..pub /\ ckpt \in 0..dur
  /\ wpc \in [Writers -> {"idle", "map", "cbw", "write", "latemap"}]
  /\ hookRef \in [Writers -> BOOLEAN] /\ hook \in BOOLEAN /\ aborted \in BOOLEAN
  /\ phase \in {"idle", "requested", "frozen", "flushed", "scan", "done", "aborting", "unhooking", "merging"}

Init ==
  /\ file = [p \in Pages |-> 0]
  /\ reflects = [p \in Pages |-> 0]
  /\ pub = 0 /\ dur = 0 /\ ckpt = 0 /\ reclaimed = 0
  /\ map = {} /\ wsLast = {} /\ wsCur = {}
  /\ wpc = [w \in Writers |-> "idle"] /\ wpage = [w \in Writers |-> AnyPage] /\ hookRef = [w \in Writers |-> FALSE]
  /\ freezeReq = FALSE /\ gateCount = 0 /\ hook = FALSE
  /\ phase = "idle" /\ full = TRUE /\ aborted = FALSE
  /\ B = {} /\ detached = {} /\ Fimg = [p \in Pages |-> 0] /\ Fref = [p \in Pages |-> 0] /\ C = 0 /\ PubF = 0 /\ floor = NoLsn
  /\ claimed = {} /\ done = {} /\ ingested = [p \in Pages |-> 0] /\ scanPage = 0 /\ tail = {} /\ captures = 0

CaptureVars == <<phase, full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>
WalVars == <<pub, dur, ckpt, reclaimed>>

-----------------------------------------------------------------------------
\* The WAL and the checkpoint.

Publish ==
  /\ pub < MaxLsn
  /\ pub' = pub + 1
  /\ UNCHANGED <<file, reflects, dur, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

Flush ==
  /\ dur < pub
  /\ dur' = pub
  /\ UNCHANGED <<file, reflects, pub, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

\* Step 7: CheckpointLSN moves to the durable frontier (CK-03's coverage is S1's).
Checkpoint ==
  /\ ckpt < dur
  /\ ckpt' = dur
  /\ UNCHANGED <<file, reflects, pub, dur, reclaimed, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

\* Step 8 with W1: trim = min(CheckpointLSN, floor); records below it are deleted.
Reclaim ==
  /\ LET trim == IF ckpt < floor THEN ckpt ELSE floor
     IN /\ reclaimed < trim
        /\ reclaimed' = trim
  /\ UNCHANGED <<file, reflects, pub, dur, ckpt, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

-----------------------------------------------------------------------------
\* Data-file writers: enter the gate (taking the hook as it is), mark the map, copy-before-write, write, leave.

WEnter(w, p) ==
  /\ wpc[w] = "idle"
  /\ file[p] < MaxVersion
  /\ (~freezeReq \/ Mutant = "nogate")
  /\ wpc' = [wpc EXCEPT ![w] = "map"]
  /\ wpage' = [wpage EXCEPT ![w] = p]
  /\ hookRef' = [hookRef EXCEPT ![w] = hook]
  /\ gateCount' = gateCount + 1
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, freezeReq>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

WMap(w) ==
  /\ wpc[w] = "map"
  /\ map' = IF Mutant = "mapafterwrite" THEN map ELSE map \cup {wpage[w]}
  /\ wpc' = [wpc EXCEPT ![w] = "cbw"]
  /\ UNCHANGED <<file, reflects, wsLast, wsCur, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

\* The hook the writer took: pages outside the running capture's set, or already captured, pass. While the capture copies, a first toucher
\* copies and a writer finding the page claimed by someone else waits for it to be done (no step until then). Once it has aborted, every
\* write passes: the abort releases the waiters (the scanner that claimed the page has stopped and will never set it done).
WCbw(w) ==
  LET p == wpage[w]
      engaged == hookRef[w] /\ p \in B /\ p \notin done
  IN
  /\ wpc[w] = "cbw"
  /\ IF engaged /\ Live
       THEN IF p \notin claimed
              THEN /\ claimed' = claimed \cup {p}
                   /\ done' = done \cup {p}
                   /\ ingested' = [ingested EXCEPT ![p] = file[p]]
              ELSE /\ (Mutant = "noclaimwait")
                   /\ UNCHANGED <<claimed, done, ingested>>
       ELSE /\ ~(engaged /\ p \in claimed /\ aborted /\ Mutant = "noabortrelease")
            /\ UNCHANGED <<claimed, done, ingested>>
  /\ wpc' = [wpc EXCEPT ![w] = "write"]
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, scanPage, tail, captures>>

WWrite(w) ==
  LET p == wpage[w] IN
  /\ wpc[w] = "write"
  /\ file' = [file EXCEPT ![p] = file[p] + 1]
  /\ reflects' = [reflects EXCEPT ![p] = pub]
  /\ wsLast' = wsLast \cup {p}
  /\ wsCur' = IF Active THEN wsCur \cup {p} ELSE wsCur
  /\ gateCount' = gateCount - 1
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "mapafterwrite" THEN "latemap" ELSE "idle"]
  /\ wpage' = IF Mutant = "mapafterwrite" THEN wpage ELSE [wpage EXCEPT ![w] = AnyPage]
  /\ hookRef' = [hookRef EXCEPT ![w] = FALSE]
  /\ UNCHANGED <<map, freezeReq>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

\* Mutant only: the map bit, set after the write has left the gate.
WLateMap(w) ==
  /\ wpc[w] = "latemap"
  /\ map' = map \cup {wpage[w]}
  /\ wpc' = [wpc EXCEPT ![w] = "idle"]
  /\ wpage' = [wpage EXCEPT ![w] = AnyPage]
  /\ UNCHANGED <<file, reflects, wsLast, wsCur, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

-----------------------------------------------------------------------------
\* The capture.

RequestFreeze ==
  /\ phase = "idle" /\ captures < MaxCaptures
  /\ phase' = "requested" /\ freezeReq' = TRUE
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The bounded acquire gave up: the gate reopens, the capture fails before anything was detached.
FreezeTimeout ==
  /\ phase = "requested"
  /\ phase' = "idle" /\ freezeReq' = (Mutant = "timeoutkeepsgate") /\ captures' = captures + 1
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail>>

\* The exclusive section: no write in flight; the live map is swapped out (detached), the image, its set, C, PubF and the floor are
\* fixed; the hook goes in.
Freeze ==
  /\ phase = "requested"
  /\ (gateCount = 0 \/ Mutant = "nogate")
  /\ \E f \in BOOLEAN :
       /\ full' = (f \/ captures = 0)
       /\ B' = IF full' THEN Pages ELSE map
  /\ detached' = map
  /\ map' = {}
  /\ wsCur' = {}
  /\ Fimg' = file /\ Fref' = reflects
  /\ C' = ckpt /\ PubF' = pub
  /\ floor' = IF Mutant = "nofloor" THEN NoLsn ELSE ckpt
  /\ claimed' = {} /\ done' = {} /\ scanPage' = 0 /\ tail' = {}
  /\ hook' = TRUE
  /\ phase' = "frozen" /\ freezeReq' = FALSE
  /\ UNCHANGED <<file, reflects, wsLast, wpc, wpage, hookRef, gateCount, aborted, ingested, captures>> /\ UNCHANGED WalVars

\* C1: the tail copy waits until every record published at the freeze is durable.
TailFlush ==
  /\ phase = "frozen"
  /\ (dur >= PubF \/ Mutant = "noflush")
  /\ phase' = "flushed"
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The copy: every durable, unreclaimed record at or above C (C2: >=). Then the floor is released.
TailCopy ==
  /\ phase = "flushed"
  /\ tail' = {l \in 1..MaxLsn : l <= dur /\ l >= C /\ l >= reclaimed}
  /\ floor' = NoLsn
  /\ phase' = "scan"
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, claimed, done, ingested, scanPage, captures>>

\* The scanner claims one page of the set nobody has claimed, then reads it in a later step.
ScanClaim(p) ==
  /\ phase = "scan" /\ scanPage = 0
  /\ p \in B /\ p \notin claimed
  /\ claimed' = claimed \cup {p} /\ scanPage' = p
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, done, ingested, tail, captures>>

ScanRead ==
  /\ phase = "scan" /\ scanPage /= 0
  /\ ingested' = [ingested EXCEPT ![scanPage] = file[scanPage]]
  /\ done' = done \cup {scanPage}
  /\ scanPage' = 0
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, tail, captures>>

\* Every page of the set is captured: the point is complete. Its freeze becomes the lineage's.
Complete ==
  /\ phase = "scan" /\ scanPage = 0 /\ done = B
  /\ phase' = "done"
  /\ wsLast' = wsCur
  /\ UNCHANGED <<file, reflects, map, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* Any failure, anywhere while copying (BKP-07), step 1: the flag. Writers waiting on a claimed page are released (WCbw), the scanner
\* stops (ScanRead needs "scan"); the hook stays installed, passing every write, until its removal.
AbortFlag ==
  /\ Live
  /\ phase' = "aborting" /\ aborted' = TRUE
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The hook's removal, completed or aborted: a request under the exclusive gate (freezeReq raised, no new writer enters) ...
UninstallRequest ==
  /\ phase \in {"done", "aborting"}
  /\ phase' = "unhooking" /\ freezeReq' = TRUE
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, hookRef, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, aborted, hook, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* Between captures their bookkeeping holds nothing (kept at its initial values, which keeps the state space small).
ClearCapture ==
  /\ full' = TRUE /\ B' = {} /\ detached' = {} /\ Fimg' = [p \in Pages |-> 0] /\ Fref' = [p \in Pages |-> 0] /\ C' = 0 /\ PubF' = 0
  /\ floor' = NoLsn /\ claimed' = {} /\ done' = {} /\ ingested' = [p \in Pages |-> 0] /\ scanPage' = 0 /\ tail' = {} /\ wsCur' = {}

\* ... and, once the writers in flight have drained, the removal itself. Only then may the capture's memory go. A completed capture ends
\* here; an aborted one goes on to its merge.
UninstallRemove ==
  /\ phase = "unhooking"
  /\ (gateCount = 0 \/ Mutant = "uninstallnogate")
  /\ hook' = FALSE /\ freezeReq' = FALSE
  /\ IF aborted
       THEN /\ phase' = "merging"
            /\ UNCHANGED <<full, B, detached, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, wsCur, captures>>
       ELSE /\ phase' = "idle" /\ captures' = captures + 1
            /\ ClearCapture
  /\ UNCHANGED <<file, reflects, map, wsLast, wpc, wpage, hookRef, gateCount, aborted>> /\ UNCHANGED WalVars

\* The abort's last step (BKP-07): the detached map goes back into the live one, the floor is released.
AbortMerge ==
  /\ phase = "merging"
  /\ map' = map \cup (IF Mutant = "mergeimageset" THEN (IF full THEN {} ELSE B) ELSE detached)
  /\ phase' = "idle" /\ aborted' = FALSE /\ captures' = captures + 1
  /\ ClearCapture
  /\ UNCHANGED <<file, reflects, wsLast, wpc, wpage, hookRef, freezeReq, gateCount, hook>> /\ UNCHANGED WalVars

\* Terminal stutter: nothing in flight.
Idle ==
  /\ phase = "idle"
  /\ \A w \in Writers : wpc[w] = "idle"
  /\ UNCHANGED vars

-----------------------------------------------------------------------------
Next ==
  \/ Publish \/ Flush \/ Checkpoint \/ Reclaim
  \/ \E w \in Writers : \E p \in Pages : WEnter(w, p)
  \/ \E w \in Writers : WMap(w) \/ WCbw(w) \/ WWrite(w) \/ WLateMap(w)
  \/ RequestFreeze \/ FreezeTimeout \/ Freeze \/ TailFlush \/ TailCopy
  \/ \E p \in Pages : ScanClaim(p)
  \/ ScanRead \/ Complete \/ AbortFlag \/ UninstallRequest \/ UninstallRemove \/ AbortMerge
  \/ Idle

Spec == Init /\ [][Next]_vars

\* Weak fairness of every progress step. Not of the environment's choices (a writer starting a write, a commit, a checkpoint, starting a
\* capture, aborting one) nor of FreezeTimeout: a timeout is a last resort, and liveness is proven without it.
Fairness ==
  /\ \A w \in Writers : WF_vars(WMap(w)) /\ WF_vars(WCbw(w)) /\ WF_vars(WWrite(w)) /\ WF_vars(WLateMap(w))
  /\ WF_vars(Flush) /\ WF_vars(Freeze) /\ WF_vars(TailFlush) /\ WF_vars(TailCopy) /\ WF_vars(\E p \in Pages : ScanClaim(p))
  /\ WF_vars(ScanRead) /\ WF_vars(Complete) /\ WF_vars(UninstallRequest) /\ WF_vars(UninstallRemove) /\ WF_vars(AbortMerge)

FairSpec == Spec /\ Fairness

-----------------------------------------------------------------------------
\* BKP-01 / BKP-07: no write is ever lost to the next incremental: it is in the live map, or in the map the running capture detached,
\* which an abort merges back.
ChangeMapComplete == wsLast \subseteq map \cup (IF Active THEN detached ELSE {})

\* BKP-02 / BKP-03: a completed point holds every page of its set as it was at the freeze.
ImageFidelity == phase = "done" => \A p \in B : p \in done /\ ingested[p] = Fimg[p]

\* BKP-04: replaying the tail from C covers everything any page of the image reflects.
TailCoversImage == phase \in {"scan", "done"} => \A p \in Pages : \A l \in (C + 1)..Fref[p] : l \in tail

\* BKP-04: until the tail is copied, nothing at or above C is reclaimed.
NoNeededRecordReclaimed == phase \in {"frozen", "flushed"} => reclaimed <= C

\* BKP-12 (C1 in the review of #1412): a writer that took the hook at its gate entry still finds it, and the memory behind it, until it is
\* past its copy-before-write.
HookNotRemovedUnderWriter == \A w \in Writers : (wpc[w] \in {"map", "cbw"} /\ hookRef[w]) => hook

\* BKP-12, liveness: a writer inside the gate eventually has written and left; the gate, once closed to new writers, reopens.
WritersProgress == \A w \in Writers : (wpc[w] /= "idle") ~> (wpc[w] = "idle")
GateReopens == freezeReq ~> ~freezeReq

\* Writers are interchangeable: the safety cfgs check one representative per permutation (never with a PROPERTY: TLC's symmetry is
\* unsound for liveness).
WriterSymmetry == Permutations(Writers)
=============================================================================
