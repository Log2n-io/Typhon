# Quarantined tests

The merge gate (`.github/workflows/merge-gate.yml`) runs the full `Typhon.Engine.Tests` suite with
`--filter "Category!=Quarantine"`. Tests tagged `[Category("Quarantine")]` are **excluded** from the
gate so the gate can be **green on a clean `main`** — see
`claude/design/Infrastructure/ci-merge-gate.md`.

Quarantine is for **documented, pre-existing reds that are not regressions** of the PR under test:
the deferred-DC backpressure issue (`#133`), the SV-durability P2 known-issue, and a few
environment/parallel-flaky tests. It is **not** a dumping ground — a genuinely broken test must be
fixed, not quarantined.

## The four markers (#703)

`[Ignore]` used to mean five different things, so nothing could lint it and nothing revisited it. One meaning
each now, enforced by `scripts/lint-test-suppressions.py` in the merge gate's `invariants` job:

| Marker | Means | Requires | Runs |
|---|---|---|---|
| `[Ignore("#N …")]` | the test **cannot** pass until #N ships | an **open** issue | nowhere |
| `[Category("Quarantine")]` | known-red, cause unfixed | an **open** issue + a row below | locally only |
| `[Category("Sensitive")]` | passes alone, flakes under parallel load | — | the gate's serial quiet pass |
| `[Explicit] + [Category("Nightly")]` | can pass; too slow/noisy for the gate | — | `nightly-suppressed.yml` |
| `[Explicit] + [Category("Manual")]` | CI genuinely cannot run it | a comment saying **why** | nowhere |

`[Ignore]` for slow / manual / flaky is rejected by the lint. The distinction is not pedantry: `ChaosStressTests`
was `[Ignore("Too long…")]` when it in fact **livelocked** (#695), and `[Ignore]` is unconditional in NUnit — no
`--filter` could reach it — so the label made the hang unreachable *and* invisible while `shards.json` still named
the fixture, giving a CI shard that ran zero tests and reported green.

**`Sensitive` is not a dumping ground for anything flaky.** It runs a test *alone*, so it only fits a test whose
failure is caused by contention for CPU. A test whose failure is caused by a **race in the code under test** must
be quarantined instead — running it alone would guarantee a green (that is #709's story).

## Rules

- Every quarantined test carries `[Category("Quarantine")]` **and** an inline comment linking its
  tracking issue and the reason.
- Every quarantined test is listed in the table below (test → issue → reason → date).
- Removing a quarantine (because the underlying issue is fixed) deletes the attribute **and** the row.
- The list is reviewed whenever its tracking issues close.

## How the list is populated

The canonical red set is **platform-specific** and must be determined on the CI box (Linux,
`c6id.8xlarge`), not a dev desktop — some reds are environment/parallel-flaky. Procedure:

1. With the AWS prerequisites in place (P0), run the gate once against `main` (`workflow_dispatch`).
2. Read the failing tests from the run's `engine.trx` artifact.
3. For each failure that is a **documented known-red** (not a new regression), add
   `[Category("Quarantine")]` + an issue-linked comment, and a row below.
4. Re-run until the gate is green on `main`. That green is the proof the quarantine is complete.

> Status: **populated from the first `c6id` gate run (PR #405).** The bulk of that run's reds were
> infrastructure issues (cache sizing, a stale type name, a Windows-only file-lock assertion, a stale
> WAL-v2 checkpoint assertion) and were **fixed**, not quarantined. Only the two genuinely Linux-specific
> failures below — which pass on Windows and cannot be reproduced/diagnosed from a Windows dev box — are
> quarantined pending a dedicated Linux investigation.

## Quarantined tests

| Test (fully-qualified) | Issue | Reason | Added |
|------------------------|-------|--------|-------|
| `Typhon.Engine.Tests.BackupCaptureRoundTripTests.AHardCrashUnderTheSameWorkload_IsSoundAfterRecovery` | [#1450](https://github.com/Log2n-io/Typhon/issues/1450) | Crash recovery + clean close leaves a destroyed Versioned entity's key in its freed cluster slot (CHK-CLU-01 Divergence, Lossless) on ~1 seed in 5; found by the backup round trip, reproduced with no backup. | 2026-10-19 |
| `Typhon.Engine.Tests.ChaosStressTests` — 2 tests (`CrossEntityTransaction_AtomicMultiUpdate`, `UltimateStress_AllSubsystems`) | [#696](https://github.com/log2n-io/Typhon/issues/696) | Re-measured 2026-10-12 on `f83ec312`: the rollback double free and stale-position void are fixed on main (REAP-02, CHAIN-01), and five former members pass 11 of 11 runs. Left: CrossEntity reports 5 s revision-chain lock timeouts 1-2 runs in 6 (the conserved sum holds); UltimateStress fails or hangs every run on an IXW-01 B+Tree descent failure past the append (#946's residual), which stops the engine. The fixture is `[Explicit] + [Category("Nightly")]`. | 2026-08-07 |
| `Typhon.Engine.Tests.ChaosStressTests.UniqueIndexViolation_UnderLoad` | [#1258](https://github.com/log2n-io/Typhon/issues/1258) | Retargeted from #696 on 2026-10-12: two spawns of one unique key both pass the pre-append check and the later publish stops the engine (AP-03 residual). 6 runs of 6 alone. | 2026-10-12 |
| `Typhon.Engine.Tests.LinearizabilityTests.ParallelOperations_AreLinearizable` (+ `_Deep`, + `DirtyCounter_IsConservedAtQuiesce`) | [#400](https://github.com/log2n-io/Typhon/issues/400) | The T5 model, run on the **production default** (`Deferred` + parallel transactions on one shared `UnitOfWork`), reproduces the shared-`ChangeSet` race within milliseconds: the new `[Conditional("DEBUG")]` concurrent-mutation guard reports e.g. *"thread 27 entered AddByMemPageIndex while thread 23 was still inside a mutating method"*. That is the T5 result, not an obstacle to it. Forcing these green — by moving to `Immediate`, or by removing the guard — would restore exactly the 188:48 selection bias the fixture exists to remove. Note the guard reddens **nothing else** in 5,003 tests, which measures how completely the racy structure was going unexercised. | 2026-08-07 |
| `Typhon.Engine.Tests.Data.SwgFixtureStressTests.Stress_Config_Completes_Without_Backpressure_Timeout` | [#774](https://github.com/log2n-io/Typhon/issues/774) | **Moved here from `Typhon.Workbench.Tests.FixtureConfigTests`, where the gate's path filter had been hiding it.** Reliably red, not flaky: 2 runs of 2 on the self-hosted gate box with *"B+Tree insert made no progress in 10000 pessimistic retries"*, green locally in Release. The second run's engine shard was `initial-failed=0`, so a retry net cannot absorb it — and by `shard.py`'s own premise ("a REAL regression fails every attempt") it must not be asked to. The underlying liveness defect is the #739 family; the exception's `(see #695)` citation is stale, #695's livelock being fixed. | 2026-08-11 |
