// 插件宿主自检（--hosttest）：对真实插件目录跑全生命周期 —— 发现 / 校验 / 装载 / tick /
// 禁用 / 恢复 / 热重载 / 即插即用 / tier2 拒绝，输出 CHECK <name> PASS/FAIL|SKIP 行，
// 统计行 STAT hosttest …，末行 HOST-OK skipped=<s> / HOST-FAIL n=<fail+unowned> skipped=<s>。
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
    /// <summary>收尾账本：CHECK 行全部过它，SKIP 只从它出（见 SkipLedger.cs）。
    /// 判据体检 S2（2026-10-25）之前，ConsentPersistChecks / ConsentHostChecks 里三处
    /// `Report(…, true, "…(skipped)")` 把跳过塞进 PASS —— 它们的消费方正是本文件。</summary>
    private static readonly SkipLedger Ledger = new(Console.WriteLine);

    public static int Run(string[] args)
    {
        // 负例探针（仅诊断用，见 SkipLedger.ProbeFromArgs）：默认不生效，
        // 让「不可忽略的 SKIP 能把 HOST-OK 改成 HOST-FAIL」这件事可以被当场证明。
        if (SkipLedger.ProbeFromArgs(args) is SkipKind forced)
        {
            Ledger.Skip(forced, "skip-probe", $"probe=forced-by-caller kind={forced}");
        }
        string? memoryProbeId = ParseFlagValue(args, "--memory-probe");
        string? expectedId = null;
        for (int i = 0; i < args.Length; i++)
        {
            // 带取值的旗标：取值本身不是「期望的插件 id」，必须跳过。
            // 少了这一步，`--hosttest --log tmp.log` 会把 tmp.log 当成插件 id 去查句柄。
            if (string.Equals(args[i], "--memory-probe", StringComparison.Ordinal)
                || string.Equals(args[i], "--log", StringComparison.Ordinal))
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
        // ———— 阶段一：仓库插件根 + 自动装载 ————
        string? root = PluginHost.ProbePluginsRoot();
        Ledger.Check("probe-root", root is not null, root ?? "(null)");
        if (root is null)
        {
            Ledger.Report("hosttest", "HOST-OK", "HOST-FAIL");
            return Ledger.Verdict;
        }

        using (PluginHost host = new(root, 60, autoLoad: true))
        {
            host.Start();
            bool loaded = WaitFor(() => host.HandlesSnapshot().Any(h => h.State == PluginLifecycleState.Running), 8000);
            IReadOnlyList<PluginHandle> handles = host.HandlesSnapshot();
            Ledger.Check("auto-load", loaded, $"handles={handles.Count}");
            Ledger.Check("discover-count", host.Discover(root).Count >= 1, $"count={host.Discover(root).Count}");

            expectedId ??= handles.FirstOrDefault(h => h.State == PluginLifecycleState.Running)?.Manifest.Id;
            Console.WriteLine($"CHECK target-id INFO {expectedId ?? "(none)"}");
            if (expectedId is null)
            {
                Console.WriteLine("HOST-FAIL");
                return 1;
            }

            PluginHandle target = handles.First(h => h.Manifest.Id == expectedId);
            Ledger.Check("target-running",
                host.Snapshot(() => target.State) == PluginLifecycleState.Running,
                $"id={target.Manifest.Id} state={target.State}");
            Ledger.Check("sandbox-loaded", host.Snapshot(() => target.SandboxAlive), "carrier-wasm-live");
            // 装载即授予 = 声明 ∩ 白名单（M1 硬编码测试能力集，契约 §5.4）—— 无能力插件授予 0 亦合法
            int expectedLeases = target.Manifest.Capabilities.Count(c => CapabilityIds.IsWellKnown(c));
            Ledger.Check("leases-granted",
                host.Snapshot(() => target.ActiveLeaseCount) == expectedLeases
                    && host.Snapshot(() => target.Leases.Count) == expectedLeases,
                $"active={host.Snapshot(() => target.ActiveLeaseCount)} granted={expectedLeases} declared={target.Manifest.Capabilities.Count}");

            bool ticking = WaitFor(() => host.Snapshot(() => target.Ticks) >= 3, 5000);
            Ledger.Check("tick-progress", ticking, $"ticks={host.Snapshot(() => target.Ticks)}");

            int targetFaults = host.Snapshot(() => target.Sink.GuestFaultCount + target.Sink.BudgetCount);
            if (targetFaults == 0)
            {
            long eventsBefore = host.Snapshot(() => target.EventsReceived);
            host.PostPluginEvent("host.probe");
            bool gotEvent = WaitFor(() => host.Snapshot(() => target.EventsReceived) > eventsBefore, 5000);
            Ledger.Check("events-running", gotEvent, $"events={host.Snapshot(() => target.EventsReceived)}");

            // —— 禁用：事件停送达、句柄吊销、状态保留（不卸载）——
            host.Disable(target).GetAwaiter().GetResult();
            Ledger.Check("disable-state",
                host.Snapshot(() => target.State) == PluginLifecycleState.Suspended,
                $"state={target.State}");
            long eventsFrozen = host.Snapshot(() => target.EventsReceived);
            long ticksFrozen = host.Snapshot(() => target.Ticks);
            host.PostPluginEvent("host.probe");
            Thread.Sleep(500);
            Ledger.Check("disable-events",
                host.Snapshot(() => target.EventsReceived) == eventsFrozen,
                $"events={host.Snapshot(() => target.EventsReceived)} frozen={eventsFrozen}");
            Ledger.Check("disable-leases",
                host.Snapshot(() => target.ActiveLeaseCount) == 0,
                $"active=0 records-kept={host.Snapshot(() => target.Leases.Count)}");
            Ledger.Check("disable-keeps-state",
                host.Snapshot(() => target.SandboxAlive) && host.Snapshot(() => target.Ticks) == ticksFrozen,
                "sandbox-alive ticks-stopped state-kept");

            // —— 恢复：句柄重新激活、事件恢复送达 ——
            target.Resume();
            Ledger.Check("resume-state",
                host.Snapshot(() => target.State) == PluginLifecycleState.Running,
                $"state={target.State}");
            Ledger.Check("resume-leases",
                host.Snapshot(() => target.ActiveLeaseCount) == expectedLeases,
                $"active={host.Snapshot(() => target.ActiveLeaseCount)} expected={expectedLeases}");
            long resumedAt = host.Snapshot(() => target.EventsReceived);
            host.PostPluginEvent("host.probe");
            bool gotAgain = WaitFor(() => host.Snapshot(() => target.EventsReceived) > resumedAt, 5000);
            Ledger.Check("resume-events", gotAgain, $"events={host.Snapshot(() => target.EventsReceived)}");

            Ledger.Check("no-faults",
                host.Snapshot(() => target.Sink.GuestFaultCount + target.Sink.BudgetCount) == 0,
                "zero-traps");
            }
            else
            {
                // 陷阱型目标（预算探针插件）：故障分支 —— 8 连故障 → 自动挂起链即禁用语义证据（t7）
                Console.WriteLine($"CHECK-DISPLAY target-faults faults={targetFaults} (budget-probe target)");
                HostBudgetProbe.RunConsecutiveFaultDrive(root, expectedId, Ledger);
            }

            // —— 授权链路（RFC-13 §3）：装载期询问、拒绝后不再问、tier 0 也产租约 ——
            // 放在最后：收窄授权会改闸门状态，放前面会污染 disable/resume 判据。
            HostCheck consentCheck = (name, ok, detail) => Ledger.Check(name, ok, detail);
            ConsentHostChecks.Run(host, target.Manifest, consentCheck);
            ConsentHostChecks.Tier0Leases(host, consentCheck);
            ConsentHostChecks.RestrictConsentCheck(host, target.Manifest.Id, consentCheck);

            // —— 跨进程授权记忆（RFC-19 §3）：落盘往返、播种、仅本次不落盘、收窄后落盘 ——
            // 放在授权链路之后：它会改宿主的记忆与落盘状态。
            ConsentPersistChecks.Run(host, target.Manifest.Id, consentCheck);
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
            Ledger.Fail("no-tier1-source-plugin", "阶段二需要至少一个合法 tier1 插件");
            Ledger.Report("hosttest", "HOST-OK", "HOST-FAIL");
            return Ledger.Verdict;
        }

        string tempRoot = HostTestFleet.CreateTempRoot(sourceDir, out string goodDir);
        string goodId = sourceManifest.Id;
        try
        {
            using PluginHost temp = new(tempRoot, 50, autoLoad: false);
            temp.Start();

            Ledger.Check("temp-discover",
                temp.Discover(tempRoot).Count == 3,
                $"count={temp.Discover(tempRoot).Count} (good+broken+tier2)");

            // 坏 manifest：拒绝装载并逐条给出原因
            Result<PluginManifest, IReadOnlyList<ManifestError>> broken = temp.ValidateManifest(Path.Combine(tempRoot, "broken"));
            Ledger.Check("manifest-reject",
                !broken.IsOk && broken.Error!.Count >= 5,
                $"errors={broken.Error!.Count}");
            foreach (ManifestError error in broken.Error!)
            {
                Console.WriteLine($"CHECK-DISPLAY manifest-error {error.Field}: {error.Message}");
            }
            Ledger.Check("broken-not-loaded",
                temp.HandlesSnapshot().All(h => h.Manifest.Id != HostTestFleet.BrokenId),
                "never-instantiated");

            // 显式装载（走 ResolveDir 回退路径：未经 ValidateManifest 预登记）
            PluginHandle good = (PluginHandle)temp.Load(sourceManifest, CancellationToken.None).GetAwaiter().GetResult();
            Ledger.Check("temp-load",
                temp.Snapshot(() => good.State) == PluginLifecycleState.Running && good.SandboxAlive,
                $"id={good.Manifest.Id} state={good.State}");

            // —— 热重载：改 manifest 内容（version → 9.9.9），文件监听 → 调度线程片间替换 ——
            long ticksBefore = temp.Snapshot(() => good.Ticks);
            string goodManifestPath = Path.Combine(goodDir, ManifestValidator.ManifestFileName);
            JsonNode? node = JsonNode.Parse(File.ReadAllText(goodManifestPath));
            node!["version"] = "9.9.9";
            File.WriteAllText(goodManifestPath, node!.ToJsonString());

            bool reloaded = WaitFor(() => temp.Snapshot(() => good.ReloadCount) >= 1, 8000);
            Ledger.Check("reload-count",
                reloaded && temp.Snapshot(() => good.ReloadCount) == 1,
                $"reloads={temp.Snapshot(() => good.ReloadCount)}");
            Ledger.Check("reload-identity",
                ReferenceEquals(temp.HandlesSnapshot().FirstOrDefault(h => h.Manifest.Id == goodId), good),
                "same-handle-object");
            Ledger.Check("reload-manifest",
                temp.Snapshot(() => good.Manifest.Version) == "9.9.9",
                $"version={good.Manifest.Version}");
            Ledger.Check("reload-running",
                temp.Snapshot(() => good.State) == PluginLifecycleState.Running,
                $"state={good.State}");
            Ledger.Check("reload-sandbox", temp.Snapshot(() => good.SandboxAlive), "new-store-live");
            Ledger.Check("reload-ticks-monotonic",
                temp.Snapshot(() => good.Ticks) >= ticksBefore,
                $"ticks={temp.Snapshot(() => good.Ticks)} before={ticksBefore}");

            // —— mtime-only：内容不变，只改时间戳 → 重载判定跑过但无操作 ——
            int checksBefore = temp.Snapshot(() => good.ReloadChecks);
            File.SetLastWriteTimeUtc(goodManifestPath, DateTime.UtcNow.AddSeconds(2));
            bool attempted = WaitFor(() => temp.Snapshot(() => good.ReloadChecks) > checksBefore, 8000);
            Ledger.Check("mtime-attempted", attempted, $"checks={temp.Snapshot(() => good.ReloadChecks)}");
            Ledger.Check("mtime-noop",
                temp.Snapshot(() => good.ReloadCount) == 1,
                $"reloads-still={temp.Snapshot(() => good.ReloadCount)}");

            // —— 即插即用：新目录投放，发现 + 装载，core 无需任何改动 ——
            HostTestFleet.WriteSecondPlugin(tempRoot);
            Ledger.Check("dropin-discover",
                temp.Discover(tempRoot).Count == 4,
                $"count={temp.Discover(tempRoot).Count}");
            Result<PluginManifest, IReadOnlyList<ManifestError>> second = temp.ValidateManifest(Path.Combine(tempRoot, "secondbrain"));
            Ledger.Check("dropin-valid", second.IsOk,
                second.IsOk ? HostTestFleet.SecondId : string.Join("; ", second.Error!.Select(e => e.Field)));
            if (second.IsOk)
            {
                PluginHandle secondHandle = (PluginHandle)temp.Load(second.Ok!, CancellationToken.None).GetAwaiter().GetResult();
                Ledger.Check("dropin-load",
                    temp.Snapshot(() => secondHandle.State) == PluginLifecycleState.Running,
                    $"id={secondHandle.Manifest.Id} state={secondHandle.State}");
            }

            // —— tier2：schema 合法但装载期拒绝（M1 无进程外沙箱）——
            Result<PluginManifest, IReadOnlyList<ManifestError>> tier2 = temp.ValidateManifest(Path.Combine(tempRoot, "heavybrain"));
            Ledger.Check("tier2-schema-valid", tier2.IsOk, "manifest-ok");
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
            Ledger.Check("tier2-reject", rejected, rejectReason);
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
            HostBudgetProbe.RunMemoryProbe(root, memoryProbeId, Ledger);
        }

        // ———— tier-2 超时语义（客体是本 exe 自己，不需要窗口，也不需要插件根）————
        Tier2StateChecks.Run((name, ok, detail) => Ledger.Check(name, ok, detail));

        // 收尾：三种结局都从账本过（见 SkipLedger.cs）—— 末行 marker 带 skipped=N，
        // 「没人接的跳过」判红；--hosttest 的 marker 语义没变，仍是 HOST-OK / HOST-FAIL。
        Ledger.Report("hosttest", "HOST-OK", "HOST-FAIL");
        return Ledger.Verdict;
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
