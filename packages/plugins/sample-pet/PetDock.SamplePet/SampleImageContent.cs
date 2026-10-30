// 实现 原规格 40 §1.1 的分层归属：素材的寻找、解码、缩放属于 plugins 层（「所有宠物」），
// 内核只收像素+尺寸（原规格 40 §3.1 D-03b）。本类把仓库 assets/sample 下的静态图加载为
// IFrameContentSource，经注入交给内核 LayeredFrameWindow —— 内核对文件一无所知。
using System.Drawing;
using System.Drawing.Drawing2D;
using PetDock.Core.Compositor;

namespace PetDock.SamplePet;

/// <summary>
/// 示例内容源：定位仓库根（原规格 50 标记），读 assets/sample/pet-sample.png，
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
        string path = ImagePath();
        try
        {
            if (!File.Exists(path))
            {
                // 明确 unavailable：不再回落到任何仓库内占位图（t8 验收 3 已把它删掉）。
                // 文案带 placeholder 字样，日志据此标注「这是代码占位，不是宠物」。
                note = $"unavailable: {path} missing (placeholder: kernel draws a code placeholder)";
                return null;
            }

            note = "loaded " + path;
            return new SampleImageContent(new Bitmap(path));
        }
        catch (Exception error)
        {
            note = "unavailable: " + error.Message;
            return null;
        }
    }

    /// <summary>
    /// 示例图路径：只认所有者本机自用覆盖件 assets/sample/local/pet-sample.png（.gitignore，
    /// 见账本 decision「所有者自留素材仅限本机」）。仓库内那张 898 字节的程序化蓝团已删除
    /// （t8 验收 3：它会被误当作宠物图），所以这里不再有任何回落——本地图不存在就是
    /// 真的没有图，TryLoad 走 unavailable，由内核画代码占位图并在日志标注 placeholder。
    /// 显式指名请用 sprite-seq 的 PETDOCK_PET_IMAGE（单图也能作可见宠物）。
    /// </summary>
    private static string ImagePath()
    {
        string root = FindRepoRoot();
        return System.IO.Path.Combine(root, "assets", "sample", "local", "pet-sample.png");
    }

    /// <summary>从 AppContext.BaseDirectory 逐级向上找仓库根：存在 原规格 50 即认为是根。</summary>
    private static string FindRepoRoot()
    {
        // 沙箱 / CI 里 exe 常被建在仓库外的临时目录（obj、bin 都不可写），
        // 逐级向上查不可能命中仓库根；显式覆盖优先，但仍要求标记文件存在，避免指到错目录后被静默接受。
        string? overridden = Environment.GetEnvironmentVariable("PETDOCK_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden)
            && File.Exists(System.IO.Path.Combine(overridden, "PetDock.sln")))
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
            if (File.Exists(System.IO.Path.Combine(current.FullName, "PetDock.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"从 {directory} 向上逐级查找均未找到 原规格 50，无法定位仓库根");
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
