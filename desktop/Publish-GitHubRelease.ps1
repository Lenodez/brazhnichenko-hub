<#
.SYNOPSIS
  One-click Open Source release pipeline for Brazhnichenko Hub:
  1) Builds signed Android ARM64 APK (Hexagon NPU + Adreno GPU + Sherpa-ONNX offline STT)
  2) Builds Windows x64 installer (LocalAIHub-Setup.exe) with GitHub Releases auto-updater
  3) Syncs LocalAIHub + LocalAIHub.Android + LocalAIHub.CrossPlatform into the public git monorepo
  4) Audits staged sources for hardcoded secrets/keys (aborts if any found)
  5) Commits, pushes to GitHub, and creates/updates GitHub Release with all ready binaries
.EXAMPLE
  pwsh -ExecutionPolicy Bypass -File .\Publish-GitHubRelease.ps1 -Version "4.8.0" -Notes "100% offline STT + Windows GitHub auto-updater"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Notes = "",
    [string]$Repo = "Lenodez/brazhnichenko-hub",
    [string]$MonorepoDir = "d:\LocalMediaAI\brazhnichenko-hub",
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [switch]$SkipCrossPlatformAssets
)

$ErrorActionPreference = "Stop"
$env:PATH = "C:\Program Files\GitHub CLI;$env:PATH"

$Version = $Version.Trim().TrimStart('v', 'V')
$Tag = "v$Version"
$HubRoot = $PSScriptRoot
$ParentRoot = Split-Path $HubRoot -Parent
$AndroidRoot = Join-Path $ParentRoot "LocalAIHub.Android"
$CrossRoot = Join-Path $ParentRoot "LocalAIHub.CrossPlatform"
$DistDir = Join-Path $HubRoot "dist"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Brazhnichenko Hub One-Click Release Pipeline: $Tag" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Build Android ARM64 APK
if (-not $SkipAndroid) {
    Write-Host "`n[1/5] Building signed Android ARM64 APK ($Tag)..." -ForegroundColor Yellow
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $HubRoot "Build-Android.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "Android build failed" }
}

# 2. Build Windows Setup.exe
if (-not $SkipWindows) {
    Write-Host "`n[2/5] Building Windows LocalAIHub-Setup.exe ($Tag)..." -ForegroundColor Yellow
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $HubRoot "Build-Installer.ps1") -Version $Version -KeepLocalApp
    if ($LASTEXITCODE -ne 0) { throw "Windows build failed" }
}

# 3. Sync clean sources into Open Source monorepo
Write-Host "`n[3/5] Syncing clean sources into $MonorepoDir..." -ForegroundColor Yellow
if (-not (Test-Path (Join-Path $MonorepoDir ".git"))) {
    throw "Git monorepo not found at $MonorepoDir"
}

$null = robocopy $AndroidRoot (Join-Path $MonorepoDir "android") /MIR /XD build .git .gradle .idea /XF *.apk *.jks *.keystore *.pass *.gguf *.onnx
if ($LASTEXITCODE -gt 7) { throw "robocopy android failed (code $LASTEXITCODE)" }

$null = robocopy $HubRoot (Join-Path $MonorepoDir "desktop") /MIR /XD bin obj dist .git .vs design-system app /XF *.apk *.exe *.msi *.jks *.keystore *.pass *.gguf *.tar.gz *.zip
if ($LASTEXITCODE -gt 7) { throw "robocopy desktop failed (code $LASTEXITCODE)" }

if (Test-Path $CrossRoot) {
    $null = robocopy $CrossRoot (Join-Path $MonorepoDir "crossplatform") /MIR /XD bin obj dist .git .vs /XF *.exe *.apk *.tar.gz *.zip *.gguf *.jks *.pass
    if ($LASTEXITCODE -gt 7) { throw "robocopy crossplatform failed (code $LASTEXITCODE)" }
}

