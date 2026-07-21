# Manual test — the onboarding loop (local build only)

Exercise `typhon new` → `dotnet run` → `typhon ui`, using **this repo's** freshly-built CLI and engine — never the
published `Typhon.Cli` tool or the `Typhon` NuGet package (which is pre-#514 and won't compile the scaffold).

Commands are shown for **PowerShell**; bash equivalents in parentheses. Run everything from the worktree root unless
noted:

```
C:\Dev\github\Typhon\.claude\worktrees\onboarding-samples
```

---

## 0. One-time setup — build the CLI, pack the engine

```powershell
# a) PUBLISH the local `typhon` CLI  → C:\temp\typhon-cli\typhon.dll
#    Use `publish`, NOT `dotnet build`: publish bundles the Workbench SPA (wwwroot) next to the dll, which
#    `typhon ui` serves. A plain build leaves wwwroot out, so `typhon ui` would 404 on `/`.
dotnet publish src\Typhon.Shell\Typhon.Shell.csproj -c Debug -o C:\temp\typhon-cli

# b) Pack THIS repo's engine into a local NuGet feed
mkdir C:\temp\typhon-feed -Force            # (bash: mkdir -p /c/temp/typhon-feed)
dotnet pack src\Typhon.Engine\Typhon.Engine.csproj -c Release -o C:\temp\typhon-feed
```

The pack prints e.g. `Typhon.0.0.1-alpha.3.6.nupkg` — that's the local engine the scaffolded app will use.

Make the local CLI easy to call:

```powershell
# PowerShell
function typhon { dotnet "C:\temp\typhon-cli\typhon.dll" @args }
```
```bash
# bash
typhon() { dotnet "C:/temp/typhon-cli/typhon.dll" "$@"; }
```

Sanity check — this must be the repo build, not the installed alpha tool:

```
typhon ui --help        # should list --trace and --open-latest
```

---

## 1. Scaffold a project

```powershell
cd C:\temp
typhon new MyApp
cd MyApp
```

You get: `Harvester.cs`, `Systems.cs`, `Program.cs`, `typhon.telemetry.json`, `.gitignore`, `MyApp.csproj`, `README.md`.

---

## 2. Point the project at the LOCAL engine

The scaffold pins the *published* `Typhon` version; redirect it to your local pack.

**a)** Create `MyApp\nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="C:\temp\typhon-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

**b)** In `MyApp.csproj`, change the Typhon reference to float to the local pack:

```xml
<PackageReference Include="Typhon" Version="*-*" />
```

> `*-*` picks the highest version across the sources — your local `0.0.1-alpha.3.6` wins over the published `0.0.1-alpha.3`.
> (Or hard-pin the exact version from the `.nupkg` filename.)

---

## 3. Run it — the whole loop in one command

```powershell
dotnet run
```

Expect the ch.1–5 walkthrough, then:

```
OK — ran end to end; profiler trace written: ...\MyApp\captures\guide.typhon-trace (~238 KB)
```

Confirm the trace exists:

```
dir captures        # (bash: ls -l captures)
```

✅ **That's the headline check: `typhon new` → `dotnet run` wrote a `.typhon-trace` with zero code edits.**

---

## 4. Open the Workbench — the two things it does

The Workbench does two distinct jobs, and the scaffold produced the inputs for **both** in one `dotnet run`: a
profiler **trace** (`captures\guide.typhon-trace`) *and* a live **database** (`swg-guide.typhon`). Run these from
inside `MyApp` so the CWD-relative discovery works.

> A session opens **one or the other — a trace or a database, not both** — so these are two separate launches
> (`--open-db` + `--open-latest` together is rejected). Stop one with Ctrl-C before starting the other.

### 4a. Watch a profile — `--open-latest`

```
typhon ui --open-latest
```

Opens the Workbench on the **Profiler** view with the newest `captures\*.typhon-trace` already loaded — the tick
timeline, per-system spans (Spawn / Roam / FootprintSync / Harvest), memory & WAL tracks, Call Tree, top spans.

### 4b. Investigate the database — `--open-db`

```
typhon ui --open-db
```

Opens the Workbench on the **Schema/Data** view with the `.typhon` database in the current directory already
attached. You'll see the `Harvester` archetype and its components (`Position, Footprint, Cargo, Drift, Extractor`),
the per-component tables (`ComponentTable_Swg.*`), and the engine internals (Storage, WAL, Epoch, allocators).

> ℹ️ **Schema resolution.** A `.typhon` database records *which assembly* defined its schema. `--open-db`
> auto-discovers your app's built assembly (`bin\…\MyApp.dll`) so the archetypes/entities resolve — you'll see a
> `Using schema assembly: …` line at startup. If the app isn't built (no `bin`), the db still opens but shows only
> engine internals with a *"Schema incompatible"* banner; build it (`dotnet build`) or point at the DLL explicitly:
> `typhon ui --open-db --schema bin\Debug\net10.0\MyApp.dll`.

> ℹ️ **Stale-cache note (once).** If you tested an earlier build and the UI shows *"Missing or invalid
> X-Workbench-Token"*, your browser cached that build's `index.html`. Do **one** hard reload (**Ctrl-Shift-R**) to
> flush it — the server now sends `index.html` as `no-cache`, so later republishes are picked up automatically.

Common variants:

```
typhon ui                                        # just the welcome screen (open anything via the Connect dialog)
typhon ui --trace captures\guide.typhon-trace    # a specific trace file
typhon ui path\to\other.typhon                   # a specific database (positional)
typhon ui --no-browser                           # don't launch a browser; print the tokenized URL to open yourself
```

Stop it with Ctrl-C.

---

## 5. Change *what* gets profiled — then watch the trace change

The scaffold ships a `typhon.telemetry.json`; the `typhon telemetry` verbs edit it for you (no hand-editing nested
JSON). This closes the loop: **edit the config → re-run → see the capture change in the Workbench.**

**a) See what's captured now** — a tri-state tree (`✓` on · `✗` off · `-` inherited):

```
typhon telemetry list
```

Under `Profiler` you'll see `CpuSampling`, `Scheduler`, `Gauges` (on), and `MemoryAllocations`, `GcTracing` (off).

**b) Turn a channel off** — drop CPU sampling:

```
typhon telemetry disable CpuSampling
```

(Open `typhon.telemetry.json` — `CpuSampling` is now `"Enabled": false`.)

**c) Re-run and compare** — the trace is materially smaller because the CPU-sample payload is gone:

```
dotnet run
dir captures        # (bash: ls -l captures) — guide.typhon-trace drops from ~238 KB to ~160 KB
```

**d) See it in the Workbench:**

```
typhon ui --open-latest
```

Open the **Call Tree** panel — with CPU sampling off, the *on-CPU* frames are empty (the trace carries **0** CPU
frames vs **179** with sampling on). Turn it back on and re-run to get them back:

```
typhon telemetry enable CpuSampling
dotnet run
```

> ✅ **That's the point of the step:** one CLI command changed what the engine captured, with zero code edits — and
> the trace (and the Workbench view of it) reflects it.

**Other verbs** (all edit `./typhon.telemetry.json`):

```
typhon telemetry effective                            # preview exactly what would emit
typhon telemetry enable MemoryAllocations             # turn ON an off-by-default channel
typhon telemetry preset scheduler                     # apply a curated bundle (list them: typhon telemetry preset)
typhon telemetry reset CpuSampling                    # remove an explicit flag → back to inherited
typhon telemetry trace captures\run2.typhon-trace     # send the next run's trace to a different file
typhon telemetry trace --clear                        # stop writing a trace entirely
typhon telemetry edit                                 # full-screen interactive tri-state tree editor
```

---

## Cleanup

```powershell
Remove-Item -Recurse -Force C:\temp\MyApp, C:\temp\typhon-feed
```
```bash
rm -rf /c/temp/MyApp /c/temp/typhon-feed
```

---

## Notes

- **Why the local pack?** The published `Typhon 0.0.1-alpha.3` predates feature #514, so the scaffold's bare
  `[Archetype]` (no id) won't compile against it. Once a post-#514 package is published and
  `ProjectScaffolder.TyphonPackageVersion` is bumped, step 2 disappears: `typhon new MyApp && cd MyApp && dotnet run`
  just works from nuget.org.
- `typhon ui` serves the Workbench on `127.0.0.1` only. The SPA (`wwwroot`) is bundled next to the dll **by
  `dotnet publish`, not `dotnet build`** — that's why step 0 publishes the CLI. If `GET /` returns 404, you're running
  the un-published `bin\Debug\...\typhon.dll`; use the published one under `C:\temp\typhon-cli`.
- Stop `typhon ui` with **Ctrl-C** before you re-publish — a running instance locks the Workbench DLLs and the publish
  will fail with "file is being used by another process".
- **One `typhon ui` at a time.** Each launch mints a NEW bootstrap token (written to
  `%LOCALAPPDATA%\Typhon\Workbench\bootstrap.token`) and hands it to the browser via the `#wbtoken=…` launch URL; a
  second server on `:5200` won't bind. To find/kill a stray: `netstat -ano | findstr 127.0.0.1:5200` →
  `taskkill /F /PID <pid>`.
- **Token handoff.** The token rides the launch URL's `#wbtoken=…` fragment; the SPA captures it into `sessionStorage`
  on load and attaches it as `X-Workbench-Token` to every API call (there's no Vite dev-proxy under `typhon ui` to
  inject it). If a *specific panel* 401s while others load, that panel's request isn't attaching the token — a bug, not
  a config issue. (This was the class of bug fixed for the profiler/data hooks: they used raw `fetch` and were missing
  the header; all now go through `applyWorkbenchAuthHeaders`.)
