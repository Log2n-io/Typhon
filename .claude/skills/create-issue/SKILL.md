---
name: create-issue
description: Create a GitHub issue and add it to the Typhon org project
argument-hint: "[title] or leave empty for interactive mode"
---

# Create GitHub Issue for Typhon

Create a GitHub issue in **`log2n-io/Typhon`** and add it to the **Typhon** org project (org `Log2n-io`, project number **1**).

> **Migration note (2026-06-29):** the repo moved to `log2n-io/Typhon` and the board to the org project `orgs/Log2n-io/projects/1`. Native **Issue Types** (Task / Bug / Feature / Epic) are live, and **Area / Product are issue-level fields** (the ones Epics carry), not project single-selects. Release maturity is expressed as a **Milestone** on Features. Use owner `log2n-io` for repo/API ops and `Log2n-io` for `gh project` ops (both resolve to the same org; GitHub owner matching is case-insensitive).

> **🔴 Hierarchy rule — Epic → Feature → Task, no skipping.** The development cycle revolves around Features, so an Epic's direct children must be typed **Feature**, never **Task**. A **Task** may only be a child of a **Feature** (further breakdown of one feature's implementation), never a direct child of an Epic. Concretely: `parent_issue_url` = an Epic ⟹ child type = `Feature`; `parent_issue_url` = a Feature ⟹ child type = `Task`. Verified pattern: #146 (Epic) → #147 (Feature) is a direct child; #435 (Feature, child of #146) → #427 (Task, child of #435). If a piece of epic-level work doesn't need further breakdown, it's still typed **Feature** as the Epic's direct child — don't downgrade it to Task just because it has no children of its own yet.

## Input provided by user

$ARGUMENTS

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/create-issue [title]

  Create a GitHub issue and add it to the Typhon org project.

Arguments:
  title           Issue title text — if provided, skips the title prompt
  --help, -h      Show this help

What it does:
  1. Gathers info (title, description, labels, issue type, area, product, milestone)
  2. Creates the issue (MCP) and sets its Issue Type (gh --type)
  3. Adds it to the Typhon org project board and sets Status
  4. Optionally links it under a parent Epic as a native sub-issue

Examples:
  /create-issue
  /create-issue "Add spatial indexing support"
```

## Required information

Gather the following. If NOT provided in the arguments, use `AskUserQuestion`:

1. **Title** (required): a clear, concise issue title
2. **Description** (required): what needs to be done and why
3. **Issue Type** (recommended — native): `Task`, `Bug`, `Feature`, `Question`, or `Epic`
4. **Type labels** (optional): one or more of `bug`, `enhancement`, `documentation`, `performance`, `refactoring`, `testing`, `technical-debt`
   (Questions use the **`Question` Issue Type**, not a label.)
5. **Area** (optional — issue-level field): the subsystem outcome area (e.g. Execution, Storage, Concurrency, Observability, …). Match the parent Epic's Area when there is one.
6. **Product** (optional — issue-level field): e.g. `Engine`, `Workbench`.
7. **Milestone** (optional — release maturity on Features): e.g. `alpha-1`.
8. **Parent Epic** (optional): issue number to link under as a native sub-issue.

> **Priority / Estimate** were project single-selects on the old board and are currently unconfigured on the new one. If needed, convey priority with labels (`important`, `P0-Critical`, `P1-High`, …) rather than a project field.

## Gathering information

- If the user gave a clear title + description, proceed directly.
- Otherwise ask via `AskUserQuestion`; multi-select for labels.
- Confirm the Issue Type — it's the primary classifier now (labels are secondary).

## Creating the issue

**IMPORTANT — absolute URLs in the body.** GitHub renders issue bodies outside the repo context (project board, etc.), so relative links 404. Use `https://github.com/Log2n-io/Typhon/blob/main/<path>` for files and `https://github.com/Log2n-io/Typhon/tree/main/<path>` for directories. See `.claude/skills/_helpers.md` rule #9.

### Step 1: Create the issue (MCP)

