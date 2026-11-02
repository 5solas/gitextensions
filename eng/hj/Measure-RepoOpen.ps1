[CmdletBinding()]
param(
    [string[]]$RepoPaths,
    [int]$Iterations = 5,
    [switch]$SkipLargeRepo,
    [string]$PerfOutputDir
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$worktreeRoot = Resolve-Path (Join-Path $scriptDir "..\..") | Select-Object -ExpandProperty Path

Write-Host "=== Git Extensions Repository Open Performance Measurement ===" -ForegroundColor Cyan
Write-Host "Worktree Root: $worktreeRoot"

if (-not $PerfOutputDir) {
    $PerfOutputDir = Join-Path $worktreeRoot "docs\hj\perf"
}

if (-not [System.IO.Path]::IsPathRooted($PerfOutputDir)) {
    $PerfOutputDir = Join-Path $worktreeRoot $PerfOutputDir
}

if (-not (Test-Path $PerfOutputDir)) {
    New-Item -ItemType Directory -Path $PerfOutputDir -Force | Out-Null
}

# 1. Target Repositories Preparation
$targetRepos = [System.Collections.Generic.List[string]]::new()

if ($RepoPaths -and $RepoPaths.Count -gt 0) {
    foreach ($r in $RepoPaths) {
        $full = [System.IO.Path]::GetFullPath($r)
        if (Test-Path $full) {
            $targetRepos.Add($full)
        } else {
            Write-Warning "User specified repo path does not exist: $full"
        }
    }
} else {
    # Default 1: GitExtensions repo itself (medium)
    $mediumRepo = "D:\work\gitextensions"
    if (Test-Path $mediumRepo) {
        $targetRepos.Add($mediumRepo)
    } else {
        Write-Warning "Medium repository path does not exist: $mediumRepo"
    }

    # Default 2: git.git (large)
    if (-not $SkipLargeRepo) {
        $largeRepo = "D:\work\perf-repos\git"
        if (-not (Test-Path $largeRepo)) {
            Write-Host "Cloning large repository (git/git) to $largeRepo..." -ForegroundColor Yellow
            $perfReposDir = "D:\work\perf-repos"
            if (-not (Test-Path $perfReposDir)) {
                New-Item -ItemType Directory -Path $perfReposDir -Force | Out-Null
            }
            try {
                git clone https://github.com/git/git.git $largeRepo
                if ($LASTEXITCODE -eq 0 -and (Test-Path $largeRepo)) {
                    Write-Host "Successfully cloned git/git." -ForegroundColor Green
                    $targetRepos.Add($largeRepo)
                } else {
                    Write-Warning "Failed to clone git/git. Proceeding without large repository."
                }
            } catch {
                Write-Warning "Exception cloning git/git: $_. Proceeding without large repository."
            }
        } else {
            Write-Host "Found existing large repository at $largeRepo." -ForegroundColor Green
            $targetRepos.Add($largeRepo)
        }
    }
}

if ($targetRepos.Count -eq 0) {
    Write-Error "No valid repositories to measure. Aborting."
    exit 1
}

Write-Host "Target Repositories ($($targetRepos.Count)): $($targetRepos -join ', ')" -ForegroundColor Cyan
Write-Host "Iterations: $Iterations" -ForegroundColor Cyan

# 2. Build test project
Write-Host "Building UI.IntegrationTests project..." -ForegroundColor Yellow
dotnet build (Join-Path $worktreeRoot "tests\app\IntegrationTests\UI.IntegrationTests\UI.IntegrationTests.csproj") /v:q
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# 3. Set environment variables and execute harness
$env:HJ_PERF_REPO = ($targetRepos -join ";")
$env:HJ_PERF_ITERATIONS = $Iterations.ToString()
$env:HJ_PERF_OUTPUT_DIR = (Resolve-Path $PerfOutputDir).Path
$env:GIT_EDITOR = "true"
$env:GIT_TERMINAL_PROMPT = "0"

Write-Host "Starting RepositoryOpenPerfTests via dotnet test..." -ForegroundColor Yellow

$testProj = Join-Path $worktreeRoot "tests\app\IntegrationTests\UI.IntegrationTests\UI.IntegrationTests.csproj"
$resultsDir = Join-Path $worktreeRoot "artifacts\Debug\TestsResults"
if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

$trxFileName = "RepositoryOpenPerfTests_$([DateTime]::Now.ToString('yyyyMMdd_HHmmss')).trx"
$testOutFile = Join-Path $resultsDir "RepositoryOpenPerfTests_output.txt"

$argList = @(
    'test',
    $testProj,
    '--no-build',
    '--filter', 'FullyQualifiedName~RepositoryOpenPerfTests',
    '--blame-crash',
    '--blame-crash-dump-type', 'mini',
    '--blame-hang-timeout', '10m',
    '--logger', "trx;LogFileName=$trxFileName"
)

$p = Start-Process -FilePath "dotnet" -ArgumentList $argList -NoNewWindow -PassThru -RedirectStandardOutput $testOutFile
if (-not $p.WaitForExit(600000)) {
    Write-Warning "RepositoryOpenPerfTests timed out after 10m. Terminating testhost/dotnet process tree..."
    # Only this run's process tree; other worktrees may be running tests too.
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $($p.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

$testExitCode = $p.ExitCode
$trxPath = Join-Path $resultsDir $trxFileName
if (Test-Path $trxPath) {
    [xml]$trx = Get-Content $trxPath
    $counters = $trx.TestRun.ResultSummary.Counters
    Write-Host "Test Results: Total=$($counters.total), Passed=$($counters.passed), Failed=$($counters.failed)" -ForegroundColor Cyan
    if ([int]$counters.failed -gt 0 -or [int]$counters.passed -eq 0) {
        $testExitCode = 1
    }
}

$statusColor = if ($testExitCode -eq 0) { "Green" } else { "Red" }; Write-Host "dotnet test completed with exit code: $testExitCode" -ForegroundColor $statusColor

# 4. Summarize results
Write-Host "`n=== Performance Measurement Results Summary ===" -ForegroundColor Cyan
$summaryFiles = Get-ChildItem -Path $PerfOutputDir -Filter "*-summary.csv"

foreach ($sumFile in $summaryFiles) {
    Write-Host "`nSummary File: $($sumFile.FullName)" -ForegroundColor Green
    Get-Content $sumFile.FullName | Format-Table -AutoSize
}

$csvFiles = Get-ChildItem -Path $PerfOutputDir -Filter "*.csv"
Write-Host "`nTotal CSV Files in $PerfOutputDir : $($csvFiles.Count)" -ForegroundColor Cyan
foreach ($f in $csvFiles) {
    Write-Host " - $($f.Name) ($($f.Length) bytes)"
}

exit $testExitCode
