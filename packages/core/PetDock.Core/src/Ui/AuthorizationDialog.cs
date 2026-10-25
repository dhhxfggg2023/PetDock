// 授权弹窗（docs/18 §3，RFC-12 §2.1 补的交互细节；RFC-13 §2.2 改为逐项勾选）。
// 规则来自文档，不是我的设计：拒绝后不允许重试同一能力；「仅本次」只对 Tier 1 有效，
// 因此 Tier 2 的「仅本次」按钮置灰；被拒绝的能力在下次打开弹窗时置灰且点不动。
// 能力描述照抄 docs/14 §2 的条目，用大白话重述。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
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
internal sealed class AuthorizationDialog : Form
{
    private readonly RadioButton _allow;
    private readonly RadioButton _deny;
    private readonly RadioButton _once;
    private readonly Button _ok;
    private readonly ListView _list;

    /// <summary>展示出来的能力 ID，顺序 = 清单顺序（第 i 个对应第 i 行）。</summary>
    private readonly List<string> _ids = new();

    /// <summary>每一行的勾选状态（逻辑真值）。原生控件的勾选状态在句柄建好前读不得
    /// （行还没有原生下标，一读就抛），所以真值自己存一份，不去问原生控件。</summary>
    private readonly List<bool> _checked = new();

    /// <summary>此前已被拒绝的能力 ID：置灰、不可勾、也永不出现在 <see cref="SelectedIds"/> 里。</summary>
    private readonly HashSet<string> _deniedIds = new(StringComparer.Ordinal);

    /// <summary>构造期：初始勾选状态只是标志位，别让事件处理器插手。</summary>
    private bool _building;

    /// <summary>旧入口：只有能力 ID 列表（没有既往状态）。</summary>
    public AuthorizationDialog(string pluginName, string source, int tier, IReadOnlyList<string> capabilities)
        : this(pluginName, source, tier, PlainItems(capabilities), isReview: false)
    {
    }

    /// <summary>新入口：带既往授权状态与「复核」标记。
    /// <paramref name="isReview"/> 给默认值，是为了让两个重载的参数个数一致 ——
    /// 否则四参数调用会绑到「只有 ID 列表」那个重载上，编译期报不出意图。</summary>
    public AuthorizationDialog(string pluginName, string source, int tier, IReadOnlyList<ConsentItem> items, bool isReview = false)
    {
        Text = I18n.Get("ui.auth.title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 560;
        Height = 430;
        Font = SystemFonts.MessageBoxFont;

        var title = new Label
        {
            Text = string.Format(CultureInfo.InvariantCulture, I18n.Get(isReview ? "ui.auth.header.review" : "ui.auth.header"), pluginName, source),
            AutoSize = false,
            Height = 24,
            Dock = DockStyle.Top,
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
            Padding = new Padding(12, 8, 12, 0),
        };

        var intro = new Label
        {
            Text = I18n.Get("ui.auth.intro"),
            AutoSize = false,
            Height = 40,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 4, 12, 0),
        };

        // 逐项可勾：ListView 的 Details 视图 + 复选框（CheckedListBox 不能按项置灰）。
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = true,
        };
        _list.Columns.Add(I18n.Get("ui.auth.col.capability"), 470);
        // ItemCheck 是事前事件：e.NewValue 可以改 —— 改成 Unchecked 就不勾。
        _list.ItemCheck += OnItemCheck;
        // 事后事件兜底：万一事前事件被绕过去了（例如外部代码直接改控件状态），这里补回来。
        _list.ItemChecked += OnItemChecked;

        _building = true;
        foreach (ConsentItem entry in items)
        {
            var row = new ListViewItem(RowText(entry))
            {
                // Tag 存能力 ID：事后事件里靠它把原生行对回清单下标
                //（ItemCheckedEventArgs 只有 Item，没有 Index）。
                Tag = entry.CapabilityId,
                ForeColor = entry.Denied ? SystemColors.GrayText : SystemColors.ControlText,
            };
            _list.Items.Add(row);
            _ids.Add(entry.CapabilityId);
            if (entry.Denied)
            {
                _deniedIds.Add(entry.CapabilityId);
                _checked.Add(false);
            }
            else
            {
                // 默认全勾（docs/18 §3：只有被拒绝的项才默认不勾）。
                // 句柄还没建时设勾选状态是安全的，而且会一直带到原生控件上。
                _checked.Add(true);
                row.Checked = true;
            }
        }
        _building = false;

