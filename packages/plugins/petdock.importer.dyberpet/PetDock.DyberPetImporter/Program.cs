using PetDock.DyberPet;

// DyberPet importer CLI（M3：可命令的转换入口，契约 §3——不做 UI/CLI 工程，那是 M5/原规格 18 §5 的范围）。
//   --probe <dir>   只探测布局并打印审计行（t4 夹具联调用）
//   --in <dir>      真包目录（只读）；产物 = petdock-sample.json + anim/*.json + dyberpet-provenance.json
//   --out <dir>     产物目录；缺省 = <仓库根>/artifacts/dyberpet/<包名>（artifacts/ 已被 gitignore）
// 退出码：0 成功；1 用法/IO/解析错误；2 布局拒收（0.4 档不接收，契约 §1）。
if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("usage: PetDock.DyberPetImporter --in <dyberpet-pack-dir> [--out <dir>]");
    Console.Error.WriteLine("       PetDock.DyberPetImporter --probe <dir>");
    return 1;
}

string? probeArg = GetArg(args, "--probe");
if (probeArg is not null)
{
    LayoutProbe probe = DyberPetLayout.Probe(probeArg);
    Console.Error.WriteLine($"dyberpet-layout {probe.Layout} confidence={probe.Confidence.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}"
        + $" flat-actions={probe.FlatActions} nested-actions={probe.NestedActions}"
        + $" evidence={string.Join(",", probe.Evidence)}");
    return 0;
}

string? inArg = GetArg(args, "--in");
if (inArg is null)
{
    Console.Error.WriteLine("dyberpet-import error=missing --in（用法见 --help）");
    return 1;
}

if (!Directory.Exists(inArg))
{
    Console.Error.WriteLine($"dyberpet-import error=source-not-found source={inArg}");
    return 1;
}

string outArg = GetArg(args, "--out") ?? DefaultOutDir(inArg);
try
{
    ImportResult result = DyberPetImporter.Import(inArg, outArg);
    Console.Out.WriteLine(
        $"imported layout={result.Layout} clips={result.Clips} frames={result.TotalFrames}"
        + $" natural={result.NaturalWidth}x{result.NaturalHeight} focus={result.Focus ?? "<none>"} out={result.OutDir}");
    return 0;
}
catch (DyberPetImporter.LayoutRejectException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"dyberpet-import error={ex.GetType().Name} message={ex.Message}");
    return 1;
}

static string? GetArg(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>缺省产物根：从 --in 向上找 .git（仓库根）/artifacts/dyberpet/<包名>；找不到就用 CWD。</summary>
static string DefaultOutDir(string sourceDir)
{
    string? current = Path.GetFullPath(sourceDir);
    string packName = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current, ".git")))
        {
            return Path.Combine(current, "artifacts", "dyberpet", packName);
        }

        current = Path.GetDirectoryName(current);
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "dyberpet", packName);
}
