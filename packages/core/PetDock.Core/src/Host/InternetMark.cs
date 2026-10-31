// 「来自互联网」标记（NTFS 的 Zone.Identifier 备用数据流）的清除。
//
// 2026-10-31：用户在 D:\download 解压后双击，看到「Windows 已保护你的电脑」，
// 来问「你放什么东西进去了」。那不是检出，是 SmartScreen 对**没签名 + 从网上下载**的文件的例行拦截，
// 而这个标记就是「从网上下载」的一半：资源管理器解压会把 zip 上的 Zone.Identifier
// 复制给每一个解出来的文件。
//
// 为什么宿主必须自己清一遍（UpdateStage.StageHelper 会调）：
//   搬运工是从 _update/helper/ 里的**副本**启动的，而 File.Copy **会把备用数据流一起带走**
//   （本次实测：Copy-Item 与 File.Copy 都保留它）。那一刻宿主进程已经退出、面板早就关了，
//   没有窗口能告诉用户「去点『更多信息 → 仍要运行』」—— 用户看到的是「点了更新，程序不见了」。
//   所以在拷贝之后就地清掉，让搬运工的启动路径上没有这个标记。
//
// 为什么用 P/Invoke 而不是 File.Delete：.NET 的路径规范化不接受带冒号的备用数据流路径，
// File.Delete("x:Zone.Identifier") 抛 NotSupportedException「不支持给定路径的格式」（已实测）。
// Win32 的 DeleteFile 才是资源管理器「解除锁定」用的那件事。
using System;
using System.Runtime.InteropServices;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class InternetMark
{
    private const string Zone = ":Zone.Identifier";

    /// <summary>删一个不存在的流返回 false 与 ERROR_FILE_NOT_FOUND —— 那不是错误，是「本来就没有」。</summary>
    private const int FileNotFound = 2;

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string name);

    /// <summary>
    /// 清掉这个文件的「来自互联网」标记。清不掉**不影响更新本身**：
    /// 最坏的结果只是用户多点一次 SmartScreen，所以这里只记日志、不向上抛。
    /// </summary>
    internal static void Clear(string path)
    {
        try
        {
            if (DeleteFileW(path + Zone))
            {
                return;
            }

            int error = Marshal.GetLastWin32Error();
            if (error != FileNotFound)
            {
                KernelLog.Write($"update-unblock-failed {error}");
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 NTFS（FAT32 的 U 盘、部分网络盘）根本没有备用数据流，也就没有标记要清。
            KernelLog.Write($"update-unblock-unavailable {error.GetType().Name}");
        }
    }
}
