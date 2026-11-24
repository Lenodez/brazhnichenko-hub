using System.Text;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public sealed record KnowledgeChunk(
    string Id,
    string Category,
    string Title,
    string Content,
    string[] Keywords
);

public sealed record RagSearchResult(KnowledgeChunk Chunk, double Score);

/// <summary>
/// Dynamic in-app RAG knowledge base for the Local AI Hub Assistant.
/// Indexes live PC hardware, all 30 NVIDIA GPUs (GTX 10 -> RTX 50), all curated models across Text/Photo/Video
/// with live fit & install status, engine runtimes, and troubleshooting runbooks (SSL/VPN, HF Gated 403, Qwen loops, VRAM/RAM).
/// Uses fast zero-dependency BM25 + Russian/English synonym expansion.
/// </summary>
public static class HubKnowledgeBase
{
    private static readonly Dictionary<string, string[]> SynonymGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vision"] = ["вижн", "mmproj", "зрение", "мультимодал", "vl", "опиши", "image", "input", "скриншот", "анализ", "видеть"],
        ["фото"] = ["photo", "изображен", "рисова", "арт", "портрет", "апскейл", "upscale", "flux", "zimage", "qwen-image", "t2i", "i2i"],
        ["видео"] = ["video", "ролик", "клип", "анимац", "мультик", "кино", "wan", "ltx", "svi", "t2v", "i2v"],
        ["текст"] = ["llm", "чат", "chat", "код", "qwen", "gemma", "gguf", "текстов", "языков", "разговор", "llama", "сесси", "персон"],
        ["голос"] = ["микрофон", "stt", "tts", "whisper", "piper", "озвучк", "дикт", "голосов", "аудио", "речь", "ffmpeg"],
        ["студия"] = ["маск", "inpaint", "инпейнт", "сравнен", "шторк", "вариац", "батч", "сетк", "пресет", "очеред", "истор"],
        ["галерея"] = ["избранн", "метаданн", "корзин", "превью", "кэш", "лайтбокс"],
        ["установ"] = ["скача", "поставить", "постав", "загрузи", "install", "download", "добав"],
        ["ошибк"] = ["сломал", "проблем", "падает", "сбой", "error", "fail", "timeout", "таймаут", "500", "crash", "краш"],
        ["сеть"] = ["ssl", "впн", "vpn", "прокси", "proxy", "github", "интернет", "блокир", "dpi", "соединен", "connection"],
        ["hugging"] = ["hf", "токен", "token", "401", "403", "gated", "лиценз", "соглас", "подтверд", "доступ", "закрыт"],
        ["движок"] = ["runtime", "llama.cpp", "llama-server", "comfy", "comfyui", "cuda", "рантайм"],
        ["цикл"] = ["круг", "повтор", "зацикл", "loop", "бесконечн", "think", "penalty", "dry", "антилуп", "анти-луп"],
        ["видеокарт"] = ["gpu", "vram", "память", "ram", "озу", "rtx", "gtx", "nvidia", "желез", "спек", "вики", "wiki", "влезет", "потянет", "график", "sparkline", "простой", "автовыгруз"],
        ["apple"] = ["mac", "macos", "мак", "макбук", "macbook", "silicon", "metal", "mps", "unified", "единая"],
        ["linux"] = ["линукс", "ubuntu", "debian", "fedora", "arch", "rocm", "vulkan", "espeak", "desktop", "appimage", "wayland"],
        ["mcp"] = ["claude", "cursor", "antigravity", "подписочн", "подключ", "json"],
        ["бэкап"] = ["резервн", "экспорт", "импорт", "профил", "перенос", "zip", "обновлен", "сервер", "vps"],
        ["удал"] = ["очист", "место", "диск", "delete", "remove", "освобод"],
        ["настройк"] = ["параметр", "тюнинг", "shift", "seed", "lora", "лора", "strength", "контекст", "ctx", "температур", "хвост", "tail", "svi", "мост", "bridge", "референс", "i2v", "t2v"]
    };

    public static List<KnowledgeChunk> BuildLiveKnowledgeBase(HardwareProfile hw, bool hfConnected, string? hfUser)
    {
        var chunks = new List<KnowledgeChunk>();
        var rt = RuntimeInstaller.GetStatus();
        var voiceSt = LocalVoiceService.GetStatus();

        // Discover installed mmproj files so the assistant knows live Vision readiness!
        List<string> installedMmprojs = [];
        try
        {
            if (System.IO.Directory.Exists(HubConfig.LlmModelsDir))
            {
                installedMmprojs = System.IO.Directory.GetFiles(HubConfig.LlmModelsDir, "*.gguf", System.IO.SearchOption.TopDirectoryOnly)
                    .Select(System.IO.Path.GetFileName)
                    .Where(n => n is not null && n.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                    .Cast<string>()
                    .ToList();
            }
        }
        catch { }

        string mmprojStatus = installedMmprojs.Count > 0
            ? $"УСТАНОВЛЕНЫ ({string.Join(", ", installedMmprojs)})"
            : "НЕТ ни одного файла mmproj-*.gguf (поэтому картинки в Чате 2.0 пока выдают ошибку 500 — нужно скачать mmproj или Vision-модель из Каталога)";

        int idleMin = HubConfig.Current.LlmIdleUnloadMinutes;
        string idleStatus = idleMin > 0 ? $"включена (через {idleMin} мин простоя)" : "выключена (0 мин)";

        // 1. Live PC / Mac state & capabilities
        string hwDetails = hw.IsAppleSilicon
            ? $"Чип: {hw.DetectedGpuName} (Apple Silicon Unified Memory {hw.DetectedRamGB:0.#} ГБ, доступно Metal GPU ≈ {hw.DetectedVramGB:0.#} ГБ). " +
              (hw.MatchedAppleChip is not null
                  ? $"Профиль Apple Silicon Вики: {hw.MatchedAppleChip.ChipName} (поколение {hw.MatchedAppleChip.Generation} {hw.MatchedAppleChip.Tier}, {hw.MatchedAppleChip.GpuCores}, ПСП {hw.MatchedAppleChip.BandwidthGBs} ГБ/с, Dynamic Caching={hw.MatchedAppleChip.HasDynamicCaching}, GPU Neural Accel={hw.MatchedAppleChip.HasNeuralGpuAccel}). Совет по форматам: {hw.MatchedAppleChip.BestFormatHint}. "
                  : "Профиль Apple Silicon (Metal). ")
            : $"GPU: {hw.DetectedGpuName} ({hw.DetectedVramGB:0.#} ГБ VRAM). " +
              (hw.MatchedWiki is not null
                  ? $"Профиль Вики: {hw.MatchedWiki.ModelName} ({hw.MatchedWiki.Architecture}, серия {hw.MatchedWiki.Series}, FP16={hw.MatchedWiki.NativeFp16}, BF16={hw.MatchedWiki.NativeBf16}, FP8={hw.MatchedWiki.NativeFp8}, FP4={hw.MatchedWiki.NativeFp4}). Совет по форматам: {hw.MatchedWiki.BestFormatHint}. "
                  : "Точный профиль NVIDIA не определён (доступен CPU/Vulkan). ");

        chunks.Add(new KnowledgeChunk(
            "live-pc-state",
            "hardware",
            hw.IsAppleSilicon ? "Текущий Mac (Apple Silicon), движки, Vision (mmproj) и модули" : "Текущий ПК, движки, Vision (mmproj) и модули",
            $"Brazhnichenko Hub v{AppUpdateService.CurrentVersion}. " +
            hwDetails +
            $"ОЗУ: {hw.DetectedRamGB:0.#} ГБ ({hw.RamTierLabel}). Свободно на диске {HubConfig.DataDriveLetter}: {hw.FreeDiskGB:0.#} ГБ. " +
            $"Движок llama.cpp (для текстовых и Vision LLM): {(rt.LlamaInstalled ? "УСТАНОВЛЕН" : "НЕ УСТАНОВЛЕН (install_runtime|llama)")}. " +
            $"Движок ComfyUI (для фото и видео): {(rt.ComfyInstalled ? "УСТАНОВЛЕН" : "НЕ УСТАНОВЛЕН (install_runtime|comfyui)")}. " +
            $"Vision-модули (mmproj для картинок в Чате): {mmprojStatus}. " +
            $"Локальный голос: Whisper STT (микрофон) = {(voiceSt.WhisperReady ? "УСТАНОВЛЕН" : "не установлен (Система → Локальный голос)")}; " +
            $"Piper HD TTS = {(voiceSt.PiperReady ? "УСТАНОВЛЕН" : "не установлен, но встроенные офлайн-голоса ОС уже работают")}; " +
            $"FFmpeg = {(voiceSt.FfmpegReady ? "УСТАНОВЛЕН" : "не найден")}. " +
            $"Авто-выгрузка LLM из памяти при простое: {idleStatus}. " +
            $"Hugging Face токен: {(hfConnected ? $"подключён (@{hfUser})" : "НЕ подключён (нужен для закрытых моделей вроде LTX-2.5)")}.",
            ["пк", "mac", "macos", "apple", "железо", "gpu", "vram", "metal", "ram", "диск", "статус", "движок", "hf", "что установлено", "mmproj", "vision", "whisper", "piper", "голос"]
        ));

        // 2. Curated models grouped by 3 directions + individual chunks for precise RAG retrieval
        foreach (var cat in new[] { ("text", "Текстовые модели (LLM)"), ("photo", "Модели для Фото и апскейла"), ("video", "Модели для Видео и звука") })
        {
            var modelsInCat = ModelCatalog.All
                .Where(m => m.Category == cat.Item1)
                .Select(m => (Spec: m, Fit: HardwareAdvisor.EvaluateModel(m, hw), State: ModelCatalog.CheckInstallState(m)))
                .OrderBy(x => x.Fit.Level)
                .ThenByDescending(x => x.Spec.IdealVramGB)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Направление «{cat.Item2}» (оценка под текущий ПК {hw.DetectedGpuName} + {hw.DetectedRamGB:0.#} ГБ RAM):");
            string neededRuntime = cat.Item1 == "text"
                ? (rt.LlamaInstalled ? "llama.cpp установлен" : "ВНИМАНИЕ: движок llama.cpp НЕ установлен — предложи установить его вместе с моделью!")
                : (rt.ComfyInstalled ? "ComfyUI установлен" : "ВНИМАНИЕ: движок ComfyUI Portable НЕ установлен — без него фото/видео не запустятся!");
            sb.AppendLine($"Статус движка: {neededRuntime}");

            foreach (var (spec, fit, state) in modelsInCat)
            {
                string instStr = state.allInstalled
                    ? "УЖЕ УСТАНОВЛЕНА"
                    : state.presentCount > 0 ? $"частично ({state.presentCount}/{state.totalCount}, докачать {state.missingGB:0.##} ГБ)" : $"не установлена ({spec.TotalSizeGB:0.##} ГБ)";
                string gatedStr = spec.IsGated ? $" | GATED HF (нужно согласие на https://huggingface.co/{spec.Files[0].RepoId} + HF токен)" : "";
                sb.AppendLine($"- ID={spec.Id} | {spec.Name} | Размер: {spec.TotalSizeGB:0.##} ГБ | Совместимость: {fit.BadgeText} ({fit.ReasonRu}) | Скорость: {fit.EstimatedSpeedHint} | Статус: {instStr}{gatedStr}. Назначение: {spec.SummaryRu}");
            }

            chunks.Add(new KnowledgeChunk(
                $"catalog-{cat.Item1}",
                "catalog",
                cat.Item2,
                sb.ToString(),
                cat.Item1 == "text"
                    ? ["текст", "llm", "чат", "qwen", "gemma", "код", "модель", "установить"]
                    : cat.Item1 == "photo"
                        ? ["фото", "картинка", "изображение", "flux", "zimage", "qwen-image", "апскейл", "ultrasharp", "модель", "установить"]
                        : ["видео", "ролик", "wan", "ltx", "svi", "звук", "анимация", "модель", "установить"]
            ));
        }

        // 3. NVIDIA GPU Wiki summary by series (GTX 10 -> RTX 50)
        foreach (var series in new[] { "10", "16/20", "30", "40", "50" })
        {
            var gpus = HardwareAdvisor.GpuWiki.Where(g => g.Series == series).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Вики NVIDIA серия {series}:");
            foreach (var g in gpus)
            {
                sb.AppendLine($"- {g.ModelName} ({g.Architecture}, {g.VramGB} ГБ VRAM, FP8={g.NativeFp8}, FP4={g.NativeFp4}, {g.TierLabel}): {g.SummaryRu}");
            }
            chunks.Add(new KnowledgeChunk(
                $"wiki-nvidia-{series}",
                "wiki",
                $"NVIDIA GPU Вики — серия {series}",
                sb.ToString(),
                ["nvidia", "вики", "wiki", "видеокарта", "gpu", $"серия {series}", ..gpus.Select(g => g.ModelName.ToLowerInvariant())]
            ));
        }

        // 3b. Apple Silicon Wiki summary by generation (M1 -> M6, Unified Memory & Metal)
        foreach (var gen in new[] { "M1", "M2", "M3", "M4", "M5", "M6" })
        {
            var chips = HardwareAdvisor.AppleSiliconWiki.Where(c => c.Generation == gen).ToList();
            if (chips.Count == 0) continue;
            var sb = new StringBuilder();
            sb.AppendLine($"Вики Apple Silicon поколение {gen} (macOS Metal / Unified Memory):");
            sb.AppendLine("Общее правило macOS: GPU Metal получает ~72–80% от Unified Memory (8 ГБ → ~5.5 ГБ, 16 ГБ → ~11.5 ГБ, 24 ГБ → ~18 ГБ, 32 ГБ → ~24 ГБ, 36 ГБ → ~27 ГБ, 48 ГБ → ~36 ГБ, 64 ГБ → ~48 ГБ, 128 ГБ → ~102 ГБ). Скорость токенов прямо зависит от ПСП (ГБ/с).");
            foreach (var c in chips)
            {
                string rams = string.Join("/", c.RamOptionsGB);
                sb.AppendLine($"- {c.ChipName} ({c.GpuCores}, ПСП {c.BandwidthGBs} ГБ/с, память {rams} ГБ, DynamicCaching={c.HasDynamicCaching}, NeuralGpuAccel={c.HasNeuralGpuAccel}): {c.SummaryRu}");
            }
            chunks.Add(new KnowledgeChunk(
                $"wiki-apple-{gen.ToLowerInvariant()}",
                "wiki",
                $"Apple Silicon Вики — поколение {gen} (macOS Metal)",
                sb.ToString(),
                ["apple", "mac", "macos", "мак", "макбук", "silicon", "metal", "unified", "вики", "wiki", gen.ToLowerInvariant(), ..chips.Select(c => c.ChipName.ToLowerInvariant())]
            ));
        }

        // 4. Troubleshooting & Operations Runbooks (the heart of helpful diagnostics!)
        chunks.Add(new KnowledgeChunk(
            "troubleshoot-network-ssl-vpn",
            "troubleshooting",
            "Ошибка сети, SSL, таймаут при скачивании с GitHub или Hugging Face",
            "Причины ошибок «The SSL connection could not be established», «Таймаут 20 сек», «0% на скачивании»: " +
            "1) Интернет-провайдер или DPI блокирует/режет GitHub (api.github.com / objects.githubusercontent.com) или Hugging Face (huggingface.co). " +
            "2) Активен несовместимый VPN/прокси или антивирус с HTTPS-проверкой. " +
            "Как помочь пользователю: " +
            "а) Предложи кнопку проверки сети (action: check_network). " +
            "б) Посоветуй включить или переключить VPN (или системный прокси Windows). " +
            "в) Если автоматическая загрузка не проходит — предложи скачать файл вручную в браузере (с VPN) и положить в нужную папку: " +
            $"ComfyUI → {HubConfig.MediaRoot} (ссылка: {RuntimeInstaller.ComfyReleasesUrl}, папка: media_root), " +
            $"llama.cpp → {HubConfig.LlmRuntimeDir} (ссылка: {RuntimeInstaller.LlamaReleasesUrl}, папка: llm_runtime), " +
            $"LLM модели → {HubConfig.LlmModelsDir} (папка: llm_models), Фото/Видео модели → {HubConfig.ComfyModelsDir} (папка: comfy_models). " +
            "При обрыве частичный .part-файл сохраняется, повторная попытка продолжит докачку.",
            ["ssl", "сеть", "vpn", "впн", "таймаут", "timeout", "github", "не качается", "зависло", "0%", "ошибка", "блокировка", "прокси", "вручную"]
        ));

        chunks.Add(new KnowledgeChunk(
            "troubleshoot-hf-gated-403",
            "troubleshooting",
            "Закрытая модель Hugging Face (HTTP 401 / 403, Gated, лицензия, токен)",
            "Некоторые модели на Hugging Face (например, Lightricks/LTX-2.5 для видео со звуком) помечены как Gated (требуют согласия с лицензией). " +
            "Если возникает HTTP 401 или 403: " +
            "Шаг 1: Зайти в аккаунт на huggingface.co, открыть страницу модели (https://huggingface.co/Lightricks/LTX-2.5) и нажать кнопку «Agree and access repository». " +
            "Шаг 2: Создать Read-токен на https://huggingface.co/settings/tokens. " +
            "Шаг 3: Вставить токен в Local AI Hub во вкладке «Система → Настройки, HF и MCP» (open_tab|settings) в разделе «1. Подключение к Hugging Face» и нажать «Подключить и проверить».",
            ["401", "403", "gated", "лицензия", "согласие", "подтвердить", "токен", "token", "hugging", "hf", "ltx", "доступ", "закрыт"]
        ));

        chunks.Add(new KnowledgeChunk(
            "troubleshoot-missing-runtime",
            "troubleshooting",
            "Не установлен движок llama.cpp или ComfyUI Portable",
            "Скачанные веса (.gguf для текста или .safetensors для фото/видео) сами по себе не запускаются без движка: " +
            "- Для текстовых LLM (Qwen, Gemma) нужен движок llama.cpp (NVIDIA CUDA 13.5 для RTX 50-серии Blackwell, CUDA 12.4 для GTX 10–RTX 40, ~620–715 МБ; action: install_runtime|llama). " +
            "- Для Фото, Видео, Студии и Историй нужен ComfyUI Portable (NVIDIA CUDA 12.8+, ~2.0 ГБ + автоустановка ноды SVI 2.0 Pro; action: install_runtime|comfyui). " +
            "Сразу после установки движка можно запускать скачанные модели.",
            ["движок", "runtime", "llama-server", "llama.cpp", "comfyui", "не найден", "не запускается", "студия"]
        ));

        chunks.Add(new KnowledgeChunk(
            "troubleshoot-qwen-loop",
            "troubleshooting",
            "Qwen ходит по кругу, зацикливается или бесконечно думает",
            "Зацикливание в Qwen 3 / 3.6 / 3.8 (особенно в кванте Q3_K_P) возникает из-за низкого presence_penalty (0.0), безлимитного бюджета размышлений <think> и сохранения старых <think> в истории чата. " +
            "Решение в Local AI Hub: применить встроенный профиль «Qwen Анти-луп» (temp=1.0, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=1.5, dry_multiplier=0.5, reasoning_budget=4096, --no-reasoning-preserve + --webui-config-file; action: apply_antiloop). " +
            "После этого нужно перезапустить LLM. Если пользователь раньше вручную менял настройки в шестерёнке браузерного чата :18080 — нажать там Reset.",
            ["круг", "зацикливание", "зацикливается", "повторяет", "loop", "думает", "think", "penalty", "dry", "qwen", "антилуп"]
        ));

        chunks.Add(new KnowledgeChunk(
            "troubleshoot-disk-vram-mcp",
            "troubleshooting",
            "Нехватка места на диске, VRAM/RAM, удаление моделей, MCP-сервер",
            "1) Если мало места на диске: можно удалить ненужные модели кнопкой «Удалить» в «Модели и GPU» или в подразделе «На диске» (open_tab|models). Общие файлы (UMT5, VAE) при удалении сохраняются автоматически. " +
            "2) Если мало VRAM (6–8 ГБ): выбирай Qwen 3 8B (4.68 ГБ) или Qwen 3 4B (2.33 ГБ), для фото FLUX 3 Klein / 4x-UltraSharp, для видео при 8–12 ГБ и 32 ГБ RAM — Wan 2.2 GGUF Q4. " +
            "3) Подключение подписочной нейросети (Claude Desktop, ChatGPT, Antigravity, Cursor) по MCP: во вкладке «Система → Настройки, HF и MCP» (open_tab|settings) есть готовый JSON и кнопки авто-подключения (13 инструментов управления Хабом).",
            ["место", "диск", "удалить", "vram", "память", "ram", "mcp", "claude", "antigravity", "cursor", "настройки"]
        ));

        chunks.Add(new KnowledgeChunk(
            "settings-photo-guide",
            "settings",
            "Настройки вкладки Фото: модели, Seed, Референс, Доп. LoRA и Апскейл",
            "Объяснение всех параметров во вкладке «🖼 Фото»: " +
            "• Модели фото: Z-Image Turbo — самое быстрое фото с нуля (8 шагов, ~10 с, без референса); FLUX 3 Klein — лучший универсал для фотореализма, 3D-мультиков и правок по референсу (16 шагов); Qwen-Image 2.1 — лучший выбор для чёткого текста/надписей на картинке и дизайна. " +
            "• Seed (-1 = случайный): число-зерно генерации. -1 даёт каждый раз новую картинку; если кадр удался, нажми «Повторить последний Seed» и подправь детали в промпте. " +
            "• Референс / фото для правки: при выборе фото модель автоматически переключается на FLUX 3 Klein и редактирует/переносит персонажа или стиль с этого кадра. " +
            "• Доп. LoRA + сила: позволяет подключить любой свой .safetensors из папки ComfyUI\\models\\loras (стили, персонажи, освещение). Рекомендуемая сила: 0.6–0.85 (выше 1.0 может пережарить картинку). " +
            "• Кнопка «✨ Улучшить Режиссёром»: переводит русскую идею на английский и дописывает камеру, свет и фактуру под выбранную модель. " +
            "• Апскейл ×4: модель 4x-UltraSharp увеличивает разрешение и резкость любой картинки в 4 раза (до 4K).",
            ["настройки", "фото", "seed", "сид", "референс", "lora", "лора", "сила", "flux", "z-image", "qwen-image", "апскейл", "что делает", "объясни"]
        ));

        chunks.Add(new KnowledgeChunk(
            "settings-video-guide",
            "settings",
            "Настройки вкладки Видео: Wan 2.2, LTX-2.5, I2V, First→Last, SVI, Мост FLUX 3, LoRA High/Low",
            "Объяснение всех параметров во вкладке «🎬 Видео»: " +
            "• Модели видео: Wan 2.2 14B FP8 — главный кино-движок на RTX 40/50 (DynamicVRAM, 4 шага); Wan 2.2 Q4/Q6 GGUF — компактные версии для RTX 20/30; LTX-2.5 — генерирует видео сразу со синхронным звуком (эффекты, атмосфера, речь). " +
            "• Стартовое фото (I2V): если выбрать картинку, модель оживит именно её; без фото работает генерация с нуля по тексту (T2V). " +
            "• Конечный кадр (First→Last): плавно приведёт видео от стартового фото к заданному финальному кадру. " +
            "• Режимы Видео-истории: SVI 2.0 Pro — бесшовное продолжение сцены по последним кадрам без потери персонажа; Мост FLUX 3 — рисует новый промежуточный ключевой кадр, когда резко меняется ракурс или локация; Цикл — замыкает финал обратно в Шаг 0. " +
            "• Доп. LoRA High и Low (для Wan 2.2): у Wan 2.2 две нейросети — High Noise задаёт общую композицию и движение, Low Noise — мелкие детали и текстуры. Поэтому комьюнити-LoRA под Wan 2.2 часто идут парой (High и Low). С нашим ускорителем Lightning (4 шага) оптимальная сила сторонних LoRA — 0.6–0.85. Для LTX-2.5 используется поле High LoRA.",
            ["настройки", "видео", "wan", "ltx", "i2v", "t2v", "first", "last", "svi", "мост", "bridge", "lora", "лора", "high", "low", "сила", "что делает", "объясни"]
        ));

        chunks.Add(new KnowledgeChunk(
            "settings-tuning-guide",
            "settings",
            "Тюнинг параметров LLM и Видео (Система → Настройки, HF и MCP)",
            "Подробное объяснение всех технических параметров тюнинга: " +
            "• LLM Контекст (0 = авто): максимальная длина памяти диалога в токенах (например 32768 или 65536). Больше контекст = больше расход VRAM. 0 включает оптимальный авто-подбор. " +
            "• LLM Запас VRAM МиБ (0 = авто): сколько видеопамяти оставлять свободной системе (например 512 или 1024 МиБ), чтобы Windows не тормозила. " +
            "• LLM Температура (реком. 1.0 для Qwen 3/3.8): креативность ответов. Ниже (0.3–0.6) — строже и точнее для кода; 1.0 в паре с top_p=0.95 — оптимум Qwen. " +
            "• Presence Penalty (анти-луп, реком. 1.5): главный штраф за повторение уже сказанных мыслей — не даёт Qwen ходить по кругу. " +
            "• DRY Multiplier (анти-повтор, реком. 0.5): алгоритм Don't Repeat Yourself, обрубает повторяющиеся фразы, не ломая синтаксис кода. " +
            "• Лимит <think> (реком. 4096): максимум токенов на внутренние размышления модели. 0 — отключить <think> (быстрые прямые ответы), -1 — безлимит. " +
            "• Wan 2.2 Shift (дефолт 5.0, диапазон 2–10): сдвиг расписания шума ModelSamplingSD3. Выше (6–8) — более резкая и контрастная композиция; ниже (3–5) — мягче и плавнее движение. " +
            "• SVI Lightning High (дефолт 0.85): сила ускорителя Lightning на High-ветке при связке с SVI 2.0 Pro (чуть ниже 1.0 даёт более стабильного персонажа без артефактов). " +
            "• SVI Хвост (кадров, дефолт 5): сколько последних кадров прошлого клипа подаётся в SVI для сохранения инерции движения (5 — плавнее движение, 1 — проще резко поменять позу).",
            ["тюнинг", "настройки", "контекст", "ctx", "запас", "fit", "температура", "presence", "penalty", "dry", "think", "shift", "lightning", "svi", "хвост", "tail", "что делает", "объясни"]
        ));

        // 5. Full v3.0 – v4.1 Feature Guides (Chat 2.0 + Vision mmproj, Studio Pro, Gallery, Local Voice, Smart GPU & Backup)
        chunks.Add(new KnowledgeChunk(
            "chat-vision-mmproj-guide",
            "chat-vision",
            "Картинки в Чате 2.0 (Vision / mmproj): какие текстовые LLM видят фото и как это включить",
            "ВАЖНО: Не путай генерацию картинок (вкладка «Фото») и анализ/понимание картинок текстовой LLM в «Чат → Локальная LLM» (Vision)! " +
            "В Brazhnichenko Hub 4.x встроенный Чат 2.0 поддерживает отправку картинок (кнопка скрепки/картинки, Drag & Drop или Ctrl+V), НО для этого у выбранной LLM должен быть установлен дополнительный файл зрения `mmproj-*.gguf` в папке LLM-моделей (" + HubConfig.LlmModelsDir + "). " +
            "Если при отправке фото появляется ошибка `HTTP 500: image input is not supported ... provide the mmproj` или бейдж сверху показывает «Только текст»: " +
            "• Вариант 1 (самый быстрый, если уже стоит Qwen 3.6 35B HauhauCS): скачать официальный файл зрения `mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf` (всего 0.84 ГБ, ID=llm-qwen36-35b-hauhau-vision-mmproj) — и перезапустить LLM! " +
            "• Вариант 2 (готовый лёгкий комплект Vision, 5.16 ГБ): установить `Qwen 2.5 VL 7B Vision` (ID=llm-qwen25-vl-7b-vision) — скачивает сразу и модель, и её mmproj; отлично распознаёт фото, скриншоты и текст (OCR) даже на 8–12 ГБ VRAM. " +
            "• Вариант 3 (от Google, 7.59 ГБ): установить `Gemma 3 12B Vision` (ID=llm-gemma3-12b-vision) — скачивает модель + mmproj комплектом, шикарно работает на 12–16 ГБ VRAM. " +
            "• Вариант 4 (вручную через Поиск HF): открыть «Модели и GPU → Поиск на Hugging Face» (open_tab|hf_search), найти нужную VL/Vision GGUF модель и скачать оттуда оба файла: саму модель и файл `mmproj-*.gguf` в папку LLM. " +
            "Как только подходящий `mmproj-*.gguf` лежит в папке моделей, Хаб САМ подставляет флаг `--mmproj` при запуске llama-server, а бейдж в Чате меняется на голубой «Vision (mmproj)». Обязательно нажми «Остановить» и снова «Запустить LLM», если сервер был запущен до скачивания mmproj!",
            ["картинки", "картинка", "фото в чат", "чат", "vision", "вижн", "mmproj", "image input is not supported", "500", "мультимодальная", "опиши фото", "что на фото", "зрение", "vl", "текстовые модели", "поддерживают картинки"]
        ));

        chunks.Add(new KnowledgeChunk(
            "chat2-features-guide",
            "chat",
            "Встроенный Чат LLM 2.0: мультисессии, персоны, температура, потоковый SSE и <think>",
            "Во вкладке «Чат → Локальная LLM» работает нативный потоковый (SSE) чат без браузера: " +
            "• Мультисессии (левая панель, сворачивается кнопкой сайдбара): создание новых чатов («+ Новый чат»), поиск по заголовкам и тексту сообщений, переименование и удаление. История сохраняется в chat_sessions.json. " +
            "• Персоны (системные промпты): Обычный ассистент, Программист, Сценарист / Промпт-инженер, Переводчик RU↔EN, Кратко и по делу. " +
            "• Быстрая температура (0.00–1.50): регулируется ползунком прямо в шапке чата и сохраняется отдельно для каждого диалога. " +
            "• Потоковый вывод: живой счётчик скорости `tok/s`, кнопка «Стоп», сворачиваемый блок «Размышления (<think>)» для Thinking-моделей, рендер Markdown и кнопка «Копировать». " +
            "• Поддерживает Vision (при наличии mmproj) и голосовой ввод кнопкой микрофона (Whisper STT).",
            ["чат", "локальная llm", "мультисессии", "диалоги", "персона", "температура", "sse", "стриминг", "tok/s", "think", "размышления", "новый чат"]
        ));

        chunks.Add(new KnowledgeChunk(
            "studio-pro-features-guide",
            "studio",
            "Студия Pro (Фото и Видео): батч ×4 + сетка 2×2, пресеты, очередь, До/После, маска Inpainting",
            "Продвинутые функции вкладок «Фото» и «Видео»: " +
            "• Батч-вариации фото (×1 / ×2 / ×4): генерация до 4 вариантов одного промпта подряд с полоской миниатюр и полноэкранной кнопкой «Сетка 2×2» для сравнения вариантов рядом с их Seed. " +
            "• Слайдер «До / После»: появляется в превью Фото после Апскейла ×4 или правки по референсу — интерактивная перетаскиваемая шторка с размерами кадров. " +
            "• Inpainting (рисование маски кистью): при выборе референс-фото появляется кнопка «Маска» — открывается редактор (кисть, ластик, размер, Ctrl+Z, инвертировать, очистить). Изменяются только закрашенные области с мягким растушёванным краем (работает на FLUX 3, Qwen-Image 2.1 и Z-Image). " +
            "• Пресеты и Доп. LoRA: для компактности спрятаны в сворачиваемом блоке «Доп. LoRA и пресеты» (в Видео туда же убран «Конечный кадр»). Можно применять встроенные пресеты и сохранять свои. " +
            "• Фоновая очередь («+ В очередь»): ставь подряд задачи Фото и Видео, управляй ими на сворачиваемой нижней панели (Старт / Пауза / Список / Очистить) — очередь сохраняется между перезапусками. " +
            "• История промптов («История»): недавние и избранные промпты отдельно для Фото и Видео, плюс кнопка микрофона для диктовки идеи голосом.",
            ["студия", "батч", "вариации", "сетка", "2x2", "до после", "сравнение", "маска", "inpainting", "инпейнт", "кисть", "пресеты", "очередь", "история промптов", "куда пропала lora", "где пресеты", "где конечный кадр"]
        ));

        chunks.Add(new KnowledgeChunk(
            "gallery-features-guide",
            "gallery",
            "Галерея: метаданные (.meta.json), кнопка «В Студию», избранное (★), кэш видео-превью",
            "Возможности вкладки «Галерея» (open_tab|gallery): " +
            "• Авто-метаданные: каждая генерация сохраняет рядом файл `.meta.json` (а для старых PNG читается встроенный `tEXt` ComfyUI). В правой панели виден промпт и параметры. " +
            "• Кнопка «В Студию»: одним кликом переносит промпт, модель, размер, Seed, референс и LoRA выбранного кадра обратно в Фото- или Видео-студию (и автоматически раскрывает блок LoRA, если она использовалась). " +
            "• Избранное (★ / клавиша F): отмечай лучшие кадры, фильтруй их и чисти лишнее кнопкой «Выбрать всё кроме ★» → «В корзину» (файлы удаляются безопасно в Корзину Windows вместе с `.meta.json`). " +
            "• Производительность: миниатюры декодируются асинхронно с LRU-кэшем, а для `.mp4` извлекается реальный первый кадр в `%LocalAppData%\\LocalAIHub\\thumb_cache`. Горячие клавиши: ← / → листать, F — избранное, Del — в корзину, клик по превью — Lightbox на весь экран.",
            ["галерея", "метаданные", "meta.json", "в студию", "восстановить промпт", "избранное", "корзина", "удалить временные", "превью видео", "лайтбокс", "горячие клавиши"]
        ));

        chunks.Add(new KnowledgeChunk(
            "voice-stt-tts-guide",
            "voice",
            "Локальный голос: микрофон Whisper.cpp (STT) и озвучка видео (TTS + FFmpeg)",
            "Все голосовые функции работают 100% локально на ПК: " +
            "• Голосовой ввод (STT, кнопка микрофона в Чате, Фото-промпте и Видео-промпте): первый клик начинает запись с микрофона (кнопка краснеет), второй клик останавливает запись и распознаёт речь (RU/EN) через локальный `whisper.cpp` (`ggml-base.bin`). Устанавливается в 1 клик в «Система → Настройки → Локальный голос» (open_tab|settings, ~150 МБ). " +
            "• Локальная озвучка видео (TTS): под результатом в Видео-студии есть сворачиваемый блок «Озвучка видео (Локальный TTS)». " +
            "  - Встроенные офлайн-голоса Windows (Pavel RU мужской, Irina RU женский, Zira EN) работают сразу без скачивания. " +
            "  - Нейросетевой русский голос Piper HD (Dmitri, ~80 МБ) ставится в 1 клик в разделе «Система → Локальный голос». " +
            "  - Кнопка «Прослушать» проигрывает озвучку, а «Наложить на видео» микширует аудио с MP4 через FFmpeg в новый файл `*_voiced.mp4`.",
            ["голос", "микрофон", "stt", "tts", "whisper", "piper", "озвучка", "озвучить видео", "диктовка", "речь", "аудио", "ffmpeg"]
        ));

        chunks.Add(new KnowledgeChunk(
            "system-gpu-backup-updates-guide",
            "system",
            "Авто-выгрузка GPU, живые графики, бэкап профиля (.zip), краш-логи и сервер обновлений",
            "Новые системные возможности Brazhnichenko Hub (вкладка «Система», open_tab|settings): " +
            "• Живые мини-графики (sparklines): в шапке GPU и RAM рисуется история VRAM %, температуры GPU °C и RAM % за последние ~75 сек (наведи мышь для min/max и последнего `tok/s`). Полоска диска показывает реальную заполненность. " +
            "• Авто-выгрузка LLM из VRAM при простое: в «Система → Настройки» можно задать таймер (Выкл / 10 / 15 / 30 / 60 мин) — если чат, Режиссёр и GPU простаивают (загрузка GPU < 15%), `llama-server` сам остановится и освободит видеопамять перед играми. " +
            "• Экспорт / Импорт профиля (.zip): сохраняет в один архив `config.json`, пресеты, историю промптов, избранное Галереи, диалоги Чата 2.0 и очередь генераций для бэкапа или переноса на другой ПК. " +
            "• Краш-логи и диагностика: все необработанные ошибки пишутся в `crash.log`; в разделе «Система → Логи» можно посмотреть `crash.log` и нажать «Скопировать отчёт». " +
            "• Автообновления и свой сервер: проверка релизов по HTTPS с Certificate Pinning и SHA-256; в «Система → Сервисы» кнопка «Сервер» позволяет при необходимости сменить адрес сервера обновлений, токен и отпечаток сертификата.",
            ["автовыгрузка", "простой", "таймер", "график", "sparkline", "бэкап", "резервная копия", "экспорт", "импорт", "профиль", "zip", "перенос", "crash", "краш", "логи", "отчёт", "обновление", "сервер обновлений", "vps"]
        ));

        chunks.Add(new KnowledgeChunk(
            "linux-platform-guide",
            "system",
            "Brazhnichenko Hub на Linux (Ubuntu / Debian / Fedora / Arch): NVIDIA CUDA, AMD ROCm, Vulkan, голос и ярлык",
            "Особенности работы Brazhnichenko Hub на Linux (x64 и arm64): " +
            "• Движок llama.cpp ставится в 1 клик во вкладке «Система»: автоматически подбирается NVIDIA CUDA 13.5 (для RTX 50 Blackwell) или CUDA 12.8 (для GTX 10–RTX 40) вместе с cudart, AMD ROCm для Radeon, либо универсальный Vulkan/CPU. " +
            "• Движок ComfyUI для Фото/Видео ставится через git + python3-venv + PyTorch (убедись, что установлены пакеты: `sudo apt install git python3-venv`). " +
            "• Локальный голос на Linux: нейросетевой русский голос Piper HD (`piper_linux_x86_64.tar.gz`) ставится в 1 клик; для мгновенной лёгкой офлайн-озвучки можно поставить `espeak-ng`, а для записи с микрофона и наложения аудио на видео — `ffmpeg` (`sudo apt install ffmpeg espeak-ng`). " +
            "• Ярлык в меню приложений GNOME/KDE/XFCE создаётся скриптом `./install-desktop.sh` из распакованного архива (удаляется через `./uninstall-desktop.sh`). " +
            "• Конфиги хранятся в `~/.config/BrazhnichenkoHub`, модели по умолчанию — в `~/BrazhnichenkoHub`.",
            ["linux", "линукс", "ubuntu", "debian", "fedora", "arch", "cuda", "rocm", "vulkan", "espeak", "piper", "ffmpeg", "desktop", "ярлык", "установка на linux"]
        ));

        return chunks;
    }

    public static List<RagSearchResult> Retrieve(string query, List<KnowledgeChunk> corpus, int topK = 5)
    {
        var queryTokens = ExpandTokens(Tokenize(query));
        if (queryTokens.Count == 0)
            return corpus.Take(topK).Select(c => new RagSearchResult(c, 1.0)).ToList();

        // Pre-tokenize corpus for BM25
        var docTokens = corpus.Select(c =>
        {
            var t = Tokenize($"{c.Title} {c.Title} {string.Join(' ', c.Keywords)} {string.Join(' ', c.Keywords)} {c.Content}");
            return t;
        }).ToList();

        double avgLen = Math.Max(1.0, docTokens.Average(d => d.Count));
        int nDocs = corpus.Count;
        const double k1 = 1.5;
        const double b = 0.75;

        // Document frequency
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dt in docTokens)
        {
            foreach (var uniq in dt.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                df[uniq] = df.GetValueOrDefault(uniq) + 1;
            }
        }

        var scored = new List<RagSearchResult>(nDocs);
        for (int i = 0; i < nDocs; i++)
        {
            var dt = docTokens[i];
            int docLen = dt.Count;
            double score = 0;

            foreach (string qt in queryTokens)
            {
                // Prefix match for Russian morphology (e.g. "установ" matches "установить", "установлена")
                int tf = 0;
                foreach (string w in dt)
                {
                    if (w.Equals(qt, StringComparison.OrdinalIgnoreCase) ||
                        (qt.Length >= 4 && w.StartsWith(qt, StringComparison.OrdinalIgnoreCase)))
                    {
                        tf++;
                    }
                }
                if (tf == 0) continue;

                int docFreq = df.Where(kv => kv.Key.Equals(qt, StringComparison.OrdinalIgnoreCase) ||
                                             (qt.Length >= 4 && kv.Key.StartsWith(qt, StringComparison.OrdinalIgnoreCase)))
                                .Select(kv => kv.Value).DefaultIfEmpty(1).Max();
                double idf = Math.Log((nDocs - docFreq + 0.5) / (docFreq + 0.5) + 1.0);
                score += idf * (tf * (k1 + 1.0)) / (tf + k1 * (1.0 - b + b * (docLen / avgLen)));
            }

            // Always keep live-pc-state accessible with small baseline boost
            if (corpus[i].Id == "live-pc-state") score += 2.5;

            // Intent boost: Vision-in-chat queries (e.g. "текстовые модели поддерживают картинки в чате" or "mmproj")
            string qLower = query.ToLowerInvariant();
            bool asksVisionInChat =
                qLower.Contains("mmproj") || qLower.Contains("vision") || qLower.Contains("вижн") || qLower.Contains("image input") ||
                ((qLower.Contains("картинк") || qLower.Contains("фото") || qLower.Contains("изображен") || qLower.Contains("скриншот")) &&
                 (qLower.Contains("чат") || qLower.Contains("текст") || qLower.Contains("llm")));
            if (asksVisionInChat)
            {
                if (corpus[i].Id == "chat-vision-mmproj-guide") score += 14.0;
                if (corpus[i].Id == "catalog-text") score += 6.0;
            }

            // Intent boost: specific Apple Silicon generation queries (M1..M6)
            foreach (string gen in new[] { "m1", "m2", "m3", "m4", "m5", "m6" })
            {
                if (Regex.IsMatch(qLower, $@"\b{gen}\b"))
                {
                    if (corpus[i].Id == $"wiki-apple-{gen}") score += 16.0;
                }
            }

            scored.Add(new RagSearchResult(corpus[i], score));
        }

        return scored
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    private static List<string> Tokenize(string text)
    {
        var matches = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{Nd}\.\-]+");
        var list = new List<string>(matches.Count);
        foreach (Match m in matches)
        {
            string s = m.Value.Trim('.', '-');
            if (s.Length >= 2) list.Add(s);
        }
        return list;
    }

    private static HashSet<string> ExpandTokens(List<string> rawTokens)
    {
        var set = new HashSet<string>(rawTokens, StringComparer.OrdinalIgnoreCase);
        foreach (string tok in rawTokens)
        {
            if (tok.Length < 3 && tok is not ("hf" or "vl" or "ai" or "m1" or "m2" or "m3" or "m4" or "m5" or "m6")) continue;
            foreach (var (root, syns) in SynonymGroups)
            {
                bool match = tok.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || (tok.Length >= 4 && (tok.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(tok, StringComparison.OrdinalIgnoreCase)))
                    || syns.Any(s => tok.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                     (tok.Length >= 4 && s.Length >= 4 && (tok.StartsWith(s, StringComparison.OrdinalIgnoreCase) || s.StartsWith(tok, StringComparison.OrdinalIgnoreCase))));
                if (match)
                {
                    set.Add(root);
                    foreach (string s in syns) set.Add(s);
                }
            }
        }
        return set;
    }
}
