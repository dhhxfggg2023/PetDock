// 安装 / 删除入口（从 ControlPanel.Plugins.cs 拆出，D-03c）。
// 面板仍然只发意图：选择来源、确认、交给宿主，界面不碰插件目录。
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class ControlPanel
{

    /// <summary>安装入口的名字（自检断言四个入口都在）。</summary>
    public IReadOnlyList<string> ManageButtonNames =>
        new[] { _installButton.Name, _removeButton.Name, _authorizeButton.Name, _disableButton.Name };

    private ToolStripMenuItem InstallMenuItem(string text, bool folder)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => InstallFrom(folder);
        return item;
    }

    /// <summary>选来源 → 确认 → 交给宿主安装。安装可能复制几十兆文件，因此这段时间界面是忙的
    /// （与导入素材同一取舍：先保证正确，进度条留给下一轮）。</summary>
    private void InstallFrom(bool folder)
    {
        string? source = folder ? PickFolder() : PickZip();
        if (source is null)
        {
            return;
        }

        if (!ConfirmDialog.Ask(I18n.Get("cp.plugins.install.confirm"), I18n.Get("cp.plugins.install.confirmTitle")))
        {
            return;
        }

        string? error = _installPlugin is null
            ? I18n.Get("cp.plugins.install.unavailable")
            : _installPlugin(source, true);
        if (error is null)
        {
            _status.Text = I18n.Get("cp.plugins.install.done");
            BindPluginRows(_snapshot());
        }
        else
        {
            _status.Text = error;
        }
    }

    /// <summary>删除选中插件：先确认，再交给宿主（内置插件在宿主侧被拒）。</summary>
    private void RemoveSelected()
    {
        string? id = _selectedPluginId;
        if (id is null)
        {
            return;
        }

        string name = _pluginRows.TryGetValue(id, out PluginLine? line) && line is not null ? line.Name : id;
        if (!ConfirmDialog.Ask(I18n.Get("cp.plugins.remove.confirm", name), I18n.Get("cp.plugins.remove.confirmTitle")))
        {
            return;
        }

        string? error = _removePlugin is null
            ? I18n.Get("cp.plugins.remove.unavailable")
            : _removePlugin(id);
        if (error is null)
        {
            _status.Text = I18n.Get("cp.plugins.remove.done");
            _selectedPluginId = null;
            BindPluginRows(_snapshot());
        }
        else
        {
            _status.Text = error;
        }
    }

    private static string? PickFolder()
    {
        using var dialog = new FolderBrowserDialog();
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static string? PickZip()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = I18n.Get("cp.plugins.install.zipFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }
}
