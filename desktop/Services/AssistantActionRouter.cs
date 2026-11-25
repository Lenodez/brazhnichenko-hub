using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public enum AssistantIntentKind
{
    ChatOrScreen,
    SaveNote,
    SetReminder,
    WebSearch
}

public sealed record AssistantIntentMatch(
    AssistantIntentKind Kind,
    string ExtractedPayload,
    DateTime? ReminderFireAt = null,
    string? SuggestedTitle = null,
    string[]? SuggestedTags = null
);

public sealed record ParsedNoteStructure(
    string Title,
    string BodyMarkdown,
    string[] Tags
);

public sealed record ParsedLlmAction(
    string Action, // "save_note" | "set_reminder"
    string Title,
    string Content,
    string[] Tags,
    int? ReminderMinutes
);

/// <summary>
/// Strictly whitelisted Personal Assistant router.
/// Allowed capabilities ONLY:
///   1. save_note     -> creates .md note inside configured Documents\Notes folder
///   2. set_reminder  -> creates local timer/notification in Brazhnichenko Hub
///   3. web_search    -> hybrid web search (DuckDuckGo / News / Wiki / optional API)
///   4. chat/screen   -> read-only Vision screen explanation, translation, or Q&A
/// ZERO shell execution, ZERO arbitrary filesystem access, ZERO system modification.
/// </summary>
public static class AssistantActionRouter
{
    private static readonly Regex PoliteLeadInRx = new(
        @"^(?:(?:привет|здравствуй|слушай|эй|ассистент|пожалуйста|можешь(?:\s+ли)?|сможешь(?:\s+ли)?|не\s+мог\s+бы(?:\s+ты)?|будь\s+добр(?:а)?|давай|хочу|мне\s+нужно|нужно|надо|помоги(?:\s+мне)?|попробуй|hey|hi|hello|please|can\s+you|could\s+you|would\s+you|i\s+want\s+to|i\s+need\s+to)[\s,!.:—–\-]+)+",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex NotePrefixRx = new(
        @"^(?:(?:сделай|сделать|создай|создать|запиши|записать|сохрани|сохранить|добавь|добавить|напиши|написать|зафиксируй|зафиксировать|закинь|закинуть|оформи|оформить)\s+(?:мне\s+|сюда\s+|в\s+память\s+)?(?:новую\s+|быструю\s+|короткую\s+)?(?:заметк[уи]|иде[юи]|конспект|мысль)|(?:добавь|добавить|запиши|записать|сохрани|сохранить)\s+(?:это\s+)?в\s+заметки|в\s+заметки|заметка|save\s+(?:a\s+)?note|make\s+(?:a\s+)?note|create\s+(?:a\s+)?note|write\s+(?:a\s+)?note|take\s+(?:a\s+)?note|note)\s*[,.:;—–\-]*\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ReminderHintRx = new(
        @"^(?:напомни(?:ть)?(?:\s+мне)?|постав(?:ь|ить)\s+(?:мне\s+)?(?:таймер|напоминание)|завед(?:и|и|сти)\s+таймер|таймер\s+на|remind(?:\s+me)?|set\s+(?:a\s+)?(?:timer|reminder))\b",
        RegexOptions.IgnoreCase);

    private static readonly Regex WebSearchPrefixRx = new(
        @"^(?:(?:найди|найти|поищи|поискать|проверь|проверить|узнай|узнать|посмотри|посмотреть)\s+(?:в\s+(?:интернете|сети|гугле)|онлайн)|погугли(?:ть)?|search\s+(?:the\s+)?web(?:\s+for)?|google)\s*[,.:;—–\-]*\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex LlmActionTagRx = new(
        @"<hub_action>\s*(\{[\s\S]*?\})\s*</hub_action>",
        RegexOptions.IgnoreCase);

    public static string StripConversationalLeadIn(string text)
    {
        string s = (text ?? "").Trim();
        // Repeat stripping up to 3 times for stacked polite lead-ins like "Привет, можешь, пожалуйста, ..."
        for (int i = 0; i < 3; i++)
        {
            string next = PoliteLeadInRx.Replace(s, "").Trim();
            if (next.Length == s.Length || string.IsNullOrWhiteSpace(next)) break;
            s = next;
        }
        return s;
    }

    public static AssistantIntentMatch RouteUserInput(string userText, DateTime? nowOverride = null)
    {
        string text = (userText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new AssistantIntentMatch(AssistantIntentKind.ChatOrScreen, "");

        DateTime now = nowOverride ?? DateTime.Now;
        string normalized = StripConversationalLeadIn(text);

        // 1. Explicit Note command (handles polite & spoken voice forms like "Можешь, пожалуйста, написать заметку, ...")
        var noteMatch = NotePrefixRx.Match(normalized);
        if (noteMatch.Success)
        {
            string payload = noteMatch.Groups[1].Value.Trim();
            payload = Regex.Replace(payload, @"^(?:о\s+том\s*,?\s*что|про\s+то\s*,?\s*что|про|что|текст(?:\s+заметки)?|that|about)\s*[,.:—–\-]*\s*", "", RegexOptions.IgnoreCase).Trim();
            var (title, body, tags) = BuildFallbackNoteStructure(payload);
            return new AssistantIntentMatch(AssistantIntentKind.SaveNote, payload, SuggestedTitle: title, SuggestedTags: tags);
        }

        // 2. Explicit Reminder / Timer command
        if (ReminderHintRx.IsMatch(normalized))
        {
            if (AssistantReminderService.TryParseReminderTime(normalized, now, out var fireAt, out var cleanText))
            {
                return new AssistantIntentMatch(AssistantIntentKind.SetReminder, cleanText, ReminderFireAt: fireAt);
            }
        }

        // 3. Explicit Web Search command
        var webMatch = WebSearchPrefixRx.Match(normalized);
        if (webMatch.Success)
        {
            return new AssistantIntentMatch(AssistantIntentKind.WebSearch, webMatch.Groups[1].Value.Trim());
        }

        return new AssistantIntentMatch(AssistantIntentKind.ChatOrScreen, text);
    }

    public static (string CleanReply, ParsedLlmAction? Action) ExtractLlmAction(string rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply)) return ("", null);
        var m = LlmActionTagRx.Match(rawReply);
        if (!m.Success) return (rawReply.Trim(), null);

        string clean = LlmActionTagRx.Replace(rawReply, "").Trim();
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var root = doc.RootElement;
            string action = root.TryGetProperty("action", out var aEl) && aEl.ValueKind == JsonValueKind.String
                ? (aEl.GetString() ?? "").Trim().ToLowerInvariant()
                : "";

            // Strictly enforce the 2 whitelisted local write actions
            if (action == "save_note")
            {
                string title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()?.Trim() ?? "" : "";
                string content = root.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString()?.Trim() ?? "" : "";
                var tags = new List<string>();
                if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in tagsEl.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                            tags.Add(it.GetString()!.Trim());
                }
                if (!string.IsNullOrWhiteSpace(content))
                    return (clean, new ParsedLlmAction("save_note", title, content, tags.ToArray(), null));
            }
            else if (action == "set_reminder")
            {
                string text = root.TryGetProperty("text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String ? txtEl.GetString()?.Trim() ?? "" : "";
                int mins = root.TryGetProperty("minutes", out var minEl) && minEl.ValueKind == JsonValueKind.Number ? minEl.GetInt32() : 0;
                if (mins is >= 1 and <= 43200 && !string.IsNullOrWhiteSpace(text))
                    return (clean, new ParsedLlmAction("set_reminder", "", text, [], mins));
            }
        }
        catch { }

