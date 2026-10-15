<#
.SYNOPSIS
    Builds one deployable payload for the SWG Tatooine bot box: the headless fleet runner and the Blazor control panel.

.DESCRIPTION
    The bot box's half of phase I2. `publish-swg.ps1` builds the SERVER payload; this builds the one that goes on the
    second instance, and they are separate scripts because they are separate instances with nothing in common but a
    private subnet — bundling them would put a browser client and a game server on a box that needs neither.

    Two binaries, because the box has two jobs:
      - SwgTatooine.Bots        the headless runner, which is what a workflow or a cron drives;
      - SwgTatooine.BotConsole  the Blazor operator surface, which is what a person drives.

    Both are self-contained, so the box needs no .NET runtime installed. Both are published into ONE directory, which
    works because they share every dependency and differ only in their entry assembly — and it means one tarball, one
    symlink and one rollback for the box as a whole.

    The layout it writes:

        <out>/
          SwgTatooine.Bots            the headless runner
          SwgTatooine.BotConsole      the control panel (binds loopback; reach it over an SSM tunnel)
          wwwroot/                     the panel's stylesheet
          VERSION                      commit, branch, UTC build time, RID
          run-fleet.sh                 the headless invocation, SWG_BOT_KEY required
          run-console.sh               the panel's invocation

    Neither script carries a key. SWG_BOT_KEY comes from the unit file's environment or the operator's shell, because a
    payload that contains the fleet's secret is a payload that must be handled like one — and this one goes to S3.

.PARAMETER Runtime
    The target RID. linux-x64 by default. The bot box can also be Graviton (08-hosting § 5 holds arm64 back for the
    SERVER, not for clients), in which case pass linux-arm64.

.PARAMETER OutDir
    Where to write the payload. Defaults to artifacts/swg-bots-<RID> at the repository root.

.PARAMETER SkipConsole
    Build only the headless runner. For a box that is driven entirely by a workflow and needs no operator surface.

.PARAMETER Archive
    Also write <OutDir>.tar.gz, which is what goes to S3.

.EXAMPLE
    ./scripts/publish-swg-bots.ps1 -Archive
    The default payload and its tarball, for the bot instance.

.EXAMPLE
    ./scripts/publish-swg-bots.ps1 -Runtime linux-arm64 -Archive
    The Graviton payload, for the spot bot box.
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'linux-x64',
    [string] $OutDir,
    [switch] $SkipConsole,
    [switch] $Archive
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts/swg-bots-$Runtime" }

# $ErrorActionPreference does not cover a native command's exit code: $PSNativeCommandUseErrorActionPreference is False
# by default on PowerShell 7.6, so a dotnet failure falls through silently unless it is checked.
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


# One expression, used by both the post-publish assertion and the generated scripts. It was computed twice, in two
# different variables, which is how they drift.
$exeName = if ($Runtime -like 'win-*') { '.exe' } else { '' }

