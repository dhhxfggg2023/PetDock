// 授权弹窗的**合批**那一半（2026-10-26）：一个窗承载多份请求、按插件分段、按段分别落账。
// 与 AuthorizationDialog.cs 是同一个类（partial）：单请求弹窗走老路径（不分段、旧标题文案），
// 批请求才分段。分文件的原因只有一个：D-03c 的单文件行数上限。
// 契约来源：docs/18 §3「合批（2026-10-26）」。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class AuthorizationDialog
{
    /// <summary>是否按插件分段展示：只有多于一份请求才分段。</summary>
    private bool Batch => _requests.Count > 1;

    /// <summary>本窗承载了几份请求（单请求弹窗恒为 1）。</summary>
    public int RequestCount => _requests.Count;

    /// <summary>是否按插件分段展示（批模式才分段）。</summary>
    public bool GroupsVisible => _list.ShowGroups;

    /// <summary>分段头文字（按请求顺序；单请求弹窗为空）。</summary>
    public IReadOnlyList<string> GroupHeaders =>
        _list.Groups.Cast<ListViewGroup>().Select(g => g.Header).ToArray();

    /// <summary>标题行文字（批模式与单请求的文案不同）。</summary>
    public string HeaderText => _header.Text;

    /// <summary>某一份请求当前勾选的能力 ID（批模式里每份请求各自落账用）。</summary>
    public IReadOnlyList<string> SelectedIdsFor(int requestIndex) =>
        Enumerable.Range(0, _ids.Count)
            .Where(i => _owner[i] == requestIndex && _checked[i] && !_deniedIds.Contains(_ids[i]))
            .Select(i => _ids[i])
            .ToArray();

    /// <summary>某一份请求展示出来的能力 ID（顺序 = 清单顺序，含此前被拒绝的项）。</summary>
    public IReadOnlyList<string> ItemIdsFor(int requestIndex) =>
        Enumerable.Range(0, _ids.Count).Where(i => _owner[i] == requestIndex).Select(i => _ids[i]).ToArray();

    /// <summary>按下标设置某一行的勾选状态（批模式自检用；语义同 <see cref="SetItemChecked"/>）。</summary>
    public bool SetItemCheckedAt(int index, bool isChecked)
    {
        if (index < 0 || index >= _checked.Count)
        {
            return false;
        }

        if (_deniedIds.Contains(_ids[index]))
        {
            // 锁定项不可勾：明确不改任何状态。
            _checked[index] = false;
            return false;
        }

        _checked[index] = isChecked;
        _list.Invalidate();

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
        _ when capabilityId.StartsWith("ipc.tier2:", StringComparison.Ordinal) => string.Format(CultureInfo.InvariantCulture, I18n.Get("ui.auth.cap.ipcTier2"), capabilityId),
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

    /// <summary>标题行：单请求沿用旧文案（含「复核」），批请求用「N 个插件同时请求」。</summary>
    private string HeaderFor()
    {
        if (!Batch)
        {
            if (_requests.Count == 0)
            {
                return string.Format(CultureInfo.InvariantCulture, I18n.Get("ui.auth.header.batch"), 0);
            }

            ConsentRequest only = _requests[0];
            return string.Format(
                CultureInfo.InvariantCulture,
                I18n.Get(only.IsReview ? "ui.auth.header.review" : "ui.auth.header"),
                only.PluginName,
                only.PluginId);
        }

        return string.Format(CultureInfo.InvariantCulture, I18n.Get("ui.auth.header.batch"), _requests.Count);
    }

    /// <summary>批模式：为每份请求建一个分段头（顺序 = 请求顺序）。</summary>
    private void CreateGroups()
    {
        if (!Batch)
        {
            return;
        }

        for (int r = 0; r < _requests.Count; r++)
        {
            _list.Groups.Add(new ListViewGroup(string.Format(
                CultureInfo.InvariantCulture, I18n.Get("ui.auth.group"), _requests[r].PluginName, _requests[r].Tier)));
        }
    }

    /// <summary>把一行挂到它所属请求的分段头下（单请求弹窗不分段，什么都不做）。</summary>
    private void AttachGroup(ListViewItem row, int requestIndex)
    {
        if (Batch)
        {
            row.Group = _list.Groups[requestIndex];
        }
    }

    /// <summary>为第 <paramref name="requestIndex"/> 份请求铺行（构造期与晚到请求共用同一条路径）。
    /// 勾选真值自己存一份：句柄建好前读原生 <c>Checked</c> 会抛（先例：D-03 那次 ListView 事故）。</summary>
    private void BuildRows(int requestIndex)
    {
        foreach (ConsentItem entry in _requests[requestIndex].Items)
        {
            var item = new ListViewItem(RowText(entry))
            {
                // Tag 存行下标：列表的勾选框回调只有行对象（没有下标），靠它把行对回清单。
                Tag = _ids.Count,
            };
            _list.Items.Add(item);
            AttachGroup(item, requestIndex);

            _ids.Add(entry.CapabilityId);
            _owner.Add(requestIndex);
            if (entry.Denied)
            {
                _deniedIds.Add(entry.CapabilityId);
                _checked.Add(false);
            }
            else
            {
                // 默认全勾（docs/18 §3：只有被拒绝的项才默认不勾）。
                _checked.Add(true);
            }
        }
    }

    /// <summary>分段头对齐请求数：晚到的请求让「单请求」变成「批」，这时才补分组头，
    /// 并把已有的行按 <see cref="_owner"/> 重新挂回各自的分段。</summary>
    private void SyncGroups()
    {
        _list.ShowGroups = Batch;
        if (!Batch)
        {
            return;
        }

        if (_list.Groups.Count == _requests.Count)
        {
            return;
        }

        _list.Groups.Clear();
        CreateGroups();
        for (int i = 0; i < _list.Items.Count && i < _owner.Count; i++)
        {
            _list.Items[i].Group = _list.Groups[_owner[i]];
        }
    }

    /// <summary>晚到的请求并进**已经打开**的窗（2026-10-26 真机：三只插件在 0.4–0.6 秒内先后申请，
    /// 开窗瞬间的队里只有一份 → 原来会连弹三个窗）。UI 线程专用：由端口在模态循环里投递调用。</summary>
    internal void AddRequest(ConsentRequest request)
    {
        if (_building)
        {
            return; // 建窗中不可能有外来的 UI 回调
        }

        int index = _requests.Count;
        _requests.Add(request);

        _building = true;
        try
        {
            SyncGroups();
            if (_list.Items.Count == 1 && _list.Items[0].Tag is null)
            {
                // 空清单占位行（「没有需要授权的项」）：有真行了就撤掉。
                _list.Items.Clear();
            }

            BuildRows(index);
            _list.FitColumns();

            _header.Text = HeaderFor();
            _intro.Text = I18n.Get(Batch ? "ui.auth.intro.batch" : "ui.auth.intro");
            _once.Enabled = _requests.All(r => r.Tier < 2);
            _once.Text = _requests.Any(r => r.Tier >= 2) ? I18n.Get("ui.auth.once.tier2") : I18n.Get("ui.auth.once");
        }
        finally
        {
            _building = false;
        }
    }
}
