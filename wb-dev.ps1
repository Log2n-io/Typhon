<#
.SYNOPSIS
  Manage the Typhon Workbench dev servers (Kestrel :5200 + Vite :5173).

.DESCRIPTION
  Single-shot replacement for the multi-step `/wb-dev` skill workflow. Tracks PIDs in
  `.claude/state/wb-dev.json` so a later invocation can stop servers it didn't start.
  All actions complete in one PowerShell session — no per-step Bash round-trips.

  Keys to the speedup over the old skill:
    - `Start-Process -PassThru` returns the launched PID directly. No netstat-then-
      Get-CimInstance dance to walk from listener -> root.
    - `Get-NetTCPConnection` queries the kernel's TCP table — no string-parsing of
      `netstat -ano` output.
    - PowerShell-native JSON via ConvertTo-Json / ConvertFrom-Json. No Python hop.
    - Tight in-script poll for port binding (250 ms) — no per-iteration tool round-
      trip cost.

.PARAMETER Action
  start | stop | reset | status | restart | watch-kestrel | watch-vite | help. Default: start.

  watch-kestrel / watch-vite run a single server in the FOREGROUND of the calling
  shell (dedicated window) so you get interactive Hot Reload prompts and live output.
  They block until Ctrl+C, then clear their own PID from the shared state file.

.EXAMPLE
  pwsh -File wb-dev.ps1 start
  pwsh -File wb-dev.ps1 status
  pwsh -File wb-dev.ps1 stop

.EXAMPLE
  # window 1:  pwsh -File wb-dev.ps1 watch-kestrel
  # window 2:  pwsh -File wb-dev.ps1 watch-vite
  # window 3:  pwsh -File wb-dev.ps1 status

.EXAMPLE
  # Prod-mode SPA (perf-testing without dev-mode React noise):
  pwsh -File wb-dev.ps1 start -Prod
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'reset', 'status', 'restart', 'watch-kestrel', 'watch-vite', 'help', '--help', '-h')]
    [string]$Action = 'start',

    # Overrides the Kestrel / ASP.NET Core log category (Microsoft.AspNetCore) via a command-line
    # config key passed to `dotnet watch`. Applies to start / restart / watch-kestrel. Without it,
    # appsettings.Development.json's "Information" wins. Accepts the standard .NET levels (e.g.
    # -LogLevel Warning, or the prefix/colon form -log:warning).
    [ValidateSet('Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None')]
    [string]$LogLevel,

    # Prod-mode SPA: build the client bundle with NODE_ENV=production then serve via `vite preview`
    # instead of `vite` (dev). Eliminates dev-mode React noise (strict-mode double-render, profiler
    # instrumentation, HMR overhead) so a `Performance` recording shows what users actually feel.
    # Kestrel still runs in dev (`dotnet watch`) — the API's perf is not what changes between modes;
    # the SPA bundle is. Applies to: start / restart.
    [switch]$Prod
)

$ErrorActionPreference = 'Stop'

# ── Paths ──────────────────────────────────────────────────────────────────────
$RepoRoot       = $PSScriptRoot
$StateDir       = Join-Path $RepoRoot '.claude/state'
$StateFile      = Join-Path $StateDir 'wb-dev.json'
$KestrelLog     = Join-Path $StateDir 'wb-dev.kestrel.log'
$KestrelErrLog  = Join-Path $StateDir 'wb-dev.kestrel.err.log'
$ViteLog        = Join-Path $StateDir 'wb-dev.vite.log'
$ViteErrLog     = Join-Path $StateDir 'wb-dev.vite.err.log'
$WorkbenchProj  = Join-Path $RepoRoot 'tools/Typhon.Workbench'
$ClientApp      = Join-Path $WorkbenchProj 'ClientApp'

# App args appended after `--` so `dotnet watch` forwards them to the Workbench host. The
# Microsoft.AspNetCore config key (a command-line config provider override) outranks
# appsettings*.json, so -LogLevel wins over the Development file's "Information".
$KestrelAppArgs = if ($LogLevel) { @('--', "--Logging:LogLevel:Microsoft.AspNetCore=$LogLevel") } else { @() }

# ── Helpers ────────────────────────────────────────────────────────────────────

function Read-DevState {
    if (-not (Test-Path $StateFile)) { return $null }
    return Get-Content $StateFile -Raw | ConvertFrom-Json
}

