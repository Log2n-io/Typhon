#Requires -Version 7
<#
.SYNOPSIS
    Wire the nested claude/ docs repo into a JetBrains Air agent worktree. Invoked by .air/worktree.json.

.DESCRIPTION
    Air creates each agent worktree with a plain `git worktree add` against the master workspace, which
    produces a checkout with NO claude/ — the docs are a separate repo (Log2n-io/Typhon-Claude) living at
    <repo>/claude and gitignored by the parent. On a documentation-first project every docs-first read then
    resolves against whatever claude/ the agent can reach, pinned to some other branch, with nothing warning
    you. This is the same hole scripts/new-worktree.ps1 exists to close, for the worktrees Air makes itself.

    Only the docs half of that procedure is needed here: Air has already fetched, branched and added the
    parent worktree. The docs repo is attached as a *worktree* of an existing clone, never a fresh network
    clone, so its objects live in a shared store and unpushed doc commits survive the agent worktree being
    deleted. If no clone can be found, one is made once in the master workspace and reused from then on.

    Never fails the caller: a broken docs wiring degrades the session, an aborted launch kills it. Problems
    are reported and the exit code stays 0 unless -Strict is passed.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/air-worktree-setup.ps1
    pwsh -NoProfile -File ./scripts/air-worktree-setup.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [string] $DocsSource  = $env:TYPHON_DOCS_SOURCE,
    [string] $DocsFrom    = 'origin/main',
    [string] $Branch,
    [string] $BuildTarget = 'test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj',
    [switch] $NoBuild,
    [switch] $Strict
)

$ErrorActionPreference = 'Stop'

$DocsRemoteUrl = 'https://github.com/Log2n-io/Typhon-Claude.git'
$DocsSentinel  = 'CLAUDE.md'      # must exist in the docs checkout for it to count as populated

# ---------------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------------

