using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using PetDock.Core.PetRuntime;

namespace PetDock.Builtins;

/// <summary>
/// 内置演示视图（贡献点 id "petdock.presenter.sprite-seq"，原规格 11 §13）：
/// 读 原规格 31 Sample Pack（fixtures/sample-pet/）的 PNG 序列 + anim/*.json，
/// 按 clip 声明的 fps 与 loop 产出帧。
/// QueryFrame 保持 原规格 11:100 的同步纯函数语义：同一 now、同一 clip、同一画板尺寸
/// → 同一位图对象、同像素（原规格 11:120 的可判定基础，也是 --animtest C2/C3 的前提）。
/// </summary>
public sealed class SpriteSeqView : IPetView, IClipCatalog
{
    /// <summary>运行期贡献点 id：逐字等于 原规格 11:310（连字符在这里合法；manifest 的 id 字段是另一件事，
    /// 见 交接档案 §11.2——本值不是 manifest id，不参与 原规格 21 pattern）。</summary>
    public InterfaceMeta Meta => new("petdock.presenter.sprite-seq", "1.0.0");

    private PackData? _pack;
    private readonly Dictionary<string, Bitmap> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _composed = new(StringComparer.Ordinal);

    // 命中图缓存：键与 _composed 同（clip#frame@画板），同一帧同一尺寸只有一张。
    private readonly Dictionary<string, byte[]> _hitmaps = new(StringComparer.Ordinal);

    /// <summary>
    /// 可命中的 alpha 下限（0..255）：≥25% 不透明才算「点在角色身上」。
    /// 取值理由：像素角色的边缘抗锯齿与投影常落在 1..32 这一带，按 0 判会让桌宠外面
    /// 一圈不可见的像素吃掉鼠标；按 128 判又会把半透明的耳朵/尾巴判成穿透。
    /// </summary>
    private const byte HitAlphaFloor = 64;
    private RenderTarget? _target;
    private string? _current;
    private bool _disposed;

    public IReadOnlyList<AnimationClip> Clips =>
        _pack?.Clips.Select(c => c.Clip).ToArray()
        ?? throw new InvalidOperationException("sprite-seq view used before InitAsync");

    public Task InitAsync(PetViewContext context)
    {
        if (_pack is not null)
        {
            return Task.CompletedTask; // 幂等：重复 init 不重读盘（原规格 11:119 的读盘只发生在 init 侧）
        }

        // 素材来源先看宿主为本实例解析好的素材根（按实例），再看进程级环境变量与默认探测（按进程）。
        // 显式路径优先于环境变量：全局变量没法表达「这只宠物用这份素材，那只用那份」。
        string? explicitRoot = PackLoader.LocateFromAssetRoots(context.AssetRoots);
        string located = explicitRoot ?? PackLoader.Locate(context.PetInstanceId);
        PackData pack;
        string imageDir;
        if (PackLoader.IsPackRoot(located))
        {
            imageDir = located;
            pack = PackLoader.Load(located);
        }
        else
        {
            imageDir = Path.GetDirectoryName(Path.GetFullPath(located))!;
            pack = PackLoader.FromSingleImage(located);
        }

        foreach (ClipData clip in pack.Clips)
        {
            for (int i = 0; i < clip.FramePaths.Count; i++)
            {
                _sources[FrameKey(clip.Clip.Id, i)] =
                    PackLoader.LoadPArgb(Path.Combine(imageDir, clip.FramePaths[i]));
            }
        }

        _pack = pack;
        // RFC-23 / 原规格 20 §3：插件侧的日志面。以前这里只能写 stderr —— 内核的日志出口是
        // internal，插件够不着（见下面那段的由来）。现在走 PluginLog：宿主按作用域把这一行
        // 记到本插件头上，常驻形态下落进诊断日志，warn/error 还会进审计流水。
        PluginLog.Info(
            $"sprite-seq ready clips={pack.Clips.Count}"
            + $" size={pack.NaturalSize.W:0.#}x{pack.NaturalSize.H:0.#}");
        // 素材来源写进 stderr：插件调不到内核的 KernelLog（internal），而 resident 相位没有
        // 附加控制台，所以自检面（--animtest/--hosttest/--drivetest）里这一行就是
        // 「桌面上是哪一只」的可指名证据。t8 验收 2/3 读它。
        Console.Error.WriteLine(
            $"sprite-seq source={located} form={(PackLoader.IsPackRoot(located) ? "pack" : "single-image")}"
            + $" clips={pack.Clips.Count} instance={context.PetInstanceId}"
            + $" source-kind={(explicitRoot is not null ? "explicit" : "env-or-default")}"
            + $" default={(PackLoader.IsDefaultLocation(located) ? "default-sample-pack" : "explicit")}");
        return Task.CompletedTask;
    }

