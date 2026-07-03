---
name: start-research
description: Start research on a GitHub issue - creates research doc, links ideas, updates status
argument-hint: [issue number or title] [--deep]
---

# Start Research on a GitHub Issue

Transition an issue into the Research phase by creating a research document, optionally seeded from existing ideas, and updating the project board.

## Input

$ARGUMENTS may contain:
- An **issue number** (e.g., `42` or `#42`) -> proceed directly to the workflow
- A **title/text** (non-numeric) -> offer to create a new issue with that title
- **Nothing** (empty) -> show available issues to pick from, or offer to create one
- The **`--deep`** flag (anywhere in arguments) -> create a directory structure instead of a single file

Extract `--deep` from arguments first, then process the remainder as issue number or title.

## Help

If `$ARGUMENTS` contains `--help` or `-h`, display the following and **stop** — do not execute the workflow.

```
/start-research [#N | title] [--deep]

  Start research on a GitHub issue — creates research doc, links ideas, updates status.

Arguments:
  #N              Issue number (e.g., 42 or #42)
  title           Text — offers to create or search
  --deep          Create directory structure instead of single file
  --help, -h      Show this help

What it does:
  1. Fetches issue (or creates one inline)
  2. Checks for existing ideas documents to seed from
  3. Creates research doc (single file or deep directory)
  4. Handles source ideas docs (archive/cross-ref/leave)
  5. Updates project status to Todo

Examples:
  /start-research #42
  /start-research "Query optimization" --deep
  /start-research
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

**Question:** "Which issue would you like to start research on?"
**Header:** "Issue"
**Options** (up to 4, prioritize Todo items):
- `#<number> - <title>` (description: "[Status] [Priority] [Area]") -- for each candidate issue
- `Create a new issue` (description: "I'll help you create one right now")

If the user picks an existing issue, continue with the normal workflow below using that issue number.

If the user picks "Create a new issue", proceed to **Inline Issue Creation** below.

### Case 2: Non-numeric argument (looks like a title)

