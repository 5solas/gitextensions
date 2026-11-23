#Requires -Version 7.0
<#
.SYNOPSIS
    Keeps the single 'upstream-sync' GitHub issue in line with an autosync run (hj-upstream-autosync.yml).
.DESCRIPTION
    The "active" issue is the open issue labelled 'upstream-sync' WITHOUT the 'review' label.
    Issues labelled 'review' are left for a human to close.

    -Phase sync     (after Invoke-AutoSync.ps1)
        up-to-date            -> close the active issue.
        rate-limited, pushed  -> no change ('pushed' is handled by -Phase finalize).
        ready                 -> report-only mode: create/update the active issue; dry-run: no change.
        any failure           -> create/update the active issue with the report ("nothing was pushed").
    -Phase finalize (after the release job)
        release ok, no similar upstream changes -> close the active issue with the release link.
        release ok, similar changes found       -> keep it open, add label 'review', comment the link.
        release failed                          -> create/update the active issue (tag exists, rerun hj-release).
.PARAMETER ResultPath
    result.json written by Invoke-AutoSync.ps1.
.PARAMETER ReportPath
    report.md written by Invoke-AutoSync.ps1.
.PARAMETER Mode
    release | report-only | dry-run (as resolved by the workflow).
.PARAMETER ReleaseResult
    finalize only: result of the release job (success, failure, cancelled, skipped).
.PARAMETER ReleaseMode
    finalize only: workflow-call (release built in this run) or tag-push (a PAT pushed the tag; hj-release runs on its own).
#>
[CmdletBinding()]
param(
    [ValidateSet('sync', 'finalize')][string]$Phase = 'sync',
    [Parameter(Mandatory)][string]$ResultPath,
    [string]$ReportPath,
    [ValidateSet('release', 'report-only', 'dry-run')][string]$Mode = 'release',
    [string]$ReleaseResult = '',
    [ValidateSet('workflow-call', 'tag-push')][string]$ReleaseMode = 'workflow-call',
    [string]$Repo = $env:GITHUB_REPOSITORY,
    [string]$Label = 'upstream-sync',
    [string]$ReviewLabel = 'review'
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

if (-not $Repo) { $Repo = '5solas/gitextensions' }
$server = if ($env:GITHUB_SERVER_URL) { $env:GITHUB_SERVER_URL } else { 'https://github.com' }
$runUrl = if ($env:GITHUB_RUN_ID) { "$server/$Repo/actions/runs/$($env:GITHUB_RUN_ID)" } else { '(local run)' }
$b = [char]96

$result = if (Test-Path $ResultPath) { Get-Content -Raw $ResultPath | ConvertFrom-Json } else { $null }
$status = if ($result) { $result.status } else { 'error' }
$report = if ($ReportPath -and (Test-Path $ReportPath)) { Get-Content -Raw $ReportPath } else { "(report.md 없음 — 실행 로그를 확인하세요: $runUrl)" }
$shortSha = if ($result -and $result.upstreamSha) { $result.upstreamSha.Substring(0, 9) } else { 'unknown' }
$commitCount = if ($result) { $result.commitCount } else { '?' }
$tag = if ($result) { $result.tag } else { '' }
$similarCount = if ($result) { [int]$result.similarMatchCount } else { 0 }

function Invoke-Gh([string[]]$GhArgs) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $out = @(& gh @GhArgs 2>&1 | ForEach-Object { "$_" })
        if ($LASTEXITCODE -ne 0) { throw "gh $($GhArgs[0..1] -join ' ') failed: $($out -join "`n")" }
        return ($out -join "`n")
    } finally { $ErrorActionPreference = $prev }
}

function Get-ActiveIssue {
    $json = Invoke-Gh @('issue', 'list', '--repo', $Repo, '--label', $Label, '--state', 'open', '--json', 'number,title,labels', '--limit', '50')
    $issues = @($json | ConvertFrom-Json)
    return @($issues | Where-Object { -not ($_.labels | Where-Object { $_.name -eq $ReviewLabel }) }) | Select-Object -First 1
}

function New-BodyFile([string]$Body) {
    if ($Body.Length -gt 60000) { $Body = $Body.Substring(0, 60000) + "`n`n… (잘림 — 전체 보고서는 실행 artifact 'hj-autosync-report')" }
    $f = Join-Path ([System.IO.Path]::GetTempPath()) ("hj-sync-issue-" + [Guid]::NewGuid().ToString('N') + '.md')
    [System.IO.File]::WriteAllText($f, $Body, [System.Text.UTF8Encoding]::new($false))
    return $f
}

