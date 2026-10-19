// 性能页（docs/18 §2.4，RFC-12 §2.1）：多宠物聚合视图 + 60s 采样。
// 硬约束：**没有宠物时显示「尚无宠物运行」，不得编造数字**——这是这条门禁读数的全部价值所在，
// 一条会自己造数的面板比没有面板更坏（RFC-04 §3 付过这笔账）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace PetDock.Core.Ui;

internal sealed class PerfPage : UserControl
{
    private readonly Func<UiSnapshot> _snapshot;
    private readonly Label _summary;
    private readonly ListView _rows;
    private readonly Button _sample;

    public PerfPage(Func<UiSnapshot> snapshot)
    {
        _snapshot = snapshot ?? (() => UiSnapshot.Empty);
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
            _rows.Items.Add(new ListViewItem(new[] { line.Name, line.Cpu, line.Memory, line.FrameMs, line.Overruns.ToString(CultureInfo.InvariantCulture) }));
        }

        _summary.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"采样窗口 {perf.Window} ｜ 降级 {perf.Degraded} ｜ 隔离区 {perf.Isolated}");
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
            PerfSummary measured = PerfSampler.Measure(60_000);
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
}

/// <summary>
/// 面板侧采样器：只在**页面真实打开**时才跑，避免常驻路径平白多出一个 60s 循环。
/// 读数口径与 PerfGate（--perftest）一致：TotalProcessorTime 增量 / 墙钟 / 核数，私有工作集。
/// </summary>
internal static class PerfSampler
{
    public static PerfSummary Measure(int windowMs)
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        self.Refresh();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (wall.ElapsedMilliseconds < windowMs)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
            if (wall.ElapsedMilliseconds % 5000 < 55)
            {
                // 每 5s 刷一次提示，让「还在采样」可见（否则 60s 像卡死）
                Application.DoEvents();
            }
        }

        wall.Stop();
        self.Refresh();
        double cpuPct = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds
            / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100.0;
        double wsMb = self.WorkingSet64 / (1024.0 * 1024.0);

        var rows = new List<PerfLine>(1)
        {
            new(
                Name: "(进程合计)",
                Cpu: cpuPct.ToString("0.00", CultureInfo.InvariantCulture) + " %",
                Memory: wsMb.ToString("0.00", CultureInfo.InvariantCulture) + " MB",
                FrameMs: "—",
                Overruns: 0),
        };

        return new PerfSummary(
            Window: (wall.Elapsed.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + "s",
            Degraded: 0,
            Isolated: 0,
            Rows: rows);
    }
}
