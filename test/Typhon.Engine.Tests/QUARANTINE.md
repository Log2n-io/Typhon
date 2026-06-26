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

> Status: **scaffold — to be populated from the first `c6id` gate run.** The infrastructure (filter +
> this index) is in place; the per-test tagging is the remaining step and needs the first CI run.

## Quarantined tests

| Test (fully-qualified) | Issue | Reason | Added |
|------------------------|-------|--------|-------|
| _(none yet — populate from the first gate run)_ | | | |
