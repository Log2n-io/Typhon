# Skill Helpers — GitHub Operations Reference

> **Referenced by:** All skills that interact with GitHub Issues, PRs, and the Typhon Project board.
>
> **Migration note (2026-06-29):** repo is now **`log2n-io/Typhon`**; board is the org project **`Log2n-io` / #1**. Native **Issue Types** (Task/Bug/Feature/Epic) are live; **Area & Product are issue-level fields** (mirror the parent Epic), and release maturity is a **Milestone** on Features. Use owner `log2n-io` for repo/API and `Log2n-io` for `gh project` (case-insensitive, same org).

## Section 1: MCP Tools for Issues & PRs (Preferred)

The GitHub MCP server provides native tool calls that bypass all shell/encoding issues. **Always prefer MCP tools over `gh` CLI** for issue and PR operations.

### Fetch an Issue

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

Returns the full issue object including `number`, `title`, `body`, `state`, `labels`, `assignees`, etc.

### Create an Issue

Use `mcp__GitHub__create_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- title: `"<title>"`
- body: `"<body>"`
- labels: `["<label1>", "<label2>"]`
- assignees: `["nockawa"]`

Returns the created issue object with its `number` and `html_url`.

### Update an Issue (close, edit body, change title, etc.)

Use `mcp__GitHub__update_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`
- state: `"closed"` (to close) or `"open"` (to reopen)
- body: `"<new body>"` (to replace the entire body)
- title: `"<new title>"` (to change title)

**This replaces the old Pattern 5** (temp file + Python + `gh issue edit --body-file`). The body is passed directly as a string — no temp files, no encoding issues, no piping.

### List Issues

Use `mcp__GitHub__list_issues` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- state: `"open"` or `"closed"` or `"all"`
- per_page: `100` (default is 30)

Returns an array of issue objects.

### Search Issues (and PRs)

Use `mcp__GitHub__search_issues` with:
- q: `"repo:log2n-io/Typhon <search terms>"`

Add `type:pr` to search PRs specifically, e.g.:
- q: `"repo:log2n-io/Typhon type:pr <number>"` — find PRs mentioning an issue
- q: `"repo:log2n-io/Typhon is:open <keywords>"` — search open issues

Returns search results with issue/PR objects.

### Create a Pull Request

Use `mcp__GitHub__create_pull_request` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- title: `"<title>"`
- body: `"<body>"`
- head: `"<branch-name>"`
- base: `"main"`

Returns the created PR object.

### List Commits

Use `mcp__GitHub__list_commits` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`

Returns an array of commit objects with sha, message, date, author, etc.

## Section 2: Project Board Operations (gh CLI Only)

The GitHub MCP server does **NOT** support GitHub Projects V2 API. All project board operations must use `gh` CLI.

### Why These Patterns Use Python Piping

This project runs on **Windows** with Claude Code's bash shell. Two things break with `gh` CLI:

1. **Temp file paths** — `$SCRATCHPAD`, `/tmp/`, and Windows `C:\` paths have cross-environment issues. **Always pipe `gh` output directly to Python**.
2. **Python encoding** — Python on Windows defaults to `cp1252`. **Always set `PYTHONUTF8=1`** when Python writes to files.

### Pattern 1: Find a Project Item ID by Issue Number

This is the most common operation — given issue #N, find its project item ID for status updates.

```bash
# Pipe directly to Python — no temp file needed. Always use --limit 500 (default is 30).
gh project item-list 1 --owner Log2n-io --limit 500 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" <ISSUE_NUMBER>
```

### Pattern 2: Find Item ID and Current Status

```bash
gh project item-list 1 --owner Log2n-io --limit 500 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(f'{item[\"id\"]}|{item.get(\"status\", \"unknown\")}')
        sys.exit(0)
