------------------------- MODULE CommitStampFamilies -------------------------
(***************************************************************************)
(* MVCC option C across stamp families (design Revision/05 §3.5, plan 06    *)
(* step 5.0).  CommitStamp.tla proves family 1 (revisions) alone; this one  *)
(* puts a spawn (family 2) and a destroy (family 3) in the same commits as  *)
(* revisions, and checks that a snapshot is one cut across them: every      *)
(* effect of a commit exists before its stamp, flagged with the owner, and  *)
(* PUBLISH only replaces the flag with the stamp.                           *)
(*                                                                         *)
(* Slots are claimed in an in-memory claim word, the page occupancy bit     *)
(* being the published set: a spawn is claimed in PREPARE and occupied at   *)
(* PUBLISH, the claim kept; a destroy's slot is hidden at PUBLISH (claim     *)
(* kept, occupancy cleared) and released by the GC.  A scan reads the       *)
(* occupancy word, then the claim word, in two steps.                       *)
(*                                                                         *)
(* Writer 1 spawns X1 and updates Y.  Writer 2 destroys X0 (born before    *)
(* everything) and updates Y.  A reader takes a snapshot and, twice, reads *)
(* Y's revision chain, looks X1 up by id, finds X1 by an occupancy scan,   *)
(* and finds X0 by an occupancy scan.                                       *)
(*                                                                         *)
(* Invariants (rules/data-engine.md):                                      *)
(*   RevisionRead   -> VIS-01/05: Y reads the highest committed stamp <= S *)
(*   SpawnRead      -> VIS-01/08: X1 is there, by lookup and by scan, iff  *)
(*                     its spawn committed at or below S                   *)
(*   DestroyRead    -> VIS-01/06/08: X0 is there iff its destroy did not   *)
(*                     commit at or below S, until no snapshot needs it    *)
(*   RepeatableRead -> VIS-01: every read of the second round equals the  *)
(*                     first                                               *)
(*   Liveness: no deadlock                                                 *)
(* Together they make a commit atomic across families: a reader sees its  *)
(* revision of Y iff it sees its structural effect.                        *)
(*                                                                         *)
(* The protocol, per writer: begin (TSN) -> reserve a card (Owner -TSN,   *)
(* Stamp 0) -> PREPARE (stage Y's flagged revision; writer 1 places X1:    *)
(* EntityMap record born = Pending(TSN), its slot claimed, the occupancy    *)
(* bit clear; writer 2 sets X0's died = Pending(TSN)) -> APPEND             *)
(* (may fail: everything staged is removed, the card released) -> register *)
(* (Owner TSN) -> draw -> store -> publish each effect (Y: stamp, unflag;  *)
(* X1: born = stamp, occupancy set, the claim kept; X0: died = stamp, its   *)
(* slot hidden) -> release the card.  The GC compacts Y by stamp and        *)
(* releases X0's claim once the oldest snapshot is past its died.           *)
(* A reader resolves a pending value through the owner's card (Owner,      *)
(* Stamp, Owner; spin on Stamp 0), and, with no card holding the owner, by *)
(* re-reading the live value.  Scans visit occupancy | claim.              *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS:                                                *)
(*   - no chain locks, no handler path, no conflict detection: those are   *)
(*     CommitStamp.tla's; here every step is atomic, memory sequentially   *)
(*     consistent                                                          *)
(*   - one card slot (NumSlots = 1 forces reuse) or two                    *)
(*   - no crash: that the pending mask is never persisted as occupancy     *)
(*     (CM-01, VIS-07) is tested on the engine (plan 06 step 5.2)          *)
(*                                                                         *)
(* GENUINENESS — each mutant (Mutant constant) MUST fail:                  *)
(*   spawnatpublish    X1 placed at PUBLISH, born = stamp (the code before  *)
(*                     plan 06 step 5.2) -> SpawnRead                      *)
(*   scanoccupancy     scans visit the occupancy bits only -> SpawnRead    *)
(*   publishclearsclaim  the publish sets occupancy and clears the claim   *)
(*                     (step 5.2's first protocol): a scan reading         *)
(*                     occupancy before and the claim after misses the     *)
(*                     spawn -> SpawnRead                                  *)
(*   destroyatpublish  died written at PUBLISH, never pending -> DestroyRead *)
(*   releaseatpublish  X0's slot released at PUBLISH -> DestroyRead        *)
(***************************************************************************)
EXTENDS Integers, Sequences, FiniteSets

CONSTANTS NumSlots, Mutant

W == {1, 2}
S == 1..NumSlots
Items == {"Y", "X1get", "X1scan", "X0scan"}
ItemOrder == <<"Y", "X1get", "X1scan", "X0scan">>

\* A born/died value: none (no stamp), pending (owner TSN), or published (stamp).
NoV == [k |-> "none", v |-> 0]
Pend(t) == [k |-> "pend", v |-> t]
Pub(s) == [k |-> "pub", v |-> s]

VARIABLES
  next, owner, cstamp,
  chain,                \* Y's revisions: Seq of [tsn, stamp, flag, w]
  x1rec,                \* X1's EntityMap record: FALSE (absent) or its born value
  x1born, x1occ, x1cl,  \* X1's born, occupancy bit, claim bit
  x0died, x0occ, x0cl,  \* X0's died, occupancy bit, claim bit (a loaded slot: occupied, not claimed)
  wpc, wtsn, wslot, wstamp, wleft, aborted,
  rpc, rsnap, ritem, rround, rcopy, ridx, rbest, rslot, rst, rtsn, rres, rocc

vars == <<next, owner, cstamp, chain, x1rec, x1born, x1occ, x1cl, x0died, x0occ, x0cl,
          wpc, wtsn, wslot, wstamp, wleft, aborted,
          rpc, rsnap, ritem, rround, rcopy, ridx, rbest, rslot, rst, rtsn, rres, rocc>>
dvars == <<chain, x1rec, x1born, x1occ, x1cl, x0died, x0occ, x0cl>>
wvars == <<wpc, wtsn, wslot, wstamp, wleft, aborted>>
rvars == <<rpc, rsnap, ritem, rround, rcopy, ridx, rbest, rslot, rst, rtsn, rres, rocc>>

Base == [tsn |-> 0, stamp |-> 0, flag |-> FALSE, w |-> 0]
Unset == -1
B2I(b) == IF b THEN 1 ELSE 0   \* results are integers: a writer id for Y, 0/1 for the others

Init ==
  /\ next = 1
  /\ owner = [s \in S |-> 0]
  /\ cstamp = [s \in S |-> 0]
  /\ chain = <<Base>>
  /\ x1rec = FALSE /\ x1born = NoV /\ x1occ = FALSE /\ x1cl = FALSE
  /\ x0died = NoV /\ x0occ = TRUE /\ x0cl = FALSE
  /\ wpc = [w \in W |-> "begin"]
  /\ wtsn = [w \in W |-> 0]
  /\ wslot = [w \in W |-> 0]
  /\ wstamp = [w \in W |-> 0]
  /\ wleft = [w \in W |-> {}]
  /\ aborted = [w \in W |-> FALSE]
  /\ rpc = "begin" /\ rsnap = 0 /\ ritem = 1 /\ rround = 1 /\ rcopy = <<>> /\ ridx = 1 /\ rbest = [s |-> -1, w |-> -1]
  /\ rslot = 1 /\ rst = 0 /\ rtsn = 0 /\ rocc = FALSE
  /\ rres = [rd \in 1..2 |-> [i \in Items |-> Unset]]

-----------------------------------------------------------------------------
(* Writers *)

Effects(w) == IF w = 1 THEN {"Y", "S"} ELSE {"Y", "D"}

WBegin(w) ==
  /\ wpc[w] = "begin"
  /\ wtsn' = [wtsn EXCEPT ![w] = next]
  /\ next' = next + 1
  /\ wpc' = [wpc EXCEPT ![w] = "reserve"]
  /\ UNCHANGED <<owner, cstamp, wslot, wstamp, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

WReserve(w) ==
  /\ wpc[w] = "reserve"
  /\ \E s \in S : owner[s] = 0 /\ owner' = [owner EXCEPT ![s] = -wtsn[w]] /\ wslot' = [wslot EXCEPT ![w] = s]
  /\ wpc' = [wpc EXCEPT ![w] = "reset"]
  /\ UNCHANGED <<next, cstamp, wtsn, wstamp, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

WReset(w) ==
  /\ wpc[w] = "reset"
  /\ cstamp' = [cstamp EXCEPT ![wslot[w]] = 0]
  /\ wpc' = [wpc EXCEPT ![w] = "stage"]
  /\ wleft' = [wleft EXCEPT ![w] = Effects(w)]
  /\ UNCHANGED <<next, owner, wtsn, wslot, wstamp, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

StageDone(w) == wpc' = [wpc EXCEPT ![w] = IF wleft'[w] = {} THEN "append" ELSE "stage"]

\* PREPARE, one effect at a time.
WStageY(w) ==
  /\ wpc[w] = "stage" /\ "Y" \in wleft[w]
  /\ chain' = Append(chain, [tsn |-> wtsn[w], stamp |-> 0, flag |-> TRUE, w |-> w])
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"Y"}] /\ StageDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<x1rec, x1born, x1occ, x1cl, x0died, x0occ, x0cl>> /\ UNCHANGED rvars

\* The spawn's placement: the record with a pending born, the slot in the pending mask, occupancy clear.  The mutant places nothing here.
WStageSpawn(w) ==
  /\ wpc[w] = "stage" /\ "S" \in wleft[w]
  /\ IF Mutant = "spawnatpublish"
     THEN UNCHANGED <<x1rec, x1born, x1cl>>
     ELSE /\ x1rec' = TRUE /\ x1born' = Pend(wtsn[w]) /\ x1cl' = TRUE
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"S"}] /\ StageDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<chain, x1occ, x0died, x0occ, x0cl>> /\ UNCHANGED rvars

WStageDestroy(w) ==
  /\ wpc[w] = "stage" /\ "D" \in wleft[w]
  /\ x0died' = IF Mutant = "destroyatpublish" THEN x0died ELSE Pend(wtsn[w])
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"D"}] /\ StageDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<chain, x1rec, x1born, x1occ, x1cl, x0occ, x0cl>> /\ UNCHANGED rvars

WAppendOk(w) ==
  /\ wpc[w] = "append"
  /\ wpc' = [wpc EXCEPT ![w] = "register"]
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

\* APPEND fails: everything PREPARE staged is removed (AP-07), the card released.
WAppendFail(w) ==
  /\ wpc[w] = "append"
  /\ chain' = SelectSeq(chain, LAMBDA x : ~(x.flag /\ x.tsn = wtsn[w]))
  /\ IF w = 1 THEN x1rec' = FALSE /\ x1born' = NoV /\ x1cl' = FALSE ELSE UNCHANGED <<x1rec, x1born, x1cl>>
  /\ x0died' = IF w = 2 THEN NoV ELSE x0died
  /\ owner' = [owner EXCEPT ![wslot[w]] = 0]
  /\ aborted' = [aborted EXCEPT ![w] = TRUE]
  /\ wpc' = [wpc EXCEPT ![w] = "done"]
  /\ UNCHANGED <<next, cstamp, wtsn, wslot, wstamp, wleft>> /\ UNCHANGED <<x1occ, x0occ, x0cl>> /\ UNCHANGED rvars

WRegister(w) ==
  /\ wpc[w] = "register"
  /\ owner' = [owner EXCEPT ![wslot[w]] = wtsn[w]]
  /\ wpc' = [wpc EXCEPT ![w] = "draw"]
  /\ UNCHANGED <<next, cstamp, wtsn, wslot, wstamp, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

WDraw(w) ==
  /\ wpc[w] = "draw"
  /\ wstamp' = [wstamp EXCEPT ![w] = next]
  /\ next' = next + 1
  /\ wpc' = [wpc EXCEPT ![w] = "store"]
  /\ UNCHANGED <<owner, cstamp, wtsn, wslot, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

WStore(w) ==
  /\ wpc[w] = "store"
  /\ cstamp' = [cstamp EXCEPT ![wslot[w]] = wstamp[w]]
  /\ wpc' = [wpc EXCEPT ![w] = "publish"]
  /\ wleft' = [wleft EXCEPT ![w] = Effects(w)]
  /\ UNCHANGED <<next, owner, wtsn, wslot, wstamp, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

PubDone(w) == wpc' = [wpc EXCEPT ![w] = IF wleft'[w] = {} THEN "release" ELSE "publish"]

WPublishY(w) ==
  /\ wpc[w] = "publish" /\ "Y" \in wleft[w]
  /\ chain' = [i \in 1..Len(chain) |-> IF chain[i].flag /\ chain[i].tsn = wtsn[w]
                                        THEN [chain[i] EXCEPT !.stamp = wstamp[w], !.flag = FALSE] ELSE chain[i]]
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"Y"}] /\ PubDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<x1rec, x1born, x1occ, x1cl, x0died, x0occ, x0cl>> /\ UNCHANGED rvars

