# Typhon durability — TLA+ specifications

Formal models of Typhon's durability protocols, model-checked with **TLC**. Each spec proves a set of `rules/`
invariants exhaustively (every reachable state within small bounds), complementing the empirical crash tests
(`test/.../Durability/CrashRecovery/`) which sample specific interleavings. Decision **D8** (see
`../../design/Durability/MinimalWal/` README) fixed the first three; `CommitStamp` (S4) models MVCC visibility:

| Spec | Models | Proves | Phase | Status |
|------|--------|--------|-------|--------|
| **`CheckpointProtocol.tla` (S1)** | barrier / capture / skip / flush2 / coverage gate / recycle / crash anywhere (DC/ACW/seqlock abstracted) | CK-01/02/04 + `NoLostPage` (CK-03 ∘ CK-04) | P0.7 | ✅ TLC green |
| **`CommitRecovery.tla` (S2)** | append (markers) / publish / drain / checkpoint / torn-tail crash / recovery `Run()` incl. crash-during-recovery + re-run | LOG-04/05, AP-01/02/12, end-state ≡ committed prefix | P1.4 | ✅ TLC green |
| **`CommittedDiscipline.tla` (S3)** | Committed-mode (Variant A) stage / claim / floor / frame publish / publish / withdraw vs a checkpoint cycle (barrier / floor read / capture / write / CheckpointLSN := the capped barrier) / crash anywhere incl. crash-during-recovery. One slot with serialized writers, or two slots written concurrently (`-twoslots`) | CM-01 (HEAD + durable never hold uncommitted bytes), AP-01, CK-13, CM-03 / acknowledged ⇒ durable | P2 | ✅ TLC green (mutant `BreakStaging` = Variant B violates `CM01_HeadCommitted`; each of the six `-mutant-<name>` CK-13 mutants violates `CK13_BelowUnpublished` or `AckDurable`) |
| **`CommitStampFamilies.tla` (S5)** | MVCC option C across families: two writers, one spawning and one destroying beside an update of a shared entity; slots claimed in an in-memory claim word, occupancy the published set; spawns placed in PREPARE (pending born, claimed, not occupied), destroys pending then hidden (claimed, not occupied) until the GC; a reader looks up, and scans in two reads (occupancy, then claims) | VIS-01/05/08/09: `RevisionRead`, `SpawnRead`, `DestroyRead`, `RepeatableRead`, and no deadlock | Plan 06 steps 5.0, 5.7 | ✅ TLC green: one card (405 k states), `-twoslots` (5.6 M); each of the 5 `-mutant-<name>` violates (`publishclearsclaim` is step 5.2's first protocol) |
| **`BackupCapture.tla` (S6)** | online backup capture (#1412): writers in a striped write gate (count + freeze flag) marking a change map and copying before writing; a freeze detaching the map (incremental) or taking every page (full), sampling C and the published LSN, setting the reclaim floor; a two-step scanner (claim, read); the tail flush and copy; checkpoint + reclaim with the floor; the hook's removal under the drained gate (freeze raised); an abort anywhere, in four steps (flag releasing waiters, uninstall request, removal, merge of the detached map) | BKP-01/07 `ChangeMapComplete`, BKP-02/03 `ImageFidelity`, BKP-04 `TailCoversImage` + `NoNeededRecordReclaimed`, BKP-12 `HookNotRemovedUnderWriter`; liveness under weak fairness and no timeout (`-liveness`): BKP-12 `WritersProgress` + `GateReopens` | #1412 | ✅ TLC green: 2 pages / 2 writers up to permutation / 1 version / 2 captures (2.76 M states, ~8 s), `-liveness` 2 writers / 1 capture (194 K, ~18 s), `-full` with 2 versions (32.2 M, ~2 min, skipped by the gate); each of the 9 `-mutant-<name>` violates (`noclaimwait`, `nofloor`, `noflush`, `mapafterwrite`, `nogate`, `mergeimageset`, `uninstallnogate`; temporal: `noabortrelease`, `timeoutkeepsgate`) |
| **`CommitStamp.tla` (S4)** | MVCC option C, family 1: writers reserve a commit card, stage flagged revisions (a handler path keeping its chain locks), APPEND (may fail), register / draw the stamp / store it, publish, release; lock-free readers resolve flagged revisions through the card (Owner, Stamp, Owner) and revalidate; the GC keyed by stamp | VIS-01/03/04/05/06: `SnapshotRead`, `RepeatableRead`, `AtomicVisibility`, `Causality`, `NoDirtyRead`, `GCSafety`, and no deadlock | Plan 06 step 3.0 | ✅ TLC green: the gate's bound (1 reader, 171 k states), `-twoslots` (two committers in flight, 4.2 M), `-full` (the plan's 2 × 2 × 2 + GC, 88.6 M, ~10 min, skipped by the gate); each of the 8 `-mutant-<name>` violates |

## Running TLC

Java + `tla2tools.jar` are all you need. The jar is **not committed** — `run-tlc.sh` downloads it on first use into the
gitignored `.tools/`. Version **v1.7.4** is pinned because it runs on **Java 8** (the local toolchain); newer jars need
Java 11+.

```bash
./run-tlc.sh CommitRecovery                      # model-check (expects GREEN)
./run-tlc.sh CommitRecovery mutant --expect-violation   # genuineness: a broken variant MUST violate
./run-tlc.sh CommittedDiscipline twoslots        # another green model: CommittedDiscipline-twoslots.cfg
./run-tlc.sh CommittedDiscipline mutant-nofloor --expect-violation   # one of several mutants: CommittedDiscipline-mutant-nofloor.cfg
```

`run-tlc.sh <Spec>` succeeds (exit 0) iff TLC prints "No error has been found". CI (the `tla` job in
`../../.github/workflows/merge-gate.yml`) calls the same script for every `.cfg`, so local and CI are identical.

## Conventions (each spec MUST follow)

- **Header maps invariants → rule IDs.** The module comment lists every checked invariant against its `rules/`
  rule (e.g. `LOG04_MarkerGates ⇒ LOG-04`). A reviewer can cross-check the spec covers what it claims.
- **`.cfg` holds the model bounds + invariant list.** Bounds stay within the §6 budget: ≤ 3 pages, ≤ 2 txs,
  ≤ 2 background threads, total state space `< 10⁷` (TLC reports the count at the end). A second green bound set is
  `<Spec>-<name>.cfg`.
- **Genuineness mutant.** Each spec ships a `<Spec>-mutant.cfg` (or a `CONSTANT` toggle) that deliberately breaks one
  guard; `run-tlc.sh <Spec> mutant --expect-violation` proves the invariant actually bites (a vacuously-green spec
  proves nothing). Further mutants are `<Spec>-mutant-<name>.cfg`, run as `run-tlc.sh <Spec> mutant-<name> --expect-violation`.
  Spec names carry no `-`, because the CI loop splits a cfg name at the first one, and a green model's `<name>` must not start
  with `mutant`.
- **Exhaustive bounds past the gate's budget** go in `<Spec>-full.cfg` (or `<Spec>-<name>-full.cfg`): green models the CI loop skips,
  run locally after any change to the spec or its protocol. The gate keeps a smaller green bound of the same spec.
- **A deadlock is a violation.** `run-tlc.sh` counts TLC's "Deadlock reached" as one, for a protocol whose liveness is that no state is
  stuck (CommitStamp's reservation order); a green model must then end in an explicit terminal stutter.
- **Abstraction is explicit.** What is intentionally NOT modeled (e.g. seqlock parity, physical segments, page-cache
  eviction, bulk-load) is stated in the header, so the proof's scope is honest.

## Maintenance contract (08-test-plan §6)

A PR that changes a protocol covered by a spec **MUST update the spec in the same PR**. The `scope:` line of each rule in
`../durability.md` names its spec, and the rule's `[VerifiesRule]`/spec linkage is checked in review.
