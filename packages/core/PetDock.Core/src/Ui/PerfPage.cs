// 性能页（docs/18 §2.4，RFC-12 §2.1 建页 / RFC-30 §3 补逐宠物形态）：逐宠物聚合视图 + 60s 采样。
// 三条硬约束，都是为了「面板上的每个数字都能被追溯」：
//   1. **没有宠物时显示「尚无宠物运行」，不得编造数字**——一条会自己造数的面板比没有面板更坏
//      （RFC-04 §3 付过这笔账）。
//   2. **CPU / 私有工作集是进程级读数**（docs/10 §3：宠物与内核同进程），所以逐宠物行的这两列
//      写「(进程共享)」，只有进程合计行给出数字——把进程读数摊给某一只宠物就是编造。
//   3. **帧耗时 / 超限计数只在「帧泵只服务一只宠物」时归属到那只宠物**（RFC-30 §3.3）：常驻形态
//      一个泵一只宠物，多只时泵读数无法归因，逐宠物行写「—」并在汇总里说明原因。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Core.Ui;

internal sealed class PerfPage : UserControl
{
    /// <summary>「没有可用归属」时单元格里的占位符（不是 0：0 会被读成「测过是 0」）。</summary>
    public const string NotAttributed = "—";

    /// <summary>进程级读数在逐宠物行里的标注（docs/10 §3）。值是键名，文案在 assets/i18n（RFC-42）。</summary>
    public const string ProcessShared = "ui.perf.processShared";

    /// <summary>计数器没有接收口时两格都写它（不是 0：0 会被读成「测过是 0」，RFC-30 §3.4）。</summary>
    public const string NotWired = "ui.perf.notWired";

    /// <summary>没有宠物时的摘要（不得编造数字，§2.4）。判据断言这个键，不写中文原文。</summary>
    public const string NoPetRunning = "ui.perf.noPetRunning";

    private readonly Func<UiSnapshot> _snapshot;
    private readonly Func<string, PetFrameReadings?>? _framesFor;
    private readonly Label _summary;
    private readonly ThemedList _rows;
    private readonly ThemedButton _sample;

    public PerfPage(Func<UiSnapshot> snapshot, Func<string, PetFrameReadings?>? frames = null)
    {
        _snapshot = snapshot ?? (() => UiSnapshot.Empty);
        _framesFor = frames;
        Name = "perf-page";

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 56),
            AutoSize = false,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceSm), Theme.Dip(this, Theme.SpaceSm), Theme.Dip(this, Theme.SpaceSm), 0),
            Text = I18n.Get("ui.perf.idle"),
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
        };

        _sample = new ThemedButton
        {
            Text = I18n.Get("ui.perf.sample"),
            Name = "sample",
            Primary = true,
        };
        _sample.FitToText();
        _sample.Width = Math.Max(_sample.Width, Theme.Dip(this, 160));
        _sample.Click += (_, _) => Sample();

        // 宿主条：Dock=Top 会把按钮拉成整行宽的色块（2026-10-28 截图实测），
        // 所以按钮不定 Dock，由宿主条在尺寸变化时把它摆在左上。
        var sampleHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Dip(this, 52),
            BackColor = Theme.Bg,
            Name = "perf-sample-host",
        };
        sampleHost.Controls.Add(_sample);
        sampleHost.Resize += (_, _) =>
            _sample.Location = new Point(
                Theme.Dip(sampleHost, Theme.SpaceSm),
                Math.Max(0, (sampleHost.Height - _sample.Height) / 2));

        _rows = new ThemedList
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            Name = "perf-rows",
        };
        _rows.SetColumns(
            (I18n.Get("ui.perf.col.pet"), 150),
            (I18n.Get("ui.perf.col.cpu"), 110),
            (I18n.Get("ui.perf.col.workingSet"), 130),
            (I18n.Get("ui.perf.col.frameMs"), 110),
            (I18n.Get("ui.perf.col.overruns"), 100));

        Controls.Add(_rows);
        Controls.Add(sampleHost);
        Controls.Add(_summary);
    }

    /// <summary>把一次快照摆到页面上。空快照不造数（§2.4）。</summary>
    public void Bind(PerfSummary perf)
    {
        _rows.Items.Clear();
        if (perf.IsEmpty)
        {
            // 空态不摆一张空表：五列全空看起来像「界面没画完」。说清为什么没有数字就够了。
            _rows.Visible = false;
            _summary.Text = I18n.Get(NoPetRunning);
            return;
        }

        _rows.Visible = true;

        foreach (PerfLine line in perf.Rows)
        {
            string overruns = line.Overruns?.ToString(CultureInfo.InvariantCulture) ?? NotAttributed;
            _rows.Items.Add(new ListViewItem(new[] { line.Name, line.Cpu, line.Memory, line.FrameMs, overruns }));
        }

        _rows.FitColumns();
        _summary.Text = Summary(perf);
    }

    /// <summary>汇总行的文案。降级/隔离区计数没有接收口时写「未接」（RFC-30 §3.4）。</summary>
    public static string Summary(PerfSummary perf)
    {
        const string CountersKey = "ui.perf.summary.counters";
        string counters = perf.CountersWired
            ? string.Format(CultureInfo.InvariantCulture, I18n.Get(CountersKey), perf.Degraded, perf.Isolated)
            : string.Format(CultureInfo.InvariantCulture, I18n.Get(CountersKey), I18n.Get(NotWired), I18n.Get(NotWired));
        return string.Format(CultureInfo.InvariantCulture, I18n.Get("ui.perf.summary.window"), perf.Window, counters);
    }

    /// <summary>
    /// 采样。这里**不**自己发明测量法：采样器与 D-14 门禁（--perftest）同源，
    /// 面板读数与门禁命令必须一致（docs/50 §M8）。采样过程中保持 UI 可响应。
    /// </summary>
    private void Sample()
    {
        UiSnapshot snapshot = _snapshot();
        if (snapshot.Pets.Count == 0)
        {
            // 没有宠物就没有可测对象：如实说明，不拿进程自身读数冒充「宠物功耗」。
            Bind(PerfSummary.Empty);
            return;
        }

        _sample.Enabled = false;
        _summary.Text = I18n.Get("ui.perf.sampling");
        Application.DoEvents();
        try
        {
            PerfSummary measured = PerfSampler.Measure(60_000, snapshot, _framesFor);
            Bind(measured);
        }
        catch (Exception ex)
        {
            _summary.Text = I18n.Get("ui.perf.sampleFailed", ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _sample.Enabled = true;
        }
    }

    /// <summary>采样按钮是否可用（自检用）。</summary>
    public bool SampleEnabled => _sample.Enabled;

    /// <summary>当前展示的行数（自检用：0 行 = 没有可测量的对象）。</summary>
    public int RowCount => _rows.Items.Count;

    /// <summary>摘要文字（自检用：空快照时必须包含 <see cref="NoPetRunning"/>）。</summary>
    public string SummaryText => _summary.Text;

    /// <summary>第 index 行的五列文字（自检用）。</summary>
    public string[] RowText(int index)
    {
        if (index < 0 || index >= _rows.Items.Count)
        {
            return Array.Empty<string>();
        }

        ListViewItem item = _rows.Items[index];
        var cells = new string[item.SubItems.Count];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = item.SubItems[i].Text;
        }

        return cells;
    }
}

