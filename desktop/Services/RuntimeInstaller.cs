using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed record RuntimeStatusInfo(
    bool LlamaInstalled,
    string LlamaExePath,
    bool ComfyInstalled,
    string ComfyRootDir,
    string ComfyPythonPath
);

public sealed record RuntimeInstallProgress(
    string Stage,
    double Percent,
    double DownloadedMB,
    double TotalMB,
    double SpeedMBs
);

public sealed record ResolvedReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

public sealed record ResolvedLlamaPlan(
    string TagName,
    string BackendLabel,
    IReadOnlyList<ResolvedReleaseAsset> Assets
)
{
    public long TotalBytes => Assets.Sum(a => a.SizeBytes);
}

public sealed record ResolvedComfyPlan(
    string TagName,
    string VariantLabel,
    ResolvedReleaseAsset Asset
);

public sealed class RuntimeInstaller
{
    public const string LlamaReleasesUrl = "https://github.com/ggml-org/llama.cpp/releases";
    public const string ComfyReleasesUrl = "https://github.com/Comfy-Org/ComfyUI/releases/latest";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _apiHttp;
    private readonly HttpClient _dlHttp;

    public RuntimeInstaller()
    {
        var apiHandler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        _apiHttp = new HttpClient(apiHandler) { Timeout = ApiTimeout };
        _apiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAIHub/2.0 (Windows; +https://github.com/ggml-org/llama.cpp)");
        _apiHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var dlHandler = new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        _dlHttp = new HttpClient(dlHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _dlHttp.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAIHub/2.0 (Windows; +https://github.com/ggml-org/llama.cpp)");
    }

    private static string LlamaExeName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    public static bool IsLlamaInstalled() => !string.IsNullOrEmpty(FindLlamaExePath());

    public static string FindLlamaExePath()
    {
        string dir = HubConfig.LlmRuntimeDir;
        if (Directory.Exists(dir))
        {
            try
            {
                string? found = Directory.GetFiles(dir, LlamaExeName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }
            catch { }
        }

        if (!OperatingSystem.IsWindows())
        {
            string[] systemCandidates = ["/opt/homebrew/bin/llama-server", "/usr/local/bin/llama-server", "/usr/bin/llama-server"];
            foreach (string c in systemCandidates)
                if (File.Exists(c)) return c;
        }
        return "";
    }

    public static bool IsComfyInstalled()
    {
        string mainPy = Path.Combine(HubConfig.ComfyRootDir, "main.py");
        string py = HubConfig.VenvPythonPath;
        return File.Exists(mainPy) && File.Exists(py);
    }

    public static RuntimeStatusInfo GetStatus() => new(
        IsLlamaInstalled(),
        FindLlamaExePath(),
        IsComfyInstalled(),
        HubConfig.ComfyRootDir,
        HubConfig.VenvPythonPath
    );

    public static bool PreferCuda13(HardwareProfile? hw)
    {
        if (hw?.MatchedWiki is not null)
            return hw.MatchedWiki.Series == "50";
        string name = hw?.DetectedGpuName ?? "";
        return name.Contains("5090", StringComparison.OrdinalIgnoreCase)
            || name.Contains("5080", StringComparison.OrdinalIgnoreCase)
            || name.Contains("5070", StringComparison.OrdinalIgnoreCase)
            || name.Contains("5060", StringComparison.OrdinalIgnoreCase)
            || name.Contains("5050", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNvidiaGpu(HardwareProfile? hw)
    {
        if (hw is null) return true;
        if (hw.IsAppleSilicon || hw.IsVulkanGpu) return false;
        if (hw.MatchedWiki is not null) return true;
        string name = hw.DetectedGpuName ?? "";
        if (name.Contains("no nvidia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("не обнаруж", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
            || name.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("intel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("arc", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apple", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(name)
            || name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || name.Contains("geforce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("rtx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("gtx", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tesla", StringComparison.OrdinalIgnoreCase)
            || name.Contains("quadro", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAmdGpu(HardwareProfile? hw)
    {
        string name = hw?.DetectedGpuName ?? "";
        return name.Contains("amd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("rocm", StringComparison.OrdinalIgnoreCase);
    }

    public static ResolvedLlamaPlan? SelectLinuxLlamaPlan(
        string tag,
        IReadOnlyList<ResolvedReleaseAsset> assets,
        HardwareProfile? hw,
        Architecture arch)
    {
        string archTag = arch == Architecture.Arm64 ? "arm64" : "x64";
        string suffix = $"-{archTag}.tar.gz";

        if (IsNvidiaGpu(hw))
        {
            var cudaOrder = PreferCuda13(hw)
                ? new[] { ("cuda-13.", "Linux NVIDIA CUDA 13 (Blackwell / RTX 50)"), ("cuda-12.", "Linux NVIDIA CUDA 12") }
                : new[] { ("cuda-12.", "Linux NVIDIA CUDA 12"), ("cuda-13.", "Linux NVIDIA CUDA 13") };

            foreach (var (marker, label) in cudaOrder)
            {
                var mainTar = assets.FirstOrDefault(a =>
                    a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains($"bin-ubuntu-{marker}", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

                if (mainTar is not null)
                {
                    var cudartTar = assets.FirstOrDefault(a =>
                        a.Name.StartsWith("cudart-llama-", StringComparison.OrdinalIgnoreCase) &&
                        a.Name.Contains($"bin-ubuntu-{marker}", StringComparison.OrdinalIgnoreCase) &&
                        a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    var list = new List<ResolvedReleaseAsset> { mainTar };
                    if (cudartTar is not null) list.Add(cudartTar);
                    return new ResolvedLlamaPlan(tag, $"{label} ({archTag})", list);
                }
            }
        }

        if (IsAmdGpu(hw) && arch == Architecture.X64)
        {
            var rocmTar = assets.FirstOrDefault(a =>
                a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("bin-ubuntu-rocm-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (rocmTar is not null)
                return new ResolvedLlamaPlan(tag, $"Linux AMD ROCm ({archTag})", [rocmTar]);
        }

        var vulkanTar = assets.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains($"bin-ubuntu-vulkan-{archTag}.tar.gz", StringComparison.OrdinalIgnoreCase));
        if (vulkanTar is not null)
            return new ResolvedLlamaPlan(tag, $"Linux Vulkan ({archTag})", [vulkanTar]);

        var cpuTar = assets.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith($"bin-ubuntu-{archTag}.tar.gz", StringComparison.OrdinalIgnoreCase));
        if (cpuTar is not null)
            return new ResolvedLlamaPlan(tag, $"Linux CPU ({archTag})", [cpuTar]);

        return null;
    }

    public static ResolvedLlamaPlan? SelectWindowsLlamaPlan(
        string tag,
        IReadOnlyList<ResolvedReleaseAsset> assets,
        HardwareProfile? hw)
    {
        if (IsNvidiaGpu(hw))
        {
            bool preferCuda13 = PreferCuda13(hw);
            var cudaOrder = preferCuda13
                ? new[] { ("cuda-13.", "NVIDIA CUDA 13 (Blackwell / RTX 50)"), ("cuda-12.4", "NVIDIA CUDA 12.4") }
                : new[] { ("cuda-12.4", "NVIDIA CUDA 12.4"), ("cuda-13.", "NVIDIA CUDA 13") };

            foreach (var (marker, label) in cudaOrder)
            {
                var mainZip = assets.FirstOrDefault(a =>
                    a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains($"bin-win-{marker}", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase));

                var cudartZip = assets.FirstOrDefault(a =>
                    a.Name.StartsWith("cudart-llama-bin-win-", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase));

                if (mainZip is not null)
                {
                    var list = new List<ResolvedReleaseAsset> { mainZip };
                    if (cudartZip is not null) list.Add(cudartZip);
                    return new ResolvedLlamaPlan(tag, label, list);
                }
            }
        }

        // Native Vulkan x64 for Intel Arc / AMD Radeon / Universal GPUs
        var vulkanZip = assets.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("bin-win-vulkan-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (vulkanZip is not null)
            return new ResolvedLlamaPlan(tag, "Windows Vulkan x64 (Intel Arc / AMD / Универсальный)", [vulkanZip]);

        return null;
    }

    public async Task<ResolvedLlamaPlan> ResolveLatestLlamaPlanAsync(HardwareProfile? hw, CancellationToken ct = default)
    {
        using var resp = await _apiHttp.GetAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!rel.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
                continue;

            var assets = new List<ResolvedReleaseAsset>();
            foreach (var a in assetsEl.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add(new ResolvedReleaseAsset(name, url, size));
            }

            if (OperatingSystem.IsMacOS())
            {
                bool isArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64 || (hw?.IsAppleSilicon ?? true);
                string marker = isArm64 ? "bin-macos-arm64.tar.gz" : "bin-macos-x64.tar.gz";
                string label = isArm64 ? "Apple Silicon Metal (macOS arm64)" : "macOS Intel (x64)";
                var macAsset = assets.FirstOrDefault(a =>
                    a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(marker, StringComparison.OrdinalIgnoreCase));
                if (macAsset is not null)
                    return new ResolvedLlamaPlan(tag, label, [macAsset]);
                continue;
            }

            if (OperatingSystem.IsLinux())
            {
                var linuxPlan = SelectLinuxLlamaPlan(tag, assets, hw, RuntimeInformation.OSArchitecture);
                if (linuxPlan is not null)
                    return linuxPlan;
                continue;
            }

            var winPlan = SelectWindowsLlamaPlan(tag, assets, hw);
            if (winPlan is not null)
                return winPlan;
        }

        throw new InvalidOperationException("Не удалось найти подходящую Windows-сборку llama.cpp в релизах GitHub.");
    }

    public async Task<ResolvedComfyPlan> ResolveLatestComfyPlanAsync(HardwareProfile? hw, CancellationToken ct = default)
    {
        using var resp = await _apiHttp.GetAsync("https://api.github.com/repos/Comfy-Org/ComfyUI/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        string tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var assets = new List<ResolvedReleaseAsset>();
        if (doc.RootElement.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add(new ResolvedReleaseAsset(name, url, size));
            }
        }

        bool isLegacy10 = hw?.MatchedWiki?.Series == "10";
        ResolvedReleaseAsset? chosen = isLegacy10
            ? assets.FirstOrDefault(a => a.Name.Equals("ComfyUI_windows_portable_nvidia_cu126.7z", StringComparison.OrdinalIgnoreCase))
              ?? assets.FirstOrDefault(a => a.Name.Equals("ComfyUI_windows_portable_nvidia.7z", StringComparison.OrdinalIgnoreCase))
            : assets.FirstOrDefault(a => a.Name.Equals("ComfyUI_windows_portable_nvidia.7z", StringComparison.OrdinalIgnoreCase))
              ?? assets.FirstOrDefault(a => a.Name.Contains("windows_portable_nvidia", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase));

        if (chosen is null)
            throw new InvalidOperationException("Не удалось найти архив ComfyUI_windows_portable_nvidia в последнем релизе GitHub.");

        string variant = chosen.Name.Contains("cu126", StringComparison.OrdinalIgnoreCase)
            ? "NVIDIA Portable (CUDA 12.6)"
            : "NVIDIA Portable (CUDA 12.8+ / RTX 20–50)";
        return new ResolvedComfyPlan(tag, variant, chosen);
    }

    public async Task<string> InstallLlamaCppAsync(
        HardwareProfile? hw,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new RuntimeInstallProgress("Поиск актуального релиза llama.cpp на GitHub...", 0, 0, 0, 0));
        var plan = await ResolveLatestLlamaPlanAsync(hw, ct);

        string runtimeDir = HubConfig.LlmRuntimeDir;
        Directory.CreateDirectory(runtimeDir);
        string tmpDir = Path.Combine(HubConfig.ConfigDir, "downloads", "llama");
        Directory.CreateDirectory(tmpDir);

        long totalBytes = Math.Max(1, plan.TotalBytes);
        long completedBefore = 0;
        var downloadedZips = new List<string>();

        for (int i = 0; i < plan.Assets.Count; i++)
        {
            var asset = plan.Assets[i];
            string zipPath = Path.Combine(tmpDir, asset.Name);
            string label = $"Скачивание llama.cpp {plan.TagName} ({plan.BackendLabel}) [{i + 1}/{plan.Assets.Count}]: {asset.Name}";

            await DownloadFileWithResumeAsync(asset.DownloadUrl, zipPath, asset.SizeBytes, (done, total, speed) =>
            {
                long overall = completedBefore + done;
                double pct = Math.Clamp(overall * 88.0 / totalBytes, 0, 88);
                progress?.Report(new RuntimeInstallProgress(label, pct, overall / 1048576.0, totalBytes / 1048576.0, speed));
            }, ct);

            completedBefore += asset.SizeBytes;
            downloadedZips.Add(zipPath);
        }

        // Extract archives into runtimeDir (supports both .zip on Windows and .tar.gz on macOS/Linux)
        for (int i = 0; i < downloadedZips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string archivePath = downloadedZips[i];
            progress?.Report(new RuntimeInstallProgress(
                $"Распаковка архива [{i + 1}/{downloadedZips.Count}] {Path.GetFileName(archivePath)}...",
                89 + i * 5, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                await RunProcessAsync("tar", ["-xzf", archivePath, "-C", runtimeDir], runtimeDir, ct);
            }
            else
            {
                await Task.Run(() => ExtractZipFlattenSingleRoot(archivePath, runtimeDir), ct);
            }
            try { File.Delete(archivePath); } catch { }
        }

        string exe = FindLlamaExePath();
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException($"Распаковка завершилась, но {LlamaExeName} не найден в {runtimeDir}.");

        if (!OperatingSystem.IsWindows())
        {
            try { await RunProcessAsync("chmod", ["+x", exe], runtimeDir, ct); } catch { }
        }

        progress?.Report(new RuntimeInstallProgress($"Готово! Установлен llama.cpp {plan.TagName} ({plan.BackendLabel})", 100, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));
        return $"Установлен llama.cpp {plan.TagName} ({plan.BackendLabel}) → {exe}";
    }

    public async Task<string> InstallComfyUiAsync(
        HardwareProfile? hw,
        IProgress<RuntimeInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await InstallComfyUiUnixAsync(progress, ct);
        }

        progress?.Report(new RuntimeInstallProgress("Поиск актуального релиза ComfyUI Portable на GitHub...", 0, 0, 0, 0));
        var plan = await ResolveLatestComfyPlanAsync(hw, ct);

        string mediaRoot = HubConfig.MediaRoot;
        Directory.CreateDirectory(mediaRoot);
        string tmpDir = Path.Combine(HubConfig.ConfigDir, "downloads", "comfyui");
        Directory.CreateDirectory(tmpDir);

        string archivePath = Path.Combine(tmpDir, plan.Asset.Name);
        long totalBytes = Math.Max(1, plan.Asset.SizeBytes);

        await DownloadFileWithResumeAsync(plan.Asset.DownloadUrl, archivePath, plan.Asset.SizeBytes, (done, total, speed) =>
        {
            double pct = Math.Clamp(done * 80.0 / totalBytes, 0, 80);
            progress?.Report(new RuntimeInstallProgress(
                $"Скачивание ComfyUI {plan.TagName} ({plan.VariantLabel})...",
                pct, done / 1048576.0, totalBytes / 1048576.0, speed));
        }, ct);

        progress?.Report(new RuntimeInstallProgress("Подготовка распаковщика .7z...", 82, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));
        string sevenZip = await EnsureSevenZipExeAsync(ct);

        progress?.Report(new RuntimeInstallProgress("Распаковка ComfyUI Portable (1–3 мин, не закрывай окно)...", 85, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));
        await RunProcessAsync(sevenZip, ["x", archivePath, $"-o{mediaRoot}", "-y"], mediaRoot, ct);

        try { File.Delete(archivePath); } catch { }

        // Install bundled SVI custom node into ComfyUI\custom_nodes
        progress?.Report(new RuntimeInstallProgress("Установка встроенной ноды ComfyUI-LocalAIHub-SVI...", 96, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));
        InstallBundledSviNode();

        if (!IsComfyInstalled())
            throw new InvalidOperationException($"Распаковка завершилась, но ComfyUI не найден в {HubConfig.ComfyRootDir}.");

        progress?.Report(new RuntimeInstallProgress($"Готово! Установлен ComfyUI {plan.TagName} ({plan.VariantLabel})", 100, totalBytes / 1048576.0, totalBytes / 1048576.0, 0));
        return $"Установлен ComfyUI {plan.TagName} ({plan.VariantLabel}) → {HubConfig.ComfyRootDir}";
    }

    private static async Task<string> InstallComfyUiUnixAsync(IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        string comfyDir = HubConfig.ComfyRootDir;
        Directory.CreateDirectory(HubConfig.MediaRoot);
        string backendLabel = OperatingSystem.IsMacOS() ? "Apple Metal MPS" : "Linux PyTorch (CUDA / Vulkan / CPU)";

        if (!Directory.Exists(comfyDir) || !File.Exists(Path.Combine(comfyDir, "main.py")))
        {
            progress?.Report(new RuntimeInstallProgress("Клонирование ComfyUI с GitHub...", 10, 0, 0, 0));
            await RunProcessAsync("git", ["clone", "--depth", "1", "https://github.com/comfyanonymous/ComfyUI.git", comfyDir], HubConfig.MediaRoot, ct);
        }

        string pythonCmd = OperatingSystem.IsMacOS() && File.Exists("/opt/homebrew/bin/python3") ? "/opt/homebrew/bin/python3" : "python3";
        string venvDir = Path.Combine(comfyDir, "venv");
        string venvPy = Path.Combine(venvDir, "bin", "python3");
        string venvPip = Path.Combine(venvDir, "bin", "pip");

        if (!File.Exists(venvPy))
        {
            progress?.Report(new RuntimeInstallProgress("Создание Python venv для ComfyUI...", 30, 0, 0, 0));
            await RunProcessAsync(pythonCmd, ["-m", "venv", venvDir], comfyDir, ct);
        }

        progress?.Report(new RuntimeInstallProgress($"Установка PyTorch ({backendLabel}) и зависимостей ComfyUI (2–5 мин)...", 55, 0, 0, 0));
        await RunProcessAsync(venvPip, ["install", "--upgrade", "pip"], comfyDir, ct);
        await RunProcessAsync(venvPip, ["install", "torch", "torchvision", "torchaudio"], comfyDir, ct);
        await RunProcessAsync(venvPip, ["install", "-r", Path.Combine(comfyDir, "requirements.txt")], comfyDir, ct);

        progress?.Report(new RuntimeInstallProgress("Установка встроенной ноды ComfyUI-LocalAIHub-SVI...", 95, 0, 0, 0));
        InstallBundledSviNode();

        if (!IsComfyInstalled())
            throw new InvalidOperationException(OperatingSystem.IsMacOS()
                ? $"Установка завершилась, но ComfyUI не найден в {comfyDir}. Убедись, что установлены git и python3 (brew install python git)."
                : $"Установка завершилась, но ComfyUI не найден в {comfyDir}. Убедись, что установлены git и python3-venv (sudo apt install git python3-venv).");

        progress?.Report(new RuntimeInstallProgress($"Готово! Установлен ComfyUI ({backendLabel})", 100, 0, 0, 0));
        return $"Установлен ComfyUI ({backendLabel}) → {comfyDir}";
    }

    private static void InstallBundledSviNode()
    {
        try
        {
            string src = Path.Combine(AppContext.BaseDirectory, "ComfyUI-LocalAIHub-SVI", "__init__.py");
            if (!File.Exists(src)) return;
            string dstDir = Path.Combine(HubConfig.ComfyCustomNodesDir, "ComfyUI-LocalAIHub-SVI");
            Directory.CreateDirectory(dstDir);
            File.Copy(src, Path.Combine(dstDir, "__init__.py"), overwrite: true);
        }
        catch { }
    }

    private async Task<string> EnsureSevenZipExeAsync(CancellationToken ct)
    {
        string[] candidates =
        [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            Path.Combine(HubConfig.ConfigDir, "tools", "7zr.exe")
        ];
        foreach (string c in candidates)
            if (File.Exists(c)) return c;

        string toolsDir = Path.Combine(HubConfig.ConfigDir, "tools");
        Directory.CreateDirectory(toolsDir);
        string dest = Path.Combine(toolsDir, "7zr.exe");
        await DownloadFileWithResumeAsync("https://www.7-zip.org/a/7zr.exe", dest, 0, null, ct);
        return dest;
    }

    private static void ExtractZipFlattenSingleRoot(string zipPath, string destDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        // Check if all entries share a single top-level directory
        var topFolders = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.FullName))
            .Select(e => e.FullName.Split('/', '\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool stripTop = topFolders.Count == 1 && archive.Entries.All(e => e.FullName.Contains('/') || e.FullName.Contains('\\'));

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (stripTop)
            {
                int sep = rel.IndexOf(Path.DirectorySeparatorChar);
                if (sep >= 0 && sep + 1 < rel.Length) rel = rel[(sep + 1)..];
            }

            string target = Path.Combine(destDir, rel);
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private async Task DownloadFileWithResumeAsync(
        string url,
        string destPath,
        long knownSize,
        Action<long, long, double>? onProgress,
        CancellationToken ct)
    {
        string partPath = destPath + ".part";
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (knownSize > 0 && File.Exists(destPath) && new FileInfo(destPath).Length == knownSize)
        {
            onProgress?.Invoke(knownSize, knownSize, 0);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var hdrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hdrCts.CancelAfter(StallTimeout);
        HttpResponseMessage resp;
        try
        {
            resp = await _dlHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hdrCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Сервер не отвечает более {(int)StallTimeout.TotalSeconds} сек (возможно, провайдер/DPI блокирует загрузку).");
        }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (File.Exists(partPath)) File.Delete(partPath);
                using var retryReq = new HttpRequestMessage(HttpMethod.Get, url);
                using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                retryCts.CancelAfter(StallTimeout);
                using var retryResp = await _dlHttp.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, retryCts.Token);
                retryResp.EnsureSuccessStatusCode();
                await StreamToFileAsync(retryResp, partPath, destPath, 0, knownSize, onProgress, ct);
                return;
            }

            resp.EnsureSuccessStatusCode();
            bool isResume = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!isResume) existing = 0;

            await StreamToFileAsync(resp, partPath, destPath, existing, knownSize, onProgress, ct);
        }
    }

    private static async Task StreamToFileAsync(
        HttpResponseMessage resp,
        string partPath,
        string destPath,
        long startOffset,
        long knownSize,
        Action<long, long, double>? onProgress,
        CancellationToken ct)
    {
        long contentLen = resp.Content.Headers.ContentLength ?? 0;
        long total = contentLen > 0 ? startOffset + contentLen : knownSize;

        await using (var netStream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(partPath, startOffset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
        {
            byte[] buf = new byte[131072];
            long downloaded = startOffset;
            long sessionBytes = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = DateTime.UtcNow;

            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await netStream.ReadAsync(buf, readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Закачка зависла (нет данных более {(int)StallTimeout.TotalSeconds} сек). Частичный файл сохранён — можно докачать позже или скачать вручную через браузер.");
                }
                if (read <= 0) break;

                await fs.WriteAsync(buf.AsMemory(0, read), ct);
                downloaded += read;
                sessionBytes += read;

                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds >= 200)
                {
                    double speed = sw.Elapsed.TotalSeconds > 0.1 ? (sessionBytes / 1048576.0) / sw.Elapsed.TotalSeconds : 0;
                    onProgress?.Invoke(downloaded, total > 0 ? total : downloaded, speed);
                    lastReport = now;
                }
            }
            double finalSpeed = sw.Elapsed.TotalSeconds > 0.1 ? (sessionBytes / 1048576.0) / sw.Elapsed.TotalSeconds : 0;
            onProgress?.Invoke(downloaded, total > 0 ? total : downloaded, finalSpeed);
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(partPath, destPath);
    }

    private static async Task RunProcessAsync(string exe, IEnumerable<string> args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {exe}");
        string err = "";
        var errTask = Task.Run(async () => err = await proc.StandardError.ReadToEndAsync(ct), ct);
        var outTask = Task.Run(async () => await proc.StandardOutput.ReadToEndAsync(ct), ct);

        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(errTask, outTask);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Ошибка распаковки ({Path.GetFileName(exe)}, код {proc.ExitCode}): {err}");
    }
}
