// 清单静态校验入口（--validate-plugins）：从 Program.cs 拆出（D-03c 单文件 400 行）。
// 语义不变：只做 schema/语义校验，不装载插件、不建窗口 —— 门禁的 L0 要能在任何机器上跑。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PetDock.Hosting;

namespace PetDock.Core;

internal static partial class Program
{
    /// <summary>
    /// 清单静态校验入口（--validate-plugins [path...]）：默认扫 packages/plugins。
    /// 判据行 `MANIFEST-OK path id` / `MANIFEST-FAIL path 字段: 原因`，末行 MANIFEST-PASS|FAIL。
    /// 只做 schema/语义校验，不装载插件、不建窗口 —— 门禁的 L0 要能在任何机器上跑。
    /// </summary>
    private static int RunManifestValidate(string[] targets)
    {
        var dirs = new List<string>();
        foreach (string raw in targets.Length > 0 ? targets : new[] { "packages/plugins" })
        {
            string full = Path.GetFullPath(raw);
            if (File.Exists(full))
            {
                dirs.Add(Path.GetDirectoryName(full) ?? full);
            }
            else if (File.Exists(Path.Combine(full, ManifestValidator.ManifestFileName)))
            {
                dirs.Add(full);
            }
            else if (Directory.Exists(full))
            {
                CollectManifestDirs(full, dirs);
            }
            else
            {
                Console.WriteLine($"MANIFEST-FAIL path={raw} 不存在");
                return ExitFail;
            }
        }

        if (dirs.Count == 0)
        {
            Console.WriteLine("MANIFEST-FAIL 没有找到任何 petdock-plugin.json");
            return ExitFail;
        }

        int failed = 0;
        foreach (string dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Result<PluginManifest, IReadOnlyList<ManifestError>> result = ManifestValidator.Validate(dir);
            string label = Path.GetRelativePath(Environment.CurrentDirectory, dir).Replace('\\', '/');
            if (result.IsOk && result.Ok is { } manifest)
            {
                Console.WriteLine($"MANIFEST-OK path={label} id={manifest.Id} tier={manifest.Tier} kind={manifest.Kind}");
                continue;
            }

            failed++;
            Console.WriteLine($"MANIFEST-FAIL path={label}");
            foreach (ManifestError error in result.Error ?? Array.Empty<ManifestError>())
            {
                Console.WriteLine($"  - {error.Field}: {error.Message}");
            }
        }

        Console.WriteLine($"STAT manifests {dirs.Count} failed {failed}");
        Console.WriteLine(failed == 0 ? "MANIFEST-PASS" : "MANIFEST-FAIL");
        return failed == 0 ? ExitOk : ExitFail;
    }
}
