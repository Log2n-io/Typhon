<#
.SYNOPSIS
    The one place that sets or clears the profiler environment for a SWG launcher.

.DESCRIPTION
    Dot-source this from a launcher. It exists because the contract was duplicated in two scripts and promptly drifted:
    swg-serve.ps1 cleared TYPHON__PROFILER__TRACE and swg-client.ps1 did not, so `swg-serve -Trace x` followed by
    `swg-client` in one shell still traced — and neither cleared TYPHON__PROFILER__ENABLED, which the server treats as a
    request. A review found that, and the duplication is what produced it.

    The contract, in full:

      - SET every variable the caller asked for, and CLEAR every one it did not. Never leave one alone. A `$env:`
        assignment persists for a whole shell session, so a variable set once to attach the Workbench keeps profiling —
        and skewing — every later run in that shell. `$env:X = $null` genuinely removes the variable in PowerShell 7.
      - Record WHICH launcher did it, so the server's startup line can credit it instead of warning about a variable the
        operator just asked for.

    It deliberately does not decide anything: the server cannot switch its own profiler on (the engine bakes its
    telemetry gate before Main runs, see demo/SwgTatooine/Sim/ProfilerArgs.cs), so this is the only layer that can, and
    it keeps no policy of its own beyond set-or-clear.
#>

# No `Set-StrictMode` here, deliberately. This file is DOT-SOURCED, so a strictness change would apply to the caller's
# whole scope and not just to this function — which is not a library's business, and is not theoretical: setting it broke
# an unrelated caller's epilogue the first time this was dot-sourced into a live session.

function Set-SwgProfilerEnv
{
    <#
    .SYNOPSIS
        Sets or clears every TYPHON__PROFILER__* variable this repository's launchers use.

    .PARAMETER Live
        The TCP port the profiler listens on for the Workbench. 0 or absent means no live exporter.

    .PARAMETER BindAddress
        The address that port binds. Loopback when absent — a routable address publishes telemetry with no auth.

    .PARAMETER Trace
        A .typhon-trace capture path, or absent for the database's own profilings/ directory.

    .PARAMETER Launcher
        The name the server should credit, e.g. 'scripts/swg-serve.ps1 -Live'. Only used for one line of output.
    #>
    [CmdletBinding()]
    param(
        [int] $Live = 0,
        [string] $BindAddress,
        [string] $Trace,
        [Parameter(Mandatory = $true)][string] $Launcher
    )

    $wanted = ($Live -gt 0) -or $Trace

    $env:TYPHON__PROFILER__LIVE = if ($Live -gt 0) { "$Live" } else { $null }
    $env:TYPHON__PROFILER__BINDADDRESS = if ($BindAddress) { $BindAddress } else { $null }
    $env:TYPHON__PROFILER__TRACE = if ($Trace) { $Trace } else { $null }

    # Never set by a launcher, always cleared by one: the server counts it as a request, so a stale one would keep an
    # "unprofiled" run profiled while every other variable said otherwise.
    $env:TYPHON__PROFILER__ENABLED = $null

    $env:TYPHON_SWG_PROFILER_LAUNCHER = if ($wanted) { $Launcher } else { $null }

    if ($Live -gt 0)
    {
        $where = if ($BindAddress) { $BindAddress } else { '127.0.0.1' }
        Write-Host "  profiler: attach the Workbench to ${where}:$Live" -ForegroundColor Yellow
        if ($BindAddress -and $BindAddress -notin @('127.0.0.1', 'localhost', '::1'))
        {
            Write-Host "  WARNING: $BindAddress is not loopback - this publishes your telemetry, unauthenticated." -ForegroundColor Red
        }
    }
}
