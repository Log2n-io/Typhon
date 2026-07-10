<#
.SYNOPSIS
  Native PowerShell twin of scripts/build-docs.sh — for local dev on Windows.
  (CI on Linux runs the .sh; this exists so you don't need Git Bash / WSL locally.)

  Builds the DocFX site into doc/_site: cleans generated output, stages the API
  assemblies from the built Shell bin, then runs the all-in-one `docfx`.

.PARAMETER Config
  Debug (default) or Release. Must match a prior `dotnet build src/Typhon.Shell -c <Config>`.

.EXAMPLE
  dotnet build src/Typhon.Shell -c Debug
  .\scripts\build-docs.ps1 Debug
  dotnet docfx serve doc/_site --port 8080
#>
param([string]$Config = "Debug")
$ErrorActionPreference = "Stop"

# Run from the repo root regardless of where the script is invoked from.
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# --- Clean generated output (best-effort: a running `docfx serve` may hold _site) ---
Remove-Item -Recurse -Force doc/obj      -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force doc/_site    -ErrorAction SilentlyContinue
New-Item    -ItemType Directory -Force doc/_site | Out-Null
Remove-Item -Recurse -Force doc/api/ref  -ErrorAction SilentlyContinue
# Purge stray pre-refactor flat api/*.yml (keep the hand-authored toc.yml) + .manifest.
Get-ChildItem doc/api -Filter *.yml -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'toc.yml' } | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item -Force doc/api/.manifest -ErrorAction SilentlyContinue

# --- Stage the public assemblies + XML docs DocFX reads (mirrors stage-api-bin.sh) ---
# From the APP (Shell) bin: it carries the 4 public DLLs + XML AND every transitive dep,
# so DocFX resolves all references. API is read from BUILT assemblies (not recompiled)
# because Typhon's public attributes are source-generated (invisible to DocFX's Roslyn).
$src = "src/Typhon.Shell/bin/$Config/net10.0"
if (-not (Test-Path $src)) {
    Write-Error "Not found: $src`nBuild first:  dotnet build src/Typhon.Shell -c $Config"
}
$dest = "doc/.api-bin"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$src/*.dll" $dest -Force
foreach ($a in 'Typhon.Engine','Typhon.Profiler','Typhon.Protocol','Typhon.Schema.Definition') {
    Copy-Item "$src/$a.xml" $dest -Force
}

# --- Build (metadata into doc/api/ref + site into doc/_site) ---
dotnet docfx doc/docfx.json
if ($LASTEXITCODE -ne 0) { Write-Error "docfx build failed ($LASTEXITCODE)" }
Write-Host "built doc/_site"
