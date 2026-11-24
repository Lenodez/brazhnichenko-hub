using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class QueuedGenerationJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Kind { get; set; } = "video"; // "photo" | "video"
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Engine { get; set; } = "wan14b_fp8";
    public int Width { get; set; } = 832;
    public int Height { get; set; } = 480;
    public int DurationSec { get; set; } = 4;
    public long Seed { get; set; } = -1;
    public int BatchCount { get; set; } = 1;
    public string? RefImagePath { get; set; }
    public string? StartImagePath { get; set; }
    public string? EndImagePath { get; set; }
    public string? LoraHigh { get; set; }
    public double LoraHighStrength { get; set; } = 0.75;
    public string? LoraLow { get; set; }
    public double LoraLowStrength { get; set; } = 0.75;
    public string Status { get; set; } = "pending"; // pending | running | done | failed | cancelled
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Thread-safe persistent generation job queue stored in HubConfig.ConfigDir.
/// Pure C# (no WPF imports) so LocalAIHub.Mcp compiles cleanly.
/// </summary>
public static class GenerationQueueService
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

    public static string QueueFilePath => Path.Combine(HubConfig.ConfigDir, "generation-queue.json");

    public static List<QueuedGenerationJob> LoadAll()
    {
        lock (Sync)
        {
            return ReadAllUnlocked();
        }
    }

    public static void SaveAll(IEnumerable<QueuedGenerationJob> jobs)
    {
        if (jobs is null)
            return;

        lock (Sync)
        {
            var list = jobs.Where(j => j is not null).ToList();
            foreach (var job in list)
                NormalizeJob(job);
            WriteAllUnlocked(list);
        }
    }

    public static void Enqueue(QueuedGenerationJob job)
    {
        if (job is null)
            return;

        lock (Sync)
        {
            NormalizeJob(job);
            var all = ReadAllUnlocked();
            all.Add(job);
            WriteAllUnlocked(all);
        }
    }

    public static void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        string cleanId = id.Trim();
        lock (Sync)
        {
            var all = ReadAllUnlocked();
            int removed = all.RemoveAll(j =>
                string.Equals(j.Id?.Trim(), cleanId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                WriteAllUnlocked(all);
        }
    }

    public static void ClearFinished()
    {
        lock (Sync)
        {
            var all = ReadAllUnlocked();
            int removed = all.RemoveAll(j => IsFinishedStatus(j.Status));
            if (removed > 0)
                WriteAllUnlocked(all);
        }
    }

    public static void ClearAll()
    {
        lock (Sync)
        {
            WriteAllUnlocked([]);
        }
    }

    private static bool IsFinishedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        string s = status.Trim();
        return s.Equals("done", StringComparison.OrdinalIgnoreCase)
            || s.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || s.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static void NormalizeJob(QueuedGenerationJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Guid.NewGuid().ToString("N")[..8];
        job.Kind = string.Equals(job.Kind?.Trim(), "photo", StringComparison.OrdinalIgnoreCase)
            ? "photo"
            : "video";
        if (string.IsNullOrWhiteSpace(job.Status))
            job.Status = "pending";
        if (job.BatchCount < 1)
            job.BatchCount = 1;
    }

    private static List<QueuedGenerationJob> ReadAllUnlocked()
    {
        try
        {
            if (!File.Exists(QueueFilePath))
                return [];
            string json = File.ReadAllText(QueueFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            return JsonSerializer.Deserialize<List<QueuedGenerationJob>>(json, ReadJsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteAllUnlocked(List<QueuedGenerationJob> jobs)
    {
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            File.WriteAllText(QueueFilePath, JsonSerializer.Serialize(jobs, WriteJsonOpts), Encoding.UTF8);
        }
        catch
        {
            // Ignore IO errors so queue UI stays resilient
        }
    }
}
