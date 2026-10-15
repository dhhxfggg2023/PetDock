// 实现 docs/40 §1.1 的分层归属：素材的寻找、解码、缩放属于 plugins 层（「所有宠物」），
// 内核只收像素+尺寸（docs/40 §3.1 D-03b）。本类把仓库 assets/sample 下的静态图加载为
// IFrameContentSource，经注入交给内核 LayeredFrameWindow —— 内核对文件一无所知。
using System.Drawing;
using System.Drawing.Drawing2D;
using PetDock.Core.Compositor;

namespace PetDock.SamplePet;

/// <summary>
/// 示例内容源：定位仓库根（docs/50-roadmap.md 标记），读 assets/sample/pet-sample.png，
/// 按帧缓冲尺寸等比放大居中。每帧由内核按需取一次；放大结果按尺寸缓存，帧泵重复取不重采样。
/// </summary>
internal sealed class SampleImageContent : IFrameContentSource
{
    private readonly Bitmap _image;
    private Bitmap? _scaled;
    private Size _scaledSize;

    private SampleImageContent(Bitmap image)
    {
        _image = image;
    }

    /// <summary>
    /// 尝试加载示例图。找不到或解析失败时返回 null 并附一条说明，调用方决定兜底方式
    /// （自检内核路径没有素材也能 SELF-OK，见 SolidFrameContent）。
    /// </summary>
    public static SampleImageContent? TryLoad(out string note)
    {
        try
        {
            note = "loaded " + ImagePath();
            return new SampleImageContent(new Bitmap(ImagePath()));
        }
        catch (Exception error)
        {
            note = "unavailable: " + error.Message;
            return null;
        }
    }

    /// <summary>
    /// 示例图路径。优先取本机自用覆盖件 assets/sample/local/pet-sample.png（该目录被 .gitignore
    /// 排除，只供所有者本机使用，见账本 decision 2026-10-15「鲸鱼娘二创仅限本机」）；
    /// 不存在时回落到仓库内的自有资产 assets/sample/pet-sample.png（程序化生成，可发布）。
    /// 两个文件的内核契约完全相同：128x128、四角 alpha=0、中心不透明。
    /// </summary>
    private static string ImagePath()
    {
        string root = FindRepoRoot();
        string local = System.IO.Path.Combine(root, "assets", "sample", "local", "pet-sample.png");
        if (File.Exists(local))
        {
            return local;
        }

        return System.IO.Path.Combine(root, "assets", "sample", "pet-sample.png");
    }

    /// <summary>从 AppContext.BaseDirectory 逐级向上找仓库根：存在 docs/50-roadmap.md 即认为是根。</summary>
    private static string FindRepoRoot()
    {
        // 沙箱 / CI 里 exe 常被建在仓库外的临时目录（obj、bin 都不可写），
        // 逐级向上查不可能命中仓库根；显式覆盖优先，但仍要求标记文件存在，避免指到错目录后被静默接受。
        string? overridden = Environment.GetEnvironmentVariable("PETDOCK_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden)
            && File.Exists(System.IO.Path.Combine(overridden, "docs", "50-roadmap.md")))
        {
            return overridden;
        }

        string? directory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            throw new DirectoryNotFoundException("无法确定程序基目录，无法定位仓库根");
        }

        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "docs", "50-roadmap.md")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"从 {directory} 向上逐级查找均未找到 docs/50-roadmap.md，无法定位仓库根");
    }

    /// <summary>
    /// 取一帧：把示例图等比放大居中到帧缓冲尺寸（宁可两侧留透明边，不拉变形），
    /// 按尺寸缓存。返回的位图在下一次调用前保持有效。
    /// </summary>
    public Bitmap? GetFrameContent(Size frameSize)
    {
        if (frameSize.Width <= 0 || frameSize.Height <= 0)
        {
            return null;
        }

        if (_scaled is not null && _scaledSize == frameSize)
        {
            return _scaled;
        }

        double scale = Math.Min(frameSize.Width / (double)_image.Width, frameSize.Height / (double)_image.Height);
        int innerWidth = Math.Max(1, (int)Math.Round(_image.Width * scale));
        int innerHeight = Math.Max(1, (int)Math.Round(_image.Height * scale));

        var scaled = new Bitmap(frameSize.Width, frameSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_image, new Rectangle(
                (frameSize.Width - innerWidth) / 2, (frameSize.Height - innerHeight) / 2, innerWidth, innerHeight));
        }

        _scaled?.Dispose();
        _scaled = scaled;
        _scaledSize = frameSize;
        return scaled;
    }

    public void Dispose()
    {
        _scaled?.Dispose();
        _scaled = null;
        _image.Dispose();
    }
}
