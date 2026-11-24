using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed record ServiceDefinition(
    string Id,
    string Name,
    string Category,
    string Description,
    int Port,
    string Url,
    string HealthUrl,
    string Folder,
    string? OutputFolder,
    string[] LogFiles,
    int[] StopPorts,
    bool UsesGpu
);

public sealed record GpuMetrics(string Name, int VramUsedMiB, int VramTotalMiB, int UtilPct, double TempC);
public sealed record SystemMetrics(GpuMetrics? Gpu, double RamUsedGB, double RamTotalGB, double DiskFreeDGB);
public sealed record ModelEntry(string Category, string Name, double SizeGB, string Purpose, string FullPath);
public sealed record LlmModelOption(string FileName, string DisplayName, string FullPath);

public sealed class GeneratedScenarioStage
{
    public int Index { get; set; }
    public string Prompt { get; set; } = "";
    public string BridgePrompt { get; set; } = "";
    public string RefMode { get; set; } = "svi_pro";
    public bool UseAnchorRef { get; set; } = true;
    public bool UsePrevRef { get; set; } = false;
    public string NoteRu { get; set; } = "";
}

public sealed class GeneratedStoryScenario
{
    public string Kind { get; set; } = "video_wan"; // video_wan | video_ltx | photo_story
    public string TitleRu { get; set; } = "";
    public string StyleLock { get; set; } = "";
    public string CharacterLock { get; set; } = "";
    public string EnvironmentLock { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Engine { get; set; } = "wan14b_fp8";
    public string Size { get; set; } = "832x480";
    public int DurationSec { get; set; } = 4;
    public bool UseStarterFrame { get; set; } = true;
    public string StarterEngine { get; set; } = "flux3";
    public string StarterPrompt { get; set; } = "";
    public List<GeneratedScenarioStage> Stages { get; set; } = [];
}

public sealed class AiServiceManager
{
    private static readonly HttpClient LongHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
    private string? _lastStartedQwenModel;

    public IReadOnlyList<ServiceDefinition> Services =>
    [
        new(
            Id: "qwen",
            Name: "Локальная LLM (llama.cpp)",
            Category: "LLM",
            Description: "Локальная языковая модель (OpenAI API, авто-подбор контекста под VRAM)",
            Port: HubConfig.Current.QwenPort,
            Url: $"http://127.0.0.1:{HubConfig.Current.QwenPort}",
            HealthUrl: $"http://127.0.0.1:{HubConfig.Current.QwenPort}/health",
            Folder: HubConfig.LlmRoot,
            OutputFolder: null,
            LogFiles: [Path.Combine(HubConfig.LlmLogsDir, "server.err.log"), Path.Combine(HubConfig.LlmLogsDir, "server.out.log")],
            StopPorts: [HubConfig.Current.QwenPort],
            UsesGpu: true
        ),
        new(
            Id: "claude",
            Name: "Claude Desktop + локальная LLM",
            Category: "LLM",
            Description: "Claude Desktop через локальный адаптер (профиль coding, 20 инструментов)",
            Port: HubConfig.Current.ClaudeBridgePort,
            Url: $"http://127.0.0.1:{HubConfig.Current.ClaudeBridgePort}/health",
            HealthUrl: $"http://127.0.0.1:{HubConfig.Current.ClaudeBridgePort}/health",
            Folder: HubConfig.LlmRoot,
            OutputFolder: null,
            LogFiles: [Path.Combine(HubConfig.LlmLogsDir, "claude-bridge-status.json"), Path.Combine(HubConfig.LlmLogsDir, "claude-bridge.err.log")],
            StopPorts: [HubConfig.Current.QwenPort],
            UsesGpu: true
        ),
        new(
            Id: "comfyui",
            Name: "ComfyUI (Движок фото и видео)",
            Category: "Media",
            Description: "Бэкенд для Студии, Фото- и Видео-историй (Z-Image, FLUX 3, Qwen 2.1, Wan 2.2 FP8, LTX-2.5)",
            Port: HubConfig.Current.ComfyPort,
            Url: $"http://127.0.0.1:{HubConfig.Current.ComfyPort}",
            HealthUrl: $"http://127.0.0.1:{HubConfig.Current.ComfyPort}/system_stats",
            Folder: HubConfig.MediaRoot,
            OutputFolder: HubConfig.ComfyOutputDir,
            LogFiles: [Path.Combine(HubConfig.MediaLogsDir, "comfy.err.log"), Path.Combine(HubConfig.MediaLogsDir, "comfy.out.log")],
            StopPorts: [HubConfig.Current.ComfyPort],
            UsesGpu: true
        ),
    ];

    public ServiceDefinition GetService(string id) => Services.First(s => s.Id == id);

    public async Task<bool> IsRunningAsync(ServiceDefinition svc)
    {
        try
        {
            using var resp = await Http.GetAsync(svc.HealthUrl);
            return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Dictionary<string, bool>> GetAllStatusesAsync()
    {
        var tasks = Services.Select(async s => (s.Id, Running: await IsRunningAsync(s))).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.Id, r => r.Running);
    }

    public async Task<SystemMetrics> GetSystemMetricsAsync()
    {
        var gpuTask = QueryGpuAsync();
        var (ramUsed, ramTotal) = QueryRam();
        double diskFreeD = 0;
        try
        {
            diskFreeD = Math.Round(new DriveInfo(HubConfig.DataDriveLetter).AvailableFreeSpace / (1024.0 * 1024 * 1024), 1);
        }
        catch { }

        return new SystemMetrics(await gpuTask, ramUsed, ramTotal, diskFreeD);
    }

    public async Task SwitchAndStartAsync(string serviceId, Action<string>? onProgress = null)
    {
        var conflicts = new[] { "qwen", "comfyui" }
            .Where(id => id != serviceId && !(serviceId == "claude" && id == "qwen"));

        foreach (var conflictId in conflicts)
        {
            var svc = GetService(conflictId);
            if (await IsRunningAsync(svc))
            {
                onProgress?.Invoke($"Остановка {svc.Name} для освобождения GPU...");
                await StopServiceAsync(conflictId);
            }
        }

        await StartServiceAsync(serviceId, onProgress);
    }

    public async Task StartServiceAsync(string serviceId, Action<string>? onProgress = null)
    {
        switch (serviceId)
        {
            case "qwen":
                await StartQwenAsync(onProgress);
                break;
            case "claude":
                await StartClaudeAsync(onProgress);
                break;
            case "comfyui":
                await StartComfyAsync(onProgress);
                break;
            default:
                throw new ArgumentException($"Неизвестный сервис: {serviceId}");
        }
    }

    public async Task StopServiceAsync(string serviceId)
    {
        await Task.Run(() =>
        {
            var svc = GetService(serviceId);
            KillListeningPids(svc.StopPorts);

            if (serviceId is "qwen" or "claude")
            {
                foreach (var p in Process.GetProcessesByName("llama-server"))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
        });

        // Give OS a moment to release GPU VRAM and sockets
        await Task.Delay(700);
    }

    public async Task StopAllGpuAsync()
    {
        await HubAssistantService.StopAssistantServerAsync();
        foreach (var id in new[] { "qwen", "comfyui" })
        {
            await StopServiceAsync(id);
        }
    }

    private static string ActiveLlmFile => HubConfig.ActiveLlmFile;

    public string? FindMatchingMmprojForModel(string? modelFileName)
    {
        try
        {
            string dir = HubConfig.LlmModelsDir;
            if (string.IsNullOrWhiteSpace(modelFileName) || !Directory.Exists(dir)) return null;

            var mmprojs = Directory.GetFiles(dir, "*.gguf", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mmprojs.Count == 0) return null;

            string stem = Path.GetFileNameWithoutExtension(modelFileName).ToLowerInvariant();
            string[] tokens = stem.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 3 && !t.StartsWith("q4") && !t.StartsWith("q5") && !t.StartsWith("q6") && !t.StartsWith("iq"))
                .ToArray();

            string? best = null;
            int bestScore = 0;
            foreach (string mp in mmprojs)
            {
                string mpName = Path.GetFileNameWithoutExtension(mp).ToLowerInvariant();
                int score = tokens.Count(t => mpName.Contains(t, StringComparison.Ordinal));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = mp;
                }
            }
            return bestScore > 0 ? best : (mmprojs.Count == 1 ? mmprojs[0] : null);
        }
        catch { return null; }
    }

