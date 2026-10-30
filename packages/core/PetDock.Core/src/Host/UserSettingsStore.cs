// 设置的落盘与读取（原规格 17 §2 schema / §3 两阶段提交 / §4 审计字段）。
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

/// <summary>用户设置的读写（原规格 17 §1 的 UserSettings 形态）。</summary>
internal sealed partial class UserSettingsStore
{
    /// <summary>设置文件名（与 pets.json 同级，都在数据区根下）。</summary>
    public const string SettingsFile = "settings.json";

    /// <summary>
    /// 设置文件的写门。UI 线程（切语言、应用设置）与后台线程（插件启停、记窗口位置）会同时
    /// 改同一份 settings.json，而每条改动都是「读出 → 改一个字段 → 整份写回」：不串起来，
    /// 后写的那个拿着旧快照把前一个刚写的字段改回去，用户看到的是「设置自己变回去了」。
    ///
    /// 静态而不是实例字段：store 按数据目录 new 出来，同一个文件可能有多个实例，
    /// 门必须跟着文件走。（同一条纪律的现成写法见 Host/StateStore.cs 的 Gates。）
    /// </summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

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
    /// 一份坏掉的用户偏好不该让程序起不来（原规格 17 §2：宿主返回快照并且阻止加载是 ABI 层的做法，
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

            // 用户关掉的插件 id（2026-10-26 用户要求「插件可以选择关闭，重启生效」）。
            // 只收字符串；坏条目单独跳过 —— 宁可下一轮多装一只，不要因为一条脏数据启动失败。
            List<string>? disabledPlugins = ReadStringArray(obj, "pluginsDisabled");

            // 用户显式打开的插件 id（与 pluginsDisabled 同一形态、同一纪律）。
            List<string>? enabledPlugins = ReadStringArray(obj, "pluginsEnabled");

            // 用户从桌面上收起、但**没有**从清单里删掉的宠物 id（2026-10-30）。
            // 这是「从桌面移除」与「删除」分开之后的另一半：前者只收起，所以要记住收起状态，
            // 否则重启之后它们又自己回到桌面上，「移除」就成了只在本次会话里有效的假动作。
            List<string>? hiddenPets = ReadStringArray(obj, "hiddenPets");

            // 界面材质（原规格 18 §2.7）：只认三个名字，其余一律当没设过（回落到不透明）。
            // 与上面几个字段同一条纪律：坏值不迁移、不猜测，直接按默认走。
            string? material = null;
            if (obj["material"] is JsonNode materialNode)
            {
                string? token = materialNode is JsonValue materialValue && materialValue.TryGetValue(out string? text)
                    ? text
                    : null;
                if (token is "opaque" or "frosted" or "glass")
                {
                    material = token;
                }
                else
                {
                    KernelLog.Write($"settings-field-unreadable field=material reason=unknown-token");
                }
            }

            // 界面亮度（2026-10-28 第二版）：只认三个名字，其余按跟随系统。
            string? themeMode = null;
            if (obj["themeMode"] is JsonNode themeNode)
            {
                string? token = themeNode is JsonValue themeValue && themeValue.TryGetValue(out string? themeText)
                    ? themeText
                    : null;
                if (token is "system" or "light" or "dark")
                {
                    themeMode = token;
                }
                else
                {
                    KernelLog.Write("settings-field-unreadable field=themeMode reason=unknown-token");
                }
            }

            // 宠物清单是否已初始化（2026-10-28 第七轮）：首次运行把示例宠物装进清单，
            // 之后即使用户把宠物全删了也不自动装回来。缺失 = 还没初始化过。
            bool? petCatalogInitialized = null;
            if (obj["petCatalogInitialized"] is JsonValue initialized
                && initialized.TryGetValue(out bool initializedValue))
            {
                petCatalogInitialized = initializedValue;
            }

            // 宠物窗口尺寸（2026-10-28）：越界值由 Clamped() 拉回，坏值当没设过。
            int? petSize = null;
            if (obj["petSize"] is JsonNode sizeNode)
            {
                try
                {
                    petSize = sizeNode.GetValue<int>();
                }
                catch (Exception error)
                {
                    KernelLog.Write($"settings-field-unreadable field=petSize {error.GetType().Name}");
                }
            }

            // 声音与天体模式（2026-10-30 补）：这两个字段**以前只写不读**——记录里有、
            // 落盘时却没有任何一行把它们写进 JSON，读回时也没有任何一行把它们取出来。
            // 后果是「设置里关掉声音 / 打开天体模式，重启后又变回默认」，而且没有任何读数看得出来。
            // 与上面几个字段同一纪律：坏值当没设过（null），不猜。
            bool? soundEnabled = null;
            if (obj["soundEnabled"] is JsonValue soundValue && soundValue.TryGetValue(out bool soundBool))
            {
                soundEnabled = soundBool;
            }

            bool? celestialMode = null;
            if (obj["celestialMode"] is JsonValue celestialValue
                && celestialValue.TryGetValue(out bool celestialBool))
            {
                celestialMode = celestialBool;
            }

            return new UserSettings(
                period, language, onboarding, petPosition, petPositions,
                disabledPlugins, material, themeMode, enabledPlugins, petSize, petCatalogInitialized,
                soundEnabled, celestialMode, hiddenPets).Clamped();
        }
        catch (Exception error)
        {
            KernelLog.Write($"settings-unreadable {error.GetType().Name}: {error.Message}");
            return UserSettings.Default;
        }
    }

    /// <summary>读一个字符串数组字段：只收非空且不重复的字符串，坏条目跳过（同一条「不让脏数据卡启动」纪律）。</summary>
    private static List<string>? ReadStringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray array)
        {
            if (obj[key] is JsonNode notArray)
            {
                KernelLog.Write($"settings-field-unreadable field={key} reason=not-an-array type={notArray.GetType().Name}");
            }

            return null;
        }

        var ids = new List<string>(array.Count);
        foreach (JsonNode? entry in array)
        {
            string? id = entry is JsonValue value && value.TryGetValue(out string? text) ? text : null;
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

}
