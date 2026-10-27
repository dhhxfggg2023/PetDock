using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using PetDock.Core.PetRuntime;

namespace PetDock.Builtins;

/// <summary>一个动画片段：ABI 侧的 AnimationClip（帧数/fps/loop）+ 磁盘上的帧文件相对路径序列。</summary>
internal sealed record ClipData(AnimationClip Clip, IReadOnlyList<string> FramePaths);

/// <summary>原规格 31 Sample Pack 解析结果（petdock-sample.json + anim/*.json 的内存形态）。</summary>
internal sealed record PackData(
    PetSize NaturalSize,
    double PreferredFps,
    string DefaultAnimation,
    IReadOnlyList<ClipData> Clips)
{
    public bool HasClip(string id) =>
        Clips.Any(c => string.Equals(c.Clip.Id, id, StringComparison.Ordinal));

    public ClipData Clip(string id) =>
        Clips.First(c => string.Equals(c.Clip.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// 原规格 31 Sample Pack 的定位与解析。定位是插件侧自己的事：
/// core 不认识任何素材格式（原规格 01 P-02 第 2 条），也不特判插件 id（P-02 第 4 条），
/// 所以「素材在哪」由实现方以环境变量 + 向上探测解决，失败时把试过的路径说清楚。
///
/// 三条优先级（2026-10-16 所有者最终口径：鲸鱼娘是静态图，暂用可动的示例宠物作默认可见宠物
/// 是可以的 —— 原话「鲸鱼娘不是动图，你暂时用可动的桌宠例子也是可以的，刚才我说的不好」）：
///   1. `PETDOCK_SAMPLE_PACK` —— 显式请求某个 pack 目录，必须含 petdock-sample.json；
///   2. `PETDOCK_PET_IMAGE` —— 显式请求单张贴图（所有者鲸鱼娘），合成一片段 pack；
///   3. 默认 —— 向上探测 assets/sample-pet（原规格 31 Sample Pack，仓库内 CC0 素材）。
/// 自检相位（PetViewContext.PetInstanceId 以 "animtest" 开头）与默认同解，不特判：
/// 「桌面上是哪一只」必须是可指名的事实，而不是相位副作用。
/// 旧实现里那条「默认回落到仓库内程序化蓝团」的路径已删除（t8 验收 3）：
/// 那正是 898 字节贴图被误当作宠物图的成因；既无包又无图时由内核画代码占位图。
/// </summary>
internal static class PackLoader
{
    private const string ManifestName = "petdock-sample.json"; // 原规格 31 §1
    private const string PackEnvVar = "PETDOCK_SAMPLE_PACK";
    private const string ImageEnvVar = "PETDOCK_PET_IMAGE";
    private const string SelfTestPhase = "animtest";
    private const string PackRel = "assets/sample-pet";
    private const string OwnerImageRel = "assets/sample/local/pet-sample.png";

    /// <summary>一个来源是不是「默认」解：默认解就是 原规格 31 Sample Pack，没有任何环境变量参与。</summary>
    public static bool IsDefaultLocation(string? located) =>
        !string.IsNullOrEmpty(located)
        && located!.EndsWith(PackRel, StringComparison.OrdinalIgnoreCase);

    /// <summary>自检相位的实例 id（--animtest 唯一入口，core 传的就是 "animtest"）。</summary>
    public static bool IsSelfTestPhase(string petInstanceId) =>
        petInstanceId.StartsWith(SelfTestPhase, StringComparison.Ordinal);

    /// <summary>
    /// 按三条优先级定位素材来源，返回**包目录**（含 petdock-sample.json）或**单图路径**（.png）。
    /// 调用方用 <see cref="IsPackRoot"/> 区分两种形态：目录走 原规格 31 的 pack 解析，
    /// 单图合成为一片段（idle，1 帧）的 pack —— 所有者贴图是静态图，本就不该有动画。
    /// </summary>
    public static string Locate(string petInstanceId)
    {
        string? fromPack = Environment.GetEnvironmentVariable(PackEnvVar);
        if (!string.IsNullOrWhiteSpace(fromPack))
        {
            string full = Path.GetFullPath(fromPack);
            if (File.Exists(Path.Combine(full, ManifestName)))
            {
                return full;
            }

            throw new DirectoryNotFoundException($"{PackEnvVar}={fromPack} 下没有 {ManifestName}");
        }

        string? fromImage = Environment.GetEnvironmentVariable(ImageEnvVar);
        if (!string.IsNullOrWhiteSpace(fromImage))
        {
            string full = Path.GetFullPath(fromImage);
            if (File.Exists(full))
            {
                return full;
            }

            throw new FileNotFoundException($"{ImageEnvVar}={fromImage} 不存在");
        }

        // 默认：原规格 31 Sample Pack。先从 exe 基目录向上找（仓库内 / 发布物同构的位置都命中），
        // 再退回所有者本机贴图这一条**已知路径**之外不猜任何位置；两条都找不到才抛。
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, PackRel, ManifestName);
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, PackRel);
            }
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, OwnerImageRel);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"默认素材定位失败：从 {AppContext.BaseDirectory} 向上既没有 {PackRel}/{ManifestName}"
            + $" 也没有 {OwnerImageRel}（要显式指名，设 {PackEnvVar}=<pack 目录> 或 {ImageEnvVar}=<png>）");
    }

    /// <summary>
    /// 按宿主给的实例级素材根定位（RFC-15 §3.2 第 4 跳）。
    /// 优先级高于进程级环境变量：显式路径是**按实例**的，环境变量是**按进程**的，
    /// 全局变量没法表达「这只宠物用这份素材，那只用那份」。
    /// 返回 null = 宿主没给素材根（调用方回落到 Locate 的三条优先级）。
    /// 抛 FileNotFoundException = 宿主给了但那些路径里没有一个是可用素材 —— 这不是「没有素材」，
    /// 而是「用户指名的东西没了」，两者必须区分。
    /// </summary>
    public static string? LocateFromAssetRoots(IReadOnlyList<string> assetRoots)
    {
        if (assetRoots is null || assetRoots.Count == 0)
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (string raw in assetRoots)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 宿主给了一个连路径都规整不出来的字符串：算作一次未命中，继续看下一个候选。
                continue;
            }

            candidates.Add(full);
            if (Directory.Exists(full))
            {
                // 目录形态：必须自带清单文件才算素材根；只是普通目录就跳过（宿主可能给了多个候选根）。
                if (IsPackRoot(full))
                {
                    return full;
                }

                continue;
            }

            if (File.Exists(full)
                && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        if (candidates.Count == 0)
        {
            // 全是空白项 —— 等价于宿主没给，回落到进程级定位。
            return null;
        }

        throw new FileNotFoundException(
            $"宿主给的素材根里没有一个是可用素材（候选 {candidates.Count} 个）：{string.Join(" | ", candidates)}"
            + $"；可用形态为含 {ManifestName} 的目录，或单个 .png 文件");
    }

    /// <summary>定位到的是 原规格 31 pack 目录（含 petdock-sample.json）还是单张贴图。</summary>
    public static bool IsPackRoot(string located) =>
        Directory.Exists(located) && File.Exists(Path.Combine(located, ManifestName));

    /// <summary>把一张静态贴图合成为单片段 pack（所有者贴图的诚实形态：1 帧、无动画可言）。</summary>
    public static PackData FromSingleImage(string imagePath)
    {
        using Bitmap image = new(imagePath);
        var clip = new AnimationClip("idle", 1, DefaultSingleImageFps, ClipLoop.Loop, Anchor: null);
        return new PackData(
            new PetSize(image.Width, image.Height),
            DefaultSingleImageFps,
            "idle",
            new[] { new ClipData(clip, new[] { Path.GetFileName(imagePath) }) });
    }

    /// <summary>单图形态的 fps：与 M0 的 SampleImageContent 同一约定（1Hz 重绘无视觉差异）。</summary>
    private const double DefaultSingleImageFps = 1.0;

    public static PackData Load(string packRoot)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packRoot, ManifestName)));
        JsonElement root = doc.RootElement;

        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        if (schemaVersion != 1)
        {
            throw new InvalidDataException($"{ManifestName} schemaVersion={schemaVersion}，本实现只读 1（原规格 31）");
        }

        var naturalSize = new PetSize(
            root.GetProperty("naturalSize").GetProperty("w").GetDouble(),
            root.GetProperty("naturalSize").GetProperty("h").GetDouble());
        double preferredFps = root.GetProperty("preferredFps").GetDouble();
        string defaultAnimation = root.GetProperty("defaultAnimation").GetString()
            ?? throw new InvalidDataException($"{ManifestName}: defaultAnimation 缺失");

        var clips = new List<ClipData>();
        foreach (JsonElement anim in root.GetProperty("animations").EnumerateArray())
        {
            string id = anim.GetProperty("id").GetString()
                ?? throw new InvalidDataException($"{ManifestName}: animations[].id 缺失");
            string clipFile = anim.GetProperty("clip").GetString()
                ?? throw new InvalidDataException($"{ManifestName}: animations[].clip 缺失");
            clips.Add(LoadClip(packRoot, id, clipFile));
        }

        PackData pack = new(naturalSize, preferredFps, defaultAnimation, clips);
        if (!pack.HasClip(defaultAnimation))
        {
            throw new InvalidDataException(
                $"{ManifestName}: defaultAnimation \"{defaultAnimation}\" 不在 animations 里");
        }

        return pack;
    }

    private static ClipData LoadClip(string packRoot, string id, string clipFile)
    {
        string clipPath = Path.Combine(packRoot, clipFile);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(clipPath));
        JsonElement root = doc.RootElement;

        double fps = root.GetProperty("fps").GetDouble();
        if (fps <= 0)
        {
            throw new InvalidDataException($"{clipFile}: fps={fps} 必须为正（原规格 11 §2.3）");
        }

        var frames = new List<string>();
        foreach (JsonElement frame in root.GetProperty("frames").EnumerateArray())
        {
            string rel = frame.GetString()
                ?? throw new InvalidDataException($"{clipFile}: frames[] 含非字符串项");
            if (!File.Exists(Path.Combine(packRoot, rel)))
            {
                throw new FileNotFoundException($"{clipFile}: 帧文件 {rel} 不存在（相对 pack 根 {packRoot}）");
            }

            frames.Add(rel);
        }

        if (frames.Count == 0)
        {
            throw new InvalidDataException($"{clipFile}: frames 为空（原规格 31 §3 每片段至少一帧）");
        }

        string loopRaw = root.GetProperty("loop").GetString()
            ?? throw new InvalidDataException($"{clipFile}: loop 缺失");
        ClipLoop loop = loopRaw switch
        {
            "once" => ClipLoop.Once,
            "loop" => ClipLoop.Loop,
            "pingpong" => ClipLoop.PingPong,
            _ => throw new InvalidDataException($"{clipFile}: loop \"{loopRaw}\" 不是 原规格 11:62 的 once|loop|pingpong"),
        };

        var clip = new AnimationClip(id, frames.Count, fps, loop, Anchor: null);
        return new ClipData(clip, frames);
    }

    /// <summary>解码 PNG 并转成 Format32bppPArgb（BitmapRegistry.Acquire 只收预乘位图；转换交给 GDI+ Clone，不自己逐像素乘）。</summary>
    public static Bitmap LoadPArgb(string path)
    {
        using Bitmap loaded = new(path);
        return loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format32bppPArgb);
    }
}