Use `mcp__GitHub__create_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- title: `"<title>"`
- body: `"<description>"`
- labels: `["<label1>", "<label2>"]`
- assignees: `["nockawa"]`  ← the maintainer; do NOT change to the org

Capture the returned `number`, `html_url`, and `id` (the big integer — needed for sub-issue linking).

### Step 2: Set the Issue Type (gh — MCP has no field for it)

```bash
gh issue edit <number> --repo log2n-io/Typhon --type "Feature"   # or Task / Bug / Question / Epic
```

### Step 3: Add to the project board

```bash
gh project item-add 1 --owner Log2n-io --url <issue_url>
```

### Step 4: Get the project item ID

**Robust patterns:** see `.claude/skills/_helpers.md` Section 2.

```bash
gh project item-list 1 --owner Log2n-io --limit 500 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" <issue_number>
```

### Step 5: Set the project Status

Status is the working project single-select (Todo / In Progress / Done). New work = **Todo**.

```bash
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <ITEM_ID> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A --single-select-option-id f75ad846
```

### Step 6 (optional): Set Area / Product / Milestone

- **Milestone** (release maturity): `gh issue edit <number> --repo log2n-io/Typhon --milestone "alpha-1"`
- **Area / Product** are **issue-level fields** (the same ones Epics carry — e.g. #146 = Area:Execution, Product:Engine), **not** the now-empty project single-selects. `gh` has no dedicated flag yet; set them via the GitHub web UI, or via GraphQL if scripting. Match the parent Epic's values when linking under one.

### Step 7 (optional): Link under a parent Epic or Feature (native sub-issue)

**Before linking, check the parent's Issue Type** and set the child's type to match the Epic → Feature → Task rule above: linking under an **Epic** ⟹ child type `Feature`; linking under a **Feature** ⟹ child type `Task`. Never link a `Task` directly under an `Epic`.

Use the issue's **database `id`** (big integer from Step 1), and `-F` so it serializes as a JSON integer. On Git Bash, prefix `MSYS_NO_PATHCONV=1` because the API path starts with `/`.

```bash
MSYS_NO_PATHCONV=1 gh api repos/log2n-io/Typhon/issues/<epic_number>/sub_issues \
  -X POST -F sub_issue_id=<issue_database_id>
```

Verify: `gh issue view <epic_number> --repo log2n-io/Typhon --json subIssuesSummary`.

## Umbrella issues (parent-of-many)

If the issue is a multi-phase umbrella with separate tracking issues per phase, do both:

1. **Include a `[tasklist]` block** in the body with one `#N` per sub-issue — the human-readable checklist.
2. **Link each sub-issue natively** via the Sub-issues API (Step 7 pattern) — this drives GitHub's Sub-issues panel / roll-up, which reflects only native links, not the `[tasklist]` text.

```bash
# For each sub-issue: get its database id, then link it under the umbrella
SUB_ID=$(gh api repos/log2n-io/Typhon/issues/<sub_number> --jq '.id')
MSYS_NO_PATHCONV=1 gh api repos/log2n-io/Typhon/issues/<umbrella_number>/sub_issues -X POST -F sub_issue_id=$SUB_ID
```

Verify:

```bash
MSYS_NO_PATHCONV=1 gh api repos/log2n-io/Typhon/issues/<umbrella_number>/sub_issues | python3 -c "
import json, sys
for s in json.load(sys.stdin):
    print(f'#{s[\"number\"]} - {s[\"title\"]} [{s[\"state\"]}]')
"
```

Do both — the `[tasklist]` gives a body-embedded checklist (used by `/complete-subtask`), the native linkage gives GitHub's first-class Sub-issues UI. If retroactively converting an existing `[tasklist]` umbrella (its `subIssuesSummary.total` is 0), just run the linking loop — no body edit needed.

## Field Reference (org project `Log2n-io` / #1)

### Project
- Project ID: `PVT_kwDOEcGj5M4Bb-8P`  (number `1`, owner `Log2n-io`)

### Status Field (the working project single-select)
- Field ID: `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A`
- Options: `Todo` = `f75ad846` · `In Progress` = `47fc9ee4` · `Done` = `98236657`

### Other project single-selects (currently **empty** — no options configured)
- Priority: `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu0` · Estimate: `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu4` · Area: `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu8` · Product: `PVTSSF_lADOEcGj5M4Bb-8PzhW2BfY`
- Re-verify anytime: `gh project field-list 1 --owner Log2n-io --format json`

### Issue-level classifiers (not project fields)
- **Issue Type:** `gh issue edit <n> --repo log2n-io/Typhon --type "<Type>"` (Task/Bug/Feature/Question/Epic)
- **Area / Product:** issue custom fields (web UI / GraphQL); mirror the parent Epic
- **Milestone:** release maturity — `gh issue edit <n> --repo log2n-io/Typhon --milestone "<name>"`

## Output

After creating, report back with:
- Issue number, title, and link
- Issue Type set
- Confirmation it was added to the Typhon org project (+ Status)
- Parent Epic link, if any
- Any Area / Product / Milestone / labels set

## Example interaction

User: `/create-issue Add support for spatial indexing`

Claude: *asks for description, issue type, area, and (optionally) parent Epic*

User: *provides answers*

Claude: *creates issue, sets type, adds to project + Status, links parent, reports success*

```
Feature #123 created: "Add support for spatial indexing"
   Link: https://github.com/Log2n-io/Typhon/issues/123
   Issue Type: Feature
   Added to Typhon org project · Status: Todo
   Parent: sub-issue of Epic #NN
   Area: Indexes · Product: Engine · Labels: enhancement, performance
```
