#Requires -Version 7
<#
.SYNOPSIS
    Create / remove / list Typhon dev worktrees with the nested claude/ docs repo wired up correctly.

.DESCRIPTION
    Typhon is a multi-repo workspace: the docs live in a separate clone of log2n-io/typhon-claude that
    sits at <repo>/claude and is gitignored by the parent. A bare `git worktree add` therefore produces a
    worktree with NO docs, and every docs-first read silently falls back to the main repo's copy — pinned
    to a different branch than the code being edited. This script makes the full procedure one command.

    By default the docs repo is attached as a *worktree* of the existing <repo>/claude clone rather than a
    fresh network clone, so unpushed doc commits live in the shared object store and survive a rushed
    cleanup. Use -DocsClone to force an independent clone instead.

.EXAMPLE
    ./scripts/new-worktree.ps1 db-repair
    ./scripts/new-worktree.ps1 db-repair -Branch fix/db-repair -NoBuild
    ./scripts/new-worktree.ps1 -List
    ./scripts/new-worktree.ps1 db-repair -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string] $Name,
    [string] $Branch,
    [string] $From        = 'origin/main',
    [string] $DocsFrom    = 'origin/main',
    [string] $BuildTarget = 'test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj',
    [switch] $List,
    [switch] $Remove,
    [switch] $DocsClone,
    [switch] $NoBuild,
    [switch] $Force,
    [switch] $Help
)

$ErrorActionPreference = 'Stop'

$DocsRemoteUrl = 'https://github.com/log2n-io/typhon-claude.git'
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

