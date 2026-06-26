# Quarantined tests

The merge gate (`.github/workflows/merge-gate.yml`) runs the full `Typhon.Engine.Tests` suite with
`--filter "Category!=Quarantine"`. Tests tagged `[Category("Quarantine")]` are **excluded** from the
gate so the gate can be **green on a clean `main`** — see
`claude/design/Infrastructure/ci-merge-gate.md`.

Quarantine is for **documented, pre-existing reds that are not regressions** of the PR under test:
the deferred-DC backpressure issue (`#133`), the SV-durability P2 known-issue, and a few
environment/parallel-flaky tests. It is **not** a dumping ground — a genuinely broken test must be
fixed, not quarantined.

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
