# Typhon durability — TLA+ specifications

Formal models of Typhon's durability protocols, model-checked with **TLC**. Each spec proves a set of `rules/`
invariants exhaustively (every reachable state within small bounds), complementing the empirical crash tests
(`test/.../Durability/CrashRecovery/`) which sample specific interleavings. Decision **D8** (see
`../../design/Durability/MinimalWal/` README) fixes exactly three specs:

| Spec | Models | Proves | Phase | Status |
|------|--------|--------|-------|--------|
| **`CheckpointProtocol.tla` (S1)** | barrier / capture / skip / flush2 / coverage gate / recycle / crash anywhere (DC/ACW/seqlock abstracted) | CK-01/02/04 + `NoLostPage` (CK-03 ∘ CK-04) | P0.7 | ✅ TLC green |
| **`CommitRecovery.tla` (S2)** | append (markers) / publish / drain / checkpoint / torn-tail crash / recovery `Run()` incl. crash-during-recovery + re-run | LOG-04/05, AP-01/02/12, end-state ≡ committed prefix | P1.4 | ✅ TLC green |
| **`CommittedDiscipline.tla` (S3)** | Committed-mode (Variant A) stage/append/publish vs fuzzy checkpoint (WAL rule + CK coverage gate) / crash anywhere incl. crash-during-recovery | CM-01 (HEAD + durable never hold uncommitted bytes), AP-01, CM-03 / acknowledged ⇒ durable | P2 | ✅ TLC green (mutant `BreakStaging` = Variant B violates `CM01_HeadCommitted`) |

## Running TLC

Java + `tla2tools.jar` are all you need. The jar is **not committed** — `run-tlc.sh` downloads it on first use into the
gitignored `.tools/`. Version **v1.7.4** is pinned because it runs on **Java 8** (the local toolchain); newer jars need
Java 11+.

```bash
./run-tlc.sh CommitRecovery                      # model-check (expects GREEN)
./run-tlc.sh CommitRecovery mutant --expect-violation   # genuineness: a broken variant MUST violate
```

`run-tlc.sh <Spec>` succeeds (exit 0) iff TLC prints "No error has been found". CI (`../../.github/workflows/tla-check.yml`)
calls the same script for every spec, so local and CI are identical.

## Conventions (each spec MUST follow)

- **Header maps invariants → rule IDs.** The module comment lists every checked invariant against its `rules/`
  rule (e.g. `LOG04_MarkerGates ⇒ LOG-04`). A reviewer can cross-check the spec covers what it claims.
- **`.cfg` holds the model bounds + invariant list.** Bounds stay within the §6 budget: ≤ 3 pages, ≤ 2 txs,
  ≤ 2 background threads, total state space `< 10⁷` (TLC reports the count at the end).
- **Genuineness mutant.** Each spec ships a `<Spec>-mutant.cfg` (or a `CONSTANT` toggle) that deliberately breaks one
  guard; `run-tlc.sh <Spec> mutant --expect-violation` proves the invariant actually bites (a vacuously-green spec
  proves nothing).
- **Abstraction is explicit.** What is intentionally NOT modeled (e.g. seqlock parity, physical segments, page-cache
  eviction, bulk-load) is stated in the header, so the proof's scope is honest.

## Maintenance contract (08-test-plan §6)

A PR that changes a protocol covered by a spec **MUST update the spec in the same PR**. The `scope:` line of each rule in
`../durability.md` names its spec, and the rule's `[VerifiesRule]`/spec linkage is checked in review.
