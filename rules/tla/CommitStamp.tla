----------------------------- MODULE CommitStamp -----------------------------
(***************************************************************************)
(* MVCC option C, family 1 (design Revision/05 §3, plan 06 step 3.0).      *)
(* A transaction's TSN is its identity and its snapshot; a separate        *)
(* commit stamp, drawn from the same counter once the commit can no longer *)
(* fail, says when its revisions became visible.  While it stamps them the *)
(* committer is registered on a commit card, and a reader that meets one   *)
(* of its still-flagged revisions decides by the card's stamp, so every    *)
(* revision of a commit resolves the same way.                             *)
(*                                                                         *)
(* Invariants (rules/data-engine.md):                                      *)
(*   SnapshotRead     -> VIS-01/05: a read returns the highest-stamp       *)
(*                       committed write with stamp <= its snapshot        *)
(*   RepeatableRead   -> VIS-01: a reader's value of an entity never       *)
(*                       changes                                           *)
(*   AtomicVisibility -> VIS-01/04: a reader sees all or none of a commit  *)
(*   Causality        -> VIS-03: a reader begun after a commit returned    *)
(*                       sees it                                           *)
(*   NoDirtyRead      -> VIS-03: a value read is never an aborted write's  *)
(*   GCSafety         -> VIS-06: a revision a live snapshot needs is never *)
(*                       reclaimed                                         *)
(*   Liveness: no deadlock (TLC's deadlock check) — the reservation is     *)
(*   taken before any chain lock (VIS-03)                                  *)
(*                                                                         *)
(* The protocol, per writer (design 05 §3.3):                              *)
(*   0 reserve a card: Owner 0 -> -TSN, then Stamp = 0 (two steps)         *)
(*   PREPARE: stage a flagged revision per entity, owner = TSN, under the  *)
(*     entity's chain lock; a handler-path writer keeps the lock until it  *)
(*     publishes that entity (AP-05)                                       *)
(*   APPEND: may fail -> the staged revisions and the card are released    *)
(*   1 Owner = TSN   2 stamp = next++   3 Stamp = stamp                    *)
(*   5 publish each revision: stamp it, clear the flag                     *)
(*   6 Owner = 0     7 Commit() returns                                    *)
(* Readers: snapshot = next++ (their own RMW on the counter), then read    *)
(* each entity twice.  A read copies the chain (a seqlock-validated copy), *)
(* takes the highest visible stamp; a flagged revision is resolved through *)
(* its owner's card — Owner, Stamp, Owner, spinning on Stamp = 0 — and,    *)
(* when no card holds its owner, by re-reading the live revision.          *)
(* The GC: cutoff = the oldest live snapshot (MinTSN); per chain, keeps    *)
(* the flagged revisions, the committed ones above the cutoff, and the     *)
(* highest-stamp one at or below it (the sentinel).                        *)
(*                                                                         *)
(* DELIBERATE ABSTRACTIONS:                                                *)
(*   - every writer writes every entity; values are the writer's id        *)
(*   - conflict detection is not modeled (write skew is allowed, ADR-003); *)
(*     the newest version is the highest stamp (VIS-05)                    *)
(*   - the card table has NumSlots slots, all in one window: Size = 1      *)
(*     forces every committer onto one card, so reuse (ABA) is reachable   *)
(*   - the WAL is the APPEND step alone; records, durability and recovery  *)
(*     are covered by CommitRecovery.tla                                   *)
(*   - each step is atomic and memory is sequentially consistent; the      *)
(*     arm64 argument (Volatile card loads, the reader's RMW) is in VIS-01 *)
(*                                                                         *)
(* GENUINENESS — each mutant (Mutant constant) MUST fail:                  *)
(*   registerafterdraw   register after the draw -> SnapshotRead           *)
(*   releaseearly        release after the first publish -> AtomicVis.     *)
(*   drawbeforeappend    stamp drawn before APPEND -> NoDirtyRead          *)
(*   gcbytsn             GC keyed by TSN -> GCSafety                       *)
(*   chainorder          walker takes the last visible in chain order      *)
(*                       -> SnapshotRead                                   *)
(*   reserveafterprepare reservation after PREPARE -> deadlock             *)
(*   nostampreset        reservation leaves Stamp -> SnapshotRead          *)
(*   singleownerread     no second Owner read -> SnapshotRead              *)
(***************************************************************************)
EXTENDS Integers, Sequences, FiniteSets

CONSTANTS NumW, NumR, NumE, NumSlots, Handlers, Mutant, SecondRound

W == 1..NumW
R == 1..NumR
E == 1..NumE
S == 1..NumSlots
None == 0

VARIABLES
  next,                \* the one counter: identities, snapshots and stamps
  owner, cstamp,       \* the commit cards
  chain,               \* chain[e]: Seq of [tsn, stamp, flag, w]
  lockX,               \* lockX[e]: the writer holding e's chain lock, or 0
  wpc, wtsn, wslot, wstamp, wleft, aborted, returned,
  rpc, rsnap, rent, rround, rcopy, ridx, rbest, rbestw, rslot, rst, rres, rfirst, rsaw

vars == <<next, owner, cstamp, chain, lockX, wpc, wtsn, wslot, wstamp, wleft, aborted, returned,
          rpc, rsnap, rent, rround, rcopy, ridx, rbest, rbestw, rslot, rst, rres, rfirst, rsaw>>
rvars == <<rpc, rsnap, rent, rround, rcopy, ridx, rbest, rbestw, rslot, rst, rres, rfirst, rsaw>>
wvars == <<wpc, wtsn, wslot, wstamp, wleft, aborted, returned>>

Base == [tsn |-> 0, stamp |-> 0, flag |-> FALSE, w |-> 0]
Unset == [s |-> -1, w |-> -1]

Init ==
  /\ next = 1
  /\ owner = [s \in S |-> 0]
  /\ cstamp = [s \in S |-> 0]
  /\ chain = [e \in E |-> <<Base>>]
  /\ lockX = [e \in E |-> 0]
  /\ wpc = [w \in W |-> "begin"]
  /\ wtsn = [w \in W |-> 0]
  /\ wslot = [w \in W |-> 0]
  /\ wstamp = [w \in W |-> 0]
  /\ wleft = [w \in W |-> E]
  /\ aborted = [w \in W |-> FALSE]
  /\ returned = [w \in W |-> FALSE]
  /\ rpc = [r \in R |-> "begin"]
  /\ rsnap = [r \in R |-> 0]
  /\ rent = [r \in R |-> 1]
  /\ rround = [r \in R |-> 1]
  /\ rcopy = [r \in R |-> <<>>]
  /\ ridx = [r \in R |-> 1]
  /\ rbest = [r \in R |-> -1]
  /\ rbestw = [r \in R |-> -1]
  /\ rslot = [r \in R |-> 1]
  /\ rst = [r \in R |-> 0]
  /\ rres = [r \in R |-> [e \in E |-> Unset]]
  /\ rfirst = [r \in R |-> [e \in E |-> Unset]]
  /\ rsaw = [r \in R |-> {}]

-----------------------------------------------------------------------------
(* Writers *)

LockFree(w, e) == lockX[e] \in {0, w}
Handler(w) == w \in Handlers
ReserveFirst == Mutant # "reserveafterprepare"

WBegin(w) ==
  /\ wpc[w] = "begin"
  /\ wtsn' = [wtsn EXCEPT ![w] = next]
  /\ next' = next + 1
  /\ wpc' = [wpc EXCEPT ![w] = IF ReserveFirst THEN "reserve" ELSE "stage"]
  /\ UNCHANGED <<owner, cstamp, chain, lockX, wslot, wstamp, wleft, aborted, returned>> /\ UNCHANGED rvars

\* 0: CAS a free card's Owner 0 -> -TSN; waits while every card of the window is taken.
WReserve(w) ==
  /\ wpc[w] = "reserve"
  /\ \E s \in S :
       /\ owner[s] = 0
       /\ owner' = [owner EXCEPT ![s] = -wtsn[w]]
       /\ wslot' = [wslot EXCEPT ![w] = s]
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "nostampreset" THEN (IF ReserveFirst THEN "stage" ELSE "append")
                                ELSE "reset"]
  /\ UNCHANGED <<next, cstamp, chain, lockX, wtsn, wstamp, wleft, aborted, returned>> /\ UNCHANGED rvars

WReset(w) ==
  /\ wpc[w] = "reset"
  /\ cstamp' = [cstamp EXCEPT ![wslot[w]] = 0]
  /\ wpc' = [wpc EXCEPT ![w] = IF ReserveFirst THEN "stage" ELSE "append"]
  /\ UNCHANGED <<next, owner, chain, lockX, wtsn, wslot, wstamp, wleft, aborted, returned>> /\ UNCHANGED rvars

\* PREPARE: stage a flagged revision of e under its chain lock; a handler-path writer keeps the lock.
WStage(w) ==
  /\ wpc[w] = "stage"
  /\ \E e \in wleft[w] :
       /\ LockFree(w, e)
       /\ chain' = [chain EXCEPT ![e] = Append(@, [tsn |-> wtsn[w], stamp |-> 0, flag |-> TRUE, w |-> w])]
       /\ lockX' = [lockX EXCEPT ![e] = IF Handler(w) THEN w ELSE 0]
       /\ wleft' = [wleft EXCEPT ![w] = @ \ {e}]
       /\ wpc' = [wpc EXCEPT ![w] = IF wleft'[w] # {} THEN "stage"
                                    ELSE IF ReserveFirst THEN (IF Mutant = "drawbeforeappend" THEN "register" ELSE "append")
                                    ELSE "reserve"]
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted, returned>> /\ UNCHANGED rvars

RemoveOwn(w, c) == SelectSeq(c, LAMBDA x : ~(x.flag /\ x.tsn = wtsn[w]))

\* APPEND succeeds: on to the commit point (the mutant drew the stamp before it, and goes on publishing).
WAppendOk(w) ==
  /\ wpc[w] = "append"
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "drawbeforeappend" THEN "publish"
                                ELSE IF Mutant = "registerafterdraw" THEN "drawfirst" ELSE "register"]
  /\ wleft' = [wleft EXCEPT ![w] = IF Mutant = "drawbeforeappend" THEN E ELSE @]
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX, wtsn, wslot, wstamp, aborted, returned>> /\ UNCHANGED rvars

\* APPEND fails: the staged revisions and the card are released, and the chain locks.
WAppendFail(w) ==
  /\ wpc[w] = "append"
  /\ chain' = [e \in E |-> RemoveOwn(w, chain[e])]
  /\ lockX' = [e \in E |-> IF lockX[e] = w THEN 0 ELSE lockX[e]]
  /\ owner' = [owner EXCEPT ![wslot[w]] = 0]
  /\ aborted' = [aborted EXCEPT ![w] = TRUE]
  /\ wpc' = [wpc EXCEPT ![w] = "done"]
  /\ UNCHANGED <<next, cstamp, wtsn, wslot, wstamp, wleft, returned>> /\ UNCHANGED rvars

\* 1: registered — Owner = TSN.  The mutant registers after the draw.
WRegister(w) ==
  /\ wpc[w] = "register"
  /\ owner' = [owner EXCEPT ![wslot[w]] = wtsn[w]]
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "registerafterdraw" THEN "store" ELSE "draw"]
  /\ UNCHANGED <<next, cstamp, chain, lockX, wtsn, wslot, wstamp, wleft, aborted, returned>> /\ UNCHANGED rvars

\* 2: the stamp, from the one counter.
WDraw(w) ==
  /\ wpc[w] = (IF Mutant = "registerafterdraw" THEN "drawfirst" ELSE "draw")
  /\ wstamp' = [wstamp EXCEPT ![w] = next]
  /\ next' = next + 1
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "registerafterdraw" THEN "register" ELSE "store"]
  /\ UNCHANGED <<owner, cstamp, chain, lockX, wtsn, wslot, wleft, aborted, returned>> /\ UNCHANGED rvars

\* 3: Stamp = stamp on the card.
WStore(w) ==
  /\ wpc[w] = "store"
  /\ cstamp' = [cstamp EXCEPT ![wslot[w]] = wstamp[w]]
  /\ wpc' = [wpc EXCEPT ![w] = IF Mutant = "drawbeforeappend" THEN "append" ELSE "publish"]
  /\ wleft' = [wleft EXCEPT ![w] = E]
  /\ UNCHANGED <<next, owner, chain, lockX, wtsn, wslot, wstamp, aborted, returned>> /\ UNCHANGED rvars

Publish(w, c) == [i \in 1..Len(c) |-> IF c[i].flag /\ c[i].tsn = wtsn[w]
                                       THEN [c[i] EXCEPT !.stamp = wstamp[w], !.flag = FALSE] ELSE c[i]]

\* 5: publish one entity's revision — under its chain lock.
WPublish(w) ==
  /\ wpc[w] = "publish"
  /\ \E e \in wleft[w] :
       /\ LockFree(w, e)
       /\ chain' = [chain EXCEPT ![e] = Publish(w, @)]
       /\ lockX' = [lockX EXCEPT ![e] = 0]
       /\ wleft' = [wleft EXCEPT ![w] = @ \ {e}]
       /\ wpc' = [wpc EXCEPT ![w] = IF wleft'[w] = {} THEN "release"
                                    ELSE IF Mutant = "releaseearly" THEN "releaseearly" ELSE "publish"]
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted, returned>> /\ UNCHANGED rvars

\* The mutant releases its card after its first publish.
WReleaseEarly(w) ==
  /\ wpc[w] = "releaseearly"
  /\ owner' = [owner EXCEPT ![wslot[w]] = 0]
  /\ wpc' = [wpc EXCEPT ![w] = "publishrest"]
  /\ UNCHANGED <<next, cstamp, chain, lockX, wtsn, wslot, wstamp, wleft, aborted, returned>> /\ UNCHANGED rvars

WPublishRest(w) ==
  /\ wpc[w] = "publishrest"
  /\ \E e \in wleft[w] :
       /\ LockFree(w, e)
       /\ chain' = [chain EXCEPT ![e] = Publish(w, @)]
       /\ lockX' = [lockX EXCEPT ![e] = 0]
       /\ wleft' = [wleft EXCEPT ![w] = @ \ {e}]
       /\ wpc' = [wpc EXCEPT ![w] = IF wleft'[w] = {} THEN "returned" ELSE "publishrest"]
  /\ UNCHANGED <<next, owner, cstamp, wtsn, wslot, wstamp, aborted, returned>> /\ UNCHANGED rvars

\* 6: Owner = 0, after the last publish.
WRelease(w) ==
  /\ wpc[w] = "release"
  /\ owner' = [owner EXCEPT ![wslot[w]] = 0]
  /\ returned' = [returned EXCEPT ![w] = TRUE]
  /\ wpc' = [wpc EXCEPT ![w] = "done"]
  /\ UNCHANGED <<next, cstamp, chain, lockX, wtsn, wslot, wstamp, wleft, aborted>> /\ UNCHANGED rvars

\* 7: Commit() returns.
WReturn(w) ==
  /\ wpc[w] = "returned"
  /\ returned' = [returned EXCEPT ![w] = TRUE]
  /\ wpc' = [wpc EXCEPT ![w] = "done"]
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX, wtsn, wslot, wstamp, wleft, aborted>> /\ UNCHANGED rvars

\* The mutant drawbeforeappend path: register, draw, store, then APPEND (which may fail), then publish.
\* registerafterdraw path: begin -> reserve -> reset -> stage -> "drawfirst" -> register -> store -> publish.
Writer(w) ==
  \/ WBegin(w) \/ WReserve(w) \/ WReset(w) \/ WStage(w) \/ WAppendOk(w) \/ WAppendFail(w)
  \/ WRegister(w) \/ WDraw(w) \/ WStore(w) \/ WPublish(w) \/ WReleaseEarly(w) \/ WPublishRest(w)
  \/ WRelease(w) \/ WReturn(w)

-----------------------------------------------------------------------------
(* Readers *)

RBegin(r) ==
  /\ rpc[r] = "begin"
  /\ rsnap' = [rsnap EXCEPT ![r] = next]
  /\ next' = next + 1
  /\ rsaw' = [rsaw EXCEPT ![r] = {w \in W : returned[w]}]
  /\ rpc' = [rpc EXCEPT ![r] = "copy"]
  /\ UNCHANGED <<owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rent, rround, rcopy, ridx, rbest, rbestw, rslot, rst, rres, rfirst>>

\* Take a candidate: the highest stamp (VIS-05), or — the mutant — the last visible in chain order.
Take(r, st, w) ==
  /\ rbest' = [rbest EXCEPT ![r] = IF Mutant = "chainorder" \/ st > rbest[r] THEN st ELSE @]
  /\ rbestw' = [rbestw EXCEPT ![r] = IF Mutant = "chainorder" \/ st > rbest[r] THEN w ELSE @]

Cur(r) == rcopy[r][ridx[r]]

\* The second round re-reads the entities 1..SecondRound (repeatable read); the first reads them all.
NextEntity(r) ==
  LET last == rent[r] = (IF rround[r] = 2 THEN SecondRound ELSE NumE)
  IN /\ rent' = [rent EXCEPT ![r] = IF last THEN 1 ELSE @ + 1]
     /\ rround' = [rround EXCEPT ![r] = IF last THEN @ + 1 ELSE @]
     /\ rpc' = [rpc EXCEPT ![r] = IF last /\ rround[r] = 2 THEN "done" ELSE "copy"]

\* The published revisions of the copy, from position lo up to the next flagged one (exclusive), taken at once: a published
\* revision needs no card (VIS-01's first branch).
NextFlag(c, lo) == LET fs == {i \in lo..Len(c) : c[i].flag} IN IF fs = {} THEN Len(c) + 1 ELSE CHOOSE i \in fs : \A j \in fs : i <= j
TakeRun(r, c, lo, hi, best, bestw) ==
  LET vis == {i \in lo..(hi - 1) : ~c[i].flag /\ c[i].stamp <= rsnap[r]}
      pick == IF vis = {} THEN 0
              ELSE IF Mutant = "chainorder" THEN CHOOSE i \in vis : \A j \in vis : j <= i
              ELSE CHOOSE i \in vis : \A j \in vis : c[j].stamp <= c[i].stamp
  IN IF pick = 0 \/ (Mutant # "chainorder" /\ c[pick].stamp <= best) THEN [s |-> best, w |-> bestw]
     ELSE [s |-> c[pick].stamp, w |-> c[pick].w]

\* After the run: resolve the next flagged revision, or end the read of this entity.
Advance(r, c, lo, best, bestw) ==
  LET hi == NextFlag(c, lo)
      b == TakeRun(r, c, lo, hi, best, bestw)
  IN IF hi > Len(c)
     THEN /\ rres' = [rres EXCEPT ![r][rent[r]] = b]
          /\ rfirst' = [rfirst EXCEPT ![r][rent[r]] = IF rround[r] = 1 THEN b ELSE @]
          /\ NextEntity(r)
          /\ ridx' = [ridx EXCEPT ![r] = hi]
          /\ rbest' = [rbest EXCEPT ![r] = b.s] /\ rbestw' = [rbestw EXCEPT ![r] = b.w]
          /\ UNCHANGED rslot
     ELSE /\ ridx' = [ridx EXCEPT ![r] = hi]
          /\ rbest' = [rbest EXCEPT ![r] = b.s] /\ rbestw' = [rbestw EXCEPT ![r] = b.w]
          /\ rslot' = [rslot EXCEPT ![r] = 1]
          /\ rpc' = [rpc EXCEPT ![r] = "o1"]
          /\ UNCHANGED <<rent, rround, rres, rfirst>>

\* A seqlock-validated copy of the chain, its published revisions taken at once.
RCopy(r) ==
  /\ rpc[r] = "copy"
  /\ rcopy' = [rcopy EXCEPT ![r] = chain[rent[r]]]
  /\ Advance(r, chain[rent[r]], 1, -1, -1)
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rst, rsaw>>

\* A flagged revision resolved (rbest holding the result): on past it.
RScan(r) ==
  /\ rpc[r] = "scan"
  /\ Advance(r, rcopy[r], ridx[r] + 1, rbest[r], rbestw[r])
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rcopy, rst, rsaw>>

\* The live revision of the copy's flagged one, by its owner (it may have been published, or removed by an abort).
Live(r) == LET c == chain[rent[r]]
               ix == {i \in 1..Len(c) : c[i].tsn = Cur(r).tsn /\ c[i].w = Cur(r).w}
           IN IF ix = {} THEN Unset ELSE LET i == CHOOSE i \in ix : TRUE IN [s |-> IF c[i].flag THEN -1 ELSE c[i].stamp, w |-> c[i].w]

\* Owner, 1st read.  No card holds the owner: re-read the live revision (not committing, or published since).
RO1(r) ==
  /\ rpc[r] = "o1"
  /\ IF rslot[r] > NumSlots
     THEN /\ LET l == Live(r) IN IF l.s >= 0 /\ l.s <= rsnap[r] THEN Take(r, l.s, l.w) ELSE UNCHANGED <<rbest, rbestw>>
          /\ rpc' = [rpc EXCEPT ![r] = "scan"]
          /\ UNCHANGED <<rslot, ridx>>
     ELSE IF owner[rslot[r]] = Cur(r).tsn
          THEN /\ rpc' = [rpc EXCEPT ![r] = "st"] /\ UNCHANGED <<rslot, ridx, rbest, rbestw>>
          ELSE /\ rslot' = [rslot EXCEPT ![r] = @ + 1] /\ UNCHANGED <<rpc, ridx, rbest, rbestw>>
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rent, rround, rcopy, rst, rres, rfirst, rsaw>>

RSt(r) ==
  /\ rpc[r] = "st"
  /\ rst' = [rst EXCEPT ![r] = cstamp[rslot[r]]]
  /\ rpc' = [rpc EXCEPT ![r] = IF Mutant = "singleownerread" THEN "decide" ELSE "o2"]
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rent, rround, rcopy, ridx, rbest, rbestw, rslot, rres, rfirst, rsaw>>

\* Owner, 2nd read: the stamp read between two equal owner reads is that owner's.
RO2(r) ==
  /\ rpc[r] = "o2"
  /\ IF owner[rslot[r]] = Cur(r).tsn
     THEN rpc' = [rpc EXCEPT ![r] = "decide"] /\ UNCHANGED rslot
     ELSE rpc' = [rpc EXCEPT ![r] = "o1"] /\ rslot' = [rslot EXCEPT ![r] = @ + 1]
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rent, rround, rcopy, ridx, rbest, rbestw, rst, rres, rfirst, rsaw>>

\* Stamp = 0: inside steps 1-3, spin.  Otherwise visible iff the stamp is at or below the snapshot.
RDecide(r) ==
  /\ rpc[r] = "decide"
  /\ IF rst[r] = 0
     THEN rpc' = [rpc EXCEPT ![r] = "o1"] /\ UNCHANGED <<ridx, rbest, rbestw>>
     ELSE /\ IF rst[r] <= rsnap[r] THEN Take(r, rst[r], Cur(r).w) ELSE UNCHANGED <<rbest, rbestw>>
          /\ rpc' = [rpc EXCEPT ![r] = "scan"]
          /\ UNCHANGED ridx
  /\ UNCHANGED <<next, owner, cstamp, chain, lockX>> /\ UNCHANGED wvars
  /\ UNCHANGED <<rsnap, rent, rround, rcopy, rslot, rst, rres, rfirst, rsaw>>

Reader(r) == RBegin(r) \/ RCopy(r) \/ RScan(r) \/ RO1(r) \/ RSt(r) \/ RO2(r) \/ RDecide(r)

-----------------------------------------------------------------------------
(* The GC *)

Live_R == {r \in R : rpc[r] \notin {"begin", "done"}}
Cutoff == IF Live_R = {} THEN next ELSE CHOOSE m \in {rsnap[r] : r \in Live_R} : \A r \in Live_R : m <= rsnap[r]
Key(x) == IF Mutant = "gcbytsn" THEN x.tsn ELSE x.stamp

GC(e) ==
  /\ lockX[e] = 0
  /\ LET c == chain[e]
         old == {i \in 1..Len(c) : ~c[i].flag /\ Key(c[i]) <= Cutoff}
         keep == IF old = {} THEN {} ELSE {CHOOSE i \in old : \A j \in old : Key(c[j]) <= Key(c[i])}
         dropped == {c[i] : i \in old \ keep}
     IN /\ dropped # {}
        /\ chain' = [chain EXCEPT ![e] = SelectSeq(c, LAMBDA x : x \notin dropped)]
  /\ UNCHANGED <<next, owner, cstamp, lockX>> /\ UNCHANGED wvars /\ UNCHANGED rvars

-----------------------------------------------------------------------------

AllDone == (\A w \in W : wpc[w] = "done") /\ (\A r \in R : rpc[r] = "done")
Terminated == AllDone /\ UNCHANGED vars

Next ==
  \/ \E w \in W : Writer(w)
  \/ \E r \in R : Reader(r)
  \/ \E e \in E : GC(e)
  \/ Terminated

Spec == Init /\ [][Next]_vars

-----------------------------------------------------------------------------
(* Invariants *)

Committed(w) == wstamp[w] # 0 /\ ~aborted[w]
\* What a reader at snap must read of e: the highest-stamp committed write at or below it, or the base.
Expect(snap) == LET ws == {w \in W : Committed(w) /\ wstamp[w] <= snap}
                IN IF ws = {} THEN [s |-> 0, w |-> 0]
                   ELSE LET m == CHOOSE w \in ws : \A v \in ws : wstamp[v] <= wstamp[w] IN [s |-> wstamp[m], w |-> m]

Read(r, e) == rres[r][e] # Unset

SnapshotRead == \A r \in R, e \in E : Read(r, e) => rres[r][e] = Expect(rsnap[r])

RepeatableRead == \A r \in R, e \in E : Read(r, e) /\ rfirst[r][e] # Unset => rres[r][e] = rfirst[r][e]

AtomicVisibility == \A r \in R, w \in W, e1, e2 \in E :
  Read(r, e1) /\ Read(r, e2) /\ rres[r][e1].w = w /\ w # 0 => rres[r][e2].s >= wstamp[w]

Causality == \A r \in R : \A w \in rsaw[r] : \A e \in E : Read(r, e) => rres[r][e].s >= wstamp[w]

NoDirtyRead == \A r \in R, e \in E : Read(r, e) /\ rres[r][e].w # 0 => ~aborted[rres[r][e].w]

\* Every live snapshot's revision is still in the chain.
GCSafety == \A r \in Live_R, e \in E :
  \E i \in 1..Len(chain[e]) : chain[e][i].w = Expect(rsnap[r]).w

TypeOK ==
  /\ next \in Nat
  /\ \A w \in W : wpc[w] \in {"begin", "reserve", "reset", "stage", "append", "register", "drawfirst", "draw", "store",
                              "publish", "releaseearly", "publishrest", "release", "returned", "done"}
=============================================================================
