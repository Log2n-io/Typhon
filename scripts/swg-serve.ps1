<#
.SYNOPSIS
    Serves the SWG Tatooine world, optionally with live profiling the Workbench can attach to.

.DESCRIPTION
    The server cannot switch its own profiler on. The engine's master gate is a `static readonly bool` resolved in a
    static constructor from `typhon.telemetry.json` and `TYPHON__PROFILER__*`, and it is baked before `Main` runs:
    this assembly's generated [ModuleInitializer] registers its components and archetypes through the engine, which
    loads Typhon.Engine first. So `--live` on the command line is refused, and this script is the working equivalent —
    it sets the variable, then starts the process.

    It also CLEARS the profiler variables when -Live is absent, which is the half that matters for measurement: a
    `$env:` assignment persists for a whole shell session, so a variable set once to attach the Workbench keeps
    profiling every later run in that shell. Without that, "a default run" is not reproducible from a shell that has
    ever profiled.

.PARAMETER Port
    The HTTP/WebSocket port the world is served on.

.PARAMETER Live
    The TCP port the profiler listens on for the Workbench. Omit for an unprofiled run.

.PARAMETER BindAddress
    The address the profiler port binds. Loopback when unset — a routable address publishes your telemetry.

.PARAMETER Trace
    Write a .typhon-trace capture here instead of the database's own profilings/ directory.

.PARAMETER Configuration
    Debug or Release. Release by default: the numbers in this repository are taken there.

.PARAMETER ServerArgs
    Everything else, passed through to the server verbatim (--hz, --pop, --interiors, --persist, …).

    It is declared FIRST, at Position 0, and that is load-bearing. Every other parameter here is implicitly positional,
    so when ServerArgs came last the pass-through tokens were bound to them before ServerArgs saw any: `--hz 50
    --interiors` put "--hz" in -BindAddress, "50" in -Trace and "--interiors" in -Configuration, where ValidateSet
    refused the call. Both documented examples failed that way until this was fixed.

.EXAMPLE
    ./scripts/swg-serve.ps1 -Port 5080 -Live 9100 --hz 50 --interiors
    Serves on 5080 with the profiler on 9100, then attach the Workbench to 127.0.0.1:9100.

.EXAMPLE
    ./scripts/swg-serve.ps1 -Port 5080 --hz 50
    An unprofiled run, with every profiler variable cleared whatever this shell was carrying.
#>
[CmdletBinding()]
param(
    # First, and positional, so it absorbs the pass-through instead of the typed parameters doing it. See .PARAMETER ServerArgs.
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $ServerArgs,
    [int] $Port = 5080,
    [int] $Live = 0,
    [string] $BindAddress,
    [string] $Trace,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "demo/SwgTatooine/bin/$Configuration/net10.0/SwgTatooine.exe"

# Always, not only when the exe is missing: building only when absent means an edited source file runs as its old binary,
# which is the "stale test bin" failure this repository has been bitten by before. msbuild's own up-to-date check makes a
# no-op build cheap.
Write-Host "Building SwgTatooine ($Configuration)..." -ForegroundColor Cyan
& dotnet build (Join-Path $repo 'demo/SwgTatooine/SwgTatooine.csproj') -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

# Set or clear, never leave alone — leaving them alone is what made a profiled shell contaminate every later run in it.
# ALL FOUR, including ENABLED, which this script never sets: the server treats it as a request, so a stale one keeps
# collection on and leaves this script's "unprofiled run" profiled anyway.
$env:TYPHON__PROFILER__LIVE = if ($Live -gt 0) { "$Live" } else { $null }
$env:TYPHON__PROFILER__BINDADDRESS = if ($BindAddress) { $BindAddress } else { $null }
$env:TYPHON__PROFILER__TRACE = if ($Trace) { $Trace } else { $null }
$env:TYPHON__PROFILER__ENABLED = $null

# Tells the server that the variables above are this run's, not the shell's history, so its startup line reports a source
# instead of warning about a stale variable. The engine never reads it.
$env:TYPHON_SWG_PROFILER_LAUNCHER = if ($Live -gt 0 -or $Trace) { 'scripts/swg-serve.ps1 -Live' } else { $null }

if ($Live -gt 0)
{
    $where = if ($BindAddress) { $BindAddress } else { '127.0.0.1' }
    Write-Host "Profiler: attach the Workbench to ${where}:$Live" -ForegroundColor Yellow
    if ($BindAddress -and $BindAddress -ne '127.0.0.1' -and $BindAddress -ne 'localhost')
    {
        Write-Host "  WARNING: $BindAddress is not loopback — this publishes your telemetry, unauthenticated." -ForegroundColor Red
    }
}

# A database of its own, under TEMP, so a launcher run never competes with a measurement run's database — and so the two
# launchers have one policy between them rather than one each.
$db = Join-Path ([System.IO.Path]::GetTempPath()) "swg-serve-$Port"
New-Item -ItemType Directory -Force $db | Out-Null

# The splat sends each element as one argv entry whatever it contains; `--%` would also stop $exe itself expanding.
# `Where-Object { $null -ne $_ }`, NOT `{ $_ }`: the latter is falsy for an empty string, so `--tag '' --hz 0` dropped the
# empty value and shifted everything after it, leaving --tag to eat --hz.
$all = @('--serve', "$Port", '--db-dir', $db) + @($ServerArgs | Where-Object { $null -ne $_ })
& $exe @all
exit $LASTEXITCODE
