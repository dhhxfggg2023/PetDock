// 插件宿主自检（--hosttest）：对真实插件目录跑全生命周期 —— 发现 / 校验 / 装载 / tick /
// 禁用 / 恢复 / 热重载 / 即插即用 / tier2 拒绝，输出 CHECK <name> PASS/FAIL 行 + HOST-OK/HOST-FAIL。
// 期望插件 id 由命令行传入（缺省取第一个 Running 句柄的 id）—— 本文件不写任何真实插件名
// （docs/01 P-02 第 4 条）；夹具 id 一律来自 HostTestFleet 常量。
// 与 --selftest 的分工：selftest 验窗口/渲染路径；hosttest 验插件宿主（docs/13 §3 全链路）。
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal static class PluginHostSelfTest
{
    public static int Run(string[] args)
    {
        string? memoryProbeId = ParseFlagValue(args, "--memory-probe");
        string? expectedId = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--memory-probe", StringComparison.Ordinal))
            {
                i++; // 跳过该旗标的取值
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                expectedId = args[i];
                break;
            }
        }
        int failures = 0;

        // ———— 阶段一：仓库插件根 + 自动装载 ————
        string? root = PluginHost.ProbePluginsRoot();
        Check(ref failures, "probe-root", root is not null, root ?? "(null)");
        if (root is null)
        {
            Console.WriteLine("HOST-FAIL");
            return 1;
        }

        using (PluginHost host = new(root, 60, autoLoad: true))
        {
            host.Start();
            bool loaded = WaitFor(() => host.HandlesSnapshot().Any(h => h.State == PluginLifecycleState.Running), 8000);
            IReadOnlyList<PluginHandle> handles = host.HandlesSnapshot();
            Check(ref failures, "auto-load", loaded, $"handles={handles.Count}");
            Check(ref failures, "discover-count", host.Discover(root).Count >= 1, $"count={host.Discover(root).Count}");

            expectedId ??= handles.FirstOrDefault(h => h.State == PluginLifecycleState.Running)?.Manifest.Id;
            Console.WriteLine($"CHECK target-id INFO {expectedId ?? "(none)"}");
            if (expectedId is null)
            {
                Console.WriteLine("HOST-FAIL");
                return 1;
            }

            PluginHandle target = handles.First(h => h.Manifest.Id == expectedId);
            Check(ref failures, "target-running",
                host.Snapshot(() => target.State) == PluginLifecycleState.Running,
                $"id={target.Manifest.Id} state={target.State}");
            Check(ref failures, "sandbox-loaded", host.Snapshot(() => target.SandboxAlive), "carrier-wasm-live");
            // 装载即授予 = 声明 ∩ 白名单（M1 硬编码测试能力集，契约 §5.4）—— 无能力插件授予 0 亦合法
            int expectedLeases = target.Manifest.Capabilities.Count(c => CapabilityIds.IsWellKnown(c));
            Check(ref failures, "leases-granted",
                host.Snapshot(() => target.ActiveLeaseCount) == expectedLeases
                    && host.Snapshot(() => target.Leases.Count) == expectedLeases,
                $"active={host.Snapshot(() => target.ActiveLeaseCount)} granted={expectedLeases} declared={target.Manifest.Capabilities.Count}");

            bool ticking = WaitFor(() => host.Snapshot(() => target.Ticks) >= 3, 5000);
            Check(ref failures, "tick-progress", ticking, $"ticks={host.Snapshot(() => target.Ticks)}");

            int targetFaults = host.Snapshot(() => target.Sink.GuestFaultCount + target.Sink.BudgetCount);
            if (targetFaults == 0)
            {
            long eventsBefore = host.Snapshot(() => target.EventsReceived);
            host.PostPluginEvent("host.probe");
            bool gotEvent = WaitFor(() => host.Snapshot(() => target.EventsReceived) > eventsBefore, 5000);
            Check(ref failures, "events-running", gotEvent, $"events={host.Snapshot(() => target.EventsReceived)}");

            // —— 禁用：事件停送达、句柄吊销、状态保留（不卸载）——
            host.Disable(target).GetAwaiter().GetResult();
            Check(ref failures, "disable-state",
                host.Snapshot(() => target.State) == PluginLifecycleState.Suspended,
                $"state={target.State}");
            long eventsFrozen = host.Snapshot(() => target.EventsReceived);
            long ticksFrozen = host.Snapshot(() => target.Ticks);
            host.PostPluginEvent("host.probe");
            Thread.Sleep(500);
            Check(ref failures, "disable-events",
                host.Snapshot(() => target.EventsReceived) == eventsFrozen,
                $"events={host.Snapshot(() => target.EventsReceived)} frozen={eventsFrozen}");
            Check(ref failures, "disable-leases",
                host.Snapshot(() => target.ActiveLeaseCount) == 0,
                $"active=0 records-kept={host.Snapshot(() => target.Leases.Count)}");
            Check(ref failures, "disable-keeps-state",
                host.Snapshot(() => target.SandboxAlive) && host.Snapshot(() => target.Ticks) == ticksFrozen,
                "sandbox-alive ticks-stopped state-kept");

            // —— 恢复：句柄重新激活、事件恢复送达 ——
            target.Resume();
            Check(ref failures, "resume-state",
                host.Snapshot(() => target.State) == PluginLifecycleState.Running,
                $"state={target.State}");
            Check(ref failures, "resume-leases",
                host.Snapshot(() => target.ActiveLeaseCount) == expectedLeases,
                $"active={host.Snapshot(() => target.ActiveLeaseCount)} expected={expectedLeases}");
            long resumedAt = host.Snapshot(() => target.EventsReceived);
            host.PostPluginEvent("host.probe");
            bool gotAgain = WaitFor(() => host.Snapshot(() => target.EventsReceived) > resumedAt, 5000);
            Check(ref failures, "resume-events", gotAgain, $"events={host.Snapshot(() => target.EventsReceived)}");

            Check(ref failures, "no-faults",
                host.Snapshot(() => target.Sink.GuestFaultCount + target.Sink.BudgetCount) == 0,
                "zero-traps");
            }
            else
            {
                // 陷阱型目标（预算探针插件）：故障分支 —— 8 连故障 → 自动挂起链即禁用语义证据（t7）
                Console.WriteLine($"CHECK-DISPLAY target-faults faults={targetFaults} (budget-probe target)");
                failures += HostBudgetProbe.RunConsecutiveFaultDrive(root, expectedId);
            }
        }

        // ———— 阶段二：临时根 + 显式装载 + 热重载 + 即插即用 ————
        // 源插件运行期选定（第一个合法 tier1）—— 本文件不写死任何插件名
        string? sourceDir = null;
        PluginManifest? sourceManifest = null;
        using (PluginHost scanner = new(root, autoLoad: false))
        {
            foreach (string dir in scanner.Discover(root))
            {
                Result<PluginManifest, IReadOnlyList<ManifestError>> result = scanner.ValidateManifest(dir);
                if (result.IsOk && result.Ok!.Tier == 1)
                {
                    sourceDir = dir;
                    sourceManifest = result.Ok;
                    break;
                }
            }
        }
        if (sourceDir is null || sourceManifest is null)
        {
            Console.WriteLine("HOST-FAIL no-tier1-source-plugin");
            return 1;
        }

        string tempRoot = HostTestFleet.CreateTempRoot(sourceDir, out string goodDir);
        string goodId = sourceManifest.Id;
        try
        {
            using PluginHost temp = new(tempRoot, 50, autoLoad: false);
            temp.Start();

            Check(ref failures, "temp-discover",
                temp.Discover(tempRoot).Count == 3,
                $"count={temp.Discover(tempRoot).Count} (good+broken+tier2)");

            // 坏 manifest：拒绝装载并逐条给出原因
            Result<PluginManifest, IReadOnlyList<ManifestError>> broken = temp.ValidateManifest(Path.Combine(tempRoot, "broken"));
            Check(ref failures, "manifest-reject",
                !broken.IsOk && broken.Error!.Count >= 5,
                $"errors={broken.Error!.Count}");
            foreach (ManifestError error in broken.Error!)
            {
                Console.WriteLine($"CHECK-DISPLAY manifest-error {error.Field}: {error.Message}");
            }
            Check(ref failures, "broken-not-loaded",
                temp.HandlesSnapshot().All(h => h.Manifest.Id != HostTestFleet.BrokenId),
                "never-instantiated");

            // 显式装载（走 ResolveDir 回退路径：未经 ValidateManifest 预登记）
            PluginHandle good = (PluginHandle)temp.Load(sourceManifest, CancellationToken.None).GetAwaiter().GetResult();
            Check(ref failures, "temp-load",
                temp.Snapshot(() => good.State) == PluginLifecycleState.Running && good.SandboxAlive,
                $"id={good.Manifest.Id} state={good.State}");

            // —— 热重载：改 manifest 内容（version → 9.9.9），文件监听 → 调度线程片间替换 ——
            long ticksBefore = temp.Snapshot(() => good.Ticks);
            string goodManifestPath = Path.Combine(goodDir, ManifestValidator.ManifestFileName);
            JsonNode? node = JsonNode.Parse(File.ReadAllText(goodManifestPath));
            node!["version"] = "9.9.9";
            File.WriteAllText(goodManifestPath, node!.ToJsonString());

            bool reloaded = WaitFor(() => temp.Snapshot(() => good.ReloadCount) >= 1, 8000);
            Check(ref failures, "reload-count",
                reloaded && temp.Snapshot(() => good.ReloadCount) == 1,
                $"reloads={temp.Snapshot(() => good.ReloadCount)}");
            Check(ref failures, "reload-identity",
                ReferenceEquals(temp.HandlesSnapshot().FirstOrDefault(h => h.Manifest.Id == goodId), good),
                "same-handle-object");
            Check(ref failures, "reload-manifest",
                temp.Snapshot(() => good.Manifest.Version) == "9.9.9",
                $"version={good.Manifest.Version}");
            Check(ref failures, "reload-running",
                temp.Snapshot(() => good.State) == PluginLifecycleState.Running,
                $"state={good.State}");
            Check(ref failures, "reload-sandbox", temp.Snapshot(() => good.SandboxAlive), "new-store-live");
            Check(ref failures, "reload-ticks-monotonic",
                temp.Snapshot(() => good.Ticks) >= ticksBefore,
                $"ticks={temp.Snapshot(() => good.Ticks)} before={ticksBefore}");

            // —— mtime-only：内容不变，只改时间戳 → 重载判定跑过但无操作 ——
            int checksBefore = temp.Snapshot(() => good.ReloadChecks);
            File.SetLastWriteTimeUtc(goodManifestPath, DateTime.UtcNow.AddSeconds(2));
            bool attempted = WaitFor(() => temp.Snapshot(() => good.ReloadChecks) > checksBefore, 8000);
            Check(ref failures, "mtime-attempted", attempted, $"checks={temp.Snapshot(() => good.ReloadChecks)}");
            Check(ref failures, "mtime-noop",
                temp.Snapshot(() => good.ReloadCount) == 1,
                $"reloads-still={temp.Snapshot(() => good.ReloadCount)}");

            // —— 即插即用：新目录投放，发现 + 装载，core 无需任何改动 ——
            HostTestFleet.WriteSecondPlugin(tempRoot);
            Check(ref failures, "dropin-discover",
                temp.Discover(tempRoot).Count == 4,
                $"count={temp.Discover(tempRoot).Count}");
            Result<PluginManifest, IReadOnlyList<ManifestError>> second = temp.ValidateManifest(Path.Combine(tempRoot, "secondbrain"));
            Check(ref failures, "dropin-valid", second.IsOk,
                second.IsOk ? HostTestFleet.SecondId : string.Join("; ", second.Error!.Select(e => e.Field)));
            if (second.IsOk)
            {
                PluginHandle secondHandle = (PluginHandle)temp.Load(second.Ok!, CancellationToken.None).GetAwaiter().GetResult();
                Check(ref failures, "dropin-load",
                    temp.Snapshot(() => secondHandle.State) == PluginLifecycleState.Running,
                    $"id={secondHandle.Manifest.Id} state={secondHandle.State}");
            }

            // —— tier2：schema 合法但装载期拒绝（M1 无进程外沙箱）——
            Result<PluginManifest, IReadOnlyList<ManifestError>> tier2 = temp.ValidateManifest(Path.Combine(tempRoot, "heavybrain"));
            Check(ref failures, "tier2-schema-valid", tier2.IsOk, "manifest-ok");
            bool rejected = false;
            string rejectReason = "";
            try
            {
                temp.Load(tier2.Ok!, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (PluginLoadException ex)
            {
                rejected = true;
                rejectReason = string.Join("; ", ex.Reasons);
            }
            Check(ref failures, "tier2-reject", rejected, rejectReason);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ———— 阶段三（可选）：内存上限归因探针（--memory-probe <id>，t7）————
        if (memoryProbeId is not null)
        {
            failures += HostBudgetProbe.RunMemoryProbe(root, memoryProbeId);
        }

        Console.WriteLine(failures == 0 ? "HOST-OK" : "HOST-FAIL");
        return failures == 0 ? 0 : 1;
    }

    private static string? ParseFlagValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void Check(ref int failures, string name, bool ok, string detail = "")
    {
        Console.WriteLine($"CHECK {name} {(ok ? "PASS" : "FAIL")} {detail}".TrimEnd());
        if (!ok)
        {
            failures++;
        }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return condition();
    }
}
