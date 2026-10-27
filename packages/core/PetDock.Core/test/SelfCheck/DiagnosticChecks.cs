// 常驻日志与脱敏规则的判据（RFC-17 §4 的前 8 条）。
//
// 判据走**生产对象**：真的 ResidentLog（真的落盘、真的滚动）、真的 RedactRules。
// 落盘只碰临时目录（原规格 41 D-10b：单测不写真实文件系统位置），
// 用完删掉 —— 先例在 ImportChecks.cs。
//
// 覆盖的判据：
//   ui-diag-log-file-written            常驻形态的日志真的落盘
//   ui-diag-log-line-has-agent-timestamp 每行带 agent_id 与 timestamp
//   ui-diag-log-part-until-complete     活动文件用 .part、完成后改名
//   ui-diag-log-rolls-on-size           超过单文件上限会滚动
//   ui-diag-log-write-never-throws      写日志不抛到调用点
//   ui-diag-redact-user-profile         用户目录前缀被替换
//   ui-diag-redact-machine-name         机器名被替换
//   ui-diag-redact-plugin-root          插件目录被替换
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class DiagnosticChecks
{
    /// <summary>判据的输出与计数回调（与 UiSelfTest 的 Check 同形）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>本组的判据名（异常兜底时逐条落 FAIL）。</summary>
    private static readonly string[] AllNames =
    {
        "ui-diag-log-file-written",
        "ui-diag-log-line-has-agent-timestamp",
        "ui-diag-log-part-until-complete",
        "ui-diag-log-rolls-on-size",
        "ui-diag-log-write-never-throws",
        "ui-diag-redact-user-profile",
        "ui-diag-redact-machine-name",
        "ui-diag-redact-plugin-root",
    };

    /// <summary>探针用的假机器事实（不取本机真实值，判据不该依赖跑在哪台机器上）。</summary>
    private const string ProbeUser = "testuser";
    private const string ProbeMachine = "PROBEMACHINE";
    private const string ProbePluginsRoot = "C:\\PluginsRoot";
    private const string ProbeDataRoot = "C:\\DataRoot";

    /// <summary>单行最大长度（与 ResidentLog.MaxLineChars 同值：写满一行才算灌满）。</summary>
    private const int FillerChars = 8 * 1024;

    public static void Run(Check check)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-diag-check-" + Guid.NewGuid().ToString("N"));
        var done = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            RunAll(check, root, done);
        }
        catch (Exception error)
        {
            foreach (string name in AllNames)
            {
                if (!done.Contains(name))
                {
                    check(name, false, $"{error.GetType().Name}: {error.Message}");
                }
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void RunAll(Check check, string root, HashSet<string> done)
    {
        void Done(string name, bool ok, string detail)
        {
            done.Add(name);
            check(name, ok, detail);
        }

        RedactRules rules = ProbeRules();

        // ①~③ 日志落盘：写若干行 → 落盘 → 读回。
        string logDir = Path.Combine(root, "logs");
        using (var log = new ResidentLog(logDir, rules))
        {
            log.Start();
            for (int i = 0; i < 5; i++)
            {
                log.Write("probe-line-" + i);
            }

            log.CommitNow();
            string? active = log.ActiveFilePath;
            string[] files = ListFiles(logDir);
            // 活动文件正被落盘线程的句柄占着：读它必须用共享读（File.ReadAllText 会报
            // IOException「being used by another process」，已实测）。
            string text = files.Length == 0
                ? string.Empty
                : Encoding.UTF8.GetString(DiagnosticBundle.ReadAllBytesShared(files[0]));
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Done("ui-diag-log-file-written",
                files.Length > 0 && lines.Length >= 5,
                $"files={files.Length} lines={lines.Length} dir={logDir}");

            bool tagged = lines.Length > 0 && lines.All(line =>
                line.Contains("agent_id=host", StringComparison.Ordinal)
                && line.Contains("timestamp=", StringComparison.Ordinal));
            Done("ui-diag-log-line-has-agent-timestamp", tagged, "lines=" + lines.Length);

            bool partDuring = active is not null && active.EndsWith(".part", StringComparison.Ordinal);
            log.Complete();
            string[] after = ListFiles(logDir);
            bool noPart = after.Length > 0
                && after.All(file => !file.EndsWith(".part", StringComparison.Ordinal));
            Done("ui-diag-log-part-until-complete", partDuring && noPart,
                $"active={active ?? "(none)"} after={string.Join(",", after)}");
        }

        // ④ 超过单文件上限会滚动（分批提交，避免缓冲把老行挤掉）。
        string rollDir = Path.Combine(root, "roll");
        using (var log = new ResidentLog(rollDir, rules))
        {
            log.Start();
            string filler = new string('x', FillerChars);
            for (int round = 0; round < 12; round++)
            {
                for (int i = 0; i < 100; i++)
                {
                    log.Write(filler);
                }

                log.CommitNow();
            }

            log.Complete();
            string[] files = ListFiles(rollDir);
            Done("ui-diag-log-rolls-on-size", files.Length >= 2,
                $"files={files.Length} names={string.Join(",", files.Select(Path.GetFileName))}");
        }

        // ⑤ 写日志不抛到调用点：目标是一个「是文件不是目录」的路径。
        string blocked = Path.Combine(root, "blocked-dir");
        File.WriteAllText(blocked, "not a directory");
        using (var log = new ResidentLog(blocked, rules))
        {
            log.Start();
            bool threw = false;
            try
            {
                log.Write("probe-line");
                log.CommitNow();
            }
            catch (Exception)
            {
                threw = true;
            }

            Done("ui-diag-log-write-never-throws", !threw, "threw=" + threw);
        }

        // ⑥ 用户目录前缀。
        string userText = rules.Text(@"C:\Users\testuser\AppData\Local\PetDock\logs\kernel.log");
        Done("ui-diag-redact-user-profile",
            !userText.Contains(ProbeUser, StringComparison.OrdinalIgnoreCase)
            && userText.Contains(RedactRules.UserProfilePlaceholder, StringComparison.Ordinal),
            "out=" + userText);

        // ⑦ 机器名。
        string machineText = rules.Text("host " + ProbeMachine + " up");
        Done("ui-diag-redact-machine-name",
            !machineText.Contains(ProbeMachine, StringComparison.OrdinalIgnoreCase)
            && machineText.Contains(RedactRules.MachinePlaceholder, StringComparison.Ordinal),
            "out=" + machineText);

        // ⑧ 插件目录。
        string pluginText = rules.Text(ProbePluginsRoot + @"\com.example.probe");
        Done("ui-diag-redact-plugin-root",
            !pluginText.Contains(ProbePluginsRoot, StringComparison.OrdinalIgnoreCase)
            && pluginText.Contains(RedactRules.PluginsRootPlaceholder, StringComparison.Ordinal),
            "out=" + pluginText);
    }

    /// <summary>探针用的脱敏规则（本机事实全部用假值）。</summary>
    private static RedactRules ProbeRules() => new(
        ProbeDataRoot,
        ProbePluginsRoot,
        exePath: null,
        userProfile: @"C:\Users\testuser",
        userName: ProbeUser,
        machineName: ProbeMachine);

    private static string[] ListFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "kernel-*") : Array.Empty<string>();

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}
