using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public sealed record AssistantNoteEntry(
    string FilePath,
    string FileName,
    string Title,
    DateTime CreatedLocal,
    DateTime ModifiedLocal,
    string[] Tags,
    string Preview,
    string BodyMarkdown,
    string RawMarkdown
);

/// <summary>
/// Strictly sandboxed Markdown (.md) notes store for the Personal Assistant.
/// Writes Obsidian-compatible YAML frontmatter files ONLY inside the configured Notes directory.
/// </summary>
public static class AssistantNotesService
{
    private static readonly Dictionary<char, string> TranslitMap = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    public static string ResolveNotesDirectory(string? notesDirOverride = null)
    {
        string dir = string.IsNullOrWhiteSpace(notesDirOverride)
            ? HubConfig.AssistantNotesDir
            : notesDirOverride.Trim();
        return Path.GetFullPath(dir);
    }

    public static bool IsPathInsideNotesDir(string filePath, string? notesDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            string root = ResolveNotesDirectory(notesDirOverride)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(filePath);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(root, cmp) && full.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static AssistantNoteEntry SaveNote(
        string title,
        string bodyMarkdown,
        IEnumerable<string>? tags = null,
        string? notesDirOverride = null,
        DateTime? nowOverride = null)
    {
        string dir = ResolveNotesDirectory(notesDirOverride);
        Directory.CreateDirectory(dir);

        DateTime now = nowOverride ?? DateTime.Now;
        string cleanTitle = SanitizeTitle(title, bodyMarkdown);
        string[] cleanTags = NormalizeTags(tags);
        string slug = BuildSafeSlug(cleanTitle);

        string baseName = $"{now:yyyy-MM-dd_HH-mm}_{slug}";
        string filePath = Path.Combine(dir, baseName + ".md");
        int suffix = 2;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(dir, $"{baseName}_{suffix}.md");
            suffix++;
        }

        string rawMarkdown = BuildMarkdownDocument(cleanTitle, bodyMarkdown, cleanTags, now);
        File.WriteAllText(filePath, rawMarkdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return ParseNoteFile(filePath, rawMarkdown, now, now);
    }

    public static List<AssistantNoteEntry> ListNotes(
        string? searchQuery = null,
        string? tagFilter = null,
        string? notesDirOverride = null)
    {
        string dir = ResolveNotesDirectory(notesDirOverride);
        if (!Directory.Exists(dir)) return [];

        var list = new List<AssistantNoteEntry>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string raw = File.ReadAllText(file, Encoding.UTF8);
                var fi = new FileInfo(file);
                var entry = ParseNoteFile(file, raw, fi.CreationTime, fi.LastWriteTime);

                if (!string.IsNullOrWhiteSpace(tagFilter))
                {
                    string tf = tagFilter.Trim().TrimStart('#');
                    if (!entry.Tags.Any(t => t.Equals(tf, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(searchQuery))
                {
                    string q = searchQuery.Trim();
                    bool hit = entry.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || entry.BodyMarkdown.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || entry.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
                        || entry.FileName.Contains(q, StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;
                }

                list.Add(entry);
            }
            catch { }
        }

        return list
            .OrderByDescending(n => n.ModifiedLocal)
            .ThenByDescending(n => n.CreatedLocal)
            .ToList();
    }

    public static AssistantNoteEntry? ReadNote(string filePath, string? notesDirOverride = null)
    {
        if (!IsPathInsideNotesDir(filePath, notesDirOverride) || !File.Exists(filePath))
            return null;
        string raw = File.ReadAllText(filePath, Encoding.UTF8);
        var fi = new FileInfo(filePath);
        return ParseNoteFile(filePath, raw, fi.CreationTime, fi.LastWriteTime);
    }

    public static AssistantNoteEntry UpdateNote(string filePath, string fullRawMarkdown, string? notesDirOverride = null)
    {
        if (!IsPathInsideNotesDir(filePath, notesDirOverride))
            throw new UnauthorizedAccessException("Редактирование разрешено только для .md заметок внутри папки Notes.");

        File.WriteAllText(filePath, fullRawMarkdown ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var fi = new FileInfo(filePath);
        return ParseNoteFile(filePath, fullRawMarkdown ?? "", fi.CreationTime, fi.LastWriteTime);
    }

    public static bool DeleteNote(string filePath, string? notesDirOverride = null)
    {
        if (!IsPathInsideNotesDir(filePath, notesDirOverride) || !File.Exists(filePath))
            return false;
        File.Delete(filePath);
        return true;
    }

    public static string BuildMarkdownDocument(string title, string bodyMarkdown, IReadOnlyList<string> tags, DateTime createdLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYamlString(title)}\"");
        sb.AppendLine($"created: {createdLocal:yyyy-MM-dd HH:mm}");
        if (tags.Count > 0)
        {
            sb.AppendLine("tags:");
            foreach (string tag in tags)
                sb.AppendLine($"  - {tag}");
        }
        else
        {
            sb.AppendLine("tags: [assistant]");
        }
        sb.AppendLine("source: brazhnichenko-hub-assistant");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine((bodyMarkdown ?? "").Trim());
        return sb.ToString();
    }

    public static AssistantNoteEntry ParseNoteFile(string filePath, string rawMarkdown, DateTime fallbackCreated, DateTime modifiedLocal)
    {
        string title = "";
        DateTime created = fallbackCreated;
        var tags = new List<string>();
        string body = rawMarkdown ?? "";

        if (body.StartsWith("---\n", StringComparison.Ordinal) || body.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            var m = Regex.Match(body, @"^---\r?\n([\s\S]*?)\r?\n---\r?\n?", RegexOptions.None);
            if (m.Success)
            {
                string fm = m.Groups[1].Value;
                body = body[m.Length..].TrimStart('\r', '\n');

                bool inTagsList = false;
                foreach (string rawLine in fm.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("- ", StringComparison.Ordinal) && inTagsList)
                    {
                        string t = line[2..].Trim().Trim('"', '\'', '#');
                        if (!string.IsNullOrWhiteSpace(t)) tags.Add(t);
                        continue;
                    }
                    inTagsList = false;

                    if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    {
                        title = line[6..].Trim().Trim('"', '\'');
                    }
                    else if (line.StartsWith("created:", StringComparison.OrdinalIgnoreCase))
                    {
                        string dtStr = line[8..].Trim().Trim('"', '\'');
                        if (DateTime.TryParseExact(dtStr, ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd"],
                                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedDt)
                            || DateTime.TryParse(dtStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsedDt))
                        {
                            created = parsedDt;
                        }
                    }
                    else if (line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = line[5..].Trim();
                        if (string.IsNullOrEmpty(val))
                        {
                            inTagsList = true;
                        }
                        else if (val.StartsWith('[') && val.EndsWith(']'))
                        {
                            foreach (string part in val[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                string t = part.Trim().Trim('"', '\'', '#');
                                if (!string.IsNullOrWhiteSpace(t)) tags.Add(t);
                            }
                        }
                        else
                        {
                            string t = val.Trim('"', '\'', '#');
                            if (!string.IsNullOrWhiteSpace(t)) tags.Add(t);
                        }
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            var h1 = Regex.Match(body, @"^\s*#\s+(.+)$", RegexOptions.Multiline);
            title = h1.Success
                ? h1.Groups[1].Value.Trim()
                : Path.GetFileNameWithoutExtension(filePath);
        }

        // Build clean single-line preview stripping heading/markdown punctuation
        string plain = Regex.Replace(body, @"^\s*#+\s+.*$", "", RegexOptions.Multiline);
        plain = Regex.Replace(plain, @"[*_`>#\[\]]+", " ");
        plain = Regex.Replace(plain, @"\s+", " ").Trim();
        if (plain.Length > 140) plain = plain[..137].TrimEnd() + "…";

        return new AssistantNoteEntry(
            FilePath: filePath,
            FileName: Path.GetFileName(filePath),
            Title: title,
            CreatedLocal: created,
            ModifiedLocal: modifiedLocal,
            Tags: tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Preview: plain,
            BodyMarkdown: body,
            RawMarkdown: rawMarkdown ?? "");
    }

    public static string BuildSafeSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "note";
        var sb = new StringBuilder();
        bool lastDash = false;
        foreach (char ch in title.Trim().ToLowerInvariant())
        {
            if (TranslitMap.TryGetValue(ch, out string? mapped))
            {
                if (mapped.Length > 0)
                {
                    sb.Append(mapped);
                    lastDash = false;
                }
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= 42) break;
        }

        string slug = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "note" : slug;
    }

    private static string SanitizeTitle(string title, string body)
    {
        string t = (title ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(t))
        {
            string firstLine = (body ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            firstLine = Regex.Replace(firstLine, @"^[#*\-\s]+", "").Trim();
            t = firstLine.Length > 64 ? firstLine[..61].TrimEnd() + "…" : firstLine;
        }
        return string.IsNullOrWhiteSpace(t) ? "Заметка ассистента" : t;
    }

    private static string[] NormalizeTags(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        if (tags is not null)
        {
            foreach (string raw in tags)
            {
                string clean = Regex.Replace((raw ?? "").Trim().TrimStart('#').ToLowerInvariant(), @"[^\p{L}\p{Nd}_-]+", "-").Trim('-');
                if (clean.Length is >= 2 and <= 28 && !result.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    result.Add(clean);
            }
        }
        if (result.Count == 0) result.Add("assistant");
        return result.Take(6).ToArray();
    }

    private static string EscapeYamlString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