\* born = stamp, the occupancy bit set, the claim kept.  The spawnatpublish mutant inserts the record here.
WPublishSpawn(w) ==
  /\ wpc[w] = "publish" /\ "S" \in wleft[w]
  /\ x1rec' = TRUE /\ x1born' = Pub(wstamp[w]) /\ x1occ' = TRUE /\ x1cl' = (Mutant # "publishclearsclaim")
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"S"}] /\ PubDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<chain, x0died, x0occ, x0cl>> /\ UNCHANGED rvars

\* died = stamp; the slot hidden — claimed, not occupied — until the GC releases it (the mutant releases it here).
WPublishDestroy(w) ==
  /\ wpc[w] = "publish" /\ "D" \in wleft[w]
  /\ x0died' = Pub(wstamp[w])
  /\ x0occ' = FALSE
  /\ x0cl' = (Mutant # "releaseatpublish")
  /\ wleft' = [wleft EXCEPT ![w] = @ \ {"D"}] /\ PubDone(w)
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted>>
  /\ UNCHANGED <<chain, x1rec, x1born, x1occ, x1cl>> /\ UNCHANGED rvars

WRelease(w) ==
  /\ wpc[w] = "release"
  /\ owner' = [owner EXCEPT ![wslot[w]] = 0]
  /\ wpc' = [wpc EXCEPT ![w] = "done"]
  /\ UNCHANGED <<next, cstamp, wtsn, wslot, wstamp, wleft, aborted>> /\ UNCHANGED dvars /\ UNCHANGED rvars

