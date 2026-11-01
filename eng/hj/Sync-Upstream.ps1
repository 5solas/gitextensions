<#
.SYNOPSIS
    Syncs Git Extensions topic branches and hj/main with upstream/master using patch-stack rebase.
.DESCRIPTION
    1. Validates working tree state and records current branch.
    2. Fetches upstream and origin remotes.
    3. Fast-forwards master to upstream/master.
    4. Rebases each topic branch from topics.txt onto master (tracks skipped commits, handles rerere).
    5. Recreates hj/main from master and merges each topic sequentially (--no-ff).
    6. Optionally runs dotnet build (/v:q).
    7. Optionally runs regression tests sequentially with trx logging and timeout monitoring.
    8. Summarizes changes (SHAs, commit counts, skipped commits, build/test results).
    9. Optionally pushes branches to origin (--force-with-lease for topics and hj/main).
   10. Restores original branch.
.PARAMETER Push
    Pushes master, topic branches, and hj/main to origin upon success.
.PARAMETER SkipTests
    Skips test execution.
.PARAMETER SkipBuild
    Skips dotnet build step.
.PARAMETER Topics
    Path to topics list file (default: eng/hj/topics.txt).
.PARAMETER DryRun
    Tests rebase and merge using temporary branches (tmp/sync-*) without altering real branches.
.PARAMETER AllowDirty
    Kept for compatibility; has no effect. The working tree must be clean (see the check below).
#>

[CmdletBinding()]
param(
    [switch]$Push,
    [switch]$SkipTests,
    [switch]$SkipBuild,
    [string]$Topics = "eng/hj/topics.txt",
    [switch]$DryRun,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve repository root
$repoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) {
    Write-Error "Not inside a git repository."
    exit 1
}
$repoRoot = $repoRoot.Trim()
Set-Location $repoRoot

# Resolve topics file path
if (-not [System.IO.Path]::IsPathRooted($Topics)) {
    $Topics = Join-Path $repoRoot $Topics
}
if (-not (Test-Path $Topics)) {
    Write-Error "Topics file not found at: $Topics"
    exit 1
}

# Read topic list (ignoring empty lines and comments)
$topicLines = @(Get-Content -Path $Topics | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($topicLines.Count -eq 0) {
    Write-Error "No topic branches specified in $Topics."
    exit 1
}

# -----------------------------------------------------------------------------
# Helper Functions
# -----------------------------------------------------------------------------

function Test-ConflictMarkersPresent([string]$FilePath) {
    if (-not (Test-Path $FilePath)) {
        return $false
    }
    # Check for conflict marker lines starting with <<<<<<<
    $match = Select-String -Path $FilePath -Pattern '^<{7}(\s|$)' -Quiet -ErrorAction SilentlyContinue
    return [bool]$match
}

function Resolve-RerereOrHaltRebase([string]$TopicName, [string]$OntoRef) {
    $gitDir = (git rev-parse --git-dir).Trim()

    while ($true) {
        $inRebase = (Test-Path (Join-Path $gitDir "rebase-merge")) -or (Test-Path (Join-Path $gitDir "rebase-apply"))
        if (-not $inRebase) {
            break
        }

        $unmergedFiles = @(git diff --name-only --diff-filter=U 2>$null | Where-Object { $_.Trim() })
        $rerereStatus = @(git rerere status 2>$null | Where-Object { $_.Trim() })

        $hasConflictMarkers = $false
        foreach ($file in $unmergedFiles) {
            if (Test-ConflictMarkersPresent $file) {
                $hasConflictMarkers = $true
                break
            }
        }

        # If no conflict markers remain and rerere has cleared or resolved them
        if (-not $hasConflictMarkers -and $rerereStatus.Count -eq 0) {
            Write-Host "[rerere] Automatically resolved conflicts for '$TopicName'. Continuing rebase..." -ForegroundColor Yellow
            & git add -A
            $env:GIT_EDITOR = 'true'
            $continueOutput = & git rebase --continue 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[rerere] Rebase completed successfully." -ForegroundColor Green
                break
            }
            continue
        }

        # Manual resolution required
        Write-Host ""
        Write-Host "=================================================================" -ForegroundColor Red
        Write-Host "CONFLICT DETECTED while rebasing '$TopicName' onto '$OntoRef'" -ForegroundColor Red
        Write-Host "=================================================================" -ForegroundColor Red
        Write-Host "Conflicting files:" -ForegroundColor Yellow
        $unmergedFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
        Write-Host ""
        Write-Host "Next steps:" -ForegroundColor Cyan
        Write-Host "  1. Resolve conflicts in the files listed above."
        Write-Host "  2. Stage resolved files: git add <file>"
        Write-Host "  3. Continue rebase:     git rebase --continue"
        Write-Host "  4. Re-run sync:         pwsh eng/hj/Sync-Upstream.ps1"
        Write-Host ""
        exit 2
    }
}

