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
| `Typhon.Engine.Tests.CheckerboardTests.SpatialGridAccessor_AccessibleFromTickContext` | [#406](https://github.com/nockawa/Typhon/issues/406) | Linux-CI-only: `SpatialGrid.IsValid` false in the tick callback; passes on Windows (isolated + full parallel). Needs Linux repro. | 2026-06-26 |
| `Typhon.Engine.Tests.ViewChangeCaptureTests.UnchangedField_NoEntryForThatFieldView` | [#406](https://github.com/nockawa/Typhon/issues/406) | Linux-CI-only: `IndexOutOfRangeException`; passes on Windows. Needs Linux repro. | 2026-06-26 |
| `Typhon.Engine.Tests.ExceptionPathLeakTests` (whole fixture — 3 lock-timeout leak tests) | [#410](https://github.com/nockawa/Typhon/issues/410) | c6id-only: `MMF.CheckInternalState` compares the whole page-state array, racing background page-cache/checkpoint timing; fails on the slower gate box even in the serial quiet pass (green locally). Likely an over-broad leak check, not a real leak. Fix = narrow/quiesce the check. | 2026-06-27 |
| `Typhon.Engine.Tests.Runtime.CheckerboardTests` (whole fixture) | [#552](https://github.com/log2n-io/Typhon/issues/552) | Non-deterministic tick-cadence / spatial-tier assertions fail under CPU contention (a different test each run). Was a fixture-level `[Ignore]`, which also removed it from local runs and from `--filter`, while `shards.json` still named it — so a CI shard budgeted a slot and ran zero tests. Needs cadence-independent assertions. | 2026-08-07 |
| `Typhon.Engine.Tests.ChaosStressTests` — 7 tests (`CrossEntityTransaction_AtomicMultiUpdate`, `AllowMultipleIndex_HighChurn`, `IndexSplit_CascadingSplitsUnderContention`, `RollbackStress_ConcurrentRollbacks`, `UniqueIndexViolation_UnderLoad`, `UltimateStress_AllSubsystems`) | [#696](https://github.com/log2n-io/Typhon/issues/696) | Concurrent value/index correctness failures, 3/3 on first-ever execution. The fixture as a whole is now `[Explicit] + [Category("Nightly")]`; these individual tests are additionally quarantined. | 2026-08-07 |
| `Typhon.Engine.Tests.ChaosStressTests.CreateDeleteRecreate_RapidLifecycle` | [#696](https://github.com/log2n-io/Typhon/issues/696) | **Retargeted from #695, whose livelock is fixed.** It used to need `--blame-hang` to terminate; it now completes in ~150 ms and fails about 1 run in 4 with *"A duplicate key was detected in a unique index"* — a concurrent unique-index correctness defect, #696's family, possibly #716's mechanism. | 2026-08-08 |
| `Typhon.Engine.Tests.LinearizabilityTests.ParallelOperations_AreLinearizable` (+ `_Deep`, + `DirtyCounter_IsConservedAtQuiesce`) | [#400](https://github.com/log2n-io/Typhon/issues/400) | The T5 model, run on the **production default** (`Deferred` + parallel transactions on one shared `UnitOfWork`), reproduces the shared-`ChangeSet` race within milliseconds: the new `[Conditional("DEBUG")]` concurrent-mutation guard reports e.g. *"thread 27 entered AddByMemPageIndex while thread 23 was still inside a mutating method"*. That is the T5 result, not an obstacle to it. Forcing these green — by moving to `Immediate`, or by removing the guard — would restore exactly the 188:48 selection bias the fixture exists to remove. Note the guard reddens **nothing else** in 5,003 tests, which measures how completely the racy structure was going unexercised. | 2026-08-07 |
| `Typhon.Engine.Tests.DifferentialRecoveryOracleTests.PayloadAxes_SurviveACrash` | [#389](https://github.com/log2n-io/Typhon/issues/389) | `ComponentCollection` buffer mutations are not WAL-redo-logged, so after a crash in the WAL window every collection recovers **EMPTY behind an intact buffer descriptor**. This is the first test in the suite that can SEE that: the raw component-bytes comparison reports 0 mismatches (the descriptor survives) while the element check reports all 8 — which is precisely why `RecoveryShadowModel` now refuses to capture a collection-bearing archetype without an `ICollectionProjector`. Regression lock for when #389 is fixed. | 2026-08-07 |