Writer(w) ==
  \/ WBegin(w) \/ WReserve(w) \/ WReset(w) \/ WStageY(w) \/ WStageSpawn(w) \/ WStageDestroy(w)
  \/ WAppendOk(w) \/ WAppendFail(w) \/ WRegister(w) \/ WDraw(w) \/ WStore(w)
  \/ WPublishY(w) \/ WPublishSpawn(w) \/ WPublishDestroy(w) \/ WRelease(w)

-----------------------------------------------------------------------------
(* The reader *)

Item == ItemOrder[ritem]
RUnchangedBut == UNCHANGED <<next, owner, cstamp>> /\ UNCHANGED dvars /\ UNCHANGED wvars

RBegin ==
  /\ rpc = "begin"
  /\ rsnap' = next
  /\ next' = next + 1
  /\ rpc' = "item"
  /\ UNCHANGED <<owner, cstamp>> /\ UNCHANGED dvars /\ UNCHANGED wvars
  /\ UNCHANGED <<ritem, rround, rcopy, ridx, rbest, rslot, rst, rtsn, rres, rocc>>

\* Record the item's result and move to the next item (the second round reads them all again).
Done(v) ==
  /\ rres' = [rres EXCEPT ![rround][Item] = v]
  /\ IF ritem = Len(ItemOrder)
     THEN /\ ritem' = 1 /\ rround' = rround + 1 /\ rpc' = IF rround = 2 THEN "done" ELSE "item"
     ELSE /\ ritem' = ritem + 1 /\ UNCHANGED rround /\ rpc' = "item"