print('NOT_FOUND|unknown')
" <ISSUE_NUMBER>
```

### Pattern 3: List Items by Status

```bash
gh project item-list 1 --owner Log2n-io --limit 500 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
statuses = sys.argv[1].split(',')
for item in items:
    if item.get('status') in statuses:
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        s = item.get('status', '?')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        print(f'#{n} | {s} | {p} | {a} | {t}')
" "Todo,In Progress"
```

### Pattern 4: Update Project Item Status

Once you have the item ID (from Pattern 1), update the status:

```bash
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <ITEM_ID> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A \
  --single-select-option-id <STATUS_OPTION_ID>
```

Status option IDs:
- Todo: `f75ad846`
- In Progress: `47fc9ee4`
- Done: `98236657`

## Key Rules

1. **MCP-first for issues & PRs** — Always use MCP tools (`mcp__GitHub__get_issue`, `mcp__GitHub__update_issue`, etc.) instead of `gh issue` / `gh pr` CLI commands
2. **`gh` CLI only for project board** — `gh project item-list`, `gh project item-edit`, `gh project item-add`, `gh project field-list` have no MCP equivalent
3. **Always pipe `gh project` output directly to Python** — temp file paths break across bash/Python/Windows boundaries
4. **NEVER use `grep` on JSON** — it's brittle to formatting changes. Use Python's `json` module
5. **Always use `--limit 500`** on `gh project item-list` — the default limit is 30, which misses items on larger boards
6. **Always check for `NOT_FOUND`** in the output before proceeding
7. **If NOT_FOUND and the issue should be on the board**, add it with `gh project item-add 1 --owner Log2n-io --url <issue_url>`, then re-fetch the project data and retry the lookup
8. **Always use `PYTHONUTF8=1`** when Python writes to files — Windows defaults to cp1252, which breaks on Unicode
9. **NEVER use relative paths in GitHub issue bodies** — GitHub renders issue bodies outside the repo context (e.g., on the project board), so relative links like `[text](claude/foo.md)` resolve to 404s. Always use absolute URLs:
   - **Files:** `https://github.com/Log2n-io/Typhon/blob/main/<path>` (e.g., `https://github.com/Log2n-io/Typhon/blob/main/claude/overview/10-errors.md`)
   - **Directories:** `https://github.com/Log2n-io/Typhon/tree/main/<path>` (e.g., `https://github.com/Log2n-io/Typhon/tree/main/claude/design/errors/`)
   - **Issues:** Use `#NN` shorthand (GitHub auto-links these correctly)
   - In design docs and local markdown files, relative paths are fine — they're rendered in the repo context

## Field Reference

| Field | Field ID | Notes |
|-------|----------|-------|
| Project ID | `PVT_kwDOEcGj5M4Bb-8P` | org `Log2n-io`, project #1 |
| Status | `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A` | Todo `f75ad846` · In Progress `47fc9ee4` · Done `98236657` |
| Priority | `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu0` | currently no options (unused) |
| Estimate | `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu4` | currently no options (unused) |
| Area | `PVTSSF_lADOEcGj5M4Bb-8PzhWsLu8` | project field empty — **Area is an issue-level field** |
| Product | `PVTSSF_lADOEcGj5M4Bb-8PzhW2BfY` | project field empty — **Product is an issue-level field** |

**Issue-level classifiers (not project fields):** Issue Type → `gh issue edit <n> --repo log2n-io/Typhon --type "<Task|Bug|Feature|Epic>"`; Milestone (release maturity) → `--milestone "<name>"`; Area/Product/Claude Code Discussion → `setIssueFieldValue` (below).

### Issue-level custom fields — `setIssueFieldValue`

`Area`, `Product`, `Priority`, `Estimate` and `Claude Code Discussion` are **organization issue-fields**, not
Projects-v2 board fields. The board columns of the same name are *derived* from them — that is why
`updateProjectV2Field` is rejected with *"Fields derived from issues…must be updated through their respective
APIs"*. Set them on the ISSUE and the board follows. A normal `gho_` token suffices; no `PROJECT_TOKEN`.

