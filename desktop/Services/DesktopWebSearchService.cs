using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAIHub.Services;

public sealed record DesktopSearchResult(string Title, string Url, string Snippet, string PubDate = "");

public sealed record DesktopSearchResponse(string Provider, string DirectAnswer, List<DesktopSearchResult> Results)
{
    public string BuildPromptContext()
    {
        if (Results.Count == 0 && string.IsNullOrWhiteSpace(DirectAnswer)) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"[СВЕЖИЕ РЕЗУЛЬТАТЫ ПОИСКА В ИНТЕРНЕТЕ ({Provider}, {DateTime.Now:yyyy-MM-dd HH:mm})]");
        if (!string.IsNullOrWhiteSpace(DirectAnswer))
            sb.AppendLine($"Краткая сводка: {DirectAnswer}\n");

        for (int i = 0; i < Results.Count; i++)
        {
            var r = Results[i];
            sb.Append($"[{i + 1}] {r.Title}");
            if (!string.IsNullOrWhiteSpace(r.PubDate)) sb.Append($" ({r.PubDate})");
            sb.AppendLine();
            sb.AppendLine($"URL: {r.Url}");
            if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                string sn = r.Snippet.Length > 550 ? r.Snippet[..550] + "…" : r.Snippet;
                sb.AppendLine(sn);
            }
            sb.AppendLine();
            if (sb.Length > 4200) break;
        }
        sb.AppendLine("Инструкция: Отвечай точно и кратко на языке пользователя, опираясь на свежие факты выше. Указывай ссылки [1], [2]. Если данных не хватает — честно скажи об этом.");
        return sb.ToString();
    }
}

