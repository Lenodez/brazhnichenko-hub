param(
    [string]$Version = "4.7.0",
    [string]$AndroidSdk = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { "d:\LocalMediaAI\android-sdk" })
)

$ErrorActionPreference = "Stop"

$HubRoot = $PSScriptRoot
$ParentDir = Split-Path $HubRoot -Parent
$AndroidProj = if (Test-Path (Join-Path $ParentDir "LocalAIHub.Android")) {
    Join-Path $ParentDir "LocalAIHub.Android"
} elseif (Test-Path (Join-Path $ParentDir "android")) {
    Join-Path $ParentDir "android"
} else {
    throw "Android project directory not found (expected sibling 'LocalAIHub.Android' or 'android')"
}
$DistRoot = Join-Path $HubRoot "dist"
$CacheDir = Join-Path $DistRoot "android-cache"
$BuildDir = Join-Path $AndroidProj "build"

$BuildTools = Join-Path $AndroidSdk "build-tools\35.0.0"
$AndroidJar = Join-Path $AndroidSdk "platforms\android-35\android.jar"
$Aapt2 = Join-Path $BuildTools "aapt2.exe"
$D8 = Join-Path $BuildTools "d8.bat"
$ZipAlign = Join-Path $BuildTools "zipalign.exe"
$ApkSigner = Join-Path $BuildTools "apksigner.bat"

if (-not (Test-Path $AndroidJar)) { throw "Android platform 35 not found at $AndroidJar" }
if (-not (Test-Path $Aapt2)) { throw "aapt2 not found at $Aapt2" }

# 1. Ensure llama.cpp Android ARM64 Snapdragon archive (Adreno OpenCL GPU + Hexagon NPU HTP v73..v85 + CPU) is cached
New-Item -ItemType Directory -Path $CacheDir, $DistRoot -Force | Out-Null
$LlamaTar = Join-Path $CacheDir "llama-b12676-bin-android-arm64-snapdragon.tar.gz"
if (-not (Test-Path $LlamaTar)) {
    Write-Host "==> Downloading llama.cpp Android ARM64 Snapdragon..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://github.com/ggml-org/llama.cpp/releases/download/b12676/llama-b12676-bin-android-arm64-snapdragon.tar.gz" -OutFile $LlamaTar -Headers @{"User-Agent"="LocalAIHub/4.3"}
}

# 2. Extract llama-server + Snapdragon GPU/NPU/CPU runtime .so libraries
Write-Host "==> Staging Snapdragon GPU/NPU/CPU native libs (arm64-v8a)..." -ForegroundColor Cyan
if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
$StageNative = Join-Path $BuildDir "native-extract"
$JniDir = Join-Path $BuildDir "apk-staging\lib\arm64-v8a"
$ResDir = Join-Path $AndroidProj "res\mipmap-xxxhdpi"
New-Item -ItemType Directory -Path $StageNative, $JniDir, $ResDir -Force | Out-Null

# Copy app icon into res/mipmap-xxxhdpi/ic_launcher.png
$AppPng = Join-Path $HubRoot "Assets\app.png"
if (Test-Path $AppPng) { Copy-Item $AppPng (Join-Path $ResDir "ic_launcher.png") -Force }

tar -xzf $LlamaTar -C $StageNative
$LlamaRoot = (Get-ChildItem $StageNative -Directory | Select-Object -First 1).FullName

# Rename bin/llama-server -> libllama_server.so so Android SELinux allows execution from nativeLibraryDir!
Copy-Item (Join-Path $LlamaRoot "bin\llama-server") (Join-Path $JniDir "libllama_server.so") -Force

$requiredSos = @(
    "libllama-server-impl.so",
    "libllama-common.so",
    "libllama.so",
    "libmtmd.so",
    "libggml.so",
    "libggml-base.so",
    "libggml-cpu.so",
    "libggml-rpc.so",
    "libggml-opencl.so",
    "libggml-hexagon.so",
    "libggml-htp-v73.so",
    "libggml-htp-v75.so",
    "libggml-htp-v79.so",
    "libggml-htp-v81.so",
    "libggml-htp-v85.so"
)
foreach ($so in $requiredSos) {
    $src = Join-Path $LlamaRoot "lib\$so"
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $JniDir $so) -Force
    } else {
        Write-Warning "Optional/expected lib missing: $so"
    }
}

# 2b. Copy compiled native JNI bridge (libhub_jni.so) so llama-server runs in Android's classloader-namespace with Hexagon NPU + Adreno OpenCL!
$PrebuiltJni = Join-Path $AndroidProj "jni\prebuilt\arm64-v8a\libhub_jni.so"
if (-not (Test-Path $PrebuiltJni)) { throw "libhub_jni.so not found at $PrebuiltJni" }
Copy-Item $PrebuiltJni (Join-Path $JniDir "libhub_jni.so") -Force

# 3. Compile Android resources with aapt2
Write-Host "==> Compiling resources & linking base APK..." -ForegroundColor Cyan
$CompiledRes = Join-Path $BuildDir "res.zip"
$BaseApk = Join-Path $BuildDir "base.apk"
$GenJava = Join-Path $BuildDir "gen"
New-Item -ItemType Directory -Path $GenJava -Force | Out-Null

