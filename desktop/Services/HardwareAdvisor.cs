namespace LocalAIHub.Services;

public sealed record GpuWikiEntry(
    string ModelName,
    string Series,          // "10", "16/20", "30", "40", "50"
    string Architecture,    // Pascal, Turing, Ampere, Ada Lovelace, Blackwell
    string ComputeCap,      // 6.1, 7.5, 8.6, 8.9, 12.0
    int VramGB,
    int BandwidthGBs,
    bool NativeFp16,
    bool NativeBf16,
    bool NativeFp8,
    bool NativeFp4,
    string TierLabel,
    string BestFormatHint,
    string SummaryRu
);

public sealed record AppleSiliconWikiEntry(
    string ChipName,
    string Generation,         // "M1", "M2", "M3", "M4", "M5", "M6"
    string Tier,               // "Base", "Pro", "Max", "Ultra"
    string GpuCores,
    int BandwidthGBs,
    int[] RamOptionsGB,
    bool HasDynamicCaching,
    bool HasNeuralGpuAccel,
    string BestFormatHint,
    string SummaryRu
);

public sealed record HardwareProfile(
    string DetectedGpuName,
    double DetectedVramGB,
    double DetectedRamGB,
    double FreeDiskGB,
    GpuWikiEntry? MatchedWiki,
    string RamTierLabel,
    string RamAdviceRu,
    string OverallTierSummaryRu,
    bool IsAppleSilicon = false,
    AppleSiliconWikiEntry? MatchedAppleChip = null,
    bool IsVulkanGpu = false
);

public enum ModelFitLevel
{
    Ideal,
    GoodWithOffload,
    Tight,
    Unsupported
}

public enum ModelPlacementGroup
{
    FullVram,      // 100% fits into GPU VRAM (fastest)
    VramPlusRam,   // Fits into GPU VRAM + system RAM (MoE fast or Dense slower / DynamicVRAM)
    TooHeavy       // Exceeds GPU + RAM budget (swap / OOM)
}

public sealed record ModelFitAssessment(
    ModelFitLevel Level,
    string BadgeText,
    string ReasonRu,
    string EstimatedSpeedHint,
    ModelPlacementGroup Placement = ModelPlacementGroup.FullVram,
    int RecommendedCtxTokens = 0,
    string RecommendedCtxHint = ""
);

public sealed record HfRepoFitAssessment(
    bool FitsOnHardware,
    int SortPriority, // 0 = Full GPU, 1 = GPU+RAM, 2 = Unknown size, 3 = Too heavy
    double? EstimatedParamsB,
    double? EstimatedQ4GB,
    string BadgeText,
    string BgHex,
    string FgHex
);

