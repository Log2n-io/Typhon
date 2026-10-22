<#
.SYNOPSIS
    Runs a large SWG Tatooine bot swarm by splitting it across several bot PROCESSES.

.DESCRIPTION
    One `SwgTatooine.Bots` process cannot carry a large swarm on this workstation, and the way it fails is misleading.
    Measured on 2026-10-22 against a server that stayed completely idle throughout:

        --bots 5000 in ONE process  ->  581 sessions, then flat for ever
        the same 5000 across EIGHT  ->  2 979 sessions on the first pass

    What happens in the single process is that its own signups start failing once its websockets are live:

        !! chunk 0..99: 1 account(s) failed, first: HttpRequestException
        !! a session could not be opened: the server refused the session with 4102

    A bot whose signup failed arrives with no token, the server refuses it (4102), the ramp counts that as a session
    closing as fast as it opened, and after three such rounds it latches into its churn backoff for the rest of the run.
    So ONE failed signup wedges the whole fleet. It is not the box — 1 307 established sockets with 64 511 ephemeral
    ports free — and it is not the server, which reported `closedSilent 0`, `closedLagging 0` and zero tick overruns
    while this was happening.

    This script works around it two ways. It spreads the swarm over several processes with DISJOINT account ranges
    (`--account-base`), and it runs repeated passes: accounts persist in their files, so each pass signs up only what
    the last one could not and spends no time on HTTP for the rest. Four passes took a 5 000-bot target from 581 to
    5 713 live sessions.

    The accounts are only reusable while the SERVER KEEPS RUNNING, because a server with no `SWG_AUTH_KEY` signs tokens
    with a key generated per process. Set that variable on the server if you want the accounts to outlive a restart.

.PARAMETER Total
    Bots to run across the whole fleet.

.PARAMETER Processes
    How many bot processes to split them over. Each one should stay below ~600, which is where a single process starts
    failing its own signups.

.PARAMETER Passes
    How many times to run the fleet. Each pass reuses the accounts the previous one provisioned, so the population
    climbs. Accounts stop accumulating once every process has wedged; three or four passes is where it flattens.

.PARAMETER Seconds
    The measured window of the final pass. Earlier passes use a short window, since they exist to provision.

.PARAMETER Endpoint
    The server's WebSocket endpoint.

.PARAMETER AccountsDirectory
    Where the per-process account files live. They are REUSED: delete them to start from no accounts.

.EXAMPLE
    ./scripts/swg-fleet.ps1 -Total 5000 -Processes 8
    Four passes, then a long final pass. Watch the population with:
      curl -s http://127.0.0.1:8080/typhon/stats.json
#>
[CmdletBinding()]
param(
    [int]$Total = 5000,
    [int]$Processes = 8,
    [int]$Passes = 4,
    [int]$Seconds = 1800,
    [string]$Endpoint = 'ws://127.0.0.1:8080/ws',
    [string]$AccountsDirectory = "$env:TEMP\swg-fleet"
)

$ErrorActionPreference = 'Stop'

if (-not $env:SWG_BOT_KEY) {
    Write-Error 'SWG_BOT_KEY must be set: bot passwords are derived from it and are never written anywhere.'
}

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'demo\SwgTatooine.Bots\bin\Release\net10.0\SwgTatooine.Bots.exe'
if (-not (Test-Path $exe)) {
    Write-Error "$exe not found. Build it: dotnet build demo/SwgTatooine.Bots/SwgTatooine.Bots.csproj -c Release"
}

if (-not (Test-Path $AccountsDirectory)) { New-Item -ItemType Directory -Path $AccountsDirectory | Out-Null }

$per = [math]::Ceiling($Total / $Processes)
$statsUrl = ($Endpoint -replace '^ws', 'http') -replace '/ws$', '/typhon/stats.json'

Write-Host "fleet: $Total bots over $Processes process(es), $per each, $Passes pass(es)"
Write-Host "accounts: $AccountsDirectory (reused; delete to start clean)"

function Get-SessionCount {
    try { (Invoke-RestMethod -Uri $statsUrl -TimeoutSec 4).replication.sessions } catch { -1 }
}

for ($pass = 1; $pass -le $Passes; $pass++) {
    # Every pass but the last exists to provision accounts, so it does not need a long window.
    $window = if ($pass -eq $Passes) { $Seconds } else { 120 }

    Get-Process -Name 'SwgTatooine.Bots' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 6

    for ($i = 0; $i -lt $Processes; $i++) {
        $base = $i * $per
        $file = Join-Path $AccountsDirectory "fleet-$i.json"
        $botArgs = @(
            '--endpoint', $Endpoint, '--bots', $per, '--kind', 'player',
            '--accounts-file', $file, '--account-base', $base,
            '--seconds', $window, '--chunk-size', 100, '--signup-concurrency', 4
        )
        Start-Process -FilePath $exe -ArgumentList $botArgs -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $AccountsDirectory "pass$pass-$i.log") `
            -RedirectStandardError  (Join-Path $AccountsDirectory "pass$pass-$i.err") | Out-Null

        # Staggered: eight processes signing up at once is the contention this script exists to avoid.
        Start-Sleep -Seconds 4
    }

    Start-Sleep -Seconds 50
    $sessions = Get-SessionCount
    $accounts = (Get-ChildItem -Path $AccountsDirectory -Filter 'fleet-*.json' |
        ForEach-Object { (Select-String -Path $_.FullName -Pattern '"token"' -AllMatches).Matches.Count } |
        Measure-Object -Sum).Sum
    Write-Host ("pass {0}/{1}: {2} sessions live, {3} accounts provisioned" -f $pass, $Passes, $sessions, $accounts)
}

Write-Host "final pass is running for $Seconds s. Population: curl -s $statsUrl"
Write-Host "stop it with: Get-Process -Name SwgTatooine.Bots | Stop-Process -Force"
