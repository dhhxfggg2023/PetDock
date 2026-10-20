// 设置的落盘与读取（docs/17 §2 schema / §3 两阶段提交 / §4 审计字段）。
//
// 为什么单独一个文件而不是塞进 pets.json：pets.json 是「导入过什么」的清单，
// settings.json 是「用户偏好」。两种形态、两种生命周期，混在一份文件里，
// 导入一次就要重写一次设置（而且两种数据的保留期不同：宠物清单会增长，偏好不会）。
//
// 落盘姿势照抄 RFC-15 的 PetDataStore.RegisterActive：先写 .tmp 再整文件替换。
// 写到一半断电的话，旧文件还在，不会留下半个 JSON。
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>用户设置的读写（docs/17 §1 的 UserSettings 形态）。</summary>
internal sealed class UserSettingsStore
{
    /// <summary>设置文件名（与 pets.json 同级，都在数据区根下）。</summary>
    public const string SettingsFile = "settings.json";

    private readonly string _path;

    public UserSettingsStore(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // 全名：类里有个叫 Path 的属性，它会盖住 System.IO.Path 这个类型名
        // （写成 Path.Combine 的话编译器把 Path 当成那个 string 属性，于是报「string 未包含 Combine」）。
        _path = System.IO.Path.Combine(root, SettingsFile);
    }

    /// <summary>设置文件路径（自检与诊断用）。</summary>
    public string Path => _path;

    /// <summary>
    /// 读设置。文件缺失、损坏、或值越界，一律回落到默认值——
    /// 一份坏掉的用户偏好不该让程序起不来（docs/17 §2：宿主返回快照并且阻止加载是 ABI 层的做法，
    /// 用户偏好没有那种严格性）。
    /// </summary>
    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return UserSettings.Default;
            }

            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8));
            if (parsed is not JsonObject obj)
            {
                KernelLog.Write("settings-unreadable reason=not-an-object");
                return UserSettings.Default;
            }

            double period = UserSettings.DefaultIdleFrameBudgetMs;
            if (obj["idleFrameBudgetMs"] is not null)
            {
                try
                {
                    period = obj["idleFrameBudgetMs"]!.GetValue<double>();
                }
                catch (Exception error)
                {
                    KernelLog.Write($"settings-field-unreadable field=idleFrameBudgetMs {error.GetType().Name}");
                }
            }

            string language = (string?)obj["language"] ?? UserSettings.DefaultLanguage;
            return new UserSettings(period, language).Clamped();
        }
        catch (Exception error)
        {
            KernelLog.Write($"settings-unreadable {error.GetType().Name}: {error.Message}");
            return UserSettings.Default;
        }
    }

    /// <summary>
    /// 写设置。两阶段提交 + docs/17 §4 要求的 agent_id 与 timestamp。
    /// 写失败抛异常（调用方负责把失败说清楚，不能假装存上了）。
    /// </summary>
    public void Save(UserSettings settings)
    {
        UserSettings value = settings.Clamped();
        var doc = new JsonObject
        {
            ["schemaVersion"] = UserSettings.SchemaVersion,
            ["agent_id"] = "host",
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["idleFrameBudgetMs"] = value.IdleFrameBudgetMs,
            ["language"] = value.Language,
        };

        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = _path + ".tmp";
        File.WriteAllText(
            temp,
            doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}
