// 「退出之后把新版本换上去」这一步的全部实现（2026-10-31）。
//
// 为什么必须是一个独立进程：要覆盖的正是宿主自己加载着的 exe/dll，Windows 不允许自我替换。
// 宿主先退出，这个进程等它退出，搬完再把程序开回来。
//
// 三条纪律：
//   1. **先备份后覆盖**。覆盖到一半失败时必须能把原来那一份换回去 ——
//      「旧版本没了、新版本也没装好」是这条路上唯一不可接受的结局。
//   2. **失败要说出来**。结局写进 <data>/_update/result.txt，下一次启动读一次就删，界面照实显示。
//   3. **不猜、不修、不联网**。它不认识内核的任何东西，只认两个目录：暂存树、安装目录。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PetDock.Update;

internal static partial class Applier
{
    /// <summary>等宿主退出最多这么久。超时就放弃这一次（不装），而不是在它还在跑时硬覆盖。</summary>
    private const int WaitMs = 120_000;

    internal static int Run(Options options) => options.Mode == ApplyMode.RollBack
        ? RunRollBack(options)
        : RunApply(options);

    private static int RunApply(Options options)
    {
        string log = Path.Combine(options.DataRoot, "_update", "helper.log");
        void Note(string line) => Write(log, line);

        Note($"start pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} wait-pid={options.WaitPid} install={options.InstallRoot}");

        if (!WaitForExit(options.WaitPid))
        {
            // 宿主没退出（退出被拦住 / 用户又点了什么）：**不装**。撤掉「装完再开」的意图，
            // 但保留待安装的那一份 —— 下一次启动会再试，用户不必重新下载。
            Note("fail host-alive");
            TryDelete(Path.Combine(options.DataRoot, "_update", "restart.txt"));
            WriteResult(options, "fail host-alive");
            return 1;
        }

        // 装的是**校验过的那一份**，不是「暂存目录里现在躺着的那些文件」（2026-10-31）。
        //
        // 差别很具体：下载时校验的是 zip 的 sha256，而宿主随后把它解到了 stage/。
        // 从 stage/ 装，等于相信「从校验到现在没有人动过那个目录」——
        // 而这两个目录都在用户可写的数据区里，中间还隔着「用户下次开机」这不知多久的一段。
        // 所以这里重新拿 zip 对一次 sha256，再**重新解一份**到 unpacked/ 去装：
        // 装到盘上的东西因此严格等于那个被校验过的 zip 的内容。
        //
        // 诚实边界：这挡不住能写数据区的人 —— 他连这个搬运工本身都能换掉。
        // 要做到那一步需要代码签名，本仓库今天没有证书。所以这里只把「校验值真的被用上了」
        // 这一条落实，不假装它是完整的供应链防护。
        string? expected = ReadExpectedSha(options);
        string? zip = ReadZipPath(options);
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(zip))
        {
            Note("fail no-pending");
            WriteResult(options, "fail no-pending");
            return 1;
        }

