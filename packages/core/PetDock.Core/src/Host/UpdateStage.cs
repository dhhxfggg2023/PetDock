// 新版本「等安装」的那一份状态（2026-10-31 用户要求：「新增下载完选择重启更新或者稍后更新，
// 稍后更新就是退出程序下次启动时更新」）。
//
// 三份极小的文件，都住在 <data>/_update/ 下。它们**不合并成一份**，因为写入者与生命周期各不相同：
//   pending.json  要装哪一份（版本 + zip 落点 + sha256）。下载校验通过时写，装完删。
//   restart.txt   这一次的意图是「装完再开」。交给搬运进程之前写。
//   result.txt    搬运进程的结局（成功 / 失败 + 原因）。下一次启动读一次就删。
//
// 为什么需要 restart.txt 这份「意图」，而不是只看 pending.json 在不在：
//   同一份 pending 有两种截然不同的时机 —— 「用户点了立即重启」与「用户下次自己点开程序」。
//   而**正在运行的这个进程本身就是即将被覆盖的那份文件**，所以唯一安全的顺序是
//   「本进程先退出，再由搬运进程落盘」。restart.txt 就是这个协议的回执：
//   它还在 = 上一趟没跑完（搬运进程被结束，或用户在我们退出后又自己点开了程序）。
//   那时**绝不能**再装 —— 我们正跑着要被覆盖的文件，Windows 只会让我们失败一半。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>待安装的那一份：版本、zip 落点、以及下载时算出的 sha256。</summary>
internal sealed record UpdatePending(string Version, string Zip, string Sha256);

/// <summary>
/// 搬运工留下的那一行结局：<c>ok 0.4.1</c> / <c>fail sha256</c> /
/// <c>rolled-back 12</c> / <c>rollback-failed 3</c>。
///
/// 为什么要有 Kind 而不是一个 bool：三种结局对用户的下一步完全不同 ——
/// 装好了（不用做什么）、装失败了（再点一次下载）、回滚了（旧版本已经回来，可以重试）、
/// 回滚也只回了一半（要人工拿 zip 重新覆盖）。把回滚说成「安装失败」是在撒谎。
/// </summary>
internal readonly record struct UpdateResult(string Kind, string Text)
{
    /// <summary>装成功了。</summary>
    internal bool Ok => string.Equals(Kind, "ok", StringComparison.Ordinal);

    /// <summary>这是回滚的结局（不是安装的结局）。</summary>
    internal bool RollBack => Kind is "rolled-back" or "rollback-failed";

    /// <summary>回滚把每一个要换回去的文件都换回去了。</summary>
    internal bool Complete => string.Equals(Kind, "rolled-back", StringComparison.Ordinal);
}

/// <summary>这一次启动与「待安装的那一份」之间的关系。</summary>
internal enum UpdateStartStep
{
    /// <summary>没有待安装的版本，正常启动。</summary>
    None,

    /// <summary>有待安装的版本：解压（若没解过）、写 restart.txt、交给搬运工，然后本进程退出。</summary>
    HandOff,

    /// <summary>上一趟交接没跑完：取消这次更新（并如实告诉用户），本次照常启动。</summary>
    Cancel,

    /// <summary>
    /// 上一趟**装到一半被打断**（搬运工的覆盖日志还在，说明它已经开始动文件）：
    /// 必须先把备份换回去，再谈「下次要不要装」。这是 2026-10-31 审计出来的那个洞 ——
    /// 以前这种情况和「只写了意图、文件没动」一起被当成 Cancel，而 Cancel 会**连唯一的备份一起删**，
    /// 于是用户既没有旧版本、也没有能跑的程序。
    /// </summary>
    RollBack,
}

/// <summary>
/// 暂存目录与三份标记文件的读写。全部是**小文件 + 原子替换**（先写 .tmp 再覆盖），
/// 因为断电/蓝屏发生在写到一半时，留下的半份 JSON 会让下一次启动读不懂整个目录。
/// </summary>
internal static partial class UpdateStage
{
    /// <summary>数据区下的暂存目录名（与插件根、市场暂存都分开）。</summary>
    internal const string Folder = "_update";

    internal const string PendingFile = "pending.json";
    internal const string RequestFile = "restart.txt";
    internal const string ResultFile = "result.txt";

    /// <summary>
    /// 搬运工「正在覆盖」的日志：在它动第一个文件**之前**写下、装完或回滚完才删。
    /// 它还在 = 上次的覆盖没走完（断电、蓝屏、被任务管理器结束）—— 这时盘上的树是新旧混杂的，
    /// 唯一安全的动作是用 backup/ 换回去，而不是把它当「什么都没发生」。
    /// 内容由搬运工写（第一行版本、其余每行 `file|相对路径`），宿主只判存在与内容。
    /// </summary>
    internal const string AppliedFile = "applying.txt";

