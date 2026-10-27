// --eventtest 的钩子静态面判据（拆出以守住 D-03c 的单文件 400 行上限）。
//
// 2026-10-19 之前 EventSelfTest 里这条判据是硬编码 `Check(..., true, "setwindowshookex-sites=0")`：
// 当时内核确实一个钩子都不装，所以那句话为真——但它不读任何东西，钩子接上之后就成了恒真断言。
// 恒真断言比没有断言更坏：它看起来在守一条规则，实际上永远不会红。
//
// 现在的判据是：内核 src/ 里「真的调用 SetWindowsHookEx」的行必须恰好一处。
// 多一处 = 有人绕过了 UiHost 的授权门（原规格 16:56 要求设备钩子只在插件持权后激活）。
// 遍历本身在 SourceScan.cs：同一次遍历现在服务两条静态面判据（这条与 EventSelfTest.Skip.cs
// 的事件生产点扫描），少一个拥有者就少一处会漂的地方。
using System;

namespace PetDock.Hosting;

internal static partial class EventSelfTest
{
    /// <summary>
    /// 数一遍内核源码里真正调用 SetWindowsHookEx 的行数（排除声明与注释）。
    /// 找不到仓库根时返回 -1 —— 那意味着这条判据没检查任何东西，必须红，不能静默通过。
    /// </summary>
    private static int CountHookInstallSites() =>
        SourceScan.Count(SourceScan.CoreSourceDir, line =>
            !line.StartsWith("//", StringComparison.Ordinal)
            && !line.StartsWith("*", StringComparison.Ordinal)
            && line.Contains("SetWindowsHookEx", StringComparison.Ordinal)
            && !line.Contains("extern", StringComparison.Ordinal));
}