public static class HardwareAdvisor
{
    public static IReadOnlyList<GpuWikiEntry> GpuWiki { get; } =
    [
        // ---- GTX 10 Series (Pascal, SM 6.1) ----
        new("GTX 1060 6GB", "10", "Pascal", "6.1", 6, 192, false, false, false, false, "Базовый (6 ГБ)",
            "GGUF Q4_K_M / Q3_K_M (FP32 fallback, без Tensor Cores)",
            "Старт для компактных LLM (4B–8B Q4) и лёгкой генерации фото. Видео-модели 14B не рекомендуются."),
        new("GTX 1070 8GB", "10", "Pascal", "6.1", 8, 256, false, false, false, false, "Начальный (8 ГБ)",
            "GGUF Q4_K_M (без аппаратного FP16/BF16)",
            "Тянет 8B LLM и фото-модели с оффлоадом, но без Tensor Cores генерация медленнее RTX 30/40 в 3–5 раз."),
        new("GTX 1080 8GB", "10", "Pascal", "6.1", 8, 320, false, false, false, false, "Начальный (8 ГБ)",
            "GGUF Q4_K_M",
            "8B–12B LLM (Q4) и Z-Image Turbo / FLUX с оффлоадом. Для современного видео узкое место — отсутствие Tensor Cores."),
        new("GTX 1080 Ti 11GB", "10", "Pascal", "6.1", 11, 484, false, false, false, false, "Ветеран (11 ГБ)",
            "GGUF Q4_K_M / FP32 (высокая ПСП, но нет BF16/FP8)",
            "Большая память 11 ГБ позволяет запускать 12B–14B LLM целиком в VRAM и фото-модели, но без ускорения BF16/FP8."),

        // ---- GTX 16 / RTX 20 Series (Turing, SM 7.5) ----
        new("GTX 1660 Super / Ti 6GB", "16/20", "Turing", "7.5", 6, 336, true, false, false, false, "Базовый (6 ГБ)",
            "GGUF Q4_K_M / FP16",
            "Аппаратный FP16 заметно быстрее 10-й серии, но 6 ГБ VRAM ограничивают выбор 4B–8B LLM и лёгкими фото."),
        new("RTX 2060 6GB", "16/20", "Turing", "7.5", 6, 336, true, false, false, false, "Базовый (6 ГБ)",
            "GGUF Q4_K_M / FP16 Tensor",
            "Хороша для 8B LLM (~30–40 tok/s) и одиночных фото с оффлоадом; для видео 14B маловато VRAM."),
        new("RTX 2060 12GB", "16/20", "Turing", "7.5", 12, 336, true, false, false, false, "Средний (12 ГБ)",
            "GGUF Q4_K_M / FP16 (без BF16/FP8)",
            "Отличная бюджетная карта: 12 ГБ вмещают Gemma 4 12B / Qwen3-14B и FLUX 3 / Z-Image, а с 32 ГБ RAM — даже Wan 2.2 Q4."),
        new("RTX 2070 / 2070 Super 8GB", "16/20", "Turing", "7.5", 8, 448, true, false, false, false, "Начальный+ (8 ГБ)",
            "GGUF Q4_K_M / FP16 Tensor",
            "Быстрая для 8B–12B LLM (Q4) и Z-Image / FLUX 3 при 32 ГБ RAM; для Wan 2.2 14B нужен только Q4_K_M + 32 ГБ RAM."),
        new("RTX 2080 / 2080 Super 8GB", "16/20", "Turing", "7.5", 8, 496, true, false, false, false, "Начальный+ (8 ГБ)",
            "GGUF Q4_K_M / FP16 Tensor",
            "Высокая ПСП для 8B–12B LLM, но потолок 8 ГБ VRAM требует оффлоада на фото/видео нового поколения."),
        new("RTX 2080 Ti 11GB", "16/20", "Turing", "7.5", 11, 616, true, false, false, false, "Средний+ (11 ГБ)",
            "GGUF Q4_K_M / FP16 (616 ГБ/с!)",
            "Мощная ПСП 616 ГБ/с: 12B–14B LLM летят, Z-Image и FLUX 3 работают уверенно; Wan 2.2 лучше в GGUF Q4_K_M."),

        // ---- RTX 30 Series (Ampere, SM 8.6) ----
        new("RTX 3050 8GB", "30", "Ampere", "8.6", 8, 224, true, true, false, false, "Начальный (8 ГБ)",
            "GGUF Q4_K_M / BF16 + INT8",
            "Нативный BF16/INT8 открывает FLUX 3 и Qwen-Image 2.1 (с оффлоадом в RAM), плюс 8B–12B LLM."),
        new("RTX 3060 12GB", "30", "Ampere", "8.6", 12, 360, true, true, false, false, "Народный AI-хит (12 ГБ)",
            "GGUF Q4_K_M / INT8 convrot / BF16 (без аппаратного FP8)",
            "Лучший бюджетный вход в локальный AI: 12 ГБ + BF16/INT8 уверенно тянут Gemma 4 12B, Qwen3-14B, FLUX 3, Z-Image и Wan 2.2 Q4 (с 32 ГБ RAM)."),
        new("RTX 3060 Ti 8GB", "30", "Ampere", "8.6", 8, 448, true, true, false, false, "Начальный+ (8 ГБ)",
            "GGUF Q4_K_M / INT8",
            "Быстрее 3060 в вычислениях, но 8 ГБ VRAM ограничивают модели 8B–12B LLM и фото с оффлоадом."),
        new("RTX 3070 / 3070 Ti 8GB", "30", "Ampere", "8.6", 8, 448, true, true, false, false, "Начальный+ (8 ГБ)",
            "GGUF Q4_K_M / INT8",
            "Шустрая для 8B–12B LLM и Z-Image Turbo; на 14B видео-моделях сильно зависит от 32 ГБ RAM."),
        new("RTX 3080 10GB", "30", "Ampere", "8.6", 10, 760, true, true, false, false, "Средний (10 ГБ)",
            "GGUF Q4_K_M / INT8 convrot (760 ГБ/с)",
            "Очень высокая скорость токенов на 12B–14B LLM и быстрая генерация фото FLUX 3 / Z-Image; для Wan 2.2 оптимален Q4_K_M."),
        new("RTX 3080 12GB / 3080 Ti 12GB", "30", "Ampere", "8.6", 12, 912, true, true, false, false, "Продвинутый (12 ГБ)",
            "GGUF Q4_K_M / INT8 convrot (912 ГБ/с!)",
            "Огромная ПСП 912 ГБ/с: 14B LLM летают, FLUX 3 / Qwen 2.1 очень быстрые, Wan 2.2 Q4_K_M (и FP8 через DynamicVRAM при 32 ГБ RAM)."),
        new("RTX 3090 / 3090 Ti 24GB", "30", "Ampere", "8.6", 24, 936, true, true, false, false, "Энтузиаст (24 ГБ)",
            "GGUF Q6_K/Q8_0 / BF16 / INT8 (936 ГБ/с)",
            "24 ГБ VRAM без ограничений вмещают Qwen3.8-27B Q6_K, Qwen3.6-35B MoE целиком в VRAM, FLUX 3, Qwen 2.1, Wan 2.2 14B и LTX-2.5!"),

        // ---- RTX 40 Series (Ada Lovelace, SM 8.9) ----
        new("RTX 4060 8GB", "40", "Ada Lovelace", "8.9", 8, 272, true, true, true, false, "Начальный+ (8 ГБ • FP8)",
            "Аппаратный FP8 + INT8 + GGUF Q4_K_M",
            "Поддержка FP8 ускоряет современные модели, но 8 ГБ VRAM комфортны для 8B–12B LLM, Z-Image и FLUX 3 (с 32 ГБ RAM)."),
        new("RTX 4060 Ti 8GB", "40", "Ada Lovelace", "8.9", 8, 288, true, true, true, false, "Начальный+ (8 ГБ • FP8)",
            "Аппаратный FP8 + INT8 + GGUF Q4_K_M",
            "Аналогично 4060: быстрый FP8, но потолок 8 ГБ VRAM для тяжёлого видео."),
        new("RTX 4060 Ti 16GB", "40", "Ada Lovelace", "8.9", 16, 288, true, true, true, false, "Оптимальный AI (16 ГБ • FP8)",
            "FP8 Scaled DynamicVRAM + INT8 + Qwen3.8-27B Q3_K_P",
            "16 ГБ + аппаратный FP8 открывают ВЕСЬ современный стек (Qwen3.8-27B, FLUX 3, Wan 2.2 14B FP8, LTX-2.5), хоть и медленнее 4080 по ПСП."),
        new("RTX 4070 / 4070 Super 12GB", "40", "Ada Lovelace", "8.9", 12, 504, true, true, true, false, "Продвинутый (12 ГБ • FP8)",
            "Аппаратный FP8 + INT8 + GGUF Q4_K_M",
            "Отличный баланс: 12B–14B LLM на макс. скорости, FLUX 3 / Z-Image / Qwen 2.1, а при 32 ГБ RAM — Wan 2.2 14B FP8 DynamicVRAM."),
        new("RTX 4070 Ti 12GB", "40", "Ada Lovelace", "8.9", 12, 504, true, true, true, false, "Продвинутый (12 ГБ • FP8)",
            "Аппаратный FP8 + INT8 + GGUF Q4_K_M",
            "Очень быстрый рендер фото и 12B–14B LLM; для Wan 2.2 14B FP8 рекомендуется 32 ГБ RAM."),
        new("RTX 4070 Ti Super 16GB", "40", "Ada Lovelace", "8.9", 16, 672, true, true, true, false, "Топ-класс (16 ГБ • FP8)",
            "FP8 Scaled DynamicVRAM + INT8 + Qwen3.8-27B / 35B MoE",
            "Полноценная AI-станция: 16 ГБ и 672 ГБ/с тянут Qwen3.8-27B целиком в VRAM (~35 tok/s), FLUX 3, Wan 2.2 14B FP8 и LTX-2.5."),
        new("RTX 4080 / 4080 Super 16GB", "40", "Ada Lovelace", "8.9", 16, 717, true, true, true, false, "Топ-класс (16 ГБ • FP8)",
            "FP8 Scaled DynamicVRAM + INT8 + Qwen3.8-27B (39 tok/s)",
            "Золотой стандарт 16 ГБ: Qwen3.8-27B Q3_K_P (66/66 слоёв, ~39 tok/s), Qwen3.6-35B MoE (~40 tok/s при 32 ГБ RAM), FLUX 3, Wan 2.2 FP8 + SVI 2.0 Pro (~90 с/5с) и LTX-2.5!"),
        new("RTX 4090 24GB", "40", "Ada Lovelace", "8.9", 24, 1008, true, true, true, false, "Флагман (24 ГБ • FP8)",
            "FP8 Scaled + BF16 + GGUF Q6_K/Q8_0 (1008 ГБ/с)",
            "24 ГБ + аппаратный FP8: Wan 2.2 14B FP8, LTX-2.5, Qwen3.8-27B Q6_K и Qwen3.6-35B MoE помещаются почти без выгрузки на максимальной скорости."),

        // ---- RTX 50 Series (Blackwell, SM 12.0) ----
        new("RTX 5060 8GB", "50", "Blackwell", "12.0", 8, 448, true, true, true, true, "Новый начальный (8 ГБ • FP4/FP8)",
            "NVFP4 / FP8 + INT8 + GGUF Q4_K_M",
            "Быстрая GDDR7 и аппаратный FP4/FP8; 8 ГБ VRAM подходят для 8B–14B (FP4/Q4), Z-Image и FLUX 3 (с 32 ГБ RAM)."),
        new("RTX 5060 Ti 8GB", "50", "Blackwell", "12.0", 8, 448, true, true, true, true, "Новый начальный+ (8 ГБ • FP4/FP8)",
            "NVFP4 / FP8 + INT8 + GGUF Q4_K_M",
            "Высокая скорость на малых/средних моделях, но для 14B видео-моделей лучше 16-гигабайтная версия."),
        new("RTX 5060 Ti 16GB", "50", "Blackwell", "12.0", 16, 448, true, true, true, true, "Оптимальный Blackwell (16 ГБ • FP4/FP8)",
            "FP8 Scaled DynamicVRAM + NVFP4 + Qwen3.8-27B",
            "16 ГБ + Blackwell FP4/FP8: уверенно запускает весь стек LLM/Фото/Видео включая Wan 2.2 14B FP8 и LTX-2.5."),
        new("RTX 5070 12GB", "50", "Blackwell", "12.0", 12, 672, true, true, true, true, "Продвинутый Blackwell (12 ГБ • FP4/FP8)",
            "NVFP4 / FP8 + INT8 + GGUF Q4_K_M",
            "GDDR7 672 ГБ/с + FP4: очень быстрые 12B–14B LLM, FLUX 3 / Z-Image / Qwen 2.1, а с 32 ГБ RAM — Wan 2.2 14B FP8."),
        new("RTX 5070 Ti 16GB", "50", "Blackwell", "12.0", 16, 896, true, true, true, true, "Топ Blackwell (16 ГБ • FP4/FP8)",
            "FP8 Scaled DynamicVRAM + NVFP4 + Qwen3.8-27B (896 ГБ/с)",
            "Быстрее RTX 4080 по ПСП (896 ГБ/с) + аппаратный FP4: идеальная 16 ГБ станция для Qwen3.8-27B, FLUX 3, Wan 2.2 FP8 и LTX-2.5."),
        new("RTX 5080 16GB", "50", "Blackwell", "12.0", 16, 960, true, true, true, true, "Топ Blackwell (16 ГБ • FP4/FP8)",
            "FP8 Scaled DynamicVRAM + NVFP4 + Qwen3.8-27B (960 ГБ/с)",
            "Почти 1 ТБ/с ПСП: ~50+ tok/s на Qwen3.8-27B, мгновенный FLUX 3 / Z-Image, стремительный Wan 2.2 14B FP8 + SVI 2.0 Pro."),
        new("RTX 5090 32GB", "50", "Blackwell", "12.0", 32, 1792, true, true, true, true, "Ультра-флагман (32 ГБ • FP4/FP8)",
            "Любые модели в Q8_0 / BF16 / FP8 / NVFP4 (1792 ГБ/с!)",
            "Абсолютный максимум: 32 ГБ VRAM и 1,8 ТБ/с вмещают Qwen3.8-27B Q8_0, Qwen3.6-35B Q6_K, Wan 2.2 14B FP8 и LTX-2.5 22B целиком в видеопамяти!"),
    ];

