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

namespace PetDock.Core.Ui;

internal sealed class PerfPage : UserControl
{
    /// <summary>「没有可用归属」时单元格里的占位符（不是 0：0 会被读成「测过是 0」）。</summary>
    public const string NotAttributed = "—";

    /// <summary>进程级读数在逐宠物行里的标注（docs/10 §3）。</summary>
    public const string ProcessShared = "(进程共享)";

    private readonly Func<UiSnapshot> _snapshot;
    private readonly Func<string, PetFrameReadings?>? _framesFor;
    private readonly Label _summary;
    private readonly ListView _rows;
    private readonly Button _sample;

    public PerfPage(Func<UiSnapshot> snapshot, Func<string, PetFrameReadings?>? frames = null)
    {
        _snapshot = snapshot ?? (() => UiSnapshot.Empty);
        _framesFor = frames;
        Name = "perf-page";

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(8, 6, 8, 0),
            Text = "尚未采样。点「开始采样（60s）」取得与 D-14 门禁同一条测量链的读数。",
        };

        _sample = new Button
        {
            Text = "开始采样（60s）",
            Dock = DockStyle.Top,
            Height = 32,
            Name = "sample",
        };
        _sample.Click += (_, _) => Sample();

        _rows = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Name = "perf-rows",
        };
        _rows.Columns.Add("宠物", 150);
        _rows.Columns.Add("CPU", 110);
        _rows.Columns.Add("私有工作集", 130);
        _rows.Columns.Add("帧耗时", 110);
        _rows.Columns.Add("超限计数", 100);

        Controls.Add(_rows);
        Controls.Add(_sample);
        Controls.Add(_summary);
    }

    /// <summary>把一次快照摆到页面上。空快照不造数（§2.4）。</summary>
    public void Bind(PerfSummary perf)
    {
        _rows.Items.Clear();
        if (perf.IsEmpty)
        {
            _summary.Text = "尚无宠物运行。没有可测量的对象，因此不显示任何性能数字。";
            return;
        }

        foreach (PerfLine line in perf.Rows)
        {
            string overruns = line.Overruns?.ToString(CultureInfo.InvariantCulture) ?? NotAttributed;
            _rows.Items.Add(new ListViewItem(new[] { line.Name, line.Cpu, line.Memory, line.FrameMs, overruns }));
        }

        _summary.Text = Summary(perf);
    }

    /// <summary>汇总行的文案。降级/隔离区计数没有接收口时写「未接」（RFC-30 §3.4）。</summary>
    public static string Summary(PerfSummary perf)
    {
        string counters = perf.CountersWired
            ? $"降级 {perf.Degraded} ｜ 隔离区 {perf.Isolated}"
            : "降级 未接 ｜ 隔离区 未接";
        return string.Create(CultureInfo.InvariantCulture, $"采样窗口 {perf.Window} ｜ {counters}");
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
        _summary.Text = "采样中…（60s，与 D-14 门禁同一测量链）";
        Application.DoEvents();
        try
        {
            PerfSummary measured = PerfSampler.Measure(60_000, snapshot, _framesFor);
            Bind(measured);
        }
        catch (Exception ex)
        {
            _summary.Text = "采样失败：" + ex.GetType().Name + ": " + ex.Message;
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

    /// <summary>摘要文字（自检用：空快照时必须包含「尚无宠物运行」）。</summary>
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
                Name: "(进程合计)",
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
                Cpu: PerfPage.ProcessShared,
                Memory: PerfPage.ProcessShared,
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
    public static string FrameText(PetFrameReadings readings) => string.Create(
        CultureInfo.InvariantCulture,
        $"avg {Fmt(readings.AvgFrameMs)} / max {Fmt(readings.MaxFrameMs)} ms（目标 {Fmt(readings.TargetFrameMs)}ms）");

    private static string Fmt(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
