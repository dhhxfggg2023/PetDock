// 授权弹窗（原规格 18 §3，RFC-12 §2.1 补的交互细节；RFC-13 §2.2 改为逐项勾选）。
// 规则来自文档，不是我的设计：拒绝后不允许重试同一能力；「仅本次」只对 Tier 1 有效，
// 因此 Tier 2 的「仅本次」按钮置灰；被拒绝的能力在下次打开弹窗时置灰且点不动。
// 2026-10-26 真机故障（三只插件启动时同时要授权 → 连弹三个窗）：同一个窗可以同时承载
// 多份请求，按插件分段展示；答案仍按每份请求各自落账（各自勾选集，范围三选一统一）。
// 合批那一半在 AuthorizationDialog.Batch.cs（partial：D-03c 单文件行数上限）。
// 能力描述照抄 原规格 14 §2 的条目，用大白话重述。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

/// <summary>用户对一次授权请求的回答。</summary>
internal enum GrantAnswer
{
    Allow,
    Deny,
    Once,
}

/// <summary>授权弹窗。无状态：每次打开都是新的，答案由调用方落账。</summary>
internal sealed partial class AuthorizationDialog : PetDockDialog
{
    private readonly ThemedRadio _allow;
    private readonly ThemedRadio _deny;
    private readonly ThemedRadio _once;
    private readonly ThemedButton _ok;
    private readonly Label _header;
    private readonly ThemedList _list;

    /// <summary>本窗要问的请求（按顺序）。单请求弹窗就是只有一个元素的批。</summary>
    private readonly List<ConsentRequest> _requests;

    /// <summary>展示出来的能力 ID，顺序 = 清单顺序（第 i 个对应第 i 行）。</summary>
    private readonly List<string> _ids = new();

    /// <summary>每一行的勾选状态（逻辑真值）。原生控件的勾选状态在句柄建好前读不得
    /// （行还没有原生下标，一读就抛），所以真值自己存一份，不去问原生控件。</summary>
    private readonly List<bool> _checked = new();

    /// <summary>每一行属于第几份请求（与 <see cref="_ids"/> 平行）。</summary>
    private readonly List<int> _owner = new();

    /// <summary>此前已被拒绝的能力 ID：置灰、不可勾、也永不出现在 <see cref="SelectedIds"/> 里。</summary>
    private readonly HashSet<string> _deniedIds = new(StringComparer.Ordinal);

    /// <summary>构造期：初始勾选状态只是标志位，别让事件处理器插手。</summary>
    private bool _building;

    /// <summary>说明行。晚到的请求会把它从「单插件」换成「N 个插件同时请求」，
    /// 所以它是字段而不是构造期局部变量。</summary>
    private readonly Label _intro;

    /// <summary>旧入口：只有能力 ID 列表（没有既往状态）。</summary>
    public AuthorizationDialog(string pluginName, string source, int tier, IReadOnlyList<string> capabilities)
        : this(pluginName, source, tier, PlainItems(capabilities), isReview: false)
    {
    }

    /// <summary>新入口：带既往授权状态与「复核」标记。
    /// <paramref name="isReview"/> 给默认值，是为了让两个重载的参数个数一致 ——
    /// 否则四参数调用会绑到「只有 ID 列表」那个重载上，编译期报不出意图。</summary>
    public AuthorizationDialog(string pluginName, string source, int tier, IReadOnlyList<ConsentItem> items, bool isReview = false)
        : this(new[] { new ConsentRequest(source, pluginName, tier, items, isReview) })
    {
    }

    /// <summary>批入口（2026-10-26）：同批请求只开一个窗，按插件分段；一次回答覆盖同批每份请求。
    /// 每份请求仍各自保留勾选集，范围（允许/拒绝/仅本次）统一 —— 用户看到的是一次询问。</summary>
    public AuthorizationDialog(IReadOnlyList<ConsentRequest> requests)
    {
        _requests = new List<ConsentRequest>(requests);

        Text = I18n.Get("ui.auth.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Theme.Apply(this);
        Width = Theme.Dip(this, 620);
        // 高度只留「标题 + 说明 + 两三条能力 + 选项 + 按钮」的余量：能力只有一两条时，
        // 多出来的高度会变成列表里一大片空行（2026-10-28 截图实测），读起来像界面没画完。
        Height = Theme.Dip(this, 400);
        int dpi = Theme.DpiOf(this);

        _header = new Label
        {
            Text = HeaderFor(),
            AutoSize = false,
            // 30dip 时上边距吃掉 8dip，留给 Heading 字形的高度只有 22px，比行高少 2px
            // （2026-10-28 判据补上「高度也要放得下」之后抓到的）。34dip 留出余量。
            Height = Theme.Dip(this, 34),
            Dock = DockStyle.Top,
            Font = Theme.FontFor(this, TypeRole.Heading),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceSm), Theme.Dip(this, Theme.SpaceMd), 0),
        };

