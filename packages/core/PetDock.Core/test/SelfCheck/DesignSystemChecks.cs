// 设计系统的判据（R2）：令牌、对比度、图标、主题切换。
//
// 这一组存在的理由：设计系统最容易「写的时候很整齐，三个月后各处冒出自定义的灰色」。
// 四条判据里前两条是静态扫描（令牌唯一来源、色值不许散落），后两条是数值判定
// （WCAG 对比度真的够、每个图标真的有字形）。它们都不看长相 —— 长相归人眼。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PetDock.Ui.Design;
using MediaColor = System.Windows.Media.Color;

namespace PetDock.Hosting;

internal static class DesignSystemChecks
{
    /// <summary>唯一允许出现十六进制色值与 Color.FromRgb 的文件。</summary>
    private const string TokensFile = "packages/core/PetDock.Ui/src/Design/Tokens.cs";

    private static readonly Regex HexLiteral = new("#[0-9A-Fa-f]{6}", RegexOptions.Compiled);
    private static readonly Regex FromRgb = new("Color\\.FromRgb", RegexOptions.Compiled);

    public static void Run(Report check)
    {
        TokensOnly(check);
        Contrast(check);
        IconCoverage(check);
        ThemeSwitch(check);
    }

    /// <summary>全工程只许 Tokens.cs 出现色值；别的文件写了就是红。</summary>
    private static void TokensOnly(Report check)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            check("ui-design-tokens-only", false, "repo-root not found");
            return;
        }

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in SourceFiles(Path.Combine(root, "packages", "core", "PetDock.Ui")))
        {
            scanned++;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (string.Equals(rel, TokensFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = Read(file);
            foreach (string line in text.Split('\n'))
            {
                if (HexLiteral.IsMatch(line) || FromRgb.IsMatch(line))
                {
                    offenders.Add(rel);
                    break;
                }
            }
        }

        check("ui-design-tokens-only",
            scanned > 0 && offenders.Count == 0,
            $"scanned={scanned} offenders={(offenders.Count == 0 ? "none" : string.Join(",", offenders))}");
    }

    /// <summary>正文对比度真的够（WCAG AA：正文 4.5，强调文字同样 4.5）。</summary>
    private static void Contrast(Report check)
    {
        var failures = new List<string>();
        foreach ((string name, Palette p) in new[] { ("dark", Palette.Dark), ("light", Palette.Light) })
        {
            // 正文在三个底上都要能读：窗口底、卡片、卡片亮档。
            Require(failures, name + ".TextPrimary/Background", p.TextPrimary, p.Background, 4.5);
            Require(failures, name + ".TextPrimary/Surface", p.TextPrimary, p.Surface, 4.5);
            Require(failures, name + ".TextPrimary/SurfaceAlt", p.TextPrimary, p.SurfaceAlt, 4.5);
            Require(failures, name + ".TextSecondary/Surface", p.TextSecondary, p.Surface, 4.5);
            Require(failures, name + ".TextSecondary/SurfaceAlt", p.TextSecondary, p.SurfaceAlt, 4.5);

            // 强调色上的文字（按钮）：这是最容易被配色玩坏的一对。
            Require(failures, name + ".AccentText/Accent", p.AccentText, p.Accent, 4.5);

            // 状态色也要能当文字用（错误条/警告条的正文）。
            Require(failures, name + ".Danger/Surface", p.Danger, p.Surface, 4.5);
            Require(failures, name + ".Warning/Surface", p.Warning, p.Surface, 4.5);
            Require(failures, name + ".Success/Surface", p.Success, p.Surface, 4.5);
        }

        check("ui-design-contrast", failures.Count == 0,
            failures.Count == 0 ? "pairs=18 min=4.5" : string.Join(" ", failures));
    }

    private static void Require(List<string> failures, string pair, MediaColor fg, MediaColor bg, double min)
    {
        double ratio = Ratio(fg, bg);
        if (ratio < min)
        {
            failures.Add($"{pair}={ratio:0.00}<{min:0.0}");
        }
    }

    /// <summary>WCAG 相对亮度比：(L1+0.05)/(L2+0.05)。</summary>
    private static double Ratio(MediaColor a, MediaColor b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double high = Math.Max(la, lb);
        double low = Math.Min(la, lb);
        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(MediaColor c)
    {
        double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>每个图标名字都有字形；未定义的名字拿兜底，而不是一个空白洞。</summary>
    private static void IconCoverage(Report check)
    {
        var missing = new List<string>();
        foreach (IconName name in Icons.All())
        {
            if (name == IconName.None)
            {
                continue;
            }

            string glyph = Icons.Glyph(name);
            if (string.IsNullOrEmpty(glyph) || glyph == "\0")
            {
                missing.Add(name.ToString());
            }
        }

        string unknown = Icons.Glyph((IconName)9999);
        bool fallback = !string.IsNullOrEmpty(Icons.Fallback) && unknown == Icons.Fallback;
        check("ui-design-icon-coverage",
            missing.Count == 0 && fallback,
            $"icons={Icons.All().Length} missing={(missing.Count == 0 ? "none" : string.Join(",", missing))} fallback={fallback}");
    }

    /// <summary>切主题真的换配色、同模式不重复发事件、能切回来。</summary>
    private static void ThemeSwitch(Report check)
    {
        ThemeMode before = Theme.Mode;
        int events = 0;
        EventHandler handler = (_, _) => events++;
        Theme.Changed += handler;
        try
        {
            Theme.SetMode(ThemeMode.Light);
            bool light = ReferenceEquals(Theme.Current, Palette.Light) && events == 1;

            Theme.SetMode(ThemeMode.Light);
            bool idempotent = events == 1;

            Theme.SetMode(ThemeMode.Dark);
            bool dark = ReferenceEquals(Theme.Current, Palette.Dark) && events == 2;

            check("ui-design-theme-switch", light && idempotent && dark,
                $"light={light} idempotent={idempotent} dark={dark} events={events}");
        }
        finally
        {
            Theme.Changed -= handler;
            Theme.SetMode(before);
        }
    }

    private static IEnumerable<string> SourceFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = file.Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PetDock.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}