function Resolve-RerereOrHaltMerge([string]$TopicName) {
    $unmergedFiles = @(git diff --name-only --diff-filter=U 2>$null | Where-Object { $_.Trim() })
    $rerereStatus = @(git rerere status 2>$null | Where-Object { $_.Trim() })

    $hasConflictMarkers = $false
    foreach ($file in $unmergedFiles) {
        if (Test-ConflictMarkersPresent $file) {
            $hasConflictMarkers = $true
            break
        }
    }

    if (-not $hasConflictMarkers -and $rerereStatus.Count -eq 0) {
        Write-Host "[rerere] Automatically resolved merge conflicts for '$TopicName'. Committing..." -ForegroundColor Yellow
        & git add -A
        & git commit --no-edit
        if ($LASTEXITCODE -eq 0) {
            return
        }
    }

    Write-Host ""
    Write-Host "=================================================================" -ForegroundColor Red
    Write-Host "CONFLICT DETECTED while merging '$TopicName' into hj/main" -ForegroundColor Red
    Write-Host "=================================================================" -ForegroundColor Red
    Write-Host "Conflicting files:" -ForegroundColor Yellow
    $unmergedFiles | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Resolve conflicts in the files listed above."
    Write-Host "  2. Stage resolved files: git add <file>"
    Write-Host "  3. Commit merge:        git commit --no-edit"
    Write-Host "  4. Re-run sync:         pwsh eng/hj/Sync-Upstream.ps1"
    Write-Host ""
    exit 2
}

# -----------------------------------------------------------------------------
# Step A: Validate Working Tree & Record Current Branch
# -----------------------------------------------------------------------------

# The script checks out and rebases branches in this worktree, so it must start clean.
# It deliberately does not stash: the stash is shared by every worktree of the repository,
# and popping it could restore another worktree's changes here.
$didStash = $false
$statusOutput = @(git status --porcelain 2>$null | Where-Object { $_.Trim() })
$trackedModifications = @($statusOutput | Where-Object { -not $_.StartsWith('??') })

