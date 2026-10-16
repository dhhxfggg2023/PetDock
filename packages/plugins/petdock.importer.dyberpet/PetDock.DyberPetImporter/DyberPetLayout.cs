using System.Text.Json;

namespace PetDock.DyberPet;

/// <summary>布局探测的三档结论（M3 契约 §1.2）：无包 / 扁平档 / 嵌套档。</summary>
internal enum DyberPetLayoutKind
{
    None,
    DyberPetFlat,
    DyberPetNested,
}

/// <summary>一次布局探测的结果：档位、置信度、证据清单，以及两档各自能数出的动作数（审计行用）。</summary>
internal sealed record LayoutProbe(
    DyberPetLayoutKind Layout,
    double Confidence,
    IReadOnlyList<string> Evidence,
    int FlatActions,
    int NestedActions)
{
    public override string ToString() =>
        $"{Layout} confidence={Confidence.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}"
        + $" flat-actions={FlatActions} nested-actions={NestedActions}";
}

/// <summary>
/// DyberPet 素材包的布局探测（M3 契约 §1 冻结的三条规则，docs/32 §2 与真包实测的分歧由此裁决）：
///   flat    = <dir>/pet_conf.json 且 <dir>/act_conf.json          → 置信度 0.9
///   nested  = <dir>/pet_conf.json 且 <dir>/actions/actions_conf.json → 置信度 0.9
///   两档同命中 → nested 优先，并强制输出审计行 dyberpet-layout-ambiguous
///   只有 pet_conf.json → 0.4，**不接收**（docs/99:67 点名的「静默解析出空动画」由此堵死）
/// 只看本目录里写得出名字的文件，不做扩展名猜测。
/// </summary>
internal static class DyberPetLayout
{
    public const string PetConfFile = "pet_conf.json";
    public const string FlatActConfFile = "act_conf.json";
    public const string NestedActionsDir = "actions";
    public const string NestedActionsConfFile = "actions_conf.json";

    public static LayoutProbe Probe(string sourceDir)
    {
        bool hasPetConf = File.Exists(Path.Combine(sourceDir, PetConfFile));
        bool hasFlat = File.Exists(Path.Combine(sourceDir, FlatActConfFile));
        bool hasNested = File.Exists(Path.Combine(sourceDir, NestedActionsDir, NestedActionsConfFile));

        var evidence = new List<string>();
        int flatActions = hasFlat ? CountActionsFlat(sourceDir) : 0;
        int nestedActions = hasNested ? CountActionsNested(sourceDir) : 0;

        if (hasPetConf && hasNested)
        {
            evidence.Add($"{PetConfFile}+{NestedActionsDir}/{NestedActionsConfFile}");
        }

        if (hasPetConf && hasFlat)
        {
            evidence.Add($"{PetConfFile}+{FlatActConfFile}");
        }

        if (hasPetConf && hasNested)
        {
            // 两档同命中：nested 优先（契约 §1.2），歧义必须可见。
            return new LayoutProbe(DyberPetLayoutKind.DyberPetNested, 0.9, evidence, flatActions, nestedActions);
        }

        if (hasPetConf && hasFlat)
        {
            return new LayoutProbe(DyberPetLayoutKind.DyberPetFlat, 0.9, evidence, flatActions, nestedActions);
        }

        if (hasPetConf)
        {
            // 0.4 档：有角色没动作配置。调用方必须拒绝导入，不允许静默产出空动画。
            evidence.Add($"{PetConfFile} only");
            return new LayoutProbe(DyberPetLayoutKind.None, 0.4, evidence, 0, 0);
        }

        evidence.Add("no pet_conf.json");
        return new LayoutProbe(DyberPetLayoutKind.None, 0.0, evidence, 0, 0);
    }

    /// <summary>扁平档动作数：act_conf.json 是「动作名 → 配置」对象（真包实测）或配置数组。</summary>
    private static int CountActionsFlat(string sourceDir)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceDir, FlatActConfFile)));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().Count()
                : doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.GetArrayLength()
                    : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>嵌套档动作数：actions_conf.json 是「动作清单」（docs/32 §2），对象/数组两种容忍形态都数。</summary>
    private static int CountActionsNested(string sourceDir)
    {
        try
        {
            string path = Path.Combine(sourceDir, NestedActionsDir, NestedActionsConfFile);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("actions", out JsonElement actions)
                && actions.ValueKind == JsonValueKind.Array)
            {
                return actions.GetArrayLength();
            }

            return root.ValueKind == JsonValueKind.Array
                ? root.GetArrayLength()
                : root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Count() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
