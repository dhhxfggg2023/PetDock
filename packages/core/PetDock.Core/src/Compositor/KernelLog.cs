// 实现 docs/12 §6「可执行检查」所需的诊断输出：无控制台时静默丢弃，有控制台时写一行
using System.Runtime.InteropServices;

namespace PetDock.Core.Compositor;

/// <summary>
/// 极简日志出口。常驻模式通常没有控制台，此时写操作静默丢弃；
/// --selftest 会先接上父进程控制台，日志即可随 KEY value 输出一起被机器读取。
/// </summary>
internal static class KernelLog
{
    private static readonly object Gate = new();

    /// <summary>是否有控制台可写（Program.cs 在自检模式下设置）。</summary>
    public static bool ConsoleAttached { get; set; }

    /// <summary>写一行诊断日志。</summary>
    public static void Write(string message)
    {
        if (!ConsoleAttached)
        {
            return;
        }

        lock (Gate)
        {
            Console.Error.WriteLine($"kernel {message}");
        }
    }
}
