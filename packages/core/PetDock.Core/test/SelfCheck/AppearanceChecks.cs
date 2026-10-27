// 外观母体的机器判据（docs/18 §2.6，2026-10-28）。
//
// 这一片把四页、三个弹窗、托盘菜单从系统默认外观换成一套自绘令牌，可验收的读数是四件事：
//   ① 源码里不再有裸色/裸字体/裸 MessageBox ——「换了一处、漏了三处」是这类改造最常见的残局；
//   ② 控件树里不再有系统默认外观的 Button/CheckBox/RadioButton/ComboBox/NumericUpDown/TabControl；
//   ③ 四档缩放（100/125/150/200%）下令牌真的按 DPI 放大（令牌、字号、像素三者的关系可量）；
//   ④ 四档缩放下面板与弹窗里的每段文字都放得下 —— 这条是用户看到的那一个结论，
//      ①~③ 都只是它的必要条件。
//
// 判据不 Show 任何窗（无人值守）：只 CreateControl + 逐层 PerformLayout。
// 口径与 docs/18 §2.6 的清单一一对应，双向记账：清单里有的这里必有一条，反之亦然。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui;
using PetDock.Core.Ui.Design;

namespace PetDock.Hosting;

internal static partial class AppearanceChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    private static readonly int[] Scales = { 96, 120, 144, 192 };

    public static void Run(Report check)
    {
        SourceTokens(check);
        ControlKinds(check);
        DpiTokens(check);
        TextFits(check);
        Material(check);
        SampleArtPresent(check);
    }

    // ⑥ 真素材的判据在 AppearanceSampleChecks.cs（同一个 partial 类，D-03c 单文件行数上限）。

    // ⑤ 材质的判据在 AppearanceMaterialChecks.cs（同一个 partial 类，D-03c 单文件行数上限）。

    /// <summary>①：Design/ 之外不许出现裸色、裸字体、裸对话框。</summary>
    private static void SourceTokens(Report check)
    {
        // Design/ 是令牌与自绘基元的家：那里写 Color.FromArgb 是**设计**，不是漏网。
        bool OutsideDesign(string rel) => rel.Contains("/Ui/Design/", StringComparison.Ordinal)
            || rel.EndsWith("/Ui/Design/Theme.cs", StringComparison.Ordinal);

        bool BareColor(string line) =>
            !line.StartsWith("//", StringComparison.Ordinal)
            && (line.Contains("Color.FromArgb(", StringComparison.Ordinal)
                || line.Contains("SystemColors.", StringComparison.Ordinal));

        bool BareFont(string line) =>
            !line.StartsWith("//", StringComparison.Ordinal)
            && line.Contains("new Font(", StringComparison.Ordinal);

        bool BareDialog(string line) =>
            !line.StartsWith("//", StringComparison.Ordinal)
            && line.Contains("MessageBox.Show(", StringComparison.Ordinal);

        int colors = SourceScan.Count("packages/core/PetDock.Core/src/Ui", BareColor, OutsideDesign);
        int fonts = SourceScan.Count("packages/core/PetDock.Core/src/Ui", BareFont, OutsideDesign);
        int dialogs = SourceScan.Count(SourceScan.CoreSourceDir, BareDialog, skipFile: null);

        // -1 = 找不到仓库根：什么也没检查，必须红（SourceScan 的口径）。
        check("ui-appearance-source-tokens", colors == 0 && fonts == 0 && dialogs == 0,
            $"bare-colors={colors} bare-fonts={fonts} bare-dialogs={dialogs} scan={(colors < 0 ? "no-repo-root" : "ok")}");
    }

    /// <summary>②：控件树里只有自绘控件（系统默认外观的控件一律算缺陷）。</summary>
    private static void ControlKinds(Report check)
    {
        var offenders = new List<string>();
        int surfaces = 0;

        void Inspect(string surface, Control root)
        {
            surfaces++;
            foreach (Control control in Walk(root))
            {
                string kind = NativeKind(control);
                if (kind.Length == 0)
                {
                    continue;
                }

                // 自绘控件都继承自系统控件（ThemedButton : Button），继承者不算缺陷。
                if (control.GetType().Namespace == typeof(Theme).Namespace)
                {
                    continue;
                }

                offenders.Add($"{surface}/{Describe(control)}:{kind}");
            }
        }

        using (var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false))
        {
            ForceLayout(panel);
            Inspect("panel", panel);
            foreach (string page in PanelPages.All)
            {
                panel.ShowPage(page);
                ForceLayout(panel);
                Inspect("panel/" + page, panel);
            }
        }

        using (var consent = new AuthorizationDialog("示例宠物", "sample-pet", 1, new[] { "input.mousePosition" }))
        {
            ForceLayout(consent);
            Inspect("consent", consent);
        }

        using (var quit = new ConfirmDialog(I18n.Get("ui.tray.exitConfirm"), I18n.Get("ui.tray.exitConfirmTitle")))
        {
            ForceLayout(quit);
            Inspect("quit", quit);
        }

        using (var onboarding = new OnboardingWindow(() => null, WriteNothing, hasPet: true))
        {
            ForceLayout(onboarding);
            Inspect("onboarding", onboarding);
        }

        check("ui-appearance-control-kinds", offenders.Count == 0,
            $"surfaces={surfaces} offenders={offenders.Count} detail={string.Join(",", offenders)}");
    }

    /// <summary>③：令牌与字号真的按 DPI 放大（四档、单调、比例正确）。</summary>
    private static void DpiTokens(Report check)
    {
        var problems = new List<string>();
        int lastDip = 0;
        int lastBody = 0;
        foreach (int dpi in Scales)
        {
            int dip = Theme.Dip(Theme.SpaceLg, dpi);
            int expected = (int)Math.Round(Theme.SpaceLg * dpi / 96.0, MidpointRounding.AwayFromZero);
            if (dip != expected)
            {
                problems.Add($"dip({dpi})={dip}!={expected}");
            }

            // 字号是像素单位：10pt 在 dpi 下等于 10*dpi/72 像素。
            float body = Theme.FontFor(TypeRole.Body, dpi).Size;
            float wanted = 10f * dpi / 72f;
            if (Math.Abs(body - wanted) > 0.6f)
            {
                problems.Add($"body({dpi})={body:0.0}!={wanted:0.0}");
            }

            if (dip < lastDip || body < lastBody)
            {
                problems.Add($"not-monotonic({dpi})");
            }

            lastDip = dip;
            lastBody = (int)Math.Round(body);
        }

        // 同一 DPI 下必须有层级：标题 > 正文 > 说明，否则「字号角色」等于没分层。
        bool ladder = Theme.FontFor(TypeRole.Title, 96).Size > Theme.FontFor(TypeRole.Body, 96).Size
            && Theme.FontFor(TypeRole.Body, 96).Size >= Theme.FontFor(TypeRole.Caption, 96).Size;

        check("ui-appearance-dpi-tokens", problems.Count == 0 && ladder,
            $"scales={string.Join("/", Scales)} problems={string.Join(",", problems)} ladder={ladder}");
    }

    /// <summary>④：四档缩放下面板与弹窗里的每段文字都放得下（用户看到的结论）。</summary>
    private static void TextFits(Report check)
    {
        var offenders = new List<string>();
        int surfaces = 0;
        int labels = 0;

        void Measure(string surface, Control root)
        {
            surfaces++;
            // 隐藏页/隐藏步骤不参与 Dock 布局（WinForms 的 LayoutEngine 跳过不可见控件），
            // 于是它们的尺寸停在 Panel 的默认 200×100 上。判据要量的是「用户看到的那一刻」，
            // 所以先把整棵树临时置为可见再量，量完还原 —— 这里断的是尺寸，不是可见性。
            Dictionary<Control, bool> wasVisible = EnsureAllVisible(root);
            try
            {
                ForceLayout(root);
                foreach (Control control in Walk(root))
                {
                    string text = control.Text ?? string.Empty;
                    if (text.Length == 0 || control.Width <= 0 || control.Height <= 0)
                    {
                        continue;
                    }

                    string? problem = FitProblem(control, text);
                    if (problem is null)
                    {
                        if (control is Label)
                        {
                            labels++;
                        }

                        continue;
                    }

                    offenders.Add($"{surface}/{Describe(control)}:{problem}");
                }
            }
            finally
            {
                foreach (KeyValuePair<Control, bool> pair in wasVisible)
                {
                    pair.Key.Visible = pair.Value;
                }
            }
        }

        try
        {
            foreach (int dpi in Scales)
            {
                Theme.SetSimulatedDpi(dpi);
                using (var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false))
                {
                    foreach (string page in PanelPages.All)
                    {
                        panel.ShowPage(page);
                        Measure($"panel@{dpi}/{page}", panel);
                    }
                }

                using (var consent = new AuthorizationDialog("示例宠物", "sample-pet", 1,
                           new[] { "input.mousePosition", "network.fetch:api.example.com" }))
                {
                    Measure($"consent@{dpi}", consent);
                }

                using (var quit = new ConfirmDialog(I18n.Get("ui.tray.exitConfirm"), I18n.Get("ui.tray.exitConfirmTitle")))
                {
                    Measure($"quit@{dpi}", quit);
                }

                using (var onboarding = new OnboardingWindow(() => null, WriteNothing, hasPet: true))
                {
                    Measure($"onboarding@{dpi}", onboarding);
                }
            }
        }
        finally
        {
            // 模拟档只能活在判据里：生产路径与其它判据一律按真机 DPI 走。
            Theme.SetSimulatedDpi(0);
        }

        check("ui-appearance-text-fits", offenders.Count == 0,
            $"surfaces={surfaces} labels={labels} offenders={offenders.Count} detail={string.Join(",", offenders)}");
    }

    /// <summary>一段文字在它的控件里放得下吗？null = 放得下。</summary>
    private static string? FitProblem(Control control, string text)
    {
        Font font = control.Font;
        const TextFormatFlags Single = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        if (control is ThemedButton)
        {
            // 自绘按钮的文字区 = 控件宽 - 左右各一个 SpaceSm（ThemedButton.OnPaint 的同一口径）。
            int inset = Theme.Dip(control, Theme.SpaceSm) * 2;
            int need = Ink.TextWidth(text, font);
            return need <= Math.Max(1, control.Width - inset)
                ? null
                : $"button-text need={need} avail={control.Width - inset}";
        }

        if (control is Label label)
        {
            int availW = label.ClientSize.Width - label.Padding.Horizontal;
            int availH = label.ClientSize.Height - label.Padding.Vertical;
            if (availW <= 0 || availH <= 0)
            {
                // 还没有拿到尺寸（例如父容器尚未布局）：这一条什么也没证明，算缺陷，
                // 免得「所有标签都是 0 宽」这种状态把判据混成绿的。
                return $"unlaid-out avail={availW}x{availH}";
            }

            Size one = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), Single);
            if (one.Width <= availW)
            {
                return null;
            }

            Size wrapped = TextRenderer.MeasureText(
                text, font, new Size(availW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            return wrapped.Height <= availH
                ? null
                : $"label-text need={one.Width} avail={availW} wrapped={wrapped.Height}>{availH}";
        }

        return null;
    }

    /// <summary>系统默认外观的控件（返回空串 = 不在清单里）。</summary>
    private static string NativeKind(Control control) => control switch
    {
        TabControl => "TabControl",
        NumericUpDown => "NumericUpDown",
        ComboBox => "ComboBox",
        RadioButton => "RadioButton",
        CheckBox => "CheckBox",
        // Button 的自绘子类在 ControlKinds 里由命名空间放行（ThemedButton/ThemedToggle）。
        Button => "Button",
        _ => string.Empty,
    };

    /// <summary>临时把整棵树置为可见（不改根窗体：Form.Visible=true 会真的 Show 出来）。</summary>
    private static Dictionary<Control, bool> EnsureAllVisible(Control root)
    {
        var map = new Dictionary<Control, bool>();
        foreach (Control child in Walk(root))
        {
            map[child] = child.Visible;
            if (!child.Visible)
            {
                child.Visible = true;
            }
        }

        return map;
    }

    private static IEnumerable<Control> Walk(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control grand in Walk(child))
            {
                yield return grand;
            }
        }
    }

    /// <summary>不 Show 也把 Dock/Flow 真的排一遍：CreateControl 之后逐层 PerformLayout。</summary>
    private static void ForceLayout(Control root)
    {
        root.CreateControl();
        Layout(root);

        static void Layout(Control parent)
        {
            parent.PerformLayout();
            foreach (Control child in parent.Controls)
            {
                Layout(child);
            }
        }
    }

    private static string Describe(Control control)
    {
        string name = string.IsNullOrEmpty(control.Name) ? "(unnamed)" : control.Name;
        var chain = new List<string>(3);
        for (Control? parent = control.Parent; parent is not null && chain.Count < 3; parent = parent.Parent)
        {
            chain.Add(string.IsNullOrEmpty(parent.Name) ? parent.GetType().Name : parent.Name);
        }

        string text = (control.Text ?? string.Empty).Replace('\n', ' ');
        if (text.Length > 14)
        {
            text = text.Substring(0, 14);
        }

        return $"{name}[{control.GetType().Name}]{control.Width}x{control.Height}"
            + $"@{string.Join("<", chain)}'{text}'";
    }

    private static bool WriteNothing(string? language, bool complete, out string error)
    {
        error = string.Empty;
        return true;
    }
}