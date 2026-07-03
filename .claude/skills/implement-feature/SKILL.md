---
name: implement-feature
description: Implement a GitHub issue end-to-end — scope it (whole issue or specific phases), build an acceptance-criteria plan from its design doc, get the plan approved, then develop autonomously with tests and a mandatory code review.
argument-hint: [issue number]
---

# Implement a Feature from a GitHub Issue

Take a GitHub issue as the entry point and carry it through to a reviewed, tested implementation: scope it, plan it against its design doc, get the plan approved, then develop it **autonomously**.

This is the heavyweight, autonomous counterpart to `/start-task`: `/start-task` only sets up status + branch; this skill plans and *implements* the work.

## Input

`$ARGUMENTS` may contain a GitHub issue number (`351` or `#351`).

- **Issue number provided** → proceed to the workflow.
- **Missing or non-numeric** → ask the user for the issue number (a direct question or `AskUserQuestion`). **Do not guess.** The skill cannot run without an issue — the issue is the entry point.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/implement-feature [#N]

  Implement a GitHub issue end-to-end, autonomously.

Arguments:
  #N              Issue number (e.g., 351 or #351) — required; prompted if missing
  --help, -h      Show this help

What it does:
  1. Fetches the issue and its linked design doc
  2. Proposes the scope — whole issue, or one/several phases
  3. Builds an acceptance-criteria plan from the design doc (plan mode)
  4. On approval: develops it autonomously — code + tests
  5. Runs a mandatory code review before declaring completion
  6. Stops and asks only when a real impediment blocks completion

Examples:
  /implement-feature #351
  /implement-feature
```

## Workflow

### 1. Fetch the issue & locate the design doc

- `mcp__GitHub__get_issue` — owner `log2n-io`, repo `Typhon`, issue number `<N>`.
- The issue body MUST link a **design doc** (under `claude/design/`). Find that link.
- Read the design doc **in full** — it is the source of truth for the plan. Read the code it references to verify current state.
- If no design doc is linked, or it cannot be found → **stop and ask the user** for the design-doc path (this is an impediment — see [Impediments](#impediments--when-to-stop)). The plan cannot be built without it.

### 2. Propose the scope

The issue and/or design doc describe the work as a whole and, usually, as numbered **phases**.

Use `AskUserQuestion`:
- **Question:** "What should I implement for #N: <title>?"
- **Header:** "Scope"
- **Options** (multi-select so the user can pick several phases):
  - `The whole issue` — every phase
  - `Phase 1 — <name>`, `Phase 2 — <name>`, … — one option per phase found

If the issue has no phases, the scope is the whole issue — skip the question.

### 3. Build the implementation plan

From the design doc + the selected scope, construct an implementation plan. The plan **MUST** contain, for the selected scope (one section per phase when several are chosen):

1. **Overview** — what this phase/scope delivers and why, in the design doc's terms.
2. **Acceptance Criteria** — a numbered, checkable list. *Completion is defined as every AC met.* Each AC must be concrete and verifiable — a specific behavior, API, file, or passing test — never vague.
3. **Implementation details** — the files, types, and changes mapped to each AC; the approach and the order of work; integration points. Grounded in the design doc. Any deviation from the design MUST be called out explicitly and approved (root `CLAUDE.md`: never deviate from specs silently).
4. **Tests** — the tests to write so the feature is *proven to work* and *protected from regression*: unit tests per AC, plus integration / Playwright tests where the design calls for them. Follow Typhon test conventions — NUnit, `TestBase<T>`, `scripts/test-affected.py`, the 15 s timeout rule, full suite before done (root `CLAUDE.md`).
5. **Code review gate** — an explicit final step: before the scope is declared complete, a code review **MUST** be performed and **pass** (correctness, AC coverage, quality, adherence to `.editorconfig` + `CLAUDE.md` standards, no `claude/rules/` invariant violated).

Draft the plan and present it for approval using **plan mode** — call `ExitPlanMode` with the full plan. **Implementation must not begin until the user approves the plan.** If the user requests changes, revise and re-present.

Once approved, post the accepted plan as a comment on the issue (durable record; `/complete-task` reads it later).

### 4. Prepare the workspace

After the plan is approved:
- Ensure an issue branch exists. If not, follow the `/start-task` branch conventions: `feature/<N>-short-name` from `main`, plus the matching branch in the nested `claude/` repo.
- Set the issue's project **Status → In Progress** (item lookup + field IDs: see `.claude/skills/_helpers.md` and `/start-task`).

### 5. Develop autonomously

Implement the plan **in full autonomy**. The approved plan is your mandate — work through the Acceptance Criteria in order, writing each AC's code and its tests together.

- Do **not** pause for confirmation on routine decisions (naming, small structure, obvious trade-offs) — make the reasonable call per the design and keep going.
- After each AC, run the affected tests: `python3 scripts/test-affected.py <files>` (15 s timeout).
- Hold to Typhon conventions throughout — `.editorconfig`, no LINQ on hot paths, `[LoggerMessage]`, no nullable reference types, etc. (root `CLAUDE.md`).
- Track progress with `TaskCreate` / `TaskUpdate` — one task per Acceptance Criterion, marked completed as each is met.

**Stop only for an important impediment** — see below.

### 6. Verify

- Once unit-green, run the full suite once: `dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Debug --no-build`.
- Confirm **every Acceptance Criterion** is met — list each AC with its evidence (the test that proves it, the observable behavior).
- For UI / Workbench work, exercise the feature in a browser (`/wb-dev`) — type checks and tests verify code, not feature correctness.

### 7. Code review — mandatory gate

Before declaring completion, a code review **MUST** happen and **pass**:
- Self-review the full diff against the Acceptance Criteria, the design doc, and the Typhon coding standards.
- If a PR exists, run `/review`; run `/security-review` for security-sensitive changes; consider `/simplify` on the changed code.
- If the review surfaces issues, fix them and re-review. **Completion is blocked until the review passes.**

### 8. Complete

- Report: each Acceptance Criterion + its evidence, the tests added, the code-review outcome. Explicitly list **any AC not met** and why (root `CLAUDE.md` plan discipline).
- Update artifacts: check the implemented phase checkbox(es) on the issue; update the design doc status.
- Close out via `/complete-subtask` (per phase) or `/complete-task` (whole issue) — these update the project board and parent checkboxes.
- **Never commit** — per project convention the user owns all git commits. Announce "ready for review + commit" and hand off; do not block on it.

## Impediments — when to stop

Develop autonomously, but **STOP and talk to the user** the moment an *important impediment* arises that compromises completion. Examples:

- The design doc is missing, contradicts the issue, or is silent on something load-bearing.
- An Acceptance Criterion cannot be met as specified.
- A required dependency, API, or a prior phase the work depends on is missing or broken.
- Implementation reveals the design itself is wrong or infeasible.
- The only way forward would violate a `claude/rules/` correctness invariant or a coding standard, with no clean path.

When this happens: **stop. Do not hack around it.** Clearly state the impediment, the options you see, and your recommendation — then engage the user in conversation to find a solution together. Resume autonomous development once it is resolved.

Routine ambiguity (naming, minor structure, small trade-offs) is **not** an impediment — make the reasonable call and continue.
