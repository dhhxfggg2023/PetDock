// 宿主自检的插件编队（临时目录夹具）。全部写入 %TEMP% 下独立目录，测试完由调用方清理；
// 绝不改动仓库内插件文件 —— 热重载的「改动插件文件」发生在临时副本上（goodbrain）。
// 夹具 id 一律 com.example.*，与仓库插件无同名冲突；tier2 夹具用于装载期拒绝路径。
using System;
using System.IO;

namespace PetDock.Hosting;

internal static class HostTestFleet
{
    public const string BrokenId = "bad-hyphen";
    public const string Tier2Id = "com.example.heavybrain";
    public const string SecondId = "com.example.secondbrain";

    /// <summary>临时插件根：goodbrain（从仓库复制来的合法 tier1）+ broken（manifest 多处违规）
    /// + heavybrain（schema 合法但 tier2，装载期拒绝）。</summary>
    public static string CreateTempRoot(string sourcePluginDir, out string goodDir)
    {
        string root = Path.Combine(Path.GetTempPath(), "petdock-hosttest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        goodDir = Path.Combine(root, "goodbrain");
        Directory.CreateDirectory(goodDir);
        CopyPluginFiles(sourcePluginDir, goodDir);

        string brokenDir = Path.Combine(root, "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, ManifestValidator.ManifestFileName), BrokenManifest);
        File.WriteAllText(Path.Combine(brokenDir, "main.js"), "// broken fixture entry\n");

        string tier2Dir = Path.Combine(root, "heavybrain");
        Directory.CreateDirectory(tier2Dir);
        File.WriteAllText(Path.Combine(tier2Dir, ManifestValidator.ManifestFileName), Tier2Manifest);
        File.WriteAllText(Path.Combine(tier2Dir, "main.js"), "// tier2 fixture entry\n");
        return root;
    }

    /// <summary>投放第二个合法 tier1 插件（目录级即插即用验证：不重启宿主、不改 core）。</summary>
    public static void WriteSecondPlugin(string root)
    {
        string dir = Path.Combine(root, "secondbrain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ManifestValidator.ManifestFileName), SecondManifest);
        File.WriteAllText(Path.Combine(dir, "main.js"), "// second fixture entry\n");
    }

    /// <summary>递归复制：源插件的 entry 可能是子目录（docs/21 允许 `src/main.js` 这类相对路径），
    /// 只拷顶层文件会让合法插件在临时根里变成 entry-not-found。
    /// 跳过构建产物目录（bin/obj），它们不是插件内容且体量大。</summary>
    private static void CopyPluginFiles(string sourceDir, string targetDir)
    {
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string sub in Directory.GetDirectories(sourceDir))
        {
            string name = Path.GetFileName(sub);
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string subTarget = Path.Combine(targetDir, name);
            Directory.CreateDirectory(subTarget);
            CopyPluginFiles(sub, subTarget);
        }
    }

    // 十二处违规：id 连字符、name 空、version 两段、api.major=0、api.minor=-1、api.extra 未知键、
    // kind 非法、tier=5、capabilities 重复 + 未知项、entry 非字符串、顶层未知键、license 整体缺失。
    private const string BrokenManifest = """
        {
          "id": "bad-hyphen",
          "name": "",
          "version": "1.0",
          "api": { "major": 0, "minor": -1, "extra": 1 },
          "kind": "widget",
          "tier": 5,
          "capabilities": ["input.mousePosition", "input.mousePosition", "time.travel"],
          "entry": 123,
          "nope": true
        }
        """;

    // schema 合法但 tier=2：装载期按 docs/13 §1 法则二 / docs/14 §4 拒绝（M1 不做进程外沙箱）。
    private const string Tier2Manifest = """
        {
          "id": "com.example.heavybrain",
          "name": "Heavy Brain Fixture",
          "version": "0.1.0",
          "api": { "major": 1, "minor": 0 },
          "kind": "brain",
          "tier": 2,
          "entry": "main.js",
          "capabilities": ["ipc.tier2:demo"],
          "license": { "spdx": "MIT" }
        }
        """;

    private const string SecondManifest = """
        {
          "id": "com.example.secondbrain",
          "name": "Second Brain Fixture",
          "version": "0.2.0",
          "api": { "major": 1, "minor": 0 },
          "kind": "brain",
          "tier": 1,
          "entry": "main.js",
          "capabilities": ["persist.read:self"],
          "license": { "spdx": "Apache-2.0" }
        }
        """;
}
