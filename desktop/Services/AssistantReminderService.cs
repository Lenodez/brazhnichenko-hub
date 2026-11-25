using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public sealed class AssistantReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Text { get; set; } = "";
    public DateTime CreatedLocal { get; set; } = DateTime.Now;
    public DateTime FireAtLocal { get; set; }
    public bool Completed { get; set; }
}

/// <summary>
/// Lightweight local background reminder/timer scheduler with JSON persistence.
/// Fires ReminderTriggered when a reminder's local time is reached.
/// </summary>
public sealed class AssistantReminderService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _storagePath;
    private readonly List<AssistantReminderItem> _items = [];
    private System.Timers.Timer? _timer;

    public event Action<AssistantReminderItem>? ReminderTriggered;

    public AssistantReminderService(string? storagePathOverride = null)
    {
        _storagePath = string.IsNullOrWhiteSpace(storagePathOverride)
            ? HubConfig.AssistantRemindersPath
            : storagePathOverride;
        LoadFromDisk();
    }

    public void Start()
    {
        if (_timer is not null) return;
        _timer = new System.Timers.Timer(2000) { AutoReset = true };
        _timer.Elapsed += (_, _) => PollDueReminders(DateTime.Now);
        _timer.Start();
        // Catch any reminders that became due right before startup
        PollDueReminders(DateTime.Now);
    }

    public AssistantReminderItem AddReminder(string text, DateTime fireAtLocal)
    {
        var item = new AssistantReminderItem
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = string.IsNullOrWhiteSpace(text) ? "Напоминание" : text.Trim(),
            CreatedLocal = DateTime.Now,
            FireAtLocal = fireAtLocal,
            Completed = false
        };

        lock (_lock)
        {
            _items.Add(item);
            SaveToDiskLocked();
        }
        return item;
    }

    public bool CancelReminder(string id)
    {
        lock (_lock)
        {
            int removed = _items.RemoveAll(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) SaveToDiskLocked();
            return removed > 0;
        }
    }

    public List<AssistantReminderItem> GetActiveReminders()
    {
        lock (_lock)
        {
            return _items
                .Where(r => !r.Completed)
                .OrderBy(r => r.FireAtLocal)
                .ToList();
        }
    }

    public List<AssistantReminderItem> PollDueReminders(DateTime nowLocal)
    {
        List<AssistantReminderItem> due = [];
        lock (_lock)
        {
            foreach (var item in _items)
            {
                if (!item.Completed && item.FireAtLocal <= nowLocal)
                {
                    item.Completed = true;
                    due.Add(item);
                }
            }

            // Prune old completed reminders (> 7 days)
            _items.RemoveAll(r => r.Completed && (nowLocal - r.FireAtLocal).TotalDays > 7);
            if (due.Count > 0) SaveToDiskLocked();
        }

        foreach (var item in due)
        {
            try { ReminderTriggered?.Invoke(item); } catch { }
        }
        return due;
    }

    /// <summary>
    /// Parses natural Russian/English reminder phrases like:
    /// "напомни через 20 минут выключить духовку", "через полчаса позвонить",
    /// "таймер на 1 час 15 мин", "напомни в 18:30 созвон", "завтра в 9:00 оплатить счета".
    /// </summary>
    public static bool TryParseReminderTime(
        string input,
        DateTime nowLocal,
        out DateTime fireAtLocal,
        out string reminderText)
    {
        fireAtLocal = default;
        reminderText = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        string working = AssistantActionRouter.StripConversationalLeadIn(input);
        // Strip leading command verb (imperative or infinitive)
        working = Regex.Replace(
            working,
            @"^(?:напомни(?:ть)?(?:\s+мне)?|постав(?:ь|ить)\s+(?:мне\s+)?(?:таймер|напоминание)|завед(?:и|сти)\s+таймер|таймер|remind(?:\s+me)?|set\s+(?:a\s+)?(?:timer|reminder))\s*",
            "",
            RegexOptions.IgnoreCase).Trim();

        // 1. Half-hour / "через полчаса"
        var halfHour = Regex.Match(working, @"\b(?:через|на|in|for)\s+полчаса\b", RegexOptions.IgnoreCase);
        if (halfHour.Success)
        {
            fireAtLocal = nowLocal.AddMinutes(30);
            reminderText = CleanReminderBody(working.Remove(halfHour.Index, halfHour.Length));
            return true;
        }

        // 2. Relative duration: "через 1 час 30 минут", "через 20 мин", "на 15 минут", "через час", "in 45 minutes"
        var relPrefix = Regex.Match(working, @"\b(?:через|на|in|for)\s+", RegexOptions.IgnoreCase);
        if (relPrefix.Success)
        {
            string after = working[(relPrefix.Index + relPrefix.Length)..];
            TimeSpan total = TimeSpan.Zero;
            int consumed = 0;

            // Check bare word "час" / "минуту" / "an hour" / "a minute"
            var bareUnit = Regex.Match(after, @"^(?:один\s+|одну\s+|an?\s+)?(час(?:а|ов)?|ч\.?|минут[уы]?|мин\.?|секунд[уы]?|сек\.?|hours?|hrs?|minutes?|mins?|seconds?|secs?)(?=\s|$|[.,!?;])", RegexOptions.IgnoreCase);
            if (bareUnit.Success)
            {
                total += UnitToSpan(1.0, bareUnit.Groups[1].Value);
                consumed = bareUnit.Length;
            }
            else
            {
                var unitPattern = new Regex(
                    @"^\s*(?:и\s+|and\s+)?(\d+(?:[.,]\d+)?)\s*(дн(?:я|ей)|день|сут(?:ок|ки)?|час(?:а|ов)?|ч\.?|минут[уы]?|мин\.?|секунд[уы]?|сек\.?|days?|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?=\s|$|[.,!?;])",
                    RegexOptions.IgnoreCase);

                while (consumed < after.Length)
                {
                    var m = unitPattern.Match(after[consumed..]);
                    if (!m.Success) break;
                    if (double.TryParse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture, out double val) && val > 0)
                    {
                        total += UnitToSpan(val, m.Groups[2].Value);
                        consumed += m.Length;
                    }
                    else break;
                }
            }

            if (total > TimeSpan.Zero && total <= TimeSpan.FromDays(30))
            {
                fireAtLocal = nowLocal.Add(total);
                string remaining = (working[..relPrefix.Index] + " " + after[consumed..]).Trim();
                reminderText = CleanReminderBody(remaining);
                return true;
            }
        }

        // 3. Absolute time: "(завтра) в 18:30" or "в 9 часов" or "at 14:15"
        var absMatch = Regex.Match(
            working,
            @"(?:(завтра|послезавтра|tomorrow)\s+)?\b(?:в|at)\s+(\d{1,2})(?:[:.](\d{2}))?(?:\s*(?:час(?:а|ов)?|ч\.?))?(?=\s|$|[.,!?;])",
            RegexOptions.IgnoreCase);
        if (absMatch.Success && int.TryParse(absMatch.Groups[2].Value, out int hh) && hh is >= 0 and <= 23)
        {
            int mm = 0;
            if (absMatch.Groups[3].Success)
            {
                if (!int.TryParse(absMatch.Groups[3].Value, out mm) || mm is < 0 or > 59)
                    return false;
            }
            else if (!absMatch.Value.Contains(':') && !absMatch.Value.Contains('.') && !Regex.IsMatch(absMatch.Value, @"час|ч\.?", RegexOptions.IgnoreCase))
            {
                // Avoid matching random numbers like "в 2 словах" unless hour word or colon present
                return false;
            }

            string dayWord = absMatch.Groups[1].Value.ToLowerInvariant();
            DateTime target = new(nowLocal.Year, nowLocal.Month, nowLocal.Day, hh, mm, 0, DateTimeKind.Local);
            if (dayWord is "завтра" or "tomorrow")
                target = target.AddDays(1);
            else if (dayWord == "послезавтра")
                target = target.AddDays(2);
            else if (target <= nowLocal)
                target = target.AddDays(1);

            fireAtLocal = target;
            reminderText = CleanReminderBody(working.Remove(absMatch.Index, absMatch.Length));
            return true;
        }

        return false;
    }

    private static TimeSpan UnitToSpan(double value, string unit)
    {
        string u = unit.ToLowerInvariant().TrimEnd('.');
        if (u.StartsWith("дн") || u.StartsWith("ден") || u.StartsWith("сут") || u.StartsWith("day"))
            return TimeSpan.FromDays(value);
        if (u.StartsWith("час") || u == "ч" || u.StartsWith("hour") || u.StartsWith("hr") || u == "h")
            return TimeSpan.FromHours(value);
        if (u.StartsWith("сек") || u.StartsWith("sec") || u == "s")
            return TimeSpan.FromSeconds(value);
        return TimeSpan.FromMinutes(value);
    }

    private static string CleanReminderBody(string raw)
    {
        string s = Regex.Replace(raw ?? "", @"^[\s,.:;—–\-]+", "");
        s = Regex.Replace(s, @"^(?:что|чтобы|о\s+том\s*,?\s*что(?:бы)?|про|about|to)\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(',', ';');
        return string.IsNullOrWhiteSpace(s) ? "Таймер ассистента" : char.ToUpper(s[0]) + s[1..];
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;
            var loaded = JsonSerializer.Deserialize<List<AssistantReminderItem>>(File.ReadAllText(_storagePath, Encoding.UTF8));
            if (loaded is not null)
            {
                lock (_lock)
                {
                    _items.Clear();
                    _items.AddRange(loaded);
                }
            }
        }
        catch { }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(_items, JsonOpts), Encoding.UTF8);
        }
        catch { }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