If $ARGUMENTS (after removing `--deep`) is not empty and not a number (doesn't match `^\d+$` after stripping `#`), use `AskUserQuestion`:

**Question:** "It looks like you provided a title instead of an issue number. Would you like to:"
**Header:** "Action"
**Options:**
- `Create a new issue with this title` (description: "I'll create '$ARGUMENTS' and start research on it")
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

4. **Continue** -- Once the issue is created, continue with the normal `/start-research` workflow below using the new issue number.

## Workflow

### 1. Fetch Issue Details

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

### 2. Status Guard

Check the issue's current project status. If the issue is already **past** "Todo" (i.e., status is "In Progress" or "Done"), warn the user:

**Question:** "Issue #<number> is already at '<current status>'. Starting research would move it back to 'Todo'. Proceed?"
**Header:** "Status"
**Options:**
- `Proceed anyway` (description: "Move status back to Todo and create the research doc")
- `Cancel` (description: "Don't change anything")

If "Cancel", stop and report that no changes were made.

### 3. Check for Existing Ideas Documents

List all files under `claude/ideas/`:

```
Glob: claude/ideas/**/*.md
```

If any ideas documents exist, present them to the user via `AskUserQuestion`:

**Question:** "I found these ideas documents. Which ones (if any) should feed into this research?"
**Header:** "Ideas"
**Options** (up to 4, pick the most likely related ones based on name/path similarity to the issue title):
- `claude/ideas/<path>` (description: first line or title from the file) -- for each candidate
- `None -- start fresh` (description: "Create the research doc from scratch using only the issue context")
**MultiSelect:** true

If the user selects one or more ideas docs, read their full content for use in step 5.

If no ideas documents exist at all, skip this step silently.

### 4. Determine Research Doc Location

**First, try to infer the category** from the selected ideas document(s):
- If one or more ideas docs were selected, use their parent directory path as the category.
  - Example: ideas doc at `claude/ideas/database-engine/QueryCaching.md` -> category = `database-engine/`
  - Example: ideas doc at `claude/ideas/async-uow/README.md` -> category = root level, doc name = `async-uow`
  - If multiple ideas docs are from different categories, use the most specific common ancestor.

**If no category can be inferred** (no ideas docs selected, or docs are at root level with no clear category), ask the user:

List existing directories under `claude/research/` and present:

**Question:** "Where should this research doc go?"
**Header:** "Location"
**Options** (up to 4):
- `research/ (root level)` (description: "No category, just a file at the top level")
- `research/<existing-category>/` (description: "Existing category") -- for each existing subdirectory
- `Other` (description: "Specify a custom path / create new category")
**MultiSelect:** false

### 5. Create Research Document

Derive the **document name** from the issue title, using PascalCase (e.g., issue "Add spatial indexing support" -> `SpatialIndexingSupport`).

#### Standard Mode (no `--deep`)

Create a single file: `claude/research/<category>/<Name>.md`

Use the **research template** from `claude/README.md`, pre-filling:

```markdown
# <Issue Title>

**Date:** <today YYYY-MM-DD>
**Status:** In progress
**GitHub Issue:** #<number>
**Outcome:** [To be determined]

## Context

<Pre-fill from the issue body. If an ideas doc was selected, also incorporate its core idea and motivation here.>

## Questions to Answer

<If the ideas doc had "Open Questions", migrate them here. Otherwise, extract implicit questions from the issue body. If neither provides questions, leave placeholder items:>
1. [Question derived from issue context]
2. [Question derived from issue context]

## Analysis

### Option A: [Name]

**Description:** [How it works]

**Pros:**
-

**Cons:**
-

### Option B: [Name]

[Same structure]

## Recommendation

[To be filled after analysis]

## Next Steps

- [ ] [Action items to be determined]

## References

- GitHub Issue: #<number>
<If ideas doc was used:>
- Source idea: `claude/ideas/<path>`
<If issue body has links:>
- [Extracted references]
```

#### Deep Mode (`--deep`)

Create a directory: `claude/research/<category>/<Name>/`

**README.md** (entry point):

```markdown
# <Issue Title>

<One-line description derived from issue body>

**Date:** <today YYYY-MM-DD>
**Status:** In progress
**GitHub Issue:** #<number>
**Outcome:** [To be determined]

## Overview

<2-3 paragraphs from issue body and ideas doc context, explaining scope and purpose of this research.>

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-context-and-questions.md) | **Context & Questions** | Problem statement, questions to answer |

## Key Insights

[To be filled as research progresses]

## References

- GitHub Issue: #<number>
<If ideas doc was used:>
- Source idea: `claude/ideas/<path>`
```

**01-context-and-questions.md** (first part):

```markdown
# Context & Questions

## Context

<Pre-fill from the issue body and ideas doc, same as standard mode.>

## Questions to Answer

1. [Question derived from issue context]
2. [Question derived from issue context]

## Initial Thoughts

<If ideas doc had analysis or exploration, bring key points here. Otherwise leave empty.>
```

Additional numbered parts can be added later with "add new part <title>".

### 6. Handle Source Ideas Documents

If one or more ideas documents were selected in step 3, ask **for each one**:

**Question:** "The ideas doc `claude/ideas/<path>` was used. What should happen to it?"
**Header:** "Ideas doc"
**Options:**
- `Archive it` (description: "Move to claude/archive/ -- the content lives on in the research doc")
- `Keep and cross-reference` (description: "Leave in ideas/ but add a link pointing to the new research doc")
- `Leave as-is` (description: "Don't change the ideas doc at all")

**If "Archive it":**
- Move the file (or directory) to `claude/archive/` preserving its name
- Add a note at the top: `> **Archived:** Promoted to research -- see claude/research/<path>`

**If "Keep and cross-reference":**
- Add to the ideas doc under `## Related`:
  ```markdown
  - **Research doc:** `claude/research/<path>` (started <today>)
  ```

**If "Leave as-is":**
- Do nothing.

### 7. Update GitHub Issue

#### Update Project Status to Todo

Get the project item ID and update Status to "Todo".

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
  --single-select-option-id f75ad846  # "Todo"
```

#### Link Research Doc in Issue Body

Fetch the current issue body, append a "Related Documents" section (if not already present), or add to the existing one.

**Step 1:** The issue body was already fetched in step 1 via `mcp__GitHub__get_issue`.

**Step 2:** Modify the body to add the research doc link.

**IMPORTANT:** Always use absolute URLs in issue bodies -- relative paths break when viewed outside the repo (e.g., on the project board). See `.claude/skills/_helpers.md` rule #9.

**Step 3:** Update the issue body:

Use `mcp__GitHub__update_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`
- body: `"<updated body with research doc link>"`

Example addition to append:
```markdown

## Related Documents

- Research: [`claude/research/<path>`](https://github.com/Log2n-io/Typhon/blob/main/claude/research/<path>)
```

If the issue body already has a "Related Documents" section, append the research doc link to it instead of creating a new section.

### 8. Report Summary

```
Starting research on #<number>: <title>

  Research doc: claude/research/<path>
   -> Mode: Standard / Deep (directory with README.md + 01-context-and-questions.md)
  Status updated: <old> -> Todo
  Ideas used: claude/ideas/<path> -> Archived / Cross-referenced / Left as-is
   (or "None -- started fresh")

Ready to research!
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