    public PetDescription Describe()
    {
        PackData pack = RequirePack();

        // RFC-31 §4 / 原规格 11:111：RequiresHitmap=true 表示「alpha 不可信、必须给显式命中图」。
        // sprite-seq 的素材是带边缘抗锯齿与半透明阴影的 PNG：alpha 落在中间值的那一圈不该可点
        // （否则桌宠边缘外 2~3px 也会吃掉鼠标）。所以这一档由插件自己出命中图，逐像素按阈值判，
        // 内核不再替它猜阈值——见 Compose 里的 HitmapFor。
        return new PetDescription(
            pack.NaturalSize,
            pack.PreferredFps,
            RequiresHitmap: true,
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    public void Attach(RenderTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _current = null; // 重新挂接回落到 pack 默认片段
    }

    public void Detach() => _target = null;

    public Task DisposeAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _disposed = true;
        foreach (Bitmap bitmap in _sources.Values)
        {
            bitmap.Dispose();
        }

        foreach (Bitmap bitmap in _composed.Values)
        {
            bitmap.Dispose();
        }

        _sources.Clear();
        _composed.Clear();
        _hitmaps.Clear();
        _target = null;
        return Task.CompletedTask;
    }

    public bool SetAnimation(string clipId, bool restart = false)
    {
        if (_pack is null || !_pack.HasClip(clipId))
        {
            return false;
        }

        _current = clipId;
        // restart 不引入「起始时刻」状态：帧相位是 now 的纯函数（原规格 11:120），
        // 一旦相位依赖调用历史，「同一 now 同一帧」就要先问调用顺序，C2 判据即失守。
        // loop 片段的相位天然绕回（原规格 31 三片段均为 loop），restart 无可重置之物。
        return true;
    }

    public Frame QueryFrame(int nowMs)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SpriteSeqView));
        }

        RenderTarget target = _target
            ?? throw new InvalidOperationException("sprite-seq view queried before Attach");
        PackData pack = RequirePack();
        string clipId = _current ?? pack.DefaultAnimation;
        AnimationClip clip = pack.Clip(clipId).Clip;
        int index = FrameIndexOf(clip, nowMs);
        Bitmap frame = Compose(clipId, index, target);
        BitmapHandle handle = target.Bitmaps.Acquire(frame);
        return new Frame(
            handle,
            new PetSize(target.PixelWidth, target.PixelHeight),
            target.Scale,
            ClearRect: null,
            Hitmap: HitmapFor(clipId, index, frame));
    }

    public void OnEvent(PetEvent evt)
    {
        // sprite-seq 无事件行为（原规格 31 §5：交互起步为空）。
    }

    /// <summary>
    /// 帧相位：Round(now·fps/1000)（AwayFromZero）再按 loop 语义折回。
    /// 用半帧取整而非 floor 的原因：--animtest 的 C3 在周期边界附近用整毫秒采样，
    /// hover 的 period=2*1000/6≈333.33ms 处，floor 会把采样点 333ms 映到帧 1 而非绕回帧 0；
    /// 半帧取整把它映射到 2→帧 0，对整毫秒采样稳定绕回（边界误差 ≤0.5·fps/1000 ≤ 0.03 帧），
    /// 且「同一 now 同一帧」不受影响。原规格 11:62 只冻结 loop 三值，未冻结相位映射。
    /// </summary>
    internal static int FrameIndexOf(AnimationClip clip, int nowMs)
    {
        if (clip.Frames <= 0)
        {
            return 0;
        }

        double slots = nowMs * (clip.Fps / 1000.0);
        int n = (int)Math.Floor(slots + 0.5);
        return clip.Loop switch
        {
            ClipLoop.Loop => ((n % clip.Frames) + clip.Frames) % clip.Frames,
            ClipLoop.Once => Math.Clamp(n, 0, clip.Frames - 1),
            ClipLoop.PingPong => PingPong(n, clip.Frames),
            _ => throw new InvalidOperationException($"未知的 loop 语义 {clip.Loop}"),
        };
    }

    private static int PingPong(int n, int frames)
    {
        int period = 2 * frames - 2;
        int m = ((n % period) + period) % period;
        return m < frames ? m : period - m;
    }

    /// <summary>
    /// 合成目标尺寸的帧位图并缓存。1:1 时直接 DrawImageUnscaled（像素与磁盘 PNG 逐位一致，金图取 queryFrame(0)）。
    /// M3 契约 §2.7：帧间尺寸不一（真包 208×224 … 310×289）时**不再缩放居中**——小帧按左上角 (0,0)
    /// 粘贴、余处保持透明（原规格 11:53 alpha=0 穿透语义，悬停命中只看不透明区）；缩放居中会让不同
    /// 动作的角色忽大忽小。大于画板的帧（画板=全包最大帧，理论上不出现）防御性等比缩小，仍左上对齐。
    /// </summary>
    private Bitmap Compose(string clipId, int index, RenderTarget target)
    {
        string key = $"{FrameKey(clipId, index)}@{target.PixelWidth}x{target.PixelHeight}";
        if (_composed.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap source = _sources[FrameKey(clipId, index)];
        var frame = new Bitmap(target.PixelWidth, target.PixelHeight, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (source.Width == target.PixelWidth && source.Height == target.PixelHeight)
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            else if (source.Width <= target.PixelWidth && source.Height <= target.PixelHeight)
            {
                // 契约 §2.7：小帧左上 (0,0) 粘贴，不缩放（角色跨动作同尺寸），余处透明穿透。
                graphics.DrawImageUnscaled(source, 0, 0);
            }
            else
            {
                // 防御分支：帧大于画板时等比缩小到恰好放入，仍按左上对齐（不居中）。
                double scale = Math.Min(
                    target.PixelWidth / (double)source.Width,
                    target.PixelHeight / (double)source.Height);
                int w = (int)Math.Round(source.Width * scale);
                int h = (int)Math.Round(source.Height * scale);
                graphics.DrawImage(source, 0, 0, w, h);
            }
        }

        _composed[key] = frame;
        return frame;
    }

    private static string FrameKey(string clipId, int index) =>
        clipId + "#" + index.ToString(CultureInfo.InvariantCulture);

    private PackData RequirePack() =>
        _pack ?? throw new InvalidOperationException("sprite-seq view used before InitAsync");

    /// <summary>
    /// 由合成后的帧算命中图（每像素 0|1，尺寸 = 画板物理像素）。
    /// 只读 alpha 通道、不改像素：原规格 11:45「hitmap 只影响命中，不影响外观」。
    /// </summary>
    private byte[] HitmapFor(string clipId, int index, Bitmap frame)
    {
        string key = $"{FrameKey(clipId, index)}@{frame.Width}x{frame.Height}";
        if (_hitmaps.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        Rectangle bounds = new(0, 0, frame.Width, frame.Height);
        BitmapData data = frame.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var map = new byte[frame.Width * frame.Height];
        try
        {
            // 逐行拷进托管缓冲再判定：unsafe 指针在这个工程里不是既有约定，不为一张命中图开先例。
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                int outRow = y * frame.Width;
                for (int x = 0; x < frame.Width; x++)
                {
                    // Format32bppPArgb 的内存序是 B,G,R,A。
                    if (row[(x * 4) + 3] >= HitAlphaFloor)
                    {
                        map[outRow + x] = 1;
                    }
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        _hitmaps[key] = map;
        return map;
    }
}
