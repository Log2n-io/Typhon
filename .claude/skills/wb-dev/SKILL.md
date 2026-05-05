---
name: wb-dev
description: Start / stop / status of the Typhon Workbench dev servers (Kestrel :5200 + Vite :5173) with file-based PID tracking so state survives across Claude Code sessions
argument-hint: start | stop | status | restart
---

# wb-dev — Workbench Dev Server Controller

Thin wrapper over `wb-dev.ps1` at the repo root. The PowerShell script does all the
work in one shot — pre-flight, launch, port-bind wait, state-file write, report.
The previous skill needed 7-9 Bash round-trips per action; this one does 1.

## Input

`$ARGUMENTS` first token determines the action; default is `start`. Pass `--help`,
`-h`, or `help` to display the script's help block.

Recognized actions: `start`, `stop`, `status`, `restart`, `help`.

## Behavior

Run the PowerShell script with the resolved action via Bash, then display its
stdout verbatim:

```bash
pwsh -NoProfile -Command './wb-dev.ps1 <action>'
```

(Fall back to `powershell -NoProfile -Command` if `pwsh` isn't on PATH.) Use
`-Command` (not `-File`) so the relative `./wb-dev.ps1` resolves against Bash's
working directory — `-File` on Windows pwsh requires an absolute path. The script
handles state-file reads/writes, port checks, PID tracking, log tailing on
failure, and stale-state warnings on its own. **Do not** run extra `netstat`,
`taskkill`, or state-file manipulation from Bash — the script covers everything
in a single PowerShell session, which is the whole point of consolidating the
workflow into one file.

## State / logs

- State JSON: `.claude/state/wb-dev.json` (gitignored)
- Logs: `.claude/state/wb-dev.kestrel{,err}.log`, `.claude/state/wb-dev.vite{,err}.log`

Shape of the state file (kept simple — no listener-PID tracking; `taskkill /T`
inside the script cascades from the recorded root PID to its descendants):

```json
{
  "kestrelPid": 1234,
  "vitePid":    5678,
  "startedAt":  "2026-05-05T...",
  "kestrelLog": "...",
  "viteLog":    "..."
}
```

## Endpoints (after start)

- Backend:  http://localhost:5200/health
- Frontend: http://localhost:5173
- OpenAPI:  http://localhost:5200/openapi.json

## When to use

- **Before running Playwright specs / hitting `/api/*` by hand / testing the dev UI** — run `/wb-dev status` first; if nothing is running, `/wb-dev start`. Don't launch `dotnet watch` or `npm run dev` directly; it leaves untracked processes that clash with future `/wb-dev` invocations.
- **When done** — `/wb-dev stop`. Leaving the servers running between sessions is fine (they hot-reload), but a stale Kestrel will lock `Typhon.Workbench.dll` and break any subsequent `dotnet build tools/Typhon.Workbench`.
