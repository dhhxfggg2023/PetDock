using System.Diagnostics;

namespace PetDock.Golden.Tests;

/// <summary>
/// 定位 DyberPet 夹具派生 pack（docs/32 §7 第 3 条的金图证据源），并在 pack 缺失时自举生成
/// （t10 / t9-1：金图不再依赖被 gitignore 的 artifacts/dyberpet-min-golden/，全新 clone 自足为绿）。
///
/// 优先级（高 → 低，永不吞掉高优先级的失败）：
///   1. <see cref="PackEnvVar"/> —— 显式指定 pack 根（含 petdock-sample.json）。指向损坏包时
///      必须红：这里只校验 manifest 存在，不校验帧内容，渲染链自然对缺帧红；绝不「坏包 → 自举」兜底。
///   2. artifacts/dyberpet-min-golden —— 本机已导入的 pack（gitignored，非交付物，存在则直接用）。
///   3. 自举 —— 上面两项都不存在（全新 clone / CI）：在 %TEMP% 下建一次性目录，跑
///      tools/gen-dyberpet-fixture.mjs 生成合成夹具，再跑 PetDock.DyberPetImporter 导入成 pack，
///      用完（渲染结束）随 PackHandle.Dispose 删除，%TEMP% 不残留。
///
/// 为何自举产物不进 tests/fixtures/：tests/fixtures/dyberpet-min/** 是 docs/41 D-13b 的合成夹具树，
/// 是 D-13c 门禁（tools/check-dyberpet-coverage.mjs）的漂移对账对象，且与 D-13a「零真实素材入库」并列；
/// 金图只「读」夹具、不「写」夹具——把 importer 派生的 pack（anim/*.json 内是绝对路径只读引用）写进
/// 被对账的树会把临时生成物混进夹具身份，破坏树哈希 4592ba8e… 的可判定性。故自举产物只落 %TEMP%。
/// </summary>
internal static class DyberPetPackLocator
{
    private const string PackEnvVar = "PETDOCK_DYBERPET_PACK";
    private const string ManifestName = "petdock-sample.json";
    public const string FixCommandHint =
        "先跑 importer --in tests/fixtures/dyberpet-min/dyberpet-flat --out artifacts/dyberpet-min-golden，" +
        "或设 PETDOCK_DYBERPET_PACK 指向有效 pack（本进程也会在 pack 缺失时自举，无需人工预生成；" +
        "自举要调 node 跑 tools/gen-dyberpet-fixture.mjs，所以 node 必须在 PATH 上）";

    /// <summary>一次性 pack 句柄：自举产物随 Dispose 删除（%TEMP% 无残留）；非自举产物不删。</summary>
    public sealed class PackHandle : IDisposable
    {
        public string Root { get; }
        private readonly string? _tempDir;

        internal PackHandle(string root, string? tempDir)
        {
            Root = root;
            _tempDir = tempDir;
        }

        public void Dispose()
        {
            if (_tempDir is null || !Directory.Exists(_tempDir))
            {
                return;
            }

            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                // 清理失败不掩盖测试结果本身；留一行诊断供排查残留。
                Console.WriteLine($"DYBERPET-PACK-CLEANUP-FAIL {_tempDir} {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static PackHandle Acquire()
    {
        // 1. 显式环境变量最高优先；坏就是坏，不兜底（t10 验收②）。
        string? fromEnv = Environment.GetEnvironmentVariable(PackEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            string full = Path.GetFullPath(fromEnv);
            if (!File.Exists(Path.Combine(full, ManifestName)))
            {
                throw new InvalidOperationException(
                    $"{PackEnvVar}={fromEnv} 下没有 {ManifestName} —— {FixCommandHint}");
            }

            return new PackHandle(full, tempDir: null);
        }

        // 2. 本机已导入的 artifacts 产物（gitignored；存在则直接用，不重建）。
        string? committed = FindArtifactsPack();
        if (committed is not null)
        {
            return new PackHandle(committed, tempDir: null);
        }

        // 3. 自举：mkdtemp 语义 → 生成器 → importer。
        return Bootstrap();
    }

    /// <summary>从 AppContext.BaseDirectory 向上找 artifacts/dyberpet-min-golden/petdock-sample.json。</summary>
    private static string? FindArtifactsPack()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "dyberpet-min-golden", ManifestName);
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        return null;
    }

    /// <summary>向上找含 tools/gen-dyberpet-fixture.mjs 的仓库根（生成器的唯一权威位置）。</summary>
    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "gen-dyberpet-fixture.mjs")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "找不到 tools/gen-dyberpet-fixture.mjs（仓库根）；金图自举需要它来生成合成夹具。");
    }

    private static PackHandle Bootstrap()
    {
        string repo = FindRepoRoot();
        string tempDir = Path.Combine(
            Path.GetTempPath(), "petdock-golden-dyberpet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string fixtureDir = Path.Combine(tempDir, "dyberpet-min");
            string packDir = Path.Combine(tempDir, "pack");

            // 生成器（node，无构建产物依赖）。
            Run(repo, "node",
                Path.Combine(repo, "tools", "gen-dyberpet-fixture.mjs"), "--out", fixtureDir);

            // importer（dotnet exec 已构建的 dll；sls build 会先产出它）。
            string importerDll = Path.Combine(
                repo, "packages", "plugins", "petdock.importer.dyberpet",
                "PetDock.DyberPetImporter", "bin", "Release", "net8.0-windows",
                "PetDock.DyberPetImporter.dll");
            if (!File.Exists(importerDll))
            {
                throw new FileNotFoundException(
                    "importer dll 未构建（先 dotnet build PetDock.sln -c Release）", importerDll);
            }

            Run(repo, "dotnet", "exec", importerDll,
                "--in", Path.Combine(fixtureDir, "dyberpet-flat"), "--out", packDir);

            if (!File.Exists(Path.Combine(packDir, ManifestName)))
            {
                throw new InvalidOperationException(
                    $"自举 importer 未产出 {ManifestName}（{packDir}）—— {FixCommandHint}");
            }

            return new PackHandle(packDir, tempDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理尽力而为 */ }
            throw;
        }
    }

    private static void Run(string workingDir, string file, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"金图自举启动 {file} 返回 null。");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string outText = stdout.Result;
        string errText = stderr.Result;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"金图自举失败 {file} {string.Join(' ', args)} exit={process.ExitCode}\n" +
                $"stdout: {outText.Trim()}\nstderr: {errText.Trim()}");
        }
    }
}
