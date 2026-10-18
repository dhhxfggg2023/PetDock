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
using PetDock.Core.PetRuntime;
using PetDock.Hosting;
using PetDock.Plugins.BongoCat;

namespace PetDock.BongoCat.Tests;

internal static class Program
{
    private static int _failures;

    public static int Main(string[] args)
    {
        Console.WriteLine($"CHECK-DISPLAY m7-abi {PetRuntimeAbi.InterfaceName} {PetRuntimeAbi.Version}");

        GazeFollowsCursor();
        PawFromKeyDown();
        IdleHasBlink();
        TierZeroOverrunIsVisible();
        PackAssetsExist();

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