    /// <summary>解压出来的新版本树（平铺：zip 里的顶层目录被剪掉一格）。</summary>
    internal const string TreeFolder = "stage";

    /// <summary>搬运工的可执行文件名。它和宿主同目录（发布物里就摆在旁边）。</summary>
    internal const string HelperName = "PetDock.Update.exe";

    /// <summary>搬运工被拷到这里再启动（见 <see cref="StageHelper"/>）。</summary>
    internal const string HelperFolder = "helper";

    /// <summary>装完之后**一定**要在这里的那个文件：没有它就没有可运行的程序。</summary>
    internal const string EntryName = "PetDock.Core.exe";

    /// <summary>解压出来的文件数上限（自己发的包只有一百多个文件；这是防呆不是防敌）。</summary>
    private const int MaxEntries = 4096;

    /// <summary>单个文件上限（与 UpdateFeed.MaxBytes 同一量级，防一个畸形 zip 撑爆磁盘）。</summary>
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    internal static string Dir(string dataRoot) => Path.Combine(dataRoot, Folder);

    internal static string TreeDir(string dataRoot) => Path.Combine(Dir(dataRoot), TreeFolder);

    internal static string PendingPath(string dataRoot) => Path.Combine(Dir(dataRoot), PendingFile);

    internal static string RequestPath(string dataRoot) => Path.Combine(Dir(dataRoot), RequestFile);

    internal static string ResultPath(string dataRoot) => Path.Combine(Dir(dataRoot), ResultFile);

    /// <summary>「正在覆盖」日志的路径（搬运工写、宿主读，格式见 <see cref="AppliedFile"/>）。</summary>
    internal static string AppliedPath(string dataRoot) => Path.Combine(Dir(dataRoot), AppliedFile);

    /// <summary>上次的覆盖是不是没走完（日志还在）。启动闸门与判据都用这一条。</summary>
    internal static bool HasAppliedJournal(string dataRoot) => File.Exists(AppliedPath(dataRoot));

    /// <summary>备份目录：半覆盖之后唯一能把旧版本换回去的东西。</summary>
    internal static string BackupDir(string dataRoot) => Path.Combine(Dir(dataRoot), "backup");

    /// <summary>搬运工那一份副本所在的目录。</summary>
    internal static string HelperDir(string dataRoot) => Path.Combine(Dir(dataRoot), HelperFolder);

    /// <summary>要被启动的那个搬运工（副本，不是安装目录里那一份）。</summary>
    internal static string HelperCopyPath(string dataRoot) =>
        Path.Combine(HelperDir(dataRoot), HelperName);

