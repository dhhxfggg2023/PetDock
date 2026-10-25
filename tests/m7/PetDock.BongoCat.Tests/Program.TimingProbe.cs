// M7 时序缺口探针（`--timing-probe`）：把两条「只在负载下才显形」的时序问题，
// 变成**不需要负载、每次都能复现**的读数。
//
// 为什么需要它：门禁里的 m7 判据在空闲机器上几乎不红，负载下又红得随机（红的 CHECK 名
// 甚至每次不同），于是「修好了没有」无从判断。本探针不复刻抖动，而是直接问两个是非题：
//
//   ① 帧往返预算：宿主路径（PluginHost.Tier2.cs:27）构造 Tier2Sandbox 时不传 frameBudgetMs，
//      落到 Tier2Sandbox.cs:54 的默认 8ms；一个 20ms 才答一帧的客体因此被
//      StopProcess 杀掉（Tier2Sandbox.cs:221-227）。发问：这样一次调用「成功了吗、客体还活着吗」。
//   ② 会话状态：客体「应答后自杀」且宿主侧已确认进程退出（IsLoaded=false）时，
//      再发一次请求会走 Tier2Sandbox.cs:182-185 的早退，它只回一行 "process-not-running"
//      而不 NoteCrash → Session 仍停在 Running（而会话文件的判据是「进程没了 = Crashed」）。
//      发问：进程已死之后，Session 说实话了吗。
//
// 两个问题都是「缺口存在 = present」。缺口的判定**不改任何产品行为**，只读状态：
// 生产侧修好（给宿主路径真实 IPC 超时、早退路径记 Crashed）之后，本探针的末行应是 M7-PROBE-OK。
//
// 用法（不在门禁路径上）：
//   dotnet tests/m7/PetDock.BongoCat.Tests/bin/Release/net8.0-windows/PetDock.BongoCat.Tests.dll --timing-probe
// 末行 M7-PROBE-OK / M7-PROBE-GAPS n=<k>，退出码 0/1。
using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PetDock.Core.Sandbox;

namespace PetDock.BongoCat.Tests;

internal static partial class Program
{
    private static int RunTimingProbe()
    {
        string command = Tier2GuestCommand();
        var sink = new CollectingSink();
        var gate = new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink);
        int gaps = 0;

        // ① 帧往返：构造与 LoadTier2Sandbox 同形（**不传** budget），客体会慢 20ms 答一帧。
        var frame = new Tier2Sandbox(gate, sink, command, Tier2GuestArgs("wire"));
        bool frameUp = frame.Start();
        frame.Call("probe.frame-script", new JsonObject { ["script"] = "slow" });
        bool frameOk = frame.TryQueryFrame(0, out Tier2Frame? _, out string? frameErr);
        bool frameGap = frameUp && (!frameOk || !frame.IsLoaded);
        if (frameGap)
        {
            gaps++;
        }
        Console.WriteLine($"PROBE frame-budget fatal={(frameGap ? "present" : "absent")}"
            + $" up={frameUp} ok={frameOk} err={frameErr} alive={frame.IsLoaded}"
            + $" timeouts={frame.Timeouts} session={frame.Session} events=[{EventSummary(sink.Events)}]");
        frame.Dispose();

        // ② 会话状态：客体应答一次就自杀，然后**等真事件**（宿主侧 Process 已退出）再发请求。
        var crash = new Tier2Sandbox(gate, sink, command, Tier2GuestArgs("crash"), requestTimeoutMs: 2000);
        bool crashUp = crash.Start();
        Tier2Reply answered = crash.Call("probe", new JsonObject());
        var waited = Stopwatch.StartNew();
        while (crash.IsLoaded && waited.ElapsedMilliseconds < 5000)
        {
            System.Threading.Thread.Yield();
        }
        bool confirmedDead = !crash.IsLoaded;
        Tier2Reply afterDeath = crash.Call("probe", new JsonObject());
        bool stale = confirmedDead && crash.Session == Tier2SessionState.Running;
        if (stale)
        {
            gaps++;
        }
        Console.WriteLine($"PROBE session-state fatal={(stale ? "present" : "absent")}"
            + $" up={crashUp} answered={answered.Ok} dead={confirmedDead}"
            + $" call={afterDeath.Error} session={crash.Session} crashes={crash.Crashes}"
            + $" restarts={crash.Restarts} waitedMs={waited.ElapsedMilliseconds}");
        crash.Dispose();

        Console.WriteLine(gaps == 0 ? "M7-PROBE-OK" : $"M7-PROBE-GAPS n={gaps}");
        return gaps == 0 ? 0 : 1;
    }
}
