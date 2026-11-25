using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed record HfUserInfo(bool Connected, string? Username, string? FullName, string? Error);

public sealed class HfAccessRequiredException : Exception
{
    public string RepoId { get; }
    public int StatusCode { get; }
    public bool HasToken { get; }
    public string RepoUrl => $"https://huggingface.co/{RepoId}";

    public HfAccessRequiredException(string repoId, int statusCode, bool hasToken)
        : base(hasToken
            ? $"Требуется принять условия использования модели {repoId} на странице https://huggingface.co/{repoId} (HTTP {statusCode})."
            : $"Модель {repoId} закрыта (gated, HTTP {statusCode}): требуется подключить токен Hugging Face и принять лицензию на https://huggingface.co/{repoId}.")
    {
        RepoId = repoId;
        StatusCode = statusCode;
        HasToken = hasToken;
    }
}
public sealed record HfRepoSearchItem(string RepoId, long Downloads, long Likes, string? PipelineTag, bool Gated);
public sealed record HfRepoFileItem(string Path, long SizeBytes, string? Sha256);
public sealed record HfDownloadProgress(
    string ModelName,
    string CurrentFile,
    int FileIndex,
    int TotalFiles,
    double Percent,
    double DownloadedMB,
    double TotalMB,
    double SpeedMBs,
    string StatusText);

