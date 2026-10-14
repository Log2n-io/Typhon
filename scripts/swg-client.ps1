<#
.SYNOPSIS
    Builds the SWG Tatooine 3D client and serves the live simulation behind it, in one command.

.DESCRIPTION
    The client is a Vite app and `dotnet build` does not run Vite, so a fresh checkout has a server with nothing to
    serve. This builds what is missing, starts the server, and prints the URL.

    The page connects to the engine over `/ws` on the origin that served it (`05-client.md` C4). Append `?source=mock`
    to the URL for the browser-side mock instead, which needs no server and is the only source with a latency slider.

.PARAMETER Port
    The port to serve on. Default 8080.

.PARAMETER Pop
    Population multiplier. 1.0 is the full planet (~17 700 entities); 0.2 is a comfortable default for looking at.

.PARAMETER GodRegion
    The god camera's largest region edge, metres. This is what gives the session a client-driven region of interest and
    a far-tier aggregate grid; 0 serves the whole planet to every camera and declares no grid.

    Bounded by the replication cell: at the demo's 64 m cell the engine refuses anything over 3072 m, and says so
    precisely. 3000 is the largest round number that fits.

.PARAMETER ServerArgs
    Everything else, passed through to the server verbatim (--hz, --interiors, --persist, …).

    Declared FIRST, at Position 0, for the reason swg-serve.ps1 documents: every other parameter here is implicitly
    positional, so a pass-through token offered anywhere else binds to one of them. Without this parameter at all,
    `--hz 25` bound "--hz" to -Port and failed with "Cannot convert value "--hz" to type "System.Int32"" — which names
    the parameter it landed on and not the one you wanted, and reads as a bug in the flag rather than a missing feature.

.PARAMETER Rebuild
    Rebuild the client even when `dist` is already there.

.PARAMETER Live
    The TCP port to serve the profiler on, so the Workbench can attach while you watch the world. Omit for an
    unprofiled run — and note that omitting it CLEARS the profiler variables rather than leaving them alone, because a
    `$env:` assignment persists for a whole shell session and would otherwise keep profiling every later run in it.

    It has to be set here rather than passed to the server, because the server cannot switch its own profiler on: the
    engine's master gate is baked before `Main` runs. `demo/SwgTatooine/Sim/ProfilerArgs.cs` has the mechanism.

.PARAMETER BindAddress
    The address the profiler port binds. Loopback when unset — a routable address publishes your telemetry.

.EXAMPLE
    ./scripts/swg-client.ps1 --hz 25 -Pop 2.0
    Serves at 25 Hz with ten times the default population. Pass-through flags come FIRST.

.EXAMPLE
    ./scripts/swg-client.ps1
    ./scripts/swg-client.ps1 -Port 8090 -Pop 1.0
    ./scripts/swg-client.ps1 -Live 9100          # then attach the Workbench to 127.0.0.1:9100
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]] $ServerArgs = @(),
    [int]    $Port        = 8080,
    [double] $Pop         = 0.2,
    [double] $GodRegion   = 3000,
    [int]    $Live        = 0,
    [string] $BindAddress,
    [switch] $Rebuild
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'demo/SwgTatooine.Client'
$exe    = Join-Path $root 'demo/SwgTatooine/bin/Release/net10.0/SwgTatooine.exe'

# $ErrorActionPreference='Stop' does NOT cover a native command's exit code here: $PSNativeCommandUseErrorActionPreference
# is False on PowerShell 7.6 by default, so npm and dotnet failures fall through and the stale exe runs. Checked by hand.
if (-not (Test-Path (Join-Path $client 'node_modules'))) {
    Write-Host 'Installing client dependencies (npm ci)...' -ForegroundColor Cyan
    Push-Location $client
    try { npm ci; if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" } } finally { Pop-Location }
}

if ($Rebuild -or -not (Test-Path (Join-Path $client 'dist/index.html'))) {
    Write-Host 'Building the client (vite build)...' -ForegroundColor Cyan
    Push-Location $client
    try { npm run build; if ($LASTEXITCODE -ne 0) { throw "vite build failed with exit code $LASTEXITCODE" } } finally { Pop-Location }
}

# Always, not only when absent: see swg-serve.ps1. An edited source file otherwise runs as its old binary.
Write-Host 'Building the server (Release)...' -ForegroundColor Cyan
dotnet build (Join-Path $root 'demo/SwgTatooine/SwgTatooine.csproj') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "server build failed with exit code $LASTEXITCODE" }

# Its own database directory, created here: the demo VALIDATES --db-dir and refuses one that does not exist rather than
# creating it. Under TEMP so nothing here competes with a measurement run's database.
$db = Join-Path ([System.IO.Path]::GetTempPath()) "swg-client-$Port"
New-Item -ItemType Directory -Force $db | Out-Null

# Set or clear, never leave alone — see -Live. The marker only changes the server's startup wording from "a variable you
# may not know about" to "the launcher did this"; the engine never reads it.
# The same shared implementation swg-serve.ps1 uses; see lib/swg-profiler-env.ps1 for the contract and why it is shared.
. (Join-Path $PSScriptRoot 'lib/swg-profiler-env.ps1')
Set-SwgProfilerEnv -Live $Live -BindAddress $BindAddress -Launcher 'scripts/swg-client.ps1 -Live'

Write-Host ''
Write-Host "  http://localhost:$Port" -ForegroundColor Green
Write-Host "  http://localhost:$Port/?source=mock   (the browser-side mock, no engine)" -ForegroundColor DarkGray
Write-Host ''

& $exe --serve $Port --pop $Pop --god-region $GodRegion --db-dir $db @ServerArgs
exit $LASTEXITCODE
