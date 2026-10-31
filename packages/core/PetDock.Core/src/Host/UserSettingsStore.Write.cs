// 设置落盘的写半边：Save / Update / WriteCore，以及字符串数组字段的写出。
//
// 为什么和读分开：这条路径要证明的是「同一份 settings.json 只有一个写者」。
// 门（WriteGate）与它保护的东西放在同一个文件里，审的时候不用在半屏读逻辑与半屏写逻辑之间来回跳。
//
// 门不可重入：change 回调里不许再调 Save/Update（会自己等自己）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal sealed partial class UserSettingsStore
{
    /// <summary>
    /// 写设置。两阶段提交 + 原规格 17 §4 要求的 agent_id 与 timestamp。
    /// 写失败抛异常（调用方负责把失败说清楚，不能假装存上了）。
    ///
    /// 整份重写（RFC-16 §3.2 的纪律）：调用方必须**先读出再改一个字段**，
    /// 不能凭记忆造一份 UserSettings 写回来 —— 那会把别的字段抹掉
    /// （典型后果：引导写完一次性标记，用户随后在设置页点一次「应用」，标记被抹，引导又弹一次）。
    /// </summary>
    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WriteGate.Wait();
        try
        {
            WriteCore(settings);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>
    /// 原子地改设置：把「读出 → 改 → 写回」整段锁在一起，返回落盘后的值。
    /// change 返回**同一个实例**表示没有要改的（省一次盘写，也让「点一下没变化」不产生 IO）。
    /// change 里不要回调用 Save/Update —— 门不可重入。
    /// </summary>
    public UserSettings Update(Func<UserSettings, UserSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        WriteGate.Wait();
        try
        {
            UserSettings current = Load();
            UserSettings next = change(current);
            if (ReferenceEquals(next, current))
            {
                return current;
            }

            WriteCore(next);
            return next;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>真正写盘的那一半：调用方必须已经握着 WriteGate。</summary>
    private void WriteCore(UserSettings settings)
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

        // 界面材质（原规格 18 §2.7）：只在不是默认值时写这个键——「没这个键」= 不透明，
        // 这样旧文件不需要迁移，而选了磨砂/玻璃的人重开程序还在。
        if (value.Material is { Length: > 0 } material && material != "opaque")
        {
            doc["material"] = material;
        }

        // 界面亮度：与材质同一条纪律——「system」是默认，不写键；显式选过浅/深才落盘。
        if (value.ThemeMode is "light" or "dark")
        {
            doc["themeMode"] = value.ThemeMode;
        }

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

        // 宠物清单初始化标记：只在 true 时写键（false/缺失都表示「下次启动还要初始化」）。
        if (value.PetCatalogInitialized == true)
        {
            doc["petCatalogInitialized"] = true;
        }

        // 宠物窗口尺寸：128 是默认值，不写键；只有用户选过别的尺寸才落盘。
        if (value.PetSize is int petSize && petSize != UserSettings.DefaultPetSize)
        {
            doc["petSize"] = petSize;
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

        // 声音与天体模式（2026-10-30 补）：这两个字段以前在记录里、却没有写出这一行，
        // 于是「关掉声音 / 打开天体模式」重启后一律回到默认。只在用户显式设过时写键。
        if (value.SoundEnabled is bool sound)
        {
            doc["soundEnabled"] = sound;
        }

        if (value.CelestialMode is bool celestial)
        {
            doc["celestialMode"] = celestial;
        }

        // 逐宠物尺寸（2026-10-31）：与 petPositions 同一条纪律 —— 键是清单 id，空表不写键，
        // 按 key 的 Ordinal 序写（同一份数据每次落盘字节序一致，repro 判据才过得去）。
        if (value.PetSizes is { Count: > 0 } sizes)
        {
            var sizeKeys = new List<string>(sizes.Keys);
            sizeKeys.Sort(StringComparer.Ordinal);
            var sizeDoc = new JsonObject();
            foreach (string key in sizeKeys)
            {
                sizeDoc[key] = sizes[key];
            }

            doc["petSizes"] = sizeDoc;
        }

        // 用户关掉 / 打开的插件（空表 / null 就不写这个键）。排序后写，字节序稳定。
        WriteStringArray(doc, "pluginsDisabled", value.DisabledPlugins);
        WriteStringArray(doc, "pluginsEnabled", value.EnabledPlugins);

        // 从桌面收起、但仍在清单里的宠物 id（2026-10-30）：与上面两个同一条纪律（空表不写键）。
        WriteStringArray(doc, "hiddenPets", value.HiddenPets);

        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 临时名带随机后缀（原来是固定的 ".tmp"）：两个进程/两条线程同时写时，
        // 固定名会让一个把另一个还没 Move 的文件覆盖掉，然后把半份内容改名成正式文件。
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temp,
                doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 写坏/抢输的那一份不要留给用户：数据目录里躺一串 .tmp 会让人以为设置坏了。
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // 删不掉就算了：下一次成功写入不会用到它。
            }

            throw;
        }
    }

    /// <summary>写一个字符串数组字段（排序后写，字节序稳定；空表不写键）。</summary>
    private static void WriteStringArray(JsonObject doc, string key, IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return;
        }

        var sorted = new List<string>(ids);
        sorted.Sort(StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (string id in sorted)
        {
            // 用 JsonValue.Create 而不是 array.Add(id)：泛型 Add<T> 会把字符串包成
            // JsonValueCustomized<string>，写出时要求 JsonSerializerOptions 带 TypeInfoResolver，
            // 否则运行期抛 InvalidOperationException（与 DiagnosticSections.ToJsonArray 同一个坑）。
            array.Add(JsonValue.Create(id)!);
        }

        doc[key] = array;
    }
}