function Get-MainRoot
{
    # --git-common-dir resolves to the MAIN repo's .git even when this script is invoked from inside a
    # worktree copy, so worktree creation is always anchored on the primary checkout.
    $common = (Invoke-Git @('-C', $PSScriptRoot, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Lines[0]
    $common = $common -replace '/', '\'
    return (Resolve-Path (Split-Path -Parent $common)).Path
}

function Test-LocalBranch
{
    param([string] $RepoPath, [string] $BranchName)
    return (Invoke-Git @('-C', $RepoPath, 'show-ref', '--verify', '--quiet', "refs/heads/$BranchName") -AllowFail).Ok
}

function Get-UnpushedCount
{
    # Commits on $BranchName not contained in ANY remote-tracking ref. Purely local — no network.
    param([string] $RepoPath, [string] $BranchName)

    $r = Invoke-Git @('-C', $RepoPath, 'rev-list', '--count', $BranchName, '--not', '--remotes') -AllowFail
    if (-not $r.Ok) { return 0 }
    return [int]($r.Lines[0])
}

function Get-DocsMode
{
    # worktree => .git is a file pointing into the shared object store; clone => .git is a directory.
    param([string] $DocsPath)

    if (-not (Test-Path $DocsPath))                    { return 'missing' }
    $git = Join-Path $DocsPath '.git'
    if (-not (Test-Path $git))                         { return 'not-a-repo' }
    if (Test-Path $git -PathType Leaf)                 { return 'worktree' }
    return 'clone'
}

function Get-BranchOf
{
    param([string] $RepoPath)

    $r = Invoke-Git @('-C', $RepoPath, 'rev-parse', '--abbrev-ref', 'HEAD') -AllowFail
    if (-not $r.Ok) { return '?' }
    return $r.Lines[0]
}

function Split-Remote
{
    # 'origin/main' -> @('origin', 'main'); a bare commit-ish -> @($null, $null) so we skip the fetch.
    param([string] $Ref)

    $i = $Ref.IndexOf('/')
    if ($i -lt 1) { return @($null, $null) }
    return @($Ref.Substring(0, $i), $Ref.Substring($i + 1))
}

function Show-Usage
{
    Write-Host @'
new-worktree.ps1 — Typhon worktree lifecycle (parent repo + nested claude/ docs repo)

  ./scripts/new-worktree.ps1 <name> [options]      create
  ./scripts/new-worktree.ps1 <name> -Remove        remove (refuses to discard work without -Force)
  ./scripts/new-worktree.ps1 -List                 inventory, with docs-repo mode per worktree

Options
  -Branch <b>       branch for BOTH repos            (default: feature/<name>)
  -From <ref>       parent start point               (default: origin/main, fetched first)
  -DocsFrom <ref>   docs start point                 (default: origin/main, fetched first)
  -DocsClone        independent clone of the docs repo instead of a worktree of <repo>/claude
  -NoBuild          skip the background pre-warm build
  -BuildTarget <p>  project to pre-warm              (default: the engine test project)
  -Force            allow -Remove to discard dirty / unpushed work
'@
}

# ---------------------------------------------------------------------------------------------------
# actions
# ---------------------------------------------------------------------------------------------------

function Invoke-List
{
    param([string] $MainRoot)

    $docsMain = Join-Path $MainRoot 'claude'
    $rows     = @()
    $current  = $null

    foreach ($line in (Invoke-Git @('-C', $MainRoot, 'worktree', 'list', '--porcelain')).Lines)
    {
        if ($line -like 'worktree *')
        {
            $current = [pscustomobject]@{ Path = ($line.Substring(9) -replace '/', '\'); Branch = '(detached)'; Docs = '-' }
            $rows   += $current
        }
        elseif ($line -like 'branch *' -and $current)
        {
            $current.Branch = $line.Substring(7) -replace '^refs/heads/', ''
        }
    }

    foreach ($row in $rows)
    {
        if ($row.Path -eq $MainRoot) { $row.Docs = "primary ($(Get-DocsMode $docsMain))"; continue }
        $docsPath = Join-Path $row.Path 'claude'
        $mode     = Get-DocsMode $docsPath
        $row.Docs = if ($mode -in @('worktree', 'clone')) { "$mode @ $(Get-BranchOf $docsPath)" } else { $mode.ToUpper() }
    }

    $rows | Format-Table -AutoSize -Property @(
        @{ Label = 'Worktree'; Expression = { Split-Path -Leaf $_.Path } }
        @{ Label = 'Branch';   Expression = { $_.Branch } }
        @{ Label = 'Docs';     Expression = { $_.Docs } }
    )

    $broken = @($rows | Where-Object { $_.Docs -in @('MISSING', 'NOT-A-REPO') })
    if ($broken.Count -gt 0)
    {
        Write-Host "WARNING: $($broken.Count) worktree(s) have no docs checkout — doc reads there resolve against the main repo." -ForegroundColor Yellow
    }
}

function Invoke-Create
{
    param([string] $MainRoot, [string] $Name, [string] $Branch)

    $wtPath   = Join-Path $MainRoot ".claude\worktrees\$Name"
    $docsMain = Join-Path $MainRoot 'claude'
    $docsPath = Join-Path $wtPath 'claude'

    if (Test-Path $wtPath)                       { throw "Worktree path already exists: $wtPath" }
    if (Test-LocalBranch $MainRoot $Branch)      { throw "Branch '$Branch' already exists in the parent repo — pick another -Branch or delete it first." }

    # --- parent repo ---------------------------------------------------------------------------
    $remote, $remoteBranch = Split-Remote $From
    if ($remote)
    {
        Write-Host "[1/4] fetch $remote/$remoteBranch"
        Invoke-Git @('-C', $MainRoot, 'fetch', $remote, $remoteBranch) | Out-Null
    }

    Write-Host "[2/4] worktree add $Name -> $Branch (off $From)"
    # --no-track: without it git infers upstream=origin/main from the start point, which makes `git status`
    # read "ahead of origin/main" and turns a reflexive `git pull` into a merge of main into the feature branch.
    Invoke-Git @('-C', $MainRoot, 'worktree', 'add', '--no-track', '-b', $Branch, $wtPath, $From) | Out-Null

    # --- docs repo -----------------------------------------------------------------------------
    $docsMode = 'clone'
    $useWorktree = (-not $DocsClone) -and (Test-Path (Join-Path $docsMain '.git'))

    if ($useWorktree)
    {
        $dRemote, $dRemoteBranch = Split-Remote $DocsFrom
        try
        {
            if ($dRemote)
            {
                Invoke-Git @('-C', $docsMain, 'fetch', $dRemote, $dRemoteBranch) | Out-Null
            }

            Write-Host "[3/4] docs worktree add -> $Branch (off $DocsFrom)"
            if (Test-LocalBranch $docsMain $Branch)
            {
                Invoke-Git @('-C', $docsMain, 'worktree', 'add', $docsPath, $Branch) | Out-Null
            }
            else
            {
                Invoke-Git @('-C', $docsMain, 'worktree', 'add', '--no-track', '-b', $Branch, $docsPath, $DocsFrom) | Out-Null
            }
            $docsMode = 'worktree'
        }
        catch
        {
            Write-Host "      docs worktree failed ($($_.Exception.Message.Split("`n")[0])) — falling back to a clone" -ForegroundColor Yellow
            if (Test-Path $docsPath) { Remove-Item -Recurse -Force $docsPath }
            $useWorktree = $false
        }
    }

    if (-not $useWorktree)
    {
        Write-Host "[3/4] docs clone -> $Branch"
        Invoke-Git @('clone', '--quiet', $DocsRemoteUrl, $docsPath) | Out-Null
        Invoke-Git @('-C', $docsPath, 'checkout', '--quiet', '-b', $Branch) | Out-Null
        $docsMode = 'clone'
    }

    # --- verify --------------------------------------------------------------------------------
    $problems = @()
    if (-not (Test-Path (Join-Path $docsPath $DocsSentinel))) { $problems += "docs checkout is missing $DocsSentinel" }
    if ((Get-BranchOf $wtPath)   -ne $Branch)                 { $problems += "parent worktree is on $(Get-BranchOf $wtPath), expected $Branch" }
    if ((Get-BranchOf $docsPath) -ne $Branch)                 { $problems += "docs repo is on $(Get-BranchOf $docsPath), expected $Branch" }
    if ($problems.Count -gt 0)
    {
        throw "Worktree created but failed verification:`n  - $($problems -join "`n  - ")"
    }

    # --- pre-warm build ------------------------------------------------------------------------
    $prewarm = 'skipped (-NoBuild)'
    if (-not $NoBuild)
    {
        $logDir = Join-Path $MainRoot '.claude\state'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $log = Join-Path $logDir "worktree-prewarm-$Name.log"
        try
        {
            $proj = Join-Path $wtPath $BuildTarget
            Start-Process -FilePath 'dotnet' -WindowStyle Hidden -WorkingDirectory $wtPath `
                -ArgumentList @('build', $proj, '-c', 'Debug') `
                -RedirectStandardOutput $log -RedirectStandardError "$log.err" | Out-Null
            $prewarm = "running in background -> $log"
        }
        catch
        {
            $prewarm = "could not start ($($_.Exception.Message))"
        }
    }

    Write-Host "[4/4] pre-warm build: $prewarm"

    $bashPath = $wtPath -replace '\\', '/'
    Write-Host ''
    Write-Host "worktree : $wtPath"
    Write-Host "branch   : $Branch  (both repos, no upstream — first push needs -u)"
    Write-Host "docs     : $docsMode"
    Write-Host ''
    Write-Host 'Now cd BOTH shells (they persist independently; do NOT use EnterWorktree here):'
    Write-Host "  bash: cd $bashPath"
    Write-Host "  pwsh: Set-Location $wtPath"
}

function Invoke-Remove
{
    param([string] $MainRoot, [string] $Name)

    $wtPath   = Join-Path $MainRoot ".claude\worktrees\$Name"
    $docsMain = Join-Path $MainRoot 'claude'
    $docsPath = Join-Path $wtPath 'claude'

    if (-not (Test-Path $wtPath)) { throw "No such worktree: $wtPath" }

    $docsMode = Get-DocsMode $docsPath
    $risks    = @()

    foreach ($pair in @(@{ P = $wtPath; L = 'parent' }, @{ P = $docsPath; L = 'docs' }))
    {
        if (-not (Test-Path (Join-Path $pair.P '.git'))) { continue }

        $dirty = (Invoke-Git @('-C', $pair.P, 'status', '--porcelain') -AllowFail).Lines | Where-Object { $_ }
        if ($dirty) { $risks += "$($pair.L): $(@($dirty).Count) uncommitted change(s)" }

        $branchName = Get-BranchOf $pair.P
        if ($branchName -ne 'HEAD')
        {
            $unpushed = Get-UnpushedCount $pair.P $branchName
            if ($unpushed -gt 0) { $risks += "$($pair.L): $unpushed commit(s) on '$branchName' not on any remote" }
        }
    }

    # An independent clone is the only copy of its objects — losing it loses the commits outright.
    if ($docsMode -eq 'clone' -and $risks.Count -gt 0)
    {
        $risks += 'docs is an independent CLONE, so unpushed docs commits are unrecoverable once deleted'
    }

    if ($risks.Count -gt 0 -and -not $Force)
    {
        Write-Host "Refusing to remove '$Name':" -ForegroundColor Yellow
        $risks | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
        Write-Host 'Push or stash first, or re-run with -Force to discard.'
        return
    }

    if ($docsMode -eq 'worktree')
    {
        $gitArgs = @('-C', $docsMain, 'worktree', 'remove', $docsPath)
        if ($Force) { $gitArgs += '--force' }
        Invoke-Git $gitArgs | Out-Null
    }
    elseif ($docsMode -ne 'missing')
    {
        Remove-Item -Recurse -Force $docsPath
    }

    $gitArgs = @('-C', $MainRoot, 'worktree', 'remove', $wtPath)
    if ($Force) { $gitArgs += '--force' }
    Invoke-Git $gitArgs | Out-Null

    Invoke-Git @('-C', $MainRoot, 'worktree', 'prune') | Out-Null
    if (Test-Path (Join-Path $docsMain '.git')) { Invoke-Git @('-C', $docsMain, 'worktree', 'prune') | Out-Null }

    Write-Host "removed: $wtPath (docs mode was '$docsMode'); both worktree lists pruned."
    Write-Host "Branches are NOT deleted — 'git -C $MainRoot branch -D <branch>' if you want them gone."
}

# ---------------------------------------------------------------------------------------------------
# entry point
# ---------------------------------------------------------------------------------------------------

if ($Help -or (-not $List -and -not $Name))
{
    Show-Usage
    return
}

$mainRoot = Get-MainRoot

if ($List)
{
    Invoke-List -MainRoot $mainRoot
    return
}

if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$')
{
    throw "Invalid worktree name '$Name' — use letters, digits, dot, dash, underscore."
}

if ($Remove)
{
    Invoke-Remove -MainRoot $mainRoot -Name $Name
    return
}

if (-not $Branch) { $Branch = "feature/$Name" }
Invoke-Create -MainRoot $mainRoot -Name $Name -Branch $Branch