    /// <summary>
    /// 把搬运工从安装目录拷到暂存区，再从这里启动它。
    ///
    /// **为什么必须拷一份**：它接下来要把安装目录整个换成新的，其中包括它自己那一份
    /// <c>PetDock.Update.exe</c>。正在运行的映像被 Windows 锁着，直接覆盖会失败，
    /// 于是「程序换了一半」—— 正是这套机制要防的那件事。从副本启动，安装目录里那个文件
    /// 就不被占用，可以照常覆盖。
    ///
    /// 拷的是 <c>PetDock.Update.*</c> 这一组（exe / dll / deps.json / runtimeconfig.json）：
    /// 它是框架依赖发布形态，少一个 .json 就跑不起来。
    /// </summary>
    internal static bool StageHelper(string installRoot, string dataRoot, out string? why)
    {
        why = null;
        string target = HelperDir(dataRoot);
        try
        {
            DeleteTree(target);
            Directory.CreateDirectory(target);
            int copied = 0;
            foreach (string file in Directory.EnumerateFiles(installRoot, "PetDock.Update.*", SearchOption.TopDirectoryOnly))
            {
                string dest = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);

                // 复制会连「来自互联网」标记一起带走（见 InternetMark）：搬运工正是从这一个副本启动的，
                // 而那一刻宿主已经退出、没有窗口能提示用户去点「仍要运行」。就地清掉。
                InternetMark.Clear(dest);
                copied++;
            }

            if (copied == 0 || !File.Exists(HelperCopyPath(dataRoot)))
            {
                why = "helper-copy-empty";
                return false;
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            why = "helper-copy-" + error.GetType().Name;
            KernelLog.Write($"update-helper-copy-failed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// 落盘「待安装的那一份」。落点必须是暂存目录里的 zip：判据会读这一条 ——
    /// 标记文件里写着任意路径，等于让一份可被改写的 JSON 决定去执行哪个文件。
    /// </summary>
    internal static bool WritePending(string dataRoot, UpdatePending pending, out string? why)
    {
        why = null;
        string dir = Dir(dataRoot);
        if (!IsInside(dir, pending.Zip) || !File.Exists(pending.Zip))
        {
            why = "pending-bad-zip";
            return false;
        }

        try
        {
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["version"] = pending.Version,
                ["zip"] = pending.Zip,
                ["sha256"] = pending.Sha256,
            });
            return WriteAtomic(PendingPath(dataRoot), json, out why);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            why = "pending-write-" + error.GetType().Name;
            return false;
        }
    }

    /// <summary>读「待安装的那一份」。读不懂、版本不像版本号、zip 不在暂存目录 —— 一律当没有。</summary>
    internal static UpdatePending? ReadPending(string dataRoot)
    {
        try
        {
            string path = PendingPath(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? version = Str(root, "version");
            string? zip = Str(root, "zip");
            string? sha = Str(root, "sha256");
            if (version is null || zip is null || !UpdateFeed.IsVersion(version))
            {
                return null;
            }

            if (!IsInside(Dir(dataRoot), zip) || !File.Exists(zip))
            {
                return null;
            }

            return new UpdatePending(version, zip, sha ?? string.Empty);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            KernelLog.Write($"update-pending-unreadable {error.GetType().Name}");
            return null;
        }
    }

    /// <summary>写下「装完再开」的意图（内容是版本号：判据与日志都读它）。</summary>
    internal static bool WriteRequest(string dataRoot, string version, out string? why) =>
        WriteAtomic(RequestPath(dataRoot), version, out why);

    internal static string? ReadRequest(string dataRoot)
    {
        try
        {
            string path = RequestPath(dataRoot);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KernelLog.Write($"update-request-unreadable {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>搬运工的结局：一行 <c>ok</c> 或 <c>fail</c>，加一个版本号或原因码。</summary>
    internal static UpdateResult? ReadResult(string dataRoot)
    {
        try
        {
            string path = ResultPath(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            string line = File.ReadAllText(path).Trim();
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return new UpdateResult(
                parts.Length > 0 ? parts[0] : string.Empty,
                parts.Length > 1 ? parts[1].Trim() : string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KernelLog.Write($"update-result-unreadable {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    internal static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KernelLog.Write($"update-delete-failed path={path} {error.GetType().Name}");
        }
    }

    /// <summary>搬运工退出之后留在暂存区的那几份东西的目录名（装完、取消、失败都要收）。</summary>
    private static readonly string[] ScratchFolders = { TreeFolder, "backup", HelperFolder };

    /// <summary>
    /// 丢掉待安装的那一份。<paramref name="withFiles"/> = 连解压树、备份、搬运工副本一起删。
    ///
    /// 取消时传 false：保留 zip 与暂存区，用户下次点「检查更新」还能直接用；
    /// 装完/取消之后传 true：那一份已经没用了，留着只会占几百兆。
    /// </summary>
    internal static void ClearPending(string dataRoot, bool withFiles)
    {
        Delete(PendingPath(dataRoot));
        Delete(RequestPath(dataRoot));
        if (!withFiles)
        {
            return;
        }

        string dir = Dir(dataRoot);

        // 「正在覆盖」日志还在 = 盘上的树可能是新旧混杂的：这时 backup/ 是**唯一**能把旧版本
        // 换回去的东西，删掉它等于把用户最后的退路清空（2026-10-31 审计：这条路径真的会删掉 backup）。
        // 所以留着，等回滚跑完再收。
        if (HasAppliedJournal(dataRoot))
        {
            KernelLog.Write($"update-clear-keeps-backup path={BackupDir(dataRoot)}");
            DeleteTree(Path.Combine(dir, TreeFolder));
            DeleteTree(Path.Combine(dir, HelperFolder));
            return;
        }

        foreach (string folder in ScratchFolders)
        {
            DeleteTree(Path.Combine(dir, folder));
        }
    }

    /// <summary>整棵删掉（尽力而为：删不掉就留在暂存区，下一次装会覆盖）。</summary>
    internal static void DeleteTree(string dir)
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
            KernelLog.Write($"update-tree-delete-failed path={dir} {error.GetType().Name}");
        }
    }

    private static bool WriteAtomic(string path, string text, out string? why)
    {
        why = null;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            why = "write-" + error.GetType().Name;
            return false;
        }
    }

    private static bool IsInside(string dir, string path)
    {
        try
        {
            string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
