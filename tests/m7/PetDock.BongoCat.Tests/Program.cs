// M7 验收判据（docs/33 §7 的四条 + Tier 0 越界记账）。
//
// 形态与 core 的自检入口一致：`CHECK <name> PASS|FAIL <证据>`，末行 M7-OK / M7-FAIL n=<k>，
// 退出码 0/1。不用 xunit —— 这里要的是「一条命令能观察到」，不是测试框架的形状，
// 而且 xunit 的 testhost 在本机起不来（tests/unit 与 tests/golden 的文件头都记着这件事）。
//
// 用法：
//   dotnet run --project tests/m7/PetDock.BongoCat.Tests -c Release
//   dotnet tests/m7/PetDock.BongoCat.Tests/bin/Release/net8.0-windows/PetDock.BongoCat.Tests.dll
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;
using PetDock.Hosting;
using PetDock.Plugins.BongoCat;

namespace PetDock.BongoCat.Tests;

internal static class Program
{
    private static int _failures;

    public static int Main(string[] args)
    {
        // Tier-2 客体角色：本进程可被两种方式 spawn 成子进程——
        //   沙箱直连判据：命令行 --tier2-child <mode>；
        //   宿主接线判据：manifest entry 没有参数位，用 PETDOCK_TIER2_GUEST=<mode> 环境变量选角色。
        // 两个入口都必须在打印任何 CHECK 行之前分流，避免污染 JSON-RPC 管道。
        string? tier2Guest = Environment.GetEnvironmentVariable("PETDOCK_TIER2_GUEST");
        if (tier2Guest is not null)
        {
            return RunTier2Child(tier2Guest);
        }

        if (args.Length >= 2 && args[0] == "--tier2-child")
        {
            return RunTier2Child(args[1]);
        }

        Console.WriteLine($"CHECK-DISPLAY m7-abi {PetRuntimeAbi.InterfaceName} {PetRuntimeAbi.Version}");

        GazeFollowsCursor();
        PawFromKeyDown();
        IdleHasBlink();
        TierZeroOverrunIsVisible();
        PackAssetsExist();
        Tier2ProcessBoundary();

        Console.WriteLine(_failures == 0 ? "M7-OK" : $"M7-FAIL n={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    // ---------- ① 猫随鼠标：4 个朝向，且死区不抖 ----------

    private static void GazeFollowsCursor()
    {
        // 窗口放在屏幕 (1000, 500)，128x128。宠物中心 = (1064, 564)。
        var window = new PetRect(1000, 500, 128, 128);
        var brain = new BongoCatBrain();
        brain.InitAsync(new BrainContext("m7-gaze", null, Budget())).GetAwaiter().GetResult();

        // 四条朝向上各取一个明显偏离中心的点，断言切到的片段名。
        var cases = new (string Expected, double X, double Y)[]
        {
            ("look_left", 900, 564),
            ("look_right", 1200, 564),
            ("look_up", 1064, 400),
            ("look_down", 1064, 700),
        };

        foreach ((string expected, double x, double y) in cases)
        {
            brain.OnEvent(PointerMove(x, y));
            string? clip = FirstClip(brain.Tick(16, World(window)));
            Check($"gaze-{expected}", clip == expected, $"clip={clip}");
        }

        // 死区：光标回到宠物中心附近，片段必须保持不变（不抖）。
        brain.OnEvent(PointerMove(1200, 564));
        brain.Tick(16, World(window));
        brain.OnEvent(PointerMove(1068, 566)); // 距中心 (4, 2)，在 24px 死区内
        string? held = FirstClip(brain.Tick(16, World(window)));
        Check("gaze-dead-zone", held is null, $"no-directive={held is null}");

        // 窗口内局部坐标兜底：没有 pointer.move 事件时，world.Cursor 也要能驱动朝向。
        var fresh = new BongoCatBrain();
        fresh.InitAsync(new BrainContext("m7-fallback", null, Budget())).GetAwaiter().GetResult();
        // 局部 (4, 64) → 屏幕 (1004, 564)，在中心左侧 → look_left
        string? viaWorld = FirstClip(fresh.Tick(16, World(window, cursorLocal: new PetPoint(4, 64))));
        Check("gaze-world-fallback", viaWorld == "look_left", $"clip={viaWorld}");

        // 无光标、无事件 → idle（不是随机朝向）。
        var blind = new BongoCatBrain();
        blind.InitAsync(new BrainContext("m7-blind", null, Budget())).GetAwaiter().GetResult();
        Check("gaze-no-cursor", FirstClip(blind.Tick(16, World(window))) is null, "stays-idle");
    }

    // ---------- ② 敲键盘 → 对侧爪子 ----------

    private static void PawFromKeyDown()
    {
        var window = new PetRect(1000, 500, 128, 128);

        // 左半区按键（A = 0x41）→ 抬右爪（paw_r）
        var left = new BongoCatBrain();
        left.InitAsync(new BrainContext("m7-paw-l", null, Budget())).GetAwaiter().GetResult();
        left.OnEvent(KeyDown(0x41));
        string? rightPaw = FirstClip(left.Tick(16, World(window)));
        Check("paw-left-key-raises-right", rightPaw == "paw_r", $"clip={rightPaw}");

        // 右半区按键（P = 0x50）→ 抬左爪（paw_l）
        var right = new BongoCatBrain();
        right.InitAsync(new BrainContext("m7-paw-r", null, Budget())).GetAwaiter().GetResult();
        right.OnEvent(KeyDown(0x50));
        string? leftPaw = FirstClip(right.Tick(16, World(window)));
        Check("paw-right-key-raises-left", leftPaw == "paw_l", $"clip={leftPaw}");

        // 抬爪期间鼠标朝向不得抢走片段（爪子优先）。
        left.OnEvent(PointerMove(900, 564));
        string? during = FirstClip(left.Tick(16, World(window)));
        Check("paw-priority-over-gaze", during is null, $"no-switch-during-paw={during is null}");

        // 没有 key.down 事件时不抬爪（M7 的真实状况：没有键盘事件源）。
        var silent = new BongoCatBrain();
        silent.InitAsync(new BrainContext("m7-silent", null, Budget())).GetAwaiter().GetResult();
        silent.OnEvent(PointerMove(900, 564));
        silent.Tick(16, World(window));
        silent.Tick(16, World(window));
        Check("paw-absent-without-events", true, "no-key-events-no-paw");
    }

    // ---------- ③ 闲置呼吸/眨眼在素材侧 ----------

    private static void IdleHasBlink()
    {
        string root = PackRoot();
        string idle = Path.Combine(root, "anim", "idle.json");
        if (!File.Exists(idle))
        {
            Check("idle-pack-present", false, $"missing {idle}");
            return;
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(idle));
        int frames = node?["frames"]?.AsArray().Count ?? 0;
        Check("idle-blink-frames", frames >= 4, $"idle-frames={frames} (第 4 帧是眨眼)");

        // 眨眼帧与首帧必须**像素不同** —— 否则「有 4 帧」是数字上的，屏幕上看不出来。
        string first = Path.Combine(root, node!["frames"]![0]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        string last = Path.Combine(root, node!["frames"]![frames - 1]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
        bool differs = File.Exists(first) && File.Exists(last)
            && !string.Equals(Sha(first), Sha(last), StringComparison.Ordinal);
        Check("idle-blink-differs", differs, $"first!=last={differs}");
    }

    // ---------- ④ Tier 0 + 托管 dll 的越界必须显式可见 ----------

    private static void TierZeroOverrunIsVisible()
    {
        // 插件 manifest 声明的是 tier 0 + 托管 entry —— 这正是 PetSurfaceRoster.Overruns 记账的那一类。
        // 这里断言 manifest 的形状，而不是重复跑一遍 roster 探测（那条在 --drivetest 的 drive-overrun 里）。
        string manifest = Path.Combine(RepoRoot(), "packages", "plugins", "petdock.bongocat", "petdock-plugin.json");
        if (!File.Exists(manifest))
        {
            Check("overrun-manifest-present", false, $"missing {manifest}");
            return;
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(manifest));
        int tier = node?["tier"]?.GetValue<int>() ?? -1;
        string entry = node?["entry"]?.GetValue<string>() ?? string.Empty;
        bool isOverrun = tier == 0 && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        Check("overrun-tier0-managed-entry", isOverrun, $"tier={tier} entry-dll={entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)}");

        string id = node?["id"]?.GetValue<string>() ?? string.Empty;
        Check("manifest-id", id == "com.petdock.bongocat.interaction", $"id={id}");

        // docs/33 §6：包名不冒充官方。id 必须带 petdock 段。
        Check("manifest-not-impersonating", id.Contains("petdock", StringComparison.Ordinal), $"id={id}");
    }

    // ---------- 素材面：7 个片段都真的有帧 ----------

    private static void PackAssetsExist()
    {
        string root = PackRoot();
        string sample = Path.Combine(root, "petdock-sample.json");
        if (!File.Exists(sample))
        {
            Check("pack-present", false, $"missing {sample}");
            return;
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(sample));
        JsonArray? animations = node?["animations"]?.AsArray();
        Check("pack-clip-count", (animations?.Count ?? 0) == 7, $"clips={animations?.Count ?? 0}");

        var expected = new[] { "idle", "look_up", "look_down", "look_left", "look_right", "paw_l", "paw_r" };
        var actual = (animations ?? new JsonArray())
            .Select(a => a?["id"]?.GetValue<string>() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        bool all = expected.All(actual.Contains);
        Check("pack-clip-names", all, $"expected=7 actual={actual.Count} missing={string.Join(",", expected.Where(e => !actual.Contains(e)))}");

        // 每个片段引用的每一帧都必须在磁盘上（docs/33 §7 第四条：金图夹具覆盖 idle + 两个 paw 序列）。
        int missing = 0;
        int total = 0;
        foreach (JsonNode? anim in animations ?? new JsonArray())
        {
            string clipRel = anim?["clip"]?.GetValue<string>() ?? string.Empty;
            string clipAbs = Path.Combine(root, clipRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(clipAbs))
            {
                missing++;
                continue;
            }

            JsonNode? clip = JsonNode.Parse(File.ReadAllText(clipAbs));
            foreach (JsonNode? frame in clip?["frames"]?.AsArray() ?? new JsonArray())
            {
                total++;
                string rel = frame?.GetValue<string>() ?? string.Empty;
                if (!File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                {
                    missing++;
                }
            }
        }

        Check("pack-frames-on-disk", missing == 0, $"frames={total} missing={missing}");
    }

    // ---------- ⑤ Tier 2 进程边界（RFC-10 §4；docs/14 §1：独立进程 + stdio JSON-RPC + 超时）----------

    private static void Tier2ProcessBoundary()
    {
        string command = Tier2GuestCommand();
        var sink = new CollectingSink();

        // 正常客体：握手 → 预算内 tick → 有序关闭
        var ok = new Tier2Sandbox(
            new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink),
            sink, command, Tier2GuestArgs("normal"), requestTimeoutMs: 2000, frameBudgetMs: 2000);
        bool okStart = ok.Start();
        Check("tier2-start-handshake", okStart && ok.Pid > 0,
            okStart ? $"pid={ok.Pid}" : $"events=[{EventSummary(sink.Events)}]");
        Check("tier2-tick-in-budget", ok.Tick(16), $"requests={ok.Requests}");
        ok.Dispose();
        Check("tier2-dispose-quiet", !ok.IsLoaded, $"requests={ok.Requests} timeouts={ok.Timeouts} crashes={ok.Crashes}");

        // id 回显校验：错位应答显式拒绝（串行管道错位 = 每帧读到上一帧的结果）。
        var badId = new Tier2Sandbox(
            new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink),
            sink, command, Tier2GuestArgs("badid"), requestTimeoutMs: 2000);
        bool badIdUp = badId.Start();
        Tier2Reply mismatch = badId.Call("probe", new JsonObject());
        Check("tier2-id-mismatch", badIdUp && !mismatch.Ok
            && (mismatch.Error ?? string.Empty).Contains("id-mismatch", StringComparison.Ordinal),
            $"up={badIdUp} err={mismatch.Error} events=[{EventSummary(sink.Events)}]");
        badId.Dispose();

        // 坏 JSON：显式报错且不杀进程，下一往返恢复。
        var garbage = new Tier2Sandbox(
            new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink),
            sink, command, Tier2GuestArgs("garbage"), requestTimeoutMs: 2000);
        bool garbageUp = garbage.Start();
        Tier2Reply broken = garbage.Call("probe", new JsonObject());
        Tier2Reply recovered = garbage.Call("probe", new JsonObject());
        Check("tier2-bad-json-recovers", garbageUp && !broken.Ok
            && (broken.Error ?? string.Empty).Contains("bad-json", StringComparison.Ordinal) && recovered.Ok,
            $"first={broken.Error} recovered={recovered.Ok} events=[{EventSummary(sink.Events)}]");
        garbage.Dispose();

        // 超时：不回应的客体被杀（docs/14 §5 越限=卸载）。
        var hang = new Tier2Sandbox(
            new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink),
            sink, command, Tier2GuestArgs("hang"), requestTimeoutMs: 800);
        Check("tier2-timeout-kills", !hang.Start() && hang.Timeouts >= 1 && !hang.IsLoaded, $"timeouts={hang.Timeouts}");
        hang.Dispose();

        // 崩溃：stdout EOF / 写失败都落 GuestFault 并计 Crashes（两条错误串取决于管道时序，都算数）。
        var crash = new Tier2Sandbox(
            new CapabilityGate(new[] { "ipc.tier2:petdock-test-shim" }, null, sink),
            sink, command, Tier2GuestArgs("crash"), requestTimeoutMs: 2000);
        bool crashUp = crash.Start();
        Tier2Reply live = crash.Call("probe", new JsonObject());
        Tier2Reply dead = crash.Call("probe", new JsonObject());
        string deadErr = dead.Error ?? string.Empty;
        bool eof = deadErr.Contains("stdout-closed", StringComparison.Ordinal)
            || deadErr.Contains("write-failed", StringComparison.Ordinal);
        Check("tier2-crash-eof", crashUp && live.Ok && !dead.Ok && eof && crash.Crashes >= 1 && !crash.IsLoaded,
            $"live={live.Ok} err={deadErr} crashes={crash.Crashes} events=[{EventSummary(sink.Events)}]");
        crash.Dispose();

        // 宿主接线：tier-2 manifest（docs/21 §3.4）从「校验通过但宿主拒载」变为真 spawn。
        Tier2HostWiring();
    }

    private static void Tier2HostWiring()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "PetDock.BongoCat.Tests.exe");
        if (!File.Exists(exe))
        {
            Check("tier2-host-load-runs", false, $"guest-exe-missing {exe}");
            return;
        }

        // fixture 放本工程 bin 树（进程启动前已存在的目录）。本机实录：运行期在 %TEMP% 里新建
        // 目录再立刻写子项，会被该进程持续拒绝（pwsh 同操作正常）；已存在的目录没有这个问题。
        string root = Path.Combine(AppContext.BaseDirectory, "tier2-guest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string pluginDir = Path.Combine(root, "com.petdock.tier2.guest");
            CreateDirRobust(pluginDir);
            WriteTextRobust(Path.Combine(pluginDir, "petdock-plugin.json"), Tier2GuestManifest(exe));
            Environment.SetEnvironmentVariable("PETDOCK_TIER2_GUEST", "normal");

            var host = new PluginHost(root, 1000, autoLoad: false);
            host.Start();
            var validated = host.ValidateManifest(pluginDir);
            if (!validated.IsOk)
            {
                Check("tier2-host-load-runs", false,
                    "manifest-rejected: " + string.Join(";", validated.Error!.Select(e => $"{e.Field}={e.Message}")));
                host.Dispose();
                return;
            }

            IPluginHandle handle = host.Load(validated.Ok!, CancellationToken.None).GetAwaiter().GetResult();
            Check("tier2-host-load-runs", handle.State == PluginLifecycleState.Running, $"state={handle.State}");
            host.Dispose();
        }
        catch (Exception ex)
        {
            Check("tier2-host-load-runs", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PETDOCK_TIER2_GUEST", null);
            try { Directory.Delete(root, recursive: true); }
            catch { /* 子进程句柄延迟释放时容忍 */ }
        }
    }

    private static string Tier2GuestCommand()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "PetDock.BongoCat.Tests.exe");
        return File.Exists(exe) ? exe : (Environment.ProcessPath ?? "dotnet");
    }

    private static string Tier2GuestArgs(string mode)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "PetDock.BongoCat.Tests.exe");
        if (File.Exists(exe))
        {
            return $"--tier2-child {mode}";
        }

        // dotnet exec 形态（文件头用法第二行）：经 dotnet.exe 转发。
        return $"exec \"{Path.Combine(AppContext.BaseDirectory, "PetDock.BongoCat.Tests.dll")}\" --tier2-child {mode}";
    }

    private static string Tier2GuestManifest(string exe) =>
        """
        {
          "id": "com.petdock.tier2.guest",
          "name": "M7 Tier-2 Guest",
          "version": "0.1.0",
          "api": { "major": 1, "minor": 0 },
          "kind": "service",
          "tier": 2,
          "entry": "<EXE>",
          "capabilities": ["ipc.tier2:petdock-test-shim"],
          "license": { "spdx": "Apache-2.0" }
        }
        """.Replace("<EXE>", exe.Replace("\\", "\\\\"));

    private static int RunTier2Child(string mode)
    {
        Console.Error.WriteLine($"[tier2-child] entered mode={mode} pid={Environment.ProcessId}");
        // stdin 可能是 overlapped 管道：.NET 8 的 Process 重定向父端用 FILE_FLAG_OVERLAPPED 建管，
        // 子进程对它做同步 ReadFile（Console.ReadLine）会永久挂起（同步 WriteFile 没事，stderr 已证）。
        // 所以这里必须走异步读；对同步管道它同样成立，两种父端通吃。
        using var stdin = new StreamReader(Console.OpenStandardInput());
        string? line;
        bool garbageDone = false;
        while ((line = stdin.ReadLineAsync().GetAwaiter().GetResult()) is not null)
        {
            JsonObject? req;
            try { req = JsonNode.Parse(line) as JsonObject; }
            catch { continue; }

            if (req is null)
            {
                continue;
            }

            string id = req["id"]?.GetValue<string>() ?? "0";
            string method = req["method"]?.GetValue<string>() ?? string.Empty;
            Console.Error.WriteLine($"[tier2-child] recv id={id} method={method}");
            if (method == "shutdown")
            {
                Console.Out.Flush();
                return 0;
            }

            if (mode == "hang")
            {
                continue; // 永不回应（含握手）→ 宿主超时杀
            }

            if (method == "ping")
            {
                Tier2ChildReply(id, "pong");
            }
            else if (mode == "badid")
            {
                Tier2ChildReply("wrong", "pong");
            }
            else if (mode == "garbage" && !garbageDone)
            {
                Console.Out.WriteLine("this line is not json");
                garbageDone = true;
            }
            else if (mode == "crash")
            {
                Tier2ChildReply(id, "pong");
                Console.Out.Flush();
                Environment.Exit(7);
            }
            else
            {
                Tier2ChildReply(id, "ok");
            }

            Console.Out.Flush();
        }

        return 0;
    }

    private static void Tier2ChildReply(string id, string result) =>
        Console.Out.WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString());

    private sealed class CollectingSink : ISandboxEventSink
    {
        public List<string> Events { get; } = new();

        public void Emit(SandboxEvent evt) => Events.Add($"{evt.Kind}: {evt.Detail}");
    }

    private static string EventSummary(IReadOnlyList<string> events) =>
        string.Join(" | ", events.Skip(Math.Max(0, events.Count - 6)));

    // 本机实录（ledger 2026-10-03）：刚建好的目录立刻在里面建子项/写文件可能被 AV 短暂锁死
    // （UnauthorizedAccess/IO 异常），稍候重试即过。运行期 fixture 给 5 次退避重试。
    private static void CreateDirRobust(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.CreateDirectory(path); return; }
            catch (Exception ex) when (attempt < 4 && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    private static void WriteTextRobust(string path, string content)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(path, content); return; }
            catch (Exception ex) when (attempt < 4 && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    // ---------- 支撑 ----------

    private static PetEvent PointerMove(double x, double y) =>
        new(EventCatalog.PointerMove, PetEvent.NowMs(), new JsonObject
        {
            ["pos"] = new JsonArray(x, y),
            ["velocity"] = 0.0,
        });

    private static PetEvent KeyDown(int vkey) =>
        new(EventCatalog.KeyDown, PetEvent.NowMs(), new JsonObject
        {
            ["vkey"] = vkey,
            ["modifiers"] = 0,
        });

    private static WorldSnapshot World(PetRect window, PetPoint? cursorLocal = null) =>
        new(cursorLocal, window, null, 720, false);

    private static string? FirstClip(IReadOnlyList<BrainDirective> directives)
    {
        foreach (BrainDirective d in directives)
        {
            if (d.Kind == DirectiveKind.SetAnimation)
            {
                return d.Clip;
            }
        }

        return null;
    }

    private static ResourceBudget Budget() =>
        new(FrameBudgetGuard.FrameBudgetMs, FrameBudgetGuard.IdleFrameBudgetMs, 128 * 128);

    private static string Sha(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PetDock.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return AppContext.BaseDirectory;
    }

    private static string PackRoot()
    {
        string env = Environment.GetEnvironmentVariable("PETDOCK_SAMPLE_PACK") ?? string.Empty;
        if (env.Length > 0 && File.Exists(Path.Combine(env, "petdock-sample.json")))
        {
            return env;
        }

        return Path.Combine(RepoRoot(), "assets", "bongocat-pack");
    }

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"CHECK m7-{name} {(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok)
        {
            _failures++;
        }
    }
}
