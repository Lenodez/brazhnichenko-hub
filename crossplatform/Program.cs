using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAIHub.Services;

namespace LocalAIHub.CrossPlatform;

public sealed record ActiveJobState(
    string Id,
    string Kind,
    string Title,
    string Status,      // running | done | error | cancelled
    string Stage,
    double Percent,
    double SpeedMBs,
    string? Error = null
);

public static class Program
{
    private static readonly AiServiceManager Svc = new();
    private static readonly HuggingFaceClient Hf = new();
    private static readonly RuntimeInstaller RtInstaller = new();
    private static readonly HubAssistantService Assistant = new();
    private static readonly LocalVoiceService Voice = new();
    private static readonly ComfyStoryClient Comfy = new();
    private static readonly TelemetryHistoryBuffer VramHistory = new(30);
    private static readonly TelemetryHistoryBuffer TempHistory = new(30);
    private static readonly TelemetryHistoryBuffer RamHistory = new(30);
    private static double _lastTokPerSec;
    private static readonly HttpClient StreamHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly ConcurrentDictionary<string, ActiveJobState> Jobs = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> JobCts = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task Main(string[] args)
    {
        int port = ParsePortArg(args, 17900);
        bool noBrowser = args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase));

        // Single-instance friendliness: if Brazhnichenko Hub is already running on this port, just open the browser!
        if (await IsAlreadyRunningAsync(port))
        {
            if (!noBrowser)
                SafeOpenBrowser($"http://127.0.0.1:{port}");
            return;
        }

        if (!IsPortFree(port))
            port = FindFreePort(port + 1);

        string webRoot = ResolveWebRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = webRoot,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapApiRoutes(app, port);

        using var bgCts = new CancellationTokenSource();
        _ = RunTelemetryLoopAsync(bgCts.Token);

        await app.StartAsync();
        string url = $"http://127.0.0.1:{port}";
        Console.WriteLine($"Brazhnichenko Hub v{AppUpdateService.CurrentVersion} running at {url} ({RuntimeInformation.OSDescription})");

        if (!noBrowser)
            SafeOpenBrowser(url);

        await app.WaitForShutdownAsync();
        bgCts.Cancel();
    }

    private static void MapApiRoutes(WebApplication app, int port)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            ok = true,
            app = "Brazhnichenko Hub",
            version = AppUpdateService.CurrentVersion,
            platform = GetPlatformLabel(),
            arch = RuntimeInformation.OSArchitecture.ToString(),
            port
        }));

        // ==================== STATUS & TELEMETRY ====================
        app.MapGet("/api/status", async () =>
        {
            var metrics = await Svc.GetSystemMetricsAsync();
            var hw = BuildCurrentHardwareProfile(metrics);
            var statuses = await Svc.GetAllStatusesAsync();
            var rt = RuntimeInstaller.GetStatus();
            var voiceSt = LocalVoiceService.GetStatus();
            var llms = Svc.GetAvailableLlmModels();
            string activeLlm = Svc.GetActiveLlmFileName();
            string? mmproj = Svc.FindMatchingMmprojForModel(activeLlm);
            string? hfToken = HubConfig.GetHuggingFaceToken();

            double diskTotalGB = 0;
            try
            {
                var di = new DriveInfo(HubConfig.DataDriveLetter);
                if (di.IsReady) diskTotalGB = Math.Round(di.TotalSize / (1024.0 * 1024 * 1024), 1);
            }
            catch { }

            return Results.Ok(new
            {
                version = AppUpdateService.CurrentVersion,
                platform = GetPlatformLabel(),
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture.ToString(),
                metrics,
                diskTotalGB,
                driveLabel = HubConfig.DataDriveLetter,
                hardware = hw,
                history = new
                {
                    vramPct = VramHistory.GetOrderedValues(),
                    tempC = TempHistory.GetOrderedValues(),
                    ramPct = RamHistory.GetOrderedValues(),
                    lastTokPerSec = _lastTokPerSec
                },
                services = statuses,
                runtime = rt,
                voice = voiceSt,
                hfConnected = !string.IsNullOrWhiteSpace(hfToken),
                activeLlm,
                activeLlmHasVision = !string.IsNullOrWhiteSpace(mmproj),
                activeLlmMmproj = mmproj is null ? null : Path.GetFileName(mmproj),
                availableLlms = llms,
                jobs = Jobs.Values.OrderByDescending(j => j.Status == "running").ThenBy(j => j.Title).ToList(),
                paths = new
                {
                    llmRoot = HubConfig.LlmRoot,
                    mediaRoot = HubConfig.MediaRoot,
                    configDir = HubConfig.ConfigDir
                }
            });
        });

        // ==================== APPLE SILICON M1-M6 & NVIDIA WIKI + SIMULATOR ====================
        app.MapGet("/api/hardware/wiki", () => Results.Ok(new
        {
            appleSilicon = HardwareAdvisor.AppleSiliconWiki,
            nvidia = HardwareAdvisor.GpuWiki
        }));

        app.MapPost("/api/hardware/simulate", async (SimulateHardwareRequest req) =>
        {
            HardwareProfile hw;
            if (req.Platform.Equals("apple", StringComparison.OrdinalIgnoreCase))
            {
                var chip = HardwareAdvisor.MatchAppleSilicon(req.ChipName ?? "Apple M4 Pro")
                    ?? HardwareAdvisor.AppleSiliconWiki.First();
                double ram = req.RamGB > 0 ? req.RamGB : chip.RamOptionsGB.First();
                hw = HardwareAdvisor.BuildSimulatedAppleProfile(chip, ram);
            }
            else if (req.Platform.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
            {
                var gpu = HardwareAdvisor.GpuWiki.FirstOrDefault(g =>
                    g.ModelName.Equals(req.ChipName, StringComparison.OrdinalIgnoreCase))
                    ?? HardwareAdvisor.MatchGpu(req.ChipName, req.VramGB)
                    ?? HardwareAdvisor.GpuWiki.First(g => g.ModelName.Contains("4080"));
                double ram = req.RamGB > 0 ? req.RamGB : 32;
                hw = HardwareAdvisor.BuildProfile(gpu.ModelName, req.VramGB > 0 ? req.VramGB : gpu.VramGB, ram, 250);
            }
            else
            {
                var metrics = await Svc.GetSystemMetricsAsync();
                hw = BuildCurrentHardwareProfile(metrics);
            }

            var models = EvaluateCatalogForProfile(hw);
            return Results.Ok(new { hardware = hw, models });
        });

        // ==================== CATALOG & HF SEARCH ====================
        app.MapGet("/api/catalog", async (string? platform, string? chip, double? ram) =>
        {
            HardwareProfile hw;
            if (!string.IsNullOrWhiteSpace(platform) && platform.Equals("apple", StringComparison.OrdinalIgnoreCase))
            {
                var c = HardwareAdvisor.MatchAppleSilicon(chip ?? "Apple M4 Pro") ?? HardwareAdvisor.AppleSiliconWiki.First();
                hw = HardwareAdvisor.BuildSimulatedAppleProfile(c, ram is > 0 ? ram.Value : c.RamOptionsGB.First());
            }
            else if (!string.IsNullOrWhiteSpace(platform) && platform.Equals("nvidia", StringComparison.OrdinalIgnoreCase))
            {
                var g = HardwareAdvisor.GpuWiki.FirstOrDefault(x => x.ModelName.Equals(chip, StringComparison.OrdinalIgnoreCase))
                    ?? HardwareAdvisor.MatchGpu(chip, 0)
                    ?? HardwareAdvisor.GpuWiki.First(x => x.ModelName.Contains("4080"));
                hw = HardwareAdvisor.BuildProfile(g.ModelName, g.VramGB, ram is > 0 ? ram.Value : 32, 250);
            }
            else
            {
                var metrics = await Svc.GetSystemMetricsAsync();
                hw = BuildCurrentHardwareProfile(metrics);
            }

            return Results.Ok(new
            {
                hardware = hw,
                models = EvaluateCatalogForProfile(hw),
                installedDisk = Svc.ScanInstalledModels()
            });
        });

        app.MapPost("/api/catalog/install", (InstallModelRequest req) =>
        {
            var spec = ModelCatalog.All.FirstOrDefault(m => m.Id.Equals(req.ModelId, StringComparison.OrdinalIgnoreCase));
            if (spec is null) return Results.NotFound(new { error = $"Модель {req.ModelId} не найдена в каталоге." });

            string jobId = StartBackgroundJob("model", $"Скачивание {spec.Name}", async (report, ct) =>
            {
                await Hf.InstallCatalogBundleAsync(spec, p =>
                    report($"{p.CurrentFile} ({p.FileIndex}/{p.TotalFiles})", p.Percent, p.SpeedMBs), ct);
            });

            return Results.Ok(new { jobId, modelId = spec.Id });
        });

        app.MapPost("/api/catalog/delete", (InstallModelRequest req) =>
        {
            var spec = ModelCatalog.All.FirstOrDefault(m => m.Id.Equals(req.ModelId, StringComparison.OrdinalIgnoreCase));
            if (spec is null) return Results.NotFound(new { error = "Модель не найдена." });
            var (filesToDelete, reclaimGB, sharedKept) = ModelCatalog.GetDeletableFiles(spec);
            int deleted = 0;
            foreach (string f in filesToDelete)
            {
                try { if (File.Exists(f)) { File.Delete(f); deleted++; } } catch { }
            }
            return Results.Ok(new { deleted, freedGB = reclaimGB, keptShared = sharedKept });
        });

        app.MapGet("/api/hf/search", async (string q, string? category, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());
            string query = q.Trim();
            if (category == "text" && !query.Contains("gguf", StringComparison.OrdinalIgnoreCase))
                query += " gguf";
            var results = await Hf.SearchModelsAsync(query, 15, ct);
            return Results.Ok(results.Select(r => new
            {
                r.RepoId,
                r.Downloads,
                r.Likes,
                r.PipelineTag,
                isGated = r.Gated
            }));
        });

        app.MapGet("/api/hf/files", async (string repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(repo)) return Results.BadRequest(new { error = "repo required" });
            var files = await Hf.ListModelFilesAsync(repo.Trim(), ct);
            return Results.Ok(files.Select(f => new { filePath = f.Path, f.SizeBytes, f.Sha256 }));
        });

        app.MapPost("/api/hf/download", (HfDownloadRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.RepoId) || string.IsNullOrWhiteSpace(req.FilePath))
                return Results.BadRequest(new { error = "repoId and filePath required" });

            string fileName = Path.GetFileName(req.FilePath);
            string targetSubdir = string.IsNullOrWhiteSpace(req.TargetSubdir)
                ? (fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? "llm_models" : "diffusion_models")
                : req.TargetSubdir;

            string destPath = ModelCatalog.ResolveLocalPath(new CatalogFileSpec(req.RepoId, req.FilePath, targetSubdir, fileName, 0));
            string jobId = StartBackgroundJob("hf", $"HF: {fileName}", async (report, ct) =>
            {
                await Hf.DownloadSingleFileAsync(fileName, req.RepoId, req.FilePath, destPath, 1, 1,
                    p => report(p.CurrentFile, p.Percent, p.SpeedMBs), ct);
            });
            return Results.Ok(new { jobId, destPath });
        });

        app.MapGet("/api/jobs", () => Results.Ok(Jobs.Values.OrderByDescending(j => j.Status == "running").ToList()));
        app.MapPost("/api/jobs/cancel", (CancelJobRequest req) =>
        {
            if (JobCts.TryGetValue(req.JobId, out var cts))
                cts.Cancel();
            return Results.Ok(new { ok = true });
        });

        // ==================== SERVICES & RUNTIMES ====================
        app.MapPost("/api/services/start", async (ServiceActionRequest req) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(req.ModelFile))
                    Svc.SetActiveLlmFileName(req.ModelFile);
                await Svc.SwitchAndStartAsync(req.Id);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/services/stop", async (ServiceActionRequest req) =>
        {
            await Svc.StopServiceAsync(req.Id);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/services/stop-all", async () =>
        {
            await Svc.StopAllGpuAsync();
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/services/logs", (string id) =>
        {
            try { return Results.Ok(new { id, logs = Svc.ReadLogsTail(id, 150) }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/runtime/install", async (RuntimeInstallRequest req) =>
        {
            var metrics = await Svc.GetSystemMetricsAsync();
            var hw = BuildCurrentHardwareProfile(metrics);
            string kind = (req.Runtime ?? "").ToLowerInvariant();
            string jobId = StartBackgroundJob("runtime", $"Установка {kind}", async (report, ct) =>
            {
                var prog = new Progress<RuntimeInstallProgress>(p => report(p.Stage, p.Percent, p.SpeedMBs));
                switch (kind)
                {
                    case "llama":
                        await RtInstaller.InstallLlamaCppAsync(hw, prog, ct);
                        break;
                    case "comfyui":
                        await RtInstaller.InstallComfyUiAsync(hw, prog, ct);
                        break;
                    case "whisper":
                        await Voice.InstallWhisperAsync(msg => report(msg, 50, 0), ct);
                        break;
                    case "piper":
                        await Voice.InstallPiperRuAsync(msg => report(msg, 50, 0), ct);
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный движок: {kind}");
                }
            });
            return Results.Ok(new { jobId });
        });

        // ==================== CHAT 2.0 (SESSIONS + SSE STREAM + VISION) ====================
        app.MapGet("/api/chat/sessions", () => Results.Ok(new
        {
            personas = ChatSessionService.BuiltInPersonas,
            sessions = ChatSessionService.LoadAll()
        }));

        app.MapPost("/api/chat/sessions", (CreateSessionRequest? req) =>
        {
            var sessions = ChatSessionService.LoadAll();
            var s = ChatSessionService.CreateNew(req?.PersonaId ?? "default");
            if (req?.Temperature is double t) s.Temperature = Math.Clamp(t, 0.0, 1.5);
            sessions.Insert(0, s);
            ChatSessionService.SaveAll(sessions);
            return Results.Ok(s);
        });

        app.MapPatch("/api/chat/sessions/{id}", (string id, UpdateSessionRequest req) =>
        {
            var sessions = ChatSessionService.LoadAll();
            var s = sessions.FirstOrDefault(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(req.Title)) s.Title = req.Title.Trim();
            if (!string.IsNullOrWhiteSpace(req.PersonaId)) s.PersonaId = req.PersonaId;
            if (req.Temperature.HasValue) s.Temperature = Math.Clamp(req.Temperature.Value, 0.0, 1.5);
            if (req.ClearMessages == true) s.Messages.Clear();
            s.UpdatedAtUtc = DateTime.UtcNow;
            ChatSessionService.SaveAll(sessions);
            return Results.Ok(s);
        });

        app.MapDelete("/api/chat/sessions/{id}", (string id) =>
        {
            var sessions = ChatSessionService.LoadAll();
            sessions.RemoveAll(x => x.Id == id);
            ChatSessionService.SaveAll(sessions);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/chat/stream", async (HttpContext ctx, ChatStreamRequest req, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";

            async Task SendEventAsync(string type, object payload)
            {
                string json = JsonSerializer.Serialize(new { type, data = payload }, JsonOpts);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(req.ModelFile))
                    Svc.SetActiveLlmFileName(req.ModelFile);

                string activeFile = Svc.GetActiveLlmFileName();
                bool hasImage = !string.IsNullOrWhiteSpace(req.ImageBase64);
                string? mmproj = Svc.FindMatchingMmprojForModel(activeFile);

                if (hasImage && string.IsNullOrWhiteSpace(mmproj))
                {
                    await SendEventAsync("vision_missing", new
                    {
                        activeLlm = activeFile,
                        canDownloadQwen36Mmproj = activeFile.Contains("qwen3.6", StringComparison.OrdinalIgnoreCase) && activeFile.Contains("hauhau", StringComparison.OrdinalIgnoreCase),
                        message = "Для анализа картинок нужен Vision-модуль (mmproj-*.gguf). Скачай его в 1 клик ниже или выбери Vision-модель (Qwen 2.5 VL 7B / Gemma 3 12B Vision)."
                    });
                    return;
                }

                var qwenSvc = Svc.GetService("qwen");
                if (!await Svc.IsRunningAsync(qwenSvc))
                {
                    await SendEventAsync("status", "Запуск локальной LLM...");
                    await Svc.SwitchAndStartAsync("qwen");
                }

                var sessions = ChatSessionService.LoadAll();
                var session = sessions.FirstOrDefault(s => s.Id == req.SessionId);
                if (session is null)
                {
                    session = ChatSessionService.CreateNew(req.PersonaId ?? "default");
                    sessions.Insert(0, session);
                }
                session.PersonaId = req.PersonaId ?? session.PersonaId;
                session.Temperature = req.Temperature ?? session.Temperature;

                string userText = (req.Message ?? "").Trim();
                if (session.Messages.Count == 0 && !string.IsNullOrWhiteSpace(userText))
                    session.Title = ChatSessionService.AutoTitleFromPrompt(userText);

                var persona = ChatSessionService.GetPersona(session.PersonaId);
                var apiMessages = new List<object> { new { role = "system", content = persona.SystemPrompt } };
                foreach (var m in session.Messages.TakeLast(16))
                    apiMessages.Add(new { role = m.Role, content = m.Content });

                if (hasImage)
                {
                    string dataUrl = req.ImageBase64!.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? req.ImageBase64!
                        : $"data:image/png;base64,{req.ImageBase64}";
                    apiMessages.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = string.IsNullOrWhiteSpace(userText) ? "Что изображено на этом фото?" : userText },
                            new { type = "image_url", image_url = new { url = dataUrl } }
                        }
                    });
                }
                else
                {
                    apiMessages.Add(new { role = "user", content = userText });
                }

                var body = new { messages = apiMessages, temperature = session.Temperature, stream = true };
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{HubConfig.Current.QwenPort}/v1/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                };

                using var httpResp = await StreamHttp.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!httpResp.IsSuccessStatusCode)
                {
                    string err = await httpResp.Content.ReadAsStringAsync(ct);
                    if (err.Contains("mmproj", StringComparison.OrdinalIgnoreCase) || err.Contains("image input is not supported", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendEventAsync("vision_missing", new
                        {
                            activeLlm = activeFile,
                            message = "Сервер LLM запущен без --mmproj. Перезапусти LLM (если mmproj уже скачан) или установи Vision-модель."
                        });
                        return;
                    }
                    await SendEventAsync("error", $"HTTP {(int)httpResp.StatusCode}: {err[..Math.Min(300, err.Length)]}");
                    return;
                }

                var fullContent = new StringBuilder();
                var fullThink = new StringBuilder();
                int tokenCount = 0;
                var sw = Stopwatch.StartNew();

                await using var stream = await httpResp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    string data = line[5..].Trim();
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        {
                            string t = rc.GetString() ?? "";
                            if (t.Length > 0)
                            {
                                fullThink.Append(t);
                                tokenCount++;
                                await SendEventAsync("think", t);
                            }
                        }
                        if (delta.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                        {
                            string t = cEl.GetString() ?? "";
                            if (t.Length > 0)
                            {
                                fullContent.Append(t);
                                tokenCount++;
                                await SendEventAsync("token", t);
                            }
                        }
                    }
                    catch { }
                }

                sw.Stop();
                double tokPerSec = sw.Elapsed.TotalSeconds > 0.15 ? Math.Round(tokenCount / sw.Elapsed.TotalSeconds, 1) : 0;
                if (tokPerSec > 0) _lastTokPerSec = tokPerSec;

                session.Messages.Add(new ChatMessageRecord { Role = "user", Content = userText, TimestampUtc = DateTime.UtcNow });
                session.Messages.Add(new ChatMessageRecord { Role = "assistant", Content = fullContent.ToString(), Reasoning = fullThink.ToString(), TimestampUtc = DateTime.UtcNow });
                session.UpdatedAtUtc = DateTime.UtcNow;
                ChatSessionService.SaveAll(sessions);

                await SendEventAsync("done", new { sessionId = session.Id, title = session.Title, tokens = tokenCount, tokPerSec });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await SendEventAsync("error", ex.Message);
            }
        });

        // ==================== AI ASSISTANT (RAG) ====================
        app.MapPost("/api/assistant/ask", async (AssistantAskRequest req, CancellationToken ct) =>
        {
            var metrics = await Svc.GetSystemMetricsAsync();
            HardwareProfile hw = BuildCurrentHardwareProfile(metrics);
            if (!string.IsNullOrWhiteSpace(req.SimulatedPlatform) && req.SimulatedPlatform.Equals("apple", StringComparison.OrdinalIgnoreCase))
            {
                var chip = HardwareAdvisor.MatchAppleSilicon(req.SimulatedChip ?? "Apple M4 Pro") ?? HardwareAdvisor.AppleSiliconWiki.First();
                hw = HardwareAdvisor.BuildSimulatedAppleProfile(chip, req.SimulatedRamGB > 0 ? req.SimulatedRamGB : chip.RamOptionsGB.First());
            }

            bool hfConn = !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken());
            var history = (req.History ?? []).Select(h => (h.Role, h.Content)).ToList();
            var reply = await Assistant.AskAsync(req.Message ?? "", history, hw, hfConn, null, null, ct);
            return Results.Ok(reply);
        });

        // ==================== STUDIO (PHOTO / VIDEO) & GALLERY ====================
        app.MapPost("/api/studio/photo", (StudioPhotoRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Введи промпт для генерации фото." });

            string jobId = StartBackgroundJob("photo", $"Фото ({req.Engine ?? "flux3"})", async (report, ct) =>
            {
                report("Запуск ComfyUI...", 5, 0);
                await Svc.SwitchAndStartAsync("comfyui");
                var (outPath, seed) = await Comfy.GeneratePhotoStageAsync(
                    req.Engine ?? "flux3",
                    req.Prompt,
                    req.Width > 0 ? req.Width : 1024,
                    req.Height > 0 ? req.Height : 1024,
                    anchorImagePath: null,
                    prevImagePath: null,
                    prefix: "hub_photo",
                    onProgress: p => report(p.StageLabel, p.Percent, 0),
                    ct: ct,
                    fixedSeed: req.Seed);
                GenerationMetadataService.Save(outPath, new GenerationMetadata
                {
                    Kind = "photo", Prompt = req.Prompt, Engine = req.Engine ?? "flux3",
                    Size = $"{req.Width}x{req.Height}", Seed = seed
                });
                report($"Готово! Seed {seed} → {Path.GetFileName(outPath)}", 100, 0);
            });
            return Results.Ok(new { jobId });
        });

        app.MapPost("/api/studio/video", (StudioVideoRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.BadRequest(new { error = "Введи промпт для генерации видео." });

            string jobId = StartBackgroundJob("video", $"Видео ({req.Engine ?? "wan14b_fp8"})", async (report, ct) =>
            {
                report("Запуск ComfyUI...", 5, 0);
                await Svc.SwitchAndStartAsync("comfyui");
                string framesDir = Path.Combine(HubConfig.StoriesRoot, "web_frames");
                Directory.CreateDirectory(framesDir);
                var (videoPath, _, _, seed) = await Comfy.GenerateVideoStageAsync(
                    req.Engine ?? "wan14b_fp8",
                    req.Prompt,
                    req.Width > 0 ? req.Width : 832,
                    req.Height > 0 ? req.Height : 480,
                    req.DurationSec > 0 ? req.DurationSec : 4,
                    startImagePath: null,
                    anchorImagePath: null,
                    refMode: "t2v",
                    prefix: "hub_video",
                    framesDir: framesDir,
                    onProgress: p => report(p.StageLabel, p.Percent, 0),
                    ct: ct,
                    fixedSeed: req.Seed,
                    negativePrompt: req.NegativePrompt);
                GenerationMetadataService.Save(videoPath, new GenerationMetadata
                {
                    Kind = "video", Prompt = req.Prompt, Engine = req.Engine ?? "wan14b_fp8",
                    Size = $"{req.Width}x{req.Height}", Seed = seed, DurationSec = req.DurationSec
                });
                report($"Готово! Seed {seed} → {Path.GetFileName(videoPath)}", 100, 0);
            });
            return Results.Ok(new { jobId });
        });

        app.MapGet("/api/gallery", (string? filter) =>
        {
            var items = ScanGalleryItems(filter);
            return Results.Ok(items);
        });

        app.MapPost("/api/gallery/favorite", (FavoriteToggleRequest req) =>
        {
            bool fav = ToggleFavorite(req.FullPath);
            return Results.Ok(new { fullPath = req.FullPath, isFavorite = fav });
        });

        app.MapGet("/api/gallery/file", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Results.NotFound();
            string full = Path.GetFullPath(path);
            string[] allowedRoots = [HubConfig.ComfyOutputDir, HubConfig.StoriesRoot, LocalVoiceService.VoiceOutDir];
            if (!allowedRoots.Any(r => !string.IsNullOrWhiteSpace(r) && full.StartsWith(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase)))
                return Results.Forbid();

            string ext = Path.GetExtension(full).ToLowerInvariant();
            string mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".wav" => "audio/wav",
                _ => "application/octet-stream"
            };
            return Results.File(full, mime, enableRangeProcessing: true);
        });

        // ==================== VOICE (TTS) ====================
        app.MapGet("/api/voice/voices", () => Results.Ok(new
        {
            status = LocalVoiceService.GetStatus(),
            voices = LocalVoiceService.GetAvailableTtsVoices()
        }));

        app.MapPost("/api/voice/tts", async (TtsRequest req, CancellationToken ct) =>
        {
            var voices = LocalVoiceService.GetAvailableTtsVoices();
            var chosen = voices.FirstOrDefault(v => v.Id == req.VoiceId) ?? voices.FirstOrDefault();
            if (chosen is null) return Results.BadRequest(new { error = "Нет доступных голосов TTS." });
            try
            {
                string wav = await Voice.SynthesizeToWavAsync(req.Text ?? "", chosen, req.Rate, ct);
                return Results.Ok(new { wavPath = wav, url = $"/api/gallery/file?path={Uri.EscapeDataString(wav)}" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // ==================== SETTINGS, MCP & PROFILE BACKUP ====================
        app.MapGet("/api/settings", () => Results.Ok(new
        {
            config = HubConfig.Current,
            mcpJson = HubConfig.BuildClaudeDesktopMcpJson(),
            hfConnected = !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken())
        }));

        app.MapPost("/api/settings", (HubConfigData updated) =>
        {
            var cur = HubConfig.Current;
            if (!string.IsNullOrWhiteSpace(updated.LlmRoot)) cur.LlmRoot = updated.LlmRoot.Trim();
            if (!string.IsNullOrWhiteSpace(updated.MediaRoot)) cur.MediaRoot = updated.MediaRoot.Trim();
            if (!string.IsNullOrWhiteSpace(updated.StoriesRoot)) cur.StoriesRoot = updated.StoriesRoot.Trim();
            cur.LlmCtxSize = updated.LlmCtxSize;
            cur.LlmTemperature = updated.LlmTemperature;
            cur.LlmPresencePenalty = updated.LlmPresencePenalty;
            cur.LlmDryMultiplier = updated.LlmDryMultiplier;
            cur.LlmReasoningBudget = updated.LlmReasoningBudget;
            cur.LlmIdleUnloadMinutes = updated.LlmIdleUnloadMinutes;
            HubConfig.Save();
            return Results.Ok(new { ok = true, config = cur });
        });

        app.MapPost("/api/settings/antiloop", () =>
        {
            var cur = HubConfig.Current;
            cur.LlmTemperature = 1.0;
            cur.LlmTopP = 0.95;
            cur.LlmTopK = 20;
            cur.LlmMinP = 0.0;
            cur.LlmPresencePenalty = 1.5;
            cur.LlmRepeatPenalty = 1.0;
            cur.LlmDryMultiplier = 0.5;
            cur.LlmReasoningBudget = 4096;
            HubConfig.Save();
            return Results.Ok(new { ok = true, config = cur });
        });

        app.MapPost("/api/hf/token", async (HfTokenRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token))
            {
                HubConfig.SaveHuggingFaceToken(null);
                return Results.Ok(new { connected = false });
            }
            var verify = await Hf.VerifyTokenAsync(req.Token.Trim(), ct);
            if (!verify.Connected)
                return Results.BadRequest(new { error = verify.Error ?? "Токен не прошёл проверку." });
            HubConfig.SaveHuggingFaceToken(req.Token.Trim());
            return Results.Ok(new { connected = true, username = verify.Username });
        });

        app.MapGet("/api/backup/export", () =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"BrazhnichenkoHub-Profile-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            ProfileBackupService.ExportProfileZip(tmp, AppUpdateService.CurrentVersion);
            byte[] bytes = File.ReadAllBytes(tmp);
            try { File.Delete(tmp); } catch { }
            return Results.File(bytes, "application/zip", Path.GetFileName(tmp));
        });

        app.MapPost("/api/system/open-folder", (OpenFolderRequest req) =>
        {
            string path = req.Target switch
            {
                "llm_models" => HubConfig.LlmModelsDir,
                "llm_runtime" => HubConfig.LlmRuntimeDir,
                "comfy_models" => HubConfig.ComfyModelsDir,
                "media_root" => HubConfig.MediaRoot,
                _ => HubConfig.ConfigDir
            };
            Directory.CreateDirectory(path);
            AiServiceManager.OpenExternal(path);
            return Results.Ok(new { ok = true, path });
        });
    }

    // ==================== HELPERS ====================

    private static string FavoritesFilePath => Path.Combine(HubConfig.ConfigDir, "gallery_favorites.json");

    private static HashSet<string> LoadFavorites()
    {
        try
        {
            if (File.Exists(FavoritesFilePath))
            {
                var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesFilePath, Encoding.UTF8));
                if (arr is not null) return new HashSet<string>(arr, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ToggleFavorite(string fullPath)
    {
        var favs = LoadFavorites();
        bool added = !favs.Remove(fullPath);
        if (added) favs.Add(fullPath);
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            File.WriteAllText(FavoritesFilePath, JsonSerializer.Serialize(favs.ToArray()), Encoding.UTF8);
        }
        catch { }
        return added;
    }

    private static HardwareProfile BuildCurrentHardwareProfile(SystemMetrics m)
    {
        string gpuName = m.Gpu?.Name ?? (OperatingSystem.IsMacOS() ? "Apple Silicon" : "No NVIDIA GPU");
        double vramGB = m.Gpu is not null ? Math.Round(m.Gpu.VramTotalMiB / 1024.0, 1) : 0;
        return HardwareAdvisor.BuildProfile(gpuName, vramGB, m.RamTotalGB, m.DiskFreeDGB);
    }

    private static List<object> EvaluateCatalogForProfile(HardwareProfile hw)
    {
        var list = new List<object>();
        foreach (var spec in ModelCatalog.All)
        {
            var fit = HardwareAdvisor.EvaluateModel(spec, hw);
            var st = ModelCatalog.CheckInstallState(spec);
            list.Add(new
            {
                spec.Id,
                spec.Category,
                spec.Name,
                spec.ShortTag,
                spec.SummaryRu,
                spec.WhyChooseRu,
                spec.TotalSizeGB,
                spec.IsGated,
                spec.MinVramGB,
                spec.IdealVramGB,
                fit = new
                {
                    level = fit.Level,
                    fit.BadgeText,
                    fit.ReasonRu,
                    fit.EstimatedSpeedHint,
                    placement = fit.Placement
                },
                installed = st.allInstalled,
                presentCount = st.presentCount,
                totalCount = st.totalCount,
                missingGB = Math.Round(st.missingGB, 2)
            });
        }
        return list;
    }

    private static List<object> ScanGalleryItems(string? filter)
    {
        var favs = LoadFavorites();
        var dirs = new[] { HubConfig.ComfyOutputDir, HubConfig.StoriesRoot }.Where(Directory.Exists);
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".mp4" };
        var files = new List<FileInfo>();

        foreach (string dir in dirs)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    if (exts.Contains(Path.GetExtension(f)))
                        files.Add(new FileInfo(f));
                }
            }
            catch { }
        }

        var items = new List<object>();
        foreach (var fi in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(160))
        {
            bool isVideo = fi.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
            bool isFav = favs.Contains(fi.FullName);
            if (filter == "photo" && isVideo) continue;
            if (filter == "video" && !isVideo) continue;
            if (filter == "fav" && !isFav) continue;

            var meta = GenerationMetadataService.TryRead(fi.FullName);
            items.Add(new
            {
                name = fi.Name,
                fullPath = fi.FullName,
                url = $"/api/gallery/file?path={Uri.EscapeDataString(fi.FullName)}",
                isVideo,
                isFavorite = isFav,
                sizeMB = Math.Round(fi.Length / 1048576.0, 2),
                modified = fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                meta
            });
        }
        return items;
    }

    private static string StartBackgroundJob(string kind, string title, Func<Action<string, double, double>, CancellationToken, Task> work)
    {
        string id = Guid.NewGuid().ToString("N")[..10];
        var cts = new CancellationTokenSource();
        JobCts[id] = cts;
        Jobs[id] = new ActiveJobState(id, kind, title, "running", "Подготовка...", 0, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                await work((stage, pct, speed) =>
                {
                    Jobs[id] = new ActiveJobState(id, kind, title, "running", stage, Math.Clamp(pct, 0, 100), Math.Round(speed, 1));
                }, cts.Token);
                Jobs[id] = Jobs[id] with { Status = "done", Percent = 100, Stage = "Готово!" };
            }
            catch (OperationCanceledException)
            {
                Jobs[id] = Jobs[id] with { Status = "cancelled", Stage = "Отменено" };
            }
            catch (Exception ex)
            {
                Jobs[id] = Jobs[id] with { Status = "error", Error = ex.Message, Stage = $"Ошибка: {ex.Message}" };
            }
            finally
            {
                JobCts.TryRemove(id, out _);
            }
        });
        return id;
    }

    private static async Task RunTelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var m = await Svc.GetSystemMetricsAsync();
                double vramPct = m.Gpu is not null && m.Gpu.VramTotalMiB > 0 ? m.Gpu.VramUsedMiB * 100.0 / m.Gpu.VramTotalMiB : 0;
                double tempC = m.Gpu?.TempC ?? 0;
                double ramPct = m.RamTotalGB > 0 ? m.RamUsedGB * 100.0 / m.RamTotalGB : 0;
                VramHistory.Push(vramPct);
                TempHistory.Push(tempC);
                RamHistory.Push(ramPct);
            }
            catch { }
            try { await Task.Delay(2500, ct); } catch { break; }
        }
    }

    private static string ResolveWebRoot()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(beside)) return beside;
        string cwd = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        if (Directory.Exists(cwd)) return cwd;
        return beside;
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsWindows()) return "Windows";
        return "Linux";
    }

    private static int ParsePortArg(string[] args, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out int p) && p is >= 1024 and <= 65535)
                return p;
        }
        return fallback;
    }

    private static async Task<bool> IsAlreadyRunningAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/api/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    private static int FindFreePort(int start)
    {
        for (int p = start; p < start + 50; p++)
            if (IsPortFree(p)) return p;
        return 0;
    }

    private static void SafeOpenBrowser(string url)
    {
        try { AiServiceManager.OpenExternal(url); } catch { }
    }
}

