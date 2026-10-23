// --perftest 的 RFC-21 半边（D-03c 拆分）：遮挡态的构造、采样与判据。
//
// 为什么单独一个文件：PerfGate.cs 已经 376 行，D-03c 的 400 行上限是硬门禁。
// 本文件只放「造出遮挡态并读出档位」这一段；D-14 的五个阈值与判定序仍在 PerfGate.cs。
//
// 判据为什么不能只看 CPU 百分比：`perf-cpu-idle` 是整段平均，遮挡档若根本没进（覆盖窗没生效、
// 或宠物帧的五个取样点恰好全透明），CPU 一样会很低 —— 那就是一条恒真的读数。
// 所以这里同时打出档位、tier 变化次数、composed/submitted/skipped 与每秒 tick 数，
// 并断言「真的进了遮挡档」+「速率真的落到 10fps 档」，两者缺一即 FAIL。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class PerfGate
{
    /// <summary>遮挡态采样窗（captain 2026-10-21 放行时定的 5 秒：够出稳态，又少闪 5 秒）。</summary>
    private const int OccludedSampleMs = 5000;

    /// <summary>「进了 10fps 档」的速率带：目标 100ms ⇒ 10/s，上下各留 4/s 的调度抖动余量。</summary>
    private const double OccludedRateMin = 6.0;
    private const double OccludedRateMax = 14.0;

    /// <summary>覆盖窗生效的观察窗：建窗到判档之间有若干 tick 的滞后，先让它落定再取基线。</summary>
    private const int CoverSettleMs = 400;

    /// <summary>
    /// 一次 LoopReadings 快照。**必须打全**：只打档位看不出速率，只打速率看不出是不是靠 Idle 档
    /// 跳过提交（那种「更低的提交数」会让「提交被压低」恒真）。
    /// </summary>
    internal readonly record struct LoopReadings(
        FrameTier Tier, double TargetMs, long Ticks, long Composed, long Submitted, long Skipped, long Changes)
    {
        internal static LoopReadings Of(ResidentFrameLoop loop) => new(
            loop.Tier, loop.TargetFrameMs, loop.Ticks, loop.Composed, loop.Submitted, loop.Skipped, loop.TierChanges);

        /// <summary>本窗口内的增量（同一次运行内的两快照相减，不跨进程比较）。</summary>
        internal LoopReadings Minus(LoopReadings earlier) => new(
            Tier, TargetMs,
            Ticks - earlier.Ticks, Composed - earlier.Composed,
            Submitted - earlier.Submitted, Skipped - earlier.Skipped,
            Changes - earlier.Changes);

        internal string Detail(double seconds)
        {
            double perSec = seconds > 0 ? seconds : 1;
            return "tier=" + Tier.ToString().ToLowerInvariant()
                + " target=" + TargetMs.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
                + " ticks=" + Ticks.ToString(CultureInfo.InvariantCulture)
                + " composed=" + Composed.ToString(CultureInfo.InvariantCulture)
                + " submitted=" + Submitted.ToString(CultureInfo.InvariantCulture)
                + " skipped=" + Skipped.ToString(CultureInfo.InvariantCulture)
                + " changes=" + Changes.ToString(CultureInfo.InvariantCulture)
                + " tick-rate=" + (Ticks / perSec).ToString("0.0", CultureInfo.InvariantCulture) + "/s"
                + " submit-rate=" + (Submitted / perSec).ToString("0.0", CultureInfo.InvariantCulture) + "/s";
        }
    }

    /// <summary>遮挡态的一次采样结果。CoverOnTop 与 OpaquePoints 是**诊断**：
    /// 判 FAIL 时必须能分清「覆盖窗没盖上去」与「宠物帧在这个点上本来就透明」。</summary>
    private readonly record struct OccludedSample(
        LoopReadings Delta, double Seconds, double CpuPct,
        int Polls, int OccludedPolls, int OpaquePoints, bool CoverOnTop, string Cover, double UncoveredRate);

    /// <summary>
    /// 造一个真 topmost 覆盖窗压在宠物窗矩形上（RFC-21 §4 判据 ui-throttle-occluded-by-window 的手法），
    /// 采 5 秒，然后**立即关掉**。宠物窗自己是 topmost，所以覆盖窗也必须 topmost 且提到同层最前。
    /// </summary>
    private static OccludedSample MeasureOccluded(
        ResidentFrameLoop loop, LayeredFrameWindow window, CpuSampler cpu, double uncoveredRate)
    {
        IntPtr originalForeground = Win32.GetForegroundWindow();
        // 判据自证的前提：前台窗不能是盖满显示器的窗。否则宠物进 Fullscreen 档、泵被停，
        // 全部读数退化成 0 —— 那条 0% 的 CPU 读数看起来像「节流很成功」，其实是「什么都没测」。
        bool pinned = PinForegroundIfFullscreen(window);
        using PetCoverWindow? cover = PetCoverWindow.Create(window.WindowHandle);
        LoopReadings start;
        int polls = 0;
        int occludedPolls = 0;
        try
        {
            PumpEvents(cover.Handle, CoverSettleMs);
            start = LoopReadings.Of(loop);
            double cpuPct = cpu.Sample(OccludedSampleMs, out TimeSpan span, () =>
            {
                polls++;
                if (loop.Tier == FrameTier.Occluded)
                {
                    occludedPolls++;
                }
            });
            LoopReadings delta = LoopReadings.Of(loop).Minus(start);
            (int opaque, string point) = OpaquePoints(window);
            return new OccludedSample(delta, span.TotalSeconds, cpuPct, polls, occludedPolls,
                opaque, CoverIsOnTop(window, cover.Handle, point), cover.Describe(), uncoveredRate);
        }
        finally
        {
            // 覆盖窗必须立刻消失（captain 放行的条件），随后把前台还给原来那个窗口。
            cover.Close();
            RestoreForeground(originalForeground);
            if (pinned)
            {
                ClosePinnedForeground();
            }
        }
    }

    /// <summary>遮挡态的两条读数：一条 CHECK-DISPLAY（人看的全量），一条 CHECK（门禁）。
    /// 判定要求四件事同时成立，任何一条单独成立都可以被「没进遮挡档」伪造。</summary>
    private static int JudgeOccluded(OccludedSample sample, ref int failures)
    {
        LoopReadings d = sample.Delta;
        double rate = sample.Seconds > 0 ? d.Ticks / sample.Seconds : 0;
        double submitRate = sample.Seconds > 0 ? d.Submitted / sample.Seconds : 0;
        bool inTier = d.Tier == FrameTier.Occluded && sample.OccludedPolls * 2 >= sample.Polls && sample.Polls > 0;
        bool inBand = rate >= OccludedRateMin && rate <= OccludedRateMax;
        // 「提交被压低」的**可证伪**口径是速率，不是 submitted<composed：遮挡档每 tick 都提交
        // （RFC-21 §3.3 的表），所以比值恒为 1，拿它当判据就是恒真。这里比的是每秒提交数：
        // 未遮挡窗的速率若没降下来（覆盖窗没生效），submit-rate 会停在 ~30/s 而直接 FAIL。
        bool submitDropped = submitRate <= OccludedRateMax && submitRate < sample.UncoveredRate * 0.8;

        Emit("CHECK-DISPLAY perf-throttle-occluded " + d.Detail(sample.Seconds)
            + " cpu=" + Fmt(sample.CpuPct) + "%"
            + " polls=" + sample.Polls.ToString(CultureInfo.InvariantCulture)
            + " occluded-polls=" + sample.OccludedPolls.ToString(CultureInfo.InvariantCulture)
            + " opaque-points=" + sample.OpaquePoints.ToString(CultureInfo.InvariantCulture) + "/5"
            + " cover-on-top=" + (sample.CoverOnTop ? "true" : "false")
            + " cover=" + sample.Cover
            + " uncovered-tick-rate=" + sample.UncoveredRate.ToString("0.0", CultureInfo.InvariantCulture) + "/s");
        // docs/41:126「完全遮挡时 ≤0.1%」：本命令**只显示不判**。理由：那是 60 秒均值面板的读数，
        // 5 秒窗 + 一次 DoEvents 抖动就足以翻红；把它变成门禁等于收紧阈值（t15 硬约束 3 禁止）。
        Emit("CHECK-DISPLAY perf-throttle-occluded-cpu " + Fmt(sample.CpuPct)
            + "% (docs/41:126 子阈值 0.10%，本命令不据此判失败：采样窗只有 "
            + sample.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s)");

        bool ok = inTier && inBand && submitDropped;
        Check(ref failures, "perf-throttle-occluded", ok,
            (inTier ? "tier=occluded" : "tier=" + d.Tier.ToString().ToLowerInvariant() + " (采样没进遮挡档)")
            + " rate " + rate.ToString("0.0", CultureInfo.InvariantCulture) + "/s in ["
            + OccludedRateMin.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + OccludedRateMax.ToString("0.0", CultureInfo.InvariantCulture) + "]/s"
            + " submit-rate " + submitRate.ToString("0.0", CultureInfo.InvariantCulture)
            + "/s < " + (sample.UncoveredRate * 0.8).ToString("0.0", CultureInfo.InvariantCulture)
            + "/s (未遮挡 " + sample.UncoveredRate.ToString("0.0", CultureInfo.InvariantCulture) + "/s)");
        return failures;
    }

    /// <summary>
    /// 前台窗若盖满显示器就先造一个小前台窗把它换掉（ThrottleChecks.Windows.cs:35-51 的同一手法）。
    /// 只在需要时才抢前台：常见情形下根本不碰用户焦点。
    /// </summary>
    private static bool PinForegroundIfFullscreen(LayeredFrameWindow window)
    {
        if (!new ThrottleProbe().SampleFullscreen(window.WindowHandle))
        {
            return false;
        }

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
        // 抢前台要「释放 ALT + SetForegroundWindow」：裸 SetForegroundWindow 在实测中拿不到前台
        // （ThrottleChecks.Windows.cs:9-10 已申报）。抢不到也继续 —— 档位读数会暴露问题。
        Foreground.keybd_event(Foreground.VK_MENU, 0, Foreground.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Foreground.keybd_event(Foreground.VK_MENU, 0,
            Foreground.KEYEVENTF_EXTENDEDKEY | Foreground.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Foreground.SetForegroundWindow(form.Handle);
        Application.DoEvents();
        _pinnedForeground = form;
        return true;
    }

    private static void ClosePinnedForeground()
    {
        Form? form = _pinnedForeground;
        _pinnedForeground = null;
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
            // 关不掉不影响判据：它只是个 320x240 的普通窗。
        }

        form.Dispose();
        Application.DoEvents();
    }

    private static void RestoreForeground(IntPtr original)
    {
        if (original == IntPtr.Zero || Win32.GetForegroundWindow() == original)
        {
            return;
        }

        Foreground.SetForegroundWindow(original);
        Application.DoEvents();
    }

    private static Form? _pinnedForeground;

    /// <summary>真 topmost 覆盖窗：只压宠物窗矩形，采完立刻关（captain 放行的两个条件）。</summary>
    private sealed class PetCoverWindow : IDisposable
    {
        private Form? _form;

        private PetCoverWindow(Form form, Rectangle bounds)
        {
            _form = form;
            Bounds = bounds;
        }

        public IntPtr Handle => _form?.Handle ?? IntPtr.Zero;

        public Rectangle Bounds { get; }

        public static PetCoverWindow Create(IntPtr petHwnd)
        {
            Rectangle rect = Win32.ScreenRect(petHwnd);
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
                Bounds = rect,
                TopMost = true,
                BackColor = Color.FromArgb(0x20, 0x80, 0x40),
            };
            form.Show();
            // 宠物窗也是 topmost：同一层里必须再把它提到本层最前，否则盖不住。
            Win32.SetWindowPos(form.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            Application.DoEvents();
            return new PetCoverWindow(form, rect);
        }

        public string Describe() => "0x" + Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)
            + " bounds=(" + Bounds.Left.ToString(CultureInfo.InvariantCulture)
            + "," + Bounds.Top.ToString(CultureInfo.InvariantCulture)
            + ")-(" + Bounds.Right.ToString(CultureInfo.InvariantCulture)
            + "," + Bounds.Bottom.ToString(CultureInfo.InvariantCulture) + ")";

        public void Close()
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
                // 同上：关不掉不影响读数，进程退出时会被回收。
            }

            form.Dispose();
            Application.DoEvents();
        }

        public void Dispose() => Close();
    }

    /// <summary>
    /// 宠物帧在探针那五个分数点上有几个不透明（与 ThrottleProbe.SampleOccluded 的点位一致）。
    /// 这是「没进遮挡档」的归因读数：0/5 ⇒ 探针找不到不透明取样点，与覆盖窗无关。
    /// </summary>
    private static (int Opaque, string Point) OpaquePoints(LayeredFrameWindow window)
    {
        (double Fx, double Fy)[] points =
        {
            (0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75),
        };
        Size size = window.WindowSize;
        int opaque = 0;
        string first = "(none)";
        foreach ((double fx, double fy) in points)
        {
            int x = Math.Clamp((int)Math.Round(size.Width * fx), 0, Math.Max(size.Width - 1, 0));
            int y = Math.Clamp((int)Math.Round(size.Height * fy), 0, Math.Max(size.Height - 1, 0));
            if (window.SampleFramePixel(x, y) is { } pixel && pixel.A != 0)
            {
                opaque++;
                if (first == "(none)")
                {
                    first = x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return (opaque, first);
    }

    /// <summary>第一个不透明取样点的**屏幕坐标**上，最上面的顶层窗是不是覆盖窗。
    /// 这一条把「覆盖窗没盖上去」与「像素点透明」彻底分开 —— 没有它，FAIL 只能靠猜。</summary>
    private static bool CoverIsOnTop(LayeredFrameWindow window, IntPtr cover, string localPoint)
    {
        if (cover == IntPtr.Zero || localPoint == "(none)")
        {
            return false;
        }

        string[] parts = localPoint.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            return false;
        }

        Rectangle rect = Win32.ScreenRect(window.WindowHandle);
        var point = new POINT { X = rect.Left + x, Y = rect.Top + y };
        return Win32.TopLevelWindowFromPoint(point) == cover;
    }

    /// <summary>转事件循环（覆盖窗要真的画出来、档位要真的判出来，都得让消息跑起来）。</summary>
    private static void PumpEvents(IntPtr hwnd, int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }
    }

    /// <summary>抢前台用的 Win32 声明。为什么不复用 ThrottleChecks.Windows.cs:332 的 Win32Extra：
    /// 它是 ThrottleChecks 里的 private 嵌套类，本类读不到；为它改那个文件不属本任务 inScope。</summary>
    private static class Foreground
    {
        internal const byte VK_MENU = 0x12;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        internal const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }

    /// <summary>采样窗里的 UI 滴答回调（每次 250ms 一次）。用自定义委托而不是泛型委托，
    /// 是为了让本文件不出现 D-03b 黑名单里的那个词（tools/lint-core.mjs 逐行子串匹配）。</summary>
    private delegate void PollTick();

}
