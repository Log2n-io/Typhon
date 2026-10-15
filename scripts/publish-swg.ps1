<#
.SYNOPSIS
    Builds one deployable payload for the SWG Tatooine server: the engine binary and the browser client together.

.DESCRIPTION
    This is phase I2 of claude/design/SwgTatooine/14-game-programme.md. It produces the thing a deploy copies, so that
    "what runs on the box" is one directory built by one command rather than a dev tree someone rsynced.

    Two halves, because `dotnet publish` does not run Vite:
      - the server, as a self-contained linux-x64 publish (08-hosting § 6; NOT Native AOT, which measured ~12-13 %
        steady-state cost in the AOT POC);
      - the client, as `npm run build`, copied in beside it under client/.

    And a VERSION file naming the commit, because a payload you cannot identify is a payload you cannot roll back from.

    The layout it writes, and what the unit file is expected to run:

        <out>/
          SwgTatooine                 the server (no .NET runtime needed on the box)
          client/                      index.html + hashed assets
          VERSION                      commit, branch, UTC build time, RID
          run.sh                       the exact command, with --client-dir already pointed at client/

    The server's own `--client-dir` default walks up from the binary looking for demo/SwgTatooine.Client/dist, which
    exists in a dev tree and in no deployment — so run.sh passes it explicitly. Without that the deployed server answers
    404 for the root and serves nothing but a WebSocket.

.PARAMETER Runtime
    The target RID. linux-x64 by default, which is what 08-hosting § 1 and § 5 deploy to.

.PARAMETER OutDir
    Where to write the payload. Defaults to artifacts/swg-<RID> at the repository root.

.PARAMETER SkipClient
    Build only the server half. For iterating on the server when the client has not changed — the payload is then
    incomplete, and the script says so.

.PARAMETER Archive
    Also write <OutDir>.tar.gz, which is what goes to S3.

.EXAMPLE
    ./scripts/publish-swg.ps1
    The default payload, for the deployed server.

.EXAMPLE
    ./scripts/publish-swg.ps1 -Runtime win-x64 -Archive
    A Windows payload and its tarball, for trying the deployed shape on this box.
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'linux-x64',
    [string] $OutDir,
    [switch] $SkipClient,
    [switch] $Archive
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts/swg-$Runtime" }

# $ErrorActionPreference does not cover a native command's exit code: $PSNativeCommandUseErrorActionPreference is False
# by default on PowerShell 7.6, so npm and dotnet failures fall through silently unless each one is checked.
function Invoke-Checked
{
    param([string] $What, [scriptblock] $Body)
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}
# <b>Written with LF endings and no BOM, and that is not cosmetic.</b> Set-Content on Windows PowerShell writes CRLF,
# so the generated script began with a shebang ending in a carriage return -- and the Linux kernel then looks for an
# interpreter literally named '/bin/sh' plus that CR, fails to find it, and reports ENOENT. systemd surfaces it as
#
#     Failed to execute /opt/tatooine/current/run.sh: No such file or directory
#
# on a file that is plainly there and executable, which is about as misleading as an error gets. A BOM before the
# shebang breaks it the same way. Every payload built on a Windows box was unrunnable until this.
function Write-ShellScript
{
    param([string] $Path, [string] $Content)
    $lf = $Content -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($Path, $lf, [System.Text.UTF8Encoding]::new($false))
}