public sealed record SimulateHardwareRequest(string Platform, string? ChipName, double RamGB, double VramGB = 0);
public sealed record InstallModelRequest(string ModelId);
public sealed record HfDownloadRequest(string RepoId, string FilePath, string? TargetSubdir, long ExpectedSizeBytes = 0);
public sealed record CancelJobRequest(string JobId);
public sealed record ServiceActionRequest(string Id, string? ModelFile = null);
public sealed record RuntimeInstallRequest(string Runtime);
public sealed record CreateSessionRequest(string? PersonaId, double? Temperature);
public sealed record UpdateSessionRequest(string? Title, string? PersonaId, double? Temperature, bool? ClearMessages);
public sealed record ChatStreamRequest(string? SessionId, string? Message, string? PersonaId, double? Temperature, string? ModelFile, string? ImageBase64);
public sealed record AssistantHistoryItem(string Role, string Content);
public sealed record AssistantAskRequest(string? Message, List<AssistantHistoryItem>? History, string? SimulatedPlatform = null, string? SimulatedChip = null, double SimulatedRamGB = 0);
public sealed record StudioPhotoRequest(string? Engine, string Prompt, int Width = 1024, int Height = 1024, long? Seed = null);
public sealed record StudioVideoRequest(string? Engine, string Prompt, string? NegativePrompt = null, int Width = 832, int Height = 480, int DurationSec = 4, long? Seed = null);
public sealed record FavoriteToggleRequest(string FullPath);
public sealed record TtsRequest(string Text, string? VoiceId, double Rate = 0);
public sealed record HfTokenRequest(string? Token);
public sealed record OpenFolderRequest(string Target);