| Field | Field ID | Type | Options / format |
|-------|----------|------|------------------|
| Area | `IFSS_kgDOApo0DQ` | single-select | Runtime `IFSSO_kgDOBI3leQ` · Execution `IFSSO_kgDOBI3lcg` · Schema `…ldg` · MVCC `…ldQ` · Concurrency `…lcQ` · Storage `…lcw` · Durability `…ldA` · Spatial `…ldw` · Query `…leA` · Observability `…leg` · Resources `…lew` · Errors `…lfA` · Utilities `…lfQ` · Workbench `…lfg` · Indexes `…l4Q` (prefix `IFSSO_kgDOBI3`) |
| Product | `IFSS_kgDOApsS4A` | single-select | Engine `IFSSO_kgDOBI9scA` · Workbench `…scg` · CLI `…sdA` · CI `…sdg` · User-adoption `IFSSO_kgDOBI-1jg` |
| Priority | `IFSS_kgDOApoUaA` | single-select | P0 `IFSSO_kgDOBI2tpw` · P1 `…tqA` · P2 `…tqQ` · P3 `…tqg` |
| Estimate | `IFSS_kgDOApoUaw` | single-select | XS `IFSSO_kgDOBI3lIw` · S `…lIg` · M `IFSSO_kgDOBI2trQ` · L `…trA` · XL `…tqw` |
| **Claude Code Discussion** | `IFT_kgDOAqrjLw` | **text** | the `https://claude.ai/code/session_…` URL |

**Area — pick the right one.** `Execution` is the Unit-of-Work / transaction-commit layer
(`claude/overview/02-execution.md`). The tick loop, `DagScheduler` and system dispatch are `Runtime`
(`claude/overview/13-runtime.md`). External filers routinely pick `Execution` for scheduler work; reclassify and
say so in the reply.

**Claude Code Discussion — set it whenever a Claude Code session produced the issue or its analysis** (triage,
research, design, bug-hunt). Available on every Issue Type *except* `Task`. The value is the session URL from the
current conversation.

```bash
# Set any mix of fields in ONE mutation. textValue for text fields, singleSelectOptionId for single-selects.
MSYS_NO_PATHCONV=1 gh api graphql -f query='mutation{
  setIssueFieldValue(input:{
    issueId:"<I_…>",                                   # issue NODE id, from mcp__GitHub__get_issue .node_id
    issueFields:[
      {fieldId:"IFSS_kgDOApo0DQ", singleSelectOptionId:"IFSSO_kgDOBI3leQ"},
      {fieldId:"IFT_kgDOAqrjLw",  textValue:"https://claude.ai/code/session_…"}
    ]
  }){ issue{ number } }
}'
```

**Input keys** (`IssueFieldCreateOrUpdateInput`): `fieldId` (required) + one of `textValue`, `singleSelectOptionId`,
`dateValue`, `numberValue`, `multiSelectOptionIds`, or `delete:true` to clear.

**Read-back gotcha:** the value selector is `value` on **both** `IssueFieldTextValue` and
`IssueFieldSingleSelectValue`. `text`, `option` and `optionName` do not exist and fail document validation before
anything executes:

```bash
MSYS_NO_PATHCONV=1 gh api graphql -f query='{ repository(owner:"Log2n-io",name:"Typhon"){ issue(number:<N>){
  issueType{name}
  issueFieldValues(first:8){ nodes{
    ... on IssueFieldTextValue         { field{ ... on IssueFieldText         { name } } value }
    ... on IssueFieldSingleSelectValue { field{ ... on IssueFieldSingleSelect { name } } value }
  } } } } }'
```

Re-query IDs with `repository{ issueFields(first:40){ nodes{ ... on IssueFieldSingleSelect { id name options{id name} } ... on IssueFieldText { id name } } } }` if a mutation 404s — fields get added over time.

**Issue Type** is separate again — `gh api graphql updateIssue(input:{id,issueTypeId})`; the MCP `update_issue` tool has no `type` field. Epic `IT_kwDOEcGj5M4CEI4A` · Feature `IT_kwDOEcGj5M4CEIDt` · Task `IT_kwDOEcGj5M4CEIDr` · Bug `IT_kwDOEcGj5M4CEIDs` · Question `IT_kwDOEcGj5M4CFL6-`.
