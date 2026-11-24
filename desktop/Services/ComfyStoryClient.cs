using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAIHub.Services;

public sealed record GenerationProgressInfo(
    string Title,
    string StageLabel,
    double Percent,
    int CurrentStep,
    int TotalSteps,
    double? SecondsPerStep,
    double ElapsedSeconds,
    double? EtaSeconds,
    bool IsIndeterminate = false);

public sealed class PhotoStoryStage
{
    public int Index { get; set; }
    public string Prompt { get; set; } = "";
    public bool UseAnchorRef { get; set; } = true;
    public bool UsePrevRef { get; set; } = false;
    public string? OutputImagePath { get; set; }
    public long LastSeed { get; set; }
}

public sealed class VideoStoryStage
{
    public int Index { get; set; }
    public string Prompt { get; set; } = "";
    public string BridgePrompt { get; set; } = "";
    public string RefMode { get; set; } = "svi_pro";
    public string? CustomStartImagePath { get; set; }
    public string? OutputVideoPath { get; set; }
    public string? PreviewImagePath { get; set; }
    public string? LastFramePath { get; set; }
    public long LastSeed { get; set; }
}

public sealed class VideoStoryProject
{
    public bool UseStarterFrame { get; set; } = false;
    public string StarterEngine { get; set; } = "flux3";
    public string StarterPrompt { get; set; } = "Full-body cinematic 35mm photograph of a young woman with copper-red hair wearing a beige trench coat standing on a sunlit European street, warm golden hour light, sharp focus";
    public string? StarterImagePath { get; set; }
    public string NegativePrompt { get; set; } = "";
    public List<VideoStoryStage> Stages { get; set; } = [];
}

public sealed class ComfyStoryClient
{
    private static string BaseUrl => $"http://127.0.0.1:{HubConfig.Current.ComfyPort}";
    private static string WsUrlBase => $"ws://127.0.0.1:{HubConfig.Current.ComfyPort}";
    private static string ComfyOutputDir => HubConfig.ComfyOutputDir;
    private static string VenvPython => HubConfig.VenvPythonPath;
    private static string MediaHelperPy => HubConfig.MediaHelperPyPath;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string StoriesRoot => HubConfig.StoriesRoot;

    public async Task<string> UploadImageAsync(string localImagePath)
    {
        if (!File.Exists(localImagePath))
            throw new FileNotFoundException($"Изображение-референс не найдено: {localImagePath}");

        using var form = new MultipartFormDataContent();
        byte[] bytes = await File.ReadAllBytesAsync(localImagePath);
        var fileContent = new ByteArrayContent(bytes);
        string ext = Path.GetExtension(localImagePath).ToLowerInvariant();
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : "image/png");
        string uploadName = $"story_{Guid.NewGuid():N}{ext}";
        form.Add(fileContent, "image", uploadName);
        form.Add(new StringContent("true"), "overwrite");

