---
name: start-design
description: Start design for a GitHub issue - creates design doc from research/ideas, updates status to Todo
argument-hint: [issue number or title] [--deep]
---

# Start Design for a GitHub Issue

Transition an issue into the Design phase by creating a design document, seeded from existing research (or ideas), and updating the project board status to "Todo".

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
/start-design [#N | title] [--deep]

  Start design for a GitHub issue — creates design doc from research/ideas, updates status to Todo.

Arguments:
  #N              Issue number (e.g., 42 or #42)
  title           Text — offers to create or search
  --deep          Create directory structure instead of single file
  --help, -h      Show this help

What it does:
  1. Fetches issue (or creates one inline)
  2. Checks for research/ideas documents to seed from
  3. Creates design doc (single file or deep directory)
  4. Handles source docs (conclude/archive/leave)
  5. Updates project status to Todo

Examples:
  /start-design #42
  /start-design "Error handling overhaul" --deep
  /start-design
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

Use the output to filter for items with Status = "Todo". Use `AskUserQuestion` to present a choice:

**Question:** "Which issue would you like to start designing?"
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
- `Create a new issue with this title` (description: "I'll create '$ARGUMENTS' and start design on it")
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

4. **Continue** -- Once the issue is created, continue with the normal `/start-design` workflow below using the new issue number.

## Workflow

### 1. Fetch Issue Details

Use `mcp__GitHub__get_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`

### 2. Status Guard

Check the issue's current project status. If the issue is already **past** "Todo" (i.e., status is "In Progress" or "Done"), warn the user:

**Question:** "Issue #<number> is already at '<current status>'. Starting design would move it back to 'Todo'. Proceed?"
**Header:** "Status"
**Options:**
- `Proceed anyway` (description: "Move status back to Todo and create the design doc")
- `Cancel` (description: "Don't change anything")

If "Cancel", stop and report that no changes were made.

### 3. Check for Existing Source Documents

#### 3a. Research documents

List all files under `claude/research/`:

```
Glob: claude/research/**/*.md
```

If any research documents exist, present them to the user via `AskUserQuestion`:

**Question:** "I found these research documents. Which ones (if any) should feed into this design?"
**Header:** "Research"
**Options** (up to 4, pick the most likely related ones based on name/path similarity to the issue title):
- `claude/research/<path>` (description: first line or title from the file) -- for each candidate
- `None` (description: "Don't use any research docs")
**MultiSelect:** true

If the user selects one or more research docs, read their full content for use in step 5.

#### 3b. Ideas documents (if no research was selected)

If the user selected "None" for research docs (or no research docs exist), also check `claude/ideas/`:

```
Glob: claude/ideas/**/*.md
```

If any ideas documents exist, present them the same way:

**Question:** "No research docs selected. I found these ideas documents. Use any as input?"
**Header:** "Ideas"
**Options** (up to 4):
- `claude/ideas/<path>` (description: first line or title from the file) -- for each candidate
- `None -- start fresh` (description: "Create the design doc from scratch using only the issue context")
**MultiSelect:** true

If the user selects ideas docs, read their full content for use in step 5.

### 4. Determine Design Doc Location

**First, try to infer the category** from the selected source document(s):
- If one or more research/ideas docs were selected, use their parent directory path as the category.
  - Example: research doc at `claude/research/database-engine/QuerySystem.md` -> category = `database-engine/`
  - Example: research doc at `claude/research/timeout/README.md` -> category = root level, doc name derived from issue
  - If multiple source docs are from different categories, use the most specific common ancestor.

**If no category can be inferred** (no source docs selected, or docs are at root level with no clear category), ask the user:

List existing directories under `claude/design/` and present:

**Question:** "Where should this design doc go?"
**Header:** "Location"
**Options** (up to 4):
- `design/ (root level)` (description: "No category, just a file at the top level")
- `design/<existing-category>/` (description: "Existing category") -- for each existing subdirectory
- `Other` (description: "Specify a custom path / create new category")
**MultiSelect:** false

### 5. Create Design Document

Derive the **document name** from the issue title, using PascalCase (e.g., issue "Add spatial indexing support" -> `SpatialIndexingSupport`).

When source documents were selected, read their content to **seed** specific sections. The design doc is a scaffold -- pre-fill what can be derived, leave the actual design work to the user.

#### Standard Mode (no `--deep`)

Create a single file: `claude/design/<category>/<Name>.md`

```markdown
# <Issue Title> Design

**Date:** <today YYYY-MM-DD>
**Status:** Draft
**GitHub Issue:** #<number>
**Branch:** --

## Summary

<If a research doc was used and has a Recommendation section, synthesize a 2-3 sentence summary from it. If an ideas doc was used, derive from its "The Idea" section. If starting fresh, derive from the issue body.>

## Goals

<If a research doc was used, extract goals from its Recommendation/Conclusion. If an ideas doc was used, extract from "Why This Might Matter". Otherwise, derive from the issue body. Format as bullet list.>
- Goal 1
- Goal 2

## Non-Goals

- [Explicitly out of scope -- to be filled by user]

## Design

### Overview

[High-level description with diagram if helpful]

### Data Structures

[Key types, schemas, storage]

### API / Interface

[Public API, method signatures]

### Implementation Details

[Key algorithms, edge cases, error handling]

## Testing Strategy

- [ ] Unit tests for X
- [ ] Integration tests for Y

## Open Questions

<If source docs had unresolved questions, migrate relevant ones here. Otherwise leave placeholders.>
- [ ] [Open question]

## References

- GitHub Issue: #<number>
<If research doc was used:>
- Research: `claude/research/<path>`
<If ideas doc was used:>
- Ideas: `claude/ideas/<path>`
<If issue body has links:>
- [Extracted references]
```

#### Deep Mode (`--deep`)

Create a directory: `claude/design/<category>/<Name>/`

**README.md** (entry point):

```markdown
# <Issue Title> Design

<One-line description derived from summary>

**Date:** <today YYYY-MM-DD>
**Status:** Draft
**GitHub Issue:** #<number>
**Branch:** --

## Summary

<Same as standard mode -- synthesized from source docs or issue body.>

## Goals

<Same as standard mode.>

## Non-Goals

- [Explicitly out of scope -- to be filled by user]

## Document Series

| Part | Title | Focus |
|------|-------|-------|
| [01](./01-overview.md) | **Overview & Data Structures** | High-level design, key types |

## Open Questions

<Migrated from source docs or placeholders.>
- [ ] [Open question]

## References

- GitHub Issue: #<number>
<If research doc was used:>
- Research: `claude/research/<path>`
<If ideas doc was used:>
- Ideas: `claude/ideas/<path>`
```

**01-overview.md** (first part):

```markdown
# Overview & Data Structures

## Design Overview

[High-level description with diagram if helpful]

## Data Structures

[Key types, schemas, storage]

## API / Interface

[Public API, method signatures]

## Implementation Details

[Key algorithms, edge cases, error handling]

## Testing Strategy

- [ ] Unit tests for X
- [ ] Integration tests for Y
```

Additional numbered parts can be added later with "add new part <title>".

### 6. Handle Source Documents

#### Research documents

If one or more research documents were selected in step 3a, ask **for each one**:

**Question:** "The research doc `claude/research/<path>` was used. What should happen to it?"
**Header:** "Research doc"
**Options:**
- `Mark as concluded (Recommended)` (description: "Keep in research/, set status to 'Moved to design', add cross-ref to the design doc")
- `Archive it` (description: "Move to claude/archive/")
- `Leave as-is` (description: "Don't change the research doc at all")

**If "Mark as concluded":**
- Update the research doc's front matter: set `**Status:**` to `Moved to design`
- Add (or update) a reference at the bottom:
  ```markdown
  - **Design doc:** `claude/design/<path>` (started <today>)
  ```

**If "Archive it":**
- Move the file (or directory) to `claude/archive/` preserving its name
- Add a note at the top: `> **Archived:** Promoted to design -- see claude/design/<path>`

**If "Leave as-is":**
- Do nothing.

#### Ideas documents

If one or more ideas documents were selected in step 3b, ask **for each one** (same options as `/start-research`):

**Question:** "The ideas doc `claude/ideas/<path>` was used. What should happen to it?"
**Header:** "Ideas doc"
**Options:**
- `Archive it` (description: "Move to claude/archive/ -- the content lives on in the design doc")
- `Keep and cross-reference` (description: "Leave in ideas/ but add a link pointing to the new design doc")
- `Leave as-is` (description: "Don't change the ideas doc at all")

**If "Archive it":**
- Move the file (or directory) to `claude/archive/` preserving its name
- Add a note at the top: `> **Archived:** Promoted to design -- see claude/design/<path>`

**If "Keep and cross-reference":**
- Add to the ideas doc under `## Related`:
  ```markdown
  - **Design doc:** `claude/design/<path>` (started <today>)
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

#### Link Design Doc in Issue Body

Fetch the current issue body, append a "Related Documents" section (if not already present), or add to the existing one.

**Step 1:** The issue body was already fetched in step 1 via `mcp__GitHub__get_issue`.

**Step 2:** Modify the body to add the design doc link.

**IMPORTANT:** Always use absolute URLs in issue bodies -- relative paths break when viewed outside the repo (e.g., on the project board). See `.claude/skills/_helpers.md` rule #9.

**Step 3:** Update the issue body:

Use `mcp__GitHub__update_issue` with:
- owner: `"log2n-io"`
- repo: `"Typhon"`
- issue_number: `<number>`
- body: `"<updated body with design doc link>"`

Example addition to append:
```markdown
- Design: [`claude/design/<path>`](https://github.com/Log2n-io/Typhon/blob/main/claude/design/<path>)
```

If the issue body already has a "Related Documents" section, append the design doc link to it instead of creating a new section. Preserve any existing links (e.g., a research doc link added by `/start-research`).

### 8. Report Summary

```
Starting design for #<number>: <title>

  Design doc: claude/design/<path>
   -> Mode: Standard / Deep (directory with README.md + 01-overview.md)
  Status updated: <old> -> Todo
  Research used: claude/research/<path> -> Concluded / Archived / Left as-is
   (or "None")
  Ideas used: claude/ideas/<path> -> Archived / Cross-referenced / Left as-is
   (or "None -- started fresh")

Ready to refine the design!
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