function Write-DevState($state) {
    if (-not (Test-Path $StateDir)) {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    }
    $state | ConvertTo-Json -Depth 4 | Set-Content -Path $StateFile -Encoding UTF8
}

# Read-modify-write a subset of keys into the state file. Used by watch-kestrel /
# watch-vite, which run in separate shells and must not clobber each other's PID.
function Update-DevState([hashtable]$patch) {
    $state = Read-DevState
    if (-not $state) { $state = [pscustomobject]@{} }
    foreach ($key in $patch.Keys) {
        if ($state.PSObject.Properties[$key]) {
            $state.$key = $patch[$key]
        }
        else {
            $state | Add-Member -NotePropertyName $key -NotePropertyValue $patch[$key]
        }
    }
    Write-DevState $state
}

# Drop one PID key on shutdown. Deletes the whole file once no PID keys remain so a
# later `status` doesn't report stale entries.
function Remove-DevStateKey([string]$key) {
    $state = Read-DevState
    if (-not $state) { return }
    if ($state.PSObject.Properties[$key]) {
        $state.PSObject.Properties.Remove($key)
    }
    $hasK = $state.PSObject.Properties['kestrelPid'] -and $state.kestrelPid
    $hasV = $state.PSObject.Properties['vitePid']    -and $state.vitePid
    if (-not $hasK -and -not $hasV) {
        Remove-Item $StateFile -ErrorAction SilentlyContinue
    }
    else {
        Write-DevState $state
    }
}

function Test-ProcAlive($processId) {
    if (-not $processId) { return $false }
    return $null -ne (Get-Process -Id $processId -ErrorAction SilentlyContinue)
}

function Get-PortOwner($port) {
    return (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty OwningProcess)
}

function Test-PortBound($port) {
    return $null -ne (Get-PortOwner $port)
}

function Wait-PortBound($port, $timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-PortBound $port) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Stop-ProcTree($processId) {
    if (-not $processId) { return }
    # /T cascades to descendants (dotnet watch -> Typhon.Workbench.exe; npm.cmd -> node.exe).
    # /F forces. Swallow output — already-dead PIDs return non-zero with a "not found" message
    # which we don't care about at this layer.
    taskkill /F /T /PID $processId 2>&1 | Out-Null
}

# This script's own process plus its ancestor chain (pwsh -> cmd/bash -> ...). The `reset` sweep
# excludes these so a `/T` tree-kill can never take down the shell running it — the documented
# "pwsh self-kill" gotcha. Bounded + cycle-guarded against a pathological parent loop.
function Get-SelfAncestry {
    $safe = [System.Collections.Generic.HashSet[int]]::new()
    $cur = $PID
    for ($i = 0; $cur -and $i -lt 64; $i++) {
        if (-not $safe.Add([int]$cur)) { break }
        $p = Get-CimInstance Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue
        if (-not $p) { break }
        $cur = [int]$p.ParentProcessId
    }
    return $safe
}

# Find every wb-dev server process by SIGNATURE — independent of the (possibly stale/missing)
# state file. Two sources, unioned: (1) dotnet/node whose command line targets this repo's
# Workbench tree (`Typhon.Workbench` appears in both the Kestrel `--project` path and Vite's
# `ClientApp/node_modules/...` path), tightly scoped so an unrelated dotnet/node is never reaped;
# (2) whoever currently owns :5200 / :5173 (catches a listener whose command line didn't match,
# e.g. the published `Typhon.Workbench.exe`). Returns pid -> reason; excludes this script's ancestry.
function Find-WbDevProcesses {
    $safe = Get-SelfAncestry
    $found = [System.Collections.Generic.Dictionary[int, string]]::new()

    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='node.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.CommandLine -and $_.CommandLine -match 'Typhon\.Workbench' -and -not $safe.Contains([int]$_.ProcessId)) {
                $found[[int]$_.ProcessId] = $_.Name
            }
        }

    foreach ($port in 5200, 5173) {
        $owner = Get-PortOwner $port
        if ($owner -and -not $safe.Contains([int]$owner) -and -not $found.ContainsKey([int]$owner)) {
            $found[[int]$owner] = "port:$port"
        }
    }
    return $found
}

# ── Actions ────────────────────────────────────────────────────────────────────

