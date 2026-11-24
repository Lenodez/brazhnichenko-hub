using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LocalAIHub.Services;

public sealed class GenerationMetadata
{
    public string Kind { get; set; } = "photo"; // "photo" | "video"
    public string Prompt { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Size { get; set; } = "";
    public long Seed { get; set; } = -1;
    public int DurationSec { get; set; } = 0;
    public string? LoraHigh { get; set; }
    public double LoraHighStrength { get; set; } = 0.75;
    public string? LoraLow { get; set; }
    public double LoraLowStrength { get; set; } = 0.75;
    public string? RefImagePath { get; set; }
    public string? EndImagePath { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Reads and writes generation metadata sidecars (&lt;media&gt;.meta.json),
/// with best-effort fallback to ComfyUI PNG tEXt/iTXt prompt chunks.
/// Pure C# (no WPF imports) so LocalAIHub.Mcp compiles cleanly.
/// </summary>
public static class GenerationMetadataService
{
    private static readonly JsonSerializerOptions WriteJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxTextChunkBytes = 8 * 1024 * 1024;

    public static string GetSidecarPath(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
            return "";
        return mediaFilePath + ".meta.json";
    }

    public static void Save(string mediaFilePath, GenerationMetadata meta)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath) || meta is null)
            return;

        try
        {
            string sidecarPath = GetSidecarPath(mediaFilePath);
            string? dir = Path.GetDirectoryName(sidecarPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(meta, WriteJsonOpts);
            File.WriteAllText(sidecarPath, json, Encoding.UTF8);
        }
        catch
        {
            // Swallow IO errors cleanly so generation / UI stays resilient
        }
    }

    public static GenerationMetadata? TryRead(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
            return null;

        // 1. Prefer explicit sidecar (<media>.meta.json)
        try
        {
            string sidecarPath = GetSidecarPath(mediaFilePath);
            if (!string.IsNullOrEmpty(sidecarPath) && File.Exists(sidecarPath))
            {
                string json = File.ReadAllText(sidecarPath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var meta = JsonSerializer.Deserialize<GenerationMetadata>(json, ReadJsonOpts);
                    if (meta is not null)
                        return meta;
                }
            }
        }
        catch
        {
            // Fall through to PNG chunk fallback
        }

        // 2. Fallback: parse ComfyUI prompt chunk from PNG tEXt / iTXt
        if (mediaFilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(mediaFilePath))
        {
            try
            {
                return TryReadFromPng(mediaFilePath);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static void DeleteSidecarIfExists(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
            return;

        try
        {
            string sidecarPath = GetSidecarPath(mediaFilePath);
            if (!string.IsNullOrEmpty(sidecarPath) && File.Exists(sidecarPath))
                File.Delete(sidecarPath);
        }
        catch
        {
            // Swallow IO errors cleanly
        }
    }

    private static GenerationMetadata? TryReadFromPng(string pngPath)
    {
        using var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < PngSignature.Length + 12)
            return null;

        Span<byte> sig = stackalloc byte[8];
        if (ReadExact(fs, sig) != 8 || !sig.SequenceEqual(PngSignature))
            return null;

        Span<byte> header = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[8];
        int ihdrWidth = 0;
        int ihdrHeight = 0;
        string? comfyPromptJson = null;

        while (fs.Position + 12 <= fs.Length)
        {
            if (ReadExact(fs, header) != 8)
                break;

            uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string chunkType = Encoding.ASCII.GetString(header[4..8]);

            long remaining = fs.Length - fs.Position;
            if ((long)chunkLen + 4 > remaining)
                break;

            if (chunkType == "IHDR" && chunkLen >= 8)
            {
                if (ReadExact(fs, ihdr) == 8)
                {
                    ihdrWidth = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr[..4]);
                    ihdrHeight = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..8]);
                }
                fs.Seek((long)chunkLen - 8 + 4, SeekOrigin.Current);
                continue;
            }

            if ((chunkType == "tEXt" || chunkType == "iTXt") && chunkLen > 0 && chunkLen <= MaxTextChunkBytes)
            {
                byte[] data = new byte[chunkLen];
                if (ReadExact(fs, data) != (int)chunkLen)
                    break;

                // Skip 4-byte CRC
                fs.Seek(4, SeekOrigin.Current);

                if (chunkType == "tEXt")
                {
                    if (TryExtractTextChunk(data, out string keyword, out string text) &&
                        string.Equals(keyword, "prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        comfyPromptJson = text;
                        break;
                    }
                }
                else
                {
                    if (TryExtractITxtChunk(data, out string keyword, out string text) &&
                        string.Equals(keyword, "prompt", StringComparison.OrdinalIgnoreCase))
                    {
                        comfyPromptJson = text;
                        break;
                    }
                }
                continue;
            }

            if (chunkType == "IEND")
                break;

            // Skip data + 4-byte CRC
            fs.Seek((long)chunkLen + 4, SeekOrigin.Current);
        }

        if (string.IsNullOrWhiteSpace(comfyPromptJson))
            return null;

        return ParseComfyPromptJson(comfyPromptJson, pngPath, ihdrWidth, ihdrHeight);
    }

    private static bool TryExtractTextChunk(byte[] data, out string keyword, out string text)
    {
        keyword = "";
        text = "";
        int nul = Array.IndexOf(data, (byte)0);
        if (nul <= 0 || nul >= data.Length - 1)
            return false;

        keyword = Encoding.Latin1.GetString(data, 0, nul);
        // ComfyUI writes UTF-8 JSON inside tEXt; fall back to Latin-1 if invalid UTF-8
        try
        {
            text = new UTF8Encoding(false, true).GetString(data, nul + 1, data.Length - nul - 1);
        }
        catch
        {
            text = Encoding.Latin1.GetString(data, nul + 1, data.Length - nul - 1);
        }
        return true;
    }

    private static bool TryExtractITxtChunk(byte[] data, out string keyword, out string text)
    {
        keyword = "";
        text = "";
        int nul1 = Array.IndexOf(data, (byte)0);
        if (nul1 <= 0 || nul1 + 3 >= data.Length)
            return false;

        keyword = Encoding.Latin1.GetString(data, 0, nul1);
        byte compressionFlag = data[nul1 + 1];
        if (compressionFlag != 0)
            return false; // Skip compressed iTXt

        // Skip compression method (1 byte), language tag (nul-terminated), translated keyword (nul-terminated)
        int nul2 = Array.IndexOf(data, (byte)0, nul1 + 3);
        if (nul2 < 0 || nul2 + 1 >= data.Length)
            return false;

        int nul3 = Array.IndexOf(data, (byte)0, nul2 + 1);
        if (nul3 < 0 || nul3 + 1 >= data.Length)
            return false;

        text = Encoding.UTF8.GetString(data, nul3 + 1, data.Length - nul3 - 1);
        return true;
    }

    private static GenerationMetadata? ParseComfyPromptJson(string promptJson, string pngPath, int ihdrWidth, int ihdrHeight)
    {
        using var doc = JsonDocument.Parse(promptJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var nodes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
                nodes[prop.Name] = prop.Value;
        }

        if (nodes.Count == 0)
            return null;

        // Collect positive node IDs referenced by samplers / guiders
        var positiveNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var negativeNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long seed = -1;
        string engine = "";
        int width = 0;
        int height = 0;
        string? loraName = null;
        double loraStrength = 0.75;

        foreach (var (_, node) in nodes)
        {
            string classType = TryGetString(node, "class_type") ?? "";
            if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
                continue;

            // Track positive / negative conditioning links
            if (TryGetLinkNodeId(inputs, "positive", out string posId))
                positiveNodeIds.Add(posId);
            if (TryGetLinkNodeId(inputs, "negative", out string negId))
                negativeNodeIds.Add(negId);

            // Extract seed
            if (seed < 0)
            {
                if (TryGetInt64(inputs, "seed", out long s) && s >= 0)
                    seed = s;
                else if (TryGetInt64(inputs, "noise_seed", out long ns) && ns >= 0)
                    seed = ns;
            }

            // Extract engine from UNETLoader / UnetLoaderGGUF
            if (string.IsNullOrEmpty(engine) &&
                (classType.Contains("UNETLoader", StringComparison.OrdinalIgnoreCase) ||
                 classType.Contains("UnetLoader", StringComparison.OrdinalIgnoreCase)))
            {
                string unet = TryGetString(inputs, "unet_name") ?? "";
                engine = InferEngineFromUnet(unet);
            }

            // Extract latent dimensions
            if (width <= 0 && height <= 0 &&
                classType.Contains("LatentImage", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetInt32(inputs, "width", out int w) && TryGetInt32(inputs, "height", out int h) && w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                }
            }

            // Extract extra LoRA
            if (loraName is null &&
                classType.Contains("LoraLoader", StringComparison.OrdinalIgnoreCase))
            {
                string? ln = TryGetString(inputs, "lora_name");
                if (!string.IsNullOrWhiteSpace(ln) &&
                    !ln.Contains("lightx2v_4steps_lora", StringComparison.OrdinalIgnoreCase) &&
                    !ln.Contains("svi_v2_pro", StringComparison.OrdinalIgnoreCase))
                {
                    loraName = ln;
                    if (TryGetDouble(inputs, "strength_model", out double str))
                        loraStrength = str;
                }
            }
        }

        // Resolve positive prompt text
        string promptText = ExtractBestPromptText(nodes, positiveNodeIds, negativeNodeIds);

        if (string.IsNullOrWhiteSpace(promptText) && seed < 0)
            return null;

        if (width <= 0 || height <= 0)
        {
            width = ihdrWidth;
            height = ihdrHeight;
        }

        DateTime createdUtc;
        try { createdUtc = File.GetLastWriteTimeUtc(pngPath); }
        catch { createdUtc = DateTime.UtcNow; }

        return new GenerationMetadata
        {
            Kind = "photo",
            Prompt = promptText,
            Engine = engine,
            Size = width > 0 && height > 0 ? $"{width}x{height}" : "",
            Seed = seed,
            DurationSec = 0,
            LoraHigh = loraName,
            LoraHighStrength = loraStrength,
            CreatedUtc = createdUtc
        };
    }

    private static string ExtractBestPromptText(
        Dictionary<string, JsonElement> nodes,
        HashSet<string> positiveNodeIds,
        HashSet<string> negativeNodeIds)
    {
        // 1. Direct positive-linked nodes (follow one hop through ReferenceMultiLatent if needed)
        var expandedPosIds = new HashSet<string>(positiveNodeIds, StringComparer.OrdinalIgnoreCase);
        foreach (string id in positiveNodeIds)
        {
            if (nodes.TryGetValue(id, out var linked) &&
                linked.TryGetProperty("inputs", out var linkedInputs) &&
                linkedInputs.ValueKind == JsonValueKind.Object &&
                TryGetLinkNodeId(linkedInputs, "positive", out string upstreamId))
            {
                expandedPosIds.Add(upstreamId);
            }
        }

        foreach (string id in expandedPosIds)
        {
            if (!nodes.TryGetValue(id, out var node)) continue;
            string? text = TryExtractPromptFromNode(node);
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        // 2. Any TextEncode / CLIPTextEncode node not explicitly marked negative
        string best = "";
        foreach (var (id, node) in nodes)
        {
            if (negativeNodeIds.Contains(id)) continue;
            string classType = TryGetString(node, "class_type") ?? "";
            if (!classType.Contains("TextEncode", StringComparison.OrdinalIgnoreCase) &&
                !classType.Contains("Prompt", StringComparison.OrdinalIgnoreCase))
                continue;

            string? text = TryExtractPromptFromNode(node);
            if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > best.Length)
                best = text.Trim();
        }

        return best;
    }

    private static string? TryExtractPromptFromNode(JsonElement node)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
            return null;

        string? prompt = TryGetString(inputs, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            return prompt;

        string? text = TryGetString(inputs, "text");
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        return null;
    }

    private static string InferEngineFromUnet(string unet)
    {
        if (string.IsNullOrWhiteSpace(unet)) return "";
        string lower = unet.ToLowerInvariant();
        if (lower.Contains("flux3") || lower.Contains("flux")) return "flux3";
        if (lower.Contains("qwen_image") || lower.Contains("qwen")) return "qwen21";
        if (lower.Contains("z_image") || lower.Contains("zimage")) return "zimage";
        if (lower.Contains("ltx")) return "ltx25";
        if (lower.Contains("wan2.2") || lower.Contains("wan"))
        {
            if (lower.Contains("fp8")) return "wan14b_fp8";
            if (lower.Contains("q6")) return "wan14b_q6";
            return "wan14b";
        }
        return "";
    }

    private static string? TryGetString(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static bool TryGetInt64(JsonElement obj, string propName, out long value)
    {
        value = 0;
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt64(out value);
        return false;
    }

    private static bool TryGetInt32(JsonElement obj, string propName, out int value)
    {
        value = 0;
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out value);
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string propName, out double value)
    {
        value = 0;
        if (obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out value);
        return false;
    }

    private static bool TryGetLinkNodeId(JsonElement inputs, string propName, out string nodeId)
    {
        nodeId = "";
        if (!inputs.TryGetProperty(propName, out var el) || el.ValueKind != JsonValueKind.Array)
            return false;

        using var enumerator = el.EnumerateArray();
        if (!enumerator.MoveNext())
            return false;

        if (enumerator.Current.ValueKind == JsonValueKind.String)
        {
            string? id = enumerator.Current.GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                nodeId = id;
                return true;
            }
        }
        return false;
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }
}
