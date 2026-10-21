// 应用自带的示例素材包的定位（RFC-16 §3.2 第 2 步）。
//
// 为什么它单独成类：引导的第 2 步要把「一只真的能动的宠物」放进用户的数据区，
// 而示例素材住在**发行物里**（assets/sample-pet），不在用户数据区。
// 谁去找这个目录，是一件与「引导窗长什么样」无关的事 —— 拆开才测得到。
//
// 定位规则与视图实现侧（PackLoader）同形态：从 AppContext.BaseDirectory 逐级向上找。
// core 不解析素材内容：这里只回答「这个目录在哪儿」（D-03b：素材格式的知识全在实现方）。
// 找不到**不是错误**：发行物可能没带示例（那正是 M9 安装包那条欠账），
// 引导据此显示「发布物里没有示例素材」，而不是抛异常。
using System;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>应用自带示例素材包的定位（只定位，不复制、不解析）。</summary>
internal static class BundledExampleAssets
{
    /// <summary>示例包在发行物里的相对路径（与发布装配写出去的路径一致）。</summary>
    public const string PackRelativePath = "assets/sample-pet";

    /// <summary>示例包的清单文件名（core 只认这个文件名，不读它的内容）。</summary>
    public const string PackManifestFile = "petdock-sample.json";

    /// <summary>环境变量名：自检与自定义部署可以显式指名，不必依赖目录布局。</summary>
    public const string PackEnvVar = "PETDOCK_BUNDLED_EXAMPLE";

    /// <summary>
    /// 定位自带示例包所在目录。找不到返回 null —— 调用方据此显示内联说明，不抛。
    /// 查找顺序：环境变量 → 从程序目录逐级向上找 assets/sample-pet。
    /// </summary>
    public static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable(PackEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && IsPackRoot(env))
        {
            return Path.GetFullPath(env);
        }

        string? baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return null;
        }

        try
        {
            for (DirectoryInfo? dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(
                    dir.FullName,
                    PackRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (IsPackRoot(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception error)
        {
            // 路径形态异常（如已到 UNC 根）—— 放弃向上探测，如实记一行。
            KernelLog.Write($"bundled-example-locate-failed {error.GetType().Name}: {error.Message}");
        }

        KernelLog.Write("bundled-example-locate-miss");
        return null;
    }

    /// <summary>这个目录是不是一个可用的示例包（存在且含清单文件）。</summary>
    public static bool IsPackRoot(string dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && Directory.Exists(dir)
        && File.Exists(Path.Combine(dir, PackManifestFile));
}
