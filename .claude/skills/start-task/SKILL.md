---
name: start-task
description: Start working on a GitHub issue - updates status, creates branch, verifies design
argument-hint: "[issue number or title]"
---

# Start Work on a GitHub Issue

Prepare to work on an issue by updating its status, creating a branch, and verifying prerequisites.

## Input

$ARGUMENTS may contain:
- An **issue number** (e.g., `42` or `#42`) -> proceed directly to the workflow
- A **title/text** (non-numeric) -> offer to create a new issue with that title
- **Nothing** (empty) -> show available issues to pick from, or offer to create one

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/start-task [#N | title]

  Start working on a GitHub issue — updates status, creates branch, verifies design.

Arguments:
  #N              Issue number (e.g., 42 or #42)
  title           Text — offers to create or search
  --help, -h      Show this help

What it does:
  1. Fetches issue details (or creates one inline)
  2. Checks for design doc
  3. Updates project status to In Progress
  4. Creates feature branch (or defers to Rider/manual)
  5. Reports summary

Examples:
  /start-task #42
  /start-task "Add caching layer"
  /start-task
```

## Handling No Issue Number

### Case 1: No arguments provided

Fetch Todo items from the project. **Always pipe `gh project item-list` directly to Python** (see `.claude/skills/_helpers.md` Section 2):

```bash
gh project item-list 1 --owner Log2n-io --limit 200 --format json 2>&1 | python3 -c "
import json, sys
items = json.load(sys.stdin)['items']
for item in items:
    s = item.get('status', '')
    if s == 'Todo':
        n = item.get('content', {}).get('number', '?')
        t = item.get('title', 'untitled')
        p = item.get('priority', '?')
        a = item.get('area', '?')
        print(f'#{n} | {s} | {p} | {a} | {t}')
"
```

Use the output to filter for items with Status = "Todo". Then use `AskUserQuestion` to present a choice:

**Question:** "Which issue would you like to start working on?"
**Header:** "Issue"
**Options** (up to 4, prioritize Todo items):
- `#<number> - <title>` (description: "[Status] [Priority] [Area]") -- for each candidate issue
- `Create a new issue` (description: "I'll help you create one right now")

If the user picks an existing issue, continue with the normal workflow below using that issue number.

If the user picks "Create a new issue", proceed to **Inline Issue Creation** below.

### Case 2: Non-numeric argument (looks like a title)

If $ARGUMENTS is not empty and not a number (doesn't match `^\d+$` after stripping `#`), use `AskUserQuestion`:

**Question:** "It looks like you provided a title instead of an issue number. Would you like to:"
**Header:** "Action"
**Options:**
- `Create a new issue with this title` (description: "I'll create '#$ARGUMENTS' and start work on it")
- `Search existing issues` (description: "Search for issues matching '$ARGUMENTS' to pick one")

If "Create a new issue": proceed to **Inline Issue Creation** with the title pre-filled.

If "Search existing issues": use `mcp__GitHub__search_issues` with q: `"repo:log2n-io/Typhon $ARGUMENTS"` and present matching issues via `AskUserQuestion`.

## Inline Issue Creation

When an issue needs to be created, do it inline rather than redirecting the user to `/create-issue`.

Follow the `/create-issue` skill workflow directly:

1. **Gather info** -- Use `AskUserQuestion` to collect:
   - Title (if not already provided from $ARGUMENTS)
   - Description (ask the user to describe what needs to be done)
   - Issue Type, labels, Area, Product, Milestone (use the same questions as `/create-issue`)

2. **Create the issue** -- Use `mcp__GitHub__create_issue` with:
   - owner: `"log2n-io"`
   - repo: `"Typhon"`
   - title: `"<title>"`
   - body: `"<description>"`
   - labels: `["<label1>", "<label2>"]`
   - assignees: `["nockawa"]`

3. **Add to project and set fields** -- Follow `/create-issue` steps 3-5

4. **Continue** -- Once the issue is created, continue with the normal `/start-task` workflow below using the new issue number.

This makes `/start-task` a one-stop command: describe what you want to work on, and Claude creates the issue + starts work in a single flow.

## Workflow

### 1. Fetch Issue Details

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

### 2. Check Design Doc

Look for design doc reference in the issue body (links to `claude/design/`).

If no design doc exists and this is an enhancement (not a bug fix):
- Ask: "This issue has no design doc. Should I create one, or proceed without?"
- If yes, create `claude/design/<IssueName>.md` using the design template
- Add a link to the design doc in the issue body under "Related Documents"
  - **IMPORTANT:** Use absolute URLs in issue bodies (see `.claude/skills/_helpers.md` rule #9):
    `[claude/design/<path>](https://github.com/Log2n-io/Typhon/blob/main/claude/design/<path>)`

### 3. Update Project Status

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
" <issue_number>

# Step 2: Update status field (using the item ID from step 1)
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <item_id> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A \
  --single-select-option-id 47fc9ee4  # "In Progress"
```

### 4. Branch Creation

**Base branch:** Always create feature branches from `main` (GitHub Flow).

Determine the recommended branch name based on issue type:
- Enhancement/Feature: `feature/<number>-short-name`
- Bug: `fix/<number>-short-name`

Then ask the user how they want the branch created:

**Question:** "How should the branch be created?"
**Header:** "Branch"
**Options:**
- `Claude creates it` (description: "I'll run git checkout -b <branch-name> from main")
- `Rider Open Task` (description: "I'll skip -- use Alt+Shift+N in Rider to create branch via Open Task for issue #<number>")
- `Skip branch` (description: "Don't create a branch yet, I'll handle it later")

**If "Claude creates it":**
```bash
# Ensure we're on main and up-to-date
git checkout main
git pull origin main
git checkout -b feature/<number>-short-name
```

Then create a matching branch in the `claude/` documentation repo:
```bash
cd claude
git checkout -b feature/<number>-short-name
cd ..
```

**If "Rider Open Task":**
Report the recommended branch name for reference but don't create it. The user will use Rider's `Tools > Tasks & Contexts > Open Task` (Alt+Shift+N) to select the issue and let Rider create the branch + context switch.
Also create the matching branch in the `claude/` documentation repo:
```bash
cd claude
git checkout -b feature/<number>-short-name
cd ..
```

**If "Skip branch":**
Just report the recommended name for later use.

### 5. Update Design Doc (if exists)

Add branch reference to the design doc:
```markdown
**GitHub Issue:** #<number>
**Branch:** `feature/<number>-short-name`
**Status:** In progress
```

Note: If the user chose "Rider Open Task" or "Skip branch", still write the recommended branch name in the design doc. Update it later if Rider uses a different name.

### 6. Report Summary

```
Starting work on #<number>: <title>

  Design doc: claude/design/<Name>.md (or "No design doc -- skipped")
  Status updated: <old> -> In Progress
  Branch: feature/<number>-short-name
   -> Created by Claude / Use Rider Open Task (Alt+Shift+N) / Skipped

Ready to implement!
```

## Status Field Option IDs

For reference:
- Todo: `f75ad846`
- In Progress: `47fc9ee4`
- Done: `98236657`

## Field Reference

### Project ID
- `PVT_kwDOEcGj5M4Bb-8P`

### Status Field
- Field ID: `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A`

> Priority/Estimate are unconfigured and Area/Product are issue-level on the org board — see `.claude/skills/_helpers.md` § Field Reference. These skills only set Status.
