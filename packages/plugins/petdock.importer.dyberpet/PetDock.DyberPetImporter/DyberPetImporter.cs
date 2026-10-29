using System.Text.Json;
using System.Threading;
using PetDock.Hosting;

namespace PetDock.DyberPet;

/// <summary>一个动作在磁盘上的归一化形态：flat/nested 两档解析后共用这条转换管线。</summary>
internal sealed record DyberPetAction(
    string Name,
    string Images,
    int? ActNum,
    double? FrameRefresh,
    double? DirectFps,
    string? NeedMove,
    string? Direction,
    int? FrameMove,
    int[]? Anchor,
    IReadOnlyList<string> ExplicitFrames)
{
    public bool HasLiveMovement => !string.IsNullOrEmpty(NeedMove);
}

/// <summary>
/// DyberPet 真包 → sprite-seq 标准包（petdock-sample.json + anim/*.json）的转换核心。
/// 逐条裁决出自 交接档案：
///   §2.2 帧数 = 文件枚举（前缀取 act_conf 的 images 字段，不是动作名）；
///   §2.3 act_num 不消费（真包 18 前缀无一与文件数相等，sleep 20>16）——只进 provenance；
///   §2.4 fps = 1/frame_refresh（缺/≤0 回落 0.120 → 8.3333fps，不许 fps=act_num/frame_refresh）；
///   §2.5 loop = LoopSet 白名单（pet_conf 的 default/left/right/up/down 五键值），其余 once；
///   §2.6 clip id = 动作名原样；defaultAnimation = pet_conf.default；
///   §2.7 naturalSize = 全包最大帧尺寸（pet_conf 的 width/height 是画板/舞台尺寸，实测与任何帧不等）；
///   §2.8 frames[] = 真包 PNG 的绝对路径只读引用（无拷贝、无哈希、无 CAS）；
///   §2.7 帧小于画板 → 左上角 (0,0) 粘贴，余处透明（SpriteSeqView.Compose 侧同律）。
/// </summary>
internal static class DyberPetImporter
{
    public const string PackManifestName = "petdock-sample.json"; // 原规格 31 §1：与 PackLoader.ManifestName 同一格式名
    public const string AnimDirName = "anim";
    public const string ProvenanceFileName = "dyberpet-provenance.json";
    public const double DefaultFrameRefresh = 0.120;
    public const double FallbackFps = 1.0 / DefaultFrameRefresh;

    /// <summary>契约 §2.5 的 LoopSet：pet_conf 里这五个键的**值**是常驻动作（loop）。</summary>
    private static readonly string[] LoopSetKeys = { "default", "left", "right", "up", "down" };

    /// <summary>进入转换前必须已达 0.9 置信档；0.4 档（只有 pet_conf.json）在这里被拒收。</summary>
    public sealed class LayoutRejectException(string message) : Exception(message);

    /// <summary>转换入口：探布局 → 读动作表 → 枚举帧 → 产 pack。返回动作名 → 帧数/fps（审计行与自检用）。</summary>
    public static ImportResult Import(
        string sourceDir,
        string outDir,
        bool bundleFrames = false,
        PackImportProgressNotify? progress = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        LayoutProbe probe = DyberPetLayout.Probe(sourceDir);
        Console.Error.WriteLine($"dyberpet-layout {probe.Layout} confidence={Fmt(probe.Confidence)}"
            + $" flat-actions={probe.FlatActions} nested-actions={probe.NestedActions}"
            + $" evidence={string.Join(",", probe.Evidence)}");
        if (probe.Layout == DyberPetLayoutKind.DyberPetNested && probe.FlatActions > 0)
        {
            Console.Error.WriteLine(
                $"dyberpet-layout-ambiguous nested={probe.NestedActions} flat={probe.FlatActions} chosen=nested");
        }

        if (probe.Layout == DyberPetLayoutKind.None)
        {
            throw new LayoutRejectException(
                $"dyberpet-layout-none confidence={Fmt(probe.Confidence)}"
                + $" reason=pet-conf-without-action-conf（契约 §1：0.4 档不接收，不许静默产出空动画）");
        }

        string petConfPath = Path.Combine(sourceDir, DyberPetLayout.PetConfFile);
        using JsonDocument petConf = JsonDocument.Parse(File.ReadAllText(petConfPath));
        JsonElement petRoot = petConf.RootElement;

        string defaultAnimation = petRoot.TryGetProperty("default", out JsonElement def) && def.ValueKind == JsonValueKind.String
            ? def.GetString()!
            : throw new InvalidDataException($"{DyberPetLayout.PetConfFile}: 无 default 键，无法定初始 clip（契约 §2.6）");

        var loopSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in LoopSetKeys)
        {
            if (petRoot.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                loopSet.Add(v.GetString()!);
            }
        }

