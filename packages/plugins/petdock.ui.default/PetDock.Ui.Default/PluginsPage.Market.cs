// 插件市场（2026-11-01）：插件页的第二态，与本地列表互斥显示。
//
// 三条纪律（与宿主侧 UiHost.Market.cs 对齐，谁也不能单方面破）：
//   * 只有用户点「浏览插件市场…」这一下才联网 —— 页面构造、刷新、切回都不碰网络；
//   * 「没拉到」与「市场里没有」分开说：前者是状态行里的原因，后者是空列表提示；
//   * 页面不下载、不解压、不校验：安装一律交给 InstallFromMarketAsync（宿主同一条安装管线）。
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using Button = System.Windows.Controls.Button;
using PetDock.Core.Ui;

namespace PetDock.Ui.Default;

public partial class PluginsPage
{
    private readonly ObservableCollection<MarketRow> _market = new();
    private bool _marketBusy;

    /// <summary>把市场那一半的固定文案与列表源接上（构造时调一次，语言换了页面会重建）。</summary>
    private void WireMarket()
    {
        MarketButton.Content = _context.T("cp.market.browse");
        MarketTitle.Text = _context.T("cp.market.title");
        MarketCloseButton.Content = _context.T("cp.market.close");
        MarketColName.Text = _context.T("cp.market.col.name");
        MarketColVersion.Text = _context.T("cp.market.col.version");
        MarketColSize.Text = _context.T("cp.market.col.size");
        MarketColAction.Text = _context.T("cp.market.col.action");
        MarketRows.ItemsSource = _market;
    }

    /// <summary>打开市场 = 换一屏 + 拉一次索引。拉失败也要能退回列表，所以这里绝不抛。</summary>
    private async void OnMarketClick(object sender, RoutedEventArgs e)
    {
        ListCard.Visibility = Visibility.Collapsed;
        SetListActionsVisible(false);
        MarketCard.Visibility = Visibility.Visible;
        await LoadMarketAsync();
    }

    private void OnMarketCloseClick(object sender, RoutedEventArgs e)
    {
        MarketCard.Visibility = Visibility.Collapsed;
        ListCard.Visibility = Visibility.Visible;
        SetListActionsVisible(true);
        // 关掉就清空：下次打开重新拉，界面里不留「上一次的列表」这种记忆。
        _market.Clear();
    }

    /// <summary>
    /// 市场视图下「安装/授权/删除」作用于看不见的本地列表，会让用户以为是给市场行用的 —— 直接收起来。
    /// </summary>
    private void SetListActionsVisible(bool visible)
    {
        Visibility state = visible ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = state;
        AuthorizeButton.Visibility = state;
        RemoveButton.Visibility = state;
    }

    private async Task LoadMarketAsync()
    {
        if (_marketBusy)
        {
            return;
        }

        _marketBusy = true;
        MarketRows.IsEnabled = false;
        _market.Clear();
        MarketEmpty.Visibility = Visibility.Collapsed;
        SetMarketStatus(_context.T("cp.market.loading"));
        try
        {
            MarketResult result = await _context.LoadMarketAsync();
            foreach (MarketEntryLine line in result.Entries)
            {
                _market.Add(new MarketRow(line, _context));
            }

            bool failed = !string.IsNullOrEmpty(result.Error);
            SetMarketStatus(failed ? DescribeMarketError(result.Error!) : string.Empty);
            MarketEmpty.Text = _context.T("cp.market.empty");
            MarketEmpty.Visibility = !failed && _market.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MarketRows.Visibility = _market.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception)
        {
            // 契约上这一段不该抛；真抛了也不能让面板停在「正在读取」上。
            SetMarketStatus(_context.T("cp.market.offline"));
            MarketRows.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _marketBusy = false;
            MarketRows.IsEnabled = true;
        }
    }

    /// <summary>
    /// 失败原因翻成人话。宿主给的既可能是文案键（cp.market.*），也可能是机器码
    /// （index-not-json / bad-download-url / entry-bad-*）：前者直接翻，后者塞进「读取索引失败：{0}」。
    /// 把机器码原样丢给用户等于没说，而把索引坏了说成「离线」是撒谎。
    /// </summary>
    private string DescribeMarketError(string reason) =>
        reason.StartsWith("cp.", StringComparison.Ordinal)
            ? _context.T(reason)
            : _context.T("cp.market.indexFailed", reason);

    private void SetMarketStatus(string text)
    {
        MarketStatus.Text = text;
        MarketStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 装一只。按下按钮就是确认（安装动作不是授权：能力询问仍由宿主在装载时逐项问）。
    /// 装完只改这一行的动作位，不重新联网 —— 列表刚拉过，本地版本已经知道了。
    /// </summary>
    private async void OnMarketInstallClick(object sender, RoutedEventArgs e)
    {
        if (_marketBusy || sender is not Button { Tag: string id })
        {
            return;
        }

        MarketRow? row = null;
        foreach (MarketRow candidate in _market)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                row = candidate;
                break;
            }
        }

        if (row is null || !row.CanInstall)
        {
            return;
        }

        _marketBusy = true;
        MarketRows.IsEnabled = false;
        try
        {
            UiCommandResult result = await _context.InstallFromMarketAsync(row.Id);
            if (result.Ok)
            {
                _context.SetStatus(_context.T("cp.plugins.install.done"), UiStatusKind.Success);
                row.MarkInstalled();
                _context.Refresh();
                return;
            }

            _context.SetStatus(
                result.Message,
                result.Cancelled ? UiStatusKind.Info : UiStatusKind.Problem);
        }
        catch (Exception)
        {
            _context.SetStatus(_context.T("ui.work.failed"), UiStatusKind.Problem);
        }
        finally
        {
            _marketBusy = false;
            MarketRows.IsEnabled = true;
        }
    }
}
