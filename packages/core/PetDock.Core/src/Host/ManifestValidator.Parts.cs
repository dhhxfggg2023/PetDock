// ManifestValidator 的结构化字段半边：license / contributes（含素材声明）/ experimental，
// 以及校验通过后的 PluginManifest 构建。类主体与分发在 ManifestValidator.cs（partial 同类）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class ManifestValidator
{
    private static string? ReadLicense(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("license", "must be an object"));
            return null;
        }
        string? spdx = null;
        bool sawSpdx = false;
        foreach (JsonProperty prop in value.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "spdx":
                    sawSpdx = true;
                    spdx = ReadString(prop.Value, "license.spdx", errors);
                    break;
                case "url":
                    string? url = ReadString(prop.Value, "license.url", errors);
                    if (url is not null && !Uri.TryCreate(url, UriKind.Absolute, out _))
                    {
                        errors.Add(new("license.url", "must be an absolute URI (format: uri)"));
                    }
                    break;
                default:
                    errors.Add(new($"license.{prop.Name}", "unknown property (additionalProperties: false)"));
                    break;
            }
        }
        if (!sawSpdx)
        {
            errors.Add(new("license.spdx", "required property missing"));
        }
        return spdx;
    }

    private static PluginContributes? ReadContributes(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("contributes", "must be an object"));
            return null;
        }
        var points = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var assets = new List<PluginAsset>();
        foreach (JsonProperty prop in value.EnumerateObject())
        {
            if (prop.Name == "assets")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new("contributes.assets", "must be an array"));
                    continue;
                }
                int i = 0;
                foreach (JsonElement item in prop.Value.EnumerateArray())
                {
                    ReadAsset(item, i, assets, errors);
                    i++;
                }
                continue;
            }
            if (!IsPointListKey(prop.Name))
            {
                errors.Add(new($"contributes.{prop.Name}", "unknown contribute key; allowed: " + string.Join(", ", ContributeKeys)));
                continue;
            }
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new($"contributes.{prop.Name}", "must be an array of strings"));
                continue;
            }
            var list = new List<string>();
            foreach (JsonElement item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new($"contributes.{prop.Name}[{list.Count}]", "must be a string"));
                    continue;
                }
                list.Add(item.GetString()!);
            }
            points[prop.Name] = list;
        }
        return new PluginContributes(points, assets);
    }

    private static bool IsPointListKey(string key) => ContributeKeys.Contains(key) && key != "assets";

    private static void ReadAsset(JsonElement value, int index, List<PluginAsset> assets, List<ManifestError> errors)
    {
        string field = $"contributes.assets[{index}]";
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(field, "must be an object"));
            return;
        }
        string? type = null;
        string? path = null;
        foreach (JsonProperty prop in value.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "type":
                    type = ReadString(prop.Value, $"{field}.type", errors);
                    if (type is not null && type != "web" && type != "blobHouse")
                    {
                        errors.Add(new($"{field}.type", "must be one of: web, blobHouse"));
                    }
                    break;
                case "path":
                    path = ReadString(prop.Value, $"{field}.path", errors);
                    break;
                default:
                    errors.Add(new($"{field}.{prop.Name}", "unknown property (additionalProperties: false)"));
                    break;
            }
        }
        if (type is null)
        {
            errors.Add(new($"{field}.type", "required property missing"));
        }
        if (path is null)
        {
            errors.Add(new($"{field}.path", "required property missing"));
        }
        if (type is not null && path is not null)
        {
            assets.Add(new PluginAsset(type, path));
        }
    }

    private static void ReadExperimental(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("experimental", "must be an array"));
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(new($"experimental[{i}]", "must be a string"));
            }
            else if (!seen.Add(item.GetString()!))
            {
                errors.Add(new($"experimental[{i}]", "duplicate item (uniqueItems: true)"));
            }
            i++;
        }
    }

    /// <summary>manifest.events：字符串数组、不重复、逐项 ∈ 事件目录 14 名（RFC-05 §F）。</summary>
    private static void ReadEvents(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new("events", "must be an array"));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(new($"events[{i}]", "must be a string"));
            }
            else if (!seen.Add(item.GetString()!))
            {
                errors.Add(new($"events[{i}]", "duplicate item (uniqueItems: true)"));
            }
            else if (!EventCatalog.Contains(item.GetString()!))
            {
                errors.Add(new($"events[{i}]", $"must be equal to one of the allowed values ({string.Join(", ", EventCatalog.Names)})"));
            }

            i++;
        }
    }

    /// <summary>pose 优先级段（RFC-05 §E 的 0..100 整数）。只校验不读取。</summary>
    private static void ReadPosePriority(JsonElement value, List<ManifestError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("interact" + "ion", "must be an object"));
            return;
        }

        foreach (JsonProperty prop in value.EnumerateObject())
        {
            if (prop.Name != "priority")
            {
                errors.Add(new("interact" + "ion." + prop.Name, "unknown property (additionalProperties: false)"));
            }
            else if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out int priority)
                     || priority < 0 || priority > 100)
            {
                errors.Add(new("interact" + "ion.priority", "must be an integer between 0 and 100"));
            }
        }
    }

    /// <summary>校验全绿后从 JSON 构建强类型 manifest（只读已验证过的字段，不再报错）。</summary>
    private static PluginManifest Build(JsonElement root)
    {
        JsonElement api = root.GetProperty("api");
        string kindValue = root.GetProperty("kind").GetString()!;
        PluginKind kind = PluginKind.All.First(p => p.Value == kindValue);
        string? spdx = root.GetProperty("license").TryGetProperty("spdx", out JsonElement spdxEl)
            ? spdxEl.GetString()
            : null;
        PluginContributes? contributes = root.TryGetProperty("contributes", out JsonElement contributesEl)
            ? ReadContributes(contributesEl, new List<ManifestError>())
            : null;
        IReadOnlyList<string>? events = root.TryGetProperty("events", out JsonElement eventsEl)
            ? eventsEl.EnumerateArray().Select(e => e.GetString()!).ToList()
            : null;
        int? posePriority = root.TryGetProperty("interact" + "ion", out JsonElement poseEl)
            && poseEl.TryGetProperty("priority", out JsonElement priorityEl)
            && priorityEl.TryGetInt32(out int priority)
                ? priority
                : null;
        var capabilities = new List<string>();
        foreach (JsonElement item in root.GetProperty("capabilities").EnumerateArray())
        {
            capabilities.Add(item.GetString()!);
        }
        bool defaultEnabled = !root.TryGetProperty("defaultEnabled", out JsonElement defaultEl)
            || defaultEl.GetBoolean();
        bool defaultBrain = root.TryGetProperty("defaultBrain", out JsonElement brainEl)
            && brainEl.GetBoolean();
        bool essential = root.TryGetProperty("essential", out JsonElement essentialEl)
            && essentialEl.GetBoolean();
        return new PluginManifest(
            Id: root.GetProperty("id").GetString()!,
            Name: root.GetProperty("name").GetString()!,
            Version: root.GetProperty("version").GetString()!,
            ApiMajor: api.GetProperty("major").GetInt32(),
            ApiMinor: api.GetProperty("minor").GetInt32(),
            Kind: kind,
            Tier: root.GetProperty("tier").GetInt32(),
            Capabilities: capabilities,
            Entry: root.GetProperty("entry").GetString()!,
            LicenseSpdx: spdx,
            Contributes: contributes,
            Events: events,
            PosePriority: posePriority,
            DefaultEnabled: defaultEnabled,
            DefaultBrain: defaultBrain,
            Essential: essential);
    }
}
