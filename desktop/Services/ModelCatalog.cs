using System.IO;

namespace LocalAIHub.Services;

public sealed record CatalogFileSpec(
    string RepoId,
    string RemotePath,
    string TargetRoot,      // "llm_models" | "comfy_models"
    string RelativeDest,    // e.g. "Qwen3-8B-Q4_K_M.gguf" or "diffusion_models/flux3-image-klein-int8-convrot.safetensors"
    double SizeGB
);

public sealed record CatalogModelSpec(
    string Id,
    string Category,        // "text" | "photo" | "video"
    string Name,
    string ShortTag,
    string SummaryRu,
    string WhyChooseRu,
    int MinVramGB,
    int IdealVramGB,
    int MinRamGB,
    int IdealRamGB,
    bool PrefersFp8,
    bool PrefersBf16,
    bool IsGated,
    double TotalSizeGB,
    CatalogFileSpec[] Files,
    bool IsMoe = false,
    bool HasVision = false,
    bool IsAssistantRecommended = false,
    string? ParentModelId = null
);

public static class ModelCatalog
{
    public const string AssistantModelId = "llm-qwen3-4b-assistant";

    public static readonly CatalogModelSpec AssistantSpec = new(
        Id: AssistantModelId,
        Category: "text",
        Name: "Qwen 3 4B Instruct (Помощник по Хабу • быстрый)",
        ShortTag: "4+ ГБ VRAM / CPU • 2.33 ГБ • Быстрый ассистент",
        SummaryRu: "Сверхлёгкая и быстрая русскоязычная модель для ассистента Alt+Space и заметок: мгновенно отвечает на ноутбуках (35 tok/s на Intel Arc 140V) без лишнего расхода батареи.",
        WhyChooseRu: "Занимает всего 2.33 ГБ, стартует за секунды даже на встройке/Vulkan и идеально форматирует заметки.",
        MinVramGB: 4, IdealVramGB: 6, MinRamGB: 8, IdealRamGB: 16,
        PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 2.33,
        Files: [new("unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "llm_models", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2.33)],
        IsAssistantRecommended: true
    );

    public static IReadOnlyList<CatalogModelSpec> All { get; } =
    [
        // ===================== 1. ТЕКСТОВЫЕ (LLM & VISION) =====================
        AssistantSpec,
        new(
            Id: "llm-qwen3-vl-4b-vision",
            Category: "text",
            Name: "Qwen 3 VL 4B Vision (Топ ассистент экрана • Q4_K_M + mmproj)",
            ShortTag: "Vision + Ассистент • 4–8 ГБ GPU • 3.11 ГБ комплект",
            SummaryRu: "Новейшая компактная Vision-модель семейства Qwen 3: отлично читает скриншоты, интерфейсы, код, таблицы и идеально подходит для Alt+Space ассистента на ноутбуке!",
            WhyChooseRu: "Лучший лёгкий Vision-ассистент (3.11 ГБ с mmproj): мгновенно разбирает экран и заметки на Intel Arc, RTX и Apple Silicon.",
            MinVramGB: 4, IdealVramGB: 6, MinRamGB: 8, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 3.11,
            Files: [
                new("unsloth/Qwen3-VL-4B-Instruct-GGUF", "Qwen3-VL-4B-Instruct-Q4_K_M.gguf", "llm_models", "Qwen3-VL-4B-Instruct-Q4_K_M.gguf", 2.33),
                new("unsloth/Qwen3-VL-4B-Instruct-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-Qwen3-VL-4B-Instruct-F16.gguf", 0.78)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-gemma3-4b-vision",
            Category: "text",
            Name: "Gemma 3 4B Vision (Google • Лёгкий экран + русский • Q4_K_M + mmproj)",
            ShortTag: "Google Vision + Ассистент • 4–8 ГБ GPU • 3.11 ГБ комплект",
            SummaryRu: "Компактная мультимодальная модель от Google с естественным русским языком: видит экран и фото, быстро переводит и отвечает в Alt+Space ассистенте.",
            WhyChooseRu: "Всего 3.11 ГБ вместе со зрением mmproj — отличный живой слог на ноутбуках и картах 6–8 ГБ.",
            MinVramGB: 4, IdealVramGB: 6, MinRamGB: 8, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 3.11,
            Files: [
                new("unsloth/gemma-3-4b-it-GGUF", "gemma-3-4b-it-Q4_K_M.gguf", "llm_models", "gemma-3-4b-it-Q4_K_M.gguf", 2.32),
                new("unsloth/gemma-3-4b-it-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-gemma-3-4b-it-F16.gguf", 0.79)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-qwen25-vl-3b-vision",
            Category: "text",
            Name: "Qwen 2.5 VL 3B Vision (Сверхбыстрый OCR экрана • Q4_K_M + mmproj)",
            ShortTag: "Vision + OCR • 4+ ГБ GPU • 2.59 ГБ комплект",
            SummaryRu: "Самая лёгкая и быстрая Vision-модель для распознавания текста со скриншотов, документов и мгновенных подсказок по экрану.",
            WhyChooseRu: "Минимум памяти (2.59 ГБ вместе с mmproj) и максимальная скорость отклика даже на скромных GPU.",
            MinVramGB: 4, IdealVramGB: 6, MinRamGB: 8, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 2.59,
            Files: [
                new("ggml-org/Qwen2.5-VL-3B-Instruct-GGUF", "Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf", "llm_models", "Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf", 1.80),
                new("ggml-org/Qwen2.5-VL-3B-Instruct-GGUF", "mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf", "llm_models", "mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf", 0.79)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-qwen3-vl-8b-vision",
            Category: "text",
            Name: "Qwen 3 VL 8B Vision (Мощный экран и OCR • Q4_K_M + mmproj)",
            ShortTag: "Vision + Ассистент • 8–16 ГБ GPU • 5.76 ГБ комплект",
            SummaryRu: "Флагманский компактный Vision Qwen 3 (8B + mmproj): высокая точность разбора сложных интерфейсов, графиков, ошибок кода и фото на русском и английском.",
            WhyChooseRu: "Оптимальный баланс ума и скорости для 12–16 ГБ GPU (и Intel Arc 140V 16–18 ГБ) в роли главного ассистента экрана.",
            MinVramGB: 8, IdealVramGB: 12, MinRamGB: 16, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 5.76,
            Files: [
                new("unsloth/Qwen3-VL-8B-Instruct-GGUF", "Qwen3-VL-8B-Instruct-Q4_K_M.gguf", "llm_models", "Qwen3-VL-8B-Instruct-Q4_K_M.gguf", 4.68),
                new("unsloth/Qwen3-VL-8B-Instruct-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-Qwen3-VL-8B-Instruct-F16.gguf", 1.08)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-qwen25-vl-7b-vision",
            Category: "text",
            Name: "Qwen 2.5 VL 7B Vision (Чат с картинками • Q4_K_M + mmproj)",
            ShortTag: "Vision (анализ фото) • 8–16 ГБ VRAM • 5.16 ГБ комплект",
            SummaryRu: "Мультимодальная Vision-LLM (видит и анализирует картинки в Чате 2.0!): распознаёт текст на фото/скриншотах (OCR), описывает сцены, помогает по интерфейсам. Включает и саму модель, и файл зрения mmproj.",
            WhyChooseRu: "Проверенный временем Vision-комплект (5.16 ГБ), 100% влезает в 8–16 ГБ VRAM.",
            MinVramGB: 8, IdealVramGB: 12, MinRamGB: 16, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 5.16,
            Files: [
                new("ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf", "llm_models", "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf", 4.36),
                new("ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf", "llm_models", "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf", 0.80)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-gemma3-12b-vision",
            Category: "text",
            Name: "Gemma 3 12B Vision (Google • Чат с картинками • Q4_K_M + mmproj)",
            ShortTag: "Google Vision + Ассистент • 10–16 ГБ GPU • 7.59 ГБ комплект",
            SummaryRu: "Умная мультимодальная модель от Google (12B) с файлом зрения mmproj в комплекте: отлично понимает фото, схемы, скриншоты и пишет живой русский текст.",
            WhyChooseRu: "Золотой стандарт Vision для RTX 3060 12GB / 4070 / 4080 / 5080 и Intel Arc 140V 16–18 ГБ — 100% в видеопамяти.",
            MinVramGB: 10, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 7.59,
            Files: [
                new("unsloth/gemma-3-12b-it-GGUF", "gemma-3-12b-it-Q4_K_M.gguf", "llm_models", "gemma-3-12b-it-Q4_K_M.gguf", 6.80),
                new("unsloth/gemma-3-12b-it-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-gemma-3-12b-it-F16.gguf", 0.79)
            ],
            HasVision: true,
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-gemma3-27b-vision",
            Category: "text",
            Name: "Gemma 3 27B Vision (Google • Флагманский Dense Vision • Q4_K_M + mmproj)",
            ShortTag: "Google Vision 27B • 18–24 ГБ GPU • 16.21 ГБ комплект",
            SummaryRu: "Крупная мультимодальная модель от Google (27B + mmproj): глубокий анализ документов, схем, фото и эталонный русский язык.",
            WhyChooseRu: "Для мощных систем 24 ГБ VRAM или 16–18 ГБ GPU + 32 ГБ RAM, когда нужно максимальное качество Vision.",
            MinVramGB: 16, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 16.21,
            Files: [
                new("unsloth/gemma-3-27b-it-GGUF", "gemma-3-27b-it-Q4_K_M.gguf", "llm_models", "gemma-3-27b-it-Q4_K_M.gguf", 15.41),
                new("unsloth/gemma-3-27b-it-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-gemma-3-27b-it-F16.gguf", 0.80)
            ],
            HasVision: true
        ),
        new(
            Id: "llm-qwen3-vl-30b-a3b-vision",
            Category: "text",
            Name: "Qwen 3 VL 30B-A3B MoE Vision (Быстрый 30B Vision • UD-Q4_K_XL + mmproj)",
            ShortTag: "MoE Vision 30B (акт. 3B) • 17.51 ГБ комплект",
            SummaryRu: "Флагманская MoE Vision-модель нового поколения (30B параметров, активно всего 3B): глубочайшее понимание скриншотов, UI, кода и графиков при высокой скорости даже с оффлоадом в ОЗУ!",
            WhyChooseRu: "Топовый мультимодальный интеллект 30B на скорости 4B-модели для систем с 16–24 ГБ GPU и 32 ГБ RAM.",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 17.51,
            Files: [
                new("unsloth/Qwen3-VL-30B-A3B-Instruct-GGUF", "Qwen3-VL-30B-A3B-Instruct-UD-Q4_K_XL.gguf", "llm_models", "Qwen3-VL-30B-A3B-Instruct-UD-Q4_K_XL.gguf", 16.50),
                new("unsloth/Qwen3-VL-30B-A3B-Instruct-GGUF", "mmproj-F16.gguf", "llm_models", "mmproj-Qwen3-VL-30B-A3B-Instruct-F16.gguf", 1.01)
            ],
            IsMoe: true,
            HasVision: true
        ),
        new(
            Id: "llm-qwen36-35b-hauhau-vision-mmproj",
            Category: "text",
            Name: "Vision-модуль (mmproj) для Qwen 3.6 35B HauhauCS",
            ShortTag: "Дополнение к Qwen 3.6 35B • 0.84 ГБ • Требует основную 35B модель",
            SummaryRu: "Официальный файл зрения (mmproj-f16) для модели Qwen 3.6 35B-A3B HauhauCS: после скачивания и перезапуска LLM эта 35B-модель начинает видеть и разбирать картинки! Не работает отдельно без Qwen 3.6 35B.",
            WhyChooseRu: "Если у тебя уже стоит Qwen 3.6 35B HauhauCS — докачай всего 0.84 ГБ вместо целой новой модели, и Vision заработает.",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 0.84,
            Files: [
                new("HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", "mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf", "llm_models", "mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf", 0.84)
            ],
            IsMoe: true,
            HasVision: true,
            ParentModelId: "llm-qwen36-35b-moe-hauhau-q4"
        ),
        new(
            Id: "llm-qwen3-8b-q4",
            Category: "text",
            Name: "Qwen 3 8B (Unsloth Q4_K_M)",
            ShortTag: "6–8 ГБ VRAM • Быстрый старт и ассистент",
            SummaryRu: "Компактная умная LLM 8B: отлично работает даже на GTX 1060 / RTX 2060 / 3060 / 4060 (6–8 ГБ VRAM) и ноутбуках Intel Arc.",
            WhyChooseRu: "Идеальна для карт 6–8 ГБ и быстрого ассистента: целиком влезает в видеопамять с длинным контекстом.",
            MinVramGB: 6, IdealVramGB: 8, MinRamGB: 16, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 4.68,
            Files: [new("unsloth/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "llm_models", "Qwen3-8B-Q4_K_M.gguf", 4.68)],
            IsAssistantRecommended: true
        ),
        new(
            Id: "llm-deepseek-r1-0528-8b",
            Category: "text",
            Name: "DeepSeek-R1-0528 8B (Unsloth Q4_K_M)",
            ShortTag: "DeepSeek • 6–8 ГБ VRAM • Логика и математика",
            SummaryRu: "Компактный дистиллят флагманского DeepSeek-R1-0528: сильные цепочки рассуждений, задачи по математике и коду даже на картах 6–8 ГБ VRAM.",
            WhyChooseRu: "Лучший лёгкий «мыслитель» не из семейства обычных инструкт-моделей — целиком в VRAM с огромной скоростью.",
            MinVramGB: 6, IdealVramGB: 8, MinRamGB: 16, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 4.68,
            Files: [new("unsloth/DeepSeek-R1-0528-Qwen3-8B-GGUF", "DeepSeek-R1-0528-Qwen3-8B-Q4_K_M.gguf", "llm_models", "DeepSeek-R1-0528-Qwen3-8B-Q4_K_M.gguf", 4.68)]
        ),
        new(
            Id: "llm-gemma4-12b-q4",
            Category: "text",
            Name: "Gemma 4 12B (Google • Unsloth Q4_K_M)",
            ShortTag: "Google • 8–12 ГБ VRAM • Живой диалог",
            SummaryRu: "Свежая 12B-модель от Google: быстрая генерация, грамотный русский язык и отличный запас контекста на 8–12 ГБ картах.",
            WhyChooseRu: "Оптимальный выбор для RTX 3060 12GB / 4070 12GB / 5070 12GB — 100% в VRAM.",
            MinVramGB: 8, IdealVramGB: 12, MinRamGB: 16, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 6.63,
            Files: [new("unsloth/gemma-4-12b-it-GGUF", "gemma-4-12b-it-Q4_K_M.gguf", "llm_models", "gemma-4-12b-it-Q4_K_M.gguf", 6.63)]
        ),
        new(
            Id: "llm-gpt-oss-20b-q4xl",
            Category: "text",
            Name: "GPT-OSS 20B MoE (OpenAI • Unsloth UD-Q4_K_XL)",
            ShortTag: "OpenAI • MoE 21B (акт. 3.6B) • Apache 2.0",
            SummaryRu: "Открытая рассуждающая MoE-модель от OpenAI: сверхбыстрая генерация, чёткое следование инструкциям, сильный анализ и код.",
            WhyChooseRu: "Влезает 100% в 16 ГБ VRAM (11 ГБ), даёт ощущение стиля OpenAI локально и летает ~65–95 tok/s!",
            MinVramGB: 12, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 11.06,
            Files: [new("unsloth/gpt-oss-20b-GGUF", "gpt-oss-20b-UD-Q4_K_XL.gguf", "llm_models", "gpt-oss-20b-UD-Q4_K_XL.gguf", 11.06)],
            IsMoe: true
        ),
        new(
            Id: "llm-gemma4-26b-a4b-iq4xs",
            Category: "text",
            Name: "Gemma 4 26B-A4B MoE (Google • Unsloth UD-IQ4_XS)",
            ShortTag: "Google • MoE 26B (акт. 4B) • Топ русский и креатив",
            SummaryRu: "Хит от Google (4+ млн скачиваний): 26B MoE с 4B активных параметров — самый живой естественный слог, сильный русский язык и творчество.",
            WhyChooseRu: "Целиком ложится в 16 ГБ VRAM (12.7 ГБ) и работает ~55–85 tok/s — идеальная альтернатива Qwen для текстов и диалогов.",
            MinVramGB: 14, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 12.66,
            Files: [new("unsloth/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-UD-IQ4_XS.gguf", "llm_models", "gemma-4-26B-A4B-it-UD-IQ4_XS.gguf", 12.66)],
            IsMoe: true
        ),
        new(
            Id: "llm-devstral-small-2-24b-q4",
            Category: "text",
            Name: "Devstral Small 2 24B (Mistral • Кодинг и агенты)",
            ShortTag: "Mistral • Dense 24B • Лучший кодер в 16 ГБ",
            SummaryRu: "Специализированная модель от Mistral для программирования, агентного редактирования проектов и работы через Claude Desktop / MCP.",
            WhyChooseRu: "Европейская архитектура, заточенная под чистый код, рефакторинг и инструменты — целиком помещается в 16 ГБ VRAM.",
            MinVramGB: 16, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 13.35,
            Files: [new("unsloth/Devstral-Small-2-24B-Instruct-2512-GGUF", "Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf", "llm_models", "Devstral-Small-2-24B-Instruct-2512-Q4_K_M.gguf", 13.35)]
        ),
        new(
            Id: "llm-cydonia-24b-v43-q4",
            Category: "text",
            Name: "Cydonia 24B v4.3 (TheDrummer • Сюжеты и ролеплей, Uncensored)",
            ShortTag: "Mistral 24B • Творчество + Без цензуры",
            SummaryRu: "Культовая творческая модель на базе Mistral 24B от TheDrummer: живой литературный стиль без «нейросетевых клише» и без цензуры.",
            WhyChooseRu: "Топ для художественных сценариев, Режиссёра, креативного письма и ролевых диалогов — 100% в 16 ГБ VRAM.",
            MinVramGB: 16, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 13.35,
            Files: [new("TheDrummer/Cydonia-24B-v4.3-GGUF", "Cydonia-24B-v4zg-Q4_K_M.gguf", "llm_models", "Cydonia-24B-v4zg-Q4_K_M.gguf", 13.35)]
        ),
        new(
            Id: "llm-qwen3-14b-q4",
            Category: "text",
            Name: "Qwen 3 14B (Unsloth Q4_K_M)",
            ShortTag: "10–12 ГБ VRAM • Код и логика",
            SummaryRu: "Крепкая плотная 14B-модель для карт 10–12 ГБ VRAM: уверенный код, рассуждения и переводы.",
            WhyChooseRu: "Золотая середина для RTX 3060 12GB / 3080 12GB / 4070 / 5070.",
            MinVramGB: 10, IdealVramGB: 12, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 8.38,
            Files: [new("unsloth/Qwen3-14B-GGUF", "Qwen3-14B-Q4_K_M.gguf", "llm_models", "Qwen3-14B-Q4_K_M.gguf", 8.38)]
        ),
        new(
            Id: "llm-qwen38-27b-hauhau-q3kp",
            Category: "text",
            Name: "Qwen 3.8 27B — HauhauCS Aggressive (Q3_K_P)",
            ShortTag: "16 ГБ VRAM • Топ Dense (39 tok/s)",
            SummaryRu: "Свободная плотная 27B нового поколения (без цензуры, MTP): помещается 66/66 слоёв в 16 ГБ VRAM при 32K контексте!",
            WhyChooseRu: "Лучший выбор для RTX 4060 Ti 16GB / 4070 Ti Super / 4080 / 5070 Ti / 5080: ~39 tok/s и 100% в видеокарте.",
            MinVramGB: 15, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 12.52,
            Files: [new("HauhauCS/Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-MTP-GGUF", "Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-Q3_K_P.gguf", "llm_models", "Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-Q3_K_P.gguf", 12.52)]
        ),
        new(
            Id: "llm-qwen38-27b-unsloth-iq4xs",
            Category: "text",
            Name: "Qwen 3.8 27B — Unsloth Dynamic (UD-IQ4_XS)",
            ShortTag: "16 ГБ VRAM • Макс. точность 4-bit",
            SummaryRu: "Оригинальная Qwen 3.8 27B в динамическом 4-битном кванте Unsloth: пик точности в коде и логике на 16 ГБ карте.",
            WhyChooseRu: "Выбирай, если нужен строгий 4-битный ум Qwen 3.8 27B целиком в 16 ГБ VRAM (контекст 16K–32K).",
            MinVramGB: 16, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 13.37,
            Files: [new("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B-UD-IQ4_XS.gguf", "llm_models", "Qwen3.8-27B-UD-IQ4_XS.gguf", 13.37)]
        ),
        new(
            Id: "llm-glm47-flash-30b-q4",
            Category: "text",
            Name: "GLM-4.7-Flash 30B-A3B MoE (Z.AI • Unsloth Q4_K_M)",
            ShortTag: "Z.AI • MoE 30B (акт. 3B) • Код, логика, агентность",
            SummaryRu: "Свежий быстрый 30B MoE от Z.AI (Zhipu): чистая структура ответов, сильный кодинг и логика при ~32–45 tok/s на связке VRAM + ОЗУ.",
            WhyChooseRu: "Сильнейшая альтернатива Qwen 3.6 35B MoE — заметно другой стиль рассуждений и высокая точность инструкций.",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 17.05,
            Files: [new("unsloth/GLM-4.7-Flash-GGUF", "GLM-4.7-Flash-Q4_K_M.gguf", "llm_models", "GLM-4.7-Flash-Q4_K_M.gguf", 17.05)],
            IsMoe: true
        ),
        new(
            Id: "llm-nemotron3-nano-30b-q4",
            Category: "text",
            Name: "Nemotron-3-Nano 30B-A3B MoE (NVIDIA • Unsloth Q4_K_M)",
            ShortTag: "NVIDIA • Гибрид Mamba + MoE 30B • Длинный контекст",
            SummaryRu: "Гибридная Mamba-Transformer MoE-модель от NVIDIA (30B / акт. 3B): минимальный расход KV-кэша на длинном контексте и сильная техническая аналитика.",
            WhyChooseRu: "Уникальная архитектура от NVIDIA — отлично держит огромные документы и бодро работает с 32 ГБ ОЗУ.",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 22.89,
            Files: [new("unsloth/Nemotron-3-Nano-30B-A3B-GGUF", "Nemotron-3-Nano-30B-A3B-Q4_K_M.gguf", "llm_models", "Nemotron-3-Nano-30B-A3B-Q4_K_M.gguf", 22.89)],
            IsMoe: true
        ),
        new(
            Id: "llm-qwen36-35b-moe-hauhau-q4",
            Category: "text",
            Name: "Qwen 3.6 35B-A3B MoE — HauhauCS (Q4_K_M)",
            ShortTag: "MoE 35B (акт. 3B) • 128K контекст • Без цензуры",
            SummaryRu: "Быстрая MoE-модель (35B всего, 3B активных): держит огромный контекст 128K и выдаёт ~35–45 tok/s даже при частичной выгрузке экспертов в 32 ГБ ОЗУ.",
            WhyChooseRu: "Незаменима для больших кодовых баз, Claude Desktop и длинных диалогов (нужно 32+ ГБ RAM).",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 19.71,
            Files: [new("HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", "Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf", "llm_models", "Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf", 19.71)],
            IsMoe: true
        ),
        new(
            Id: "llm-qwen3-next-80b-instruct-q2xl",
            Category: "text",
            Name: "Qwen3-Next 80B-A3B Instruct — Unsloth (UD-Q2_K_XL)",
            ShortTag: "MoE 80B (акт. 3B) • 28 ГБ • Умный + быстрый с ОЗУ",
            SummaryRu: "80-миллиардная MoE-модель нового поколения с всего 3B активных параметров: глубокая эрудиция и логика при ~28–38 tok/s на связке 16 ГБ VRAM + 32 ГБ ОЗУ.",
            WhyChooseRu: "Отличный баланс для 16 ГБ GPU + 32 ГБ RAM: заметно умнее 27B/35B, при этом работает быстро без долгого <think>.",
            MinVramGB: 12, IdealVramGB: 32, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 28.06,
            Files: [new("unsloth/Qwen3-Next-80B-A3B-Instruct-GGUF", "Qwen3-Next-80B-A3B-Instruct-UD-Q2_K_XL.gguf", "llm_models", "Qwen3-Next-80B-A3B-Instruct-UD-Q2_K_XL.gguf", 28.06)],
            IsMoe: true
        ),
        new(
            Id: "llm-qwen3-next-80b-thinking-ablit-q3km",
            Category: "text",
            Name: "Qwen3-Next 80B-A3B Thinking — Abliterated (Q3_K_M)",
            ShortTag: "MoE 80B (акт. 3B) • Thinking + Без цензуры • ~25–30 tok/s",
            SummaryRu: "Тяжёлый 80B флагман-мыслитель без цензуры: благодаря MoE (3B активных) работает удивительно бодро (~25–30 tok/s) на 16 ГБ VRAM + 32 ГБ ОЗУ!",
            WhyChooseRu: "Максимум ума и свободы для сложных сценариев, аналитики и кода на 16+ ГБ GPU и 32+ ГБ RAM.",
            MinVramGB: 16, IdealVramGB: 48, MinRamGB: 32, IdealRamGB: 48,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 35.57,
            Files: [new("mradermacher/Huihui-Qwen3-Next-80B-A3B-Thinking-abliterated-GGUF", "Huihui-Qwen3-Next-80B-A3B-Thinking-abliterated.Q3_K_M.gguf", "llm_models", "Huihui-Qwen3-Next-80B-A3B-Thinking-abliterated.Q3_K_M.gguf", 35.57)],
            IsMoe: true
        ),
        new(
            Id: "llm-qwen38-27b-unsloth-q6k",
            Category: "text",
            Name: "Qwen 3.8 27B — Unsloth (UD-Q6_K)",
            ShortTag: "Dense 27B • 6-бит без потерь",
            SummaryRu: "Почти эталонное 6-битное качество плотной Qwen 3.8 27B: 100% в видеопамяти на RTX 3090/4090/5090 (24–32 ГБ), либо неторопливо (~4–6 tok/s) через GPU + 32 ГБ ОЗУ.",
            WhyChooseRu: "Выбирай, когда нужен максимум качества плотной 27B-модели и готов подождать ответ (на 16 ГБ карте ~4–6 tok/s, на 24+ ГБ ~30+ tok/s).",
            MinVramGB: 12, IdealVramGB: 24, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 20.47,
            Files: [new("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B-UD-Q6_K.gguf", "llm_models", "Qwen3.8-27B-UD-Q6_K.gguf", 20.47)],
            IsMoe: false
        ),

        // ===================== 2. ФОТО (ComfyUI) =====================
        new(
            Id: "photo-flux3-klein",
            Category: "photo",
            Name: "FLUX 3 Image Klein INT8 (Топ фото + правка)",
            ShortTag: "8–16+ ГБ VRAM • Мульти-референс",
            SummaryRu: "Главный универсал: фотореализм, 3D-мультики, точечное редактирование и мульти-референсы (якорь персонажа + мосты в видео). Включает энкодер и VAE.",
            WhyChooseRu: "Must-have для Фото-историй, Шага 0 и Моста FLUX 3 — компактный INT8 (~10,3 ГБ суммарно).",
            MinVramGB: 8, IdealVramGB: 12, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: true, IsGated: false, TotalSizeGB: 10.27,
            Files: [
                new("black-forest-labs/flux-3-image-klein-comfy", "diffusion_models/flux3-image-klein-int8-convrot.safetensors", "comfy_models", "diffusion_models/flux3-image-klein-int8-convrot.safetensors", 5.43),
                new("black-forest-labs/flux-3-image-klein-comfy", "text_encoders/qwen3vl_4b_int8_convrot.safetensors", "comfy_models", "text_encoders/qwen3vl_4b_int8_convrot.safetensors", 4.52),
                new("Comfy-Org/Flux-2-Klein-4B", "split_files/vae/flux2-vae.safetensors", "comfy_models", "vae/flux2-vae.safetensors", 0.31),
            ]
        ),
        new(
            Id: "photo-zimage-turbo",
            Category: "photo",
            Name: "Z-Image Turbo BF16 (8 шагов, быстрый T2I)",
            ShortTag: "12–16+ ГБ VRAM • Apache 2.0",
            SummaryRu: "Сверхбыстрая генерация фото с нуля за 8 шагов (~10 с на RTX 4080). Полный комплект с Qwen3-4B энкодером и VAE.",
            WhyChooseRu: "Лучшая модель для быстрых черновиков и генерации картинок с нуля.",
            MinVramGB: 8, IdealVramGB: 16, MinRamGB: 16, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: true, IsGated: false, TotalSizeGB: 19.27,
            Files: [
                new("Comfy-Org/Z-Image-Turbo", "split_files/diffusion_models/z_image_turbo_bf16.safetensors", "comfy_models", "diffusion_models/z_image_turbo_bf16.safetensors", 11.46),
                new("Comfy-Org/Z-Image-Turbo", "split_files/text_encoders/qwen_3_4b.safetensors", "comfy_models", "text_encoders/qwen_3_4b.safetensors", 7.49),
                new("Comfy-Org/Z-Image-Turbo", "split_files/vae/ae.safetensors", "comfy_models", "vae/ae.safetensors", 0.31),
            ]
        ),
        new(
            Id: "photo-qwen-image-21",
            Category: "photo",
            Name: "Qwen-Image 2.1 Turbo INT8 (Текст, дизайн, правка)",
            ShortTag: "12–16+ ГБ VRAM • Чёткая типографика",
            SummaryRu: "Сильнейшая модель для надписей на картинках, открыток, дизайна, правок по референсам и прозрачного фона (RGBA).",
            WhyChooseRu: "Ставь, если генерируешь постеры, обложки, текст внутри картинки или дизайн.",
            MinVramGB: 10, IdealVramGB: 16, MinRamGB: 24, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: true, IsGated: false, TotalSizeGB: 16.10,
            Files: [
                new("Comfy-Org/Qwen-Image-2.1", "diffusion_models/qwen_image_2.1_turbo_int8_convrot.safetensors", "comfy_models", "diffusion_models/qwen_image_2.1_turbo_int8_convrot.safetensors", 6.76),
                new("Comfy-Org/Qwen-Image-2.1", "text_encoders/qwen3vl_8b_int8_convrot.safetensors", "comfy_models", "text_encoders/qwen3vl_8b_int8_convrot.safetensors", 8.71),
                new("Comfy-Org/Qwen-Image-2.1", "vae/qwen_image_2.1_vae_bf16.safetensors", "comfy_models", "vae/qwen_image_2.1_vae_bf16.safetensors", 0.63),
            ]
        ),
        new(
            Id: "photo-upscale-4x",
            Category: "photo",
            Name: "4x-UltraSharp (Апскейлер фото ×4 до 4K)",
            ShortTag: "6+ ГБ VRAM • 64 МБ",
            SummaryRu: "Мгновенно увеличивает резкость и разрешение любого фото в 4 раза прямо во вкладке «Студия».",
            WhyChooseRu: "Весит всего 64 МБ, работает на абсолютно любой видеокарте от GTX 1060 до RTX 5090.",
            MinVramGB: 4, IdealVramGB: 6, MinRamGB: 8, IdealRamGB: 16,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 0.06,
            Files: [new("Kim2091/UltraSharp", "4x-UltraSharp.pth", "comfy_models", "upscale_models/4x-UltraSharp.pth", 0.06)]
        ),

        // ===================== 3. ВИДЕО (ComfyUI) =====================
        new(
            Id: "video-wan22-fp8-svi",
            Category: "video",
            Name: "Wan 2.2 14B FP8 Scaled + Lightning + SVI 2.0 Pro",
            ShortTag: "16–32 ГБ VRAM (RTX 40/50) • Топ-кино + цепочки",
            SummaryRu: "Полный флагманский видео-комплект: T2V + I2V (High/Low FP8 Scaled с DynamicVRAM), 4-шаговые Lightning LoRA, SVI 2.0 Pro (длинные бесшовные истории), UMT5 и VAE.",
            WhyChooseRu: "Лучший выбор для RTX 4060 Ti 16GB / 4070 Ti Super / 4080 / 4090 / 5070 Ti / 5080 / 5090 + 32+ ГБ RAM.",
            MinVramGB: 12, IdealVramGB: 16, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: true, PrefersBf16: true, IsGated: false, TotalSizeGB: 66.50,
            Files: [
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors", "comfy_models", "diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors", 13.31),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/diffusion_models/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors", "comfy_models", "diffusion_models/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors", 13.31),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors", "comfy_models", "diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors", 13.31),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors", "comfy_models", "diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors", 13.31),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_high_noise.safetensors", "comfy_models", "loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_high_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_low_noise.safetensors", "comfy_models", "loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_low_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors", "comfy_models", "loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors", "comfy_models", "loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors", 1.14),
                new("Kijai/WanVideo_comfy", "LoRAs/Stable-Video-Infinity/v2.0/SVI_v2_PRO_Wan2.2-I2V-A14B_HIGH_lora_rank_128_fp16.safetensors", "comfy_models", "loras/SVI_v2_PRO_Wan2.2-I2V-A14B_HIGH_lora_rank_128_fp16.safetensors", 1.14),
                new("Kijai/WanVideo_comfy", "LoRAs/Stable-Video-Infinity/v2.0/SVI_v2_PRO_Wan2.2-I2V-A14B_LOW_lora_rank_128_fp16.safetensors", "comfy_models", "loras/SVI_v2_PRO_Wan2.2-I2V-A14B_LOW_lora_rank_128_fp16.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", "comfy_models", "text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", 6.27),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/vae/wan_2.1_vae.safetensors", "comfy_models", "vae/wan_2.1_vae.safetensors", 0.24),
            ]
        ),
        new(
            Id: "video-wan22-gguf-q4",
            Category: "video",
            Name: "Wan 2.2 14B GGUF Q4_K_M + Lightning (для 8–12 ГБ)",
            ShortTag: "8–12 ГБ VRAM (RTX 20/30) • Компактный",
            SummaryRu: "Компактная 4-битная GGUF-версия Wan 2.2 14B (T2V + I2V High/Low + Lightning LoRA + UMT5 + VAE) для карт без аппаратного FP8.",
            WhyChooseRu: "Оптимальный видео-движок для RTX 2060 12GB / 2080 Ti / 3060 12GB / 3080 10-12GB при 32 ГБ RAM.",
            MinVramGB: 8, IdealVramGB: 12, MinRamGB: 24, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: false, IsGated: false, TotalSizeGB: 46.90,
            Files: [
                new("QuantStack/Wan2.2-T2V-A14B-GGUF", "HighNoise/Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf", "comfy_models", "diffusion_models/Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf", 8.99),
                new("QuantStack/Wan2.2-T2V-A14B-GGUF", "LowNoise/Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf", "comfy_models", "diffusion_models/Wan2.2-T2V-A14B-LowNoise-Q4_K_M.gguf", 8.99),
                new("QuantStack/Wan2.2-I2V-A14B-GGUF", "HighNoise/Wan2.2-I2V-A14B-HighNoise-Q4_K_M.gguf", "comfy_models", "diffusion_models/Wan2.2-I2V-A14B-HighNoise-Q4_K_M.gguf", 8.99),
                new("QuantStack/Wan2.2-I2V-A14B-GGUF", "LowNoise/Wan2.2-I2V-A14B-LowNoise-Q4_K_M.gguf", "comfy_models", "diffusion_models/Wan2.2-I2V-A14B-LowNoise-Q4_K_M.gguf", 8.99),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_high_noise.safetensors", "comfy_models", "loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_high_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_low_noise.safetensors", "comfy_models", "loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_low_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors", "comfy_models", "loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors", "comfy_models", "loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors", 1.14),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", "comfy_models", "text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", 6.27),
                new("Comfy-Org/Wan_2.2_ComfyUI_Repackaged", "split_files/vae/wan_2.1_vae.safetensors", "comfy_models", "vae/wan_2.1_vae.safetensors", 0.24),
            ]
        ),
        new(
            Id: "video-ltx25-audio",
            Category: "video",
            Name: "LTX-2.5 22B INT8 (Видео + синхронный звук)",
            ShortTag: "16–32 ГБ VRAM • Аудио + Видео (Gated HF)",
            SummaryRu: "Генерирует видео сразу со встроенным синхронным звуком (эффекты, окружение, речь) + x2 апскейлер. Требует принятой лицензии Lightricks/LTX-2.5 на Hugging Face.",
            WhyChooseRu: "Единственная локальная модель, создающая картинку и звук одновременно (~60 с на RTX 4080).",
            MinVramGB: 12, IdealVramGB: 16, MinRamGB: 32, IdealRamGB: 32,
            PrefersFp8: false, PrefersBf16: true, IsGated: true, TotalSizeGB: 37.00,
            Files: [
                new("Lightricks/LTX-2.5", "diffusion_models/ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors", "comfy_models", "diffusion_models/ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors", 20.03),
                new("Lightricks/LTX-2.5", "text_encoders/gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors", "comfy_models", "text_encoders/gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors", 14.32),
                new("Lightricks/LTX-2.5", "vae/ltx-2.5-video-vae-bf16.safetensors", "comfy_models", "vae/ltx-2.5-video-vae-bf16.safetensors", 1.37),
                new("Lightricks/LTX-2.5", "vae/ltx-2.5-audio-vae-bf16.safetensors", "comfy_models", "vae/ltx-2.5-audio-vae-bf16.safetensors", 0.34),
                new("Lightricks/LTX-2.5", "latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors", "comfy_models", "latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors", 0.93),
            ]
        ),
    ];

    public static CatalogModelSpec? FindById(string id) =>
        All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string ResolveLocalPath(CatalogFileSpec f)
    {
        string baseDir = f.TargetRoot == "llm_models" ? HubConfig.LlmModelsDir : HubConfig.ComfyModelsDir;
        return Path.Combine(baseDir, f.RelativeDest.Replace('/', Path.DirectorySeparatorChar));
    }

    public static (bool allInstalled, int presentCount, int totalCount, double missingGB) CheckInstallState(CatalogModelSpec spec)
    {
        int present = 0;
        double missing = 0;
        foreach (var f in spec.Files)
        {
            string full = ResolveLocalPath(f);
            if (File.Exists(full) && new FileInfo(full).Length > 1024 * 1024)
                present++;
            else
                missing += f.SizeGB;
        }
        return (present == spec.Files.Length, present, spec.Files.Length, Math.Round(missing, 2));
    }

    public static (List<string> filesToDelete, double reclaimGB, int sharedKeptCount) GetDeletableFiles(CatalogModelSpec spec)
    {
        // Protect shared files (e.g., UMT5 / VAE / Lightning LoRAs) still used by other installed bundles
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in All)
        {
            if (other.Id.Equals(spec.Id, StringComparison.OrdinalIgnoreCase)) continue;
            var otherState = CheckInstallState(other);
            if (otherState.presentCount == 0) continue;
            // Only protect if the other bundle has at least one unique file of its own installed
            var specPaths = spec.Files.Select(ResolveLocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool otherHasOwnFiles = other.Files.Any(f =>
            {
                string p = ResolveLocalPath(f);
                return !specPaths.Contains(p) && File.Exists(p) && new FileInfo(p).Length > 1024 * 1024;
            });
            if (!otherHasOwnFiles) continue;
            foreach (var f in other.Files)
                protectedPaths.Add(ResolveLocalPath(f));
        }

        var deletable = new List<string>();
        long bytes = 0;
        int sharedKept = 0;

        foreach (var f in spec.Files)
        {
            string full = ResolveLocalPath(f);
            string part = full + ".part";

            if (File.Exists(part))
            {
                deletable.Add(part);
                try { bytes += new FileInfo(part).Length; } catch { }
            }

            if (File.Exists(full))
            {
                if (protectedPaths.Contains(full))
                {
                    sharedKept++;
                }
                else
                {
                    deletable.Add(full);
                    try { bytes += new FileInfo(full).Length; } catch { }
                }
            }
        }

        return (deletable, Math.Round(bytes / (1024.0 * 1024 * 1024), 2), sharedKept);
    }

    public static bool IsMediaEngineInstalled(string engineTag)
    {
        static bool BundleReady(string catalogId)
        {
            var spec = FindById(catalogId);
            return spec is not null && CheckInstallState(spec).allInstalled;
        }

        string modelsDir = HubConfig.ComfyModelsDir;
        bool ExistsAny(params string[] relPaths) =>
            relPaths.Any(r =>
            {
                string full = Path.Combine(modelsDir, r.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(full) && new FileInfo(full).Length > 1024 * 1024;
            });

        return engineTag.ToLowerInvariant() switch
        {
            "zimage" => BundleReady("photo-zimage-turbo"),
            "flux3" => BundleReady("photo-flux3-klein"),
            "qwen21" => BundleReady("photo-qwen-image-21"),
            "upscale" => BundleReady("photo-upscale-4x"),
            "wan14b_fp8" => BundleReady("video-wan22-fp8-svi") || ExistsAny(
                "diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors",
                "diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors"),
            "wan14b" => BundleReady("video-wan22-gguf-q4") || ExistsAny(
                "diffusion_models/Wan2.2-T2V-A14B-HighNoise-Q4_K_M.gguf",
                "diffusion_models/Wan2.2-I2V-A14B-HighNoise-Q4_K_M.gguf"),
            "wan14b_q6" => ExistsAny(
                "diffusion_models/Wan2.2-T2V-A14B-HighNoise-Q6_K.gguf",
                "diffusion_models/Wan2.2-I2V-A14B-HighNoise-Q6_K.gguf"),
            "ltx25" => BundleReady("video-ltx25-audio"),
            _ => false
        };
    }

    public static string EngineTagToCatalogCategory(string engineTag) =>
        engineTag.ToLowerInvariant() is "wan14b_fp8" or "wan14b" or "wan14b_q6" or "ltx25" ? "video" : "photo";
}