        using var resp = await Http.PostAsync($"{BaseUrl}/upload/image", form);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("name").GetString() ?? uploadName;
    }

    private sealed record NodeStageSpec(string Label, double StartPct, double EndPct, int StepOffset = -1);

    public async Task InterruptAsync()
    {
        try
        {
            using var resp = await Http.PostAsync($"{BaseUrl}/interrupt", null);
        }
        catch { }
    }

    public static List<string> ListCustomLoras()
    {
        string dir = Path.Combine(HubConfig.ComfyModelsDir, "loras");
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f))
                .Where(rel =>
                {
                    string lower = rel.ToLowerInvariant();
                    return !lower.Contains("lightx2v_4steps_lora") && !lower.Contains("svi_v2_pro");
                })
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    public async Task<(string outputPath, long seed)> GeneratePhotoStageAsync(
        string engine,
        string prompt,
        int width,
        int height,
        string? anchorImagePath,
        string? prevImagePath,
        string prefix,
        Action<string>? onStatus = null,
        Action<GenerationProgressInfo>? onProgress = null,
        CancellationToken ct = default,
        long? fixedSeed = null,
        string? extraLora = null,
        double extraLoraStrength = 0.75)
    {
        long seed = fixedSeed is > 0 ? fixedSeed.Value : Random.Shared.NextInt64(1, 9_999_999_999_999);
        var refs = new List<string>();
        string label = engine switch
        {
            "qwen21" => "Qwen-Image 2.1",
            "zimage" => "Z-Image Turbo",
            _ => "FLUX 3 Klein"
        };
        string title = $"Фото ({label} • {width}×{height})";

        if (!string.IsNullOrWhiteSpace(anchorImagePath) && File.Exists(anchorImagePath))
        {
            onStatus?.Invoke("Загрузка якоря персонажа в ComfyUI...");
            onProgress?.Invoke(new GenerationProgressInfo(title, "Загрузка якоря персонажа...", 3, 0, 0, null, 0, null, true));
            refs.Add(await UploadImageAsync(anchorImagePath));
        }
        if (!string.IsNullOrWhiteSpace(prevImagePath) && File.Exists(prevImagePath) && prevImagePath != anchorImagePath)
        {
            onStatus?.Invoke("Загрузка предыдущего кадра в ComfyUI...");
            onProgress?.Invoke(new GenerationProgressInfo(title, "Загрузка предыдущего кадра...", 5, 0, 0, null, 0, null, true));
            refs.Add(await UploadImageAsync(prevImagePath));
        }

        JsonObject graph = engine switch
        {
            "qwen21" => BuildQwen21PhotoGraph(prompt, width, height, seed, refs, prefix, extraLora, extraLoraStrength),
            "zimage" => BuildZImagePhotoGraph(prompt, width, height, seed, prefix, extraLora, extraLoraStrength),
            _ => BuildFlux3PhotoGraph(prompt, width, height, seed, refs, prefix, extraLora, extraLoraStrength)
        };

        var (stages, totalSteps) = GetPhotoStageMap(engine);
        onStatus?.Invoke($"Генерация кадра ({label})...");
        string file = await QueueAndWaitForSingleAssetAsync(graph, title, stages, totalSteps, 1.5, onProgress, ct);
        return (file, seed);
    }

    public async Task<(string videoPath, string previewPath, string lastFramePath, long seed)> GenerateVideoStageAsync(
        string engine,
        string prompt,
        int width,
        int height,
        int durationSec,
        string? startImagePath,
        string? anchorImagePath,
        string refMode,
        string prefix,
        string framesDir,
        Action<string>? onStatus = null,
        Action<GenerationProgressInfo>? onProgress = null,
        CancellationToken ct = default,
        long? fixedSeed = null,
        string? customBridgePrompt = null,
        string? prevVideoPath = null,
        string? negativePrompt = null,
        string? extraLoraHigh = null,
        double extraLoraHighStrength = 0.75,
        string? extraLoraLow = null,
        double extraLoraLowStrength = 0.75)
    {
        if (refMode == "prev_and_anchor") refMode = "svi_pro";

        long seed = fixedSeed is > 0 ? fixedSeed.Value : Random.Shared.NextInt64(1, 9_999_999_999_999);
        bool hasStart = !string.IsNullOrWhiteSpace(startImagePath) && File.Exists(startImagePath);
        bool hasAnchor = !string.IsNullOrWhiteSpace(anchorImagePath) && File.Exists(anchorImagePath) &&
                         !string.Equals(anchorImagePath, startImagePath, StringComparison.OrdinalIgnoreCase);

        // Optional Step A: FLUX 3 Bridge synthesizes a crisp start frame combining Step 0 Anchor + Previous Last Frame
        string? effectiveStartPath = startImagePath;
        if (refMode == "flux3_bridge" && hasStart && hasAnchor)
        {
            onStatus?.Invoke("Мост FLUX 3: синтез стартового фото-кадра (Шаг 0 + последний кадр)...");
            string bridgePrompt = !string.IsNullOrWhiteSpace(customBridgePrompt)
                ? customBridgePrompt.Trim()
                : $"Preserve the exact subject appearance, face, outfit, and style from the first reference image while matching the pose, framing, lighting, and environment of the second reference image. {prompt}";
            var (bridgePath, _) = await GeneratePhotoStageAsync(
                "flux3",
                bridgePrompt,
                width,
                height,
                anchorImagePath,
                startImagePath,
                $"{prefix}_bridge",
                onStatus,
                onProgress,
                ct);
            effectiveStartPath = bridgePath;
            hasStart = File.Exists(effectiveStartPath);
            hasAnchor = false; // Bridge already fused both images into one clean start frame
        }

        string? uploadedStart = null;
        string? uploadedAnchor = null;

        string modeLabel = !hasStart
            ? "T2V"
            : (refMode == "flux3_bridge") ? "FLUX3-мост → I2V"
            : (hasAnchor && refMode == "svi_pro" && engine != "ltx25") ? "SVI 2.0 Pro (Якорь+5к)"
            : (hasAnchor && refMode == "prev_to_anchor") ? "I2V Цикл→Шаг0"
            : "I2V";

        string engineLabel = engine switch
        {
            "ltx25" => "LTX-2.5 Audio",
            "wan14b_fp8" => "Wan 2.2 14B FP8 (DynamicVRAM)",
            "wan14b_q6" => "Wan 2.2 14B Q6_K",
            _ => "Wan 2.2 14B Q4_K_M"
        };
        string title = $"Видео ({engineLabel} • {modeLabel} • {width}×{height}, {durationSec}с)";

        if (hasStart)
        {
            onStatus?.Invoke("Загрузка стартового кадра в ComfyUI...");
            onProgress?.Invoke(new GenerationProgressInfo(title, "Загрузка стартового кадра...", 2, 0, 0, null, 0, null, true));
            uploadedStart = await UploadImageAsync(effectiveStartPath!);
        }
        if (hasAnchor && refMode is "svi_pro" or "prev_to_anchor")
        {
            onStatus?.Invoke("Загрузка референсного кадра Шага 0 в ComfyUI...");
            onProgress?.Invoke(new GenerationProgressInfo(title, "Загрузка якоря Шага 0...", 3, 0, 0, null, 0, null, true));
            uploadedAnchor = await UploadImageAsync(anchorImagePath!);
        }

        string variant = engine switch
        {
            "wan14b_fp8" => "FP8",
            "wan14b_q6" => "Q6_K",
            _ => "Q4_K_M"
        };
        string? sviPrevVideo = (refMode == "svi_pro" && !string.IsNullOrWhiteSpace(prevVideoPath) && File.Exists(prevVideoPath))
            ? prevVideoPath
            : null;
        JsonObject graph = engine == "ltx25"
            ? BuildLtx25VideoGraph(prompt, width, height, durationSec, seed, uploadedStart, uploadedAnchor, refMode, prefix, negativePrompt, extraLoraHigh, extraLoraHighStrength)
            : BuildWan14bVideoGraph(prompt, width, height, durationSec, seed, uploadedStart, uploadedAnchor, refMode, prefix, variant, sviPrevVideo, negativePrompt, extraLoraHigh, extraLoraHighStrength, extraLoraLow, extraLoraLowStrength);

        var (stages, totalSteps) = GetVideoStageMap(engine);
        onStatus?.Invoke($"Генерация видео ({engineLabel} • {modeLabel})...");
        string videoPath = await QueueAndWaitForSingleAssetAsync(graph, title, stages, totalSteps, 5.0, onProgress, ct);

        Directory.CreateDirectory(framesDir);
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        string previewPath = Path.Combine(framesDir, $"{stem}_mid.png");
        string lastFramePath = Path.Combine(framesDir, $"{stem}_last.png");

        onStatus?.Invoke("Извлечение превью и последнего кадра для следующего этапа...");
        onProgress?.Invoke(new GenerationProgressInfo(title, "Извлечение превью и последнего кадра...", 99, totalSteps, totalSteps, null, 0, 1, false));
        await ExtractVideoFrameAsync(videoPath, previewPath, "mid");
        await ExtractVideoFrameAsync(videoPath, lastFramePath, "last");
        return (videoPath, previewPath, lastFramePath, seed);
    }

    public async Task<string> UpscaleImage4xAsync(
        string localImagePath,
        string prefix = "studio/upscale_4x",
        Action<string>? onStatus = null,
        Action<GenerationProgressInfo>? onProgress = null,
        CancellationToken ct = default)
    {
        const string title = "Апскейл ×4 (4x-UltraSharp)";
        onStatus?.Invoke("Загрузка исходного фото в ComfyUI...");
        onProgress?.Invoke(new GenerationProgressInfo(title, "Загрузка исходного фото...", 10, 0, 1, null, 0, null, true));
        string uploaded = await UploadImageAsync(localImagePath);

        var graph = new JsonObject
        {
            ["1"] = Node("LoadImage", new JsonObject { ["image"] = uploaded }),
            ["2"] = Node("UpscaleModelLoader", new JsonObject { ["model_name"] = "4x-UltraSharp.pth" }),
            ["3"] = Node("ImageUpscaleWithModel", new JsonObject { ["upscale_model"] = Ref("2"), ["image"] = Ref("1") }),
            ["4"] = Node("SaveImage", new JsonObject { ["images"] = Ref("3"), ["filename_prefix"] = prefix }),
        };

        var stages = new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка изображения", 15, 25),
            ["2"] = new("Загрузка 4x-UltraSharp", 25, 40),
            ["3"] = new("Увеличение резкости ×4 на GPU...", 40, 90, StepOffset: 0),
            ["4"] = new("Сохранение 4K PNG", 90, 99),
        };

        onStatus?.Invoke("Увеличение резкости ×4 (4x-UltraSharp)...");
        return await QueueAndWaitForSingleAssetAsync(graph, title, stages, 1, 1.0, onProgress, ct);
    }

    public async Task ExtractVideoFrameAsync(string videoPath, string outPngPath, string which = "last")
    {
        await RunMediaHelperAsync(["extract-frame", videoPath, outPngPath, which]);
    }

    public async Task ConcatVideosAsync(IEnumerable<string> clipPaths, string outMp4Path, IEnumerable<int>? trimFlags = null)
    {
        var args = new List<string> { "concat" };
        if (trimFlags is not null)
        {
            string joined = string.Join(",", trimFlags);
            if (!string.IsNullOrEmpty(joined))
                args.Add($"--trim={joined}");
        }
        args.Add(outMp4Path);
        args.AddRange(clipPaths);
        await RunMediaHelperAsync(args);
    }

    private static async Task RunMediaHelperAsync(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = VenvPython,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(MediaHelperPy);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить story_media.py");
        string err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Ошибка обработки видео: {err.Trim()}");
    }

    private static (Dictionary<string, NodeStageSpec> map, int totalSteps) GetPhotoStageMap(string engine) => engine switch
    {
        "qwen21" => (new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка Qwen-Image 2.1", 2, 8),
            ["2"] = new("Загрузка Qwen3-VL 8B", 4, 10),
            ["3"] = new("Загрузка VAE", 6, 12),
            ["5"] = new("Кодирование промпта и референсов", 10, 18),
            ["6"] = new("Подготовка кэша Qwen 2.1", 16, 18),
            ["11"] = new("Генерация кадра (Qwen-Image 2.1)", 18, 92, StepOffset: 0),
            ["12"] = new("Декодирование VAE", 92, 97),
            ["13"] = new("Сохранение изображения", 97, 99),
        }, 8),
        "zimage" => (new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка Z-Image Turbo", 2, 8),
            ["2"] = new("Загрузка текстового энкодера", 4, 10),
            ["4"] = new("Кодирование промпта", 10, 16),
            ["7"] = new("Генерация кадра (Z-Image Turbo)", 16, 92, StepOffset: 0),
            ["8"] = new("Декодирование VAE", 92, 97),
            ["9"] = new("Сохранение изображения", 97, 99),
        }, 8),
        _ => (new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка FLUX 3 Klein", 2, 8),
            ["2"] = new("Загрузка Qwen3-VL 4B", 4, 10),
            ["4"] = new("Кодирование промпта", 8, 13),
            ["7"] = new("Обработка референсов", 13, 16),
            ["8"] = new("Генерация кадра (FLUX 3 Klein)", 16, 92, StepOffset: 0),
            ["9"] = new("Декодирование VAE", 92, 97),
            ["10"] = new("Сохранение изображения", 97, 99),
        }, 16),
    };

    private static (Dictionary<string, NodeStageSpec> map, int totalSteps) GetVideoStageMap(string engine) => engine == "ltx25"
        ? (new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка LTX-2.5 22B", 2, 8),
            ["4"] = new("Загрузка Gemma 4 12B", 5, 11),
            ["6"] = new("Кодирование промпта (Gemma 4)", 11, 17),
            ["7"] = new("Кодирование негативного промпта", 14, 17),
            ["32"] = new("Подготовка стартового кадра", 16, 18),
            ["16"] = new("Этап 1: Генерация видео + звука", 18, 62, StepOffset: 0),
            ["18"] = new("Латентный апскейл ×2", 62, 68),
            ["22"] = new("Этап 2: HD-детализация + звук", 68, 90, StepOffset: 8),
            ["24"] = new("Декодирование видео (VAE)", 90, 95),
            ["25"] = new("Декодирование аудио (Audio VAE)", 95, 97),
            ["26"] = new("Сборка видео + звука", 97, 98),
            ["27"] = new("Сохранение MP4", 98, 99),
        }, 11)
        : (new Dictionary<string, NodeStageSpec>
        {
            ["1"] = new("Загрузка High Noise 14B", 2, 6),
            ["2"] = new("Загрузка Low Noise 14B", 4, 8),
            ["3"] = new("Подключение Lightning LoRA (High)", 6, 9),
            ["4"] = new("Подключение Lightning LoRA (Low)", 7, 10),
            ["20"] = new("Подключение SVI 2.0 Pro LoRA (High)", 8, 10),
            ["21"] = new("Подключение SVI 2.0 Pro LoRA (Low)", 9, 11),
            ["7"] = new("Загрузка текстового энкодера UMT5", 10, 12),
            ["9"] = new("Кодирование промпта (UMT5)", 11, 15),
            ["10"] = new("Кодирование негативного промпта", 13, 16),
            ["11"] = new("Подготовка латентов / стартового кадра", 15, 18),
            ["12"] = new("Эксперт 1: High Noise (движение)", 18, 54, StepOffset: 0),
            ["13"] = new("Эксперт 2: Low Noise (детализация)", 54, 90, StepOffset: 2),
            ["14"] = new("Декодирование кадров (VAE)", 90, 96),
            ["22"] = new("Бесшовная обрезка якорного кадра (SVI)", 96, 97),
            ["15"] = new("Сборка видеопотока", 97, 98),
            ["16"] = new("Сохранение MP4", 98, 99),
        }, 4);

    private async Task<string> QueueAndWaitForSingleAssetAsync(
        JsonObject graph,
        string title,
        Dictionary<string, NodeStageSpec> stageMap,
        int totalGlobalSteps,
        double vaeBufferSec,
        Action<GenerationProgressInfo>? onProgress,
        CancellationToken ct)
    {
        string clientId = Guid.NewGuid().ToString("N");
        using var ws = new ClientWebSocket();
        bool wsConnected = false;
        try
        {
            await ws.ConnectAsync(new Uri($"{WsUrlBase}/ws?clientId={clientId}"), ct);
            wsConnected = true;
        }
        catch { }

        var payload = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = clientId
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var postResp = await Http.PostAsync($"{BaseUrl}/prompt", content, ct);
        string postText = await postResp.Content.ReadAsStringAsync(ct);
        if (!postResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI отклонил запрос: {postText}");

        using var postDoc = JsonDocument.Parse(postText);
        string promptId = postDoc.RootElement.GetProperty("prompt_id").GetString()
            ?? throw new InvalidOperationException("ComfyUI не вернул prompt_id");

        var sw = Stopwatch.StartNew();
        onProgress?.Invoke(new GenerationProgressInfo(title, "В очереди ComfyUI...", 2, 0, totalGlobalSteps, null, 0, null, true));

        if (wsConnected)
        {
            string? activeNode = null;
            double activeNodeStartSec = 0;
            string? lastStepNode = null;
            int lastStepVal = 0;
            double lastStepSec = 0;
            double? avgSecPerStep = null;
            int pureStepSamples = 0;
            int currentGlobalStep = 0;
            double currentPct = 2;
            string currentStage = "Запуск графа в ComfyUI...";

            var recvBuf = new byte[16384];
            using var ms = new MemoryStream();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(25));

            try
            {
                while (ws.State == WebSocketState.Open && !timeoutCts.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(recvBuf, timeoutCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType == WebSocketMessageType.Text)
                            ms.Write(recvBuf, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0) continue;

                    using var msgDoc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                    var root = msgDoc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    string msgType = typeEl.GetString() ?? "";
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

                    string? msgPromptId = data.TryGetProperty("prompt_id", out var pidEl) && pidEl.ValueKind == JsonValueKind.String
                        ? pidEl.GetString() : null;
                    if (msgPromptId is not null && msgPromptId != promptId) continue;

                    double nowSec = sw.Elapsed.TotalSeconds;

                    if (msgType == "executing")
                    {
                        string? nodeId = data.TryGetProperty("node", out var nodeEl) && nodeEl.ValueKind == JsonValueKind.String
                            ? nodeEl.GetString() : null;

                        if (string.IsNullOrEmpty(nodeId))
                        {
                            // Execution finished!
                            onProgress?.Invoke(new GenerationProgressInfo(title, "Завершение и сохранение результата...", 99, totalGlobalSteps, totalGlobalSteps, avgSecPerStep, nowSec, 0.5, false));
                            break;
                        }

                        activeNode = nodeId;
                        activeNodeStartSec = nowSec;
                        lastStepNode = null;
                        lastStepVal = 0;

                        if (stageMap.TryGetValue(nodeId, out var spec))
                        {
                            currentStage = spec.Label;
                            currentPct = Math.Max(currentPct, spec.StartPct);
                            double? eta = avgSecPerStep.HasValue && totalGlobalSteps > 0
                                ? Math.Max(0, (totalGlobalSteps - currentGlobalStep) * avgSecPerStep.Value + (currentPct < 90 ? vaeBufferSec : 1.0))
                                : null;
                            onProgress?.Invoke(new GenerationProgressInfo(title, currentStage, currentPct, currentGlobalStep, totalGlobalSteps, avgSecPerStep, nowSec, eta, false));
                        }
                    }
                    else if (msgType == "progress")
                    {
                        int val = data.TryGetProperty("value", out var vEl) && vEl.TryGetInt32(out int vi) ? vi : 0;
                        int max = data.TryGetProperty("max", out var mEl) && mEl.TryGetInt32(out int mi) ? mi : 0;
                        string? nodeId = data.TryGetProperty("node", out var nEl) && nEl.ValueKind == JsonValueKind.String
                            ? nEl.GetString() : activeNode;

                        if (max <= 0) continue;

                        if (nodeId is not null && stageMap.TryGetValue(nodeId, out var spec))
                        {
                            currentStage = spec.Label;
                            double frac = Math.Clamp((double)val / max, 0, 1);
                            currentPct = spec.StartPct + (spec.EndPct - spec.StartPct) * frac;

                            if (spec.StepOffset >= 0)
                            {
                                currentGlobalStep = Math.Min(totalGlobalSteps, spec.StepOffset + val);

                                if (lastStepNode == nodeId && val > lastStepVal)
                                {
                                    double stepDur = (nowSec - lastStepSec) / (val - lastStepVal);
                                    if (stepDur > 0.05 && stepDur < 600)
                                    {
                                        avgSecPerStep = pureStepSamples == 0
                                            ? stepDur
                                            : (avgSecPerStep!.Value * pureStepSamples + stepDur) / (pureStepSamples + 1);
                                        pureStepSamples++;
                                    }
                                }
                                else if (val == 1 && activeNodeStartSec > 0 && nowSec > activeNodeStartSec)
                                {
                                    double firstDur = nowSec - activeNodeStartSec;
                                    if (avgSecPerStep is null && firstDur > 0.1 && firstDur < 600)
                                        avgSecPerStep = firstDur;
                                }

                                lastStepNode = nodeId;
                                lastStepVal = val;
                                lastStepSec = nowSec;
                            }
                        }
                        else
                        {
                            double frac = Math.Clamp((double)val / max, 0, 1);
                            currentPct = Math.Max(currentPct, 15 + frac * 75);
                        }

                        double? eta = avgSecPerStep.HasValue && totalGlobalSteps > 0
                            ? Math.Max(0, (totalGlobalSteps - currentGlobalStep) * avgSecPerStep.Value + (currentPct < 90 ? vaeBufferSec : 1.0))
                            : null;

                        onProgress?.Invoke(new GenerationProgressInfo(
                            title,
                            currentStage,
                            Math.Clamp(currentPct, 0, 99),
                            currentGlobalStep,
                            totalGlobalSteps,
                            avgSecPerStep,
                            nowSec,
                            eta,
                            false));
                    }
                    else if (msgType == "execution_interrupted")
                    {
                        throw new OperationCanceledException("Генерация остановлена пользователем.");
                    }
                    else if (msgType == "execution_error")
                    {
                        string errMsg = data.TryGetProperty("exception_message", out var em) ? em.GetString() ?? data.GetRawText() : data.GetRawText();
                        throw new InvalidOperationException($"Ошибка генерации ComfyUI: {errMsg}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await InterruptAsync();
                throw new OperationCanceledException("Генерация отменена пользователем.");
            }
            catch (WebSocketException)
            {
                // Fall back to HTTP history polling below
            }
        }

        // Fetch completed output from /history/{promptId} (also acts as fallback if WS disconnected)
        var deadline = DateTime.UtcNow.AddMinutes(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var histResp = await Http.GetAsync($"{BaseUrl}/history/{promptId}", ct);
            if (histResp.IsSuccessStatusCode)
            {
                string histText = await histResp.Content.ReadAsStringAsync(ct);
                using var histDoc = JsonDocument.Parse(histText);
                if (histDoc.RootElement.TryGetProperty(promptId, out var entry))
                {
                    string statusStr = entry.GetProperty("status").GetProperty("status_str").GetString() ?? "";
                    if (statusStr == "error")
                    {
                        string msgs = entry.GetProperty("status").GetProperty("messages").GetRawText();
                        throw new InvalidOperationException($"Ошибка генерации ComfyUI: {msgs}");
                    }
                    if (statusStr != "success")
                    {
                        string msgs = entry.GetProperty("status").GetProperty("messages").GetRawText();
                        if (msgs.Contains("execution_interrupted", StringComparison.OrdinalIgnoreCase))
                            throw new OperationCanceledException("Генерация остановлена пользователем.");
                        throw new InvalidOperationException($"Статус генерации ComfyUI: {statusStr} ({msgs})");
                    }

                    if (entry.TryGetProperty("outputs", out var outputs))
                    {
                        foreach (var nodeOut in outputs.EnumerateObject())
                        {
                            foreach (var group in nodeOut.Value.EnumerateObject())
                            {
                                if (group.Value.ValueKind != JsonValueKind.Array) continue;
                                foreach (var asset in group.Value.EnumerateArray())
                                {
                                    if (asset.ValueKind != JsonValueKind.Object) continue;
                                    if (asset.TryGetProperty("type", out var t) && t.GetString() == "output" &&
                                        asset.TryGetProperty("filename", out var fn))
                                    {
                                        string sub = asset.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
                                        string full = Path.Combine(ComfyOutputDir, sub, fn.GetString()!);
                                        if (File.Exists(full))
                                        {
                                            onProgress?.Invoke(new GenerationProgressInfo(title, "Готово!", 100, totalGlobalSteps, totalGlobalSteps, null, sw.Elapsed.TotalSeconds, 0, false));
                                            return full;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    throw new InvalidOperationException("ComfyUI завершил задачу, но файл результата не найден.");
                }
            }
            await Task.Delay(1200, ct);
        }
        throw new TimeoutException("Истекло время ожидания генерации в ComfyUI.");
    }

    // ---- Graph builders ----

    private static JsonObject Node(string classType, JsonObject inputs) =>
        new() { ["class_type"] = classType, ["inputs"] = inputs };

    private static JsonArray Ref(string nodeId, int slot = 0) => [nodeId, slot];

    private static JsonObject BuildFlux3PhotoGraph(string prompt, int width, int height, long seed, List<string> uploadedRefs, string prefix, string? extraLora = null, double extraLoraStrength = 0.75)
    {
        var g = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject { ["unet_name"] = "flux3-image-klein-int8-convrot.safetensors", ["weight_dtype"] = "default" }),
            ["2"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = "qwen3vl_4b_int8_convrot.safetensors", ["type"] = "flux3", ["device"] = "default" }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = "flux2-vae.safetensors" }),
            ["4"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("2"), ["text"] = prompt }),
            ["5"] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Ref("4") }),
        };

        JsonArray modelRef = Ref("1");
        if (!string.IsNullOrWhiteSpace(extraLora))
        {
            g["90"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = modelRef, ["lora_name"] = extraLora, ["strength_model"] = Math.Clamp(extraLoraStrength, -2.0, 2.0) });
            modelRef = Ref("90");
        }

        JsonArray posRef = Ref("4");
        JsonArray negRef = Ref("5");
        g["6"] = Node("EmptyFlux2LatentImage", new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 });
        JsonArray latRef = Ref("6");

        if (uploadedRefs.Count > 0)
        {
            var rmlInputs = new JsonObject
            {
                ["positive"] = Ref("4"),
                ["negative"] = Ref("5"),
                ["vae"] = Ref("3"),
                ["resolution"] = "resize",
                ["resolution.megapixels"] = Math.Clamp(Math.Round((width * height) / 1_000_000.0, 2), 0.25, 2.0)
            };
            for (int i = 0; i < uploadedRefs.Count; i++)
            {
                string loadId = (20 + i).ToString();
                g[loadId] = Node("LoadImage", new JsonObject { ["image"] = uploadedRefs[i] });
                rmlInputs[$"images.image{i}"] = Ref(loadId);
            }
            g["7"] = Node("ReferenceMultiLatent", rmlInputs);
            posRef = Ref("7", 0);
            negRef = Ref("7", 1);
        }

        g["8"] = Node("KSampler", new JsonObject
        {
            ["model"] = modelRef,
            ["positive"] = posRef,
            ["negative"] = negRef,
            ["latent_image"] = latRef,
            ["seed"] = seed,
            ["steps"] = 16,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0
        });
        g["9"] = Node("VAEDecode", new JsonObject { ["samples"] = Ref("8"), ["vae"] = Ref("3") });
        g["10"] = Node("SaveImage", new JsonObject { ["images"] = Ref("9"), ["filename_prefix"] = prefix });
        return g;
    }

    private static JsonObject BuildQwen21PhotoGraph(string prompt, int width, int height, long seed, List<string> uploadedRefs, string prefix, string? extraLora = null, double extraLoraStrength = 0.75)
    {
        int res = Math.Clamp((int)Math.Round(Math.Sqrt(width * (double)height)), 512, 1536);
        var encInputs = new JsonObject
        {
            ["clip"] = Ref("2"),
            ["vae"] = Ref("3"),
            ["prompt"] = prompt,
            ["negative_prompt"] = "",
            ["resolution"] = res
        };
        var g = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject { ["unet_name"] = "qwen_image_2.1_turbo_int8_convrot.safetensors", ["weight_dtype"] = "default" }),
            ["2"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = "qwen3vl_8b_int8_convrot.safetensors", ["type"] = "qwen_image", ["device"] = "default" }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = "qwen_image_2.1_vae_bf16.safetensors" }),
        };
        JsonArray modelRef = Ref("1");
        if (!string.IsNullOrWhiteSpace(extraLora))
        {
            g["90"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = modelRef, ["lora_name"] = extraLora, ["strength_model"] = Math.Clamp(extraLoraStrength, -2.0, 2.0) });
            modelRef = Ref("90");
        }
        for (int i = 0; i < uploadedRefs.Count; i++)
        {
            string loadId = (20 + i).ToString();
            g[loadId] = Node("LoadImage", new JsonObject { ["image"] = uploadedRefs[i] });
            encInputs[$"images.image_{i + 1}"] = Ref(loadId);
        }
        g["5"] = Node("TextEncodeQwenImage21", encInputs);
        g["6"] = Node("QwenImage21Cache", new JsonObject { ["model"] = modelRef, ["device"] = "auto", ["dtype"] = "default" });
        g["7"] = Node("CFGGuider", new JsonObject { ["model"] = Ref("6"), ["positive"] = Ref("5", 0), ["negative"] = Ref("5", 1), ["cfg"] = 1.0 });
        g["8"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler" });
        g["9"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed });
        g["10"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = "1.0, 0.978453, 0.95418, 0.926626, 0.89508, 0.845148, 0.704534, 0.414568, 0" });
        g["11"] = Node("SamplerCustomAdvanced", new JsonObject
        {
            ["noise"] = Ref("9"),
            ["guider"] = Ref("7"),
            ["sampler"] = Ref("8"),
            ["sigmas"] = Ref("10"),
            ["latent_image"] = Ref("5", 2)
        });
        g["12"] = Node("VAEDecode", new JsonObject { ["samples"] = Ref("11"), ["vae"] = Ref("3") });
        g["13"] = Node("SaveImage", new JsonObject { ["images"] = Ref("12"), ["filename_prefix"] = prefix });
        return g;
    }

    private static JsonObject BuildZImagePhotoGraph(string prompt, int width, int height, long seed, string prefix, string? extraLora = null, double extraLoraStrength = 0.75)
    {
        var g = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject { ["unet_name"] = "z_image_turbo_bf16.safetensors", ["weight_dtype"] = "default" }),
            ["2"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = "qwen_3_4b.safetensors", ["type"] = "lumina2", ["device"] = "default" }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = "ae.safetensors" }),
            ["4"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("2"), ["text"] = prompt }),
            ["5"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("2"), ["text"] = "" }),
            ["6"] = Node("EmptySD3LatentImage", new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }),
        };
        JsonArray modelRef = Ref("1");
        if (!string.IsNullOrWhiteSpace(extraLora))
        {
            g["90"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = modelRef, ["lora_name"] = extraLora, ["strength_model"] = Math.Clamp(extraLoraStrength, -2.0, 2.0) });
            modelRef = Ref("90");
        }
        g["7"] = Node("KSampler", new JsonObject
        {
            ["model"] = modelRef,
            ["positive"] = Ref("4"),
            ["negative"] = Ref("5"),
            ["latent_image"] = Ref("6"),
            ["seed"] = seed,
            ["steps"] = 8,
            ["cfg"] = 1.0,
            ["sampler_name"] = "res_multistep",
            ["scheduler"] = "simple",
            ["denoise"] = 1.0
        });
        g["8"] = Node("VAEDecode", new JsonObject { ["samples"] = Ref("7"), ["vae"] = Ref("3") });
        g["9"] = Node("SaveImage", new JsonObject { ["images"] = Ref("8"), ["filename_prefix"] = prefix });
        return g;
    }

    private static JsonObject BuildWan14bVideoGraph(
        string prompt,
        int width,
        int height,
        int durationSec,
        long seed,
        string? uploadedStartImage,
        string? uploadedAnchorImage,
        string refMode,
        string prefix,
        string variant = "FP8",
        string? sviPrevVideoPath = null,
        string? negativePrompt = null,
        string? extraLoraHigh = null,
        double extraLoraHighStrength = 0.75,
        string? extraLoraLow = null,
        double extraLoraLowStrength = 0.75)
    {
        bool isI2v = !string.IsNullOrWhiteSpace(uploadedStartImage);
        bool hasAnchor = !string.IsNullOrWhiteSpace(uploadedAnchorImage);
        bool isFp8 = variant.Equals("FP8", StringComparison.OrdinalIgnoreCase);
        bool useSviPro = isI2v && hasAnchor && refMode == "svi_pro";
        string loraDir = Path.Combine(HubConfig.ComfyModelsDir, "loras");
        const string sviHighLora = "SVI_v2_PRO_Wan2.2-I2V-A14B_HIGH_lora_rank_128_fp16.safetensors";
        const string sviLowLora = "SVI_v2_PRO_Wan2.2-I2V-A14B_LOW_lora_rank_128_fp16.safetensors";
        if (useSviPro && (!File.Exists(Path.Combine(loraDir, sviHighLora)) || !File.Exists(Path.Combine(loraDir, sviLowLora))))
            useSviPro = false;

        int tailFrames = Math.Clamp(HubConfig.Current.WanSviTailFrames, 1, 9);
        bool useSviTail5 = useSviPro && tailFrames >= 5 && !string.IsNullOrWhiteSpace(sviPrevVideoPath) && File.Exists(sviPrevVideoPath);
        // Wan 2.2 14B runs at 16 fps, frame count = 4*k + 1; add +4 frames when trimming 4 overlap frames on 5-frame SVI tail
        int frames = Math.Max(17, ((durationSec * 16) / 4) * 4 + 1 + (useSviTail5 ? 4 : 0));
        string negText = !string.IsNullOrWhiteSpace(negativePrompt)
            ? negativePrompt.Trim()
            : "blurry, distorted, static, flickering, morphing background, sudden lighting change, low quality, overexposed, artifacts, watermark, subtitles";

        string highFile = isFp8
            ? (isI2v ? "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" : "wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors")
            : (isI2v ? $"Wan2.2-I2V-A14B-HighNoise-{variant}.gguf" : $"Wan2.2-T2V-A14B-HighNoise-{variant}.gguf");
        string lowFile = isFp8
            ? (isI2v ? "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors" : "wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors")
            : (isI2v ? $"Wan2.2-I2V-A14B-LowNoise-{variant}.gguf" : $"Wan2.2-T2V-A14B-LowNoise-{variant}.gguf");

        string dmDir = Path.Combine(HubConfig.ComfyModelsDir, "diffusion_models");
        if (!File.Exists(Path.Combine(dmDir, highFile)) || !File.Exists(Path.Combine(dmDir, lowFile)))
            throw new FileNotFoundException($"Файлы {highFile} / {lowFile} ещё не скачаны или отсутствуют. Установи их во вкладке «Каталог и GPU Вики» или выбери другой вариант Wan 2.2.");
        string highLora = isI2v ? "wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors" : "wan2.2_t2v_lightx2v_4steps_lora_v1.1_high_noise.safetensors";
        string lowLora = isI2v ? "wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors" : "wan2.2_t2v_lightx2v_4steps_lora_v1.1_low_noise.safetensors";

        JsonObject highNode = isFp8
            ? Node("UNETLoader", new JsonObject { ["unet_name"] = highFile, ["weight_dtype"] = "default" })
            : Node("UnetLoaderGGUF", new JsonObject { ["unet_name"] = highFile });
        JsonObject lowNode = isFp8
            ? Node("UNETLoader", new JsonObject { ["unet_name"] = lowFile, ["weight_dtype"] = "default" })
            : Node("UnetLoaderGGUF", new JsonObject { ["unet_name"] = lowFile });

        double highLightningStrength = useSviPro ? Math.Clamp(HubConfig.Current.WanSviHighLightningStrength, 0.4, 1.2) : 1.0;
        double shiftVal = Math.Clamp(HubConfig.Current.WanShift, 2.0, 10.0);

        var g = new JsonObject
        {
            ["1"] = highNode,
            ["2"] = lowNode,
            ["3"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = Ref("1"), ["lora_name"] = highLora, ["strength_model"] = highLightningStrength }),
            ["4"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = Ref("2"), ["lora_name"] = lowLora, ["strength_model"] = 1.0 }),
            ["7"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = "umt5_xxl_fp8_e4m3fn_scaled.safetensors", ["type"] = "wan", ["device"] = "default" }),
            ["8"] = Node("VAELoader", new JsonObject { ["vae_name"] = "wan_2.1_vae.safetensors" }),
            ["9"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("7"), ["text"] = prompt }),
            ["10"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("7"), ["text"] = negText }),
        };

        JsonArray highModelRef = Ref("3");
        JsonArray lowModelRef = Ref("4");
        if (useSviPro)
        {
            g["20"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = highModelRef, ["lora_name"] = sviHighLora, ["strength_model"] = 1.0 });
            g["21"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = lowModelRef, ["lora_name"] = sviLowLora, ["strength_model"] = 1.0 });
            highModelRef = Ref("20");
            lowModelRef = Ref("21");
        }
        if (!string.IsNullOrWhiteSpace(extraLoraHigh))
        {
            g["90"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = highModelRef, ["lora_name"] = extraLoraHigh, ["strength_model"] = Math.Clamp(extraLoraHighStrength, -2.0, 2.0) });
            highModelRef = Ref("90");
        }
        if (!string.IsNullOrWhiteSpace(extraLoraLow))
        {
            g["91"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = lowModelRef, ["lora_name"] = extraLoraLow, ["strength_model"] = Math.Clamp(extraLoraLowStrength, -2.0, 2.0) });
            lowModelRef = Ref("91");
        }
        g["5"] = Node("ModelSamplingSD3", new JsonObject { ["model"] = highModelRef, ["shift"] = shiftVal });
        g["6"] = Node("ModelSamplingSD3", new JsonObject { ["model"] = lowModelRef, ["shift"] = shiftVal });

        JsonArray posRef;
        JsonArray negRef;
        JsonArray latRef;

        if (useSviPro)
        {
            g["18"] = Node("LoadImage", new JsonObject { ["image"] = uploadedAnchorImage! });
            if (!string.IsNullOrWhiteSpace(sviPrevVideoPath) && File.Exists(sviPrevVideoPath))
            {
                // Load last 5 frames of previous MP4 so Wan VAE encodes real motion velocity (4-frame temporal latent)
                g["19"] = Node("LoadVideoTailFrames", new JsonObject { ["video_path"] = sviPrevVideoPath, ["num_frames"] = 5 });
            }
            else
            {
                g["19"] = Node("LoadImage", new JsonObject { ["image"] = uploadedStartImage! });
            }
            g["11"] = Node("WanImageToVideoSVIProImages", new JsonObject
            {
                ["positive"] = Ref("9"),
                ["negative"] = Ref("10"),
                ["vae"] = Ref("8"),
                ["width"] = width,
                ["height"] = height,
                ["length"] = frames,
                ["anchor_image"] = Ref("18"),
                ["prev_image"] = Ref("19"),
                ["motion_latent_count"] = 1
            });
            posRef = Ref("11", 0);
            negRef = Ref("11", 1);
            latRef = Ref("11", 2);
        }
        else if (isI2v && hasAnchor && refMode == "prev_to_anchor")
        {
            g["18"] = Node("LoadImage", new JsonObject { ["image"] = uploadedStartImage! });
            g["19"] = Node("LoadImage", new JsonObject { ["image"] = uploadedAnchorImage! });
            g["11"] = Node("WanFirstLastFrameToVideo", new JsonObject
            {
                ["positive"] = Ref("9"),
                ["negative"] = Ref("10"),
                ["vae"] = Ref("8"),
                ["start_image"] = Ref("18"),
                ["end_image"] = Ref("19"),
                ["width"] = width,
                ["height"] = height,
                ["length"] = frames,
                ["batch_size"] = 1
            });
            posRef = Ref("11", 0);
            negRef = Ref("11", 1);
            latRef = Ref("11", 2);
        }
        else if (isI2v)
        {
            g["18"] = Node("LoadImage", new JsonObject { ["image"] = uploadedStartImage! });
            g["11"] = Node("WanImageToVideo", new JsonObject
            {
                ["positive"] = Ref("9"),
                ["negative"] = Ref("10"),
                ["vae"] = Ref("8"),
                ["start_image"] = Ref("18"),
                ["width"] = width,
                ["height"] = height,
                ["length"] = frames,
                ["batch_size"] = 1
            });
            posRef = Ref("11", 0);
            negRef = Ref("11", 1);
            latRef = Ref("11", 2);
        }
        else
        {
            g["11"] = Node("EmptyHunyuanLatentVideo", new JsonObject
            {
                ["width"] = width,
                ["height"] = height,
                ["length"] = frames,
                ["batch_size"] = 1
            });
            posRef = Ref("9");
            negRef = Ref("10");
            latRef = Ref("11");
        }

        g["12"] = Node("KSamplerAdvanced", new JsonObject
        {
            ["model"] = Ref("5"),
            ["positive"] = posRef,
            ["negative"] = negRef,
            ["latent_image"] = latRef,
            ["add_noise"] = "enable",
            ["noise_seed"] = seed,
            ["steps"] = 4,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["start_at_step"] = 0,
            ["end_at_step"] = 2,
            ["return_with_leftover_noise"] = "enable"
        });
        g["13"] = Node("KSamplerAdvanced", new JsonObject
        {
            ["model"] = Ref("6"),
            ["positive"] = posRef.DeepClone(),
            ["negative"] = negRef.DeepClone(),
            ["latent_image"] = Ref("12"),
            ["add_noise"] = "disable",
            ["noise_seed"] = 0,
            ["steps"] = 4,
            ["cfg"] = 1.0,
            ["sampler_name"] = "euler",
            ["scheduler"] = "simple",
            ["start_at_step"] = 2,
            ["end_at_step"] = 4,
            ["return_with_leftover_noise"] = "disable"
        });
        g["14"] = Node("VAEDecode", new JsonObject { ["samples"] = Ref("13"), ["vae"] = Ref("8") });
        JsonArray videoFramesRef = Ref("14");
        if (useSviPro)
        {
            // SVI 2.0 Pro places Step 0 anchor at frame 0; when fed a 5-frame motion tail, frames 1..3 echo the 4-frame overlap latent, so trim to frame 4
            int startIdx = useSviTail5 ? 4 : 1;
            g["22"] = Node("ImageFromBatch", new JsonObject { ["image"] = Ref("14"), ["batch_index"] = startIdx, ["length"] = 4096 });
            videoFramesRef = Ref("22");
        }
        g["15"] = Node("CreateVideo", new JsonObject { ["images"] = videoFramesRef, ["fps"] = 16 });
        g["16"] = Node("SaveVideo", new JsonObject { ["video"] = Ref("15"), ["filename_prefix"] = prefix, ["format"] = "auto", ["codec"] = "auto" });
        return g;
    }

    private static JsonObject BuildLtx25VideoGraph(
        string prompt,
        int width,
        int height,
        int durationSec,
        long seed,
        string? uploadedStartImage,
        string? uploadedAnchorImage,
        string refMode,
        string prefix,
        string? negativePrompt = null,
        string? extraLora = null,
        double extraLoraStrength = 0.75)
    {
        bool isI2v = !string.IsNullOrWhiteSpace(uploadedStartImage);
        bool useEndAnchor = !string.IsNullOrWhiteSpace(uploadedAnchorImage) && refMode == "prev_to_anchor";
        string negText = !string.IsNullOrWhiteSpace(negativePrompt)
            ? negativePrompt.Trim()
            : "blurry, ugly, distorted, flickering, morphing background, watermark, subtitles";
        // LTX-2.5 stage 1 generates at half resolution (divisible by 32), stage 2 upscales x2
        int halfW = Math.Max(256, ((width / 2) / 32) * 32);
        int halfH = Math.Max(192, ((height / 2) / 32) * 32);
        // LTX frame count = 8*k + 1 at 24 fps
        int frames = Math.Max(25, ((durationSec * 24) / 8) * 8 + 1);

        var g = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject { ["unet_name"] = "ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors", ["weight_dtype"] = "default" }),
            ["2"] = Node("VAELoader", new JsonObject { ["vae_name"] = "ltx-2.5-video-vae-bf16.safetensors" }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = "ltx-2.5-audio-vae-bf16.safetensors" }),
            ["4"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = "gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors", ["type"] = "ltxv", ["device"] = "default" }),
            ["5"] = Node("LatentUpscaleModelLoader", new JsonObject { ["model_name"] = "ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors" }),
            ["6"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("4"), ["text"] = prompt }),
            ["7"] = Node("CLIPTextEncode", new JsonObject { ["clip"] = Ref("4"), ["text"] = negText }),
            ["8"] = Node("LTXVConditioning", new JsonObject { ["positive"] = Ref("6"), ["negative"] = Ref("7"), ["frame_rate"] = 24.0 }),
            ["9"] = Node("EmptyLTXVLatentVideo", new JsonObject { ["width"] = halfW, ["height"] = halfH, ["length"] = frames, ["batch_size"] = 1 }),
            ["10"] = Node("LTXVEmptyLatentAudio", new JsonObject { ["audio_vae"] = Ref("3"), ["frames_number"] = frames, ["frame_rate"] = 24, ["batch_size"] = 1 }),
        };

        JsonArray stage1VideoLatent = Ref("9");
        JsonArray stage1Pos = Ref("8", 0);
        JsonArray stage1Neg = Ref("8", 1);

        if (isI2v)
        {
            g["30"] = Node("LoadImage", new JsonObject { ["image"] = uploadedStartImage! });
            g["31"] = Node("LTXVPreprocess", new JsonObject { ["image"] = Ref("30"), ["img_compression"] = 18 });
            g["32"] = Node("LTXVImgToVideoInplace", new JsonObject { ["vae"] = Ref("2"), ["image"] = Ref("31"), ["latent"] = Ref("9"), ["strength"] = 0.7, ["bypass"] = false });
            stage1VideoLatent = Ref("32");
        }

        if (useEndAnchor)
        {
            g["34"] = Node("LoadImage", new JsonObject { ["image"] = uploadedAnchorImage! });
            g["35"] = Node("LTXVPreprocess", new JsonObject { ["image"] = Ref("34"), ["img_compression"] = 18 });
            g["36"] = Node("LTXVAddGuide", new JsonObject
            {
                ["positive"] = stage1Pos,
                ["negative"] = stage1Neg,
                ["vae"] = Ref("2"),
                ["latent"] = stage1VideoLatent,
                ["image"] = Ref("35"),
                ["frame_idx"] = -1,
                ["strength"] = 0.85
            });
            stage1Pos = Ref("36", 0);
            stage1Neg = Ref("36", 1);
            stage1VideoLatent = Ref("36", 2);
        }

        JsonArray ltxModelRef = Ref("1");
        if (!string.IsNullOrWhiteSpace(extraLora))
        {
            g["90"] = Node("LoraLoaderModelOnly", new JsonObject { ["model"] = ltxModelRef, ["lora_name"] = extraLora, ["strength_model"] = Math.Clamp(extraLoraStrength, -2.0, 2.0) });
            ltxModelRef = Ref("90");
        }

        g["11"] = Node("LTXVConcatAVLatent", new JsonObject { ["video_latent"] = stage1VideoLatent, ["audio_latent"] = Ref("10") });
        g["12"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed });
        g["13"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler_ancestral" });
        g["14"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = "1.0, 0.99375, 0.9875, 0.98125, 0.975, 0.909375, 0.725, 0.421875, 0.0" });
        g["15"] = Node("LTXVDualCFGGuider", new JsonObject { ["model"] = ltxModelRef, ["positive"] = stage1Pos, ["negative"] = stage1Neg, ["video_cfg"] = 1.0, ["audio_cfg"] = 1.0 });
        g["16"] = Node("SamplerCustomAdvanced", new JsonObject { ["noise"] = Ref("12"), ["guider"] = Ref("15"), ["sampler"] = Ref("13"), ["sigmas"] = Ref("14"), ["latent_image"] = Ref("11") });
        g["17"] = Node("LTXVSeparateAVLatent", new JsonObject { ["av_latent"] = Ref("16", 1) });

        JsonArray sepVideoLatent = Ref("17", 0);
        JsonArray stage2Guider = Ref("15");
        if (useEndAnchor)
        {
            g["37"] = Node("LTXVCropGuides", new JsonObject { ["positive"] = stage1Pos.DeepClone(), ["negative"] = stage1Neg.DeepClone(), ["latent"] = Ref("17", 0) });
            g["38"] = Node("LTXVDualCFGGuider", new JsonObject { ["model"] = ltxModelRef.DeepClone(), ["positive"] = Ref("37", 0), ["negative"] = Ref("37", 1), ["video_cfg"] = 1.0, ["audio_cfg"] = 1.0 });
            sepVideoLatent = Ref("37", 2);
            stage2Guider = Ref("38");
        }

        g["18"] = Node("LTXVLatentUpsampler", new JsonObject { ["samples"] = sepVideoLatent, ["upscale_model"] = Ref("5"), ["vae"] = Ref("2") });

        JsonArray stage2VideoLatent = Ref("18");
        if (isI2v)
        {
            g["33"] = Node("LTXVImgToVideoInplace", new JsonObject { ["vae"] = Ref("2"), ["image"] = Ref("31"), ["latent"] = Ref("18"), ["strength"] = 1.0, ["bypass"] = false });
            stage2VideoLatent = Ref("33");
        }

        g["19"] = Node("LTXVConcatAVLatent", new JsonObject { ["video_latent"] = stage2VideoLatent, ["audio_latent"] = Ref("17", 1) });
        g["20"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = 42 });
        g["21"] = Node("ManualSigmas", new JsonObject { ["sigmas"] = "0.85, 0.7250, 0.4219, 0.0" });
        g["22"] = Node("SamplerCustomAdvanced", new JsonObject { ["noise"] = Ref("20"), ["guider"] = stage2Guider, ["sampler"] = Ref("13"), ["sigmas"] = Ref("21"), ["latent_image"] = Ref("19") });
        g["23"] = Node("LTXVSeparateAVLatent", new JsonObject { ["av_latent"] = Ref("22", 1) });
        g["24"] = Node("VAEDecodeTiled", new JsonObject { ["samples"] = Ref("23", 0), ["vae"] = Ref("2"), ["tile_size"] = 512, ["overlap"] = 64, ["temporal_size"] = 64, ["temporal_overlap"] = 16 });
        g["25"] = Node("LTXVAudioVAEDecode", new JsonObject { ["samples"] = Ref("23", 1), ["audio_vae"] = Ref("3") });
        g["26"] = Node("CreateVideo", new JsonObject { ["images"] = Ref("24"), ["audio"] = Ref("25"), ["fps"] = 24.0 });
        g["27"] = Node("SaveVideo", new JsonObject { ["video"] = Ref("26"), ["filename_prefix"] = prefix, ["format"] = "auto", ["codec"] = "auto" });
        return g;
    }
}
