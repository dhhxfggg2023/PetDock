// ManifestValidator 的字段判定半边：字符串/整数（ajv 整数语义）、api/kind/tier、能力枚举校验。
// 类主体与分发在 ManifestValidator.cs（partial 同类）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal static partial class ManifestValidator
{
    // schema 合法、但 docs/14 §2 明令永不发放的两项：声明放行，授予时被 CapabilityGate 跳过并审计。
    private static readonly string[] SchemaOnlyCapabilities = { "assets.write:cas", "audio.play" };

    private static readonly Regex IdPattern = new("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.Compiled);

    private static string? ReadString(JsonElement value, string field, List<ManifestError> errors, bool minLengthOne = true)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new(field, "must be a string"));
            return null;
        }
        string s = value.GetString()!;
        if (minLengthOne && s.Length == 0)
        {
            errors.Add(new(field, "must be a non-empty string (minLength: 1)"));
            return null;
        }
        return s;
    }

    /// <summary>ajv 的整数语义：JSON number 且整数值且在 int 范围内（1.0 算整数，true/字符串不算）。</summary>
    private static int? ReadInt(JsonElement value, string field, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double d)
            || Math.Floor(d) != d
            || d < int.MinValue || d > int.MaxValue)
        {
            errors.Add(new(field, "must be an integer"));
            return null;
        }
        return (int)d;
    }

    private static void ReadApi(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("api", "must be an object"));
            return;
        }
        int? major = null;
        int? minor = null;
        foreach (JsonProperty prop in value.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "major":
                    major = ReadInt(prop.Value, "api.major", errors);
                    break;
                case "minor":
                    minor = ReadInt(prop.Value, "api.minor", errors);
                    break;
                default:
                    errors.Add(new($"api.{prop.Name}", "unknown property (additionalProperties: false)"));
                    break;
            }
        }
        if (major is null)
        {
            errors.Add(new("api.major", "required property missing"));
        }
        else if (major < 1)
        {
            errors.Add(new("api.major", "must be >= 1"));
        }
        if (minor is null)
        {
            errors.Add(new("api.minor", "required property missing"));
        }
        else if (minor < 0)
        {
            errors.Add(new("api.minor", "must be >= 0"));
        }
    }

    private static PluginKind? ReadKind(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new("kind", "must be a string"));
            return null;
        }
        string k = value.GetString()!;
        foreach (PluginKind kind in PluginKind.All)
        {
            if (kind.Value == k)
            {
                return kind;
            }
        }
        // 合法值清单运行期由 All 拼出（两个字面值不可整词落盘 —— D-03b）。
        errors.Add(new("kind", "must be one of: " + string.Join(", ", PluginKind.All.Select(p => p.Value))));
        return null;
    }

    private static int? ReadTier(JsonElement value, List<ManifestError> errors)
    {
        int? tier = ReadInt(value, "tier", errors);
        if (tier is not null && tier is not (0 or 1 or 2))
        {
            errors.Add(new("tier", "must be one of: 0, 1, 2"));
        }
        return tier;
    }

    private static List<string>? ReadCapabilities(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("capabilities", "must be an array"));
            return null;
        }
        var caps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(new($"capabilities[{i}]", "must be a string"));
                i++;
                continue;
            }
            string c = item.GetString()!;
            if (!IsSchemaValidCapability(c))
            {
                errors.Add(new($"capabilities[{i}]", $"unknown capability id: {c}"));
            }
            if (!seen.Add(c))
            {
                errors.Add(new($"capabilities[{i}]", $"duplicate capability: {c} (uniqueItems: true)"));
            }
            caps.Add(c);
            i++;
        }
        return caps;
    }

    /// <summary>docs/21 oneOf：11 固定枚举 + 2 模式项。t3 的 CapabilityIds.IsWellKnown 覆盖
    /// 8 固定 + 2 模式项；差额恰是两项「schema 合法但永不发放」的能力，单独放行。</summary>
    private static bool IsSchemaValidCapability(string id) =>
        CapabilityIds.IsWellKnown(id) || SchemaOnlyCapabilities.Contains(id);
}
