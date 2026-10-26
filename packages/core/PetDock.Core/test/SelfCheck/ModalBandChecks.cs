// 「弹窗真的在最上面」这一族判据（2026-10-26 真机故障之后的反向门禁）。
// 故障的取证在 src/Ui/PetDockDialog.cs 的注释里：宠物窗置顶、本产品模态窗不置顶 → 用户前台有
// 最大化应用时弹窗被压在它后面，宠物窗又被 ShowDialog 置 WS_DISABLED ⇒「点不了，也关不掉」。
//
// 断两件事，缺一不可：
//   ① 每个模态窗的原生 exstyle 带 WS_EX_TOPMOST（ui-modal-topmost-*）。读位，不读 Visible ——
//      Visible=True 的窗照样可能被压在别人后面，故障当天所有窗口的 Visible 都是 True。
//   ② 真的「谁在最上面」：宠物窗先插到置顶档最前，再开模态窗，重叠处 WindowFromPoint 必须
//      解析到模态窗（ui-modal-above-topmost-pet）。①只证明样式位，②才是用户看到的那个结论。
// 再配一条负例对照（ui-modal-band-control）：同样位置放一个**不**继承基类的普通窗，WindowFromPoint
// 必须解析回宠物窗 —— 证明 ② 对这类故障真的会红，而不是一条永远绿的判据。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class ModalBandChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        TopmostBitOnEachModal(check);
        BandCompleteness(check);
        AboveTopmostPet(check);
        PlainFormControl(check);
    }

    /// <summary>①：三个模态窗显示一次，读它们的原生 exstyle。</summary>
    private static void TopmostBitOnEachModal(Report check)
    {
        using (var onboarding = new OnboardingWindow(() => null, WriteNothing, hasPet: true))
        {
            check("ui-modal-topmost-onboarding", BandOf(onboarding, out string detail), detail);
        }

        using (var consent = new AuthorizationDialog("示例宠物", "sample-pet", 1, new[] { "input.mousePosition" }))
        {
            check("ui-modal-topmost-consent", BandOf(consent, out string detail), detail);
        }

        using (var quit = new ConfirmDialog(I18n.Get("ui.tray.exitConfirm"), I18n.Get("ui.tray.exitConfirmTitle")))
        {
            check("ui-modal-topmost-quit", BandOf(quit, out string detail), detail);
        }
    }

    /// <summary>
    /// 这一类故障的扼杀点：本程序集里**每一个**给用户看的窗口都必须在置顶档。
    /// 用反射枚举而不是人肉清单 —— 新增的窗忘了继承基类时会在这里红，而不是等用户报「点不了」。
    /// </summary>
    private static void BandCompleteness(Report check)
    {
        // 两个有理由的例外：
        //  · 宠物的分层窗自己就是置顶的（docs/12 §2.1 的 WS_EX_TOPMOST）；
        //  · 控制面板由托盘点击打开，同一次点击给了本进程前台权（UiHost.Panel.cs:25 有 Activate），
        //    而且它是用户可能一直开着的常驻窗 —— 常驻窗跟着置顶会一直压住用户自己的工作。
        var allowed = new HashSet<string>(StringComparer.Ordinal) { nameof(LayeredFrameWindow), nameof(ControlPanel) };
        var forms = new List<string>();
        var offenders = new List<string>();
        foreach (Type type in typeof(ModalBandChecks).Assembly.GetTypes())
        {
            if (!typeof(Form).IsAssignableFrom(type) || type.IsAbstract) continue;
            forms.Add(type.Name);
            if (type == typeof(PetDockDialog) || typeof(PetDockDialog).IsAssignableFrom(type)) continue;
            if (!allowed.Contains(type.Name)) offenders.Add(type.Name);
        }

        check("ui-modal-band-completeness", offenders.Count == 0,
            $"forms={forms.Count} offenders={string.Join(",", offenders)} allowed={string.Join(",", allowed)}");
    }

    /// <summary>②：宠物在置顶档最前面时，模态窗必须压在它上面。</summary>
    private static void AboveTopmostPet(Report check)
    {
        using var pet = ShowPetAt(240, 240);
        using var dialog = new ConfirmDialog("probe", "probe");
        Overlap(pet, dialog, out IntPtr before, out IntPtr after, out string detail);
        check("ui-modal-above-topmost-pet",
            before == pet.WindowHandle && after == dialog.Handle,
            $"before=0x{before:X} expect=pet after=0x{after:X} expect=modal {detail}");
    }

    /// <summary>负例对照：不继承基类的普通窗必须被宠物盖住（否则 ② 红不了，等于没判）。</summary>
    private static void PlainFormControl(Report check)
    {
        using var pet = ShowPetAt(240, 240);
        using var plain = new Form
        {
            Text = "control",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        Overlap(pet, plain, out IntPtr before, out IntPtr after, out string detail);
        check("ui-modal-band-control",
            before == pet.WindowHandle && after == pet.WindowHandle,
            $"before=0x{before:X} after=0x{after:X} expect=pet,pet {detail}");
    }

    /// <summary>把宠物摆到 (x,y) 并插到置顶档最前，然后出一帧（没出帧的分层窗全透明，命中不了）。</summary>
    private static LayeredFrameWindow ShowPetAt(int x, int y)
    {
        var pet = new LayeredFrameWindow();
        pet.Show();
        pet.SetWindowPosition(x, y);
        pet.BringToFrontOfTopmost();
        pet.PresentFirstFrame();
        Application.DoEvents();
        return pet;
    }

    /// <summary>
    /// 同一块重叠区域上「放窗之前 / 之后」各读一次 WindowFromPoint。
    /// 探针点取宠物矩形内部、且落在被放窗的客户区里：±40 的偏移让 440x150 的确认框把那一点完整盖住。
    /// </summary>
    private static void Overlap(LayeredFrameWindow pet, Form window, out IntPtr before, out IntPtr after, out string detail)
    {
        Rectangle rect = Win32.ScreenRect(pet.WindowHandle);
        var probe = new POINT { X = rect.Left + 32, Y = rect.Top + 40 };
        before = Win32.TopLevelWindowFromPoint(probe);

        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(rect.Left - 40, rect.Top - 40);
        window.Show();
        Application.DoEvents();
        after = Win32.TopLevelWindowFromPoint(probe);
        window.Hide();

        detail = $"probe=({probe.X},{probe.Y}) pet=0x{pet.WindowHandle:X} window=0x{window.Handle:X}"
            + $" pet-rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}";
    }

    /// <summary>显示一次再读 exstyle：样式位在真窗上量，不在控件的属性里量（属性 True 而位不在，实测过）。</summary>
    private static bool BandOf(Form form, out string detail)
    {
        form.Show();
        Application.DoEvents();
        long exStyle = Win32.GetWindowLongPtr(form.Handle, Win32.GWL_EXSTYLE).ToInt64();
        form.Hide();
        bool topmost = (exStyle & Win32.WS_EX_TOPMOST) != 0;
        detail = $"{form.GetType().Name} exstyle=0x{exStyle:X} topmost={topmost} prop={form.TopMost}";
        return topmost;
    }

    private static bool WriteNothing(string? language, bool complete, out string error)
    {
        error = string.Empty;
        return true;
    }
}
