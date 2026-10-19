// --eventtest 的钩子静态面判据（拆出以守住 D-03c 的单文件 400 行上限）。
//
// 2026-10-19 之前 EventSelfTest 里这条判据是硬编码 `Check(..., true, "setwindowshookex-sites=0")`：
// 当时内核确实一个钩子都不装，所以那句话为真——但它不读任何东西，钩子接上之后就成了恒真断言。
// 恒真断言比没有断言更坏：它看起来在守一条规则，实际上永远不会红。
//
// 现在的判据是：内核 src/ 里「真的调用 SetWindowsHookEx」的行必须恰好一处。
// 多一处 = 有人绕过了 UiHost 的授权门（docs/16:56 要求设备钩子只在插件持权后激活）。
using System;
using System.IO;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    /// <summary>
    /// 数一遍内核源码里真正调用 SetWindowsHookEx 的行数（排除声明与注释）。
    /// 找不到仓库根时返回 -1 —— 那意味着这条判据没检查任何东西，必须红，不能静默通过。
    /// </summary>
    private static int CountHookInstallSites()
    {
        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                return -1;
            }

            string src = Path.Combine(dir.FullName, "packages", "core", "PetDock.Core", "src");
            if (!Directory.Exists(src))
            {
                return -1;
            }

            int sites = 0;
            foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                foreach (string raw in File.ReadLines(file))
                {
                    string line = raw.TrimStart();

                    // 排除三类：P/Invoke 声明（extern）、注释行、以及 <summary> 里的说明。
                    // 判据问的是「谁真的调用了它」——注释里提到这个名字不算安装点。
                    if (line.StartsWith("//", StringComparison.Ordinal)
                        || line.StartsWith("*", StringComparison.Ordinal)
                        || !line.Contains("SetWindowsHookEx", StringComparison.Ordinal)
                        || line.Contains("extern", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sites++;
                }
            }

            return sites;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
