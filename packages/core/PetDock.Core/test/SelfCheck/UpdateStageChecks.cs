// 「下载完之后怎么装」这一片的判据（2026-10-31）。
//
// 判的是**磁盘上留下来的东西**，不是某个方法有没有被调用过：
//   * 待安装标记写没写、写的落点是不是被限制在暂存目录里；
//   * 交接成功后 restart.txt 在不在（它是「这次退出是要去装东西」的唯一回执）；
//   * 上一趟没跑完时会不会**取消**而不是在运行中的目录上硬装。
//
// 全部在自检临时目录里跑真文件：不联网、不碰 %LOCALAPPDATA%\PetDock、不碰安装目录。
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class UpdateStageChecks
{
    internal static void Run(UpdateChecks.Report check)
    {
        string root = SelfTestScratch.NewDir("update-stage");
        try
        {
            PendingRoundTrip(check, root);
            PendingRejectsOutsideZip(check, root);
            RequestIsTheReceipt(check, root);
            ExtractTrimsTopAndRejectsMissingEntry(check, root);
            // 2026-10-31 用户双击撞上的那个框：搬运工副本不许带着「来自互联网」标记。
            HelperCopyIsUnblocked(check, root);
            ResultIsReadOnce(check, root);
            RelaunchFailureIsNotSilent(check, root);
            // 2026-10-31 审计出来的那一刀：覆盖到一半时，唯一的退路不许被清掉。
            ClearPendingKeepsBackup(check, root);
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// 标记能写能读，且读回来的是同一份 —— 「稍后更新」这一整条路都压在这三行 JSON 上：
    /// 写不进去或读不回来，用户看到的是「已登记」，下次启动却是旧版本而且什么提示都没有。
    /// </summary>
    private static void PendingRoundTrip(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "data");
        string update = UpdateStage.Dir(data);
        Directory.CreateDirectory(update);
        string zip = Path.Combine(update, "PetDock-9.9.9-win-x64.zip");
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });

        var pending = new UpdatePending("9.9.9", zip, new string('a', 64));
        bool written = UpdateStage.WritePending(data, pending, out string? why);
        UpdatePending? back = UpdateStage.ReadPending(data);

        bool ok = written && back is not null
            && back.Version == "9.9.9"
            && string.Equals(back.Zip, zip, StringComparison.OrdinalIgnoreCase)
            && back.Sha256 == pending.Sha256;
        check("ui-update-pending-roundtrip", ok,
            $"written={written} why={why ?? "none"} version={back?.Version} sha={back?.Sha256?.Length}");

        UpdateStage.Delete(zip);
        bool goneWhenZipGone = UpdateStage.ReadPending(data) is null;
        check("ui-update-pending-needs-zip", goneWhenZipGone,
            $"zip-exists={File.Exists(zip)} pending={(UpdateStage.ReadPending(data) is null ? "none" : "still-there")}");
    }

    /// <summary>
    /// 标记里的 zip 必须在暂存目录里。否则一份可被改写的 JSON 就成了「去执行哪个文件」的依据 ——
    /// 这份文件住在用户可写的数据区，它不是可信输入。
    /// </summary>
    private static void PendingRejectsOutsideZip(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "outside");
        Directory.CreateDirectory(UpdateStage.Dir(data));
        string decoy = Path.Combine(root, "elsewhere.zip");
        File.WriteAllBytes(decoy, new byte[] { 0 });

        bool written = UpdateStage.WritePending(data, new UpdatePending("9.9.9", decoy, "x"), out string? why);
        check("ui-update-pending-inside-only", !written && why == "pending-bad-zip",
            $"written={written} why={why ?? "none"} path={decoy}");
    }

    /// <summary>
    /// restart.txt 是「这次退出是要去装东西」的回执：它在 = 上一趟没跑完。
    /// 判据看的是它真的被读得出来、清得掉 —— 交接与取消两条路都靠这一对动作。
    /// </summary>
    private static void RequestIsTheReceipt(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "receipt");
        Directory.CreateDirectory(UpdateStage.Dir(data));
        bool written = UpdateStage.WriteRequest(data, "9.9.9", out string? why);
        string? read = UpdateStage.ReadRequest(data);
        UpdateStage.ClearPending(data, withFiles: false);
        string? afterClear = UpdateStage.ReadRequest(data);

        bool ok = written && read == "9.9.9" && afterClear is null;
        check("ui-update-request-is-receipt", ok,
            $"written={written} why={why ?? "none"} read={read ?? "none"} after-clear={afterClear ?? "none"}");
    }

    /// <summary>
    /// 解压要剪掉 zip 里的顶层目录（发布包是 PetDock-x.y.z/…），并且**没有入口就整份失败**：
    /// 一个解出来但没有 PetDock.Core.exe 的树，装上去就是「更新之后打不开」。
    /// </summary>
    private static void ExtractTrimsTopAndRejectsMissingEntry(UpdateChecks.Report check, string root)
    {
        string zip = Path.Combine(root, "good.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            // 顶层目录不带版本号：与真实发布包同形（见 tools/pack.mjs 里那段说明）。
            Write(archive, "PetDock/PetDock.Core.exe", "exe");
            Write(archive, "PetDock/plugins/x/petdock-plugin.json", "{}");
        }

        string tree = Path.Combine(root, "tree");
        bool extracted = UpdateStage.Extract(zip, tree, out int files, out string? why);
        bool trimmed = File.Exists(Path.Combine(tree, "PetDock.Core.exe"))
            && File.Exists(Path.Combine(tree, "plugins", "x", "petdock-plugin.json"));

        string badZip = Path.Combine(root, "bad.zip");
        using (ZipArchive archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            Write(archive, "PetDock/readme.txt", "x");
        }

        string badTree = Path.Combine(root, "bad-tree");
        bool rejected = !UpdateStage.Extract(badZip, badTree, out _, out string? badWhy);
        check("ui-update-extract-trims-top", extracted && trimmed && files == 2,
            $"extracted={extracted} trimmed={trimmed} files={files} why={why ?? "none"}");
        check("ui-update-extract-needs-entry", rejected,
            $"rejected={rejected} why={badWhy ?? "none"}");
    }

    /// <summary>搬运工的结局只描述**那一次**：读一次就删，否则用户每天开机都看到同一句「已更新」。</summary>
    private static void ResultIsReadOnce(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "result");
        string dir = UpdateStage.Dir(data);
        Directory.CreateDirectory(dir);
        File.WriteAllText(UpdateStage.ResultPath(data), "ok 9.9.9");

        UpdateResult? first = UpdateStartup.ConsumeResult(data);
        UpdateResult? second = UpdateStartup.ConsumeResult(data);
        bool once = first is not null && first.Value.Ok && first.Value.Text == "9.9.9" && second is null;
        check("ui-update-result-once", once,
            $"first={(first is null ? "none" : first.Value.Ok + "/" + first.Value.Text)} second={(second is null ? "none" : "still-there")}");
    }

    /// <summary>
    /// 「装好了，但没能自己开回来」不许只留在 helper.log（2026-10-31 审计的遗留项）：
    /// 搬运工留一行原因，宿主那一次启动把它并进结局 —— 用户要么看到「已更新到 x.y.z」，
    /// 要么看到「已更新到 x.y.z，但没能自动打开，这次是你自己启动的」，不能再出现
    /// 「点了一次更新、桌宠消失、之后一句解释都没有」。
    ///
    /// 同时判反面：没有那一行时仍然是普普通通的「已更新」，不许把正常结局说成异常。
    /// </summary>
    private static void RelaunchFailureIsNotSilent(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "relaunch-report");
        Directory.CreateDirectory(UpdateStage.Dir(data));
        File.WriteAllText(UpdateStage.ResultPath(data), "ok 9.9.9");
        File.WriteAllText(UpdateStage.RelaunchPath(data), "Win32Exception");

        UpdateResult? reported = UpdateStartup.ConsumeResult(data);
        bool saidSo = reported.HasValue
            && reported.Value.Ok
            && !reported.Value.Restarted
            && reported.Value.Text == "9.9.9"
            && !File.Exists(UpdateStage.RelaunchPath(data));
        check("ui-update-relaunch-reported", saidSo,
            $"ok={(reported.HasValue && reported.Value.Ok)} restarted={(reported.HasValue && reported.Value.Restarted)} "
            + $"text={(reported.HasValue ? reported.Value.Text : "none")} left={File.Exists(UpdateStage.RelaunchPath(data))}");

        string clean = Path.Combine(root, "relaunch-normal");
        Directory.CreateDirectory(UpdateStage.Dir(clean));
        File.WriteAllText(UpdateStage.ResultPath(clean), "ok 9.9.9");
        UpdateResult? normal = UpdateStartup.ConsumeResult(clean);
        bool quiet = normal.HasValue && normal.Value.Ok && normal.Value.Restarted;
        check("ui-update-relaunch-absent-is-normal", quiet,
            $"ok={(normal.HasValue && normal.Value.Ok)} restarted={(normal.HasValue && normal.Value.Restarted)}");
    }

    /// <summary>
    /// 覆盖日志（applying.txt）还在时，`ClearPending(withFiles: true)` **不许**删 backup ——
    /// 那是半覆盖之后唯一能把旧版本换回去的东西。2026-10-31 审计里「用户既没有旧版本也没有程序」
    /// 那条路的入口，就是这行清理把 backup 一起删掉。
    ///
    /// 同时判反面：日志撤掉之后的清理**必须**真的清干净，否则暂存区会一直留着几百兆。
    /// </summary>
    private static void ClearPendingKeepsBackup(UpdateChecks.Report check, string root)
    {
        string data = Path.Combine(root, "clear-backup");
        Directory.CreateDirectory(UpdateStage.Dir(data));
        string backup = UpdateStage.BackupDir(data);
        Directory.CreateDirectory(backup);
        string kept = Path.Combine(backup, UpdateStage.EntryName);
        File.WriteAllText(kept, "old");
        File.WriteAllText(UpdateStage.AppliedPath(data), "version=1.2.3");

        UpdateStage.ClearPending(data, withFiles: true);
        bool keptWhileApplying = File.Exists(kept) && UpdateStage.HasAppliedJournal(data);

        UpdateStage.Delete(UpdateStage.AppliedPath(data));
        UpdateStage.ClearPending(data, withFiles: true);
        bool clearedAfter = !Directory.Exists(backup);
        check("ui-update-clear-keeps-backup", keptWhileApplying && clearedAfter,
            $"kept-while-applying={keptWhileApplying} cleared-after={clearedAfter}");
    }

    /// <summary>
    /// 搬运工副本上不许留「来自互联网」标记 —— 2026-10-31 用户双击时撞上的那个框。
    ///
    /// 判的是**拷贝之后**的状态，不是「有没有调过 Clear」：标记是 NTFS 的备用数据流，
    /// 而 File.Copy 会把它一起带走（实测 Copy-Item 与 File.Copy 都保留）。搬运工启动时
    /// 宿主已经退出、面板早已关闭，那一刻没有任何窗口能提示用户去点「仍要运行」，
    /// 用户看到的是「点了更新，程序不见了」。
    ///
    /// 同时判反面：源文件**仍然**带标记 —— 证明清掉的是这一个副本，
    /// 而不是碰巧因为跑在 FAT32 上、或者因为扫描了整个目录。
    /// </summary>
    private static void HelperCopyIsUnblocked(UpdateChecks.Report check, string root)
    {
        string install = Path.Combine(root, "install");
        string data = Path.Combine(root, "helper-data");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(UpdateStage.Dir(data));

        // 与真实安装目录同形：发布物把 PetDock.Update.* 摆在宿主旁边；内容要是 PE 头，
        // 免得把「文件是不是可执行」和「标记清没清」两件事混在一起。
        string source = Path.Combine(install, UpdateStage.HelperName);
        File.WriteAllBytes(source, new byte[] { 0x4D, 0x5A });
        Mark(source);

        bool staged = UpdateStage.StageHelper(install, data, out string? why);
        string copy = UpdateStage.HelperCopyPath(data);
        bool sourceMarked = HasMark(source);
        bool copyMarked = HasMark(copy);

        check("ui-update-helper-copy-unblocked",
            staged && File.Exists(copy) && sourceMarked && !copyMarked,
            $"staged={staged} why={why ?? "none"} source-marked={sourceMarked} copy-marked={copyMarked}");
    }

    // 下面两个只服务这一条判据。.NET 的路径规范化不接受带冒号的备用数据流路径
    // （File.WriteAllText("x:Zone.Identifier") 抛 NotSupportedException），只能走 Win32。
    private const string Zone = ":Zone.Identifier";

    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 3;
    private const uint CreateAlways = 2;
    private const uint InvalidAttributes = 0xFFFFFFFF;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string name);

    /// <summary>给文件挂上「来自互联网」标记，形状与浏览器下载时写的一致。</summary>
    private static void Mark(string path)
    {
        using SafeFileHandle handle = CreateFileW(path + Zone, GenericWrite, ShareAll, IntPtr.Zero, CreateAlways, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            // 非 NTFS 卷没有备用数据流。判据会如实报 source-marked=False 并判红 —— 那是对的：
            // 这一条在这台机器上就不该被算成通过。
            return;
        }

        byte[] text = System.Text.Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3");
        WriteFile(handle, text, text.Length, out _, IntPtr.Zero);
    }

    private static bool HasMark(string path) => GetFileAttributesW(path + Zone) != InvalidAttributes;

    private static void Write(ZipArchive archive, string name, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(text);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响结论。
        }
    }
}
