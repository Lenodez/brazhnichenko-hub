<#
.SYNOPSIS
  Builds Brazhnichenko Hub, computes SHA-256, uploads versioned installer to VPS release storage, and atomically updates latest.json.
.EXAMPLE
  .\Publish-Release.ps1 -Version "2.2.1" -Notes "Переименование в Brazhnichenko Hub"
  .\Publish-Release.ps1 -Version "2.2.1" -Notes "Тест автообновления" -KeepLocalApp -TagGit
#>
param(
    [string]$Version = "",
    [string]$Notes = "Обновление Brazhnichenko Hub",
    [string]$VpsHost = $env:HUB_VPS_HOST,
    [string]$RemoteRoot = "/var/www/localaihub",
    [switch]$KeepLocalApp,
    [switch]$TagGit
)

$ErrorActionPreference = 'Stop'
$root = 'D:\LocalMediaAI\LocalAIHub'
$csprojPath = Join-Path $root 'LocalAIHub.csproj'

# Sync <Version> in LocalAIHub.csproj if -Version was explicitly provided
$rawCsproj = Get-Content $csprojPath -Raw -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($rawCsproj -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    } else {
        $Version = '2.2.1'
    }
} else {
    $Version = $Version.Trim().TrimStart('v', 'V')
    if ($rawCsproj -match '<Version>[^<]+</Version>') {
        $updated = [regex]::Replace($rawCsproj, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
        Set-Content -Path $csprojPath -Value $updated -Encoding UTF8 -NoNewline
    }
}

Write-Host "=== Publishing Brazhnichenko Hub v$Version to $VpsHost ===" -ForegroundColor Cyan

if (-not $KeepLocalApp) {
    Get-Process -Name LocalAIHub,LocalAIHub-Setup -ErrorAction SilentlyContinue | Stop-Process -Force
} else {
    Get-Process -Name LocalAIHub-Setup -ErrorAction SilentlyContinue | Stop-Process -Force
}

# 1. Build all binaries & Setup.exe
& (Join-Path $root 'Build-Installer.ps1') -Version $Version -KeepLocalApp:$KeepLocalApp

$setupExe = Join-Path $root 'dist\LocalAIHub-Setup.exe'
$fi = Get-Item $setupExe
$sha256 = (Get-FileHash -Path $setupExe -Algorithm SHA256).Hash.ToUpperInvariant()
$remoteFile = "LocalAIHub-Setup-$Version.exe"

Write-Host "Uploading $remoteFile ($([math]::Round($fi.Length / 1MB, 2)) MB, SHA256=$sha256)..." -ForegroundColor Cyan
scp -o BatchMode=yes $setupExe "${VpsHost}:${RemoteRoot}/releases/${remoteFile}.tmp"
if ($LASTEXITCODE -ne 0) { throw "SCP upload failed!" }

# 2. Build latest.json locally and upload atomically
$manifest = [ordered]@{
    version      = $Version
    notes        = $Notes
    url          = "/releases/$remoteFile"
    sha256       = $sha256
    size_bytes   = $fi.Length
    published_at = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
} | ConvertTo-Json -Depth 4

$tmpJson = Join-Path $env:TEMP "localaihub-latest.json"
[System.IO.File]::WriteAllText($tmpJson, $manifest, (New-Object System.Text.UTF8Encoding($false)))

scp -o BatchMode=yes $tmpJson "${VpsHost}:${RemoteRoot}/latest.json.tmp"
if ($LASTEXITCODE -ne 0) { throw "SCP latest.json failed!" }
Remove-Item -Force -ErrorAction SilentlyContinue $tmpJson

ssh -o BatchMode=yes $VpsHost "mv '${RemoteRoot}/releases/${remoteFile}.tmp' '${RemoteRoot}/releases/${remoteFile}' && chmod 644 '${RemoteRoot}/releases/${remoteFile}' && mv '${RemoteRoot}/latest.json.tmp' '${RemoteRoot}/latest.json' && chmod 644 '${RemoteRoot}/latest.json'"
if ($LASTEXITCODE -ne 0) { throw "Remote atomic publish failed!" }

# 3. Rotate old release archives on VPS (keep newest 10)
Write-Host "Rotating release archives on VPS (keeping newest 10)..." -ForegroundColor Cyan
ssh -o BatchMode=yes $VpsHost "cd '${RemoteRoot}/releases' && ls -t LocalAIHub-Setup-*.exe 2>/dev/null | tail -n +11 | xargs -r rm -f && ls -lh '${RemoteRoot}/latest.json' '${RemoteRoot}/releases/'"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Release archive rotation on VPS failed (non-fatal)."
}

# 4. Optional Git tag / reminder if repository is initialized (non-fatal)
if (Test-Path (Join-Path $root '.git')) {
    $tagName = "v$Version"
    if ($TagGit) {
        try {
            Push-Location $root
            $existingTag = git tag -l $tagName 2>$null
            if ($existingTag) {
                Write-Warning "Git tag '$tagName' already exists; skipping."
            } else {
                git tag -a $tagName -m "Release $tagName`: $Notes"
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "Created git tag $tagName." -ForegroundColor Cyan
                } else {
                    Write-Warning "Failed to create git tag '$tagName' (exit code $LASTEXITCODE, non-fatal)."
                }
            }
        } catch {
            Write-Warning "Git tagging failed (non-fatal): $_"
        } finally {
            Pop-Location
        }
    } else {
        Write-Host "Git repo detected in $root (pass -TagGit to auto-tag $tagName)." -ForegroundColor DarkGray
    }
}

Write-Host "PUBLISHED! Brazhnichenko Hub v$Version is now live on VPS." -ForegroundColor Green
