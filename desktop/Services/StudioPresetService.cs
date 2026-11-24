using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class StudioPreset
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "photo"; // "photo" | "video"
    public string Engine { get; set; } = "flux3";
    public string Size { get; set; } = "1024x1024";
    public int DurationSec { get; set; } = 4;
    public string? LoraHigh { get; set; }
    public double LoraHighStrength { get; set; } = 0.75;
    public string? LoraLow { get; set; }
    public double LoraLowStrength { get; set; } = 0.75;
    public bool IsBuiltIn { get; set; } = false;
}

/// <summary>
/// Thread-safe store for built-in and custom Studio presets.
/// Pure C# (no WPF imports) so LocalAIHub.Mcp compiles cleanly.
/// </summary>
public static class StudioPresetService
{
    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions WriteJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string PresetsFilePath => Path.Combine(HubConfig.ConfigDir, "studio-presets.json");

    public static List<StudioPreset> Load(string kind)
    {
        string normalizedKind = NormalizeKind(kind);
        var result = GetBuiltInDefaults(normalizedKind);

        lock (Sync)
        {
            var custom = ReadCustomUnlocked()
                .Where(p => string.Equals(NormalizeKind(p.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => ClonePreset(p, isBuiltIn: false));

            result.AddRange(custom);
        }

        return result;
    }

    public static void SaveCustom(StudioPreset preset)
    {
        if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
            return;

        string cleanName = preset.Name.Trim();
        string normalizedKind = NormalizeKind(preset.Kind);

        lock (Sync)
        {
            var all = ReadCustomUnlocked();
            int existingIdx = all.FindIndex(p =>
                string.Equals(NormalizeKind(p.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((p.Name ?? "").Trim(), cleanName, StringComparison.OrdinalIgnoreCase));

            var copy = ClonePreset(preset, isBuiltIn: false);
            copy.Name = cleanName;
            copy.Kind = normalizedKind;

            if (existingIdx >= 0)
                all[existingIdx] = copy;
            else
                all.Add(copy);

            WriteCustomUnlocked(all);
        }
    }

    public static void DeleteCustom(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        string cleanName = name.Trim();
        string normalizedKind = NormalizeKind(kind);

        lock (Sync)
        {
            var all = ReadCustomUnlocked();
            int removed = all.RemoveAll(p =>
                string.Equals(NormalizeKind(p.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((p.Name ?? "").Trim(), cleanName, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
                WriteCustomUnlocked(all);
        }
    }

    private static List<StudioPreset> GetBuiltInDefaults(string normalizedKind)
    {
        if (normalizedKind == "video")
        {
            return
            [
                new StudioPreset
                {
                    Name = "Кино 16:9 (Wan 2.2 FP8 • 4 сек)",
                    Kind = "video",
                    Engine = "wan14b_fp8",
                    Size = "832x480",
                    DurationSec = 4,
                    IsBuiltIn = true
                },
                new StudioPreset
                {
                    Name = "Reels 9:16 (Wan 2.2 FP8 • 5 сек)",
                    Kind = "video",
                    Engine = "wan14b_fp8",
                    Size = "480x832",
                    DurationSec = 5,
                    IsBuiltIn = true
                },
                new StudioPreset
                {
                    Name = "Видео со звуком (LTX-2.5 • 4 сек)",
                    Kind = "video",
                    Engine = "ltx25",
                    Size = "832x480",
                    DurationSec = 4,
                    IsBuiltIn = true
                }
            ];
        }

        return
        [
            new StudioPreset
            {
                Name = "Фотореализм (FLUX 3 • 1024×1024)",
                Kind = "photo",
                Engine = "flux3",
                Size = "1024x1024",
                DurationSec = 0,
                IsBuiltIn = true
            },
            new StudioPreset
            {
                Name = "Быстрый черновик (Z-Image • 1024×1024)",
                Kind = "photo",
                Engine = "zimage",
                Size = "1024x1024",
                DurationSec = 0,
                IsBuiltIn = true
            },
            new StudioPreset
            {
                Name = "Постер / Текст (Qwen 2.1 • 1280×720)",
                Kind = "photo",
                Engine = "qwen21",
                Size = "1280x720",
                DurationSec = 0,
                IsBuiltIn = true
            }
        ];
    }

    private static StudioPreset ClonePreset(StudioPreset p, bool isBuiltIn) => new()
    {
        Name = (p.Name ?? "").Trim(),
        Kind = NormalizeKind(p.Kind),
        Engine = string.IsNullOrWhiteSpace(p.Engine)
            ? (NormalizeKind(p.Kind) == "video" ? "wan14b_fp8" : "flux3")
            : p.Engine.Trim(),
        Size = string.IsNullOrWhiteSpace(p.Size)
            ? (NormalizeKind(p.Kind) == "video" ? "832x480" : "1024x1024")
            : p.Size.Trim(),
        DurationSec = p.DurationSec,
        LoraHigh = string.IsNullOrWhiteSpace(p.LoraHigh) ? null : p.LoraHigh.Trim(),
        LoraHighStrength = p.LoraHighStrength,
        LoraLow = string.IsNullOrWhiteSpace(p.LoraLow) ? null : p.LoraLow.Trim(),
        LoraLowStrength = p.LoraLowStrength,
        IsBuiltIn = isBuiltIn
    };

    private static string NormalizeKind(string? kind) =>
        string.Equals(kind?.Trim(), "video", StringComparison.OrdinalIgnoreCase) ? "video" : "photo";

    private static List<StudioPreset> ReadCustomUnlocked()
    {
        try
        {
            if (!File.Exists(PresetsFilePath))
                return [];
            string json = File.ReadAllText(PresetsFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            return JsonSerializer.Deserialize<List<StudioPreset>>(json, ReadJsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteCustomUnlocked(List<StudioPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            File.WriteAllText(PresetsFilePath, JsonSerializer.Serialize(presets, WriteJsonOpts), Encoding.UTF8);
        }
        catch
        {
            // Ignore IO errors so Studio UI stays resilient
        }
    }
}
