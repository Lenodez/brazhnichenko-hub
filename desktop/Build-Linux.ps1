param(
    [ValidateSet("linux-x64", "linux-arm64")]
    [string]$Runtime = "linux-x64",
    [string]$Version = "4.3.0"
)

$ErrorActionPreference = "Stop"

$HubRoot = $PSScriptRoot
$CrossProj = Join-Path (Split-Path $HubRoot -Parent) "LocalAIHub.CrossPlatform\LocalAIHub.CrossPlatform.csproj"
$McpProj = Join-Path $HubRoot "Mcp\LocalAIHub.Mcp.csproj"
$NuGetCfg = Join-Path $HubRoot "NuGet.Config"

$ArchShort = $Runtime.Replace("linux-", "")
$DistRoot = Join-Path $HubRoot "dist"
$StageDir = Join-Path $DistRoot "linux-stage-$Runtime"
$PublishApp = Join-Path $StageDir "publish-app"
$PublishMcp = Join-Path $StageDir "publish-mcp"
$BundleParent = Join-Path $DistRoot "linux-$ArchShort"
$BundleFolderName = "BrazhnichenkoHub-linux-$ArchShort"
$BundleDir = Join-Path $BundleParent $BundleFolderName
$TarGzPath = Join-Path $DistRoot "BrazhnichenkoHub-linux-$ArchShort.tar.gz"

Write-Host "==> Publishing Brazhnichenko Hub ($Runtime, v$Version)..." -ForegroundColor Cyan

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
if (Test-Path $BundleDir) { Remove-Item $BundleDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishApp, $PublishMcp, $BundleDir -Force | Out-Null

dotnet restore $CrossProj --configfile $NuGetCfg -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore (CrossPlatform) failed" }

dotnet publish $CrossProj -c Release -r $Runtime --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $PublishApp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (CrossPlatform) failed" }

Write-Host "==> Publishing LocalAIHub.Mcp ($Runtime)..." -ForegroundColor Cyan
dotnet restore $McpProj --configfile $NuGetCfg -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore (Mcp) failed" }

dotnet publish $McpProj -c Release -r $Runtime --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $PublishMcp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Mcp) failed" }

Write-Host "==> Assembling Linux bundle: $BundleDir" -ForegroundColor Cyan

Copy-Item (Join-Path $PublishApp "BrazhnichenkoHub") (Join-Path $BundleDir "BrazhnichenkoHub") -Force
Copy-Item (Join-Path $PublishMcp "LocalAIHub.Mcp") (Join-Path $BundleDir "LocalAIHub.Mcp") -Force
Copy-Item (Join-Path $PublishApp "wwwroot") (Join-Path $BundleDir "wwwroot") -Recurse -Force

$StoryPy = Join-Path $PublishApp "story_media.py"
if (Test-Path $StoryPy) { Copy-Item $StoryPy $BundleDir -Force }

$SviDir = Join-Path $PublishApp "ComfyUI-LocalAIHub-SVI"
if (Test-Path $SviDir) { Copy-Item $SviDir $BundleDir -Recurse -Force }

$AppPng = Join-Path $HubRoot "Assets\app.png"
if (Test-Path $AppPng) { Copy-Item $AppPng (Join-Path $BundleDir "brazhnichenko-hub.png") -Force }

function Write-Utf8Lf([string]$Path, [string]$Text) {
    $lf = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    [System.IO.File]::WriteAllText($Path, $lf, (New-Object System.Text.UTF8Encoding($false)))
}

# run.sh
$RunSh = @'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"
exec "$SCRIPT_DIR/BrazhnichenkoHub" "$@"
'@
Write-Utf8Lf (Join-Path $BundleDir "run.sh") $RunSh

# install-desktop.sh
$InstallDesktopSh = @'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
mkdir -p "$APP_DIR" "$ICON_DIR"

if [ -f "$SCRIPT_DIR/brazhnichenko-hub.png" ]; then
  cp "$SCRIPT_DIR/brazhnichenko-hub.png" "$ICON_DIR/brazhnichenko-hub.png"
fi

cat > "$APP_DIR/brazhnichenko-hub.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Brazhnichenko Hub
Comment=Local AI Workstation (LLM, Vision, Photo, Video & Voice)
Exec="$SCRIPT_DIR/run.sh"
Icon=brazhnichenko-hub
Terminal=false
Categories=Development;AudioVideo;Graphics;Utility;
StartupNotify=true
EOF

