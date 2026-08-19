#Requires -Version 7
<#
.SYNOPSIS
    Detach the nested claude/ docs worktree before Air deletes an agent worktree. Invoked by .air/worktree.json.

.DESCRIPTION
    Air removes the agent worktree directory itself. What it cannot know about is the docs repo registered
    inside it: deleting the folder leaves a stale worktree entry in the shared docs clone, which then blocks
    re-adding the same path or branch later. So this unregisters it first, then prunes.

    Work is never destroyed here. Commits live in the shared object store and the branch is never deleted,
    so anything committed stays reachable from the docs clone after the folder is gone — that is the whole
    reason the docs repo is attached as a worktree rather than cloned. Uncommitted edits are the exception:
    they die with the directory Air is about to delete regardless, so they are reported rather than blocked
    on, and the recovery command is printed.

    Never fails the caller: a failed teardown step must not leave Air unable to retire the session.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/air-worktree-cleanup.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

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

$wtRoot   = Split-Path -Parent $PSScriptRoot
$docsPath = Join-Path $wtRoot 'claude'

try
{
    $gitLink = Join-Path $docsPath '.git'

    if (-not (Test-Path $gitLink))
    {
        Write-Host "[skip] no docs repo at $docsPath — nothing to detach"
        return
    }

    if (Test-Path $gitLink -PathType Container)
    {
        # An independent clone, not a worktree: its objects exist nowhere else, so unregistering is
        # meaningless and deleting it would be the only copy. Leave it entirely to Air.
        Write-Host "[skip] $docsPath is an independent clone, not a worktree — left untouched"
        return
    }

    $branch = (Invoke-Git @('-C', $docsPath, 'rev-parse', '--abbrev-ref', 'HEAD') -AllowFail).Lines[0]
    $head   = (Invoke-Git @('-C', $docsPath, 'rev-parse', '--short', 'HEAD') -AllowFail).Lines[0]

    $common   = (Invoke-Git @('-C', $docsPath, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Lines[0]
    $docsMain = (Resolve-Path (Split-Path -Parent ($common -replace '/', '\'))).Path

    $dirty = @((Invoke-Git @('-C', $docsPath, 'status', '--porcelain') -AllowFail).Lines | Where-Object { $_ })
    if ($dirty.Count -gt 0)
    {
        Write-Host "WARNING: $($dirty.Count) uncommitted docs change(s) will be lost with the worktree directory:"
        $dirty | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
    }

    $unpushed = 0
    if ($branch -and $branch -ne 'HEAD')
    {
        $r = Invoke-Git @('-C', $docsPath, 'rev-list', '--count', $branch, '--not', '--remotes') -AllowFail
        if ($r.Ok) { $unpushed = [int]($r.Lines[0]) }
    }

    Invoke-Git @('-C', $docsMain, 'worktree', 'remove', '--force', $docsPath) -AllowFail | Out-Null
    Invoke-Git @('-C', $docsMain, 'worktree', 'prune') -AllowFail | Out-Null

    Write-Host "detached: $docsPath (worktree of $docsMain), list pruned"

    if ($unpushed -gt 0)
    {
        Write-Host "$unpushed docs commit(s) on '$branch' are on no remote, but the branch is kept and the objects are shared:"
        Write-Host "  git -C $docsMain log $branch        # still readable"
        Write-Host "  git -C $docsMain push -u origin $branch"
    }
    elseif ($branch -and $branch -ne 'HEAD')
    {
        Write-Host "branch '$branch' ($head) kept in $docsMain — delete with: git -C $docsMain branch -D $branch"
    }
}
catch
{
    Write-Host "WARNING: docs worktree cleanup failed: $($_.Exception.Message)"
    Write-Host 'A stale registration may remain — recover with: git -C <docs-clone> worktree prune'
}