function Invoke-Help {
    @'
wb-dev.ps1 [start | stop | status | restart | watch-kestrel | watch-vite | help]

  Manage the Typhon Workbench dev servers (Kestrel :5200 + Vite :5173).
  Single-shot — replaces the 7-9 round-trip /wb-dev skill flow.

Actions:
  start          Launch Kestrel + Vite detached, wait for binding, write state JSON
  stop           Read state, kill process trees, delete state, verify
  reset          Kill EVERY lingering Workbench Kestrel/Vite by signature + port
                 owner (ignores the state file), then clear state. Use when start
                 reports an untracked port, or status shows stale/orphan processes.
  status         Read state, check liveness + port binding
  restart        Stop then start (1 s pause between)
  watch-kestrel  Run Kestrel in THIS shell (foreground, interactive Hot Reload);
                 blocks until Ctrl+C. Use a dedicated window.
  watch-vite     Run Vite in THIS shell (foreground); blocks until Ctrl+C.
  help           Show this help

  watch-* write their PID into the same state file, so `status` / `stop` from a
  third shell see them. Ctrl+C in the watch window clears that server's PID.

Options:
  -LogLevel <level>  Override the Kestrel / ASP.NET Core log category
                     (Microsoft.AspNetCore). Levels: Trace, Debug, Information,
                     Warning, Error, Critical, None. Applies to start / restart /
                     watch-kestrel. Without it, appsettings.Development.json's
                     "Information" wins. Prefix/colon form also works: -log:warning
  -Prod              Build the SPA with NODE_ENV=production then serve via
                     `vite preview` instead of the dev server. Eliminates dev-mode
                     React noise (strict-mode double-render, profiler instrumentation,
                     HMR overhead) so a Performance recording shows what users actually
                     feel. Kestrel still runs in dev — the API perf is not what
                     differs between modes. Applies to: start / restart.

Examples:
  wb-dev.ps1 watch-kestrel -log:warning
  wb-dev.ps1 start -LogLevel Warning
  wb-dev.ps1 start -Prod                 # production SPA build, perf-test mode

State:    .claude/state/wb-dev.json  (includes "mode": "dev" | "prod")
Logs:     .claude/state/wb-dev.kestrel.log, wb-dev.vite.log
          .claude/state/wb-dev.kestrel.err.log, wb-dev.vite.err.log

Endpoints (after start):
  Backend:  http://localhost:5200/health
  Frontend: http://localhost:5173
  OpenAPI:  http://localhost:5200/openapi.json
'@
}

function Invoke-Status {
    $state = Read-DevState
    if (-not $state) {
        Write-Host 'no state file — wb-dev is not tracked'
        $p1 = Get-PortOwner 5200
        $p2 = Get-PortOwner 5173
        if ($p1 -or $p2) {
            Write-Host 'WARN: untracked process bound to wb-dev ports:'
            if ($p1) { Write-Host "  :5200 -> pid $p1" }
            if ($p2) { Write-Host "  :5173 -> pid $p2" }
        }
        return
    }

    $kAlive   = Test-ProcAlive $state.kestrelPid
    $vAlive   = Test-ProcAlive $state.vitePid
    $port5200 = Get-PortOwner 5200
    $port5173 = Get-PortOwner 5173

    $modeLabel = if ($state.PSObject.Properties['mode'] -and $state.mode) { $state.mode } else { 'dev (legacy state)' }
    Write-Host "Mode:    $modeLabel"
    Write-Host "Kestrel  pid $($state.kestrelPid)  $(if ($kAlive) { 'alive' } else { 'dead' })"
    Write-Host "         :5200  $(if ($port5200) { "bound (pid $port5200)" } else { 'unbound' })"
    Write-Host "Vite     pid $($state.vitePid)  $(if ($vAlive) { 'alive' } else { 'dead' })"
    Write-Host "         :5173  $(if ($port5173) { "bound (pid $port5173)" } else { 'unbound' })"
    Write-Host "Started: $($state.startedAt)"

    # Stale-state warning: port bound to a different PID than what we recorded.
    if ($port5200 -and $port5200 -ne $state.kestrelPid -and -not $kAlive) {
        Write-Host "WARN: :5200 owner pid $port5200 differs from recorded pid $($state.kestrelPid) — state is stale (reboot? manual kill?)"
    }
    if ($port5173 -and $port5173 -ne $state.vitePid -and -not $vAlive) {
        Write-Host "WARN: :5173 owner pid $port5173 differs from recorded pid $($state.vitePid) — state is stale (reboot? manual kill?)"
    }
}

