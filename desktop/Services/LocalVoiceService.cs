using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class VoiceStackStatus
{
    public bool WhisperExeReady { get; init; }
    public bool WhisperModelReady { get; init; }
    public bool WhisperReady => WhisperExeReady && WhisperModelReady;
    public string? WhisperExePath { get; init; }
    public string? WhisperModelPath { get; init; }

    public bool PiperExeReady { get; init; }
    public bool PiperModelReady { get; init; }
    public bool PiperReady => PiperExeReady && PiperModelReady;
    public string? PiperExePath { get; init; }
    public string? PiperModelPath { get; init; }

    public bool FfmpegReady { get; init; }
    public string? FfmpegPath { get; init; }
}

public sealed class TtsVoiceOption
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Engine { get; init; } = "sapi"; // "sapi" or "piper"
    public string? ModelPath { get; init; }
    public string? SapiVoiceName { get; init; }
}

public sealed class LocalVoiceService : IDisposable
{
    private const string MciAlias = "LocalAIHubMicRec";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    private string? _activeRecordingPath;
    private bool _isRecording;
    private Process? _unixRecProc;

    static LocalVoiceService()
    {
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("BrazhnichenkoHub/4.0");
    }

    public static string VoiceRootDir => Path.Combine(HubConfig.Current.MediaRoot, "voice");
    public static string WhisperDir => Path.Combine(VoiceRootDir, "whisper");
    public static string PiperDir => Path.Combine(VoiceRootDir, "piper");
    public static string VoiceOutDir => Path.Combine(HubConfig.Current.MediaRoot, "voice_out");

