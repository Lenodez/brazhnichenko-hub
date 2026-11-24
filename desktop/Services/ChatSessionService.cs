using System.IO;
using System.Text;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class ChatPersona
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public double DefaultTemperature { get; init; } = 0.7;
}

public sealed class ChatMessageRecord
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public string? ImagePath { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "Новый чат";
    public string PersonaId { get; set; } = "default";
    public double Temperature { get; set; } = 0.7;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ChatMessageRecord> Messages { get; set; } = [];
}

public static class ChatSessionService
{
    public const int MaxSessions = 60;
    public const int MaxMessagesPerSession = 80;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SessionsFilePath => Path.Combine(HubConfig.ConfigDir, "chat_sessions.json");
    public static string LegacyHistoryFilePath => Path.Combine(HubConfig.ConfigDir, "local-chat-history.json");

    public static IReadOnlyList<ChatPersona> BuiltInPersonas { get; } =
    [
        new ChatPersona
        {
            Id = "default",
            Name = "Обычный ассистент",
            Description = "Универсальный помощник для повседневных задач и вопросов",
            SystemPrompt = "Ты умный, лаконичный и полезный локальный AI-ассистент. Отвечай на языке пользователя чётко и по делу.",
            DefaultTemperature = 0.7
        },
        new ChatPersona
        {
            Id = "coder",
            Name = "Программист",
            Description = "Код, архитектура, отладка и ревью без лишней воды",
            SystemPrompt = "Ты senior software engineer. Пиши чистый, рабочий, современный код, указывай edge-кейсы и объясняй решения кратко и точно.",
            DefaultTemperature = 0.25
        },
        new ChatPersona
        {
            Id = "director",
            Name = "Сценарист / Промпт-инженер",
            Description = "Идеи сцен, свет, камера и английские промпты для FLUX / Wan / LTX",
            SystemPrompt = "Ты опытный кинорежиссёр и промпт-инженер для FLUX, Qwen-Image, Wan 2.2 и LTX-2.5. Предлагай выразительные визуальные решения и готовые детальные промпты.",
            DefaultTemperature = 0.85
        },
        new ChatPersona
        {
            Id = "translator",
            Name = "Переводчик RU ↔ EN",
            Description = "Точный и естественный перевод без канцелярита",
            SystemPrompt = "Ты профессиональный переводчик. Если текст на русском — переведи его естественно на английский; если на английском или другом языке — на живой русский. Сохраняй стиль и смысл оригинала.",
            DefaultTemperature = 0.3
        },
        new ChatPersona
        {
            Id = "concise",
            Name = "Кратко и по делу",
            Description = "Выжимка, буллеты и прямые ответы без вступлений",
            SystemPrompt = "Отвечай максимально коротко, структурно (списками) и строго по сути вопроса, без вводных фраз и воды.",
            DefaultTemperature = 0.4
        }
    ];

    public static ChatPersona GetPersona(string? personaId)
    {
        if (!string.IsNullOrWhiteSpace(personaId))
        {
            var found = BuiltInPersonas.FirstOrDefault(p =>
                string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return BuiltInPersonas[0];
    }

    public static List<ChatSession> LoadAll()
    {
        try
        {
            if (File.Exists(SessionsFilePath))
            {
                string json = File.ReadAllText(SessionsFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<List<ChatSession>>(json, JsonOpts);
                if (loaded is { Count: > 0 })
                {
                    foreach (var s in loaded)
                    {
                        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString("N")[..10];
                        if (string.IsNullOrWhiteSpace(s.Title)) s.Title = "Чат";
                        s.PersonaId = GetPersona(s.PersonaId).Id;
                        s.Temperature = Math.Clamp(s.Temperature, 0.0, 2.0);
                        s.Messages ??= [];
                    }
                    return loaded.OrderByDescending(s => s.UpdatedAtUtc).Take(MaxSessions).ToList();
                }
            }

            // One-time migration from v3.0 local-chat-history.json
            if (File.Exists(LegacyHistoryFilePath))
            {
                string legacyJson = File.ReadAllText(LegacyHistoryFilePath, Encoding.UTF8);
                var legacy = JsonSerializer.Deserialize<List<ChatMessageRecord>>(legacyJson, JsonOpts);
                if (legacy is { Count: > 0 })
                {
                    string firstUser = legacy.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
                    var migrated = new ChatSession
                    {
                        Title = AutoTitleFromPrompt(firstUser),
                        PersonaId = "default",
                        Temperature = 0.7,
                        Messages = legacy.TakeLast(MaxMessagesPerSession).ToList(),
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    var list = new List<ChatSession> { migrated };
                    SaveAll(list);
                    return list;
                }
            }
        }
        catch { }

        return [CreateNew("default")];
    }

    public static void SaveAll(List<ChatSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(HubConfig.ConfigDir);
            var trimmed = sessions
                .OrderByDescending(s => s.UpdatedAtUtc)
                .Take(MaxSessions)
                .Select(s => new ChatSession
                {
                    Id = string.IsNullOrWhiteSpace(s.Id) ? Guid.NewGuid().ToString("N")[..10] : s.Id,
                    Title = string.IsNullOrWhiteSpace(s.Title) ? "Новый чат" : s.Title.Trim(),
                    PersonaId = GetPersona(s.PersonaId).Id,
                    Temperature = Math.Clamp(s.Temperature, 0.0, 2.0),
                    CreatedAtUtc = s.CreatedAtUtc,
                    UpdatedAtUtc = s.UpdatedAtUtc,
                    Messages = (s.Messages ?? []).TakeLast(MaxMessagesPerSession).ToList()
                })
                .ToList();

            string json = JsonSerializer.Serialize(trimmed, JsonOpts);
            string tmp = SessionsFilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, SessionsFilePath, overwrite: true);
        }
        catch { }
    }

    public static ChatSession CreateNew(string? personaId = null)
    {
        var persona = GetPersona(personaId);
        return new ChatSession
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Title = "Новый чат",
            PersonaId = persona.Id,
            Temperature = persona.DefaultTemperature,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Messages = []
        };
    }

    public static string AutoTitleFromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "Новый чат";
        string cleaned = string.Join(' ', prompt.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length <= 42) return cleaned;
        return cleaned[..41].TrimEnd() + "…";
    }

    public static List<ChatSession> FilterSessions(IEnumerable<ChatSession> sessions, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return sessions.OrderByDescending(s => s.UpdatedAtUtc).ToList();

        string q = query.Trim();
        return sessions
            .Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Messages.Any(m => m.Content.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.UpdatedAtUtc)
            .ToList();
    }

    public static string? BuildImageDataUrl(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(imagePath);
            if (bytes.Length == 0 || bytes.Length > 20 * 1024 * 1024) return null;
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }
}
