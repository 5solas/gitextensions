<#
.SYNOPSIS
    Monitors upstream/master for changes and verifies if the Git Extensions patch stack applies cleanly.
.DESCRIPTION
    1. Re-executes from a temporary copy to prevent file-locking issues during branch switching.
    2. Validates working tree cleanliness and records the starting branch.
    3. Resolves BaseRef (default: origin/master) and UpstreamRef (default: upstream/master).
    4. If upstream is up-to-date: logs status, closes any open watcher GitHub issue, and exits 0.
    5. If upstream has new commits:
       - Computes commit count and extracts one-line logs (up to 30 commits).
       - Analyzes file overlaps between upstream commits and each topic branch.
       - Performs dry-run rebases of each topic branch on isolated temporary branches.
       - Records clean rebases, skipped/adopted commits, or conflicts with file lists.
       - Performs dry-run integration merge into a temporary hj/main branch.
       - Optionally verifies dotnet build of the cleanly merged tree (-CheckBuild).
       - Generates a comprehensive Korean Markdown report.
       - Reports through exactly ONE GitHub issue with label 'upstream-sync' (idempotent).
       - Safely deletes all temporary branches and restores the starting branch in a finally block.
.PARAMETER BaseRef
    Base reference to compare against (default: origin/master or master).
    Can be set to an older commit (e.g. upstream/master~5) to simulate new upstream commits.
.PARAMETER UpstreamRef
    Upstream reference to compare against (default: upstream/master).
.PARAMETER Topics
    Path to topics list file (default: origin/hj/infra:eng/hj/topics.txt, hj/infra:eng/hj/topics.txt, or eng/hj/topics.txt).
.PARAMETER CheckBuild
    Runs dotnet build (/v:q) on the merged tree if all topics rebase cleanly and merge cleanly.
.PARAMETER ReportPath
    File path to save the generated Markdown report.
.PARAMETER NoIssue
    Skips GitHub issue creation, updating, or closing. (Default when running locally without GITHUB_TOKEN).
.PARAMETER Repo
    Target GitHub repository in 'owner/repo' format (default: 5solas/gitextensions).
.PARAMETER GitHubToken
    Token for GitHub CLI (defaults to GITHUB_TOKEN or GH_TOKEN environment variables).
.PARAMETER NonInteractive
    Runs non-interactively without prompting.
#>