        return (clean, null);
    }

    public static string BuildNoteStructuringSystemPrompt() =>
        "Ты модуль форматирования заметок Brazhnichenko Hub. Преврати диктовку пользователя в аккуратную Markdown-заметку.\n" +
        "Верни ТОЛЬКО валидный JSON без пояснений и без markdown-ограждений:\n" +
        "{\"title\":\"Короткий заголовок (3-7 слов)\",\"content\":\"Аккуратный Markdown-текст заметки со списками при необходимости\",\"tags\":[\"тег1\",\"тег2\"]}\n" +
        "Сохраняй все факты, цифры и язык пользователя. Ничего не выдумывай.";

    public static ParsedNoteStructure ParseStructuredNoteOrFallback(string? llmJsonReply, string rawUserPayload)
    {
        if (!string.IsNullOrWhiteSpace(llmJsonReply))
        {
            try
            {
                string cleaned = llmJsonReply.Trim();
                // Strip <think>...</think> blocks if model emitted reasoning
                cleaned = Regex.Replace(cleaned, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
                // Strip ```json fences if present
                var fence = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
                if (fence.Success) cleaned = fence.Groups[1].Value.Trim();

                int start = cleaned.IndexOf('{');
                int end = cleaned.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    using var doc = JsonDocument.Parse(cleaned[start..(end + 1)]);
                    var root = doc.RootElement;
                    string title = root.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                        ? tEl.GetString()?.Trim() ?? ""
                        : "";
                    string content = root.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                        ? cEl.GetString()?.Trim() ?? ""
                        : "";
                    var tags = new List<string>();
                    if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in tagsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                string tg = item.GetString()?.Trim() ?? "";
                                if (!string.IsNullOrWhiteSpace(tg)) tags.Add(tg);
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        var fb = BuildFallbackNoteStructure(rawUserPayload);
                        return new ParsedNoteStructure(
                            string.IsNullOrWhiteSpace(title) ? fb.Title : title,
                            content,
                            tags.Count > 0 ? tags.ToArray() : fb.Tags);
                    }
                }
            }
            catch { }
        }

        return BuildFallbackNoteStructure(rawUserPayload);
    }

    public static ParsedNoteStructure BuildFallbackNoteStructure(string payload)
    {
        string raw = (payload ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new ParsedNoteStructure("Быстрая заметка", "", ["assistant"]);

        // Extract inline #hashtags if user included them
        var tags = new List<string>();
        foreach (Match m in Regex.Matches(raw, @"#([\p{L}\p{Nd}_-]{2,24})"))
            tags.Add(m.Groups[1].Value.ToLowerInvariant());

        string cleanBody = raw;
        string title;
        int colonIdx = raw.IndexOf(':');
        if (colonIdx is >= 3 and <= 54)
        {
            title = raw[..colonIdx].Trim();
            cleanBody = raw[(colonIdx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(cleanBody)) cleanBody = raw;
        }
        else
        {
            string firstSentence = Regex.Split(raw, @"(?<=[.!?])\s+|\r?\n").FirstOrDefault()?.Trim() ?? raw;
            if (firstSentence.Length > 56)
            {
                string[] words = firstSentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                title = string.Join(' ', words.Take(6)).TrimEnd(',', ';', ':');
                if (words.Length > 6) title += "…";
            }
            else
            {
                title = firstSentence.TrimEnd('.');
            }
        }

        if (title.Length > 0)
            title = char.ToUpper(title[0]) + title[1..];
        if (cleanBody.Length > 0)
            cleanBody = char.ToUpper(cleanBody[0]) + cleanBody[1..];

        if (tags.Count == 0) tags.Add("assistant");
        return new ParsedNoteStructure(title, cleanBody, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
