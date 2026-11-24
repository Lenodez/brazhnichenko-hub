using System.IO;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

/// <summary>
/// Thread-safe persistent store of favorited Gallery file paths (protected from bulk cleanup).
/// Pure C# (no WPF namespaces) so LocalAIHub.Mcp compiles cleanly.
/// </summary>
public static class GalleryFavoritesStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static HashSet<string>? _favorites;

    public static string StoreFilePath => Path.Combine(HubConfig.ConfigDir, "gallery-favorites.json");

    private static string Normalize(string path)
    {
        string trimmed = path.Trim();
        try { return Path.GetFullPath(trimmed); }
        catch { return trimmed; }
    }

    private static HashSet<string> EnsureLoadedLocked()
    {
        if (_favorites is not null) return _favorites;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(StoreFilePath))
            {
                string json = File.ReadAllText(StoreFilePath, Encoding.UTF8);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list is not null)
                {
                    foreach (string item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item))
                            set.Add(Normalize(item));
                    }
                }
            }
        }
        catch { }

        _favorites = set;
        return _favorites;
    }

    private static void SaveLocked(HashSet<string> set)
    {
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            var ordered = set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(StoreFilePath, JsonSerializer.Serialize(ordered, JsonOpts), Encoding.UTF8);
        }
        catch { }
    }

    public static bool IsFavorite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm = Normalize(path);
        lock (Sync)
        {
            return EnsureLoadedLocked().Contains(norm);
        }
    }

    public static bool Toggle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm = Normalize(path);
        lock (Sync)
        {
            var set = EnsureLoadedLocked();
            bool nowFav;
            if (set.Contains(norm))
            {
                set.Remove(norm);
                nowFav = false;
            }
            else
            {
                set.Add(norm);
                nowFav = true;
            }
            SaveLocked(set);
            return nowFav;
        }
    }

    public static bool Remove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm = Normalize(path);
        lock (Sync)
        {
            var set = EnsureLoadedLocked();
            if (!set.Remove(norm)) return false;
            SaveLocked(set);
            return true;
        }
    }

    public static int Count()
    {
        lock (Sync)
        {
            return EnsureLoadedLocked().Count;
        }
    }

    public static int PruneMissing()
    {
        lock (Sync)
        {
            var set = EnsureLoadedLocked();
            int removed = set.RemoveWhere(p => !File.Exists(p));
            if (removed > 0) SaveLocked(set);
            return removed;
        }
    }
}
