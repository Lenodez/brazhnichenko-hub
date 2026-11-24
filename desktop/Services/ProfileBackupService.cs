using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class ProfileBackupManifest
{
    public string App { get; set; } = "Brazhnichenko Hub";
    public string AppVersion { get; set; } = "4.1.0";
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
    public List<string> IncludedFiles { get; set; } = [];
}

public sealed record ProfileRestoreResult(int RestoredFileCount, List<string> RestoredFiles, DateTime? ExportedUtc);

public static class ProfileBackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static readonly string[] TrackedFileNames =
    [
        "config.json",
        "studio_presets.json",
        "prompt_history.json",
        "gallery_favorites.json",
        "chat_sessions.json",
        "generation_queue.json"
    ];

    public static ProfileBackupManifest ExportProfileZip(string destinationZipPath, string appVersion = "4.1.0")
    {
        if (string.IsNullOrWhiteSpace(destinationZipPath))
            throw new ArgumentException("Путь для сохранения архива не указан.", nameof(destinationZipPath));

        string? parent = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        string tmpZip = destinationZipPath + ".tmp";
        if (File.Exists(tmpZip)) File.Delete(tmpZip);

        var manifest = new ProfileBackupManifest
        {
            AppVersion = appVersion,
            ExportedUtc = DateTime.UtcNow,
            IncludedFiles = []
        };

        using (var archive = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
        {
            foreach (string name in TrackedFileNames)
            {
                string srcPath = Path.Combine(HubConfig.ConfigDir, name);
                if (!File.Exists(srcPath)) continue;

                byte[] bytes = File.ReadAllBytes(srcPath);
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
                manifest.IncludedFiles.Add(name);
            }

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var ms = manifestEntry.Open();
            byte[] mBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOpts));
            ms.Write(mBytes, 0, mBytes.Length);
        }

        File.Move(tmpZip, destinationZipPath, overwrite: true);
        return manifest;
    }

    public static ProfileRestoreResult ImportProfileZip(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
            throw new FileNotFoundException("Файл резервной копии не найден.", sourceZipPath);

        Directory.CreateDirectory(HubConfig.ConfigDir);
        var allowed = new HashSet<string>(TrackedFileNames, StringComparer.OrdinalIgnoreCase);

        ProfileBackupManifest? manifest = null;
        var entriesToRestore = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using (var archive = ZipFile.OpenRead(sourceZipPath))
        {
            var mEntry = archive.GetEntry("manifest.json");
            if (mEntry is not null)
            {
                using var sr = new StreamReader(mEntry.Open(), Encoding.UTF8);
                manifest = JsonSerializer.Deserialize<ProfileBackupManifest>(sr.ReadToEnd(), JsonOpts);
            }

            foreach (var entry in archive.Entries)
            {
                string fileName = Path.GetFileName(entry.FullName);
                if (!allowed.Contains(fileName)) continue;

                using var ms = new MemoryStream();
                using (var es = entry.Open()) es.CopyTo(ms);
                entriesToRestore[fileName] = ms.ToArray();
            }
        }

        if (entriesToRestore.Count == 0)
            throw new InvalidOperationException("В выбранном архиве нет файлов профиля Brazhnichenko Hub.");

        var restored = new List<string>();
        foreach (var (fileName, bytes) in entriesToRestore)
        {
            string dest = Path.Combine(HubConfig.ConfigDir, fileName);
            string tmp = dest + ".restore.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dest, overwrite: true);
            restored.Add(fileName);
        }

        HubConfig.Reload();
        return new ProfileRestoreResult(restored.Count, restored, manifest?.ExportedUtc);
    }
}
