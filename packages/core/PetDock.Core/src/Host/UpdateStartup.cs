// 启动时「装不装已经下载好的新版本」这个决定（2026-10-31）。
//
// 用户的要求是两条：「下载完选择重启更新或者稍后更新」「稍后更新就是退出程序下次启动时更新」。
// 后者在这一片：**下一次启动**走到这里，发现有待安装的一份，就把它装掉。
//
// 为什么不是「退出时直接换文件」：
//   退出那一刻本进程还活着，而要被覆盖的正是它自己加载的 dll/exe。Windows 会拒绝，
//   半换的目录谁也起不来。所以搬运必须发生在本进程**已经退出**之后 —— 那是搬运工的活，
//   本片的职责只是「准备好、把意图写下、把进程交出去」。
//
// 顺序为什么是 HandOff → 写 restart.txt → 起搬运工 → ExitThread：
//   倒过来（先退出再指望有人搬）是最常见的一种错法：没有任何东西记得「退出去是要干什么」，
//   于是用户看到的是「程序关了，然后什么也没发生」。restart.txt 就是那个记得的人。
using System;
using System.Diagnostics;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>一次启动的结论：干什么、用哪一份、以及该对用户说什么。</summary>
internal sealed record UpdateStartPlan(UpdateStartStep Step, UpdatePending? Pending, string Detail)
{
    internal static UpdateStartPlan None { get; } = new(UpdateStartStep.None, null, "none");
}

internal static class UpdateStartup
{
    /// <summary>重启后要回到的界面（目前只有「打开主控面板」一种）。</summary>
    internal const string OpenPanelFlag = "--open-panel";

    /// <summary>
    /// 决定这一次启动与「待安装的那一份」的关系。只读判定，不搬文件、不弹窗、不联网：
    /// 它跑在消息泵起来之前，任何一步阻塞都会变成「双击了但没反应」。
    /// </summary>
    /// <param name="dataRoot">数据区根（null = 还没有数据区，什么都不做）。</param>
    /// <param name="probe">
    /// 探针形态（--residenttest / 性能采样）。**探针一律不参与更新**：门禁里没有人点更新，
    /// 而「启动时顺手装了个新版本」会让判据跑在一个会变的程序上，读数从此不可比。
    /// 而且探针不消费那份待安装的文件 —— 它只是不参与，不是放弃。
    /// </param>
    internal static UpdateStartPlan Decide(string? dataRoot, bool probe = false)
    {
        if (probe || string.IsNullOrEmpty(dataRoot))
        {
            return UpdateStartPlan.None;
        }

        UpdatePending? pending = UpdateStage.ReadPending(dataRoot);
        if (pending is null)
        {
            return UpdateStartPlan.None;
        }

        // 上一趟交接没跑完：搬运工被结束，或者用户在退出后又自己点开了程序。
        // 这时**绝不能**再装 —— 正在运行的就是要被覆盖的那份文件。
        string? request = UpdateStage.ReadRequest(dataRoot);
        if (!string.IsNullOrEmpty(request))
        {
            UpdateStage.ClearPending(dataRoot, withFiles: true);
            return new UpdateStartPlan(
                UpdateStartStep.Cancel, pending, "handoff-unfinished-" + request);
        }

        return new UpdateStartPlan(UpdateStartStep.HandOff, pending, "handoff v" + pending.Version);
    }

    /// <summary>
    /// 交接的前半段：把「要装哪一份、装完要再开」写下来，并确认搬运工真的在。
    /// **不搬文件、不起进程**，所以判据可以直接驱动它，断言磁盘上留下的那两份标记。
    ///
    /// 任何一步失败都不动现场（除了不写请求）：更新没准备好不是关掉桌宠的理由，
    /// 用户该继续用旧版本，pending 留着下次再试。
    /// </summary>
    internal static bool TryPrepare(
        UpdateStartPlan plan,
        string? dataRoot,
        string installRoot,
        out string? why)
    {
        why = null;
        if (plan.Step != UpdateStartStep.HandOff || plan.Pending is null || string.IsNullOrEmpty(dataRoot))
        {
            why = "no-plan";
            return false;
        }

        UpdatePending pending = plan.Pending;
        string tree = UpdateStage.TreeDir(dataRoot);
        if (!File.Exists(Path.Combine(tree, UpdateStage.EntryName)))
        {
            if (!UpdateStage.Extract(pending.Zip, tree, out int files, out string? unzipWhy))
            {
                why = "unzip-" + (unzipWhy ?? "?");
                return false;
            }

            KernelLog.Write($"update-handoff-extracted version={pending.Version} files={files}");
        }

        // 搬运工不在就**绝不能**写请求：留一个「装完再开」的标记而没有搬运工，
        // 下一次启动会把它读成「上一趟没跑完」，于是永远取消 —— 那比不更新更糟。
        string helper = Path.Combine(installRoot, UpdateStage.HelperName);
        if (!File.Exists(helper))
        {
            why = "helper-missing";
            KernelLog.Write($"update-handoff-aborted stage=helper path={helper}");
            return false;
        }

        // 搬运工要先拷一份到暂存区再从那里启动：它接下来要覆盖安装目录里**它自己**那一份，
        // 而正在运行的映像被 Windows 锁着（见 UpdateStage.StageHelper）。
        if (!UpdateStage.StageHelper(installRoot, dataRoot, out string? copyWhy))
        {
            why = "helper-" + (copyWhy ?? "?");
            KernelLog.Write($"update-handoff-aborted stage=helper-copy why={copyWhy}");
            return false;
        }

        if (!UpdateStage.WriteRequest(dataRoot, pending.Version, out string? writeWhy))
        {
            why = "request-" + (writeWhy ?? "?");
            KernelLog.Write($"update-handoff-aborted stage=request why={writeWhy}");
            return false;
        }

        why = "ok";
        return true;
    }

