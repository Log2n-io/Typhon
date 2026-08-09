---
name: new-worktree
description: Create / remove / inventory Typhon dev worktrees with the nested claude/ docs repo wired up correctly — one command instead of the three-step manual procedure that silently half-completes
argument-hint: <name> | <name> --remove | --list
---

# new-worktree — Typhon worktree lifecycle

Thin wrapper over `scripts/new-worktree.ps1`. The PowerShell script does the whole procedure in one shot —
fetch, branch, docs repo, verify, background pre-warm — and reports. **Do not** hand-run `git worktree add`
/ `git clone` from Bash; the point of this skill is that the steps can no longer be partially applied.

## Why this exists

Typhon is a multi-repo workspace: the docs are a separate clone of `log2n-io/typhon-claude` living at
`<repo>/claude`, **gitignored by the parent**. So `git worktree add` alone produces a worktree with no docs,
and on a documentation-first project every docs-first read then silently resolves against the main repo's
`claude/` — pinned to whatever branch *that* checkout sits on. You get last quarter's design docs against
this week's code, with nothing warning you. `--list` currently shows three existing worktrees in exactly
that state.

## Input

`$ARGUMENTS` first token is the worktree name. Recognized flags (map them onto the script's PowerShell
switches): `--list`, `--remove`, `--force`, `--no-build`, `--docs-clone`, `--branch <b>`, `--from <ref>`.
With no arguments, show the script's usage block.

## Behavior

Run the script via PowerShell and display its stdout verbatim:

```bash
pwsh -NoProfile -File ./scripts/new-worktree.ps1 <name> [options]
```

Then **`cd` both shells** into the new worktree — the script prints the exact two commands, because a child
process cannot change the parent's working directory:

- Bash: `cd <worktree>` 
- PowerShell: `Set-Location <worktree>`

Both tools persist their cwd independently, so both are required. Do **not** use `EnterWorktree`: it fails
deterministically here because its owner-resolution walks into the nested `claude/` repo and validates a
parent-owned path against *that* repo's worktree list.

Use worktree-absolute paths for builds regardless — the session root and auto-loaded `CLAUDE.md` stay
pointed at the main repo.

## What the script does that hand-running misses

| Step | Detail |
|------|--------|
| Anchors on the main repo | Resolves `--git-common-dir`, so it works when invoked from inside another worktree |
| Fetches first | Branches off freshly fetched `origin/main`, never the local `main` (checked out elsewhere, so git would refuse anyway) |
| `--no-track` | Without it git infers `upstream=origin/main`, making `git status` read "ahead of origin/main" and turning a reflexive `git pull` into a merge of main into your feature branch |
| Docs as a **worktree**, not a clone | Objects live in the shared `<repo>/claude` store, so unpushed doc commits survive deleting the folder. An independent clone is the only copy of its objects — verified: a force-removed docs commit was still readable from the main clone afterwards |
| Same branch in both repos | Code and docs move together |
| Verifies | Sentinel `claude/CLAUDE.md` present + both HEADs on the expected branch, else it fails loudly instead of leaving a half-built worktree |
| Pre-warms | Backgrounds a Debug build of the engine test project (a fresh worktree has no `obj/`/`bin/`, so the first build is fully cold). `--no-build` to skip — worth doing when other agents are running tests on this box |

## Removal

`--remove` refuses to proceed when either repo has uncommitted changes or commits absent from every
remote-tracking ref, listing exactly what would be lost; `--force` overrides. It removes the docs worktree
*before* the parent and prunes both worktree lists — `rm -rf` would leave a stale registration behind.
Branches are never deleted; it prints the `branch -D` command instead.

## Notes

- Default branch name is `feature/<name>`; override with `--branch`.
- Neither branch gets an upstream, so the first push is `git push -u origin <branch>` in each repo.
- `--docs-clone` forces the old independent-clone behavior if a worktree of the docs repo is unwanted.
- Every worktree that exists makes `/benchmark` fail from the **main** repo (BenchmarkDotNet duplicate-csproj
  discovery). Run benchmarks from inside a worktree.