/// <summary>
/// Hybrid Web Search for Windows Desktop Assistant & Chat.
/// Priority: Optional Tavily/Brave API keys -> Free DuckDuckGo Lite (POST) + Google News RSS + Wikipedia API.
/// </summary>
public static class DesktopWebSearchService
{
    private const string DesktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopUa);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
        return client;
    }

    public static async Task<DesktopSearchResponse> SearchAsync(string rawQuery, CancellationToken ct = default)
    {
        string clean = Regex.Replace(rawQuery ?? "", @"https?://\S+", " ");
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        if (clean.Length > 180) clean = clean[..180].Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return new DesktopSearchResponse("none", "", []);

        var collected = new List<DesktopSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string provider = "";
        string directAnswer = "";

        string tavilyKey = (HubConfig.Current.WebSearchTavilyKey ?? "").Trim();
        string braveKey = (HubConfig.Current.WebSearchBraveKey ?? "").Trim();

        if (!string.IsNullOrEmpty(tavilyKey))
        {
            var tr = await SearchTavilyAsync(clean, tavilyKey, ct);
            if (tr.Results.Count > 0 || !string.IsNullOrEmpty(tr.DirectAnswer))
            {
                directAnswer = tr.DirectAnswer;
                AddUnique(collected, seen, tr.Results, 6);
                provider = Append(provider, "Tavily");
            }
        }

        if (collected.Count < 3 && !string.IsNullOrEmpty(braveKey))
        {
            var br = await SearchBraveAsync(clean, braveKey, ct);
            if (br.Count > 0)
            {
                AddUnique(collected, seen, br, 6);
                provider = Append(provider, "Brave");
            }
        }

        bool hasCyrillic = Regex.IsMatch(clean, @"[\u0400-\u04FF]");
        if (collected.Count < 3)
        {
            var ddg = await SearchDdgLitePostAsync(clean, hasCyrillic ? "ru-ru" : "wt-wt", ct);
            if (ddg.Count > 0)
            {
                AddUnique(collected, seen, ddg, 5);
                provider = Append(provider, "DuckDuckGo");
            }
        }

        if (collected.Count < 4)
        {
            var news = await SearchGoogleNewsRssAsync(clean, hasCyrillic, ct);
            if (news.Count > 0)
            {
                int max = collected.Count == 0 ? 5 : Math.Min(6, collected.Count + 2);
                AddUnique(collected, seen, news, max);
                provider = Append(provider, "Google News");
            }
        }

        if (collected.Count < 3)
        {
            var wiki = await SearchWikipediaAsync(clean, hasCyrillic ? "ru" : "en", ct);
            if (wiki.Count == 0 && hasCyrillic)
                wiki = await SearchWikipediaAsync(clean, "en", ct);
            if (wiki.Count > 0)
            {
                AddUnique(collected, seen, wiki, 5);
                provider = Append(provider, "Wikipedia");
            }
        }

        return new DesktopSearchResponse(string.IsNullOrEmpty(provider) ? "offline" : provider, directAnswer, collected);
    }

    private static async Task<DesktopSearchResponse> SearchTavilyAsync(string query, string key, CancellationToken ct)
    {
        var list = new List<DesktopSearchResult>();
        string answer = "";
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                api_key = key,
                query,
                search_depth = "basic",
                include_answer = true,
                max_results = 5
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("answer", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                    answer = (aEl.GetString() ?? "").Trim();
                if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        string t = it.TryGetProperty("title", out var te) ? (te.GetString() ?? "").Trim() : "";
                        string u = it.TryGetProperty("url", out var ue) ? (ue.GetString() ?? "").Trim() : "";
                        string c = it.TryGetProperty("content", out var ce) ? (ce.GetString() ?? "").Trim() : "";
                        if (!string.IsNullOrEmpty(u)) list.Add(new DesktopSearchResult(string.IsNullOrEmpty(t) ? u : t, u, c));
                    }
                }
            }
        }
        catch { }
        return new DesktopSearchResponse("Tavily", answer, list);
    }

    private static async Task<List<DesktopSearchResult>> SearchBraveAsync(string query, string key, CancellationToken ct)
    {
        var list = new List<DesktopSearchResult>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count=5");
            req.Headers.Add("X-Subscription-Token", key);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("web", out var web)
                    && web.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        string t = StripHtml(it.TryGetProperty("title", out var te) ? te.GetString() : "");
                        string u = it.TryGetProperty("url", out var ue) ? (ue.GetString() ?? "").Trim() : "";
                        string d = StripHtml(it.TryGetProperty("description", out var de) ? de.GetString() : "");
                        string age = it.TryGetProperty("age", out var ae) ? (ae.GetString() ?? "").Trim() : "";
                        if (!string.IsNullOrEmpty(u)) list.Add(new DesktopSearchResult(string.IsNullOrEmpty(t) ? u : t, u, d, age));
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<DesktopSearchResult>> SearchDdgLitePostAsync(string query, string kl, CancellationToken ct)
    {
        var list = new List<DesktopSearchResult>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://lite.duckduckgo.com/lite/")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query, ["kl"] = kl })
            };
            req.Headers.Referrer = new Uri("https://lite.duckduckgo.com/");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                string html = await resp.Content.ReadAsStringAsync(ct);
                var linkMatches = Regex.Matches(html, @"<a[^>]+href=[""']([^""']+)[""'][^>]*class=[""']result-link[""'][^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var snipMatches = Regex.Matches(html, @"<td[^>]*class=[""']result-snippet[""'][^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

                var urls = new List<string>();
                var titles = new List<string>();
                foreach (Match m in linkMatches)
                {
                    if (urls.Count >= 5) break;
                    string href = UnwrapDdgUrl(WebUtility.HtmlDecode(m.Groups[1].Value));
                    string title = StripHtml(m.Groups[2].Value);
                    if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(title))
                    {
                        urls.Add(href);
                        titles.Add(title);
                    }
                }
                var snips = snipMatches.Select(m => StripHtml(m.Groups[1].Value)).ToList();
                for (int i = 0; i < urls.Count; i++)
                    list.Add(new DesktopSearchResult(titles[i], urls[i], i < snips.Count ? snips[i] : ""));
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<DesktopSearchResult>> SearchGoogleNewsRssAsync(string query, bool isRu, CancellationToken ct)
    {
        var list = new List<DesktopSearchResult>();
        try
        {
            string lp = isRu ? "&hl=ru&gl=RU&ceid=RU:ru" : "&hl=en-US&gl=US&ceid=US:en";
            string xml = await Http.GetStringAsync($"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}{lp}", ct);
            foreach (Match m in Regex.Matches(xml, @"<item>(.*?)</item>", RegexOptions.Singleline))
            {
                if (list.Count >= 4) break;
                string item = m.Groups[1].Value;
                string title = StripHtml(ExtractTag(item, "title"));
                string link = WebUtility.HtmlDecode(ExtractTag(item, "link")).Trim();
                string pubDate = StripHtml(ExtractTag(item, "pubDate"));
                string desc = StripHtml(WebUtility.HtmlDecode(ExtractTag(item, "description")));
                if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(title))
                    list.Add(new DesktopSearchResult(title, link, desc == title ? "" : desc, pubDate));
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<DesktopSearchResult>> SearchWikipediaAsync(string query, string lang, CancellationToken ct)
    {
        var list = new List<DesktopSearchResult>();
        try
        {
            string json = await Http.GetStringAsync($"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json&utf8=1&srlimit=3", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("query", out var q)
                && q.TryGetProperty("search", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (list.Count >= 2) break;
                    string title = (it.GetProperty("title").GetString() ?? "").Trim();
                    string snip = StripHtml(it.GetProperty("snippet").GetString());
                    if (!string.IsNullOrEmpty(title))
                    {
                        string pageUrl = $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
                        list.Add(new DesktopSearchResult($"{title} — Википедия", pageUrl, snip));
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private static void AddUnique(List<DesktopSearchResult> dst, HashSet<string> seen, List<DesktopSearchResult> src, int max)
    {
        foreach (var r in src)
        {
            if (dst.Count >= max) break;
            if (string.IsNullOrWhiteSpace(r.Url) || !seen.Add(r.Url)) continue;
            dst.Add(r);
        }
    }

    private static string Append(string cur, string next) =>
        string.IsNullOrEmpty(cur) ? next : (cur.Contains(next) ? cur : cur + " + " + next);

    private static string UnwrapDdgUrl(string href)
    {
        if (href.StartsWith("//")) href = "https:" + href;
        int idx = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (idx >= 0)
        {
            string sub = href[(idx + 5)..];
            int amp = sub.IndexOf('&');
            if (amp >= 0) sub = sub[..amp];
            return Uri.UnescapeDataString(sub);
        }
        return href;
    }

    private static string ExtractTag(string xml, string tag)
    {
        var m = Regex.Match(xml, $@"<{tag}[^>]*>(.*?)</{tag}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        string v = m.Groups[1].Value.Trim();
        if (v.StartsWith("<![CDATA[") && v.EndsWith("]]>")) v = v[9..^3];
        return v;
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string noTags = Regex.Replace(html, @"<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(noTags), @"\s+", " ").Trim();
    }
}