        string? focus = petRoot.TryGetProperty("focus", out JsonElement focusEl) && focusEl.ValueKind == JsonValueKind.String
            ? focusEl.GetString()
            : null;

        List<DyberPetAction> actions = probe.Layout == DyberPetLayoutKind.DyberPetFlat
            ? ReadFlatActions(sourceDir)
            : ReadNestedActions(sourceDir);

        string actionDir = Path.Combine(sourceDir, "action"); // 真包实测：扁平档帧在 action/
        var clips = new List<ClipOutput>();
        var skipped = new List<string>();
        int maxW = 0;
        int maxH = 0;

        foreach (DyberPetAction action in actions)
        {
            token.ThrowIfCancellationRequested();
            List<string> frames = EnumerateFrames(action, actionDir, sourceDir);
            if (frames.Count == 0)
            {
                // 契约 §2.3：0 帧 clip 会让 PackLoader.LoadClip 抛「frames 为空」——宁缺毋滥，审计行可见。
                skipped.Add(action.Name);
                Console.Error.WriteLine($"dyberpet-clip-skipped {action.Name} reason=no-frames"
                    + $" images={action.Images}");
                continue;
            }

            double frameRefresh = action.FrameRefresh ?? DefaultFrameRefresh;
            double fps;
            string fpsOrigin;
            if (action.DirectFps is double direct)
            {
                fps = direct > 0 ? direct : FallbackFps;
                fpsOrigin = direct > 0 ? $"direct-fps={Fmt(direct)}" : "direct-fps<=0";
            }
            else if (frameRefresh > 0)
            {
                fps = 1.0 / frameRefresh;
                fpsOrigin = $"refresh={Fmt(frameRefresh)}";
            }
            else
            {
                fps = FallbackFps;
                fpsOrigin = "missing-frame_refresh";
                Console.Error.WriteLine(
                    $"dyberpet-fps-fallback {action.Name} reason=missing-frame_refresh");
            }

            // naturalSize = 全包最大帧（契约 §2.7）。PNG IHDR 头 24 字节即得尺寸，不整图解码。
            foreach (string frame in frames)
            {
                (int w, int h) = PngMeta.ReadSize(frame);
                if (w > maxW)
                {
                    maxW = w;
                }

                if (h > maxH)
                {
                    maxH = h;
                }
            }

            bool loop = loopSet.Contains(action.Name);
            var clip = new ClipOutput(
                action.Name,
                frames,
                fps,
                loop ? "loop" : "once",
                action,
                fpsOrigin);
            clips.Add(clip);
            Console.Error.WriteLine($"dyberpet-clip {action.Name} frames={frames.Count}"
                + $" fps={Fmt(fps)} {fpsOrigin} loop={(loop ? "loop" : "once")} images={action.Images}"
                + (action.ActNum is int actNum ? $" act_num={actNum}(provenance-only)" : string.Empty));
        }

        if (maxW == 0 || maxH == 0)
        {
            throw new InvalidDataException("全部动作都没有可枚举的帧（importer 不产出空包）");
        }

