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
(*   - an abort is a nondeterministic step at any point of a capture; its  *)
(*     timing (CbwStallTimeout) is CbwStallTests'                          *)
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
(*                                                                         *)
(* BOUNDS: the gate's cfg, 2 pages / 1 writer / 2 captures (1.06 M        *)
(* distinct states, ~3 s); -full, 2 pages / 2 writers (12.6 M, ~30 s).     *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS Pages, Writers, MaxVersion, MaxLsn, MaxCaptures, Mutant

ASSUME Mutant \in {"none", "noclaimwait", "nofloor", "noflush", "mapafterwrite", "nogate"}

NoLsn == MaxLsn + 1          \* "no floor": above every LSN

VARIABLES
  file, reflects,            \* [Pages -> version], [Pages -> highest LSN the version reflects]
  pub, dur, ckpt, reclaimed, \* published / durable frontier, CheckpointLSN, every LSN < reclaimed is deleted
  map, wsLast, wsCur,        \* live change map; ghosts: written since the last completed freeze / since this freeze
  wpc, wpage,                \* writer pc, target page
  freezeReq, gateCount,      \* the write gate
  phase, full,               \* capture phase; full or incremental
  B, Fimg, Fref, C, PubF, floor,
  claimed, done, ingested, scanPage, tail, captures

vars == <<file, reflects, pub, dur, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount, phase, full,
          B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

Active == phase \in {"frozen", "flushed", "scan", "done"}
HookOn == phase \in {"frozen", "flushed", "scan"}

TypeOK ==
  /\ file \in [Pages -> 0..(MaxVersion + Cardinality(Writers))]   \* writers may enter one page together
  /\ reflects \in [Pages -> 0..MaxLsn]
  /\ pub \in 0..MaxLsn /\ dur \in 0..pub /\ ckpt \in 0..dur
  /\ wpc \in [Writers -> {"idle", "map", "cbw", "write", "latemap"}]
  /\ phase \in {"idle", "requested", "frozen", "flushed", "scan", "done"}

Init ==
  /\ file = [p \in Pages |-> 0]
  /\ reflects = [p \in Pages |-> 0]
  /\ pub = 0 /\ dur = 0 /\ ckpt = 0 /\ reclaimed = 0
  /\ map = {} /\ wsLast = {} /\ wsCur = {}
  /\ wpc = [w \in Writers |-> "idle"] /\ wpage = [w \in Writers |-> CHOOSE p \in Pages : TRUE]
  /\ freezeReq = FALSE /\ gateCount = 0
  /\ phase = "idle" /\ full = TRUE
  /\ B = {} /\ Fimg = [p \in Pages |-> 0] /\ Fref = [p \in Pages |-> 0] /\ C = 0 /\ PubF = 0 /\ floor = NoLsn
  /\ claimed = {} /\ done = {} /\ ingested = [p \in Pages |-> 0] /\ scanPage = 0 /\ tail = {} /\ captures = 0

CaptureVars == <<phase, full, B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>
WalVars == <<pub, dur, ckpt, reclaimed>>

-----------------------------------------------------------------------------
\* The WAL and the checkpoint.

Publish ==
  /\ pub < MaxLsn
  /\ pub' = pub + 1
  /\ UNCHANGED <<file, reflects, dur, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

Flush ==
  /\ dur < pub
  /\ dur' = pub
  /\ UNCHANGED <<file, reflects, pub, ckpt, reclaimed, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

\* Step 7: CheckpointLSN moves to the durable frontier (CK-03's coverage is S1's).
Checkpoint ==
  /\ ckpt < dur
  /\ ckpt' = dur
  /\ UNCHANGED <<file, reflects, pub, dur, reclaimed, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

\* Step 8 with W1: trim = min(CheckpointLSN, floor); records below it are deleted.
Reclaim ==
  /\ LET trim == IF ckpt < floor THEN ckpt ELSE floor
     IN /\ reclaimed < trim
        /\ reclaimed' = trim
  /\ UNCHANGED <<file, reflects, pub, dur, ckpt, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>>
  /\ UNCHANGED CaptureVars

-----------------------------------------------------------------------------
\* Data-file writers: enter the gate, mark the map, copy-before-write, write, leave.

WEnter(w, p) ==
  /\ wpc[w] = "idle"
  /\ file[p] < MaxVersion
  /\ (~freezeReq \/ Mutant = "nogate")
  /\ wpc' = [wpc EXCEPT ![w] = "map"]
  /\ wpage' = [wpage EXCEPT ![w] = p]
  /\ gateCount' = gateCount + 1
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, freezeReq>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

WMap(w) ==
  /\ wpc[w] = "map"
  /\ map' = IF Mutant = "mapafterwrite" THEN map ELSE map \cup {wpage[w]}
  /\ wpc' = [wpc EXCEPT ![w] = "cbw"]
  /\ UNCHANGED <<file, reflects, wsLast, wsCur, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

\* The hook: pages outside the running capture's set, or already captured, pass. A first toucher copies; a writer finding the page
\* claimed by someone else waits for it to be done (no step until then).
WCbw(w) ==
  LET p == wpage[w] IN
  /\ wpc[w] = "cbw"
  /\ IF HookOn /\ p \in B /\ p \notin done
       THEN IF p \notin claimed
              THEN /\ claimed' = claimed \cup {p}
                   /\ done' = done \cup {p}
                   /\ ingested' = [ingested EXCEPT ![p] = file[p]]
              ELSE /\ (Mutant = "noclaimwait")
                   /\ UNCHANGED <<claimed, done, ingested>>
       ELSE UNCHANGED <<claimed, done, ingested>>
  /\ wpc' = [wpc EXCEPT ![w] = "write"]
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, B, Fimg, Fref, C, PubF, floor, scanPage, tail, captures>>

WWrite(w) ==
  LET p == wpage[w] IN
  /\ wpc[w] = "write"
  /\ file' = [file EXCEPT ![p] = file[p] + 1]
  /\ reflects' = [reflects EXCEPT ![p] = pub]
  /\ wsLast' = wsLast \cup {p}
  /\ wsCur' = IF Active THEN wsCur \cup {p} ELSE wsCur
  /\ gateCount' = gateCount - 1
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "mapafterwrite" THEN "latemap" ELSE "idle"]
  /\ UNCHANGED <<map, wpage, freezeReq>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

\* Mutant only: the map bit, set after the write has left the gate.
WLateMap(w) ==
  /\ wpc[w] = "latemap"
  /\ map' = map \cup {wpage[w]}
  /\ wpc' = [wpc EXCEPT ![w] = "idle"]
  /\ UNCHANGED <<file, reflects, wsLast, wsCur, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars /\ UNCHANGED CaptureVars

-----------------------------------------------------------------------------
\* The capture.

RequestFreeze ==
  /\ phase = "idle" /\ captures < MaxCaptures
  /\ phase' = "requested" /\ freezeReq' = TRUE
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The bounded acquire gave up: the gate reopens, the capture fails before anything was detached.
FreezeTimeout ==
  /\ phase = "requested"
  /\ phase' = "idle" /\ freezeReq' = FALSE /\ captures' = captures + 1
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail>>

\* The exclusive section: no write in flight; the image, its set, C, PubF and the floor are fixed; the hook goes in.
Freeze ==
  /\ phase = "requested"
  /\ (gateCount = 0 \/ Mutant = "nogate")
  /\ \E f \in BOOLEAN :
       /\ full' = (f \/ captures = 0)
       /\ B' = IF full' THEN Pages ELSE map
  /\ map' = {}
  /\ wsCur' = {}
  /\ Fimg' = file /\ Fref' = reflects
  /\ C' = ckpt /\ PubF' = pub
  /\ floor' = IF Mutant = "nofloor" THEN NoLsn ELSE ckpt
  /\ claimed' = {} /\ done' = {} /\ scanPage' = 0 /\ tail' = {}
  /\ phase' = "frozen" /\ freezeReq' = FALSE
  /\ UNCHANGED <<file, reflects, wsLast, wpc, wpage, gateCount, ingested, captures>> /\ UNCHANGED WalVars

\* C1: the tail copy waits until every record published at the freeze is durable.
TailFlush ==
  /\ phase = "frozen"
  /\ (dur >= PubF \/ Mutant = "noflush")
  /\ phase' = "flushed"
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* The copy: every durable, unreclaimed record at or above C (C2: >=). Then the floor is released.
TailCopy ==
  /\ phase = "flushed"
  /\ tail' = {l \in 1..MaxLsn : l <= dur /\ l >= C /\ l >= reclaimed}
  /\ floor' = NoLsn
  /\ phase' = "scan"
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, B, Fimg, Fref, C, PubF, claimed, done, ingested, scanPage, captures>>

\* The scanner claims one page of the set nobody has claimed, then reads it in a later step.
ScanClaim(p) ==
  /\ phase = "scan" /\ scanPage = 0
  /\ p \in B /\ p \notin claimed
  /\ claimed' = claimed \cup {p} /\ scanPage' = p
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, B, Fimg, Fref, C, PubF, floor, done, ingested, tail, captures>>

ScanRead ==
  /\ phase = "scan" /\ scanPage /= 0
  /\ ingested' = [ingested EXCEPT ![scanPage] = file[scanPage]]
  /\ done' = done \cup {scanPage}
  /\ scanPage' = 0
  /\ UNCHANGED <<file, reflects, map, wsLast, wsCur, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<phase, full, B, Fimg, Fref, C, PubF, floor, claimed, tail, captures>>

\* Every page of the set is captured: the point is complete. Its freeze becomes the lineage's.
Complete ==
  /\ phase = "scan" /\ scanPage = 0 /\ done = B
  /\ phase' = "done"
  /\ wsLast' = wsCur
  /\ UNCHANGED <<file, reflects, map, wsCur, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars
  /\ UNCHANGED <<full, B, Fimg, Fref, C, PubF, floor, claimed, done, ingested, scanPage, tail, captures>>

\* Between captures their bookkeeping holds nothing (kept at its initial values, which keeps the state space small).
ClearCapture ==
  /\ full' = TRUE /\ B' = {} /\ Fimg' = [p \in Pages |-> 0] /\ Fref' = [p \in Pages |-> 0] /\ C' = 0 /\ PubF' = 0
  /\ floor' = NoLsn /\ claimed' = {} /\ done' = {} /\ ingested' = [p \in Pages |-> 0] /\ scanPage' = 0 /\ tail' = {} /\ wsCur' = {}

\* The hook comes out under the exclusive gate.
Uninstall ==
  /\ phase = "done" /\ gateCount = 0
  /\ phase' = "idle" /\ captures' = captures + 1
  /\ ClearCapture
  /\ UNCHANGED <<file, reflects, map, wsLast, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars

\* Any failure, anywhere (BKP-07): the set goes back into the live map, the floor is released, the hook removed; waiting writers proceed.
Abort ==
  /\ phase \in {"frozen", "flushed", "scan"}
  /\ map' = map \cup B
  /\ phase' = "idle" /\ captures' = captures + 1
  /\ ClearCapture
  /\ UNCHANGED <<file, reflects, wsLast, wpc, wpage, freezeReq, gateCount>> /\ UNCHANGED WalVars

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
  \/ ScanRead \/ Complete \/ Uninstall \/ Abort
  \/ Idle

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
\* BKP-01 / BKP-07: no write is ever lost to the next incremental.
ChangeMapComplete == wsLast \subseteq map \cup (IF Active THEN B ELSE {})

\* BKP-02 / BKP-03: a completed point holds every page of its set as it was at the freeze.
ImageFidelity == phase = "done" => \A p \in B : p \in done /\ ingested[p] = Fimg[p]

\* BKP-04: replaying the tail from C covers everything any page of the image reflects.
TailCoversImage == phase \in {"scan", "done"} => \A p \in Pages : \A l \in (C + 1)..Fref[p] : l \in tail

\* BKP-04: until the tail is copied, nothing at or above C is reclaimed.
NoNeededRecordReclaimed == phase \in {"frozen", "flushed"} => reclaimed <= C
=============================================================================
