using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public sealed record AssistantAction(string Type, string Param, string Label);

public sealed record AssistantReply(
    string Text,
    List<AssistantAction> Actions,
    string EngineUsed,
    List<string> RagSources
);

public sealed record NetworkCheckResult(
    bool GitHubOk,
    string GitHubDetail,
    bool HuggingFaceOk,
    string HuggingFaceDetail,
    bool HfAuthOk,
    string HfAuthDetail,
    bool LlamaInstalled,
    bool ComfyInstalled,
    double FreeDiskGB
)
{
    public string ToSummaryText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Результаты диагностики сети и окружения:");
        sb.AppendLine($"• GitHub (для llama.cpp и ComfyUI): {(GitHubOk ? "✓ Доступен" : "✕ Недоступен")} — {GitHubDetail}");
        sb.AppendLine($"• Hugging Face (для моделей): {(HuggingFaceOk ? "✓ Доступен" : "✕ Недоступен")} — {HuggingFaceDetail}");
        sb.AppendLine($"• HF-токен: {(HfAuthOk ? $"✓ {HfAuthDetail}" : $"○ {HfAuthDetail}")}");
        sb.AppendLine($"• Движок llama.cpp: {(LlamaInstalled ? "✓ Установлен" : "⚠ Не установлен")}");
        sb.AppendLine($"• Движок ComfyUI: {(ComfyInstalled ? "✓ Установлен" : "⚠ Не установлен")}");
        sb.AppendLine($"• Свободно на диске {HubConfig.DataDriveLetter}: {FreeDiskGB:0.0} ГБ");

        if (!GitHubOk || !HuggingFaceOk)
        {
            sb.AppendLine();
            sb.AppendLine("Рекомендация: обнаружены проблемы с доступом к GitHub или Hugging Face (вероятно, провайдер/DPI режет SSL-соединение). Включи VPN / системный прокси Windows или скачай нужные файлы вручную через браузер по кнопкам ниже.");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed class HubAssistantService
{
    private static readonly HttpClient LlmHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly HttpClient ProbeHttp = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(6) })
    {
        Timeout = TimeSpan.FromSeconds(7)
    };

    private static readonly Regex ActionRegex = new(@"\[\[ACTION:([a-z_]+)(?:\|([^|\]]*))?(?:\|([^\]]*))?\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static HubAssistantService()
    {
        ProbeHttp.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAIHub/2.0");
    }

    public static string AssistantModelPath => ModelCatalog.ResolveLocalPath(ModelCatalog.AssistantSpec.Files[0]);
    public static bool IsAssistantModelInstalled() => File.Exists(AssistantModelPath) && new FileInfo(AssistantModelPath).Length > 500L * 1024 * 1024;
    public static string AssistantBaseUrl => $"http://127.0.0.1:{HubConfig.Current.AssistantPort}";

    public async Task<bool> IsAssistantServerRunningAsync() => await PingHealthAsync($"{AssistantBaseUrl}/health");
    public async Task<bool> IsMainLlmRunningAsync() => await PingHealthAsync($"http://127.0.0.1:{HubConfig.Current.QwenPort}/health");

    private static async Task<bool> PingHealthAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(1200);
            using var resp = await ProbeHttp.GetAsync(url, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static async Task StopAssistantServerAsync()
    {
        await Task.Run(() =>
        {
            int port = HubConfig.Current.AssistantPort;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -Command \"Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {{ Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(5000);
                }
                else
                {
                    var pids = new HashSet<int>();
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "lsof",
                            Arguments = $"-ti tcp:{port} -sTCP:LISTEN",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        };
                        using var p = Process.Start(psi);
                        if (p is not null)
                        {
                            string outText = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(3000);
                            foreach (string line in outText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                                if (int.TryParse(line.Trim(), out int pid) && pid > 1) pids.Add(pid);
                        }
                    }
                    catch { }

                    if (pids.Count == 0 && OperatingSystem.IsLinux())
                    {
                        try
                        {
                            var ssPsi = new ProcessStartInfo
                            {
                                FileName = "ss",
                                Arguments = "-tlnp",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                CreateNoWindow = true
                            };
                            using var sp = Process.Start(ssPsi);
                            if (sp is not null)
                            {
                                string ssOut = sp.StandardOutput.ReadToEnd();
                                sp.WaitForExit(3000);
                                foreach (int pid in AiServiceManager.ParseSsListeningPids(ssOut, [port]))
                                    pids.Add(pid);
                            }
                        }
                        catch { }
                    }

                    foreach (int pid in pids)
                    {
                        try { using var proc = Process.GetProcessById(pid); proc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
        });
    }

    public async Task<(string? baseUrl, string engineLabel)> EnsureChatEndpointAsync(
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // 1. Reuse already running main Qwen server (zero extra VRAM, instant response!)
        if (await IsMainLlmRunningAsync())
            return ($"http://127.0.0.1:{HubConfig.Current.QwenPort}", "Основная LLM (уже запущена)");

        // 2. Reuse already running dedicated compact Assistant server
        if (await IsAssistantServerRunningAsync())
            return (AssistantBaseUrl, "Qwen 3 4B Помощник");

        // 3. Start dedicated compact Qwen3-4B-Instruct assistant server if installed
        if (!RuntimeInstaller.IsLlamaInstalled() || !IsAssistantModelInstalled())
            return (null, "Офлайн RAG-режим (без LLM)");

        string exe = RuntimeInstaller.FindLlamaExePath();
        if (string.IsNullOrEmpty(exe))
            return (null, "Офлайн RAG-режим (без LLM)");

        onProgress?.Invoke("Запуск компактного AI-Помощника (Qwen 3 4B, ~2 сек)...");
        string logsDir = HubConfig.LlmLogsDir;
        Directory.CreateDirectory(logsDir);

        var args = new List<string>
        {
            "-m", AssistantModelPath,
            "--alias", "hub-assistant-4b",
            "--host", "127.0.0.1",
            "--port", HubConfig.Current.AssistantPort.ToString(),
            "--ctx-size", "8192",
            "--parallel", "1",
            "--fit", "on",
            "--fit-target", "512",
            "--flash-attn", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--jinja",
            "--reasoning", "off",
            "--temp", "0.4",
            "--top-p", "0.9",
            "--top-k", "20",
            "--presence-penalty", "1.2",
            "--repeat-penalty", "1.0",
            "--no-webui"
        };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        var linuxEnv = AiServiceManager.BuildLinuxLlamaEnv(exe);
        if (linuxEnv is not null)
            foreach (var kv in linuxEnv) psi.Environment[kv.Key] = kv.Value;

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить llama-server для Помощника.");
        string outLog = Path.Combine(logsDir, "assistant.out.log");
        string errLog = Path.Combine(logsDir, "assistant.err.log");
        _ = Task.Run(async () => { try { await using var fs = File.Create(outLog); await proc.StandardOutput.BaseStream.CopyToAsync(fs); } catch { } });
        _ = Task.Run(async () => { try { await using var fs = File.Create(errLog); await proc.StandardError.BaseStream.CopyToAsync(fs); } catch { } });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new InvalidOperationException($"Сервер Помощника завершился с кодом {proc.ExitCode}. Проверь {errLog}.");
            if (await IsAssistantServerRunningAsync())
                return (AssistantBaseUrl, "Qwen 3 4B Помощник");
            await Task.Delay(700, ct);
        }

        throw new TimeoutException("Компактный AI-Помощник не успел запуститься за 45 сек.");
    }

    public async Task<NetworkCheckResult> RunNetworkDiagnosticsAsync(HuggingFaceClient hfClient, double freeDiskGb, CancellationToken ct = default)
    {
        var ghTask = ProbeHostAsync("https://api.github.com/rate_limit", ct);
        var hfTask = ProbeHostAsync("https://huggingface.co/api/models?limit=1", ct);
        var authTask = hfClient.VerifyTokenAsync(null, ct);
        await Task.WhenAll(ghTask, hfTask, authTask);

        var (ghOk, ghMsg) = ghTask.Result;
        var (hfOk, hfMsg) = hfTask.Result;
        var auth = authTask.Result;
        var rt = RuntimeInstaller.GetStatus();

        return new NetworkCheckResult(
            ghOk, ghMsg,
            hfOk, hfMsg,
            auth.Connected, auth.Connected ? $"подключён (@{auth.Username})" : (auth.Error ?? "не задан"),
            rt.LlamaInstalled,
            rt.ComfyInstalled,
            freeDiskGb
        );
    }

    private static async Task<(bool ok, string detail)> ProbeHostAsync(string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await ProbeHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return (resp.IsSuccessStatusCode || (int)resp.StatusCode == 403,
                $"HTTP {(int)resp.StatusCode} ({sw.ElapsedMilliseconds} мс)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            string msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Length > 110) msg = msg[..110] + "...";
            return (false, $"{msg} ({sw.ElapsedMilliseconds} мс)");
        }
    }

    public async Task<AssistantReply> AskAsync(
        string userMessage,
        IReadOnlyList<(string Role, string Content)> history,
        HardwareProfile hw,
        bool hfConnected,
        string? hfUser,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        int ragTopK = 3)
    {
        var corpus = HubKnowledgeBase.BuildLiveKnowledgeBase(hw, hfConnected, hfUser);
        var hits = HubKnowledgeBase.Retrieve(userMessage, corpus, topK: ragTopK);
        // Always ensure live PC hardware & engine status is included once
        var pcChunk = corpus.FirstOrDefault(c => c.Id == "live-pc-state");
        if (pcChunk is not null && !hits.Any(h => h.Chunk.Id == "live-pc-state"))
            hits.Insert(0, new RagSearchResult(pcChunk, 99));

        var ragSources = hits.Select(h => h.Chunk.Title).ToList();

        var (baseUrl, engineLabel) = await EnsureChatEndpointAsync(onProgress, ct);
        if (baseUrl is null)
        {
            return BuildOfflineRagFallbackReply(userMessage, hw, hfConnected, hits, ragSources);
        }

        var ragContext = new StringBuilder();
        foreach (var h in hits)
        {
            ragContext.AppendLine($"### [{h.Chunk.Title}]");
            ragContext.AppendLine(h.Chunk.Content);
            ragContext.AppendLine();
        }

        string systemPrompt = """
            Ты — встроенный AI-Помощник приложения Brazhnichenko Hub (Windows / macOS Apple Silicon / Linux).
            Отвечай кратко, по делу, дружелюбно, на русском языке (3–12 строк, структурировано списком если предлагаешь варианты).
            Используй ТОЛЬКО факты, модели и статусы из блока БАЗА ЗНАНИЙ (RAG) ниже — не выдумывай несуществующие ID моделей!

            ГЛАВНЫЕ ПРАВИЛА:
            1. Когда пользователь хочет поставить модель для фото/видео/текста — посмотри его GPU и RAM в БАЗЕ ЗНАНИЙ, предложи 2–3 лучших варианта с размером и скоростью. Если модель помечена «УЖЕ УСТАНОВЛЕНА» — скажи об этом и НЕ предлагай кнопку её скачивания (вместо этого предложи открыть Студию, Чат или Сервисы). Если нужный движок (llama.cpp для текста, ComfyUI для фото/видео) НЕ установлен — предупреди об этом и добавь кнопку его установки!
            2. РАЗЛИЧАЙ генерацию картинок (вкладка «Фото»: FLUX 3, Z-Image, Qwen-Image) и Vision — анализ/понимание картинок в «Чат → Локальная LLM»! Если пользователь спрашивает про картинки В ЧАТЕ / Vision / ошибку `image input is not supported` / `mmproj` — объясни про файл зрения `mmproj-*.gguf`, предложи конкретные Vision-модели/дополнения из каталога (`llm-qwen36-35b-hauhau-vision-mmproj`, `llm-qwen25-vl-7b-vision`, `llm-gemma3-12b-vision`) и напомни перезапустить LLM после скачивания `mmproj`.
            3. Если модель Gated на Hugging Face (например LTX-2.5) и токен/лицензия не готовы — объясни про согласие на сайте HF и добавь кнопки для лицензии и настроек.
            4. Если пользователь жалуется на ошибки скачивания (SSL, таймаут, 0%, GitHub/HF не отвечает) — объясни вероятную причину (провайдер/DPI/нужен VPN), предложи кнопку проверки сети и прямую ссылку в браузер + нужную папку. НЕ добавляй кнопки про несвязанные модели!
            5. Если Qwen ходит по кругу — объясни причину и предложи кнопку профиля «Qwen Анти-луп».
            6. Кнопки действий пиши ТОЛЬКО отдельными строками в самом конце ответа (НИКОГДА не вставляй [[ACTION:...]] внутрь предложений!), СТРОГО в формате:
               [[ACTION:type|param|Текст кнопки]]
               Разрешённые type:
               - install_model | <точный ID модели из базы, например llm-qwen25-vl-7b-vision или llm-qwen36-35b-hauhau-vision-mmproj> | Скачать <короткое название>
               - install_runtime | llama или comfyui | Установить llama.cpp / ComfyUI
               - check_network | | Проверить сеть GitHub и HF
               - apply_antiloop | | Применить профиль Qwen Анти-луп
               - open_url | <https://...> | Открыть страницу ↗
               - open_tab | catalog_text / catalog_photo / catalog_video / hf_search / chat_llm / gallery / settings / services / studio / models | Открыть раздел
               - open_folder | llm_models / llm_runtime / comfy_models / media_root | Открыть папку

            === БАЗА ЗНАНИЙ (RAG, актуально на текущий момент) ===
            """ + "\n" + ragContext;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var (role, content) in history.TakeLast(6))
        {
            messages.Add(new { role, content });
        }
        messages.Add(new { role = "user", content = userMessage });

        int safeMaxTokens = HubConfig.Current.LlmReasoningBudget > 0
            ? Math.Max(1200, HubConfig.Current.LlmReasoningBudget + 1200)
            : 2600;
        var payload = new
        {
            messages,
            temperature = 0.35,
            top_p = 0.9,
            presence_penalty = 1.1,
            max_tokens = safeMaxTokens,
            chat_template_kwargs = new { enable_thinking = false }
        };

        onProgress?.Invoke($"AI-Помощник думает ({engineLabel})...");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        using var resp = await LlmHttp.SendAsync(req, ct);
        string raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ошибка ответа LLM (HTTP {(int)resp.StatusCode}): {raw[..Math.Min(200, raw.Length)]}");

        using var doc = JsonDocument.Parse(raw);
        var msgEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        string answer = msgEl.TryGetProperty("content", out var cEl) ? (cEl.GetString() ?? "") : "";

        // Strip any accidental <think>...</think> block if main thinking Qwen was reused
        answer = Regex.Replace(answer, @"<think>.*?</think>\s*", "", RegexOptions.Singleline).Trim();
        if (string.IsNullOrWhiteSpace(answer))
            answer = "⚠ Запущенная Thinking-модель потратила лимит на внутренние размышления и не успела вывести ответ. Повтори вопрос или останови основную LLM, чтобы Помощник ответил на быстрой Qwen 3 4B Instruct.";

        var (cleanText, actions) = ParseAndValidateActions(answer, userMessage);
        return new AssistantReply(cleanText, actions, engineLabel, ragSources);
    }

    public async Task<AssistantReply> ExplainActionFailureAsync(
        string actionDescription,
        string errorMessage,
        HardwareProfile hw,
        bool hfConnected,
        string? hfUser,
        CancellationToken ct = default)
    {
        string prompt =
            $"Только что пользователь попытался выполнить действие: «{actionDescription}», но получил ошибку:\n" +
            $"{errorMessage}\n\n" +
            $"Коротко (3–5 предложений) объясни простым человеческим языком, что пошло не так (например, сеть/DPI режет GitHub или HF и нужен VPN, либо нужно подтвердить лицензию на Hugging Face, либо не хватает места/движка), и дай 1–3 конкретные кнопки решения.";

        try
        {
            var reply = await AskAsync(prompt, [], hw, hfConnected, hfUser, null, ct, ragTopK: 2);
            // Guarantee direct helpful recovery buttons for SSL/network/gated errors
            var extra = BuildOfflineErrorDiagnosis(actionDescription, errorMessage).Actions;
            var existingKeys = reply.Actions.Select(a => $"{a.Type}|{a.Param}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in extra)
            {
                if (existingKeys.Add($"{a.Type}|{a.Param}") && reply.Actions.Count < 4)
                    reply.Actions.Add(a);
            }
            return reply;
        }
        catch
        {
            // Guaranteed helpful fallback even if LLM isn't running
            return BuildOfflineErrorDiagnosis(actionDescription, errorMessage);
        }
    }

    private static (string cleanText, List<AssistantAction> actions) ParseAndValidateActions(string rawAnswer, string userQuery)
    {
        var actions = new List<AssistantAction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract action tags line-by-line: if a tag stands alone on a line, remove it cleanly; if inline inside prose, replace with its readable label!
        var lines = rawAnswer.Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (ActionRegex.IsMatch(trimmed) && ActionRegex.Replace(trimmed, "").Trim().Length == 0)
            {
                foreach (Match m in ActionRegex.Matches(trimmed))
                    TryAddAction(m, actions, seen);
                continue;
            }

            string replaced = ActionRegex.Replace(line, m =>
            {
                string lbl = TryAddAction(m, actions, seen);
                return lbl;
            });
            outLines.Add(replaced);
        }

        string clean = Regex.Replace(string.Join('\n', outLines), @"\n{3,}", "\n\n").Trim();

        // Safety net: if model mentioned an UNINSTALLED catalog ID in text without emitting an action tag, add it!
        foreach (var spec in ModelCatalog.All)
        {
            if (clean.Contains(spec.Id, StringComparison.OrdinalIgnoreCase) &&
                !ModelCatalog.CheckInstallState(spec).allInstalled &&
                seen.Add($"install_model|{spec.Id}"))
            {
                actions.Add(new AssistantAction("install_model", spec.Id, $"Скачать {ShortModelLabel(spec)} ({spec.TotalSizeGB:0.##} ГБ)"));
            }
        }

        // Keep UI tidy (max 5 action buttons per reply)
        if (actions.Count > 5) actions = actions.Take(5).ToList();
        return (clean, actions);
    }

    private static string TryAddAction(Match m, List<AssistantAction> actions, HashSet<string> seen)
    {
        string type = m.Groups[1].Value.Trim().ToLowerInvariant();
        string param = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
        string label = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";
        if (string.IsNullOrWhiteSpace(label))
            label = DefaultActionLabel(type, param);

        if (!IsValidAction(type, param)) return "";
        // Never offer to download an already fully installed model
        if (type == "install_model" && ModelCatalog.FindById(param) is { } spec && ModelCatalog.CheckInstallState(spec).allInstalled)
            return "";
        // Never offer to install a runtime that's already installed
        if (type == "install_runtime" && ((param == "llama" && RuntimeInstaller.IsLlamaInstalled()) || (param == "comfyui" && RuntimeInstaller.IsComfyInstalled())))
            return "";

        string key = $"{type}|{param}";
        if (seen.Add(key))
            actions.Add(new AssistantAction(type, param, label));
        return label;
    }

    private static string DefaultActionLabel(string type, string param) => type switch
    {
        "install_model" => ModelCatalog.FindById(param) is { } sp ? $"Скачать {ShortModelLabel(sp)} ({sp.TotalSizeGB:0.##} ГБ)" : "Скачать модель",
        "install_runtime" => param == "comfyui" ? "Установить ComfyUI (~2.0 ГБ)" : "Установить llama.cpp (~620 МБ)",
        "check_network" => "Проверить сеть GitHub и HF",
        "apply_antiloop" => "Применить профиль Qwen Анти-луп",
        "open_url" => "Открыть страницу ↗",
        "open_folder" => param switch
        {
            "llm_models" => "Открыть папку LLM-моделей",
            "llm_runtime" => "Открыть папку llama.cpp",
            "comfy_models" => "Открыть папку моделей ComfyUI",
            _ => "Открыть папку MediaAI"
        },
        "open_tab" => param switch
        {
            "studio" => "Открыть Студию",
            "services" => "Открыть Сервисы и порты",
            "settings" => "Открыть Настройки и MCP",
            "models" => "Открыть Модели на диске",
            "catalog_photo" => "Открыть Каталог → Фото",
            "catalog_video" => "Открыть Каталог → Видео",
            "catalog_text" => "Открыть Каталог → Текст",
            "hf_search" => "Открыть Поиск Hugging Face",
            "chat_llm" => "Открыть Чат 2.0 (LLM)",
            "gallery" => "Открыть Галерею",
            _ => "Открыть раздел"
        },
        _ => "Выполнить"
    };

    private static bool IsValidAction(string type, string param) => type switch
    {
        "install_model" => ModelCatalog.FindById(param) is not null,
        "install_runtime" => param is "llama" or "comfyui",
        "check_network" or "apply_antiloop" => true,
        "open_url" => param.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
        "open_tab" => param is "catalog_text" or "catalog_photo" or "catalog_video" or "hf_search" or "chat_llm" or "gallery" or "settings" or "services" or "studio" or "models",
        "open_folder" => param is "llm_models" or "llm_runtime" or "comfy_models" or "media_root",
        _ => false
    };

    private static string ShortModelLabel(CatalogModelSpec spec)
    {
        int idx = spec.Name.IndexOf('(');
        string s = idx > 4 ? spec.Name[..idx].Trim() : spec.Name;
        return s.Length > 28 ? s[..28].Trim() + "…" : s;
    }

    private static AssistantReply BuildOfflineRagFallbackReply(
        string query,
        HardwareProfile hw,
        bool hfConnected,
        List<RagSearchResult> hits,
        List<string> ragSources)
    {
        string q = query.ToLowerInvariant();
        var rt = RuntimeInstaller.GetStatus();
        var sb = new StringBuilder();
        var actions = new List<AssistantAction>();

        sb.AppendLine($"(Работаю в быстром офлайн RAG-режиме по базе знаний Хаба — для полноценного живого диалога можно установить компактного помощника Qwen 3 4B, 2.33 ГБ.)");
        sb.AppendLine();

        if (q.Contains("mmproj") || q.Contains("vision") || q.Contains("вижн") ||
            (q.Contains("картинк") && (q.Contains("чат") || q.Contains("текст") || q.Contains("llm"))) ||
            q.Contains("image input"))
        {
            sb.AppendLine("Чтобы локальная LLM в **Чате 2.0** видела и анализировала картинки (Vision), ей нужен дополнительный файл зрения **`mmproj-*.gguf`** в папке LLM-моделей:");
            sb.AppendLine("1) Если у тебя уже стоит **Qwen 3.6 35B HauhauCS** — просто докачай к ней официальный Vision-модуль **mmproj (0.84 ГБ)** и перезапусти LLM.");
            sb.AppendLine("2) Или установи готовую Vision-модель с `mmproj` в комплекте: **Qwen 2.5 VL 7B Vision (5.16 ГБ)** или **Gemma 3 12B Vision (7.59 ГБ)**.");
            sb.AppendLine("После скачивания нажми «Остановить» и снова «Запустить LLM» — Хаб сам передаст `--mmproj`, а бейдж сменится на «Vision (mmproj)».");
            foreach (string id in new[] { "llm-qwen36-35b-hauhau-vision-mmproj", "llm-qwen25-vl-7b-vision", "llm-gemma3-12b-vision" })
            {
                if (ModelCatalog.FindById(id) is { } sp && !ModelCatalog.CheckInstallState(sp).allInstalled)
                    actions.Add(new("install_model", sp.Id, $"Скачать {ShortModelLabel(sp)} ({sp.TotalSizeGB:0.##} ГБ)"));
            }
            actions.Add(new("open_tab", "hf_search", "Поиск mmproj на Hugging Face"));
        }
        else if (q.Contains("ssl") || q.Contains("впн") || q.Contains("vpn") || q.Contains("сеть") || q.Contains("таймаут") || q.Contains("не кача"))
        {
            sb.AppendLine("Похоже на проблему с доступом к GitHub или Hugging Face (часто из-за провайдера/DPI или прокси).");
            sb.AppendLine("1. Запусти проверку сети — я покажу, какой именно хост режется.");
            sb.AppendLine("2. Если режется GitHub/HF — включи VPN или скачай архив вручную через браузер в нужную папку.");
            actions.Add(new("check_network", "", "Проверить сеть GitHub и HF"));
            actions.Add(new("open_url", RuntimeInstaller.ComfyReleasesUrl, "GitHub ComfyUI ↗"));
            actions.Add(new("open_url", RuntimeInstaller.LlamaReleasesUrl, "GitHub llama.cpp ↗"));
        }
        else if (q.Contains("401") || q.Contains("403") || q.Contains("gated") || q.Contains("лиценз") || q.Contains("токен") || q.Contains("подтверд"))
        {
            sb.AppendLine("Закрытые (Gated) модели Hugging Face вроде LTX-2.5 требуют 2 шага:");
            sb.AppendLine("1. Принять лицензию на странице модели («Agree and access repository»).");
            sb.AppendLine("2. Вставить Read-токен Hugging Face во вкладке «Настройки и MCP».");
            actions.Add(new("open_url", "https://huggingface.co/Lightricks/LTX-2.5", "Лицензия LTX-2.5 ↗"));
            actions.Add(new("open_url", "https://huggingface.co/settings/tokens", "Получить HF-токен ↗"));
            actions.Add(new("open_tab", "settings", "Открыть Настройки и MCP"));
        }
        else if (q.Contains("круг") || q.Contains("зацикл") || q.Contains("повтор") || q.Contains("loop"))
        {
            sb.AppendLine("Зацикливание Qwen решается профилем «Qwen Анти-луп» (presence_penalty=1.5, dry=0.5, лимит <think>=4096, --no-reasoning-preserve).");
            actions.Add(new("apply_antiloop", "", "Применить профиль Qwen Анти-луп"));
        }
        else if (q.Contains("объясн") || q.Contains("что делает") || q.Contains("настройк") || q.Contains("параметр") || q.Contains("shift") || q.Contains("lora") || q.Contains("лора") || q.Contains("seed") || q.Contains("хвост") || q.Contains("svi") || q.Contains("penalty") || q.Contains("dry"))
        {
            var topSettings = hits.Where(h => h.Chunk.Category == "settings").Take(2).ToList();
            if (topSettings.Count == 0)
                topSettings = hits.Take(2).ToList();
            foreach (var h in topSettings)
            {
                sb.AppendLine($"▸ {h.Chunk.Title}:");
                sb.AppendLine(h.Chunk.Content);
                sb.AppendLine();
            }
            actions.Add(new("open_tab", "settings", "Открыть Настройки"));
            actions.Add(new("open_folder", "comfy_models", "Открыть папку моделей/LoRA"));
        }
        else
        {
            string cat = (q.Contains("видео") || q.Contains("ролик") || q.Contains("wan") || q.Contains("ltx")) ? "video"
                : (q.Contains("фото") || q.Contains("картин") || q.Contains("flux") || q.Contains("zimage") || q.Contains("апскейл")) ? "photo"
                : "text";

            string catRu = cat == "photo" ? "Фото" : cat == "video" ? "Видео" : "Текстовых LLM";
            sb.AppendLine($"Под твой ПК ({hw.DetectedGpuName}, {hw.DetectedVramGB:0.#} ГБ VRAM, {hw.DetectedRamGB:0.#} ГБ RAM) для направления «{catRu}» лучше всего подойдут:");

            var best = ModelCatalog.All
                .Where(m => m.Category == cat && m.Id != ModelCatalog.AssistantModelId)
                .Select(m => (Spec: m, Fit: HardwareAdvisor.EvaluateModel(m, hw), State: ModelCatalog.CheckInstallState(m)))
                .OrderBy(x => x.Fit.Level)
                .ThenByDescending(x => x.Spec.IdealVramGB)
                .Take(3)
                .ToList();

            foreach (var (spec, fit, state) in best)
            {
                string st = state.allInstalled ? "уже установлена" : $"{spec.TotalSizeGB:0.##} ГБ";
                sb.AppendLine($"• {spec.Name} — {fit.BadgeText} ({st}, {fit.EstimatedSpeedHint})");
                if (!state.allInstalled)
                    actions.Add(new("install_model", spec.Id, $"Скачать {ShortModelLabel(spec)} ({spec.TotalSizeGB:0.##} ГБ)"));
            }

            if (cat == "text" && !rt.LlamaInstalled)
            {
                sb.AppendLine("\n⚠ Движок llama.cpp ещё не установлен — он нужен для запуска текстовых моделей.");
                actions.Insert(0, new("install_runtime", "llama", "Установить llama.cpp (~620 МБ)"));
            }
            else if (cat is "photo" or "video" && !rt.ComfyInstalled)
            {
                sb.AppendLine("\n⚠ Движок ComfyUI Portable ещё не установлен — без него фото/видео не запустятся.");
                actions.Insert(0, new("install_runtime", "comfyui", "Установить ComfyUI (~2.0 ГБ)"));
            }
        }

        if (!IsAssistantModelInstalled())
            actions.Add(new("install_model", ModelCatalog.AssistantModelId, "Установить Помощника Qwen 3 4B (2.33 ГБ)"));

        return new AssistantReply(sb.ToString().TrimEnd(), actions.Take(5).ToList(), "Офлайн RAG-режим (без LLM)", ragSources);
    }

    private static AssistantReply BuildOfflineErrorDiagnosis(string actionDesc, string errorMessage)
    {
        string err = errorMessage.ToLowerInvariant();
        var actions = new List<AssistantAction>();
        var sb = new StringBuilder();
        sb.AppendLine($"При выполнении «{actionDesc}» возникла ошибка: {errorMessage}");
        sb.AppendLine();

        if (err.Contains("ssl") || err.Contains("таймаут") || err.Contains("timeout") || err.Contains("зависл") || err.Contains("connection"))
        {
            sb.AppendLine("Скорее всего, интернет-провайдер или DPI блокирует/режет соединение с GitHub или Hugging Face.");
            sb.AppendLine("Что сделать: включи VPN (или системный прокси) и попробуй снова, либо скачай файл вручную через браузер.");
            actions.Add(new("check_network", "", "Проверить сеть GitHub и HF"));
            if (actionDesc.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new("open_url", RuntimeInstaller.ComfyReleasesUrl, "Скачать ComfyUI в браузере ↗"));
                actions.Add(new("open_folder", "media_root", "Открыть папку MediaAI"));
            }
            else if (actionDesc.Contains("llama", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new("open_url", RuntimeInstaller.LlamaReleasesUrl, "Скачать llama.cpp в браузере ↗"));
                actions.Add(new("open_folder", "llm_runtime", "Открыть папку runtime"));
            }
        }
        else if (err.Contains("401") || err.Contains("403") || err.Contains("gated") || err.Contains("лиценз"))
        {
            sb.AppendLine("Модель закрыта лицензией на Hugging Face (Gated). Нужно зайти на страницу модели, нажать «Agree and access repository» и подключить HF Read-токен в «Настройки и MCP».");
            actions.Add(new("open_tab", "settings", "Открыть Настройки и MCP"));
            actions.Add(new("open_url", "https://huggingface.co/settings/tokens", "Получить HF-токен ↗"));
        }
        else
        {
            sb.AppendLine("Проверь свободное место на диске и сетевое соединение.");
            actions.Add(new("check_network", "", "Запустить диагностику сети"));
        }

        return new AssistantReply(sb.ToString().TrimEnd(), actions, "Диагностика RAG", ["Диагностика ошибок"]);
    }
}