        if (!clips.Any(c => string.Equals(c.Name, defaultAnimation, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"pet_conf.default={defaultAnimation} 没有可用帧（PackLoader 会拒绝 defaultAnimation 不在 animations 里的包）");
        }

        ClipOutput defaultClip = clips.First(c => string.Equals(c.Name, defaultAnimation, StringComparison.Ordinal));
        int totalFrames = clips.Sum(c => c.Frames.Count);
        if (bundleFrames && progress is not null)
        {
            progress(new PackImportProgress(0, totalFrames));
        }

        WritePack(outDir, clips, defaultClip, maxW, maxH, sourceDir, bundleFrames, progress, totalFrames, token);
        ImportProvenance.Write(outDir, sourceDir, probe, actions, clips, skipped, focus, loopSet, petRoot);
        PackSelfCheck.Run(outDir, clips, maxW, maxH, focus);
        if (bundleFrames && progress is not null)
        {
            progress(new PackImportProgress(totalFrames, totalFrames));
        }

        Console.Error.WriteLine($"dyberpet-import source={sourceDir} layout={probe.Layout}"
            + $" actions={actions.Count} clips={clips.Count} skipped={skipped.Count}"
            + $" frames={totalFrames} natural={maxW}x{maxH} preferredFps={Fmt(defaultClip.Fps)}"
            + $" out={outDir}");
        Console.Error.WriteLine(
            "DEGRADED fidelity=degraded missing=growth,quests,shop,ai-dialogue,audio,live-movement,random-act");
        return new ImportResult(sourceDir, outDir, probe.Layout, actions.Count, clips.Count, totalFrames, maxW, maxH, focus);
    }

    private static string Fmt(double value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>扁平档：act_conf.json 是「动作名 → {images, act_num, frame_refresh, …}」对象（真包实测 121 行/21 动作）。</summary>
    private static List<DyberPetAction> ReadFlatActions(string sourceDir)
    {
        string path = Path.Combine(sourceDir, DyberPetLayout.FlatActConfFile);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var actions = new List<DyberPetAction>();
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            actions.Add(ReadActionEntry(prop.Name, prop.Value, explicitFrames: null));
        }

        return actions;
    }

    /// <summary>嵌套档（原规格 32 §2）：actions/actions_conf.json 清单 + actions/&lt;action&gt;_conf.json 单动作配置。</summary>
    private static List<DyberPetAction> ReadNestedActions(string sourceDir)
    {
        string actionsDir = Path.Combine(sourceDir, DyberPetLayout.NestedActionsDir);
        string confPath = Path.Combine(actionsDir, DyberPetLayout.NestedActionsConfFile);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(confPath));
        JsonElement root = doc.RootElement;

