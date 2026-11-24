using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAIHub.Services;

public sealed class HubConfigData
{
    public string LlmRoot { get; set; } = "";
    public string MediaRoot { get; set; } = "";
    public string StoriesRoot { get; set; } = "";

    // LLM tuning (0 = auto per model architecture)
    public int LlmCtxSize { get; set; } = 0;
    public int LlmFitTargetMiB { get; set; } = 0;
    public double LlmTemperature { get; set; } = 1.0;
    public double LlmTopP { get; set; } = 0.95;
    public int LlmTopK { get; set; } = 20;
    public double LlmMinP { get; set; } = 0.0;
    public double LlmPresencePenalty { get; set; } = 1.5;
    public double LlmRepeatPenalty { get; set; } = 1.0;
    public double LlmDryMultiplier { get; set; } = 0.5;
    public int LlmReasoningBudget { get; set; } = 4096; // -1 = unlimited, 0 = disable thinking, >0 = max think tokens
    public int LlmIdleUnloadMinutes { get; set; } = 0; // 0 = disabled, >0 = auto-stop llama-server after idle minutes

    // ComfyUI / Video tuning
    public double WanShift { get; set; } = 5.0;
    public double WanSviHighLightningStrength { get; set; } = 0.85;
    public int WanSviTailFrames { get; set; } = 5;
    public string DefaultPhotoEngine { get; set; } = "flux3";
    public string DefaultVideoEngine { get; set; } = "wan14b_fp8";
    public string DirectorLlmFile { get; set; } = ""; // "" = auto (active LLM), or specific .gguf filename

    // Ports
    public int QwenPort { get; set; } = 18080;
    public int ClaudeBridgePort { get; set; } = 17842;
    public int ComfyPort { get; set; } = 8188;
    public int AssistantPort { get; set; } = 18082;

    // Auto-updater overrides (empty = built-in defaults)
    public string UpdateServerUrl { get; set; } = "";
    public string UpdateServerToken { get; set; } = "";
    public string UpdateCertSha256 { get; set; } = "";
    public bool AutoCheckUpdatesOnStartup { get; set; } = true;

    // UX state
    public bool FirstRunCompleted { get; set; } = false;
}

