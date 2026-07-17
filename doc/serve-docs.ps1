<#
.SYNOPSIS
  Build the Typhon DocFX site and host it locally.

.DESCRIPTION
  One command to preview the docs in a browser. Two build tiers:

    * Fast (default when the engine isn't built, or with -Fast): re-renders only the
      markdown against the already-generated doc/api/ref. Seconds; no engine build.
      This is the right tier while writing docs.

    * Full (default when src/Typhon.Shell is built, or with -BuildEngine): stages the
      public assemblies + XML from the Shell bin and regenerates the API reference from
      them, then builds the site. Needed when the API surface or XML docs changed.
      Delegates to scripts/build-docs.ps1 so the staging logic lives in one place.

  After building, it serves doc/_site over HTTP (blocks until Ctrl+C).

  Why the API comes from built assemblies, not source: Typhon's public attributes
  (TraceEvent, BeginParam, ...) are source-generated and invisible to DocFX's own Roslyn
  compile, so DocFX reads the DLLs MSBuild already produced.

.PARAMETER Config
  Debug (default) or Release — which src/Typhon.Shell bin to stage the API from (full build only).

.PARAMETER Port
  HTTP port for the local server. Default 8080.

.PARAMETER Fast
  Force the markdown-only build (reuse existing doc/api/ref; skip staging + metadata).

.PARAMETER BuildEngine
  Run `dotnet build src/Typhon.Shell -c <Config>` first, then a full API build. Use this
  after changing public API or XML doc comments.

.PARAMETER NoServe
  Build only; do not start the server (parity with scripts/build-docs.ps1).

.PARAMETER NoBuild
  Skip building; serve the existing doc/_site as-is.

.PARAMETER Open
  Open the site in the default browser once the server starts.

.PARAMETER Help
  Show this help (synopsis, parameters, and examples) and exit without building or serving.

.EXAMPLE
  ./doc/serve-docs.ps1
  # Auto-picks full build if the engine is built, else fast; hosts at http://localhost:8080

.EXAMPLE
  ./doc/serve-docs.ps1 -Fast -Open
  # Quick markdown rebuild, host, and open the browser

.EXAMPLE
  ./doc/serve-docs.ps1 -BuildEngine -Config Release
  # Rebuild the engine + regenerate the API reference, then host

.EXAMPLE
  ./doc/serve-docs.ps1 -NoBuild
  # Just host whatever is already in doc/_site
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Debug",
    [int]$Port = 8080,
    [switch]$Fast,
    [switch]$BuildEngine,
    [switch]$NoServe,
    [switch]$NoBuild,
    [switch]$Open,
    [switch]$Help
)

# -Help: render the comment-based help above and exit, before touching anything else.
if ($Help)
{
    Get-Help $PSCommandPath -Detailed
    return
}

$ErrorActionPreference = "Stop"

# Resolve the repo root from this script's location (doc/serve-docs.ps1 -> repo is one up),
# so the script works no matter where it's invoked from.
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$siteDir  = Join-Path $repo "doc/_site"
$apiRef   = Join-Path $repo "doc/api/ref"
$docfxCfg = "doc/docfx.json"

# --- Preconditions -----------------------------------------------------------------------
# DocFX is a dotnet global tool in this repo; `dotnet docfx` invokes it (matches build-docs.ps1).
try
{
    $null = & dotnet docfx --version 2>$null
    if ($LASTEXITCODE -ne 0) { throw }
}
catch
{
    Write-Error "docfx not found. Install it once with:  dotnet tool install -g docfx"
}

# --- Build -------------------------------------------------------------------------------
if (-not $NoBuild)
{
    $shellBin = "src/Typhon.Shell/bin/$Config/net10.0"

    # Decide the tier. Explicit flags win; otherwise auto-detect from whether the engine is built.
    $useFast = $false
    if ($Fast)
    {
        $useFast = $true
    }
    elseif ($BuildEngine)
    {
        Write-Host "Building src/Typhon.Shell ($Config) for a fresh API reference..." -ForegroundColor Cyan
        & dotnet build src/Typhon.Shell -c $Config
        if ($LASTEXITCODE -ne 0) { Write-Error "engine build failed ($LASTEXITCODE)" }
    }
    elseif (-not (Test-Path $shellBin))
    {
        Write-Warning "src/Typhon.Shell is not built ($shellBin missing) — doing a markdown-only build against the existing API reference."
        Write-Warning "Run with -BuildEngine (or 'dotnet build src/Typhon.Shell -c $Config') to regenerate the API pages from code."
        $useFast = $true
    }

    if ($useFast)
    {
        if (-not (Test-Path $apiRef))
        {
            Write-Warning "doc/api/ref is missing — API reference pages and their xrefs will be unresolved."
            Write-Warning "Do one full build first:  ./doc/serve-docs.ps1 -BuildEngine"
        }
        Write-Host "Fast build: rendering markdown into doc/_site (reusing doc/api/ref)..." -ForegroundColor Cyan
        & dotnet docfx build $docfxCfg
        if ($LASTEXITCODE -ne 0) { Write-Error "docfx build failed ($LASTEXITCODE)" }
    }
    else
    {
        # Full build: stage assemblies + regenerate metadata + build site. Reuse the canonical script.
        Write-Host "Full build via scripts/build-docs.ps1 ($Config)..." -ForegroundColor Cyan
        & (Join-Path $repo "scripts/build-docs.ps1") $Config
        if ($LASTEXITCODE -ne 0) { Write-Error "full docs build failed ($LASTEXITCODE)" }
    }

    Write-Host "Built doc/_site" -ForegroundColor Green
}

# --- Serve -------------------------------------------------------------------------------
if (-not $NoServe)
{
    if (-not (Test-Path $siteDir))
    {
        Write-Error "doc/_site not found — build first (drop -NoBuild)."
    }

    $url = "http://localhost:$Port"
    Write-Host ""
    Write-Host "Serving $siteDir at $url  (Ctrl+C to stop)" -ForegroundColor Green

    if ($Open)
    {
        # Fire the browser just before serve blocks; it'll connect once the server is up.
        Start-Process $url
    }

    & dotnet docfx serve doc/_site --port $Port
}
