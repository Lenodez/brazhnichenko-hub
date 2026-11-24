package com.brazhnichenko.hub;

import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLDecoder;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Hybrid Web Search Engine for offline-first mobile LLMs.
 * Priority:
 *  1) Optional user API keys: Tavily API / Brave Search API (when configured in System settings)
 *  2) Free zero-key hybrid: DuckDuckGo Lite (POST) + Google News RSS (fresh dates/news) + Wikipedia RU/EN API
 *  3) Direct URL reader if the user includes an http(s):// link in the prompt
 */
public final class WebSearchEngine {
    private static final String DESKTOP_UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public static final class SearchResult {
        public final String title;
        public final String url;
        public final String snippet;
        public final String pubDate;

        public SearchResult(String title, String url, String snippet, String pubDate) {
            this.title = title;
            this.url = url;
            this.snippet = snippet;
            this.pubDate = pubDate;
        }

        public JSONObject toJson() {
            JSONObject o = new JSONObject();
            try {
                o.put("title", title);
                o.put("url", url);
                o.put("snippet", snippet);
                if (pubDate != null && !pubDate.isEmpty()) o.put("pubDate", pubDate);
            } catch (Exception ignored) { }
            return o;
        }
    }

    public static final class SearchResponse {
        public final String provider;
        public final String directAnswer;
        public final List<SearchResult> results;

        public SearchResponse(String provider, String directAnswer, List<SearchResult> results) {
            this.provider = provider;
            this.directAnswer = directAnswer;
            this.results = results;
        }

        public JSONArray resultsToJson() {
            JSONArray arr = new JSONArray();
            for (SearchResult r : results) arr.put(r.toJson());
            return arr;
        }
    }

    private WebSearchEngine() { }

    public static SearchResponse search(String rawQuery, SharedPreferences prefs) {
        String cleanQuery = sanitizeQuery(rawQuery);
        if (cleanQuery.isEmpty()) {
            return new SearchResponse("none", "", new ArrayList<>());
        }

        List<SearchResult> collected = new ArrayList<>();
        Set<String> seenUrls = new LinkedHashSet<>();
        String usedProvider = "";
        String directAnswer = "";

        // 0. If user pasted a direct URL in the prompt, fetch its readable text first
        String embeddedUrl = extractFirstUrl(rawQuery);
        if (embeddedUrl != null) {
            SearchResult pageRes = fetchUrlExcerpt(embeddedUrl);
            if (pageRes != null) {
                collected.add(pageRes);
                seenUrls.add(pageRes.url);
                usedProvider = "URL";
            }
        }

        String mode = prefs != null ? prefs.getString("search_provider", "auto") : "auto";
        String tavilyKey = prefs != null ? prefs.getString("tavily_api_key", "").trim() : "";
        String braveKey = prefs != null ? prefs.getString("brave_api_key", "").trim() : "";

        // 1. Optional Tavily API
        if (("auto".equals(mode) || "tavily".equals(mode)) && !tavilyKey.isEmpty()) {
            SearchResponse tr = searchTavily(cleanQuery, tavilyKey);
            if (!tr.results.isEmpty() || !tr.directAnswer.isEmpty()) {
                directAnswer = tr.directAnswer;
                addUnique(collected, seenUrls, tr.results, 6);
                usedProvider = appendProvider(usedProvider, "Tavily");
            }
        }

        // 2. Optional Brave Search API
        if (collected.size() < 3 && ("auto".equals(mode) || "brave".equals(mode)) && !braveKey.isEmpty()) {
            SearchResponse br = searchBrave(cleanQuery, braveKey);
            if (!br.results.isEmpty()) {
                addUnique(collected, seenUrls, br.results, 6);
                usedProvider = appendProvider(usedProvider, "Brave");
            }
        }

        // 3. Free zero-key primary: DuckDuckGo Lite via POST (direct clean links + rich snippets!)
        boolean hasCyrillic = cleanQuery.matches(".*[\\u0400-\\u04FF].*");
        if (collected.size() < 3) {
            List<SearchResult> ddg = searchDuckDuckGoLitePost(cleanQuery, hasCyrillic ? "ru-ru" : "wt-wt");
            if (!ddg.isEmpty()) {
                addUnique(collected, seenUrls, ddg, 5);
                usedProvider = appendProvider(usedProvider, "DuckDuckGo");
            }
        }

        // 4. Free fresh news supplement: Google News RSS (adds fresh dated headlines!)
        if (collected.size() < 5) {
            List<SearchResult> news = searchGoogleNewsRss(cleanQuery, hasCyrillic);
            if (!news.isEmpty()) {
                int maxToAdd = collected.isEmpty() ? 5 : Math.min(6, collected.size() + 2);
                addUnique(collected, seenUrls, news, maxToAdd);
                usedProvider = appendProvider(usedProvider, "Google News");
            }
        }

        // 5. Free encyclopedic fallback: Wikipedia API (RU / EN)
        if (collected.size() < 3) {
            List<SearchResult> wiki = searchWikipedia(cleanQuery, hasCyrillic ? "ru" : "en");
            if (wiki.isEmpty() && hasCyrillic) {
                wiki = searchWikipedia(cleanQuery, "en");
            }
            if (!wiki.isEmpty()) {
                addUnique(collected, seenUrls, wiki, 5);
                usedProvider = appendProvider(usedProvider, "Wikipedia");
            }
        }

        return new SearchResponse(usedProvider.isEmpty() ? "offline" : usedProvider, directAnswer, collected);
    }

