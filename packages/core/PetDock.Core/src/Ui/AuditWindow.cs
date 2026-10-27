// 审计流水的只读视图（原规格 18 §2.8）：把「谁在什么时候被授予/收回了什么」摊开给用户看。
//
// 为什么非要一个窗：流水从 2026-10-20 起就一直在落盘（AuditTrail），但只有落盘、没有出口——
// 用户只能去程序目录里翻 jsonl。数据层有用而界面上看不见，等于没做（原规格 00 §2.3 的 M-11）。
//
// 这一层刻意只做三件事：读、列、说清「读不到」与「还没有」的区别。判定与写入都不在这里。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed class AuditWindow : PetDockDialog
{
    /// <summary>最多列这么多行（流水可以有几十万字，窗口只负责「最近发生了什么」）。</summary>
    private const int MaxRows = 200;

    private readonly ThemedList _list;
    private readonly Label _status;

    private AuditWindow(AuditTrailRead read)
    {
        Text = I18n.Get("cp.audit.title");
        Theme.Apply(this);
        ClientSize = new Size(Theme.Dip(this, 720), Theme.Dip(this, 460));
        StartPosition = FormStartPosition.CenterParent;

        _status = new Label
        {
            Name = "audit-status",
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(this, 40),
            AutoSize = false,
            BackColor = Theme.Bg,
            ForeColor = Theme.TextDim,
            Font = Theme.FontFor(this, TypeRole.Body),
            Padding = new Padding(Theme.Dip(this, Theme.SpaceSm), Theme.Dip(this, Theme.SpaceXs), 0, 0),
        };

        _list = new ThemedList
        {
            Name = "audit-rows",
            Dock = DockStyle.Fill,
        };
        _list.SetColumns(
            (I18n.Get("cp.audit.col.time"), 150),
            (I18n.Get("cp.audit.col.plugin"), 160),
            (I18n.Get("cp.audit.col.kind"), 90),
            (I18n.Get("cp.audit.col.detail"), 260));

        FlowLayoutPanel bar = Theme.DialogButtonBar(this);
        var close = new ThemedButton
        {
            Name = "audit-close",
            Text = I18n.Get("cp.audit.close"),
            Primary = true,
        };
        close.Click += (_, _) => Close();
        bar.Controls.Add(close);

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(bar);

        // 停靠顺序：Fill 先加会吃掉整块，底栏与状态条必须在它前面入栈（2026-10-28 实测过的形状）。
        Controls.SetChildIndex(_list, 0);

        Fill(read);
        close.FitToText();
        AcceptButton = close;
        CancelButton = close;
        Theme.ApplyMaterial(this);
    }

    /// <summary>行数（自检读它，避免自检去数控件）。</summary>
    public int RowCount => _list.Items.Count;

    /// <summary>列数（自检读它：列被删掉时窗还在，但信息就不全了）。</summary>
    public int RowColumnCount => _list.Columns.Count;

    /// <summary>底部那句话（自检读它，区分「读不到」与「还没有」）。</summary>
    public string StatusText => _status.Text;

    /// <summary>按读回的结果建窗。窗口不自己读磁盘：读盘由宿主侧做，判定只在这一层之外。</summary>
    public static AuditWindow Create(AuditTrailRead read)
    {
        return new AuditWindow(read);
    }

    private void Fill(AuditTrailRead read)
    {
        if (!read.Available)
        {
            // 读不到：说清是「读不出来」，并把原因原样带出来。
            _status.ForeColor = Theme.Bad;
            _status.Text = read.Note;
            return;
        }

        if (read.Rows.Count == 0)
        {
            _status.Text = I18n.Get("cp.audit.empty");
            return;
        }

        int shown = Math.Min(read.Rows.Count, MaxRows);
        int first = read.Rows.Count - shown;
        _list.BeginUpdate();
        try
        {
            for (int i = first; i < read.Rows.Count; i++)
            {
                AuditTrailRow row = read.Rows[i];
                string detail = row.Incomplete ? I18n.Get("cp.audit.incomplete", row.Detail) : row.Detail;
                _list.Items.Add(new ListViewItem(new[] { row.Timestamp, row.PluginId, KindLabel(row.Kind), detail }));
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _list.FitColumns();
        // I18n.Get 最多带两个占位参数（I18n.cs 只声明了 0/1/2 个参数的三个重载），
        // 所以这句由两段拼出来，而不是塞四个占位符。
        string summary = I18n.Get("cp.audit.summary", shown, read.Rows.Count)
            + I18n.Get("cp.audit.files", read.Files);
        if (read.Incomplete > 0)
        {
            summary += I18n.Get("cp.audit.unfinished", read.Incomplete);
        }

        _status.Text = summary;
    }

    /// <summary>类别名走 i18n；认不出的类别原样显示，不吞掉。</summary>
    private static string KindLabel(string kind) => kind switch
    {
        AuditTrail.KindGrant => I18n.Get("cp.audit.kind.grant"),
        AuditTrail.KindDeny => I18n.Get("cp.audit.kind.deny"),
        AuditTrail.KindRevoke => I18n.Get("cp.audit.kind.revoke"),
        AuditTrail.KindBudget => I18n.Get("cp.audit.kind.budget"),
        AuditTrail.KindFault => I18n.Get("cp.audit.kind.fault"),
        AuditTrail.KindConsent => I18n.Get("cp.audit.kind.consent"),
        AuditTrail.KindRestrict => I18n.Get("cp.audit.kind.restrict"),
        _ => kind,
    };
}