    /// <summary>
    /// 交接的后半段：把搬运工起起来。它等本进程退出（<c>--wait-pid</c>），然后换文件、把程序开回来。
    /// 返回 true = 调用方现在就该退出；false = 起不来（请求已经撤掉，本次照常用旧版本）。
    /// </summary>
    internal static bool TryLaunch(string? dataRoot, string version, string installRoot)
    {
        if (string.IsNullOrEmpty(dataRoot))
        {
            return false;
        }

        // 启的是**副本**（StageHelper 拷到暂存区的那一份），不是安装目录里那一份 ——
        // 它马上要覆盖安装目录，包括它自己。
        string helper = UpdateStage.HelperCopyPath(dataRoot);
        string pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo
        {
            FileName = helper,
            Arguments = string.Join(' ', new[]
            {
                "--apply", Quote(dataRoot), "--install", Quote(installRoot), "--wait-pid", pid,
            }),
            // 不设那个「交给外壳」的开关：.NET Core 上 ProcessStartInfo 的默认就是直接起进程，
            // 而内核文本里那个词是 D-03b 的黑名单项（理由见 UiHost.Update.cs 打开资源管理器那段）。
            WorkingDirectory = installRoot,
        };
        info.Environment["PETDOCK_UPDATE_FROM"] = version;

        try
        {
            using Process? started = Process.Start(info);
            KernelLog.Write(
                $"update-handoff-started version={version} helper={helper} wait-pid={pid} helper-pid={started?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}");
            return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception
            or InvalidOperationException or UnauthorizedAccessException)
        {
            // 起不来就把意图撤掉（理由同 TryPrepare 里那段：留一个没人接的请求会让下次启动永远取消）。
            UpdateStage.Delete(UpdateStage.RequestPath(dataRoot));
            KernelLog.Write($"update-handoff-failed stage=start {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// <summary>两段合起来：准备好就起搬运工。返回 true = 调用方应当退出。</summary>
    internal static bool TryHandOff(UpdateStartPlan plan, string? dataRoot, string installRoot) =>
        TryPrepare(plan, dataRoot, installRoot, out _) && TryLaunch(dataRoot, plan.Pending!.Version, installRoot);

    /// <summary>
    /// 上一次启动的结局（搬运工写的 result.txt）。读一次就删：它只描述**那一次**，
    /// 留着会让用户每天开机都看到同一句「已更新到 0.4.0」。
    ///
    /// 返回的是**结构**不是一句话：调用方决定怎么说（这里没法翻文案 —— 本类在 Host 层，
    /// 而文案表在 Ui 层；早先那次把「已是最新」和「没查到」说成一句的教训就来自这种越界）。
    /// </summary>
    internal static (bool Ok, string Text)? ConsumeResult(string? dataRoot)
    {
        if (string.IsNullOrEmpty(dataRoot))
        {
            return null;
        }

        (bool Ok, string Text)? result = UpdateStage.ReadResult(dataRoot);
        if (result is null)
        {
            return null;
        }

        UpdateStage.Delete(UpdateStage.ResultPath(dataRoot));
        UpdateStage.Delete(UpdateStage.RequestPath(dataRoot));
        if (result.Value.Ok)
        {
            UpdateStage.ClearPending(dataRoot, withFiles: true);
            KernelLog.Write($"update-applied version={result.Value.Text}");
            return result;
        }

        KernelLog.Write($"update-apply-failed why={result.Value.Text}");
        return result;
    }

    private static string Quote(string value) => '"' + value.TrimEnd('\\') + '"';
}