\* Visible: a value's state for a snapshot — TRUE/FALSE for a decided one, or the owner TSN to resolve.
Resolve(t) == /\ rtsn' = t /\ rslot' = 1 /\ rpc' = "o1"

\* Start an item.  Y: copy the chain (a seqlock-validated copy).  The others: read the value, or start resolving its owner.
RItem ==
  /\ rpc = "item"
  /\ CASE Item = "Y" ->
            /\ rcopy' = chain /\ ridx' = 1 /\ rbest' = [s |-> -1, w |-> -1] /\ rpc' = "walk"
            /\ UNCHANGED <<ritem, rround, rres, rslot, rtsn>>
       [] Item = "X1get" ->
            /\ IF ~x1rec THEN Done(B2I(FALSE)) /\ UNCHANGED <<rslot, rtsn>>
               ELSE IF x1born.k = "pend" THEN Resolve(x1born.v) /\ UNCHANGED <<ritem, rround, rres>>
               ELSE Done(B2I(x1born.v <= rsnap)) /\ UNCHANGED <<rslot, rtsn>>
            /\ UNCHANGED <<rcopy, ridx, rbest>>
       [] Item \in {"X1scan", "X0scan"} ->
            /\ rocc' = IF Item = "X1scan" THEN x1occ ELSE x0occ
            /\ rpc' = "claim"
            /\ UNCHANGED <<ritem, rround, rres, rslot, rtsn, rcopy, ridx, rbest>>
  /\ (Item \notin {"X1scan", "X0scan"} => UNCHANGED rocc)
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, rst>>