if (Test-Path $OutDir)
{
    # Removed rather than overwritten, for publish-swg.ps1's reason: a stale file from a previous RID would ship inside a
    # payload that looks freshly built.
    Remove-Item -Recurse -Force $OutDir
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

Write-Host "Publishing the fleet runner ($Runtime, self-contained)..." -ForegroundColor Cyan
Invoke-Checked 'dotnet publish (bots)' {
    dotnet publish (Join-Path $repo 'demo/SwgTatooine.Bots/SwgTatooine.Bots.csproj') `
        -c Release -r $Runtime --self-contained true `
        -o $OutDir --nologo -v q
}

if ($SkipConsole)
{
    Write-Host '  -SkipConsole: no control panel in this payload. The box can only be driven headlessly.' -ForegroundColor Yellow
}
else
{
    # Into the SAME directory on purpose. The two entry assemblies share every dependency, so a second directory would be
    # a second copy of the runtime (~70 MB) for no benefit, and two payloads to keep in step on one box.
    Write-Host "Publishing the control panel ($Runtime, self-contained)..." -ForegroundColor Cyan
    Invoke-Checked 'dotnet publish (console)' {
        dotnet publish (Join-Path $repo 'demo/SwgTatooine.BotConsole/SwgTatooine.BotConsole.csproj') `
            -c Release -r $Runtime --self-contained true `
            -o $OutDir --nologo -v q
    }

    # BOTH apphosts, not just the one that was published last. The two projects publish into one directory, so the second
    # publish overwrites the shared DLLs they have in common -- which is safe only while they resolve the same package
    # versions, and silently produces a payload missing an entry point if the first one's apphost is ever clobbered.
    foreach ($exe in 'SwgTatooine.Bots', 'SwgTatooine.BotConsole')
    {
        if (-not (Test-Path (Join-Path $OutDir "$exe$exeName")))
        {
            throw "the publish left no $exe$exeName in $OutDir"
        }
    }
}

# Identify the payload, for the reason publish-swg.ps1 gives: a payload you cannot name is one you cannot roll back from.
$commit = (& git -C $repo rev-parse HEAD).Trim()
$branch = (& git -C $repo rev-parse --abbrev-ref HEAD).Trim()
$dirty = if ((& git -C $repo status --porcelain)) { ' (working tree dirty)' } else { '' }
@(
    "commit  $commit$dirty"
    "branch  $branch"
    "built   $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    "runtime $Runtime"
    "console $(if ($SkipConsole) { 'NOT INCLUDED (-SkipConsole)' } else { 'included' })"
) | Set-Content -Path (Join-Path $OutDir 'VERSION') -Encoding utf8

@'
#!/bin/sh
# The bot box's headless fleet. Generated by scripts/publish-swg-bots.ps1; edit the script, not this file.
#
# SWG_BOT_KEY is REQUIRED and is not in this payload: bot passwords are derived from it and never stored, so the
# accounts file beside this script authorises nothing without it. Use the SAME value every run, or the accounts already
# signed up cannot be logged into. Put it in the unit file's Environment= or EnvironmentFile=, never on the command
# line, where it would be visible in `ps` to every user on the box.
#
# ENDPOINT is the server's PRIVATE address: same AZ, $0 transfer, and still the real TCP/WebSocket path (07-bots § 5).
#
# --kind player needs accounts; --kind god needs none and no key. The server must have been started with a raised
# /auth limit (--auth-burst / --auth-refill-ms) or a thousand signups take about ninety-nine minutes: the limiter keys
# on the caller's /24, so this whole box is one key.
set -eu
here=$(cd "$(dirname "$0")" && pwd)

: "${ENDPOINT:?set ENDPOINT to the server's ws:// address}"
if [ "${KIND:-god}" = "player" ] && [ -z "${SWG_BOT_KEY:-}" ]; then
    echo "SWG_BOT_KEY is required for --kind player; see this script's header" >&2
    exit 2
fi

exec "$here/SwgTatooine.Bots__EXE__" \
    --endpoint "$ENDPOINT" \
    --bots "${BOTS:-1000}" \
    --kind "${KIND:-god}" \
    --seconds "${SECONDS_TO_RUN:-3600}" \
    --connect-rate "${CONNECT_RATE:-200}" \
    --accounts-file "${ACCOUNTS_FILE:-$here/data/bots.jsonl}" \
    "$@"
'@.Replace('__EXE__', $exeName) | ForEach-Object { Write-ShellScript -Path (Join-Path $OutDir 'run-fleet.sh') -Content $_ }

@'
#!/bin/sh
# The bot box's control panel. Generated by scripts/publish-swg-bots.ps1; edit the script, not this file.
#
# LOOPBACK by default and that is the posture, not a limitation: this page has a button that opens a thousand sessions
# against a server, so it is reached over an SSM port forward (08-hosting § 8, roadmap Q6 — tunnel-only, no operator
# password). Setting BIND to anything else publishes that button to whoever can route to this box.
#
#   aws ssm start-session --target <instance> \
#       --document-name AWS-StartPortForwardingSession \
#       --parameters 'portNumber=8090,localPortNumber=8090'
#
# SWG_BOT_KEY must be in this process's environment for a player fleet, for the reason run-fleet.sh gives.
#
# Shaping latency needs CAP_NET_ADMIN: give the unit `AmbientCapabilities=CAP_NET_ADMIN`, or allow tc through sudoers.
# Without it the panel refuses a non-zero latency rather than running unshaped, which would report a 60 ms run that was
# really a 0.2 ms one.
set -eu
here=$(cd "$(dirname "$0")" && pwd)

exec "$here/SwgTatooine.BotConsole__EXE__" \
    --port "${PORT:-8090}" \
    --bind "${BIND:-127.0.0.1}" \
    --endpoint "${ENDPOINT:-ws://127.0.0.1:9100/ws}" \
    "$@"
'@.Replace('__EXE__', $exeName) | ForEach-Object { Write-ShellScript -Path (Join-Path $OutDir 'run-console.sh') -Content $_ }

$bytes = (Get-ChildItem -Recurse -File $OutDir | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "  payload $OutDir" -ForegroundColor Green
Write-Host ("  {0:N1} MB, {1} files, commit {2}" -f ($bytes / 1MB), (Get-ChildItem -Recurse -File $OutDir).Count, $commit.Substring(0, 8))

if ($Archive)
{
    $tar = "$OutDir.tar.gz"
    if (Test-Path $tar) { Remove-Item -Force $tar }
    Write-Host "  archiving to $tar..." -ForegroundColor Cyan
    Invoke-Checked 'tar' { tar -czf $tar -C (Split-Path -Parent $OutDir) (Split-Path -Leaf $OutDir) }
    Write-Host ("  {0:N1} MB archived" -f ((Get-Item $tar).Length / 1MB)) -ForegroundColor Green
}
