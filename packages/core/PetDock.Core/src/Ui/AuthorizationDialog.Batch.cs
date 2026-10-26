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
}