    public static IReadOnlyList<AppleSiliconWikiEntry> AppleSiliconWiki { get; } =
    [
        // ---- M1 Family ----
        new("Apple M1", "M1", "Base", "7–8 GPU", 68, [8, 16], false, false,
            "GGUF Q4_K_M / MLX 4-bit (Metal, 68 ГБ/с)",
            "Стартовый Apple Silicon: 8 ГБ — только 4B LLM (Qwen 3 4B); 16 ГБ (~12 ГБ Metal) уверенно тянет 7B–8B LLM и Qwen 2.5 VL 7B Vision (~10–14 tok/s)."),
        new("Apple M1 Pro", "M1", "Pro", "14–16 GPU", 200, [16, 32], false, false,
            "GGUF Q4_K_M / Q5_K_M (200 ГБ/с Metal)",
            "200 ГБ/с — в 3 раза быстрее базового M1! На 16 ГБ летят 8B–12B LLM (~18–26 tok/s), на 32 ГБ (~24 ГБ Metal) отлично работают 24B–35B MoE и FLUX 3."),
        new("Apple M1 Max", "M1", "Max", "24–32 GPU", 400, [32, 64], false, false,
            "GGUF Q4_K_M / Q6_K (400 ГБ/с Metal!)",
            "Народный AI-хит на вторичке: 400 ГБ/с дают ~35–55 tok/s на 12B–35B MoE. На 64 ГБ (~48 ГБ Metal) целиком влезает Qwen3-Next 80B-A3B!"),
        new("Apple M1 Ultra", "M1", "Ultra", "48–64 GPU", 800, [64, 128], false, false,
            "GGUF Q6_K / Q8_0 (800 ГБ/с Metal!)",
            "Мощнейшая ПСП 800 ГБ/с (уровень RTX 4080) + 64–128 ГБ единой памяти: тянет 70B–80B модели целиком в Metal GPU на ~25–60 tok/s."),

        // ---- M2 Family ----
        new("Apple M2", "M2", "Base", "8–10 GPU", 100, [8, 16, 24], false, false,
            "GGUF Q4_K_M / MLX 4-bit (100 ГБ/с)",
            "100 ГБ/с (+47% к M1): 16 ГБ хороши для 8B–12B LLM (~13–18 tok/s), а редкая версия на 24 ГБ (~18 ГБ Metal) вмещает 20B–26B MoE и Vision!"),
        new("Apple M2 Pro", "M2", "Pro", "16–19 GPU", 200, [16, 32], false, false,
            "GGUF Q4_K_M (200 ГБ/с Metal)",
            "Стабильные 200 ГБ/с: 16 ГБ для 8B–12B Vision, 32 ГБ (~24 ГБ Metal) для Qwen 3.8 27B, Qwen 3.6 35B MoE (~25–35 tok/s) и FLUX 3 MPS."),
        new("Apple M2 Max", "M2", "Max", "30–38 GPU", 400, [32, 64, 96], false, false,
            "GGUF Q4_K_M / Q6_K (400 ГБ/с Metal)",
            "400 ГБ/с и до 96 ГБ Unified Memory (~72 ГБ Metal): идеальная мобильная AI-станция для 35B–80B MoE, 70B Dense и ComfyUI MPS."),
        new("Apple M2 Ultra", "M2", "Ultra", "60–76 GPU", 800, [64, 128, 192], false, false,
            "GGUF Q6_K / Q8_0 (800 ГБ/с Metal)",
            "До 192 ГБ Unified Memory и 800 ГБ/с: без проблем держит в памяти одновременно огромную 80B+ LLM и фото/видео стек."),

        // ---- M3 Family (Dynamic Caching + Mesh Shading) ----
        new("Apple M3", "M3", "Base", "8–10 GPU", 100, [8, 16, 24], true, false,
            "GGUF Q4_K_M / MLX 4-bit (100 ГБ/с + Dynamic Caching)",
            "Dynamic Caching эффективнее использует Metal-память: 16 ГБ — 8B–12B LLM/Vision, 24 ГБ — до 26B MoE (~14–22 tok/s)."),
        new("Apple M3 Pro", "M3", "Pro", "14–18 GPU", 150, [18, 36], true, false,
            "GGUF Q4_K_M / IQ4_XS (150 ГБ/с Metal)",
            "18 ГБ (~13.5 ГБ Metal) — идеальны для Gemma 3 12B Vision / GPT-OSS 20B; 36 ГБ (~27 ГБ Metal) вмещают Qwen 3.6 35B + mmproj (~20–28 tok/s)."),
        new("Apple M3 Max", "M3", "Max", "30–40 GPU", 400, [36, 48, 64, 96, 128], true, false,
            "GGUF Q4_K_M / Q6_K (300–400 ГБ/с Metal)",
            "Флагманский мобильный AI: 400 ГБ/с (40-core) и до 128 ГБ памяти — 35B–80B MoE летают на ~45–65 tok/s, FLUX 3 и Wan 2.2 Q4 работают в Metal."),
        new("Apple M3 Ultra", "M3", "Ultra", "60–80 GPU", 819, [96, 256, 512], true, false,
            "GGUF Q8_0 / BF16 (819 ГБ/с • до 512 ГБ!)",
            "Абсолютный монстр Mac Studio: до 512 ГБ Unified Memory и 819 ГБ/с — запускает крупнейшие открытые модели планеты (DeepSeek 671B Q4, Qwen 80B Q8)."),

        // ---- M4 Family (120 -> 546 GB/s) ----
        new("Apple M4", "M4", "Base", "10 GPU", 120, [16, 24, 32], true, false,
            "GGUF Q4_K_M / MLX 4-bit (120 ГБ/с)",
            "Теперь старт с 16 ГБ (+20% ПСП к M3): 16 ГБ — 8B–12B Vision (~15–22 tok/s), 24–32 ГБ — Qwen 3.6 35B MoE и Qwen 3.8 27B!"),
        new("Apple M4 Pro", "M4", "Pro", "16–20 GPU", 273, [24, 48, 64], true, false,
            "GGUF Q4_K_M / Q6_K (273 ГБ/с Metal!)",
            "Гигантский скачок ПСП до 273 ГБ/с! 24 ГБ — все 12B–26B MoE (~35–45 tok/s); 48–64 ГБ (~36–48 ГБ Metal) — Qwen3-Next 80B-A3B на ~35 tok/s!"),
        new("Apple M4 Max", "M4", "Max", "32–40 GPU", 546, [36, 48, 64, 128], true, false,
            "GGUF Q4_K_M / Q6_K / Q8_0 (410–546 ГБ/с Metal!)",
            "Топ-ноутбук для AI: 546 ГБ/с (40 GPU) дают ~60–85 tok/s на MoE 35B/80B и ~25–35 tok/s на Qwen 3.8 27B; 64–128 ГБ без ограничений."),
        new("Apple M4 Ultra", "M4", "Ultra", "64–80 GPU", 1092, [128, 256, 512], true, false,
            "GGUF Q8_0 / BF16 (>1 ТБ/с Metal)",
            "Сверхвысокая ПСП >1 ТБ/с и огромный пул Unified Memory — настольный AI-кластер в корпусе Mac Studio/Pro."),

        // ---- M5 Family (GPU Neural Accelerators / FP8+Matrix in GPU cores) ----
        new("Apple M5", "M5", "Base", "10 GPU (Neural Accel)", 153, [16, 24, 32], true, true,
            "GGUF Q4_K_M / Q4_K_XL / MLX (153 ГБ/с + GPU Neural Accel)",
            "Встроенные матричные Neural Accelerators в каждом GPU-ядре ускоряют prefill промптов в 3–4×! 24–32 ГБ — отличный компактный Mac для 24B–35B MoE."),
        new("Apple M5 Pro", "M5", "Pro", "16–20 GPU (Neural Accel)", 307, [24, 48, 64], true, true,
            "GGUF Q4_K_M / Q6_K (307 ГБ/с + GPU Neural Accel)",
            "307 ГБ/с + быстрый prefill длинного контекста и Vision-картинок: 48–64 ГБ уверенно тянут 35B–80B MoE (~40–55 tok/s) и FLUX 3 MPS."),
        new("Apple M5 Max", "M5", "Max", "32–40 GPU (Neural Accel)", 614, [48, 64, 128], true, true,
            "GGUF Q6_K / Q8_0 (546–614 ГБ/с + GPU Neural Accel)",
            "До 614 ГБ/с и аппаратное ускорение матриц: мгновенный Vision, 80B MoE на ~70+ tok/s и быстрый рендер фото/видео в Metal/MPS."),
        new("Apple M5 Ultra", "M5", "Ultra", "64–80 GPU (Neural Accel)", 1228, [128, 256, 512], true, true,
            "GGUF Q8_0 / FP16 (>1,2 ТБ/с Metal)",
            "Флагманская рабочая станция поколения M5: молниеносный prefill и >1,2 ТБ/с под любые открытые LLM и медиа-пайплайны."),

        // ---- M6 Family (Next-gen 2nm Apple Silicon) ----
        new("Apple M6", "M6", "Base", "10–12 GPU (Next-Gen Neural)", 180, [16, 24, 32], true, true,
            "GGUF Q4_K_M / MLX 4-bit (180+ ГБ/с Metal)",
            "Поколение 2 нм: высокая энергоэффективность, быстрый Vision и ~22–32 tok/s на 8B–26B MoE моделях при 16–32 ГБ памяти."),
        new("Apple M6 Pro", "M6", "Pro", "18–24 GPU (Next-Gen Neural)", 340, [24, 48, 64], true, true,
            "GGUF Q4_K_M / Q6_K (340+ ГБ/с Metal)",
            "Мощный универсал поколения M6: ~45–65 tok/s на 35B/80B MoE, комфортный 64K–128K контекст и быстрая генерация фото в MPS."),
        new("Apple M6 Max", "M6", "Max", "36–48 GPU (Next-Gen Neural)", 680, [48, 64, 128, 192], true, true,
            "GGUF Q6_K / Q8_0 / FP16 (680+ ГБ/с Metal)",
            "Ультра-флагман мобильных Mac: 680+ ГБ/с и до 192 ГБ Unified Memory — бескомпромиссный локальный AI для 80B–200B+ моделей и студии.")
    ];

