// 授权决定的跨进程记忆（RFC-19 §2.1）。
//
// 落点：<data>/consent.json —— 与 pets.json（RFC-15）、settings.json（RFC-18）同级。
// 为什么落 JSON 而不是注册表：授权决定是「用户偏好」，原规格 17 §1 把它归在 UserSettings 形态；
// 注册表适合的是开机自启那种系统级开关（RFC-18 的裁决）。
//
// 写盘姿势照抄 UserSettingsStore.Save 与 PetDataStore.RegisterActive：先写 .tmp 再整文件替换
// （原规格 17 §3 的两阶段提交）。读到损坏的文件时回落成空表、不抛 —— 一份坏掉的授权记忆
// 不该让程序起不来（最坏后果是「重新问一遍用户」）。
//
// 只记**授予**，不记拒绝（RFC-19 §2.2 的裁决）：被拒项在弹窗里不可重新勾选（原规格 18 §3），
// 持久化拒绝 = 用户被永久锁在门外，且没有任何「清除」的出口。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>授权决定的落盘读写（原规格 17 §1 的 UserSettings 形态之一种）。</summary>
internal sealed class ConsentStore
{
    /// <summary>授权记忆文件名（与其它两份 JSON 同级）。</summary>
    public const string ConsentFile = "consent.json";

    /// <summary>schema 版本（原规格 17 §2：显式版本，只向前）。</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;

    public ConsentStore(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // 全名限定：类里有个叫 Path 的属性会盖住 System.IO.Path 这个类型名
        // （写成 Path.Combine 编译器会报「string 未包含 Combine 的定义」）。
        _path = System.IO.Path.Combine(root, ConsentFile);
    }

    /// <summary>授权记忆文件路径（自检与诊断用）。</summary>
    public string Path => _path;

    /// <summary>
    /// 读出「插件 id → 已授予的能力 ID 集」。
    /// 文件缺失或损坏 ⇒ 空表（不是异常）：下一次装载就重新问用户。
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return result;
            }

            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8));
            if (parsed is not JsonObject root)
            {
                return result;
            }

            if (root["plugins"] is not JsonObject plugins)
            {
                return result;
            }

            foreach (KeyValuePair<string, JsonNode?> entry in plugins)
            {
                if (entry.Value is not JsonArray array)
                {
                    continue;
                }

                var ids = new List<string>();
                foreach (JsonNode? item in array)
                {
                    string? id = (string?)item;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }

                if (ids.Count > 0)
                {
                    result[entry.Key] = ids;
                }
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"consent-store-unreadable {error.GetType().Name}: {error.Message}");
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// 写回「插件 id → 已授予的能力 ID 集」。整份覆盖写。
    /// 写失败抛给调用方（调用方决定要不要吞掉——授权本身已经生效，写盘只是记性）。
    /// </summary>
    public void Save(IReadOnlyDictionary<string, IReadOnlyList<string>> grants)
    {
        var plugins = new JsonObject();
        foreach (KeyValuePair<string, IReadOnlyList<string>> entry in grants)
        {
            var array = new JsonArray();
            foreach (string id in entry.Value)
            {
                // 用 JsonValue.Create 而不是 array.Add(id)：泛型 Add<T> 会把字符串包成
                // JsonValueCustomized<string>，写出时要求 JsonSerializerOptions 带 TypeInfoResolver，
                // 否则运行期抛 InvalidOperationException（已实测）。
                array.Add(JsonValue.Create(id)!);
            }

            plugins[entry.Key] = array;
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["plugins"] = plugins,
        };

        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 两阶段提交（原规格 17 §3）：先写临时文件，再整文件替换。
        string temp = _path + ".tmp";
        File.WriteAllText(
            temp,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
