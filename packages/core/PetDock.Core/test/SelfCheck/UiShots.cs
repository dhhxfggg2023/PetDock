// 人眼验收的截图工件（原规格 18 §7.1 最后一行：人眼验收需要有人先看图）。
//
// 与判据分开的原因：判据量的是「放不放得下」，截图回答的是「看起来是不是一套东西」。
// 后者机器判不了，但可以替人省掉「装一遍、点四页」的成本 —— 所有界面都用 DrawToBitmap 存成 PNG，
// 100% 与 150% 各一组，所有者直接看文件即可。
//
// 不 Show 窗口（无人值守）：CreateControl + 临时可见 + 逐层 PerformLayout，再画到 Bitmap 上。
// 托盘菜单是外壳绘制的弹出窗，DrawToBitmap 拿不到它，因此不在截图里（人眼验收时单看）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using PetDock.Core.Ui;
using PetDock.Core.Ui.Design;

namespace PetDock.Hosting;

/// <summary>截图工件的日志行回调（D-03b：自声明委托，不用系统泛型委托）。</summary>
internal delegate void ShotLog(string line);

/// <summary>人眼验收用的截图工件。</summary>
internal static class UiShots
{
    private static readonly int[] Scales = { 96, 144 };

    /// <summary>
    /// 材质的真机截图：磨砂/玻璃是系统合成在窗口背后的东西，PrintWindow/DrawToBitmap 都拿不到，
    /// 只能把窗口真的摆到桌面上、再从屏幕拷一块。三档各出一张，人眼看「有没有那层底」。
    /// </summary>
    public static int CaptureMaterials(string dir, ShotLog emit)
    {
        Directory.CreateDirectory(dir);
        MaterialKind saved = Theme.Material;
        int count = 0;
        try
        {
            using var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
            panel.ShowPage(PanelPages.Settings);
            panel.StartPosition = FormStartPosition.Manual;
            panel.Location = new Point(80, 80);
            panel.Show();
            Application.DoEvents();

            // 屏幕被锁（LockApp 在前台）时 CopyFromScreen 拍到的是锁屏界面，
            // 三档材质会拍成同一张图——那种「证据」比没有更坏，所以直接跳过。
            if (GetForegroundWindow() != panel.Handle)
            {
                emit("SHOT-SKIP material 前台不是面板（锁屏/别的应用在前台），屏幕截图会拍到别的东西");
                return 0;
            }

            foreach (MaterialKind kind in new[] { MaterialKind.Opaque, MaterialKind.Frosted, MaterialKind.Glass })
            {
                Theme.Material = kind;
                MaterialReport report = Theme.ApplyMaterial(panel);
                panel.Invalidate(true);
                panel.Update();

                // 让 DWM 有机会把合成做完，再拷屏幕（否则拍到的是上一档）。
                for (int i = 0; i < 20; i++)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(10);
                }

                string name = $"material-{Theme.MaterialToken(kind)}.png";
                using var bitmap = new Bitmap(panel.Width, panel.Height);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(panel.Location, Point.Empty, panel.Size);
                }

                bitmap.Save(Path.Combine(dir, name), ImageFormat.Png);
                emit($"SHOT {name} {bitmap.Width}x{bitmap.Height} material=[{report.Describe()}]");
                count++;
            }

