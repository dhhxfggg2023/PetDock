using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetDock.DyberPet;

/// <summary>
/// Provenance 产物（契约 §2.8 / §4：真包与标准包之间逐字对应，不消费的字段在此留底供 M4 回用）。
/// 素材零分发：本文件只含**路径引用与配置值**，不含任何素材字节，可安全入库。
/// </summary>
internal static class ImportProvenance
{
    public static void Write(
        string outDir,
        string sourceDir,
        LayoutProbe probe,
        IReadOnlyList<DyberPetAction> actions,
        IReadOnlyList<ClipOutput> clips,
        IReadOnlyList<string> skipped,
        string? focus,
        IReadOnlyCollection<string> loopSet,
        JsonElement petRoot)
    {
        var needMove = actions.Where(a => a.HasLiveMovement)
            .Select(a => new Dictionary<string, object?>
            {
                ["action"] = a.Name,
                ["direction"] = a.Direction,
                ["frame_move"] = a.FrameMove,
            })
            .ToList();

        var anchors = actions.Where(a => a.Anchor is not null)
            .Select(a => new Dictionary<string, object?>
            {
                ["action"] = a.Name,
                ["anchor"] = a.Anchor,
            })
            .ToList();

        // 契约 §4：random_act 只进 provenance（定时随机动作需要 Brain，M4）。
        var randomActs = new List<Dictionary<string, object?>>();
        if (petRoot.TryGetProperty("random_act", out JsonElement randomEl) && randomEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in randomEl.EnumerateArray())
            {
                var item = new Dictionary<string, object?>();
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in entry.EnumerateObject())
                    {
                        item[prop.Name] = JsonSerializer.Deserialize<JsonElement>(prop.Value.GetRawText());
                    }
                }
                else
                {
                    item["entry"] = entry.GetRawText();
                }

                randomActs.Add(item);
            }
        }

        // pet_conf 里未映射的顶层键逐字留底（interact_speed/refresh/scale/item_favorite/item_dislike/msg_dict/prompt…）。
        var unmappedPetConf = new Dictionary<string, JsonElement>();
        foreach (JsonProperty prop in petRoot.EnumerateObject())
        {
            unmappedPetConf[prop.Name] = JsonSerializer.Deserialize<JsonElement>(prop.Value.GetRawText());
        }

        var doc = new Dictionary<string, object?>
        {
            ["generator"] = "petdock.importer.dyberpet (M3 t3)",
            ["contract"] = ".dsh-code/M3-contract-frozen.md",
            ["source"] = new Dictionary<string, object?>
            {
                ["dir"] = sourceDir,
                ["layout"] = probe.Layout.ToString(),
                ["confidence"] = probe.Confidence,
                ["evidence"] = probe.Evidence,
                ["readOnly"] = true,
                ["note"] = "素材只读引用（docs/32 §6）：frames 为绝对路径，无拷贝/哈希/CAS；assets/user/** 被 gitignore 覆盖，零字节入库。",
            },
            ["layout"] = new Dictionary<string, object?>
            {
                ["flatActions"] = probe.FlatActions,
                ["nestedActions"] = probe.NestedActions,
                ["ambiguous"] = probe.FlatActions > 0 && probe.NestedActions > 0,
            },
            ["loopSet"] = loopSet.ToList(),
            ["focus"] = focus,
            ["clips"] = clips.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Name,
                ["frames"] = c.Frames.Count,
                ["fps"] = c.Fps,
                ["fpsOrigin"] = c.FpsOrigin,
                ["loop"] = c.Loop,
                ["images"] = c.Source.Images,
                ["actNumProvenanceOnly"] = c.Source.ActNum,
            }).ToList(),
            ["skippedClips"] = skipped.ToList(),
            ["liveMovementNotConsumed"] = needMove,
            ["anchorsNotConsumed"] = anchors,
            ["randomActNotConsumed"] = randomActs,
            ["patpatNotConsumed"] = petRoot.TryGetProperty("patpat", out JsonElement patpat)
                ? JsonSerializer.Deserialize<JsonElement>(patpat.GetRawText())
                : null,
            ["unmappedFeatures"] = new Dictionary<string, object?>
            {
                // 契约 §5.4 十条降级登记（stderr 冻结行只取前七条）。
                ["growth"] = "pet_conf 无对应字段可映射，M3 不做",
                ["quests"] = "不做（docs/32 §5 不可复现）",
                ["shop"] = "不做（docs/32 §5 不可复现）",
                ["aiDialogue"] = "msg_dict/prompt 不消费（docs/32 §5 不可复现）",
                ["audio"] = "note/*.wav 不消费（presenter 无音频面）",
                ["liveMovement"] = "need_move/direction/frame_move 不消费（缺 Brain 与 MoveTo 宿主接线）",
                ["randomAct"] = "random_act[] 留底不消费（需 Brain 定时器）",
                ["hoverProduction"] = "core 无 hover 事件生产路径（Cursor 从未填充），悬停=自检面交付，M4 补",
                ["anchor"] = "anchor 偏移不消费（fall:[5,0]/seebook:[0,-35] 留底）",
                ["importUi"] = "M3 为可命令转换，无 UI（docs/18 §5 UI 提示行属 RFC-C）",
            },
            ["unmappedPetConf"] = unmappedPetConf,
        };

        DyberPetImporter.WriteJson(Path.Combine(outDir, DyberPetImporter.ProvenanceFileName), doc);
    }
}
