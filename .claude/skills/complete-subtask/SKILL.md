---
name: complete-subtask
description: Complete a sub-issue of an umbrella issue - close it, check parent checkbox, update design doc
argument-hint: [sub-issue number]
---

# Complete a Sub-Issue (Subtask)

Mark a sub-issue as done within an umbrella issue workflow. This is the lightweight counterpart to `/complete-task` — it handles subtask completion without branch cleanup, PR checks, or ADR prompts.

**Typical workflow:**
```
/start-task #36          <- umbrella issue, creates branch
... implement #37 ...
/complete-subtask #37    <- this skill
... implement #38 ...
/complete-subtask #38    <- this skill
... implement #39, #40 ...
/complete-subtask #39
/complete-subtask #40
/complete-task #36       <- closes umbrella, merges PR, cleans up
```

## Input

$ARGUMENTS should contain the sub-issue number (e.g., `37` or `#37`).

If no argument provided, use `AskUserQuestion` to ask which sub-issue to complete.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/complete-subtask [#N]

  Complete a sub-issue — close it, check parent checkbox, update project status.

Arguments:
  #N              Sub-issue number (e.g., 37 or #37)
  --help, -h      Show this help

What it does:
  1. Fetches sub-issue and detects parent issue
  2. Closes the sub-issue
  3. Updates project status to Done
  4. Checks the checkbox in the parent issue body
  5. Updates design doc status (if exists)

Examples:
  /complete-subtask #37
  /complete-subtask 38
```

## Workflow

### 1. Fetch Sub-Issue Details

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

Confirm the issue is open (state = "open"). If already closed, report and exit.

### 2. Detect Parent (Umbrella) Issue

From the sub-issue body (returned in step 1), search for a parent reference. Common patterns:
- `**GitHub Issue:** #NN (umbrella)` or `Sub-issues: #37, #38, ...`
- `Parent: #NN`
- `Part of #NN`
- Any `#NN` reference where NN is a different issue

Look for patterns like:
- A line containing "umbrella" with an issue number
- A line containing "parent" with an issue number
- A "Sub-issues:" or "Part of" reference

If multiple candidates are found, or none are found, ask:

```
Question: "Which issue is the parent/umbrella for #<number>?"
Header: "Parent"
Options:
  - #<candidate1> - <title> (if candidates found)
  - Enter manually (description: "I'll type the parent issue number")
```

### 3. Close the Sub-Issue

Use `mcp__GitHub__update_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`
- state: `"closed"`

### 4. Update Project Status to Done

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

# Step 2: Update status to Done (using the item ID from step 1)
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <item_id> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A \
  --single-select-option-id 98236657  # "Done"
```

### 5. Check Checkbox in Parent Issue

**Step 5a:** Fetch the parent issue to get its current body.

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<parent_number>`

**Step 5b:** In the returned body, find a line matching the pattern `- [ ]` that contains `#<sub_number>` (the sub-issue number). Replace `- [ ]` with `- [x]` on that line.

**Checkbox detection patterns** (match any):
- `- [ ] ... #37 ...` (explicit issue reference)
- `- [ ] ... #37: ...` (reference with colon)
- `- [ ] **#37** ...` (bold reference)
- `- [ ] ... Sub-issue #37 ...` (text reference)

If the checkbox line also contains a description, preserve it. Only change `[ ]` to `[x]`.

**Step 5c:** Update the parent issue body with the modified content.

Use `mcp__GitHub__update_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<parent_number>`
- body: `"<modified body with checkbox checked>"`

This replaces the old temp file + Python + `gh issue edit --body-file` pattern. The body is passed directly — no temp files, no encoding issues.

If no matching checkbox is found, report it but don't fail — the parent may use a different format.

### 6. Update Design Doc Status (if exists)

Look for a design doc reference in the sub-issue body (links to `claude/design/`).

If found, read the design doc and update its status line:

```
# BEFORE:
**Status:** Ready for implementation

# AFTER:
**Status:** Implemented
```

Only update the `**Status:**` line in the YAML-like header at the top of the doc. Don't modify anything else.

If no design doc is found or referenced, skip this step silently.

### 7. Report Summary

```
Completed sub-issue #<number>: <title>

  Issue: #<number> closed
  Project: Status -> Done
  Parent: #<parent> checkbox checked
  Design: claude/design/<path> -> Status: Implemented (or "no design doc")

Parent #<parent> progress: X/Y sub-issues complete
```

For the progress line, count checked vs total checkboxes in the parent body that reference sub-issues.

## Edge Cases

### Sub-issue has no parent
If no parent can be detected and the user doesn't provide one:
- Still close the sub-issue and update project status
- Skip checkbox update
- Report that no parent was found

### Parent has no checkboxes
If the parent issue body doesn't have checkboxes:
- Still close the sub-issue and update project status
- Report that no checkbox was found to check

### Design doc not in expected format
If the design doc doesn't have a `**Status:**` line:
- Skip the update
- Report that the design doc format wasn't recognized

## Status Field Option IDs

For reference:
- Todo: `f75ad846`
- In Progress: `47fc9ee4`
- Done: `98236657`

## Field IDs

- Status: `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A`
- Project ID: `PVT_kwDOEcGj5M4Bb-8P`
