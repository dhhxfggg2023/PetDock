// t10 金图夹具的自检入口（docs/41 D-12）。
//
// 为什么不是 `dotnet test`：本机 DSH 沙箱拒绝 testhost 对父进程的 OpenProcess
// （Win32Exception (5) 拒绝访问，栈落在 SetParentProcessExitCallback → OpenProcess），
// 于是整个运行中止、一条断言都跑不到。这与 tests/unit（t9）同病，换 runner 版本也没用。
//
// 因此这里用 xunit.runner.utility 的 AssemblyRunner 在**本进程内**执行本工程 dll：
// 没有父进程、没有 testhost、没有回调。退出码 = 失败数（0 = 全绿），
// 输出形如 `SUMMARY total=N failed=M` 与 `TESTS-PASS` / `TESTS-FAIL N`。
//
// 用法（两条等价）：
//   dotnet run --project tests/golden/PetDock.Golden.Tests/PetDock.Golden.Tests.csproj -c Release
//   dotnet tests/golden/PetDock.Golden.Tests/bin/Release/net8.0-windows/PetDock.Golden.Tests.dll
//
// 生成 meta.json（D-12c）的独立模式：
//   dotnet …/PetDock.Golden.Tests.dll --write-meta
// 它只写源树 baselines/{clip}/frame-000.meta.json（GPU 型号 + 驱动 + sha256 + 已认证型号），
// 不跑任何测试。基准 PNG 本身由 core 的 golden-baseline 一次性脚本渲染（同一条链）。
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Xunit.Runners;

namespace PetDock.Golden.Tests;

/// <summary>进程内 xUnit 运行器（见文件头：绕开本机被沙箱拒绝的 testhost 路径）。</summary>
public static class Program
{
    /// <summary>入口。返回进程退出码：0 = 全部通过，1 = 有失败，2 = 用法/装载错误。</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--write-meta")
        {
            return BaselineGenerator.WriteMeta();
        }

        string? dll = args.FirstOrDefault();
        if (dll is null)
        {
            dll = Assembly.GetExecutingAssembly().Location;
            Console.WriteLine($"ENTRY-INFO default-dll {dll}");
        }

        if (!System.IO.File.Exists(dll))
        {
            Console.WriteLine($"ENTRY-FAIL dll-not-found path={dll}");
            return 2;
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.LoadFrom(dll);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ENTRY-FAIL assembly-load path={dll} ex={ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        using var runner = AssemblyRunner.WithoutAppDomain(assembly.Location);
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        int failed = 0;
        int total = 0;

        runner.OnTestPassed = info =>
        {
            total++;
            Console.WriteLine($"PASS {info.TestDisplayName}");
        };
        runner.OnTestFailed = info =>
        {
            total++;
            failed++;
            Console.WriteLine($"FAIL {info.TestDisplayName}");
            string msg = (info.ExceptionMessage ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            Console.WriteLine($"     {msg}");
        };
        runner.OnTestSkipped = info =>
        {
            total++;
            Console.WriteLine($"SKIP {info.TestDisplayName} ({info.SkipReason})");
        };
        runner.OnErrorMessage = _ => { };
        runner.OnExecutionComplete = info =>
        {
            Console.WriteLine($"SUMMARY total={info.TotalTests} failed={info.TestsFailed} skipped={info.TestsSkipped} time={info.ExecutionTime:F0}ms");
            Console.WriteLine(info.TestsFailed == 0 ? "TESTS-PASS" : $"TESTS-FAIL {info.TestsFailed}");
            done.TrySetResult(info.TestsFailed == 0 ? 0 : 1);
        };

        runner.Start();
        int code = await done.Task;

        Console.WriteLine($"LOCAL total={total} failed={failed}");
        return code;
    }
}
