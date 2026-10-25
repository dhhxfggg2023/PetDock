// 预算判据探针（t7）：把两条沙箱预算保证从「不可判定」变成「可判定 + 原始证据」。
// (1) 连续故障驱动：专用宿主 ManualTickOnly 逐片同步驱动，8 连故障 → 自动挂起的计数链逐片可读；
// (2) 内存上限归因：同一探针模块在三种 Store 上限下各跑一片，按 trap 消息分类归因。
// 探针 id 一律由调用方（命令行）传入 —— 本文件不写任何插件名（docs/01 P-02 第 4 条）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

internal static class HostBudgetProbe
{
    /// <summary>连续故障 → 自动挂起（契约 §5.3）：专用宿主手动切片驱动目标插件，
    /// 每片打印 计数/状态/tick，断言计数 1..8 逐片递增、第 8 片挂起、挂起后切片冻结且沙箱保留。
    /// 判据行交给调用方的账本（与 --hosttest 同一份统计）—— 探针不再自己数失败数，
    /// 否则它的 CHECK 行既不进 pass 也不进 fail，收尾统计就成了半张账（判据体检 S2 同一类洞）。</summary>
    public static void RunConsecutiveFaultDrive(string pluginsRoot, string expectedId, SkipLedger ledger)
    {
        using PluginHost host = new(pluginsRoot, 600000, autoLoad: false);
        host.ManualTickOnly = true;
        host.Start();

        PluginManifest? manifest = FindManifestById(host, pluginsRoot, expectedId);
        if (manifest is null)
        {
            ledger.Fail("budget-drive", $"target-not-found id={expectedId}");
            return;
        }

        PluginHandle handle = (PluginHandle)host.Load(manifest, CancellationToken.None).GetAwaiter().GetResult();
        long ticksAtLoad = host.Snapshot(() => handle.Ticks);

        bool chainOk = true;
        PluginLifecycleState lastState = PluginLifecycleState.Running;
        for (int i = 1; i <= 8; i++)
        {
            host.TickNow();
            int faults = host.Snapshot(() => handle.ConsecutiveFaults);
            long ticks = host.Snapshot(() => handle.Ticks);
            lastState = host.Snapshot(() => handle.State);
            Console.WriteLine($"CHECK-DISPLAY budget-drive slice={i} faults={faults} ticks={ticks} state={lastState}");
            if (faults != i || ticks != ticksAtLoad + i)
            {
                chainOk = false;
            }
        }
        ledger.Check("budget-chain", chainOk, "faults 1..8 monotonic one-per-slice");
        ledger.Check("budget-suspend",
            lastState == PluginLifecycleState.Suspended,
            $"state={lastState} at-fault-8");

        long frozenTicks = host.Snapshot(() => handle.Ticks);
        host.TickNow();
        host.TickNow();
        ledger.Check("budget-ticks-stop",
            host.Snapshot(() => handle.Ticks) == frozenTicks
                && host.Snapshot(() => handle.State) == PluginLifecycleState.Suspended,
            $"ticks={host.Snapshot(() => handle.Ticks)} frozen={frozenTicks}");
        ledger.Check("budget-keeps-state",
            host.Snapshot(() => handle.SandboxAlive),
            "sandbox-alive state-kept (auto-suspend = suspend, not unload)");

        string trap = FirstTrap(host.Snapshot(() => handle.Sink.Describe()));
        Console.WriteLine($"CHECK-DISPLAY budget-trap {trap}");
        ledger.Check("budget-trap", trap.Length > 0,
            trap.Length > 0 ? "trap-message-captured" : "no-trap-message");
    }

    /// <summary>内存上限归因（docs/14 §5 150MB）：同一探针模块在三种 Store 上限下各跑一片。
    /// 预期分类：32MB → divide by zero（长在第一段就被拒）；150MB → unreachable（恰在 2400→2401 页被拒）；
    /// 1GB → fuel（两段增长都成功，切片先被 CPU 预算截住）。判据行交给调用方的账本。</summary>
    public static void RunMemoryProbe(string pluginsRoot, string probeId, SkipLedger ledger)
    {
        string? modulePath = null;
        using (PluginHost scanner = new(pluginsRoot, autoLoad: false))
        {
            PluginManifest? manifest = FindManifestById(scanner, pluginsRoot, probeId);
            if (manifest is not null)
            {
                modulePath = Path.Combine(FindDirById(scanner, pluginsRoot, probeId) ?? string.Empty, manifest.Entry);
            }
        }
        if (modulePath is null || !File.Exists(modulePath))
        {
            ledger.Fail("memory-probe", $"probe-not-found id={probeId}");
            return;
        }
        byte[] module = File.ReadAllBytes(modulePath);

        RunOneMemoryCase(ledger, "limit-32mb", 32L * 1024 * 1024, "divide by zero", module);
        RunOneMemoryCase(ledger, "limit-150mb", 150L * 1024 * 1024, "unreachable", module);
        RunOneMemoryCase(ledger, "limit-1gb", 1024L * 1024 * 1024, "fuel", module);
    }

    private static void RunOneMemoryCase(SkipLedger ledger, string name, long memoryBytes, string expectNeedle, byte[] module)
    {
        ProbeSink sink = new();
        CapabilityGate gate = new(Array.Empty<string>(), null, sink);
        using PluginSandbox sandbox = new(gate, sink, new SandboxBudgets
        {
            MemoryBytes = memoryBytes,
            FuelPerSlice = 1_000_000,
        });
        if (!sandbox.Load(module))
        {
            ledger.Check($"memory-{name}", false, "sandbox-load-failed");
            return;
        }
        sandbox.Tick();
        string trap = sink.LastTrap();
        bool hit = trap.Contains(expectNeedle, StringComparison.Ordinal);
        ledger.Check($"memory-{name}", hit, $"trap=\"{trap}\" expect~\"{expectNeedle}\"");
    }

    private static PluginManifest? FindManifestById(PluginHost host, string pluginsRoot, string id)
    {
        foreach (string dir in host.Discover(pluginsRoot))
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> r = host.ValidateManifest(dir);
            if (r.IsOk && r.Ok!.Id == id)
            {
                return r.Ok;
            }
        }
        return null;
    }

    private static string? FindDirById(PluginHost host, string pluginsRoot, string id)
    {
        foreach (string dir in host.Discover(pluginsRoot))
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> r = host.ValidateManifest(dir);
            if (r.IsOk && r.Ok!.Id == id)
            {
                return dir;
            }
        }
        return null;
    }

    private static string FirstTrap(string described)
    {
        foreach (string part in described.Split(" | "))
        {
            string entry = part.Trim();
            if (entry.StartsWith("BudgetExceeded:", StringComparison.Ordinal)
                || entry.StartsWith("GuestFault:", StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return string.Empty;
    }

    /// <summary>探针自用事件落点：只记最近一条预算/故障事件原文（归因证据）。</summary>
    private sealed class ProbeSink : ISandboxEventSink
    {
        private readonly object _gate = new();
        private string _lastTrap = string.Empty;

        public void Emit(SandboxEvent evt)
        {
            lock (_gate)
            {
                if (evt.Kind is SandboxEventKind.BudgetExceeded or SandboxEventKind.GuestFault)
                {
                    _lastTrap = evt.Detail;
                }
            }
        }

        public string LastTrap()
        {
            lock (_gate)
            {
                return _lastTrap;
            }
        }
    }

}