public static class HubConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static HubConfigData? _current;

    public static string ConfigDir
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "BrazhnichenkoHub");
            }
            if (OperatingSystem.IsLinux())
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
                string baseDir = string.IsNullOrWhiteSpace(xdg)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                    : xdg;
                return Path.Combine(baseDir, "BrazhnichenkoHub");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAIHub");
        }
    }

    public static string ConfigFilePath => Path.Combine(ConfigDir, "config.json");

    public static string HfTokenCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "huggingface", "token");

    public static HubConfigData Current
    {
        get
        {
            _current ??= LoadOrCreate();
            return _current;
        }
    }

    public static void Reload() => _current = LoadOrCreate();

    public static void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(Current, JsonOpts), Encoding.UTF8);
    }

    private static HubConfigData LoadOrCreate()
    {
        HubConfigData? cfg = null;
        try
        {
            if (File.Exists(ConfigFilePath))
                cfg = JsonSerializer.Deserialize<HubConfigData>(File.ReadAllText(ConfigFilePath, Encoding.UTF8));
        }
        catch { }

        bool isNew = cfg is null;
        cfg ??= new HubConfigData();

        // Smart auto-detection: preserve existing D:\LocalLLM & D:\LocalMediaAI on Windows, else pick clean default
        if (string.IsNullOrWhiteSpace(cfg.LlmRoot))
        {
            cfg.LlmRoot = OperatingSystem.IsWindows() && Directory.Exists(@"D:\LocalLLM")
                ? @"D:\LocalLLM"
                : Path.Combine(PickDefaultDataRoot(), "LLM");
        }
        if (string.IsNullOrWhiteSpace(cfg.MediaRoot))
        {
            cfg.MediaRoot = OperatingSystem.IsWindows() && Directory.Exists(@"D:\LocalMediaAI")
                ? @"D:\LocalMediaAI"
                : Path.Combine(PickDefaultDataRoot(), "MediaAI");
        }
        if (string.IsNullOrWhiteSpace(cfg.StoriesRoot))
        {
            cfg.StoriesRoot = Path.Combine(cfg.MediaRoot, "Stories");
        }

        if (isNew)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(cfg, JsonOpts), Encoding.UTF8);
            }
            catch { }
        }

        return cfg;
    }

    private static string PickDefaultDataRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BrazhnichenkoHub");
        }
        try
        {
            var d = new DriveInfo("D");
            if (d.IsReady && d.DriveType == DriveType.Fixed && d.AvailableFreeSpace > 30L * 1024 * 1024 * 1024)
                return @"D:\LocalAI";
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAIHub", "Data");
    }

    // ---- Resolved universal paths ----
    public static string LlmRoot => Current.LlmRoot;
    public static string LlmModelsDir => Path.Combine(LlmRoot, "models");
    public static string LlmRuntimeDir => Path.Combine(LlmRoot, "runtime");
    public static string LlmLogsDir => Path.Combine(LlmRoot, "logs");
    public static string LlmServerArgsPath => Path.Combine(LlmRoot, "server-args.json");
    public static string ActiveLlmFile => Path.Combine(LlmRoot, "active-model.txt");
    public static string DirectorContextPath => Path.Combine(LlmRoot, "media-director-context.md");
    public static string ClaudeBridgePath => Path.Combine(LlmRoot, "claude-bridge.cjs");
    public static string ClaudeConfigDir => Path.Combine(LlmRoot, "claude-config");

    public static string MediaRoot => Current.MediaRoot;
    public static string StoriesRoot => Current.StoriesRoot;
    public static string MediaLogsDir => Path.Combine(MediaRoot, "logs");

    public static string ComfyRootDir
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return Path.Combine(MediaRoot, "ComfyUI");
            string portable = Path.Combine(MediaRoot, "ComfyUI_windows_portable", "ComfyUI");
            if (Directory.Exists(portable)) return portable;
            string direct = Path.Combine(MediaRoot, "ComfyUI");
            if (Directory.Exists(direct)) return direct;
            return portable;
        }
    }

    public static string ComfyModelsDir => Path.Combine(ComfyRootDir, "models");
    public static string ComfyOutputDir => Path.Combine(ComfyRootDir, "output");
    public static string ComfyCustomNodesDir => Path.Combine(ComfyRootDir, "custom_nodes");

    public static string VenvPythonPath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                string[] unixCandidates =
                [
                    Path.Combine(ComfyRootDir, "venv", "bin", "python3"),
                    Path.Combine(MediaRoot, "venv", "bin", "python3"),
                    Path.Combine(ComfyRootDir, "venv", "bin", "python"),
                    Path.Combine(MediaRoot, "venv", "bin", "python")
                ];
                foreach (string c in unixCandidates)
                    if (File.Exists(c)) return c;
                return unixCandidates[0];
            }
            string venv1 = Path.Combine(MediaRoot, "venv", "Scripts", "python.exe");
            if (File.Exists(venv1)) return venv1;
            string venv2 = Path.Combine(ComfyRootDir, "venv", "Scripts", "python.exe");
            if (File.Exists(venv2)) return venv2;
            string emb = Path.Combine(MediaRoot, "ComfyUI_windows_portable", "python_embeded", "python.exe");
            if (File.Exists(emb)) return emb;
            return venv1;
        }
    }

    public static string MediaHelperPyPath
    {
        get
        {
            string inMedia = Path.Combine(MediaRoot, "story_media.py");
            if (File.Exists(inMedia)) return inMedia;
            string inApp = Path.Combine(AppContext.BaseDirectory, "story_media.py");
            if (File.Exists(inApp)) return inApp;
            return inMedia;
        }
    }

    public static string McpExePath
    {
        get
        {
            string binName = OperatingSystem.IsWindows() ? "LocalAIHub.Mcp.exe" : "LocalAIHub.Mcp";
            string beside = Path.Combine(AppContext.BaseDirectory, binName);
            if (File.Exists(beside)) return beside;
            string inHubApp = Path.Combine(MediaRoot, "LocalAIHub", "app", binName);
            if (File.Exists(inHubApp)) return inHubApp;
            return beside;
        }
    }

    public static string DataDriveLetter
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return "/";
            string root = Path.GetPathRoot(MediaRoot) ?? "C:\\";
            return root.Length > 0 ? root[..1].ToUpperInvariant() : "C";
        }
    }

    // ---- Hugging Face token helpers (shared with HF CLI cache) ----
    public static string? GetHuggingFaceToken()
    {
        string? env = Environment.GetEnvironmentVariable("HF_TOKEN") ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            if (File.Exists(HfTokenCachePath))
            {
                string t = File.ReadAllText(HfTokenCachePath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        catch { }
        return null;
    }

    public static void SaveHuggingFaceToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            try { if (File.Exists(HfTokenCachePath)) File.Delete(HfTokenCachePath); } catch { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(HfTokenCachePath)!);
        File.WriteAllText(HfTokenCachePath, token.Trim(), Encoding.UTF8);
    }

    public static string BuildClaudeDesktopMcpJson()
    {
        string mcpPath = McpExePath;
        var obj = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["local-ai-hub"] = new
                {
                    command = mcpPath,
                    args = Array.Empty<string>()
                }
            }
        };
        return JsonSerializer.Serialize(obj, JsonOpts);
    }
}
