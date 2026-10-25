// 界面文案的**单一所有者**（RFC-42 §3.1）：文案在 assets/i18n/<lang>.json，代码只持有键。
// 改文案不重新编译；缺键**显示键名本身**（回落中/英会把漏翻伪装成正常，这是本片的设计意图）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PetDock.Core.Compositor;

namespace PetDock.Core.Ui;

internal static class I18n
{
    internal const string DefaultLanguage = "zh-CN";

    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    /// <summary>当前语言（加载失败时是 <see cref="DefaultLanguage"/>）。</summary>
    internal static string Language { get; private set; } = DefaultLanguage;

    /// <summary>已加载的键数（判据用它证明表真的读进来了，而不是读不到就静默空转）。</summary>
    internal static int KeyCount => _table.Count;

    /// <summary>缺键次数：判据断言「本趟一次都没缺」比断言某句文案长什么样更稳。</summary>
    internal static int MissingCount { get; private set; }

    /// <summary>
    /// 加载 <c>assets/i18n/&lt;language&gt;.json</c>。文件缺失或 JSON 坏 → 退回
    /// <see cref="DefaultLanguage"/> 并写一行 <c>i18n-load-failed</c>；两种语言都读不到时表为空，
    /// 于是每个键都显示键名本身（比显示空白更接近真相）。
    /// </summary>
    internal static void Load(string language, string? assetsRoot = null)
    {
        Language = DefaultLanguage;
        _table = new Dictionary<string, string>(StringComparer.Ordinal);
        _fallback = new Dictionary<string, string>(StringComparer.Ordinal);

        string? root = assetsRoot ?? ResolveAssetsRoot();
        if (root is null)
        {
            KernelLog.Write("i18n-load-failed reason=no-assets-root");
            return;
        }

        var loaded = TryRead(Path.Combine(root, "i18n"), language, out var table, out var reason);
        if (!loaded)
        {
            KernelLog.Write($"i18n-load-failed lang={language} reason={reason}");
        }
        else
        {
            _table = table!;
            Language = language;
        }

        if (!string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            if (TryRead(Path.Combine(root, "i18n"), DefaultLanguage, out var fallbackTable, out _))
            {
                _fallback = fallbackTable!;
            }
        }

        KernelLog.Write($"i18n-loaded lang={Language} keys={_table.Count} fallback={_fallback.Count}");
    }

    /// <summary>取一条文案。缺键返回**键名本身**并计一次（RFC-42 §3.2）。</summary>
    internal static string Get(string key)
    {
        if (_table.TryGetValue(key, out string? text)) return text;
        if (_fallback.TryGetValue(key, out string? fallback)) return fallback;
        MissingCount++;
        return key;
    }

    /// <summary>带 <c>{0}</c> 占位符的文案（RFC-42 只允许简单替换，不随数变化的句子）。</summary>
    internal static string Get(string key, object arg0) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), arg0);

    /// <summary>带两个占位符的文案（例：`{0} {1}`——异常类型 + 消息）。</summary>
    internal static string Get(string key, object arg0, object arg1) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), arg0, arg1);

    internal static bool Has(string key) => _table.ContainsKey(key) || _fallback.ContainsKey(key);

    /// <summary>判据用：把缺键计数归零，跑完再问「这趟缺了几个」。</summary>
    internal static void ResetMissingCount() => MissingCount = 0;

    /// <summary>
    /// 读一门语言：`i18n/&lt;lang&gt;.json`，外加 `i18n/&lt;lang&gt;/*.json` 的**分片**（按文件名字典序合并）。
    /// 分片是为了让多个人/多个子代理同时迁文案时**不撞同一个文件**：各写各的分片，键名各自带前缀。
    /// 同一个键出现在两处时**先读到的赢**并计一次冲突（静默覆盖会让两个人以为自己的文案生效了）。
    /// </summary>
    private static bool TryRead(string dir, string language, out Dictionary<string, string>? table, out string reason)
    {
        table = null;
        reason = "missing";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int conflicts = 0;
        foreach (string path in TableFiles(dir, language))
        {
            if (!TryReadFile(path, map, ref conflicts, out reason))
            {
                return false;
            }
        }

        if (map.Count == 0)
        {
            return false;
        }

        if (conflicts > 0)
        {
            KernelLog.Write($"i18n-duplicate-keys n={conflicts} first-wins");
        }

        table = map;
        reason = "ok";
        return true;
    }

    private static IEnumerable<string> TableFiles(string dir, string language)
    {
        string single = Path.Combine(dir, language + ".json");
        if (File.Exists(single))
        {
            yield return single;
        }

        string shardDir = Path.Combine(dir, language);
        if (!Directory.Exists(shardDir))
        {
            yield break;
        }

        string[] shards = Directory.GetFiles(shardDir, "*.json");
        Array.Sort(shards, StringComparer.Ordinal);
        foreach (string shard in shards)
        {
            yield return shard;
        }
    }

    private static bool TryReadFile(string path, Dictionary<string, string> map, ref int conflicts, out string reason)
    {
        reason = "ok";
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "not-an-object " + Path.GetFileName(path);
                return false;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    reason = "value-not-a-string key=" + property.Name;
                    return false;
                }

                if (property.Name.Contains(' ') || property.Name.Contains('/'))
                {
                    reason = "bad-key " + property.Name;
                    return false;
                }

                if (map.ContainsKey(property.Name))
                {
                    conflicts++;
                    continue;
                }

                map[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            return true;
        }
        catch (Exception error)
        {
            reason = Path.GetFileName(path) + " " + error.GetType().Name + ": " + error.Message;
            return false;
        }
    }

    /// <summary>从 exe 处向上找含 <c>assets/i18n</c> 的目录（开发树、便携目录、判据临时装配都能覆盖）。</summary>
    private static string? ResolveAssetsRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("PETDOCK_ASSETS");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(Path.Combine(overrideRoot, "i18n")))
        {
            return overrideRoot;
        }

        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && candidate is not null; depth++)
        {
            string assets = Path.Combine(candidate.FullName, "assets");
            if (Directory.Exists(Path.Combine(assets, "i18n"))) return assets;
            candidate = candidate.Parent;
        }

        return null;
    }
}