[CmdletBinding()]
param(
    [string]$BaseRef,
    [string]$UpstreamRef = "upstream/master",
    [string]$Topics,
    [switch]$CheckBuild,
    [string]$ReportPath,
    [switch]$NoIssue,
    [string]$Repo = "5solas/gitextensions",
    [string]$GitHubToken,
    [switch]$NonInteractive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Re-exec from a temporary directory copy so that branch checkouts and rebases
# do not overwrite, delete, or lock the executing script file itself.
# -----------------------------------------------------------------------------
if (-not $env:HJ_WATCH_REEXEC) {
    $scriptFile = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
    if ($scriptFile -and (Test-Path -Path $scriptFile -PathType Leaf)) {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("hj-watch-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        $tempScript = Join-Path $tempDir ([System.IO.Path]::GetFileName($scriptFile))
        Copy-Item -Path $scriptFile -Destination $tempScript -Force

        $forwardArgs = [System.Collections.Generic.List[string]]::new()
        foreach ($key in $PSBoundParameters.Keys) {
            $val = $PSBoundParameters[$key]
            if ($val -is [System.Management.Automation.SwitchParameter]) {
                if ($val.IsPresent) {
                    $forwardArgs.Add("-$key")
                }
            } else {
                $forwardArgs.Add("-$key")
                $forwardArgs.Add("$val")
            }
        }

        $env:HJ_WATCH_REEXEC = "1"
        try {
            & pwsh -NoProfile -File $tempScript @forwardArgs
            $exitCode = $LASTEXITCODE
            exit $exitCode
        } finally {
            Remove-Item env:HJ_WATCH_REEXEC -ErrorAction SilentlyContinue
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Resolve repository root
$repoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $repoRoot) {
    Write-Error "Not inside a git repository."
    exit 1
}
$repoRoot = $repoRoot.Trim()
Set-Location $repoRoot

# Validate working tree state (ignore untracked files starting with '??')
$statusOutput = @(git status --porcelain 2>$null | Where-Object { $_.Trim() })
$trackedModifications = @($statusOutput | Where-Object { -not $_.StartsWith('??') })
if ($trackedModifications.Count -gt 0) {
    Write-Error "Working tree has local tracked modifications. Please commit or revert them before running watch.`n$($trackedModifications -join "`n")"
    exit 1
}

# Record starting branch / commit
$originalBranch = (git branch --show-current 2>$null)
if ($originalBranch) {
    $originalBranch = $originalBranch.Trim()
}
if (-not $originalBranch) {
    $originalBranch = (git rev-parse HEAD 2>$null).Trim()
}

# Resolve target GitHub repository
if ([string]::IsNullOrWhiteSpace($Repo)) {
    if ($env:GITHUB_REPOSITORY) {
        $Repo = $env:GITHUB_REPOSITORY
    } else {
        $Repo = "5solas/gitextensions"
    }
}

# Determine if GitHub issue operations should be performed
$shouldManageIssues = -not $NoIssue
if ($shouldManageIssues) {
    $hasToken = $GitHubToken -or $env:GITHUB_TOKEN -or $env:GH_TOKEN
    $isCi = ($env:CI -eq 'true')
    if (-not $hasToken -and -not $isCi) {
        Write-Host "Local run without GITHUB_TOKEN detected: skipping GitHub issue management (use -NoIssue explicitly to silence)." -ForegroundColor DarkGray
        $shouldManageIssues = $false
    }
}

# Setup GitHub token in environment if explicitly provided
if ($GitHubToken) {
    $env:GITHUB_TOKEN = $GitHubToken
}

# Resolve BaseRef
if ([string]::IsNullOrWhiteSpace($BaseRef)) {
    if (git rev-parse --verify "refs/remotes/origin/master" 2>$null) {
        $BaseRef = "origin/master"
    } elseif (git rev-parse --verify "refs/heads/master" 2>$null) {
        $BaseRef = "master"
    } else {
        Write-Error "Could not resolve default BaseRef ('origin/master' or 'master')."
        exit 1
    }
}

$baseSha = (git rev-parse --verify $BaseRef 2>$null)
if (-not $baseSha) {
    Write-Error "Base ref '$BaseRef' could not be resolved."
    exit 1
}
$baseSha = $baseSha.Trim()

$upstreamSha = (git rev-parse --verify $UpstreamRef 2>$null)
if (-not $upstreamSha) {
    Write-Error "Upstream ref '$UpstreamRef' could not be resolved. Ensure upstream remote is fetched."
    exit 1
}
$upstreamSha = $upstreamSha.Trim()

$shortBaseSha = $baseSha.Substring(0, [System.Math]::Min(9, $baseSha.Length))
$shortUpstreamSha = $upstreamSha.Substring(0, [System.Math]::Min(9, $upstreamSha.Length))

Write-Host "Upstream Watcher:" -ForegroundColor Cyan
Write-Host "  Base Ref     : $BaseRef ($shortBaseSha)" -ForegroundColor White
Write-Host "  Upstream Ref : $UpstreamRef ($shortUpstreamSha)" -ForegroundColor White

# -----------------------------------------------------------------------------
# Case 1: Upstream is up-to-date with Base
# -----------------------------------------------------------------------------
if ($baseSha -eq $upstreamSha) {
    Write-Host "==> Upstream is up to date ($shortUpstreamSha == $shortBaseSha). [변경 없음]" -ForegroundColor Green

    if ($shouldManageIssues) {
        try {
            $openIssuesJson = & gh issue list --repo $Repo --label upstream-sync --state open --json number,title 2>$null
            if ($LASTEXITCODE -eq 0 -and $openIssuesJson) {
                $openIssues = @(($openIssuesJson -join "`n") | ConvertFrom-Json)
                foreach ($iss in $openIssues) {
                    Write-Host "Closing open watcher issue #$($iss.number) ($($iss.title))..." -ForegroundColor Cyan
                    $closeComment = "업스트림($UpstreamRef, ``$shortUpstreamSha``)이 베이스($BaseRef)에 동기화 완료되어 최신 상태입니다. 이슈를 닫습니다."
                    & gh issue close $iss.number --repo $Repo --comment $closeComment
                }
            }
        } catch {
            Write-Warning "Failed to check or close open watcher issues: $_"
        }
    }

    exit 0
}

# -----------------------------------------------------------------------------
# Case 2: Upstream has new commits -> Analyze Patch Stack
# -----------------------------------------------------------------------------
Write-Host "==> Upstream has moved! Analyzing new commits and patch stack..." -ForegroundColor Yellow

# Collect new upstream commits
$commitCount = [int](git rev-list --count "$baseSha..$upstreamSha" 2>$null).Trim()
$allCommitLogs = @(git log --oneline "$baseSha..$upstreamSha" 2>$null)
$maxDisplayCommits = 30
$displayedLogs = [System.Collections.Generic.List[string]]::new()
for ($i = 0; $i -lt [System.Math]::Min($maxDisplayCommits, $allCommitLogs.Count); $i++) {
    $displayedLogs.Add($allCommitLogs[$i])
}
$moreCommitsCount = $allCommitLogs.Count - $displayedLogs.Count

# Collect files touched by new upstream commits
$upstreamTouchedFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$rawUpstreamFiles = @(git diff --name-only "$baseSha..$upstreamSha" 2>$null)
foreach ($f in $rawUpstreamFiles) {
    $trimmed = $f.Trim()
    if ($trimmed) {
        [void]$upstreamTouchedFiles.Add($trimmed)
    }
}

# Resolve topics list
$tempTopicsFile = $null
$topicsSourceDescription = $null
$resolvedTopicsPath = $null

if ($PSBoundParameters.ContainsKey('Topics') -and -not [string]::IsNullOrWhiteSpace($Topics)) {
    $topicsSourceDescription = $Topics
    $resolvedTopicsPath = if ([System.IO.Path]::IsPathRooted($Topics)) { $Topics } else { Join-Path $repoRoot $Topics }
    if (-not (Test-Path $resolvedTopicsPath)) {
        Write-Error "Topics file not found at: $resolvedTopicsPath"
        exit 1
    }
} else {
    $infraTopics = & git show "origin/hj/infra:eng/hj/topics.txt" 2>$null
    if (-not $infraTopics) {
        $infraTopics = & git show "hj/infra:eng/hj/topics.txt" 2>$null
    }
    if ($LASTEXITCODE -eq 0 -and $infraTopics) {
        $tempTopicsFile = Join-Path ([System.IO.Path]::GetTempPath()) ("hj-watch-topics-" + [Guid]::NewGuid().ToString("N") + ".txt")
        Set-Content -Path $tempTopicsFile -Value $infraTopics -Encoding utf8
        $resolvedTopicsPath = $tempTopicsFile
        $topicsSourceDescription = "hj/infra:eng/hj/topics.txt"
    } else {
        $fallbackTopics = Join-Path $repoRoot "eng/hj/topics.txt"
        if (-not (Test-Path $fallbackTopics)) {
            Write-Error "Topics file not found at: $fallbackTopics"
            exit 1
        }
        $resolvedTopicsPath = $fallbackTopics
        $topicsSourceDescription = "eng/hj/topics.txt"
    }
}

$topicLines = @(Get-Content -Path $resolvedTopicsPath | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
if ($topicLines.Count -eq 0) {
    Write-Error "No topic branches specified in $topicsSourceDescription."
    exit 1
}

# Calculate overlaps between upstream commits and each topic branch
$topicOverlaps = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[string]]]::new()
$allOverlappingFiles = [System.Collections.Generic.SortedDictionary[string, System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)

$topicBaseline = if (git rev-parse --verify "refs/heads/master" 2>$null) { "master" } elseif (git rev-parse --verify "refs/remotes/origin/master" 2>$null) { "origin/master" } else { $baseSha }

foreach ($topic in $topicLines) {
    $topicRef = $topic
    if (-not (git rev-parse --verify "refs/heads/$topic" 2>$null)) {
        if (git rev-parse --verify "refs/remotes/origin/$topic" 2>$null) {
            $topicRef = "origin/$topic"
        }
    }

    # Extract the topic branch's own modified files relative to the topic baseline
    $topicFiles = @(git diff --name-only "$topicBaseline...$topicRef" 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $topicFiles) {
        $mb = (git merge-base $topicBaseline $topicRef 2>$null)
        if ($mb) {
            $topicFiles = @(git diff --name-only "$($mb.Trim())..$topicRef" 2>$null)
        } else {
            $topicFiles = @()
        }
    }

    $overlaps = [System.Collections.Generic.List[string]]::new()
    foreach ($tf in $topicFiles) {
        $normTf = $tf.Trim()
        if ($normTf -and $upstreamTouchedFiles.Contains($normTf)) {
            $overlaps.Add($normTf)
            if (-not $allOverlappingFiles.ContainsKey($normTf)) {
                $allOverlappingFiles[$normTf] = [System.Collections.Generic.List[string]]::new()
            }
            $allOverlappingFiles[$normTf].Add($topic)
        }
    }
    $topicOverlaps[$topic] = $overlaps
}

# Dry-run rebase and merge using isolated temporary branches
$runId = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$createdTempBranches = [System.Collections.Generic.List[string]]::new()
$topicResults = [System.Collections.Generic.List[PSCustomObject]]::new()
$allRebasedCleanly = $true

$mainMergeStatus = "Skipped"
$mainConflictFiles = @()
$buildStatus = "Skipped"

try {
    Write-Host "==> Rebasing topic branches onto $UpstreamRef ($shortUpstreamSha)..." -ForegroundColor Cyan

    foreach ($topic in $topicLines) {
        $safeName = $topic -replace '[^a-zA-Z0-9_\-]', '-'
        $tempBranch = "tmp/watch-$runId-$safeName"

        $sourceRef = $topic
        if (-not (git rev-parse --verify "refs/heads/$topic" 2>$null)) {
            if (git rev-parse --verify "refs/remotes/origin/$topic" 2>$null) {
                $sourceRef = "origin/$topic"
            }
        }

        & git branch -f $tempBranch $sourceRef
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create temporary branch '$tempBranch' from '$sourceRef'."
            exit 1
        }
        $createdTempBranches.Add($tempBranch)

        $beforeSha = (git rev-parse $tempBranch 2>$null).Trim()
        $beforeCount = [int](git rev-list --count "$topicBaseline..$tempBranch" 2>$null).Trim()

        # Identify commits upstream has already adopted (git cherry)
        $cherryLines = @(git cherry $upstreamSha $tempBranch 2>$null)
        $skippedCommits = [System.Collections.Generic.List[string]]::new()
        foreach ($cLine in $cherryLines) {
            if ($cLine -match '^-\s+([0-9a-fA-F]+)') {
                $cSha = $Matches[1]
                $cSummary = (git log -1 --format="%h %s" $cSha 2>$null).Trim()
                $skippedCommits.Add($cSummary)
            }
        }

        & git checkout -q $tempBranch
        & git rebase $upstreamSha $tempBranch 2>$null
        $rebaseCode = $LASTEXITCODE

        if ($rebaseCode -ne 0) {
            $allRebasedCleanly = $false
            $conflictFiles = @(git diff --name-only --diff-filter=U 2>$null | Where-Object { $_.Trim() })
            & git rebase --abort 2>$null

            Write-Host "    [CONFLICT] Topic '$topic' conflicted ($($conflictFiles.Count) file(s))" -ForegroundColor Red

            $topicResults.Add([PSCustomObject]@{
                Topic             = $topic
                TempBranch        = $tempBranch
                BeforeSha         = $beforeSha
                AfterSha          = ""
                BeforeCommitCount = $beforeCount
                AfterCommitCount  = -1
                SkippedCommits    = $skippedCommits.ToArray()
                IsFullyAdopted    = $false
                Status            = "Conflict"
                ConflictingFiles  = $conflictFiles
                Overlaps          = $topicOverlaps[$topic].ToArray()
            })
        } else {
            $afterSha = (git rev-parse $tempBranch 2>$null).Trim()
            $afterCount = [int](git rev-list --count "$upstreamSha..$tempBranch" 2>$null).Trim()
            $isAdopted = ($afterCount -eq 0 -or $afterSha -eq $upstreamSha)

            $statusText = if ($isAdopted) { "Adopted (Empty)" } else { "Clean" }
            Write-Host "    [OK] Topic '$topic' rebased cleanly ($beforeCount -> $afterCount commits)" -ForegroundColor Green

            $topicResults.Add([PSCustomObject]@{
                Topic             = $topic
                TempBranch        = $tempBranch
                BeforeSha         = $beforeSha
                AfterSha          = $afterSha
                BeforeCommitCount = $beforeCount
                AfterCommitCount  = $afterCount
                SkippedCommits    = $skippedCommits.ToArray()
                IsFullyAdopted    = $isAdopted
                Status            = $statusText
                ConflictingFiles  = @()
                Overlaps          = $topicOverlaps[$topic].ToArray()
            })
        }
    }

    # Integration merge (hj/main) dry run
    $tempMain = "tmp/watch-$runId-hj-main"
    if ($allRebasedCleanly) {
        Write-Host "==> Verifying integration branch (hj/main) merge..." -ForegroundColor Cyan
        & git branch -f $tempMain $upstreamSha
        $createdTempBranches.Add($tempMain)
        & git checkout -q $tempMain

        $mergeSuccess = $true
        foreach ($tr in $topicResults) {
            if ($tr.IsFullyAdopted) {
                Write-Host "    Skipping fully adopted topic '$($tr.Topic)' from merge" -ForegroundColor DarkGray
                continue
            }
            & git merge --no-ff --no-edit $tr.TempBranch 2>$null
            if ($LASTEXITCODE -ne 0) {
                $mergeSuccess = $false
                $mainConflictFiles = @(git diff --name-only --diff-filter=U 2>$null | Where-Object { $_.Trim() })
                & git merge --abort 2>$null
                Write-Host "    [CONFLICT] Integration merge failed on topic '$($tr.Topic)'" -ForegroundColor Red
                break
            }
        }

        if ($mergeSuccess) {
            $mainMergeStatus = "Clean"
            Write-Host "    [OK] hj/main integration merged cleanly." -ForegroundColor Green
        } else {
            $mainMergeStatus = "Conflict"
        }
    } else {
        $mainMergeStatus = "Skipped (토픽 rebase 충돌)"
        Write-Host "==> Integration merge skipped due to topic rebase conflicts." -ForegroundColor Yellow
    }

    # Optional Build check
    if ($CheckBuild) {
        if ($allRebasedCleanly -and $mainMergeStatus -eq "Clean") {
            Write-Host "==> Running dotnet build on cleanly merged tree..." -ForegroundColor Cyan
            & git submodule update --init --recursive 2>$null
            & dotnet build /v:q
            if ($LASTEXITCODE -eq 0) {
                $buildStatus = "Passed"
                Write-Host "    [OK] dotnet build succeeded." -ForegroundColor Green
            } else {
                $buildStatus = "Failed"
                Write-Host "    [FAIL] dotnet build failed." -ForegroundColor Red
            }
        } else {
            $buildStatus = "Skipped (패치 스택 충돌)"
            Write-Host "==> dotnet build skipped due to rebase/merge conflicts." -ForegroundColor Yellow
        }
    } else {
        $buildStatus = "Skipped"
    }

} finally {
    # Ensure any active rebase or merge in progress is aborted
    & git rebase --abort 2>$null
    & git merge --abort 2>$null

    # Switch back to original branch before deleting temporary branches
    if ($originalBranch) {
        & git checkout -q $originalBranch 2>$null
    }

    # Clean up all temporary test branches
    foreach ($tb in $createdTempBranches) {
        & git branch -D $tb 2>$null
    }

    if ($tempTopicsFile -and (Test-Path $tempTopicsFile)) {
        Remove-Item -Path $tempTopicsFile -Force -ErrorAction SilentlyContinue
    }
}

