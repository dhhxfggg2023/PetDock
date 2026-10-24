// 设置的落盘与读取（docs/17 §2 schema / §3 两阶段提交 / §4 审计字段）。
//
// 为什么单独一个文件而不是塞进 pets.json：pets.json 是「导入过什么」的清单，
// settings.json 是「用户偏好」。两种形态、两种生命周期，混在一份文件里，
// 导入一次就要重写一次设置（而且两种数据的保留期不同：宠物清单会增长，偏好不会）。
//
// 落盘姿势照抄 RFC-15 的 PetDataStore.RegisterActive：先写 .tmp 再整文件替换。
// 写到一半断电的话，旧文件还在，不会留下半个 JSON。
using System;
using System.Drawing;
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

            // 引导的一次性判定（RFC-16 §3.1）：可空字段，旧文件缺它就是「没做过」，
            // 不需要迁移。值坏掉（不是数字 / 是负数）当没做过 —— 一次引导不该因为
            // 一个手滑改坏的文件就把用户卡在引导里出不来。
            int? onboarding = null;
            if (obj["onboardingCompletedVersion"] is JsonNode marker)
            {
                try
                {
                    int version = marker.GetValue<int>();
                    if (version > 0)
                    {
                        onboarding = version;
                    }
                    else
                    {
                        KernelLog.Write("settings-field-unreadable field=onboardingCompletedVersion reason=not-positive");
                    }
                }
                catch (Exception error)
                {
                    KernelLog.Write($"settings-field-unreadable field=onboardingCompletedVersion {error.GetType().Name}");
                }
            }

            // 窗口位置（RFC-22 §3.4）。缺键、不是对象、坐标不是整数，一律当「没存过」——
            // 位置坏掉时正确的行为是回到默认位，而不是带着一个荒谬坐标启动（宠物会跑到屏幕外）。
            Point? petPosition = null;
            if (obj["petPosition"] is JsonObject position)
            {
                try
                {
                    petPosition = new Point(
                        position["x"]!.GetValue<int>(),
                        position["y"]!.GetValue<int>());
                }
                catch (Exception error)
                {
                    KernelLog.Write($"settings-field-unreadable field=petPosition {error.GetType().Name}");
                }
            }
            else if (obj["petPosition"] is JsonNode notObject)
            {
                KernelLog.Write($"settings-field-unreadable field=petPosition reason=not-an-object type={notObject.GetType().Name}");
            }

            // 逐宠物位置（RFC-37 §7 第 4 条）：键是清单 id。缺键 / 不是对象 ⇒ 空表（「一只都没存过」）；
            // 单条坏掉只跳过那一条 —— 一个手滑改坏的坐标不该把别的宠物的位置一起丢掉。
            IReadOnlyDictionary<string, Point>? petPositions = null;
            if (obj["petPositions"] is JsonObject positions)
            {
                var table = new Dictionary<string, Point>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonNode?> pair in positions)
                {
                    if (pair.Key.Length == 0 || pair.Value is not JsonObject entry)
                    {
                        KernelLog.Write($"settings-field-unreadable field=petPositions key={pair.Key} reason=not-an-object");
                        continue;
                    }

                    try
                    {
                        table[pair.Key] = new Point(
                            entry["x"]!.GetValue<int>(),
                            entry["y"]!.GetValue<int>());
                    }
                    catch (Exception error)
                    {
                        KernelLog.Write($"settings-field-unreadable field=petPositions key={pair.Key} {error.GetType().Name}");
                    }
                }

                petPositions = table;
            }
            else if (obj["petPositions"] is JsonNode notObject)
            {
                KernelLog.Write($"settings-field-unreadable field=petPositions reason=not-an-object type={notObject.GetType().Name}");
            }

            return new UserSettings(period, language, onboarding, petPosition, petPositions).Clamped();
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
    ///
    /// 整份重写（RFC-16 §3.2 的纪律）：调用方必须**先读出再改一个字段**，
    /// 不能凭记忆造一份 UserSettings 写回来 —— 那会把别的字段抹掉
    /// （典型后果：引导写完一次性标记，用户随后在设置页点一次「应用」，标记被抹，引导又弹一次）。
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

        // 引导的一次性判定（RFC-16 §3.1）。没做过就**不写这个键**，
        // 而不是写 null —— 「键不存在」与「键存在但为 null」在演化上是两件事，
        // 前者明确是「这一版还没有这个概念」。
        if (value.OnboardingCompletedVersion is int onboarding)
        {
            doc["onboardingCompletedVersion"] = onboarding;
        }

        // 窗口位置（RFC-22 §3.2）：没存过就不写这个键（与引导标记同一条纪律）。
        if (value.PetPosition is Point position)
        {
            doc["petPosition"] = new JsonObject
            {
                ["x"] = position.X,
                ["y"] = position.Y,
            };
        }

        // 逐宠物位置（RFC-37 §7 第 4 条）：键是清单 id。空表 / null 就不写这个键（与引导标记同一条纪律）。
        // 按 key 的 Ordinal 序写：同一份数据每次落盘的字节序一致，diff 里不会出现无意义的重排。
        if (value.PetPositions is { Count: > 0 } positions)
        {
            var keys = new List<string>(positions.Keys);
            keys.Sort(StringComparer.Ordinal);
            var positionDoc = new JsonObject();
            foreach (string key in keys)
            {
                Point entry = positions[key];
                positionDoc[key] = new JsonObject { ["x"] = entry.X, ["y"] = entry.Y };
            }

            doc["petPositions"] = positionDoc;
        }

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