    public static String buildPromptContext(SearchResponse sr) {
        if (sr == null || (sr.results.isEmpty() && (sr.directAnswer == null || sr.directAnswer.isEmpty()))) {
            return "";
        }
        String today = new java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", new Locale("ru", "RU")).format(new java.util.Date());
        StringBuilder sb = new StringBuilder();
        sb.append("[СВЕЖИЕ РЕЗУЛЬТАТЫ ПОИСКА В ИНТЕРНЕТЕ (").append(sr.provider).append(", ").append(today).append(")]\n");
        if (sr.directAnswer != null && !sr.directAnswer.isEmpty()) {
            sb.append("Краткая сводка: ").append(sr.directAnswer).append("\n\n");
        }
        int idx = 1;
        for (SearchResult r : sr.results) {
            sb.append("[").append(idx++).append("] ").append(r.title);
            if (r.pubDate != null && !r.pubDate.isEmpty()) {
                sb.append(" (").append(r.pubDate).append(")");
            }
            sb.append("\nURL: ").append(r.url).append("\n");
            if (r.snippet != null && !r.snippet.isEmpty()) {
                String sn = r.snippet.length() > 550 ? r.snippet.substring(0, 550) + "…" : r.snippet;
                sb.append(sn).append("\n");
            }
            sb.append("\n");
            if (sb.length() > 4000) break;
        }
        sb.append("Инструкция: Отвечай точно, кратко и по делу на языке пользователя, опираясь на свежие факты из источников выше. ")
          .append("Ссылайся на источники в формате [1], [2]. Если нужных данных в результатах нет — честно скажи об этом и не придумывай цифры.");
        return sb.toString();
    }

