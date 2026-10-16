// 本机可执行入口（不进 CI 判据；CI 走 dotnet test 的正常 xunit 通道）。
// 存在理由：账本 mistakes.md 2026-10-16 两条 —— 这台机器的 DSH 沙箱让 testhost 在
// SetParentProcessExitCallback→OpenProcess 上必 Win32 5，`dotnet test` 与 `dotnet vstest`
// 都在**一条断言都没跑**时中止。ledger 给的 sanctioned 出路 (a)：测试程序集自己当父进程、
// 由自检面驱动。本 Main 反射执行本assembly 里带 [Fact] 的方法 —— 与被测代码同一批方法、
// Assert 抛出语义相同（xunit.assert 的异常照抛），只是宿主是自己。
// 输出行形态与 core 自检一致：`ABI-CHECK <class>.<method> PASS|FAIL [原因]`，末行
// `ABI-OK`（exit 0）/ `ABI-FAIL n=<失败数>`（exit n）。
using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PetDock.AbiContract.Tests;

internal static class Runner
{
    private static int Main(string[] args)
    {
        if (args.Contains("--list", StringComparer.Ordinal))
        {
            foreach (string name in Facts().Select(f => f.Name)) Console.WriteLine(name);
            return 0;
        }

        int failures = 0;
        foreach (MethodInfo fact in Facts())
        {
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(fact.DeclaringType!);
                fact.Invoke(instance, Array.Empty<object>());
                Console.WriteLine($"ABI-CHECK {fact.DeclaringType!.Name}.{fact.Name} PASS");
            }
            catch (Exception error)
            {
                Exception root = error is TargetInvocationException tie && tie.InnerException is not null
                    ? tie.InnerException : error;
                failures++;
                Console.WriteLine($"ABI-CHECK {fact.DeclaringType!.Name}.{fact.Name} FAIL {root.Message.Split('\n')[0]}");
            }
            finally
            {
                (instance as IDisposable)?.Dispose();
            }
        }

        Console.WriteLine(failures == 0 ? "ABI-OK" : $"ABI-FAIL n={failures}");
        return failures;
    }

    private static MethodInfo[] Facts() =>
        typeof(Runner).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsPublic && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null && m.GetParameters().Length == 0))
            .ToArray();
}
