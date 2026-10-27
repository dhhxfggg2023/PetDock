// 实现 原规格 12 §6「可执行检查」所需的诊断输出：无控制台时静默丢弃，有控制台时写一行
using System.Runtime.InteropServices;

namespace PetDock.Core.Compositor;

/// <summary>
/// 日志落盘口（RFC-17 §3.1）。KernelLog 只认这个窄接口：
/// 落盘层住在 Host 层，Compositor 层不能反向依赖它。
/// </summary>
public interface IKernelLogSink
{
    /// <summary>写一行。实现**不得**把异常抛给调用点（日志失败不该带走业务）。</summary>
    void Write(string message);
}

/// <summary>
/// 极简日志出口。常驻模式通常没有控制台，此时写操作静默丢弃；
/// --selftest 会先接上父进程控制台，日志即可随 KEY value 输出一起被机器读取。
/// </summary>
internal static class KernelLog
{
    private static readonly object Gate = new();

    /// <summary>是否有控制台可写（Program.cs 在自检模式下设置）。</summary>
    public static bool ConsoleAttached { get; set; }

    /// <summary>
    /// 落盘口（可空，RFC-17 §3.1）。没装上落盘层时行为与今天完全一致：
    /// 无控制台就静默丢弃。装上之后，无控制台时把行交给落盘层。
    /// 这里刻意只暴露一个**窄接口**而不是具体落盘层类型：落盘层在 Host 层，
    /// 而 KernelLog 在 Compositor 层，依赖方向不能反过来。
    /// </summary>
    public static IKernelLogSink? Sink { get; set; }

    /// <summary>写一行诊断日志。</summary>
    public static void Write(string message)
    {
        if (!ConsoleAttached)
        {
            // 常驻形态的落盘口（RFC-17 §3.1）：没有控制台不等于没有日志。
            Sink?.Write(message);
            return;
        }

        lock (Gate)
        {
            Console.Error.WriteLine($"kernel {message}");
        }

        // 有控制台时两处都写：自检命令的机器可读输出不能因此丢掉。
        Sink?.Write(message);
    }
}