function Invoke-Start {
    $state = Read-DevState
    if ($state) {
        if ((Test-ProcAlive $state.kestrelPid) -and (Test-ProcAlive $state.vitePid)) {
            Write-Host 'wb-dev already running:'
            Write-Host "  Kestrel pid $($state.kestrelPid)  ->  http://localhost:5200/health"
            Write-Host "  Vite    pid $($state.vitePid)  ->  http://localhost:5173"
            return
        }
        Write-Host 'stale state file — clearing'
        Remove-Item $StateFile -ErrorAction SilentlyContinue
    }

    if ((Test-PortBound 5200) -or (Test-PortBound 5173)) {
        Write-Host 'ERROR: port already bound by an untracked process:'
        $p1 = Get-PortOwner 5200; if ($p1) { Write-Host "  :5200 -> pid $p1" }
        $p2 = Get-PortOwner 5173; if ($p2) { Write-Host "  :5173 -> pid $p2" }
        Write-Host 'kill the owning process(es) first; do not invoke wb-dev start until clear'
        exit 1
    }

    if (-not (Test-Path $StateDir)) {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    }

    # Prod mode: build the SPA first (synchronous, fail-fast). The build emits to ../wwwroot per
    # vite.config.ts; `vite preview` then serves that bundle on :5173 with the same /api proxy as
    # dev mode (preview block we added to vite.config.ts mirrors the server block). The bundle is
    # built with NODE_ENV=production so the React runtime drops dev-mode hooks (strict-mode
    # double-render, profiler instrumentation, performance.measure) — that's the whole point.
    if ($Prod) {
        Write-Host 'prod mode: building SPA (NODE_ENV=production) — this can take ~15-30 s on a cold cache'
        $buildExit = & npm.cmd --prefix $ClientApp run build 2>&1 | Tee-Object -FilePath $ViteLog | ForEach-Object {
            # Stream build output into wb-dev.vite.log AND inline so the user sees progress.
            Write-Host $_
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: SPA build failed (exit $LASTEXITCODE) — check $ViteLog for details"
            exit 1
        }
        Write-Host 'SPA build complete; launching Kestrel + vite preview'
    }

    # Launch via cmd.exe /c with -WindowStyle Hidden so the child processes get their own hidden
    # console session and are NOT in this terminal's process group. Without this (-NoNewWindow),
    # Ctrl+C sends CTRL_C_EVENT to the entire group, killing npm/node without console-mode cleanup
    # and leaving the terminal with VT mode stuck on (the "goes crazy" symptom).
    $kestrelAppArgStr = if ($KestrelAppArgs.Count) { ' ' + ($KestrelAppArgs -join ' ') } else { '' }
    $kestrelCmd = "dotnet watch --project `"$WorkbenchProj`"$kestrelAppArgStr 1>`"$KestrelLog`" 2>`"$KestrelErrLog`""
    $kestrel = Start-Process 'cmd.exe' `
        -ArgumentList "/c $kestrelCmd" `
        -WorkingDirectory $RepoRoot `
        -WindowStyle Hidden `
        -PassThru

    # In prod, run `vite preview` (serves the production bundle from ../wwwroot); in dev, `vite`
    # (the HMR-enabled dev server). Both bind :5173 with the same /api token-injecting proxy.
    $viteScript = if ($Prod) { 'preview' } else { 'dev' }
    $viteCmd = "npm.cmd run $viteScript 1>>`"$ViteLog`" 2>`"$ViteErrLog`""
    $vite = Start-Process 'cmd.exe' `
        -ArgumentList "/c $viteCmd" `
        -WorkingDirectory $ClientApp `
        -WindowStyle Hidden `
        -PassThru

    # Cold dotnet+orval+vite build runs ~28-30 s; Kestrel needs a few more seconds after that
    # to actually bind. 90 s leaves comfortable headroom for cold caches while still failing
    # fast on a genuine hang.
    $bindTimeoutSec = 90
    $modeLabel = if ($Prod) { 'PROD (vite preview)' } else { 'dev (vite hmr)' }
    Write-Host "launching [$modeLabel]: Kestrel pid $($kestrel.Id) + Vite pid $($vite.Id)"
    if ($LogLevel) { Write-Host "Microsoft.AspNetCore log level overridden -> $LogLevel" }
    Write-Host "waiting for ports to bind (timeout $bindTimeoutSec s)..."

    # try/finally ensures child processes are killed if Ctrl+C is pressed during the wait loop.
    # The children no longer receive the signal themselves (detached console), so this script
    # is responsible for cleanup on interruption.
    $startupComplete = $false
    try {
        $kBound = Wait-PortBound 5200 $bindTimeoutSec
        $vBound = Wait-PortBound 5173 $bindTimeoutSec

        if (-not ($kBound -and $vBound)) {
            Write-Host 'ERROR: timeout waiting for binding'
            Write-Host "  :5200 $(if ($kBound) { 'bound' } else { 'NOT BOUND' })"
            Write-Host "  :5173 $(if ($vBound) { 'bound' } else { 'NOT BOUND' })"
            Write-Host '--- last 30 lines: Kestrel stdout ---'
            if (Test-Path $KestrelLog)    { Get-Content $KestrelLog    -Tail 30 }
            Write-Host '--- last 30 lines: Kestrel stderr ---'
            if (Test-Path $KestrelErrLog) { Get-Content $KestrelErrLog -Tail 30 }
            Write-Host '--- last 30 lines: Vite stdout ---'
            if (Test-Path $ViteLog)    { Get-Content $ViteLog    -Tail 30 }
            Write-Host '--- last 30 lines: Vite stderr ---'
            if (Test-Path $ViteErrLog) { Get-Content $ViteErrLog -Tail 30 }
            exit 1
        }

        $newState = [pscustomobject]@{
            kestrelPid = $kestrel.Id
            vitePid    = $vite.Id
            mode       = if ($Prod) { 'prod' } else { 'dev' }
            startedAt  = (Get-Date).ToString('o')
            kestrelLog = $KestrelLog
            viteLog    = $ViteLog
        }
        Write-DevState $newState
        $startupComplete = $true

        Write-Host "started successfully [$modeLabel]:"
        Write-Host "  Kestrel pid $($kestrel.Id)  ->  http://localhost:5200/health"
        Write-Host "  Vite    pid $($vite.Id)  ->  http://localhost:5173"
        Write-Host "  Logs:   $KestrelLog"
        Write-Host "          $ViteLog"
    }
    finally {
        if (-not $startupComplete) {
            Stop-ProcTree $kestrel.Id
            Stop-ProcTree $vite.Id
        }
    }
}

