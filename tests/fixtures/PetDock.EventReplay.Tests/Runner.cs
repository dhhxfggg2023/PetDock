// 本机可执行入口（不进 CI 判据；CI 走 dotnet test 的正常 xunit 通道）。
// 存在理由与 PetDock.AbiContract.Tests/Runner.cs 逐字相同：本机 DSH 沙箱让 testhost 在
// SetParentProcessExitCallback→OpenProcess 上必 Win32 5，`dotnet test` 与 `dotnet vstest`
// 都在一条断言都没跑时中止。出路是测试程序集自己当父进程：本 Main 反射执行本 assembly 里
// 带 [Fact] 的方法 —— 与被测代码同一批方法、Assert 抛出语义相同，只是宿主是自己。
//
// 输出行形态（t1 契约 §8.1 定死）：
//   EVENT-CHECK <class>.<method> PASS|FAIL <msg>
//   EVENT-CHECK <class>.<method> SKIP <reason>        ← SKIP 不是 PASS（§2.6）
//   EVENT-SKIP  n=<k> keys=<key,key,...>
//   EVENT-OK                      （exit 0）
//   EVENT-FAIL n=<k>              （exit k）
//
// 开关：
//   --list     列出全部 [Fact] 名
//   --strict   SKIP 计入失败数 —— t1 §2.6「SKIP 不是 PASS，不许计绿」的机器形态。
//              t2 落地后 t5 应带 --strict 跑，让每一处 SKIP 都变成必须回答的问题。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PetDock.EventReplay.Tests;

internal static class Runner
{
    private static int Main(string[] args)
    {
        bool strict = args.Contains("--strict", StringComparer.Ordinal);

        if (args.Contains("--list", StringComparer.Ordinal))
        {
            foreach (string name in Facts().Select(FixtureName)) Console.WriteLine(name);
            return 0;
        }

        // 验收④的**逐条**物证：把 CoverageLedger 的每一格（13 事件 × 2 格 + 5 模式 + 13×5 交叉）
        // 原样打出来（主语 / 状态 / 具名原因）。t5 照它逐条对账，不需要读代码。
        if (args.Contains("--coverage", StringComparer.Ordinal))
        {
            PrintCoverage();
            return 0;
        }

        int failures = 0;
        var skipped = new List<string>();
        foreach (MethodInfo fact in Facts())
        {
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(fact.DeclaringType!);
                fact.Invoke(instance, Array.Empty<object>());
                if (Skip.Current is string reason)
                {
                    // 判据自己声明本条不适用（例如核心侧入口尚未存在）：如实打印，不计绿。
                    skipped.Add(FixtureName(fact));
                    Console.WriteLine($"EVENT-CHECK {FixtureName(fact)} SKIP {reason}");
                }
                else
                {
                    Console.WriteLine($"EVENT-CHECK {FixtureName(fact)} PASS");
                }
            }
            catch (Exception error)
            {
                Exception root = error is TargetInvocationException tie && tie.InnerException is not null
                    ? tie.InnerException : error;
                failures++;
                Console.WriteLine($"EVENT-CHECK {FixtureName(fact)} FAIL {root.Message.Split('\n')[0]}");
            }
            finally
            {
                Skip.Current = null;
                (instance as IDisposable)?.Dispose();
            }
        }

        int total = failures + (strict ? skipped.Count : 0);
        if (skipped.Count > 0)
        {
            Console.WriteLine($"EVENT-SKIP n={skipped.Count} keys={string.Join(",", skipped)}");
        }

        Console.WriteLine(total == 0 ? "EVENT-OK" : $"EVENT-FAIL n={total}");
        return total;
    }

    private static string FixtureName(MethodInfo fact) => $"{fact.DeclaringType!.Name}.{fact.Name}";

    /// <summary>把覆盖对账逐格打出来（验收④的逐条物证）。</summary>
    private static void PrintCoverage()
    {
        Console.WriteLine($"EVENT-COVERAGE summary {CoverageLedger.Describe()}");
        foreach (CoverageLedger.Cell cell in CoverageLedger.Events())
        {
            Console.WriteLine($"EVENT-COVERAGE cell {cell}");
        }

        foreach (CoverageLedger.Cell cell in CoverageLedger.Modes())
        {
            Console.WriteLine($"EVENT-COVERAGE mode {cell}");
        }

        Console.WriteLine($"EVENT-COVERAGE cross-total {CoverageLedger.Cross().Count}");
        foreach (CoverageLedger.Cell cell in CoverageLedger.Cross())
        {
            Console.WriteLine($"EVENT-COVERAGE cross {cell}");
        }

        Console.WriteLine($"EVENT-COVERAGE surfaces {CoreSurface.Current.Detail}");
        Console.WriteLine("COVERAGE-OK");
    }

    private static MethodInfo[] Facts() =>
        typeof(Runner).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null && m.GetParameters().Length == 0))
            .ToArray();
}

/// <summary>
/// 判据向 Runner 声明「本条 SKIP 及其原因」的单槽位。Mark 把它置位，Runner 打印
/// EVENT-CHECK ... SKIP 并（在 --strict 下）计入失败。零依赖、无静态泄漏：Runner 每条后清空。
/// </summary>
internal static class Skip
{
    public static string? Current;

    public static void With(string reason) => Current = reason;
}