public sealed class HuggingFaceClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient ApiHttp = new(new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly HttpClient DownloadHttp = new(new SocketsHttpHandler { ConnectTimeout = ConnectTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static HttpRequestMessage CreateAuthRequest(HttpMethod method, string url, string? overrideToken = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd("LocalAIHub/2.0");
        string? token = overrideToken ?? HubConfig.GetHuggingFaceToken();
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<HfUserInfo> VerifyTokenAsync(string? token = null, CancellationToken ct = default)
    {
        string? effective = token ?? HubConfig.GetHuggingFaceToken();
        if (string.IsNullOrWhiteSpace(effective))
            return new(false, null, null, "Токен не задан (публичные модели доступны и без токена)");

        try
        {
            using var req = CreateAuthRequest(HttpMethod.Get, "https://huggingface.co/api/whoami-v2", effective);
            using var resp = await ApiHttp.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, null, null, $"Токен отклонён (HTTP {(int)resp.StatusCode})");

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            string? name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? full = doc.RootElement.TryGetProperty("fullname", out var fn) ? fn.GetString() : null;
            return new(true, name, full, null);
        }
        catch (Exception ex)
        {
            return new(false, null, null, ex.Message);
        }
    }

    public async Task<List<HfRepoSearchItem>> SearchModelsAsync(string query, int limit = 12, CancellationToken ct = default)
    {
        string q = Uri.EscapeDataString(query.Trim());
        string url = $"https://huggingface.co/api/models?search={q}&sort=downloads&direction=-1&limit={Math.Clamp(limit, 1, 50)}";
        return await FetchModelListAsync(url, ct);
    }

    public async Task<List<HfRepoSearchItem>> FetchTopPopularModelsAsync(string mode = "llm", int limit = 50, CancellationToken ct = default)
    {
        int lim = Math.Clamp(limit, 5, 60);
        string url = mode switch
        {
            "llm" => $"https://huggingface.co/api/models?filter=gguf&pipeline_tag=text-generation&sort=downloads&direction=-1&limit={lim}",
            "lora" => $"https://huggingface.co/api/models?search=lora&sort=downloads&direction=-1&limit={lim}",
            "media" => $"https://huggingface.co/api/models?filter=diffusers&sort=downloads&direction=-1&limit={lim}",
            _ => $"https://huggingface.co/api/models?sort=downloads&direction=-1&limit={lim}"
        };
        return await FetchModelListAsync(url, ct);
    }

    private static async Task<List<HfRepoSearchItem>> FetchModelListAsync(string url, CancellationToken ct)
    {
        using var req = CreateAuthRequest(HttpMethod.Get, url);
        using var resp = await ApiHttp.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<HfRepoSearchItem>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            long dl = el.TryGetProperty("downloads", out var dlEl) && dlEl.TryGetInt64(out long d) ? d : 0;
            long likes = el.TryGetProperty("likes", out var lkEl) && lkEl.TryGetInt64(out long l) ? l : 0;
            string? pipe = el.TryGetProperty("pipeline_tag", out var pEl) ? pEl.GetString() : null;
            bool gated = el.TryGetProperty("gated", out var gEl) && (gEl.ValueKind == JsonValueKind.True || gEl.ValueKind == JsonValueKind.String);
            if (!string.IsNullOrEmpty(id))
                list.Add(new(id, dl, likes, pipe, gated));
        }
        return list;
    }

    public async Task<bool> HasRepoAccessAsync(string repoId, string remotePath, CancellationToken ct = default)
    {
        string url = $"https://huggingface.co/{repoId}/resolve/main/{remotePath}";
        using var req = CreateAuthRequest(HttpMethod.Head, url);
        using var resp = await ApiHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode is 401 or 403)
            return false;
        return resp.IsSuccessStatusCode || (int)resp.StatusCode is >= 300 and < 400;
    }

    public async Task<List<HfRepoFileItem>> ListModelFilesAsync(string repoId, CancellationToken ct = default)
    {
        string url = $"https://huggingface.co/api/models/{repoId}/tree/main?recursive=true";
        using var req = CreateAuthRequest(HttpMethod.Get, url);
        using var resp = await ApiHttp.SendAsync(req, ct);
        if ((int)resp.StatusCode is 401 or 403)
            throw new HfAccessRequiredException(repoId, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken()));
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<HfRepoFileItem>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("type", out var tEl) || tEl.GetString() != "file") continue;
            string path = el.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".pth", StringComparison.OrdinalIgnoreCase))
                continue;
            long size = el.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sz) ? sz : 0;
            string? oid = null;
            if (el.TryGetProperty("lfs", out var lfsEl) && lfsEl.ValueKind == JsonValueKind.Object &&
                lfsEl.TryGetProperty("oid", out var oidEl))
                oid = oidEl.GetString();
            list.Add(new(path, size, oid));
        }
        return list.OrderBy(f => f.Path).ToList();
    }

    public async Task InstallCatalogBundleAsync(
        CatalogModelSpec spec,
        Action<HfDownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        int totalFiles = spec.Files.Length;
        for (int i = 0; i < totalFiles; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = spec.Files[i];
            string destPath = ModelCatalog.ResolveLocalPath(f);
            await DownloadSingleFileAsync(
                spec.Name,
                f.RepoId,
                f.RemotePath,
                destPath,
                i + 1,
                totalFiles,
                onProgress,
                ct);
        }
    }

    public async Task DownloadSingleFileAsync(
        string displayName,
        string repoId,
        string remotePath,
        string destPath,
        int fileIndex = 1,
        int totalFiles = 1,
        Action<HfDownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        // Query HF metadata for exact size & LFS sha256
        string fileName = Path.GetFileName(remotePath);
        long expectedSize = 0;
        string? expectedSha256 = null;

        try
        {
            string dirPart = Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? "";
            string treeUrl = string.IsNullOrEmpty(dirPart)
                ? $"https://huggingface.co/api/models/{repoId}/tree/main"
                : $"https://huggingface.co/api/models/{repoId}/tree/main/{dirPart}";
            using var metaReq = CreateAuthRequest(HttpMethod.Get, treeUrl);
            using var metaResp = await ApiHttp.SendAsync(metaReq, ct);
            if (metaResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync(ct));
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("path", out var pEl) && pEl.GetString() == remotePath)
                    {
                        if (el.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sz)) expectedSize = sz;
                        if (el.TryGetProperty("lfs", out var lfsEl) && lfsEl.ValueKind == JsonValueKind.Object &&
                            lfsEl.TryGetProperty("oid", out var oidEl))
                            expectedSha256 = oidEl.GetString();
                        break;
                    }
                }
            }
        }
        catch { }

        if (File.Exists(destPath) && expectedSize > 0 && new FileInfo(destPath).Length == expectedSize)
        {
            onProgress?.Invoke(new(displayName, fileName, fileIndex, totalFiles, 100, expectedSize / 1048576.0, expectedSize / 1048576.0, 0, "Уже скачан (проверен)"));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string partPath = destPath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize > 0 && existingBytes > expectedSize)
        {
            File.Delete(partPath);
            existingBytes = 0;
        }

        string downloadUrl = $"https://huggingface.co/{repoId}/resolve/main/{remotePath}?download=true";
        using var req = CreateAuthRequest(HttpMethod.Get, downloadUrl);
        if (existingBytes > 0)
            req.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using var hdrCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hdrCts.CancelAfter(StallTimeout);
        HttpResponseMessage resp;
        try
        {
            resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, hdrCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Hugging Face не отвечает более {(int)StallTimeout.TotalSeconds} сек (проверь интернет/VPN или скачай файл вручную через браузер).");
        }

        long totalBytes = expectedSize;
        using (resp)
        {
            if ((int)resp.StatusCode is 401 or 403)
                throw new HfAccessRequiredException(repoId, (int)resp.StatusCode, !string.IsNullOrWhiteSpace(HubConfig.GetHuggingFaceToken()));
            resp.EnsureSuccessStatusCode();

            bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existingBytes > 0;
            totalBytes = resumed
                ? existingBytes + (resp.Content.Headers.ContentLength ?? 0)
                : (resp.Content.Headers.ContentLength ?? expectedSize);

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, true))
            {
                byte[] buf = new byte[1024 * 1024];
                long downloaded = resumed ? existingBytes : 0;
                long sessionDownloaded = 0;
                var sw = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(StallTimeout);
                    int read;
                    try
                    {
                        read = await remoteStream.ReadAsync(buf, readCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Закачка {fileName} зависла (нет данных более {(int)StallTimeout.TotalSeconds} сек). Прогресс сохранён в .part — можно докачать позже или скачать вручную.");
                    }
                    if (read == 0) break;
                    await fileStream.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    sessionDownloaded += read;

                    if (sw.Elapsed - lastReport >= TimeSpan.FromMilliseconds(350))
                    {
                        lastReport = sw.Elapsed;
                        double pct = totalBytes > 0 ? Math.Min(99.5, downloaded * 100.0 / totalBytes) : 0;
                        double speedMBs = sw.Elapsed.TotalSeconds > 0.1 ? (sessionDownloaded / 1048576.0) / sw.Elapsed.TotalSeconds : 0;
                        onProgress?.Invoke(new(displayName, fileName, fileIndex, totalFiles, pct, downloaded / 1048576.0, totalBytes / 1048576.0, speedMBs, "Скачивание..."));
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            onProgress?.Invoke(new(displayName, fileName, fileIndex, totalFiles, 99.7, totalBytes / 1048576.0, totalBytes / 1048576.0, 0, "Проверка SHA-256..."));
            string actual = await ComputeSha256Async(partPath, ct);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                throw new IOException($"Ошибка контрольной суммы SHA-256 для {fileName} (получено {actual[..12]}..., ожидалось {expectedSha256[..12]}...)");
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(partPath, destPath);
        onProgress?.Invoke(new(displayName, fileName, fileIndex, totalFiles, 100, totalBytes / 1048576.0, totalBytes / 1048576.0, 0, "Установлено и проверено!"));
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
