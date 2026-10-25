----------------------------- MODULE WalArchive -----------------------------
(***************************************************************************)
(* Continuous WAL archiving — Spec S7 (#1418, impl/1418-wal-archive-plan   *)
(* §5.2).  The checkpoint's step 8 reclaims sealed WAL segments; with      *)
(* archiving configured an archive floor holds every segment the archiver  *)
(* has not made durable in the store, unless the backlog policy released   *)
(* it.  This spec proves, exhaustively over small bounds and across        *)
(* crashes and the open's reclaim, that no segment leaves the database     *)
(* un-archived but by a counted release, that the floor never stands past  *)
(* a segment the store does not hold, and that a restore never accepts a   *)
(* target past a hole in the archive.                                      *)
(*                                                                         *)
(* Invariants proven (each mapped to its rules/durability.md rule):        *)
(*   ArchivedOrReleased    ->  BKP-10 (every reclaimed segment is in the   *)
(*                             store, or was reclaimed under a counted     *)
(*                             release: a visible gap)                     *)
(*   FloorHoldsUnarchived  ->  BKP-10 (an engaged floor never stands past  *)
(*                             a segment on disk the store does not hold:  *)
(*                             the raise follows the durable publish)      *)
(*   FloorAtArchiveEnd     ->  BKP-10 (the floor is at most the archive's  *)
(*                             end + 1, at every reopen too: AC-A4)        *)
(*   NoSilentHole          ->  BKP-10, BKP-11 (a restore to L that the     *)
(*                             planner's walk accepts replays every LSN up *)
(*                             to L: the walk judges the archive's entries *)
(*                             by their metadata — continuity, the footer's *)
(*                             GapBefore and dropped range — and the       *)
(*                             invariant checks what they really hold)     *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS (the proof's honest scope):                     *)
(*   - every LSN is its own segment (as S6), appended durable in one step; *)
(*     a segment is sealed after it is durable (rotation follows the       *)
(*     durable advance, WA-15).  Segment sizes, the backlog sum and the    *)
(*     limit are one nondeterministic Release: any instant may cross it    *)
(*   - the base of every restore is the genesis image (P = 0): a point's   *)
(*     tail and the tail/archive agreement (WA-11) are S6 and the          *)
(*     planner's (ArchiveRestorePlannerTests); the cut inside a segment    *)
(*     is a prefix of the log, which S2 (CommitRecovery) proves recovers   *)
(*     to exactly the committed prefix                                     *)
(*   - the store's publish (temp, flush, rename without replace, directory *)
(*     flush) is one durable step (ArchiveStoreCrashTests crash every      *)
(*     file-system operation of it); a crash loses nothing durable         *)
(*   - step 8 is one atomic step: the CAS of a release against a raise in  *)
(*     the same cycle is WalArchiveFloorTests'.  The checkpoint advances   *)
(*     CheckpointLSN to the durable LSN                                    *)
(*   - a crash and the next open are one step: the floor restarts engaged  *)
(*     at 0 (reclaimFloorAtOpen), the archiver's memory is rebuilt from    *)
(*     the store, every segment on disk is sealed (adopted)                *)
(*   - the torn-tail seal (WA-9) is ArchiveTornTailTests': a crash may     *)
(*     tear the active segment after recovery applied its record (a fence  *)
(*     chunk); its archived copy then lacks that record and its footer     *)
(*     names it as a dropped range                                         *)
(*   - no liveness: the engine never waits on the archive (BKP-12), and    *)
(*     the archive's own progress is not a safety property                 *)
(*                                                                         *)
(* GENUINENESS — every mutant MUST violate (Mutant constant):              *)
(*   "raisebeforepublish" the floor is raised before the copy is durable   *)
(*                        -> FloorHoldsUnarchived, ArchivedOrReleased      *)
(*   "openreclaim"        the open reclaims below CheckpointLSN, ignoring  *)
(*                        the floor -> ArchivedOrReleased                  *)
(*   "nofloor"            step 8 ignores the archive floor                 *)
(*                        -> ArchivedOrReleased                            *)
(*   "continuityunchecked" the walk takes every archived entry up to L,    *)
(*                        neither Continues nor GapBefore -> NoSilentHole  *)
(*   "droppedunchecked"   the walk ignores a footer's dropped range        *)
(*                        -> NoSilentHole                                  *)
(*   "releasenocount"     a release is not counted: no gap evidence        *)
(*                        -> ArchivedOrReleased                            *)
(***************************************************************************)
EXTENDS Naturals, FiniteSets

CONSTANTS MaxLsn, MaxCrashes, Mutant

ASSUME MaxLsn \in Nat /\ MaxCrashes \in Nat
ASSUME Mutant \in {"none", "raisebeforepublish", "openreclaim", "nofloor", "continuityunchecked", "releasenocount", "droppedunchecked"}

VARIABLES
  durable,      \* the highest durable LSN; every LSN is its own segment
  sealed,       \* the highest sealed segment (sealed <= durable)
  ckpt,         \* CheckpointLSN: step 8 trims segments wholly below it
  present,      \* segment files on disk
  archived,     \* segments durable in the store
  inflight,     \* the segment being archived (published, floor not raised yet); 0 = none
  floor,        \* the archive floor: an engaged floor keeps every segment s >= floor
  released,     \* the backlog policy released the floor (step 8 trims as if archiving were off)
  counted,      \* that release was counted: a point is requested and the gap has its reason
  started,      \* the archiver opened the store and published its floor this session
  archThrough,  \* the archiver's ArchivedThroughLsn
  gone,         \* segments reclaimed
  relGhost,     \* segments reclaimed un-archived under a counted release: the visible gaps
  crashes,
  torn,         \* segments a crash tore after recovery applied their record: sealed, their archived copy lacks it
  dropped,      \* archived entries whose footer names a dropped range (a torn segment's): the copy does not hold the record
  gapFlag       \* archived entries whose footer records GapBefore (published past archThrough + 1)

vars == <<durable, sealed, ckpt, present, archived, inflight, floor, released, counted, started, archThrough, gone, relGhost, crashes, torn, dropped, gapFlag>>

Lsns == 1 .. MaxLsn

Max(S) == IF S = {} THEN 0 ELSE CHOOSE x \in S : \A y \in S : y <= x
Min(S) == CHOOSE x \in S : \A y \in S : x <= y

TypeOK ==
  /\ durable \in 0 .. MaxLsn /\ sealed \in 0 .. durable /\ ckpt \in 0 .. durable
  /\ present \subseteq Lsns /\ archived \subseteq Lsns /\ gone \subseteq Lsns /\ relGhost \subseteq gone
  /\ inflight \in 0 .. MaxLsn /\ floor \in 0 .. MaxLsn + 1 /\ archThrough \in 0 .. MaxLsn
  /\ released \in BOOLEAN /\ counted \in BOOLEAN /\ started \in BOOLEAN /\ crashes \in 0 .. MaxCrashes
  /\ torn \subseteq Lsns /\ dropped \subseteq archived /\ gapFlag \subseteq archived

Init ==
  /\ durable = 0 /\ sealed = 0 /\ ckpt = 0
  /\ present = {} /\ archived = {} /\ inflight = 0
  /\ floor = 0 /\ released = FALSE /\ counted = FALSE /\ started = FALSE
  /\ archThrough = 0 /\ gone = {} /\ relGhost = {} /\ crashes = 0
  /\ torn = {} /\ dropped = {} /\ gapFlag = {}

\* A commit: its segment is on disk and durable.
Append ==
  /\ durable < MaxLsn
  /\ durable' = durable + 1
  /\ present' = present \cup {durable + 1}
  /\ UNCHANGED <<sealed, ckpt, archived, inflight, floor, released, counted, started, archThrough, gone, relGhost, crashes, torn, dropped, gapFlag>>

\* Rotation seals the oldest durable segment not sealed yet.
Seal ==
  /\ sealed < durable
  /\ sealed' = sealed + 1
  /\ UNCHANGED <<durable, ckpt, present, archived, inflight, floor, released, counted, started, archThrough, gone, relGhost, crashes, torn, dropped,
                 gapFlag>>

Checkpoint ==
  /\ ckpt < durable
  /\ ckpt' = durable
  /\ UNCHANGED <<durable, sealed, present, archived, inflight, floor, released, counted, started, archThrough, gone, relGhost, crashes, torn, dropped,
                 gapFlag>>

\* Step 8's trim: min(CheckpointLSN, floor) with the floor engaged; CheckpointLSN alone once released (or, mutant, always).
Trim == IF released \/ Mutant = "nofloor" THEN ckpt ELSE IF ckpt < floor THEN ckpt ELSE floor

\* Step 8: every sealed segment wholly below the trim is deleted (WP-09's strict <). Under a counted release an un-archived one is a gap.
Reclaim ==
  LET doomed == {s \in present : s <= sealed /\ s < Trim}
  IN /\ doomed # {}
     /\ present' = present \ doomed
     /\ gone' = gone \cup doomed
     /\ relGhost' = IF released /\ counted THEN relGhost \cup (doomed \ archived) ELSE relGhost
     /\ UNCHANGED <<durable, sealed, ckpt, archived, inflight, floor, released, counted, started, archThrough, crashes, torn, dropped, gapFlag>>

\* The backlog policy: the bytes only the floor holds passed the limit. Counted (a point requested, the reason recorded) unless mutant.
Release ==
  /\ ~released
  /\ released' = TRUE
  /\ counted' = (Mutant # "releasenocount")
  /\ UNCHANGED <<durable, sealed, ckpt, present, archived, inflight, floor, started, archThrough, gone, relGhost, crashes, torn, dropped, gapFlag>>

\* The archiver opens the store and publishes its floor: the archive's end + 1.
Start ==
  /\ ~started
  /\ started' = TRUE
  /\ floor' = archThrough + 1
  /\ released' = FALSE
  /\ counted' = FALSE
  /\ UNCHANGED <<durable, sealed, ckpt, present, archived, inflight, archThrough, gone, relGhost, crashes, torn, dropped, gapFlag>>

\* The oldest sealed, durable segment on disk past the archive's end (a released gap is skipped: archiving resumes at the oldest present).
Pending == {s \in present \ archived : s <= sealed /\ s > archThrough}

\* What the archiver records in an entry's footer as it archives s (ArchiveOne): GapBefore when s does not follow the archive's end; a dropped range
\* when s was torn at a crash (its copy is kept up to its last whole frame, the record recovery applied from the torn frame is not in it).
Record(s) ==
  /\ archived' = archived \cup {s}
  /\ dropped' = IF s \in torn THEN dropped \cup {s} ELSE dropped
  /\ gapFlag' = IF archThrough > 0 /\ s > archThrough + 1 THEN gapFlag \cup {s} ELSE gapFlag

\* The archiver's publish of the next segment: durable in the store. Mutant raisebeforepublish raises the floor here instead.
Publish ==
  /\ started /\ inflight = 0 /\ Pending # {}
  /\ LET s == Min(Pending) IN
       /\ inflight' = s
       /\ IF Mutant = "raisebeforepublish"
            THEN /\ floor' = s + 1 /\ released' = FALSE /\ counted' = FALSE /\ UNCHANGED <<archived, dropped, gapFlag>>
            ELSE /\ Record(s) /\ UNCHANGED <<floor, released, counted>>
  /\ UNCHANGED <<durable, sealed, ckpt, present, started, archThrough, gone, relGhost, crashes, torn>>

\* The raise after the publish: the floor (re-engaged if released) moves past the archived segment. Mutant: the publish comes second.
Raise ==
  /\ inflight # 0
  /\ IF Mutant = "raisebeforepublish"
       THEN /\ Record(inflight) /\ UNCHANGED <<floor, released, counted>>
       ELSE /\ floor' = inflight + 1 /\ released' = FALSE /\ counted' = FALSE /\ UNCHANGED <<archived, dropped, gapFlag>>
  /\ archThrough' = inflight
  /\ inflight' = 0
  /\ UNCHANGED <<durable, sealed, ckpt, present, started, gone, relGhost, crashes, torn>>

\* A crash anywhere, then the next open: the floor restarts engaged at 0 and the open reclaims nothing for the archive (WA-3); mutant
\* openreclaim reclaims below CheckpointLSN as an archiving-off open does. The archiver's memory is rebuilt from the store. The crash may tear the
\* active segment after recovery applied its record (a fence chunk of the torn frame): the open seals it there (WA-9), so it is adopted, but its
\* archived copy will not hold that record.
Crash ==
  /\ crashes < MaxCrashes
  /\ crashes' = crashes + 1
  /\ sealed' = durable
  /\ inflight' = 0 /\ started' = FALSE /\ released' = FALSE /\ counted' = FALSE
  /\ floor' = 0
  /\ archThrough' = Max(archived)
  /\ \E tear \in BOOLEAN : torn' = IF tear /\ durable > sealed THEN torn \cup {durable} ELSE torn
  /\ LET doomed == IF Mutant = "openreclaim" THEN {s \in present : s < ckpt} ELSE {}
     IN /\ present' = present \ doomed
        /\ gone' = gone \cup doomed
  /\ UNCHANGED <<durable, ckpt, archived, relGhost, dropped, gapFlag>>

\* No liveness is claimed: once nothing else can happen, the model stutters.
Done == UNCHANGED vars

Next == Append \/ Seal \/ Checkpoint \/ Reclaim \/ Release \/ Start \/ Publish \/ Raise \/ Crash \/ Done

Spec == Init /\ [][Next]_vars

\* The restore planner's walk (ArchiveRestorePlanner.PlanSegments) from the genesis base (C = 0), over the archived entries in LSN order, judging each
\* by what its header and footer say: an entry that does not continue the end so far (Continues: its first LSN past end + 1), or whose footer
\* records GapBefore, refuses a target past that end (and ends the walk for one at or below it); an entry past L ends the walk. The result is the
\* entries the restore replays and the end they reach. Mutant continuityunchecked takes every entry up to L.
RECURSIVE Walk(_, _, _, _)
Walk(L, x, end, sel) ==
  IF x > MaxLsn THEN [ok |-> TRUE, sel |-> sel, end |-> end]
  ELSE IF x \notin archived THEN Walk(L, x + 1, end, sel)
  ELSE IF Mutant # "continuityunchecked" /\ (x > end + 1 \/ (end > 0 /\ x \in gapFlag)) THEN [ok |-> L <= end, sel |-> sel, end |-> end]
  ELSE IF x > L THEN [ok |-> TRUE, sel |-> sel, end |-> end]
  ELSE Walk(L, x + 1, x, sel \cup {x})

\* The planner accepts L when the walk reaches it, and no entry it replays names a dropped range at or below L (mutant droppedunchecked: never
\* checked). Otherwise it refuses (Gap, BeyondArchive).
Plan(L) == LET w == Walk(L, 1, 0, {})
           IN [ok |-> w.ok /\ L <= w.end /\ (Mutant = "droppedunchecked" \/ w.sel \cap dropped = {}), sel |-> w.sel]

\* What an entry really holds: its record, unless its segment was torn and the copy dropped it.
Holds(sel) == sel \ dropped

ArchivedOrReleased == \A s \in gone : s \in archived \/ s \in relGhost

FloorHoldsUnarchived == ~released => \A s \in present \ archived : s >= floor

FloorAtArchiveEnd == floor <= archThrough + 1

\* A restore the planner accepts replays every durable LSN up to its target: no hole the metadata hid, no record a torn copy lacks.
NoSilentHole == \A L \in Lsns : Plan(L).ok => \A x \in 1 .. L : x <= durable => x \in Holds(Plan(L).sel)
=============================================================================