# -----------------------------------------------------------------------------
# Generate Korean Markdown Report
# -----------------------------------------------------------------------------
$b = [char]96
$b3 = "$b$b$b"
$b4 = "$b$b$b$b"

$overallStatus = if ($allRebasedCleanly -and $mainMergeStatus -eq "Clean") {
    if ($CheckBuild) {
        if ($buildStatus -eq "Passed") { "✅ 패치 스택 정상 적용 및 빌드 성공" } else { "❌ 패치 스택 적용 성공 (빌드 실패)" }
    } else {
        "✅ 모든 패치 스택 정상 적용 가능"
    }
} else {
    "⚠️ 패치 스택 충돌 발생 (수동 조치 필요)"
}

$reportSb = [System.Text.StringBuilder]::new()
[void]$reportSb.AppendLine("## 🔍 업스트림 변경 및 패치 스택 검증 리포트")
[void]$reportSb.AppendLine("")
[void]$reportSb.AppendLine("- **최신 업스트림**: $b$UpstreamRef$b ($b$shortUpstreamSha$b)")
[void]$reportSb.AppendLine("- **포크 베이스**: $b$BaseRef$b ($b$shortBaseSha$b)")
[void]$reportSb.AppendLine("- **신규 업스트림 커밋 수**: **$commitCount** 개")
[void]$reportSb.AppendLine("- **패치 스택 상태**: $overallStatus")
[void]$reportSb.AppendLine("- **통합 브랜치($b" + "hj/main$b) 머지**: $(if ($mainMergeStatus -eq 'Clean') { '✅ 머지 성공' } elseif ($mainMergeStatus -eq 'Conflict') { '❌ 머지 충돌' } else { "⚠️ $mainMergeStatus" })")
if ($CheckBuild) {
    [void]$reportSb.AppendLine("- **솔루션 빌드($b" + "dotnet build$b)**: $(if ($buildStatus -eq 'Passed') { '✅ 통과' } elseif ($buildStatus -eq 'Failed') { '❌ 실패' } else { "⚠️ $buildStatus" })")
}
[void]$reportSb.AppendLine("")