            panel.Hide();
        }
        catch (Exception error)
        {
            emit($"SHOT-FAIL material {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Theme.Material = saved;
        }

        return count;
    }

    public static int Capture(string dir, ShotLog emit)
    {
        Directory.CreateDirectory(dir);
        int count = 0;
        foreach (int dpi in Scales)
        {
            Theme.SetSimulatedDpi(dpi);
            try
            {
                using (var panel = new ControlPanel(() => UiSnapshot.Empty, showWindow: false))
                {
                    foreach (string page in PanelPages.All)
                    {
                        panel.ShowPage(page);
                        emit($"SHOTINFO page={page} panel={panel.ClientSize.Width}x{panel.ClientSize.Height} " + Geometry(panel));
                        Panel settingsPage = panel.PageOf(PanelPages.Settings);
                        if (string.Equals(page, PanelPages.Settings, StringComparison.Ordinal))
                        {
                            var tree = new List<string>();
                            for (int i = 0; i < settingsPage.Controls.Count; i++)
                            {
                                Control child = settingsPage.Controls[i];
                                tree.Add($"{i}:{child.GetType().Name}({child.Name}) dock={child.Dock} bounds={child.Bounds} vis={child.Visible}");
                            }

                            emit("SHOTINFO settings-page= " + settingsPage.ClientSize + " children=[" + string.Join(" | ", tree) + "]");
                        }
                        count += Save(panel, Path.Combine(dir, $"panel-{page}@{dpi}.png"), emit, revealHidden: false);
                    }
                }

                // 两套色板各留证据（2026-10-28 第二版）：亮/暗都显式设定，不靠系统默认——
                // 否则「跟随系统」的机器上两张图会一模一样，等于没有证据。
                if (dpi == 96)
                {
                    ThemeMode previousMode = Theme.Mode;
                    foreach ((ThemeMode mode, string token) in new[] { (ThemeMode.Dark, "dark"), (ThemeMode.Light, "light") })
                    {
                        Theme.Mode = mode;
                        try
                        {
                            using var themed = new ControlPanel(() => UiSnapshot.Empty, showWindow: false);
                            foreach (string page in new[] { PanelPages.Settings, PanelPages.Pets })
                            {
                                themed.ShowPage(page);
                                count += Save(themed, Path.Combine(dir, $"panel-{page}-{token}@{dpi}.png"), emit, revealHidden: false);
                            }
                        }
                        finally
                        {
                            Theme.Mode = previousMode;
                        }
                    }
                }

                using (var consent = new AuthorizationDialog(
                           "示例宠物", "sample-pet", 1, new[] { "input.mousePosition", "network.fetch:api.example.com" }))
                {
                    count += Save(consent, Path.Combine(dir, $"consent@{dpi}.png"), emit);
                }

                using (var quit = new ConfirmDialog(I18n.Get("ui.tray.exitConfirm"), I18n.Get("ui.tray.exitConfirmTitle")))
                {
                    count += Save(quit, Path.Combine(dir, $"quit@{dpi}.png"), emit);
                }

                using (var onboarding = new OnboardingWindow(() => null, WriteNothing, hasPet: true))
                {
                    for (int step = 0; step < onboarding.StepCount; step++)
                    {
                        // 先确认「要拍的是第几步」，再拍：2026-10-28 首版只前进一次，
                        // 结果 onboarding-2 与 -3 是同一张图（哈希相同），截图给不出证据。
                        emit($"SHOTINFO onboarding step={onboarding.StepIndex + 1}/{onboarding.StepCount}");
                        count += Save(onboarding, Path.Combine(dir, $"onboarding-{step + 1}@{dpi}.png"), emit, revealHidden: false);
                        if (step < onboarding.StepCount - 1)
                        {
                            onboarding.GoNext();
                        }
                    }
                }
            }
            finally
            {
                Theme.SetSimulatedDpi(0);
            }
        }

        return count;
    }

    private static int Save(Control root, string path, ShotLog emit, bool revealHidden = true)
    {
        bool wasShown = root is Form { Visible: true };
        try
        {
            // DrawToBitmap 只画「真的存在」的子窗口：窗体没 Show 过时，子控件的 WS_VISIBLE
            // 还没落到外壳上，整张图只有底色。所以截图必须先 Show（会闪一下，这是工件不是判据）。
            if (root is Form form && !wasShown)
            {
                form.Show();
                Application.DoEvents();
            }

            Dictionary<Control, bool> hidden = revealHidden
                ? MakeVisible(root)
                : new Dictionary<Control, bool>();
            try
            {
                root.CreateControl();
                Application.DoEvents();
                Layout(root);
                Application.DoEvents();

                // 布局改过之后先把整棵树的失效区同步重画一遍：PrintWindow 拿的是窗口当前像素，
                // 控件被重新停靠后留下的残影会被当成「界面画错了」（2026-10-28 实测）。
                root.Invalidate(true);
                root.Update();
                Application.DoEvents();
                using var bitmap = new Bitmap(Math.Max(1, root.Width), Math.Max(1, root.Height));
                if (!Paint(root, bitmap))
                {
                    root.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                }

                bitmap.Save(path, ImageFormat.Png);
                emit($"SHOT {Path.GetFileName(path)} {bitmap.Width}x{bitmap.Height}");
                return 1;
            }
            finally
            {
                foreach (KeyValuePair<Control, bool> pair in hidden)
                {
                    pair.Key.Visible = pair.Value;
                }
            }
        }
        catch (Exception error)
        {
            emit($"SHOT-FAIL {Path.GetFileName(path)} {error.GetType().Name}: {error.Message}");
            return 0;
        }
        finally
        {
            if (root is Form shown && !wasShown)
            {
                shown.Hide();
            }
        }
    }

    /// <summary>几何读数（截图对不上时用它定位，而不是靠看像素猜）。</summary>
    private static string Geometry(Control root)
    {
        var parts = new List<string>();
        foreach (Control child in Walk(root))
        {
            if (child is ListView list)
            {
                var widths = new List<int>();
                foreach (ColumnHeader column in list.Columns)
                {
                    widths.Add(column.Width);
                }

                parts.Add(
                    $"{child.Name}[{child.GetType().Name}] bounds={child.Bounds} cols=[{string.Join(",", widths)}]");
            }
            else if (child is Label { Name: "page-title" or "panel-status" })
            {
                parts.Add($"{child.Name}[{child.GetType().Name}] bounds={child.Bounds}");
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>截图前把隐藏页/隐藏步骤也置为可见（Dock 布局跳过不可见控件）。</summary>
    private static Dictionary<Control, bool> MakeVisible(Control root)
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

    /// <summary>
    /// 画一整窗：优先 PrintWindow(PW_RENDERFULLCONTENT)，失败才退回 DrawToBitmap。
    /// 为什么：DrawToBitmap 对自带子窗口的控件（ListView 的表头就是一个 SysHeader32）会把子窗口
    /// 画到错的偏移上——2026-10-28 的截图里设置页表头无故右移 80px、首列标题消失，就是这个假象，
    /// 真窗上看并没有（所以人眼验收的工件不能靠 DrawToBitmap 出）。
    /// </summary>
    private static bool Paint(Control root, Bitmap bitmap)
    {
        if (root is not Form { IsHandleCreated: true } form)
        {
            return false;
        }

        using Graphics graphics = Graphics.FromImage(bitmap);
        IntPtr dc = graphics.GetHdc();
        try
        {
            return PrintWindow(form.Handle, dc, PwRenderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(dc);
        }
    }

    /// <summary>
    /// 采集标志。PW_RENDERFULLCONTENT(2) 能把自绘内容抓全，但对 ListView 的子窗口（表头）
    /// 会把它按列拉伸到列表空白区，截图里因此出现一整套竖线（2026-10-28 实测：pets/perf 空白表
    /// 出现等距竖线，而同一窗口用 0 号标志拍就没有）。选 0：宁可少一点层级效果，不要假缺陷。
    /// </summary>
    private const uint PwRenderFullContent = 0x00000002;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

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

    private static void Layout(Control parent)
    {
        parent.PerformLayout();
        foreach (Control child in parent.Controls)
        {
            Layout(child);
        }
    }

    private static bool WriteNothing(string? language, bool complete, out string error)
    {
        error = string.Empty;
        return true;
    }
}