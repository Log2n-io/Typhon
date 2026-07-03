---
name: complete-task
description: Complete work on a GitHub issue - close issue, update artifacts, prompt for doc updates
argument-hint: [issue number]
---

# Complete Work on a GitHub Issue

Finalize work on an issue by closing it, updating project status, and ensuring all artifacts are properly maintained.

## Input

$ARGUMENTS should contain the issue number (e.g., "42" or "#42").

If no issue number provided, use `AskUserQuestion` to ask which issue to complete.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/complete-task [#N]

  Complete work on a GitHub issue — close issue, update project, prompt for doc updates.

Arguments:
  #N              Issue number (e.g., 42 or #42)
  --help, -h      Show this help

What it does:
  1. Verifies issue state and checks PR/branch status
  2. Closes the issue
  3. Updates project status to Done
  4. Handles design doc (keep / move to archive)
  5. Suggests overview doc updates and ADR creation
  6. Offers branch cleanup

Examples:
  /complete-task #42
  /complete-task 36
```

## Workflow

### 1. Verify Issue State

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

Confirm the issue is currently open and has been worked on.

### 2. Check PR and Branch State

**Step 2a: Check for existing PRs** (open or merged):

Use `mcp__GitHub__search_issues` with:
- q: `"repo:log2n-io/Typhon type:pr <number>"`

This returns PRs that mention the issue number.

**Step 2b: Detect unmerged feature branch** (if no merged PR found):

```bash
# Find the feature/fix branch for this issue
git branch --list "feature/<number>*" --list "fix/<number>*"

# If a branch exists, check if it has commits ahead of main
git rev-list --count main..<branch_name>
```

**Decision matrix:**

| PR State | Branch State | Action |
|----------|-------------|--------|
| Merged PR exists | — | Proceed normally (happy path) |
| Open PR exists | — | Warn: "PR should be merged first" -> ask to proceed or wait |
| No PR | Branch has commits ahead of main | Warn: "Branch has N unmerged commits" -> offer to create PR |
| No PR | No feature branch / branch is even with main | Proceed normally (work may have been committed directly to main) |

**If no PR and branch has unmerged commits**, ask:

```
Question: "Branch '<branch_name>' has N commits not yet merged to main. A PR should typically be created and merged before completing the task. What would you like to do?"
Header: "PR"
Options:
  - Create a PR now (Recommended) (description: "I'll create a PR from <branch_name> to main, then continue after it's merged")
  - Skip PR (description: "Proceed without a PR — I'll handle merging manually")
  - Cancel (description: "Stop — I'll create and merge the PR first, then re-run /complete-task")
```

**If "Create a PR now":**
- Push the branch if not already pushed: `git push -u origin <branch_name>`
- Create the PR:

Use `mcp__GitHub__create_pull_request` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- title: `"<summary derived from issue title>"`
- body: `"<summary derived from issue body>"`
- head: `"<branch_name>"`
- base: `"main"`

- Then check the `claude/` documentation repo for a matching branch with changes:

```bash
cd claude
# Check if a matching branch exists and has commits ahead of main
CLAUDE_BRANCH=$(git branch --list "<branch_name>" --format="%(refname:short)")
if [ -n "$CLAUDE_BRANCH" ]; then
  CLAUDE_AHEAD=$(git rev-list --count main.."$CLAUDE_BRANCH")
  echo "claude/ branch: $CLAUDE_BRANCH, commits ahead: $CLAUDE_AHEAD"
else
  echo "claude/ has no matching branch"
fi
cd ..
```

If the `claude/` repo has a matching branch with commits ahead of main, also create a PR for it:
- Push the branch: `cd claude && git push -u origin <branch_name> && cd ..`
- Create the PR:

Use `mcp__GitHub__create_pull_request` with:
- owner: `"log2n-io"`
- repo: `"Typhon-docs"` (or the actual `claude/` remote repo name — check with `cd claude && git remote get-url origin && cd ..`)
- title: `"Docs: <same summary as main PR>"`
- body: `"Documentation updates for log2n-io/Typhon#<issue_number>"`
- head: `"<branch_name>"`
- base: `"main"`

Report both PR URLs.

- **Stop here** — report the PR URL(s) and tell the user to merge them, then re-run `/complete-task`
- Do NOT proceed to close the issue or update status yet

**If "Skip PR":**
- Proceed with the rest of the workflow (close issue, update status, etc.)

**If "Cancel":**
- Stop and report that no changes were made

### 3. Close the Issue

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
" <issue_number>

# Step 2: Update status to Done (using the item ID from step 1)
gh project item-edit --project-id PVT_kwDOEcGj5M4Bb-8P --id <item_id> \
  --field-id PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A \
  --single-select-option-id 98236657  # "Done"
```

### 5. Check for Design Doc

Look for design doc in:
- Issue body (links to `claude/design/`)
- Project item's "Design Doc" field

If a design doc exists, ask the user:
```
Question: "The design doc 'claude/design/FeatureName.md' was used. What should happen to it?"
Header: "Design Doc"
Options:
  - Keep in design/ (Recommended) — still the authoritative spec; stays as living documentation for the feature
  - Move to archive/ — outdated, superseded, or no longer relevant
```

Note: we no longer move shipped design docs to `reference/` — design docs are the feature's living spec and stay in `design/` after implementation. Archive is reserved for superseded designs.

### 6. Suggest Overview Updates

Check if this issue might affect overview documentation:

Suggest potentially affected overview docs based on Area field:
- Database -> `claude/overview/02-execution.md`, `claude/overview/04-data.md`
- Transactions -> `claude/overview/02-execution.md`
- MVCC -> `claude/overview/04-data.md`
- Indexes -> `claude/overview/04-data.md`
- Schema -> `claude/overview/04-data.md`
- Storage -> `claude/overview/03-storage.md`
- Memory -> `claude/overview/08-resources.md`
- Concurrency -> `claude/overview/01-concurrency.md`
- Primitives -> `claude/overview/11-utilities.md`
- Telemetry -> `claude/overview/09-observability.md`

Ask:
```
Question: "These overview docs might need updates based on this work:"
  - claude/overview/XX-topic.md

"Would you like to review any of them?"
Options:
  - Yes, open them for review
  - Skip for now
```

### 7. Check for ADR Need

Ask:
```
Question: "Did this work involve a significant architectural decision that should be documented?"
Header: "ADR"
Options:
  - Yes, create an ADR (I'll help draft it)
  - No, no significant decisions
```

If yes, help create an ADR in `claude/adr/` following the template.

### 8. Clean Up Branch (Optional)

Check if a local feature/fix branch still exists for this issue:

```bash
git branch --list "feature/<number>*" --list "fix/<number>*"
```

If a branch exists **and** a merged PR was found in step 2 (meaning the code is safely on `main`):

```
Question: "Branch '<branch_name>' was merged via PR. Delete the local branch?"
Header: "Branch"
Options:
  - Yes, delete it (description: "git branch -d <branch_name>")
  - Keep it (description: "Leave the branch for now")
```

If "Yes":
- `git branch -d <branch_name>`
- Also clean up the `claude/` repo branch if it exists:
  ```bash
  cd claude && git checkout main && git branch -d <branch_name> 2>/dev/null; cd ..
  ```

If no merged PR was found (user chose "Skip PR" in step 2), do **not** offer to delete — the branch may contain the only copy of the work.

### 9. Report Summary

```
Completing #<number>: <title>

  Issue closed
  Status: In Progress -> Done
  Design doc: claude/design/<Name>.md
   -> Kept (or archived)

  Overview docs reviewed: (list or "skipped")
  ADR created: claude/adr/0XX-decision.md (or "none")
  Branch cleaned up: feature/<number>-name (or "kept")

Work complete!
```

## Status Field Option IDs

For reference:
- Todo: `f75ad846`
- In Progress: `47fc9ee4`
- Done: `98236657`

## Field IDs

- Status: `PVTSSF_lADOEcGj5M4Bb-8PzhWrH1A`
- Project ID: `PVT_kwDOEcGj5M4Bb-8P`