[void]$reportSb.AppendLine("### 1. 토픽 브랜치 패치 스택 상태")
[void]$reportSb.AppendLine("")
[void]$reportSb.AppendLine("| 토픽 브랜치 | 상태 | 커밋 수 (전 → 후) | 기반영(Skip) | 충돌 파일 |")
[void]$reportSb.AppendLine("| :--- | :--- | :--- | :--- | :--- |")
foreach ($tr in $topicResults) {
    $stIcon = switch ($tr.Status) {
        "Clean" { "✅ 정상 적용" }
        "Adopted (Empty)" { "🟡 완전 채택됨 (0)" }
        "Conflict" { "❌ 충돌" }
        default { $tr.Status }
    }
    $cRange = if ($tr.AfterCommitCount -ge 0) { "$($tr.BeforeCommitCount) → $($tr.AfterCommitCount)" } else { "$($tr.BeforeCommitCount) → -" }
    $skipInfo = if ($tr.SkippedCommits.Count -gt 0) { "$($tr.SkippedCommits.Count)개" } else { "0" }
    $cfInfo = if ($tr.ConflictingFiles.Count -gt 0) { "$($tr.ConflictingFiles.Count)개 ($($tr.ConflictingFiles -join ', '))" } else { "-" }
    [void]$reportSb.AppendLine("| $b$($tr.Topic)$b | $stIcon | $cRange | $skipInfo | $cfInfo |")
}
[void]$reportSb.AppendLine("")

