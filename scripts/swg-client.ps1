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

.PARAMETER Rebuild
    Rebuild the client even when `dist` is already there.

.EXAMPLE
    ./scripts/swg-client.ps1
    ./scripts/swg-client.ps1 -Port 8090 -Pop 1.0
#>
[CmdletBinding()]
param(
    [int]    $Port      = 8080,
    [double] $Pop       = 0.2,
    [double] $GodRegion = 3000,
    [switch] $Rebuild
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'demo/SwgTatooine.Client'
$exe    = Join-Path $root 'demo/SwgTatooine/bin/Release/net10.0/SwgTatooine.exe'

if (-not (Test-Path (Join-Path $client 'node_modules'))) {
    Write-Host 'Installing client dependencies (npm ci)...' -ForegroundColor Cyan
    Push-Location $client
    try { npm ci } finally { Pop-Location }
}

if ($Rebuild -or -not (Test-Path (Join-Path $client 'dist/index.html'))) {
    Write-Host 'Building the client (vite build)...' -ForegroundColor Cyan
    Push-Location $client
    try { npm run build } finally { Pop-Location }
}

if (-not (Test-Path $exe)) {
    Write-Host 'Building the server (Release)...' -ForegroundColor Cyan
    dotnet build (Join-Path $root 'demo/SwgTatooine/SwgTatooine.csproj') -c Release
}

# Its own database directory, created here: the demo VALIDATES --db-dir and refuses one that does not exist rather than
# creating it. Under TEMP so nothing here competes with a measurement run's database.
$db = Join-Path $env:TEMP "swg-client-$Port"
New-Item -ItemType Directory -Force $db | Out-Null

Write-Host ''
Write-Host "  http://localhost:$Port" -ForegroundColor Green
Write-Host "  http://localhost:$Port/?source=mock   (the browser-side mock, no engine)" -ForegroundColor DarkGray
Write-Host ''

& $exe --serve $Port --pop $Pop --god-region $GodRegion --db-dir $db
