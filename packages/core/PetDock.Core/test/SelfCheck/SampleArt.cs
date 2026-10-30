// 自检用的「真素材」加载器（2026-10-28）：所有者指出自检里用纯色几何图当宠物，
// 看图上分不出这是宠物还是占位块。这里统一从 `assets/sample/local/pet-sample.png`
// 取真实示例图，按目标尺寸等比缩放居中。
//
// 为什么回落而不是直接失败：该图是本机自用覆盖件（.gitignore，见账本 decision
// 「鲸鱼娘二创仅限本机」），别人的检出里可能没有。缺图时调用方仍能跑完判据，
// 但要把 note 打进读数 —— 让「这是占位」写在证据里，而不是靠看图猜。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 与其它自检夹具同属一个命名空间（PetDock.Hosting），这样夹具文件不必逐个加 using。
namespace PetDock.Hosting;

/// <summary>示例素材的加载与缩放（按尺寸缓存；线程安全只对本进程的自检线程用）。</summary>
internal static class SampleArt
{
    /// <summary>相对仓库根的示例图路径（所有者本机覆盖件）。</summary>
    private const string RelativePath = "assets/sample/local/pet-sample.png";

    /// <summary>
    /// 本机素材指针（可选）：`assets/sample/local/sample-art.txt` 第一行写一张图的路由。
    /// 为什么用文件而不是把路径写进源码：更完整的本机素材包不可入库（授权不明），
    /// 而它的目录名/格式名进了内核源码就会撞 原规格 02 §2 的黑名单。
    /// 指针文件在 .gitignore 的目录里，只有这台机器读得到。
    /// </summary>
    private const string PointerPath = "assets/sample/local/sample-art.txt";

    private static readonly Dictionary<Size, Bitmap> Frames = new();
    private static readonly object Gate = new();
    private static Bitmap? _source;
    private static string _note = "not-loaded";
    private static string _notePath = RelativePath;

    /// <summary>上次定位的说明（供判据读数：loaded … / unavailable …）。</summary>
    public static string Note => _note;

    /// <summary>
    /// 取一帧目标尺寸的示例图。找不到素材时返回 null，调用方自行决定兜底画法。
    /// </summary>
    public static Bitmap? TryFrame(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // 判据与窗口泵在不同线程上取图（宠物夹具在 UI 线程、性能判据在自检线程），
        // 而 Dictionary 不是线程安全的：2026-10-28 没加锁时 --uitest 偶发 0xC0000005。
        lock (Gate)
        {
            Bitmap? source = Source();
            if (source is null)
            {
                return null;
            }

            var size = new Size(width, height);
            if (Frames.TryGetValue(size, out Bitmap? cached))
            {
                return cached;
            }

            var scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(scaled))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 等比缩放居中：示例图是 1:1 立绘，拉伸会把人画扁。
                double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
                int drawWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
                int drawHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
                graphics.DrawImage(source, new Rectangle((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight));
            }

            Frames[size] = scaled;
            _note = $"loaded {_notePath} src={source.Width}x{source.Height} scaled={width}x{height}";
            return scaled;
        }
    }

    private static Bitmap? Source()
    {
        if (_source is not null)
        {
            return _source;
        }

        string? path = Locate();
        if (path is null)
        {
            return null;
        }

        try
        {
            _source = new Bitmap(path);
            _notePath = path;
            return _source;
        }
        catch (Exception error)
        {
            _note = $"unavailable {path}: {error.GetType().Name}: {error.Message}";
            return null;
        }
    }

    private static string? Locate()
    {
        string? overridden = Environment.GetEnvironmentVariable("PETDOCK_SAMPLE_ART");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return overridden;
        }

        // 从可执行目录往上找仓库根（以 assets 目录为标记）：dotnet exec 的 base 在 bin/Debug 下。
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            string assetsRoot = Path.Combine(directory.FullName, "assets");
            if (Directory.Exists(assetsRoot))
            {
                string? pointed = Pointed(directory.FullName);
                if (pointed is not null)
                {
                    return pointed;
                }
            }

            string candidate = Path.Combine(directory.FullName, RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        _note = $"unavailable {RelativePath} not found above {AppContext.BaseDirectory}";
        return null;
    }

    /// <summary>读本机指针文件；读不到/指向不存在就返回 null（继续走默认图）。</summary>
    private static string? Pointed(string repoRoot)
    {
        string pointer = Path.Combine(repoRoot, PointerPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(pointer))
        {
            return null;
        }

        try
        {
            foreach (string line in File.ReadAllLines(pointer))
            {
                string text = line.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                string resolved = Path.IsPathRooted(text) ? text : Path.Combine(repoRoot, text);
                return File.Exists(resolved) ? resolved : null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }
}