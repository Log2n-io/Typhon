# typhon-dev — run the LOCAL CLI build as if it were the installed `typhon` dotnet tool.
#
# Publishes src/Typhon.Shell to artifacts/typhon-dev, then registers a global `typhon-dev` function that forwards
# every argument to that build. Deliberately NOT named `typhon`, so the globally-installed tool
# (`dotnet tool install -g Typhon.Cli`) keeps working untouched and you can compare the two side by side.
#
#   . scripts/typhon-dev.ps1          # publish + register (Debug)
#   . scripts/typhon-dev.ps1 -NoBuild # re-register only (fast, e.g. in a fresh shell)
#   typhon-dev ui --open-latest       # then use it exactly like `typhon`
#
# WHY PUBLISH, NOT BUILD — this is the whole reason the script exists. `typhon ui` serves the Workbench SPA from
# `AppContext.BaseDirectory/wwwroot` (resolved explicitly so the packaged tool doesn't inherit the caller's CWD as its
# content root). That wwwroot is injected by the `_IncludeWorkbenchSpaInPublish` target in Typhon.Shell.csproj, which
# hooks `ComputeResolvedFilesToPublishList` — a PUBLISH-only target. So `bin/Debug/net10.0/typhon.dll` has no wwwroot
# next to it and `typhon ui` answers a bare 404 on `/` with no warning of any kind. A plain `dotnet build` output is
# NOT a usable CLI. (The `Typhon.Workbench.staticwebassets.runtime.json` left in the build output is a red herring —
# it only feeds `dotnet run` from the project directory, and WorkbenchHost never consults it.)

[CmdletBinding()]
param(
    # Build configuration to publish. Debug is the fast dev loop; Release mirrors what ships.
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # Skip the publish and just (re-)register the function against whatever is already in artifacts/typhon-dev.
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir '..')).Path
$Csproj = Join-Path $RepoRoot 'src/Typhon.Shell/Typhon.Shell.csproj'
$OutDir = Join-Path $RepoRoot 'artifacts/typhon-dev'
$Dll = Join-Path $OutDir 'typhon.dll'

if (-not $NoBuild) {
    Write-Host "==> Publishing the Typhon CLI ($Configuration) -> $OutDir"
    dotnet publish $Csproj -c $Configuration -o $OutDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $Dll)) {
    throw "typhon.dll not found at $Dll. Run without -NoBuild to publish it first."
}

# Fail loudly here rather than letting `typhon ui` 404 later — this is the exact trap the header describes.
$Wwwroot = Join-Path $OutDir 'wwwroot'
if (-not (Test-Path $Wwwroot)) {
    Write-Warning "No wwwroot in $OutDir - 'typhon-dev ui' will return 404 on '/'."
    Write-Warning "The Workbench SPA is missing. Build it once: scripts/workbench-bootstrap.ps1 (or npm run build in tools/Typhon.Workbench/ClientApp), then re-run this script."
}

# Registered in the GLOBAL scope so the function survives whether the script is dot-sourced (`. scripts/typhon-dev.ps1`)
# or invoked normally (`scripts/typhon-dev.ps1`) — a plain `function typhon-dev` would die with the script's own scope.
# Re-running is idempotent: re-declaring a function replaces it outright, so repeated calls just repoint it at the
# fresh publish. The DLL path is baked in at registration time (not resolved per call), so the function keeps working
# from any working directory — which matters, since `typhon-dev ui --open-db` resolves its target from YOUR cwd.
$FunctionBody = "dotnet `"$Dll`" @args"
Set-Item -Path 'function:global:typhon-dev' -Value ([scriptblock]::Create($FunctionBody)) -Force

Write-Host ""
Write-Host "typhon-dev registered -> $Dll"
Write-Host "  typhon-dev --version"
Write-Host "  typhon-dev ui --open-db          # browse the database in the current directory"
Write-Host "  typhon-dev ui --open-latest      # open the newest ./captures/*.typhon-trace"
Write-Host ""
Write-Host "Only for THIS session. To make it permanent, add to `$PROFILE:"
Write-Host "  function typhon-dev { dotnet `"$Dll`" @args }"