    public static AppleSiliconWikiEntry? MatchAppleSilicon(string? chipName)
    {
        if (string.IsNullOrWhiteSpace(chipName)) return null;
        string u = chipName.ToUpperInvariant();
        if (!u.Contains("APPLE") && !System.Text.RegularExpressions.Regex.IsMatch(u, @"\bM[1-6]\b"))
            return null;

        string gen = "";
        for (int g = 6; g >= 1; g--)
        {
            if (u.Contains($"M{g}")) { gen = $"M{g}"; break; }
        }
        if (string.IsNullOrEmpty(gen)) return null;

        string tier = u.Contains("ULTRA") ? "Ultra"
                    : u.Contains("MAX") ? "Max"
                    : u.Contains("PRO") ? "Pro"
                    : "Base";

        return AppleSiliconWiki.FirstOrDefault(e => e.Generation == gen && e.Tier == tier)
            ?? AppleSiliconWiki.FirstOrDefault(e => e.Generation == gen);
    }

    public static double EstimateAppleMetalVramGB(double unifiedRamGB)
    {
        if (unifiedRamGB <= 8.5) return Math.Max(4.0, unifiedRamGB - 2.5);
        if (unifiedRamGB <= 16.5) return Math.Round(unifiedRamGB * 0.72, 1); // ~11.5–12 GB
        if (unifiedRamGB >= 90) return Math.Round(unifiedRamGB * 0.80, 1);
        return Math.Round(unifiedRamGB * 0.75, 1); // macOS default iogpu.wired_limit_mb (~75%)
    }

