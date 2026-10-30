// 单实例锁的判据（RFC-32 §4）。命令形态 `--uitest`，判据行 `CHECK ui-single-instance-*`。
//
// 判据为什么只测锁、不测「第二个进程真的退出了」：跨进程判据要给子进程单独装夹具与超时，
// 而锁的语义（第一次拿到、同名第二次拿不到、释放后能再拿）在本进程内就是全部真相；
// 「用户形态才会去抢锁」这一条由 Program.Main 的分派点决定，属 RFC-32 §7 的欠账。
using System;

namespace PetDock.Hosting;

internal static class InstanceChecks
{
    /// <summary>判据出口（与 SettingsChecks/PerfChecks 同形）。</summary>
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        // 用一次性名字：绝不与正在跑的 PetDock 抢生产名（否则判据会随「本机是否开着宠物」变色）。
        string name = @"Local\PetDock.SelfTest." + Guid.NewGuid().ToString("N");
        try
        {
            var first = new SingleInstance(name);
            check("ui-single-instance-first", first.IsFirst, $"name={name} first={first.IsFirst}");

            var second = new SingleInstance(name);
            check("ui-single-instance-refused", !second.IsFirst, $"name={name} second={second.IsFirst}");

            second.Dispose();
            first.Dispose();

            var third = new SingleInstance(name);
            check("ui-single-instance-released", third.IsFirst, $"name={name} third={third.IsFirst}");
            third.Dispose();
        }
        catch (Exception error)
        {
            check("ui-single-instance-crash", false, $"{error.GetType().Name}: {error.Message}");
        }
    }
}
