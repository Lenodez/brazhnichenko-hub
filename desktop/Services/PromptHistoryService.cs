using System.IO;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class PromptHistoryItem
{
    public string Text { get; set; } = "";
    public string Kind { get; set; } = "photo";
    public bool IsFavorite { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public static class PromptHistoryService
{
    private const int MaxItemsPerKind = 40;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string HistoryFilePath => Path.Combine(HubConfig.ConfigDir, "prompt-history.json");

    public static List<PromptHistoryItem> Load(string kind)
    {
        string normalizedKind = NormalizeKind(kind);
        lock (Sync)
        {
            return ReadAllUnlocked()
                .Where(x => string.Equals(NormalizeKind(x.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.IsFavorite)
                .ThenByDescending(x => x.LastUsedUtc)
                .ToList();
        }
    }

    public static bool IsFavorite(string kind, string prompt)
    {
        string clean = (prompt ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return false;
        string normalizedKind = NormalizeKind(kind);
        lock (Sync)
        {
            return ReadAllUnlocked().Any(x =>
                string.Equals(NormalizeKind(x.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Text.Trim(), clean, StringComparison.OrdinalIgnoreCase) &&
                x.IsFavorite);
        }
    }

    public static void Record(string kind, string prompt)
    {
        string clean = (prompt ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return;

        string normalizedKind = NormalizeKind(kind);
        lock (Sync)
        {
            var all = ReadAllUnlocked();
            var existing = all.FirstOrDefault(x =>
                string.Equals(NormalizeKind(x.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Text.Trim(), clean, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.Text = clean;
                existing.Kind = normalizedKind;
                existing.LastUsedUtc = DateTime.UtcNow;
            }
            else
            {
                all.Add(new PromptHistoryItem
                {
                    Text = clean,
                    Kind = normalizedKind,
                    IsFavorite = false,
                    LastUsedUtc = DateTime.UtcNow
                });
            }

            WriteAllUnlocked(PrunePerKind(all));
        }
    }

    public static bool ToggleFavorite(string kind, string prompt)
    {
        string clean = (prompt ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return false;

        string normalizedKind = NormalizeKind(kind);
        lock (Sync)
        {
            var all = ReadAllUnlocked();
            var existing = all.FirstOrDefault(x =>
                string.Equals(NormalizeKind(x.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Text.Trim(), clean, StringComparison.OrdinalIgnoreCase));

            bool newFav;
            if (existing is not null)
            {
                existing.Text = clean;
                existing.Kind = normalizedKind;
                existing.IsFavorite = !existing.IsFavorite;
                existing.LastUsedUtc = DateTime.UtcNow;
                newFav = existing.IsFavorite;
            }
            else
            {
                all.Add(new PromptHistoryItem
                {
                    Text = clean,
                    Kind = normalizedKind,
                    IsFavorite = true,
                    LastUsedUtc = DateTime.UtcNow
                });
                newFav = true;
            }

            WriteAllUnlocked(PrunePerKind(all));
            return newFav;
        }
    }

    public static void Remove(string kind, string prompt)
    {
        string clean = (prompt ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return;

        string normalizedKind = NormalizeKind(kind);
        lock (Sync)
        {
            var all = ReadAllUnlocked();
            int removed = all.RemoveAll(x =>
                string.Equals(NormalizeKind(x.Kind), normalizedKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Text.Trim(), clean, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
            {
                WriteAllUnlocked(PrunePerKind(all));
            }
        }
    }

    private static string NormalizeKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? "photo" : kind.Trim().ToLowerInvariant();

    private static List<PromptHistoryItem> PrunePerKind(List<PromptHistoryItem> items)
    {
        var result = new List<PromptHistoryItem>();
        var grouped = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .GroupBy(x => NormalizeKind(x.Kind), StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var favs = group
                .Where(x => x.IsFavorite)
                .OrderByDescending(x => x.LastUsedUtc)
                .ToList();

            int nonFavAllowance = Math.Max(0, MaxItemsPerKind - favs.Count);
            var nonFavs = group
                .Where(x => !x.IsFavorite)
                .OrderByDescending(x => x.LastUsedUtc)
                .Take(nonFavAllowance);

            result.AddRange(favs);
            result.AddRange(nonFavs);
        }

        return result;
    }

    private static List<PromptHistoryItem> ReadAllUnlocked()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return [];
            string json = File.ReadAllText(HistoryFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<PromptHistoryItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteAllUnlocked(List<PromptHistoryItem> items)
    {
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(items, JsonOpts), Encoding.UTF8);
        }
        catch
        {
            // Ignore IO errors so prompt UI stays resilient
        }
    }
}
