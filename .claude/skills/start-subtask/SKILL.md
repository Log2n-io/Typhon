---
name: start-subtask
description: Start working on a sub-issue of an umbrella issue - updates status, validates dependencies, updates design doc
argument-hint: [sub-issue number]
---

# Start Working on a Sub-Issue (Subtask)

Begin work on a sub-issue within an umbrella issue workflow. This is the lightweight counterpart to `/start-task` -- it handles subtask activation without branch creation or design doc creation (those already exist from the umbrella).

**Typical workflow:**
```
/start-task #36          <- umbrella issue, creates branch
/start-subtask #37       <- this skill
... implement #37 ...
/complete-subtask #37    <- marks #37 done
/start-subtask #38       <- this skill
... implement #38 ...
/complete-subtask #38
... etc ...
/complete-task #36       <- closes umbrella, merges PR, cleans up
```

## Input

$ARGUMENTS should contain the sub-issue number (e.g., `37` or `#37`).

If no argument provided, detect the current umbrella (from the branch name or recent `/start-task`) and list its uncompleted sub-issues via `AskUserQuestion`.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/start-subtask [#N]

  Start working on a sub-issue of an umbrella issue.

Arguments:
  #N              Sub-issue number (e.g., 37 or #37)
  --help, -h      Show this help

What it does:
  1. Fetches sub-issue details
  2. Detects and validates parent (umbrella) issue
  3. Checks dependency ordering
  4. Updates project status to In Progress
  5. Updates design doc status (if exists)

Examples:
  /start-subtask #37
  /start-subtask 38
  /start-subtask
```

## Workflow

### 1. Fetch Sub-Issue Details

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

Confirm the issue is open (state = "open"). If already closed, report that it's already done and exit.

### 2. Detect Parent (Umbrella) Issue

From the sub-issue body (returned in step 1), search for a parent reference. Use the same detection logic as `/complete-subtask`:
- `Parent: #NN` or `Part of #NN`
- `**GitHub Issue:** #NN (umbrella)`
- Any `#NN` reference where NN is a different issue with sub-issue checkboxes

If multiple candidates or none found, ask:

```
Question: "Which issue is the parent/umbrella for #<number>?"
Header: "Parent"
Options:
  - #<candidate1> - <title> (if candidates found)
  - Enter manually (description: "I'll type the parent issue number")
```

### 3. Validate Parent Status

Fetch the parent issue and verify it's "In Progress":

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<parent_number>`

If the parent is **not** In Progress, warn:

```
Question: "Parent #<parent> is not In Progress. Are you sure you want to start this sub-issue?"
Header: "Status"
Options:
  - Proceed anyway (description: "Start the sub-issue regardless of parent status")
  - Cancel (description: "Don't start -- run /start-task on the parent first")
```

If "Cancel", stop and suggest running `/start-task <parent>` first.

### 4. Check Dependencies

From the parent issue body (already fetched in step 3), examine the sub-issue checklist for ordering clues.

Look at the checkbox list in the parent. If the sub-issue being started has **unchecked sub-issues listed above it** in the checklist, warn about potential dependency:

```
Question: "Sub-issues listed before #<number> in the parent are not yet complete: #<earlier1>, #<earlier2>. These might be dependencies. Proceed?"
Header: "Dependencies"
Options:
  - Proceed anyway (description: "I know the order -- this one is fine to start now")
  - Cancel (description: "Let me complete those first")
```

If all prior sub-issues are checked (or there are none before this one), skip silently.

### 5. Update Project Status to In Progress

**Project item lookup:** Read `.claude/skills/_helpers.md` Section 2 for the robust patterns.

```bash
# Step 1: Find the item ID by piping directly to Python (no temp files)
gh project item-list 1 --owner Log2n-io --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    if item.get('content', {}).get('number') == int(sys.argv[1]):
        print(item['id'])
        sys.exit(0)
print('NOT_FOUND')
" <sub_issue_number>

# Step 1b: If NOT_FOUND, add the sub-issue to the project board first
# gh project item-add 1 --owner Log2n-io --url https://github.com/Log2n-io/Typhon/issues/<sub_issue_number>
# Then re-run step 1

# Step 2: Update status to In Progress (using the item ID from step 1)
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <item_id> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A \
  --single-select-option-id 47fc9ee4  # "In Progress"
```

### 6. Update Design Doc Status (if exists)

Look for a design doc reference in the sub-issue body (links to `claude/design/`).

If found, read the design doc and update its status line:

```
# BEFORE:
**Status:** Draft

# AFTER:
**Status:** In progress
```

Only update the `**Status:**` line in the header area. Don't modify anything else.

If no design doc is found or referenced, skip this step silently.

### 7. Report Summary

```
Starting sub-issue #<number>: <title>

  Parent: #<parent> -- <parent_title>
  Project: Status -> In Progress
  Dependencies: All prior sub-issues complete / Warnings acknowledged
  Design: claude/design/<path> -> Status: In progress (or "no design doc")

Ready to implement!
```

## Edge Cases

### Sub-issue has no parent
If no parent can be detected and the user doesn't provide one:
- Still update the sub-issue project status to In Progress
- Skip dependency validation
- Report that no parent was found

### Sub-issue not on project board
If the project item lookup returns NOT_FOUND:
- **Add the sub-issue to the project board** with `gh project item-add 1 --owner Log2n-io --url <issue_url>`
- Re-fetch the project data and find the new item ID
- Then update its status to In Progress as normal
- Report that the sub-issue was added to the board

### Design doc not in expected format
If the design doc doesn't have a `**Status:**` line:
- Skip the update
- Report that the design doc format wasn't recognized

### No argument -- list sub-issues from parent

If no argument is provided, try to detect the current umbrella from the git branch name (e.g., `feature/36-error-foundation` -> #36). Fetch the parent issue body and list unchecked sub-issues:

```
Question: "Which sub-issue would you like to start?"
Header: "Sub-issue"
Options:
  - #37 - <title> (description: "Not started")
  - #38 - <title> (description: "Not started")
  - ... (up to 4, skip already-checked ones)
```

## Status Field Option IDs

For reference:
- Todo: `f75ad846`
- In Progress: `47fc9ee4`
- Done: `98236657`

## Field IDs

- Status: `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A`
- Project ID: `PVT_kwDOEcGj5M4Bb-8P`