# Scrub any accidental local VPS host default in desktop/Publish-Release.ps1 if present
$pubRel = Join-Path $MonorepoDir "desktop\Publish-Release.ps1"
if (Test-Path $pubRel) {
    $raw = Get-Content $pubRel -Raw -Encoding UTF8
    $clean = $raw -replace 'root@\d+\.\d+\.\d+\.\d+', '$env:HUB_VPS_HOST'
    if ($clean -ne $raw) { Set-Content $pubRel $clean -Encoding UTF8 -NoNewline }
}

# 4. Mandatory Secret & Binary Audit before git push
Write-Host "`n[4/5] Auditing monorepo for secrets, keystores & large binaries..." -ForegroundColor Yellow
$secretHits = Get-ChildItem $MonorepoDir -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and $_.Name -ne 'Publish-GitHubRelease.ps1' -and $_.Extension -notmatch '^\.(png|jpg|ico|so)$' } |
    Select-String -Pattern '(hf_[A-Za-z0-9]{15,}|ghp_[A-Za-z0-9]{15,}|gho_[A-Za-z0-9]{15,}|tvly-[A-Za-z0-9]{15,}|sk-[A-Za-z0-9]{20,}|root@\d+\.\d+\.\d+\.\d+|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY)'
if ($secretHits) {
    $secretHits | Format-Table Path, LineNumber, Line -AutoSize
    throw "ABORTED: Potential secret detected in monorepo!"
}

$forbiddenFiles = Get-ChildItem $MonorepoDir -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and $_.Name -match '\.(jks|keystore|pass|apk|gguf|exe|msi)$' }
if ($forbiddenFiles) {
    $forbiddenFiles | Select-Object FullName
    throw "ABORTED: Forbidden binary/keystore file found in monorepo!"
}
Write-Host "  Audit passed cleanly (0 secrets, 0 keystores)." -ForegroundColor Green

# 5. Commit, Push & Publish GitHub Release
Write-Host "`n[5/5] Pushing to GitHub & publishing Release $Tag..." -ForegroundColor Yellow
Push-Location $MonorepoDir
try {
    git add -A
    $pending = git status --porcelain
    if ($pending) {
        git commit -m "Release $Tag"
    }
    git push origin main
    git tag -f $Tag
    git push --force origin $Tag

    $assets = New-Object System.Collections.Generic.List[string]
    $apkPath = Join-Path $DistDir "BrazhnichenkoHub-Android-arm64.apk"
    $exePath = Join-Path $DistDir "LocalAIHub-Setup.exe"
    if (Test-Path $apkPath) { $assets.Add($apkPath) }
    if (Test-Path $exePath) { $assets.Add($exePath) }
    if (-not $SkipCrossPlatformAssets) {
        foreach ($extra in @("BrazhnichenkoHub-macOS-arm64.tar.gz", "BrazhnichenkoHub-linux-x64.tar.gz", "BrazhnichenkoHub-linux-arm64.tar.gz")) {
            $p = Join-Path $DistDir $extra
            if (Test-Path $p) { $assets.Add($p) }
        }
    }

    if ([string]::IsNullOrWhiteSpace($Notes)) {
        $Notes = "Brazhnichenko Hub $Tag — Android ARM64 NPU + Windows Desktop + macOS + Linux."
    }

    $releaseExists = $false
    gh release view $Tag --repo $Repo *> $null
    if ($LASTEXITCODE -eq 0) { $releaseExists = $true }

    if ($releaseExists) {
        gh release edit $Tag --repo $Repo --title "Brazhnichenko Hub $Tag (Android + Windows + macOS + Linux)" --notes $Notes
        if ($assets.Count -gt 0) {
            gh release upload $Tag @assets --repo $Repo --clobber
        }
    } else {
        gh release create $Tag @assets --repo $Repo --title "Brazhnichenko Hub $Tag (Android + Windows + macOS + Linux)" --notes $Notes
    }
    if ($LASTEXITCODE -ne 0) { throw "gh release publish failed" }
} finally {
    Pop-Location
}

Write-Host "`nDONE! Published https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