function Invoke-Stop {
    $state = Read-DevState
    if (-not $state) {
        Write-Host 'no state file — nothing to stop'
        $p1 = Get-PortOwner 5200
        $p2 = Get-PortOwner 5173
        if ($p1 -or $p2) {
            Write-Host 'WARN: untracked process bound to wb-dev ports:'
            if ($p1) { Write-Host "  :5200 -> pid $p1 (run: taskkill /F /PID $p1)" }
            if ($p2) { Write-Host "  :5173 -> pid $p2 (run: taskkill /F /PID $p2)" }
        }
        return
    }

    Stop-ProcTree $state.kestrelPid
    Stop-ProcTree $state.vitePid

    Remove-Item $StateFile -ErrorAction SilentlyContinue

    Start-Sleep -Milliseconds 300
    $stillBound = @()
    if (Test-PortBound 5200) { $stillBound += "5200 (pid $(Get-PortOwner 5200))" }
    if (Test-PortBound 5173) { $stillBound += "5173 (pid $(Get-PortOwner 5173))" }
    if ($stillBound.Count -gt 0) {
        Write-Host "WARN: ports still bound after kill: $($stillBound -join ', ')"
        Write-Host 'manual taskkill /F /PID <pid> may be required'
    } else {
        Write-Host "stopped Kestrel (pid $($state.kestrelPid)) + Vite (pid $($state.vitePid))"
    }
}

function Invoke-Restart {
    Invoke-Stop
    Start-Sleep -Seconds 1
    Invoke-Start
}

