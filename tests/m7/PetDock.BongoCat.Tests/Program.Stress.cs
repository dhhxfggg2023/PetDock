// M7 负载复现模式：把**整套 m7 判据**同时起 N 份，让「装配/状态推进」与断言取值之间
// 真的有竞争，从而把「只在 --all 里红、单独跑全绿」这件事变成一条可复跑的读数。
//
// 为什么需要它：`--all` 里前面跑过 window/perf 层，机器负载与冷启动节奏都不同；单独跑 m7 时
// 这些竞争几乎不发生，于是同一条判据时而绿时而红，红了也说不清是哪一侧的问题。本模式不改任何
// 判据的期望值、不加重试，只负责把负载造出来 —— 红不红仍然由判据自己说。
//
// 用法（**不在门禁路径上**，门禁照旧无参跑一遍）：
//   dotnet tests/m7/PetDock.BongoCat.Tests/bin/Release/net8.0-windows/PetDock.BongoCat.Tests.dll --stress [n]
// n 默认 6（= 6 份并发；本机实测 6 份约 6–13 秒）。末行 M7-STRESS-OK / M7-STRESS-FAIL failed=<k>，
// 退出码 0/1。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PetDock.BongoCat.Tests;

internal static partial class Program
{
    private const int StressDefaultParallel = 6;
    private const int StressRunTimeoutMs = 10 * 60 * 1000;

    private static int RunStress(int parallel)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("STRESS-ERROR 取不到本进程可执行文件路径（Environment.ProcessPath 为空）");
            Console.WriteLine("M7-STRESS-FAIL failed=0");
            return 1;
        }

        Console.WriteLine($"STRESS parallel={parallel} exe={exe}");
        Console.WriteLine("STRESS 说明：每一份都是完整的 m7 判据（同一份 Release 产物），并发起跑；"
            + "任何一份红都照原样报出来。");

        var runs = new List<(int Index, Process Proc, Task<string> Out, Task<string> Err)>();
        for (int i = 1; i <= parallel; i++)
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 子份必须是「跑整套判据」的角色，不能被环境变量选成 tier-2 客体。
            info.Environment.Remove("PETDOCK_TIER2_GUEST");
            var proc = new Process { StartInfo = info };
            proc.Start();
            runs.Add((i, proc, proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync()));
        }

        int failed = 0;
        int index = 0;
        foreach ((int number, Process proc, Task<string> outTask, Task<string> errTask) in runs)
        {
            index++;
            bool exited = proc.WaitForExit(StressRunTimeoutMs);
            if (!exited)
            {
                // 超时的那份也要收干净，否则它会继续占着磁盘/句柄影响后面的读数。
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                proc.WaitForExit();
            }

            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            int exitCode = exited ? proc.ExitCode : -1;
            string last = LastLine(stdout);
            var fails = new List<string>();
            foreach (string line in stdout.Split('\n'))
            {
                if (line.Contains(" FAIL ", StringComparison.Ordinal))
                {
                    fails.Add(line.Trim());
                }
            }

            if (exitCode != 0 || fails.Count > 0 || !exited)
            {
                failed++;
            }
            Console.WriteLine($"STRESS run={number} exit={exitCode} last={last} fails={fails.Count}"
                + (exited ? string.Empty : " TIMEOUT=1"));
            foreach (string line in fails)
            {
                Console.WriteLine($"STRESS run={number}   {line}");
            }
            if (stderr.Trim().Length > 0)
            {
                Console.WriteLine($"STRESS run={number}   stderr={FirstLine(stderr)}");
            }
        }

        Console.WriteLine($"STRESS parallel={parallel} runs={index} failed={failed}");
        Console.WriteLine(failed == 0 ? "M7-STRESS-OK" : $"M7-STRESS-FAIL failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static string LastLine(string text)
    {
        string[] lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                return lines[i].Trim();
            }
        }
        return "<empty>";
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }
        return "<empty>";
    }
}
