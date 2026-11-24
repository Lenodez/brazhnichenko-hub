using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAIHub.Services;

namespace LocalAIHub.Mcp;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };
    private static readonly AiServiceManager ServiceManager = new();
    private static readonly HuggingFaceClient HfClient = new();
    private static readonly ComfyStoryClient StoryClient = new();

    public static async Task Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? response = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                response = await HandleRequestAsync(doc.RootElement);
            }
            catch (Exception ex)
            {
                response = MakeError(null, -32700, $"Parse/internal error: {ex.Message}");
            }

            if (response is not null)
            {
                await writer.WriteLineAsync(response.ToJsonString(JsonOut));
            }
        }
    }

    private static async Task<JsonNode?> HandleRequestAsync(JsonElement req)
    {
        string method = req.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
        bool hasId = req.TryGetProperty("id", out var idEl);
        JsonNode? idNode = hasId ? JsonNode.Parse(idEl.GetRawText()) : null;

        // Notifications (no id) require no response
        if (!hasId) return null;

        try
        {
            return method switch
            {
                "initialize" => MakeResult(idNode, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false }
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "local-ai-hub",
                        ["version"] = "2.0.0"
                    }
                }),
                "ping" => MakeResult(idNode, new JsonObject()),
                "tools/list" => MakeResult(idNode, new JsonObject { ["tools"] = BuildToolsList() }),
                "tools/call" => await HandleToolCallAsync(idNode, req.GetProperty("params")),
                _ => MakeError(idNode, -32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex)
        {
            return MakeError(idNode, -32603, ex.Message);
        }
    }

    private static JsonArray BuildToolsList()
    {
        JsonObject Tool(string name, string desc, JsonObject props, string[]? required = null)
        {
            var reqArr = new JsonArray();
            if (required is not null) foreach (var r in required) reqArr.Add(r);
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = reqArr
                }
            };
        }

        JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };
        JsonObject Num(string desc) => new() { ["type"] = "number", ["description"] = desc };
        JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };
        JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };

        return
        [
            Tool("get_hardware_profile",
                "Detect this PC's NVIDIA GPU, VRAM, RAM, free disk space, architecture capabilities (FP16/BF16/FP8/FP4), and matched GTX 10 - RTX 50 wiki profile.",
                new JsonObject()),

            Tool("lookup_gpu_wiki",
                "Query the built-in NVIDIA GPU Wiki (GTX 10-series through RTX 50-series) and RAM compatibility matrix.",
                new JsonObject
                {
                    ["series"] = Str("Optional series filter: '10', '16/20', '30', '40', '50'"),
                    ["query"] = Str("Optional model search text, e.g. '4080', '3060', '5090'"),
                    ["simulate_ram_gb"] = Num("Optional RAM in GB (e.g. 16, 32, 64) to get RAM-combination advice")
                }),

            Tool("list_recommended_models",
                "List curated AI models across text (LLM), photo, and video evaluated against this PC's exact GPU+RAM (or simulated specs), with install status.",
                new JsonObject
                {
                    ["category"] = Str("Filter: 'all', 'text', 'photo', or 'video' (default 'all')"),
                    ["simulate_gpu"] = Str("Optional GPU name from wiki to simulate another PC, e.g. 'RTX 3060 12GB'"),
                    ["simulate_ram_gb"] = Num("Optional RAM in GB for simulation")
                }),

            Tool("list_installed_models",
                "List all AI model files currently installed on disk (LLM, diffusion/video, LoRAs, text encoders, VAEs, upscalers).",
                new JsonObject()),

            Tool("search_huggingface",
                "Search Hugging Face Hub for GGUF/safetensors models or list files inside a specific Hugging Face repo.",
                new JsonObject
                {
                    ["query"] = Str("Search keyword on Hugging Face Hub (e.g. 'Qwen3.8-27B GGUF')"),
                    ["repo_id"] = Str("Optional exact repo ID (e.g. 'unsloth/Qwen3.8-27B-GGUF') to list its downloadable files"),
                    ["limit"] = Int("Max search results (default 10)")
                }),

            Tool("install_model",
                "Download and SHA-256 verify either a curated Local AI Hub catalog bundle (by catalog_id) OR a custom file from Hugging Face (repo_id + remote_path).",
                new JsonObject
                {
                    ["catalog_id"] = Str("Curated catalog model ID from list_recommended_models (e.g. 'llm-qwen38-27b-hauhau-q3kp', 'photo-flux3-klein', 'video-wan22-fp8-svi')"),
                    ["repo_id"] = Str("Custom Hugging Face repo ID (when catalog_id is omitted)"),
                    ["remote_path"] = Str("File path inside the Hugging Face repo (when catalog_id is omitted)"),
                    ["target_folder"] = Str("Destination category for custom file: 'llm_models', 'diffusion_models', 'loras', 'text_encoders', 'vae', 'upscale_models'")
                }),

            Tool("install_runtime",
                "Install or check execution engine runtimes needed to run models: 'llama' (llama.cpp NVIDIA CUDA 12.4/13.x or Vulkan for LLMs) or 'comfyui' (ComfyUI Portable NVIDIA + SVI node for photo/video).",
                new JsonObject
                {
                    ["runtime"] = Str("'llama' or 'comfyui'"),
                    ["dry_run"] = Bool("If true, only resolve and return the GitHub release plan & download size for this PC's GPU without downloading")
                },
                ["runtime"]),

            Tool("get_hub_settings",
                "Inspect Local AI Hub paths, ports, Hugging Face auth status, active LLM model, and internal LLM/ComfyUI tuning parameters.",
                new JsonObject()),

            Tool("update_hub_settings",
                "Tune Local AI Hub internal settings (LLM context size, fit target, temperature; Wan 2.2 shift, SVI lightning strength, SVI tail frames; active LLM model; default engines; data paths).",
                new JsonObject
                {
                    ["llm_ctx_size"] = Int("LLM context size override (0 = auto per model, or e.g. 16384, 32768, 65536, 131072)"),
                    ["llm_fit_target_mib"] = Int("LLM VRAM headroom in MiB (0 = auto, or e.g. 512, 1024, 2048)"),
                    ["llm_temperature"] = Num("LLM sampling temperature (e.g. 0.6 - 1.0, recommended 1.0 for Qwen3/3.6/3.8)"),
                    ["llm_presence_penalty"] = Num("LLM presence penalty anti-loop (0.0 - 2.0, recommended 1.5 for Qwen3/3.6/3.8)"),
                    ["llm_dry_multiplier"] = Num("LLM DRY anti-repetition multiplier (0.0 - 2.0, recommended 0.5)"),
                    ["llm_reasoning_budget"] = Int("Max thinking tokens per reply (-1 = unlimited, 0 = disable thinking, 4096 = recommended anti-loop cap)"),
                    ["active_llm_file"] = Str("Filename of installed GGUF in LLM models folder to set active"),
                    ["wan_shift"] = Num("Wan 2.2 ModelSamplingSD3 shift (2.0 - 10.0, default 5.0)"),
                    ["wan_svi_high_lightning_strength"] = Num("Wan 2.2 Lightning HIGH LoRA strength when stacked with SVI 2.0 Pro (0.4 - 1.2, default 0.85)"),
                    ["wan_svi_tail_frames"] = Int("Number of trailing frames for SVI 2.0 Pro motion velocity (1 or 5, default 5)"),
                    ["default_photo_engine"] = Str("'flux3', 'zimage', or 'qwen21'"),
                    ["default_video_engine"] = Str("'wan14b_fp8', 'wan14b', 'wan14b_q6', or 'ltx25'"),
                    ["llm_root"] = Str("Optional new LLM root folder"),
                    ["media_root"] = Str("Optional new MediaAI root folder")
                }),

            Tool("manage_service",
                "Check status, start (with automatic GPU switching), or stop local AI services ('qwen', 'comfyui', or 'all').",
                new JsonObject
                {
                    ["action"] = Str("'status', 'start', 'stop', or 'stop_all'"),
                    ["service"] = Str("'qwen' or 'comfyui' (required for start/stop)")
                },
                ["action"]),

            Tool("get_director_context",
                "Read the Studio Director context markdown (installed models, prompt grammar, ref_mode rules) so you can write optimal multi-stage photo/video scripts.",
                new JsonObject()),

            Tool("create_video_story_pipeline",
                "Create and save a complete multi-stage Video Story (Step 0 starter photo + Stages 1..N with ref_mode, bridge_prompt, and negative_prompt) into Local AI Hub's video_story.json, and optionally execute the render pipeline.",
                new JsonObject
                {
                    ["starter_prompt"] = Str("English Step 0 anchor still-frame prompt (optional if use_starter_frame=false)"),
                    ["starter_engine"] = Str("'flux3' (default), 'zimage', or 'qwen21'"),
                    ["negative_prompt"] = Str("Style-specific English negative prompt"),
                    ["engine"] = Str("Video engine if run_now=true: 'wan14b_fp8' (default), 'wan14b', 'wan14b_q6', 'ltx25'"),
                    ["width"] = Int("Video width if run_now=true (default 832; use 480 for 9:16 vertical)"),
                    ["height"] = Int("Video height if run_now=true (default 480; use 832 for 9:16 vertical)"),
                    ["duration_sec"] = Int("Clip length in seconds per stage: 3, 4, or 5 (default 5)"),
                    ["run_now"] = Bool("If true, immediately starts ComfyUI, renders Step 0 + all stages, and concatenates the final MP4 (takes several minutes)"),
                    ["stages"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered list of video stages [{prompt, ref_mode, bridge_prompt}]",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["prompt"] = Str("English video prompt for this stage"),
                                ["ref_mode"] = Str("'svi_pro' (default), 'flux3_bridge', 'prev_only', 'prev_to_anchor', 'anchor_only'"),
                                ["bridge_prompt"] = Str("Static English frame prompt when ref_mode='flux3_bridge'")
                            },
                            ["required"] = new JsonArray("prompt")
                        }
                    }
                },
                ["stages"]),

            Tool("create_photo_story_pipeline",
                "Create and save a multi-stage Photo Story (character anchor + sequential scenes) into Local AI Hub's photo_story.json.",
                new JsonObject
                {
                    ["stages"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ordered list of photo stages [{prompt, use_anchor_ref, use_prev_ref}]",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["prompt"] = Str("English photo prompt"),
                                ["use_anchor_ref"] = Bool("Use Stage 1 as character anchor (default true for stage 2+)"),
                                ["use_prev_ref"] = Bool("Also use previous stage as composition reference")
                            },
                            ["required"] = new JsonArray("prompt")
                        }
                    }
                },
                ["stages"]),
        ];
    }

    private static async Task<JsonNode> HandleToolCallAsync(JsonNode? idNode, JsonElement callParams)
    {
        string name = callParams.GetProperty("name").GetString() ?? "";
        JsonElement args = callParams.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.Object
            ? aEl
            : default;

        try
        {
            string resultText = name switch
            {
                "get_hardware_profile" => await ToolGetHardwareProfileAsync(),
                "lookup_gpu_wiki" => ToolLookupGpuWiki(args),
                "list_recommended_models" => await ToolListRecommendedModelsAsync(args),
                "list_installed_models" => ToolListInstalledModels(),
                "search_huggingface" => await ToolSearchHuggingFaceAsync(args),
                "install_model" => await ToolInstallModelAsync(args),
                "install_runtime" => await ToolInstallRuntimeAsync(args),
                "get_hub_settings" => await ToolGetHubSettingsAsync(),
                "update_hub_settings" => ToolUpdateHubSettings(args),
                "manage_service" => await ToolManageServiceAsync(args),
                "get_director_context" => ServiceManager.LoadMediaDirectorContext(),
                "create_video_story_pipeline" => await ToolCreateVideoStoryAsync(args),
                "create_photo_story_pipeline" => ToolCreatePhotoStory(args),
                _ => throw new ArgumentException($"Unknown tool: {name}")
            };

            return MakeResult(idNode, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = resultText }
                },
                ["isError"] = false
            });
        }
        catch (Exception ex)
        {
            return MakeResult(idNode, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"Error in {name}: {ex.Message}" }
                },
                ["isError"] = true
            });
        }
    }

    private static string? GetStr(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? GetInt(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.TryGetInt32(out int i) ? i : null;

    private static double? GetDouble(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.TryGetDouble(out double d) ? d : null;

    private static bool? GetBool(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    private static async Task<HardwareProfile> DetectCurrentHardwareAsync()
    {
        var m = await ServiceManager.GetSystemMetricsAsync();
        double vramGb = m.Gpu is not null ? Math.Round(m.Gpu.VramTotalMiB / 1024.0, 1) : 0;
        return HardwareAdvisor.BuildProfile(m.Gpu?.Name, vramGb, m.RamTotalGB, m.DiskFreeDGB);
    }

    private static async Task<string> ToolGetHardwareProfileAsync()
    {
        var hw = await DetectCurrentHardwareAsync();
        return JsonSerializer.Serialize(hw, JsonPretty);
    }

    private static string ToolLookupGpuWiki(JsonElement args)
    {
        string? series = GetStr(args, "series");
        string? query = GetStr(args, "query");
        double? simRam = GetDouble(args, "simulate_ram_gb");

        var list = HardwareAdvisor.GpuWiki.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(series))
            list = list.Where(g => g.Series.Equals(series.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
            list = list.Where(g => g.ModelName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                   g.Architecture.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

        var matched = list.ToList();
        object payload = simRam.HasValue && matched.Count > 0
            ? new
            {
                count = matched.Count,
                ram_combo_advice = HardwareAdvisor.DescribeRamTier(simRam.Value, matched[0].VramGB),
                gpus = matched
            }
            : new { count = matched.Count, gpus = matched };

        return JsonSerializer.Serialize(payload, JsonPretty);
    }

    private static async Task<string> ToolListRecommendedModelsAsync(JsonElement args)
    {
        string category = (GetStr(args, "category") ?? "all").ToLowerInvariant();
        string? simGpu = GetStr(args, "simulate_gpu");
        double? simRam = GetDouble(args, "simulate_ram_gb");

        HardwareProfile hw;
        if (!string.IsNullOrWhiteSpace(simGpu))
        {
            var g = HardwareAdvisor.MatchGpu(simGpu, 0)
                ?? HardwareAdvisor.GpuWiki.FirstOrDefault(x => x.ModelName.Contains(simGpu, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"GPU '{simGpu}' not found in NVIDIA 10-50 series wiki.");
            hw = HardwareAdvisor.BuildSimulatedProfile(g, simRam ?? 32);
        }
        else
        {
            hw = await DetectCurrentHardwareAsync();
            if (simRam.HasValue)
                hw = HardwareAdvisor.BuildProfile(hw.DetectedGpuName, hw.DetectedVramGB, simRam.Value, hw.FreeDiskGB);
        }

        var items = ModelCatalog.All
            .Where(m => category == "all" || m.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(m =>
            {
                var fit = HardwareAdvisor.EvaluateModel(m, hw);
                var (allInstalled, present, total, missingGB) = ModelCatalog.CheckInstallState(m);
                return new
                {
                    id = m.Id,
                    category = m.Category,
                    name = m.Name,
                    short_tag = m.ShortTag,
                    total_size_gb = m.TotalSizeGB,
                    gated = m.IsGated,
                    fit_level = fit.Level.ToString(),
                    badge = fit.BadgeText,
                    reason = fit.ReasonRu,
                    estimated_speed = fit.EstimatedSpeedHint,
                    installed = allInstalled,
                    files_present = $"{present}/{total}",
                    missing_gb = missingGB,
                    summary = m.SummaryRu
                };
            })
            .OrderBy(x => x.category)
            .ThenBy(x => x.fit_level)
            .ToList();

        return JsonSerializer.Serialize(new { hardware = hw.OverallTierSummaryRu, ram_advice = hw.RamAdviceRu, models = items }, JsonPretty);
    }

    private static string ToolListInstalledModels()
    {
        var list = ServiceManager.ScanInstalledModels();
        return JsonSerializer.Serialize(new
        {
            count = list.Count,
            total_gb = Math.Round(list.Sum(m => m.SizeGB), 2),
            models = list
        }, JsonPretty);
    }

    private static async Task<string> ToolSearchHuggingFaceAsync(JsonElement args)
    {
        string? repoId = GetStr(args, "repo_id");
        if (!string.IsNullOrWhiteSpace(repoId))
        {
            var files = await HfClient.ListModelFilesAsync(repoId.Trim());
            return JsonSerializer.Serialize(new
            {
                repo_id = repoId.Trim(),
                file_count = files.Count,
                files = files.Select(f => new { f.Path, size_gb = Math.Round(f.SizeBytes / (1024.0 * 1024 * 1024), 2), f.Sha256 })
            }, JsonPretty);
        }

        string query = GetStr(args, "query") ?? throw new ArgumentException("Provide either 'query' or 'repo_id'.");
        int limit = GetInt(args, "limit") ?? 10;
        var repos = await HfClient.SearchModelsAsync(query, limit);
        return JsonSerializer.Serialize(new { query, results = repos }, JsonPretty);
    }

    private static async Task<string> ToolInstallModelAsync(JsonElement args)
    {
        string? catalogId = GetStr(args, "catalog_id");
        if (!string.IsNullOrWhiteSpace(catalogId))
        {
            var spec = ModelCatalog.FindById(catalogId.Trim())
                ?? throw new ArgumentException($"Catalog ID '{catalogId}' not found. Call list_recommended_models first.");
            await HfClient.InstallCatalogBundleAsync(spec, p =>
                Console.Error.WriteLine($"[install {p.FileIndex}/{p.TotalFiles}] {p.CurrentFile}: {p.Percent:F1}% ({p.SpeedMBs:F1} MB/s) - {p.StatusText}"));
            return JsonSerializer.Serialize(new
            {
                success = true,
                catalog_id = spec.Id,
                name = spec.Name,
                files_installed = spec.Files.Length
            }, JsonPretty);
        }

        string repoId = GetStr(args, "repo_id") ?? throw new ArgumentException("Provide 'catalog_id' or ('repo_id' + 'remote_path').");
        string remotePath = GetStr(args, "remote_path") ?? throw new ArgumentException("Provide 'remote_path'.");
        string targetFolder = (GetStr(args, "target_folder") ?? "llm_models").ToLowerInvariant();
        string fileName = Path.GetFileName(remotePath);
        string destPath = targetFolder switch
        {
            "llm_models" => Path.Combine(HubConfig.LlmModelsDir, fileName),
            "diffusion_models" => Path.Combine(HubConfig.ComfyModelsDir, "diffusion_models", fileName),
            "loras" => Path.Combine(HubConfig.ComfyModelsDir, "loras", fileName),
            "text_encoders" => Path.Combine(HubConfig.ComfyModelsDir, "text_encoders", fileName),
            "vae" => Path.Combine(HubConfig.ComfyModelsDir, "vae", fileName),
            "upscale_models" => Path.Combine(HubConfig.ComfyModelsDir, "upscale_models", fileName),
            _ => throw new ArgumentException($"Unknown target_folder: {targetFolder}")
        };

        await HfClient.DownloadSingleFileAsync(fileName, repoId.Trim(), remotePath.Trim(), destPath, 1, 1, p =>
            Console.Error.WriteLine($"[install] {p.CurrentFile}: {p.Percent:F1}% ({p.SpeedMBs:F1} MB/s) - {p.StatusText}"));
        return JsonSerializer.Serialize(new { success = true, repo_id = repoId, saved_to = destPath }, JsonPretty);
    }

    private static async Task<string> ToolInstallRuntimeAsync(JsonElement args)
    {
        string runtime = (GetStr(args, "runtime") ?? "").Trim().ToLowerInvariant();
        bool dryRun = GetBool(args, "dry_run") ?? false;
        var hw = await DetectCurrentHardwareAsync();
        var installer = new RuntimeInstaller();

        if (runtime is "llama" or "llama.cpp")
        {
            if (dryRun)
            {
                var plan = await installer.ResolveLatestLlamaPlanAsync(hw);
                return JsonSerializer.Serialize(new
                {
                    runtime = "llama",
                    dry_run = true,
                    already_installed = RuntimeInstaller.IsLlamaInstalled(),
                    tag = plan.TagName,
                    backend = plan.BackendLabel,
                    total_mb = Math.Round(plan.TotalBytes / 1048576.0, 1),
                    assets = plan.Assets.Select(a => new { a.Name, size_mb = Math.Round(a.SizeBytes / 1048576.0, 1) })
                }, JsonPretty);
            }
            var prog = new Progress<RuntimeInstallProgress>(p =>
                Console.Error.WriteLine($"[runtime llama] {p.Stage} ({p.Percent:F1}%, {p.SpeedMBs:F1} MB/s)"));
            string msg = await installer.InstallLlamaCppAsync(hw, prog);
            return JsonSerializer.Serialize(new { success = true, runtime = "llama", message = msg, status = RuntimeInstaller.GetStatus() }, JsonPretty);
        }

        if (runtime is "comfyui" or "comfy")
        {
            if (dryRun)
            {
                var plan = await installer.ResolveLatestComfyPlanAsync(hw);
                return JsonSerializer.Serialize(new
                {
                    runtime = "comfyui",
                    dry_run = true,
                    already_installed = RuntimeInstaller.IsComfyInstalled(),
                    tag = plan.TagName,
                    variant = plan.VariantLabel,
                    total_mb = Math.Round(plan.Asset.SizeBytes / 1048576.0, 1),
                    asset = plan.Asset.Name
                }, JsonPretty);
            }
            var prog = new Progress<RuntimeInstallProgress>(p =>
                Console.Error.WriteLine($"[runtime comfyui] {p.Stage} ({p.Percent:F1}%, {p.SpeedMBs:F1} MB/s)"));
            string msg = await installer.InstallComfyUiAsync(hw, prog);
            return JsonSerializer.Serialize(new { success = true, runtime = "comfyui", message = msg, status = RuntimeInstaller.GetStatus() }, JsonPretty);
        }

        throw new ArgumentException("Provide runtime: 'llama' or 'comfyui'.");
    }

    private static async Task<string> ToolGetHubSettingsAsync()
    {
        HubConfig.Reload();
        var hf = await HfClient.VerifyTokenAsync();
        return JsonSerializer.Serialize(new
        {
            config_file = HubConfig.ConfigFilePath,
            config = HubConfig.Current,
            runtimes = RuntimeInstaller.GetStatus(),
            active_llm = ServiceManager.GetActiveLlmFileName(),
            available_llms = ServiceManager.GetAvailableLlmModels().Select(m => m.FileName),
            huggingface = hf
        }, JsonPretty);
    }

    private static string ToolUpdateHubSettings(JsonElement args)
    {
        HubConfig.Reload();
        var cfg = HubConfig.Current;

        if (GetInt(args, "llm_ctx_size") is int ctx) cfg.LlmCtxSize = Math.Max(0, ctx);
        if (GetInt(args, "llm_fit_target_mib") is int fit) cfg.LlmFitTargetMiB = Math.Max(0, fit);
        if (GetDouble(args, "llm_temperature") is double temp) cfg.LlmTemperature = Math.Clamp(temp, 0.0, 2.0);
        if (GetDouble(args, "llm_presence_penalty") is double pres) cfg.LlmPresencePenalty = Math.Clamp(pres, 0.0, 2.0);
        if (GetDouble(args, "llm_dry_multiplier") is double dry) cfg.LlmDryMultiplier = Math.Clamp(dry, 0.0, 2.0);
        if (GetInt(args, "llm_reasoning_budget") is int tb) cfg.LlmReasoningBudget = Math.Max(-1, tb);
        if (GetDouble(args, "wan_shift") is double shift) cfg.WanShift = Math.Clamp(shift, 2.0, 10.0);
        if (GetDouble(args, "wan_svi_high_lightning_strength") is double lstr) cfg.WanSviHighLightningStrength = Math.Clamp(lstr, 0.4, 1.2);
        if (GetInt(args, "wan_svi_tail_frames") is int tail) cfg.WanSviTailFrames = Math.Clamp(tail, 1, 9);
        if (GetStr(args, "default_photo_engine") is string pe && pe is "flux3" or "zimage" or "qwen21") cfg.DefaultPhotoEngine = pe;
        if (GetStr(args, "default_video_engine") is string ve && ve is "wan14b_fp8" or "wan14b" or "wan14b_q6" or "ltx25") cfg.DefaultVideoEngine = ve;
        if (GetStr(args, "llm_root") is string lr && !string.IsNullOrWhiteSpace(lr)) cfg.LlmRoot = lr.Trim();
        if (GetStr(args, "media_root") is string mr && !string.IsNullOrWhiteSpace(mr))
        {
            cfg.MediaRoot = mr.Trim();
            cfg.StoriesRoot = Path.Combine(cfg.MediaRoot, "Stories");
        }
        HubConfig.Save();

        if (GetStr(args, "active_llm_file") is string activeLlm && !string.IsNullOrWhiteSpace(activeLlm))
            ServiceManager.SetActiveLlmFileName(activeLlm);

        return JsonSerializer.Serialize(new { success = true, updated = cfg, active_llm = ServiceManager.GetActiveLlmFileName() }, JsonPretty);
    }

    private static async Task<string> ToolManageServiceAsync(JsonElement args)
    {
        string action = (GetStr(args, "action") ?? "status").ToLowerInvariant();
        string? svc = GetStr(args, "service")?.ToLowerInvariant();

        if (action == "status")
        {
            var statuses = await ServiceManager.GetAllStatusesAsync();
            var metrics = await ServiceManager.GetSystemMetricsAsync();
            return JsonSerializer.Serialize(new { statuses, metrics }, JsonPretty);
        }
        if (action == "stop_all")
        {
            await ServiceManager.StopAllGpuAsync();
            return JsonSerializer.Serialize(new { success = true, message = "All GPU services stopped." });
        }
        if (svc is not ("qwen" or "comfyui"))
            throw new ArgumentException("service must be 'qwen' or 'comfyui'.");

        if (action == "start")
        {
            await ServiceManager.SwitchAndStartAsync(svc, msg => Console.Error.WriteLine($"[service] {msg}"));
            return JsonSerializer.Serialize(new { success = true, service = svc, status = "running" });
        }
        if (action == "stop")
        {
            await ServiceManager.StopServiceAsync(svc);
            return JsonSerializer.Serialize(new { success = true, service = svc, status = "stopped" });
        }
        throw new ArgumentException($"Unknown action: {action}");
    }

    private static async Task<string> ToolCreateVideoStoryAsync(JsonElement args)
    {
        if (!args.TryGetProperty("stages", out var stagesEl) || stagesEl.ValueKind != JsonValueKind.Array || stagesEl.GetArrayLength() == 0)
            throw new ArgumentException("stages array must contain at least 1 stage.");

        string starterPrompt = GetStr(args, "starter_prompt") ?? "";
        string starterEngine = GetStr(args, "starter_engine") ?? "flux3";
        string negPrompt = GetStr(args, "negative_prompt") ?? "";
        string engine = GetStr(args, "engine") ?? HubConfig.Current.DefaultVideoEngine;
        int w = GetInt(args, "width") ?? 832;
        int h = GetInt(args, "height") ?? 480;
        int dur = GetInt(args, "duration_sec") ?? 5;
        bool runNow = GetBool(args, "run_now") ?? false;

        var project = new VideoStoryProject
        {
            UseStarterFrame = !string.IsNullOrWhiteSpace(starterPrompt),
            StarterEngine = starterEngine,
            StarterPrompt = starterPrompt,
            NegativePrompt = negPrompt,
            Stages = []
        };

        int idx = 1;
        foreach (var stEl in stagesEl.EnumerateArray())
        {
            string p = stEl.TryGetProperty("prompt", out var pEl) ? pEl.GetString() ?? "" : "";
            string rm = stEl.TryGetProperty("ref_mode", out var rEl) ? rEl.GetString() ?? "svi_pro" : "svi_pro";
            string bp = stEl.TryGetProperty("bridge_prompt", out var bEl) ? bEl.GetString() ?? "" : "";
            project.Stages.Add(new VideoStoryStage
            {
                Index = idx++,
                Prompt = p.Trim(),
                RefMode = rm,
                BridgePrompt = bp.Trim()
            });
        }

        Directory.CreateDirectory(HubConfig.StoriesRoot);
        string storyFile = Path.Combine(HubConfig.StoriesRoot, "video_story.json");
        File.WriteAllText(storyFile, JsonSerializer.Serialize(project, JsonPretty), Encoding.UTF8);

        if (!runNow)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                saved_to = storyFile,
                stages_count = project.Stages.Count,
                note = "Video Story saved! Open Local AI Hub -> 'Видео-история' and click '▶ Запустить пайплайн', or call create_video_story_pipeline with run_now=true."
            }, JsonPretty);
        }

        // Execute full pipeline via ComfyUI
        await ServiceManager.SwitchAndStartAsync("comfyui", msg => Console.Error.WriteLine($"[comfy] {msg}"));
        string framesDir = Path.Combine(HubConfig.StoriesRoot, "video_frames");
        if (project.UseStarterFrame)
        {
            var (sp, _) = await StoryClient.GeneratePhotoStageAsync(project.StarterEngine, project.StarterPrompt, w, h, null, null, "stories/mcp_starter");
            project.StarterImagePath = sp;
            File.WriteAllText(storyFile, JsonSerializer.Serialize(project, JsonPretty), Encoding.UTF8);
        }

        for (int i = 0; i < project.Stages.Count; i++)
        {
            var st = project.Stages[i];
            string? startImg = i == 0 ? project.StarterImagePath : (st.RefMode == "anchor_only" ? project.StarterImagePath : project.Stages[i - 1].LastFramePath);
            string? prevVid = i > 0 ? project.Stages[i - 1].OutputVideoPath : null;
            string? anchorImg = i > 0 && st.RefMode is "svi_pro" or "flux3_bridge" or "prev_to_anchor" ? project.StarterImagePath : null;

            var (vp, pp, lf, seed) = await StoryClient.GenerateVideoStageAsync(
                engine, st.Prompt, w, h, dur, startImg, anchorImg, st.RefMode,
                $"stories/mcp_stage_{i + 1}", framesDir, null, null, default, null, st.BridgePrompt, prevVid, project.NegativePrompt);
            st.OutputVideoPath = vp;
            st.PreviewImagePath = pp;
            st.LastFramePath = lf;
            st.LastSeed = seed;
            File.WriteAllText(storyFile, JsonSerializer.Serialize(project, JsonPretty), Encoding.UTF8);
        }

        var clips = project.Stages.Select(s => s.OutputVideoPath!).ToList();
        var trims = project.Stages.Select((s, i) => i == 0 ? 0 : (s.RefMode is "prev_only" or "prev_to_anchor" ? 1 : 0)).ToList();
        string outMp4 = Path.Combine(HubConfig.StoriesRoot, $"mcp_story_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        await StoryClient.ConcatVideosAsync(clips, outMp4, trims);

        return JsonSerializer.Serialize(new
        {
            success = true,
            saved_to = storyFile,
            output_mp4 = outMp4,
            stages = project.Stages.Select(s => new { s.Index, s.OutputVideoPath, s.LastSeed })
        }, JsonPretty);
    }

    private static string ToolCreatePhotoStory(JsonElement args)
    {
        if (!args.TryGetProperty("stages", out var stagesEl) || stagesEl.ValueKind != JsonValueKind.Array || stagesEl.GetArrayLength() == 0)
            throw new ArgumentException("stages array must contain at least 1 stage.");

        var stages = new List<PhotoStoryStage>();
        int idx = 1;
        foreach (var stEl in stagesEl.EnumerateArray())
        {
            string p = stEl.TryGetProperty("prompt", out var pEl) ? pEl.GetString() ?? "" : "";
            bool ua = idx > 1 && (!stEl.TryGetProperty("use_anchor_ref", out var uaEl) || uaEl.GetBoolean());
            bool up = idx > 1 && stEl.TryGetProperty("use_prev_ref", out var upEl) && upEl.GetBoolean();
            stages.Add(new PhotoStoryStage { Index = idx++, Prompt = p.Trim(), UseAnchorRef = ua, UsePrevRef = up });
        }

        Directory.CreateDirectory(HubConfig.StoriesRoot);
        string file = Path.Combine(HubConfig.StoriesRoot, "photo_story.json");
        File.WriteAllText(file, JsonSerializer.Serialize(stages, JsonPretty), Encoding.UTF8);
        return JsonSerializer.Serialize(new { success = true, saved_to = file, stages_count = stages.Count }, JsonPretty);
    }

    private static JsonObject MakeResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject MakeError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