# Nuke-from-orbit recovery: kill EVERY lingering Workbench Kestrel/Vite found by signature +
# port owner (state file ignored), then clear state. Use when `start` reports an untracked port,
# or `status` shows stale/orphan processes (e.g. a `dotnet watch` that choked on "too many
# changes" and left an orphan watcher). Unlike `stop`, it does not depend on the tracked PIDs.
function Invoke-Reset {
    Write-Host 'reset — killing every lingering Workbench dev process (state-independent)...'
    $found = Find-WbDevProcesses
    if ($found.Count -eq 0) {
        Write-Host '  no Workbench Kestrel/Vite processes found'
    }
    else {
        foreach ($entry in $found.GetEnumerator()) {
            Write-Host ("  killing pid {0} [{1}]" -f $entry.Key, $entry.Value)
            Stop-ProcTree $entry.Key
        }
    }

    Remove-Item $StateFile -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $stillBound = @()
    if (Test-PortBound 5200) { $stillBound += "5200 (pid $(Get-PortOwner 5200))" }
    if (Test-PortBound 5173) { $stillBound += "5173 (pid $(Get-PortOwner 5173))" }
    if ($stillBound.Count -gt 0) {
        Write-Host "WARN: ports still bound after reset: $($stillBound -join ', ')"
        Write-Host 'a manual  taskkill /F /PID <pid>  may be required'
    }
    else {
        Write-Host 'reset complete — :5200 and :5173 are free; state cleared'
    }
}

# Run one server in the foreground of THIS shell. -NoNewWindow keeps the child on the
# calling console, so its stdout streams live and stdin is available for `dotnet watch`
# Hot Reload prompts. Blocks on Wait-Process until Ctrl+C; the finally clause kills the
# tree and removes only this server's PID from the shared state.
function Invoke-WatchKestrel {
    if (Test-PortBound 5200) {
        Write-Host "ERROR: :5200 already bound (pid $(Get-PortOwner 5200)) — stop it first"
        exit 1
    }
    if (-not (Test-Path $StateDir)) {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    }

    Write-Host 'starting Kestrel (dotnet watch) in this window — Ctrl+C to stop'
    if ($LogLevel) { Write-Host "Microsoft.AspNetCore log level overridden -> $LogLevel" }
    $proc = Start-Process 'dotnet' `
        -ArgumentList (@('watch', '--project', $WorkbenchProj) + $KestrelAppArgs) `
        -WorkingDirectory $RepoRoot `
        -NoNewWindow -PassThru

    Update-DevState @{ kestrelPid = $proc.Id; startedAt = (Get-Date).ToString('o') }
    Write-Host "Kestrel pid $($proc.Id)  ->  http://localhost:5200/health"
    Write-Host "check from another shell:  wb-dev.ps1 status"

    try {
        Wait-Process -Id $proc.Id
    }
    finally {
        Stop-ProcTree $proc.Id
        Remove-DevStateKey 'kestrelPid'
        Write-Host "`nKestrel stopped — kestrelPid cleared from state"
    }
}

function Invoke-WatchVite {
    if (Test-PortBound 5173) {
        Write-Host "ERROR: :5173 already bound (pid $(Get-PortOwner 5173)) — stop it first"
        exit 1
    }
    if (-not (Test-Path $StateDir)) {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    }

    Write-Host 'starting Vite (npm run dev) in this window — Ctrl+C to stop'
    $proc = Start-Process 'npm.cmd' `
        -ArgumentList 'run', 'dev' `
        -WorkingDirectory $ClientApp `
        -NoNewWindow -PassThru

    Update-DevState @{ vitePid = $proc.Id; startedAt = (Get-Date).ToString('o') }
    Write-Host "Vite pid $($proc.Id)  ->  http://localhost:5173"
    Write-Host "check from another shell:  wb-dev.ps1 status"

    try {
        Wait-Process -Id $proc.Id
    }
    finally {
        Stop-ProcTree $proc.Id
        Remove-DevStateKey 'vitePid'
        Write-Host "`nVite stopped — vitePid cleared from state"
    }
}

# ── Dispatch ───────────────────────────────────────────────────────────────────

switch ($Action) {
    'help'          { Invoke-Help }
    '--help'        { Invoke-Help }
    '-h'            { Invoke-Help }
    'start'         { Invoke-Start }
    'stop'          { Invoke-Stop }
    'reset'         { Invoke-Reset }
    'status'        { Invoke-Status }
    'restart'       { Invoke-Restart }
    'watch-kestrel' { Invoke-WatchKestrel }
    'watch-vite'    { Invoke-WatchVite }
}