        if (_list.Items.Count == 0)
        {
            _list.CheckBoxes = false;
            _list.Items.Add(new ListViewItem(I18n.Get("ui.auth.empty")));
        }

        _allow = new RadioButton { Text = I18n.Get("ui.auth.allow"), AutoSize = true, Checked = true, Name = "Allow" };
        _deny = new RadioButton { Text = I18n.Get("ui.auth.deny"), AutoSize = true, Name = "Deny" };
        _once = new RadioButton
        {
            Text = tier >= 2 ? I18n.Get("ui.auth.once.tier2") : I18n.Get("ui.auth.once"),
            // 「仅本次」仅对 Tier 1 会话有效（docs/18 §3 规则）——不可选的选项要看起来不可选。
            Enabled = tier < 2,
            AutoSize = true,
            Name = "Once",
        };

        var choices = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Bottom,
            Height = 76,
            Padding = new Padding(12, 4, 12, 0),
            WrapContents = false,
        };
        choices.Controls.Add(_allow);
        choices.Controls.Add(_deny);
        choices.Controls.Add(_once);

        _ok = new Button { Text = I18n.Get("ui.auth.ok"), DialogResult = DialogResult.OK, Width = 88 };
        var cancel = new Button { Text = I18n.Get("ui.auth.cancel"), DialogResult = DialogResult.Cancel, Width = 88 };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(12, 6, 12, 6),
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var footer = new Label
        {
            Text = I18n.Get("ui.auth.footer"),
            AutoSize = false,
            Height = 22,
            Dock = DockStyle.Bottom,
            Padding = new Padding(12, 0, 12, 0),
            ForeColor = SystemColors.GrayText,
        };

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(choices);
        Controls.Add(buttons);
        Controls.Add(intro);
        Controls.Add(title);
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

    /// <summary>「仅本次」是否可选（Tier 2 必须置灰，docs/18 §3）。</summary>
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

    /// <summary>该能力是否锁定（此前已被拒绝：置灰且点不动，docs/18 §3）。</summary>
    public bool IsItemLocked(string capabilityId) => _deniedIds.Contains(capabilityId);

    /// <summary>以编程方式设置某项的勾选状态，返回是否真的生效。
    /// 被拒绝（锁定）的项一律不生效：返回 false，且 <see cref="SelectedIds"/> 不变 ——
    /// 「拒绝后该插件不允许重试同一能力」这条规则在数据层就成立，不只是画灰
    /// （docs/18 §3）。这个入口是给无人值守自检用的：真实鼠标点击跑不出来，
    /// 但「被拒项即使被程序强行设置也不进授予集」是可断言的。</summary>
    public bool SetItemChecked(string capabilityId, bool isChecked)
    {
        int index = _ids.IndexOf(capabilityId);
        if (index < 0 || index >= _checked.Count)
        {
            return false;
        }

        if (_deniedIds.Contains(capabilityId))
        {
            // 锁定项不可勾：明确不改任何状态。
            _checked[index] = false;
            return false;
        }

        _checked[index] = isChecked;
        if (index < _list.Items.Count)
        {
            TrySetChecked(_list.Items[index], isChecked);
        }

        return true;
    }

    /// <summary>把能力 ID 说成人话（docs/14 §2 的描述列）。未知能力原样显示 ——
    /// 猜一个友好的说法比显示原始 ID 更危险：用户会以为他知道自己授权了什么。</summary>
    public static string PlainTalk(string capabilityId) => capabilityId switch
    {
        "input.mousePosition" => I18n.Get("ui.auth.cap.input.mousePosition"),
        "input.moveWindow" => I18n.Get("ui.auth.cap.input.moveWindow"),
        "input.clickThrough" => I18n.Get("ui.auth.cap.input.clickThrough"),
        "input.keyboardHook" => I18n.Get("ui.auth.cap.input.keyboardHook"),
        "assets.read:cas" => I18n.Get("ui.auth.cap.assets.read"),
        "assets.write:cas" => I18n.Get("ui.auth.cap.assets.write"),
        "audio.play" => I18n.Get("ui.auth.cap.audio.play"),
        "persist.write:self" => I18n.Get("ui.auth.cap.persist.write"),
        "persist.read:self" => I18n.Get("ui.auth.cap.persist.read"),
        "screen.changeDpi" => I18n.Get("ui.auth.cap.screen.changeDpi"),
        _ when capabilityId.StartsWith("network.fetch:", StringComparison.Ordinal) =>
            string.Format(CultureInfo.InvariantCulture, I18n.Get("ui.auth.cap.networkFetch"), capabilityId["network.fetch:".Length..], capabilityId),
        _ when capabilityId.StartsWith("ipc.tier2:", StringComparison.Ordinal) => I18n.Get("ui.auth.cap.ipcTier2", capabilityId),
        _ => capabilityId,
    };

    /// <summary>旧入口的清单：只有 ID，没有既往状态 —— 全部视为「未拒绝、未授予」。</summary>
    private static IReadOnlyList<ConsentItem> PlainItems(IReadOnlyList<string> capabilities)
    {
        var items = new List<ConsentItem>(capabilities.Count);
        foreach (string id in capabilities)
        {
            items.Add(new ConsentItem(id, PreGranted: false, Denied: false));
        }

        return items;
    }

    /// <summary>一行显示文字：人话描述 + 既往状态后缀。</summary>
    private static string RowText(ConsentItem entry) =>
        PlainTalk(entry.CapabilityId)
        + (entry.PreGranted ? I18n.Get("ui.auth.row.preGranted") : string.Empty)
        + (entry.Denied ? I18n.Get("ui.auth.row.denied") : string.Empty);

    /// <summary>事前事件：勾选状态要变之前先问一次。
    /// 被拒绝的项在这里把 e.NewValue 改成 Unchecked —— 勾选就不会发生（docs/18 §3：
    /// 被拒绝的能力点不动、也不再申请）。这条路径不碰原生控件的 Checked 属性，
    /// 所以不受「行还没拿到原生下标就读不得」的限制。</summary>
    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_building || e.Index < 0 || e.Index >= _checked.Count)
        {
            return;
        }

        if (_deniedIds.Contains(_ids[e.Index]))
        {
            e.NewValue = CheckState.Unchecked;
            _checked[e.Index] = false;
            return;
        }

        _checked[e.Index] = e.NewValue == CheckState.Checked;
    }

    /// <summary>事后事件兜底：若事前事件被绕过去了（例如外部代码直接改控件状态），
    /// 这里把被拒绝项弹回未勾，并把逻辑真值对齐。
    /// 事后事件的参数只有 Item（没有 Index），行下标靠 Tag 里的能力 ID 对回来。</summary>
    private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_building || e.Item is not ListViewItem row)
        {
            return;
        }

        int index = row.Tag is string id ? _ids.IndexOf(id) : -1;
        if (index < 0 || index >= _checked.Count)
        {
            return;
        }

        if (_deniedIds.Contains(_ids[index]))
        {
            _checked[index] = false;
            LockDenied(index);
            return;
        }

        _checked[index] = ReadChecked(row);
    }

    /// <summary>把被拒绝项弹回未勾：同步改一次，再用 BeginInvoke 兜一次底。</summary>
    private void LockDenied(int index)
    {
        if (index < 0 || index >= _list.Items.Count)
        {
            return;
        }

        TrySetChecked(_list.Items[index], false);

        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(new MethodInvoker(() =>
            {
                if (!IsDisposed && index < _list.Items.Count && ReadChecked(_list.Items[index]))
                {
                    TrySetChecked(_list.Items[index], false);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁，兜底回弹不必再做。
        }
    }

    /// <summary>设置一行的勾选状态。行还没有原生下标时（控件刚建句柄）会抛 ——
    /// 那时按「改不动」处理：逻辑真值已经记下了，画面稍后自己跟上。</summary>
    private static void TrySetChecked(ListViewItem row, bool isChecked)
    {
        try
        {
            row.Checked = isChecked;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // 行还没有原生下标，或者控件已销毁。
        }
    }

    /// <summary>读一行的勾选状态。读不得时按「未勾」处理。</summary>
    private static bool ReadChecked(ListViewItem row)
    {
        try
        {
            return row.Checked;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // 行还没有原生下标（控件刚建句柄），或者控件已销毁。
            return false;
        }
    }
}
