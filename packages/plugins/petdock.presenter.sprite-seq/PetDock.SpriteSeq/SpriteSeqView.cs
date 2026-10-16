using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using PetDock.Core.PetRuntime;

namespace PetDock.Builtins;

/// <summary>
/// 内置演示视图（贡献点 id "petdock.presenter.sprite-seq"，docs/11 §13）：
/// 读 docs/31 Sample Pack（fixtures/sample-pet/）的 PNG 序列 + anim/*.json，
/// 按 clip 声明的 fps 与 loop 产出帧。
/// QueryFrame 保持 docs/11:100 的同步纯函数语义：同一 now、同一 clip、同一画板尺寸
/// → 同一位图对象、同像素（docs/11:120 的可判定基础，也是 --animtest C2/C3 的前提）。
/// </summary>
public sealed class SpriteSeqView : IPetView, IClipCatalog
{
    /// <summary>运行期贡献点 id：逐字等于 docs/11:310（连字符在这里合法；manifest 的 id 字段是另一件事，
    /// 见 .dsh-code/M2-contract-frozen.md §11.2——本值不是 manifest id，不参与 docs/21 pattern）。</summary>
    public InterfaceMeta Meta => new("petdock.presenter.sprite-seq", "1.0.0");

    private PackData? _pack;
    private readonly Dictionary<string, Bitmap> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _composed = new(StringComparer.Ordinal);
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
            return Task.CompletedTask; // 幂等：重复 init 不重读盘（docs/11:119 的读盘只发生在 init 侧）
        }

        // 素材来源由 PackLoader 的三条优先级定位（PETDOCK_SAMPLE_PACK → PETDOCK_PET_IMAGE →
        // 默认 fixtures/sample-pet），宿主不写插件 id 特判，所以这里不按相位分流：
        // 自检相位与正常启动解析到同一个默认包，「桌面上是哪一只」不因测试而变。
        string located = PackLoader.Locate(context.PetInstanceId);
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
        // 素材来源写进 stderr：插件调不到内核的 KernelLog（internal），而 resident 相位没有
        // 附加控制台，所以自检面（--animtest/--hosttest/--drivetest）里这一行就是
        // 「桌面上是哪一只」的可指名证据。t8 验收 2/3 读它。
        Console.Error.WriteLine(
            $"sprite-seq source={located} form={(PackLoader.IsPackRoot(located) ? "pack" : "single-image")}"
            + $" clips={pack.Clips.Count} instance={context.PetInstanceId}"
            + $" default={(PackLoader.IsDefaultLocation(located) ? "docs/31-sample-pack" : "explicit")}");
        return Task.CompletedTask;
    }

    public PetDescription Describe()
    {
        PackData pack = RequirePack();
        return new PetDescription(
            pack.NaturalSize,
            pack.PreferredFps,
            RequiresHitmap: false,
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
        // restart 不引入「起始时刻」状态：帧相位是 now 的纯函数（docs/11:120），
        // 一旦相位依赖调用历史，「同一 now 同一帧」就要先问调用顺序，C2 判据即失守。
        // loop 片段的相位天然绕回（docs/31 三片段均为 loop），restart 无可重置之物。
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
        return new Frame(handle, new PetSize(target.PixelWidth, target.PixelHeight), target.Scale);
    }

    public void OnEvent(PetEvent evt)
    {
        // sprite-seq 无事件行为（docs/31 §5：交互起步为空）。
    }

    /// <summary>
    /// 帧相位：Round(now·fps/1000)（AwayFromZero）再按 loop 语义折回。
    /// 用半帧取整而非 floor 的原因：--animtest 的 C3 在周期边界附近用整毫秒采样，
    /// hover 的 period=2*1000/6≈333.33ms 处，floor 会把采样点 333ms 映到帧 1 而非绕回帧 0；
    /// 半帧取整把它映射到 2→帧 0，对整毫秒采样稳定绕回（边界误差 ≤0.5·fps/1000 ≤ 0.03 帧），
    /// 且「同一 now 同一帧」不受影响。docs/11:62 只冻结 loop 三值，未冻结相位映射。
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
    /// 粘贴、余处保持透明（docs/11:53 alpha=0 穿透语义，悬停命中只看不透明区）；缩放居中会让不同
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
}