if ($trackedModifications.Count -gt 0) {
    Write-Error "Working tree has local modifications. Commit or revert them before running sync.`n$($trackedModifications -join "`n")"
    exit 1
}

$originalBranch = (git branch --show-current 2>$null).Trim()
if (-not $originalBranch) {
    $originalBranch = (git rev-parse HEAD 2>$null).Trim()
}

$createdTempBranches = [System.Collections.Generic.List[string]]::new()

try {
    # -------------------------------------------------------------------------
    # Step B: Fetch Remotes
    # -------------------------------------------------------------------------
    Write-Host "==> [1/6] Fetching remotes (upstream, origin)..." -ForegroundColor Cyan
    & git fetch upstream --prune --tags
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to fetch upstream remote."
        exit 1
    }
    & git fetch origin --prune
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to fetch origin remote."
        exit 1
    }

    # -------------------------------------------------------------------------
    # Step C: Fast-Forward master to upstream/master
    # -------------------------------------------------------------------------
    Write-Host "==> [2/6] Fast-forwarding master to upstream/master..." -ForegroundColor Cyan
    $masterBeforeSha = (git rev-parse master 2>$null).Trim()
    $upstreamMasterSha = (git rev-parse upstream/master 2>$null).Trim()

    & git merge-base --is-ancestor master upstream/master
    if ($LASTEXITCODE -ne 0) {
        Write-Error "master cannot be fast-forwarded to upstream/master. Direct commits may exist on master."
        exit 1
    }

    $hasUpstreamChanges = ($masterBeforeSha -ne $upstreamMasterSha)
    if (-not $hasUpstreamChanges) {
        Write-Host "    Upstream master has no new commits ($masterBeforeSha). [변경 없음]" -ForegroundColor Yellow
    } else {
        Write-Host "    Upstream master updated: $masterBeforeSha -> $upstreamMasterSha" -ForegroundColor Cyan
    }

    if (-not $DryRun) {
        if ($originalBranch -eq 'master') {
            & git merge --ff-only upstream/master
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to fast-forward checked out master branch."
                exit 1
            }
        } else {
            & git branch -f master upstream/master
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to update master branch pointer."
                exit 1
            }
        }
        $masterAfterSha = (git rev-parse master 2>$null).Trim()
    } else {
        $masterAfterSha = $upstreamMasterSha
        Write-Host "    [DryRun] master target SHA: $masterAfterSha" -ForegroundColor DarkGray
    }

    # -------------------------------------------------------------------------
    # Step D & E: Rebase Topic Branches onto master
    # -------------------------------------------------------------------------
    Write-Host "==> [3/6] Rebasing topic branches onto master..." -ForegroundColor Cyan
    $topicResults = [System.Collections.Generic.List[PSCustomObject]]::new()
    $rebaseOntoBase = if ($DryRun) { $upstreamMasterSha } else { "master" }

    foreach ($topic in $topicLines) {
        Write-Host "--- Processing topic: $topic" -ForegroundColor White
        $beforeSha = (git rev-parse $topic 2>$null).Trim()
        $beforeCount = [int](git rev-list --count "$rebaseOntoBase..$topic" 2>$null).Trim()

        # Identify commits that upstream has already adopted (git cherry)
        $cherryLines = @(git cherry $rebaseOntoBase $topic 2>$null)
        $skippedCommits = [System.Collections.Generic.List[string]]::new()
        foreach ($cLine in $cherryLines) {
            if ($cLine -match '^-\s+([0-9a-fA-F]+)') {
                $cSha = $Matches[1]
                $cSummary = (git log -1 --format="%h %s" $cSha 2>$null).Trim()
                $skippedCommits.Add($cSummary)
            }
        }

        if ($skippedCommits.Count -gt 0) {
            Write-Host "    Upstream already adopted $($skippedCommits.Count) commit(s) (will be skipped):" -ForegroundColor Yellow
            foreach ($s in $skippedCommits) {
                Write-Host "      - $s" -ForegroundColor Yellow
            }
        }

        $targetBranch = $topic
        if ($DryRun) {
            $safeName = $topic -replace '[^a-zA-Z0-9_\-]', '-'
            $targetBranch = "tmp/sync-$safeName"
            & git branch -f $targetBranch $topic
            $createdTempBranches.Add($targetBranch)
            Write-Host "    [DryRun] Created test branch '$targetBranch' for rebase" -ForegroundColor DarkGray
            & git checkout $targetBranch
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to checkout temporary test branch '$targetBranch'."
                exit 1
            }
        }

        # Perform rebase onto master / target upstream
        & git rebase $rebaseOntoBase $targetBranch
        if ($LASTEXITCODE -ne 0) {
            # Handle conflicts or rerere
            Resolve-RerereOrHaltRebase -TopicName $targetBranch -OntoRef $rebaseOntoBase
        }

        $afterSha = (git rev-parse $targetBranch 2>$null).Trim()
        $afterCount = [int](git rev-list --count "$rebaseOntoBase..$targetBranch" 2>$null).Trim()

        # Check if topic is now empty (identical to master / upstream)
        $isAdopted = ($afterSha -eq $upstreamMasterSha)
        if ($isAdopted) {
            Write-Warning "Topic '$topic' is fully adopted upstream - consider removing it from $Topics"
        }

        $topicStatus = "Rebased"
        if ($isAdopted) {
            $topicStatus = "Adopted (Empty)"
        } elseif ($beforeSha -eq $afterSha) {
            $topicStatus = "Up to date (변경 없음)"
        }

        $topicResults.Add([PSCustomObject]@{
            Topic              = $topic
            TargetBranch       = $targetBranch
            BeforeSha          = $beforeSha
            AfterSha           = $afterSha
            BeforeCommitCount  = $beforeCount
            AfterCommitCount   = $afterCount
            SkippedCommits     = $skippedCommits.ToArray()
            IsFullyAdopted     = $isAdopted
            Status             = $topicStatus
        })
    }

    # -------------------------------------------------------------------------
    # Step F: Recreate hj/main and Merge Topic Branches
    # -------------------------------------------------------------------------
    Write-Host "==> [4/6] Recreating hj/main and merging topics..." -ForegroundColor Cyan

    $mainBranch = if ($DryRun) { "tmp/sync-hj-main" } else { "hj/main" }
    $mainBase = if ($DryRun) { $upstreamMasterSha } else { "master" }

    if ($DryRun) {
        & git branch -f $mainBranch $mainBase
        $createdTempBranches.Add($mainBranch)
    } else {
        # Check if hj/main is checked out elsewhere or locally
        if ($originalBranch -eq 'hj/main') {
            & git reset --hard master
        } else {
            & git branch -f $mainBranch master
        }
    }

    & git checkout $mainBranch
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to checkout $mainBranch."
        exit 1
    }

    foreach ($tr in $topicResults) {
        if ($tr.IsFullyAdopted) {
            Write-Host "    Skipping fully adopted topic '$($tr.Topic)' from merge" -ForegroundColor DarkGray
            continue
        }

        Write-Host "    Merging '$($tr.TargetBranch)' into $mainBranch (--no-ff)..." -ForegroundColor White
        & git merge --no-ff --no-edit $tr.TargetBranch
        if ($LASTEXITCODE -ne 0) {
            Resolve-RerereOrHaltMerge -TopicName $tr.TargetBranch
        }
    }

    $newHjMainSha = (git rev-parse $mainBranch 2>$null).Trim()

    # -------------------------------------------------------------------------
    # Step G: Build Verification
    # -------------------------------------------------------------------------
    $buildSuccess = $true
    if (-not $SkipBuild) {
        Write-Host "==> [5/6] Building solution (dotnet build /v:q)..." -ForegroundColor Cyan
        & dotnet build /v:q
        if ($LASTEXITCODE -ne 0) {
            Write-Error "dotnet build failed with exit code $LASTEXITCODE."
            $buildSuccess = $false
            exit 1
        }
        Write-Host "    Build succeeded (0 errors)." -ForegroundColor Green
    } else {
        Write-Host "==> [5/6] Build skipped (-SkipBuild)." -ForegroundColor DarkGray
    }

    # -------------------------------------------------------------------------
    # Step H: Regression Tests Execution
    # -------------------------------------------------------------------------
    $testResults = [System.Collections.Generic.List[PSCustomObject]]::new()
    $allTestsPassed = $true

    if (-not $SkipTests) {
        Write-Host "==> [6/6] Running regression tests sequentially..." -ForegroundColor Cyan
        Write-Host "    NOTE: Test assemblies synchronize globally via named semaphore 'GitExtensionsTestAssemblySerializer'." -ForegroundColor DarkGray

        $suites = @(
            @{
                Name    = "GitUI.Tests (Ghostty)"
                Project = "tests/app/UnitTests/GitUI.Tests"
                Filter  = "FullyQualifiedName~Ghostty"
                LogFile = "sync-GitUI.trx"
            },
            @{
                Name    = "GitCommands.Tests"
                Project = "tests/app/UnitTests/GitCommands.Tests"
                Filter  = $null
                LogFile = "sync-GitCommands.trx"
            },
            @{
                Name    = "UI.IntegrationTests (Budget)"
                Project = "tests/app/IntegrationTests/UI.IntegrationTests"
                Filter  = "FullyQualifiedName~FormBrowseGitProcessBudgetTests"
                LogFile = "sync-FormBrowseGitProcessBudget.trx"
            },
            @{
                Name    = "UI.IntegrationTests (GhosttyTab)"
                Project = "tests/app/IntegrationTests/UI.IntegrationTests"
                Filter  = "FullyQualifiedName~GhosttyConsoleTab"
                LogFile = "sync-GhosttyConsoleTab.trx"
            },
            @{
                Name    = "UI.IntegrationTests (FormBrowse)"
                Project = "tests/app/IntegrationTests/UI.IntegrationTests"
                Filter  = "FullyQualifiedName~FormBrowseTests"
                LogFile = "sync-FormBrowse.trx"
            }
        )

        foreach ($s in $suites) {
            $projPath = Join-Path $repoRoot $s.Project
            $trxName = $s.LogFile
            $cmdArgs = @("test", $projPath, "--logger", "trx;LogFileName=$trxName", "-v:q")
            if ($s.Filter) {
                $cmdArgs += @("--filter", $s.Filter)
            }

            Write-Host "    Running suite: $($s.Name)..." -ForegroundColor White

            # Clean stale trx files
            Get-ChildItem -Path $repoRoot -Recurse -Filter $trxName -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

            $proc = Start-Process -FilePath "dotnet" -ArgumentList $cmdArgs -PassThru -NoNewWindow
            $completed = $proc.WaitForExit(900000) # 15 minutes timeout

            if (-not $completed) {
                Write-Error "Test suite '$($s.Name)' timed out after 15 minutes. Killing process tree..."
                & taskkill /PID $proc.Id /T /F 2>$null
                $allTestsPassed = $false
                $testResults.Add([PSCustomObject]@{
                    Name     = $s.Name
                    Total    = 0
                    Passed   = 0
                    Failed   = 1
                    Skipped  = 0
                    Status   = "TimedOut"
                    TrxPath  = ""
                })
                continue
            }

            # Locate produced trx file
            $foundTrx = Get-ChildItem -Path $repoRoot -Recurse -Filter $trxName -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $foundTrx) {
                $status = if ($proc.ExitCode -eq 0) { "Passed" } else { "Failed" }
                if ($proc.ExitCode -ne 0) { $allTestsPassed = $false }
                $testResults.Add([PSCustomObject]@{
                    Name     = $s.Name
                    Total    = 0
                    Passed   = 0
                    Failed   = if ($proc.ExitCode -eq 0) { 0 } else { 1 }
                    Skipped  = 0
                    Status   = $status
                    TrxPath  = ""
                })
                continue
            }

            # Parse trx
            [xml]$trxXml = Get-Content -Path $foundTrx.FullName
            $counters = $trxXml.TestRun.ResultSummary.Counters
            $totalCount = [int]$counters.total
            $passedCount = [int]$counters.passed
            $failedCount = [int]$counters.failed
            $skippedCount = [int]($counters.notExecuted + $counters.skipped)

            # Inspect failed tests for known environment issues
            $failedNodes = @($trxXml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
            $unexpectedFailures = [System.Collections.Generic.List[string]]::new()
            foreach ($fn in $failedNodes) {
                $tName = $fn.testName
                if ($tName -match 'RunBatchCommand_can_handle_max_length_arguments') {
                    Write-Warning "Known environment failure: RunBatchCommand_can_handle_max_length_arguments (path length limit)"
                } else {
                    $unexpectedFailures.Add($tName)
                }
            }

            $suiteStatus = "Passed"
            if ($unexpectedFailures.Count -gt 0) {
                $suiteStatus = "Failed"
                $allTestsPassed = $false
            } elseif ($failedCount -gt 0) {
                $suiteStatus = "Passed (known warning)"
            }

            $testResults.Add([PSCustomObject]@{
                Name     = $s.Name
                Total    = $totalCount
                Passed   = $passedCount
                Failed   = $failedCount
                Skipped  = $skippedCount
                Status   = $suiteStatus
                TrxPath  = $foundTrx.FullName
            })
        }
    } else {
        Write-Host "==> [6/6] Regression tests skipped (-SkipTests)." -ForegroundColor DarkGray
    }

    # -------------------------------------------------------------------------
    # Step I: Result Summary
    # -------------------------------------------------------------------------
    Write-Host ""
    Write-Host "===============================================================================" -ForegroundColor Green
    Write-Host "                            UPSTREAM SYNC SUMMARY" -ForegroundColor Green
    Write-Host "===============================================================================" -ForegroundColor Green
    Write-Host "Master Branch:" -ForegroundColor White
    Write-Host "  Previous SHA : $masterBeforeSha"
    Write-Host "  New SHA      : $masterAfterSha"
    if (-not $hasUpstreamChanges) {
        Write-Host "  Upstream Sync: 변경 없음 (Already up-to-date with upstream/master)" -ForegroundColor Yellow
    } else {
        Write-Host "  Upstream Sync: $masterBeforeSha -> $masterAfterSha" -ForegroundColor Green
    }
    Write-Host ""
    Write-Host "Topic Branches:" -ForegroundColor White
    $topicSummaryView = $topicResults | Select-Object @{N="Topic";E={$_.Topic}},
        @{N="Before SHA";E={$_.BeforeSha.Substring(0, [System.Math]::Min(9, $_.BeforeSha.Length))}},
        @{N="After SHA";E={$_.AfterSha.Substring(0, [System.Math]::Min(9, $_.AfterSha.Length))}},
        @{N="Commits";E={"$($_.BeforeCommitCount) -> $($_.AfterCommitCount)"}},
        @{N="Skipped (Adopted)";E={$_.SkippedCommits.Count}},
        @{N="Status";E={$_.Status}}
    $topicSummaryView | Format-Table -AutoSize | Out-String | Write-Host

    $allSkipped = @($topicResults | ForEach-Object { $_.SkippedCommits } | Where-Object { $_ })
    if ($allSkipped.Count -gt 0) {
        Write-Host "Skipped (Upstream-Adopted) Commits:" -ForegroundColor Yellow
        foreach ($s in $allSkipped) {
            Write-Host "  - $s" -ForegroundColor Yellow
        }
        Write-Host ""
    }

    Write-Host "hj/main Integration Branch:" -ForegroundColor White
    Write-Host "  New SHA      : $newHjMainSha"
    Write-Host ""

    if (-not $SkipBuild) {
        Write-Host "Build: Passed" -ForegroundColor Green
    }

    if (-not $SkipTests) {
        Write-Host ""
        Write-Host "Test Execution Results:" -ForegroundColor White
        $testResults | Format-Table Name, Total, Passed, Failed, Skipped, Status -AutoSize | Out-String | Write-Host
        Write-Host "TRX Result Files:" -ForegroundColor DarkGray
        foreach ($tr in $testResults) {
            if ($tr.TrxPath) {
                Write-Host "  - $($tr.Name): $($tr.TrxPath)" -ForegroundColor DarkGray
            }
        }
    }

    if (-not $allTestsPassed) {
        Write-Error "One or more test suites failed."
        exit 1
    }

    # -------------------------------------------------------------------------
    # Step J: Push to Origin
    # -------------------------------------------------------------------------
    if ($Push) {
        if ($DryRun) {
            Write-Host "[DryRun] -Push specified but skipped due to -DryRun mode." -ForegroundColor DarkGray
        } else {
            Write-Host ""
            Write-Host "==> Pushing updated branches to origin..." -ForegroundColor Cyan

            Write-Host "    Pushing master to origin..." -ForegroundColor White
            & git push origin master
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to push master to origin."
                exit 1
            }

            foreach ($tr in $topicResults) {
                $tName = $tr.Topic
                Write-Host "    Pushing $tName with force-with-lease..." -ForegroundColor White
                & git push "--force-with-lease=${tName}:origin/${tName}" origin "$tName"
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "Failed to push $tName to origin."
                    exit 1
                }
            }

            Write-Host "    Pushing hj/main with force-with-lease..." -ForegroundColor White
            & git push "--force-with-lease=hj/main:origin/hj/main" origin hj/main
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to push hj/main to origin."
                exit 1
            }

            Write-Host "==> All branches successfully pushed to origin." -ForegroundColor Green
        }
    }

} finally {
    # -------------------------------------------------------------------------
    # Step K & L: Cleanup Temporary Branches & Restore Original Branch
    # -------------------------------------------------------------------------
    if ($createdTempBranches.Count -gt 0) {
        # Switch away before deleting temporary branches
        & git checkout $originalBranch 2>$null
        foreach ($tb in $createdTempBranches) {
            & git branch -D $tb 2>$null
        }
    }

    # Only restore original branch if we are not stalled in an active rebase/merge conflict
    $gitDir = (git rev-parse --git-dir 2>$null)
    if ($gitDir) {
        $gitDir = $gitDir.Trim()
        $inRebase = (Test-Path (Join-Path $gitDir "rebase-merge")) -or (Test-Path (Join-Path $gitDir "rebase-apply"))
        $inMerge = Test-Path (Join-Path $gitDir "MERGE_HEAD")

        if (-not $inRebase -and -not $inMerge) {
            $curr = (git branch --show-current 2>$null).Trim()
            if ($curr -ne $originalBranch) {
                Write-Host "Restoring original branch '$originalBranch'..." -ForegroundColor DarkGray
                & git checkout $originalBranch 2>$null
            }
        }
    }

}