        _intro = new Label
        {
            Text = I18n.Get(Batch ? "ui.auth.intro.batch" : "ui.auth.intro"),
            AutoSize = false,
            Height = Theme.Dip(this, 44),
            Dock = DockStyle.Top,
            Font = Theme.FontFor(this, TypeRole.Body),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceXs), Theme.Dip(this, Theme.SpaceMd), 0),
        };

        // 逐项可勾：勾选框由列表自己画（见 ThemedList.RowChecks），真值存在 _checked 里。
        // 批模式用分组头把每份请求隔开：一个窗、几个插件、各自一段。
        _list = new ThemedList
        {
            Dock = DockStyle.Fill,
            RowChecks = true,
            ShowGroups = Batch,
            IsRowChecked = IsRowChecked,
            IsRowCheckEnabled = IsRowCheckEnabled,
            RowCheckToggled = ToggleRowCheck,
        };
        _list.Columns.Add(I18n.Get("ui.auth.col.capability"));
        CreateGroups();

        _building = true;
        for (int r = 0; r < _requests.Count; r++)
        {
            BuildRows(r);
        }

        _building = false;

        _list.FitColumns();

        if (_list.Items.Count == 0)
        {
            _list.Items.Add(new ListViewItem(I18n.Get("ui.auth.empty")));
        }

        _allow = new ThemedRadio { Text = I18n.Get("ui.auth.allow"), Name = "Allow" };
        _allow.Checked = true;
        _deny = new ThemedRadio { Text = I18n.Get("ui.auth.deny"), Name = "Deny" };
        _once = new ThemedRadio
        {
            // 「仅本次」仅对 Tier 1 会话有效（原规格 18 §3 规则）——批里只要有一份 Tier 2，
            // 统一的范围就选不了「仅本次」：不可选的选项要看起来不可选。
            Text = _requests.Any(r => r.Tier >= 2) ? I18n.Get("ui.auth.once.tier2") : I18n.Get("ui.auth.once"),
            Enabled = _requests.All(r => r.Tier < 2),
            Name = "Once",
        };
        _allow.FitToText();
        _deny.FitToText();
        _once.FitToText();

        var choices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(this, 94),
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceXs), Theme.Dip(this, Theme.SpaceMd), 0),
            WrapContents = false,
            BackColor = Theme.Bg,
        };
        choices.Controls.Add(_allow);
        choices.Controls.Add(_deny);
        choices.Controls.Add(_once);

        _ok = new ThemedButton { Text = I18n.Get("ui.auth.ok"), DialogResult = DialogResult.OK, Primary = true };
        _ok.FitToText();
        var cancel = new ThemedButton { Text = I18n.Get("ui.auth.cancel"), DialogResult = DialogResult.Cancel };
        cancel.FitToText();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = Theme.Dip(this, 52),
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceSm), Theme.Dip(this, Theme.SpaceMd), Theme.Dip(this, Theme.SpaceSm)),
            BackColor = Theme.Bg,
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var footer = new Label
        {
            Text = I18n.Get("ui.auth.footer"),
            AutoSize = false,
            Height = Theme.Dip(this, 26),
            Dock = DockStyle.Bottom,
            Font = Theme.FontFor(this, TypeRole.Caption),
            ForeColor = Theme.TextDim,
            BackColor = Theme.Bg,
            Padding = new Padding(Theme.Dip(this, Theme.SpaceMd), 0, Theme.Dip(this, Theme.SpaceMd), 0),
        };

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(choices);
        Controls.Add(buttons);
        Controls.Add(_intro);
        Controls.Add(_header);
        AcceptButton = _ok;
        CancelButton = cancel;
    }

    /// <summary>本次回答。对话框被取消时按「拒绝」处理（默认拒绝比默认允许安全）。</summary>
    public GrantAnswer Answer =>
        _deny.Checked ? GrantAnswer.Deny
        : _once.Enabled && _once.Checked ? GrantAnswer.Once
        : GrantAnswer.Allow;

    /// <summary>三个选项是否都在场（自检用，按稳定顺序）。</summary>
    public string Answers =>
        string.Join(",", new[] { _allow, _deny, _once }.Select(r => r.Name));

    /// <summary>「仅本次」是否可选（Tier 2 必须置灰，原规格 18 §3）。</summary>
    public bool OnceEnabled => _once.Enabled;

    /// <summary>当前勾选的能力 ID（顺序 = 清单顺序；被拒绝的项永不在内）。</summary>
    public IReadOnlyList<string> SelectedIds
    {
        get
        {
            var picked = new List<string>();
            for (int i = 0; i < _ids.Count && i < _checked.Count; i++)
            {
                if (_checked[i] && !_deniedIds.Contains(_ids[i]))
                {
                    picked.Add(_ids[i]);
                }
            }

            return picked;
        }
    }

    /// <summary>展示出来的全部能力 ID（顺序 = 清单顺序，含此前被拒绝的项）。</summary>
    public IReadOnlyList<string> ItemIds => _ids.ToArray();

    /// <summary>被拒绝（Denied==true）因而锁定不可勾的能力 ID（顺序 = 清单顺序）。</summary>
    public IReadOnlyList<string> LockedIds =>
        _ids.Where(id => _deniedIds.Contains(id)).ToArray();

    /// <summary>该能力是否锁定（此前已被拒绝：置灰且点不动，原规格 18 §3）。</summary>
    public bool IsItemLocked(string capabilityId) => _deniedIds.Contains(capabilityId);

    /// <summary>以编程方式设置某项的勾选状态，返回是否真的生效。
    /// 被拒绝（锁定）的项一律不生效：返回 false，且 <see cref="SelectedIds"/> 不变 ——
    /// 「拒绝后该插件不允许重试同一能力」这条规则在数据层就成立，不只是画灰
    /// （原规格 18 §3）。这个入口是给无人值守自检用的：真实鼠标点击跑不出来，
    /// 但「被拒项即使被程序强行设置也不进授予集」是可断言的。
    /// 同 ID 出现在多份请求里时作用于第一行（批模式请用 <see cref="SetItemCheckedAt"/>）。</summary>
    public bool SetItemChecked(string capabilityId, bool isChecked)
    {
        int index = _ids.IndexOf(capabilityId);
        return index >= 0 && SetItemCheckedAt(index, isChecked);
    }

    /// <summary>一行显示文字：人话描述 + 既往状态后缀。
    /// <see cref="PlainTalk"/> 的实现放在 AuthorizationDialog.Batch.cs（D-03c 行数上限）。</summary>
    private static string RowText(ConsentItem entry) =>
        PlainTalk(entry.CapabilityId)
        + (entry.PreGranted ? I18n.Get("ui.auth.row.preGranted") : string.Empty)
        + (entry.Denied ? I18n.Get("ui.auth.row.denied") : string.Empty);

    /// <summary>某一行（按 Tag 里的行下标）当前是否勾着 —— 列表绘制时问这里。
    /// 真值只存在 <see cref="_checked"/> 里：不读原生控件，也就不会踩「句柄建好前读不得」。</summary>
    private bool IsRowChecked(ListViewItem row) =>
        row.Tag is int index && index >= 0 && index < _checked.Count && _checked[index];

    /// <summary>某一行是否可勾：此前被拒绝的能力置灰且点不动（原规格 18 §3）。</summary>
    private bool IsRowCheckEnabled(ListViewItem row) =>
        row.Tag is int index && index >= 0 && index < _ids.Count && !_deniedIds.Contains(_ids[index]);

    /// <summary>用户点在勾选框上：翻转这一行的真值并重画。不可勾的行由列表先挡掉。</summary>
    private void ToggleRowCheck(ListViewItem row)
    {
        if (row.Tag is int index && index >= 0 && index < _checked.Count)
        {
            SetItemCheckedAt(index, !_checked[index]);
        }
    }
}