    public static HardwareProfile BuildAppleSiliconProfile(string? chipName, double unifiedRamGB, double freeDiskGB)
    {
        var matched = MatchAppleSilicon(chipName);
        double metalBudget = EstimateAppleMetalVramGB(unifiedRamGB);
        string displayName = matched?.ChipName ?? (string.IsNullOrWhiteSpace(chipName) ? "Apple Silicon" : chipName.Trim());
        string ramLabel = $"{unifiedRamGB:F0} ГБ Unified Memory (~{metalBudget:F1} ГБ Metal GPU)";
        string ramAdvice = unifiedRamGB switch
        {
            <= 10 => "8 ГБ Unified Memory — хватит для Qwen 3 4B (2.3 ГБ) и лёгкого чата; для Vision/Фото лучше от 16 ГБ.",
            <= 18 => "16 ГБ Unified Memory (~12 ГБ Metal) — отлично для 7B–12B LLM, Qwen 2.5 VL 7B Vision, Gemma 3 12B Vision и FLUX 3 (MPS).",
            <= 28 => "24 ГБ Unified Memory (~18 ГБ Metal) — комфортно для 20B–26B MoE (GPT-OSS 20B, Gemma 4 26B), Devstral 24B, Vision и FLUX 3.",
            <= 40 => "32–36 ГБ Unified Memory (~24–27 ГБ Metal) — топ для Qwen 3.6 35B MoE + mmproj, Qwen 3.8 27B, FLUX 3 и Wan 2.2 Q4!",
            <= 70 => "48–64 ГБ Unified Memory (~36–48 ГБ Metal) — целиком вмещает Qwen3-Next 80B-A3B, крупные Vision-модели и видео-пайплайны!",
            _ => "96+ ГБ Unified Memory — без ограничений для крупнейших 80B–400B+ моделей целиком в Metal GPU."
        };

        string bwText = matched is not null ? $", {matched.BandwidthGBs} ГБ/с" : "";
        string summary = $"{displayName} ({unifiedRamGB:F0} ГБ Unified → ~{metalBudget:F0} ГБ Metal GPU{bwText}) — macOS Apple Silicon.";

        return new HardwareProfile(
            displayName,
            metalBudget,
            unifiedRamGB,
            freeDiskGB,
            null,
            ramLabel,
            ramAdvice,
            summary,
            IsAppleSilicon: true,
            MatchedAppleChip: matched);
    }

    public static HardwareProfile BuildSimulatedAppleProfile(AppleSiliconWikiEntry chip, double unifiedRamGB, double freeDiskGB = 250) =>
        BuildAppleSiliconProfile(chip.ChipName, unifiedRamGB, freeDiskGB);

    public static GpuWikiEntry? MatchGpu(string? gpuName, double detectedVramGB)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return null;
        string norm = gpuName.ToUpperInvariant()
            .Replace("NVIDIA", "")
            .Replace("GEFORCE", "")
            .Replace("LAPTOP GPU", "")
            .Trim();

        // Try exact model number tokens first (most specific longest match)
        var scored = GpuWiki.Select(entry =>
        {
            string key = entry.ModelName.ToUpperInvariant();
            string baseNum = key.Split(' ')[1]; // e.g. "4080", "3060", "1080"
            int score = 0;
            if (norm.Contains(baseNum)) score += 100;
            if (key.Contains("TI SUPER") && norm.Contains("TI SUPER")) score += 50;
            else if (key.Contains("TI") && norm.Contains("TI") && !key.Contains("TI SUPER")) score += 35;
            else if (!key.Contains("TI") && !norm.Contains("TI")) score += 15;
            if (key.Contains("SUPER") && norm.Contains("SUPER")) score += 20;
            if (detectedVramGB > 0)
            {
                double diff = Math.Abs(entry.VramGB - detectedVramGB);
                if (diff <= 1.2) score += 40;
                else if (diff <= 2.5) score += 15;
                else score -= 30;
            }
            return (entry, score);
        })
        .OrderByDescending(x => x.score)
        .FirstOrDefault();