        string actual = Sha256Of(zip!);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            Note($"fail sha256 expected={expected} actual={actual}");
            // 包对不上就不要装了，而且**别每一次开机都再试一遍**：清掉待安装的那一份。
            TryDelete(Path.Combine(options.DataRoot, "_update", "restart.txt"));
            TryDelete(Path.Combine(options.DataRoot, "_update", "pending.json"));
            WriteResult(options, "fail sha256");
            return 1;
        }

        string stage = Path.Combine(options.DataRoot, "_update", "unpacked");
        if (!Unpack(zip!, stage, Note, out string? unpackWhy))
        {
            Note($"fail unpack {unpackWhy}");
            TryDelete(Path.Combine(options.DataRoot, "_update", "restart.txt"));
            TryDelete(Path.Combine(options.DataRoot, "_update", "pending.json"));
            WriteResult(options, "fail unpack");
            return 1;
        }

        if (!File.Exists(Path.Combine(stage, "PetDock.Core.exe")))
        {
            Note("fail unpack-no-entry");
            WriteResult(options, "fail no-stage");
            return 1;
        }

        // 覆盖之前先把「我要动哪些文件」写下来（applying.txt）：这是唯一能在断电之后
        // 分清「什么都没发生」与「装到一半」的东西，也是回滚时要知道删掉哪些**新增**文件的依据。
        if (!WriteJournal(options, stage, Note, out string? journalWhy))
        {
            Note($"fail journal {journalWhy}");
            WriteResult(options, "fail journal");
            return 1;
        }

        var copied = new List<string>();
        string backup = Path.Combine(options.DataRoot, "_update", "backup");
        try
        {
            DeleteTree(backup);
            foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(stage, file);
                string target = Path.Combine(options.InstallRoot, relative);
                if (!IsInside(options.InstallRoot, target))
                {
                    throw new IOException("escape " + relative);
                }

                if (File.Exists(target))
                {
                    string kept = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    File.Copy(target, kept, overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied.Add(relative);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Note($"fail copy {error.GetType().Name}: {error.Message}");
            int restored = Restore(backup, options.InstallRoot, copied, Note);

            // 覆盖失败 → 撤掉这一次更新，而不是留着让它下次再试。
            // 留着会变成一个**每次开机都失败一次**的循环：restart.txt 在 = 「上一趟没跑完」，
            // 启动路径每次都会取消它、报一次错，而用户什么也做不了。
            // 现在的口径：失败一次就说清楚一次，待安装的那一份清掉，用户想装再点一次下载。
            TryDelete(Path.Combine(options.DataRoot, "_update", "restart.txt"));
            TryDelete(Path.Combine(options.DataRoot, "_update", "pending.json"));
            TryDelete(JournalPath(options));
            DeleteTree(stage);
            WriteResult(options, $"fail copy restored={restored.ToString(CultureInfo.InvariantCulture)}");
            return 1;
        }

        Note($"copied files={copied.Count.ToString(CultureInfo.InvariantCulture)}");

        // 装完了：先把结果写下来，再去开程序。顺序不能倒 —— 开起来的新进程会立刻读 result.txt
        // 并把「已更新到 x.y.z」显示给用户，晚一步写它就说不上话了。
        string version = ReadVersion(options) ?? "?";
        WriteResult(options, "ok " + version);
        TryDelete(JournalPath(options));
        TryDelete(Path.Combine(options.DataRoot, "_update", "restart.txt"));
        Note($"ok version={version}");

        if (!Relaunch(options, Note))
        {
            // 装是装好了，只是没能自己开起来。result.txt **仍然写 ok** ——
            // 它是「装没装上」的答案，不是「有没有自己开起来」的答案；
            // 把它写进同一行会让下一次启动显示「已更新到 9.9.9 relaunch-failed」这种半句话。
            // 没能起来这件事只进 helper.log，下次启动读 result 时顺手把暂存区清掉。
            return 0;
        }

        DeleteTree(stage);
        DeleteTree(backup);
        return 0;
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.WaitForExit(WaitMs);
        }
        catch (ArgumentException)
        {
            return true; // 已经不在了
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>把已经覆盖过的文件换回备份里那一份。换不回几个就报几个（不假装全好了）。</summary>
    private static int Restore(string backup, string installRoot, List<string> copied, Note note)
    {
        int restored = 0;
        foreach (string relative in copied)
        {
            string kept = Path.Combine(backup, relative);
            if (!File.Exists(kept))
            {
                continue;
            }

            try
            {
                File.Copy(kept, Path.Combine(installRoot, relative), overwrite: true);
                restored++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                note($"restore-failed {relative} {error.GetType().Name}");
            }
        }

        return restored;
    }

    private static bool Relaunch(Options options, Note note)
    {
        string exe = Path.Combine(options.InstallRoot, "PetDock.Core.exe");
        if (!File.Exists(exe))
        {
            note("relaunch missing-exe");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = options.RelaunchArgs,
                WorkingDirectory = options.InstallRoot,
            });
            return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            note($"relaunch-failed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    private static string? ReadVersion(Options options)
    {
        try
        {
            string json = File.ReadAllText(Path.Combine(options.DataRoot, "_update", "pending.json"));
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>结局一行：<c>ok &lt;版本&gt;</c> 或 <c>fail &lt;原因&gt;</c>。先写 .tmp 再替换（半份文件读不懂）。</summary>
    private static void WriteResult(Options options, string text)
    {
        try
        {
            string dir = Path.Combine(options.DataRoot, "_update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "result.txt");
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 写不进去就什么都不说：宿主那边只会显示「上次更新没有报告」，不会撒谎说成功。
        }
    }

    private static void Write(string log, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(
                log,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 日志写不进去不影响搬运本身。
        }
    }

    private static bool IsInside(string dir, string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
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
            // 删不掉就留着：下一次交接会把它读成「上一趟没跑完」，那是安全的那一侧。
        }
    }

    private static void DeleteTree(string dir)
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
            // 同上：留着不影响结论。
        }
    }

    /// <summary>日志口（D-03b：内核文本里那个泛型委托名是黑名单项，所以自声明一个）。</summary>
    private delegate void Note(string line);
}
