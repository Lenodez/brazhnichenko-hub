<#
.SYNOPSIS
  Builds LocalAIHub.exe, LocalAIHub.Mcp.exe, packs payload.zip, and compiles universal GUI installer dist\LocalAIHub-Setup.exe.
#>
param(
    [string]$Version = "",
    [switch]$KeepLocalApp
)

$ErrorActionPreference = 'Stop'
$root = 'D:\LocalMediaAI\LocalAIHub'
$csprojPath = Join-Path $root 'LocalAIHub.csproj'
$nuget = Join-Path $root 'NuGet.Config'
$appDir = if ($KeepLocalApp) { Join-Path $root 'Installer\build_out' } else { Join-Path $root 'app' }
$stageDir = Join-Path $root 'Installer\stage'
$resDir = Join-Path $root 'Installer\Resources'
$distDir = Join-Path $root 'dist'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projXml = Get-Content $csprojPath -Raw
    $Version = ($projXml.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '2.2.1' }
}

if ($KeepLocalApp -and (Test-Path $appDir)) { Remove-Item $appDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

Write-Host "Building Brazhnichenko Hub v$Version (out: $appDir)..." -ForegroundColor Yellow

Write-Host '[1/4] Publishing LocalAIHub.exe...' -ForegroundColor Cyan
dotnet publish $csprojPath -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $appDir --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "Failed to publish LocalAIHub.exe (exit $LASTEXITCODE)" }

Write-Host '[2/4] Publishing LocalAIHub.Mcp.exe...' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'Mcp\LocalAIHub.Mcp.csproj') -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $appDir --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "Failed to publish LocalAIHub.Mcp.exe (exit $LASTEXITCODE)" }

Write-Host '[3/4] Packing payload.zip...' -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir, $resDir, $distDir -Force | Out-Null

Copy-Item (Join-Path $appDir 'LocalAIHub.exe') $stageDir
Copy-Item (Join-Path $appDir 'LocalAIHub.Mcp.exe') $stageDir
Copy-Item 'D:\LocalMediaAI\story_media.py' $stageDir
Copy-Item 'D:\LocalLLM\media-director-context.md' $stageDir
$sviStage = Join-Path $stageDir 'ComfyUI-LocalAIHub-SVI'
New-Item -ItemType Directory -Path $sviStage -Force | Out-Null
Copy-Item 'D:\LocalMediaAI\ComfyUI_windows_portable\ComfyUI\custom_nodes\ComfyUI-LocalAIHub-SVI\__init__.py' $sviStage

$payloadZip = Join-Path $resDir 'payload.zip'
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Remove-Item $stageDir -Recurse -Force
if ($KeepLocalApp -and (Test-Path $appDir)) { Remove-Item $appDir -Recurse -Force }

Write-Host '[4/4] Compiling LocalAIHub-Setup.exe...' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'Installer\LocalAIHub.Installer.csproj') -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $distDir --configfile $nuget
if ($LASTEXITCODE -ne 0) { throw "Failed to compile LocalAIHub-Setup.exe (exit $LASTEXITCODE)" }

$setupExe = Join-Path $distDir 'LocalAIHub-Setup.exe'
$mb = [math]::Round((Get-Item $setupExe).Length / 1MB, 2)
Write-Host "DONE! Universal installer v$Version ready: $setupExe ($mb MB)" -ForegroundColor Green
