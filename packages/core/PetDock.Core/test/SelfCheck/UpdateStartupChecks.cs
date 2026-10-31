// 「这次启动该不该装已经下载好的新版本」这一片的判据（2026-10-31）。
//
// 用户的原话是「稍后更新就是退出程序下次启动时更新」。这条承诺只有一个地方能证明：
// 启动路径看到待安装标记时，到底**装还是不装**。这里把三种现场都造出来：
//   * 干净的一份          → HandOff（装，然后退出让搬运工接手）
//   * 带 restart.txt 的一份 → Cancel（上一趟没跑完，**不能**在正在运行的文件上硬装）
//   * 什么都没有          → None
//
// 判据直接驱动 Decide（纯判定 + 读两个小文件），不启动进程、不搬文件。
using System;
using System.IO;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class UpdateStartupChecks
{
    internal static void Run(UpdateChecks.Report check)
    {
        string root = SelfTestScratch.NewDir("update-startup");
        try
        {
            NothingToDo(check, root);
            HandOffWhenReady(check, root);
            CancelWhenUnfinished(check, root);
            // 2026-10-31 审计：`restart.txt` 在有两种截然不同的现场，必须分开处置。
            RollBackWhenInterrupted(check, root);
            GateIsOffForProbes(check, root);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉不影响结论。
            }
        }
    }

    private static void NothingToDo(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "empty");
        Directory.CreateDirectory(UpdateStage.Dir(data));
        UpdateStartPlan plan = UpdateStartup.Decide(data);
        UpdateStartPlan noRoot = UpdateStartup.Decide(null);

        check("ui-update-start-none", plan.Step == UpdateStartStep.None && noRoot.Step == UpdateStartStep.None,
            $"step={plan.Step} no-root={noRoot.Step}");
    }

    /// <summary>有标记、没有回执 = 该装了。这是「稍后更新」那一整条路的落点。</summary>
    private static void HandOffWhenReady(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "ready");
        string? pending = Stage(data, "1.2.3");
        UpdateStartPlan plan = UpdateStartup.Decide(data);

        bool resolved = pending is not null
            && plan.Step == UpdateStartStep.HandOff
            && plan.Pending?.Version == "1.2.3";
        check("ui-update-start-handoff", resolved, $"step={plan.Step} version={plan.Pending?.Version}");
    }

    /// <summary>
    /// restart.txt 还在 = 上一趟交接没跑完（搬运工被结束，或用户退出后又自己点开了程序）。
    /// 这时**必须取消**：正在运行的就是要被覆盖的那份文件。
    /// 而且取消要把它清干净 —— 留着会让下一次启动再取消一次，永远更新不了。
    /// </summary>
    private static void CancelWhenUnfinished(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "unfinished");
        Stage(data, "1.2.3");
        UpdateStage.WriteRequest(data, "1.2.3", out _);

        UpdateStartPlan plan = UpdateStartup.Decide(data);
        bool cleared = UpdateStage.ReadPending(data) is null && UpdateStage.ReadRequest(data) is null;
        bool cancelled = plan.Step == UpdateStartStep.Cancel && cleared;

        // 清干净之后，下一次启动应当回到「什么也没有」，而不是一直取消下去。
        UpdateStartPlan next = UpdateStartup.Decide(data);
        check("ui-update-start-cancel-unfinished", cancelled && next.Step == UpdateStartStep.None,
            $"step={plan.Step} cleared={cleared} next={next.Step} detail={plan.Detail}");
    }

    /// <summary>
    /// 覆盖到一半被打断（搬运工的 applying.txt 还在）：必须判 RollBack，而且**不许**动
    /// 待安装标记与 backup —— 下一步是把备份换回去，不是「取消并清空」。
    ///
    /// 这是审计里那条「用户既没有旧版本、也没有能跑的程序」的入口：
    /// 旧代码把它和「只写了意图、文件没动」混成一种，判 Cancel，而 Cancel 会连 backup 一起删。
    /// </summary>
    private static void RollBackWhenInterrupted(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "interrupted");
        Stage(data, "1.2.3");
        UpdateStage.WriteRequest(data, "1.2.3", out _);
        string backup = UpdateStage.BackupDir(data);
        Directory.CreateDirectory(backup);
        string kept = Path.Combine(backup, UpdateStage.EntryName);
        File.WriteAllText(kept, "old");
        File.WriteAllText(
            UpdateStage.AppliedPath(data),
            "version=1.2.3" + Environment.NewLine + "file|" + UpdateStage.EntryName);

        UpdateStartPlan plan = UpdateStartup.Decide(data);
        bool pendingKept = UpdateStage.ReadPending(data) is not null;
        bool backupKept = File.Exists(kept);
        bool journalKept = UpdateStage.HasAppliedJournal(data);
        check("ui-update-start-rollback-interrupted",
            plan.Step == UpdateStartStep.RollBack && pendingKept && backupKept && journalKept,
            $"step={plan.Step} pending={pendingKept} backup={backupKept} journal={journalKept} detail={plan.Detail}");
    }

    /// <summary>
    /// 探针形态**不参与**更新：门禁里没有人点更新，而「启动时顺手装了个新版本」会让判据
    /// 跑在一个会变的程序上，读数从此不可比。
    ///
    /// 还有一半更重要：探针**不消费**那份待安装的文件。它只是不参与，不是放弃 ——
    /// 用户跑过一次诊断之后，下次正常启动照样该把新版本装上。
    /// </summary>
    private static void GateIsOffForProbes(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "probe");
        Stage(data, "1.2.3");
        UpdateStartPlan probe = UpdateStartup.Decide(data, probe: true);
        bool kept = UpdateStage.ReadPending(data) is not null;

        // 同一条路上，非探针形态必须**仍然**答应装（证明上面那个 None 是探针拦下来的，
        // 不是这份现场本身就没准备好）。
        UpdateStartPlan normal = UpdateStartup.Decide(data, probe: false);
        check("ui-update-start-gate-off-probe",
            probe.Step == UpdateStartStep.None && kept && normal.Step == UpdateStartStep.HandOff,
            $"probe={probe.Step} kept={kept} normal={normal.Step}");
    }

    /// <summary>造一份「已下载、已解好、等着装」的现场，返回 zip 路径。</summary>
    private static string? Stage(string dataRoot, string version)
    {
        try
        {
            string dir = UpdateStage.Dir(dataRoot);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "PetDock-" + version + "-win-x64.zip");
            if (!File.Exists(zip))
            {
                File.WriteAllBytes(zip, new byte[] { 0x50, 0x4b, 0x03, 0x04 });
            }

            return UpdateStage.WritePending(dataRoot, new UpdatePending(version, zip, new string('b', 64)), out _)
                ? zip
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