        return scored.score >= 100 ? scored.entry : null;
    }

    public static HardwareProfile BuildProfile(string? gpuName, double vramGB, double ramGB, double freeDiskGB)
    {
        if (MatchAppleSilicon(gpuName) is not null ||
            (!string.IsNullOrWhiteSpace(gpuName) && gpuName.Contains("Apple", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildAppleSiliconProfile(gpuName, ramGB > 0 ? ramGB : vramGB, freeDiskGB);
        }

        var matched = MatchGpu(gpuName, vramGB);
        double effVram = vramGB > 0 ? vramGB : (matched?.VramGB ?? 0);
        bool isVulkanGpu = matched is null && !string.IsNullOrWhiteSpace(gpuName) &&
            (gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
             || gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase)
             || gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
             || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
        var (ramLabel, ramAdvice) = DescribeRamTier(ramGB, effVram);

        string summary;
        if (matched is not null)
        {
            string caps = (matched.NativeFp4 ? "FP4 + " : "") +
                          (matched.NativeFp8 ? "FP8 + " : "") +
                          (matched.NativeBf16 ? "BF16" : matched.NativeFp16 ? "FP16" : "FP32");
            summary = $"{matched.ModelName} ({matched.Architecture}, {effVram:F0} ГБ VRAM, {caps}) + {ramGB:F0} ГБ RAM — {matched.TierLabel}.";
        }
        else if (isVulkanGpu && effVram > 0)
        {
            summary = $"{gpuName} ({effVram:F0} ГБ Vulkan) + {ramGB:F0} ГБ RAM — Vulkan GPU-ускорение LLM (llama.cpp).";
        }
        else if (effVram > 0)
        {
            summary = $"{gpuName ?? "NVIDIA GPU"} ({effVram:F0} ГБ VRAM) + {ramGB:F0} ГБ RAM.";
        }
        else
        {
            summary = $"NVIDIA GPU не обнаружена ({ramGB:F0} ГБ RAM) — возможен только CPU-режим для лёгких LLM.";
        }

        return new HardwareProfile(
            gpuName ?? "Не обнаружена",
            effVram,
            ramGB,
            freeDiskGB,
            matched,
            ramLabel,
            ramAdvice,
            summary,
            IsVulkanGpu: isVulkanGpu);
    }

    public static HardwareProfile BuildSimulatedProfile(GpuWikiEntry gpu, double ramGB, double freeDiskGB = 200)
    {
        return BuildProfile($"NVIDIA GeForce {gpu.ModelName}", gpu.VramGB, ramGB, freeDiskGB);
    }

    public static (string label, string advice) DescribeRamTier(double ramGB, double vramGB)
    {
        if (ramGB < 20)
        {
            return ("≤ 16 ГБ RAM (строгий лимит оффлоада)",
                "При 16 ГБ оперативной памяти лучше выбирать модели, которые целиком помещаются в VRAM (размер ≤ VRAM − 1,5 ГБ). Тяжёлые видео-модели 14B/22B с DynamicVRAM и крупные MoE-LLM могут упираться в файл подкачки.");
        }
        if (ramGB < 48)
        {
            return ("32 ГБ RAM (оптимальный баланс DynamicVRAM)",
                vramGB >= 12
                    ? "Идеальная связка для DynamicVRAM (Wan 2.2 14B FP8, LTX-2.5 22B) и быстрых MoE-LLM с частичной выгрузкой экспертов в RAM (Qwen3.6-35B-A3B @ ~40 tok/s)."
                    : "32 ГБ RAM компенсируют небольшой объём VRAM — можно запускать FLUX 3 / Qwen 2.1 и Wan 2.2 Q4 с подкачкой из RAM, но генерация будет медленнее.");
        }
        return ("64+ ГБ RAM (максимальный запас)",
            "Огромный запас под кэширование слоёв DynamicVRAM, параллельную загрузку High/Low экспертов Wan 2.2 без подкачки с диска, и крупные Q6_K/Q8_0 MoE-LLM.");
    }

    /// <summary>
    /// Estimates a practical KV-cache-safe context window (tokens) for llama.cpp with Q8_0 KV cache + Flash Attention.
    /// </summary>
    public static (int Tokens, string BadgeText) EstimateRecommendedContext(
        double modelSizeGB,
        bool isMoe,
        HardwareProfile hw,
        ModelPlacementGroup placement)
    {
        if (placement == ModelPlacementGroup.TooHeavy || modelSizeGB <= 0)
            return (0, "");

        double vram = hw.DetectedVramGB;
        double ram = hw.DetectedRamGB;

        // Free memory available for KV-cache + compute buffers
        double kvBudgetGB;
        if (placement == ModelPlacementGroup.FullVram)
        {
            kvBudgetGB = Math.Max(0.35, vram - modelSizeGB - 1.0);
        }
        else
        {
            double ramUsable = Math.Max(0, ram - 10.0);
            kvBudgetGB = Math.Max(0.35, (vram + ramUsable) - modelSizeGB - 1.5);
        }

        // Approximate KV-cache footprint per 1K tokens under Q8_0 KV quantization (GQA models)
        double gbPer1K = modelSizeGB switch
        {
            <= 5.0 => 0.036,
            <= 10.5 => 0.055,
            _ when isMoe => 0.068,
            <= 22.0 => 0.105,
            _ => 0.140
        };

        double rawK = kvBudgetGB / gbPer1K;
        // Cap practical context for RAM-offloaded models so prompt prefill remains responsive
        double maxK = placement == ModelPlacementGroup.FullVram
            ? (modelSizeGB <= 5.0 && hw.IsVulkanGpu ? 32.0 : 128.0)
            : (isMoe ? 64.0 : 32.0);
        double effK = Math.Min(rawK, maxK);

        if (effK >= 120) return (131072, "CTX ~128K");
        if (effK >= 60) return (65536, "CTX ~64K");
        if (effK >= 30) return (32768, "CTX ~32K");
        if (effK >= 15) return (16384, "CTX ~16K");
        if (effK >= 7.5) return (8192, "CTX ~8K");
        if (effK >= 3.5) return (4096, "CTX ~4K");
        return (2048, "CTX ~2K");
    }

    /// <summary>
    /// Parses parameter count from a Hugging Face repo ID (e.g. Qwen3-8B-GGUF, Gemma-3-12B-it-GGUF, Qwen3-30B-A3B-GGUF)
    /// and assesses whether a Q4_K_M quantization will fit on the given hardware without SSD swap.
    /// </summary>
    public static HfRepoFitAssessment EstimateHfRepoFit(string repoId, HardwareProfile hw)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return new(true, 2, null, null, "? ФАЙЛЫ", "#19191F", "#A1A1AA");

        string lower = repoId.ToLowerInvariant();
        bool isMoe = lower.Contains("moe")
            || System.Text.RegularExpressions.Regex.IsMatch(lower, @"a\d+(?:\.\d+)?b")
            || lower.Contains("mixtral")
            || System.Text.RegularExpressions.Regex.IsMatch(lower, @"\d+x\d+b");

        double? paramsB = null;
        // Handle 8x7B / 8x22B MoE naming first
        var moeMult = System.Text.RegularExpressions.Regex.Match(lower, @"(?<![a-z0-9])(\d+)x(\d+(?:\.\d+)?)b(?![a-z0-9])");
        if (moeMult.Success &&
            double.TryParse(moeMult.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double experts) &&
            double.TryParse(moeMult.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out double eachB))
        {
            paramsB = experts * eachB * 0.85; // shared layers overlap
        }
        else
        {
            // Match main model parameter size (exclude active-param tokens like "a3b" / "a4b")
            var matches = System.Text.RegularExpressions.Regex.Matches(lower, @"(?<![a-z0-9])(\d+(?:\.\d+)?)b(?![a-z0-9])");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double b) && b is >= 0.4 and <= 1000)
                {
                    if (paramsB is null || b > paramsB.Value)
                        paramsB = b;
                }
            }
        }

        if (paramsB is null)
            return new(true, 2, null, null, "? ПРОВЕРЬ", "#19191F", "#A1A1AA");

        double estQ4GB = Math.Round(paramsB.Value * 0.62, 1);
        double vramUsable = Math.Max(0, hw.DetectedVramGB - 1.5);
        double ramUsable = Math.Max(0, hw.DetectedRamGB - 10.0);
        double totalPool = hw.IsAppleSilicon
            ? hw.DetectedVramGB + 1.0
            : vramUsable + ramUsable + (isMoe ? 2.5 : 0.0);

        if (hw.DetectedVramGB >= 4 && estQ4GB <= vramUsable)
            return new(true, 0, paramsB, estQ4GB, $"■ GPU (~{estQ4GB:0.#} ГБ)", "#11241C", "#34D399");

        if (hw.DetectedVramGB >= 6 && estQ4GB <= totalPool)
            return new(true, 1, paramsB, estQ4GB, isMoe ? $"◧ MoE+ОЗУ (~{estQ4GB:0.#} ГБ)" : $"◧ GPU+ОЗУ (~{estQ4GB:0.#} ГБ)", "#241D0E", "#F59E0B");

        return new(false, 3, paramsB, estQ4GB, $"□ ТЯЖЁЛАЯ (~{estQ4GB:0.#} ГБ)", "#261416", "#F87171");
    }

    public static ModelFitAssessment EvaluateModel(CatalogModelSpec model, HardwareProfile hw)
    {
        // Standalone add-on modules (e.g. mmproj for a 35B parent model) MUST inherit their parent's
        // placement group so a 0.8 GB Vision add-on never falsely shows up in the "100% GPU" section
        // when its parent model is in "VRAM + ОЗУ" or "SWAP"!
        if (!string.IsNullOrWhiteSpace(model.ParentModelId))
        {
            var parent = ModelCatalog.FindById(model.ParentModelId);
            if (parent is not null)
            {
                var combined = parent with { TotalSizeGB = Math.Round(parent.TotalSizeGB + model.TotalSizeGB, 2), ParentModelId = null };
                var parentFit = EvaluateModel(combined, hw);
                return parentFit with
                {
                    BadgeText = parentFit.Placement switch
                    {
                        ModelPlacementGroup.FullVram => "■ МОДУЛЬ (GPU)",
                        ModelPlacementGroup.VramPlusRam => "◧ МОДУЛЬ (GPU+ОЗУ)",
                        _ => "□ МОДУЛЬ (SWAP)"
                    },
                    ReasonRu = $"Дополнение к «{parent.Name}» (вместе ~{combined.TotalSizeGB:0.##} ГБ). {parentFit.ReasonRu}"
                };
            }
        }

        if (hw.IsAppleSilicon)
            return EvaluateModelForAppleSilicon(model, hw);

        double vram = hw.DetectedVramGB;
        double ram = hw.DetectedRamGB;
        bool hasFp8 = !hw.IsVulkanGpu && (hw.MatchedWiki?.NativeFp8 ?? (vram >= 16));
        bool hasBf16 = !hw.IsVulkanGpu && (hw.MatchedWiki?.NativeBf16 ?? (vram >= 8));
        int bw = hw.MatchedWiki?.BandwidthGBs
            ?? (hw.IsVulkanGpu
                ? (hw.DetectedGpuName.Contains("140V", StringComparison.OrdinalIgnoreCase) || hw.DetectedGpuName.Contains("130V", StringComparison.OrdinalIgnoreCase) ? 136 : 220)
                : (vram >= 16 ? 700 : 360));

        // ===================== 1. ТЕКСТОВЫЕ LLM (честное разделение VRAM vs VRAM+RAM) =====================
        if (model.Category == "text")
        {
            double vramUsable = Math.Max(0, vram - 1.5);
            double ramUsable = Math.Max(0, ram - 10.0); // OS + apps reserve
            double totalPool = vramUsable + ramUsable;

            // Case A: Fits 100% into GPU VRAM!
            if (vram >= 4 && model.TotalSizeGB <= vramUsable)
            {
                // Realistic tok/s scaled by GPU memory bandwidth & model size
                double estTok = model.IsMoe
                    ? Math.Clamp(bw / 8.5, hw.IsVulkanGpu ? 18 : 45, 130)
                    : Math.Clamp((bw * 0.72) / Math.Max(2.2, model.TotalSizeGB), hw.IsVulkanGpu ? 10 : 18, 140);
                int lo = Math.Max(hw.IsVulkanGpu ? 8 : 15, (int)(estTok * 0.80));
                int hi = Math.Max(lo + 6, (int)(estTok * 1.15));
                string speed = hw.IsVulkanGpu ? $"~{lo}–{hi} tok/s (Vulkan)" : $"~{lo}–{hi} tok/s (100% VRAM)";
                var (ctxTok, ctxBadge) = EstimateRecommendedContext(model.TotalSizeGB, model.IsMoe, hw, ModelPlacementGroup.FullVram);

                return new(
                    ModelFitLevel.Ideal,
                    hw.IsVulkanGpu ? "■ 100% VULKAN" : "■ 100% VRAM",
                    hw.IsVulkanGpu
                        ? $"Целиком помещается в {vram:F0} ГБ памяти Vulkan GPU (весит {model.TotalSizeGB:0.##} ГБ) — быстрый локальный отклик."
                        : $"Целиком помещается в твои {vram:F0} ГБ VRAM (весит {model.TotalSizeGB:0.##} ГБ) — максимальная скорость и мгновенный отклик.",
                    speed,
                    ModelPlacementGroup.FullVram,
                    ctxTok,
                    ctxBadge);
            }

            // Case B: Fits into GPU VRAM + System RAM (without heavy SSD swap)
            bool fitsVramPlusRam = vram >= 6 && model.TotalSizeGB <= totalPool + (model.IsMoe ? 2.5 : 0.0);
            if (fitsVramPlusRam)
            {
                double inRamGb = Math.Max(1.0, model.TotalSizeGB - Math.Max(2.0, vramUsable - 1.5));
                var (ctxTok, ctxBadge) = EstimateRecommendedContext(model.TotalSizeGB, model.IsMoe, hw, ModelPlacementGroup.VramPlusRam);
                if (model.IsMoe)
                {
                    // MoE only activates ~3B weights per token -> surprisingly fast even with RAM offload!
                    string speed = model.TotalSizeGB <= 22 ? "~32–45 tok/s (MoE + ОЗУ)"
                        : model.TotalSizeGB <= 30 ? "~26–36 tok/s (MoE + ОЗУ)"
                        : "~20–30 tok/s (MoE + ОЗУ)";
                    return new(
                        ModelFitLevel.GoodWithOffload,
                        "◧ VRAM + ОЗУ (MoE)",
                        $"Влезет в связку {vram:F0} ГБ VRAM + {ram:F0} ГБ ОЗУ (~{inRamGb:F0} ГБ в RAM). Т.к. это MoE (активно ~3B параметров), скорость останется высокой.",
                        speed,
                        ModelPlacementGroup.VramPlusRam,
                        ctxTok,
                        ctxBadge);
                }
                else
                {
                    // Dense model split across GPU + RAM -> accurate but slower (~3-8 tok/s)
                    double ramFrac = inRamGb / Math.Max(1.0, model.TotalSizeGB);
                    string speed = ramFrac < 0.25 ? "~8–14 tok/s (Dense + ОЗУ)" : "~3–6 tok/s (Dense + ОЗУ)";
                    return new(
                        ModelFitLevel.Tight,
                        "◧ VRAM + ОЗУ (Dense)",
                        $"Запустится через --fit on (~{inRamGb:F0} ГБ слоёв в ОЗУ из {ram:F0} ГБ). Для плотной модели скорость составит {speed.Split('(')[0].Trim()} — подходит, если важнее макс. качество.",
                        speed,
                        ModelPlacementGroup.VramPlusRam,
                        ctxTok,
                        ctxBadge);
                }
            }

            // Case C: Too heavy even for VRAM + RAM (would heavily swap to SSD)
            string whyHeavy = $"Весит {model.TotalSizeGB:0.##} ГБ — не поместится в {vram:F0} ГБ VRAM + {ram:F0} ГБ ОЗУ без тяжёлого свопа на SSD (нужно от {model.MinVramGB} ГБ VRAM и {model.MinRamGB}+ ГБ RAM).";
            return new(ModelFitLevel.Unsupported, "□ SWAP", whyHeavy, "< 2 tok/s (swap)", ModelPlacementGroup.TooHeavy);
        }

        // ===================== 2. ФОТО И ВИДЕО (ComfyUI / DynamicVRAM) =====================
        if (vram < model.MinVramGB - 0.8 || ram < model.MinRamGB - 2.0)
        {
            string why = vram < model.MinVramGB - 0.8
                ? $"Нужно от {model.MinVramGB} ГБ VRAM (у тебя {vram:F0} ГБ)."
                : $"Нужно от {model.MinRamGB} ГБ RAM (у тебя {ram:F0} ГБ).";
            return new(ModelFitLevel.Unsupported, "□ НЕ ВЛЕЗЕТ", why, "—", ModelPlacementGroup.TooHeavy);
        }

        // Peak active working set (for video bundles, only 1 branch T2V or I2V loads at a time, not all 66 GB)
        bool mediaFitsFullVram = model.Category == "photo"
            ? vram >= model.IdealVramGB - 0.8
            : vram >= (model.Id.Contains("ltx") ? 24 : 24);
        var mediaPlacement = mediaFitsFullVram ? ModelPlacementGroup.FullVram : ModelPlacementGroup.VramPlusRam;

        // Special architecture advice: FP8 Scaled shines on Ada/Blackwell (40/50 series)
        if (model.PrefersFp8 && !hasFp8)
        {
            return new(
                ModelFitLevel.GoodWithOffload,
                "◧ ЭМУЛЯЦИЯ FP8",
                $"На {hw.MatchedWiki?.Architecture ?? "этой карте"} нет аппаратного FP8 (появился в RTX 40/50) — будет работать через DynamicVRAM + ОЗУ, но на 30/20 серии часто выгоднее GGUF Q4_K_M.",
                model.Category == "video" ? "~2–3 мин / 5с" : "~20–40 с",
                mediaPlacement);
        }

        if (model.PrefersBf16 && !hasBf16)
        {
            return new(
                ModelFitLevel.Tight,
                "▲ НЕТ BF16",
                $"На {hw.MatchedWiki?.Architecture ?? "Pascal/Turing"} нет нативного BF16 — модель запустится через FP16/FP32, но будет заметно медленнее.",
                "Медленно",
                mediaPlacement);
        }

        if (vram >= model.IdealVramGB - 0.8 && ram >= model.IdealRamGB - 2.0)
        {
            string speed = model.Category switch
            {
                "photo" => mediaFitsFullVram ? "~8–18 с / кадр (VRAM)" : "~15–30 с / кадр (VRAM + ОЗУ)",
                _ => hasFp8 ? "~60–95 с / 5с (DynamicVRAM)" : "~90–140 с / 5с (DynamicVRAM)"
            };
            string badge = mediaFitsFullVram ? "■ 100% VRAM" : "■ DYNAMIC VRAM";
            return new(
                ModelFitLevel.Ideal,
                badge,
                $"Отлично работает на {vram:F0} ГБ VRAM + {ram:F0} ГБ RAM{(hasFp8 && model.PrefersFp8 ? " с аппаратным FP8" : "")}.",
                speed,
                mediaPlacement);
        }

        if (vram >= model.MinVramGB - 0.5 && ram >= model.IdealRamGB - 2.0)
        {
            string speed = model.Category == "photo" ? "~20–45 с / кадр (VRAM + ОЗУ)" : "~2–3,5 мин / 5с (DynamicVRAM)";
            return new(
                ModelFitLevel.GoodWithOffload,
                "◧ VRAM + ОЗУ",
                $"Запустится за счёт {ram:F0} ГБ ОЗУ, но для максимальной скорости лучше {model.IdealVramGB}+ ГБ VRAM.",
                speed,
                ModelPlacementGroup.VramPlusRam);
        }

        return new(
            ModelFitLevel.Tight,
            "▲ ВПРИТЫК",
            $"Мало запаса по памяти (рекомендуется {model.IdealVramGB} ГБ VRAM и {model.IdealRamGB} ГБ RAM).",
            "Медленно / риск OOM",
            ModelPlacementGroup.VramPlusRam);
    }

    private static ModelFitAssessment EvaluateModelForAppleSilicon(CatalogModelSpec model, HardwareProfile hw)
    {
        double unified = hw.DetectedRamGB;
        double metalBudget = hw.DetectedVramGB > 0 ? hw.DetectedVramGB : EstimateAppleMetalVramGB(unified);
        int bw = hw.MatchedAppleChip?.BandwidthGBs ?? 200;
        string chip = hw.MatchedAppleChip?.ChipName ?? hw.DetectedGpuName;

        if (model.Category == "text")
        {
            // Reserve ~1.5 GB inside Metal budget for KV-cache & context
            double safeMetal = Math.Max(2.0, metalBudget - 1.5);
            if (model.TotalSizeGB <= safeMetal)
            {
                // On Apple Silicon, LLM generation speed is directly memory-bandwidth bound
                double activeGB = model.IsMoe ? Math.Min(model.TotalSizeGB, 3.2) : Math.Max(2.0, model.TotalSizeGB);
                double estTok = Math.Clamp((bw * 0.72) / activeGB, 8, 130);
                int lo = Math.Max(6, (int)(estTok * 0.80));
                int hi = Math.Max(lo + 5, (int)(estTok * 1.15));
                var (ctxTok, ctxBadge) = EstimateRecommendedContext(model.TotalSizeGB, model.IsMoe, hw, ModelPlacementGroup.FullVram);
                return new(
                    ModelFitLevel.Ideal,
                    "■ 100% METAL GPU",
                    $"Целиком помещается в Unified Memory ({unified:F0} ГБ → ~{metalBudget:F0} ГБ Metal на {chip}, {bw} ГБ/с). Отличная скорость без свопа.",
                    $"~{lo}–{hi} tok/s (Metal)",
                    ModelPlacementGroup.FullVram,
                    ctxTok,
                    ctxBadge);
            }

            if (model.TotalSizeGB <= metalBudget + 1.5)
            {
                double activeGB = model.IsMoe ? Math.Min(model.TotalSizeGB, 3.5) : Math.Max(2.5, model.TotalSizeGB);
                double estTok = Math.Clamp((bw * 0.55) / activeGB, 5, 80);
                var (ctxTok, ctxBadge) = EstimateRecommendedContext(model.TotalSizeGB, model.IsMoe, hw, ModelPlacementGroup.VramPlusRam);
                return new(
                    ModelFitLevel.Tight,
                    "▲ ВПРИТЫК (METAL)",
                    $"Весит {model.TotalSizeGB:0.##} ГБ при лимите Metal ~{metalBudget:F0} ГБ из {unified:F0} ГБ Unified RAM — закрой лишние вкладки браузера или уменьши контекст.",
                    $"~{Math.Max(4, (int)(estTok * 0.8))}–{Math.Max(8, (int)(estTok * 1.1))} tok/s",
                    ModelPlacementGroup.VramPlusRam,
                    ctxTok,
                    ctxBadge);
            }

            return new(
                ModelFitLevel.Unsupported,
                "□ НЕ ВЛЕЗЕТ",
                $"Весит {model.TotalSizeGB:0.##} ГБ — превышает доступный бюджет Metal (~{metalBudget:F0} ГБ из {unified:F0} ГБ). Нужен Mac с {Math.Ceiling((model.TotalSizeGB + 4) / 0.75 / 8) * 8:0}+ ГБ памяти.",
                "< 2 tok/s (swap)",
                ModelPlacementGroup.TooHeavy);
        }

        // Photo & Video on macOS (ComfyUI PyTorch MPS — unified memory, no CUDA FP8)
        double neededUnified = model.Category == "photo" ? Math.Max(14, model.MinVramGB + 6) : Math.Max(24, model.MinVramGB + 12);
        if (unified < neededUnified - 1.0)
        {
            return new(
                ModelFitLevel.Unsupported,
                "□ МАЛО UNIFIED RAM",
                $"Для {model.Name} в ComfyUI MPS на macOS нужно от {neededUnified:0} ГБ Unified Memory (у тебя {unified:F0} ГБ).",
                "—",
                ModelPlacementGroup.TooHeavy);
        }

        if (model.Category == "photo")
        {
            string speed = bw >= 400 ? "~15–35 с / кадр (MPS)" : bw >= 200 ? "~30–60 с / кадр (MPS)" : "~60–110 с / кадр (MPS)";
            return new(
                ModelFitLevel.Ideal,
                "■ COMFYUI MPS",
                $"Работает на {chip} ({unified:F0} ГБ Unified, {bw} ГБ/с) через PyTorch Metal/MPS.",
                speed,
                ModelPlacementGroup.FullVram);
        }

        string vidSpeed = bw >= 400 ? "~3–6 мин / 4с (MPS)" : "~6–12 мин / 4с (MPS)";
        return new(
            model.PrefersFp8 ? ModelFitLevel.Tight : ModelFitLevel.GoodWithOffload,
            model.PrefersFp8 ? "▲ MPS (ЛУЧШЕ Q4)" : "◧ COMFYUI MPS",
            model.PrefersFp8
                ? $"На macOS MPS нет аппаратного NVIDIA FP8 — на {chip} видео Wan 2.2 лучше запускать в кванте GGUF Q4/Q6."
                : $"Запустится на {chip} ({unified:F0} ГБ Unified) через PyTorch MPS.",
            vidSpeed,
            ModelPlacementGroup.VramPlusRam);
    }
}