/// <summary>
/// 面板侧采样器：只在**页面真实打开**时才跑，避免常驻路径平白多出一个 60s 循环。
/// 读数口径与 PerfGate（--perftest）一致：TotalProcessorTime 增量 / 墙钟 / 核数，私有工作集。
/// 帧耗时按**宠物 id** 逐只取（RFC-37 片 4：每只一个泵）；取不到的那只写「—」，不摊给别的宠物。
/// CPU / 私有工作集在 in-process 里没有逐宠物读数，仍写「(进程共享)」——分摊不是测量。
/// </summary>
internal static class PerfSampler
{
    public static PerfSummary Measure(
        int windowMs,
        UiSnapshot snapshot,
        Func<string, PetFrameReadings?>? framesFor = null)
    {
        IReadOnlyList<PetLine> pets = snapshot.Pets;
        if (pets.Count == 0)
        {
            return PerfSummary.Empty;
        }

        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        self.Refresh();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (wall.ElapsedMilliseconds < windowMs)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
        }

        wall.Stop();
        self.Refresh();
        double cpuPct = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds
            / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100.0;
        double wsMb = self.WorkingSet64 / (1024.0 * 1024.0);

        // 进程合计行：只有一个泵时可归属给「这一只」，多个泵时进程级帧耗时没有定义 → 「—」。
        PetFrameReadings? sole = null;
        int attributed = 0;
        foreach (PetLine pet in pets)
        {
            if (framesFor?.Invoke(pet.Id) is PetFrameReadings reading)
            {
                sole = attributed == 0 ? reading : null;
                attributed++;
            }
        }

        bool processFrameAttributed = attributed == 1 && sole is not null;
        var rows = new List<PerfLine>(pets.Count + 1)
        {
            new(
                Name: I18n.Get("ui.perf.row.processTotal"),
                Cpu: Fmt(cpuPct) + " %",
                Memory: Fmt(wsMb) + " MB",
                FrameMs: processFrameAttributed ? FrameText(sole!) : PerfPage.NotAttributed,
                Overruns: processFrameAttributed ? sole!.OverBudget : null),
        };

        foreach (PetLine pet in pets)
        {
            PetFrameReadings? own = framesFor?.Invoke(pet.Id);
            rows.Add(new PerfLine(
                Name: pet.Name,
                Cpu: I18n.Get(PerfPage.ProcessShared),
                Memory: I18n.Get(PerfPage.ProcessShared),
                FrameMs: own is null ? PerfPage.NotAttributed : FrameText(own),
                Overruns: own?.OverBudget));
        }

        return new PerfSummary(
            Window: Fmt(wall.Elapsed.TotalSeconds) + "s",
            Degraded: 0,
            Isolated: 0,
            Rows: rows,
            CountersWired: false);
    }

    /// <summary>帧耗时单元格：avg/max 与目标预算一起写，否则读者无法判断 max 是否超限。</summary>
    public static string FrameText(PetFrameReadings readings) => string.Format(
        CultureInfo.InvariantCulture,
        I18n.Get("ui.perf.frame"), Fmt(readings.AvgFrameMs), Fmt(readings.MaxFrameMs), Fmt(readings.TargetFrameMs));

    private static string Fmt(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