if (-not $allRebasedCleanly -or $mainMergeStatus -eq "Conflict") {
    [void]$reportSb.AppendLine("### 2. ⚠️ 충돌 상세")
    [void]$reportSb.AppendLine("")
    foreach ($tr in $topicResults) {
        if ($tr.ConflictingFiles.Count -gt 0) {
            [void]$reportSb.AppendLine("- **토픽 $b$($tr.Topic)$b rebase 충돌 파일:**")
            foreach ($cf in $tr.ConflictingFiles) {
                [void]$reportSb.AppendLine("  - $b$cf$b")
            }
        }
    }
    if ($mainConflictFiles.Count -gt 0) {
        [void]$reportSb.AppendLine("- **통합 브랜치($b" + "hj/main$b) 머지 충돌 파일:**")
        foreach ($cf in $mainConflictFiles) {
            [void]$reportSb.AppendLine("  - $b$cf$b")
        }
    }
    [void]$reportSb.AppendLine("")
}

[void]$reportSb.AppendLine("### 3. ⚠️ 조기 경보: 파일 변경 중복 (Overlaps)")
[void]$reportSb.AppendLine("")
if ($allOverlappingFiles.Count -eq 0) {
    [void]$reportSb.AppendLine("업스트림 신규 커밋과 포크 토픽 브랜치 간 중복 수정된 파일이 없습니다.")
} else {
    [void]$reportSb.AppendLine("> 다음 파일들은 업스트림 신규 커밋과 포크 토픽 브랜치 양쪽에서 모두 변경되었습니다. 잠재적 충돌 및 로직 변경에 유의하십시오.")
    [void]$reportSb.AppendLine("")
    [void]$reportSb.AppendLine("| 중복 변경 파일 | 관련 토픽 브랜치 |")
    [void]$reportSb.AppendLine("| :--- | :--- |")
    foreach ($kv in $allOverlappingFiles.GetEnumerator()) {
        $topicsList = ($kv.Value | Select-Object -Unique) -join ", "
        [void]$reportSb.AppendLine("| $b$($kv.Key)$b | $b$topicsList$b |")
    }
}
[void]$reportSb.AppendLine("")

