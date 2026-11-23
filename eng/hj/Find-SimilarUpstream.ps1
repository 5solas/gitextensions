#Requires -Version 7.0
<#
.SYNOPSIS
    Lists upstream commits that look similar to fork patches, grouped by the topics in similar-watch.txt.
.DESCRIPTION
    Reads topic lines ("name | regex | globs") and checks every commit in -Range:
      - regex  : matched case-insensitively against the commit subject and body;
      - globs  : matched against the files the commit changed.
    A commit is listed under a topic when either matches. This is a keyword hint with false positives
    and misses; it only tells a human where to look.

    Writes a Korean Markdown section (and optional JSON) and returns an object:
      @{ MatchCount; MatchedCommitCount; Markdown; Topics }
    Read-only: runs git log / git show only.
.PARAMETER Range
    Revision range to scan, e.g. "origin/master..upstream/master".
.PARAMETER ListFile
    Topic list file. Default: origin/hj/infra:eng/hj/similar-watch.txt, then hj/infra:..., then the copy next to this script.
.PARAMETER MarkdownPath
    Optional file to write the Markdown section to.
.PARAMETER JsonPath
    Optional file to write the match list (JSON) to.
.PARAMETER MaxPerTopic
    Maximum commits listed per topic in the Markdown (all are kept in JSON).
.PARAMETER HeadingLevel
    Markdown heading level of the section title (default 3 -> "###").
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Range,
    [string]$ListFile,
    [string]$MarkdownPath,
    [string]$JsonPath,
    [int]$MaxPerTopic = 15,
    [int]$HeadingLevel = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ListLines {
    if ($ListFile) {
        if (-not (Test-Path $ListFile)) { throw "Similar-watch list not found: $ListFile" }
        return [pscustomobject]@{ Source = $ListFile; Lines = @(Get-Content -Path $ListFile -Encoding utf8) }
    }
    foreach ($spec in @('origin/hj/infra:eng/hj/similar-watch.txt', 'hj/infra:eng/hj/similar-watch.txt')) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $content = @(& git show $spec 2>$null) } finally { $ErrorActionPreference = $prev }
        if ($LASTEXITCODE -eq 0 -and $content.Count -gt 0) {
            return [pscustomobject]@{ Source = $spec; Lines = $content }
        }
    }
    $local = Join-Path $PSScriptRoot 'similar-watch.txt'
    if (Test-Path $local) {
        return [pscustomobject]@{ Source = $local; Lines = @(Get-Content -Path $local -Encoding utf8) }
    }
    throw "similar-watch.txt not found (origin/hj/infra, hj/infra, or $local)."
}

