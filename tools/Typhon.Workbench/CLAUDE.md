# Typhon Workbench

The Workbench is a local developer tool that opens a `.typhon` database (a directory) or attaches to a live engine and surfaces data browsing, schema inspection, query authoring,
profiling, and tracing in one professional-grade UI. Think **JetBrains DataGrip for Typhon** — not a profiler-with-extras.

## Stack

- **Host**: ASP.NET Core 10 (Kestrel) — minimal APIs, built-in OpenAPI
- **SPA**: React 19 + Vite + TypeScript strict + Tailwind v4 + shadcn (slate base, CSS variables)
- **State/data**: Zustand + TanStack Query, typed via Orval from `/openapi.json`
- **Docking/UI**: dockview for workspace panels, cmdk for command palette, Lucide for icons
- **Testing**: Vitest (JS), NUnit (.NET), Playwright deferred to Phase 3

## Ports

- Kestrel: `:5200`
- Vite dev server: `:5173` (proxies `/api`, `/openapi.json`, `/swagger`, `/health` to `:5200`)

## Project layout

```
tools/Typhon.Workbench/
├── Typhon.Workbench.csproj     # ASP.NET Core 10 web SDK, ProjectReferences to Engine + Profiler
├── Program.cs                  # Minimal Kestrel host — /health + /openapi.json
├── appsettings*.json
├── Properties/launchSettings.json
├── ClientApp/                  # React SPA (excluded from MSBuild via <Content Remove/> pattern)
│   ├── package.json
│   ├── vite.config.ts          # Proxy → :5200
│   ├── tsconfig.json           # Strict; @/* alias
│   ├── tailwind.config.ts      # CSS-variable tokens, Compact density, darkMode: 'class'
│   ├── components.json         # shadcn — slate base, CSS variables
│   ├── orval.config.ts         # Codegen from /openapi.json
│   ├── src/main.tsx            # createRoot + QueryClientProvider
│   ├── src/App.tsx             # "Hello Workbench" at Phase 0
│   └── src/globals.css         # Tailwind v4 @theme block
└── wwwroot/                    # Built client bundle (gitignored)
```

## Authority order for schema

When loading a `.typhon` database, the authority order is **binaries > database > Workbench**. If the loaded schema assembly doesn't match the file's recorded
schema hash, we report an incompatibility banner — we never silently trust Workbench's interpretation over what the engine or binary declared.

## Phase plan

Umbrella: [#253](https://github.com/nockawa/Typhon/issues/253). Full plan: [09-bootstrap-plan.md](https://github.com/nockawa/typhon-claude/blob/main/design/typhon-workbench/09-bootstrap-plan.md).

## Claude Code integration

Skills, MCP server registrations, and hooks for Workbench development live in the **repo root `.claude/`** (prefixed `wb-`). 
Nested `settings.json` is not loaded when the session is started from the repo root — only this `CLAUDE.md` is auto-loaded when Claude edits files under `tools/Typhon.Workbench/`.

### Running the webapp locally

**Always use `/wb-dev` to start / stop the dev servers.** It tracks both Kestrel (:5200) and Vite (:5173) PIDs in `.claude/state/wb-dev.json` so a later session (or `stop` invocation) can tear them down even if it didn't start them.

- **When you need the webapp running** — running Playwright specs (`e2e/*.spec.ts`), hitting `/api/*` by hand, exercising the dev UI, testing the Dev Fixture tab — run `/wb-dev status` first. If nothing is running, `/wb-dev start`. Do not launch `dotnet watch` or `npm run dev` directly; it leaves untracked processes that clash with future `/wb-dev` invocations.
- **When you're done** — `/wb-dev stop`. Leaving the servers running between sessions is fine (they hot-reload) but a stale Kestrel will lock `Typhon.Workbench.dll` and break any subsequent `dotnet build tools/Typhon.Workbench`.
- **Playwright prerequisites** — Playwright tests expect `:5173` reachable with the Vite proxy forwarding to `:5200`. `/wb-dev status` confirms both are LISTENING before you invoke `npx playwright test`.

## Threat model (localhost-only tool)

The Workbench is a single-user local dev tool. It binds Kestrel to `127.0.0.1` only — never a routable interface. Two defenses layer on top:

1. **Bootstrap token (`X-Workbench-Token`)** — 256-bit random hex generated at startup and written to `%LOCALAPPDATA%\Typhon\Workbench\bootstrap.token` (or `$XDG_DATA_HOME/typhon/workbench/` on POSIX). Required on every MVC API endpoint (`/api/sessions/*`, `/api/fs/*`, `/api/sessions/{id}/resources/*`). The token reaches the SPA one of two ways depending on how the UI is served:
   - **Vite dev (`/wb-dev`, `:5173`):** the dev proxy reads the token file at each request and injects the header server-side; the browser never sees it.
   - **`typhon ui` (Kestrel serves the SPA, no proxy — #429):** the host opens the browser at `…/#wbtoken=<token>` and the SPA moves the token from the URL **fragment** into `sessionStorage`, then strips the fragment. This is the Jupyter-notebook handoff: the fragment is never sent to the server or written to request logs, `sessionStorage` and the fragment are origin-scoped (a page at another origin can't read them), and the custom `X-Workbench-Token` header can't be forged cross-origin (no permissive CORS), so the CSRF protection is intact. The one delta from dev is that the launched tab now holds the token — an approved trade-off for a first-party single-user local tool (an SPA XSS could exfiltrate it; there is no untrusted content in the SPA).

   Either way, a malicious webpage can issue `fetch('http://127.0.0.1:5200/api/...')` but cannot read the token (file on disk in dev; another origin's `sessionStorage` under `typhon ui`) and cannot attach the custom header cross-origin, so every request 401s.
2. **Session token (`X-Session-Token`)** — each Open/Attach/Trace session has its own random Guid. The `RequireSession` filter enforces token-to-route-id match so session A's token cannot address session B.

SSE endpoints (`/heartbeat`, `/resources/stream`) cannot use custom headers (`EventSource` limitation) so they're gated by the URL sessionId only. That's safe because no attacker can obtain a sessionId without first going through the bootstrap-token-gated `POST /api/sessions/*`.

`/health` is intentionally ungated so liveness checks work from any local tool.

**Do not weaken any of this to support a remote-access mode.** Remote attach is a separate deployment with real auth — not a relaxation of this tool's posture.

## What's still deferred

- No production CORS config; the server intentionally binds only to localhost dev ports. Any new
  API surface must document its threat model before shipping a non-local build.
- No auth beyond `X-Session-Token` header + URL matching (see `RequireSessionAttribute`). Session
  creation (`POST /api/sessions/*`) is unauthenticated — fine for local dev, not for anything else.
- No production logging / telemetry pipeline yet.