\* A scan's second read: the claim word.  A slot occupied at the first read, or claimed at this one, is visited and decided by its record.
RClaim ==
  /\ rpc = "claim"
  /\ CASE Item = "X1scan" ->
            /\ LET visited == IF Mutant = "scanoccupancy" THEN rocc ELSE rocc \/ x1cl
               IN IF ~visited THEN Done(B2I(FALSE)) /\ UNCHANGED <<rslot, rtsn>>
                  ELSE IF x1born.k = "pend" THEN Resolve(x1born.v) /\ UNCHANGED <<ritem, rround, rres>>
                  ELSE Done(B2I(x1born.k = "pub" /\ x1born.v <= rsnap)) /\ UNCHANGED <<rslot, rtsn>>
       [] Item = "X0scan" ->
            /\ LET visited == IF Mutant = "scanoccupancy" THEN rocc ELSE rocc \/ x0cl
               IN IF ~visited THEN Done(B2I(FALSE)) /\ UNCHANGED <<rslot, rtsn>>
                  ELSE IF x0died.k = "pend" THEN Resolve(x0died.v) /\ UNCHANGED <<ritem, rround, rres>>
                  ELSE Done(B2I(x0died.k = "none" \/ x0died.v > rsnap)) /\ UNCHANGED <<rslot, rtsn>>
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, rst, rcopy, ridx, rbest, rocc>>

\* Y's walk over the copy: a published revision is taken by stamp; a flagged one is resolved through its owner.
RWalk ==
  /\ rpc = "walk"
  /\ IF ridx > Len(rcopy)
     THEN Done(rbest.w) /\ UNCHANGED <<ridx, rbest, rslot, rtsn>>
     ELSE IF rcopy[ridx].flag
          THEN Resolve(rcopy[ridx].tsn) /\ UNCHANGED <<ridx, rbest, ritem, rround, rres>>
          ELSE /\ rbest' = IF rcopy[ridx].stamp <= rsnap /\ rcopy[ridx].stamp > rbest.s
                           THEN [s |-> rcopy[ridx].stamp, w |-> rcopy[ridx].w] ELSE rbest
               /\ ridx' = ridx + 1
               /\ UNCHANGED <<rpc, ritem, rround, rres, rslot, rtsn>>
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, rcopy, rst, rocc>>

\* The live value of what is being resolved, when no card holds its owner: a stamp, or -1 (not committed, or gone).
LiveStamp ==
  CASE Item = "Y" -> LET ix == {i \in 1..Len(chain) : chain[i].tsn = rtsn /\ chain[i].w = rcopy[ridx].w}
                     IN IF ix = {} THEN -1 ELSE LET i == CHOOSE i \in ix : TRUE IN IF chain[i].flag THEN -1 ELSE chain[i].stamp
    [] Item \in {"X1get", "X1scan"} -> IF x1rec /\ x1born.k = "pub" THEN x1born.v ELSE -1
    [] Item = "X0scan" -> IF x0died.k = "pub" THEN x0died.v ELSE -1

\* The owner resolved to stamp st (-1: not committed).  Finish the item, or go on with Y's walk.
Resolved(st) ==
  CASE Item = "Y" ->
         /\ rbest' = IF st >= 0 /\ st <= rsnap /\ st > rbest.s THEN [s |-> st, w |-> rcopy[ridx].w] ELSE rbest
         /\ ridx' = ridx + 1 /\ rpc' = "walk"
         /\ UNCHANGED <<ritem, rround, rres>>
    [] Item \in {"X1get", "X1scan"} -> Done(B2I(st >= 0 /\ st <= rsnap)) /\ UNCHANGED <<ridx, rbest>>
    [] Item = "X0scan" -> Done(B2I(~(st >= 0 /\ st <= rsnap))) /\ UNCHANGED <<ridx, rbest>>