function ConvertTo-GlobRegex([string]$Glob) {
    $g = $Glob.Trim().Replace('\', '/')
    $escaped = [regex]::Escape($g)
    $escaped = $escaped.Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return [regex]::new("^$escaped$", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

$prevEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try {
    $list = Get-ListLines

    # Parse topics.
    $topics = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in $list.Lines) {
        $line = "$raw".Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = @($line -split '\s\|\s')
        if ($parts.Count -ne 3) {
            Write-Warning "similar-watch: skipping malformed line (need 'name | regex | globs'): $line"
            continue
        }
        $name = $parts[0].Trim()
        $pattern = $parts[1].Trim()
        $globText = $parts[2].Trim()
        $regex = $null
        if ($pattern -and $pattern -ne '-') {
            try {
                $regex = [regex]::new($pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            } catch {
                Write-Warning "similar-watch: invalid regex for '$name': $($_.Exception.Message)"
            }
        }
        $globs = @()
        if ($globText -and $globText -ne '-') {
            $globs = @($globText -split ',' | Where-Object { $_.Trim() } | ForEach-Object { ConvertTo-GlobRegex $_ })
        }
        [void]$topics.Add([pscustomobject]@{ Name = $name; Regex = $regex; Globs = $globs; Matches = [System.Collections.Generic.List[object]]::new() })
    }

    # Read commits: <RS>sha<US>subject<US>body<US> followed by the changed file names.
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $logText = (& git -c core.quotepath=off log --format='%x1e%H%x1f%s%x1f%b%x1f' --name-only $Range 2>&1 | ForEach-Object { "$_" }) -join "`n"
        $logCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    if ($logCode -ne 0) { throw "git log $Range failed: $logText" }

    $commitCount = 0
    $matchedShas = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($record in ($logText -split [char]0x1e)) {
        if (-not $record.Trim()) { continue }
        $fields = $record -split [char]0x1f
        if ($fields.Count -lt 4) { continue }
        $commitCount++
        $sha = $fields[0].Trim()
        $subject = $fields[1].Trim()
        $body = $fields[2]
        $files = @($fields[3] -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $text = "$subject`n$body"

        foreach ($t in $topics) {
            $reasons = [System.Collections.Generic.List[string]]::new()
            if ($t.Regex) {
                $m = $t.Regex.Match($text)
                if ($m.Success) { [void]$reasons.Add("키워드 '$($m.Value.Trim())'") }
            }
            if ($t.Globs.Count -gt 0) {
                $hit = $files | Where-Object { $f = $_; $t.Globs | Where-Object { $_.IsMatch($f) } } | Select-Object -First 1
                if ($hit) { [void]$reasons.Add("경로 $hit") }
            }
            if ($reasons.Count -gt 0) {
                [void]$t.Matches.Add([pscustomobject]@{ Sha = $sha; Subject = $subject; Reasons = $reasons.ToArray() })
                [void]$matchedShas.Add($sha)
            }
        }
    }

    # Markdown section.
    $b = [char]96
    $h = '#' * [Math]::Max(1, $HeadingLevel)
    $sb = [System.Text.StringBuilder]::new()
    $matchCount = ($topics | ForEach-Object { $_.Matches.Count } | Measure-Object -Sum).Sum
    if (-not $matchCount) { $matchCount = 0 }
    [void]$sb.AppendLine("$h 포크 패치와 유사한 upstream 변경")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> 키워드·경로 대조 결과입니다(보조 수단: 오탐·누락 가능). 대상 $commitCount 개 커밋, 주제 목록 $b$($list.Source)$b.")
    [void]$sb.AppendLine("")
    if ($matchedShas.Count -eq 0) {
        [void]$sb.AppendLine("없음")
    } else {
        foreach ($t in $topics) {
            if ($t.Matches.Count -eq 0) { continue }
            [void]$sb.AppendLine("- **$($t.Name)** ($($t.Matches.Count))")
            foreach ($m in ($t.Matches | Select-Object -First $MaxPerTopic)) {
                $short = $m.Sha.Substring(0, [Math]::Min(9, $m.Sha.Length))
                $subj = $m.Subject.Replace('|', '\|')
                [void]$sb.AppendLine("  - $b$short$b $subj — $($m.Reasons -join ', ')")
            }
            if ($t.Matches.Count -gt $MaxPerTopic) {
                [void]$sb.AppendLine("  - … 외 $($t.Matches.Count - $MaxPerTopic) 개")
            }
        }
    }
    $markdown = $sb.ToString()

    if ($MarkdownPath) {
        $dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($MarkdownPath))
        if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        [System.IO.File]::WriteAllText($MarkdownPath, $markdown, [System.Text.UTF8Encoding]::new($false))
    }

    $topicSummary = @($topics | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; matches = @($_.Matches | ForEach-Object { [pscustomobject]@{ sha = $_.Sha; subject = $_.Subject; reasons = $_.Reasons } }) }
    })
    if ($JsonPath) {
        $dir = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($JsonPath))
        if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $json = [pscustomobject]@{ range = $Range; source = $list.Source; commitCount = $commitCount; matchedCommitCount = $matchedShas.Count; topics = $topicSummary } | ConvertTo-Json -Depth 6
        [System.IO.File]::WriteAllText($JsonPath, $json, [System.Text.UTF8Encoding]::new($false))
    }

    Write-Host $markdown

    [pscustomobject]@{
        MatchCount         = [int]$matchCount
        MatchedCommitCount = $matchedShas.Count
        Markdown           = $markdown
        Topics             = $topicSummary
    }
} finally {
    [Console]::OutputEncoding = $prevEncoding
}
