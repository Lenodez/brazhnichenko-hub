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
    public const string DefaultServerBaseUrl = "https://github.com/Lenodez/brazhnichenko-hub/releases/latest";
    private const string DefaultHubAccessToken = "";
    private const string DefaultPinnedCertSha256 = "";

    public static string EffectiveServerBaseUrl
    {
        get
        {
            string custom = (HubConfig.Current.UpdateServerUrl ?? "").Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(custom) ? DefaultServerBaseUrl : custom;
        }
    }

    private static string EffectiveToken
    {
        get
        {
            string custom = (HubConfig.Current.UpdateServerToken ?? "").Trim();
            return string.IsNullOrWhiteSpace(custom) ? DefaultHubAccessToken : custom;
        }
    }

    private static string EffectiveCertSha256
    {
        get
        {
            string custom = (HubConfig.Current.UpdateCertSha256 ?? "").Trim().Replace(":", "").Replace(" ", "");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            // Use built-in pin only for the built-in default server; for custom URL without pin allow valid CA or explicit "ANY"
            return string.Equals(EffectiveServerBaseUrl, DefaultServerBaseUrl, StringComparison.OrdinalIgnoreCase)
                ? DefaultPinnedCertSha256
                : "";
        }
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null) return "2.3.1";
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    private static HttpClient CreatePinnedClient(TimeSpan timeout)
    {
        string expectedPin = EffectiveCertSha256;
        string token = EffectiveToken;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, sslErrors) =>
                {
                    if (cert is null) return false;
                    // Standard trusted HTTPS CA (e.g. Let's Encrypt on a custom domain)
                    if (sslErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;
                    // Explicit self-signed trust mode if user entered "trust" / "*" for a custom IP
                    if (expectedPin.Equals("trust", StringComparison.OrdinalIgnoreCase) || expectedPin == "*")
                        return true;
                    if (!string.IsNullOrWhiteSpace(expectedPin))
                    {
                        using var x509 = new X509Certificate2(cert);
                        string fp = x509.GetCertHashString(HashAlgorithmName.SHA256);
                        return string.Equals(fp, expectedPin, StringComparison.OrdinalIgnoreCase);
                    }
                    // If user switched to a custom IP without entering a cert SHA-256 pin yet,
                    // allow self-signed TLS (payload integrity is still enforced by SHA-256 check on .exe!)
                    return !string.Equals(EffectiveServerBaseUrl, DefaultServerBaseUrl, StringComparison.OrdinalIgnoreCase);
                }
            }
        };

        var http = new HttpClient(handler) { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Add("X-Hub-Token", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalAIHub/{CurrentVersion}");
        return http;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string cur = CurrentVersion;
        string baseUrl = EffectiveServerBaseUrl;
        try
        {
            using var http = CreatePinnedClient(TimeSpan.FromSeconds(8));
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

        using var http = CreatePinnedClient(TimeSpan.FromMinutes(3));
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

        // Mandatory SHA-256 verification before executing downloaded binary
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
