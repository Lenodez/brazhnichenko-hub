param(
    [string]$Runtime = "osx-arm64",
    [string]$Version = "4.3.0"
)

$ErrorActionPreference = "Stop"

$HubRoot = $PSScriptRoot
$CrossProj = Join-Path (Split-Path $HubRoot -Parent) "LocalAIHub.CrossPlatform\LocalAIHub.CrossPlatform.csproj"
$McpProj = Join-Path $HubRoot "Mcp\LocalAIHub.Mcp.csproj"
$NuGetCfg = Join-Path $HubRoot "NuGet.Config"

$DistRoot = Join-Path $HubRoot "dist"
$StageDir = Join-Path $DistRoot "macOS-stage-$Runtime"
$PublishApp = Join-Path $StageDir "publish-app"
$PublishMcp = Join-Path $StageDir "publish-mcp"
$BundleRoot = Join-Path $DistRoot "macOS"
$AppBundle = Join-Path $BundleRoot "Brazhnichenko Hub.app"
$TarGzPath = Join-Path $DistRoot "BrazhnichenkoHub-macOS-$($Runtime.Replace('osx-','')).tar.gz"

Write-Host "==> Publishing Brazhnichenko Hub ($Runtime, v$Version)..." -ForegroundColor Cyan

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
if (Test-Path $AppBundle) { Remove-Item $AppBundle -Recurse -Force }
New-Item -ItemType Directory -Path $PublishApp, $PublishMcp, $BundleRoot -Force | Out-Null

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

Write-Host "==> Assembling macOS .app bundle: $AppBundle" -ForegroundColor Cyan
$ContentsDir = Join-Path $AppBundle "Contents"
$MacOsDir = Join-Path $ContentsDir "MacOS"
$ResourcesDir = Join-Path $ContentsDir "Resources"
New-Item -ItemType Directory -Path $MacOsDir, $ResourcesDir -Force | Out-Null

# Copy binaries & runtime assets into Contents/MacOS
Copy-Item (Join-Path $PublishApp "BrazhnichenkoHub") (Join-Path $MacOsDir "BrazhnichenkoHub") -Force
Copy-Item (Join-Path $PublishMcp "LocalAIHub.Mcp") (Join-Path $MacOsDir "LocalAIHub.Mcp") -Force
Copy-Item (Join-Path $PublishApp "wwwroot") (Join-Path $MacOsDir "wwwroot") -Recurse -Force

$StoryPy = Join-Path $PublishApp "story_media.py"
if (Test-Path $StoryPy) { Copy-Item $StoryPy $MacOsDir -Force }

$SviDir = Join-Path $PublishApp "ComfyUI-LocalAIHub-SVI"
if (Test-Path $SviDir) { Copy-Item $SviDir $MacOsDir -Recurse -Force }

$AppPng = Join-Path $HubRoot "Assets\app.png"
if (Test-Path $AppPng) { Copy-Item $AppPng (Join-Path $ResourcesDir "app.png") -Force }

# Write Info.plist
$InfoPlist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Brazhnichenko Hub</string>
  <key>CFBundleDisplayName</key>
  <string>Brazhnichenko Hub</string>
  <key>CFBundleIdentifier</key>
  <string>com.brazhnichenko.hub</string>
  <key>CFBundleVersion</key>
  <string>$Version</string>
  <key>CFBundleShortVersionString</key>
  <string>$Version</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>BrazhnichenkoHub</string>
  <key>CFBundleIconFile</key>
  <string>app.png</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
"@
[System.IO.File]::WriteAllText((Join-Path $ContentsDir "Info.plist"), $InfoPlist, (New-Object System.Text.UTF8Encoding($false)))

# Write README-macOS.txt
$Readme = @"
Brazhnichenko Hub v$Version for macOS (Apple Silicon M1–M6)
===========================================================

1. Перенеси «Brazhnichenko Hub.app» в папку /Applications (Программы).
2. Так как сборка не подписана платным сертификатом Apple Developer, перед первым запуском выполни в Терминале одну команду:

   xattr -dr com.apple.quarantine "/Applications/Brazhnichenko Hub.app"

3. Запусти «Brazhnichenko Hub.app» двойным кликом — откроется тёмный интерфейс в браузере (http://127.0.0.1:17900).
4. Во вкладке «Система» нажми «Установить llama.cpp (Apple Metal)» — движок для Apple Silicon скачается автоматически (~16 МБ).
5. Во вкладке «Модели и Mac M1–M6 / GPU» автоматически определится твой чип (M1–M6) и объём Unified Memory с расчётом скорости (tok/s).
"@
$ReadmePath = Join-Path $BundleRoot "README-macOS.txt"
[System.IO.File]::WriteAllText($ReadmePath, $Readme, (New-Object System.Text.UTF8Encoding($false)))

# Build .tar.gz using .NET System.Formats.Tar with proper POSIX 0755 executable bits!
Write-Host "==> Packing POSIX tar.gz archive: $TarGzPath" -ForegroundColor Cyan
if (Test-Path $TarGzPath) { Remove-Item $TarGzPath -Force }

Add-Type -AssemblyName System.Formats.Tar
Add-Type -AssemblyName System.IO.Compression

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

            function Add-TarFile([string]$fullPath, [string]$entryName, [bool]$isExec) {
                $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, $entryName)
                $entry.Mode = if ($isExec) { $modeExec } else { $modeFile }
                $bytes = [System.IO.File]::ReadAllBytes($fullPath)
                $entry.DataStream = New-Object System.IO.MemoryStream(,$bytes)
                $tw.WriteEntry($entry)
                $entry.DataStream.Dispose()
            }

            Add-TarFile $ReadmePath "README-macOS.txt" $false

            Get-ChildItem -Path $AppBundle -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($BundleRoot.Length + 1).Replace('\', '/')
                $isExec = ($_.Name -eq "BrazhnichenkoHub" -or $_.Name -eq "LocalAIHub.Mcp")
                Add-TarFile $_.FullName $rel $isExec
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
