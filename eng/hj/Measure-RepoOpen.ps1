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

Write-Host "Starting RepositoryOpenPerfTests via dotnet test..." -ForegroundColor Yellow

$testProj = Join-Path $worktreeRoot "tests\app\IntegrationTests\UI.IntegrationTests\UI.IntegrationTests.csproj"
dotnet test $testProj --filter "FullyQualifiedName~RepositoryOpenPerfTests" --logger "trx" --blame-hang-timeout 15m

$testExitCode = $LASTEXITCODE
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