[void]$reportSb.AppendLine("### 4. 📝 신규 업스트림 커밋 목록 ($commitCount 개)")
[void]$reportSb.AppendLine("")
[void]$reportSb.AppendLine($b4)
foreach ($cl in $displayedLogs) {
    [void]$reportSb.AppendLine($cl)
}
if ($moreCommitsCount -gt 0) {
    [void]$reportSb.AppendLine("... 외 $moreCommitsCount 개 커밋 생략")
}
[void]$reportSb.AppendLine($b4)
[void]$reportSb.AppendLine("")

[void]$reportSb.AppendLine("### 5. 🛠️ 로컬 동기화 적용 가이드")
[void]$reportSb.AppendLine("")
if ($allRebasedCleanly -and $mainMergeStatus -eq "Clean") {
    [void]$reportSb.AppendLine("로컬 작업 트리에서 아래 명령을 실행하여 최신 업스트림 동기화 및 원격 푸시를 진행하십시오:")
    [void]$reportSb.AppendLine("${b3}pwsh")
    [void]$reportSb.AppendLine("pwsh eng/hj/Sync-Upstream.ps1 -Push")
    [void]$reportSb.AppendLine($b3)
} else {
    [void]$reportSb.AppendLine("패치 스택 충돌이 감지되었습니다. 로컬 작업 트리에서 동기화를 수행하고 수동으로 해결하십시오:")
    [void]$reportSb.AppendLine("${b3}pwsh")
    [void]$reportSb.AppendLine("# 1. 동기화 실행 (충돌 지점에서 중단됨)")
    [void]$reportSb.AppendLine("pwsh eng/hj/Sync-Upstream.ps1")
    [void]$reportSb.AppendLine("")
    [void]$reportSb.AppendLine("# 2. 충돌 파일 수동 해결 후 스테이징")
    [void]$reportSb.AppendLine("git add <충돌_파일>")
    [void]$reportSb.AppendLine("git rebase --continue # 또는 머지 충돌 시 git commit --no-edit")
    [void]$reportSb.AppendLine("")
    [void]$reportSb.AppendLine("# 3. 모든 토픽 해결 후 동기화 완료 및 푸시")
    [void]$reportSb.AppendLine("pwsh eng/hj/Sync-Upstream.ps1 -Push")
    [void]$reportSb.AppendLine($b3)
}
$markdownReport = $reportSb.ToString()

