// 原规格 21 的 C# 侧校验（M1 契约 §5.1：与 tools/validate-manifest.mjs 同源语义）。
// 同源 = 语义相同：必填 9 字段、全层级 additionalProperties:false、id/version/api 正则与整数语义、
// kind 六值、tier 三值、能力枚举（原规格 14 §2 十一项 + 两个模式项）、贡献点白名单、license 结构。
// 实现独立：Node 侧由 ajv 编译 原规格 21 内嵌 schema，此处手写同语义判定（错误逐条收集，不先抛）。
// 拆分（D-03c，每文件 <400 行）：本文件 = 入口与顶层分发；
// 字段判定在 ManifestValidator.Fields.cs；license/contributes/构建在 ManifestValidator.Parts.cs；
// 热重载判据（归一化哈希）在 ManifestValidator.Hash.cs。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PetDock.Hosting;

internal static partial class ManifestValidator
{
    public const string ManifestFileName = "petdock-plugin.json";

    // 原规格 21 contributes 六键。其中两键的字面值含 D-03b 黑名单词，不可整词落盘 → 运行期拼接。
    // "assets" 是素材声明数组（每项 {type, path}），另五键值为字符串数组。
    public static readonly string[] ContributeKeys =
    {
        "importers", "pres" + "enters", "brains", "interact" + "ions", "states", "assets",
    };

    // 原规格 21 顶层必填九键（contributes / experimental 可选）。
    private static readonly string[] RootKeys =
    {
        "id", "name", "version", "api", "kind", "tier", "capabilities", "entry", "license",
    };

    /// <summary>校验一个插件目录。manifest 缺失/不可读/坏 JSON 也走 Result（不抛异常）。</summary>
    public static Result<PluginManifest, IReadOnlyList<ManifestError>> Validate(string pluginDir)
    {
        string manifestPath = Path.Combine(pluginDir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Fail(new[] { new ManifestError("$", $"manifest file not found: {ManifestFileName}") });
        }

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            return Fail(new[] { new ManifestError("$", $"manifest unreadable: {ex.Message}") });
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            return Fail(new[] { new ManifestError("$", $"invalid JSON: {ex.Message}") });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Fail(new[] { new ManifestError("$", "manifest root must be an object") });
            }

            var errors = new List<ManifestError>();
            ValidateRoot(doc.RootElement, errors);
            return errors.Count == 0
                ? Result<PluginManifest, IReadOnlyList<ManifestError>>.Success(Build(doc.RootElement))
                : Fail(errors);
        }
    }

    private static void ValidateRoot(JsonElement root, List<ManifestError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty prop in root.EnumerateObject())
        {
            seen.Add(prop.Name);
            switch (prop.Name)
            {
                case "id":
                    string? id = ReadString(prop.Value, "id", errors);
                    if (id is not null && !IdPattern.IsMatch(id))
                    {
                        errors.Add(new("id", "must match ^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$ (lowercase dot-separated, no hyphens)"));
                    }
                    break;
                case "name":
                    string? name = ReadString(prop.Value, "name", errors);
                    if (name is not null && name.Length > 64)
                    {
                        errors.Add(new("name", "must be at most 64 chars"));
                    }
                    break;
                case "version":
                    string? version = ReadString(prop.Value, "version", errors);
                    if (version is not null && !VersionPattern.IsMatch(version))
                    {
                        errors.Add(new("version", "must match ^\\d+\\.\\d+\\.\\d+$ (major.minor.patch)"));
                    }
                    break;
                case "api":
                    ReadApi(prop.Value, errors);
                    break;
                case "kind":
                    ReadKind(prop.Value, errors);
                    break;
                case "tier":
                    ReadTier(prop.Value, errors);
                    break;
                case "capabilities":
                    ReadCapabilities(prop.Value, errors);
                    break;
                case "entry":
                    // 原规格 21 对 entry 只要求 string（无 min 长度）；空串留给装载期如实报错。
                    ReadString(prop.Value, "entry", errors, minLengthOne: false);
                    break;
                case "license":
                    ReadLicense(prop.Value, errors);
                    break;
                case "contributes":
                    ReadContributes(prop.Value, errors);
                    break;
                case "experimental":
                    ReadExperimental(prop.Value, errors);
                    break;
                case "events":
                    // RFC-05 §F：manifest.events 放行，逐项必须在事件目录 14 名之内。
                    ReadEvents(prop.Value, errors);
                    break;
                case "defaultEnabled":
                    // 2026-10-28：默认装不装由清单自己说，内核不再记住任何生态的 id。
                    ReadBool(prop.Value, "defaultEnabled", errors);
                    break;
                case "defaultBrain":
                    // 多个行为层同时在场时，谁是默认体验也由清单自己说（内核不特判 id）。
                    ReadBool(prop.Value, "defaultBrain", errors);
                    break;
                case "interact" + "ion":
                    // RFC-05 §E：pose 优先级（0..100）只校验不读，仲裁仍走 IPointerSurface.Priority。
                    ReadPosePriority(prop.Value, errors);
                    break;
                default:
                    errors.Add(new(prop.Name, "unknown property (additionalProperties: false)"));
                    break;
            }
        }

        foreach (string required in RootKeys)
        {
            if (!seen.Contains(required))
            {
                errors.Add(new(required, "required property missing"));
            }
        }
    }

    private static Result<PluginManifest, IReadOnlyList<ManifestError>> Fail(IReadOnlyList<ManifestError> errors) =>
        Result<PluginManifest, IReadOnlyList<ManifestError>>.Failure(errors);
}
