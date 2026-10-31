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
            ResultIsReadOnce(check, root);
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

        (bool Ok, string Text)? first = UpdateStartup.ConsumeResult(data);
        (bool Ok, string Text)? second = UpdateStartup.ConsumeResult(data);
        bool once = first is not null && first.Value.Ok && first.Value.Text == "9.9.9" && second is null;
        check("ui-update-result-once", once,
            $"first={(first is null ? "none" : first.Value.Ok + "/" + first.Value.Text)} second={(second is null ? "none" : "still-there")}");
    }

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
