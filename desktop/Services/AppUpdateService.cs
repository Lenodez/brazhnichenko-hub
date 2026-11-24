using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string Notes,
    string DownloadUrl,
    string Sha256,
    long SizeBytes,
    string PublishedAt,
    string? ErrorMessage = null);

public sealed class AppUpdateService
{
    public const string DefaultGitHubRepo = "Lenodez/brazhnichenko-hub";
    public const string DefaultServerBaseUrl = "https://github.com/" + DefaultGitHubRepo;

    public static string EffectiveServerBaseUrl
    {
        get
        {
            string custom = (HubConfig.Current.UpdateServerUrl ?? "").Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(custom) ? DefaultServerBaseUrl : custom;
        }
    }

    private static bool IsGitHubMode(string url) =>
        url.Contains("github.com/", StringComparison.OrdinalIgnoreCase)
        || url.Contains("api.github.com/", StringComparison.OrdinalIgnoreCase);

    private static string EffectiveToken => (HubConfig.Current.UpdateServerToken ?? "").Trim();

    private static string EffectiveCertSha256 =>
        (HubConfig.Current.UpdateCertSha256 ?? "").Trim().Replace(":", "").Replace(" ", "");

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null) return "4.8.0";
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout, bool includeCustomToken)
    {
        string expectedPin = EffectiveCertSha256;
        string token = includeCustomToken ? EffectiveToken : "";

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = true,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, sslErrors) =>
                {
                    if (cert is null) return false;
                    if (sslErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;
                    if (expectedPin.Equals("trust", StringComparison.OrdinalIgnoreCase) || expectedPin == "*")
                        return true;
                    if (!string.IsNullOrWhiteSpace(expectedPin))
                    {
                        using var x509 = new X509Certificate2(cert);
                        string fp = x509.GetCertHashString(HashAlgorithmName.SHA256);
                        return string.Equals(fp, expectedPin, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                }
            }
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Add("X-Hub-Token", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrazhnichenkoHub/{CurrentVersion}");
        return http;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string cur = CurrentVersion;
        string baseUrl = EffectiveServerBaseUrl;
        return IsGitHubMode(baseUrl)
            ? await CheckGitHubReleaseAsync(baseUrl, cur, ct)
            : await CheckCustomServerAsync(baseUrl, cur, ct);
    }

    private async Task<UpdateCheckResult> CheckGitHubReleaseAsync(string baseUrl, string cur, CancellationToken ct)
    {
        try
        {
            string apiUrl = ResolveGitHubLatestApi(baseUrl);
            using var http = CreateHttpClient(TimeSpan.FromSeconds(10), includeCustomToken: false);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(apiUrl, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "").Trim() : "";
            string latest = tag.TrimStart('v', 'V');
            string notes = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "").Trim() : "";
            if (notes.Length > 320) notes = notes[..320].Trim() + "…";
            string pub = root.TryGetProperty("published_at", out var pEl) ? (pEl.GetString() ?? "").Trim() : "";

            string exeUrl = "";
            string sha256 = "";
            long sizeBytes = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    exeUrl = a.TryGetProperty("browser_download_url", out var uEl) ? (uEl.GetString() ?? "").Trim() : "";
                    sizeBytes = a.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sz) ? sz : 0;
                    if (a.TryGetProperty("digest", out var dEl))
                    {
                        string dg = (dEl.GetString() ?? "").Trim();
                        if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            sha256 = dg["sha256:".Length..].Trim();
                    }
                    if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateCheckResult(false, false, cur, cur, "", "", "", 0, "", "Не найден tag_name в GitHub Release.");

            bool hasUpdate = CompareVersions(latest, cur) > 0 && !string.IsNullOrWhiteSpace(exeUrl);
            return new UpdateCheckResult(true, hasUpdate, cur, latest, notes, exeUrl, sha256, sizeBytes, pub);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, cur, cur, "", "", "", 0, "", ex.Message);
        }
    }

    private static string ResolveGitHubLatestApi(string url)
    {
        if (url.Contains("api.github.com/repos/", StringComparison.OrdinalIgnoreCase))
            return url.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase) ? url : url.TrimEnd('/') + "/releases/latest";

        // Convert https://github.com/{owner}/{repo}[/...] -> https://api.github.com/repos/{owner}/{repo}/releases/latest
        var uri = new Uri(url);
        var segs = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string repo = segs.Length >= 2 ? $"{segs[0]}/{segs[1]}" : DefaultGitHubRepo;
        return $"https://api.github.com/repos/{repo}/releases/latest";
    }

    private async Task<UpdateCheckResult> CheckCustomServerAsync(string baseUrl, string cur, CancellationToken ct)
    {
        try
        {
            using var http = CreateHttpClient(TimeSpan.FromSeconds(8), includeCustomToken: true);
            using var resp = await http.GetAsync($"{baseUrl}/latest.json", ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string latest = root.TryGetProperty("version", out var vEl) ? (vEl.GetString() ?? "").Trim() : "";
            string notes = root.TryGetProperty("notes", out var nEl) ? (nEl.GetString() ?? "").Trim() : "";
            string url = root.TryGetProperty("url", out var uEl) ? (uEl.GetString() ?? "").Trim() : "";
            string sha256 = root.TryGetProperty("sha256", out var sEl) ? (sEl.GetString() ?? "").Trim() : "";
            long sizeBytes = root.TryGetProperty("size_bytes", out var szEl) && szEl.TryGetInt64(out long sz) ? sz : 0;
            string pub = root.TryGetProperty("published_at", out var pEl) ? (pEl.GetString() ?? "").Trim() : "";

            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(url))
                return new UpdateCheckResult(false, false, cur, cur, "", "", "", 0, "", "Некорректный latest.json на сервере.");

            if (url.StartsWith('/'))
                url = baseUrl + url;

            bool hasUpdate = CompareVersions(latest, cur) > 0;
            return new UpdateCheckResult(true, hasUpdate, cur, latest, notes, url, sha256, sizeBytes, pub);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, false, cur, cur, "", "", "", 0, "", ex.Message);
        }
    }

    public async Task<string> DownloadAndVerifyInstallerAsync(
        UpdateCheckResult info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string updatesDir = Path.Combine(HubConfig.ConfigDir, "updates");
        Directory.CreateDirectory(updatesDir);

        string safeVer = string.Concat(info.LatestVersion.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'));
        string finalPath = Path.Combine(updatesDir, $"LocalAIHub-Setup-{safeVer}.exe");
        string partPath = finalPath + ".part";

        bool custom = !IsGitHubMode(info.DownloadUrl);
        using var http = CreateHttpClient(TimeSpan.FromMinutes(3), includeCustomToken: custom);
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? info.SizeBytes;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buf = new byte[81920];
            long received = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                received += read;
                if (total > 0)
                    progress?.Report(Math.Clamp(received * 100.0 / total, 0, 100));
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var fs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = await SHA256.HashDataAsync(fs, ct);
            string actual = Convert.ToHexString(hash);
            if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(partPath); } catch { }
                throw new InvalidOperationException("Ошибка проверки SHA-256 скачанного обновления! Файл удалён ради безопасности.");
            }
        }

        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partPath, finalPath);
        progress?.Report(100);
        return finalPath;
    }

    public static void LaunchAutoUpdater(string installerExePath)
    {
        string targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        int pid = Environment.ProcessId;
        var psi = new ProcessStartInfo
        {
            FileName = installerExePath,
            Arguments = $"--auto-update --target \"{targetDir}\" --pid {pid}",
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static int CompareVersions(string a, string b)
    {
        static Version ParseClean(string s)
        {
            string clean = s.Trim().TrimStart('v', 'V');
            var parts = clean.Split('.');
            if (parts.Length == 2) clean += ".0";
            return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
        }
        return ParseClean(a).CompareTo(ParseClean(b));
    }
}
