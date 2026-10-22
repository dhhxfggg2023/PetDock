// RFC-21 §4 的判据体（第三块：世界状态的构造）。
//
// 判据必须能有**两种可复现的世界状态**，否则它测的是运气不是行为（RFC-21 §4 与 t2 审计）：
//   · 遮挡：「真的被盖住」= 一个真的顶层窗压在宠物窗上；「没被盖住」= 什么都不压；
//     再加第三态「透明区被盖住、不透明取样点没被盖住」。
//   · 全屏：「前台窗确实盖满显示器」= 真的无边框窗铺满 Screen.Bounds 并**抢到前台**；
//     「没盖满」= 关掉它，前台回到一个明显不盖满的小窗。
//
// 抢前台必须「释放 ALT + SetForegroundWindow」（申报在 decisions.md）：实测裸
// SetForegroundWindow / AttachThreadInput / SwitchToThisWindow 全都拿不到前台，
// 而前台锁对「用户刚按过 ALT」有豁免。抢不到时调用方必须 FAIL 并打印读数 ——
// 绝不在「前台其实不是我的」状态下断言全屏成立。
//
// 夹具是**真对象**：真 LayeredFrameWindow（分层、topmost、真 DIB、真 UpdateLayeredWindow）、
// 真 PetFrameSource / PetInstance 链。这里没有替身实现被测行为，只摆世界。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    private static Form? _foreground;

    private static void CloseLeftoverWindows() => CloseForeground();

    /// <summary>把一个小窗（明显不盖满显示器）推成前台窗 —— 「前台不是全屏」这一态。</summary>
    private static void SmallForeground()
    {
        CloseForeground();
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.None,
            Bounds = new Rectangle(80, 80, 320, 240),
        };
        form.Show();
        Application.DoEvents();
        AcquireForeground(form.Handle);
        Application.DoEvents();
        _foreground = form;
    }

    private static void CloseForeground()
    {
        Form? form = _foreground;
        _foreground = null;
        if (form is null)
        {
            return;
        }

        try
        {
            form.Close();
        }
        catch (Exception)
        {
            // 关不掉也不影响判据：下一次 SmallForeground 会覆盖它。
        }

        form.Dispose();
        Application.DoEvents();
    }

    /// <summary>
    /// 抢前台：释放 ALT（前台锁对「用户刚按过 ALT」有豁免）后 SetForegroundWindow。
    /// 返回是否真的抢到（GetForegroundWindow() == hwnd）—— 抢不到时调用方必须 FAIL，不许假装。
    /// </summary>
    private static bool AcquireForeground(IntPtr hwnd)
    {
        Win32Extra.keybd_event(Win32Extra.VK_MENU, 0, Win32Extra.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Win32Extra.keybd_event(
            Win32Extra.VK_MENU, 0, Win32Extra.KEYEVENTF_EXTENDEDKEY | Win32Extra.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Win32Extra.SetForegroundWindow(hwnd);
        Application.DoEvents();
        return Win32Extra.GetForegroundWindow() == hwnd;
    }

    private static string ForegroundDescription() => Win32.WindowDescription(Win32Extra.GetForegroundWindow());

    /// <summary>真的无边框窗铺满主显示器，并抢到前台 —— 「前台窗确实盖满显示器」这一态。</summary>
    private sealed class FullscreenForeground : IDisposable
    {
        private Form? _form;

        private FullscreenForeground(Form form) => _form = form;

        /// <summary>是否真的抢到了前台（false ⇒ 调用方必须 FAIL，不许当作全屏成立）。</summary>
        public bool Acquired { get; private set; }

        /// <summary>前台窗的实测矩形是否**覆盖** rcMonitor（覆盖不是相等：不可见边框会让最大化窗溢出）。</summary>
        public bool CoversMonitor { get; private set; }

        public static FullscreenForeground Create(out string detail)
        {
            Rectangle bounds = Screen.PrimaryScreen!.Bounds;
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
                Bounds = bounds,
                TopMost = false,
            };
            form.Show();
            Application.DoEvents();

            var self = new FullscreenForeground(form) { Acquired = AcquireForeground(form.Handle) };
            Rectangle actual = Win32.ScreenRect(form.Handle);
            self.CoversMonitor = actual.Left <= bounds.Left && actual.Top <= bounds.Top
                && actual.Right >= bounds.Right && actual.Bottom >= bounds.Bottom;
            detail = "hwnd=0x" + form.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " bounds=" + Describe(actual)
                + " monitor=" + Describe(bounds)
                + " acquired=" + Flag(self.Acquired)
                + " coversMonitor=" + Flag(self.CoversMonitor)
                + " foreground=" + ForegroundDescription();
            return self;
        }

        private static string Describe(Rectangle r) =>
            "(" + r.Left.ToString(CultureInfo.InvariantCulture) + "," + r.Top.ToString(CultureInfo.InvariantCulture)
            + ")-(" + r.Right.ToString(CultureInfo.InvariantCulture) + "," + r.Bottom.ToString(CultureInfo.InvariantCulture) + ")";

        public void Dispose()
        {
            Form? form = _form;
            _form = null;
            if (form is null)
            {
                return;
            }

            try
            {
                form.Close();
            }
            catch (Exception)
            {
                // 同上：关不掉不影响结论。
            }

            form.Dispose();
            Application.DoEvents();
        }
    }

    /// <summary>判据用的真窗口夹具：真 LayeredFrameWindow + 真 PetFrameSource/PetInstance 链。</summary>
    private sealed class LayerFixture : IDisposable
    {
        private LayerFixture(LayeredFrameWindow window, PetInstance instance)
        {
            Window = window;
            Instance = instance;
        }

        public LayeredFrameWindow Window { get; }

        /// <summary>真实的宠物实例（LastFrameReused 的生产点就在它的 Swap 里）。</summary>
        public PetInstance Instance { get; }

        public IntPtr Handle => Window.WindowHandle;

        /// <summary>
        /// 造一台真窗口。<paramref name="transparentField"/> 决定帧里的不透明区在哪：
        /// false = 整帧不透明（默认）；true = **只有右下角那块不透明，其余全透明** ——
        /// 这正是 docs/12 的「小帧按左上角粘贴、余处保持透明」（t2 审计的实测条件），
        /// 它让「探针取样点」与「被盖住的点」可以分开摆，是「透明点不算遮挡」那条唯一有效的构造。
        /// <para>
        /// <paramref name="clipFrames"/> = 1 时交付**永远同一个位图对象**（帧号恒为 0）——
        /// 静止档判据必须在这种世界里跑：3 帧 × 1fps 的夹具每秒换一次帧，`LastFrameReused`
        /// 因此每秒闪一次 false、档位在 Idle/Active 之间跳，判据就成了「跑两次对一次」的运气题
        /// （2026-10-22 实测：同一份二进制第一次 FAIL、第二次 PASS）。
        /// </para>
        /// </summary>
        public static LayerFixture Create(bool transparentField = false, int clipFrames = 3)
        {
            PetInstance instance = PetDriveTest.MakeInstance(out StubView view, out StubBrain brain);
            view.DeclareClip("idle", clipFrames, 1, ClipLoop.Loop);
            var fixture = transparentField
                ? Build(new CornerBlobContent(), instance)
                : Build(new PetFrameSource(instance) { WindowHandle = IntPtr.Zero, Probe = new WorldProbe(() => 480) }, instance);
            return fixture;
        }

        /// <summary>
        /// 造一台**内容源可抛异常**的真窗口（RFC-21 t14 用）。
        /// 为什么需要：判据要造的「泵意外退出」必须是**真的**退出，
        /// 而不是往泵里塞一个假标志位。让内容源在下一 tick 抛异常，就走
        /// `FramePump.Loop()` 的 `catch → SetExitReason("tick-failed") → break` 那条真路。
        /// 窗口本身仍是真 LayeredFrameWindow，内容源走真的 IFrameContentSource 注入路径。
        /// </summary>
        public static LayerFixture CreateFailable()
        {
            PetInstance instance = PetDriveTest.MakeInstance(out StubView view, out StubBrain brain);
            view.DeclareClip("idle", 3, 1, ClipLoop.Loop);
            var content = new CornerBlobContent();
            LayerFixture fixture = Build(content, instance);
            fixture.FailableContent = content;
            return fixture;
        }

        /// <summary>
        /// 「下一次合成就抛异常」的内容源句柄（仅 <see cref="CreateFailable"/> 造出的夹具非 null）。
        /// 判据用它真的把泵打退，而不是只改一个布尔读数。
        /// </summary>
        public CornerBlobContent? FailableContent { get; private set; }

        private static LayerFixture Build(IFrameContentSource content, PetInstance instance)
        {
            var window = new LayeredFrameWindow(content);
            window.Show();
            window.MoveToCenter();
            Application.DoEvents();
            // 先把一帧合成进 DIB：遮挡采样读的是当前帧的 alpha。
            window.PresentFirstFrame();
            Application.DoEvents();
            return new LayerFixture(window, instance);
        }

        /// <summary>按窗口局部坐标问当前帧的 alpha（与生产路径同一个读数面）。</summary>
        public byte? AlphaAt(int x, int y) => Window.SampleFramePixel(x, y)?.A;

        /// <summary>一次判定的输入：全是真读数（真句柄、真实测矩形、真 DPI）。</summary>
        public ThrottleInputs Inputs(bool reused, double userFrameMs = 1000.0 / 30.0, Func<int, int, byte?>? alpha = null)
        {
            (int X, int Y) pos = Window.CurrentPosition;
            Size size = Window.WindowSize;
            return new ThrottleInputs(
                Window.WindowHandle,
                alpha ?? AlphaAt,
                reused,
                (pos.X, pos.Y, size.Width, size.Height),
                Window.CurrentDpi,
                userFrameMs);
        }

        /// <summary>五个分数取样点里有几个是**不透明**的（判据读数：透明点不算遮挡那条的自证材料）。</summary>
        public (int Opaque, int Total) OpaqueRatios()
        {
            Size size = Window.WindowSize;
            (double X, double Y)[] ratios = { (0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) };
            int opaque = 0;
            foreach ((double fx, double fy) in ratios)
            {
                int x = Math.Clamp((int)Math.Round(size.Width * fx), 0, size.Width - 1);
                int y = Math.Clamp((int)Math.Round(size.Height * fy), 0, size.Height - 1);
                if (AlphaAt(x, y) is byte a && a != 0)
                {
                    opaque++;
                }
            }

            return (opaque, ratios.Length);
        }

        /// <summary>「真的被盖住」：一个真的顶层窗整窗压在宠物窗上（宠物是 topmost ⇒ 盖住者必须也是 topmost）。</summary>
        public Form CoverWhole() => Cover(Win32.ScreenRect(Handle));

        /// <summary>「透明区被盖住」：只盖左上角一小块（默认夹具下那块不透明；透明夹具下那块透明）。</summary>
        public Form CoverCorner()
        {
            Rectangle rect = Win32.ScreenRect(Handle);
            return Cover(new Rectangle(rect.Left, rect.Top, 24, 24));
        }

        private static Form Cover(Rectangle bounds)
        {
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
                Bounds = bounds,
                TopMost = true,
                BackColor = Color.FromArgb(0x20, 0x80, 0x40),
            };
            form.Show();
            // 宠物窗自己也是 topmost：同一层里必须把它再提到本层最前，否则盖不住。
            Win32.SetWindowPos(form.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            Application.DoEvents();
            Spin(120);
            return form;
        }

        public void Dispose()
        {
            try
            {
                Window.Hide();
            }
            catch (Exception)
            {
                // 窗口已经没了：没什么可藏。
            }

            Window.Dispose();
        }
    }

    /// <summary>
    /// 判据用的内容源：整帧透明，**只有右下角一块不透明**。
    /// 为什么需要它：探针的五个分数取样点里，(0.5,0.5)/(0.25,0.25)/(0.75,0.25)/(0.25,0.75) 全透明，
    /// 第一个不透明点是 (0.75,0.75)。于是「盖住左上角透明区」与「盖住取样点」是两件可分开摆的事 ——
    /// 默认夹具整帧不透明（StubView 用 FillEllipse 铺满），测不出「透明点不算遮挡」这个行为。
    /// 走真的 IFrameContentSource 注入路径，不是替身：LayeredFrameWindow 照常 DrawBitmap / Present。
    /// </summary>
    private sealed class CornerBlobContent : IFrameContentSource
    {
        private Bitmap? _frame;
        private Size _size;

        /// <summary>
        /// 置 true 后**下一次** GetFrameContent 直接抛异常，之后自动复位。
        /// 这是 RFC-21 t14 那张「真的让泵自己退出来」的牌：异常会穿过
        /// `LayeredFrameWindow.ComposeFrame` 冒到 `FramePump.Loop()` 的 try/catch，
        /// 泵据此记 `tick-failed` 并 break —— 走的是生产代码里那条真路径，不是假标志位。
        /// </summary>
        public bool FailNextFrame { get; set; }

        /// <summary>本内容源被要过几帧（判据读数：能证明异常真的发生在合成链上）。</summary>
        public long ServeCount { get; private set; }

        public Bitmap? GetFrameContent(Size frameSize)
        {
            if (FailNextFrame)
            {
                FailNextFrame = false;
                throw new InvalidOperationException("throttle-check: 内容源故障（造泵意外退出）");
            }

            ServeCount++;
            if (frameSize.Width <= 0 || frameSize.Height <= 0)
            {
                return null;
            }

            if (_frame is null || _size != frameSize)
            {
                _frame?.Dispose();
                _frame = new Bitmap(frameSize.Width, frameSize.Height, PixelFormat.Format32bppArgb);
                using Graphics graphics = Graphics.FromImage(_frame);
                graphics.Clear(Color.FromArgb(0, 0, 0, 0));
                using var brush = new SolidBrush(Color.FromArgb(255, 0xC0, 0x6E, 0x3A));
                int w = frameSize.Width;
                int h = frameSize.Height;
                // 右下角四分之一块：覆盖 (0.75,0.75) 那个分数点，离 (0.5,0.5) 足够远。
                graphics.FillRectangle(brush, w / 2, h / 2, w - (w / 2), h - (h / 2));
                _size = frameSize;
            }

            return _frame;
        }

        public void Dispose()
        {
            _frame?.Dispose();
            _frame = null;
        }
    }

    /// <summary>
    /// 前台抢占用的 Win32 声明（只在自检里用：生产路径不需要抢前台）。
    /// 放在 test/ 里是有意的 —— 它是判据的构造手段，不是内核能力。
    /// </summary>
    private static class Win32Extra
    {
        internal const byte VK_MENU = 0x12;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        internal const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }
}