# Save report to file if requested
if ($ReportPath) {
    $reportDir = [System.IO.Path]::GetDirectoryName($ReportPath)
    if ($reportDir -and -not (Test-Path $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($ReportPath, $markdownReport, [System.Text.Encoding]::UTF8)
    Write-Host "Report saved to: $ReportPath" -ForegroundColor Green
}

# Display report to console
Write-Host ""
Write-Host $markdownReport -ForegroundColor White
Write-Host ""

# -----------------------------------------------------------------------------
# GitHub Issue Reporting (Idempotent single issue with label 'upstream-sync')
# -----------------------------------------------------------------------------
$issueTitle = "upstream: $commitCount new commits ($shortUpstreamSha)"

if ($shouldManageIssues) {
    Write-Host "==> Managing GitHub issue in $Repo..." -ForegroundColor Cyan

    # Ensure label exists
    & gh label create upstream-sync --description "Upstream synchronization watcher alerts" --color "0E8A16" --force --repo $Repo 2>$null

    # Search for existing open issue with label upstream-sync
    $existingIssuesJson = & gh issue list --repo $Repo --label upstream-sync --state open --json number,title,body 2>$null
    $openIssue = $null
    if ($LASTEXITCODE -eq 0 -and $existingIssuesJson) {
        $openIssues = @(($existingIssuesJson -join "`n") | ConvertFrom-Json)
        if ($openIssues.Count -gt 0) {
            $openIssue = $openIssues[0]
        }
    }

    $tempReportFile = Join-Path ([System.IO.Path]::GetTempPath()) ("hj-upstream-report-" + [Guid]::NewGuid().ToString("N") + ".md")
    [System.IO.File]::WriteAllText($tempReportFile, $markdownReport, [System.Text.Encoding]::UTF8)

    try {
        if ($openIssue -and $openIssue.title -eq $issueTitle) {
            # Same upstream commit as the last report: nothing new, avoid a daily comment.
            Write-Host "Issue #$($openIssue.number) already reports $shortUpstreamSha; leaving it unchanged." -ForegroundColor DarkGray
        } elseif ($openIssue) {
            Write-Host "Updating existing watcher issue #$($openIssue.number)..." -ForegroundColor Cyan
            & gh issue edit $openIssue.number --repo $Repo --title $issueTitle --body-file $tempReportFile
            if ($LASTEXITCODE -ne 0) { Write-Error "Failed to edit issue #$($openIssue.number)."; exit 1 }

            $updateComment = "🔄 업스트림 최신 커밋($shortUpstreamSha, 신규 $commitCount 개) 기준으로 패치 스택 검증 결과가 갱신되었습니다."
            & gh issue comment $openIssue.number --repo $Repo --body $updateComment
            Write-Host "Issue #$($openIssue.number) updated successfully." -ForegroundColor Green
        } else {
            Write-Host "Creating new watcher issue with title '$issueTitle'..." -ForegroundColor Cyan
            & gh issue create --repo $Repo --title $issueTitle --body-file $tempReportFile --label upstream-sync
            if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create GitHub issue (are Issues enabled on the repository?)."; exit 1 }
            Write-Host "Issue created successfully." -ForegroundColor Green
        }
    } finally {
        Remove-Item -Path $tempReportFile -Force -ErrorAction SilentlyContinue
    }
}

exit 0