    public bool IsRecording => _isRecording;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string lpstrCommand, StringBuilder? lpstrReturnString, int uReturnLength, IntPtr hwndCallback);

    public static VoiceStackStatus GetStatus()
    {
        string? whisperExe = OperatingSystem.IsWindows()
            ? (FindFirstFile(WhisperDir, "whisper-cli.exe") ?? FindFirstFile(WhisperDir, "main.exe"))
            : (FindFirstFile(WhisperDir, "whisper-cli") ?? FindFirstFile(WhisperDir, "main") ?? FindUnixBin("whisper-cli"));
        string? whisperModel = FindFirstFile(WhisperDir, "ggml-*.bin");
        string? piperExe = OperatingSystem.IsWindows()
            ? FindFirstFile(PiperDir, "piper.exe")
            : (FindFirstFile(PiperDir, "piper") ?? FindUnixBin("piper"));
        string? piperModel = FindFirstFile(PiperDir, "*.onnx");
        string? ffmpeg = FindFfmpegPath();

        return new VoiceStackStatus
        {
            WhisperExeReady = whisperExe is not null,
            WhisperModelReady = whisperModel is not null,
            WhisperExePath = whisperExe,
            WhisperModelPath = whisperModel,
            PiperExeReady = piperExe is not null,
            PiperModelReady = piperModel is not null,
            PiperExePath = piperExe,
            PiperModelPath = piperModel,
            FfmpegReady = ffmpeg is not null,
            FfmpegPath = ffmpeg
        };
    }

    private static string? FindUnixBin(string name)
    {
        foreach (string dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string? FindFfmpegPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string[] candidates = OperatingSystem.IsWindows()
            ? [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"),
                Path.Combine(HubConfig.Current.MediaRoot, "ffmpeg.exe"),
                Path.Combine(VoiceRootDir, "ffmpeg.exe"),
                @"C:\ffmpeg\bin\ffmpeg.exe"
              ]
            : [
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg",
                "/snap/bin/ffmpeg",
                Path.Combine(HubConfig.Current.MediaRoot, "ffmpeg"),
                Path.Combine(VoiceRootDir, "ffmpeg")
              ];
        foreach (string c in candidates)
            if (File.Exists(c)) return c;

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string f = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
        }
        return null;
    }

    private static string? FindFirstFile(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ==================== MICROPHONE RECORDING (WinMM PCM 16kHz Mono) ====================

    public string StartRecording()
    {
        if (_isRecording)
            throw new InvalidOperationException("Запись с микрофона уже идёт.");

        Directory.CreateDirectory(VoiceOutDir);
        string wavPath = Path.Combine(VoiceOutDir, $"mic_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _activeRecordingPath = wavPath;

        if (!OperatingSystem.IsWindows())
        {
            string ffmpeg = FindFfmpegPath()
                ?? throw new InvalidOperationException("Для записи с микрофона на macOS/Linux нужен ffmpeg (brew install ffmpeg).");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(OperatingSystem.IsMacOS() ? "avfoundation" : "alsa");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(OperatingSystem.IsMacOS() ? ":0" : "default");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("16000");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(wavPath);
            _unixRecProc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg для записи с микрофона.");
            _isRecording = true;
            return wavPath;
        }

        mciSendStringW($"close {MciAlias}", null, 0, IntPtr.Zero);
        int rc = mciSendStringW($"open new type waveaudio alias {MciAlias}", null, 0, IntPtr.Zero);
        if (rc != 0)
            throw new InvalidOperationException($"Не удалось открыть микрофон (WinMM код {rc}). Проверь, что микрофон подключён.");

        // 16 kHz, 16-bit, mono — ideal native format for whisper.cpp
        mciSendStringW($"set {MciAlias} time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2", null, 0, IntPtr.Zero);
        rc = mciSendStringW($"record {MciAlias}", null, 0, IntPtr.Zero);
        if (rc != 0)
        {
            mciSendStringW($"close {MciAlias}", null, 0, IntPtr.Zero);
            throw new InvalidOperationException($"Не удалось начать запись (WinMM код {rc}).");
        }

        _isRecording = true;
        return wavPath;
    }

    public string StopRecording()
    {
        if (!_isRecording || string.IsNullOrWhiteSpace(_activeRecordingPath))
            throw new InvalidOperationException("Запись не активна.");

        string target = _activeRecordingPath;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                if (_unixRecProc is not null && !_unixRecProc.HasExited)
                {
                    try { _unixRecProc.StandardInput.Write('q'); _unixRecProc.StandardInput.Flush(); } catch { }
                    if (!_unixRecProc.WaitForExit(3000))
                        try { _unixRecProc.Kill(true); } catch { }
                }
            }
            finally
            {
                _unixRecProc?.Dispose();
                _unixRecProc = null;
                _isRecording = false;
                _activeRecordingPath = null;
            }
            if (!File.Exists(target) || new FileInfo(target).Length < 512)
                throw new InvalidOperationException("Записанный файл пуст — проверь доступ к микрофону в настройках системы.");
            return target;
        }

        try
        {
            mciSendStringW($"stop {MciAlias}", null, 0, IntPtr.Zero);
            int rc = mciSendStringW($"save {MciAlias} \"{target}\"", null, 0, IntPtr.Zero);
            if (rc != 0)
                throw new InvalidOperationException($"Не удалось сохранить WAV (WinMM код {rc}).");
        }
        finally
        {
            mciSendStringW($"close {MciAlias}", null, 0, IntPtr.Zero);
            _isRecording = false;
            _activeRecordingPath = null;
        }

        if (!File.Exists(target) || new FileInfo(target).Length < 512)
            throw new InvalidOperationException("Записанный файл пуст — скажи чуть громче или проверь микрофон в настройках Windows.");

        return target;
    }

    public void CancelRecording()
    {
        if (!_isRecording) return;
        string? target = _activeRecordingPath;
        if (!OperatingSystem.IsWindows())
        {
            try { if (_unixRecProc is not null && !_unixRecProc.HasExited) _unixRecProc.Kill(true); } catch { }
            _unixRecProc?.Dispose();
            _unixRecProc = null;
            _isRecording = false;
            _activeRecordingPath = null;
            try { if (!string.IsNullOrEmpty(target) && File.Exists(target)) File.Delete(target); } catch { }
            return;
        }
        try
        {
            mciSendStringW($"stop {MciAlias}", null, 0, IntPtr.Zero);
            mciSendStringW($"close {MciAlias}", null, 0, IntPtr.Zero);
        }
        catch { }
        finally
        {
            _isRecording = false;
            _activeRecordingPath = null;
        }
        try { if (!string.IsNullOrEmpty(target) && File.Exists(target)) File.Delete(target); } catch { }
    }

    // ==================== WHISPER.CPP STT ====================

    public async Task<string> TranscribeWavAsync(string wavPath, string language = "auto", CancellationToken ct = default)
    {
        var st = GetStatus();
        if (!st.WhisperReady || st.WhisperExePath is null || st.WhisperModelPath is null)
            throw new InvalidOperationException("Whisper (STT) ещё не установлен. Установи его в 1 клик в разделе «Система → Локальный голос».");

        var psi = new ProcessStartInfo
        {
            FileName = st.WhisperExePath,
            WorkingDirectory = Path.GetDirectoryName(st.WhisperExePath) ?? WhisperDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(st.WhisperModelPath);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(wavPath);
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(language) ? "auto" : language);
        psi.ArgumentList.Add("--no-timestamps");
        psi.ArgumentList.Add("-nt");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить whisper-cli.exe.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"Ошибка whisper.cpp (код {proc.ExitCode}): {stderr.Trim()}");

        return CleanWhisperOutput(stdout);
    }

    public static string CleanWhisperOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !l.StartsWith("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase));
        return string.Join(' ', lines).Trim();
    }

    public async Task InstallWhisperAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(WhisperDir);
        var st = GetStatus();

        if (!st.WhisperExeReady)
        {
            if (OperatingSystem.IsMacOS())
            {
                string? brew = FindUnixBin("brew");
                if (brew is not null)
                {
                    onProgress?.Invoke("Установка whisper-cpp через Homebrew...");
                    var brewPsi = new ProcessStartInfo { FileName = brew, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    brewPsi.ArgumentList.Add("install");
                    brewPsi.ArgumentList.Add("whisper-cpp");
                    using var bp = Process.Start(brewPsi);
                    if (bp is not null) await bp.WaitForExitAsync(ct);
                }
                if (GetStatus().WhisperExeReady == false)
                    throw new InvalidOperationException("Для Whisper STT на macOS установи whisper-cpp: выполни в Терминале `brew install whisper-cpp ffmpeg`, затем повтори.");
            }
            else if (OperatingSystem.IsLinux())
            {
                // Still download the base model first if missing, then instruct user on native whisper-cli
                string modelPath = Path.Combine(WhisperDir, "ggml-base.bin");
                if (!File.Exists(modelPath))
                {
                    onProgress?.Invoke("Скачивание мультиязычной модели Whisper Base (~142 МБ, RU + EN)...");
                    await DownloadFileAsync("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
                        modelPath, pct => onProgress?.Invoke($"Скачивание ggml-base.bin: {pct:0}%"), ct);
                }
                throw new InvalidOperationException("Модель ggml-base.bin скачана! Для бинарника whisper-cli на Linux установи пакет whisper.cpp (или собери из github.com/ggml-org/whisper.cpp и положи whisper-cli в PATH или папку voice/whisper).");
            }
            else
            {
                onProgress?.Invoke("Поиск последнего бинарного релиза whisper.cpp на GitHub...");
                string? zipUrl = await FindGitHubReleaseAssetUrlAsync(
                    "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10",
                    name => name.Equals("whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase),
                    ct)
                    ?? await FindGitHubReleaseAssetUrlAsync(
                        "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10",
                        name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                name.Contains("bin-x64", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("cuda", StringComparison.OrdinalIgnoreCase),
                        ct);

                zipUrl ??= "https://github.com/ggml-org/whisper.cpp/releases/download/b5907/whisper-bin-x64.zip";

                string tmpZip = Path.Combine(WhisperDir, "whisper-bin-x64.zip");
                onProgress?.Invoke("Скачивание whisper.cpp (Windows x64)...");
                await DownloadFileAsync(zipUrl, tmpZip, pct => onProgress?.Invoke($"Скачивание whisper.cpp: {pct:0}%"), ct);

                onProgress?.Invoke("Распаковка whisper.cpp...");
                ZipFile.ExtractToDirectory(tmpZip, WhisperDir, overwriteFiles: true);
                try { File.Delete(tmpZip); } catch { }
            }
        }

        st = GetStatus();
        if (!st.WhisperModelReady)
        {
            string modelPath = Path.Combine(WhisperDir, "ggml-base.bin");
            string modelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
            onProgress?.Invoke("Скачивание мультиязычной модели Whisper Base (~142 МБ, RU + EN)...");
            await DownloadFileAsync(modelUrl, modelPath, pct => onProgress?.Invoke($"Скачивание ggml-base.bin: {pct:0}%"), ct);
        }

        onProgress?.Invoke("Whisper STT установлен и готов к голосовому вводу!");
    }

    // ==================== LOCAL TTS (Windows SAPI Offline + Piper Neural HD) ====================

    public static List<TtsVoiceOption> GetAvailableTtsVoices()
    {
        var list = new List<TtsVoiceOption>();

        // 1. Piper neural voices (if installed)
        if (Directory.Exists(PiperDir))
        {
            foreach (string onnx in Directory.GetFiles(PiperDir, "*.onnx", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(onnx);
                list.Add(new TtsVoiceOption
                {
                    Id = $"piper:{name}",
                    DisplayName = $"Piper HD — {name}",
                    Engine = "piper",
                    ModelPath = onnx
                });
            }
        }

        // 2. Built-in OS offline voices (work out-of-the-box, zero download!)
        if (OperatingSystem.IsMacOS())
        {
            list.Add(new TtsVoiceOption { Id = "say:Milena", DisplayName = "macOS RU — Женский (Milena, офлайн)", Engine = "say", SapiVoiceName = "Milena" });
            list.Add(new TtsVoiceOption { Id = "say:Yuri", DisplayName = "macOS RU — Мужской (Yuri, офлайн)", Engine = "say", SapiVoiceName = "Yuri" });
            list.Add(new TtsVoiceOption { Id = "say:Samantha", DisplayName = "macOS EN — English (Samantha, офлайн)", Engine = "say", SapiVoiceName = "Samantha" });
        }
        else if (OperatingSystem.IsLinux())
        {
            list.Add(new TtsVoiceOption { Id = "espeak:ru", DisplayName = "Linux RU — espeak-ng (офлайн)", Engine = "espeak", SapiVoiceName = "ru" });
            list.Add(new TtsVoiceOption { Id = "espeak:en", DisplayName = "Linux EN — espeak-ng (офлайн)", Engine = "espeak", SapiVoiceName = "en" });
        }
        else
        {
            list.Add(new TtsVoiceOption { Id = "sapi:ru-male", DisplayName = "Windows RU — Мужской (Pavel, офлайн)", Engine = "sapi", SapiVoiceName = "Pavel" });
            list.Add(new TtsVoiceOption { Id = "sapi:ru-female", DisplayName = "Windows RU — Женский (Irina, офлайн)", Engine = "sapi", SapiVoiceName = "Irina" });
            list.Add(new TtsVoiceOption { Id = "sapi:en-female", DisplayName = "Windows EN — English (Zira, офлайн)", Engine = "sapi", SapiVoiceName = "Zira" });
        }

        return list;
    }

    public async Task<string> SynthesizeToWavAsync(string text, TtsVoiceOption voice, double rate = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Введи текст для озвучки.", nameof(text));

        Directory.CreateDirectory(VoiceOutDir);
        string outWav = Path.Combine(VoiceOutDir, $"tts_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        if (voice.Engine == "piper")
        {
            var st = GetStatus();
            if (!st.PiperReady || st.PiperExePath is null || string.IsNullOrWhiteSpace(voice.ModelPath))
                throw new InvalidOperationException("Piper HD TTS не установлен.");

            var psi = new ProcessStartInfo
            {
                FileName = st.PiperExePath,
                WorkingDirectory = Path.GetDirectoryName(st.PiperExePath) ?? PiperDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(voice.ModelPath);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(outWav);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить piper.exe.");
            await proc.StandardInput.WriteLineAsync(text.Trim());
            proc.StandardInput.Close();
            string err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || !File.Exists(outWav))
                throw new InvalidOperationException($"Ошибка Piper TTS (код {proc.ExitCode}): {err.Trim()}");

            return outWav;
        }
        else if (voice.Engine == "espeak" || OperatingSystem.IsLinux())
        {
            string? bin = FindUnixBin("espeak-ng") ?? FindUnixBin("espeak");
            if (bin is null)
                throw new InvalidOperationException("Для системной озвучки на Linux установи `espeak-ng` (`sudo apt install espeak-ng`) или поставь нейросетевой голос Piper HD в 1 клик в разделе «Система».");

            int wpm = Math.Clamp((int)Math.Round(165 + rate * 15), 90, 280);
            var psi = new ProcessStartInfo
            {
                FileName = bin,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(voice.SapiVoiceName) ? "ru" : voice.SapiVoiceName);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(wpm.ToString());
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(outWav);
            psi.ArgumentList.Add(text.Trim());

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить espeak-ng.");
            string err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || !File.Exists(outWav))
                throw new InvalidOperationException($"Ошибка Linux TTS (код {proc.ExitCode}): {err.Trim()}");
            return outWav;
        }
        else if (voice.Engine == "say" || OperatingSystem.IsMacOS())
        {
            // Native macOS `say` offline synthesizer -> 22.05kHz 16-bit PCM WAV
            int wpm = Math.Clamp((int)Math.Round(185 + rate * 18), 110, 300);
            var psi = new ProcessStartInfo
            {
                FileName = "say",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            if (!string.IsNullOrWhiteSpace(voice.SapiVoiceName))
            {
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add(voice.SapiVoiceName);
            }
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(wpm.ToString());
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outWav);
            psi.ArgumentList.Add("--data-format=LEI16@22050");
            psi.ArgumentList.Add(text.Trim());

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить macOS say.");
            string err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0 || !File.Exists(outWav))
                throw new InvalidOperationException($"Ошибка macOS TTS (код {proc.ExitCode}): {err.Trim()}");
            return outWav;
        }
        else
        {
            // Native Windows System.Speech offline synthesizer via powershell.exe (zero NuGet deps, works everywhere)
            string txtFile = outWav + ".txt";
            await File.WriteAllTextAsync(txtFile, text.Trim(), Encoding.UTF8, ct);
            int intRate = Math.Clamp((int)Math.Round(rate), -6, 6);
            string hint = (voice.SapiVoiceName ?? "").Replace("'", "''");
            string safeTxt = txtFile.Replace("'", "''");
            string safeWav = outWav.Replace("'", "''");

            string psScript =
                "Add-Type -AssemblyName System.Speech; " +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                $"$hint = '{hint}'; " +
                "$v = $s.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Name -like \"*$hint*\" } | Select-Object -First 1; " +
                "if ($v) { $s.SelectVoice($v.VoiceInfo.Name) }; " +
                $"$s.Rate = {intRate}; " +
                $"$s.SetOutputToWaveFile('{safeWav}'); " +
                $"$s.Speak([System.IO.File]::ReadAllText('{safeTxt}', [System.Text.Encoding]::UTF8)); " +
                "$s.Dispose();";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(psScript);

            try
            {
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить синтезатор речи Windows.");
                string err = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0 || !File.Exists(outWav))
                    throw new InvalidOperationException($"Ошибка синтеза речи: {err.Trim()}");
                return outWav;
            }
            finally
            {
                try { if (File.Exists(txtFile)) File.Delete(txtFile); } catch { }
            }
        }
    }

    public static string SelectLinuxPiperAsset(Architecture arch) =>
        arch == Architecture.Arm64 ? "piper_linux_aarch64.tar.gz" : "piper_linux_x86_64.tar.gz";

    public async Task InstallPiperRuAsync(Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(PiperDir);
        var st = GetStatus();

        if (!st.PiperExeReady)
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                string asset = OperatingSystem.IsMacOS()
                    ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "piper_macos_aarch64.tar.gz" : "piper_macos_x64.tar.gz")
                    : SelectLinuxPiperAsset(RuntimeInformation.OSArchitecture);
                onProgress?.Invoke($"Скачивание Piper TTS ({asset})...");
                string url = $"https://github.com/rhasspy/piper/releases/download/2023.11.14-2/{asset}";
                string tmpTar = Path.Combine(PiperDir, asset);
                await DownloadFileAsync(url, tmpTar, pct => onProgress?.Invoke($"Скачивание Piper TTS: {pct:0}%"), ct);
                onProgress?.Invoke("Распаковка Piper TTS...");
                var tPsi = new ProcessStartInfo { FileName = "tar", UseShellExecute = false, CreateNoWindow = true };
                tPsi.ArgumentList.Add("-xzf"); tPsi.ArgumentList.Add(tmpTar); tPsi.ArgumentList.Add("-C"); tPsi.ArgumentList.Add(PiperDir);
                using var tp = Process.Start(tPsi);
                if (tp is not null) await tp.WaitForExitAsync(ct);
                try { File.Delete(tmpTar); } catch { }

                string? piperBin = FindFirstFile(PiperDir, "piper");
                if (piperBin is not null)
                {
                    try
                    {
                        var cPsi = new ProcessStartInfo { FileName = "chmod", UseShellExecute = false, CreateNoWindow = true };
                        cPsi.ArgumentList.Add("+x"); cPsi.ArgumentList.Add(piperBin);
                        using var cp = Process.Start(cPsi);
                        if (cp is not null) await cp.WaitForExitAsync(ct);
                    }
                    catch { }
                }
            }
            else
            {
                onProgress?.Invoke("Скачивание Piper TTS (Windows x64)...");
                string zipUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
                string tmpZip = Path.Combine(PiperDir, "piper_windows_amd64.zip");
                await DownloadFileAsync(zipUrl, tmpZip, pct => onProgress?.Invoke($"Скачивание Piper TTS: {pct:0}%"), ct);
                onProgress?.Invoke("Распаковка Piper TTS...");
                ZipFile.ExtractToDirectory(tmpZip, PiperDir, overwriteFiles: true);
                try { File.Delete(tmpZip); } catch { }
            }
        }

        string voiceOnnx = Path.Combine(PiperDir, "ru_RU-dmitri-medium.onnx");
        string voiceJson = Path.Combine(PiperDir, "ru_RU-dmitri-medium.onnx.json");
        if (!File.Exists(voiceOnnx))
        {
            onProgress?.Invoke("Скачивание русского нейро-голоса Dmitri (~63 МБ)...");
            await DownloadFileAsync(
                "https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/dmitri/medium/ru_RU-dmitri-medium.onnx",
                voiceOnnx,
                pct => onProgress?.Invoke($"Скачивание ru_RU-dmitri-medium: {pct:0}%"),
                ct);
        }
        if (!File.Exists(voiceJson))
        {
            await DownloadFileAsync(
                "https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/dmitri/medium/ru_RU-dmitri-medium.onnx.json",
                voiceJson,
                null,
                ct);
        }

        onProgress?.Invoke("Piper HD TTS (русский нейро-голос) установлен и готов!");
    }

    // ==================== FFMPEG VIDEO VOICEOVER MUXING ====================

    public async Task<string> MuxAudioOntoVideoAsync(string videoPath, string audioWavPath, bool keepOriginalAudio = true, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Видеофайл не найден.", videoPath);
        if (!File.Exists(audioWavPath)) throw new FileNotFoundException("Аудиофайл не найден.", audioWavPath);

        string ffmpeg = FindFfmpegPath()
            ?? throw new InvalidOperationException(OperatingSystem.IsMacOS()
                ? "ffmpeg не найден в системе. Установи FFmpeg: brew install ffmpeg"
                : OperatingSystem.IsLinux()
                    ? "ffmpeg не найден в системе. Установи FFmpeg: sudo apt install ffmpeg"
                    : "ffmpeg.exe не найден в системе. Установи FFmpeg (winget install Gyan.FFmpeg).");

        string dir = Path.GetDirectoryName(videoPath) ?? VoiceOutDir;
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        string outMp4 = Path.Combine(dir, $"{stem}_voiced_{DateTime.Now:HHmmss}.mp4");

        // Replace audio track cleanly (or mix if requested and source has audio)
        bool hasSourceAudio = keepOriginalAudio && await VideoHasAudioStreamAsync(ffmpeg, videoPath, ct);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioWavPath);

        if (hasSourceAudio)
        {
            psi.ArgumentList.Add("-filter_complex");
            psi.ArgumentList.Add("[0:a]volume=0.35[a0];[1:a]volume=1.0[a1];[a0][a1]amix=inputs=2:duration=first:dropout_transition=2[aout]");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("[aout]");
        }
        else
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("1:a:0");
            psi.ArgumentList.Add("-shortest");
        }

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add(outMp4);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        string err = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0 || !File.Exists(outMp4))
            throw new InvalidOperationException($"Ошибка наложения озвучки (FFmpeg {proc.ExitCode}): {err.Trim()}");

        return outMp4;
    }

    private static async Task<bool> VideoHasAudioStreamAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            string info = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return info.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<string?> FindGitHubReleaseAssetUrlAsync(string apiUrl, Func<string, bool> predicate, CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync(apiUrl, ct);
            using var doc = JsonDocument.Parse(json);

            static string? SearchReleaseObject(JsonElement rel, Func<string, bool> pred)
            {
                if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    return null;
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(url) && pred(name))
                        return url;
                }
                return null;
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    string? found = SearchReleaseObject(rel, predicate);
                    if (found is not null) return found;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return SearchReleaseObject(doc.RootElement, predicate);
            }
        }
        catch { }
        return null;
    }

    private static async Task DownloadFileAsync(string url, string destPath, Action<double>? onPercent, CancellationToken ct)
    {
        string tmp = destPath + ".part";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            byte[] buf = new byte[81920];
            long read = 0;
            int n;
            var lastReport = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && onPercent is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    onPercent(Math.Clamp(read * 100.0 / total, 0, 100));
                }
            }
        }
        File.Move(tmp, destPath, overwrite: true);
    }

    public void Dispose()
    {
        CancelRecording();
    }
}