if (Test-Path $OutDir)
{
    # Removed rather than overwritten: a stale file from a previous RID or a renamed asset would otherwise ship inside a
    # payload that looks freshly built.
    Remove-Item -Recurse -Force $OutDir
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

Write-Host "Publishing the server ($Runtime, self-contained)..." -ForegroundColor Cyan
Invoke-Checked 'dotnet publish' {
    dotnet publish (Join-Path $repo 'demo/SwgTatooine/SwgTatooine.csproj') `
        -c Release -r $Runtime --self-contained true `
        -o $OutDir --nologo -v q
}

if ($SkipClient)
{
    Write-Host '  -SkipClient: no browser client in this payload. It will serve a WebSocket and no page.' -ForegroundColor Yellow
}
else
{
    $client = Join-Path $repo 'demo/SwgTatooine.Client'
    if (-not (Test-Path (Join-Path $client 'node_modules')))
    {
        Write-Host 'Installing client dependencies (npm ci)...' -ForegroundColor Cyan
        Push-Location $client
        try { Invoke-Checked 'npm ci' { npm ci } } finally { Pop-Location }
    }

    Write-Host 'Building the browser client (vite build)...' -ForegroundColor Cyan
    Push-Location $client
    try { Invoke-Checked 'vite build' { npm run build } } finally { Pop-Location }

    $dist = Join-Path $client 'dist'
    if (-not (Test-Path (Join-Path $dist 'index.html')))
    {
        throw "the client build left no index.html in $dist"
    }

    Copy-Item -Recurse -Force $dist (Join-Path $OutDir 'client')
}

# Identify the payload. A deploy that cannot say which commit it is carrying is a deploy nobody can roll back with
# confidence, and `git describe` is one line.
$commit = (& git -C $repo rev-parse HEAD).Trim()
$branch = (& git -C $repo rev-parse --abbrev-ref HEAD).Trim()
$dirty = if ((& git -C $repo status --porcelain)) { ' (working tree dirty)' } else { '' }
@(
    "commit  $commit$dirty"
    "branch  $branch"
    "built   $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    "runtime $Runtime"
    "client  $(if ($SkipClient) { 'NOT INCLUDED (-SkipClient)' } else { 'included' })"
) | Set-Content -Path (Join-Path $OutDir 'VERSION') -Encoding utf8

# The command the unit file runs, written beside the binary so that the payload documents its own invocation — including
# --client-dir, which the server cannot infer once it is out of the repository.
$exeName = if ($Runtime -like 'win-*') { 'SwgTatooine.exe' } else { 'SwgTatooine' }
@'
#!/bin/sh
# The deployed server's command line. Generated by scripts/publish-swg.ps1; edit the script, not this file.
#
# --bind 0.0.0.0 is required to be reachable and demands --origin, because allowing any origin on a public bind would let
# any page a visitor opens drive this server from their browser. Set ORIGIN to the front door's URL.
#
# Live profiling cannot be switched on by a flag: the engine bakes its telemetry gate before Main runs. Export
# TYPHON__PROFILER__LIVE in the unit's Environment= instead, and reach it over an SSM tunnel rather than binding it wide.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# TLS_CHAIN/TLS_KEY arrive from the unit's EnvironmentFile and are empty on a box with no certificate, in which case the
# server serves plain HTTP on the game port and says so. AUTH_LOCAL_ONLY keeps /auth off the public internet -- required
# whenever this is reachable without a CDN in front, because a signup costs the server ~51 ms of a core.
exec "$here/__EXE__" \
    --serve "${PORT:-8080}" \
    --bind "${BIND:-127.0.0.1}" \
    ${ORIGIN:+--origin "$ORIGIN"} \
    ${TLS_CHAIN:+--tls-chain "$TLS_CHAIN"} \
    ${TLS_KEY:+--tls-key "$TLS_KEY"} \
    ${TLS_CHAIN:+--tls-port "${TLS_PORT:-443}"} \
    ${TLS_CHAIN:+--redirect-port "${REDIRECT_PORT:-80}"} \
    ${AUTH_LOCAL_ONLY:+--auth-local-only}     ${AUTH_ITERATIONS:+--auth-iterations "$AUTH_ITERATIONS"} \
    --client-dir "$here/client" \
    --db-dir "${DB_DIR:-$here/data}" \
    --persist \
    "$@"
'@.Replace('__EXE__', $exeName) | ForEach-Object { Write-ShellScript -Path (Join-Path $OutDir 'run.sh') -Content $_ }

$bytes = (Get-ChildItem -Recurse -File $OutDir | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "  payload $OutDir" -ForegroundColor Green
Write-Host ("  {0:N1} MB, {1} files, commit {2}" -f ($bytes / 1MB), (Get-ChildItem -Recurse -File $OutDir).Count, $commit.Substring(0, 8))

if ($Archive)
{
    $tar = "$OutDir.tar.gz"
    if (Test-Path $tar) { Remove-Item -Force $tar }
    Write-Host "  archiving to $tar..." -ForegroundColor Cyan
    # bsdtar ships with Windows 10+ and every Linux runner, so no PowerShell module is needed. -C keeps the archive rooted
    # at the payload directory rather than embedding the absolute path it was built at.
    Invoke-Checked 'tar' { tar -czf $tar -C (Split-Path -Parent $OutDir) (Split-Path -Leaf $OutDir) }
    Write-Host ("  {0:N1} MB archived" -f ((Get-Item $tar).Length / 1MB)) -ForegroundColor Green
}