chmod +x "$SCRIPT_DIR/BrazhnichenkoHub" "$SCRIPT_DIR/LocalAIHub.Mcp" "$SCRIPT_DIR/run.sh"
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
fi

echo "Ярлык Brazhnichenko Hub установлен: $APP_DIR/brazhnichenko-hub.desktop"
'@
Write-Utf8Lf (Join-Path $BundleDir "install-desktop.sh") $InstallDesktopSh

# uninstall-desktop.sh
$UninstallDesktopSh = @'
#!/usr/bin/env bash
set -euo pipefail
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
rm -f "$APP_DIR/brazhnichenko-hub.desktop" "$ICON_DIR/brazhnichenko-hub.png"
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
fi
echo "Ярлык Brazhnichenko Hub удалён."
'@
Write-Utf8Lf (Join-Path $BundleDir "uninstall-desktop.sh") $UninstallDesktopSh

# README-Linux.txt
$Readme = @"
Brazhnichenko Hub v$Version for Linux ($ArchShort)
==================================================

Быстрый запуск:
  1. Распакуй архив:
     tar -xzf BrazhnichenkoHub-linux-$ArchShort.tar.gz
     cd $BundleFolderName
  2. Запусти Хаб:
     ./run.sh
     (автоматически откроется интерфейс в браузере: http://127.0.0.1:17900)
     Для headless-сервера / SSH: ./run.sh --no-browser --port 17900

Ярлык в меню приложений (GNOME / KDE / XFCE):
  ./install-desktop.sh
  (удаление ярлыка: ./uninstall-desktop.sh)

Рекомендуемые системные пакеты (по желанию):
  - Для ComfyUI (Фото/Видео):   sudo apt install git python3-venv
  - Для голоса и видео-аудио:   sudo apt install ffmpeg espeak-ng
  - Во вкладке «Система» в 1 клик ставятся:
      * llama.cpp (NVIDIA CUDA 13.5 / CUDA 12.8 + cudart, AMD ROCm, Vulkan или CPU)
      * ComfyUI (PyTorch venv)
      * Piper HD TTS (нейросетевой русский голос Dmitri)

Конфиги:  ~/.config/BrazhnichenkoHub
Модели:   ~/BrazhnichenkoHub
"@
Write-Utf8Lf (Join-Path $BundleDir "README-Linux.txt") $Readme

# Pack .tar.gz using .NET System.Formats.Tar with exact POSIX 0755 executable bits!
Write-Host "==> Packing POSIX tar.gz archive: $TarGzPath" -ForegroundColor Cyan
if (Test-Path $TarGzPath) { Remove-Item $TarGzPath -Force }

Add-Type -AssemblyName System.Formats.Tar
Add-Type -AssemblyName System.IO.Compression

$execNames = @("BrazhnichenkoHub", "LocalAIHub.Mcp", "run.sh", "install-desktop.sh", "uninstall-desktop.sh")

$fs = [System.IO.File]::Create($TarGzPath)
try {
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
    try {
        $tw = New-Object System.Formats.Tar.TarWriter($gz, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
        try {
            $modeExec = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor [System.IO.UnixFileMode]::UserExecute `
                   -bor [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::GroupExecute `
                   -bor [System.IO.UnixFileMode]::OtherRead -bor [System.IO.UnixFileMode]::OtherExecute
            $modeFile = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite `
                   -bor [System.IO.UnixFileMode]::GroupRead -bor [System.IO.UnixFileMode]::OtherRead

            Get-ChildItem -Path $BundleDir -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($BundleParent.Length + 1).Replace('\', '/')
                $isExec = $execNames -contains $_.Name
                $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, $rel)
                $entry.Mode = if ($isExec) { $modeExec } else { $modeFile }
                $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
                $entry.DataStream = New-Object System.IO.MemoryStream(,$bytes)
                $tw.WriteEntry($entry)
                $entry.DataStream.Dispose()
            }
        } finally {
            $tw.Dispose()
        }
    } finally {
        $gz.Dispose()
    }
} finally {
    $fs.Dispose()
}

Remove-Item $StageDir -Recurse -Force
$sizeMB = [Math]::Round((Get-Item $TarGzPath).Length / 1MB, 1)
Write-Host "==> SUCCESS! Ready: $TarGzPath ($sizeMB MB)" -ForegroundColor Green
