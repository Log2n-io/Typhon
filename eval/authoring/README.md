# Typhon authoring eval

Measures what a coding agent writes when it builds against Typhon **as an external user**: the `Typhon` NuGet package, its
XML docs, and the published `llms.txt` / `llms-full.txt`. No repository access. Issue: #1649 (part of #1583).

Each task targets an authoring mistake that Typhon either does not report or reports late (see the rules/ sweep behind
#1650–#1663). Every check is deterministic: it builds the agent's project, drives it through a fixed contract
(`public static class Solution`), and asserts observable behaviour, declared access, or a Roslyn syntax property.

| Task | Subject | What a wrong solution looks like |
|---|---|---|
| T01 | Model + persistence | missing registration, Transient data lost on reopen, string field truncation |
| T02 | Indexed query | `Where(Func)` full scan instead of `WhereField` on an indexed field |
| T03 | Unique key under concurrency | duplicate accounts, exception leaking from a unique violation |
| T04 | Two dependent systems | undeclared read, consumer not ordered after producer (sees last tick's value) |
| T05 | Hot parallel write | movement lost on reopen, undeclared read of `Velocity` (DV-01). Not covered: span writes without `MarkDirty` survive a clean close (the checkpoint writes the page) and are lost only on a crash, which no public API can stage deterministically |
| T06 | Writes from request threads | SingleVersion written off-tick instead of routed through a system |
| T07 | Schema evolution | no revision bump, new field not initialised for existing rows |
| T08 | Async request handler | transaction held across `await`, lost updates under write conflicts |
| T09 | Spatial query | moves never fenced in host mode: query finds entities at their old position |
| T10 | Replicated system | content write without `Replicate`: clients never see it |

## Layout

- `preamble.md` — common instructions prepended to every `TASK.md`.
- `tasks/Txx/TASK.md` — the task; `tasks/Txx/starter/` — files copied into the workspace (given model, marked DO NOT MODIFY).
- `template/` — `Solution.csproj` and `nuget.config` (package from a local feed, isolated packages folder).
- `checker/` — NUnit project; `Checks/Txx.cs` is compiled alone against the agent's project.
- `seed/` — T07's v1 database writer (built from `tasks/T07/starter/Model.cs`, run in its own process).
- `reference/Txx/` — reference solutions; `run.py validate` must pass all checks with them.
- `results/` — one file per run.

## Protocol

```bash
python3 eval/authoring/run.py pack     --feed <dir> --version 0.0.0-eval.N          # pack Typhon + Typhon.Protocol
python3 eval/authoring/run.py validate --feed <dir> --version ... --work <dir>     # reference solutions must pass
python3 eval/authoring/run.py prepare  --feed <dir> --version ... --docs <dir> --work <dir>
#   ... one fresh agent per task, workspace = <work>/Txx, prompt = "read TASK.md and complete it" ...
python3 eval/authoring/run.py check    --feed <dir> --version ... --work <dir> --out results/<name>.md
```

Workspaces live **outside** the repository so the agent cannot read engine source by accident. The model, the docs
snapshot and the package version are recorded in each result file; a later run is comparable only if the model is the same.
Each check was validated both ways: the reference solution passes it, and a variant carrying the targeted mistake fails it
(`reference/`; the trap variants are described in the result file of the validation run). Checks run with `Typhon:Checks:Enabled` and `Typhon:Checks:DeclaredAccess` on, so the engine's own opt-in checks count
against the solution too.