& $Aapt2 compile --dir (Join-Path $AndroidProj "res") -o $CompiledRes
if ($LASTEXITCODE -ne 0) { throw "aapt2 compile failed" }

& $Aapt2 link -I $AndroidJar `
    --manifest (Join-Path $AndroidProj "AndroidManifest.xml") `
    --java $GenJava `
    -o $BaseApk `
    $CompiledRes
if ($LASTEXITCODE -ne 0) { throw "aapt2 link failed" }

# 4. Compile Java sources -> classes -> classes.dex
Write-Host "==> Compiling Java sources & DEX..." -ForegroundColor Cyan
$ClassesDir = Join-Path $BuildDir "classes"
$DexDir = Join-Path $BuildDir "dex"
New-Item -ItemType Directory -Path $ClassesDir, $DexDir -Force | Out-Null

$javaFiles = @(Get-ChildItem -Path (Join-Path $AndroidProj "src") -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
$genFiles = @(Get-ChildItem -Path $GenJava -Recurse -Filter "*.java" | ForEach-Object { $_.FullName })
$allJava = $javaFiles + $genFiles

& javac -encoding UTF-8 --release 11 -cp $AndroidJar -d $ClassesDir @allJava
if ($LASTEXITCODE -ne 0) { throw "javac failed" }

$classFiles = @(Get-ChildItem -Path $ClassesDir -Recurse -Filter "*.class" | ForEach-Object { $_.FullName })
& $D8 --min-api 26 --lib $AndroidJar --output $DexDir @classFiles
if ($LASTEXITCODE -ne 0) { throw "d8 failed" }

# 5. Merge classes.dex + arm64-v8a native libs into APK zip
Write-Host "==> Packaging classes.dex & arm64-v8a Snapdragon libraries into APK..." -ForegroundColor Cyan
$UnsignedApk = Join-Path $BuildDir "unsigned.apk"
Copy-Item $BaseApk $UnsignedApk -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($UnsignedApk, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, (Join-Path $DexDir "classes.dex"), "classes.dex", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null

    # Pack assets/wwwroot/* with strict POSIX forward slashes (avoids aapt2 Windows backslash bug!)
    $AssetsRoot = Join-Path $AndroidProj "assets"
    Get-ChildItem -Path $AssetsRoot -Recurse -File | ForEach-Object {
        $rel = "assets/" + $_.FullName.Substring($AssetsRoot.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }

    Get-ChildItem -Path $JniDir -File | ForEach-Object {
        # Store native .so compressed cleanly (extractNativeLibs=true allows deflated .so and cuts APK size in half!)
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, "lib/arm64-v8a/$($_.Name)", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
}

# 6. ZipAlign & Sign APK (v2 + v3 scheme)
Write-Host "==> ZipAligning & Signing APK..." -ForegroundColor Cyan
$AlignedApk = Join-Path $BuildDir "aligned.apk"
& $ZipAlign -f -p 4 $UnsignedApk $AlignedApk
if ($LASTEXITCODE -ne 0) { throw "zipalign failed" }

$Keystore = if ($env:HUB_KEYSTORE_PATH) { $env:HUB_KEYSTORE_PATH } else { Join-Path $CacheDir "brazhnichenko-hub-android.jks" }
$KeyAlias = if ($env:HUB_KEYSTORE_ALIAS) { $env:HUB_KEYSTORE_ALIAS } else { "hubrelease" }
$PassFile = Join-Path $CacheDir "keystore.pass"
$KeyPass = if ($env:HUB_KEYSTORE_PASS) {
    $env:HUB_KEYSTORE_PASS
} elseif (Test-Path $PassFile) {
    (Get-Content $PassFile -Raw).Trim()
} else {
    "localdev-selfsigned-pass"
}

if (-not (Test-Path $Keystore)) {
    & keytool -genkeypair -v -keystore $Keystore -alias $KeyAlias `
        -keyalg RSA -keysize 2048 -validity 10000 `
        -storepass $KeyPass -keypass $KeyPass `
        -dname "CN=Brazhnichenko Hub, OU=LocalAI, O=OpenSource, L=RU, C=RU"
    if ($LASTEXITCODE -ne 0) { throw "keytool failed" }
}

$FinalApk = Join-Path $DistRoot "BrazhnichenkoHub-Android-arm64.apk"
& $ApkSigner sign --ks $Keystore --ks-key-alias $KeyAlias `
    --ks-pass "pass:$KeyPass" --key-pass "pass:$KeyPass" `
    --out $FinalApk $AlignedApk
if ($LASTEXITCODE -ne 0) { throw "apksigner sign failed" }

Write-Host "==> Verifying APK signature..." -ForegroundColor Cyan
& $ApkSigner verify --verbose $FinalApk
if ($LASTEXITCODE -ne 0) { throw "apksigner verify failed" }

$sizeMB = [Math]::Round((Get-Item $FinalApk).Length / 1MB, 1)
Write-Host "==> SUCCESS! Signed Android APK ready: $FinalApk ($sizeMB MB)" -ForegroundColor Green
