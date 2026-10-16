// meta.json 的读写与「已认证 GPU」查询（docs/41 D-12c/D-12d）。
// meta 形状（t1 契约 §6.3）：{ gpu, driver, generatedAt, sourceAsset, sha256, certifiedGpus[] }。
// certifiedGpus 是本机在基准生成时追加进去的已认证型号列表（D-12d「记录该型号为已认证」）。
using System.Text.Json;

namespace PetDock.Golden.Tests;

internal static class BaselineMeta
{
    /// <summary>当前 GPU 型号是否已登记在该 clip 的 meta.json 里（D-12d 的已认证判定）。</summary>
    public static bool IsGpuCertified(string metaPath, string gpuModel)
    {
        if (string.IsNullOrWhiteSpace(gpuModel) || !File.Exists(metaPath))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (!doc.RootElement.TryGetProperty("certifiedGpus", out JsonElement list))
            {
                return false;
            }

            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && string.Equals(entry.GetString(), gpuModel, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // meta 坏了按「未认证」处理：不阻断（GPU_DRIFT 路径），让真人看到差异图去修。
        }

        return false;
    }

    /// <summary>写 meta.json：登记 GPU 型号 + 驱动版本（D-12c），并把当前型号加进已认证列表（D-12d）。</summary>
    public static void Write(string metaPath, string sourceAsset, string sha256)
    {
        string gpu = GpuProbe.GpuModel();
        string driver = GpuProbe.DriverVersion();
        var certified = new List<string>();
        if (!string.IsNullOrWhiteSpace(gpu))
        {
            certified.Add(gpu);
        }

        var payload = new
        {
            gpu = string.IsNullOrWhiteSpace(gpu) ? "unavailable" : gpu,
            driver = string.IsNullOrWhiteSpace(driver) ? "unavailable" : driver,
            generatedAt = DateTime.UtcNow.ToString("o"),
            sourceAsset,
            sha256,
            certifiedGpus = certified,
        };

        File.WriteAllText(metaPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>读 meta.json 的 sha256 字段（无/坏返回 null，供完整性断言判缺）。</summary>
    public static string? Sha256Of(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("sha256", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读 meta.json 的 sourceAsset 字段（无/坏返回 null）。</summary>
    public static string? SourceAssetOf(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            return doc.RootElement.TryGetProperty("sourceAsset", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
