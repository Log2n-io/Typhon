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
  start | stop | status | restart | help. Default: start.

.EXAMPLE
  pwsh -File wb-dev.ps1 start
  pwsh -File wb-dev.ps1 status
  pwsh -File wb-dev.ps1 stop
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'restart', 'help', '--help', '-h')]
    [string]$Action = 'start'
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

# ── Actions ────────────────────────────────────────────────────────────────────

function Invoke-Help {
    @'
wb-dev.ps1 [start | stop | status | restart | help]

  Manage the Typhon Workbench dev servers (Kestrel :5200 + Vite :5173).
  Single-shot — replaces the 7-9 round-trip /wb-dev skill flow.

Actions:
  start     Launch Kestrel + Vite, wait for binding, write state JSON
  stop      Read state, kill process trees, delete state, verify
  status    Read state, check liveness + port binding
  restart   Stop then start (1 s pause between)
  help      Show this help

State:    .claude/state/wb-dev.json
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

    # Launch via cmd.exe /c with -WindowStyle Hidden so the child processes get their own hidden
    # console session and are NOT in this terminal's process group. Without this (-NoNewWindow),
    # Ctrl+C sends CTRL_C_EVENT to the entire group, killing npm/node without console-mode cleanup
    # and leaving the terminal with VT mode stuck on (the "goes crazy" symptom).
    $kestrelCmd = "dotnet watch --project `"$WorkbenchProj`" 1>`"$KestrelLog`" 2>`"$KestrelErrLog`""
    $kestrel = Start-Process 'cmd.exe' `
        -ArgumentList "/c $kestrelCmd" `
        -WorkingDirectory $RepoRoot `
        -WindowStyle Hidden `
        -PassThru

    $viteCmd = "npm.cmd run dev 1>`"$ViteLog`" 2>`"$ViteErrLog`""
    $vite = Start-Process 'cmd.exe' `
        -ArgumentList "/c $viteCmd" `
        -WorkingDirectory $ClientApp `
        -WindowStyle Hidden `
        -PassThru

    # Cold dotnet+orval+vite build runs ~28-30 s; Kestrel needs a few more seconds after that
    # to actually bind. 90 s leaves comfortable headroom for cold caches while still failing
    # fast on a genuine hang.
    $bindTimeoutSec = 90
    Write-Host "launching: Kestrel pid $($kestrel.Id) + Vite pid $($vite.Id)"
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
            startedAt  = (Get-Date).ToString('o')
            kestrelLog = $KestrelLog
            viteLog    = $ViteLog
        }
        Write-DevState $newState
        $startupComplete = $true

        Write-Host 'started successfully:'
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

# ── Dispatch ───────────────────────────────────────────────────────────────────

switch ($Action) {
    'help'    { Invoke-Help }
    '--help'  { Invoke-Help }
    '-h'      { Invoke-Help }
    'start'   { Invoke-Start }
    'stop'    { Invoke-Stop }
    'status'  { Invoke-Status }
    'restart' { Invoke-Restart }
}
