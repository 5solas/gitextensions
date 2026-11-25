#Requires -Version 7.0
<#
.SYNOPSIS
    Unattended upstream sync for the hj fork (CI): rebase topics, rebuild hj/main, verify, bump the
    release number, tag, and push everything in ONE atomic, leased push.
.DESCRIPTION
    Designed for .github/workflows/hj-upstream-autosync.yml; safe to run locally with -DryRun.

    It never moves local branches. Every result is built on temporary branches (tmp/autosync-*)
    created from the origin/* remote-tracking refs, and is pushed with explicit refspecs.

     1. Fetch upstream/<UpstreamBranch> and origin (+ tags).
     2. upstream == origin/master -> status 'up-to-date', nothing else happens.
     3. Daily limit: a hj/v* tag created on the current UTC day -> 'rate-limited' (not in -DryRun).
     4. Similar-change scan (Find-SimilarUpstream.ps1) over origin/master..upstream.
     5. Rebase each topic of origin/hj/infra:eng/hj/topics.txt onto upstream (rerere off).
        Any conflict -> 'conflict' (all topics are tried so the report lists every conflict).
     6. Commit "chore(hj): bump to hj-<N>" on the rebased hj/infra: eng/hj/VERSION = N and
        docs/hj/release-notes/<base>-hj-<N>.md (upstream range + similar-change section).
     7. Recreate hj/main = upstream + topics merged --no-ff in topics.txt order.
     8. Native + .NET build (Release). Failure -> 'build-failed'.
     9. Tests: GitCommands.Tests and GitUI.Tests block; UI.IntegrationTests subset reports only.
    10. -DryRun: print the push plan (optionally 'git push --dry-run' with -ProbePush) -> 'ready'.
        Otherwise: annotated tag (Release.ps1 format) + one 'git push --atomic' with
        --force-with-lease=<ref>:<sha fetched in step 1> for master, every topic and hj/main,
        plus the new tag -> 'pushed', or 'lease-rejected' / 'push-rejected-workflows' / 'push-failed'.

    -BaseRef <ref> (dry-run only) simulates an older mirror to exercise the flow when upstream has not moved.

    Outputs in -OutDir: result.json, report.md, similar.md, trx/*.trx, *.binlog.
    Exit code 0 for up-to-date / rate-limited / ready / pushed, 1 otherwise.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$ProbePush,
    [switch]$IgnoreDailyLimit,
    [switch]$NoFetch,
    [switch]$SkipBuild,
    [switch]$SkipNativeBuild,
    [switch]$SkipTests,
    [switch]$SkipIntegrationTests,
    [string]$BaseRef,
    [string]$OutDir = 'artifacts/hj-autosync',
    [string]$Remote = 'origin',
    [string]$UpstreamRemote = 'upstream',
    [string]$UpstreamBranch = 'master',
    [string]$MirrorBranch = 'master',
    [string]$InfraBranch = 'hj/infra',
    [string]$MainBranch = 'hj/main',
    [string[]]$KnownTestFailures = @('RunBatchCommand_can_handle_max_length_arguments')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# UTF-8 console I/O: Korean report text must not depend on the runner's code page, and native
# (git/gh) output with non-ASCII commit subjects must decode correctly.
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {
    Write-Verbose "Console encoding unchanged: $($_.Exception.Message)"
}

# NOTE: functions that return a value must not let native-command output reach the pipeline
# (PowerShell returns ALL uncaptured output). Send it to the host with '| Out-Host'.

$SuccessStatuses = @('up-to-date', 'rate-limited', 'ready', 'pushed')
$StatusLabels = @{
    'up-to-date'              = '✅ 변경 없음'
    'rate-limited'            = '⏸️ 오늘 이미 릴리스함 (하루 1회 제한)'
    'ready'                   = '🧪 dry-run 통과 (푸시하지 않음)'
    'pushed'                  = '🚀 푸시 완료'
    'conflict'                = '❌ 패치 스택 충돌'
    'build-failed'            = '❌ 빌드 실패'
    'test-failed'             = '❌ 테스트 실패'
    'lease-rejected'          = '❌ 원격이 그사이 바뀜 (lease 거부)'
    'push-rejected-workflows' = '❌ 워크플로 파일 권한 부족으로 푸시 거부'
    'push-failed'             = '❌ 푸시 실패'
    'error'                   = '❌ 오류'
}

# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------
function Invoke-GitCommand([string[]]$GitArgs) {
    # Never throws on a non-zero exit code or stderr output; returns code + merged output lines.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = @(& git @GitArgs 2>&1 | ForEach-Object { "$_" })
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $out; Text = ($out -join "`n") }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-GitOutput([string[]]$GitArgs) {
    $r = Invoke-GitCommand $GitArgs
    if ($r.Code -ne 0) { throw "git $($GitArgs -join ' ') failed ($($r.Code)):`n$($r.Text)" }
    return $r.Text.Trim()
}

function Test-GitRef([string]$Ref) {
    return (Invoke-GitCommand @('rev-parse', '--verify', '--quiet', "$Ref^{commit}")).Code -eq 0
}

function Get-SafeName([string]$Name) { return ($Name -replace '[^a-zA-Z0-9_\-]', '-') }

function Test-InProgress {
    $gitDir = Get-GitOutput @('rev-parse', '--git-dir')
    return [pscustomobject]@{
        Rebase = (Test-Path (Join-Path $gitDir 'rebase-merge')) -or (Test-Path (Join-Path $gitDir 'rebase-apply'))
        Merge  = Test-Path (Join-Path $gitDir 'MERGE_HEAD')
    }
}

function Set-Status([string]$Status, [string]$Message) {
    $script:result.status = $Status
    if ($Message) { $script:result.message = $Message }
}

function Invoke-TestSuite([string]$Name, [string]$Project, [string]$Filter, [bool]$Blocking) {
    $trxName = "$Name.trx"
    $trxPath = Join-Path $script:trxDir $trxName
    if (Test-Path $trxPath) { Remove-Item $trxPath -Force }
    $testArgs = @('test', $Project, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '-v', 'q',
        '--logger', "trx;LogFileName=$trxName", '--results-directory', $script:trxDir,
        '--blame-hang-timeout', '3m', '--blame-hang-dump-type', 'mini')
    if ($Filter) { $testArgs += @('--filter', $Filter) }

    Write-Host "==> Test suite '$Name' ($(if ($Blocking) { 'blocking' } else { 'report only' }))..." -ForegroundColor Cyan
    # '| Out-Host': keep the test runner's output out of this function's return value.
    & dotnet @testArgs | Out-Host
    $exitCode = $LASTEXITCODE

    $suite = [ordered]@{
        name = $Name; blocking = $Blocking; exitCode = $exitCode
        total = 0; passed = 0; failed = 0; skipped = 0
        failedTests = @(); knownFailures = @(); outcome = 'failed'
    }
    if (Test-Path $trxPath) {
        [xml]$trx = Get-Content -Path $trxPath -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($counters) {
            $suite.total = [int]$counters.GetAttribute('total')
            $suite.passed = [int]$counters.GetAttribute('passed')
            $suite.failed = [int]$counters.GetAttribute('failed')
            # TRX has no 'skipped' counter; skipped tests are counted as notExecuted.
            $suite.skipped = [int]$counters.GetAttribute('notExecuted')
        }
        $failedNames = @($trx.SelectNodes("//*[local-name()='UnitTestResult'][@outcome='Failed']") | ForEach-Object { $_.GetAttribute('testName') })
        foreach ($tn in $failedNames) {
            if ($script:KnownTestFailures | Where-Object { $tn -match [regex]::Escape($_) }) {
                $suite.knownFailures += $tn
            } else {
                $suite.failedTests += $tn
            }
        }
        if ($suite.failedTests.Count -eq 0 -and ($exitCode -eq 0 -or $suite.knownFailures.Count -gt 0)) {
            $suite.outcome = if ($suite.knownFailures.Count -gt 0) { 'passed (known failure)' } else { 'passed' }
        }
    } elseif ($exitCode -eq 0) {
        $suite.outcome = 'passed'
    } else {
        $suite.failedTests = @("(no TRX; dotnet test exit code $exitCode — hang/crash? see blame artifacts)")
    }
    $script:result.tests += [pscustomobject]$suite
    return [pscustomobject]$suite
}

# -----------------------------------------------------------------------------
# State
# -----------------------------------------------------------------------------
$repoRoot = (Get-GitOutput @('rev-parse', '--show-toplevel'))
Set-Location $repoRoot
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$trxDir = Join-Path $OutDir 'trx'
New-Item -ItemType Directory -Path $trxDir -Force | Out-Null

$result = [ordered]@{
    status = 'error'; message = ''; dryRun = $DryRun.IsPresent
    upstreamRef = "$UpstreamRemote/$UpstreamBranch"; upstreamSha = ''; baseSha = ''; commitCount = 0
    hjNumber = 0; version = ''; tag = ''; releaseNotes = ''
    dailyLimitHit = $false; similarMatchCount = 0
    topics = @(); mainMerge = 'not run'; build = 'not run'; tests = @()
    refs = @(); pushCommand = ''; pushOutput = ''; probeOutput = ''; pushed = $false
    errorDetail = ''
}
$commitLog = @()
$similarMarkdown = ''
$tempBranches = [System.Collections.Generic.List[string]]::new()
$localTagCreated = $null
$submodulesTouched = $false

$dirty = @((Invoke-GitCommand @('status', '--porcelain', '--untracked-files=no')).Output | Where-Object { $_.Trim() })
if ($dirty.Count -gt 0) {
    Write-Error "Working tree has tracked modifications; commit or revert them first.`n$($dirty -join "`n")"
    exit 1
}
$originalHead = (Invoke-GitCommand @('symbolic-ref', '--quiet', '--short', 'HEAD')).Text.Trim()
if (-not $originalHead) { $originalHead = Get-GitOutput @('rev-parse', 'HEAD') }

# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------
function Invoke-Main {
    $upstreamRef = "$UpstreamRemote/$UpstreamBranch"
    $mirrorRef = "$Remote/$MirrorBranch"

    if (-not $NoFetch) {
        Write-Host "==> Fetching $UpstreamRemote and $Remote..." -ForegroundColor Cyan
        [void](Get-GitOutput @('fetch', '--no-tags', $UpstreamRemote, "+refs/heads/${UpstreamBranch}:refs/remotes/$UpstreamRemote/$UpstreamBranch"))
        [void](Get-GitOutput @('fetch', '--prune', $Remote))
        [void](Get-GitOutput @('fetch', $Remote, '+refs/tags/hj/*:refs/tags/hj/*'))
    }

    $upstreamSha = Get-GitOutput @('rev-parse', "$upstreamRef^{commit}")
    $baseSha = Get-GitOutput @('rev-parse', "$mirrorRef^{commit}")
    if ($BaseRef) {
        # Simulation only (e.g. upstream/master~5): pretend the mirror is older to exercise the full flow.
        if (-not $DryRun) { throw '-BaseRef is only allowed with -DryRun.' }
        $baseSha = Get-GitOutput @('rev-parse', "$BaseRef^{commit}")
    }
    $script:result.upstreamSha = $upstreamSha
    $script:result.baseSha = $baseSha

    if ($upstreamSha -eq $baseSha) {
        Set-Status 'up-to-date' "$mirrorRef 가 $upstreamRef 와 같습니다."
        return
    }
    if ((Invoke-GitCommand @('merge-base', '--is-ancestor', $baseSha, $upstreamSha)).Code -ne 0) {
        Set-Status 'error' "$mirrorRef 가 $upstreamRef 의 조상이 아닙니다(미러에 직접 커밋됨?). 수동 확인이 필요합니다."
        return
    }

    $script:result.commitCount = [int](Get-GitOutput @('rev-list', '--count', "$baseSha..$upstreamSha"))
    $script:commitLog = @((Invoke-GitCommand @('-c', 'core.quotepath=off', 'log', '--oneline', '--no-decorate', "$baseSha..$upstreamSha")).Output)

    # Topics come from the remote hj/infra, never from local branches.
    $topicsText = Get-GitOutput @('show', "$Remote/${InfraBranch}:eng/hj/topics.txt")
    $topics = @($topicsText -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($topics.Count -eq 0) { throw "No topics in $Remote/${InfraBranch}:eng/hj/topics.txt" }
    if ($topics -notcontains $InfraBranch) { throw "$InfraBranch must be listed in topics.txt (the release number lives there)." }

    # Lease values: what the remote looks like right now.
    $expected = [ordered]@{ $MirrorBranch = $baseSha }
    foreach ($t in $topics) {
        if (-not (Test-GitRef "$Remote/$t")) { throw "Topic '$t' does not exist on $Remote." }
        $expected[$t] = Get-GitOutput @('rev-parse', "$Remote/$t")
    }
    if (-not (Test-GitRef "$Remote/$MainBranch")) { throw "$Remote/$MainBranch does not exist." }
    $expected[$MainBranch] = Get-GitOutput @('rev-parse', "$Remote/$MainBranch")

    # Daily limit (UTC calendar day of the newest hj/v* tag).
    $todayUtc = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-dd')
    $tagLines = @((Invoke-GitCommand @('for-each-ref', '--format=%(creatordate:iso-strict) %(refname:short)', 'refs/tags/hj/v*')).Output | Where-Object { $_.Trim() })
    $tagsToday = @($tagLines | Where-Object {
        $d = ($_ -split ' ')[0]
        try { ([DateTimeOffset]::Parse($d)).UtcDateTime.ToString('yyyy-MM-dd') -eq $todayUtc } catch { $false }
    })
    if ($tagsToday.Count -gt 0) {
        $script:result.dailyLimitHit = $true
        if (-not $DryRun -and -not $IgnoreDailyLimit) {
            Set-Status 'rate-limited' "오늘(UTC $todayUtc) 이미 태그가 있습니다: $($tagsToday -join ', '). 내일 다시 시도합니다."
            return
        }
    }

    # Release number and version.
    $versionText = Get-GitOutput @('show', "$Remote/${InfraBranch}:eng/hj/VERSION")
    $currentNumber = 0
    if (-not [int]::TryParse($versionText.Trim(), [ref]$currentNumber)) { throw "Invalid eng/hj/VERSION on $Remote/${InfraBranch}: '$versionText'" }
    $maxTagNumber = 0
    foreach ($line in $tagLines) {
        if ($line -match '-hj-(\d+)$') { $maxTagNumber = [Math]::Max($maxTagNumber, [int]$Matches[1]) }
    }
    $hjNumber = [Math]::Max($currentNumber, $maxTagNumber) + 1
    $coreYml = Get-GitOutput @('show', "${upstreamSha}:.github/workflows/_app-build-core.yml")
    if ($coreYml -notmatch '(?m)^\s*BUILD_VERSION_BASE:\s*([0-9.]+)') { throw 'BUILD_VERSION_BASE not found in upstream _app-build-core.yml' }
    $baseVersion = $Matches[1].Trim()
    $versionString = "$baseVersion-hj-$hjNumber"
    $tagName = "hj/v$versionString"
    $script:result.hjNumber = $hjNumber
    $script:result.version = $versionString
    $script:result.tag = $tagName
    if (Test-GitRef "refs/tags/$tagName") { throw "Tag $tagName already exists." }

    # Similar-change scan (read-only); runs before any checkout so this script's sibling is the one we started with.
    $similarScript = Join-Path $PSScriptRoot 'Find-SimilarUpstream.ps1'
    if (Test-Path $similarScript) {
        try {
            $sim = & $similarScript -Range "$baseSha..$upstreamSha" -MarkdownPath (Join-Path $OutDir 'similar.md') -JsonPath (Join-Path $OutDir 'similar.json') 6>$null
            $simObj = @($sim | Where-Object { $_ -and $_.PSObject.Properties['MatchCount'] }) | Select-Object -Last 1
            if ($simObj) {
                $script:similarMarkdown = $simObj.Markdown
                $script:result.similarMatchCount = $simObj.MatchedCommitCount
            }
        } catch {
            $script:similarMarkdown = "### 포크 패치와 유사한 upstream 변경`n`n(검사 실패: $($_.Exception.Message))`n"
        }
    }

    # ---- Rebase topics -------------------------------------------------------
    Write-Host "==> Rebasing $($topics.Count) topic(s) onto $upstreamRef ($($upstreamSha.Substring(0, 9)))..." -ForegroundColor Cyan
    [void](Get-GitOutput @('checkout', '-q', '--detach', $upstreamSha))
    $topicResults = [System.Collections.Generic.List[object]]::new()
    $anyConflict = $false
    foreach ($t in $topics) {
        $tmp = "tmp/autosync-$(Get-SafeName $t)"
        [void](Get-GitOutput @('branch', '-f', $tmp, "$Remote/$t"))
        [void]$script:tempBranches.Add($tmp)

        $before = [int](Get-GitOutput @('rev-list', '--count', "$upstreamSha..$tmp"))
        $adopted = @((Invoke-GitCommand @('cherry', $upstreamSha, $tmp)).Output | Where-Object { $_ -match '^-\s' }).Count

        $rb = Invoke-GitCommand @('-c', 'rerere.enabled=false', 'rebase', '--no-autostash', $upstreamSha, $tmp)
        $entry = [ordered]@{ topic = $t; before = $before; adopted = $adopted; after = -1; status = ''; conflictFiles = @(); newSha = '' }
        if ($rb.Code -ne 0) {
            $state = Test-InProgress
            if ($state.Rebase) {
                $entry.conflictFiles = @((Invoke-GitCommand @('diff', '--name-only', '--diff-filter=U')).Output | Where-Object { $_.Trim() })
                [void](Invoke-GitCommand @('rebase', '--abort'))
                $entry.status = 'conflict'
            } else {
                $entry.status = 'error'
                $entry.conflictFiles = @("(rebase failed without conflict state) $($rb.Text)")
            }
            $anyConflict = $true
            Write-Host "    [CONFLICT] $t" -ForegroundColor Red
        } else {
            $after = [int](Get-GitOutput @('rev-list', '--count', "$upstreamSha..$tmp"))
            $entry.after = $after
            $entry.newSha = Get-GitOutput @('rev-parse', $tmp)
            if ($after -ne ($before - $adopted)) {
                $entry.status = 'error'
                $anyConflict = $true
                $entry.conflictFiles = @("(commit count mismatch: expected $($before - $adopted), got $after)")
            } else {
                $entry.status = if ($after -eq 0) { 'adopted' } else { 'rebased' }
            }
            Write-Host "    [OK] $t ($before -> $after, adopted $adopted)" -ForegroundColor Green
        }
        [void]$topicResults.Add([pscustomobject]$entry)
    }
    $script:result.topics = $topicResults.ToArray()
    if ($anyConflict) {
        Set-Status 'conflict' '토픽 rebase 가 깨끗하게 끝나지 않았습니다. 아무것도 푸시하지 않았습니다.'
        return
    }

    # ---- Release bump commit on hj/infra ---------------------------------------
    $infraTmp = "tmp/autosync-$(Get-SafeName $InfraBranch)"
    [void](Get-GitOutput @('checkout', '-q', $infraTmp))
    $versionPath = Join-Path $repoRoot 'eng/hj/VERSION'
    $oldVersionBytes = [System.IO.File]::ReadAllBytes($versionPath)
    $eol = if ([System.Text.Encoding]::ASCII.GetString($oldVersionBytes).Contains("`r`n")) { "`r`n" } else { "`n" }
    [System.IO.File]::WriteAllText($versionPath, "$hjNumber$eol", [System.Text.UTF8Encoding]::new($false))

    $notesRel = "docs/hj/release-notes/$versionString.md"
    $notesPath = Join-Path $repoRoot $notesRel
    New-Item -ItemType Directory -Path (Split-Path $notesPath) -Force | Out-Null
    $b = [char]96
    $notes = [System.Text.StringBuilder]::new()
    [void]$notes.AppendLine("# Git Extensions $versionString")
    [void]$notes.AppendLine("")
    [void]$notes.AppendLine("upstream 자동 반영 릴리스입니다(서명 없음). upstream $b$($upstreamSha.Substring(0, 9))$b 기준, 새 upstream 커밋 $($script:result.commitCount) 개.")
    [void]$notes.AppendLine("")
    [void]$notes.AppendLine("## 새 upstream 커밋")
    [void]$notes.AppendLine("")
    foreach ($l in ($script:commitLog | Select-Object -First 50)) { [void]$notes.AppendLine("- $l") }
    if ($script:commitLog.Count -gt 50) { [void]$notes.AppendLine("- … 외 $($script:commitLog.Count - 50) 개") }
    [void]$notes.AppendLine("")
    if ($script:similarMarkdown) { [void]$notes.AppendLine(($script:similarMarkdown -replace '(?m)^### ', '## ')) }
    [System.IO.File]::WriteAllText($notesPath, $notes.ToString(), [System.Text.UTF8Encoding]::new($false))
    $script:result.releaseNotes = $notesRel

    # -f: upstream's .gitignore rule '[Rr]elease*/' matches docs/hj/release-notes/.
    [void](Get-GitOutput @('add', '-f', '--', 'eng/hj/VERSION', $notesRel))
    [void](Get-GitOutput @('commit', '-q', '--no-verify', '-m', "chore(hj): bump to hj-$hjNumber"))
    $infraEntry = $topicResults | Where-Object { $_.topic -eq $InfraBranch }
    $infraEntry.newSha = Get-GitOutput @('rev-parse', $infraTmp)

    # ---- Recreate hj/main ------------------------------------------------------
    Write-Host "==> Recreating $MainBranch..." -ForegroundColor Cyan
    $mainTmp = "tmp/autosync-$(Get-SafeName $MainBranch)"
    [void](Get-GitOutput @('checkout', '-q', '-B', $mainTmp, $upstreamSha))
    [void]$script:tempBranches.Add($mainTmp)
    foreach ($tr in $topicResults) {
        if ($tr.status -eq 'adopted') { continue }
        $tmp = "tmp/autosync-$(Get-SafeName $tr.topic)"
        $mg = Invoke-GitCommand @('-c', 'rerere.enabled=false', 'merge', '--no-ff', '--no-verify', '-m', "Merge branch '$($tr.topic)' into $MainBranch", $tmp)
        if ($mg.Code -ne 0) {
            $files = @((Invoke-GitCommand @('diff', '--name-only', '--diff-filter=U')).Output | Where-Object { $_.Trim() })
            [void](Invoke-GitCommand @('merge', '--abort'))
            $script:result.mainMerge = "conflict on '$($tr.topic)': $($files -join ', ')"
            Set-Status 'conflict' "$MainBranch 재생성 중 '$($tr.topic)' 머지 충돌. 아무것도 푸시하지 않았습니다."
            return
        }
        if ((Invoke-GitCommand @('merge-base', '--is-ancestor', $tmp, 'HEAD')).Code -ne 0) {
            throw "Topic $($tr.topic) is not an ancestor of the rebuilt $MainBranch."
        }
    }
    $newMainSha = Get-GitOutput @('rev-parse', 'HEAD')
    $script:result.mainMerge = 'clean'

    # ---- Build -------------------------------------------------------------------
    if ($SkipBuild) {
        $script:result.build = 'skipped'
    } else {
        Write-Host "==> Building (Release)..." -ForegroundColor Cyan
        $script:submodulesTouched = $true
        [void](Get-GitOutput @('submodule', 'update', '--init', '--recursive'))
        if (-not $SkipNativeBuild) {
            & dotnet build src/native/build.proj -c Release --verbosity q --nologo "/bl:$(Join-Path $OutDir 'native.binlog')" | Out-Host
            if ($LASTEXITCODE -ne 0) {
                $script:result.build = 'failed (native)'
                Set-Status 'build-failed' '네이티브 빌드 실패. 아무것도 푸시하지 않았습니다.'
                return
            }
        }
        & dotnet build -c Release --verbosity q --nologo "/bl:$(Join-Path $OutDir 'build.binlog')" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            $script:result.build = 'failed'
            Set-Status 'build-failed' 'dotnet build 실패. 아무것도 푸시하지 않았습니다.'
            return
        }
        $script:result.build = 'passed'
    }

    # ---- Tests -------------------------------------------------------------------
    if (-not $SkipTests -and -not $SkipBuild) {
        $blockingFailed = $false
        foreach ($s in @(
                @{ Name = 'GitCommands.Tests'; Project = 'tests/app/UnitTests/GitCommands.Tests/GitCommands.Tests.csproj'; Filter = $null },
                @{ Name = 'GitUI.Tests'; Project = 'tests/app/UnitTests/GitUI.Tests/GitUI.Tests.csproj'; Filter = $null })) {
            $r = Invoke-TestSuite -Name $s.Name -Project $s.Project -Filter $s.Filter -Blocking $true
            if ($r.outcome -eq 'failed') { $blockingFailed = $true }
        }
        if (-not $SkipIntegrationTests) {
            [void](Invoke-TestSuite -Name 'UI.IntegrationTests-hj' -Project 'tests/app/IntegrationTests/UI.IntegrationTests/UI.IntegrationTests.csproj' `
                    -Filter 'FullyQualifiedName~FormBrowseGitProcessBudgetTests|FullyQualifiedName~GhosttyConsoleTab|FullyQualifiedName~FormBrowseTests' -Blocking $false)
        }
        if ($blockingFailed) {
            Set-Status 'test-failed' '차단 테스트가 실패했습니다. 아무것도 푸시하지 않았습니다.'
            return
        }
    }

    # ---- Push plan -------------------------------------------------------------------
    $plan = [System.Collections.Generic.List[object]]::new()
    [void]$plan.Add([pscustomobject]@{ ref = $MirrorBranch; old = $expected[$MirrorBranch]; new = $upstreamSha })
    foreach ($tr in $topicResults) { [void]$plan.Add([pscustomobject]@{ ref = $tr.topic; old = $expected[$tr.topic]; new = $tr.newSha }) }
    [void]$plan.Add([pscustomobject]@{ ref = $MainBranch; old = $expected[$MainBranch]; new = $newMainSha })
    foreach ($p in $plan) {
        # Diagnostics only: GitHub may refuse GITHUB_TOKEN pushes that add/change .github/workflows files.
        $p | Add-Member -NotePropertyName workflowDiff -NotePropertyValue @((Invoke-GitCommand @('diff', '--name-only', $p.old, $p.new, '--', '.github/workflows')).Output | Where-Object { $_.Trim() }).Count
        $p | Add-Member -NotePropertyName newWorkflowCommits -NotePropertyValue ([int]((Invoke-GitCommand @('rev-list', '--count', '--full-history', $p.new, '--not', "--remotes=$Remote", '--', '.github/workflows')).Text.Trim()))
    }
    $script:result.refs = $plan.ToArray()

    $pushArgs = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $plan) {
        if ($p.old -eq $p.new) { continue }
        [void]$pushArgs.Add("--force-with-lease=refs/heads/$($p.ref):$($p.old)")
    }
    $refspecs = [System.Collections.Generic.List[string]]::new()
    foreach ($p in $plan) {
        if ($p.old -eq $p.new) { continue }
        [void]$refspecs.Add("$($p.new):refs/heads/$($p.ref)")
    }
    $script:result.pushCommand = "git push --atomic --porcelain $Remote $($pushArgs -join ' ') $($refspecs -join ' ') refs/tags/${tagName}:refs/tags/$tagName"

    if ($DryRun) {
        if ($ProbePush) {
            # Read-only probe: checks auth, atomic support and the leases; GitHub's server-side
            # workflow-permission check is NOT exercised because no pack is sent.
            $probe = Invoke-GitCommand (@('push', '--dry-run', '--atomic', '--porcelain', $Remote) + $pushArgs + $refspecs + @("${newMainSha}:refs/tags/$tagName"))
            $script:result.probeOutput = $probe.Text
            if ($probe.Code -ne 0) {
                Set-Status 'push-failed' "git push --dry-run 실패(exit $($probe.Code)). 실제 실행이었다면 푸시가 거부됐을 것입니다."
                return
            }
        }
        $msg = 'dry-run: 모든 검증 통과. 아래 명령으로 푸시했을 것입니다.'
        if ($script:result.dailyLimitHit) { $msg += ' (실제 실행이었다면 하루 1회 제한에 걸렸습니다.)' }
        Set-Status 'ready' $msg
        return
    }

    # ---- Tag + atomic push ---------------------------------------------------------------
    $tagMessage = "Git Extensions $versionString`n`nupstream-base: $(Get-GitOutput @('merge-base', $newMainSha, $upstreamSha))"
    [void](Get-GitOutput @('tag', '-a', $tagName, '-m', $tagMessage, $newMainSha))
    $script:localTagCreated = $tagName

    Write-Host "==> $($script:result.pushCommand)" -ForegroundColor Cyan
    $push = Invoke-GitCommand (@('push', '--atomic', '--porcelain', $Remote) + $pushArgs + $refspecs + @("refs/tags/${tagName}:refs/tags/$tagName"))
    $script:result.pushOutput = $push.Text
    Write-Host $push.Text
    if ($push.Code -eq 0) {
        $script:result.pushed = $true
        $script:localTagCreated = $null
        Set-Status 'pushed' "푸시 완료: $tagName"
    } elseif ($push.Text -match 'workflows?`? permission|refusing to allow') {
        Set-Status 'push-rejected-workflows' 'GitHub 가 워크플로 파일 변경 권한(workflows) 부족으로 푸시를 거부했습니다. --atomic 이므로 원격은 바뀌지 않았습니다.'
    } elseif ($push.Text -match 'stale info|fetch first|non-fast-forward') {
        Set-Status 'lease-rejected' '실행 중 누군가 원격 브랜치를 바꿨습니다(lease 거부). --atomic 이므로 원격은 바뀌지 않았습니다.'
    } else {
        Set-Status 'push-failed' "git push 실패(exit $($push.Code)). --atomic 이므로 원격은 바뀌지 않았어야 합니다 — 출력을 확인하세요."
    }
}

# -----------------------------------------------------------------------------
# Report
# -----------------------------------------------------------------------------
function Write-Report {
    $b = [char]96
    $r = $script:result
    $short = { param($s) if ($s) { $s.Substring(0, [Math]::Min(9, $s.Length)) } else { '-' } }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("## 업스트림 자동 반영: $($StatusLabels[$r.status])")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **모드**: $(if ($r.dryRun) { 'dry-run (푸시·태그·릴리스 없음)' } else { '실제 실행' })")
    [void]$sb.AppendLine("- **upstream**: $b$($r.upstreamRef)$b ($b$(& $short $r.upstreamSha)$b), 포크 미러 $b$Remote/$MirrorBranch$b ($b$(& $short $r.baseSha)$b), 새 커밋 **$($r.commitCount)** 개")
    if ($r.tag) { [void]$sb.AppendLine("- **릴리스**: $b$($r.tag)$b (eng/hj/VERSION → $($r.hjNumber))") }
    if ($r.message) { [void]$sb.AppendLine("- **결과**: $($r.message)") }
    [void]$sb.AppendLine("")
    if ($r.errorDetail) {
        [void]$sb.AppendLine("<details><summary>오류 상세 (예외·위치·스택)</summary>")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("$b$b$b")
        [void]$sb.AppendLine($r.errorDetail)
        [void]$sb.AppendLine("$b$b$b")
        [void]$sb.AppendLine("</details>")
        [void]$sb.AppendLine("")
    }

    if ($r.topics.Count -gt 0) {
        [void]$sb.AppendLine("### 토픽 rebase")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| 토픽 | 결과 | 커밋 수 (전 → 후) | upstream 기반영 | 충돌 파일 |")
        [void]$sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |")
        foreach ($t in $r.topics) {
            $after = if ($t.after -ge 0) { $t.after } else { '-' }
            $files = if ($t.conflictFiles.Count -gt 0) { ($t.conflictFiles | ForEach-Object { "$b$_$b" }) -join '<br>' } else { '-' }
            [void]$sb.AppendLine("| $b$($t.topic)$b | $($t.status) | $($t.before) → $after | $($t.adopted) | $files |")
        }
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("- **$MainBranch 재생성**: $($r.mainMerge)")
        [void]$sb.AppendLine("- **빌드**: $($r.build)")
        [void]$sb.AppendLine("")
    }

    if ($r.tests.Count -gt 0) {
        [void]$sb.AppendLine("### 테스트")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| 스위트 | 차단 | 결과 | 전체 | 통과 | 실패 | 건너뜀 |")
        [void]$sb.AppendLine("| :--- | :--- | :--- | ---: | ---: | ---: | ---: |")
        foreach ($s in $r.tests) {
            [void]$sb.AppendLine("| $($s.name) | $(if ($s.blocking) { '예' } else { '아니오 (보고만)' }) | $($s.outcome) | $($s.total) | $($s.passed) | $($s.failed) | $($s.skipped) |")
        }
        foreach ($s in $r.tests) {
            foreach ($f in ($s.failedTests | Select-Object -First 15)) { [void]$sb.AppendLine("- ❌ $($s.name): $b$f$b") }
            foreach ($f in $s.knownFailures) { [void]$sb.AppendLine("- ⚠️ $($s.name): 알려진 환경 실패 $b$f$b") }
        }
        [void]$sb.AppendLine("")
    }

    if ($r.refs.Count -gt 0) {
        [void]$sb.AppendLine("### 푸시 계획")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| ref | 원격 현재 (lease) | 새 값 | .github/workflows 차이 파일 | 새 커밋 중 워크플로 변경 |")
        [void]$sb.AppendLine("| :--- | :--- | :--- | ---: | ---: |")
        foreach ($p in $r.refs) {
            [void]$sb.AppendLine("| $b$($p.ref)$b | $b$(& $short $p.old)$b | $b$(& $short $p.new)$b | $($p.workflowDiff) | $($p.newWorkflowCommits) |")
        }
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("$b$b$b")
        [void]$sb.AppendLine($r.pushCommand)
        [void]$sb.AppendLine("$b$b$b")
        foreach ($o in @(@('probe (git push --dry-run)', $r.probeOutput), @('push', $r.pushOutput))) {
            if ($o[1]) {
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("<details><summary>$($o[0]) 출력</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("$b$b$b")
                [void]$sb.AppendLine($o[1])
                [void]$sb.AppendLine("$b$b$b")
                [void]$sb.AppendLine("</details>")
            }
        }
        [void]$sb.AppendLine("")
    }

    if ($script:similarMarkdown) {
        [void]$sb.AppendLine($script:similarMarkdown)
    }

    if ($script:commitLog.Count -gt 0) {
        [void]$sb.AppendLine("### 새 upstream 커밋 ($($r.commitCount) 개)")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("$b$b$b$b")
        foreach ($l in ($script:commitLog | Select-Object -First 30)) { [void]$sb.AppendLine($l) }
        if ($script:commitLog.Count -gt 30) { [void]$sb.AppendLine("... 외 $($script:commitLog.Count - 30) 개") }
        [void]$sb.AppendLine("$b$b$b$b")
    }
    return $sb.ToString()
}

# -----------------------------------------------------------------------------
# Run
# -----------------------------------------------------------------------------
try {
    Invoke-Main
} catch {
    $err = $_
    # Compact call chain from the script stack ("Get-GitOutput:101 <- Invoke-Main:220"); the frame text
    # is localized ("at X, file: line N" on the runner, "X에서 file: 줄 N" on Korean Windows).
    $frames = @("$($err.ScriptStackTrace)" -split "`r?`n" | ForEach-Object {
        if ($_ -match '^(?:at\s+(?<fn>[^,]+),|(?<fn>\S+)에서\s)(?:.*\D)?(?<line>\d+)\s*$' -and $Matches['fn'] -ne '<ScriptBlock>') { "$($Matches['fn']):$($Matches['line'])" }
    } | Select-Object -First 3)
    $where = if ($frames.Count -gt 0) { $frames -join ' <- ' }
             elseif ($err.InvocationInfo -and $err.InvocationInfo.ScriptName) { "$([System.IO.Path]::GetFileName($err.InvocationInfo.ScriptName)):$($err.InvocationInfo.ScriptLineNumber)" }
             else { 'unknown' }
    # One line for the report bullet and the issue title area; the full text goes to errorDetail.
    $oneLine = (($err.Exception.Message -split "`r?`n") | Where-Object { $_.Trim() }) -join ' / '
    if ($oneLine.Length -gt 300) { $oneLine = $oneLine.Substring(0, 300) + '…' }
    Set-Status 'error' "$oneLine (위치: $where)"
    $result.errorDetail = @(
        "$($err.Exception.GetType().FullName): $($err.Exception.Message)"
        $(if ($err.InvocationInfo) { $err.InvocationInfo.PositionMessage })
        'Script stack:'
        $err.ScriptStackTrace
    ) -join "`n"
    Write-Host $result.errorDetail -ForegroundColor Red
} finally {
    $state = Test-InProgress
    if ($state.Rebase) { [void](Invoke-GitCommand @('rebase', '--abort')) }
    if ($state.Merge) { [void](Invoke-GitCommand @('merge', '--abort')) }
    if ($localTagCreated) { [void](Invoke-GitCommand @('tag', '-d', $localTagCreated)) }
    # Leave the work tree as we found it (hj/infra temp branch may have had VERSION written).
    [void](Invoke-GitCommand @('checkout', '-q', '-f', $originalHead))
    if ($submodulesTouched) { [void](Invoke-GitCommand @('submodule', 'update', '--init', '--recursive')) }
    foreach ($tb in $tempBranches) { [void](Invoke-GitCommand @('branch', '-D', $tb)) }
}

$report = Write-Report
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $OutDir 'report.md'), $report, $utf8)
[System.IO.File]::WriteAllText((Join-Path $OutDir 'result.json'), ($result | ConvertTo-Json -Depth 8), $utf8)
Write-Host ""
Write-Host $report
Write-Host "Status: $($result.status)  (result: $(Join-Path $OutDir 'result.json'))" -ForegroundColor $(if ($SuccessStatuses -contains $result.status) { 'Green' } else { 'Red' })

exit $(if ($SuccessStatuses -contains $result.status) { 0 } else { 1 })
