# Contributing to Typhon

**Date:** 2026-01-25
**Status:** Implemented
**Author:** Claude Code + nockawa

---

## Summary

This document proposes a unified development workflow for Typhon that integrates:

- **Planning & Tracking**: GitHub Projects as the single source of truth for work status
- **Knowledge Base**: The `claude/` documentation ecosystem as living architecture memory
- **Development Loop**: An explicit lifecycle from ideation → shipping
- **Automation**: Claude Code as an active assistant enforcing consistency and reducing friction
- **Tooling**: Rider + CLI + GitHub as the developer backbone

The goal is to create a system where:
1. Nothing falls through the cracks
2. Context is always recoverable (even after months away)
3. The "next thing to work on" is always clear
4. Documentation stays synchronized with reality

> 💡 **TL;DR — Just want to get started?** Skip to the [Developer's Daily Guide](#developers-daily-guide) for practical step-by-step workflows, and [Rider IDE Setup](#rider-ide-setup) for one-time configuration.

---

## Table of Contents

1. [The Big Picture](#the-big-picture)
2. [Branch Strategy](#branch-strategy)
3. [Lifecycle Stages](#lifecycle-stages)
4. [Artifact Relationships](#artifact-relationships)
5. [Workflow Rituals](#workflow-rituals)
6. [Claude Code Integration Points](#claude-code-integration-points)
7. [Automation Specifications](#automation-specifications)
8. [Views & Dashboards](#views--dashboards)
9. [Developer's Daily Guide](#developers-daily-guide)
10. [Implementation Plan](#implementation-plan)

---

## The Big Picture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           TYPHON DEVELOPMENT SYSTEM                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────┐     ┌─────────────┐     ┌─────────────┐     ┌───────────┐  │
│  │   IDEAS     │────►│  RESEARCH   │────►│   DESIGN    │────►│   CODE    │  │
│  │  (claude/)  │     │  (claude/)  │     │  (claude/)  │     │  (src/)   │  │
│  └─────────────┘     └─────────────┘     └─────────────┘     └───────────┘  │
│        │                   │                   │                   │        │
│        ▼                   ▼                   ▼                   ▼        │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                    GITHUB PROJECT (Source of Truth)                 │    │
│  ┌─────────┐        ┌──────────────┐        ┌─────────┐                │    │
│  │  Todo   │───────►│ In Progress  │───────►│  Done   │                │    │
│  └─────────┘        └──────────────┘        └─────────┘                │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│        │                                                       │            │
│        ▼                                                       ▼            │
│  ┌─────────────┐                                       ┌─────────────────┐  │
│  │  ARCHIVE    │◄──────────────────────────────────────│    REFERENCE    │  │
│  │  (claude/)  │      Completed items documented       │    (claude/)    │  │
│  └─────────────┘                                       └─────────────────┘  │
│                                                                             │
│  ┌──────────────────────────────────────────────────────────────────────┐   │
│  │  RIDER: Tasks, branches, context switching, shelving, navigation     │   │
│  ├──────────────────────────────────────────────────────────────────────┤   │
│  │  CLAUDE CODE: Watches, prompts, automates, correlates, enforces      │   │
│  └──────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

> 📌 **The Status field is now 3-state** (`Todo` / `In Progress` / `Done`), simplified from an earlier 6-state field (`Backlog`/`Research`/`Ready`/`In Progress`/`Review`/`Done`) as part of the org migration to `log2n-io`. Ideation, research, and design all sit at `Todo` — what stage a `Todo` item is actually at is tracked by which `claude/` doc (if any) it links to, not by a distinct Status value. The Lifecycle Stages section below reflects this.

### Core Principles

1. **GitHub Project = Source of Truth** — Where is this work right now? What's the roadmap?
2. **claude/ docs = Knowledge** — What do we know about this problem/solution?
3. **Issues = Discussion** — Comments, decisions, blockers for specific work items
4. **Rider = Developer Cockpit** — Branch management, context switching, issue navigation
5. **Claude Code = Glue** — Keeps everything synchronized and prompts for missing pieces

---

## Branch Strategy

Typhon uses **GitHub Flow** — a simple, effective branching model:

```
main (default) ◄── feature/18-accesscontrol
                ◄── feature/42-query-engine
                ◄── fix/55-page-cache-bug

Tags: v0.1.0, v0.2.0 (mark release points)
```

### Branch Types

| Branch | Purpose | Naming | Lifespan |
|--------|---------|--------|----------|
| `main` | Default branch, always deployable | — | Permanent |
| `feature/*` | New functionality | `feature/<issue>-short-name` | Days to weeks |
| `fix/*` | Bug fixes | `fix/<issue>-short-name` | Days |
| `hotfix/*` | Emergency fixes (rare) | `hotfix/<issue>-description` | Hours |

### Workflow

1. **Create feature branch** from `main`: `git checkout -b feature/42-query-engine`
2. **Work on the branch**: Commit frequently with issue references (`#42: Add parser`)
3. **Open PR** to `main` when ready
4. **Merge PR** (squash or merge commit)
5. **Delete feature branch** after merge

### Releases

Releases are simply **tags on `main`**:

```bash
# When ready for a release:
git tag -a v0.1.0 -m "Telemetry foundation release"
git push origin v0.1.0
gh release create v0.1.0 --title "v0.1.0 - Telemetry Foundation" --generate-notes
```

**Versioning (pre-1.0):**
- Breaking changes: bump MINOR (`0.1.0` → `0.2.0`)
- Non-breaking changes: bump PATCH (`0.1.0` → `0.1.1`)

### Linking to Documentation

When linking to `claude/` docs in GitHub issues, use the `main` branch:

```markdown
[Design Doc](https://github.com/log2n-io/Typhon/blob/main/claude/design/FeatureName.md)
```

---

## Lifecycle Stages

### Stage 1: Ideation

**Trigger:** "I wonder if we should..." / "What about..." / Random thought

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Idea doc | `claude/ideas/[category]/Name.md` | Date, Status, The Idea, Why It Matters |
| GitHub Issue | Optional (if worth tracking) | Label: `idea`, Status: `Todo` |

**Claude's Role:**
- Prompt: "Should I capture this as an idea doc?"
- Create with template
- Ask: "Worth adding to GitHub for tracking, or just parking it?"

**Example Claude Code Prompts:**
```
"I have an idea for spatial indexing support, capture it"
"Create an idea doc about query caching in the database-engine category"
"/create-issue Add support for spatial indexing"  ← if worth tracking immediately
```

### Stage 2: Research

**Trigger:** Idea needs exploration / Multiple approaches exist / Unknown territory

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Research doc | `claude/research/[category]/Name.md` | Context, Questions, Options, Recommendation |
| GitHub Issue | Required | Label: `research`, Status: `Todo` |
| Linked ADRs | `claude/adr/` | Created when key decisions are made |

**Claude's Role:**
- Prompt: "This needs research before we can design. Create research doc?"
- Link issue ↔ research doc bidirectionally
- When research concludes: "Ready to promote to design?"

**Example Claude Code Prompts:**
```
"Research the best approach for write-ahead logging in our persistence layer"
"Create a research doc comparing B+Tree vs LSM-Tree for our index layer"
"/create-issue Research WAL implementation strategies"  ← with label: research
"Promote the WAL research to a design doc, the approach is decided"
```

### Stage 3: Design

**Trigger:** Research complete / Approach chosen / Ready to plan implementation

| Artifact | Location | Required Fields |
|----------|----------|-----------------|
| Design doc | `claude/design/[category]/Name.md` | Summary, Goals, Non-Goals, Design, Testing Strategy |
| GitHub Issue | Required | Label: `enhancement`/`bug`, Status: `Todo` |
| Branch | `feature/xxx` or `fix/xxx` | Created when work begins |

**Claude's Role:**
- Generate design doc from research conclusions
- Validate design covers: data structures, API, edge cases, tests
- Prompt: "Design looks complete. Create branch and start implementation?"

**Example Claude Code Prompts:**
```
"Create a design doc for the WAL system based on the research conclusions"
"Review the design for QueryEngine — does it cover edge cases and testing?"
"/create-issue Implement WAL system"  ← with design doc linked
"/start-task 42"  ← when ready to begin implementation
```

### Stage 4: Implementation

**Trigger:** Design approved / Ready to code

| Artifact | Location | Status |
|----------|----------|--------|
| GitHub Issue | — | Status: `In Progress` |
| Branch | Active | Commits reference issue |
| Design doc | Updated | Status: `In progress`, Branch noted |

**Claude's Role:**
- Track progress against design doc checklist
- Remind about missing tests
- Prompt when stuck: "Blocked? Should we update the issue?"

**Example Claude Code Prompts:**
```
"/start-task 42"  ← updates status, creates branch, checks design doc
"/dev-status"  ← see what's currently in progress
"What's left to do for #42 according to the design doc?"
"I'm blocked on #42, update the issue with a note about the concurrency problem"
```

### Stage 5: Completion

**Trigger:** PR merged / Feature complete

| Artifact | Action |
|----------|--------|
| GitHub Issue | Closed (auto or manual) |
| Design doc | Stays in `design/`; move to `archive/` only if superseded |
| GitHub Project | Status → Done |
| overview/ | Updated if architectural impact |
| ADR | Created if significant decision was made |

**Claude's Role:**
- Prompt: "Feature complete! Let me update the project and archive the design doc."
- Check: "Should we update any overview/ docs?"
- Check: "Any architectural decisions worth an ADR?"

**Example Claude Code Prompts:**
```
"/complete-task 42"  ← closes issue, archives design, updates docs
"What got done this week, and what's stale?"  ← /weekly-review was never implemented; ask Claude directly
"Create an ADR for the decision to use circular buffers for revision chains"
```

---

## Artifact Relationships

```
                    ┌─────────────────┐
                    │ GitHub Project  │
                    │  (Tracking)     │
                    └────────┬────────┘
                             │ references
                             ▼
┌──────────────┐    ┌─────────────────┐    ┌──────────────┐
│  ideas/      │───►│  GitHub Issue   │◄───│  design/     │
│              │    │  (Work Item)    │    │              │
└──────────────┘    └────────┬────────┘    └──────────────┘
                             │                     ▲
                             │ linked to           │ based on
                             ▼                     │
                    ┌─────────────────┐    ┌──────────────┐
                    │    Branch       │    │  research/   │
                    │    (Code)       │    │              │
                    └────────┬────────┘    └──────────────┘
                             │
                             │ produces
                             ▼
                    ┌─────────────────┐
                    │      PR         │
                    │                 │
                    └────────┬────────┘
                             │
                      updates│
                             ▼
                    ┌──────────────────┐
                    │   overview/      │
                    │   (Living)       │
                    └──────────────────┘
```

### Linking Conventions

**In GitHub Issues:**
```markdown
## Related Documents
- Design: [QueryEngine](../claude/design/QueryEngine.md)
- Research: [QuerySystem](../claude/research/QuerySystem.md)
- ADR: [ADR-025](../claude/adr/025-query-execution-model.md)
```

**In Design Docs:**
```markdown
**GitHub Issue:** #42
**Branch:** `feature/query-engine`
```

**In GitHub Project:**
Design docs are linked in the issue body's "Related Documents" section (not as a project field, since text fields aren't clickable).

---

## Workflow Rituals

### Daily Start (5 min)

1. **Check GitHub Project board** — What's In Progress?
2. **Ask Claude:** "What am I working on?" (Claude reads GitHub Project)
3. **Review any blockers** — Update issue status if needed

### Before Starting New Work

1. **Check if design exists** — Is there a `design/` doc?
2. **Check if research is complete** — Any open questions?
3. **Update issue status** → `In Progress`
4. **Create branch** with proper naming

**Two approaches — pick your preferred flow:**

| | Claude-First | Rider-First |
|---|---|---|
| Step 1 | `/start-task #42` — Claude verifies design, updates status, suggests branch | `Alt+Shift+N` in Rider — Open Task creates branch + switches context |
| Step 2 | Claude creates branch or you use Rider | `/start-task #42` — Claude verifies design, updates project status |
| Best for | New issues, need design check | Quick context switch, branch already planned |

Both approaches end in the same state: issue In Progress, branch created, Rider context loaded.

### After Completing Work

1. **Rider:** `Alt+Shift+W` (Close Task) — commits remaining changes, optionally merges branch
2. **Update issue** with summary of what was done
3. **Ask Claude:** `/complete-task #42`
   - Claude updates design doc status (or archives if superseded)
   - Claude updates GitHub Project status → Done
   - Claude checks for overview/ updates
   - Claude offers branch cleanup

### Weekly Review (30 min)

1. **Review GitHub Project** — Status fields accurate? Stale items? Anything stuck?
2. **Check `ideas/`** — Anything to promote or archive?
3. **Check `research/`** — Any stale research?
4. **Ask Claude** to walk the project board and `ideas/`/`research/` for stale items — `/weekly-review` (below) was never implemented as a dedicated skill

---

## Claude Code Integration Points

### Skill: `/dev-status` — Where Are We?

```markdown
---
name: status
description: Show current development status across all systems
---

Reads and correlates:
- GitHub Project (In Progress items, Phase, Priority)
- Recent git activity

Output:
- Current focus area
- Active work items with links
- Any stale items (no activity > 7 days)
- Suggested next actions
```

### Skill: `/create-issue` — Create a New Work Item

```markdown
---
name: create-issue
description: Create a GitHub issue and add it to the Typhon dev project
argument-hint: [title] or leave empty for interactive mode
---

Actions:
1. Gather info interactively (title, description, type labels, area, priority, phase, estimate)
2. Create GitHub issue assigned to nockawa
3. Add issue to "Typhon dev" project (#7)
4. Set all project fields (Status, Priority, Phase, Area, Estimate)
5. Report summary with issue link and field values
```

### Skill: `/start-task` — Begin a Work Item

```markdown
---
name: start-task
description: Start working on a GitHub issue
argument-hint: [issue number or title]
---

Actions:
1. If no argument: list Ready/Backlog issues to pick from, or offer to create a new one
2. If non-numeric argument: offer to create issue or search existing ones
3. Verify design doc exists (prompt to create if missing)
4. Update issue status → In Progress
5. Create branch if needed (feature/XX-name or fix/XX-name)
6. Update design doc with branch name
7. Report readiness
```

### Skill: `/start-subtask` — Begin a Sub-Issue

```markdown
---
name: start-subtask
description: Start working on a sub-issue of an umbrella issue
argument-hint: [sub-issue number]
---

Actions:
1. Verify parent umbrella is In Progress
2. Validate dependencies (warn if prior sub-issues not done)
3. Update project status → In Progress
4. Update design doc status → In progress
5. Report readiness
```

### Skill: `/complete-subtask` — Finish a Sub-Issue

```markdown
---
name: complete-subtask
description: Complete a sub-issue of an umbrella issue
argument-hint: [sub-issue number]
---

Actions:
1. Close the sub-issue
2. Update project status → Done
3. Check the sub-issue's checkbox in parent issue body
4. Update design doc status → Implemented
5. Report progress (X/Y sub-issues complete)
```

**Typical umbrella workflow:**
```
/start-task #36          ← umbrella, creates branch
/start-subtask #37       ← activate sub-issue
... implement ...
/complete-subtask #37    ← close sub-issue, check checkbox
/start-subtask #38       ← activate next sub-issue
... implement ...
/complete-subtask #38
/complete-task #36       ← close umbrella, merge PR
```

### Skill: `/complete-task` — Finish a Work Item

```markdown
---
name: complete-task
description: Mark work complete and update all artifacts
argument-hint: [issue number]
---

Actions:
1. Update issue status → Done (or close)
2. Design doc stays in design/ (archive only if superseded)
3. Update GitHub Project status → Done
4. Check for overview/ updates needed
5. Prompt for ADR if significant decision
6. Report summary
```

### Hook: Post-Commit Analysis

```yaml
# .claude/hooks/post-commit.yaml
trigger: after git commit
actions:
  - Check if commit references an issue
  - Warn if large change without design doc
  - Suggest updating relevant docs
```

### Hook: Session Start

```yaml
# .claude/hooks/session-start.yaml
trigger: claude code session start
actions:
  - Show current phase from GitHub Project
  - List In Progress items
  - Note any stale work (>7 days no activity)
```

---

## Automation Specifications

### GitHub Actions: Project Sync

```yaml
# .github/workflows/project-sync.yml
name: Sync Project Status

on:
  issues:
    types: [opened, closed, labeled]
  pull_request:
    types: [opened, closed, merged]

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - name: Add new issues to project
        if: github.event_name == 'issues' && github.event.action == 'opened'
        run: gh project item-add 1 --owner Log2n-io --url ${{ github.event.issue.html_url }}

      - name: Move to Done on close
        if: github.event_name == 'issues' && github.event.action == 'closed'
        run: |
          # Update project item status to Done

      - name: Move to In Progress on PR
        if: github.event_name == 'pull_request' && github.event.action == 'opened'
        run: |
          # Find linked issue, update status
```

### GitHub Actions: Doc Link Validator

```yaml
# .github/workflows/doc-links.yml
name: Validate Doc Links

on:
  push:
    paths: ['claude/**/*.md']

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - name: Check internal links
        run: |
          # Validate all markdown links resolve
          # Check issue references exist
          # Warn on orphaned docs
```

---

## Views & Dashboards

### GitHub Project Views

| View | Layout | Configuration |
|------|--------|---------------|
| **Workflow Board** | Board | Group by Status |
| **Roadmap** | Roadmap | Date field: Target |
| **By Area** | Table | Group by Area, filter: `-status:Done` |
| **By Priority** | Table | Group by Priority, filter: `-status:Done` |
| **By Phase** | Table | Group by Phase |

### Custom Fields for Project

| Field | Type | Values | Purpose |
|-------|------|--------|---------|
| Status | Single-select | Todo, In Progress, Done | Workflow stage |
| Area | Single-select | Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives, Observability, Execution, Errors, Workbench | Subsystem |
| Estimate | Single-select | XS, S, M, L, XL | Effort sizing |
| Priority | Single-select | P0, P1, P2, P3 | Urgency |
| Target | Date | Target dates | Roadmap positioning |

---

## Rider IDE Setup

Rider's built-in Task Management connects directly to GitHub Issues and provides branch creation, context switching, and issue navigation — all tightly integrated with the Typhon workflow.

### Task Server Configuration

**Settings → Tools → Tasks → Servers → Add → GitHub**

| Setting | Value |
|---------|-------|
| Server URL | `https://github.com/log2n-io/Typhon` |
| API Token | Personal access token with `repo` scope |
| Search query | `assignee:nockawa state:open` |

### Task Name & Branch Templates

**Settings → Tools → Tasks:**

| Setting | Value | Example Output |
|---------|-------|----------------|
| Changelist name format | `${id} ${summary}` | `42 Query engine foundation` |
| Feature branch name format | `feature/${id}-${summary}` | `feature/42-query-engine-foundation` |
| ☑ Lowercased | checked | (auto-lowercases and replaces spaces with hyphens) |

The branch format matches the convention used by `/start-task` (e.g., `feature/42-query-engine`).

For bug-fix issues, manually adjust to `fix/${id}-${summary}` when prompted, or set up separate task types if desired.

### Commit Message Template

**Settings → Tools → Tasks → Servers → (your GitHub server) → Commit Message tab:**

Enable the commit message checkbox and set the template to:

```
#${id}: ${summary}
```

This produces commit messages like `#42: Query engine foundation`, automatically linking commits to GitHub issues. The template is populated when you close a task or commit while a task is active.

### Issue Navigation Pattern

**Settings → Tools → Tasks → Issue Navigation → Add:**

| Setting | Value |
|---------|-------|
| Pattern | `#(\d+)` |
| Link URL | `https://github.com/log2n-io/Typhon/issues/$1` |

This makes `#42` references in code comments, commit messages, and changelogs clickable — jumping straight to the GitHub issue.

### Key Shortcuts

| Action | Shortcut | What It Does |
|--------|----------|-------------|
| Open Task | `Alt+Shift+N` | Pick a GitHub issue → creates branch, shelves current changes, clears editor tabs |
| Close Task | `Alt+Shift+W` | Commits changes, optionally merges branch, restores previous context |
| Switch Task | `Alt+Shift+N` | Swap to a different open task (Rider restores that task's editor state) |
| Task Combo | Top-right toolbar | Quick-switch between recent tasks |

### Context Switching

When you **Open Task**, Rider automatically:
- Shelves uncommitted changes from the current branch
- Creates the new branch (using the template above)
- Clears editor tabs and restores the new task's saved context (tabs, bookmarks, breakpoints)

When you **Close Task**, Rider reverses this:
- Commits or shelves remaining changes
- Optionally merges/deletes the branch
- Restores the previous task's editor state

This means you can jump between issues mid-day without losing your place — each task has its own editor "workspace."

---

### Workbench Dev Server (`wb-dev.ps1`)

A repo-root PowerShell script (`wb-dev.ps1`) starts and stops the Workbench dev stack — Kestrel on `:5200` and Vite on `:5173` — in a single shot. Tracks PIDs in `.claude/state/wb-dev.json` so a follow-up session can stop them even if it didn't start them.

```powershell
pwsh -NoProfile -Command './wb-dev.ps1 start'    # boot both servers, wait for ports
pwsh -NoProfile -Command './wb-dev.ps1 status'   # report whether each is listening
pwsh -NoProfile -Command './wb-dev.ps1 stop'     # tear them down
```

Equivalent to running `dotnet watch` + `npm run dev` by hand, but tracks the processes properly so a stale Kestrel doesn't lock `Typhon.Workbench.dll` and break the next build. Inside Claude Code, use the `/wb-dev` skill — same script, no manual invocation.

---

## Developer's Daily Guide

### Morning Routine

```
1. Open Rider (workspace loads last project)
   - Check the Task combo (top-right) — Rider remembers your last active task
   - If resuming yesterday's work, your editor tabs/bookmarks are already restored
2. Terminal: `claude` (starts session)
3. Claude automatically shows:
   - "Good morning! Current phase: Telemetry"
   - "You have 2 items In Progress: #42, #45"
   - "⚠️ #42 has no activity for 5 days"
4. Pick up where you left off or ask "what should I focus on?"
   - To switch tasks mid-day: Alt+Shift+N in Rider (shelves current work, loads new context)
```

### Starting New Feature

```
Developer: "I want to work on query caching"

Claude: "I found these related items:
  - Idea: ideas/database-engine/QueryCaching.md (created Dec 2024)
  - No research doc yet
  - No GitHub issue

  This seems like it needs research first. Should I:
  1. Create a research doc and GitHub issue?
  2. Just capture more thoughts in the idea doc?
  3. Something else?"

Developer: "Create research and issue"

Claude: [Creates research/database-engine/QueryCaching.md]
        [Creates GitHub issue #50 with label:research]
        [Links them together]
        [Adds to Project in Research column]

"Done! Research doc and issue #50 created.
 The research doc has starter questions based on the idea.
 Want me to help explore any specific aspect first?"

Developer: "Let's start implementing"

Claude: [Runs /start-task #50]
        [Updates status → In Progress, suggests branch]

"Branch: feature/50-query-caching
 → Use Alt+Shift+N in Rider to Open Task for #50
   (creates branch + switches editor context)"

Developer: [Alt+Shift+N in Rider → selects #50 → branch created, tabs cleared]
```

### Completing Feature

```
Developer: "Feature for #42 is done, PR merged"

Claude: "Excellent! Let me wrap this up:

  ✅ Issue #42 closed
  ✅ design/QueryEngine.md kept in place (implementation complete)
  ✅ Project status → Done

  Quick questions:
  1. Should I update any overview/ docs? This touched the query layer.
  2. Any architectural decisions worth an ADR?

  [If yes to 1]: Which sections? (shows relevant overview docs)
  [If yes to 2]: What was the key decision?"
```

---

## Decisions Made

These questions were resolved during implementation:

1. **Iterations/Sprints:** → **Continuous flow** (not sprints)
2. **Milestones:** → **Phases** via project field (Telemetry, Query, WAL, Reliability, Infrastructure)
3. **Sub-issues:** → **Yes**, use sub-issues under Phase parent issues
4. **Notifications:** → Deferred for now (`/weekly-review` was never implemented — stale-item detection is manual today)

---

## Implementation Summary

The following has been implemented:

### GitHub Project Fields
- Status: Todo → In Progress → Done
- Priority: P0-Critical, P1-High, P2-Medium, P3-Low
- Phase: Telemetry, Query, WAL, Reliability, Infrastructure
- Area: Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives, Observability, Execution, Errors, Workbench
- Estimate: XS, S, M, L, XL
- Target: (date field for Roadmap view)

### Claude Code Skills
- `/dev-status` — Show current development status
- `/start-research #XX` — Start research on an issue
- `/start-design #XX` — Start design for an issue
- `/start-task #XX` — Begin work on an issue
- `/start-subtask #XX` — Start a sub-issue (update status, validate dependencies, update design doc)
- `/complete-subtask #XX` — Complete a sub-issue (close, check parent checkbox, update design doc)
- `/complete-task #XX` — Finish work, update artifacts
- `/create-issue` — Create new issue with all fields
- `/implement-feature` — Implement a GitHub issue end-to-end (plan → build → test → review)
- `/new-panel` — Scaffold a new Workbench panel
- `/benchmark` — Benchmark regression tracking
- `/coverage` — Code coverage analysis and trend reports
- `/profile` — Profile a benchmark workload with dotTrace
- `/wb-dev` — Start/stop/status the Workbench dev servers

### Rider Configuration
- **Task Server:** GitHub → `log2n-io/Typhon`, filter: `assignee:nockawa state:open`
- **Changelist name:** `${id} ${summary}`
- **Branch template:** `feature/${id}-${summary}` (lowercased)
- **Commit message:** `#${id}: ${summary}` (per-server → Commit Message tab)
- **Issue navigation:** `#(\d+)` → `https://github.com/log2n-io/Typhon/issues/$1`

### GitHub Actions
- `.github/workflows/project-sync.yml` — Auto-add issues, sync status on close/merge

---

## References

- [claude/CLAUDE.md](claude/CLAUDE.md) — Document lifecycle system
- [.claude/skills/](.claude/skills/) — Claude Code skills
- [GitHub Project](https://github.com/orgs/Log2n-io/projects/1) — Source of truth for work tracking
- [GitHub Projects Best Practices](https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/best-practices-for-projects)