function Set-ActiveIssue([string]$Title, [string]$Body, [string[]]$ExtraLabels = @()) {
    [void](Invoke-Gh @('label', 'create', $Label, '--description', 'Upstream synchronization alerts', '--color', '0E8A16', '--force', '--repo', $Repo))
    $file = New-BodyFile $Body
    try {
        $issue = Get-ActiveIssue
        if ($issue) {
            $ghArgs = @('issue', 'edit', $issue.number, '--repo', $Repo, '--title', $Title, '--body-file', $file)
            foreach ($l in $ExtraLabels) { $ghArgs += @('--add-label', $l) }
            [void](Invoke-Gh $ghArgs)
            if ($issue.title -ne $Title) {
                [void](Invoke-Gh @('issue', 'comment', $issue.number, '--repo', $Repo, '--body', "🔄 갱신: $Title ($runUrl)"))
            }
            Write-Host "Updated issue #$($issue.number): $Title"
        } else {
            $ghArgs = @('issue', 'create', '--repo', $Repo, '--title', $Title, '--body-file', $file, '--label', $Label)
            foreach ($l in $ExtraLabels) { $ghArgs += @('--label', $l) }
            Write-Host (Invoke-Gh $ghArgs)
        }
    } finally {
        Remove-Item $file -Force -ErrorAction SilentlyContinue
    }
}

function Close-ActiveIssue([string]$Comment) {
    $issue = Get-ActiveIssue
    if (-not $issue) { Write-Host 'No active upstream-sync issue to close.'; return }
    [void](Invoke-Gh @('issue', 'close', $issue.number, '--repo', $Repo, '--comment', $Comment))
    Write-Host "Closed issue #$($issue.number)."
}

$titleBase = "upstream: $commitCount new commits ($shortSha)"

if ($Phase -eq 'sync') {
    if ($Mode -eq 'dry-run') { Write-Host 'dry-run: issues are not touched.'; exit 0 }
    switch ($status) {
        'up-to-date' { Close-ActiveIssue "업스트림이 포크 ${b}master${b} 와 같습니다(${b}$shortSha${b}). 이슈를 닫습니다. ($runUrl)"; break }
        'rate-limited' { Write-Host 'rate-limited: no issue change.'; break }
        'pushed' { Write-Host 'pushed: the finalize job reports the release.'; break }
        'ready' {
            if ($Mode -eq 'report-only') {
                $body = "> 🔕 자동 반영이 꺼져 있습니다(저장소 변수 ${b}HJ_AUTO_RELEASE=false${b}). 검증은 통과했지만 아무것도 푸시하지 않았습니다. 수동 반영: ${b}pwsh eng/hj/Sync-Upstream.ps1 -Push${b}`n> 실행: $runUrl`n`n$report"
                Set-ActiveIssue "$titleBase — 자동 반영 꺼짐 (적용 가능)" $body
            }
            break
        }
        default {
            $body = "> ⛔ 자동 반영 중단: **$status**. 원격에는 아무것도 푸시하지 않았습니다.`n> 실행: $runUrl`n`n$report"
            Set-ActiveIssue "$titleBase — $status" $body
        }
    }
    exit 0
}

# ---- finalize -------------------------------------------------------------------
$releaseUrl = "$server/$Repo/releases/tag/$tag"
$releaseOk = ($ReleaseMode -eq 'tag-push') -or ($ReleaseResult -eq 'success')
if ($releaseOk) {
    $done = if ($ReleaseMode -eq 'tag-push') {
        "✅ 자동 반영 완료: $b$tag$b 푸시. 릴리스 빌드는 태그 푸시로 별도 실행됩니다: $releaseUrl (실행: $runUrl)"
    } else {
        "✅ 자동 반영·릴리스 완료: $b$tag$b → $releaseUrl (실행: $runUrl)"
    }
    if ($similarCount -gt 0) {
        try { [void](Invoke-Gh @('label', 'create', $ReviewLabel, '--description', 'Needs a human look', '--color', 'FBCA04', '--repo', $Repo)) } catch { Write-Host "label '$ReviewLabel' already exists (or cannot be created): $($_.Exception.Message)" }
        $body = "> $done`n>`n> 🔎 포크 패치와 유사해 보이는 upstream 변경이 $similarCount 개 커밋에서 발견되어 이슈를 열어 둡니다. 확인 후 직접 닫으세요(키워드 대조라 오탐 가능).`n`n$report"
        Set-ActiveIssue "upstream 반영 완료 $tag — 유사 변경 검토 필요" $body @($ReviewLabel)
    } else {
        Close-ActiveIssue $done
    }
} else {
    $body = "> ⚠️ 브랜치와 태그 $b$tag$b 는 푸시됐지만 릴리스 빌드가 '$ReleaseResult' 로 끝났습니다.`n> Actions → hj-release → Run workflow 에서 ${b}tag=$tag${b} 로 다시 실행하세요. 실행: $runUrl`n`n$report"
    Set-ActiveIssue "$titleBase — release-failed ($tag)" $body
}
exit 0
