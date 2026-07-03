---
name: dev-status
description: Show current development status from GitHub Project
argument-hint: (no arguments)
---

# Show Typhon Development Status

Display the current state of work from the GitHub Project, including active items, upcoming work, and items needing attention.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/dev-status

  Show current development status from the GitHub Project board.

Arguments:
  --help, -h      Show this help

What it does:
  1. Shows In Progress items
  2. Lists Todo items (next to pick up)
  3. Flags items needing attention (stale, critical)
  4. Provides mountain view summary (totals by status)

Examples:
  /dev-status
```

## What to Display

### 1. Active Milestone / Epic

The legacy **Phase** field was retired in the org-board migration. Group active work by **Milestone** (release maturity carried on Features) or by parent **Epic** instead — read these per issue via `mcp__GitHub__get_issue` (`milestone`, `parent_issue_url`).

### 2. In Progress Items

Query items where Status = "In Progress". **Always pipe `gh project item-list` directly to Python** (see `.claude/skills/_helpers.md` Section 2):
```bash
gh project item-list 1 --owner Log2n-io --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    s = item.get('status', '')
    if s == 'In Progress':
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        e = item.get('estimate', '?')
        print(f'#{n} | {s} | {p} | {a} | {e} | {t}')
"
```

Parse the output to filter and format. For each In Progress item, show:
- Issue number and title
- Area (issue-level; may be blank on the board)
- Branch (if mentioned in issue body)
- Design Doc (if field is set)

### 3. Todo Items

Query items where Status = "Todo" - these are ready to be picked up next.

### 4. Items Needing Attention

Flag issues that:
- Have Status = "In Progress" but no recent activity (check issue last updated date)
- Have Status = "Todo" for more than 14 days
- Are P0-Critical and not In Progress

To check issue activity, use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

The returned object includes `updated_at` which can be used to determine staleness.

### 5. Mountain View Summary

Calculate totals:
- Count of items by Status (Todo / In Progress / Done)
- Breakdown by Area (issue-level field; read per issue when needed)

## Output Format

```
Typhon Development Status

Active Milestone: [name]  (or Epic #XX -- N/M sub-issues done)

In Progress (N):
   #XX Title [Area]
       Branch: feature/XX-name (if known)
       Design: path/to/design.md (if set)

Todo (N):
   #XX Title [Area] -- has design / needs design

Needs Attention:
   #XX reason (e.g., "no activity for N days", "P0 not started")

Mountain View:
   Todo: N | In Progress: N | Done: N
   By Area: <area> N, ...

Suggested: [Pick up #XX or continue #YY]
```

## Implementation

Use `gh project item-list 1 --owner Log2n-io --limit 200 --format json` piped to Python to get all project items, then filter and format the output.

For activity checks on individual issues, use `mcp__GitHub__get_issue` to get the `updated_at` field.
