// 自检的临时落点（2026-10-30）：系统临时目录不可写时，退到构建输出目录。
//
// 为什么需要它：判据要造临时夹具（导入包、设置文件、流水目录…），历史上都写 `Path.GetTempPath()`。
// 本机实测**受限令牌的子进程**在 %TEMP% / Documents / AppData 下建目录一律 UnauthorizedAccessException
// （.NET 自己的 Directory.CreateDirectory 直接抛，与权限位无关；同一进程写工作区正常）。
// 那种环境下 --uitest 一次红 23 条，红的是环境不是代码 —— 门禁于是变得读不懂。
//
// 口径（一次探测，进程内缓存）：
//   1. 先试系统临时目录：真的建目录、写文件、删掉。成功就用它（正常机器走这条）。
//   2. 失败则退到 `AppContext.BaseDirectory/selftest-scratch`：构建输出目录，被 .gitignore 覆盖
//      （[Bb]in/），随构建重建，不进源码树、不会被提交。
// 落点**如实打进行里**（见 UiSelfTest 的 STAT selftest-scratch 行）：读的人才知道这次跑在哪。
//
// 这一层只决定「夹具放哪」，不改任何判据的结论口径：`ui-audit-trail-not-in-repo` 仍然断言
// 流水不落进仓库源码树；退到构建输出目录时它按声明过的构建产物目录判，见 AuditTrailChecks ⑨。
using System;
using System.IO;

namespace PetDock.Hosting;

internal static class SelfTestScratch
{
    private static readonly Lazy<Probe> Once = new(Measure);

    /// <summary>判据夹具的根。首次使用时探测，之后复用。</summary>
    internal static string Base => Once.Value.Root;

    /// <summary>落点来源（system-temp / build-output / none）。</summary>
    internal static string Source => Once.Value.Source;

    /// <summary>探测读数（原始值，不是自称）。</summary>
    internal static string Reading => Once.Value.Reading;

    /// <summary>是否退到了构建输出目录。</summary>
    internal static bool IsFallback => Once.Value.Source == "build-output";

    /// <summary>夹具根下的一次性目录（调用方自己负责删除）。</summary>
    internal static string NewDir(string prefix)
    {
        string dir = Path.Combine(Base, prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed record Probe(string Root, string Source, string Reading);

    private static Probe Measure()
    {
        string system = Path.GetTempPath();
        string? whySystem = TryUse(system);
        if (whySystem is null)
        {
            return new Probe(system, "system-temp", "base=" + system + " probe=write-ok");
        }

        string fallback = Path.Combine(AppContext.BaseDirectory, "selftest-scratch");
        string? whyFallback = TryUse(fallback);
        if (whyFallback is null)
        {
            return new Probe(fallback, "build-output",
                "base=" + fallback + " probe=write-ok system-temp=" + whySystem);
        }

        // 两处都写不进去：仍旧返回系统临时目录，让判据照常红，而且红得带读数。
        return new Probe(system, "none",
            "base=" + system + " probe=unwritable system-temp=" + whySystem + " build-output=" + whyFallback);
    }

    /// <summary>能建目录 + 写文件 + 删掉就返回 null；否则返回失败读数（异常类型名）。</summary>
    private static string? TryUse(string root)
    {
        string probe = Path.Combine(root, "scratch-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(probe);
            File.WriteAllText(Path.Combine(probe, "probe.txt"), "ok");
            Directory.Delete(probe, recursive: true);
            return null;
        }
        catch (Exception error)
        {
            TryDelete(probe);
            return error.GetType().Name;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清不掉不影响结论。
        }
    }
}