function Invoke-Git
{
    param([string[]] $Arguments, [switch] $AllowFail)

    $raw  = & git @Arguments 2>&1
    $code = $LASTEXITCODE
    $text = @($raw | ForEach-Object { "$_" })

    if ($code -ne 0 -and -not $AllowFail)
    {
        throw "git $($Arguments -join ' ') failed (exit $code):`n$($text -join "`n")"
    }

    return [pscustomobject]@{ Ok = ($code -eq 0); Lines = $text; Text = ($text -join "`n") }
}

function Get-BranchOf
{
    param([string] $RepoPath)

    $r = Invoke-Git @('-C', $RepoPath, 'rev-parse', '--abbrev-ref', 'HEAD') -AllowFail
    if (-not $r.Ok) { return '?' }
    return $r.Lines[0]
}

function Test-LocalBranch
{
    param([string] $RepoPath, [string] $BranchName)
    return (Invoke-Git @('-C', $RepoPath, 'show-ref', '--verify', '--quiet', "refs/heads/$BranchName") -AllowFail).Ok
}

function Test-Ref
{
    param([string] $RepoPath, [string] $Ref)
    return (Invoke-Git @('-C', $RepoPath, 'rev-parse', '--verify', '--quiet', "$Ref^{commit}") -AllowFail).Ok
}

function Get-MasterRoot
{
    # --git-common-dir resolves to the MASTER workspace's .git even from inside an Air worktree, so the
    # shared docs clone is looked for next to the checkout Air actually branches from.
    param([string] $WorktreeRoot)

    $r = Invoke-Git @('-C', $WorktreeRoot, 'rev-parse', '--path-format=absolute', '--git-common-dir') -AllowFail
    if (-not $r.Ok) { return $null }
    return (Resolve-Path (Split-Path -Parent ($r.Lines[0] -replace '/', '\'))).Path
}

function Split-Remote
{
    # 'origin/main' -> @('origin', 'main'); a bare commit-ish -> @($null, $null) so we skip the fetch.
    param([string] $Ref)

    $i = $Ref.IndexOf('/')
    if ($i -lt 1) { return @($null, $null) }
    return @($Ref.Substring(0, $i), $Ref.Substring($i + 1))
}

function Resolve-DocsSource
{
    # Preference order: explicit env/param, the master workspace's own clone, then a one-off clone made
    # there. Cloning into the master workspace rather than the agent worktree is what makes every later
    # agent worktree a free `worktree add` against a warm object store.
    param([string] $Candidate, [string] $MasterRoot)

    foreach ($path in @($Candidate, (Join-Path $MasterRoot 'claude')))
    {
        if ($path -and (Test-Path (Join-Path $path '.git')))
        {
            return [pscustomobject]@{ Path = (Resolve-Path $path).Path; Created = $false }
        }
    }

    if (-not $MasterRoot) { throw 'no docs clone found and the master workspace root could not be resolved' }

    $target = Join-Path $MasterRoot 'claude'
    Write-Host "      no docs clone found — cloning $DocsRemoteUrl into the master workspace (one-off)"
    Invoke-Git @('clone', '--quiet', $DocsRemoteUrl, $target) | Out-Null
    return [pscustomobject]@{ Path = (Resolve-Path $target).Path; Created = $true }
}

# ---------------------------------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------------------------------

$wtRoot   = Split-Path -Parent $PSScriptRoot
$docsPath = Join-Path $wtRoot 'claude'

try
{
    if (Test-Path (Join-Path $docsPath $DocsSentinel))
    {
        Write-Host "[skip] docs already present at $docsPath (branch $(Get-BranchOf $docsPath))"
        return
    }

    if (-not $Branch) { $Branch = Get-BranchOf $wtRoot }
    if ($Branch -in @('?', 'HEAD'))
    {
        throw "the agent worktree is not on a named branch (HEAD is '$Branch') — cannot mirror it in the docs repo"
    }

    $masterRoot = Get-MasterRoot $wtRoot
    $docs       = Resolve-DocsSource -Candidate $DocsSource -MasterRoot $masterRoot
    $docsMain   = $docs.Path

    Write-Host "[1/3] docs source: $docsMain"

    # A worktree deleted by Air without cleanup leaves a stale registration that would block re-adding the
    # same path or branch. Pruning is cheap and purely local.
    Invoke-Git @('-C', $docsMain, 'worktree', 'prune') -AllowFail | Out-Null

    $remote, $remoteBranch = Split-Remote $DocsFrom
    if ($remote -and -not $docs.Created)
    {
        Invoke-Git @('-C', $docsMain, 'fetch', $remote, $remoteBranch) -AllowFail | Out-Null
    }

    # Offline or a renamed default branch must not sink the whole setup — degrade to the local default,
    # then to whatever the docs clone has checked out.
    $start = $DocsFrom
    if (-not (Test-Ref $docsMain $start)) { $start = 'main' }
    if (-not (Test-Ref $docsMain $start)) { $start = 'HEAD' }

    if (Test-Path $docsPath) { Remove-Item -Recurse -Force $docsPath }

    Write-Host "[2/3] docs worktree add -> $Branch (off $start)"
    if (Test-LocalBranch $docsMain $Branch)
    {
        Invoke-Git @('-C', $docsMain, 'worktree', 'add', $docsPath, $Branch) | Out-Null
    }
    else
    {
        # --no-track: without it git infers upstream=origin/main from the start point, which makes
        # `git status` read "ahead of origin/main" and turns a reflexive `git pull` into a merge of main
        # into the agent's branch.
        Invoke-Git @('-C', $docsMain, 'worktree', 'add', '--no-track', '-b', $Branch, $docsPath, $start) | Out-Null
    }

    $problems = @()
    if (-not (Test-Path (Join-Path $docsPath $DocsSentinel))) { $problems += "docs checkout is missing $DocsSentinel" }
    if ((Get-BranchOf $docsPath) -ne $Branch)                 { $problems += "docs repo is on $(Get-BranchOf $docsPath), expected $Branch" }
    if ($problems.Count -gt 0)
    {
        throw "docs worktree created but failed verification:`n  - $($problems -join "`n  - ")"
    }

    $prewarm = 'skipped'
    if (-not $NoBuild -and $env:TYPHON_AIR_PREWARM -ne '0')
    {
        # A fresh worktree has no obj/ or bin/, so the agent's first build is fully cold. Backgrounded:
        # Air blocks the session on this script, and the agent has reading to do before it needs a binary.
        $log = Join-Path $wtRoot 'air-prewarm.log'
        try
        {
            Start-Process -FilePath 'dotnet' -WindowStyle Hidden -WorkingDirectory $wtRoot `
                -ArgumentList @('build', (Join-Path $wtRoot $BuildTarget), '-c', 'Debug') `
                -RedirectStandardOutput $log -RedirectStandardError "$log.err" | Out-Null
            $prewarm = "running in background -> $log"
        }
        catch
        {
            $prewarm = "could not start ($($_.Exception.Message))"
        }
    }

    Write-Host "[3/3] pre-warm build: $prewarm"
    Write-Host ''
    Write-Host "docs     : $docsPath (worktree of $docsMain)"
    Write-Host "branch   : $Branch  (both repos, no upstream — first push needs -u)"
}
catch
{
    Write-Host ''
    Write-Host '################################################################################'
    Write-Host '# claude/ docs repo NOT wired up for this worktree.'
    Write-Host "# $($_.Exception.Message)"
    Write-Host '# Every docs-first read in this session will find nothing, or the wrong branch.'
    Write-Host '# Fix: pwsh -NoProfile -File ./scripts/air-worktree-setup.ps1 -Strict'
    Write-Host '################################################################################'
    if ($Strict) { exit 1 }
}