    public List<LlmModelOption> GetAvailableLlmModels()
    {
        string modelsDir = HubConfig.LlmModelsDir;
        if (!Directory.Exists(modelsDir)) return [];
        return Directory.EnumerateFiles(modelsDir, "*.gguf")
            .Where(f =>
            {
                string n = Path.GetFileName(f);
                // Hide auxiliary vision mmproj files and non-first split shards (00002-of-*, etc.)
                if (n.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) return false;
                var m = System.Text.RegularExpressions.Regex.Match(n, @"-(\d{5})-of-(\d{5})\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value != "00001") return false;
                return true;
            })
            .Select(f =>
            {
                string name = Path.GetFileName(f);
                return new LlmModelOption(name, FriendlyLlmName(name), f);
            })
            .OrderByDescending(m => m.FileName.Contains("HauhauCS", StringComparison.OrdinalIgnoreCase)
                                 || m.FileName.Contains("heretic", StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.FileName)
            .ToList();
    }

    public static string FriendlyLlmName(string fileName)
    {
        string lower = fileName.ToLowerInvariant();
        if (lower.Contains("gemma-4-26b"))
            return "Gemma 4 26B-A4B MoE — Google (живой язык + русский)";
        if (lower.Contains("gemma-4-12b"))
            return "Gemma 4 12B — Google";
        if (lower.Contains("gpt-oss-20b"))
            return "GPT-OSS 20B MoE — OpenAI";
        if (lower.Contains("devstral"))
            return "Devstral Small 2 24B — Mistral (Кодинг)";
        if (lower.Contains("cydonia"))
            return "Cydonia 24B v4.3 — TheDrummer (Творчество, свободная)";
        if (lower.Contains("glm-4.7"))
            return "GLM-4.7-Flash 30B MoE — Z.AI";
        if (lower.Contains("nemotron-3"))
            return "Nemotron-3-Nano 30B MoE — NVIDIA";
        if (lower.Contains("deepseek-r1"))
            return "DeepSeek-R1-0528 8B — DeepSeek";
        if (lower.Contains("qwen3-next-80b") && lower.Contains("abliterated"))
            return "Qwen3-Next 80B-A3B MoE — Abliterated (свободная, 80B)";
        if (lower.Contains("qwen3-next-80b"))
            return "Qwen3-Next 80B-A3B MoE — Unsloth (80B)";
        if (lower.Contains("qwen3.8") && lower.Contains("hauhaucs"))
            return "Qwen 3.8 27B — HauhauCS Aggressive (свободная, Q3_K_P)";
        if (lower.Contains("qwen3.8") && lower.Contains("q6_k"))
            return "Qwen 3.8 27B — Unsloth (UD-Q6_K, 6-бит)";
        if (lower.Contains("qwen3.8"))
            return "Qwen 3.8 27B — Оригинал Unsloth";
        if (lower.Contains("hauhaucs") && lower.Contains("aggressive"))
            return "Qwen 3.6 35B MoE — HauhauCS Aggressive (свободная, Q4_K_M)";
        if (lower.Contains("heretic"))
            return "Qwen 3.6 — Heretic (свободная, Q4_K_M)";
        if (lower.Contains("qwen3.6-35b-a3b-ud-q4_k_m"))
            return "Qwen 3.6 — Оригинал Unsloth (Q4_K_M)";
        if (lower.Contains("qwen3-4b-instruct"))
            return "Qwen 3 4B Instruct — Помощник по Хабу (2.33 ГБ)";
        return Path.GetFileNameWithoutExtension(fileName);
    }

    public string GetActiveLlmFileName()
    {
        var available = GetAvailableLlmModels();
        if (File.Exists(ActiveLlmFile))
        {
            string saved = File.ReadAllText(ActiveLlmFile).Trim();
            if (available.Any(m => m.FileName.Equals(saved, StringComparison.OrdinalIgnoreCase)))
                return saved;
        }
        return available.FirstOrDefault()?.FileName ?? "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf";
    }

    public void SetActiveLlmFileName(string fileName)
    {
        Directory.CreateDirectory(HubConfig.LlmRoot);
        File.WriteAllText(ActiveLlmFile, fileName.Trim(), Encoding.UTF8);
    }

    public string GetEffectiveDirectorLlmFileName()
    {
        string configured = HubConfig.Current.DirectorLlmFile?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var available = GetAvailableLlmModels();
            var match = available.FirstOrDefault(m => m.FileName.Equals(configured, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.FileName;
        }
        return GetActiveLlmFileName();
    }

    public async Task<string> EnsureDirectorLlmRunningAsync(Action<string>? onProgress = null)
    {
        string desired = GetEffectiveDirectorLlmFileName();
        string desiredPath = Path.Combine(HubConfig.LlmModelsDir, desired);
        if (!RuntimeInstaller.IsLlamaInstalled() || !File.Exists(desiredPath))
            throw new InvalidOperationException("Для Режиссёра нужна локальная LLM и движок llama.cpp. Установи их во вкладке «Модели и GPU → 1. Текстовые (LLM)».");

        // Fast path: if compact 4B assistant is selected and already running on :18082, reuse it directly
        if (desired.Equals(Path.GetFileName(HubAssistantService.AssistantModelPath), StringComparison.OrdinalIgnoreCase))
        {
            var asst = new HubAssistantService();
            if (await asst.IsAssistantServerRunningAsync())
                return $"{HubAssistantService.AssistantBaseUrl}/v1/chat/completions";
        }

        var qwen = GetService("qwen");
        bool qwenRunning = await IsRunningAsync(qwen);
        if (qwenRunning)
        {
            string? loadedModel = _lastStartedQwenModel ?? await QueryLoadedQwenModelNameAsync();
            if (loadedModel is not null && loadedModel.Contains(Path.GetFileNameWithoutExtension(desired), StringComparison.OrdinalIgnoreCase))
                return $"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions";

            // Different LLM is currently loaded — switch cleanly to the selected Director model
            onProgress?.Invoke($"Переключение LLM на модель Режиссёра ({FriendlyLlmName(desired)})...");
            await StopServiceAsync("qwen");
        }
        else
        {
            // Free ComfyUI / other GPU services before loading Director LLM
            foreach (var other in Services.Where(s => s.UsesGpu && s.Id != "qwen"))
            {
                if (await IsRunningAsync(other))
                {
                    onProgress?.Invoke($"Освобождение GPU: остановка {other.Name}...");
                    await StopServiceAsync(other.Id);
                }
            }
        }

        await StartQwenAsync(onProgress, overrideModelFile: desired);
        return $"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions";
    }

    public async Task<(string EndpointUrl, string ModelFile, bool HasVision)> EnsureLlmModelRunningAsync(
        string? preferredModelFile,
        Action<string>? onProgress = null)
    {
        string desired = !string.IsNullOrWhiteSpace(preferredModelFile)
            ? preferredModelFile.Trim()
            : GetActiveLlmFileName();

        var available = GetAvailableLlmModels();
        if (available.Count == 0 || !RuntimeInstaller.IsLlamaInstalled())
            throw new InvalidOperationException("Нет установленной LLM или движка llama.cpp. Установи модель во вкладке «Модели и GPU».");

        var match = available.FirstOrDefault(m => m.FileName.Equals(desired, StringComparison.OrdinalIgnoreCase))
                    ?? available[0];
        desired = match.FileName;
        bool hasVision = FindMatchingMmprojForModel(desired) is not null;

        var qwen = GetService("qwen");
        bool qwenRunning = await IsRunningAsync(qwen);
        if (qwenRunning)
        {
            string? loadedModel = _lastStartedQwenModel ?? await QueryLoadedQwenModelNameAsync();
            if (loadedModel is not null && loadedModel.Contains(Path.GetFileNameWithoutExtension(desired), StringComparison.OrdinalIgnoreCase))
                return ($"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions", desired, hasVision);

            onProgress?.Invoke($"Переключение на модель ассистента ({FriendlyLlmName(desired)})...");
            await StopServiceAsync("qwen");
        }
        else
        {
            foreach (var other in Services.Where(s => s.UsesGpu && s.Id != "qwen"))
            {
                if (await IsRunningAsync(other))
                {
                    onProgress?.Invoke($"Освобождение GPU: остановка {other.Name}...");
                    await StopServiceAsync(other.Id);
                }
            }
        }

        await StartQwenAsync(onProgress, overrideModelFile: desired);
        return ($"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions", desired, hasVision);
    }

    private static async Task<string?> QueryLoadedQwenModelNameAsync()
    {
        try
        {
            using var resp = await Http.GetAsync($"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/models");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                return data[0].GetProperty("id").GetString();
        }
        catch { }
        return null;
    }

    private async Task StartQwenAsync(Action<string>? onProgress, string? overrideModelFile = null)
    {
        var svc = GetService("qwen");
        if (await IsRunningAsync(svc)) return;

        // Free compact assistant server if it was occupying VRAM before launching full LLM
        await HubAssistantService.StopAssistantServerAsync();

        string root = HubConfig.LlmRoot;
        string activeFile = !string.IsNullOrWhiteSpace(overrideModelFile) ? overrideModelFile.Trim() : GetActiveLlmFileName();
        _lastStartedQwenModel = activeFile;
        onProgress?.Invoke($"Запуск llama-server ({FriendlyLlmName(activeFile)})...");
        string runtimeDir = HubConfig.LlmRuntimeDir;
        string exe = RuntimeInstaller.FindLlamaExePath();
        if (string.IsNullOrEmpty(exe))
            throw new FileNotFoundException($"Движок llama.cpp не установлен в {runtimeDir}. Нажми «Установить llama.cpp» на карточке сервиса или во вкладке «Система».");

        string modelPath = Path.Combine(HubConfig.LlmModelsDir, activeFile);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Файл модели не найден: {modelPath}. Скачай модель во вкладке «Каталог и GPU Вики».");

        var cfg = HubConfig.Current;
        List<string> extraArgs;
        if (File.Exists(HubConfig.LlmServerArgsPath))
        {
            extraArgs = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(HubConfig.LlmServerArgsPath)) ?? []).ToList();
        }
        else
        {
            extraArgs =
            [
                "--alias", Path.GetFileNameWithoutExtension(activeFile),
                "--host", "127.0.0.1",
                "--port", cfg.QwenPort.ToString(),
                "--ctx-size", "32768",
                "--parallel", "1",
                "--fit", "on",
                "--fit-target", "1024",
                "--flash-attn", "on",
                "--cache-type-k", "q8_0",
                "--cache-type-v", "q8_0",
                "--jinja",
                "--temp", cfg.LlmTemperature.ToString("0.0#", CultureInfo.InvariantCulture),
                "--top-p", cfg.LlmTopP.ToString("0.0#", CultureInfo.InvariantCulture),
                "--top-k", cfg.LlmTopK.ToString(),
                "--min-p", cfg.LlmMinP.ToString("0.0#", CultureInfo.InvariantCulture),
                "--presence-penalty", cfg.LlmPresencePenalty.ToString("0.0#", CultureInfo.InvariantCulture),
                "--repeat-penalty", cfg.LlmRepeatPenalty.ToString("0.0#", CultureInfo.InvariantCulture),
                "--repeat-last-n", "256",
                "--dry-multiplier", cfg.LlmDryMultiplier.ToString("0.0#", CultureInfo.InvariantCulture),
                "--dry-base", "1.75",
                "--dry-allowed-length", "2",
                "--dry-penalty-last-n", "512",
                "--metrics"
            ];
        }

        // Apply HubConfig tuning overrides or smart Dense 27B auto-profile
        int targetCtx = cfg.LlmCtxSize > 0
            ? cfg.LlmCtxSize
            : (activeFile.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase) ? 32768 : 0);
        int targetFit = cfg.LlmFitTargetMiB > 0
            ? cfg.LlmFitTargetMiB
            : (activeFile.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase) ? 512 : 0);

