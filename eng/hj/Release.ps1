#Requires -Version 7.0

[CmdletBinding()]
param (
    [int]$HjNumber = 0,
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$SkipMsi,
    [switch]$Tag,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

# (a) Check initial git status
$initialStatus = (git -C $repoRoot status --porcelain)
if ($initialStatus -and (-not $AllowDirty)) {
    throw "Working tree is dirty. Stash or commit changes, or pass -AllowDirty."
}

# (b) Read BUILD_VERSION_BASE from workflow
$workflowPath = Join-Path $repoRoot ".github/workflows/_app-build-core.yml"
if (-not (Test-Path $workflowPath)) {
    throw "Workflow file not found at '$workflowPath'"
}

$workflowContent = Get-Content -Path $workflowPath -Raw
if ($workflowContent -match '(?m)^\s*BUILD_VERSION_BASE:\s*([0-9.]+)') {
    $baseVersion = $Matches[1].Trim()
} else {
    throw "Could not find BUILD_VERSION_BASE in '$workflowPath'"
}

# Resolve HjNumber if not passed
if ($HjNumber -le 0) {
    $versionFile = Join-Path $PSScriptRoot "VERSION"
    if (-not (Test-Path $versionFile)) {
        throw "VERSION file not found at '$versionFile'"
    }
    $rawHj = (Get-Content -Path $versionFile -Raw).Trim()
    if (-not [int]::TryParse($rawHj, [ref]$HjNumber)) {
        throw "Invalid integer in VERSION file: '$rawHj'"
    }
}

# (c) Compute versions
$numericVersion = "$baseVersion.$HjNumber"
$stringVersion = "$baseVersion-hj-$HjNumber"
$suffix = "-hj-$HjNumber"

Write-Host "=== Git Extensions Release Build ===" -ForegroundColor Cyan
Write-Host "Base Version:    $baseVersion"
Write-Host "HJ Number:       $HjNumber"
Write-Host "Numeric Version: $numericVersion"
Write-Host "String Version:  $stringVersion"
Write-Host "Configuration:   $Configuration"
Write-Host "SkipPublish:     $SkipPublish"
Write-Host "SkipMsi:         $SkipMsi"
Write-Host "Tag:             $Tag"
Write-Host "Repo Root:       $repoRoot"

$shortSha = (git -C $repoRoot rev-parse --short HEAD).Trim()

try {
    # (d) Stamp version using set_version_to.cs
    Write-Host "`nStamping version using eng/set_version_to.cs..." -ForegroundColor Yellow
    Push-Location (Join-Path $repoRoot "eng")
    try {
        dotnet run set_version_to.cs -- -v $numericVersion -t $stringVersion
        if ($LASTEXITCODE -ne 0) {
            throw "set_version_to.cs failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    # (e) Build native components if C++ tools are available
    $vswherePath = $null
    $vswhereCmd = Get-Command vswhere -ErrorAction SilentlyContinue
    if ($vswhereCmd) {
        $vswherePath = $vswhereCmd.Source
    } else {
        $installerVswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $installerVswhere) {
            $vswherePath = $installerVswhere
        } else {
            $nugetVswhere = Get-ChildItem -Path (Join-Path $env:USERPROFILE ".nuget\packages\vswhere") -Filter "vswhere.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
            if ($nugetVswhere) {
                $vswherePath = $nugetVswhere[-1]
            }
        }
    }

    $vcToolsPath = $null
    if ($vswherePath) {
        $vcToolsOutput = (& $vswherePath -latest -products * -property installationPath -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64)
        if ($vcToolsOutput) {
            $vcToolsPath = $vcToolsOutput.Trim()
        }
    }

    if ($vcToolsPath) {
        Write-Host "`nBuilding native components ($Configuration)..." -ForegroundColor Yellow
        Push-Location $repoRoot
        try {
            dotnet restore src/native/build.proj
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet restore src/native/build.proj failed with exit code $LASTEXITCODE"
            }
            dotnet build src/native/build.proj -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet build src/native/build.proj failed with exit code $LASTEXITCODE"
            }
        } finally {
            Pop-Location
        }
    } else {
        Write-Warning "Visual Studio C++ build tools (VC.Tools.x86.x64) not detected. Skipping native components build."
    }

    # (f) Build .NET
    Write-Host "`nBuilding solution ($Configuration)..." -ForegroundColor Yellow
    Push-Location $repoRoot
    try {
        dotnet build -c $Configuration --verbosity q --nologo "/p:GitCommit=$shortSha"
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    # (g) Publish if not skipped
    if (-not $SkipPublish) {
        if (-not $SkipMsi) {
            $hasVsMsbuild = $false
            if ($vswherePath) {
                $vsMsbuildOutput = (& $vswherePath -latest -prerelease -products * -property installationPath -requires Microsoft.Component.MSBuild)
                if ($vsMsbuildOutput -and (Test-Path (Join-Path $vsMsbuildOutput.Trim() "MSBuild\Current\Bin\MSBuild.exe"))) {
                    $hasVsMsbuild = $true
                }
            }

            if (-not $hasVsMsbuild) {
                Write-Warning "VS MSBuild not detected via vswhere. Skipping MSI creation."
                $SkipMsi = $true
            } elseif (-not $vcToolsPath) {
                Write-Warning "Native components were not built. Skipping MSI creation."
                $SkipMsi = $true
            }
        }

        Write-Host "`nPublishing solution ($Configuration)..." -ForegroundColor Yellow
        $prevSuffix = $env:ARTIFACT_BUILD_SUFFIX
        $env:ARTIFACT_BUILD_SUFFIX = $suffix
        try {
            Push-Location $repoRoot
            try {
                $publishArgs = @(
                    "publish",
                    "-c", $Configuration,
                    "--no-build",
                    "/p:GitCommit=$shortSha"
                )
                if ($SkipMsi) {
                    $publishArgs += "/p:HjSkipMsi=true"
                }

                dotnet @publishArgs
                if ($LASTEXITCODE -ne 0) {
                    throw "dotnet publish failed with exit code $LASTEXITCODE"
                }
            } finally {
                Pop-Location
            }
        } finally {
            $env:ARTIFACT_BUILD_SUFFIX = $prevSuffix
        }
    }
} finally {
    # (h) Revert stamp files
    Write-Host "`nReverting stamped version files..." -ForegroundColor Yellow
    $stampFilesMain = @(
        "CommonAssemblyInfo.cs",
        "CommonAssemblyInfoExternals.cs",
        "externals/NetSpell.SpellChecker/Properties/AssemblyInfo.cs",
        "src/native/GitExtensionsShellEx/GitExtensionsShellEx.rc",
        "src/native/GitExtSshAskPass/SshAskPass.rc2"
    )

    foreach ($relFile in $stampFilesMain) {
        $fullPath = Join-Path $repoRoot $relFile
        if (Test-Path $fullPath) {
            $diffCheck = (git -C $repoRoot status --porcelain -- $relFile)
            if ($diffCheck) {
                git -C $repoRoot checkout -- $relFile
            }
        }
    }

    # Revert any remaining AssemblyInfo.cs in externals tracked by main repo
    $mainStatus = (git -C $repoRoot status --porcelain)
    if ($mainStatus) {
        foreach ($line in ($mainStatus -split "`r?`n")) {
            if ($line -match '^\s*M\s+(externals[/\\]\S+AssemblyInfo\.cs)$') {
                $mainAssemblyFile = $Matches[1].Trim()
                git -C $repoRoot checkout -- $mainAssemblyFile
            }
        }
    }

    $submodules = @(
        "externals/Git.hub",
        "externals/conemu-inside",
        "externals/ICSharpCode.TextEditor"
    )

    foreach ($sub in $submodules) {
        $subPath = Join-Path $repoRoot $sub
        if (Test-Path $subPath) {
            $subStatus = (git -C $subPath status --porcelain)
            if ($subStatus) {
                foreach ($line in ($subStatus -split "`r?`n")) {
                    if ($line -match '^\s*M\s+(.+AssemblyInfo\.cs)$') {
                        $assemblyFile = $Matches[1].Trim()
                        git -C $subPath checkout -- $assemblyFile
                    }
                }
            }
        }
    }

    # Verify status matches initial
    $revertedStatus = (git -C $repoRoot status --porcelain)
    $initialStr = ($initialStatus -join "`n").Trim()
    $revertedStr = ($revertedStatus -join "`n").Trim()
    if ($initialStr -ne $revertedStr) {
        Write-Warning "Working tree status after revert does not match initial status!"
        Write-Warning "Initial:`n$initialStr"
        Write-Warning "Current:`n$revertedStr"
    } else {
        Write-Host "Working tree status successfully restored." -ForegroundColor Green
    }
}

# (i) Create tag if requested
if ($Tag) {
    $tagName = "hj/v$stringVersion"
    $existingTag = (git -C $repoRoot tag -l $tagName)
    if ($existingTag) {
        throw "Tag '$tagName' already exists."
    }

    $mergeBase = (git -C $repoRoot merge-base HEAD upstream/master).Trim()
    $tagMessage = "Git Extensions $stringVersion`n`nupstream-base: $mergeBase"
    git -C $repoRoot tag -a $tagName -m $tagMessage
    Write-Host "Created tag: $tagName" -ForegroundColor Green
}

# (j) Output summary
Write-Host "`n=== Release Summary ===" -ForegroundColor Cyan
Write-Host "Version String:  $stringVersion"
Write-Host "Numeric Version: $numericVersion"

$exePath = Join-Path $repoRoot "artifacts\$Configuration\bin\GitExtensions\net10.0-windows\GitExtensions.exe"
$dllPath = Join-Path $repoRoot "artifacts\$Configuration\bin\GitExtensions\net10.0-windows\GitExtensions.dll"
if (Test-Path $exePath) {
    Write-Host "Build EXE:       $exePath"
} elseif (Test-Path $dllPath) {
    Write-Host "Build DLL:       $dllPath"
}

$publishDir = Join-Path $repoRoot "artifacts\$Configuration\publish"
if (Test-Path $publishDir) {
    $zipFiles = Get-ChildItem -Path $publishDir -Filter "*.zip"
    $msiFiles = Get-ChildItem -Path $publishDir -Filter "*.msi"
    foreach ($z in $zipFiles) {
        Write-Host "Generated ZIP:   $($z.FullName)"
    }
    foreach ($m in $msiFiles) {
        Write-Host "Generated MSI:   $($m.FullName)"
    }
}