RO1 ==
  /\ rpc = "o1"
  /\ IF rslot > NumSlots
     THEN Resolved(LiveStamp) /\ UNCHANGED <<rslot, rst>>
     ELSE IF owner[rslot] = rtsn
          THEN rpc' = "st" /\ UNCHANGED <<rslot, rst, ridx, rbest, ritem, rround, rres>>
          ELSE rslot' = rslot + 1 /\ UNCHANGED <<rpc, rst, ridx, rbest, ritem, rround, rres>>
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, rcopy, rtsn, rocc>>

RSt ==
  /\ rpc = "st"
  /\ rst' = cstamp[rslot]
  /\ rpc' = "o2"
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, ritem, rround, rcopy, ridx, rbest, rslot, rtsn, rres, rocc>>

RO2 ==
  /\ rpc = "o2"
  /\ IF owner[rslot] = rtsn
     THEN IF rst = 0 THEN rpc' = "o1" /\ UNCHANGED <<rslot, ridx, rbest, ritem, rround, rres>>
          ELSE Resolved(rst) /\ UNCHANGED rslot
     ELSE rpc' = "o1" /\ rslot' = rslot + 1 /\ UNCHANGED <<ridx, rbest, ritem, rround, rres>>
  /\ RUnchangedBut /\ UNCHANGED <<rsnap, rcopy, rst, rtsn, rocc>>

Reader == RBegin \/ RItem \/ RClaim \/ RWalk \/ RO1 \/ RSt \/ RO2

-----------------------------------------------------------------------------
(* The GC: cutoff = the reader's snapshot while it reads *)

Cutoff == IF rpc \in {"begin", "done"} THEN next ELSE rsnap

GCY ==
  LET old == {i \in 1..Len(chain) : ~chain[i].flag /\ chain[i].stamp <= Cutoff}
      keep == IF old = {} THEN {} ELSE {CHOOSE i \in old : \A j \in old : chain[j].stamp <= chain[i].stamp}
      dropped == {chain[i] : i \in old \ keep}
  IN /\ dropped # {}
     /\ chain' = SelectSeq(chain, LAMBDA x : x \notin dropped)
     /\ UNCHANGED <<next, owner, cstamp, x1rec, x1born, x1occ, x1cl, x0died, x0occ, x0cl>> /\ UNCHANGED wvars /\ UNCHANGED rvars

\* A destroyed entity's slot is released once no snapshot can see it alive.
GCX0 ==
  /\ x0cl /\ x0died.k = "pub" /\ x0died.v < Cutoff
  /\ x0cl' = FALSE
  /\ UNCHANGED <<next, owner, cstamp, chain, x1rec, x1born, x1occ, x1cl, x0died, x0occ>> /\ UNCHANGED wvars /\ UNCHANGED rvars

-----------------------------------------------------------------------------

Terminated == (\A w \in W : wpc[w] = "done") /\ rpc = "done" /\ UNCHANGED vars

Next == (\E w \in W : Writer(w)) \/ Reader \/ GCY \/ GCX0 \/ Terminated

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants *)

Committed(w) == wstamp[w] # 0 /\ ~aborted[w]
VisibleAt(w, snap) == Committed(w) /\ wstamp[w] <= snap

ExpectY == LET ws == {w \in W : VisibleAt(w, rsnap)}
           IN IF ws = {} THEN 0 ELSE CHOOSE w \in ws : \A v \in ws : wstamp[v] <= wstamp[w]

Res(rd, i) == rres[rd][i]
Read(rd, i) == Res(rd, i) # Unset

RevisionRead == \A rd \in 1..2 : Read(rd, "Y") => Res(rd, "Y") = ExpectY
SpawnRead == \A rd \in 1..2, i \in {"X1get", "X1scan"} : Read(rd, i) => Res(rd, i) = B2I(VisibleAt(1, rsnap))
DestroyRead == \A rd \in 1..2 : Read(rd, "X0scan") => Res(rd, "X0scan") = B2I(~VisibleAt(2, rsnap))
RepeatableRead == \A i \in Items : Read(2, i) => Res(2, i) = Res(1, i)

TypeOK ==
  /\ next \in Nat
  /\ rpc \in {"begin", "item", "claim", "walk", "o1", "st", "o2", "done"}
=============================================================================
