// 回滚：把上一趟「覆盖到一半被打断」的安装换回旧版本（2026-10-31）。
//
// 为什么这一片非有不可（2026-10-31 审计的两条致命结论）：
//   1. 覆盖是**逐文件**的，进程在第 300 个文件上被结束（断电、蓝屏、任务管理器），
//      盘上就是「一半新、一半旧」的树 —— 它可能根本起不来；
//   2. 更糟的是下一次启动：旧逻辑把这种情况与「只写了意图、一个文件都没动」混成一种，
//      判 Cancel，而 Cancel 走 ClearPending(withFiles: true) —— **连唯一的那份 backup 一起删**。
//      于是用户既没有旧版本、也没有能跑的程序，界面还说「已取消」。
//
// 现在的分工：
//   覆盖前写 applying.txt（进程被打断时它还留着）→ 启动闸门看到它就判 RollBack →
//   宿主退出、把这一份交给搬运工 → 这里用 backup/ 换回去 → 写 result.txt → 把程序开回来。
//   判据在 UpdateStartupChecks / UpdateStageChecks（宿主侧）与 tools/update-helper-selftest.mjs（真进程）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PetDock.Update;

internal static partial class Applier
{
    /// <summary>「正在覆盖」日志的文件名（宿主侧 UpdateStage.AppliedFile 是同一个名字）。</summary>
    private const string JournalName = "applying.txt";

    /// <summary>日志里的版本行前缀；其余行是 <c>file|相对路径</c>。</summary>
    private const string JournalFile = "file|";

    private static string JournalPath(Options options) =>
        Path.Combine(options.DataRoot, "_update", JournalName);

    /// <summary>
    /// 在动第一个文件之前写下「我要覆盖哪些文件」。写不下来就**不装** ——
    /// 不能在没有回滚依据的情况下开始覆盖，那正是这条路上唯一不可接受的结局。
    /// </summary>
    private static bool WriteJournal(Options options, string stage, Note note, out string? why)
    {
        why = null;
        try
        {
            var lines = new List<string> { "version=" + (ReadVersion(options) ?? "?") };
            foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                lines.Add(JournalFile + Path.GetRelativePath(stage, file));
            }

            string? dir = Path.GetDirectoryName(JournalPath(options));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllLines(JournalPath(options), lines);
            note($"journal files={lines.Count - 1}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            why = error.GetType().Name;
            return false;
        }
    }

    /// <summary>读回「要覆盖哪些文件」。读不懂就返回空表（回滚会退化成「只按 backup 里有什么换什么」）。</summary>
    private static List<string> ReadJournal(Options options)
    {
        var files = new List<string>();
        try
        {
            string path = JournalPath(options);
            if (!File.Exists(path))
            {
                return files;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (!line.StartsWith(JournalFile, StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = line[JournalFile.Length..].Trim();
                if (relative.Length > 0)
                {
                    files.Add(relative);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 读不出来时按空表继续：backup 目录本身也是证据（见下）。
        }

        return files;
    }

    private static int RunRollBack(Options options)
    {
        string log = Path.Combine(options.DataRoot, "_update", "helper.log");
        void Note(string line) => Write(log, line);
        _sink = Note;

        Note($"rollback-start pid={Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} wait-pid={options.WaitPid} install={options.InstallRoot}");

        if (!WaitForExit(options.WaitPid))
        {
            // 宿主没退：现在换文件只会换到一半。什么都不动，下一次启动再试（日志留着）。
            Note("rollback-fail host-alive");
            WriteResult(options, "rollback-failed 0");
            return 1;
        }

        string backup = Path.Combine(options.DataRoot, "_update", "backup");
        if (!Directory.Exists(backup))
        {
            // 没有备份 = 上一次其实还没开始覆盖（或者备份被手删了）。这时盘上就是旧的，什么都不用做。
            Note("rollback-nothing-to-do no-backup");
            TryDelete(JournalPath(options));
            WriteResult(options, "rolled-back 0");
            Relaunch(options, Note);
            return 0;
        }

        // 两张表合起来才完整：
        //   日志里有、backup 里没有 → 那是新版本**新增**的文件，删掉它才是「回到旧版本」；
        //   backup 里有、日志里没有（日志丢了） → 仍然要换回去，那是被覆盖过的旧文件。
        var planned = ReadJournal(options);
        var known = new HashSet<string>(planned, StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(backup, file);
            if (known.Add(relative))
            {
                planned.Add(relative);
            }
        }

        int restored = 0;
        int removed = 0;
        int failed = 0;
        foreach (string relative in planned)
        {
            string kept = Path.Combine(backup, relative);
            string target = Path.Combine(options.InstallRoot, relative);
            if (!IsInside(options.InstallRoot, target))
            {
                failed++;
                Note($"rollback-escape {relative}");
                continue;
            }

            try
            {
                if (File.Exists(kept))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(kept, target, overwrite: true);
                    restored++;
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    removed++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failed++;
                Note($"rollback-file-failed {relative} {error.GetType().Name}");
            }
        }

        Note($"rollback-done restored={restored} removed={removed} failed={failed} planned={planned.Count}");
        TryDelete(JournalPath(options));

        if (failed == 0)
        {
            // 换干净了：备份与解包树都没用了。反过来说，**只要还有一个没换回去**，
            // 这两样就都留着 —— 那是用户手里最后的东西。
            WriteResult(options, $"rolled-back {restored + removed}");
            DeleteTree(backup);
            DeleteTree(Path.Combine(options.DataRoot, "_update", "stage"));
        }
        else
        {
            WriteResult(options, $"rollback-failed {failed}");
        }

        if (!Relaunch(options, Note))
        {
            Note("rollback-relaunch-failed");
        }

        return failed == 0 ? 0 : 1;
    }
}
