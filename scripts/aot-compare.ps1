#!/usr/bin/env pwsh
# aot-compare.ps1 — build Typhon.Aot.Demo two ways (JIT and Native AOT) and report the deltas.
#
# The demo asserts its own correctness, so a run that prints RESULT: PASS has verified the engine end to end
# (schema registration, MVCC, WAL, indexed/scan/spatial queries, tick DAG, durability) in that configuration.
#
#   ./scripts/aot-compare.ps1                  # 300 ticks  — warm-up-dominated regime
#   ./scripts/aot-compare.ps1 -Ticks 3000      # 3000 ticks — steady state; the honest throughput comparison
#   ./scripts/aot-compare.ps1 -Runs 5
#
# Native publish needs the VS C++ build tools; ILC shells out to vswhere.exe, so on Git Bash / minimal shells add
#   C:\Program Files (x86)\Microsoft Visual Studio\Installer
# to PATH first.
#
# Why two tick counts matter: a short run measures how fast the process gets going (AOT wins — no tier-0), a long
# run measures generated-code quality once tiered JIT + dynamic PGO have fully engaged (JIT wins — AOT has no
# profile feedback). Reporting only one of them would misrepresent the trade. #409

[CmdletBinding()]
param(
    [int]$Ticks = 300,
    [int]$Runs = 3,
    [string]$Rid = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'samples/Typhon.Aot.Demo/Typhon.Aot.Demo.csproj'
$outDir = Join-Path $repo 'artifacts/aot-compare'
$jitDir = Join-Path $outDir 'jit'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host '== building JIT variant ==' -ForegroundColor Cyan
# PublishAot=true stamps IsDynamicCodeSupported=false into runtimeconfig.json even for a plain build, which would
# make the "JIT" baseline run with dynamic code disabled — same JIT, but exercising the AOT branches. Force it off
# so the baseline is a genuine, unconstrained CoreCLR run.
dotnet build $proj -c $Configuration -p:PublishAot=false -p:IsAotCompatible=false --output $jitDir | Out-Null

Write-Host '== publishing Native AOT variant ==' -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r $Rid | Out-Null
$aotExe = Join-Path $repo "samples/Typhon.Aot.Demo/bin/$Configuration/net10.0/$Rid/publish/Typhon.Aot.Demo.exe"
if (-not (Test-Path $aotExe)) { throw "native binary not found at $aotExe" }
Write-Host ("native binary: {0:N1} MB" -f ((Get-Item $aotExe).Length / 1MB))

function Invoke-Variant {
    param([string]$Tag, [string[]]$Cmd)
    $samples = @()
    for ($i = 0; $i -lt $Runs; $i++) {
        $json = Join-Path $outDir "$Tag-$i.json"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        & $Cmd[0] ($Cmd[1..($Cmd.Length - 1)] + @('--json', $json, '--ticks', "$Ticks",
            '--dir', (Join-Path ([IO.Path]::GetTempPath()) "typhon-aot-$Tag"))) | Out-Null
        $sw.Stop()
        if ($LASTEXITCODE -ne 0) { throw "$Tag run $i FAILED (exit $LASTEXITCODE) — an assertion did not hold" }
        $r = Get-Content $json -Raw | ConvertFrom-Json
        $samples += [pscustomobject]@{
            WallMs = $sw.Elapsed.TotalMilliseconds
            TotalMs = $r.totalMs
            P50 = $r.tickP50Ms
            P99 = $r.tickP99Ms
            Rss = $r.peakWorkingSetMb
            Thr = $r.entitiesPerTick / $r.tickP50Ms * 1000 / 1e6
        }
    }
    $median = { param($v) ($v | Sort-Object)[[int]($v.Count / 2)] }
    [pscustomobject]@{
        Wall = & $median ($samples.WallMs); Total = & $median ($samples.TotalMs)
        P50 = & $median ($samples.P50); P99 = & $median ($samples.P99)
        Rss = & $median ($samples.Rss); Thr = & $median ($samples.Thr)
    }
}

Write-Host "== running $Runs x $Ticks ticks ==" -ForegroundColor Cyan
$jit = Invoke-Variant 'jit' @('dotnet', (Join-Path $jitDir 'Typhon.Aot.Demo.dll'))
$aot = Invoke-Variant 'aot' @($aotExe)

Write-Host ''
Write-Host ("{0,-28}{1,12}{2,12}{3,13}" -f 'metric', 'JIT', 'AOT', 'AOT vs JIT')
Write-Host ('-' * 65)
foreach ($m in @(
    @{ Name = 'process wall clock (ms)'; Key = 'Wall'; F = 'N1' },
    @{ Name = 'runtime startup (ms)';    Key = 'Startup'; F = 'N1' },
    @{ Name = 'tick p50 (ms)';           Key = 'P50';  F = 'N3' },
    @{ Name = 'tick p99 (ms)';           Key = 'P99';  F = 'N3' },
    @{ Name = 'throughput (M ent/s)';    Key = 'Thr';  F = 'N1' },
    @{ Name = 'peak working set (MB)';   Key = 'Rss';  F = 'N1' })) {
    if ($m.Key -eq 'Startup') { $j = $jit.Wall - $jit.Total; $a = $aot.Wall - $aot.Total }
    else { $j = $jit.($m.Key); $a = $aot.($m.Key) }
    Write-Host ("{0,-28}{1,12}{2,12}{3,12}" -f $m.Name, $j.ToString($m.F), $a.ToString($m.F),
        ('{0:+0.0;-0.0}%' -f (($a - $j) / $j * 100)))
}
Write-Host ''
Write-Host 'Both variants PASSED every correctness assertion (a failure would have thrown above).' -ForegroundColor Green