        var names = new List<string>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            names.AddRange(root.EnumerateArray().Select(ReadActionName));
        }
        else if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("actions", out JsonElement list)
            && list.ValueKind == JsonValueKind.Array)
        {
            names.AddRange(list.EnumerateArray().Select(ReadActionName));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            names.AddRange(root.EnumerateObject().Select(p => p.Name));
        }

        var actions = new List<DyberPetAction>();
        foreach (string name in names)
        {
            string confFile = Path.Combine(actionsDir, $"{name}_conf.json");
            if (!File.Exists(confFile))
            {
                Console.Error.WriteLine($"dyberpet-clip-skipped {name} reason=missing-action-conf");
                continue;
            }

            using JsonDocument conf = JsonDocument.Parse(File.ReadAllText(confFile));
            JsonElement entry = conf.RootElement;

            // 帧文件名列表（原规格 32 §3「帧文件名列表」）相对 actions/<action>/ 解析；
            // 没有 frames 列表但写了 images 前缀时，退回 <images>_<n>.png 枚举（与 flat 同律）。
            List<string>? explicitFrames = null;
            if (entry.TryGetProperty("frames", out JsonElement framesEl) && framesEl.ValueKind == JsonValueKind.Array)
            {
                explicitFrames = framesEl.EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => f.GetString()!)
                    .ToList();
            }

            double? directFps = entry.TryGetProperty("fps", out JsonElement fpsEl) && fpsEl.ValueKind == JsonValueKind.Number
                ? fpsEl.GetDouble()
                : null;
            actions.Add(ReadActionEntry(name, entry, explicitFrames, directFps));
        }

        return actions;
    }

    private static string ReadActionName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? throw new InvalidDataException("actions 清单含 null 项"),
        JsonValueKind.Object when element.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
            => n.GetString() ?? throw new InvalidDataException("actions 清单项 name 为 null"),
        _ => throw new InvalidDataException($"actions 清单项形态不识别（{element.ValueKind}），原规格 32 §2 的 unverified 字段需先定稿"),
    };

    /// <summary>flat/nested 共用的动作配置字段读取（同一套键名，真包实测出处）。</summary>
    private static DyberPetAction ReadActionEntry(string name, JsonElement entry, List<string>? explicitFrames, double? directFps = null)
    {
        string images = entry.TryGetProperty("images", out JsonElement imagesEl) && imagesEl.ValueKind == JsonValueKind.String
            ? imagesEl.GetString()!
            : name;
        int? actNum = entry.TryGetProperty("act_num", out JsonElement actNumEl) && actNumEl.ValueKind == JsonValueKind.Number
            ? actNumEl.GetInt32()
            : null;
        double? frameRefresh = entry.TryGetProperty("frame_refresh", out JsonElement refreshEl) && refreshEl.ValueKind == JsonValueKind.Number
            ? refreshEl.GetDouble()
            : null;
        string? needMove = entry.TryGetProperty("need_move", out JsonElement moveEl) && moveEl.ValueKind == JsonValueKind.True
            ? "true"
            : null;
        string? direction = entry.TryGetProperty("direction", out JsonElement dirEl) && dirEl.ValueKind == JsonValueKind.String
            ? dirEl.GetString()
            : null;
        int? frameMove = entry.TryGetProperty("frame_move", out JsonElement fmEl) && fmEl.ValueKind == JsonValueKind.Number
            ? fmEl.GetInt32()
            : null;
        int[]? anchor = null;
        if (entry.TryGetProperty("anchor", out JsonElement anchorEl) && anchorEl.ValueKind == JsonValueKind.Array
            && anchorEl.GetArrayLength() == 2)
        {
            anchor = anchorEl.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        }

        return new DyberPetAction(name, images, actNum, frameRefresh, directFps, needMove, direction, frameMove, anchor, explicitFrames ?? (IReadOnlyList<string>)Array.Empty<string>());
    }

    /// <summary>
    /// 帧枚举（契约 §2.2）：显式列表（nested）优先；否则 <c>&lt;dir&gt;/&lt;images&gt;_&lt;n&gt;.png</c> 按 n 升序枚举。
    /// 只认数字后缀，排序按数值（default_10 排在 default_2 之后）；找不到任何帧返回空表（上游会 skip）。
    /// </summary>
    private static List<string> EnumerateFrames(DyberPetAction action, string flatActionDir, string sourceDir)
    {
        if (action.ExplicitFrames.Count > 0)
        {
            // 显式列表里的帧必须齐全：缺一张就是上游配置与文件不一致，宁可导入期失败也不静默丢帧。
            string baseDir = Path.Combine(sourceDir, DyberPetLayout.NestedActionsDir, action.Name);
            var resolved = new List<string>();
            foreach (string frame in action.ExplicitFrames)
            {
                string full = Path.GetFullPath(Path.Combine(baseDir, frame));
                if (!File.Exists(full))
                {
                    throw new FileNotFoundException($"嵌套档帧缺失: {frame}（action={action.Name}，基目录 {baseDir}）");
                }

                resolved.Add(full);
            }

            return resolved;
        }

        string dir = flatActionDir;
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        string prefix = action.Images + "_";
        var numbered = new List<(int N, string Path)>();
        foreach (string file in Directory.GetFiles(dir, action.Images + "_*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            int underscore = name.LastIndexOf('_');
            if (underscore < 0 || underscore + 1 >= name.Length
                || !int.TryParse(name[(underscore + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                continue;
            }

            numbered.Add((n, Path.GetFullPath(file)));
        }

        return numbered.OrderBy(t => t.N).Select(t => t.Path).ToList();
    }

    /// <summary>
    /// 落盘：petdock-sample.json + anim/*.json。CLI 默认仍写绝对路径只读引用（契约 §2.8，不入库）；
    /// 宿主导入（bundleFrames=true）把帧复制进包内并写相对路径，导入后不依赖用户原目录。
    /// </summary>
    private static void WritePack(
        string outDir,
        List<ClipOutput> clips,
        ClipOutput defaultClip,
        int w,
        int h,
        string sourceDir,
        bool bundleFrames,
        PackImportProgressNotify? progress,
        int totalFrames,
        CancellationToken token)
    {
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, AnimDirName));

        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["name"] = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            ["author"] = bundleFrames
                ? "imported from DyberPet pack (assets bundled)"
                : "imported from DyberPet pack (read-only reference; assets NOT bundled)",
            ["license"] = "UNLICENSED-third-party (see dyberpet-provenance.json; 原规格 52 §5 只读引用不入库)",
            ["naturalSize"] = new Dictionary<string, object?> { ["w"] = w, ["h"] = h },
            ["preferredFps"] = defaultClip.Fps,
            ["animations"] = clips.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Name,
                ["clip"] = $"{AnimDirName}/{c.Name}.json",
            }).ToList(),
            ["defaultAnimation"] = defaultClip.Name,
        };
        WriteJson(Path.Combine(outDir, PackManifestName), manifest);

        int done = 0;
        foreach (ClipOutput clip in clips)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<string> frames = bundleFrames
                ? BundleFrames(outDir, clip, progress, totalFrames, ref done, token)
                : clip.Frames.ToList();
            var clipJson = new Dictionary<string, object?>
            {
                ["frames"] = frames,
                ["fps"] = clip.Fps,
                ["loop"] = clip.Loop,
            };
            WriteJson(Path.Combine(outDir, AnimDirName, clip.Name + ".json"), clipJson);
        }
    }

    /// <summary>把一帧帧复制到包内 img/&lt;clip&gt;/，返回相对路径（正斜杠，PackLoader 按包根解析）。</summary>
    private static IReadOnlyList<string> BundleFrames(
        string outDir,
        ClipOutput clip,
        PackImportProgressNotify? progress,
        int totalFrames,
        ref int done,
        CancellationToken token)
    {
        string relativeDir = Path.Combine("img", SafeName(clip.Name));
        string fullDir = Path.Combine(outDir, relativeDir);
        Directory.CreateDirectory(fullDir);

        var frames = new List<string>();
        foreach (string frame in clip.Frames)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(frame);
            File.Copy(frame, Path.Combine(fullDir, name), overwrite: true);
            done++;
            progress?.Invoke(new PackImportProgress(done, totalFrames));
            frames.Add((relativeDir + "/" + name).Replace('\\', '/'));
        }

        return frames;
    }

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    internal static void WriteJson(string path, object payload)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, options));
    }
}

/// <summary>一条待写盘 clip 的内存形态。</summary>
internal sealed record ClipOutput(
    string Name,
    IReadOnlyList<string> Frames,
    double Fps,
    string Loop,
    DyberPetAction Source,
    string FpsOrigin);

/// <summary>导入结果摘要（CLI 收尾行与自检报告用）。</summary>
internal sealed record ImportResult(
    string SourceDir,
    string OutDir,
    DyberPetLayoutKind Layout,
    int Actions,
    int Clips,
    int TotalFrames,
    int NaturalWidth,
    int NaturalHeight,
    string? Focus);