    private static SearchResponse searchTavily(String query, String apiKey) {
        List<SearchResult> list = new ArrayList<>();
        String answer = "";
        try {
            HttpURLConnection c = (HttpURLConnection) new URL("https://api.tavily.com/search").openConnection();
            c.setRequestMethod("POST");
            c.setDoOutput(true);
            c.setConnectTimeout(5500);
            c.setReadTimeout(7500);
            c.setRequestProperty("Content-Type", "application/json");
            c.setRequestProperty("Accept", "application/json");
            JSONObject body = new JSONObject();
            body.put("api_key", apiKey);
            body.put("query", query);
            body.put("search_depth", "basic");
            body.put("include_answer", true);
            body.put("max_results", 5);
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            try (OutputStream os = c.getOutputStream()) { os.write(bytes); }
            if (c.getResponseCode() == 200) {
                String resp = readStream(c.getInputStream(), 256 * 1024);
                JSONObject j = new JSONObject(resp);
                answer = j.optString("answer", "").trim();
                JSONArray arr = j.optJSONArray("results");
                if (arr != null) {
                    for (int i = 0; i < arr.length() && list.size() < 5; i++) {
                        JSONObject it = arr.getJSONObject(i);
                        String title = it.optString("title", "").trim();
                        String url = it.optString("url", "").trim();
                        String content = it.optString("content", "").trim();
                        if (!url.isEmpty() && (!title.isEmpty() || !content.isEmpty())) {
                            list.add(new SearchResult(title.isEmpty() ? url : title, url, content, ""));
                        }
                    }
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return new SearchResponse("Tavily", answer, list);
    }

    private static SearchResponse searchBrave(String query, String apiKey) {
        List<SearchResult> list = new ArrayList<>();
        try {
            String u = "https://api.search.brave.com/res/v1/web/search?q=" + URLEncoder.encode(query, "UTF-8") + "&count=5";
            HttpURLConnection c = (HttpURLConnection) new URL(u).openConnection();
            c.setConnectTimeout(5500);
            c.setReadTimeout(7000);
            c.setRequestProperty("Accept", "application/json");
            c.setRequestProperty("X-Subscription-Token", apiKey);
            if (c.getResponseCode() == 200) {
                String resp = readStream(c.getInputStream(), 256 * 1024);
                JSONObject j = new JSONObject(resp);
                JSONObject web = j.optJSONObject("web");
                JSONArray arr = web != null ? web.optJSONArray("results") : null;
                if (arr != null) {
                    for (int i = 0; i < arr.length() && list.size() < 5; i++) {
                        JSONObject it = arr.getJSONObject(i);
                        String title = stripHtml(it.optString("title", ""));
                        String url = it.optString("url", "").trim();
                        String desc = stripHtml(it.optString("description", ""));
                        String age = it.optString("age", "").trim();
                        if (!url.isEmpty() && (!title.isEmpty() || !desc.isEmpty())) {
                            list.add(new SearchResult(title.isEmpty() ? url : title, url, desc, age));
                        }
                    }
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return new SearchResponse("Brave", "", list);
    }

    private static List<SearchResult> searchDuckDuckGoLitePost(String query, String kl) {
        List<SearchResult> list = new ArrayList<>();
        try {
            HttpURLConnection c = (HttpURLConnection) new URL("https://lite.duckduckgo.com/lite/").openConnection();
            c.setRequestMethod("POST");
            c.setDoOutput(true);
            c.setConnectTimeout(5000);
            c.setReadTimeout(7000);
            c.setInstanceFollowRedirects(true);
            c.setRequestProperty("User-Agent", DESKTOP_UA);
            c.setRequestProperty("Referer", "https://lite.duckduckgo.com/");
            c.setRequestProperty("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8");
            c.setRequestProperty("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
            String form = "q=" + URLEncoder.encode(query, "UTF-8") + "&kl=" + URLEncoder.encode(kl, "UTF-8");
            try (OutputStream os = c.getOutputStream()) {
                os.write(form.getBytes(StandardCharsets.UTF_8));
            }
            if (c.getResponseCode() == 200) {
                String html = readStream(c.getInputStream(), 256 * 1024);
                Pattern linkPat = Pattern.compile("<a[^>]+href=[\"']([^\"']+)[\"'][^>]*class=[\"']result-link[\"'][^>]*>(.*?)</a>", Pattern.DOTALL | Pattern.CASE_INSENSITIVE);
                Pattern snipPat = Pattern.compile("<td[^>]*class=[\"']result-snippet[\"'][^>]*>(.*?)</td>", Pattern.DOTALL | Pattern.CASE_INSENSITIVE);

                List<String> urls = new ArrayList<>();
                List<String> titles = new ArrayList<>();
                Matcher lm = linkPat.matcher(html);
                while (lm.find() && urls.size() < 5) {
                    String href = unwrapDdgUrl(decodeXmlEntities(lm.group(1)));
                    String title = stripHtml(lm.group(2));
                    if (href.startsWith("http") && !title.isEmpty()) {
                        urls.add(href);
                        titles.add(title);
                    }
                }
                List<String> snippets = new ArrayList<>();
                Matcher sm = snipPat.matcher(html);
                while (sm.find() && snippets.size() < 5) {
                    snippets.add(stripHtml(sm.group(1)));
                }
                for (int i = 0; i < urls.size(); i++) {
                    String sn = i < snippets.size() ? snippets.get(i) : "";
                    list.add(new SearchResult(titles.get(i), urls.get(i), sn, ""));
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return list;
    }

    private static List<SearchResult> searchGoogleNewsRss(String query, boolean isRu) {
        List<SearchResult> list = new ArrayList<>();
        try {
            String langParams = isRu ? "&hl=ru&gl=RU&ceid=RU:ru" : "&hl=en-US&gl=US&ceid=US:en";
            String u = "https://news.google.com/rss/search?q=" + URLEncoder.encode(query, "UTF-8") + langParams;
            HttpURLConnection c = (HttpURLConnection) new URL(u).openConnection();
            c.setConnectTimeout(4500);
            c.setReadTimeout(6000);
            c.setInstanceFollowRedirects(true);
            c.setRequestProperty("User-Agent", DESKTOP_UA);
            if (c.getResponseCode() == 200) {
                String xml = readStream(c.getInputStream(), 256 * 1024);
                Pattern itemPat = Pattern.compile("<item>(.*?)</item>", Pattern.DOTALL);
                Matcher m = itemPat.matcher(xml);
                while (m.find() && list.size() < 4) {
                    String item = m.group(1);
                    String title = stripHtml(extractXmlTag(item, "title"));
                    String link = decodeXmlEntities(extractXmlTag(item, "link")).trim();
                    String pubDate = stripHtml(extractXmlTag(item, "pubDate"));
                    String desc = stripHtml(decodeXmlEntities(extractXmlTag(item, "description")));
                    if (!link.isEmpty() && link.startsWith("http") && !title.isEmpty()) {
                        String cleanSnippet = desc.equals(title) ? "" : desc;
                        list.add(new SearchResult(title, link, cleanSnippet, pubDate));
                    }
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return list;
    }

    private static List<SearchResult> searchWikipedia(String query, String lang) {
        List<SearchResult> list = new ArrayList<>();
        try {
            String u = "https://" + lang + ".wikipedia.org/w/api.php?action=query&list=search&srsearch="
                    + URLEncoder.encode(query, "UTF-8") + "&format=json&utf8=1&srlimit=3";
            HttpURLConnection c = (HttpURLConnection) new URL(u).openConnection();
            c.setConnectTimeout(4500);
            c.setReadTimeout(6000);
            c.setRequestProperty("User-Agent", "BrazhnichenkoHub/4.6 (Android Local AI)");
            if (c.getResponseCode() == 200) {
                String resp = readStream(c.getInputStream(), 128 * 1024);
                JSONObject j = new JSONObject(resp);
                JSONObject q = j.optJSONObject("query");
                JSONArray arr = q != null ? q.optJSONArray("search") : null;
                if (arr != null) {
                    for (int i = 0; i < arr.length() && list.size() < 2; i++) {
                        JSONObject it = arr.getJSONObject(i);
                        String title = it.optString("title", "").trim();
                        String snippet = stripHtml(it.optString("snippet", ""));
                        if (!title.isEmpty()) {
                            String pageUrl = "https://" + lang + ".wikipedia.org/wiki/" + URLEncoder.encode(title.replace(' ', '_'), "UTF-8");
                            list.add(new SearchResult(title + " — Википедия", pageUrl, snippet, ""));
                        }
                    }
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return list;
    }

    private static SearchResult fetchUrlExcerpt(String url) {
        try {
            HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(4500);
            c.setReadTimeout(5500);
            c.setInstanceFollowRedirects(true);
            c.setRequestProperty("User-Agent", DESKTOP_UA);
            if (c.getResponseCode() == 200) {
                String html = readStream(c.getInputStream(), 180 * 1024);
                String title = stripHtml(extractXmlTag(html, "title"));
                String cleaned = html
                        .replaceAll("(?is)<script[^>]*>.*?</script>", " ")
                        .replaceAll("(?is)<style[^>]*>.*?</style>", " ")
                        .replaceAll("(?is)<noscript[^>]*>.*?</noscript>", " ");
                String text = stripHtml(cleaned);
                if (text.length() > 1400) text = text.substring(0, 1400) + "…";
                if (text.length() >= 60) {
                    return new SearchResult(title.isEmpty() ? url : title, url, text, "");
                }
            }
            c.disconnect();
        } catch (Exception ignored) { }
        return null;
    }

    private static String extractFirstUrl(String text) {
        if (text == null) return null;
        Matcher m = Pattern.compile("https?://[^\\s<>\"'\\)\\]]+").matcher(text);
        return m.find() ? m.group(0) : null;
    }

    private static String sanitizeQuery(String raw) {
        if (raw == null) return "";
        String q = raw.replaceAll("https?://\\S+", " ").replaceAll("\\s+", " ").trim();
        if (q.length() > 180) q = q.substring(0, 180).trim();
        return q;
    }

    private static void addUnique(List<SearchResult> dst, Set<String> seen, List<SearchResult> src, int maxTotal) {
        for (SearchResult r : src) {
            if (dst.size() >= maxTotal) break;
            if (r.url == null || r.url.isEmpty() || seen.contains(r.url)) continue;
            seen.add(r.url);
            dst.add(r);
        }
    }

    private static String appendProvider(String cur, String next) {
        if (cur == null || cur.isEmpty()) return next;
        if (cur.contains(next)) return cur;
        return cur + " + " + next;
    }

    private static String unwrapDdgUrl(String href) {
        try {
            if (href.startsWith("//")) href = "https:" + href;
            int idx = href.indexOf("uddg=");
            if (idx >= 0) {
                String sub = href.substring(idx + 5);
                int amp = sub.indexOf('&');
                if (amp >= 0) sub = sub.substring(0, amp);
                return URLDecoder.decode(sub, "UTF-8");
            }
        } catch (Exception ignored) { }
        return href;
    }

    private static String extractXmlTag(String xml, String tag) {
        Pattern p = Pattern.compile("(?is)<" + tag + "[^>]*>(.*?)</" + tag + ">");
        Matcher m = p.matcher(xml);
        if (!m.find()) return "";
        String v = m.group(1).trim();
        if (v.startsWith("<![CDATA[") && v.endsWith("]]>")) {
            v = v.substring(9, v.length() - 3);
        }
        return v;
    }

    private static String decodeXmlEntities(String s) {
        if (s == null) return "";
        return s.replace("&amp;", "&")
                .replace("&lt;", "<")
                .replace("&gt;", ">")
                .replace("&quot;", "\"")
                .replace("&#39;", "'")
                .replace("&apos;", "'")
                .replace("&nbsp;", " ")
                .replace("&#x27;", "'");
    }

    private static String stripHtml(String s) {
        if (s == null || s.isEmpty()) return "";
        String noTags = s.replaceAll("(?s)<[^>]+>", " ");
        return decodeXmlEntities(noTags).replaceAll("\\s+", " ").trim();
    }

    private static String readStream(InputStream is, int maxBytes) throws Exception {
        if (is == null) return "";
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n, total = 0;
        while ((n = is.read(buf)) > 0 && total < maxBytes) {
            baos.write(buf, 0, Math.min(n, maxBytes - total));
            total += n;
        }
        return baos.toString("UTF-8");
    }
}