        static void UpsertArg(List<string> list, string flag, string value)
        {
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i] == flag)
                {
                    list[i + 1] = value;
                    return;
                }
            }
            list.Add(flag);
            list.Add(value);
        }

        UpsertArg(extraArgs, "--alias", Path.GetFileNameWithoutExtension(activeFile));
        UpsertArg(extraArgs, "--port", cfg.QwenPort.ToString());
        string? mmprojPath = FindMatchingMmprojForModel(activeFile);
        if (!string.IsNullOrWhiteSpace(mmprojPath))
            UpsertArg(extraArgs, "--mmproj", mmprojPath);
        if (targetCtx > 0) UpsertArg(extraArgs, "--ctx-size", targetCtx.ToString());
        if (targetFit > 0) UpsertArg(extraArgs, "--fit-target", targetFit.ToString());
        UpsertArg(extraArgs, "--temp", cfg.LlmTemperature.ToString("0.0#", CultureInfo.InvariantCulture));
        UpsertArg(extraArgs, "--top-p", cfg.LlmTopP.ToString("0.0#", CultureInfo.InvariantCulture));
        UpsertArg(extraArgs, "--top-k", cfg.LlmTopK.ToString());
        UpsertArg(extraArgs, "--min-p", cfg.LlmMinP.ToString("0.0#", CultureInfo.InvariantCulture));
        UpsertArg(extraArgs, "--presence-penalty", cfg.LlmPresencePenalty.ToString("0.0#", CultureInfo.InvariantCulture));
        UpsertArg(extraArgs, "--repeat-penalty", cfg.LlmRepeatPenalty.ToString("0.0#", CultureInfo.InvariantCulture));
        UpsertArg(extraArgs, "--repeat-last-n", "256");
        if (cfg.LlmDryMultiplier > 0.001)
        {
            UpsertArg(extraArgs, "--dry-multiplier", cfg.LlmDryMultiplier.ToString("0.0#", CultureInfo.InvariantCulture));
            UpsertArg(extraArgs, "--dry-base", "1.75");
            UpsertArg(extraArgs, "--dry-allowed-length", "2");
            UpsertArg(extraArgs, "--dry-penalty-last-n", "512");
        }

        // Prevent infinite Qwen thinking loops via reasoning budget + don't re-feed old <think> blocks into history
        if (cfg.LlmReasoningBudget >= 0)
        {
            UpsertArg(extraArgs, "--reasoning-budget", cfg.LlmReasoningBudget.ToString());
            if (cfg.LlmReasoningBudget > 0)
            {
                UpsertArg(extraArgs, "--reasoning-budget-message", "\n\n[Thinking budget reached. Providing final answer now.]\n\n");
            }
        }
        if (!extraArgs.Contains("--no-reasoning-preserve") && !extraArgs.Contains("--reasoning-preserve"))
        {
            extraArgs.Add("--no-reasoning-preserve");
        }

        // Write llama-server WebUI default config so browser chat inherits anti-loop sampling defaults!
        try
        {
            string webUiCfgPath = Path.Combine(HubConfig.ConfigDir, "llama-webui-config.json");
            Directory.CreateDirectory(HubConfig.ConfigDir);
            var webUiCfg = new Dictionary<string, object>
            {
                ["temperature"] = cfg.LlmTemperature,
                ["top_p"] = cfg.LlmTopP,
                ["top_k"] = cfg.LlmTopK,
                ["min_p"] = cfg.LlmMinP,
                ["presence_penalty"] = cfg.LlmPresencePenalty,
                ["repeat_penalty"] = cfg.LlmRepeatPenalty,
                ["repeat_last_n"] = 256,
                ["dry_multiplier"] = cfg.LlmDryMultiplier,
                ["dry_base"] = 1.75,
                ["dry_allowed_length"] = 2,
                ["dry_penalty_last_n"] = 512
            };
            File.WriteAllText(webUiCfgPath, JsonSerializer.Serialize(webUiCfg, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            UpsertArg(extraArgs, "--webui-config-file", webUiCfgPath);
        }
        catch { }

        var allArgs = new List<string> { "-m", modelPath };
        if (OperatingSystem.IsMacOS() && !extraArgs.Contains("-ngl") && !extraArgs.Contains("--n-gpu-layers"))
        {
            allArgs.Add("-ngl");
            allArgs.Add("99");
        }
        allArgs.AddRange(extraArgs);

        string logsDir = HubConfig.LlmLogsDir;
        Directory.CreateDirectory(logsDir);
        Dictionary<string, string>? linuxEnv = BuildLinuxLlamaEnv(exe);
        SpawnDetachedWithLogs(
            exe,
            allArgs,
            Path.GetDirectoryName(exe)!,
            Path.Combine(logsDir, "server.out.log"),
            Path.Combine(logsDir, "server.err.log"),
            linuxEnv);

        await WaitForHealthAsync(svc, TimeSpan.FromMinutes(5), onProgress);
    }

    public static Dictionary<string, string>? BuildLinuxLlamaEnv(string exePath)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        string? exeDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrWhiteSpace(exeDir)) dirs.Add(exeDir);
        string rtDir = HubConfig.LlmRuntimeDir;
        if (Directory.Exists(rtDir))
        {
            dirs.Add(rtDir);
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(rtDir, "*", SearchOption.AllDirectories))
                    dirs.Add(sub);
            }
            catch { }
        }
        string? existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(existing))
            dirs.Add(existing);
        return new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = string.Join(':', dirs) };
    }

    private async Task StartClaudeAsync(Action<string>? onProgress)
    {
        await StartQwenAsync(onProgress);

        var claudeSvc = GetService("claude");
        if (!await IsRunningAsync(claudeSvc))
        {
            onProgress?.Invoke($"Запуск локального адаптера Claude (порт {HubConfig.Current.ClaudeBridgePort})...");
            string root = HubConfig.LlmRoot;
            string logsDir = HubConfig.LlmLogsDir;
            Directory.CreateDirectory(logsDir);
            SpawnDetachedWithLogs(
                OperatingSystem.IsWindows() ? "node.exe" : "node",
                [HubConfig.ClaudeBridgePath],
                root,
                Path.Combine(logsDir, "claude-bridge.out.log"),
                Path.Combine(logsDir, "claude-bridge.err.log"));

            await WaitForHealthAsync(claudeSvc, TimeSpan.FromSeconds(20), onProgress);
        }

        onProgress?.Invoke("Открытие Claude Desktop...");
        if (OperatingSystem.IsMacOS())
        {
            string macClaude = "/Applications/Claude.app/Contents/MacOS/Claude";
            if (!File.Exists(macClaude))
                throw new FileNotFoundException("Claude Desktop не найден в /Applications/Claude.app.");
            var macPsi = new ProcessStartInfo { FileName = macClaude, UseShellExecute = false };
            macPsi.Environment["CLAUDE_CONFIG_DIR"] = HubConfig.ClaudeConfigDir;
            Process.Start(macPsi);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Официального приложения Claude Desktop под Linux нет — используй встроенный Чат 2.0 или подключи MCP-сервер (Система → MCP JSON) к Claude Code CLI / Cursor / Antigravity.");
        }

        string? claudeInstall = await FindClaudeDesktopPathAsync();
        if (string.IsNullOrWhiteSpace(claudeInstall))
        {
            throw new FileNotFoundException("Пакет Claude Desktop (MSIX) не найден.");
        }

        string claudeExe = Path.Combine(claudeInstall, "app", "Claude.exe");
        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(claudeExe)!
        };
        psi.Environment["CLAUDE_CONFIG_DIR"] = HubConfig.ClaudeConfigDir;
        Process.Start(psi);
    }

    private async Task StartComfyAsync(Action<string>? onProgress)
    {
        var svc = GetService("comfyui");
        if (await IsRunningAsync(svc)) return;

        // Free compact assistant server if it was occupying VRAM before launching ComfyUI
        await HubAssistantService.StopAssistantServerAsync();

        onProgress?.Invoke($"Запуск ComfyUI (порт {HubConfig.Current.ComfyPort})...");
        string comfyDir = HubConfig.ComfyRootDir;
        string pythonExe = HubConfig.VenvPythonPath;
        string mainPy = Path.Combine(comfyDir, "main.py");
        if (!File.Exists(mainPy) || !File.Exists(pythonExe))
            throw new FileNotFoundException($"Движок ComfyUI Portable не установлен ({comfyDir}). Нажми «Установить ComfyUI» на карточке сервиса или во вкладке «Настройки и MCP».");

        string logsDir = HubConfig.MediaLogsDir;
        Directory.CreateDirectory(logsDir);

        SpawnDetachedWithLogs(
            pythonExe,
            [mainPy, "--listen", "127.0.0.1", "--port", HubConfig.Current.ComfyPort.ToString(), "--disable-auto-launch", "--offline"],
            comfyDir,
            Path.Combine(logsDir, "comfy.out.log"),
            Path.Combine(logsDir, "comfy.err.log"));

        await WaitForHealthAsync(svc, TimeSpan.FromMinutes(3), onProgress);
    }

    private async Task WaitForHealthAsync(ServiceDefinition svc, TimeSpan timeout, Action<string>? onProgress)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(svc))
            {
                onProgress?.Invoke($"{svc.Name} готов ({svc.Url})");
                return;
            }
            await Task.Delay(1500);
        }
        throw new TimeoutException($"{svc.Name} не ответил за {timeout.TotalSeconds:0} с. Проверьте вкладку «Логи».");
    }

    private static void SpawnDetachedWithLogs(
        string exe,
        IEnumerable<string> args,
        string workingDir,
        string outLog,
        string errLog,
        Dictionary<string, string>? env = null)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            string Quote(string s) => s.Contains(' ') || s.Contains('&') || s.Contains('(') ? $"\"{s}\"" : s;
            string argLine = string.Join(" ", args.Select(Quote));
            string cmdLine = $"/c \"\"{exe}\" {argLine} > \"{outLog}\" 2> \"{errLog}\"\"";

            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdLine,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }
        else
        {
            static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
            string argLine = string.Join(" ", args.Select(ShQuote));
            string shCmd = $"nohup {ShQuote(exe)} {argLine} > {ShQuote(outLog)} 2> {ShQuote(errLog)} < /dev/null &";

            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(shCmd);
        }

        if (env is not null)
        {
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
        Process.Start(psi);
    }

    private static async Task<string?> FindClaudeDesktopPathAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"(Get-AppxPackage 'Claude' | Select-Object -First 1).InstallLocation\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        string output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    public string ReadLogsTail(string serviceId, int maxLines = 120)
    {
        var svc = GetService(serviceId);
        var sb = new StringBuilder();
        foreach (var file in svc.LogFiles)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long start = Math.Max(0, fs.Length - 64 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string content = sr.ReadToEnd();
                var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
                var tail = string.Join(Environment.NewLine, lines.TakeLast(maxLines)).Trim();
                if (!string.IsNullOrEmpty(tail))
                {
                    sb.AppendLine($"=== {file} ===");
                    sb.AppendLine(tail);
                    sb.AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"=== {file} (ошибка чтения: {ex.Message}) ===");
            }
        }
        return sb.Length > 0 ? sb.ToString() : "(Логи пока пусты)";
    }

    public List<ModelEntry> ScanInstalledModels()
    {
        var list = new List<ModelEntry>();
        void ScanDir(string dir, string category)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
                    continue;
                var fi = new FileInfo(file);
                list.Add(new ModelEntry(category, fi.Name, Math.Round(fi.Length / (1024.0 * 1024 * 1024), 2), DescribeModel(fi.Name), fi.FullName));
            }
        }

        ScanDir(HubConfig.LlmModelsDir, "LLM (llama.cpp)");
        string cm = HubConfig.ComfyModelsDir;
        ScanDir(Path.Combine(cm, "diffusion_models"), "Diffusion / Video");
        ScanDir(Path.Combine(cm, "loras"), "LoRA");
        ScanDir(Path.Combine(cm, "text_encoders"), "Text Encoders");
        ScanDir(Path.Combine(cm, "vae"), "VAE");
        ScanDir(Path.Combine(cm, "latent_upscale_models"), "Upscalers");
        ScanDir(Path.Combine(cm, "upscale_models"), "Upscalers");
        return list.OrderBy(m => m.Category).ThenBy(m => m.Name).ToList();
    }

    private static int ComputeSafeMaxTokens(int desiredOutputTokens)
    {
        int rb = HubConfig.Current.LlmReasoningBudget;
        if (rb > 0)
            return Math.Max(desiredOutputTokens, rb + desiredOutputTokens);
        return Math.Max(desiredOutputTokens, 2600);
    }

    private static string ExtractLlmContent(JsonElement msgEl, string fallbackHint)
    {
        string raw = msgEl.TryGetProperty("content", out var cEl) ? (cEl.GetString() ?? "") : "";
        string clean = System.Text.RegularExpressions.Regex
            .Replace(raw, @"<think>.*?</think>\s*", "", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Trim();
        int openThink = clean.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (openThink >= 0)
            clean = clean[..openThink].Trim();

        if (!string.IsNullOrWhiteSpace(clean))
            return clean;

        bool hadReasoning = msgEl.TryGetProperty("reasoning_content", out var rEl) && !string.IsNullOrWhiteSpace(rEl.GetString());
        if (hadReasoning)
            return fallbackHint;

        return "";
    }

    private static async Task<string> PostChatWithThinkingRecoveryAsync(
        HttpClient http,
        string endpointUrl,
        List<object> messages,
        int desiredOutputTokens,
        double temperature,
        double presencePenalty,
        string recoveryInstruction)
    {
        var body = new
        {
            messages,
            max_tokens = ComputeSafeMaxTokens(desiredOutputTokens),
            temperature,
            presence_penalty = presencePenalty,
            chat_template_kwargs = new { enable_thinking = false }
        };
        using var reqContent = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(endpointUrl, reqContent);
        resp.EnsureSuccessStatusCode();
        string raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var msgEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        string answer = ExtractLlmContent(msgEl, "");
        if (!string.IsNullOrWhiteSpace(answer))
            return answer;

        // Auto-recovery: if a thinking model exhausted max_tokens inside reasoning_content,
        // feed its own draft thoughts back and ask for ONLY the final output.
        string reasoning = msgEl.TryGetProperty("reasoning_content", out var rEl) ? (rEl.GetString() ?? "").Trim() : "";
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            string clipped = reasoning.Length > 2400 ? reasoning[^2400..] : reasoning;
            var retryMsgs = new List<object>(messages)
            {
                new { role = "assistant", content = clipped },
                new { role = "user", content = recoveryInstruction }
            };
            var retryBody = new
            {
                messages = retryMsgs,
                max_tokens = Math.Max(1400, desiredOutputTokens),
                temperature = 0.4,
                presence_penalty = 1.0,
                chat_template_kwargs = new { enable_thinking = false }
            };
            using var retryContent = new StringContent(JsonSerializer.Serialize(retryBody), Encoding.UTF8, "application/json");
            using var retryResp = await http.PostAsync(endpointUrl, retryContent);
            if (retryResp.IsSuccessStatusCode)
            {
                string retryRaw = await retryResp.Content.ReadAsStringAsync();
                using var retryDoc = JsonDocument.Parse(retryRaw);
                var retryMsg = retryDoc.RootElement.GetProperty("choices")[0].GetProperty("message");
                string recovered = ExtractLlmContent(retryMsg, "");
                if (!string.IsNullOrWhiteSpace(recovered))
                    return recovered;
            }
        }

        return "";
    }

    public async Task<string> EnhancePromptWithQwenAsync(
        string idea,
        string targetMode,
        Action<string>? onProgress = null,
        bool hasInputImage = false,
        bool hasLastImage = false,
        IReadOnlyList<(string Role, string Content)>? discussionHistory = null)
    {
        string endpointUrl = await EnsureDirectorLlmRunningAsync(onProgress);

        string systemInstruction = targetMode switch
        {
            "video_wan" when hasInputImage && hasLastImage =>
                """
                You are an expert prompt engineer for Wan 2.2 14B First-Last Frame Image-to-Video generation (4-5 seconds, no audio).
                The user provides BOTH a start frame and an end frame. Do NOT re-describe static appearance already visible in the images.
                Focus on the smooth continuous physical transition from start frame to end frame: natural body mechanics, facial expression progression, secondary motion (hair, fabric, reflections), and one explicit smooth camera movement (e.g. "slow dolly in", "slow cinematic arc", "smooth crane up", "locked-off tripod shot").
                Keep lighting consistent (never write "sudden lighting shift"). Do NOT describe audio.
                Write one concise, vivid English paragraph (3-4 sentences). Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            "video_wan" when hasInputImage =>
                """
                You are an expert prompt engineer for Wan 2.2 14B Image-to-Video (I2V, 4-5 seconds, no audio).
                The start frame ALREADY defines the character, outfit, and room. Do NOT re-describe static appearance!
                Write one concise, vivid English I2V paragraph (3-4 sentences) covering:
                1. Concrete chronological physical action unfolding start -> middle -> end over 4-5 seconds (body/hand movement, expressive face/gaze, hair/fabric physics).
                2. Subtle ambient micro-motion with stable consistent lighting.
                3. One explicit Wan 2.2 camera movement (e.g. "slow dolly in, camera moves slowly forward toward the subject", "slow cinematic arc", "rack focus", "gentle handheld motion", "locked-off tripod shot").
                Never include audio descriptions. Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            "video_wan" =>
                """
                You are an expert prompt engineer for Wan 2.2 14B Text-to-Video (T2V, 4-5 seconds, no audio).
                Translate and expand the user's idea into one vivid, cinematic English paragraph (3-4 sentences) covering:
                1. Shot framing, lighting (e.g. golden hour, chiaroscuro, Rembrandt lighting, cold neon, rim light, volumetric haze), and authentic camera/lens look (e.g. ARRI Alexa with Cooke Anamorphic, Sony Venice 2, 35mm film — or clean 3D/anime style if stylized).
                2. Subject & clean uncluttered environment with concrete details.
                3. One clear chronological action over 4-5 seconds + subtle micro-motion + one explicit smooth camera movement (e.g. "slow dolly in", "side tracking shot", "slow cinematic arc", "crane up", "rack focus", "handheld motion").
                Never describe audio. Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            "video_ltx" when hasInputImage =>
                """
                You are an expert prompt engineer for LTX-2.5 Image-to-Video WITH synchronized audio (4-5 seconds).
                The start frame already establishes the scene. Focus on concrete chronological action (start -> middle -> end), facial expression, natural physics, ONE explicit smooth camera movement, AND vivid synchronized sound effects / ambient audio matching the action.
                Write one concise English paragraph (3-4 sentences). Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            "video_ltx" =>
                """
                You are an expert prompt engineer for LTX-2.5 Text-to-Video WITH synchronized audio (4-5 seconds).
                Translate and expand the user's idea into one vivid English paragraph (3-5 sentences) combining: framing, lighting & lens look; concrete subject & chronological action + smooth camera movement; and explicit synchronized sound effects / ambient audio.
                Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            _ when hasInputImage =>
                """
                You are an expert prompt engineer for modern reference-based AI image editing (FLUX 3 Klein, Qwen-Image 2.1).
                Translate and expand the user's edit request into one crisp, natural English instruction (2-4 sentences) preserving the subject from the reference image while specifying the exact requested change in pose, outfit, setting, lighting, and optical depth of field.
                Output ONLY the final English prompt without quotes, labels, or explanations.
                """,
            _ =>
                """
                You are an expert prompt engineer for modern photorealistic and stylized AI image generators (FLUX 3 Klein, Z-Image Turbo, Qwen-Image 2.1).
                Translate and expand the user's idea into one detailed, natural English paragraph (3-4 sentences) covering framing & subject details, clean atmospheric environment & specific lighting, and camera/lens look (or art style if stylized).
                Output ONLY the final English prompt without quotes, labels, or explanations.
                """
        };

        var userPayload = new StringBuilder();
        userPayload.AppendLine($"Идея / черновик промпта: {idea}");
        if (discussionHistory is { Count: > 0 })
        {
            userPayload.AppendLine("\n=== УТОЧНЕНИЯ ИЗ ОБСУЖДЕНИЯ С РЕЖИССЁРОМ (ОБЯЗАТЕЛЬНО УЧЕСТЬ) ===");
            foreach (var (role, msg) in discussionHistory.TakeLast(10))
                userPayload.AppendLine($"{(role == "user" ? "Пользователь" : "Режиссёр")}: {msg}");
            userPayload.AppendLine("\nСобери итоговый, максимально точный английский промпт, строго учтя все пожелания из обсуждения.");
        }

        onProgress?.Invoke("Режиссёр собирает кинематографический промпт...");
        var msgs = new List<object>
        {
            new { role = "system", content = systemInstruction + "\n\nCRITICAL: Do NOT count words and do NOT rewrite drafts repeatedly. If you are a thinking model, keep internal thoughts to 2 short bullet points max, then immediately output ONLY the final English prompt." },
            new { role = "user", content = userPayload.ToString() }
        };
        string result = await PostChatWithThinkingRecoveryAsync(
            LongHttp,
            endpointUrl,
            msgs,
            desiredOutputTokens: 700,
            temperature: 0.65,
            presencePenalty: 1.1,
            recoveryInstruction: "Stop thinking and output ONLY the final English prompt paragraph now.");
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("Thinking-модель потратила весь лимит токенов на внутренние размышления и не успела выдать промпт. Попробуй повторить запрос или выбери Instruct-модель для Режиссёра (например Gemma 4 26B / Cydonia 24B / Qwen3-Next Instruct).");
        return result;
    }

    public async Task<string> DiscussSinglePromptWithDirectorAsync(
        string targetMode,
        string currentPrompt,
        bool hasStartImage,
        bool hasEndImage,
        int durationSec,
        IReadOnlyList<(string Role, string Content)> chatHistory,
        string userMessage,
        Action<string>? onProgress = null)
    {
        string endpointUrl = await EnsureDirectorLlmRunningAsync(onProgress);
        onProgress?.Invoke("Режиссёр обдумывает сцену...");

        string modeDesc = targetMode switch
        {
            "video_ltx" => hasStartImage
                ? $"одиночного видео I2V со звуком (LTX-2.5, {durationSec} сек, по прикреплённому стартовому фото)"
                : $"одиночного видео T2V со звуком (LTX-2.5, {durationSec} сек, с нуля по тексту)",
            "video_wan" => hasStartImage && hasEndImage
                ? $"одиночного видео First→Last (Wan 2.2 14B, {durationSec} сек, плавный переход от стартового к конечному кадру, без звука)"
                : hasStartImage
                    ? $"одиночного видео I2V (Wan 2.2 14B, {durationSec} сек, оживление прикреплённого фото, без звука)"
                    : $"одиночного видео T2V (Wan 2.2 14B, {durationSec} сек, с нуля по тексту, без звука)",
            _ => hasStartImage
                ? "редактирования фото по референсу (FLUX 3 / Qwen-Image)"
                : "одиночного фото (FLUX 3 / Z-Image / Qwen-Image)"
        };

        string i2vHint = targetMode.StartsWith("video") && hasStartImage
            ? "ВАЖНО: у пользователя уже прикреплено стартовое фото (I2V), поэтому обсуждай в первую очередь физику движения, мимику, развитие за " + durationSec + " сек и конкретное движение камеры (slow dolly in, cinematic arc, rack focus, handheld, locked-off), а НЕ перепридумывай внешность с нуля."
            : "ВАЖНО: помоги заострить 3 слоя сцены: (1) конкретное действие и эмоцию, (2) движение камеры и ракурс, (3) свет и оптику/стиль" + (targetMode == "video_ltx" ? " + синхронный звук" : "") + ".";

        string systemPrompt = $"""
            Ты — опытный кинорежиссёр и промпт-инженер для {modeDesc} в Local AI Hub.
            Текущий черновик промпта пользователя:
            «{currentPrompt}»

            {i2vHint}

            Правила ответа (строго на русском, компактно 4–8 строк, без воды и общих фраз):
            - Если это начало обсуждения: коротко оцени черновик, предложи конкретное усиление (движение камеры, динамику действия за {durationSec} сек, свет/оптику) и задай РОВНО 1–2 коротких уточняющих вопроса, чтобы сцена стала острой и живой.
            - Если пользователь уже ответил или попросил правку: коротко зафиксируй обновлённую задумку (камера + действие + свет) и скажи: «Готово! Жми «✨ Собрать в промпт», и я вставлю готовый английский промпт в поле выше».
            - Если ты thinking-модель: думай МАКСИМАЛЬНО коротко (2–3 фразы), затем сразу пиши ответ пользователю на русском.
            """;

        var msgs = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var (role, content) in chatHistory.TakeLast(10))
            msgs.Add(new { role, content });
        msgs.Add(new { role = "user", content = userMessage });

        string reply = await PostChatWithThinkingRecoveryAsync(
            LongHttp,
            endpointUrl,
            msgs,
            desiredOutputTokens: 900,
            temperature: 0.65,
            presencePenalty: 1.2,
            recoveryInstruction: "Выдай короткий финальный ответ пользователю на русском языке (4–6 строк) без размышлений.");
        return string.IsNullOrWhiteSpace(reply)
            ? "⚠ Thinking-модель слишком долго размышляла и упёрлась в лимит токенов. Попробуй повторить сообщение или переключи модель Режиссёра на быструю Instruct-модель (например, Gemma 4 26B / Cydonia 24B / Qwen 3 4B)."
            : reply;
    }

    public static string MediaDirectorContextPath => HubConfig.DirectorContextPath;

    public string LoadMediaDirectorContext()
    {
        if (File.Exists(MediaDirectorContextPath))
            return File.ReadAllText(MediaDirectorContextPath, Encoding.UTF8);
        return "You are a director and prompt engineer for Local AI Hub (RTX 4080 16GB): FLUX 3 Klein, Z-Image Turbo, Qwen-Image 2.1, Wan 2.2 14B FP8 (SVI 2.0 Pro, FLUX 3 Bridge, First-Last Frame), LTX-2.5 Audio.";
    }

    public async Task<string> DiscussStoryWithDirectorAsync(
        string targetKind,
        string ideaRu,
        int stageCount,
        string? currentScenarioSummary,
        IReadOnlyList<(string Role, string Content)> chatHistory,
        string userMessage,
        string? endpointUrl = null)
    {
        string url = endpointUrl ?? await EnsureDirectorLlmRunningAsync();

        string kindRu = targetKind == "photo_story" ? "Фото-истории" : (targetKind == "video_ltx" ? "Видео-истории со звуком (LTX-2.5)" : "Видео-истории (Wan 2.2)");
        bool hasScenario = !string.IsNullOrWhiteSpace(currentScenarioSummary);

        string systemPrompt = hasScenario
            ? $"""
              Ты — опытный режиссёр-постановщик {kindRu} ({stageCount} этапов) в Local AI Hub.
              Общайся на русском языке, коротко и конкретно (4–8 строк), без воды и общих фраз.
              У пользователя УЖЕ собран сценарий (приведён ниже). Пользователь хочет обсудить его или изменить конкретный этап/стиль/действие.
              Предложи конкретные острые правки (что именно поменять в каком этапе, какой ракурс/действие добавить), либо задай 1 уточняющий вопрос, если правка неоднозначна.
              В конце коротко скажи: «Если план подходит — жми «✨ Обновить этапы», и я перепишу сценарий».

              === ТЕКУЩИЙ СЦЕНАРИЙ ===
              {currentScenarioSummary}
              """
            : $"""
              Ты — опытный режиссёр-постановщик {kindRu} ({stageCount} этапов) в Local AI Hub.
              Идея пользователя: «{ideaRu}».
              Главная проблема плохих сценариев — размытость («просто идёт», «красиво смотрит»). Твоя задача — сделать сюжет острым, визуально конкретным и живым!
              Правила ответа (строго на русском, компактно, 4–8 строк):
              - Если это начало обсуждения: кратко предложи конкретный каркас из {stageCount} битов (1 строка на этап) и задай РОВНО 2 коротких уточняющих вопроса (например: 1) точный визуальный стиль/эстетика, 2) конкретная деталь героя/кульминация/финал).
              - Если пользователь уже ответил на вопросы: коротко зафиксируй итоговую задумку по этапам и скажи «Готово! Жми «✨ Собрать сценарий», чтобы загрузить промпты в этапы».
              """;

        var msgs = new List<object> { new { role = "system", content = systemPrompt + "\n(Если ты thinking-модель: думай максимально кратко, 2–3 фразы, и сразу выдай ответ на русском.)" } };
        foreach (var (role, content) in chatHistory.TakeLast(8))
            msgs.Add(new { role, content });
        msgs.Add(new { role = "user", content = userMessage });

        string reply = await PostChatWithThinkingRecoveryAsync(
            LongHttp,
            url,
            msgs,
            desiredOutputTokens: 900,
            temperature: 0.65,
            presencePenalty: 1.2,
            recoveryInstruction: "Выдай короткий финальный ответ пользователю на русском языке (4–6 строк) без размышлений.");
        return string.IsNullOrWhiteSpace(reply)
            ? "⚠ Thinking-модель потратила лимит на внутренние размышления. Напиши короткий ответ или переключи LLM Режиссёра на Instruct-модель."
            : reply;
    }

    public async Task<GeneratedStoryScenario> GenerateStoryScenarioWithQwenAsync(
        string ideaRu,
        string targetKind,
        int stageCount,
        bool loopToStart,
        string orientationHint = "auto",
        IReadOnlyList<(string Role, string Content)>? discussionHistory = null,
        string? currentScenarioSummary = null,
        string? revisionInstruction = null,
        string? endpointUrl = null)
    {
        string url = endpointUrl ?? await EnsureDirectorLlmRunningAsync();

        stageCount = Math.Clamp(stageCount, 2, 8);
        string stackCtx = LoadMediaDirectorContext();

        string orientInstr = orientationHint switch
        {
            "9:16" => targetKind == "photo_story"
                ? "ВАЖНО: пользователь выбрал ВЕРТИКАЛЬНЫЙ формат — обязательно ставь \"size\": \"768x1024\"."
                : "ВАЖНО: пользователь выбрал ВЕРТИКАЛЬНЫЙ формат 9:16 — обязательно ставь \"size\": \"480x832\" и строй вертикальную композицию.",
            "16:9" => targetKind == "photo_story"
                ? "ВАЖНО: пользователь выбрал ГОРИЗОНТАЛЬНЫЙ формат 16:9 — обязательно ставь \"size\": \"1280x720\"."
                : "ВАЖНО: пользователь выбрал ГОРИЗОНТАЛЬНЫЙ формат 16:9 — обязательно ставь \"size\": \"832x480\".",
            "1:1" => targetKind == "photo_story"
                ? "ВАЖНО: пользователь выбрал КВАДРАТНЫЙ формат — обязательно ставь \"size\": \"1024x1024\"."
                : "ВАЖНО: пользователь выбрал КВАДРАТНЫЙ формат 1:1 — обязательно ставь \"size\": \"640x640\".",
            _ => ""
        };

        string schemaInstr = targetKind == "photo_story"
            ? $$"""
            Ты создаёшь сценарий для вкладки «Фото-история» из ровно {{stageCount}} этапов.
            {{orientInstr}}
            Сначала зафиксируй 3 замка (style_lock, character_lock, environment_lock) и повторяй style_lock в каждом промпте.
            Верни СТРОГО валидный JSON без пояснений до или после JSON:
            {
              "title_ru": "Короткое название истории на русском",
              "style_lock": "Exact English visual style phrase repeated in every prompt",
              "character_lock": "Exact English canonical character description",
              "environment_lock": "Exact English clean room/location & fixed lighting description",
              "engine": "flux3" или "qwen21",
              "size": "1024x1024" | "1024x768" | "768x1024" | "1280x720",
              "stages": [
                {
                  "prompt": "Detailed English prompt (50-80 words). Stage 1 establishes character, clean background, and style_lock. Stage 2+ starts with 'The same [character] from the reference image...' and keeps environment_lock + style_lock.",
                  "use_anchor_ref": true (для этапа 1 всегда false, для этапов 2+ обычно true),
                  "use_prev_ref": false (true только если нужно сохранить композицию/окружение прямо из предыдущего кадра),
                  "note_ru": "Короткое пояснение сцены и выбора референсов на русском (1 предложение)"
                }
              ]
            }
            """
            : $$"""
            Ты создаёшь сценарий для вкладки «Видео-история» ({{(targetKind == "video_ltx" ? "LTX-2.5 с синхронным звуком — обязательно описывай реалистичные звуки сцены в каждом промпте!" : "Wan 2.2 14B FP8 DynamicVRAM — без звука, фокус на выразительной актёрской игре, физике движения, камере и стабильном фоне")}}) из ровно {{stageCount}} видео-этапов + стартовый кадр Шага 0.
            {{orientInstr}}
            {{(loopToStart ? "ВАЖНО: пользователь хочет ЗАМКНУТЫЙ ЦИКЛ — у последнего этапа обязательно поставь ref_mode=\"prev_to_anchor\" и опиши плавное возвращение камеры и героя к стартовому кадру Шага 0." : "")}}
            Сначала зафиксируй 3 замка (style_lock, character_lock, environment_lock) и подбери negative_prompt под выбранный стиль (для мультяшного стиля обязательно добавь в negative_prompt 'photorealistic, live action, real photograph'!).
            Верни СТРОГО валидный JSON без пояснений до или после JSON:
            {
              "title_ru": "Короткое название видео-истории на русском",
              "style_lock": "Exact English style phrase repeated at the end of starter_prompt, every bridge_prompt, and every stage prompt",
              "character_lock": "Exact English canonical character description",
              "environment_lock": "Exact English clean uncluttered setting and fixed lighting (NO sudden lighting shifts, NO cluttered tiny monitor UI)",
              "negative_prompt": "Style-specific English negative prompt (for cartoon/3D animation MUST include 'photorealistic, live action, real photograph, morphing background, sudden lighting change, flickering, distorted anatomy, extra wings, blurry, watermark, text')",
              "engine": "{{(targetKind == "video_ltx" ? "ltx25" : "wan14b_fp8")}}",
              "size": "832x480" | "480x832" | "640x640" | "768x448",
              "duration_sec": 4 или 5,
              "starter_engine": "flux3" или "zimage" или "qwen21",
              "starter_prompt": "Detailed English STILL PHOTOGRAPH/RENDER prompt (55-85 words) for Step 0 anchor frame containing character_lock, clean environment_lock, expressive pose, and style_lock",
              "stages": [
                {
                  "prompt": "Hierarchical Wan 2.2 English video prompt (55-85 words): [Framing & fixed lighting] + [1 clear comedic/story beat unfolding chronologically start->middle->end over 4-5s] + [subtle background micro-motion] + [1 explicit smooth camera move] + [exact style_lock]{{(targetKind == "video_ltx" ? " + [synchronized sound effects]" : "")}}",
                  "ref_mode": "svi_pro" | "flux3_bridge" | "prev_only" | "prev_to_anchor" | "anchor_only",
                  "bridge_prompt": "ONLY when ref_mode is 'flux3_bridge': detailed English STILL FRAME prompt (45-75 words) for the exact opening moment (t=0) of this stage, explicitly referencing the character & style from the first reference image (Step 0) and the scene/pose/lighting from the second reference image (previous stage's last frame), ending with style_lock. Empty string \"\" for all other ref_modes.",
                  "note_ru": "Короткое пояснение комедийного/сюжетного бита и выбора ref_mode на русском (1 предложение)"
                }
              ]
            }
            Жёсткие правила для ref_mode на этапах 2+:
            - Для Wan 2.2 14B: по умолчанию ставь "svi_pro" (якорь Шага 0 + 5-кадровый хвост инерции движения — сохраняет и героя, и комнату!).
            - Ставь "flux3_bridge" (с обязательным bridge_prompt!), когда меняешь крупность плана (например, переход на крупный план выражения лица или обратно на средний план).
            - НИКОГДА не ставь "prev_only" после крупного плана (close-up), если герой отходит/улетает и открывается комната — иначе фон перерисуется в другую комнату!
            - НИКОГДА не пиши 'dramatic lighting shift' и не добавляй мелкие читаемые интерфейсы/тексты на фоне.
            """;

        string sharpnessRule = """
            АНТИ-ВОДА (ОБЯЗАТЕЛЬНО):
            - НЕ размывай сценарий общими фразами ("walks around", "looks nicely", "enjoys the moment").
            - Каждый этап обязан содержать: (1) чёткий ракурс камеры (close-up / medium shot / low-angle tracking shot), (2) конкретное физическое действие и взаимодействие с конкретным предметом, (3) чёткую эмоцию/мимику и визуальное развитие сюжета от этапа к этапу.
            - Строго соблюдай ВСЕ уточнения из диалога с пользователем (стиль, детали героя, правки конкретных этапов).
            """;

        string systemPrompt = stackCtx + "\n\n---\n\n" + sharpnessRule + "\n\n" + schemaInstr;

        var userPayload = new StringBuilder();
        userPayload.AppendLine($"Идея истории: {ideaRu}");
        userPayload.AppendLine($"Количество этапов: {stageCount}.");

        if (discussionHistory is { Count: > 0 })
        {
            userPayload.AppendLine("\n=== УТОЧНЕНИЯ ИЗ ДИАЛОГА С ПОЛЬЗОВАТЕЛЕМ (ОБЯЗАТЕЛЬНО УЧЕСТЬ) ===");
            foreach (var (role, msg) in discussionHistory.TakeLast(10))
                userPayload.AppendLine($"{(role == "user" ? "Пользователь" : "Режиссёр")}: {msg}");
        }

        if (!string.IsNullOrWhiteSpace(currentScenarioSummary))
        {
            userPayload.AppendLine("\n=== ТЕКУЩИЙ СЦЕНАРИЙ (ДО ПРАВКИ) ===");
            userPayload.AppendLine(currentScenarioSummary);
        }

        if (!string.IsNullOrWhiteSpace(revisionInstruction))
        {
            userPayload.AppendLine($"\n=== ЗАПРОС НА ПРАВКУ ===\n{revisionInstruction}");
            userPayload.AppendLine("Внеси запрошенные изменения в указанные этапы (или стиль), сохранив остальные удачные этапы и общую согласованность (style_lock, character_lock, environment_lock).");
        }

        var scenarioMsgs = new List<object>
        {
            new { role = "system", content = systemPrompt + "\n\n(If you are a thinking model: do NOT count words; keep <think> very concise, max 3-4 short lines, then immediately output the valid JSON.)" },
            new { role = "user", content = userPayload.ToString() }
        };
        string text = await PostChatWithThinkingRecoveryAsync(
            LongHttp,
            url,
            scenarioMsgs,
            desiredOutputTokens: 2600,
            temperature: 0.65,
            presencePenalty: 1.0,
            recoveryInstruction: "Output ONLY the valid JSON object now without any thinking or markdown.");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Thinking-модель потратила лимит токенов на размышления и не успела вывести JSON сценария. Попробуй повторить или выбери Instruct-модель для Режиссёра.");

        // Extract JSON block cleanly even if fenced in ```json ... ```
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Qwen вернула не JSON:\n{text}");
        string jsonOnly = text[start..(end + 1)];

        using var doc = JsonDocument.Parse(jsonOnly);
        var root = doc.RootElement;

        var scenario = new GeneratedStoryScenario
        {
            Kind = targetKind,
            TitleRu = root.TryGetProperty("title_ru", out var tEl) ? tEl.GetString() ?? "" : "",
            StyleLock = root.TryGetProperty("style_lock", out var slEl) ? slEl.GetString() ?? "" : "",
            CharacterLock = root.TryGetProperty("character_lock", out var clEl) ? clEl.GetString() ?? "" : "",
            EnvironmentLock = root.TryGetProperty("environment_lock", out var elEl) ? elEl.GetString() ?? "" : "",
            NegativePrompt = root.TryGetProperty("negative_prompt", out var npEl) ? npEl.GetString() ?? "" : "",
        };

        if (targetKind == "photo_story")
        {
            string eng = root.TryGetProperty("engine", out var eEl) ? eEl.GetString() ?? "flux3" : "flux3";
            scenario.Engine = eng is "flux3" or "qwen21" ? eng : "flux3";
            string sz = root.TryGetProperty("size", out var sEl) ? sEl.GetString() ?? "1024x1024" : "1024x1024";
            scenario.Size = orientationHint switch
            {
                "9:16" => "768x1024",
                "16:9" => "1280x720",
                "1:1" => "1024x1024",
                _ => sz is "1024x1024" or "1024x768" or "768x1024" or "1280x720" ? sz : "1024x1024"
            };
        }
        else
        {
            scenario.Engine = targetKind == "video_ltx" ? "ltx25" : "wan14b_fp8";
            string sz = root.TryGetProperty("size", out var sEl) ? sEl.GetString() ?? "832x480" : "832x480";
            scenario.Size = orientationHint switch
            {
                "9:16" => "480x832",
                "16:9" => "832x480",
                "1:1" => "640x640",
                _ => sz is "832x480" or "480x832" or "640x640" or "768x448" ? sz : "832x480"
            };
            int dur = root.TryGetProperty("duration_sec", out var dEl) && dEl.TryGetInt32(out int di) ? di : 4;
            scenario.DurationSec = dur is 3 or 4 or 5 ? dur : 4;
            string se = root.TryGetProperty("starter_engine", out var seEl) ? seEl.GetString() ?? "flux3" : "flux3";
            scenario.StarterEngine = se is "flux3" or "zimage" or "qwen21" ? se : "flux3";
            scenario.StarterPrompt = root.TryGetProperty("starter_prompt", out var spEl) ? spEl.GetString() ?? "" : "";
            scenario.UseStarterFrame = !string.IsNullOrWhiteSpace(scenario.StarterPrompt);
        }

        if (root.TryGetProperty("stages", out var stagesEl) && stagesEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 1;
            foreach (var item in stagesEl.EnumerateArray())
            {
                string prompt = item.TryGetProperty("prompt", out var pEl) ? pEl.GetString() ?? "" : "";
                string bridgePrompt = item.TryGetProperty("bridge_prompt", out var bpEl) ? bpEl.GetString() ?? "" : "";
                string rm = item.TryGetProperty("ref_mode", out var rEl) ? rEl.GetString() ?? "svi_pro" : "svi_pro";
                if (rm is not ("svi_pro" or "flux3_bridge" or "prev_only" or "prev_to_anchor" or "anchor_only"))
                    rm = targetKind == "video_ltx" ? "flux3_bridge" : "svi_pro";
                bool useAnchor = item.TryGetProperty("use_anchor_ref", out var uaEl) && uaEl.ValueKind == JsonValueKind.True;
                bool usePrev = item.TryGetProperty("use_prev_ref", out var upEl) && upEl.ValueKind == JsonValueKind.True;
                if (idx == 1) { useAnchor = false; usePrev = false; bridgePrompt = ""; }
                string note = item.TryGetProperty("note_ru", out var nEl) ? nEl.GetString() ?? "" : "";

                scenario.Stages.Add(new GeneratedScenarioStage
                {
                    Index = idx++,
                    Prompt = prompt.Trim(),
                    BridgePrompt = rm == "flux3_bridge" ? bridgePrompt.Trim() : "",
                    RefMode = rm,
                    UseAnchorRef = useAnchor,
                    UsePrevRef = usePrev,
                    NoteRu = note.Trim()
                });
            }
        }

        if (scenario.Stages.Count == 0)
            throw new InvalidOperationException("Qwen вернула пустой список этапов.");

        if (loopToStart && targetKind != "photo_story" && scenario.Stages.Count >= 2)
            scenario.Stages[^1].RefMode = "prev_to_anchor";

        return scenario;
    }

    private static string DescribeModel(string fileName) => fileName.ToLowerInvariant() switch
    {
        var n when n.Contains("qwen3.8") && n.Contains("hauhaucs") => "Qwen 3.8 27B Dense — HauhauCS Aggressive (свободная, 100% в VRAM, Q3_K_P)",
        var n when n.Contains("qwen3.8") => "Qwen 3.8 27B Dense — новое поколение (глубокая логика и код)",
        var n when n.Contains("hauhaucs") => "Qwen 3.6 35B MoE — HauhauCS Aggressive (свободная, без цензуры)",
        var n when n.Contains("heretic") => "Qwen 3.6 35B MoE — Heretic (свободная, сохраняет точность в коде)",
        var n when n.Contains("qwen3.6") => "Текстовая LLM 35B MoE — оригинал Unsloth (чат, код, Claude Desktop)",
        var n when n.Contains("z_image_turbo") => "Быстрая генерация фото с нуля (T2I, 8 шагов, Apache 2.0)",
        var n when n.Contains("flux3-image-klein") => "Топ-универсал: фото до 4K, точечное редактирование, до 10 референсов",
        var n when n.Contains("qwen_image_2.1") && !n.Contains("vae") => "Дизайн, чёткий текст, редактирование и прозрачный фон (RGBA)",
        var n when n.Contains("ltx-2.5-22b") => "Видео + синхронный звук за один проход (T2V / I2V / мульти-сцены)",
        var n when n.Contains("wan2.2_t2v_high_noise_14b_fp8") => "Кино-видео T2V 14B FP8 Scaled — DynamicVRAM + аппаратный FP8 (High Noise)",
        var n when n.Contains("wan2.2_t2v_low_noise_14b_fp8") => "Детализация видео T2V 14B FP8 Scaled — DynamicVRAM (Low Noise)",
        var n when n.Contains("wan2.2_i2v_high_noise_14b_fp8") => "Оживление фото I2V 14B FP8 Scaled — DynamicVRAM + аппаратный FP8 (High Noise)",
        var n when n.Contains("wan2.2_i2v_low_noise_14b_fp8") => "Детализация оживления I2V 14B FP8 Scaled — DynamicVRAM (Low Noise)",
        var n when n.Contains("wan2.2_ti2v_5b") => "Лёгкое быстрое видео T2V / I2V (для быстрых черновиков)",
        var n when n.Contains("wan2.2-t2v-a14b-highnoise-q6") => "Кино-видео T2V 14B Q6_K — максимум деталей (High Noise)",
        var n when n.Contains("wan2.2-t2v-a14b-lownoise-q6") => "Детализация видео T2V 14B Q6_K (пара Low Noise к High)",
        var n when n.Contains("wan2.2-i2v-a14b-highnoise-q6") => "Оживление фото I2V 14B Q6_K — максимум деталей (High Noise)",
        var n when n.Contains("wan2.2-i2v-a14b-lownoise-q6") => "Детализация оживления I2V 14B Q6_K (пара Low Noise к High)",
        var n when n.Contains("wan2.2-t2v-a14b-highnoise") => "Кино-видео по тексту 14B Q4_K_M (High Noise, в паре с Low + Lightning)",
        var n when n.Contains("wan2.2-t2v-a14b-lownoise") => "Детализация видео T2V 14B Q4_K_M (пара Low Noise к High Noise)",
        var n when n.Contains("wan2.2-i2v-a14b-highnoise") => "Оживление фото в кино-видео 14B Q4_K_M (High Noise, в паре с Low)",
        var n when n.Contains("wan2.2-i2v-a14b-lownoise") => "Детализация оживления фото I2V 14B Q4_K_M (пара Low Noise к High Noise)",
        var n when n.Contains("svi_v2_pro") => "SVI 2.0 Pro LoRA — анти-дрейф и бесшовное продолжение цепочек Wan 2.2 I2V",
        var n when n.Contains("lightx2v_4steps") => "Ускоритель Wan 2.2 14B с 20 шагов до 4 шагов (CFG 1.0)",
        var n when n.Contains("gemma4-12b") => "Текстовый энкодер для LTX-2.5",
        var n when n.Contains("gemma4_e2b") => "Улучшатель промптов для LTX-2.5",
        var n when n.Contains("umt5_xxl") => "Текстовый энкодер для Wan 2.2 (5B и 14B)",
        var n when n.Contains("qwen3vl_8b") => "Энкодер для Qwen-Image 2.1",
        var n when n.Contains("qwen3vl_4b") => "Энкодер для FLUX 3 Klein",
        var n when n.Contains("qwen_3_4b") => "Текстовый энкодер для Z-Image Turbo",
        var n when n.Contains("ltx-2.5-audio-vae") => "Аудио-декодер (звук) для LTX-2.5",
        var n when n.Contains("ltx-2.5-video-vae") => "Видео-VAE для LTX-2.5",
        var n when n.Contains("ltx-2.5-latent-spatial-upscaler") => "Латентный x2-апскейлер для LTX-2.5",
        var n when n.Contains("ultrasharp") => "Увеличение резкости фото ×4 (апскейлер для SwarmUI и ComfyUI)",
        var n when n.Contains("wan_2.1_vae") => "Видео-VAE для Wan 2.2 14B",
        var n when n.Contains("wan2.2_vae") => "Видео-VAE для Wan 2.2 5B",
        var n when n.Contains("flux2-vae") => "VAE для FLUX 3 Klein",
        var n when n.Contains("qwen_image_2.1_vae") => "RGBA-VAE для Qwen-Image 2.1",
        var n when n.Contains("ae.safetensors") => "VAE для Z-Image Turbo",
        _ => "Вспомогательный файл модели"
    };

    public static void OpenExternal(string target)
    {
        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            var psi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
    }

    private static async Task<GpuMetrics?> QueryGpuAsync()
    {
        if (OperatingSystem.IsMacOS())
            return await QueryMacAppleSiliconAsync();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string line = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            var parts = line.Split(',').Select(s => s.Trim()).ToArray();
            if (parts.Length < 5) return null;
            return new GpuMetrics(
                parts[0],
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture),
                int.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture)
            );
        }
        catch
        {
            return OperatingSystem.IsLinux() ? await QueryLinuxAmdGpuAsync() : null;
        }
    }

    private static async Task<GpuMetrics?> QueryLinuxAmdGpuAsync()
    {
        try
        {
            if (!Directory.Exists("/sys/class/drm")) return null;
            foreach (string cardDir in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                string devDir = Path.Combine(cardDir, "device");
                string vramTotalFile = Path.Combine(devDir, "mem_info_vram_total");
                if (!File.Exists(vramTotalFile)) continue;

                if (!long.TryParse((await File.ReadAllTextAsync(vramTotalFile)).Trim(), out long totalBytes) || totalBytes <= 0)
                    continue;
                long usedBytes = 0;
                string vramUsedFile = Path.Combine(devDir, "mem_info_vram_used");
                if (File.Exists(vramUsedFile))
                    long.TryParse((await File.ReadAllTextAsync(vramUsedFile)).Trim(), out usedBytes);

                int utilPct = 0;
                string busyFile = Path.Combine(devDir, "gpu_busy_percent");
                if (File.Exists(busyFile))
                    int.TryParse((await File.ReadAllTextAsync(busyFile)).Trim(), out utilPct);

                string gpuName = "AMD Radeon GPU (Linux DRM)";
                string rocmName = (await RunQuickCmdAsync("rocm-smi", "--showproductname --csv")).Trim();
                if (!string.IsNullOrWhiteSpace(rocmName))
                {
                    string? parsed = rocmName.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1).FirstOrDefault()?.Split(',').LastOrDefault()?.Trim();
                    if (!string.IsNullOrWhiteSpace(parsed))
                        gpuName = parsed.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? parsed : $"AMD {parsed}";
                }

                return new GpuMetrics(
                    gpuName,
                    (int)Math.Round(usedBytes / (1024.0 * 1024.0)),
                    (int)Math.Round(totalBytes / (1024.0 * 1024.0)),
                    utilPct,
                    0);
            }
        }
        catch { }
        return null;
    }

    private static async Task<GpuMetrics?> QueryMacAppleSiliconAsync()
    {
        try
        {
            string brand = (await RunQuickCmdAsync("sysctl", "-n machdep.cpu.brand_string")).Trim();
            if (string.IsNullOrWhiteSpace(brand))
                brand = "Apple Silicon";

            var (ramUsedGB, ramTotalGB) = QueryRamMac();
            double metalBudgetGB = HardwareAdvisor.EstimateAppleMetalVramGB(ramTotalGB);
            int budgetMiB = (int)Math.Round(metalBudgetGB * 1024.0);
            int usedMiB = (int)Math.Round(Math.Min(ramUsedGB, metalBudgetGB) * 1024.0);

            return new GpuMetrics(brand, usedMiB, budgetMiB, 0, 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> RunQuickCmdAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
        catch { return ""; }
    }

    private static string RunQuickCmdSync(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output;
        }
        catch { return ""; }
    }

    // ---- Cross-platform RAM & TCP port-to-PID helpers ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static (double usedGB, double totalGB) QueryRam()
    {
        if (OperatingSystem.IsMacOS())
            return QueryRamMac();
        if (OperatingSystem.IsLinux())
            return QueryRamLinux();

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return (0, 0);
        double total = mem.ullTotalPhys / (1024.0 * 1024 * 1024);
        double avail = mem.ullAvailPhys / (1024.0 * 1024 * 1024);
        return (Math.Round(total - avail, 1), Math.Round(total, 1));
    }

    private static (double usedGB, double totalGB) QueryRamMac()
    {
        try
        {
            string memStr = RunQuickCmdSync("sysctl", "-n hw.memsize").Trim();
            if (!ulong.TryParse(memStr, out ulong totalBytes) || totalBytes == 0)
                return (0, 0);
            double totalGB = totalBytes / (1024.0 * 1024 * 1024);

            string vmOut = RunQuickCmdSync("vm_stat", "");
            long pageSize = 16384; // Apple Silicon default 16 KB page
            var psMatch = System.Text.RegularExpressions.Regex.Match(vmOut, @"page size of (\d+) bytes");
            if (psMatch.Success && long.TryParse(psMatch.Groups[1].Value, out long ps))
                pageSize = ps;

            long ParsePages(string label)
            {
                var m = System.Text.RegularExpressions.Regex.Match(vmOut, $@"{label}:\s+(\d+)\.");
                return m.Success && long.TryParse(m.Groups[1].Value, out long v) ? v : 0;
            }

            long usedPages = ParsePages("Pages active") + ParsePages("Pages wired down") + ParsePages("Pages occupied by compressor");
            double usedGB = (usedPages * (double)pageSize) / (1024.0 * 1024 * 1024);
            return (Math.Round(Math.Clamp(usedGB, 0, totalGB), 1), Math.Round(totalGB, 1));
        }
        catch { return (0, 0); }
    }

    private static (double usedGB, double totalGB) QueryRamLinux()
    {
        try
        {
            if (!File.Exists("/proc/meminfo")) return (0, 0);
            return ParseLinuxMemInfo(File.ReadAllText("/proc/meminfo"));
        }
        catch { return (0, 0); }
    }

    public static (double usedGB, double totalGB) ParseLinuxMemInfo(string meminfoContent)
    {
        if (string.IsNullOrWhiteSpace(meminfoContent)) return (0, 0);
        long totalKb = 0, availKb = 0, freeKb = 0;
        foreach (string line in meminfoContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                long.TryParse(System.Text.RegularExpressions.Regex.Match(line, @"\d+").Value, out totalKb);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                long.TryParse(System.Text.RegularExpressions.Regex.Match(line, @"\d+").Value, out availKb);
            else if (line.StartsWith("MemFree:", StringComparison.Ordinal))
                long.TryParse(System.Text.RegularExpressions.Regex.Match(line, @"\d+").Value, out freeKb);
        }
        if (totalKb <= 0) return (0, 0);
        long effectiveAvail = availKb > 0 ? availKb : freeKb;
        double totalGB = totalKb / (1024.0 * 1024.0);
        double usedGB = Math.Max(0, (totalKb - effectiveAvail) / (1024.0 * 1024.0));
        return (Math.Round(usedGB, 1), Math.Round(totalGB, 1));
    }

    public static HashSet<int> ParseSsListeningPids(string ssOutput, IReadOnlyCollection<int> ports)
    {
        var pids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(ssOutput) || ports.Count == 0) return pids;
        var portSet = new HashSet<int>(ports);
        foreach (string line in ssOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            bool portMatched = false;
            foreach (int p in portSet)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, $@":{p}(?:\s|$)"))
                {
                    portMatched = true;
                    break;
                }
            }
            if (!portMatched) continue;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line, @"pid=(\d+)"))
            {
                if (int.TryParse(m.Groups[1].Value, out int pid) && pid > 1)
                    pids.Add(pid);
            }
        }
        return pids;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1, localPort2, localPort3, localPort4;
        public uint remoteAddr;
        public byte remotePort1, remotePort2, remotePort3, remotePort4;
        public int owningPid;
        public int LocalPort => (localPort1 << 8) + localPort2;
    }

    private static void KillListeningPids(int[] ports)
    {
        var pids = new HashSet<int>();
        if (!OperatingSystem.IsWindows())
        {
            foreach (int port in ports)
            {
                string outText = RunQuickCmdSync("lsof", $"-ti tcp:{port} -sTCP:LISTEN");
                foreach (string line in outText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out int pid) && pid > 1)
                        pids.Add(pid);
                }
            }
            if (pids.Count == 0 && OperatingSystem.IsLinux())
            {
                string ssOut = RunQuickCmdSync("ss", "-tlnp");
                foreach (int pid in ParseSsListeningPids(ssOut, ports))
                    pids.Add(pid);
            }
            foreach (int pid in pids)
            {
                try { using var proc = Process.GetProcessById(pid); proc.Kill(entireProcessTree: true); } catch { }
            }
            return;
        }

        var portSet = new HashSet<int>(ports);
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        int buffSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        IntPtr buff = Marshal.AllocHGlobal(buffSize);
        try
        {
            if (GetExtendedTcpTable(buff, ref buffSize, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) == 0)
            {
                int count = Marshal.ReadInt32(buff);
                IntPtr rowPtr = IntPtr.Add(buff, 4);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    if (portSet.Contains(row.LocalPort) && row.owningPid > 4)
                    {
                        pids.Add(row.owningPid);
                    }
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buff);
        }

        foreach (int pid in pids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }
}
