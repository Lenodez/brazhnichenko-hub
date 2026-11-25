using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SherpaOnnx;

namespace LocalAIHub.Services;

public sealed record SherpaSttModelSpec(
    string Id,
    string DisplayName,
    string DescriptionRu,
    double SizeMB,
    (string Url, string LocalName)[] Files
);

public sealed record SherpaStatusInfo(
    bool RuntimeDllReady,
    bool GigaAmReady,
    bool ParakeetReady,
    string ActiveModelId,
    string ActiveModelLabel,
    bool AnyReady
);

/// <summary>
/// High-speed in-process offline Speech-to-Text engine backed by Sherpa-ONNX v2.0.3 C-API
/// (identical models to our Android app: GigaAM v2 Russian & NVIDIA Parakeet-TDT v3).
/// Keeps the active ONNX model warm in RAM so transcription takes ~150-350 ms instead of 2-3 s!
/// </summary>
public static class SherpaDesktopSttEngine
{
    public const string ModelGigaAm = "gigaam-v2-ru";
    public const string ModelParakeet = "parakeet-tdt-v3";

    private const string RuntimeTarBz2Url =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.3/sherpa-onnx-v2.0.3-win-x64-shared-MD-Release-no-tts-lib.tar.bz2";

    public static readonly IReadOnlyList<SherpaSttModelSpec> Models =
    [
        new(
            Id: ModelGigaAm,
            DisplayName: "GigaAM v2 Russian (Сбер · 225 МБ)",
            DescriptionRu: "Лучшая точность чистой русской речи офлайн · мгновенный отклик (~0.2 с), как на Android.",
            SizeMB: 225.5,
            Files: [
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19/resolve/main/model.int8.onnx", "model.int8.onnx"),
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19/resolve/main/tokens.txt", "tokens.txt")
            ]
        ),
        new(
            Id: ModelParakeet,
            DisplayName: "NVIDIA Parakeet-TDT v3 (RU+EN · 670 МБ)",
            DescriptionRu: "Смешанная речь RU + EN, IT-термины, пунктуация и заглавные из коробки · 5–10× быстрее Whisper.",
            SizeMB: 670.5,
            Files: [
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/encoder.int8.onnx", "encoder.int8.onnx"),
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/decoder.int8.onnx", "decoder.int8.onnx"),
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/joiner.int8.onnx", "joiner.int8.onnx"),
                ("https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/tokens.txt", "tokens.txt")
            ]
        )
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private static readonly object RecLock = new();
    private static OfflineRecognizer? _cachedRecognizer;
    private static string _cachedModelId = "";
    private static int _resolverRegistered;

    static SherpaDesktopSttEngine()
    {
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("BrazhnichenkoHub/5.3");
    }

    public static string SherpaRootDir => Path.Combine(LocalVoiceService.VoiceRootDir, "sherpa");
    public static string SherpaLibDir => Path.Combine(SherpaRootDir, "lib");
    public static string GetModelDir(string modelId) => Path.Combine(SherpaRootDir, "models", modelId);

    public static bool IsRuntimeDllReady() =>
        File.Exists(Path.Combine(SherpaLibDir, "sherpa-onnx-c-api.dll"))
        && File.Exists(Path.Combine(SherpaLibDir, "onnxruntime.dll"));

    public static bool IsModelInstalled(string modelId)
    {
        var spec = Models.FirstOrDefault(m => m.Id == modelId);
        if (spec is null) return false;
        string dir = GetModelDir(modelId);
        return spec.Files.All(f =>
        {
            string p = Path.Combine(dir, f.LocalName);
            long minBytes = f.LocalName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? 1024 * 1024 : 16;
            return File.Exists(p) && new FileInfo(p).Length >= minBytes;
        });
    }

    public static SherpaStatusInfo GetStatus()
    {
        bool dll = IsRuntimeDllReady();
        bool giga = IsModelInstalled(ModelGigaAm);
        bool para = IsModelInstalled(ModelParakeet);

        string pref = HubConfig.Current.DesktopSttModel ?? "";
        string active = "";
        if (dll)
        {
            if (pref == ModelParakeet && para) active = ModelParakeet;
            else if (pref == ModelGigaAm && giga) active = ModelGigaAm;
            else if (giga) active = ModelGigaAm;
            else if (para) active = ModelParakeet;
        }

        string label = active switch
        {
            ModelGigaAm => "GigaAM v2 RU",
            ModelParakeet => "Parakeet v3",
            _ => ""
        };
        return new SherpaStatusInfo(dll, giga, para, active, label, !string.IsNullOrEmpty(active));
    }

    public static void UnloadCachedModel()
    {
        lock (RecLock)
        {
            try { _cachedRecognizer?.Dispose(); } catch { }
            _cachedRecognizer = null;
            _cachedModelId = "";
        }
    }

    public static Task<(string Text, long DecodeMs, string EngineLabel)> TranscribeSamplesAsync(
        float[] samples16k,
        string? preferredModelId = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var st = GetStatus();
            string modelId = !string.IsNullOrWhiteSpace(preferredModelId) && IsModelInstalled(preferredModelId)
                ? preferredModelId
                : st.ActiveModelId;
            if (!st.RuntimeDllReady || string.IsNullOrEmpty(modelId))
                throw new InvalidOperationException("Офлайн-движок Sherpa-ONNX (GigaAM v2 / Parakeet v3) ещё не установлен.");

            EnsureNativeResolverRegistered();

            var sw = Stopwatch.StartNew();
            lock (RecLock)
            {
                var rec = GetOrCreateRecognizerLocked(modelId);
                using var stream = rec.CreateStream();
                stream.AcceptWaveform(LiveMicRecorder.SampleRate, samples16k);
                rec.Decode(stream);
                string text = (stream.Result?.Text ?? "").Trim();
                sw.Stop();
                string label = modelId == ModelParakeet ? "Parakeet v3" : "GigaAM v2";
                return (text, sw.ElapsedMilliseconds, label);
            }
        }, ct);
    }

    private static OfflineRecognizer GetOrCreateRecognizerLocked(string modelId)
    {
        if (_cachedRecognizer is not null && _cachedModelId == modelId)
            return _cachedRecognizer;

        try { _cachedRecognizer?.Dispose(); } catch { }
        _cachedRecognizer = null;
        _cachedModelId = "";

        string dir = GetModelDir(modelId);
        var cfg = new OfflineRecognizerConfig();
        cfg.FeatConfig.SampleRate = LiveMicRecorder.SampleRate;
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        cfg.ModelConfig.Provider = "cpu";
        cfg.ModelConfig.Debug = 0;

        if (modelId == ModelGigaAm)
        {
            cfg.ModelConfig.NeMoCtc.Model = ToSafeNativePath(Path.Combine(dir, "model.int8.onnx"));
            cfg.ModelConfig.Tokens = ToSafeNativePath(Path.Combine(dir, "tokens.txt"));
        }
        else if (modelId == ModelParakeet)
        {
            cfg.ModelConfig.Transducer.Encoder = ToSafeNativePath(Path.Combine(dir, "encoder.int8.onnx"));
            cfg.ModelConfig.Transducer.Decoder = ToSafeNativePath(Path.Combine(dir, "decoder.int8.onnx"));
            cfg.ModelConfig.Transducer.Joiner = ToSafeNativePath(Path.Combine(dir, "joiner.int8.onnx"));
            cfg.ModelConfig.Tokens = ToSafeNativePath(Path.Combine(dir, "tokens.txt"));
            cfg.ModelConfig.ModelType = "nemo_transducer";
        }
        else
        {
            throw new ArgumentException($"Неизвестная модель Sherpa-ONNX: {modelId}");
        }

        _cachedRecognizer = new OfflineRecognizer(cfg);
        _cachedModelId = modelId;
        return _cachedRecognizer;
    }

    public static async Task InstallRuntimeAndModelAsync(
        string modelId,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var spec = Models.FirstOrDefault(m => m.Id == modelId)
            ?? throw new ArgumentException($"Неизвестная модель: {modelId}");

        Directory.CreateDirectory(SherpaLibDir);
        if (!IsRuntimeDllReady())
        {
            onProgress?.Invoke("Скачивание ядра Sherpa-ONNX v2.0.3 (~7.3 МБ)...");
            string tarPath = Path.Combine(SherpaRootDir, "sherpa-onnx-win-x64.tar.bz2");
            await DownloadFileAsync(RuntimeTarBz2Url, tarPath, "Sherpa-ONNX runtime", onProgress, ct);

            onProgress?.Invoke("Распаковка Sherpa-ONNX DLL...");
            string tmpExtract = Path.Combine(SherpaRootDir, "_extract_tmp");
            if (Directory.Exists(tmpExtract)) Directory.Delete(tmpExtract, true);
            Directory.CreateDirectory(tmpExtract);

            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xjf \"{tarPath}\" -C \"{tmpExtract}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить tar.exe для распаковки Sherpa-ONNX."))
            {
                string err = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"Ошибка распаковки tar.exe (код {proc.ExitCode}): {err}");
            }

            foreach (string dll in Directory.EnumerateFiles(tmpExtract, "*.dll", SearchOption.AllDirectories))
                File.Copy(dll, Path.Combine(SherpaLibDir, Path.GetFileName(dll)), overwrite: true);

            try { Directory.Delete(tmpExtract, true); } catch { }
            try { File.Delete(tarPath); } catch { }
        }

        string modelDir = GetModelDir(modelId);
        Directory.CreateDirectory(modelDir);
        for (int i = 0; i < spec.Files.Length; i++)
        {
            var (url, localName) = spec.Files[i];
            string dest = Path.Combine(modelDir, localName);
            long minBytes = localName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? 1024 * 1024 : 16;
            if (File.Exists(dest) && new FileInfo(dest).Length >= minBytes) continue;
            await DownloadFileAsync(url, dest, $"{spec.DisplayName} [{i + 1}/{spec.Files.Length}] {localName}", onProgress, ct);
        }

        HubConfig.Current.DesktopSttModel = modelId;
        HubConfig.Save();
        onProgress?.Invoke($"✓ {spec.DisplayName} установлен и готов к мгновенной диктовке!");
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        string label,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        string tmp = destPath + ".part";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        byte[] buf = new byte[81920];
        long readTotal = 0;
        var sw = Stopwatch.StartNew();
        long lastReportMs = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            readTotal += n;
            if (sw.ElapsedMilliseconds - lastReportMs >= 250)
            {
                lastReportMs = sw.ElapsedMilliseconds;
                if (total > 0)
                    onProgress?.Invoke($"Загрузка {label}: {readTotal * 100.0 / total:F0}% ({readTotal / 1048576.0:F1}/{total / 1048576.0:F1} МБ)");
                else
                    onProgress?.Invoke($"Загрузка {label}: {readTotal / 1048576.0:F1} МБ...");
            }
        }
        await dst.FlushAsync(ct);
        dst.Close();
        File.Move(tmp, destPath, overwrite: true);
    }

    private static void EnsureNativeResolverRegistered()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0) return;

        NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, (libraryName, _, _) =>
        {
            if (libraryName.Equals(Dll.Filename, StringComparison.OrdinalIgnoreCase)
                || libraryName.Contains("sherpa-onnx", StringComparison.OrdinalIgnoreCase))
            {
                // Preload onnxruntime.dll from SherpaLibDir so Windows loader finds it cleanly
                string ortPath = Path.Combine(SherpaLibDir, "onnxruntime.dll");
                if (File.Exists(ortPath))
                    NativeLibrary.TryLoad(ortPath, out _);

                string apiPath = Path.Combine(SherpaLibDir, "sherpa-onnx-c-api.dll");
                if (File.Exists(apiPath) && NativeLibrary.TryLoad(apiPath, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>
    /// Converts non-ASCII Windows paths to 8.3 short paths when available so LPStr marshaling in SherpaOnnx C-API never garbles Cyrillic folders.
    /// </summary>
    private static string ToSafeNativePath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.All(c => c < 128)) return path;
        try
        {
            var sb = new StringBuilder(512);
            uint len = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            if (len > 0 && len < sb.Capacity) return sb.ToString();
        }
        catch { }
        return path;
    }
}
